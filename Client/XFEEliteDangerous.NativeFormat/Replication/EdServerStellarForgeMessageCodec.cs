using System.Buffers.Binary;
using System.Security.Cryptography;

namespace XFEEliteDangerous.NativeFormat.Replication;

/// <summary>
/// Pinned native RPC66/67: U64 system address; reply U64 and a narrow FString.
/// This codec supports the explicit private policy of no post-generation overrides.
/// 3CCCE80 treats an empty string as a successful no-change operation, leaving
/// the native procedural world intact. It does not create or validate that world.
/// </summary>
public static class EdServerStellarForgeMessageCodec
{
    public const byte RequestMethod = 66, ReplyMethod = 67;

    public static ulong ReadRequest(EdServerCallEnvelope message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.PayloadBits != 72) throw new InvalidDataException("Invalid native post-generation request shape.");
        var data = message.CopyPayload();
        try
        {
            if (data[0] != RequestMethod) throw new InvalidDataException("Invalid native post-generation request method.");
            return BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(1));
        }
        finally { CryptographicOperations.ZeroMemory(data); }
    }

    public static EdServerCallEnvelope CreateNoOverridesReply(ulong reference, ulong systemAddress)
    {
        Span<byte> data = stackalloc byte[13];
        data.Clear(); data[0] = ReplyMethod;
        BinaryPrimitives.WriteUInt64LittleEndian(data[1..], systemAddress);
        //626B40 reads a32-bit narrow byte count; zero means no override text.
        try { return new(reference, false, 104, data); }
        finally { data.Clear(); }
    }

    public static ulong ReadNoOverridesReply(EdServerCallEnvelope message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Flag || message.PayloadBits != 104) throw new InvalidDataException("Invalid native empty post-generation reply shape.");
        var data = message.CopyPayload();
        try
        {
            if (data[0] != ReplyMethod || BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(9)) != 0)
                throw new InvalidDataException("Expected an explicit no-overrides post-generation reply.");
            return BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(1));
        }
        finally { CryptographicOperations.ZeroMemory(data); }
    }
}
