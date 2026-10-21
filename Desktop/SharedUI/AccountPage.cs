using XFEEliteDangerous.Client;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using CommunityToolkit.WinUI.Controls;

namespace XFEEliteDangerous.Desktop;

public sealed class AccountPage : DesktopPage
{
    private readonly StackPanel card = new() { Spacing = 16 };
    public AccountPage() : base(AppText.Get("AccountPage.Text065"), AppText.Get("AccountPage.Text066"))
    {
        Body.Children.Add(Card(AppText.Get("AccountPage.Text067"), "", card));
        Render();
        Loaded += async (_, _) =>
        {
            Render();
            if (DesktopContext.State.IsLoggedIn) await RunAsync(DesktopContext.RefreshPermissionsAsync);
        };
        DesktopContext.SessionChanged += Render;
        Unloaded += (_, _) => DesktopContext.SessionChanged -= Render;
    }
    private void Render()
    {
        card.Children.Clear();
        card.Children.Add(new PersonPicture { DisplayName = DesktopContext.State.DisplayName, Width = 64, Height = 64, HorizontalAlignment = HorizontalAlignment.Left });
        card.Children.Add(new TextBlock { Text = DesktopContext.State.DisplayName, FontSize = 24 });
        if (DesktopContext.State.IsLoggedIn)
        {
            card.Children.Add(new TextBlock { Text = DesktopContext.IsAdmin ? AppText.Get("AccountPage.Text068") : AppText.Get("AccountPage.Text069"), Opacity = .7 });
            card.Children.Add(new TextBlock { Text = AppText.Get("AccountPage.Text070") + DesktopContext.State.AccountId, Opacity = .7 });
            if (DesktopContext.IsAdmin)
            {
                var create = Button(AppText.Get("AccountPage.Text071"), () => RunAsync(CreateManagedAccountAsync)); create.Name = "CreateManagedAccount"; card.Children.Add(create);
            }
            var logout = Button(AppText.Get("AccountPage.Text072"), () => RunAsync(async () =>
            {
                try { await Task.Run(DesktopContext.Local.Logout, Lifetime.Token); }
                finally { DesktopContext.ClearSession(); }
            }));
            logout.Name = "LogoutAccount"; card.Children.Add(logout);
        }
        else
        {
            card.Children.Add(new TextBlock { Text = AppText.Get("AccountPage.Text073"), TextWrapping = TextWrapping.Wrap, Opacity = .7, MaxWidth = 480 });
            var login = Button(AppText.Get("AccountPage.Text074"), () => RunAsync(LoginAsync), true); login.Name = "LoginAccount"; card.Children.Add(login);
        }
    }
    private async Task LoginAsync()
    {
        var email = new TextBox { Header = AppText.Get("AccountPage.Text075"), Text = SystemProfile.RememberAccount ? SystemProfile.PlayerEmail : "", MinWidth = 360 };
        var password = new PasswordBox { Header = AppText.Get("AccountPage.Text076") };
        var remember = new CheckBox { Content = AppText.Get("AccountPage.Text077"), IsChecked = SystemProfile.RememberAccount };
        var register = new Button { Content = AppText.Get("AccountPage.Text078") };
        var panel = new StackPanel { Spacing = 14 };
        panel.Children.Add(email); panel.Children.Add(password); panel.Children.Add(remember); panel.Children.Add(register);
        var createAccount = false;
        var dialog = new ContentDialog { Title = AppText.Get("AccountPage.Text079"), Content = panel, PrimaryButtonText = AppText.Get("AccountPage.Text079"), CloseButtonText = AppText.Get("AccountPage.Text080"), DefaultButton = ContentDialogButton.Primary };
        register.Click += (_, _) => { createAccount = true; dialog.Hide(); };
        var result = await DialogService.ShowAsync(dialog);
        if (createAccount) { password.Password = ""; await RegisterAsync(); return; }
        if (result != ContentDialogResult.Primary) { password.Password = ""; return; }
        var enteredPassword = password.Password; password.Password = "";
        var data = await DesktopContext.Local.LoginAsync(email.Text.Trim(), enteredPassword, Lifetime.Token);
        var issuedToken = data.Text("token");
        if (issuedToken.Length != 64) throw new InvalidDataException(AppText.Get("AccountPage.Text081"));
        DesktopContracts.ApplyPlayerAccount(data["account"] ?? throw new InvalidDataException(AppText.Get("AccountPage.Text082")));
        DesktopContext.Local.Token = issuedToken;
        DesktopContext.State.IsLoggedIn = true;
        SystemProfile.RememberAccount = remember.IsChecked == true;
        SystemProfile.PlayerEmail = SystemProfile.RememberAccount ? email.Text.Trim() : "";
        SystemProfile.SaveProfile();
        await DesktopContext.RefreshPermissionsAsync();
        Success(AppText.Get("AccountPage.Text083"));
    }
    private async Task RegisterAsync()
    {
        var name = new TextBox { Header = AppText.Get("AccountPage.Text084"), MinWidth = 360 };
        var email = new TextBox { Header = AppText.Get("AccountPage.Text075") };
        var password = new PasswordBox { Header = AppText.Get("AccountPage.Text085") };
        var confirm = new PasswordBox { Header = AppText.Get("AccountPage.Text086") };
        var panel = new StackPanel { Spacing = 14 }; panel.Children.Add(name); panel.Children.Add(email); panel.Children.Add(password); panel.Children.Add(confirm);
        if (await ShowAsync(AppText.Get("AccountPage.Text087"), panel, AppText.Get("AccountPage.Text088")) != ContentDialogResult.Primary) return;
        if (password.Password != confirm.Password) throw new InvalidOperationException(AppText.Get("AccountPage.Text089"));
        var enteredPassword = password.Password; password.Password = confirm.Password = "";
        await DesktopContext.Local.RegisterAsync(email.Text.Trim(), name.Text.Trim(), enteredPassword, Lifetime.Token);
        if (SystemProfile.RememberAccount) { SystemProfile.PlayerEmail = email.Text.Trim(); SystemProfile.SaveProfile(); }
        Success(AppText.Get("AccountPage.Text090"));
    }

    private async Task CreateManagedAccountAsync()
    {
        if (!DesktopContext.IsAdmin) throw new InvalidOperationException(AppText.Get("AccountPage.Text091"));
        var name = new TextBox { Header = AppText.Get("AccountPage.Text084"), MinWidth = 360 };
        var email = new TextBox { Header = AppText.Get("AccountPage.Text075") };
        var password = new PasswordBox { Header = AppText.Get("AccountPage.Text092") };
        var role = new ComboBox { Header = AppText.Get("AccountPage.Text093"), MinWidth = 260 };
        role.Items.Add(new ComboBoxItem { Content = AppText.Get("AccountPage.Text094"), Tag = "player" });
        role.Items.Add(new ComboBoxItem { Content = AppText.Get("AccountPage.Text095"), Tag = "administrator" });
        role.SelectedIndex = 0;
        var panel = new StackPanel { Spacing = 14 }; panel.Children.Add(name); panel.Children.Add(email); panel.Children.Add(password);
        panel.Children.Add(role);
        if (await ShowAsync(AppText.Get("AccountPage.Text096"), panel, AppText.Get("AccountPage.Text088")) != ContentDialogResult.Primary) return;
        var secret = password.Password; password.Password = "";
        await DesktopContext.Local.CreateAccountAsync(email.Text.Trim(), name.Text.Trim(), secret, (role.SelectedItem as ComboBoxItem)?.Tag as string ?? "player", Lifetime.Token);
        Success(AppText.Get("AccountPage.Text097"));
    }
}
