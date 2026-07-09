using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Custom Inspector for ARWindowManager.
/// Auto-populates the full page list the first time you select ARWindowManager in the Inspector.
/// You can also re-run it any time via the "Re-Setup All Pages" button.
///
/// The page list is computed AUTOMATICALLY every time this runs -- it is never hand-typed.
/// It uses PageIdentityUtility (Assets/Editor/PageIdentityUtility.cs) to find every page
/// marker in the scene and its real pageId, read directly from that page's own content
/// prefab -- always correct, whether or not audio exists for it yet.
///
/// Because this is computed fresh every time, adding a new page marker later needs NO
/// code changes here -- just re-open this Inspector (or click "Re-Setup All Pages") and
/// the new page is picked up automatically.
/// </summary>
[CustomEditor(typeof(ARWindowManager))]
public class ARWindowManagerEditor : Editor
{
    private const string CatalogAssetPath = "Assets/code/AudioLanguageCatalog.asset";

    void OnEnable()
    {
        var mgr = (ARWindowManager)target;
        if (mgr.pages == null || mgr.pages.Count == 0)
        {
            PopulatePages(mgr);
            EditorUtility.SetDirty(mgr);
        }
    }

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        EditorGUILayout.Space(12);
        GUI.backgroundColor = new Color(0.4f, 0.8f, 0.4f);
        if (GUILayout.Button("Re-Setup All Pages", GUILayout.Height(36)))
        {
            PopulatePages((ARWindowManager)target);
            EditorUtility.SetDirty(target);
            Debug.Log("[AR-WINDOW] Pages list re-populated automatically from the scene + each page's own prefab.");
        }
        GUI.backgroundColor = Color.white;

        EditorGUILayout.HelpBox(
            $"Total pages: {((ARWindowManager)target).pages?.Count ?? 0}  " +
            "(including quiz pages with no audio)\n" +
            "Position in list = page index used by the window calculation.\n" +
            "This list is computed automatically -- click 'Re-Setup All Pages' any time after adding a new page marker.",
            MessageType.Info);
    }

    public static void PopulatePages(ARWindowManager mgr)
    {
        List<string> catalogPageIds = LoadCatalogPageIds();

        mgr.pages = PageIdentityUtility.GetAllPages(catalogPageIds)
            .Select(p => P(p.addressableKey, p.pageId))
            .ToList();
    }

    private static List<string> LoadCatalogPageIds()
    {
        var pageIds = new List<string>();
        var catalog = AssetDatabase.LoadAssetAtPath<ARAddressableAudioCatalog>(CatalogAssetPath);
        if (catalog == null) return pageIds;

        foreach (var entry in catalog.GetAllEntries())
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.pageId)) continue;
            if (!pageIds.Contains(entry.pageId)) pageIds.Add(entry.pageId);
        }
        return pageIds;
    }

    static ARWindowManager.PageEntry P(string addressableKey, string pageId) =>
        new ARWindowManager.PageEntry { addressableKey = addressableKey, pageId = pageId };

    // ─────────────────────────────────────────────────────────────────────────
    // Menu: Tools → AR Storybook → Refresh Page List
    // Same as the "Re-Setup All Pages" button above, but reachable without having
    // to first find and select the ARWindowManager object in the scene.
    // ─────────────────────────────────────────────────────────────────────────

    [MenuItem("Tools/AR Storybook/Refresh Page List", false, 3)]
    public static void UpdatePageOrderMenuItem()
    {
        var mgr = Object.FindFirstObjectByType<ARWindowManager>();
        if (mgr == null)
        {
            EditorUtility.DisplayDialog("Refresh Page List",
                "The open scene has no ARWindowManager — that is the object holding the page order " +
                "list, usually on a manager object in the main AR scene.\n\n" +
                "Open your main AR scene and try again.", "OK");
            return;
        }

        Undo.RecordObject(mgr, "Update Page Order");
        PopulatePages(mgr);
        EditorUtility.SetDirty(mgr);
        EditorSceneManager.MarkSceneDirty(mgr.gameObject.scene);

        EditorUtility.DisplayDialog("Refresh Page List",
            $"Done — the page list now has {(mgr.pages != null ? mgr.pages.Count : 0)} pages, " +
            "read automatically from the scene. Save the scene (Ctrl+S) to keep it.", "OK");
    }
}
