using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;

namespace XFEEliteDangerous.NativeFormat;

/// <summary>Wire values for the supported encrypted compressed response branch; header names are Nonce and uncompressedSize.</summary>
public sealed record EliteEncryptedResponse(string Nonce, string UncompressedSize, string BodyBase64);

/// <summary>The decoded payload and uninterpreted decrypted bytes 4..7. No JSON or authentication success is inferred.</summary>
public sealed record EliteDecodedResponse(byte[] Payload, byte[] OpaquePrefix);

/// <summary>
/// Discovery codec for the statically traced 2026.09.03.332841 response branch:
/// canonical Base64 -> raw ChaCha20 counter zero -> EDDE + four opaque bytes -> independent LZ4 raw block.
/// Encode uses a valid literal-only LZ4 block. It does not infer the meaning of the opaque bytes.
/// </summary>
/// <remarks>
/// The native reader at RVA 0x9A7800 reads Nonce/uncompressedSize, decrypts at 0x9A7E24, compares
/// little-endian 0x45444445 at 0x9A7E3F, and skips eight bytes before LZ4 at 0x9A7EEA.
/// It does not read the four skipped bytes after EDDE, and does not select this branch using an Encrypted header.
/// This bounded implementation supports only the positive-size EDDE branch. Empty/plain bodies, missing keys,
/// wrong magic, invalid sizes and unknown formats are rejected rather than represented as successful JSON.
/// This stream has no authentication tag. The caller supplies the transport key and a fresh nonce;
/// neither is retained. No HTTP handler, native login or game-ready state is enabled by this class.
/// Static branch reconstruction and independent public fixtures do not establish complete native game compatibility.
/// </remarks>
public static class EliteResponseEnvelopeCodec
{
    public const int DefaultMaximumOutputSize = RawLz4Block.DefaultMaximumOutputSize;
    private const uint EddeMagic = 0x45444445;
    private const int PrefixSize = 8;

    public static EliteEncryptedResponse Encode(ReadOnlySpan<byte> key, string nonce, ReadOnlySpan<byte> payload,
        ReadOnlySpan<byte> opaquePrefix = default, int maximumOutputSize = DefaultMaximumOutputSize)
    {
        ValidateKeyNonce(key, nonce);
        ValidateOutputLimit(maximumOutputSize);
        if (payload.IsEmpty || payload.Length > maximumOutputSize)
            throw new ArgumentOutOfRangeException(nameof(payload), "Encrypted EDDE payload must have a positive bounded size.");
        if (!opaquePrefix.IsEmpty && opaquePrefix.Length != 4)
            throw new ArgumentException("Opaque response prefix must be empty (four zero bytes) or exactly four bytes.", nameof(opaquePrefix));

        var extensionCount = payload.Length < 15 ? 0 : (payload.Length - 15) / 255 + 1;
        var plain = new byte[PrefixSize + 1 + extensionCount + payload.Length];
        var cipher = new byte[plain.Length];
        Span<byte> nonceBytes = stackalloc byte[IetfChaCha20.NonceSize];
        try
        {
            BinaryPrimitives.WriteUInt32LittleEndian(plain, EddeMagic);
            if (!opaquePrefix.IsEmpty) opaquePrefix.CopyTo(plain.AsSpan(4, 4));
            var cursor = PrefixSize;
            plain[cursor++] = (byte)(Math.Min(payload.Length, 15) << 4);
            if (extensionCount > 0)
            {
                var remaining = payload.Length - 15;
                while (remaining >= 255) { plain[cursor++] = 255; remaining -= 255; }
                plain[cursor++] = (byte)remaining; // A zero terminator is required for exact 15+255*n lengths.
            }
            payload.CopyTo(plain.AsSpan(cursor));
            CopyAsciiNonce(nonce, nonceBytes);
            IetfChaCha20.Transform(key, nonceBytes, 0, plain, cipher);
            return new(nonce, payload.Length.ToString(CultureInfo.InvariantCulture), Convert.ToBase64String(cipher));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            CryptographicOperations.ZeroMemory(cipher);
            CryptographicOperations.ZeroMemory(nonceBytes);
        }
    }

