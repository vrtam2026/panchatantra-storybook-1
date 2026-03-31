using System;
using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(menuName = "AR/Audio Localization Database", fileName = "ARAudioLocalizationDatabase")]
public class ARAudioLocalizationDatabase : ScriptableObject
{
    [Serializable]
    public class AudioSegment
    {
        public AudioClip clip;
        [Min(0f)] public float delayBefore = 0f;
        [Min(0f)] public float delayAfter = 0f;
        [Range(0f, 2f)] public float volume = 1f;
        public bool loop = false;
    }

    [Serializable]
    public class PageAudio
    {
        public string pageId;
        public List<AudioSegment> voiceClips = new();
        public List<AudioSegment> bgmClips = new();
    }

    [Serializable]
    public class LanguagePack
    {
        public string languageName;
        public List<PageAudio> pages = new();
    }

    [SerializeField] private string defaultLanguage = "English";
    [SerializeField] private List<LanguagePack> languagePacks = new();

    public string DefaultLanguage => string.IsNullOrWhiteSpace(defaultLanguage) ? "English" : defaultLanguage;

    public bool TryGetPageAudio(string languageName, string pageId, out PageAudio pageAudio)
    {
        pageAudio = null;
        if (string.IsNullOrWhiteSpace(pageId)) return false;

        if (TryGetPageAudioInternal(languageName, pageId, out pageAudio)) return true;
        if (TryGetPageAudioInternal(DefaultLanguage, pageId, out pageAudio)) return true;
        return false;
    }

    private bool TryGetPageAudioInternal(string languageName, string pageId, out PageAudio pageAudio)
    {
        pageAudio = null;
        if (languagePacks == null || languagePacks.Count == 0) return false;

        string langKey = Normalize(languageName);
        string pageKey = Normalize(pageId);

        foreach (var pack in languagePacks)
        {
            if (pack == null) continue;
            if (Normalize(pack.languageName) != langKey) continue;

            foreach (var page in pack.pages)
            {
                if (page == null) continue;
                if (Normalize(page.pageId) == pageKey)
                {
                    pageAudio = page;
                    return true;
                }
            }
            return false;
        }
        return false;
    }

    private static string Normalize(string s) => (s ?? "").Trim().ToLowerInvariant();
}
