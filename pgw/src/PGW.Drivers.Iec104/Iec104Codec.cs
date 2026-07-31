using System.Buffers.Binary;

namespace PGW.Drivers.Iec104;

public enum Iec104TypeId : byte
{
    M_SP_NA_1 = 1,   // single point
    M_DP_NA_1 = 3,   // double point
    M_ME_NA_1 = 9,   // measured value, normalized
    M_ME_NB_1 = 11,  // measured value, scaled
    M_ME_NC_1 = 13,  // measured value, short float
    M_SP_TB_1 = 30,  // single point, with CP56Time2a
    M_ME_TF_1 = 36,  // measured value, short float, with CP56Time2a
    C_IC_NA_1 = 100, // general interrogation command
    C_CS_NA_1 = 103, // clock sync command
}

/// <summary>Cause of Transmission — verbatim-matched against lib60870 (mz-automation, the reference
/// open-source IEC 60870-5-104 implementation), not memory. See README "IEC 60870-5-104".</summary>
public enum Iec104Cot : byte
{
    Periodic = 1, BackgroundScan = 2, Spontaneous = 3, Initialized = 4, Request = 5,
    Activation = 6, ActivationCon = 7, Deactivation = 8, DeactivationCon = 9, ActivationTermination = 10,
    ReturnInfoRemote = 11, ReturnInfoLocal = 12,
    InterrogatedByStation = 20,
    UnknownTypeId = 44, UnknownCot = 45, UnknownCa = 46, UnknownIoa = 47,
}

[Flags]
public enum Iec104Quality : byte
{
    Good = 0, Overflow = 0x01, Blocked = 0x10, Substituted = 0x20, NonTopical = 0x40, Invalid = 0x80,
}

public sealed record Iec104Point(int Ioa, object? Value, Iec104Quality Quality, DateTime? Timestamp);

public sealed record Iec104Asdu(Iec104TypeId TypeId, Iec104Cot Cot, bool Negative, bool Test, byte OriginatorAddress, int CommonAddress, IReadOnlyList<Iec104Point> Points);

/// <summary>
/// APCI framing (I/S/U format) and ASDU encode/decode for a useful subset of IEC 60870-5-104: the
/// monitoring types a general interrogation typically returns (single/double point, normalized/scaled/
/// float measured values, with or without a CP56Time2a timestamp) and the one control type this driver
/// needs to send (C_IC_NA_1, general interrogation). Field widths and constants were checked against
/// lib60870's source rather than reconstructed from memory — see README "IEC 60870-5-104".
/// </summary>
public static class Iec104Codec
{
    public const byte Start = 0x68;
    private const int IoaSize = 3;
    private const int CasduSize = 2;

    // ---- APCI (6-byte header: Start, Length, 4 control bytes) ----

    public static byte[] BuildUFrame(byte controlByte0) => new byte[] { Start, 0x04, controlByte0, 0x00, 0x00, 0x00 };

    public static readonly byte[] StartDtAct = BuildUFrame(0x07);
    public static readonly byte[] StartDtCon = BuildUFrame(0x0B);
    public static readonly byte[] StopDtAct = BuildUFrame(0x13);
    public static readonly byte[] StopDtCon = BuildUFrame(0x23);
    public static readonly byte[] TestFrAct = BuildUFrame(0x43);
    public static readonly byte[] TestFrCon = BuildUFrame(0x83);

    public static byte[] BuildSFrame(int receiveSeq)
    {
        var frame = new byte[6];
        frame[0] = Start; frame[1] = 0x04;
        frame[2] = 0x01; frame[3] = 0x00;
        frame[4] = (byte)((receiveSeq << 1) & 0xFE);
        frame[5] = (byte)((receiveSeq >> 7) & 0xFF);
        return frame;
    }

    public static byte[] BuildIFrame(int sendSeq, int receiveSeq, byte[] asdu)
    {
        var frame = new byte[6 + asdu.Length];
        frame[0] = Start;
        frame[1] = (byte)(4 + asdu.Length);
        frame[2] = (byte)((sendSeq << 1) & 0xFE);
        frame[3] = (byte)((sendSeq >> 7) & 0xFF);
        frame[4] = (byte)((receiveSeq << 1) & 0xFE);
        frame[5] = (byte)((receiveSeq >> 7) & 0xFF);
        asdu.CopyTo(frame, 6);
        return frame;
    }

    public enum FrameKind { I, S, U }

    public static FrameKind ClassifyControl(byte b0) => (b0 & 0x01) == 0 ? FrameKind.I : (b0 & 0x03) == 0x01 ? FrameKind.S : FrameKind.U;

    public static int DecodeSeq(byte lo, byte hi) => ((hi << 7) | (lo >> 1)) & 0x7FFF;

    // ---- ASDU ----

    public static byte[] BuildGeneralInterrogation(int commonAddress, byte originatorAddress = 0, byte qoi = 20)
    {
        var asdu = new byte[6 + IoaSize + 1];
        asdu[0] = (byte)Iec104TypeId.C_IC_NA_1;
        asdu[1] = 0x01; // VSQ: SQ=0, 1 object
        asdu[2] = (byte)Iec104Cot.Activation;
        asdu[3] = originatorAddress;
        BinaryPrimitives.WriteUInt16LittleEndian(asdu.AsSpan(4, 2), (ushort)commonAddress);
        // IOA = 0 (addresses the station, not a specific point), then QOI.
        asdu[9] = qoi;
        return asdu;
    }

