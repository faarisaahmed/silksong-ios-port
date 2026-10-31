using System.Runtime.InteropServices;
using UnityEngine;

namespace IOSPort
{
    // Lets the game be heard with the iPhone's silent switch on (see Plugins/iOS/PlaybackAudio.mm).
    public class PlaybackAudio : MonoBehaviour
    {
        [DllImport("__Internal")]
        static extern void IOSPortUsePlaybackAudio();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (Application.platform != RuntimePlatform.IPhonePlayer)
                return;
            IOSPortUsePlaybackAudio();
            var go = new GameObject("IOSPort Playback Audio");
            DontDestroyOnLoad(go);
            go.AddComponent<PlaybackAudio>();
        }

        // Covers Unity resetting the session as it regains focus, in case it does so after the native observers ran.
        void OnApplicationFocus(bool focused)
        {
            if (focused)
                IOSPortUsePlaybackAudio();
        }
    }
}
