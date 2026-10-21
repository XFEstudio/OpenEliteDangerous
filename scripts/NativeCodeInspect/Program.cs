using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using Iced.Intel;

// Read-only static disassembly of the exact authorized E file. No process APIs,
// native code execution, sockets, UI, credentials, patching or D-drive access.
if(args.Length!=1||!args[0].Contains('='))return 78;
var parts=args[0].Split('=',2);
uint target=0;
if(parts[0] is "--mission-labels" or "--startup-labels" or "--offline-labels")
{if(parts[1]!="static")return 78;}
else if(parts[0]=="--json-errors")
{if(parts[1]!="static")return 78;}
else if(parts[0]=="--admin-signals")
{if(parts[1] is not("static" or "sync-complete" or "machine-query"))return 78;target=parts[1]=="static"?0x78u:parts[1]=="sync-complete"?0x118u:0xF8u;}
else if(parts[0]=="--literal")
{if(parts[1].Length is <3 or >160||parts[1].Any(character=>character is <' ' or >'~'))return 78;}
else if(parts[0] is not ("--function" or "--family" or "--block" or "--data" or "--refs" or "--rip-refs" or "--direct-refs" or "--string" or "--prefix" or "--rtti" or "--type-getter" or "--replicator-field" or "--component-field" or "--loadout-field" or "--reference-mapper-writes" or "--mission-callers" or "--serverlink-query-poll" or "--network-provider-config" or "--leaf-virtual-sites" or "--game-data-field" or "--control-field" or "--control-accessor-sites")||!uint.TryParse(parts[1].StartsWith("0x",StringComparison.OrdinalIgnoreCase)?parts[1][2..]:parts[1],System.Globalization.NumberStyles.HexNumber,null,out target))return 78;
if(parts[0]=="--replicator-field"&&target is <0x200 or >0x400)return 78;
if(parts[0]=="--component-field"&&target is <0x58 or >0x400)return 78;
if(parts[0]=="--loadout-field"&&target is <0x28 or >0x1B0)return 78;
if(parts[0]=="--reference-mapper-writes"&&target!=0x88)return 78;
if(parts[0]=="--mission-callers"&&target!=0x98)return 78;
if(parts[0]=="--serverlink-query-poll"&&target!=0x60)return 78;
if(parts[0]=="--network-provider-config"&&target is not(0x58 or 0xB0))return 78;
if(parts[0]=="--leaf-virtual-sites"&&target is not(0xF8 or 0x78))return 78;
if(parts[0]=="--control-accessor-sites"&&target is not(0xC0 or 0xC8))return 78;
var file=File.ReadAllBytes(@"E:\Elite Dangerous\Products\elite-dangerous-odyssey-64\EliteDangerous64.exe");
if(Convert.ToHexStringLower(SHA256.HashData(file))!="e6be8bbe04e6a7ae226d4318945af7f367de13dc5a007a261964d9ba8144e988")return 78;
using var stream=new MemoryStream(file,false);using var pe=new PEReader(stream);
var image=pe.PEHeaders.PEHeader!.ImageBase;
var table=pe.GetSectionData(pe.PEHeaders.PEHeader.ExceptionTableDirectory.RelativeVirtualAddress).GetContent(0,pe.PEHeaders.PEHeader.ExceptionTableDirectory.Size).ToArray();
if(parts[0]=="--json-errors")
{
    const uint begin=0x5A68700;var bytes=pe.GetSectionData((int)begin).GetContent(0,0x600).AsSpan();var offset=0;
    while(offset<bytes.Length)
    {
        while(offset<bytes.Length&&bytes[offset] is not(>=32 and <127))offset++;
        var end=offset;while(end<bytes.Length&&bytes[end] is >=32 and <127)end++;
        if(end-offset>=4)Console.WriteLine($"{begin+offset:X8}: "+System.Text.Json.JsonSerializer.Serialize(System.Text.Encoding.ASCII.GetString(bytes[offset..end])));
        offset=end+1;
    }
    return 0;
}
if(parts[0] is "--mission-labels" or "--startup-labels" or "--offline-labels")
{
    foreach(var section in pe.PEHeaders.SectionHeaders.Where(value=>value.Name==".rdata"))
    {
        var bytes=file.AsSpan(section.PointerToRawData,section.SizeOfRawData);var start=0;
        while(start<bytes.Length)
        {
            while(start<bytes.Length&&bytes[start] is not(>=32 and <127))start++;
            var end=start;while(end<bytes.Length&&bytes[end] is >=32 and <127)end++;
            if(end-start is >=5 and <=160)
            {
                var label=System.Text.Encoding.ASCII.GetString(bytes[start..end]);
                if(parts[0]=="--offline-labels"?new[]{"offline","singleplayer","standalone","training","nonetwork","noserver","localserver","serverless","solo","authority","unreplicated","localonly","isserver","netmode","listenserver","servermode"}.Any(term=>label.Contains(term,StringComparison.OrdinalIgnoreCase))
                    :parts[0]=="--mission-labels"?label.Contains("mission",StringComparison.OrdinalIgnoreCase)
                    :label.Contains("warning",StringComparison.OrdinalIgnoreCase)||label.Contains("shader",StringComparison.OrdinalIgnoreCase))
                    Console.WriteLine($"{section.VirtualAddress+start:X8}: "+System.Text.Json.JsonSerializer.Serialize(label));
            }
            start=end+1;
        }
    }
    return 0;
}
if(parts[0]=="--rtti")
{
    if(target<8)return 78;
    var locator=BinaryPrimitives.ReadUInt64LittleEndian(pe.GetSectionData((int)target-8).GetContent(0,8).AsSpan());
    if(locator<image||locator-image>=(uint)pe.PEHeaders.PEHeader.SizeOfImage)return 78;
    var locatorRva=(uint)(locator-image);var col=pe.GetSectionData((int)locatorRva).GetContent(0,24).AsSpan();
    if(BinaryPrimitives.ReadUInt32LittleEndian(col)!=1||BinaryPrimitives.ReadUInt32LittleEndian(col[20..])!=locatorRva)return 78;
    var typeRva=BinaryPrimitives.ReadUInt32LittleEndian(col[12..]);
    if(typeRva>=(uint)pe.PEHeaders.PEHeader.SizeOfImage-16)return 78;
    var typeBytes=pe.GetSectionData((int)typeRva+16);var name=typeBytes.GetContent(0,Math.Min(2048,typeBytes.Length)).AsSpan();var end=name.IndexOf((byte)0);
    if(end is <4 or >=2048||!name[..end].ToArray().All(value=>value is >=32 and <127))return 78;
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new{vtableRva=target,typeDescriptorRva=typeRva,type=System.Text.Encoding.ASCII.GetString(name[..end])}));return 0;
}
if(parts[0]=="--prefix")
{
    Console.WriteLine(Convert.ToHexStringLower(pe.GetSectionData(checked((int)target)).GetContent(0,32).AsSpan()));return 0;
}
if(parts[0]=="--literal")
{
    var needle=System.Text.Encoding.ASCII.GetBytes(parts[1]+"\0");var cursor=0;
    while(cursor<file.Length)
    {
        var found=file.AsSpan(cursor).IndexOf(needle);if(found<0)break;cursor+=found;
        var section=pe.PEHeaders.SectionHeaders.FirstOrDefault(section=>cursor>=section.PointerToRawData&&cursor<section.PointerToRawData+section.SizeOfRawData);
        if(!string.IsNullOrEmpty(section.Name))Console.WriteLine($"{section.VirtualAddress+cursor-section.PointerToRawData:X8}: "+System.Text.Json.JsonSerializer.Serialize(parts[1]));
        cursor+=needle.Length;
    }
    return 0;
}
if(parts[0]=="--string")
{
    var section=pe.GetSectionData(checked((int)target));
    var bytes=section.GetContent(0,Math.Min(192,section.Length)).AsSpan();var end=bytes.IndexOf((byte)0);
    if(end is <1 or >160||!bytes[..end].ToArray().All(value=>value is >=32 and <127))return 78;
    Console.WriteLine($"{target:X8}: "+System.Text.Json.JsonSerializer.Serialize(System.Text.Encoding.ASCII.GetString(bytes[..end])));
    return 0;
}
if(parts[0]=="--data")
{
    var data=pe.GetSectionData((int)target).GetContent(0,256).ToArray();
    for(var offset=0;offset<data.Length;offset+=8)Console.WriteLine($"{target+offset:X8} {BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset)):X16}");
    return 0;
}
var formatter=new IntelFormatter();var output=new StringOutput();
if(parts[0]=="--control-field")
{
    if(target is not(0x680 or 0x614 or 0x654 or 0x5D0 or 0x2F8))return 78;
    var controlBegin=target==0x614?0x3380000:target is 0x654 or 0x5D0?0x2E00000:target==0x2F8?0x2C50000:0x2500000;
    var controlSize=target==0x614?0x80000:target is 0x654 or 0x5D0?0x100000:target==0x2F8?0x70000:0x200000;
    var section=pe.GetSectionData(controlBegin);var decoder=Iced.Intel.Decoder.Create(64,new ByteArrayCodeReader(section.GetContent(0,controlSize).ToArray()));decoder.IP=image+(uint)controlBegin;
    while(decoder.IP<image+(uint)(controlBegin+controlSize))
    {
        var instruction=decoder.Decode();
        if(instruction.IsInvalid||instruction.IsIPRelativeMemoryOperand||(instruction.MemoryDisplacement64!=target
                &&(target!=0x654||instruction.MemoryDisplacement64!=target+0x58)
                &&(target!=0x5D0||instruction.MemoryDisplacement64!=target-0x58))
            ||instruction.MemoryBase is Register.RSP or Register.RBP
            ||!Enumerable.Range(0,instruction.OpCount).Any(index=>instruction.GetOpKind(index)==OpKind.Memory))continue;
        output.Reset();formatter.Format(instruction,output);Console.WriteLine($"candidate={instruction.IP-image:X8}: {output}");
    }
    return 0;
}
if(parts[0]=="--game-data-field")
{
    if(target is not(0x310 or 0x368))return 78;
    var section=pe.GetSectionData(0x2500000);var decoder=Iced.Intel.Decoder.Create(64,new ByteArrayCodeReader(section.GetContent(0,0x200000).ToArray()));decoder.IP=image+0x2500000;
    while(decoder.IP<image+0x2700000)
    {
        var instruction=decoder.Decode();
        if(instruction.IsInvalid||instruction.Op0Kind!=OpKind.Memory||instruction.MemoryDisplacement64!=target||instruction.Mnemonic is Mnemonic.Cmp or Mnemonic.Test)continue;
        output.Reset();formatter.Format(instruction,output);Console.WriteLine($"candidate={instruction.IP-image:X8}: {output}");
    }
    return 0;
}
if(parts[0]=="--leaf-virtual-sites")
{
    var ranges=new List<(uint Begin,uint End)>();
    for(var i=0;i+12<=table.Length;i+=12)ranges.Add((BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(i)),BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(i+4))));
    var ordered=ranges.OrderBy(value=>value.Begin).ToArray();
    bool HasUnwind(uint rva)
    {
        var lo=0;var hi=ordered.Length-1;
        while(lo<=hi){var mid=(lo+hi)/2;var row=ordered[mid];if(rva<row.Begin)hi=mid-1;else if(rva>=row.End)lo=mid+1;else return true;}
        return false;
    }
    foreach(var section in pe.PEHeaders.SectionHeaders.Where(value=>(value.SectionCharacteristics&SectionCharacteristics.MemExecute)!=0))
    {
        var bytes=file.AsSpan(section.PointerToRawData,section.SizeOfRawData);
        for(var offset=0;offset+7<=bytes.Length;offset++)
        {
            // Leaf thunks need no unwind metadata and were absent from the .pdata scan.
            if(bytes[offset]!=0xFF)continue;
            var rva=(uint)(section.VirtualAddress+offset);if(HasUnwind(rva))continue;
            var decoder=Iced.Intel.Decoder.Create(64,new ByteArrayCodeReader(bytes.Slice(offset,Math.Min(15,bytes.Length-offset)).ToArray()));decoder.IP=image+rva;
            var instruction=decoder.Decode();
            if(instruction.IsInvalid||instruction.FlowControl is not(FlowControl.IndirectCall or FlowControl.IndirectBranch)
                ||instruction.Op0Kind!=OpKind.Memory||instruction.IsIPRelativeMemoryOperand||instruction.MemoryDisplacement64!=target)continue;
            output.Reset();formatter.Format(instruction,output);Console.WriteLine($"candidate-without-unwind={rva:X8}: {output}");
        }
    }
    return 0;
}
if(parts[0]=="--type-getter")
{
    // Exact native leaf pattern: mov eax,[rip+typeId]; mov [rdx],eax;
    // mov rax,rdx; ret. A candidate is not yet a concrete service binding.
    foreach(var section in pe.PEHeaders.SectionHeaders.Where(value=>(value.SectionCharacteristics&SectionCharacteristics.MemExecute)!=0))
    {
        var bytes=file.AsSpan(section.PointerToRawData,section.SizeOfRawData);
        for(var offset=0;offset+12<=bytes.Length;offset++)
        {
            if(bytes[offset]!=0x8B||bytes[offset+1]!=0x05
                ||(long)section.VirtualAddress+offset+6+BinaryPrimitives.ReadInt32LittleEndian(bytes[(offset+2)..])!=target
                ||!bytes.Slice(offset+6,6).SequenceEqual(new byte[]{0x89,0x02,0x48,0x8B,0xC2,0xC3}))continue;
            Console.WriteLine($"typeId={target:X8} getter={section.VirtualAddress+offset:X8}");
        }
    }
    return 0;
}
if(parts[0]=="--rip-refs")
{
    // Static candidates include leaf/global initializers absent from .pdata.
    // A RIP displacement may precede up to eight immediate bytes. Validate each
    // candidate with Iced; callers must still inspect its instruction boundary.
    var emitted=new HashSet<ulong>();
    foreach(var section in pe.PEHeaders.SectionHeaders.Where(value=>(value.SectionCharacteristics&System.Reflection.PortableExecutable.SectionCharacteristics.MemExecute)!=0))
    {
        var bytes=file.AsSpan(section.PointerToRawData,section.SizeOfRawData);
        for(var index=0;index+4<=bytes.Length;index++)
        {
            var afterDisplacement=(long)section.VirtualAddress+index+4;
            var trailing=(long)target-afterDisplacement-BinaryPrimitives.ReadInt32LittleEndian(bytes[index..]);
            if(trailing is <0 or >8)continue;
            for(var start=Math.Max(0,index-11);start<index;start++)
            {
                var decoder=Iced.Intel.Decoder.Create(64,new ByteArrayCodeReader(bytes.Slice(start,Math.Min(15,bytes.Length-start)).ToArray()));
                decoder.IP=image+(uint)(section.VirtualAddress+start);var instruction=decoder.Decode();
                if(instruction.IsInvalid||!instruction.IsIPRelativeMemoryOperand||instruction.IPRelativeMemoryAddress!=image+target||!emitted.Add(instruction.IP))continue;
                output.Reset();formatter.Format(instruction,output);Console.WriteLine($"candidate={instruction.IP-image:X8}: {output}");
            }
        }
    }
    return 0;
}
if(parts[0]=="--direct-refs")
{
    foreach(var section in pe.PEHeaders.SectionHeaders.Where(value=>(value.SectionCharacteristics&System.Reflection.PortableExecutable.SectionCharacteristics.MemExecute)!=0))
    {
        var bytes=file.AsSpan(section.PointerToRawData,section.SizeOfRawData);
        for(var index=0;index+5<=bytes.Length;index++)
        {
            if(bytes[index] is not(0xE8 or 0xE9))continue;
            var rva=(uint)(section.VirtualAddress+index);
            if((long)rva+5+BinaryPrimitives.ReadInt32LittleEndian(bytes[(index+1)..])!=target)continue;
            var decoder=Iced.Intel.Decoder.Create(64,new ByteArrayCodeReader(bytes.Slice(index,5).ToArray()));decoder.IP=image+rva;
            var instruction=decoder.Decode();if(instruction.IsInvalid||instruction.Length!=5||instruction.NearBranchTarget!=image+target)continue;
            output.Reset();formatter.Format(instruction,output);Console.WriteLine($"{rva:X8}: {output}");
        }
    }
    return 0;
}
if(parts[0]=="--family")
{
    var ranges=new List<(uint Begin,uint End,uint Unwind)>();
    for(var index=0;index+12<=table.Length;index+=12)
        ranges.Add((BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(index)),BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(index+4)),BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(index+8))));
    var byBegin=ranges.ToDictionary(value=>value.Begin);var containing=ranges.SingleOrDefault(value=>target>=value.Begin&&target<value.End);
    if(containing.End==0)return 78;
    uint Root(uint begin)
    {
        var seen=new HashSet<uint>();
        while(byBegin.TryGetValue(begin,out var row)&&seen.Add(begin))
        {
            var header=pe.GetSectionData((int)row.Unwind).GetContent(0,4);
            if((header[0]>>3&4)==0)break;
            var chain=row.Unwind+(uint)((4+header[2]*2+3)&~3);
            begin=BinaryPrimitives.ReadUInt32LittleEndian(pe.GetSectionData((int)chain).GetContent(0,4).AsSpan());
        }
        return begin;
    }
    var root=Root(containing.Begin);Console.WriteLine($"family-entry={root:X8}");
    foreach(var range in ranges.Where(value=>Root(value.Begin)==root).OrderBy(value=>value.Begin))
    {
        Console.WriteLine($"fragment={range.Begin:X8}..{range.End:X8}");
        var bytes=pe.GetSectionData((int)range.Begin).GetContent(0,checked((int)(range.End-range.Begin))).ToArray();var reader=new ByteArrayCodeReader(bytes);
        var decoder=Iced.Intel.Decoder.Create(64,reader);decoder.IP=image+range.Begin;
        while(reader.CanReadByte){var instruction=decoder.Decode();if(instruction.IsInvalid||instruction.NextIP>image+range.End)break;
            output.Reset();formatter.Format(instruction,output);Console.WriteLine($"{instruction.IP-image:X8}: {output}{Literal(in instruction)}");}
    }
    return 0;
}
if(parts[0]=="--block")
{
    var body=pe.GetSectionData((int)target).GetContent(0,0x600).ToArray();var reader=new ByteArrayCodeReader(body);var decoder=Iced.Intel.Decoder.Create(64,reader);decoder.IP=image+target;
    while(reader.CanReadByte){var instruction=decoder.Decode();output.Reset();formatter.Format(instruction,output);Console.WriteLine($"{instruction.IP-image:X8}: {output}{Literal(in instruction)}");}return 0;
}
for(var offset=0;offset+12<=table.Length;offset+=12)
{
    var begin=BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(offset));var end=BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(offset+4));
    if(parts[0]=="--replicator-field"&&(begin<0x620000||begin>=0x670000))continue;
    if(parts[0]=="--component-field"&&(begin<0x960000||begin>=0x990000))continue;
    if(parts[0]=="--loadout-field"&&(begin<0x4380000||begin>=0x43D0000))continue;
    if(parts[0]=="--admin-signals"&&(target==0xF8?(begin<0x1000||begin>=0x4D00000):(begin<0x1D00000||begin>=0x2200000)))continue;
    if(parts[0]=="--mission-callers"&&(begin<0x1D00000||begin>=0x4D00000))continue;
    //The engine's networking consumer lives in the4C... range as well as game
    //services below45.... Excluding that range loses the actual peer-result
    //reader even though its ServerLink producer was already found.
    if(parts[0]=="--serverlink-query-poll"&&(begin<0x1000||begin>=0x4D00000))continue;
    if(parts[0]=="--network-provider-config"&&(begin<0x8C8000||begin>=0x8E0000))continue;
    if(parts[0]=="--control-accessor-sites"&&(begin<0x2500000||begin>=0x3500000))continue;
    if(begin>=end||end-begin>0x20000||parts[0]=="--function"&&(target<begin||target>=end))continue;
    var body=pe.GetSectionData((int)begin).GetContent(0,(int)(end-begin)).ToArray();var reader=new ByteArrayCodeReader(body);var decoder=Iced.Intel.Decoder.Create(64,reader);decoder.IP=image+begin;
    var context=new Queue<string>();
    while(reader.CanReadByte)
    {
        var instruction=decoder.Decode();if(instruction.IsInvalid||instruction.NextIP>image+end)break;
        if(parts[0]=="--control-accessor-sites")
        {
            if(instruction.MemoryDisplacement64==target&&!instruction.IsIPRelativeMemoryOperand
                &&instruction.Op0Kind==OpKind.Memory&&instruction.FlowControl is FlowControl.IndirectCall or FlowControl.IndirectBranch)
            {foreach(var line in context)Console.WriteLine(line);output.Reset();formatter.Format(instruction,output);Console.WriteLine($"caller={begin:X8} {instruction.IP-image:X8}: {output}");}
            output.Reset();formatter.Format(instruction,output);context.Enqueue($"{instruction.IP-image:X8}: {output}");if(context.Count>7)context.Dequeue();
        }
        else if(parts[0]=="--admin-signals")
        {
            if(instruction.MemoryDisplacement64==target&&!instruction.IsIPRelativeMemoryOperand
                &&(instruction.Mnemonic is Mnemonic.Call or Mnemonic.Jmp&&instruction.Op0Kind==OpKind.Memory
                    ||target==0xF8&&instruction.Mnemonic==Mnemonic.Mov&&instruction.Op0Kind==OpKind.Register&&instruction.Op1Kind==OpKind.Memory))
            {foreach(var line in context)Console.WriteLine(line);output.Reset();formatter.Format(instruction,output);Console.WriteLine($"caller={begin:X8} {instruction.IP-image:X8}: {output}");}
            output.Reset();formatter.Format(instruction,output);context.Enqueue($"{instruction.IP-image:X8}: {output}");if(context.Count>8)context.Dequeue();
        }
        else if(parts[0]=="--mission-callers")
        {
            if(instruction.Mnemonic is Mnemonic.Call or Mnemonic.Jmp&&instruction.Op0Kind==OpKind.Memory&&instruction.MemoryDisplacement64==0x98
                &&context.Any(line=>line.Contains("+88h]",StringComparison.Ordinal)))
            {foreach(var line in context)Console.WriteLine(line);output.Reset();formatter.Format(instruction,output);Console.WriteLine($"caller={begin:X8} {instruction.IP-image:X8}: {output}");}
            output.Reset();formatter.Format(instruction,output);context.Enqueue($"{instruction.IP-image:X8}: {output}");if(context.Count>32)context.Dequeue();
        }
        else if(parts[0]=="--serverlink-query-poll")
        {
            if(instruction.MemoryBase is not(Register.None or Register.RSP)&&!instruction.IsIPRelativeMemoryOperand
                &&instruction.MemoryDisplacement64==0x60&&instruction.MemorySize==MemorySize.UInt8)
            {foreach(var line in context)Console.WriteLine(line);output.Reset();formatter.Format(instruction,output);Console.WriteLine($"caller={begin:X8} {instruction.IP-image:X8}: {output}");}
            output.Reset();formatter.Format(instruction,output);context.Enqueue($"{instruction.IP-image:X8}: {output}");if(context.Count>6)context.Dequeue();
        }
        else if(parts[0]=="--reference-mapper-writes")
        {
            if(instruction.Mnemonic==Mnemonic.Mov&&instruction.Op0Kind==OpKind.Memory&&instruction.MemoryDisplacement64==0x88
                &&instruction.MemoryBase is not(Register.None or Register.RSP or Register.RBP))
            {output.Reset();formatter.Format(instruction,output);Console.WriteLine($"caller={begin:X8} {instruction.IP-image:X8}: {output}");}
        }
        else if(parts[0] is "--replicator-field" or "--component-field" or "--loadout-field" or "--network-provider-config")
        {
            if(instruction.MemoryBase!=Register.None&&!instruction.IsIPRelativeMemoryOperand&&instruction.MemoryDisplacement64==target)
            {output.Reset();formatter.Format(instruction,output);Console.WriteLine($"caller={begin:X8} {instruction.IP-image:X8}: {output}");}
        }
        else if(parts[0]=="--refs")
        {
            if(instruction.IsIPRelativeMemoryOperand&&instruction.IPRelativeMemoryAddress==image+target
                ||instruction.FlowControl is FlowControl.Call or FlowControl.UnconditionalBranch&&instruction.NearBranchTarget==image+target)
            {output.Reset();formatter.Format(instruction,output);Console.WriteLine($"caller={begin:X8} {instruction.IP-image:X8}: {output}");}
            else if(instruction.Mnemonic==Mnemonic.Lea&&!instruction.IsIPRelativeMemoryOperand
                &&instruction.MemoryBase!=Register.None&&instruction.MemoryDisplacement64==target)
            {output.Reset();formatter.Format(instruction,output);Console.WriteLine($"base-relative-candidate={begin:X8} {instruction.IP-image:X8}: {output}");}
        }
        else{output.Reset();formatter.Format(instruction,output);Console.WriteLine($"{instruction.IP-image:X8}: {output}{Literal(in instruction)}");}
    }
    if(parts[0]=="--function")return 0;
}
if(parts[0]=="--refs")
{
    var needle=BitConverter.GetBytes(image+target);var start=0;
    while(start<file.Length){var found=file.AsSpan(start).IndexOf(needle);if(found<0)break;start+=found;var section=pe.PEHeaders.SectionHeaders.FirstOrDefault(section=>start>=section.PointerToRawData&&start<section.PointerToRawData+section.SizeOfRawData);Console.WriteLine($"pointer-data={section.VirtualAddress+start-section.PointerToRawData:X8}");start+=8;}
}
return 0;

string Literal(in Instruction instruction)
{
    if(instruction.Mnemonic is not(Mnemonic.Lea or Mnemonic.Mov)||!instruction.IsIPRelativeMemoryOperand||instruction.IPRelativeMemoryAddress<image
        ||instruction.IPRelativeMemoryAddress-image>int.MaxValue)return "";
    try
    {
        var section=pe.GetSectionData((int)(instruction.IPRelativeMemoryAddress-image));var bytes=section.GetContent(0,Math.Min(192,section.Length)).AsSpan();
        var end=bytes.IndexOf((byte)0);
        if(end is >=4 and <=160&&bytes[..end].ToArray().All(value=>value is >=32 and <127))
            return " ; "+System.Text.Json.JsonSerializer.Serialize(System.Text.Encoding.ASCII.GetString(bytes[..end]));
        var count=0;while(count+1<bytes.Length&&bytes[count] is >=32 and <127&&bytes[count+1]==0)count+=2;
        if(count>=8&&count+1<bytes.Length&&bytes[count]==0&&bytes[count+1]==0)
            return " ; "+System.Text.Json.JsonSerializer.Serialize(System.Text.Encoding.Unicode.GetString(bytes[..count]));
    }
    catch(ArgumentOutOfRangeException){}
    return "";
}
