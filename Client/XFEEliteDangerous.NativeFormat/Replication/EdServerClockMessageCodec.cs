using System.Buffers.Binary;
using System.Security.Cryptography;

namespace XFEEliteDangerous.NativeFormat.Replication;

/// <summary>Native signed seconds/nanoseconds value (54AAF0/54D580).
/// Clock RPC timestamps use UTC seconds since the Windows FILETIME epoch (1601),
/// while the same value type can also represent signed durations.</summary>
public readonly record struct EdServerNetworkTime
{
    public long Seconds { get; }
    public int Nanoseconds { get; }
    public EdServerNetworkTime(long seconds,int nanoseconds)
    {
        if(nanoseconds is <=-1_000_000_000 or >=1_000_000_000
            ||seconds>0&&nanoseconds<0||seconds<0&&nanoseconds>0)
            throw new ArgumentException("Noncanonical native clock value.");
        (Seconds,Nanoseconds)=(seconds,nanoseconds);
    }
    public static EdServerNetworkTime FromTimestamp(long timestamp,long frequency)
    {
        if(frequency<=0)throw new ArgumentOutOfRangeException(nameof(frequency));
        return new(timestamp/frequency,(int)((Int128)(timestamp%frequency)*1_000_000_000/frequency));
    }
    public static EdServerNetworkTime FromUtc(DateTimeOffset utc)
        =>FromTimestamp(utc.UtcDateTime.ToFileTimeUtc(),TimeSpan.TicksPerSecond);
}

/// <summary>Hash-pinned ServerAdmin RPC20/21: request t0; reply t0, receive t1, send t2.
/// 983350/536000 read nano-I32 then seconds-I64;9695D0/93C130 write three such values.
/// The native controller has155 methods, hence an8-bit index in the observed aligned mode.</summary>
public static class EdServerClockMessageCodec
{
    public const byte RequestMethod=20,ReplyMethod=21;
    public static EdServerNetworkTime ReadRequest(EdServerCallEnvelope message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if(!message.Flag||message.PayloadBits!=104)throw new InvalidDataException("Invalid native clock request shape.");
        var payload=message.CopyPayload();
        try
        {
            if(payload[0]!=RequestMethod)throw new InvalidDataException("Invalid native clock request method.");
            return ReadTime(payload.AsSpan(1));
        }
        finally{CryptographicOperations.ZeroMemory(payload);}
    }
    public static EdServerCallEnvelope CreateReply(ulong reference,EdServerNetworkTime request,EdServerNetworkTime received,EdServerNetworkTime sent)
    {
        Span<byte> payload=stackalloc byte[37];payload[0]=ReplyMethod;
        WriteTime(payload[1..],request);WriteTime(payload[13..],received);WriteTime(payload[25..],sent);
        try{return new(reference,true,296,payload);}finally{payload.Clear();}
    }
    public static (EdServerNetworkTime Request,EdServerNetworkTime Received,EdServerNetworkTime Sent) ReadReply(EdServerCallEnvelope message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if(!message.Flag||message.PayloadBits!=296)throw new InvalidDataException("Invalid native clock reply shape.");
        var payload=message.CopyPayload();
        try
        {
            if(payload[0]!=ReplyMethod)throw new InvalidDataException("Invalid native clock reply method.");
            return(ReadTime(payload.AsSpan(1)),ReadTime(payload.AsSpan(13)),ReadTime(payload.AsSpan(25)));
        }
        finally{CryptographicOperations.ZeroMemory(payload);}
    }
    private static EdServerNetworkTime ReadTime(ReadOnlySpan<byte> data)
    {
        try{return new(BinaryPrimitives.ReadInt64LittleEndian(data[4..]),BinaryPrimitives.ReadInt32LittleEndian(data));}
        catch(ArgumentException error){throw new InvalidDataException("Invalid native clock value.",error);}
    }
    private static void WriteTime(Span<byte> data,EdServerNetworkTime value)
    {BinaryPrimitives.WriteInt32LittleEndian(data,value.Nanoseconds);BinaryPrimitives.WriteInt64LittleEndian(data[4..],value.Seconds);}
}
