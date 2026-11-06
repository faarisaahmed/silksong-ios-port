using System;
using System.Collections.Generic;
using GlobalEnums;
using InControl;
using UnityEngine;
using UnityEngine.UI;

namespace IOSPort
{
    // On-screen touch controls for the iOS port.
    //  - Shown during gameplay and in the pause/inventory menus. The title screens show only the movement control
    //    (the menus themselves are tapped directly) plus a gear that opens the settings panel.
    //  - Hidden while a real controller is in use, unless TOUCH CONTROLS is set to ALWAYS ON (or OFF: never shown).
    //  - Each button presses whatever controller button its game action is bound to (see TouchGamepad), so in-game
    //    rebinding and button prompts keep working.
    //  - Movement is a joystick, a D-pad or keyboard-style arrow keys (MOVEMENT in settings). The joystick behaves
    //    like the Hollow Knight iOS port (Touch Controls Kit), with its art: a small base ring and an "L" knob that
    //    follows the finger well past the ring and springs back smoothly; in game a touch anywhere in the lower-left
    //    zone grabs it (and the D-pad).
    //  - Hold MENU + INV together to enter remap mode: drag to move, tap a button to change its action or shape or
    //    delete it, ADD for a new one. The game's HUD shows there as boxes that work the same way (see HudLayout):
    //    one for all of it at first, which the action menu can split into the masks, the spool and the rosary/shard
    //    counters. The layout is saved in PlayerPrefs.
    // Art: button icons from the Silksong mobile HUD (Resources/IOSPortHud).
    [DefaultExecutionOrder(-1000)]
    public class TouchControls : MonoBehaviour
    {
        [Serializable]
        class Item
        {
            public string id;   // "stick" or a slot name
            public string role; // action shown/pressed by this button
            public float x, y;  // centre, in safe-area fractions
            public float size;  // height, in safe-area heights (width follows the icon's aspect ratio)
            public string shape; // "round", "square" or "wide"; empty: the action's own button art
            // HUD items (role "hud"): the HudLayout blocks they move, as bits; x, y is their offset in screen heights.
            public int hud;
        }

        [Serializable]
        class Layout
        {
            public int version = LayoutVersion;
            public float opacity = 0.6f;
            public List<Item> items = new List<Item>();
            // v7's two HUD block offsets, in screen heights (HUD items since v8).
            public float hudMasksX, hudMasksY, hudSpoolX, hudSpoolY;
        }

        class Role
        {
            public string Id, Name;
            public Func<HeroActions, PlayerAction> Action;
            public Sprite Sprite, Glyph; // the button art, and its symbol alone (for the other shapes)
        }

        class Control
        {
            public Item Item;
            public Role Role;
            public RectTransform Rect;
            public Image Image, Glyph;
            public bool Pressed, Forwarded;
            public float PressedAt = -1f;
            // A tap that ends before ChordWindow still presses, for this long after the finger lifts.
            public float TapUntil = -1f;
        }

        const string PrefsKey = "IOSPort.TouchLayout2";
        const int LayoutVersion = 8;
        const string HudRole = "hud";
        const int AllHud = 7; // every HudLayout block

        static readonly string[] Shapes = { "round", "square", "wide" };
        const float WideAspect = 1.94f; // the game's wide buttons (MAP)
        // Choices for a HUD item in the action menu, and the names its blocks go by.
        static readonly (string name, int blocks)[] HudChoices =
            { ("MASKS + SPOOL", AllHud), ("MASKS", 1), ("SPOOL", 2), ("ROSARIES + SHARDS", 4) };
        static readonly string[] HudBlockNames = { "Masks", "Spool", "Rosaries + Shards" };

        // Joystick tuning, taken from the HK port's TCKJoystick (borderSize ~5, smoothFactor 7, 1.2 sensitivity).
        const float StickSensitivity = 1.2f;
        const float StickDeadZone = 0.12f;
        const float StickReturnSpeed = 7f;
        // HK port proportions: the base ring is ~58% of the movement control's size, the knob is as big as the ring,
        // and the knob travels up to 2.4 ring radii from the centre.
        const float JoystickBaseScale = 0.58f;
        const float StickTravel = 2.4f;

        // MENU and INV wait this long before pressing, so pressing both can toggle remap mode instead.
        const float ChordWindow = 0.15f;
        const float TapPressSeconds = 0.1f;

        static TouchControls instance;

        // Testing aid: fake fingers set by TouchScript, keyed by finger number.
        public static readonly Dictionary<int, Vector2> TestTouches = new Dictionary<int, Vector2>();

        static readonly (string id, string name, Func<HeroActions, PlayerAction> action)[] RoleDefs =
        {
            ("jump", "Jump", a => a.Jump),
            ("attack", "Attack", a => a.Attack),
            ("dash", "Dash", a => a.Dash),
            ("bind", "Bind", a => a.Cast),
            ("skill", "Silk Skill", a => a.QuickCast),
            ("harpoon", "Harpoon", a => a.SuperDash),
            ("needolin", "Needolin", a => a.DreamNail),
            ("map", "Map", a => a.QuickMap),
            ("inventory", "Inventory", a => a.OpenInventory),
            ("menu", "Menu", a => a.Pause),
            ("taunt", "Taunt", a => a.Taunt),
        };

        readonly Dictionary<string, Role> roles = new Dictionary<string, Role>();
        readonly List<Control> buttons = new List<Control>();
        // HUD items: in remap mode, outline boxes around the HUD blocks they move.
        readonly List<Control> huds = new List<Control>();
        readonly Dictionary<string, Sprite> frames = new Dictionary<string, Sprite>();
        readonly Dictionary<int, Vector2> touches = new Dictionary<int, Vector2>();
        readonly HashSet<int> newTouches = new HashSet<int>();
        readonly HashSet<int> knownTouches = new HashSet<int>();
        TouchGamepad pad;
        Layout layout;
        Canvas canvas;
        RectTransform hud;
        CanvasGroup group;
        Rect safeArea;
        Vector2Int screenSize;
        Sprite panelSprite;
        Font font, titleFont;
        // Off-white of the game's menu text.
        static readonly Color TextColor = new Color(0.96f, 0.94f, 0.9f, 1f);
        bool controllerInUse;
        string lastState;

        // Movement control (the "stick" layout item): joystick ring + knob, D-pad, or arrow keys. `ring` is the
        // container for all three; directions are indexed right, up, left, down.
        Item stickItem;
        RectTransform ring, knob;
        Image ringImage, knobImage, dpadImage;
        readonly Image[] dpadArms = new Image[4];
        readonly Image[] arrowKeys = new Image[4];
        readonly bool[] dirHeld = new bool[4];
        static readonly InputControlType[] DirControls =
            { InputControlType.DPadRight, InputControlType.DPadUp, InputControlType.DPadLeft, InputControlType.DPadDown };
        PortSettings.MoveStyle moveStyle;
        int stickFinger = -1;
        Vector2 ringCenter, knobOffset;

        // Settings panel, opened from the gear on the title screen or SETTINGS in remap mode.
        RectTransform gear, settingsPanel;
        bool settingsOpen;
        readonly List<(Text text, Func<string> label)> settingsLabels = new List<(Text, Func<string>)>();

        // Remap mode
        bool remapping, chordLatched;
        RectTransform remapPanel, rolePicker;
        Text remapHint;
        Control selected; // a button or a HUD item
        bool selectedStick;
        int dragFinger = -1;
        Vector2 dragStart, dragOffset, hudDragStartOffset;
        bool dragMoved, countersShown, hintSet;
        // Action menu cells, lit when they match the selected control.
        readonly List<(Image image, Func<Control, bool> current)> pickerCells = new List<(Image, Func<Control, bool>)>();
        readonly List<GameObject> buttonOnlyCells = new List<GameObject>();
        readonly List<(RectTransform rect, Action onTap)> panelButtons = new List<(RectTransform, Action)>();
        readonly HashSet<int> panelFingers = new HashSet<int>();

        // Desktop testing: launch with -iosportTouch to show the controls and drive them with the mouse.
        static readonly bool DesktopTest = !Application.isMobilePlatform && Array.IndexOf(Environment.GetCommandLineArgs(), "-iosportTouch") >= 0;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (!Application.isMobilePlatform && !DesktopTest)
                return;
            if (instance != null)
                return;
            var go = new GameObject("IOSPort Touch Controls");
            DontDestroyOnLoad(go);
            instance = go.AddComponent<TouchControls>();
        }

