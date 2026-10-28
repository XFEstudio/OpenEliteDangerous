using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using System.Text.Json.Nodes;
using XFEEliteDangerous.Client;
using XFEEliteDangerous.Core;

namespace XFEEliteDangerous.Desktop;

public sealed partial class FleetPage
{
    private sealed record CategoryChoice(string Key, string Caption);
    private sealed record ModuleChoice(string Symbol,string Label);
    private readonly ListView categories = new() { Name = "FleetCategories", MinWidth = 160, DisplayMemberPath = nameof(CategoryChoice.Caption) };
    private readonly TextBox slotSearch = new() { Name = "FleetSlotSearch", PlaceholderText = AppText.Get("Refit.SearchSlots") };
    private readonly TextBox moduleSearch = new() { Name = "FleetModuleSearch", PlaceholderText = AppText.Get("Refit.SearchModules") };
    private readonly TextBlock slotHeading = new() { FontSize = 21, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock inspectorHeading = new() { FontSize = 23, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock inspectorSymbol = new() { Opacity = .65, FontSize = 12, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock moduleFacts = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ToggleSwitch moduleEnabled = Toggle();
    private readonly NumberBox modulePriority = new() { Name = "FleetModulePriority", Minimum = 1, Maximum = 5, SmallChange = 1, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Header = AppText.Get("Refit.PowerPriority") };
    private readonly NumberBox moduleIntegrity = new() { Name = "FleetModuleIntegrity", Minimum = 0, Maximum = 100, SmallChange = 1, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Header = AppText.Get("Refit.Integrity") };
    private readonly TextBlock noPowerPriority = new(){Text=AppText.Get("Refit.ReadOnlyPriority"),TextWrapping=TextWrapping.Wrap,Opacity=.65,Visibility=Visibility.Collapsed};
    private readonly InfoBar applyState = new() { Name = "FleetApplyState", IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Informational, Title = AppText.Get("Refit.ApplyStatus"), Message = AppText.Get("Refit.ReadFirst") };
    private IReadOnlyList<FleetEditorSlot> editorSlots = [];
    private string[] compatibleModules = [];
    private string selectedSlotName = "";
    private bool selectingCategory;

    private void BuildEditorWorkbench()
    {
        modules.DisplayMemberPath=nameof(ModuleChoice.Label);
        var workbench = new Grid { Name = "FleetWorkbench", ColumnSpacing = 14, RowSpacing = 14 };
        workbench.ColumnDefinitions.Add(new() { Width = new GridLength(180) });
        workbench.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        var inspectorColumn = new ColumnDefinition { Width = new GridLength(360) };
        workbench.ColumnDefinitions.Add(inspectorColumn);
        workbench.RowDefinitions.Add(new() { Height = GridLength.Auto });
        workbench.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var categoryBody = new StackPanel { Spacing = 16 };
        categoryBody.Children.Add(new TextBlock { Text = AppText.Get("Refit.Outfitting"), FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        categoryBody.Children.Add(categories);
        categoryBody.Children.Add(new TextBlock { Text = AppText.Get("Refit.CategoryHelp"), TextWrapping = TextWrapping.Wrap, Opacity = .65, FontSize = 12 });
        var categoryCard = EditorCard(categoryBody);
        workbench.Children.Add(categoryCard);
        var slotBody = new StackPanel { Spacing = 12 };
        slotBody.Children.Add(slotHeading); slotBody.Children.Add(slotSearch); slotBody.Children.Add(slots);
        slots.ItemTemplate = (DataTemplate)XamlReader.Load("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"><Border Padding="14" CornerRadius="10" BorderThickness="1" BorderBrush="{ThemeResource CardStrokeColorDefaultBrush}" Background="{ThemeResource CardBackgroundFillColorDefaultBrush}" Width="218" MinHeight="140" Margin="0,0,8,8"><StackPanel Spacing="9"><TextBlock Text="{Binding Caption}" Opacity="0.65" FontSize="11" TextWrapping="Wrap"/><FontIcon Glyph="&#xE7C3;" HorizontalAlignment="Left" FontSize="24"/><TextBlock Text="{Binding DisplayName}" FontWeight="SemiBold" TextWrapping="Wrap"/><TextBlock Text="{Binding State}" FontSize="11" Opacity="0.7" TextWrapping="Wrap"/></StackPanel></Border></DataTemplate>
            """);
        var slotCard = EditorCard(slotBody); Grid.SetColumn(slotCard, 1); workbench.Children.Add(slotCard);
        var inspector = new StackPanel { Spacing = 12 };
        inspector.Children.Add(inspectorHeading); inspector.Children.Add(inspectorSymbol);
        var tabs = new Pivot { Name = "FleetInspectorTabs" };
        var properties = new StackPanel { Spacing = 12 };
        properties.Children.Add(moduleFacts); properties.Children.Add(moduleEnabled); properties.Children.Add(modulePriority); properties.Children.Add(noPowerPriority); properties.Children.Add(moduleIntegrity);
        var apply = Button(AppText.Get("Refit.ApplyProperties"), () => RunAsync(ApplyModulePropertiesAsync), true); apply.Name = "ApplyFleetProperties";
        properties.Children.Add(apply);
        properties.Children.Add(BuildAmmunitionInspector());
        tabs.Items.Add(new PivotItem { Header = AppText.Get("Refit.Properties"), Content = properties });
        var replacement = new StackPanel { Spacing = 12 };
        replacement.Children.Add(new TextBlock { Text = AppText.Get("Refit.CompatibleOnly"), TextWrapping = TextWrapping.Wrap, Opacity = .7 });
        replacement.Children.Add(moduleSearch); replacement.Children.Add(modules);
        var fit = Button(AppText.Get("FleetPage.Text141"), () => RunAsync(() => SlotAction("module"))); fit.Name = "FitFleetModule";
        var store = Button(AppText.Get("FleetPage.Text142"), () => RunAsync(() => SlotAction("store"))); store.Name = "StoreFleetModule";
        replacement.Children.Add(fit); replacement.Children.Add(store);
        replacement.Children.Add(new TextBlock { Text = AppText.Get("FleetPage.Text143"), FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        replacement.Children.Add(storage);
        replacement.Children.Add(Button(AppText.Get("FleetPage.Text145"), () => RunAsync(() => SlotAction("retrieve"))));
        tabs.Items.Add(new PivotItem { Header = AppText.Get("Refit.Replace"), Content = replacement });
        tabs.Items.Add(new PivotItem { Header = AppText.Get("Refit.Engineering"), Content = BuildEngineeringInspector() });
        inspector.Children.Add(tabs);
        var inspectorCard = EditorCard(inspector); Grid.SetColumn(inspectorCard, 2); workbench.Children.Add(inspectorCard);
        Body.Children.Add(workbench); Body.Children.Add(applyState);
        categories.SelectionChanged += (_, _) => { if (!selectingCategory) FilterEditorSlots(); };
        slotSearch.TextChanged += (_, _) => FilterEditorSlots();
        moduleSearch.TextChanged += (_, _) => FilterModuleChoices();
        slots.SelectionChanged += (_, _) => SelectEditorSlot();
        workbench.SizeChanged += (_, e) =>
        {
            var stacked = e.NewSize.Width < 900;
            inspectorColumn.Width = stacked ? new GridLength(0) : new GridLength(360);
            Grid.SetColumn(inspectorCard, stacked ? 0 : 2); Grid.SetRow(inspectorCard, stacked ? 1 : 0); Grid.SetColumnSpan(inspectorCard, stacked ? 2 : 1);
        };
        RefreshEditorCategories();
        SelectEditorSlot();
    }

    private static Border EditorCard(UIElement child) => new()
    {
        Child = child, Padding = new Thickness(16), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1),
        BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
        Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"]
    };

    private void RefreshEditorCategories()
    {
        var old = (categories.SelectedItem as CategoryChoice)?.Key ?? "hardpoint";
        selectingCategory = true;
        try
        {
            var choices = new[] { "hardpoint", "utility", "core", "internal" }.Select(key => new CategoryChoice(key,
                AppText.Get("Refit.Category." + key) + "\n" + AppText.Format("Refit.CategoryCount", editorSlots.Count(x => x.Category == key && x.Module.Length > 0), editorSlots.Count(x => x.Category == key)))).ToArray();
            categories.ItemsSource = choices; categories.SelectedItem = choices.Single(x => x.Key == old);
        }
        finally { selectingCategory = false; }
    }

    private void FilterEditorSlots()
    {
        var category = (categories.SelectedItem as CategoryChoice)?.Key ?? "hardpoint";
        var search = slotSearch.Text.Trim();
        var rows = editorSlots.Where(x => x.Category == category && (search.Length == 0 || (x.Name + " " + x.Module + " " + x.DisplayName).Contains(search, StringComparison.OrdinalIgnoreCase))).ToArray();
        slotHeading.Text = AppText.Get("Refit.Category." + category);
        slots.ItemsSource = rows;
        slots.SelectedItem = rows.FirstOrDefault(x => x.Name == selectedSlotName) ?? rows.FirstOrDefault();
        SelectEditorSlot();
    }

    private void SelectEditorSlot()
    {
        var slot = slots.SelectedItem as FleetEditorSlot;
        selectedSlotName = slot?.Name ?? "";
        inspectorHeading.Text = slot?.DisplayName ?? AppText.Get("Refit.SelectSlot");
        inspectorSymbol.Text = slot is null ? "" : slot.Name + "\n" + slot.Module;
        var module = slot?.Data["module"];
        moduleEnabled.IsOn = module.Flag("on"); modulePriority.Value = (module?.Number("priority") ?? 0)+1;
        moduleIntegrity.Value = (module?.Number("healthMillion") ?? 0) / 10000d;
        moduleEnabled.IsEnabled = modulePriority.IsEnabled = moduleIntegrity.IsEnabled = module is not null && DesktopContext.State.CanEditShips;
        if(slot?.Data["integrityEditable"]?.GetValue<bool>()==false)moduleIntegrity.IsEnabled=false;
        if(slot?.Data["priorityEditable"]?.GetValue<bool>()==false)modulePriority.IsEnabled=false;
        modulePriority.Visibility=slot?.Data["priorityEditable"]?.GetValue<bool>()==false?Visibility.Collapsed:Visibility.Visible;
        noPowerPriority.Visibility=modulePriority.Visibility==Visibility.Collapsed?Visibility.Visible:Visibility.Collapsed;
        moduleFacts.Text = module is null ? AppText.Get("FleetPage.Text119") : AppText.Format("Refit.ModuleFacts", module.Number("id"), module.Number("value"),
            module["ammo"] is null?AppText.Get("Refit.NotApplicable"):module["ammo"].Number("clip").ToString(),module["ammo"] is null?AppText.Get("Refit.NotApplicable"):module["ammo"].Number("hopper").ToString());
        compatibleModules = slot?.Data["compatible"].Rows().Select(x => x.Text("name")).ToArray() ?? [];
        FilterModuleChoices();
        SelectAdvancedSlot(slot);
    }

    private void FilterModuleChoices()
    {
        var search = moduleSearch.Text.Trim();
        var values = compatibleModules.Where(x => search.Length == 0 || (x + " " + FleetEditorProjection.ModuleName(x)).Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
        modules.ItemsSource = values.Select(value=>new ModuleChoice(value,FleetEditorProjection.ModuleName(value))).ToArray(); modules.SelectedIndex = values.Length > 0 ? 0 : -1;
    }

    private async Task ApplyModulePropertiesAsync()
    {
        if (!DesktopContext.State.CanEditShips) throw new UnauthorizedAccessException(AppText.Get("FleetPage.Text153"));
        if (ships.SelectedItem is not FleetShipChoice ship || slots.SelectedItem is not FleetEditorSlot slot || slot.Data["module"] is null)
            throw new InvalidOperationException(AppText.Get("Refit.SelectSlot"));
        if (!double.IsFinite(modulePriority.Value) || modulePriority.Value != Math.Truncate(modulePriority.Value) || !double.IsFinite(moduleIntegrity.Value))
            throw new InvalidDataException(AppText.Get("InventoryPage.Text247"));
        var result = await DesktopContext.Local.RefitAsync(AccountId,
            new JsonObject { ["action"]="properties",["shipId"]=ship.Id,["slot"]=slot.Name,["enabled"]=moduleEnabled.IsOn,
                ["priority"]=modulePriority.IsEnabled?(int)modulePriority.Value-1:(int)(slot.Data["module"].Number("priority")),["health"]=moduleIntegrity.Value/100d }, version, Lifetime.Token);
        await LoadAsync(); ShowFleetApplyResult(result);
    }

    private void ShowFleetApplyResult(JsonNode result)
    {
        var live = result.Flag("appliedInGame");
        applyState.Title = AppText.Get(live ? "Refit.AppliedLive" : "Refit.Saved");
        applyState.Message = AppText.Get(live ? "Refit.AppliedLiveDetail" : "FleetPage.Text155");
        applyState.Severity = InfoBarSeverity.Success;
        Success(applyState.Message);
    }
}
