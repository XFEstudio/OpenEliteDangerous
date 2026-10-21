namespace XFEEliteDangerous.Core;

/// <summary>Explicit private storage and transfer metadata, separate from installed modules and ship cargo.</summary>
public sealed record NativeStoredModule
{
    public required uint SlotId{get;init;}
    public required string SystemName{get;init;}
    public required ulong MarketId{get;init;}
    public required bool InTransit{get;init;}
    public required ulong TransferTime{get;init;}
    public required long Value{get;init;}
    public required NativeModuleSnapshot Module{get;init;}
    internal object Public()=>new{slot_id=SlotId,system_name=SystemName,marketId=MarketId,inTransit=InTransit,transferTime=TransferTime,value=Value,
        module=NativeSavegameDocument.ModuleDocument(Module)};
    internal static void Validate(IReadOnlyList<NativeStoredModule>? entries,NativeSavegameCatalogue catalogue)
    {
        if(entries is null||entries.Count>1024||entries.Any(value=>value is null)||entries.Select(value=>value.SlotId).Distinct().Count()!=entries.Count)
            throw new ArgumentException("Invalid native stored-module entries.");
        foreach(var entry in entries)
        {
            if(string.IsNullOrWhiteSpace(entry.SystemName)||entry.SystemName.Length>200||entry.SystemName.Any(char.IsControl)||entry.Value<0
                ||entry.Module is not {} module||module.Priority>4||module.HealthMillion>1_000_000||module.Id==0
                ||!catalogue.Modules.Any(definition=>definition.Id==module.Id&&definition.Name==module.Name))
                throw new ArgumentException("Invalid native stored-module metadata or resource identity.");
            if(module.Default is {} original&&!catalogue.Modules.Any(definition=>definition.Id==original.Id&&definition.Name==original.Name))
                throw new ArgumentException("Invalid native stored-module default resource.");
        }
    }
}
