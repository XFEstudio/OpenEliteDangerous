using System.Text.Json;
using Xunit;
using XFEEliteDangerous.Core;

namespace XFEEliteDangerous.Tests;

public sealed class SoloStarterWorkspaceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "xfe-solo-starter-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task StandalonePreparationDoesNotRequireAnExistingCommanderOrImportedDefinitions()
    {
        var (library, operations) = CreateModuleFixture();
        var directory = Path.Combine(root, "workspace");
        var id = await SoloStarterWorkspace.PrepareAsync(directory, library, operations);
        await using var state = new StateStore(directory);
        await state.Read(database =>
        {
            var account = Assert.Single(database.Accounts).Value;
            Assert.Equal(id, account.Id);
            Assert.True(account.NativeTutorial.HasChosenStarterPack);
            Assert.Equal(10477373803UL, account.NativeSave!.SystemMetadata!.Address);
            Assert.Equal(account.NativeSave.SystemMetadata.Address, account.NativeSave.Snapshot.Location.Address);
            Assert.Equal("SideWinder", Assert.Single(account.NativeSave.Snapshot.Vessels).Name);
            Assert.Equal(0, account.NativeArx.TotalBalance);
            account.NativeSave.ValidateCurrent(account);
            return true;
        });
        var savePath = Path.Combine(directory, "state.json");
        var before = await File.ReadAllBytesAsync(savePath);
        Assert.Equal(id, await SoloStarterWorkspace.PrepareAsync(directory, library, operations));
        Assert.Equal(before, await File.ReadAllBytesAsync(savePath));
        using var refs = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(directory, "native-resources", "manifest.json")));
        Assert.Equal(SoloStarterWorkspace.GameHash, refs.RootElement.GetProperty("gameSha256").GetString());
        Assert.Contains(refs.RootElement.GetProperty("documents").EnumerateArray(), item =>
            item.GetProperty("operation").GetString() == "/2.0/elite/powerplay2/powers/list");
        using var powers = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(directory, "native-resources", "starter-powerplay.json")));
        Assert.All(powers.RootElement.GetProperty("powers").EnumerateArray(), power =>
            Assert.NotEmpty(power.GetProperty("perks").EnumerateArray()));
    }

    [Fact]
    public async Task StandalonePreparationLeavesAnExistingUnmanagedWorkspaceIntact()
    {
        var (library, operations) = CreateModuleFixture();
        var directory = Path.Combine(root, "workspace");
        Directory.CreateDirectory(directory);
        var savePath = Path.Combine(directory, "state.json");
        const string previous = "{\"schema\":1,\"serverName\":\"Keep this workspace\"}";
        await File.WriteAllTextAsync(savePath, previous);
        await Assert.ThrowsAsync<InvalidDataException>(() => SoloStarterWorkspace.PrepareAsync(directory, library, operations));
        Assert.Equal(previous, await File.ReadAllTextAsync(savePath));
        Assert.False(Directory.Exists(Path.Combine(directory, "native-resources")));
    }

    private (string Library, string Operations) CreateModuleFixture()
    {
        Directory.CreateDirectory(root);
        var library = Path.Combine(root, "test-module.dll");
        var operations = Path.Combine(root, "operations.json");
        // Preparation copies and fingerprints the module; no native code is loaded in this test.
        File.WriteAllBytes(library, [1, 2, 3]);
        File.WriteAllText(operations, "[\"/2.0/elite/codex/discovery/summary\"]");
        return (library, operations);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
