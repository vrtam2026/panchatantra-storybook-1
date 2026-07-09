using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEngine;
using UnityEngine.AddressableAssets;

/// <summary>
/// One-click tool to find and connect all page audio, wherever it already lives.
///
/// THE SIMPLE RULE: put a language's recordings in a folder named after that
/// language -- Assets/Audio/English/, Assets/Audio/Hindi/, Assets/Audio/Tamil/,
/// however many you need. Name each file with its page range (e.g. "45-46.mp3")
/// or in page order (e.g. "page1.mp3", "page2.mp3" ...). Click this tool. Done.
///
/// It ALSO still scans Assets/Story_Assets/.../Story-N/... folders, for
/// recordings that already live in the older, messier layout -- same matching
/// rules apply there too, so nothing needs to be moved or renamed to work.
///
/// Self-healing: every run first removes any catalog entry pointing at audio
/// that no longer exists (deleted by hand), then rebuilds from whatever
/// recordings actually exist right now. Delete one page's audio, delete a
/// whole language, delete everything -- one click always fully rebuilds from
/// wherever things currently stand. Never touches a page that already has
/// working audio.
///
/// Menu: Tools → AR Storybook → Connect Audio To Pages
/// </summary>
public static class AudioAddressableSetupTool
{
    private const string AudioRootFolder    = "Assets/Audio";
    private const string StoryAssetsRoot    = "Assets/Story_Assets";
    private const string PackOutputFolder   = "Assets/code/AudioPacks";
    private const string CatalogAssetPath   = "Assets/code/AudioLanguageCatalog.asset";
    private const string DefaultLanguageForStoryAssets = "English";

    [MenuItem("Tools/AR Storybook/Connect Audio To Pages", false, 2)]
    public static void SetupAll() => Run(onlyLanguage: null);

    /// <summary>
    /// Same as SetupAll, but only touches ONE language -- used by the "Set Up
    /// This Language's Audio" button on that language's own asset.
    /// </summary>
    public static (int created, int purged) SetupForLanguage(string language)
    {
        return Run(onlyLanguage: language);
    }

    private static (int created, int purged) Run(string onlyLanguage)
    {
        AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.Settings;
        if (settings == null)
        {
            Debug.LogError("[Audio Setup] Addressable Asset Settings not found. " +
                           "Open Window → Asset Management → Addressables → Groups first.");
            return (0, 0);
        }

        ARAddressableAudioCatalog catalog = AssetDatabase.LoadAssetAtPath<ARAddressableAudioCatalog>(CatalogAssetPath);
        if (catalog == null)
        {
            Debug.LogError($"[Audio Setup] AudioLanguageCatalog not found at: {CatalogAssetPath}");
            return (0, 0);
        }

        bool catalogDirty = false;

        // Self-healing step: if a recording, a pack, or a whole language was deleted by
        // hand since the last run, its catalog entry now points at nothing. Purge those
        // FIRST, every time, so this always fully rebuilds from what actually exists now.
        int purged = catalog.RemoveEntriesWhere(e =>
            (onlyLanguage == null || string.Equals(e.languageName, onlyLanguage, StringComparison.OrdinalIgnoreCase)) &&
            (e.audioPack == null || string.IsNullOrEmpty(e.audioPack.AssetGUID) ||
             string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(e.audioPack.AssetGUID)) ||
             AssetDatabase.LoadAssetAtPath<ARPageAudioPack>(AssetDatabase.GUIDToAssetPath(e.audioPack.AssetGUID)) == null));
        if (purged > 0)
        {
            catalogDirty = true;
            Debug.Log($"[Audio Setup] Removed {purged} old entries pointing at deleted audio -- rebuilding those from scratch.");
        }

        int created = 0;
        var allPages = PageIdentityUtility.GetAllPages().Where(p => !string.IsNullOrEmpty(p.pageId)).ToList();

