using System.Collections.Generic;
using InControl;
using UnityEngine;

namespace IOSPort
{
    // Virtual Xbox-style controller driven by the on-screen touch controls.
    // The game binds its actions to controller buttons, so the touch UI presses whichever
    // button an action is currently bound to; in-game rebinding and button prompts keep working.
    public class TouchGamepad : InputDevice
    {
        public static readonly InputControlType[] Buttons =
        {
            InputControlType.Action1, InputControlType.Action2, InputControlType.Action3, InputControlType.Action4,
            InputControlType.LeftBumper, InputControlType.RightBumper, InputControlType.LeftTrigger, InputControlType.RightTrigger,
            InputControlType.LeftStickButton, InputControlType.RightStickButton,
            InputControlType.DPadUp, InputControlType.DPadDown, InputControlType.DPadLeft, InputControlType.DPadRight,
            InputControlType.View, InputControlType.Back, InputControlType.Menu, InputControlType.Start, InputControlType.Select,
        };

        readonly HashSet<InputControlType> held = new HashSet<InputControlType>();

        public Vector2 Stick;

        // Editor play-test hooks (see IOSPort.PlayTest); unused in normal play.
        public static Vector2 TestStick;
        public static InputControlType TestButton = InputControlType.None;

        public TouchGamepad()
            : base("Touch Controls")
        {
            DeviceClass = InputDeviceClass.Controller;
            DeviceStyle = InputDeviceStyle.XboxOne;
            AddControl(InputControlType.LeftStickLeft, "Left Stick Left");
            AddControl(InputControlType.LeftStickRight, "Left Stick Right");
            AddControl(InputControlType.LeftStickUp, "Left Stick Up");
            AddControl(InputControlType.LeftStickDown, "Left Stick Down");
            foreach (var button in Buttons)
                AddControl(button, button.ToString());
        }

        public void ReleaseAll()
        {
            held.Clear();
            Stick = Vector2.zero;
        }

        public void SetHeld(InputControlType control, bool down)
        {
            if (control == InputControlType.None)
                return;
            if (down)
                held.Add(control);
            else
                held.Remove(control);
        }

        public override void Update(ulong updateTick, float deltaTime)
        {
            UpdateLeftStickWithValue(TestStick != Vector2.zero ? TestStick : Stick, updateTick, deltaTime);
            foreach (var button in Buttons)
                GetControl(button).UpdateWithState(held.Contains(button) || button == TestButton, updateTick, deltaTime);
        }
    }
}
