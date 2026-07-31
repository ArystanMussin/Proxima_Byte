using System.Buffers.Binary;

namespace PGW.Drivers.Dlms;

public enum DlmsAuthentication : byte { None = 0, Lls = 1 }

public enum DlmsAssociationResult : byte { Accepted = 0, RejectedPermanent = 1, RejectedTransient = 2 }

public sealed class DlmsAssociationException(DlmsAssociationResult result) : Exception($"DLMS association rejected: {result}")
{
    public DlmsAssociationResult Result { get; } = result;
}

public sealed class DlmsAccessException(byte accessResult) : Exception($"DLMS GET denied, access result {accessResult}")
{
    public byte AccessResult { get; } = accessResult;
}

/// <summary>
/// BER (Basic Encoding Rules), ACSE (AARQ/AARE), xDLMS (InitiateRequest, Get-Request/Response-Normal)
/// and COSEM common-data-type encode/decode for a minimal read-only DLMS/COSEM client.
///
/// This is deliberately a small slice of a very large standard (the DLMS Green/Blue Books run to
/// hundreds of pages): TCP wrapper transport only (IEC 62056-47) — no HDLC/serial; GET-Request-Normal
/// only — no block transfer, SET, ACTION, selective access, or push; No-Security or Low Level
/// Security (LLS) only — no High Level Security/ciphering. See README "DLMS/COSEM" for what that
/// means in practice and how these byte layouts were verified (against the u9n/dlms-cosem open-source
/// reference implementation's actual source, not from memory).
/// </summary>
public static class DlmsCodec
{
    // {2, 16, 756, 5, 8} — the DLMS UA's assigned OID arc, BER-encoded. Verified against
    // dlms_cosem/protocol/acse/base.py (DLMSObjectIdentifier.PREFIX).
    private static readonly byte[] OidPrefix = { 0x60, 0x85, 0x74, 0x05, 0x08 };

    // ---- BER ----

    public static byte[] BerEncode(byte tag, byte[] data)
    {
        // Short-form length only (<=127 bytes) — every APDU this client sends fits comfortably
        // within that (AARQ/GET-Request are a few dozen bytes at most).
        if (data.Length > 127) throw new NotSupportedException("DLMS: BER long-form length not needed/supported for outbound APDUs");
        var result = new byte[2 + data.Length];
        result[0] = tag;
        result[1] = (byte)data.Length;
        data.CopyTo(result, 2);
        return result;
    }

    /// <summary>Reads one BER TLV starting at <paramref name="offset"/>, advances it past the value,
    /// and returns the tag byte and the value's byte range. Handles both short-form and long-form
    /// (0x81/0x82-prefixed) length, since incoming APDUs from a real meter aren't bounded like ours.</summary>
    public static (byte Tag, int ValueStart, int ValueLength) BerDecodeTlv(byte[] data, ref int offset)
    {
        var tag = data[offset++];
        var lenByte = data[offset++];
        int length;
        if ((lenByte & 0x80) == 0)
        {
            length = lenByte;
        }
        else
        {
            var lenBytes = lenByte & 0x7F;
            length = 0;
            for (var i = 0; i < lenBytes; i++) length = (length << 8) | data[offset++];
        }
        var valueStart = offset;
        offset += length;
        return (tag, valueStart, length);
    }

    // ---- Wrapper header (IEC 62056-47): 8 bytes, all fields big-endian uint16. ----

