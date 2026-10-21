namespace XFEEliteDangerous.NativeFormat.Replication;

/// <summary>Known SYNC wire fields; unnamed option semantics are intentionally preserved.</summary>
public sealed record EdServerSyncMessage(
    ushort Protocol, ushort Sequence, ushort Acknowledgement, byte LetterTypeBits,
    uint ClockBits, bool LengthDelimited, byte AlignmentMode, ushort CompressionVersion,
    ReadOnlyMemory<byte> NameBytes, uint ObservedAddress, ushort ObservedPort,
    ReadOnlyMemory<byte> EncryptedKeyBytes, bool DiscoveryFlag, bool IsClient,
    uint BuildRevision, uint BaseRevision)
{
    public override string ToString() => $"Native SYNC protocol={Protocol} typeBits={LetterTypeBits}; private fields omitted";
}

public readonly record struct EdServerAckMessage(ushort Sequence, ushort SelectiveMask, byte Allowance);

/// <summary>
/// Verified control-letter field packing. SYNC includes its zero type/preamble
/// (16 bits total); ACK and SEQ methods read bodies after an enclosing type.
/// Optional per-letter framing and reliable delivery remain caller concerns.
/// </summary>
public static class EdServerControlMessageCodec
{
    public const int InitialLetterTypeBits = 16;
    public const int MaximumNameBytes = 31; // Native max=20 selects a five-bit length field.
    public const int MaximumEncryptedKeyBytes = 4096;

    public static EdServerSyncMessage ReadSyncLetter(ReadOnlySpan<byte> bytes, ref int bitOffset, ushort expectedProtocol)
    {
        var p = bitOffset;
        if (Read(bytes, ref p, 16) != 0)
            throw new InvalidDataException("Expected a zero SYNC type/preamble.");
        var protocol = (ushort)Read(bytes, ref p, 16);
        if (protocol != expectedProtocol)
            throw new InvalidDataException("The native SYNC protocol version does not match.");
        var sequence = (ushort)Read(bytes, ref p, 16);
        var acknowledgement = (ushort)Read(bytes, ref p, 16);
        var typeBits = (byte)(Read(bytes, ref p, 4) + 1);
        var clockBits = Read(bytes, ref p, 32);
        var lengthDelimited = Read(bytes, ref p, 1) != 0;
        var alignment = (byte)Read(bytes, ref p, 8);
        var compression = (ushort)Read(bytes, ref p, 16);
        var name = ReadBytes(bytes, ref p, (int)Read(bytes, ref p, 5));
        var address = Read(bytes, ref p, 32);
        var port = (ushort)Read(bytes, ref p, 16);
        var lengthBits = (int)Read(bytes, ref p, 5);
        var keyLength = (1UL << lengthBits) + Read(bytes, ref p, lengthBits) - 1;
        if (keyLength > MaximumEncryptedKeyBytes)
            throw new InvalidDataException("The native SYNC key vector exceeds the supported bound.");
        var key = ReadBytes(bytes, ref p, (int)keyLength);
        var discovery = Read(bytes, ref p, 1) != 0;
        var isClient = Read(bytes, ref p, 1) != 0;
        var build = Read(bytes, ref p, 32);
        var baseBuild = Read(bytes, ref p, 32);
        bitOffset = p;
        return new(protocol, sequence, acknowledgement, typeBits, clockBits, lengthDelimited,
            alignment, compression, name, address, port, key, discovery, isClient, build, baseBuild);
    }

