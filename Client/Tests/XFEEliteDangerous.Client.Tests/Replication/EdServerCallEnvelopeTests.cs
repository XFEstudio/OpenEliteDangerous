using XFEEliteDangerous.NativeFormat.Replication;
using Xunit;

namespace XFEEliteDangerous.Client.Tests;

public sealed class EdServerCallEnvelopeTests
{
    [Theory]
    [InlineData(true,0,8,165,88,"02000000020011000000a5")]
    [InlineData(false,0,8,165,65,"020000000200474a01")]
    [InlineData(true,3,3,5,86,"1000000010003800000028")]
    [InlineData(false,7,0,0,61,"0001000000018000")]
    public void EnvelopeMatchesIndependentNativeHeaderVectors(bool aligned,int start,int bits,byte value,int end,string hex)
    {
        var expected=Convert.FromHexString(hex);using var call=new EdServerCallEnvelope(0x200000002,true,bits,bits==0?[]:[value]);
        var output=new byte[expected.Length];var p=start;EdServerCallEnvelopeCodec.WriteBody(output,ref p,call,aligned);
        Assert.Equal(end,p);Assert.Equal(expected,output);Assert.Equal(end-start,EdServerCallEnvelopeCodec.GetBodyBitLength(call,aligned));
        p=start;using var decoded=EdServerCallEnvelopeCodec.ReadBody(expected,ref p,aligned);
        Assert.Equal(call.ObjectReference,decoded.ObjectReference);Assert.Equal(call.Flag,decoded.Flag);Assert.Equal(bits,decoded.PayloadBits);Assert.Equal(call.CopyPayload(),decoded.CopyPayload());
        for(var length=0;length<(end+7)/8;length++)
        {
            var shortened=expected[..length];var cursor=start;
            Assert.Throws<InvalidDataException>(()=>EdServerCallEnvelopeCodec.ReadBody(shortened,ref cursor,aligned));Assert.Equal(start,cursor);
        }
    }
    [Fact]
    public void InvalidPayloadAndShortOutputAreRejectedWithoutWrites()
    {
        Assert.Throws<ArgumentException>(()=>new EdServerCallEnvelope(1UL<<48,false,0,[]));
        Assert.Throws<ArgumentException>(()=>new EdServerCallEnvelope(1,false,8193,new byte[1025]));
        Assert.Throws<ArgumentException>(()=>new EdServerCallEnvelope(1,false,3,[255]));
        Assert.Throws<ArgumentException>(()=>new EdServerCallEnvelope(1,false,8,[]));
        using var call=new EdServerCallEnvelope(1,false,8,[42]);var output=new byte[]{0xA5};var p=0;
        Assert.Throws<InvalidDataException>(()=>EdServerCallEnvelopeCodec.WriteBody(output,ref p,call,true));Assert.Equal(0,p);Assert.Equal(new byte[]{0xA5},output);
        var payload=call.CopyPayload();payload[0]=1;Assert.Equal(new byte[]{42},call.CopyPayload());
        call.Dispose();Assert.Throws<ObjectDisposedException>(()=>call.CopyPayload());
    }
}
