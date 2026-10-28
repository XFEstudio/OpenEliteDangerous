using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using XFEEliteDangerous.Client;
using XFEEliteDangerous.Core;
using XFEEliteDangerous.OnlineClient;
using XFEEliteDangerous.Server;

if(args is ["--prepare-native-account",var ownedDirectory,var ownedAccount])
{
    var artifacts=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../artifacts"))+Path.DirectorySeparatorChar;
    if(!Path.GetFullPath(ownedDirectory).StartsWith(artifacts,StringComparison.OrdinalIgnoreCase)||ownedAccount.Length!=32||ownedAccount.Any(c=>!char.IsAsciiHexDigit(c)))
        throw new ArgumentException("An isolated artifact workspace and fixture account are required.");
    await PrepareNativeAsync(ownedDirectory,[ownedAccount]);return;
}
if(args is ["--prepare-native",var fixtureRoot])
{
    var ids=new[]{"a","b"}.Select(label=>JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(fixtureRoot,$"launch-{label}.private.json")),GameLinkJson.Default.OnlineLaunch)!.Lease.AccountId).ToArray();
    await PrepareNativeAsync(Path.Combine(fixtureRoot,"workspace"),ids);return;
}
if(args is ["--probe",var deploymentPath])
{
    var deployment=JsonNode.Parse(await File.ReadAllTextAsync(deploymentPath))!;var dataRoot=deployment["dataRoot"]!.GetValue<string>();
    var profile=JsonSerializer.Deserialize(await File.ReadAllTextAsync(Path.Combine(dataRoot,"game-client.json")),GameLinkJson.Default.OnlineServerProfile)!;
    var access=JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(dataRoot,"admin-access.json")))!;
    await using var connection=new OnlineConnection(profile);
    OnlineGameLease lease;
    try{lease=await connection.OpenLocalAsync(deployment["account"]!.GetValue<string>(),access["key"]!.GetValue<string>());}
    catch(GameServerException error)when(error.Status==409&&error.Message.Contains("离线游戏占用",StringComparison.Ordinal))
    {
        Console.WriteLine("PASS deployed pinned HTTPS authentication and offline-owner guard: the running offline commander's concurrent online launch is rejected.");return;
    }
    var login=await connection.InvokeAsync(new("data","GET","/2.0/elite/user/login",new(){["machineToken"]="private-read-only-probe",["authToken"]="private-read-only-probe-auth"},[]));
    var saved=await connection.InvokeAsync(new("data","GET","/2.0/elite/commander/savegame",new(),[]));
    var commodities=await connection.InvokeAsync(new("data","GET","/2.0/elite/resources/commodities",new(),[]));
    if(login.Status!=200||saved.Status!=200||commodities.Status!=200||JsonNode.Parse(saved.Payload)!["commander"]!["id"]!.GetValue<uint>()!=lease.NativeUserId)throw new InvalidOperationException("Deployed game endpoint probe failed.");
    using var healthHandler=new HttpClientHandler{UseProxy=false,AllowAutoRedirect=false};
    var healthPin=Convert.FromHexString(profile.CertificateSha256);
    healthHandler.ServerCertificateCustomValidationCallback=(_,certificate,_,_)=>certificate is not null&&CryptographicOperations.FixedTimeEquals(SHA256.HashData(certificate.RawData),healthPin);
    using var healthClient=new HttpClient(healthHandler){Timeout=TimeSpan.FromSeconds(5)};
    var health=JsonNode.Parse(await healthClient.GetStringAsync(new Uri(new Uri(profile.Address),"health")))!;
    if(health["nativeMultiplayerValidated"]?.GetValue<bool>()!=true||health["nativeMultiplayerValidationScope"]?.GetValue<string>()!="pinned-odyssey-two-clients-loopback")throw new InvalidOperationException("Deployed native validation scope differs.");
    Console.WriteLine("PASS deployed pinned HTTPS health: native multiplayer validated, scope pinned-odyssey-two-clients-loopback.");
    Console.WriteLine("PASS deployed pinned HTTPS game endpoint: local-owner authentication, native login, account-owned save and central commodities. No native game was started.");
    return;
}
var root=Path.GetFullPath(args[0]);var source=Path.GetFullPath(args[1]);Directory.CreateDirectory(root);
var workspacePath=Path.Combine(root,"workspace");Directory.CreateDirectory(workspacePath);
if(!File.Exists(Path.Combine(workspacePath,"state.json")))
{
    await using(var original=new StateStore(source))await original.Read(state=>{File.WriteAllBytes(Path.Combine(workspacePath,"state.json"),JsonSerializer.SerializeToUtf8Bytes(state,Wire.Json));return true;});
    foreach(var directory in new[]{"native-resources","native-business"})
    {
        var from=Path.Combine(source,directory);if(!Directory.Exists(from))continue;
        foreach(var file in Directory.EnumerateFiles(from,"*",SearchOption.AllDirectories)){var target=Path.Combine(workspacePath,directory,Path.GetRelativePath(from,file));Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.Copy(file,target);}
    }
}
var gamePort=FreePort();var adminPort=FreePort();
if(gamePort==adminPort)adminPort=FreePort();
var config=new ServerConfiguration(workspacePath,root,adminPort,Background:true,Online:new("127.0.0.1",gamePort));
var configPath=Path.Combine(root,"server.json");await File.WriteAllTextAsync(configPath,JsonSerializer.Serialize(config,Wire.Json));
await using var workspace=new OfflineWorkspace(workspacePath);
var owner=await workspace.OpenLocalOwnerSessionAsync();var admin=owner["token"]!.GetValue<string>();
var secret="Fixture-"+Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
var a=await workspace.CreateAccountAsync(admin,"a-"+Guid.NewGuid().ToString("N")+"@online.invalid","Online A",secret,"player");
var b=await workspace.CreateAccountAsync(admin,"b-"+Guid.NewGuid().ToString("N")+"@online.invalid","Online B",secret,"player");
foreach(var row in new[]{a,b})await workspace.InitializeNativeAsync(admin,row["id"]!.GetValue<string>(),"0");
var serverInfo=new ProcessStartInfo("dotnet"){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden};
foreach(var value in new[]{"exec","--runtimeconfig",Path.Combine(AppContext.BaseDirectory,"OnlineIntegration.runtimeconfig.json"),"--depsfile",Path.Combine(AppContext.BaseDirectory,"OnlineIntegration.deps.json"),typeof(OnlineGameHost).Assembly.Location,"--config",configPath})serverInfo.ArgumentList.Add(value);
using var server=Process.Start(serverInfo)??throw new IOException("Fixture server did not start.");
var checks=new List<object>();
void Check(bool condition,string name){if(!condition)throw new InvalidOperationException("FAIL "+name);checks.Add(new{name,passed=true});Console.WriteLine("PASS "+name);}
var profilePath=Path.Combine(root,"game-client.json");
try
{
    var deadline=DateTimeOffset.UtcNow.AddSeconds(20);
    while(!File.Exists(profilePath)){if(server.HasExited||DateTimeOffset.UtcNow>deadline)throw new IOException("Fixture server startup failed; see "+root);await Task.Delay(100);}
    var profile=JsonSerializer.Deserialize(await File.ReadAllTextAsync(profilePath),GameLinkJson.Default.OnlineServerProfile)!;
    await using var first=new OnlineConnection(profile);await using var second=new OnlineConnection(profile);
    OnlineGameLease firstLease=null!,secondLease=null!;
    while(true)
    {
        try{firstLease=await first.LoginAsync(a["email"]!.GetValue<string>(),secret);break;}
        catch(HttpRequestException)when(DateTimeOffset.UtcNow<deadline){await Task.Delay(100);}
    }
    secondLease=await second.LoginAsync(b["email"]!.GetValue<string>(),secret);
    Check(firstLease.RunId!=secondLease.RunId&&firstLease.NativeUserId!=secondLease.NativeUserId,"Two real HTTPS sessions have independent private identities");
    await using(var duplicate=new OnlineConnection(profile))
    {
        try{await duplicate.LoginAsync(a["email"]!.GetValue<string>(),secret);throw new Exception("Duplicate login accepted");}catch(GameServerException error){Check(error.Status==409,"Duplicate account game session rejected");}
    }
    await using(var wrong=new OnlineConnection(profile))
    {
        try{await wrong.LoginAsync(a["email"]!.GetValue<string>(),"incorrect");throw new Exception("Bad password accepted");}catch(GameServerException error){Check(error.Status==401,"Wrong password rejected");}
    }
    await using(var pin=new OnlineConnection(profile with{CertificateSha256=new string('0',64)}))
    {
        try{await pin.LoginAsync(a["email"]!.GetValue<string>(),secret);throw new Exception("Bad certificate accepted");}catch(HttpRequestException){Check(true,"Wrong TLS pin fails without insecure fallback");}
    }
    await using(var badToken=new OnlineConnection(profile))
    {
        badToken.Resume(firstLease with{Token=new string('0',64)});
        try{await badToken.InvokeAsync(new("exchange",Datagrams:[]));throw new Exception("Bad session accepted");}catch(GameServerException error){Check(error.Status==401,"Invalid session rejected");}
    }
    var identity=new Dictionary<string,string>{["machineToken"]="fixture-machine-a",["authToken"]="fixture-auth-a"};
    var login=await first.InvokeAsync(new("data","GET","/2.0/elite/user/login",identity,[]));
    Check(login.Status==200&&JsonNode.Parse(login.Payload)!["runId"]!.GetValue<ulong>()==firstLease.RunId,"Native login is completed by the server data authority");
    var secondLogin=await second.InvokeAsync(new("data","GET","/2.0/elite/user/login",new(){["machineToken"]="fixture-machine-b",["authToken"]="fixture-auth-b"},[]));
    Check(secondLogin.Status==200,"Second native data authority logs in independently");
    var save=await first.InvokeAsync(new("data","GET","/2.0/elite/commander/savegame",new(),[]));
    Check(save.Status==200&&JsonNode.Parse(save.Payload)!["commander"]!["id"]!.GetValue<uint>()==firstLease.NativeUserId,"Native save comes from the authenticated server account");
    var symbols=await first.InvokeAsync(new("data","GET","/2.0/elite/resources/commodities",new(),[]));
    var otherSymbols=await second.InvokeAsync(new("data","GET","/2.0/elite/resources/commodities",new(),[]));
    Check(symbols.Status==200&&symbols.Payload==otherSymbols.Payload,"Both clients read the same central commodity definitions");
    var access=JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root,"admin-access.json")))!;
    using var controlClient=new HttpClient();string? controlToken=access["key"]!.GetValue<string>();
    async Task<(int Status,JsonNode Data)> Control(string operation,JsonObject? input=null)
    {
        using var request=new HttpRequestMessage(HttpMethod.Post,$"http://127.0.0.1:{adminPort}/api/control");request.Headers.Authorization=new("Bearer",controlToken);
        request.Content=new StringContent(new JsonObject{["operation"]=operation,["arguments"]=input??new()}.ToJsonString(),System.Text.Encoding.UTF8,"application/json");
        using var response=await controlClient.SendAsync(request);return((int)response.StatusCode,JsonNode.Parse(await response.Content.ReadAsStringAsync())!);
    }
    var trusted=await Control("auth/local-owner");controlToken=trusted.Data["token"]!.GetValue<string>();
    var commander=await Control("commander",new(){["accountId"]=firstLease.AccountId});
    var entered=await first.InvokeAsync(new("data","PUT","/2.0/elite/commander/login",new(){["gameMode"]="open"},[]));Check(entered.Status==200,"Native Open Play mode is admitted by the shared authority");
    var busy=await Control("inventory/edit",new(){["accountId"]=firstLease.AccountId,["edit"]="credits",["version"]=commander.Data["version"]!.GetValue<string>(),["values"]=new JsonObject{["delta"]=23}});
    Check(busy.Status==409,"Management cannot edit a commander currently in the online world");
    var left=await first.InvokeAsync(new("data","PUT","/2.0/elite/commander/logout",new(){["cmdrId"]="Invalid"},[]));Check(left.Status==200,"Native logout releases the online edit gate");
    var edited=await Control("inventory/edit",new(){["accountId"]=firstLease.AccountId,["edit"]="credits",["version"]=commander.Data["version"]!.GetValue<string>(),["values"]=new JsonObject{["delta"]=23}});
    var updated=await first.InvokeAsync(new("data","GET","/2.0/elite/commander/savegame",new(),[]));
    Check(edited.Status==200&&JsonNode.Parse(updated.Payload)!["commander"]!["credits"]!.GetValue<int>()==23,"Server management edits are visible through the game data endpoint");
    var otherSave=await second.InvokeAsync(new("data","GET","/2.0/elite/commander/savegame",new(),[]));Check(JsonNode.Parse(otherSave.Payload)!["commander"]!["credits"]!.GetValue<int>()==0,"Editing one commander cannot alter another commander's balance");
    var galaxy=await Control("galaxy");var definition=galaxy.Data["definitions"]!.AsArray().Single(item=>item!["operation"]!.GetValue<string>()=="/2.0/elite/resources/commodities")!;
    var publicEdit=await Control("galaxy/edit",new(){["version"]=galaxy.Data["version"]!.GetValue<string>(),["kind"]="definition",["key"]="/2.0/elite/resources/commodities",["payload"]=definition["payload"]!.GetValue<string>()});
    var refreshedA=await first.InvokeAsync(new("data","GET","/2.0/elite/resources/commodities",new(),[]));var refreshedB=await second.InvokeAsync(new("data","GET","/2.0/elite/resources/commodities",new(),[]));
    Check(publicEdit.Status==200&&refreshedA.Payload==refreshedB.Payload&&File.Exists(Path.Combine(workspacePath,"galaxy.json")),"Public commodity publication is persistent and shared by both clients");
    var systemRow=publicEdit.Data["systems"]!.AsArray().First()!;var address=systemRow["address"]!.ToJsonString();
    var systemA=await first.InvokeAsync(new("data","GET","/2.0/elite/starsystem",new(){["systemAddr"]=address},[]));var systemB=await second.InvokeAsync(new("data","GET","/2.0/elite/starsystem",new(){["systemAddr"]=address},[]));
    Check(systemA.Status==200&&systemA.Payload==systemB.Payload,"System economic and political documents come from the same public world store");
    var stale=await Control("galaxy/edit",new(){["version"]=galaxy.Data["version"]!.GetValue<string>(),["kind"]="definition",["key"]="/2.0/elite/resources/commodities",["payload"]=definition["payload"]!.GetValue<string>()});
    Check(stale.Status==409,"Stale public world updates cannot overwrite a newer version");
    var playerLogin=await Control("auth/login",new(){["email"]=a["email"]!.GetValue<string>(),["password"]=secret});var originalToken=controlToken;controlToken=playerLogin.Data["token"]!.GetValue<string>();
    var forbidden=await Control("galaxy");Check(forbidden.Status==403,"Player accounts cannot read the administrator world editor");await Control("auth/logout");controlToken=originalToken;
    var roles=await first.InvokeAsync(new("data","GET","/2.0/elite/server/list",new(),[]));
    var roundTrip=JsonSerializer.Deserialize(JsonSerializer.Serialize(new GameLinkRequest("exchange",Datagrams:[]),GameLinkJson.Default.GameLinkRequest),GameLinkJson.Default.GameLinkRequest)!;
    Check(roundTrip.Body is null&&roundTrip.Method is null&&roundTrip.Path is null&&roundTrip.Fields is null&&roundTrip.Datagrams is {Length:0},"Native exchange format preserves null and empty byte arrays distinctly");
    using(var directory=JsonDocument.Parse(roles.Payload))
    {
        var key=Convert.FromHexString(directory.RootElement.GetProperty("servers")[0].GetProperty("encryptionKey").GetString()!);
        try
        {
            var sync=new XFEEliteDangerous.NativeFormat.Replication.EdServerSyncMessage(33837,91,0,8,0,false,3,0,ReadOnlyMemory<byte>.Empty,0,0,ReadOnlyMemory<byte>.Empty,false,true,332841,0);
            var packet=XFEEliteDangerous.NativeFormat.Replication.EdServerDatagramCodec.EncodeMode3(new(firstLease.RunId,false,1),XFEEliteDangerous.NativeFormat.Replication.EdServerControlMessageCodec.EncodeSyncLetter(sync),key);
            var reply=await first.InvokeAsync(new("exchange",Datagrams:[new(45000,46001,packet)]));
            Check(reply.Status==200&&reply.Datagrams?.Length==1,"Native handshake bytes traverse HTTPS and return an actual authority response");
        }
        finally{CryptographicOperations.ZeroMemory(key);}
    }
    var bridgeDirectory=Path.Combine(root,"bridge-probe");
    await using(var host=new OnlineBridgeHost(bridgeDirectory,first))
    {
        using var game=NativeGameBridge.Open(Path.Combine(bridgeDirectory,"data.bridge"));
        var result=await Task.Run(()=>game.Call(new("data","GET","/2.0/elite/commander/savegame",new(),[])));
        Check(result.Status==200,"Independent native IPC mapping reaches the real HTTPS server");
    }
    await first.DisposeAsync();await second.DisposeAsync();
    await using(var fresh=new OnlineConnection(profile)){await fresh.LoginAsync(a["email"]!.GetValue<string>(),secret);Check(true,"Disconnected account can establish a new session");}
    await File.WriteAllTextAsync(Path.Combine(root,"integration-report.json"),JsonSerializer.Serialize(new{passed=true,nativeClientsValidated=false,checks},Wire.Json));
    if(args.Contains("--native-ready"))
    {
        await PrepareNativeAsync(workspacePath,[a["id"]!.GetValue<string>(),b["id"]!.GetValue<string>()]);
        await using var nativeA=new OnlineConnection(profile);await using var nativeB=new OnlineConnection(profile);
        var leaseA=await nativeA.LoginAsync(a["email"]!.GetValue<string>(),secret);var leaseB=await nativeB.LoginAsync(b["email"]!.GetValue<string>(),secret);
        await File.WriteAllTextAsync(Path.Combine(root,"launch-a.private.json"),JsonSerializer.Serialize(new OnlineLaunch(profile,leaseA),GameLinkJson.Default.OnlineLaunch));
        await File.WriteAllTextAsync(Path.Combine(root,"launch-b.private.json"),JsonSerializer.Serialize(new OnlineLaunch(profile,leaseB),GameLinkJson.Default.OnlineLaunch));
        Console.WriteLine("Fixture server ready for bounded native validation: "+root);
        var keepAlive=new List<OnlineConnection>{nativeA,nativeB};
        for(var tick=0;tick<1800&&!server.HasExited&&!File.Exists(Path.Combine(root,"stop.fixture"));tick++)
        {
            foreach(var native in keepAlive.ToArray())try{await native.InvokeAsync(new("exchange",Datagrams:[]));}catch(GameServerException error)when(error.Status==401){keepAlive.Remove(native);}
            await Task.Delay(1000);
        }
    }
}
finally
{
    try
    {
        var access=JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root,"admin-access.json")))!;
        using var client=new HttpClient();using var request=new HttpRequestMessage(HttpMethod.Post,$"http://127.0.0.1:{adminPort}/api/shutdown");request.Headers.Authorization=new("Bearer",access["key"]!.GetValue<string>());using var response=await client.SendAsync(request);
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));await server.WaitForExitAsync(timeout.Token);
    }
    catch(Exception error){Console.Error.WriteLine("Fixture shutdown: "+error.GetType().Name);}
}
static int FreePort(){var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();try{return((IPEndPoint)listener.LocalEndpoint).Port;}finally{listener.Stop();}}

