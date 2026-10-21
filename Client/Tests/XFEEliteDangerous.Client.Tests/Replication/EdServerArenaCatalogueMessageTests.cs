using XFEEliteDangerous.NativeFormat.Replication;
using Xunit;

namespace XFEEliteDangerous.Client.Tests;

public sealed class EdServerArenaCatalogueMessageTests
{
    [Fact]
    public void NativeEmptyArenaReplyUsesCompressedZeroCountAndUnalignedEnvelope()
    {
        using var request = new EdServerCallEnvelope(0x10203040506, true, 8, [70]);
        EdServerArenaCatalogueMessageCodec.ReadRequest(request);
        using var forwarded = new EdServerCallEnvelope(request.ObjectReference, false, 8, [70]);
        EdServerArenaCatalogueMessageCodec.ReadRequest(forwarded);
        using var reply = EdServerArenaCatalogueMessageCodec.CreateEmptyReply(request.ObjectReference);
        Assert.Equal(13, reply.PayloadBits);
        Assert.Equal("4700", Convert.ToHexString(reply.CopyPayload()));
        var wire = new byte[(EdServerCallEnvelopeCodec.GetBodyBitLength(reply, false) + 7) / 8];
        var written = 0;
        EdServerCallEnvelopeCodec.WriteBody(wire, ref written, reply, false);
        var read = 0;
        using var decoded = EdServerCallEnvelopeCodec.ReadBody(wire, ref read, false);
        Assert.Equal(written, read);
        Assert.Equal(request.ObjectReference, decoded.ObjectReference);
        EdServerArenaCatalogueMessageCodec.ReadEmptyReply(decoded);
    }

    [Fact]
    public void ArenaCodecRejectsOtherMethodsNonemptyVectorsAndWrongCountWidth()
    {
        using var wrongMethod = new EdServerCallEnvelope(11, true, 8, [69]);
        using var wrongReplyDirection = new EdServerCallEnvelope(11, true, 13, [71, 0]);
        using var extraRequest = new EdServerCallEnvelope(11, true, 16, [70, 0]);
        using var wideCount = new EdServerCallEnvelope(11, true, 40, [71, 0, 0, 0, 0]);
        using var nonempty = new EdServerCallEnvelope(11, true, 13, [71, 1]);
        Assert.Throws<InvalidDataException>(() => EdServerArenaCatalogueMessageCodec.ReadRequest(wrongMethod));
        Assert.Throws<InvalidDataException>(() => EdServerArenaCatalogueMessageCodec.ReadEmptyReply(wrongReplyDirection));
        Assert.Throws<InvalidDataException>(() => EdServerArenaCatalogueMessageCodec.ReadRequest(extraRequest));
        Assert.Throws<InvalidDataException>(() => EdServerArenaCatalogueMessageCodec.ReadEmptyReply(wideCount));
        Assert.Throws<InvalidDataException>(() => EdServerArenaCatalogueMessageCodec.ReadEmptyReply(nonempty));
    }
}
