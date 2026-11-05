using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;

namespace IOSPort
{
    // Extra safety for save files on iOS, on top of the game's own .bak files and restore points.
    // Copies each save slot (userN.dat) into Documents/SaveBackups as "Save N - <date time>.dat", and the game
    // settings (shared.dat) as "Settings - <date time>.dat", at launch, every few minutes and whenever the app goes
    // to the background, but only for the files that changed since their last backup. Keeps the newest
    // MaxPerSlot backups of each. They show up in the Files app; to restore one, pick it in IMPORT SAVES (it goes
    // back into its own slot), or copy it into the Silksong folder as userN.dat while the game is closed.
    public class SaveBackup : MonoBehaviour
    {
        const int MaxPerSlot = 15;
        const int MaxSettings = 5;
        const float IntervalSeconds = 300f;
        const string FolderName = "SaveBackups";
        const string DateFormat = "yyyy-MM-dd HH.mm";

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

        // "Save 2" for user2.dat, "Settings" for shared.dat.
        static string Label(string file)
        {
            string name = Path.GetFileNameWithoutExtension(file);
            return name == "shared" ? "Settings" : "Save " + name.Substring(4);
        }

        static List<string> SaveFiles(string dir) =>
            Directory.GetFiles(dir, "user*.dat").Where(f => int.TryParse(Path.GetFileNameWithoutExtension(f).Substring(4), out _))
                .Concat(Directory.GetFiles(dir, "shared.dat"))
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();

        // A label's backups, oldest first (the date in the name sorts in time order).
        static List<string> BackupsOf(string backups, string label) =>
            Directory.GetFiles(backups, label + " - *.dat").OrderBy(f => f, StringComparer.Ordinal).ToList();

        public static void Snapshot(string reason)
        {
            try
            {
                string root = Application.persistentDataPath;
                string backups = Path.Combine(root, FolderName);
                Directory.CreateDirectory(backups);
                MigrateFolders(backups);
                // Skip files written in the last couple of seconds; the game may still be writing them.
                foreach (var f in SaveFiles(root).Where(f => (DateTime.UtcNow - File.GetLastWriteTimeUtc(f)).TotalSeconds > 2))
                {
                    string label = Label(f);
                    var existing = BackupsOf(backups, label);
                    if (existing.Count > 0 && SameContents(f, existing[existing.Count - 1]))
                        continue;
                    string target = Add(backups, label, DateTime.Now, File.ReadAllBytes(f));
                    Debug.Log("[IOSPort] backed up " + Path.GetFileName(f) + " to " + FolderName + "/" + Path.GetFileName(target) + " (" + reason + ")");
                    Trim(backups, label);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[IOSPort] save backup failed: " + e.Message);
            }
        }

        static string Add(string backups, string label, DateTime when, byte[] data)
        {
            string target = Path.Combine(backups, label + " - " + when.ToString(DateFormat, CultureInfo.InvariantCulture) + ".dat");
            // Two backups in the same minute: the later one gets the seconds too.
            if (File.Exists(target))
                target = Path.Combine(backups, label + " - " + when.ToString(DateFormat + ".ss", CultureInfo.InvariantCulture) + ".dat");
            string temp = target + ".partial";
            File.WriteAllBytes(temp, data);
            if (File.Exists(target))
                File.Delete(target);
            File.Move(temp, target);
            return target;
        }

        static void Trim(string backups, string label)
        {
            var all = BackupsOf(backups, label);
            int keep = label == "Settings" ? MaxSettings : MaxPerSlot;
            for (int i = 0; i < all.Count - keep; i++)
                File.Delete(all[i]);
        }

        // Earlier versions kept a folder per backup (SaveBackups/<yyyy-MM-dd_HH-mm-ss>/) holding every file each time.
        // Turns those into the per-file backups, leaving out copies that hadn't changed since the one before.
        static void MigrateFolders(string backups)
        {
            var folders = Directory.GetDirectories(backups).OrderBy(d => d, StringComparer.Ordinal).ToList();
            if (folders.Count == 0)
                return;
            var last = new Dictionary<string, byte[]>();
            foreach (var label in new[] { "Settings", "Save 1", "Save 2", "Save 3", "Save 4" })
            {
                var existing = BackupsOf(backups, label);
                if (existing.Count > 0)
                    last[label] = File.ReadAllBytes(existing[existing.Count - 1]);
            }
            foreach (var folder in folders)
            {
                string name = Path.GetFileName(folder);
                // ".<date>.partial" folders were unfinished copies.
                bool partial = name.StartsWith(".") && name.EndsWith(".partial");
                if (!DateTime.TryParseExact(name.Substring(0, Math.Min(19, name.Length)), "yyyy-MM-dd_HH-mm-ss",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var when) && !partial)
                    continue;  // not one of ours
                if (!partial)
                    foreach (var f in SaveFiles(folder))
                    {
                        string label = Label(f);
                        var data = File.ReadAllBytes(f);
                        if (last.TryGetValue(label, out var prev) && prev.AsSpan().SequenceEqual(data))
                            continue;
                        Add(backups, label, when, data);
                        last[label] = data;
                    }
                Directory.Delete(folder, true);
            }
            foreach (var label in last.Keys)
                Trim(backups, label);
            Debug.Log("[IOSPort] save backups: moved " + folders.Count + " old backup folders to per-save files");
        }

        static bool SameContents(string a, string b)
        {
            var fa = new FileInfo(a);
            var fb = new FileInfo(b);
            if (fa.Length != fb.Length)
                return false;
            return File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
        }
    }
}
