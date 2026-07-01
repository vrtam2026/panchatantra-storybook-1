using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEngine;

/// <summary>
/// One-click tool to configure all audio packs as Addressable assets.
///
/// HOW TO USE:
///   1. Drop VO files into Assets/Audio/{Language}/   named: {pageId}_VO.mp3
///   2. Drop BGM files into Assets/Audio/{Language}/  named: {pageId}_BGM.mp3
///   3. Tools → AR Storybook → Setup Audio Addressables
///   4. Done — all packs created, groups configured, catalog filled.
/// </summary>
public static class AudioAddressableSetupTool
{
    private const string AudioRootFolder  = "Assets/Audio";
    private const string PackOutputFolder = "Assets/code/AudioPacks";
    private const string CatalogAssetPath = "Assets/code/AudioLanguageCatalog.asset";
    private const float  DefaultBgmVolume = 0.5f;

    [MenuItem("Tools/AR Storybook/Setup Audio Addressables")]
    public static void SetupAll()
    {
        AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.Settings;
        if (settings == null)
        {
            Debug.LogError("[Audio Setup] Addressable Asset Settings not found. " +
                           "Open Window → Asset Management → Addressables → Groups first.");
            return;
        }

        ARAddressableAudioCatalog catalog = AssetDatabase.LoadAssetAtPath<ARAddressableAudioCatalog>(CatalogAssetPath);
        if (catalog == null)
        {
            Debug.LogError($"[Audio Setup] AudioLanguageCatalog not found at: {CatalogAssetPath}");
            return;
        }

        if (!Directory.Exists(AudioRootFolder))
        {
            Debug.LogWarning($"[Audio Setup] Audio root folder not found: {AudioRootFolder}");
            return;
        }

        int totalCreated = 0;
        int totalUpdated = 0;
        int totalSkipped = 0;
        bool catalogDirty = false;

        foreach (string langDir in Directory.GetDirectories(AudioRootFolder))
        {
            string language  = Path.GetFileName(langDir);
            string groupName = $"Audio_{language}";

            AddressableAssetGroup group = GetOrCreateGroup(settings, groupName);
            string packFolder = $"{PackOutputFolder}/{language}";
            EnsureFolder(packFolder);

            // Group files by pageId
            var byPage = new Dictionary<string, List<string>>();
            foreach (string file in Directory.GetFiles(langDir, "*", SearchOption.TopDirectoryOnly)
                         .Where(IsAudioFile))
            {
                if (!TryParsePageId(file, out string pageId)) continue;
                if (!byPage.ContainsKey(pageId)) byPage[pageId] = new List<string>();
                byPage[pageId].Add(file);
            }

            foreach (var kvp in byPage)
            {
                string pageId   = kvp.Key;
                string packPath = $"{packFolder}/Page_{pageId}_{language}.asset";

                ARPageAudioPack pack = AssetDatabase.LoadAssetAtPath<ARPageAudioPack>(packPath);
                bool isNew = pack == null;

                if (isNew)
                {
                    pack = ScriptableObject.CreateInstance<ARPageAudioPack>();
                    pack.languageName = language;
                    pack.pageId       = pageId;
                    pack.voiceClips   = new List<ARPageAudioPack.AudioSegment>();
                    pack.bgmClips     = new List<ARPageAudioPack.AudioSegment>();
                    AssetDatabase.CreateAsset(pack, packPath);
                    totalCreated++;
                }

                bool packDirty = false;

                foreach (string file in kvp.Value)
                {
                    string assetPath = file.Replace('\\', '/');
                    // Make path relative to project (Assets/...)
                    int assetsIdx = assetPath.IndexOf("Assets/");
                    if (assetsIdx < 0) continue;
                    assetPath = assetPath.Substring(assetsIdx);

                    AudioClip clip = AssetDatabase.LoadAssetAtPath<AudioClip>(assetPath);
                    if (clip == null) continue;

                    if (IsVO(file))
                    {
                        if (pack.voiceClips.Count == 0 || pack.voiceClips[0].clip != clip)
                        {
                            pack.voiceClips = new List<ARPageAudioPack.AudioSegment>
                            {
                                new ARPageAudioPack.AudioSegment { clip = clip, volume = 1f }
                            };
                            packDirty = true;
                        }
                    }
                    else if (IsBGM(file))
                    {
                        if (pack.bgmClips.Count == 0 || pack.bgmClips[0].clip != clip)
                        {
                            pack.bgmClips = new List<ARPageAudioPack.AudioSegment>
                            {
                                new ARPageAudioPack.AudioSegment { clip = clip, volume = DefaultBgmVolume, loop = true }
                            };
                            packDirty = true;
                        }
                    }
                }

                if (packDirty || isNew)
                {
                    EditorUtility.SetDirty(pack);
                    if (!isNew) totalUpdated++;
                }
                else
                {
                    totalSkipped++;
                }

                // Register as Addressable
                string address = $"audio/{language}/{pageId}";
                RegisterAddressable(settings, group, packPath, address);

                // Update catalog — check existing entries manually
                if (!catalog.TryGetAddress(language, pageId, out _))
                {
                    catalog.AddEntry(language, pageId, address);
                    catalogDirty = true;
                }
            }
        }

        if (catalogDirty) EditorUtility.SetDirty(catalog);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log($"[Audio Setup] Done — Created:{totalCreated}  Updated:{totalUpdated}  Skipped:{totalSkipped}");
        EditorUtility.DisplayDialog("Audio Setup Complete",
            $"Created: {totalCreated}\nUpdated: {totalUpdated}\nSkipped (no change): {totalSkipped}\n\nAll audio packs are Addressable.",
            "OK");
    }

