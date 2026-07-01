using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Fills all Hindi ARPageAudioPack assets with a chosen placeholder AudioClip
/// so you can test language switching across all pages before real Hindi audio arrives.
///
/// Menu: Tools → AR Storybook → Fill Hindi Packs with Placeholder Audio
/// </summary>
public static class ARHindiPlaceholderFiller
{
    private const string HindiPackDir = "Assets/code/AudioPacks/Hindi";

    [MenuItem("Tools/AR Storybook/Fill Hindi Packs with Placeholder Audio")]
    public static void Run()
    {
        // Step 1: pick a placeholder clip from the project
        string[] clipGuids = AssetDatabase.FindAssets("t:AudioClip");
        if (clipGuids.Length == 0)
        {
            EditorUtility.DisplayDialog("Error", "No AudioClip assets found in the project.", "OK");
            return;
        }

        // Try to find the BGM clip first as a good placeholder (it loops, always audible)
        AudioClip placeholder = null;
        foreach (var guid in clipGuids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            // Prefer something called "bgm" or "loop" so it's clearly a placeholder
            string lower = path.ToLower();
            if (lower.Contains("bgm") || lower.Contains("loop") || lower.Contains("background"))
            {
                placeholder = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
                if (placeholder != null) break;
            }
        }

        // Fallback: just use the first clip found
        if (placeholder == null)
            placeholder = AssetDatabase.LoadAssetAtPath<AudioClip>(
                AssetDatabase.GUIDToAssetPath(clipGuids[0]));

        if (placeholder == null)
        {
            EditorUtility.DisplayDialog("Error", "Could not load any AudioClip as placeholder.", "OK");
            return;
        }

        // Step 2: confirm with the user
        bool proceed = EditorUtility.DisplayDialog(
            "Fill Hindi Packs",
            $"Placeholder clip selected:\n\"{placeholder.name}\"\n\n" +
            $"This will assign this clip as a voice segment in ALL Hindi packs that currently have empty (null) clips.\n\n" +
            $"Packs that already have real Hindi audio will NOT be overwritten.\n\n" +
            $"Proceed?",
            "Yes, Fill", "Cancel");

        if (!proceed) return;

        // Step 3: find all Hindi packs
        string[] packGuids = AssetDatabase.FindAssets("t:ARPageAudioPack", new[] { HindiPackDir });
        if (packGuids.Length == 0)
        {
            EditorUtility.DisplayDialog("Error",
                $"No ARPageAudioPack assets found in:\n{HindiPackDir}\n\nRun the migration tool first.",
                "OK");
            return;
        }

        int filled  = 0;
        int skipped = 0;

        EditorUtility.DisplayProgressBar("Filling Hindi Packs", "Starting...", 0f);

        try
        {
            for (int i = 0; i < packGuids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(packGuids[i]);
                var pack = AssetDatabase.LoadAssetAtPath<ARPageAudioPack>(path);
                if (pack == null) continue;

                float progress = (float)i / packGuids.Length;
                EditorUtility.DisplayProgressBar("Filling Hindi Packs", $"{pack.pageId}...", progress);

                bool dirty = false;

                // Fill voice clips — only if all are null (don't overwrite real data)
                bool allVoiceNull = pack.voiceClips == null || pack.voiceClips.Count == 0 ||
                                    pack.voiceClips.TrueForAll(s => s.clip == null);
                if (allVoiceNull)
                {
                    if (pack.voiceClips == null || pack.voiceClips.Count == 0)
                    {
                        pack.voiceClips = new System.Collections.Generic.List<ARPageAudioPack.AudioSegment>
                        {
                            new ARPageAudioPack.AudioSegment { clip = placeholder, volume = 1f, loop = false }
                        };
                    }
                    else
                    {
                        foreach (var seg in pack.voiceClips)
                            if (seg.clip == null) seg.clip = placeholder;
                    }
                    dirty = true;
                }
                else
                {
                    skipped++;
                    continue;
                }

                // Fill BGM clips if empty
                bool allBgmNull = pack.bgmClips == null || pack.bgmClips.Count == 0 ||
                                  pack.bgmClips.TrueForAll(s => s.clip == null);
                if (allBgmNull)
                {
                    if (pack.bgmClips == null || pack.bgmClips.Count == 0)
                    {
                        pack.bgmClips = new System.Collections.Generic.List<ARPageAudioPack.AudioSegment>
                        {
                            new ARPageAudioPack.AudioSegment { clip = placeholder, volume = 0.5f, loop = true }
                        };
                    }
                    else
                    {
                        foreach (var seg in pack.bgmClips)
                            if (seg.clip == null) seg.clip = placeholder;
                    }
                    dirty = true;
                }

                if (dirty)
                {
                    EditorUtility.SetDirty(pack);
                    filled++;
                }
            }

            AssetDatabase.SaveAssets();
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        EditorUtility.DisplayDialog("Done",
            $"Placeholder: \"{placeholder.name}\"\n\n" +
            $"Filled:   {filled} Hindi packs\n" +
            $"Skipped:  {skipped} packs (already had real audio)\n\n" +
            $"Now rebuild Addressables and upload to CCD to test.",
            "OK");

        Debug.Log($"[Hindi Filler] Done — {filled} filled, {skipped} skipped. Clip: {placeholder.name}");
    }
}
