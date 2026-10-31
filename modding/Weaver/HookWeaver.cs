using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace Weaver;

// Adds hook points. Two kinds:
//
// Body hooks, for game methods (we can edit their code). Foo gets
//     if (HookRegistry.Active[id]) { ...box args...; result = HookRegistry.Invoke(id, this, args); ...copy back ref args...; return result; }
//     <original body, unchanged>
// plus a private copy Foo__iosport_orig of the original body, which HookRegistry calls as "the original"
// (calling Foo itself would loop back into the hook, and could dispatch to an override).
//
// Call hooks, for Unity engine methods (compiled into the engine, can't be edited). Every call to the
// method from game or mod code is pointed at a generated IOSPortGenerated.HookIds.Call<id>() that does the
// same check and otherwise calls the engine method. Calls from inside the engine itself aren't affected.
//
// A generated IOSPortGenerated.HookIds.Register() tells HookRegistry which method is which id.
static class HookWeaver
{
    public const string CloneSuffix = "__iosport_orig";
    const string HookIdsType = "IOSPortGenerated.HookIds";

    public enum Kind { Body, Call }

    public sealed record Entry(int Id, Kind Kind, string Assembly, string Type, string Method);

    public static string? CantHook(MethodDefinition method, GameAssemblies game)
    {
        bool engine = game.IsEngine(method.DeclaringType);
        if (!engine && !game.IsWeavable(method.DeclaringType)) return "it's in " + method.Module.Assembly.Name.Name + ", which can't be modified";
        if (!engine && !method.HasBody) return "it has no body (abstract/extern)";
        if (method.IsConstructor) return "constructors can't be hooked yet";
        if (method.HasGenericParameters || HasGenericContext(method.DeclaringType)) return "generic methods can't be hooked";
        if (!method.IsStatic && method.DeclaringType.IsValueType) return "struct methods can't be hooked";
        if (method.ReturnType.IsByReference || method.ReturnType.IsPointer) return "it returns a reference";
        if (method.CallingConvention == MethodCallingConvention.VarArg) return "it uses varargs";
        foreach (var p in method.Parameters)
        {
            var t = p.ParameterType is ByReferenceType br ? br.ElementType : p.ParameterType;
            if (t.IsPointer || t.IsFunctionPointer || t.FullName.StartsWith("System.Span`1") || t.FullName.StartsWith("System.ReadOnlySpan`1") || t.FullName == "System.TypedReference")
                return "it takes a pointer/span parameter";
        }
        return null;
    }

    public static Kind KindOf(MethodDefinition method, GameAssemblies game) => game.IsEngine(method.DeclaringType) ? Kind.Call : Kind.Body;

    static bool HasGenericContext(TypeDefinition? type)
    {
        for (; type != null; type = type.DeclaringType)
            if (type.HasGenericParameters) return true;
        return false;
    }

    // Does this module call any of the given engine methods? (method full name set)
    public static bool HasCallSites(ModuleDefinition module, HashSet<string> targets) =>
        targets.Count > 0 && CallSites(module, targets).Any();

    static IEnumerable<(MethodDefinition caller, Instruction ins)> CallSites(ModuleDefinition module, HashSet<string> targets)
    {
        foreach (var type in module.GetTypes())
        {
            if (type.Namespace == "IOSPortGenerated")
                continue;
            foreach (var method in type.Methods.Where(m => m.HasBody))
            {
                foreach (var ins in method.Body.Instructions)
                {
                    if ((ins.OpCode == OpCodes.Call || ins.OpCode == OpCodes.Callvirt) && ins.Operand is MethodReference mr
                        && mr is not GenericInstanceMethod && targets.Contains(mr.FullName)
                        && ins.Previous?.OpCode != OpCodes.Constrained)
                        yield return (method, ins);
                }
            }
        }
    }

