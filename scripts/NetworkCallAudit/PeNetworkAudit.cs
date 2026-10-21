using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using Iced.Intel;
using Decoder=Iced.Intel.Decoder;

internal sealed record NetworkImport(string Library,string Api,string IatRva,bool Delayed,string Category);
internal sealed record NativeNetworkCall(string Api,string Category,string SiteRva,string FunctionRva,string Binding)
{
    public string Effect=>ApiClassification.Effect(Api,Category);
}
internal sealed record ManagedNetworkCall(string Api,string Category,string IlOffset,string Method);
internal sealed record InternalNetworkEntry(string Name,string Rva,string Adaptation,int DirectCallSites,string[] DirectCallerFunctions,int ReadOnlyPointerReferences);
internal sealed record NetworkFunction(string Rva,string[] Apis,string[] DirectCallers,string[] CallersAtDepthTwo,bool CallerListTruncated);
internal sealed record ApiPathReference(string Path,string LiteralRva,string SiteRva,string FunctionRva);
internal sealed record NativeScanCoverage(int IndexedFunctions,long DecodedInstructions,int InvalidInstructions,int RegisterIndirectCalls,
    int StaticDirectEdges,string Boundary);
internal sealed record ImageAudit(string Image,string Sha256,long Bytes,string Machine,bool Managed,bool IsMain,
    List<NetworkImport> Imports,List<NativeNetworkCall> NativeCalls,List<ManagedNetworkCall> ManagedCalls,
    int DynamicResolverCalls,List<EndpointLiteral> Endpoints,List<ApiPathLiteral> ApiPaths,List<ApiPathReference> ApiPathReferences,List<ApiMethodReference> ApiMethods,List<InternalNetworkEntry> KnownInternalEntries,
    List<NetworkFunction> NetworkFunctions,NativeScanCoverage Coverage);

