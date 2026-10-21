using System.Buffers.Binary;
using XFEEliteDangerous.NativeFormat.Replication;
using Xunit;

namespace XFEEliteDangerous.Client.Tests;

public sealed class EdServerIslandMessageTests
{
    private static byte[] Minimum()
    {
        // Independent fixed zero-peer vector: method, correlation, root address,
        // six zero doubles, frame flag, zero descriptor, zero compressed peer count.
        var bytes=new byte[153];bytes[0]=2;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(1),0x12345678);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(5),10477373803);return bytes;
    }
    [Fact]public void ReadsThePinnedMinimumRequestAndChecksItsWholeBoundary()
    {
        var bytes=Minimum();var request=EdServerIslandMessageCodec.ReadSoloRequest(bytes,1224,true);
        Assert.Equal(0x12345678u,request.Correlation);Assert.Equal(10477373803UL,request.Address);Assert.Equal(0u,request.FlightMode);
        bytes[61]=1;Assert.True(EdServerIslandMessageCodec.ReadSoloRequest(bytes,1224,true).FrameFlag);
        bytes[61]|=2;Assert.Throws<InvalidDataException>(()=>EdServerIslandMessageCodec.ReadSoloRequest(bytes,1224,true));
    }
    [Fact]public void RejectsTruncatedNonfiniteAndUnsupportedPeerRequests()
    {
        var bytes=Minimum();
        Assert.Throws<InvalidDataException>(()=>EdServerIslandMessageCodec.ReadSoloRequest(bytes[..^1],1216,true));
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(13),BitConverter.DoubleToInt64Bits(double.NaN));
        Assert.Throws<InvalidDataException>(()=>EdServerIslandMessageCodec.ReadSoloRequest(bytes,1224,true));
        bytes=Minimum();bytes[152]=1;
        Assert.Throws<NotSupportedException>(()=>EdServerIslandMessageCodec.ReadSoloRequest(bytes,1224,true));
    }
    [Fact]public void AssignmentContainsOnlyItsTwoFreshSessionsAndNoRemoteMembers()
    {
        using var result=EdServerIslandMessageCodec.CreateSoloAssignment(99,0x12345678,101,102);var data=result.CopyPayload();
        Assert.False(result.Flag);Assert.Equal(343,result.PayloadBits);Assert.Equal(3,data[0]);
        Assert.Equal(0x12345678u,BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(1)));
        Assert.Equal(101UL,BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(5)));
        Assert.Equal(102UL,BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(13)));
        Assert.All(data[21..],value=>Assert.Equal(0,value));
        Assert.Throws<ArgumentException>(()=>EdServerIslandMessageCodec.CreateSoloAssignment(99,1,101,101));
        Assert.Throws<ArgumentException>(()=>EdServerIslandMessageCodec.CreateSoloAssignment(99,1,0,101));
    }
}
