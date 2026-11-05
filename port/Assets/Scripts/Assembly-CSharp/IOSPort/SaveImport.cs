using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using UnityEngine;

namespace IOSPort
{
    // IMPORT SAVES in the port settings panel: opens the Files picker for a zip from EXPORT SAVES or loose
    // save files (user1.dat ... user4.dat, from a PC or another phone), copies them into Documents and refreshes
    // the save slots. userN.dat (or a backup, "Save N - <date>.dat") goes into slot N; a .dat with any other name goes into the first empty slot.
    // Only works from the title screen, so the game never has a save open while it changes. shared.dat (settings)
    // is left alone, because the game keeps its own copy in memory and would write over it. SaveBackup takes a
    // snapshot first, so the saves that were there can be got back from Documents/SaveBackups.
    public class SaveImport : MonoBehaviour
    {
        const string ObjectName = "IOSPort Save Import";
        const int Slots = 4;

        [DllImport("__Internal")]
        static extern void IOSPortPickFiles(string receiver);

        static readonly Regex SlotFile = new Regex(@"^user([1-4])\.dat(\.bak\d*)?$", RegexOptions.IgnoreCase);
        static readonly Regex BackupFile = new Regex(@"^Save ([1-4]) - .*\.dat$", RegexOptions.IgnoreCase);
        static readonly Regex RestoreFolder = new Regex(@"^Restore_Points[1-4]$", RegexOptions.IgnoreCase);
        // Saves start with BinaryFormatter's header.
        static readonly byte[] Header = { 0, 1, 0, 0, 0, 255, 255, 255, 255 };

        public static string Status { get; private set; } = "";

        public static string Label => "IMPORT SAVES" + (Status.Length > 0 ? " (" + Status + ")" : "");

        static bool OnTitleScreen => GameManager.UnsafeInstance != null && GameManager.UnsafeInstance.GameState == GlobalEnums.GameState.MAIN_MENU;

        public static void Import()
        {
            if (!OnTitleScreen)
            {
                Status = "QUIT TO THE MAIN MENU FIRST";
                return;
            }
            if (Application.platform != RuntimePlatform.IPhonePlayer)
                return;
            if (GameObject.Find(ObjectName) == null)
                DontDestroyOnLoad(new GameObject(ObjectName, typeof(SaveImport)));
            Status = "PICKING";
            IOSPortPickFiles(ObjectName);
        }

        // From the picker: the picked files' paths, one per line.
        void OnFilesPicked(string paths)
        {
            var files = paths.Split('\n').Where(p => p.Length > 0).ToList();
            if (files.Count == 0)
            {
                Status = "";
                return;
            }
            ImportFiles(files);
            foreach (var f in files)
                try { File.Delete(f); } catch (Exception) { }
        }

        public static void ImportFiles(IList<string> files)
        {
            if (!OnTitleScreen)
            {
                Status = "QUIT TO THE MAIN MENU FIRST";
                return;
            }
            try
            {
                // Destination path (relative to Documents) -> contents.
                var found = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
                var unnamed = new List<byte[]>();
                foreach (var file in files)
                {
                    if (file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        ReadZip(file, found);
                    else
                    {
                        string target = Destination(Path.GetFileName(file));
                        var data = File.ReadAllBytes(file);
                        if (target != null)
                            found[target] = data;
                        else if (file.EndsWith(".dat", StringComparison.OrdinalIgnoreCase) && IsSave(data))
                            unnamed.Add(data);
                    }
                }
                // Main save files that aren't saves would show up as corrupted slots; leave those out.
                foreach (var bad in found.Where(f => SlotFile.Match(f.Key).Groups[2].Length == 0 && !f.Key.Contains("/") && !IsSave(f.Value)).Select(f => f.Key).ToList())
                {
                    Debug.LogWarning("[IOSPort] save import: " + bad + " isn't a Silksong save, skipped");
                    found.Remove(bad);
                }

                string root = Application.persistentDataPath;
                var taken = new HashSet<int>(Enumerable.Range(1, Slots).Where(i => File.Exists(Path.Combine(root, "user" + i + ".dat")) || found.ContainsKey("user" + i + ".dat")));
                foreach (var data in unnamed)
                {
                    int slot = Enumerable.Range(1, Slots).FirstOrDefault(i => !taken.Contains(i));
                    if (slot == 0)
                    {
                        Status = "NO EMPTY SLOT";
                        return;
                    }
                    taken.Add(slot);
                    found["user" + slot + ".dat"] = data;
                }
                int slots = found.Keys.Count(k => !k.Contains("/") && SlotFile.Match(k).Groups[2].Length == 0);
                if (slots == 0)
                {
                    Status = "NO SAVES FOUND";
                    return;
                }

                SaveBackup.Snapshot("before import");
                foreach (var f in found)
                {
                    string path = Path.Combine(root, f.Key);
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllBytes(path, f.Value);
                }
                Debug.Log("[IOSPort] imported " + found.Count + " save file(s): " + string.Join(", ", found.Keys.OrderBy(k => k)));
                Status = slots == 1 ? "1 SLOT IMPORTED" : slots + " SLOTS IMPORTED";
                UIManager.instance?.ReloadSaves();
            }
            catch (Exception e)
            {
                Debug.LogError("[IOSPort] save import failed: " + e);
                Status = "FAILED";
            }
        }

        // Takes the save files out of a zip, wherever they are in it (EXPORT SAVES zips have them at the top, a
        // zipped PC save folder has them one folder down).
        static void ReadZip(string file, Dictionary<string, byte[]> found)
        {
            using (var zip = new ZipArchive(File.OpenRead(file), ZipArchiveMode.Read))
                foreach (var entry in zip.Entries)
                {
                    var parts = entry.FullName.Replace('\\', '/').Split('/');
                    if (parts.Length == 0 || parts.Any(p => p == ".."))
                        continue;
                    string target;
                    int restore = Array.FindIndex(parts, p => RestoreFolder.IsMatch(p));
                    if (restore >= 0 && restore < parts.Length - 1 && parts[parts.Length - 1].Length > 0)
                        target = string.Join("/", parts, restore, parts.Length - restore);
                    else
                        target = Destination(parts[parts.Length - 1]);
                    if (target == null)
                        continue;
                    using (var src = entry.Open())
                    using (var dst = new MemoryStream())
                    {
                        src.CopyTo(dst);
                        found[target] = dst.ToArray();
                    }
                }
        }

        // userN.dat and its .bak copies keep their names, and the backups ("Save N - <date>.dat") go back to slot N;
        // anything else isn't placed by name.
        static string Destination(string name)
        {
            if (SlotFile.IsMatch(name))
                return name.ToLowerInvariant();
            var backup = BackupFile.Match(name);
            return backup.Success ? "user" + backup.Groups[1].Value + ".dat" : null;
        }

        static bool IsSave(byte[] data) => data.Length > Header.Length && data.AsSpan(0, Header.Length).SequenceEqual(Header);
    }
}
