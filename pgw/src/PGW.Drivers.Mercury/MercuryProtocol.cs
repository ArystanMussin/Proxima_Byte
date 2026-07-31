namespace PGW.Drivers.Mercury;

public enum MercuryParam
{
    EnergyActiveTotal, EnergyReactiveTotal,
    VoltageA, VoltageB, VoltageC,
    CurrentA, CurrentB, CurrentC,
    PowerTotal, PowerA, PowerB, PowerC,
    Frequency,
}

internal enum MercuryGroup { Energy, Voltage, Current, Power, Frequency }

internal static class MercuryParamInfo
{
    public static (MercuryGroup Group, int Index) Resolve(MercuryParam p) => p switch
    {
        MercuryParam.EnergyActiveTotal => (MercuryGroup.Energy, 0),
        MercuryParam.EnergyReactiveTotal => (MercuryGroup.Energy, 1),
        MercuryParam.VoltageA => (MercuryGroup.Voltage, 0),
        MercuryParam.VoltageB => (MercuryGroup.Voltage, 1),
        MercuryParam.VoltageC => (MercuryGroup.Voltage, 2),
        MercuryParam.CurrentA => (MercuryGroup.Current, 0),
        MercuryParam.CurrentB => (MercuryGroup.Current, 1),
        MercuryParam.CurrentC => (MercuryGroup.Current, 2),
        MercuryParam.PowerTotal => (MercuryGroup.Power, 0),
        MercuryParam.PowerA => (MercuryGroup.Power, 1),
        MercuryParam.PowerB => (MercuryGroup.Power, 2),
        MercuryParam.PowerC => (MercuryGroup.Power, 3),
        MercuryParam.Frequency => (MercuryGroup.Frequency, 0),
        _ => throw new ArgumentOutOfRangeException(nameof(p)),
    };

    public static string ParseParam(string s) => s.Trim().ToLowerInvariant().Replace("_", "") switch
    {
        "energyactive" or "energyactivetotal" or "energy" => nameof(MercuryParam.EnergyActiveTotal),
        "energyreactive" or "energyreactivetotal" => nameof(MercuryParam.EnergyReactiveTotal),
        "voltagea" or "ua" or "v1" => nameof(MercuryParam.VoltageA),
        "voltageb" or "ub" or "v2" => nameof(MercuryParam.VoltageB),
        "voltagec" or "uc" or "v3" => nameof(MercuryParam.VoltageC),
        "currenta" or "ia" or "i1" => nameof(MercuryParam.CurrentA),
        "currentb" or "ib" or "i2" => nameof(MercuryParam.CurrentB),
        "currentc" or "ic" or "i3" => nameof(MercuryParam.CurrentC),
        "powertotal" or "power" => nameof(MercuryParam.PowerTotal),
        "powera" or "p1" => nameof(MercuryParam.PowerA),
        "powerb" or "p2" => nameof(MercuryParam.PowerB),
        "powerc" or "p3" => nameof(MercuryParam.PowerC),
        "frequency" or "freq" or "f" => nameof(MercuryParam.Frequency),
        var other => throw new FormatException($"unknown mercury param '{other}' — expected one of: " +
            "energy_active_total, energy_reactive_total, voltage_a/b/c, current_a/b/c, power_total/a/b/c, frequency"),
    };
}

/// <summary>
/// High-level Меркурий 230 request/response transactions over an <see cref="IMercuryTransport"/> — see
/// <see cref="MercuryCodec"/> for how confident to be in the byte-level details this builds on.
/// </summary>
public sealed class MercuryProtocol(IMercuryTransport transport, byte address, int responseTimeoutMs = 1000, int interByteGapMs = 80)
{
    public async Task OpenChannelAsync(byte accessLevel, byte[] password, CancellationToken ct)
    {
        if (password.Length != 6) throw new ArgumentException("Mercury password must be exactly 6 bytes", nameof(password));
        var req = new byte[9];
        req[0] = address;
        req[1] = 0x01; // CONNECT
        req[2] = accessLevel;
        password.CopyTo(req, 3);
        var resp = await SendAndReceiveAsync(MercuryCodec.AppendCrc(req), ct);
        // A bare 3-byte reply (address + CRC, no payload) signals success; 4 bytes means an error code follows.
        if (resp.Length == 4) throw new IOException($"Mercury: channel open rejected, error code {resp[1]:X2}");
        if (resp.Length != 3) throw new IOException($"Mercury: unexpected channel-open response length {resp.Length}");
    }

