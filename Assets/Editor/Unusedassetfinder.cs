// UnusedAssetFinder.cs
// Save to: Assets/Editor/UnusedAssetFinder.cs
// Unity 6 (6000.x) compatible
// Requires: Addressables package installed (com.unity.addressables)
// If Addressables is NOT installed, delete lines marked [ADDR]

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;          // [ADDR]
using UnityEditor.AddressableAssets.Settings; // [ADDR]
using UnityEngine;

public class UnusedAssetFinder : EditorWindow
{
    // ── State ──────────────────────────────────────────────────────────────
    private List<string> _assets = new List<string>();
    private List<bool> _checked = new List<bool>();
    private Vector2 _scroll;
    private bool _scanned;
    private string _filterExt = "";

    // ── Menu ───────────────────────────────────────────────────────────────
    [MenuItem("Tools/Unused Asset Finder")]
    public static void Open() => GetWindow<UnusedAssetFinder>("Unused Asset Finder");

    // ── GUI ────────────────────────────────────────────────────────────────
    private void OnGUI()
    {
        GUILayout.Space(6);

        // Safety banner
        EditorGUILayout.HelpBox(
            "BEFORE ANYTHING: Commit your project to Git.\n" +
            "Protected automatically: Resources/, StreamingAssets/, Plugins/, Editor/, " +
            "Addressables, all Build Scene dependencies, Scripts, Shader includes, Config files.",
            MessageType.Warning);

        GUILayout.Space(6);

        if (GUILayout.Button("Scan Project for Unused Assets", GUILayout.Height(36)))
            Scan();

        if (!_scanned) return;

        GUILayout.Space(6);
        EditorGUILayout.LabelField(
            $"Potentially unused: {_assets.Count} assets",
            EditorStyles.boldLabel);

        // Extension filter
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Filter extension:", GUILayout.Width(110));
        _filterExt = EditorGUILayout.TextField(_filterExt, GUILayout.Width(70)).ToLower().TrimStart('.');
        if (GUILayout.Button("Clear", GUILayout.Width(50))) _filterExt = "";
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Select All Visible")) SelectVisible(true);
        if (GUILayout.Button("Deselect All Visible")) SelectVisible(false);
        if (GUILayout.Button("Export CSV")) ExportCSV();
        EditorGUILayout.EndHorizontal();

        int selCount = _checked.Count(x => x);
        var prevBG = GUI.backgroundColor;
        GUI.backgroundColor = selCount > 0 ? new Color(1f, 0.3f, 0.3f) : Color.gray;

        bool doDelete = GUILayout.Button(
            $"DELETE {selCount} SELECTED  —  IRREVERSIBLE  (Git commit first!)",
            GUILayout.Height(34));

        GUI.backgroundColor = prevBG;

        if (doDelete && selCount > 0)
        {
            if (EditorUtility.DisplayDialog(
                "Confirm Permanent Delete",
                $"About to delete {selCount} asset(s).\n\n" +
                "This CANNOT be undone in Unity.\n" +
                "Have you committed to Git?",
                "Yes, Delete Permanently",
                "Cancel — Go Back"))
            {
                Delete();
            }
        }

        // Asset list
        GUILayout.Space(4);
        _scroll = EditorGUILayout.BeginScrollView(_scroll);

        for (int i = 0; i < _assets.Count; i++)
        {
            string path = _assets[i];
            string ext = Path.GetExtension(path).ToLower().TrimStart('.');

            if (!string.IsNullOrEmpty(_filterExt) && ext != _filterExt)
                continue;

            EditorGUILayout.BeginHorizontal();
            _checked[i] = EditorGUILayout.Toggle(_checked[i], GUILayout.Width(18));
            EditorGUILayout.LabelField(ext, GUILayout.Width(48));
            EditorGUILayout.LabelField(path, GUILayout.MinWidth(100));

            if (GUILayout.Button("Ping", GUILayout.Width(38)))
            {
                var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
                EditorGUIUtility.PingObject(obj);
                Selection.activeObject = obj;
            }
            EditorGUILayout.EndHorizontal();
        }

        EditorGUILayout.EndScrollView();
    }

