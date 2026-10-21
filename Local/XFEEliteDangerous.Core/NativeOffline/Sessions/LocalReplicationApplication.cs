using System.Security.Cryptography;
using XFEEliteDangerous.NativeFormat.Replication;
using XFEEliteDangerous.Client;

namespace XFEEliteDangerous.Core;

// Application payloads are owned by the fully parsed packet and always disposed.
// Credentials never enter diagnostics, persistent saves or the replay cache.
internal sealed record LocalApplicationCommand(byte Type,EdServerStartServerAdmin? Start=null,
    EdServerSessionRequest? Request=null,EdServerSessionMembers? Members=null,EdServerSessionJoined? Joined=null,
    EdServerSessionLeft? Left=null,EdServerObjectStateAcknowledgement? StateAck=null,EdServerObjectSalute? Salute=null,EdServerCallEnvelope? Call=null,EdServerObjectUpdate? State=null,bool AlignedCallValues=false):IDisposable
{
    internal static LocalApplicationCommand Read(byte type,ReadOnlySpan<byte> bytes,ref int position,bool alignedCallValues=false)=>type switch
    {
        26=>new(type,Start:EdServerAdminMessageCodec.ReadStartBody(bytes,ref position,256,1041)),
        13=>new(type,Request:EdServerSessionMessageCodec.ReadRequestBody(bytes,ref position)),
        14=>new(type,Members:EdServerSessionMessageCodec.ReadMembersBody(bytes,ref position,64)),
        15=>new(type,Joined:EdServerSessionMessageCodec.ReadJoinedBody(bytes,ref position,64)),
        16=>new(type,Left:EdServerSessionMessageCodec.ReadLeftBody(bytes,ref position)),
        19=>new(type,StateAck:EdServerObjectReplicationCodec.ReadAcknowledgementBody(bytes,ref position)),
        //62C48A/62C4C8 register UREP18 and CertREP33 with the same651F20
        // reader. The body Boolean selects the archive in64CB30; the letter
        // type is the delivery class, not an alternative archive selector.
        18 or 33=>new(type,State:EdServerObjectUpdateCodec.ReadBody(bytes,ref position)),
        32=>new(type,Salute:EdServerObjectReplicationCodec.ReadSaluteBody(bytes,ref position)),
        20 or 21 or 22 or 23=>new(type,Call:EdServerCallEnvelopeCodec.ReadBody(bytes,ref position,alignedCallValues),AlignedCallValues:alignedCallValues),
        _=>throw new NotSupportedException("unsupported-letter-type")
    };
    internal byte[] Identity()
    {
        var data=new byte[1472];var bit=0;
        try
        {
            EdServerControlMessageCodec.WriteLetterType(data,ref bit,Type,16);
            if(Start is {} start)EdServerAdminMessageCodec.WriteStartBody(data,ref bit,start,256,1041);
            else if(Request is {} request)EdServerSessionMessageCodec.WriteRequestBody(data,ref bit,request);
            else if(Members is {} members)EdServerSessionMessageCodec.WriteMembersBody(data,ref bit,members,64);
            else if(Joined is {} joined)EdServerSessionMessageCodec.WriteJoinedBody(data,ref bit,joined,64);
            else if(Left is {} left)EdServerSessionMessageCodec.WriteLeftBody(data,ref bit,left);
            else if(StateAck is {} ack)EdServerObjectReplicationCodec.WriteAcknowledgementBody(data,ref bit,ack);
            else if(Salute is {} salute)EdServerObjectReplicationCodec.WriteSaluteBody(data,ref bit,salute);
            else if(Call is {} call)EdServerCallEnvelopeCodec.WriteBody(data,ref bit,call,true);
            else if(State is {} state)EdServerObjectUpdateCodec.WriteBody(data,ref bit,state);
            return SHA256.HashData(data.AsSpan(0,(bit+7)/8));
        }
        finally{CryptographicOperations.ZeroMemory(data);}
    }
    public void Dispose(){Start?.Dispose();Call?.Dispose();State?.Dispose();}
}

