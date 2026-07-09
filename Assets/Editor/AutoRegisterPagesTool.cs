using System.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;

/// <summary>
/// One-click tool: makes every page marker's content downloadable, for every marker
/// in the open scene at once -- no per-page selecting or dragging needed, however
/// many pages there are.
///
/// How it matches a marker to its content: it looks for a prefab file anywhere in
/// the project whose FILE NAME exactly matches the marker's Addressable Key (e.g.
/// marker "Story_2_page_45-46" -> looks for a prefab file also named
/// "Story_2_page_45-46"). If found, it registers that prefab so the app can
/// download it when the marker is scanned.
///
/// Only fills genuine gaps -- a marker that already has downloadable content is
/// never touched. If no prefab with a matching name is found, that one marker is
/// reported so you can connect it by hand (drag its prefab in Addressables Groups).
///
/// Menu: Tools → AR Storybook → Make All Pages Downloadable
/// </summary>
public static class AutoRegisterPagesTool
{
    [MenuItem("Tools/AR Storybook/Make All Pages Downloadable", false, 1)]
    public static void RegisterAll()
    {
        AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.Settings;
        if (settings == null)
        {
            Debug.LogError("[Register Pages] Addressable Asset Settings not found. " +
                           "Open Window → Asset Management → Addressables → Groups first.");
            return;
        }

        var registeredKeys = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);
        foreach (var group in settings.groups)
        {
            if (group == null) continue;
            foreach (var entry in group.entries)
                if (entry != null && !string.IsNullOrEmpty(entry.address))
                    registeredKeys.Add(entry.address);
        }

        CustomARHandler[] handlers = Object.FindObjectsByType<CustomARHandler>(FindObjectsSortMode.None);

        int registered = 0;
        int alreadyDone = 0;
        var notFound = new System.Collections.Generic.List<string>();

        foreach (var handler in handlers)
        {
            if (handler == null || string.IsNullOrWhiteSpace(handler.addressableKey)) continue;
            string key = handler.addressableKey;

            if (registeredKeys.Contains(key))
            {
                alreadyDone++;
                continue;
            }

            string[] guids = AssetDatabase.FindAssets($"t:Prefab {key}");
            string matchPath = null;
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (System.IO.Path.GetFileNameWithoutExtension(path) == key)
                {
                    matchPath = path;
                    break;
                }
            }

            if (matchPath == null)
            {
                notFound.Add(key);
                continue;
            }

            AudioAddressableSetupTool.RegisterAddressable(settings, settings.DefaultGroup, matchPath, key);
            registered++;
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        string message =
            $"Newly registered: {registered}\n" +
            $"Already downloadable: {alreadyDone}\n" +
            $"Could not find a matching prefab: {notFound.Count}";

        if (notFound.Count > 0)
        {
            message += "\n\nThese markers need a manual connection (their prefab's file name " +
                       "doesn't match their marker name):\n• " + string.Join("\n• ", notFound.Take(20));
            if (notFound.Count > 20) message += $"\n...and {notFound.Count - 20} more";
        }

        Debug.Log($"[Register Pages] Done — Registered:{registered}  AlreadyDone:{alreadyDone}  NotFound:{notFound.Count}");
        EditorUtility.DisplayDialog("Register All Pages", message, "OK");
    }
}
