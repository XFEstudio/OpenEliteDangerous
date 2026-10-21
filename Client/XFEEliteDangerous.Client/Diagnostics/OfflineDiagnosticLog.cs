using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace XFEEliteDangerous.Client;

/// <summary>Local JSONL diagnostics. Never serializes request bodies, credentials or arbitrary objects.</summary>
public sealed class OfflineDiagnosticLog : IDisposable
{
    internal sealed record Context(string Id, string Trace, Context? Parent);
    private readonly object gate = new();
    private readonly AsyncLocal<Context?> current = new();
    private readonly HashSet<string> secrets = new(StringComparer.Ordinal);
    private readonly string directory, name, runId;
    private readonly long segmentBytes;
    private FileStream? stream;
    private int segment;
    private long sequence, operationSequence, failures;
    private bool disposed;
    public long WriteFailures => Interlocked.Read(ref failures);
    public string LastWriteFailure { get; private set; } = "";
    public string? CurrentTraceId=>current.Value?.Trace;
    public string? CurrentOperationId=>current.Value?.Id;
    public string FirstFile => Path.Combine(directory, name + ".0000.jsonl");

    public OfflineDiagnosticLog(string directory, string name, string runId, long segmentBytes = 16 * 1024 * 1024)
    {
        if (name.Length is 0 or >64 || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            throw new ArgumentException("Invalid diagnostic file name.", nameof(name));
        if (segmentBytes < 1024) throw new ArgumentOutOfRangeException(nameof(segmentBytes));
        this.directory = Path.GetFullPath(directory); this.name = name; this.runId = runId; this.segmentBytes = segmentBytes;
        Directory.CreateDirectory(this.directory);
        OpenSegment();
    }
    public void RegisterSecret(string value)
    {
        if (value.Length is <4 or >8192) return;
        lock (gate) if (secrets.Count < 256) secrets.Add(value);
    }
    public Operation Begin(string component, string operation, IReadOnlyDictionary<string,string?>? fields = null,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0, [CallerMemberName] string member = "")
    {
        var context = new Context("op-" + Interlocked.Increment(ref operationSequence).ToString("D8"), current.Value?.Trace ?? "", current.Value);
        if (context.Trace.Length == 0) context = context with { Trace = context.Id };
        current.Value = context;
        Write("debug", component, "operation.begin", operation, fields, file: file, line: line, member: member);
        return new(this, context, component, operation, file, line, member);
    }
    public void Write(string level, string component, string eventName, string message = "",
        IReadOnlyDictionary<string,string?>? fields = null, Exception? exception = null,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0, [CallerMemberName] string member = "")
    {
        try
        {
            lock (gate)
            {
                if (disposed) return;
                using var bytes = new MemoryStream();
                using (var json = new Utf8JsonWriter(bytes))
                {
                    json.WriteStartObject();
                    json.WriteNumber("sequence", ++sequence);
                    json.WriteString("utc", DateTimeOffset.UtcNow);
                    json.WriteNumber("monotonicTicks", Stopwatch.GetTimestamp());
                    json.WriteNumber("timestampFrequency", Stopwatch.Frequency);
                    json.WriteString("runId", runId); json.WriteNumber("pid", Environment.ProcessId);
                    json.WriteNumber("managedThreadId", Environment.CurrentManagedThreadId);
                    json.WriteString("level", level); json.WriteString("component", component); json.WriteString("event", eventName);
                    if (current.Value is {} context)
                    {
                        json.WriteString("traceId", context.Trace); json.WriteString("operationId", context.Id);
                        if (context.Parent is {} parent) json.WriteString("parentOperationId", parent.Id);
                    }
                    json.WriteString("message", Clean(message, 16384));
                    json.WriteStartObject("source"); json.WriteString("file", file); json.WriteNumber("line", line);
                    json.WriteString("member", member); json.WriteEndObject();
                    if (fields is not null)
                    {
                        json.WriteStartObject("fields");
                        foreach (var field in fields)
                            json.WriteString(field.Key, Sensitive(field.Key) ? "[redacted]" : Clean(field.Value ?? "", 4096));
                        json.WriteEndObject();
                    }
                    if (exception is not null)
                    {
                        json.WriteStartObject("exception");
                        json.WriteString("type", exception.GetType().FullName);
                        json.WriteNumber("hresult", exception.HResult);
                        json.WriteString("message", Clean(exception.Message, 16384));
                        // ToString retains nested exceptions and their managed stacks.
                        json.WriteString("detail", Clean(exception.ToString(), 131072));
                        json.WriteEndObject();
                    }
                    if (failures > 0) json.WriteNumber("priorWriteFailures", failures);
                    json.WriteEndObject();
                }
                if (stream!.Length > 0 && stream.Length + bytes.Length + 1 > segmentBytes)
                { stream.Dispose(); segment++; OpenSegment(); }
                stream.Write(bytes.GetBuffer(), 0, checked((int)bytes.Length)); stream.WriteByte((byte)'\n');
                stream.Flush();
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ObjectDisposedException)
        { Interlocked.Increment(ref failures); LastWriteFailure = error.GetType().Name + ": " + error.Message; }
    }
    private void OpenSegment()
    {
        stream = new FileStream(Path.Combine(directory, name + "." + segment.ToString("D4") + ".jsonl"),
            FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite, 4096, FileOptions.WriteThrough);
    }
    private static bool Sensitive(string key) => new[] { "password", "token", "secret", "authorization", "cookie", "nonce", "encryptionkey", "ticket" }
        .Any(value => key.Contains(value, StringComparison.OrdinalIgnoreCase));
    private string Clean(string value, int maximum)
    {
        foreach (var secret in secrets) value = value.Replace(secret, "[redacted]", StringComparison.Ordinal);
        // Native logs can include keys/command-line tickets that never reached a managed request.
        var result = new StringBuilder(Math.Min(value.Length, maximum));
        for (var index = 0; index < value.Length && result.Length < maximum;)
        {
            var end = index; while (end < value.Length && char.IsAsciiHexDigit(value[end])) end++;
            if (end - index >= 32) { result.Append("[redacted-hex]"); index = end; }
            else
            {
                end=index;while(end<value.Length&&(char.IsAsciiLetterOrDigit(value[end])||value[end] is '+' or '/' or '='))end++;
                if(end-index>=64){result.Append("[redacted-encoded]");index=end;}
                else{result.Append(value[index]);index++;}
            }
        }
        if (value.Length > maximum) result.Append(" [truncated]");
        return result.ToString();
    }
    public void Dispose() { lock (gate) { if (disposed) return; disposed = true; stream?.Dispose(); stream = null; secrets.Clear(); } }

    public sealed class Operation : IDisposable
    {
        private readonly OfflineDiagnosticLog owner;
        private readonly Context context;
        private readonly string component, operation, file, member;
        private readonly int line;
        private readonly long start = Stopwatch.GetTimestamp();
        private string outcome = "completed";
        private bool disposed;
        internal Operation(OfflineDiagnosticLog owner, Context context, string component, string operation, string file, int line, string member)
        { this.owner=owner;this.context=context;this.component=component;this.operation=operation;this.file=file;this.line=line;this.member=member; }
        public void Complete(string result) => outcome = result;
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            owner.Write(outcome is "failed" or "rejected" ? "warning" : "debug", component, "operation.end", operation,
                new Dictionary<string,string?> { ["outcome"]=outcome, ["durationMs"]=Stopwatch.GetElapsedTime(start).TotalMilliseconds.ToString("F3",System.Globalization.CultureInfo.InvariantCulture) },
                file:file,line:line,member:member);
            if (ReferenceEquals(owner.current.Value,context)) owner.current.Value=context.Parent;
        }
    }
}

public static class OfflineDiagnostics
{
    public static OfflineDiagnosticLog? Current { get; set; }
}
