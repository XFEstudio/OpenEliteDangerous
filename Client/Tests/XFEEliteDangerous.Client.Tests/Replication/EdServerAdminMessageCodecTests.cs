using System.Text;
using System.Text.Json;
using XFEEliteDangerous.NativeFormat.Replication;
using Xunit;

namespace XFEEliteDangerous.Client.Tests;

public sealed class EdServerAdminMessageCodecTests
{
    // Independent literal construction: UInt32LE byte counts2,2,0,1;
    // bytes41/00,80/FF,empty,7F; then one true bit. The offset5 literal
    // is the same169-bit integer shifted left5, not output from this codec.
    private const string StartLiteral = "0200000041000200000080FF00000000010000007F01";

    [Theory]
    [InlineData(0, StartLiteral)]
    [InlineData(5, "4000000020084000000000F01F00000020000000E02F")]
    public void StartBodyMatchesIndependentBinaryLiteralsIncludingUnalignedNulAndNonUtf8(int start, string hex)
    {
        var source = Convert.FromHexString(hex);
        var p = start;
        using var actual = EdServerAdminMessageCodec.ReadStartBody(source, ref p);
        Assert.Equal(start + 169, p);
        Assert.Equal(new byte[] { 0x41, 0 }, actual.CopyMachineIdBytes());
        Assert.Equal(new byte[] { 0x80, 0xFF }, actual.CopyMachineTokenBytes());
        Assert.Empty(actual.CopyAuthTokenBytes());
        Assert.Equal(new byte[] { 0x7F }, actual.CopyNeutralAlternateAuthenticationBytes());
        Assert.True(actual.Missions);
        Assert.Equal(169, EdServerAdminMessageCodec.GetStartBodyBitLength(actual));
        var encoded = new byte[source.Length]; var writerPosition = start;
        EdServerAdminMessageCodec.WriteStartBody(encoded, ref writerPosition, actual);
        Assert.Equal(p, writerPosition);
        Assert.Equal(source, encoded);
    }

