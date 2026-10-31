using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mono.Cecil;

namespace Weaver;

// `prepare`: turns a BepInEx folder into inputs for the Unity iOS build.
//   Assets/IOSPortMods/Plugins/*.dll        mods, patcher DLLs, BepInEx/Harmony (iOS-only plugins)
//   Assets/IOSPortMods/link.xml             keeps them from being stripped
//   Assets/StreamingAssets/BepInEx/...      mod data files + configs, copied to Documents/BepInEx on the phone
//   IOSPortMods/hooks.tsv, weaver.json      what `weave` adds to the game DLLs during the build
static class Prepare
{
    // BepInEx core files are supplied by the tool (Preloader isn't used on iOS).
    static readonly string[] CoreAssemblies =
    {
        "BepInEx", "0Harmony", "Mono.Cecil", "Mono.Cecil.Rocks", "Mono.Cecil.Mdb", "Mono.Cecil.Pdb",
        "MonoMod.Utils", "MonoMod.RuntimeDetour", "HarmonyXInterop", "BepInEx.Harmony", "0Harmony20", "BepInEx.Preloader",
    };
    static readonly string[] OptionalCore = { "HarmonyXInterop", "BepInEx.Harmony", "0Harmony20" };

    // Folders mods write their own output into; not worth shipping in the app.
    static readonly string[] SkippedFolders = { "Dumps", "TextDumps", "cache", "Cache", "logs", "Logs", "__MACOSX" };
    static readonly string[] SkippedExtensions = { ".pdb", ".mdb", ".DS_Store", ".bak", ".log" };

    sealed class ModAssembly
    {
        public required string Name;
        public required string SourcePath;
        public required string RelativePath; // inside BepInEx/plugins (or patchers)
        public required AssemblyDefinition Assembly;
        public bool IsPatcher;
        public string? Excluded;
    }