        static Layout DefaultLayout()
        {
            var l = new Layout();
            void Add(string id, float x, float y, float size) => l.items.Add(new Item { id = id, role = id, x = x, y = y, size = size });
            // Sizes and rows measured from the Silksong mobile layout reference (640x288 screenshot): right side is
            // two columns (map top right, Taunt under it on the left with MENU beside it, Bind beside Dash); SELECT top-left; joystick lower-left.
            Add("stick", 0.105f, 0.2f, 0.25f);
            Add("inventory", 0.05f, 0.76f, 0.11f);
            Add("menu", 0.715f, 0.74f, 0.11f);
            Add("taunt", 0.805f, 0.74f, 0.14f);
            Add("map", 0.92f, 0.91f, 0.11f);
            Add("skill", 0.92f, 0.74f, 0.13f);
            Add("needolin", 0.805f, 0.565f, 0.15f);
            Add("harpoon", 0.92f, 0.565f, 0.13f);
            Add("bind", 0.805f, 0.38f, 0.15f);
            Add("dash", 0.92f, 0.38f, 0.17f);
            Add("attack", 0.805f, 0.16f, 0.19f);
            Add("jump", 0.92f, 0.16f, 0.19f);
            l.items.Add(new Item { id = "hud", role = HudRole, hud = AllHud });
            return l;
        }

        void Awake()
        {
            // Trajan Pro, the game's own menu font (copied into Resources/IOSPortFonts).
            font = Resources.Load<Font>("IOSPortFonts/TrajanPro-Regular") ?? Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            titleFont = Resources.Load<Font>("IOSPortFonts/TrajanPro-Bold") ?? font;
            panelSprite = MakeRoundedRect(64, 18);
            foreach (var (id, name, action) in RoleDefs)
                roles[id] = new Role { Id = id, Name = name, Action = action, Sprite = LoadSprite(id), Glyph = LoadSprite("glyph_" + id) };
            foreach (var shape in Shapes)
                frames[shape] = MakeFrameSprite(shape);
            layout = LoadLayout();
            ApplyHudLayout();

            // The game renders a 16:9 view; on wider screens the uncovered edges are never cleared,
            // which shows garbage behind the controls. A camera that draws nothing clears them first.
            var clear = new GameObject("IOSPort Background Clear").AddComponent<Camera>();
            clear.transform.SetParent(transform, false);
            clear.depth = -100f;
            clear.clearFlags = CameraClearFlags.SolidColor;
            clear.backgroundColor = Color.black;
            clear.cullingMask = 0;

            canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32000;
            // Only the settings panel takes UI raycasts (so taps on it don't reach the game's menus underneath);
            // the rest of the HUD is excluded by this group.
            gameObject.AddComponent<GraphicRaycaster>();
            // The HUD fades (opacity, hidden with a controller...) as one group; the settings gear and panel are
            // siblings outside it so no setting can ever hide them.
            hud = NewRect("hud", transform);
            Anchor(hud, Vector2.zero, Vector2.one);
            group = hud.gameObject.AddComponent<CanvasGroup>();
            group.blocksRaycasts = false;
            group.interactable = false;

            BuildMovement();
            foreach (var item in layout.items)
                if (item.id != "stick")
                    AddControl(item);
            BuildRemapPanel();
            BuildRolePicker();
            BuildGear();
            BuildSettingsPanel();
            ApplyMoveStyle();
            Relayout();
        }

        void OnDestroy()
        {
            if (pad != null && InputManager.IsSetup)
                InputManager.DetachDevice(pad);
        }

        static Sprite LoadSprite(string name) => Resources.Load<Sprite>("IOSPortHud/" + name);

        void BuildMovement()
        {
            stickItem = layout.items.Find(i => i.id == "stick");
            if (stickItem == null)
            {
                stickItem = DefaultLayout().items.Find(i => i.id == "stick");
                layout.items.Add(stickItem);
            }
            ring = NewRect("stick ring", hud);
            ringImage = ring.gameObject.AddComponent<Image>();
            ringImage.sprite = LoadSprite("stickbase");
            knob = NewRect("stick knob", ring);
            knobImage = knob.gameObject.AddComponent<Image>();
            knobImage.sprite = LoadSprite("stickknob");

            // D-pad: the cross outline, plus a highlight over each arm while it's held.
            var armSprite = MakeRoundedRect(32, 6);
            for (int i = 0; i < 4; i++)
            {
                var arm = NewRect("dpad arm " + i, ring);
                arm.anchorMin = arm.anchorMax = new Vector2(0.5f, 0.5f);
                arm.pivot = new Vector2(0f, 0.5f);
                arm.localEulerAngles = new Vector3(0f, 0f, 90f * i);
                dpadArms[i] = arm.gameObject.AddComponent<Image>();
                dpadArms[i].sprite = armSprite;
            }
            dpadImage = NewRect("dpad", ring).gameObject.AddComponent<Image>();
            dpadImage.sprite = MakeDPadSprite(256);
            Anchor(dpadImage.rectTransform, Vector2.zero, Vector2.one);

            // Arrow keys in the keyboard's inverted-T layout.
            var keySprite = MakeKeySprite(128);
            for (int i = 0; i < 4; i++)
            {
                var key = NewRect("arrow key " + i, ring);
                key.anchorMin = key.anchorMax = new Vector2(0.5f, 0.5f);
                key.localEulerAngles = new Vector3(0f, 0f, 90f * i);
                arrowKeys[i] = key.gameObject.AddComponent<Image>();
                arrowKeys[i].sprite = keySprite;
            }
        }

        // Shows the parts of the movement control for the chosen style.
        void ApplyMoveStyle()
        {
            moveStyle = PortSettings.Move;
            bool joystick = moveStyle == PortSettings.MoveStyle.Joystick, dpad = moveStyle == PortSettings.MoveStyle.DPad;
            ringImage.enabled = joystick;
            knob.gameObject.SetActive(joystick);
            dpadImage.gameObject.SetActive(dpad);
            for (int i = 0; i < 4; i++)
            {
                dpadArms[i].gameObject.SetActive(dpad);
                arrowKeys[i].gameObject.SetActive(moveStyle == PortSettings.MoveStyle.ArrowKeys);
            }
            ReleaseMovement();
            PlaceStick();
        }

        void BuildGear()
        {
            gear = NewRect("settings gear", transform);
            var image = gear.gameObject.AddComponent<Image>();
            image.sprite = MakeGearSprite(128);
            image.color = new Color(1f, 1f, 1f, 0.75f);
        }

        static bool IsHud(Control c) => c != null && c.Item.role == HudRole;

        Control AddControl(Item item)
        {
            var c = new Control { Item = item };
            c.Rect = NewRect(item.id, hud);
            c.Image = c.Rect.gameObject.AddComponent<Image>();
            if (IsHud(c))
            {
                c.Image.sprite = panelSprite;
                c.Image.type = Image.Type.Sliced;
                c.Image.raycastTarget = false;
                c.Rect.anchorMin = c.Rect.anchorMax = Vector2.zero;
                // Not faded with the buttons, so it stays easy to find.
                c.Rect.gameObject.AddComponent<CanvasGroup>().ignoreParentGroups = true;
                c.Rect.gameObject.SetActive(false);
                huds.Add(c);
                return c;
            }
            if (!roles.TryGetValue(item.role ?? "", out c.Role))
                c.Role = roles["jump"];
            c.Image.preserveAspect = true;
            c.Glyph = NewRect("glyph", c.Rect).gameObject.AddComponent<Image>();
            c.Glyph.preserveAspect = true;
            Dress(c);
            buttons.Add(c);
            return c;
        }

        void RemoveControl(Control c)
        {
            layout.items.Remove(c.Item);
            buttons.Remove(c);
            huds.Remove(c);
            Destroy(c.Rect.gameObject);
            if (selected == c)
                selected = null;
            ApplyHudLayout();
        }

        // Each action's own art has a shape; that one is drawn as is, the others as a frame around its symbol.
        static string OwnShape(Role role)
        {
            if (role.Id == "menu" || role.Id == "inventory")
                return "square";
            var r = role.Sprite != null ? role.Sprite.rect : new Rect(0, 0, 1, 1);
            return r.width / r.height > 1.4f ? "wide" : "round";
        }

        static string ShapeOf(Control c) => string.IsNullOrEmpty(c.Item.shape) ? OwnShape(c.Role) : c.Item.shape;

        void Dress(Control c)
        {
            string shape = ShapeOf(c);
            bool own = shape == OwnShape(c.Role) || c.Role.Glyph == null;
            c.Image.sprite = own ? c.Role.Sprite : frames[shape];
            c.Glyph.sprite = c.Role.Glyph;
            c.Glyph.gameObject.SetActive(!own);
            // The symbol fills the middle of the frame.
            float inset = shape == "wide" ? 0.2f : 0.22f;
            Anchor(c.Glyph.rectTransform, new Vector2(inset * (shape == "wide" ? 0.6f : 1f), inset), new Vector2(1f - inset * (shape == "wide" ? 0.6f : 1f), 1f - inset));
        }

        void SetRole(Control c, Role role)
        {
            c.Role = role;
            c.Item.role = role.Id;
            Dress(c);
            Place(c);
        }

