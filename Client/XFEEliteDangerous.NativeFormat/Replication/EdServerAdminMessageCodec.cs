using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XFEEliteDangerous.NativeFormat.Replication;

public enum EdServerAdminLetter : byte { StartServerAdmin = 26, ServerAdminReady = 27 }

/// <summary>
/// Owned opaque narrow-byte credentials, not decoded text. Explicit copy methods
/// return caller-owned buffers which the caller should clear. Normal logging and
/// serialization expose lengths only. Dispose clears this instance's copies.
/// </summary>
[JsonConverter(typeof(EdServerStartServerAdminJsonConverter))]
public sealed class EdServerStartServerAdmin : IDisposable
{
    private readonly object gate = new();
    private readonly byte[][] credentials;
    private bool disposed;
    public bool Missions { get; }
    public int MachineIdLength => Length(0);
    public int MachineTokenLength => Length(1);
    public int AuthTokenLength => Length(2);
    public int NeutralAlternateAuthenticationBytesLength => Length(3);

    public EdServerStartServerAdmin(ReadOnlySpan<byte> machineId, ReadOnlySpan<byte> machineToken,
        ReadOnlySpan<byte> authToken, ReadOnlySpan<byte> neutralAlternateAuthenticationBytes, bool missions)
    {
        // A local allocation ceiling, not an inferred native credential limit.
        foreach (var length in new[] { machineId.Length, machineToken.Length, authToken.Length, neutralAlternateAuthenticationBytes.Length })
            if (length > EdServerAdminMessageCodec.HardMaximumCredentialBytes)
                throw new ArgumentOutOfRangeException(nameof(machineId), "A credential exceeds the local allocation ceiling.");
        credentials = [machineId.ToArray(), machineToken.ToArray(), authToken.ToArray(), neutralAlternateAuthenticationBytes.ToArray()];
        Missions = missions;
    }

    private EdServerStartServerAdmin(byte[][] ownedCredentials, bool missions)
        => (credentials, Missions) = (ownedCredentials, missions);

    internal static EdServerStartServerAdmin TakeDecodedOwnership(byte[][] ownedCredentials, bool missions)
        => new(ownedCredentials, missions);

    public byte[] CopyMachineIdBytes() => Copy(0);
    public byte[] CopyMachineTokenBytes() => Copy(1);
    public byte[] CopyAuthTokenBytes() => Copy(2);
    public byte[] CopyNeutralAlternateAuthenticationBytes() => Copy(3);

    private int Length(int index)
    {
        lock (gate) { ObjectDisposedException.ThrowIf(disposed, this); return credentials[index].Length; }
    }

    private byte[] Copy(int index)
    {
        lock (gate) { ObjectDisposedException.ThrowIf(disposed, this); return credentials[index].ToArray(); }
    }

    internal EncodingSnapshot Capture()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return new(credentials.Select(value => value.ToArray()).ToArray(), Missions);
        }
    }

    public override string ToString()
        => $"EdServerStartServerAdmin {{ Credentials = [redacted], Missions = {Missions} }}";

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            foreach (var value in credentials) CryptographicOperations.ZeroMemory(value);
            disposed = true;
        }
    }

    internal sealed class EncodingSnapshot(byte[][] values, bool missions) : IDisposable
    {
        internal ReadOnlySpan<byte> this[int index] => values[index];
        internal bool Missions { get; } = missions;
        public void Dispose() { foreach (var value in values) CryptographicOperations.ZeroMemory(value); }
        public override string ToString() => "EdServerAdminEncodingSnapshot { Credentials = [redacted] }";
    }
}

/// <summary>Diagnostic JSON only; credentials can only be decoded by the bounded binary codec.</summary>
public sealed class EdServerStartServerAdminJsonConverter : JsonConverter<EdServerStartServerAdmin>
{
    public override EdServerStartServerAdmin Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => throw new NotSupportedException("Server-admin credentials require the bounded native binary codec.");

    public override void Write(Utf8JsonWriter writer, EdServerStartServerAdmin value, JsonSerializerOptions options)
    {
        // Explicit output also avoids reflecting over the owner's span-based constructor.
        // No copy method or credential byte is passed to the JSON serializer.
        writer.WriteStartObject();
        writer.WriteNumber("machineIdLength", value.MachineIdLength);
        writer.WriteNumber("machineTokenLength", value.MachineTokenLength);
        writer.WriteNumber("authTokenLength", value.AuthTokenLength);
        writer.WriteNumber("neutralAlternateAuthenticationBytesLength", value.NeutralAlternateAuthenticationBytesLength);
        writer.WriteBoolean("missions", value.Missions);
        writer.WriteEndObject();
    }
}

