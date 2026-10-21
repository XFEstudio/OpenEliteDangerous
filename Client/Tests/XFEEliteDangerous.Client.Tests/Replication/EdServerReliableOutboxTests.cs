using System.Collections;
using System.Reflection;
using XFEEliteDangerous.NativeFormat.Replication;
using Xunit;

namespace XFEEliteDangerous.Client.Tests;

public sealed class EdServerReliableOutboxTests
{
    private static EdServerReliableOutbox Create(ManualClock clock, ushort seed = 100, int capacity = 16,
        int maximumAttempts = 3, TimeSpan? lifetime = null, int payloadBytes = 100) => new(seed,
            new(payloadBytes + 14, 4), TimeSpan.FromSeconds(1), lifetime ?? TimeSpan.FromSeconds(10),
            maximumAttempts, clock, capacity);

    private static IReadOnlyList<EdServerReliableTransmission> Queue(EdServerReliableOutbox box, params byte[][] payloads)
    {
        Assert.True(box.TryEnqueueBatch(payloads.Select(x => (ReadOnlyMemory<byte>)x).ToArray(), out var messages));
        return messages;
    }

    private static void DisposeAll(IEnumerable<EdServerReliableTransmission> messages)
    {
        foreach (var message in messages) message.Dispose();
    }

    [Fact]
    public void MemAndJoinedBatchKeepsOrderAndClaimsNoExtraSequence()
    {
        var clock = new ManualClock();
        using var box = Create(clock);
        var messages = Queue(box, [14, 1], [15, 2]);
        Assert.Equal(new ushort[] { 101, 102 }, messages.Select(x => x.Sequence));
        Assert.Equal(new byte[] { 14, 1 }, messages[0].CopyPayload());
        Assert.Equal(new byte[] { 15, 2 }, messages[1].CopyPayload());
        Assert.All(messages, x => Assert.Equal(1, x.Attempt));
        Assert.Equal(102, box.LastSentSequence);
        Assert.Equal(100, box.LastAcknowledgedSequence);
        Assert.Equal(2, box.PendingCount);
        Assert.Throws<NotSupportedException>(() => ((IList<EdServerReliableTransmission>)messages).Clear());
        DisposeAll(messages);
    }

    [Fact]
    public void AtomicCapacityFailureDoesNotPartiallyQueueOrConsumeSequence()
    {
        using var box = Create(new(), capacity: 2);
        DisposeAll(Queue(box, [13]));
        Assert.False(box.TryEnqueueBatch(new ReadOnlyMemory<byte>[] { new byte[] { 14 }, new byte[] { 15 } }, out var failed));
        Assert.Empty(failed);
        Assert.Equal(new ushort[] { 101 }, box.PendingSequences());
        Assert.Equal(101, box.LastSentSequence);
        Assert.True(box.TryAcknowledge(new(101, 0, 16)));
        var pair = Queue(box, [14], [15]);
        Assert.Equal(new ushort[] { 102, 103 }, pair.Select(x => x.Sequence));
        DisposeAll(pair);
    }

    [Fact]
    public void InvalidSecondPayloadRollsBackWholeBatchBeforeReservation()
    {
        using var box = Create(new(), payloadBytes: 4);
        Assert.Throws<InvalidDataException>(() => box.TryEnqueueBatch(
            new ReadOnlyMemory<byte>[] { new byte[] { 14 }, new byte[5] }, out _));
        Assert.Equal(0, box.PendingCount);
        Assert.Equal(100, box.LastSentSequence);
        Assert.Throws<InvalidDataException>(() => box.TryEnqueueBatch(
            new ReadOnlyMemory<byte>[] { new byte[] { 14 }, ReadOnlyMemory<byte>.Empty }, out _));
        Assert.Equal(0, box.PendingCount);
    }

