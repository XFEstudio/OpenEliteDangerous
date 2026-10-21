using System.Security.Cryptography;
using System.Text.Json;
using XFEEliteDangerous.NativeFormat.Replication;
using XFEEliteDangerous.Core;
using XFEEliteDangerous.Client;
using Xunit;
namespace XFEEliteDangerous.Tests;

public sealed class LocalReplicationApplicationTests
{
    [Theory][InlineData(false)][InlineData(true)]
    public void ReplaceableRpcStillRequiresControllerOwnershipAndWholeBatchCommit(bool oneWay)
    {
        const ulong block=NativeObjectIdAllocator.FirstReplicatedObjectBase;
        var calls=0;
        NativeRpcModuleReply? Handler(NativeRpcModuleRequest request)
        {
            calls++;Assert.Equal("primary",request.Role);Assert.Equal(12UL,request.ClientMachine);
            Assert.Equal(101UL,request.IslandId);Assert.Equal(102UL,request.WakeId);Assert.Equal(10477373803UL,request.CurrentSystem);
            if(request.Payload[0]!=2)return null;
            return new(1,true,oneWay?[]:[new(22,false,8,[3])]);
        }
        using var app=new LocalReplicationApplication(11,12,new(block,0),SHA256.HashData("synthetic-machine"u8),SHA256.HashData("synthetic-auth"u8),true,12,false,Handler,101,102);
        using var request=new LocalApplicationCommand(22,Call:new(block+2,false,8,[2]),AlignedCallValues:true);
        Assert.Throws<InvalidDataException>(()=>app.Prepare([request],block+4096,1360));Assert.Equal(0,calls);
        using var start=new EdServerStartServerAdmin([],"synthetic-machine"u8,"synthetic-auth"u8,[],false);
        using(var plan=app.Prepare([new(26,Start:start)],block+4096,1360))plan.Commit();
        using(var plan=app.Prepare([new(13,Request:new(block+1,block+4096))],block+4096,1360))plan.Commit();
        using(var plan=app.Prepare([new(15,Joined:new(block+1,[],[]))],block+4096,1360))plan.Commit();
        using var wrong=new LocalApplicationCommand(22,Call:new(block+3,false,8,[2]));
        Assert.Throws<InvalidDataException>(()=>app.Prepare([wrong],block+4096,1360,selectedSystemAddress:10477373803));Assert.Equal(0,calls);
        using var unknown=new LocalApplicationCommand(22,Call:new(block+2,false,8,[69]));
        Assert.Throws<NotSupportedException>(()=>app.Prepare([request,unknown],block+4096,1360,selectedSystemAddress:10477373803));
        using var plan2=app.Prepare([request],block+4096,1360,selectedSystemAddress:10477373803);
        if(oneWay)Assert.Empty(plan2.Emissions);
        else
        {
            var emission=Assert.Single(plan2.Emissions);var p=16;
            using var reply=EdServerCallEnvelopeCodec.ReadBody(emission.Payload,ref p,false);
            Assert.Equal(block+2,reply.ObjectReference);Assert.Equal(new byte[]{3},reply.CopyPayload());
        }
        plan2.Commit();Assert.False(JsonSerializer.SerializeToElement(app.Snapshot()).GetProperty("worldReady").GetBoolean());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyArenaCatalogueAllowsOtherMinimumSoloRepliesToCommitAtomically(bool dispatchFlag)
    {
        const ulong block=NativeObjectIdAllocator.FirstReplicatedObjectBase;
        const ulong address=0x0102030405060708;
        using var app=new LocalReplicationApplication(11,12,new(block,0),SHA256.HashData("synthetic-machine"u8),SHA256.HashData("synthetic-auth"u8),true,12);
        using var arena=new LocalApplicationCommand(22,Call:new(block+2,dispatchFlag,8,[70]));
        Assert.Throws<InvalidDataException>(()=>app.Prepare([arena],block+4096,1360));
        using var start=new EdServerStartServerAdmin([],"synthetic-machine"u8,"synthetic-auth"u8,[],false);
        using(var plan=app.Prepare([new(26,Start:start)],block+4096,1360))plan.Commit();
        using(var plan=app.Prepare([new(13,Request:new(block+1,block+4096))],block+4096,1360))plan.Commit();
        using(var plan=app.Prepare([new(15,Joined:new(block+1,[],[]))],block+4096,1360))plan.Commit();
        using var postgen=new LocalApplicationCommand(22,Call:new(block+2,dispatchFlag,72,Convert.FromHexString("420807060504030201")));
        using var query=new LocalApplicationCommand(23,Call:new(block+2,dispatchFlag,88,Convert.FromHexString("0D78563412060504030201")));
        using var signal=new LocalApplicationCommand(23,Call:new(block+2,dispatchFlag,8,[15]));
        using var unknown=new LocalApplicationCommand(22,Call:new(block+2,true,8,[69]));
        Assert.Throws<NotSupportedException>(()=>app.Prepare([arena,postgen,query,unknown],block+4096,1360,selectedSystemAddress:address));
        Assert.Equal(0,app.EmptyArenaCatalogueReplies);Assert.Equal(0,app.StellarForgeNoOverridesReplies);Assert.Equal(0,app.UnavailablePeerInfoReplies);
        using(var disposed=app.Prepare([arena,postgen,query],block+4096,1360,selectedSystemAddress:address))Assert.Equal(3,disposed.Emissions.Count);
        Assert.Equal(0,app.EmptyArenaCatalogueReplies);
        using(var plan=app.Prepare([arena,postgen,query,signal],block+4096,1360,selectedSystemAddress:address))
        {
            var p=16;
            using var reply=EdServerCallEnvelopeCodec.ReadBody(plan.Emissions[0].Payload,ref p,false);
            Assert.Equal(block+2,reply.ObjectReference);
            EdServerArenaCatalogueMessageCodec.ReadEmptyReply(reply);plan.Commit();
        }
        Assert.Equal(1,app.EmptyArenaCatalogueReplies);Assert.Equal(1,app.StellarForgeNoOverridesReplies);Assert.Equal(1,app.UnavailablePeerInfoReplies);
        Assert.Equal(1u,JsonSerializer.SerializeToElement(app.Snapshot()).GetProperty("clientNotificationReceipts").GetUInt32());
        Assert.False(JsonSerializer.SerializeToElement(app.Snapshot()).GetProperty("worldReady").GetBoolean());
    }

    [Fact]
    public void MinimumSoloNotificationsAndExplicitUnavailablePeerLookupDoNotInventWorldState()
    {
        const ulong block=NativeObjectIdAllocator.FirstReplicatedObjectBase;
        using var app=new LocalReplicationApplication(11,12,new(block,0),SHA256.HashData("synthetic-machine"u8),SHA256.HashData("synthetic-auth"u8),true,12);
        using var start=new EdServerStartServerAdmin([],"synthetic-machine"u8,"synthetic-auth"u8,[],false);
        using var notification=new LocalApplicationCommand(23,Call:new(block+2,true,8,[15]));
        Assert.Throws<InvalidDataException>(()=>app.Prepare([notification],block+4096,1360));
        using(var plan=app.Prepare([new(26,Start:start)],block+4096,1360))plan.Commit();
        using(var plan=app.Prepare([new(13,Request:new(block+1,block+4096))],block+4096,1360))plan.Commit();
        using(var plan=app.Prepare([new(15,Joined:new(block+1,[],[]))],block+4096,1360))plan.Commit();
        using var registration=new LocalApplicationCommand(22,Call:new(block+2,true,72,[35,12,0,0,0,0,0,0,0]));
        using var query=new LocalApplicationCommand(23,Call:new(block+2,true,88,Convert.FromHexString("0D78563412060504030201")));
        using var unknown=new LocalApplicationCommand(23,Call:new(block+2,true,8,[46]));
        Assert.Throws<NotSupportedException>(()=>app.Prepare([notification,registration,query,unknown],block+4096,1360));
        Assert.Equal(0,app.UnavailablePeerInfoReplies);
        Assert.Equal(0u,JsonSerializer.SerializeToElement(app.Snapshot()).GetProperty("clientNotificationReceipts").GetUInt32());
        using(var plan=app.Prepare([notification,registration,query],block+4096,1360))
        {
            var emission=Assert.Single(plan.Emissions);var p=16;
            using var reply=EdServerCallEnvelopeCodec.ReadBody(emission.Payload,ref p,false);
            Assert.Equal(0x12345678u,EdServerPeerInfoMessageCodec.ReadUnavailableReply(reply));plan.Commit();
        }
        var snapshot=JsonSerializer.SerializeToElement(app.Snapshot());
        Assert.Equal(5u,snapshot.GetProperty("clientNotificationReceipts").GetUInt32());
        Assert.Equal(1,app.UnavailablePeerInfoReplies);Assert.False(snapshot.GetProperty("worldReady").GetBoolean());
    }

    [Fact]
    public void StellarForgeReplyIsBoundToFreshSelectedSystemAndCommitsWithItsWholeBatch()
    {
        const ulong block=NativeObjectIdAllocator.FirstReplicatedObjectBase;
        const ulong address=0x0102030405060708;
        using var app=new LocalReplicationApplication(11,12,new(block,0),SHA256.HashData("synthetic-machine"u8),SHA256.HashData("synthetic-auth"u8),true,12);
        using var start=new EdServerStartServerAdmin([],"synthetic-machine"u8,"synthetic-auth"u8,[],false);
        using(var plan=app.Prepare([new(26,Start:start)],block+4096,1360))plan.Commit();
        using(var plan=app.Prepare([new(13,Request:new(block+1,block+4096))],block+4096,1360))plan.Commit();
        using(var plan=app.Prepare([new(15,Joined:new(block+1,[],[]))],block+4096,1360))plan.Commit();
        using var call=new LocalApplicationCommand(22,Call:new(block+2,true,72,Convert.FromHexString("420807060504030201")));
        Assert.Throws<InvalidDataException>(()=>app.Prepare([call],block+4096,1360));
        Assert.Throws<InvalidDataException>(()=>app.Prepare([call],block+4096,1360,selectedSystemAddress:address+1));
        using(var uncommitted=app.Prepare([call],block+4096,1360,selectedSystemAddress:address))Assert.Single(uncommitted.Emissions);
        Assert.Equal(0,app.StellarForgeNoOverridesReplies);
        using var unknown=new LocalApplicationCommand(23,Call:new(block+2,true,8,[46]));
        Assert.Throws<NotSupportedException>(()=>app.Prepare([call,unknown],block+4096,1360,selectedSystemAddress:address));
        Assert.Equal(0,app.StellarForgeNoOverridesReplies);
        using(var plan=app.Prepare([call],block+4096,1360,selectedSystemAddress:address))
        {
            var emission=Assert.Single(plan.Emissions);Assert.True(emission.Reliable);
            var p=16;
            using var response=EdServerCallEnvelopeCodec.ReadBody(emission.Payload,ref p,false);
            Assert.Equal(block+2,response.ObjectReference);
            Assert.Equal(address,EdServerStellarForgeMessageCodec.ReadNoOverridesReply(response));
            plan.Commit();
        }
        Assert.Equal(1,app.StellarForgeNoOverridesReplies);
        Assert.False(JsonSerializer.SerializeToElement(app.Snapshot()).GetProperty("worldReady").GetBoolean());
    }

    [Fact]
    public void SourceConfirmedEmptyMethodDoesNotRejectAValidJoinedControlBatch()
    {
        const ulong block=NativeObjectIdAllocator.FirstReplicatedObjectBase;
        using var app=new LocalReplicationApplication(11,12,new(block,0),SHA256.HashData("synthetic-machine"u8),SHA256.HashData("synthetic-auth"u8),true,12);
        using var call=new LocalApplicationCommand(23,Call:new(block+2,true,8,[47]));
        Assert.Throws<InvalidDataException>(()=>app.Prepare([call],block+4096,1360));
        using var start=new EdServerStartServerAdmin([],"synthetic-machine"u8,"synthetic-auth"u8,[],false);
        using(var plan=app.Prepare([new(26,Start:start)],block+4096,1360))plan.Commit();
        using(var plan=app.Prepare([new(13,Request:new(block+1,block+4096))],block+4096,1360))plan.Commit();
        using(var plan=app.Prepare([new(15,Joined:new(block+1,[],[]))],block+4096,1360))plan.Commit();
        var salute=new LocalApplicationCommand(32,Salute:new(block+2,true,true));
        using(var plan=app.Prepare([salute,call],block+4096,1360)){Assert.Empty(plan.Emissions);plan.Commit();}
        using var snapshot=JsonDocument.Parse(JsonSerializer.Serialize(app.Snapshot()));
        Assert.True(snapshot.RootElement.GetProperty("controllerSaluted").GetBoolean());
        Assert.False(snapshot.RootElement.GetProperty("worldReady").GetBoolean());
        using var unknown=new LocalApplicationCommand(23,Call:new(block+2,true,8,[46]));
        using var extra=new LocalApplicationCommand(23,Call:new(block+2,true,16,[47,0]));
        Assert.Throws<NotSupportedException>(()=>app.Prepare([unknown],block+4096,1360));
        Assert.Throws<NotSupportedException>(()=>app.Prepare([extra],block+4096,1360));
    }
    [Fact]public void RestartedManagementApplicationHasFreshReferencesAndRetainsOnlyItsPrivateIdentity()
    {
        const ulong block=NativeObjectIdAllocator.FirstReplicatedObjectBase;
        using var old=new LocalReplicationApplication(11,12,new(block,0),SHA256.HashData("synthetic-machine"u8),SHA256.HashData("synthetic-auth"u8),true,12);
        using var start=new EdServerStartServerAdmin([],"synthetic-machine"u8,"synthetic-auth"u8,[],false);
        using(var plan=old.Prepare([new(26,Start:start)],block+4096,1360))plan.Commit();
        using var fresh=old.Restart(new(block+8192,1));
        Assert.Equal(EdServerSessionPeerState.None,fresh.PeerState);Assert.False(fresh.RecognizesController(block+2));
        Assert.Equal("controller",fresh.CallReferenceKind(block+8194));Assert.NotEqual("controller",fresh.CallReferenceKind(block+2));
        using(var result=fresh.Prepare([new(26,Start:start)],block+12288,1360))result.Commit();
        Assert.Equal(1,fresh.AcceptedStarts);Assert.Equal(1,old.AcceptedStarts);
        Assert.Throws<InvalidDataException>(()=>fresh.Prepare([new(15,Joined:new(block+1,[],[]))],block+12288,1360));
    }
    [Theory]
    [InlineData(true,false)] [InlineData(false,true)]
    public void WrongCredentialsCannotPrepareOrBind(bool wrongMachine,bool wrongAuth)
    {
        using var app=Create();using var start=new EdServerStartServerAdmin([],wrongMachine?[1]:"synthetic-machine"u8,wrongAuth?[1]:"synthetic-auth"u8,[],false);
        Assert.Throws<InvalidDataException>(()=>app.Prepare([new(26,Start:start)],8192,1360));Assert.Equal(0,app.AcceptedStarts);
        Assert.Equal(EdServerSessionPeerState.None,app.PeerState);
    }
    [Fact]
    public void CandidateDoesNotCommitOnDisposalOrMalformedLaterSessionCommand()
    {
        using var app=Create();using var start=new EdServerStartServerAdmin([],"synthetic-machine"u8,"synthetic-auth"u8,[],false);
        using(var uncommitted=app.Prepare([new(26,Start:start)],8192,1360))Assert.Single(uncommitted.Emissions);
        Assert.Equal(0,app.AcceptedStarts);
        using(var accepted=app.Prepare([new(26,Start:start)],8192,1360))accepted.Commit();
        Assert.Equal(1,app.AcceptedStarts);
        Assert.Throws<InvalidDataException>(()=>app.Prepare([new(13,Request:new(4097,8192)),new(15,Joined:new(4098,[],[]))],8192,1360));
        Assert.Equal(EdServerSessionPeerState.None,app.PeerState);
        using(var join=app.Prepare([new(13,Request:new(4097,8192))],8192,1360)){Assert.Equal(2,join.Emissions.Count);join.Commit();}
        Assert.Equal(EdServerSessionPeerState.AwaitingJoined,app.PeerState);
        using(var finish=app.Prepare([new(15,Joined:new(4097,[],[]))],8192,1360))finish.Commit();
        Assert.Equal(EdServerSessionPeerState.Joined,app.PeerState);
        Assert.DoesNotContain("synthetic",JsonSerializer.Serialize(app.Snapshot()));
    }
    [Fact]
    public void MissingAllocationInvalidMtuOrUnsupportedRoleCannotBindAndStalePlanCannotCommit()
    {
        using var app=Create();using var start=new EdServerStartServerAdmin([],"synthetic-machine"u8,"synthetic-auth"u8,[],false);
        Assert.Throws<InvalidDataException>(()=>app.Prepare([new(26,Start:start)],null,1360));
        Assert.Throws<InvalidDataException>(()=>app.Prepare([new(26,Start:start)],8192,575));
        using var missions=new EdServerStartServerAdmin([],"synthetic-machine"u8,"synthetic-auth"u8,[],true);
        Assert.Throws<NotSupportedException>(()=>app.Prepare([new(26,Start:missions)],8192,1360));
        using var first=app.Prepare([new(26,Start:start)],8192,1360);using var stale=app.Prepare([new(26,Start:start)],8192,1360);
        first.Commit();Assert.Throws<InvalidOperationException>(()=>stale.Commit());Assert.Equal(1,app.AcceptedStarts);
    }
    [Fact]
    public void AuthenticatedManagementStartCanPrecedeTheMtuReply()
    {
        using var app=Create();using var start=new EdServerStartServerAdmin([],"synthetic-machine"u8,"synthetic-auth"u8,[],false);
        using var plan=app.Prepare([new(26,Start:start)],8192,null);
        Assert.Single(plan.Emissions);Assert.Equal(0,app.AcceptedStarts);
        plan.Commit();Assert.Equal(1,app.AcceptedStarts);
        Assert.Equal(EdServerSessionPeerState.None,app.PeerState);
    }
    private static LocalReplicationApplication Create()=>new(11,12,new(4096,0),SHA256.HashData("synthetic-machine"u8),SHA256.HashData("synthetic-auth"u8));
    [Fact]public void ScalarNotificationRequiresOwnershipAndItsReceiptRollsBackWithTheWholeBatch()
    {
        const ulong block=NativeObjectIdAllocator.FirstReplicatedObjectBase;
        using var app=new LocalReplicationApplication(11,12,new(block,0),SHA256.HashData("synthetic-machine"u8),SHA256.HashData("synthetic-auth"u8),true,12);
        using var notification=new LocalApplicationCommand(22,Call:new(block+2,false,40,[128,7,0,0,0]));
        Assert.Throws<InvalidDataException>(()=>app.Prepare([notification],block+4096,1360));
        using var start=new EdServerStartServerAdmin([],"synthetic-machine"u8,"synthetic-auth"u8,[],false);
        using(var plan=app.Prepare([new(26,Start:start)],block+4096,1360))plan.Commit();
        using(var plan=app.Prepare([new(13,Request:new(block+1,block+4096))],block+4096,1360))plan.Commit();
        using(var plan=app.Prepare([new(15,Joined:new(block+1,[],[]))],block+4096,1360))plan.Commit();
        using var unknown=new LocalApplicationCommand(23,Call:new(block+2,false,8,[7]));
        Assert.Throws<NotSupportedException>(()=>app.Prepare([notification,unknown],block+4096,1360));
        using(var snapshot=JsonDocument.Parse(JsonSerializer.Serialize(app.Snapshot())))Assert.Equal(JsonValueKind.Null,snapshot.RootElement.GetProperty("clientScalar128").ValueKind);
        using(var plan=app.Prepare([notification],block+4096,1360)){Assert.Empty(plan.Emissions);plan.Commit();}
        using(var snapshot=JsonDocument.Parse(JsonSerializer.Serialize(app.Snapshot())))
        {Assert.Equal(7u,snapshot.RootElement.GetProperty("clientScalar128").GetUInt32());Assert.False(snapshot.RootElement.GetProperty("worldReady").GetBoolean());}
    }
    [Fact]
    public void UnsolicitedReplicationAckCannotStartApplicationOrEnableControllerMode()
    {
        using var app=Create();var ack=new LocalApplicationCommand(19,StateAck:new(4098,false,true,1));
        Assert.Throws<InvalidDataException>(()=>app.Prepare([ack],8192,1360));Assert.Equal(0,app.AcceptedStarts);
        using var start=new EdServerStartServerAdmin([],"synthetic-machine"u8,"synthetic-auth"u8,[],false);
        using(var plan=app.Prepare([new(26,Start:start)],8192,1360))plan.Commit();
        Assert.Throws<InvalidDataException>(()=>app.Prepare([ack],8192,1360));
        using var snapshot=JsonDocument.Parse(JsonSerializer.Serialize(app.Snapshot()));
        Assert.False(snapshot.RootElement.GetProperty("controllerStateAcknowledged").GetBoolean());
        Assert.False(snapshot.RootElement.GetProperty("controllerLiveAcknowledged").GetBoolean());
        Assert.False(snapshot.RootElement.GetProperty("worldReady").GetBoolean());
    }
    [Fact]
    public void ControllerCreationIsTransactionalNamedOwnedAndOnceAfterJoined()
    {
        const ulong block=NativeObjectIdAllocator.FirstReplicatedObjectBase;
        using var app=new LocalReplicationApplication(11,12,new(block,0),SHA256.HashData("synthetic-machine"u8),SHA256.HashData("synthetic-auth"u8),true,12);
        using var start=new EdServerStartServerAdmin([],"synthetic-machine"u8,"synthetic-auth"u8,[],false);
        using(var plan=app.Prepare([new(26,Start:start)],block+4096,1360))plan.Commit();
        using(var plan=app.Prepare([new(13,Request:new(block+1,block+4096))],block+4096,1360))
        {
            var p=16;var advertised=EdServerSessionMessageCodec.ReadJoinedBody(plan.Emissions[1].Payload,ref p);
            Assert.Equal(new[]{block+2},advertised.FirstObjectIds);Assert.Empty(advertised.SecondObjectIds);plan.Commit();
        }
        var joined=new LocalApplicationCommand(15,Joined:new(block+1,[],[]));
        using(var discarded=app.Prepare([joined],block+4096,1360))Assert.Equal(2,discarded.Emissions.Count);
        Assert.Equal(EdServerSessionPeerState.AwaitingJoined,app.PeerState);
        using(var plan=app.Prepare([joined],block+4096,1360))
        {
            Assert.Equal(2,plan.Emissions.Count);var emission=plan.Emissions[0];var p=0;
            Assert.Equal((byte)17,EdServerControlMessageCodec.ReadLetterType(emission.Payload,ref p,16));
            var create=EdServerObjectCreateCodec.ReadLeafBody(emission.Payload,ref p);
            Assert.Equal(block+2,create.ObjectReference);Assert.Equal(EdServerObjectCreateCodec.ClientRootReference,create.ParentObjectReference);
            Assert.True(create.GoLiveWhenReady);Assert.Equal(new(1,"ServerAdmin"),create.Factory);
            Assert.Equal(new[]{block+1},create.Dependencies);Assert.Equal((ulong)12,create.OwnerMachineReference);Assert.Equal((ushort)64655,create.Version);
            p=0;var stateBytes=plan.Emissions[1].Payload;
            Assert.Equal((byte)18,EdServerControlMessageCodec.ReadLetterType(stateBytes,ref p,16));
            Assert.Equal(new(block+2,64655,1,0),EdServerObjectReplicationCodec.ReadDefaultBody(stateBytes,ref p,0));plan.Commit();
        }
        using(var duplicate=app.Prepare([joined],block+4096,1360)){Assert.Empty(duplicate.Emissions);duplicate.Commit();}
        var salute=new LocalApplicationCommand(32,Salute:new(block+2,true,true));
        using var pendingCall=new LocalApplicationCommand(23,Call:new(block+2,false,8,[7]));
        Assert.Throws<NotSupportedException>(()=>app.Prepare([salute,pendingCall],block+4096,1360));
        Assert.Throws<InvalidDataException>(()=>app.Prepare([salute with{Salute=salute.Salute! with{ObjectReference=block+3}}],block+4096,1360));
        using(var discarded=app.Prepare([salute],block+4096,1360))Assert.Empty(discarded.Emissions);
        using(var before=JsonDocument.Parse(JsonSerializer.Serialize(app.Snapshot())))Assert.False(before.RootElement.GetProperty("controllerSaluted").GetBoolean());
        using(var plan=app.Prepare([salute],block+4096,1360))plan.Commit();
        var ack=new LocalApplicationCommand(19,StateAck:new(block+2,false,false,1));
        Assert.Throws<InvalidDataException>(()=>app.Prepare([ack with{StateAck=ack.StateAck! with{ObjectReference=block+3}}],block+4096,1360));
        Assert.Throws<InvalidDataException>(()=>app.Prepare([ack with{StateAck=ack.StateAck! with{Sequence=2}}],block+4096,1360));
        Assert.Throws<InvalidDataException>(()=>app.Prepare([ack with{StateAck=ack.StateAck! with{SecondaryArchive=true}}],block+4096,1360));
        using(var discarded=app.Prepare([ack],block+4096,1360))Assert.Empty(discarded.Emissions);
        using(var before=JsonDocument.Parse(JsonSerializer.Serialize(app.Snapshot())))Assert.False(before.RootElement.GetProperty("controllerStateAcknowledged").GetBoolean());
        using(var plan=app.Prepare([ack],block+4096,1360))plan.Commit();
        using(var after=JsonDocument.Parse(JsonSerializer.Serialize(app.Snapshot())))
        {Assert.True(after.RootElement.GetProperty("controllerStateAcknowledged").GetBoolean());Assert.False(after.RootElement.GetProperty("controllerLiveAcknowledged").GetBoolean());}
        using(var plan=app.Prepare([ack with{StateAck=ack.StateAck! with{Live=true}}],block+4096,1360))plan.Commit();
        using var metadata=JsonDocument.Parse(JsonSerializer.Serialize(app.Snapshot()));
        Assert.True(metadata.RootElement.GetProperty("controllerCreationQueued").GetBoolean());
        Assert.True(metadata.RootElement.GetProperty("controllerInitialStateQueued").GetBoolean());
        Assert.True(metadata.RootElement.GetProperty("controllerLiveAcknowledged").GetBoolean());
        Assert.True(metadata.RootElement.GetProperty("controllerSaluteLive").GetBoolean());
        Assert.False(metadata.RootElement.GetProperty("worldReady").GetBoolean());
    }
    [Fact]
    public void ControllerCreationRejectsNativeSpecialObjectDomains()
        =>Assert.Throws<ArgumentException>(()=>new LocalReplicationApplication(11,12,new(4096,0),new byte[32],new byte[32],true));
    [Fact]
    public void ClockRequiresJoinedExactControllerAndIsTransactionalWithUnknownMethods()
    {
        const ulong block=NativeObjectIdAllocator.FirstReplicatedObjectBase;
        using var app=new LocalReplicationApplication(11,12,new(block,0),SHA256.HashData("synthetic-machine"u8),SHA256.HashData("synthetic-auth"u8),true,12);
        using var request=new EdServerCallEnvelope(block+2,true,104,[20,1,0,0,0,2,0,0,0,0,0,0,0]);
        var call=new LocalApplicationCommand(20,Call:request);
        Assert.Throws<InvalidDataException>(()=>app.Prepare([call],block+4096,1360,new(3,0),new(3,1)));
        using var start=new EdServerStartServerAdmin([],"synthetic-machine"u8,"synthetic-auth"u8,[],false);
        using(var plan=app.Prepare([new(26,Start:start)],block+4096,1360))plan.Commit();
        using(var plan=app.Prepare([new(13,Request:new(block+1,block+4096))],block+4096,1360))plan.Commit();
        using(var plan=app.Prepare([new(15,Joined:new(block+1,[],[]))],block+4096,1360))plan.Commit();
        using var terrain=new LocalApplicationCommand(23,Call:new(block+2,true,16,[94,1]));
        using(var uncommitted=app.Prepare([terrain],block+4096,1360))Assert.Empty(uncommitted.Emissions);
        using(var snapshot=JsonDocument.Parse(JsonSerializer.Serialize(app.Snapshot())))Assert.Equal(JsonValueKind.Null,snapshot.RootElement.GetProperty("terrainCapability").ValueKind);
        using(var discarded=app.Prepare([call],block+4096,1360,new(3,0),new(3,1)))Assert.Single(discarded.Emissions);
        Assert.Equal(0,app.ClockReplies);
        using var unknown=new LocalApplicationCommand(23,Call:new(block+2,false,8,[7]));
        Assert.Throws<NotSupportedException>(()=>app.Prepare([terrain,unknown],block+4096,1360));
        using(var plan=app.Prepare([terrain],block+4096,1360))plan.Commit();
        using(var snapshot=JsonDocument.Parse(JsonSerializer.Serialize(app.Snapshot())))Assert.True(snapshot.RootElement.GetProperty("terrainCapability").GetBoolean());
        Assert.Throws<NotSupportedException>(()=>app.Prepare([call,unknown],block+4096,1360,new(3,0),new(3,1)));
        Assert.Equal(0,app.ClockReplies);
        using(var plan=app.Prepare([call],block+4096,1360,new(3,0),new(3,1)))
        {
            var emission=Assert.Single(plan.Emissions);Assert.False(emission.Reliable);var p=0;
            Assert.Equal(20,EdServerControlMessageCodec.ReadLetterType(emission.Payload,ref p,16));
            using var response=EdServerCallEnvelopeCodec.ReadBody(emission.Payload,ref p,false);
            Assert.Equal((new EdServerNetworkTime(2,1),new EdServerNetworkTime(3,0),new EdServerNetworkTime(3,1)),EdServerClockMessageCodec.ReadReply(response));
            plan.Commit();
        }
        Assert.Equal(1,app.ClockReplies);
        using var register=new LocalApplicationCommand(22,Call:new(block+2,false,80,[33,1,12,0,0,0,0,0,0,0]));
        using var wrongProfile=new LocalApplicationCommand(22,Call:new(block+2,false,80,[34,1,13,0,0,0,0,0,0,0]));
        Assert.Throws<InvalidDataException>(()=>app.Prepare([register,wrongProfile],block+4096,1360));
        using(var snapshot=JsonDocument.Parse(JsonSerializer.Serialize(app.Snapshot())))Assert.Equal(JsonValueKind.Null,snapshot.RootElement.GetProperty("profileRegistration33").ValueKind);
        using(var plan=app.Prepare([register],block+4096,1360)){Assert.Empty(plan.Emissions);plan.Commit();}
        var fullFixedState=new byte[78];fullFixedState[3]=0x0E;var stateBit=476;
        foreach(var index in new[]{451,452})
        {
            Put(fullFixedState,ref stateBit,(ulong)index,9);Put(fullFixedState,ref stateBit,32,32);Put(fullFixedState,ref stateBit,0,32);
        }
        using var update=new LocalApplicationCommand(18,State:new(block+2,true,64655,1,stateBit,fullFixedState));
        Assert.Throws<NotSupportedException>(()=>app.Prepare([update,unknown],block+4096,1360));
        using(var snapshot=JsonDocument.Parse(JsonSerializer.Serialize(app.Snapshot())))Assert.Equal(0,snapshot.RootElement.GetProperty("secondaryArchiveStates").GetInt32());
        using(var plan=app.Prepare([update],block+4096,1360))
        {
            var emission=Assert.Single(plan.Emissions);Assert.False(emission.Reliable);var p=16;
            Assert.Equal(new(block+2,true,true,1),EdServerObjectReplicationCodec.ReadAcknowledgementBody(emission.Payload,ref p));plan.Commit();
        }
        using(var snapshot=JsonDocument.Parse(JsonSerializer.Serialize(app.Snapshot())))
        {Assert.Equal(1,snapshot.RootElement.GetProperty("secondaryArchiveStates").GetInt32());Assert.True(snapshot.RootElement.GetProperty("profileRegistration33").GetBoolean());}

        static void Put(byte[] bytes,ref int bit,ulong value,int bits)
        {for(var i=0;i<bits;i++,bit++)if((value>>i&1)!=0)bytes[bit/8]|=(byte)(1<<(bit%8));}
    }

    [Theory]
    [InlineData((byte)18)]
    [InlineData((byte)33)]
    public void ReplicationDeliveryClassesShareTheBodyCodecAndKeepArchiveIndependent(byte letterType)
    {
        foreach(var secondary in new[]{false,true})
        {
            var bytes=new byte[32];var p=3;
            using var state=new EdServerObjectUpdate(0x200000002UL,secondary,64655,1,10,[0,0]);
            EdServerObjectUpdateCodec.WriteBody(bytes,ref p,state);var end=p;p=3;
            using var command=LocalApplicationCommand.Read(letterType,bytes,ref p);
            Assert.Equal(end,p);Assert.Equal(letterType,command.Type);Assert.Equal(secondary,command.State!.SecondaryArchive);
            Assert.Equal(state.ObjectReference,command.State.ObjectReference);Assert.Equal(10,command.State.PayloadBits);
        }
    }
}
