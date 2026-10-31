using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Weaver;

// Works out which game methods the mods might patch with Harmony, so the build can give each one a hook point.
//  - [HarmonyPatch] attributes are read exactly.
//  - Manual harmony.Patch(...) calls and TargetMethod() can't be read statically, so as a fallback any
//    game method whose type the mod mentions (typeof / type-name string) and whose name appears as a
//    string in the mod (nameof, AccessTools.Method(..., "Name")) is treated as a possible target.
//    A spare hook point costs one array check per call, so over-including is cheap.
sealed class Discovery
{
    public readonly Dictionary<MethodDefinition, string> Targets = new();
    public readonly List<string> Warnings = new();

    readonly GameAssemblies game;

    public Discovery(GameAssemblies game) => this.game = game;

    public void Scan(ModuleDefinition mod)
    {
        string modName = mod.Assembly.Name.Name;
        ScanAttributes(mod, modName);
        ScanReferences(mod, modName);
    }

    // ---- [HarmonyPatch] attributes ----

    sealed class PatchSpec
    {
        public TypeDefinition? Type;
        public string? TypeName;
        public string? Method;
        public int MethodType; // HarmonyLib.MethodType: 0 Normal, 1 Getter, 2 Setter, 3 Constructor, 4 StaticConstructor, 5 Enumerator
        public List<TypeReference>? Args;

        public PatchSpec Merge(PatchSpec inner) => new()
        {
            Type = inner.Type ?? Type,
            TypeName = inner.TypeName ?? TypeName,
            Method = inner.Method ?? Method,
            MethodType = inner.MethodType != 0 ? inner.MethodType : MethodType,
            Args = inner.Args ?? Args,
        };
    }

    void ScanAttributes(ModuleDefinition mod, string modName)
    {
        foreach (var type in mod.GetTypes())
        {
            var classSpec = new PatchSpec();
            bool classPatched = false;
            foreach (var spec in PatchAttributes(type.CustomAttributes))
            {
                classSpec = classSpec.Merge(spec);
                classPatched = true;
            }
            if (!classPatched)
                continue;

            bool hasTargetMethod = type.Methods.Any(m => m.IsStatic && (m.Name == "TargetMethod" || m.Name == "TargetMethods"));
            var specs = new List<PatchSpec>();
            foreach (var method in type.Methods)
            {
                var methodSpecs = PatchAttributes(method.CustomAttributes).ToList();
                foreach (var ms in methodSpecs)
                    specs.Add(classSpec.Merge(ms));
            }
            if (specs.Count == 0)
                specs.Add(classSpec);

            foreach (var spec in specs)
            {
                var declaring = spec.Type ?? (spec.TypeName != null ? game.FindType(spec.TypeName) : null);
                if (declaring == null || (spec.Method == null && spec.MethodType is 0 or 5))
                {
                    if (!hasTargetMethod && declaring == null && spec.TypeName != null)
                        Warnings.Add($"{modName}: {type.FullName} patches type '{spec.TypeName}', which isn't in the game.");
                    continue; // TargetMethod()/incomplete spec: covered by the reference heuristic
                }
                foreach (var target in Resolve(declaring, spec))
                    Add(target, $"{modName}: [HarmonyPatch] {type.FullName}", exact: true);
            }
        }
    }

    IEnumerable<PatchSpec> PatchAttributes(IEnumerable<CustomAttribute> attributes)
    {
        foreach (var attr in attributes)
        {
            if (attr.AttributeType.FullName != "HarmonyLib.HarmonyPatch")
                continue;
            var spec = new PatchSpec();
            var strings = new List<string>();
            foreach (var arg in attr.ConstructorArguments)
            {
                switch (arg.Value)
                {
                    case TypeReference tr:
                        spec.Type ??= game.Resolve(tr);
                        if (spec.Type == null)
                            spec.TypeName = tr.FullName;
                        break;
                    case string s:
                        strings.Add(s);
                        break;
                    case CustomAttributeArgument[] array when array.All(a => a.Value is TypeReference):
                        spec.Args = array.Select(a => (TypeReference)a.Value).ToList();
                        break;
                    case int i when arg.Type.FullName == "HarmonyLib.MethodType":
                        spec.MethodType = i;
                        break;
                }
            }
            // (string typeName, string methodName) or (string methodName)
            if (strings.Count >= 2) { spec.TypeName = strings[0]; spec.Method = strings[1]; }
            else if (strings.Count == 1) spec.Method = strings[0];

            foreach (var named in attr.Properties.Concat(attr.Fields))
            {
                if (named.Name is "methodName" && named.Argument.Value is string m) spec.Method = m;
                if (named.Name is "declaringType" && named.Argument.Value is TypeReference t) spec.Type = game.Resolve(t);
            }
            yield return spec;
        }
    }

