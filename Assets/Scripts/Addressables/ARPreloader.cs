using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

/// <summary>
/// Runs at app start and downloads all assets tagged "Preload" from CCD.
/// This includes:
///   1. Page prefab dependencies (visuals)
///   2. Audio packs for the same pages in every language the catalog knows about
///
/// Audio preload is automatic — no manual setup needed.
/// Simply mark a prefab as Preload in Addressables → its audio is preloaded too.
/// The AudioPreloadLabelSync editor script keeps audio Preload labels in sync automatically.
/// </summary>
public class ARPreloader : MonoBehaviour
{
    [Tooltip("The Addressables label used to tag assets that should download at app start.")]
    public string labelToPreload = "Preload";

    // ─────────────────────────────────────────────────────────────────────────

    void Start()
    {
        StartCoroutine(PreloadAll());
    }

    IEnumerator PreloadAll()
    {
        // ── Step 1: Download prefab visual dependencies ───────────────────────
        Debug.Log("[AR-PRELOAD] Starting prefab download for label: " + labelToPreload);

        var prefabHandle = Addressables.DownloadDependenciesAsync(labelToPreload);
        yield return prefabHandle;

        if (prefabHandle.Status == AsyncOperationStatus.Succeeded)
            Debug.Log("[AR-PRELOAD] Prefab dependencies cached and ready.");
        else
            Debug.LogWarning("[AR-PRELOAD] Prefab preload failed: " +
                             prefabHandle.OperationException?.Message);

        Addressables.Release(prefabHandle);

        // ── Step 2: Find which pages are tagged Preload ───────────────────────
        // We load resource locations to get the primary keys (addressableKey) of
        // every Preload-tagged entry. From there we find the matching pageId and
        // preload audio for all languages.

        var locHandle = Addressables.LoadResourceLocationsAsync(labelToPreload);
        yield return locHandle;

        if (locHandle.Status != AsyncOperationStatus.Succeeded)
        {
            Debug.LogWarning("[AR-PRELOAD] Could not read Preload resource locations. " +
                             "Audio preload skipped.");
            Addressables.Release(locHandle);
            yield break;
        }

        // ── Step 3: Preload audio for each Preload page ───────────────────────

        var audioService   = ARAddressableAudioService.Instance;
        var windowManager  = ARWindowManager.Instance;

        if (audioService == null)
        {
            Debug.LogWarning("[AR-PRELOAD] ARAddressableAudioService not found in scene. " +
                             "Audio preload skipped.");
            Addressables.Release(locHandle);
            yield break;
        }

        if (windowManager == null || windowManager.pages == null || windowManager.pages.Count == 0)
        {
            Debug.LogWarning("[AR-PRELOAD] ARWindowManager not ready. Audio preload skipped.");
            Addressables.Release(locHandle);
            yield break;
        }

        var languages = audioService.GetAllLanguages();

        // Build addressableKey → pageId lookup from ARWindowManager's page list
        var pageMap = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var page in windowManager.pages)
        {
            if (page == null || string.IsNullOrEmpty(page.addressableKey)) continue;
            pageMap[page.addressableKey] = page.pageId ?? "";
        }

        int audioQueued = 0;
        foreach (var location in locHandle.Result)
        {
            string addressableKey = location.PrimaryKey;

            if (!pageMap.TryGetValue(addressableKey, out string pageId)) continue;
            if (string.IsNullOrEmpty(pageId)) continue; // quiz page — no audio

            foreach (var lang in languages)
            {
                audioService.PreloadAudioPack(lang, pageId);
                audioQueued++;
                Debug.Log($"[AR-PRELOAD] Queued audio: [{lang}] {pageId}");
            }
        }

        Addressables.Release(locHandle);

        if (audioQueued == 0)
            Debug.Log("[AR-PRELOAD] No audio packs to preload (no Preload pages have audio).");
        else
            Debug.Log($"[AR-PRELOAD] Audio preload queued for {audioQueued} packs " +
                      $"({audioQueued / Mathf.Max(1, languages.Count)} pages × {languages.Count} languages).");
    }
}
