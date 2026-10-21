using System.Numerics;
using System.Text;

namespace XFEEliteDangerous.NativeFormat.Replication;

/// <summary>A nonzero remote factory identifier, with an optional first-use name definition.</summary>
public sealed record EdServerObjectFactory(ushort Identifier, string? DefinitionName = null);

/// <summary>
/// The ordinary leaf-object subset of native +OBJ. References are raw U48 values;
/// allocation, ownership, factory availability and live state require separate policy.
/// Dependencies here are replication-session references (653900), not parent objects.
/// Cloned objects and nested construction are deliberately outside this subset.
/// </summary>
public sealed record EdServerLeafObjectCreate(ulong ObjectReference, ulong ParentObjectReference, bool GoLiveWhenReady,
    EdServerObjectFactory Factory, ushort Version, IReadOnlyList<ulong> Dependencies, ulong? OwnerMachineReference = null);

/// <summary>
/// Statically closed leaf layout from650060,64D1C0,653AB0 and53BDB0 in the pinned
/// client. It describes construction only: it does not supply reflected properties,
/// ServerAdmin behaviour, a world or a successful native-live transition.
/// </summary>
public static class EdServerObjectCreateCodec
{
    public const byte LetterType = 17;
    public const int MaximumFactoryNameBytes = 128;
    public const int MaximumDependencies = 64;
    // Native sender7FBD30 maps its configured client-root MCO to signed-255;
    // receiver7FBCE0 reverses that mapping. The wire carries the low48 bits.
    public const ulong ClientRootReference = 0xFFFFFFFFFF01;
    private const ulong MaximumReference = 0xFFFFFFFFFFFF;

    public static EdServerLeafObjectCreate ReadLeafBody(ReadOnlySpan<byte> source, ref int bitOffset)
    {
        var p = bitOffset;
        var reference = Read(source, ref p, 48);
        var parent = Read(source, ref p, 48);
        var goLive = Read(source, ref p, 1) != 0;
        var identifier = Compressed(source, ref p, 4, ushort.MaxValue - 1);
        string? name = null;
        if (identifier == 0)
        {
            identifier = Compressed(source, ref p, 4, ushort.MaxValue - 1);
            if (identifier == 0) throw new InvalidDataException("Factory zero is a reserved definition marker.");
            var length = Read(source, ref p, 32);
            if (length is < 1 or > MaximumFactoryNameBytes) throw new InvalidDataException("Factory name exceeds the local bound.");
            Require(source.Length, p, checked((long)length * 8));
            Span<byte> bytes = stackalloc byte[(int)length];
            for (var i = 0; i < bytes.Length; i++)
            {
                bytes[i] = (byte)Read(source, ref p, 8);
                if (bytes[i] is < 32 or > 126) throw new InvalidDataException("Unsupported factory-name encoding.");
            }
            name = Encoding.ASCII.GetString(bytes);
        }
        var version = (ushort)Read(source, ref p, 16);
        if (Read(source, ref p, 1) != 0) throw new NotSupportedException("Cloned native object construction is not implemented.");
        var count = Compressed(source, ref p, 5, MaximumDependencies);
        Require(source.Length, p, count * 48L);
        var dependencies = new ulong[count];
        for (var i = 0; i < count; i++) dependencies[i] = Read(source, ref p, 48);
        ulong? owner = Read(source, ref p, 1) == 0 ? null : Read(source, ref p, 48);
        if (Compressed(source, ref p, 5, 4096) != 0) throw new NotSupportedException("Nested native object construction is not implemented.");
        bitOffset = p;
        return new(reference, parent, goLive, new((ushort)identifier, name), version, Array.AsReadOnly(dependencies), owner);
    }

    public static int GetLeafBodyBitLength(EdServerLeafObjectCreate message)
    {
        var snapshot = Snapshot(message);
        return Bits(message, snapshot.Name, snapshot.Dependencies);
    }

