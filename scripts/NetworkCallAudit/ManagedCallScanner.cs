using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

internal static class ManagedCallScanner
{
    private static readonly Dictionary<ushort,OpCode> Codes=typeof(OpCodes).GetFields(BindingFlags.Public|BindingFlags.Static)
        .Where(field=>field.FieldType==typeof(OpCode)).Select(field=>(OpCode)field.GetValue(null)!).ToDictionary(code=>unchecked((ushort)code.Value));
    internal static List<ManagedNetworkCall> Scan(PEReader pe)
    {
        var results=new List<ManagedNetworkCall>();var metadata=pe.GetMetadataReader();
        foreach(var handle in metadata.MethodDefinitions)
        {
            var method=metadata.GetMethodDefinition(handle);if(method.RelativeVirtualAddress==0)continue;
            var il=pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes();if(il is null)continue;
            var owner=TypeName(metadata,method.GetDeclaringType())+"."+metadata.GetString(method.Name);
            for(var offset=0;offset<il.Length;)
            {
                var site=offset;ushort code=il[offset++];if(code==0xFE&&offset<il.Length)code=(ushort)(0xFE00|il[offset++]);
                if(!Codes.TryGetValue(code,out var op))throw new InvalidDataException("Unknown IL opcode.");
                var length=OperandLength(op.OperandType,il,offset);
                if(length<0||offset+length>il.Length)throw new InvalidDataException("Truncated IL operand.");
                if(op.OperandType==OperandType.InlineMethod)
                {
                    var token=BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(offset,4));var target=Resolve(metadata,MetadataTokens.EntityHandle(token));
                    if(target is { } call&&ApiClassification.Managed(call.Type) is { } category)
                        results.Add(new(call.Type+"."+call.Name,category,"IL_"+site.ToString("X4"),owner));
                }
                offset+=length;
            }
        }
        return results;
    }
    internal static int OperandLength(OperandType type,byte[] il,int offset)=>type switch
    {
        OperandType.InlineNone=>0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar=>1,
        OperandType.InlineVar=>2,
        OperandType.InlineI8 or OperandType.InlineR=>8,
        OperandType.InlineSwitch=>offset+4<=il.Length?checked(4+BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(offset,4))*4):-1,
        _=>4
    };
    private static (string Type,string Name)? Resolve(MetadataReader metadata,EntityHandle handle)=>handle.Kind switch
    {
        HandleKind.MethodSpecification=>Resolve(metadata,metadata.GetMethodSpecification((MethodSpecificationHandle)handle).Method),
        HandleKind.MemberReference=>Member(metadata,metadata.GetMemberReference((MemberReferenceHandle)handle)),
        HandleKind.MethodDefinition=>Definition(metadata,metadata.GetMethodDefinition((MethodDefinitionHandle)handle)),
        _=>null
    };
    private static (string Type,string Name) Member(MetadataReader metadata,MemberReference value)=>(TypeName(metadata,value.Parent),metadata.GetString(value.Name));
    private static (string Type,string Name) Definition(MetadataReader metadata,MethodDefinition value)=>(TypeName(metadata,value.GetDeclaringType()),metadata.GetString(value.Name));
    private static string TypeName(MetadataReader metadata,EntityHandle handle)
    {
        if(handle.Kind==HandleKind.TypeReference)
        {
            var type=metadata.GetTypeReference((TypeReferenceHandle)handle);var ns=metadata.GetString(type.Namespace);
            return ns.Length>0?ns+"."+metadata.GetString(type.Name):metadata.GetString(type.Name);
        }
        if(handle.Kind==HandleKind.TypeDefinition)
        {
            var type=metadata.GetTypeDefinition((TypeDefinitionHandle)handle);var ns=metadata.GetString(type.Namespace);
            return ns.Length>0?ns+"."+metadata.GetString(type.Name):metadata.GetString(type.Name);
        }
        return "<"+handle.Kind+">";
    }
}
