namespace XFEEliteDangerous.NativeFormat.Replication;

/// <summary>
/// Local private-server allocation policy, not the original service's allocation
/// algorithm. One instance belongs to one server process; all launch/session keys
/// share its increasing ID space. IDs are never reclaimed in that lifetime.
/// The caller must authenticate and deduplicate reliable requests before allocation.
/// </summary>
public sealed class NativeObjectIdAllocator
{
    public const ulong BlockSize = 4096;
    // +OBJ reader650060 and factory activation6426B0 accept ordinary replicated
    // references starting at2^33. Lower ranges have native special/local semantics.
    // The generic allocator can still model raw ranges for codec fixtures.
    public const ulong FirstReplicatedObjectBase = 0x200000000;
    public const ulong ReservedStart = 0xFFFFFFFF0000;
    private readonly object _gate = new();
    private readonly Dictionary<string, ulong> _sessionCounts = new(StringComparer.Ordinal);
    private readonly ulong _exclusiveLimit;
    private ulong _nextBase;
    private ulong _allocatedBlockCount;

    public NativeObjectIdAllocator(ulong firstBase = BlockSize, ulong exclusiveLimit = ReservedStart)
    {
        if (firstBase == 0 || firstBase % BlockSize != 0 || firstBase >= ReservedStart)
            throw new ArgumentOutOfRangeException(nameof(firstBase));
        if (exclusiveLimit > ReservedStart || exclusiveLimit % BlockSize != 0
            || exclusiveLimit <= firstBase || exclusiveLimit - firstBase < BlockSize)
            throw new ArgumentOutOfRangeException(nameof(exclusiveLimit));
        (_nextBase, _exclusiveLimit) = (firstBase, exclusiveLimit);
    }

    public ulong AllocatedBlockCount { get { lock (_gate) return _allocatedBlockCount; } }

    /// <param name="launchSessionKey">Local launch/session identifier, never an official ticket or password.</param>
    public NativeObjectIdBlock AllocateBlock(string launchSessionKey)
    {
        ArgumentNullException.ThrowIfNull(launchSessionKey);
        if (string.IsNullOrWhiteSpace(launchSessionKey) || launchSessionKey.Length > 128
            || launchSessionKey.Any(char.IsControl))
            throw new ArgumentException("A bounded local launch/session identifier is required.", nameof(launchSessionKey));
        lock (_gate)
        {
            if (_nextBase > _exclusiveLimit - BlockSize)
                throw new InvalidOperationException("The configured ordinary object-ID range is exhausted.");
            _sessionCounts.TryGetValue(launchSessionKey, out var count);
            var allocation = new NativeObjectIdBlock(_nextBase, count);
            _nextBase += BlockSize;
            _sessionCounts[launchSessionKey] = count + 1;
            _allocatedBlockCount++;
            return allocation;
        }
    }

    public NativeObjectIdBlock AllocateBlock(Guid launchId)
    {
        if (launchId == Guid.Empty)
            throw new ArgumentException("The local launch identifier cannot be empty.", nameof(launchId));
        return AllocateBlock(launchId.ToString("N"));
    }
}

/// <summary>One private-server block. SessionBlockIndex starts at zero independently for each session.</summary>
public readonly record struct NativeObjectIdBlock(ulong Base, ulong SessionBlockIndex)
{
    public ulong Count => NativeObjectIdAllocator.BlockSize;
    public ulong Last => checked(Base + Count - 1);
}
