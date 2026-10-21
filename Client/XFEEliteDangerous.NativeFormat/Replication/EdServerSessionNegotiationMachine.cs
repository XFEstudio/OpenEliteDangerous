namespace XFEEliteDangerous.NativeFormat.Replication;

public enum EdServerSessionPeerState { None = 0, Requested = 1, AwaitingJoined = 2, Joined = 3 }

/// <summary>An already existing, authorized local session; all membership and object sets are caller supplied.</summary>
public sealed record EdServerLocalSessionDefinition(ulong SessionId, ulong LocalMachineRunId,
    ulong InitialObjectIdBase, IReadOnlyList<ulong> MemberMachineRunIds,
    IReadOnlyList<ulong> FirstObjectIds, IReadOnlyList<ulong> SecondObjectIds);

/// <summary>The caller pins a peer and its previously allocated ordinary object-ID block before negotiation.</summary>
public readonly record struct EdServerSessionPeerDefinition(ulong MachineRunId, ulong InitialObjectIdBase);

public abstract record EdServerSessionOutbound(ulong DestinationMachineRunId)
{
    public abstract EdServerSessionLetter Letter { get; }
}

public sealed record EdServerSessionRequestOutbound(ulong DestinationMachineRunId, EdServerSessionRequest Message)
    : EdServerSessionOutbound(DestinationMachineRunId)
{
    public override EdServerSessionLetter Letter => EdServerSessionLetter.Request;
}

public sealed record EdServerSessionMembersOutbound(ulong DestinationMachineRunId, EdServerSessionMembers Message)
    : EdServerSessionOutbound(DestinationMachineRunId)
{
    public override EdServerSessionLetter Letter => EdServerSessionLetter.Members;
}

public sealed record EdServerSessionJoinedOutbound(ulong DestinationMachineRunId, EdServerSessionJoined Message)
    : EdServerSessionOutbound(DestinationMachineRunId)
{
    public override EdServerSessionLetter Letter => EdServerSessionLetter.Joined;
}

public sealed record EdServerSessionNegotiationResult(EdServerSessionPeerState State, bool StateChanged,
    bool IsDuplicate, IReadOnlyList<EdServerSessionOutbound> Outbound);

/// <summary>
/// Bounded SESRQ/MEM/JOINED negotiation for one pre-existing local session. Native
/// state transitions follow 0x64BDC0, 0x65BA80, 0x65C2C0 and 0x65BF60. Explicit
/// peer/base authorization and ordinary aligned blocks are private-server policy.
/// This component does not allocate a session, connect discovered machines, assign
/// object ownership, ACK reliable letters, or create a world. The caller must
/// authenticate the sender and finish transport sequencing before invoking it.
/// Outbound letters must be sent in order using the existing reliable transport.
/// </summary>
public sealed class EdServerSessionNegotiationMachine
{
    public const int DefaultMaximumPeers = 64;
    public const int HardMaximumPeers = 1024;
    private readonly object _gate = new();
    private readonly Dictionary<ulong, Peer> _peers = [];
    private readonly HashSet<ulong> _allowedMachines;
    private readonly EdServerSessionRequest _request;
    private readonly EdServerSessionMembers _members;
    private readonly EdServerSessionJoined _joined;
    private readonly int _maximumObjectReferences;

    public ulong SessionId { get; }
    public ulong LocalMachineRunId { get; }
    public bool WorldReady => false;
    public int PeerCount => _peers.Count;

