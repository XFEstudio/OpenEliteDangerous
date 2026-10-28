using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using XFEEliteDangerous.Client;

namespace XFEEliteDangerous.Core;

public sealed record NativeEngineeringModifier(string Name, float Value, uint Type);
public sealed record NativeModuleEngineering(ulong EngineerId, ulong RecipeId, string RecipeName, uint Level,
    float Progress, uint CraftCount, NativeEngineeringModifier[] Modifiers);
public sealed record NativeEngineeringBlueprint(string Key, string ModulePrefix, NativeModuleEngineering State);
public sealed record NativeExperimentalEffect(string Name, string[] Tags);
public sealed record NativeRefitCatalogue(NativeModuleDefinition[] Modules, NativeEngineeringBlueprint[] Blueprints, NativeExperimentalEffect[] Specials);

public static class NativeEngineering
{
    private static readonly Lazy<NativeRefitCatalogue> catalogue=new(()=>
    {
        var assembly=typeof(NativeEngineering).Assembly;
        using var stream=assembly.GetManifestResourceStream(assembly.GetManifestResourceNames().Single(name=>name.EndsWith(".native-refit.json",StringComparison.Ordinal)))!;
        return JsonSerializer.Deserialize(stream,EngineeringJson.Default.NativeRefitCatalogue)??throw new InvalidDataException("Invalid public refit definitions.");
    });
    public static NativeRefitCatalogue Catalogue=>catalogue.Value;
    public static string Family(string module)=>module.StartsWith("Int_Hyperdrive_",StringComparison.Ordinal)?"Int_Hyperdrive_"
        :module.Contains("_Armour_",StringComparison.Ordinal)?module.Split("_Armour_",StringSplitOptions.None)[0]+"_Armour_"
        :module.StartsWith("Hpt_",StringComparison.Ordinal)?string.Join('_',module.Split('_').Take(2))+"_":module.Split("_Size",StringSplitOptions.None)[0]+"_";
    public static NativeEngineeringBlueprint[] Blueprints(string module)=>Catalogue.Blueprints.Where(value=>value.ModulePrefix==Family(module)).ToArray();
    public static string? Tag(string module)=>Family(module) switch
    {
        "Int_Hyperdrive_" or "Int_Hyperdrive_Overcharge_"=>"eTag_FSD",
        "Int_Engine_"=>"eTag_Engine", "Int_Powerplant_"=>"eTag_PowerPlant", "Int_PowerDistributor_"=>"eTag_PowerDistributor",
        "Int_ShieldGenerator_" or "Int_ShieldGenerator_BiWeave_"=>"eTag_ShieldGenerator",
        "Int_ShieldCellBank_"=>"eTag_ShieldCell", "Int_HullReinforcement_"=>"eTag_HullReinforcement",
        var family when family.Contains("_Armour_",StringComparison.Ordinal)=>"eTag_Armour",
        "Hpt_PulseLaser_"=>"eTag_PulseLaser", "Hpt_BurstLaser_"=>"eTag_BurstLaser", "Hpt_BeamLaser_"=>"eTag_BeamLaser",
        "Hpt_MultiCannon_"=>"eTag_Multicannon", "Hpt_Cannon_"=>"eTag_Cannon", "Hpt_Slugshot_"=>"eTag_FragCannon",
        "Hpt_RailGun_"=>"eTag_RailGun", "Hpt_PlasmaAccelerator_"=>"eTag_PlasmaAccelerator",
        "Hpt_ShieldBooster_"=>"eTag_ShieldBooster", _=>null
    };
    public static NativeExperimentalEffect[] Effects(string module)=>Tag(module) is {} tag?Catalogue.Specials.Where(value=>value.Tags.Contains(tag,StringComparer.Ordinal)
        ||module.StartsWith("Hpt_",StringComparison.Ordinal)&&tag!="eTag_ShieldBooster"&&value.Tags.Contains("eTag_Weapon",StringComparer.Ordinal)).ToArray():[];
    public static NativeModuleEngineering? Prepare(string module,JsonObject input)
    {
        if(input["remove"]?.GetValue<bool>()==true)return null;
        var key=input["blueprintKey"]?.GetValue<string>()??throw new InvalidDataException(AppText.Get("Refit.BlueprintRequired"));
        var blueprint=Blueprints(module).SingleOrDefault(value=>value.Key==key)??throw new InvalidDataException(AppText.Get("Refit.BlueprintRequired"));
        var modifiers=blueprint.State.Modifiers.ToDictionary(value=>value.Name,StringComparer.Ordinal);
        if(input["values"] is JsonObject values)
        {
            if(values.Count>64)throw new InvalidDataException(AppText.Get("Refit.InvalidEngineering"));
            foreach(var pair in values)
            {
                if(!modifiers.TryGetValue(pair.Key,out var original)||pair.Value is not JsonValue number||!number.TryGetValue<float>(out var value)
                    ||!float.IsFinite(value)||value is < -0.99f or >100)throw new InvalidDataException(AppText.Get("Refit.InvalidEngineering"));
                modifiers[pair.Key]=original with{Value=value};
            }
        }
        var effect=input["experimental"]?.GetValue<string>()??"";
        if(effect.Length>0)
        {
            if(!Effects(module).Any(value=>value.Name==effect))throw new InvalidDataException(AppText.Get("Refit.InvalidEngineering"));
            modifiers.Add(effect,new(effect,1,3));
        }
        var state=blueprint.State with{Modifiers=modifiers.Values.ToArray()};Validate(state);return state;
    }
    public static void Validate(NativeModuleEngineering? state)
    {
        if(state is null)return;
        if(state.RecipeName.Length is <1 or >100||state.RecipeName.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c!='_')||state.Level is <1 or >5
            ||!float.IsFinite(state.Progress)||state.Progress is <0 or >1||state.Modifiers.Length is <1 or >64||state.Modifiers.Count(value=>value.Type==3)>1)
            throw new ArgumentException("Invalid native engineering metadata.");
        var names=new HashSet<string>(StringComparer.Ordinal);
        foreach(var item in state.Modifiers)
            if(item.Name.Length is <1 or >100||!names.Add(item.Name)||item.Name.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c!='_')
                ||!float.IsFinite(item.Value)||item.Value is < -0.99f or >100||item.Type is not(1 or 3)||item.Type==3&&item.Value!=1)
                throw new ArgumentException("Invalid native engineering modifier.");
    }
    public static bool Equivalent(NativeModuleEngineering? left,NativeModuleEngineering? right)=>left is null?right is null:right is not null
        &&left.EngineerId==right.EngineerId&&left.RecipeId==right.RecipeId&&left.RecipeName==right.RecipeName&&left.Level==right.Level
        &&Math.Abs(left.Progress-right.Progress)<.00001f&&left.CraftCount==right.CraftCount&&left.Modifiers.Length==right.Modifiers.Length
        &&left.Modifiers.All(item=>right.Modifiers.Any(value=>value.Name==item.Name&&value.Type==item.Type&&Math.Abs(value.Value-item.Value)<.00001f));
}

[JsonSourceGenerationOptions(PropertyNamingPolicy=JsonKnownNamingPolicy.CamelCase,GenerationMode=JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(NativeRefitCatalogue))]
internal partial class EngineeringJson:JsonSerializerContext;
