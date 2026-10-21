using XFEEliteDangerous.NativeFormat.Replication;
using Xunit;

namespace XFEEliteDangerous.Client.Tests;

public sealed class EdServerSessionNegotiationTests
{
    private const ulong Session = 0x123456;
    private const ulong A = 101, B = 202, C = 303;
    private const ulong BaseA = 4096, BaseB = 8192, BaseC = 12288;

    private static EdServerSessionNegotiationMachine Create(ulong local = A, ulong remote = B,
        int objectLimit = 4) => new(new(Session, local, local == A ? BaseA : BaseB,
            [local], [local == A ? 4097UL : 8193UL], [local == A ? 4098UL : 8194UL]),
            [new(remote, remote == A ? BaseA : BaseB)], maximumObjectReferences: objectLimit);

    [Fact]
    public void TwoExistingSessionsCompleteNativeOrderedExchangeWithoutCreatingWorld()
    {
        var a = Create();
        var b = Create(B, A);
        var request = Assert.IsType<EdServerSessionRequestOutbound>(Assert.Single(a.BeginJoin(B).Outbound));
        Assert.Equal(EdServerSessionPeerState.Requested, a.GetPeerState(B));
        var atB = b.ReceiveRequest(A, request.Message);
        Assert.Equal(new[] { EdServerSessionLetter.Members, EdServerSessionLetter.Joined },
            atB.Outbound.Select(x => x.Letter));
        Assert.Equal(EdServerSessionPeerState.AwaitingJoined, b.GetPeerState(A));
        var members = Assert.IsType<EdServerSessionMembersOutbound>(atB.Outbound[0]);
        Assert.Equal(new[] { B }, members.Message.MachineRunIds);
        Assert.Equal(BaseB, members.Message.InitialObjectIdBase);
        var atA = a.ReceiveMembers(B, members.Message);
        var aJoined = Assert.IsType<EdServerSessionJoinedOutbound>(Assert.Single(atA.Outbound));
        Assert.Equal(A, members.DestinationMachineRunId);
        Assert.Equal(B, aJoined.DestinationMachineRunId);
        Assert.Equal(new ulong[] { 4097 }, aJoined.Message.FirstObjectIds);
        Assert.Equal(new ulong[] { 4098 }, aJoined.Message.SecondObjectIds);
        Assert.True(b.ReceiveJoined(A, aJoined.Message).StateChanged);
        var bJoined = Assert.IsType<EdServerSessionJoinedOutbound>(atB.Outbound[1]);
        Assert.True(a.ReceiveJoined(B, bJoined.Message).StateChanged);
        Assert.Equal(EdServerSessionPeerState.Joined, a.GetPeerState(B));
        Assert.Equal(EdServerSessionPeerState.Joined, b.GetPeerState(A));
        Assert.False(a.WorldReady);
        Assert.False(b.WorldReady);
    }

    [Fact]
    public void SimultaneousRequestsConvergeWithoutResetOrExtraPeers()
    {
        var a = Create();
        var b = Create(B, A);
        var aq = Assert.IsType<EdServerSessionRequestOutbound>(Assert.Single(a.BeginJoin(B).Outbound));
        var bq = Assert.IsType<EdServerSessionRequestOutbound>(Assert.Single(b.BeginJoin(A).Outbound));
        var ax = a.ReceiveRequest(B, bq.Message);
        var bx = b.ReceiveRequest(A, aq.Message);
        a.ReceiveMembers(B, Assert.IsType<EdServerSessionMembersOutbound>(bx.Outbound[0]).Message);
        b.ReceiveMembers(A, Assert.IsType<EdServerSessionMembersOutbound>(ax.Outbound[0]).Message);
        a.ReceiveJoined(B, Assert.IsType<EdServerSessionJoinedOutbound>(bx.Outbound[1]).Message);
        b.ReceiveJoined(A, Assert.IsType<EdServerSessionJoinedOutbound>(ax.Outbound[1]).Message);
        Assert.Equal(EdServerSessionPeerState.Joined, a.GetPeerState(B));
        Assert.Equal(EdServerSessionPeerState.Joined, b.GetPeerState(A));
        Assert.Equal(1, a.PeerCount);
        Assert.Equal(1, b.PeerCount);
    }

