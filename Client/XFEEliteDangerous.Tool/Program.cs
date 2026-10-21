using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using XFEEliteDangerous.Client;
using XFEEliteDangerous.Core;
using XFEEliteDangerous.ManagementClient;

try { return await RunAsync(AppText.ConfigureArguments(args)); }
catch (ArgumentException error) { Console.Error.WriteLine(error.Message); return 1; }

static async Task<int> RunAsync(string[] args)
{
    if (args.Length == 0 || args[0] is "--help" or "help")
    {
        Console.WriteLine(AppText.Get("Program.Text053"));
        return 0;
    }
    try
    {
        var command = args[0]; var options = Parse(args[1..]);
        if(command is "server-status" or "server-probe")
        {
            using var api=new ManagementApi(options.GetValueOrDefault("address")??"http://127.0.0.1:48080/api/");
            if(command=="server-status"){Print(await api.CallAsync("status"));return 0;}
            var access=JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(options.GetValueOrDefault("data-root")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"XFEEliteDangerous","Server"),"admin-access.json")))!;
            var owner=await api.CallAsync("auth/local-owner",token:access["key"]!.GetValue<string>());var token=owner["token"]!.GetValue<string>();
            try
            {
                var accounts=await api.CallAsync("accounts",token:token);if(accounts is not JsonArray)throw new InvalidDataException("Accounts response is not an array.");
                var catalog=await api.CallAsync("catalog",new(){["search"]=""},token);if(catalog is not JsonArray)throw new InvalidDataException("Catalogue response is not an array.");
                var profile=await api.CallAsync("profile",token:token);if(profile["role"]?.GetValue<string>()!="administrator")throw new InvalidDataException("Local owner is not an administrator.");
                var probeId=options.GetValueOrDefault("account")??owner["account"]!["id"]!.GetValue<string>();
                _=await api.CallAsync("fleet",new(){["accountId"]=probeId},token);
                var rejected=false;try{_=await api.CallAsync("profile",token:"invalid-token");}catch(LocalSessionException){rejected=true;}
                if(!rejected)throw new InvalidDataException("Invalid token was accepted.");
                Console.WriteLine("PASS typed C# management client: object/array responses, local-owner login, fleet, catalogue and HTTP401 mapping.");
            }
            finally{await api.CallAsync("auth/logout",token:token);}
            return 0;
        }
        if (command == "inspect")
        {
            var inspection = await new GameInstallationInspector().InspectAsync(Required(options, "game"));
            Console.WriteLine(JsonSerializer.Serialize(inspection, ClientJson.Options)); return 0;
        }
        var directory = options.GetValueOrDefault("data") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XFEEliteDangerous", "Offline");
        await using var workspace = new OfflineWorkspace(directory);
        if (command == "status") { Print(await workspace.StatusAsync()); return 0; }
        if (command is not ("create-account" or "inventory" or "credits" or "set-quantity" or "import-journal" or "export")) throw new ArgumentException(AppText.Get("Program.Text054"));
        if (!options.ContainsKey("password-stdin") || !Console.IsInputRedirected) throw new ArgumentException(AppText.Get("Program.Text055"));
        var password = await Console.In.ReadLineAsync() ?? throw new ArgumentException(AppText.Get("Program.Text056"));
        var email = Required(options, "email");
        if (command == "create-account")
        {
            Print(await workspace.RegisterAsync(email, Required(options, "name"), password)); return 0;
        }
        var login = await workspace.LoginAsync(email, password);
        password = "";
        var session = login["token"]!.GetValue<string>();
        var id = options.GetValueOrDefault("account") ?? login["account"]!["id"]!.GetValue<string>();
        try
        {
            switch (command)
            {
                case "inventory": Print(await workspace.CommanderAsync(session, id)); break;
                case "credits":
                    Print(await workspace.EditAsync(session, id, InventoryEdit.Credits, new JsonObject { ["delta"] = long.Parse(Required(options, "delta"), CultureInfo.InvariantCulture) }, Required(options, "version"))); break;
                case "set-quantity":
                    var category = Required(options, "category");
                    if (category is not ("cargo" or "materials")) throw new ArgumentException(AppText.Get("Program.Text057"));
                    Print(await workspace.EditAsync(session, id, category == "cargo" ? InventoryEdit.Cargo : InventoryEdit.Materials,
                        new JsonObject { ["itemId"] = Required(options, "item"), ["category"] = Required(options, "item-category"), ["quantity"] = long.Parse(Required(options, "quantity"), CultureInfo.InvariantCulture) }, Required(options, "version"))); break;
                case "import-journal":
                    var snapshot = await new JournalSnapshotReader().ReadAsync(Required(options, "journal"));
                    var node = JsonSerializer.SerializeToNode(snapshot, ClientJson.Options)!.AsObject();
                    foreach (var row in node["cargo"]!.AsArray()) row!["category"] = "cargo";
                    Print(await workspace.ImportJournalAsync(session, id, node, Required(options, "version"))); break;
                case "export":
                    var output = LocalPaths.FilePath(Required(options, "output"));
                    var commander = await workspace.CommanderAsync(session, id);
                    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                    await using (var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        await JsonSerializer.SerializeAsync(stream, commander, ClientJson.Options);
                    Console.WriteLine(AppText.Get("Program.Text058") + output); break;
            }
            return 0;
        }
        finally { workspace.Logout(session); }
    }
    catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException or JsonException or OverflowException or FormatException)
    {
        Console.Error.WriteLine(error is JsonException ? AppText.Get("Program.Text059") : error.Message); return 1;
    }
}
static Dictionary<string, string?> Parse(string[] args)
{
    var result = new Dictionary<string, string?>(StringComparer.Ordinal);
    var known = new HashSet<string>(["data", "email", "name", "password-stdin", "account", "game", "delta", "version", "category", "item", "item-category", "quantity", "journal", "output","address","data-root"], StringComparer.Ordinal);
    for (var index = 0; index < args.Length; index++)
    {
        if (!args[index].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException(AppText.Get("Program.Text060"));
        var key = args[index][2..];
        if (!known.Contains(key)) throw new ArgumentException(AppText.Get("Program.Text061") + key);
        var value = key == "password-stdin" ? null : ++index < args.Length ? args[index] : throw new ArgumentException(AppText.Get("Program.Text062") + key);
        if (!result.TryAdd(key, value)) throw new ArgumentException(AppText.Get("Program.Text063") + key);
    }
    return result;
}
static string Required(Dictionary<string, string?> options, string key) => options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException(AppText.Get("Program.Text064") + key);
static void Print(JsonNode value) => Console.WriteLine(value.ToJsonString(ClientJson.Options));
