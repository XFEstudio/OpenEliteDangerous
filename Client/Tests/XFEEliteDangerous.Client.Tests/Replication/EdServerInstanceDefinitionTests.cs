using System.Buffers.Binary;
using XFEEliteDangerous.NativeFormat.Replication;
using Xunit;

namespace XFEEliteDangerous.Client.Tests;

public sealed class EdServerInstanceDefinitionTests
{
    [Fact]public void ReplyCorrelatesTargetAndUsesTheNativeEmptyListCount()
    {
        var bytes=new byte[18];bytes[0]=17;BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(1),0x200002002);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(9),10477373803);
        var request=EdServerInstanceDefinitionMessageCodec.ReadRequest(bytes,144,true);
        Assert.Equal(10477373803UL,request.System);
        using var reply=EdServerInstanceDefinitionMessageCodec.CreateEmptyReply(99,request.Target);var data=reply.CopyPayload();
        Assert.Equal(77,reply.PayloadBits);Assert.False(reply.Flag);Assert.Equal(18,data[0]);
        Assert.Equal(request.Target,BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(1)));Assert.Equal(0,data[9]);
        bytes[17]=2;Assert.Throws<InvalidDataException>(()=>EdServerInstanceDefinitionMessageCodec.ReadRequest(bytes,144,true));
        Assert.Throws<InvalidDataException>(()=>EdServerInstanceDefinitionMessageCodec.ReadRequest(bytes,137,false));
    }
}
