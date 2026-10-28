using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using XFEEliteDangerous.Client;

namespace XFEEliteDangerous.Desktop;

public sealed partial class FleetPage
{
    private sealed record BlueprintChoice(string Key,string Label,JsonObject State);
    private sealed record EffectChoice(string Symbol,string Label);
    private readonly ComboBox engineeringBlueprints=new(){Name="FleetEngineeringBlueprint",HorizontalAlignment=HorizontalAlignment.Stretch,DisplayMemberPath=nameof(BlueprintChoice.Label)};
    private readonly ComboBox engineeringEffects=new(){Name="FleetExperimentalEffect",HorizontalAlignment=HorizontalAlignment.Stretch,DisplayMemberPath=nameof(EffectChoice.Label)};
    private readonly StackPanel engineeringValues=new(){Name="FleetEngineeringValues",Spacing=10};
    private readonly TextBlock engineeringCurrent=new(){TextWrapping=TextWrapping.Wrap};
    private readonly Dictionary<string,NumberBox> modifierEditors=new(StringComparer.Ordinal);
    private readonly NumberBox ammunitionClip=new(){Name="FleetAmmoClip",Minimum=0,Maximum=1_000_000,SmallChange=1,Header=AppText.Get("Refit.AmmoClip"),SpinButtonPlacementMode=NumberBoxSpinButtonPlacementMode.Compact};
    private readonly NumberBox ammunitionHopper=new(){Name="FleetAmmoHopper",Minimum=0,Maximum=1_000_000,SmallChange=1,Header=AppText.Get("Refit.AmmoHopper"),SpinButtonPlacementMode=NumberBoxSpinButtonPlacementMode.Compact};
    private StackPanel ammunitionPanel=null!;

