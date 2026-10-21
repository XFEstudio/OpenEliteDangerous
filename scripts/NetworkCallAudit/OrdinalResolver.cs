using System.Buffers.Binary;

internal static class OrdinalResolver
{
    private static readonly Dictionary<string,Dictionary<ushort,string>> Cache=new(StringComparer.OrdinalIgnoreCase);
    internal static string Resolve(string library,ushort ordinal,bool x64)
    {
        // Resolve ordinal names from the corresponding installed system PE export table;
        // do not LoadLibrary or execute GetProcAddress on the game or a system library.
        var key=(x64?"64:":"32:")+library;
        if(!Cache.TryGetValue(key,out var names))
        {
            names=[];Cache.Add(key,names);
            if(Path.GetFileName(library)!=library)return "#"+ordinal;
            var system=x64?Environment.SystemDirectory:Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"SysWOW64");
            var path=Path.Combine(system,library);
            if(File.Exists(path))
                try
                {
                    using var image=new PeNetworkAudit.Image(File.ReadAllBytes(path));
                    var directory=image.Pe.PEHeaders.PEHeader!.ExportTableDirectory;
                    if(directory.Size>=40)
                    {
                        var header=image.Data((uint)directory.RelativeVirtualAddress,40);
                        var basis=BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
                        var count=BinaryPrimitives.ReadUInt32LittleEndian(header[24..]);
                        var nameTable=BinaryPrimitives.ReadUInt32LittleEndian(header[32..]);
                        var ordinalTable=BinaryPrimitives.ReadUInt32LittleEndian(header[36..]);
                        if(count<=65536)
                            for(uint index=0;index<count;index++)
                            {
                                var number=basis+BinaryPrimitives.ReadUInt16LittleEndian(image.Data(ordinalTable+index*2,2));
                                var text=BinaryPrimitives.ReadUInt32LittleEndian(image.Data(nameTable+index*4,4));
                                if(number<=ushort.MaxValue)names[(ushort)number]=image.Ascii(text);
                            }
                    }
                }
                catch(Exception error) when(error is IOException or BadImageFormatException or InvalidDataException) {names.Clear();}
        }
        return names.TryGetValue(ordinal,out var name)?name+" [ordinal "+ordinal+"]":"#"+ordinal;
    }
}