    [Fact]
    public void ExplicitMtuBudgetIncludesMode3HeaderChecksumAndCallerPrefix()
    {
        using var box = new EdServerReliableOutbox(100, new(116, 4), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), 3);
        var messages = Queue(box, Enumerable.Repeat((byte)14, 102).ToArray());
        var transmission = messages[0];
        var letters = new byte[4 + transmission.PayloadLength];
        var p = 0;
        EdServerControlMessageCodec.WriteSequenceLetter(letters, ref p, transmission.Sequence, 16);
        Assert.Equal(32, p);
        transmission.CopyPayloadTo(letters.AsSpan(4));
        var packet = EdServerDatagramCodec.EncodeMode3(new(1, false, 1), letters, new byte[32]);
        Assert.Equal(116, packet.Length);
        Assert.Throws<InvalidDataException>(() => box.TryEnqueueBatch(new ReadOnlyMemory<byte>[] { new byte[103] }, out _));
        Assert.Equal(101, box.LastSentSequence);
        DisposeAll(messages);
    }

    [Fact]
    public void AckCumulativeAndAllSelectiveBitsUseAckPlusTwoThroughPlusEleven()
    {
        using var box = Create(new());
        DisposeAll(Queue(box, Enumerable.Range(0, 13).Select(i => new byte[] { (byte)i }).ToArray()));
        Assert.True(box.TryAcknowledge(new(102, 1 | (1 << 9), 63)));
        Assert.Equal(new ushort[] { 103, 105, 106, 107, 108, 109, 110, 111, 112 }, box.PendingSequences());
        Assert.Equal(102, box.LastAcknowledgedSequence);
        Assert.True(box.TryAcknowledge(new(113, 0, 16)));
        Assert.Empty(box.PendingSequences());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public void IndividualMaskBitAcknowledgesExactlyTheVerifiedOffset(int bit)
    {
        using var box = Create(new());
        DisposeAll(Queue(box, Enumerable.Range(0, 11).Select(_ => new byte[] { 14 }).ToArray()));
        Assert.True(box.TryAcknowledge(new(100, (ushort)(1 << bit), 16)));
        Assert.DoesNotContain((ushort)(102 + bit), box.PendingSequences());
        Assert.Contains((ushort)101, box.PendingSequences());
        Assert.Equal(10, box.PendingCount);
    }

    [Fact]
    public void WraparoundSkipsZeroForQueueCumulativeAckAndSelectiveAck()
    {
        using var box = Create(new(), seed: 65533);
        var messages = Queue(box, [1], [2], [3], [4], [5], [6]);
        Assert.Equal(new ushort[] { 65534, 65535, 1, 2, 3, 4 }, messages.Select(x => x.Sequence));
        Assert.True(box.TryAcknowledge(new(65534, 1, 16))); // +2 is 1, not 0 or 65535.
        Assert.Equal(new ushort[] { 65535, 2, 3, 4 }, box.PendingSequences());
        Assert.True(box.TryAcknowledge(new(1, 0, 16)));
        Assert.Equal(new ushort[] { 2, 3, 4 }, box.PendingSequences());
        Assert.False(box.TryAcknowledge(new(65535, 0, 16))); // stale across wrap.
        DisposeAll(messages);
    }

    [Fact]
    public void FirstSequenceAfterMaximumSeedIsOne()
    {
        using var box = Create(new(), seed: ushort.MaxValue);
        var send = Queue(box, [14]);
        Assert.Equal(1, Assert.Single(send).Sequence);
        DisposeAll(send);
    }

    [Fact]
    public void FutureBackwardAndUnsentSelectiveAckDoNotMutateQueueOrCursor()
    {
        using var box = Create(new());
        DisposeAll(Queue(box, [14], [15]));
        foreach (var ack in new EdServerAckMessage[] { new(103, 0, 16), new(99, 0, 16), new(101, 1, 16), new(102, 1023, 16) })
        {
            Assert.False(box.TryAcknowledge(ack));
            Assert.Equal(new ushort[] { 101, 102 }, box.PendingSequences());
            Assert.Equal(100, box.LastAcknowledgedSequence);
            Assert.Equal(102, box.LastSentSequence);
        }
        Assert.True(box.TryAcknowledge(new(101, 0, 16)));
        Assert.False(box.TryAcknowledge(new(100, 0, 16)));
        Assert.Equal(101, box.LastAcknowledgedSequence);
    }

    [Theory]
    [InlineData(0, 0, 16)]
    [InlineData(101, 1024, 16)]
    [InlineData(101, 0, 64)]
    public void InvalidWireAckRangesAreRejectedBeforeAnyMutation(ushort sequence, ushort mask, byte allowance)
    {
        using var box = Create(new());
        DisposeAll(Queue(box, [14]));
        Assert.Throws<ArgumentOutOfRangeException>(() => box.TryAcknowledge(new(sequence, mask, allowance)));
        Assert.Equal(new ushort[] { 101 }, box.PendingSequences());
        Assert.Equal(100, box.LastAcknowledgedSequence);
    }

    [Fact]
    public void SelectiveFreeSlotsCannotPermitUnboundedForwardSequenceGap()
    {
        using var box = Create(new(), capacity: 4);
        DisposeAll(Queue(box, [1], [2], [3], [4]));
        Assert.True(box.TryAcknowledge(new(100, 7, 16))); // clears 102,103,104; 101 is still missing.
        Assert.Equal(1, box.PendingCount);
        Assert.False(box.TryEnqueueBatch(new ReadOnlyMemory<byte>[] { new byte[] { 5 } }, out _));
        Assert.Equal(104, box.LastSentSequence);
        Assert.True(box.TryAcknowledge(new(101, 0, 16)));
        DisposeAll(Queue(box, [5]));
        Assert.Equal(105, box.LastSentSequence);
    }

    [Fact]
    public void DueRetransmissionKeepsSequenceAndBytesButDoesNotRepeatBeforeTimer()
    {
        var clock = new ManualClock();
        using var box = Create(clock);
        var initial = Queue(box, [14, 8], [15, 9]);
        clock.Advance(TimeSpan.FromMilliseconds(999));
        Assert.Empty(box.ClaimDueRetransmissions());
        clock.Advance(TimeSpan.FromMilliseconds(1));
        var retry = box.ClaimDueRetransmissions();
        Assert.Equal(new ushort[] { 101, 102 }, retry.Select(x => x.Sequence));
        Assert.All(retry, x => Assert.Equal(2, x.Attempt));
        Assert.Equal(initial[0].CopyPayload(), retry[0].CopyPayload());
        Assert.Equal(initial[1].CopyPayload(), retry[1].CopyPayload());
        Assert.Empty(box.ClaimDueRetransmissions());
        Assert.Equal(102, box.LastSentSequence);
        DisposeAll(initial);
        DisposeAll(retry);
    }

    [Fact]
    public void StaggeredFramesBecomeDueIndependentlyAndAckedFramesNeverReappear()
    {
        var clock = new ManualClock();
        using var box = Create(clock);
        DisposeAll(Queue(box, [14]));
        clock.Advance(TimeSpan.FromMilliseconds(500));
        DisposeAll(Queue(box, [15]));
        clock.Advance(TimeSpan.FromMilliseconds(500));
        var firstRetry = box.ClaimDueRetransmissions();
        Assert.Equal(101, Assert.Single(firstRetry).Sequence);
        Assert.True(box.TryAcknowledge(new(101, 0, 16)));
        clock.Advance(TimeSpan.FromMilliseconds(500));
        var secondRetry = box.ClaimDueRetransmissions();
        Assert.Equal(102, Assert.Single(secondRetry).Sequence);
        DisposeAll(firstRetry);
        DisposeAll(secondRetry);
    }

    [Fact]
    public void CopiesSnapshotsAndDisposalCannotAlterQueuedPayloads()
    {
        var clock = new ManualClock();
        using var box = Create(clock);
        byte[] source = [14, 1, 2];
        var initial = Queue(box, source);
        source[0] = 99;
        var copy = initial[0].CopyPayload();
        copy[1] = 99;
        initial[0].Dispose();
        Assert.Throws<ObjectDisposedException>(() => initial[0].CopyPayload());
        clock.Advance(TimeSpan.FromSeconds(1));
        var retry = box.ClaimDueRetransmissions();
        Assert.Equal(new byte[] { 14, 1, 2 }, retry[0].CopyPayload());
        DisposeAll(retry);
    }

    [Fact]
    public void WallClockRollbackDoesNotDelayMonotonicRetry()
    {
        var clock = new ManualClock();
        using var box = Create(clock);
        DisposeAll(Queue(box, [14]));
        clock.ShiftUtc(TimeSpan.FromDays(-3));
        clock.Advance(TimeSpan.FromSeconds(1));
        var retry = box.ClaimDueRetransmissions();
        Assert.Equal(101, Assert.Single(retry).Sequence);
        Assert.Equal(EdServerReliableOutboxState.Active, box.State);
        DisposeAll(retry);
    }

    [Fact]
    public void MonotonicClockRollbackClosesAndClearsWithoutNewSequence()
    {
        var clock = new ManualClock();
        using var box = Create(clock);
        DisposeAll(Queue(box, [14]));
        var owned = OwnedPayloads(box);
        clock.ShiftTimestamp(TimeSpan.FromTicks(-1));
        Assert.Throws<InvalidOperationException>(() => box.ClaimDueRetransmissions());
        Assert.Equal(EdServerReliableOutboxState.ClockRollback, box.State);
        Assert.Empty(box.PendingSequences());
        Assert.All(owned, bytes => Assert.All(bytes, b => Assert.Equal(0, b)));
        AssertClosed(box, 101);
    }

    [Fact]
    public void LifetimeExpiresWholeStreamBeforeAnyPartialRetransmission()
    {
        var clock = new ManualClock();
        using var box = Create(clock, lifetime: TimeSpan.FromSeconds(2));
        DisposeAll(Queue(box, [14]));
        clock.Advance(TimeSpan.FromMilliseconds(500));
        DisposeAll(Queue(box, [15]));
        var owned = OwnedPayloads(box);
        clock.Advance(TimeSpan.FromMilliseconds(1500));
        Assert.Throws<InvalidOperationException>(() => box.ClaimDueRetransmissions());
        Assert.Equal(EdServerReliableOutboxState.Expired, box.State);
        Assert.Equal(0, box.PendingCount);
        Assert.All(owned, bytes => Assert.All(bytes, b => Assert.Equal(0, b)));
        AssertClosed(box, 102);
    }

    [Fact]
    public void EnqueueAndAckCannotResurrectExpiredFrames()
    {
        foreach (var action in new[] { "enqueue", "ack" })
        {
            var clock = new ManualClock();
            using var box = Create(clock, lifetime: TimeSpan.FromSeconds(1));
            DisposeAll(Queue(box, [14]));
            clock.Advance(TimeSpan.FromSeconds(1));
            if (action == "enqueue") Assert.Throws<InvalidOperationException>(() => box.TryEnqueueBatch(
                new ReadOnlyMemory<byte>[] { new byte[] { 15 } }, out _));
            else Assert.Throws<InvalidOperationException>(() => box.TryAcknowledge(new(101, 0, 16)));
            Assert.Equal(EdServerReliableOutboxState.Expired, box.State);
            AssertClosed(box, 101);
        }
    }

    [Fact]
    public void AttemptLimitIncludesInitialSendAndFailsClosedOnlyAtNextDueTime()
    {
        var clock = new ManualClock();
        using var box = Create(clock, maximumAttempts: 2);
        DisposeAll(Queue(box, [14]));
        clock.Advance(TimeSpan.FromSeconds(1));
        var lastRetry = box.ClaimDueRetransmissions();
        Assert.Equal(2, Assert.Single(lastRetry).Attempt);
        Assert.Empty(box.ClaimDueRetransmissions());
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Throws<InvalidOperationException>(() => box.ClaimDueRetransmissions());
        Assert.Equal(EdServerReliableOutboxState.RetryLimitExceeded, box.State);
        AssertClosed(box, 101);
        DisposeAll(lastRetry);
    }

    [Fact]
    public void RevokeIsIdempotentClearsOwnedMemoryAndPreventsAllFurtherSends()
    {
        using var box = Create(new());
        var initial = Queue(box, [14, 1, 2], [15, 3, 4]);
        var owned = OwnedPayloads(box);
        box.Revoke();
        box.Revoke();
        Assert.Equal(EdServerReliableOutboxState.Revoked, box.State);
        Assert.Equal(0, box.PendingCount);
        Assert.All(owned, bytes => Assert.All(bytes, b => Assert.Equal(0, b)));
        AssertClosed(box, 102);
        // Already handed-out snapshots belong to their caller and are separately disposable.
        Assert.Equal(new byte[] { 14, 1, 2 }, initial[0].CopyPayload());
        DisposeAll(initial);
    }

    [Fact]
    public void AckImmediatelyClearsItsOwnedPendingBuffers()
    {
        using var box = Create(new());
        DisposeAll(Queue(box, [14, 1], [15, 2]));
        var owned = OwnedPayloads(box);
        Assert.True(box.TryAcknowledge(new(102, 0, 16)));
        Assert.All(owned, bytes => Assert.All(bytes, b => Assert.Equal(0, b)));
        Assert.Equal(0, box.PendingCount);
    }

    [Fact]
    public void ConcurrentBatchReservationsCannotExceedHardCapacity()
    {
        using var box = Create(new(), capacity: EdServerReliableOutbox.HardMaximumCapacity);
        var accepted = 0;
        Parallel.For(0, 64, _ =>
        {
            if (box.TryEnqueueBatch(new ReadOnlyMemory<byte>[] { new byte[] { 14 }, new byte[] { 15 } }, out var batch))
            {
                Interlocked.Increment(ref accepted);
                DisposeAll(batch);
            }
        });
        Assert.Equal(16, accepted);
        Assert.Equal(32, box.PendingCount);
        Assert.Equal(132, box.LastSentSequence);
        Assert.Equal(32, box.PendingSequences().Distinct().Count());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(33)]
    public void CapacityBoundsAreExplicitLocalPolicy(int invalid)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(new(), capacity: invalid));
    }

    [Fact]
    public void InvalidClockTimingSeedAndFrameBudgetAreRejected()
    {
        var budget = new EdServerReliableFrameBudget(1360, 4);
        Assert.Throws<ArgumentOutOfRangeException>(() => new EdServerReliableOutbox(0, budget, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), 2));
        Assert.Throws<ArgumentException>(() => new EdServerReliableOutbox(1, default, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EdServerReliableOutbox(1, budget, TimeSpan.Zero, TimeSpan.FromSeconds(5), 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EdServerReliableOutbox(1, budget, TimeSpan.MaxValue, TimeSpan.MaxValue, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EdServerReliableOutbox(1, budget, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EdServerReliableOutbox(1, budget, TimeSpan.FromSeconds(1), TimeSpan.MaxValue, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EdServerReliableOutbox(1, budget, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EdServerReliableOutbox(1, budget, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), 1001));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EdServerReliableFrameBudget(65508, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EdServerReliableFrameBudget(14, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EdServerReliableFrameBudget(1360, 0));
    }

    private static void AssertClosed(EdServerReliableOutbox box, ushort lastSent)
    {
        Assert.Throws<InvalidOperationException>(() => box.TryEnqueueBatch(new ReadOnlyMemory<byte>[] { new byte[] { 14 } }, out _));
        Assert.Throws<InvalidOperationException>(() => box.ClaimDueRetransmissions());
        Assert.Throws<InvalidOperationException>(() => box.TryAcknowledge(new(lastSent, 0, 16)));
        Assert.Equal(lastSent, box.LastSentSequence);
        Assert.Equal(0, box.PendingCount);
    }

    // Synthetic-only introspection verifies zeroing the actual owned cache, not a returned copy.
    private static byte[][] OwnedPayloads(EdServerReliableOutbox box)
    {
        var pending = (IDictionary)typeof(EdServerReliableOutbox).GetField("_pending", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(box)!;
        return pending.Values.Cast<object>().Select(x => (byte[])x.GetType().GetProperty("Payload")!.GetValue(x)!).ToArray();
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _timestamp = 10 * TimeSpan.TicksPerSecond;
        private DateTimeOffset _utc = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);
        public override DateTimeOffset GetUtcNow() => _utc;
        public void Advance(TimeSpan elapsed) { ShiftTimestamp(elapsed); ShiftUtc(elapsed); }
        public void ShiftTimestamp(TimeSpan elapsed) => Interlocked.Add(ref _timestamp, elapsed.Ticks);
        public void ShiftUtc(TimeSpan elapsed) => _utc += elapsed;
    }
}
