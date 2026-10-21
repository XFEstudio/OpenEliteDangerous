using XFEEliteDangerous.NativeFormat.Replication;
using Xunit;

namespace XFEEliteDangerous.Client.Tests;

public sealed class EdServerPeerInfoMessageTests
{
    [Fact]
    public void NativeLookupUsesU32CorrelationU48ReferenceAndCompressedEmptyVector()
    {
        using var request=new EdServerCallEnvelope(0x200000002,true,88,Convert.FromHexString("0D78563412060504030201"));
        Assert.Equal((0x12345678u,0x010203040506UL),EdServerPeerInfoMessageCodec.ReadRequest(request));
        using var forwarded=new EdServerCallEnvelope(request.ObjectReference,false,88,request.CopyPayload());
        Assert.Equal((0x12345678u,0x010203040506UL),EdServerPeerInfoMessageCodec.ReadRequest(forwarded));
        using var unavailable=EdServerPeerInfoMessageCodec.CreateUnavailableReply(request.ObjectReference,0x12345678);
        Assert.Equal(205,unavailable.PayloadBits);Assert.Equal(26,unavailable.CopyPayload().Length);
        Assert.Equal("0E78563412FFFFFFFFFFFFFFFF00000000000000000000000000",Convert.ToHexString(unavailable.CopyPayload()));
        Assert.Equal(0x12345678u,EdServerPeerInfoMessageCodec.ReadUnavailableReply(unavailable));
        var bytes=new byte[48];var offset=3;
        EdServerCallEnvelopeCodec.WriteBody(bytes,ref offset,unavailable,false);var end=offset;offset=3;
        using var decoded=EdServerCallEnvelopeCodec.ReadBody(bytes,ref offset,false);
        Assert.Equal(end,offset);Assert.Equal(205,decoded.PayloadBits);
        Assert.Equal(0x12345678u,EdServerPeerInfoMessageCodec.ReadUnavailableReply(decoded));
    }

    [Fact]
    public void UnknownMethodsAndInventedSuccessAreRejected()
    {
        using var wrong=new EdServerCallEnvelope(5,false,88,new byte[11]);
        Assert.Throws<InvalidDataException>(()=>EdServerPeerInfoMessageCodec.ReadRequest(wrong));
        var bytes=new byte[26];bytes[0]=14;
        using var fakeSuccess=new EdServerCallEnvelope(5,true,205,bytes);
        Assert.Throws<InvalidDataException>(()=>EdServerPeerInfoMessageCodec.ReadUnavailableReply(fakeSuccess));
        using var fullByteCount=new EdServerCallEnvelope(5,true,208,bytes);
        Assert.Throws<InvalidDataException>(()=>EdServerPeerInfoMessageCodec.ReadUnavailableReply(fullByteCount));
    }
}