        // ---- per frame ----

        void Update()
        {
            if (pad == null && InputManager.IsSetup)
            {
                pad = new TouchGamepad();
                InputManager.AttachDevice(pad);
            }
            if (Screen.safeArea != safeArea || Screen.width != screenSize.x || Screen.height != screenSize.y)
                Relayout();

            ReadTouches();
            bool controllerConnected = ControllerConnected();
            if (touches.Count > 0 || !controllerConnected)
                controllerInUse = false;
            else if (pad != null && InputManager.ActiveDevice != pad && InputManager.ActiveDevice.DeviceClass == InputDeviceClass.Controller)
                controllerInUse = true;
            if (PortSettings.Move != moveStyle)
                ApplyMoveStyle();

            // With no controller connected the touch controls always show, whatever the setting.
            var mode = PortSettings.Touch;
            bool wanted = mode == PortSettings.TouchMode.Always
                || (mode == PortSettings.TouchMode.Auto ? !controllerInUse : !controllerConnected);
            var gm = GameManager.UnsafeInstance;
            bool inGame = HudWanted(), onTitle = !inGame && OnTitleScreens();
            bool paused = inGame && gm.isPaused && !(PlayerData.instance != null && PlayerData.instance.isInventoryOpen);
            bool showGame = inGame && wanted;
            bool showTitle = onTitle && wanted;   // movement control only
            bool full = remapping || showGame;

            // Remap mode shows the opacity too, so FADE - / + can be seen working (its panels ignore it).
            group.alpha = full || showTitle ? layout.opacity : 0f;
            foreach (var c in buttons)
                c.Rect.gameObject.SetActive(full);
            // The settings gear is always there on the title screens and in the pause menu.
            gear.gameObject.SetActive((onTitle || paused) && !remapping && !settingsOpen);
            if (Debug.isDebugBuild)
            {
                string state = $"mode={mode} controller={controllerConnected}/{controllerInUse} title={onTitle} game={inGame} paused={paused} settings={settingsOpen} remap={remapping}";
                if (state != lastState)
                    Debug.Log("[IOSPort] touch HUD: " + (lastState = state));
            }
            settingsPanel.gameObject.SetActive(settingsOpen);
            remapPanel.gameObject.SetActive(remapping && !settingsOpen);
            if (!remapping || settingsOpen)
                rolePicker.gameObject.SetActive(false);

            if (settingsOpen)
            {
                pad?.ReleaseAll();
                ReleaseMovement();
                HandlePanelTaps();
                return;
            }
            if (gear.gameObject.activeSelf)
            {
                foreach (var id in newTouches)
                {
                    if (RectTransformUtility.RectangleContainsScreenPoint(gear, touches[id]))
                    {
                        OpenSettings();
                        return;
                    }
                }
            }
            if (remapping)
            {
                UpdateRemap();
                return;
            }
            if (showGame)
            {
                UpdatePlaying();
                return;
            }
            // Title screens (touches also go straight to the game's menus) or nothing at all.
            pad?.ReleaseAll();
            ResetButtons();
            if (onTitle)
                UpdateTitle(showTitle);
            else
                ReleaseMovement();
        }

        // In game (playing, paused, inventory, cutscenes); not on the title/menu screens.
        static bool HudWanted()
        {
            var gm = GameManager.UnsafeInstance;
            if (gm == null)
                return false;
            var state = gm.GameState;
            return state != GameState.MAIN_MENU && state != GameState.INACTIVE && state != GameState.PRIMER;
        }

        // A real controller (not the touch controls' own virtual one) is attached.
        bool ControllerConnected()
        {
            if (!InputManager.IsSetup)
                return false;
            foreach (var device in InputManager.Devices)
                if (device != pad && device.IsAttached && device.DeviceClass == InputDeviceClass.Controller)
                    return true;
            return false;
        }

        // Title menu and its sub-screens (options, save select, mode select...).
        static bool OnTitleScreens()
        {
            var gm = GameManager.UnsafeInstance;
            return gm != null && gm.GameState == GameState.MAIN_MENU;
        }

        void UpdateTitle(bool showMovement)
        {
            if (!showMovement || pad == null)
            {
                ReleaseMovement();
                return;
            }
            UpdateMovement(new HashSet<int>(), titleScreen: true);
        }

        void OpenSettings()
        {
            settingsOpen = true;
            pad?.ReleaseAll();
            ReleaseMovement();
            RefreshSettingsLabels();
            // The finger that opened it doesn't count as a tap on the panel.
            panelFingers.Clear();
            panelFingers.UnionWith(touches.Keys);
        }

        void ReadTouches()
        {
            touches.Clear();
            for (int i = 0; i < Input.touchCount; i++)
            {
                var t = Input.GetTouch(i);
                if (t.phase != TouchPhase.Ended && t.phase != TouchPhase.Canceled)
                    touches[t.fingerId] = t.position;
            }
            if (DesktopTest && Input.touchCount == 0 && Input.GetMouseButton(0))
                touches[0] = Input.mousePosition;
            foreach (var t in TestTouches) // scripted touches (TouchScript), in safe-area fractions
                touches[100 + t.Key] = new Vector2(safeArea.x + t.Value.x * safeArea.width, safeArea.y + t.Value.y * safeArea.height);

            newTouches.Clear();
            foreach (var id in touches.Keys)
                if (!knownTouches.Contains(id))
                    newTouches.Add(id);
            knownTouches.Clear();
            knownTouches.UnionWith(touches.Keys);
        }

        void ResetButtons()
        {
            foreach (var c in buttons)
            {
                c.Pressed = c.Forwarded = false;
                c.PressedAt = -1f;
                c.TapUntil = -1f;
                Style(c);
            }
            chordLatched = false;
        }

        void UpdatePlaying()
        {
            if (pad == null)
                return;
            pad.ReleaseAll();
            var handler = ManagerSingleton<InputHandler>.UnsafeInstance;
            var actions = handler != null ? handler.inputActions : null;

            // Buttons first, so a touch on a button never grabs the stick.
            var onButton = new HashSet<int>();
            foreach (var c in buttons)
            {
                bool pressed = false;
                foreach (var t in touches)
                {
                    if (t.Key != stickFinger && Contains(c, t.Value, 1.1f))
                    {
                        pressed = true;
                        onButton.Add(t.Key);
                    }
                }
                if (pressed && !c.Pressed)
                    c.PressedAt = Time.unscaledTime;
                if (!pressed)
                {
                    if (c.Pressed && !c.Forwarded && !chordLatched && (c.Role.Id == "menu" || c.Role.Id == "inventory"))
                        c.TapUntil = Time.unscaledTime + TapPressSeconds;
                    c.PressedAt = -1f;
                    c.Forwarded = false;
                }
                c.Pressed = pressed;
            }

            // MENU + INV together toggles remap mode; each waits a moment before pressing so the chord doesn't
            // also open the pause menu or inventory.
            var menu = buttons.Find(b => b.Role.Id == "menu");
            var inv = buttons.Find(b => b.Role.Id == "inventory");
            if (menu != null && inv != null)
            {
                if (menu.Pressed && inv.Pressed && !menu.Forwarded && !inv.Forwarded && !chordLatched)
                {
                    chordLatched = true;
                    StartRemap();
                    return;
                }
                if (!menu.Pressed && !inv.Pressed)
                    chordLatched = false;
            }

            var gm = GameManager.UnsafeInstance;
            bool inventoryOpen = PlayerData.instance != null && PlayerData.instance.isInventoryOpen;
            bool inMenu = gm != null && (gm.isPaused || inventoryOpen);
            foreach (var c in buttons)
            {
                bool chordButton = c.Role.Id == "menu" || c.Role.Id == "inventory";
                if (c.Pressed && !c.Forwarded && !(chordButton && chordLatched))
                {
                    if (!chordButton || Time.unscaledTime - c.PressedAt >= ChordWindow)
                    {
                        c.Forwarded = true;
                        if (Debug.isDebugBuild && actions != null)
                            Debug.Log("[IOSPort] touch " + c.Role.Id + " -> " + BoundControl(c.Role.Action(actions)));
                    }
                }
                if (actions != null && !c.Pressed && Time.unscaledTime < c.TapUntil)
                    pad.SetHeld(BoundControl(c.Role.Action(actions)), true);
                if (actions != null && c.Forwarded && c.Pressed)
                {
                    // In the inventory, Harpoon and Silk Skill act as the shoulder buttons: previous / next pane.
                    if (inventoryOpen && c.Role.Id == "harpoon")
                        pad.SetHeld(BoundControl(actions.PaneLeft), true);
                    else if (inventoryOpen && c.Role.Id == "skill")
                        pad.SetHeld(BoundControl(actions.PaneRight), true);
                    else
                        pad.SetHeld(BoundControl(c.Role.Action(actions)), true);
                    // In the pause menu and inventory, Jump always confirms and Bind always goes back (A / B, as
                    // on PC with a controller and in the HK port), even if those actions were rebound in game.
                    if (inMenu && c.Role.Id == "jump")
                        pad.SetHeld(BoundControl(actions.MenuSubmit), true);
                    else if (inMenu && c.Role.Id == "bind")
                        pad.SetHeld(BoundControl(actions.MenuCancel), true);
                }
                Style(c);
            }

            UpdateMovement(onButton, titleScreen: false);
        }

