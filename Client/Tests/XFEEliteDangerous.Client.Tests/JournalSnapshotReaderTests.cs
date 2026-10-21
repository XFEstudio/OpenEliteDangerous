using System.Text.Json;
using XFEEliteDangerous.Client;
using Xunit;

namespace XFEEliteDangerous.Client.Tests;

public sealed class JournalSnapshotReaderTests
{
    [Fact]
    public async Task LatestLoginIsAuthoritativeAndEarlierObservedShipsRemain()
    {
        using var fixture = new Fixture();
        fixture.Journal("01", """
            {"timestamp":"2026-10-16T10:00:00Z","event":"LoadGame","Commander":"Example","FID":"secret-fid","Credits":100,"Ship":"sidewinder","ShipID":0}
            {"timestamp":"2026-10-16T10:00:01Z","event":"Loadout","Ship":"sidewinder","ShipID":0,"Modules":[{"Slot":"PowerPlant","Item":"int_powerplant_size2_class1","Value":2000,"Priority":1,"On":true,"Health":0.75}]}
            """);
        fixture.Journal("02", """
            {"timestamp":"2026-10-17T10:00:00Z","event":"Fileheader","gameversion":"4.3.2.0"}
            {"timestamp":"2026-10-17T10:00:01Z","event":"LoadGame","Commander":"Example","Credits":1000,"Ship":"Mandalay","ShipID":3}
            {"timestamp":"2026-10-17T10:00:02Z","event":"Loadout","Ship":"mandalay","ShipID":3,"ShipName":"Explorer","ShipIdent":"AB-01","Modules":[{"Slot":"MainEngines","Item":"int_engine_size5_class5","Value":500,"Priority":2,"On":false,"Health":0.9}]}
            {"timestamp":"2026-10-17T10:00:03Z","event":"RefuelAll","Cost":25}
            """);
        var snapshot = await fixture.Read();
        Assert.Equal(975, snapshot.Credits);
        Assert.Equal(2, snapshot.Ships.Count);
        var ship = Assert.Single(snapshot.Ships, s => s.Active);
        Assert.Equal("3", ship.Id); Assert.Equal("Explorer", ship.Name); Assert.Equal("AB-01", ship.Ident);
        var module = Assert.Single(ship.Modules);
        Assert.False(module.Enabled); Assert.Equal(2, module.Priority); Assert.Equal(0.9, module.Health); Assert.Equal(500, module.Value);
        Assert.Equal("4.3.2.0", snapshot.Source.GameVersion);
        Assert.Equal("local-journal-partial", snapshot.Source.Completeness);
    }

    [Fact]
    public async Task LatestCommanderDoesNotReceivePreviousCommandersInventory()
    {
        using var fixture = new Fixture();
        fixture.Journal("01", """
            {"timestamp":"2026-10-16T10:00:00Z","event":"LoadGame","Commander":"Other","Credits":500000,"Ship":"anaconda","ShipID":42}
            {"timestamp":"2026-10-16T10:00:01Z","event":"Cargo","Inventory":[{"Name":"gold","Count":99}]}
            {"timestamp":"2026-10-17T10:00:00Z","event":"LoadGame","Commander":"Current","Credits":123,"Ship":"sidewinder","ShipID":0}
            """);
        var snapshot = await fixture.Read();
        Assert.Equal("Current", snapshot.Name); Assert.Equal(123, snapshot.Credits);
        Assert.Empty(snapshot.Cargo); Assert.Equal("sidewinder", Assert.Single(snapshot.Ships).ShipType);
    }

    [Fact]
    public async Task MaterialSnapshotsTradeAndCraftPreserveCategories()
    {
        using var fixture = new Fixture(); fixture.Login();
        fixture.Journal("02", """
            {"timestamp":"2026-10-17T10:00:01Z","event":"Materials","Raw":[{"Name":"iron","Count":10}],"Manufactured":[{"Name":"chemicalprocessors","Count":3}],"Encoded":[{"Name":"legacyfirmware","Count":5}]}
            {"timestamp":"2026-10-17T10:00:02Z","event":"MaterialTrade","Paid":{"Material":"iron","Category":"Raw","Quantity":2},"Received":{"Material":"chemicalprocessors","Category":"Manufactured","Quantity":6}}
            {"timestamp":"2026-10-17T10:00:03Z","event":"EngineerCraft","Ingredients":[{"Name":"iron","Count":1},{"Name":"legacyfirmware","Count":2}]}
            {"timestamp":"2026-10-17T10:00:04Z","event":"MaterialCollected","Category":"Raw","Name":"iron","Count":3}
            {"timestamp":"2026-10-17T10:00:05Z","event":"ShipLocker","Items":[{"Name":"circuitboard","Count":4}],"Components":[],"Consumables":[],"Data":[]}
            {"timestamp":"2026-10-17T10:00:06Z","event":"Backpack","Items":[{"Name":"circuitboard","Count":2}],"Components":[],"Consumables":[],"Data":[]}
            """);
        var snapshot = await fixture.Read();
        Assert.Equal(10, snapshot.Materials.Single(m => m.Id == "iron").Quantity);
        Assert.Equal(9, snapshot.Materials.Single(m => m.Category == "Manufactured").Quantity);
        Assert.Equal(3, snapshot.Materials.Single(m => m.Category == "Encoded").Quantity);
        Assert.Equal(4, snapshot.Materials.Single(m => m.Category == "ShipLocker.Items").Quantity);
        Assert.Equal(2, snapshot.Materials.Single(m => m.Category == "Backpack.Items").Quantity);
    }

