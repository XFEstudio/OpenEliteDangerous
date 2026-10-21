using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using XFEEliteDangerous.Client;

namespace XFEEliteDangerous.Core;

/// <summary>
/// Data source called by the owned native process. Path and verb are identifiers already emitted
/// by its original parser; there is no listener, connection, remote fallback or official identity.
/// </summary>
public sealed class OfflineGameData : INativeGameData
{
    private readonly StateStore store;
    private readonly string accountId;
    private readonly NativeReferenceLibrary references;
    private readonly HotNativeReadModule readModules;
    private readonly NativeJournalArchive journals;
    private readonly bool sharedMode;
    private readonly SharedGalaxyStore? galaxy;
    private readonly SemaphoreSlim calls = new(1, 1);
    private bool loggedIn, selected;
    private readonly string localPassword = Wire.Secret();
    public ulong RunId { get; } = NewRunId();
    public OfflineWorldAuthority World { get; }
    INativeWorldAuthority INativeGameData.World=>World;
    public long SaveCommits { get; private set; }
    public long JournalCommits { get; private set; }
    public OfflineGameData(string directory, string accountId,SharedWorldDirectory? sharedWorld=null)
    {
        OfflineDiagnostics.Current?.RegisterSecret(localPassword);
        this.accountId = accountId;
        sharedMode=sharedWorld is not null;
        galaxy=sharedMode?new SharedGalaxyStore(directory):null;
        references = new NativeReferenceLibrary(directory);
        readModules = new HotNativeReadModule(directory);
        store = new StateStore(directory);
        var user = store.Read(state => Account(state).NativeUserId).GetAwaiter().GetResult();
        World = new(RunId,user,sharedWorld?.Allocator,sharedWorld is null?null:target=>sharedWorld.Resolve(RunId,target),sharedWorld is null?null:committed=>sharedWorld.Complete(RunId,committed));
        World.SetRpcModule(request=>sharedWorld?.Handle(RunId,request)??readModules.InvokeRpc(request));
        journals=new(store.DirectoryPath,accountId);
    }

