using XFEEliteDangerous.Client;



using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using XFEExtension.NetCore.WinUIHelper.Utilities;
using XFEExtension.NetCore.WinUIHelper.Utilities.Helper;

namespace XFEEliteDangerous.Desktop;

public static class DesktopContext
{
    public static bool IsAdmin => State.IsLoggedIn && State.AccountRole == "administrator";
    public static Window MainWindow { get; set; } = null!;
    public static Microsoft.UI.Dispatching.DispatcherQueue? Dispatcher { get; set; }
    public static DesktopState State { get; } = new();
    public static LocalClient Local { get; } = new();
    public static event Action? SessionChanged;
    public static event Action? LanguageChanged;
    public static void ChangeLanguage(string preference, bool persist = true)
    {
        var oldLanguage = AppText.Language;
        var oldCatalog = AppText.Catalog(oldLanguage);
        SystemProfile.Language = AppText.NormalizePreference(preference);
        AppText.Apply(SystemProfile.Language);
        State.RefreshLocalizedLabels();
        if (!State.IsLoggedIn) State.DisplayName = AppText.Get("AppShellPage.Text105");
        if (State.ServerName == oldCatalog["DesktopContext.Text107"]) State.ServerName = AppText.Get("DesktopContext.Text107");
        foreach (var key in new[] { "DesktopContext.Text108", "HomePage.Text203" })
            if (State.ConnectionStatus == oldCatalog[key]) { State.ConnectionStatus = AppText.Get(key); break; }
        if (persist) SystemProfile.SaveProfile();
        LanguageChanged?.Invoke();
    }
    public static string Role => State.RoleLabel;
    public static void Initialize()
    {

        State.ServerName = AppText.Get("DesktopContext.Text107");
        State.DisplayName = AppText.Get("AppShellPage.Text105");
        foreach (var type in new[] { typeof(NavigationHostPage), typeof(AppShellPage), typeof(HomePage), typeof(InventoryPage), typeof(FleetPage), typeof(OnlinePage),typeof(GalaxyPage), typeof(AccountPage), typeof(SettingsPage), typeof(DiagnosticsPage), typeof(PermissionsPage) })
            PageManager.RegisterPage(type);
    }
    public static void Changed() => SessionChanged?.Invoke();
    public static void ClearSession()
    {
        Local.Logout();
        State.IsLoggedIn = false;
        State.AccountRole = "anonymous";
        State.AccountId = "";
        State.DisplayName = AppText.Get("AppShellPage.Text105");
        State.CanEditCargo = State.CanEditMaterials = State.CanEditCredits = State.CanEditShips = false;
        Changed();
    }
    public static void Persist()
    {

        SystemProfile.SaveProfile();
    }
    public static async Task InvalidateSessionAsync(string? expectedToken)
    {
        void ClearCurrent() { if (Local.Token == expectedToken) ClearSession(); }
        var dispatcher = Dispatcher;
        if (dispatcher is null || dispatcher.HasThreadAccess) { ClearCurrent(); return; }
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryEnqueue(() =>
        {
            try { ClearCurrent(); done.SetResult(); }
            catch (Exception error) { done.SetException(error); }
        })) return;
        await done.Task.ConfigureAwait(false);
    }
    public static async Task RefreshPermissionsAsync()
    {
        if (State.IsLoggedIn)
        {
            var session = Local.Token;
            var account = await Local.ProfileAsync();
            if (Local.Token != session) return;
            DesktopContracts.ApplyPlayerAccount(account);
            if (IsAdmin) State.CanEditCargo = State.CanEditMaterials = State.CanEditCredits = State.CanEditShips = true;
            else
            {
                var data = await Local.PermissionsAsync();
                if (Local.Token != session) return;
                State.CanEditCargo = data.Flag("allowPlayerCargo");
                State.CanEditMaterials = data.Flag("allowPlayerMaterials");
                State.CanEditCredits = data.Flag("allowPlayerCredits");
                State.CanEditShips = data.Flag("allowPlayerShipEditing");
            }
        }
        Changed();
    }
    public static void Log(Exception error)
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XFEEliteDangerous", "Desktop", "Logs");
            Directory.CreateDirectory(path);
            var message = error.Message;
            if (!string.IsNullOrEmpty(Local.Token)) message = message.Replace(Local.Token, "[redacted]", StringComparison.Ordinal);
            File.AppendAllText(Path.Combine(path, "desktop.log"), $"{DateTimeOffset.Now:O} {error.GetType().Name}: {message}\n{error.StackTrace}\n");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public partial class DesktopState : ObservableObject
{
    public void RefreshLocalizedLabels() => OnPropertyChanged(nameof(RoleLabel));

    [ObservableProperty] public partial string ServerName { get; set; } = "";
    [ObservableProperty] public partial string DisplayName { get; set; } = "";
    [ObservableProperty] public partial string AccountId { get; set; } = "";
    [ObservableProperty] public partial string ConnectionStatus { get; set; } = AppText.Get("DesktopContext.Text108");
    [ObservableProperty, NotifyPropertyChangedFor(nameof(RoleLabel))] public partial bool IsLoggedIn { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(RoleLabel))] public partial string AccountRole { get; set; } = "anonymous";
    public string RoleLabel => IsLoggedIn && AccountRole == "administrator" ? AppText.Get("DesktopContext.Text109") : AppText.Get("DesktopContext.Text110");
    [ObservableProperty] public partial bool CanEditCredits { get; set; }
    [ObservableProperty] public partial bool CanEditCargo { get; set; }
    [ObservableProperty] public partial bool CanEditMaterials { get; set; }
    [ObservableProperty] public partial bool CanEditShips { get; set; }
}

public static class JsonAccess
{
    public static string Text(this JsonNode? node, string name, string fallback = "") => node?[name]?.ToString() ?? fallback;
    public static bool Flag(this JsonNode? node, string name) => bool.TryParse(node?[name]?.ToString(), out var value) && value;
    public static long Number(this JsonNode? node, string name) => long.TryParse(node?[name]?.ToString(), out var value) ? value : 0;
    public static IEnumerable<JsonNode> Rows(this JsonNode? node) => node is JsonArray array ? array.OfType<JsonNode>() : [];
}
