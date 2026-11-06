using UnityEngine;

namespace IOSPort
{
    // First thing each frame: takes HudLayout's offsets back off, so the game's HUD scripts see their own positions.
    [DefaultExecutionOrder(-32000)]
    public class HudLayoutRestore : MonoBehaviour
    {
        void Update() => HudLayout.Restore();
    }
}
