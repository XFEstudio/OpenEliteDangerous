using System.Text.Json.Nodes;
using System.Text.Json;
using System.Security.Cryptography;
using XFEEliteDangerous.Client;

namespace XFEEliteDangerous.Core;

public sealed record PreparedLiveRefit(string Id, string AccountId, uint ShipId, string Slot, string Action,
    NativeModuleSnapshot? Before, NativeModuleSnapshot? After, NativeCommanderSave Candidate, string StorageBeforeHash);

public sealed partial class OfflineWorkspace
{
    public async Task<JsonNode> SaveIdleRefitAsync(string? session,string? targetId,JsonObject input,string version)
    {
        var prepared=await PrepareLiveRefitAsync(session,targetId,input,version);
        return await store.Update(state=>
        {
            var actor=Authenticate(state,session);var account=Target(state,actor,prepared.AccountId);CheckVersion(account.Commander,version);
            if(actor.Role!=AccountRoles.Administrator&&!state.Permissions.AllowPlayerShipEditing)throw new UnauthorizedAccessException(AppText.Get("FleetPage.Text153"));
            return CommitManagedNative(account,prepared.Candidate);
        });
    }
    public Task<PreparedLiveRefit> PrepareLiveRefitAsync(string? session, string? targetId, JsonObject input, string version, LiveRefitSlot? observed = null) => store.Read(state =>
    {
        var actor = Authenticate(state, session); var account = Target(state, actor, targetId);
        if (actor.Role != AccountRoles.Administrator && !state.Permissions.AllowPlayerShipEditing)
            throw new UnauthorizedAccessException(AppText.Get("FleetPage.Text153"));
        CheckVersion(account.Commander, version);
        var save = account.NativeSave?.ExportCurrent(account) ?? throw new InvalidDataException(AppText.Get("OfflineWorkspace_Fleet.Text405"));
        var shipId = (uint)Integer(input, "shipId", 1, uint.MaxValue - 1L);
        var ship = save.Snapshot.Vessels.SingleOrDefault(value => value.Id == shipId) ?? throw new InvalidDataException(AppText.Get("OfflineWorkspace_Fleet.Text406"));
        var slotName = Text(input, "slot", 100);
        var slot = ship.Modules.SingleOrDefault(value => value.Name == slotName) ?? throw new InvalidDataException(AppText.Get("OfflineWorkspace_Fleet.Text410"));
        if (observed is not null)
        {
            if (observed.Slot != slotName || !LiveRefitMailbox.Matches(observed.Module, slot.Module)) throw new SaveConflictException();
            if (slot.Module is not null && observed.Module is not null)
                slot = slot with { Module = slot.Module with { On = observed.Module.On, Priority = observed.PriorityEditable?observed.Module.Priority:slot.Module.Priority, HealthMillion = observed.IntegrityEditable?observed.Module.HealthMillion:slot.Module.HealthMillion,
                    Ammo=observed.Module.Ammo,Engineering=observed.Module.Engineering } };
            ship = ship with { Modules = ship.Modules.Select(value => value.Name == slotName ? slot : value).ToArray() };
            save = save with { Snapshot = save.Snapshot with { Vessels = save.Snapshot.Vessels.Select(value => value.Id == shipId ? ship : value).ToArray() } };
        }
        var action = Text(input, "action", 40);
        NativeModuleSnapshot? after;
        NativeCommanderSave candidate;
        if (action == "properties")
        {
            var before = slot.Module ?? throw new InvalidDataException(AppText.Get("Refit.SelectSlot"));
            var enabled = Boolean(input, "enabled");
            var priority = (uint)Integer(input, "priority", 0, 4);
            if(observed is {PriorityEditable:false}&&priority!=before.Priority)throw new InvalidDataException(AppText.Get("Refit.ReadOnlyPriority"));
            if (input["health"] is not JsonValue value || !value.TryGetValue<double>(out var health) || !double.IsFinite(health) || health is < 0 or > 1)
                throw new InvalidDataException(AppText.Get("Refit.InvalidHealth"));
            if(observed is {IntegrityEditable:false}&&health!=1)throw new InvalidDataException(AppText.Get("Refit.ReadOnlyIntegrity"));
            after = before with { On = enabled, Priority = priority, HealthMillion = (uint)Math.Round(health * 1_000_000, MidpointRounding.AwayFromZero) };
            var changed = ship with { Modules = ship.Modules.Select(value => value.Name == slotName ? value with { Module = after } : value).ToArray() };
            candidate = save with { Snapshot = save.Snapshot with { Vessels = save.Snapshot.Vessels.Select(value => value.Id == shipId ? changed : value).ToArray() } };
        }
        else if(action is "engineering" or "ammunition")
        {
            var before=slot.Module??throw new InvalidDataException(AppText.Get("Refit.SelectSlot"));
            if(action=="ammunition"&&observed is {AmmoEditable:false})throw new InvalidDataException(AppText.Get("Refit.AmmoUnavailable"));
            after=action=="engineering"?before with{Engineering=NativeEngineering.Prepare(before.Name,input)}
                :before with{Ammo=new((uint)Integer(input,"clip",0,1_000_000),(uint)Integer(input,"hopper",0,1_000_000))};
            var changed=ship with{Modules=ship.Modules.Select(value=>value.Name==slotName?value with{Module=after}:value).ToArray()};
            candidate=save with{Snapshot=save.Snapshot with{Vessels=save.Snapshot.Vessels.Select(value=>value.Id==shipId?changed:value).ToArray()}};
        }
        else if (action is "module" or "store" or "retrieve")
        {
            candidate = EditFleetModule(save, ship, input, action);
            after = candidate.Snapshot.Vessels.Single(value => value.Id == shipId).Modules.Single(value => value.Name == slotName).Module;
        }
        else throw new InvalidDataException(AppText.Get("Refit.UnsupportedAction"));
        var inventory = candidate.CreateInventory(account, account.Commander.Revision);
        candidate.ValidateCurrent(new() { NativeUserId = account.NativeUserId, NativeSave = candidate, Commander = inventory });
        return new PreparedLiveRefit(Guid.NewGuid().ToString("N"), account.Id, shipId, slotName, action, slot.Module, after, candidate, StorageHash(save));
    });