/// <summary>
/// Raw ready-letter data only. SessionId is genuinely U64 in this message; zero,
/// reserved values and values above U48 are preserved for the authorized caller
/// to interpret. Decoding it never establishes a session or world.
/// </summary>
public readonly record struct EdServerServerAdminReady(ulong SessionId, bool Missions)
{
    public override string ToString() => $"EdServerServerAdminReady {{ SessionId = [redacted], Missions = {Missions} }}";
}

/// <summary>
/// Verified type26/27 bodies only. Caller owns letter type, negotiated framing,
/// SEQ/reliability, authentication, existing session definitions and failure
/// policy. Limits below are explicit local memory policy, not native maxima;
/// the native writer's0x2EE0 allocation capacity is not a protocol limit.
/// See docs/protocol-evidence/native-server-list-connect-contract.json.
/// </summary>
public static class EdServerAdminMessageCodec
{
    public const int DefaultMaximumCredentialBytes = 4096;
    public const int HardMaximumCredentialBytes = 65536;
    public const int DefaultMaximumBodyBytes = 4 * DefaultMaximumCredentialBytes + 17;
    public const int HardMaximumBodyBytes = 4 * HardMaximumCredentialBytes + 17;
    public const int MinimumStartBodyBits = 129;
    public const int ReadyBodyBits = 65;

    // Native0x63BD20 / reader0x653700: four ordinary U32 lengths and their raw
    // eight-bit bytes, then one Boolean. No NUL, UTF8 assumption or compression.
    public static EdServerStartServerAdmin ReadStartBody(ReadOnlySpan<byte> bytes, ref int bitOffset,
        int maximumCredentialBytes = DefaultMaximumCredentialBytes, int maximumBodyBytes = DefaultMaximumBodyBytes)
    {
        ValidateLimits(maximumCredentialBytes, maximumBodyBytes);
        var start = bitOffset; var p = start;
        Span<int> starts = stackalloc int[4]; Span<int> lengths = stackalloc int[4];
        long totalCredentialBytes = 0;
        for (var index = 0; index < 4; index++)
        {
            var count = Read(bytes, ref p, 32);
            if (count > (ulong)maximumCredentialBytes)
                throw new InvalidDataException("A native credential exceeds the configured local bound.");
            totalCredentialBytes += (long)count;
            RequireBodyLimit(totalCredentialBytes, maximumBodyBytes, reading: true);
            starts[index] = p; lengths[index] = (int)count;
            Require(bytes.Length, p, (long)count * 8);
            p = checked(p + (int)count * 8);
        }
        var missions = Read(bytes, ref p, 1) != 0;
        // Validate the whole body before allocating or copying any credential.
        RequireBodyLimit(totalCredentialBytes, maximumBodyBytes, reading: true);
        byte[][] values = new byte[4][];
        var transferred = false;
        try
        {
            for (var index = 0; index < 4; index++)
            {
                values[index] = new byte[lengths[index]];
                var valuePosition = starts[index];
                for (var offset = 0; offset < values[index].Length; offset++)
                    values[index][offset] = (byte)Read(bytes, ref valuePosition, 8);
            }
            var result = EdServerStartServerAdmin.TakeDecodedOwnership(values, missions);
            transferred = true;
            bitOffset = p;
            return result;
        }
        finally
        {
            if (!transferred) foreach (var value in values) if (value is not null) CryptographicOperations.ZeroMemory(value);
        }
    }

    public static int GetStartBodyBitLength(EdServerStartServerAdmin message,
        int maximumCredentialBytes = DefaultMaximumCredentialBytes, int maximumBodyBytes = DefaultMaximumBodyBytes)
    {
        ArgumentNullException.ThrowIfNull(message);
        ValidateLimits(maximumCredentialBytes, maximumBodyBytes);
        using var snapshot = message.Capture();
        return ValidateSnapshot(snapshot, maximumCredentialBytes, maximumBodyBytes);
    }

