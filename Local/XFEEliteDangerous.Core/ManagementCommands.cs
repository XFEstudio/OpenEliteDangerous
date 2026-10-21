using XFEEliteDangerous.Client;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XFEEliteDangerous.Core;

/// <summary>Explicit management operations. Authentication remains in OfflineWorkspace.</summary>
public sealed class ManagementCommands(OfflineWorkspace workspace)
{
    public async Task<JsonNode> ExecuteAsync(string operation,string? token,JsonObject input)
    {
        string Text(string key)=>input[key]?.GetValue<string>()??throw new InvalidDataException(AppText.Get("ManagementCommands.Text353")+key);
        string? Id()=>input["accountId"]?.GetValue<string>();
        return operation switch
        {
            "status"=>await workspace.StatusAsync(),
            "auth/login"=>await workspace.LoginAsync(Text("email"),Text("password")),
            "auth/register"=>await workspace.RegisterAsync(Text("email"),Text("name"),Text("password")),
            "auth/logout"=>Logout(),
            "profile"=>await workspace.ProfileAsync(token),
            "accounts"=>await workspace.AccountsAsync(token),
            "accounts/create"=>await workspace.CreateAccountAsync(token,Text("email"),Text("name"),Text("password"),Text("role")),
            "accounts/role"=>await workspace.SetRoleAsync(token,Text("accountId"),Text("role")),
            "permissions"=>await workspace.PermissionsAsync(token),
            "permissions/update"=>await workspace.UpdatePermissionsAsync(token,input["permissions"]?.Deserialize<Permissions>(Wire.Json)??throw new InvalidDataException(AppText.Get("ManagementCommands.Text354"))),
            "settings"=>await workspace.SettingsAsync(token),
            "settings/update"=>await workspace.UpdateSettingsAsync(token,Text("name"),Text("gameDirectory"),input["registration"]!.GetValue<bool>()),
            "commander"=>await workspace.CommanderAsync(token,Id()),
            "catalog"=>await workspace.CatalogAsync(token,input["search"]?.GetValue<string>()??"",Id()),
            "inventory/edit"=>await workspace.EditAsync(token,Id(),ParseEdit(Text("edit")),input["values"]?.AsObject()??throw new InvalidDataException(AppText.Get("ManagementCommands.Text355")),Text("version")),
            "journal/import"=>await workspace.ImportJournalAsync(token,Text("accountId"),input["commander"]!.AsObject(),Text("version")),
            "native/export"=>await workspace.ExportNativeSaveAsync(token,Id()),
            "native/import"=>await workspace.ImportNativeSaveAsync(token,Text("accountId"),input["save"]!.Deserialize<NativeCommanderSave>(Wire.Json)??throw new InvalidDataException(AppText.Get("ManagementCommands.Text356")),Text("version")),
            "fleet"=>await workspace.FleetAsync(token,Id()),
            "fleet/edit"=>await workspace.EditFleetAsync(token,Id(),input,Text("version")),
            "native/initialize"=>await workspace.InitializeNativeAsync(token,Id(),Text("version")),
            "materials/prepare"=>await workspace.PrepareMaterialsAsync(token,Id(),Text("version")),
            "galaxy"=>await workspace.GalaxyAsync(token),
            "galaxy/edit"=>await workspace.EditGalaxyAsync(token,Text("version"),Text("kind"),Text("key"),Text("payload")),
            _=>throw new InvalidDataException(AppText.Get("ManagementCommands.Text357")+operation)
        };
        JsonNode Logout(){workspace.Logout(token);return new JsonObject{["loggedOut"]=true};}
    }
    private static InventoryEdit ParseEdit(string value)=>value switch
    {"credits"=>InventoryEdit.Credits,"cargo"=>InventoryEdit.Cargo,"materials"=>InventoryEdit.Materials,"modules"=>InventoryEdit.Modules,_=>throw new InvalidDataException(AppText.Get("ManagementCommands.Text358"))};
}
