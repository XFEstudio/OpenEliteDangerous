using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XFEEliteDangerous.Core;
using XFEEliteDangerous.NativeFormat;

namespace XFEEliteDangerous.OfflineRuntime;

internal static unsafe partial class OfflineCurl
{
    private static readonly StringBuilder fixtureHeaders = new();
    private static readonly MemoryStream fixtureBody = new();
    private static byte[] fixtureUpload = [];
    private static int fixtureCursor;
    private static int fixtureProgressCalls;
    [UnmanagedCallersOnly] private static int FixtureOption(nint handle,int option,nint value)=>0;
    [UnmanagedCallersOnly] private static int FixtureProgress(nint context,double downloadTotal,double downloadNow,double uploadTotal,double uploadNow)
    { fixtureProgressCalls++;return 0; }
    [UnmanagedCallersOnly] private static nuint FixtureWrite(byte* bytes,nuint size,nuint count,nint context)
    {
        var span=new ReadOnlySpan<byte>(bytes,checked((int)(size*count)));
        if(context==1)fixtureHeaders.Append(Encoding.ASCII.GetString(span));else fixtureBody.Write(span);
        return (nuint)span.Length;
    }
    [UnmanagedCallersOnly] private static nuint FixtureRead(byte* bytes,nuint size,nuint count,nint context)
    {
        var length=Math.Min(checked((int)(size*count)),fixtureUpload.Length-fixtureCursor);
        fixtureUpload.AsSpan(fixtureCursor,length).CopyTo(new Span<byte>(bytes,length));fixtureCursor+=length;return(nuint)length;
    }
    internal static int CallbackSelfTest(OfflineGameData source,string directory)
    {
        if(hooks.Count!=0)throw new InvalidOperationException("Callback fixtures never run against installed game hooks.");
        var oldData=data;var oldKey=key;var oldLog=log;var oldOption=originalOption;var oldCompleted=completed;
        var allocations=new List<nint>();var handle=(nint)0x12345;var multi=(nint)0x54321;
        try
        {
            data=source;key=RandomNumberGenerator.GetBytes(32);log=Path.Combine(directory,"callback-fixture.tsv");originalOption=&FixtureOption;
            requests[handle]=new();
            var headers=Allocate(32);Marshal.WriteIntPtr(headers,TextBuffer("Nonce: 001122334455"));Marshal.WriteIntPtr(headers+8,headers+16);
            Marshal.WriteIntPtr(headers+16,TextBuffer("Encrypted: 1"));Marshal.WriteIntPtr(headers+24,0);
            fixtureProgressCalls=0;
            Set(20056,(nint)(delegate* unmanaged<nint,double,double,double,double,int>)&FixtureProgress);
            Set(10023,headers);Set(20079,(nint)(delegate* unmanaged<byte*,nuint,nuint,nint,nuint>)&FixtureWrite);Set(10029,1);
            Set(20011,(nint)(delegate* unmanaged<byte*,nuint,nuint,nint,nuint>)&FixtureWrite);Set(10001,2);
            Set(10002,TextBuffer("https://local.invalid/2.0/server/time"));
            if(!Complete()||requests[handle].Status!=200)return 24;
            using(var time=JsonDocument.Parse(fixtureBody.ToArray()))if(time.RootElement.GetProperty("unixTimestamp").GetInt64()<1)return 25;
            var url=TextBuffer("https://local.invalid/2.0/elite/user/login?");Set(10002,url);
            // The caller's memory is no longer valid after setopt. This catches use-after-free
            // without ever starting a game, making a connection, or dereferencing game memory.
            new Span<byte>((void*)url,20).Fill((byte)'x');
            if(!Complete()||requests[handle].Status!=200)return 20;
            using(var login=JsonDocument.Parse(Response()))if(!login.RootElement.TryGetProperty("runId",out _))return 21;
            fixtureUpload=Encoding.ASCII.GetBytes(EliteRequestEnvelopeCodec.Encode(key,"001122334455",[],
                "{\"privacy_mode\":\"PRIVATE\",\"accepting_squadron_invites\":false}"u8).BodyBase64);fixtureCursor=0;
            Set(10002,TextBuffer("https://local.invalid/2.0/elite/commander/settings?"));
            Set(46,1);Set(14,fixtureUpload.Length);Set(20012,(nint)(delegate* unmanaged<byte*,nuint,nuint,nint,nuint>)&FixtureRead);Set(10009,1);
            if(!Complete()||requests[handle].Status!=200||fixtureCursor!=fixtureUpload.Length||Response()!="{}")return 22;
            var settings=source.InvokeAsync("GET","/2.0/elite/commander/settings",new Dictionary<string,string>(),[]).GetAwaiter().GetResult();
            using(var parsed=JsonDocument.Parse(settings.Payload))if(parsed.RootElement.GetProperty("settings").GetProperty("accepting_squadron_invites").GetBoolean())return 23;
            if(fixtureProgressCalls!=0)return 26;
            return 0;
            bool Complete()
            {
                fixtureHeaders.Clear();fixtureBody.SetLength(0);
                if(((delegate* unmanaged<nint,nint,int>)&Add)(multi,handle)!=0)return false;
                var remaining=0;var message=((delegate* unmanaged<nint,int*,nint>)&Next)(multi,&remaining);
                return message!=0&&*(int*)message==1&&*(nint*)(message+8)==handle&&*(int*)(message+16)==0&&remaining==0;
            }
            string Response()
            {
                var fields=fixtureHeaders.ToString().Split("\r\n").Where(line=>line.Contains(':')).Select(line=>line.Split(':',2)).ToDictionary(pair=>pair[0],pair=>pair[1].Trim());
                var result=EliteResponseEnvelopeCodec.Decode(key,new(fields["Nonce"],fields["uncompressedSize"],Encoding.ASCII.GetString(fixtureBody.ToArray())));
                try{return Encoding.UTF8.GetString(result.Payload);}finally{CryptographicOperations.ZeroMemory(result.Payload);}
            }
            void Set(int option,nint value){if(((delegate* unmanaged<nint,int,nint,int>)&Option)(handle,option,value)!=0)throw new InvalidOperationException("Owned fixture option failed.");}
            nint Allocate(int count){var pointer=(nint)NativeMemory.AllocZeroed((nuint)count);allocations.Add(pointer);return pointer;}
            nint TextBuffer(string text){var bytes=Encoding.UTF8.GetBytes(text+'\0');var pointer=Allocate(bytes.Length);Marshal.Copy(bytes,0,pointer,bytes.Length);return pointer;}
        }
        finally
        {
            requests.TryRemove(handle,out _);if(queues.TryRemove(multi,out var queue))NativeMemory.Free((void*)queue.Message);
            foreach(var pointer in allocations)NativeMemory.Free((void*)pointer);
            CryptographicOperations.ZeroMemory(key);CryptographicOperations.ZeroMemory(fixtureUpload);fixtureUpload=[];
            data=oldData;key=oldKey;log=oldLog;originalOption=oldOption;completed=oldCompleted;fixtureHeaders.Clear();fixtureBody.SetLength(0);
        }
    }
}
