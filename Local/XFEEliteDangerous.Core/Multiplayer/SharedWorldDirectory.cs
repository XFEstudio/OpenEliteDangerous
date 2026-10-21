using System.Buffers.Binary;
using System.Security.Cryptography;
using XFEEliteDangerous.NativeFormat.Replication;
using XFEEliteDangerous.Client;

namespace XFEEliteDangerous.Core;

public sealed record NativePeerInformation(ulong Identity,string ConnectionDetails,string EncryptionKey,uint NativeUserId);
public sealed record SharedWorldMember(ulong RunId,uint NativeUserId,ulong? System,uint FlightMode,ulong? IslandId,ushort RoutePort);

/// <summary>One server owns the allocator, instance identities and admitted routes.
/// Raw native peer bytes are routed only between members of the same instance.</summary>
public sealed class SharedWorldDirectory : IDisposable
{
    private sealed class Peer(ulong run,uint user,ushort port)
    {
        internal readonly ulong RunId=run;
        internal readonly uint UserId=user;
        internal readonly ushort Port=port;
        internal readonly byte[] Key=RandomNumberGenerator.GetBytes(32);
        internal (ulong System,uint Mode,bool Frame)? Room;
        internal (ulong System,uint Mode,bool Frame)? PendingRoom;
        internal ushort ChannelPort;
        internal readonly Queue<GameDatagram> Incoming=[];
        internal readonly HashSet<ushort> GrantedRoutes=[];
    }
    private readonly object gate=new();
    private readonly Dictionary<ulong,Peer> peers=[];
    private readonly Dictionary<(ulong System,uint Mode,bool Frame),(ulong Island,ulong Wake)> rooms=[];
    private int nextPort=47000;
    public NativeObjectIdAllocator Allocator{get;}=new(NativeObjectIdAllocator.FirstReplicatedObjectBase);
    public void Register(ulong run,uint user)
    {
        lock(gate)
        {
            if(peers.Count>=32||peers.ContainsKey(run)||nextPort>57000)throw new InvalidOperationException(AppText.Get("SharedWorldDirectory.Text380"));
            if(run==0||run>EdServerSessionMessageCodec.MaximumWireId||user==0)throw new ArgumentException("Invalid private game identity.");
            var peer=new Peer(run,user,checked((ushort)nextPort++));peers.Add(run,peer);
            OfflineDiagnostics.Current?.RegisterSecret(Convert.ToHexStringLower(peer.Key));
        }
    }
    public NativeRpcModuleReply? Handle(ulong run,NativeRpcModuleRequest request)
    {
        if(request.Role!="primary"||request.LetterType!=22||request.Payload.Length==0||request.Payload[0]!=2)return null;
        var query=EdServerIslandMessageCodec.ReadSoloRequest(request.Payload,request.PayloadBits,request.AlignedValues);
        if(query.Address!=request.CurrentSystem||request.ClientMachine!=run)throw new InvalidDataException("Shared instance identity differs.");
        lock(gate)
        {
            var peer=peers[run];var key=(query.Address,query.FlightMode,query.FrameFlag);
            if(!rooms.TryGetValue(key,out var room)){room=(NewIdentity(),NewIdentity());while(room.Item1==room.Item2)room.Item2=NewIdentity();rooms.Add(key,room);}
            var members=peers.Values.Where(value=>value.RunId!=run&&value.Room==key).Select(value=>value.RunId).ToArray();peer.PendingRoom=key;
            using var response=EdServerIslandMessageCodec.CreateAssignment(0,query.Correlation,room.Island,room.Wake,members);
            OfflineDiagnostics.Current?.Write("debug","online.world","instance.prepared","Private shared instance candidate prepared; transport commit is still required.",
                new Dictionary<string,string?>{["system"]=query.Address.ToString(),["members"]=members.Length.ToString(),["nativeUserId"]=peer.UserId.ToString()});
            return new(1,true,[new(23,response.Flag,response.PayloadBits,response.CopyPayload())]);
        }
    }
    public void Complete(ulong run,bool committed)
    {
        lock(gate)
        {
            if(!peers.TryGetValue(run,out var peer)||peer.PendingRoom is not {} pending)return;
            if(committed){peer.Room=pending;OfflineDiagnostics.Current?.Write("info","online.world","instance.committed","Shared instance membership committed with the native reliable batch.");}
            peer.PendingRoom=null;
            RetireEmptyRooms();
        }
    }
    public NativePeerInformation? Resolve(ulong requester,ulong target)
    {
        lock(gate)
        {
            if(!peers.TryGetValue(requester,out var source)||!peers.TryGetValue(target,out var peer))return null;
            // The native provider asks for its own machine directory before
            // joining an instance. This does not admit a route to another room.
            if(requester!=target)
            {
                if(source.Room is null||source.Room!=peer.Room)return null;
                source.GrantedRoutes.Add(peer.Port);peer.GrantedRoutes.Add(source.Port);
            }
            return new(peer.RunId,new EdServerIp4Endpoint(0x7F000001,peer.Port,1360).ToString(),Convert.ToHexStringLower(peer.Key),peer.UserId);
        }
    }
    public void BindChannel(ulong run,ushort channel){lock(gate){if(channel==0)throw new InvalidDataException("Invalid native channel.");var peer=peers[run];if(peer.ChannelPort!=0&&peer.ChannelPort!=channel)throw new InvalidDataException("Native channel changed.");peer.ChannelPort=channel;}}
    public void Route(ulong run,GameDatagram packet)
    {
        lock(gate)
        {
            var source=peers[run];var target=peers.Values.SingleOrDefault(value=>value.Port==packet.Port);
            if(packet.Payload.Length is <8 or >1472||EdServerDatagramHeader.Read(packet.Payload).SenderRunId!=run)
                throw new InvalidDataException("Native peer datagram sender differs.");
            if(!source.GrantedRoutes.Contains(packet.Port))throw new InvalidDataException("Native peer route has not been admitted.");
            // Late retransmissions to a former member are normal during native
            // disconnect. Drop them; never tear down the remaining player's
            // server connection or forward them into a different instance.
            if(target is null||source.Room is null||source.Room!=target.Room||target.RunId==run||target.ChannelPort==0)return;
            if(target.Incoming.Count>=256)throw new InvalidOperationException("Native peer queue is full.");
            target.Incoming.Enqueue(new(target.ChannelPort,source.Port,packet.Payload.ToArray()));
        }
    }
    public GameDatagram[] Drain(ulong run){lock(gate){var queue=peers[run].Incoming;var result=queue.Take(128).ToArray();for(var i=0;i<result.Length;i++)queue.Dequeue();return result;}}
    public void Leave(ulong run){lock(gate){if(peers.TryGetValue(run,out var peer)){peer.Room=null;peer.PendingRoom=null;peer.Incoming.Clear();}RetireEmptyRooms();}}
    public SharedWorldMember[] Snapshot(){lock(gate)return peers.Values.Select(peer=>new SharedWorldMember(peer.RunId,peer.UserId,peer.Room?.System,peer.Room?.Mode??0,peer.Room is {} key?rooms[key].Island:null,peer.Port)).ToArray();}
    public void Remove(ulong run){lock(gate){if(peers.Remove(run,out var peer)){CryptographicOperations.ZeroMemory(peer.Key);peer.Incoming.Clear();}RetireEmptyRooms();}}
    private void RetireEmptyRooms()
    {
        // An instance ends when its last committed or prepared member leaves.
        // Reusing that native Island/Wake identity resurrects a closed session
        // in the still-running client, including stale peer transport state.
        foreach(var room in rooms.Keys.Where(key=>!peers.Values.Any(peer=>peer.Room==key||peer.PendingRoom==key)).ToArray())
        {
            rooms.Remove(room);
            OfflineDiagnostics.Current?.Write("info","online.world","instance.retired","The final member left; native Island/Wake identities retired.");
        }
    }
    public void Dispose(){lock(gate){foreach(var peer in peers.Values)CryptographicOperations.ZeroMemory(peer.Key);peers.Clear();rooms.Clear();}}
    private static ulong NewIdentity(){ulong value;do{value=BinaryPrimitives.ReadUInt64LittleEndian(RandomNumberGenerator.GetBytes(8))&0xFFFF_FFFF_FFFF;}while(value==0);return value;}
}
