using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(ARTrackedPageNode))]
public class ARTrackedPageNodeEditor : Editor
{
    private bool _advBgm;
    private bool _advPageEnd;
    private readonly List<bool> _modelSlotFoldouts = new List<bool>();
    private bool _adv3DVideo;

    private SerializedProperty _pageId;
    private SerializedProperty _mediaManager;
    private SerializedProperty _pageType;
    private SerializedProperty _layerRoot2D;
    private SerializedProperty _commonBackgroundImage;
    private SerializedProperty _storyParts;
    private SerializedProperty _mainVideos;
    private SerializedProperty _backgroundLoopVideos;
    private SerializedProperty _mainVideoSettings;
    private SerializedProperty _backgroundVideoSettings;
    private SerializedProperty _animators;
    private SerializedProperty _splineMovers;
    private SerializedProperty _splinePathMovers;
    private SerializedProperty _modelSlots;
    private SerializedProperty _pageEndTrigger3D;
    private SerializedProperty _loopBgmUntilVoiceEnds;
    private SerializedProperty _stopBgmWhenVoiceEnds;
    private SerializedProperty _blackOverlay;
    private SerializedProperty _contentRoot;
    private SerializedProperty _delayBeforeFade;
    private SerializedProperty _fadeDuration;
    private SerializedProperty _postFadeDelay;
    private SerializedProperty _pageTurnSound;

