using GlobalEnums;
using InControl;
using UnityEngine;

namespace IOSPort
{
    // On iPhone, controllers come through Unity's Input System, which only reports each controller's two menu
    // buttons and names them after the controller type (View/Menu, Share/Options, Create/Options, or Select/Start
    // for everything else, Switch and Backbone included). The game binds the inventory to the names its PC
    // controllers use: those other controllers end up treated as Xbox 360 ones and wait for Back, which never
    // comes, and PlayStation controllers open it with the touchpad click, which iOS doesn't report. So on iPhone
    // the left menu button (minus, Share, Create, View, Select) always opens the inventory and the right one
    // (plus, Options, Menu, Start) always pauses. Called by InputHandler.MapControllerButtons (see patch_game.py).
    public static class ControllerMenuButtons
    {
        static readonly InputControlType[] Inventory =
        {
            InputControlType.Share, InputControlType.Create, InputControlType.View, InputControlType.Back,
            InputControlType.Select, InputControlType.Minus,
        };

        static readonly InputControlType[] Pause =
        {
            InputControlType.Start, InputControlType.Options, InputControlType.Menu, InputControlType.Plus,
        };

        public static void Add(HeroActions actions, GamepadType type)
        {
            if (Application.platform != RuntimePlatform.IPhonePlayer)
                return;
            // The touchpad binding goes, so button prompts (an action's first binding) show Share instead.
            if (type == GamepadType.PS4 || type == GamepadType.PS5)
                actions.OpenInventory.RemoveBinding(new DeviceBindingSource(InputControlType.TouchPadButton));
            foreach (var control in Inventory)
                actions.OpenInventory.AddBinding(new DeviceBindingSource(control));
            foreach (var control in Pause)
                actions.Pause.AddDefaultBinding(new DeviceBindingSource(control));
        }
    }
}
