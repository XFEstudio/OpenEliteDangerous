using XFEEliteDangerous.Client;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XFEEliteDangerous.Core;

public enum InventoryEdit { Credits, Cargo, Materials, Modules }
public sealed class LocalSessionException(string message) : InvalidOperationException(message);
public sealed class SaveConflictException() : InvalidOperationException(AppText.Get("NativeTutorialSelection.Text382"));

/// <summary>In-process operations; no listener, socket, URL or remote client.</summary>
public sealed partial class OfflineWorkspace : IAsyncDisposable
{
    private readonly StateStore store;
    private readonly TimeProvider clock;
    private readonly ConcurrentDictionary<string, (string Account, DateTimeOffset Expires)> sessions = new(StringComparer.Ordinal);
    public string DirectoryPath => store.DirectoryPath;
    public const bool NativeSoloReady = false;
    public OfflineWorkspace(string directory, TimeProvider? timeProvider = null)
    {
        store = new StateStore(XFEEliteDangerous.Client.LocalPaths.Directory(directory));
        clock = timeProvider ?? TimeProvider.System;
    }
    public Task<JsonNode> StatusAsync() => store.Read(state => Json(new
    {
        name = state.ServerName ?? AppText.Get("DesktopContext.Text107"), mode = "local-offline", accounts = state.Accounts.Count,
        storage = DirectoryPath, firstAccountRequired = state.Accounts.Count == 0,
        networkTransport = false, gameIntegrationReady = NativeSoloReady,
        nativeSolo = AppText.Get("OfflineWorkspace.Text383")
    }));
    public Task<JsonNode> RegisterAsync(string email, string name, string password) => store.Update(state =>
    {
        if (state.Accounts.Count > 0 && state.AllowPlayerRegistration == false) throw new UnauthorizedAccessException(AppText.Get("OfflineWorkspace.Text384"));
        // The first local account owns the workspace. There is no default password or remote administrator token.
        var role = state.Accounts.Count == 0 ? AccountRoles.Administrator : AccountRoles.Player;
        return Json(new { account = Register(state, email, name, password, role).Public() });
    });
    public async Task<JsonNode> LoginAsync(string email, string password)
    {
        email = Clean(email, 254).ToLowerInvariant();
        if (password is null || password.Length > 256) throw new LocalSessionException(AppText.Get("OfflineWorkspace.Text385"));
        var result = await store.Read(state =>
        {
            var account = state.Accounts.Values.FirstOrDefault(value => value.Email == email);
            if (account is null || account.Disabled || !Verify(account, password)) throw new LocalSessionException(AppText.Get("OfflineWorkspace.Text385"));
            return (account.Id, Profile: Json(account.Public()));
        }).ConfigureAwait(false);
        foreach (var entry in sessions.Where(entry => entry.Value.Expires <= clock.GetUtcNow())) sessions.TryRemove(entry.Key, out _);
        var token = Wire.Secret(); sessions[token] = (result.Id, clock.GetUtcNow().AddHours(12));
        return new JsonObject { ["token"] = token, ["account"] = result.Profile };
    }
    public void Logout(string? session) { if (session is not null) sessions.TryRemove(session, out _); }
    public Task<JsonNode> ProfileAsync(string? session) => store.Read(state => Json(Authenticate(state, session).Public()));
    public Task<JsonNode> AccountsAsync(string? session) => store.Read(state =>
    {
        RequireAdministrator(Authenticate(state, session)); return Json(state.Accounts.Values.Select(value => value.Public()).ToArray());
    });
    public Task<JsonNode> PermissionsAsync(string? session) => store.Read(state => Json(Authenticate(state, session).Role == AccountRoles.Administrator
        ? new Permissions { AllowPlayerCredits = true, AllowPlayerCargo = true, AllowPlayerMaterials = true, AllowPlayerShipEditing = true } : state.Permissions));
    public Task<JsonNode> UpdatePermissionsAsync(string? session, Permissions permissions) => store.Update(state =>
    {
        RequireAdministrator(Authenticate(state, session));
        state.Permissions = JsonSerializer.Deserialize<Permissions>(JsonSerializer.Serialize(permissions, Wire.Json), Wire.Json)!;
        return Json(state.Permissions);
    });
    public Task<JsonNode> SettingsAsync(string? session) => store.Read(state =>
    {
        RequireAdministrator(Authenticate(state, session));
        return Json(new { serverName = state.ServerName ?? AppText.Get("DesktopContext.Text107"), gameDirectory = state.GameDirectory ?? @"E:\Elite Dangerous", allowPlayerRegistration = state.AllowPlayerRegistration ?? true });
    });
    public Task<JsonNode> UpdateSettingsAsync(string? session, string name, string gameDirectory, bool registration) => store.Update(state =>
    {
        RequireAdministrator(Authenticate(state, session));
        state.ServerName = Clean(name, 80); state.GameDirectory = XFEEliteDangerous.Client.LocalPaths.Directory(gameDirectory); state.AllowPlayerRegistration = registration;
        return Json(new { saved = true });
    });
    public Task<JsonNode> CreateAccountAsync(string? session, string email, string name, string password, string role) => store.Update(state =>
    {
        RequireAdministrator(Authenticate(state, session)); return Json(Register(state, email, name, password, role).Public());
    });
    public async Task<JsonNode> SetRoleAsync(string? session, string targetId, string role)
    {
        var result = await store.Update(state =>
        {
            var actor = Authenticate(state, session); RequireAdministrator(actor);
            if (actor.Id == targetId) throw new InvalidOperationException(AppText.Get("OfflineWorkspace.Text386"));
            if (!AccountRoles.IsValid(role)) throw new InvalidDataException(AppText.Get("OfflineWorkspace.Text387"));
            var account = FindAccount(state, targetId);
            if (account.Role == role) return (Changed: false, Data: Json(account.Public()));
            RequireAnotherAdministrator(state, account); account.Role = role;
            return (Changed: true, Data: Json(account.Public()));
        }).ConfigureAwait(false);
        if (result.Changed) foreach (var entry in sessions.Where(entry => entry.Value.Account == targetId)) sessions.TryRemove(entry.Key, out _);
        return result.Data;
    }
    public Task<JsonNode> CommanderAsync(string? session, string? targetId = null) => store.Read(state => Json(Target(state, session, targetId).Commander.Public()));
    public Task<JsonNode> CatalogAsync(string? session, string search = "",string? targetId=null) => store.Read(state =>
    {
        var account=Target(state,session,targetId);
        var terms = (search ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return Json(state.Catalog.Where(value => (account.NativeSave is not {} native||value.Category is "cargo" or "module"
                ||native.Materials.Any(row=>row.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)==value.Id&&row.Type==value.Category))
            &&terms.All(term => (value.Id + " " + value.Name + " " + value.Category).Contains(term, StringComparison.OrdinalIgnoreCase))).Take(5000).ToArray());
    });
    public Task<JsonNode> EditAsync(string? session, string? targetId, InventoryEdit operation, JsonObject values, string version)
    {
        var candidate = (JsonObject)values.DeepClone();
        return store.Update(state =>
        {
            var actor = Authenticate(state, session); var account = Target(state, actor, targetId);
            var allowed = operation switch
            {
                InventoryEdit.Credits => state.Permissions.AllowPlayerCredits, InventoryEdit.Cargo => state.Permissions.AllowPlayerCargo,
                InventoryEdit.Materials => state.Permissions.AllowPlayerMaterials, InventoryEdit.Modules => state.Permissions.AllowPlayerShipEditing,
                _ => throw new ArgumentOutOfRangeException(nameof(operation))
            };
            if (!allowed && actor.Role != AccountRoles.Administrator) throw new UnauthorizedAccessException(AppText.Get("OfflineWorkspace.Text388"));
            CheckVersion(account.Commander, version);
            if(operation==InventoryEdit.Cargo&&account.NativeSave is {} native&&!Text(candidate,"itemId",160).StartsWith("native-cargo-",StringComparison.Ordinal))
            {
                var id=Text(candidate,"itemId",160);var quantity=(uint)Integer(candidate,"quantity",0,ushort.MaxValue);
                var definition=state.Catalog.SingleOrDefault(value=>value.Id==id&&value.Category=="cargo")??throw new InvalidDataException(AppText.Get("OfflineWorkspace.Text389"));
                var save=native.ExportCurrent(account);var ship=save.Snapshot.Vessels.Single(value=>value.Id==save.Snapshot.CurrentVesselId);
                var cargo=ship.Cargo.ToList();
                var symbol=definition.Name;
                if(new NativeReferenceLibrary(store.DirectoryPath).Read("/2.0/elite/resources/commodities") is {} commodityDocument)
                {
                    using var definitions=JsonDocument.Parse(commodityDocument.Payload);
                    var matched=definitions.RootElement.GetProperty("commodities").EnumerateArray().FirstOrDefault(value=>string.Equals(value.GetProperty("name").GetString(),definition.Id,StringComparison.OrdinalIgnoreCase));
                    if(matched.ValueKind==JsonValueKind.Undefined)throw new InvalidDataException(AppText.Get("OfflineWorkspace.Text390"));
                    symbol=matched.GetProperty("name").GetString()!;
                }
                if(quantity>0)cargo.Add(new(){Commodity=symbol,Owner=account.NativeUserId,Quantity=quantity,TotalValue=0});
                save=save with{Snapshot=save.Snapshot with{Vessels=save.Snapshot.Vessels.Select(value=>value.Id==ship.Id?ship with{Cargo=cargo}:value).ToArray()}};
                _=CommitManagedNative(account,save);return Json(account.Commander.Public());
            }
            return Mutate(state, account, operation.ToString().ToLowerInvariant(), candidate);
        });
    }
    public Task<JsonNode> ImportJournalAsync(string? session, string targetId, JsonObject commander, string version)
    {
        var candidate = (JsonObject)commander.DeepClone();
        return store.Update(state =>
        {
            RequireAdministrator(Authenticate(state, session)); var account = FindAccount(state, targetId); CheckVersion(account.Commander, version);
            return Import(state, account, new JsonObject { ["commander"] = candidate.DeepClone() });
        });
    }
    public Task<JsonNode> ExportNativeSaveAsync(string? session, string? targetId = null) => store.Read(state =>
    {
        var account = Target(state, session, targetId);
        if (account.NativeSave is null) throw new InvalidOperationException(AppText.Get("OfflineWorkspace.Text391"));
        return Json(account.NativeSave.ExportCurrent(account));
    });
    public Task<JsonNode> ImportNativeSaveAsync(string? session, string targetId, NativeCommanderSave save, string version)
    {
        var candidate = JsonSerializer.Deserialize<NativeCommanderSave>(JsonSerializer.Serialize(save, Wire.Json), Wire.Json) ?? throw new InvalidDataException(AppText.Get("OfflineWorkspace.Text392"));
        return store.Update(state =>
        {
            RequireAdministrator(Authenticate(state, session)); var account = FindAccount(state, targetId); CheckVersion(account.Commander, version);
            NativeIdentityAllocator.Ensure(state);
            var inventory = candidate.CreateInventory(account, checked(account.Commander.Revision + 1));
            candidate.ValidateCurrent(new AccountState { NativeUserId = account.NativeUserId, Commander = inventory });
            var backup = store.BackupNative(account); account.NativeSave = candidate; account.Commander = inventory;
            return Json(new { imported = true, backup, commander = inventory.Public(), gameIntegrationReady = false });
        });
    }
    private AccountState Target(DatabaseState state, string? session, string? id) => Target(state, Authenticate(state, session), id);
    private static AccountState Target(DatabaseState state, AccountState actor, string? id)
    {
        if (string.IsNullOrEmpty(id) || actor.Id == id) return actor;
        RequireAdministrator(actor); return FindAccount(state, id);
    }
    private static AccountState FindAccount(DatabaseState state, string id) => state.Accounts.GetValueOrDefault(id) ?? throw new InvalidDataException(AppText.Get("OfflineWorkspace.Text393"));
    private AccountState Authenticate(DatabaseState state, string? token)
    {
        if (token is null || !sessions.TryGetValue(token, out var session) || session.Expires <= clock.GetUtcNow()
            || !state.Accounts.TryGetValue(session.Account, out var account) || account.Disabled) throw new LocalSessionException(AppText.Get("OfflineWorkspace.Text394"));
        return account;
    }
    private static void RequireAdministrator(AccountState actor)
    {
        if (actor.Role != AccountRoles.Administrator) throw new UnauthorizedAccessException(AppText.Get("OfflineWorkspace.Text395"));
    }
    private static void RequireAnotherAdministrator(DatabaseState state, AccountState target)
    {
        if (target.Role == AccountRoles.Administrator && !target.Disabled && !state.Accounts.Values.Any(other => other.Id != target.Id && other.Role == AccountRoles.Administrator && !other.Disabled))
            throw new InvalidOperationException(AppText.Get("OfflineWorkspace.Text396"));
    }
    private static AccountState Register(DatabaseState state, string email, string name, string password, string role)
    {
        email = Clean(email, 254).ToLowerInvariant(); name = Clean(name, 64);
        if (!AccountRoles.IsValid(role) || !email.Contains('@') || email.Any(char.IsWhiteSpace) || password is null || password.Length is < 8 or > 256 || password.Any(char.IsControl))
            throw new InvalidDataException(AppText.Get("OfflineWorkspace.Text397"));
        if (state.Accounts.Values.Any(account => account.Email == email)) throw new InvalidOperationException(AppText.Get("OfflineWorkspace.Text398"));
        var salt = RandomNumberGenerator.GetBytes(16);
        var account = new AccountState { Email = email, DisplayName = name, Role = role, PasswordSalt = Convert.ToBase64String(salt),
            PasswordHash = Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(password, salt, 210000, HashAlgorithmName.SHA256, 32)) };
        account.Commander.Name = name; state.Accounts.Add(account.Id, account); NativeIdentityAllocator.Ensure(state); return account;
    }
    private static bool Verify(AccountState account, string password)
    {
        try { return CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(account.PasswordHash), Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(account.PasswordSalt), 210000, HashAlgorithmName.SHA256, 32)); }
        catch (FormatException) { return false; }
    }
    private static string Clean(string value, int max)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > max || value.Any(char.IsControl)) throw new InvalidDataException(AppText.Get("OfflineWorkspace.Text399")); return value.Trim();
    }
    private static void CheckVersion(CommanderState commander, string? version)
    {
        if (version != commander.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture)) throw new SaveConflictException();
    }
    private static bool ValidItemId(string? value) => value is { Length: > 0 and <= 160 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.' or ':' or '$' or ';');
    private static string Text(JsonObject body, string key, int max) => Clean(body[key]?.GetValue<string>() ?? "", max);
    private static string TextOrDefault(JsonObject body, string key, string fallback) => body[key] is null ? fallback : Text(body, key, 40);
    private static long Integer(JsonObject body,string key,long min,long max)
    {
        if(body[key] is JsonValue value)
        {
            long result;
            if(value.TryGetValue<long>(out var signed))result=signed;
            else if(value.TryGetValue<int>(out var integer))result=integer;
            else if(value.TryGetValue<uint>(out var unsigned))result=unsigned;
            else throw new InvalidDataException(AppText.Get("OfflineWorkspace.Text400")+key);
            if(result>=min&&result<=max)return result;
        }
        throw new InvalidDataException(AppText.Get("OfflineWorkspace.Text401")+key);
    }
    private static bool Boolean(JsonObject body, string key) => body[key] is JsonValue value && value.TryGetValue<bool>(out var result) ? result : throw new InvalidDataException(AppText.Get("OfflineWorkspace.Text402") + key);
    private static JsonNode Json(object value) => JsonSerializer.SerializeToNode(value, Wire.Json) ?? new JsonObject();
    public ValueTask DisposeAsync() { sessions.Clear(); return store.DisposeAsync(); }
}
