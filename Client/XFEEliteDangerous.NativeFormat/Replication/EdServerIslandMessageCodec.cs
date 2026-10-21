using System.Numerics;
using System.Security.Cryptography;

namespace XFEEliteDangerous.NativeFormat.Replication;

public readonly record struct EdServerSoloIslandRequest(uint Correlation,ulong Address,bool FrameFlag,uint FlightMode);

/// <summary>Pinned RPC2 reader958EE0/968380/967E50 and RPC3 reader959600.
/// Supports the explicit zero-peer Solo request and creates two local sessions,
/// Island and Wake. It does not set native object or world readiness.</summary>
public static class EdServerIslandMessageCodec
{
    public static EdServerSoloIslandRequest ReadSoloRequest(ReadOnlySpan<byte> payload,int bits,bool alignedValues)
    {
        if(bits is <1000 or >8192||payload.Length!=(bits+7)/8)throw new InvalidDataException("Invalid native island request bound.");
        var p=0;
        if(Read(payload,ref p,8,bits)!=2)throw new InvalidDataException("Invalid native island request method.");
        var correlation=(uint)Read(payload,ref p,32,bits);
        var address=Read(payload,ref p,64,bits);
        for(var i=0;i<6;i++)FiniteDouble(payload,ref p,bits);
        var frame=Read(payload,ref p,1,bits)!=0;Align(payload,ref p,bits,alignedValues);
        //967E50: two IDs,3-bit flight mode, scalar, three IDs, two flags,
        //6-bit classification, ID, two scalars,2-bit classification, optional
        //spatial values (always serialized), radius, flag. Its caller aligns once.
        Read(payload,ref p,64,bits);Read(payload,ref p,64,bits);
        var mode=(uint)Read(payload,ref p,3,bits);Read(payload,ref p,32,bits);
        for(var i=0;i<3;i++)Read(payload,ref p,64,bits);
        Read(payload,ref p,1,bits);Read(payload,ref p,1,bits);Read(payload,ref p,6,bits);
        Read(payload,ref p,64,bits);Read(payload,ref p,32,bits);Read(payload,ref p,32,bits);Read(payload,ref p,2,bits);
        for(var i=0;i<3;i++)FiniteDouble(payload,ref p,bits);
        var radius=BitConverter.Int32BitsToSingle((int)Read(payload,ref p,32,bits));
        if(!float.IsFinite(radius))throw new InvalidDataException("Invalid native island radius.");
        Read(payload,ref p,1,bits);Align(payload,ref p,bits,alignedValues);
        var exponent=(int)Read(payload,ref p,5,bits);
        var peers=(1UL<<exponent)+Read(payload,ref p,exponent,bits)-1;
        if(peers!=0)throw new NotSupportedException("This local island service admits only the zero-peer Solo request.");
        Align(payload,ref p,bits,alignedValues);
        if(p!=bits||bits%8!=0&&(payload[^1]>>(bits%8))!=0||address==0||address>0x007F_FFFF_FFFF_FFFF)
            throw new InvalidDataException("Noncanonical native island request.");
        return new(correlation,address,frame,mode);
    }
    public static EdServerCallEnvelope CreateSoloAssignment(ulong controller,uint correlation,ulong island,ulong wake)
        =>CreateAssignment(controller,correlation,island,wake,[]);
    // 626EA0's elements call4C18CD0: each is a U48 machine reference, not
    // an IP string. The original manager resolves it through RPC13/14.
    public static EdServerCallEnvelope CreateAssignment(ulong controller,uint correlation,ulong island,ulong wake,IReadOnlyList<ulong> peers)
    {
        if(island==0||wake==0||island==wake||island>0xFFFF_FFFF_FFFF||wake>0xFFFF_FFFF_FFFF)
            throw new ArgumentException("Distinct fresh local island and wake sessions are required.");
        if(peers.Count>32||peers.Any(id=>id==0||id>0xFFFF_FFFF_FFFF)||peers.Distinct().Count()!=peers.Count)
            throw new ArgumentException("Invalid native island peers.");
        Span<byte> payload=stackalloc byte[43+peers.Count*6+1];payload.Clear();var p=0;
        Write(payload,ref p,3,8);Write(payload,ref p,correlation,32);
        Write(payload,ref p,island,64);Write(payload,ref p,wake,64);
        Write(payload,ref p,0,64);Write(payload,ref p,0,64); // no extra or ProvingGround session
        var exponent=BitOperations.Log2((uint)peers.Count+1);
        Write(payload,ref p,(uint)exponent,5);Write(payload,ref p,(uint)peers.Count+1-(1U<<exponent),exponent);
        foreach(var peer in peers)Write(payload,ref p,peer,48);
        Write(payload,ref p,0,5); // no additional U64 callback data
        Write(payload,ref p,0,5); // no additional U32 callback data
        Write(payload,ref p,0,32); // empty local diagnostic string
        try{return new(controller,false,p,payload[..((p+7)/8)]);}finally{CryptographicOperations.ZeroMemory(payload);}
    }
    private static void FiniteDouble(ReadOnlySpan<byte> data,ref int p,int bits)
    {if(!double.IsFinite(BitConverter.Int64BitsToDouble((long)Read(data,ref p,64,bits))))throw new InvalidDataException("Invalid native island coordinates.");}
    private static void Align(ReadOnlySpan<byte> data,ref int p,int bits,bool enabled)
    {if(enabled&&p%8!=0&&Read(data,ref p,8-p%8,bits)!=0)throw new InvalidDataException("Nonzero native island alignment padding.");}
    private static ulong Read(ReadOnlySpan<byte> data,ref int p,int count,int limit)
    {
        if(count is <0 or >64||p<0||p+count>limit)throw new InvalidDataException("Truncated native island request.");
        ulong value=0;for(var bit=0;bit<count;bit++,p++)value|=(ulong)((data[p/8]>>(p%8))&1)<<bit;return value;
    }
    private static void Write(Span<byte> data,ref int p,ulong value,int count)
    {for(var bit=0;bit<count;bit++,p++)if(((value>>bit)&1)!=0)data[p/8]|=(byte)(1<<(p%8));}
}
