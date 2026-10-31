using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace IOSPort
{
    // BepInEx mods built in by ./build: after Unity compiles the game scripts for
    // the player, run the weaver on them (BepInEx patchers + hook points for Harmony patches) before IL2CPP
    // converts them. Does nothing unless ./build has prepared IOSPortMods/weaver.json.
    public class ModsBuild : IPostBuildPlayerScriptDLLs
    {
        public int callbackOrder => 0;

        public void OnPostBuildPlayerScriptDLLs(BuildReport report)
        {
            string project = Path.GetDirectoryName(Application.dataPath);
            string configPath = Path.Combine(project, "IOSPortMods", "weaver.json");
            if (!File.Exists(configPath))
                return;

            var config = JsonUtility.FromJson<Config>(File.ReadAllText(configPath));
            string compiled = Path.Combine(project, "Library", "Bee", "PlayerScriptAssemblies");
            // Bee's compiler output, untouched by previous weaving.
            string pristineDir = Directory.GetDirectories(Path.Combine(project, "Library", "Bee", "artifacts"), "*P.dag")
                .OrderByDescending(Directory.GetLastWriteTimeUtc).FirstOrDefault();

            // The linker reads the copies in Temp/StagingArea/Data/Managed when they exist, so weave both.
            string staging = Path.Combine(project, "Temp", "StagingArea", "Data", "Managed");
            foreach (string name in config.Assemblies)
            {
                string source = pristineDir == null ? null : Path.Combine(pristineDir, name + ".dll");
                if (!File.Exists(Path.Combine(compiled, name + ".dll")))
                    continue; // precompiled plugin, woven by ./build before the build
                if (source == null || !File.Exists(source))
                    throw new BuildFailedException("[IOSPort] can't find the compiler output for " + name);
                foreach (string dir in new[] { compiled, staging })
                {
                    string dll = Path.Combine(dir, name + ".dll");
                    if (File.Exists(dll))
                        Run(config.Weaver, $"weave --project \"{project}\" --dll \"{dll}\" --source \"{source}\"");
                }
            }
        }

        static void Run(string weaver, string arguments)
        {
            string dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet");
            var info = new ProcessStartInfo(Path.Combine(dotnetRoot, "dotnet"), $"\"{weaver}\" {arguments}")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            info.EnvironmentVariables["DOTNET_ROOT"] = dotnetRoot;
            using var process = Process.Start(info);
            string output = process.StandardOutput.ReadToEnd();
            string errors = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Debug.Log("[IOSPort] weaver " + arguments + "\n" + output);
            if (process.ExitCode != 0)
                throw new BuildFailedException("[IOSPort] weaver failed:\n" + output + errors);
        }

        [Serializable]
        class Config
        {
            public string Weaver;
            public string[] Assemblies;
        }
    }
}
