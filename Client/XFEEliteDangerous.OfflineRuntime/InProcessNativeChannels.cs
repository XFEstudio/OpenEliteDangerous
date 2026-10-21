using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using XFEEliteDangerous.Core;
using XFEEliteDangerous.Client;
using System.Runtime.CompilerServices;

namespace XFEEliteDangerous.OfflineRuntime;

/// <summary>
/// A local implementation of the pinned game's datagram channel interface.
/// Packets remain in this process. Replies enter its original receive queue on
/// the natural update thread, after the send call has returned. The original
/// manager, protocol readers and game flow still determine session readiness.
/// </summary>
internal static unsafe class InProcessNativeChannels
{
    private sealed class Channel(ushort port)
    {
        internal readonly ushort Port = port;
        internal readonly Queue<OfflineLocalPacket> Replies = [];
        internal nint PendingRead;
        internal long Sent, Received;
    }
    private const int ReadSize = 0x6B0, BufferSize = 0x5C0;
    private static readonly object gate = new();
    private static readonly Dictionary<nint, Channel> channels = [];
    private static readonly List<NativeHook> hooks = [];
    private static delegate* unmanaged<nint, void> originalDestroy;
    private static nint image;
    private static INativeWorldAuthority authority = null!;
    private static string log = "";
    private static int entries, nextPort = 45000, pumping;

    internal static void Install(nint module, INativeWorldAuthority world, string directory)
    {
        image = module; authority = world; log = Path.Combine(directory, "native-local-sessions.tsv");
        Check(0x4C4D700, "40555356574154415541564157488dac2428feffff4881ecd8020000488b0545");
        Check(0x4C4DE50, "48895c24105556574883ec50488b818800000033ed48898198000000488bf248");
        Check(0x4C4E790, "4053574154415541564881ecd0000000488b05c18125014833c448898424b000");
        Check(0x4C4A280, "405641564883ec6848899c2480000000488d050125e200488901488bf1488b49");
        Check(0x4C4EB30, "4053555657415641574881ecc8000000488b05217e25014833c448898424b000");
        Check(0x53A590, "4883ec288b15deadb0054c8bc965488b042558000000b9280000004c8b04d04a");
        Check(0x4C4B550, "40534883ec2048837950ff488bd97416e82b3e000048837b6000750a488d4368");
        hooks.Add(new(image + 0x4C4D700, (nint)(delegate* unmanaged<nint,nint,ushort,ushort,void>)&Initialize));
        hooks.Add(new(image + 0x4C4DE50, (nint)(delegate* unmanaged<nint,nint,ulong,nint,int,int>)&Send));
        hooks.Add(new(image + 0x4C4E790, (nint)(delegate* unmanaged<nint,nint,void>)&PostRead));
        hooks.Add(new(image + 0x4C4B550, (nint)(delegate* unmanaged<nint,nint>)&AcquireWriteBuffer));
        var destroy = new NativeHook(image + 0x4C4A280, (nint)(delegate* unmanaged<nint,void>)&Destroy);
        originalDestroy = (delegate* unmanaged<nint,void>)destroy.Trampoline; hooks.Add(destroy);
        authority.Diagnostic = value=>Record(value);
        foreach (var hook in hooks) hook.Enable();
        Record("INSTALLED\tin-process-channel\tnative-receive-queue");
    }

    [UnmanagedCallersOnly]
    private static void Initialize(nint channel, nint listener, ushort requestedPort, ushort unused)
    {
        try
        {
            lock (gate)
            {
                if (!IsChannel(channel) || *(byte*)(channel+0x10)!=0 || listener==0 || channels.ContainsKey(channel))
                { Record("CHANNEL_REJECT\tlayout-family-or-lifetime"); return; }
                // The native base initializer only stores the listener (+8).
                // Port/address are logical labels, with no operating-system bind.
                var port = requestedPort == 0 ? checked((ushort)nextPort++) : requestedPort;
                if (channels.Values.Any(value=>value.Port==port)) throw new InvalidDataException("duplicate-local-port");
                var state = new Channel(port) { PendingRead = AllocateRead(channel) };
                channels.Add(channel,state);
                *(nint*)(channel+8)=listener;
                *(ushort*)(channel+0x18)=port;
                *(ushort*)(channel+0x20)=port;
                *(uint*)(channel+0x1C)=0x7F000001;
                Record("CHANNEL_OPEN\t"+port);
            }
        }
        catch (Exception error) { Failure("initialize",error);Record("CHANNEL_ERROR\tinit\t"+error.GetType().Name); }
    }

    [UnmanagedCallersOnly]
    private static nint AcquireWriteBuffer(nint channel)
    {
        lock(gate)return channels.ContainsKey(channel) && *(nint*)(channel+0x60)==0 ? channel+0x68 : 0;
    }