    public static byte[] EncodeSyncLetter(EdServerSyncMessage message)
    {
        Validate(message);
        var lengthBits = System.Numerics.BitOperations.Log2((uint)message.EncryptedKeyBytes.Length + 1);
        var bitCount = 16 + 16 + 16 + 16 + 4 + 32 + 1 + 8 + 16 + 5
            + message.NameBytes.Length * 8 + 32 + 16 + 5 + lengthBits
            + message.EncryptedKeyBytes.Length * 8 + 1 + 1 + 32 + 32;
        var bytes = new byte[(bitCount + 7) / 8];
        var p = 0;
        Write(bytes, ref p, 0, 16);
        Write(bytes, ref p, message.Protocol, 16);
        Write(bytes, ref p, message.Sequence, 16);
        Write(bytes, ref p, message.Acknowledgement, 16);
        Write(bytes, ref p, (uint)message.LetterTypeBits - 1, 4);
        Write(bytes, ref p, message.ClockBits, 32);
        Write(bytes, ref p, message.LengthDelimited ? 1U : 0, 1);
        Write(bytes, ref p, message.AlignmentMode, 8);
        Write(bytes, ref p, message.CompressionVersion, 16);
        Write(bytes, ref p, (uint)message.NameBytes.Length, 5);
        WriteBytes(bytes, ref p, message.NameBytes.Span);
        Write(bytes, ref p, message.ObservedAddress, 32);
        Write(bytes, ref p, message.ObservedPort, 16);
        Write(bytes, ref p, (uint)lengthBits, 5);
        Write(bytes, ref p, (uint)message.EncryptedKeyBytes.Length + 1 - (1U << lengthBits), lengthBits);
        WriteBytes(bytes, ref p, message.EncryptedKeyBytes.Span);
        Write(bytes, ref p, message.DiscoveryFlag ? 1U : 0, 1);
        Write(bytes, ref p, message.IsClient ? 1U : 0, 1);
        Write(bytes, ref p, message.BuildRevision, 32);
        Write(bytes, ref p, message.BaseRevision, 32);
        return bytes;
    }

    public static EdServerAckMessage ReadAckBody(ReadOnlySpan<byte> bytes, ref int bitOffset)
    {
        var p = bitOffset;
        var sequence = (ushort)Read(bytes, ref p, 16);
        var mask = (ushort)Read(bytes, ref p, 10);
        var allowance = (byte)Read(bytes, ref p, 6);
        if (sequence == 0)
            throw new InvalidDataException("Native ACK sequence cannot be zero.");
        bitOffset = p;
        return new(sequence, mask, allowance);
    }

    public static ushort ReadSequenceBody(ReadOnlySpan<byte> bytes, ref int bitOffset)
    {
        var p = bitOffset;
        var value = (ushort)Read(bytes, ref p, 16);
        if (value == 0)
            throw new InvalidDataException("Native SEQ sequence cannot be zero.");
        bitOffset = p;
        return value;
    }

    /// <summary>
    /// Type 11's complete body is one UInt64, verified at native RVA 0x64FC70.
    /// This reads a value only; allocation range policy and reliable wrapping are separate.
    /// Type 10 has no body, verified at native RVA 0x64FCD0.
    /// </summary>
    public static ulong ReadAllocatedIdBody(ReadOnlySpan<byte> bytes, ref int bitOffset)
    {
        var p = bitOffset;
        var low = Read(bytes, ref p, 32);
        var high = Read(bytes, ref p, 32);
        bitOffset = p;
        return low | ((ulong)high << 32);
    }

    public static byte[] EncodeAllocatedIdLetter(ulong allocatedBase, int letterTypeBits)
    {
        if (letterTypeBits is < 4 or > 16)
            throw new ArgumentOutOfRangeException(nameof(letterTypeBits));
        var bytes = new byte[(letterTypeBits + 64 + 7) / 8];
        var p = 0;
        Write(bytes, ref p, (byte)EdServerBuiltInLetter.IdAllocated, letterTypeBits);
        Write(bytes, ref p, (uint)allocatedBase, 32);
        Write(bytes, ref p, (uint)(allocatedBase >> 32), 32);
        return bytes;
    }

    public static byte[] EncodeAckLetter(EdServerAckMessage message, int letterTypeBits)
    {
        if (message.Sequence == 0 || message.SelectiveMask > 1023 || message.Allowance > 63)
            throw new ArgumentOutOfRangeException(nameof(message));
        if (letterTypeBits is < 1 or > 16)
            throw new ArgumentOutOfRangeException(nameof(letterTypeBits));
        var bytes = new byte[(letterTypeBits + 32 + 7) / 8];
        var p = 0;
        Write(bytes, ref p, (byte)EdServerBuiltInLetter.Ack, letterTypeBits);
        Write(bytes, ref p, message.Sequence, 16);
        Write(bytes, ref p, message.SelectiveMask, 10);
        Write(bytes, ref p, message.Allowance, 6);
        return bytes;
    }

    /// <summary>Writes without inserting byte padding between adjacent letters.</summary>
    public static void WriteLetterType(Span<byte> bytes, ref int bitOffset, byte type, int letterTypeBits)
    {
        if (letterTypeBits is < 1 or > 16 || type >= (1U << letterTypeBits))
            throw new ArgumentOutOfRangeException(nameof(letterTypeBits));
        Write(bytes, ref bitOffset, type, letterTypeBits);
    }

