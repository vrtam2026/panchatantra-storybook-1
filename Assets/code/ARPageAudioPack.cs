using System;
using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(menuName = "AR/Page Audio Pack", fileName = "ARPageAudioPack")]
public class ARPageAudioPack : ScriptableObject
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

    [Header("Identity")]
    public string languageName;
    public string pageId;

    [Header("Voice Narration")]
    public List<AudioSegment> voiceClips = new();

    [Header("Background Music")]
    public List<AudioSegment> bgmClips = new();

    // Future: activity audio by string ID
    // public List<ActivityAudioEntry> activityClips = new();
}
