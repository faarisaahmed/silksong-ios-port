using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;
using UnityEngine;

namespace IOSPort
{
    // Batch entry point: Unity -batchmode -executeMethod IOSPort.IOSBuild.Build
    public static class IOSBuild
    {
        // Next to the Unity project folder unless the build script says otherwise.
        static string DefaultXcodeOutput => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "build", "Xcode"));

        // -iosportOut <dir> picks the Xcode project folder; -iosportSimulator builds for the iOS Simulator;
        // -iosportBundleId <id> changes the app's bundle id.
        static string XcodeOutput => Arg("-iosportOut") ?? DefaultXcodeOutput;
        static string BundleId => Arg("-iosportBundleId") ?? "com.personal.silksong";
        static bool Simulator => Environment.GetCommandLineArgs().Contains("-iosportSimulator");

        static string Arg(string name)
        {
            var args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        [MenuItem("iOS Port/Build Xcode Project")]
        public static void Build()
        {
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.iOS)
                EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.iOS, BuildTarget.iOS);

            ConfigurePlayer();
            TextureOptimizer.Run();

            AddressablesSetup.Setup();
            AddressableAssetSettings.BuildPlayerContent(out var addrResult);
            if (!string.IsNullOrEmpty(addrResult.Error))
                Fail("Addressables build failed: " + addrResult.Error);

            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { "Assets/Scenes/Pre_Menu_Loader.unity" },
                locationPathName = XcodeOutput,
                target = BuildTarget.iOS,
                options = BuildOptions.None,
            });
            if (report.summary.result != BuildResult.Succeeded)
                Fail($"Player build {report.summary.result} with {report.summary.totalErrors} errors");

            Debug.Log($"[IOSPort] Xcode project written to {XcodeOutput}");
        }

        // Code-only rebuild: reuses the Addressables content from the last full Build().
        public static void BuildPlayerOnly()
        {
            ConfigurePlayer();
            UnityEditor.AddressableAssets.AddressableAssetSettingsDefaultObject.Settings.BuildAddressablesWithPlayerBuild =
                AddressableAssetSettings.PlayerBuildOption.DoNotBuildWithPlayer;
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { "Assets/Scenes/Pre_Menu_Loader.unity" },
                locationPathName = XcodeOutput,
                target = BuildTarget.iOS,
                options = BuildOptions.None,
            });
            if (report.summary.result != BuildResult.Succeeded)
                Fail($"Player build {report.summary.result} with {report.summary.totalErrors} errors");
            Debug.Log($"[IOSPort] Xcode project written to {XcodeOutput}");
        }

        static void ConfigurePlayer()
        {
            ExecutionOrders.Apply();
            // Editor play-testing can leave these on; they make a slow "Development" player with a debug console.
            // (Simulator builds are always Development: Unity only ships a development simulator player.)
            EditorUserBuildSettings.development = Simulator;
            EditorUserBuildSettings.connectProfiler = false;
            EditorUserBuildSettings.allowDebugging = false;
            EditorUserBuildSettings.buildWithDeepProfilingSupport = false;
            EditorUserBuildSettings.iOSXcodeBuildConfig = XcodeBuildConfig.Release;
            var ios = NamedBuildTarget.iOS;
            PlayerSettings.companyName = "Team Cherry";
            PlayerSettings.productName = "Silksong";
            PlayerSettings.SetApplicationIdentifier(ios, BundleId);
            PlayerSettings.SetScriptingBackend(ios, ScriptingImplementation.IL2CPP);
            // PlayMaker and the game resolve many types by reflection, so keep stripping minimal.
            PlayerSettings.SetManagedStrippingLevel(ios, ManagedStrippingLevel.Minimal);
            // With incremental GC off (the game ships with it off), players compiled by Xcode 26 break reflection:
            // Newtonsoft.Json can't read PlayerData and the title screen comes up empty. Unity's issue tracker:
            // "[iOS] Stripping issue on Xcode 26 when "Use incremental GC" is disabled".
            PlayerSettings.gcIncremental = true;
            PlayerSettings.SetArchitecture(ios, 1); // ARM64
            PlayerSettings.iOS.sdkVersion = Simulator ? iOSSdkVersion.SimulatorSDK : iOSSdkVersion.DeviceSDK;
            PlayerSettings.iOS.simulatorSdkArchitecture = AppleMobileArchitectureSimulator.ARM64;
            PlayerSettings.iOS.targetOSVersionString = "15.0";
            PlayerSettings.iOS.appleEnableAutomaticSigning = false;
            PlayerSettings.iOS.hideHomeButton = true;
            PlayerSettings.iOS.deferSystemGesturesMode = UnityEngine.iOS.SystemGestureDeferMode.All;
            PlayerSettings.statusBarHidden = true;
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.AutoRotation;
            PlayerSettings.allowedAutorotateToLandscapeLeft = true;
            PlayerSettings.allowedAutorotateToLandscapeRight = true;
            PlayerSettings.allowedAutorotateToPortrait = false;
            PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;

            // Release builds: no log output (saves a little CPU; nothing reads it on the phone).
            foreach (LogType type in Enum.GetValues(typeof(LogType)))
                PlayerSettings.SetStackTraceLogType(type, Simulator ? StackTraceLogType.ScriptOnly : StackTraceLogType.None);
            PlayerSettings.usePlayerLog = Simulator;

            SetAppIcon();
            // Plain black launch screen; IOSPort.StartupSplash fades the FaarisPorts loading screen in from it.
            PlayerSettings.iOS.SetiPhoneLaunchScreenType(iOSLaunchScreenType.ImageAndBackgroundRelative);
            PlayerSettings.iOS.SetLaunchScreenImage(null, iOSLaunchScreenImageType.iPhoneLandscapeImage);
            PlayerSettings.iOS.SetLaunchScreenImage(null, iOSLaunchScreenImageType.iPhonePortraitImage);

            // Render at 326 DPI instead of the native 458 (~71% resolution); not exposed by the PlayerSettings API.
            var playerSettings = new SerializedObject(Unsupported.GetSerializedAssetInterfaceSingleton("PlayerSettings"));
            playerSettings.FindProperty("resolutionScalingMode").intValue = 1; // FixedDPI
            playerSettings.FindProperty("targetPixelDensity").intValue = 326;
            playerSettings.FindProperty("iOSLaunchScreenBackgroundColor").colorValue = Color.black;
            playerSettings.ApplyModifiedProperties();
        }

        // Every iOS icon slot gets Assets/IOSPortIcon/AppIcon.png (1024 px; Unity scales it per slot).
        static void SetAppIcon()
        {
            const string path = "Assets/IOSPortIcon/AppIcon.png";
            if (AssetImporter.GetAtPath(path) is TextureImporter importer && (importer.mipmapEnabled || importer.textureCompression != TextureImporterCompression.Uncompressed || importer.npotScale != TextureImporterNPOTScale.None))
            {
                importer.mipmapEnabled = false;
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.npotScale = TextureImporterNPOTScale.None;
                importer.SaveAndReimport();
            }
            var icon = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (icon == null)
            {
                Debug.LogWarning("[IOSPort] " + path + " missing; keeping the default icon");
                return;
            }
            var ios = NamedBuildTarget.iOS;
            foreach (var kind in PlayerSettings.GetSupportedIconKinds(ios))
            {
                var icons = PlayerSettings.GetPlatformIcons(ios, kind);
                foreach (var slot in icons)
                    slot.SetTextures(icon);
                PlayerSettings.SetPlatformIcons(ios, kind, icons);
            }
        }

        static void Fail(string message)
        {
            Debug.LogError("[IOSPort] " + message);
            if (Application.isBatchMode)
                EditorApplication.Exit(1);
            throw new BuildFailedException(message);
        }

        // Exposes the app's Documents folder (where saves live) in the iOS Files app.
        [PostProcessBuild]
        public static void OnPostprocessBuild(BuildTarget target, string path)
        {
            if (target != BuildTarget.iOS)
                return;
            string plistPath = Path.Combine(path, "Info.plist");
            var plist = new PlistDocument();
            plist.ReadFromFile(plistPath);
            plist.root.SetBoolean("UIFileSharingEnabled", true);
            plist.root.SetBoolean("LSSupportsOpeningDocumentsInPlace", true);
            plist.root.SetBoolean("UIRequiresFullScreen", true);
            plist.WriteToFile(plistPath);

            // Plugins/iOS/Rumble.mm.
            string projectPath = PBXProject.GetPBXProjectPath(path);
            var project = new PBXProject();
            project.ReadFromFile(projectPath);
            string framework = project.GetUnityFrameworkTargetGuid();
            project.AddFrameworkToProject(framework, "CoreHaptics.framework", false);
            project.AddFrameworkToProject(framework, "GameController.framework", false);
            project.WriteToFile(projectPath);

            // The game's V-Sync On and Frame Rate Cap Off both mean "no cap" (-1), which iOS turns into 30 fps.
            // Run at the screen's rate instead, like V-Sync on PC.
            string renderingPath = Path.Combine(path, "Classes/UnityAppController+Rendering.mm");
            const string from = "    if (targetFPS <= 0)\n        targetFPS = UnityGetTargetFPS();\n";
            const string to = "    if (targetFPS <= 0)\n        targetFPS = (int)[UIScreen mainScreen].maximumFramesPerSecond;\n"
                + "    if (targetFPS <= 0)\n        targetFPS = UnityGetTargetFPS();\n";
            string rendering = File.ReadAllText(renderingPath);
            if (!rendering.Contains(to))
            {
                if (!rendering.Contains(from))
                    Fail("Couldn't patch the frame rate callback in " + renderingPath);
                File.WriteAllText(renderingPath, rendering.Replace(from, to));
            }
        }
    }
}