        void UpdateMovement(HashSet<int> onButton, bool titleScreen)
        {
            if (moveStyle == PortSettings.MoveStyle.ArrowKeys)
            {
                UpdateArrowKeys(onButton);
                return;
            }
            if (stickFinger >= 0 && !touches.ContainsKey(stickFinger))
                stickFinger = -1;
            if (stickFinger < 0)
            {
                // A new touch in the lower-left zone grabs the stick/D-pad (on the title screens only a touch on the
                // control itself, so the menus stay tappable); it stays where it is and reads the finger's direction.
                foreach (var id in newTouches)
                {
                    if (!onButton.Contains(id) && (titleScreen ? InMovementBox(touches[id], 1.2f) : InStickZone(touches[id])))
                    {
                        stickFinger = id;
                        break;
                    }
                }
            }
            if (moveStyle == PortSettings.MoveStyle.DPad)
                UpdateDPad();
            else
                UpdateStick();
        }

        // D-pad: 8 directions from the finger's angle around the centre (diagonals press two arms).
        void UpdateDPad()
        {
            Array.Clear(dirHeld, 0, 4);
            if (stickFinger >= 0)
            {
                var offset = touches[stickFinger] - ringCenter * canvas.scaleFactor;
                if (offset.magnitude > ring.sizeDelta.x * 0.5f * canvas.scaleFactor * 0.18f)
                {
                    float a = Mathf.Atan2(offset.y, offset.x) * Mathf.Rad2Deg;
                    dirHeld[0] = Mathf.Abs(a) < 67.5f;
                    dirHeld[1] = a > 22.5f && a < 157.5f;
                    dirHeld[2] = Mathf.Abs(a) > 112.5f;
                    dirHeld[3] = a < -22.5f && a > -157.5f;
                }
            }
            ApplyDirections();
        }

        // Arrow keys: each finger presses whichever key it's on, so sliding between keys and pressing two at once
        // (diagonals) both work.
        void UpdateArrowKeys(HashSet<int> onButton)
        {
            Array.Clear(dirHeld, 0, 4);
            foreach (var t in touches)
            {
                if (onButton.Contains(t.Key))
                    continue;
                for (int i = 0; i < 4; i++)
                {
                    var key = arrowKeys[i].rectTransform;
                    var half = key.sizeDelta * canvas.scaleFactor * 0.56f;
                    var d = t.Value - (ringCenter + key.anchoredPosition) * canvas.scaleFactor;
                    if (Mathf.Abs(d.x) <= half.x && Mathf.Abs(d.y) <= half.y)
                        dirHeld[i] = true;
                }
            }
            ApplyDirections();
        }

        void ApplyDirections()
        {
            for (int i = 0; i < 4; i++)
            {
                pad?.SetHeld(DirControls[i], dirHeld[i]);
                dpadArms[i].color = new Color(1f, 1f, 1f, dirHeld[i] ? 0.45f : 0f);
                arrowKeys[i].color = new Color(1f, 1f, 1f, dirHeld[i] ? 1f : 0.8f);
                arrowKeys[i].rectTransform.localScale = Vector3.one * (dirHeld[i] ? 0.92f : 1f);
            }
        }

        void ReleaseMovement()
        {
            stickFinger = -1;
            UpdateKnob(Vector2.zero, instant: true);
            Array.Clear(dirHeld, 0, 4);
            ApplyDirections();
            if (pad != null)
                pad.Stick = Vector2.zero;
        }

        bool InMovementBox(Vector2 screenPoint, float slack)
        {
            var half = ring.sizeDelta * canvas.scaleFactor * 0.5f * slack;
            var d = screenPoint - ringCenter * canvas.scaleFactor;
            return Mathf.Abs(d.x) <= half.x && Mathf.Abs(d.y) <= half.y;
        }

        void TintMovement(Color color)
        {
            ringImage.color = color;
            dpadImage.color = color;
            if (remapping)
                foreach (var key in arrowKeys)
                    key.color = color;
        }

        void UpdateStick()
        {
            Vector2 value = Vector2.zero;
            if (stickFinger >= 0)
            {
                float radius = ring.sizeDelta.x * 0.5f * canvas.scaleFactor * StickTravel;
                var offset = Vector2.ClampMagnitude(touches[stickFinger] - ringCenter * canvas.scaleFactor, radius);
                UpdateKnob(offset / canvas.scaleFactor, instant: true);
                value = Vector2.ClampMagnitude(offset / radius * StickSensitivity, 1f);
                if (value.magnitude < StickDeadZone)
                    value = Vector2.zero;
            }
            else
            {
                UpdateKnob(Vector2.zero, instant: false);
            }
            pad.Stick = value;
            knobImage.color = new Color(1f, 1f, 1f, stickFinger >= 0 ? 1f : 0.75f);
        }

        void UpdateKnob(Vector2 target, bool instant)
        {
            knobOffset = instant ? target : Vector2.Lerp(knobOffset, target, 1f - Mathf.Exp(-StickReturnSpeed * 3f * Time.unscaledDeltaTime));
            knob.anchoredPosition = knobOffset;
        }

        bool InStickZone(Vector2 screenPoint)
        {
            // Left ~45% of the screen, lower ~65%, plus anywhere on the ring itself.
            float radius = ring.sizeDelta.x * 0.5f * canvas.scaleFactor;
            if ((screenPoint - ringCenter * canvas.scaleFactor).sqrMagnitude <= radius * radius * 1.6f)
                return true;
            return screenPoint.x < safeArea.x + safeArea.width * 0.45f && screenPoint.y < safeArea.y + safeArea.height * 0.65f;
        }

        void Style(Control c)
        {
            bool lit = c.Pressed && (c.Forwarded || remapping);
            c.Image.color = c.Glyph.color = remapping && c == selected ? new Color(1f, 0.82f, 0.4f, 1f) : new Color(1f, 1f, 1f, lit ? 1f : 0.8f);
            c.Rect.localScale = Vector3.one * (c.Pressed && !remapping ? 0.92f : 1f);
        }

        static InputControlType BoundControl(PlayerAction action)
        {
            foreach (var binding in action.Bindings)
                if (binding is DeviceBindingSource device)
                    return device.Control;
            return InputControlType.None;
        }

        // ---- Remap mode ----

        void StartRemap()
        {
            remapping = true;
            selected = null;
            selectedStick = false;
            dragFinger = -1;
            // The rosary and shard counters only show when the amounts change; keep them up to place them.
            if (!countersShown)
            {
                CurrencyCounter.Show(CurrencyType.Money);
                CurrencyCounter.Show(CurrencyType.Shard);
                countersShown = true;
            }
            pad?.ReleaseAll();
            stickFinger = -1;
            UpdateKnob(Vector2.zero, instant: true);
            // Fingers already down (the MENU+INV chord) don't count as taps.
            panelFingers.Clear();
            panelFingers.UnionWith(touches.Keys);
            UpdateRemapHint();
        }

        void StopRemap()
        {
            remapping = false;
            selected = null;
            selectedStick = false;
            foreach (var h in huds)
                h.Rect.gameObject.SetActive(false);
            if (countersShown)
            {
                CurrencyCounter.Hide(CurrencyType.Money);
                CurrencyCounter.Hide(CurrencyType.Shard);
                countersShown = false;
            }
            rolePicker.gameObject.SetActive(false);
            SaveLayout();
            ResetButtons();
            chordLatched = touches.Count > 0; // don't re-trigger while the chord fingers are still down
        }

