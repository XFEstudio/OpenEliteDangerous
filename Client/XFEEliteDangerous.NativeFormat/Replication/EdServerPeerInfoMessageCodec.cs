using System.Buffers.Binary;
using System.Security.Cryptography;

namespace XFEEliteDangerous.NativeFormat.Replication;

/// <summary>Native13/14 correlation and the explicit unavailable result.
/// 982FB0 reads U32 and U48;982E60 reads U32,U64,two FStrings,U32,a
/// compressed-count string vector.1D3B540's not-ready path uses the unavailable
/// values below. This negative result never invents a peer endpoint or key.</summary>
public static class EdServerPeerInfoMessageCodec
{
    /// <summary>Pinned982E60/959410 shape. Values are private-session metadata,
    /// never official credentials. Field meanings require native consumer validation.</summary>
    public static EdServerCallEnvelope CreateAvailableReply(ulong reference,uint requestId,ulong identity,string connectionDetails,string encryptionKey,uint nativeUserId)
    {
        if(identity==ulong.MaxValue||nativeUserId==0||!EdServerIp4Endpoint.TryParse(connectionDetails,out var endpoint)||endpoint.Address!=0x7F000001
            ||encryptionKey.Length!=64||!encryptionKey.All(Uri.IsHexDigit))throw new ArgumentException("Invalid private peer metadata.");
        var route=System.Text.Encoding.UTF8.GetBytes(connectionDetails);var key=System.Text.Encoding.ASCII.GetBytes(encryptionKey);
        var data=new byte[26+route.Length+key.Length];var p=0;
        void Write(ulong value,int bits){for(var b=0;b<bits;b++,p++)if(((value>>b)&1)!=0)data[p/8]|=(byte)(1<<(p%8));}
        Write(14,8);Write(requestId,32);Write(identity,64);Write((uint)route.Length,32);foreach(var value in route)Write(value,8);
        Write((uint)key.Length,32);foreach(var value in key)Write(value,8);Write(nativeUserId,32);Write(0,5);
        try{return new(reference,true,p,data);}finally{CryptographicOperations.ZeroMemory(data);CryptographicOperations.ZeroMemory(key);}
    }
    public static (uint RequestId, ulong MachineReference) ReadRequest(EdServerCallEnvelope message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.PayloadBits != 88) throw new InvalidDataException("Invalid native peer-info request shape.");
        var data = message.CopyPayload();
        try
        {
            if (data[0] != 13) throw new InvalidDataException("Invalid native peer-info request method.");
            ulong machine = 0;
            for (var index = 0; index < 6; index++) machine |= (ulong)data[index + 5] << (index * 8);
            return (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(1)), machine);
        }
        finally { CryptographicOperations.ZeroMemory(data); }
    }

    public static EdServerCallEnvelope CreateUnavailableReply(ulong reference, uint requestId)
    {
        //8 method +32 correlation +64 unavailable +2*32 empty strings
        // +32 zero scalar +5 compressed zero-count bits =205 bits.
        Span<byte> data = stackalloc byte[26]; data.Clear(); data[0] = 14;
        BinaryPrimitives.WriteUInt32LittleEndian(data[1..], requestId);
        BinaryPrimitives.WriteUInt64LittleEndian(data[5..], ulong.MaxValue);
        try { return new(reference, true, 205, data); }
        finally { data.Clear(); }
    }

    public static uint ReadUnavailableReply(EdServerCallEnvelope message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!message.Flag || message.PayloadBits != 205) throw new InvalidDataException("Invalid native unavailable-peer reply shape.");
        var data = message.CopyPayload();
        try
        {
            if (data[0] != 14 || BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(5)) != ulong.MaxValue
                || data.AsSpan(13).ContainsAnyExcept((byte)0))
                throw new InvalidDataException("Expected an explicit native unavailable-peer result.");
            return BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(1));
        }
        finally { CryptographicOperations.ZeroMemory(data); }
    }
}
