using System.Security.Cryptography;

namespace XFEEliteDangerous.NativeFormat.Replication;

/// <summary>Owned REP envelope; parsing is independent from applying a reflected state/delta.</summary>
public sealed class EdServerObjectUpdate:IDisposable
{
    private readonly EdServerCallEnvelope data;
    public ulong ObjectReference=>data.ObjectReference;
    public bool SecondaryArchive=>data.Flag;
    public ushort ConstructionVersion{get;}
    public ushort Sequence{get;}
    public int PayloadBits=>data.PayloadBits;
    public EdServerObjectUpdate(ulong reference,bool secondary,ushort version,ushort sequence,int payloadBits,ReadOnlySpan<byte> payload)
    {
        if(sequence is 0 or >1023||payloadBits<10)throw new ArgumentException("Invalid native object state envelope.");
        data=new(reference,secondary,payloadBits,payload);ConstructionVersion=version;Sequence=sequence;
    }
    public byte[] CopyPayload()=>data.CopyPayload();
    public ushort BaseSequence
    {
        get{var bytes=CopyPayload();try{return(ushort)((bytes[0]|bytes[1]<<8)&1023);}finally{CryptographicOperations.ZeroMemory(bytes);}}
    }
    public void Dispose()=>data.Dispose();
    public override string ToString()=>$"Native REP: version{ConstructionVersion}, sequence{Sequence}, payload{PayloadBits}bits; application not implied";
}

/// <summary>651F20 reads UREP18/CertREP33: ref48, archive1, version16, sequence10, payloadLengthMinusOne20, bounded body.</summary>
public static class EdServerObjectUpdateCodec
{
    public static EdServerObjectUpdate ReadBody(ReadOnlySpan<byte> source,ref int bitOffset)
    {
        var p=bitOffset;var reference=Read(source,ref p,48);var secondary=Read(source,ref p,1)!=0;
        var version=(ushort)Read(source,ref p,16);var sequence=(ushort)Read(source,ref p,10);var bits=checked((int)Read(source,ref p,20)+1);
        if(bits is <10 or >EdServerCallEnvelopeCodec.MaximumPayloadBits)throw new InvalidDataException("native-rep-payload-bound");
        Require(source.Length,p,bits);var bytes=new byte[(bits+7)/8];
        try
        {
            for(var index=0;index<bits;index++)if(Read(source,ref p,1)!=0)bytes[index/8]|=(byte)(1<<(index%8));
            var value=new EdServerObjectUpdate(reference,secondary,version,sequence,bits,bytes);bitOffset=p;return value;
        }
        finally{CryptographicOperations.ZeroMemory(bytes);}
    }
    public static void WriteBody(Span<byte> destination,ref int bitOffset,EdServerObjectUpdate message)
    {
        ArgumentNullException.ThrowIfNull(message);Require(destination.Length,bitOffset,95+message.PayloadBits);
        var p=bitOffset;var bytes=message.CopyPayload();
        try
        {
            Write(destination,ref p,message.ObjectReference,48);Write(destination,ref p,message.SecondaryArchive?1UL:0,1);
            Write(destination,ref p,message.ConstructionVersion,16);Write(destination,ref p,message.Sequence,10);
            Write(destination,ref p,(uint)(message.PayloadBits-1),20);
            for(var index=0;index<message.PayloadBits;index++)Write(destination,ref p,(ulong)(bytes[index/8]>>(index%8)&1),1);
            bitOffset=p;
        }
        finally{CryptographicOperations.ZeroMemory(bytes);}
    }
    private static void Require(int length,int p,int bits)
    {if(p<0||bits<0||(long)p+bits>(long)length*8)throw new InvalidDataException("native-rep-truncated");}
    private static ulong Read(ReadOnlySpan<byte> data,ref int p,int bits)
    {Require(data.Length,p,bits);ulong value=0;for(var i=0;i<bits;i++,p++)value|=(ulong)(data[p/8]>>(p%8)&1)<<i;return value;}
    private static void Write(Span<byte> data,ref int p,ulong value,int bits)
    {for(var i=0;i<bits;i++,p++){var mask=1<<(p%8);data[p/8]=(byte)(data[p/8]&~mask|(((value>>i)&1)==0?0:mask));}}
}