    [Fact]
    public void OutboundMessagesUseExistingVerifiedWireCodec()
    {
        var receiver = Create(B, A);
        var result = receiver.ReceiveRequest(A, new(Session, BaseA));
        var members = Assert.IsType<EdServerSessionMembersOutbound>(result.Outbound[0]).Message;
        var joined = Assert.IsType<EdServerSessionJoinedOutbound>(result.Outbound[1]).Message;
        var bytes = new byte[128];
        var p = 3;
        EdServerSessionMessageCodec.WriteMembersBody(bytes, ref p, members);
        var middle = p;
        EdServerSessionMessageCodec.WriteJoinedBody(bytes, ref p, joined);
        var end = p;
        p = 3;
        var readMembers = EdServerSessionMessageCodec.ReadMembersBody(bytes, ref p);
        Assert.Equal(middle, p);
        var readJoined = EdServerSessionMessageCodec.ReadJoinedBody(bytes, ref p);
        Assert.Equal(end, p);
        Assert.Equal(Session, readMembers.SessionId);
        Assert.Equal(BaseB, readMembers.InitialObjectIdBase);
        Assert.Equal(members.MachineRunIds, readMembers.MachineRunIds);
        Assert.Equal(joined.FirstObjectIds, readJoined.FirstObjectIds);
        Assert.Equal(joined.SecondObjectIds, readJoined.SecondObjectIds);
    }

    [Fact]
    public void RequestRetriesReusePinnedMessageAndCannotResetLaterState()
    {
        var machine = Create();
        var first = machine.BeginJoin(B);
        var retry = machine.BeginJoin(B);
        Assert.True(first.StateChanged);
        Assert.False(retry.StateChanged);
        Assert.True(retry.IsDuplicate);
        Assert.Same(Assert.IsType<EdServerSessionRequestOutbound>(first.Outbound[0]).Message,
            Assert.IsType<EdServerSessionRequestOutbound>(retry.Outbound[0]).Message);
        machine.ReceiveMembers(B, new(Session, BaseB, [B]));
        Assert.Empty(machine.BeginJoin(B).Outbound);
        machine.ReceiveJoined(B, new(Session, [], []));
        Assert.Empty(machine.BeginJoin(B).Outbound);
        Assert.Equal(EdServerSessionPeerState.Joined, machine.GetPeerState(B));
    }

    [Fact]
    public void RepeatedRequestAfterNewJoinDoesNotRepeatNativeAddOrEmission()
    {
        var machine = Create();
        machine.ReceiveRequest(B, new(Session, BaseB));
        var duplicate = machine.ReceiveRequest(B, new(Session, BaseB));
        Assert.True(duplicate.IsDuplicate);
        Assert.False(duplicate.StateChanged);
        Assert.Empty(duplicate.Outbound);
        machine.ReceiveJoined(B, new(Session, [], []));
        Assert.Empty(machine.ReceiveRequest(B, new(Session, BaseB)).Outbound);
    }

    [Fact]
    public void MembersRetriesResendJoinedWithoutRepeatingTransition()
    {
        var machine = Create();
        machine.BeginJoin(B);
        var members = new EdServerSessionMembers(Session, BaseB, [B, A]);
        var first = machine.ReceiveMembers(B, members);
        var duplicate = machine.ReceiveMembers(B, members);
        Assert.True(first.StateChanged);
        Assert.False(duplicate.StateChanged);
        Assert.True(duplicate.IsDuplicate);
        Assert.Same(Assert.IsType<EdServerSessionJoinedOutbound>(first.Outbound[0]).Message,
            Assert.IsType<EdServerSessionJoinedOutbound>(duplicate.Outbound[0]).Message);
        machine.ReceiveJoined(B, new(Session, [], []));
        var afterJoined = machine.ReceiveMembers(B, members);
        Assert.Equal(EdServerSessionPeerState.Joined, afterJoined.State);
        Assert.True(afterJoined.IsDuplicate);
        Assert.Single(afterJoined.Outbound);
    }

