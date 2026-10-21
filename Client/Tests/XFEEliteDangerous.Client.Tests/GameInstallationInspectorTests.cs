using XFEEliteDangerous.Client;
using Xunit;

namespace XFEEliteDangerous.Client.Tests;

public sealed class GameInstallationInspectorTests
{
    [Fact]
    public async Task InspectorHashesActualFilesAndOnlyExportsSafeConfigurationEvidence()
    {
        using var fixture = new Fixture();
        var product = Path.Combine(fixture.Directory, "Products", "test-product"); Directory.CreateDirectory(product);
        await File.WriteAllTextAsync(Path.Combine(fixture.Directory, "EDLaunch.exe"), "launcher fixture");
        await File.WriteAllTextAsync(Path.Combine(product, "VersionInfo.txt"), """{"Version":"2026.09.03.332841","name":"Elite: Dangerous","executable":"EliteDangerous64.exe"}""");
        var pe = new byte[128]; pe[0] = (byte)'M'; pe[1] = (byte)'Z'; pe[60] = 64;
        pe[64] = (byte)'P'; pe[65] = (byte)'E'; pe[68] = 0x64; pe[69] = 0x86; pe[88] = 0x0b; pe[89] = 0x02;
        await File.WriteAllBytesAsync(Path.Combine(product, "EliteDangerous64.exe"), pe);
        await File.WriteAllBytesAsync(Path.Combine(product, "OodleDictionary4.bin"), [1, 2, 3]);
        await File.WriteAllTextAsync(Path.Combine(product, "AppConfig.xml"), """<AppConfig><Network URL="https://example.invalid/path?authToken=DO-NOT-EXPORT" Token="DO-NOT-EXPORT" /><Other Password="DO-NOT-EXPORT" /></AppConfig>""");
        var snapshot = await new GameInstallationInspector().InspectAsync(fixture.Directory);
        var result = Assert.Single(snapshot.Products);
        Assert.True(snapshot.LauncherPresent); Assert.Equal("2026.09.03.332841", result.Version);
        Assert.Equal("AMD64", result.Pe?.Machine); Assert.Equal("PE32+", result.Pe?.Format); Assert.True(result.Pe?.Valid);
        Assert.Matches("^[a-f0-9]{64}$", result.ExecutableEvidence!.Sha256);
        Assert.Equal("VersionInfo.txt", result.VersionInfoEvidence?.Name); Assert.Equal("AppConfig.xml", result.AppConfigEvidence?.Name);
        Assert.Single(result.Dictionaries); Assert.Equal("example.invalid", Assert.Single(result.ServiceHosts));
        var json = System.Text.Json.JsonSerializer.Serialize(snapshot, ClientJson.Options);
        Assert.DoesNotContain("DO-NOT-EXPORT", json); Assert.DoesNotContain("authToken", json);
    }

    [Fact]
    public async Task ExecutablePathTraversalIsNotRead()
    {
        using var language = AppText.UseLanguage("zh-CN");
        using var fixture = new Fixture();
        var product = Path.Combine(fixture.Directory, "Products", "test-product"); Directory.CreateDirectory(product);
        await File.WriteAllTextAsync(Path.Combine(product, "VersionInfo.txt"), """{"executable":"../../outside.exe"}""");
        var result = Assert.Single((await new GameInstallationInspector().InspectAsync(fixture.Directory)).Products);
        Assert.Null(result.Executable); Assert.Null(result.ExecutableEvidence);
        Assert.Contains(result.Warnings, w => w.Contains("越过产品目录"));
    }

    [Fact]
    public async Task BadVersionAndXmlAreReported()
    {
        using var fixture = new Fixture();
        var product = Path.Combine(fixture.Directory, "Products", "test-product"); Directory.CreateDirectory(product);
        await File.WriteAllTextAsync(Path.Combine(product, "VersionInfo.txt"), "broken");
        await File.WriteAllTextAsync(Path.Combine(product, "AppConfig.xml"), "<broken>");
        var result = Assert.Single((await new GameInstallationInspector().InspectAsync(fixture.Directory)).Products);
        Assert.Equal(2, result.Warnings.Count);
    }
}
