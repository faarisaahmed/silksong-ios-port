using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace IOSPort
{
    // FaarisPorts loading screen. iOS shows a black launch screen while the app starts; from Unity's first
    // frame this fades the image in quickly, zooms it in slowly while the game loads, and fades it out once
    // the game is ready (the Team Cherry intro starting, the first-run language select, or the title menu).
    public class StartupSplash : MonoBehaviour
    {
        const float FadeInSeconds = 0.35f;
        const float FadeOutSeconds = 0.6f;
        const float MinSeconds = 1.5f;        // shown at least this long, so the fade-in never gets cut off
        const float ZoomPerSecond = 0.02f;    // slow push-in: about 10% bigger after 5 seconds
        const float TimeoutSeconds = 60f;

        Texture2D texture;
        CanvasGroup group;
        RawImage image;
        float shownAt = -1f;
        float fadeStart = -1f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Bootstrap()
        {
            if (!Application.isMobilePlatform)
                return;
            var texture = Resources.Load<Texture2D>("IOSPortSplash/Splash");
            if (texture == null)
                return;
            var go = new GameObject("IOSPort Startup Splash");
            DontDestroyOnLoad(go);
            var splash = go.AddComponent<StartupSplash>();
            splash.texture = texture;

            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = short.MaxValue;
            splash.group = go.AddComponent<CanvasGroup>();
            splash.group.blocksRaycasts = false;
            splash.group.interactable = false;

            var black = new GameObject("Background", typeof(RectTransform)).AddComponent<RawImage>();
            black.transform.SetParent(go.transform, false);
            black.color = Color.black;
            Stretch(black.rectTransform);

            var image = new GameObject("Image", typeof(RectTransform)).AddComponent<RawImage>();
            image.transform.SetParent(go.transform, false);
            image.texture = texture;
            image.color = new Color(1f, 1f, 1f, 0f);
            Stretch(image.rectTransform);
            // Raised 5% (the picture's bottom edge is black, like the background), and zoomed in around the label
            // rather than the centre, so "FaarisPorts" stays clear of the screen's rounded bottom edge.
            image.rectTransform.anchorMin = new Vector2(0f, 0.05f);
            image.rectTransform.anchorMax = new Vector2(1f, 1.05f);
            image.rectTransform.pivot = new Vector2(0.5f, 0.12f);
            splash.image = image;
            var fitter = image.gameObject.AddComponent<AspectRatioFitter>();
            fitter.aspectMode = AspectRatioFitter.AspectMode.EnvelopeParent;
            fitter.aspectRatio = (float)texture.width / texture.height;
        }

        static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
        }

        void Update()
        {
            if (shownAt < 0f)
                shownAt = Time.unscaledTime;
            float shown = Time.unscaledTime - shownAt;
            image.color = new Color(1f, 1f, 1f, Mathf.SmoothStep(0f, 1f, shown / FadeInSeconds));
            image.rectTransform.localScale = Vector3.one * (1f + ZoomPerSecond * shown);

            if (fadeStart < 0f)
            {
                if (shown >= MinSeconds && (GameReady() || Time.realtimeSinceStartup > TimeoutSeconds))
                {
                    fadeStart = Time.unscaledTime;
                    Debug.Log($"[IOSPort] loading screen fading out at {Time.realtimeSinceStartup:F1}s");
                }
                return;
            }
            float t = (Time.unscaledTime - fadeStart) / FadeOutSeconds;
            group.alpha = 1f - Mathf.SmoothStep(0f, 1f, t);
            if (t >= 1f)
            {
                Destroy(gameObject);
                Resources.UnloadAsset(texture);
            }
        }

        static bool GameReady()
        {
            var start = FindAnyObjectByType<StartManager>();
            if (start != null && start.startManagerAnimator != null && start.startManagerAnimator.GetBool("WillShowQuote"))
                return true;
            if (FindAnyObjectByType<LanguageSelector>() != null)
                return true;
            return SceneManager.GetActiveScene().name == "Menu_Title";
        }
    }
}
