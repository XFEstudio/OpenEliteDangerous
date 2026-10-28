using System.Runtime.InteropServices;
using System.Text;
using XFEEliteDangerous.Core;

namespace XFEEliteDangerous.OfflineRuntime;

internal static partial class NativeLiveRefit
{
    private sealed record Recipe(nint Reference, NativeModuleSnapshot Template);
    private static readonly Dictionary<nint, Recipe> Recipes = [];
    private static (string Name, int Index)[] slotDefinitions = [];

    private static void InstallOutfitting()
    {
        Check(0x4390E50,"48895c240848897424188954241055574156488bec4883ec708bfa410fb6f145");
        Check(0x439A040,"48895c2418488974242089542410574883ec4080b97902000000488bd98bfa74");
        Check(0x43B3A10,"40555356574154415541564157488d6c24f84881ec08010000488b05382faf01");
        Check(0x62F2A0,"40534883ec20488bd9e8f2a700004c8b034d85c07473498d4830b8fffffffff0");
    }
    private static nint Manager()=>Component(shipLife,0x5F2C234,0x5599D68);
    private static nint PrioritySettings(Module module)
    {
        if(!Read(Manager()+0xE0,out nint power)||!Read(power,out nint table)||table!=image+0x550AC48
            ||!Read(power+0x140,out ulong count)||count>128||!Read(power+0x148,out nint entries))throw new InvalidOperationException("native_power_manager_unavailable");
        for(var index=0;index<(int)count;index++)
        {
            var entry=entries+index*24;
            if(Read(entry,out nint life)&&life==module.Life&&Read(entry+16,out nint priority)&&Read(priority,out table)&&table==image+0x55983B0
                &&Read(table+0x18,out nint getter)&&getter==image+0x58DCB0&&Read(table+0x20,out nint setter)&&setter==image+0x43B3780)return priority;
        }
        return 0;
    }
    private static (string Name,int Index)[] NativeSlots()
    {
        if(slotDefinitions.Length>0)return slotDefinitions;
        var manager=Manager();
        if(!Read(manager+0x150,out nint definition)||!Read(definition+8,out nint array)||!Read(definition+0x10,out uint count)||count is 0 or >128)return [];
        var slots=new List<(string,int)>();
        for(var index=0;index<count;index++)
            if(Read(array+index*0x70,out nint name)&&Ascii(name) is {} text)slots.Add((text,index));
        if(slots.Count==count)slotDefinitions=slots.ToArray();
        return slots.ToArray();
    }
    private static nint CurrentLife(int index)
    {
        var manager=Manager();
        if(!Read(manager+0x1D0,out ulong count)||index<0||(ulong)index>=count||!Read(manager+0x1D8,out nint array)||!Read(array+index*0x30,out nint life))return 0;
        return life;
    }
    private static unsafe nint RecipeReference(int index)
    {
        var reference=((delegate* unmanaged<nint,uint,nint>)(image+0x439A040))(Manager(),(uint)index);
        if(!Read(reference,out nint recipe)||recipe==0)throw new InvalidOperationException("native_recipe_unavailable");
        return recipe;
    }
    private static unsafe void CacheRecipes()
    {
        var slots=NativeSlots();
        foreach(var module in Modules.Values)
        {
            if(!Live(module.Life,module.EntityId))continue;
            var matches=slots.Where(slot=>slot.Name==module.Slot).ToArray();if(matches.Length!=1||CurrentLife(matches[0].Index)!=module.Life)continue;
            var reference=RecipeReference(matches[0].Index);
            if(Recipes.ContainsKey(reference))continue;
            var snapshot=Snapshot(module);
            if(!Read(reference+8,out int count)||count is <1 or >1_000_000)throw new InvalidDataException("Invalid native recipe reference count.");
            // The pinned native Rc recipe uses a game-thread-only counter at +8.
            // This is the exact retain/release operation in 43B5080/43B5FC0,
            // keeping a removed recipe alive without altering any game state.
            *(int*)(reference+8)=count+1;
            Recipes.Add(reference,new(reference,snapshot));recipeIndexes.TryAdd(RecipeKey(snapshot),reference);
        }
    }
    private static unsafe void ClearRecipes()
    {
        foreach(var recipe in Recipes.Values)
        {
            DropRecipe(recipe.Reference);
        }
        Recipes.Clear();recipeIndexes.Clear();slotDefinitions=[];
    }
    private static Module? CurrentModule(string slot)
    {
        var definitions=NativeSlots().Where(value=>value.Name==slot).ToArray();
        if(definitions.Length!=1)throw new InvalidOperationException("native_slot_unavailable");
        var life=CurrentLife(definitions[0].Index);if(life==0)return null;
        if(!Read(life+0xB0,out ulong entity)||entity==0)throw new InvalidOperationException("native_module_unavailable");
        var recipe=RecipeReference(definitions[0].Index);
        if(!Recipes.TryGetValue(recipe,out var known))throw new InvalidOperationException("native_recipe_not_observed");
        if(Modules.TryGetValue(slot,out var old)&&old.Life==life&&old.EntityId==entity)return old;
        var module=new Module(life,entity,slot,known.Template);Modules[slot]=module;return module;
    }
    private static void BeginCommand(LiveRefitCommand command)
    {
        if(command.After is {Priority:>4} or {HealthMillion:>1_000_000})throw new InvalidDataException("invalid_module_properties");
        var module=CurrentModule(command.Slot);
        var before=module is null?null:Snapshot(module);
        if(!LiveRefitMailbox.Matches(before,command.Before))
        {Complete(command,false,"module_changed","The native slot no longer contains the requested module.",null);return;}
        if(command.Action=="properties")
        {
            if(module is null||command.After is null||!LiveRefitMailbox.Matches(before,command.After))throw new InvalidDataException("invalid_property_target");
            pending=new(command,module,before,Environment.TickCount64+150);
            SetProperties(module,command.After);return;
        }
        if(command.Action=="ammunition")
        {
            if(module is null||command.After?.Ammo is null||!LiveRefitMailbox.Matches(before,command.After))throw new InvalidDataException("invalid_ammunition_target");
            pending=new(command,module,before,Environment.TickCount64+150);SetAmmunition(module,command.After.Ammo,true);return;
        }
        if(command.Action is not("module" or "store" or "retrieve" or "engineering"))throw new InvalidDataException("unsupported_action");
        nint expectedRecipe=0;
        if(command.After is {} after)
        {
            if(after.Boost is not(null or 0)||after.Class is not(null or 0)||after.DamageOverride is not(null or 8))
            {Complete(command,false,"module_state_unsupported","Nondefault module boost/class state needs its own verified adapter.",null);return;}
            expectedRecipe=EnsureRecipe(after);
        }
        pending=new(command,module,before,Environment.TickCount64+150,true,expectedRecipe);
        if(command.After is null)RemoveModule(command.Slot);
        else InstallModule(command.Slot,command.After,expectedRecipe);
    }
    private static void VerifyPending(Pending waiting)
    {
        var command=waiting.Command;
        if(DateTimeOffset.UtcNow>=command.ExpiresUtc)throw new InvalidOperationException("readback_timeout");
        var module=CurrentModule(command.Slot);
        if(command.After is null)
        {
            if(module is null){Modules.Remove(command.Slot);CompletePending(waiting,new(command.Slot,null));pending=null;}
            return;
        }
        if(module is null||!Live(module.Life,module.EntityId))return;
        if(waiting.Initialize)
        {
            if(waiting.ExpectedRecipe!=RecipeReference(NativeSlots().Single(value=>value.Name==command.Slot).Index))throw new InvalidOperationException("installed_recipe_differs");
            module=module with{Data=command.After};Modules[command.Slot]=module;
            InitializeModule(module,command.After);SetProperties(module,command.After);
            pending=waiting with{Module=module,Initialize=false,VerifyAfter=Environment.TickCount64+150};return;
        }
        var actual=Snapshot(module);
        if(SameProperties(actual,command.After,command.Action!="properties")&&actual.Excess==command.After.Excess)
        {CompletePending(waiting,ObservedSlot(module));pending=null;}
    }
    private static unsafe void InitializeModule(Module module,NativeModuleSnapshot state)
    {
        var identity=Component(module.Life,0x5F2C224,0x5597298);Read(identity,out nint table);
        if(!Read(table+0xE0,out nint uid)||uid!=image+0x43B39F0||!Read(table+0xC8,out nint excess)||excess!=image+0x43B2D60)throw new InvalidDataException("Native module identity setters differ.");
        ((delegate* unmanaged<nint,ulong,void>)uid)(identity,state.Uid.GetValueOrDefault(ulong.MaxValue));
        ((delegate* unmanaged<nint,ulong,void>)excess)(identity,state.Excess);
        var power=Component(module.Life,0x5F2C228,0x5597F60);Read(power,out table);
        if(!Read(table+0x108,out nint damage)||damage!=image+0xEEA920)throw new InvalidDataException("Native module damage override setter differs.");
        ((delegate* unmanaged<nint,byte,void>)damage)(power,(byte)(state.DamageOverride??8));
        SetAmmunition(module,state.Ammo);
    }
    private static unsafe void RemoveModule(string name)
    {
        var index=NativeSlots().Single(value=>value.Name==name).Index;
        ((delegate* unmanaged<nint,uint,byte,byte,void>)(image+0x4390E50))(Manager(),(uint)index,1,1);
    }
    private static unsafe void InstallModule(string name,NativeModuleSnapshot module,nint preparedRecipe=0)
    {
        var index=NativeSlots().Single(value=>value.Name==name).Index;
        var recipe=preparedRecipe!=0?preparedRecipe:EnsureRecipe(module);
        byte* output=stackalloc byte[24];new Span<byte>(output,24).Clear();
        byte* weak=stackalloc byte[16];new Span<byte>(weak,16).Clear();
        try
        {
            // param6=0 preserves the original native slot compatibility check.
            ((delegate* unmanaged<nint,nint,uint,nint,nint,byte,byte,byte,ulong,void>)(image+0x43B3A10))
                (Manager(),(nint)output,(uint)index,(nint)(&recipe),(nint)weak,0,1,1,ulong.MaxValue);
            if(*(nint*)output==0)throw new InvalidOperationException("native_install_rejected");
        }
        finally{((delegate* unmanaged<nint,void>)(image+0x62F2A0))((nint)output);}
    }
    private static void CompletePending(Pending waiting,LiveRefitSlot actual)
    {
        if(waiting.RejectedCommand is {} rejected)Complete(rejected,false,"native_apply_failed_restored",waiting.FailureDetail??"Original native state restored and read back.",actual,true);
        else Complete(waiting.Command,true,"applied","Original native setters and subsequent update readback agree.",actual);
    }
    private static void BeginRecovery(Pending failed,Exception error)
    {
        var action=failed.Command.Action is "properties" or "ammunition"?failed.Command.Action:"module";
        var command=failed.Command with{Action=action,After=failed.Previous,ExpiresUtc=DateTimeOffset.UtcNow.AddSeconds(8)};
        var module=CurrentModule(command.Slot);var recipe=action=="module"&&failed.Previous is {} previous?EnsureRecipe(previous):0;
        pending=new(command,module,failed.Previous,Environment.TickCount64+150,action=="module",recipe,failed.Command,error.Message);
        if(action=="properties")SetProperties(module??throw new InvalidOperationException("recovery_module_missing"),failed.Previous!);
        else if(action=="ammunition")SetAmmunition(module??throw new InvalidOperationException("recovery_module_missing"),failed.Previous?.Ammo,true);
        else if(failed.Previous is null)RemoveModule(command.Slot);
        else InstallModule(command.Slot,failed.Previous,recipe);
    }
    private static string? Ascii(nint pointer)
    {
        var bytes=new List<byte>();
        for(var i=0;i<=100;i++)
        {
            if(!Read(pointer+i,out byte value))return null;
            if(value==0)return bytes.Count>0?Encoding.ASCII.GetString(bytes.ToArray()):null;
            if(!char.IsAsciiLetterOrDigit((char)value)&&value!='_')return null;
            bytes.Add(value);
        }
        return null;
    }
}
