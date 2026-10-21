namespace XFEEliteDangerous.Core;

/// <summary>Persisted explicit native state. The current private inventory supplies editable fields.</summary>
public sealed record NativeCommanderSave
{
    public required NativeSavegameSnapshot Snapshot { get; init; }
    public required NativeSavegameCatalogue Catalogue { get; init; }
    public required NativeFullSavegameProfile Profile { get; init; }
    public NativeSystemMetadata? SystemMetadata {get;init;}
    public NativeSavegameUploadState? UploadState {get;init;}
    public IReadOnlyList<NativeStoredModule> StoredModules {get;init;}=[];
    private readonly IReadOnlyList<NativeMaterialStack> materials=[];
    private readonly IReadOnlyDictionary<uint,NativeShipLabel> shipLabels=new Dictionary<uint,NativeShipLabel>();
    // The source-generated required-member constructor passes null for new
    // optional properties absent from older schema-1 files.
    public IReadOnlyList<NativeMaterialStack> Materials {get=>materials;init=>materials=value??[];}
    public IReadOnlyDictionary<uint,NativeShipLabel> ShipLabels {get=>shipLabels;init=>shipLabels=value??new Dictionary<uint,NativeShipLabel>();}

    public CommanderState CreateInventory(AccountState account, long revision)
    {
        _ = NativeFullSavegameDocument.SerializeComplete(Snapshot, Catalogue, Profile);
        SystemMetadata?.Validate(Profile.CurrentSystem);
        NativeSavegameUpload.Validate(this);
        NativeStoredModule.Validate(StoredModules,Catalogue);
        NativeMaterials.Validate(Materials);
        if (Catalogue.PinnedVersion != "2026.09.03.332841")
            throw new ArgumentException("Native save targets an unsupported game build.");
        if ((Snapshot.Location.Address & 0x007F_FFFF_FFFF_FFFFUL) != Profile.CurrentSystem
            || (Snapshot.Vessels.Single(ship => ship.Id == Snapshot.CurrentVesselId).Location.Address & 0x007F_FFFF_FFFF_FFFFUL) != Profile.CurrentSystem)
            throw new ArgumentException("Current native vessel and commander must reference the declared current system.");
        var inventory=NativeCommanderProjection.CreateInventory(Snapshot, Catalogue, account.NativeUserId,
            account.Commander.Id, Profile.Name, revision);
        inventory.Materials=Materials.Where(row=>row.Quantity>0).Select(row=>new InventoryStack{Id=row.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),Name=row.Name,Category=row.Type,Quantity=row.Quantity}).ToList();
        if(ShipLabels is null||ShipLabels.Any(pair=>!Snapshot.Vessels.Any(ship=>ship.Id==pair.Key)||pair.Value is null
            ||string.IsNullOrWhiteSpace(pair.Value.Name)||pair.Value.Ident is null||pair.Value.Name.Length>100||pair.Value.Ident.Length>24||pair.Value.Name.Any(char.IsControl)||pair.Value.Ident.Any(char.IsControl)))
            throw new ArgumentException("Invalid private ship labels.");
        foreach(var ship in inventory.Ships)
            if(ShipLabels.TryGetValue(uint.Parse(ship.Id,System.Globalization.CultureInfo.InvariantCulture),out var label)){ship.Name=label.Name;ship.Ident=label.Ident;}
        return inventory;
    }

    public string SerializeCurrent(AccountState account)
    {
        var current = ExportCurrent(account);
        return NativeMaterials.Compose(NativeSavegameUpload.Compose(NativeFullSavegameDocument.SerializeComplete(current.Snapshot, current.Catalogue, current.Profile),current),current.Materials);
    }

    public NativeCommanderSave ExportCurrent(AccountState account) => this with
    {
        Snapshot = NativeCommanderProjection.ApplyInventory(Snapshot, Catalogue, account.NativeUserId, account.Commander,Materials.Count>0),
        Materials = NativeMaterials.Apply(Materials,account.Commander),
        Profile = Profile with { Name = account.Commander.Name }
    };

    public void ValidateCurrent(AccountState account)
    {
        _ = CreateInventory(account, account.Commander.Revision);
        _ = SerializeCurrent(account);
    }
}
public sealed record NativeShipLabel(string Name,string Ident);
