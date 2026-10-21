using XFEEliteDangerous.NativeFormat.Replication;
using Xunit;

namespace XFEEliteDangerous.Client.Tests;
public sealed class EdServerObjectUpdateTests
{
    [Fact]
    public void DefaultRepFrameAndBoundedEnvelopeUseTheSameWireFormat()
    {
        var bytes=new byte[32];var p=3;
        EdServerObjectReplicationCodec.WriteDefaultBody(bytes,ref p,new(0x200000002,64655,1,0));var end=p;p=3;
        using var update=EdServerObjectUpdateCodec.ReadBody(bytes,ref p);
        Assert.Equal(end,p);Assert.Equal(0x200000002UL,update.ObjectReference);Assert.False(update.SecondaryArchive);
        Assert.Equal(64655,update.ConstructionVersion);Assert.Equal(1,update.Sequence);Assert.Equal(10,update.PayloadBits);Assert.Equal(0,update.BaseSequence);
        var rewritten=new byte[32];var q=3;EdServerObjectUpdateCodec.WriteBody(rewritten,ref q,update);Assert.Equal(bytes,rewritten);Assert.Equal(end,q);
    }
    [Fact]
    public void DeltaEnvelopeIsOpaqueOwnedAndItsBaseDoesNotImplyApplication()
    {
        byte[] data=[5,0,1];using var update=new EdServerObjectUpdate(12,true,14,23,17,data);data[0]=99;
        Assert.Equal(5,update.BaseSequence);var bytes=new byte[32];var p=0;EdServerObjectUpdateCodec.WriteBody(bytes,ref p,update);p=0;
        using var read=EdServerObjectUpdateCodec.ReadBody(bytes,ref p);Assert.Equal(17,read.PayloadBits);Assert.True(read.SecondaryArchive);Assert.Equal(5,read.BaseSequence);
        read.Dispose();Assert.Throws<ObjectDisposedException>(()=>read.CopyPayload());
    }
    [Fact]
    public void TruncationCannotAdvanceTheCursorAndSequenceZeroIsRejected()
    {
        var bytes=new byte[8];var p=2;
        Assert.Throws<InvalidDataException>(()=>EdServerObjectUpdateCodec.ReadBody(bytes,ref p));Assert.Equal(2,p);
        Assert.Throws<ArgumentException>(()=>new EdServerObjectUpdate(2,false,14,0,10,[0,0]));
    }
}
