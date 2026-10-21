using System.Globalization;
using XFEEliteDangerous.Client.Storage;
using Xunit;

namespace XFEEliteDangerous.Client.Tests;

public sealed class NativeLocationStringCodecTests
{
    [Fact]
    public void KnownEightFieldLiteralUsesConfirmedNativeFieldOrder()
    {
        // Independent literals follow the native +70/+78/+80 stores and
        // 0x86D8E0 argument order; no matrix/Euler conversion is assumed.
        var location = NativeLocationStringCodec.Parse("10477373803,1.25,-2.5,3E+10,0.125,-0.25,0.5,7");
        Assert.Equal(10477373803UL, location.Address);
        Assert.Equal(new(1.25, -2.5, 30_000_000_000), location.Position);
        Assert.Equal(new(0.125, -0.25, 0.5), location.RotationVector);
        Assert.True(location.FrameFlag);
        Assert.Equal("10477373803,1.25,-2.5,30000000000,0.125,-0.25,0.5,1", NativeLocationStringCodec.Format(location));
    }

    [Theory]
    [InlineData("0", 0UL)]
    [InlineData("18446744073709551615", ulong.MaxValue)]
    [InlineData("281474976710656", 281474976710656UL)]
    public void AddressIsFullUnsigned64BitWithoutInferredWorldBounds(string address, ulong expected)
    {
        var location = NativeLocationStringCodec.Parse($"{address},0,0,0,0,0,0,0");
        Assert.Equal(expected, location.Address);
        Assert.Equal($"{address},0,0,0,0,0,0,0", NativeLocationStringCodec.Format(location));
        Assert.False(location.FrameFlag);
    }

    [Theory]
    [InlineData("0", false)]
    [InlineData("1", true)]
    [InlineData("2", true)]
    [InlineData("2147483648", true)]
    [InlineData("4294967295", true)]
    public void NativeFlagIsUnsigned32BitAndCanonicalizesNonzero(string flag, bool expected)
    {
        var location = NativeLocationStringCodec.Parse($"1,0,0,0,0,0,0,{flag}");
        Assert.Equal(expected, location.FrameFlag);
        Assert.Equal($"1,0,0,0,0,0,0,{(expected ? 1 : 0)}", NativeLocationStringCodec.Format(location));
    }

    [Fact]
    public void RotationVectorRemainsOpaqueWithoutNormalization()
    {
        var location = NativeLocationStringCodec.Parse("1,0,0,0,360,-720,1024,1");
        Assert.Equal(new(360, -720, 1024), location.RotationVector);
        Assert.Equal("1,0,0,0,360,-720,1024,1", NativeLocationStringCodec.Format(location));
    }

    [Theory]
    [InlineData("fr-FR")]
    [InlineData("de-DE")]
    [InlineData("ar-SA")]
    public void ParseAndFormatRemainInvariantUnderCommaAndNonLatinCultures(string name)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
            var location = NativeLocationStringCodec.Parse("42,1.25,-0.5,1E-06,2.5,3.75,4.125,0");
            Assert.Equal(new(1.25, -0.5, 0.000001), location.Position);
            Assert.Equal("42,1.25,-0.5,1E-06,2.5,3.75,4.125,0", NativeLocationStringCodec.Format(location));
            Assert.False(NativeLocationStringCodec.TryParse("42,1,25,-0,5,1E-06,2,5,3,75,4,125,0", out _));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void RoundTripFormattingPreservesExtremeSubnormalAndNegativeZeroBits()
    {
        double[] components = [double.MaxValue, double.MinValue, double.Epsilon, -double.Epsilon,
            BitConverter.Int64BitsToDouble(long.MinValue), 1.0000000000000002];
        var value = new NativeLocationStringValue(ulong.MaxValue,
            new(components[0], components[1], components[2]), new(components[3], components[4], components[5]), true);
        var actual = NativeLocationStringCodec.Parse(NativeLocationStringCodec.Format(value));
        double[] actualComponents = [actual.Position.X, actual.Position.Y, actual.Position.Z,
            actual.RotationVector.X, actual.RotationVector.Y, actual.RotationVector.Z];
        Assert.Equal(components.Select(BitConverter.DoubleToInt64Bits), actualComponents.Select(BitConverter.DoubleToInt64Bits));
        Assert.Equal(ulong.MaxValue, actual.Address);
    }

    [Fact]
    public void DecimalExponentAndLeadingZeroNumericTokensAreSupported()
    {
        var location = NativeLocationStringCodec.Parse("00042,+1.25,.5,1.,-2E-3,3e+2,0004,0002");
        Assert.Equal(new(1.25, 0.5, 1), location.Position);
        Assert.Equal(new(-0.002, 300, 4), location.RotationVector);
        Assert.Equal(42UL, location.Address);
        Assert.True(location.FrameFlag);
    }

    [Fact]
    public void PackedThirteenTokenFormatUsesTheNativeAddressBitLayout()
    {
        // Independent native layout: mass 0; sector (1,2,3); system 5; body 6.
        const ulong address=(6UL<<55)|(5UL<<44)|(1UL<<30)|(2UL<<17)|(3UL<<3);
        var value=NativeLocationStringCodec.Parse("1, 2, 3, 0, 5, 6, 7, 8, 9, 10, 11, 12, 1");
        Assert.Equal(address,value.Address);Assert.Equal(new(7,8,9),value.Position);
        Assert.Equal(new(10,11,12),value.RotationVector);Assert.True(value.FrameFlag);
        Assert.Equal("1, 2, 3, 0, 5, 6, 7, 8, 9, 10, 11, 12, 1",NativeLocationStringCodec.FormatPacked(value));
    }

