using System.Collections;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace IOSPort
{
    // Testing aid: plays fake touches from Documents/iosport-touchscript.txt (deleted once read), for checking
    // the touch HUD in the Simulator. Lines: "<seconds after gameplay starts> down <finger> <x> <y>",
    // "<seconds> up <finger>" or "<seconds> shot <name>" (x/y are safe-area fractions, 0,0 = bottom left).
    // "sdown" takes whole-screen fractions instead; "press <InputControlType>" / "release" hold a virtual
    // controller button; "export" runs EXPORT SAVES; "noclip" and "warp" press the DebugTools buttons ("warp <scene> <gate>" goes elsewhere), "gates" logs the room's gates; "cams" logs the cameras and blur planes, "disable <type>" turns that component off everywhere, "hide <name>" hides renderers by name, "front" logs what covers the middle of the screen; "kill" deals Hornet lethal damage (to check the death effect); "god <InvincibilityStates>" sets the cheat invincibility and "silk" fills the silk spool. A first line of "title"
    // starts the clock on the title screen instead of in gameplay.
    public class TouchScript : MonoBehaviour
    {
        string[] lines;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (!Debug.isDebugBuild)
                return; // test aid only (Simulator builds are debug builds)
            string path = Path.Combine(Application.persistentDataPath, "iosport-touchscript.txt");
            if (!File.Exists(path))
                return;
            var go = new GameObject("IOSPort TouchScript");
            DontDestroyOnLoad(go);
            go.AddComponent<TouchScript>().lines = File.ReadAllLines(path);
            File.Delete(path);
        }

        IEnumerator Start()
        {
            var startState = lines.Length > 0 && lines[0].Trim() == "title" ? GlobalEnums.GameState.MAIN_MENU : GlobalEnums.GameState.PLAYING;
            while (GameManager.UnsafeInstance == null || GameManager.UnsafeInstance.GameState != startState)
                yield return null;
            float start = Time.unscaledTime;
            foreach (var line in lines)
            {
                var p = line.Split(' ');
                if (p.Length < 2 || !float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float at))
                    continue;
                while (Time.unscaledTime - start < at)
                    yield return null;
                if (p[1] == "down" && p.Length >= 5)
                    TouchControls.TestTouches[int.Parse(p[2])] = new Vector2(float.Parse(p[3], CultureInfo.InvariantCulture), float.Parse(p[4], CultureInfo.InvariantCulture));
                else if (p[1] == "sdown" && p.Length >= 5)
                {
                    var sa = Screen.safeArea;
                    float x = float.Parse(p[3], CultureInfo.InvariantCulture) * Screen.width, y = float.Parse(p[4], CultureInfo.InvariantCulture) * Screen.height;
                    TouchControls.TestTouches[int.Parse(p[2])] = new Vector2((x - sa.x) / sa.width, (y - sa.y) / sa.height);
                }
                else if (p[1] == "press" && p.Length >= 3)
                    TouchGamepad.TestButton = (InControl.InputControlType)System.Enum.Parse(typeof(InControl.InputControlType), p[2]);
                else if (p[1] == "kill" && HeroController.instance != null)
                {
                    var hero = HeroController.instance;
                    hero.TakeDamage(hero.gameObject, GlobalEnums.CollisionSide.top, 999, GlobalEnums.HazardType.ENEMY);
                }
                else if (p[1] == "god" && p.Length >= 3)
                    CheatManager.Invincibility = (CheatManager.InvincibilityStates)System.Enum.Parse(typeof(CheatManager.InvincibilityStates), p[2]);
                else if (p[1] == "silk" && HeroController.instance != null)
                    HeroController.instance.AddSilk(PlayerData.instance.CurrentSilkMax, false);
                else if (p[1] == "export")
                    SaveExport.Export();
                else if (p[1] == "noclip")
                    DebugTools.ToggleNoclip();
                else if (p[1] == "warp" && p.Length >= 4)
                    DebugTools.WarpTo(p[2], p[3]);
                else if (p[1] == "warp")
                    DebugTools.WarpToVaultsDoor();
                else if (p[1] == "gates")
                    DebugTools.LogGates();
                else if (p[1] == "probe" && p.Length >= 3)
                    DebugTools.ProbeGate(string.Join(" ", p, 2, p.Length - 2));
                else if (p[1] == "release")
                    TouchGamepad.TestButton = InControl.InputControlType.None;
                else if (p[1] == "up" && p.Length >= 3)
                    TouchControls.TestTouches.Remove(int.Parse(p[2]));
                else if (p[1] == "state")
                {
                    var gm = GameManager.UnsafeInstance;
                    var hero = HeroController.instance;
                    var handler = ManagerSingleton<InputHandler>.UnsafeInstance;
                    Debug.Log("[IOSPort] state: hero=" + (hero != null ? hero.transform.position.ToString() : "none")
                        + " device=" + InControl.InputManager.ActiveDevice?.Name
                        + " gameState=" + gm?.GameState + " paused=" + gm?.isPaused
                        + " inventory=" + (PlayerData.instance != null && PlayerData.instance.isInventoryOpen)
                        + " acceptingInput=" + (handler != null && handler.acceptingInput)
                        + " pauseBinding=" + (handler != null ? string.Join(",", System.Linq.Enumerable.Select(handler.inputActions.Pause.Bindings, b => b.ToString())) : ""));
                }
                else if (p[1] == "cams")
                    LogCameras();
                else if (p[1] == "disable" && p.Length >= 3)
                {
                    foreach (var b in Object.FindObjectsByType<Behaviour>(FindObjectsSortMode.None))
                        if (b.GetType().Name == p[2])
                            b.enabled = false;
                }
                else if (p[1] == "hide" && p.Length >= 3)
                {
                    string name = string.Join(" ", p, 2, p.Length - 2);
                    foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
                        if (r.name == name)
                            r.enabled = false;
                }
                else if (p[1] == "hidenearer" && p.Length >= 3)
                {
                    float z = float.Parse(p[2], CultureInfo.InvariantCulture);
                    foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
                        if (r.bounds.center.z < z && r.gameObject.layer != 5)
                            r.enabled = false;
                }
                else if (p[1] == "front")
                    LogRenderersInView();
                else if (p[1] == "darkness")
                    SaveDarkness();
                else if (p[1] == "shot" && p.Length >= 3)
                    // On iOS the path is taken relative to the app's Documents folder.
                    ScreenCapture.CaptureScreenshot("touchtest_" + p[2] + ".png");
                Debug.Log("[IOSPort] touch script: " + line);
            }
        }

        // For tracking down darkness problems: saves the darkness cutout texture (touchtest_darkness.png) and logs
        // what draws into it.
        static void SaveDarkness()
        {
            var rt = Shader.GetGlobalTexture("_DarknessCutout") as RenderTexture;
            Debug.Log("[IOSPort] darkness cutout: " + (rt != null ? rt.name + " " + rt.width + "x" + rt.height + " " + rt.format : "none"));
            if (rt != null)
            {
                var old = RenderTexture.active;
                RenderTexture.active = rt;
                var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                RenderTexture.active = old;
                File.WriteAllBytes(Path.Combine(Application.persistentDataPath, "touchtest_darkness.png"), tex.EncodeToPNG());
                Object.Destroy(tex);
            }
            foreach (var r in Object.FindObjectsByType<SpriteRenderer>(FindObjectsSortMode.None))
                if (r.gameObject.layer == 1 || r.sharedMaterial != null && r.sharedMaterial.shader.name == "Sprites/Darkness Sprite")
                    Debug.Log("[IOSPort] darkness sprite: " + r.name + " parent=" + r.transform.parent?.name + " layer=" + r.gameObject.layer
                        + " enabled=" + (r.enabled && r.gameObject.activeInHierarchy) + " color=" + r.color + " z=" + r.bounds.center.z
                        + " shader=" + r.sharedMaterial?.shader.name + " keywords=" + string.Join(",", r.sharedMaterial != null ? r.sharedMaterial.shaderKeywords : new string[0]));
            foreach (var cam in Camera.allCameras)
                if (cam.name == "Darkness Camera")
                    Debug.Log("[IOSPort] darkness camera clear=" + cam.clearFlags + " bg=" + cam.backgroundColor + " mask=" + cam.cullingMask);
        }

        // For tracking down rendering problems: the visible renderers covering the middle of the screen, nearest first.
        static void LogRenderersInView()
        {
            var cam = GameCameras.instance != null ? GameCameras.instance.tk2dCam.GetComponent<Camera>() : Camera.main;
            var ray = cam.transform.position;
            var hits = new System.Collections.Generic.List<Renderer>();
            var all = Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None);
            foreach (var r in all)
            {
                var b = r.bounds;
                if (r.enabled && r.gameObject.activeInHierarchy && b.min.x <= ray.x && b.max.x >= ray.x && b.min.y <= ray.y && b.max.y >= ray.y)
                    hits.Add(r);
            }
            Debug.Log("[IOSPort] renderers: " + all.Length + ", covering the middle: " + hits.Count + " (camera " + cam.name + " at " + ray + ")");
            hits.Sort((a, b) => a.bounds.center.z.CompareTo(b.bounds.center.z));
            foreach (var r in hits)
                Debug.Log("[IOSPort] in view: " + r.name + " z=" + r.bounds.center.z + " size=" + r.bounds.size
                    + " layer=" + r.sortingLayerName + "/" + r.sortingOrder + " shader=" + r.sharedMaterial?.shader.name
                    + " queue=" + r.sharedMaterial?.renderQueue + " parent=" + r.transform.parent?.name);
        }

        // For tracking down rendering problems: every camera, its effects, and the blur planes.
        static void LogCameras()
        {
            foreach (var cam in Camera.allCameras)
            {
                var effects = new System.Collections.Generic.List<string>();
                foreach (var b in cam.GetComponents<Behaviour>())
                    if (!(b is Camera))
                        effects.Add(b.GetType().Name + (b.enabled ? "" : "(off)"));
                Debug.Log("[IOSPort] camera " + cam.name + " depth=" + cam.depth + " pos=" + cam.transform.position
                    + " near=" + cam.nearClipPlane + " far=" + cam.farClipPlane + " ortho=" + cam.orthographic
                    + " target=" + (cam.targetTexture != null ? cam.targetTexture.name : "screen")
                    + " mask=" + cam.cullingMask + " effects=" + string.Join(",", effects));
            }
            for (int i = 0; i < BlurPlane.BlurPlaneCount; i++)
            {
                var plane = BlurPlane.GetBlurPlane(i);
                var r = plane.GetComponent<MeshRenderer>();
                Debug.Log("[IOSPort] blur plane " + plane.name + " z=" + plane.PlaneZ + " scene=" + plane.gameObject.scene.name
                    + " bounds=" + r.bounds + " material=" + r.sharedMaterial?.shader.name);
            }
            Debug.Log("[IOSPort] camera blur spacing=" + CameraBlurPlane.Spacing + " vibrancy=" + CameraBlurPlane.Vibrancy
                + " maskLerp=" + CameraBlurPlane.MaskLerp);
        }
    }
}
