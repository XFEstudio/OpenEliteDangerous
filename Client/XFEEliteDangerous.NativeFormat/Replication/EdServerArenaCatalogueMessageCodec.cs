using System.Security.Cryptography;

namespace XFEEliteDangerous.NativeFormat.Replication;

/// <summary>
/// Native70/71 advertises the ProvingGround/Arena mode catalogue.95A220 sets
/// compressed mode before reading its vector count.2731000 explicitly clears
/// the catalogue on zero entries;2741CB0 may request it again after its timer.
/// The minimum Solo service has no Arena modes. This is not world readiness.
/// </summary>
public static class EdServerArenaCatalogueMessageCodec
{
    public static void ReadRequest(EdServerCallEnvelope message)
    {
        ArgumentNullException.ThrowIfNull(message);
        //The Call flag is a native dispatch/forwarding flag, not request
        //authentication or direction.96DB80's remote sender writes it as0.
        //The owning server must separately validate the controller and peer.
        if (message.PayloadBits != 8)
            throw new InvalidDataException("Invalid native Arena catalogue request shape.");
        var data = message.CopyPayload();
        try
        {
            if (data[0] != 70) throw new InvalidDataException("Invalid native Arena catalogue request method.");
        }
        finally { CryptographicOperations.ZeroMemory(data); }
    }

    public static EdServerCallEnvelope CreateEmptyReply(ulong reference)
    {
        //8 method bits +5 zero exponent bits for compressed count0.
        return new(reference, false, 13, [71, 0]);
    }

    public static void ReadEmptyReply(EdServerCallEnvelope message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Flag || message.PayloadBits != 13)
            throw new InvalidDataException("Invalid native empty Arena catalogue shape.");
        var data = message.CopyPayload();
        try
        {
            if (data[0] != 71 || data[1] != 0)
                throw new InvalidDataException("Expected the explicit empty Arena catalogue.");
        }
        finally { CryptographicOperations.ZeroMemory(data); }
    }
}