    // ── Scan ───────────────────────────────────────────────────────────────
    private void Scan()
    {
        _assets.Clear();
        _checked.Clear();
        _scanned = false;

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            // ── 1. Build Settings scenes (all enabled scenes + full dep tree) ──
            EditorUtility.DisplayProgressBar("Unused Asset Finder", "Collecting build scene dependencies...", 0.1f);

            string[] buildScenes = EditorBuildSettings.scenes
                .Where(s => s.enabled && !string.IsNullOrEmpty(s.path))
                .Select(s => s.path)
                .ToArray();

            if (buildScenes.Length > 0)
            {
                foreach (string dep in AssetDatabase.GetDependencies(buildScenes, true))
                    used.Add(dep);
            }

            // ── 2. ALL scenes in the project (even those not in Build Settings) ──
            // Comment this block out if you only want to scan against Build Settings scenes.
            EditorUtility.DisplayProgressBar("Unused Asset Finder", "Collecting all scene dependencies...", 0.25f);

            string[] allSceneGuids = AssetDatabase.FindAssets("t:Scene");
            string[] allScenePaths = allSceneGuids
                .Select(g => AssetDatabase.GUIDToAssetPath(g))
                .Where(p => p.StartsWith("Assets/"))
                .ToArray();

            if (allScenePaths.Length > 0)
                foreach (string dep in AssetDatabase.GetDependencies(allScenePaths, true))
                    used.Add(dep);

            // ── 3. Addressables (assets + full dependency trees) ──        [ADDR]
            EditorUtility.DisplayProgressBar("Unused Asset Finder", "Collecting Addressables dependencies...", 0.4f);

            var addrSettings = AddressableAssetSettingsDefaultObject.Settings; // [ADDR]
            if (addrSettings != null)                                           // [ADDR]
            {                                                                   // [ADDR]
                var addrPaths = new List<string>();                             // [ADDR]

                foreach (var group in addrSettings.groups)                     // [ADDR]
                {                                                               // [ADDR]
                    if (group == null) continue;                                // [ADDR]

                    foreach (var entry in group.entries)                        // [ADDR]
                    {                                                           // [ADDR]
                        if (string.IsNullOrEmpty(entry.AssetPath)) continue;   // [ADDR]

                        if (AssetDatabase.IsValidFolder(entry.AssetPath))      // [ADDR]
                        {                                                       // [ADDR]
                            // Folder entry -- recurse all assets inside        // [ADDR]
                            foreach (string guid in AssetDatabase.FindAssets("", new[] { entry.AssetPath })) // [ADDR]
                                addrPaths.Add(AssetDatabase.GUIDToAssetPath(guid)); // [ADDR]
                        }                                                       // [ADDR]
                        else                                                    // [ADDR]
                        {                                                       // [ADDR]
                            addrPaths.Add(entry.AssetPath);                    // [ADDR]
                        }                                                       // [ADDR]
                    }                                                           // [ADDR]
                }                                                               // [ADDR]

                if (addrPaths.Count > 0)                                       // [ADDR]
                    foreach (string dep in AssetDatabase.GetDependencies(addrPaths.ToArray(), true)) // [ADDR]
                        used.Add(dep);                                          // [ADDR]
            }                                                                   // [ADDR]

            // ── 4. All Prefabs in the project (safety net) ──
            EditorUtility.DisplayProgressBar("Unused Asset Finder", "Collecting prefab dependencies...", 0.55f);

            string[] prefabGuids = AssetDatabase.FindAssets("t:Prefab");
            string[] prefabPaths = prefabGuids
                .Select(g => AssetDatabase.GUIDToAssetPath(g))
                .Where(p => p.StartsWith("Assets/"))
                .ToArray();

            if (prefabPaths.Length > 0)
                foreach (string dep in AssetDatabase.GetDependencies(prefabPaths, true))
                    used.Add(dep);

            // ── 5. ScriptableObjects (may reference assets) ──
            EditorUtility.DisplayProgressBar("Unused Asset Finder", "Collecting ScriptableObject dependencies...", 0.65f);

            string[] soGuids = AssetDatabase.FindAssets("t:ScriptableObject");
            string[] soPaths = soGuids
                .Select(g => AssetDatabase.GUIDToAssetPath(g))
                .Where(p => p.StartsWith("Assets/"))
                .ToArray();

            if (soPaths.Length > 0)
                foreach (string dep in AssetDatabase.GetDependencies(soPaths, true))
                    used.Add(dep);

            // ── 6. Find unused ──
            EditorUtility.DisplayProgressBar("Unused Asset Finder", "Comparing against all project assets...", 0.8f);

            string[] allPaths = AssetDatabase.GetAllAssetPaths();

            foreach (string path in allPaths)
            {
                if (!path.StartsWith("Assets/")) continue; // Packages etc.
                if (AssetDatabase.IsValidFolder(path)) continue; // Folders
                if (ShouldProtect(path)) continue; // Protected paths
                if (used.Contains(path)) continue; // Referenced

                _assets.Add(path);
                _checked.Add(false);
            }

            // Sort: by extension, then by path
            var sorted = _assets
                .Select((p, i) => (path: p, sel: _checked[i]))
                .OrderBy(x => Path.GetExtension(x.path).ToLower())
                .ThenBy(x => x.path)
                .ToList();

            _assets = sorted.Select(x => x.path).ToList();
            _checked = sorted.Select(x => x.sel).ToList();
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[UnusedAssetFinder] Scan failed: {e}");
            EditorUtility.DisplayDialog("Scan Error", e.Message, "OK");
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        _scanned = true;
        Debug.Log($"[UnusedAssetFinder] Done. {_assets.Count} potentially unused assets found.");
        Repaint();
    }

