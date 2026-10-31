using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace IOSPort
{
    // Performance options for weaker iPhones, changed from the port settings panel.
    //  - Resolution: multiplier on the build's fixed render DPI (326 DPI, ~71% of native on an iPhone 11 Pro Max).
    //  - Bloom/lens effects: full-screen extras the game's own menu has no switch for. Frame rate, particles,
    //    shader quality, dithering and noise are left to the game's Video menu.
    // Saved in PlayerPrefs and re-applied every second, since cameras are recreated on scene loads.
    public class PerformanceSettings : MonoBehaviour
    {
        public static readonly float[] Scales = { 1f, 0.85f, 0.7f, 0.55f };

        static readonly HashSet<string> ExtraEffects = new HashSet<string> { "BloomOptimized", "LensCAAndDistortion" };

        const string ScaleKey = "IOSPort.Perf.Scale", EffectsKey = "IOSPort.Perf.LowEffects";

        public static int ScaleIndex { get; private set; }
        public static bool LowEffects { get; private set; }

        static PerformanceSettings instance;
        readonly List<Behaviour> disabledByUs = new List<Behaviour>();
        float nextApply;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Bootstrap()
        {
            if (!Application.isMobilePlatform || instance != null)
                return;
            // Release builds on the phone don't log at all (nothing reads it, and it costs CPU).
            if (!Debug.isDebugBuild)
                Debug.unityLogger.logEnabled = false;
            else
                UnityEngine.SceneManagement.SceneManager.sceneLoaded += (scene, mode) =>
                {
                    Debug.Log($"[IOSPort] scene {scene.name} loaded at {Time.realtimeSinceStartup:F2}s");
                    LogBrokenShaders(scene);
                };
            ScaleIndex = Mathf.Clamp(PlayerPrefs.GetInt(ScaleKey, 0), 0, Scales.Length - 1);
            LowEffects = PlayerPrefs.GetInt(EffectsKey, 0) != 0;
            var go = new GameObject("IOSPort Performance");
            DontDestroyOnLoad(go);
            instance = go.AddComponent<PerformanceSettings>();
            QualitySettings.resolutionScalingFixedDPIFactor = Scales[ScaleIndex];
        }

        // Debug builds: names renderers whose shader didn't make it to the device (drawn with the error shader).
        static void LogBrokenShaders(UnityEngine.SceneManagement.Scene scene)
        {
            foreach (var root in scene.GetRootGameObjects())
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            foreach (var m in r.sharedMaterials)
                if (m != null && (m.shader == null || !m.shader.isSupported || m.shader.name == "Hidden/InternalErrorShader"))
                    Debug.Log($"[IOSPort] broken shader in {scene.name}: {(r.transform.parent != null ? r.transform.parent.name + "/" : "")}{r.name} material {m.name} shader {(m.shader != null ? m.shader.name : "null")}");
        }

        public static string ScaleLabel => "RESOLUTION " + Mathf.RoundToInt(Scales[ScaleIndex] * 100f) + "%";
        public static string EffectsLabel => "BLOOM & LENS EFFECTS " + (LowEffects ? "OFF" : "ON");

        public static void CycleScale() { ScaleIndex = (ScaleIndex + 1) % Scales.Length; Save(); }
        public static void ToggleEffects() { LowEffects = !LowEffects; Save(); }

        // The game's Resolution and Full Screen rows do nothing useful on an iPhone (Resolution would override the
        // render scale above), so MenuButtonList hides them.
        public static bool HidesMenuOption(Selectable s)
        {
            if (!Application.isMobilePlatform || s == null)
                return false;
            var ui = UIManager.instance;
            return ui != null && (s == ui.resolutionOption || s == ui.fullscreenMenuOption
                || (ui.fullscreenOption != null && s == ui.fullscreenOption.optionList));
        }

        static void Save()
        {
            PlayerPrefs.SetInt(ScaleKey, ScaleIndex);
            PlayerPrefs.SetInt(EffectsKey, LowEffects ? 1 : 0);
            PlayerPrefs.Save();
            QualitySettings.resolutionScalingFixedDPIFactor = Scales[ScaleIndex];
            if (instance != null)
                instance.Apply();
            Debug.Log($"[IOSPort] performance: {ScaleLabel}, {EffectsLabel}");
        }

        int lastWidth, framesSinceApply;

        void Update()
        {
            if (Screen.width != lastWidth)
            {
                lastWidth = Screen.width;
                Debug.Log($"[IOSPort] render size {Screen.width}x{Screen.height}");
            }
            framesSinceApply++;
            if (Time.unscaledTime < nextApply)
                return;
            if (Debug.isDebugBuild)
                Debug.Log($"[IOSPort] fps {framesSinceApply} (target {Application.targetFrameRate})");
            framesSinceApply = 0;
            nextApply = Time.unscaledTime + 1f;
            Apply();
        }

        void Apply()
        {
            if (LowEffects)
            {
                foreach (var cam in Camera.allCameras)
                {
                    foreach (var b in cam.GetComponents<Behaviour>())
                    {
                        if (b != null && b.enabled && ExtraEffects.Contains(b.GetType().Name))
                        {
                            b.enabled = false;
                            disabledByUs.Add(b);
                        }
                    }
                }
            }
            else if (disabledByUs.Count > 0)
            {
                foreach (var b in disabledByUs)
                    if (b != null)
                        b.enabled = true;
                disabledByUs.Clear();
            }
        }
    }
}
