using System.Globalization;
using System.Text.Json;

namespace XFEEliteDangerous.NativeFormat.Replication;

/// <summary>
/// A bounded reader for the confirmed IP4 form of a native server/list record.
/// No authentication, packet cipher, handshake or gameplay success is implied.
/// </summary>
public sealed class EdServerDescriptor
{
    public const int MaximumEncryptionKeyBytes = 4096;
    public ulong RunId { get; }
    public ulong Id { get; }
    public EdServerIp4Endpoint Endpoint { get; }
    public ReadOnlyMemory<byte> EncryptionKey { get; }

    private EdServerDescriptor(ulong runId, ulong id, EdServerIp4Endpoint endpoint, byte[] encryptionKey) =>
        (RunId, Id, Endpoint, EncryptionKey) = (runId, id, endpoint, encryptionKey);

    public static EdServerDescriptor Parse(JsonElement record)
    {
        if (record.ValueKind != JsonValueKind.Object)
            throw new FormatException("The server descriptor must be an object.");
        var runIdText = RequiredString(record, "runId");
        if (!TryUnsigned(runIdText, out var runId))
            throw new FormatException("The server descriptor runId must be an unsigned decimal string.");
        var endpoint = EdServerIp4Endpoint.Parse(RequiredString(record, "connectionDetails"));
        var key = DecodeEncryptionKey(RequiredString(record, "encryptionKey"));
        ulong id = 0;
        if (record.TryGetProperty("id", out var idElement))
        {
            if (idElement.ValueKind == JsonValueKind.String)
            {
                if (!TryUnsigned(idElement.GetString()!, out id))
                    throw new FormatException("The server descriptor id must be an unsigned integer.");
            }
            else if (idElement.ValueKind != JsonValueKind.Number || !idElement.TryGetUInt64(out id))
                throw new FormatException("The server descriptor id must be an unsigned integer.");
        }
        return new(runId, id, endpoint, key);
    }

    /// <summary>
    /// Native helper RVA 0x545120 decodes two hexadecimal characters per byte.
    /// The cipher's key-size requirement is not yet established. Odd or invalid
    /// text is rejected here rather than imitating the native zero substitution.
    /// </summary>
    public static byte[] DecodeEncryptionKey(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        if ((hex.Length & 1) != 0 || hex.Length > MaximumEncryptionKeyBytes * 2)
            throw new FormatException("The encryption key must be bounded, even-length hexadecimal text.");
        try { return Convert.FromHexString(hex); }
        catch (FormatException) { throw new FormatException("The encryption key contains a non-hexadecimal character."); }
    }

    public override string ToString() => $"EdServerDescriptor {{ Endpoint = {Endpoint}, EncryptionKey = [redacted] }}";

    private static string RequiredString(JsonElement record, string property) =>
        record.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new FormatException($"The server descriptor requires string field '{property}'.");

    private static bool TryUnsigned(string text, out ulong value) =>
        ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
}