    public static Iec104Asdu DecodeAsdu(ReadOnlySpan<byte> asdu)
    {
        var typeId = (Iec104TypeId)asdu[0];
        var sq = (asdu[1] & 0x80) != 0;
        var count = asdu[1] & 0x7F;
        var cotByte = asdu[2];
        var test = (cotByte & 0x80) != 0;
        var negative = (cotByte & 0x40) != 0;
        var cot = (Iec104Cot)(cotByte & 0x3F);
        var originator = asdu[3];
        var commonAddress = BinaryPrimitives.ReadUInt16LittleEndian(asdu.Slice(4, 2));

        var points = new List<Iec104Point>(count);
        var offset = 6;
        var firstIoa = ReadIoa(asdu, offset);
        for (var i = 0; i < count; i++)
        {
            var ioa = sq ? firstIoa + i : ReadIoa(asdu, offset);
            if (!sq || i == 0) offset += IoaSize;

            var (point, elementSize) = DecodeElement(typeId, ioa, asdu, offset);
            points.Add(point);
            offset += elementSize;
        }

        return new Iec104Asdu(typeId, cot, negative, test, originator, commonAddress, points);
    }

    private static int ReadIoa(ReadOnlySpan<byte> asdu, int offset) => asdu[offset] | (asdu[offset + 1] << 8) | (asdu[offset + 2] << 16);

    private static (Iec104Point Point, int ElementSize) DecodeElement(Iec104TypeId type, int ioa, ReadOnlySpan<byte> asdu, int offset)
    {
        switch (type)
        {
            case Iec104TypeId.M_SP_NA_1:
            {
                var siq = asdu[offset];
                return (new Iec104Point(ioa, (siq & 0x01) != 0, (Iec104Quality)(siq & 0xF0), null), 1);
            }
            case Iec104TypeId.M_SP_TB_1:
            {
                var siq = asdu[offset];
                var ts = DecodeCp56Time2a(asdu.Slice(offset + 1, 7));
                return (new Iec104Point(ioa, (siq & 0x01) != 0, (Iec104Quality)(siq & 0xF0), ts), 8);
            }
            case Iec104TypeId.M_DP_NA_1:
            {
                var diq = asdu[offset];
                var state = diq & 0x03; // 0=indeterminate,1=off,2=on,3=indeterminate
                return (new Iec104Point(ioa, state == 2, (Iec104Quality)(diq & 0xF0), null), 1);
            }
            case Iec104TypeId.M_ME_NA_1:
            {
                var raw = BinaryPrimitives.ReadInt16LittleEndian(asdu.Slice(offset, 2));
                var quality = asdu[offset + 2];
                return (new Iec104Point(ioa, raw, (Iec104Quality)quality, null), 3);
            }
            case Iec104TypeId.M_ME_NB_1:
            {
                var raw = BinaryPrimitives.ReadInt16LittleEndian(asdu.Slice(offset, 2));
                var quality = asdu[offset + 2];
                return (new Iec104Point(ioa, raw, (Iec104Quality)quality, null), 3);
            }
            case Iec104TypeId.M_ME_NC_1:
            {
                var value = BinaryPrimitives.ReadSingleLittleEndian(asdu.Slice(offset, 4));
                var quality = asdu[offset + 4];
                return (new Iec104Point(ioa, value, (Iec104Quality)quality, null), 5);
            }
            case Iec104TypeId.M_ME_TF_1:
            {
                var value = BinaryPrimitives.ReadSingleLittleEndian(asdu.Slice(offset, 4));
                var quality = asdu[offset + 4];
                var ts = DecodeCp56Time2a(asdu.Slice(offset + 5, 7));
                return (new Iec104Point(ioa, value, (Iec104Quality)quality, ts), 12);
            }
            default:
                throw new NotSupportedException($"IEC 104: unsupported type ID {(byte)type} ({type})");
        }
    }

    /// <summary>CP56Time2a: ms(2 LE) + minute(6 bits) + hour(5 bits) + day-of-month(5 bits) +
    /// month(4 bits) + year(7 bits, 2-digit). Standard fixed bit-packed timestamp used throughout
    /// IEC 60870-5-101/104.</summary>
    public static DateTime DecodeCp56Time2a(ReadOnlySpan<byte> t)
    {
        var ms = BinaryPrimitives.ReadUInt16LittleEndian(t[..2]);
        var second = ms / 1000;
        var millis = ms % 1000;
        var minute = t[2] & 0x3F;
        var hour = t[3] & 0x1F;
        var day = t[4] & 0x1F;
        var month = t[5] & 0x0F;
        var year = 2000 + (t[6] & 0x7F);
        try { return new DateTime(year, Math.Max(1, month), Math.Max(1, day), hour, minute, second, millis, DateTimeKind.Utc); }
        catch (ArgumentOutOfRangeException) { return DateTime.UtcNow; }
    }
}
