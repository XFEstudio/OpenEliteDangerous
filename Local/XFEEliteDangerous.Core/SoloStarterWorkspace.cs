using System.Security.Cryptography;
using System.Text.Json;
using XFEEliteDangerous.Client;
using XFEEliteDangerous.Client.Storage;

namespace XFEEliteDangerous.Core;

/// <summary>Creates a private flight sandbox without importing an existing commander or service data.</summary>
public static class SoloStarterWorkspace
{
    public const string GameBuild = "2026.09.03.332841";
    public const string GameHash = "e6be8bbe04e6a7ae226d4318945af7f367de13dc5a007a261964d9ba8144e988";
    internal const ulong SystemAddress = 10477373803;
    private const string MarkerFile = "solo-starter.json";
    private const string PowerList = "/2.0/elite/powerplay2/powers/list";

    public static async Task<string> PrepareAsync(string directory, string businessLibrary, string operationsFile)
    {
        var root = Path.TrimEndingDirectorySeparator(LocalPaths.Directory(directory));
        if (Directory.Exists(root)) return await ReopenAsync(root).ConfigureAwait(false);
        var parent = Path.GetDirectoryName(root) ?? throw new ArgumentException("A dedicated local SOLO workspace is required.");
        Directory.CreateDirectory(parent);
        businessLibrary = LocalPaths.FilePath(businessLibrary);
        operationsFile = LocalPaths.FilePath(operationsFile);
        if (!File.Exists(businessLibrary) || !File.Exists(operationsFile))
            throw new FileNotFoundException("The bundled SOLO business module is missing. Extract the entire Windows release package.");
        var operations = JsonSerializer.Deserialize<string[]>(File.ReadAllText(operationsFile), Wire.Json)
            ?? throw new InvalidDataException("Missing bundled business operations.");
        if (operations.Length == 0 || operations.Any(value => !value.StartsWith("/2.0/elite/", StringComparison.Ordinal)))
            throw new InvalidDataException("Invalid bundled business operations.");

        // Publish the whole workspace at once. An interrupted first run cannot leave a partial active save.
        var staging = LocalPaths.Directory(Path.Combine(parent, ".solo-setup-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(staging);
        string id;
        await using (var workspace = new OfflineWorkspace(staging))
        {
            var owner = await workspace.OpenLocalOwnerSessionAsync().ConfigureAwait(false);
            id = owner["account"]!["id"]!.GetValue<string>();
            await using var state = new StateStore(staging);
            await state.Update(database =>
            {
                var account = database.Accounts[id];
                account.DisplayName = "Solo Pilot";
                var save = CreateSave(account.NativeUserId);
                account.NativeSave = save;
                account.Commander = save.CreateInventory(account, 1);
                // This is an already-created private pilot, not a claim about an official account's tutorial history.
                account.NativeTutorial = new()
                {
                    HasChosenStarterPack = true,
                    Flight = new() { Complete = true },
                    OnFoot = new() { Complete = true }
                };
                database.AllowPlayerRegistration = false;
                save.ValidateCurrent(account);
                return true;
            }).ConfigureAwait(false);
        }

        WriteReferences(staging);
        WriteBusiness(staging, businessLibrary, operations);
        WriteJson(Path.Combine(staging, MarkerFile), new { schema = 1, accountId = id, gameSha256 = GameHash });
        _ = await ReopenAsync(staging).ConfigureAwait(false);
        try { Directory.Move(staging, root); }
        catch (IOException) when (Directory.Exists(root)) { return await ReopenAsync(root).ConfigureAwait(false); }
        return id;
    }

    private static async Task<string> ReopenAsync(string root)
    {
        var marker = LocalPaths.FilePath(Path.Combine(root, MarkerFile));
        if (!File.Exists(marker) || !File.Exists(Path.Combine(root, "state.json")))
            throw new InvalidDataException("This folder is not a complete starter workspace. Choose a new folder; existing saves were left intact.");
        using var document = JsonDocument.Parse(File.ReadAllBytes(marker));
        var value = document.RootElement;
        if (value.GetProperty("schema").GetInt32() != 1 || value.GetProperty("gameSha256").GetString() != GameHash)
            throw new InvalidDataException("The SOLO workspace belongs to another game build.");
        var id = value.GetProperty("accountId").GetString() ?? "";
        if (id.Length != 32 || !id.All(char.IsAsciiHexDigit)) throw new InvalidDataException("Invalid SOLO workspace identity.");
        var references = new NativeReferenceLibrary(root);
        if (references.Read(PowerList) is not { Status: 200 })
            throw new InvalidDataException("The generated SOLO catalogue is missing from this workspace.");
        await using var state = new StateStore(root);
        return await state.Read(database =>
        {
            if (!database.Accounts.TryGetValue(id, out var account) || account.Disabled || account.NativeSave is null)
                throw new InvalidDataException("The SOLO commander's save is missing or disabled.");
            account.NativeSave.ValidateCurrent(account);
            return id;
        }).ConfigureAwait(false);
    }

    private static NativeCommanderSave CreateSave(uint commander)
    {
        var empty = new NativeEmptyObject();
        var list = new NativeEmptyArray();
        var profile = new NativeFullSavegameProfile
        {
            Name = "Solo Pilot", LoanCeiling = 0, Debt = 0, StartupState = 0x00ff_ffff,
            Gender = "M", ProvingGround = new(0, 0, 0, 0), BattleRating = 0,
            Avatar = "", AvatarXml = "", VehicleDistanceTravelled = 0, AtrLevel = 0, Notoriety = 0, MarkedWanted = false,
            CurrentSystemKey = "10477373802", CurrentSystem = SystemAddress, GameVersion = new(GameBuild, "4.4.1.1", 332841, 0),
            EmptyCollections = new()
            {
                Rank = empty, Constants = empty, Pinned = empty, CurrentCrew = list, Suits = list, WeaponRack = list,
                SystemHyperspacePermits = list, RegionHyperspacePermits = list, LandingPermits = list, LegacyFines = list,
                LegalStates = list, TaxiBookings = list, ShipLocker = empty,
                Microresources = new() { Inventory = empty, Discovered = empty },
                OnboardingObjectives = new() { Objectives = empty, ExtraData = empty }
            }
        };
        // Use the native Sol key pair and a deliberately empty space spawn, clear of bodies and stations.
        var location = new NativeLocationStringValue(SystemAddress, new(10_000_000_000_000, 0, 0), new(0, 0, 0), false);
        return NativeSidewinderSaveFactory.Create(commander, 1, 1000, profile, location) with
        {
            SystemMetadata = new()
            {
                Address = SystemAddress, Id = 1, Name = "Sol", LastChanged = 0, CacheUntil = 0, Population = 0,
                Wealth = 0, Security = 0, StandardOfLiving = 0, DevelopmentLevel = 0,
                Distances = empty, Polities = empty, UnownedSettlements = empty
            },
            Materials = BundledData.NativeMaterials,
            ShipLabels = new Dictionary<uint, NativeShipLabel> { [1] = new("Sidewinder", "SOLO-1") }
        };
    }

    private static void WriteReferences(string workspace)
    {
        var root = Path.Combine(workspace, "native-resources");
        Directory.CreateDirectory(root);
        var entries = new List<object>();
        void Add(string operation, string filename, byte[] bytes)
        {
            File.WriteAllBytes(Path.Combine(root, filename), bytes);
            entries.Add(new { operation, file = filename, sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), length = bytes.Length });
        }
        void AddJson(string operation, string filename, object value) => Add(operation, filename, JsonSerializer.SerializeToUtf8Bytes(value, Wire.Json));
        var empty = new Dictionary<string, object>();
        var emptyRows = Array.Empty<object>();
        // The native Powerplay index requires a nonempty spatial seed. This private exercise has no
        // controlled systems, pledges, rewards or online political data. The symbol/id refer to a local game resource.
        AddJson(PowerList, "starter-powerplay.json", new
        {
            powers = new[]
            {
                new
                {
                    id = 200010, name = "Local Practice Power", home_system_addr = SystemAddress, faction_id = 2,
                    power_resource_name = "ArissaLavignyDuval",
                    leader = new
                    {
                        age = "0", gender = "F", iconId = "2", imageId = "2", colourId = "2", fullname = "Practice",
                        controlEthos = "", uniqueModuleSKU = "", controlCommodity = "", preparationEthos = "",
                        influenceModifier = "0", takingControlEthos = "", preparationCommodity = "", takingControlCommodity = ""
                    },
                    commodities = new { acquisition = "", reinforcement = "", undermining = "" },
                    summary = new
                    {
                        systems_state_exploited = 0, systems_state_fortified = 0, systems_state_stronghold = 0,
                        systems_state_exploited_change = 0, systems_state_fortified_change = 0, systems_state_stronghold_change = 0,
                        expansion_range_exploited = 0, exploitation_range_exploited = 0,
                        expansion_range_fortified = 0, exploitation_range_fortified = 0,
                        expansion_range_stronghold = 0, exploitation_range_stronghold = 0
                    },
                    configuration = new { ai_spawn_range_exploited = 0, ai_spawn_range_fortified = 0, ai_spawn_range_stronghold = 0 },
                    rewards = emptyRows, controlBonuses = emptyRows,
                    // Pinned reader 0x1415EC0 indexes the last parsed perk even when count is zero.
                    // This recognized perk with a zero effect keeps the private exercise neutral.
                    perks = new[] { new { name = "controlled_rebuy", value = 0, unlockRank = 1, availability = "occupied", type = "perk" } },
                    ethos = new { acquisition = emptyRows, conflict = emptyRows, reinforcement = emptyRows, undermining = emptyRows, achilles = emptyRows }
                }
            },
            powerplay = new
            {
                lastTick = 0, nextTick = 4_102_444_800UL, contestThreshold = "0.3",
                meritsPerResourceHandIn = 0, meritsPerCommodityHandIn = 0, maxScoreThreshold = 5000,
                exploitedScoreThreshold = 1000, fortificationScoreThreshold = 2000, strongholdScoreThreshold = 3000
            },
            blacklist = emptyRows, microresources = emptyRows, collectionMicroresources = emptyRows, deliveryMicroresources = emptyRows,
            rankThresholds = new { rankTwo = 1000, rankThree = 2000, rankFour = 3000, rankFive = 4000, aboveRankFive = 5000 },
            activityMeritsMultipliers = new { acquisition = 1, reinforcement = 1, undermining = 1, marked = new { attacking = 1, defending = 1, suggested = 1 } },
            systemAssistMeritBonus = new { maximum = 0 }
        });
        AddJson("/2.0/elite/resources/finance", "starter-finance.json", new Dictionary<string, string>
        {
            ["LoanCeiling"] = "0", ["LoanMarkup"] = "0", ["LoanTaxRate"] = "0", ["LoanRate"] = "0",
            ["CargoInsurance"] = "0", ["ShipInsurance"] = "0.05", ["MinStartingBalance"] = "1000",
            ["WingProfitShare"] = "0", ["UniversalAnnualRate"] = "0", ["UniversalAnnualLoanProvider"] = "",
            ["XPPerRankBase"] = "600", ["XPPerRankRatio"] = "1.075"
        });
        AddJson("/2.0/elite/resources/microresources", "starter-microresources.json", new { microresources = empty });
        AddJson("/2.0/elite/resources/permits", "starter-permits.json", new { permits = empty });
        AddJson("/2.0/elite/colonisation/resources/optionsfulllist", "starter-colonisation.json",
            new { primaryStarportRecipes = empty, options = new { space = empty, planet = empty } });
        AddJson("/2.0/elite/colonisation/resources/marketlink/weighting", "starter-weighting.json", new { primary = 1, secondary = 0 });
        Add("/2.0/elite/synthesis/recipes", "starter-recipes.xml", "<data><recipes /></data>"u8.ToArray());
        Add("/2.0/elite/resources/ussregion/list", "starter-regions.xml", "<data><regions /></data>"u8.ToArray());
        var assembly = typeof(SoloStarterWorkspace).Assembly;
        using var source = assembly.GetManifestResourceStream(assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith(".native-commodities.json", StringComparison.Ordinal)))!;
        using var bytes = new MemoryStream();
        source.CopyTo(bytes);
        Add("/2.0/elite/resources/commodities", "starter-commodities.json", bytes.ToArray());
        WriteJson(Path.Combine(root, "manifest.json"), new { schema = 1, build = GameBuild, gameSha256 = GameHash, documents = entries });
    }

    private static void WriteBusiness(string workspace, string library, string[] operations)
    {
        var root = Path.Combine(workspace, "native-business");
        Directory.CreateDirectory(root);
        const string filename = "read-solo-starter.dll";
        var target = Path.Combine(root, filename);
        File.Copy(library, target);
        using var input = File.OpenRead(target);
        WriteJson(Path.Combine(root, "active.json"), new
        {
            schema = 1, abi = 1, saveAbi = 1, rpcAbi = 1, rpcMethods = new[] { 2, 9, 17, 32, 89 },
            generation = "solo-starter", file = filename, sha256 = Convert.ToHexStringLower(SHA256.HashData(input)),
            gameSha256 = GameHash, operations
        });
    }

    private static void WriteJson(string path, object data) => File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(data, Wire.Json));
}
