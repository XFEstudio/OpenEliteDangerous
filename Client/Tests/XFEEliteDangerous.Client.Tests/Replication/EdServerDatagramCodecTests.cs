using System.Text.Json;
using XFEEliteDangerous.NativeFormat.Replication;
using Xunit;

namespace XFEEliteDangerous.Client.Tests;

public sealed class EdServerDatagramCodecTests
{
    private static byte[] Key => Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();

    [Fact]
    public void MatchesIndependentPythonAesAndZlibFixtures()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "edserver-mode3-reference.json")));
        foreach (var vector in document.RootElement.GetProperty("vectors").EnumerateArray())
        {
            var header = new EdServerDatagramHeader(vector.GetProperty("senderRunId").GetUInt64(),
                vector.GetProperty("opaqueFlag").GetBoolean(), vector.GetProperty("counter").GetUInt16());
            var letters = Convert.FromHexString(vector.GetProperty("letterHex").GetString()!);
            var key = Convert.FromHexString(vector.GetProperty("keyHex").GetString()!);
            var packet = Convert.FromHexString(vector.GetProperty("packetHex").GetString()!);
            Assert.Equal(packet, EdServerDatagramCodec.EncodeMode3(header, letters, key));
            using var decoded = EdServerDatagramCodec.DecodeMode3(packet, key);
            Assert.Equal(header, decoded.Header);
            Assert.Equal(letters, decoded.OpaqueLetterBytes.ToArray());
        }
    }

    [Fact]
    public void HeaderHasLittleEndianRunIdFlagAndCounter()
    {
        var packet = Convert.FromHexString("BC9A785634120300");
        var header = EdServerDatagramHeader.Read(packet);
        Assert.Equal(0x123456789ABCUL, header.SenderRunId);
        Assert.True(header.OpaqueFlag);
        Assert.Equal(1, header.PacketCounter);
        var output = new byte[8];
        header.Write(output);
        Assert.Equal(packet, output);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(65507 - 10)]
    public void EncodesWholeAndPartialAesBlocks(int length)
    {
        var input = Enumerable.Range(0, length).Select(value => (byte)(value * 13)).ToArray();
        var header = new EdServerDatagramHeader(123, false, 32767);
        var packet = EdServerDatagramCodec.EncodeMode3(header, input, Key);
        Assert.Equal(length + 10, packet.Length);
        Assert.Equal(input, EdServerDatagramCodec.DecodeMode3(packet, Key).OpaqueLetterBytes.ToArray());
    }

    [Fact]
    public void CipherUsesFeedbackRatherThanRepeatedOrIncrementedSeed()
    {
        var stream = new byte[48];
        EdServerDatagramCodec.TransformMode3(stream, Key, 0);
        // AES-256 ECB of all-zero block with the public 00..1f test key.
        Assert.Equal(Convert.FromHexString("F29000B62A499FD0A9F39A6ADD2E7780"), stream[..16]);
        Assert.False(stream[..16].SequenceEqual(stream[16..32]));
        EdServerDatagramCodec.TransformMode3(stream, Key, 0);
        Assert.All(stream, value => Assert.Equal(0, value));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(8)]
    [InlineData(10)]
    [InlineData(35)]
    public void CorruptionIsRejectedWithoutReturningLetters(int index)
    {
        var packet = EdServerDatagramCodec.EncodeMode3(new(123, false, 12), new byte[30], Key);
        packet[index] ^= 0x10;
        Assert.Throws<InvalidDataException>(() => EdServerDatagramCodec.DecodeMode3(packet, Key));
    }

    [Fact]
    public void WrongKeyIsRejected()
    {
        var packet = EdServerDatagramCodec.EncodeMode3(new(123, true, 12), new byte[30], Key);
        Assert.Throws<InvalidDataException>(() => EdServerDatagramCodec.DecodeMode3(packet, new byte[32]));
    }

    [Fact]
    public void DisposalClearsTheExposedBackingBuffer()
    {
        var packet = EdServerDatagramCodec.EncodeMode3(new(123, false, 1), [1, 2, 3, 4], Key);
        var decoded = EdServerDatagramCodec.DecodeMode3(packet, Key);
        var view = decoded.OpaqueLetterBytes;
        decoded.Dispose();
        Assert.All(view.ToArray(), value => Assert.Equal(0, value));
        Assert.Throws<ObjectDisposedException>(() => decoded.OpaqueLetterBytes);
        decoded.Dispose();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(65508)]
    public void InvalidLengthsAreRejected(int length) => Assert.Throws<InvalidDataException>(() =>
        EdServerDatagramCodec.DecodeMode3(new byte[length], Key));

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(33)]
    public void CipherRejectsWrongKeySizes(int length) => Assert.Throws<ArgumentException>(() =>
        EdServerDatagramCodec.EncodeMode3(new(123, false, 1), [], new byte[length]));

    [Fact]
    public void HeaderRejectsZeroOverflowAndDefaultState()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new EdServerDatagramHeader(0, false, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EdServerDatagramHeader(1UL << 48, false, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EdServerDatagramHeader(1, false, 32768));
        Assert.Throws<InvalidDataException>(() => EdServerDatagramHeader.Read(new byte[8]));
        Assert.Throws<InvalidDataException>(() => default(EdServerDatagramHeader).Write(new byte[8]));
    }
}
