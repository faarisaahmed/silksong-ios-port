using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using Mono.Cecil;

namespace Weaver;

// Runs BepInEx preloader patchers (BepInEx/patchers/*.dll) against game DLLs at build time, the same
// way BepInEx's preloader would at game start.
static class Patchers
{
    sealed class Patcher
    {
        public required string Name;
        public required HashSet<string> Targets;
        public required MethodInfo Patch;
        public MethodInfo? Finish;
    }

    sealed class ConsoleListener : ILogListener
    {
        public void LogEvent(object sender, LogEventArgs e) => Console.WriteLine($"    [{e.Source.SourceName}] {e.Data}");
        public void Dispose() { }
    }

    static List<Patcher>? loaded;
    static bool initialized;

    public static void Init(string patchersDir, string pluginsDir, string workDir, string managedDir)
    {
        SetPath("BepInExRootPath", workDir);
        SetPath("PluginPath", pluginsDir);
        SetPath("PatcherPluginPath", patchersDir);
        SetPath("CachePath", Path.Combine(workDir, "cache"));
        SetPath("ConfigPath", Path.Combine(workDir, "config"));
        SetPath("BepInExConfigPath", Path.Combine(workDir, "config", "BepInEx.cfg"));
        SetPath("GameRootPath", workDir);
        SetPath("ManagedPath", managedDir);
        if (initialized)
            return;
        initialized = true;
        Directory.CreateDirectory(Path.Combine(workDir, "cache"));
        Directory.CreateDirectory(Path.Combine(workDir, "config"));
        Logger.Listeners.Add(new ConsoleListener());

        loaded = new List<Patcher>();
        if (!Directory.Exists(patchersDir))
            return;

        // Patchers are built against whatever Mono.Cecil/MonoMod version they liked; BepInEx's Mono runtime
        // doesn't care about versions, so hand them the copies already loaded (or one from their folder).
        System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            var match = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == name.Name);
            if (match != null)
                return match;
            var file = new[] { Path.Combine(AppContext.BaseDirectory, name.Name + ".dll") }.Where(File.Exists)
                .Concat(Directory.GetFiles(patchersDir, name.Name + ".dll", SearchOption.AllDirectories)).FirstOrDefault();
            return file != null ? context.LoadFromAssemblyPath(file) : null;
        };
        foreach (var file in Directory.GetFiles(patchersDir, "*.dll", SearchOption.AllDirectories))
        {
            Assembly asm;
            try { asm = Assembly.LoadFrom(file); }
            catch (Exception e) { Console.WriteLine($"  ! patcher {Path.GetFileName(file)} couldn't be loaded: {e.Message}"); continue; }

            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray()!; }
            foreach (var type in types)
            {
                var targets = type.GetProperty("TargetDLLs", BindingFlags.Public | BindingFlags.Static);
                var patch = type.GetMethods(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(m => m.Name == "Patch" && m.GetParameters().Length == 1
                    && (m.GetParameters()[0].ParameterType == typeof(AssemblyDefinition) || m.GetParameters()[0].ParameterType == typeof(AssemblyDefinition).MakeByRefType()));
                if (targets == null || patch == null)
                    continue;
                try
                {
                    type.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static, Type.EmptyTypes)?.Invoke(null, null);
                    var names = ((IEnumerable<string>)targets.GetValue(null)!).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    loaded.Add(new Patcher
                    {
                        Name = type.FullName!,
                        Targets = names,
                        Patch = patch,
                        Finish = type.GetMethod("Finish", BindingFlags.Public | BindingFlags.Static, Type.EmptyTypes),
                    });
                    Console.WriteLine($"  patcher {type.FullName} -> {string.Join(", ", names)}");
                }
                catch (Exception e)
                {
                    Console.WriteLine($"  ! patcher {type.FullName} failed to start: {(e.InnerException ?? e).Message}");
                }
            }
        }
    }

    public static bool Targets(string dllFileName) => loaded != null && loaded.Any(p => p.Targets.Contains(dllFileName));

    public static void Apply(AssemblyDefinition assembly, string dllFileName)
    {
        foreach (var patcher in loaded!.Where(p => p.Targets.Contains(dllFileName)))
        {
            Console.WriteLine($"  {patcher.Name} patching {dllFileName}");
            var args = new object[] { assembly };
            try { patcher.Patch.Invoke(null, args); }
            catch (Exception e) { throw new Exception($"patcher {patcher.Name} failed on {dllFileName}: {(e.InnerException ?? e)}"); }
        }
    }

    public static void Finish()
    {
        foreach (var patcher in loaded ?? new List<Patcher>())
        {
            try { patcher.Finish?.Invoke(null, null); }
            catch (Exception e) { Console.WriteLine($"  ! {patcher.Name}.Finish failed: {(e.InnerException ?? e).Message}"); }
        }
    }

    static void SetPath(string property, string value) =>
        typeof(Paths).GetProperty(property, BindingFlags.Static | BindingFlags.Public)!.GetSetMethod(true)!.Invoke(null, new object[] { value });
}