    [UnmanagedCallersOnly]
    private static int Send(nint channel, nint sharedReference, ulong bitCount, nint address, int addressBytes)
    {
        try
        {
            lock (gate)
            {
                if (!channels.TryGetValue(channel,out var state) || bitCount is 0 or >BufferSize*8
                    || addressBytes is not(16 or 128) || !Read(address,out ushort family) || family!=2
                    || !Read(address+4,out uint ipv4) || ipv4!=0x0100007F)
                { Record("SEND_REJECT\tchannel-size-or-local-address"); return 10049; }
                Span<byte> portBytes=stackalloc byte[2];
                if (!Copy(address+2,portBytes)) {Record("SEND_REJECT\tunreadable-address");return 10049;}
                var targetPort=BinaryPrimitives.ReadUInt16BigEndian(portBytes);
                if (!authority.CanRoute(targetPort)) { Record("SEND_REJECT\tunknown-local-role"); return 10049; }
                var bytes=new byte[checked((int)((bitCount+7)/8))];
                if (!Read(channel+0x88,out nint buffer) || !Copy(buffer,bytes)) {Record("SEND_REJECT\tunreadable-payload");return 10014;}
                using var operation=OfflineDiagnostics.Current?.Begin("native.channel","channel.send",
                    new Dictionary<string,string?>{["channelPort"]=state.Port.ToString(),["targetPort"]=targetPort.ToString(),
                        ["packetBits"]=bitCount.ToString(),["nativeEntryRva"]="0x4C4DE50",["sendOrdinal"]=(state.Sent+1).ToString()});
                try { Enqueue(state,authority.Receive(state.Port,targetPort,bytes)); }
                finally { CryptographicOperations.ZeroMemory(bytes); }
                if (++state.Sent<=8) Record("SEND\t"+state.Port+"\t"+targetPort+"\t"+bitCount);
                return 0;
            }
        }
        catch (Exception error) { Failure("send",error);Record("CHANNEL_ERROR\tsend\t"+error.GetType().Name); return 10022; }
        finally
        {
            // This audited send entry consumes one native reference on every
            // return path. Its synchronous completion callback is a native no-op.
            if (Read(sharedReference,out nint owned) && owned!=0) ReleaseSend(owned);
        }
    }

    [UnmanagedCallersOnly]
    private static void PostRead(nint channel,nint context)
    {
        try
        {
            lock (gate)
            {
                if (!channels.TryGetValue(channel,out var state)) { DeleteRead(context); return; }
                if (!IsRead(context,channel) || state.PendingRead!=0) throw new InvalidDataException("native-read-ownership");
                *(int*)(context+0x10)=2;
                state.PendingRead=context;
            }
        }
        catch (Exception error) { Failure("post-read",error);Record("CHANNEL_ERROR\tpost\t"+error.GetType().Name); }
    }

    // Called after the original game-flow update, before the diagnostic throttle.
    internal static void Pump()
    {
        if (Interlocked.Exchange(ref pumping,1)!=0) return;
        try
        {
            lock (gate)
            {
                foreach (var pair in channels)
                {
                    var state=pair.Value;
                    Enqueue(state,authority.Poll(state.Port));
                    for (var budget=0;budget<32 && state.PendingRead!=0 && state.Replies.Count>0;budget++)
                    {
                        var context=state.PendingRead;
                        if (!IsRead(context,pair.Key)) throw new InvalidDataException("native-pending-read-layout");
                        var packet=state.Replies.Dequeue();
                        try
                        {
                            packet.Payload.CopyTo(new Span<byte>((void*)(context+0x40),BufferSize));
                            // Native receive polling reads +610 as byte count;
                            // +614 is the receive-flags output, not its length.
                            *(uint*)(context+0x610)=checked((uint)packet.Payload.Length);
                            *(uint*)(context+0x614)=0;
                            var address=new Span<byte>((void*)(context+0x618),128); address.Clear();
                            BinaryPrimitives.WriteUInt16LittleEndian(address,2);
                            BinaryPrimitives.WriteUInt16BigEndian(address[2..],packet.SourcePort);
                            address[4]=127; address[7]=1; *(int*)(context+0x698)=16;
                            state.PendingRead=0;
                            OfflineDiagnostics.Current?.Write("debug","native.channel","receive.deliver.begin","Delivering local bytes to the original native receive completion.",
                                new Dictionary<string,string?>{["channelPort"]=state.Port.ToString(),["sourcePort"]=packet.SourcePort.ToString(),
                                    ["packetBytes"]=packet.Payload.Length.ToString(),["receiveOrdinal"]=(state.Received+1).ToString(),["nativeEntryRva"]="0x4C4EB30"});
                            ((delegate* unmanaged<nint,int,nint,void>)(image+0x4C4EB30))(pair.Key,0,context);
                            OfflineDiagnostics.Current?.Write("debug","native.channel","receive.deliver.complete","Original receive completion returned.",
                                new Dictionary<string,string?>{["channelPort"]=state.Port.ToString(),["receiveOrdinal"]=(state.Received+1).ToString()});
                            if (++state.Received<=8)Record("RECEIVE\t"+state.Port+"\t"+packet.SourcePort+"\t"+packet.Payload.Length);
                        }
                        finally { CryptographicOperations.ZeroMemory(packet.Payload); }
                    }
                }
            }
        }
        catch (Exception error) { Failure("pump",error);Record("CHANNEL_ERROR\tpump\t"+error.GetType().Name); }
        finally { Volatile.Write(ref pumping,0); }
    }

