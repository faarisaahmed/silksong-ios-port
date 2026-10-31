using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace IOSPort
{
    // Extra safety for save files on iOS, on top of the game's own .bak files and restore points.
    // Copies the saves (user*.dat, shared.dat) into Documents/SaveBackups/<date_time>/ at launch, every few
    // minutes and whenever the app goes to the background, but only when something changed. Keeps the newest
    // MaxBackups snapshots. They show up in the Files app; to restore, copy a snapshot's files back into the
    // Silksong folder while the game is closed.
    public class SaveBackup : MonoBehaviour
    {
        const int MaxBackups = 40;
        const float IntervalSeconds = 300f;
        const string FolderName = "SaveBackups";

        static SaveBackup instance;
        float nextCheck;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Bootstrap()
        {
            if (Application.platform != RuntimePlatform.IPhonePlayer || instance != null)
                return;
            var go = new GameObject("IOSPort Save Backup");
            DontDestroyOnLoad(go);
            instance = go.AddComponent<SaveBackup>();
            Snapshot("launch");
        }

        void Update()
        {
            if (Time.unscaledTime < nextCheck)
                return;
            nextCheck = Time.unscaledTime + IntervalSeconds;
            Snapshot("timer");
        }

        void OnApplicationPause(bool paused)
        {
            if (paused)
                Snapshot("background");
        }

        void OnApplicationQuit() => Snapshot("quit");

        static List<string> SaveFiles(string dir) =>
            Directory.GetFiles(dir, "user*.dat").Concat(Directory.GetFiles(dir, "shared.dat"))
                .Where(f => Path.GetFileName(f).EndsWith(".dat", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();

        public static void Snapshot(string reason)
        {
            try
            {
                string root = Application.persistentDataPath;
                string backups = Path.Combine(root, FolderName);
                // Skip files written in the last couple of seconds; the game may still be writing them.
                var files = SaveFiles(root).Where(f => (DateTime.UtcNow - File.GetLastWriteTimeUtc(f)).TotalSeconds > 2).ToList();
                if (files.Count == 0)
                    return;

                var latest = Directory.Exists(backups)
                    ? Directory.GetDirectories(backups).OrderBy(d => d, StringComparer.Ordinal).LastOrDefault()
                    : null;
                if (latest != null && files.All(f => SameContents(f, Path.Combine(latest, Path.GetFileName(f)))))
                    return;

                string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                string temp = Path.Combine(backups, "." + stamp + ".partial");
                Directory.CreateDirectory(temp);
                foreach (var f in files)
                    File.Copy(f, Path.Combine(temp, Path.GetFileName(f)), true);
                string target = Path.Combine(backups, stamp);
                if (Directory.Exists(target))
                    target += "_" + Guid.NewGuid().ToString("N").Substring(0, 4);
                Directory.Move(temp, target);
                Debug.Log("[IOSPort] backed up " + files.Count + " save file(s) to " + FolderName + "/" + Path.GetFileName(target) + " (" + reason + ")");

                var all = Directory.GetDirectories(backups).Where(d => !Path.GetFileName(d).StartsWith("."))
                    .OrderBy(d => d, StringComparer.Ordinal).ToList();
                for (int i = 0; i < all.Count - MaxBackups; i++)
                    Directory.Delete(all[i], true);
                foreach (var stale in Directory.GetDirectories(backups).Where(d => Path.GetFileName(d).StartsWith(".") && d != temp))
                    Directory.Delete(stale, true);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[IOSPort] save backup failed: " + e.Message);
            }
        }

        static bool SameContents(string a, string b)
        {
            if (!File.Exists(b))
                return false;
            var fa = new FileInfo(a);
            var fb = new FileInfo(b);
            if (fa.Length != fb.Length)
                return false;
            return File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
        }
    }
}
