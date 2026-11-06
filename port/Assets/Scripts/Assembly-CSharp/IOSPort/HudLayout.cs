using System.Collections.Generic;
using UnityEngine;

namespace IOSPort
{
    // Moves (or hides) the game's HUD in three blocks, placed in the touch controls' remap mode (see TouchControls):
    //  - Masks: the health masks and the bind orb at their left (the game keeps the orb under the spool).
    //  - Spool: the silk spool, tool icons and the extra icons beside them.
    //  - Counters: the rosary and shard counters below the spool.
    // Offsets are in fractions of the screen height. Several HUD scripts place one piece relative to another
    // (the tool icons follow the end of the spool, the counters sit at a fixed height), so the game has to see its
    // own positions: HudLayoutRestore takes the offsets off at the very start of the frame and LateUpdate puts them
    // back at the very end, just before rendering.
    [DefaultExecutionOrder(32000)]
    public class HudLayout : MonoBehaviour
    {
        public enum Block { Masks, Spool, Counters }
        public const int BlockCount = 3;

        public static readonly Vector2[] Offsets = new Vector2[BlockCount];
        public static readonly bool[] Shown = { true, true, true };
        // Hidden blocks are moved this far away (screen heights).
        const float HiddenOffset = 50f;

        static HudLayout instance;

        class Piece
        {
            public Transform T, Parent;
            public Block Block;
            public bool UnderSpool; // inside the spool block's pieces, so it only needs the difference
            public Vector3 LastSet, Applied;
        }

        readonly List<Piece> pieces = new List<Piece>();
        Transform canvas;

        static readonly string[] SpoolPieces = { "Thread", "Tool Icons", "Extras", "Delivery Icon" };

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (!Application.isMobilePlatform || instance != null)
                return;
            var go = new GameObject("IOSPort HUD Layout");
            DontDestroyOnLoad(go);
            instance = go.AddComponent<HudLayout>();
            go.AddComponent<HudLayoutRestore>();
        }

        static Transform HudCanvas()
        {
            var gc = GameCameras.SilentInstance;
            return gc != null && gc.hudCanvasSlideOut != null ? gc.hudCanvasSlideOut.transform : null;
        }

        void Collect(Transform hud)
        {
            pieces.Clear();
            canvas = hud;
            void Add(Transform t, Block b, bool underSpool)
            {
                if (t != null)
                    pieces.Add(new Piece { T = t, Parent = t.parent, Block = b, UnderSpool = underSpool });
            }
            Add(hud.Find("Health"), Block.Masks, false);
            Add(hud.Find("Crest Get Effects"), Block.Masks, false); // the crest change flash around the orb
            Add(hud.Find("Thread/Spool/Bind Orb"), Block.Masks, true);
            foreach (var n in SpoolPieces)
                Add(hud.Find(n), Block.Spool, false);
            Add(hud.Find("Counters"), Block.Counters, false);
        }

        static Vector2 Offset(Block b) => Shown[(int)b] ? Offsets[(int)b] : new Vector2(HiddenOffset, 0f);

        // Start of the frame (HudLayoutRestore): back to where the game put everything.
        internal static void Restore()
        {
            if (instance == null)
                return;
            foreach (var p in instance.pieces)
            {
                // Anything the game moved since LateUpdate is a new position of its own.
                if (p.T != null && p.Applied != Vector3.zero && p.T.localPosition == p.LastSet)
                    p.T.localPosition -= p.Applied;
                p.Applied = Vector3.zero;
            }
        }

        void LateUpdate()
        {
            var hud = HudCanvas();
            if (hud == null)
                return;
            if (hud != canvas)
                Collect(hud);
            var cam = GameCameras.SilentInstance.hudCamera;
            float unitsPerHeight = cam != null && cam.orthographic ? 2f * cam.orthographicSize : 0f;
            foreach (var p in pieces)
            {
                // The counters leave the HUD for a fixed spot of their own while it's hidden (shops); leave them there.
                if (p.T == null || p.T.parent != p.Parent)
                    continue;
                var offset = Offset(p.Block);
                if (p.UnderSpool)
                    offset -= Offset(Block.Spool); // its parent already moves with the spool block
                if (offset == Vector2.zero)
                    continue;
                var world = new Vector3(offset.x, offset.y, 0f) * unitsPerHeight;
                var local = p.Parent != null ? p.Parent.InverseTransformVector(world) : world;
                p.T.localPosition += local;
                p.LastSet = p.T.localPosition;
                p.Applied = local;
            }
        }

        // Where the block is on screen without any offset (pixels, origin bottom left), from its visible sprites.
        // Only valid between HudLayoutRestore and LateUpdate (TouchControls.Update). False when the HUD isn't up.
        public static bool BaseRect(Block block, out Rect rect)
        {
            rect = default;
            if (instance == null || instance.canvas == null || GameCameras.SilentInstance == null)
                return false;
            var cam = GameCameras.SilentInstance.hudCamera;
            if (cam == null || !cam.isActiveAndEnabled)
                return false;
            // Hidden parts (the counters fade out between pickups) only count when nothing in the block is showing.
            return Bounds(block, cam, true, out rect) || Bounds(block, cam, false, out rect);
        }

        static bool Bounds(Block block, Camera cam, bool visibleOnly, out Rect rect)
        {
            rect = default;
            bool any = false;
            Vector2 min = Vector2.positiveInfinity, max = Vector2.negativeInfinity;
            var orb = instance.canvas.Find("Thread/Spool/Bind Orb");
            foreach (var p in instance.pieces)
            {
                if (p.T == null || p.Block != block || p.T.parent != p.Parent || !p.T.gameObject.activeInHierarchy || p.T.name == "Crest Get Effects")
                    continue;
                foreach (var r in p.T.GetComponentsInChildren<Renderer>())
                {
                    if (!r.enabled || r is ParticleSystemRenderer || visibleOnly && !Showing(r))
                        continue;
                    // The orb belongs to the masks block even though it's inside the spool's pieces.
                    if (block == Block.Spool && orb != null && r.transform.IsChildOf(orb))
                        continue;
                    var b = r.bounds;
                    Vector2 lo = cam.WorldToScreenPoint(b.min), hi = cam.WorldToScreenPoint(b.max);
                    // Leave out anything off screen or screen-sized (fades and flashes).
                    if (hi.x < 0 || hi.y < 0 || lo.x > Screen.width || lo.y > Screen.height
                        || hi.x - lo.x > Screen.width * 0.4f || hi.y - lo.y > Screen.height * 0.4f)
                        continue;
                    min = Vector2.Min(min, Vector2.Min(lo, hi));
                    max = Vector2.Max(max, Vector2.Max(lo, hi));
                    any = true;
                }
            }
            if (any)
                rect = Rect.MinMaxRect(min.x, min.y, max.x, max.y);
            return any;
        }

        // Whether the renderer is actually drawing something: the HUD hides most of its parts by fading them out.
        static bool Showing(Renderer r)
        {
            if (r is SpriteRenderer sr)
                return sr.sprite != null && sr.color.a > 0.02f;
            var text = r.GetComponent<TMPro.TMP_Text>();
            if (text != null)
                return !string.IsNullOrWhiteSpace(text.text) && text.color.a > 0.02f && text.alpha > 0.02f;
            var tk2d = r.GetComponent<tk2dBaseSprite>();
            if (tk2d != null)
                return tk2d.color.a > 0.02f;
            return r.sharedMaterial == null || !r.sharedMaterial.HasProperty("_Color") || r.sharedMaterial.color.a > 0.02f;
        }
    }
}
