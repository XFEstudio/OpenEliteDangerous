using XFEExtension.NetCore.AutoConfig;
using Microsoft.UI.Xaml;

namespace XFEEliteDangerous.Desktop;

[AutoLoadProfile]
public partial class SystemProfile : XFEProfile
{
    public SystemProfile()
    {
        ProfilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XFEEliteDangerous", "Desktop", "Profiles", nameof(SystemProfile));
        DefaultProfileOperationMode = ProfileOperationMode.Json;
    }
    [ProfileProperty] public static partial string WorkspaceDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XFEEliteDangerous", "Offline");
    [ProfileProperty] public static partial string GameDirectory { get; set; } = @"E:\Elite Dangerous";
    [ProfileProperty] public static partial string ServerAddress {get;set;}="http://127.0.0.1:48080/api/";
    [ProfileProperty] public static partial string OnlineServerAddress {get;set;}="https://localhost:48081/game/";
    [ProfileProperty] public static partial string OnlineCertificateSha256 {get;set;}="";
    [ProfileProperty] public static partial string PlayerEmail { get; set; } = "";
    [ProfileProperty] public static partial bool RememberAccount { get; set; } = true;
    [ProfileProperty] public static partial string Language { get; set; } = "system";
    [ProfileProperty] public static partial ElementTheme Theme { get; set; } = Microsoft.UI.Xaml.ElementTheme.Default;
}
