using System.Net;
using System.Net.Sockets;
using FluentModbus;
using PGW.Core;

namespace PGW.Drivers.Modbus;

/// <summary>
/// Modbus TCP server / slave (§6): renders the Tag Space through a Register Map and routes client
/// writes back through the core (never talks to a source driver directly, per §3.4).
/// </summary>
public sealed class ModbusTcpServerInterface : IProtocolInterface
{
    public string InterfaceTypeId => "modbus_tcp_server";

    private readonly ModbusOutputSettings _cfg;
    private readonly List<RegisterMapEntry> _map;
    private readonly Func<string, object?, CancellationToken, Task<WriteResult>> _coreWrite;
    private readonly RingLog _log;
    private readonly ModbusTcpServer _server = new();
    private readonly HashSet<byte> _unitIds;
    private readonly Dictionary<(byte, ModbusArea, int), RegisterMapEntry> _byAddress;
    private readonly ILookup<string, RegisterMapEntry> _byTag;
    private TrackingTcpClientProvider? _provider;
    private ITagSource? _source;

    private long _requests, _exceptions;

    public ModbusTcpServerInterface(ModbusOutputSettings cfg, List<RegisterMapEntry> map,
        Func<string, object?, CancellationToken, Task<WriteResult>> coreWrite, RingLog log)
    {
        _cfg = cfg;
        _map = map;
        _coreWrite = coreWrite;
        _log = log;
        _unitIds = map.Select(m => m.UnitId).ToHashSet();
        _byAddress = new();
        foreach (var m in map)
            for (int i = 0; i < ModbusCodec.RegisterCount(m.Type); i++)
                _byAddress.TryAdd((m.UnitId, m.Area, m.Address + i), m);
        _byTag = map.ToLookup(m => m.TagId);
    }

