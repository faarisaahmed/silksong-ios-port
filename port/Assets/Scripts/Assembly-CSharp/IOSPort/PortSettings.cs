using UnityEngine;

namespace IOSPort
{
    // Port options changed from the settings panel (gear on the title screen, or SETTINGS in remap mode).
    // Saved in PlayerPrefs.
    public static class PortSettings
    {
        // Auto: hidden while a controller is being used. OffWithController: hidden whenever a controller is connected.
        // Either way the touch controls come back when no controller is connected.
        public enum TouchMode { Auto, Always, OffWithController }
        public enum MoveStyle { Joystick, DPad, ArrowKeys }

        const string TouchKey = "IOSPort.TouchMode", MoveKey = "IOSPort.MoveStyle", WideKey = "IOSPort.FullWidth";

        public static TouchMode Touch { get; private set; } = (TouchMode)Mathf.Clamp(PlayerPrefs.GetInt(TouchKey, 0), 0, 2);
        public static MoveStyle Move { get; private set; } = (MoveStyle)Mathf.Clamp(PlayerPrefs.GetInt(MoveKey, 0), 0, 2);
        // Full width uses the game's own wide-screen support (the whole iPhone screen). The game's camera still stops
        // where a 16:9 view meets a room's edge, so some rooms show past their edges, as on an ultrawide PC monitor;
        // 16:9 draws exactly what a normal PC monitor shows, with black bars at the sides.
        public static bool FullWidth { get; private set; } = PlayerPrefs.GetInt(WideKey, 1) != 0;

        public static string TouchLabel => "TOUCH CONTROLS: " + (Touch == TouchMode.Auto ? "AUTO (HIDE WHILE USING CONTROLLER)" : Touch == TouchMode.Always ? "ALWAYS ON" : "OFF WITH CONTROLLER CONNECTED");
        public static string ScreenLabel => "SCREEN: " + (FullWidth ? "FULL WIDTH" : "16:9 (LIKE PC)");
        public static string MoveLabel => "MOVEMENT: " + (Move == MoveStyle.Joystick ? "JOYSTICK" : Move == MoveStyle.DPad ? "D-PAD" : "ARROW KEYS");

        public static void CycleTouch()
        {
            Touch = (TouchMode)(((int)Touch + 1) % 3);
            PlayerPrefs.SetInt(TouchKey, (int)Touch);
            PlayerPrefs.Save();
        }

        public static void ToggleFullWidth()
        {
            FullWidth = !FullWidth;
            PlayerPrefs.SetInt(WideKey, FullWidth ? 1 : 0);
            PlayerPrefs.Save();
            ForceCameraAspect.IOSPortReapply();
        }

        public static void CycleMove()
        {
            Move = (MoveStyle)(((int)Move + 1) % 3);
            PlayerPrefs.SetInt(MoveKey, (int)Move);
            PlayerPrefs.Save();
        }
    }
}
