using System.Net;
using System.Security.Cryptography;
using XFEEliteDangerous.NativeFormat.Replication;
using XFEEliteDangerous.Client;
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("XFEEliteDangerous.Tests")]
namespace XFEEliteDangerous.Core;

// The owning peer gate serializes this object. Parse the entire datagram before
// ACK/allocation mutation. SEQ identifies one reliable group, whose multiple
// type1 letters share that sequence until the next explicit SEQ. Native
// 4C37820 writes stream+84 only in the SEQ reader;4C35183..4C355B9 dispatches
// subsequent reliable letters using that classification without incrementing it.
internal sealed class LocalReplicationSession : IDisposable
{
    internal const ushort Protocol = 33837;
    internal const int OutgoingTypeBits = 16;
    private readonly IPEndPoint remote;
    private readonly NativeObjectIdAllocator allocator;
    private readonly string launchId;
    private readonly TimeProvider clock;
    private readonly bool initiateMtuProbe;
    private LocalReplicationApplication? application;
    private readonly Func<ulong,EdServerIp4Endpoint?>? resolveRoute;
    private EdServerHandshakeMachine? machine;
    private EdServerReliableOutbox? outbox;
    private ushort counter, receivedSequence;
    private long lastMtuProbe;
    private int mtuProbeAttempts;
    private readonly Dictionary<ushort, Replay> replays = [];
    private readonly Queue<ushort> replayOrder = [];
    private readonly Dictionary<ushort, long> seenTypes = [];
    private readonly Queue<object> recentControls = [];
    private readonly Dictionary<string,long> controlFailureCounts=new(StringComparer.Ordinal);
    private List<object> currentTags = [];
    internal bool Closed { get; private set; }
    internal EdServerHandshakeState State => machine?.State ?? EdServerHandshakeState.Disconnected;
    internal int IncomingTypeBits => machine?.IncomingLetterTypeBits ?? EdServerControlMessageCodec.InitialLetterTypeBits;
    internal bool IncomingLengthDelimited => machine?.IncomingLengthDelimited ?? false;
    internal byte IncomingAlignmentMode => machine?.IncomingAlignmentMode ?? 0;
    internal DateTimeOffset LastSeen { get; set; }
    internal ushort? EstablishedMtu { get; private set; }
    internal ulong? InitialObjectIdBase { get; private set; }
    internal int PendingReliable => outbox?.PendingCount ?? 0;
    internal long UnsupportedLetters { get; private set; }
    internal long ControlErrors { get; private set; }
    internal int MtuProbeAttempts => mtuProbeAttempts;
    internal IReadOnlyDictionary<ushort, long> SeenTypes => new Dictionary<ushort,long>(seenTypes);
    internal IReadOnlyList<object> RecentControls => recentControls.ToArray();
    internal IReadOnlyDictionary<string,long> ControlFailureCounts=>new Dictionary<string,long>(controlFailureCounts,StringComparer.Ordinal);
    internal string LastControlFailure { get; private set; } = "";
    internal ushort? LastMtuReplyCandidate { get; private set; }
    internal int LastReliableDistance { get; private set; }
    internal int AcceptedRoutes { get; private set; }
    internal int TransportRestarts {get;private set;}
    internal long HeartbeatPackets {get;private set;}
    private long lastOutputTimestamp;
    internal object? RouteSchemaFailure{get;private set;}
    internal object? LastCallEnvelopeShape{get;private set;}
    private readonly Queue<object> recentCallShapes=[];
    internal IReadOnlyList<object> RecentCallShapes=>recentCallShapes.ToArray();
    private readonly List<object> objectStateShapes=[];
    internal IReadOnlyList<object> ObjectStateShapes=>objectStateShapes.ToArray();
    internal object? ApplicationSnapshot=>application?.Snapshot();
    internal string StateSummary=>$"{State}\tresets={TransportRestarts}\tallocation={InitialObjectIdBase.HasValue}\tstarts={application?.AcceptedStarts??0}\tmember={application?.PeerState.ToString()??"None"}";
    internal LocalReplicationSession(IPEndPoint remote, NativeObjectIdAllocator allocator, string launchId, TimeProvider? clock = null, bool initiateMtuProbe = false,LocalReplicationApplication? application=null,Func<ulong,EdServerIp4Endpoint?>? resolveRoute=null)
        => (this.remote, this.allocator, this.launchId, this.clock, this.initiateMtuProbe,this.application,this.resolveRoute) = (remote, allocator, launchId, clock ?? TimeProvider.System, initiateMtuProbe,application,resolveRoute);
    internal ushort NextPacketCounter() => counter = counter == EdServerDatagramHeader.MaximumCounter ? (ushort)1 : (ushort)(counter + 1);
    // Both ServerAdmin connections belong to the same authenticated native
    // manager. Its allocator is created once (62C096), outside either channel.
    // Only the owning in-process authority may propagate its first issued block.
    internal void BindClientAllocator(ulong firstBase)
    {
        if(firstBase<NativeObjectIdAllocator.FirstReplicatedObjectBase || firstBase>=NativeObjectIdAllocator.ReservedStart
            || firstBase%NativeObjectIdAllocator.BlockSize!=0 || InitialObjectIdBase is {} existing && existing!=firstBase)
            throw new InvalidDataException("local-client-allocation-mismatch");
        InitialObjectIdBase??=firstBase;
    }

