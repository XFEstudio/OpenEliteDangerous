using System.Security.Cryptography;

namespace XFEEliteDangerous.NativeFormat.Replication;

public enum EdServerReliableOutboxState { Active, Expired, RetryLimitExceeded, ClockRollback, Revoked }

/// <summary>
/// An explicit mode-3 datagram budget. PrefixBytes is supplied by the encoder and
/// includes SEQ and any packet/framing overhead not already in the letter payload.
/// The outbox never guesses negotiated type widths, alignment or fragmentation.
/// </summary>
public readonly record struct EdServerReliableFrameBudget
{
    public int MaximumDatagramBytes { get; }
    public int PrefixBytes { get; }
    public int MaximumLetterPayloadBytes => MaximumDatagramBytes
        - EdServerDatagramCodec.HeaderBytes - EdServerDatagramCodec.ChecksumBytes - PrefixBytes;

    public EdServerReliableFrameBudget(int maximumDatagramBytes, int prefixBytes)
    {
        if (maximumDatagramBytes is < 1 or > EdServerDatagramCodec.MaximumDatagramBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumDatagramBytes));
        if (prefixBytes < 1 || (long)prefixBytes + EdServerDatagramCodec.HeaderBytes
            + EdServerDatagramCodec.ChecksumBytes >= maximumDatagramBytes)
            throw new ArgumentOutOfRangeException(nameof(prefixBytes));
        MaximumDatagramBytes = maximumDatagramBytes;
        PrefixBytes = prefixBytes;
    }
}

/// <summary>
/// An owned immutable transmission snapshot. CopyPayload returns a separate buffer
/// for the encoder; dispose this snapshot and clear that encoding buffer afterwards.
/// Retransmissions retain Sequence; the transport must use a fresh datagram counter.
/// </summary>
public sealed class EdServerReliableTransmission : IDisposable
{
    private readonly byte[] _payload;
    private bool _disposed;
    public ushort Sequence { get; }
    public int Attempt { get; }
    public int PayloadLength => _payload.Length;

    internal EdServerReliableTransmission(ushort sequence, int attempt, ReadOnlySpan<byte> payload)
        => (Sequence, Attempt, _payload) = (sequence, attempt, payload.ToArray());

    public byte[] CopyPayload()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _payload.ToArray();
    }

    public void CopyPayloadTo(Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (destination.Length < _payload.Length) throw new ArgumentException("Destination is too short.", nameof(destination));
        _payload.CopyTo(destination);
    }

    public void Dispose()
    {
        if (_disposed) return;
        CryptographicOperations.ZeroMemory(_payload);
        _disposed = true;
    }
}

/// <summary>
/// A bounded private-server reliable send queue, not a complete native transport.
/// Sequence space is 1..65535. Native ACK cumulative coverage and mask bit i =
/// ack+(i+2) are verified at 0x4C1A6DC/0x4C3E5F2. Capacity and a maximum forward
/// span of Capacity (default 16, hard 32) are conservative local policy, not a claim
/// about native window size. Retry interval, lifetime and attempts are caller
/// policy, not inferred native RTO. It stores plaintext letters, never encrypted
/// datagrams or counters. Revocation, expiry or retry exhaustion clears owned
/// payloads and permanently closes this instance rather than skipping a SEQ gap.
/// </summary>
public sealed class EdServerReliableOutbox : IDisposable
{
    public const int DefaultCapacity = 16;
    public const int HardMaximumCapacity = 32;
    private readonly object _gate = new();
    private readonly Dictionary<ushort, Entry> _pending = [];
    private readonly TimeProvider _clock;
    private readonly TimeSpan _retryInterval, _maximumLifetime;
    private readonly int _maximumAttempts;
    private long _lastTimestamp;
    private ushort _lastAcknowledged, _lastSent;
    private EdServerReliableOutboxState _state;

    public int Capacity { get; }
    public EdServerReliableFrameBudget FrameBudget { get; }
    public EdServerReliableOutboxState State { get { lock (_gate) return _state; } }
    public int PendingCount { get { lock (_gate) return _pending.Count; } }
    public ushort LastSentSequence { get { lock (_gate) return _lastSent; } }
    public ushort LastAcknowledgedSequence { get { lock (_gate) return _lastAcknowledged; } }

