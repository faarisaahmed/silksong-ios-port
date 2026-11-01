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
    // controller button; "export" runs EXPORT SAVES; "noclip" and "warp" press the DebugTools buttons ("warp <scene> <gate>" goes elsewhere), "gates" logs the room's gates; "kill" deals Hornet lethal damage (to check the death effect). A first line of "title"
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
                else if (p[1] == "shot" && p.Length >= 3)
                    // On iOS the path is taken relative to the app's Documents folder.
                    ScreenCapture.CaptureScreenshot("touchtest_" + p[2] + ".png");
                Debug.Log("[IOSPort] touch script: " + line);
            }
        }
    }
}
