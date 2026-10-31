using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using UnityEngine;
using Logger = BepInEx.Logging.Logger;

namespace IOSPort.Mods
{
    // Stands in for BepInEx's preloader + Chainloader on iOS. The mods are compiled into the app,
    // so instead of loading DLLs from disk this finds their plugin classes and starts them the same way.
    //
    // Files: the app bundle carries the mods' data under StreamingAssets/BepInEx. On first launch (and
    // after each new build) those are copied to Documents/BepInEx, which shows up in the Files app,
    // so configs and skin files can be edited on the phone.
    public static class ModLoader
    {
        const string ManifestFile = "iosport/plugins.txt";
        const string HookAssembliesFile = "iosport/hook-assemblies.txt";
        const string StampFile = "iosport/build-stamp.txt";

        static ManualLogSource log;
        static readonly Dictionary<string, string> assemblyLocations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static string RootPath { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Start()
        {
            string bundled = Path.Combine(Application.streamingAssetsPath, "BepInEx");
            if (!File.Exists(Path.Combine(bundled, ManifestFile)))
                return;

            try
            {
                RootPath = Path.Combine(Application.persistentDataPath, "BepInEx");
                Directory.CreateDirectory(RootPath);
                SetPaths(RootPath);
                SyncBundledFiles(bundled, RootPath);
                Logger.Listeners.Add(new DiskLogListener("LogOutput.log", LogLevel.All));
                Logger.Listeners.Add(new DebugLogListener());
                AddInternalLogSource("BepInEx.Logging.HarmonyLogSource"); // HarmonyX warnings, as on PC
                log = Logger.CreateLogSource("BepInEx");
                typeof(ThreadingHelper).GetMethod("Initialize", BindingFlags.Static | BindingFlags.NonPublic)?.Invoke(null, null);
                log.LogMessage("BepInEx 5 (iOS hardcoded mods) - " + Application.productName + " " + Application.version);

                RegisterHooks(bundled);
                LoadPlugins(bundled);
            }
            catch (Exception e)
            {
                Debug.LogError("[iOS Mods] startup failed: " + e);
                log?.LogFatal(e);
            }
        }

        // Replacement for Assembly.Location in mod code (always "" on iOS): points into Documents/BepInEx/plugins.
        public static string AssemblyLocation(Assembly assembly)
        {
            if (assembly == null)
                return "";
            string name = assembly.GetName().Name;
            if (assemblyLocations.TryGetValue(name, out string path))
                return path;
            return Path.Combine(Paths.PluginPath ?? "", name + ".dll");
        }

        static void SetPaths(string root)
        {
            void Set(string property, object value) =>
                typeof(Paths).GetProperty(property, BindingFlags.Static | BindingFlags.Public)?.GetSetMethod(true)?.Invoke(null, new[] { value });

            string gameRoot = Path.GetDirectoryName(Application.dataPath);
            Set("ExecutablePath", Path.Combine(gameRoot, "Hollow Knight Silksong"));
            Set("ProcessName", "Hollow Knight Silksong");
            Set("GameRootPath", gameRoot);
            Set("ManagedPath", Path.Combine(Application.dataPath, "Managed"));
            Set("BepInExRootPath", root);
            Set("ConfigPath", Path.Combine(root, "config"));
            Set("BepInExConfigPath", Path.Combine(root, "config", "BepInEx.cfg"));
            Set("PluginPath", Path.Combine(root, "plugins"));
            Set("PatcherPluginPath", Path.Combine(root, "patchers"));
            Set("BepInExAssemblyDirectory", Path.Combine(root, "core"));
            Set("BepInExAssemblyPath", Path.Combine(root, "core", "BepInEx.dll"));
            Set("CachePath", Path.Combine(root, "cache"));
            Set("DllSearchPaths", new string[0]);
            foreach (var dir in new[] { "config", "plugins", "patchers", "cache", "iosport" })
                Directory.CreateDirectory(Path.Combine(root, dir));
        }

        // Plugin data is refreshed from the bundle whenever the build changes; configs are only
        // seeded when missing so settings changed on the phone survive reinstalls.
        static void SyncBundledFiles(string bundled, string root)
        {
            string stamp = File.ReadAllText(Path.Combine(bundled, StampFile)).Trim();
            string stampPath = Path.Combine(root, StampFile);
            bool newBuild = !File.Exists(stampPath) || File.ReadAllText(stampPath).Trim() != stamp;

            foreach (string file in Directory.GetFiles(bundled, "*", SearchOption.AllDirectories))
            {
                string relative = file.Substring(bundled.Length + 1);
                string target = Path.Combine(root, relative);
                bool isConfig = relative.StartsWith("config" + Path.DirectorySeparatorChar);
                if (File.Exists(target) && (isConfig || !newBuild))
                    continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target, true);
            }
            File.WriteAllText(stampPath, stamp);
        }

