using System.Text;
using PGW.Core;

namespace PGW.Drivers.Modbus;

/// <summary>One row of the Register Map (§6.3): tag_id -> {unit_id, area, address, type, word_order, ro/rw, on_bad}.
/// `FlagAddress` is only meaningful for <see cref="OnBadPolicy.FreezeAndFlag"/>: a bit (Coil/DI) that the
/// interface sets while the tag is Bad and clears once it's Good again, alongside freezing the value
/// itself — the "flag" half of freeze_and_flag, not writable by SCADA clients.</summary>
public sealed record RegisterMapEntry(
    string TagId, byte UnitId, ModbusArea Area, ushort Address, TagDataType Type, WordOrder WordOrder,
    bool ReadOnly, OnBadPolicy OnBad, object? SubstituteValue, ModbusAddress? FlagAddress = null);

public sealed record ModbusOutputSettings(
    string Bind = "0.0.0.0", int Port = 502, int MaxConnections = 16,
    List<string>? WhitelistRead = null, bool GlobalReadOnly = true, bool IgnoreUnknownUnit = false);

public static class RegisterMapBuilder
{
    public static (List<RegisterMapEntry> Entries, List<string> Errors) Build(OutputConfig output, WordOrder defaultWordOrder)
    {
        var entries = new List<RegisterMapEntry>();
        var errors = new List<string>();
        var seen = new HashSet<(byte, ModbusArea, int)>();

        foreach (var m in output.Map)
        {
            var tag = m.GetStr("tag");
            var unitId = (byte)m.GetInt("unit_id", 1);
            var area = m.GetStr("area", "HR").ToUpperInvariant() switch
            {
                "CO" or "COIL" or "COILS" => ModbusArea.Coil,
                "DI" => ModbusArea.DiscreteInput,
                "IR" => ModbusArea.InputRegister,
                _ => ModbusArea.HoldingRegister,
            };
            var address = m.GetInt("address");
            var type = Enum.Parse<TagDataType>(m.GetStr("type", "float32"), true);
            var wordOrder = ModbusSourceFactory.ParseWordOrder(m.GetStr("word_order"), defaultWordOrder);
            var onBad = m.GetStr("on_bad", "hold").ToLowerInvariant() switch
            {
                "zero" => OnBadPolicy.Zero,
                "substitute" => OnBadPolicy.Substitute,
                "freeze_and_flag" => OnBadPolicy.FreezeAndFlag,
                _ => OnBadPolicy.Hold,
            };
            // DI/IR are physically read-only in Modbus; HR/Coil follow the map entry's `rw` flag (default RO, §6.5).
            var readOnly = area is ModbusArea.DiscreteInput or ModbusArea.InputRegister || !m.GetBool("rw");

            if (address < 0 || address > 65535) { errors.Add($"tag '{tag}': address {address} out of range 0-65535"); continue; }

            var regLen = ModbusCodec.RegisterCount(type);
            if (address + regLen > 65536) { errors.Add($"tag '{tag}': address range exceeds 65535"); continue; }

            for (int i = 0; i < regLen; i++)
            {
                var key = (unitId, area, address + i);
                if (!seen.Add(key)) { errors.Add($"tag '{tag}': address {area}:{address} overlaps another map entry (unit {unitId})"); break; }
            }

            ModbusAddress? flagAddress = null;
            if (onBad == OnBadPolicy.FreezeAndFlag && m.TryGetValue("flag_address", out var faObj) && faObj is not null)
            {
                var flagArea = m.GetStr("flag_area", "DI").ToUpperInvariant() switch
                {
                    "CO" or "COIL" or "COILS" => ModbusArea.Coil,
                    "DI" => ModbusArea.DiscreteInput,
                    var s => throw new FormatException($"tag '{tag}': flag_area '{s}' must be CO or DI (a status flag is a single bit)"),
                };
                var flagAddr = Convert.ToInt32(faObj);
                if (flagAddr < 0 || flagAddr > 65535) { errors.Add($"tag '{tag}': flag_address {flagAddr} out of range 0-65535"); }
                else if (!seen.Add((unitId, flagArea, flagAddr)))
                    errors.Add($"tag '{tag}': flag_address {flagArea}:{flagAddr} overlaps another map entry (unit {unitId})");
                else
                    flagAddress = new ModbusAddress(flagArea, (ushort)flagAddr);
            }
            else if (onBad == OnBadPolicy.FreezeAndFlag)
            {
                errors.Add($"tag '{tag}': on_bad: freeze_and_flag needs a flag_address (Coil/DI bit set while quality is Bad); " +
                           "without it the value just freezes with no visible flag — use on_bad: hold instead if that's intended");
            }

            entries.Add(new RegisterMapEntry(tag, unitId, area, (ushort)address, type, wordOrder, readOnly, onBad,
                m.TryGetValue("substitute", out var sv) ? sv : null, flagAddress));
        }

        return (entries, errors);
    }

    public static string ExportCsv(IEnumerable<RegisterMapEntry> entries)
    {
        var sb = new StringBuilder("﻿");
        sb.Append("tag,unit_id,area,address,type,word_order,read_only,on_bad,flag_area,flag_address\r\n");
        foreach (var e in entries)
            sb.Append($"{e.TagId},{e.UnitId},{e.Area},{e.Address},{e.Type},{e.WordOrder},{e.ReadOnly},{e.OnBad}," +
                      $"{(e.FlagAddress is { } f ? f.Area.ToString() : "")},{(e.FlagAddress is { } f2 ? f2.Register.ToString() : "")}\r\n");
        return sb.ToString();
    }
}
