using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEngine;

/// <summary>
/// One-time migration tool.
/// Reads ARAudioLocalizationDatabase and creates:
///   - ARPageAudioPack assets for every English page (clips copied from DB)
///   - ARPageAudioPack assets for every Hindi page   (empty — clips added later)
/// Registers all assets as Addressable in Audio_English / Audio_Hindi groups.
/// Fills AudioLanguageCatalog with all entries.
///
/// Menu: Tools → AR Storybook → Migrate Audio Packs from Database
/// </summary>
public static class AudioPackMigrationTool
{
    private const string DbPath          = "Assets/code/ARAudioLocalizationDatabase.asset";
    private const string CatalogPath     = "Assets/code/AudioLanguageCatalog.asset";
    private const string EnglishPackDir  = "Assets/code/AudioPacks/English";
    private const string HindiPackDir    = "Assets/code/AudioPacks/Hindi";
    private const string EnglishGroup    = "Audio_English";
    private const string HindiGroup      = "Audio_Hindi";

    [MenuItem("Tools/AR Storybook/Migrate Audio Packs from Database")]
    public static void Run()
    {
        // ── Load dependencies ──────────────────────────────────────────────
        var db = AssetDatabase.LoadAssetAtPath<ARAudioLocalizationDatabase>(DbPath);
        if (db == null)
        {
            EditorUtility.DisplayDialog("Error", $"ARAudioLocalizationDatabase not found at:\n{DbPath}", "OK");
            return;
        }

        var catalog = AssetDatabase.LoadAssetAtPath<ARAddressableAudioCatalog>(CatalogPath);
        if (catalog == null)
        {
            EditorUtility.DisplayDialog("Error", $"AudioLanguageCatalog not found at:\n{CatalogPath}", "OK");
            return;
        }

        var settings = AddressableAssetSettingsDefaultObject.Settings;
        if (settings == null)
        {
            EditorUtility.DisplayDialog("Error", "Addressable Asset Settings not found. Open Window → Asset Management → Addressables → Groups first.", "OK");
            return;
        }

        // ── Ensure folders ─────────────────────────────────────────────────
        EnsureFolder(EnglishPackDir);
        EnsureFolder(HindiPackDir);

        // ── Ensure groups ──────────────────────────────────────────────────
        var engGroup  = GetOrCreateGroup(settings, EnglishGroup);
        var hindiGroup = GetOrCreateGroup(settings, HindiGroup);

        // ── Find the English language pack in the database ─────────────────
        ARAudioLocalizationDatabase.LanguagePack englishPack = null;
        foreach (var lp in db.GetAllLanguagePacks())
        {
            if (lp != null && lp.languageName.Trim().ToLower() == "english")
            {
                englishPack = lp;
                break;
            }
        }

        if (englishPack == null)
        {
            EditorUtility.DisplayDialog("Error", "No English language pack found in ARAudioLocalizationDatabase.", "OK");
            return;
        }

        int created  = 0;
        int skipped  = 0;
        bool catalogDirty = false;

        EditorUtility.DisplayProgressBar("Migrating Audio Packs", "Starting...", 0f);

        try
        {
            int total = englishPack.pages.Count;

            for (int i = 0; i < total; i++)
            {
                var page = englishPack.pages[i];
                if (page == null || string.IsNullOrWhiteSpace(page.pageId)) continue;

                string pageId = page.pageId.Trim();
                float progress = (float)i / total;
                EditorUtility.DisplayProgressBar("Migrating Audio Packs", $"Processing {pageId}...", progress);

                // ── English pack ───────────────────────────────────────────
                string engPath = $"{EnglishPackDir}/Page_{pageId}_English.asset";
                var engAsset = AssetDatabase.LoadAssetAtPath<ARPageAudioPack>(engPath);

                if (engAsset == null)
                {
                    engAsset = ScriptableObject.CreateInstance<ARPageAudioPack>();
                    engAsset.languageName = "English";
                    engAsset.pageId       = pageId;
                    engAsset.voiceClips   = CopySegments(page.voiceClips);
                    engAsset.bgmClips     = CopySegments(page.bgmClips);
                    AssetDatabase.CreateAsset(engAsset, engPath);
                    created++;
                }
                else
                {
                    // Update clips in case DB changed
                    engAsset.voiceClips = CopySegments(page.voiceClips);
                    engAsset.bgmClips   = CopySegments(page.bgmClips);
                    EditorUtility.SetDirty(engAsset);
                    skipped++;
                }

                string engAddress = $"audio/English/{pageId}";
                RegisterAddressable(settings, engGroup, engPath, engAddress);
                if (!catalog.TryGetAddress("English", pageId, out _))
                {
                    catalog.AddEntry("English", pageId, engAddress);
                    catalogDirty = true;
                }

                // ── Hindi pack (empty — clips added later) ─────────────────
                string hindiPath = $"{HindiPackDir}/Page_{pageId}_Hindi.asset";
                var hindiAsset = AssetDatabase.LoadAssetAtPath<ARPageAudioPack>(hindiPath);

                if (hindiAsset == null)
                {
                    hindiAsset = ScriptableObject.CreateInstance<ARPageAudioPack>();
                    hindiAsset.languageName = "Hindi";
                    hindiAsset.pageId       = pageId;
                    hindiAsset.voiceClips   = new List<ARPageAudioPack.AudioSegment>
                    {
                        new ARPageAudioPack.AudioSegment { clip = null, volume = 1f, loop = false }
                    };
                    hindiAsset.bgmClips = new List<ARPageAudioPack.AudioSegment>
                    {
                        new ARPageAudioPack.AudioSegment { clip = null, volume = 0.5f, loop = true }
                    };
                    AssetDatabase.CreateAsset(hindiAsset, hindiPath);
                    created++;
                }

                string hindiAddress = $"audio/Hindi/{pageId}";
                RegisterAddressable(settings, hindiGroup, hindiPath, hindiAddress);
                if (!catalog.TryGetAddress("Hindi", pageId, out _))
                {
                    catalog.AddEntry("Hindi", pageId, hindiAddress);
                    catalogDirty = true;
                }
            }

            if (catalogDirty) EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        EditorUtility.DisplayDialog("Migration Complete",
            $"English packs: {englishPack.pages.Count}\n" +
            $"Hindi packs:   {englishPack.pages.Count} (empty — add clips when audio arrives)\n\n" +
            $"Created: {created}   Updated: {skipped}\n" +
            $"All registered in Addressable groups.\n" +
            $"AudioLanguageCatalog updated.",
            "OK");

        Debug.Log($"[Audio Migration] Done — {created} created, {skipped} updated. " +
                  $"English:{englishPack.pages.Count}  Hindi:{englishPack.pages.Count}");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static List<ARPageAudioPack.AudioSegment> CopySegments(
        List<ARAudioLocalizationDatabase.AudioSegment> src)  // DB AudioSegment → Pack AudioSegment
    {
        var list = new List<ARPageAudioPack.AudioSegment>();
        if (src == null) return list;
        foreach (var s in src)
        {
            if (s == null) continue;
            list.Add(new ARPageAudioPack.AudioSegment
            {
                clip        = s.clip,
                delayBefore = s.delayBefore,
                delayAfter  = s.delayAfter,
                volume      = s.volume,
                loop        = s.loop
            });
        }
        return list;
    }

    private static AddressableAssetGroup GetOrCreateGroup(AddressableAssetSettings settings, string groupName)
    {
        var group = settings.FindGroup(groupName);
        if (group != null) return group;

        group = settings.CreateGroup(groupName, false, false, true, null);

        var bundled = group.AddSchema<BundledAssetGroupSchema>();
        bundled.BundleMode = BundledAssetGroupSchema.BundlePackingMode.PackSeparately;
        bundled.BuildPath.SetVariableByName(settings, AddressableAssetSettings.kRemoteBuildPath);
        bundled.LoadPath.SetVariableByName(settings, AddressableAssetSettings.kRemoteLoadPath);
        group.AddSchema<ContentUpdateGroupSchema>();

        Debug.Log($"[Audio Migration] Created group: {groupName}");
        return group;
    }

    private static void RegisterAddressable(AddressableAssetSettings settings,
        AddressableAssetGroup group, string assetPath, string address)
    {
        string guid = AssetDatabase.AssetPathToGUID(assetPath);
        if (string.IsNullOrEmpty(guid)) return;

        var entry = settings.FindAssetEntry(guid) ??
                    settings.CreateOrMoveEntry(guid, group, false, false);
        entry.address = address;
    }

    private static void EnsureFolder(string path)
    {
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
            AssetDatabase.ImportAsset(path);
        }
    }
}
