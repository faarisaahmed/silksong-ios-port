using System.Collections;
using System.IO;
using GlobalEnums;
using UnityEngine;

namespace IOSPort
{
    // Testing aid: if Documents/iosport-autoload.txt holds a save slot number (src/scripts/sim.sh writes it), that
    // slot is loaded once the title menu is up. The file is deleted when read, so only the next launch is affected.
    // iOS doesn't pass launch arguments through to the game, hence the file.
    public class AutoLoad : MonoBehaviour
    {
        int slot;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (!Debug.isDebugBuild)
                return; // test aid only (Simulator builds are debug builds)
            string path = Path.Combine(Application.persistentDataPath, "iosport-autoload.txt");
            if (!File.Exists(path))
                return;
            bool valid = int.TryParse(File.ReadAllText(path).Trim(), out int slot);
            File.Delete(path);
            if (!valid)
                return;
            var go = new GameObject("IOSPort AutoLoad");
            DontDestroyOnLoad(go);
            go.AddComponent<AutoLoad>().slot = slot;
        }

        IEnumerator Start()
        {
            while (GameManager.UnsafeInstance == null || UIManager.instance == null || UIManager.instance.menuState != MainMenuState.MAIN_MENU)
                yield return null;
            yield return new WaitForSeconds(2f);
            Debug.Log("[IOSPort] auto-loading save slot " + slot);
            GameManager.instance.LoadGameFromUI(slot);
            Destroy(gameObject);
        }
    }
}
