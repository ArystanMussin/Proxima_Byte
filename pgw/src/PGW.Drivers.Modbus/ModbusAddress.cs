using PGW.Core;

namespace PGW.Drivers.Modbus;

public enum ModbusArea { Coil, DiscreteInput, InputRegister, HoldingRegister }

public readonly record struct ModbusAddress(ModbusArea Area, ushort Register, int? Bit = null)
{
    /// <summary>Native address string stored on the tag/map entry, e.g. "HR:100" or "HR:10.3".</summary>
    public override string ToString()
    {
        var prefix = Area switch { ModbusArea.Coil => "CO", ModbusArea.DiscreteInput => "DI", ModbusArea.InputRegister => "IR", _ => "HR" };
        return Bit is null ? $"{prefix}:{Register}" : $"{prefix}:{Register}.{Bit}";
    }

    public static ModbusAddress Parse(string native)
    {
        var parts = native.Split(':', 2);
        var area = parts[0].ToUpperInvariant() switch
        {
            "CO" or "COIL" => ModbusArea.Coil,
            "DI" => ModbusArea.DiscreteInput,
            "IR" => ModbusArea.InputRegister,
            "HR" => ModbusArea.HoldingRegister,
            _ => throw new FormatException($"unknown Modbus area '{parts[0]}'"),
        };
        var addrParts = parts[1].Split('.', 2);
        var register = ushort.Parse(addrParts[0]);
        int? bit = addrParts.Length > 1 ? int.Parse(addrParts[1]) : null;
        return new ModbusAddress(area, register, bit);
    }

    /// <summary>Builds an address from a tag config entry: explicit {area,address[,bit]}, or classic 6-digit notation.</summary>
    public static ModbusAddress FromConfig(IReadOnlyDictionary<string, object?> t, bool oneBased)
    {
        var offset = oneBased ? 1 : 0;
        if (t.TryGetValue("area", out var areaObj) && areaObj is not null)
        {
            var area = areaObj.ToString()!.ToUpperInvariant() switch
            {
                "CO" or "COIL" or "COILS" => ModbusArea.Coil,
                "DI" => ModbusArea.DiscreteInput,
                "IR" => ModbusArea.InputRegister,
                "HR" => ModbusArea.HoldingRegister,
                var s => throw new FormatException($"unknown Modbus area '{s}'"),
            };
            var addr = t.GetInt("address") - offset;
            int? bit = t.TryGetValue("bit", out var b) && b is not null ? Convert.ToInt32(b) : null;
            return new ModbusAddress(area, (ushort)addr, bit);
        }

        // classic notation: 000001-065536 coils, 100001-165536 DI, 300001-365536 IR, 400001-465536 HR
        var classic = t.GetInt("address");
        return classic switch
        {
            >= 400001 and <= 465536 => new ModbusAddress(ModbusArea.HoldingRegister, (ushort)(classic - 400001)),
            >= 300001 and <= 365536 => new ModbusAddress(ModbusArea.InputRegister, (ushort)(classic - 300001)),
            >= 100001 and <= 165536 => new ModbusAddress(ModbusArea.DiscreteInput, (ushort)(classic - 100001)),
            _ => new ModbusAddress(ModbusArea.Coil, (ushort)(classic - 1)),
        };
    }
}

public sealed record ReadBlock(ModbusArea Area, ushort Start, int Length, IReadOnlyList<(string TagId, ModbusAddress Addr, TagDataType Type, int Bit)> Items);

/// <summary>Groups adjacent tag addresses into as few read requests as possible (§4.3).</summary>
public static class ModbusBlockPlanner
{
    public static List<ReadBlock> Plan(IEnumerable<(string TagId, ModbusAddress Addr, TagDataType Type)> tags, int gapTolerance = 5)
    {
        var blocks = new List<ReadBlock>();
        foreach (var group in tags.GroupBy(t => t.Addr.Area))
        {
            var maxLen = group.Key is ModbusArea.Coil or ModbusArea.DiscreteInput ? 2000 : 125;
            var sorted = group.Select(t => (t.TagId, t.Addr, t.Type, RegLen: ModbusCodec.RegisterCount(t.Type)))
                               .OrderBy(t => t.Addr.Register).ToList();

            var current = new List<(string, ModbusAddress, TagDataType, int)>();
            ushort blockStart = 0;
            int blockEnd = 0;

            foreach (var t in sorted)
            {
                var tagEnd = t.Addr.Register + t.RegLen;
                if (current.Count == 0)
                {
                    blockStart = t.Addr.Register;
                    blockEnd = tagEnd;
                }
                else if (t.Addr.Register - blockEnd <= gapTolerance && tagEnd - blockStart <= maxLen)
                {
                    blockEnd = Math.Max(blockEnd, tagEnd);
                }
                else
                {
                    blocks.Add(new ReadBlock(group.Key, blockStart, blockEnd - blockStart, current));
                    current = new();
                    blockStart = t.Addr.Register;
                    blockEnd = tagEnd;
                }
                current.Add((t.TagId, t.Addr, t.Type, t.Addr.Bit ?? 0));
            }
            if (current.Count > 0)
                blocks.Add(new ReadBlock(group.Key, blockStart, blockEnd - blockStart, current));
        }
        return blocks;
    }
}
