using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

/// <summary>
/// Sliding window memory manager.
/// Keeps current page ± windowSize pages loaded. Everything outside is released.
/// List is auto-populated by ARWindowManagerEditor when you click ARWindowManager in Inspector.
/// </summary>
public class ARWindowManager : MonoBehaviour
{
    public static ARWindowManager Instance { get; private set; }

    [Serializable]
    public class PageEntry
    {
        [Tooltip("Addressable key of the page prefab. Must match addressableKey on CustomARHandler.")]
        public string addressableKey;
        [Tooltip("Audio catalog page ID. Empty for quiz pages which have no audio.")]
        public string pageId;
    }

    [Header("Window Settings")]
    [Tooltip("How many pages before and after the current page to keep in memory.\n" +
             "windowSize=2 → keeps current page ±2 = 5 pages total.")]
    public int windowSize = 2;

    [Tooltip("ON  = pages outside window have their prefab AND audio silently released.\n" +
             "OFF = pages manage their own lifecycle naturally (no forced release).")]
    public bool releaseContentOutsideWindow = true;

    [Header("All Pages  (auto-populated — do not edit manually)")]
    public List<PageEntry> pages = new List<PageEntry>();

    // ─────────────────────────────────────────────────────────────────────────

    private CustomARHandler[] _cachedHandlers;
    private readonly Dictionary<string, AsyncOperationHandle> _prefetchHandles = new Dictionary<string, AsyncOperationHandle>();
    private readonly HashSet<string> _pendingPrefetchRelease = new HashSet<string>();

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    void Start()
    {
        _cachedHandlers = FindObjectsByType<CustomARHandler>(FindObjectsSortMode.None);
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;

        // Release any outstanding visual prefetch handles so they don't leak if this
        // component is ever destroyed independently of a full scene unload.
        foreach (var handle in _prefetchHandles.Values)
            Addressables.Release(handle);
        _prefetchHandles.Clear();
        _pendingPrefetchRelease.Clear();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Called by CustomARHandler.OnTrackingFound
    // ─────────────────────────────────────────────────────────────────────────

    public void OnPageDetected(string addressableKey)
    {
        if (!releaseContentOutsideWindow) return;
        if (pages == null || pages.Count == 0) return;

        int index = FindIndex(addressableKey);
        if (index < 0)
        {
            Debug.LogWarning($"[AR-WINDOW] Page not found in list: '{addressableKey}'. " +
                             "Re-run ARWindowManager setup from Inspector.");
            return;
        }

        int from = index - windowSize;
        int to   = index + windowSize;

        var audioService = ARAddressableAudioService.Instance;
        var languages = audioService != null ? audioService.GetAllLanguages() : new List<string>();

        var allHandlers = _cachedHandlers;

        for (int i = 0; i < pages.Count; i++)
        {
            var entry = pages[i];
            if (entry == null || string.IsNullOrEmpty(entry.addressableKey)) continue;

            bool inWindow = i >= from && i <= to;

            if (inWindow)
            {
                // Pre-download audio for neighbours so it is ready before they are scanned
                if (audioService != null && !string.IsNullOrEmpty(entry.pageId))
                    foreach (var lang in languages)
                        audioService.PreloadAudioPack(lang, entry.pageId);

                // Pre-download the visual page content in the background (no instantiate)
                if (!_prefetchHandles.ContainsKey(entry.addressableKey))
                {
                    string prefetchKey = entry.addressableKey;
                    var handle = Addressables.DownloadDependenciesAsync(prefetchKey, false);
                    _prefetchHandles[prefetchKey] = handle;
                    Debug.Log($"[AR-WINDOW] Visual prefetch started: '{prefetchKey}'");

                    handle.Completed += op =>
                    {
                        if (op.Status == AsyncOperationStatus.Succeeded)
                            Debug.Log($"[AR-WINDOW] Visual prefetch ready: '{prefetchKey}'");
                        else
                            Debug.LogWarning($"[AR-WINDOW] Visual prefetch FAILED: '{prefetchKey}' — " +
                                              $"{op.OperationException?.Message}");

                        // If the page fell out of the window before this finished, release it now.
                        if (_pendingPrefetchRelease.Remove(prefetchKey))
                        {
                            Addressables.Release(op);
                            _prefetchHandles.Remove(prefetchKey);
                        }
                    };
                }
                else
                {
                    // Page re-entered the window before its pending release fired — keep it.
                    _pendingPrefetchRelease.Remove(entry.addressableKey);
                }
            }
            else
            {
                // Silently release prefab via its CustomARHandler
                foreach (var handler in allHandlers)
                {
                    if (string.Equals(handler.addressableKey.Trim(),
                                      entry.addressableKey.Trim(),
                                      StringComparison.OrdinalIgnoreCase))
                    {
                        handler.ForceRelease();
                        break;
                    }
                }

                // Release audio for all languages
                if (audioService != null && !string.IsNullOrEmpty(entry.pageId))
                    foreach (var lang in languages)
                        audioService.ReleaseAudioPack(lang, entry.pageId);

                // Release the pre-downloaded visual content for this page.
                // If its download is still in flight, don't cancel it — just mark it
                // for release once the Completed callback above fires.
                if (_prefetchHandles.TryGetValue(entry.addressableKey, out var prefetchHandle))
                {
                    if (prefetchHandle.IsDone)
                    {
                        Addressables.Release(prefetchHandle);
                        _prefetchHandles.Remove(entry.addressableKey);
                    }
                    else
                    {
                        _pendingPrefetchRelease.Add(entry.addressableKey);
                    }
                }
            }
        }

        Debug.Log($"[AR-WINDOW] Detected '{addressableKey}' (index {index}). " +
                  $"Window: {Mathf.Max(0, from)}–{Mathf.Min(pages.Count - 1, to)}");
    }

    // ─────────────────────────────────────────────────────────────────────────

    int FindIndex(string addressableKey)
    {
        string key = (addressableKey ?? "").Trim();
        for (int i = 0; i < pages.Count; i++)
        {
            if (pages[i] == null) continue;
            if (string.Equals(pages[i].addressableKey.Trim(), key,
                              StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }
}
