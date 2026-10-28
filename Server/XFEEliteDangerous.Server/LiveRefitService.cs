using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using XFEEliteDangerous.Client;
using XFEEliteDangerous.Core;

namespace XFEEliteDangerous.Server;

/// <summary>Waits outside the save/session locks for an original-game-thread acknowledgement.</summary>
internal sealed class LiveRefitService(OfflineWorkspace workspace, OnlineGameHost online, string? artifactsRoot)
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> gates = new(StringComparer.Ordinal);
    internal async Task<JsonNode> ApplyAsync(string? token, JsonObject input, Func<Task<JsonNode>> idleEdit)
    {
        var version = input["version"]?.GetValue<string>() ?? throw new InvalidDataException("Missing refit version.");
        var prepared = await workspace.PrepareLiveRefitAsync(token, input["accountId"]?.GetValue<string>(), input, version);
        var gate = gates.GetOrAdd(prepared.AccountId, _ => new(1, 1));
        if (!await gate.WaitAsync(0)) throw new ManagementBusyException(AppText.Get("Refit.Busy"));
        try
        {
            var mailbox = new LiveRefitMailbox(workspace.DirectoryPath, prepared.AccountId);
            var status = mailbox.ReadStatus();
            if (!LiveRefitMailbox.Fresh(status, DateTimeOffset.UtcNow)) return await idleEdit();
            if (status!.ImageSha256 != "e6be8bbe04e6a7ae226d4318945af7f367de13dc5a007a261964d9ba8144e988"
                || !(online.OwnsRefit(prepared.AccountId, status.RuntimeId) || OwnsLocal(prepared.AccountId, status)))
                throw new ManagementBusyException(AppText.Get("Refit.RuntimeUnavailable"));
            if (status.Flow == "MainMenu") return await idleEdit();
            if (status.Flow != "InGame" || status.ShipId != prepared.ShipId || status.ShipEntityId == 0)
                throw new ManagementBusyException(AppText.Get("Refit.ActiveShipRequired"));
            var slot = status.Slots.SingleOrDefault(value => value.Slot == prepared.Slot);
            if (slot is null || !LiveRefitMailbox.Matches(slot.Module, prepared.Before)) throw new SaveConflictException();
            // Recheck version after acquiring this account's live-edit gate.
            prepared = await workspace.PrepareLiveRefitAsync(token, prepared.AccountId, input, version, slot);
            prepared=prepared with{After=NativeState(prepared.After,status)};
            var command = new LiveRefitCommand(prepared.Id, status.RuntimeId, DateTimeOffset.UtcNow.AddSeconds(8),
                prepared.ShipId, status.ShipEntityId, prepared.Slot, prepared.Action, prepared.Before, prepared.After);
            mailbox.Submit(command);
            OfflineDiagnostics.Current?.Write("info", "refit", "command.queued", "Validated refit sent to the current native ship.",
                new Dictionary<string, string?> { ["account"] = prepared.AccountId, ["commandId"] = command.Id, ["slot"] = prepared.Slot });
            while (DateTimeOffset.UtcNow < command.ExpiresUtc.AddSeconds(10))
            {
                await Task.Delay(100);
                var reply = mailbox.ReadStatus();
                if (reply?.RuntimeId != command.RuntimeId || reply.Result?.CommandId != command.Id) continue;
                var result = reply.Result;
                if (!result.Applied)
                {
                    if(result.Restored)await workspace.RestoreFailedLiveRefitAsync(prepared,result);
                    throw new InvalidOperationException(AppText.Format("Refit.ClientRejected", result.Code));
                }
                JsonNode committed;
                try{committed=await workspace.CommitLiveRefitAsync(token,prepared,result);}
                catch(Exception saveError)
                {
                    var rollback=new LiveRefitCommand(Guid.NewGuid().ToString("N"),command.RuntimeId,DateTimeOffset.UtcNow.AddSeconds(8),command.ShipId,
                        command.ShipEntityId,command.Slot,command.Action is "properties" or "ammunition"?command.Action:"module",result.Actual?.Module,NativeState(prepared.Before,status));
                    mailbox.Submit(rollback);LiveRefitResult? restored=null;
                    while(DateTimeOffset.UtcNow<rollback.ExpiresUtc.AddSeconds(1))
                    {
                        await Task.Delay(100);var response=mailbox.ReadStatus();
                        if(response?.RuntimeId==rollback.RuntimeId&&response.Result?.CommandId==rollback.Id){restored=response.Result;break;}
                    }
                    if(restored is {Applied:true})
                    {
                        try{await workspace.RestoreFailedLiveRefitAsync(prepared,restored);}
                        catch(Exception recoveryError)
                        {
                            OfflineDiagnostics.Current?.Write("critical","refit","recovery.save_failed","Native rollback was verified, but save reconciliation failed; request history retained.",
                                new Dictionary<string,string?>{["commandId"]=command.Id,["rollbackId"]=rollback.Id},recoveryError);
                            throw new ManagementBusyException(AppText.Get("Refit.RecoveryRequired"));
                        }
                        OfflineDiagnostics.Current?.Write("warning","refit","command.rolled_back","Save commit failed; original native module state was restored and verified.",
                            new Dictionary<string,string?>{["commandId"]=command.Id,["rollbackId"]=rollback.Id},saveError);
                        throw new ManagementBusyException(AppText.Get("Refit.CommitRolledBack"));
                    }
                    OfflineDiagnostics.Current?.Write("critical","refit","rollback.incomplete","Save commit and native rollback could not both be verified; request history retained.",
                        new Dictionary<string,string?>{["commandId"]=command.Id,["rollbackId"]=rollback.Id},saveError);
                    throw new ManagementBusyException(AppText.Get("Refit.RecoveryRequired"));
                }
                OfflineDiagnostics.Current?.Write("info", "refit", "command.committed", "Native readback verified and merged into the latest save.",
                    new Dictionary<string, string?> { ["commandId"] = command.Id, ["slot"] = prepared.Slot });
                return committed;
            }
            throw new ManagementBusyException(AppText.Get("Refit.Timeout"));
        }
        finally { gate.Release(); }
    }
    private static NativeModuleSnapshot? NativeState(NativeModuleSnapshot? module,LiveRefitStatus status)
    {
        if(module is null)return null;
        var source=status.Slots.FirstOrDefault(value=>value.Module?.Id==module.Id&&value.Module.Name==module.Name);
        // Indestructible / unpowered modules have no corresponding runtime
        // property. Keep their serialized defaults in Candidate, and use the
        // explicitly read-only runtime placeholder only in the native command.
        return source?.Module is {} observed?module with{Priority=source.PriorityEditable?module.Priority:observed.Priority,
            HealthMillion=source.IntegrityEditable?module.HealthMillion:observed.HealthMillion}:module;
    }
    private bool OwnsLocal(string account, LiveRefitStatus status)
    {
        if (artifactsRoot is null || !Directory.Exists(artifactsRoot)) return false;
        foreach (var directory in Directory.EnumerateDirectories(artifactsRoot, "offline-game-*"))
        {
            var path = Path.Combine(directory, "launch.json"); if (!File.Exists(path)) continue;
            try
            {
                var launch = JsonNode.Parse(File.ReadAllText(path));
                if (launch?["ProcessId"]?.GetValue<int>() != status.ProcessId || launch["online"]?.GetValue<bool>() == true
                    || launch["accountId"]?.GetValue<string>() != account
                    || !string.Equals(launch["workspace"]?.GetValue<string>(), workspace.DirectoryPath, StringComparison.OrdinalIgnoreCase)) continue;
                using var process = System.Diagnostics.Process.GetProcessById(status.ProcessId);
                return !process.HasExited && Math.Abs((process.StartTime.ToUniversalTime() - File.GetLastWriteTimeUtc(path)).TotalSeconds) <= 15
                    && string.Equals(process.MainModule?.FileName, @"E:\Elite Dangerous\Products\elite-dangerous-odyssey-64\EliteDangerous64.exe", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception error) when (error is IOException or System.Text.Json.JsonException or ArgumentException or System.ComponentModel.Win32Exception) { }
        }
        return false;
    }
}