    [Fact]
    public void JoinedRetriesAreIdempotentAndCannotReplaceAcceptedObjectSets()
    {
        var machine = Create();
        machine.ReceiveRequest(B, new(Session, BaseB));
        var first = machine.ReceiveJoined(B, new(Session, [8193], [8194]));
        var duplicate = machine.ReceiveJoined(B, new(Session, [8193], [8194]));
        Assert.True(first.StateChanged);
        Assert.False(duplicate.StateChanged);
        Assert.True(duplicate.IsDuplicate);
        Assert.Empty(duplicate.Outbound);
        Assert.Throws<InvalidDataException>(() => machine.ReceiveJoined(B, new(Session, [8195], [8194])));
        Assert.True(machine.ReceiveJoined(B, new(Session, [8193], [8194])).IsDuplicate);
        Assert.False(machine.WorldReady);
    }

    [Theory]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    public void WrongSessionIsRejectedWithoutMutation(int letter)
    {
        var machine = Create();
        machine.BeginJoin(B);
        Assert.Throws<InvalidDataException>(() => Receive(machine, letter, B, Session + 1, BaseB));
        Assert.Equal(EdServerSessionPeerState.Requested, machine.GetPeerState(B));
    }

    [Theory]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    public void UnknownSenderCannotCreatePeerOrSession(int letter)
    {
        var machine = Create();
        Assert.Throws<InvalidDataException>(() => Receive(machine, letter, C, Session, BaseC));
        Assert.Equal(1, machine.PeerCount);
        Assert.Equal(EdServerSessionPeerState.None, machine.GetPeerState(B));
        Assert.Throws<InvalidDataException>(() => machine.GetPeerState(C));
        Assert.Throws<InvalidDataException>(() => machine.BeginJoin(C));
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(8193UL)]
    [InlineData(12288UL)]
    [InlineData(ulong.MaxValue)]
    public void RequestAndMembersRequirePinnedBaseIncludingDuplicateRequests(ulong wrongBase)
    {
        var machine = Create();
        Assert.Throws<InvalidDataException>(() => machine.ReceiveRequest(B, new(Session, wrongBase)));
        machine.BeginJoin(B);
        Assert.Throws<InvalidDataException>(() => machine.ReceiveMembers(B, new(Session, wrongBase, [])));
        machine.ReceiveRequest(B, new(Session, BaseB));
        Assert.Throws<InvalidDataException>(() => machine.ReceiveRequest(B, new(Session, wrongBase)));
        Assert.Equal(EdServerSessionPeerState.AwaitingJoined, machine.GetPeerState(B));
    }

    [Fact]
    public void MembersAndJoinedRejectUnsolicitedOrPrematureTransitions()
    {
        var machine = Create();
        Assert.Throws<InvalidDataException>(() => machine.ReceiveMembers(B, new(Session, BaseB, [B])));
        Assert.Throws<InvalidDataException>(() => machine.ReceiveJoined(B, new(Session, [], [])));
        machine.BeginJoin(B);
        Assert.Throws<InvalidDataException>(() => machine.ReceiveJoined(B, new(Session, [], [])));
        Assert.Equal(EdServerSessionPeerState.Requested, machine.GetPeerState(B));
    }

    [Fact]
    public void InvalidMemberCollectionsCannotDiscoverPeersOrPartiallyAdvance()
    {
        var machine = Create();
        machine.BeginJoin(B);
        foreach (var ids in new ulong[][] { [C], [0], [A, A], [A, B, C] })
            Assert.Throws<InvalidDataException>(() => machine.ReceiveMembers(B, new(Session, BaseB, ids)));
        Assert.Equal(EdServerSessionPeerState.Requested, machine.GetPeerState(B));
        Assert.False(machine.ReceiveMembers(B, new(Session, BaseB, [B])).IsDuplicate);
        Assert.Equal(1, machine.PeerCount);
    }

