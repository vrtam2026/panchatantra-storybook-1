using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Inspector for a Language asset. One button does everything for this language:
/// finds and connects any real recordings sitting in the project (same as
/// Connect Audio To Pages, but only for this language), then fills whatever is
/// still missing with a placeholder sound -- so you never have to decide which
/// of two tools to use. Nothing here is hardcoded to any specific language.
/// </summary>
[CustomEditor(typeof(ARStorybookLanguage))]
public class ARStorybookLanguageEditor : Editor
{
    private const string CatalogAssetPath = "Assets/code/AudioLanguageCatalog.asset";

    public override void OnInspectorGUI()
    {
        var lang = (ARStorybookLanguage)target;
        string name = lang.LanguageName;

        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Language", EditorStyles.boldLabel);
        EditorGUILayout.LabelField(name, EditorStyles.largeLabel);
        EditorGUILayout.Space(8);

        var catalog = AssetDatabase.LoadAssetAtPath<ARAddressableAudioCatalog>(CatalogAssetPath);
        var pages = PageIdentityUtility.GetAllPages()
            .Where(p => !p.addressableKey.ToLowerInvariant().Contains("quiz") && !string.IsNullOrEmpty(p.pageId))
            .ToList();

        if (pages.Count == 0)
        {
            EditorGUILayout.HelpBox(
                "No pages found in the open scene, so page counts can't be shown right now. " +
                "Open the scene with your page markers to see status here.",
                MessageType.Info);
        }
        else
        {
            int have = catalog != null ? pages.Count(p => catalog.HasEntry(name, p.pageId)) : 0;
            EditorGUILayout.LabelField($"{have} of {pages.Count} pages have {name} audio");
        }

        EditorGUILayout.Space(10);

        bool isReferenceLanguage = name.Equals("English", StringComparison.OrdinalIgnoreCase);
        if (isReferenceLanguage)
        {
            EditorGUILayout.HelpBox(
                "English is treated as the reference language -- other languages' test buttons copy " +
                "voices from it. No setup button needed here; use Connect Audio To Pages in the Tools " +
                "menu after adding English recordings.",
                MessageType.None);
        }
        else
        {
            GUI.backgroundColor = new Color(0.4f, 0.8f, 0.4f);
            if (GUILayout.Button($"Set Up {name} Audio", GUILayout.Height(32)))
            {
                bool run = EditorUtility.DisplayDialog($"Set Up {name} Audio",
                    $"This will, for {name} only:\n\n" +
                    "1. Find and connect any real recordings already in the project\n" +
                    "2. Fill everything still missing with a placeholder sound\n\n" +
                    $"Pages that already have real {name} audio are never touched or overwritten. " +
                    "Safe to click any time, from any state -- if you deleted everything, this rebuilds " +
                    "it all from scratch.",
                    "Set It Up", "Cancel");

                if (run)
                {
                    var (connected, _) = AudioAddressableSetupTool.SetupForLanguage(name);
                    int filled = ARLanguagePlaceholderFiller.FillGaps(name, out _);
                    EditorUtility.DisplayDialog($"{name} Audio Set Up",
                        $"Connected {connected} real recording(s).\n" +
                        $"Filled {filled} remaining page(s) with placeholder sound so nothing is silent.",
                        "OK");
                }
            }
            GUI.backgroundColor = Color.white;
            EditorGUILayout.LabelField(
                $"Put {name}'s recordings in a folder named \"{name}\" anywhere under Assets " +
                "(e.g. Assets/Audio/" + name + "/), named by page range (\"45-46.mp3\") or page order " +
                "(\"page1.mp3\", \"page2.mp3\" ...). Then click this button.",
                EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.Space(8);

            if (GUILayout.Button("Test Voices (wrong on purpose)", GUILayout.Height(26)))
            {
                bool run = EditorUtility.DisplayDialog("Test Voices",
                    $"This copies English voices onto {name} pages that have NO real audio yet, " +
                    "shuffled so it's obvious you're hearing test audio, not a translation.\n\n" +
                    $"Pages that already have real {name} audio are never touched.",
                    "Run it", "Cancel");
                if (run) ARLanguageVoiceShuffler.Run(name);
            }
            EditorGUILayout.LabelField(
                "Optional extra test: gives pages shuffled English voices so it's obvious you're hearing " +
                "test audio, instead of the same placeholder sound everywhere.",
                EditorStyles.wordWrappedMiniLabel);
        }

        EditorGUILayout.Space(10);
        EditorGUILayout.HelpBox(
            "To remove this language: select it in the Project window and press Delete. " +
            "Recordings and audio packs are never deleted -- only this marker.",
            MessageType.None);
    }
}
