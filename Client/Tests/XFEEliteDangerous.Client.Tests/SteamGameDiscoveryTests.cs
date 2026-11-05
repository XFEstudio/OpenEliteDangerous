using Xunit;

namespace XFEEliteDangerous.Client.Tests;

public sealed class SteamGameDiscoveryTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("xfe-steam-test-").FullName;

    [Fact]
    public void ReadsModernAdditionalLibraryAndActualInstallName()
    {
        var steam = Path.Combine(root, "Steam");
        var library = Path.Combine(root, "Space 航行 Library");
        var executable = Install(library, "Elite Dangerous Custom");
        Write(Path.Combine(steam, "steamapps", "libraryfolders.vdf"),
            $$"""
            "libraryfolders"
            {
                // The app can be installed outside the default library.
                "0" { "path" "{{Escape(steam)}}" "apps" { } }
                "1" { "path" "{{Escape(library)}}" "apps" { "359320" "1" } }
                "2" { "path" "{{Escape(library)}}" }
            }
            """);
        Assert.Equal(executable, Assert.Single(SteamGameDiscovery.FindOdyssey([steam, library])));
    }

    [Fact]
    public void ReadsLegacyLibraryConfigurationAfterMalformedModernFile()
    {
        var steam = Path.Combine(root, "Steam");
        var library = Path.Combine(root, "Legacy Library");
        var executable = Install(library, "Elite Dangerous");
        Write(Path.Combine(steam, "steamapps", "libraryfolders.vdf"), "\"libraryfolders\" { \"0\" { \"path\"");
        Write(Path.Combine(steam, "config", "libraryfolders.vdf"), $"\"LibraryFolders\" {{ \"1\" \"{Escape(library)}\" }}");
        Assert.Equal(executable, Assert.Single(SteamGameDiscovery.FindOdyssey([steam])));
    }

    [Theory]
    [InlineData("..", "359320")]
    [InlineData("../Elsewhere", "359320")]
    [InlineData("Elite Dangerous", "999999")]
    public void RejectsAnInvalidInstallFolderOrAnUnrelatedApp(string directory, string id)
    {
        _ = Install(root, "Elite Dangerous");
        Write(Path.Combine(root, "steamapps", "appmanifest_359320.acf"), $"\"AppState\" {{ \"appid\" \"{id}\" \"installdir\" \"{directory}\" }}");
        Assert.Empty(SteamGameDiscovery.FindOdyssey([root]));
    }

    [Fact]
    public async Task AcceptsInstallProductAndExeSelectionsButRejectsAnUnreviewedImage()
    {
        var file = Install(root, "Elite Dangerous");
        var product = Path.GetDirectoryName(file)!;
        var install = Path.GetDirectoryName(Path.GetDirectoryName(product)!)!;
        Assert.Equal(file, OdysseyGame.ExecutableFromSelection(install));
        Assert.Equal(file, OdysseyGame.ExecutableFromSelection(product));
        Assert.Equal(file, OdysseyGame.ExecutableFromSelection(file));
        Assert.Throws<InvalidDataException>(() => OdysseyGame.ExecutableFromSelection(Path.Combine(product, "Other.exe")));
        await Assert.ThrowsAsync<InvalidDataException>(() => VerifiedOdysseyImage.OpenAsync(file));
        // A rejected image also releases its file lease.
        using var unlocked = File.Open(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    private static string Install(string library, string folder)
    {
        var apps = Path.Combine(library, "steamapps");
        Write(Path.Combine(apps, "appmanifest_359320.acf"), $"\"AppState\" {{ \"appid\" \"359320\" \"installdir\" \"{folder}\" }}");
        var file = Path.Combine(apps, "common", folder, "Products", OdysseyGame.Product, OdysseyGame.ExecutableName);
        Write(file, "stub, not a supported executable");
        return file;
    }

    private static string Escape(string text) => text.Replace("\\", "\\\\", StringComparison.Ordinal);
    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
