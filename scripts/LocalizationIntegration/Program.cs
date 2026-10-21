using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using XFEEliteDangerous.Client;
using XFEEliteDangerous.Core;
using XFEEliteDangerous.ManagementClient;
using XFEEliteDangerous.OnlineClient;
using XFEEliteDangerous.Server;

if (args.Length is not (1 or 2)) throw new ArgumentException("Expected output directory and optional published server executable.");
var root = Path.GetFullPath(args[0]);
if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any()) throw new IOException("Use a new, empty integration output directory.");
Directory.CreateDirectory(root);
var results = new List<object>();
void Check(string name, bool condition)
{
    results.Add(new { name, passed = condition });
    if (!condition) throw new InvalidOperationException(name);
}
async Task<T> Rejected<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T error) { return error; }
    throw new InvalidOperationException("Expected rejection: " + typeof(T).Name);
}
using var managementReservation = new TcpListener(IPAddress.Loopback, 0);
using var gameReservation = new TcpListener(IPAddress.Loopback, 0);
managementReservation.Start(); gameReservation.Start();
var port = ((IPEndPoint)managementReservation.LocalEndpoint).Port;
var gamePort = ((IPEndPoint)gameReservation.LocalEndpoint).Port;
var config = new ServerConfiguration(Path.Combine(root, "workspace"), root, port, Background: true, Online: new("127.0.0.1", gamePort));
var configFile = Path.Combine(root, "server.json");
await File.WriteAllTextAsync(configFile, JsonSerializer.Serialize(config, Wire.Json));
managementReservation.Stop(); gameReservation.Stop();
var serverExecutable = args.Length == 2 ? Path.GetFullPath(args[1]) : null;
var info = new ProcessStartInfo(serverExecutable ?? "dotnet") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
if (serverExecutable is null)
foreach (var value in new[] { "exec", "--runtimeconfig", Path.Combine(AppContext.BaseDirectory, "LocalizationIntegration.runtimeconfig.json"),
    "--depsfile", Path.Combine(AppContext.BaseDirectory, "LocalizationIntegration.deps.json"), typeof(OnlineGameHost).Assembly.Location,
    }) info.ArgumentList.Add(value);