    public static void WriteStartBody(Span<byte> bytes, ref int bitOffset, EdServerStartServerAdmin message,
        int maximumCredentialBytes = DefaultMaximumCredentialBytes, int maximumBodyBytes = DefaultMaximumBodyBytes)
    {
        ArgumentNullException.ThrowIfNull(message);
        ValidateLimits(maximumCredentialBytes, maximumBodyBytes);
        // Snapshot under the owner's lock. Concurrent disposal cannot clear a
        // credential between validation and encoding; this copy is always wiped.
        using var snapshot = message.Capture();
        var bits = ValidateSnapshot(snapshot, maximumCredentialBytes, maximumBodyBytes);
        Require(bytes.Length, bitOffset, bits); // All failures precede output mutation.
        var p = bitOffset;
        for (var index = 0; index < 4; index++)
        {
            var value = snapshot[index];
            Write(bytes, ref p, (uint)value.Length, 32);
            foreach (var item in value) Write(bytes, ref p, item, 8);
        }
        Write(bytes, ref p, snapshot.Missions ? 1UL : 0UL, 1);
        bitOffset = p;
    }

    // Native reader0x652250: ordinary U64 (not U48), then one Boolean.
    public static EdServerServerAdminReady ReadReadyBody(ReadOnlySpan<byte> bytes, ref int bitOffset)
    {
        var p = bitOffset;
        Require(bytes.Length, p, ReadyBodyBits);
        var result = new EdServerServerAdminReady(Read(bytes, ref p, 64), Read(bytes, ref p, 1) != 0);
        bitOffset = p;
        return result;
    }

    public static void WriteReadyBody(Span<byte> bytes, ref int bitOffset, EdServerServerAdminReady message)
    {
        Require(bytes.Length, bitOffset, ReadyBodyBits);
        var p = bitOffset;
        Write(bytes, ref p, message.SessionId, 64);
        Write(bytes, ref p, message.Missions ? 1UL : 0UL, 1);
        bitOffset = p;
    }

    private static int ValidateSnapshot(EdServerStartServerAdmin.EncodingSnapshot snapshot, int maximumCredentialBytes, int maximumBodyBytes)
    {
        long total = 0;
        for (var index = 0; index < 4; index++)
        {
            if (snapshot[index].Length > maximumCredentialBytes)
                throw new ArgumentOutOfRangeException(nameof(snapshot), "A credential exceeds the configured local bound.");
            total += snapshot[index].Length;
        }
        RequireBodyLimit(total, maximumBodyBytes, reading: false);
        return checked(MinimumStartBodyBits + (int)total * 8);
    }

    private static void ValidateLimits(int maximumCredentialBytes, int maximumBodyBytes)
    {
        if (maximumCredentialBytes is < 0 or > HardMaximumCredentialBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumCredentialBytes));
        if (maximumBodyBytes is < 17 or > HardMaximumBodyBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumBodyBytes));
    }

    private static void RequireBodyLimit(long totalCredentialBytes, int maximumBodyBytes, bool reading)
    {
        if ((MinimumStartBodyBits + totalCredentialBytes * 8 + 7) / 8 <= maximumBodyBytes) return;
        if (reading) throw new InvalidDataException("The native body exceeds the configured local byte bound.");
        throw new ArgumentOutOfRangeException(nameof(maximumBodyBytes), "The body exceeds the configured local byte bound.");
    }

    private static ulong Read(ReadOnlySpan<byte> bytes, ref int p, int count)
    {
        Require(bytes.Length, p, count);
        ulong value = 0;
        for (var index = 0; index < count; index++, p++)
            value |= (ulong)((bytes[p / 8] >> (p % 8)) & 1) << index;
        return value;
    }

    private static void Write(Span<byte> bytes, ref int p, ulong value, int count)
    {
        // Only called after complete validation by the public writer.
        for (var index = 0; index < count; index++, p++)
        {
            var mask = 1 << (p % 8);
            bytes[p / 8] = (byte)((bytes[p / 8] & ~mask) | (((value >> index) & 1) == 0 ? 0 : mask));
        }
    }

    private static void Require(int byteCount, int bitOffset, long bitCount)
    {
        if (bitOffset < 0 || bitCount < 0 || (long)bitOffset + bitCount > int.MaxValue
            || (long)bitOffset + bitCount > (long)byteCount * 8)
            throw new InvalidDataException("Native server-admin body is truncated or has an invalid bit offset.");
    }
}
