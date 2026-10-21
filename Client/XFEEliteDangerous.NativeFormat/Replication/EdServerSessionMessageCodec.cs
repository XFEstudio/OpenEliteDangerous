namespace XFEEliteDangerous.NativeFormat.Replication;

public enum EdServerSessionLetter : byte { Request = 13, Members = 14, Joined = 15, Left = 16 }

public sealed record EdServerSessionRequest(ulong SessionId, ulong InitialObjectIdBase);
public sealed record EdServerSessionMembers(ulong SessionId, ulong InitialObjectIdBase, IReadOnlyList<ulong> MachineRunIds);
/// <summary>The two native object-ID collections retain neutral names; their complete world semantics are unverified.</summary>
public sealed record EdServerSessionJoined(ulong SessionId, IReadOnlyList<ulong> FirstObjectIds, IReadOnlyList<ulong> SecondObjectIds);
public readonly record struct EdServerSessionLeft(ulong SessionId);

/// <summary>
/// Statically verified session bodies only; no membership policy, world objects,
/// ownership, successful join response or reliable-message acknowledgement is
/// inferred. Caller owns type, SEQ/window handling and optional letter framing.
/// IDs remain raw 48-bit values; native special-object mapping is not guessed.
/// </summary>
public static class EdServerSessionMessageCodec
{
    public const ulong MaximumWireId = 0xFFFFFFFFFFFF;
    public const int DefaultMaximumCollectionCount = 4096;
    public const int HardMaximumCollectionCount = 65534;

    // Native 0x6522C0 / 0x653900; sender 0x64BE88..0x64BECA.
    public static EdServerSessionRequest ReadRequestBody(ReadOnlySpan<byte> bytes, ref int bitOffset)
    {
        var p = bitOffset;
        var result = new EdServerSessionRequest(Read(bytes, ref p, 48), Read(bytes, ref p, 64));
        bitOffset = p;
        return result;
    }

    public static void WriteRequestBody(Span<byte> bytes, ref int bitOffset, EdServerSessionRequest message)
    {
        ArgumentNullException.ThrowIfNull(message);
        ValidateId(message.SessionId);
        Require(bytes.Length, bitOffset, 112);
        var p = bitOffset;
        Write(bytes, ref p, message.SessionId, 48);
        Write(bytes, ref p, message.InitialObjectIdBase, 64);
        bitOffset = p;
    }

    // Native 0x6528F0 reads only the common fixed-width session reference.
    public static EdServerSessionLeft ReadLeftBody(ReadOnlySpan<byte> bytes, ref int bitOffset)
    {
        var p = bitOffset;
        var result = new EdServerSessionLeft(Read(bytes, ref p, 48));
        bitOffset = p;
        return result;
    }

    public static void WriteLeftBody(Span<byte> bytes, ref int bitOffset, EdServerSessionLeft message)
    {
        ValidateId(message.SessionId);
        Write(bytes, ref bitOffset, message.SessionId, 48);
    }

    // Native 0x652AE0: session U48, initial base U64, compressed U32 with
    // width parameter 20 (five-bit exponent), then U48 machine references.
    public static EdServerSessionMembers ReadMembersBody(ReadOnlySpan<byte> bytes, ref int bitOffset,
        int maximumCount = DefaultMaximumCollectionCount)
    {
        ValidateMaximum(maximumCount);
        var p = bitOffset;
        var session = Read(bytes, ref p, 48);
        var initialBase = Read(bytes, ref p, 64);
        var count = ReadCount(bytes, ref p, 5, maximumCount);
        var ids = ReadIds(bytes, ref p, count);
        bitOffset = p;
        return new(session, initialBase, ids);
    }

    public static void WriteMembersBody(Span<byte> bytes, ref int bitOffset, EdServerSessionMembers message,
        int maximumCount = DefaultMaximumCollectionCount)
    {
        ArgumentNullException.ThrowIfNull(message);
        ValidateMaximum(maximumCount);
        ValidateId(message.SessionId);
        ValidateIds(message.MachineRunIds, maximumCount);
        Require(bytes.Length, bitOffset, 112L + CountBits(message.MachineRunIds.Count, 5) + message.MachineRunIds.Count * 48L);
        var p = bitOffset;
        Write(bytes, ref p, message.SessionId, 48);
        Write(bytes, ref p, message.InitialObjectIdBase, 64);
        WriteCount(bytes, ref p, message.MachineRunIds.Count, 5);
        WriteIds(bytes, ref p, message.MachineRunIds);
        bitOffset = p;
    }