        static void RegisterHooks(string bundled)
        {
            string listPath = Path.Combine(bundled, HookAssembliesFile);
            if (!File.Exists(listPath))
                return;
            foreach (string assemblyName in File.ReadAllLines(listPath).Select(l => l.Trim()).Where(l => l.Length > 0))
            {
                Assembly assembly;
                try { assembly = Assembly.Load(assemblyName); }
                catch (Exception) { log.LogDebug(assemblyName + " isn't part of this build (nothing uses it); no hook points there"); continue; }
                var type = assembly.GetType("IOSPortGenerated.HookIds");
                if (type == null)
                {
                    log.LogError(assemblyName + " has no hook points; patches on it won't run (build problem: run ./build again)");
                    continue;
                }
                try
                {
                    type.GetMethod("Register", BindingFlags.Static | BindingFlags.Public).Invoke(null, null);
                }
                catch (Exception e)
                {
                    log.LogError("Couldn't register hook points in " + assemblyName + ": " + (e.InnerException ?? e));
                }
            }
            log.LogInfo(HookRegistry.RegisteredCount + " hook points available for Harmony patches");
        }

        static void LoadPlugins(string bundled)
        {
            // plugins.txt: one "AssemblyName|relative/path/in/plugins/Mod.dll" per line
            var candidates = new List<PluginInfo>();
            var pluginTypes = new Dictionary<PluginInfo, Type>();
            foreach (string line in File.ReadAllLines(Path.Combine(bundled, ManifestFile)))
            {
                var parts = line.Split('|');
                if (parts.Length < 2)
                    continue;
                string assemblyName = parts[0], relative = parts[1];
                string location = Path.Combine(Paths.PluginPath, relative);
                assemblyLocations[assemblyName] = location;

                Assembly assembly;
                try
                {
                    assembly = Assembly.Load(assemblyName);
                }
                catch (Exception e)
                {
                    log.LogError("Mod assembly " + assemblyName + " is missing from the build: " + e.Message);
                    continue;
                }

                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }

                foreach (var type in types)
                {
                    if (type.IsAbstract || !typeof(BaseUnityPlugin).IsAssignableFrom(type))
                        continue;
                    var metadata = MetadataHelper.GetMetadata(type);
                    if (metadata == null)
                        continue;
                    var info = new PluginInfo();
                    SetInfo(info, "Metadata", metadata);
                    SetInfo(info, "Dependencies", MetadataHelper.GetDependencies(type));
                    SetInfo(info, "Processes", MetadataHelper.GetAttributes<BepInProcess>(type));
                    SetInfo(info, "Incompatibilities", MetadataHelper.GetAttributes<BepInIncompatibility>(type));
                    SetInfo(info, "Location", location);
                    SetInfo(info, "TypeName", type.FullName);
                    candidates.Add(info);
                    pluginTypes[info] = type;
                }
            }

            var manager = new GameObject("BepInEx_Manager");
            UnityEngine.Object.DontDestroyOnLoad(manager);
            typeof(Chainloader).GetProperty("ManagerObject", BindingFlags.Static | BindingFlags.Public)?.GetSetMethod(true)?.Invoke(null, new object[] { manager });

            log.LogInfo(candidates.Count + " plugin" + (candidates.Count == 1 ? "" : "s") + " to load");
            var byGuid = new Dictionary<string, PluginInfo>();
            foreach (var info in candidates)
            {
                if (byGuid.ContainsKey(info.Metadata.GUID))
                    log.LogWarning("Skipping duplicate plugin " + info.Metadata.GUID + " in " + info.Location);
                else
                    byGuid[info.Metadata.GUID] = info;
            }

