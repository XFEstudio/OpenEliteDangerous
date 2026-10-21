using XFEEliteDangerous.Core;
using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using System.Text.Json;
using XFEEliteDangerous.Client;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace XFEEliteDangerous.Desktop;

public sealed record AccountChoice(string Id, string Email, string Name, string Role = "player")
{
    public string Label => $"{Name} · {(Role == "administrator" ? AppText.Get("AccountPage.Text095") : AppText.Get("AccountPage.Text094"))} · {Email}";
}
public sealed record InventoryRow(string Id, string Name, long Quantity, string Detail, string Category = "")
{
    public string Subtitle => $"{Detail} · {Id}";
    public string Count => Quantity.ToString("N0");
}
public sealed record CatalogChoice(string Id, string Name, string Category)
{
    public string Label => $"{Name} · {Category} · {Id}";
}

public sealed class InventoryPage : DesktopPage
{
    private readonly ComboBox accounts = new() { MinWidth = 360, DisplayMemberPath = nameof(AccountChoice.Label), PlaceholderText = AppText.Get("InventoryPage.Text210") };
    private readonly AutoSuggestBox search = new() { PlaceholderText = AppText.Get("InventoryPage.Text211"), MinWidth = 320 };
    private readonly ListView list = new() { MaxHeight = 580, MinHeight = 180, SelectionMode = ListViewSelectionMode.Single, IsItemClickEnabled = true };
    private readonly TextBlock balance = new() { FontSize = 20, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock count = new() { Opacity = .65 };
    private readonly Button creditButton;
    private readonly Button editButton;
    private readonly Button addButton;
    private readonly Button? importButton;
    private readonly Button? roleButton;
    private readonly ObservableCollection<InventoryRow> rows = [];
    private List<InventoryRow> source = [];
    private string category = "cargo";
    private string version = "";
    private Dictionary<string, JsonNode> shipDetails = [];
    private long credits;
    private bool suppressAccountChange;
    private int generation;

    public InventoryPage() : base(AppText.Get("AppShellPage.Text101"), AppText.Get("InventoryPage.Text212"))
    {
        if (DesktopContext.IsAdmin)
        {
            importButton = Button(AppText.Get("InventoryPage.Text213"), () => RunAsync(ImportJournalAsync));
            importButton.Name = "ImportJournal";
            roleButton = Button(AppText.Get("InventoryPage.Text214"), () => RunAsync(EditAccountRoleAsync)); roleButton.Name = "EditAccountRole";
            var administratorTools = new StackPanel { Spacing = 10 };
            administratorTools.Children.Add(accounts); administratorTools.Children.Add(Horizontal(importButton, roleButton));
            Body.Children.Add(Card(AppText.Get("DesktopContracts.Text115"), AppText.Get("InventoryPage.Text215"), administratorTools));
            accounts.SelectionChanged += async (_, _) => { if (!suppressAccountChange) await RunAsync(LoadAsync); };
        }
        var selector = new SelectorBar();
        foreach (var entry in new[] { (AppText.Get("InventoryPage.Text216"), "cargo"), (AppText.Get("InventoryPage.Text217"), "materials"), (AppText.Get("InventoryPage.Text218"), "ships") })
            selector.Items.Add(new SelectorBarItem { Text = entry.Item1, Tag = entry.Item2 });
        selector.SelectedItem = selector.Items[0];
        selector.SelectionChanged += async (_, _) =>
        {
            category = selector.SelectedItem?.Tag as string ?? "cargo";
            ApplyFilter(); UpdateActions();
            await RunAsync(LoadAsync);
        };
        Body.Children.Add(selector);
        creditButton = Button(AppText.Get("InventoryPage.Text219"), () => RunAsync(EditCreditsAsync));
        creditButton.Name = "EditCredits";
        Body.Children.Add(Horizontal(balance, creditButton));
        search.TextChanged += (_, args) =>
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            ApplyFilter();
            search.ItemsSource = source.Where(Matches).Select(x => x.Name).Distinct().Take(10).ToArray();
        };
        search.QuerySubmitted += (_, args) => { if (args.ChosenSuggestion is string selected) search.Text = selected; ApplyFilter(); };
        editButton = Button(AppText.Get("InventoryPage.Text220"), () => RunAsync(EditSelectedAsync));
        addButton = Button(AppText.Get("InventoryPage.Text221"), () => RunAsync(AddAsync));
        editButton.Name = "EditInventory"; addButton.Name = "AddInventory";
        Body.Children.Add(Horizontal(search, Button(AppText.Get("InventoryPage.Text222"), () => RunAsync(RefreshAsync)), editButton, addButton));
        list.ItemTemplate = (DataTemplate)XamlReader.Load("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><Grid Padding='12' ColumnSpacing='16'><Grid.ColumnDefinitions><ColumnDefinition Width='*'/><ColumnDefinition Width='Auto'/></Grid.ColumnDefinitions><StackPanel Spacing='6'><TextBlock Text='{Binding Name}' FontSize='16'/><TextBlock Text='{Binding Subtitle}' Opacity='0.6' TextTrimming='CharacterEllipsis'/></StackPanel><TextBlock Text='{Binding Count}' Grid.Column='1' VerticalAlignment='Center'/></Grid></DataTemplate>");
        list.ItemsSource = rows;
        list.ItemClick += async (_, args) =>
        {
            if (args.ClickedItem is not InventoryRow row) return;
            if (category == "ships") await RunAsync(() => ShowShipAsync(row));
            else if (CanEditCategory()) await RunAsync(() => EditQuantityAsync(row));
        };
        list.SelectionChanged += (_, _) => UpdateActions();
        Body.Children.Add(list); Body.Children.Add(count);
        if(DesktopContext.IsAdmin)Body.Children.Add(Button(AppText.Get("InventoryPage.Text223"),()=>RunAsync(async()=>
        {
            await DesktopContext.Local.PrepareMaterialsAsync(CurrentId,version,Lifetime.Token);await LoadAsync();Success(AppText.Get("InventoryPage.Text224"));
        })));
        Body.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Informational, Title = AppText.Get("InventoryPage.Text225"), Message = AppText.Get("InventoryPage.Text226"), MaxWidth = 1100 });
        DesktopContext.SessionChanged += UpdateActions;
        Unloaded += (_, _) => DesktopContext.SessionChanged -= UpdateActions;
        Loaded += async (_, _) => await RunAsync(RefreshAsync);
        UpdateActions();
    }
    private string? CurrentId => DesktopContext.IsAdmin ? (accounts.SelectedItem as AccountChoice)?.Id : DesktopContext.State.AccountId;
    private bool CanEditCategory() => DesktopContext.State.IsLoggedIn && (category == "cargo" ? DesktopContext.State.CanEditCargo : category == "materials" && DesktopContext.State.CanEditMaterials);
    private void UpdateActions()
    {
        creditButton.Visibility = DesktopContext.State.IsLoggedIn && DesktopContext.State.CanEditCredits ? Visibility.Visible : Visibility.Collapsed;
        editButton.Visibility = addButton.Visibility = CanEditCategory() ? Visibility.Visible : Visibility.Collapsed;
        editButton.IsEnabled = list.SelectedItem is not null;
        addButton.Content = category == "materials" ? AppText.Get("InventoryPage.Text227") : AppText.Get("InventoryPage.Text221");
        if (importButton is not null)
        {
            importButton.Visibility = DesktopContext.IsAdmin && DesktopContext.State.IsLoggedIn ? Visibility.Visible : Visibility.Collapsed;
            importButton.IsEnabled = accounts.SelectedItem is AccountChoice;
        }
        if (roleButton is not null)
        {
            roleButton.Visibility = DesktopContext.IsAdmin ? Visibility.Visible : Visibility.Collapsed;
            roleButton.IsEnabled = accounts.SelectedItem is AccountChoice target && target.Id != DesktopContext.State.AccountId;
        }
    }
    private async Task RefreshAsync()
    {
        if (!DesktopContext.State.IsLoggedIn) { source.Clear(); ApplyFilter(); balance.Text = AppText.Get("InventoryPage.Text228"); UpdateActions(); return; }
        await DesktopContext.RefreshPermissionsAsync();
        if (DesktopContext.IsAdmin)
        {
            var data = await DesktopContext.Local.AccountsAsync(Lifetime.Token);
            var selected = CurrentId;
            var choices = await Task.Run(() => data.Rows().Select(x => new AccountChoice(x.Text("id"), x.Text("email"), x.Text("displayName"), x.Text("role", "player"))).ToArray(), Lifetime.Token);
            suppressAccountChange = true;
            try { accounts.ItemsSource = choices; accounts.SelectedItem = choices.FirstOrDefault(x => x.Id == selected) ?? choices.FirstOrDefault(); }
            finally { suppressAccountChange = false; }
        }
        await LoadAsync();
    }
    private async Task LoadAsync()
    {
        if (!DesktopContext.State.IsLoggedIn || string.IsNullOrEmpty(CurrentId)) return;
        var requestGeneration = ++generation;
        var data = await DesktopContext.Local.CommanderAsync(CurrentId, Lifetime.Token);
        var requestedCategory = category;
        var prepared = await Task.Run(() => DesktopContracts.Inventory(data, requestedCategory).ToList(), Lifetime.Token);
        if (requestGeneration != generation || requestedCategory != category || Lifetime.IsCancellationRequested) return;
        credits = data.Number("credits"); version = data.Text("version");
        shipDetails = data["ships"].Rows().Where(x => !string.IsNullOrEmpty(x.Text("id"))).ToDictionary(x => x.Text("id"), x => x.DeepClone());
        balance.Text = AppText.Format("InventoryPage.Text229", (credits));
        source = prepared; ApplyFilter(); UpdateActions();
    }
    private bool Matches(InventoryRow row)
    {
        var keywords = search.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return keywords.All(key => (row.Name + " " + row.Id + " " + row.Detail + " " + row.Category).Contains(key, StringComparison.OrdinalIgnoreCase));
    }
    private void ApplyFilter()
    {
        var filtered = source.Where(Matches).ToArray();
        rows.Clear();
        foreach (var row in filtered.Take(1000)) rows.Add(row);
        count.Text = filtered.Length > 1000 ? AppText.Format("InventoryPage.Text230", (filtered.Length)) : AppText.Format("InventoryPage.Text231", (filtered.Length));
    }
    private async Task EditSelectedAsync()
    {
        if (list.SelectedItem is InventoryRow row) await EditQuantityAsync(row);
    }
    private async Task ShowShipAsync(InventoryRow row)
    {
        if (!shipDetails.TryGetValue(row.Id, out var ship)) return;
        var moduleList = new ListView { MaxHeight = 420, MinWidth = 500, SelectionMode = ListViewSelectionMode.None };
        moduleList.ItemTemplate = (DataTemplate)XamlReader.Load("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><StackPanel Spacing='6' Padding='8'><TextBlock Text='{Binding Name}'/><TextBlock Text='{Binding Subtitle}' Opacity='0.65' TextWrapping='Wrap'/></StackPanel></DataTemplate>");
        moduleList.ItemsSource = ship["modules"].Rows().Select(x => new InventoryRow(x.Text("itemId"), x.Text("slot"), 0, AppText.Format("InventoryPage.Text232", (x.Number("priority")), ((x.Flag("enabled") ? AppText.Get("InventoryPage.Text233") : AppText.Get("InventoryPage.Text234"))), (x.Text("health"))) )).ToArray();
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = $"{ship.Text("shipType")} · {ship.Text("ident")}\n{(ship.Flag("active") ? AppText.Get("InventoryPage.Text235") : AppText.Get("InventoryPage.Text236"))}", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(moduleList);
        var canEdit = DesktopContext.State.IsLoggedIn && DesktopContext.State.CanEditShips;
        moduleList.SelectionMode = canEdit ? ListViewSelectionMode.Single : ListViewSelectionMode.None;
        var result = await ShowAsync(row.Name + AppText.Get("InventoryPage.Text237"), panel, canEdit ? AppText.Get("InventoryPage.Text238") : null);
        // Close the first ContentDialog completely before constructing the editor.
        if (result != ContentDialogResult.Primary || moduleList.SelectedItem is not InventoryRow moduleRow) return;
        var module = ship["modules"].Rows().FirstOrDefault(x => x.Text("slot") == moduleRow.Name);
        if (module is null) return;
        await EditModuleAsync(row.Id, module);
    }
    private async Task EditModuleAsync(string shipId, JsonNode module)
    {
        if (!DesktopContext.State.CanEditShips) return;
        var priority = new NumberBox { Minimum = 0, Maximum = 4, Value = module.Number("priority"), SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Width = 150 };
        var enabled = Toggle(module.Flag("enabled"));
        var health = new NumberBox { Minimum = 0, Maximum = 100, Value = double.TryParse(module.Text("health"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var initialHealth) ? initialHealth * 100 : 100, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Width = 150 };
        var panel = new StackPanel { Spacing = 8, MinWidth = 440 };
        panel.Children.Add(new TextBlock { Text = module.Text("itemId"), Opacity = .65, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(Card(AppText.Get("InventoryPage.Text239"), AppText.Get("InventoryPage.Text240"), priority));
        panel.Children.Add(Card(AppText.Get("InventoryPage.Text241"), AppText.Get("InventoryPage.Text242"), enabled));
        panel.Children.Add(Card(AppText.Get("InventoryPage.Text243"), AppText.Get("InventoryPage.Text244"), health));
        if (await ShowAsync(module.Text("slot") + AppText.Get("InventoryPage.Text245"), panel, AppText.Get("InventoryPage.Text246")) != ContentDialogResult.Primary) return;
        if (!double.IsFinite(priority.Value) || priority.Value % 1 != 0 || !double.IsFinite(health.Value)) throw new InvalidOperationException(AppText.Get("InventoryPage.Text247"));
        await DesktopContext.Local.EditAsync(CurrentId, InventoryEdit.Modules, new { shipId, slot = module.Text("slot"), priority = checked((int)priority.Value), enabled = enabled.IsOn, health = health.Value / 100d }, version, Lifetime.Token);
        await LoadAsync(); Success(AppText.Get("InventoryPage.Text248"));
    }
    private async Task EditQuantityAsync(InventoryRow row)
    {
        if (!CanEditCategory()) return;
        var quantity = new NumberBox { Header = AppText.Get("InventoryPage.Text249"), Value = row.Quantity, Minimum = 0, Maximum = 1000000, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, MinWidth = 320 };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = row.Id, Opacity = .6, TextWrapping = TextWrapping.Wrap }); panel.Children.Add(quantity);
        if (await ShowAsync(row.Name, panel, AppText.Get("InventoryPage.Text250")) != ContentDialogResult.Primary) return;
        if (!double.IsFinite(quantity.Value) || quantity.Value % 1 != 0) throw new InvalidOperationException(AppText.Get("InventoryPage.Text251"));
        await DesktopContext.Local.EditQuantityAsync(CurrentId, category, row.Id, row.Category, checked((long)quantity.Value), version, Lifetime.Token);
        await LoadAsync(); Success(AppText.Get("InventoryPage.Text252"));
    }
    private async Task EditCreditsAsync()
    {
        if (!DesktopContext.State.CanEditCredits) return;
        var delta = new NumberBox { Header = AppText.Get("InventoryPage.Text253"), Value = 1000, Minimum = -9000000000000, Maximum = 9000000000000, MinWidth = 320, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        if (await ShowAsync(AppText.Format("InventoryPage.Text254", (credits)), delta, AppText.Get("InventoryPage.Text255")) != ContentDialogResult.Primary) return;
        if (!double.IsFinite(delta.Value) || delta.Value % 1 != 0) throw new InvalidOperationException(AppText.Get("InventoryPage.Text256"));
        await DesktopContext.Local.EditAsync(CurrentId, InventoryEdit.Credits, new { delta = checked((long)delta.Value) }, version, Lifetime.Token);
        await LoadAsync(); Success(AppText.Get("InventoryPage.Text257"));
    }
    private async Task AddAsync()
    {
        if (!CanEditCategory()) return;
        var input = new AutoSuggestBox { Header = category == "materials" ? AppText.Get("InventoryPage.Text258") : AppText.Get("InventoryPage.Text259"), PlaceholderText = AppText.Get("InventoryPage.Text260"), MinWidth = 380, DisplayMemberPath = nameof(CatalogChoice.Label) };
        var quantity = new NumberBox { Header = AppText.Get("InventoryPage.Text261"), Value = 1, Minimum = 0, Maximum = 1000000, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var hint = new TextBlock { Text = AppText.Get("InventoryPage.Text262"), Opacity = .65, TextWrapping = TextWrapping.Wrap };
        CatalogChoice? selected = null;
        var catalog = await DesktopContext.Local.CatalogAsync(cancel: Lifetime.Token,accountId:CurrentId);
        CatalogChoice[] ParseChoices(JsonNode data) => DesktopContracts.Catalog(data, category);
        var choices = await Task.Run(() => ParseChoices(catalog), Lifetime.Token);
        input.ItemsSource = choices.Take(30).ToArray();
        var searchGeneration = 0;
        input.TextChanged += async (_, args) =>
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            selected = null;
            var requested = ++searchGeneration;
            var query = input.Text;
            var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            try
            {
                await Task.Delay(200, Lifetime.Token);
                if (requested != searchGeneration) return;
                var response = await DesktopContext.Local.CatalogAsync(query, Lifetime.Token,CurrentId);
                var candidates = await Task.Run(() => ParseChoices(response), Lifetime.Token);
                if (requested != searchGeneration) return;
                choices = candidates;
                input.ItemsSource = candidates.Where(x => terms.All(term => x.Label.Contains(term, StringComparison.OrdinalIgnoreCase))).Take(30).ToArray();
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { DesktopContext.Log(error); hint.Text = AppText.Get("InventoryPage.Text263") + error.Message; }
        };
        input.SuggestionChosen += (_, args) => { selected = args.SelectedItem as CatalogChoice; hint.Text = selected?.Id ?? ""; };
        input.QuerySubmitted += (_, args) => { if (args.ChosenSuggestion is CatalogChoice choice) selected = choice; };
        var panel = new StackPanel { Spacing = 14 }; panel.Children.Add(input); panel.Children.Add(hint); panel.Children.Add(quantity);
        if (await ShowAsync(category == "materials" ? AppText.Get("InventoryPage.Text227") : AppText.Get("InventoryPage.Text264"), panel, AppText.Get("InventoryPage.Text265")) != ContentDialogResult.Primary) return;
        selected ??= choices.FirstOrDefault(x => x.Id.Equals(input.Text.Trim(), StringComparison.OrdinalIgnoreCase) || x.Name.Equals(input.Text.Trim(), StringComparison.OrdinalIgnoreCase));
        if (selected is null) throw new InvalidOperationException(AppText.Get("InventoryPage.Text266"));
        if (!double.IsFinite(quantity.Value) || quantity.Value % 1 != 0) throw new InvalidOperationException(AppText.Get("InventoryPage.Text251"));
        await DesktopContext.Local.EditQuantityAsync(CurrentId, category, selected.Id, selected.Category, checked((long)quantity.Value), version, Lifetime.Token);
        await LoadAsync(); Success(AppText.Get("InventoryPage.Text267"));
    }
    private async Task ImportJournalAsync()
    {
        if (!DesktopContext.IsAdmin || !DesktopContext.State.IsLoggedIn || accounts.SelectedItem is not AccountChoice target)
            throw new InvalidOperationException(AppText.Get("InventoryPage.Text268"));
        var targetId = target.Id;
        var existing = await DesktopContext.Local.CommanderAsync(targetId, Lifetime.Token);
        var originalVersion = existing.Text("version");
        var directory = new TextBox { Header = AppText.Get("InventoryPage.Text269"), Text = JournalSnapshotReader.DefaultDirectory, MinWidth = 430 };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = AppText.Get("InventoryPage.Text270"), TextWrapping = TextWrapping.Wrap, MaxWidth = 480 });
        panel.Children.Add(directory);
        if (await ShowAsync(AppText.Get("InventoryPage.Text271"), panel, AppText.Get("InventoryPage.Text272")) != ContentDialogResult.Primary) return;
        var journalDirectory = directory.Text.Trim();
        var snapshot = await Task.Run(() => new JournalSnapshotReader().ReadAsync(journalDirectory, Lifetime.Token), Lifetime.Token);
        var serialized = await Task.Run(() => JsonSerializer.SerializeToNode(snapshot, ClientJson.Options) as JsonObject ?? throw new InvalidDataException(AppText.Get("InventoryPage.Text273")), Lifetime.Token);
        var preview = new StackPanel { Spacing = 12, MinWidth = 430 };
        preview.Children.Add(new TextBlock { Text = AppText.Format("InventoryPage.Text274", (target.Name), (snapshot.Name), (snapshot.Credits), (snapshot.Cargo.Count), (snapshot.Materials.Count), (snapshot.Ships.Count)), TextWrapping = TextWrapping.Wrap });
        preview.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning, Title = AppText.Get("InventoryPage.Text275"), Message = AppText.Get("InventoryPage.Text276") });
        if (snapshot.Source.Warnings.Count > 0) preview.Children.Add(new TextBlock { Text = string.Join("\n", snapshot.Source.Warnings.Take(6)), TextWrapping = TextWrapping.Wrap, Opacity = .7, MaxWidth = 480 });
        if (await ShowAsync(AppText.Get("InventoryPage.Text277"), preview, AppText.Get("InventoryPage.Text278")) != ContentDialogResult.Primary) return;
        if (CurrentId != targetId) throw new InvalidOperationException(AppText.Get("InventoryPage.Text279"));

        await DesktopContext.Local.ImportJournalAsync(targetId, serialized.AsObject(), originalVersion, Lifetime.Token);
        await LoadAsync(); Success(AppText.Get("InventoryPage.Text280"));
    }
    private async Task EditAccountRoleAsync()
    {
        if (!DesktopContext.IsAdmin || accounts.SelectedItem is not AccountChoice target) return;
        if (target.Id == DesktopContext.State.AccountId) throw new InvalidOperationException(AppText.Get("InventoryPage.Text281"));
        var role = new ComboBox { Header = AppText.Get("AccountPage.Text093"), MinWidth = 320 };
        role.Items.Add(new ComboBoxItem { Content = AppText.Get("AccountPage.Text094"), Tag = "player" });
        role.Items.Add(new ComboBoxItem { Content = AppText.Get("AccountPage.Text095"), Tag = "administrator" });
        role.SelectedIndex = target.Role == "administrator" ? 1 : 0;
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = target.Label, TextWrapping = TextWrapping.Wrap }); panel.Children.Add(role);
        panel.Children.Add(new TextBlock { Text = AppText.Get("InventoryPage.Text282"), TextWrapping = TextWrapping.Wrap, MaxWidth = 440, Opacity = .7 });
        if (await ShowAsync(AppText.Get("InventoryPage.Text283"), panel, AppText.Get("InventoryPage.Text284")) != ContentDialogResult.Primary) return;
        var selectedRole = (role.SelectedItem as ComboBoxItem)?.Tag as string ?? "player";
        if (selectedRole == target.Role) return;
        await DesktopContext.Local.SetRoleAsync(target.Id, selectedRole, Lifetime.Token);
        await RefreshAsync(); Success(AppText.Get("InventoryPage.Text285"));
    }
}
