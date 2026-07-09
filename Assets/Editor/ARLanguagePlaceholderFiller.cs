using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;
using UnityEngine.AddressableAssets;

/// <summary>
/// Fills in placeholder audio for ONE language, for every page that doesn't have
/// real audio for it yet -- including pages with no pack at all yet, not just
/// packs that already exist with empty slots. Works for ANY language.
///
/// Run from: click a Language asset in the Project window → its Inspector's
/// "Set Up &lt;Language&gt; Audio" button calls this automatically after trying
/// to connect real recordings first.
/// </summary>
public static class ARLanguagePlaceholderFiller
{
    private const string PackOutputFolder = "Assets/code/AudioPacks";
    private const string CatalogAssetPath = "Assets/code/AudioLanguageCatalog.asset";

    /// <summary>Standalone entry point -- confirms with the user, then fills gaps.</summary>
    public static void Run(string language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            EditorUtility.DisplayDialog("Error", "No language given.", "OK");
            return;
        }

        bool proceed = EditorUtility.DisplayDialog($"Fill {language} Placeholder Audio",
            $"This puts a placeholder sound into every {language} page that has NO audio yet.\n\n" +
            $"Pages that already have real {language} audio are never touched.",
            "Fill It", "Cancel");
        if (!proceed) return;

        int filled = FillGaps(language, out int skipped);
        EditorUtility.DisplayDialog("Done",
            $"Filled: {filled} {language} pages\nSkipped: {skipped} (already had real audio)",
            "OK");
    }

    /// <summary>
    /// Does the actual work with no confirmation dialog -- used when a caller (like the
    /// merged "Set Up Language Audio" button) has already confirmed with the user once.
    /// Returns how many pages were filled; `skipped` reports how many already had audio.
    /// </summary>
    public static int FillGaps(string language, out int skipped)
    {
        skipped = 0;
        var settings = AddressableAssetSettingsDefaultObject.Settings;
        var catalog = AssetDatabase.LoadAssetAtPath<ARAddressableAudioCatalog>(CatalogAssetPath);
        if (settings == null || catalog == null) return 0;

        List<AudioClip> pool = BuildPlaceholderPool();
        if (pool.Count == 0)
        {
            Debug.LogWarning("[Audio Filler] No real recordings exist yet anywhere in the project, so there's " +
                              "nothing to pick random placeholders from. Add at least one language's real audio " +
                              "first (any language), then run this again.");
            return 0;
        }

        var rng = new System.Random();

        var pages = PageIdentityUtility.GetAllPages()
            .Where(p => !p.addressableKey.ToLowerInvariant().Contains("quiz") && !string.IsNullOrEmpty(p.pageId))
            .ToList();

        int filled = 0;
        bool catalogDirty = false;

        // Shuffle once and hand pages out in that order instead of drawing independently
        // each time -- an independent random pick per page CAN repeat the same clip
        // several times by chance, especially when the pool is small (e.g. only one
        // story's worth of real recordings exist so far). Shuffling guarantees no
        // repeats until the whole pool has been used once; only then does it reshuffle
        // and start a new lap, so repeats never cluster together.
        List<AudioClip> shuffled = Shuffle(pool, rng);
        int poolIndex = 0;

        foreach (var page in pages)
        {
            if (catalog.HasEntry(language, page.pageId))
            {
                skipped++;
                continue;
            }

            string packFolder = $"{PackOutputFolder}/{language}";
            AudioAddressableSetupTool.EnsureFolder(packFolder);
            string packPath = $"{packFolder}/Page_{AudioAddressableSetupTool.SanitizeForFileName(page.pageId)}_{language}.asset";

            var pack = AssetDatabase.LoadAssetAtPath<ARPageAudioPack>(packPath);
            if (pack == null)
            {
                pack = ScriptableObject.CreateInstance<ARPageAudioPack>();
                pack.languageName = language;
                pack.pageId = page.pageId;
                pack.bgmClips = new List<ARPageAudioPack.AudioSegment>();
                AssetDatabase.CreateAsset(pack, packPath);
            }

            // Walk the shuffled pool one clip at a time; reshuffle and start over only
            // once every clip in it has been used, so no clip repeats until all of them
            // have had a turn.
            if (poolIndex >= shuffled.Count)
            {
                shuffled = Shuffle(pool, rng);
                poolIndex = 0;
            }
            AudioClip placeholder = shuffled[poolIndex];
            poolIndex++;

            pack.voiceClips = new List<ARPageAudioPack.AudioSegment>
            {
                new ARPageAudioPack.AudioSegment { clip = placeholder, volume = 1f, loop = false }
            };
            EditorUtility.SetDirty(pack);

            AddressableAssetGroup group = AudioAddressableSetupTool.GetOrCreateGroup(settings, $"Audio_{language}");
            AudioAddressableSetupTool.RegisterAddressable(settings, group, packPath, $"audio/{language}/{page.pageId}");

            catalog.AddEntry(language, page.pageId, new AssetReferenceT<ARPageAudioPack>(AssetDatabase.AssetPathToGUID(packPath)));
            catalogDirty = true;
            filled++;
        }

        if (catalogDirty) EditorUtility.SetDirty(catalog);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log($"[{language} Filler] Done — {filled} filled (randomly, from a pool of {pool.Count} real clips), {skipped} skipped.");
        return filled;
    }

    // Fisher-Yates -- a genuine per-run shuffle, not a fixed rotation, so re-running
    // this fills gaps with a different mix each time rather than the same pattern.
    private static List<AudioClip> Shuffle(List<AudioClip> source, System.Random rng)
    {
        var list = new List<AudioClip>(source);
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
        return list;
    }

    // Pool of REAL recordings already connected for any language/page -- clips longer
    // than 2 seconds are assumed to be actual voice-overs, not short placeholder stingers.
    // Falls back to any AudioClip in the project only if literally nothing has ever been
    // recorded yet, so a first-ever run still fills gaps instead of doing nothing.
    private static List<AudioClip> BuildPlaceholderPool()
    {
        var pool = new List<AudioClip>();
        foreach (string guid in AssetDatabase.FindAssets("t:ARPageAudioPack"))
        {
            var pack = AssetDatabase.LoadAssetAtPath<ARPageAudioPack>(AssetDatabase.GUIDToAssetPath(guid));
            if (pack?.voiceClips == null) continue;
            foreach (var seg in pack.voiceClips)
            {
                if (seg?.clip != null && seg.clip.length > 2f)
                    pool.Add(seg.clip);
            }
        }

        if (pool.Count > 0) return pool;

        Debug.LogWarning("[Audio Filler] No real recordings found in any language pack yet -- " +
                          "falling back to any AudioClip in the project as a one-time placeholder pool.");
        foreach (var guid in AssetDatabase.FindAssets("t:AudioClip"))
        {
            var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(AssetDatabase.GUIDToAssetPath(guid));
            if (clip != null) pool.Add(clip);
        }
        return pool;
    }
}
