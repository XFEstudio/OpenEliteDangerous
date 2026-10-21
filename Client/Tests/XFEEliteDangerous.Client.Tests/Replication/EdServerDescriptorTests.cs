using System.Text.Json;
using XFEEliteDangerous.NativeFormat.Replication;
using Xunit;

namespace XFEEliteDangerous.Client.Tests;

public sealed class EdServerDescriptorTests
{
    [Theory]
    [InlineData("IP4:127.0.0.1:5100,1264", 0x7F000001U, 5100, 1264)]
    [InlineData("IP4:255.254.253.252:65535,65535", 0xFFFEFDFCU, 65535, 65535)]
    [InlineData("IP4:0.0.0.0:0,0", 0U, 0, 0)]
    [InlineData("IP4:127.000.00.001:05100,01264", 0x7F000001U, 5100, 1264)]
    public void Ip4GrammarPreservesAddressPortAndMtu(string text, uint address, int port, int mtu)
    {
        var endpoint = EdServerIp4Endpoint.Parse(text);
        Assert.Equal(address, endpoint.Address);
        Assert.Equal(port, endpoint.Port);
        Assert.Equal(mtu, endpoint.Mtu);
        Assert.Equal(endpoint, EdServerIp4Endpoint.Parse(endpoint.ToString()));
        Assert.Equal(port, endpoint.ToIPEndPoint().Port);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("127.0.0.1:5100,1264")]
    [InlineData("IP6:127.0.0.1:5100,1264")]
    [InlineData("ip4:127.0.0.1:5100,1264")]
    [InlineData("IP4:256.0.0.1:5100,1264")]
    [InlineData("IP4:127.0.0.1:65536,1264")]
    [InlineData("IP4:127.0.0.1:5100,65536")]
    [InlineData("IP4:127.0.0.1:5100")]
    [InlineData("IP4:127.0.0.1:5100,1264,0")]
    [InlineData("IP4:127.0.0.1:5100,1264 ")]
    [InlineData("IP4:127.0.0.1:-1,1264")]
    [InlineData("IP4:127.0.0.1:5100,+1264")]
    [InlineData("IP4:127.0..1:5100,1264")]
    [InlineData("IP4:localhost:5100,1264")]
    public void InvalidOrUnverifiedEndpointGrammarIsRejected(string? text) =>
        Assert.False(EdServerIp4Endpoint.TryParse(text, out _));

    [Fact]
    public void DescriptorUsesStringRunIdHexBytesAndOptionalId()
    {
        using var json = JsonDocument.Parse("""
            {"runId":"18446744073709551615","connectionDetails":"IP4:127.0.0.1:5100,1264","encryptionKey":"0001abCDff","id":23}
            """);
        var record = EdServerDescriptor.Parse(json.RootElement);
        Assert.Equal(ulong.MaxValue, record.RunId);
        Assert.Equal(23UL, record.Id);
        Assert.Equal(new byte[] { 0, 1, 0xAB, 0xCD, 0xFF }, record.EncryptionKey.ToArray());
        Assert.DoesNotContain("abCD", record.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("\"42\"", 42UL)]
    [InlineData("42", 42UL)]
    [InlineData("0", 0UL)]
    public void DescriptorSupportsVerifiedIdAlternatives(string id, ulong expected)
    {
        using var json = JsonDocument.Parse("{\"runId\":\"1\",\"connectionDetails\":\"IP4:127.0.0.1:5100,1264\",\"encryptionKey\":\"\",\"id\":" + id + "}");
        Assert.Equal(expected, EdServerDescriptor.Parse(json.RootElement).Id);
    }

    [Theory]
    [InlineData("{\"runId\":1,\"connectionDetails\":\"IP4:127.0.0.1:5100,1264\",\"encryptionKey\":\"00\"}")]
    [InlineData("{\"runId\":\"-1\",\"connectionDetails\":\"IP4:127.0.0.1:5100,1264\",\"encryptionKey\":\"00\"}")]
    [InlineData("{\"runId\":\"1\",\"connectionDetails\":\"IP4:127.0.0.1:5100,1264\"}")]
    [InlineData("{\"runId\":\"1\",\"connectionDetails\":\"IP4:127.0.0.1:5100,1264\",\"encryptionKey\":\"00\",\"id\":-1}")]
    [InlineData("{\"runId\":\"1\",\"connectionDetails\":\"IP4:127.0.0.1:5100,1264\",\"encryptionKey\":\"00\",\"id\":1.5}")]
    public void InvalidDescriptorsAreRejected(string text)
    {
        using var json = JsonDocument.Parse(text);
        Assert.Throws<FormatException>(() => EdServerDescriptor.Parse(json.RootElement));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0x01")]
    [InlineData("zz")]
    [InlineData("AA BB")]
    [InlineData("AA\0BB")]
    [InlineData("ＡＡ")]
    public void InvalidHexKeysAreRejected(string text) =>
        Assert.Throws<FormatException>(() => EdServerDescriptor.DecodeEncryptionKey(text));

    [Fact]
    public void KeyDecoderBoundsAllocation() => Assert.Throws<FormatException>(() =>
        EdServerDescriptor.DecodeEncryptionKey(new string('0', EdServerDescriptor.MaximumEncryptionKeyBytes * 2 + 2)));
}
