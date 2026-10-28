using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using XFEExtension.NetCore.WinUIHelper.Utilities;
using XFEExtension.NetCore.WinUIHelper.Utilities.Additions;
using XFEEliteDangerous.Client;
using System.Text.RegularExpressions;

namespace XFEEliteDangerous.Desktop;

/// <summary>Constructs native role-specific UI without activating a window, sending input or making HTTP requests.</summary>
public static class DesktopVerification
{
    private static string? progress;
    public static bool IsRunning { get; private set; }
    internal static void Trace(string value) { if (progress is not null) File.AppendAllText(progress, value + "\n"); }
    public static async Task RunAsync(string destination)
    {
        var path = Path.GetFullPath(destination);
        progress = path + ".progress";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(progress, "Unified desktop native verification started\n");
        IsRunning = true;
        var checks = new List<object>();
        var failures = new List<string>();
        var retained = new List<Page>();
        var state = DesktopContext.State;
        var prior = (state.IsLoggedIn, state.AccountRole, state.AccountId, state.DisplayName, state.CanEditCredits, state.CanEditCargo, state.CanEditMaterials, state.CanEditShips);
        var token = DesktopContext.Local.Token;
        var initialLanguage = AppText.Language;
        var previousPreference = SystemProfile.Language;
        void Check(string name, bool passed)
        {
            Trace(name + ": " + passed);
            checks.Add(new { name, passed });
            if (!passed) failures.Add(name);
        }
        void Scene(string role)
        {
            state.IsLoggedIn = false; state.AccountRole = "anonymous"; state.AccountId = ""; state.DisplayName = AppText.Get("AppShellPage.Text105");
            state.CanEditCredits = state.CanEditCargo = state.CanEditMaterials = state.CanEditShips = false;
            if (role != "anonymous")
            {
                DesktopContracts.ApplyPlayerAccount(new JsonObject { ["id"] = "verification-" + role, ["displayName"] = "UI Check", ["role"] = role });
                state.IsLoggedIn = true;
                if (role == "administrator") state.CanEditCredits = state.CanEditCargo = state.CanEditMaterials = state.CanEditShips = true;
            }
            DesktopContext.Changed();
        }
        try
        {
            Check("Window is hidden before validation", !DesktopContext.MainWindow.AppWindow.IsVisible);
            Check("Startup has no loaded bearer credentials", token is null);
            Check("No administrator configuration discovery exists", typeof(DesktopContext).Assembly.GetTypes().All(x => x.Name != "AdminConfigurationDiscovery"));
            Check("Profile contains no credentials or trusted role", !typeof(SystemProfile).GetProperties().Any(x => x.Name.Contains("password", StringComparison.OrdinalIgnoreCase) || x.Name.Contains("token", StringComparison.OrdinalIgnoreCase) || x.Name.Contains("role", StringComparison.OrdinalIgnoreCase)));
            Scene("anonymous");
            var shell = new AppShellPage(); retained.Add(shell);
            var navigation = Walk(shell).OfType<NavigationView>().Single();
            Check("Shell uses native TitleBar", Walk(shell).OfType<TitleBar>().Count() == 1);
            foreach (var role in new[] { "anonymous", "player", "administrator" })
            {
                Scene(role);
                bool HasNavigation(Type type) => navigation.MenuItems.OfType<NavigationViewItem>().Any(x => Target(x) == type.FullName);
                Check(role + ": inventory navigation follows login", HasNavigation(typeof(InventoryPage)) == (role != "anonymous"));
                Check(role + ": fleet navigation follows login", HasNavigation(typeof(FleetPage)) == (role != "anonymous"));
                Check(role + ": online login navigation is available", HasNavigation(typeof(OnlinePage)));
                Check(role+": shared galaxy navigation is admin-only",HasNavigation(typeof(GalaxyPage))==(role=="administrator"));
                Check(role + ": administrator navigation follows role", HasNavigation(typeof(PermissionsPage)) == (role == "administrator"));
                Check(role + ": account remains in footer", navigation.FooterMenuItems.OfType<NavigationViewItem>().Select(Target).SequenceEqual(new[] { typeof(AccountPage).FullName, typeof(SettingsPage).FullName }));
                Check(role + ": all navigation routes are registered", navigation.MenuItems.Concat(navigation.FooterMenuItems).OfType<NavigationViewItem>().All(x => Equals(x.GetValue(NavigationAddition.NavigateToProperty), typeof(NavigationHostPage).FullName) && Target(x) is string name && PageManager.PageDefinitions.ContainsKey(name)));
                foreach (var type in new[] { typeof(HomePage), typeof(AccountPage), typeof(SettingsPage), typeof(InventoryPage), typeof(FleetPage),typeof(OnlinePage),typeof(GalaxyPage), typeof(DiagnosticsPage), typeof(PermissionsPage) })
                {
                    var page = (DesktopPage)Activator.CreateInstance(type)!; retained.Add(page);
                    Check(role + ": " + type.Name + " native construction", page.Content is ScrollViewer);
                    var controls = Walk(page).ToArray();
                    Check(role + ": " + type.Name + " uses selected language", page.Language == initialLanguage);
                    if (initialLanguage == "en-US")
                        Check(role + ": " + type.Name + " has no untranslated Chinese labels", controls.SelectMany(Labels).All(text => text == "简体中文" || !Regex.IsMatch(text, @"\p{IsCJKUnifiedIdeographs}")));
                    if (type == typeof(SettingsPage))
                    {
                        Check(role + ": community settings cards", controls.OfType<SettingsCard>().Count() >= 3);
                        Check(role + ": grouped settings expanders", controls.OfType<SettingsExpander>().Count() >= 2);
                        Check(role + ": workspace configuration is admin-only", controls.OfType<SettingsExpander>().Any(x => x.Name == "WorkspaceConfiguration") == (role == "administrator"));
                        var languages = controls.OfType<ComboBox>().Single(x => x.Name == "DisplayLanguage");
                        Check(role + ": language selector has stable preference values", languages.Items.OfType<ComboBoxItem>().Select(x => x.Tag as string).SequenceEqual(new[] { "system", "en-US", "zh-CN" }));
                    }
                    if (type == typeof(AccountPage))
                    {
                        var names = controls.OfType<Button>().Select(x => x.Name).ToHashSet();
                        Check(role + ": anonymous card only offers login", names.Contains("LoginAccount") == (role == "anonymous"));
                        Check(role + ": logout follows login state", names.Contains("LogoutAccount") == (role != "anonymous"));
                        Check(role + ": managed account creation is admin-only", names.Contains("CreateManagedAccount") == (role == "administrator"));
                    }
                    if (type == typeof(InventoryPage))
                    {
                        Check(role + ": native inventory SelectorBar", controls.OfType<SelectorBar>().Single().Items.Count == 3);
                        Check(role + ": Journal import is admin-only", controls.OfType<Button>().Any(x => x.Name == "ImportJournal") == (role == "administrator"));
                        Check(role + ": account role editor is admin-only", controls.OfType<Button>().Any(x => x.Name == "EditAccountRole") == (role == "administrator"));
                        if (role == "player") { CheckInventoryPermissions(page, Check); Scene(role); }
                    }
                    if (type == typeof(PermissionsPage))
                        Check(role + ": permissions editor is admin-only", controls.OfType<ToggleSwitch>().Any() == (role == "administrator"));
                    if(type==typeof(FleetPage))
                    {
                        Check(role+": native ship selection",controls.OfType<ComboBox>().Any(value=>value.Name=="FleetShips"));
                        Check(role+": native outfitting slot cards",controls.OfType<GridView>().Any(value=>value.Name=="FleetSlots"));
                        Check(role+": outfitting categories and inspector",controls.OfType<ListView>().Any(value=>value.Name=="FleetCategories") && controls.OfType<Pivot>().Any(value=>value.Name=="FleetInspectorTabs"));
                        Check(role+": power priorities match the game scale",controls.OfType<NumberBox>().Any(value=>value.Name=="FleetModulePriority"&&value.Minimum==1&&value.Maximum==5));
                        Check(role+": engineering blueprint and experimental selectors",controls.OfType<ComboBox>().Any(value=>value.Name=="FleetEngineeringBlueprint")&&controls.OfType<ComboBox>().Any(value=>value.Name=="FleetExperimentalEffect"));
                        Check(role+": ammunition controls exist",controls.OfType<NumberBox>().Any(value=>value.Name=="FleetAmmoClip")&&controls.OfType<NumberBox>().Any(value=>value.Name=="FleetAmmoHopper"));
                        Check(role+": fleet creation and module storage controls",controls.OfType<Button>().Any(value=>value.Name=="CreateFleetShip")&&controls.OfType<Button>().Any(value=>value.Name=="StoreFleetModule"));
                    }
                    if(type==typeof(OnlinePage))
                    {
                        Check(role+": online credentials use a PasswordBox",controls.OfType<PasswordBox>().Any(value=>value.Name=="OnlinePassword"));
                        Check(role+": online certificate pin is explicit",controls.OfType<TextBox>().Any(value=>value.Name=="OnlineServerFingerprint"&&value.MaxLength==64));
                        Check(role+": online launch uses a separate action",controls.OfType<Button>().Any(value=>value.Name=="LaunchOnlineGame"));
                    }
                    if(type==typeof(GalaxyPage))
                    {
                        Check(role+": shared world editor is admin-only",controls.OfType<TextBox>().Any(value=>value.Name=="SharedGalaxyEditor")== (role=="administrator"));
                        Check(role+": public world publish is admin-only",controls.OfType<Button>().Any(value=>value.Name=="SaveSharedGalaxy")== (role=="administrator"));
                    }
                }
            }
            Scene("player");
            Check("Downgrading role removes administrator navigation", !navigation.MenuItems.OfType<NavigationViewItem>().Any(x => Target(x) == typeof(PermissionsPage).FullName));
            Check("Role change detaches old admin page and navigation history", shell.ViewModel.Navigation.NavigationService.Frame is Frame changedFrame && changedFrame.Content is NavigationHostPage { Content: AccountPage } && changedFrame.BackStack.Count == 0 && changedFrame.ForwardStack.Count == 0);
            DesktopContext.Local.Token = "current-construction-session";
            await DesktopContext.InvalidateSessionAsync("obsolete-construction-session");
            Check("Obsolete response cannot clear a newer session", state.IsLoggedIn && state.AccountRole == "player");
            await DesktopContext.InvalidateSessionAsync("current-construction-session");
            Check("Expired session clears role and permissions", !state.IsLoggedIn && state.AccountRole == "anonymous" && !state.CanEditCargo && !state.CanEditCredits && !state.CanEditMaterials && !state.CanEditShips);
            DesktopContext.ClearSession();
            Check("Logout removes privileged navigation and session", !DesktopContext.IsAdmin && !state.IsLoggedIn && DesktopContext.Local.Token is null && !navigation.MenuItems.OfType<NavigationViewItem>().Any(x => Target(x) == typeof(InventoryPage).FullName || Target(x) == typeof(PermissionsPage).FullName));
            CheckWireProjections(Check);
            Scene("administrator");
            var localizedFrame = shell.ViewModel.Navigation.NavigationService.Frame!;
            localizedFrame.Navigate(typeof(NavigationHostPage), typeof(SettingsPage).FullName + "|administrator");
            var beforeSwitch = localizedFrame.Content;
            var backCount = localizedFrame.BackStack.Count;
            DesktopContext.Local.Token = "localization-verification-session";
            DesktopContext.ChangeLanguage(initialLanguage == "en-US" ? "zh-CN" : "en-US", persist: false);
            await Task.Yield();
            Check("Language switch retains administrator session", state.IsLoggedIn && state.AccountRole == "administrator" && DesktopContext.Local.Token == "localization-verification-session");
            Check("Language switch preserves page and history", ReferenceEquals(beforeSwitch, localizedFrame.Content) && localizedFrame.Content is NavigationHostPage { Content: SettingsPage } && localizedFrame.BackStack.Count == backCount);
            Check("Language switch updates navigation labels", navigation.MenuItems.OfType<NavigationViewItem>().Any(x => Equals(x.Content, AppText.Get("AppShellPage.Text098"))));
            Check("Language switch updates current page", localizedFrame.Content is NavigationHostPage updated && updated.Content is SettingsPage changed && changed.Language == AppText.Language && Walk(changed).OfType<TextBlock>().Any(x => x.Text == AppText.Get("AppShellPage.Text106")));
            Check("Language preference is held by persistent profile", SystemProfile.Language == AppText.Language && typeof(SystemProfile).GetProperty("Language") is not null);
            DesktopContext.ChangeLanguage(initialLanguage, persist: false);
            Check("Window remains hidden after validation", !DesktopContext.MainWindow.AppWindow.IsVisible);
        }
        catch (Exception error) { failures.Add(error.GetType().Name + ": " + error.Message + "\n" + error.StackTrace); }
        finally
        {
            (state.IsLoggedIn, state.AccountRole, state.AccountId, state.DisplayName, state.CanEditCredits, state.CanEditCargo, state.CanEditMaterials, state.CanEditShips) = prior;
            DesktopContext.Local.Token = token;
            AppText.Apply(initialLanguage);
            SystemProfile.Language = previousPreference;
            IsRunning = false;
        }
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            application = "XFEEliteDangerous.Desktop",
            language = initialLanguage,
            rolesCovered = new[] { "anonymous", "player", "administrator" },
            windowActivated = false,
            syntheticInputSent = false,
            networkRequestsSent = false,
            gameStarted = false,
            passed = failures.Count == 0,
            checks,
            failures,
            limitation = "Native construction, role navigation, permission visibility and wire projections only; no activated window, popup interaction, local persistence acceptance or game integration is claimed."
        }, new JsonSerializerOptions { WriteIndented = true }));
        Environment.ExitCode = failures.Count == 0 ? 0 : 1;
        GC.KeepAlive(retained);
    }
    private static string? Target(NavigationViewItem item) => (item.GetValue(NavigationAddition.NavigateParameterProperty) as string)?.Split('|')[0];
    private static IEnumerable<string> Labels(object control) => control switch
    {
        SettingsExpander x => new[] { x.Header as string, x.Description as string }.OfType<string>(),
        SettingsCard x => new[] { x.Header as string, x.Description as string }.OfType<string>(),
        InfoBar x => new[] { x.Title, x.Message }.OfType<string>(),
        TextBlock x => [x.Text],
        TextBox x => new[] { x.Header as string, x.PlaceholderText }.OfType<string>(),
        PasswordBox x => new[] { x.Header as string, x.PlaceholderText }.OfType<string>(),
        SelectorBarItem x => [x.Text],
        ToggleSwitch x => new[] { x.OnContent as string, x.OffContent as string }.OfType<string>(),
        ContentControl x when x.Content is string text => [text],
        _ => []
    };
    private static void CheckInventoryPermissions(DesktopPage page, Action<string, bool> check)
    {
        var controls = Walk(page).ToArray();
        var selector = controls.OfType<SelectorBar>().Single();
        var buttons = controls.OfType<Button>().Where(x => x.Name is "EditCredits" or "EditInventory" or "AddInventory").ToDictionary(x => x.Name);
        bool Visible(string name) => buttons[name].Visibility == Visibility.Visible;
        void Rights(bool loggedIn, bool credits, bool cargo, bool materials)
        {
            var state = DesktopContext.State;
            state.AccountRole = loggedIn ? "player" : "anonymous"; state.IsLoggedIn = loggedIn; state.AccountId = "";
            state.CanEditCredits = credits; state.CanEditCargo = cargo; state.CanEditMaterials = materials;
            DesktopContext.Changed();
        }
        Rights(false, true, true, true);
        check("Signed-out users see no editing controls", !Visible("EditCredits") && !Visible("EditInventory") && !Visible("AddInventory"));
        Rights(true, false, false, false);
        check("Read-only users see no editing controls", !Visible("EditCredits") && !Visible("EditInventory") && !Visible("AddInventory"));
        Rights(true, true, false, false);
        check("Credit permission does not grant cargo edits", Visible("EditCredits") && !Visible("EditInventory") && !Visible("AddInventory"));
        Rights(true, false, true, false);
        check("Cargo permission shows cargo controls", !Visible("EditCredits") && Visible("EditInventory") && Visible("AddInventory"));
        selector.SelectedItem = selector.Items.First(x => (string?)x.Tag == "materials");
        check("Cargo permission does not grant material edits", !Visible("EditInventory") && !Visible("AddInventory"));
        Rights(true, false, false, true);
        check("Material permission shows material controls", Visible("EditInventory") && Visible("AddInventory"));
        selector.SelectedItem = selector.Items.First(x => (string?)x.Tag == "ships");
        check("Ship category hides quantity controls", !Visible("EditInventory") && !Visible("AddInventory"));
    }
    private static void CheckWireProjections(Action<string, bool> check)
    {
        check("Fractional uptime parses correctly", Math.Abs(JsonNode.Parse("{\"uptimeSeconds\":12.75}").Real("uptimeSeconds") - 12.75) < .001);
        var categories = new[] { "Raw", "Manufactured", "Encoded", "ShipLocker.Consumables", "ShipLocker.Items", "Backpack.Components", "Backpack.Data" };
        var materials = new JsonArray(categories.Select((category, i) => (JsonNode)new JsonObject { ["id"] = "item" + i, ["name"] = "Item " + i, ["category"] = category, ["quantity"] = i + 1 }).ToArray());
        var rows = DesktopContracts.Inventory(new JsonObject { ["materials"] = materials.DeepClone() }, "materials");
        check("Inventory preserves original material categories", rows.Select(x => x.Category).SequenceEqual(categories));
        check("Catalog array preserves material categories", DesktopContracts.Catalog(materials, "materials").Select(x => x.Category).SequenceEqual(categories));
        var body = DesktopContracts.Quantity("medkit", "ShipLocker.Consumables", 4, "42");
        check("Quantity payload retains category and string version", body.Text("category") == "ShipLocker.Consumables" && body["version"]!.GetValue<string>() == "42" && body["quantity"]!.GetValue<long>() == 4);
        check("Optional missing version is omitted", !DesktopContracts.Quantity("iron", "Raw", 2, null).ContainsKey("version"));
        var rejected = false;
        try { DesktopContracts.ApplyPlayerAccount(new JsonObject { ["id"] = "test", ["role"] = "owner" }); } catch (InvalidDataException) { rejected = true; }
        check("Unknown account roles are rejected", rejected);
    }
    private static IEnumerable<object> Walk(object root)
    {
        yield return root;
        IEnumerable<object> children = root switch
        {
            SettingsExpander expander => expander.Items.Cast<object>(),
            NavigationView navigation => navigation.MenuItems.Concat(navigation.FooterMenuItems).Concat(new[] { navigation.Content, navigation.PaneHeader }.OfType<object>()),
            Page page when page.Content is object child => [child],
            ScrollViewer viewer when viewer.Content is object child => [child],
            Border border when border.Child is object child => [child],
            Pivot pivot => pivot.Items.Cast<object>(),
            Panel panel => panel.Children.Cast<object>(),
            ContentControl content when content.Content is object child => [child],
            _ => []
        };
        foreach (var item in children)
            foreach (var descendant in Walk(item))
                yield return descendant;
    }
}