    // Native 0x6523C0: common U48, two compressed U16 counts (four-bit
    // exponents), all first-count U48 objects, then all second-count U48 objects.
    public static EdServerSessionJoined ReadJoinedBody(ReadOnlySpan<byte> bytes, ref int bitOffset,
        int maximumCount = DefaultMaximumCollectionCount)
    {
        ValidateMaximum(maximumCount);
        var p = bitOffset;
        var session = Read(bytes, ref p, 48);
        var firstCount = ReadCount(bytes, ref p, 4, maximumCount);
        var secondCount = ReadCount(bytes, ref p, 4, maximumCount);
        if ((long)firstCount + secondCount > maximumCount)
            throw new InvalidDataException("The combined native session object count exceeds the configured bound.");
        Require(bytes.Length, p, ((long)firstCount + secondCount) * 48);
        var first = ReadIds(bytes, ref p, firstCount);
        var second = ReadIds(bytes, ref p, secondCount);
        bitOffset = p;
        return new(session, first, second);
    }

    public static void WriteJoinedBody(Span<byte> bytes, ref int bitOffset, EdServerSessionJoined message,
        int maximumCount = DefaultMaximumCollectionCount)
    {
        ArgumentNullException.ThrowIfNull(message);
        ValidateMaximum(maximumCount);
        ValidateId(message.SessionId);
        ValidateIds(message.FirstObjectIds, maximumCount);
        ValidateIds(message.SecondObjectIds, maximumCount);
        var total = (long)message.FirstObjectIds.Count + message.SecondObjectIds.Count;
        if (total > maximumCount)
            throw new ArgumentOutOfRangeException(nameof(message));
        Require(bytes.Length, bitOffset, 48L + CountBits(message.FirstObjectIds.Count, 4)
            + CountBits(message.SecondObjectIds.Count, 4) + total * 48);
        var p = bitOffset;
        Write(bytes, ref p, message.SessionId, 48);
        WriteCount(bytes, ref p, message.FirstObjectIds.Count, 4);
        WriteCount(bytes, ref p, message.SecondObjectIds.Count, 4);
        WriteIds(bytes, ref p, message.FirstObjectIds);
        WriteIds(bytes, ref p, message.SecondObjectIds);
        bitOffset = p;
    }

    private static int ReadCount(ReadOnlySpan<byte> bytes, ref int p, int exponentBits, int maximum)
    {
        var exponent = (int)Read(bytes, ref p, exponentBits);
        var low = Read(bytes, ref p, exponent);
        var value = (1UL << exponent) + low - 1;
        if (value > (ulong)maximum)
            throw new InvalidDataException("The native session collection exceeds the configured bound.");
        return (int)value;
    }

    private static void WriteCount(Span<byte> bytes, ref int p, int value, int exponentBits)
    {
        var exponent = Exponent(value);
        Write(bytes, ref p, (ulong)exponent, exponentBits);
        Write(bytes, ref p, (ulong)value + 1 - (1UL << exponent), exponent);
    }

    private static int CountBits(int value, int exponentBits) => exponentBits + Exponent(value);
    private static int Exponent(int value) => System.Numerics.BitOperations.Log2((uint)value + 1);

    private static ulong[] ReadIds(ReadOnlySpan<byte> bytes, ref int p, int count)
    {
        Require(bytes.Length, p, count * 48L);
        var ids = new ulong[count];
        for (var i = 0; i < count; i++) ids[i] = Read(bytes, ref p, 48);
        return ids;
    }

    private static void WriteIds(Span<byte> bytes, ref int p, IReadOnlyList<ulong> ids)
    {
        foreach (var id in ids) Write(bytes, ref p, id, 48);
    }

    private static void ValidateMaximum(int maximum)
    {
        if (maximum is < 0 or > HardMaximumCollectionCount)
            throw new ArgumentOutOfRangeException(nameof(maximum));
    }

    private static void ValidateIds(IReadOnlyList<ulong> ids, int maximum)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count > maximum) throw new ArgumentOutOfRangeException(nameof(ids));
        foreach (var id in ids) ValidateId(id);
    }

    private static void ValidateId(ulong id)
    {
        if (id > MaximumWireId) throw new ArgumentOutOfRangeException(nameof(id));
    }

    private static ulong Read(ReadOnlySpan<byte> bytes, ref int p, int count)
    {
        Require(bytes.Length, p, count);
        ulong value = 0;
        for (var i = 0; i < count; i++, p++) value |= (ulong)((bytes[p / 8] >> (p % 8)) & 1) << i;
        return value;
    }

    private static void Write(Span<byte> bytes, ref int p, ulong value, int count)
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
            throw new InvalidDataException("Native session message is truncated or has an invalid bit offset.");
    }
}
