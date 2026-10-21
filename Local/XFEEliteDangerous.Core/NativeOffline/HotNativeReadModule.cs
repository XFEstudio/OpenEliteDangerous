using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using XFEEliteDangerous.Client;

namespace XFEEliteDangerous.Core;

/// <summary>A versioned C ABI for replaceable business code. The stable host owns all sessions and commits.</summary>
internal sealed unsafe partial class HotNativeReadModule
{
    private const string GameHash="e6be8bbe04e6a7ae226d4318945af7f367de13dc5a007a261964d9ba8144e988";
    private readonly string root;
    private readonly Dictionary<string,Module> mapped=new(StringComparer.OrdinalIgnoreCase);
    private Module? current;
    private string acceptedManifest="",lastFailure="";
    private readonly object gate=new();
    private sealed class Module(nint handle,FileStream lease,string hash,string generation,HashSet<string> operations,HashSet<byte> rpcMethods,bool saveAbi)
    {
        // NativeAOT runtime threads can outlive a call. Keep every loaded generation mapped until process exit.
        internal readonly nint Handle=handle;
        internal readonly FileStream Lease=lease;
        internal readonly string Hash=hash,Generation=generation;
        internal readonly HashSet<string> Operations=operations;
        internal readonly nint Invoke=NativeLibrary.GetExport(handle,"XfeReadModuleInvoke");
        internal readonly nint Free=NativeLibrary.GetExport(handle,"XfeReadModuleFree");
        internal readonly HashSet<byte> RpcMethods=rpcMethods;
        internal readonly nint RpcInvoke=rpcMethods.Count>0?NativeLibrary.GetExport(handle,"XfeRpcModuleInvoke"):0;
        internal readonly bool SaveAbi=saveAbi;
        internal readonly nint SaveInvoke=saveAbi?NativeLibrary.GetExport(handle,"XfeSaveModuleInvoke"):0;
    }
    internal NativeRpcModuleReply? InvokeRpc(NativeRpcModuleRequest request)
    {
        lock(gate)
        {
            Refresh(false);var module=current;
            if(module is null||module.RpcInvoke==0||request.Payload.Length==0||!module.RpcMethods.Contains(request.Payload[0]))return null;
            var bytes=JsonSerializer.SerializeToUtf8Bytes(request,NativeReadModuleJson.Default.NativeRpcModuleRequest);
            byte* output=null;var length=0;
            try
            {
                if(bytes.Length>65536)throw new InvalidDataException("RPC module input exceeds its bound.");
                int result;fixed(byte* input=bytes)result=((delegate* unmanaged<byte*,int,byte**,int*,int>)module.RpcInvoke)(input,bytes.Length,&output,&length);
                if(result==1&&output==null&&length==0)return null;
                if(output==null||length is <=0 or >65536)throw new InvalidDataException("RPC module returned an invalid native buffer.");
                var reply=JsonSerializer.Deserialize(new ReadOnlySpan<byte>(output,length),NativeReadModuleJson.Default.NativeRpcModuleReply)
                    ??throw new InvalidDataException("RPC module returned no reply.");
                if(result!=0||reply.Protocol!=1||reply.Emissions is null||reply.ErrorType is not null)
                {
                    OfflineDiagnostics.Current?.Write("error","business.hotload","rpc.module.failed","Replaceable C# RPC handler failed.",
                        new Dictionary<string,string?>{["generation"]=module.Generation,["exceptionType"]=reply.ErrorType,["error"]=reply.Error,["stackTrace"]=reply.StackTrace});
                    throw new InvalidDataException("The active RPC handler failed.");
                }
                if(!reply.Handled&&reply.Emissions.Length!=0||reply.Emissions.Length>8)
                    throw new InvalidDataException("Invalid RPC module emission count.");
                OfflineDiagnostics.Current?.Write("debug","business.hotload","rpc.module.reply","Replaceable C# RPC handler returned to the fixed transactional host.",
                    new Dictionary<string,string?>{["generation"]=module.Generation,["rpcMethod"]=request.Payload[0].ToString(),
                        ["handled"]=reply.Handled.ToString(),["emissions"]=reply.Emissions.Length.ToString()});
                return reply.Handled?reply:null;
            }
            catch(Exception error)when(error is InvalidDataException or JsonException or ArgumentException)
            {
                OfflineDiagnostics.Current?.Write("error","business.hotload","rpc.module.contract.failed","Local RPC module contract rejected; the reliable transaction is not accepted.",
                    new Dictionary<string,string?>{["generation"]=module.Generation},error);throw;
            }
            finally
            {CryptographicOperations.ZeroMemory(bytes);if(output!=null)((delegate* unmanaged<byte*,void>)module.Free)(output);}
        }
    }
    internal HotNativeReadModule(string workspace)
    {
        root=Path.Combine(Path.GetFullPath(workspace),"native-business");
        if(File.Exists(Path.Combine(root,"active.json")))Refresh(true);
    }
    internal NativeDataReply? Invoke(AccountState account,string method,string operation,IReadOnlyDictionary<string,string> fields,byte[] body)
    {
        if(method!="GET"||!Allowed(operation))return null;
        lock(gate)
        {
            Refresh(false);
            var module=current;
            if(module is null||!module.Operations.Contains(operation))return null;
            NativeProfileData.VerifyCommander(account,fields);NativeProfileData.Empty(body);
            var save=account.NativeSave??throw new NativeDataException(409,"A complete local commander is required.");
            // Authentication and machine identifiers were already checked by the stable host. They are not business inputs.
            var query=fields.Where(pair=>!IsIdentity(pair.Key)).ToDictionary(pair=>pair.Key,pair=>pair.Value,StringComparer.Ordinal);
            var request=JsonSerializer.SerializeToUtf8Bytes(new NativeReadModuleRequest(1,operation,query,
                save.Profile.CurrentSystem,save.Profile.LoanCeiling),NativeReadModuleJson.Default.NativeReadModuleRequest);
            if(request.Length>65536)throw new NativeDataException(413,"Read module request exceeds its bound.");
            byte* output=null;var length=0;
            try
            {
                int result;
                fixed(byte* input=request)result=((delegate* unmanaged<byte*,int,byte**,int*,int>)module.Invoke)(input,request.Length,&output,&length);
                if(result==1&&output==null&&length==0)return null;
                if(output==null||length is <=0 or >8*1024*1024)throw new InvalidDataException("Read module returned an invalid native buffer.");
                var reply=JsonSerializer.Deserialize(new ReadOnlySpan<byte>(output,length),NativeReadModuleJson.Default.NativeReadModuleReply)
                    ??throw new InvalidDataException("Read module returned no reply.");
                if(result!=0||reply.Protocol!=1||reply.Status is <200 or >599||reply.Payload is null||reply.ErrorType is not null)
                {
                    OfflineDiagnostics.Current?.Write("error","business.hotload","module.invoke.failed","Replaceable C# read handler failed.",
                        new Dictionary<string,string?>{["generation"]=module.Generation,["path"]=operation,
                            ["exceptionType"]=reply.ErrorType,["error"]=reply.Error,["stackTrace"]=reply.StackTrace});
                    throw new NativeDataException(500,"The active local read handler failed; inspect business.hotload diagnostics.");
                }
                OfflineDiagnostics.Current?.Write("debug","business.hotload","module.reply","Replaceable C# read handler completed.",
                    new Dictionary<string,string?>{["generation"]=module.Generation,["path"]=operation,["httpStatus"]=reply.Status.ToString()});
                return new(reply.Status,reply.Payload);
            }
            catch(NativeDataException){throw;}
            catch(Exception error)when(error is InvalidDataException or JsonException or ArgumentException)
            {
                OfflineDiagnostics.Current?.Write("error","business.hotload","module.contract.failed","The active read module violated its native reply contract.",
                    new Dictionary<string,string?>{["generation"]=module.Generation,["path"]=operation},error);
                throw new NativeDataException(500,"The local read module contract failed; inspect business.hotload diagnostics.");
            }
            finally {if(output!=null)((delegate* unmanaged<byte*,void>)module.Free)(output);}
        }
    }
    private void Refresh(bool startup)
    {
        var manifest=Path.Combine(root,"active.json");
        if(!File.Exists(manifest))return;
        string candidate="unreadable";
        try
        {
            NoLink(root);var bytes=Read(manifest,65536);
            candidate=Convert.ToHexStringLower(SHA256.HashData(bytes));
            if(candidate==acceptedManifest)return;
            using var parsed=JsonDocument.Parse(bytes);var metadata=parsed.RootElement;
            if(metadata.GetProperty("schema").GetInt32()!=1||metadata.GetProperty("abi").GetInt32()!=1
                ||metadata.GetProperty("gameSha256").GetString()!=GameHash)throw new InvalidDataException("Read module version differs from the pinned ABI/game.");
            var file=metadata.GetProperty("file").GetString()??"";
            var generation=metadata.GetProperty("generation").GetString()??"";
            var hash=metadata.GetProperty("sha256").GetString()??"";
            if(file.Length is <5 or >160||Path.GetFileName(file)!=file||!file.EndsWith(".dll",StringComparison.Ordinal)
                ||file.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c is not ('-' or '_' or '.'))
                ||generation.Length is 0 or >64||generation.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c is not ('-' or '_'))
                ||hash.Length!=64||hash.Any(c=>!char.IsAsciiHexDigit(c)))throw new InvalidDataException("Invalid read module identity.");
            var operations=new HashSet<string>(StringComparer.Ordinal);
            var routes=metadata.GetProperty("operations");
            if(routes.ValueKind!=JsonValueKind.Array||routes.GetArrayLength() is 0 or >128)throw new InvalidDataException("Invalid read module operations.");
            foreach(var route in routes.EnumerateArray())
            {
                var operation=route.GetString()??"";
                if(!Allowed(operation)||!operations.Add(operation))throw new InvalidDataException("Unsupported or duplicate hot read operation.");
            }
            var rpcMethods=new HashSet<byte>();
            if(metadata.TryGetProperty("rpcAbi",out var rpcAbi))
            {
                if(rpcAbi.GetInt32()!=1)throw new InvalidDataException("Unsupported local RPC module ABI.");
                var methods=metadata.GetProperty("rpcMethods");
                if(methods.ValueKind!=JsonValueKind.Array||methods.GetArrayLength() is 0 or >32)throw new InvalidDataException("Invalid RPC module methods.");
                foreach(var method in methods.EnumerateArray())
                    if(!method.TryGetByte(out var number)||number>=155||!rpcMethods.Add(number))throw new InvalidDataException("Invalid or duplicate RPC method.");
            }
            var path=Path.Combine(root,file);NoLink(path);
            var saveAbi=metadata.TryGetProperty("saveAbi",out var savedAbi);
            if(saveAbi&&savedAbi.GetInt32()!=1)throw new InvalidDataException("Unsupported local save module ABI.");
            if(mapped.TryGetValue(path,out var existing))
            {
                if(existing.Hash!=hash||existing.Generation!=generation||!existing.Operations.SetEquals(operations)||!existing.RpcMethods.SetEquals(rpcMethods)||existing.SaveAbi!=saveAbi)
                    throw new InvalidDataException("A loaded read generation is immutable; publish a new filename.");
                current=existing;
            }
            else
            {
                if(mapped.Count>=128)throw new InvalidDataException("The process has reached its retained read module generation bound.");
                var lease=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
                try
                {
                    if(lease.Length is <=0 or >64*1024*1024||Convert.ToHexStringLower(SHA256.HashData(lease))!=hash)
                        throw new InvalidDataException("Read module integrity verification failed.");
                    var handle=NativeLibrary.Load(path); // No unload: this is an independent NativeAOT runtime.
                    if(((delegate* unmanaged<int>)NativeLibrary.GetExport(handle,"XfeReadModuleVersion"))()!=1)
                        throw new InvalidDataException("Unsupported native read module ABI.");
                    var loaded=new Module(handle,lease,hash,generation,operations,rpcMethods,saveAbi);
                    mapped.Add(path,loaded);current=loaded;lease=null!;
                }
                finally{lease?.Dispose();}
            }
            acceptedManifest=candidate;lastFailure="";
            OfflineDiagnostics.Current?.Write("info","business.hotload","module.activated","New C# read module activated without replacing sessions or saves.",
                new Dictionary<string,string?>{["generation"]=generation,["moduleFile"]=file,["operations"]=operations.Count.ToString(),
                    ["rpcMethods"]=string.Join(",",rpcMethods.Order()),["saveAbi"]=saveAbi.ToString(),["retainedGenerations"]=mapped.Count.ToString()});
        }
        catch(Exception error)when(error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or KeyNotFoundException
            or InvalidOperationException or FormatException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            if(startup)throw new InvalidDataException("The initial local read module is invalid.",error);
            if(candidate!=lastFailure)
            {
                lastFailure=candidate;
                OfflineDiagnostics.Current?.Write("error","business.hotload","module.reload.rejected","Read module reload rejected; the last validated generation remains active.",
                    new Dictionary<string,string?>{["activeGeneration"]=current?.Generation},error);
            }
        }
    }
    // These operations bind identity, world/session cryptography or the authoritative save and remain in the stable host.
    private static bool Allowed(string path)=>path.StartsWith("/2.0/elite/",StringComparison.Ordinal)&&path.Length is >11 and <=512
        &&path[11..].All(c=>char.IsAsciiLetterOrDigit(c)||c is '/' or '_' or '-')
        &&path is not("/2.0/elite/user" or "/2.0/elite/user/login" or "/2.0/elite/user/logout"
            or "/2.0/elite/commander/login" or "/2.0/elite/commander/logout" or "/2.0/elite/commander/savegame"
            or "/2.0/elite/server/list" or "/2.0/elite/starsystem/key");
    private static bool IsIdentity(string key)=>key.Contains("token",StringComparison.OrdinalIgnoreCase)
        ||key.Contains("password",StringComparison.OrdinalIgnoreCase)||key.Contains("machine",StringComparison.OrdinalIgnoreCase)
        ||key.Contains("auth",StringComparison.OrdinalIgnoreCase)||key.Equals("runID",StringComparison.OrdinalIgnoreCase)
        ||key.Equals("cmdrId",StringComparison.OrdinalIgnoreCase)||key.Equals("fTime",StringComparison.OrdinalIgnoreCase);
    private static byte[] Read(string path,int maximum)
    {
        NoLink(path);using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete);
        if(input.Length is <=0||input.Length>maximum)throw new InvalidDataException("Invalid read module manifest length.");
        var bytes=new byte[(int)input.Length];input.ReadExactly(bytes);return bytes;
    }
    private static void NoLink(string path)
    {if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Hot read modules cannot use filesystem links.");}
}
