using System.IO;
using System.Runtime.InteropServices;
using InControl;
using UnityEngine;

namespace IOSPort
{
    // The game's controller rumble on iPhone (see Plugins/iOS/Rumble.mm): Unity can't rumble controllers on iOS,
    // so the game's motor strengths go to the controller through Apple's GameController haptics, or to the phone
    // itself while playing with the touch controls. The game's Controller Rumble setting (on/reduced/off) applies
    // to both. Fed every frame by PlatformVibrationHelper (patched in by tools/patch_game.py).
    // What the native side reports (which controller, any errors) goes to Documents/Logs/rumble.txt.
    public static class Rumble
    {
        [DllImport("__Internal")]
        static extern void IOSPortRumble(float small, float large, int toController);

        [DllImport("__Internal")]
        static extern string IOSPortRumbleLog();

        static float lastSmall, lastLarge;
        static bool lastToController, logStarted;

        public static void Set(float small, float large)
        {
            if (Application.platform != RuntimePlatform.IPhonePlayer)
                return;
            var device = InputManager.ActiveDevice;
            bool toController = device != null && device != InputDevice.Null && device.IsAttached && !(device is TouchGamepad);
            bool same = toController == lastToController && Mathf.Abs(small - lastSmall) < 0.01f && Mathf.Abs(large - lastLarge) < 0.01f;
            // Controllers get a fresh short buzz while rumbling (Rumble.mm), so they need calling every frame.
            if (same && !(toController && (small > 0.01f || large > 0.01f)))
                return;
            string note = toController != lastToController || !logStarted ? "rumble to " + (toController ? "controller: " + device.Name : "phone") + "\n" : null;
            lastSmall = small;
            lastLarge = large;
            lastToController = toController;
            IOSPortRumble(small, large, toController ? 1 : 0);
            WriteLog(note);
        }

        static void WriteLog(string note)
        {
            string text = note + IOSPortRumbleLog();
            if (string.IsNullOrEmpty(text))
                return;
            try
            {
                string dir = Path.Combine(Application.persistentDataPath, "Logs");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "rumble.txt");
                if (!logStarted)
                {
                    logStarted = true;
                    File.WriteAllText(path, "");
                }
                File.AppendAllText(path, text);
            }
            catch (IOException)
            {
            }
        }
    }
}
