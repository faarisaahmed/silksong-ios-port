using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace IOSPort
{
    // iPhone texture overrides to cut memory on 4 GB devices (iPhone 11 Pro Max).
    // - Large textures use lighter ASTC block sizes (6x6 for >= 2048 px, 5x5 for >= 1024 px).
    // - Textures over 2048 px are halved, except ones referenced by standalone Sprite assets
    //   (listed in sprite_textures.txt): those sprites store pixel rects that would no longer line up.
    // - TextMeshPro font atlases stay uncompressed at full size (Alpha8, as on PC): they hold distance fields, and
    //   ASTC blocks turn the letter edges into visible steps.
    public static class TextureOptimizer
    {
        const string ProtectedList = "Assets/Editor/IOSPort/sprite_textures.txt";

        [MenuItem("iOS Port/Optimize Textures")]
        public static void Run()
        {
            var protectedGuids = new HashSet<string>(File.ReadAllLines(ProtectedList));
            var fontAtlases = FontAtlases();
            int changed = 0, halved = 0;
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { "Assets" }))
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    if (!(AssetImporter.GetAtPath(path) is TextureImporter importer))
                        continue;
                    if (path.StartsWith("Assets/IOSPortIcon/") || path.StartsWith("Assets/Resources/IOSPortHud/") || path.StartsWith("Assets/Resources/IOSPortSplash/"))
                        continue; // app icon, touch HUD art and loading screen stay uncompressed
                    importer.GetSourceTextureWidthAndHeight(out int w, out int h);
                    int side = Mathf.Max(w, h);
                    if (fontAtlases.Contains(path))
                    {
                        var font = importer.GetPlatformTextureSettings("iPhone");
                        if (font.overridden && font.format == TextureImporterFormat.Alpha8 && font.maxTextureSize >= side)
                            continue;
                        font.overridden = true;
                        font.maxTextureSize = Mathf.Max(32, Mathf.NextPowerOfTwo(side));
                        font.format = TextureImporterFormat.Alpha8;
                        font.textureCompression = TextureImporterCompression.Uncompressed;
                        importer.SetPlatformTextureSettings(font);
                        importer.SaveAndReimport();
                        changed++;
                        continue;
                    }
                    if (side < 1024)
                        continue;

                    int maxSize = Mathf.Min(importer.maxTextureSize, Mathf.NextPowerOfTwo(side));
                    if (side > 2048 && !protectedGuids.Contains(guid))
                    {
                        maxSize /= 2;
                        halved++;
                    }
                    var settings = importer.GetPlatformTextureSettings("iPhone");
                    settings.overridden = true;
                    settings.maxTextureSize = maxSize;
                    settings.format = side >= 2048 ? TextureImporterFormat.ASTC_6x6 : TextureImporterFormat.ASTC_5x5;
                    settings.compressionQuality = (int)TextureCompressionQuality.Normal;
                    importer.SetPlatformTextureSettings(settings);
                    importer.SaveAndReimport();
                    changed++;
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }
            Debug.Log($"[IOSPort] Texture optimizer: {changed} textures overridden for iPhone, {halved} halved, {fontAtlases.Count} font atlases");
        }

        // The ripped project's font assets don't load as TMP_FontAsset in the editor, so their atlases are found from
        // the asset files themselves ("atlas: {fileID: 2800000, guid: ...}").
        static HashSet<string> FontAtlases()
        {
            var paths = new HashSet<string>();
            var atlas = new Regex(@"^  atlas: \{fileID: 2800000, guid: ([0-9a-f]{32})", RegexOptions.Multiline);
            foreach (var file in Directory.GetFiles("Assets", "*.asset", SearchOption.AllDirectories))
            {
                string head;
                using (var reader = new StreamReader(file))
                {
                    var buffer = new char[2048];
                    head = new string(buffer, 0, reader.Read(buffer, 0, buffer.Length));
                }
                if (!head.Contains("m_fontAssetType") && !head.Contains("m_FaceInfo") && !head.Contains("fontInfo:"))
                    continue;
                foreach (Match m in atlas.Matches(File.ReadAllText(file)))
                {
                    var path = AssetDatabase.GUIDToAssetPath(m.Groups[1].Value);
                    if (!string.IsNullOrEmpty(path))
                        paths.Add(path);
                }
            }
            return paths;
        }
    }
}
