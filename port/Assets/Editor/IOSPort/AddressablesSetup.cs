using System.IO;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEngine;

namespace IOSPort
{
    // Recreates the shipped game's Addressables layout (groups, addresses, labels)
    // from addressables_map.tsv, generated from the original catalog.bin and bundle contents.
    // Each line: asset path \t address \t comma-separated labels \t group name \t 1 if the group is looked up through the catalog.
    // Groups with 0 only hold shared dependencies, matching the original's dependency-only bundles.
    public static class AddressablesSetup
    {
        const string MapPath = "Assets/Editor/IOSPort/addressables_map.tsv";

        [MenuItem("iOS Port/Setup Addressables")]
        public static void Setup()
        {
            var settings = AddressableAssetSettingsDefaultObject.GetSettings(true);
            settings.BuildRemoteCatalog = false;
            settings.BuildAddressablesWithPlayerBuild = AddressableAssetSettings.PlayerBuildOption.DoNotBuildWithPlayer;

            int added = 0, missing = 0;
            foreach (var line in File.ReadAllLines(MapPath))
            {
                var cols = line.Split('\t');
                string path = cols[0], address = cols[1], labels = cols[2], groupName = cols[3];
                bool inCatalog = cols[4] == "1";

                string guid = AssetDatabase.AssetPathToGUID(path);
                if (string.IsNullOrEmpty(guid))
                {
                    Debug.LogWarning($"[IOSPort] Missing asset: {path}");
                    missing++;
                    continue;
                }

                var group = settings.FindGroup(groupName) ?? CreateGroup(settings, groupName, inCatalog);
                var entry = settings.CreateOrMoveEntry(guid, group, false, false);
                entry.SetAddress(string.IsNullOrEmpty(address) ? path : address, false);
                foreach (var label in labels.Split(','))
                {
                    if (label.Length == 0)
                        continue;
                    settings.AddLabel(label, false);
                    entry.SetLabel(label, true, true, false);
                }
                added++;
            }

            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
            Debug.Log($"[IOSPort] Addressables setup: {added} entries, {missing} missing, {settings.groups.Count} groups");
            IsolateDuplicates(settings);
            DisableCrc(settings);
        }

        // The bundles ship inside the signed app, so a CRC check guards against nothing, and it makes Unity read and
        // decompress every bundle in full when it's loaded instead of only the parts the game asks for.
        static void DisableCrc(AddressableAssetSettings settings)
        {
            foreach (var group in settings.groups)
            {
                var schema = group != null ? group.GetSchema<BundledAssetGroupSchema>() : null;
                if (schema != null && schema.UseAssetBundleCrc)
                {
                    schema.UseAssetBundleCrc = false;
                    EditorUtility.SetDirty(schema);
                }
            }
            AssetDatabase.SaveAssets();
        }

        // Anything still pulled into more than one bundle (e.g. shared textures the original kept in
        // sprite atlases we could not map) gets its own small bundle instead of a copy per scene.
        static void IsolateDuplicates(AddressableAssetSettings settings)
        {
            const string groupName = "Duplicate Asset Isolation";
            var old = settings.FindGroup(groupName);
            if (old != null)
                settings.RemoveGroup(old);
            new UnityEditor.AddressableAssets.Build.AnalyzeRules.CheckBundleDupeDependencies().FixIssues(settings);
            var group = settings.FindGroup(groupName);
            if (group == null)
            {
                Debug.Log("[IOSPort] No duplicated dependencies");
                return;
            }
            var schema = group.GetSchema<BundledAssetGroupSchema>();
            schema.BundleMode = BundledAssetGroupSchema.BundlePackingMode.PackSeparately;
            schema.Compression = BundledAssetGroupSchema.BundleCompressionMode.LZ4;
            schema.BuildPath.SetVariableByName(settings, AddressableAssetSettings.kLocalBuildPath);
            schema.LoadPath.SetVariableByName(settings, AddressableAssetSettings.kLocalLoadPath);
            schema.IncludeAddressInCatalog = false;
            schema.IncludeGUIDInCatalog = false;
            schema.IncludeLabelsInCatalog = false;
            group.GetSchema<ContentUpdateGroupSchema>().StaticContent = false;
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
            Debug.Log($"[IOSPort] Isolated {group.entries.Count} duplicated dependencies");
        }

        static AddressableAssetGroup CreateGroup(AddressableAssetSettings settings, string name, bool inCatalog)
        {
            var group = settings.CreateGroup(name, false, false, false, null,
                typeof(BundledAssetGroupSchema), typeof(ContentUpdateGroupSchema));
            var schema = group.GetSchema<BundledAssetGroupSchema>();
            schema.BundleMode = BundledAssetGroupSchema.BundlePackingMode.PackTogether;
            // LZ4 loads quickly and lets Unity read bundles without decompressing them fully into RAM.
            schema.Compression = BundledAssetGroupSchema.BundleCompressionMode.LZ4;
            schema.BuildPath.SetVariableByName(settings, AddressableAssetSettings.kLocalBuildPath);
            schema.LoadPath.SetVariableByName(settings, AddressableAssetSettings.kLocalLoadPath);
            schema.IncludeAddressInCatalog = inCatalog;
            schema.IncludeGUIDInCatalog = inCatalog;
            schema.IncludeLabelsInCatalog = inCatalog;
            group.GetSchema<ContentUpdateGroupSchema>().StaticContent = false;
            return group;
        }
    }
}