    public static void WriteLeafBody(Span<byte> destination, ref int bitOffset, EdServerLeafObjectCreate message)
    {
        var snapshot = Snapshot(message);
        Require(destination.Length, bitOffset, Bits(message, snapshot.Name, snapshot.Dependencies));
        var p = bitOffset;
        Write(destination, ref p, message.ObjectReference, 48);
        Write(destination, ref p, message.ParentObjectReference, 48);
        Write(destination, ref p, message.GoLiveWhenReady ? 1UL : 0, 1);
        if (snapshot.Name is not null)
        {
            WriteCompressed(destination, ref p, 0, 4);
            WriteCompressed(destination, ref p, message.Factory.Identifier, 4);
            Write(destination, ref p, (uint)snapshot.Name.Length, 32);
            foreach (var value in snapshot.Name) Write(destination, ref p, value, 8);
        }
        else WriteCompressed(destination, ref p, message.Factory.Identifier, 4);
        Write(destination, ref p, message.Version, 16);
        Write(destination, ref p, 0, 1); // No existing-object/clone reference.
        WriteCompressed(destination, ref p, snapshot.Dependencies.Length, 5);
        foreach (var dependency in snapshot.Dependencies) Write(destination, ref p, dependency, 48);
        Write(destination, ref p, message.OwnerMachineReference.HasValue ? 1UL : 0, 1);
        if (message.OwnerMachineReference is ulong owner) Write(destination, ref p, owner, 48);
        WriteCompressed(destination, ref p, 0, 5); // This subset has no nested children.
        bitOffset = p;
    }

    private static (byte[]? Name, ulong[] Dependencies) Snapshot(EdServerLeafObjectCreate message)
    {
        ArgumentNullException.ThrowIfNull(message); ArgumentNullException.ThrowIfNull(message.Factory);
        ArgumentNullException.ThrowIfNull(message.Dependencies);
        Reference(message.ObjectReference); Reference(message.ParentObjectReference);
        if (message.OwnerMachineReference.HasValue) Reference(message.OwnerMachineReference.Value);
        if (message.Factory.Identifier is 0 or ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(message));
        byte[]? name = null;
        if (message.Factory.DefinitionName is string text)
        {
            if (text.Length is < 1 or > MaximumFactoryNameBytes || text.Any(value => value is < ' ' or > '~'))
                throw new ArgumentException("Unsupported bounded native factory name.", nameof(message));
            name = Encoding.ASCII.GetBytes(text);
        }
        if (message.Dependencies.Count > MaximumDependencies) throw new ArgumentOutOfRangeException(nameof(message));
        var dependencies = message.Dependencies.ToArray();
        if (dependencies.Length > MaximumDependencies) throw new ArgumentOutOfRangeException(nameof(message));
        foreach (var dependency in dependencies) Reference(dependency);
        return (name, dependencies);
    }

    private static int Bits(EdServerLeafObjectCreate message, byte[]? name, ulong[] dependencies)
        => 48 + 48 + 1 + CountBits(message.Factory.Identifier, 4) + (name is null ? 0 : 4 + 32 + name.Length * 8)
           + 16 + 1 + CountBits(dependencies.Length, 5) + dependencies.Length * 48 + 1
           + (message.OwnerMachineReference.HasValue ? 48 : 0) + 5;
    private static int CountBits(int value, int exponentBits) => exponentBits + BitOperations.Log2((uint)value + 1);
    private static void Reference(ulong value)
    { if (value > MaximumReference) throw new ArgumentOutOfRangeException(nameof(value)); }
    private static int Compressed(ReadOnlySpan<byte> source, ref int p, int exponentBits, int maximum)
    {
        var exponent = (int)Read(source, ref p, exponentBits);
        var value = (1UL << exponent) + Read(source, ref p, exponent) - 1;
        if (value > (uint)maximum) throw new InvalidDataException("Compressed native value exceeds the local bound.");
        return (int)value;
    }
    private static void WriteCompressed(Span<byte> destination, ref int p, int value, int exponentBits)
    {
        var exponent = BitOperations.Log2((uint)value + 1);
        Write(destination, ref p, (uint)exponent, exponentBits);
        Write(destination, ref p, (ulong)value + 1 - (1UL << exponent), exponent);
    }
    private static ulong Read(ReadOnlySpan<byte> source, ref int p, int count)
    {
        Require(source.Length, p, count); ulong value = 0;
        for (var i = 0; i < count; i++, p++) value |= (ulong)((source[p / 8] >> (p % 8)) & 1) << i;
        return value;
    }
    private static void Write(Span<byte> destination, ref int p, ulong value, int count)
    {
        Require(destination.Length, p, count);
        for (var i = 0; i < count; i++, p++)
        {
            var mask = 1 << (p % 8);
            destination[p / 8] = (byte)((destination[p / 8] & ~mask) | (((value >> i) & 1) == 0 ? 0 : mask));
        }
    }
    private static void Require(int byteCount, int bitOffset, long count)
    {
        if (bitOffset < 0 || count < 0 || (long)bitOffset + count > (long)byteCount * 8)
            throw new InvalidDataException("Native object creation is truncated or has an invalid bit offset.");
    }
}
