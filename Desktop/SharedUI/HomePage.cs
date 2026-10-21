using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using XFEEliteDangerous.Client;

namespace XFEEliteDangerous.Desktop;

public sealed class HomePage : DesktopPage
{
    private readonly TextBlock summary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock game = new() { TextWrapping = TextWrapping.Wrap };
    public HomePage() : base(AppText.Get("AppShellPage.Text098"), AppText.Get("HomePage.Text195"))
    {
        Body.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Success, Title = AppText.Get("HomePage.Text196"), Message = AppText.Get("HomePage.Text197") });
        Body.Children.Add(Card(AppText.Get("DesktopContext.Text108"), SystemProfile.ServerAddress, Horizontal(Button(AppText.Get("HomePage.Text198"),()=>RunAsync(async()=>{if(!await DesktopContext.Local.ConnectLocalOwnerAsync())throw new InvalidOperationException(AppText.Get("HomePage.Text199"));DesktopContext.Changed();})),Button(AppText.Get("HomePage.Text200"), () => RunAsync(RefreshAsync), true))));
        Body.Children.Add(summary);
        Body.Children.Add(Card(AppText.Get("HomePage.Text201"), SystemProfile.GameDirectory, Button(AppText.Get("HomePage.Text202"), () => RunAsync(InspectAsync))));
        Body.Children.Add(game);
        Loaded += async (_, _) => await RunAsync(RefreshAsync);
    }
    private async Task RefreshAsync()
    {
        var data = await DesktopContext.Local.StatusAsync(Lifetime.Token);
        DesktopContext.State.ServerName = data.Text("name", AppText.Get("DesktopContext.Text107"));
        DesktopContext.State.ConnectionStatus = AppText.Get("HomePage.Text203");
        summary.Text = AppText.Format("HomePage.Text204", (DesktopContext.State.ServerName), (data.Number("accounts")), (data.Text("storage"))) + (data.Flag("firstAccountRequired") ? AppText.Get("HomePage.Text205") : AppText.Get("HomePage.Text206"));
    }
    private async Task InspectAsync()
    {
        var directory = SystemProfile.GameDirectory;
        var result = await Task.Run(() => new GameInstallationInspector().InspectAsync(directory, Lifetime.Token), Lifetime.Token);
        game.Text = string.Join("\n", result.Products.Select(product => $"{product.Name ?? AppText.Get("HomePage.Text207")} · {product.Version ?? AppText.Get("HomePage.Text208")} · {product.Pe?.Machine ?? AppText.Get("HomePage.Text209")}"))
            + "\n" + string.Join("\n", result.Warnings.Concat(result.Products.SelectMany(product => product.Warnings)));
    }
}
