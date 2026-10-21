using XFEEliteDangerous.Client;
using System.Text.Json;
using System.Text.Json.Nodes;
using XFEEliteDangerous.Core;
using XFEEliteDangerous.ManagementClient;

namespace XFEEliteDangerous.Desktop;

/// <summary>Management API adapter; the desktop never opens the server's state file.</summary>
public sealed class LocalClient : IDisposable
{
    private ManagementApi? api;
    private string? address;
    public string? Token {get;set;}
    private static JsonObject Input(object value)=>JsonSerializer.SerializeToNode(value,Wire.Json)!.AsObject();
    public Task<JsonNode> StatusAsync(CancellationToken cancel=default)=>Call("status",null,cancel);
    public Task<JsonNode> LoginAsync(string email,string password,CancellationToken cancel=default)=>Call("auth/login",new{email,password},cancel);
    public Task<JsonNode> RegisterAsync(string email,string name,string password,CancellationToken cancel=default)=>Call("auth/register",new{email,name,password},cancel);
    public Task<JsonNode> ProfileAsync(CancellationToken cancel=default)=>Call("profile",null,cancel);
    public Task<JsonNode> AccountsAsync(CancellationToken cancel=default)=>Call("accounts",null,cancel);
    public Task<JsonNode> PermissionsAsync(CancellationToken cancel=default)=>Call("permissions",null,cancel);
    public Task<JsonNode> SettingsAsync(CancellationToken cancel=default)=>Call("settings",null,cancel);
    public Task<JsonNode> GalaxyAsync(CancellationToken cancel=default)=>Call("galaxy",null,cancel);
    public Task<JsonNode> EditGalaxyAsync(string version,string kind,string key,string payload,CancellationToken cancel=default)=>Call("galaxy/edit",new{version,kind,key,payload},cancel);
    public Task<JsonNode> CommanderAsync(string? id=null,CancellationToken cancel=default)=>Call("commander",new{accountId=id},cancel);
    public Task<JsonNode> CatalogAsync(string search="",CancellationToken cancel=default,string? accountId=null)=>Call("catalog",new{search,accountId},cancel);
    public Task<JsonNode> EditAsync(string? id,InventoryEdit operation,object values,string version,CancellationToken cancel=default)=>Call("inventory/edit",new{accountId=id,edit=operation.ToString().ToLowerInvariant(),values,version},cancel);
    public Task<JsonNode> EditQuantityAsync(string? id,string category,string itemId,string itemCategory,long quantity,string version,CancellationToken cancel=default)
    {
        if(category is not("cargo" or "materials"))throw new ArgumentException(AppText.Get("LocalClient.Text286"));
        return EditAsync(id,category=="cargo"?InventoryEdit.Cargo:InventoryEdit.Materials,new{itemId,category=itemCategory,quantity},version,cancel);
    }
    public Task<JsonNode> ImportJournalAsync(string id,JsonObject commander,string version,CancellationToken cancel=default)=>Call("journal/import",new{accountId=id,commander,version},cancel);
    public Task<JsonNode> SetRoleAsync(string id,string role,CancellationToken cancel=default)=>Call("accounts/role",new{accountId=id,role},cancel);
    public Task<JsonNode> CreateAccountAsync(string email,string name,string password,string role,CancellationToken cancel=default)=>Call("accounts/create",new{email,name,password,role},cancel);
    public Task<JsonNode> UpdatePermissionsAsync(bool credits,bool cargo,bool materials,bool ships,CancellationToken cancel=default)=>Call("permissions/update",new{permissions=new Permissions{AllowPlayerCredits=credits,AllowPlayerCargo=cargo,AllowPlayerMaterials=materials,AllowPlayerShipEditing=ships}},cancel);
    public Task<JsonNode> UpdateSettingsAsync(string name,string gameDirectory,bool registration,CancellationToken cancel=default)=>Call("settings/update",new{name,gameDirectory,registration},cancel);
    public Task<JsonNode> FleetAsync(string? id,CancellationToken cancel=default)=>Call("fleet",new{accountId=id},cancel);
    public Task<JsonNode> EditFleetAsync(string? id,JsonObject edit,string version,CancellationToken cancel=default)
    {var input=(JsonObject)edit.DeepClone();input["accountId"]=id;input["version"]=version;return Call("fleet/edit",input,cancel);}
    public Task<JsonNode> PrepareMaterialsAsync(string? id,string version,CancellationToken cancel=default)=>Call("materials/prepare",new{accountId=id,version},cancel);
    public Task<JsonNode> ExportNativeAsync(string? id,CancellationToken cancel=default)=>Call("native/export",new{accountId=id},cancel);
    public Task<JsonNode> InitializeNativeAsync(string? id,string version,CancellationToken cancel=default)=>Call("native/initialize",new{accountId=id,version},cancel);
    public Task<JsonNode> ImportNativeAsync(string? id,JsonObject save,string version,CancellationToken cancel=default)=>Call("native/import",new{accountId=id,save,version},cancel);
    public async Task<bool> ConnectLocalOwnerAsync()
    {
        var path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"XFEEliteDangerous","Server","admin-access.json");
        if(!File.Exists(path))return false;
        var access=JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        SystemProfile.ServerAddress=access["address"]!.GetValue<string>();
        Token=access["key"]!.GetValue<string>();
        try
        {
            var result=await Call("auth/local-owner",null,default);
            Token=result["token"]!.GetValue<string>();DesktopContracts.ApplyPlayerAccount(result["account"]!);DesktopContext.State.IsLoggedIn=true;
            await DesktopContext.RefreshPermissionsAsync();return true;
        }
        catch{Token=null;throw;}
    }
    public void Logout()
    {
        var previous=Token;Token=null;
        if(previous is not null)_=LogoutServerAsync(previous);
    }
    private async Task LogoutServerAsync(string token)
    {try{if(api is not null)await api.CallAsync("auth/logout",token:token);}catch(Exception error){DesktopContext.Log(error);}}
    private async Task<JsonNode> Call(string operation,object? values,CancellationToken cancel)
    {
        if(DesktopVerification.IsRunning)throw new InvalidOperationException(AppText.Get("LocalClient.Text287"));
        if(api is null||address!=SystemProfile.ServerAddress)
        {api?.Dispose();address=SystemProfile.ServerAddress;api=new(address);}
        var session=Token;
        try{return await api.CallAsync(operation,values is JsonObject json?json:values is null?null:Input(values),session,cancel).ConfigureAwait(false);}
        catch(LocalSessionException){await DesktopContext.InvalidateSessionAsync(session).ConfigureAwait(false);throw;}
    }
    public void Dispose()=>api?.Dispose();
}
