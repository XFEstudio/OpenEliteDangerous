using XFEEliteDangerous.Client;
using System.Text.Json;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;

namespace XFEEliteDangerous.Desktop;

public sealed class DiagnosticsPage : DesktopPage
{
    private readonly TextBox output = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 280, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono") };
    public DiagnosticsPage() : base(AppText.Get("AppShellPage.Text104"), AppText.Get("DiagnosticsPage.Text116"))
    {
        Body.Children.Add(Button(AppText.Get("DiagnosticsPage.Text117"), () => RunAsync(RefreshAsync), true)); Body.Children.Add(output);
        Loaded += async (_, _) => await RunAsync(RefreshAsync);
    }
    private async Task RefreshAsync()
    {
        var data = await DesktopContext.Local.StatusAsync(Lifetime.Token);
        output.Text = await Task.Run(() => data.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Lifetime.Token);
    }
}