    public static EliteDecodedResponse Decode(ReadOnlySpan<byte> key, EliteEncryptedResponse envelope,
        int maximumOutputSize = DefaultMaximumOutputSize)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ValidateKeyNonce(key, envelope.Nonce);
        ValidateOutputLimit(maximumOutputSize);
        if (!int.TryParse(envelope.UncompressedSize, NumberStyles.None, CultureInfo.InvariantCulture, out var expectedSize)
            || expectedSize <= 0 || expectedSize > maximumOutputSize)
            throw new InvalidDataException("EDDE uncompressedSize must be a positive decimal integer within the configured limit.");
        if (envelope.BodyBase64 is null)
            throw new InvalidDataException("Encrypted EDDE response body is missing.");
        // LZ4's literal-only upper bound plus its eight-byte envelope. Check encoded length before Base64 allocation.
        var maximumCipherSize = (long)expectedSize + expectedSize / 255 + 16 + PrefixSize;
        var maximumEncodedLength = 4 * ((maximumCipherSize + 2) / 3);
        if (envelope.BodyBase64.Length == 0 || envelope.BodyBase64.Length > maximumEncodedLength)
            throw new InvalidDataException("Encrypted EDDE body is empty or exceeds the bounded raw-block size.");

        byte[]? cipher = null;
        byte[]? plain = null;
        Span<byte> nonceBytes = stackalloc byte[IetfChaCha20.NonceSize];
        try
        {
            try { cipher = Convert.FromBase64String(envelope.BodyBase64); }
            catch (FormatException error) { throw new InvalidDataException("EDDE response body is not standard Base64.", error); }
            if (cipher.Length < PrefixSize + 1 || Convert.ToBase64String(cipher) != envelope.BodyBase64)
                throw new InvalidDataException("EDDE response body must be canonical padded Base64 with an eight-byte prefix and LZ4 token.");
            plain = new byte[cipher.Length];
            CopyAsciiNonce(envelope.Nonce, nonceBytes);
            IetfChaCha20.Transform(key, nonceBytes, 0, cipher, plain);
            if (BinaryPrimitives.ReadUInt32LittleEndian(plain) != EddeMagic)
                throw new InvalidDataException("Decrypted response does not contain the supported EDDE marker.");
            var payload = RawLz4Block.Decode(plain.AsSpan(PrefixSize), expectedSize, maximumOutputSize);
            return new(payload, plain.AsSpan(4, 4).ToArray());
        }
        finally
        {
            if (cipher is not null) CryptographicOperations.ZeroMemory(cipher);
            if (plain is not null) CryptographicOperations.ZeroMemory(plain);
            CryptographicOperations.ZeroMemory(nonceBytes);
        }
    }

    private static void ValidateKeyNonce(ReadOnlySpan<byte> key, string nonce)
    {
        if (key.Length != IetfChaCha20.KeySize)
            throw new ArgumentException("Response transport key must contain exactly 32 bytes.", nameof(key));
        if (nonce is null || nonce.Length != IetfChaCha20.NonceSize || nonce.Any(character => character < 0x20 || character > 0x7e))
            throw new ArgumentException("Response nonce must contain exactly twelve printable ASCII characters, used as raw bytes.", nameof(nonce));
    }

    private static void ValidateOutputLimit(int maximumOutputSize)
    {
        if (maximumOutputSize < 0 || maximumOutputSize > RawLz4Block.HardMaximumOutputSize)
            throw new ArgumentOutOfRangeException(nameof(maximumOutputSize), "EDDE output limit exceeds the supported bound.");
    }

    private static void CopyAsciiNonce(string nonce, Span<byte> output)
    {
        for (var index = 0; index < output.Length; index++) output[index] = (byte)nonce[index];
    }
}