foreach (var value in new[] { "--config", configFile, "--language", "en-US" }) info.ArgumentList.Add(value);
using var server = Process.Start(info) ?? throw new IOException("Fixture server did not start.");
using var raw = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(5) };
string? localKey = null;
try
{
    var deadline = DateTimeOffset.UtcNow.AddSeconds(25);
    while (true)
    {
        if (server.HasExited || DateTimeOffset.UtcNow > deadline) throw new IOException("Fixture startup failed. See its private startup logs.");
        try { var health = await raw.GetStringAsync(config.Address + "health"); if (JsonNode.Parse(health)?["status"]?.ToString() == "ready") break; }
        catch (HttpRequestException) { }
        await Task.Delay(100);
    }
    localKey = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, "admin-access.json")))!["key"]!.GetValue<string>();
    var profile = JsonSerializer.Deserialize(await File.ReadAllTextAsync(Path.Combine(root, "game-client.json")), GameLinkJson.Default.OnlineServerProfile)!;
    using var api = new ManagementApi(config.Address);
    var password = Guid.NewGuid().ToString("N");
    const string email = "language-check@example.invalid";
    const string displayName = "Test 指挥官";
    var account = await api.CallAsync("auth/register", new() { ["email"] = email, ["name"] = displayName, ["password"] = password });
    var login = await api.CallAsync("auth/login", new() { ["email"] = email, ["password"] = password });
    var token = login["token"]!.GetValue<string>();
    var id = account["account"]!["id"]!.GetValue<string>();
    foreach (var language in new[] { "en-US", "zh-CN" })
    {
        using var scope = AppText.UseLanguage(language);
        var denied = await Rejected<LocalSessionException>(() => api.CallAsync("profile", token: "invalid-token"));
        Check(language + ": management client propagates language and HTTP 401", denied.Message == AppText.Get("OfflineWorkspace.Text394"));
        var current = await api.CallAsync("profile", token: token);
        Check(language + ": account identity and user text are invariant", current["id"]!.ToString() == id && current["displayName"]!.ToString() == displayName && current["role"]!.ToString() == "administrator");
        var commander = await api.CallAsync("commander", token: token);
        var edit = new JsonObject { ["edit"] = "credits", ["values"] = new JsonObject { ["delta"] = 1L }, ["version"] = commander["version"]!.DeepClone() };
        await api.CallAsync("inventory/edit", edit, token);
        var conflict = await Rejected<SaveConflictException>(() => api.CallAsync("inventory/edit", edit, token));
        Check(language + ": localized save conflict retains exception type", conflict.Message == AppText.Get("NativeTutorialSelection.Text382"));
        await using var online = new OnlineConnection(profile);
        var onlineDenied = await Rejected<GameServerException>(() => online.LoginAsync(email, "incorrect-password"));
        Check(language + ": pinned HTTPS client propagates error language", onlineDenied.Status == 401 && onlineDenied.Message == AppText.Get("OfflineWorkspace.Text385"));
    }
    var replies = await Task.WhenAll(Enumerable.Range(0, 32).Select(async i =>
    {
        var language = i % 2 == 0 ? "en-US" : "zh-CN";
        using var scope = AppText.UseLanguage(language);
        var denied = await Rejected<LocalSessionException>(() => api.CallAsync("profile"));
        return denied.Message == AppText.Get("OfflineWorkspace.Text394");
    }));
    Check("Concurrent mixed-language requests do not leak language", replies.All(x => x));
    using var weightedRequest = new HttpRequestMessage(HttpMethod.Post, config.Address + "control");
    weightedRequest.Headers.AcceptLanguage.ParseAdd("en-US;q=0, zh-CN;q=0.9");
    weightedRequest.Content = new StringContent("{\"operation\":\"profile\",\"arguments\":{}}", System.Text.Encoding.UTF8, "application/json");
    using var weightedReply = await raw.SendAsync(weightedRequest);
    var weightedBody = JsonNode.Parse(await weightedReply.Content.ReadAsStringAsync())!;
    Check("HTTP language negotiation honors q=0 exclusion", weightedReply.StatusCode == HttpStatusCode.Unauthorized && weightedBody["error"]!.ToString() == AppText.Catalog("zh-CN")["OfflineWorkspace.Text394"]);
    Check("Machine-readable rejection code is independent of language", weightedBody["code"]!.ToString() == "request_rejected");
    var final = await api.CallAsync("commander", token: token);
    Check("Both language edits persist numeric credits unchanged", final["credits"]!.GetValue<long>() == 2);
    await api.CallAsync("auth/logout", token: token);
    var serverAssemblies = serverExecutable is null ? null : new[] { "XFEEliteDangerous.Server.dll", "XFEEliteDangerous.Core.dll", "XFEEliteDangerous.Client.dll", "XFEEliteDangerous.NativeFormat.dll" }
        .ToDictionary(name => name, name => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(serverExecutable)!, name)))));
    await File.WriteAllTextAsync(Path.Combine(root, "validation.json"), JsonSerializer.Serialize(new { passed = true, serverExecutable, serverAssemblies, languages = new[] { "en-US", "zh-CN" }, checks = results, gameStarted = false, desktopInputSent = false }, Wire.Json));
    Console.WriteLine($"PASS {results.Count} localization integration checks: HTTP, pinned HTTPS, stable identities, save conflicts and concurrent request isolation.");
}
finally
{
    if (!server.HasExited && localKey is not null)
    {
        using var shutdown = new HttpRequestMessage(HttpMethod.Post, config.Address + "shutdown");
        shutdown.Headers.Authorization = new("Bearer", localKey);
        using var response = await raw.SendAsync(shutdown);
        if (!response.IsSuccessStatusCode) throw new IOException("Fixture shutdown rejected.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await server.WaitForExitAsync(timeout.Token);
    }
    else if (!server.HasExited) { server.Kill(entireProcessTree: true); await server.WaitForExitAsync(); }
}
