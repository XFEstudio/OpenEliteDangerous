using XFEEliteDangerous.Client;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace XFEEliteDangerous.Desktop;

public sealed record FleetShipChoice(uint Id,string Name,string Prototype,bool Active)
{public string Label=>$"{Name} · {Prototype}"+(Active?AppText.Get("FleetPage.Text118"):"");}
public sealed record StoredModuleChoice(uint Id,string Module)
{public string Label=>AppText.Format("FleetPage.Text120", FleetEditorProjection.ModuleName(Module), Id);}

public sealed partial class FleetPage : DesktopPage
{
    private readonly ComboBox accounts=new(){MinWidth=380,DisplayMemberPath=nameof(AccountChoice.Label)};
    private readonly ComboBox ships=new(){MinWidth=440,DisplayMemberPath=nameof(FleetShipChoice.Label)};
    private readonly GridView slots=new(){MinHeight=360,MaxHeight=650,SelectionMode=ListViewSelectionMode.Single};
    private readonly ComboBox modules=new(){HorizontalAlignment=HorizontalAlignment.Stretch,PlaceholderText=AppText.Get("FleetPage.Text121")};
    private readonly ComboBox storage=new(){HorizontalAlignment=HorizontalAlignment.Stretch,DisplayMemberPath=nameof(StoredModuleChoice.Label),PlaceholderText=AppText.Get("FleetPage.Text122")};
    private readonly TextBox name=new(){PlaceholderText=AppText.Get("FleetPage.Text123"),MaxLength=100,MinWidth=240};
    private readonly TextBox ident=new(){PlaceholderText=AppText.Get("FleetPage.Text124"),MaxLength=24,MinWidth=140};
    private readonly TextBlock details=new(){TextWrapping=TextWrapping.Wrap};
    private JsonNode? fleet;
    private string version="";
    private bool refreshing;
    private string? AccountId=>DesktopContext.IsAdmin?(accounts.SelectedItem as AccountChoice)?.Id:DesktopContext.State.AccountId;
    public FleetPage():base(AppText.Get("AppShellPage.Text102"),AppText.Get("FleetPage.Text125"))
    {
        accounts.Name="FleetAccounts";ships.Name="FleetShips";slots.Name="FleetSlots";
        if(DesktopContext.IsAdmin)Body.Children.Add(Card(AppText.Get("DesktopContracts.Text115"),AppText.Get("FleetPage.Text126"),accounts));
        accounts.SelectionChanged+=async(_,_)=>{if(!refreshing)await RunAsync(LoadAsync);};
        Body.Children.Add(Horizontal(ships,Button(AppText.Get("FleetPage.Text127"),()=>RunAsync(RefreshAsync))));
        Body.Children.Add(details);
        if(DesktopContext.IsAdmin)Body.Children.Add(Button(AppText.Get("FleetPage.Text128"),()=>RunAsync(async()=>
        {
            await DesktopContext.Local.InitializeNativeAsync(AccountId,version,Lifetime.Token);await LoadAsync();Success(AppText.Get("FleetPage.Text129"));
        })));
        ships.SelectionChanged+=(_,_)=>SelectShip();
        Body.Children.Add(Card(AppText.Get("FleetPage.Text130"),AppText.Get("FleetPage.Text131"),Horizontal(name,ident,Button(AppText.Get("FleetPage.Text132"),()=>RunAsync(()=>EditAsync(new(){["action"]="rename",["name"]=name.Text,["ident"]=ident.Text}))))));
        var create=Button(AppText.Get("FleetPage.Text133"),()=>RunAsync(CloneAsync));create.Name="CreateFleetShip";
        var select=Button(AppText.Get("FleetPage.Text134"),()=>RunAsync(()=>EditAsync(new(){["action"]="select"})));select.Name="SelectFleetShip";
        var remove=Button(AppText.Get("FleetPage.Text135"),()=>RunAsync(DeleteAsync));remove.Name="DeleteFleetShip";
        Body.Children.Add(Horizontal(create,select,remove));
        var launch=Button(AppText.Get("FleetPage.Text136"),()=>RunAsync(async()=>
        {
            if(AccountId is not {} id)throw new InvalidOperationException(AppText.Get("FleetPage.Text137"));
            await ManagedGameLauncher.StartAsync(id,Lifetime.Token);Success(AppText.Get("FleetPage.Text138"));
        }),true);launch.Name="LaunchManagedSolo";Body.Children.Add(launch);
        BuildEditorWorkbench();
        var advanced=new Expander{Header=AppText.Get("FleetPage.Text148"),HorizontalAlignment=HorizontalAlignment.Stretch};
        advanced.Content=Horizontal(Button(AppText.Get("FleetPage.Text149"),()=>RunAsync(ExportAsync)),Button(AppText.Get("FleetPage.Text150"),()=>RunAsync(ImportAsync)));
        Body.Children.Add(advanced);
        Loaded+=async(_,_)=>await RunAsync(RefreshAsync);
    }
    private async Task RefreshAsync()
    {
        if(!DesktopContext.State.IsLoggedIn)return;
        if(DesktopContext.IsAdmin)
        {
            var old=AccountId;var rows=(await DesktopContext.Local.AccountsAsync(Lifetime.Token)).Rows().Select(value=>new AccountChoice(value.Text("id"),value.Text("email"),value.Text("displayName"),value.Text("role"))).ToArray();
            refreshing=true;try{accounts.ItemsSource=rows;accounts.SelectedItem=rows.FirstOrDefault(value=>value.Id==old)??rows.FirstOrDefault();}finally{refreshing=false;}
        }
        await LoadAsync();
    }
    private async Task LoadAsync()
    {
        if(string.IsNullOrEmpty(AccountId))return;
        var old=(ships.SelectedItem as FleetShipChoice)?.Id;
        fleet=await DesktopContext.Local.FleetAsync(AccountId,Lifetime.Token);version=fleet["commander"].Text("version");
        var rows=fleet.Flag("native")?fleet["ships"].Rows().Select(value=>new FleetShipChoice((uint)value.Number("id"),value["label"].Text("name"),value.Text("prototype"),value.Flag("active"))).ToArray():[];
        ships.ItemsSource=rows;ships.SelectedItem=rows.FirstOrDefault(value=>value.Id==old)??rows.FirstOrDefault();
        storage.ItemsSource=fleet["stored"].Rows().Select(value=>new StoredModuleChoice((uint)value.Number("slotId"),value["module"].Text("name"))).ToArray();
        if(!fleet.Flag("native"))details.Text=AppText.Get("FleetPage.Text151");
        SelectShip();
    }
    private void SelectShip()
    {
        if(ships.SelectedItem is not FleetShipChoice choice){slots.ItemsSource=null;return;}
        var ship=fleet?["ships"].Rows().Single(value=>value.Number("id")==choice.Id);
        name.Text=ship?["label"].Text("name")??"";ident.Text=ship?["label"].Text("ident")??"";
        details.Text=AppText.Format("FleetPage.Text152", (choice.Id), (choice.Prototype), (ship?["fuel"].Text("level")), (ship?["fuel"].Text("capacity")));
        if(choice.Active&&fleet.Flag("liveRefit")&&fleet?["liveCargoCapacity"] is {} capacity)details.Text+="\n"+AppText.Format("Refit.LiveCargoCapacity",capacity.ToString());
        editorSlots=ship is null?[]:FleetEditorProjection.Slots(ship);
        RefreshEditorCategories();
        FilterEditorSlots();
    }
    private async Task EditAsync(JsonObject command)
    {
        if(!DesktopContext.State.CanEditShips)throw new UnauthorizedAccessException(AppText.Get("FleetPage.Text153"));
        if(ships.SelectedItem is not FleetShipChoice ship)throw new InvalidOperationException(AppText.Get("FleetPage.Text154"));
        command["shipId"]=ship.Id;
        var result=command["action"]?.GetValue<string>() is "module" or "store" or "retrieve"
            ?await DesktopContext.Local.RefitAsync(AccountId,command,version,Lifetime.Token)
            :await DesktopContext.Local.EditFleetAsync(AccountId,command,version,Lifetime.Token);
        await LoadAsync();
        ShowFleetApplyResult(result);
    }
    private async Task CloneAsync()
    {
        if(ships.SelectedItem is not FleetShipChoice source)throw new InvalidOperationException(AppText.Get("FleetPage.Text156"));
        var label=new TextBox{Text=source.Name+AppText.Get("FleetPage.Text157"),MaxLength=100};var callSign=new TextBox{Text="XFE-NEW",MaxLength=24};
        var panel=new StackPanel{Spacing=12};panel.Children.Add(new TextBlock{Text=AppText.Get("FleetPage.Text158"),TextWrapping=TextWrapping.Wrap});panel.Children.Add(label);panel.Children.Add(callSign);
        if(await ShowAsync(AppText.Get("FleetPage.Text159"),panel,AppText.Get("FleetPage.Text160"))!=ContentDialogResult.Primary)return;
        await EditAsync(new(){["action"]="clone",["templateShipId"]=source.Id,["name"]=label.Text,["ident"]=callSign.Text});
    }
    private async Task DeleteAsync()
    {
        if(ships.SelectedItem is not FleetShipChoice ship)return;
        if(await ShowAsync(AppText.Get("FleetPage.Text161"),new TextBlock{Text=AppText.Format("FleetPage.Text162", (ship.Name)),TextWrapping=TextWrapping.Wrap},AppText.Get("FleetPage.Text163"))==ContentDialogResult.Primary)
            await EditAsync(new(){["action"]="remove"});
    }
    private Task SlotAction(string action)
    {
        if(slots.SelectedItem is not FleetEditorSlot slot)throw new InvalidOperationException(AppText.Get("FleetPage.Text164"));
        var command=new JsonObject{["action"]=action,["slot"]=slot.Name};
        if(action=="module")command["module"]=(modules.SelectedItem as ModuleChoice)?.Symbol??throw new InvalidOperationException(AppText.Get("FleetPage.Text165"));
        if(action=="retrieve")command["storageId"]=(storage.SelectedItem as StoredModuleChoice)?.Id??throw new InvalidOperationException(AppText.Get("FleetPage.Text166"));
        return EditAsync(command);
    }
    private async Task ExportAsync()
    {
        var save=await DesktopContext.Local.ExportNativeAsync(AccountId,Lifetime.Token);
        var text=new TextBox{Text=save.ToJsonString(new(){WriteIndented=true}),IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.NoWrap,MinWidth=600,MaxHeight=480};
        await ShowAsync(AppText.Get("FleetPage.Text167"),text);
    }
    private async Task ImportAsync()
    {
        var text=new TextBox{AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,MinWidth=600,MinHeight=300,MaxHeight=480,MaxLength=4*1024*1024,PlaceholderText=AppText.Get("FleetPage.Text168")};
        if(await ShowAsync(AppText.Get("FleetPage.Text169"),text,AppText.Get("FleetPage.Text170"))!=ContentDialogResult.Primary)return;
        await DesktopContext.Local.ImportNativeAsync(AccountId,JsonNode.Parse(text.Text)?.AsObject()??throw new InvalidDataException(AppText.Get("FleetPage.Text171")),version,Lifetime.Token);
        await LoadAsync();Success(AppText.Get("FleetPage.Text172"));
    }
}