    public static int Weave(AssemblyDefinition assembly, ModuleDefinition runtime, IEnumerable<Entry> entries, List<string> warnings)
    {
        var module = assembly.MainModule;
        if (module.GetType(HookIdsType) != null)
            throw new InvalidOperationException(assembly.Name.Name + " already has hook points (it was woven twice)");

        var registry = runtime.GetType("IOSPort.Mods.HookRegistry");
        var refs = new Refs(
            module.ImportReference(registry.Fields.First(f => f.Name == "Active")),
            module.ImportReference(registry.Methods.First(m => m.Name == "Invoke")));
        var map = module.ImportReference(registry.Methods.First(m => m.Name == "Map"));

        var hookIds = new TypeDefinition("IOSPortGenerated", "HookIds",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
            module.TypeSystem.Object);
        var register = new MethodDefinition("Register", MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, module.TypeSystem.Void);
        hookIds.Methods.Add(register);
        module.Types.Add(hookIds);
        var reg = register.Body.GetILProcessor();

        void EmitMap(int id, MethodReference original, TypeReference originalType, MethodReference clone, TypeReference cloneType)
        {
            reg.Emit(OpCodes.Ldc_I4, id);
            reg.Emit(OpCodes.Ldtoken, original);
            reg.Emit(OpCodes.Ldtoken, originalType);
            reg.Emit(OpCodes.Ldtoken, clone);
            reg.Emit(OpCodes.Ldtoken, cloneType);
            reg.Emit(OpCodes.Call, map);
        }

        int count = 0;
        var entryList = entries.ToList();

        // Body hooks for this assembly's own methods.
        var types = module.GetTypes().ToDictionary(t => t.FullName);
        foreach (var e in entryList.Where(e => e.Kind == Kind.Body && string.Equals(e.Assembly, assembly.Name.Name, StringComparison.OrdinalIgnoreCase)))
        {
            if (!types.TryGetValue(e.Type, out var type))
            {
                warnings.Add($"{assembly.Name.Name}: type {e.Type} not in the player build, skipped");
                continue;
            }
            var method = type.Methods.FirstOrDefault(m => m.FullName == e.Method);
            if (method == null)
            {
                warnings.Add($"{assembly.Name.Name}: {e.Method} not in the player build, skipped");
                continue;
            }
            if (!method.HasBody || method.IsConstructor || method.HasGenericParameters)
                continue;

            var clone = Clone(method);
            type.Methods.Add(clone);
            AddStub(method, e.Id, refs);
            EmitMap(e.Id, method, type, clone, type);
            count++;
        }

        // Call hooks: redirect calls to engine methods through generated shims.
        var callEntries = entryList.Where(e => e.Kind == Kind.Call).ToDictionary(e => e.Method);
        var shims = new Dictionary<(int, bool), MethodDefinition>();
        foreach (var (caller, ins) in CallSites(module, callEntries.Keys.ToHashSet()).ToList())
        {
            var target = (MethodReference)ins.Operand;
            var entry = callEntries[target.FullName];
            bool virtualCall = ins.OpCode == OpCodes.Callvirt;
            if (!shims.TryGetValue((entry.Id, virtualCall), out var shim))
            {
                shim = MakeCallShim(module, hookIds, target, entry.Id, virtualCall, refs);
                shims[(entry.Id, virtualCall)] = shim;
                if (!shims.ContainsKey((entry.Id, !virtualCall)))
                    EmitMap(entry.Id, module.ImportReference(target), module.ImportReference(target.DeclaringType), module.ImportReference(target), module.ImportReference(target.DeclaringType));
            }
            ins.OpCode = OpCodes.Call;
            ins.Operand = shim;
            count++;
        }

        reg.Emit(OpCodes.Ret);
        return count;
    }

    sealed record Refs(FieldReference Active, MethodReference Invoke);

    static MethodDefinition MakeCallShim(ModuleDefinition module, TypeDefinition owner, MethodReference target, int id, bool virtualCall, Refs refs)
    {
        var resolved = target.Resolve();
        bool instance = target.HasThis;
        var shim = new MethodDefinition($"Call{id}" + (virtualCall ? "" : "_nv"), MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
            module.ImportReference(target.ReturnType));
        ParameterDefinition? self = null;
        if (instance)
        {
            self = new ParameterDefinition("self", ParameterAttributes.None, module.ImportReference(target.DeclaringType));
            shim.Parameters.Add(self);
        }
        var args = new List<ParameterDefinition>();
        foreach (var p in target.Parameters)
        {
            var copy = new ParameterDefinition(p.Name, p.Attributes, module.ImportReference(p.ParameterType));
            shim.Parameters.Add(copy);
            args.Add(copy);
        }
        owner.Methods.Add(shim);

        var body = shim.Body;
        var direct = Instruction.Create(OpCodes.Nop);
        var il = EmitDispatch(body, args, self, shim.ReturnType, id, refs, direct);
        var processor = body.GetILProcessor();
        foreach (var ins in il)
            processor.Append(ins);
        processor.Append(direct);
        foreach (var p in shim.Parameters)
            processor.Emit(OpCodes.Ldarg, p);
        processor.Emit(virtualCall && resolved?.IsVirtual == true ? OpCodes.Callvirt : OpCodes.Call, module.ImportReference(target));
        processor.Emit(OpCodes.Ret);
        body.OptimizeMacros();
        return shim;
    }

