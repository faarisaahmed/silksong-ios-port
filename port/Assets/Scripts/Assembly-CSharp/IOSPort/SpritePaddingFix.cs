using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace IOSPort
{
    // Many of the game's sprites came out of the asset extraction cropped to their visible pixels, losing the
    // transparent padding of the original sprite rect (e.g. the menu fleurs: 99x86 originally, 42x48 extracted).
    // World sprites are unaffected, but UI Images stretch the sprite rect over their box, so cropped sprites drew
    // too big (menu fleurs, Mode Select icons). Resources/IOSPortData/sprite_padding.txt lists the original rects,
    // read from the PC game's bundles; this draws each affected UI Image inside its original padded area.
    [RequireComponent(typeof(Image))]
    public class SpritePaddingFix : BaseMeshEffect
    {
        const float ScanInterval = 0.5f;

        static Dictionary<string, Vector4> table;   // name|w|h -> original rect width, height, tight offset x, y
        static readonly Dictionary<Sprite, Vector4> cache = new Dictionary<Sprite, Vector4>();
        static readonly HashSet<int> seen = new HashSet<int>();

        Image image;

        class Scanner : MonoBehaviour
        {
            float next;

            // Scenes are scanned as soon as they load (before their first frame is drawn), hidden UI included,
            // so menus never show a frame at the wrong size when they appear.
            void Awake() => UnityEngine.SceneManagement.SceneManager.sceneLoaded += (scene, mode) => Scan();

            void Update()
            {
                if (Time.unscaledTime < next)
                    return;
                // New UI (menus, inventory panes, pooled prefabs) appears all the time, so keep looking.
                Scan();
            }

            void Scan()
            {
                next = Time.unscaledTime + ScanInterval;
                foreach (var image in FindObjectsByType<Image>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    if (!seen.Add(image.GetInstanceID()) || image.type != Image.Type.Simple)
                        continue;
                    image.gameObject.AddComponent<SpritePaddingFix>().image = image;
                }
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Bootstrap()
        {
            if (!Application.isMobilePlatform)
                return;
            var data = Resources.Load<TextAsset>("IOSPortData/sprite_padding");
            if (data == null)
                return;
            table = new Dictionary<string, Vector4>();
            foreach (var line in data.text.Split('\n'))
            {
                if (line.Length == 0 || line[0] == '#')
                    continue;
                var parts = line.Split('\t');
                var v = parts[1].Split('|');
                table[parts[0]] = new Vector4(float.Parse(v[0], System.Globalization.CultureInfo.InvariantCulture),
                    float.Parse(v[1], System.Globalization.CultureInfo.InvariantCulture),
                    float.Parse(v[2], System.Globalization.CultureInfo.InvariantCulture),
                    float.Parse(v[3], System.Globalization.CultureInfo.InvariantCulture));
            }
            Resources.UnloadAsset(data);
            var go = new GameObject("IOSPort Sprite Padding Fix");
            DontDestroyOnLoad(go);
            go.AddComponent<Scanner>();
        }

        protected override void Awake()
        {
            base.Awake();
            if (image == null)
                image = GetComponent<Image>();
            if (graphic != null)
                graphic.SetVerticesDirty();
        }

        static bool TryGetPadding(Sprite sprite, out Vector4 padding)
        {
            if (cache.TryGetValue(sprite, out padding))
                return padding.x > 0f;
            var size = sprite.rect.size;
            if (!table.TryGetValue(sprite.name + "|" + Mathf.RoundToInt(size.x) + "|" + Mathf.RoundToInt(size.y), out padding))
                padding = Vector4.zero;
            cache[sprite] = padding;
            return padding.x > 0f;
        }

        public override void ModifyMesh(VertexHelper vh)
        {
            if (!IsActive() || image == null || image.type != Image.Type.Simple || vh.currentVertCount == 0)
                return;
            var sprite = image.overrideSprite;
            if (sprite == null || !TryGetPadding(sprite, out var p))
                return;

            // The area the original (padded) sprite would have filled, including Preserve Aspect.
            var rt = image.rectTransform;
            var area = rt.rect;
            if (image.preserveAspect)
            {
                float spriteRatio = p.x / p.y, rectRatio = area.width / area.height;
                if (spriteRatio > rectRatio)
                {
                    float h = area.width / spriteRatio;
                    area.y += (area.height - h) * rt.pivot.y;
                    area.height = h;
                }
                else
                {
                    float w = area.height * spriteRatio;
                    area.x += (area.width - w) * rt.pivot.x;
                    area.width = w;
                }
            }
            var size = sprite.rect.size;
            var target = new Rect(area.x + p.z / p.x * area.width, area.y + p.w / p.y * area.height,
                size.x / p.x * area.width, size.y / p.y * area.height);

            // Move the quad (or sprite mesh) from where Image put it into that padded position.
            var vert = new UIVertex();
            Vector2 min = Vector2.positiveInfinity, max = Vector2.negativeInfinity;
            for (int i = 0; i < vh.currentVertCount; i++)
            {
                vh.PopulateUIVertex(ref vert, i);
                min = Vector2.Min(min, vert.position);
                max = Vector2.Max(max, vert.position);
            }
            var span = max - min;
            if (span.x <= 0f || span.y <= 0f)
                return;
            for (int i = 0; i < vh.currentVertCount; i++)
            {
                vh.PopulateUIVertex(ref vert, i);
                var t = ((Vector2)vert.position - min) / span;
                vert.position = new Vector3(target.x + t.x * target.width, target.y + t.y * target.height, vert.position.z);
                vh.SetUIVertex(vert, i);
            }
        }
    }
}