    [Fact]
    public void ObjectCollectionsRespectCombinedBoundBeforeStateChanges()
    {
        var machine = Create(objectLimit: 2);
        machine.ReceiveRequest(B, new(Session, BaseB));
        Assert.Throws<InvalidDataException>(() => machine.ReceiveJoined(B, new(Session, [8193, 8194], [8195])));
        Assert.Equal(EdServerSessionPeerState.AwaitingJoined, machine.GetPeerState(B));
        machine.ReceiveJoined(B, new(Session, [8193], [8194]));
        Assert.Equal(EdServerSessionPeerState.Joined, machine.GetPeerState(B));
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(0x1000000000000UL)]
    public void InvalidWireObjectReferencesAreRejectedAtomically(ulong invalid)
    {
        var machine = Create();
        machine.ReceiveRequest(B, new(Session, BaseB));
        Assert.Throws<InvalidDataException>(() => machine.ReceiveJoined(B, new(Session, [8193], [invalid])));
        Assert.Equal(EdServerSessionPeerState.AwaitingJoined, machine.GetPeerState(B));
    }

    [Fact]
    public void CallerArraysAndReturnedCollectionsCannotMutatePinnedDefinitionOrAcceptedState()
    {
        ulong[] localMembers = [A], localFirst = [4097], localSecond = [4098];
        EdServerSessionPeerDefinition[] peers = [new(B, BaseB)];
        var machine = new EdServerSessionNegotiationMachine(new(Session, A, BaseA,
            localMembers, localFirst, localSecond), peers);
        localMembers[0] = C;
        localFirst[0] = 99;
        peers[0] = new(C, BaseC);
        var outbound = machine.ReceiveRequest(B, new(Session, BaseB)).Outbound;
        var members = Assert.IsType<EdServerSessionMembersOutbound>(outbound[0]).Message;
        var joined = Assert.IsType<EdServerSessionJoinedOutbound>(outbound[1]).Message;
        Assert.Equal(new[] { A }, members.MachineRunIds);
        Assert.Equal(new ulong[] { 4097 }, joined.FirstObjectIds);
        Assert.Throws<NotSupportedException>(() => ((IList<ulong>)joined.FirstObjectIds)[0] = 999);
        ulong[] remoteFirst = [8193], remoteSecond = [8194];
        machine.ReceiveJoined(B, new(Session, remoteFirst, remoteSecond));
        remoteFirst[0] = 999;
        remoteSecond[0] = 998;
        Assert.True(machine.ReceiveJoined(B, new(Session, [8193], [8194])).IsDuplicate);
    }

    [Fact]
    public void ZeroObjectLimitAllowsAnExplicitEmptyNegotiationButNeverWorldReadiness()
    {
        var machine = new EdServerSessionNegotiationMachine(new(Session, A, BaseA, [A], [], []),
            [new(B, BaseB)], maximumObjectReferences: 0);
        machine.ReceiveRequest(B, new(Session, BaseB));
        Assert.Throws<InvalidDataException>(() => machine.ReceiveJoined(B, new(Session, [1], [])));
        machine.ReceiveJoined(B, new(Session, [], []));
        Assert.False(machine.WorldReady);
    }

    [Fact]
    public void LocalSessionAndPeerIdsMustBeNonzeroAndFitWireWidth()
    {
        Assert.Throws<InvalidDataException>(() => new EdServerSessionNegotiationMachine(
            new(0, A, BaseA, [A], [], []), [new(B, BaseB)]));
        Assert.Throws<InvalidDataException>(() => new EdServerSessionNegotiationMachine(
            new(Session, 0, BaseA, [0], [], []), [new(B, BaseB)]));
        Assert.Throws<InvalidDataException>(() => new EdServerSessionNegotiationMachine(
            new(Session, A, BaseA, [A], [], []), [new(0x1000000000000, BaseB)]));
    }