    [Fact]
    public void StartWriterPreservesPrefixAndSuffixBits()
    {
        using var message = Sample();
        var bytes = Enumerable.Repeat((byte)0xA5, 22).ToArray(); var p = 5;
        EdServerAdminMessageCodec.WriteStartBody(bytes, ref p, message);
        Assert.Equal(174, p);
        Assert.Equal(Convert.FromHexString("4500000020084000000000F01F00000020000000E0AF"), bytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FourEmptyCredentialsRemainValidBodyDataWithoutClaimingAuthentication(bool missions)
    {
        using var message = new EdServerStartServerAdmin([], [], [], [], missions);
        var bytes = new byte[17]; var p = 0;
        EdServerAdminMessageCodec.WriteStartBody(bytes, ref p, message, maximumCredentialBytes: 0, maximumBodyBytes: 17);
        Assert.Equal(129, p);
        Assert.All(bytes.AsSpan(0, 16).ToArray(), value => Assert.Equal((byte)0, value));
        Assert.Equal(missions ? (byte)1 : (byte)0, bytes[16]);
        p = 0;
        using var actual = EdServerAdminMessageCodec.ReadStartBody(bytes, ref p, maximumCredentialBytes: 0, maximumBodyBytes: 17);
        Assert.Equal(missions, actual.Missions);
        Assert.Equal(0, actual.MachineIdLength + actual.MachineTokenLength + actual.AuthTokenLength + actual.NeutralAlternateAuthenticationBytesLength);
    }

    [Fact]
    public void DefaultCredentialAndCombinedBodyCeilingsAreInclusiveLocalPolicy()
    {
        var field = Enumerable.Repeat((byte)0x81, EdServerAdminMessageCodec.DefaultMaximumCredentialBytes).ToArray();
        using var message = new EdServerStartServerAdmin(field, field, field, field, false);
        var bits = EdServerAdminMessageCodec.GetStartBodyBitLength(message);
        var bytes = new byte[(bits + 7) / 8]; var p = 0;
        Assert.Equal(EdServerAdminMessageCodec.DefaultMaximumBodyBytes, bytes.Length);
        EdServerAdminMessageCodec.WriteStartBody(bytes, ref p, message);
        p = 0;
        using var actual = EdServerAdminMessageCodec.ReadStartBody(bytes, ref p);
        Assert.Equal(bits, p);
        Assert.Equal(field, actual.CopyAuthTokenBytes());
    }

    [Fact]
    public void ExplicitLargerLocalBudgetDoesNotPretendCapacity2Ee0IsANativeMaximum()
    {
        var field = new byte[5000];
        using var message = new EdServerStartServerAdmin(field, field, field, field, false);
        Assert.Throws<ArgumentOutOfRangeException>(() => EdServerAdminMessageCodec.GetStartBodyBitLength(message));
        var bits = EdServerAdminMessageCodec.GetStartBodyBitLength(message, 5000, 20017);
        Assert.Equal(160129, bits);
        var bytes = new byte[(bits + 7) / 8]; var p = 0;
        EdServerAdminMessageCodec.WriteStartBody(bytes, ref p, message, 5000, 20017);
        p = 0;
        using var actual = EdServerAdminMessageCodec.ReadStartBody(bytes, ref p, 5000, 20017);
        Assert.Equal(5000, actual.NeutralAlternateAuthenticationBytesLength);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void EveryByteTruncationOfTheIndependentStartVectorLeavesCursorUnchanged(int start)
    {
        var source = Convert.FromHexString(start == 0 ? StartLiteral : "4000000020084000000000F01F00000020000000E02F");
        for (var length = 0; length < source.Length; length++)
        {
            var p = start; var truncated = source.AsSpan(0, length).ToArray();
            Assert.Throws<InvalidDataException>(() => EdServerAdminMessageCodec.ReadStartBody(truncated, ref p));
            Assert.Equal(start, p);
        }
    }

    [Fact]
    public void HugeDeclaredCredentialAndCombinedBudgetViolationAreRejectedTransactionally()
    {
        var p = 0;
        Assert.Throws<InvalidDataException>(() => EdServerAdminMessageCodec.ReadStartBody(Convert.FromHexString("FFFFFFFF"), ref p));
        Assert.Equal(0, p);
        var bytes = Convert.FromHexString(StartLiteral);
        Assert.Throws<InvalidDataException>(() => EdServerAdminMessageCodec.ReadStartBody(bytes, ref p, maximumCredentialBytes: 1));
        Assert.Equal(0, p);
        Assert.Throws<InvalidDataException>(() => EdServerAdminMessageCodec.ReadStartBody(bytes, ref p, maximumBodyBytes: 21));
        Assert.Equal(0, p);
    }

    [Fact]
    public void FailedStartWritesValidateAllFieldsAndOutputCapacityBeforeMutation()
    {
        using var message = Sample();
        var bytes = Enumerable.Repeat((byte)0xA5, 32).ToArray(); var before = bytes.ToArray(); var p = 3;
        Assert.Throws<ArgumentOutOfRangeException>(() => EdServerAdminMessageCodec.WriteStartBody(bytes, ref p, message, maximumCredentialBytes: 1));
        Assert.Equal(3, p); Assert.Equal(before, bytes);
        Assert.Throws<ArgumentOutOfRangeException>(() => EdServerAdminMessageCodec.WriteStartBody(bytes, ref p, message, maximumBodyBytes: 21));
        Assert.Equal(3, p); Assert.Equal(before, bytes);
        var tooSmall = bytes.AsSpan(0, 20).ToArray(); var smallBefore = tooSmall.ToArray();
        Assert.Throws<InvalidDataException>(() => EdServerAdminMessageCodec.WriteStartBody(tooSmall, ref p, message));
        Assert.Equal(3, p); Assert.Equal(smallBefore, tooSmall);
    }

    [Theory]
    [InlineData(-1, 16401)]
    [InlineData(65537, 16401)]
    [InlineData(4096, 16)]
    [InlineData(4096, 262162)]
    public void InvalidLocalBoundsRejectBeforeReadingOrWriting(int maximumCredentialBytes, int maximumBodyBytes)
    {
        using var message = Sample(); var bytes = new byte[32]; var before = bytes.ToArray(); var p = 0;
        Assert.Throws<ArgumentOutOfRangeException>(() => EdServerAdminMessageCodec.ReadStartBody(bytes, ref p, maximumCredentialBytes, maximumBodyBytes));
        Assert.Equal(0, p);
        Assert.Throws<ArgumentOutOfRangeException>(() => EdServerAdminMessageCodec.WriteStartBody(bytes, ref p, message, maximumCredentialBytes, maximumBodyBytes));
        Assert.Equal(0, p); Assert.Equal(before, bytes);
    }

    [Fact]
    public void CredentialOwnerCopiesInputsAndExplicitOutputCopies()
    {
        var source = new byte[] { 1, 2, 3 };
        using var message = new EdServerStartServerAdmin(source, source, source, source, false);
        source[0] = 99;
        var returned = message.CopyAuthTokenBytes(); returned[1] = 98;
        Assert.Equal(new byte[] { 1, 2, 3 }, message.CopyMachineIdBytes());
        Assert.Equal(new byte[] { 1, 2, 3 }, message.CopyAuthTokenBytes());
    }

    [Fact]
    public void LoggingAndJsonDoNotExposeCredentialValuesAndDisposedOwnerCannotBeEncoded()
    {
        var credential = Encoding.ASCII.GetBytes("synthetic-secret-ABCD");
        var message = new EdServerStartServerAdmin(credential, credential, credential, credential, true);
        Assert.DoesNotContain("synthetic-secret", message.ToString());
        var serialized = JsonSerializer.Serialize(message);
        Assert.DoesNotContain("synthetic-secret", serialized);
        Assert.DoesNotContain(Convert.ToBase64String(credential), serialized);
        using var metadata = JsonDocument.Parse(serialized);
        Assert.Equal(credential.Length, metadata.RootElement.GetProperty("authTokenLength").GetInt32());
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize<EdServerStartServerAdmin>(serialized));
        message.Dispose(); message.Dispose();
        Assert.DoesNotContain("synthetic-secret", message.ToString());
        Assert.Throws<ObjectDisposedException>(() => message.CopyMachineTokenBytes());
        var p = 0; var target = Enumerable.Repeat((byte)0xAA, 128).ToArray(); var before = target.ToArray();
        Assert.Throws<ObjectDisposedException>(() => EdServerAdminMessageCodec.WriteStartBody(target, ref p, message));
        Assert.Equal(0, p); Assert.Equal(before, target);
    }

    [Fact]
    public void ConstructorRejectsAFieldAboveTheHardLocalAllocationCeiling()
    {
        var bytes = new byte[EdServerAdminMessageCodec.HardMaximumCredentialBytes + 1];
        Assert.Throws<ArgumentOutOfRangeException>(() => new EdServerStartServerAdmin(bytes, [], [], [], false));
    }

    [Theory]
    [InlineData(0, "112233445566778801")]
    [InlineData(3, "88109921AA32BB430C")]
    public void ReadyBodyMatchesIndependentU64AndBooleanGoldenLiterals(int start, string hex)
    {
        var source = Convert.FromHexString(hex); var p = start;
        var actual = EdServerAdminMessageCodec.ReadReadyBody(source, ref p);
        Assert.Equal(0x8877665544332211UL, actual.SessionId); Assert.True(actual.Missions);
        Assert.Equal(start + 65, p);
        var target = new byte[source.Length]; var writePosition = start;
        EdServerAdminMessageCodec.WriteReadyBody(target, ref writePosition, actual);
        Assert.Equal(p, writePosition); Assert.Equal(source, target);
    }

    [Fact]
    public void ReadyWriterPreservesSurroundingBitsAndDoesNotTruncateToU48()
    {
        var target = Enumerable.Repeat((byte)0xA5, 9).ToArray(); var p = 3;
        EdServerAdminMessageCodec.WriteReadyBody(target, ref p, new(0x8877665544332211, true));
        Assert.Equal(Convert.FromHexString("8D109921AA32BB43AC"), target);
        foreach (var id in new[] { 0UL, 1UL << 48, ulong.MaxValue })
        {
            var bytes = new byte[9]; p = 0;
            EdServerAdminMessageCodec.WriteReadyBody(bytes, ref p, new(id, false));
            p = 0; var actual = EdServerAdminMessageCodec.ReadReadyBody(bytes, ref p);
            Assert.Equal(id, actual.SessionId); Assert.False(actual.Missions);
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(int.MaxValue)]
    public void InvalidOrTruncatedReadyReadAndWriteDoNotCommit(int start)
    {
        var bytes = Enumerable.Repeat((byte)0xA5, 8).ToArray(); var before = bytes.ToArray(); var p = start;
        Assert.Throws<InvalidDataException>(() => EdServerAdminMessageCodec.ReadReadyBody(bytes, ref p));
        Assert.Equal(start, p);
        Assert.Throws<InvalidDataException>(() => EdServerAdminMessageCodec.WriteReadyBody(bytes, ref p, new(1, false)));
        Assert.Equal(start, p); Assert.Equal(before, bytes);
    }

    [Fact]
    public void BodyOnlyApisNeverPrependTypeSequenceOrInterpretReadyAsWorldState()
    {
        Assert.Equal((byte)26, (byte)EdServerAdminLetter.StartServerAdmin);
        Assert.Equal((byte)27, (byte)EdServerAdminLetter.ServerAdminReady);
        using var empty = new EdServerStartServerAdmin([], [], [], [], false);
        var bytes = new byte[17]; var p = 0;
        EdServerAdminMessageCodec.WriteStartBody(bytes, ref p, empty);
        Assert.Equal(129, p); Assert.All(bytes, item => Assert.Equal((byte)0, item));
        Assert.DoesNotContain("123456789", new EdServerServerAdminReady(123456789, false).ToString());
    }

    private static EdServerStartServerAdmin Sample()
        => new(new byte[] { 0x41, 0 }, new byte[] { 0x80, 0xFF }, [], new byte[] { 0x7F }, true);
}
