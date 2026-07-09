using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEngine;

/// <summary>
/// Visual checker for the audio system. Shows every audio pack in the project
/// with a clear ✔ (correctly set up, can actually be played) or ✘ (missing the
/// "Addressable" registration step, so it will NOT play even though its
/// recording exists and is connected).
///
/// This is the "show me what's not connected" tool — use "Fix All" to
/// automatically register anything showing ✘, no manual work needed.
///
/// Menu: Tools → AR Storybook → Show Which Pages Are Missing Audio
/// </summary>
public class AudioPackStatusWindow : EditorWindow
{
    private Vector2 _scroll;
    private const string PackOutputFolder = "Assets/code/AudioPacks";
    private const string DefaultGroupName = "Audio_Unassigned";

    private class PackRow
    {
        public string path;
        public string guid;
        public string languageName;
        public string pageId;
        public bool isRegistered;
    }

    private List<PackRow> _rows = new List<PackRow>();

    [MenuItem("Tools/AR Storybook/Show Which Pages Are Missing Audio", false, 4)]
    public static void ShowWindow()
    {
        var window = GetWindow<AudioPackStatusWindow>("Audio Pack Status");
        window.minSize = new Vector2(500, 300);
        window.Refresh();
    }

    private void OnEnable() => Refresh();

    private void Refresh()
    {
        _rows.Clear();

        AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.Settings;

        string[] guids = AssetDatabase.FindAssets("t:ARPageAudioPack");
        foreach (string guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            ARPageAudioPack pack = AssetDatabase.LoadAssetAtPath<ARPageAudioPack>(path);
            if (pack == null) continue;

            bool registered = settings != null && settings.FindAssetEntry(guid) != null;

            _rows.Add(new PackRow
            {
                path = path,
                guid = guid,
                languageName = pack.languageName,
                pageId = pack.pageId,
                isRegistered = registered
            });
        }

        _rows = _rows.OrderBy(r => r.languageName).ThenBy(r => r.pageId).ToList();
    }

    private void OnGUI()
    {
        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("Audio Pack Status", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "✔ = this page's audio is correctly registered and will play.\n" +
            "✘ = the recording exists and is connected, but was never added to the app's downloadable-content list — it will NOT play until fixed.",
            MessageType.Info);

        int missingCount = _rows.Count(r => !r.isRegistered);

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Refresh", GUILayout.Height(28)))
                Refresh();

            GUI.enabled = missingCount > 0;
            GUI.backgroundColor = missingCount > 0 ? new Color(0.9f, 0.6f, 0.2f) : Color.white;
            if (GUILayout.Button($"Fix All ({missingCount} missing)", GUILayout.Height(28)))
                FixAll();
            GUI.backgroundColor = Color.white;
            GUI.enabled = true;
        }

        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField($"Total packs found: {_rows.Count}    Missing: {missingCount}", EditorStyles.miniLabel);
        EditorGUILayout.Space(4);

        _scroll = EditorGUILayout.BeginScrollView(_scroll);
        foreach (var row in _rows)
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                string icon = row.isRegistered ? "✔" : "✘";
                Color prevColor = GUI.color;
                GUI.color = row.isRegistered ? new Color(0.3f, 0.8f, 0.3f) : new Color(0.9f, 0.3f, 0.3f);
                GUILayout.Label(icon, GUILayout.Width(20));
                GUI.color = prevColor;

                EditorGUILayout.LabelField($"{row.languageName} — {row.pageId}");

                if (!row.isRegistered)
                {
                    if (GUILayout.Button("Fix This One", GUILayout.Width(100)))
                    {
                        FixOne(row);
                        Refresh();
                    }
                }
            }
        }
        EditorGUILayout.EndScrollView();
    }

    private void FixAll()
    {
        foreach (var row in _rows.Where(r => !r.isRegistered).ToList())
            FixOne(row);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Refresh();

        EditorUtility.DisplayDialog("Audio Pack Status", "Done — every missing pack has been added to the downloadable-content list.", "OK");
    }

    private void FixOne(PackRow row)
    {
        AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.Settings;
        if (settings == null)
        {
            Debug.LogError("[Audio Status] Addressable Asset Settings not found. Open Window → Asset Management → Addressables → Groups first.");
            return;
        }

        string groupName = $"Audio_{row.languageName}";
        AddressableAssetGroup group = settings.FindGroup(groupName);
        if (group == null)
        {
            group = settings.CreateGroup(groupName, false, false, true, null);
            var bundled = group.AddSchema<BundledAssetGroupSchema>();
            bundled.BundleMode = BundledAssetGroupSchema.BundlePackingMode.PackSeparately;
            bundled.BuildPath.SetVariableByName(settings, AddressableAssetSettings.kRemoteBuildPath);
            bundled.LoadPath.SetVariableByName(settings, AddressableAssetSettings.kRemoteLoadPath);
            group.AddSchema<ContentUpdateGroupSchema>();
        }

        AddressableAssetEntry entry = settings.CreateOrMoveEntry(row.guid, group, false, false);
        entry.address = $"audio/{row.languageName}/{row.pageId}";

        row.isRegistered = true;
    }
}