    public async Task CloseChannelAsync(CancellationToken ct)
    {
        var frame = MercuryCodec.AppendCrc(new byte[] { address, 0x02 }); // CLOSE
        await SendAndReceiveAsync(frame, ct);
    }

    internal async Task<double[]> ReadGroupAsync(MercuryGroup group, CancellationToken ct) => group switch
    {
        MercuryGroup.Energy => await ReadEnergyAsync(ct),
        MercuryGroup.Voltage => await ReadParamsAsync(0x11, 100.0, 3, ct),
        MercuryGroup.Current => await ReadParamsAsync(0x21, 1000.0, 3, ct),
        MercuryGroup.Power => await ReadParamsAsync(0x00, 100.0, 4, ct),
        MercuryGroup.Frequency => await ReadParamsAsync(0x40, 100.0, 1, ct),
        _ => throw new ArgumentOutOfRangeException(nameof(group)),
    };

    private async Task<double[]> ReadEnergyAsync(CancellationToken ct)
    {
        // LIST(0x05), period=0x00 (running total, not a daily/monthly slice), tariff=0x00 (sum of all tariffs).
        var frame = MercuryCodec.AppendCrc(new byte[] { address, 0x05, 0x00, 0x00 });
        var resp = await SendAndReceiveAsync(frame, ct);
        if (resp.Length < 7) throw new IOException($"Mercury: energy response too short ({resp.Length} bytes)");

        var active = MercuryCodec.DecodeU32(resp.AsSpan(1, 4)) / 1000.0;
        // Reactive energy sits further into the same response on real hardware, but only the first (active)
        // value could be corroborated with confidence — report 0/NaN-free rather than guess at an offset
        // for a value this driver can't currently verify.
        var reactive = resp.Length >= 15 ? MercuryCodec.DecodeU32(resp.AsSpan(9, 4)) / 1000.0 : 0.0;
        return new[] { active, reactive };
    }

    private async Task<double[]> ReadParamsAsync(byte param, double scale, int count, CancellationToken ct)
    {
        // READ_PARAMS(0x08), PARAM_ALL(0x16) selects "all phases + sum" breakdown, then the parameter byte
        // picks power/voltage/current/frequency.
        var frame = MercuryCodec.AppendCrc(new byte[] { address, 0x08, 0x16, param });
        var resp = await SendAndReceiveAsync(frame, ct);

        var values = new double[count];
        for (var i = 0; i < count; i++)
        {
            var offset = 1 + i * 3;
            if (offset + 3 > resp.Length - 2)
                throw new IOException($"Mercury: parameter 0x{param:X2} response too short ({resp.Length} bytes) for {count} value(s)");
            values[i] = MercuryCodec.DecodeU24(resp.AsSpan(offset, 3)) / scale;
        }
        return values;
    }

    private async Task<byte[]> SendAndReceiveAsync(byte[] request, CancellationToken ct)
    {
        await transport.WriteAsync(request, ct);

        var buffer = new List<byte>();
        var buf = new byte[64];

        // Phase 1: wait for the first byte — a real meter can take a few hundred ms to respond.
        using (var firstByteCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            firstByteCts.CancelAfter(responseTimeoutMs);
            var n = await transport.ReadAsync(buf, 0, buf.Length, firstByteCts.Token);
            if (n == 0) throw new TimeoutException("Mercury: no response within timeout");
            buffer.AddRange(buf.AsSpan(0, n).ToArray());
        }

        // Phase 2: once bytes are flowing, a short gap means the frame is complete — the same
        // inter-character-silence idea real RTU-family protocols use for framing without a length prefix.
        while (true)
        {
            using var gapCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            gapCts.CancelAfter(interByteGapMs);
            var n = await transport.ReadAsync(buf, 0, buf.Length, gapCts.Token);
            if (n == 0) break;
            buffer.AddRange(buf.AsSpan(0, n).ToArray());
        }

        if (buffer.Count < 3) throw new IOException($"Mercury: short response ({buffer.Count} bytes)");
        var frame = buffer.ToArray();
        if (!MercuryCodec.VerifyCrc(frame)) throw new IOException("Mercury: CRC mismatch in response");
        if (frame[0] != address) throw new IOException($"Mercury: response from address {frame[0]}, expected {address}");
        return frame;
    }
}
