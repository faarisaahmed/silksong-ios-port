using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEngine;

namespace IOSPort
{
    // EXPORT SAVES in the port settings panel: zips the saves (user*.dat, shared.dat, the game's .bak copies and
    // restore points) into the app's cache folder and opens the iOS share sheet, so they can be sent to
    // iCloud Drive, AirDrop or Files outside the app. The zip mirrors the Documents folder, so restoring is
    // unzipping it back into the Silksong folder while the game is closed.
    public static class SaveExport
    {
        [DllImport("__Internal")]
        static extern void IOSPortShareFile(string path);

        public static string Status { get; private set; } = "";

        public static string Label => "EXPORT SAVES" + (Status.Length > 0 ? " (" + Status + ")" : "");

        public static void Export()
        {
            try
            {
                string root = Application.persistentDataPath;
                var files = Directory.GetFiles(root)
                    .Where(f => IsSaveFile(Path.GetFileName(f)))
                    .Concat(Directory.GetDirectories(root, "Restore_Points*").SelectMany(d => Directory.GetFiles(d, "*", SearchOption.AllDirectories)))
                    .ToList();
                if (files.Count == 0)
                {
                    Status = "NO SAVES YET";
                    return;
                }
                string zipPath = Path.Combine(Application.temporaryCachePath, "Silksong saves " + DateTime.Now.ToString("yyyy-MM-dd HH.mm") + ".zip");
                foreach (var old in Directory.GetFiles(Application.temporaryCachePath, "Silksong saves *.zip"))
                    File.Delete(old);
                using (var zip = new ZipArchive(File.Create(zipPath), ZipArchiveMode.Create))
                {
                    foreach (var f in files)
                    {
                        var entry = zip.CreateEntry(Path.GetRelativePath(root, f).Replace('\\', '/'), System.IO.Compression.CompressionLevel.Optimal);
                        entry.LastWriteTime = File.GetLastWriteTime(f);
                        using (var src = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        using (var dst = entry.Open())
                            src.CopyTo(dst);
                    }
                }
                Debug.Log($"[IOSPort] exported {files.Count} save files to {zipPath}");
                Status = Directory.GetFiles(root, "user*.dat").Length + " SAVE SLOTS";
                if (Application.platform == RuntimePlatform.IPhonePlayer)
                    IOSPortShareFile(zipPath);
            }
            catch (Exception e)
            {
                Debug.LogError("[IOSPort] save export failed: " + e);
                Status = "FAILED";
            }
        }

        static bool IsSaveFile(string name) =>
            (name.StartsWith("user", StringComparison.OrdinalIgnoreCase) || name.StartsWith("shared", StringComparison.OrdinalIgnoreCase))
            && name.IndexOf(".dat", StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
