using System.Buffers.Binary;
using XFEEliteDangerous.NativeFormat.Replication;
using Xunit;

namespace XFEEliteDangerous.Client.Tests;

public sealed class EdServerClockMessageTests
{
    [Fact]
    public void NativeOrderIsNanosecondsThenSignedSecondsAndThreeTimeReply()
    {
        using var request=new EdServerCallEnvelope(0x200000002,true,104,[20,0x15,0xCD,0x5B,0x07,0xD2,0x04,0,0,0,0,0,0]);
        var t0=EdServerClockMessageCodec.ReadRequest(request);
        Assert.Equal(new(1234,123456789),t0);
        using var reply=EdServerClockMessageCodec.CreateReply(request.ObjectReference,t0,new(2,100),new(2,200));
        Assert.Equal(296,reply.PayloadBits);Assert.True(reply.Flag);Assert.Equal(request.ObjectReference,reply.ObjectReference);
        Assert.Equal((t0,new EdServerNetworkTime(2,100),new EdServerNetworkTime(2,200)),EdServerClockMessageCodec.ReadReply(reply));
        var bytes=reply.CopyPayload();Assert.Equal(21,bytes[0]);
        Assert.Equal(100,BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(13)));
        Assert.Equal(2,BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(17)));
    }
    [Theory]
    [InlineData(123456789L,10000000L,12L,345678900)]
    [InlineData(-123456789L,10000000L,-12L,-345678900)]
    [InlineData(0L,1000L,0L,0)]
    public void TimestampConversionScalesSignedUnits(long ticks,long frequency,long seconds,int nanos)
        =>Assert.Equal(new(seconds,nanos),EdServerNetworkTime.FromTimestamp(ticks,frequency));
    [Fact]public void ClockUtcUsesFileTimeEpochAndPreservesHundredNanosecondPrecision()
    {
        var unixEpoch=DateTimeOffset.FromUnixTimeSeconds(0);
        Assert.Equal(new(11644473600,0),EdServerNetworkTime.FromUtc(unixEpoch));
        Assert.Equal(new(11644473600,123456700),EdServerNetworkTime.FromUtc(unixEpoch.AddTicks(1234567)));
        Assert.Equal(EdServerNetworkTime.FromUtc(unixEpoch),EdServerNetworkTime.FromUtc(unixEpoch.ToOffset(TimeSpan.FromHours(8))));
    }
    [Fact]
    public void MalformedShapeAndNoncanonicalTimeAreRejected()
    {
        using var noFlag=new EdServerCallEnvelope(2,false,104,new byte[13]);
        Assert.Throws<InvalidDataException>(()=>EdServerClockMessageCodec.ReadRequest(noFlag));
        var bytes=new byte[13];bytes[0]=20;BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(1),1_000_000_000);
        using var invalid=new EdServerCallEnvelope(2,true,104,bytes);
        Assert.Throws<InvalidDataException>(()=>EdServerClockMessageCodec.ReadRequest(invalid));
        Assert.Throws<ArgumentException>(()=>new EdServerNetworkTime(1,-1));
        Assert.Throws<ArgumentException>(()=>new EdServerNetworkTime(-1,1));
        Assert.Throws<ArgumentOutOfRangeException>(()=>EdServerNetworkTime.FromTimestamp(1,0));
    }
}