static async Task PrepareNativeAsync(string directory,string[] accounts)
{
    // Only the isolated fixture's two accounts are changed. The accepted local
    // commander's startup/tutorial metadata supplies an explicit existing-player
    // scenario; administrative initialization alone deliberately leaves it new.
    await using var store=new StateStore(directory);
    await store.Update(state=>
    {
        var seed=state.Accounts.Values.First(row=>!accounts.Contains(row.Id)&&row.NativeSave is not null&&row.NativeTutorial.HasChosenStarterPack);
        for(var index=0;index<accounts.Length;index++)
        {
            var row=state.Accounts[accounts[index]];var save=row.NativeSave!;
            var location=save.Snapshot.Location with{Position=save.Snapshot.Location.Position with{Y=seed.NativeSave!.Snapshot.Location.Position.Y+index*250}};
            save=save with{Profile=save.Profile with{StartupState=seed.NativeSave!.Profile.StartupState},
                Snapshot=save.Snapshot with{Location=location,Vessels=save.Snapshot.Vessels.Select(ship=>ship with{Location=location,LastLocation=location}).ToArray()}};
            row.NativeSave=save;row.Commander=save.CreateInventory(row,checked(row.Commander.Revision+1));
            row.NativeTutorial=JsonSerializer.Deserialize<NativeTutorialProfile>(JsonSerializer.Serialize(seed.NativeTutorial,Wire.Json),Wire.Json)!;
        }
        return true;
    });
    Console.WriteLine($"Prepared {accounts.Length} isolated native fixture(s) from accepted startup metadata.");
}
