using Iced.Intel;

internal sealed record ApiMethodReference(string Path,string Method,int NativeMethod,string LiteralRva,string ConstructorCallRva,string FunctionRva);

// Constant arguments only. Tracks callee-saved integer constants across calls,
// including the compiler's common LEA EDX,[zero+method] spelling. Incoming branch
// paths merge by intersection, so conflicting branch values become unknown. No
// branch outcome, memory read or caller argument is guessed. Static evidence only.
internal static class NativeApiMethodScanner
{
    private sealed record Value(ulong Number,ApiPathLiteral[]? Paths=null);
    internal static IEnumerable<ApiMethodReference> Scan(IReadOnlyList<Instruction> instructions,ulong imageBase,
        IReadOnlyDictionary<uint,ApiPathLiteral> literals,uint function)
    {
        if(instructions.Count==0)yield break;
        var byAddress=instructions.Select((instruction,index)=>(instruction.IP,index)).ToDictionary(value=>value.IP,value=>value.index);
        var starts=new SortedSet<int>{0};
        for(var index=0;index<instructions.Count;index++)
        {
            var instruction=instructions[index];
            if(instruction.FlowControl is FlowControl.ConditionalBranch or FlowControl.UnconditionalBranch
                &&byAddress.TryGetValue(instruction.NearBranchTarget,out var target))starts.Add(target);
            if((instruction.FlowControl is not(FlowControl.Next or FlowControl.Call or FlowControl.IndirectCall)
                ||index+1<instructions.Count&&instruction.NextIP!=instructions[index+1].IP)&&index+1<instructions.Count)starts.Add(index+1);
        }
        var boundaries=starts.ToArray();var blockForInstruction=new int[instructions.Count];
        for(var block=0;block<boundaries.Length;block++)
            for(var index=boundaries[block];index<(block+1<boundaries.Length?boundaries[block+1]:instructions.Count);index++)blockForInstruction[index]=block;
        var entry=byAddress.TryGetValue(imageBase+function,out var entryInstruction)?blockForInstruction[entryInstruction]:0;
        var states=new Arguments?[boundaries.Length];states[entry]=new(imageBase,literals);
        var queue=new Queue<int>();var queued=new HashSet<int>{entry};queue.Enqueue(entry);
        while(queue.TryDequeue(out var block))
        {
            queued.Remove(block);var state=states[block]!.Copy();var end=block+1<boundaries.Length?boundaries[block+1]:instructions.Count;
            for(var index=boundaries[block];index<end;index++)state.Observe(instructions[index],retainBranchFacts:true);
            var last=instructions[end-1];var successors=new HashSet<int>();
            if(last.FlowControl is FlowControl.ConditionalBranch or FlowControl.UnconditionalBranch
                &&byAddress.TryGetValue(last.NearBranchTarget,out var destination))successors.Add(blockForInstruction[destination]);
            if(last.FlowControl is FlowControl.Next or FlowControl.Call or FlowControl.IndirectCall or FlowControl.ConditionalBranch
                &&end<instructions.Count&&last.NextIP==instructions[end].IP)successors.Add(blockForInstruction[end]);
            foreach(var successor in successors)
            {
                bool changed;
                if(states[successor] is null){states[successor]=state.Copy();changed=true;}
                else changed=states[successor]!.Intersect(state);
                if(changed&&queued.Add(successor))queue.Enqueue(successor);
            }
        }
        // Read only the converged states. A provisional path through a loop or
        // one side of a branch must never leave a stale method in the inventory.
        for(var block=0;block<boundaries.Length;block++)
        {
            if(states[block] is null)continue;var state=states[block]!.Copy();var end=block+1<boundaries.Length?boundaries[block+1]:instructions.Count;
            for(var index=boundaries[block];index<end;index++)
            {
                var instruction=instructions[index];
                if(instruction.FlowControl==FlowControl.Call&&instruction.NearBranchTarget==imageBase+0x99F3F0)
                    foreach(var method in state.ReadAll(function))yield return method with{ConstructorCallRva=$"0x{instruction.IP-imageBase:X}"};
                state.Observe(in instruction,retainBranchFacts:true);
            }
        }
    }
    internal static ApiMethodReference? Read(IReadOnlyList<Instruction> history,ulong imageBase,
        IReadOnlyDictionary<uint,ApiPathLiteral> literals,uint function)
    {
        var state=new Arguments(imageBase,literals);var targets=Targets(history);
        foreach(var instruction in history)
        {
            if(targets.Contains(instruction.IP))state.Clear();state.Observe(in instruction);
        }
        return state.Read(function);
    }
    private static HashSet<ulong> Targets(IReadOnlyList<Instruction> instructions)=>instructions
        .Where(value=>value.FlowControl is FlowControl.ConditionalBranch or FlowControl.UnconditionalBranch)
        .Select(value=>value.NearBranchTarget).ToHashSet();
    private sealed class Arguments(ulong imageBase,IReadOnlyDictionary<uint,ApiPathLiteral> literals)
    {
        private readonly Dictionary<Register,Value> registers=[];
        private readonly InstructionInfoFactory factory=new();
        internal void Clear()=>registers.Clear();
        internal Arguments Copy()
        {var copy=new Arguments(imageBase,literals);foreach(var pair in registers)copy.registers.Add(pair.Key,pair.Value);return copy;}
        internal bool Intersect(Arguments other)
        {
            var changed=false;foreach(var key in registers.Keys.ToArray())
            {
                var prior=registers[key];
                if(other.registers.TryGetValue(key,out var value))
                {
                    if(prior.Paths is {} left&&value.Paths is {} right)
                    {
                        var merged=left.Concat(right).Distinct().OrderBy(path=>path.Rva,StringComparer.Ordinal).ToArray();
                        if(merged.Length<=32)
                        {if(!left.SequenceEqual(merged)){registers[key]=new(0,merged);changed=true;}continue;}
                    }
                    else if(prior.Paths is null&&value.Paths is null&&prior.Number==value.Number)continue;
                }
                registers.Remove(key);changed=true;
            }
            return changed;
        }
        internal ApiMethodReference? Read(uint function)=>ReadAll(function).SingleOrDefault();
        internal IEnumerable<ApiMethodReference> ReadAll(uint function)
        {
            if(!registers.TryGetValue(Register.R8,out var path)||path.Paths is null
                ||!registers.TryGetValue(Register.RDX,out var method)||method.Paths is not null||method.Number>int.MaxValue)yield break;
            var number=(int)method.Number;
            // 5C166A..5C168A dispatches enum 4 to5C1843, which sets
            // CURLOPT_CUSTOMREQUEST(10036) to the literal DELETE at50BA6A0.
            foreach(var literal in path.Paths)
                yield return new(literal.Path,number switch{1=>"GET",2=>"POST",3=>"PUT",4=>"DELETE",_=>"UNRESOLVED"},number,literal.Rva,"",$"0x{function:X}");
        }
        internal void Observe(in Instruction instruction,bool retainBranchFacts=false)
        {
            if(instruction.IsInvalid){Clear();return;}
            Value? result=null;var destination=instruction.Op0Kind==OpKind.Register?instruction.Op0Register:Register.None;
            var wide=destination.GetSize() is 4 or 8;
            if(wide)
            {
                if(instruction.Mnemonic==Mnemonic.Mov)
                {
                    if(instruction.Op1Kind==OpKind.Register)registers.TryGetValue(instruction.Op1Register.GetFullRegister(),out result);
                    else if(IsImmediate(instruction.Op1Kind))result=new(instruction.GetImmediate(1));
                }
                else if(instruction.Mnemonic==Mnemonic.Xor&&instruction.Op1Kind==OpKind.Register&&instruction.Op1Register==destination)result=new(0);
                else if(instruction.Mnemonic==Mnemonic.Lea)
                {
                    if(instruction.IsIPRelativeMemoryOperand)
                    {
                        var address=instruction.IPRelativeMemoryAddress;
                        if(address>=imageBase&&address-imageBase<=uint.MaxValue&&literals.TryGetValue((uint)(address-imageBase),out var path))result=new(0,[path]);
                    }
                    else if(Number(instruction.MemoryBase,out var basis)&&Number(instruction.MemoryIndex,out var index))
                        result=new(unchecked(basis+index*(ulong)instruction.MemoryIndexScale+instruction.MemoryDisplacement64));
                }
                else if(instruction.Mnemonic is Mnemonic.Inc or Mnemonic.Dec&&Number(destination,out var operand))
                    result=new(unchecked(instruction.Mnemonic==Mnemonic.Inc?operand+1:operand-1));
                else if(instruction.Mnemonic is Mnemonic.Add or Mnemonic.Sub or Mnemonic.And or Mnemonic.Or&&IsImmediate(instruction.Op1Kind)&&Number(destination,out var operand2))
                {
                    var number=instruction.GetImmediate(1);result=new(instruction.Mnemonic switch
                    {Mnemonic.Add=>unchecked(operand2+number),Mnemonic.Sub=>unchecked(operand2-number),Mnemonic.And=>operand2&number,_=>operand2|number});
                }
                if(result is not null&&destination.GetSize()==4)result=result.Paths is null?new((uint)result.Number):null;
            }
            foreach(var used in factory.GetInfo(in instruction).GetUsedRegisters())
                if(used.Access is OpAccess.Write or OpAccess.CondWrite or OpAccess.ReadWrite or OpAccess.ReadCondWrite)
                    registers.Remove(used.Register.GetFullRegister());
            if(result is not null)registers[destination.GetFullRegister()]=result;
            if(instruction.FlowControl is FlowControl.Call or FlowControl.IndirectCall)
            {
                foreach(var register in new[]{Register.RAX,Register.RCX,Register.RDX,Register.R8,Register.R9,Register.R10,Register.R11})registers.Remove(register);
            }
            else if(instruction.FlowControl!=FlowControl.Next&&!retainBranchFacts)Clear();
        }
        private bool Number(Register register,out ulong value)
        {
            value=0;if(register==Register.None)return true;
            if(!registers.TryGetValue(register.GetFullRegister(),out var found)||found.Paths is not null)return false;
            value=found.Number;return true;
        }
        private static bool IsImmediate(OpKind kind)=>kind is OpKind.Immediate8 or OpKind.Immediate8to16 or OpKind.Immediate8to32 or OpKind.Immediate8to64
            or OpKind.Immediate16 or OpKind.Immediate32 or OpKind.Immediate32to64 or OpKind.Immediate64;
    }
}
