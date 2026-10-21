using System.Text.Json;
using XFEEliteDangerous.NativeFormat.Replication;
using Xunit;

namespace XFEEliteDangerous.Client.Tests;

public sealed class EdServerLetterFramingTests
{
    [Fact]
    public void NativeEnvelopeMatchesSixIndependentBitPackingVectors()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "edserver-letter-framing-reference.json")));
        foreach (var v in fixture.RootElement.GetProperty("vectors").EnumerateArray())
        {
            var expected = Convert.FromHexString(v.GetProperty("hex").GetString()!);
            var start = v.GetProperty("startBit").GetInt32();
            var typeBits = v.GetProperty("typeBits").GetInt32();
            var type = v.GetProperty("type").GetByte();
            var alignment = v.GetProperty("alignmentMode").GetByte();
            var delimited = v.GetProperty("lengthDelimited").GetBoolean();
            var mtu = v.GetProperty("mtu").GetUInt16();
            var p = start;
            Assert.Equal(type, EdServerControlMessageCodec.ReadLetterType(expected, ref p, typeBits));
            var readFrame = EdServerLetterFramingCodec.BeginRead(expected, ref p, type, delimited, alignment);
            Assert.Equal(v.GetProperty("bodyStartBit").GetInt32(), p);
            Assert.Equal(mtu, EdServerMtuMessageCodec.ReadReply(expected, ref p));
            Assert.Equal(v.GetProperty("bodyEndBit").GetInt32(), p);
            EdServerLetterFramingCodec.CompleteRead(expected, ref p, readFrame);
            Assert.Equal(v.GetProperty("endBit").GetInt32(), p);
            var actual = new byte[expected.Length];
            p = start;
            EdServerControlMessageCodec.WriteLetterType(actual, ref p, type, typeBits);
            var writeFrame = EdServerLetterFramingCodec.BeginWrite(actual, ref p, type, delimited, alignment);
            EdServerMtuMessageCodec.WriteReply(actual, ref p, mtu);
            EdServerLetterFramingCodec.CompleteWrite(actual, ref p, writeFrame);
            Assert.Equal(v.GetProperty("endBit").GetInt32(), p);
            Assert.Equal(expected, actual);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(255)]
    public void NonzeroBytePaddingAlignsEachLetterNotThreeBytes(int mode)
    {
        var bytes = new byte[18];
        var p = 0;
        foreach (ushort value in new ushort[] { 1360, 1500 })
        {
            EdServerControlMessageCodec.WriteLetterType(bytes, ref p, 7, 11);
            var frame = EdServerLetterFramingCodec.BeginWrite(bytes, ref p, 7, true, (byte)mode);
            EdServerMtuMessageCodec.WriteReply(bytes, ref p, value);
            EdServerLetterFramingCodec.CompleteWrite(bytes, ref p, frame);
            Assert.Equal(0, p % 8);
        }
        Assert.Equal(144, p); // Each 11 + 24 + 16 + 5 pad + 16 trailer = 72 bits.
    }

    [Fact]
    public void SyncHasNoLengthOrTrailerEvenWhenFramingIsAdvertised()
    {
        var bytes = new byte[4];
        var p = 16;
        var frame = EdServerLetterFramingCodec.BeginWrite(bytes, ref p, 0, true, 3);
        Assert.Equal(-1, frame.PrefixStartBit);
        Assert.Equal(16, p);
        p += 3;
        EdServerLetterFramingCodec.CompleteWrite(bytes, ref p, frame);
        Assert.Equal(24, p);
    }

    [Fact]
    public void CorruptTrailerIsRejectedWithoutAdvancingCallerCursor()
    {
        // Build a valid independent vector first; changing its trailer must fail.
        var bytes = MakeReply();
        var p = 11;
        var frame = EdServerLetterFramingCodec.BeginRead(bytes, ref p, 7, true, 3);
        EdServerMtuMessageCodec.ReadReply(bytes, ref p);
        bytes[^1] ^= 1;
        var bodyEnd = p;
        Assert.Throws<InvalidDataException>(() => EdServerLetterFramingCodec.CompleteRead(bytes, ref p, frame));
        Assert.Equal(bodyEnd, p);
    }

    [Fact]
    public void NativePooledAlignmentBitsAreSkippedWhileTheTrailerIsStillVerified()
    {
        var bytes = MakeReply();
        var p = 11;
        var frame = EdServerLetterFramingCodec.BeginRead(bytes, ref p, 7, true, 3);
        EdServerMtuMessageCodec.ReadReply(bytes, ref p);
        bytes[p / 8] |= (byte)(1 << (p % 8));
        EdServerLetterFramingCodec.CompleteRead(bytes, ref p, frame);
        Assert.Equal(bytes.Length*8,p);
    }

    [Fact]
    public void DeclaredLengthCannotHideExtraOrMissingBodyBits()
    {
        var bytes = MakeReply();
        var p = 11;
        var frame = EdServerLetterFramingCodec.BeginRead(bytes, ref p, 7, true, 3);
        p += 8;
        Assert.Throws<InvalidDataException>(() => EdServerLetterFramingCodec.CompleteRead(bytes, ref p, frame));
        p = frame.DeclaredBodyEndBit!.Value + 1;
        Assert.Throws<InvalidDataException>(() => EdServerLetterFramingCodec.CompleteRead(bytes, ref p, frame));
    }

    [Fact]
    public void TruncatedEnvelopeDoesNotCommitPrefixRead()
    {
        var p = 11;
        Assert.Throws<InvalidDataException>(() => EdServerLetterFramingCodec.BeginRead(new byte[4], ref p, 7, true, 3));
        Assert.Equal(11, p);
        p = 0;
        Assert.Throws<InvalidDataException>(() => EdServerLetterFramingCodec.BeginRead(
            new byte[] { 0xFF, 0xFF, 0xFF }, ref p, 7, true, 3));
        Assert.Equal(0, p);
    }

    [Fact]
    public void CompletionCannotWritePastBufferOrForgePrefix()
    {
        var bytes = new byte[7];
        var p = 11;
        var frame = EdServerLetterFramingCodec.BeginWrite(bytes, ref p, 7, true, 3);
        EdServerMtuMessageCodec.WriteReply(bytes, ref p, 1360);
        Assert.Throws<InvalidDataException>(() => EdServerLetterFramingCodec.CompleteWrite(bytes, ref p, frame));
        Assert.Equal(51, p);
        Assert.Throws<InvalidDataException>(() => EdServerLetterFramingCodec.CompleteWrite(bytes, ref p,
            frame with { PrefixStartBit = 1 }));
    }

    private static byte[] MakeReply()
    {
        var bytes = new byte[9];
        var p = 0;
        EdServerControlMessageCodec.WriteLetterType(bytes, ref p, 7, 11);
        var frame = EdServerLetterFramingCodec.BeginWrite(bytes, ref p, 7, true, 3);
        EdServerMtuMessageCodec.WriteReply(bytes, ref p, 1360);
        EdServerLetterFramingCodec.CompleteWrite(bytes, ref p, frame);
        return bytes;
    }
}
