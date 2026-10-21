using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using CommunityToolkit.WinUI.Controls;
using XFEEliteDangerous.Client;
using XFEExtension.NetCore.WinUIHelper.Utilities.Helper;

namespace XFEEliteDangerous.Desktop;

public sealed class SettingsPage : DesktopPage
{
    public SettingsPage() : base(AppText.Get("AppShellPage.Text106"), AppText.Get("SettingsPage.Text323"))
    {
        var appearance = new SettingsExpander { Header = AppText.Get("SettingsPage.Text324"), Description = AppText.Get("SettingsPage.Text325"), IsExpanded = true };
        var language = new ComboBox { Name = "DisplayLanguage", MinWidth = 200 };
        language.Items.Add(new ComboBoxItem { Content = AppText.Get("SettingsPage.Text326"), Tag = "system" });
        language.Items.Add(new ComboBoxItem { Content = "English", Tag = "en-US" });
        language.Items.Add(new ComboBoxItem { Content = "简体中文", Tag = "zh-CN" });
        language.SelectedItem = language.Items.OfType<ComboBoxItem>().FirstOrDefault(x => (string)x.Tag == SystemProfile.Language) ?? language.Items[0];
        language.SelectionChanged += (_, _) =>
        {
            if (language.SelectedItem is ComboBoxItem { Tag: string selected }) DesktopContext.ChangeLanguage(selected);
        };
        appearance.Items.Add(Card(AppText.Get("Language.Label"), AppText.Get("Language.Description"), language));
        var theme = new ComboBox { MinWidth = 160 };
        theme.Items.Add(new ComboBoxItem { Content = AppText.Get("SettingsPage.Text326"), Tag = ElementTheme.Default });
        theme.Items.Add(new ComboBoxItem { Content = AppText.Get("SettingsPage.Text327"), Tag = ElementTheme.Light });
        theme.Items.Add(new ComboBoxItem { Content = AppText.Get("SettingsPage.Text328"), Tag = ElementTheme.Dark });
        theme.SelectedIndex = (int)SystemProfile.Theme;
        theme.SelectionChanged += (_, _) =>
        {
            if (theme.SelectedItem is not ComboBoxItem { Tag: ElementTheme selected }) return;
            SystemProfile.Theme = selected; AppThemeHelper.ChangeTheme(selected); SystemProfile.SaveProfile();
        };
        appearance.Items.Add(Card(AppText.Get("SettingsPage.Text329"), AppText.Get("SettingsPage.Text330"), theme));
        var remember = Toggle(SystemProfile.RememberAccount);
        remember.Toggled += (_, _) =>
        {
            SystemProfile.RememberAccount = remember.IsOn;
            if (!remember.IsOn) SystemProfile.PlayerEmail = "";
            SystemProfile.SaveProfile();
        };
        appearance.Items.Add(Card(AppText.Get("AccountPage.Text077"), AppText.Get("SettingsPage.Text331"), remember));
        Body.Children.Add(appearance);
        var endpoint=new TextBox{Text=SystemProfile.ServerAddress,MinWidth=400};
        Body.Children.Add(Card(AppText.Get("SettingsPage.Text332"),AppText.Get("SettingsPage.Text333"),Horizontal(endpoint,Button(AppText.Get("SettingsPage.Text334"),()=>RunAsync(()=>
        {
            using var check=new XFEEliteDangerous.ManagementClient.ManagementApi(endpoint.Text.Trim());
            DesktopContext.ClearSession();SystemProfile.ServerAddress=check.Address.ToString();SystemProfile.SaveProfile();Success(AppText.Get("SettingsPage.Text335"));return Task.CompletedTask;
        })))));
        var files = new SettingsExpander { Header = AppText.Get("SettingsPage.Text336"), Description = AppText.Get("SettingsPage.Text337"), IsExpanded = true };
        var path = new TextBox { Text = SystemProfile.GameDirectory, MinWidth = 320 };
        files.Items.Add(Card(AppText.Get("SettingsPage.Text338"), AppText.Get("SettingsPage.Text339"), Horizontal(path, Button(AppText.Get("SettingsPage.Text340"), () => RunAsync(() =>
        {
            var directory = LocalPaths.Directory(path.Text.Trim());
            if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(AppText.Get("GameInstallationInspector.Text017"));
            SystemProfile.GameDirectory = directory; SystemProfile.SaveProfile(); Success(AppText.Get("SettingsPage.Text341")); return Task.CompletedTask;
        })))));
        files.Items.Add(Card(AppText.Get("SettingsPage.Text342"), AppText.Get("SettingsPage.Text343"), new TextBlock { Text = SystemProfile.WorkspaceDirectory, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap, MaxWidth = 580 }));
        Body.Children.Add(files);
        Body.Children.Add(Card(AppText.Get("Legal.Title"), AppText.Get("Legal.Notice"), new TextBlock()));
        if (DesktopContext.IsAdmin) AddWorkspaceConfiguration();
    }
    private void AddWorkspaceConfiguration()
    {
        var name = new TextBox { MinWidth = 320 };
        var registration = Toggle();
        var group = new SettingsExpander { Name = "WorkspaceConfiguration", Header = AppText.Get("SettingsPage.Text344"), Description = AppText.Get("SettingsPage.Text345"), IsExpanded = true };
        group.Items.Add(Card(AppText.Get("SettingsPage.Text346"), AppText.Get("SettingsPage.Text347"), name));
        group.Items.Add(Card(AppText.Get("SettingsPage.Text348"), AppText.Get("SettingsPage.Text349"), registration));
        Body.Children.Add(group);
        async Task ReadAsync()
        {
            if (!DesktopContext.IsAdmin) return;
            var data = await DesktopContext.Local.SettingsAsync(Lifetime.Token);
            name.Text = data.Text("serverName"); registration.IsOn = data.Flag("allowPlayerRegistration");
        }
        Body.Children.Add(Horizontal(Button(AppText.Get("SettingsPage.Text350"), () => RunAsync(ReadAsync)), Button(AppText.Get("SettingsPage.Text351"), () => RunAsync(async () =>
        {
            await DesktopContext.Local.UpdateSettingsAsync(name.Text.Trim(), SystemProfile.GameDirectory, registration.IsOn, Lifetime.Token);
            DesktopContext.State.ServerName = name.Text.Trim(); Success(AppText.Get("SettingsPage.Text352"));
        }), true)));
        Loaded += async (_, _) => await RunAsync(ReadAsync);
    }
}
