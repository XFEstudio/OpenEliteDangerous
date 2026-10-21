using XFEEliteDangerous.NativeFormat.Replication;
using Xunit;

namespace XFEEliteDangerous.Client.Tests;

public sealed class EdServerObjectReplicationTests
{
    [Fact]
    public void SaluteMatchesNativeReferenceAndFlagsWithoutGrantingAuthority()
    {
        var expected=Convert.FromHexString("02000000020003");var bytes=new byte[7];var p=0;
        var value=new EdServerObjectSalute(0x200000002,true,true);
        EdServerObjectReplicationCodec.WriteSaluteBody(bytes,ref p,value);Assert.Equal(50,p);Assert.Equal(expected,bytes);
        p=0;Assert.Equal(value,EdServerObjectReplicationCodec.ReadSaluteBody(expected,ref p));Assert.Equal(50,p);
        for(var length=0;length<7;length++)
        {
            var shortened=expected[..length];var cursor=0;
            Assert.Throws<InvalidDataException>(()=>EdServerObjectReplicationCodec.ReadSaluteBody(shortened,ref cursor));Assert.Equal(0,cursor);
        }
        p=0;Assert.Throws<ArgumentOutOfRangeException>(()=>EdServerObjectReplicationCodec.WriteSaluteBody(bytes,ref p,value with{ObjectReference=1UL<<48}));
        Assert.Equal(expected,bytes);Assert.Equal(0,p);
    }
    [Fact]
    public void AcknowledgementMatchesIndependentLayoutAndTruncatesAtomically()
    {
        var expected=Convert.FromHexString("0200000002000600");var p=0;
        var value=new EdServerObjectStateAcknowledgement(0x200000002,false,true,1);var actual=new byte[8];
        EdServerObjectReplicationCodec.WriteAcknowledgementBody(actual,ref p,value);Assert.Equal(60,p);Assert.Equal(expected,actual);
        p=0;Assert.Equal(value,EdServerObjectReplicationCodec.ReadAcknowledgementBody(expected,ref p));Assert.Equal(60,p);
        for(var length=0;length<8;length++)
        {
            var truncated=expected[..length];var cursor=0;
            Assert.Throws<InvalidDataException>(()=>EdServerObjectReplicationCodec.ReadAcknowledgementBody(truncated,ref cursor));Assert.Equal(0,cursor);
        }
        foreach(var invalid in new[]{value with{ObjectReference=1UL<<48},value with{Sequence=1024}})
        {
            p=0;Assert.Throws<ArgumentOutOfRangeException>(()=>EdServerObjectReplicationCodec.WriteAcknowledgementBody(actual,ref p,invalid));Assert.Equal(0,p);Assert.Equal(expected,actual);
        }
    }
    // Independent bit-list vectors, assembled from the native reader field widths.
    [Theory]
    [InlineData(8589934594UL,0,1,0,0,105,"0200000002000000024800000000")]
    [InlineData(8589938690UL,44500,1023,3,3,111,"10800000100040ddfa3f03000000")]
    [InlineData(281474976710655UL,65535,512,17,7,129,"80ffffffffff7fffff006a000000000000")]
    [InlineData(8589942786UL,1,3,512,1,618,"04400000040004000c902000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000")]
    public void DefaultsMatchNativeLayout(ulong reference,ushort version,ushort sequence,int mask,int start,int end,string hex)
    {
        var expected=Convert.FromHexString(hex);var message=new EdServerDefaultObjectState(reference,version,sequence,mask);
        var actual=new byte[expected.Length];var p=start;
        EdServerObjectReplicationCodec.WriteDefaultBody(actual,ref p,message);
        Assert.Equal(expected,actual);Assert.Equal(end,p);
        Assert.Equal(end-start,EdServerObjectReplicationCodec.GetDefaultBodyBitLength(message));
        p=start;Assert.Equal(message,EdServerObjectReplicationCodec.ReadDefaultBody(expected,ref p,mask));Assert.Equal(end,p);
        for(var length=0;length<(end+7)/8;length++)
        {
            var truncated=expected[..length];var cursor=start;
            Assert.Throws<InvalidDataException>(()=>EdServerObjectReplicationCodec.ReadDefaultBody(truncated,ref cursor,mask));Assert.Equal(start,cursor);
        }
    }
    [Theory]
    [InlineData(48)] // Secondary archive.
    [InlineData(95)] // Nonzero base sequence.
    [InlineData(105)] // Nonzero optional-property mask.
    public void UnsupportedPayloadNeverConsumesCursor(int alteredBit)
    {
        var bytes=new byte[14];var p=0;
        EdServerObjectReplicationCodec.WriteDefaultBody(bytes,ref p,new(0x200000002,0,1,1));
        bytes[alteredBit/8]|=(byte)(1<<(alteredBit%8));p=0;
        Assert.Throws<NotSupportedException>(()=>EdServerObjectReplicationCodec.ReadDefaultBody(bytes,ref p,1));Assert.Equal(0,p);
    }
    [Fact]
    public void DryRunAndWrongSchemaAreRejectedAtomically()
    {
        var bytes=new byte[14];var p=0;EdServerObjectReplicationCodec.WriteDefaultBody(bytes,ref p,new(0x200000002,0,1,1));
        p=0;Assert.Throws<InvalidDataException>(()=>EdServerObjectReplicationCodec.ReadDefaultBody(bytes,ref p,2));Assert.Equal(0,p);
        bytes[65/8]&=unchecked((byte)~(1<<(65%8)));
        Assert.Throws<InvalidDataException>(()=>EdServerObjectReplicationCodec.ReadDefaultBody(bytes,ref p,1));Assert.Equal(0,p);
    }
    [Fact]
    public void InvalidWritesLeaveBufferAndPositionUnchanged()
    {
        EdServerDefaultObjectState[] invalid=[new(1UL<<48,0,1,0),new(1,0,0,0),new(1,0,1024,0),new(1,0,1,-1),new(1,0,1,513)];
        foreach(var message in invalid)
        {
            var bytes=Enumerable.Repeat((byte)0xA5,100).ToArray();var before=bytes.ToArray();var p=3;
            Assert.Throws<ArgumentOutOfRangeException>(()=>EdServerObjectReplicationCodec.WriteDefaultBody(bytes,ref p,message));Assert.Equal(before,bytes);Assert.Equal(3,p);
        }
        var small=new byte[1];var cursor=0;
        Assert.Throws<InvalidDataException>(()=>EdServerObjectReplicationCodec.WriteDefaultBody(small,ref cursor,new(1,0,1,0)));Assert.Equal(0,cursor);Assert.Equal(new byte[1],small);
    }
}