        void UpdateRemap()
        {
            foreach (var c in buttons)
            {
                c.Pressed = false;
                Style(c);
            }
            TintMovement(selectedStick ? new Color(1f, 0.82f, 0.4f, 1f) : Color.white);
            PlaceHudBoxes();

            // MENU + INV again leaves remap mode.
            var menu = buttons.Find(b => b.Role.Id == "menu");
            var inv = buttons.Find(b => b.Role.Id == "inventory");
            // Two fingers, one on each, even if the first one already started a drag.
            bool chord = menu != null && inv != null && touches.Count >= 2 && AnyTouchIn(menu) && AnyTouchIn(inv);
            if (chord && !chordLatched)
            {
                if (dragFinger >= 0 && dragMoved)
                    SaveLayout();
                dragFinger = -1;
                chordLatched = true;
                StopRemap();
                return;
            }
            if (!chord && touches.Count == 0)
                chordLatched = false;

            HandlePanelTaps();

            if (dragFinger >= 0)
            {
                if (!touches.TryGetValue(dragFinger, out var p))
                {
                    // Finger lifted: a tap (no real movement) on a button opens the action picker.
                    if (!dragMoved && selected != null)
                        OpenRolePicker();
                    dragFinger = -1;
                    SaveLayout();
                    return;
                }
                if ((p - dragStart).magnitude > 12f * Mathf.Max(1f, Screen.dpi / 160f))
                    dragMoved = true;
                if (dragMoved && IsHud(selected))
                {
                    SetHudOffset(selected, hudDragStartOffset + (p - dragStart) / Screen.height);
                    return;
                }
                if (dragMoved)
                {
                    var target = p + dragOffset;
                    var item = selectedStick ? stickItem : selected?.Item;
                    if (item != null)
                    {
                        item.x = Mathf.Clamp01((target.x - safeArea.x) / safeArea.width);
                        item.y = Mathf.Clamp01((target.y - safeArea.y) / safeArea.height);
                        if (selectedStick) PlaceStick(); else Place(selected);
                    }
                }
                return;
            }

            foreach (var id in newTouches)
            {
                if (panelFingers.Contains(id))
                    continue;
                var point = touches[id];
                if (rolePicker.gameObject.activeSelf && RectTransformUtility.RectangleContainsScreenPoint(rolePicker, point))
                    continue;
                var hit = buttons.FindLast(c => Contains(c, point, 1f));
                bool stickHit = hit == null && InMovementBox(point, 1f);
                if (hit == null && !stickHit)
                    hit = HudBoxAt(point);
                rolePicker.gameObject.SetActive(false);
                selected = hit;
                selectedStick = stickHit;
                if (IsHud(hit))
                {
                    dragFinger = id;
                    dragStart = point;
                    dragMoved = false;
                    hudDragStartOffset = new Vector2(hit.Item.x, hit.Item.y);
                }
                else if (hit != null || stickHit)
                {
                    dragFinger = id;
                    dragStart = point;
                    dragMoved = false;
                    dragOffset = (stickHit ? ringCenter : hit.Rect.anchoredPosition) * canvas.scaleFactor - point;
                }
                UpdateRemapHint();
                break;
            }
        }

        bool AnyTouchIn(Control c)
        {
            foreach (var t in touches)
                if (Contains(c, t.Value, 1.1f))
                    return true;
            return false;
        }

        void HandlePanelTaps()
        {
            foreach (var id in newTouches)
            {
                foreach (var (rect, onTap) in panelButtons)
                {
                    if (rect.gameObject.activeInHierarchy && RectTransformUtility.RectangleContainsScreenPoint(rect, touches[id]))
                    {
                        panelFingers.Add(id);
                        onTap();
                        break;
                    }
                }
            }
            panelFingers.IntersectWith(touches.Keys);
        }

        void UpdateRemapHint()
        {
            if (IsHud(selected))
            {
                remapHint.text = "HUD (" + HudName(selected.Item.hud) + ") selected: drag to move it, tap it to split it up or delete it.";
                return;
            }
            string what = selectedStick ? "Movement control" : selected != null ? selected.Role.Name : null;
            remapHint.text = what == null
                ? "REMAP: drag a control or a HUD box to move it, tap one to change or delete it. MENU + INV or DONE to finish."
                : what + " selected: SIZE - / + to resize." + (selectedStick ? "" : " Tap it for its action, shape or DELETE.");
        }

        static string HudName(int blocks)
        {
            var names = new List<string>();
            for (int b = 0; b < HudLayout.BlockCount; b++)
                if ((blocks & (1 << b)) != 0)
                    names.Add(HudBlockNames[b]);
            return string.Join(" + ", names);
        }

        void Fade(float delta)
        {
            layout.opacity = Mathf.Clamp(layout.opacity + delta, 0.15f, 1f);
            SaveLayout();
            remapHint.text = $"Buttons at {Mathf.RoundToInt(layout.opacity * 100f)}% opacity.";
        }

        // A new button in the middle of the screen (below the remap panel): a round Attack button, ready to be
        // changed in the action menu.
        void AddNewButton()
        {
            int n = 1;
            while (layout.items.Exists(i => i.id == "button" + n))
                n++;
            var item = new Item { id = "button" + n, role = "attack", x = 0.5f, y = 0.22f, size = 0.15f };
            layout.items.Add(item);
            var c = AddControl(item);
            Place(c);
            KeepRemapUiOnTop();
            selected = c;
            selectedStick = false;
            SaveLayout();
            UpdateRemapHint();
        }

        void KeepRemapUiOnTop()
        {
            remapPanel.SetAsLastSibling();
            rolePicker.SetAsLastSibling();
        }

        // MENU (with INV) is how remap mode is reached in game, so the last one stays.
        bool IsLastMenu(Control c) => c != null && !IsHud(c) && c.Role.Id == "menu" && buttons.FindAll(b => b.Role.Id == "menu").Count == 1;

        bool KeepMenu(Control c)
        {
            if (!IsLastMenu(c))
                return false;
            remapHint.text = "Keep one MENU button: with INV it opens this editor. ADD another one first to change this one.";
            hintSet = true;
            return true;
        }

        void BuildRemapPanel()
        {
            remapPanel = NewRect("remap panel", hud);
            Anchor(remapPanel, new Vector2(0.17f, 0.36f), new Vector2(0.75f, 0.6f));
            var bg = remapPanel.gameObject.AddComponent<Image>();
            bg.sprite = panelSprite;
            bg.type = Image.Type.Sliced;
            bg.color = new Color(0f, 0f, 0f, 0.7f);
            remapPanel.gameObject.AddComponent<CanvasGroup>().ignoreParentGroups = true;

            remapHint = NewText(remapPanel, "", 30);
            Anchor((RectTransform)remapHint.transform, new Vector2(0.03f, 0.52f), new Vector2(0.97f, 0.95f));

            var actions = new (string, Action)[]
            {
                ("SIZE -", () => Resize(1f / 1.1f)),
                ("SIZE +", () => Resize(1.1f)),
                ("FADE -", () => Fade(-0.1f)),
                ("FADE +", () => Fade(0.1f)),
                ("ADD", AddNewButton),
                ("RESET", ResetLayout),
                ("SETTINGS", OpenSettings),
                ("DONE", StopRemap),
            };
            for (int i = 0; i < actions.Length; i++)
            {
                var b = NewRect(actions[i].Item1, remapPanel);
                Anchor(b, new Vector2(i / (float)actions.Length + 0.01f, 0.08f), new Vector2((i + 1) / (float)actions.Length - 0.01f, 0.45f));
                var img = b.gameObject.AddComponent<Image>();
                img.sprite = panelSprite;
                img.type = Image.Type.Sliced;
                img.color = new Color(1f, 1f, 1f, 0.25f);
                var text = NewText(b, actions[i].Item1, 24);
                Anchor((RectTransform)text.transform, Vector2.zero, Vector2.one);
                panelButtons.Add((b, actions[i].Item2));
            }
            remapPanel.gameObject.SetActive(false);
        }

        // ---- HUD items (HudLayout) ----

        void ApplyHudLayout()
        {
            for (int b = 0; b < HudLayout.BlockCount; b++)
            {
                HudLayout.Shown[b] = false;
                HudLayout.Offsets[b] = Vector2.zero;
            }
            foreach (var item in layout.items)
                if (item.role == HudRole)
                    for (int b = 0; b < HudLayout.BlockCount; b++)
                        if ((item.hud & (1 << b)) != 0)
                        {
                            HudLayout.Shown[b] = true;
                            HudLayout.Offsets[b] = new Vector2(item.x, item.y);
                        }
        }

        // Where these blocks are without any offset (screen pixels); see HudLayout.BaseRect.
        static bool HudBaseRect(int blocks, out Rect rect)
        {
            rect = default;
            bool any = false;
            for (int b = 0; b < HudLayout.BlockCount; b++)
            {
                if ((blocks & (1 << b)) == 0 || !HudLayout.BaseRect((HudLayout.Block)b, out var r))
                    continue;
                rect = any ? Rect.MinMaxRect(Mathf.Min(rect.xMin, r.xMin), Mathf.Min(rect.yMin, r.yMin), Mathf.Max(rect.xMax, r.xMax), Mathf.Max(rect.yMax, r.yMax)) : r;
                any = true;
            }
            return any;
        }

        static bool HudRect(Item item, out Rect rect)
        {
            if (!HudBaseRect(item.hud, out rect))
                return false;
            rect.position += new Vector2(item.x, item.y) * Screen.height;
            return true;
        }

