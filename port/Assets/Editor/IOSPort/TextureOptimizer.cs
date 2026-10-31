using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace IOSPort
{
    // iPhone texture overrides to cut memory on 4 GB devices (iPhone 11 Pro Max).
    // - Large textures use lighter ASTC block sizes (6x6 for >= 2048 px, 5x5 for >= 1024 px).
    // - Textures over 2048 px are halved, except ones referenced by standalone Sprite assets
    //   (listed in sprite_textures.txt): those sprites store pixel rects that would no longer line up.
    public static class TextureOptimizer
    {
        const string ProtectedList = "Assets/Editor/IOSPort/sprite_textures.txt";

        [MenuItem("iOS Port/Optimize Textures")]
        public static void Run()
        {
            var protectedGuids = new HashSet<string>(File.ReadAllLines(ProtectedList));
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
            Debug.Log($"[IOSPort] Texture optimizer: {changed} textures overridden for iPhone, {halved} halved");
        }
    }
}