    [Fact]
    public async Task CurrentCargoSidecarIsAuthoritativeWithoutDoubleCountingPickup()
    {
        using var fixture = new Fixture(); fixture.Login();
        fixture.Journal("02", """
            {"timestamp":"2026-10-17T10:00:01Z","event":"Cargo","Inventory":[{"Name":"gold","Count":10}]}
            {"timestamp":"2026-10-17T10:00:02Z","event":"CollectCargo","Type":"gold"}
            {"timestamp":"2026-10-17T10:00:03Z","event":"Cargo","Vessel":"Ship","Count":1}
            """);
        fixture.Sidecar("Cargo", """{"timestamp":"2026-10-17T10:00:02Z","event":"Cargo","Inventory":[{"Name":"ancientrelic","Name_Localised":"Guardian Relic","Count":1}]}""");
        var snapshot = await fixture.Read();
        var cargo = Assert.Single(snapshot.Cargo);
        Assert.Equal("ancientrelic", cargo.Id); Assert.Equal(1, cargo.Quantity); Assert.Equal("Guardian Relic", cargo.Name);
    }

    [Fact]
    public async Task StaleSidecarCannotReplaceNewSessionSnapshot()
    {
        using var language = AppText.UseLanguage("zh-CN");
        using var fixture = new Fixture(); fixture.Login();
        fixture.Journal("02", """{"timestamp":"2026-10-17T10:00:01Z","event":"Cargo","Inventory":[{"Name":"gold","Count":2}]}""");
        fixture.Sidecar("Cargo", """{"timestamp":"2026-10-16T10:00:00Z","event":"Cargo","Inventory":[{"Name":"silver","Count":999}]}""");
        var snapshot = await fixture.Read();
        Assert.Equal("gold", Assert.Single(snapshot.Cargo).Id);
        Assert.Contains(snapshot.Source.Warnings, w => w.Contains("早于最新登录"));
    }

    [Fact]
    public async Task CataloguesNeverBecomeOwnedInventory()
    {
        using var language = AppText.UseLanguage("zh-CN");
        using var fixture = new Fixture(); fixture.Login();
        fixture.Sidecar("Outfitting", """{"timestamp":"2026-10-17T10:00:01Z","event":"Outfitting","Items":[{"id":128,"Name":"int_powerplant_size8_class5","BuyPrice":9999}]}""");
        fixture.Sidecar("Shipyard", """{"timestamp":"2026-10-17T10:00:02Z","event":"Shipyard","PriceList":[{"id":4,"ShipType":"anaconda","ShipPrice":999999}]}""");
        var snapshot = await fixture.Read();
        Assert.Empty(snapshot.Ships.Single().Modules); Assert.Single(snapshot.Ships);
        Assert.Equal(2, snapshot.Source.Warnings.Count(w => w.Contains("商店目录")));
    }

    [Fact]
    public async Task PrivacyIsAnExplicitWhitelistNotRecursiveJournalSerialization()
    {
        using var fixture = new Fixture();
        fixture.Journal("01", """
            {"timestamp":"2026-10-17T10:00:00Z","event":"LoadGame","Commander":"Example","FID":"DONT-EXPORT-FID","Token":"DONT-EXPORT-TOKEN","Credits":1000,"Ship":"sidewinder","ShipID":0}
            {"timestamp":"2026-10-17T10:00:01Z","event":"ReceiveText","From":"DONT-EXPORT-SENDER","Message":"DONT-EXPORT-CHAT","authToken":"PRIVATE"}
            {"timestamp":"2026-10-17T10:00:02Z","event":"Loadout","Ship":"sidewinder","ShipID":0,"Modules":[{"Slot":"A","Item":"module","authToken":"DONT-EXPORT-MODULE"}]}
            """);
        var json = JsonSerializer.Serialize(await fixture.Read(), ClientJson.Options);
        Assert.DoesNotContain("DONT-EXPORT", json); Assert.DoesNotContain("PRIVATE", json);
        Assert.DoesNotContain("FID", json); Assert.DoesNotContain("Token", json);
        Assert.DoesNotContain(fixture.Directory, json);
    }

