using UnityEngine;
using UnityEngine.UI;

namespace IOSPort
{
    // The game keeps the camera inside each room's lock areas assuming a 16:9 view (CameraController's 14.6 units
    // either side), so on a wider phone screen the extra width shows past them: the hidden parts of the room that
    // the console versions never show. This fades the screen to black beyond the edge a 16:9 view would stop at,
    // and only outside the 16:9 part in the middle, so nothing that's visible on 16:9 is ever covered.
    public class OutOfBoundsFade : MonoBehaviour
    {
        const float HalfView = 14.6f;  // CameraController's half width of the view
        const float Feather = 1.2f;    // world units of fade before solid black

        RawImage leftSolid, leftFade, rightSolid, rightFade;
        Canvas canvas;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (!Application.isMobilePlatform)
                return;
            var go = new GameObject("IOSPort OutOfBoundsFade");
            DontDestroyOnLoad(go);
            go.AddComponent<OutOfBoundsFade>();
        }

        void Awake()
        {
            canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            var layers = SortingLayer.layers;
            canvas.sortingLayerID = layers[layers.Length - 1].id;
            canvas.sortingOrder = 32767;
            var fade = new Texture2D(64, 1, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            for (int i = 0; i < 64; i++)
            {
                float t = i / 63f;
                fade.SetPixel(i, 0, new Color(0, 0, 0, t * t * (3 - 2 * t)));
            }
            fade.Apply();
            leftSolid = Part(null, false);
            leftFade = Part(fade, true);
            rightSolid = Part(null, false);
            rightFade = Part(fade, false);
            canvas.enabled = false;
        }

        RawImage Part(Texture2D tex, bool flip)
        {
            var go = new GameObject("part", typeof(RectTransform));
            go.transform.SetParent(transform, false);
            var image = go.AddComponent<RawImage>();
            image.raycastTarget = false;
            image.color = tex == null ? Color.black : Color.white;
            if (tex != null)
            {
                image.texture = tex;
                image.uvRect = flip ? new Rect(1, 0, -1, 1) : new Rect(0, 0, 1, 1);
            }
            var rt = (RectTransform)go.transform;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
            return image;
        }

        static void Place(RawImage image, float from, float to)
        {
            var rt = image.rectTransform;
            rt.anchorMin = new Vector2(Mathf.Clamp01(from), 0);
            rt.anchorMax = new Vector2(Mathf.Clamp01(to), 1);
            image.enabled = to > from && to > 0 && from < 1;
        }

        void LateUpdate()
        {
            var gm = GameManager.UnsafeInstance;
            var gc = GameCameras.SilentInstance;
            var cc = gc != null ? gc.cameraController : null;
            bool show = gm != null && cc != null && cc.cam != null && gm.IsGameplayScene() &&
                        ForceCameraAspect.CurrentViewportAspect > 1.78f && cc.mode != CameraController.CameraMode.FROZEN;
            if (!show)
            {
                canvas.enabled = false;
                return;
            }
            var cam = cc.cam;
            if (canvas.worldCamera != cam)
            {
                canvas.worldCamera = cam;
                canvas.planeDistance = cam.nearClipPlane + 0.05f;
            }
            float camX = cam.transform.position.x, camY = cam.transform.position.y;
            // How far the camera is allowed to go, plus half a 16:9 view: the edge of what's meant to be seen.
            float minX = cc.CurrentLockArea != null ? Mathf.Max(cc.xLockMin, HalfView) : HalfView;
            float maxX = cc.CurrentLockArea != null && cc.xLockMax > 0 ? Mathf.Min(cc.xLockMax, cc.xLimit) : cc.xLimit;
            float left = Mathf.Min(minX - HalfView, camX - HalfView);
            float right = Mathf.Max(maxX + HalfView, camX + HalfView);
            float V(float x) => cam.WorldToViewportPoint(new Vector3(x, camY, 0)).x;
            Place(leftSolid, 0, V(left - Feather));
            Place(leftFade, V(left - Feather), V(left));
            Place(rightFade, V(right), V(right + Feather));
            Place(rightSolid, V(right + Feather), 1);
            canvas.enabled = leftFade.enabled || rightFade.enabled || leftSolid.enabled || rightSolid.enabled;
        }
    }
}
