using System.Text;
using PGW.Core;

namespace PGW.Drivers.Modbus;

/// <summary>One row of the Register Map (§6.3): tag_id -> {unit_id, area, address, type, word_order, ro/rw, on_bad}.</summary>
public sealed record RegisterMapEntry(
    string TagId, byte UnitId, ModbusArea Area, ushort Address, TagDataType Type, WordOrder WordOrder,
    bool ReadOnly, OnBadPolicy OnBad, object? SubstituteValue);

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

            entries.Add(new RegisterMapEntry(tag, unitId, area, (ushort)address, type, wordOrder, readOnly, onBad,
                m.TryGetValue("substitute", out var sv) ? sv : null));
        }

        return (entries, errors);
    }

    public static string ExportCsv(IEnumerable<RegisterMapEntry> entries)
    {
        var sb = new StringBuilder("﻿");
        sb.Append("tag,unit_id,area,address,type,word_order,read_only,on_bad\r\n");
        foreach (var e in entries)
            sb.Append($"{e.TagId},{e.UnitId},{e.Area},{e.Address},{e.Type},{e.WordOrder},{e.ReadOnly},{e.OnBad}\r\n");
        return sb.ToString();
    }
}
