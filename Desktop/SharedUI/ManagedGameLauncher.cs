using XFEEliteDangerous.Client;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.Json;
using XFEEliteDangerous.Core;

namespace XFEEliteDangerous.Desktop;

internal static class ManagedGameLauncher
{
    internal static async Task StartOnlineAsync(OnlineLaunch launch,CancellationToken cancel)
    {
        var file=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"XFEEliteDangerous","Server","deployment.json");
        var deployment=JsonNode.Parse(await File.ReadAllTextAsync(file,cancel))!;
        var info=new ProcessStartInfo(deployment["bootstrapperExe"]!.GetValue<string>())
        {UseShellExecute=false,RedirectStandardInput=true,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=deployment["workingDirectory"]!.GetValue<string>()};
        info.Environment["XFE_LANGUAGE"] = AppText.Language;
        foreach(var value in new[]{"--debug-solo","--runtime",deployment["nativeRuntime"]!.GetValue<string>(),"--online-stdin","true"})info.ArgumentList.Add(value);
        using var child=Process.Start(info)??throw new InvalidOperationException(AppText.Get("ManagedGameLauncher.Text288"));
        await child.StandardInput.WriteAsync(JsonSerializer.Serialize(launch,GameLinkJson.Default.OnlineLaunch).AsMemory(),cancel);child.StandardInput.Close();
        await Task.Delay(1500,cancel);
        if(child.HasExited&&child.ExitCode!=0)throw new InvalidOperationException(AppText.Get("ManagedGameLauncher.Text289"));
    }
    internal static async Task StartAsync(string account,CancellationToken cancel)
    {
        if(account.Length!=32||!account.All(Uri.IsHexDigit))throw new InvalidDataException(AppText.Get("ManagedGameLauncher.Text290"));
        var file=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"XFEEliteDangerous","Server","deployment.json");
        var deployment=JsonNode.Parse(await File.ReadAllTextAsync(file,cancel))!;
        var info=new ProcessStartInfo(deployment["bootstrapperExe"]!.GetValue<string>())
        {UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=deployment["workingDirectory"]!.GetValue<string>()};
        info.Environment["XFE_LANGUAGE"] = AppText.Language;
        foreach(var value in new[]{"--debug-solo","--workspace",deployment["workspace"]!.GetValue<string>(),"--account",account,"--runtime",deployment["nativeRuntime"]!.GetValue<string>()})info.ArgumentList.Add(value);
        var child=Process.Start(info)??throw new InvalidOperationException(AppText.Get("ManagedGameLauncher.Text291"));
        await Task.Delay(1500,cancel);
        if(child.HasExited&&child.ExitCode!=0)throw new InvalidOperationException(AppText.Get("ManagedGameLauncher.Text292"));
    }
}