    [UnmanagedCallersOnly]
    private static void Destroy(nint channel)
    {
        lock (gate)
        {
            try
            {
                if (channels.Remove(channel,out var state))
                {
                    if(state.PendingRead!=0)DeleteRead(state.PendingRead);
                    foreach(var packet in state.Replies)CryptographicOperations.ZeroMemory(packet.Payload);
                    Record("CHANNEL_CLOSE\t"+state.Port+"\t"+state.Sent+"\t"+state.Received);
                }
            }
            catch(Exception error){Failure("destroy",error);Record("CHANNEL_ERROR\tdestroy\t"+error.GetType().Name);}
            originalDestroy(channel);
        }
    }
    private static void Enqueue(Channel state,IReadOnlyList<OfflineLocalPacket> packets)
    {
        foreach(var packet in packets)
        {
            if(packet.Payload.Length>BufferSize || state.Replies.Count>=128)
            {CryptographicOperations.ZeroMemory(packet.Payload);Record("DELIVERY_REJECT\tsize-or-queue-bound");}
            else state.Replies.Enqueue(packet);
        }
    }
    private static nint AllocateRead(nint channel)
    {
        var context=((delegate* unmanaged<nuint,nint>)(image+0x53A590))(ReadSize);
        if(context==0)throw new OutOfMemoryException();
        new Span<byte>((void*)context,ReadSize).Clear();
        *(nint*)context=image+0x5A6C788;
        *(int*)(context+0x10)=2; *(nint*)(context+0x18)=channel;
        *(nint*)(context+0x38)=context;
        *(uint*)(context+0x600)=BufferSize; *(nint*)(context+0x608)=context+0x40;
        return context;
    }
    private static bool IsChannel(nint value)=>Read(value,out nint table)&&table==image+0x5A6C798;
    private static bool IsRead(nint value,nint channel)=>Read(value,out nint table)&&table==image+0x5A6C788
        &&Read(value+0x18,out nint owner)&&owner==channel&&Read(value+8,out int references)&&references==0;
    private static void DeleteRead(nint context)
    {
        if(!Read(context,out nint table)||table!=image+0x5A6C788)throw new InvalidDataException("native-read-delete-layout");
        ((delegate* unmanaged<nint,uint,nint>)(*(nint*)table))(context,1);
    }
    private static void ReleaseSend(nint value)
    {
        // This object's count is deliberately non-atomic in the original send.
        if(!Read(value+8,out int count)||count<=0||count>65536||!Read(value,out nint table)
            ||table<image+0x5000000||table>=image+0x6000000)return;
        *(int*)(value+8)=count-1;
        if(count==1)((delegate* unmanaged<nint,uint,nint>)(*(nint*)table))(value,1);
    }
    private static bool Read<T>(nint source,out T value)where T:unmanaged
    {
        T copy=default;value=default;
        if(source<0x10000 || source>nint.MaxValue-sizeof(T)
            ||!ProbeNative.ReadProcessMemory(Native.GetCurrentProcess(),source,&copy,(nuint)sizeof(T),out var read)||read!=(nuint)sizeof(T))return false;
        value=copy;return true;
    }
    private static bool Copy(nint source,Span<byte> bytes)
    {
        fixed(byte* destination=bytes)return source>=0x10000 && source<=nint.MaxValue-bytes.Length
            &&ProbeNative.ReadProcessMemory(Native.GetCurrentProcess(),source,destination,(nuint)bytes.Length,out var read)&&read==(nuint)bytes.Length;
    }
    private static void Check(int rva,string expected)
    {
        if(!new ReadOnlySpan<byte>((void*)(image+rva),expected.Length/2).SequenceEqual(Convert.FromHexString(expected)))
            throw new InvalidDataException("The pinned local channel entry differs: "+rva.ToString("X"));
    }
    private static void Failure(string operation,Exception error,[CallerFilePath]string file="",[CallerLineNumber]int line=0,[CallerMemberName]string member="")
        =>OfflineDiagnostics.Current?.Write("error","native.channel","channel.failed",operation,exception:error,file:file,line:line,member:member);
    private static void Record(string value,[CallerFilePath]string file="",[CallerLineNumber]int line=0,[CallerMemberName]string member="")
    {
        OfflineDiagnostics.Current?.Write(value.Contains("ERROR",StringComparison.Ordinal)||value.Contains("REJECT",StringComparison.Ordinal)?"warning":"debug",
            "native.channel",value.Split('\t')[0],value,file:file,line:line,member:member);
        try{lock(gate)if(entries++<4096)File.AppendAllText(log,Environment.TickCount64+"\t"+value+Environment.NewLine);}catch{}
    }
}
