using System.Runtime.InteropServices;
using InControl;
using UnityEngine;

namespace IOSPort
{
    // The game's controller rumble on iPhone (see Plugins/iOS/Rumble.mm): Unity can't rumble controllers on iOS,
    // so the game's motor strengths go to the controller through Apple's GameController haptics, or to the phone
    // itself while playing with the touch controls. The game's Controller Rumble setting (on/reduced/off) applies
    // to both. Fed every frame by PlatformVibrationHelper (patched in by tools/patch_game.py).
    public static class Rumble
    {
        [DllImport("__Internal")]
        static extern void IOSPortRumble(float small, float large, int toController);

        static float lastSmall, lastLarge;
        static bool lastToController;

        public static void Set(float small, float large)
        {
            if (Application.platform != RuntimePlatform.IPhonePlayer)
                return;
            var device = InputManager.ActiveDevice;
            bool toController = device != null && device != InputDevice.Null && device.IsAttached && !(device is TouchGamepad);
            if (toController == lastToController && Mathf.Abs(small - lastSmall) < 0.01f && Mathf.Abs(large - lastLarge) < 0.01f)
                return;
            lastSmall = small;
            lastLarge = large;
            lastToController = toController;
            IOSPortRumble(small, large, toController ? 1 : 0);
        }
    }
}
