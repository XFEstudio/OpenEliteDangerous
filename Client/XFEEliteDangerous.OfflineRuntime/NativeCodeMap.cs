using System.Buffers.Binary;
using System.Reflection.PortableExecutable;

namespace XFEEliteDangerous.OfflineRuntime;

/// <summary>Maps native return addresses to the pinned image's actual unwind function ranges.</summary>
internal static class NativeCodeMap
{
    private sealed record Range(uint Begin,uint End,uint Family);
    private static Range[] ranges=[];
    private static nint image;
    private static int size;
    internal static void Initialize(string executable,nint module)
    {
        using var file=File.OpenRead(executable);using var pe=new PEReader(file);
        image=module;size=pe.PEHeaders.PEHeader!.SizeOfImage;
        var directory=pe.PEHeaders.PEHeader.ExceptionTableDirectory;
        var bytes=pe.GetSectionData(directory.RelativeVirtualAddress).GetContent(0,directory.Size).AsSpan();
        var result=new List<Range>();
        for(var index=0;index+12<=bytes.Length;index+=12)
        {
            var begin=BinaryPrimitives.ReadUInt32LittleEndian(bytes[index..]);
            var end=BinaryPrimitives.ReadUInt32LittleEndian(bytes[(index+4)..]);
            var unwind=BinaryPrimitives.ReadUInt32LittleEndian(bytes[(index+8)..]);
            if(begin>=end||end>size)continue;
            var family=begin;
            for(var depth=0;depth<16;depth++)
            {
                var header=pe.GetSectionData((int)unwind).GetContent(0,4).AsSpan();
                if((header[0]>>3&4)==0)break;
                var chained=pe.GetSectionData((int)unwind+4+((header[2]+1)&~1)*2).GetContent(0,12).AsSpan();
                family=BinaryPrimitives.ReadUInt32LittleEndian(chained);
                unwind=BinaryPrimitives.ReadUInt32LittleEndian(chained[8..]);
            }
            result.Add(new(begin,end,family));
        }
        ranges=result.OrderBy(value=>value.Begin).ToArray();
    }
    internal static string Rva(nint address)=>address>=image&&address<image+size?"0x"+(address-image).ToString("X"):"external";
    internal static Dictionary<string,string?> Locate(nint address)
    {
        var result=new Dictionary<string,string?> {["nativeReturnRva"]=Rva(address),["nativeImage"]="EliteDangerous64.exe"};
        if(address<image||address>=image+size)return result;
        var rva=(uint)(address-image);var low=0;var high=ranges.Length-1;
        while(low<=high)
        {
            var middle=(low+high)/2;var range=ranges[middle];
            if(rva<range.Begin)high=middle-1;
            else if(rva>=range.End)low=middle+1;
            else
            {
                result["nativeFunctionRva"]="0x"+range.Family.ToString("X");
                result["nativeFunctionOffset"]="0x"+(rva-range.Family).ToString("X");
                result["nativeSymbol"]="sub_"+range.Family.ToString("X8");
                break;
            }
        }
        return result;
    }
}