    public static int Run(Options o)
    {
        string bepinex = o.Require("bepinex");
        string project = o.Require("project");
        string runtimePath = o.Require("runtime");
        string vendor = o.Require("vendor");
        string weaverDll = o.Require("weaver");

        string pluginsDir = Path.Combine(bepinex, "plugins");
        string patchersDir = Path.Combine(bepinex, "patchers");
        string configDir = Path.Combine(bepinex, "config");
        string assetsOut = Path.Combine(project, "Assets", "IOSPortMods");
        string streamingOut = Path.Combine(project, "Assets", "StreamingAssets", "BepInEx");
        string stateOut = Path.Combine(project, "IOSPortMods");

        Clean(project);

        var report = new Report();
        using var game = new GameAssemblies(project, new[] { vendor, Path.GetDirectoryName(runtimePath)!, UnityManaged(), UnityAot() });
        var mods = Collect(pluginsDir, false, report, game.Resolver).Concat(Collect(patchersDir, true, report, game.Resolver)).ToList();
        foreach (var mod in mods)
            game.Resolver.Register(mod.Assembly);

        // A mod can only go in if everything it references will be in the build.
        var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        available.UnionWith(KnownAssemblies(project));
        available.UnionWith(CoreAssemblies);
        available.UnionWith(mods.Select(m => m.Name));
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var mod in mods.Where(m => m.Excluded == null))
            {
                var missing = mod.Assembly.MainModule.AssemblyReferences.Select(r => r.Name).Where(n => !available.Contains(n)).ToList();
                if (missing.Count == 0)
                    continue;
                bool bepinex6 = missing.Any(n => n.StartsWith("BepInEx.Core") || n.StartsWith("BepInEx.Unity"));
                mod.Excluded = bepinex6
                    ? "it's a BepInEx 6 mod; only BepInEx 5 mods are supported"
                    : "it needs " + string.Join(", ", missing) + ", which isn't in your BepInEx folder";
                available.Remove(mod.Name);
                changed = true;
            }
        }

        var included = mods.Where(m => m.Excluded == null).ToList();
        var discovery = new Discovery(game);
        foreach (var mod in included.Where(m => !m.IsPatcher))
        {
            discovery.Scan(mod.Assembly.MainModule);
            foreach (var problem in Rewrites.Problems(mod.Assembly.MainModule))
                report.Warn(mod.Name, problem);
        }
        foreach (var w in discovery.Warnings)
            report.Warn("hooks", w);

        // Hooks requested from the phone (BepInEx/iosport/missing-hooks.txt copied back into the BepInEx folder).
        foreach (var file in new[] { Path.Combine(bepinex, "missing-hooks.txt"), Path.Combine(bepinex, "iosport", "missing-hooks.txt") }.Where(File.Exists))
            AddRequestedHooks(file, game, discovery, report);

        // ---- write outputs ----
        Directory.CreateDirectory(Path.Combine(assetsOut, "Plugins"));
        Directory.CreateDirectory(Path.Combine(streamingOut, "iosport"));
        Directory.CreateDirectory(stateOut);

        var runtime = AssemblyDefinition.ReadAssembly(runtimePath, new ReaderParameters { AssemblyResolver = game.Resolver });
        var pluginFiles = new List<string>();

        // Hook manifest: ids are global across assemblies.
        var targets = discovery.Targets.OrderBy(t => t.Key.Module.Assembly.Name.Name).ThenBy(t => t.Key.FullName).ToList();
        var entries = targets.Select((t, i) => new HookWeaver.Entry(i, HookWeaver.KindOf(t.Key, game),
            t.Key.Module.Assembly.Name.Name, t.Key.DeclaringType.FullName, t.Key.FullName)).ToList();
        File.WriteAllLines(Path.Combine(stateOut, "hooks.tsv"),
            entries.Select(e => string.Join('\t', e.Id, e.Assembly, e.Type, e.Method, targets[e.Id].Value, e.Kind)));

        // Engine methods are hooked at their call sites, in game DLLs and in the mods themselves.
        var callTargets = entries.Where(e => e.Kind == HookWeaver.Kind.Call).Select(e => e.Method).ToHashSet();
        var callingAssemblies = game.Weavable.Values.Where(a => HookWeaver.HasCallSites(a.MainModule, callTargets))
            .Select(a => a.Name.Name).ToList();
        var wovenMods = new List<string>();
        foreach (var e in entries.Where(e => e.Kind == HookWeaver.Kind.Call))
        {
            var one = new HashSet<string> { e.Method };
            if (!game.Weavable.Values.Any(a => HookWeaver.HasCallSites(a.MainModule, one)) && !included.Any(m => HookWeaver.HasCallSites(m.Assembly.MainModule, one)))
            {
                string reason = targets[e.Id].Value;
                if (reason.Contains("[HarmonyPatch]") || !reason.Contains("mentions"))
                    report.Warn("hooks", $"{e.Method} is a Unity engine function that game code never calls directly (only the engine does), so {reason.Split(':')[0]}'s patch on it won't run.");
            }
        }

        foreach (var mod in included)
        {
            if (!mod.IsPatcher && HookWeaver.HasCallSites(mod.Assembly.MainModule, callTargets))
            {
                var warnings = new List<string>();
                HookWeaver.Weave(mod.Assembly, runtime.MainModule, entries.Where(e => e.Kind == HookWeaver.Kind.Call), warnings);
                wovenMods.Add(mod.Name);
            }
            var unresolvedRefs = new List<string>();
            int fieldRefs = Rewrites.FieldRefs(mod.Assembly.MainModule, unresolvedRefs);
            if (fieldRefs > 0)
                report.Info(mod.Name, $"pre-built {fieldRefs} AccessTools.FieldRefAccess accessor(s)");
            foreach (var u in unresolvedRefs)
                report.Warn(mod.Name, $"AccessTools.FieldRefAccess for {u} can't be pre-built; it will fail on iOS");
            int fixedLocations = Rewrites.PatchMod(mod.Assembly, runtime.MainModule);
            string target = Path.Combine(assetsOut, "Plugins", mod.Name + ".dll");
            mod.Assembly.Write(target);
            pluginFiles.Add(target);
            if (fixedLocations > 0)
                report.Info(mod.Name, $"adjusted {fixedLocations} call(s) for iOS (Assembly.Location / PatchAll)");
        }

        var referenced = included.SelectMany(m => m.Assembly.MainModule.AssemblyReferences.Select(r => r.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var core in CoreAssemblies.Where(c => c != "BepInEx.Preloader"))
        {
            string src = Path.Combine(vendor, core + ".dll");
            if (!File.Exists(src) || OptionalCore.Contains(core) && !referenced.Contains(core))
                continue;
            string target = Path.Combine(assetsOut, "Plugins", core + ".dll");
            if (core == "0Harmony")
            {
                using var harmony = AssemblyDefinition.ReadAssembly(src, new ReaderParameters { AssemblyResolver = game.Resolver });
                Rewrites.PatchHarmony(harmony, runtime.MainModule);
                harmony.Write(target);
            }
            else
            {
                File.Copy(src, target, true);
            }
            pluginFiles.Add(target);
        }
        string runtimeTarget = Path.Combine(assetsOut, "Plugins", Path.GetFileName(runtimePath));
        File.Copy(runtimePath, runtimeTarget, true);
        pluginFiles.Add(runtimeTarget);

        foreach (var file in pluginFiles)
            UnityMeta.WritePlugin(file);
        WriteLinkXml(Path.Combine(assetsOut, "link.xml"), pluginFiles.Select(Path.GetFileNameWithoutExtension)!);

        // Mod data files and configs.
        int dataFiles = CopyData(pluginsDir, Path.Combine(streamingOut, "plugins"), report);
        int scaled = SkinScaler.ScaleToBuild(Path.Combine(streamingOut, "plugins"), project);
        if (scaled > 0)
            report.Info("skins", $"scaled {scaled} sprite image(s) to match the iPhone build's reduced-size sprite atlases");
        if (Directory.Exists(configDir))
            dataFiles += CopyData(configDir, Path.Combine(streamingOut, "config"), report);
        File.WriteAllLines(Path.Combine(streamingOut, "iosport", "plugins.txt"),
            included.Where(m => !m.IsPatcher).Select(m => m.Name + "|" + m.RelativePath));
        File.WriteAllText(Path.Combine(streamingOut, "iosport", "build-stamp.txt"), DateTime.UtcNow.ToString("o"));
        File.WriteAllText(Path.Combine(streamingOut, "README.txt"),
            "Mods in this app were built in by the ./build tool. Configs in BepInEx/config can be edited here;\n" +
            "plugin files are refreshed from the app on each new build. Log: BepInEx/LogOutput.log\n");

        Patchers.Init(patchersDir, pluginsDir, Path.Combine(stateOut, "patcher-work"), Path.Combine(project, "Assets", "Plugins"));
        var toWeave = entries.Where(e => e.Kind == HookWeaver.Kind.Body).Select(e => e.Assembly)
            .Concat(callingAssemblies)
            .Concat(game.Weavable.Keys.Where(n => Patchers.Targets(n + ".dll")))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n).ToList();
        File.WriteAllLines(Path.Combine(streamingOut, "iosport", "hook-assemblies.txt"), toWeave.Concat(wovenMods));

        string pluginsFolder = Path.Combine(project, "Assets", "Plugins");
        File.WriteAllText(Path.Combine(stateOut, "weaver.json"), JsonSerializer.Serialize(new WeaverConfig
        {
            Weaver = weaverDll,
            Runtime = runtimePath,
            Vendor = vendor,
            Bepinex = bepinex,
            Assemblies = toWeave,
            Precompiled = toWeave.Where(n => File.Exists(Path.Combine(pluginsFolder, n + ".dll"))).ToList(),
        }, new JsonSerializerOptions { WriteIndented = true }));

        // ---- report ----
        Console.WriteLine();
        Console.WriteLine($"Mods going into the build ({included.Count(m => !m.IsPatcher)}):");
        foreach (var mod in included.Where(m => !m.IsPatcher))
            Console.WriteLine($"  + {mod.RelativePath}");
        foreach (var mod in included.Where(m => m.IsPatcher))
            Console.WriteLine($"  + patcher {mod.RelativePath}");
        foreach (var mod in mods.Where(m => m.Excluded != null))
            Console.WriteLine($"  - {mod.RelativePath}: left out, {mod.Excluded}");
        int engineCount = entries.Count(e => e.Kind == HookWeaver.Kind.Call);
        Console.WriteLine($"Hook points: {entries.Count - engineCount} game methods + {engineCount} Unity engine functions (at their call sites)");
        Console.WriteLine($"Game DLLs to modify: {string.Join(", ", toWeave)}");
        Console.WriteLine($"Data files: {dataFiles}");
        report.Print();
        return 0;
    }

    static List<ModAssembly> Collect(string dir, bool patchers, Report report, IAssemblyResolver resolver)
    {
        var result = new Dictionary<string, ModAssembly>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(dir))
            return new List<ModAssembly>();
        foreach (var file in Directory.GetFiles(dir, "*.dll", SearchOption.AllDirectories).OrderBy(f => f))
        {
            string relative = Path.GetRelativePath(dir, file);
            AssemblyDefinition asm;
            try
            {
                asm = AssemblyDefinition.ReadAssembly(file, new ReaderParameters { InMemory = true, AssemblyResolver = resolver });
            }
            catch (BadImageFormatException)
            {
                report.Warn(relative, "is a native (Windows) library; it can't run on iPhone and was skipped");
                continue;
            }
            string name = asm.Name.Name;
            if (CoreAssemblies.Contains(name, StringComparer.OrdinalIgnoreCase))
                continue;
            if (result.TryGetValue(name, out var existing))
            {
                if (existing.Assembly.Name.Version >= asm.Name.Version)
                    continue;
            }
            result[name] = new ModAssembly { Name = name, SourcePath = file, RelativePath = relative, Assembly = asm, IsPatcher = patchers };
        }
        return result.Values.ToList();
    }

    // Assemblies the build already has: game scripts/plugins, Unity engine modules, the .NET profile.
    static IEnumerable<string> KnownAssemblies(string project)
    {
        var dirs = new[]
        {
            Path.Combine(project, "Library", "ScriptAssemblies"),
            Path.Combine(project, "Assets", "Plugins"),
            UnityManaged(),
            UnityAot(),
            Path.Combine(UnityAot(), "Facades"),
        };
        foreach (var dir in dirs.Where(Directory.Exists))
            foreach (var f in Directory.GetFiles(dir, "*.dll"))
                yield return Path.GetFileNameWithoutExtension(f);
        yield return "UnityEngine";
        yield return "netstandard";
    }

    public static string UnityRoot() =>
        Environment.GetEnvironmentVariable("UNITY_EDITOR_APP") ?? "/Applications/Unity/Hub/Editor/6000.0.50f1/Unity.app";
    public static string UnityPlayerManaged() =>
        Path.Combine(Path.GetDirectoryName(UnityRoot())!, "PlaybackEngines", "iOSSupport", "Variations", "il2cpp", "Managed");
    public static string UnityManaged() => Path.Combine(UnityRoot(), "Contents", "Managed", "UnityEngine");
    public static string UnityAot() => Path.Combine(UnityRoot(), "Contents", "MonoBleedingEdge", "lib", "mono", "unityaot-macos");

    static void AddRequestedHooks(string file, GameAssemblies game, Discovery discovery, Report report)
    {
        // Lines look like "Assembly-CSharp|HeroController|TakeDamage(System.Int32,...)" (HookRegistry.Describe)
        foreach (var line in File.ReadAllLines(file).Select(l => l.Trim()).Where(l => l.Length > 0))
        {
            var parts = line.Split('|');
            if (parts.Length < 3) continue;
            var type = game.FindType(parts[1]);
            string name = parts[2].Split('(')[0];
            var method = type?.Methods.FirstOrDefault(m => m.Name == name);
            if (method == null)
            {
                report.Warn("missing-hooks.txt", $"{line}: not found in the game");
                continue;
            }
            string? problem = HookWeaver.CantHook(method, game);
            if (problem != null)
                report.Warn("missing-hooks.txt", $"{line}: {problem}");
            else
                foreach (var overload in type!.Methods.Where(m => m.Name == name && HookWeaver.CantHook(m, game) == null))
                    discovery.Targets[overload] = "requested by the phone (missing-hooks.txt)";
        }
    }

    static int CopyData(string from, string to, Report report)
    {
        if (!Directory.Exists(from))
            return 0;
        int count = 0;
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(from, file);
            var parts = relative.Split(Path.DirectorySeparatorChar);
            if (parts.Take(parts.Length - 1).Any(p => SkippedFolders.Contains(p)))
                continue;
            string name = Path.GetFileName(file);
            if (name.StartsWith('.') || SkippedExtensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                continue; // compiled into the app instead
            if (name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.ChangeExtension(file, ".dll")))
                continue; // API docs
            if (name.EndsWith(".assetbundle", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase))
                report.Warn(relative, "is a Unity asset bundle built for PC; Unity can't load PC bundles on iOS, so whatever's in it won't show up");
            string target = Path.Combine(to, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
            count++;
        }
        return count;
    }

    static void WriteLinkXml(string path, IEnumerable<string> assemblies)
    {
        var sb = new StringBuilder("<linker>\n");
        foreach (var a in assemblies.OrderBy(a => a))
            sb.Append($"  <assembly fullname=\"{a}\" preserve=\"all\"/>\n");
        sb.Append("</linker>\n");
        File.WriteAllText(path, sb.ToString());
    }

    public static void Clean(string project)
    {
        foreach (var path in new[]
        {
            Path.Combine(project, "Assets", "IOSPortMods"),
            Path.Combine(project, "Assets", "StreamingAssets", "BepInEx"),
            Path.Combine(project, "IOSPortMods"),
        })
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
            if (File.Exists(path + ".meta")) File.Delete(path + ".meta");
        }
    }
}