        void SetHudOffset(Control c, Vector2 o)
        {
            // Keep the box on screen.
            if (HudBaseRect(c.Item.hud, out var r))
            {
                o.x = Mathf.Clamp(o.x, -r.xMin / Screen.height, (Screen.width - r.xMax) / Screen.height);
                o.y = Mathf.Clamp(o.y, -r.yMin / Screen.height, (Screen.height - r.yMax) / Screen.height);
            }
            c.Item.x = o.x;
            c.Item.y = o.y;
            ApplyHudLayout();
        }

        // Gives these HUD blocks to `c`, taking them off any other HUD item (which goes when it has none left).
        void TakeHud(Control c, int blocks)
        {
            c.Item.hud = blocks;
            foreach (var other in huds.ToArray())
            {
                if (other == c)
                    continue;
                other.Item.hud &= ~blocks;
                if (other.Item.hud == 0)
                    RemoveControl(other);
            }
            ApplyHudLayout();
        }

        // Keeps these blocks in `c` and moves the rest of what it had to a new HUD item in the same place.
        void SplitHud(Control c, int blocks)
        {
            int rest = c.Item.hud & ~blocks;
            if (rest != 0)
            {
                int n = 2;
                while (layout.items.Exists(i => i.id == "hud" + n))
                    n++;
                var item = new Item { id = "hud" + n, role = HudRole, hud = rest, x = c.Item.x, y = c.Item.y };
                layout.items.Add(item);
                AddControl(item);
                KeepRemapUiOnTop();
            }
            TakeHud(c, blocks);
        }

        // Turns a button into a HUD item (or back), keeping it where it was on screen.
        void ChangeKind(Control c, Role role, int blocks)
        {
            Vector2 at = IsHud(c)
                ? (HudRect(c.Item, out var r) ? r.center : new Vector2(Screen.width, Screen.height) * 0.5f)
                : c.Rect.anchoredPosition * canvas.scaleFactor;
            var item = c.Item;
            RemoveControl(c);
            layout.items.Add(item);
            item.shape = null;
            if (role != null)
            {
                item.role = role.Id;
                item.hud = 0;
                item.size = 0.15f;
                item.x = Mathf.Clamp01((at.x - safeArea.x) / safeArea.width);
                item.y = Mathf.Clamp01((at.y - safeArea.y) / safeArea.height);
            }
            else
            {
                item.role = HudRole;
                item.x = item.y = 0f;
            }
            selected = AddControl(item);
            if (role != null)
            {
                Place(selected);
                ApplyHudLayout();
            }
            else
            {
                TakeHud(selected, blocks);
                if (HudBaseRect(blocks, out var home))
                    SetHudOffset(selected, (at - home.center) / Screen.height);
            }
            KeepRemapUiOnTop();
        }

        Control HudBoxAt(Vector2 point)
        {
            float pad = 12f * Mathf.Max(1f, Screen.dpi / 160f);
            for (int i = huds.Count - 1; i >= 0; i--)
                if (huds[i].Rect.gameObject.activeSelf && HudRect(huds[i].Item, out var r)
                    && point.x >= r.xMin - pad && point.x <= r.xMax + pad && point.y >= r.yMin - pad && point.y <= r.yMax + pad)
                    return huds[i];
            return null;
        }

        void PlaceHudBoxes()
        {
            float pad = 8f * Mathf.Max(1f, Screen.dpi / 160f);
            foreach (var h in huds)
            {
                bool showing = HudRect(h.Item, out var r);
                h.Rect.gameObject.SetActive(showing);
                if (!showing)
                    continue;
                h.Rect.sizeDelta = (r.size + Vector2.one * pad * 2f) / canvas.scaleFactor;
                h.Rect.anchoredPosition = r.center / canvas.scaleFactor;
                h.Image.color = selected == h ? new Color(1f, 0.82f, 0.4f, 0.35f) : new Color(1f, 1f, 1f, 0.18f);
            }
        }

        // The action menu: the actions, the HUD parts (a button can become one and back), shapes and DELETE.
        void BuildRolePicker()
        {
            rolePicker = NewRect("action picker", hud);
            Anchor(rolePicker, new Vector2(0.15f, 0.61f), new Vector2(0.77f, 0.99f));
            var bg = rolePicker.gameObject.AddComponent<Image>();
            bg.sprite = panelSprite;
            bg.type = Image.Type.Sliced;
            bg.color = new Color(0f, 0f, 0f, 0.8f);
            rolePicker.gameObject.AddComponent<CanvasGroup>().ignoreParentGroups = true;

            // Two rows of action icons, then a row of HUD parts and one of shapes, as fractions of the height.
            const float iconRow = 0.31f, textRow = 0.19f;
            RectTransform Cell(string name, float x0, float x1, float y1, float h, Func<Control, bool> current, Action onTap)
            {
                var cell = NewRect(name, rolePicker);
                Anchor(cell, new Vector2(x0 + 0.005f, y1 - h + 0.012f), new Vector2(x1 - 0.005f, y1 - 0.012f));
                var img = cell.gameObject.AddComponent<Image>();
                img.sprite = panelSprite;
                img.type = Image.Type.Sliced;
                pickerCells.Add((img, current));
                panelButtons.Add((cell, () =>
                {
                    hintSet = false;
                    if (selected != null)
                        onTap();
                    RefreshPicker();
                    SaveLayout();
                    if (!hintSet)
                        UpdateRemapHint();
                }));
                return cell;
            }
            void Close() => rolePicker.gameObject.SetActive(false);

            int cols = 6, i = 0;
            foreach (var (id, name, _) in RoleDefs)
            {
                var role = roles[id];
                float x0 = (i % cols) / (float)cols;
                var cell = Cell(id, x0, x0 + 1f / cols, 1f - (i / cols) * iconRow, iconRow, c => !IsHud(c) && c.Role == role, () =>
                {
                    if (role.Id != "menu" && KeepMenu(selected))
                        return;
                    if (IsHud(selected))
                        ChangeKind(selected, role, 0);
                    else
                        SetRole(selected, role);
                    Close();
                });
                var icon = NewRect("icon", cell).gameObject.AddComponent<Image>();
                icon.sprite = role.Sprite;
                icon.preserveAspect = true;
                Anchor((RectTransform)icon.transform, new Vector2(0.1f, 0.3f), new Vector2(0.9f, 0.96f));
                var label = NewText(cell, name, 22);
                Anchor((RectTransform)label.transform, new Vector2(0f, 0f), new Vector2(1f, 0.3f));
                i++;
            }

            float y = 1f - 2f * iconRow;
            for (int k = 0; k < HudChoices.Length; k++)
            {
                var (name, blocks) = HudChoices[k];
                float x0 = k / (float)HudChoices.Length;
                var cell = Cell(name, x0, x0 + 1f / HudChoices.Length, y, textRow, c => IsHud(c) && c.Item.hud == blocks, () =>
                {
                    if (KeepMenu(selected))
                        return;
                    if (IsHud(selected))
                        SplitHud(selected, blocks);
                    else
                        ChangeKind(selected, null, blocks);
                    Close();
                });
                Anchor((RectTransform)NewText(cell, "HUD: " + name, 22).transform, new Vector2(0.03f, 0f), new Vector2(0.97f, 1f));
            }

            y -= textRow;
            for (int k = 0; k <= Shapes.Length; k++)
            {
                float x0 = k / (float)(Shapes.Length + 1), x1 = (k + 1) / (float)(Shapes.Length + 1);
                RectTransform cell;
                if (k == Shapes.Length)
                {
                    cell = Cell("delete", x0, x1, y, textRow, _ => false, () =>
                    {
                        if (KeepMenu(selected))
                            return;
                        RemoveControl(selected);
                        Close();
                    });
                    Anchor((RectTransform)NewText(cell, "DELETE", 24).transform, Vector2.zero, Vector2.one);
                    continue;
                }
                string shape = Shapes[k];
                // Shapes stay open, to compare them.
                cell = Cell(shape, x0, x1, y, textRow, c => !IsHud(c) && ShapeOf(c) == shape, () =>
                {
                    if (IsHud(selected))
                        return;
                    selected.Item.shape = shape;
                    Dress(selected);
                    Place(selected);
                });
                var icon = NewRect("icon", cell).gameObject.AddComponent<Image>();
                icon.sprite = frames[shape];
                icon.preserveAspect = true;
                Anchor((RectTransform)icon.transform, new Vector2(0.05f, 0.12f), new Vector2(0.4f, 0.88f));
                Anchor((RectTransform)NewText(cell, shape.ToUpperInvariant(), 22).transform, new Vector2(0.4f, 0f), new Vector2(0.97f, 1f));
                buttonOnlyCells.Add(cell.gameObject);
            }
            rolePicker.gameObject.SetActive(false);
        }

        void OpenRolePicker()
        {
            if (selected == null)
                return;
            rolePicker.gameObject.SetActive(true);
            RefreshPicker();
        }

