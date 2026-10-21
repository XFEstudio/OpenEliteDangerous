using System.Security.Cryptography;
using System.Text;

namespace XFEEliteDangerous.NativeFormat;

public sealed record EliteEncryptedRequest(string Nonce, string QueryBase64, string BodyBase64);
public sealed record EliteDecryptedRequest(byte[] Query, byte[] Body);

/// <summary>
/// Request-only discovery codec for the verified 2026.09.03.332841 client's encrypted wrapper.
/// Query and body are one ChaCha20 stream (counter zero), then split and Base64 encoded separately.
/// The twelve hexadecimal nonce characters are ASCII bytes, not hex-decoded bytes.
/// This class does not construct URLs, infer content formats, authenticate users, decode responses,
/// retain the caller's key, or automatically enable native game endpoints.
/// </summary>
public static class EliteRequestEnvelopeCodec
{
    public const int MaximumPlaintextBytes = 8 * 1024 * 1024;

    public static EliteEncryptedRequest Encode(ReadOnlySpan<byte> key, string nonce,
        ReadOnlySpan<byte> query, ReadOnlySpan<byte> body)
    {
        Validate(key, nonce);
        var count = CheckedLength(query.Length, body.Length);
        var plain = new byte[count];
        var cipher = new byte[count];
        try
        {
            query.CopyTo(plain);
            body.CopyTo(plain.AsSpan(query.Length));
            IetfChaCha20.Transform(key, Encoding.ASCII.GetBytes(nonce), 0, plain, cipher);
            return new(nonce, Convert.ToBase64String(cipher.AsSpan(0, query.Length)),
                Convert.ToBase64String(cipher.AsSpan(query.Length)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            CryptographicOperations.ZeroMemory(cipher);
        }
    }

    public static EliteDecryptedRequest Decode(ReadOnlySpan<byte> key, EliteEncryptedRequest envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        Validate(key, envelope.Nonce);
        ArgumentNullException.ThrowIfNull(envelope.QueryBase64);
        ArgumentNullException.ThrowIfNull(envelope.BodyBase64);
        // Two separately padded Base64 segments can consume up to four extra characters.
        if ((long)envelope.QueryBase64.Length + envelope.BodyBase64.Length > 4L * ((MaximumPlaintextBytes + 2L) / 3) + 4)
            throw new ArgumentOutOfRangeException(nameof(envelope), "Request envelope exceeds the discovery limit.");
        byte[]? query = null, body = null, cipher = null, plain = null;
        try
        {
            query = DecodeCanonicalBase64(envelope.QueryBase64);
            body = DecodeCanonicalBase64(envelope.BodyBase64);
            var count = CheckedLength(query.Length, body.Length);
            cipher = new byte[count]; plain = new byte[count];
            query.CopyTo(cipher, 0); body.CopyTo(cipher, query.Length);
            IetfChaCha20.Transform(key, Encoding.ASCII.GetBytes(envelope.Nonce), 0, cipher, plain);
            return new(plain.AsSpan(0, query.Length).ToArray(), plain.AsSpan(query.Length).ToArray());
        }
        finally
        {
            if (query is not null) CryptographicOperations.ZeroMemory(query);
            if (body is not null) CryptographicOperations.ZeroMemory(body);
            if (cipher is not null) CryptographicOperations.ZeroMemory(cipher);
            if (plain is not null) CryptographicOperations.ZeroMemory(plain);
        }
    }

    private static void Validate(ReadOnlySpan<byte> key, string nonce)
    {
        if (key.Length != IetfChaCha20.KeySize) throw new ArgumentException("Request key must contain exactly 32 bytes.", nameof(key));
        if (nonce is null || nonce.Length != IetfChaCha20.NonceSize || nonce.Any(x => !Uri.IsHexDigit(x)))
            throw new ArgumentException("Request nonce must contain exactly twelve hexadecimal ASCII characters.", nameof(nonce));
    }

    private static int CheckedLength(int queryLength, int bodyLength)
    {
        var length = (long)queryLength + bodyLength;
        if (length > MaximumPlaintextBytes) throw new ArgumentOutOfRangeException(nameof(bodyLength), "Request plaintext exceeds the discovery limit.");
        return (int)length;
    }

    private static byte[] DecodeCanonicalBase64(string text)
    {
        var bytes = Convert.FromBase64String(text);
        if (Convert.ToBase64String(bytes) != text)
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw new FormatException("Request segments must use canonical standard Base64.");
        }
        return bytes;
    }
}