    public static byte[] BuildWrapperHeader(ushort sourceWport, ushort destinationWport, int bodyLength)
    {
        var header = new byte[8];
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(0, 2), 1); // version
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2, 2), sourceWport);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4, 2), destinationWport);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6, 2), (ushort)bodyLength);
        return header;
    }

    public static int ReadWrapperBodyLength(byte[] header) => BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(6, 2));

    // ---- AARQ / InitiateRequest ----

    /// <summary>Builds an AARQ APDU proposing Logical Name referencing, no ciphering, and (optionally)
    /// Low Level Security. Byte layout verified against dlms_cosem/protocol/acse/aarq.py,
    /// acse/base.py, acse/user_information.py and xdlms/initiate_request.py + conformance.py.</summary>
    public static byte[] BuildAarq(DlmsAuthentication auth, byte[]? password, ushort clientMaxPduSize)
    {
        var body = new List<byte>();

        // [1] application-context-name: LN referencing, no ciphering (context id 1).
        body.AddRange(BerEncode(0xA1, BerEncode(0x06, [.. OidPrefix, 0x01, 0x01])));

        if (auth == DlmsAuthentication.Lls)
        {
            // [10] sender-acse-requirements: 2 raw bytes (not further BER-wrapped inside), fixed
            // value per the Green Book's own examples: 0x07 unused-bit-count, 0x80 = authentication bit set.
            body.AddRange(BerEncode(0x8A, [0x07, 0x80]));
            // [11] mechanism-name: LLS = auth arc(2), mechanism id 1.
            body.AddRange(BerEncode(0x8B, [.. OidPrefix, 0x02, 0x01]));
            // [12] calling-authentication-value: CHOICE charstring [0] IMPLICIT, tag 0x80.
            body.AddRange(BerEncode(0xAC, BerEncode(0x80, password ?? [])));
        }

        var initiateRequest = BuildInitiateRequest(clientMaxPduSize);
        // [30] user-information: OCTET STRING (tag 0x04) wrapping the xDLMS InitiateRequest.
        body.AddRange(BerEncode(0xBE, BerEncode(0x04, initiateRequest)));

        return BerEncode(0x60, body.ToArray());
    }

    /// <summary>xDLMS InitiateRequest: only proposes the "get" service (bit 4 of the 24-bit
    /// conformance block) — this client never sends SET/ACTION. Layout verified against
    /// xdlms/initiate_request.py + xdlms/conformance.py.</summary>
    private static byte[] BuildInitiateRequest(ushort clientMaxPduSize)
    {
        var out_ = new List<byte>
        {
            0x01, // TAG: initiateRequest
            0x00, // dedicated-key: absent
            0x00, // response-allowed marker
            0x00, // proposed-quality-of-service: absent
            0x06, // proposed-dlms-version-number = 6
            0x5F, 0x1F, 0x04, // conformance: [Application 31] tag + length(4)
            0x00, // conformance: unused-bits = 0
            0x00, 0x00, 0x10, // conformance bitmask: bit4 ("get") set, nothing else
        };
        out_.Add((byte)(clientMaxPduSize >> 8));
        out_.Add((byte)clientMaxPduSize);
        return out_.ToArray();
    }

    /// <summary>Parses an AARE APDU (tag 0x61) far enough to know whether the association was
    /// accepted — doesn't decode the negotiated conformance/max-pdu-size in the response's
    /// user-information (InitiateResponse); this client's own request is already minimal enough
    /// that the server's own limits are what matter, and re-parsing them buys nothing for a
    /// register-reading client. Throws <see cref="DlmsAssociationException"/> if rejected.</summary>
    public static void ParseAareOrThrow(byte[] aare)
    {
        if (aare.Length < 2 || aare[0] != 0x61)
            throw new IOException($"DLMS: expected AARE (tag 0x61), got 0x{aare[0]:X2}");

        var offset = 2; // skip AARE tag + outer length byte
        while (offset < aare.Length)
        {
            var (tag, valueStart, valueLength) = BerDecodeTlv(aare, ref offset);
            if (tag != 0xA2) continue; // [2] result
            // result is itself a BER INTEGER (tag 0x02, length 1) — unwrap it.
            var innerOffset = valueStart;
            var (_, innerStart, _) = BerDecodeTlv(aare, ref innerOffset);
            var result = (DlmsAssociationResult)aare[innerStart];
            if (result != DlmsAssociationResult.Accepted) throw new DlmsAssociationException(result);
            return;
        }
        throw new IOException("DLMS: AARE had no [2] result field");
    }

    // ---- GET.request-normal / GET.response-normal ----

    private const byte InvokeIdAndPriority = 0xC1; // invoke_id=1, confirmed=1(bit6), high_priority=1(bit7)

    /// <summary>Layout verified against xdlms/get.py (GetRequestNormal) and cosem/base.py
    /// (CosemAttribute: class_id(2) + OBIS(6) + attribute_id(1), all big-endian/raw).</summary>
    public static byte[] BuildGetRequestNormal(ushort classId, byte[] obis6, byte attributeId)
    {
        if (obis6.Length != 6) throw new ArgumentException("OBIS must be exactly 6 bytes", nameof(obis6));
        var apdu = new byte[4 + 9 + 1];
        apdu[0] = 0xC0; // get-request tag (192)
        apdu[1] = 0x01; // get-request-normal
        apdu[2] = InvokeIdAndPriority;
        BinaryPrimitives.WriteUInt16BigEndian(apdu.AsSpan(3, 2), classId);
        obis6.CopyTo(apdu, 5);
        apdu[11] = attributeId;
        apdu[12] = 0x00; // access-selection: none
        return apdu;
    }

    /// <summary>Parses a GET.response-normal (tag 0xC4) and decodes its Data value. Throws
    /// <see cref="DlmsAccessException"/> if the server responded with an access error instead of
    /// data. Layout verified against xdlms/get.py (GetResponseNormal / GetResponseNormalWithError).</summary>
    public static object? ParseGetResponseNormal(byte[] apdu)
    {
        if (apdu.Length < 4 || apdu[0] != 0xC4)
            throw new IOException($"DLMS: expected GET.response-normal (tag 0xC4), got 0x{(apdu.Length > 0 ? apdu[0] : 0):X2}");
        if (apdu[1] != 0x01)
            throw new NotSupportedException($"DLMS: only get-response-normal is supported, got response type {apdu[1]}");

        var choice = apdu[3];
        if (choice == 1) throw new DlmsAccessException(apdu[4]);
        if (choice != 0) throw new IOException($"DLMS: unexpected GET.response data choice {choice}");

        var offset = 4;
        return DecodeData(apdu, ref offset);
    }

    // ---- COSEM common data types ----
    // Tag values and fixed/variable-length framing verified against dlms_data.py: fixed-length types
    // are `tag + N raw bytes`; variable-length types are `tag + 1 length byte + N bytes`.

    public static object? DecodeData(byte[] data, ref int offset)
    {
        var tag = data[offset++];
        switch (tag)
        {
            case 0: return null; // null-data
            case 3: return data[offset++] != 0; // boolean
            case 5: { var v = ReadInt(data, offset, 4, signed: true); offset += 4; return v; } // double-long
            case 6: { var v = ReadInt(data, offset, 4, signed: false); offset += 4; return v; } // double-long-unsigned
            case 9: { var len = data[offset++]; var v = data.AsSpan(offset, len).ToArray(); offset += len; return v; } // octet-string
            case 10: { var len = data[offset++]; var v = System.Text.Encoding.ASCII.GetString(data, offset, len); offset += len; return v; } // visible-string
            case 15: { var v = (sbyte)data[offset++]; return (long)v; } // integer (8-bit signed)
            case 16: { var v = ReadInt(data, offset, 2, signed: true); offset += 2; return v; } // long (16-bit signed)
            case 17: { var v = data[offset++]; return (long)v; } // unsigned (8-bit)
            case 18: { var v = ReadInt(data, offset, 2, signed: false); offset += 2; return v; } // long-unsigned (16-bit)
            case 20: { var v = ReadInt(data, offset, 8, signed: true); offset += 8; return v; } // long64
            case 21: { var v = ReadInt(data, offset, 8, signed: false); offset += 8; return unchecked((long)v); } // long64-unsigned
            case 22: { var v = data[offset++]; return (long)v; } // enum
            case 23: { var v = BinaryPrimitives.ReadSingleBigEndian(data.AsSpan(offset, 4)); offset += 4; return v; } // float32
            case 24: { var v = BinaryPrimitives.ReadDoubleBigEndian(data.AsSpan(offset, 8)); offset += 8; return v; } // float64
            default:
                throw new NotSupportedException($"DLMS: unsupported/compound data type tag {tag} (arrays/structures are out of scope for this client — read the leaf attribute directly)");
        }
    }

    private static long ReadInt(byte[] data, int offset, int length, bool signed)
    {
        var span = data.AsSpan(offset, length);
        return length switch
        {
            2 => signed ? BinaryPrimitives.ReadInt16BigEndian(span) : BinaryPrimitives.ReadUInt16BigEndian(span),
            4 => signed ? BinaryPrimitives.ReadInt32BigEndian(span) : BinaryPrimitives.ReadUInt32BigEndian(span),
            8 => signed ? BinaryPrimitives.ReadInt64BigEndian(span) : unchecked((long)BinaryPrimitives.ReadUInt64BigEndian(span)),
            _ => throw new NotSupportedException($"DLMS: unsupported integer width {length}"),
        };
    }

    public static byte[] ParseObis(string obis)
    {
        var parts = obis.Split('.');
        if (parts.Length is not (5 or 6))
            throw new FormatException($"DLMS: '{obis}' is not a valid OBIS code (expected 5 or 6 dot-separated parts)");
        var result = new byte[6];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!byte.TryParse(parts[i], out result[i]))
                throw new FormatException($"DLMS: '{obis}' is not a valid OBIS code (part '{parts[i]}' is not 0-255)");
        }
        if (parts.Length == 5) result[5] = 255; // F defaults to 255 ("not billing-period-specific")
        return result;
    }
}