    [Theory]
    [InlineData(0UL)] [InlineData(10477373803UL)] [InlineData(ulong.MaxValue)]
    [InlineData(0x123456789abcdef0UL)] [InlineData(0x123456789abcdef1UL)]
    [InlineData(0x123456789abcdef2UL)] [InlineData(0x123456789abcdef4UL)]
    [InlineData(0x123456789abcdef5UL)] [InlineData(0x123456789abcdef6UL)]
    public void PackedLocationRetainsAllAddressBitsAndFiniteVectors(ulong address)
    {
        var value=new NativeLocationStringValue(address,new(1e13,-2,3),new(.1,-.2,.3),true);
        Assert.Equal(value,NativeLocationStringCodec.Parse(NativeLocationStringCodec.FormatPacked(value)));
    }

    [Theory]
    [InlineData("1,2,3,8,5,6,0,0,0,0,0,0,0")]
    [InlineData("1,2,3,4,5,6,0,0,0,0,0,0,0")]
    [InlineData("0,8192,0,0,5,6,0,0,0,0,0,0,0")]
    [InlineData("0,0,0,0,2048,6,0,0,0,0,0,0,0")]
    [InlineData("0,0,0,0,0,512,0,0,0,0,0,0,0")]
    [InlineData("0,0,0,0,0,0,NaN,0,0,0,0,0,0")]
    [InlineData("0,0,0,0,0,0,0,0,0,0,0,0,1junk")]
    public void PackedLocationRejectsOverlappingOverflowAndNonfiniteFields(string text)
        =>Assert.False(NativeLocationStringCodec.TryParse(text,out _));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1,2,3,4,5,6,7")]
    [InlineData("1,2,3,4,5,6,7,0,1")]
    [InlineData("1,0,0,0,0,0,,1")]
    [InlineData("1,0,0,0,0,0,0,1,")]
    [InlineData("1,0,0,0,0,0,0,1\0ignored")]
    [InlineData("1,0,0,0,0,0,0,1garbage")]
    [InlineData("1,0,0,0,0,0,0,true")]
    [InlineData("1,0,0,0,0,0,0,1 ")]
    [InlineData(" 1,0,0,0,0,0,0,1")]
    [InlineData("1, 0,0,0,0,0,0,1")]
    [InlineData("1,0,0,0,0,0,0,-1")]
    [InlineData("1,0,0,0,0,0,0,+1")]
    [InlineData("1,0,0,0,0,0,0,4294967296")]
    [InlineData("1,0,0,0,0,0,0,1.0")]
    [InlineData("1,0,0,0,0,0,0,١")]
    [InlineData("18446744073709551616,0,0,0,0,0,0,0")]
    [InlineData("-1,0,0,0,0,0,0,0")]
    [InlineData("+1,0,0,0,0,0,0,0")]
    [InlineData("0x10,0,0,0,0,0,0,0")]
    [InlineData("1,1_000,0,0,0,0,0,0")]
    [InlineData("1,0x10,0,0,0,0,0,0")]
    public void MalformedOverflowOrAmbiguousInputIsRejectedWithoutPartialResult(string? text)
    {
        Assert.False(NativeLocationStringCodec.TryParse(text, out var value));
        Assert.Equal(default, value);
        if (text is null) Assert.Throws<ArgumentNullException>(() => NativeLocationStringCodec.Parse(text!));
        else Assert.Throws<FormatException>(() => NativeLocationStringCodec.Parse(text));
    }

    public static IEnumerable<object[]> NonfiniteText()
    {
        foreach (var token in new[] { "NaN", "Infinity", "-Infinity", "1e309", "-1e309" })
            for (var component = 1; component <= 6; component++)
            {
                string[] tokens = ["1", "0", "0", "0", "0", "0", "0", "0"];
                tokens[component] = token;
                yield return [string.Join(',', tokens)];
            }
    }

    [Theory]
    [MemberData(nameof(NonfiniteText))]
    public void NonfiniteAndOverflowedDoublesAreRejectedAtEveryComponent(string text) =>
        Assert.False(NativeLocationStringCodec.TryParse(text, out _));

    public static IEnumerable<object[]> NonfiniteValues()
    {
        foreach (var nonfinite in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            for (var component = 0; component < 6; component++)
            {
                var values = new double[6];
                values[component] = nonfinite;
                yield return [new NativeLocationStringValue(1, new(values[0], values[1], values[2]),
                    new(values[3], values[4], values[5]), false)];
            }
    }

    [Theory]
    [MemberData(nameof(NonfiniteValues))]
    public void FormatterRejectsNonfiniteTypedValues(NativeLocationStringValue value) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeLocationStringCodec.Format(value));

    [Fact]
    public void WholeTextAndPerTokenLimitsBoundParsingWork()
    {
        Assert.False(NativeLocationStringCodec.TryParse(new string('0', NativeLocationStringCodec.MaximumTextLength + 1), out _));
        var longToken = new string('0', NativeLocationStringCodec.MaximumTokenLength + 1);
        Assert.False(NativeLocationStringCodec.TryParse($"{longToken},0,0,0,0,0,0,0", out _));
        Assert.False(NativeLocationStringCodec.TryParse($"1,{longToken},0,0,0,0,0,0", out _));
        var atTokenLimit = new string('0', NativeLocationStringCodec.MaximumTokenLength);
        Assert.True(NativeLocationStringCodec.TryParse($"{atTokenLimit},0,0,0,0,0,0,0", out var location));
        Assert.Equal(0UL, location.Address);
    }
}
