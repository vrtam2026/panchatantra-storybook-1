using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AddressableAssets;

/// <summary>
/// Maps (language + pageId) to the actual ARPageAudioPack asset — drag the audio
/// pack file directly into the entry, no address text ever needs to be typed.
/// Safe to keep local (small size). One entry per language per page.
/// </summary>
[CreateAssetMenu(menuName = "AR/Addressable Audio Catalog", fileName = "ARAddressableAudioCatalog")]
public class ARAddressableAudioCatalog : ScriptableObject
{
    [Serializable]
    public class Entry
    {
        public string languageName;
        public string pageId;
        [Tooltip("Drag the ARPageAudioPack asset for this page/language here directly.")]
        public AssetReferenceT<ARPageAudioPack> audioPack;
    }

    [SerializeField] private List<Entry> entries = new();

    public bool TryGetAudioPack(string language, string pageId, out AssetReferenceT<ARPageAudioPack> audioPack)
    {
        audioPack = null;
        if (string.IsNullOrWhiteSpace(language) || string.IsNullOrWhiteSpace(pageId)) return false;

        string langKey = Normalize(language);
        string pageKey = Normalize(pageId);

        foreach (var entry in entries)
        {
            if (entry == null) continue;
            if (Normalize(entry.languageName) == langKey && Normalize(entry.pageId) == pageKey)
            {
                // Skip broken/empty entries instead of stopping -- an old entry with a
                // missing pack reference must never hide a valid one added later.
                if (entry.audioPack != null && entry.audioPack.RuntimeKeyIsValid())
                {
                    audioPack = entry.audioPack;
                    return true;
                }
            }
        }
        return false;
    }

    public bool HasEntry(string language, string pageId) =>
        TryGetAudioPack(language, pageId, out _);

    public IReadOnlyList<Entry> GetAllEntries() => entries;

    public void AddEntry(string language, string pageId, AssetReferenceT<ARPageAudioPack> audioPack)
    {
        // If an entry for this language+page already exists (even a broken one with an
        // empty pack reference), repair it in place instead of adding a duplicate row.
        string langKey = Normalize(language);
        string pageKey = Normalize(pageId);
        foreach (var entry in entries)
        {
            if (entry == null) continue;
            if (Normalize(entry.languageName) == langKey && Normalize(entry.pageId) == pageKey)
            {
                entry.audioPack = audioPack;
                return;
            }
        }
        entries.Add(new Entry { languageName = language, pageId = pageId, audioPack = audioPack });
    }

    /// <summary>
    /// Removes every entry for one language (used when a language is removed from the
    /// project). Returns how many entries were removed. Does not delete any asset files.
    /// </summary>
    public int RemoveEntriesForLanguage(string language)
    {
        if (string.IsNullOrWhiteSpace(language)) return 0;
        string key = Normalize(language);
        return entries.RemoveAll(e => e != null && Normalize(e.languageName) == key);
    }

    /// <summary>
    /// Removes every entry whose audio pack is missing or broken, according to whatever
    /// check the caller passes in (the editor tool checks the file still exists on disk --
    /// this class stays runtime-safe and doesn't know about AssetDatabase itself).
    /// Called automatically every time "Connect Audio To Pages" runs, so deleting audio
    /// files by hand never leaves stale entries behind -- the next click cleans them up
    /// and rebuilds from whatever recordings currently exist.
    /// </summary>
    public int RemoveEntriesWhere(Func<Entry, bool> shouldRemove)
    {
        if (shouldRemove == null) return 0;
        return entries.RemoveAll(e => e == null || shouldRemove(e));
    }

    private static string Normalize(string s) => (s ?? "").Trim().ToLowerInvariant();
}
