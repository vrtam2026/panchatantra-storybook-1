using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Video;

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
        // During a script recompile or prefab reimport, Unity re-runs OnEnable for the
        // still-selected object before that object has been rebuilt -- target is null for
        // a moment. Touching serializedObject then throws SerializedObjectNotCreatableException.
        // Bail out; Unity calls OnEnable again once the real object is back.
        if (target == null) return;

        _pageId = serializedObject.FindProperty("pageId");
        _mediaManager = serializedObject.FindProperty("mediaManager");
        _pageType = serializedObject.FindProperty("pageType");
        _layerRoot2D = serializedObject.FindProperty("layerRoot2D");
        _commonBackgroundImage = serializedObject.FindProperty("commonBackgroundImage");
        _storyParts = serializedObject.FindProperty("storyParts");
        _mainVideos = serializedObject.FindProperty("mainVideos");
        _backgroundLoopVideos = serializedObject.FindProperty("backgroundLoopVideos");
        _mainVideoSettings = serializedObject.FindProperty("mainVideoSettings");
        _backgroundVideoSettings = serializedObject.FindProperty("backgroundVideoSettings");
        _animators = serializedObject.FindProperty("animators");
        _splineMovers = serializedObject.FindProperty("splineMovers");
        _splinePathMovers = serializedObject.FindProperty("splinePathMovers");
        _modelSlots = serializedObject.FindProperty("modelSlots");
        _pageEndTrigger3D = serializedObject.FindProperty("pageEndTrigger3D");
        _loopBgmUntilVoiceEnds = serializedObject.FindProperty("loopBgmUntilVoiceEnds");
        _stopBgmWhenVoiceEnds = serializedObject.FindProperty("stopBgmWhenVoiceEnds");
        _blackOverlay = serializedObject.FindProperty("blackOverlay");
        _contentRoot = serializedObject.FindProperty("contentRoot");
        _delayBeforeFade = serializedObject.FindProperty("delayBeforeFade");
        _fadeDuration = serializedObject.FindProperty("fadeDuration");
        _postFadeDelay = serializedObject.FindProperty("postFadeDelay");
        _pageTurnSound = serializedObject.FindProperty("pageTurnSound");
    }

    public override void OnInspectorGUI()
    {
        // Same reason as the guard in OnEnable: the cached properties are null if OnEnable
        // bailed out early during a recompile. Draw nothing until Unity re-runs OnEnable.
        if (target == null || _pageId == null) return;

        serializedObject.Update();

        EditorGUILayout.PropertyField(_pageId, new GUIContent("Page Id"));
        EditorGUILayout.PropertyField(_mediaManager, new GUIContent("Media Manager"));
        EditorGUILayout.PropertyField(_pageType, new GUIContent("Page Type"));

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
            EditorGUILayout.PropertyField(_stopBgmWhenVoiceEnds, new GUIContent("Stop BGM When Voice Ends"));
        }

        EditorGUILayout.Space(2);
        _advPageEnd = EditorGUILayout.Foldout(_advPageEnd, "Advanced: Page End Effect", true, EditorStyles.foldoutHeader);
        if (_advPageEnd)
        {
            EditorGUILayout.PropertyField(_blackOverlay, new GUIContent("Black Overlay"));
            EditorGUILayout.PropertyField(_contentRoot, new GUIContent("Content Root"));
            EditorGUILayout.PropertyField(_delayBeforeFade, new GUIContent("Delay Before Fade"));
            EditorGUILayout.PropertyField(_fadeDuration, new GUIContent("Fade Duration"));
            EditorGUILayout.PropertyField(_postFadeDelay, new GUIContent("Post Fade Delay"));
            EditorGUILayout.PropertyField(_pageTurnSound, new GUIContent("Page Turn Sound"));
        }

        serializedObject.ApplyModifiedProperties();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 2D
    // ─────────────────────────────────────────────────────────────────────────

    private void Draw2D()
    {
        EditorGUILayout.PropertyField(_layerRoot2D, new GUIContent("Layer Root"));
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
                entry.FindPropertyRelative("loop").boolValue = true;
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
        SerializedProperty mainVids = slot.FindPropertyRelative("mainVideos");
        SerializedProperty mainImgs = slot.FindPropertyRelative("visualLayers");
        SerializedProperty playOrder = slot.FindPropertyRelative("mainPlayOrder");
        SerializedProperty bgImage = slot.FindPropertyRelative("backgroundImage");
        SerializedProperty bgVids = slot.FindPropertyRelative("backgroundVideos");
        SerializedProperty timing = slot.FindPropertyRelative("timing");
        SerializedProperty manualDur = slot.FindPropertyRelative("manualDuration");

        // Auto-ensure one entry exists in each list so the fields always show.
        if (mainVids.arraySize == 0)
            mainVids.InsertArrayElementAtIndex(0);
        if (bgVids.arraySize == 0)
        {
            bgVids.InsertArrayElementAtIndex(0);
            bgVids.GetArrayElementAtIndex(0).FindPropertyRelative("loop").boolValue = true;
            bgVids.GetArrayElementAtIndex(0).FindPropertyRelative("stopAtPartEnd").boolValue = true;
        }

        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            // ── Slot header ───────────────────────────────────────────────────
            Rect hdr = EditorGUILayout.GetControlRect(false, 28);
            EditorGUI.DrawRect(hdr, new Color(0.12f, 0.28f, 0.50f, 0.90f));

            const float btnW = 26f;
            const float gap = 2f;
            var delR = new Rect(hdr.xMax - btnW, hdr.y + 2, btnW, hdr.height - 4);
            var dnR = new Rect(hdr.xMax - btnW * 2 - gap, hdr.y + 2, btnW, hdr.height - 4);
            var upR = new Rect(hdr.xMax - btnW * 3 - gap * 2, hdr.y + 2, btnW, hdr.height - 4);
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

            // ── What plays in this slot ───────────────────────────────────────
            if (DrawMainContentSection(mainVids, mainImgs, playOrder)) return true;

            EditorGUILayout.Space(10);

            // ── What sits behind it ──────────────────────────────────────────
            if (DrawBackgroundSection(bgImage, bgVids, mainImgs)) return true;

            EditorGUILayout.Space(10);

            SerializedProperty mv = mainVids.GetArrayElementAtIndex(0);

            // ── When does this slot end? ──────────────────────────────────────
            DrawSectionHeader("WHEN DOES THIS SLOT END");
            string[] timingLabels = { "When the videos finish", "After a set number of seconds", "When the voice audio ends", "When the user taps the screen" };
            timing.enumValueIndex = EditorGUILayout.Popup(new GUIContent("Move on"), timing.enumValueIndex, timingLabels);

            // Read mode AFTER the popup so it reflects the value just chosen.
            PartTiming2D mode = (PartTiming2D)timing.enumValueIndex;
            if (mode == PartTiming2D.ManualDuration)
            {
                EditorGUILayout.PropertyField(manualDur, new GUIContent("Seconds"));
            }
            else if (mode == PartTiming2D.AutoFromMainVideo &&
                     mv.FindPropertyRelative("video").objectReferenceValue == null)
            {
                EditorGUILayout.HelpBox("No video added yet — set a number of seconds instead.", MessageType.Warning);
                EditorGUILayout.PropertyField(manualDur, new GUIContent("Seconds"));
            }

            EditorGUILayout.Space(4);
            EditorGUI.indentLevel--;
        }

        return false;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Slot contents — one uniform, numbered list.
    //
    // Every item looks the same and is numbered in the order it plays: Item 1,
    // Item 2, Item 3... The first video is NOT a special one-off field any more --
    // it is simply Item 1 in the list, so a newcomer sees one consistent pattern
    // instead of "one main field, then some different-looking extra boxes".
    //
    // Data is unchanged: Item 1 is still mainVideos[0], which is what the runtime
    // has always used. Only the presentation changed.
    //
    // Images live in the shared "visualLayers" list (already wired at runtime for
    // show/hide/fade), tagged with isBackgroundLayer so each appears in its own
    // section on screen. That tag has no effect on how a layer behaves.
    // ─────────────────────────────────────────────────────────────────────────

    // Returns true if the array was modified (caller should stop drawing this frame).
    private bool DrawMainContentSection(SerializedProperty mainVids, SerializedProperty mainImgs, SerializedProperty playOrder)
    {
        var imageIndices = CollectLayerIndices(mainImgs, isBackground: false);
        int totalItems = mainVids.arraySize + imageIndices.Count;

        DrawSectionHeader("WHAT PLAYS IN THIS SLOT");

        // The one question that matters once there is more than one item.
        bool sequential = playOrder.enumValueIndex == (int)MainPlayOrder.OneAfterAnother;
        if (totalItems > 1)
        {
            string[] orderLabels = { "One after another", "All at the same time" };
            playOrder.enumValueIndex = EditorGUILayout.Popup(
                new GUIContent("Play these", "One after another: Item 1 finishes, then Item 2 starts, and so on.\nAll at the same time: every item starts together."),
                playOrder.enumValueIndex, orderLabels);
            sequential = playOrder.enumValueIndex == (int)MainPlayOrder.OneAfterAnother;
            EditorGUILayout.Space(4);
        }

        // Videos first, then images — numbered continuously so the order reads top to bottom.
        int itemNo = 1;
        for (int i = 0; i < mainVids.arraySize; i++, itemNo++)
        {
            bool canDelete = totalItems > 1; // always keep at least one item in the slot
            if (DrawVideoItemCard(mainVids, i, $"Item {itemNo}  —  Video", sequential, canDelete))
                return true;
        }
        for (int n = 0; n < imageIndices.Count; n++, itemNo++)
        {
            if (DrawImageItemCard(mainImgs, imageIndices[n], $"Item {itemNo}  —  Image", canDelete: true))
                return true;
        }

        // "One after another" mode: each item's start time is computed automatically
        // so it begins the moment the previous one ends. No manual math needed.
        if (sequential && totalItems > 1)
            ApplySequentialTiming(mainVids);

        DrawAddButtons(
            "+ Add Video", () =>
            {
                mainVids.InsertArrayElementAtIndex(mainVids.arraySize);
            },
            "+ Add Image", () =>
            {
                AppendImageLayer(mainImgs, isBackground: false);
            });

        return false;
    }

    private bool DrawBackgroundSection(SerializedProperty bgImage, SerializedProperty bgVids, SerializedProperty mainImgs)
    {
        var imageIndices = CollectLayerIndices(mainImgs, isBackground: true);

        DrawSectionHeader("WHAT SITS BEHIND IT   (hides when the slot ends)");

        // The original single "Background Image" field, shown as Item 1 so the
        // numbering is continuous with anything added below it.
        int itemNo = 1;
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.LabelField($"Item {itemNo}  —  Image", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(bgImage, new GUIContent("Image", "Drag a background image / sprite here."));
        }
        EditorGUILayout.Space(4);
        itemNo++;

        for (int i = 0; i < bgVids.arraySize; i++, itemNo++)
        {
            bool canDelete = bgVids.arraySize > 1; // keep at least one background video row
            if (DrawBgVideoItemCard(bgVids, i, $"Item {itemNo}  —  Video", canDelete))
                return true;
        }
        for (int n = 0; n < imageIndices.Count; n++, itemNo++)
        {
            if (DrawImageItemCard(mainImgs, imageIndices[n], $"Item {itemNo}  —  Image", canDelete: true))
                return true;
        }

        DrawAddButtons(
            "+ Add Video", () =>
            {
                bgVids.InsertArrayElementAtIndex(bgVids.arraySize);
                bgVids.GetArrayElementAtIndex(bgVids.arraySize - 1).FindPropertyRelative("loop").boolValue = true;
            },
            "+ Add Image", () =>
            {
                AppendImageLayer(mainImgs, isBackground: true);
            });

        return false;
    }

    private static void DrawSectionHeader(string title)
    {
        EditorGUILayout.Space(2);
        Rect r = EditorGUILayout.GetControlRect(false, 20);
        EditorGUI.DrawRect(r, new Color(0.20f, 0.20f, 0.20f, 0.85f));
        EditorGUI.LabelField(new Rect(r.x + 6, r.y, r.width - 8, r.height), title, EditorStyles.miniBoldLabel);
        EditorGUILayout.Space(4);
    }

    private void DrawAddButtons(string labelA, System.Action onA, string labelB, System.Action onB)
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button(labelA, GUILayout.Height(24))) { onA(); serializedObject.ApplyModifiedProperties(); }
            if (GUILayout.Button(labelB, GUILayout.Height(24))) { onB(); serializedObject.ApplyModifiedProperties(); }
        }
    }

    private static void AppendImageLayer(SerializedProperty visualLayers, bool isBackground)
    {
        visualLayers.InsertArrayElementAtIndex(visualLayers.arraySize);
        var added = visualLayers.GetArrayElementAtIndex(visualLayers.arraySize - 1);
        added.FindPropertyRelative("isBackgroundLayer").boolValue = isBackground;
        added.FindPropertyRelative("showAtPartStart").boolValue = true;
        added.FindPropertyRelative("hideAtPartEnd").boolValue = true;
    }

    // Returns indices (into the shared visualLayers list) belonging to one section,
    // so Main and Background each only draw their own images.
    private static List<int> CollectLayerIndices(SerializedProperty visualLayers, bool isBackground)
    {
        var result = new List<int>();
        for (int i = 0; i < visualLayers.arraySize; i++)
        {
            var flag = visualLayers.GetArrayElementAtIndex(i).FindPropertyRelative("isBackgroundLayer");
            if (flag.boolValue == isBackground) result.Add(i);
        }
        return result;
    }

    // Draws the "Item N — ..." title row with its X button.
    // Returns true if the item was deleted (caller should stop drawing this frame).
    private bool DrawItemHeader(SerializedProperty list, int i, string label, bool canDelete)
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.LabelField(label, EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(!canDelete))
            {
                if (GUILayout.Button(new GUIContent("X", canDelete ? "Remove this item" : "A slot needs at least one item"), GUILayout.Width(24)))
                {
                    list.DeleteArrayElementAtIndex(i);
                    serializedObject.ApplyModifiedProperties();
                    return true;
                }
            }
        }
        return false;
    }

    // Returns true if the array was modified (caller should stop iterating this frame).
    private bool DrawVideoItemCard(SerializedProperty list, int i, string label, bool sequential, bool canDelete)
    {
        SerializedProperty item = list.GetArrayElementAtIndex(i);
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            if (DrawItemHeader(list, i, label, canDelete)) return true;

            EditorGUILayout.PropertyField(item.FindPropertyRelative("video"), new GUIContent("Video"));
            EditorGUILayout.Slider(item.FindPropertyRelative("playbackSpeed"), 0.1f, 3f, new GUIContent("Speed"));
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PropertyField(item.FindPropertyRelative("freezeFirstSeconds"), new GUIContent("Freeze First (s)"));
                EditorGUILayout.PropertyField(item.FindPropertyRelative("freezeLastSeconds"), new GUIContent("Freeze Last (s)"));
            }
            EditorGUILayout.PropertyField(item.FindPropertyRelative("waitForPartEnd"),
                new GUIContent("Slot waits for this", "Tick if the slot should not move on until this video has finished."));

            if (!sequential)
                EditorGUILayout.PropertyField(item.FindPropertyRelative("startDelay"),
                    new GUIContent("Start after (s)", "Optional: hold this item back a few seconds instead of starting it with the others."));
        }
        EditorGUILayout.Space(4);
        return false;
    }

    private bool DrawBgVideoItemCard(SerializedProperty list, int i, string label, bool canDelete)
    {
        SerializedProperty item = list.GetArrayElementAtIndex(i);
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            if (DrawItemHeader(list, i, label, canDelete)) return true;

            EditorGUILayout.PropertyField(item.FindPropertyRelative("video"), new GUIContent("Video"));
            EditorGUILayout.Slider(item.FindPropertyRelative("playbackSpeed"), 0.1f, 3f, new GUIContent("Speed"));
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PropertyField(item.FindPropertyRelative("freezeFirstSeconds"), new GUIContent("Freeze First (s)"));
                EditorGUILayout.PropertyField(item.FindPropertyRelative("freezeLastSeconds"), new GUIContent("Freeze Last (s)"));
            }
            EditorGUILayout.PropertyField(item.FindPropertyRelative("loop"),
                new GUIContent("Repeat", "Keep replaying this video for as long as the slot is on screen."));
        }
        EditorGUILayout.Space(4);
        return false;
    }

    private bool DrawImageItemCard(SerializedProperty list, int i, string label, bool canDelete)
    {
        SerializedProperty item = list.GetArrayElementAtIndex(i);
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            if (DrawItemHeader(list, i, label, canDelete)) return true;

            EditorGUILayout.PropertyField(item.FindPropertyRelative("layer"), new GUIContent("Image"));
            EditorGUILayout.PropertyField(item.FindPropertyRelative("fadeIn"), new GUIContent("Fade In"));
            EditorGUILayout.PropertyField(item.FindPropertyRelative("fadeOut"), new GUIContent("Fade Out"));

            // An image has no natural length of its own, so ask in plain words how
            // long it stays. 0 seconds is what the runtime already reads as
            // "stay until the slot ends", so no new field is needed for that.
            SerializedProperty visibleDur = item.FindPropertyRelative("visibleDuration");
            string[] durLabels = { "Until the slot ends", "For a set number of seconds" };
            int durMode = visibleDur.floatValue > 0f ? 1 : 0;

            int newDurMode = EditorGUILayout.Popup(new GUIContent("Stay on screen"), durMode, durLabels);
            if (newDurMode == 0)
            {
                visibleDur.floatValue = 0f;
            }
            else
            {
                if (durMode == 0) visibleDur.floatValue = 3f; // just switched — give a sensible starting value
                EditorGUILayout.PropertyField(visibleDur, new GUIContent("Seconds"));
            }
        }
        EditorGUILayout.Space(4);
        return false;
    }

    // "One after another" mode: sets each item's start time so it begins right as the
    // previous one ends. Item 1 always starts at 0; only items 2+ get a computed delay.
    private void ApplySequentialTiming(SerializedProperty mainVids)
    {
        float cumulative = 0f;
        for (int i = 0; i < mainVids.arraySize; i++)
        {
            SerializedProperty item = mainVids.GetArrayElementAtIndex(i);
            SerializedProperty startDelayProp = item.FindPropertyRelative("startDelay");

            if (i > 0) startDelayProp.floatValue = cumulative;
            else cumulative = 0f; // Item 1 starts immediately

            var video = item.FindPropertyRelative("video").objectReferenceValue as VideoPlayer;
            float freezeFirst = item.FindPropertyRelative("freezeFirstSeconds").floatValue;
            float freezeLast = item.FindPropertyRelative("freezeLastSeconds").floatValue;
            float speed = Mathf.Max(0.01f, item.FindPropertyRelative("playbackSpeed").floatValue);
            float clipLen = (video != null && video.clip != null) ? (float)video.clip.length / speed : 3f;

            cumulative += freezeFirst + clipLen + freezeLast;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 3D
    // ─────────────────────────────────────────────────────────────────────────

    private void Draw3D()
    {
        DrawBar("3D PAGE");

        DrawSimpleList(_animators, "Animators");
        DrawSimpleList(_splineMovers, "Spline Movers");
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
                    EditorGUILayout.PropertyField(s.FindPropertyRelative("playbackSpeed"), new GUIContent("Speed"));
                    EditorGUILayout.PropertyField(s.FindPropertyRelative("freezeFirstSeconds"), new GUIContent("Freeze First (s)"));
                    EditorGUILayout.PropertyField(s.FindPropertyRelative("freezeLastSeconds"), new GUIContent("Freeze Last (s)"));
                    EditorGUILayout.PropertyField(s.FindPropertyRelative("startDelay"), new GUIContent("Start Delay"));
                    EditorGUILayout.PropertyField(s.FindPropertyRelative("waitForPageEnd"), new GUIContent("Wait For Page End"));
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
                    EditorGUILayout.PropertyField(s.FindPropertyRelative("startDelay"), new GUIContent("Start Delay"));
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
            SerializedProperty slot = _modelSlots.GetArrayElementAtIndex(i);
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