    IEnumerable<MethodDefinition> Resolve(TypeDefinition declaring, PatchSpec spec)
    {
        string? name = spec.MethodType switch
        {
            1 => "get_" + spec.Method,
            2 => "set_" + spec.Method,
            3 => ".ctor",
            4 => ".cctor",
            _ => spec.Method,
        };
        var found = FindMethods(declaring, name!).ToList();
        if (spec.Args != null)
        {
            var exact = found.Where(m => m.Parameters.Count == spec.Args.Count &&
                m.Parameters.Select(p => Normalize(p.ParameterType.FullName)).SequenceEqual(spec.Args.Select(a => Normalize(a.FullName)))).ToList();
            if (exact.Count > 0)
                found = exact;
        }
        if (found.Count == 0)
        {
            Warnings.Add($"Patch target {declaring.FullName}.{name} not found in the game.");
            yield break;
        }
        foreach (var method in found)
        {
            if (spec.MethodType == 5)
            {
                var stateMachine = StateMachineMoveNext(method);
                if (stateMachine != null)
                    yield return stateMachine;
                continue;
            }
            yield return method;
        }
    }

    // ---- references heuristic ----

    // Pairs a type and a method name that appear in the same class (including its lambdas/nested classes), e.g.
    //   harmony.Patch(AccessTools.Method(typeof(HeroController), "TakeDamage"), ...)
    //   static readonly string[] Setters = { "SetBool", "SetInt" }; ... typeof(PlayerData).GetMethod(name)
    //   static MethodBase TargetMethod() => typeof(X).GetMethod(nameof(X.Foo));
    void ScanReferences(ModuleDefinition mod, string modName)
    {
        foreach (var group in mod.GetTypes().GroupBy(Outermost))
        {
            var types = new HashSet<TypeDefinition>();
            var strings = new HashSet<string>(StringComparer.Ordinal);
            foreach (var type in group)
            {
                foreach (var attr in type.CustomAttributes.Concat(type.Methods.SelectMany(m => m.CustomAttributes)))
                    foreach (var arg in attr.ConstructorArguments)
                    {
                        if (arg.Value is TypeReference tr && game.Resolve(tr) is { } td) types.Add(td);
                        if (arg.Value is string str) strings.Add(str);
                    }

                foreach (var method in type.Methods.Where(m => m.HasBody))
                {
                    foreach (var ins in method.Body.Instructions)
                    {
                        switch (ins.Operand)
                        {
                            case string str:
                                strings.Add(str);
                                break;
                            case TypeReference tr when ins.OpCode == OpCodes.Ldtoken:
                                if (game.Resolve(tr) is { } td) types.Add(td);
                                break;
                            case MethodReference mr when ins.OpCode == OpCodes.Ldtoken:
                                // methodof via expression trees / SymbolExtensions.GetMethodInfo(() => x.Foo())
                                if (game.Resolve(mr) is { } md) Add(md, $"{modName}: method token in {type.FullName}", exact: false);
                                break;
                        }
                    }
                }
            }

            // Type names as strings: AccessTools.TypeByName("HeroController"), AccessTools.Method("HeroController:TakeDamage")
            foreach (var str in strings.ToList())
            {
                int colon = str.IndexOf(':');
                string typePart = colon > 0 ? str[..colon] : str;
                if (typePart.Length > 0 && game.FindType(typePart) is { } td)
                {
                    types.Add(td);
                    if (colon > 0)
                        strings.Add(str[(colon + 1)..]);
                }
            }

            if (types.Count == 0 || strings.Count == 0)
                continue;
            foreach (var t in types)
            {
                foreach (var name in strings)
                {
                    if (name.Length == 0 || name.Length > 128)
                        continue;
                    foreach (var m in FindMethods(t, name).Concat(FindMethods(t, "get_" + name)).Concat(FindMethods(t, "set_" + name)))
                        Add(m, $"{modName}: {group.Key.Name} mentions {t.Name} and \"{name}\"", exact: false);
                }
            }
        }
    }

    static TypeDefinition Outermost(TypeDefinition type)
    {
        while (type.DeclaringType != null)
            type = type.DeclaringType;
        return type;
    }

    // ---- helpers ----

    IEnumerable<MethodDefinition> FindMethods(TypeDefinition type, string name)
    {
        // Harmony resolves names on the given type and its base types.
        for (var t = type; t != null; t = t.BaseType == null ? null : game.Resolve(t.BaseType))
        {
            bool any = false;
            foreach (var m in t.Methods.Where(m => m.Name == name))
            {
                any = true;
                yield return m;
            }
            if (any)
                yield break;
        }
    }

    static MethodDefinition? StateMachineMoveNext(MethodDefinition method)
    {
        var attr = method.CustomAttributes.FirstOrDefault(a =>
            a.AttributeType.FullName is "System.Runtime.CompilerServices.IteratorStateMachineAttribute" or "System.Runtime.CompilerServices.AsyncStateMachineAttribute");
        var smType = (attr?.ConstructorArguments[0].Value as TypeReference)?.Resolve();
        return smType?.Methods.FirstOrDefault(m => m.Name == "MoveNext" || m.Name.EndsWith(".MoveNext"));
    }

    void Add(MethodDefinition method, string reason, bool exact)
    {
        string? problem = HookWeaver.CantHook(method, game);
        if (problem != null)
        {
            if (exact)
                Warnings.Add($"{reason} -> {method.DeclaringType.FullName}.{method.Name}: {problem}; this patch won't run on iOS.");
            return;
        }
        if (!Targets.ContainsKey(method) || exact)
            Targets[method] = reason;
    }

    internal static string Normalize(string typeName) => typeName.Replace('/', '+');
}
