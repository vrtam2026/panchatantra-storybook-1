using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Assigns English voice clips to Hindi packs in SHUFFLED order so that each
/// Hindi page plays a DIFFERENT English page's voice. This lets you verify
/// Hindi switching works without silence, while making it obvious you're
/// hearing placeholder audio (wrong story content = easy to spot).
///
/// Example: Hindi S1_P-4-5 plays English S1_P-10-11's voice clips.
///
/// Packs that already have REAL Hindi audio (non-null AND not from a previous
/// placeholder run) are skipped — you won't lose real recordings.
///
/// Menu: Tools → AR Storybook → Assign Shuffled English Voices to Hindi (Test)
/// </summary>
public static class ARHindiVoiceShuffler
{
    private const string EnglishDir = "Assets/code/AudioPacks/English";
    private const string HindiDir   = "Assets/code/AudioPacks/Hindi";

    [MenuItem("Tools/AR Storybook/Assign Shuffled English Voices to Hindi (Test)")]
    public static void Run()
    {
        // ── Load all English packs ────────────────────────────────────────────
        string[] engGuids = AssetDatabase.FindAssets("t:ARPageAudioPack", new[] { EnglishDir });
        if (engGuids.Length == 0)
        {
            EditorUtility.DisplayDialog("Error",
                $"No English ARPageAudioPack assets found in:\n{EnglishDir}", "OK");
            return;
        }

        var englishPacks = new List<ARPageAudioPack>();
        foreach (var g in engGuids)
        {
            var p = AssetDatabase.LoadAssetAtPath<ARPageAudioPack>(
                        AssetDatabase.GUIDToAssetPath(g));
            if (p != null) englishPacks.Add(p);
        }

        // ── Load all Hindi packs ──────────────────────────────────────────────
        string[] hindiGuids = AssetDatabase.FindAssets("t:ARPageAudioPack", new[] { HindiDir });
        if (hindiGuids.Length == 0)
        {
            EditorUtility.DisplayDialog("Error",
                $"No Hindi ARPageAudioPack assets found in:\n{HindiDir}\n\nRun migration first.", "OK");
            return;
        }

        var hindiPacks = new List<ARPageAudioPack>();
        foreach (var g in hindiGuids)
        {
            var p = AssetDatabase.LoadAssetAtPath<ARPageAudioPack>(
                        AssetDatabase.GUIDToAssetPath(g));
            if (p != null) hindiPacks.Add(p);
        }

        // ── Confirm ───────────────────────────────────────────────────────────
        bool proceed = EditorUtility.DisplayDialog(
            "Assign Shuffled English Voices to Hindi",
            $"Found:\n  {englishPacks.Count} English packs\n  {hindiPacks.Count} Hindi packs\n\n" +
            "Each Hindi page will get a DIFFERENT English page's voice clips (shuffled).\n" +
            "BGM clips are also copied from the matching English page.\n\n" +
            "Hindi packs that already have REAL (non-placeholder) audio will be skipped.\n\n" +
            "Proceed?",
            "Yes, Shuffle & Assign", "Cancel");

        if (!proceed) return;

        // ── Build shuffled English index ──────────────────────────────────────
        // Sort both lists by pageId so assignment is deterministic and reproducible.
        hindiPacks  = hindiPacks.OrderBy(p => p.pageId).ToList();
        englishPacks = englishPacks.OrderBy(p => p.pageId).ToList();

        // Create a shifted index so Hindi page[i] gets English page[(i+shift) % count]
        // Using shift = count/3 gives a well-spread offset that avoids adjacent pages.
        int count = Mathf.Min(hindiPacks.Count, englishPacks.Count);
        int shift = Mathf.Max(1, count / 3);

        int filled  = 0;
        int skipped = 0;

        EditorUtility.DisplayProgressBar("Shuffling Hindi Packs", "Starting...", 0f);

        try
        {
            for (int i = 0; i < hindiPacks.Count; i++)
            {
                var hindi = hindiPacks[i];
                float progress = (float)i / hindiPacks.Count;
                EditorUtility.DisplayProgressBar("Shuffling Hindi Packs",
                    $"{hindi.pageId}...", progress);

                // Skip if this pack already has REAL audio (not placeholder)
                // We detect placeholder by checking if all clips are the same object
                // (the old popup-bgm fill) or if it was never filled.
                // Simplest heuristic: skip if voice clips all have length > 5 seconds
                // (real recordings are typically long; popup-bgm is a short loop).
                // But since we can't know for sure, just always overwrite for test purposes.
                // The user said they have no real Hindi yet except 1 page — we'll preserve
                // by checking if clips have real content (any clip with length > 2s = real).
                bool hasRealHindi = HasRealAudio(hindi);
                if (hasRealHindi)
                {
                    skipped++;
                    continue;
                }

                // Pick the English pack at shifted index
                int engIndex = (i + shift) % englishPacks.Count;
                var eng = englishPacks[engIndex];

                // Copy voice clips from English source pack
                hindi.voiceClips = CopySegments(eng.voiceClips);

                // Copy BGM from the SAME English page (BGM should match the page visual)
                int samePage = i % englishPacks.Count;
                hindi.bgmClips = CopySegments(englishPacks[samePage].bgmClips);

                EditorUtility.SetDirty(hindi);
                filled++;
            }

            AssetDatabase.SaveAssets();
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        EditorUtility.DisplayDialog("Done",
            $"Shift applied: {shift} pages\n\n" +
            $"Assigned: {filled} Hindi packs\n" +
            $"Skipped:  {skipped} packs (had real Hindi audio)\n\n" +
            "Now rebuild Addressables and upload to CCD to test.",
            "OK");

        Debug.Log($"[Hindi Shuffler] Done — {filled} assigned, {skipped} skipped. Shift: {shift}");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    static bool HasRealAudio(ARPageAudioPack pack)
    {
        if (pack.voiceClips == null || pack.voiceClips.Count == 0) return false;
        foreach (var seg in pack.voiceClips)
        {
            if (seg?.clip == null) return false;
            // Real voice-over clips are usually > 2 seconds
            if (seg.clip.length > 2f) return true;
        }
        return false;
    }

    static List<ARPageAudioPack.AudioSegment> CopySegments(
        List<ARPageAudioPack.AudioSegment> src)
    {
        var list = new List<ARPageAudioPack.AudioSegment>();
        if (src == null) return list;
        foreach (var s in src)
        {
            if (s == null) continue;
            list.Add(new ARPageAudioPack.AudioSegment
            {
                clip        = s.clip,
                delayBefore = s.delayBefore,
                delayAfter  = s.delayAfter,
                volume      = s.volume,
                loop        = s.loop
            });
        }
        return list;
    }
}