            var loaded = new Dictionary<string, Version>();
            var failed = new HashSet<string>();
            foreach (var info in SortByDependencies(byGuid))
            {
                var missing = info.Dependencies
                    .Where(d => (d.Flags & BepInDependency.DependencyFlags.HardDependency) != 0)
                    .Where(d => !loaded.TryGetValue(d.DependencyGUID, out var v) || v < d.MinimumVersion)
                    .Select(d => d.DependencyGUID)
                    .ToList();
                if (missing.Count > 0)
                {
                    log.LogError("Could not load [" + info + "] because it has missing dependencies: " + string.Join(", ", missing.ToArray()));
                    failed.Add(info.Metadata.GUID);
                    continue;
                }
                var incompatible = info.Incompatibilities.Where(i => loaded.ContainsKey(i.IncompatibilityGUID)).Select(i => i.IncompatibilityGUID).ToList();
                if (incompatible.Count > 0)
                {
                    log.LogError("Could not load [" + info + "] because it is incompatible with: " + string.Join(", ", incompatible.ToArray()));
                    continue;
                }

                try
                {
                    log.LogInfo("Loading [" + info + "]");
                    Chainloader.PluginInfos[info.Metadata.GUID] = info;
                    var instance = (BaseUnityPlugin)manager.AddComponent(pluginTypes[info]);
                    SetInfo(info, "Instance", instance);
                    loaded[info.Metadata.GUID] = info.Metadata.Version;
                }
                catch (Exception e)
                {
                    Chainloader.PluginInfos.Remove(info.Metadata.GUID);
                    failed.Add(info.Metadata.GUID);
                    log.LogError("Error loading [" + info + "] : " + e);
                }
            }
            log.LogMessage("Chainloader startup complete (" + loaded.Count + " loaded, " + failed.Count + " failed)");
        }

        static IEnumerable<PluginInfo> SortByDependencies(Dictionary<string, PluginInfo> byGuid)
        {
            var visited = new HashSet<string>();
            var order = new List<PluginInfo>();
            void Visit(PluginInfo info, HashSet<string> stack)
            {
                if (visited.Contains(info.Metadata.GUID) || !stack.Add(info.Metadata.GUID))
                    return;
                foreach (var dep in info.Dependencies)
                {
                    if (byGuid.TryGetValue(dep.DependencyGUID, out var depInfo))
                        Visit(depInfo, stack);
                }
                stack.Remove(info.Metadata.GUID);
                visited.Add(info.Metadata.GUID);
                order.Add(info);
            }
            foreach (var info in byGuid.Values.OrderBy(i => i.Metadata.GUID, StringComparer.Ordinal))
                Visit(info, new HashSet<string>());
            return order;
        }

        static void AddInternalLogSource(string typeName)
        {
            try
            {
                if (Activator.CreateInstance(typeof(Logger).Assembly.GetType(typeName), true) is ILogSource source)
                    Logger.Sources.Add(source);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[iOS Mods] " + typeName + ": " + e.Message);
            }
        }

        // BepInEx's UnityLogListener looks up Unity internals by reflection; Debug.Log is enough here.
        sealed class DebugLogListener : ILogListener
        {
            public void LogEvent(object sender, LogEventArgs e)
            {
                if (e.Source is UnityLogSource)
                    return;
                if ((e.Level & (LogLevel.Fatal | LogLevel.Error)) != 0) Debug.LogError(e.ToString());
                else if ((e.Level & LogLevel.Warning) != 0) Debug.LogWarning(e.ToString());
                else if ((e.Level & (LogLevel.Message | LogLevel.Info)) != 0) Debug.Log(e.ToString());
            }

            public void Dispose() { }
        }

        static void SetInfo(PluginInfo info, string property, object value) =>
            typeof(PluginInfo).GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetSetMethod(true)?.Invoke(info, new[] { value });

        internal static void WriteMissingHooks()
        {
            if (RootPath == null)
                return;
            try
            {
                File.WriteAllLines(Path.Combine(RootPath, "iosport", "missing-hooks.txt"), HookRegistry.MissingHooks.ToArray());
            }
            catch (Exception e)
            {
                Debug.LogWarning("[iOS Mods] couldn't write missing-hooks.txt: " + e.Message);
            }
        }
    }
}
