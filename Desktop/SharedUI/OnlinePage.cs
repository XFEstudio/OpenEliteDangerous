using XFEEliteDangerous.Client;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using XFEEliteDangerous.Core;
using XFEEliteDangerous.OnlineClient;

namespace XFEEliteDangerous.Desktop;

public sealed class OnlinePage : DesktopPage
{
    private readonly TextBox address=new(){Text=SystemProfile.OnlineServerAddress,MinWidth=460,PlaceholderText=AppText.Get("OnlinePage.Text294")};
    private readonly TextBox fingerprint=new(){Text=SystemProfile.OnlineCertificateSha256,MinWidth=460,MaxLength=64,PlaceholderText=AppText.Get("OnlinePage.Text295")};
    private readonly TextBox email=new(){Text=SystemProfile.PlayerEmail,MinWidth=360,PlaceholderText=AppText.Get("OnlinePage.Text296")};
    private readonly PasswordBox password=new(){MinWidth=360,PlaceholderText=AppText.Get("OnlinePage.Text297")};
    public OnlinePage():base(AppText.Get("AppShellPage.Text099"),AppText.Get("OnlinePage.Text298"))
    {
        address.Name="OnlineServerAddress";fingerprint.Name="OnlineServerFingerprint";email.Name="OnlineEmail";password.Name="OnlinePassword";
        Body.Children.Add(Card(AppText.Get("OnlinePage.Text299"),AppText.Get("OnlinePage.Text300"),Vertical(address,fingerprint)));
        Body.Children.Add(Card(AppText.Get("OnlinePage.Text301"),AppText.Get("OnlinePage.Text302"),Vertical(email,password)));
        var connect=Button(AppText.Get("OnlinePage.Text303"),()=>RunAsync(ConnectAsync),true);connect.Name="LaunchOnlineGame";Body.Children.Add(connect);
        Body.Children.Add(new InfoBar{IsOpen=true,IsClosable=false,Severity=InfoBarSeverity.Informational,Title=AppText.Get("OnlinePage.Text304"),Message=AppText.Get("OnlinePage.Text305")});
        Loaded+=async(_,_)=>await RunAsync(async()=>
        {
            if(DesktopVerification.IsRunning||!string.IsNullOrWhiteSpace(fingerprint.Text))return;
            var file=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"XFEEliteDangerous","Server","game-client.json");
            if(!File.Exists(file))return;
            var local=JsonSerializer.Deserialize(await File.ReadAllTextAsync(file,Lifetime.Token),GameLinkJson.Default.OnlineServerProfile);
            if(local is not null){address.Text=local.Address;fingerprint.Text=local.CertificateSha256;}
        });
    }
    private static StackPanel Vertical(params UIElement[] elements){var panel=new StackPanel{Spacing=10};foreach(var element in elements)panel.Children.Add(element);return panel;}
    private async Task ConnectAsync()
    {
        var server=new OnlineServerProfile(address.Text.Trim(),fingerprint.Text.Trim());
        await using var connection=new OnlineConnection(server);
        var secret=password.Password;password.Password="";
        var lease=await connection.LoginAsync(email.Text.Trim(),secret,Lifetime.Token);
        SystemProfile.OnlineServerAddress=server.Address;SystemProfile.OnlineCertificateSha256=server.CertificateSha256;DesktopContext.Persist();
        await ManagedGameLauncher.StartOnlineAsync(new(server,lease),Lifetime.Token);connection.DetachLease();
        Success(AppText.Get("OnlinePage.Text306"));
    }
}