    [Fact]
    public void ConstructorRejectsMissingLocalMembershipAndDuplicateMachinesOrBlocks()
    {
        Assert.Throws<ArgumentException>(() => new EdServerSessionNegotiationMachine(
            new(Session, A, BaseA, [], [], []), [new(B, BaseB)]));
        Assert.Throws<ArgumentException>(() => new EdServerSessionNegotiationMachine(
            new(Session, A, BaseA, [A], [], []), [new(A, BaseB)]));
        Assert.Throws<ArgumentException>(() => new EdServerSessionNegotiationMachine(
            new(Session, A, BaseA, [A], [], []), [new(B, BaseB), new(B, BaseC)]));
        Assert.Throws<ArgumentException>(() => new EdServerSessionNegotiationMachine(
            new(Session, A, BaseA, [A], [], []), [new(B, BaseA)]));
        Assert.Throws<ArgumentException>(() => new EdServerSessionNegotiationMachine(
            new(Session, A, BaseA, [A], [], []), [new(B, BaseB), new(C, BaseB)]));
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(1UL)]
    [InlineData(0xFFFFFFFF0000UL)]
    [InlineData(ulong.MaxValue)]
    public void InitialBasesMustFollowExplicitPrivateServerAllocationPolicy(ulong invalid)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new EdServerSessionNegotiationMachine(
            new(Session, A, invalid, [A], [], []), [new(B, BaseB)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EdServerSessionNegotiationMachine(
            new(Session, A, BaseA, [A], [], []), [new(B, invalid)]));
    }

    [Fact]
    public void ConfigurationAndCollectionBoundsCannotBeBypassed()
    {
        var definition = new EdServerLocalSessionDefinition(Session, A, BaseA, [A], [], []);
        Assert.Throws<ArgumentOutOfRangeException>(() => new EdServerSessionNegotiationMachine(definition, [], 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EdServerSessionNegotiationMachine(definition, [], 1025));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EdServerSessionNegotiationMachine(definition, [], maximumObjectReferences: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EdServerSessionNegotiationMachine(definition, [], maximumObjectReferences: 65535));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EdServerSessionNegotiationMachine(definition,
            [new(B, BaseB), new(C, BaseC)], maximumPeers: 1));
        Assert.Throws<InvalidDataException>(() => new EdServerSessionNegotiationMachine(definition with
            { MemberMachineRunIds = [A, C] }, [new(B, BaseB)]));
        Assert.Throws<InvalidDataException>(() => new EdServerSessionNegotiationMachine(definition with
            { FirstObjectIds = [1, 2], SecondObjectIds = [3] }, [new(B, BaseB)], maximumObjectReferences: 2));
    }

    [Fact]
    public void MaximumPeerBoundaryAndConcurrentDuplicatesRemainBounded()
    {
        var definitions = Enumerable.Range(0, EdServerSessionNegotiationMachine.HardMaximumPeers)
            .Select(i => new EdServerSessionPeerDefinition((ulong)i + 2, (ulong)(i + 2) * 4096)).ToArray();
        var machine = new EdServerSessionNegotiationMachine(new(Session, 1, BaseA, [1], [], []),
            definitions, EdServerSessionNegotiationMachine.HardMaximumPeers);
        var changed = 0;
        Parallel.For(0, 32, _ =>
        {
            if (machine.ReceiveRequest(2, new(Session, BaseB)).StateChanged)
                Interlocked.Increment(ref changed);
        });
        Assert.Equal(1, changed);
        Assert.Equal(1024, machine.PeerCount);
        Assert.Equal(EdServerSessionPeerState.AwaitingJoined, machine.GetPeerState(2));
        Assert.Equal(EdServerSessionPeerState.None, machine.GetPeerState(1025));
        Assert.False(machine.WorldReady);
    }

    private static void Receive(EdServerSessionNegotiationMachine machine, int letter, ulong sender,
        ulong session, ulong initialBase)
    {
        switch (letter)
        {
            case 13: machine.ReceiveRequest(sender, new(session, initialBase)); break;
            case 14: machine.ReceiveMembers(sender, new(session, initialBase, [])); break;
            case 15: machine.ReceiveJoined(sender, new(session, [], [])); break;
            default: throw new ArgumentOutOfRangeException(nameof(letter));
        }
    }
}