        void RefreshPicker()
        {
            foreach (var cell in buttonOnlyCells)
                cell.SetActive(selected != null && !IsHud(selected));
            foreach (var (image, current) in pickerCells)
                image.color = selected != null && current(selected) ? new Color(1f, 0.82f, 0.4f, 0.4f) : new Color(1f, 1f, 1f, 0.08f);
        }

        // Full-screen panel: touch controls mode, movement style, performance, layout editing.
        void BuildSettingsPanel()
        {
            settingsPanel = NewRect("settings", transform);
            Anchor(settingsPanel, Vector2.zero, Vector2.one);
            // The shade takes UI raycasts, so taps on the panel never reach the game's menus underneath.
            var shade = settingsPanel.gameObject.AddComponent<Image>();
            shade.color = new Color(0f, 0f, 0f, 0.55f);

            var panel = NewRect("panel", settingsPanel);
            Anchor(panel, new Vector2(0.24f, 0.05f), new Vector2(0.76f, 0.95f));
            var bg = panel.gameObject.AddComponent<Image>();
            bg.sprite = panelSprite;
            bg.type = Image.Type.Sliced;
            bg.color = new Color(0f, 0f, 0f, 0.85f);
            var title = NewText(panel, "PORT SETTINGS", 40);
            title.font = titleFont;
            Anchor((RectTransform)title.transform, new Vector2(0.05f, 0.88f), new Vector2(0.95f, 0.98f));
            // Thin rule under the title, like the game's menu headers.
            var rule = NewRect("rule", panel).gameObject.AddComponent<Image>();
            rule.color = new Color(TextColor.r, TextColor.g, TextColor.b, 0.6f);
            Anchor(rule.rectTransform, new Vector2(0.3f, 0.872f), new Vector2(0.7f, 0.876f));

            var rows = new (Func<string> label, Action onTap)[]
            {
                (() => PortSettings.TouchLabel, PortSettings.CycleTouch),
                (() => PortSettings.MoveLabel, () => { PortSettings.CycleMove(); ApplyMoveStyle(); }),
                (() => PortSettings.ScreenLabel, PortSettings.ToggleFullWidth),
                (() => PerformanceSettings.ScaleLabel, PerformanceSettings.CycleScale),
                (() => PerformanceSettings.EffectsLabel, PerformanceSettings.ToggleEffects),
                (() => SaveExport.Label, SaveExport.Export),
                (() => SaveImport.Label, SaveImport.Import),
                (() => remapping ? "BACK TO LAYOUT EDITING" : "EDIT BUTTON LAYOUT", () => { settingsOpen = false; if (!remapping) StartRemap(); }),
                (() => "CLOSE", () => settingsOpen = false),
            };
            const float top = 0.86f;
            float step = 0.84f / rows.Length;
            for (int i = 0; i < rows.Length; i++)
            {
                var b = NewRect("setting " + i, panel);
                float y1 = top - i * step;
                Anchor(b, new Vector2(0.06f, y1 - step + 0.015f), new Vector2(0.94f, y1));
                var img = b.gameObject.AddComponent<Image>();
                img.sprite = panelSprite;
                img.type = Image.Type.Sliced;
                img.color = new Color(1f, 1f, 1f, i == rows.Length - 1 ? 0.16f : 0.08f);
                var text = NewText(b, rows[i].label(), 28);
                Anchor((RectTransform)text.transform, new Vector2(0.03f, 0f), new Vector2(0.97f, 1f));
                var row = rows[i];
                settingsLabels.Add((text, row.label));
                panelButtons.Add((b, () =>
                {
                    row.onTap();
                    RefreshSettingsLabels();
                }));
            }
            settingsPanel.gameObject.SetActive(false);
        }

        void RefreshSettingsLabels()
        {
            foreach (var (t, label) in settingsLabels)
                t.text = label();
        }

        void Resize(float factor)
        {
            var item = selectedStick ? stickItem : IsHud(selected) ? null : selected?.Item;
            if (item == null)
                return;
            item.size = Mathf.Clamp(item.size * factor, 0.06f, 0.6f);
            if (selectedStick) PlaceStick(); else Place(selected);
            SaveLayout();
        }

        void ResetLayout()
        {
            var defaults = DefaultLayout();
            foreach (var c in buttons)
                Destroy(c.Rect.gameObject);
            foreach (var c in huds)
                Destroy(c.Rect.gameObject);
            buttons.Clear();
            huds.Clear();
            layout.items = defaults.items;
            ApplyHudLayout();
            stickItem = layout.items.Find(i => i.id == "stick");
            foreach (var item in layout.items)
                if (item.id != "stick")
                    AddControl(item);
            KeepRemapUiOnTop();
            selected = null;
            selectedStick = false;
            Relayout();
            SaveLayout();
            UpdateRemapHint();
        }

        // ---- Layout ----

        void Relayout()
        {
            safeArea = Screen.safeArea;
            screenSize = new Vector2Int(Screen.width, Screen.height);
            PlaceStick();
            foreach (var c in buttons)
                Place(c);
            // Settings gear: top-left corner (MAP and MENU are top-right in game).
            float g = 0.1f * safeArea.height / canvas.scaleFactor;
            gear.anchorMin = gear.anchorMax = Vector2.zero;
            gear.sizeDelta = new Vector2(g, g);
            gear.anchoredPosition = ToCanvas(0.045f, 0.9f);
        }

        Vector2 ToCanvas(float x, float y) =>
            new Vector2(safeArea.x + x * safeArea.width, safeArea.y + y * safeArea.height) / canvas.scaleFactor;

        void PlaceStick()
        {
            float size = stickItem.size * safeArea.height / canvas.scaleFactor;
            ringCenter = ToCanvas(stickItem.x, stickItem.y);
            ring.anchorMin = ring.anchorMax = Vector2.zero;
            ring.anchoredPosition = ringCenter;
            knob.anchorMin = knob.anchorMax = new Vector2(0.5f, 0.5f);
            knob.sizeDelta = new Vector2(size, size) * JoystickBaseScale;
            // D-pad arms are half the pad long and as wide as the cross's arms.
            foreach (var arm in dpadArms)
                arm.rectTransform.sizeDelta = new Vector2(size * 0.48f, size * 0.33f);

            if (moveStyle == PortSettings.MoveStyle.Joystick)
            {
                ring.sizeDelta = new Vector2(size, size) * JoystickBaseScale;
                return;
            }
            if (moveStyle == PortSettings.MoveStyle.DPad)
            {
                ring.sizeDelta = new Vector2(size, size);
                return;
            }
            // Arrow keys: up above down, left and right beside down, like a keyboard. The control's size is its height.
            float k = size * 0.5f, gap = size * 0.04f, step = k + gap;
            ring.sizeDelta = new Vector2(3f * k + 2f * gap, 2f * k + gap);
            var positions = new[] { new Vector2(step, -step * 0.5f), new Vector2(0f, step * 0.5f), new Vector2(-step, -step * 0.5f), new Vector2(0f, -step * 0.5f) };
            for (int i = 0; i < 4; i++)
            {
                arrowKeys[i].rectTransform.sizeDelta = new Vector2(k, k);
                arrowKeys[i].rectTransform.anchoredPosition = positions[i];
            }
        }

        void Place(Control c)
        {
            float h = c.Item.size * safeArea.height / canvas.scaleFactor;
            var sprite = c.Image.sprite;
            float aspect = sprite != null ? sprite.rect.width / sprite.rect.height : 1f;
            c.Rect.anchorMin = c.Rect.anchorMax = Vector2.zero;
            c.Rect.sizeDelta = new Vector2(h * aspect, h);
            c.Rect.anchoredPosition = ToCanvas(c.Item.x, c.Item.y);
        }

        bool Contains(Control c, Vector2 screenPoint, float slack)
        {
            var half = c.Rect.sizeDelta * canvas.scaleFactor * 0.5f * slack;
            var d = screenPoint - c.Rect.anchoredPosition * canvas.scaleFactor;
            if (ShapeOf(c) != "round")
                return Mathf.Abs(d.x) <= half.x && Mathf.Abs(d.y) <= half.y;
            return d.sqrMagnitude <= half.x * half.x;
        }

