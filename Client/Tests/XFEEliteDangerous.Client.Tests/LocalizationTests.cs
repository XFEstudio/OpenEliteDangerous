using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace XFEEliteDangerous.Client.Tests;

[CollectionDefinition("Process language", DisableParallelization = true)]
public sealed class ProcessLanguageCollection;

[Collection("Process language")]
public sealed class LocalizationTests
{
    [Fact]
    public void CataloguesHaveIdenticalKeysAndCompatibleFormats()
    {
        var en = AppText.Catalog("en-US"); var zh = AppText.Catalog("zh-CN");
        Assert.True(en.Count >= 463);
        Assert.Equal(en.Keys.Order(), zh.Keys.Order());
        foreach (var key in en.Keys)
        {
            Assert.False(string.IsNullOrWhiteSpace(en[key]), key);
            Assert.False(string.IsNullOrWhiteSpace(zh[key]), key);
            Assert.DoesNotMatch(@"\p{IsCJKUnifiedIdeographs}", en[key]);
            var a = CompositeFormat.Parse(en[key]); var b = CompositeFormat.Parse(zh[key]);
            Assert.Equal(a.MinimumArgumentCount, b.MinimumArgumentCount);
            Assert.Equal(Placeholders(en[key]), Placeholders(zh[key]));
            var values = Enumerable.Repeat<object>(1234, a.MinimumArgumentCount).ToArray();
            _ = string.Format(CultureInfo.GetCultureInfo("en-US"), a, values);
            _ = string.Format(CultureInfo.GetCultureInfo("zh-CN"), b, values);
        }
        static string[] Placeholders(string text) => Regex.Matches(text, @"\{\d+(?:,-?\d+)?(?::[^}]+)?\}").Select(x => x.Value).Order().ToArray();
    }

    [Theory]
    [InlineData("zh-CN", "未登录")]
    [InlineData("en-US", "Signed out")]
    [InlineData("fr-FR, en-US;q=0.9, zh-CN;q=0.3", "Signed out")]
    [InlineData("en-US;q=0, zh-CN;q=0.8", "未登录")]
    [InlineData("zh-CN;q=invalid, en-US", "Signed out")]
    [InlineData("zh-CN;q=2", "Signed out")]
    [InlineData("de-DE", "Signed out")]
    [InlineData("*", "Signed out")]
    public void RequestsSelectLanguageWithoutChangingWireCulture(string header, string expected)
    {
        var numeric = CultureInfo.CurrentCulture; var defaultUi = CultureInfo.DefaultThreadCurrentUICulture;
        var prior = AppText.Language;
        using (AppText.UseLanguage(header))
        {
            Assert.Equal(expected, AppText.Get("AppShellPage.Text105"));
            Assert.Same(numeric, CultureInfo.CurrentCulture);
            Assert.Same(defaultUi, CultureInfo.DefaultThreadCurrentUICulture);
        }
        Assert.Equal(prior, AppText.Language);
    }

    [Fact]
    public async Task ConcurrentRequestsAndNestedScopesDoNotLeakCulture()
    {
        var before = AppText.Language;
        async Task<string> Request(string language)
        {
            using var scope = AppText.UseLanguage(language);
            await Task.Yield();
            var selected = AppText.Language;
            using (AppText.UseLanguage(language == "zh-CN" ? "en-US" : "zh-CN"))
            { await Task.Yield(); Assert.NotEqual(selected, AppText.Language); }
            Assert.Equal(selected, AppText.Language);
            return AppText.Get("AppShellPage.Text105");
        }
        var values = await Task.WhenAll(Enumerable.Range(0, 40).Select(i => Request(i % 2 == 0 ? "zh-CN" : "en-US")));
        for (var i = 0; i < values.Length; i++) Assert.Equal(i % 2 == 0 ? "未登录" : "Signed out", values[i]);
        Assert.Equal(before, AppText.Language);
    }

    [Theory]
    [InlineData("en", "en-US")]
    [InlineData("zh-Hans", "zh-CN")]
    [InlineData("system", "system")]
    [InlineData("EN-us", "en-US")]
    public void PreferencesNormalizeToStableIdentifiers(string input, string expected) => Assert.Equal(expected, AppText.NormalizePreference(input));

    [Fact]
    public void CliLanguageOptionsAreRemovedWithoutChangingProtocolArguments()
    {
        var before = AppText.Language; var numeric = CultureInfo.CurrentCulture;
        try
        {
            Assert.Equal(new[] { "--inspect", "--game", "E:\\Game" }, AppText.ConfigureArguments(["--language", "en-US", "--inspect", "--game", "E:\\Game"]));
            Assert.Equal("Signed out", AppText.Get("AppShellPage.Text105"));
            Assert.Equal(new[] { "--status" }, AppText.ConfigureArguments(["--status", "--language=zh-CN"]));
            Assert.Equal("未登录", AppText.Get("AppShellPage.Text105"));
            Assert.Same(numeric, CultureInfo.CurrentCulture);
            Assert.Throws<ArgumentException>(() => AppText.ConfigureArguments(["--language"]));
            Assert.Throws<ArgumentException>(() => AppText.ConfigureArguments(["--language=de-DE"]));
            Assert.Throws<ArgumentException>(() => AppText.ConfigureArguments(["--language=en-US", "--language=zh-CN"]));
            Assert.Throws<KeyNotFoundException>(() => AppText.Get("unknown.key"));
        }
        finally { AppText.Apply(before); }
    }
}