    // ── Protection rules ──────────────────────────────────────────────────
    private static bool ShouldProtect(string path)
    {
        // Protected folders
        if (path.Contains("/Resources/")) return true; // Runtime string-loaded
        if (path.Contains("/StreamingAssets/")) return true; // Always runtime
        if (path.Contains("/Plugins/")) return true; // Native libs
        if (path.Contains("/Editor/")) return true; // Editor-only
        if (path.Contains("/Gizmos/")) return true; // Editor gizmos
        if (path.Contains("Editor Default Resources")) return true;
        if (path.Contains("/com.unity.")) return true; // Package cache

        string ext = Path.GetExtension(path).ToLower();

        // Code -- may be used via reflection / AddComponent
        if (ext is ".cs" or ".asmdef" or ".asmref" or ".dll") return true;

        // Shader includes -- NOT tracked by AssetDatabase.GetDependencies
        if (ext is ".cginc" or ".hlsl" or ".glsl" or ".compute") return true;

        // Config and data files -- Vuforia, Addressables, Unity settings
        if (ext is ".json" or ".xml" or ".yaml" or ".yml" or ".txt") return true;

        // Addressables build output
        if (ext is ".hash" or ".bin" or ".bundle") return true;

        // Vuforia
        if (ext is ".dat" or ".unitypackage") return true;

        // Scene files are roots, not targets
        if (ext == ".unity") return true;

        // Unity internal
        if (ext is ".shadervariants" or ".giparams" or ".lighting") return true;

        return false;
    }

    // ── Helpers ───────────────────────────────────────────────────────────
    private void SelectVisible(bool value)
    {
        for (int i = 0; i < _assets.Count; i++)
        {
            string ext = Path.GetExtension(_assets[i]).ToLower().TrimStart('.');
            if (string.IsNullOrEmpty(_filterExt) || ext == _filterExt)
                _checked[i] = value;
        }
    }

    private void ExportCSV()
    {
        string savePath = EditorUtility.SaveFilePanel(
            "Export Unused Assets List", "", "unused_assets.csv", "csv");
        if (string.IsNullOrEmpty(savePath)) return;

        string projectRoot = Directory.GetParent(Application.dataPath).FullName;
        var lines = new List<string> { "Extension,Size_KB,Path" };

        foreach (string assetPath in _assets)
        {
            string full = Path.Combine(projectRoot, assetPath.Replace("/", Path.DirectorySeparatorChar.ToString()));
            long sizeKB = File.Exists(full) ? new FileInfo(full).Length / 1024 : 0;
            string ext = Path.GetExtension(assetPath);
            lines.Add($"{ext},{sizeKB},\"{assetPath}\"");
        }

        File.WriteAllLines(savePath, lines);
        Debug.Log($"[UnusedAssetFinder] CSV saved to: {savePath}");
        EditorUtility.RevealInFinder(savePath);
    }

    private void Delete()
    {
        var targets = new List<string>();
        for (int i = 0; i < _assets.Count; i++)
            if (_checked[i]) targets.Add(_assets[i]);

        int deleted = 0, failed = 0;

        try
        {
            for (int i = 0; i < targets.Count; i++)
            {
                EditorUtility.DisplayProgressBar(
                    "Deleting...", targets[i], (float)i / targets.Count);

                if (AssetDatabase.DeleteAsset(targets[i]))
                {
                    _assets.Remove(targets[i]);
                    deleted++;
                }
                else
                {
                    Debug.LogWarning($"[UnusedAssetFinder] Could not delete: {targets[i]}");
                    failed++;
                }
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        _checked = new List<bool>(Enumerable.Repeat(false, _assets.Count));
        AssetDatabase.Refresh();

        string msg = $"Deleted: {deleted}";
        if (failed > 0) msg += $"\nFailed (see Console): {failed}";
        Debug.Log($"[UnusedAssetFinder] {msg}");
        EditorUtility.DisplayDialog("Delete Complete", msg, "OK");
        Repaint();
    }
}