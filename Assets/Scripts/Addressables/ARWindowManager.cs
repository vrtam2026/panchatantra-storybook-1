using System;
using System.Collections.Generic;
using UnityEngine;

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

    private static readonly string[] Languages = { "English", "Hindi" };

    private CustomARHandler[] _cachedHandlers;

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
                    foreach (var lang in Languages)
                        audioService.PreloadAudioPack(lang, entry.pageId);
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
                    foreach (var lang in Languages)
                        audioService.ReleaseAudioPack(lang, entry.pageId);
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
