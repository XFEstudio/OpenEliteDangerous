using System.Text.Json;
using System.Text.Json.Serialization;
using XFEEliteDangerous.Client;

namespace XFEEliteDangerous.Core;

public sealed record LiveRefitSlot(string Slot, NativeModuleSnapshot? Module, bool IntegrityEditable = true, bool PriorityEditable = true,
    bool AmmoEditable = false, float? MaximumHealth = null, NativeModuleAmmo? AmmoCapacity = null);
public sealed record LiveRefitResult(string CommandId, bool Applied, string Code, string Detail, LiveRefitSlot? Actual, NativeModuleSnapshot? Previous = null, bool Restored = false);
public sealed record LiveRefitStatus(int Schema, string RuntimeId, string ImageSha256, int ProcessId, uint ShipId,
    ulong ShipEntityId, string Flow, DateTimeOffset ObservedUtc, LiveRefitSlot[] Slots, LiveRefitResult? Result = null, string[]? AvailableModules = null, uint? CargoCapacity = null);
public sealed record LiveRefitCommand(string Id, string RuntimeId, DateTimeOffset ExpiresUtc, uint ShipId,
    ulong ShipEntityId, string Slot, string Action, NativeModuleSnapshot? Before, NativeModuleSnapshot? After);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(LiveRefitStatus))]
[JsonSerializable(typeof(LiveRefitCommand))]
public partial class LiveRefitJson : JsonSerializerContext;

/// <summary>Bounded per-account mailbox. Commands contain data only, never native addresses or executable code.</summary>
public sealed class LiveRefitMailbox
{
    private readonly string directory;
    public LiveRefitMailbox(string workspace, string account)
    {
        if (account.Length != 32 || account.Any(c => !char.IsAsciiHexDigit(c))) throw new ArgumentException("Invalid refit account identity.");
        directory = LocalPaths.Directory(Path.Combine(workspace, "live-refit", account));
    }
    public LiveRefitStatus? ReadStatus() => Read("status.json", LiveRefitJson.Default.LiveRefitStatus);
    public LiveRefitCommand? ReadCommand() => Read("command.json", LiveRefitJson.Default.LiveRefitCommand);
    public void Publish(LiveRefitStatus status) => Write("status.json", JsonSerializer.SerializeToUtf8Bytes(status, LiveRefitJson.Default.LiveRefitStatus));
    public void Submit(LiveRefitCommand command)
    {
        if(!Guid.TryParseExact(command.Id,"N",out _))throw new InvalidDataException("Invalid refit transaction identity.");
        var bytes=JsonSerializer.SerializeToUtf8Bytes(command,LiveRefitJson.Default.LiveRefitCommand);
        Directory.CreateDirectory(LocalPaths.Directory(Path.Combine(directory,"history")));
        Write(Path.Combine("history",command.Id+".json"),bytes);
        Write("command.json",bytes);
    }
    public static bool Fresh(LiveRefitStatus? status, DateTimeOffset now) => status is { Schema: 1, Slots.Length: <= 128 }
        && Guid.TryParseExact(status.RuntimeId, "N", out _) && status.ProcessId > 0
        && status.ObservedUtc <= now.AddSeconds(2) && status.ObservedUtc >= now.AddSeconds(-5);
    public static bool Matches(NativeModuleSnapshot? left, NativeModuleSnapshot? right) =>
        left is null ? right is null : right is not null && left.Id == right.Id && left.Name == right.Name && Uid(left.Uid) == Uid(right.Uid);
    private static ulong Uid(ulong? value)=>value is null or 0 or ulong.MaxValue?0:value.Value;
    private T? Read<T>(string name, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
    {
        var path = LocalPaths.FilePath(Path.Combine(directory, name));
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length is < 2 or > 1_048_576) throw new InvalidDataException("Refit mailbox exceeds its bound.");
            return JsonSerializer.Deserialize(stream, type);
        }
        catch (FileNotFoundException) { return default; }
        catch (DirectoryNotFoundException) { return default; }
    }
    private void Write(string name, byte[] bytes)
    {
        if (bytes.Length > 1_048_576) throw new InvalidDataException("Refit mailbox exceeds its bound.");
        Directory.CreateDirectory(directory);
        var path = LocalPaths.FilePath(Path.Combine(directory, name));
        var temporary = LocalPaths.FilePath(path + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
