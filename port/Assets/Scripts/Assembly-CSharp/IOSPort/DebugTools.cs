using GlobalEnums;
using UnityEngine;

namespace IOSPort
{
    // Testing aid for debug builds (Simulator builds and ./build --debug): buttons at the top of the screen to fly
    // through walls (NOCLIP: move with the stick, no damage, turns itself off at room transitions) and to jump
    // to a room for checking a reported bug.
    public class DebugTools : MonoBehaviour
    {
        static DebugTools instance;
        bool noclip;
        Collider2D[] madeTriggers = new Collider2D[0];

        const float Speed = 18f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (!Debug.isDebugBuild)
                return;
            var go = new GameObject("IOSPort DebugTools");
            DontDestroyOnLoad(go);
            instance = go.AddComponent<DebugTools>();
        }

        public static void ToggleNoclip()
        {
            if (instance != null)
                instance.SetNoclip(!instance.noclip);
        }

        // Whispering Vaults, at the lever door next to the Grand Bellway exit.
        public static void WarpToVaultsDoor() => WarpTo("Library_11", "left2");

        public static void WarpTo(string scene, string gate)
        {
            var gm = GameManager.UnsafeInstance;
            if (gm == null || HeroController.UnsafeInstance == null)
                return;
            instance?.SetNoclip(false);
            gm.BeginSceneTransition(new GameManager.SceneLoadInfo
            {
                SceneName = scene,
                EntryGateName = gate,
                Visualization = GameManager.SceneLoadVisualizations.Default,
            });
        }

        // Logs every gate in the loaded rooms: whether it's been opened, its animation, and its solid colliders.
        public static void LogGates()
        {
            var activated = typeof(Gate).GetField("activated", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            foreach (var gate in FindObjectsByType<Gate>(FindObjectsSortMode.None))
            {
                var anim = gate.GetComponent<Animator>();
                string state = "no animator";
                if (anim != null)
                {
                    var info = anim.GetCurrentAnimatorStateInfo(0);
                    state = (anim.enabled ? "" : "(disabled) ") + (info.IsName("Open") ? "Open" : info.IsName("Close") ? "Close" : info.shortNameHash.ToString()) + " t=" + info.normalizedTime.ToString("0.00");
                }
                var solid = System.Array.FindAll(gate.GetComponentsInChildren<Collider2D>(), c => c.enabled && !c.isTrigger);
                Debug.Log($"[IOSPort] gate {gate.name} at {gate.transform.position}: activated={activated?.GetValue(gate)} anim={state} solid=[{string.Join(", ", System.Array.ConvertAll(solid, c => c.name + "@" + c.bounds.center.ToString("0.0")))}]");
            }
        }

        // Dumps a gate's hierarchy, then rebinds its animator and jumps to the end of Open, and dumps it again.
        public static void ProbeGate(string name)
        {
            foreach (var gate in FindObjectsByType<Gate>(FindObjectsSortMode.None))
            {
                if (gate.name != name)
                    continue;
                void Dump(Transform t, string indent, int depth)
                {
                    string shown = "";
                    foreach (char ch in t.name)
                        shown += ch >= 32 && ch < 127 ? ch.ToString() : $"\\u{(int)ch:x4}";
                    Debug.Log($"[IOSPort] probe {indent}'{shown}' active={t.gameObject.activeSelf} local={t.localPosition.ToString("0.00")} comps={string.Join(",", System.Array.ConvertAll(t.GetComponents<Component>(), c => c.GetType().Name))}");
                    if (depth < 3)
                        foreach (Transform c in t)
                            Dump(c, indent + "  ", depth + 1);
                }
                Dump(gate.transform, "", 0);
                var anim = gate.GetComponent<Animator>();
                anim.Rebind();
                anim.Play("Open", 0, 1f);
                anim.Update(0f);
                Debug.Log("[IOSPort] probe after Rebind + Open at end:");
                Dump(gate.transform, "", 0);
            }
        }

        void SetNoclip(bool on)
        {
            var hero = HeroController.UnsafeInstance;
            if (hero == null || on == noclip)
                return;
            noclip = on;
            if (on)
            {
                hero.RelinquishControl();
                hero.AffectedByGravity(false);
                hero.SetDamageMode(DamageMode.NO_DAMAGE);
                madeTriggers = System.Array.FindAll(hero.GetComponents<Collider2D>(), c => !c.isTrigger);
                foreach (var c in madeTriggers)
                    c.isTrigger = true;
            }
            else
            {
                foreach (var c in madeTriggers)
                    if (c != null)
                        c.isTrigger = false;
                madeTriggers = new Collider2D[0];
                hero.SetDamageMode(DamageMode.FULL_DAMAGE);
                hero.AffectedByGravity(true);
                hero.RegainControl();
            }
            Debug.Log("[IOSPort] noclip " + (on ? "on" : "off"));
        }

        void Update()
        {
            if (!noclip)
                return;
            var hero = HeroController.UnsafeInstance;
            if (hero == null || hero.cState.transitioning || hero.cState.dead)
            {
                noclip = false;
                madeTriggers = new Collider2D[0];
                return;
            }
            var input = ManagerSingleton<InputHandler>.UnsafeInstance;
            Vector2 move = input != null ? input.inputActions.MoveVector.Vector : Vector2.zero;
            hero.Body.linearVelocity = Vector2.zero;
            hero.transform.position += (Vector3)(move * Speed * Time.deltaTime);
        }

        void OnGUI()
        {
            if (GameManager.UnsafeInstance == null || GameManager.UnsafeInstance.GameState != GameState.PLAYING)
                return;
            float s = Screen.height / 400f;
            GUI.matrix = Matrix4x4.Scale(new Vector3(s, s, 1f));
            float w = Screen.width / s;
            var style = new GUIStyle(GUI.skin.button) { fontSize = 12 };
            if (GUI.Button(new Rect(w / 2 - 125, 4, 120, 26), noclip ? "NOCLIP: ON" : "NOCLIP: OFF", style))
                ToggleNoclip();
            if (GUI.Button(new Rect(w / 2 + 5, 4, 120, 26), "GO TO VAULTS DOOR", style))
                WarpToVaultsDoor();
            var hero = HeroController.UnsafeInstance;
            if (hero != null)
            {
                var p = hero.transform.position;
                GUI.Label(new Rect(w / 2 - 125, 32, 250, 20), $"{GameManager.UnsafeInstance.sceneName}  {p.x:0.0}, {p.y:0.0}", style);
            }
        }
    }
}
