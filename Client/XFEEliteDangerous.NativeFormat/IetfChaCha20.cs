using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace XFEEliteDangerous.NativeFormat;

/// <summary>
/// Raw IETF ChaCha20: 256-bit key, 96-bit nonce and 32-bit block counter, per RFC 8439 sections 2.3–2.4.
/// This primitive has no Poly1305 tag, authentication, padding, native packet envelope or automatic HTTP integration.
/// Passing the RFC vectors establishes the primitive only; it does not establish Elite Dangerous protocol compatibility.
/// </summary>
/// <remarks>
/// Specification: https://www.rfc-editor.org/rfc/rfc8439.html#section-2.3 .
/// Calls retain no key or nonce in object/static fields, and temporary state and keystream buffers are cleared in finally blocks.
/// Callers own their key/nonce buffers and must enforce key/nonce uniqueness and the native protocol's verified counter rules.
/// Transform is symmetric: the same operation encrypts or decrypts. Separate calls restart at the given block boundary.
/// </remarks>
public static class IetfChaCha20
{
    public const int KeySize = 32;
    public const int NonceSize = 12;
    public const int BlockSize = 64;

    /// <summary>Writes exactly one 64-byte block. All uint counter values, including uint.MaxValue, are valid.</summary>
    public static void GenerateBlock(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, uint counter, Span<byte> output)
    {
        ValidateKeyNonce(key, nonce);
        if (output.Length < BlockSize) throw new ArgumentException("ChaCha20 block output must hold at least 64 bytes.", nameof(output));
        var written = output[..BlockSize];
        RejectSecretOverlap(key, nonce, written);
        Span<uint> state = stackalloc uint[16];
        try
        {
            Initialize(state, key, nonce, counter);
            WriteBlock(state, written);
        }
        finally { CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(state)); }
    }

    /// <summary>
    /// XORs input with the keystream, writing input.Length bytes. Exact in-place transformation is supported;
    /// partially overlapping input/output and output that overwrites a key/nonce are rejected before writing.
    /// The complete input's counter range is checked up front, so rejected requests cannot partially overwrite output.
    /// </summary>
    public static void Transform(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, uint initialCounter,
        ReadOnlySpan<byte> input, Span<byte> output)
    {
        ValidateKeyNonce(key, nonce);
        ValidateCounterRange(initialCounter, input.Length);
        if (output.Length < input.Length) throw new ArgumentException("Output is smaller than the input.", nameof(output));
        var written = output[..input.Length];
        RejectSecretOverlap(key, nonce, written);
        if (input.Overlaps(written, out var offset) && offset != 0)
            throw new ArgumentException("Input and output must not partially overlap.", nameof(output));
        if (input.IsEmpty) return;

        Span<uint> state = stackalloc uint[16];
        Span<byte> keystream = stackalloc byte[BlockSize];
        try
        {
            Initialize(state, key, nonce, initialCounter);
            var consumed = 0;
            while (consumed < input.Length)
            {
                WriteBlock(state, keystream);
                var count = Math.Min(BlockSize, input.Length - consumed);
                for (var index = 0; index < count; index++)
                    written[consumed + index] = (byte)(input[consumed + index] ^ keystream[index]);
                consumed += count;
                // Do not wrap even after the last block. The preflight check proves every used counter fits.
                if (consumed < input.Length) state[12] = checked(state[12] + 1u);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(state));
            CryptographicOperations.ZeroMemory(keystream);
        }
    }

    /// <summary>Allocating convenience overload; validation occurs before allocating output.</summary>
    public static byte[] Transform(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, uint initialCounter, ReadOnlySpan<byte> input)
    {
        ValidateKeyNonce(key, nonce);
        ValidateCounterRange(initialCounter, input.Length);
        var output = new byte[input.Length];
        Transform(key, nonce, initialCounter, input, output);
        return output;
    }

    private static void ValidateKeyNonce(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce)
    {
        if (key.Length != KeySize) throw new ArgumentException("IETF ChaCha20 requires a 32-byte key.", nameof(key));
        if (nonce.Length != NonceSize) throw new ArgumentException("IETF ChaCha20 requires a 12-byte nonce.", nameof(nonce));
    }

    private static void ValidateCounterRange(uint counter, int length)
    {
        var blocks = ((ulong)length + BlockSize - 1UL) / BlockSize;
        var available = (ulong)uint.MaxValue - counter + 1UL;
        if (blocks > available)
            throw new ArgumentOutOfRangeException(nameof(counter), "Input would exhaust the 32-bit ChaCha20 block counter.");
    }

    private static void RejectSecretOverlap(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, Span<byte> output)
    {
        if (key.Overlaps(output) || nonce.Overlaps(output))
            throw new ArgumentException("Output must not overwrite the caller's key or nonce.", nameof(output));
    }

    private static void Initialize(Span<uint> state, ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, uint counter)
    {
        state[0] = 0x61707865u; state[1] = 0x3320646eu; state[2] = 0x79622d32u; state[3] = 0x6b206574u;
        for (var index = 0; index < 8; index++) state[index + 4] = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(index * 4, 4));
        state[12] = counter;
        for (var index = 0; index < 3; index++) state[index + 13] = BinaryPrimitives.ReadUInt32LittleEndian(nonce.Slice(index * 4, 4));
    }

    private static void WriteBlock(ReadOnlySpan<uint> initial, Span<byte> output)
    {
        Span<uint> work = stackalloc uint[16];
        try
        {
            initial.CopyTo(work);
            for (var iteration = 0; iteration < 10; iteration++)
            {
                QuarterRound(work, 0, 4, 8, 12); QuarterRound(work, 1, 5, 9, 13);
                QuarterRound(work, 2, 6, 10, 14); QuarterRound(work, 3, 7, 11, 15);
                QuarterRound(work, 0, 5, 10, 15); QuarterRound(work, 1, 6, 11, 12);
                QuarterRound(work, 2, 7, 8, 13); QuarterRound(work, 3, 4, 9, 14);
            }
            for (var index = 0; index < 16; index++)
                BinaryPrimitives.WriteUInt32LittleEndian(output.Slice(index * 4, 4), unchecked(work[index] + initial[index]));
        }
        finally { CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(work)); }
    }

    private static void QuarterRound(Span<uint> state, int a, int b, int c, int d)
    {
        state[a] = unchecked(state[a] + state[b]); state[d] = BitOperations.RotateLeft(state[d] ^ state[a], 16);
        state[c] = unchecked(state[c] + state[d]); state[b] = BitOperations.RotateLeft(state[b] ^ state[c], 12);
        state[a] = unchecked(state[a] + state[b]); state[d] = BitOperations.RotateLeft(state[d] ^ state[a], 8);
        state[c] = unchecked(state[c] + state[d]); state[b] = BitOperations.RotateLeft(state[b] ^ state[c], 7);
    }
}
