using XFEEliteDangerous.Client;
using Microsoft.UI.Xaml.Controls;
using CommunityToolkit.WinUI.Controls;

namespace XFEEliteDangerous.Desktop;

public sealed class PermissionsPage : DesktopPage
{
    private readonly ToggleSwitch credits = Toggle();
    private readonly ToggleSwitch cargo = Toggle();
    private readonly ToggleSwitch materials = Toggle();
    private readonly ToggleSwitch ships = Toggle();
    public PermissionsPage() : base(AppText.Get("AppShellPage.Text103"), AppText.Get("PermissionsPage.Text309"))
    {
        if (!DesktopContext.IsAdmin)
        {
            Body.Children.Add(new TextBlock { Text = AppText.Get("PermissionsPage.Text310") });
            return;
        }
        var group = new SettingsExpander { Header = AppText.Get("PermissionsPage.Text311"), Description = AppText.Get("PermissionsPage.Text312"), IsExpanded = true };
        group.Items.Add(Card(AppText.Get("PermissionsPage.Text313"), AppText.Get("PermissionsPage.Text314"), credits));
        group.Items.Add(Card(AppText.Get("InventoryPage.Text216"), AppText.Get("PermissionsPage.Text315"), cargo));
        group.Items.Add(Card(AppText.Get("InventoryPage.Text217"), AppText.Get("PermissionsPage.Text316"), materials));
        group.Items.Add(Card(AppText.Get("PermissionsPage.Text317"), AppText.Get("PermissionsPage.Text318"), ships));
        Body.Children.Add(group);
        Body.Children.Add(Horizontal(Button(AppText.Get("PermissionsPage.Text319"), () => RunAsync(LoadAsync)), Button(AppText.Get("PermissionsPage.Text320"), () => RunAsync(async () =>
        {
            if (!DesktopContext.IsAdmin) throw new InvalidOperationException(AppText.Get("PermissionsPage.Text321"));
            await DesktopContext.Local.UpdatePermissionsAsync(credits.IsOn, cargo.IsOn, materials.IsOn, ships.IsOn, Lifetime.Token);
            Success(AppText.Get("PermissionsPage.Text322"));
        }), true)));
        Loaded += async (_, _) => await RunAsync(LoadAsync);
    }
    private async Task LoadAsync()
    {
        if (!DesktopContext.IsAdmin) throw new InvalidOperationException(AppText.Get("PermissionsPage.Text321"));
        var data = await DesktopContext.Local.PermissionsAsync(Lifetime.Token);
        credits.IsOn = data.Flag("allowPlayerCredits"); cargo.IsOn = data.Flag("allowPlayerCargo");
        materials.IsOn = data.Flag("allowPlayerMaterials"); ships.IsOn = data.Flag("allowPlayerShipEditing");
    }
}
