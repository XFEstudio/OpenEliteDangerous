using System.Text.Json.Nodes;

namespace XFEEliteDangerous.Core;

public sealed partial class OfflineWorkspace
{
    public async Task InitializeSharedGalaxyAsync()
    {
        var seeds=await store.Read(state=>state.Accounts.Values.Where(account=>!account.Disabled&&account.NativeSave?.SystemMetadata is not null)
            .Select(account=>account.NativeSave!.SystemMetadata!).GroupBy(system=>system.Address).Select(group=>group.First()).ToArray());
        var galaxy=new SharedGalaxyStore(DirectoryPath);foreach(var seed in seeds)galaxy.System(seed.Address,seed);
    }
    public async Task<JsonNode> GalaxyAsync(string? token)
    {
        await store.Read(state=>{RequireAdministrator(Authenticate(state,token));return true;});
        var galaxy=new SharedGalaxyStore(DirectoryPath).Snapshot();var references=new NativeReferenceLibrary(DirectoryPath);
        return Json(new{version=galaxy.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture),systems=galaxy.Systems.Select(pair=>new{address=pair.Key,payload=pair.Value}).ToArray(),
            definitions=SharedGalaxyStore.DefinitionOperations.Select(operation=>new{operation,payload=galaxy.Definitions.GetValueOrDefault(operation)??references.Read(operation)?.Payload??"",overridden=galaxy.Definitions.ContainsKey(operation)}).ToArray()});
    }
    public async Task<JsonNode> EditGalaxyAsync(string? token,string version,string kind,string key,string payload)
    {
        await store.Read(state=>{RequireAdministrator(Authenticate(state,token));return true;});
        new SharedGalaxyStore(DirectoryPath).Update(version,kind,key,payload);
        return await GalaxyAsync(token);
    }
}
