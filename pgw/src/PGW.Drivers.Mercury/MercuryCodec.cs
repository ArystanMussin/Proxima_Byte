namespace PGW.Drivers.Mercury;

/// <summary>
/// Wire-level framing/decoding for the Меркурий 230/200-family protocol (Инкотекс/Incotex) — the most
/// common electricity meter protocol on RS-485 across Kazakhstan/Russia/CIS metering installs. This is
/// NOT Modbus (no function codes, no register model) even though it happens to reuse the same CRC16
/// algorithm and, like Modbus RTU, typically rides RS-485.
///
/// Reverse-engineered from two independently-maintained open-source implementations (a PHP client and
/// an ESPHome/C++ component actively used against real Меркурий 230 hardware), cross-checked against
/// each other for internal consistency (byte-length arithmetic on every response shape lines up exactly
/// across both). It has NOT been validated against real Меркурий hardware in this environment — there is
/// none available here. Treat every decoded value as "should be right" rather than "is right" until
/// checked against a real meter; §"Меркурий" in the README says the same thing more loudly.
/// </summary>
public static class MercuryCodec
{
    /// <summary>Same CRC16 as Modbus RTU (init 0xFFFF, poly 0xA001, transmitted low-byte-first) — pure
    /// coincidence of two unrelated protocols reaching for the same well-known CRC, not a Modbus tie-in.</summary>
    public static ushort Crc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
        }
        return crc;
    }

    /// <summary>Appends the frame's CRC16 (low byte first) to <paramref name="frameWithoutCrc"/>.</summary>
    public static byte[] AppendCrc(byte[] frameWithoutCrc)
    {
        var crc = Crc16(frameWithoutCrc);
        var full = new byte[frameWithoutCrc.Length + 2];
        frameWithoutCrc.CopyTo(full, 0);
        full[^2] = (byte)(crc & 0xFF);
        full[^1] = (byte)(crc >> 8);
        return full;
    }

    public static bool VerifyCrc(ReadOnlySpan<byte> frameWithCrc)
    {
        if (frameWithCrc.Length < 2) return false;
        var body = frameWithCrc[..^2];
        var expected = Crc16(body);
        return frameWithCrc[^2] == (byte)(expected & 0xFF) && frameWithCrc[^1] == (byte)(expected >> 8);
    }

    /// <summary>Decodes a 4-byte accumulator (energy totals): word-swapped little-endian —
    /// value = (LE16(d[0],d[1]) &lt;&lt; 16) | LE16(d[2],d[3]). Verbatim-matched against the real
    /// firmware's <c>dm32_4()</c>.</summary>
    public static uint DecodeU32(ReadOnlySpan<byte> d)
    {
        uint v = d[1];
        v = (v << 8) | d[0];
        v = (v << 8) | d[3];
        v = (v << 8) | d[2];
        return v;
    }

    /// <summary>Decodes a 3-byte instantaneous value (voltage/current/power/frequency) — the top 2 bits
    /// of the first byte are flags, masked off. Verbatim-matched against the real firmware's
    /// <c>dm32_3()</c>.</summary>
    public static uint DecodeU24(ReadOnlySpan<byte> d)
    {
        uint v = (uint)(d[0] & 0x3F);
        v = (v << 8) | d[2];
        v = (v << 8) | d[1];
        return v;
    }
}