        // 1. THE SIMPLE CONVENTION: Assets/Audio/{Language}/ -- one folder per language,
        // files named by page range or page order. Matches against every page in the
        // whole scene at once (page numbers are unique across the whole storybook).
        if (!Directory.Exists(AudioRootFolder))
        {
            Debug.Log($"[Audio Setup] {AudioRootFolder} not found yet -- create a folder there named after " +
                      "a language (e.g. Assets/Audio/Hindi/) and put its recordings in it to use this.");
        }
        else
        {
            foreach (string langDir in Directory.GetDirectories(AudioRootFolder))
            {
                string language = Path.GetFileName(langDir);
                if (onlyLanguage != null && !string.Equals(language, onlyLanguage, StringComparison.OrdinalIgnoreCase))
                    continue;

                List<string> allFilesInLangDir = Directory.GetFiles(langDir, "*", SearchOption.AllDirectories).ToList();
                List<string> audioFiles = allFilesInLangDir.Where(IsAudioFile).ToList();
                WarnAboutUnrecognizedFiles(allFilesInLangDir, audioFiles, langDir);

                created += MatchAndConnect(audioFiles, allPages, language, settings, catalog, ref catalogDirty);
            }
        }

        // 2. Older/messier layout: Assets/Story_Assets/.../Story-N/.../Audio*/ folders,
        // wherever recordings already happen to live, whatever naming is already in use.
        created += ScanStoryAssetsFolders(settings, catalog, onlyLanguage, ref catalogDirty);

        // 3. Safety net: any audio pack that exists but isn't registered/cataloged yet
        // (e.g. someone dragged a clip into a pack by hand) gets fixed too.
        int fixedExisting = 0;
        foreach (string packGuid in AssetDatabase.FindAssets("t:ARPageAudioPack"))
        {
            string packPath = AssetDatabase.GUIDToAssetPath(packGuid);
            ARPageAudioPack pack = AssetDatabase.LoadAssetAtPath<ARPageAudioPack>(packPath);
            if (pack == null || string.IsNullOrWhiteSpace(pack.pageId) || string.IsNullOrWhiteSpace(pack.languageName)) continue;
            if (onlyLanguage != null && !string.Equals(pack.languageName, onlyLanguage, StringComparison.OrdinalIgnoreCase)) continue;

            bool alreadyRegistered = settings.FindAssetEntry(packGuid) != null;
            bool alreadyInCatalog = catalog.TryGetAudioPack(pack.languageName, pack.pageId, out _);
            if (alreadyRegistered && alreadyInCatalog) continue;

            AddressableAssetGroup existingGroup = GetOrCreateGroup(settings, $"Audio_{pack.languageName}");
            RegisterAddressable(settings, existingGroup, packPath, $"audio/{pack.languageName}/{pack.pageId}");

            if (!alreadyInCatalog)
            {
                catalog.AddEntry(pack.languageName, pack.pageId, new AssetReferenceT<ARPageAudioPack>(packGuid));
                catalogDirty = true;
            }
            fixedExisting++;
        }

        if (catalogDirty) EditorUtility.SetDirty(catalog);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log($"[Audio Setup] Done — Connected:{created}  FixedExisting:{fixedExisting}  RemovedStale:{purged}" +
                  (onlyLanguage != null ? $"  Language:{onlyLanguage}" : ""));

        if (onlyLanguage == null)
        {
            EditorUtility.DisplayDialog("Audio Setup Complete",
                $"Connected: {created}\n" +
                $"Fixed packs that were missing registration: {fixedExisting}\n" +
                $"Removed old entries pointing at deleted audio: {purged}\n\n" +
                "All audio packs are Addressable.",
                "OK");
        }

