using XFEEliteDangerous.NativeFormat.Replication;
using Xunit;
namespace XFEEliteDangerous.Client.Tests;
public sealed class EdServerPersistentRecordMapTests
{
    [Fact]public void EmptyRecordMapPreservesCorrelationAndMatchesTheNativeCompressedLayout()
    {
        using var request=new EdServerCallEnvelope(0x200000002,false,40,Convert.FromHexString("4E04030201"));
        Assert.Equal(0x01020304u,EdServerPersistentRecordMapMessageCodec.ReadRequest(request));
        using var reply=EdServerPersistentRecordMapMessageCodec.CreateEmptyReply(request.ObjectReference,0x01020304);
        Assert.False(reply.Flag);Assert.Equal(128,reply.PayloadBits);
        Assert.Equal("4F0403020163081C0000000000000000",Convert.ToHexString(reply.CopyPayload()));
        // Independent raw-Snappy literal interpretation: prefix declares8
        // output bytes; tag low2 bits select a literal and upper6 encode len-1.
        var block=reply.CopyPayload().AsSpan(6);Assert.Equal(8,block[0]);Assert.Equal(0,block[1]&3);
        var literalLength=(block[1]>>2)+1;Assert.Equal(8,literalLength);Assert.Equal(literalLength+2,block.Length);
        Assert.True(block[2..].ContainsAnyExcept((byte)0)==false);
        Assert.Equal(0x01020304u,EdServerPersistentRecordMapMessageCodec.ReadEmptyReply(reply));
    }
    [Fact]public void NonemptyOrMalformedMapCannotBeAcceptedAsEmpty()
    {
        using var reply=EdServerPersistentRecordMapMessageCodec.CreateEmptyReply(7,9);
        var bytes=reply.CopyPayload();bytes[^1]=1;
        using var nonempty=new EdServerCallEnvelope(7,false,128,bytes);
        using var wrongMethod=new EdServerCallEnvelope(7,false,40,[77,0,0,0,0]);
        Assert.Throws<InvalidDataException>(()=>EdServerPersistentRecordMapMessageCodec.ReadEmptyReply(nonempty));
        Assert.Throws<InvalidDataException>(()=>EdServerPersistentRecordMapMessageCodec.ReadRequest(wrongMethod));
    }
}
