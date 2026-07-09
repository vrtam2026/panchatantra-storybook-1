using System.Collections.Generic;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;

/// <summary>
/// Automatically syncs the Preload label from page prefab entries to their
/// matching audio pack entries, in every language's Audio_&lt;Language&gt; group
/// (however many languages the project has — nothing hardcoded).
///
/// HOW TO USE:
///   Runs automatically every time Addressables settings are saved — normally you never
///   need to do anything. To run it by hand: Tools → AR Storybook →
///   Sync Preload Labels For Audio.
/// </summary>
public static class AudioPreloadLabelSync
{
    private const string PreloadLabel = "Preload";
    private static bool _syncing;

    // ─── Manual menu trigger ─────────────────────────────────────────────────

    [MenuItem("Tools/AR Storybook/Sync Preload Labels For Audio", false, 15)]
    public static void SyncNowMenu()
    {
        SyncNow();
        Debug.Log("[AUDIO-SYNC] Manual sync triggered from Tools menu.");
    }

    // ─── Core sync logic ─────────────────────────────────────────────────────

    public static void SyncNow(AddressableAssetSettings s = null)
    {
        if (_syncing) return;
        if (s == null) s = AddressableAssetSettingsDefaultObject.Settings;
        if (s == null)
        {
            Debug.LogWarning("[AUDIO-SYNC] AddressableAssetSettings not found.");
            return;
        }

        _syncing = true;
        try
        {
            // Step 1: Build prefabAddressableKey → pageId map from ARWindowManagerEditor
            var pageMap = BuildPageMap();

            // Step 2: Collect every pageId whose prefab entry has the Preload label
            var preloadPageIds = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var group in s.groups)
            {
                if (group == null || group.ReadOnly) continue;
                // Skip audio groups — we only read FROM prefab groups here. Any group named
                // "Audio_<Language>" is an audio group, however many languages exist.
                if (IsAudioGroup(group.Name)) continue;

                foreach (var entry in group.entries)
                {
                    if (entry == null) continue;
                    if (!entry.labels.Contains(PreloadLabel)) continue;
                    if (pageMap.TryGetValue(entry.address, out string pageId) &&
                        !string.IsNullOrEmpty(pageId))
                    {
                        preloadPageIds.Add(pageId);
                        Debug.Log($"[AUDIO-SYNC] Prefab marked Preload: {entry.address} → pageId: {pageId}");
                    }
                }
            }

            // Step 3: Apply / remove Preload label on matching audio entries
            bool changed = false;
            foreach (var group in s.groups)
            {
                if (group == null) continue;
                if (!IsAudioGroup(group.Name)) continue;

                foreach (var entry in group.entries)
                {
                    if (entry == null) continue;

                    string pageId    = ExtractPageIdFromAddress(entry.address);
                    if (string.IsNullOrEmpty(pageId)) continue;

                    bool shouldHave  = preloadPageIds.Contains(pageId);
                    bool hasLabel    = entry.labels.Contains(PreloadLabel);

                    if (shouldHave == hasLabel) continue; // already correct

                    entry.SetLabel(PreloadLabel, shouldHave, true);
                    changed = true;

                    Debug.Log($"[AUDIO-SYNC] {(shouldHave ? "Added" : "Removed")} " +
                              $"Preload on [{group.Name}] {entry.address}");
                }
            }

            if (changed)
            {
                EditorUtility.SetDirty(s);
                AssetDatabase.SaveAssets();
                Debug.Log("[AUDIO-SYNC] Sync complete — audio Preload labels updated.");
            }
            else
            {
                Debug.Log("[AUDIO-SYNC] Sync complete — all audio labels already correct, nothing changed.");
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds prefabAddressableKey → pageId map using ARWindowManagerEditor's PopulatePages.
    /// Always in sync with the master page list — no manual maintenance needed.
    /// </summary>
    static Dictionary<string, string> BuildPageMap()
    {
        var map = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
        var go  = new GameObject("__AudioPreloadSync_Temp__");
        try
        {
            var mgr = go.AddComponent<ARWindowManager>();
            ARWindowManagerEditor.PopulatePages(mgr);
            foreach (var page in mgr.pages)
            {
                if (page == null || string.IsNullOrEmpty(page.addressableKey)) continue;
                map[page.addressableKey] = page.pageId ?? "";
            }
        }
        finally
        {
            Object.DestroyImmediate(go);
        }
        return map;
    }

    /// <summary>
    /// True for any Addressables group created for audio, e.g. "Audio_English",
    /// "Audio_Hindi", "Audio_Telugu" — whatever languages the project has, this
    /// matches all of them automatically since AudioAddressableSetupTool always
    /// names audio groups "Audio_&lt;Language&gt;".
    /// </summary>
    static bool IsAudioGroup(string groupName) =>
        !string.IsNullOrEmpty(groupName) && groupName.StartsWith("Audio_");

    /// <summary>
    /// Extracts pageId from an audio entry address.
    /// Expected format:  audio/English/S1_P-8-9  →  returns  S1_P-8-9
    /// </summary>
    static string ExtractPageIdFromAddress(string address)
    {
        if (string.IsNullOrEmpty(address)) return null;
        int lastSlash = address.LastIndexOf('/');
        return lastSlash >= 0 ? address.Substring(lastSlash + 1) : address;
    }
}

// ─── Auto-trigger when Addressables settings asset is saved ──────────────────

/// <summary>
/// Watches for saves to AddressableAssetSettings.asset.
/// Any time you make changes in the Addressables Groups window and Unity saves,
/// the sync runs automatically so audio labels stay in sync without any extra step.
/// </summary>
class AudioPreloadAutoSync : AssetPostprocessor
{
    static void OnPostprocessAllAssets(
        string[] importedAssets, string[] deletedAssets,
        string[] movedAssets,    string[] movedFromAssetPaths)
    {
        if (Application.isPlaying) return;
        foreach (var path in importedAssets)
        {
            if (path.Contains("AddressableAssetSettings") && path.EndsWith(".asset"))
            {
                var s = AddressableAssetSettingsDefaultObject.Settings;
                if (s != null)
                {
                    Debug.Log("[AUDIO-SYNC] Addressables settings changed — running auto-sync.");
                    AudioPreloadLabelSync.SyncNow(s);
                }
                return;
            }
        }
    }
}
