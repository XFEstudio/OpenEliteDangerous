using System.Buffers.Binary;

namespace XFEEliteDangerous.NativeFormat;

/// <summary>
/// Bounded decoder for an independent LZ4 raw block, with the exact decompressed size supplied out of band.
/// This is not an LZ4 frame, streaming/dictionary decoder or a complete Elite Dangerous response codec.
/// </summary>
/// <remarks>
/// Format: https://github.com/lz4/lz4/blob/v1.10.0/doc/lz4_Block_format.md .
/// Input must contain a complete block; no trailing bytes, external dictionary or implicit zero-filled history is accepted.
/// The format's final literal-only sequence, five final literals and last-match distance restrictions are enforced.
/// Public reference-encoder fixtures verify this primitive only, not native game authentication or response compatibility.
/// </remarks>
public static class RawLz4Block
{
    public const int DefaultMaximumOutputSize = 16 * 1024 * 1024;
    public const int HardMaximumOutputSize = 64 * 1024 * 1024;

    /// <summary>
    /// Decodes one complete independent block, requiring exactly <paramref name="expectedSize"/> output bytes.
    /// All size arguments are checked before output allocation. Malformed or inconsistent input throws
    /// <see cref="InvalidDataException"/>; an invalid or excessive requested size throws
    /// <see cref="ArgumentOutOfRangeException"/>. A zero-length output is encoded by a final token (normally 00),
    /// not by an empty input span. No partial output is returned on failure.
    /// </summary>
    public static byte[] Decode(ReadOnlySpan<byte> input, int expectedSize,
        int maximumOutputSize = DefaultMaximumOutputSize)
    {
        if (maximumOutputSize < 0 || maximumOutputSize > HardMaximumOutputSize)
            throw new ArgumentOutOfRangeException(nameof(maximumOutputSize), "LZ4 output limit exceeds the supported bound.");
        if (expectedSize < 0 || expectedSize > maximumOutputSize)
            throw new ArgumentOutOfRangeException(nameof(expectedSize), "Expected LZ4 output exceeds the configured limit.");
        if (input.IsEmpty) throw new InvalidDataException("LZ4 raw block is missing its final sequence token.");

        var output = new byte[expectedSize];
        try
        {
            var read = 0;
            var written = 0;
            var lastMatchStart = -1;
            while (read < input.Length)
            {
                var token = input[read++];
                var literalLength = ReadLength(input, ref read, token >> 4, expectedSize - written);
                if (literalLength > input.Length - read)
                    throw new InvalidDataException("LZ4 literal sequence is truncated.");
                input.Slice(read, literalLength).CopyTo(output.AsSpan(written, literalLength));
                read += literalLength;
                written += literalLength;

                // Only a literal-only final sequence may terminate a raw block. Its low token nibble is unused.
                if (read == input.Length)
                {
                    if (written != expectedSize)
                        throw new InvalidDataException("LZ4 decoded size does not match the expected size.");
                    if (lastMatchStart >= 0 && (literalLength < 5 || expectedSize - lastMatchStart < 12))
                        throw new InvalidDataException("LZ4 final literal or last-match restriction is violated.");
                    return output;
                }

                if (input.Length - read < 2) throw new InvalidDataException("LZ4 match offset is truncated.");
                var offset = BinaryPrimitives.ReadUInt16LittleEndian(input.Slice(read, 2));
                read += 2;
                if (offset == 0 || offset > written)
                    throw new InvalidDataException("LZ4 match offset must refer to previously decoded bytes.");

                var remainingOutput = expectedSize - written;
                if (remainingOutput < 4) throw new InvalidDataException("LZ4 match exceeds the expected output size.");
                var matchLength = ReadLength(input, ref read, token & 0x0f, remainingOutput - 4) + 4;
                lastMatchStart = written;
                // Forward byte copying deliberately reads bytes just produced when matchLength > offset.
                // Span.CopyTo (memmove semantics) cannot implement this expanding-history operation.
                for (var index = 0; index < matchLength; index++)
                    output[written + index] = output[written + index - offset];
                written += matchLength;
            }

            throw new InvalidDataException("LZ4 block ends with a match instead of a final literal sequence.");
        }
        catch
        {
            // Failed responses can contain sensitive application data; do not retain a partially decoded buffer.
            Array.Clear(output);
            throw;
        }
    }

    private static int ReadLength(ReadOnlySpan<byte> input, ref int read, int nibble, int maximum)
    {
        var length = nibble;
        if (length > maximum) throw new InvalidDataException("LZ4 length exceeds the expected output size.");
        if (nibble != 15) return length;
        byte extension;
        do
        {
            if (read == input.Length) throw new InvalidDataException("LZ4 length extension is truncated.");
            extension = input[read++];
            // Subtraction before addition prevents overflow even for arbitrarily long extension chains.
            if (extension > maximum - length)
                throw new InvalidDataException("LZ4 extended length exceeds the expected output size.");
            length += extension;
        } while (extension == 255);
        return length;
    }
}
