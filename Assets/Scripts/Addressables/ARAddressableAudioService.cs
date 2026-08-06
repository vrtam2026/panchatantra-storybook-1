using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

/// <summary>
/// Loads and caches ARPageAudioPack assets from Addressables.
/// Handles race conditions when language is switched quickly.
/// Does NOT release the page visual prefab — only manages audio handles.
/// Attach to any scene-persistent GameObject in the AR scene.
/// </summary>
public class ARAddressableAudioService : MonoBehaviour
{
    [SerializeField] private ARAddressableAudioCatalog catalog;

    // Cache key = "language:pageid" (both lowercased)
    private readonly Dictionary<string, AsyncOperationHandle<ARPageAudioPack>> _cache =
        new Dictionary<string, AsyncOperationHandle<ARPageAudioPack>>();

    public static ARAddressableAudioService Instance { get; private set; }

    // ---------------------------------------------------------------

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;

        foreach (var handle in _cache.Values)
        {
            if (handle.IsValid())
                Addressables.Release(handle);
        }
        _cache.Clear();
    }

    // ---------------------------------------------------------------
    // Public API
    // ---------------------------------------------------------------

    /// <summary>
    /// Load or return cached audio pack for the given language and page.
    /// Returns true if a load was started (async) or a cache hit was found (sync callback).
    /// Returns false if catalog is not configured or has no entry for this combination.
    ///
    /// requestId / getLatestRequestId: pass an incrementing int and a delegate that returns
    /// the CURRENT latest id. If stale (user switched language again), callback is suppressed.
    /// </summary>
    public bool LoadAudioPack(
        string language,
        string pageId,
        int requestId,
        Func<int> getLatestRequestId,
        Action<ARPageAudioPack, bool> onLoaded)
    {
        if (catalog == null)
        {
            Debug.LogWarning("[AR-AUDIO] ARAddressableAudioService: catalog is not assigned in Inspector.");
            return false;
        }

        if (!catalog.TryGetAudioPack(language, pageId, out var audioPackRef))
        {
            Debug.LogWarning($"[AR-AUDIO] No catalog entry for lang:'{language}' page:'{pageId}'");
            return false;
        }

        string cacheKey = MakeKey(language, pageId);

        // Cache hit (already downloaded)
        if (_cache.TryGetValue(cacheKey, out var cached) &&
            cached.IsValid() &&
            cached.IsDone &&
            cached.Status == AsyncOperationStatus.Succeeded)
        {
            Debug.Log($"[AR-AUDIO] Cache hit: {language} {pageId}");
            if (requestId == getLatestRequestId())
                onLoaded?.Invoke(cached.Result, true);
            return true;
        }

        // In-flight download already running — attach new callback instead of cancelling it.
        // Cancelling a download in progress would cause the first caller's callback to fire on
        // a released handle, silently dropping audio.
        if (_cache.TryGetValue(cacheKey, out var inFlight) && inFlight.IsValid() && !inFlight.IsDone)
        {
            Debug.Log($"[AR-AUDIO] Attaching to in-flight download: {language} {pageId}");
            inFlight.Completed += op =>
            {
                if (!op.IsValid()) return;
                if (op.Status == AsyncOperationStatus.Succeeded)
                {
                    if (requestId == getLatestRequestId())
                        onLoaded?.Invoke(op.Result, true);
                }
                else
                {
                    if (requestId == getLatestRequestId())
                        onLoaded?.Invoke(null, false);
                }
            };
            return true;
        }

        // Remove failed/stale completed handle
        if (_cache.TryGetValue(cacheKey, out var stale) && stale.IsValid())
        {
            Addressables.Release(stale);
            _cache.Remove(cacheKey);
        }

        Debug.Log($"[AR-AUDIO] Downloading audio pack: {language} {pageId}");

        var handle = audioPackRef.LoadAssetAsync<ARPageAudioPack>();
        _cache[cacheKey] = handle;

        handle.Completed += op =>
        {
            if (!op.IsValid()) return;

            if (op.Status == AsyncOperationStatus.Succeeded)
            {
                Debug.Log($"[AR-AUDIO] Audio pack ready: {language} {pageId}");
                if (requestId == getLatestRequestId())
                    onLoaded?.Invoke(op.Result, true);
            }
            else
            {
                Debug.LogError($"[AR-AUDIO] Failed to load audio pack: {language} {pageId} — {op.OperationException?.Message}");
                _cache.Remove(cacheKey);
                if (requestId == getLatestRequestId())
                    onLoaded?.Invoke(null, false);
            }
        };

        return true;
    }

    /// <summary>
    /// Warm the cache for a page without a callback.
    /// Call this when a page is first scanned so audio is ready when the page finishes loading.
    /// </summary>
    public void PreloadAudioPack(string language, string pageId)
    {
        if (catalog == null) return;
        if (!catalog.TryGetAudioPack(language, pageId, out var audioPackRef)) return;

        string cacheKey = MakeKey(language, pageId);
        if (_cache.ContainsKey(cacheKey)) return; // already loading or cached

        Debug.Log($"[AR-AUDIO] Preloading audio pack: {language} {pageId}");

        var handle = audioPackRef.LoadAssetAsync<ARPageAudioPack>();
        _cache[cacheKey] = handle;

        handle.Completed += op =>
        {
            if (!op.IsValid()) return;
            if (op.Status != AsyncOperationStatus.Succeeded)
            {
                Debug.LogWarning($"[AR-AUDIO] Preload failed: {language} {pageId}");
                _cache.Remove(cacheKey);
            }
            else
            {
                Debug.Log($"[AR-AUDIO] Preload complete: {language} {pageId}");
            }
        };
    }

    /// <summary>
    /// Every distinct language name currently present in the catalog. Always up to
    /// date -- add a language's audio via the Editor tool (which adds catalog
    /// entries) and it shows up here automatically on the next run, no code change.
    /// </summary>
    public List<string> GetAllLanguages()
    {
        var languages = new List<string>();
        if (catalog == null) return languages;

        foreach (var entry in catalog.GetAllEntries())
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.languageName)) continue;

            bool alreadyListed = false;
            foreach (var l in languages)
            {
                if (string.Equals(l, entry.languageName, StringComparison.OrdinalIgnoreCase))
                {
                    alreadyListed = true;
                    break;
                }
            }
            if (!alreadyListed) languages.Add(entry.languageName);
        }
        return languages;
    }

    public bool HasCachedPack(string language, string pageId)
    {
        string cacheKey = MakeKey(language, pageId);
        return _cache.TryGetValue(cacheKey, out var h) &&
               h.IsValid() &&
               h.IsDone &&
               h.Status == AsyncOperationStatus.Succeeded;
    }

    /// <summary>
    /// Releases the cached audio pack for the given language and page.
    /// Called by ARWindowManager when a page leaves the active window.
    /// </summary>
    public void ReleaseAudioPack(string language, string pageId)
    {
        string key = MakeKey(language, pageId);
        if (_cache.TryGetValue(key, out var handle))
        {
            // Release through the catalog's own AssetReference rather than the generic
            // Addressables.Release(handle). An AssetReference keeps its OWN internal
            // "already loaded" flag, and only its ReleaseAsset() clears that flag.
            // Releasing just the handle frees the asset but leaves the reference still
            // believing it is loaded, so the next LoadAssetAsync() on that same
            // reference throws "Attempting to load AssetReference that has already been
            // loaded" -- and that page stays silent when it is scanned again.
            if (handle.IsValid() &&
                catalog != null &&
                catalog.TryGetAudioPack(language, pageId, out var audioPackRef))
            {
                audioPackRef.ReleaseAsset();
            }
            else if (handle.IsValid())
            {
                // No catalog entry to release through (entry removed since load) --
                // fall back so the asset is still freed rather than leaked.
                Addressables.Release(handle);
            }

            _cache.Remove(key);
            Debug.Log($"[AR-AUDIO] Released from cache: {key}");
        }
    }

    // ---------------------------------------------------------------
    // Diagnostics
    // ---------------------------------------------------------------

    public class DownloadItem
    {
        public string Key;      // "english:s1_p-4-5"
        public float  Progress; // 0.0 → 1.0
    }

    public struct CacheInfo
    {
        public List<string>       Cached;      // downloaded and ready in memory
        public List<DownloadItem> Downloading; // currently in-flight, with progress
    }

    public enum PackState { NotLoaded, Downloading, Cached, Failed }

    public class PackStatus
    {
        public string    Language;
        public string    PageId;
        public string    Address;
        public PackState State;
        public float     Progress; // only meaningful when Downloading
    }

    /// <summary>
    /// Returns the full status of every entry in the catalog — Cached, Downloading, NotLoaded, or Failed.
    /// Used by the diagnostic overlay to show the complete picture.
    /// </summary>
    public List<PackStatus> GetFullStatus()
    {
        var result = new List<PackStatus>();
        if (catalog == null) return result;

        foreach (var entry in catalog.GetAllEntries())
        {
            if (entry == null) continue;
            string key = MakeKey(entry.languageName, entry.pageId);
            var status = new PackStatus
            {
                Language = entry.languageName,
                PageId   = entry.pageId,
                Address  = entry.audioPack != null ? entry.audioPack.AssetGUID : "",
                State    = PackState.NotLoaded,
                Progress = 0f
            };

            if (_cache.TryGetValue(key, out var handle) && handle.IsValid())
            {
                if (handle.IsDone)
                    status.State = handle.Status == AsyncOperationStatus.Succeeded
                                    ? PackState.Cached : PackState.Failed;
                else
                {
                    status.State    = PackState.Downloading;
                    status.Progress = handle.PercentComplete;
                }
            }

            result.Add(status);
        }
        return result;
    }

    /// <summary>
    /// Returns a snapshot of the audio cache state for the diagnostic overlay.
    /// </summary>
    public CacheInfo GetCacheInfo()
    {
        var info = new CacheInfo
        {
            Cached      = new List<string>(),
            Downloading = new List<DownloadItem>()
        };

        foreach (var kv in _cache)
        {
            if (!kv.Value.IsValid()) continue;
            if (kv.Value.IsDone && kv.Value.Status == AsyncOperationStatus.Succeeded)
                info.Cached.Add(kv.Key);
            else if (!kv.Value.IsDone)
                info.Downloading.Add(new DownloadItem
                {
                    Key      = kv.Key,
                    Progress = kv.Value.PercentComplete
                });
        }

        return info;
    }

    // ---------------------------------------------------------------

    private static string MakeKey(string language, string pageId) =>
        $"{(language ?? "").Trim().ToLower()}:{(pageId ?? "").Trim().ToLower()}";
}