    private UIElement BuildAmmunitionInspector()
    {
        ammunitionPanel=new(){Name="FleetAmmunition",Spacing=10,Visibility=Visibility.Collapsed};
        ammunitionPanel.Children.Add(new TextBlock{Text=AppText.Get("Refit.Ammunition"),FontSize=18});
        ammunitionPanel.Children.Add(ammunitionClip);ammunitionPanel.Children.Add(ammunitionHopper);
        ammunitionPanel.Children.Add(Button(AppText.Get("Refit.ApplyAmmo"),()=>RunAsync(ApplyAmmunitionAsync)));
        return ammunitionPanel;
    }
    private UIElement BuildEngineeringInspector()
    {
        var panel=new StackPanel{Spacing=12};panel.Children.Add(engineeringCurrent);
        panel.Children.Add(new TextBlock{Text=AppText.Get("Refit.Blueprint"),FontWeight=Microsoft.UI.Text.FontWeights.SemiBold});
        panel.Children.Add(engineeringBlueprints);
        panel.Children.Add(new TextBlock{Text=AppText.Get("Refit.Experimental"),FontWeight=Microsoft.UI.Text.FontWeights.SemiBold});
        panel.Children.Add(engineeringEffects);panel.Children.Add(engineeringValues);
        panel.Children.Add(new TextBlock{Text=AppText.Get("Refit.EngineeringHelp"),TextWrapping=TextWrapping.Wrap,Opacity=.7,FontSize=12});
        panel.Children.Add(Button(AppText.Get("Refit.ApplyEngineering"),()=>RunAsync(()=>ApplyEngineeringAsync(false)),true));
        panel.Children.Add(Button(AppText.Get("Refit.RemoveEngineering"),()=>RunAsync(()=>ApplyEngineeringAsync(true))));
        engineeringBlueprints.SelectionChanged+=(_,_)=>RefreshEngineeringValues();
        return panel;
    }
    private static string EngineeringText(string symbol)
    {
        var key="Refit.EngineeringLabel."+symbol;
        return AppText.Catalog(AppText.Language).TryGetValue(key,out var value)?value:symbol.Replace('_',' ');
    }
    private void SelectAdvancedSlot(FleetEditorSlot? slot)
    {
        var module=slot?.Data["module"];
        var enabled=module is not null&&DesktopContext.State.CanEditShips;
        ammunitionPanel.Visibility=slot?.Data["ammoEditable"]?.GetValue<bool>()==true?Visibility.Visible:Visibility.Collapsed;
        ammunitionClip.IsEnabled=ammunitionHopper.IsEnabled=enabled;
        ammunitionClip.Maximum=slot?.Data["ammoCapacity"]?.Number("clip")??1_000_000;ammunitionHopper.Maximum=slot?.Data["ammoCapacity"]?.Number("hopper")??1_000_000;
        ammunitionClip.Value=module?["ammo"]?.Number("clip")??0;ammunitionHopper.Value=module?["ammo"]?.Number("hopper")??0;
        engineeringBlueprints.IsEnabled=engineeringEffects.IsEnabled=enabled;
        var current=module?["engineering"];
        engineeringCurrent.Text=current is null?AppText.Get("Refit.NoEngineering"):EngineeringText(current.Text("recipeName"))+" · G"+current.Number("level")+" · "+current.Number("progress").ToString("P0",AppText.DisplayCulture);
        if(slot?.Data["maximumHealth"] is JsonValue health)engineeringCurrent.Text+="\n"+AppText.Format("Refit.MaximumHealth",health.GetValue<float>());
        var choices=slot?.Data["engineeringBlueprints"].Rows().Select(row=>new BlueprintChoice(row.Text("key"),EngineeringText(row["state"].Text("recipeName"))+" · G"+row["state"].Number("level")+" · "+row["state"].Number("progress").ToString("P0",AppText.DisplayCulture),(JsonObject)row["state"]!.DeepClone())).ToArray()??[];
        engineeringBlueprints.ItemsSource=choices;
        engineeringBlueprints.SelectedItem=choices.FirstOrDefault(value=>value.State.Text("recipeName")==current.Text("recipeName")&&value.State.Number("level")==current.Number("level"))??choices.FirstOrDefault();
        var effects=new List<EffectChoice>{new("",AppText.Get("Refit.NoExperimental"))};
        effects.AddRange(slot?.Data["experimentalEffects"].Rows().Select(row=>new EffectChoice(row.Text("name"),EngineeringText(row.Text("name"))))??[]);
        engineeringEffects.ItemsSource=effects;
        var selected=current?["modifiers"].Rows().FirstOrDefault(row=>row.Number("type")==3)?.Text("name")??"";
        engineeringEffects.SelectedItem=effects.FirstOrDefault(value=>value.Symbol==selected)??effects[0];
        RefreshEngineeringValues();
    }
    private void RefreshEngineeringValues()
    {
        modifierEditors.Clear();engineeringValues.Children.Clear();
        if(engineeringBlueprints.SelectedItem is not BlueprintChoice blueprint)return;
        var current=(slots.SelectedItem as FleetEditorSlot)?.Data["module"]?["engineering"];
        foreach(var row in blueprint.State["modifiers"].Rows())
        {
            var name=row.Text("name");var value=row.Number("value");
            if(current.Text("recipeName")==blueprint.State.Text("recipeName")&&current.Number("level")==blueprint.State.Number("level"))
                value=current?["modifiers"].Rows().FirstOrDefault(item=>item.Text("name")==name)?.Number("value")??value;
            var editor=new NumberBox{Header=EngineeringText(name)+" (%)",Minimum=-99,Maximum=10_000,SmallChange=1,Value=value*100,
                IsEnabled=DesktopContext.State.CanEditShips,SpinButtonPlacementMode=NumberBoxSpinButtonPlacementMode.Compact};
            modifierEditors.Add(name,editor);engineeringValues.Children.Add(editor);
        }
    }
    private async Task ApplyAdvancedRefitAsync(JsonObject change)
    {
        if(!DesktopContext.State.CanEditShips)throw new UnauthorizedAccessException(AppText.Get("FleetPage.Text153"));
        if(ships.SelectedItem is not FleetShipChoice ship||slots.SelectedItem is not FleetEditorSlot slot||slot.Data["module"] is null)throw new InvalidOperationException(AppText.Get("Refit.SelectSlot"));
        change["shipId"]=ship.Id;change["slot"]=slot.Name;
        var result=await DesktopContext.Local.RefitAsync(AccountId,change,version,Lifetime.Token);await LoadAsync();ShowFleetApplyResult(result);
    }
    private Task ApplyAmmunitionAsync()
    {
        foreach(var value in new[]{ammunitionClip.Value,ammunitionHopper.Value})
            if(!double.IsFinite(value)||value!=Math.Truncate(value)||value<0||value>1_000_000)throw new InvalidDataException(AppText.Get("Refit.InvalidAmmo"));
        return ApplyAdvancedRefitAsync(new(){["action"]="ammunition",["clip"]=(uint)ammunitionClip.Value,["hopper"]=(uint)ammunitionHopper.Value});
    }
    private Task ApplyEngineeringAsync(bool remove)
    {
        if(remove)return ApplyAdvancedRefitAsync(new(){["action"]="engineering",["remove"]=true});
        if(engineeringBlueprints.SelectedItem is not BlueprintChoice blueprint)throw new InvalidDataException(AppText.Get("Refit.BlueprintRequired"));
        var values=new JsonObject();foreach(var pair in modifierEditors)
        {
            if(!double.IsFinite(pair.Value.Value))throw new InvalidDataException(AppText.Get("Refit.InvalidEngineering"));
            values[pair.Key]=pair.Value.Value/100d;
        }
        return ApplyAdvancedRefitAsync(new(){["action"]="engineering",["blueprintKey"]=blueprint.Key,["experimental"]=(engineeringEffects.SelectedItem as EffectChoice)?.Symbol??"",["values"]=values});
    }
}