/// <summary>
/// One actual private matchmaking management session, created by the server
/// before accepting StartServerAdmin. Its server-owned block/session identity
/// is distinct from the actual client allocation and from every world/island.
/// Empty management object sets describe this service; they do not claim a world.
/// The outer peer gate and fresh lease serialize and authorize every operation.
/// </summary>
internal sealed class LocalReplicationApplication:IDisposable
{
    private readonly EdServerLocalSessionDefinition definition;
    private readonly ulong remote;
    private readonly byte[] machineDigest,authDigest;
    private readonly bool enableControllerCreation;
    private readonly ulong controllerReference;
    private EdServerSessionNegotiationMachine? membership;
    private long version;
    private bool closed;
    private bool controllerCreationQueued;
    private bool controllerStateAcknowledged,controllerLiveAcknowledged;
    private bool controllerSaluted,controllerSaluteLive;
    private bool? terrainCapability;
    private readonly uint nativeUserId;
    private readonly bool missionsRole;
    private readonly Func<NativeRpcModuleRequest,NativeRpcModuleReply?>? rpcModule;
    private readonly ulong islandId,wakeId;
    private readonly Func<ulong,NativePeerInformation?>? resolvePeer;
    private readonly Action<bool>? rpcTransaction;
    internal string RoleName=>missionsRole?"missions":"primary";
    private bool? profileRegistration33,profileRegistration34;
    private uint clientNotificationReceipts;
    private uint? clientScalar128;
    private EdServerAdminSecondaryArchive? secondaryArchive;
    // Captured from the exact hash-pinned2026.09.03.332841 client at63B3C0,
    // after both native archives finish and before construction-version validation.
    // This schema has2 fixed/0 optional/3 variable properties, index width7.
    internal const ushort ControllerConstructionVersion=64655;
    internal const int ControllerOptionalProperties=0;
    internal const int ControllerRegisteredMethods=155;
    //965CF0 initializes root+388 to float100;63B600 serializes the
    // immutable registered defaults. The pinned native007g read independently
    // confirms all14 words:0x42C80000 followed by13 zero words. Only strings
    //0,1,2,5 are known empty;3/4 must arrive as complete variable overrides.
    private static readonly byte[] ControllerSecondaryDefaults=CreateSecondaryDefaults();
    private static byte[] CreateSecondaryDefaults()
    {var bytes=new byte[EdServerAdminSecondaryArchive.FixedBits/8];System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes,0x42C80000);return bytes;}
    internal int AcceptedStarts{get;private set;}
    internal int ClockReplies{get;private set;}
    internal int StellarForgeNoOverridesReplies{get;private set;}
    internal int UnavailablePeerInfoReplies{get;private set;}
    internal int EmptyArenaCatalogueReplies{get;private set;}
    internal EdServerSessionPeerState PeerState=>membership?.GetPeerState(remote)??EdServerSessionPeerState.None;
    internal bool RecognizesController(ulong reference)=>!closed&&enableControllerCreation&&controllerCreationQueued
        &&PeerState==EdServerSessionPeerState.Joined&&reference==controllerReference;
    internal string CallReferenceKind(ulong reference)=>reference==controllerReference?"controller":reference==definition.SessionId?"management-session":
        reference==definition.LocalMachineRunId?"server-machine":reference>=definition.InitialObjectIdBase&&reference<definition.InitialObjectIdBase+4096?"allocated-server-block":
        reference>=0xFFFFFFFFFF00?"reserved-alias":"other";
    internal string PeerInfoTargetKind(ulong reference)=>reference==remote?"authenticated-game-machine":
        reference==definition.LocalMachineRunId?"private-control-machine":"other";
    // A transport reset invalidates the previous replicated objects and session
    // membership. Retain only the same authenticated private identity, with a
    // fresh allocated reference block. No commander/save state is touched.
    internal LocalReplicationApplication Restart(NativeObjectIdBlock freshBlock)
    {
        if(closed)throw new InvalidOperationException("Private application is closed.");
        return new(definition.LocalMachineRunId,remote,freshBlock,machineDigest,authDigest,enableControllerCreation,nativeUserId,missionsRole,rpcModule,islandId,wakeId,resolvePeer,rpcTransaction);
    }
    internal LocalReplicationApplication(ulong local,ulong remote,NativeObjectIdBlock serverBlock,
        ReadOnlySpan<byte> machineDigest,ReadOnlySpan<byte> authDigest,bool enableControllerCreation=false,uint nativeUserId=0,bool missionsRole=false,
        Func<NativeRpcModuleRequest,NativeRpcModuleReply?>? rpcModule=null,ulong islandId=0,ulong wakeId=0,Func<ulong,NativePeerInformation?>? resolvePeer=null,Action<bool>? rpcTransaction=null)
    {
        if(local==0||local>EdServerSessionMessageCodec.MaximumWireId||remote==0||remote==local||remote>EdServerSessionMessageCodec.MaximumWireId
            ||serverBlock.Base==0||serverBlock.Base%4096!=0||serverBlock.Base>=NativeObjectIdAllocator.ReservedStart
            ||machineDigest.Length!=32||authDigest.Length!=32)throw new ArgumentException("Invalid private management session definition.");
        if(enableControllerCreation&&serverBlock.Base<NativeObjectIdAllocator.FirstReplicatedObjectBase)
            throw new ArgumentException("Controller objects require the native replicated reference domain.");
        this.remote=remote;this.machineDigest=machineDigest.ToArray();this.authDigest=authDigest.ToArray();
        this.nativeUserId=nativeUserId;
        this.missionsRole=missionsRole;
        this.rpcModule=rpcModule;this.islandId=islandId;this.wakeId=wakeId;
        this.resolvePeer=resolvePeer;
        this.rpcTransaction=rpcTransaction;
        this.enableControllerCreation=enableControllerCreation;controllerReference=serverBlock.Base+2;
        definition=new(serverBlock.Base+1,local,serverBlock.Base,Array.AsReadOnly(new[]{local}),
            enableControllerCreation?Array.AsReadOnly(new[]{controllerReference}):[],[]);
    }
    internal Plan Prepare(IReadOnlyList<LocalApplicationCommand> commands,ulong? actualClientBase,ushort? actualMtu,
        EdServerNetworkTime? receivedTime=null,EdServerNetworkTime? sentTime=null,ulong? selectedSystemAddress=null)
    {
        if(closed)throw new InvalidDataException("application-closed");
        var candidate=membership?.Fork();var starts=0;var nextClosed=false;var nextControllerQueued=controllerCreationQueued;
        var nextStateAcknowledged=controllerStateAcknowledged;var nextLiveAcknowledged=controllerLiveAcknowledged;var payloads=new List<Emission>();
        var nextControllerSaluted=controllerSaluted;var nextControllerSaluteLive=controllerSaluteLive;
        var nextTerrain=terrainCapability;
        var nextRegistration33=profileRegistration33;var nextRegistration34=profileRegistration34;
        var nextNotifications=clientNotificationReceipts;
        var nextScalar128=clientScalar128;
        var nextArchive=secondaryArchive;var ownsArchive=false;
        try
        {
            foreach(var command in commands)
            {
                if(nextClosed)throw new InvalidDataException("application-closed");
                if(command.Start is {} start)
                {
                    if(candidate is not null)throw new InvalidDataException("application-already-started");
                    // StartServerAdmin and the MTU reply may occupy successive
                    // reliable groups after a native reconnect. Requiring the
                    // later MTU before acknowledging this earlier group creates
                    // a receive-window deadlock. The authenticated allocation is
                    // still required; Ready's11-byte reply fits the transport's
                    // conservative default without waiting for an MTU probe.
                    if(actualClientBase is null)throw new InvalidDataException("application-transport-not-ready");
                    if(actualMtu is {} mtu&&mtu is not(>=576 and <=65507))throw new InvalidDataException("application-mtu-invalid");
                    if(start.Missions!=missionsRole||start.NeutralAlternateAuthenticationBytesLength!=0)throw new NotSupportedException("unsupported-application-role");
                    if(!Authenticate(start))throw new InvalidDataException("application-credentials-rejected");
                    candidate=new(definition,[new(remote,actualClientBase.Value)],maximumPeers:1,maximumObjectReferences:64);
                    var bytes=new byte[11];var bit=0;EdServerControlMessageCodec.WriteLetterType(bytes,ref bit,27,16);
                    EdServerAdminMessageCodec.WriteReadyBody(bytes,ref bit,new(definition.SessionId,missionsRole));payloads.Add(new(command,bytes));starts++;
                    continue;
                }
                if(candidate is null)throw new InvalidDataException("application-not-started");
                if(command.State is {} update)
                {
                    if(!RecognizesController(update.ObjectReference)||update.ConstructionVersion!=ControllerConstructionVersion)
                        throw new InvalidDataException("controller-state-mismatch");
                    if(!update.SecondaryArchive)throw new NotSupportedException("controller-state-unimplemented");
                    if(!ownsArchive){nextArchive=secondaryArchive?.Fork()??new(controllerReference,ControllerConstructionVersion,ControllerSecondaryDefaults,39);ownsArchive=true;}
                    nextArchive!.Apply(update);
                    var bytes=new byte[10];var p=0;EdServerControlMessageCodec.WriteLetterType(bytes,ref p,19,16);
                    EdServerObjectReplicationCodec.WriteAcknowledgementBody(bytes,ref p,new(controllerReference,true,true,update.Sequence));
                    payloads.Add(new(command,bytes,false));continue;
                }
                if(command.Call is {} call)
                {
                    var diagnosticPayload=call.CopyPayload();
                    try{OfflineDiagnostics.Current?.Write("debug","protocol.rpc","rpc.received","Native RPC entered local dispatch.",
                        new Dictionary<string,string?>{["sessionRole"]=RoleName,["letterType"]=command.Type.ToString(),
                            ["rpcMethod"]=diagnosticPayload.Length>0?diagnosticPayload[0].ToString():"missing",
                            ["payloadBits"]=call.PayloadBits.ToString(),["flag"]=call.Flag.ToString(),["referenceKind"]=CallReferenceKind(call.ObjectReference)});}
                    finally{CryptographicOperations.ZeroMemory(diagnosticPayload);}
                    if(!RecognizesController(call.ObjectReference))throw new InvalidDataException("controller-call-mismatch");
                    //64FD00 passes the wire flag to native dispatch. It is not
                    //a request-direction bit:96DB80->937100 sends method70 with
                    //flag0. This authenticated server owns this exact controller;
                    //never use that flag in place of the identity/reference gate.
                    if(missionsRole&&command.Type==23&&call.PayloadBits==40)
                    {
                        var payload=call.CopyPayload();var recordMap=payload[0]==78;CryptographicOperations.ZeroMemory(payload);
                        if(recordMap)
                        {
                            var requestId=EdServerPersistentRecordMapMessageCodec.ReadRequest(call);
                            // The fresh local authority has no persistent mission
                            // records. Return the actual empty map through its
                            // native promise/correlation path, without setting
                            // connection, world or completion flags.
                            using var response=EdServerPersistentRecordMapMessageCodec.CreateEmptyReply(controllerReference,requestId);
                            var bytes=new byte[(16+EdServerCallEnvelopeCodec.GetBodyBitLength(response,false)+7)/8];var p=0;
                            EdServerControlMessageCodec.WriteLetterType(bytes,ref p,23,16);
                            EdServerCallEnvelopeCodec.WriteBody(bytes,ref p,response,false);
                            payloads.Add(new(command,bytes));continue;
                        }
                    }
                    if(command.Type==22&&call.PayloadBits==8)
                    {
                        var payload=call.CopyPayload();
                        var arena=payload[0]==70;
                        CryptographicOperations.ZeroMemory(payload);
                        if(arena)
                        {
                            EdServerArenaCatalogueMessageCodec.ReadRequest(call);
                            //This private Solo service advertises no Arena modes.
                            //The exact native consumer2731000 handles zero entries;
                            //do not leave70 rejecting the entire reliable batch.
                            using var response=EdServerArenaCatalogueMessageCodec.CreateEmptyReply(controllerReference);
                            var bytes=new byte[(16+EdServerCallEnvelopeCodec.GetBodyBitLength(response,false)+7)/8];var p=0;
                            EdServerControlMessageCodec.WriteLetterType(bytes,ref p,22,16);
                            EdServerCallEnvelopeCodec.WriteBody(bytes,ref p,response,false);
                            payloads.Add(new(command,bytes,ArenaCatalogueReply:true));continue;
                        }
                    }
                    if(command.Type==23&&call.PayloadBits==88)
                    {
                        var query=EdServerPeerInfoMessageCodec.ReadRequest(call);
                        // This one-player management service publishes no peer-to-peer
                        // directory. Complete the lookup with the native negative
                        // result rather than inventing a machine, endpoint or key.
                        var peer=resolvePeer?.Invoke(query.MachineReference);
                        OfflineDiagnostics.Current?.Write("debug","protocol.rpc","peer.lookup","Native machine directory lookup completed.",
                            new Dictionary<string,string?>{["nativeUserId"]=nativeUserId.ToString(),["requestedMachine"]=query.MachineReference.ToString(),
                                ["callerMachine"]=remote.ToString(),["available"]=(peer is not null).ToString()});
                        using var response=peer is null?EdServerPeerInfoMessageCodec.CreateUnavailableReply(controllerReference,query.RequestId)
                            :EdServerPeerInfoMessageCodec.CreateAvailableReply(controllerReference,query.RequestId,peer.Identity,peer.ConnectionDetails,peer.EncryptionKey,peer.NativeUserId);
                        var bytes=new byte[(16+EdServerCallEnvelopeCodec.GetBodyBitLength(response,false)+7)/8];var p=0;
                        EdServerControlMessageCodec.WriteLetterType(bytes,ref p,22,16);
                        EdServerCallEnvelopeCodec.WriteBody(bytes,ref p,response,false);
                        payloads.Add(new(command,bytes,UnavailablePeerReply:peer is null));continue;
                    }
                    if(command.Type==23&&call.PayloadBits==8||command.Type==22&&call.PayloadBits==72)
                    {
                        var payload=call.CopyPayload();
                        try
                        {
                            // The pinned callbacks15/16/35/36 are one-way server
                            // notifications. Record receipt only in this minimum
                            // private service.35's U64 is ignored by9884B0 as well;
                            // identity comes from the authenticated native peer.
                            // No session/world readiness or gameplay state is set.
                            var receipt=command.Type==23?payload[0] switch{15=>1u,16=>2u,36=>8u,_=>0u}
                                :payload[0]==35?4u:0u;
                            if(receipt!=0){nextNotifications|=receipt;continue;}
                        }
                        finally{CryptographicOperations.ZeroMemory(payload);}
                    }
                    if(command.Type==22&&call.PayloadBits==72)
                    {
                        var payload=call.CopyPayload();
                        var isPostGeneration=payload[0]==EdServerStellarForgeMessageCodec.RequestMethod;
                        CryptographicOperations.ZeroMemory(payload);
                        if(isPostGeneration)
                        {
                            var address=EdServerStellarForgeMessageCodec.ReadRequest(call);
                            if(selectedSystemAddress is null||address!=selectedSystemAddress.Value)
                                throw new InvalidDataException("controller-postgen-selected-system-required");
                            using var response=EdServerStellarForgeMessageCodec.CreateNoOverridesReply(controllerReference,address);
                            var bytes=new byte[(16+EdServerCallEnvelopeCodec.GetBodyBitLength(response,false)+7)/8];var p=0;
                            EdServerControlMessageCodec.WriteLetterType(bytes,ref p,22,16);
                            EdServerCallEnvelopeCodec.WriteBody(bytes,ref p,response,false);
                            payloads.Add(new(command,bytes,StellarForgeReply:true));continue;
                        }
                    }
                    if(command.Type==23&&call.PayloadBits==8)
                    {
                        var bytes=call.CopyPayload();
                        try
                        {
                            // The pinned native method47 dispatches to39F0,
                            // an empty ret. It has no parameters, reply or
                            // state transition; do not let this known no-op
                            // reject another valid control in the same batch.
                            if(bytes[0]==47)continue;
                        }
                        finally{CryptographicOperations.ZeroMemory(bytes);}
                    }
                    if(command.Type==22&&call.PayloadBits==80)
                    {
                        var bytes=call.CopyPayload();
                        try
                        {
                            if(bytes[0] is 33 or 34&&bytes[1]<=1)
                            {
                                //1D6FCA0 registers the current user profile on its
                                // primary/secondary ServerAdmin record.984020 reads
                                // Boolean, byte alignment, U64. Only this authenticated
                                // private account can be attached; no caller-supplied
                                // identity can attach a different profile.
                                if(nativeUserId==0)throw new InvalidDataException("controller-profile-context-required");
                                if(System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(2))!=nativeUserId)
                                    throw new InvalidDataException("controller-profile-identity-mismatch");
                                if(bytes[0]==33)nextRegistration33=bytes[1]!=0;else nextRegistration34=bytes[1]!=0;
                                continue;
                            }
                        }
                        finally{CryptographicOperations.ZeroMemory(bytes);}
                    }
                    if(command.Type==22&&call.PayloadBits==40)
                    {
                        var bytes=call.CopyPayload();
                        try
                        {
                            //9813A0/95A990 sends one U32;9813F0 forwards it to
                            //the server-side notification interface+30. Retain
                            //the exact authenticated receipt, with no reply or
                            //interpretation as native world readiness.
                            if(bytes[0]==128&&!call.Flag)
                            {nextScalar128=System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(1));continue;}
                        }
                        finally{CryptographicOperations.ZeroMemory(bytes);}
                    }
                    if(command.Type==23&&call.PayloadBits==16)
                    {
                        var bytes=call.CopyPayload();
                        try
                        {
                            if(bytes[0]==94&&bytes[1]<=1)
                            {
                                //978D70->1D22B60/44BF7B0 is exactly the terrain
                                // predicate used by9AB190 for user/login?terrain=0/1.
                                //96C290 forwards this one-way flag to the server peer.
                                nextTerrain=bytes[1]!=0;continue;
                            }
                        }
                        finally{CryptographicOperations.ZeroMemory(bytes);}
                    }
                    if(command.Type==20&&call.PayloadBits==104)
                    {
                        var clockRequest=EdServerClockMessageCodec.ReadRequest(call);
                        if(receivedTime is not {} receive||sentTime is not {} send)throw new InvalidDataException("controller-clock-context-required");
                        OfflineDiagnostics.Current?.Write("debug","protocol.clock","clock.reply","UTC FILETIME clock sample; monotonic timestamps are reserved for retransmission timers.",
                            new Dictionary<string,string?>{["sessionRole"]=RoleName,["echoSeconds"]=clockRequest.Seconds.ToString(),
                                ["echoNanoseconds"]=clockRequest.Nanoseconds.ToString(),["receiveSeconds"]=receive.Seconds.ToString(),
                                ["receiveNanoseconds"]=receive.Nanoseconds.ToString(),["sendSeconds"]=send.Seconds.ToString(),["sendNanoseconds"]=send.Nanoseconds.ToString(),
                                ["clockEpoch"]="1601-01-01T00:00:00Z"});
                        using var response=EdServerClockMessageCodec.CreateReply(controllerReference,clockRequest,receive,send);
                        // Native987600 constructs letter20. This clock reply is not a
                        // reliable application success and must not consume a SEQ.
                        // Our SYNC advertises outgoing alignment mode0, independent
                        // from the client's incoming mode3; its length is compressed.
                        var bytes=new byte[(16+EdServerCallEnvelopeCodec.GetBodyBitLength(response,false)+7)/8];var p=0;
                        EdServerControlMessageCodec.WriteLetterType(bytes,ref p,20,16);
                        EdServerCallEnvelopeCodec.WriteBody(bytes,ref p,response,false);
                        payloads.Add(new(command,bytes,false));continue;
                    }
                    if(rpcModule is not null)
                    {
                        var payload=call.CopyPayload();
                        try
                        {
                            if(call.PayloadBits<8)throw new InvalidDataException("rpc-module-method-required");
                            var replacement=rpcModule(new(1,RoleName,command.Type,call.Flag,command.AlignedCallValues,
                                call.PayloadBits,payload,selectedSystemAddress??0,remote,islandId,wakeId));
                            if(replacement is not null)
                            {
                                if(replacement.Protocol!=1||!replacement.Handled||replacement.Emissions is null
                                    ||replacement.Emissions.Length>8)throw new InvalidDataException("rpc-module-emissions-required");
                                foreach(var emission in replacement.Emissions)
                                {
                                    if(emission.LetterType is <20 or >23||emission.Payload is null||emission.PayloadBits is <8 or >8192
                                        ||emission.Payload.Length!=(emission.PayloadBits+7)/8)
                                        throw new InvalidDataException("rpc-module-emission-invalid");
                                    using var response=new EdServerCallEnvelope(controllerReference,emission.Flag,emission.PayloadBits,emission.Payload);
                                    var bytes=new byte[(16+EdServerCallEnvelopeCodec.GetBodyBitLength(response,false)+7)/8];var p=0;
                                    EdServerControlMessageCodec.WriteLetterType(bytes,ref p,emission.LetterType,16);
                                    EdServerCallEnvelopeCodec.WriteBody(bytes,ref p,response,false);
                                    payloads.Add(new(command,bytes,emission.Reliable));
                                }
                                continue;
                            }
                        }
                        finally{CryptographicOperations.ZeroMemory(payload);}
                    }
                    // Only the bounded envelope is implemented. Never ACK or synthesize
                    // success for an unimplemented controller method; the whole candidate
                    // batch rolls back, including any preceding Salute/REP_ACK changes.
                    throw new NotSupportedException("controller-call-unimplemented");
                }
                if(command.Salute is {} salute)
                {
                    if(candidate.GetPeerState(remote)!=EdServerSessionPeerState.Joined
                        ||salute.ObjectReference!=definition.SessionId&&salute.ObjectReference!=definition.LocalMachineRunId
                            &&(!enableControllerCreation||!nextControllerQueued||salute.ObjectReference!=controllerReference))
                        throw new InvalidDataException("controller-salute-mismatch");
                    if(salute.ObjectReference==controllerReference)
                    {nextControllerSaluted=true;nextControllerSaluteLive|=salute.Live;}
                    continue;
                }
                if(command.StateAck is {} ack)
                {
                    if(!enableControllerCreation||!nextControllerQueued||candidate.GetPeerState(remote)!=EdServerSessionPeerState.Joined
                        ||ack.ObjectReference!=controllerReference||ack.SecondaryArchive||ack.Sequence!=1)
                        throw new InvalidDataException("controller-ack-mismatch");
                    nextStateAcknowledged=true;nextLiveAcknowledged|=ack.Live;continue;
                }
                EdServerSessionNegotiationResult? result=null;
                if(command.Request is {} request)result=candidate.ReceiveRequest(remote,request);
                else if(command.Members is {} members)result=candidate.ReceiveMembers(remote,members);
                else if(command.Joined is {} joined)result=candidate.ReceiveJoined(remote,joined);
                else if(command.Left is {} left)
                {if(left.SessionId!=definition.SessionId)throw new InvalidDataException("application-session-mismatch");nextClosed=true;}
                if(result is not null)foreach(var outbound in result.Outbound)payloads.Add(new(command,Encode(outbound)));
                if(enableControllerCreation&&!nextControllerQueued&&result is{State:EdServerSessionPeerState.Joined,StateChanged:true})
                {
                    // A new per-peer factory alias is introduced by name on the wire. This is
                    // not the client's process-local factory array index. The native +OBJ
                    // layout and ServerAdmin registration are closed against the pinned EXE.
                    // 653AB0/653900 read session dependencies;650861 requires actual
                    // local membership in at least one. An empty set is never usable.
                    // This per-client control object is server-allocated, with the
                    // admitted client as its explicit native authority.978AF0 calls
                    //64A800/645AE0 and requires a local authority before invoking
                    // the original ServerLinkRegister binding callback. An absent
                    // authority is not inferred from its parent or creator.
                    var create=new EdServerLeafObjectCreate(controllerReference,EdServerObjectCreateCodec.ClientRootReference,true,new(1,"ServerAdmin"),ControllerConstructionVersion,[definition.SessionId],remote);
                    var bytes=new byte[(16+EdServerObjectCreateCodec.GetLeafBodyBitLength(create)+7)/8];var bit=0;
                    EdServerControlMessageCodec.WriteLetterType(bytes,ref bit,EdServerObjectCreateCodec.LetterType,16);
                    EdServerObjectCreateCodec.WriteLeafBody(bytes,ref bit,create);
                    payloads.Add(new(command,bytes));
                    var state=new EdServerDefaultObjectState(controllerReference,ControllerConstructionVersion,1,ControllerOptionalProperties);
                    bytes=new byte[(16+EdServerObjectReplicationCodec.GetDefaultBodyBitLength(state)+7)/8];bit=0;
                    EdServerControlMessageCodec.WriteLetterType(bytes,ref bit,EdServerObjectReplicationCodec.LetterType,16);
                    EdServerObjectReplicationCodec.WriteDefaultBody(bytes,ref bit,state);
                    payloads.Add(new(command,bytes));nextControllerQueued=true;
                    OfflineDiagnostics.Current?.Write("info","protocol.replication","controller.create.queued","Native controller create/default state queued after membership joined.",
                        new Dictionary<string,string?>{["sessionRole"]=RoleName,["constructionVersion"]=ControllerConstructionVersion.ToString(),["registeredMethods"]=ControllerRegisteredMethods.ToString()});
                }
            }
            return new(this,version,candidate,starts,nextClosed,nextControllerQueued,nextStateAcknowledged,nextLiveAcknowledged,nextControllerSaluted,nextControllerSaluteLive,nextTerrain,nextRegistration33,nextRegistration34,nextNotifications,nextScalar128,nextArchive,ownsArchive,payloads);
        }
        catch{rpcTransaction?.Invoke(false);if(ownsArchive)nextArchive?.Dispose();foreach(var emission in payloads)CryptographicOperations.ZeroMemory(emission.Payload);throw;}
    }
    private bool Authenticate(EdServerStartServerAdmin message)
    {
        var machine=message.CopyMachineTokenBytes();var auth=message.CopyAuthTokenBytes();
        Span<byte> mh=stackalloc byte[32];Span<byte> ah=stackalloc byte[32];
        try{SHA256.HashData(machine,mh);SHA256.HashData(auth,ah);return CryptographicOperations.FixedTimeEquals(mh,machineDigest)&CryptographicOperations.FixedTimeEquals(ah,authDigest);}
        finally{CryptographicOperations.ZeroMemory(machine);CryptographicOperations.ZeroMemory(auth);mh.Clear();ah.Clear();}
    }
    private static byte[] Encode(EdServerSessionOutbound outbound)
    {
        var bytes=new byte[512];var bit=0;
        try
        {
            EdServerControlMessageCodec.WriteLetterType(bytes,ref bit,(byte)outbound.Letter,16);
            switch(outbound)
            {
                case EdServerSessionRequestOutbound request:EdServerSessionMessageCodec.WriteRequestBody(bytes,ref bit,request.Message);break;
                case EdServerSessionMembersOutbound members:EdServerSessionMessageCodec.WriteMembersBody(bytes,ref bit,members.Message,64);break;
                case EdServerSessionJoinedOutbound joined:EdServerSessionMessageCodec.WriteJoinedBody(bytes,ref bit,joined.Message,64);break;
                default:throw new NotSupportedException("unsupported-application-reply");
            }
            return bytes[..((bit+7)/8)];
        }
        finally{CryptographicOperations.ZeroMemory(bytes);}
    }
    internal sealed record Emission(LocalApplicationCommand Owner,byte[] Payload,bool Reliable=true,bool StellarForgeReply=false,bool UnavailablePeerReply=false,bool ArenaCatalogueReply=false);
    internal sealed class Plan(LocalReplicationApplication owner,long expected,EdServerSessionNegotiationMachine? candidate,int starts,bool nextClosed,bool nextControllerQueued,bool nextStateAcknowledged,bool nextLiveAcknowledged,bool nextControllerSaluted,bool nextControllerSaluteLive,bool? nextTerrain,bool? nextRegistration33,bool? nextRegistration34,uint nextNotifications,uint? nextScalar128,EdServerAdminSecondaryArchive? nextArchive,bool ownsArchive,List<Emission> emissions):IDisposable
    {
        private bool committed;
        internal IReadOnlyList<Emission> Emissions=>emissions;
        internal void Commit()
        {
            if(owner.closed||owner.version!=expected)throw new InvalidOperationException("Private session candidate became stale.");
            owner.rpcTransaction?.Invoke(true);
            owner.membership=candidate;owner.AcceptedStarts+=starts;owner.closed=nextClosed;owner.version++;
            owner.controllerCreationQueued=nextControllerQueued;
            owner.controllerStateAcknowledged=nextStateAcknowledged;owner.controllerLiveAcknowledged=nextLiveAcknowledged;
            owner.controllerSaluted=nextControllerSaluted;owner.controllerSaluteLive=nextControllerSaluteLive;
            owner.terrainCapability=nextTerrain;
            owner.profileRegistration33=nextRegistration33;owner.profileRegistration34=nextRegistration34;
            owner.clientNotificationReceipts=nextNotifications;
            owner.clientScalar128=nextScalar128;
            if(ownsArchive){owner.secondaryArchive?.Dispose();owner.secondaryArchive=nextArchive;}
            owner.ClockReplies+=emissions.Count(value=>!value.Reliable&&value.Owner.Type==20);
            owner.StellarForgeNoOverridesReplies+=emissions.Count(value=>value.StellarForgeReply);
            owner.UnavailablePeerInfoReplies+=emissions.Count(value=>value.UnavailablePeerReply);
            owner.EmptyArenaCatalogueReplies+=emissions.Count(value=>value.ArenaCatalogueReply);
            foreach(var emission in emissions)
            {
                try
                {
                    var bit=0;var type=EdServerControlMessageCodec.ReadLetterType(emission.Payload,ref bit,16);
                    if(type is not(20 or 21 or 22 or 23))continue;
                    using var call=EdServerCallEnvelopeCodec.ReadBody(emission.Payload,ref bit,false);
                    var payload=call.CopyPayload();
                    try{OfflineDiagnostics.Current?.Write("debug","protocol.rpc","rpc.reply.queued","Committed native RPC response queued.",
                        new Dictionary<string,string?>{["sessionRole"]=owner.RoleName,["letterType"]=type.ToString(),
                            ["rpcMethod"]=payload.Length>0?payload[0].ToString():"missing",["payloadBits"]=call.PayloadBits.ToString(),
                            ["flag"]=call.Flag.ToString(),["reliable"]=emission.Reliable.ToString()});}
                    finally{CryptographicOperations.ZeroMemory(payload);}
                }
                catch(Exception error){OfflineDiagnostics.Current?.Write("warning","protocol.rpc","diagnostic.decode.failed","Could not describe an already committed reply.",exception:error);}
            }
            committed=true;
        }
        public void Dispose(){if(!committed)owner.rpcTransaction?.Invoke(false);if(ownsArchive&&!committed)nextArchive?.Dispose();foreach(var emission in emissions)CryptographicOperations.ZeroMemory(emission.Payload);}
    }
    internal object Snapshot()=>new{started=membership is not null,acceptedStarts=AcceptedStarts,peerState=PeerState.ToString(),closed,
        controllerCreationEnabled=enableControllerCreation,controllerCreationQueued,controllerInitialStateQueued=controllerCreationQueued,
        controllerStateAcknowledged,controllerLiveAcknowledged,controllerSaluted,controllerSaluteLive,terrainCapability,profileRegistration33,profileRegistration34,clockReplies=ClockReplies,
        stellarForgeNoOverridesReplies=StellarForgeNoOverridesReplies,
        unavailablePeerInfoReplies=UnavailablePeerInfoReplies,emptyArenaCatalogueReplies=EmptyArenaCatalogueReplies,clientNotificationReceipts,clientScalar128,
        notificationPolicy="receipt-only; no optional server subsystem or world transition",
        secondaryArchiveStates=secondaryArchive?.StateCount??0,secondaryArchiveSequence=secondaryArchive?.LatestSequence??0,worldReady=false};
    public void Dispose(){closed=true;membership=null;secondaryArchive?.Dispose();secondaryArchive=null;CryptographicOperations.ZeroMemory(machineDigest);CryptographicOperations.ZeroMemory(authDigest);}
}
