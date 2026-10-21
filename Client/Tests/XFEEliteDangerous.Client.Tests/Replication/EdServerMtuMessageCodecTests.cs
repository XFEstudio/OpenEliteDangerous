using XFEEliteDangerous.NativeFormat.Replication;
using Xunit;

namespace XFEEliteDangerous.Client.Tests;

public sealed class EdServerMtuMessageCodecTests
{
    [Fact]
    public void ReadsLiteralLittleEndianMtuTestBody()
    {
        var offset = 0;
        var value = EdServerMtuMessageCodec.ReadTest(Convert.FromHexString("F0040300AABBCC"), ref offset);
        Assert.Equal(1264, value.Mtu);
        Assert.Equal(Convert.FromHexString("AABBCC"), value.Padding.ToArray());
        Assert.Equal(56, offset);
    }

    [Fact]
    public void ReadsNonAlignedLiteralReply()
    {
        // Three prefix bits followed by 0x04f0 little-endian bit field.
        var offset = 3;
        Assert.Equal(1264, EdServerMtuMessageCodec.ReadReply(Convert.FromHexString("852700"), ref offset));
        Assert.Equal(19, offset);
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
    public void NonAlignedMessagesPreserveNeighborBits(int offset)
    {
        var bytes = Enumerable.Repeat((byte)0xA5, 20).ToArray();
        var before = bytes.ToArray();
        var write = offset;
        EdServerMtuMessageCodec.WriteTest(bytes, ref write, 1500, [0x12, 0x34, 0xFE]);
        var read = offset;
        var test = EdServerMtuMessageCodec.ReadTest(bytes, ref read);
        Assert.Equal(1500, test.Mtu);
        Assert.Equal(new byte[] { 0x12, 0x34, 0xFE }, test.Padding.ToArray());
        Assert.Equal(write, read);
        for (var bit = 0; bit < bytes.Length * 8; bit++)
            if (bit < offset || bit >= write)
                Assert.Equal((before[bit / 8] >> (bit % 8)) & 1, (bytes[bit / 8] >> (bit % 8)) & 1);
        EdServerMtuMessageCodec.WriteReply(bytes, ref write, 1500);
        Assert.Equal(1500, EdServerMtuMessageCodec.ReadReply(bytes, ref read));
        Assert.Equal(write, read);
    }

    [Theory]
    [InlineData("")]
    [InlineData("F0")]
    [InlineData("F004")]
    [InlineData("F00403")]
    [InlineData("F0040300AABB")]
    [InlineData("F004FFFF")]
    public void TruncatedTestPreservesCursor(string hex)
    {
        var offset = 0;
        Assert.Throws<InvalidDataException>(() => EdServerMtuMessageCodec.ReadTest(Convert.FromHexString(hex), ref offset));
        Assert.Equal(0, offset);
    }

    [Fact]
    public void FailedWriteDoesNotMutateDestinationOrCursor()
    {
        var bytes = new byte[] { 0xAA, 0x55, 0xAA, 0x55 };
        var before = bytes.ToArray();
        var offset = 1;
        Assert.Throws<InvalidDataException>(() => EdServerMtuMessageCodec.WriteTest(bytes, ref offset, 1500, []));
        Assert.Equal(1, offset);
        Assert.Equal(before, bytes);
        offset = -1;
        Assert.Throws<InvalidDataException>(() => EdServerMtuMessageCodec.ReadReply(bytes, ref offset));
        Assert.Equal(-1, offset);
    }
}
