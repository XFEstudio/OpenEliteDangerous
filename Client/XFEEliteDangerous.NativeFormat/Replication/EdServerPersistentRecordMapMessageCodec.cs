using System.Buffers.Binary;
using System.Security.Cryptography;

namespace XFEEliteDangerous.NativeFormat.Replication;

/// <summary>
/// Native78/79: U32 correlation and a compressed byte vector containing a record
/// map.984D60/933CE0 read its envelope;9B6AD0/9B5E20 expand the raw Snappy block;
/// 1D21000/1CA6CA0 fulfill the matching request and accept a zero-entry map.
/// Only the explicitly empty local map is supported here, never nonempty imports.
/// </summary>
public static class EdServerPersistentRecordMapMessageCodec
{
    public static uint ReadRequest(EdServerCallEnvelope message)
    {
        if(message.PayloadBits!=40)throw new InvalidDataException("Invalid native record-map request shape.");
        var bytes=message.CopyPayload();
        try
        {
            if(bytes[0]!=78)throw new InvalidDataException("Invalid native record-map request method.");
            return BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(1));
        }
        finally{CryptographicOperations.ZeroMemory(bytes);}
    }
    public static EdServerCallEnvelope CreateEmptyReply(ulong reference,uint requestId)
    {
        Span<byte> bytes=stackalloc byte[16];bytes.Clear();bytes[0]=79;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[1..],requestId);
        // Compressed vector length10: exponent3 (five bits), residual3
        // (three bits). Its ten bytes are varint raw-size8, Snappy literal
        // token(8-1)<<2 and eight zero bytes: compressed map count0 plus padding.
        bytes[5]=3|(3<<5);bytes[6]=8;bytes[7]=(8-1)<<2;
        try{return new(reference,false,128,bytes);}finally{bytes.Clear();}
    }
    public static uint ReadEmptyReply(EdServerCallEnvelope message)
    {
        if(message.Flag||message.PayloadBits!=128)throw new InvalidDataException("Invalid native empty record-map reply shape.");
        var bytes=message.CopyPayload();
        try
        {
            if(bytes[0]!=79||bytes[5]!=0x63||bytes[6]!=8||bytes[7]!=0x1C||bytes.AsSpan(8).ContainsAnyExcept((byte)0))
                throw new InvalidDataException("Expected the explicit empty native record map.");
            return BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(1));
        }
        finally{CryptographicOperations.ZeroMemory(bytes);}
    }
}
