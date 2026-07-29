using System.Buffers.Binary;
using System.Text;
using PGW.Core;

namespace PGW.Drivers.Modbus;

public enum WordOrder { ABCD, CDAB, BADC, DCBA }

/// <summary>
/// Packs/unpacks tag values to/from raw 16-bit Modbus registers, honouring word/byte order (§4.2).
/// Registers always arrive/leave in big-endian-per-register form (FluentModbus convention); this class
/// only reorders whole words/bytes across a multi-register value.
/// </summary>
public static class ModbusCodec
{
    public static int RegisterCount(TagDataType type) => type switch
    {
        TagDataType.Bool or TagDataType.Int16 or TagDataType.UInt16 => 1,
        TagDataType.Int32 or TagDataType.UInt32 or TagDataType.Float32 => 2,
        TagDataType.Int64 or TagDataType.UInt64 or TagDataType.Float64 => 4,
        _ => 1,
    };

    public static object? Decode(ReadOnlySpan<ushort> regs, TagDataType type, WordOrder order, int? bit = null)
    {
        if (type == TagDataType.Bool)
            return ((regs[0] >> (bit ?? 0)) & 1) == 1;

        Span<byte> canon = stackalloc byte[regs.Length * 2];
        ToCanonicalBytes(regs, order, canon);

        return type switch
        {
            TagDataType.Int16 => BinaryPrimitives.ReadInt16BigEndian(canon),
            TagDataType.UInt16 => BinaryPrimitives.ReadUInt16BigEndian(canon),
            TagDataType.Int32 => BinaryPrimitives.ReadInt32BigEndian(canon),
            TagDataType.UInt32 => BinaryPrimitives.ReadUInt32BigEndian(canon),
            TagDataType.Int64 => BinaryPrimitives.ReadInt64BigEndian(canon),
            TagDataType.UInt64 => BinaryPrimitives.ReadUInt64BigEndian(canon),
            TagDataType.Float32 => BinaryPrimitives.ReadSingleBigEndian(canon),
            TagDataType.Float64 => BinaryPrimitives.ReadDoubleBigEndian(canon),
            TagDataType.String => Encoding.ASCII.GetString(canon).TrimEnd('\0'),
            _ => null,
        };
    }

    public static ushort[] Encode(object? value, TagDataType type, WordOrder order, int registerCount)
    {
        var canon = new byte[registerCount * 2];
        switch (type)
        {
            case TagDataType.Int16: BinaryPrimitives.WriteInt16BigEndian(canon, Convert.ToInt16(value)); break;
            case TagDataType.UInt16: BinaryPrimitives.WriteUInt16BigEndian(canon, Convert.ToUInt16(value)); break;
            case TagDataType.Int32: BinaryPrimitives.WriteInt32BigEndian(canon, Convert.ToInt32(value)); break;
            case TagDataType.UInt32: BinaryPrimitives.WriteUInt32BigEndian(canon, Convert.ToUInt32(value)); break;
            case TagDataType.Int64: BinaryPrimitives.WriteInt64BigEndian(canon, Convert.ToInt64(value)); break;
            case TagDataType.UInt64: BinaryPrimitives.WriteUInt64BigEndian(canon, Convert.ToUInt64(value)); break;
            case TagDataType.Float32: BinaryPrimitives.WriteSingleBigEndian(canon, Convert.ToSingle(value)); break;
            case TagDataType.Float64: BinaryPrimitives.WriteDoubleBigEndian(canon, Convert.ToDouble(value)); break;
            case TagDataType.String: Encoding.ASCII.GetBytes((value as string ?? "").PadRight(canon.Length, '\0'), canon); break;
        }

        var raw = new byte[canon.Length];
        ReorderWords(canon, order, raw);

        var regs = new ushort[registerCount];
        for (int i = 0; i < registerCount; i++)
            regs[i] = (ushort)((raw[i * 2] << 8) | raw[i * 2 + 1]);
        return regs;
    }

    private static void ToCanonicalBytes(ReadOnlySpan<ushort> regs, WordOrder order, Span<byte> raw)
    {
        for (int i = 0; i < regs.Length; i++)
        {
            raw[i * 2] = (byte)(regs[i] >> 8);
            raw[i * 2 + 1] = (byte)(regs[i] & 0xFF);
        }
        ReorderWords(raw.ToArray(), order, raw);
    }

    /// <summary>Word/byte reordering is its own inverse, so the same routine encodes and decodes.</summary>
    private static void ReorderWords(ReadOnlySpan<byte> src, WordOrder order, Span<byte> dst)
    {
        int words = src.Length / 2;
        bool swapWords = order is WordOrder.CDAB or WordOrder.DCBA;
        bool swapBytes = order is WordOrder.BADC or WordOrder.DCBA;
        for (int w = 0; w < words; w++)
        {
            int srcWord = swapWords ? words - 1 - w : w;
            byte b0 = src[srcWord * 2], b1 = src[srcWord * 2 + 1];
            dst[w * 2] = swapBytes ? b1 : b0;
            dst[w * 2 + 1] = swapBytes ? b0 : b1;
        }
    }

    public static bool GetPackedBit(ReadOnlySpan<byte> packed, int address) =>
        (packed[address / 8] & (1 << (address % 8))) != 0;

    public static void SetPackedBit(Span<byte> packed, int address, bool value)
    {
        int byteIdx = address / 8, bitIdx = address % 8;
        if (value) packed[byteIdx] |= (byte)(1 << bitIdx);
        else packed[byteIdx] &= (byte)~(1 << bitIdx);
    }
}
