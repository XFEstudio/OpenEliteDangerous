using System.Buffers.Binary;

namespace XFEEliteDangerous.NativeFormat.Replication;

/// <summary>RPC17/18:983F00 reads target U64, system U64 and bool;
/// 9836A0/934750 read the correlated target and a compressed-count object list.
/// This fresh private world configures no additional instance definitions.
/// Generated StellarForge bodies are handled separately by the client.</summary>
public static class EdServerInstanceDefinitionMessageCodec
{
    public static (ulong Target,ulong System,bool Option) ReadRequest(ReadOnlySpan<byte> bytes,int bits,bool aligned)
    {
        if(!aligned||bits!=144||bytes.Length!=18||bytes[0]!=17||bytes[17]>1)
            throw new InvalidDataException("Invalid native instance definition request.");
        var target=BinaryPrimitives.ReadUInt64LittleEndian(bytes[1..]);var system=BinaryPrimitives.ReadUInt64LittleEndian(bytes[9..]);
        if(target==0||target>0xFFFF_FFFF_FFFF||system==0)throw new InvalidDataException("Invalid native instance definition identity.");
        return(target,system,bytes[17]!=0);
    }
    public static EdServerCallEnvelope CreateEmptyReply(ulong controller,ulong target)
    {
        if(target==0||target>0xFFFF_FFFF_FFFF)throw new ArgumentException("A native target reference is required.");
        Span<byte> bytes=stackalloc byte[10];bytes.Clear();bytes[0]=18;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[1..],target);
        return new(controller,false,77,bytes);
    }
}
