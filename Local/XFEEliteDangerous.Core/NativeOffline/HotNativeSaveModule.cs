using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using XFEEliteDangerous.Client;

namespace XFEEliteDangerous.Core;

internal sealed unsafe partial class HotNativeReadModule
{
    internal NativeDataReply? InvokeSave(AccountState account,bool selected,byte[] body)
    {
        lock(gate)
        {
            Refresh(false);var module=current;
            if(module is null||module.SaveInvoke==0)return null;
            var request=NativeSaveModuleAdapter.Request(account,selected,body);
            var bytes=JsonSerializer.SerializeToUtf8Bytes(request,NativeReadModuleJson.Default.NativeSaveModuleRequest);
            byte* output=null;var length=0;
            try
            {
                if(bytes.Length>8*1024*1024)throw new NativeDataException(413,"Native save module input exceeds its bound.");
                int result;fixed(byte* input=bytes)result=((delegate* unmanaged<byte*,int,byte**,int*,int>)module.SaveInvoke)(input,bytes.Length,&output,&length);
                if(result==1&&output==null&&length==0)return null;
                if(output==null||length is <=0 or >8*1024*1024)throw new InvalidDataException("Native save module returned an invalid buffer.");
                var reply=JsonSerializer.Deserialize(new ReadOnlySpan<byte>(output,length),NativeReadModuleJson.Default.NativeSaveModuleReply)
                    ??throw new InvalidDataException("Missing native save module reply.");
                if(result!=0||reply.Protocol!=1||reply.ErrorType is not null)
                    OfflineDiagnostics.Current?.Write(result==0?"warning":"error","business.hotload","save.module.diagnostic",
                        "Replaceable native save handler supplied an exact C# diagnostic.",new Dictionary<string,string?>
                        {["generation"]=module.Generation,["httpStatus"]=reply.Status.ToString(),["exceptionType"]=reply.ErrorType,
                            ["error"]=reply.Error,["stackTrace"]=reply.StackTrace});
                if(result!=0)throw new NativeDataException(500,"The active local save handler failed; inspect business.hotload diagnostics.");
                var response=NativeSaveModuleAdapter.Commit(account,reply);
                OfflineDiagnostics.Current?.Write(response.Status>=400?"warning":"debug","business.hotload","save.module.reply",
                    "Native save candidate returned to the fixed host's StateStore transaction.",new Dictionary<string,string?>
                    {["generation"]=module.Generation,["httpStatus"]=response.Status.ToString(),["revision"]=account.Commander.Revision.ToString()});
                return response;
            }
            catch(Exception error)when(error is InvalidDataException or JsonException or ArgumentException)
            {
                OfflineDiagnostics.Current?.Write("error","business.hotload","save.module.contract.failed",
                    "Native save candidate rejected before the durable transaction.",new Dictionary<string,string?>{["generation"]=module.Generation},error);
                throw new NativeDataException(500,"The local save module contract failed; inspect business.hotload diagnostics.");
            }
            finally
            {CryptographicOperations.ZeroMemory(bytes);if(output!=null)((delegate* unmanaged<byte*,void>)module.Free)(output);}
        }
    }
}
