using Mono.Cecil;

namespace Weaver;

// The game's assemblies as seen at build time, plus which of them can take hook points.
sealed class GameAssemblies : IDisposable
{
    // Precompiled game DLLs in Assets/Plugins that mods may patch. Platform SDKs, Burst and
    // System libraries are left alone.
    static readonly string[] NotWeavable =
    {
        "System.", "SharpDX", "GalaxyCSharp", "com.rlabrecque", "XGamingRuntime", "XblPCSandbox", "zlib",
        "Unity.Burst", "Unity.Collections", "Unity.Mathematics", "Unity.MemoryProfiler", "Unity.Recorder", "TeamCherry.BuildBot",
    };

    public readonly Resolver Resolver = new();
    public readonly Dictionary<string, AssemblyDefinition> Weavable = new(StringComparer.OrdinalIgnoreCase);
    // Unity engine modules (the iOS player versions). Their methods can't be modified, but calls to them can be.
    public readonly Dictionary<string, AssemblyDefinition> Engine = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, TypeDefinition> typesByName = new(StringComparer.Ordinal);

    public GameAssemblies(string projectDir, IEnumerable<string> extraSearchDirs)
    {
        string scripts = Path.Combine(projectDir, "Library", "ScriptAssemblies");
        string plugins = Path.Combine(projectDir, "Assets", "Plugins");
        foreach (var dir in new[] { scripts, plugins }.Concat(extraSearchDirs))
        {
            if (Directory.Exists(dir))
                Resolver.AddSearchDirectory(dir);
        }

        var files = new List<string>
        {
            Path.Combine(scripts, "Assembly-CSharp.dll"),
            Path.Combine(scripts, "Assembly-CSharp-firstpass.dll"),
        };
        files.AddRange(Directory.GetFiles(plugins, "*.dll").Where(IsWeavableFile));
        foreach (var file in files.Where(File.Exists))
            Weavable[Load(file).Name.Name] = Load(file);
        foreach (var file in Directory.GetFiles(Prepare.UnityPlayerManaged(), "UnityEngine.*Module.dll"))
        {
            var asm = Load(file);
            Engine[asm.Name.Name] = asm;
        }
    }

    readonly Dictionary<string, AssemblyDefinition> loaded = new(StringComparer.OrdinalIgnoreCase);

    AssemblyDefinition Load(string file)
    {
        if (loaded.TryGetValue(file, out var existing))
            return existing;
        var asm = AssemblyDefinition.ReadAssembly(file, new ReaderParameters { AssemblyResolver = Resolver });
        Resolver.Register(asm);
        loaded[file] = asm;
        foreach (var type in asm.MainModule.GetTypes())
        {
            typesByName.TryAdd(Discovery.Normalize(type.FullName), type);
            if (type.DeclaringType == null)
                typesByName.TryAdd(type.Name, type); // AccessTools.TypeByName also accepts short names
        }
        return asm;
    }

    public static bool IsWeavableFile(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        return !NotWeavable.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    public bool IsWeavable(TypeDefinition type) => Weavable.ContainsKey(type.Module.Assembly.Name.Name);
    public bool IsEngine(TypeDefinition type) => Engine.ContainsKey(type.Module.Assembly.Name.Name);

    public TypeDefinition? FindType(string name) =>
        typesByName.TryGetValue(Discovery.Normalize(name), out var t) ? t : null;

    public TypeDefinition? Resolve(TypeReference type)
    {
        try
        {
            var element = type is TypeSpecification spec ? spec.GetElementType() : type;
            var resolved = element.Resolve();
            if (resolved == null)
                return null;
            // Map onto our loaded copy so all results come from the same AssemblyDefinition.
            return FindType(resolved.FullName) is { } ours && ours.Module.Assembly.Name.Name == resolved.Module.Assembly.Name.Name ? ours : null;
        }
        catch (AssemblyResolutionException)
        {
            return null;
        }
    }

    public MethodDefinition? Resolve(MethodReference method)
    {
        var type = Resolve(method.DeclaringType);
        if (type == null)
            return null;
        try
        {
            var resolved = method.Resolve();
            return resolved == null ? null : type.Methods.FirstOrDefault(m => m.FullName == resolved.FullName);
        }
        catch (AssemblyResolutionException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        foreach (var asm in loaded.Values)
            asm.Dispose();
    }
}

sealed class Resolver : DefaultAssemblyResolver
{
    public Resolver()
    {
        // Engine modules as the iOS player has them (the editor's copies have extra editor-only API).
        AddSearchDirectory(Prepare.UnityPlayerManaged());
        // UnityEngine.dll (the facade) sits one level above the module DLLs.
        AddSearchDirectory(Path.GetDirectoryName(Prepare.UnityManaged())!);
        AddSearchDirectory(Prepare.UnityAot());
        AddSearchDirectory(Path.Combine(Prepare.UnityAot(), "Facades"));
    }

    public void Register(AssemblyDefinition assembly) => RegisterAssembly(assembly);

    // Cecil 0.10's GAC fallback throws on macOS; treat that as "not found".
    public override AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
    {
        try
        {
            return base.Resolve(name, parameters);
        }
        catch (Exception e) when (e is not AssemblyResolutionException)
        {
            throw new AssemblyResolutionException(name);
        }
    }
}
