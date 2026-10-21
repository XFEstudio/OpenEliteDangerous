using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XFEEliteDangerous.NativeFormat.Replication;
using XFEEliteDangerous.Client;

namespace XFEEliteDangerous.Core;

/// <summary>A delivery from the local authority to the native queue. The port is an in-process routing label.</summary>
public sealed record OfflineLocalPacket(ushort SourcePort, byte[] Payload);

/// <summary>Owns one local identity and its control sessions. It never creates or uses an operating-system endpoint.</summary>
public sealed class OfflineWorldAuthority : IDisposable, INativeWorldAuthority
{
    private sealed class Role(bool missions, ushort port, ulong runId)
    {
        internal readonly bool Missions = missions;
        internal readonly ushort Port = port;
        internal readonly ulong RunId = runId;
        internal readonly byte[] Key = RandomNumberGenerator.GetBytes(32);
        internal LocalReplicationSession? Session;
        internal ushort ClientPort;
    }
    private readonly object gate = new();
    private readonly ulong clientRunId;
    private readonly uint userId;
    private readonly NativeObjectIdAllocator allocator;
    private readonly Func<ulong,NativePeerInformation?>? resolvePeer;
    private readonly Action<bool>? rpcTransaction;
    private readonly string launch = Guid.NewGuid().ToString("N");
    private readonly Role[] roles;
    private readonly ulong islandId,wakeId;
    private Func<NativeRpcModuleRequest,NativeRpcModuleReply?>? rpcModule;
    private byte[]? machineDigest, authDigest;
    private ulong? selectedSystem;
    private ulong? nativeClientBase;
    private bool disposed;
    public Action<string>? Diagnostic { get; set; }
    public bool CanRoute(ushort port)=>port is 46001 or 46002;
    public OfflineWorldAuthority(ulong clientRunId, uint nativeUserId,NativeObjectIdAllocator? sharedAllocator=null,Func<ulong,NativePeerInformation?>? resolvePeer=null,Action<bool>? rpcTransaction=null)
    {
        if (clientRunId is 0 or >EdServerDatagramHeader.MaximumRunId || nativeUserId == 0) throw new ArgumentException("Invalid local runtime identity.");
        this.clientRunId = clientRunId; userId = nativeUserId;allocator=sharedAllocator??new(NativeObjectIdAllocator.FirstReplicatedObjectBase);this.resolvePeer=resolvePeer;
        this.rpcTransaction=rpcTransaction;
        var primary = NewRunId(clientRunId);
        roles = [new(false, 46001, primary), new(true, 46002, NewRunId(clientRunId, primary))];
        islandId=NewRunId(clientRunId,roles[0].RunId,roles[1].RunId);
        wakeId=NewRunId(clientRunId,roles[0].RunId,roles[1].RunId,islandId);
        foreach(var role in roles){OfflineDiagnostics.Current?.RegisterSecret(Convert.ToHexStringLower(role.Key));OfflineDiagnostics.Current?.RegisterSecret(Convert.ToBase64String(role.Key));}
    }
    internal void SetRpcModule(Func<NativeRpcModuleRequest,NativeRpcModuleReply?> handler)
    {lock(gate){if(roles.Any(role=>role.Session is not null))throw new InvalidOperationException("RPC host must be bound before any peer session.");rpcModule=handler;}}
    internal void BindIdentity(IReadOnlyDictionary<string,string> fields)
    {
        if (!fields.TryGetValue("machineToken", out var machine) || !fields.TryGetValue("authToken", out var auth)) return;
        if (machine.Length is 0 or >4096 || auth.Length is 0 or >4096) throw new NativeDataException(400,"Invalid local runtime credential size.");
        OfflineDiagnostics.Current?.RegisterSecret(machine);OfflineDiagnostics.Current?.RegisterSecret(auth);
        var nextMachine = SHA256.HashData(Encoding.UTF8.GetBytes(machine));
        var nextAuth = SHA256.HashData(Encoding.UTF8.GetBytes(auth));
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            if (machineDigest is not null)
            {
                var same = CryptographicOperations.FixedTimeEquals(machineDigest,nextMachine) & CryptographicOperations.FixedTimeEquals(authDigest!,nextAuth);
                CryptographicOperations.ZeroMemory(nextMachine); CryptographicOperations.ZeroMemory(nextAuth);
                if (!same) throw new NativeDataException(409,"The local runtime identity changed during this launch.");
                return;
            }
            machineDigest=nextMachine; authDigest=nextAuth;
        }
    }
    internal void SelectSystem(ulong? address) { lock(gate) selectedSystem=address; }
    internal string DirectoryDocument()
    {
        lock(gate)
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            if(machineDigest is null)throw new NativeDataException(409,"The local runtime identity has not reached its native login boundary.");
            using var output=new MemoryStream();
            using(var writer=new Utf8JsonWriter(output))
            {
                writer.WriteStartObject();
                foreach(var role in roles)
                {
                    writer.WriteStartArray(role.Missions?"missionsservers":"servers");writer.WriteStartObject();
                    writer.WriteString("runId",role.RunId.ToString(CultureInfo.InvariantCulture));
                    writer.WriteNumber("id",role.Missions?2:1);
                    writer.WriteString("connectionDetails",new EdServerIp4Endpoint(0x7F000001,role.Port,1360).ToString());
                    writer.WriteString("encryptionKey",Convert.ToHexStringLower(role.Key));
                    writer.WriteEndObject();writer.WriteEndArray();
                }
                writer.WriteEndObject();
            }
            return Encoding.UTF8.GetString(output.GetBuffer(),0,checked((int)output.Length));
        }
    }
    public IReadOnlyList<OfflineLocalPacket> Receive(ushort clientPort, ushort targetPort, ReadOnlySpan<byte> packet)
    {
        using var diagnostic=OfflineDiagnostics.Current?.Begin("protocol.datagram","local.receive",new Dictionary<string,string?>
            {["sourcePort"]=clientPort.ToString(),["targetPort"]=targetPort.ToString(),["packetBytes"]=packet.Length.ToString()});
        lock(gate)
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            var role=roles.SingleOrDefault(value=>value.Port==targetPort);
            if(role is null||machineDigest is null||clientPort==0)return [];
            try
            {
                using var decoded=EdServerDatagramCodec.DecodeMode3(packet,role.Key);
                if(decoded.Header.SenderRunId!=clientRunId)throw new InvalidDataException("local-sender-identity-mismatch");
                if(role.Session is null)
                {
                    var application=new LocalReplicationApplication(role.RunId,clientRunId,allocator.AllocateBlock(launch),machineDigest,authDigest!,true,userId,role.Missions,rpcModule,islandId,wakeId,resolvePeer,role.Missions?null:rpcTransaction);
                    role.Session=new(new IPEndPoint(IPAddress.Loopback,clientPort),allocator,launch,initiateMtuProbe:true,application:application,
                        resolveRoute:resolvePeer is null?null:target=>resolvePeer(target) is {} peer?EdServerIp4Endpoint.Parse(peer.ConnectionDetails):null);
                    role.ClientPort=clientPort;
                }
                if(role.ClientPort!=clientPort)throw new InvalidDataException("local-channel-changed");
                if(nativeClientBase is {} firstBase)role.Session.BindClientAllocator(firstBase);
                var before=role.Session.StateSummary;
                var letters=role.Session.Receive(decoded.OpaqueLetterBytes,decoded.Header.OpaqueFlag,selectedSystem);
                OfflineDiagnostics.Current?.Write(role.Session.LastControlFailure.Length>0?"warning":"debug","protocol.session","session.receive",
                    role.Session.LastControlFailure.Length>0?role.Session.LastControlFailure:"Native packet processed.",
                    new Dictionary<string,string?>{["sessionRole"]=role.Missions?"missions":"primary",["stateBefore"]=before,
                        ["stateAfter"]=role.Session.StateSummary,["replyCount"]=letters.Count.ToString(),["pendingReliable"]=role.Session.PendingReliable.ToString()});
                if(before!=role.Session.StateSummary)Diagnostic?.Invoke("SESSION_STATE\t"+(role.Missions?"missions":"primary")+"\t"+role.Session.StateSummary);
                if(nativeClientBase is null && role.Session.InitialObjectIdBase is {} allocated)
                { nativeClientBase=allocated;Diagnostic?.Invoke("SHARED_CLIENT_ALLOCATOR_BOUND"); }
                if(role.Session.LastControlFailure.Length>0)
                    Diagnostic?.Invoke("SESSION_REJECT\t"+(role.Missions?"missions":"primary")+"\t"+role.Session.LastControlFailure+"\t"+role.Session.StateSummary+"\t"+role.Session.LastCallEnvelopeShape);
                return Encode(role,letters);
            }
            catch(Exception error)when(error is InvalidDataException or ArgumentException or CryptographicException)
            { diagnostic?.Complete("rejected");OfflineDiagnostics.Current?.Write("error","protocol.datagram","datagram.rejected",
                "Local datagram decode/identity validation failed.",exception:error);
                Diagnostic?.Invoke("SESSION_ERROR\t"+error.GetType().Name+"\t"+error.Message);return []; }
        }
    }
    public IReadOnlyList<OfflineLocalPacket> Poll(ushort clientPort)
    {
        lock(gate)
        {
            if(disposed)return [];
            var output=new List<OfflineLocalPacket>();
            foreach(var role in roles.Where(value=>value.ClientPort==clientPort&&value.Session is not null))
                output.AddRange(Encode(role,role.Session!.ClaimDueRetransmissions()));
            return output;
        }
    }
    private static IReadOnlyList<OfflineLocalPacket> Encode(Role role,IReadOnlyList<byte[]> letters)
    {
        var output=new List<OfflineLocalPacket>();
        foreach(var value in letters)
        {
            try{
                var counter=role.Session!.NextPacketCounter();
                output.Add(new(role.Port,EdServerDatagramCodec.EncodeMode3(new(role.RunId,false,counter),value,role.Key)));
                OfflineDiagnostics.Current?.Write("debug","protocol.datagram","datagram.reply","Local packet queued.",
                    new Dictionary<string,string?>{["sessionRole"]=role.Missions?"missions":"primary",["packetCounter"]=counter.ToString(),
                        ["letterBytes"]=value.Length.ToString(),["pendingReliable"]=role.Session.PendingReliable.ToString()});
            }
            finally{CryptographicOperations.ZeroMemory(value);}
        }
        return output;
    }
    public void Dispose()
    {
        lock(gate)
        {
            if(disposed)return;disposed=true;
            foreach(var role in roles){role.Session?.Dispose();CryptographicOperations.ZeroMemory(role.Key);}
            if(machineDigest is not null)CryptographicOperations.ZeroMemory(machineDigest);
            if(authDigest is not null)CryptographicOperations.ZeroMemory(authDigest);
        }
    }
    private static ulong NewRunId(params ulong[] excluded)
    {
        ulong value;do{value=BinaryPrimitives.ReadUInt64LittleEndian(RandomNumberGenerator.GetBytes(8))&EdServerDatagramHeader.MaximumRunId;}while(value==0||excluded.Contains(value));return value;
    }
}
