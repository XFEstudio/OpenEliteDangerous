using System.Runtime.InteropServices;
using System.Text.Json;
using System.Buffers.Binary;
using System.Text;
using XFEEliteDangerous.Client;
using XFEEliteDangerous.NativeFormat.Replication;

namespace XFEEliteDangerous.ReadBusiness;

public static unsafe class RpcServices
{
    [UnmanagedCallersOnly(EntryPoint="XfeRpcModuleInvoke")]
    public static int Invoke(byte* input,int length,byte** output,int* outputLength)
    {
        if(output==null||outputLength==null)return 2;*output=null;*outputLength=0;
        try
        {
            if(input==null||length is <=0 or >65536)throw new InvalidDataException("Invalid RPC request buffer.");
            var request=JsonSerializer.Deserialize(new ReadOnlySpan<byte>(input,length),NativeReadModuleJson.Default.NativeRpcModuleRequest)
                ??throw new InvalidDataException("Missing RPC request.");
            if(request.Protocol!=1||request.Payload is null||request.PayloadBits<8)throw new InvalidDataException("Invalid RPC request contract.");
            if(request.Role is not ("primary" or "missions"))return 1;
            if(request.LetterType==23&&request.Payload[0]==89)
            {
                //984E70: U32 correlation plus an ordinary narrow FString.
                //90 shares984D60/933CE0 with79: the same compressed record-map
                //reply. A fresh local record namespace contains no records.
                if(!request.AlignedValues||request.Payload.Length<9||request.PayloadBits!=request.Payload.Length*8)
                    throw new InvalidDataException("Invalid local named record request.");
                var nameLength=BinaryPrimitives.ReadUInt32LittleEndian(request.Payload.AsSpan(5));
                if(nameLength is 0 or >1024||nameLength!=request.Payload.Length-9||request.Payload.AsSpan(9).Contains((byte)0))
                    throw new InvalidDataException("Invalid local record namespace.");
                _=new UTF8Encoding(false,true).GetCharCount(request.Payload.AsSpan(9));
                var correlation=BinaryPrimitives.ReadUInt32LittleEndian(request.Payload.AsSpan(1));
                using var response=EdServerPersistentRecordMapMessageCodec.CreateEmptyReply(0,correlation);
                var bytes=response.CopyPayload();bytes[0]=90;
                Write(new(1,true,[new(23,response.Flag,response.PayloadBits,bytes)]),output,outputLength);return 0;
            }
            if(request.Role!="primary")return 1;
            if(request.LetterType==22&&request.Payload[0]==9)
            {
                // The pinned 986150->95A990 sender identifies method9 at+28.
                // Its5130858/982970 reader consumes exactly one U32 before
                // dispatching9861B0. That callback has no result or response
                // and ignores the scalar. In the single-machine local peer
                // this is a one-way receipt: ACK its reliable group without
                // inventing a reply or modifying native readiness. Rejecting
                // it rolls back the definition/island replies in that group
                // on a second SOLO entry and leaves PointOfFocus awaiting live.
                if(request.Flag||!request.AlignedValues||request.PayloadBits!=40||request.Payload.Length!=5)
                    throw new InvalidDataException("Invalid local scalar notification (method 9).");
                Write(new(1,true,[]),output,outputLength);return 0;
            }
            if(request.LetterType==22&&request.Payload[0]==32)
            {
                //988420 forwards the authenticated sender and one U32 to
                //the island-service interface+50. The pinned client binds it
                //to201090 (no-op), so no client response exists. The host
                //commits this one-way receipt and acknowledges its reliable
                //group; it does not set a disconnect or world-ready flag.
                if(request.Flag||!request.AlignedValues||request.PayloadBits!=40||request.Payload.Length!=5)
                    throw new InvalidDataException("Invalid local exit notification.");
                Write(new(1,true,[]),output,outputLength);return 0;
            }
            if(request.LetterType==23&&request.Payload[0]==17)
            {
                var query=EdServerInstanceDefinitionMessageCodec.ReadRequest(request.Payload,request.PayloadBits,request.AlignedValues);
                RequireSystem(query.System,request.CurrentSystem);
                using var response=EdServerInstanceDefinitionMessageCodec.CreateEmptyReply(0,query.Target);
                Write(new(1,true,[new(23,response.Flag,response.PayloadBits,response.CopyPayload())]),output,outputLength);return 0;
            }
            if(request.LetterType==22&&request.Payload[0]==2)
            {
                var query=EdServerIslandMessageCodec.ReadSoloRequest(request.Payload,request.PayloadBits,request.AlignedValues);
                RequireSystem(query.Address,request.CurrentSystem);
                using var response=EdServerIslandMessageCodec.CreateSoloAssignment(0,query.Correlation,request.IslandId,request.WakeId);
                Write(new(1,true,[new(23,response.Flag,response.PayloadBits,response.CopyPayload())]),output,outputLength);return 0;
            }
            return 1;
        }
        catch(Exception error)
        {
            try{Write(new(1,false,[],error.GetType().FullName,error.Message,error.ToString()),output,outputLength);}catch{}
            return 2;
        }
    }
    private static void RequireSystem(ulong requested,ulong selected)
    {
        if(selected==0||(requested&0x007F_FFFF_FFFF_FFFF)!=(selected&0x007F_FFFF_FFFF_FFFF))
            throw new InvalidDataException("The request is outside the selected local system.");
    }
    private static void Write(NativeRpcModuleReply reply,byte** output,int* length)
    {
        var bytes=JsonSerializer.SerializeToUtf8Bytes(reply,NativeReadModuleJson.Default.NativeRpcModuleReply);
        if(bytes.Length>65536)throw new InvalidDataException("RPC reply exceeds its bound.");
        var buffer=(byte*)NativeMemory.Alloc((nuint)bytes.Length);bytes.CopyTo(new Span<byte>(buffer,bytes.Length));*length=bytes.Length;*output=buffer;
    }
}