    public EdServerReliableOutbox(ushort lastAcknowledgedSequence, EdServerReliableFrameBudget frameBudget,
        TimeSpan retryInterval, TimeSpan maximumLifetime, int maximumAttempts,
        TimeProvider? timeProvider = null, int capacity = DefaultCapacity)
    {
        if (lastAcknowledgedSequence == 0) throw new ArgumentOutOfRangeException(nameof(lastAcknowledgedSequence));
        if (frameBudget.MaximumLetterPayloadBytes <= 0 || frameBudget.PrefixBytes <= 0)
            throw new ArgumentException("An explicit valid mode-3 frame budget is required.", nameof(frameBudget));
        if (capacity is < 1 or > HardMaximumCapacity) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (retryInterval <= TimeSpan.Zero || retryInterval == TimeSpan.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(retryInterval));
        if (maximumLifetime < retryInterval || maximumLifetime == TimeSpan.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(maximumLifetime));
        if (maximumAttempts is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        (_lastAcknowledged, _lastSent, FrameBudget, Capacity) =
            (lastAcknowledgedSequence, lastAcknowledgedSequence, frameBudget, capacity);
        (_retryInterval, _maximumLifetime, _maximumAttempts) = (retryInterval, maximumLifetime, maximumAttempts);
        _clock = timeProvider ?? TimeProvider.System;
        if (_clock.TimestampFrequency <= 0) throw new ArgumentException("A monotonic timestamp provider is required.", nameof(timeProvider));
        _lastTimestamp = _clock.GetTimestamp();
    }

    /// <summary>
    /// Atomically reserves and queues a batch such as MEM followed by JOINED.
    /// Each supplied payload is one already encoded letter/frame without its SEQ
    /// prefix. False means no capacity/sequence span and consumes no sequence.
    /// A bad MTU size rejects the whole batch before copying or committing it.
    /// Returned snapshots represent the initial send (attempt 1).
    /// </summary>
    public bool TryEnqueueBatch(IReadOnlyList<ReadOnlyMemory<byte>> letterPayloads,
        out IReadOnlyList<EdServerReliableTransmission> transmissions)
    {
        ArgumentNullException.ThrowIfNull(letterPayloads);
        lock (_gate)
        {
            EnsureActive();
            transmissions = Array.Empty<EdServerReliableTransmission>();
            if (letterPayloads.Count > Capacity) return false;
            for (var i = 0; i < letterPayloads.Count; i++)
                if (letterPayloads[i].Length is 0 || letterPayloads[i].Length > FrameBudget.MaximumLetterPayloadBytes)
                    throw new InvalidDataException("An encoded reliable letter exceeds the explicit MTU budget or is empty.");
            var now = CheckClockAndExpiry();
            if (_pending.Count + letterPayloads.Count > Capacity
                || Distance(_lastAcknowledged, _lastSent) + letterPayloads.Count > Capacity) return false;
            var candidates = new List<Entry>(letterPayloads.Count);
            var snapshots = new List<EdServerReliableTransmission>(letterPayloads.Count);
            var sequence = _lastSent;
            try
            {
                for (var i = 0; i < letterPayloads.Count; i++)
                {
                    var entry = new Entry(sequence = EdServerHandshakeMachine.Next(sequence), letterPayloads[i].ToArray(), now);
                    candidates.Add(entry);
                    snapshots.Add(new(entry.Sequence, 1, entry.Payload));
                }
                var immutableSnapshots = snapshots.AsReadOnly();
                foreach (var entry in candidates) _pending.Add(entry.Sequence, entry);
                _lastSent = sequence;
                transmissions = immutableSnapshots;
                return true;
            }
            catch
            {
                foreach (var entry in candidates)
                {
                    _pending.Remove(entry.Sequence);
                    CryptographicOperations.ZeroMemory(entry.Payload);
                }
                foreach (var snapshot in snapshots) snapshot.Dispose();
                throw;
            }
        }
    }

    /// <summary>
    /// Rejects a future/backward cumulative ACK or selective bit outside the
    /// actually sent forward span without changing the queue or cursor. An ACK
    /// may repeat the last cumulative sequence to add selective acknowledgements.
    /// Allowance is range checked but flow-control policy is not implemented here.
    /// </summary>
    public bool TryAcknowledge(EdServerAckMessage acknowledgement)
    {
        if (acknowledgement.Sequence == 0 || acknowledgement.SelectiveMask > 1023 || acknowledgement.Allowance > 63)
            throw new ArgumentOutOfRangeException(nameof(acknowledgement));
        lock (_gate)
        {
            EnsureActive();
            var sentSpan = Distance(_lastAcknowledged, _lastSent);
            var advance = Distance(_lastAcknowledged, acknowledgement.Sequence);
            if (advance > sentSpan) return false;
            var selective = new List<ushort>(10);
            for (var bit = 0; bit < 10; bit++)
            {
                if ((acknowledgement.SelectiveMask & (1 << bit)) == 0) continue;
                var sequence = Advance(acknowledgement.Sequence, bit + 2);
                var distance = Distance(_lastAcknowledged, sequence);
                if (distance == 0 || distance > sentSpan) return false;
                selective.Add(sequence);
            }
            CheckClockAndExpiry();
            foreach (var sequence in _pending.Keys.Where(x => Distance(_lastAcknowledged, x) <= advance).Concat(selective).Distinct().ToArray())
                if (_pending.Remove(sequence, out var entry)) CryptographicOperations.ZeroMemory(entry.Payload);
            _lastAcknowledged = acknowledgement.Sequence;
            return true;
        }
    }

    /// <summary>
    /// Claims due retransmissions atomically and increments their attempt count.
    /// A failed lifetime/attempt check closes the entire reliable stream, so an
    /// unacknowledged gap cannot silently disappear. No background timer is used.
    /// </summary>
    public IReadOnlyList<EdServerReliableTransmission> ClaimDueRetransmissions()
    {
        lock (_gate)
        {
            EnsureActive();
            var now = CheckClockAndExpiry();
            var due = _pending.Values.Where(x => _clock.GetElapsedTime(x.LastSentTimestamp, now) >= _retryInterval)
                .OrderBy(x => Distance(_lastAcknowledged, x.Sequence)).ToArray();
            if (due.Any(x => x.Attempts >= _maximumAttempts))
                CloseAndThrow(EdServerReliableOutboxState.RetryLimitExceeded);
            var result = due.Select(x => new EdServerReliableTransmission(x.Sequence, x.Attempts + 1, x.Payload)).ToArray();
            foreach (var entry in due) { entry.Attempts++; entry.LastSentTimestamp = now; }
            return Array.AsReadOnly(result);
        }
    }

    public IReadOnlyList<ushort> PendingSequences()
    {
        lock (_gate) return Array.AsReadOnly(_pending.Keys.OrderBy(x => Distance(_lastAcknowledged, x)).ToArray());
    }

    /// <summary>Checks lifetime/clock closure without claiming a retry or reserving a sequence.</summary>
    public void VerifyActive()
    {
        lock (_gate) { EnsureActive(); CheckClockAndExpiry(); }
    }

    public void Revoke()
    {
        lock (_gate)
        {
            if (_state == EdServerReliableOutboxState.Active) _state = EdServerReliableOutboxState.Revoked;
            ClearPending();
        }
    }

    public void Dispose() => Revoke();

    private long CheckClockAndExpiry()
    {
        var now = _clock.GetTimestamp();
        if (_clock.GetElapsedTime(_lastTimestamp, now) < TimeSpan.Zero)
            CloseAndThrow(EdServerReliableOutboxState.ClockRollback);
        if (_pending.Values.Any(x => _clock.GetElapsedTime(x.FirstSentTimestamp, now) >= _maximumLifetime))
            CloseAndThrow(EdServerReliableOutboxState.Expired);
        _lastTimestamp = now;
        return now;
    }

    private void EnsureActive()
    {
        if (_state != EdServerReliableOutboxState.Active)
            throw new InvalidOperationException($"The reliable outbox is closed ({_state}).");
    }

    private void CloseAndThrow(EdServerReliableOutboxState state)
    {
        _state = state;
        ClearPending();
        throw new InvalidOperationException($"The reliable outbox is closed ({state}).");
    }

    private void ClearPending()
    {
        foreach (var entry in _pending.Values) CryptographicOperations.ZeroMemory(entry.Payload);
        _pending.Clear();
    }

    private static int Distance(ushort from, ushort to) => (to - from + ushort.MaxValue) % ushort.MaxValue;
    private static ushort Advance(ushort sequence, int count) => (ushort)((sequence - 1 + count) % ushort.MaxValue + 1);

    private sealed class Entry(ushort sequence, byte[] payload, long timestamp)
    {
        public ushort Sequence { get; } = sequence;
        public byte[] Payload { get; } = payload;
        public long FirstSentTimestamp { get; } = timestamp;
        public long LastSentTimestamp { get; set; } = timestamp;
        public int Attempts { get; set; } = 1;
    }
}