    public Task<JsonNode> CommitLiveRefitAsync(string? session, PreparedLiveRefit prepared, LiveRefitResult result) => store.Update(state =>
    {
        var actor = Authenticate(state, session); var account = Target(state, actor, prepared.AccountId);
        if (actor.Role != AccountRoles.Administrator && !state.Permissions.AllowPlayerShipEditing)
            throw new UnauthorizedAccessException(AppText.Get("FleetPage.Text153"));
        if (result.CommandId != prepared.Id || !result.Applied || result.Actual is not { } actual || actual.Slot != prepared.Slot || !LiveRefitMailbox.Matches(actual.Module, prepared.After)
            || actual.Module?.On != prepared.After?.On || actual.Module?.Priority != prepared.After?.Priority
            || !NativeEngineering.Equivalent(actual.Module?.Engineering,prepared.After?.Engineering)
            || prepared.Action!="properties"&&prepared.After?.Ammo is {} requestedAmmo&&actual.Module?.Ammo!=requestedAmmo
            || Math.Abs((long)(actual.Module?.HealthMillion ?? 0) - (prepared.After?.HealthMillion ?? 0)) > 2)
            throw new InvalidDataException(AppText.Get("Refit.ReadbackFailed"));
        var save = account.NativeSave?.ExportCurrent(account) ?? throw new SaveConflictException();
        var ship = save.Snapshot.Vessels.SingleOrDefault(value => value.Id == prepared.ShipId);
        var slot = ship?.Modules.SingleOrDefault(value => value.Name == prepared.Slot);
        if (save.Snapshot.CurrentVesselId != prepared.ShipId || slot is null
            || !(LiveRefitMailbox.Matches(slot.Module, prepared.Before) || LiveRefitMailbox.Matches(slot.Module, prepared.After))) throw new SaveConflictException();
        var warehouseEdit=prepared.Action is "module" or "store" or "retrieve";
        if (warehouseEdit && StorageHash(save) != prepared.StorageBeforeHash) throw new SaveConflictException();
        var savedModule=prepared.Candidate.Snapshot.Vessels.Single(value=>value.Id==prepared.ShipId).Modules.Single(value=>value.Name==prepared.Slot).Module;
        var applied = savedModule is null ? null : savedModule with { On = actual.Module!.On,
            Priority = actual.PriorityEditable?actual.Module.Priority:savedModule.Priority, HealthMillion = actual.IntegrityEditable?actual.Module.HealthMillion:savedModule.HealthMillion,
            Ammo=actual.Module.Ammo,Engineering=actual.Module.Engineering };
        ship = ship! with { Modules = ship!.Modules.Select(value => value.Name == prepared.Slot ? value with { Module = applied } : value).ToArray() };
        var candidate = save with { Snapshot = save.Snapshot with { Vessels = save.Snapshot.Vessels.Select(value => value.Id == prepared.ShipId ? ship : value).ToArray() } };
        if (warehouseEdit)
        {
            var storage=prepared.Candidate.StoredModules.ToArray();
            if(result.Previous is {} nativeBefore&&prepared.Before is not null&&storage.Length>0&&LiveRefitMailbox.Matches(storage[^1].Module,prepared.Before))
                storage[^1]=storage[^1] with{Module=storage[^1].Module with{Ammo=nativeBefore.Ammo,Engineering=nativeBefore.Engineering,On=nativeBefore.On,HealthMillion=nativeBefore.HealthMillion}};
            candidate=candidate with{StoredModules=storage,Catalogue=candidate.Catalogue with{
                Modules=candidate.Catalogue.Modules.Concat(prepared.Candidate.Catalogue.Modules.Where(value=>!candidate.Catalogue.Modules.Any(old=>old.Name==value.Name))).ToArray(),
                Ships=candidate.Catalogue.Ships.Select(value=>value.Name==ship.Name?prepared.Candidate.Catalogue.Ships.Single(changed=>changed.Name==value.Name):value).ToArray()}};
        }
        var committed = CommitManagedNative(account, candidate);
        committed["requiresReload"] = false; committed["appliedInGame"] = true; committed["commandId"] = result.CommandId;
        return committed;
    });
    private static string StorageHash(NativeCommanderSave save) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(save.StoredModules, Wire.Json)));

    /// <summary>Compensates only this previously authorized native edit, after verified game rollback.</summary>
    public Task RestoreFailedLiveRefitAsync(PreparedLiveRefit prepared, LiveRefitResult rollback) => store.Update(state =>
    {
        var before=prepared.Before;
        if(rollback.Restored&&before is not null&&rollback.Previous is {} previous&&LiveRefitMailbox.Matches(previous,before))
            before=before with{On=previous.On,HealthMillion=previous.HealthMillion,Ammo=previous.Ammo,Engineering=previous.Engineering,
                Priority=rollback.Actual?.PriorityEditable==true?previous.Priority:before.Priority};
        if((!rollback.Applied&&!rollback.Restored)||rollback.Actual is not {} actual||actual.Slot!=prepared.Slot||!LiveRefitMailbox.Matches(actual.Module,before)
            ||actual.Module?.On!=before?.On||actual.PriorityEditable&&actual.Module?.Priority!=before?.Priority
            ||!NativeEngineering.Equivalent(actual.Module?.Engineering,before?.Engineering)
            ||before?.Ammo is {} ammo&&actual.Module?.Ammo!=ammo
            ||actual.IntegrityEditable&&Math.Abs((long)(actual.Module?.HealthMillion??0)-(before?.HealthMillion??0))>2)throw new InvalidDataException(AppText.Get("Refit.ReadbackFailed"));
        if(!state.Accounts.TryGetValue(prepared.AccountId,out var account))throw new SaveConflictException();
        var save=account.NativeSave?.ExportCurrent(account)??throw new SaveConflictException();
        var ship=save.Snapshot.Vessels.SingleOrDefault(value=>value.Id==prepared.ShipId)??throw new SaveConflictException();
        var slot=ship.Modules.SingleOrDefault(value=>value.Name==prepared.Slot)??throw new SaveConflictException();
        if(save.Snapshot.CurrentVesselId!=prepared.ShipId||!(LiveRefitMailbox.Matches(slot.Module,prepared.Before)||LiveRefitMailbox.Matches(slot.Module,prepared.After)))throw new SaveConflictException();
        ship=ship with{Modules=ship.Modules.Select(value=>value.Name==prepared.Slot?value with{Module=before}:value).ToArray()};
        _=CommitManagedNative(account,save with{Snapshot=save.Snapshot with{Vessels=save.Snapshot.Vessels.Select(value=>value.Id==ship.Id?ship:value).ToArray()}});
        return true;
    });
}
