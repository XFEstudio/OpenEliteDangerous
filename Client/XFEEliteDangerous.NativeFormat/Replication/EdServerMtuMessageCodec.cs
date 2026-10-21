namespace XFEEliteDangerous.NativeFormat.Replication;

/// <summary>Only IDs confirmed in the 2026.09.03.332841 built-in letter registry.</summary>
public enum EdServerBuiltInLetter : byte
{
    Sync = 0,
    Ack = 1,
    Sequence = 2,
    ReliableFragment = 3,
    UnreliableFragment = 4,
    Route = 5,
    MtuTest = 6,
    MtuReply = 7,
    IdAllocationRequest = 10,
    IdAllocated = 11
}

/// <summary>
/// Field codecs for the verified MTU letter bodies. The caller must separately
/// validate the enclosing letter type, negotiated type width and sequence wrapper.
/// No acknowledgement or connection transition is inferred by these methods.
/// </summary>
public static class EdServerMtuMessageCodec
{
    public static EdServerMtuTest ReadTest(ReadOnlySpan<byte> packedData, ref int bitOffset)
    {
        var position = bitOffset;
        var mtu = (ushort)ReadBits(packedData, ref position, 16);
        var count = (ushort)ReadBits(packedData, ref position, 16);
        RequireAvailable(packedData.Length, position, count * 8);
        var padding = new byte[count];
        for (var index = 0; index < count; index++)
            padding[index] = (byte)ReadBits(packedData, ref position, 8);
        bitOffset = position;
        return new(mtu, padding);
    }

    public static ushort ReadReply(ReadOnlySpan<byte> packedData, ref int bitOffset)
    {
        var position = bitOffset;
        var mtu = (ushort)ReadBits(packedData, ref position, 16);
        bitOffset = position;
        return mtu;
    }

    public static void WriteTest(Span<byte> packedData, ref int bitOffset, ushort mtu, ReadOnlySpan<byte> padding)
    {
        if (padding.Length > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(padding));
        RequireAvailable(packedData.Length, bitOffset, 32 + padding.Length * 8);
        var position = bitOffset;
        WriteBits(packedData, ref position, mtu, 16);
        WriteBits(packedData, ref position, (ushort)padding.Length, 16);
        foreach (var value in padding)
            WriteBits(packedData, ref position, value, 8);
        bitOffset = position;
    }

    public static void WriteReply(Span<byte> packedData, ref int bitOffset, ushort mtu)
    {
        RequireAvailable(packedData.Length, bitOffset, 16);
        WriteBits(packedData, ref bitOffset, mtu, 16);
    }

    private static uint ReadBits(ReadOnlySpan<byte> bytes, ref int position, int count)
    {
        RequireAvailable(bytes.Length, position, count);
        uint result = 0;
        for (var bit = 0; bit < count; bit++, position++)
            result |= (uint)((bytes[position >> 3] >> (position & 7)) & 1) << bit;
        return result;
    }

    private static void WriteBits(Span<byte> bytes, ref int position, uint value, int count)
    {
        for (var bit = 0; bit < count; bit++, position++)
        {
            var mask = 1 << (position & 7);
            bytes[position >> 3] = (byte)((bytes[position >> 3] & ~mask) | (((value >> bit) & 1) != 0 ? mask : 0));
        }
    }

    private static void RequireAvailable(int byteLength, int position, int bitCount)
    {
        if (position < 0 || (long)position + bitCount > (long)byteLength * 8)
            throw new InvalidDataException("The packed MTU message is truncated or its bit offset is invalid.");
    }
}

public sealed class EdServerMtuTest
{
    public ushort Mtu { get; }
    public ReadOnlyMemory<byte> Padding { get; }
    internal EdServerMtuTest(ushort mtu, byte[] padding) => (Mtu, Padding) = (mtu, padding);
}