    // -----------------------------------------------------------------------

    private static AddressableAssetGroup GetOrCreateGroup(AddressableAssetSettings settings, string groupName)
    {
        AddressableAssetGroup group = settings.FindGroup(groupName);
        if (group != null) return group;

        group = settings.CreateGroup(groupName, false, false, true, null);

        var bundled = group.AddSchema<BundledAssetGroupSchema>();
        bundled.BundleMode = BundledAssetGroupSchema.BundlePackingMode.PackSeparately;
        bundled.BuildPath.SetVariableByName(settings, AddressableAssetSettings.kRemoteBuildPath);
        bundled.LoadPath.SetVariableByName(settings, AddressableAssetSettings.kRemoteLoadPath);

        group.AddSchema<ContentUpdateGroupSchema>();

        Debug.Log($"[Audio Setup] Created group: {groupName} (Pack Separately, Remote)");
        return group;
    }

    private static void RegisterAddressable(AddressableAssetSettings settings,
        AddressableAssetGroup group, string assetPath, string address)
    {
        string guid = AssetDatabase.AssetPathToGUID(assetPath);
        if (string.IsNullOrEmpty(guid)) return;

        AddressableAssetEntry entry = settings.FindAssetEntry(guid);
        if (entry == null)
            entry = settings.CreateOrMoveEntry(guid, group, false, false);

        entry.address = address;
    }

    private static void EnsureFolder(string path)
    {
        if (Directory.Exists(path)) return;
        Directory.CreateDirectory(path);
        AssetDatabase.ImportAsset(path);
    }

    private static bool IsAudioFile(string path)
    {
        string ext = Path.GetExtension(path).ToLower();
        return ext == ".mp3" || ext == ".wav" || ext == ".ogg" || ext == ".aif" || ext == ".aiff";
    }

    private static bool IsVO(string path)  => Path.GetFileNameWithoutExtension(path).ToUpper().EndsWith("_VO");
    private static bool IsBGM(string path) => Path.GetFileNameWithoutExtension(path).ToUpper().EndsWith("_BGM");

    private static bool TryParsePageId(string filePath, out string pageId)
    {
        pageId = null;
        string name  = Path.GetFileNameWithoutExtension(filePath);
        string upper = name.ToUpper();
        string stripped = upper.EndsWith("_VO")  ? name.Substring(0, name.Length - 3)
                        : upper.EndsWith("_BGM") ? name.Substring(0, name.Length - 4)
                        : null;
        if (stripped == null) return false;
        pageId = stripped;
        return !string.IsNullOrEmpty(pageId);
    }
}
