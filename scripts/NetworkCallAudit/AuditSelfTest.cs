using System.Reflection.Emit;
using System.Text;
using Iced.Intel;
using Decoder=Iced.Intel.Decoder;
using FlowControl=Iced.Intel.FlowControl;

internal static class AuditSelfTest
{
    internal static int Run()
    {
        var checks=new Dictionary<string,bool>();
        var reader=new ByteArrayCodeReader(new byte[]{0xFF,0x15,0x20,0,0,0});var decoder=Decoder.Create(64,reader);decoder.IP=0x140001000;
        decoder.Decode(out var call);
        checks["native-rip-relative-call-golden"]=call.FlowControl==FlowControl.IndirectCall&&PeNetworkAudit.TryIatRva(in call,0x140000000,out var rva)&&rva==0x1026;
        var ranges=new[]{new PeNetworkAudit.FunctionRange(0x1000,0x1010),new PeNetworkAudit.FunctionRange(0x1020,0x1040)};
        checks["function-lookup-half-open-and-gap"]=PeNetworkAudit.FindFunction(ranges,0x100F)?.Begin==0x1000&&PeNetworkAudit.FindFunction(ranges,0x1010) is null
            &&PeNetworkAudit.FindFunction(ranges,0x1020)?.Begin==0x1020&&PeNetworkAudit.FindFunction(ranges,0x1040) is null;
        checks["endpoint-secret-parts-omitted"]=EndpointScanner.Redact("https://user:password@example.invalid:9443/path?token=private#secret")=="https://example.invalid:9443/path";
        checks["ipv6-literal-format-preserved"]=EndpointScanner.Redact("https://[::1]:9443/path?token=hidden")=="https://[::1]:9443/path";
        var bytes=Encoding.ASCII.GetBytes("\0https://example.invalid/a?token=hidden\0").Concat(Encoding.Unicode.GetBytes("https://example.invalid/b#secret\0")).ToArray();
        var literals=EndpointScanner.Scan(bytes,offset=>(uint)offset);
        checks["ascii-and-utf16-literals-located-without-query"]=literals.Count==2&&literals.Any(value=>value.Encoding=="ASCII"&&value.Endpoint.EndsWith("/a"))
            &&literals.Any(value=>value.Encoding=="UTF-16LE"&&value.Endpoint.EndsWith("/b"))&&!literals.Any(value=>value.Endpoint.Contains("hidden")||value.Endpoint.Contains("secret"));
        checks["transport-and-adapter-distinguished"]=ApiClassification.Native("ws2_32.dll","sendto")=="Winsock transport"
            &&ApiClassification.Native("iphlpapi.dll","GetAdaptersAddresses")=="Adapter/connectivity query";
        checks["local-file-and-crypto-not-mislabeled-network"]=ApiClassification.Native("kernel32.dll","CreateFileW") is null
            &&ApiClassification.Native("crypt32.dll","CryptHashCertificate") is null;
        checks["local-helper-not-counted-as-network-io"]=ApiClassification.Effect("WS2_32.dll!WSAGetLastError [ordinal 111]","Winsock transport")=="local-helper"
            &&ApiClassification.Effect("WS2_32.dll!htons [ordinal 9]","Winsock transport")=="local-helper"
            &&ApiClassification.Effect("WS2_32.dll!freeaddrinfo","DNS/address")=="local-helper"
            &&ApiClassification.Effect("WS2_32.dll!sendto [ordinal 20]","Winsock transport")=="transport-io";
        checks["managed-http-and-dns-distinguished"]=ApiClassification.Managed("System.Net.Http.HttpClient")=="HTTP/WebSocket"
            &&ApiClassification.Managed("System.Net.Dns")=="DNS/address";
        checks["il-switch-operand-length"]=ManagedCallScanner.OperandLength(OperandType.InlineSwitch,[2,0,0,0,0,0,0,0,0,0,0,0],0)==12;
        var paths=new List<ApiPathLiteral>();EndpointScanner.Scan(Encoding.ASCII.GetBytes("\0"+"2.0/elite/server/list\0/2.0/elite/user/arcs/balance\0"),offset=>(uint)offset,paths);
        checks["relative-native-api-paths-with-rva"]=paths.Count==2&&paths[0] is {Path:"/2.0/elite/server/list",Rva:"0x1"}
            &&paths[1].Path=="/2.0/elite/user/arcs/balance";
        var methodReader=new ByteArrayCodeReader(new byte[]{0x4C,0x8D,0x05,0xF9,0x0F,0,0,0xBA,3,0,0,0,0x48,0x8B,0xCB,0xE8,0,0,0,0});
        var methodDecoder=Decoder.Create(64,methodReader);methodDecoder.IP=0x140001000;var methodHistory=new List<Instruction>();
        while(methodReader.CanReadByte)methodHistory.Add(methodDecoder.Decode());
        var methodLiterals=new Dictionary<uint,ApiPathLiteral>{{0x2000,new("0x2000","ASCII","/2.0/elite/example")}};
        checks["request-method-constant-same-block"]=NativeApiMethodScanner.Read(methodHistory.Take(3).ToArray(),0x140000000,methodLiterals,0x1000) is {Method:"PUT",NativeMethod:3};
        checks["request-method-call-clobber-not-inferred"]=NativeApiMethodScanner.Read(methodHistory,0x140000000,methodLiterals,0x1000) is null;
        checks["request-method-unknown-literal-not-inferred"]=NativeApiMethodScanner.Read(methodHistory.Take(3).ToArray(),0x140000000,new Dictionary<uint,ApiPathLiteral>(),0x1000) is null;
        List<Instruction> Decode(byte[] code)
        {
            var source=new ByteArrayCodeReader(code);var engine=Decoder.Create(64,source);engine.IP=0x140001000;var result=new List<Instruction>();
            while(source.CanReadByte)result.Add(engine.Decode());return result;
        }
        checks["request-method-callee-saved-zero-lea"]=NativeApiMethodScanner.Read(Decode([0x31,0xED,0xE8,0,0,0,0,0x4C,0x8D,0x05,0xF2,0x0F,0,0,0x8D,0x55,3]),
            0x140000000,methodLiterals,0x1000) is {Method:"PUT",NativeMethod:3};
        checks["request-method-memory-value-not-inferred"]=NativeApiMethodScanner.Read(Decode([0x8B,0x2B,0xE8,0,0,0,0,0x4C,0x8D,0x05,0xF2,0x0F,0,0,0x8D,0x55,3]),
            0x140000000,methodLiterals,0x1000) is null;
        checks["request-method-incoming-branch-invalidates-fallthrough-constant"]=NativeApiMethodScanner.Read(Decode([0x75,2,0x31,0xED,0x4C,0x8D,0x05,0xF5,0x0F,0,0,0x8D,0x55,3]),
            0x140000000,methodLiterals,0x1000) is null;
        checks["request-method-partial-register-write-invalidates"]=NativeApiMethodScanner.Read(Decode([0x4C,0x8D,0x05,0xF9,0x0F,0,0,0xBA,3,0,0,0,0xB2,1]),
            0x140000000,methodLiterals,0x1000) is null;
        checks["request-method-cfg-preserves-common-constant"]=NativeApiMethodScanner.Scan(Decode([0x31,0xED,0x85,0xC9,0x75,2,0xFF,0xC0,
            0x4C,0x8D,0x05,0xF1,0x0F,0,0,0x8D,0x55,3,0xE8,0xD9,0xE3,0x99,0]),0x140000000,methodLiterals,0x1000).SingleOrDefault() is {Method:"PUT"};
        checks["request-method-cfg-conflicting-values-not-inferred"]=!NativeApiMethodScanner.Scan(Decode([0x31,0xED,0x85,0xC9,0x75,5,0xBD,1,0,0,0,
            0x4C,0x8D,0x05,0xEE,0x0F,0,0,0x8D,0x55,3,0xE8,0xD6,0xE3,0x99,0]),0x140000000,methodLiterals,0x1000).Any();
        var alternatives=new Dictionary<uint,ApiPathLiteral>(methodLiterals){{0x2010,new("0x2010","ASCII","/2.0/elite/alternative")}};
        var choices=NativeApiMethodScanner.Scan(Decode([0x85,0xC9,0x75,9,0x4C,0x8D,0x05,0xF5,0x0F,0,0,0xEB,7,
            0x4C,0x8D,0x05,0xFC,0x0F,0,0,0xBA,3,0,0,0,0xE8,0xD2,0xE3,0x99,0]),0x140000000,alternatives,0x1000).ToArray();
        checks["request-method-cfg-literal-alternatives-have-one-proven-method"]=choices.Length==2&&choices.All(choice=>choice.Method=="PUT")&&choices.Select(choice=>choice.Path).Distinct().Count()==2;
        checks["request-method-delete-is-explicit-enum-four"]=NativeApiMethodScanner.Read(Decode([0x4C,0x8D,0x05,0xF9,0x0F,0,0,0xBA,4,0,0,0]),
            0x140000000,methodLiterals,0x1000) is {Method:"DELETE",NativeMethod:4};
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new{passed=checks.Values.All(value=>value),checks,gameRead=false,networkUsed=false}));
        return checks.Values.All(value=>value)?0:1;
    }
}
