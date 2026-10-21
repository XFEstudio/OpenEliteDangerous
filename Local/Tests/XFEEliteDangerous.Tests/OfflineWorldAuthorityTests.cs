using System.Security.Cryptography;
using System.Text.Json;
using XFEEliteDangerous.Core;
using XFEEliteDangerous.NativeFormat.Replication;
using Xunit;

namespace XFEEliteDangerous.Tests;

public sealed class OfflineWorldAuthorityTests
{
    private const ulong Client = 0x123456;
    private static readonly Dictionary<string,string> Identity = new() { ["machineToken"]="local-machine-fixture", ["authToken"]="local-auth-fixture" };
    [Fact] public void DirectoryRequiresNativeIdentityAndRejectsIdentityChange()
    {
        using var world=new OfflineWorldAuthority(Client,17);
        Assert.Throws<NativeDataException>(()=>world.DirectoryDocument());
        world.BindIdentity(Identity);world.BindIdentity(Identity);
        Assert.Throws<NativeDataException>(()=>world.BindIdentity(new Dictionary<string,string>(Identity){["authToken"]="different"}));
        Assert.DoesNotContain("local-auth-fixture",world.DirectoryDocument());
    }
    [Theory] [InlineData(false,46001)] [InlineData(true,46002)]
    public void BothLocalRolesNegotiateWithDistinctNativeMachineIdentity(bool missions,int expectedPort)
    {
        using var world=new OfflineWorldAuthority(Client,17);world.BindIdentity(Identity);
        using var directory=JsonDocument.Parse(world.DirectoryDocument());
        var role=directory.RootElement.GetProperty(missions?"missionsservers":"servers")[0];
        var key=Convert.FromHexString(role.GetProperty("encryptionKey").GetString()!);
        try
        {
            Assert.Equal($"IP4:127.0.0.1:{expectedPort},1360",role.GetProperty("connectionDetails").GetString());
            var input=EdServerDatagramCodec.EncodeMode3(new(Client,false,1),EdServerControlMessageCodec.EncodeSyncLetter(Sync()),key);
            var reply=Assert.Single(world.Receive(45000,(ushort)expectedPort,input));
            Assert.Equal(expectedPort,reply.SourcePort);
            using var decoded=EdServerDatagramCodec.DecodeMode3(reply.Payload,key);
            Assert.Equal(ulong.Parse(role.GetProperty("runId").GetString()!),decoded.Header.SenderRunId);
            Assert.NotEqual(Client,decoded.Header.SenderRunId);
            var bit=0;var sync=EdServerControlMessageCodec.ReadSyncLetter(decoded.OpaqueLetterBytes.Span,ref bit,33837);
            Assert.False(sync.IsClient);Assert.Equal((ushort)45000,sync.ObservedPort);Assert.Equal((ushort)91,sync.Acknowledgement);
            Assert.Equal((byte)16,sync.LetterTypeBits);
        }
        finally{CryptographicOperations.ZeroMemory(key);}
    }
    [Fact] public void RejectedChecksumAndSenderCannotPoisonTheFirstHandshake()
    {
        using var world=new OfflineWorldAuthority(Client,17);world.BindIdentity(Identity);
        using var directory=JsonDocument.Parse(world.DirectoryDocument());
        var key=Convert.FromHexString(directory.RootElement.GetProperty("servers")[0].GetProperty("encryptionKey").GetString()!);
        try
        {
            var letters=EdServerControlMessageCodec.EncodeSyncLetter(Sync());
            var corrupt=EdServerDatagramCodec.EncodeMode3(new(Client,false,1),letters,key);corrupt[^1]^=0x80;
            Assert.Empty(world.Receive(45000,46001,corrupt));
            Assert.Empty(world.Receive(45000,46001,EdServerDatagramCodec.EncodeMode3(new(Client+1,false,2),letters,key)));
            Assert.Single(world.Receive(45000,46001,EdServerDatagramCodec.EncodeMode3(new(Client,false,3),letters,key)));
        }
        finally{CryptographicOperations.ZeroMemory(key);}
    }
    [Fact] public void ANewLaunchRejectsThePreviousLaunchPacketKey()
    {
        using var first=new OfflineWorldAuthority(Client,17);first.BindIdentity(Identity);
        using var second=new OfflineWorldAuthority(Client,17);second.BindIdentity(Identity);
        using var directory=JsonDocument.Parse(first.DirectoryDocument());
        var key=Convert.FromHexString(directory.RootElement.GetProperty("servers")[0].GetProperty("encryptionKey").GetString()!);
        try
        {
            var packet=EdServerDatagramCodec.EncodeMode3(new(Client,false,1),EdServerControlMessageCodec.EncodeSyncLetter(Sync()),key);
            Assert.Empty(second.Receive(45000,46001,packet));
        }
        finally{CryptographicOperations.ZeroMemory(key);}
    }
    [Fact] public void MissionsAuthorityRejectsPrimaryStartAndRepliesWithItsActualRole()
    {
        const ulong block=NativeObjectIdAllocator.FirstReplicatedObjectBase;
        using var app=new LocalReplicationApplication(11,12,new(block,0),SHA256.HashData("synthetic-machine"u8),SHA256.HashData("synthetic-auth"u8),true,17,missionsRole:true);
        using var primary=new EdServerStartServerAdmin([],"synthetic-machine"u8,"synthetic-auth"u8,[],false);
        Assert.Throws<NotSupportedException>(()=>app.Prepare([new(26,Start:primary)],block+4096,1360));
        Assert.Equal(0,app.AcceptedStarts);
        using var missions=new EdServerStartServerAdmin([],"synthetic-machine"u8,"synthetic-auth"u8,[],true);
        using var plan=app.Prepare([new(26,Start:missions)],block+4096,1360);
        var reply=Assert.Single(plan.Emissions);var position=0;
        Assert.Equal(27,EdServerControlMessageCodec.ReadLetterType(reply.Payload,ref position,16));
        var ready=EdServerAdminMessageCodec.ReadReadyBody(reply.Payload,ref position);
        Assert.True(ready.Missions);Assert.Equal(block+1,ready.SessionId);
        plan.Commit();Assert.Equal(1,app.AcceptedStarts);
    }
    [Fact] public void BothConnectionsUseTheOneNativeManagerAllocationAndAnEarlyMissionsRetryCanRecover()
    {
        using var world=new OfflineWorldAuthority(Client,17);world.BindIdentity(Identity);
        var diagnostics=new List<string>();world.Diagnostic=diagnostics.Add;
        using var directory=JsonDocument.Parse(world.DirectoryDocument());
        using var primary=new Peer(world,directory.RootElement.GetProperty("servers")[0]);
        using var missions=new Peer(world,directory.RootElement.GetProperty("missionsservers")[0]);
        using var start=new EdServerStartServerAdmin([],"local-machine-fixture"u8,"local-auth-fixture"u8,[],true);
        var body=new byte[1024];var bit=0;
        EdServerControlMessageCodec.WriteSequenceLetter(body,ref bit,92,8);
        EdServerControlMessageCodec.WriteLetterType(body,ref bit,26,8);
        EdServerAdminMessageCodec.WriteStartBody(body,ref bit,start);
        var message=body[..((bit+7)/8)];
        Assert.Empty(missions.Send(message));
        Assert.Contains(diagnostics,value=>value.Contains("application-transport-not-ready",StringComparison.Ordinal));
        var allocate=new byte[4];bit=0;
        EdServerControlMessageCodec.WriteSequenceLetter(allocate,ref bit,92,8);
        EdServerControlMessageCodec.WriteLetterType(allocate,ref bit,10,8);
        Assert.NotEmpty(primary.Send(allocate));
        var replies=missions.Send(message);
        var ready=replies.Select(value=>ReadReady(value)).Single(value=>value is not null)!.Value;
        Assert.True(ready.Missions);Assert.Equal(1,diagnostics.Count(value=>value=="SHARED_CLIENT_ALLOCATOR_BOUND"));
    }
    private static EdServerServerAdminReady? ReadReady(byte[] bytes)
    {
        var bit=0;if(EdServerControlMessageCodec.ReadLetterType(bytes,ref bit,16)!=2)return null;
        _=EdServerControlMessageCodec.ReadSequenceBody(bytes,ref bit);
        return EdServerControlMessageCodec.ReadLetterType(bytes,ref bit,16)==27?EdServerAdminMessageCodec.ReadReadyBody(bytes,ref bit):null;
    }
    [Fact] public void TransportAcknowledgementCanShareItsPacketWithTheFirstReliableRequest()
    {
        using var world=new OfflineWorldAuthority(Client,17);world.BindIdentity(Identity);
        using var directory=JsonDocument.Parse(world.DirectoryDocument());
        using var primary=new Peer(world,directory.RootElement.GetProperty("servers")[0],piggybackId:true);
    }
    [Fact]public void JoinedMissionsConnectionReturnsItsActualEmptyMapThroughTheCorrelatedNativeReply()
    {
        const ulong block=NativeObjectIdAllocator.FirstReplicatedObjectBase;
        using var app=new LocalReplicationApplication(11,12,new(block,0),SHA256.HashData("synthetic-machine"u8),SHA256.HashData("synthetic-auth"u8),true,17,missionsRole:true);
        using var start=new EdServerStartServerAdmin([],"synthetic-machine"u8,"synthetic-auth"u8,[],true);
        using(var plan=app.Prepare([new(26,Start:start)],block+4096,1360))plan.Commit();
        using(var plan=app.Prepare([new(13,Request:new(block+1,block+4096))],block+4096,1360))plan.Commit();
        using(var plan=app.Prepare([new(15,Joined:new(block+1,[],[]))],block+4096,1360))plan.Commit();
        using var query=new LocalApplicationCommand(23,Call:new(block+2,false,40,[78,4,3,2,1]));
        using var unknown=new LocalApplicationCommand(23,Call:new(block+2,false,8,[69]));
        Assert.Throws<NotSupportedException>(()=>app.Prepare([query,unknown],block+4096,1360));
        using var accepted=app.Prepare([query],block+4096,1360);
        var emission=Assert.Single(accepted.Emissions);var bit=0;
        Assert.Equal(23,EdServerControlMessageCodec.ReadLetterType(emission.Payload,ref bit,16));
        using var reply=EdServerCallEnvelopeCodec.ReadBody(emission.Payload,ref bit,false);
        Assert.Equal(0x01020304u,EdServerPersistentRecordMapMessageCodec.ReadEmptyReply(reply));accepted.Commit();
        Assert.False(JsonSerializer.SerializeToElement(app.Snapshot()).GetProperty("worldReady").GetBoolean());
    }
    private sealed class Peer:IDisposable
    {
        private readonly OfflineWorldAuthority world;
        private readonly ushort port;
        private readonly byte[] key;
        private ushort counter;
        internal Peer(OfflineWorldAuthority world,JsonElement record,bool piggybackId=false)
        {
            this.world=world;port=EdServerIp4Endpoint.Parse(record.GetProperty("connectionDetails").GetString()!).Port;
            key=Convert.FromHexString(record.GetProperty("encryptionKey").GetString()!);
            var reply=Assert.Single(Send(EdServerControlMessageCodec.EncodeSyncLetter(Sync() with{AlignmentMode=0})));
            var bit=0;var sync=EdServerControlMessageCodec.ReadSyncLetter(reply,ref bit,33837);
            var acknowledgement=EdServerControlMessageCodec.EncodeAckLetter(new(sync.Sequence,0,16),8);
            if(!piggybackId){_=Send(acknowledgement);return;}
            var packet=new byte[acknowledgement.Length+4];acknowledgement.CopyTo(packet,0);bit=acknowledgement.Length*8;
            EdServerControlMessageCodec.WriteSequenceLetter(packet,ref bit,92,8);
            EdServerControlMessageCodec.WriteLetterType(packet,ref bit,10,8);
            Assert.Contains(Send(packet),value=>value.Length>=6 && value[0]==2 && value[4]==11);
        }
        internal IReadOnlyList<byte[]> Send(byte[] letters)
        {
            var output=new List<byte[]>();
            foreach(var packet in world.Receive(45000,port,EdServerDatagramCodec.EncodeMode3(new(Client,false,++counter),letters,key)))
            {using var clear=EdServerDatagramCodec.DecodeMode3(packet.Payload,key);output.Add(clear.OpaqueLetterBytes.ToArray());}
            return output;
        }
        public void Dispose()=>CryptographicOperations.ZeroMemory(key);
    }
    private static EdServerSyncMessage Sync()=>new(33837,91,0,8,0,false,3,0,ReadOnlyMemory<byte>.Empty,0,0,ReadOnlyMemory<byte>.Empty,false,true,332841,0);
}
