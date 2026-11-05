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
    var mode=args[0];if(mode is not("--launch" or "--debug-solo" or "--native-check" or "--runtime-check" or "--hot-read-check" or "--play-solo" or "--prepare-solo" or "--starter-check" or "--check-game"))throw new ArgumentException(AppText.Get("Program.Text004"));
    var options=new Dictionary<string,string>(StringComparer.Ordinal);
    for(var index=1;index<args.Length;index+=2)
    {
        if(index+1>=args.Length||args[index] is not("--workspace" or "--account" or "--runtime" or "--seconds" or "--visible" or "--server-profile" or "--online-stdin" or "--game" or "--steam-root")||!options.TryAdd(args[index],args[index+1]))throw new ArgumentException(AppText.Get("Program.Text005"));
    }
    // Anchor caller-relative inputs before a Windows file dialog can change the working directory.
    foreach(var key in new[]{"--workspace","--runtime","--server-profile"})
        if(options.TryGetValue(key,out var supplied))options[key]=Path.GetFullPath(supplied);
    var steamFixture=options.GetValueOrDefault("--steam-root");
    if(steamFixture is not null)
    {
        steamFixture=SafePaths.LocalAbsolute(steamFixture);SafePaths.NoReparseAncestors(steamFixture);
        if(mode is not("--starter-check" or "--check-game")||!SafePaths.Within(SafePaths.ArtifactsRoot(),steamFixture))
            throw new ArgumentException("A Steam metadata fixture is allowed only under this build's artifacts directory in diagnostic modes.");
    }
    var dataOnly=mode is "--prepare-solo" or "--runtime-check" or "--hot-read-check";
    var interactive=mode is "--play-solo" or "--launch" or "--debug-solo";
    using var selected=dataOnly?null:await GameSelection.ResolveAsync(options.GetValueOrDefault("--game"),steamFixture,interactive);
    if(!dataOnly&&selected is null){Console.WriteLine(AppText.Get("Launch.GameSelectionCancelled"));return 0;}
    if(mode=="--check-game")
    {
        Console.WriteLine(JsonSerializer.Serialize(new{executable=selected!.Image.Executable,source=selected.Source,build=OdysseyGame.Build,sha256=OdysseyGame.Sha256},ClientJson.Options));
        return 0;
    }
    var starter=mode is "--play-solo" or "--prepare-solo" or "--starter-check";
    var nativeCheck=mode is "--native-check" or "--starter-check";
    var workspace=LocalPaths.Directory(options.GetValueOrDefault("--workspace")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"XFEEliteDangerous",starter?"SoloStarter":"Offline"));
    options.TryGetValue("--account",out var account);
    var package=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,".."));
    if(starter)
    {
        if(account is not null||options.ContainsKey("--server-profile")||options.ContainsKey("--online-stdin"))
            throw new ArgumentException("The standalone SOLO launcher uses its own local commander.");
        if(mode=="--starter-check"&&(!options.ContainsKey("--workspace")||!SafePaths.Within(SafePaths.ArtifactsRoot(),workspace)))
            throw new ArgumentException("A starter check requires an isolated workspace under this build's artifacts directory.");
        account=await SoloStarterWorkspace.PrepareAsync(workspace,Path.Combine(package,"business","XFEEliteDangerous.ReadBusiness.dll"),
            Path.Combine(package,"business","operations.json"));
        Console.WriteLine(AppText.Get("Launch.StarterWorkspaceReady")+workspace);
        if(mode=="--prepare-solo")return 0;
    }
    await using var online=await OpenOnlineAsync(options,account);
    account=online?.Lease?.AccountId??account;
    if(account is null)throw new ArgumentException(AppText.Get("Program.Text006"));
    var library=LocalPaths.FilePath(options.GetValueOrDefault("--runtime")??(starter?Path.Combine(package,"native","XFEEliteDangerous.OfflineRuntime.dll"):
        Path.Combine(SafePaths.ArtifactsRoot(),"offline-runtime","win-x64","XFEEliteDangerous.OfflineRuntime.dll")));
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
    var visible=mode is "--launch" or "--debug-solo" or "--play-solo";
    if(options.TryGetValue("--visible",out var visibility))
    {
        if(!nativeCheck||!bool.TryParse(visibility,out visible))throw new ArgumentException(AppText.Get("Program.Text007"));
    }
    await using var session=await OwnedOfflineGame.CreateAsync(selected!,workspace,account,library,visible,nativeCheck||starter||mode=="--debug-solo",online,starter);
    session.Resume();Console.WriteLine((online is null?AppText.Get("Program.Text008"):AppText.Get("Program.Text009"))+"，PID="+session.ProcessId+AppText.Get("Program.Text010")+session.RuntimeDirectory);
    if(nativeCheck)cancel.CancelAfter(TimeSpan.FromSeconds(seconds));
    try{await session.WaitAsync(cancel.Token);}catch(OperationCanceledException){}
    if(mode=="--play-solo")
    {
        if(session.ExitCode is uint exitCode && exitCode!=0)
        {
            Console.Error.WriteLine(AppText.Get("Launch.StarterGameExited")+"0x"+exitCode.ToString("X8")+"; "+session.RuntimeDirectory);
            return 1;
        }
        return 0;
    }
    // This receipt asserts data completion only. It never declares native world or flight ready.
    Console.WriteLine(JsonSerializer.Serialize(new {nativeSoloValidated=false,localDataCompletions=session.HasExited?(ulong?)null:await session.TransfersAsync(),report=session.RuntimeDirectory},ClientJson.Options));
    return nativeCheck?2:0;
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