        return (created, purged);
    }

    // -----------------------------------------------------------------------
    // The one shared matcher -- used for BOTH the clean Assets/Audio/{Language}
    // convention and the messier Story_Assets scan below. One set of rules,
    // used everywhere, instead of two different systems to remember.
    // -----------------------------------------------------------------------

    private static int MatchAndConnect(List<string> audioFiles, List<PageIdentityUtility.PageInfo> candidatePages,
        string language, AddressableAssetSettings settings, ARAddressableAudioCatalog catalog, ref bool catalogDirty)
    {
        int created = 0;
        var byRange = new Dictionary<string, List<string>>();
        var positional = new List<string>();

        foreach (string file in audioFiles)
        {
            string name = Path.GetFileNameWithoutExtension(file);
            // Accepts a plain hyphen, spaced hyphen, or an en/em-dash a phone keyboard or
            // word processor might auto-substitute (e.g. "45–46.mp3"), plus an underscore
            // as a less common but still reasonable separator (e.g. "45_46.mp3").
            Match rangeMatch = Regex.Match(name, @"(\d+)\s*[-_–—]\s*(\d+)");
            if (rangeMatch.Success)
            {
                string range = $"{rangeMatch.Groups[1].Value}-{rangeMatch.Groups[2].Value}";
                if (!byRange.TryGetValue(range, out var list)) byRange[range] = list = new List<string>();
                list.Add(file);
            }
            else
            {
                positional.Add(file);
            }
        }

        // Convention 1: the file name contains a page range, e.g. "45-46.mp3" -- one
        // clip, matched as a whole two-number token against a real page. If a range
        // key does NOT match a real page as a whole, it's held back for Convention 3
        // below instead of being silently dropped.
        var unresolvedRangeKeys = new List<string>();
        foreach (var kvp in byRange)
        {
            string pageId = candidatePages.FirstOrDefault(p => PageIdentityUtility.PageIdContainsRange(p.pageId, kvp.Key))?.pageId;
            if (string.IsNullOrEmpty(pageId))
            {
                unresolvedRangeKeys.Add(kvp.Key);
                continue;
            }
            if (PackExistsForPageId(catalog, language, pageId)) continue;

            string bestFile = PickBestVariant(kvp.Value);
            if (CreatePackFromRawFiles(language, pageId, new List<string> { bestFile }, settings, catalog, ref catalogDirty))
                created++;
        }

        // Convention 3: "{page}-{segment}", e.g. "57-1.mp3" then "57-2.mp3" -- multiple
        // voice clips for the SAME page, played one after another in segment order.
        // Only considered for range-shaped names that did NOT match a real page as a
        // whole above, so a genuine two-page spread like "45-46.mp3" always wins
        // Convention 1 first and is never reinterpreted as page 45 segment 46.
        var segmentGroups = new Dictionary<string, List<(int segment, string file)>>();
        foreach (string key in unresolvedRangeKeys)
        {
            Match segMatch = Regex.Match(key, @"^(\d+)-(\d+)$");
            if (!segMatch.Success) continue;
            if (!int.TryParse(segMatch.Groups[2].Value, out int segmentNumber)) continue;

            string pageNumber = segMatch.Groups[1].Value;
            if (!segmentGroups.TryGetValue(pageNumber, out var list))
                segmentGroups[pageNumber] = list = new List<(int, string)>();

            // A single segment slot can itself have a retake ("57-1.mp3" + "57-1 2nd.mp3") --
            // pick the best one for that slot the same way Convention 1 does.
            string bestForSegment = PickBestVariant(byRange[key]);
            list.Add((segmentNumber, bestForSegment));
        }

        foreach (var kvp in segmentGroups)
        {
            string pageId = candidatePages.FirstOrDefault(p => PageIdentityUtility.PageIdContainsRange(p.pageId, kvp.Key))?.pageId;
            if (string.IsNullOrEmpty(pageId)) continue; // page number alone matches no real page either -- nothing to connect
            if (PackExistsForPageId(catalog, language, pageId)) continue;

            List<string> orderedFiles = kvp.Value.OrderBy(x => x.segment).Select(x => x.file).ToList();
            if (CreatePackFromRawFiles(language, pageId, orderedFiles, settings, catalog, ref catalogDirty))
                created++;
        }

        // Convention 2: the file name is just a number, e.g. "page3.mp3" -- the Nth
        // file matches the Nth page in the candidate list, sorted the same way the
        // scene/window system sorts them.
        var sortedCandidates = candidatePages.OrderBy(p => p.sortNumber).ToList();
        var positionalSorted = positional
            .Select(f => new { file = f, num = ExtractPositionalNumber(f) })
            .Where(x => x.num > 0)
            .OrderBy(x => x.num)
            .ToList();

        // Positional matching is inherently fragile -- it trusts that file #3 really is
        // the 3rd page. If a file is missing or misnumbered, everything after the gap
        // silently shifts onto the WRONG page. Warn loudly instead of guessing quietly.
        int expectedNext = 1;
        foreach (var entry in positionalSorted)
        {
            if (entry.num != expectedNext)
            {
                Debug.LogWarning($"[Audio Setup] Positional audio files for {language} skip from expected " +
                                  $"page #{expectedNext} to #{entry.num} (found '{Path.GetFileName(entry.file)}'). " +
                                  "Every file after a gap like this can end up on the WRONG page. Rename these " +
                                  "with an explicit page range instead (e.g. \"45-46.mp3\") to avoid relying on order.");
            }
            expectedNext = entry.num + 1;

            int index = entry.num - 1;
            if (index < 0 || index >= sortedCandidates.Count) continue;

            string pageId = sortedCandidates[index].pageId;
            if (string.IsNullOrEmpty(pageId)) continue;
            if (PackExistsForPageId(catalog, language, pageId)) continue;

            if (CreatePackFromRawFiles(language, pageId, new List<string> { entry.file }, settings, catalog, ref catalogDirty))
                created++;
        }

        return created;
    }

    // -----------------------------------------------------------------------
    // Story_Assets scan -- wherever your recordings already live in the older
    // layout, whatever naming is already in use there. Only fills genuine gaps.
    // -----------------------------------------------------------------------

    private static int ScanStoryAssetsFolders(AddressableAssetSettings settings,
        ARAddressableAudioCatalog catalog, string onlyLanguage, ref bool catalogDirty)
    {
        int created = 0;

        if (!Directory.Exists(StoryAssetsRoot))
        {
            Debug.Log($"[Audio Setup] {StoryAssetsRoot} not found -- nothing to scan there.");
            return created;
        }

        // Match any folder whose NAME contains "audio" -- covers "Audio", "Audios",
        // "Audio_english", "All English Audios", etc.
        string[] audioDirs = Directory.GetDirectories(StoryAssetsRoot, "*", SearchOption.AllDirectories)
            .Where(d => Path.GetFileName(d).IndexOf("audio", StringComparison.OrdinalIgnoreCase) >= 0)
            .ToArray();

        foreach (string audioDir in audioDirs)
        {
            string storyNumber = FindStoryNumber(audioDir);
            if (storyNumber == null) continue;

            string storyMarker = $"S{storyNumber}_P";
            string language = DetectLanguageFromPath(audioDir, settings);
            if (onlyLanguage != null && !string.Equals(language, onlyLanguage, StringComparison.OrdinalIgnoreCase))
                continue;

            List<PageIdentityUtility.PageInfo> storyPages = PageIdentityUtility.GetAllPages()
                .Where(p => !string.IsNullOrEmpty(p.pageId) && p.pageId.Contains(storyMarker))
                .ToList();

            if (storyPages.Count == 0)
            {
                Debug.Log($"[Audio Setup] '{audioDir}': no pages in the open scene have a pageId containing " +
                          $"'{storyMarker}' -- skipping this folder. (You can still connect these recordings " +
                          "by dragging a recording onto that page's audio slot directly.)");
                continue;
            }

            List<string> allFilesInDir = Directory.GetFiles(audioDir, "*", SearchOption.TopDirectoryOnly).ToList();
            List<string> audioFiles = allFilesInDir.Where(IsAudioFile).ToList();
            WarnAboutUnrecognizedFiles(allFilesInDir, audioFiles, audioDir);

            created += MatchAndConnect(audioFiles, storyPages, language, settings, catalog, ref catalogDirty);
        }

        return created;
    }

    // Walks up from an audio folder looking for the nearest ancestor folder whose name
    // contains "story" plus a number -- e.g. "Story 3", "story-2", "story_1", "Story 5".
    private static string FindStoryNumber(string audioDir)
    {
        string dir = Path.GetDirectoryName(audioDir);
        string root = Path.GetFullPath(StoryAssetsRoot).TrimEnd('/', '\\');

        while (!string.IsNullOrEmpty(dir))
        {
            string name = Path.GetFileName(dir);
            if (name.IndexOf("story", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Match m = Regex.Match(name, @"\d+");
                if (m.Success) return m.Value;
            }

            if (string.Equals(Path.GetFullPath(dir).TrimEnd('/', '\\'), root, StringComparison.OrdinalIgnoreCase))
                break;

            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    // Common language names to look for anywhere in an audio folder's full path.
    // Falls back to English if none of these appear.
    private static readonly string[] KnownLanguageNames =
    {
        "English", "Hindi", "Telugu", "Tamil", "Kannada", "Malayalam",
        "Marathi", "Bengali", "Gujarati", "Punjabi", "Odia", "Urdu"
    };

    private static string DetectLanguageFromPath(string audioDir, AddressableAssetSettings settings)
    {
        foreach (string lang in KnownLanguageNames)
        {
            if (audioDir.IndexOf(lang, StringComparison.OrdinalIgnoreCase) >= 0)
                return lang;
        }

        // Also recognize any Language asset this project has (Assets/code/*.asset of
        // type ARStorybookLanguage), so a language outside the built-in list above --
        // added the moment you create its asset -- still gets detected from the path.
        foreach (string guid in AssetDatabase.FindAssets("t:ARStorybookLanguage"))
        {
            string lang = Path.GetFileNameWithoutExtension(AssetDatabase.GUIDToAssetPath(guid));
            if (!string.IsNullOrEmpty(lang) && audioDir.IndexOf(lang, StringComparison.OrdinalIgnoreCase) >= 0)
                return lang;
        }

        // Also recognize any language this project already has (its Audio_<Language>
        // groups), for languages set up before the Language-asset system existed.
        if (settings != null)
        {
            foreach (var group in settings.groups)
            {
                if (group == null || !group.Name.StartsWith("Audio_")) continue;
                string lang = group.Name.Substring("Audio_".Length);
                if (!string.IsNullOrEmpty(lang) && audioDir.IndexOf(lang, StringComparison.OrdinalIgnoreCase) >= 0)
                    return lang;
            }
        }

        Debug.Log($"[Audio Setup] '{audioDir}' has no language name in its path -- assuming " +
                  $"{DefaultLanguageForStoryAssets}. If that is wrong, put these recordings in a folder " +
                  "whose path contains the language name (e.g. .../Hindi/Audio/) and run this again.");
        return DefaultLanguageForStoryAssets;
    }

    private static int ExtractPositionalNumber(string filePath)
    {
        string name = Path.GetFileNameWithoutExtension(filePath);

        // Prefer a number that directly follows "page"/"pg" (e.g. "Story2_page3.mp3" -> 3,
        // not the "2" from "Story2") since that's almost always the intended index.
        Match afterPage = Regex.Match(name, @"(?:page|pg)\s*[-_]?\s*(\d+)", RegexOptions.IgnoreCase);
        if (afterPage.Success && int.TryParse(afterPage.Groups[1].Value, out int fromPage))
            return fromPage;

        // Otherwise take the LAST number in the name, not the first -- a leading number
        // is more often a story/version prefix than the intended page index.
        MatchCollection all = Regex.Matches(name, @"\d+");
        if (all.Count > 0 && int.TryParse(all[all.Count - 1].Value, out int fromLast))
            return fromLast;

        return -1;
    }

    // When more than one file exists for the same page range (e.g. a re-recorded
    // "13-14 2nd.mp3" next to the original "13-14.mp3"), prefer the plain "range.mp3"
    // file and log a note about the other one -- never silently discard it.
    private static string PickBestVariant(List<string> files)
    {
        if (files.Count == 1) return files[0];

        string plain = files.FirstOrDefault(f =>
            Regex.IsMatch(Path.GetFileNameWithoutExtension(f), @"^\d+-\d+$"));
        string chosen = plain ?? files[0];

        foreach (string f in files)
        {
            if (f != chosen)
                Debug.Log($"[Audio Setup] Multiple audio files found for the same page range -- " +
                          $"using '{Path.GetFileName(chosen)}', ignoring '{Path.GetFileName(f)}'. " +
                          "Rename or remove the one you don't want if this picked the wrong one.");
        }

        return chosen;
    }

    // "Exists" means the pack actually has a real clip in it -- not just that a catalog
    // entry points at *some* asset. A pack created earlier but left empty (source file
    // failed to load that time, or the run was interrupted) must NOT block trying again.
    private static bool PackExistsForPageId(ARAddressableAudioCatalog catalog, string language, string pageId)
    {
        if (!catalog.TryGetAudioPack(language, pageId, out var audioPackRef)) return false;

        string path = AssetDatabase.GUIDToAssetPath(audioPackRef.AssetGUID);
        ARPageAudioPack pack = string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<ARPageAudioPack>(path);
        if (pack == null) return false;

        bool hasVoice = pack.voiceClips != null && pack.voiceClips.Exists(s => s != null && s.clip != null);
        bool hasBgm   = pack.bgmClips   != null && pack.bgmClips.Exists(s => s != null && s.clip != null);
        return hasVoice || hasBgm;
    }

    // Builds (or updates) a page's pack from one or more clips, IN ORDER -- one file
    // means a single voice clip as before; several files (e.g. "57-1.mp3", "57-2.mp3")
    // become sequential voiceClips that play one after another on that page.
    internal static bool CreatePackFromRawFiles(string language, string pageId, List<string> orderedFilePaths,
        AddressableAssetSettings settings, ARAddressableAudioCatalog catalog, ref bool catalogDirty)
    {
        var clips = new List<AudioClip>();
        foreach (string filePath in orderedFilePaths)
        {
            string assetPath = filePath.Replace('\\', '/');
            int assetsIdx = assetPath.IndexOf("Assets/");
            if (assetsIdx < 0) continue;
            assetPath = assetPath.Substring(assetsIdx);

            AudioClip clip = AssetDatabase.LoadAssetAtPath<AudioClip>(assetPath);
            if (clip == null)
            {
                Debug.LogWarning($"[Audio Setup] Could not load '{assetPath}' as an AudioClip -- skipping.");
                continue;
            }
            clips.Add(clip);
        }
        if (clips.Count == 0) return false;

        string packFolder = $"{PackOutputFolder}/{language}";
        EnsureFolder(packFolder);
        // pageId comes from a free-text field on the page prefab -- sanitize it before
        // using it in a file name so a stray "/", ":" etc. can't silently fail to create
        // the asset or land it somewhere unexpected.
        string packPath = $"{packFolder}/Page_{SanitizeForFileName(pageId)}_{language}.asset";

        ARPageAudioPack pack = AssetDatabase.LoadAssetAtPath<ARPageAudioPack>(packPath);
        if (pack == null)
        {
            pack = ScriptableObject.CreateInstance<ARPageAudioPack>();
            pack.languageName = language;
            pack.pageId       = pageId;
            pack.bgmClips     = new List<ARPageAudioPack.AudioSegment>();
            AssetDatabase.CreateAsset(pack, packPath);
        }

        pack.voiceClips = clips
            .Select(c => new ARPageAudioPack.AudioSegment { clip = c, volume = 1f })
            .ToList();
        EditorUtility.SetDirty(pack);

        AddressableAssetGroup group = GetOrCreateGroup(settings, $"Audio_{language}");
        string address = $"audio/{language}/{pageId}";
        RegisterAddressable(settings, group, packPath, address);

        if (!catalog.TryGetAudioPack(language, pageId, out _))
        {
            string packGuid = AssetDatabase.AssetPathToGUID(packPath);
            var audioPackRef = new AssetReferenceT<ARPageAudioPack>(packGuid);
            catalog.AddEntry(language, pageId, audioPackRef);
            catalogDirty = true;
        }

        string names = string.Join(", ", orderedFilePaths.Select(Path.GetFileName));
        Debug.Log($"[Audio Setup] Connected [{names}] -> page '{pageId}' ({language}), {clips.Count} clip(s) in sequence.");
        return true;
    }

    // -----------------------------------------------------------------------

    internal static AddressableAssetGroup GetOrCreateGroup(AddressableAssetSettings settings, string groupName)
    {
        AddressableAssetGroup group = settings.FindGroup(groupName);
        if (group != null) return group;

        group = settings.CreateGroup(groupName, false, false, true, null);

        var bundled = group.AddSchema<BundledAssetGroupSchema>();
        bundled.BundleMode = BundledAssetGroupSchema.BundlePackingMode.PackSeparately;
        bundled.BuildPath.SetVariableByName(settings, AddressableAssetSettings.kRemoteBuildPath);
        bundled.LoadPath.SetVariableByName(settings, AddressableAssetSettings.kRemoteLoadPath);

        group.AddSchema<ContentUpdateGroupSchema>();

        Debug.Log($"[Audio Setup] Created group: {groupName} (Pack Separately, Remote)");
        return group;
    }

    internal static void RegisterAddressable(AddressableAssetSettings settings,
        AddressableAssetGroup group, string assetPath, string address)
    {
        string guid = AssetDatabase.AssetPathToGUID(assetPath);
        if (string.IsNullOrEmpty(guid)) return;

        AddressableAssetEntry entry = settings.FindAssetEntry(guid);
        if (entry == null)
            entry = settings.CreateOrMoveEntry(guid, group, false, false);

        entry.address = address;
    }

    internal static void EnsureFolder(string path)
    {
        if (Directory.Exists(path)) return;
        Directory.CreateDirectory(path);
        AssetDatabase.ImportAsset(path);
    }

    // pageId is free text read off a page prefab -- strip anything illegal in a Windows/
    // Unity asset file name so building a pack path from it can never silently fail.
    internal static string SanitizeForFileName(string pageId)
    {
        if (string.IsNullOrEmpty(pageId)) return pageId;
        foreach (char c in Path.GetInvalidFileNameChars())
            pageId = pageId.Replace(c, '_');
        return pageId;
    }

    // Includes .m4a/.aac (the default format for iPhone/Android voice memos and WhatsApp
    // voice notes -- very likely to be what a non-technical user actually hands over) and
    // .flac/.wma so more real-world recordings are picked up instead of silently skipped.
    private static bool IsAudioFile(string path)
    {
        string ext = Path.GetExtension(path).ToLower();
        return ext == ".mp3" || ext == ".wav" || ext == ".ogg" || ext == ".aif" || ext == ".aiff" ||
               ext == ".m4a" || ext == ".aac" || ext == ".flac" || ext == ".wma";
    }

    // Files that are clearly never audio and should never be reported as "skipped" --
    // Unity metadata, OS junk files, and common non-audio project files.
    private static readonly string[] IgnoredExtensions = { ".meta", ".ds_store", ".db", ".ini", ".txt", ".md" };

    // Points out any file sitting in an audio folder that ISN'T being picked up, instead
    // of silently ignoring it -- e.g. a recording handed over in a format not in the list
    // above, so the user finds out immediately instead of wondering why a page stayed silent.
    private static void WarnAboutUnrecognizedFiles(List<string> allFiles, List<string> recognizedAudioFiles, string dir)
    {
        var unrecognized = allFiles
            .Except(recognizedAudioFiles)
            .Where(f => !IgnoredExtensions.Contains(Path.GetExtension(f).ToLower()))
            .ToList();

        if (unrecognized.Count == 0) return;

        string names = string.Join(", ", unrecognized.Select(Path.GetFileName));
        Debug.LogWarning($"[Audio Setup] '{dir}' has {unrecognized.Count} file(s) that were NOT recognized as " +
                          $"audio and were skipped: {names}. Supported formats: .mp3 .wav .ogg .m4a .aac .flac .wma .aif .aiff.");
    }
}
