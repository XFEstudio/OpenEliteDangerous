using XFEEliteDangerous.NativeFormat.Replication;
using Xunit;

namespace XFEEliteDangerous.Client.Tests;

public sealed class EdServerStellarForgeMessageTests
{
    [Fact]
    public void NativeByteLayoutRetainsAllSystemAddressBitsAndUsesEmptyNarrowString()
    {
        using var request = new EdServerCallEnvelope(0x200000002, true, 72, Convert.FromHexString("420807060504030201"));
        Assert.Equal(0x0102030405060708UL, EdServerStellarForgeMessageCodec.ReadRequest(request));
        using var forwarded = new EdServerCallEnvelope(request.ObjectReference, false, 72, request.CopyPayload());
        Assert.Equal(0x0102030405060708UL, EdServerStellarForgeMessageCodec.ReadRequest(forwarded));
        using var reply = EdServerStellarForgeMessageCodec.CreateNoOverridesReply(request.ObjectReference, 0x0102030405060708UL);
        Assert.Equal(104, reply.PayloadBits);
        Assert.Equal("43080706050403020100000000", Convert.ToHexString(reply.CopyPayload()));
        Assert.Equal(0x0102030405060708UL, EdServerStellarForgeMessageCodec.ReadNoOverridesReply(reply));
    }

    [Fact]
    public void WrongReplyFlagMethodTrailingDataAndNonemptyOverridesAreNotSilentlyAccepted()
    {
        using var wrongFlag = new EdServerCallEnvelope(5, true, 104, Convert.FromHexString("43080706050403020100000000"));
        using var wrongMethod = new EdServerCallEnvelope(5, true, 72, new byte[9]);
        using var extra = new EdServerCallEnvelope(5, true, 80, Convert.FromHexString("42080706050403020100"));
        using var nonempty = new EdServerCallEnvelope(5, true, 104, Convert.FromHexString("43080706050403020101000000"));
        Assert.Throws<InvalidDataException>(() => EdServerStellarForgeMessageCodec.ReadNoOverridesReply(wrongFlag));
        Assert.Throws<InvalidDataException>(() => EdServerStellarForgeMessageCodec.ReadRequest(wrongMethod));
        Assert.Throws<InvalidDataException>(() => EdServerStellarForgeMessageCodec.ReadRequest(extra));
        Assert.Throws<InvalidDataException>(() => EdServerStellarForgeMessageCodec.ReadNoOverridesReply(nonempty));
    }
}
