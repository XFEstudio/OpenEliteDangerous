using System.Buffers.Binary;
using System.Security.Cryptography;

namespace XFEEliteDangerous.NativeFormat.Replication;

/// <summary>
/// The verified clear eight-byte native UDP header. Bit 48 is preserved as an
/// opaque flag; the native send path sets it when compressing the letters.
/// This codec does not implement that compression format.
/// </summary>
public readonly record struct EdServerDatagramHeader
{
    public const ulong MaximumRunId = 0x0000FFFFFFFFFFFF;
    public const ushort MaximumCounter = 0x7FFF;
    public ulong SenderRunId { get; }
    public bool OpaqueFlag { get; }
    public ushort PacketCounter { get; }

    public EdServerDatagramHeader(ulong senderRunId, bool opaqueFlag, ushort packetCounter)
    {
        if (senderRunId is 0 or > MaximumRunId)
            throw new ArgumentOutOfRangeException(nameof(senderRunId));
        if (packetCounter > MaximumCounter)
            throw new ArgumentOutOfRangeException(nameof(packetCounter));
        (SenderRunId, OpaqueFlag, PacketCounter) = (senderRunId, opaqueFlag, packetCounter);
    }

    public static EdServerDatagramHeader Read(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 8)
            throw new InvalidDataException("The native UDP header requires eight bytes.");
        var bits = BinaryPrimitives.ReadUInt64LittleEndian(packet);
        var runId = bits & MaximumRunId;
        if (runId == 0)
            throw new InvalidDataException("Native UDP packets cannot have a zero sender runId.");
        return new(runId, (bits & (1UL << 48)) != 0, (ushort)(bits >> 49));
    }

    public void Write(Span<byte> packet)
    {
        // Validate default(struct) as well as explicitly constructed headers.
        if (SenderRunId is 0 or > MaximumRunId || PacketCounter > MaximumCounter)
            throw new InvalidDataException("The native UDP header has an invalid runId or counter.");
        if (packet.Length < 8)
            throw new ArgumentException("The header destination requires eight bytes.", nameof(packet));
        BinaryPrimitives.WriteUInt64LittleEndian(packet,
            SenderRunId | (OpaqueFlag ? 1UL << 48 : 0) | ((ulong)PacketCounter << 49));
    }
}

/// <summary>
/// Verified UDP framing and mode-3 encryption only. The returned letter bytes are
/// opaque: checksum acceptance does not establish a valid handshake or gameplay.
/// </summary>
public static class EdServerDatagramCodec
{
    public const int MaximumDatagramBytes = 65507;
    public const int HeaderBytes = 8;
    public const int ChecksumBytes = 2;

    public static byte[] EncodeMode3(EdServerDatagramHeader header, ReadOnlySpan<byte> letterBytes, ReadOnlySpan<byte> key)
    {
        RequireKey(key);
        if (letterBytes.Length > MaximumDatagramBytes - HeaderBytes - ChecksumBytes)
            throw new ArgumentOutOfRangeException(nameof(letterBytes));
        var packet = new byte[HeaderBytes + ChecksumBytes + letterBytes.Length];
        header.Write(packet);
        letterBytes.CopyTo(packet.AsSpan(HeaderBytes + ChecksumBytes));
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(HeaderBytes), ComputeChecksum(packet));
        TransformMode3(packet.AsSpan(HeaderBytes), key, header.PacketCounter);
        return packet;
    }

    public static EdServerDecodedDatagram DecodeMode3(ReadOnlySpan<byte> packet, ReadOnlySpan<byte> key)
    {
        RequireKey(key);
        if (packet.Length is < HeaderBytes + ChecksumBytes or > MaximumDatagramBytes)
            throw new InvalidDataException("Native UDP datagram length is outside the supported bounds.");
        var header = EdServerDatagramHeader.Read(packet);
        var clear = packet.ToArray();
        try
        {
            TransformMode3(clear.AsSpan(HeaderBytes), key, header.PacketCounter);
            var received = BinaryPrimitives.ReadUInt16LittleEndian(clear.AsSpan(HeaderBytes));
            clear.AsSpan(HeaderBytes, ChecksumBytes).Clear();
            if (received != ComputeChecksum(clear))
                throw new InvalidDataException("Native UDP checksum verification failed.");
            return new(header, clear.AsSpan(HeaderBytes + ChecksumBytes).ToArray());
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    /// <summary>
    /// CRC-32 reflected polynomial 0xEDB88320, initial value 0x1337,
    /// no final complement, low UInt16. The checksum bytes must already be zero.
    /// </summary>
    public static ushort ComputeChecksum(ReadOnlySpan<byte> clearPacketWithZeroChecksum)
    {
        uint crc = 0x1337;
        foreach (var value in clearPacketWithZeroChecksum)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320U : 0U);
        }
        return (ushort)crc;
    }

    /// <summary>
    /// Native mode 3 uses AES-256 ECB to generate an OFB-style stream. The seed is
    /// four copies of the zero-extended UInt15 packet counter as UInt32LE; each
    /// encrypted block becomes the next seed. This API changes only its payload.
    /// </summary>
    public static void TransformMode3(Span<byte> payload, ReadOnlySpan<byte> key, ushort packetCounter)
    {
        RequireKey(key);
        if (packetCounter > EdServerDatagramHeader.MaximumCounter)
            throw new ArgumentOutOfRangeException(nameof(packetCounter));
        if (payload.Length > MaximumDatagramBytes - HeaderBytes)
            throw new ArgumentOutOfRangeException(nameof(payload));
        Span<byte> feedback = stackalloc byte[16];
        Span<byte> next = stackalloc byte[16];
        for (var index = 0; index < 16; index += 4)
            BinaryPrimitives.WriteUInt32LittleEndian(feedback[index..], packetCounter);
        using var aes = Aes.Create();
        aes.Key = key.ToArray();
        try
        {
            for (var offset = 0; offset < payload.Length; offset += 16)
            {
                aes.EncryptEcb(feedback, next, PaddingMode.None);
                next.CopyTo(feedback);
                var count = Math.Min(16, payload.Length - offset);
                for (var index = 0; index < count; index++)
                    payload[offset + index] ^= next[index];
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(feedback);
            CryptographicOperations.ZeroMemory(next);
        }
    }

    private static void RequireKey(ReadOnlySpan<byte> key)
    {
        if (key.Length != 32)
            throw new ArgumentException("Native UDP mode 3 requires exactly 32 key bytes.", nameof(key));
    }
}

public sealed class EdServerDecodedDatagram : IDisposable
{
    private byte[]? _letterBytes;
    public EdServerDatagramHeader Header { get; }
    public ReadOnlyMemory<byte> OpaqueLetterBytes => _letterBytes
        ?? throw new ObjectDisposedException(nameof(EdServerDecodedDatagram));
    internal EdServerDecodedDatagram(EdServerDatagramHeader header, byte[] bytes) =>
        (Header, _letterBytes) = (header, bytes);
    public void Dispose()
    {
        var bytes = Interlocked.Exchange(ref _letterBytes, null);
        if (bytes is not null)
            CryptographicOperations.ZeroMemory(bytes);
    }
    public override string ToString() => _letterBytes is { } bytes
        ? $"Native UDP: {bytes.Length} opaque letter bytes; handshake unverified"
        : "Native UDP: disposed";
}
