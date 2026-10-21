using System.Text.Json;

namespace XFEEliteDangerous.Core;

public sealed class StateStore : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string path;
    private readonly string lockPath;
    private int disposed;
    public string DirectoryPath { get; }
    public StateStore(string directory)
    {
        DirectoryPath = XFEEliteDangerous.Client.LocalPaths.Directory(directory);
        Directory.CreateDirectory(DirectoryPath);
        path = Path.Combine(DirectoryPath, "state.json");
        lockPath = XFEEliteDangerous.Client.LocalPaths.FilePath(Path.Combine(DirectoryPath, "workspace.lock"));
        using var lease = AcquireFileLeaseAsync().GetAwaiter().GetResult();
        _ = Load();
    }
    private DatabaseState Load()
    {
            var state = File.Exists(path) ? JsonSerializer.Deserialize<DatabaseState>(File.ReadAllText(XFEEliteDangerous.Client.LocalPaths.FilePath(path)), Wire.Json) ?? throw new InvalidDataException("Empty saved state.") : new();
            if (state.Schema != 1) throw new InvalidDataException("Unsupported state schema.");
            // Missing role on older schema-1 records retains AccountState's player initializer.
            if (state.Accounts.Values.Any(account => !AccountRoles.IsValid(account.Role)))
                throw new InvalidDataException("Invalid saved account role.");
            if (state.Accounts.Values.Where(account => account.NativeUserId != 0)
                .GroupBy(account => account.NativeUserId).Any(group => group.Count() > 1))
                throw new InvalidDataException("Duplicate saved private native user identity.");
            if (state.Accounts.Values.Any(account => account.NativeTutorial is null
                || account.NativeTutorial.Flight is null || account.NativeTutorial.OnFoot is null))
                throw new InvalidDataException("Invalid saved native tutorial state.");
            if(state.Accounts.Values.Any(account=>account.NativeArx is null||account.NativeArx.TotalBalance<0))
                throw new InvalidDataException("Invalid saved private ARX ledger.");
            if(state.Accounts.Values.Any(account=>account.NativePreferences is null||account.NativePreferences.PrivacyMode is not("PRIVATE" or "SQUADRON")))
                throw new InvalidDataException("Invalid saved native commander preferences.");
            if(state.Accounts.Values.Any(account=>!NativeBookmarkData.Valid(account.NativeBookmarks)))
                throw new InvalidDataException("Invalid saved native bookmarks.");
            try
            {
                if(state.NativeResources is null||state.NativeResourceRevision<0)throw new ArgumentException("Invalid native resource state.");
                state.NativeResources.Validate();
            }
            catch(ArgumentException error){throw new InvalidDataException("Invalid saved native resource configuration.",error);}
            foreach (var account in state.Accounts.Values.Where(account => account.NativeSave is not null))
                try { account.NativeSave!.ValidateCurrent(account); }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException or OverflowException)
                { throw new InvalidDataException("Invalid saved native commander state.", error); }
            return state;
    }
    public async Task<T> Read<T>(Func<DatabaseState, T> operation)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            using var lease = await AcquireFileLeaseAsync().ConfigureAwait(false);
            return operation(Load());
        }
        finally { gate.Release(); }
    }
    public async Task<T> Update<T>(Func<DatabaseState, T> operation)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            using var lease = await AcquireFileLeaseAsync().ConfigureAwait(false);
            var next = Load();
            var previous = JsonSerializer.Serialize(next, Wire.Json);
            var result = operation(next);
            var encoded = JsonSerializer.SerializeToUtf8Bytes(next, Wire.Json);
            if (System.Text.Encoding.UTF8.GetString(encoded) == previous) return result;
            var temporary = path + "." + Wire.Id() + ".tmp";
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(encoded); file.Flush(flushToDisk: true); }
                try
                {
                    if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
                    else File.Move(temporary, path);
                }
                catch(Exception error)when(error is IOException or UnauthorizedAccessException)
                {
                    XFEEliteDangerous.Client.OfflineDiagnostics.Current?.Write("error","save.store","save.atomic.failed",
                        "The flushed state candidate could not atomically replace the previous file.",new Dictionary<string,string?>
                        {["targetPath"]=path,["candidatePath"]=temporary,["backupPath"]=path+".bak",
                            ["targetExists"]=File.Exists(path).ToString()},error);
                    throw;
                }
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return result;
        }
        finally { gate.Release(); }
    }
    public string Backup(CommanderState commander)
    {
        var directory = Path.Combine(DirectoryPath, "inventory-backups");
        Directory.CreateDirectory(directory);
        var name = $"{commander.Id}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Wire.Id()}.json";
        using var file = new FileStream(Path.Combine(directory, name), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(file, commander.Public(), Wire.Json); file.Flush(true);
        return name;
    }
    public string BackupNative(AccountState account)
    {
        var directory = Path.Combine(DirectoryPath, "inventory-backups");
        Directory.CreateDirectory(directory);
        var name = $"{account.Commander.Id}-native-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Wire.Id()}.json";
        using var file = new FileStream(Path.Combine(directory, name), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(file, new { commander = account.Commander.Public(), nativeSave = account.NativeSave?.ExportCurrent(account),
            nativeTutorial = NativeTutorialSelection.FromProfile(account.NativeTutorial) }, Wire.Json);
        file.Flush(true);
        return name;
    }
    private async Task<FileStream> AcquireFileLeaseAsync()
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        while (true)
        {
            try { return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException error) when ((error.HResult & 0xFFFF) is 32 or 33 && System.Diagnostics.Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(10))
            { await Task.Delay(15).ConfigureAwait(false); }
        }
    }
    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref disposed, 1);
        await gate.WaitAsync().ConfigureAwait(false);
        // Queued calls acquire the gate and observe disposed; do not destroy it underneath them.
        gate.Release();
    }
}
