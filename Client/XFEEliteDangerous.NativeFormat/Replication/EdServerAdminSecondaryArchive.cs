using System.Security.Cryptography;

namespace XFEEliteDangerous.NativeFormat.Replication;

/// <summary>
/// The pinned ServerAdmin secondary archive:12 fixed properties totalling448bits,
/// no optional mask,6 byte-string properties.6339D0 reads ordered fixed-bit runs
/// and length-delimited variable overrides;63B600 derives its9-bit index width.
/// This is per-peer control metadata, never a world or vessel state.
/// </summary>
public sealed class EdServerAdminSecondaryArchive:IDisposable
{
    public const int FixedBits=448,StringCount=6,IndexBits=9,MaximumStates=32;
    private readonly ulong reference;
    private readonly ushort version;
    private readonly byte[] fixedDefaults;
    private readonly bool defaultsVerified;
    private readonly byte defaultKnownStrings;
    private readonly Dictionary<ushort,State> states=[];
    private readonly Queue<ushort> order=[];
    private bool disposed;
    public int StateCount=>states.Count;
    public ushort LatestSequence{get;private set;}
    public EdServerAdminSecondaryArchive(ulong reference,ushort version,ReadOnlySpan<byte> verifiedFixedDefaults=default,byte verifiedEmptyStringMask=63)
    {
        if(reference is 0 or >0xFFFFFFFFFFFF||verifiedFixedDefaults.Length is not(0 or FixedBits/8)||verifiedEmptyStringMask>63)throw new ArgumentOutOfRangeException(nameof(reference));
        this.reference=reference;this.version=version;defaultsVerified=!verifiedFixedDefaults.IsEmpty;
        fixedDefaults=defaultsVerified?verifiedFixedDefaults.ToArray():new byte[FixedBits/8];
        defaultKnownStrings=verifiedEmptyStringMask;
    }
    public EdServerAdminSecondaryArchive Fork()
    {
        ObjectDisposedException.ThrowIf(disposed,this);var clone=new EdServerAdminSecondaryArchive(reference,version,defaultsVerified?fixedDefaults:[],defaultKnownStrings){LatestSequence=LatestSequence};
        foreach(var sequence in order){clone.states.Add(sequence,states[sequence].Copy());clone.order.Enqueue(sequence);}return clone;
    }
    public void Apply(EdServerObjectUpdate update)
    {
        ObjectDisposedException.ThrowIf(disposed,this);ArgumentNullException.ThrowIfNull(update);
        if(update.ObjectReference!=reference||!update.SecondaryArchive||update.ConstructionVersion!=version)
            throw new InvalidDataException("controller-secondary-schema-mismatch");
        var bytes=update.CopyPayload();byte[]? digest=null;State? candidate=null;
        try
        {
            digest=SHA256.HashData(bytes);
            states.TryGetValue(update.Sequence,out var prior);
            if(prior is not null&&CryptographicOperations.FixedTimeEquals(prior.Digest,digest))return;
            var p=0;var baseSequence=(ushort)Read(bytes,ref p,10,update.PayloadBits);
            if(baseSequence==0)candidate=new(){Fixed=fixedDefaults.ToArray(),KnownStrings=defaultKnownStrings};
            else if(states.TryGetValue(baseSequence,out var baseline))candidate=baseline.Copy();
            else throw new NotSupportedException("controller-secondary-base-unavailable");
            var previousEnd=-1;var seenVariables=new HashSet<int>();var suppliedFixedBits=0;
            while(p<update.PayloadBits)
            {
                var index=(int)Read(bytes,ref p,IndexBits,update.PayloadBits);
                if(index<FixedBits)
                {
                    var end=(int)Read(bytes,ref p,IndexBits,update.PayloadBits);
                    if(index<=previousEnd||end<=index||end>FixedBits)throw new InvalidDataException("controller-secondary-fixed-run");
                    for(var bit=index;bit<end;bit++)
                    {
                        var value=Read(bytes,ref p,1,update.PayloadBits);var mask=1<<(bit%8);
                        candidate.Fixed[bit/8]=(byte)((candidate.Fixed[bit/8]&~mask)|(value==0?0:mask));
                    }
                    previousEnd=end;
                    suppliedFixedBits+=end-index;
                }
                else
                {
                    var variable=index-FixedBits;
                    if(variable>=StringCount||!seenVariables.Add(variable))throw new InvalidDataException("controller-secondary-variable-index");
                    var payloadLength=(long)Read(bytes,ref p,32,update.PayloadBits);
                    if(payloadLength<32||payloadLength>update.PayloadBits-p)throw new InvalidDataException("controller-secondary-variable-length");
                    var end=p+(int)payloadLength;
                    var length=(long)Read(bytes,ref p,32,end);
                    if(length>1024||length*8!=end-p)throw new InvalidDataException("controller-secondary-string-length");
                    var value=new byte[(int)length];
                    for(var i=0;i<value.Length;i++)value[i]=(byte)Read(bytes,ref p,8,end);
                    CryptographicOperations.ZeroMemory(candidate.Strings[variable]);candidate.Strings[variable]=value;
                    candidate.KnownStrings|=(byte)(1<<variable);
                }
            }
            if(p!=update.PayloadBits)throw new InvalidDataException("controller-secondary-trailing-bits");
            if(baseSequence==0&&!defaultsVerified&&suppliedFixedBits!=FixedBits)
                throw new NotSupportedException("controller-secondary-defaults-unverified");
            if(candidate.KnownStrings!=63)throw new NotSupportedException("controller-secondary-string-defaults-unverified");
            if(prior is not null)
            {
                // UREP/CertREP can encode the same retained frame using different
                // baseline/override runs. Compare applied values, not wire bytes.
                if(!prior.Fixed.AsSpan().SequenceEqual(candidate.Fixed)||!Enumerable.Range(0,StringCount).All(index=>prior.Strings[index].AsSpan().SequenceEqual(candidate.Strings[index])))
                    throw new InvalidDataException("controller-secondary-changed-duplicate");
                return;
            }
            CryptographicOperations.ZeroMemory(candidate.Digest);candidate.Digest=digest;digest=null;
            states.Add(update.Sequence,candidate);order.Enqueue(update.Sequence);candidate=null;LatestSequence=update.Sequence;
            while(states.Count>MaximumStates){var sequence=order.Dequeue();states.Remove(sequence,out var expired);expired!.Dispose();}
        }
        finally{CryptographicOperations.ZeroMemory(bytes);if(digest is not null)CryptographicOperations.ZeroMemory(digest);candidate?.Dispose();}
    }
    public byte[] CopyFixedState(ushort sequence)
    {ObjectDisposedException.ThrowIf(disposed,this);return states[sequence].Fixed.ToArray();}
    public byte[] CopyString(ushort sequence,int index)
    {ObjectDisposedException.ThrowIf(disposed,this);if(index is <0 or >=StringCount)throw new ArgumentOutOfRangeException(nameof(index));return states[sequence].Strings[index].ToArray();}
    public void Dispose()
    {if(disposed)return;disposed=true;foreach(var state in states.Values)state.Dispose();states.Clear();order.Clear();CryptographicOperations.ZeroMemory(fixedDefaults);LatestSequence=0;}
    private static ulong Read(ReadOnlySpan<byte> bytes,ref int p,int count,int end)
    {
        if(count is <0 or >64||p<0||(long)p+count>end||(long)end>bytes.Length*8)throw new InvalidDataException("controller-secondary-truncated");
        ulong value=0;for(var i=0;i<count;i++,p++)value|=(ulong)(bytes[p/8]>>(p%8)&1)<<i;return value;
    }
    private sealed class State:IDisposable
    {
        internal byte[] Fixed=new byte[FixedBits/8],Digest=[];
        internal byte KnownStrings;
        internal byte[][] Strings=Enumerable.Range(0,StringCount).Select(_=>Array.Empty<byte>()).ToArray();
        internal State Copy()=>new(){Fixed=Fixed.ToArray(),Digest=Digest.ToArray(),KnownStrings=KnownStrings,Strings=Strings.Select(value=>value.ToArray()).ToArray()};
        public void Dispose(){CryptographicOperations.ZeroMemory(Fixed);CryptographicOperations.ZeroMemory(Digest);foreach(var value in Strings)CryptographicOperations.ZeroMemory(value);}
    }
}
