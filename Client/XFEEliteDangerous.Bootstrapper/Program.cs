using System.Text.Json;
using XFEEliteDangerous.Client;
using XFEEliteDangerous.Bootstrapper;
using XFEEliteDangerous.Core;
using XFEEliteDangerous.OnlineClient;
using System.Text.Json.Nodes;

try
{
    args = AppText.ConfigureArguments(args);
    if(args is ["--loader-check-child"])
    {Console.WriteLine("loader-check-child-ready");await Task.Delay(Timeout.Infinite);return 0;}
    if(args is ["--loader-check"])
    {await OfflineNativeModule.RunCallbackChecksAsync();Console.WriteLine("PASS native loader: 64-bit callback return, early successful/failed thread exits, and exited child rejected before memory read. No game or desktop was opened.");return 0;}
    if(args.Length==0||args is ["--status"])
    {
        Console.WriteLine(JsonSerializer.Serialize(new {mode="in-process-offline",externalServiceRequired=false,nativeSoloReady=false,
            detail=AppText.Get("Program.Text003")},ClientJson.Options));return 0;
    }
    if(args is ["--inspect","--game",var game])
    {Console.WriteLine(JsonSerializer.Serialize(await new GameInstallationInspector().InspectAsync(game),ClientJson.Options));return 0;}
    var mode=args[0];if(mode is not("--launch" or "--debug-solo" or "--native-check" or "--runtime-check" or "--hot-read-check"))throw new ArgumentException(AppText.Get("Program.Text004"));
    var options=new Dictionary<string,string>(StringComparer.Ordinal);
    for(var index=1;index<args.Length;index+=2)
    {
        if(index+1>=args.Length||args[index] is not("--workspace" or "--account" or "--runtime" or "--seconds" or "--visible" or "--server-profile" or "--online-stdin")||!options.TryAdd(args[index],args[index+1]))throw new ArgumentException(AppText.Get("Program.Text005"));
    }
    var workspace=LocalPaths.Directory(options.GetValueOrDefault("--workspace")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"XFEEliteDangerous","Offline"));
    options.TryGetValue("--account",out var account);
    await using var online=await OpenOnlineAsync(options,account);
    account=online?.Lease?.AccountId??account;
    if(account is null)throw new ArgumentException(AppText.Get("Program.Text006"));
    var library=LocalPaths.FilePath(options.GetValueOrDefault("--runtime")??Path.Combine(SafePaths.ArtifactsRoot(),"offline-runtime","win-x64","XFEEliteDangerous.OfflineRuntime.dll"));
    if(mode=="--hot-read-check")
    {
        var report=Path.Combine(SafePaths.ArtifactsRoot(),"offline-hot-read-check-"+Guid.NewGuid().ToString("N"));
        await HotReadChecks.RunAsync(workspace,account,report);return 0;
    }
    if(mode=="--runtime-check")
    {
        var report=Path.Combine(SafePaths.ArtifactsRoot(),"offline-native-data-check-"+Guid.NewGuid().ToString("N"));
        RuntimeChecks.Run(library,workspace,account,report);Console.WriteLine("NativeAOT callbacks and local save parsing passed: "+report);return 0;
    }
    var seconds=options.TryGetValue("--seconds",out var secondsText)&&int.TryParse(secondsText,out var parsed)&&parsed is >=10 and <=3600?parsed:120;
    using var cancel=new CancellationTokenSource();Console.CancelKeyPress+=(_,eventArgs)=>{eventArgs.Cancel=true;cancel.Cancel();};
    var visible=mode is "--launch" or "--debug-solo";
    if(options.TryGetValue("--visible",out var visibility))
    {
        if(mode!="--native-check"||!bool.TryParse(visibility,out visible))throw new ArgumentException(AppText.Get("Program.Text007"));
    }
    await using var session=await OwnedOfflineGame.CreateAsync(workspace,account,library,visible,mode is "--native-check" or "--debug-solo",online);
    session.Resume();Console.WriteLine((online is null?AppText.Get("Program.Text008"):AppText.Get("Program.Text009"))+"，PID="+session.ProcessId+AppText.Get("Program.Text010")+session.RuntimeDirectory);
    if(mode=="--native-check")cancel.CancelAfter(TimeSpan.FromSeconds(seconds));
    try{await session.WaitAsync(cancel.Token);}catch(OperationCanceledException){}
    // This receipt asserts data completion only. It never declares native world or flight ready.
    Console.WriteLine(JsonSerializer.Serialize(new {nativeSoloValidated=false,localDataCompletions=session.HasExited?(ulong?)null:await session.TransfersAsync(),report=session.RuntimeDirectory},ClientJson.Options));
    return mode=="--native-check"?2:0;
}
catch(Exception error)when(error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
{Console.Error.WriteLine(error.Message);return 1;}

static async Task<OnlineConnection?> OpenOnlineAsync(Dictionary<string,string> options,string? account)
{
    if(options.TryGetValue("--online-stdin",out var stdin))
    {
        if(stdin!="true"||options.ContainsKey("--server-profile")||!Console.IsInputRedirected)throw new ArgumentException(AppText.Get("Program.Text011"));
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));var text=new char[65537];var length=0;
        while(length<text.Length){var read=await Console.In.ReadAsync(text.AsMemory(length),timeout.Token);if(read==0)break;length+=read;}
        if(length==text.Length)throw new InvalidDataException(AppText.Get("Program.Text012"));
        var launch=JsonSerializer.Deserialize(text.AsSpan(0,length),GameLinkJson.Default.OnlineLaunch)??throw new InvalidDataException(AppText.Get("Program.Text013"));
        var connection=new OnlineConnection(launch.Server);connection.Resume(launch.Lease);return connection;
    }
    if(!options.TryGetValue("--server-profile",out var profile))return null;
    var server=JsonSerializer.Deserialize(await File.ReadAllTextAsync(profile),GameLinkJson.Default.OnlineServerProfile)??throw new InvalidDataException(AppText.Get("Program.Text014"));
    if(account is null)throw new ArgumentException(AppText.Get("Program.Text015"));
    var file=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(profile))!,"admin-access.json");
    if(!File.Exists(file))throw new FileNotFoundException(AppText.Get("Program.Text016"),file);
    var access=JsonNode.Parse(await File.ReadAllTextAsync(file))!;var local=new OnlineConnection(server);
    try{await local.OpenLocalAsync(account,access["key"]!.GetValue<string>());return local;}catch{await local.DisposeAsync();throw;}
}