    internal static MethodDefinition Clone(MethodDefinition method)
    {
        var attrs = method.Attributes & ~(MethodAttributes.MemberAccessMask | MethodAttributes.Virtual | MethodAttributes.Abstract
            | MethodAttributes.NewSlot | MethodAttributes.Final | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName
            | MethodAttributes.CheckAccessOnOverride);
        var clone = new MethodDefinition(method.Name + CloneSuffix, attrs | MethodAttributes.Private | MethodAttributes.HideBySig, method.ReturnType)
        {
            ImplAttributes = method.ImplAttributes,
        };
        foreach (var p in method.Parameters)
            clone.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, p.ParameterType));

        var src = method.Body;
        var dst = clone.Body;
        dst.InitLocals = src.InitLocals;
        dst.MaxStackSize = src.MaxStackSize;
        var variables = new Dictionary<VariableDefinition, VariableDefinition>();
        foreach (var v in src.Variables)
        {
            var nv = new VariableDefinition(v.VariableType);
            variables[v] = nv;
            dst.Variables.Add(nv);
        }

        var map = new Dictionary<Instruction, Instruction>();
        foreach (var ins in src.Instructions)
        {
            var copy = Instruction.Create(OpCodes.Nop);
            copy.OpCode = ins.OpCode;
            copy.Operand = ins.Operand;
            map[ins] = copy;
            dst.Instructions.Add(copy);
        }
        foreach (var copy in dst.Instructions)
        {
            copy.Operand = copy.Operand switch
            {
                Instruction target => map[target],
                Instruction[] targets => targets.Select(t => map[t]).ToArray(),
                VariableDefinition v => variables[v],
                ParameterDefinition p when p == src.ThisParameter => dst.ThisParameter,
                ParameterDefinition p => clone.Parameters[p.Index],
                var other => other,
            };
        }
        foreach (var h in src.ExceptionHandlers)
        {
            dst.ExceptionHandlers.Add(new ExceptionHandler(h.HandlerType)
            {
                CatchType = h.CatchType,
                TryStart = Map(h.TryStart), TryEnd = Map(h.TryEnd),
                HandlerStart = Map(h.HandlerStart), HandlerEnd = Map(h.HandlerEnd),
                FilterStart = Map(h.FilterStart),
            });
        }
        return clone;

        Instruction? Map(Instruction? i) => i == null ? null : map[i];
    }

    static void AddStub(MethodDefinition method, int id, Refs refs)
    {
        var body = method.Body;
        body.SimplifyMacros();
        var first = body.Instructions[0];
        var il = EmitDispatch(body, method.Parameters, method.IsStatic ? null : body.ThisParameter, method.ReturnType, id, refs, first);
        var processor = body.GetILProcessor();
        foreach (var ins in il)
            processor.InsertBefore(first, ins);
        body.OptimizeMacros();
    }

    // if (!Active[id]) goto skip; object[] a = {args}; object r = HookRegistry.Invoke(id, self, a); <copy back refs>; return (R)r;
    static List<Instruction> EmitDispatch(MethodBody body, IList<ParameterDefinition> parameters, ParameterDefinition? self,
        TypeReference returnType, int id, Refs refs, Instruction skip)
    {
        var module = body.Method.Module;
        var objectType = module.TypeSystem.Object;
        var argsVar = new VariableDefinition(new ArrayType(objectType));
        var tmpVar = new VariableDefinition(objectType);
        body.Variables.Add(argsVar);
        body.Variables.Add(tmpVar);
        body.InitLocals = true;

        var il = new List<Instruction>();
        void Emit(Instruction i) => il.Add(i);

        Emit(Instruction.Create(OpCodes.Ldsfld, refs.Active));
        Emit(Instruction.Create(OpCodes.Ldc_I4, id));
        Emit(Instruction.Create(OpCodes.Ldelem_U1));
        Emit(Instruction.Create(OpCodes.Brfalse, skip));

        Emit(Instruction.Create(OpCodes.Ldc_I4, parameters.Count));
        Emit(Instruction.Create(OpCodes.Newarr, objectType));
        Emit(Instruction.Create(OpCodes.Stloc, argsVar));
        for (int i = 0; i < parameters.Count; i++)
        {
            var p = parameters[i];
            if (p.IsOut)
                continue;
            var type = p.ParameterType is ByReferenceType br ? br.ElementType : p.ParameterType;
            Emit(Instruction.Create(OpCodes.Ldloc, argsVar));
            Emit(Instruction.Create(OpCodes.Ldc_I4, i));
            Emit(Instruction.Create(OpCodes.Ldarg, p));
            if (p.ParameterType.IsByReference)
                Emit(Instruction.Create(OpCodes.Ldobj, type));
            if (type.IsValueType || type.IsGenericParameter)
                Emit(Instruction.Create(OpCodes.Box, type));
            Emit(Instruction.Create(OpCodes.Stelem_Ref));
        }

        Emit(Instruction.Create(OpCodes.Ldc_I4, id));
        Emit(self == null ? Instruction.Create(OpCodes.Ldnull) : Instruction.Create(OpCodes.Ldarg, self));
        Emit(Instruction.Create(OpCodes.Ldloc, argsVar));
        Emit(Instruction.Create(OpCodes.Call, refs.Invoke));
        Emit(Instruction.Create(OpCodes.Stloc, tmpVar));

        for (int i = 0; i < parameters.Count; i++)
        {
            var p = parameters[i];
            if (!p.ParameterType.IsByReference)
                continue;
            var type = ((ByReferenceType)p.ParameterType).ElementType;
            var next = Instruction.Create(OpCodes.Nop);
            var value = new VariableDefinition(objectType);
            body.Variables.Add(value);
            Emit(Instruction.Create(OpCodes.Ldloc, argsVar));
            Emit(Instruction.Create(OpCodes.Ldc_I4, i));
            Emit(Instruction.Create(OpCodes.Ldelem_Ref));
            Emit(Instruction.Create(OpCodes.Stloc, value));
            if (type.IsValueType)
            {
                Emit(Instruction.Create(OpCodes.Ldloc, value));
                Emit(Instruction.Create(OpCodes.Brfalse, next));
            }
            Emit(Instruction.Create(OpCodes.Ldarg, p));
            Emit(Instruction.Create(OpCodes.Ldloc, value));
            Emit(Instruction.Create(OpCodes.Unbox_Any, type));
            Emit(Instruction.Create(OpCodes.Stobj, type));
            Emit(next);
        }

        if (returnType.MetadataType == MetadataType.Void)
        {
            Emit(Instruction.Create(OpCodes.Ret));
        }
        else if (returnType.IsValueType)
        {
            // null (e.g. a prefix skipped the original without setting __result) -> default(T)
            var resultVar = new VariableDefinition(returnType);
            body.Variables.Add(resultVar);
            var useDefault = Instruction.Create(OpCodes.Ldloca, resultVar);
            Emit(Instruction.Create(OpCodes.Ldloc, tmpVar));
            Emit(Instruction.Create(OpCodes.Brfalse, useDefault));
            Emit(Instruction.Create(OpCodes.Ldloc, tmpVar));
            Emit(Instruction.Create(OpCodes.Unbox_Any, returnType));
            Emit(Instruction.Create(OpCodes.Ret));
            Emit(useDefault);
            Emit(Instruction.Create(OpCodes.Initobj, returnType));
            Emit(Instruction.Create(OpCodes.Ldloc, resultVar));
            Emit(Instruction.Create(OpCodes.Ret));
        }
        else
        {
            Emit(Instruction.Create(OpCodes.Ldloc, tmpVar));
            Emit(Instruction.Create(OpCodes.Castclass, returnType));
            Emit(Instruction.Create(OpCodes.Ret));
        }
        return il;
    }
}
