using System.Runtime.InteropServices;
using System.Text.Json;
using XFEEliteDangerous.Client;
using XFEEliteDangerous.Core;

namespace XFEEliteDangerous.ReadBusiness;

public static unsafe class SaveServices
{
    [UnmanagedCallersOnly(EntryPoint="XfeSaveModuleInvoke")]
    public static int Invoke(byte* input,int length,byte** output,int* outputLength)
    {
        if(output==null||outputLength==null)return 2;*output=null;*outputLength=0;
        try
        {
            if(input==null||length is <=0 or >8*1024*1024)throw new InvalidDataException("Invalid native save input buffer.");
            var request=JsonSerializer.Deserialize(new ReadOnlySpan<byte>(input,length),NativeReadModuleJson.Default.NativeSaveModuleRequest)
                ??throw new InvalidDataException("Missing native save request.");
            Write(NativeSaveModuleAdapter.Evaluate(request),output,outputLength);return 0;
        }
        catch(Exception error)
        {
            try{Write(new(1,500,"",null,error.GetType().FullName,error.Message,error.ToString()),output,outputLength);}catch{}
            return 2;
        }
    }
    private static void Write(NativeSaveModuleReply reply,byte** output,int* length)
    {
        var bytes=JsonSerializer.SerializeToUtf8Bytes(reply,NativeReadModuleJson.Default.NativeSaveModuleReply);
        if(bytes.Length>8*1024*1024)throw new InvalidDataException("Native save reply exceeds its bound.");
        var buffer=(byte*)NativeMemory.Alloc((nuint)bytes.Length);bytes.CopyTo(new Span<byte>(buffer,bytes.Length));
        *length=bytes.Length;*output=buffer;
    }
}