    internal IReadOnlyList<byte[]> Receive(ReadOnlyMemory<byte> letters, bool compressed,ulong? selectedSystemAddress=null)
    {
        var replies=ReceiveCore(letters,compressed,selectedSystemAddress);
        if(replies.Count!=0)lastOutputTimestamp=clock.GetTimestamp();
        return replies;
    }

    private IReadOnlyList<byte[]> ReceiveCore(ReadOnlyMemory<byte> letters, bool compressed,ulong? selectedSystemAddress)
    {
        if (Closed) return [];
        if (compressed) { UnsupportedLetters++;OfflineDiagnostics.Current?.Write("warning","protocol.replication","packet.compressed.unsupported","Compressed letter stream is unsupported.");return []; }
        var receiveUtc=clock.GetUtcNow();
        currentTags = []; LastControlFailure = ""; LastMtuReplyCandidate = null; LastReliableDistance = -2;
        try
        {
            using var packet = Parse(letters.Span);
            LastReliableDistance = packet.Groups.Count == 0 ? -1 : Distance(receivedSequence, packet.Groups[0].Sequence);
            if (packet.Sync is { } sync) return ReceiveSync(sync);
            if (State == EdServerHandshakeState.AwaitingAcknowledgement && packet.Ack is { } initial)
            {
                if (!CanAcceptAck(initial) || !machine!.ReceiveAcknowledgement(initial)) throw new InvalidDataException("initial-ack-mismatch");
                // Native ACK can share a packet with the first reliable group,
                // especially on the second ServerAdmin connection/reconnect.
                // Parse() already validated every letter boundary. Admit the
                // valid transport ACK and dispatch the remaining authenticated
                // commands through their ordinary transactional checks.
                if(packet.Groups.Count==0 && packet.MtuTests.Count==0 && packet.UnreliableApplications.Count==0)
                    return InitialMtuProbe();
            }
            if (State != EdServerHandshakeState.Established) throw new InvalidDataException("transport-not-established");
            if (packet.Ack is { } ack && !CanAcceptAck(ack)) throw new InvalidDataException("ack-outside-sent-span");
            outbox!.VerifyActive();
            var identities = new List<byte[]>(); var accepted = new List<Group>(); var duplicates = new List<Replay>();
            var prospective = receivedSequence;
            try
            {
                foreach (var group in packet.Groups)
                {
                    var identity = Identity(group); identities.Add(identity);
                    if (replays.TryGetValue(group.Sequence, out var replay))
                    {
                        if (!CryptographicOperations.FixedTimeEquals(identity, replay.Identity)) throw new InvalidDataException("changed-reliable-duplicate");
                        duplicates.Add(replay); continue;
                    }
                    if (group.Sequence != EdServerHandshakeMachine.Next(prospective)) throw new NotSupportedException("receive-window-required");
                    if (group.Commands.Any(command => command.Type == (byte)EdServerBuiltInLetter.MtuReply && initiateMtuProbe && (mtuProbeAttempts == 0 || command.Mtu != 1360)))
                        throw new InvalidDataException("mtu-value-mismatch");
                    prospective = group.Sequence; accepted.Add(group);
                }
                var applicationCommands=accepted.SelectMany(group=>group.Commands).Where(command=>command.Application is not null).Select(command=>command.Application!)
                    .Concat(packet.UnreliableApplications).ToArray();
                if(applicationCommands.Length>0&&application is null)throw new NotSupportedException("unsupported-letter-type");
                using var applicationPlan=applicationCommands.Length==0?null:application!.Prepare(applicationCommands,InitialObjectIdBase,
                    accepted.SelectMany(group=>group.Commands).LastOrDefault(command=>command.Mtu.HasValue)?.Mtu??EstablishedMtu,
                    EdServerNetworkTime.FromUtc(receiveUtc),
                    EdServerNetworkTime.FromUtc(clock.GetUtcNow()),selectedSystemAddress);
                var routeReplies=new Dictionary<Command,byte[]>();
                foreach(var command in accepted.SelectMany(group=>group.Commands).Where(command=>command.Route?.IsRequest==true))
                {
                    var route=command.Route!;var endpoint=resolveRoute?.Invoke(route.MachineRunId);
                    OfflineDiagnostics.Current?.Write("debug","protocol.route","route.lookup","Native route request resolved against the private instance directory.",
                        new Dictionary<string,string?>{["requestedMachine"]=route.MachineRunId.ToString(),["available"]=endpoint.HasValue.ToString()});
                    if(endpoint is {} available)routeReplies.Add(command,EdServerRouteMessageCodec.EncodeIp4Update(route.MachineRunId,available));
                }
                var replyCount = packet.MtuTests.Count + routeReplies.Count + accepted.Sum(group => group.Commands.Count(value => value.Type == (byte)EdServerBuiltInLetter.IdAllocationRequest))+(applicationPlan?.Emissions.Count(value=>value.Reliable)??0);
                if (replyCount > 0 && !CanQueue(replyCount, packet.Ack)) throw new NotSupportedException("send-window-full");
                if (packet.Ack is { } acceptedAck && !outbox.TryAcknowledge(acceptedAck)) throw new InvalidDataException("ack-rejected");
                var payloads = new List<(Group? Owner, byte[] Bytes)>(); var replies = new List<byte[]>();
                var cache = new Dictionary<Group, List<byte[]>>();
                foreach (var group in accepted) cache.Add(group, []);
                try
                {
                    foreach (var mtu in packet.MtuTests)
                    {
                        var payload = new byte[4]; var p = 0;
                        EdServerControlMessageCodec.WriteLetterType(payload, ref p, (byte)EdServerBuiltInLetter.MtuReply, OutgoingTypeBits);
                        EdServerMtuMessageCodec.WriteReply(payload, ref p, mtu); payloads.Add((null, payload));
                    }
                    foreach (var group in accepted)
                    foreach (var command in group.Commands)
                    {
                        if(routeReplies.TryGetValue(command,out var routeReply))payloads.Add((group,routeReply));
                        if(command.Type==(byte)EdServerBuiltInLetter.IdAllocationRequest)
                        {
                            var block = allocator.AllocateBlock(launchId); InitialObjectIdBase ??= block.Base;
                            payloads.Add((group, EdServerControlMessageCodec.EncodeAllocatedIdLetter(block.Base, OutgoingTypeBits)));
                        }
                        if(command.Application is {} app&&applicationPlan is not null)
                            foreach(var emission in applicationPlan.Emissions.Where(value=>ReferenceEquals(value.Owner,app)))
                                if(emission.Reliable)payloads.Add((group,emission.Payload.ToArray()));
                                else{var wire=emission.Payload.ToArray();replies.Add(wire);cache[group].Add(wire.ToArray());}
                    }
                    if(applicationPlan is not null)
                        foreach(var emission in applicationPlan.Emissions.Where(value=>packet.UnreliableApplications.Contains(value.Owner)))
                            if(emission.Reliable)payloads.Add((null,emission.Payload.ToArray()));else replies.Add(emission.Payload.ToArray());
                    if (payloads.Count > 0)
                    {
                        if (!outbox.TryEnqueueBatch(payloads.Select(value => (ReadOnlyMemory<byte>)value.Bytes).ToArray(), out var queued))
                            throw new InvalidOperationException("Control queue changed after preflight.");
                        for (var index = 0; index < queued.Count; index++)
                        {
                            using var transmission = queued[index]; var wire = EncodeTransmission(transmission); replies.Add(wire);
                            if (payloads[index].Owner is { } owner) cache[owner].Add(wire.ToArray());
                        }
                    }
                    applicationPlan?.Commit();
                    foreach (var replay in duplicates) replies.AddRange(replay.Replies.Select(value => value.ToArray()));
                    foreach (var group in accepted)
                    {
                        foreach (var command in group.Commands)
                        {
                            if (command.Type == (byte)EdServerBuiltInLetter.MtuReply) EstablishedMtu = command.Mtu;
                            if (command.Type == (byte)EdServerBuiltInLetter.Route) AcceptedRoutes++;
                        }
                        AddReplay(group.Sequence, new(Identity(group), cache[group].ToArray())); cache.Remove(group);
                        receivedSequence = group.Sequence;
                    }
                    if (packet.Groups.Count > 0) replies.Add(EdServerControlMessageCodec.EncodeAckLetter(new(receivedSequence, 0, 16), OutgoingTypeBits));
                    return replies;
                }
                catch { foreach (var reply in replies) CryptographicOperations.ZeroMemory(reply); throw; }
                finally
                {
                    foreach (var payload in payloads) CryptographicOperations.ZeroMemory(payload.Bytes);
                    foreach (var pending in cache.Values) foreach (var bytes in pending) CryptographicOperations.ZeroMemory(bytes);
                }
            }
            finally { foreach (var identity in identities) CryptographicOperations.ZeroMemory(identity); }
        }
        catch (NotSupportedException error) { UnsupportedLetters++; LastControlFailure = Label(error);LogFailure(error);if(error is EdServerRouteSchemaException schema)RouteSchemaFailure=new{schema.Kind,schema.NameBytes,schema.Depth}; }
        catch (Exception error) when (error is InvalidDataException or ArgumentException) { ControlErrors++; LastControlFailure = Label(error);LogFailure(error); }
        catch (InvalidOperationException error) { ControlErrors++; LastControlFailure = "control-commit-failed";LogFailure(error); Revoke(); }
        finally
        {
            if(LastControlFailure.Length!=0&&(controlFailureCounts.Count<64||controlFailureCounts.ContainsKey(LastControlFailure)))
                controlFailureCounts[LastControlFailure]=controlFailureCounts.GetValueOrDefault(LastControlFailure)+1;
            recentControls.Enqueue(new { bytes = letters.Length, outcome = LastControlFailure.Length == 0 ? "accepted" : LastControlFailure,
                reliableDistance = LastReliableDistance, mtu = LastMtuReplyCandidate, tags = currentTags.ToArray() });
            while (recentControls.Count > 24) recentControls.Dequeue();
        }
        return [];
    }

