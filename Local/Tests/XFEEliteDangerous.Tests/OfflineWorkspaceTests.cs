using System.Text.Json;
using System.Text.Json.Nodes;
using XFEEliteDangerous.Core;
using Xunit;

namespace XFEEliteDangerous.Tests;

public sealed class OfflineWorkspaceTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "XFEEliteDangerous-offline-tests-" + Guid.NewGuid().ToString("N"));
    private OfflineWorkspace data = null!;
    private const string Password = "OfflineFixture!2026";
    public Task InitializeAsync() { data = new OfflineWorkspace(directory); return Task.CompletedTask; }
    public async Task DisposeAsync() { await data.DisposeAsync(); Directory.Delete(directory, true); }
    private async Task<(string Token, string Id)> User(string email = "owner@example.invalid", string name = "Owner")
    {
        await data.RegisterAsync(email, name, Password);
        var result = await data.LoginAsync(email, Password);
        return (result["token"]!.GetValue<string>(), result["account"]!["id"]!.GetValue<string>());
    }
    private static JsonObject Change(long value) => new() { ["delta"] = value };
    private static string Version(JsonNode node) => node["version"]!.GetValue<string>();
    private static JsonObject PartialCommander() => new()
    {
        ["name"] = "Imported", ["credits"] = 400L,
        ["cargo"] = new JsonArray(new JsonObject { ["id"] = "fixturecargo", ["name"] = "Fixture cargo", ["quantity"] = 2, ["category"] = "cargo" }),
        ["materials"] = new JsonArray(new JsonObject { ["id"] = "fixturematerial", ["name"] = "Fixture material", ["quantity"] = 8, ["category"] = "Raw" }),
        ["ships"] = new JsonArray(new JsonObject
        {
            ["id"] = "1", ["shipType"] = "SideWinder", ["name"] = "Fixture", ["ident"] = "TEST", ["active"] = true,
            ["modules"] = new JsonArray(new JsonObject { ["slot"] = "MainEngines", ["itemId"] = "fixturemodule", ["priority"] = 2, ["enabled"] = true, ["health"] = 0.8, ["value"] = 100L })
        })
    };
    [Fact] public async Task FirstRegistrationIsAdministratorAndSubsequentRegistrationIsPlayer()
    {
        var owner = await User(); var player = await User("player@example.invalid", "Player");
        Assert.Equal("administrator", (await data.ProfileAsync(owner.Token))["role"]!.GetValue<string>());
        Assert.Equal("player", (await data.ProfileAsync(player.Token))["role"]!.GetValue<string>());
        Assert.Equal(2, (await data.AccountsAsync(owner.Token)).AsArray().Count);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => data.AccountsAsync(player.Token));
    }
    [Fact] public async Task ConcurrentFirstRegistrationsProduceExactlyOneAdministrator()
    {
        await Task.WhenAll(Task.Run(() => data.RegisterAsync("one@example.invalid", "One", Password)), Task.Run(() => data.RegisterAsync("two@example.invalid", "Two", Password)));
        var one = await data.LoginAsync("one@example.invalid", Password); var two = await data.LoginAsync("two@example.invalid", Password);
        Assert.Single(new[] { one, two }, row => row["account"]!["role"]!.GetValue<string>() == "administrator");
    }
    [Fact] public async Task PublicAccountsAndPersistedStateDoNotContainPlainPasswordsOrSessionTokens()
    {
        var owner = await User(); var saved = await File.ReadAllTextAsync(Path.Combine(directory, "state.json"));
        Assert.DoesNotContain(Password, saved); Assert.DoesNotContain(owner.Token, saved); Assert.DoesNotContain("sessions", saved);
        var account = (await data.ProfileAsync(owner.Token)).ToJsonString();
        Assert.DoesNotContain("password", account, StringComparison.OrdinalIgnoreCase);
        await Assert.ThrowsAsync<LocalSessionException>(() => data.LoginAsync("owner@example.invalid", "wrong"));
    }
    [Fact] public async Task RegistrationPolicyPreservesAdministratorManagedCreation()
    {
        var owner = await User();
        await data.UpdateSettingsAsync(owner.Token, "Fixture workspace", directory, false);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => data.RegisterAsync("closed@example.invalid", "Closed", Password));
        var created = await data.CreateAccountAsync(owner.Token, "managed@example.invalid", "Managed", Password, "player");
        Assert.Equal("player", created["role"]!.GetValue<string>());
    }
    [Theory]
    [InlineData(InventoryEdit.Credits)] [InlineData(InventoryEdit.Cargo)] [InlineData(InventoryEdit.Materials)] [InlineData(InventoryEdit.Modules)]
    public async Task PlayerEditingStartsDisabledForEveryCategory(InventoryEdit edit)
    {
        await User(); var player = await User("player@example.invalid", "Player");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => data.EditAsync(player.Token, player.Id, edit, Change(1), "0"));
    }
    [Fact] public async Task PermissionFlagsAreIndependentAndCheckedOnEachOperation()
    {
        var owner = await User(); var player = await User("player@example.invalid", "Player");
        await data.UpdatePermissionsAsync(owner.Token, new Permissions { AllowPlayerCredits = true });
        Assert.Equal(7, (await data.EditAsync(player.Token, player.Id, InventoryEdit.Credits, Change(7), "0"))["credits"]!.GetValue<long>());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => data.EditAsync(player.Token, player.Id, InventoryEdit.Cargo, new(), "1"));
        await data.UpdatePermissionsAsync(owner.Token, new Permissions());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => data.EditAsync(player.Token, player.Id, InventoryEdit.Credits, Change(1), "1"));
    }
    [Fact] public async Task PlayerCannotReadOrWriteAnotherCommanderEvenWithEditingEnabled()
    {
        var owner = await User(); var player = await User("player@example.invalid", "Player");
        await data.UpdatePermissionsAsync(owner.Token, new Permissions { AllowPlayerCredits = true });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => data.CommanderAsync(player.Token, owner.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => data.EditAsync(player.Token, owner.Id, InventoryEdit.Credits, Change(7), "0"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => data.ImportJournalAsync(player.Token, player.Id, PartialCommander(), "0"));
    }
    [Fact] public async Task LogoutInvalidatesLocalSession()
    {
        var owner = await User(); data.Logout(owner.Token);
        await Assert.ThrowsAsync<LocalSessionException>(() => data.ProfileAsync(owner.Token));
    }
    [Fact] public async Task ExpiredSessionCannotModifyStoredData()
    {
        await data.DisposeAsync(); var time = new ManualTime(); data = new OfflineWorkspace(directory, time);
        var owner = await User(); time.Advance(TimeSpan.FromHours(13));
        await Assert.ThrowsAsync<LocalSessionException>(() => data.EditAsync(owner.Token, owner.Id, InventoryEdit.Credits, Change(9), "0"));
    }
    [Fact] public async Task RoleChangesInvalidateExistingSessionsAndCannotDemoteCurrentAdministrator()
    {
        var owner = await User(); var player = await User("player@example.invalid", "Player");
        await Assert.ThrowsAsync<InvalidOperationException>(() => data.SetRoleAsync(owner.Token, owner.Id, "player"));
        await data.SetRoleAsync(owner.Token, player.Id, "administrator");
        await Assert.ThrowsAsync<LocalSessionException>(() => data.ProfileAsync(player.Token));
        var fresh = await data.LoginAsync("player@example.invalid", Password);
        Assert.Equal("administrator", fresh["account"]!["role"]!.GetValue<string>());
    }
    [Fact] public async Task ConcurrentEditsCannotOverwriteTheSameRevision()
    {
        var owner = await User();
        var edits = new[] { 5L, 7L }.Select(async value =>
        {
            try { await Task.Run(() => data.EditAsync(owner.Token, owner.Id, InventoryEdit.Credits, Change(value), "0")); return true; }
            catch (SaveConflictException) { return false; }
        });
        Assert.Single(await Task.WhenAll(edits), success => success);
        var saved = await data.CommanderAsync(owner.Token); Assert.Equal("1", Version(saved));
        Assert.Contains(saved["credits"]!.GetValue<long>(), new[] { 5L, 7L });
        await Assert.ThrowsAsync<SaveConflictException>(() => data.EditAsync(owner.Token, owner.Id, InventoryEdit.Credits, Change(1), ""));
    }
    [Fact] public async Task InvalidBalanceLeavesRevisionAndStateUnchanged()
    {
        var owner = await User();
        await Assert.ThrowsAsync<InvalidDataException>(() => data.EditAsync(owner.Token, owner.Id, InventoryEdit.Credits, Change(-1), "0"));
        Assert.Equal("0", Version(await data.CommanderAsync(owner.Token)));
    }
    [Fact] public async Task JournalImportCreatesBackupAndEnablesInventoryCatalogAndModuleEdits()
    {
        var owner = await User();
        var result = await data.ImportJournalAsync(owner.Token, owner.Id, PartialCommander(), "0");
        Assert.True(File.Exists(Path.Combine(directory, "inventory-backups", result["backup"]!.GetValue<string>())));
        Assert.Single((await data.CatalogAsync(owner.Token, "fixture cargo")).AsArray());
        var cargo = await data.EditAsync(owner.Token, owner.Id, InventoryEdit.Cargo,
            new JsonObject { ["itemId"] = "fixturecargo", ["quantity"] = 9L }, "1");
        Assert.Equal(9, cargo["cargo"]![0]!["quantity"]!.GetValue<int>());
        var module = await data.EditAsync(owner.Token, owner.Id, InventoryEdit.Modules,
            new JsonObject { ["shipId"] = "1", ["slot"] = "MainEngines", ["priority"] = 4L, ["enabled"] = false, ["health"] = 0.6 }, "2");
        Assert.False(module["ships"]![0]!["modules"]![0]!["enabled"]!.GetValue<bool>());
        Assert.Equal(0.6, module["ships"]![0]!["modules"]![0]!["health"]!.GetValue<double>());
    }
    [Fact] public async Task InvalidModuleEditRollsBackEveryField()
    {
        var owner = await User(); await data.ImportJournalAsync(owner.Token, owner.Id, PartialCommander(), "0");
        await Assert.ThrowsAsync<InvalidDataException>(() => data.EditAsync(owner.Token, owner.Id, InventoryEdit.Modules,
            new JsonObject { ["shipId"] = "1", ["slot"] = "MainEngines", ["priority"] = 4L, ["health"] = 4.0 }, "1"));
        var saved = await data.CommanderAsync(owner.Token); Assert.Equal("1", Version(saved));
        Assert.Equal(2, saved["ships"]![0]!["modules"]![0]!["priority"]!.GetValue<int>());
    }
    [Fact] public async Task ZeroQuantityRemovesStackAndUnknownCatalogItemIsRejected()
    {
        var owner = await User(); await data.ImportJournalAsync(owner.Token, owner.Id, PartialCommander(), "0");
        var saved = await data.EditAsync(owner.Token, owner.Id, InventoryEdit.Materials,
            new JsonObject { ["itemId"] = "fixturematerial", ["category"] = "Raw", ["quantity"] = 0L }, "1");
        Assert.Empty(saved["materials"]!.AsArray());
        await Assert.ThrowsAsync<InvalidDataException>(() => data.EditAsync(owner.Token, owner.Id, InventoryEdit.Cargo,
            new JsonObject { ["itemId"] = "not-a-real-item", ["quantity"] = 1L }, "2"));
    }
    [Fact] public async Task ReturnedSnapshotsCannotModifyStoredData()
    {
        var owner = await User(); var snapshot = await data.CommanderAsync(owner.Token); snapshot["credits"] = 99L;
        Assert.Equal(0L, (await data.CommanderAsync(owner.Token))["credits"]!.GetValue<long>());
    }
    [Fact] public async Task RestartPreservesInventoryAndRoleButNotSessions()
    {
        var owner = await User(); await data.EditAsync(owner.Token, owner.Id, InventoryEdit.Credits, Change(123), "0");
        await data.UpdateSettingsAsync(owner.Token, "Offline fixture", directory, true);
        await data.DisposeAsync(); data = new OfflineWorkspace(directory);
        await Assert.ThrowsAsync<LocalSessionException>(() => data.ProfileAsync(owner.Token));
        var fresh = await data.LoginAsync("owner@example.invalid", Password);
        Assert.Equal(123L, (await data.CommanderAsync(fresh["token"]!.GetValue<string>()))["credits"]!.GetValue<long>());
        Assert.Equal("Offline fixture", (await data.StatusAsync())["name"]!.GetValue<string>());
    }
    [Fact] public async Task SeparateInstancesSeeEachOthersCommittedDataWithoutKeepingAFileLockOpen()
    {
        var owner = await User(); await using var second = new OfflineWorkspace(directory);
        var login = await second.LoginAsync("owner@example.invalid", Password);
        var token = login["token"]!.GetValue<string>();
        await second.EditAsync(token, owner.Id, InventoryEdit.Credits, Change(21), "0");
        Assert.Equal(21L, (await data.CommanderAsync(owner.Token))["credits"]!.GetValue<long>());
        await Assert.ThrowsAsync<SaveConflictException>(() => data.EditAsync(owner.Token, owner.Id, InventoryEdit.Credits, Change(1), "0"));
    }
    [Fact] public async Task StatusNeverClaimsNativeSoloAcceptance()
    {
        var status = await data.StatusAsync(); Assert.False(status["networkTransport"]!.GetValue<bool>()); Assert.False(status["gameIntegrationReady"]!.GetValue<bool>());
    }
    [Fact] public void CoreAndDataLibrariesHaveNoNetworkTransportDependency()
    {
        foreach (var assembly in new[] { typeof(OfflineWorkspace).Assembly, typeof(XFEEliteDangerous.Client.JournalSnapshotReader).Assembly })
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => reference.Name is "System.Net.Http" or "System.Net.Sockets" or "System.Net.NameResolution" or "XFEExtension.NetCore.ServerInteractive");
    }
    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset current = DateTimeOffset.Parse("2026-10-20T00:00:00Z");
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan delta) => current += delta;
    }
}
