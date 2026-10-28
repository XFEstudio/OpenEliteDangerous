using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using XFEEliteDangerous.Core;
using XFEEliteDangerous.Client;

namespace XFEEliteDangerous.OfflineRuntime;

internal static partial class NativeLiveRefit
{
    private static nint resourceLibrary,modifierContext;
    private static readonly Dictionary<string,nint> recipeIndexes=new(StringComparer.Ordinal);
    private static void InstallAssets()
    {
        Check(0x53A590,"4883ec288b15deadb0054c8bc965488b042558000000b9280000004c8b04d04a");
        Check(0x122EEC0,"48895c2408574883ec2033ff488d058dc9f203897908488bd94889014883c110");
        Check(0x2139450,"40534883ec20488bd94883c110488339007407e8e8d19fffeb0233c080bbb900");
        Check(0x2139250,"4056574154415541574883ec20488bf14d8be14883c1104d8be848837a10004c");
        Check(0x438C5E0,"48895c2408574883ec20488b02488bfa488901488bd9488b420848894108488b");
        Check(0x1B2F2F0,"48895c240848896c24104889742418574883ec20488bf1410fb6e9498bc8498b");
        Check(0x2F5D420,"48895c2420574883ec20488bd948896c24304881c120ffffff4889742438410f");
        Check(0x2F5D600,"4055564883ec28488b01488bea488bf1ff504084c00f847b010000488b06488b");
        Check(0x2F4D3D0,"405356574883ec304883b9f003000000488bf90f297424200f57f674144883b9");
    }
    private static bool CanLoadRecipes()=>Read(resourceLibrary,out nint table)&&table==image+0x5216560
        &&Read(table+0x30,out nint load)&&load==image+0x1B2F2F0;
    private static string RecipeKey(NativeModuleSnapshot module)
    {
        var state=module.Engineering;
        if(state is null)return module.Name;
        return module.Name+"|"+state.EngineerId+"|"+state.RecipeId+"|"+state.RecipeName+"|"+state.Level+"|"+state.Progress.ToString("R",CultureInfo.InvariantCulture)+"|"+state.CraftCount
            +string.Join(";",state.Modifiers.OrderBy(value=>value.Name,StringComparer.Ordinal).Select(value=>value.Name+":"+value.Type+":"+value.Value.ToString("R",CultureInfo.InvariantCulture)));
    }
    private static unsafe nint Allocate(nuint size)=>((delegate* unmanaged<nuint,nint>)(image+0x53A590))(size) is var pointer&&pointer!=0?pointer:throw new OutOfMemoryException();
    private static unsafe nint NativeText(string value)
    {
        if(value.Length is <1 or >160||value.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c!='_'))throw new InvalidDataException("invalid_native_resource_name");
        var result=Allocate((nuint)value.Length+21);
        *(ulong*)result=(ulong)value.Length;*(ulong*)(result+8)=(ulong)value.Length;*(int*)(result+16)=1;
        Encoding.ASCII.GetBytes(value,new Span<byte>((void*)(result+20),value.Length));*(byte*)(result+20+value.Length)=0;return result;
    }
    private static unsafe void DropText(nint text)
    {
        if(text==0)return;
        if(!Read(text+16,out int count)||count is <1 or >1_000_000||!Read(text+8,out ulong length)||length>160)throw new InvalidDataException("invalid_native_string_lease");
        if(Interlocked.Decrement(ref *(int*)(text+16))==0)((delegate* unmanaged<nint,nuint,void>)(image+0x549C90))(text,(nuint)length+21);
    }
    private static unsafe void DropRecipe(nint reference)
    {
        if(!Read(reference+8,out int count)||count is <1 or >1_000_001)throw new InvalidDataException("invalid_native_recipe_lease");
        *(int*)(reference+8)=count-1;
        if(count==1&&Read(reference,out nint table)&&Read(table,out nint destroy)&&destroy>=image&&destroy<image+0x5000000)
            ((delegate* unmanaged<nint,uint,void>)destroy)(reference,1);
    }
    private static unsafe nint EnsureRecipe(NativeModuleSnapshot state)
    {
        var key=RecipeKey(state);if(recipeIndexes.TryGetValue(key,out var known))return known;
        NativeEngineering.Validate(state.Engineering);
        if(!CanLoadRecipes())throw new InvalidOperationException("native_resource_library_unavailable");
        nint text=0,recipe=0;byte* handle=stackalloc byte[8];new Span<byte>(handle,8).Clear();
        try
        {
            text=NativeText(state.Name);
            byte* resourceKey=stackalloc byte[24];new Span<byte>(resourceKey,24).Clear();
            *(nint*)resourceKey=text;*(nint*)(resourceKey+8)=text+20;*(ulong*)(resourceKey+16)=(ulong)state.Name.Length;
            ((delegate* unmanaged<nint,nint,nint,uint,uint,nint>)(image+0x1B2F2F0))(resourceLibrary,(nint)handle,(nint)resourceKey,0,0);
            if(*(nint*)handle==0)throw new InvalidOperationException("native_asset_not_found");
            recipe=Allocate(0xC0);
            ((delegate* unmanaged<nint,nint,nint>)(image+0x122EEC0))(recipe,(nint)handle);*(int*)(recipe+8)=1;
            if(state.Engineering is {} engineering)BuildEngineering(recipe,engineering);
            else ((delegate* unmanaged<nint,void>)(image+0x2139450))(recipe);
            if(!Read(recipe+24,out nint resolved)||resolved==0)throw new InvalidOperationException("native_asset_not_ready");
            if(!NativeEngineering.Equivalent(ReadEngineering(recipe),state.Engineering))throw new InvalidOperationException("native_engineering_build_differs");
            Recipes.Add(recipe,new(recipe,state));recipeIndexes.Add(key,recipe);
            OfflineDiagnostics.Current?.Write("info","native.refit","recipe.loaded","Original game resource library and modifier pipeline built a native recipe.",
                new Dictionary<string,string?>{["module"]=state.Name,["blueprint"]=state.Engineering?.RecipeName,["modifiers"]=state.Engineering?.Modifiers.Length.ToString()});
            var retained=recipe;recipe=0;return retained;
        }
        finally
        {
            if(recipe!=0)DropRecipe(recipe);
            ((delegate* unmanaged<nint,void>)(image+0x1B29370))((nint)handle);
            DropText(text);
        }
    }
    private static unsafe void BuildEngineering(nint recipe,NativeModuleEngineering state)
    {
        if(!Read(modifierContext,out nint table)||table!=image+0x5264420)throw new InvalidOperationException("native_engineering_context_unavailable");
        var texts=new List<nint>();
        try
        {
            byte* rows=stackalloc byte[state.Modifiers.Length*16];byte* vector=stackalloc byte[32];new Span<byte>(vector,32).Clear();
            for(var index=0;index<state.Modifiers.Length;index++)
            {
                var item=state.Modifiers[index];var text=NativeText(item.Name);texts.Add(text);
                *(nint*)(rows+index*16)=text;*(float*)(rows+index*16+8)=item.Value;*(uint*)(rows+index*16+12)=item.Type;
            }
            *(ulong*)vector=(ulong)state.Modifiers.Length;*(ulong*)(vector+16)=(ulong)state.Modifiers.Length;*(nint*)(vector+24)=(nint)rows;
            ((delegate* unmanaged<nint,nint,nint,nint,void>)(image+0x2139250))(recipe,(nint)vector,modifierContext,0);
            byte* metadata=stackalloc byte[48];new Span<byte>(metadata,48).Clear();
            var name=NativeText(state.RecipeName);texts.Add(name);
            //43ABA70 / 24DBB10 store recipe ID before engineer ID in this native structure.
            *(ulong*)metadata=state.RecipeId;*(ulong*)(metadata+8)=state.EngineerId;*(nint*)(metadata+16)=name;
            *(uint*)(metadata+24)=state.Level;*(float*)(metadata+28)=state.Progress;*(uint*)(metadata+32)=state.CraftCount;
            ((delegate* unmanaged<nint,nint,nint>)(image+0x438C5E0))(recipe+0x40,(nint)metadata);
        }
        finally{foreach(var text in texts)DropText(text);}
    }
    private static NativeModuleEngineering? ReadEngineering(nint recipe)
    {
        if(!Read(recipe+0x30,out ulong count)||count>64||!Read(recipe+0x38,out nint rows)||!Read(recipe+0x50,out nint name))throw new InvalidDataException("unreadable_native_engineering");
        if(count==0&&name==0)return null;
        var title=RcText(name)??throw new InvalidDataException("unreadable_native_blueprint_name");
        if(!Read(recipe+0x40,out ulong recipeId)||!Read(recipe+0x48,out ulong engineerId)||!Read(recipe+0x58,out uint level)
            ||!Read(recipe+0x5C,out float progress)||!Read(recipe+0x60,out uint craftCount))throw new InvalidDataException("unreadable_native_blueprint_metadata");
        var modifiers=new List<NativeEngineeringModifier>();
        for(var index=0;index<(int)count;index++)
        {
            if(!Read(rows+index*16,out nint text)||RcText(text) is not {} key||!Read(rows+index*16+8,out float value)||!Read(rows+index*16+12,out uint type))throw new InvalidDataException("unreadable_native_modifier");
            modifiers.Add(new(key,value,type));
        }
        var result=new NativeModuleEngineering(engineerId,recipeId,title,level,progress,craftCount,modifiers.ToArray());NativeEngineering.Validate(result);return result;
    }
    private static unsafe NativeModuleAmmo? ReadAmmunition(Module module)
    {
        var clip=FindComponent(module.Life,0x5F41D40);var hopper=FindComponent(module.Life,0x5F5096C);
        if(clip==0&&hopper==0)return null;
        uint clipCount=0,hopperCount=0;
        if(clip!=0)
        {
            if(!Read(clip,out nint table)||table!=image+0x5344B38||!Read(table+0x18,out nint getter)||getter!=image+0x2F4D3D0)throw new InvalidOperationException("native_clip_interface_differs");
            clipCount=((delegate* unmanaged<nint,uint>)getter)(clip);
        }
        if(hopper!=0)
        {
            if(!Read(hopper,out nint table)||table!=image+0x5340980||!Read(table+0x28,out nint getter)||getter!=image+0x2F4EA30)throw new InvalidOperationException("native_hopper_interface_differs");
            var quantity=((delegate* unmanaged<nint,ulong>)getter)(hopper);if(quantity>uint.MaxValue)throw new InvalidDataException("native_hopper_quantity_invalid");hopperCount=(uint)quantity;
        }
        return new(clipCount,hopperCount);
    }
    private static bool AmmoEditable(Module module)
    {
        var weapon=FindComponent(module.Life,0x5F39834);
        return weapon!=0&&Read(weapon,out nint table)&&Read(table+0x128,out nint getter)&&getter==image+0x2F4E8D0&&Read(weapon+0x304,out uint ammoType)&&ammoType!=uint.MaxValue;
    }
    private static unsafe void SetAmmunition(Module module,NativeModuleAmmo? ammo,bool required=false)
    {
        if(ammo is null)return;
        if(!AmmoEditable(module))
        {
            if(required||ReadAmmunition(module)!=ammo)throw new InvalidOperationException("native_ammunition_not_editable");return;
        }
        var clip=Component(module.Life,0x5F41D40,0x5344B38);var hopper=Component(module.Life,0x5F5096C,0x5340980);
        var capacity=AmmunitionCapacity(module);
        if(ammo.Clip>capacity.Clip||ammo.Hopper>capacity.Hopper)throw new InvalidDataException("ammunition_exceeds_native_capacity");
        ((delegate* unmanaged<nint,uint,byte,void>)(image+0x2F5D420))(clip,ammo.Clip,0);
        ((delegate* unmanaged<nint,ulong,void>)(image+0x2F5D600))(hopper,ammo.Hopper);
    }
    private static unsafe NativeModuleAmmo AmmunitionCapacity(Module module)
    {
        var clip=Component(module.Life,0x5F41D40,0x5344B38);var hopper=Component(module.Life,0x5F5096C,0x5340980);
        var clipCount=((delegate* unmanaged<nint,uint>)(image+0x2F4D380))(clip);
        var reserve=((delegate* unmanaged<nint,ulong>)(image+0x2F4E940))(hopper);
        if(reserve>uint.MaxValue)throw new InvalidDataException("native_ammo_capacity_invalid");return new(clipCount,(uint)reserve);
    }
    private static float? MaximumHealth(Module module)
    {
        var health=HealthComponent(module.Life);if(health==0||!Read(health+0x410,out ulong encoded))return null;
        var value=BitConverter.UInt32BitsToSingle((uint)encoded^(uint)(encoded>>32));return float.IsFinite(value)&&value>=0?value:null;
    }
    private static LiveRefitSlot ObservedSlot(Module module)=>new(module.Slot,Snapshot(module),HealthComponent(module.Life)!=0,PrioritySettings(module)!=0,AmmoEditable(module),MaximumHealth(module),AmmoEditable(module)?AmmunitionCapacity(module):null);
}