    internal IReadOnlyList<byte[]> ClaimDueRetransmissions()
    {
        if (Closed || outbox is null) return [];
        try
        {
            var replies = new List<byte[]>(); replies.AddRange(InitialMtuProbe());
            foreach (var transmission in outbox.ClaimDueRetransmissions()) using (transmission) replies.Add(EncodeTransmission(transmission));
            // Native4C1B140 ages the send-idle timer+1FC and schedules a
            // datagram while connected even when no application is sending.
            // Retain the acknowledged receive position with a small periodic
            // ACK, rather than leaving an established private peer silent.
            // This consumes no reliable sequence and claims no world state.
            if(replies.Count==0&&application is not null&&State==EdServerHandshakeState.Established
                &&clock.GetElapsedTime(lastOutputTimestamp)>=TimeSpan.FromSeconds(1))
            {
                replies.Add(EdServerControlMessageCodec.EncodeAckLetter(new(receivedSequence,0,16),OutgoingTypeBits));
                HeartbeatPackets++;
            }
            if(replies.Count!=0)lastOutputTimestamp=clock.GetTimestamp();
            return replies;
        }
        catch (InvalidOperationException error)
        {
            ControlErrors++; LastControlFailure = "reliable-delivery-" + outbox.State;
            LogFailure(error);
            Revoke(); return [];
        }
    }
    private void LogFailure(Exception error,[System.Runtime.CompilerServices.CallerFilePath]string file="",
        [System.Runtime.CompilerServices.CallerLineNumber]int line=0,[System.Runtime.CompilerServices.CallerMemberName]string member="")
    {
        OfflineDiagnostics.Current?.Write("error","protocol.replication","replication.rejected",LastControlFailure,
            new Dictionary<string,string?>{["sessionRole"]=application?.RoleName??"unknown",["state"]=StateSummary,
                ["reliableDistance"]=LastReliableDistance.ToString(),["pendingReliable"]=PendingReliable.ToString()},
            error,file,line,member);
    }
    private IReadOnlyList<byte[]> InitialMtuProbe()
    {
        if (!initiateMtuProbe || State != EdServerHandshakeState.Established || EstablishedMtu.HasValue || mtuProbeAttempts >= 5
            || mtuProbeAttempts > 0 && clock.GetElapsedTime(lastMtuProbe) < TimeSpan.FromSeconds(1)) return [];
        var bytes = new byte[1350]; var p = 0;
        EdServerControlMessageCodec.WriteLetterType(bytes, ref p, (byte)EdServerBuiltInLetter.MtuTest, OutgoingTypeBits);
        EdServerMtuMessageCodec.WriteTest(bytes, ref p, 1360, new byte[1344]); mtuProbeAttempts++; lastMtuProbe = clock.GetTimestamp(); return [bytes];
    }
    private IReadOnlyList<byte[]> ReceiveSync(EdServerSyncMessage sync)
    {
        if (machine is null)
        {
            if (sync.Acknowledgement != 0) throw new InvalidDataException("first-sync-is-not-hard");
            var ip = remote.Address.GetAddressBytes(); var observed = ((uint)ip[0] << 24) | ((uint)ip[1] << 16) | ((uint)ip[2] << 8) | ip[3];
            var parameters = sync with { IsClient = false, LetterTypeBits = OutgoingTypeBits, CompressionVersion = 0, LengthDelimited = false, AlignmentMode = 0,
                EncryptedKeyBytes = ReadOnlyMemory<byte>.Empty, NameBytes = "XFEEliteDangerous"u8.ToArray(), ObservedAddress = observed, ObservedPort = checked((ushort)remote.Port) };
            var candidate = new EdServerHandshakeMachine(parameters, (ushort)RandomNumberGenerator.GetInt32(1, ushort.MaxValue + 1));
            var response = candidate.ReceiveSync(sync); machine = candidate; receivedSequence = candidate.RemoteSequence;
            // Native callbacks in an unfocused/private desktop can be processed more than
            // seven seconds apart. Five one-second attempts used to revoke valid peers
            // before their next ACK. This is bounded private-server delivery policy:
            // retain the same SEQ for at most one minute; authentication leases are still
            // checked before every send. No readiness flag or missing reply is synthesized.
            outbox = new(candidate.LocalSequence, new(1360, 4), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60), 60, clock);
            return response is null ? [] : [EdServerControlMessageCodec.EncodeSyncLetter(response)];
        }
        if(sync.Acknowledgement==0&&sync.Sequence!=machine.RemoteSequence
            &&machine.State is EdServerHandshakeState.Established or EdServerHandshakeState.AwaitingAcknowledgement)
            return RestartTransport(sync);
        var next = machine.ReceiveSync(sync); return next is null ? [] : [EdServerControlMessageCodec.EncodeSyncLetter(next)];
    }
    private IReadOnlyList<byte[]> RestartTransport(EdServerSyncMessage sync)
    {
        // Native reset chooses a new random sequence. Keep its successor clear
        // of the old sent window as an additional private-server replay bound.
        ushort seed;var lastAck=outbox!.LastAcknowledgedSequence;var lastSent=outbox.LastSentSequence;
        do{seed=(ushort)RandomNumberGenerator.GetInt32(1,ushort.MaxValue+1);}
        while(Distance(lastAck,EdServerHandshakeMachine.Next(seed))<=Distance(lastAck,lastSent)+32
            ||Distance(EdServerHandshakeMachine.Next(seed),lastAck)<=32);
        var next=machine!.CreateRestart(sync,seed); // Validates role/build/key before any allocation or state change.
        var reply=EdServerControlMessageCodec.EncodeSyncLetter(next.PendingSync!);
        LocalReplicationApplication? nextApplication=null;
        EdServerReliableOutbox? nextOutbox=null;
        try
        {
            nextApplication=application?.Restart(allocator.AllocateBlock("management-"+launchId));
            nextOutbox=new(next.LocalSequence,new(1360,4),TimeSpan.FromSeconds(1),TimeSpan.FromSeconds(60),60,clock);
        }
        catch{nextApplication?.Dispose();nextOutbox?.Dispose();CryptographicOperations.ZeroMemory(reply);throw;}
        outbox.Dispose();application?.Dispose();foreach(var replay in replays.Values)replay.Dispose();
        replays.Clear();replayOrder.Clear();outbox=nextOutbox;application=nextApplication;machine=next;
        // The allocator belongs to the native network manager, not this UDP
        // transport. 62C096 constructs it once;64FC70 retains firstBase+8.
        // A hard-SYNC resets4C26CA0's queues but the same launched client keeps
        // its issued block and may immediately send StartServerAdmin again,
        // without another type10 request. Keep this lease-scoped binding.
        receivedSequence=next.RemoteSequence;EstablishedMtu=null;mtuProbeAttempts=0;lastMtuProbe=0;
        TransportRestarts++;
        return[reply];
    }
    private bool CanAcceptAck(EdServerAckMessage ack)
    {
        if (machine is null || ack.Sequence == 0 || ack.SelectiveMask > 1023 || ack.Allowance > 63) return false;
        if (State == EdServerHandshakeState.AwaitingAcknowledgement) return ack.Sequence == machine.LocalSequence && ack.SelectiveMask == 0;
        var last = outbox!.LastAcknowledgedSequence; var span = Distance(last, outbox.LastSentSequence);
        if (Distance(last, ack.Sequence) > span) return false;
        for (var bit = 0; bit < 10; bit++) if ((ack.SelectiveMask & (1 << bit)) != 0)
        { var sequence = (ushort)((ack.Sequence - 1 + bit + 2) % ushort.MaxValue + 1); var distance = Distance(last, sequence); if (distance == 0 || distance > span) return false; }
        return true;
    }
    private bool CanQueue(int count, EdServerAckMessage? ack)
    {
        if (outbox is null || outbox.State != EdServerReliableOutboxState.Active) return false;
        var last = outbox.LastAcknowledgedSequence; var after = ack?.Sequence ?? last;
        var pending = outbox.PendingSequences().Count(sequence => !ack.HasValue || Distance(last, sequence) > Distance(last, after)
            && !Enumerable.Range(0, 10).Any(bit => (ack.Value.SelectiveMask & (1 << bit)) != 0 && sequence == (ushort)((after - 1 + bit + 2) % ushort.MaxValue + 1)));
        return pending + count <= outbox.Capacity && Distance(after, outbox.LastSentSequence) + count <= outbox.Capacity;
    }
    private Packet Parse(ReadOnlySpan<byte> letters)
    {
        var p = 0; var count = 0; Group? group = null; var result = new Packet();
        try
        {
        while (letters.Length * 8 - p >= IncomingTypeBits)
        {
            if (p != 0 && EdServerControlMessageCodec.IsZeroPadding(letters, p)) break;
            if (++count > 32) throw new NotSupportedException("control-count-bound");
            var start = p; var type = EdServerControlMessageCodec.ReadLetterType(letters, ref p, IncomingTypeBits);
            currentTags.Add(new { type, offset = start }); if (seenTypes.Count < 32 || seenTypes.ContainsKey(type)) seenTypes[type] = seenTypes.GetValueOrDefault(type) + 1;
            if (type == 0)
            {
                if (start != 0) throw new InvalidDataException("nonzero-terminator"); p = 0;
                result.Sync = EdServerControlMessageCodec.ReadSyncLetter(letters, ref p, Protocol);
                if (result.Sync.AlignmentMode != 0) EdServerLetterFramingCodec.AlignRead(letters, ref p);
                if (!EdServerControlMessageCodec.IsZeroPadding(letters, p)) throw new InvalidDataException("sync-trailing-data"); return result;
            }
            var frame = EdServerLetterFramingCodec.BeginRead(letters, ref p, type, IncomingLengthDelimited, IncomingAlignmentMode);
            Command? command = null;
            switch ((EdServerBuiltInLetter)type)
            {
                case EdServerBuiltInLetter.Ack:
                    if (result.Ack.HasValue) throw new InvalidDataException("repeated-ack"); result.Ack = EdServerControlMessageCodec.ReadAckBody(letters, ref p); break;
                case EdServerBuiltInLetter.Sequence:
                    if(group is not null && group.Commands.Count==0)throw new InvalidDataException("empty-reliable-group");
                    var sequence=EdServerControlMessageCodec.ReadSequenceBody(letters,ref p);
                    if(result.Groups.Any(value=>value.Sequence==sequence))throw new InvalidDataException("repeated-reliable-group");
                    group=new(sequence);result.Groups.Add(group);break;
                case EdServerBuiltInLetter.MtuTest:
                    var test = EdServerMtuMessageCodec.ReadTest(letters, ref p).Mtu; ValidateMtu(test); result.MtuTests.Add(test); break;
                case EdServerBuiltInLetter.MtuReply:
                    var mtu = EdServerMtuMessageCodec.ReadReply(letters, ref p); LastMtuReplyCandidate = mtu; ValidateMtu(mtu); command = new(type, mtu); break;
                case EdServerBuiltInLetter.Route: command = new(type, null, EdServerRouteMessageCodec.ReadBody(letters, ref p)); break;
                case EdServerBuiltInLetter.IdAllocationRequest: command = new(type); break;
                case (EdServerBuiltInLetter)26:
                case (EdServerBuiltInLetter)13:
                case (EdServerBuiltInLetter)14:
                case (EdServerBuiltInLetter)15:
                case (EdServerBuiltInLetter)16:
                case (EdServerBuiltInLetter)32:
                    var app=LocalApplicationCommand.Read(type,letters,ref p);result.OwnedApplications.Add(app);command=new(type,Application:app);break;
                case (EdServerBuiltInLetter)20:
                case (EdServerBuiltInLetter)21:
                case (EdServerBuiltInLetter)22:
                case (EdServerBuiltInLetter)23:
                    var alignedCalls=(IncomingAlignmentMode&2)!=0;
                    var callCommand=LocalApplicationCommand.Read(type,letters,ref p,alignedCalls);result.OwnedApplications.Add(callCommand);
                    if(callCommand.Call is {} call)
                    {
                        var payload=call.CopyPayload();
                        try{
                            var ownController=application?.RecognizesController(call.ObjectReference)==true;
                            var peerInfoTargetKind=ownController&&call.PayloadBits==88&&payload[0]==13
                                ?application!.PeerInfoTargetKind(EdServerPeerInfoMessageCodec.ReadRequest(call).MachineReference):null;
                            //Only a bounded enum candidate, not a raw arbitrary U32.
                            //128 is still unimplemented; this does not ACK it or
                            //assign business semantics to the native scalar.
                            var scalar128=ownController&&call.PayloadBits==40&&payload[0]==128
                                ?System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(1)):(uint?)null;
                            LastCallEnvelopeShape=new{letterType=type,payloadBits=call.PayloadBits,flag=call.Flag,alignedValues=alignedCalls,
                            referenceKind=application?.CallReferenceKind(call.ObjectReference)??"unbound",
                            methodIndex=ownController&&call.PayloadBits>=8
                                &&payload[0]<LocalReplicationApplication.ControllerRegisteredMethods?(int?)payload[0]:null,
                            peerInfoTargetKind,method128Scalar=scalar128<=255?scalar128:null,applicationExecuted=false};
                            if(recentCallShapes.Count<32&&!recentCallShapes.Contains(LastCallEnvelopeShape))recentCallShapes.Enqueue(LastCallEnvelopeShape);}
                        finally{CryptographicOperations.ZeroMemory(payload);}
                    }
                    if(group is null)result.UnreliableApplications.Add(callCommand);else command=new(type,Application:callCommand);break;
                case (EdServerBuiltInLetter)19:
                    var ack=LocalApplicationCommand.Read(type,letters,ref p);result.OwnedApplications.Add(ack);
                    if(group is null)result.UnreliableApplications.Add(ack);else command=new(type,Application:ack);
                    break;
                case (EdServerBuiltInLetter)18:
                case (EdServerBuiltInLetter)33:
                    var stateCommand=LocalApplicationCommand.Read(type,letters,ref p);result.OwnedApplications.Add(stateCommand);
                    if(stateCommand.State is {} state)
                    {
                        var shape=new{letterType=type,referenceKind=application?.CallReferenceKind(state.ObjectReference)??"unbound",state.SecondaryArchive,
                            state.ConstructionVersion,state.PayloadBits,state.BaseSequence};
                        if(objectStateShapes.Count<32&&!objectStateShapes.Contains(shape))objectStateShapes.Add(shape);
                    }
                    if(group is null)result.UnreliableApplications.Add(stateCommand);else command=new(type,Application:stateCommand);
                    break;
                default: throw new NotSupportedException("unsupported-letter-type");
            }
            EdServerLetterFramingCodec.CompleteRead(letters, ref p, frame);
            if (command is not null)
            { if (group is null) throw new InvalidDataException("missing-reliable-prefix"); group.Commands.Add(command); }
        }
        if(group is not null && group.Commands.Count==0)throw new InvalidDataException("empty-reliable-group");
        if (!EdServerControlMessageCodec.IsZeroPadding(letters, p)) throw new InvalidDataException("truncated-letter-type"); return result;
        }
        catch{result.Dispose();throw;}
    }
    private static void ValidateMtu(ushort value) { if (value is < 576 or > 65507) throw new InvalidDataException("invalid-mtu-bound"); }
    private static byte[] EncodeTransmission(EdServerReliableTransmission transmission)
    { var bytes = new byte[4 + transmission.PayloadLength]; var p = 0; EdServerControlMessageCodec.WriteSequenceLetter(bytes, ref p, transmission.Sequence, OutgoingTypeBits); transmission.CopyPayloadTo(bytes.AsSpan(4)); return bytes; }
    private static byte[] Identity(Group group)
    {
        var bytes = new byte[4+35*group.Commands.Count];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes,group.Sequence);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2),checked((ushort)group.Commands.Count));
        var offset=4;
        foreach(var command in group.Commands)
        {
            bytes[offset]=command.Type;System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset+1),command.Mtu??0);
            if(command.Route is {} route){var digest=route.CopyIdentityDigest();digest.CopyTo(bytes,offset+3);CryptographicOperations.ZeroMemory(digest);}
            if(command.Application is {} app){var digest=app.Identity();digest.CopyTo(bytes,offset+3);CryptographicOperations.ZeroMemory(digest);}
            offset+=35;
        }
        try { return SHA256.HashData(bytes); } finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private void AddReplay(ushort sequence, Replay replay)
    {
        replays.Add(sequence, replay); replayOrder.Enqueue(sequence);
        while (replays.Count > 32) { var old = replayOrder.Dequeue(); if (replays.Remove(old, out var removed)) removed.Dispose(); }
    }
    internal void Revoke() { Closed = true; outbox?.Revoke();application?.Dispose(); foreach (var replay in replays.Values) replay.Dispose(); replays.Clear(); replayOrder.Clear(); }
    public void Dispose() => Revoke();
    private static int Distance(ushort from, ushort to) => (to - from + ushort.MaxValue) % ushort.MaxValue;
    private static string Label(Exception error) => error is EdServerRouteSchemaException?"unsupported-route-schema":error.Message switch
    {
        "Expected a zero SYNC type/preamble."=>"sync-preamble-mismatch",
        "The native SYNC protocol version does not match."=>"sync-protocol-mismatch",
        "The native SYNC key vector exceeds the supported bound."=>"sync-key-bound",
        "The SYNC protocol, peer role or build revisions do not match this local session."=>"sync-peer-version-mismatch",
        "The client advertised a disconnected/error SYNC."=>"client-disconnected-sync",
        "The soft SYNC acknowledgement is outside the outstanding exchange."=>"sync-ack-mismatch",
        "A new hard SYNC after establishment requires the reconnect state machine."=>"reconnect-unimplemented",
        "Native control letter is truncated or has an invalid offset."=>"control-truncated",
        "The native letter body does not match its declared bit length."=>"letter-body-length-mismatch",
        "The native letter trailer is not 0xFADE."=>"letter-trailer-mismatch",
        "Invalid native letter frame boundaries."=>"letter-frame-bounds",
        "Native letter envelope is truncated or has an invalid offset."=>"letter-frame-truncated",
        _=>LabelApplication(error)
    };
    private static string LabelApplication(Exception error)=>error.Message.StartsWith("controller-secondary-",StringComparison.Ordinal)?error.Message:error.Message is "initial-ack-mismatch" or "transport-not-established" or "ack-outside-sent-span"
        or "changed-reliable-duplicate" or "receive-window-required" or "mtu-value-mismatch" or "send-window-full" or "ack-rejected"
        or "control-count-bound" or "nonzero-terminator" or "sync-trailing-data" or "repeated-ack" or "unsupported-letter-type"
        or "missing-reliable-prefix" or "truncated-letter-type" or "invalid-mtu-bound" or "empty-reliable-group" or "repeated-reliable-group"
        or "application-closed" or "application-already-started" or "application-transport-not-ready" or "unsupported-application-role"
        or "application-credentials-rejected" or "application-not-started" or "application-session-mismatch" or "controller-ack-mismatch" or "controller-salute-mismatch"
        or "controller-call-mismatch" or "controller-call-unimplemented" or "controller-state-mismatch" or "controller-state-unimplemented"
        or "controller-profile-context-required" or "controller-profile-identity-mismatch"
        or "native-rep-truncated" or "native-rep-payload-bound" ? error.Message : "invalid-control-shape";
    private sealed record Command(byte Type, ushort? Mtu = null, EdServerRouteMetadata? Route = null,LocalApplicationCommand? Application=null);
    private sealed class Group(ushort sequence){internal ushort Sequence{get;}=sequence;internal List<Command> Commands{get;}=[];}
    private sealed class Packet:IDisposable
    {
        internal EdServerSyncMessage? Sync;internal EdServerAckMessage? Ack;internal readonly List<Group> Groups=[];internal readonly List<ushort> MtuTests=[];
        internal readonly List<LocalApplicationCommand> OwnedApplications=[];
        internal readonly List<LocalApplicationCommand> UnreliableApplications=[];
        public void Dispose(){foreach(var app in OwnedApplications)app.Dispose();}
    }
    private sealed record Replay(byte[] Identity, byte[][] Replies) : IDisposable
    { public void Dispose() { CryptographicOperations.ZeroMemory(Identity); foreach (var reply in Replies) CryptographicOperations.ZeroMemory(reply); } }
}
