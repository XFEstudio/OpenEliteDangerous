using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using XFEEliteDangerous.Core;
using XFEEliteDangerous.NativeFormat;
using XFEEliteDangerous.Client;

namespace XFEEliteDangerous.OfflineRuntime;

/// <summary>
/// Completes the pinned client's data jobs synchronously from C# memory. Original CURL handles
/// remain valid for option/destructor ABI, but are never added to its transfer engine.
/// No original perform call, socket, DNS lookup, TLS or network fallback is invoked.
/// </summary>
internal static unsafe partial class OfflineCurl
{
    // Confirmed in the pinned native constructor at RVA 0x5C1608: 0x4E6F.
    // 20056 is CURLOPT_PROGRESSFUNCTION, which has a different ABI and returns
    // zero to continue. It must never receive response bytes.
    private const int HeaderFunction = 20079;
    private const int HeaderData = 10029;
    private sealed class Request
    {
        internal readonly long Id=Interlocked.Increment(ref requestSequence);
        internal string Path="",TraceId="",OperationId="";
        internal readonly Dictionary<int,nint> Options = [];
        internal string Url = "", Method = "";
        internal int Status;
    }
    private sealed class CompletionQueue
    {
        internal readonly Queue<(nint Handle,int Result)> Items = [];
        internal nint Message = (nint)NativeMemory.AllocZeroed(24);
    }
    private static readonly ConcurrentDictionary<nint,Request> requests = [];
    private static readonly ConcurrentDictionary<nint,CompletionQueue> queues = [];
    private static readonly List<NativeHook> hooks = [];
    private static delegate* unmanaged<nint> originalInit;
    private static delegate* unmanaged<nint,int,nint,int> originalOption;
    private static delegate* unmanaged<nint,int,nint,int> originalInfo;
    private static delegate* unmanaged<nint,void> originalCleanup;
    private static delegate* unmanaged<nint,int> originalMultiCleanup;
    private static INativeGameData data = null!;
    private static byte[] key = [];
    private static string log = "";
    private static readonly object logGate = new();
    private static int completed;
    private static long requestSequence;
    [ThreadStatic]internal static Dictionary<string,string?>? NativeCompletionContext;
    internal static int Completed => Volatile.Read(ref completed);
    internal static void Install(nint image, INativeGameData source, byte[] envelopeKey, string logDirectory)
    {
        data = source; key = envelopeKey.ToArray(); log = Path.Combine(logDirectory, "native-local-data.tsv");
        var init = Hook(0x48D5720, (nint)(delegate* unmanaged<nint>)&Init);
        originalInit = (delegate* unmanaged<nint>)init.Trampoline;
        var option = Hook(0x48D5770, (nint)(delegate* unmanaged<nint,int,nint,int>)&Option);
        originalOption = (delegate* unmanaged<nint,int,nint,int>)option.Trampoline;
        var info = Hook(0x48D56F0, (nint)(delegate* unmanaged<nint,int,nint,int>)&Info);
        originalInfo = (delegate* unmanaged<nint,int,nint,int>)info.Trampoline;
        var cleanupEasy = Hook(0x48D56E0, (nint)(delegate* unmanaged<nint,void>)&CleanupEasy);
        originalCleanup = (delegate* unmanaged<nint,void>)cleanupEasy.Trampoline;
        Hook(0x48D6A50, (nint)(delegate* unmanaged<nint,nint,int>)&Add);
        Hook(0x48D6EA0, (nint)(delegate* unmanaged<nint,int*,int>)&Perform);
        Hook(0x48D6E10, (nint)(delegate* unmanaged<nint,int*,nint>)&Next);
        Hook(0x48D6FF0, (nint)(delegate* unmanaged<nint,nint,int>)&Remove);
        var cleanup = Hook(0x48D6BA0, (nint)(delegate* unmanaged<nint,int>)&CleanupMulti);
        originalMultiCleanup = (delegate* unmanaged<nint,int>)cleanup.Trampoline;
        foreach (var hook in hooks) hook.Enable();
        NativeHook Hook(int rva, nint callback) { var result = new NativeHook(image + rva, callback); hooks.Add(result); return result; }
    }
    [UnmanagedCallersOnly] private static nint Init()
    {
        var handle = originalInit();
        if (handle != 0) requests[handle] = new();
        else OfflineDiagnostics.Current?.Write("error","native.data","curl.init.failed","Native CURL initialization returned null.",new Dictionary<string,string?>{["nativeEntryRva"]="0x48D5720"});
        return handle;
    }
    [UnmanagedCallersOnly] private static int Option(nint handle, int option, nint value)
    {
        try
        {
            if (!requests.TryGetValue(handle, out var request)) return 43;
            var result = originalOption(handle, option, value);
            if (result == 0) lock (request)
            {
                request.Options[option] = value;
                // CURL duplicates these strings in setopt. The native caller releases its
                // temporary FString before multi_add; keeping that pointer is a use-after-free.
                if (option == 10002) request.Url = Text(value, 8 * 1024 * 1024);
                if (option == 10036) request.Method = Text(value, 16);
            }
            return result;
        }
        catch(Exception error) { OfflineDiagnostics.Current?.Write("error","native.data","curl.option.failed","Native CURL option handling failed.",
            new Dictionary<string,string?>{["option"]=option.ToString(),["nativeEntryRva"]="0x48D5770"},error);return 43; }
    }
    [UnmanagedCallersOnly] private static int Info(nint handle, int info, nint output)
    {
        if (info == 0x200002 && output != 0 && requests.TryGetValue(handle, out var request)) { *(int*)output = request.Status; return 0; }
        return originalInfo(handle, info, output);
    }
    [UnmanagedCallersOnly] private static void CleanupEasy(nint handle)
    { requests.TryRemove(handle, out _); originalCleanup(handle); }
    [UnmanagedCallersOnly] private static int Add(nint multi, nint handle)
    {
        var result = 0;
        using var operation=OfflineDiagnostics.Current?.Begin("native.data","curl.multi.add",
            new Dictionary<string,string?>{["requestId"]=requests.TryGetValue(handle,out var known)?"request-"+known.Id:"unknown",["nativeEntryRva"]="0x48D6A50"});
        try
        {
            if (!requests.TryGetValue(handle, out var request)) return 2;
            Dictionary<int,nint> options; string url, customMethod;
            lock (request) { options = new(request.Options); url = request.Url; customMethod = request.Method; }
            var separator = url.IndexOf('?');
            var uri = new Uri(separator < 0 ? url : url[..separator]); // Parsing only; never opens a connection.
            var path = uri.AbsolutePath;
            request.Path=path;request.TraceId=OfflineDiagnostics.Current?.CurrentTraceId??"";
            request.OperationId=OfflineDiagnostics.Current?.CurrentOperationId??"";
            OfflineDiagnostics.Current?.Write("debug","native.data","request.received","Native data job entered the local adapter.",
                new Dictionary<string,string?>{["path"]=path,["requestId"]="request-"+request.Id});
            NativeDataReply reply; string method;
            var plainTime = path == "/2.0/server/time";
            if (plainTime)
            {
                // Native9A28F0 reads this first job directly from raw CURL JSON,
                // before login and without the encrypted response wrapper.
                method = customMethod.Length > 0 ? customMethod : "GET";
                reply = data.InvokeAsync(method, path, new Dictionary<string,string>(), []).GetAwaiter().GetResult();
            }
            else
            {
            if (!path.StartsWith("/2.0/elite/", StringComparison.Ordinal))
            { Record("UNSUPPORTED\t" + path); throw new InvalidDataException("Unknown native data namespace."); }
            var headers = Headers(options.GetValueOrDefault(10023));
            if (!headers.TryGetValue("Nonce", out var nonce) || headers.GetValueOrDefault("Encrypted") != "1") throw new InvalidDataException("Unknown native request format.");
            var bodySize = checked((int)options.GetValueOrDefault(60));
            if (bodySize < 0 || bodySize > 8 * 1024 * 1024) throw new InvalidDataException("Native data body is too large.");
            var bodyText = options.TryGetValue(10015, out var bodyPointer) && bodyPointer != 0
                ? Encoding.ASCII.GetString(new ReadOnlySpan<byte>((void*)bodyPointer, bodySize)) : "";
            // Original PUT uses CURLOPT_UPLOAD and a read callback, rather than CURLOPT_POSTFIELDS.
            if (options.GetValueOrDefault(46) != 0)
            {
                var size = checked((int)options.GetValueOrDefault(14));
                if (size < 0 || size > 8 * 1024 * 1024) throw new InvalidDataException("Native upload is too large.");
                var callback = (delegate* unmanaged<byte*,nuint,nuint,nint,nuint>)options.GetValueOrDefault(20012);
                if (size > 0 && callback == null) throw new InvalidDataException("Native upload reader is missing.");
                var buffer = new byte[size]; var cursor = 0;
                fixed (byte* destination = buffer) while (cursor < size)
                {
                    var read = callback(destination + cursor, 1, (nuint)(size - cursor), options.GetValueOrDefault(10009));
                    if (read == 0 || read > (nuint)(size - cursor)) throw new InvalidDataException("Native upload reader did not fill its declared body.");
                    cursor += checked((int)read);
                }
                bodyText = Encoding.ASCII.GetString(buffer); CryptographicOperations.ZeroMemory(buffer);
            }
            var encodedQuery = separator < 0 ? "" : Uri.UnescapeDataString(url[(separator + 1)..]);
            var decoded = EliteRequestEnvelopeCodec.Decode(key, new(nonce, encodedQuery, bodyText));
            OfflineDiagnostics.Current?.RegisterSecret(nonce);
            var queryFields=Fields(decoded.Query);
            foreach(var field in queryFields)if(field.Key.Contains("token",StringComparison.OrdinalIgnoreCase)||field.Key.Contains("password",StringComparison.OrdinalIgnoreCase))
                OfflineDiagnostics.Current?.RegisterSecret(field.Value);
            method = customMethod.Length > 0 ? customMethod
                : options.GetValueOrDefault(46) != 0 ? "PUT" : options.GetValueOrDefault(47) != 0 ? "POST" : "GET";
            try { reply = data.InvokeAsync(method, path, queryFields, decoded.Body).GetAwaiter().GetResult(); }
            finally { CryptographicOperations.ZeroMemory(decoded.Query); CryptographicOperations.ZeroMemory(decoded.Body); }
            }
            request.Status = reply.Status;
            var payload = reply.Status == 204 ? [] : Encoding.UTF8.GetBytes(reply.Payload);
            var responseNonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
            var encoded = !plainTime && payload.Length > 0 ? EliteResponseEnvelopeCodec.Encode(key, responseNonce, payload) : null;
            var responseBody = plainTime ? payload : encoded is null ? [] : Encoding.ASCII.GetBytes(encoded.BodyBase64);
            Deliver(options.GetValueOrDefault(HeaderFunction), options.GetValueOrDefault(HeaderData), Encoding.ASCII.GetBytes("HTTP/1.1 " + reply.Status + " Local\r\n"));
            if (encoded is not null)
            {
                Header("Nonce", encoded.Nonce); Header("uncompressedSize", encoded.UncompressedSize);
            }
            Header("Content-Length", responseBody.Length.ToString(CultureInfo.InvariantCulture));
            Header("Content-Type", reply.Payload.StartsWith('<') ? "application/xml" : "application/json");
            Deliver(options.GetValueOrDefault(HeaderFunction), options.GetValueOrDefault(HeaderData), "\r\n"u8.ToArray());
            if (responseBody.Length > 0) Deliver(options.GetValueOrDefault(20011), options.GetValueOrDefault(10001), responseBody);
            Record(method + "\t" + path + "\t" + reply.Status.ToString(CultureInfo.InvariantCulture));
            operation?.Complete(reply.Status>=400?"rejected":"completed");
            OfflineDiagnostics.Current?.Write(reply.Status>=400?"warning":"debug","native.data","completion.queued","Local data completion queued for the original native consumer.",
                new Dictionary<string,string?>{["requestId"]="request-"+request.Id,["method"]=method,["path"]=path,
                    ["httpStatus"]=reply.Status.ToString(),["curlResult"]="0",["responseBytes"]=responseBody.Length.ToString()});
            Interlocked.Increment(ref completed);
            void Header(string name, string value) => Deliver(options.GetValueOrDefault(HeaderFunction), options.GetValueOrDefault(HeaderData), Encoding.ASCII.GetBytes(name + ": " + value + "\r\n"));
        }
        catch (Exception error) { result = 23;operation?.Complete("failed");OfflineDiagnostics.Current?.Write("error","native.data","curl.local.failed",
            "Native data callback or local data execution failed.",new Dictionary<string,string?>{["curlResult"]="23",["nativeEntryRva"]="0x48D6A50"},error);
            Record("FAILED\t" + error.GetType().Name + "\t" + error.Message.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ')); }
        try { var queue = queues.GetOrAdd(multi, _ => new()); lock (queue) queue.Items.Enqueue((handle, result)); return 0; }
        catch(Exception error) { OfflineDiagnostics.Current?.Write("error","native.data","completion.queue.failed","Unable to queue the native data completion.",exception:error);return 3; }
    }
    [UnmanagedCallersOnly] private static int Perform(nint multi, int* running) { if (running != null) *running = 0; return 0; }
    [UnmanagedCallersOnly] private static nint Next(nint multi, int* remaining)
    {
        NativeCompletionContext=null;
        if (remaining != null) *remaining = 0;
        if (!queues.TryGetValue(multi, out var queue)) return 0;
        lock (queue)
        {
            if (!queue.Items.TryDequeue(out var item)) return 0;
            if(requests.TryGetValue(item.Handle,out var request))
            {
                NativeCompletionContext=new Dictionary<string,string?>{["httpRequestId"]="request-"+request.Id,
                    ["httpPath"]=request.Path,["httpTraceId"]=request.TraceId,["httpOperationId"]=request.OperationId,["httpStatus"]=request.Status.ToString()};
                OfflineDiagnostics.Current?.Write("debug","native.data","completion.delivered","Original native consumer received the data completion.",
                    NativeCompletionContext);
            }
            *(int*)queue.Message = 1; *(nint*)(queue.Message + 8) = item.Handle; *(int*)(queue.Message + 16) = item.Result;
            if (remaining != null) *remaining = queue.Items.Count;
            return queue.Message;
        }
    }
    [UnmanagedCallersOnly] private static int Remove(nint multi, nint handle) => 0;
    [UnmanagedCallersOnly] private static int CleanupMulti(nint multi)
    {
        if (queues.TryRemove(multi, out var queue)) NativeMemory.Free((void*)queue.Message);
        return originalMultiCleanup(multi);
    }
    private static string Text(nint value, int maximum)
    {
        if (value == 0) return "";
        var length = 0; while (length < maximum && ((byte*)value)[length] != 0) length++;
        if (length == maximum) throw new InvalidDataException("Native text exceeds its audited bound.");
        return Encoding.UTF8.GetString(new ReadOnlySpan<byte>((void*)value, length));
    }
    private static Dictionary<string,string> Headers(nint list)
    {
        var result = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase); var count = 0;
        for (var current = list; current != 0; current = *(nint*)(current + 8))
        {
            if (++count > 128) throw new InvalidDataException("Native header list exceeds its bound.");
            var value = Text(*(nint*)current, 16384); var split = value.IndexOf(':');
            if (split > 0) result[value[..split].Trim()] = value[(split + 1)..].Trim();
        }
        return result;
    }
    private static Dictionary<string,string> Fields(byte[] query)
    {
        var result = new Dictionary<string,string>(StringComparer.Ordinal);
        foreach (var pair in Encoding.UTF8.GetString(query).Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = pair.IndexOf('='); if (split < 1) throw new InvalidDataException("Invalid native data field.");
            if (!result.TryAdd(Uri.UnescapeDataString(pair[..split]), Uri.UnescapeDataString(pair[(split + 1)..]))) throw new InvalidDataException("Duplicate native data field.");
        }
        return result;
    }
    private static void Deliver(nint function, nint context, byte[] bytes)
    {
        if (function == 0) throw new InvalidDataException("Native completion callback is missing.");
        fixed (byte* pointer = bytes)
            if (((delegate* unmanaged<byte*,nuint,nuint,nint,nuint>)function)(pointer, 1, (nuint)bytes.Length, context) != (nuint)bytes.Length)
                throw new IOException("Native data callback rejected the completion.");
    }
    private static void Record(string value)
    {
        try { lock (logGate) if (!File.Exists(log) || new FileInfo(log).Length < 8 * 1024 * 1024) File.AppendAllText(log, value + Environment.NewLine); } catch { }
    }
}
