using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Assigns one language's voice clips to ANOTHER language's packs in SHUFFLED order,
/// so each target-language page plays a DIFFERENT source-language page's voice. Lets
/// you verify language switching works without silence, while making it obvious you're
/// hearing placeholder audio (wrong story content = easy to spot). Works for any two
/// languages, not just English → Hindi.
///
/// Packs that already have REAL audio in the target language are skipped — you won't
/// lose real recordings.
///
/// Run from: click a Language asset in the Project window → its Inspector has the button.
/// </summary>
public static class ARLanguageVoiceShuffler
{
    public static void Run(string targetLanguage, string sourceLanguage = null)
    {
        if (string.IsNullOrWhiteSpace(targetLanguage))
        {
            EditorUtility.DisplayDialog("Error", "No target language given.", "OK");
            return;
        }

        // Default to English if it has packs; otherwise fall back to whatever OTHER
        // language already has the most recordings, so this still works on a project
        // that was set up entirely without an English folder.
        if (string.IsNullOrWhiteSpace(sourceLanguage))
        {
            sourceLanguage = PickDefaultSourceLanguage(targetLanguage);
            if (sourceLanguage == null)
            {
                EditorUtility.DisplayDialog("Error",
                    "No language has any recorded ARPageAudioPack assets yet to shuffle from. " +
                    "Record real audio for at least one language first.", "OK");
                return;
            }
        }

        string sourceDir = $"Assets/code/AudioPacks/{sourceLanguage}";
        string targetDir = $"Assets/code/AudioPacks/{targetLanguage}";

        // ── Load all source-language packs ────────────────────────────────────
        string[] sourceGuids = AssetDatabase.FindAssets("t:ARPageAudioPack", new[] { sourceDir });
        if (sourceGuids.Length == 0)
        {
            EditorUtility.DisplayDialog("Error",
                $"No {sourceLanguage} ARPageAudioPack assets found in:\n{sourceDir}", "OK");
            return;
        }

        var sourcePacks = new List<ARPageAudioPack>();
        foreach (var g in sourceGuids)
        {
            var p = AssetDatabase.LoadAssetAtPath<ARPageAudioPack>(
                        AssetDatabase.GUIDToAssetPath(g));
            if (p != null) sourcePacks.Add(p);
        }

        // ── Load all target-language packs ────────────────────────────────────
        string[] targetGuids = AssetDatabase.FindAssets("t:ARPageAudioPack", new[] { targetDir });
        if (targetGuids.Length == 0)
        {
            EditorUtility.DisplayDialog("Error",
                $"No {targetLanguage} ARPageAudioPack assets found in:\n{targetDir}\n\n" +
                "Click \"Find & Connect All Audio Automatically\" first, or add at least one page for this language.",
                "OK");
            return;
        }

        var targetPacks = new List<ARPageAudioPack>();
        foreach (var g in targetGuids)
        {
            var p = AssetDatabase.LoadAssetAtPath<ARPageAudioPack>(
                        AssetDatabase.GUIDToAssetPath(g));
            if (p != null) targetPacks.Add(p);
        }

        // ── Confirm ───────────────────────────────────────────────────────────
        bool proceed = EditorUtility.DisplayDialog(
            $"Assign Shuffled {sourceLanguage} Voices to {targetLanguage}",
            $"Found:\n  {sourcePacks.Count} {sourceLanguage} packs\n  {targetPacks.Count} {targetLanguage} packs\n\n" +
            $"Each {targetLanguage} page will get a DIFFERENT {sourceLanguage} page's voice clips (shuffled).\n" +
            "BGM clips are also copied from the matching page.\n\n" +
            $"{targetLanguage} packs that already have REAL (non-placeholder) audio will be skipped.\n\n" +
            "Proceed?",
            "Yes, Shuffle & Assign", "Cancel");

        if (!proceed) return;

        // ── Build a genuinely random assignment ───────────────────────────────
        // A real shuffle (Fisher-Yates) every time this runs -- not a fixed formula,
        // so it gives a different mix on each click, not the same rotation every time.
        var sourceOrder = Enumerable.Range(0, sourcePacks.Count).ToList();
        var rng = new System.Random();
        for (int i = sourceOrder.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (sourceOrder[i], sourceOrder[j]) = (sourceOrder[j], sourceOrder[i]);
        }

        int filled  = 0;
        int skipped = 0;

        EditorUtility.DisplayProgressBar($"Shuffling {targetLanguage} Packs", "Starting...", 0f);

        try
        {
            for (int i = 0; i < targetPacks.Count; i++)
            {
                var target = targetPacks[i];
                float progress = (float)i / targetPacks.Count;
                EditorUtility.DisplayProgressBar($"Shuffling {targetLanguage} Packs",
                    $"{target.pageId}...", progress);

                // Skip if this pack already has REAL audio (heuristic: any clip longer
                // than 2 seconds is assumed to be a real recording, not a short placeholder).
                bool hasRealAudio = HasRealAudio(target);
                if (hasRealAudio)
                {
                    skipped++;
                    continue;
                }

                // Pick a random source pack from the shuffled order (wrap around if the
                // target list is longer than the source list)
                var src = sourcePacks[sourceOrder[i % sourceOrder.Count]];

                // Copy voice clips from the randomly-picked source pack
                target.voiceClips = CopySegments(src.voiceClips);

                // BGM comes from the same randomly-picked source pack too, so voice and
                // background music always belong to the same page's recording
                target.bgmClips = CopySegments(src.bgmClips);

                EditorUtility.SetDirty(target);
                filled++;
            }

            AssetDatabase.SaveAssets();
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        EditorUtility.DisplayDialog("Done",
            $"Assigned: {filled} {targetLanguage} packs (randomly shuffled)\n" +
            $"Skipped:  {skipped} packs (had real {targetLanguage} audio)\n\n" +
            "Now rebuild Addressables and upload to CCD to test.",
            "OK");

        Debug.Log($"[{targetLanguage} Shuffler] Done — {filled} assigned, {skipped} skipped.");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    // English if it has any packs; otherwise the language (other than the target) with
    // the most ARPageAudioPack assets on disk -- works for a project set up without an
    // English folder at all instead of always assuming one exists.
    static string PickDefaultSourceLanguage(string targetLanguage)
    {
        if (AssetDatabase.FindAssets("t:ARPageAudioPack", new[] { "Assets/code/AudioPacks/English" }).Length > 0)
            return "English";

        string[] langDirs = System.IO.Directory.Exists("Assets/code/AudioPacks")
            ? System.IO.Directory.GetDirectories("Assets/code/AudioPacks")
            : new string[0];

        string best = null;
        int bestCount = 0;
        foreach (string dir in langDirs)
        {
            string lang = System.IO.Path.GetFileName(dir);
            if (string.Equals(lang, targetLanguage, System.StringComparison.OrdinalIgnoreCase)) continue;

            int count = AssetDatabase.FindAssets("t:ARPageAudioPack", new[] { dir }).Length;
            if (count > bestCount)
            {
                bestCount = count;
                best = lang;
            }
        }
        return best;
    }

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