        Layout LoadLayout()
        {
            try
            {
                var json = PlayerPrefs.GetString(PrefsKey, "");
                if (!string.IsNullOrEmpty(json))
                {
                    var saved = JsonUtility.FromJson<Layout>(json);
                    if (saved != null && saved.version == 3 && saved.items.Count > 0)
                    {
                        // v4 swapped the default Bind and Needolin spots; keep the rest of a saved layout.
                        var bind = saved.items.Find(i => i.id == "bind");
                        var needolin = saved.items.Find(i => i.id == "needolin");
                        if (bind != null && needolin != null)
                        {
                            (bind.x, needolin.x) = (needolin.x, bind.x);
                            (bind.y, needolin.y) = (needolin.y, bind.y);
                            (bind.size, needolin.size) = (needolin.size, bind.size);
                        }
                        saved.version = 4;
                    }
                    if (saved != null && saved.version == 4 && saved.items.Count > 0)
                    {
                        // v5 moved Bind next to Dash, Needolin into Bind's old spot and Menu into Needolin's.
                        // Only buttons still where v4 put them move, so custom placements are kept.
                        MoveIfAt(saved, "menu", 0.805f, 0.91f, 0.74f);
                        MoveIfAt(saved, "bind", 0.805f, 0.565f, 0.38f);
                        MoveIfAt(saved, "needolin", 0.805f, 0.74f, 0.565f);
                        saved.version = 5;
                    }
                    if (saved != null && saved.version == 5 && saved.items.Count > 0)
                    {
                        // v6 added Taunt in Menu's spot and moved Menu left of it.
                        if (!saved.items.Exists(i => i.id == "taunt"))
                        {
                            var menu = saved.items.Find(i => i.id == "menu");
                            if (menu != null && Mathf.Abs(menu.x - 0.805f) < 0.01f && Mathf.Abs(menu.y - 0.74f) < 0.01f)
                                menu.x = 0.715f;
                            saved.items.Add(DefaultLayout().items.Find(i => i.id == "taunt"));
                        }
                        saved.version = 6;
                    }
                    if (saved != null && saved.version == 6 && saved.items.Count > 0)
                    {
                        // v7 moved the joystick lower and further left, where the HK port has it (if not moved by hand).
                        var stick = saved.items.Find(i => i.id == "stick");
                        if (stick != null && Mathf.Abs(stick.x - 0.12f) < 0.01f && Mathf.Abs(stick.y - 0.26f) < 0.01f)
                        {
                            stick.x = 0.105f;
                            stick.y = 0.2f;
                        }
                        saved.version = 7;
                    }
                    if (saved != null && saved.version == 7 && saved.items.Count > 0)
                    {
                        // v8 made the HUD blocks layout items: v7's two offsets become one item, or two if apart.
                        var masks = new Vector2(saved.hudMasksX, saved.hudMasksY);
                        var spool = new Vector2(saved.hudSpoolX, saved.hudSpoolY);
                        if (masks == spool)
                            saved.items.Add(new Item { id = "hud", role = HudRole, hud = AllHud, x = masks.x, y = masks.y });
                        else
                        {
                            saved.items.Add(new Item { id = "hud", role = HudRole, hud = 1, x = masks.x, y = masks.y });
                            saved.items.Add(new Item { id = "hud2", role = HudRole, hud = AllHud & ~1, x = spool.x, y = spool.y });
                        }
                        saved.version = LayoutVersion;
                    }
                    if (saved != null && saved.version == LayoutVersion && saved.items.Count > 0)
                        return saved;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[IOSPort] Could not load touch layout: " + e.Message);
            }
            return DefaultLayout();
        }

        static void MoveIfAt(Layout l, string id, float x, float oldY, float newY)
        {
            var item = l.items.Find(i => i.id == id);
            if (item != null && Mathf.Abs(item.x - x) < 0.01f && Mathf.Abs(item.y - oldY) < 0.01f)
                item.y = newY;
        }

        void SaveLayout()
        {
            PlayerPrefs.SetString(PrefsKey, JsonUtility.ToJson(layout));
            PlayerPrefs.Save();
        }

        static RectTransform NewRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        static void Anchor(RectTransform rect, Vector2 min, Vector2 max)
        {
            rect.anchorMin = min;
            rect.anchorMax = max;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
        }

        Text NewText(Transform parent, string value, int maxSize)
        {
            var text = NewRect("label", parent).gameObject.AddComponent<Text>();
            text.font = font;
            text.text = value;
            text.alignment = TextAnchor.MiddleCenter;
            text.resizeTextForBestFit = true;
            text.resizeTextMinSize = 10;
            text.resizeTextMaxSize = maxSize;
            text.color = TextColor;
            return text;
        }

        // Shared by the procedural HUD art: white line-art with a faint fill, from a signed distance (in pixels,
        // negative inside).
        static float LineArt(float sdf, float stroke) =>
            Mathf.Max(Mathf.Clamp01(stroke - Mathf.Abs(sdf) + 0.5f), sdf < 0f ? 0.12f : 0f);

        static float BoxDistance(Vector2 p, Vector2 halfSize, float radius)
        {
            var q = new Vector2(Mathf.Abs(p.x), Mathf.Abs(p.y)) - halfSize + Vector2.one * radius;
            return new Vector2(Mathf.Max(q.x, 0f), Mathf.Max(q.y, 0f)).magnitude + Mathf.Min(Mathf.Max(q.x, q.y), 0f) - radius;
        }

        // Solid triangle pointing right (+x) with its tip at `tip` and base at `baseX`, in [-1, 1] units.
        static bool InTriangle(Vector2 q, float baseX, float tip, float halfHeight) =>
            q.x >= baseX && q.x <= tip && Mathf.Abs(q.y) <= halfHeight * (tip - q.x) / (tip - baseX);

        static Sprite FromPixels(int size, Func<Vector2, float> alpha) => FromPixels(size, size, alpha);

        static Sprite FromPixels(int width, int height, Func<Vector2, float> alpha)
        {
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha(new Vector2(x + 0.5f - width * 0.5f, y + 0.5f - height * 0.5f))));
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, width, height), new Vector2(0.5f, 0.5f), 100f);
        }

        // Button frames for the shapes, outlines like the game's mobile buttons.
        static Sprite MakeFrameSprite(string shape)
        {
            int h = 256, w = shape == "wide" ? Mathf.RoundToInt(h * WideAspect) : h;
            float stroke = h * 0.022f;
            var half = new Vector2(w, h) * 0.5f - Vector2.one * (stroke + 1f);
            float radius = shape == "round" ? half.y : shape == "wide" ? h * 0.15f : h * 0.17f;
            return FromPixels(w, h, p => Mathf.Clamp01(stroke - Mathf.Abs(BoxDistance(p, half, radius)) + 0.5f));
        }

        // Plus-shaped D-pad outline with an arrowhead in each arm.
        static Sprite MakeDPadSprite(int size)
        {
            float half = size * 0.5f, stroke = size * 0.012f;
            return FromPixels(size, p =>
            {
                float e = half - stroke - 1f, w = half * 0.33f;
                float sdf = Mathf.Min(BoxDistance(p, new Vector2(e, w), w * 0.3f), BoxDistance(p, new Vector2(w, e), w * 0.3f));
                float a = LineArt(sdf, stroke);
                var u = p / half;
                for (int i = 0; i < 4; i++)
                {
                    float ang = -90f * i * Mathf.Deg2Rad;
                    var q = new Vector2(u.x * Mathf.Cos(ang) - u.y * Mathf.Sin(ang), u.x * Mathf.Sin(ang) + u.y * Mathf.Cos(ang));
                    if (InTriangle(q, 0.58f, 0.84f, 0.15f))
                        a = 0.85f;
                }
                return a;
            });
        }

        // Keyboard keycap: rounded square outline with an arrowhead pointing right (rotated per key).
        static Sprite MakeKeySprite(int size)
        {
            float half = size * 0.5f, stroke = size * 0.022f;
            return FromPixels(size, p =>
            {
                float e = half - stroke - 1f;
                float a = LineArt(BoxDistance(p, new Vector2(e, e), half * 0.25f), stroke);
                return InTriangle(p / half, -0.22f, 0.4f, 0.34f) ? 0.9f : a;
            });
        }

        // Settings gear.
        static Sprite MakeGearSprite(int size)
        {
            float half = size * 0.5f;
            return FromPixels(size, p =>
            {
                float r = p.magnitude, ang = Mathf.Atan2(p.y, p.x);
                float tooth = Mathf.Clamp01((Mathf.Cos(ang * 8f) - 0.25f) * 4f);
                float outer = half * (0.72f + 0.2f * tooth);
                return Mathf.Clamp01(outer - r + 0.5f) * Mathf.Clamp01(r - half * 0.3f + 0.5f);
            });
        }

        // Rounded rectangle for panels, 9-sliced.
        static Sprite MakeRoundedRect(int size, float radius)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            float half = size * 0.5f;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float qx = Mathf.Max(Mathf.Abs(x + 0.5f - half) - (half - radius), 0f);
                float qy = Mathf.Max(Mathf.Abs(y + 0.5f - half) - (half - radius), 0f);
                float dist = Mathf.Sqrt(qx * qx + qy * qy) - radius;
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(0.5f - dist)));
            }
            tex.Apply();
            float b = radius + 2f;
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(b, b, b, b));
        }
    }
}
