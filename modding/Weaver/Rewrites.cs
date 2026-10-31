using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Weaver;

static class Rewrites
{
    // Harmony: route patching to HookRegistry instead of MonoMod detours (impossible on iOS).
    public static void PatchHarmony(AssemblyDefinition harmony, ModuleDefinition runtime)
    {
        var module = harmony.MainModule;
        var update = module.ImportReference(runtime.GetType("IOSPort.Mods.HookRegistry").Methods.First(m => m.Name == "Update"));

        var wrapper = module.GetType("HarmonyLib.PatchFunctions").Methods.First(m => m.Name == "UpdateWrapper");
        ReplaceBody(wrapper, il =>
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Call, update);
            il.Emit(OpCodes.Ret);
        });

        // MethodInvoker.GetHandler builds a DynamicMethod (used by AccessTools' static constructor, among others).
        var fastInvoker = module.ImportReference(runtime.GetType("IOSPort.Mods.HookRegistry").Methods.First(m => m.Name == "FastInvoker"));
        var handlerType = module.GetType("HarmonyLib.FastInvokeHandler");
        var corlib = module.TypeSystem.CoreLibrary;
        var typeType = new TypeReference("System", "Type", module, corlib);
        var getTypeFromHandle = new MethodReference("GetTypeFromHandle", typeType, typeType);
        getTypeFromHandle.Parameters.Add(new ParameterDefinition(new TypeReference("System", "RuntimeTypeHandle", module, corlib, true)));
        foreach (var getHandler in module.GetType("HarmonyLib.MethodInvoker").Methods.Where(m => m.Name == "GetHandler").ToList())
        {
            ReplaceBody(getHandler, il =>
            {
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldtoken, handlerType);
                il.Emit(OpCodes.Call, getTypeFromHandle);
                il.Emit(OpCodes.Call, fastInvoker);
                il.Emit(OpCodes.Castclass, handlerType);
                il.Emit(OpCodes.Ret);
            });
        }

        // GeneralExtensions.FullDescription (used to build log messages) hits reflection IL2CPP doesn't support
        // for some generic types; fall back to a plain name instead of failing the patch.
        foreach (var describe in module.GetType("HarmonyLib.GeneralExtensions").Methods.Where(m => m.Name == "FullDescription"
            && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.FullName == "System.Reflection.MethodBase").ToList())
            WrapWithFallback(describe, module);

        // Harmony's static constructor installs stack-trace detours.
        var stackTraceFixes = module.GetType("HarmonyLib.Internal.RuntimeFixes.StackTraceFixes")
            ?? module.GetTypes().First(t => t.Name == "StackTraceFixes");
        ReplaceBody(stackTraceFixes.Methods.First(m => m.Name == "Install"), il => il.Emit(OpCodes.Ret));
    }

    // Mod DLLs:
    //  - Assembly.Location is "" on iOS; send it to ModLoader.AssemblyLocation instead.
    //  - harmony.PatchAll() and Assembly.GetExecutingAssembly() work out the calling assembly from the
    //    call stack, which IL2CPP can't be relied on for; pass the mod's own assembly explicitly.
    public static int PatchMod(AssemblyDefinition mod, ModuleDefinition runtime)
    {
        var module = mod.MainModule;
        MethodReference? replacement = null;
        int count = 0;
        var corlib = module.TypeSystem.CoreLibrary;
        var typeType = new TypeReference("System", "Type", module, corlib);
        var assemblyType = new TypeReference("System.Reflection", "Assembly", module, corlib);
        var getTypeFromHandle = new MethodReference("GetTypeFromHandle", typeType, typeType);
        getTypeFromHandle.Parameters.Add(new ParameterDefinition(new TypeReference("System", "RuntimeTypeHandle", module, corlib, true)));
        var getAssembly = new MethodReference("get_Assembly", assemblyType, typeType) { HasThis = true };
        var anchor = module.Types.First(t => t.Name != "<Module>" && !t.HasGenericParameters);

        foreach (var type in module.GetTypes())
        {
            foreach (var method in type.Methods.Where(m => m.HasBody))
            {
                var il = method.Body.GetILProcessor();
                foreach (var ins in method.Body.Instructions.ToList())
                {
                    if (ins.Operand is MethodReference call && (ins.OpCode == OpCodes.Call || ins.OpCode == OpCodes.Callvirt))
                    {
                        bool patchAll = call.DeclaringType.FullName == "HarmonyLib.Harmony" && call.Name == "PatchAll" && call.Parameters.Count == 0;
                        bool executing = call.DeclaringType.FullName == "System.Reflection.Assembly" && call.Name == "GetExecutingAssembly";
                        if (patchAll || executing)
                        {
                            // ... typeof(SomeTypeInThisMod).Assembly. The call instruction itself becomes the
                            // ldtoken so branches that target it still land at the start of the sequence.
                            var originalCall = call;
                            ins.OpCode = OpCodes.Ldtoken;
                            ins.Operand = anchor;
                            var getType = il.Create(OpCodes.Call, getTypeFromHandle);
                            var toAssembly = il.Create(OpCodes.Callvirt, getAssembly);
                            il.InsertAfter(ins, getType);
                            il.InsertAfter(getType, toAssembly);
                            if (patchAll)
                            {
                                var withAssembly = new MethodReference("PatchAll", module.TypeSystem.Void, originalCall.DeclaringType) { HasThis = true };
                                withAssembly.Parameters.Add(new ParameterDefinition(assemblyType));
                                il.InsertAfter(toAssembly, il.Create(OpCodes.Callvirt, withAssembly));
                            }
                            count++;
                            continue;
                        }
                    }

                    if ((ins.OpCode == OpCodes.Callvirt || ins.OpCode == OpCodes.Call) && ins.Operand is MethodReference mr
                        && mr.Name is "get_Location" or "get_CodeBase"
                        && mr.DeclaringType.FullName == "System.Reflection.Assembly")
                    {
                        replacement ??= module.ImportReference(runtime.GetType("IOSPort.Mods.ModLoader").Methods.First(m => m.Name == "AssemblyLocation"));
                        ins.OpCode = OpCodes.Call;
                        ins.Operand = replacement;
                        count++;
                    }
                }
            }
        }
        return count;
    }

    // AccessTools.FieldRefAccess / StaticFieldRefAccess build accessors with DynamicMethod at runtime.
    // When the class, field type and field name are spelled out in the call (the usual case), generate the
    // accessor at build time instead:
    //   FieldRefAccess<T,F>("name")          -> delegate to a generated `ref F Ref(T o) => ref o.name`
    //   FieldRefAccess<T,F>(obj, "name")     -> ldflda
    //   StaticFieldRefAccess<T,F>("name")    -> ldsflda
    public static int FieldRefs(ModuleDefinition module, List<string> unresolved)
    {
        TypeDefinition? holder = null;
        int count = 0, next = 0;
        foreach (var type in module.GetTypes().ToList())
        {
            foreach (var method in type.Methods.Where(m => m.HasBody).ToList())
            {
                var il = method.Body.GetILProcessor();
                foreach (var ins in method.Body.Instructions.ToList())
                {
                    if (ins.OpCode != OpCodes.Call || ins.Operand is not GenericInstanceMethod gm
                        || gm.ElementMethod.DeclaringType.FullName != "HarmonyLib.AccessTools"
                        || ins.Previous?.OpCode != OpCodes.Ldstr)
                        continue;
                    string name = gm.ElementMethod.Name;
                    var ps = gm.ElementMethod.Parameters;
                    bool delegateForm = name == "FieldRefAccess" && gm.GenericArguments.Count == 2 && ps.Count == 1 && ps[0].ParameterType.FullName == "System.String";
                    bool instanceForm = name == "FieldRefAccess" && gm.GenericArguments.Count == 2 && ps.Count == 2 && ps[1].ParameterType.FullName == "System.String";
                    bool staticForm = name == "StaticFieldRefAccess" && gm.GenericArguments.Count == 2 && ps.Count == 1 && ps[0].ParameterType.FullName == "System.String";
                    if (!delegateForm && !instanceForm && !staticForm)
                        continue;

                    var ldstr = ins.Previous;
                    string fieldName = (string)ldstr.Operand;
                    var owner = gm.GenericArguments[0];
                    FieldDefinition? field = null;
                    try
                    {
                        for (var t = owner.Resolve(); t != null && field == null; t = t.BaseType?.Resolve())
                            field = t.Fields.FirstOrDefault(f => f.Name == fieldName);
                    }
                    catch (AssemblyResolutionException) { }
                    if (field == null || owner.IsValueType || owner is GenericInstanceType || field.IsStatic != staticForm)
                    {
                        unresolved.Add($"{owner.Name}.{fieldName}");
                        continue;
                    }
                    var fieldRef = module.ImportReference(field);

                    if (instanceForm || staticForm)
                    {
                        ldstr.OpCode = OpCodes.Nop;
                        ldstr.Operand = null;
                        ins.OpCode = staticForm ? OpCodes.Ldsflda : OpCodes.Ldflda;
                        ins.Operand = fieldRef;
                        count++;
                        continue;
                    }

                    if (holder == null)
                    {
                        holder = new TypeDefinition("IOSPortGenerated", "FieldRefs",
                            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed, module.TypeSystem.Object);
                        module.Types.Add(holder);
                    }
                    var accessor = new MethodDefinition($"Ref{next++}_{fieldName}", MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
                        new ByReferenceType(module.ImportReference(gm.GenericArguments[1])));
                    accessor.Parameters.Add(new ParameterDefinition("instance", ParameterAttributes.None, module.ImportReference(owner)));
                    var body = accessor.Body.GetILProcessor();
                    body.Emit(OpCodes.Ldarg_0);
                    body.Emit(OpCodes.Ldflda, fieldRef);
                    body.Emit(OpCodes.Ret);
                    holder.Methods.Add(accessor);

                    // new AccessTools.FieldRef<T,F>(null, &accessor)
                    var delegateType = new GenericInstanceType(((GenericInstanceType)gm.ElementMethod.ReturnType).ElementType);
                    delegateType.GenericArguments.Add(gm.GenericArguments[0]);
                    delegateType.GenericArguments.Add(gm.GenericArguments[1]);
                    var ctor = new MethodReference(".ctor", module.TypeSystem.Void, delegateType) { HasThis = true };
                    ctor.Parameters.Add(new ParameterDefinition(module.TypeSystem.Object));
                    ctor.Parameters.Add(new ParameterDefinition(module.TypeSystem.IntPtr));
                    ldstr.OpCode = OpCodes.Ldnull;
                    ldstr.Operand = null;
                    ins.OpCode = OpCodes.Ldftn;
                    ins.Operand = accessor;
                    il.InsertAfter(ins, il.Create(OpCodes.Newobj, ctor));
                    count++;
                }
            }
        }
        return count;
    }

    // Things that can't work on iOS, for the report.
    public static IEnumerable<string> Problems(ModuleDefinition mod)
    {
        var seen = new HashSet<string>();
        foreach (var type in mod.GetTypes())
        {
            foreach (var method in type.Methods.Where(m => m.HasBody))
            {
                foreach (var ins in method.Body.Instructions)
                {
                    if (ins.Operand is not MethodReference mr)
                        continue;
                    string owner = mr.DeclaringType.FullName;
                    string? problem =
                        owner.StartsWith("System.Reflection.Emit.") ? "generates code at runtime (Reflection.Emit), which iOS blocks" :
                        owner.StartsWith("MonoMod.RuntimeDetour.") || owner.StartsWith("On.") || owner.StartsWith("IL.") ? "uses MonoMod hooks, which need runtime code patching" :
                        owner == "System.Reflection.Assembly" && mr.Name is "LoadFile" or "LoadFrom" ? "loads DLLs from disk at runtime" :
                        mr.Name == "ReversePatch" || owner == "HarmonyLib.ReversePatcher" ? "uses Harmony reverse patches" :
                        owner == "HarmonyLib.AccessTools" && mr.Name.Contains("FieldRefAccess") && ins.Previous?.OpCode != OpCodes.Ldstr ? "uses AccessTools.*FieldRefAccess with a computed field name, which generates code at runtime and won't work on iOS" :
                        owner == "HarmonyLib.AccessTools" && mr.Name == "MethodDelegate" ? "uses AccessTools.MethodDelegate (works for most methods, but not with virtualCall: false)" :
                        null;
                    if (problem != null && seen.Add(problem))
                        yield return $"{type.FullName}.{method.Name} {problem}";
                }
            }
            foreach (var method in type.Methods)
            {
                if (method.CustomAttributes.Any(a => a.AttributeType.FullName == "HarmonyLib.HarmonyTranspiler") || method.Name == "Transpiler" && type.CustomAttributes.Any(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch"))
                {
                    string problem = "has a Harmony transpiler (IL rewrite), which will be skipped";
                    if (seen.Add(problem + type.FullName))
                        yield return $"{type.FullName}.{method.Name} {problem}";
                }
            }
        }
    }

    // Patchers run here under .NET 8, so references they import through reflection (typeof(...), MethodInfo)
    // point at .NET 8's System.Private.CoreLib/System.Runtime. Unity's runtime has those types in mscorlib.
    public static int RetargetCoreLib(ModuleDefinition module)
    {
        var modern = module.AssemblyReferences.Where(r => r.Name is "System.Private.CoreLib" or "System.Runtime" or "netstandard" && r.Version.Major >= 5).ToList();
        if (modern.Count == 0)
            return 0;
        var mscorlib = module.AssemblyReferences.FirstOrDefault(r => r.Name == "mscorlib");
        if (mscorlib == null)
        {
            mscorlib = new AssemblyNameReference("mscorlib", new Version(4, 0, 0, 0))
            {
                PublicKeyToken = new byte[] { 0xb7, 0x7a, 0x5c, 0x56, 0x19, 0x34, 0xe0, 0x89 },
            };
            module.AssemblyReferences.Add(mscorlib);
        }
        int count = 0;
        foreach (var type in module.GetTypeReferences())
        {
            if (type.Scope is AssemblyNameReference scope && modern.Contains(scope))
            {
                type.Scope = mscorlib;
                count++;
            }
        }
        foreach (var r in modern)
            module.AssemblyReferences.Remove(r);
        return count;
    }

    // string M(MethodBase m) => try { M__iosport_orig(m) } catch { m.Name }
    static void WrapWithFallback(MethodDefinition method, ModuleDefinition module)
    {
        var original = HookWeaver.Clone(method);
        method.DeclaringType.Methods.Add(original);
        var corlib = module.TypeSystem.CoreLibrary;
        var memberInfo = new TypeReference("System.Reflection", "MemberInfo", module, corlib);
        var getName = new MethodReference("get_Name", module.TypeSystem.String, memberInfo) { HasThis = true };
        var exception = new TypeReference("System", "Exception", module, corlib);

        var body = method.Body;
        body.Instructions.Clear();
        body.ExceptionHandlers.Clear();
        body.Variables.Clear();
        var result = new VariableDefinition(module.TypeSystem.String);
        body.Variables.Add(result);
        body.InitLocals = true;
        var il = body.GetILProcessor();
        var ret = il.Create(OpCodes.Ldloc, result);

        var tryStart = il.Create(OpCodes.Ldarg_0);
        il.Append(tryStart);
        il.Emit(OpCodes.Call, original);
        il.Emit(OpCodes.Stloc, result);
        il.Emit(OpCodes.Leave, ret);
        var handlerStart = il.Create(OpCodes.Pop);
        il.Append(handlerStart);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, getName);
        il.Emit(OpCodes.Stloc, result);
        il.Emit(OpCodes.Leave, ret);
        il.Append(ret);
        il.Emit(OpCodes.Ret);
        body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
        {
            CatchType = exception,
            TryStart = tryStart, TryEnd = handlerStart,
            HandlerStart = handlerStart, HandlerEnd = ret,
        });
    }

    static void ReplaceBody(MethodDefinition method, Action<ILProcessor> emit)
    {
        var body = method.Body;
        body.Instructions.Clear();
        body.ExceptionHandlers.Clear();
        body.Variables.Clear();
        emit(body.GetILProcessor());
    }
}