    public EdServerSessionNegotiationMachine(EdServerLocalSessionDefinition existingLocalSession,
        IReadOnlyList<EdServerSessionPeerDefinition> authorizedPeers,
        int maximumPeers = DefaultMaximumPeers,
        int maximumObjectReferences = EdServerSessionMessageCodec.DefaultMaximumCollectionCount)
    {
        ArgumentNullException.ThrowIfNull(existingLocalSession);
        ArgumentNullException.ThrowIfNull(authorizedPeers);
        if (maximumPeers is < 1 or > HardMaximumPeers)
            throw new ArgumentOutOfRangeException(nameof(maximumPeers));
        if (maximumObjectReferences is < 0 or > EdServerSessionMessageCodec.HardMaximumCollectionCount)
            throw new ArgumentOutOfRangeException(nameof(maximumObjectReferences));
        if (authorizedPeers.Count > maximumPeers)
            throw new ArgumentOutOfRangeException(nameof(authorizedPeers));
        ValidateId(existingLocalSession.SessionId);
        ValidateId(existingLocalSession.LocalMachineRunId);
        ValidateBase(existingLocalSession.InitialObjectIdBase);
        SessionId = existingLocalSession.SessionId;
        LocalMachineRunId = existingLocalSession.LocalMachineRunId;
        _maximumObjectReferences = maximumObjectReferences;
        _allowedMachines = [LocalMachineRunId];
        var bases = new HashSet<ulong> { existingLocalSession.InitialObjectIdBase };
        foreach (var definition in authorizedPeers)
        {
            ValidateId(definition.MachineRunId);
            ValidateBase(definition.InitialObjectIdBase);
            if (!_allowedMachines.Add(definition.MachineRunId) || !bases.Add(definition.InitialObjectIdBase))
                throw new ArgumentException("Local peer IDs and allocated blocks must be distinct.", nameof(authorizedPeers));
            _peers.Add(definition.MachineRunId, new Peer(definition.InitialObjectIdBase));
        }
        var members = CopyMembers(existingLocalSession.MemberMachineRunIds);
        if (!members.Contains(LocalMachineRunId))
            throw new ArgumentException("The existing session must contain its local machine.", nameof(existingLocalSession));
        var (first, second) = CopyObjects(existingLocalSession.FirstObjectIds, existingLocalSession.SecondObjectIds);
        _request = new(SessionId, existingLocalSession.InitialObjectIdBase);
        _members = new(SessionId, existingLocalSession.InitialObjectIdBase, members);
        _joined = new(SessionId, first, second);
    }

    public EdServerSessionPeerState GetPeerState(ulong machineRunId)
    {
        lock (_gate) return RequirePeer(machineRunId).State;
    }

    /// <summary>An independent candidate for transactional transport admission. All copied collections are immutable.</summary>
    public EdServerSessionNegotiationMachine Fork()
    {
        lock(_gate)return new(this);
    }

    private EdServerSessionNegotiationMachine(EdServerSessionNegotiationMachine source)
    {
        SessionId=source.SessionId;LocalMachineRunId=source.LocalMachineRunId;
        _allowedMachines=new(source._allowedMachines);_request=source._request;_members=source._members;_joined=source._joined;
        _maximumObjectReferences=source._maximumObjectReferences;
        foreach(var (id,peer) in source._peers)_peers.Add(id,new(peer.InitialObjectIdBase)
            {State=peer.State,ReceivedMembers=peer.ReceivedMembers,FirstObjects=peer.FirstObjects,SecondObjects=peer.SecondObjects});
    }

    /// <summary>A repeated call while Requested retransmits the same request; it never resets a later state.</summary>
    public EdServerSessionNegotiationResult BeginJoin(ulong machineRunId)
    {
        lock (_gate)
        {
            var peer = RequirePeer(machineRunId);
            var previous = peer.State;
            if (previous >= EdServerSessionPeerState.AwaitingJoined)
                return Result(peer, false, true);
            peer.State = EdServerSessionPeerState.Requested;
            return Result(peer, previous != peer.State, previous == peer.State,
                new EdServerSessionRequestOutbound(machineRunId, _request));
        }
    }

    public EdServerSessionNegotiationResult ReceiveRequest(ulong senderMachineRunId, EdServerSessionRequest message)
    {
        ArgumentNullException.ThrowIfNull(message);
        lock (_gate)
        {
            var peer = RequireMessage(senderMachineRunId, message.SessionId, message.InitialObjectIdBase);
            if (peer.State >= EdServerSessionPeerState.AwaitingJoined)
                return Result(peer, false, true); // Native already-joining branch, not a new join.
            peer.State = EdServerSessionPeerState.AwaitingJoined;
            return Result(peer, true, false,
                new EdServerSessionMembersOutbound(senderMachineRunId, _members),
                new EdServerSessionJoinedOutbound(senderMachineRunId, _joined));
        }
    }

    public EdServerSessionNegotiationResult ReceiveMembers(ulong senderMachineRunId, EdServerSessionMembers message)
    {
        ArgumentNullException.ThrowIfNull(message);
        lock (_gate)
        {
            var peer = RequireMessage(senderMachineRunId, message.SessionId, message.InitialObjectIdBase);
            if (peer.State == EdServerSessionPeerState.None)
                throw new InvalidDataException("SESMEM requires a pending or established peer exchange.");
            // Validation and bounded snapshots precede every mutation. Listed peers
            // are references only; no remote connection or state is created here.
            var members = CopyMembers(message.MachineRunIds);
            var duplicate = peer.ReceivedMembers is not null && peer.ReceivedMembers.SequenceEqual(members);
            var changed = peer.State == EdServerSessionPeerState.Requested;
            peer.ReceivedMembers = members;
            if (changed) peer.State = EdServerSessionPeerState.AwaitingJoined;
            return Result(peer, changed, duplicate, new EdServerSessionJoinedOutbound(senderMachineRunId, _joined));
        }
    }

