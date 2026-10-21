using XFEEliteDangerous.Client;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using XFEExtension.NetCore.WinUIHelper.Utilities;

namespace XFEEliteDangerous.Desktop;

/// <summary>Compiled WinUI navigation activation boundary for the shared, programmatic feature pages.</summary>
public sealed partial class NavigationHostPage : Page
{
    public NavigationHostPage() => InitializeComponent();
    public void RefreshLanguage()
    {
        if (Content is DesktopPage previous)
        {
            var type = previous.GetType();
            Content = null;
            Content = (Page)Activator.CreateInstance(type)!;
        }
    }
    protected override void OnNavigatedTo(NavigationEventArgs args)
    {
        base.OnNavigatedTo(args);
        var name = (args.Parameter as string)?.Split('|')[0];
        if (name is null || !PageManager.PageDefinitions.TryGetValue(name, out var type) || !typeof(DesktopPage).IsAssignableFrom(type))
            throw new InvalidOperationException(AppText.Get("NavigationHostPage.Text293"));
        if ((type == typeof(PermissionsPage)||type==typeof(GalaxyPage)) && !DesktopContext.IsAdmin || (type == typeof(InventoryPage)||type==typeof(FleetPage)) && !DesktopContext.State.IsLoggedIn)
            type = typeof(AccountPage);
        Content = (Page)Activator.CreateInstance(type)!;
    }
}
