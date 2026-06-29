using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Custom Inspector for ARWindowManager.
/// Auto-populates the full page list the first time you select ARWindowManager in the Inspector.
/// You can also re-run it any time via the "Re-Setup All Pages" button.
/// </summary>
[CustomEditor(typeof(ARWindowManager))]
public class ARWindowManagerEditor : Editor
{
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
            Debug.Log("[AR-WINDOW] Pages list re-populated.");
        }
        GUI.backgroundColor = Color.white;

        EditorGUILayout.HelpBox(
            $"Total pages: {((ARWindowManager)target).pages?.Count ?? 0}  " +
            $"(including quiz pages with no audio)\n" +
            "Position in list = page index used by the window calculation.",
            MessageType.Info);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Full page table — addressableKey + pageId for every page in the book
    // Quiz pages have empty pageId (no audio pack exists for them)
    // ─────────────────────────────────────────────────────────────────────────

    public static void PopulatePages(ARWindowManager mgr)
    {
        mgr.pages = new List<ARWindowManager.PageEntry>
        {
            // ── Story 1  (pages intro → 22) ──────────────────────────────────
            P("Story_1_page_intro",   "S1_P-intro" ),
            P("Story_1_page_4-5",     "S1_P-4-5"  ),
            P("Story_1_page_6-7",     "S1_P-6-7"  ),
            P("Story_1_page_8-9",     "S1_P-8-9"  ),
            P("Story_1_page_10-11",   "S1_P-10-11"),
            P("Story_1_page_12-13",   "S1_P-12-13"),
            P("Story_1_page_14-15",   "S1_P-14-15"),
            P("Story_1_page_16-17",   "S1_P-16-17"),
            P("Story_1_page_18-19",   "S1_P-18-19"),
            P("Story_1_page_20-21",   "S1_P-20-21"),
            P("Story_1_page_22-quiz", ""           ),   // quiz — no audio

            // ── Story 2  (pages 24 → 46) ─────────────────────────────────────
            P("Story_1_page_24-25",   "S2_P24-25" ),
            P("Story_1_page_26-27",   "S2_P26-27" ),
            P("Story_1_page_28-29",   "S2_P28-29" ),
            P("Story_1_page_30-31",   "S2_P30-31" ),
            P("Story_1_page_32-33",   "S2_P32-33" ),
            P("Story_1_page_34-35",   "S2_P34-35" ),
            P("Story_1_page_36-37",   "S2_P36-37" ),
            P("Story_1_page_38-39",   "S2_P38-39" ),
            P("Story_1_page_40-41",   "S2_P40-41" ),
            P("Story_1_page_42-43",   "S2_P42-43" ),
            P("Story_1_page_44-45",   "S2_P44-45" ),
            P("Story_1_page_46-quiz", ""           ),   // quiz — no audio

            // ── Story 3  (pages 48 → 64) ─────────────────────────────────────
            P("Story_1_page_48-49",   "S3_P48-49" ),
            P("Story_1_page_50-51",   "S3_P50-51" ),
            P("Story_1_page_52-53",   "S3_P52-53" ),
            P("Story_1_page_54-55",   "S3_P54-55" ),
            P("Story_1_page_56-57",   "S3_P56-57" ),
            P("Story_1_page_58-59",   "S3_P58-59" ),
            P("Story_1_page_60-61",   "S3_P60-61" ),
            P("Story_1_page_62-63",   "S3_P62-63" ),
            P("Story_1_page_64-quiz", ""           ),   // quiz — no audio

            // ── Story 4  (pages 66 → 84) — pages 70-71 do not exist ──────────
            P("Story_1_page_66-67",   "S4_P66-67" ),
            P("Story_1_page_68-69",   "S4_P68-69" ),
            P("Story_1_page_72-73",   "S4_P72-73" ),
            P("Story_1_page_74-75",   "S4_P74-75" ),
            P("Story_1_page_76-77",   "S4_P76-77" ),
            P("Story_1_page_78-79",   "S4_P78-79" ),
            P("Story_1_page_80-81",   "S4_P80-81" ),
            P("Story_1_page_82-83",   "S4_P82-83" ),
            P("Story_1_page_84-quiz", ""           ),   // quiz — no audio

            // ── Story 5  (pages 86 → 108) ────────────────────────────────────
            P("Story_1_page_86-87",   "S5_P86-87"  ),
            P("Story_1_page_88-89",   "S5_P88-89"  ),
            P("Story_1_page_90-91",   "S5_P90-91"  ),
            P("Story_1_page_92-93",   "S5_P92-93"  ),
            P("Story_1_page_94-95",   "S5_P94-95"  ),
            P("Story_1_page_96-97",   "S5_P96-97"  ),
            P("Story_1_page_98-99",   "S5_P98-99"  ),
            P("Story_1_page_100-101", "S5_P100-101"),
            P("Story_1_page_102-103", "S5_P102-103"),
            P("Story_1_page_104-105", "S5_P104-105"),
            P("Story_1_page_106-107", "S5_P106-107"),
            P("Story_1_page_108-quiz", ""           ),  // quiz — no audio
        };
    }

    static ARWindowManager.PageEntry P(string addressableKey, string pageId) =>
        new ARWindowManager.PageEntry { addressableKey = addressableKey, pageId = pageId };
}