    public EdServerSessionNegotiationResult ReceiveJoined(ulong senderMachineRunId, EdServerSessionJoined message)
    {
        ArgumentNullException.ThrowIfNull(message);
        lock (_gate)
        {
            var peer = RequireMessage(senderMachineRunId, message.SessionId);
            if (peer.State < EdServerSessionPeerState.AwaitingJoined)
                throw new InvalidDataException("SESJOINED requires a peer awaiting the joined message.");
            var (first, second) = CopyObjects(message.FirstObjectIds, message.SecondObjectIds);
            if (peer.State == EdServerSessionPeerState.Joined)
            {
                if (!peer.FirstObjects!.SequenceEqual(first) || !peer.SecondObjects!.SequenceEqual(second))
                    throw new InvalidDataException("A duplicate SESJOINED cannot replace the accepted object-reference sets.");
                return Result(peer, false, true);
            }
            peer.FirstObjects = first;
            peer.SecondObjects = second;
            peer.State = EdServerSessionPeerState.Joined;
            return Result(peer, true, false);
        }
    }

    private Peer RequireMessage(ulong sender, ulong session, ulong? initialBase = null)
    {
        var peer = RequirePeer(sender);
        if (session != SessionId || initialBase.HasValue && initialBase.Value != peer.InitialObjectIdBase)
            throw new InvalidDataException("The session or pinned peer object-ID base does not match this local definition.");
        return peer;
    }

    private Peer RequirePeer(ulong machineRunId) => _peers.TryGetValue(machineRunId, out var peer)
        ? peer : throw new InvalidDataException("The sender is not an explicitly authorized peer of this local session.");

    private IReadOnlyList<ulong> CopyMembers(IReadOnlyList<ulong> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count > _allowedMachines.Count)
            throw new InvalidDataException("The session machine collection exceeds the configured peer bound.");
        var result = new ulong[ids.Count];
        var distinct = new HashSet<ulong>();
        for (var i = 0; i < result.Length; i++)
        {
            var id = ids[i];
            if (!_allowedMachines.Contains(id) || !distinct.Add(id))
                throw new InvalidDataException("The session machine collection contains an unauthorized or duplicate reference.");
            result[i] = id;
        }
        return Array.AsReadOnly(result);
    }

    private (IReadOnlyList<ulong>, IReadOnlyList<ulong>) CopyObjects(IReadOnlyList<ulong> first, IReadOnlyList<ulong> second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        if ((long)first.Count + second.Count > _maximumObjectReferences)
            throw new InvalidDataException("The combined object-reference count exceeds the configured bound.");
        return (CopyIds(first), CopyIds(second));
    }

    private static IReadOnlyList<ulong> CopyIds(IReadOnlyList<ulong> source)
    {
        var result = new ulong[source.Count];
        for (var i = 0; i < result.Length; i++) { ValidateId(source[i]); result[i] = source[i]; }
        return Array.AsReadOnly(result);
    }

    private static void ValidateId(ulong id)
    {
        if (id == 0 || id > EdServerSessionMessageCodec.MaximumWireId)
            throw new InvalidDataException("Session, machine and object references must be nonzero 48-bit IDs.");
    }

    private static void ValidateBase(ulong initialBase)
    {
        if (initialBase == 0 || initialBase % NativeObjectIdAllocator.BlockSize != 0
            || initialBase >= NativeObjectIdAllocator.ReservedStart)
            throw new ArgumentOutOfRangeException(nameof(initialBase), "A previously allocated ordinary aligned object-ID block is required.");
    }

    private static EdServerSessionNegotiationResult Result(Peer peer, bool changed, bool duplicate,
        params EdServerSessionOutbound[] outbound) => new(peer.State, changed, duplicate, Array.AsReadOnly(outbound));

    private sealed class Peer(ulong initialObjectIdBase)
    {
        public ulong InitialObjectIdBase { get; } = initialObjectIdBase;
        public EdServerSessionPeerState State { get; set; }
        public IReadOnlyList<ulong>? ReceivedMembers { get; set; }
        public IReadOnlyList<ulong>? FirstObjects { get; set; }
        public IReadOnlyList<ulong>? SecondObjects { get; set; }
    }
}
