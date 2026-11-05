using System.Collections.Generic;
using UnityEngine;

namespace IOSPort
{
    // Moves the game's HUD in two blocks, placed in the touch controls' remap mode (see TouchControls):
    //  - Masks: the health masks and the bind orb at their left (the game keeps the orb under the spool).
    //  - Spool: everything below them (silk spool, tool icons, shard and rosary counters, the extra icons), which
    //    keeps the game's own layout and just moves as one.
    // Offsets are in fractions of the screen height. Each piece gets the offset added on top of wherever the game
    // puts it, every frame, so the HUD's own movement (sliding in and out, counters popping up) still plays.
    [DefaultExecutionOrder(31500)]
    public class HudLayout : MonoBehaviour
    {
        public enum Block { Masks, Spool }

        public static Vector2 MasksOffset, SpoolOffset;

        static HudLayout instance;

        // A HUD piece and what was last done to it, so a position the game sets itself is kept (and offset).
        class Piece
        {
            public Transform T;
            public Block Block;
            public bool UnderSpool; // inside the spool block's pieces, so it only needs the difference
            public Vector3 LastSet, Applied;
            public bool Moved;
        }

        readonly List<Piece> pieces = new List<Piece>();
        Transform canvas;

        static readonly string[] SpoolPieces = { "Thread", "Tool Icons", "Counters", "Extras", "Delivery Icon" };

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (!Application.isMobilePlatform || instance != null)
                return;
            var go = new GameObject("IOSPort HUD Layout");
            DontDestroyOnLoad(go);
            instance = go.AddComponent<HudLayout>();
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
                    pieces.Add(new Piece { T = t, Block = b, UnderSpool = underSpool });
            }
            Add(hud.Find("Health"), Block.Masks, false);
            Add(hud.Find("Crest Get Effects"), Block.Masks, false); // the crest change flash around the orb
            Add(hud.Find("Thread/Spool/Bind Orb"), Block.Masks, true);
            foreach (var n in SpoolPieces)
                Add(hud.Find(n), Block.Spool, false);
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
                if (p.T == null)
                    continue;
                var offset = p.Block == Block.Masks ? MasksOffset : SpoolOffset;
                if (p.UnderSpool)
                    offset -= SpoolOffset; // its parent already moves with the spool block
                var world = new Vector3(offset.x, offset.y, 0f) * unitsPerHeight;
                var local = p.T.parent != null ? p.T.parent.InverseTransformVector(world) : world;
                var current = p.T.localPosition;
                // Something else moved it since last frame: that's the new base.
                var basePos = p.Moved && current == p.LastSet ? current - p.Applied : current;
                if (!p.Moved && local == Vector3.zero)
                    continue;
                p.T.localPosition = basePos + local;
                p.LastSet = p.T.localPosition;
                p.Applied = local;
                p.Moved = true;
            }
        }

        // The block's on-screen box (pixels, origin bottom left), from its visible sprites. False when the HUD
        // isn't up or nothing in the block is showing.
        public static bool ScreenRect(Block block, out Rect rect)
        {
            rect = default;
            if (instance == null || instance.canvas == null || GameCameras.SilentInstance == null)
                return false;
            var cam = GameCameras.SilentInstance.hudCamera;
            if (cam == null || !cam.isActiveAndEnabled)
                return false;
            bool any = false;
            Vector2 min = Vector2.positiveInfinity, max = Vector2.negativeInfinity;
            var orb = instance.canvas.Find("Thread/Spool/Bind Orb");
            foreach (var p in instance.pieces)
            {
                if (p.T == null || p.Block != block || !p.T.gameObject.activeInHierarchy || p.T.name == "Crest Get Effects")
                    continue;
                foreach (var r in p.T.GetComponentsInChildren<Renderer>())
                {
                    if (!r.enabled || r is ParticleSystemRenderer || !Showing(r))
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
            if (!any)
                return false;
            rect = Rect.MinMaxRect(min.x, min.y, max.x, max.y);
            return true;
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