internal static partial class PeNetworkAudit
{
    private sealed record Symbol(string Library,string Api,uint Iat,bool Delayed);
    internal sealed record FunctionRange(uint Begin,uint End);
    private sealed record FunctionFamily(uint Entry,FunctionRange[] Ranges);
    private sealed record Seed(string Name,uint Rva,string Adaptation);
    private static readonly Seed[] MainSeeds=
    [
        new("curl_easy_init wrapper",0x48D5720,"C# bridge connected-loopback callbacks installed"),
        new("curl_easy_setopt wrapper",0x48D5770,"Private CA, local URL normalization and open/socket/close callbacks"),
        new("curl_multi_add_handle wrapper",0x48D6A50,"Existing C# numeric observer; original operation retained"),
        new("curl_multi_info_read wrapper",0x48D6E10,"Existing C# numeric observer; original operation retained"),
        new("curl_multi_perform wrapper",0x48D6EA0,"Existing C# numeric observer; original operation retained"),
        new("native UDP Init",0x4C4D700,"Optional C# virtual UDP initialization; fixed parent-owned loopback byte relay verified"),
        new("native UDP whole send",0x4C4DE50,"Optional C# parent relay; actual native MTU/ID/application membership exchange observed"),
        new("native UDP Post receive",0x4C4E790,"Optional original-worker APC completions; actual game receive callbacks verified"),
        new("native UDP close",0x4C4A280,"Original close retained; no readiness predicate forced")
    ];
    internal static ImageAudit Read(string path,string relative,bool main)
    {
        var bytes=File.ReadAllBytes(path);using var image=new Image(bytes);
        var imports=ReadImports(image).ToArray();
        var slots=imports.GroupBy(symbol=>symbol.Iat).ToDictionary(group=>group.Key,group=>group.First());
        var networkImports=imports.Select(symbol=>new{symbol,category=ApiClassification.Native(symbol.Library,symbol.Api)})
            .Where(value=>value.category is not null).Select(value=>new NetworkImport(value.symbol.Library,value.symbol.Api,
                Hex(value.symbol.Iat),value.symbol.Delayed,value.category!)).ToList();
        var calls=new List<NativeNetworkCall>();var managedCalls=new List<ManagedNetworkCall>();var resolverCalls=0;
        var apiPaths=new List<ApiPathLiteral>();var endpoints=EndpointScanner.Scan(bytes,image.FileOffsetToRva,apiPaths);
        var pathRvas=apiPaths.GroupBy(path=>Convert.ToUInt32(path.Rva[2..],16)).ToDictionary(group=>group.Key,group=>group.First());
        var pathReferences=new List<ApiPathReference>();
        var apiMethods=new List<ApiMethodReference>();
        var functions=FunctionRanges(image);var ranges=functions.ToArray();
        var reverse=new Dictionary<uint,HashSet<uint>>();var knownCalls=new Dictionary<uint,List<uint>>();
        var seeds=main?MainSeeds:[];
        long instructions=0;var invalid=0;var indirect=0;var edges=0;
        if(image.Pe.HasMetadata)managedCalls=ManagedCallScanner.Scan(image.Pe);
        else if(image.Is64)
            foreach(var family in FunctionFamilies(image,functions))
            {
                var functionInstructions=new List<Instruction>();
                foreach(var range in family.Ranges)
                {
                    if(image.Offset(range.Begin,(int)(range.End-range.Begin)) is not { } offset)continue;
                    var reader=new ByteArrayCodeReader(bytes,offset,(int)(range.End-range.Begin));
                    var decoder=Decoder.Create(64,reader);decoder.IP=image.Base+range.Begin;
                    while(decoder.IP<image.Base+range.End)
                    {
                    decoder.Decode(out var instruction);instructions++;
                    if(instruction.Code==Code.INVALID){invalid++;if(main)functionInstructions.Add(instruction);continue;}
                    if(main)functionInstructions.Add(instruction);
                    if(instruction.IsIPRelativeMemoryOperand&&instruction.IPRelativeMemoryAddress>=image.Base
                        &&instruction.IPRelativeMemoryAddress-image.Base<=uint.MaxValue
                        &&pathRvas.TryGetValue((uint)(instruction.IPRelativeMemoryAddress-image.Base),out var pathLiteral))
                        pathReferences.Add(new(pathLiteral.Path,pathLiteral.Rva,Hex((uint)(instruction.IP-image.Base)),Hex(range.Begin)));
                    if(instruction.FlowControl is not (FlowControl.Call or FlowControl.IndirectCall or FlowControl.UnconditionalBranch or FlowControl.IndirectBranch))continue;
                    if(instruction.FlowControl==FlowControl.IndirectCall&&instruction.Op0Kind==OpKind.Register)indirect++;
                    var symbol=ResolveImport(image,slots,in instruction,out var binding);
                    if(symbol is not null)
                    {
                        if(ApiClassification.Resolver(symbol.Api))resolverCalls++;
                        if(ApiClassification.Native(symbol.Library,symbol.Api) is { } category)
                            calls.Add(new(symbol.Library+"!"+symbol.Api,category,Hex(checked((uint)(instruction.IP-image.Base))),Hex(range.Begin),binding));
                    }
                    if(instruction.Op0Kind is not (OpKind.NearBranch64 or OpKind.NearBranch32 or OpKind.NearBranch16))continue;
                    var target=instruction.NearBranchTarget;
                    if(target<image.Base||target-image.Base>uint.MaxValue)continue;
                    var rva=(uint)(target-image.Base);
                    if(seeds.Any(seed=>seed.Rva==rva))
                    {if(!knownCalls.TryGetValue(rva,out var sites))knownCalls.Add(rva,sites=[]);sites.Add(range.Begin);}
                    if(FindFunction(ranges,rva) is not { } callee||callee.Begin==range.Begin)continue;
                    if(!reverse.TryGetValue(callee.Begin,out var parents))reverse.Add(callee.Begin,parents=[]);
                    if(parents.Add(range.Begin))edges++;
                    }
                }
                if(main)apiMethods.AddRange(NativeApiMethodScanner.Scan(functionInstructions.OrderBy(value=>value.IP).ToArray(),image.Base,pathRvas,family.Entry));
            }
        var entries=seeds.Select(seed=>
        {
            knownCalls.TryGetValue(seed.Rva,out var callers);
            return new InternalNetworkEntry(seed.Name,Hex(seed.Rva),seed.Adaptation,callers?.Count??0,
                callers?.Distinct().Order().Select(Hex).ToArray()??[],ReadOnlyPointerReferences(image,image.Base+seed.Rva));
        }).ToList();
        var roots=calls.GroupBy(call=>call.FunctionRva).Select(group=>new{rva=Convert.ToUInt32(group.Key[2..],16),apis=group.Select(call=>call.Api).Distinct().Order().ToArray()})
            .Concat(seeds.Select(seed=>new{rva=FindFunction(ranges,seed.Rva)?.Begin??seed.Rva,apis=new[]{"pinned-internal:"+seed.Name}}))
            .GroupBy(value=>value.rva).Select(group=>new{rva=group.Key,apis=group.SelectMany(value=>value.apis).Distinct().ToArray()});
        var networkFunctions=roots.Select(value=>
        {
            var parents=reverse.GetValueOrDefault(value.rva)??[];
            var grandparents=parents.SelectMany(parent=>reverse.GetValueOrDefault(parent)??[]).Distinct().Order().ToArray();
            return new NetworkFunction(Hex(value.rva),value.apis,parents.Order().Take(256).Select(Hex).ToArray(),
                grandparents.Take(256).Select(Hex).ToArray(),parents.Count>256||grandparents.Length>256);
        }).ToList();
        return new(relative,Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),bytes.Length,image.Pe.PEHeaders.CoffHeader.Machine.ToString(),
            image.Pe.HasMetadata,main,networkImports,calls,managedCalls,resolverCalls,endpoints,apiPaths.Distinct().ToList(),pathReferences,apiMethods,entries,networkFunctions,
            new(functions.Count,instructions,invalid,indirect,edges,"x64 native exception-table ranges only; indirect register dispatch counted but not resolved. x86 native imports/literals inventoried; x86 call disassembly not claimed."));
    }
    private static Symbol? ResolveImport(Image image,Dictionary<uint,Symbol> slots,in Instruction instruction,out string binding)
    {
        binding="iat-indirect";
        if(TryIatRva(in instruction,image.Base,out var iat)&&slots.TryGetValue(iat,out var direct))return direct;
        if(instruction.Op0Kind is not (OpKind.NearBranch64 or OpKind.NearBranch32))return null;
        var target=instruction.NearBranchTarget;
        if(target<image.Base||target-image.Base>uint.MaxValue||image.Offset((uint)(target-image.Base),6) is not { } offset)return null;
        var reader=new ByteArrayCodeReader(image.Bytes,offset,6);var decoder=Decoder.Create(64,reader);decoder.IP=target;decoder.Decode(out var thunk);
        if(thunk.Mnemonic==Mnemonic.Jmp&&TryIatRva(in thunk,image.Base,out iat)&&slots.TryGetValue(iat,out var resolved))
        {binding="direct-to-import-thunk";return resolved;}
        return null;
    }
    internal static bool TryIatRva(in Instruction instruction,ulong imageBase,out uint rva)
    {
        rva=0;
        if(instruction.Op0Kind!=OpKind.Memory||!instruction.IsIPRelativeMemoryOperand)return false;
        var address=instruction.IPRelativeMemoryAddress;
        if(address<imageBase||address-imageBase>uint.MaxValue)return false;
        rva=(uint)(address-imageBase);return true;
    }
    internal static FunctionRange? FindFunction(FunctionRange[] ranges,uint rva)
    {
        var low=0;var high=ranges.Length-1;
        while(low<=high)
        {var mid=low+(high-low)/2;var value=ranges[mid];if(rva<value.Begin)high=mid-1;else if(rva>=value.End)low=mid+1;else return value;}
        return null;
    }
    private static IEnumerable<FunctionFamily> FunctionFamilies(Image image,List<FunctionRange> functions)
    {
        // UNW_FLAG_CHAININFO links split x64 RUNTIME_FUNCTION fragments to their
        // real entry. It supplies no guessed code gap or external entry state.
        var parents=new Dictionary<uint,uint>();var table=image.Pe.PEHeaders.PEHeader!.ExceptionTableDirectory;
        var data=image.Data((uint)table.RelativeVirtualAddress,table.Size);
        for(var offset=0;offset+12<=data.Length;offset+=12)
        {
            var begin=BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);var unwind=BinaryPrimitives.ReadUInt32LittleEndian(data[(offset+8)..]);
            if(image.Offset(unwind,4) is not {} header)continue;
            var flags=image.Bytes[header]>>3;var count=image.Bytes[header+2];
            if((flags&4)==0)continue;var chain=unwind+(uint)((4+count*2+3)&~3);
            if(image.Offset(chain,12) is not {} position)continue;
            var parent=BinaryPrimitives.ReadUInt32LittleEndian(image.Bytes.AsSpan(position));
            if(parent!=begin)parents[begin]=parent;
        }
        uint Root(uint begin)
        {var seen=new HashSet<uint>();while(parents.TryGetValue(begin,out var parent)&&seen.Add(begin))begin=parent;return begin;}
        return functions.GroupBy(range=>Root(range.Begin)).Select(group=>new FunctionFamily(group.Key,group.OrderBy(range=>range.Begin).ToArray()));
    }
    private static List<FunctionRange> FunctionRanges(Image image)
    {
        var result=new List<FunctionRange>();if(!image.Is64||image.Pe.HasMetadata)return result;
        var table=image.Pe.PEHeaders.PEHeader!.ExceptionTableDirectory;
        if(table.Size==0)return result;
        var data=image.Data((uint)table.RelativeVirtualAddress,table.Size);
        for(var offset=0;offset+12<=data.Length;offset+=12)
        {
            var begin=BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);var end=BinaryPrimitives.ReadUInt32LittleEndian(data[(offset+4)..]);
            if(end<=begin||end-begin>1024*1024||!image.IsExecutable(begin)||image.Offset(begin,(int)(end-begin)) is null)continue;
            result.Add(new(begin,end));
        }
        return result.Distinct().OrderBy(range=>range.Begin).ToList();
    }
    private static IEnumerable<Symbol> ReadImports(Image image)
    {
        var result=new List<Symbol>();var header=image.Pe.PEHeaders.PEHeader!;
        ReadTable((uint)header.ImportTableDirectory.RelativeVirtualAddress,false);
        ReadTable((uint)header.DelayImportTableDirectory.RelativeVirtualAddress,true);
        return result;
        void ReadTable(uint rva,bool delay)
        {
            if(rva==0)return;
            var width=delay?32:20;
            for(var index=0;index<4096;index++)
            {
                var descriptor=image.Data(checked(rva+(uint)(index*width)),width);
                if(descriptor.IndexOfAnyExcept((byte)0)<0)return;
                uint U32(int offset)=>BinaryPrimitives.ReadUInt32LittleEndian(image.Data(checked(rva+(uint)(index*width+offset)),4));
                if(delay&&(U32(0)&1)==0)throw new InvalidDataException("Unsupported VA delay import descriptor.");
                var library=image.Ascii(U32(delay?4:12));var names=U32(delay?16:0);var iat=U32(delay?12:16);
                if(names==0)names=iat;
                var size=image.Is64?8:4;
                for(var item=0;item<65536;item++)
                {
                    var entry=image.Data(checked(names+(uint)(item*size)),size);
                    var value=image.Is64?BinaryPrimitives.ReadUInt64LittleEndian(entry):BinaryPrimitives.ReadUInt32LittleEndian(entry);
                    if(value==0)break;
                    var ordinalMask=image.Is64?0x8000000000000000UL:0x80000000UL;
                    var name=(value&ordinalMask)!=0?OrdinalResolver.Resolve(library,(ushort)value,image.Is64):image.Ascii(checked((uint)value+2));
                    result.Add(new(library,name,checked(iat+(uint)(item*size)),delay));
                    if(item==65535)throw new InvalidDataException("Unterminated import thunk.");
                }
            }
            throw new InvalidDataException("Unterminated import table.");
        }
    }
    private static int ReadOnlyPointerReferences(Image image,ulong target)
    {
        var count=0;
        foreach(var section in image.Pe.PEHeaders.SectionHeaders.Where(section=>(section.SectionCharacteristics&SectionCharacteristics.MemRead)!=0
            &&(section.SectionCharacteristics&(SectionCharacteristics.MemWrite|SectionCharacteristics.MemExecute))==0))
        {
            var start=section.PointerToRawData;var end=Math.Min(image.Bytes.Length,(long)start+section.SizeOfRawData);
            for(var offset=start;offset+8<=end;offset+=8)
                if(BinaryPrimitives.ReadUInt64LittleEndian(image.Bytes.AsSpan(offset,8))==target)count++;
        }
        return count;
    }
    internal static string Hex(uint value)=>"0x"+value.ToString("X");

    internal sealed class Image:IDisposable
    {
        internal byte[] Bytes {get;}
        internal PEReader Pe {get;}
        internal bool Is64=>Pe.PEHeaders.PEHeader?.Magic==PEMagic.PE32Plus;
        internal ulong Base=>Pe.PEHeaders.PEHeader!.ImageBase;
        internal Image(byte[] bytes){Bytes=bytes;Pe=new PEReader(new MemoryStream(bytes,false));if(Pe.PEHeaders.PEHeader is null)throw new BadImageFormatException();}
        internal int? Offset(uint rva,int size)
        {
            if(size<0)return null;
            foreach(var section in Pe.PEHeaders.SectionHeaders)
            {
                var delta=(long)rva-section.VirtualAddress;
                if(delta>=0&&delta+size<=section.SizeOfRawData&&section.PointerToRawData+delta+size<=Bytes.Length)
                    return checked(section.PointerToRawData+(int)delta);
            }
            return null;
        }
        internal ReadOnlySpan<byte> Data(uint rva,int size)=>Offset(rva,size) is { } offset?Bytes.AsSpan(offset,size):throw new InvalidDataException("PE RVA exceeds mapped file bytes.");
        internal string Ascii(uint rva)
        {
            var offset=Offset(rva,1)??throw new InvalidDataException("PE string RVA is unmapped.");
            var length=0;while(length<4096&&offset+length<Bytes.Length&&Bytes[offset+length]!=0)length++;
            if(length==4096||offset+length==Bytes.Length)throw new InvalidDataException("Unterminated PE string.");
            return Encoding.ASCII.GetString(Bytes,offset,length);
        }
        internal uint? FileOffsetToRva(int offset)
        {
            foreach(var section in Pe.PEHeaders.SectionHeaders)
                if(offset>=section.PointerToRawData&&(long)offset<section.PointerToRawData+(long)section.SizeOfRawData)
                    return checked((uint)(section.VirtualAddress+offset-section.PointerToRawData));
            return null;
        }
        internal bool IsExecutable(uint rva)=>Pe.PEHeaders.SectionHeaders.Any(section=>rva>=section.VirtualAddress&&(long)rva<section.VirtualAddress+(long)section.VirtualSize
            &&(section.SectionCharacteristics&SectionCharacteristics.MemExecute)!=0);
        public void Dispose()=>Pe.Dispose();
    }
}
