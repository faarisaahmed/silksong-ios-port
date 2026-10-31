using UnityEditor;

namespace IOSPort
{
    // Touch HUD art (Assets/Resources/IOSPortHud) imports as crisp, uncompressed UI sprites.
    public class HudArtImporter : AssetPostprocessor
    {
        void OnPreprocessTexture()
        {
            if (assetPath.StartsWith("Assets/Resources/IOSPortSplash/"))
            {
                // Loading screen: full size, RGB (the dark gradients band if compressed).
                var splash = (TextureImporter)assetImporter;
                splash.textureType = TextureImporterType.Default;
                splash.mipmapEnabled = false;
                splash.npotScale = TextureImporterNPOTScale.None;
                splash.textureCompression = TextureImporterCompression.Uncompressed;
                splash.SetPlatformTextureSettings(new TextureImporterPlatformSettings
                {
                    name = "iPhone",
                    overridden = true,
                    maxTextureSize = 4096,
                    format = TextureImporterFormat.RGB24,
                });
                return;
            }
            if (!assetPath.StartsWith("Assets/Resources/IOSPortHud/"))
                return;
            var importer = (TextureImporter)assetImporter;
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.mipmapEnabled = false;
            importer.alphaIsTransparency = true;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.SetPlatformTextureSettings(new TextureImporterPlatformSettings
            {
                name = "iPhone",
                overridden = true,
                maxTextureSize = 512,
                format = TextureImporterFormat.RGBA32,
            });
        }
    }
}