    public async Task<NativeDataReply> InvokeAsync(string method, string operation, IReadOnlyDictionary<string,string> fields, byte[] body)
    {
        using var diagnostic=OfflineDiagnostics.Current?.Begin("data.authority",method+" "+operation,new Dictionary<string,string?>
            {["method"]=method,["path"]=operation,["queryFields"]=string.Join(",",fields.Keys.Order()),["requestBytes"]=body.Length.ToString()});
        if (body.Length > 8 * 1024 * 1024 || operation.Length > 512) return Error(413, "Native data exceeds its local bound.");
        if (method is not ("GET" or "PUT" or "POST" or "DELETE")) return Error(405, "Unsupported native data operation.");
        await calls.WaitAsync().ConfigureAwait(false);
        try
        {
            var reply=await store.Update(state => Dispatch(state, Account(state), method, operation, fields, body)).ConfigureAwait(false);
            if(method=="PUT"&&operation=="/2.0/elite/commander/savegame"&&reply.Status is >=200 and <300)
            {
                SaveCommits++;
                OfflineDiagnostics.Current?.Write("info","save","save.commit","Authoritative save persisted by StateStore.",
                    new Dictionary<string,string?>{["saveCommits"]=SaveCommits.ToString(),["requestBytes"]=body.Length.ToString()});
            }
            OfflineDiagnostics.Current?.Write(reply.Status>=400?"warning":"debug","data.authority","data.reply","Local data operation completed.",
                new Dictionary<string,string?>{["httpStatus"]=reply.Status.ToString(),["responseBytes"]=System.Text.Encoding.UTF8.GetByteCount(reply.Payload).ToString()});
            diagnostic?.Complete(reply.Status>=400?"rejected":"completed");
            return reply;
        }
        catch (NativeDataException error) {
            diagnostic?.Complete("rejected");OfflineDiagnostics.Current?.Write("warning","data.authority","data.rejected","Local data contract rejected the operation.",
                new Dictionary<string,string?>{["httpStatus"]=error.Status.ToString()},error);
            return Error(error.Status, error.Message); }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidDataException or OverflowException)
        { diagnostic?.Complete("failed");OfflineDiagnostics.Current?.Write("error","data.authority","data.invalid","Local data validation failed.",exception:error);
            return Error(400, "Native local data is inconsistent: " + error.Message); }
        catch(Exception error){diagnostic?.Complete("failed");OfflineDiagnostics.Current?.Write("error","data.authority","data.failed","Local data/store operation failed.",exception:error);throw;}
        finally { calls.Release(); }
    }
    private AccountState Account(DatabaseState state) => state.Accounts.TryGetValue(accountId, out var account) && !account.Disabled
        ? account : throw new NativeDataException(401, "The selected local account is unavailable.");
    private NativeDataReply Dispatch(DatabaseState state, AccountState account, string method, string path, IReadOnlyDictionary<string,string> fields, byte[] body)
    {
        if (path == "/2.0/server/time")
        {
            NativeProfileData.Method(method, "GET"); NativeProfileData.Empty(body);
            return NativeDataReply.Json(new { unixTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
        }
        if (path == "/2.0/elite/user/login")
        {
            NativeProfileData.Method(method, "GET"); NativeProfileData.Empty(body); World.BindIdentity(fields); loggedIn = true;
            return NativeDataReply.Json(new { fid = "F" + account.NativeUserId.ToString(CultureInfo.InvariantCulture),
                user = new { email = account.Email }, runId = RunId, turnPassword = localPassword, turnUser = "XFE-" + RunId });
        }
        if (!loggedIn) return Error(401, "Select the local identity first.");
        World.SelectSystem(account.NativeSave?.Profile.CurrentSystem);
        if (fields.TryGetValue("runID", out var run) && (!ulong.TryParse(run, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id != RunId))
            return Error(401, "Local runtime identity differs.");
        if(galaxy?.Definition(path) is {} sharedDefinition)
        {
            NativeProfileData.Method(method,"GET");NativeProfileData.Empty(body);NativeProfileData.VerifyCommander(account,fields);return sharedDefinition;
        }
        if(galaxy is not null&&path=="/2.0/elite/starsystem")
        {
            NativeProfileData.Method(method,"GET");NativeProfileData.Empty(body);NativeProfileData.VerifyCommander(account,fields);
            if(!fields.TryGetValue("systemAddr",out var addressText)||!ulong.TryParse(addressText,NumberStyles.None,CultureInfo.InvariantCulture,out var sharedAddress))throw new NativeDataException(400,"Invalid shared system address.");
            return galaxy.System(sharedAddress,account.NativeSave?.SystemMetadata);
        }
        if (references.Read(path) is {} reference)
        {
            NativeProfileData.Method(method, "GET"); NativeProfileData.Empty(body);
            NativeProfileData.VerifyCommander(account, fields); return reference;
        }
        if (NativeProfileData.Handle(account, method, path, fields, body) is {} profile) return profile;
        if (readModules.Invoke(account,method,path,fields,body) is {} replacement) return replacement;
        if (NativeCommanderServices.Handle(account, method, path, fields, body) is {} services) return services;
        if (NativeBookmarkOperations.Handle(account, method, path, fields, body) is {} bookmarks) return bookmarks;
        if (NativeShipData.Handle(account, method, path, fields, body) is {} ship) return ship;
        if (NativeSystemData.Handle(account, method, path, fields, body) is {} system) return system;
        switch (path)
        {
            case "/2.0/elite/server/list":
                NativeProfileData.Method(method,"GET"); NativeProfileData.Empty(body); NativeProfileData.VerifyCommander(account,fields);
                return new(200,World.DirectoryDocument());
            case "/2.0/elite/commander/login":
                NativeProfileData.Method(method, "PUT"); NativeProfileData.Empty(body); NativeProfileData.VerifyCommander(account, fields);
                var gameMode=NativeProfileData.Text(fields,"gameMode");
                if(gameMode!="solo"&&(!sharedMode||gameMode!="open"))return Error(501,"Unsupported private game mode: "+gameMode);
                if (fields.TryGetValue("contentGating", out var gating) && gating is not ("0" or "1")) return Error(400, "Unknown local content mode.");
                selected = true; return NativeDataReply.Json(new{OK="Logged in"});
            case "/2.0/elite/commander/logout":
                NativeProfileData.Method(method, "PUT"); NativeProfileData.Empty(body);
                if (!fields.TryGetValue("cmdrId", out var commander) || commander != "Invalid") NativeProfileData.VerifyCommander(account, fields);
                selected = false; return new(200, "{}");
            case "/2.0/elite/user":
                NativeProfileData.Method(method, "GET"); NativeProfileData.Empty(body); return new(200, NativeUserXml.Write(account));
            case "/2.0/elite/commander/savegame":
                NativeProfileData.VerifyCommander(account, fields);
                if (method == "PUT") return readModules.InvokeSave(account,selected,body)??NativeSavegameUpload.Handle(account, selected, body);
                NativeProfileData.Method(method, "GET"); NativeProfileData.Empty(body);
                return account.NativeSave is {} save ? new(200, save.SerializeCurrent(account)) : Error(409, "No complete local native save is installed.");
            case "/2.0/elite/starsystem/key":
                NativeProfileData.Method(method, "GET"); NativeProfileData.Empty(body);
                if (!ulong.TryParse(NativeProfileData.Text(fields, "systemAddr"), NumberStyles.None, CultureInfo.InvariantCulture, out var address)) return Error(400, "Invalid system address.");
                if (account.NativeSave is not {} saved) return Error(409, "No local system is saved.");
                if (saved.Profile.CurrentSystem != address) return Error(404, "This system is not yet in the local world.");
                return new(200, new XElement("response", new XElement("systemAddress", address), new XElement("key", saved.Profile.CurrentSystemKey)).ToString(SaveOptions.DisableFormatting));
            case "/2.0/elite/resources/commodities":
                Read(); return NativeDataReply.Json(new { commodities = state.NativeResources.Commodities.Select(value => value.Public()).ToArray() });
            case "/2.0/elite/resources/permits":
                Read(); return NativeDataReply.Json(new { permits = state.NativeResources.Permits.ToDictionary(value=>value.Id.ToString(CultureInfo.InvariantCulture),value=>value.Public()) });
            case "/2.0/elite/user/privategroup":
                Read(); return NativeProfileData.Text(fields, "returnType") == "users" ? new(200, "<response><users /></response>") : Error(400, "Unknown local group selection.");
            case "/2.0/elite/user/arcs/balance":
                Read(); return NativeDataReply.Json(new { balance = new { total_balance = account.NativeArx.TotalBalance } });
            case "/2.0/elite/event":
                NativeProfileData.Method(method, "POST"); return new(200, "{}"); // Native queue needs completion only; telemetry is discarded locally.
            case "/2.0/elite/journal":
                NativeProfileData.Method(method,"PUT"); NativeProfileData.VerifyCommander(account,fields);
                var added=journals.Store(body);if(added)JournalCommits++;
                OfflineDiagnostics.Current?.Write("info","save.journal","journal.receipt","Journal stored with durable duplicate detection.",
                    new Dictionary<string,string?>{["newReceipt"]=added.ToString(),["requestBytes"]=body.Length.ToString(),["journalCommits"]=JournalCommits.ToString()});
                return new(200,"{}");
            case "/2.0/elite/user/logout":
                NativeProfileData.Method(method, "PUT"); NativeProfileData.Empty(body); loggedIn = selected = false; return new(200, "{}");
        }
        if (NativeStartupResources.Read(path) is {} resource) { Read(); return resource; }
        OfflineDiagnostics.Current?.Write("warning","data.authority","operation.unimplemented","No local handler exists for this native operation.",
            new Dictionary<string,string?>{["method"]=method,["path"]=path,["httpStatus"]="501"});
        return Error(501, "Local native operation has not been implemented: " + path);
        void Read() { NativeProfileData.Method(method, "GET"); NativeProfileData.Empty(body); NativeProfileData.VerifyCommander(account, fields); }
    }
    private static NativeDataReply Error(int status, string detail) => NativeDataReply.Json(new { error = detail }, status);
    private static ulong NewRunId()
    {
        ulong value;
        do { value = BinaryPrimitives.ReadUInt64LittleEndian(RandomNumberGenerator.GetBytes(8)) & 0x0000_FFFF_FFFF_FFFF; } while (value == 0);
        return value;
    }
    public ValueTask DisposeAsync() { World.Dispose(); return store.DisposeAsync(); }
}
