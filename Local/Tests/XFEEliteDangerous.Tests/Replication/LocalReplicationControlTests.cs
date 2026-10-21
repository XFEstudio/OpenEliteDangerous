using System.Net;
using XFEEliteDangerous.NativeFormat.Replication;
using XFEEliteDangerous.Core;
using Xunit;

namespace XFEEliteDangerous.Tests;

public sealed class NativeEdServerReliableControlTests
{
    [Theory][InlineData(false,0)][InlineData(true,3)]
    public void ARouteRequestGetsAReliableDirectoryUpdateAndRetriesReplayTheSameResponse(bool framed,byte alignment)
    {
        var lookups=0;
        using var fixture=new Fixture(framed,alignment,resolveRoute:machine=>{lookups++;return machine==123?new(0x7F000001,47001,1360):null;});
        var query=new byte[8];var bit=0;EdServerControlMessageCodec.WriteLetterType(query,ref bit,5,11);
        for(var index=0;index<48;index++,bit++)if(((123UL>>index)&1)!=0)query[bit/8]|=(byte)(1<<(bit%8));
        query[bit/8]|=(byte)(1<<(bit%8));
        var packet=fixture.Packet(Seq(102),(query,49));var replies=fixture.Session.Receive(packet,false);
        Assert.Equal(2,replies.Count);Assert.Equal(1,lookups);Assert.Equal(1,fixture.Session.PendingReliable);
        bit=0;Assert.Equal(2,EdServerControlMessageCodec.ReadLetterType(replies[0],ref bit,16));
        EdServerControlMessageCodec.ReadSequenceBody(replies[0],ref bit);
        Assert.Equal(5,EdServerControlMessageCodec.ReadLetterType(replies[0],ref bit,16));
        var route=EdServerRouteMessageCodec.ReadBody(replies[0],ref bit);
        Assert.False(route.IsRequest);Assert.Equal(123UL,route.MachineRunId);Assert.Equal(new[]{"IP4"},route.Schemes);Assert.Equal(1,route.EndpointCount);
        var replay=fixture.Session.Receive(packet,false);Assert.Equal(replies[0],replay[0]);Assert.Equal(1,lookups);
        var malformed=fixture.Packet(Seq(103),(query,49),(Type(99),0));
        Assert.Empty(fixture.Session.Receive(malformed,false));Assert.Equal(1,lookups);
    }
    [Fact]
    public void IdlePrivateConnectionSendsAnUnsequencedAckWithoutAllocatingOrStartingAnApplication()
    {
        using var fixture=new Fixture(withApplication:true);
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(999));Assert.Empty(fixture.Session.ClaimDueRetransmissions());
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(1));
        var heartbeat=Assert.Single(fixture.Session.ClaimDueRetransmissions());var p=0;
        Assert.Equal((byte)EdServerBuiltInLetter.Ack,EdServerControlMessageCodec.ReadLetterType(heartbeat,ref p,16));
        Assert.Equal(new EdServerAckMessage(101,0,16),EdServerControlMessageCodec.ReadAckBody(heartbeat,ref p));
        Assert.True(EdServerControlMessageCodec.IsZeroPadding(heartbeat,p));
        Assert.Equal(1,fixture.Session.HeartbeatPackets);Assert.Equal(0,fixture.Session.PendingReliable);
        Assert.Null(fixture.Session.InitialObjectIdBase);Assert.Equal(1UL,fixture.Allocator.AllocatedBlockCount);
        Assert.Empty(fixture.Session.ClaimDueRetransmissions());
        fixture.Session.Revoke();fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Empty(fixture.Session.ClaimDueRetransmissions());Assert.Equal(1,fixture.Session.HeartbeatPackets);
    }

    [Fact]
    public void ReliableRepliesSuppressExtraHeartbeatsAndLaterHeartbeatUsesCommittedReceivePosition()
    {
        using var fixture=new Fixture(withApplication:true);
        var first=fixture.Session.Receive(fixture.Packet(Seq(102),Id()),false);Assert.Equal(2,first.Count);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(first[0],Assert.Single(fixture.Session.ClaimDueRetransmissions()));
        Assert.Equal(0,fixture.Session.HeartbeatPackets);
        var p=16;var replySequence=EdServerControlMessageCodec.ReadSequenceBody(first[0],ref p);
        Assert.Empty(fixture.Session.Receive(fixture.Packet(Ack(replySequence)),false));
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(999));Assert.Empty(fixture.Session.ClaimDueRetransmissions());
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(1));var heartbeat=Assert.Single(fixture.Session.ClaimDueRetransmissions());p=16;
        Assert.Equal((ushort)102,EdServerControlMessageCodec.ReadAckBody(heartbeat,ref p).Sequence);
        Assert.Equal(1,fixture.Session.HeartbeatPackets);Assert.Equal(0,fixture.Session.PendingReliable);
        Assert.Equal(2UL,fixture.Allocator.AllocatedBlockCount);
    }

    [Fact]
    public void ReconnectedNativeClientCanStartManagementWithoutRequestingAnotherIdBlock()
    {
        using var fixture=new Fixture(withApplication:true);
        Assert.Equal(2,fixture.Session.Receive(fixture.Packet(Seq(102),MtuReply(),Id()),false).Count);
        var firstBase=fixture.Session.InitialObjectIdBase;
        var reply=Assert.Single(fixture.Session.Receive(EdServerControlMessageCodec.EncodeSyncLetter(Fixture.Sync with{Sequence=501}),false));
        var p=0;var sync=EdServerControlMessageCodec.ReadSyncLetter(reply,ref p,33837);
        Assert.Empty(fixture.Session.Receive(fixture.Packet(Ack(sync.Sequence)),false));
        // The actual native reconnect sends StartServerAdmin without type10,
        // before the later reliable group carrying the new MTU reply.
        var accepted=fixture.Session.Receive(fixture.Packet(Seq(502),Start()),false);
        Assert.Equal(2,accepted.Count);Assert.Equal("",fixture.Session.LastControlFailure);
        Assert.Equal(firstBase,fixture.Session.InitialObjectIdBase);
        Assert.Null(fixture.Session.EstablishedMtu);
        Assert.Equal(3UL,fixture.Allocator.AllocatedBlockCount); // Original server/client, fresh server.
        var retransmitted=fixture.Session.Receive(fixture.Packet(Seq(502),Start()),false);
        Assert.Equal(accepted[0],retransmitted[0]);Assert.Equal(accepted[1],retransmitted[1]);
        Assert.Equal(3UL,fixture.Allocator.AllocatedBlockCount);
        Assert.Empty(fixture.Session.Receive(fixture.Packet(Seq(502),Start(wrongAuthentication:true)),false));
        Assert.Equal("changed-reliable-duplicate",fixture.Session.LastControlFailure);
        Assert.Single(fixture.Session.Receive(fixture.Packet(Seq(503),MtuReply()),false));
        Assert.Equal((ushort)1360,fixture.Session.EstablishedMtu);Assert.Equal("",fixture.Session.LastControlFailure);
    }
    [Fact]public void HardResynchronizationDropsOldWindowsAndRequiresANewAcknowledgedTransport()
    {
        using var fixture=new Fixture();
        Assert.Equal(2,fixture.Session.Receive(fixture.Packet(Seq(102),MtuReply(),Id()),false).Count);
        var firstBase=fixture.Session.InitialObjectIdBase;Assert.Equal(1,fixture.Session.PendingReliable);
        var sync=Fixture.Sync with{Sequence=501};
        var reply=Assert.Single(fixture.Session.Receive(EdServerControlMessageCodec.EncodeSyncLetter(sync),false));var p=0;
        var parsed=EdServerControlMessageCodec.ReadSyncLetter(reply,ref p,33837);
        Assert.Equal(501,parsed.Acknowledgement);Assert.NotEqual(fixture.ServerSequence,parsed.Sequence);
        Assert.Equal(1,fixture.Session.TransportRestarts);Assert.Null(fixture.Session.EstablishedMtu);
        Assert.Equal(firstBase,fixture.Session.InitialObjectIdBase);Assert.Equal(0,fixture.Session.PendingReliable);
        Assert.Equal(EdServerHandshakeState.AwaitingAcknowledgement,fixture.Session.State);
        Assert.Single(fixture.Session.Receive(EdServerControlMessageCodec.EncodeSyncLetter(sync),false));
        Assert.Equal(1,fixture.Session.TransportRestarts);
        Assert.Empty(fixture.Session.Receive(fixture.Packet(Ack(fixture.ServerSequence)),false));
        Assert.Equal(EdServerHandshakeState.AwaitingAcknowledgement,fixture.Session.State);
        Assert.Empty(fixture.Session.Receive(fixture.Packet(Ack(parsed.Sequence)),false));
        Assert.Equal(EdServerHandshakeState.Established,fixture.Session.State);
        Assert.Equal(2,fixture.Session.Receive(fixture.Packet(Seq(502),MtuReply(),Id()),false).Count);
        Assert.Equal(firstBase,fixture.Session.InitialObjectIdBase);Assert.Equal(2UL,fixture.Allocator.AllocatedBlockCount);
    }
    [Fact]public void InvalidRestartCannotClearAValidWindowOrConsumeAnObjectBlock()
    {
        using var fixture=new Fixture();fixture.Session.Receive(fixture.Packet(Seq(102),MtuReply(),Id()),false);
        var originalBase=fixture.Session.InitialObjectIdBase;var pending=fixture.Session.PendingReliable;
        foreach(var changed in new[]{Fixture.Sync with{Sequence=501,BuildRevision=1},Fixture.Sync with{Sequence=501,IsClient=false},
            Fixture.Sync with{Sequence=0},Fixture.Sync with{Sequence=501,EncryptedKeyBytes=new byte[]{1}}})
        {
            Assert.Empty(fixture.Session.Receive(EdServerControlMessageCodec.EncodeSyncLetter(changed),false));
            Assert.Equal(EdServerHandshakeState.Established,fixture.Session.State);
            Assert.Equal(originalBase,fixture.Session.InitialObjectIdBase);Assert.Equal(pending,fixture.Session.PendingReliable);
            Assert.Equal(0,fixture.Session.TransportRestarts);Assert.Equal(1UL,fixture.Allocator.AllocatedBlockCount);
        }
        Assert.Equal(2,fixture.Session.Receive(fixture.Packet(Seq(103),Id()),false).Count);
    }
    [Theory]
    [InlineData(false,0)]
    [InlineData(true,3)]
    public void OneSequenceIdentifiesWholeReliableGroupAndRetryIsDeduplicated(bool framed,byte alignment)
    {
        using var fixture=new Fixture(framed,alignment);
        var batch=fixture.Packet(Seq(102),MtuReply(),MtuReply(),Id());
        var first=fixture.Session.Receive(batch,false);Assert.Equal(2,first.Count);Assert.Equal((ushort)1360,fixture.Session.EstablishedMtu!.Value);
        Assert.Equal(1UL,fixture.Allocator.AllocatedBlockCount);
        var p=16;Assert.Equal((ushort)102,EdServerControlMessageCodec.ReadAckBody(first[1],ref p).Sequence);
        var retry=fixture.Session.Receive(batch,false);Assert.Equal(first[0],retry[0]);Assert.Equal(first[1],retry[1]);
        Assert.Empty(fixture.Session.Receive(fixture.Packet(Seq(102),MtuReply(),Id()),false)); // A partial replay changes the group.
        Assert.Equal(1UL,fixture.Allocator.AllocatedBlockCount);
        Assert.Equal(2,fixture.Session.Receive(fixture.Packet(Seq(103),Id()),false).Count);Assert.Equal(2UL,fixture.Allocator.AllocatedBlockCount);
    }

    [Theory]
    [InlineData(false,0)]
    [InlineData(true,3)]
    public void RouteMetadataAndMtuCanSharePrefixAndMalformedTailCannotCommitAllocation(bool framed,byte alignment)
    {
        using var fixture=new Fixture(framed,alignment);
        var route=Route();var replies=fixture.Session.Receive(fixture.Packet(Seq(102),MtuReply(),route),false);
        Assert.Single(replies);Assert.Equal(1,fixture.Session.AcceptedRoutes);Assert.Equal((ushort)1360,fixture.Session.EstablishedMtu!.Value);
        Assert.Single(fixture.Session.Receive(fixture.Packet(Seq(102),MtuReply(),route),false));Assert.Equal(1,fixture.Session.AcceptedRoutes);
        var malformed=fixture.Packet(Seq(103),Id(),route);Array.Resize(ref malformed,malformed.Length-3);
        Assert.Empty(fixture.Session.Receive(malformed,false));Assert.Equal(0UL,fixture.Allocator.AllocatedBlockCount);
        Assert.Equal(2,fixture.Session.Receive(fixture.Packet(Seq(103),Id()),false).Count);
    }

    [Theory]
    [InlineData(false,0)]
    [InlineData(true,3)]
    public void ExplicitGroupsAdvanceOnceAndCanBeRetriedTogetherOrSeparately(bool framed,byte alignment)
    {
        using var fixture=new Fixture(framed,alignment);
        var original=fixture.Session.Receive(fixture.Packet(Seq(102),MtuReply(),Route(),Seq(103),MtuReply(),MtuReply()),false);
        Assert.Single(original);var bit=16;Assert.Equal((ushort)103,EdServerControlMessageCodec.ReadAckBody(original[0],ref bit).Sequence);
        Assert.Single(fixture.Session.Receive(fixture.Packet(Seq(102),MtuReply(),Route()),false));
        Assert.Single(fixture.Session.Receive(fixture.Packet(Seq(103),MtuReply(),MtuReply()),false));
        Assert.Equal(1,fixture.Session.AcceptedRoutes);
        var next=fixture.Session.Receive(fixture.Packet(Seq(102),MtuReply(),Route(),Seq(104),Id()),false);
        Assert.Equal(2,next.Count);Assert.Equal(1UL,fixture.Allocator.AllocatedBlockCount);
        Assert.Empty(fixture.Session.Receive(fixture.Packet(Seq(105),Id(),Seq(105),Id()),false));
        Assert.Equal(1UL,fixture.Allocator.AllocatedBlockCount);
    }

    [Fact]
    public void RouteCodecIsBoundedUnalignedAndTruncationDoesNotAdvanceCaller()
    {
        var encoded=Route();var p=11;var metadata=EdServerRouteMessageCodec.ReadBody(encoded.Item1,ref p);
        Assert.Equal(123UL,metadata.MachineRunId);Assert.Equal(1,metadata.EndpointCount);Assert.Equal(new[]{"Comp","IP4"},metadata.Schemes);
        Assert.Equal(11+encoded.Item2,p);
        for(var count=0;count<(p+7)/8;count++)
        {
            var prefix=encoded.Item1.AsSpan(0,count).ToArray();var position=11;
            Assert.Throws<InvalidDataException>(()=>EdServerRouteMessageCodec.ReadBody(prefix,ref position));Assert.Equal(11,position);
        }
    }

    [Fact]
    public void RelayRouteMarkerHasNoBodyAndLeavesFollowingFieldUntouched()
    {
        var bytes=new byte[64];var bit=3;
        void Bits(ulong value,int width){for(var index=0;index<width;index++,bit++)if((value&(1UL<<index))!=0)bytes[bit/8]|=(byte)(1<<(bit%8));}
        void Text(string text){Bits((uint)text.Length,32);foreach(var value in System.Text.Encoding.ASCII.GetBytes(text))Bits(value,8);}
        Bits(123,48);Bits(0,1);Text("Comp");Bits(2,16);Text("Relay");Text("IP4");Bits(0x7F000001,32);Bits(34567,16);Bits(1360,16);
        var expected=bit;Bits(0x5A,8);
        var position=3;var route=EdServerRouteMessageCodec.ReadBody(bytes,ref position);
        Assert.Equal(new[]{"Comp","Relay","IP4"},route.Schemes);Assert.Equal(2,route.EndpointCount);Assert.Equal(expected,position);
    }

    [Fact]
    public void ServerMtuProbeRequiresRealMatchingReplyAndStopsAfterAgreement()
    {
        var clock=new ManualClock();using var session=new LocalReplicationSession(new(IPAddress.Loopback,1234),new(),"mtu-fixture",clock,true);
        var sync=Assert.Single(session.Receive(EdServerControlMessageCodec.EncodeSyncLetter(Fixture.Sync),false));var p=0;
        var sequence=EdServerControlMessageCodec.ReadSyncLetter(sync,ref p,33837).Sequence;
        var probe=Assert.Single(session.Receive(EdServerControlMessageCodec.EncodeAckLetter(new(sequence,0,16),11),false));p=0;
        Assert.Equal(1350,probe.Length);Assert.Equal((byte)EdServerBuiltInLetter.MtuTest,EdServerControlMessageCodec.ReadLetterType(probe,ref p,16));
        var test=EdServerMtuMessageCodec.ReadTest(probe,ref p);Assert.Equal(1360,test.Mtu);Assert.Equal(1344,test.Padding.Length);Assert.Null(session.EstablishedMtu);
        Assert.Empty(session.ClaimDueRetransmissions());clock.Advance(TimeSpan.FromSeconds(1));Assert.Single(session.ClaimDueRetransmissions());
        byte[] Reply(ushort mtu)
        {
            var result=new byte[7];var bit=0;EdServerControlMessageCodec.WriteSequenceLetter(result,ref bit,102,11);
            EdServerControlMessageCodec.WriteLetterType(result,ref bit,(byte)EdServerBuiltInLetter.MtuReply,11);EdServerMtuMessageCodec.WriteReply(result,ref bit,mtu);return result;
        }
        Assert.Empty(session.Receive(Reply(1359),false));Assert.Null(session.EstablishedMtu);
        Assert.Single(session.Receive(Reply(1360),false));Assert.Equal((ushort)1360,session.EstablishedMtu!.Value);
        clock.Advance(TimeSpan.FromSeconds(10));Assert.Empty(session.ClaimDueRetransmissions());Assert.Equal(2,session.MtuProbeAttempts);
    }

    [Fact]
    public void ServerMtuProbeHasFiniteBudgetAndUnknownTypeIsDiagnosticOnly()
    {
        var clock=new ManualClock();using var session=new LocalReplicationSession(new(IPAddress.Loopback,1234),new(),"mtu-limit-fixture",clock,true);
        var sync=Assert.Single(session.Receive(EdServerControlMessageCodec.EncodeSyncLetter(Fixture.Sync),false));var p=0;
        var sequence=EdServerControlMessageCodec.ReadSyncLetter(sync,ref p,33837).Sequence;
        Assert.Single(session.Receive(EdServerControlMessageCodec.EncodeAckLetter(new(sequence,0,16),11),false));
        for(var index=0;index<8;index++){clock.Advance(TimeSpan.FromSeconds(1));var replies=session.ClaimDueRetransmissions();Assert.Equal(index<4?1:0,replies.Count);}
        Assert.Equal(5,session.MtuProbeAttempts);Assert.Null(session.EstablishedMtu);
        var unknown=Type(99);Assert.Empty(session.Receive(unknown,false));Assert.Equal(1,session.SeenTypes[99]);Assert.Equal(0,session.PendingReliable);
    }
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 3)]
    public void UnsupportedTrailingLetterCannotAllocateOrCommitEarlierAck(bool framed, byte alignment)
    {
        using var fixture = new Fixture(framed, alignment);
        var pending = fixture.Session.Receive(fixture.Packet(MtuTest()), false);
        Assert.Single(pending);
        var before = fixture.Session.PendingReliable;
        var invalid = fixture.Packet(Ack(fixture.ServerSequence + 1), Seq(102), Id(), (Type(99), 0));
        Assert.Empty(fixture.Session.Receive(invalid, false));
        Assert.Equal(before, fixture.Session.PendingReliable);
        Assert.Equal(0UL, fixture.Allocator.AllocatedBlockCount);
        Assert.Null(fixture.Session.InitialObjectIdBase);
        var accepted = fixture.Session.Receive(fixture.Packet(Seq(102), Id()), false);
        Assert.Equal(2, accepted.Count);
        Assert.Equal(1UL, fixture.Allocator.AllocatedBlockCount);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 3)]
    public void IdRetryReplaysExactReliableSequenceWithoutAllocatingAgain(bool framed, byte alignment)
    {
        using var fixture = new Fixture(framed, alignment);
        var input = fixture.Packet(Seq(102), Id());
        var first = fixture.Session.Receive(input, false);
        var duplicate = fixture.Session.Receive(input, false);
        Assert.Equal(2, first.Count);
        Assert.Equal(first[0], duplicate[0]);
        Assert.Equal(first[1], duplicate[1]);
        Assert.Equal(1UL, fixture.Allocator.AllocatedBlockCount);
        Assert.Equal(1, fixture.Session.PendingReliable);
        Assert.NotNull(fixture.Session.InitialObjectIdBase);
        // The reliable sequence cannot authorize a different command body.
        Assert.Empty(fixture.Session.Receive(fixture.Packet(Seq(102), MtuReply()), false));
        Assert.Null(fixture.Session.EstablishedMtu);
        Assert.Equal(1UL, fixture.Allocator.AllocatedBlockCount);
    }

    [Fact]
    public void TimerRetransmitsPlaintextWithSameSequenceAndAckRemovesPending()
    {
        using var fixture = new Fixture();
        var first = Assert.Single(fixture.Session.Receive(fixture.Packet(MtuTest()), false));
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(999));
        Assert.Empty(fixture.Session.ClaimDueRetransmissions());
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(1));
        var retry = Assert.Single(fixture.Session.ClaimDueRetransmissions());
        Assert.Equal(first, retry);
        var firstCounter = fixture.Session.NextPacketCounter();
        Assert.NotEqual(firstCounter, fixture.Session.NextPacketCounter());
        var p = 16;
        var sent = EdServerControlMessageCodec.ReadSequenceBody(retry, ref p);
        Assert.Empty(fixture.Session.Receive(fixture.Packet(Ack(sent)), false));
        Assert.Equal(0, fixture.Session.PendingReliable);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Empty(fixture.Session.ClaimDueRetransmissions());
    }

    [Fact]
    public void DelayedClientAcknowledgementDoesNotExpireAnOtherwiseHealthyLocalTransport()
    {
        using var fixture = new Fixture();
        var first = Assert.Single(fixture.Session.Receive(fixture.Packet(MtuTest()), false));
        for (var second = 1; second <= 13; second++)
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(first, Assert.Single(fixture.Session.ClaimDueRetransmissions()));
            Assert.False(fixture.Session.Closed);
        }
        var position = 16;
        var sequence = EdServerControlMessageCodec.ReadSequenceBody(first, ref position);
        Assert.Empty(fixture.Session.Receive(fixture.Packet(Ack(sequence)), false));
        Assert.Equal(0, fixture.Session.PendingReliable);
        Assert.False(fixture.Session.Closed);
        Assert.Equal(2, fixture.Session.Receive(fixture.Packet(Seq(102), Id()), false).Count);
        Assert.Equal(1UL, fixture.Allocator.AllocatedBlockCount);
    }

    [Fact]
    public void UnresponsiveLocalTransportHasAFiniteDeliveryDeadlineAndExplicitDiagnostic()
    {
        using var fixture = new Fixture();
        Assert.Single(fixture.Session.Receive(fixture.Packet(MtuTest()), false));
        fixture.Clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Empty(fixture.Session.ClaimDueRetransmissions());
        Assert.True(fixture.Session.Closed);
        Assert.Equal(0, fixture.Session.PendingReliable);
        Assert.Equal("reliable-delivery-Expired", fixture.Session.LastControlFailure);
        Assert.Empty(fixture.Session.Receive(fixture.Packet(Seq(102), Id()), false));
        Assert.Equal(0UL, fixture.Allocator.AllocatedBlockCount);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 3)]
    public void SameReliableOperationCanPiggybackNewAckButDifferentOperationCannotCommitIt(bool framed, byte alignment)
    {
        using var fixture = new Fixture(framed, alignment);
        var original = fixture.Session.Receive(fixture.Packet(Seq(102), Id()), false);
        var p = 16;
        var responseSequence = EdServerControlMessageCodec.ReadSequenceBody(original[0], ref p);
        Assert.Equal(1, fixture.Session.PendingReliable);
        Assert.Empty(fixture.Session.Receive(fixture.Packet(Ack(responseSequence), Seq(102), MtuReply()), false));
        Assert.Equal(1, fixture.Session.PendingReliable); // Bad identity must not commit even a valid ACK.
        Assert.Null(fixture.Session.EstablishedMtu);
        var replay = fixture.Session.Receive(fixture.Packet(Ack(responseSequence), Seq(102), Id()), false);
        Assert.Equal(2, replay.Count);
        Assert.Equal(original[0], replay[0]);
        Assert.Equal(original[1], replay[1]);
        Assert.Equal(0, fixture.Session.PendingReliable);
        Assert.Equal(1UL, fixture.Allocator.AllocatedBlockCount);
    }

    [Fact]
    public void FutureAckOrTruncatedPayloadCannotCommitMtuOrAllocate()
    {
        using var fixture = new Fixture();
        Assert.Empty(fixture.Session.Receive(fixture.Packet(Ack(fixture.ServerSequence + 1), Seq(102), Id()), false));
        Assert.Equal(0UL, fixture.Allocator.AllocatedBlockCount);
        var truncated = fixture.Packet(Seq(102), MtuReply());
        Array.Resize(ref truncated, truncated.Length - 1);
        Assert.Empty(fixture.Session.Receive(truncated, false));
        Assert.Null(fixture.Session.EstablishedMtu);
        Assert.Equal(0, fixture.Session.PendingReliable);
    }

    [Fact]
    public void FullWindowRejectsAllocationWithoutConsumingIdsAndCombinedAckCanFreeIt()
    {
        using var fixture = new Fixture();
        ushort lastSent = 0;
        for (var i = 0; i < 16; i++)
        {
            var reply = Assert.Single(fixture.Session.Receive(fixture.Packet(MtuTest()), false));
            var p = 16;
            lastSent = EdServerControlMessageCodec.ReadSequenceBody(reply, ref p);
        }
        Assert.Equal(16, fixture.Session.PendingReliable);
        Assert.Empty(fixture.Session.Receive(fixture.Packet(Seq(102), Id()), false));
        Assert.Equal(0UL, fixture.Allocator.AllocatedBlockCount);
        Assert.Equal(2, fixture.Session.Receive(fixture.Packet(Ack(lastSent), Seq(102), Id()), false).Count);
        Assert.Equal(1UL, fixture.Allocator.AllocatedBlockCount);
        Assert.Equal(1, fixture.Session.PendingReliable);
    }

    [Fact]
    public void ExpiryClosesBeforeAnIdRequestCanAllocateAndCannotRestartSameControl()
    {
        using var fixture = new Fixture();
        Assert.Single(fixture.Session.Receive(fixture.Packet(MtuTest()), false));
        fixture.Clock.Advance(TimeSpan.FromSeconds(60));
        Assert.Empty(fixture.Session.Receive(fixture.Packet(Seq(102), Id()), false));
        Assert.True(fixture.Session.Closed);
        Assert.Equal(0UL, fixture.Allocator.AllocatedBlockCount);
        Assert.Equal(0, fixture.Session.PendingReliable);
        Assert.Empty(fixture.Session.Receive(EdServerControlMessageCodec.EncodeSyncLetter(Fixture.Sync), false));
    }

    [Fact]
    public void RevocationDiscardsPendingRetriesAndReplayCache()
    {
        using var fixture = new Fixture();
        var input = fixture.Packet(Seq(102), Id());
        Assert.Equal(2, fixture.Session.Receive(input, false).Count);
        fixture.Session.Revoke();
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Empty(fixture.Session.ClaimDueRetransmissions());
        Assert.Empty(fixture.Session.Receive(input, false));
        Assert.True(fixture.Session.Closed);
        Assert.Equal(0, fixture.Session.PendingReliable);
        Assert.Equal(1UL, fixture.Allocator.AllocatedBlockCount);
    }

    [Fact]
    public void NonzeroTailAfterSyncCannotCreateHandshake()
    {
        using var session = new LocalReplicationSession(new(IPAddress.Loopback, 1234), new(), "synthetic");
        var bytes = EdServerControlMessageCodec.EncodeSyncLetter(Fixture.Sync).Concat(new byte[] { 0xFF }).ToArray();
        Assert.Empty(session.Receive(bytes, false));
        Assert.Equal(EdServerHandshakeState.Disconnected, session.State);
    }

    private static (byte[], int) Ack(int sequence) => (EdServerControlMessageCodec.EncodeAckLetter(new((ushort)sequence, 0, 16), 11), 32);
    private static (byte[], int) Seq(ushort sequence)
    {
        var data = new byte[4]; var p = 0;
        EdServerControlMessageCodec.WriteSequenceLetter(data, ref p, sequence, 11);
        return (data, 16);
    }
    private static (byte[], int) Id() => (Type((byte)EdServerBuiltInLetter.IdAllocationRequest), 0);
    private static (byte[],int) Start(bool wrongAuthentication=false)
    {
        using var value=new EdServerStartServerAdmin([],"synthetic-machine"u8,
            wrongAuthentication?"wrong-auth"u8:"synthetic-auth"u8,[],false);
        var data=new byte[256];var p=0;EdServerControlMessageCodec.WriteLetterType(data,ref p,26,11);
        var beginning=p;EdServerAdminMessageCodec.WriteStartBody(data,ref p,value);
        return(data,p-beginning);
    }
    private static byte[] Type(byte type)
    {
        var data = new byte[2]; var p = 0;
        EdServerControlMessageCodec.WriteLetterType(data, ref p, type, 11);
        return data;
    }
    private static (byte[], int) MtuTest()
    {
        var data = new byte[6]; var p = 0;
        EdServerControlMessageCodec.WriteLetterType(data, ref p, (byte)EdServerBuiltInLetter.MtuTest, 11);
        EdServerMtuMessageCodec.WriteTest(data, ref p, 1360, []);
        return (data, 32);
    }
    private static (byte[], int) MtuReply()
    {
        var data = new byte[4]; var p = 0;
        EdServerControlMessageCodec.WriteLetterType(data, ref p, (byte)EdServerBuiltInLetter.MtuReply, 11);
        EdServerMtuMessageCodec.WriteReply(data, ref p, 1360);
        return (data, 16);
    }
    private static (byte[],int) Route()
    {
        var data=new byte[64];var p=0;EdServerControlMessageCodec.WriteLetterType(data,ref p,(byte)EdServerBuiltInLetter.Route,11);
        var beginning=p;void Bits(ulong value,int width){for(var index=0;index<width;index++,p++)if((value&(1UL<<index))!=0)data[p/8]|=(byte)(1<<(p%8));}
        void Text(string text){Bits((uint)text.Length,32);foreach(var value in System.Text.Encoding.ASCII.GetBytes(text))Bits(value,8);}
        Bits(123,48);Bits(0,1);Text("Comp");Bits(1,16);Text("IP4");Bits(0x7F000001,32);Bits(34567,16);Bits(1360,16);
        return(data,p-beginning);
    }
    private sealed class Fixture : IDisposable
    {
        internal static EdServerSyncMessage Sync => new(33837, 101, 0, 11, 0x3F800000, false, 0, 0x42A9,
            "synthetic"u8.ToArray(), 0x7F000001, 0, ReadOnlyMemory<byte>.Empty, false, true, 332841, 332841);
        public ManualClock Clock { get; } = new();
        public NativeObjectIdAllocator Allocator { get; } = new();
        public LocalReplicationSession Session { get; }
        public ushort ServerSequence { get; }
        private readonly bool framed;
        private readonly byte alignment;
        public Fixture(bool framed = false, byte alignment = 0,bool withApplication=false,Func<ulong,EdServerIp4Endpoint?>? resolveRoute=null)
        {
            (this.framed, this.alignment) = (framed, alignment);
            var app=withApplication?new LocalReplicationApplication(11,12,Allocator.AllocateBlock("management-fixture"),
                System.Security.Cryptography.SHA256.HashData("synthetic-machine"u8),
                System.Security.Cryptography.SHA256.HashData("synthetic-auth"u8)):null;
            Session = new(new(IPAddress.Loopback, 1234), Allocator, "local-control-fixture", Clock,application:app,resolveRoute:resolveRoute);
            var response = Assert.Single(Session.Receive(EdServerControlMessageCodec.EncodeSyncLetter(Sync with
                { LengthDelimited = framed, AlignmentMode = alignment }), false));
            var p = 0;
            ServerSequence = EdServerControlMessageCodec.ReadSyncLetter(response, ref p, 33837).Sequence;
            Assert.Empty(Session.Receive(Packet(Ack(ServerSequence)), false));
            Assert.Equal(EdServerHandshakeState.Established, Session.State);
        }
        public byte[] Packet(params (byte[] Data, int Bits)[] messages)
        {
            var result = new byte[512]; var p = 0;
            foreach (var (data, bits) in messages)
            {
                var source = 0;
                var type = EdServerControlMessageCodec.ReadLetterType(data, ref source, 11);
                EdServerControlMessageCodec.WriteLetterType(result, ref p, type, 11);
                var frame = EdServerLetterFramingCodec.BeginWrite(result, ref p, type, framed, alignment);
                for (var i = 0; i < bits; i++, source++, p++)
                    if ((data[source / 8] & (1 << (source % 8))) != 0) result[p / 8] |= (byte)(1 << (p % 8));
                EdServerLetterFramingCodec.CompleteWrite(result, ref p, frame);
            }
            return result[..((p + 7) / 8)];
        }
        public void Dispose() => Session.Dispose();
    }
    private sealed class ManualClock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        public void Advance(TimeSpan value) => timestamp += value.Ticks;
    }
}
