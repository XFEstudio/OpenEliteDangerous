namespace XFEEliteDangerous.NativeFormat.Replication;

/// <summary>
/// Initial primary-archive REP carrying the registered type's defaults. The mask
/// width is supplied by the verified type schema; zero bits mean no overrides.
/// This subset cannot encode deltas, secondary archives or arbitrary properties.
/// </summary>
public sealed record EdServerDefaultObjectState(ulong ObjectReference,ushort ConstructionVersion,ushort Sequence,int OptionalPropertyCount);
public sealed record EdServerObjectStateAcknowledgement(ulong ObjectReference,bool SecondaryArchive,bool Live,ushort Sequence);
public sealed record EdServerObjectSalute(ulong ObjectReference,bool Certified,bool Live);

public static class EdServerObjectReplicationCodec
{
    public const byte LetterType=18;
    public const int MaximumOptionalProperties=512;
    public const byte AcknowledgementLetterType=19;
    public const byte SaluteLetterType=32;

    // Registered at62C8D6..62C8F6. Reader64E9B0 and formatter63E4E0 agree on
    // ref48 and the C/L flags. C is observed only; it cannot grant authority.
    public static EdServerObjectSalute ReadSaluteBody(ReadOnlySpan<byte> source,ref int bitOffset)
    {
        var p=bitOffset;var value=new EdServerObjectSalute(Read(source,ref p,48),Read(source,ref p,1)!=0,Read(source,ref p,1)!=0);
        bitOffset=p;return value;
    }
    public static void WriteSaluteBody(Span<byte> destination,ref int bitOffset,EdServerObjectSalute message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if(message.ObjectReference>0xFFFFFFFFFFFF)throw new ArgumentOutOfRangeException(nameof(message));
        Require(destination.Length,bitOffset,50);var p=bitOffset;
        Write(destination,ref p,message.ObjectReference,48);Write(destination,ref p,message.Certified?1UL:0,1);
        Write(destination,ref p,message.Live?1UL:0,1);bitOffset=p;
    }

    // 651C60 reads ref48, two Booleans, sequence10. The first selects the
    // archive in654500; the second is the peer live flag (63FFC0/6586B0).
    public static EdServerObjectStateAcknowledgement ReadAcknowledgementBody(ReadOnlySpan<byte> source,ref int bitOffset)
    {
        var p=bitOffset;var value=new EdServerObjectStateAcknowledgement(Read(source,ref p,48),Read(source,ref p,1)!=0,
            Read(source,ref p,1)!=0,(ushort)Read(source,ref p,10));bitOffset=p;return value;
    }
    public static void WriteAcknowledgementBody(Span<byte> destination,ref int bitOffset,EdServerObjectStateAcknowledgement message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if(message.ObjectReference>0xFFFFFFFFFFFF||message.Sequence>1023)throw new ArgumentOutOfRangeException(nameof(message));
        Require(destination.Length,bitOffset,60);var p=bitOffset;
        Write(destination,ref p,message.ObjectReference,48);Write(destination,ref p,message.SecondaryArchive?1UL:0,1);
        Write(destination,ref p,message.Live?1UL:0,1);Write(destination,ref p,message.Sequence,10);bitOffset=p;
    }

    // 651F20: ref48,selector1,version16,sequence10,payloadLengthMinusOne20.
    // 64CB30: baseSequence10. Base0 -> 636720/6339D0: raw optional-property
    // mask followed by overrides until the declared end. Defaults need no overrides.
    public static int GetDefaultBodyBitLength(EdServerDefaultObjectState message)
    { Validate(message);return 105+message.OptionalPropertyCount; }

    public static void WriteDefaultBody(Span<byte> destination,ref int bitOffset,EdServerDefaultObjectState message)
    {
        Validate(message);Require(destination.Length,bitOffset,105+message.OptionalPropertyCount);
        var p=bitOffset;
        Write(destination,ref p,message.ObjectReference,48);Write(destination,ref p,0,1);
        Write(destination,ref p,message.ConstructionVersion,16);Write(destination,ref p,message.Sequence,10);
        Write(destination,ref p,(uint)(9+message.OptionalPropertyCount),20);
        Write(destination,ref p,0,10);
        for(var i=0;i<message.OptionalPropertyCount;i++)Write(destination,ref p,0,1);
        bitOffset=p;
    }

    public static EdServerDefaultObjectState ReadDefaultBody(ReadOnlySpan<byte> source,ref int bitOffset,int expectedOptionalPropertyCount)
    {
        if(expectedOptionalPropertyCount is <0 or >MaximumOptionalProperties)throw new ArgumentOutOfRangeException(nameof(expectedOptionalPropertyCount));
        var p=bitOffset;var reference=Read(source,ref p,48);
        if(Read(source,ref p,1)!=0)throw new NotSupportedException("Secondary native archives are outside the initial-default subset.");
        var version=(ushort)Read(source,ref p,16);var sequence=(ushort)Read(source,ref p,10);
        var payloadBits=Read(source,ref p,20)+1;
        if(payloadBits!=(ulong)(10+expectedOptionalPropertyCount))throw new InvalidDataException("Initial-default REP length differs from the verified schema.");
        Require(source.Length,p,(int)payloadBits);
        if(sequence==0)throw new InvalidDataException("Sequence zero is a native dry-run marker.");
        if(Read(source,ref p,10)!=0)throw new NotSupportedException("Delta replication requires a separate base-state implementation.");
        for(var i=0;i<expectedOptionalPropertyCount;i++)
            if(Read(source,ref p,1)!=0)throw new NotSupportedException("Reflected property overrides are outside the initial-default subset.");
        var result=new EdServerDefaultObjectState(reference,version,sequence,expectedOptionalPropertyCount);
        Validate(result);bitOffset=p;return result;
    }

    private static void Validate(EdServerDefaultObjectState message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if(message.ObjectReference>0xFFFFFFFFFFFF||message.Sequence is 0 or >1023||message.OptionalPropertyCount is <0 or >MaximumOptionalProperties)
            throw new ArgumentOutOfRangeException(nameof(message));
    }
    private static void Require(int length,int position,int bits)
    { if(position<0||bits<0||(long)position+bits>(long)length*8)throw new InvalidDataException("Initial native replication body is truncated."); }
    private static ulong Read(ReadOnlySpan<byte> data,ref int p,int bits)
    {
        Require(data.Length,p,bits);ulong value=0;
        for(var i=0;i<bits;i++,p++)value|=(ulong)((data[p/8]>>(p%8))&1)<<i;
        return value;
    }
    private static void Write(Span<byte> data,ref int p,ulong value,int bits)
    {
        for(var i=0;i<bits;i++,p++)
        {
            var mask=1<<(p%8);data[p/8]=(byte)((data[p/8]&~mask)|(((value>>i)&1)==0?0:mask));
        }
    }
}
