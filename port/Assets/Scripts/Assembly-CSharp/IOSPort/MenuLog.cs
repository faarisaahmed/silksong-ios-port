using System;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.EventSystems;

namespace IOSPort
{
    // Diagnostic for the pause menu sometimes ignoring every press until the app is restarted: while the game is
    // paused (and for a few seconds around it), writes the menu's input state to Documents/MenuLog.txt each time it
    // changes, with the touches and presses of that frame. Players send the file from the Files app.
    public class MenuLog : MonoBehaviour
    {
        const long MaxBytes = 512 * 1024;
        static MenuLog instance;
        static readonly FieldInfo IgnoreUnpause = typeof(UIManager).GetField("ignoreUnpause", BindingFlags.Instance | BindingFlags.NonPublic);

        string path, lastState;
        float activeUntil;
        bool wasPaused;
        readonly StringBuilder line = new StringBuilder();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (!Application.isMobilePlatform || instance != null)
                return;
            var go = new GameObject("IOSPort Menu Log");
            DontDestroyOnLoad(go);
            instance = go.AddComponent<MenuLog>();
        }

        void Awake()
        {
            path = Path.Combine(Application.persistentDataPath, "MenuLog.txt");
            Write("---- started " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + Application.version);
        }

        void LateUpdate()
        {
            var gm = GameManager.UnsafeInstance;
            var ui = UIManager.instance;
            var ih = ManagerSingleton<InputHandler>.UnsafeInstance;
            if (gm == null || ui == null || ih == null)
                return;
            bool paused = gm.isPaused;
            if (paused != wasPaused)
                activeUntil = Time.unscaledTime + 5f;
            wasPaused = paused;
            if (!paused && Time.unscaledTime > activeUntil)
                return;

            var es = EventSystem.current;
            var sel = es != null ? es.currentSelectedGameObject : null;
            string state = "game=" + gm.GameState + " ui=" + ui.uiState + "/" + ui.menuState
                + " accepting=" + ih.acceptingInput + " pauseAllowed=" + ih.PauseAllowed
                + " ignoreUnpause=" + (IgnoreUnpause != null && (bool)IgnoreUnpause.GetValue(ui))
                + " fading=" + ui.IsFadingMenu + " nav=" + (es != null && es.sendNavigationEvents)
                + " selected=" + (sel == null ? "none" : sel.name + (sel.activeInHierarchy ? "" : "(inactive)"))
                + " module=" + (es != null && es.currentInputModule != null ? es.currentInputModule.GetType().Name : "none")
                + " mouseInput=" + ui.inputModule.allowMouseInput + " cursor=" + Cursor.visible
                + " timeScale=" + Time.timeScale.ToString("0.##");

            line.Clear();
            var actions = ih.inputActions;
            if (actions.Pause.WasPressed) line.Append(" PAUSE-pressed");
            if (actions.MenuSubmit.WasPressed) line.Append(" SUBMIT-pressed");
            if (actions.MenuCancel.WasPressed) line.Append(" CANCEL-pressed");
            if (Input.GetMouseButtonDown(0)) line.Append(" mouse-down@" + Round(Input.mousePosition));
            if (Input.GetMouseButtonUp(0)) line.Append(" mouse-up@" + Round(Input.mousePosition));
            for (int i = 0; i < Input.touchCount; i++)
            {
                var t = Input.GetTouch(i);
                if (t.phase == TouchPhase.Began || t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled)
                    line.Append(" touch" + t.fingerId + "-" + t.phase + "@" + Round(t.position));
            }
            if (state == lastState && line.Length == 0)
                return;
            string touches = " touches=" + Input.touchCount + (Input.GetMouseButton(0) ? " mouseHeld" : "");
            Write(Time.unscaledTime.ToString("0.00") + (state == lastState ? "" : " " + state) + touches + line);
            lastState = state;
        }

        static string Round(Vector3 p) => (int)p.x + "," + (int)p.y;

        void Write(string text)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxBytes)
                    File.Copy(path, Path.Combine(Application.persistentDataPath, "MenuLog-old.txt"), true);
                if (info.Exists && info.Length > MaxBytes)
                    File.Delete(path);
                File.AppendAllText(path, text + "\n");
            }
            catch (Exception)
            {
                // A log must never break the game.
            }
        }
    }
}