    private void OnEnable()
    {
        _pageId                  = serializedObject.FindProperty("pageId");
        _mediaManager            = serializedObject.FindProperty("mediaManager");
        _pageType                = serializedObject.FindProperty("pageType");
        _layerRoot2D             = serializedObject.FindProperty("layerRoot2D");
        _commonBackgroundImage   = serializedObject.FindProperty("commonBackgroundImage");
        _storyParts              = serializedObject.FindProperty("storyParts");
        _mainVideos              = serializedObject.FindProperty("mainVideos");
        _backgroundLoopVideos    = serializedObject.FindProperty("backgroundLoopVideos");
        _mainVideoSettings       = serializedObject.FindProperty("mainVideoSettings");
        _backgroundVideoSettings = serializedObject.FindProperty("backgroundVideoSettings");
        _animators               = serializedObject.FindProperty("animators");
        _splineMovers            = serializedObject.FindProperty("splineMovers");
        _splinePathMovers        = serializedObject.FindProperty("splinePathMovers");
        _modelSlots              = serializedObject.FindProperty("modelSlots");
        _pageEndTrigger3D        = serializedObject.FindProperty("pageEndTrigger3D");
        _loopBgmUntilVoiceEnds   = serializedObject.FindProperty("loopBgmUntilVoiceEnds");
        _stopBgmWhenVoiceEnds    = serializedObject.FindProperty("stopBgmWhenVoiceEnds");
        _blackOverlay            = serializedObject.FindProperty("blackOverlay");
        _contentRoot             = serializedObject.FindProperty("contentRoot");
        _delayBeforeFade         = serializedObject.FindProperty("delayBeforeFade");
        _fadeDuration            = serializedObject.FindProperty("fadeDuration");
        _postFadeDelay           = serializedObject.FindProperty("postFadeDelay");
        _pageTurnSound           = serializedObject.FindProperty("pageTurnSound");
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        EditorGUILayout.PropertyField(_pageId,       new GUIContent("Page Id"));
        EditorGUILayout.PropertyField(_mediaManager, new GUIContent("Media Manager"));
        EditorGUILayout.PropertyField(_pageType,     new GUIContent("Page Type"));

        EditorGUILayout.Space(8);

        if ((PageType)_pageType.enumValueIndex == PageType.TwoD)
            Draw2D();
        else
            Draw3D();

        EditorGUILayout.Space(8);
        _advBgm = EditorGUILayout.Foldout(_advBgm, "Advanced: BGM", true, EditorStyles.foldoutHeader);
        if (_advBgm)
        {
            EditorGUILayout.PropertyField(_loopBgmUntilVoiceEnds, new GUIContent("Loop BGM Until Voice Ends"));
            EditorGUILayout.PropertyField(_stopBgmWhenVoiceEnds,  new GUIContent("Stop BGM When Voice Ends"));
        }

        EditorGUILayout.Space(2);
        _advPageEnd = EditorGUILayout.Foldout(_advPageEnd, "Advanced: Page End Effect", true, EditorStyles.foldoutHeader);
        if (_advPageEnd)
        {
            EditorGUILayout.PropertyField(_blackOverlay,    new GUIContent("Black Overlay"));
            EditorGUILayout.PropertyField(_contentRoot,     new GUIContent("Content Root"));
            EditorGUILayout.PropertyField(_delayBeforeFade, new GUIContent("Delay Before Fade"));
            EditorGUILayout.PropertyField(_fadeDuration,    new GUIContent("Fade Duration"));
            EditorGUILayout.PropertyField(_postFadeDelay,   new GUIContent("Post Fade Delay"));
            EditorGUILayout.PropertyField(_pageTurnSound,   new GUIContent("Page Turn Sound"));
        }

        serializedObject.ApplyModifiedProperties();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 2D
    // ─────────────────────────────────────────────────────────────────────────

    private void Draw2D()
    {
        EditorGUILayout.PropertyField(_layerRoot2D,           new GUIContent("Layer Root"));
        EditorGUILayout.PropertyField(_commonBackgroundImage, new GUIContent(
            "Shared Background",
            "Shown at page start, stays visible across ALL video slots. Never hides between slots."));

        DrawBar("VIDEO SLOTS  —  Slot 1 plays first, then Slot 2, then Slot 3 ...");

        for (int i = 0; i < _storyParts.arraySize; i++)
        {
            if (DrawSlotCard(_storyParts.GetArrayElementAtIndex(i), i))
                break;
            EditorGUILayout.Space(6);
        }

        EditorGUILayout.Space(4);
        if (GUILayout.Button("+ Add Video Slot", GUILayout.Height(32)))
        {
            _storyParts.InsertArrayElementAtIndex(_storyParts.arraySize);
            var s = _storyParts.GetArrayElementAtIndex(_storyParts.arraySize - 1);

            var mv = s.FindPropertyRelative("mainVideos");
            if (mv.arraySize == 0) mv.InsertArrayElementAtIndex(0);

            var bv = s.FindPropertyRelative("backgroundVideos");
            if (bv.arraySize == 0)
            {
                bv.InsertArrayElementAtIndex(0);
                var entry = bv.GetArrayElementAtIndex(0);
                entry.FindPropertyRelative("loop").boolValue         = true;
                entry.FindPropertyRelative("stopAtPartEnd").boolValue = true;
            }

            serializedObject.ApplyModifiedProperties();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Slot Card
    // ─────────────────────────────────────────────────────────────────────────

    // Returns true if the array was modified so the caller can break the loop.
    private bool DrawSlotCard(SerializedProperty slot, int index)
    {
        SerializedProperty mainVids  = slot.FindPropertyRelative("mainVideos");
        SerializedProperty bgImage   = slot.FindPropertyRelative("backgroundImage");
        SerializedProperty bgVids    = slot.FindPropertyRelative("backgroundVideos");
        SerializedProperty timing    = slot.FindPropertyRelative("timing");
        SerializedProperty manualDur = slot.FindPropertyRelative("manualDuration");

        // Auto-ensure one entry exists in each list so the fields always show.
        if (mainVids.arraySize == 0)
            mainVids.InsertArrayElementAtIndex(0);
        if (bgVids.arraySize == 0)
        {
            bgVids.InsertArrayElementAtIndex(0);
            bgVids.GetArrayElementAtIndex(0).FindPropertyRelative("loop").boolValue         = true;
            bgVids.GetArrayElementAtIndex(0).FindPropertyRelative("stopAtPartEnd").boolValue = true;
        }

        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            // ── Slot header ───────────────────────────────────────────────────
            Rect hdr = EditorGUILayout.GetControlRect(false, 28);
            EditorGUI.DrawRect(hdr, new Color(0.12f, 0.28f, 0.50f, 0.90f));

            const float btnW = 26f;
            const float gap  = 2f;
            var delR = new Rect(hdr.xMax - btnW,             hdr.y + 2, btnW, hdr.height - 4);
            var dnR  = new Rect(hdr.xMax - btnW * 2 - gap,   hdr.y + 2, btnW, hdr.height - 4);
            var upR  = new Rect(hdr.xMax - btnW * 3 - gap*2, hdr.y + 2, btnW, hdr.height - 4);
            var lblR = new Rect(hdr.x + 6, hdr.y, hdr.width - btnW * 3 - gap * 2 - 8, hdr.height);

            EditorGUI.LabelField(lblR, $"SLOT {index + 1}", EditorStyles.boldLabel);

            if (GUI.Button(upR, "▲") && index > 0)
            {
                _storyParts.MoveArrayElement(index, index - 1);
                serializedObject.ApplyModifiedProperties();
                return true;
            }
            if (GUI.Button(dnR, "▼") && index < _storyParts.arraySize - 1)
            {
                _storyParts.MoveArrayElement(index, index + 1);
                serializedObject.ApplyModifiedProperties();
                return true;
            }
            if (GUI.Button(delR, "X"))
            {
                _storyParts.DeleteArrayElementAtIndex(index);
                serializedObject.ApplyModifiedProperties();
                return true;
            }

            EditorGUI.indentLevel++;
            EditorGUILayout.Space(6);

            // ── Main Video ────────────────────────────────────────────────────
            SerializedProperty mv = mainVids.GetArrayElementAtIndex(0);
            EditorGUILayout.PropertyField(mv.FindPropertyRelative("video"),
                new GUIContent("Main Video", "Drag the VideoPlayer here."));
            EditorGUILayout.Slider(mv.FindPropertyRelative("playbackSpeed"), 0.1f, 3f,
                new GUIContent("Speed", "1 = normal speed.  < 1 = slower.  > 1 = faster."));
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PropertyField(mv.FindPropertyRelative("freezeFirstSeconds"), new GUIContent("Freeze First (s)"));
                EditorGUILayout.PropertyField(mv.FindPropertyRelative("freezeLastSeconds"),  new GUIContent("Freeze Last (s)"));
            }

            EditorGUILayout.Space(8);

            // ── Background ────────────────────────────────────────────────────
            EditorGUILayout.LabelField("Background  (stops when main video ends)", EditorStyles.miniBoldLabel);

            EditorGUILayout.PropertyField(bgImage,
                new GUIContent("Image", "Drag a background image / sprite here. Hides when slot ends."));

            SerializedProperty bv0 = bgVids.GetArrayElementAtIndex(0);
            EditorGUILayout.PropertyField(bv0.FindPropertyRelative("video"),
                new GUIContent("Video", "Drag a background VideoPlayer here. Stops when slot ends."));
            EditorGUILayout.Slider(bv0.FindPropertyRelative("playbackSpeed"), 0.1f, 3f,
                new GUIContent("BG Speed", "1 = normal speed."));
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PropertyField(bv0.FindPropertyRelative("freezeFirstSeconds"), new GUIContent("BG Freeze First (s)"));
                EditorGUILayout.PropertyField(bv0.FindPropertyRelative("freezeLastSeconds"),  new GUIContent("BG Freeze Last (s)"));
            }

            EditorGUILayout.Space(8);

            // ── When does this slot end? ──────────────────────────────────────
            EditorGUILayout.LabelField("When does this slot end?", EditorStyles.miniBoldLabel);
            string[] timingLabels = { "When main video finishes", "After fixed duration (seconds)", "When voice audio ends", "When user taps screen" };
            timing.enumValueIndex = EditorGUILayout.Popup("Slot ends", timing.enumValueIndex, timingLabels);

            // Read mode AFTER the popup so it reflects the value just chosen.
            PartTiming2D mode = (PartTiming2D)timing.enumValueIndex;
            if (mode == PartTiming2D.ManualDuration)
            {
                EditorGUILayout.PropertyField(manualDur, new GUIContent("Duration (seconds)"));
            }
            else if (mode == PartTiming2D.AutoFromMainVideo &&
                     mv.FindPropertyRelative("video").objectReferenceValue == null)
            {
                EditorGUILayout.HelpBox("No video assigned — set a Duration as fallback.", MessageType.Warning);
                EditorGUILayout.PropertyField(manualDur, new GUIContent("Duration (seconds)"));
            }

            EditorGUILayout.Space(4);
            EditorGUI.indentLevel--;
        }

        return false;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 3D
    // ─────────────────────────────────────────────────────────────────────────

    private void Draw3D()
    {
        DrawBar("3D PAGE");

        DrawSimpleList(_animators,        "Animators");
        DrawSimpleList(_splineMovers,     "Spline Movers");
        DrawSimpleList(_splinePathMovers, "Spline Path Movers");

        EditorGUILayout.Space(4);
        DrawModelSlots();

        EditorGUILayout.Space(6);
        EditorGUILayout.PropertyField(_pageEndTrigger3D, new GUIContent("Page End Trigger"));
        PageEndTrigger3D trig = (PageEndTrigger3D)_pageEndTrigger3D.enumValueIndex;
        if (trig == PageEndTrigger3D.AnimationEvent)
            EditorGUILayout.HelpBox("Call TriggerPageEnd() from an Animation Event.", MessageType.Info);
        else if (trig == PageEndTrigger3D.Manual)
            EditorGUILayout.HelpBox("Call StartPageEndFade() from a script.", MessageType.Info);

        EditorGUILayout.Space(4);
        _adv3DVideo = EditorGUILayout.Foldout(_adv3DVideo, "Old Video Data", true, EditorStyles.foldoutHeader);
        if (_adv3DVideo)
            DrawOldData();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Old Data — collapsed, preserves existing prefab assignments
    // ─────────────────────────────────────────────────────────────────────────

    private void DrawOldData()
    {
        EditorGUILayout.HelpBox("Old video assignments. Do not clear unless fully moved to Video Slots above.", MessageType.None);

        for (int i = 0; i < _mainVideos.arraySize; i++)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField($"Main Video {i + 1}", EditorStyles.miniBoldLabel);
                EditorGUILayout.PropertyField(_mainVideos.GetArrayElementAtIndex(i), new GUIContent("Video Player"));
                if (i < _mainVideoSettings.arraySize)
                {
                    var s = _mainVideoSettings.GetArrayElementAtIndex(i);
                    EditorGUI.indentLevel++;
                    EditorGUILayout.PropertyField(s.FindPropertyRelative("playbackSpeed"),      new GUIContent("Speed"));
                    EditorGUILayout.PropertyField(s.FindPropertyRelative("freezeFirstSeconds"), new GUIContent("Freeze First (s)"));
                    EditorGUILayout.PropertyField(s.FindPropertyRelative("freezeLastSeconds"),  new GUIContent("Freeze Last (s)"));
                    EditorGUILayout.PropertyField(s.FindPropertyRelative("startDelay"),         new GUIContent("Start Delay"));
                    EditorGUILayout.PropertyField(s.FindPropertyRelative("waitForPageEnd"),     new GUIContent("Wait For Page End"));
                    EditorGUI.indentLevel--;
                }
            }
        }

        SyncSize(_mainVideos, _mainVideoSettings);
        EditorGUILayout.Space(4);

        for (int i = 0; i < _backgroundLoopVideos.arraySize; i++)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField($"BG Video {i + 1}", EditorStyles.miniBoldLabel);
                EditorGUILayout.PropertyField(_backgroundLoopVideos.GetArrayElementAtIndex(i), new GUIContent("Video Player"));
                if (i < _backgroundVideoSettings.arraySize)
                {
                    var s = _backgroundVideoSettings.GetArrayElementAtIndex(i);
                    EditorGUI.indentLevel++;
                    EditorGUILayout.PropertyField(s.FindPropertyRelative("playbackSpeed"), new GUIContent("Speed"));
                    EditorGUILayout.PropertyField(s.FindPropertyRelative("startDelay"),    new GUIContent("Start Delay"));
                    EditorGUI.indentLevel--;
                }
            }
        }

        SyncSize(_backgroundLoopVideos, _backgroundVideoSettings);
    }

    private void SyncSize(SerializedProperty vids, SerializedProperty settings)
    {
        if (vids.arraySize == settings.arraySize) return;
        EditorGUILayout.HelpBox("Count mismatch.", MessageType.Warning);
        if (GUILayout.Button("Sync"))
        {
            while (settings.arraySize < vids.arraySize) settings.InsertArrayElementAtIndex(settings.arraySize);
            while (settings.arraySize > vids.arraySize) settings.DeleteArrayElementAtIndex(settings.arraySize - 1);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private void DrawSimpleList(SerializedProperty list, string title)
    {
        EditorGUILayout.Space(2);
        EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
        for (int i = 0; i < list.arraySize; i++)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PropertyField(list.GetArrayElementAtIndex(i), new GUIContent($"{i + 1}"));
                if (GUILayout.Button("X", GUILayout.Width(24)))
                {
                    // Unity object-reference arrays: first delete nulls the ref; second removes the element.
                    if (list.GetArrayElementAtIndex(i).objectReferenceValue != null)
                        list.DeleteArrayElementAtIndex(i);
                    list.DeleteArrayElementAtIndex(i);
                    break;
                }
            }
        }
        if (GUILayout.Button($"+ Add {title}"))
            list.InsertArrayElementAtIndex(list.arraySize);
    }

    // Model Slots — plain fields, no card/color styling, just clearer labels than
    // Unity's default "Element 0" so each slot is easy to tell apart at a glance.
    private void DrawModelSlots()
    {
        EditorGUILayout.LabelField("Model Slots (optional) — shows one 3D model at a time, in order", EditorStyles.boldLabel);

        while (_modelSlotFoldouts.Count < _modelSlots.arraySize) _modelSlotFoldouts.Add(false);
        while (_modelSlotFoldouts.Count > _modelSlots.arraySize) _modelSlotFoldouts.RemoveAt(_modelSlotFoldouts.Count - 1);

        for (int i = 0; i < _modelSlots.arraySize; i++)
        {
            SerializedProperty slot  = _modelSlots.GetArrayElementAtIndex(i);
            SerializedProperty model = slot.FindPropertyRelative("model");
            string modelName = model.objectReferenceValue != null ? model.objectReferenceValue.name : "no model assigned";

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    _modelSlotFoldouts[i] = EditorGUILayout.Foldout(
                        _modelSlotFoldouts[i], $"Slot {i + 1} — {modelName}", true, EditorStyles.foldoutHeader);

                    if (GUILayout.Button("X", GUILayout.Width(24)))
                    {
                        _modelSlots.DeleteArrayElementAtIndex(i);
                        _modelSlotFoldouts.RemoveAt(i);
                        break;
                    }
                }

                if (!_modelSlotFoldouts[i]) continue;

                EditorGUILayout.PropertyField(model, new GUIContent("Model"));
                EditorGUILayout.PropertyField(slot.FindPropertyRelative("position"), new GUIContent("Position"));

                SerializedProperty matchAnim = slot.FindPropertyRelative("matchAnimationLength");
                EditorGUILayout.PropertyField(matchAnim, new GUIContent("Match Animation Length"));
                using (new EditorGUI.DisabledScope(matchAnim.boolValue))
                    EditorGUILayout.PropertyField(slot.FindPropertyRelative("showDuration"), new GUIContent("Show Duration"));

                EditorGUILayout.PropertyField(slot.FindPropertyRelative("gapBeforeNext"), new GUIContent("Gap Before Next"));

                SerializedProperty scaleInOut = slot.FindPropertyRelative("scaleInOut");
                EditorGUILayout.PropertyField(scaleInOut, new GUIContent("Scale In/Out"));
                using (new EditorGUI.DisabledScope(!scaleInOut.boolValue))
                    EditorGUILayout.PropertyField(slot.FindPropertyRelative("scaleDuration"), new GUIContent("Scale Duration"));

                SerializedProperty sfxClip = slot.FindPropertyRelative("sfxClip");
                EditorGUILayout.PropertyField(sfxClip, new GUIContent("SFX Clip"));
                using (new EditorGUI.DisabledScope(sfxClip.objectReferenceValue == null))
                    EditorGUILayout.PropertyField(slot.FindPropertyRelative("sfxVolume"), new GUIContent("SFX Volume"));
            }
            EditorGUILayout.Space(3);
        }

        if (GUILayout.Button("+ Add Model Slot"))
        {
            _modelSlots.InsertArrayElementAtIndex(_modelSlots.arraySize);
            _modelSlotFoldouts.Add(true); // new slot starts expanded so you can fill it in right away
        }
    }

    private static void DrawBar(string title)
    {
        EditorGUILayout.Space(4);
        Rect r = EditorGUILayout.GetControlRect(false, 22);
        EditorGUI.DrawRect(r, new Color(0.15f, 0.15f, 0.15f, 0.6f));
        EditorGUI.LabelField(r, "  " + title, EditorStyles.boldLabel);
        EditorGUILayout.Space(2);
    }
}
