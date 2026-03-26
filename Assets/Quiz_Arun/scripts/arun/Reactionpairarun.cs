using UnityEngine;
using UnityEngine.Video;

// ─────────────────────────────────────────────────────────────────────────────
// ReactionPairArun.cs
// One matched pair: a VO audio clip + its corresponding sage video.
// Used in QuizManagerArun's correctPairs and wrongPairs lists.
// NOT a MonoBehaviour — never add as a component.
// ─────────────────────────────────────────────────────────────────────────────

[System.Serializable]
public class ReactionPairArun
{
    [Tooltip("The voice over audio clip for this reaction.")]
    public AudioClip audio;

    [Tooltip("The sage video that matches this voice over (green screen).")]
    public VideoClip video;
}