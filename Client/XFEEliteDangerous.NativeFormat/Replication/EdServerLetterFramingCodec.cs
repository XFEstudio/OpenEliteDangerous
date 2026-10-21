namespace XFEEliteDangerous.NativeFormat.Replication;

/// <summary>
/// Optional native per-letter envelope. The caller reads/writes the type first,
/// begins a frame, processes the known body, then completes it. SYNC has no
/// length/trailer envelope, but still follows the advertised byte alignment.
/// Evidence: native RVA 0x4C17310, 0x4C23F5B and 0x4C441C0.
/// </summary>
public static class EdServerLetterFramingCodec
{
    public const ushort Trailer = 0xFADE;
    public const int MaximumBodyBits = 0xFFFFFF;

    public static EdServerLetterFrame BeginRead(ReadOnlySpan<byte> packet, ref int bitOffset,
        byte letterType, bool lengthDelimited, byte alignmentMode)
    {
        var p = bitOffset;
        Require(packet.Length, p, 0);
        var prefix = lengthDelimited && letterType != 0 ? p : -1;
        int? end = null;
        if (prefix >= 0)
        {
            var length = (int)Read(packet, ref p, 24);
            Require(packet.Length, p, (long)length + 16);
            end = p + length;
        }
        var frame = new EdServerLetterFrame(p, end, prefix, alignmentMode);
        bitOffset = p;
        return frame;
    }

    public static void CompleteRead(ReadOnlySpan<byte> packet, ref int bitOffset, EdServerLetterFrame frame)
    {
        var p = bitOffset;
        ValidateFrame(packet.Length, frame, p);
        if (frame.AlignmentMode != 0)
            AlignRead(packet, ref p);
        if (frame.DeclaredBodyEndBit is int end)
        {
            if (p != end)
                throw new InvalidDataException("The native letter body does not match its declared bit length.");
            if (Read(packet, ref p, 16) != Trailer)
                throw new InvalidDataException("The native letter trailer is not 0xFADE.");
        }
        bitOffset = p;
    }

    public static EdServerLetterFrame BeginWrite(Span<byte> packet, ref int bitOffset,
        byte letterType, bool lengthDelimited, byte alignmentMode)
    {
        var p = bitOffset;
        Require(packet.Length, p, 0);
        var prefix = lengthDelimited && letterType != 0 ? p : -1;
        if (prefix >= 0)
            Write(packet, ref p, 0, 24);
        bitOffset = p;
        return new EdServerLetterFrame(p, null, prefix, alignmentMode);
    }

    public static void CompleteWrite(Span<byte> packet, ref int bitOffset, EdServerLetterFrame frame)
    {
        var p = bitOffset;
        ValidateFrame(packet.Length, frame, p);
        var alignedEnd = frame.AlignmentMode == 0 ? p : checked((p + 7) & ~7);
        var length = alignedEnd - frame.BodyStartBit;
        if (frame.PrefixStartBit >= 0 && length > MaximumBodyBits)
            throw new InvalidDataException("The native letter body exceeds its 24-bit length.");
        Require(packet.Length, p, (long)alignedEnd - p + (frame.PrefixStartBit >= 0 ? 16 : 0));
        if (frame.AlignmentMode != 0)
            AlignWrite(packet, ref p);
        if (frame.PrefixStartBit >= 0)
        {
            var prefix = frame.PrefixStartBit;
            Write(packet, ref prefix, (uint)length, 24);
            Write(packet, ref p, Trailer, 16);
        }
        bitOffset = p;
    }

    /// <summary>Native BytePadding values 1..255 all select byte alignment here.</summary>
    public static void AlignRead(ReadOnlySpan<byte> packet, ref int bitOffset)
    {
        var end = checked((bitOffset + 7) & ~7);
        Require(packet.Length, bitOffset, (long)end - bitOffset);
        //555850 advances to the next byte without reading/clearing the skipped
        // bits. Native pooled writers need not zero them; they carry no field.
        // Declared frame length, trailer and all actual body bounds still apply.
        bitOffset = end;
    }

    public static void AlignWrite(Span<byte> packet, ref int bitOffset)
    {
        var end = checked((bitOffset + 7) & ~7);
        Require(packet.Length, bitOffset, (long)end - bitOffset);
        var p = bitOffset;
        while (p < end) Write(packet, ref p, 0, 1);
        bitOffset = end;
    }

    private static void ValidateFrame(int byteCount, EdServerLetterFrame frame, int p)
    {
        Require(byteCount, frame.BodyStartBit, 0);
        Require(byteCount, p, 0);
        if (p < frame.BodyStartBit || frame.PrefixStartBit < -1
            || (frame.PrefixStartBit >= 0 && frame.PrefixStartBit != frame.BodyStartBit - 24)
            || (frame.DeclaredBodyEndBit is int end && (end < p || end < frame.BodyStartBit
                || frame.PrefixStartBit < 0 || (long)end > (long)byteCount * 8 - 16)))
            throw new InvalidDataException("Invalid native letter frame boundaries.");
    }

    private static uint Read(ReadOnlySpan<byte> bytes, ref int p, int count)
    {
        Require(bytes.Length, p, count);
        uint result = 0;
        for (var i = 0; i < count; i++, p++)
            result |= (uint)((bytes[p / 8] >> (p % 8)) & 1) << i;
        return result;
    }

    private static void Write(Span<byte> bytes, ref int p, uint value, int count)
    {
        Require(bytes.Length, p, count);
        for (var i = 0; i < count; i++, p++)
        {
            var mask = 1 << (p % 8);
            bytes[p / 8] = (byte)((bytes[p / 8] & ~mask) | (((value >> i) & 1) == 0 ? 0 : mask));
        }
    }

    private static void Require(int byteCount, int bitOffset, long bitCount)
    {
        if (bitOffset < 0 || bitCount < 0 || (long)bitOffset + bitCount > (long)byteCount * 8)
            throw new InvalidDataException("Native letter envelope is truncated or has an invalid offset.");
    }
}

public readonly record struct EdServerLetterFrame(int BodyStartBit, int? DeclaredBodyEndBit,
    int PrefixStartBit, byte AlignmentMode);