    [Fact]
    public async Task TruncatedJournalAndSidecarAreReportedWithoutDiscardingGoodState()
    {
        using var fixture = new Fixture(); fixture.Login();
        fixture.Journal("02", """
            {"timestamp":"2026-10-17T10:00:01Z","event":"Materials","Raw":[{"Name":"iron","Count":3}]}
            {"timestamp":
            """);
        fixture.Sidecar("Cargo", "{not JSON");
        var snapshot = await fixture.Read();
        Assert.Equal(3, Assert.Single(snapshot.Materials).Quantity);
        Assert.Contains(snapshot.Source.Warnings, w => w.Contains("Journal.02.log:2"));
        Assert.Contains(snapshot.Source.Warnings, w => w.Contains("Cargo.json JSON"));
        Assert.All(snapshot.Source.Files, file => Assert.Matches("^[a-f0-9]{64}$", file.Sha256));
    }

    [Fact]
    public async Task ModulesInfoOnlyUpdatesMatchingActiveLoadoutAndOldFilesAreIgnored()
    {
        using var fixture = new Fixture(); fixture.Login();
        fixture.Journal("02", """{"timestamp":"2026-10-17T10:00:01Z","event":"Loadout","Ship":"sidewinder","ShipID":0,"Modules":[{"Slot":"PowerPlant","Item":"plant","Priority":1,"Health":0.8,"Value":50}]}""");
        fixture.Sidecar("ModulesInfo", """{"timestamp":"2026-10-17T10:00:02Z","event":"ModuleInfo","Modules":[{"Slot":"PowerPlant","Item":"plant","Priority":4},{"Slot":"ShieldGenerator","Item":"notowned","Priority":2}]}""");
        var module = Assert.Single((await fixture.Read()).Ships.Single().Modules);
        Assert.Equal(4, module.Priority); Assert.Equal(0.8, module.Health); Assert.Equal(50, module.Value);
    }

    [Fact]
    public async Task ShipSaleAndStoredListDoNotResurrectHistoricalShipOwnership()
    {
        using var fixture = new Fixture(); fixture.Login();
        fixture.Journal("02", """
            {"timestamp":"2026-10-17T10:00:01Z","event":"ShipyardNew","ShipType":"type6","NewShipID":2}
            {"timestamp":"2026-10-17T10:00:02Z","event":"Loadout","Ship":"type6","ShipID":2,"Modules":[{"Slot":"A","Item":"first"},{"Slot":"B","Item":"second"}]}
            {"timestamp":"2026-10-17T10:00:03Z","event":"ModuleSwap","Ship":"type6","ShipID":2,"FromSlot":"A","ToSlot":"B"}
            {"timestamp":"2026-10-17T10:00:04Z","event":"StoredShips","ShipsHere":[],"ShipsRemote":[]}
            """);
        var snapshot = await fixture.Read(); var ship = Assert.Single(snapshot.Ships);
        Assert.Equal("2", ship.Id); Assert.Equal("second", ship.Modules.Single(m => m.Slot == "A").ItemId);
        Assert.Equal("first", ship.Modules.Single(m => m.Slot == "B").ItemId);
    }

    [Fact]
    public async Task MissingLoginIsAnErrorInsteadOfInventingAnAccount()
    {
        using var fixture = new Fixture(); fixture.Journal("01", """{"timestamp":"2026-10-17T10:00:00Z","event":"Cargo","Count":0}""");
        await Assert.ThrowsAsync<InvalidDataException>(fixture.Read);
    }

