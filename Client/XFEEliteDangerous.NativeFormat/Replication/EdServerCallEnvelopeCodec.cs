using System.Numerics;
using System.Security.Cryptography;

namespace XFEEliteDangerous.NativeFormat.Replication;

/// <summary>Owned opaque native Call body. Decoding this envelope does not validate
/// a method, execute it or synthesize an application response.</summary>
public sealed class EdServerCallEnvelope:IDisposable
{
    private readonly byte[] payload;
    private int disposed;
    public ulong ObjectReference{get;}
    public bool Flag{get;}
    public int PayloadBits{get;}
    public EdServerCallEnvelope(ulong objectReference,bool flag,int payloadBits,ReadOnlySpan<byte> data)
    {
        if(objectReference>0xFFFFFFFFFFFF||payloadBits is <0 or >EdServerCallEnvelopeCodec.MaximumPayloadBits||data.Length!=(payloadBits+7)/8)
            throw new ArgumentException("Invalid bounded native Call envelope.");
        if(payloadBits%8!=0&&data.Length>0&&(data[^1]>>(payloadBits%8))!=0)throw new ArgumentException("Noncanonical native Call payload padding.");
        ObjectReference=objectReference;Flag=flag;PayloadBits=payloadBits;payload=data.ToArray();
    }
    public byte[] CopyPayload()
    { ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed)!=0,this);return payload.ToArray(); }
    public void Dispose(){if(Interlocked.Exchange(ref disposed,1)==0)CryptographicOperations.ZeroMemory(payload);}
    public override string ToString()=>$"Native Call envelope: {PayloadBits} payload bits; method execution not implied";
}

/// <summary>Common type20..23 Call header closed against64FD00. The serialization
/// mode must come from the authenticated native peer, independently of type width.</summary>
public static class EdServerCallEnvelopeCodec
{
    public const int MaximumPayloadBits=8192;
    public static EdServerCallEnvelope ReadBody(ReadOnlySpan<byte> source,ref int bitOffset,bool alignedValues)
    {
        var p=bitOffset;var reference=Read(source,ref p,48);var flag=Read(source,ref p,1)!=0;
        var length=alignedValues?Read(source,ref p,31):Compressed(source,ref p);
        if(length>MaximumPayloadBits)throw new InvalidDataException("Native Call exceeds the local payload bound.");
        Require(source.Length,p,(int)length);var payload=new byte[((int)length+7)/8];
        try
        {
            for(var index=0;index<(int)length;index++)
                if(Read(source,ref p,1)!=0)payload[index/8]|=(byte)(1<<(index%8));
            var result=new EdServerCallEnvelope(reference,flag,(int)length,payload);bitOffset=p;return result;
        }
        finally{CryptographicOperations.ZeroMemory(payload);}
    }
    public static int GetBodyBitLength(EdServerCallEnvelope message,bool alignedValues)
    {
        ArgumentNullException.ThrowIfNull(message);
        return 49+(alignedValues?31:5+BitOperations.Log2((uint)message.PayloadBits+1))+message.PayloadBits;
    }
    public static void WriteBody(Span<byte> target,ref int bitOffset,EdServerCallEnvelope message,bool alignedValues)
    {
        Require(target.Length,bitOffset,GetBodyBitLength(message,alignedValues));var p=bitOffset;var payload=message.CopyPayload();
        try
        {
            Write(target,ref p,message.ObjectReference,48);Write(target,ref p,message.Flag?1UL:0,1);
            if(alignedValues)Write(target,ref p,(uint)message.PayloadBits,31);
            else
            {
                var exponent=BitOperations.Log2((uint)message.PayloadBits+1);Write(target,ref p,(uint)exponent,5);
                Write(target,ref p,(ulong)message.PayloadBits+1-(1UL<<exponent),exponent);
            }
            for(var index=0;index<message.PayloadBits;index++)Write(target,ref p,(ulong)((payload[index/8]>>(index%8))&1),1);
            bitOffset=p;
        }
        finally{CryptographicOperations.ZeroMemory(payload);}
    }
    private static ulong Compressed(ReadOnlySpan<byte> data,ref int p)
    { var exponent=(int)Read(data,ref p,5);return (1UL<<exponent)+Read(data,ref p,exponent)-1; }
    private static void Require(int length,int p,int bits)
    { if(p<0||bits<0||(long)p+bits>(long)length*8)throw new InvalidDataException("Native Call envelope is truncated."); }
    private static ulong Read(ReadOnlySpan<byte> data,ref int p,int bits)
    {
        Require(data.Length,p,bits);ulong value=0;
        for(var index=0;index<bits;index++,p++)value|=(ulong)((data[p/8]>>(p%8))&1)<<index;
        return value;
    }
    private static void Write(Span<byte> data,ref int p,ulong value,int bits)
    {
        for(var index=0;index<bits;index++,p++)
        { var mask=1<<(p%8);data[p/8]=(byte)((data[p/8]&~mask)|(((value>>index)&1)==0?0:mask)); }
    }
}
