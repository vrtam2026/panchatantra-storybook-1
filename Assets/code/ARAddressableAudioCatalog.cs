using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Maps (language + pageId) to an Addressable address string for the audio pack.
/// Does NOT hold direct AudioClip references — only string addresses.
/// Safe to keep local (small size). One entry per language per page.
/// Example address: "audio/English/S1_P-8-9"
/// </summary>
[CreateAssetMenu(menuName = "AR/Addressable Audio Catalog", fileName = "ARAddressableAudioCatalog")]
public class ARAddressableAudioCatalog : ScriptableObject
{
    [Serializable]
    public class Entry
    {
        public string languageName;
        public string pageId;
        [Tooltip("Addressable address for this audio pack. Example: audio/English/S1_P-8-9")]
        public string address;
    }

    [SerializeField] private List<Entry> entries = new();

    public bool TryGetAddress(string language, string pageId, out string address)
    {
        address = null;
        if (string.IsNullOrWhiteSpace(language) || string.IsNullOrWhiteSpace(pageId)) return false;

        string langKey = Normalize(language);
        string pageKey = Normalize(pageId);

        foreach (var entry in entries)
        {
            if (entry == null) continue;
            if (Normalize(entry.languageName) == langKey && Normalize(entry.pageId) == pageKey)
            {
                address = entry.address;
                return !string.IsNullOrWhiteSpace(address);
            }
        }
        return false;
    }

    public bool HasEntry(string language, string pageId) =>
        TryGetAddress(language, pageId, out _);

    public IReadOnlyList<Entry> GetAllEntries() => entries;

    public void AddEntry(string language, string pageId, string address)
    {
        entries.Add(new Entry { languageName = language, pageId = pageId, address = address });
    }

    private static string Normalize(string s) => (s ?? "").Trim().ToLowerInvariant();
}
