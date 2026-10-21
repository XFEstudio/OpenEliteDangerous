using System.Text;
using System.Text.Json;
using XFEEliteDangerous.NativeFormat.Replication;
using Xunit;

namespace XFEEliteDangerous.Client.Tests;

public sealed class EdServerHandshakeTests
{
    private static EdServerSyncMessage Client(ushort sequence = 25, ushort ack = 0) =>
        new(33837, sequence, ack, 11, 0x3F800000, false, 0, 0,
            Encoding.ASCII.GetBytes("LocalServer"), 0x7F000001, 5100, ReadOnlyMemory<byte>.Empty, false, true, 332841, 0);
    private static EdServerSyncMessage Server() => Client() with { IsClient = false };

    [Fact]
    public void SyncFieldsMatchIndependentBitPackingFixtures()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "edserver-sync-reference.json")));
        foreach (var vector in json.RootElement.GetProperty("vectors").EnumerateArray())
        {
            var m = vector.GetProperty("message");
            var expected = Client(m.GetProperty("sequence").GetUInt16(), m.GetProperty("acknowledgement").GetUInt16()) with
            { EncryptedKeyBytes = Convert.FromHexString(m.GetProperty("encryptedKeyHex").GetString()!) };
            var bytes = Convert.FromHexString(vector.GetProperty("letterHex").GetString()!);
            Assert.Equal(bytes, EdServerControlMessageCodec.EncodeSyncLetter(expected));
            var p = 0;
            var actual = EdServerControlMessageCodec.ReadSyncLetter(bytes, ref p, 33837);
            Assert.Equal(vector.GetProperty("consumedBits").GetInt32(), p);
            Assert.Equal(expected with { NameBytes = actual.NameBytes, EncryptedKeyBytes = actual.EncryptedKeyBytes }, actual);
            Assert.Equal(expected.NameBytes.ToArray(), actual.NameBytes.ToArray());
            Assert.Equal(expected.EncryptedKeyBytes.ToArray(), actual.EncryptedKeyBytes.ToArray());
        }
    }

    [Fact]
    public void SyncReadsNegotiatedOneThroughSixteenBitWidths()
    {
        for (byte width = 1; width <= 16; width++)
        {
            var p = 0;
            var bytes = EdServerControlMessageCodec.EncodeSyncLetter(Client() with { LetterTypeBits = width });
            Assert.Equal(width, EdServerControlMessageCodec.ReadSyncLetter(bytes, ref p, 33837).LetterTypeBits);
        }
    }

    [Fact]
    public void TruncatedOrWrongProtocolSyncDoesNotCommitCursor()
    {
        var bytes = EdServerControlMessageCodec.EncodeSyncLetter(Client());
        for (var length = 0; length < bytes.Length; length++)
        {
            var p = 0;
            var truncated = bytes[..length];
            Assert.Throws<InvalidDataException>(() => EdServerControlMessageCodec.ReadSyncLetter(truncated, ref p, 33837));
            Assert.Equal(0, p);
        }
        var offset = 0;
        Assert.Throws<InvalidDataException>(() => EdServerControlMessageCodec.ReadSyncLetter(bytes, ref offset, 1));
        Assert.Equal(0, offset);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(11)]
    [InlineData(16)]
    public void AckUsesExactSixteenTenSixBitFields(int typeBits)
    {
        var expected = new EdServerAckMessage(65535, 1023, 63);
        var bytes = EdServerControlMessageCodec.EncodeAckLetter(expected, typeBits);
        var p = 0;
        Assert.Equal(1, EdServerControlMessageCodec.ReadLetterType(bytes, ref p, typeBits));
        Assert.Equal(expected, EdServerControlMessageCodec.ReadAckBody(bytes, ref p));
        Assert.Equal(typeBits + 32, p);
    }

    [Fact]
    public void SequenceAndAckRejectZero()
    {
        var p = 0;
        Assert.Throws<InvalidDataException>(() => EdServerControlMessageCodec.ReadSequenceBody(new byte[2], ref p));
        Assert.Equal(0, p);
        Assert.Throws<InvalidDataException>(() => EdServerControlMessageCodec.ReadAckBody(new byte[4], ref p));
        Assert.Equal(0, p);
        Assert.Throws<ArgumentOutOfRangeException>(() => EdServerControlMessageCodec.EncodeAckLetter(new(0, 0, 1), 11));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(11)]
    [InlineData(16)]
    public void AllocatedIdBodyIsCompleteUInt64WithoutInventedPolicy(int typeBits)
    {
        var bytes = EdServerControlMessageCodec.EncodeAllocatedIdLetter(0xFEDCBA9876543210UL, typeBits);
        var p = 0;
        Assert.Equal(11, EdServerControlMessageCodec.ReadLetterType(bytes, ref p, typeBits));
        Assert.Equal(0xFEDCBA9876543210UL, EdServerControlMessageCodec.ReadAllocatedIdBody(bytes, ref p));
        Assert.Equal(typeBits + 64, p);
        Assert.Equal((byte)10, (byte)EdServerBuiltInLetter.IdAllocationRequest);
    }

    [Fact]
    public void ReliableMtuReplyFollowsSequenceInOneContinuousBitStream()
    {
        var bytes = new byte[20];
        var p = 0;
        EdServerControlMessageCodec.WriteAckLetter(bytes, ref p, new(25, 0, 16), 11);
        EdServerControlMessageCodec.WriteSequenceLetter(bytes, ref p, 101, 11);
        EdServerControlMessageCodec.WriteLetterType(bytes, ref p, (byte)EdServerBuiltInLetter.MtuReply, 11);
        EdServerMtuMessageCodec.WriteReply(bytes, ref p, 1360);
        Assert.Equal(97, p);
        var read = 0;
        Assert.Equal(1, EdServerControlMessageCodec.ReadLetterType(bytes, ref read, 11));
        Assert.Equal(new EdServerAckMessage(25, 0, 16), EdServerControlMessageCodec.ReadAckBody(bytes, ref read));
        Assert.Equal(2, EdServerControlMessageCodec.ReadLetterType(bytes, ref read, 11));
        Assert.Equal(101, EdServerControlMessageCodec.ReadSequenceBody(bytes, ref read));
        Assert.Equal(7, EdServerControlMessageCodec.ReadLetterType(bytes, ref read, 11));
        Assert.Equal(1360, EdServerMtuMessageCodec.ReadReply(bytes, ref read));
        Assert.Equal(p, read);
        Assert.True(EdServerControlMessageCodec.IsZeroPadding(bytes, read));
        Assert.False(EdServerControlMessageCodec.IsZeroPadding(bytes, 0));
    }

    [Fact]
    public void ServerHardSyncReplyAndMatchingAckEstablishOnlyTransport()
    {
        var machine = new EdServerHandshakeMachine(Server(), 100);
        var reply = machine.ReceiveSync(Client());
        Assert.NotNull(reply);
        Assert.Equal(101, reply.Sequence);
        Assert.Equal(25, reply.Acknowledgement);
        Assert.False(reply.IsClient);
        Assert.Equal(11, machine.IncomingLetterTypeBits);
        Assert.Equal(EdServerHandshakeState.AwaitingAcknowledgement, machine.State);
        Assert.False(machine.ReceiveAcknowledgement(new(100, 0, 1)));
        Assert.Equal(EdServerHandshakeState.AwaitingAcknowledgement, machine.State);
        Assert.True(machine.ReceiveAcknowledgement(new(101, 0, 1)));
        Assert.Equal(EdServerHandshakeState.Established, machine.State);
        Assert.Null(machine.PendingSync);
    }

    [Fact]
    public void DuplicateHardSyncReusesPendingSequence()
    {
        var machine = new EdServerHandshakeMachine(Server(), 100);
        var first = machine.ReceiveSync(Client());
        var second = machine.ReceiveSync(Client());
        Assert.Equal(first, second);
        Assert.Equal(101, machine.LocalSequence);
        Assert.Equal(EdServerHandshakeState.AwaitingAcknowledgement, machine.State);
    }

    [Fact]
    public void MatchingSoftSyncEstablishesWithoutSyncReplyLoop()
    {
        var machine = new EdServerHandshakeMachine(Server(), 100);
        machine.ReceiveSync(Client());
        Assert.Null(machine.ReceiveSync(Client(25, 101)));
        Assert.Equal(EdServerHandshakeState.Established, machine.State);
        Assert.Null(machine.PendingSync);
    }

    [Fact]
    public void NewHardSyncRequiresAFreshValidatedEpochAndCannotMutateTheOldExchange()
    {
        var original=new EdServerHandshakeMachine(Server(),100);
        original.ReceiveSync(Client());original.ReceiveAcknowledgement(new(101,0,0));
        var next=original.CreateRestart(Client(70),500);
        Assert.Equal(EdServerHandshakeState.Established,original.State);Assert.Equal(25,original.RemoteSequence);
        Assert.Equal(EdServerHandshakeState.AwaitingAcknowledgement,next.State);
        Assert.Equal(501,next.LocalSequence);Assert.Equal(70,next.RemoteSequence);
        Assert.Equal(next.PendingSync,next.ReceiveSync(Client(70)));
        Assert.False(next.ReceiveAcknowledgement(new(101,0,0)));
        Assert.True(next.ReceiveAcknowledgement(new(501,0,0)));
        Assert.Throws<InvalidDataException>(()=>original.CreateRestart(Client(70)with{BuildRevision=1},700));
        Assert.Throws<InvalidOperationException>(()=>original.CreateRestart(Client(),700));
        Assert.Throws<InvalidOperationException>(()=>original.CreateRestart(Client(70,1),700));
        Assert.Equal(EdServerHandshakeState.Established,original.State);Assert.Equal(101,original.LocalSequence);
    }

    [Fact]
    public void InvalidAndUnsupportedSyncDoNotEstablish()
    {
        var machine = new EdServerHandshakeMachine(Server(), 100);
        Assert.Throws<InvalidDataException>(() => machine.ReceiveSync(Client(0)));
        Assert.Throws<InvalidDataException>(() => machine.ReceiveSync(Client() with { Protocol = 1 }));
        Assert.Throws<InvalidDataException>(() => machine.ReceiveSync(Client() with { BuildRevision = 1 }));
        Assert.Throws<InvalidDataException>(() => machine.ReceiveSync(Client() with { IsClient = false }));
        Assert.Throws<InvalidDataException>(() => machine.ReceiveSync(Client(25, 101)));
        Assert.Throws<NotSupportedException>(() => machine.ReceiveSync(Client() with { EncryptedKeyBytes = new byte[] { 1 } }));
        Assert.Equal(EdServerHandshakeState.Disconnected, machine.State);
    }

    [Fact]
    public void AdvertisedFramingAndBytePaddingAreTrackedIndependentlyFromServer()
    {
        var machine = new EdServerHandshakeMachine(Server(), 100);
        var reply = machine.ReceiveSync(Client() with { LengthDelimited = true, AlignmentMode = 3 });
        Assert.True(machine.IncomingLengthDelimited);
        Assert.Equal(3, machine.IncomingAlignmentMode);
        Assert.False(reply!.LengthDelimited);
        Assert.Equal(0, reply.AlignmentMode);
    }

    [Fact]
    public void SequenceSpaceWrapsAt65535WithoutZero()
    {
        Assert.Equal(1, EdServerHandshakeMachine.Next(65535));
        Assert.Equal(65535, EdServerHandshakeMachine.Next(65534));
        var machine = new EdServerHandshakeMachine(Server(), 65535);
        Assert.Equal(1, machine.ReceiveSync(Client())!.Sequence);
        Assert.True(machine.ReceiveAcknowledgement(new(1, 0, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => EdServerHandshakeMachine.Next(0));
    }
}
