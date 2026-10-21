using XFEEliteDangerous.Client;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using XFEExtension.NetCore.WinUIHelper.Interface.Services;
using XFEExtension.NetCore.WinUIHelper.Utilities;
using XFEExtension.NetCore.WinUIHelper.Utilities.Additions;

namespace XFEEliteDangerous.Desktop;

public partial class AppShellViewModel : ObservableObject
{
    public DesktopState State => DesktopContext.State;
    public INavigationViewService Navigation { get; } = ServiceManager.GetService<INavigationViewService>();
    public IMessageService Messages { get; } = ServiceManager.GetService<IMessageService>();
    public ILoadingService Loading { get; } = ServiceManager.GetService<ILoadingService>();
}

public sealed class AppShellPage : Page
{
    public AppShellViewModel ViewModel { get; } = new();
    private readonly NavigationView navigation = new() { IsSettingsVisible = false, IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed, PaneDisplayMode = NavigationViewPaneDisplayMode.Auto, OpenPaneLength = 244, AlwaysShowHeader = false };
    private string identity = "";
    public AppShellPage()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var title = new TitleBar { Height = 48, IconSource = new FontIconSource { Glyph = "\uE7C3" } };
        title.SetBinding(TitleBar.TitleProperty, new Binding { Source = ViewModel.State, Path = new PropertyPath(nameof(DesktopState.ServerName)) });
        title.SetBinding(TitleBar.SubtitleProperty, new Binding { Source = ViewModel.State, Path = new PropertyPath(nameof(DesktopState.RoleLabel)) });
        var server = new TextBlock { Margin = new Thickness(12, 0, 24, 0), VerticalAlignment = VerticalAlignment.Center, MaxWidth = 280, TextTrimming = TextTrimming.CharacterEllipsis };
        server.SetBinding(TextBlock.TextProperty, new Binding { Source = ViewModel.State, Path = new PropertyPath(nameof(DesktopState.ServerName)) });
        title.RightHeader = server;
        root.Children.Add(title);
        Grid.SetRow(navigation, 1); root.Children.Add(navigation);
        var header = new StackPanel { Spacing = 8, Margin = new Thickness(24, 20, 12, 28) };
        header.Children.Add(new TextBlock { Text = "OPEN ELITE DANGEROUS", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, CharacterSpacing = 100, FontSize = 15, TextWrapping = TextWrapping.Wrap });
        var role = new TextBlock { Opacity = .6 };
        role.SetBinding(TextBlock.TextProperty, new Binding { Source = ViewModel.State, Path = new PropertyPath(nameof(DesktopState.RoleLabel)) });
        header.Children.Add(role); navigation.PaneHeader = header;
        var frame = new Frame();
        var overlay = new Grid(); overlay.Children.Add(frame); navigation.Content = overlay;
        var messages = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(24), MaxWidth = 600 };
        Grid.SetRow(messages, 1); root.Children.Add(messages);
        var global = new Grid { Visibility = Visibility.Collapsed };
        var text = new TextBlock();
        global.Children.Add(text); Grid.SetRow(global, 1); root.Children.Add(global);
        Content = root;
        // Native navigation uses the compiled host; feature pages are instantiated by the host.
        DesktopContext.MainWindow.SetTitleBar(title);
        ViewModel.Navigation.Initialize(navigation, frame);
        ViewModel.Messages.Initialize(messages, DispatcherQueue);
        ViewModel.Loading.Initialize(overlay, global, text, DispatcherQueue, ViewModel.Navigation.NavigationService);
        RefreshNavigation();
        identity = CurrentIdentity;
        ViewModel.Navigation.NavigateTo<NavigationHostPage>(Parameter(typeof(HomePage)));
        DesktopContext.SessionChanged += SessionChanged;
        DesktopContext.LanguageChanged += LanguageChanged;
        Unloaded += (_, _) => { DesktopContext.SessionChanged -= SessionChanged; DesktopContext.LanguageChanged -= LanguageChanged; };
    }
    private void LanguageChanged()
    {
        Language = AppText.Language;
        RefreshNavigation();
        if (ViewModel.Navigation.NavigationService.Frame?.Content is NavigationHostPage host) host.RefreshLanguage();
    }
    private static string CurrentIdentity => DesktopContext.State.AccountRole + ":" + DesktopContext.State.IsLoggedIn + ":" + DesktopContext.State.AccountId;
    private static string Parameter(Type page) => page.FullName + "|" + DesktopContext.State.AccountRole;
    private void SessionChanged()
    {
        var changed = identity != CurrentIdentity;
        identity = CurrentIdentity;
        RefreshNavigation();
        if (changed)
        {
            // Detach previous role-specific controls before navigating, even when the destination type is unchanged.
            var frame = ViewModel.Navigation.NavigationService.Frame;
            if (frame?.Content is NavigationHostPage previous) previous.Content = null;
            if (frame is not null)
            {
                frame.Navigate(typeof(NavigationHostPage), Parameter(typeof(AccountPage)));
                frame.BackStack.Clear(); frame.ForwardStack.Clear();
            }
        }
    }
    private void RefreshNavigation()
    {
        navigation.MenuItems.Clear(); navigation.FooterMenuItems.Clear();
        Add(navigation.MenuItems, AppText.Get("AppShellPage.Text098"), "\uE80F", typeof(HomePage));
        Add(navigation.MenuItems, AppText.Get("AppShellPage.Text099"), "\uE774", typeof(OnlinePage));
        if(DesktopContext.IsAdmin)Add(navigation.MenuItems,AppText.Get("AppShellPage.Text100"),"\uE774",typeof(GalaxyPage));
        if (DesktopContext.State.IsLoggedIn) Add(navigation.MenuItems, AppText.Get("AppShellPage.Text101"), "\uE8B7", typeof(InventoryPage));
        if (DesktopContext.State.IsLoggedIn) Add(navigation.MenuItems, AppText.Get("AppShellPage.Text102"), "\uE7C3", typeof(FleetPage));
        if (DesktopContext.IsAdmin) Add(navigation.MenuItems, AppText.Get("AppShellPage.Text103"), "\uE72E", typeof(PermissionsPage));
        Add(navigation.MenuItems, AppText.Get("AppShellPage.Text104"), "\uE9D9", typeof(DiagnosticsPage));
        var account = Add(navigation.FooterMenuItems, AppText.Get("AppShellPage.Text105"), "\uE77B", typeof(AccountPage));
        account.SetBinding(ContentControl.ContentProperty, new Binding { Source = ViewModel.State, Path = new PropertyPath(nameof(DesktopState.DisplayName)) });
        Add(navigation.FooterMenuItems, AppText.Get("AppShellPage.Text106"), "\uE713", typeof(SettingsPage));
    }
    private static NavigationViewItem Add(IList<object> items, string text, string glyph, Type page)
    {
        var item = new NavigationViewItem { Content = text, Icon = new FontIcon { Glyph = glyph } };
        item.SetValue(NavigationAddition.NavigateToProperty, typeof(NavigationHostPage).FullName!);
        item.SetValue(NavigationAddition.NavigateParameterProperty, Parameter(page));
        items.Add(item);
        return item;
    }
}