    [Fact]
    public async Task EngineeringIsPreservedAndCraftUpdatesItsActualParameters()
    {
        using var fixture = new Fixture(); fixture.Login();
        fixture.Journal("02", """
            {"timestamp":"2026-10-17T10:00:01Z","event":"Loadout","Ship":"sidewinder","ShipID":0,"Modules":[{"Slot":"MainEngines","Item":"engine","Engineering":{"Engineer":"Felicity Farseer","EngineerID":300100,"BlueprintID":128673657,"BlueprintName":"Engine_Dirty","Level":3,"Quality":1.0,"ExperimentalEffect":"special_engine_performance","Modifiers":[{"Label":"EngineOptPerformance","Value":126.0,"OriginalValue":100.0,"LessIsGood":0,"Token":"PRIVATE"}]}}]}
            {"timestamp":"2026-10-17T10:00:02Z","event":"EngineerCraft","Slot":"MainEngines","Module":"engine","Engineer":"Felicity Farseer","EngineerID":300100,"BlueprintID":128673657,"BlueprintName":"Engine_Dirty","Level":4,"Quality":0.8,"Modifiers":[{"Label":"EngineOptPerformance","Value":133.0,"OriginalValue":100.0,"LessIsGood":0}]}
            """);
        var engineering = (await fixture.Read()).Ships.Single().Modules.Single().Engineering!;
        Assert.Equal(4, engineering["Level"]!.GetValue<int>());
        Assert.Equal(133.0, engineering["Modifiers"]![0]!["Value"]!.GetValue<double>());
        Assert.Equal("special_engine_performance", engineering["ExperimentalEffect"]!.GetValue<string>());
        Assert.DoesNotContain("PRIVATE", engineering.ToJsonString());
    }

    [Fact]
    public async Task MalformedArrayEntriesDoNotCrashImport()
    {
        using var fixture = new Fixture(); fixture.Login();
        fixture.Journal("02", """
            {"timestamp":"2026-10-17T10:00:01Z","event":"Loadout","Ship":"sidewinder","ShipID":0,"Modules":[null,"bad",{"Slot":"A","Item":"engine"}]}
            {"timestamp":"2026-10-17T10:00:02Z","event":"Materials","Raw":[123,{"Name":"iron","Count":2}]}
            """);
        var snapshot = await fixture.Read();
        Assert.Single(snapshot.Ships.Single().Modules); Assert.Equal(2, Assert.Single(snapshot.Materials).Quantity);
    }

    [Fact]
    public async Task OnFootSrvFighterAndTaxiLoadGameRecordsAreNotOwnedShips()
    {
        using var fixture = new Fixture(); fixture.Login();
        fixture.Journal("02", """
            {"timestamp":"2026-10-17T10:00:01Z","event":"LoadGame","Commander":"Example","Credits":1000,"Ship":"combat_multicrew_srv_01","ShipID":4}
            {"timestamp":"2026-10-17T10:00:02Z","event":"LoadGame","Commander":"Example","Credits":1000,"Ship":"flightsuit","ShipID":4293000000}
            {"timestamp":"2026-10-17T10:00:03Z","event":"LoadGame","Commander":"Example","Credits":1000,"Ship":"independent_fighter","ShipID":5}
            {"timestamp":"2026-10-17T10:00:04Z","event":"LoadGame","Commander":"Example","Credits":1000,"Ship":"vulture","ShipID":6,"Taxi":true}
            """);
        var ship = Assert.Single((await fixture.Read()).Ships);
        Assert.Equal("sidewinder", ship.ShipType); Assert.True(ship.Active);
    }

    [Fact]
    public async Task CreditChangesFollowDocumentedAmountCostAndDonationFields()
    {
        using var fixture = new Fixture(); fixture.Login();
        fixture.Journal("02", """
            {"timestamp":"2026-10-17T10:00:01Z","event":"PayFines","Amount":50}
            {"timestamp":"2026-10-17T10:00:02Z","event":"PayBounties","Amount":25}
            {"timestamp":"2026-10-17T10:00:03Z","event":"RedeemVoucher","Amount":300}
            {"timestamp":"2026-10-17T10:00:04Z","event":"MissionCompleted","Reward":100,"Donated":200}
            {"timestamp":"2026-10-17T10:00:05Z","event":"ModuleRetrieve","Cost":10,"Ship":"sidewinder","ShipID":0,"Slot":"A","RetrievedItem":"engine"}
            """);
        Assert.Equal(1115, (await fixture.Read()).Credits);
    }
}

internal sealed class Fixture : IDisposable
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "fixtures"));
    public string Directory { get; } = Path.Combine(Root, Guid.NewGuid().ToString("N"));
    public Fixture() => System.IO.Directory.CreateDirectory(Directory);
    public void Journal(string suffix, string lines) => File.WriteAllText(Path.Combine(Directory, $"Journal.{suffix}.log"), lines);
    public void Sidecar(string name, string json) => File.WriteAllText(Path.Combine(Directory, name + ".json"), json);
    public void Login() => Journal("01", """{"timestamp":"2026-10-17T10:00:00Z","event":"LoadGame","Commander":"Example","Credits":1000,"Ship":"sidewinder","ShipID":0}""");
    public Task<CommanderSnapshot> Read() => new JournalSnapshotReader().ReadAsync(Directory);
    public void Dispose()
    {
        var resolved = Path.GetFullPath(Directory);
        if (!resolved.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Fixture escaped its test root.");
        System.IO.Directory.Delete(resolved, true);
    }
}