    public static void WriteSequenceLetter(Span<byte> bytes, ref int bitOffset, ushort sequence, int letterTypeBits)
    {
        if (sequence == 0 || letterTypeBits is < 2 or > 16)
            throw new ArgumentOutOfRangeException(nameof(sequence));
        Require(bytes.Length, bitOffset, letterTypeBits + 16);
        WriteLetterType(bytes, ref bitOffset, (byte)EdServerBuiltInLetter.Sequence, letterTypeBits);
        Write(bytes, ref bitOffset, sequence, 16);
    }

    public static void WriteAckLetter(Span<byte> bytes, ref int bitOffset, EdServerAckMessage message, int letterTypeBits)
    {
        if (message.Sequence == 0 || message.SelectiveMask > 1023 || message.Allowance > 63
            || letterTypeBits is < 1 or > 16)
            throw new ArgumentOutOfRangeException(nameof(message));
        Require(bytes.Length, bitOffset, letterTypeBits + 32);
        WriteLetterType(bytes, ref bitOffset, (byte)EdServerBuiltInLetter.Ack, letterTypeBits);
        Write(bytes, ref bitOffset, message.Sequence, 16);
        Write(bytes, ref bitOffset, message.SelectiveMask, 10);
        Write(bytes, ref bitOffset, message.Allowance, 6);
    }

    public static void WriteAllocatedIdLetter(Span<byte> bytes, ref int bitOffset, ulong allocatedBase, int letterTypeBits)
    {
        if (letterTypeBits is < 4 or > 16)
            throw new ArgumentOutOfRangeException(nameof(letterTypeBits));
        Require(bytes.Length, bitOffset, letterTypeBits + 64);
        WriteLetterType(bytes, ref bitOffset, (byte)EdServerBuiltInLetter.IdAllocated, letterTypeBits);
        Write(bytes, ref bitOffset, (uint)allocatedBase, 32);
        Write(bytes, ref bitOffset, (uint)(allocatedBase >> 32), 32);
    }

    public static bool IsZeroPadding(ReadOnlySpan<byte> bytes, int bitOffset)
    {
        Require(bytes.Length, bitOffset, 0);
        for (long bit = bitOffset; bit < (long)bytes.Length * 8; bit++)
            if (((bytes[(int)(bit / 8)] >> (int)(bit % 8)) & 1) != 0)
                return false;
        return true;
    }

    public static byte ReadLetterType(ReadOnlySpan<byte> bytes, ref int bitOffset, int letterTypeBits)
    {
        if (letterTypeBits is < 1 or > 16)
            throw new ArgumentOutOfRangeException(nameof(letterTypeBits));
        var type = Read(bytes, ref bitOffset, letterTypeBits);
        if (type > byte.MaxValue)
            throw new InvalidDataException("The letter type is outside the verified native registry range.");
        return (byte)type;
    }

    private static void Validate(EdServerSyncMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.LetterTypeBits is < 1 or > 16 || message.NameBytes.Length > MaximumNameBytes
            || message.EncryptedKeyBytes.Length > MaximumEncryptedKeyBytes)
            throw new ArgumentOutOfRangeException(nameof(message));
    }

    private static byte[] ReadBytes(ReadOnlySpan<byte> bytes, ref int p, int length)
    {
        Require(bytes.Length, p, length * 8);
        var result = new byte[length];
        for (var i = 0; i < length; i++) result[i] = (byte)Read(bytes, ref p, 8);
        return result;
    }

    private static void WriteBytes(Span<byte> bytes, ref int p, ReadOnlySpan<byte> value)
    {
        foreach (var b in value) Write(bytes, ref p, b, 8);
    }

    private static uint Read(ReadOnlySpan<byte> bytes, ref int p, int bits)
    {
        Require(bytes.Length, p, bits);
        uint result = 0;
        for (var b = 0; b < bits; b++, p++) result |= (uint)((bytes[p / 8] >> (p % 8)) & 1) << b;
        return result;
    }

    private static void Write(Span<byte> bytes, ref int p, uint value, int bits)
    {
        Require(bytes.Length, p, bits);
        for (var b = 0; b < bits; b++, p++)
        {
            var mask = 1 << (p % 8);
            bytes[p / 8] = (byte)((bytes[p / 8] & ~mask) | (((value >> b) & 1) != 0 ? mask : 0));
        }
    }

    private static void Require(int byteCount, int p, int bits)
    {
        if (p < 0 || (long)p + bits > (long)byteCount * 8)
            throw new InvalidDataException("Native control letter is truncated or has an invalid offset.");
    }
}
