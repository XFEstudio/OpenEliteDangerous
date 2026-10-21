using System.Security.Cryptography;
using System.Text;
namespace XFEEliteDangerous.NativeFormat.Replication;

public sealed class EdServerRouteSchemaException(string kind,uint nameBytes,int depth):NotSupportedException("Unsupported bounded route schema.")
{
    public string Kind{get;}=kind;
    public uint NameBytes{get;}=nameBytes;
    public int Depth{get;}=depth;
}

public sealed class EdServerRouteMetadata
{
    private readonly byte[] digest;
    internal EdServerRouteMetadata(ulong machine,bool request,int endpoints,IReadOnlyList<string> schemes,byte[] digest)
        =>(MachineRunId,IsRequest,EndpointCount,Schemes,this.digest)=(machine,request,endpoints,schemes,digest);
    public ulong MachineRunId{get;}
    //4C373D0 explicitly labels flag1 as "request" and flag0 as "update".
    public bool IsRequest{get;}
    public int EndpointCount{get;}
    public IReadOnlyList<string> Schemes{get;}
    public byte[] CopyIdentityDigest()=>digest.ToArray();
    public override string ToString()=>$"Route metadata: request={IsRequest}, endpoints={EndpointCount}";
}

// Pinned readers:4C372C0 U48+bool;4C18B10 ordinary narrow type name;
// IP4 factory4C10A50;IP4NAT4C10D00;Comp4C113E0;Relay4C11C10.
// This parses metadata
// only. No advertised address is used to open, redirect or authorize a socket.
public static class EdServerRouteMessageCodec
{
    public static byte[] EncodeIp4Update(ulong machine,EdServerIp4Endpoint endpoint,int typeBits=16)
    {
        if(machine is 0 or >EdServerDatagramHeader.MaximumRunId||endpoint.Address!=0x7F000001||endpoint.Port==0||endpoint.Mtu is <576 or >1472)
            throw new ArgumentException("A private logical IPv4 route is required.");
        var bytes=new byte[(typeBits+169+7)/8];var p=0;
        EdServerControlMessageCodec.WriteLetterType(bytes,ref p,(byte)EdServerBuiltInLetter.Route,typeBits);
        void Write(ulong value,int count){for(var bit=0;bit<count;bit++,p++)if(((value>>bit)&1)!=0)bytes[p/8]|=(byte)(1<<(p%8));}
        Write(machine,48);Write(0,1);Write(3,32);foreach(var value in Encoding.ASCII.GetBytes("IP4"))Write(value,8);
        Write(endpoint.Address,32);Write(endpoint.Port,16);Write(endpoint.Mtu,16);
        return bytes;
    }
    public static EdServerRouteMetadata ReadBody(ReadOnlySpan<byte> bytes,ref int bitOffset)
    {
        var start=bitOffset;var p=start;var machine=Read(bytes,ref p,48);var removed=Read(bytes,ref p,1)!=0;
        var schemes=new List<string>();var endpoints=0;
        if(!removed)ReadEndpoint(bytes,ref p,0,schemes,ref endpoints);
        if(p-start>1472*8)throw new InvalidDataException("Route metadata exceeds the local datagram bound.");
        var canonical=new byte[(p-start+7)/8];
        try
        {
            for(var index=0;index<p-start;index++)if((bytes[(start+index)/8]&(1<<((start+index)%8)))!=0)canonical[index/8]|=(byte)(1<<(index%8));
            var digest=SHA256.HashData(canonical);bitOffset=p;
            return new(machine,removed,endpoints,Array.AsReadOnly(schemes.ToArray()),digest);
        }
        finally{CryptographicOperations.ZeroMemory(canonical);}
    }
    private static void ReadEndpoint(ReadOnlySpan<byte> bytes,ref int p,int depth,List<string> schemes,ref int endpoints)
    {
        if(depth>4||schemes.Count>=16)throw new InvalidDataException("Route nesting exceeds the local bound.");
        var length=Read(bytes,ref p,32);if(length>8)throw new EdServerRouteSchemaException("name-length",(uint)length,depth);
        Span<byte> name=stackalloc byte[8];
        for(var index=0;index<(int)length;index++)name[index]=(byte)Read(bytes,ref p,8);
        var scheme=Encoding.ASCII.GetString(name[..(int)length]);schemes.Add(scheme);
        switch(scheme)
        {
            case "":return;
            case "IP4":Read(bytes,ref p,32);Read(bytes,ref p,16);Read(bytes,ref p,16);endpoints++;return;
            // Factory4C11C80: two U64 address halves, U16 port, U16 MTU.
            // Parsed metadata only, no IPv6 child socket or route authority.
            case "IP6":Read(bytes,ref p,64);Read(bytes,ref p,64);Read(bytes,ref p,16);Read(bytes,ref p,16);endpoints++;return;
            // Turn6 factory4C11ED0 reads one U32 and U16.
            case "Turn6":Read(bytes,ref p,32);Read(bytes,ref p,16);endpoints++;return;
            // Factory4C11C10 ignores the stream argument and constructs the
            // marker via4C31430. There is no body after the scheme name.
            case "Relay":endpoints++;return;
            case "IP4NAT":
                for(var index=0;index<4;index++){Read(bytes,ref p,32);Read(bytes,ref p,16);}
                Read(bytes,ref p,8);Read(bytes,ref p,1);Read(bytes,ref p,1);Read(bytes,ref p,32);endpoints++;return;
            case "Comp":
                var count=Read(bytes,ref p,16);if(count>4)throw new InvalidDataException("Route collection exceeds the local bound.");
                for(var index=0;index<(int)count;index++)ReadEndpoint(bytes,ref p,depth+1,schemes,ref endpoints);return;
            default:throw new EdServerRouteSchemaException("unknown-name",(uint)length,depth);
        }
    }
    private static ulong Read(ReadOnlySpan<byte> bytes,ref int p,int count)
    {
        if(p<0||(long)p+count>(long)bytes.Length*8)throw new InvalidDataException("Route metadata is truncated.");
        ulong value=0;for(var bit=0;bit<count;bit++,p++)value|=(ulong)((bytes[p/8]>>(p%8))&1)<<bit;return value;
    }
}
