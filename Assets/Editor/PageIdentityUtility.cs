using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;

/// <summary>
/// Shared helper: the ONE place that knows how to find every page in the scene and its
/// real pageId. Used by both ARWindowManagerEditor and AudioAddressableSetupTool so there
/// is only one lookup to keep correct, instead of two separate copies drifting apart.
///
/// The real pageId is read directly from the page's own content prefab
/// (ARTrackedPageNode.PageId) -- this is always correct, whether or not audio exists for
/// that page yet. Falls back to matching against known audio-catalog pageIds by shared
/// page-number only if the prefab can't be found or has no pageId set.
/// </summary>
public static class PageIdentityUtility
{
    public class PageInfo
    {
        public string addressableKey;
        public string pageId;
        public int sortNumber;
    }

    public static List<PageInfo> GetAllPages(List<string> catalogPageIdsForFallback = null)
    {
        var fallbackList = catalogPageIdsForFallback ?? new List<string>();
        var result = new List<PageInfo>();

        CustomARHandler[] handlers = Object.FindObjectsByType<CustomARHandler>(FindObjectsSortMode.None);
        foreach (CustomARHandler handler in handlers)
        {
            if (handler == null || string.IsNullOrWhiteSpace(handler.addressableKey)) continue;

            string key = handler.addressableKey;
            string pageId = ResolvePageId(key, fallbackList);
            int sortNumber = ExtractSortNumber(key);

            result.Add(new PageInfo { addressableKey = key, pageId = pageId, sortNumber = sortNumber });
        }

        return result.OrderBy(p => p.sortNumber).ToList();
    }

    // Quiz pages always get an empty pageId -- no audio expected.
    private static string ResolvePageId(string addressableKey, List<string> catalogPageIds)
    {
        if (addressableKey.ToLowerInvariant().Contains("quiz"))
            return "";

        string fromPrefab = ReadPageIdFromContentPrefab(addressableKey);
        if (!string.IsNullOrWhiteSpace(fromPrefab))
            return fromPrefab;

        Match numberRangeMatch = Regex.Match(addressableKey, @"(\d+)-(\d+)$");
        if (!numberRangeMatch.Success) return "";

        string numberRange = numberRangeMatch.Value;
        return catalogPageIds.FirstOrDefault(pid => PageIdContainsRange(pid, numberRange)) ?? "";
    }

    /// <summary>
    /// True when the pageId contains the page-number range as a whole token.
    /// "1-2" must NOT match inside "21-22" -- a digit touching either side of the
    /// range breaks the match.
    /// </summary>
    public static bool PageIdContainsRange(string pageId, string numberRange)
    {
        if (string.IsNullOrEmpty(pageId) || string.IsNullOrEmpty(numberRange)) return false;
        return Regex.IsMatch(pageId, $@"(^|\D){Regex.Escape(numberRange)}(\D|$)");
    }

    public static string ReadPageIdFromContentPrefab(string addressableKey)
    {
        AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.Settings;
        if (settings == null) return null;

        foreach (var group in settings.groups)
        {
            if (group == null) continue;
            foreach (var entry in group.entries)
            {
                if (entry == null || entry.address != addressableKey) continue;

                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(entry.AssetPath);
                ARTrackedPageNode node = prefab != null ? prefab.GetComponent<ARTrackedPageNode>() : null;
                return node != null ? node.PageId : null;
            }
        }
        return null;
    }

    public static int ExtractSortNumber(string addressableKey)
    {
        // Sort by the PAGE number, which sits at the END of the key -- keys like
        // "Story_2_page_45-46" START with the story number (2), which is the same for
        // every page in that story and would make sorting meaningless.
        Match m = Regex.Match(addressableKey, @"(\d+)\s*-\s*\d+\s*$");   // ends with a range -> first of the pair
        if (!m.Success) m = Regex.Match(addressableKey, @"(\d+)\s*$");   // ends with a single number
        if (!m.Success) m = Regex.Match(addressableKey, @"(\d+)");       // fallback: any number (e.g. "..._intro")
        return m.Success && int.TryParse(m.Groups[1].Value, out int n) ? n : int.MaxValue;
    }
}