    public Task StartAsync(ITagSource source, CancellationToken ct)
    {
        _source = source;
        foreach (var u in _unitIds) _server.AddUnit(u);

        _server.RequestValidator = ValidateRequest;
        _server.EnableRaisingEvents = true;
        _server.RegistersChanged += OnRegistersChanged;
        _server.CoilsChanged += OnCoilsChanged;
        source.Changed += OnTagChanged;

        _provider = new TrackingTcpClientProvider(_cfg.Bind, _cfg.Port, _cfg.WhitelistRead ?? new(), _log);
        _server.Start(_provider, false);

        foreach (var m in _map)
        {
            var snap = source.Get(m.TagId);
            if (snap is not null) ApplyToBuffer(m, snap);
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        if (_source is not null) _source.Changed -= OnTagChanged;
        _server.Stop();
        return Task.CompletedTask;
    }

    public Task<WriteResult> OnExternalWriteAsync(string tagId, TagValue value, CancellationToken ct) =>
        _coreWrite(tagId, value.Value, ct);

    public InterfaceHealth GetHealth() => new(_provider?.AcceptedCount ?? 0, _requests, _exceptions);

    private void OnTagChanged(TagSnapshot snap)
    {
        foreach (var m in _byTag[snap.Definition.Id])
            ApplyToBuffer(m, snap);
    }

    private void ApplyToBuffer(RegisterMapEntry m, TagSnapshot snap)
    {
        object? value;
        if (snap.Quality == TagQuality.Good) value = snap.Value;
        else if (m.OnBad == OnBadPolicy.Zero) value = TagTypeConversion.DefaultValue(m.Type);
        else if (m.OnBad == OnBadPolicy.Substitute) value = m.SubstituteValue ?? TagTypeConversion.DefaultValue(m.Type);
        else return; // Hold / FreezeAndFlag: keep whatever is already in the buffer

        lock (_server.Lock)
        {
            if (m.Area == ModbusArea.Coil)
                ModbusCodec.SetPackedBit(_server.GetCoils(m.UnitId), m.Address, Convert.ToBoolean(value ?? false));
            else if (m.Area == ModbusArea.DiscreteInput)
                ModbusCodec.SetPackedBit(_server.GetDiscreteInputs(m.UnitId), m.Address, Convert.ToBoolean(value ?? false));
            else
            {
                var regs = ModbusCodec.Encode(value, m.Type, m.WordOrder, ModbusCodec.RegisterCount(m.Type));
                var buf = m.Area == ModbusArea.HoldingRegister ? _server.GetHoldingRegisters(m.UnitId) : _server.GetInputRegisters(m.UnitId);
                for (int i = 0; i < regs.Length; i++) buf[m.Address + i] = (short)regs[i];
            }
        }
    }

    private ModbusExceptionCode ValidateRequest(byte unitId, ModbusFunctionCode fc, ushort address, ushort quantity)
    {
        Interlocked.Increment(ref _requests);

        if (!_unitIds.Contains(unitId))
            return _cfg.IgnoreUnknownUnit ? ModbusExceptionCode.OK : ModbusExceptionCode.GatewayTargetDeviceFailedToRespond;

        var isWrite = fc is ModbusFunctionCode.WriteSingleCoil or ModbusFunctionCode.WriteSingleRegister
            or ModbusFunctionCode.WriteMultipleCoils or ModbusFunctionCode.WriteMultipleRegisters;
        if (!isWrite) return ModbusExceptionCode.OK;

        if (_cfg.GlobalReadOnly)
        {
            Interlocked.Increment(ref _exceptions);
            return ModbusExceptionCode.IllegalDataAddress;
        }

        var area = fc is ModbusFunctionCode.WriteSingleCoil or ModbusFunctionCode.WriteMultipleCoils ? ModbusArea.Coil : ModbusArea.HoldingRegister;
        if (!_byAddress.TryGetValue((unitId, area, address), out var entry) || entry.ReadOnly)
        {
            Interlocked.Increment(ref _exceptions);
            return ModbusExceptionCode.IllegalDataAddress;
        }

        return ModbusExceptionCode.OK;
    }

    private void OnRegistersChanged(object? sender, RegistersChangedEventArgs e)
    {
        var touched = new HashSet<RegisterMapEntry>();
        foreach (var addr in e.Registers)
            if (_byAddress.TryGetValue((e.UnitIdentifier, ModbusArea.HoldingRegister, addr), out var entry))
                touched.Add(entry);
        foreach (var m in touched) _ = ForwardWriteAsync(m);
    }

    private void OnCoilsChanged(object? sender, CoilsChangedEventArgs e)
    {
        foreach (var addr in e.Coils)
            if (_byAddress.TryGetValue((e.UnitIdentifier, ModbusArea.Coil, addr), out var entry))
                _ = ForwardWriteAsync(entry);
    }

    private async Task ForwardWriteAsync(RegisterMapEntry m)
    {
        var value = ReadValueFromBuffer(m);
        var result = await _coreWrite(m.TagId, value, CancellationToken.None);
        _log.Add(m.TagId, $"client write unit={m.UnitId} {(result.Ok ? "ok" : "failed: " + result.ErrorCode)}", result.Ok ? "INFO" : "WARN");
        if (!result.Ok) Interlocked.Increment(ref _exceptions);
    }

    private object? ReadValueFromBuffer(RegisterMapEntry m)
    {
        lock (_server.Lock)
        {
            if (m.Area == ModbusArea.Coil)
                return ModbusCodec.GetPackedBit(_server.GetCoils(m.UnitId), m.Address);

            var buf = _server.GetHoldingRegisters(m.UnitId);
            var regs = new ushort[ModbusCodec.RegisterCount(m.Type)];
            for (int i = 0; i < regs.Length; i++) regs[i] = unchecked((ushort)buf[m.Address + i]);
            return ModbusCodec.Decode(regs, m.Type, m.WordOrder);
        }
    }
}

/// <summary>Accepts TCP connections, rejecting any not on the whitelist (§6.1); tracks accepted-connection count.</summary>
internal sealed class TrackingTcpClientProvider : ITcpClientProvider
{
    private readonly TcpListener _listener;
    private readonly List<IPAddress>? _whitelist;
    private readonly RingLog _log;
    public int AcceptedCount;

    public TrackingTcpClientProvider(string bind, int port, List<string> whitelist, RingLog log)
    {
        _listener = new TcpListener(IPAddress.Parse(bind), port);
        _listener.Start();
        _whitelist = whitelist.Count > 0 ? whitelist.Select(IPAddress.Parse).ToList() : null;
        _log = log;
    }

    public async Task<TcpClient> AcceptTcpClientAsync()
    {
        while (true)
        {
            var client = await _listener.AcceptTcpClientAsync();
            var remote = ((IPEndPoint)client.Client.RemoteEndPoint!).Address;
            if (_whitelist is null || _whitelist.Contains(remote))
            {
                Interlocked.Increment(ref AcceptedCount);
                return client;
            }
            _log.Add("modbus_tcp_server", $"rejected connection from {remote}: not whitelisted", "WARN");
            client.Close();
        }
    }

    public void Dispose() => _listener.Stop();
}