sealed class WeaverConfig
{
    public string Weaver { get; set; } = "";
    public string Runtime { get; set; } = "";
    public string Vendor { get; set; } = "";
    public string Bepinex { get; set; } = "";
    public List<string> Assemblies { get; set; } = new();
    public List<string> Precompiled { get; set; } = new();
}

sealed class Report
{
    readonly List<string> warnings = new();
    readonly List<string> infos = new();
    public void Warn(string who, string what) => warnings.Add($"{who}: {what}");
    public void Info(string who, string what) => infos.Add($"{who}: {what}");

    public void Print()
    {
        foreach (var i in infos.Distinct())
            Console.WriteLine("  i " + i);
        if (warnings.Count == 0)
            return;
        Console.WriteLine();
        Console.WriteLine("Heads up:");
        foreach (var w in warnings.Distinct())
            Console.WriteLine("  ! " + w);
    }
}

static class UnityMeta
{
    // iOS-only plugin, not auto-referenced by game scripts, no reference validation (mods reference Assembly-CSharp).
    public static void WritePlugin(string dllPath)
    {
        string guid = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes("iosportmods:" + Path.GetFileName(dllPath)))).ToLowerInvariant();
        File.WriteAllText(dllPath + ".meta", $@"fileFormatVersion: 2
guid: {guid}
PluginImporter:
  externalObjects: {{}}
  serializedVersion: 2
  iconMap: {{}}
  executionOrder: {{}}
  defineConstraints: []
  isPreloaded: 0
  isOverridable: 0
  isExplicitlyReferenced: 1
  validateReferences: 0
  platformData:
  - first:
      Any:
    second:
      enabled: 0
      settings: {{}}
  - first:
      Editor: Editor
    second:
      enabled: 0
      settings:
        DefaultValueInitialized: true
  - first:
      iPhone: iOS
    second:
      enabled: 1
      settings:
        AddToEmbeddedBinaries: false
        CPU: AnyCPU
        CompileFlags:
        FrameworkDependencies:
  userData:
  assetBundleName:
  assetBundleVariant:
");
    }
}
