using System.Buffers.Binary;
using XFEEliteDangerous.Client;
using XFEEliteDangerous.Core;
using XFEEliteDangerous.NativeFormat.Replication;
using Xunit;

namespace XFEEliteDangerous.Tests;

public sealed class MultiplayerTests
{
    [Fact]public void ExchangeSerializationPreservesAnAbsentBodyInsteadOfAnEmptyBase64String()
    {
        var request=new GameLinkRequest("exchange",Datagrams:[]);
        var bytes=System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(request,GameLinkJson.Default.GameLinkRequest);
        var decoded=System.Text.Json.JsonSerializer.Deserialize(bytes,GameLinkJson.Default.GameLinkRequest)!;
        Assert.Null(decoded.Body);Assert.Null(decoded.Method);Assert.Null(decoded.Fields);Assert.Empty(decoded.Datagrams!);
    }
    private static NativeRpcModuleRequest Query(ulong client,ulong system=42)
    {
        var payload=new byte[153];payload[0]=2;BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(1),17);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(5),system);return new(1,"primary",22,false,true,1224,payload,system,client,1,2);
    }
    [Fact]public void MatchingPlayersShareInstancesAndReceiveOnlyOtherMembers()
    {
        using var world=new SharedWorldDirectory();world.Register(101,1);world.Register(102,2);world.Register(103,3);
        Assert.Single(world.Handle(101,Query(101))!.Emissions);world.Complete(101,true);var second=Assert.Single(world.Handle(102,Query(102))!.Emissions);world.Complete(102,true);
        Assert.Equal(392,second.PayloadBits); //343 + one count exponent bit + one U48 reference
        world.Handle(103,Query(103,99));world.Complete(103,true);var members=world.Snapshot();
        Assert.Equal(members[0].IslandId,members[1].IslandId);Assert.NotEqual(members[0].IslandId,members[2].IslandId);
        Assert.NotNull(world.Resolve(101,102));Assert.NotNull(world.Resolve(101,101));Assert.Null(world.Resolve(101,103));
        world.Leave(102);Assert.Null(world.Resolve(101,102));
    }
    [Fact]public void OwnDirectoryIsAvailableBeforeAdmissionWithoutGrantingAnotherPlayersRoute()
    {
        using var world=new SharedWorldDirectory();world.Register(101,1);world.Register(102,2);
        var own=world.Resolve(101,101)!;Assert.Equal(101UL,own.Identity);Assert.Equal(1U,own.NativeUserId);
        Assert.Null(world.Resolve(101,102));Assert.Null(world.Resolve(102,101));
        var otherPort=world.Snapshot().Single(peer=>peer.RunId==102).RoutePort;
        var bytes=new byte[8];new EdServerDatagramHeader(101,false,1).Write(bytes);
        Assert.Throws<InvalidDataException>(()=>world.Route(101,new(45000,otherPort,bytes)));
    }
    [Fact]public void TheLastDepartureRetiresTheInstanceButNotAnotherMembersLiveRoom()
    {
        using var world=new SharedWorldDirectory();world.Register(101,1);world.Register(102,2);
        world.Handle(101,Query(101));world.Complete(101,true);world.Handle(102,Query(102));world.Complete(102,true);
        var original=world.Snapshot().Single(peer=>peer.RunId==101).IslandId;
        world.Leave(101);world.Handle(101,Query(101));world.Complete(101,true);
        Assert.Equal(original,world.Snapshot().Single(peer=>peer.RunId==101).IslandId);
        world.Leave(101);world.Leave(102);
        world.Handle(101,Query(101));world.Complete(101,true);world.Handle(102,Query(102));world.Complete(102,true);
        var current=world.Snapshot();Assert.NotEqual(original,current[0].IslandId);Assert.Equal(current[0].IslandId,current[1].IslandId);
    }
    [Fact]public void APreparedMemberKeepsTheRoomIdentityUntilItsReliableBatchCompletes()
    {
        using var world=new SharedWorldDirectory();world.Register(101,1);world.Register(102,2);world.Register(103,3);
        world.Handle(101,Query(101));world.Complete(101,true);
        var original=world.Snapshot().Single(peer=>peer.RunId==101).IslandId;
        world.Handle(102,Query(102));world.Remove(101);world.Complete(102,true);
        Assert.Equal(original,world.Snapshot().Single(peer=>peer.RunId==102).IslandId);
        world.Leave(102);world.Handle(103,Query(103));world.Complete(103,false);
        world.Handle(102,Query(102));world.Complete(102,true);
        Assert.NotEqual(original,world.Snapshot().Single(peer=>peer.RunId==102).IslandId);
    }
    [Fact]public void PeerPacketsKeepTheirBytesAndCannotCrossRoomsOrSpoofAnotherSender()
    {
        using var world=new SharedWorldDirectory();world.Register(101,1);world.Register(102,2);world.Register(103,3);
        world.Handle(101,Query(101));world.Complete(101,true);world.Handle(102,Query(102));world.Complete(102,true);world.Handle(103,Query(103,99));world.Complete(103,true);
        world.BindChannel(101,45000);world.BindChannel(102,45001);world.BindChannel(103,45002);
        var second=world.Resolve(101,102)!;var port=EdServerIp4Endpoint.Parse(second.ConnectionDetails).Port;
        var bytes=new byte[20];new EdServerDatagramHeader(101,false,7).Write(bytes);bytes[10]=97;
        world.Route(101,new(45000,port,bytes));var delivered=Assert.Single(world.Drain(102));
        Assert.Equal(45001,delivered.ClientPort);Assert.Equal(bytes,delivered.Payload);Assert.Empty(world.Drain(102));
        Assert.Throws<InvalidDataException>(()=>world.Route(103,new(45002,port,bytes)));
        new EdServerDatagramHeader(103,false,8).Write(bytes);Assert.Throws<InvalidDataException>(()=>world.Route(101,new(45000,port,bytes)));
        world.Remove(102);Assert.Null(world.Resolve(101,102));
        new EdServerDatagramHeader(101,false,9).Write(bytes);world.Route(101,new(45000,port,bytes));Assert.Empty(world.Drain(101));
    }
    [Fact]public void InstanceAdmissionRequiresTheAuthenticatedIdentityAndKnownSystem()
    {
        using var world=new SharedWorldDirectory();world.Register(101,1);
        Assert.Throws<InvalidDataException>(()=>world.Handle(101,Query(102)));
        Assert.Throws<InvalidDataException>(()=>world.Handle(101,Query(101) with{CurrentSystem=99}));
        Assert.Throws<InvalidOperationException>(()=>world.Register(101,1));Assert.DoesNotContain(world.Snapshot(),member=>member.IslandId is not null);
    }
    [Fact]public void ARejectedReliableBatchCannotPublishTentativeInstanceMembership()
    {
        using var world=new SharedWorldDirectory();world.Register(101,1);world.Register(102,2);
        world.Handle(101,Query(101));Assert.Null(Assert.Single(world.Snapshot(),peer=>peer.RunId==101).IslandId);
        world.Complete(101,false);world.Handle(102,Query(102));world.Complete(102,true);Assert.Null(world.Resolve(102,101));
        world.Handle(101,Query(101));world.Complete(101,true);Assert.NotNull(world.Resolve(102,101));
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task MemoryBridgeCorrelatesRealIndependentMappingsAndPreservesPayload(bool world)
    {
        var root=Path.Combine(Path.GetTempPath(),"xfe-bridge-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var path=Path.Combine(root,"link");
        try
        {
            using var host=NativeGameBridge.Create(path,world);using var game=NativeGameBridge.Open(path,world);
            var call=Task.Run(()=>game.Call(new("data","PUT","/private/save",new(){["test"]="本地"},[0,1,255]),TimeSpan.FromSeconds(5)));
            long id=0;GameLinkRequest? request=null;var deadline=DateTimeOffset.UtcNow.AddSeconds(4);
            while(!host.TryRead(out id,out request)){if(DateTimeOffset.UtcNow>deadline)throw new TimeoutException();await Task.Delay(1);}
            Assert.Equal([0,1,255],request!.Body);Assert.Equal("本地",request.Fields!["test"]);
            host.Reply(id,new(200,"saved"));Assert.Equal("saved",(await call).Payload);Assert.False(host.TryRead(out _,out _));
        }
        finally{Directory.Delete(root,true);}
    }
    [Fact]public void TimedOutBridgeNeverConsumesALateReplyAsAnotherRequest()
    {
        var root=Path.Combine(Path.GetTempPath(),"xfe-bridge-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            using var host=NativeGameBridge.Create(Path.Combine(root,"link"),true);using var game=NativeGameBridge.Open(Path.Combine(root,"link"),true);
            Assert.Throws<TimeoutException>(()=>game.Call(new("exchange",Datagrams:[]),TimeSpan.FromMilliseconds(10)));
            Assert.True(host.TryRead(out var id,out _));host.Reply(id,new(200));
            Assert.Throws<IOException>(()=>game.Call(new("exchange",Datagrams:[])));
        }
        finally{Directory.Delete(root,true);}
    }
}
