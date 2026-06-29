using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(ContentController))]
public class ContentControllerEditor : Editor
{
    static ContentControllerEditor()
    {
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        AssemblyReloadEvents.beforeAssemblyReload += RestoreAllActivityTransformPreviewsStatic;
        AssemblyReloadEvents.beforeAssemblyReload += ClearVisualEffectEditorPreview;
    }

    private static void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.ExitingEditMode || state == PlayModeStateChange.EnteredPlayMode)
        {
            RestoreAllActivityTransformPreviewsStatic();
            RestoreSavedActivityPreviewPosesOnAllControllers();
            ClearVisualEffectEditorPreview();
        }
    }

    private SerializedProperty activities;
    private SerializedProperty activityPanel;
    private SerializedProperty defaultRaycastCamera;
    private SerializedProperty defaultAudioSource;
    private SerializedProperty completeImmediatelyWhenNoActivities;
    private SerializedProperty restartImmediatelyOnReplay;
    private SerializedProperty onActivitiesStarted;
    private SerializedProperty onActivitiesCompleted;
    private SerializedProperty onActivitiesReset;

    private bool showSetup = true;
    private bool showPageOptions;
    private bool showControllerEvents;
    private bool showAdvancedTemplates;
    private bool showOldGroupSetup;
    private bool showAdvancedActivitySetup;


    private struct ActivityTransformPreviewSnapshot
    {
        public Vector3 localPosition;
        public Quaternion localRotation;
        public Vector3 localScale;
    }

    private class VisualEffectEditorPreviewItem
    {
        public GameObject go;
        public Vector3 startPosition;
        public Vector3 sideDirection;
        public Quaternion startRotation;
        public float startedAt;
        public bool hasStarted;
        public float duration;
        public float distance;
        public float sideMovement;
        public float flutter;
        public float spin;
        public FallingObjectMotion motion;
        public float seed;
    }

    private static readonly List<VisualEffectEditorPreviewItem> visualEffectEditorPreviewItems = new List<VisualEffectEditorPreviewItem>();
    private static bool visualEffectEditorPreviewUpdateRegistered;
    private static readonly Dictionary<int, ActivityTransformPreviewSnapshot> activityTransformPreviewSnapshots = new Dictionary<int, ActivityTransformPreviewSnapshot>();
    private static bool hasActivityTransformClipboard;
    private static Vector3 activityTransformClipboardPosition;
    private static Vector3 activityTransformClipboardRotation;
    private static Vector3 activityTransformClipboardScale = Vector3.one;
    private readonly HashSet<int> activePreviewTargetsThisDraw = new HashSet<int>();

    private enum BeginnerInteractionMain
    {
        Screen,
        One3DObject,
        Multiple3DObjects,
        ChoiceButtons,
        UIButton,
        NothingWaitOnly
    }

    private enum OneObjectCompletion
    {
        OneCorrectTap,
        RequiredNumberOfTaps,
        ActiveTappingTime,
        ProgressReachesFull,
        StoryPausesThenTapObject
    }

    private enum MultipleObjectCompletion
    {
        AnyAllowedObject,
        RequiredObjects,
        ObjectsTappedInOrder
    }

    private enum ScreenCompletion
    {
        OneTap,
        RequiredNumberOfTaps
    }

    private enum ButtonCompletion
    {
        UIButtonOnly,
        ScreenOrUIButton
    }

    private static readonly string[] BeginnerMainLabels =
    {
        "Screen",
        "One 3D Object",
        "Multiple 3D Objects",
        "Choice Buttons",
        "UI Button",
        "Nothing - Wait Only"
    };

    private static readonly string[] OneObjectCompletionLabels =
    {
        "One Correct Tap",
        "Required Number Of Taps",
        "Active Tapping Time",
        "Progress Reaches Full",
        "Story Pauses Then Tap Object"
    };

    private static readonly string[] MultipleObjectCompletionLabels =
    {
        "Any Allowed Object",
        "Required Objects",
        "Objects Tapped In Order"
    };

    private static readonly string[] ScreenCompletionLabels =
    {
        "One Screen Tap",
        "Required Number Of Screen Taps"
    };

    private static readonly string[] ButtonCompletionLabels =
    {
        "UI Button Only",
        "Screen Or UI Button"
    };


    private static ContentController[] FindAllContentControllersInScene()
    {
#if UNITY_2023_1_OR_NEWER
        return UnityEngine.Object.FindObjectsByType<ContentController>(FindObjectsInactive.Include, FindObjectsSortMode.None);
#else
        return Resources.FindObjectsOfTypeAll<ContentController>();
#endif
    }

    private static ActivityPanel FindAnyActivityPanelInScene()
    {
#if UNITY_2023_1_OR_NEWER
        return UnityEngine.Object.FindFirstObjectByType<ActivityPanel>(FindObjectsInactive.Include);
#else
        ActivityPanel[] panels = Resources.FindObjectsOfTypeAll<ActivityPanel>();
        return panels != null && panels.Length > 0 ? panels[0] : null;
#endif
    }

    [MenuItem("Tools/Activity Template/Refresh All Content Controllers In Scene")]
    private static void RefreshAllContentControllersInScene()
    {
        ContentController[] controllers = FindAllContentControllersInScene();
        int changed = 0;
        foreach (ContentController controller in controllers)
        {
            if (controller == null) continue;
            Undo.RecordObject(controller, "Refresh Activity Template");
            changed += controller.RefreshActivityTemplateData(true);
            EditorUtility.SetDirty(controller);
        }
        UnityEditor.SceneManagement.EditorSceneManager.MarkAllScenesDirty();
        Debug.Log("[Activity Template] Refreshed " + controllers.Length + " ContentController component(s). Changed items: " + changed);
    }

    [MenuItem("Tools/Activity Template/Auto Fix Missing Colliders In Scene")]
    private static void AutoFixMissingCollidersInScene()
    {
        ContentController[] controllers = FindAllContentControllersInScene();
        int added = 0;
        foreach (ContentController controller in controllers)
            added += AddMissingColliders(controller);
        UnityEditor.SceneManagement.EditorSceneManager.MarkAllScenesDirty();
        Debug.Log("[Activity Template] Added " + added + " missing BoxCollider component(s) to assigned tap objects.");
    }

    private static readonly ActivityStartRule[] StartValues =
    {
        ActivityStartRule.AfterStoryEnds,
        ActivityStartRule.AfterRevealFinishes,
        ActivityStartRule.AfterVoiceStarts,
        ActivityStartRule.AfterWaitingTime,
        ActivityStartRule.AfterPreviousActivity,
        ActivityStartRule.FromAnimationEvent,
        ActivityStartRule.ManualStart,
        ActivityStartRule.AfterStoryObjectFinishes
    };

    private static readonly string[] StartLabels =
    {
        "After Story Ends",
        "After Reveal Finishes",
        "After Voice Starts",
        "After Waiting Time",
        "After Previous Activity",
        "From Animation Event",
        "Manual Start",
        "After Selected Story Animation Or Movement"
    };

    private static readonly ActivityInputKind[] InputValues =
    {
        ActivityInputKind.TapAnywhere,
        ActivityInputKind.TapObject,
        ActivityInputKind.TapButton,
        ActivityInputKind.TapAnywhereOrButton,
        ActivityInputKind.TapManyTimes,
        ActivityInputKind.TapObjectsInOrder,
        ActivityInputKind.KeepTapping,
        ActivityInputKind.HelpAction,
        ActivityInputKind.ProgressGate,
        ActivityInputKind.GroupAction,
        ActivityInputKind.ChooseOption,
        ActivityInputKind.AnswerQuestion,
        ActivityInputKind.WaitOnly,
        ActivityInputKind.WaitForStoryThenTapObject
    };

    private static readonly string[] InputLabels =
    {
        "Tap Screen",
        "Tap One Object",
        "Tap UI Button",
        "Tap Screen Or Button",
        "Tap Many Times",
        "Tap Objects In Order",
        "Keep Tapping For Time",
        "Help Action Legacy",
        "Fill Meter By Tapping",
        "Tap Characters To React",
        "Choose Option",
        "Choose Correct Option",
        "Wait Only",
        "Story Pause Then Tap Object"
    };

    private static readonly ActivityChoiceCorrectBehaviour[] ChoiceCorrectBehaviourValues =
    {
        ActivityChoiceCorrectBehaviour.ContinueStoryImmediately,
        ActivityChoiceCorrectBehaviour.PlayCorrectResultThenContinueStory
    };

    private static readonly string[] ChoiceCorrectBehaviourLabels =
    {
        "Continue Story Immediately",
        "Play Correct Result Then Continue Story"
    };

    private static readonly ActivityChoiceWrongOptionBehaviour[] ChoiceWrongBehaviourValues =
    {
        ActivityChoiceWrongOptionBehaviour.KeepSelectable,
        ActivityChoiceWrongOptionBehaviour.DisableAndGrayOut
    };

    private static readonly string[] ChoiceWrongBehaviourLabels =
    {
        "Keep Wrong Option Selectable",
        "Disable And Grey Out Wrong Option"
    };


    private static readonly StoryMomentTapCompletionMode[] StoryMomentCompleteValues =
    {
        StoryMomentTapCompletionMode.RequiredTapCount,
        StoryMomentTapCompletionMode.RequiredActiveTappingTime
    };

    private static readonly string[] StoryMomentCompleteLabels =
    {
        "Required Correct Taps",
        "Required Active Tapping Time"
    };

    private static readonly ActivityNextInputRule[] NextInputValues =
    {
        ActivityNextInputRule.Immediately,
        ActivityNextInputRule.AfterReactionFinishes,
        ActivityNextInputRule.AfterFixedDelay
    };

    private static readonly string[] NextInputLabels =
    {
        "Let Each Reaction Control Itself",
        "Wait For Blocking Reaction",
        "Wait For Fixed Time"
    };

    private static readonly ActivityNoInputAction[] NoInputActionValues =
    {
        ActivityNoInputAction.AutoPlayResultThenContinue,
        ActivityNoInputAction.SkipActivityAndContinue,
        ActivityNoInputAction.DoNothing
    };

    private static readonly string[] NoInputActionLabels =
    {
        "Auto Play Activity Result Then Continue",
        "Skip Activity And Continue",
        "Do Nothing"
    };


    private static readonly ActivityProgressBarFillMode[] ProgressBarFillValues =
    {
        ActivityProgressBarFillMode.FollowInputProgress,
        ActivityProgressBarFillMode.FollowActivityTime,
        ActivityProgressBarFillMode.FillWhenResultPlays
    };

    private static readonly string[] ProgressBarFillLabels =
    {
        "Child Input",
        "Time",
        "Result Animation"
    };

    private static readonly ActivityProgressBarBehavior[] ProgressBehaviorValues =
    {
        ActivityProgressBarBehavior.OnlyFillUp,
        ActivityProgressBarBehavior.GoDownIfChildStops,
        ActivityProgressBarBehavior.FillWithTime,
        ActivityProgressBarBehavior.FillDuringResult,
        ActivityProgressBarBehavior.AdvancedCustom
    };

    private static readonly string[] ProgressBehaviorLabels =
    {
        "Keep Filling Only",
        "Go Down When Child Stops",
        "Fill By Time",
        "Fill While Result Plays",
        "Advanced Custom"
    };

    private static readonly ActivityResultPlayTiming[] ResultPlayTimingValues =
    {
        ActivityResultPlayTiming.OnEveryCorrectInput,
        ActivityResultPlayTiming.AfterRequiredInputs,
        ActivityResultPlayTiming.WhenProgressIsFull,
        ActivityResultPlayTiming.WhileChildIsInteracting,
        ActivityResultPlayTiming.AfterNoInputAutoPlay
    };

    private static readonly string[] ResultPlayTimingLabels =
    {
        "Play Every Time Child Does It",
        "Play After Needed Inputs",
        "Play When Bar Is Full",
        "Play While Child Is Doing It",
        "Play Only After No Input"
    };

    private static readonly ActivityReactionAnimationPlayMode[] ReactionAnimationPlayModeValues =
    {
        ActivityReactionAnimationPlayMode.SelectedClipOnly,
        ActivityReactionAnimationPlayMode.RandomClip,
        ActivityReactionAnimationPlayMode.AllTogether,
        ActivityReactionAnimationPlayMode.AllOneByOne
    };

    private static readonly string[] ReactionAnimationPlayModeLabels =
    {
        "Selected Clip Only",
        "Pick One Random Clip",
        "Play All Together",
        "Play All One By One"
    };

    private static readonly ActivityVfxSpawnAreaMode[] VfxSpawnAreaValues =
    {
        ActivityVfxSpawnAreaMode.FromSourceOrSpawnPoint,
        ActivityVfxSpawnAreaMode.SpreadAcrossPage,
        ActivityVfxSpawnAreaMode.InsideRectangleArea
    };

    private static readonly string[] VfxSpawnAreaLabels =
    {
        "From One Place",
        "Spread Across Page",
        "Inside Rectangle Area"
    };

    private static readonly ProgressGatePreviewAnimationSelectionMode[] ProgressHelperSelectionValues =
    {
        ProgressGatePreviewAnimationSelectionMode.UseFirstAnimation,
        ProgressGatePreviewAnimationSelectionMode.PickRandomAnimationOnce,
        ProgressGatePreviewAnimationSelectionMode.UseSelectedAnimationNumber,
        ProgressGatePreviewAnimationSelectionMode.PlaySelectedAnimationNumbers,
        ProgressGatePreviewAnimationSelectionMode.PlayAllAnimationsInOrder,
        ProgressGatePreviewAnimationSelectionMode.PlayAllAnimationsByProgress,
        ProgressGatePreviewAnimationSelectionMode.PlaySelectedNumbersByProgress
    };

    private static readonly string[] ProgressHelperSelectionLabels =
    {
        "Use First Animation",
        "Pick One Random Animation",
        "Play One Animation Number",
        "Play Selected Animation Numbers",
        "Play All Animations One By One",
        "Change Animation With Progress",
        "Change Selected Animations With Progress"
    };

    private static readonly ActivityFinishRule[] FinishValues =
    {
        ActivityFinishRule.AfterFirstValidInput,
        ActivityFinishRule.AfterActiveTimeEnds,
        ActivityFinishRule.AfterRequiredInputs,
        ActivityFinishRule.AfterAllTargets,
        ActivityFinishRule.Manual
    };

    private static readonly string[] FinishLabels =
    {
        "After First Valid Input",
        "Keep Input Open For A Time",
        "After Required Inputs",
        "After All Targets",
        "Manual Finish"
    };

    private static readonly ActivityReactionType[] ReactionTypeValues =
    {
        ActivityReactionType.VisualEffect,
        ActivityReactionType.AnimationClip,
        ActivityReactionType.SoundEffect,
        ActivityReactionType.VoiceOver,
        ActivityReactionType.EnableObjects,
        ActivityReactionType.DisableObjects,
        ActivityReactionType.MaterialColor,
        ActivityReactionType.MoveObject,
        ActivityReactionType.CustomAction
    };

    private static readonly string[] ReactionTypeLabels =
    {
        "Visual Effect",
        "Animation",
        "Sound",
        "Voice",
        "Turn Objects On",
        "Turn Objects Off",
        "Change Color",
        "Move Object",
        "Custom Action"
    };

    private static readonly VisualEffectPlayMode[] VisualEffectPlayModeValues =
    {
        VisualEffectPlayMode.AddNewEachInput,
        VisualEffectPlayMode.RestartSameEffect,
        VisualEffectPlayMode.PlayOnlyWhenFinished
    };

    private static readonly string[] VisualEffectPlayModeLabels =
    {
        "Add New Effect Each Input",
        "Restart The Same Effect",
        "Wait Until Previous Effect Finishes"
    };

    private static readonly FallingObjectMotion[] FallingObjectMotionValues =
    {
        FallingObjectMotion.GentleFall,
        FallingObjectMotion.SwirlFall,
        FallingObjectMotion.BounceFall,
        FallingObjectMotion.FlutterFall
    };

    private static readonly string[] FallingObjectMotionLabels =
    {
        "Gentle Fall",
        "Magic Swirl Fall",
        "Bouncy Fall",
        "Flower Shower Natural"
    };

    private static readonly ActivityReactionMoment[] MomentValues =
    {
        ActivityReactionMoment.EveryValidInput,
        ActivityReactionMoment.IfReactionIsFree,
        ActivityReactionMoment.WhenActivityStarts,
        ActivityReactionMoment.WhenActivityCompletes,
        ActivityReactionMoment.WhenInputFails
    };

    private static readonly string[] MomentLabels =
    {
        "Every Valid Input",
        "Only When This Reaction Is Free",
        "When Activity Starts",
        "When Activity Ends",
        "When Input Fails"
    };

    private static readonly ReactionSfxMode[] SfxValues =
    {
        ReactionSfxMode.PlayOnce,
        ReactionSfxMode.RestartOnEveryTrigger,
        ReactionSfxMode.DoNotRestartWhilePlaying,
        ReactionSfxMode.StopWhenReactionEnds,
        ReactionSfxMode.LoopUntilReactionEnds,
        ReactionSfxMode.WaitForPreviousThenPlay,
        ReactionSfxMode.MatchReactionDuration
    };

    private static readonly string[] SfxLabels =
    {
        "Play With Reaction",
        "Restart Every Time",
        "Do Not Restart If Already Playing",
        "Stop When Reaction Ends",
        "Loop Until Reaction Ends",
        "Wait For Previous Audio",
        "Match Reaction Duration"
    };

    private void OnEnable()
    {
        if (target == null || serializedObject == null)
            return;

        activities = serializedObject.FindProperty("activities");
        activityPanel = serializedObject.FindProperty("activityPanel");
        defaultRaycastCamera = serializedObject.FindProperty("defaultRaycastCamera");
        defaultAudioSource = serializedObject.FindProperty("defaultAudioSource");
        completeImmediatelyWhenNoActivities = serializedObject.FindProperty("completeImmediatelyWhenNoActivities");
        restartImmediatelyOnReplay = serializedObject.FindProperty("restartImmediatelyOnReplay");
        onActivitiesStarted = serializedObject.FindProperty("onActivitiesStarted");
        onActivitiesCompleted = serializedObject.FindProperty("onActivitiesCompleted");
        onActivitiesReset = serializedObject.FindProperty("onActivitiesReset");

        // Safe migration when the Inspector opens. This creates missing new optional lists/fields
        // without deleting old activity setup. It keeps the guided Inspector current after code updates.
        ContentController controller = target as ContentController;
        if (controller != null)
        {
            int changed = controller.RefreshActivityTemplateData(false);
            if (changed > 0)
            {
                EditorUtility.SetDirty(controller);
                serializedObject.UpdateIfRequiredOrScript();
            }
        }
    }

    public override void OnInspectorGUI()
    {
        if (target == null || serializedObject == null)
            return;

        if (activities == null)
            OnEnable();

        if (activities == null)
            return;

        serializedObject.Update();

        DrawActivityBuilderHeader();
        DrawQuickToolbar();
        DrawActivities();
        DrawRequiredSetup();
        DrawPageOptions();
        DrawControllerEvents();

        serializedObject.ApplyModifiedProperties();
        ApplyEditorActivityTransformPreviews();
    }

    private void OnDisable()
    {
        RestoreAllActivityTransformPreviews();
    }

    private void RefreshSelectedController(bool force)
    {
        ContentController controller = target as ContentController;
        if (controller == null) return;

        serializedObject.ApplyModifiedProperties();
        Undo.RecordObject(controller, "Refresh Activity Template");
        int changed = controller.RefreshActivityTemplateData(force);
        EditorUtility.SetDirty(controller);
        serializedObject.UpdateIfRequiredOrScript();
        Repaint();
        SceneView.RepaintAll();
        Debug.Log("[Activity Template] Refreshed selected ContentController. Changed items: " + changed, controller);
    }

    private void AutoFixMissingCollidersForSelectedController()
    {
        ContentController controller = target as ContentController;
        if (controller == null) return;
        int added = AddMissingColliders(controller);
        if (added > 0)
        {
            EditorUtility.SetDirty(controller);
            serializedObject.Update();
            Repaint();
            SceneView.RepaintAll();
        }
        Debug.Log("[Activity Template] Auto collider fix added " + added + " BoxCollider component(s).", controller);
    }

    private static int AddMissingColliders(ContentController controller)
    {
        if (controller == null) return 0;
        HashSet<GameObject> tapObjects = new HashSet<GameObject>();

        foreach (ActivityStep step in controller.Activities)
        {
            if (step == null) continue;

            if (NeedsTargetObjectCollider(step.childInput) && step.targetObject != null)
                tapObjects.Add(step.targetObject);

            if (step.storyMomentTapObject != null)
                tapObjects.Add(step.storyMomentTapObject);

            if (step.targetActions != null)
            {
                foreach (ActivityTargetAction action in step.targetActions)
                {
                    if (action != null && action.tapObject != null)
                        tapObjects.Add(action.tapObject);
                }
            }

            if (step.groupTapObjects != null)
            {
                foreach (GameObject go in step.groupTapObjects)
                {
                    if (go != null) tapObjects.Add(go);
                }
            }
        }

        int added = 0;
        foreach (GameObject go in tapObjects)
        {
            if (go == null || HasColliderInChildren(go)) continue;
            Undo.AddComponent<BoxCollider>(go);
            EditorUtility.SetDirty(go);
            added++;
        }
        return added;
    }

    private static bool NeedsTargetObjectCollider(ActivityInputKind kind)
    {
        return kind == ActivityInputKind.TapObject
            || kind == ActivityInputKind.TapManyTimes
            || kind == ActivityInputKind.ProgressGate
            || kind == ActivityInputKind.HelpAction
            || kind == ActivityInputKind.TapObjectAndReact;
    }

    private void DrawActivityBuilderHeader()
    {
        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Story Activity Builder", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "No-coder template mode. Choose only what this activity needs. Optional features stay hidden until enabled. Input setup and Result Actions are separate so changing input does not remove the assigned result.",
            MessageType.Info);

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button(TipContent("Refresh Activity Template", "Safely updates old saved activity data to the new template format. Keeps your assigned objects, animations, audio and UI references."), GUILayout.Height(26)))
        {
            RefreshSelectedController(force: true);
        }

        if (GUILayout.Button(TipContent("Auto Fix Missing Colliders", "Adds a Box Collider to assigned tap objects that do not already have a Collider. This helps non-coders fix object tap setup quickly."), GUILayout.Height(26)))
        {
            AutoFixMissingCollidersForSelectedController();
        }
        EditorGUILayout.EndHorizontal();
    }

    private void DrawQuickToolbar()
    {
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        EditorGUILayout.LabelField("Activity Toolbox", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Pick a starting template. You can rename it and add reactions after it is created.",
            MessageType.None);

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button(TipContent("+ Tap Screen", "Use when the child taps anywhere on the screen after the story or reveal."), GUILayout.Height(28)))
            AddPresetActivity(ActivityInputKind.TapAnywhere, "Tap Screen");
        if (GUILayout.Button(TipContent("+ Tap One Object", "Use when the child must tap one specific 3D object."), GUILayout.Height(28)))
            AddPresetActivity(ActivityInputKind.TapObject, "Tap One Object");
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button(TipContent("+ Tap UI Button", "Use when the child taps one or two UI buttons."), GUILayout.Height(26)))
            AddPresetActivity(ActivityInputKind.TapButton, "Tap UI Button");
        if (GUILayout.Button(TipContent("+ Keep Tapping For Time", "Use when the child can keep tapping for a fixed time."), GUILayout.Height(26)))
            AddPresetActivity(ActivityInputKind.KeepTapping, "Keep Tapping For Time");
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button(TipContent("+ Fill Meter By Tapping", "Use when the child taps to fill a meter before the story continues."), GUILayout.Height(26)))
            AddPresetActivity(ActivityInputKind.ProgressGate, "Fill Meter By Tapping");
        if (GUILayout.Button(TipContent("+ Tap Characters To React", "Use when tapping one or more characters should play greeting or group reactions."), GUILayout.Height(26)))
            AddPresetActivity(ActivityInputKind.GroupAction, "Tap Characters To React");
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button(TipContent("+ Story Pause Then Tap Object", "Use when the story reaches a moment, pauses, lets the child tap an object, then resumes."), GUILayout.Height(26)))
            AddPresetActivity(ActivityInputKind.WaitForStoryThenTapObject, "Story Pause Then Tap Object");
        if (GUILayout.Button(TipContent("+ Choose Correct Option", "Use when the child answers a question by selecting the correct option from 2 to 5 buttons."), GUILayout.Height(26)))
            AddPresetActivity(ActivityInputKind.AnswerQuestion, "Choose Correct Option");
        EditorGUILayout.EndHorizontal();

        showAdvancedTemplates = EditorGUILayout.Foldout(showAdvancedTemplates, "Advanced Templates", true);
        if (showAdvancedTemplates)
        {
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(TipContent("+ Advanced Legacy Help", "Advanced old setup. Use only for existing pages that already depend on it."), GUILayout.Height(24)))
                AddPresetActivity(ActivityInputKind.HelpAction, "Help Action");
            EditorGUILayout.EndHorizontal();
        }

        EditorGUILayout.EndVertical();
    }

    private void DrawActivities()
    {
        EditorGUILayout.Space(6);
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Activities On This Page", EditorStyles.boldLabel);
        if (GUILayout.Button(TipContent("+ Blank Activity", "Create an empty activity and configure it manually."), GUILayout.Width(120), GUILayout.Height(26)))
            AddActivity();
        EditorGUILayout.EndHorizontal();

        if (activities.arraySize == 0)
        {
            EditorGUILayout.HelpBox(
                "No activity is added. This page behaves like a normal story page.",
                MessageType.None);
        }

        for (int i = 0; i < activities.arraySize; i++)
            DrawActivity(activities.GetArrayElementAtIndex(i), i);

        EditorGUILayout.EndVertical();
    }

    private void DrawActivity(SerializedProperty activity, int index)
    {
        SerializedProperty name = activity.FindPropertyRelative("activityName");
        SerializedProperty enabled = activity.FindPropertyRelative("enabled");
        SerializedProperty startWhen = activity.FindPropertyRelative("startWhen");
        SerializedProperty childInput = activity.FindPropertyRelative("childInput");
        SerializedProperty reactions = activity.FindPropertyRelative("reactions");

        string title = string.IsNullOrWhiteSpace(name.stringValue) ? "Activity " + (index + 1) : name.stringValue;

        EditorGUILayout.Space(8);
        EditorGUILayout.BeginVertical("box");

        EditorGUILayout.BeginHorizontal();
        activity.isExpanded = EditorGUILayout.Foldout(activity.isExpanded, (index + 1) + ". " + title, true, EditorStyles.foldoutHeader);
        if (GUILayout.Button(TipContent("Remove", "Remove this activity or reaction from the page."), GUILayout.Width(80)))
        {
            activities.DeleteArrayElementAtIndex(index);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
            return;
        }
        EditorGUILayout.EndHorizontal();

        if (!activity.isExpanded)
        {
            EditorGUILayout.LabelField("Start", NiceStart((ActivityStartRule)startWhen.enumValueIndex));
            EditorGUILayout.LabelField("Input", NiceInput((ActivityInputKind)childInput.enumValueIndex));
            EditorGUILayout.LabelField("Reactions", reactions.arraySize.ToString());
            EditorGUILayout.EndVertical();
            return;
        }

        DrawStepBox("1. Activity", "Name this activity clearly. Turn it off only if you want to keep the setup but not run it.");
        EditorGUILayout.PropertyField(enabled, TipContent("Use This Activity"));
        EditorGUILayout.PropertyField(name, TipContent("Activity Name"));

        DrawStartSection(activity);           // 2. When Should This Activity Start
        DrawInstructionSection(activity);     // 3. What Should The Child See Or Hear
        DrawInputSection(activity);           // 4. What Should The Child Do
        DrawTappingAnimationSection(activity);// 5. Character Animation While Tapping
        DrawHelpAndTimeoutSection(activity);  // 6. Timing
        DrawProgressBarSection(activity);     // 7. Progress Bar Optional
        DrawWrongFeedbackSection(activity);   // 8. Wrong Input Optional
        DrawReactionSection(activity);        // 9. Result Actions
        DrawFinishSection(activity);          // 10. After Activity
        DrawActivitySoundsSection(activity);  // 12. Sounds Optional
        DrawObjectStateSection(activity);     // 13. Object On / Off Changes

        // Setup Check is always last so the person reads it after completing the setup above.
        DrawStepBox("14. Setup Check", "Read this before testing. It tells you exactly what is missing and how to fix it.");
        DrawActivitySetupCheck(activity);

        EditorGUILayout.EndVertical();
    }

    private void DrawActivitySetupCheck(SerializedProperty activity)
    {
        bool hasProblem = false;
        SerializedProperty input = activity.FindPropertyRelative("childInput");
        ActivityInputKind kind = (ActivityInputKind)input.enumValueIndex;

        if (activityPanel.objectReferenceValue == null)
        {
            hasProblem = true;
            EditorGUILayout.HelpBox("Teacher Mode: Activity UI Panel is missing. Drag the ARInteractionUI or ActivityPanel here. This panel shows instruction text, buttons, progress and feedback for activities.", MessageType.Warning);
            if (GUILayout.Button(TipContent("Find Activity UI Automatically", "Searches the scene for ActivityPanel or ARInteractionUI and assigns it.")))
            {
                ActivityPanel found = FindAnyActivityPanelInScene();
                if (found != null)
                {
                    activityPanel.objectReferenceValue = found;
                    serializedObject.ApplyModifiedProperties();
                    Debug.Log("[Activity Template] Assigned Activity UI Panel automatically.", found);
                }
                else
                {
                    Debug.LogWarning("[Activity Template] No ActivityPanel or ARInteractionUI was found in the scene.");
                }
            }
        }

        if ((kind == ActivityInputKind.TapObject || kind == ActivityInputKind.TapManyTimes || kind == ActivityInputKind.HelpAction || kind == ActivityInputKind.ProgressGate) && activity.FindPropertyRelative("targetObject").objectReferenceValue == null)
        {
            hasProblem = true;
            EditorGUILayout.HelpBox("Setup Check: Object To Tap is missing. Drag the 3D object the child should tap.", MessageType.Warning);
        }

        if ((kind == ActivityInputKind.TapObject || kind == ActivityInputKind.TapManyTimes || kind == ActivityInputKind.HelpAction || kind == ActivityInputKind.ProgressGate) && activity.FindPropertyRelative("targetObject").objectReferenceValue is GameObject target)
        {
            if (!HasColliderInChildren(target))
            {
                hasProblem = true;
                EditorGUILayout.HelpBox("Setup Check: The object cannot be tapped because it has no Collider. Add Box Collider, Capsule Collider, Sphere Collider, or Mesh Collider.", MessageType.Warning);
                if (GUILayout.Button(TipContent("Add Box Collider To Tap Object", "Adds a Box Collider to the selected target so object tapping can work.")))
                {
                    Undo.AddComponent<BoxCollider>(target);
                    EditorUtility.SetDirty(target);
                }
            }
        }


        if (kind == ActivityInputKind.WaitForStoryThenTapObject && activity.FindPropertyRelative("storyMomentTapObject").objectReferenceValue == null)
        {
            hasProblem = true;
            EditorGUILayout.HelpBox("Setup Check: 3D Object To Tap is missing. Drag the object the child should tap.", MessageType.Warning);
        }

        if (kind == ActivityInputKind.WaitForStoryThenTapObject && (ActivityStartRule)activity.FindPropertyRelative("startWhen").enumValueIndex == ActivityStartRule.AfterStoryObjectFinishes)
        {
            bool hasMovement = activity.FindPropertyRelative("storyWaitComponent").objectReferenceValue != null;
            bool hasAnimator = activity.FindPropertyRelative("storyWaitAnimator").objectReferenceValue != null;
            if (!hasMovement && !hasAnimator)
            {
                hasProblem = true;
                EditorGUILayout.HelpBox("Setup Check: Start is set to wait for story, but no Story Movement or Story Animator is assigned.", MessageType.Warning);
            }
        }

        if (kind == ActivityInputKind.WaitForStoryThenTapObject && activity.FindPropertyRelative("storyMomentTapObject").objectReferenceValue is GameObject storyTapObject)
        {
            if (!HasColliderInChildren(storyTapObject))
            {
                hasProblem = true;
                EditorGUILayout.HelpBox("Setup Check: The tap object has no Collider, so the child cannot tap it.", MessageType.Warning);
                if (GUILayout.Button(TipContent("Add Box Collider To Tap Object", "Adds a Box Collider so this object can receive taps.")))
                {
                    Undo.AddComponent<BoxCollider>(storyTapObject);
                    EditorUtility.SetDirty(storyTapObject);
                }
            }
        }
        if (kind == ActivityInputKind.ProgressGate)
        {
            if (activity.FindPropertyRelative("progressUseHelperAnimationWhileTapping").boolValue)
            {
                if (activity.FindPropertyRelative("progressHelperAnimator").objectReferenceValue == null && activity.FindPropertyRelative("resultAnimator").objectReferenceValue == null)
                {
                    hasProblem = true;
                    EditorGUILayout.HelpBox("Setup Check: Helper animation is ON, but no Animator is assigned. Drag the monkey or object Animator into Helper Animator.", MessageType.Warning);
                }

                SerializedProperty helperClips = activity.FindPropertyRelative("progressHelperAnimations");
                if (helperClips == null || helperClips.arraySize == 0)
                {
                    hasProblem = true;
                    EditorGUILayout.HelpBox("Setup Check: Helper animation is ON, but no helper animation clips are assigned. Drag one or more short trying/pulling animations.", MessageType.Warning);
                }
            }

            if (activity.FindPropertyRelative("resultAnimator").objectReferenceValue == null && activity.FindPropertyRelative("resultVoiceOver").objectReferenceValue != null)
            {
                // This is allowed because voice-only result may be intentional.
            }

            if (activity.FindPropertyRelative("resultAnimationClip").objectReferenceValue != null && activity.FindPropertyRelative("resultAnimator").objectReferenceValue == null)
            {
                hasProblem = true;
                EditorGUILayout.HelpBox("Setup Check: Result Animation Clip is assigned, but Result Animator is missing. Drag the Animator that should play the final story animation.", MessageType.Warning);
            }

            if (activity.FindPropertyRelative("resultAnimationClip").objectReferenceValue == null && activity.FindPropertyRelative("resultVoiceOver").objectReferenceValue == null)
            {
                EditorGUILayout.HelpBox("Info: No result animation or voice is assigned. This is OK if the normal story should continue after the meter is full.", MessageType.None);
            }
        }

        if (kind == ActivityInputKind.GroupAction)
        {
            SerializedProperty targetActions = activity.FindPropertyRelative("targetActions");
            SerializedProperty taps = activity.FindPropertyRelative("groupTapObjects");
            SerializedProperty actions = activity.FindPropertyRelative("groupActions");
            bool hasTargetActions = targetActions != null && targetActions.arraySize > 0;
            bool hasOldTapObjects = taps != null && taps.arraySize > 0;
            bool hasOldActions = actions != null && actions.arraySize > 0;

            if (!hasTargetActions && !hasOldTapObjects)
            {
                hasProblem = true;
                EditorGUILayout.HelpBox("Setup Check: Target Set needs at least one object the child can tap. Add items to Target Actions, or use Allowed Objects To Tap.", MessageType.Warning);
            }

            if (!hasTargetActions && !hasOldActions)
            {
                hasProblem = true;
                EditorGUILayout.HelpBox("Setup Check: Add at least one Target Action or one older Group Reaction item so something happens after the tap.", MessageType.Warning);
            }

            if (hasTargetActions)
            {
                for (int t = 0; t < targetActions.arraySize; t++)
                {
                    SerializedProperty item = targetActions.GetArrayElementAtIndex(t);
                    GameObject tapObject = item.FindPropertyRelative("tapObject").objectReferenceValue as GameObject;
                    if (tapObject == null)
                    {
                        hasProblem = true;
                        EditorGUILayout.HelpBox("Setup Check: A Target Action is missing Tap Object.", MessageType.Warning);
                        break;
                    }
                    if (!HasColliderInChildren(tapObject))
                    {
                        hasProblem = true;
                        EditorGUILayout.HelpBox("Setup Check: One Target Action object has no Collider. Add a Collider so the child can tap it.", MessageType.Warning);
                        break;
                    }
                }
            }

            if (hasOldTapObjects)
            {
                for (int t = 0; t < taps.arraySize; t++)
                {
                    if (taps.GetArrayElementAtIndex(t).objectReferenceValue is GameObject tapObject && !HasColliderInChildren(tapObject))
                    {
                        hasProblem = true;
                        EditorGUILayout.HelpBox("Setup Check: One allowed tap object has no Collider. Add a Collider so the child can tap it.", MessageType.Warning);
                        break;
                    }
                }
            }

            ActivityGroupCompletionMode groupMode = (ActivityGroupCompletionMode)activity.FindPropertyRelative("groupCompletionMode").enumValueIndex;
            if (groupMode == ActivityGroupCompletionMode.RequiredObjects && hasTargetActions)
            {
                bool hasRequired = false;
                for (int t = 0; t < targetActions.arraySize; t++)
                {
                    if (targetActions.GetArrayElementAtIndex(t).FindPropertyRelative("required").boolValue)
                    {
                        hasRequired = true;
                        break;
                    }
                }
                if (!hasRequired)
                {
                    hasProblem = true;
                    EditorGUILayout.HelpBox("Setup Check: Required Objects mode is selected, but no Target Action is marked Required.", MessageType.Warning);
                }
            }
        }

        if (kind == ActivityInputKind.ChooseOption || kind == ActivityInputKind.AnswerQuestion)
        {
            SerializedProperty choiceOptions = activity.FindPropertyRelative("choiceOptions");
            SerializedProperty optionTexts = activity.FindPropertyRelative("optionTexts");
            int optionCount = choiceOptions != null && choiceOptions.arraySize > 0 ? choiceOptions.arraySize : (optionTexts != null ? optionTexts.arraySize : 0);
            int correctCount = 0;

            if (choiceOptions != null && choiceOptions.arraySize > 0)
            {
                for (int o = 0; o < choiceOptions.arraySize; o++)
                {
                    if (choiceOptions.GetArrayElementAtIndex(o).FindPropertyRelative("isCorrect").boolValue)
                        correctCount++;
                }
            }
            else
            {
                correctCount = optionCount > 0 ? 1 : 0;
            }

            if (optionCount == 0)
            {
                hasProblem = true;
                EditorGUILayout.HelpBox("Teacher Mode: Choose Correct Option needs at least 2 options. Click Refresh Activity Template to create safe default options without removing your component.", MessageType.Warning);
                if (GUILayout.Button(TipContent("Refresh Choice Setup", "Creates missing option data and migrates old single-animation options into Scenario Actions.")))
                    RefreshSelectedController(force: true);
            }
            if (correctCount != 1)
            {
                hasProblem = true;
                EditorGUILayout.HelpBox("Teacher Mode: Choose Correct Option needs exactly one correct answer. Use Correct Option Number in the Options section. The refresh button will also force only one correct answer.", MessageType.Warning);
                if (GUILayout.Button(TipContent("Fix Correct Option Selection", "Keeps one correct option and clears extra correct checkboxes.")))
                    RefreshSelectedController(force: true);
            }

            if (optionCount > 2 && activityPanel.objectReferenceValue is ActivityPanel panel)
            {
                if (!HasChoiceButtonLayout(panel, optionCount))
                {
                    hasProblem = true;
                    EditorGUILayout.HelpBox("Setup Check: This option count needs a matching option group in Activity UI Panel. Assign the 3, 4, or 5 option group once in ARInteractionUI, or assign Dynamic Button Parent and Dynamic Button Prefab as fallback.", MessageType.Warning);
                }
            }
        }

        SerializedProperty reactions = activity.FindPropertyRelative("reactions");
        for (int i = 0; i < reactions.arraySize; i++)
        {
            SerializedProperty reaction = reactions.GetArrayElementAtIndex(i);
            if (!reaction.FindPropertyRelative("enabled").boolValue) continue;

            ActivityReactionType type = (ActivityReactionType)reaction.FindPropertyRelative("type").enumValueIndex;
            string reactionName = reaction.FindPropertyRelative("reactionName").stringValue;

            if (type == ActivityReactionType.VisualEffect && reaction.FindPropertyRelative("vfxObjects").arraySize == 0)
            {
                hasProblem = true;
                EditorGUILayout.HelpBox("Setup Check: " + reactionName + " has no Visual Effect Objects. Drag a ParticleSystem or 3D prefab such as flowers, petals, coins, leaves, or sparkles.", MessageType.Warning);
            }

            if (type == ActivityReactionType.AnimationClip)
            {
                if (reaction.FindPropertyRelative("animator").objectReferenceValue == null)
                {
                    hasProblem = true;
                    EditorGUILayout.HelpBox("Setup Check: " + reactionName + " is missing Animator To Play. Drag the Animator from the object or character.", MessageType.Warning);
                }
                if (reaction.FindPropertyRelative("animationClip").objectReferenceValue == null)
                {
                    hasProblem = true;
                    EditorGUILayout.HelpBox("Setup Check: " + reactionName + " is missing Animation Clip To Play. Drag the animation clip asset.", MessageType.Warning);
                }
            }

            bool hasSound = reaction.FindPropertyRelative("optionalSfx").objectReferenceValue != null || reaction.FindPropertyRelative("mainAudio").objectReferenceValue != null || reaction.FindPropertyRelative("reactionVoiceOver").objectReferenceValue != null;
            if (hasSound && defaultAudioSource.objectReferenceValue == null)
            {
                hasProblem = true;
                EditorGUILayout.HelpBox("Setup Check: Audio is assigned, but Audio Source For Sounds is empty in Required Setup. Drag an AudioSource there.", MessageType.Warning);
            }
        }

        if (!hasProblem)
            EditorGUILayout.HelpBox("Setup Check: Ready. No required fields appear to be missing for this activity.", MessageType.Info);
    }

    private void DrawStartSection(SerializedProperty activity)
    {
        DrawStepBox("2. When Should This Activity Start?", "Choose the story moment where this activity should become active.");
        SerializedProperty startWhen = activity.FindPropertyRelative("startWhen");
        DrawEnumPopup(startWhen, StartValues, StartLabels, TipContent("Start When"));

        ActivityStartRule rule = (ActivityStartRule)startWhen.enumValueIndex;

        if (rule == ActivityStartRule.AfterWaitingTime || rule == ActivityStartRule.AfterStoryEnds || rule == ActivityStartRule.AfterRevealFinishes || rule == ActivityStartRule.AfterVoiceStarts)
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("waitBeforeStart"), TipContent("Extra Wait Before Start"));

        if (rule == ActivityStartRule.AfterStoryObjectFinishes)
        {
            EditorGUILayout.HelpBox("Story plays first. Pick the exact story model or Animator and the animation clip. When that clip finishes, this activity starts after the delay.", MessageType.None);

            SerializedProperty watchObject = activity.FindPropertyRelative("storyWaitObjectOrAnimator");
            if (watchObject != null)
                EditorGUILayout.PropertyField(watchObject, TipContent("Story Model Or Animator To Watch", "Drag the story model or its Animator. Example: drag fox walk or fox walk Animator. Do not drag the full page unless you really want to wait for the whole page."));

            EditorGUILayout.PropertyField(activity.FindPropertyRelative("storyWaitAnimator"), TipContent("Animator To Watch Optional", "Optional. Use this only if the Animator is not found from the story model above."));
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("storyWaitAnimationClip"), TipContent("Animation Clip To Wait For", "Drag the exact animation clip. Example: fox walk 01. Activity starts after this clip finishes."));

            EditorGUILayout.PropertyField(activity.FindPropertyRelative("waitBeforeStart"), TipContent("Delay After Animation", "Wait this many seconds after the selected animation finishes, then start the activity."));
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("storyWaitTimeoutSeconds"), TipContent("Warn If Animation Is Not Found After Seconds", "Optional. Shows a Console warning if the selected animation or movement is not reached. It will not start the activity early."));

            SerializedProperty pauseStory = activity.FindPropertyRelative("pauseStoryWhileActivity");
            SerializedProperty childInput = activity.FindPropertyRelative("childInput");
            if (childInput != null && (ActivityInputKind)childInput.enumValueIndex == ActivityInputKind.WaitForStoryThenTapObject && !pauseStory.boolValue)
                pauseStory.boolValue = true;

            EditorGUILayout.PropertyField(pauseStory, TipContent("Pause Story While Activity Runs", "ON = pause story animation, voice, and movement while the child completes this activity. Story resumes from the same point."));

            EditorGUILayout.Space(2);
            EditorGUILayout.HelpBox("Advanced: typed Animator state names are intentionally hidden. Use the Animation Clip field above. This avoids typing mistakes.", MessageType.None);
        }

        if (rule == ActivityStartRule.FromAnimationEvent || rule == ActivityStartRule.ManualStart)
        {
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("startKey"), TipContent("Start Key Optional"));
            EditorGUILayout.HelpBox("Use this only when another script or animation event starts this activity.", MessageType.None);
        }
    }

    private void DrawInstructionSection(SerializedProperty activity)
    {
        DrawStepBox("3. What Should The Child See Or Hear?", "Instruction text and optional voice for this activity. Background sounds are in section 12 Sounds.");
        EditorGUILayout.PropertyField(activity.FindPropertyRelative("instructionText"), TipContent("Text Shown To Child"));
        EditorGUILayout.PropertyField(activity.FindPropertyRelative("activityVoiceOver"), TipContent("Instruction Voice Optional"));
        if (activity.FindPropertyRelative("activityVoiceOver").objectReferenceValue != null)
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("waitForActivityVoiceOver"), TipContent("Wait Until Voice Finishes"));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // SECTION: ALL SOUNDS OPTIONAL
    // ═══════════════════════════════════════════════════════════════════════
    private void DrawActivitySoundsSection(SerializedProperty activity)
    {
        DrawStepBox("12. Sounds Optional",
            "All sounds are optional. Leave any field empty for silence. " +
            "Each sound has its own volume slider.");

        SerializedProperty fold = activity.FindPropertyRelative("activityStartSound");
        fold.isExpanded = EditorGUILayout.Foldout(fold.isExpanded, "Show All Sound Settings", true);
        if (!fold.isExpanded) return;

        EditorGUI.indentLevel++;

        EditorGUILayout.LabelField("Activity Sounds", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(activity.FindPropertyRelative("activityStartSound"),
            TipContent("Sound When Activity Starts", "Plays once the moment this activity begins."));
        if (activity.FindPropertyRelative("activityStartSound").objectReferenceValue != null)
            EditorGUILayout.Slider(activity.FindPropertyRelative("activityStartSoundVolume"), 0f, 2f, TipContent("Volume"));

        EditorGUILayout.PropertyField(activity.FindPropertyRelative("activityCompleteSound"),
            TipContent("Sound When Activity Completes", "Plays after the child finishes, before result animation."));
        if (activity.FindPropertyRelative("activityCompleteSound").objectReferenceValue != null)
            EditorGUILayout.Slider(activity.FindPropertyRelative("activityCompleteSoundVolume"), 0f, 2f, TipContent("Volume"));

        EditorGUILayout.PropertyField(activity.FindPropertyRelative("activityDurationAudio"),
            TipContent("Background Sound During Activity", "Loops while this activity is running. Stops when activity ends."));
        if (activity.FindPropertyRelative("activityDurationAudio").objectReferenceValue != null)
        {
            EditorGUI.indentLevel++;
            EditorGUILayout.Slider(activity.FindPropertyRelative("activityDurationAudioVolume"), 0f, 2f, TipContent("Background Volume"));
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("loopActivityDurationAudio"), TipContent("Loop"));
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("fadeActivityAudioOnEnd"), TipContent("Fade Out When Activity Ends"));
            if (activity.FindPropertyRelative("fadeActivityAudioOnEnd").boolValue)
                EditorGUILayout.PropertyField(activity.FindPropertyRelative("activityAudioFadeSeconds"), TipContent("Fade Duration"));
            EditorGUI.indentLevel--;
        }

        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Correct Tap Sounds", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "This shared sound plays when the child taps correctly. " +
            "If the specific activity already has its own tap sound assigned (like Fill Meter or Drum activities), " +
            "this is used as a fallback only.",
            MessageType.None);
        EditorGUILayout.PropertyField(activity.FindPropertyRelative("generalCorrectTapSound"),
            TipContent("Sound On Correct Tap", "Plays every correct tap. Leave empty if the activity already has its own tap sound."));
        if (activity.FindPropertyRelative("generalCorrectTapSound").objectReferenceValue != null)
        {
            EditorGUI.indentLevel++;
            EditorGUILayout.Slider(activity.FindPropertyRelative("generalCorrectTapSoundVolume"), 0f, 2f, TipContent("Volume"));
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("correctTapSoundGapMode"),
                TipContent("Gap Between Plays",
                "Prevents the same sound from playing too fast. SmallGap 0.15s is a good choice for most drum activities. NoGap = every tap plays."));
            if ((CorrectTapSoundGapMode)activity.FindPropertyRelative("correctTapSoundGapMode").enumValueIndex == CorrectTapSoundGapMode.Custom)
                EditorGUILayout.PropertyField(activity.FindPropertyRelative("correctTapCustomGapSeconds"), TipContent("Custom Gap Seconds"));
            EditorGUI.indentLevel--;
        }

        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Progress Sounds", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(activity.FindPropertyRelative("progressDropSound"),
            TipContent("Sound When Bar Goes Down", "Plays when the progress bar drops because the child stopped tapping."));
        if (activity.FindPropertyRelative("progressDropSound").objectReferenceValue != null)
            EditorGUILayout.Slider(activity.FindPropertyRelative("progressDropSoundVolume"), 0f, 2f, TipContent("Volume"));

        EditorGUILayout.PropertyField(activity.FindPropertyRelative("progressFullSound"),
            TipContent("Sound When Bar Is Full", "Plays the moment progress reaches 100 percent."));
        if (activity.FindPropertyRelative("progressFullSound").objectReferenceValue != null)
            EditorGUILayout.Slider(activity.FindPropertyRelative("progressFullSoundVolume"), 0f, 2f, TipContent("Volume"));

        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Animation Sounds", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("Animation-while-tapping sounds are shown directly inside the Character Animation While Tapping section so you can assign sounds next to the clips.", MessageType.None);

        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Hint Sounds", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(activity.FindPropertyRelative("noInputHintSound"),
            TipContent("Sound When No-Input Hint Appears", "Plays when the child has done nothing for too long."));
        if (activity.FindPropertyRelative("noInputHintSound").objectReferenceValue != null)
            EditorGUILayout.Slider(activity.FindPropertyRelative("noInputHintSoundVolume"), 0f, 2f, TipContent("Volume"));
        EditorGUILayout.HelpBox("Wrong-input hint sound is in section 8 Wrong Input, next to the hint text.", MessageType.None);

        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Result Sounds", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(activity.FindPropertyRelative("resultAnimationStartSound"),
            TipContent("Sound When Result Starts", "Plays at the start of the result animation. Different from Result Sound Effect which plays after."));
        if (activity.FindPropertyRelative("resultAnimationStartSound").objectReferenceValue != null)
            EditorGUILayout.Slider(activity.FindPropertyRelative("resultAnimationStartSoundVolume"), 0f, 2f, TipContent("Volume"));

        EditorGUI.indentLevel--;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // SECTION: HINTS WHILE DOING WELL (positive milestone hints)
    // ═══════════════════════════════════════════════════════════════════════
    private void DrawMilestoneHintsSection(SerializedProperty activity)
    {
        DrawStepBox("Hints While Doing Well Optional",
            "Add encouraging text that shows when the child reaches a progress milestone.\n" +
            "Uses the same text area as instruction and hint text.\n" +
            "Example: At 50% show 'Keep going! Halfway there!'");

        SerializedProperty milestones = activity.FindPropertyRelative("progressMilestones");
        milestones.isExpanded = EditorGUILayout.Foldout(milestones.isExpanded, "Show Milestone Hints (" + milestones.arraySize + " added)", true);
        if (!milestones.isExpanded) return;

        EditorGUI.indentLevel++;
        EditorGUILayout.HelpBox(
            "Each milestone fires when progress crosses the percentage you set.\n" +
            "Text stays visible until the next milestone replaces it or the activity ends.\n" +
            "Wrong-input or no-input hints can still override milestone text.",
            MessageType.Info);

        for (int i = 0; i < milestones.arraySize; i++)
        {
            SerializedProperty m = milestones.GetArrayElementAtIndex(i);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.BeginHorizontal();
            SerializedProperty mEnabled = m.FindPropertyRelative("enabled");
            EditorGUILayout.PropertyField(mEnabled, GUIContent.none, GUILayout.Width(16));
            float pct = m.FindPropertyRelative("progressPercent").floatValue;
            EditorGUILayout.LabelField("Milestone " + (i + 1) + " — fires at " + Mathf.RoundToInt(pct) + "%",
                mEnabled.boolValue ? EditorStyles.boldLabel : EditorStyles.miniLabel);
            if (GUILayout.Button("Remove", GUILayout.Width(60)))
            {
                milestones.DeleteArrayElementAtIndex(i);
                break;
            }
            EditorGUILayout.EndHorizontal();

            if (!mEnabled.boolValue) { EditorGUILayout.EndVertical(); continue; }

            EditorGUI.indentLevel++;
            EditorGUILayout.Slider(m.FindPropertyRelative("progressPercent"), 0f, 100f,
                TipContent("Fires At Progress %", "0 = fires at start. 50 = fires at halfway. 100 = fires when complete."));
            EditorGUILayout.PropertyField(m.FindPropertyRelative("hintText"),
                TipContent("Text To Show", "Example: Keep going! You are halfway there!"));
            EditorGUILayout.PropertyField(m.FindPropertyRelative("repeatMode"),
                TipContent("When To Show Again",
                "FireOnce = shows once per run. EveryTimeCrossed = shows every time progress goes up past this. FireAgainAfterDrop = shows again if progress dropped below this and rose above again."));
            EditorGUILayout.PropertyField(m.FindPropertyRelative("sound"),
                TipContent("Sound Optional", "Plays when this milestone is reached."));
            if (m.FindPropertyRelative("sound").objectReferenceValue != null)
                EditorGUILayout.Slider(m.FindPropertyRelative("soundVolume"), 0f, 2f, TipContent("Sound Volume"));
            EditorGUI.indentLevel--;

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(2);
        }

        if (GUILayout.Button("+ Add Milestone Hint"))
            milestones.InsertArrayElementAtIndex(milestones.arraySize);

        EditorGUI.indentLevel--;
    }

    private void DrawObjectStateSection(SerializedProperty activity)
    {
        DrawStepBox("13. Object On / Off Changes", "Optional. Use this when objects must appear or disappear when the activity starts or completes. These are automatically restored when replay happens.");
        SerializedProperty fold = activity.FindPropertyRelative("objectsOnWhenActivityStarts");
        fold.isExpanded = EditorGUILayout.Foldout(fold.isExpanded, "Show / Hide Objects", true);
        if (!fold.isExpanded)
            return;

        EditorGUI.indentLevel++;
        EditorGUILayout.HelpBox("All changes here are reversed automatically when the page replays. Objects return to their original state.", MessageType.Info);
        EditorGUILayout.PropertyField(activity.FindPropertyRelative("objectsOnWhenActivityStarts"), TipContent("Show These When Activity Starts", "These objects become visible the moment this activity begins."), true);
        EditorGUILayout.PropertyField(activity.FindPropertyRelative("objectsOffWhenActivityStarts"), TipContent("Hide These When Activity Starts", "These objects become invisible the moment this activity begins."), true);
        EditorGUILayout.PropertyField(activity.FindPropertyRelative("objectsOnWhenActivityCompletes"), TipContent("Show These When Activity Completes", "These objects become visible after the activity finishes successfully."), true);
        EditorGUILayout.PropertyField(activity.FindPropertyRelative("objectsOffWhenActivityCompletes"), TipContent("Hide These When Activity Completes", "These objects become invisible after the activity finishes successfully."), true);
        EditorGUI.indentLevel--;
    }

    private void DrawInputSection(SerializedProperty activity)
    {
        DrawStepBox("4. What Should The Child Interact With?", "First choose the main thing the child touches. Then choose how that interaction completes.");
        SerializedProperty input = activity.FindPropertyRelative("childInput");

        BeginnerInteractionMain main = GetBeginnerMain((ActivityInputKind)input.enumValueIndex);
        BeginnerInteractionMain nextMain = (BeginnerInteractionMain)EditorGUILayout.Popup(TipContent("Interaction Type", "Choose only the main input type first. More options appear below after this choice."), (int)main, BeginnerMainLabels);
        if (nextMain != main)
        {
            SetDefaultInputForMain(activity, nextMain);
            main = nextMain;
        }

        ActivityInputKind kind = (ActivityInputKind)input.enumValueIndex;

        if (main == BeginnerInteractionMain.Screen)
        {
            ScreenCompletion completion = kind == ActivityInputKind.TapManyTimes ? ScreenCompletion.RequiredNumberOfTaps : ScreenCompletion.OneTap;
            ScreenCompletion next = (ScreenCompletion)EditorGUILayout.Popup(TipContent("How Should This Complete?"), (int)completion, ScreenCompletionLabels);
            if (next != completion)
                input.enumValueIndex = next == ScreenCompletion.RequiredNumberOfTaps ? (int)ActivityInputKind.TapManyTimes : (int)ActivityInputKind.TapAnywhere;
            if ((ActivityInputKind)input.enumValueIndex == ActivityInputKind.TapManyTimes)
                EditorGUILayout.PropertyField(activity.FindPropertyRelative("requiredInputCount"), TipContent("Required Tap Count"));
        }
        else if (main == BeginnerInteractionMain.One3DObject)
        {
            OneObjectCompletion completion = GetOneObjectCompletion((ActivityInputKind)input.enumValueIndex);
            OneObjectCompletion next = (OneObjectCompletion)EditorGUILayout.Popup(TipContent("How Should This Complete?"), (int)completion, OneObjectCompletionLabels);
            if (next != completion)
                SetOneObjectCompletion(activity, next);

            kind = (ActivityInputKind)input.enumValueIndex;
            if (kind == ActivityInputKind.WaitForStoryThenTapObject)
            {
                DrawWaitForStoryThenTapObjectFields(activity);
                return;
            }

            EditorGUILayout.PropertyField(activity.FindPropertyRelative("targetObject"), TipContent("Object Child Taps", "Drag the 3D object the child should tap. It must have a Collider."));
            if (kind == ActivityInputKind.TapManyTimes)
                EditorGUILayout.PropertyField(activity.FindPropertyRelative("requiredInputCount"), TipContent("Required Tap Count"));
            if (kind == ActivityInputKind.KeepTapping)
                EditorGUILayout.HelpBox("The child must keep tapping this object during Total Activity Time.", MessageType.None);
            if (kind == ActivityInputKind.ProgressGate)
                DrawProgressGateFields(activity);
        }
        else if (main == BeginnerInteractionMain.Multiple3DObjects)
        {
            MultipleObjectCompletion completion = kind == ActivityInputKind.TapObjectsInOrder ? MultipleObjectCompletion.ObjectsTappedInOrder : GetGroupCompletion(activity);
            MultipleObjectCompletion next = (MultipleObjectCompletion)EditorGUILayout.Popup(TipContent("How Should This Complete?"), (int)completion, MultipleObjectCompletionLabels);
            if (next != completion)
                SetMultipleObjectCompletion(activity, next);

            kind = (ActivityInputKind)input.enumValueIndex;
            if (kind == ActivityInputKind.TapObjectsInOrder)
            {
                EditorGUILayout.PropertyField(activity.FindPropertyRelative("targetObjects"), TipContent("Allowed Objects"), true);
                activity.FindPropertyRelative("mustTapInOrder").boolValue = true;
            }
            else
            {
                DrawGroupActionFields(activity);
            }
        }
        else if (main == BeginnerInteractionMain.ChoiceButtons)
        {
            input.enumValueIndex = (int)ActivityInputKind.AnswerQuestion;
            DrawChooseCorrectOptionFields(activity);
        }
        else if (main == BeginnerInteractionMain.UIButton)
        {
            ButtonCompletion completion = kind == ActivityInputKind.TapAnywhereOrButton ? ButtonCompletion.ScreenOrUIButton : ButtonCompletion.UIButtonOnly;
            ButtonCompletion next = (ButtonCompletion)EditorGUILayout.Popup(TipContent("How Should This Complete?"), (int)completion, ButtonCompletionLabels);
            input.enumValueIndex = next == ButtonCompletion.ScreenOrUIButton ? (int)ActivityInputKind.TapAnywhereOrButton : (int)ActivityInputKind.TapButton;
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("optionTexts"), TipContent("Button Texts"), true);
        }
        else
        {
            input.enumValueIndex = (int)ActivityInputKind.WaitOnly;
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("waitOnlySeconds"), TipContent("Wait Seconds"));
        }

        SerializedProperty nextInput = activity.FindPropertyRelative("nextInputRule");
        nextInput.isExpanded = EditorGUILayout.Foldout(nextInput.isExpanded, "Advanced Input Control Optional", true);
        if (nextInput.isExpanded)
        {
            EditorGUI.indentLevel++;
            DrawEnumPopup(nextInput, NextInputValues, NextInputLabels, TipContent("Next Input Is Accepted"));
            if ((ActivityNextInputRule)nextInput.enumValueIndex == ActivityNextInputRule.AfterFixedDelay)
                EditorGUILayout.PropertyField(activity.FindPropertyRelative("nextInputDelaySeconds"), TipContent("Delay Before Next Input"));
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("maxTimeToFirstInput"), TipContent("Max Time To First Input 0 No Limit"));
            EditorGUI.indentLevel--;
        }
    }

    private static BeginnerInteractionMain GetBeginnerMain(ActivityInputKind kind)
    {
        switch (kind)
        {
            case ActivityInputKind.TapObject:
            case ActivityInputKind.TapManyTimes:
            case ActivityInputKind.KeepTapping:
            case ActivityInputKind.ProgressGate:
            case ActivityInputKind.HelpAction:
            case ActivityInputKind.WaitForStoryThenTapObject:
                return BeginnerInteractionMain.One3DObject;
            case ActivityInputKind.TapObjectsInOrder:
            case ActivityInputKind.GroupAction:
                return BeginnerInteractionMain.Multiple3DObjects;
            case ActivityInputKind.ChooseOption:
            case ActivityInputKind.AnswerQuestion:
                return BeginnerInteractionMain.ChoiceButtons;
            case ActivityInputKind.TapButton:
            case ActivityInputKind.TapAnywhereOrButton:
                return BeginnerInteractionMain.UIButton;
            case ActivityInputKind.WaitOnly:
                return BeginnerInteractionMain.NothingWaitOnly;
            default:
                return BeginnerInteractionMain.Screen;
        }
    }

    private void SetDefaultInputForMain(SerializedProperty activity, BeginnerInteractionMain main)
    {
        SerializedProperty input = activity.FindPropertyRelative("childInput");
        switch (main)
        {
            case BeginnerInteractionMain.One3DObject:
                input.enumValueIndex = (int)ActivityInputKind.TapObject;
                activity.FindPropertyRelative("finishWhen").enumValueIndex = (int)ActivityFinishRule.AfterFirstValidInput;
                break;
            case BeginnerInteractionMain.Multiple3DObjects:
                input.enumValueIndex = (int)ActivityInputKind.GroupAction;
                activity.FindPropertyRelative("groupCompletionMode").enumValueIndex = (int)ActivityGroupCompletionMode.AnyAllowedObject;
                break;
            case BeginnerInteractionMain.ChoiceButtons:
                input.enumValueIndex = (int)ActivityInputKind.AnswerQuestion;
                activity.FindPropertyRelative("finishWhen").enumValueIndex = (int)ActivityFinishRule.AfterFirstValidInput;
                activity.FindPropertyRelative("choiceHideUiWhileResultPlays").boolValue = true;
                activity.FindPropertyRelative("choiceBlockInputWhileResultPlays").boolValue = true;
                activity.FindPropertyRelative("choiceReturnQuestionAfterWrong").boolValue = true;
                activity.FindPropertyRelative("pauseStoryWhileActivity").boolValue = true;
                break;
            case BeginnerInteractionMain.UIButton:
                input.enumValueIndex = (int)ActivityInputKind.TapButton;
                break;
            case BeginnerInteractionMain.NothingWaitOnly:
                input.enumValueIndex = (int)ActivityInputKind.WaitOnly;
                break;
            default:
                input.enumValueIndex = (int)ActivityInputKind.TapAnywhere;
                activity.FindPropertyRelative("finishWhen").enumValueIndex = (int)ActivityFinishRule.AfterFirstValidInput;
                break;
        }
    }

    private static OneObjectCompletion GetOneObjectCompletion(ActivityInputKind kind)
    {
        switch (kind)
        {
            case ActivityInputKind.TapManyTimes:
                return OneObjectCompletion.RequiredNumberOfTaps;
            case ActivityInputKind.KeepTapping:
                return OneObjectCompletion.ActiveTappingTime;
            case ActivityInputKind.ProgressGate:
            case ActivityInputKind.HelpAction:
                return OneObjectCompletion.ProgressReachesFull;
            case ActivityInputKind.WaitForStoryThenTapObject:
                return OneObjectCompletion.StoryPausesThenTapObject;
            default:
                return OneObjectCompletion.OneCorrectTap;
        }
    }

    private void SetOneObjectCompletion(SerializedProperty activity, OneObjectCompletion completion)
    {
        SerializedProperty input = activity.FindPropertyRelative("childInput");
        switch (completion)
        {
            case OneObjectCompletion.RequiredNumberOfTaps:
                input.enumValueIndex = (int)ActivityInputKind.TapManyTimes;
                activity.FindPropertyRelative("finishWhen").enumValueIndex = (int)ActivityFinishRule.AfterRequiredInputs;
                break;
            case OneObjectCompletion.ActiveTappingTime:
                input.enumValueIndex = (int)ActivityInputKind.KeepTapping;
                activity.FindPropertyRelative("finishWhen").enumValueIndex = (int)ActivityFinishRule.AfterActiveTimeEnds;
                break;
            case OneObjectCompletion.ProgressReachesFull:
                input.enumValueIndex = (int)ActivityInputKind.ProgressGate;
                activity.FindPropertyRelative("finishWhen").enumValueIndex = (int)ActivityFinishRule.AfterActiveTimeEnds;
                activity.FindPropertyRelative("useProgressBar").boolValue = true;
                break;
            case OneObjectCompletion.StoryPausesThenTapObject:
                input.enumValueIndex = (int)ActivityInputKind.WaitForStoryThenTapObject;
                activity.FindPropertyRelative("pauseStoryWhileActivity").boolValue = true;
                break;
            default:
                input.enumValueIndex = (int)ActivityInputKind.TapObject;
                activity.FindPropertyRelative("finishWhen").enumValueIndex = (int)ActivityFinishRule.AfterFirstValidInput;
                break;
        }
    }

    private MultipleObjectCompletion GetGroupCompletion(SerializedProperty activity)
    {
        ActivityGroupCompletionMode mode = (ActivityGroupCompletionMode)activity.FindPropertyRelative("groupCompletionMode").enumValueIndex;
        if (mode == ActivityGroupCompletionMode.RequiredObjects || mode == ActivityGroupCompletionMode.RequiredObjectCount)
            return MultipleObjectCompletion.RequiredObjects;
        return MultipleObjectCompletion.AnyAllowedObject;
    }

    private void SetMultipleObjectCompletion(SerializedProperty activity, MultipleObjectCompletion completion)
    {
        SerializedProperty input = activity.FindPropertyRelative("childInput");
        if (completion == MultipleObjectCompletion.ObjectsTappedInOrder)
        {
            input.enumValueIndex = (int)ActivityInputKind.TapObjectsInOrder;
            activity.FindPropertyRelative("mustTapInOrder").boolValue = true;
            activity.FindPropertyRelative("finishWhen").enumValueIndex = (int)ActivityFinishRule.AfterRequiredInputs;
            return;
        }

        input.enumValueIndex = (int)ActivityInputKind.GroupAction;
        activity.FindPropertyRelative("groupCompletionMode").enumValueIndex = completion == MultipleObjectCompletion.RequiredObjects ? (int)ActivityGroupCompletionMode.RequiredObjects : (int)ActivityGroupCompletionMode.AnyAllowedObject;
    }


    private void DrawChooseCorrectOptionFields(SerializedProperty activity)
    {
        EditorGUILayout.HelpBox("Simple flow: question appears, child selects one option, wrong answer can retry, correct answer completes the activity.", MessageType.Info);

        SerializedProperty options = activity.FindPropertyRelative("choiceOptions");
        if (options.arraySize < 2)
            options.arraySize = 2;
        if (options.arraySize > 5)
            options.arraySize = 5;

        DrawMiniHeader("1. Options", "Choose how many answer buttons appear. Use 2 to 5 options. The Activity UI Panel should have matching prebuilt option groups assigned once.");
        int optionCount = Mathf.Clamp(options.arraySize, 2, 5);
        int nextCount = EditorGUILayout.IntSlider(TipContent("Option Count", "How many answer buttons the child will see. Minimum 2, maximum 5."), optionCount, 2, 5);
        if (nextCount != options.arraySize)
        {
            options.arraySize = nextCount;
            for (int i = 0; i < options.arraySize; i++)
            {
                SerializedProperty option = options.GetArrayElementAtIndex(i);
                SerializedProperty text = option.FindPropertyRelative("buttonText");
                if (string.IsNullOrWhiteSpace(text.stringValue))
                    text.stringValue = "Option " + (i + 1);
            }
        }

        DrawEnumPopup(activity.FindPropertyRelative("choiceWrongOptionBehaviour"), ChoiceWrongBehaviourValues, ChoiceWrongBehaviourLabels, TipContent("Wrong Option Behaviour", "Choose whether a wrong option can be tapped again, or becomes grey and disabled after the child selects it."));
        DrawEnumPopup(activity.FindPropertyRelative("choiceCorrectBehaviour"), ChoiceCorrectBehaviourValues, ChoiceCorrectBehaviourLabels, TipContent("Correct Option Behaviour", "Continue Story Immediately means the correct option does not need animation or audio. Play Correct Result Then Continue Story means the correct option result plays first."));
        EditorGUILayout.PropertyField(activity.FindPropertyRelative("choiceHideUiWhileResultPlays"), TipContent("Hide UI While Result Plays", "ON = hide the question buttons while the selected option animation or audio plays. Recommended."));
        EditorGUILayout.PropertyField(activity.FindPropertyRelative("choiceBlockInputWhileResultPlays"), TipContent("Block Input While Result Plays", "ON = the child cannot press another option while the selected result is playing. Recommended."));
        EditorGUILayout.PropertyField(activity.FindPropertyRelative("choiceReturnQuestionAfterWrong"), TipContent("Return Question After Wrong Answer", "ON = after a wrong option result finishes, the question and remaining options appear again."));

        DrawMiniHeader("2. Correct Answer", "Pick exactly one correct option. This is safer than ticking multiple checkboxes.");
        int correct = FindCorrectChoiceIndex(options);
        string[] correctLabels = new string[options.arraySize];
        for (int i = 0; i < options.arraySize; i++)
            correctLabels[i] = "Option " + (i + 1);
        int selected = EditorGUILayout.Popup(TipContent("Correct Option Number", "Choose the one option that should complete this activity."), Mathf.Clamp(correct, 0, options.arraySize - 1), correctLabels);
        SetCorrectChoiceIndex(options, selected);

        for (int i = 0; i < options.arraySize; i++)
            DrawChoiceOptionCard(options.GetArrayElementAtIndex(i), i, selected);
    }

    private void DrawChoiceOptionCard(SerializedProperty option, int index, int correctIndex)
    {
        SerializedProperty buttonText = option.FindPropertyRelative("buttonText");
        SerializedProperty actions = option.FindPropertyRelative("scenarioActions");
        string title = (index + 3) + ". Option " + (index + 1) + (index == correctIndex ? " Correct" : " Wrong") + "  |  " + (buttonText != null ? buttonText.stringValue : "");
        EditorGUILayout.Space(5);
        option.isExpanded = EditorGUILayout.Foldout(option.isExpanded, title, true, EditorStyles.foldoutHeader);
        if (!option.isExpanded)
            return;

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        EditorGUILayout.PropertyField(buttonText, TipContent("Button Text", "Text shown on this option button. Keep it short and child friendly."));
        EditorGUI.BeginDisabledGroup(true);
        EditorGUILayout.PropertyField(option.FindPropertyRelative("isCorrect"), TipContent("Is Correct Answer", "This is controlled by Correct Option Number above so only one answer can be correct."));
        EditorGUI.EndDisabledGroup();

        SerializedProperty playResult = option.FindPropertyRelative("playResultForThisOption");
        EditorGUILayout.PropertyField(playResult, TipContent("Play Scenario For This Option", "ON = this option plays its own scenario after it is selected. OFF = no animation or sound is needed."));

        if (playResult.boolValue)
        {
            EditorGUILayout.Space(3);
            DrawMiniHeader("Scenario Setup", "One option can be a full scenario. Add one or more actions. Each action has its own animation speed, loop count, sound, voice and wait settings.");
            EditorGUILayout.PropertyField(option.FindPropertyRelative("scenarioPlayMode"), TipContent("Play Actions", "One By One plays Action 1 then Action 2 then Action 3. Together starts all actions at the same time."));
            EditorGUILayout.IntSlider(option.FindPropertyRelative("scenarioRepeatCount"), 1, 10, TipContent("Scenario Repeat Count", "How many times the full action list repeats."));

            if (actions != null)
            {
                if (actions.arraySize == 0)
                    EditorGUILayout.HelpBox("No scenario actions added yet. Click Add Scenario Action. If you leave this empty, the old single result fields below will be used as fallback.", MessageType.Info);

                for (int a = 0; a < actions.arraySize; a++)
                    DrawChoiceScenarioActionCard(actions, a);

                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button(TipContent("+ Add Scenario Action", "Add one animation, sound, voice, object state change or custom event to this option scenario."), GUILayout.Height(24)))
                {
                    int newIndex = actions.arraySize;
                    actions.InsertArrayElementAtIndex(newIndex);
                    SerializedProperty added = actions.GetArrayElementAtIndex(newIndex);
                    added.FindPropertyRelative("enabled").boolValue = true;
                    added.FindPropertyRelative("actionName").stringValue = "Action " + (newIndex + 1);
                    added.FindPropertyRelative("animator").objectReferenceValue = null;
                    added.FindPropertyRelative("animationClip").objectReferenceValue = null;
                    added.FindPropertyRelative("soundEffect").objectReferenceValue = null;
                    added.FindPropertyRelative("voiceOver").objectReferenceValue = null;
                    added.FindPropertyRelative("narration").objectReferenceValue = null;
                    added.FindPropertyRelative("animationSpeed").floatValue = 1f;
                    added.FindPropertyRelative("animationLoopCount").intValue = 1;
                    added.FindPropertyRelative("waitForAnimation").boolValue = true;
                    added.FindPropertyRelative("soundVolume").floatValue = 1f;
                    added.FindPropertyRelative("voiceVolume").floatValue = 1f;
                    added.FindPropertyRelative("narrationVolume").floatValue = 1f;
                    added.FindPropertyRelative("waitForVoiceOver").boolValue = true;
                    added.FindPropertyRelative("waitForNarration").boolValue = true;
                    added.FindPropertyRelative("extraWaitSeconds").floatValue = 0f;
                }
                if (actions.arraySize > 0 && GUILayout.Button(TipContent("Clear Actions", "Remove all scenario actions for this option."), GUILayout.Height(24)))
                    actions.ClearArray();
                EditorGUILayout.EndHorizontal();
            }

            SerializedProperty legacy = option.FindPropertyRelative("animationClip");
            bool showLegacy = actions == null || actions.arraySize == 0 || legacy.objectReferenceValue != null || option.FindPropertyRelative("soundEffect").objectReferenceValue != null || option.FindPropertyRelative("voiceOver").objectReferenceValue != null || option.FindPropertyRelative("narration").objectReferenceValue != null;
            if (showLegacy)
            {
                EditorGUILayout.Space(5);
                SerializedProperty legacyFold = option.FindPropertyRelative("animationClip");
                legacyFold.isExpanded = EditorGUILayout.Foldout(legacyFold.isExpanded, "Legacy Single Result Optional", true);
                if (legacyFold.isExpanded)
                {
                    EditorGUI.indentLevel++;
                    EditorGUILayout.HelpBox("Use this only for old scenes or quick one-animation options. For new setup, use Scenario Actions above.", MessageType.None);
                    EditorGUILayout.PropertyField(option.FindPropertyRelative("animator"), TipContent("Animator To Play"));
                    EditorGUILayout.PropertyField(option.FindPropertyRelative("animationClip"), TipContent("Animation To Play"));
                    if (option.FindPropertyRelative("animationClip").objectReferenceValue != null)
                    {
                        EditorGUILayout.Slider(option.FindPropertyRelative("animationSpeed"), 0.1f, 3f, TipContent("Animation Speed"));
                        EditorGUILayout.PropertyField(option.FindPropertyRelative("waitForAnimation"), TipContent("Wait For Animation"));
                    }
                    EditorGUILayout.PropertyField(option.FindPropertyRelative("soundEffect"), TipContent("Sound To Play"));
                    if (option.FindPropertyRelative("soundEffect").objectReferenceValue != null)
                        EditorGUILayout.PropertyField(option.FindPropertyRelative("soundVolume"), TipContent("Sound Volume"));
                    EditorGUILayout.PropertyField(option.FindPropertyRelative("voiceOver"), TipContent("Voice To Play"));
                    if (option.FindPropertyRelative("voiceOver").objectReferenceValue != null)
                    {
                        EditorGUILayout.PropertyField(option.FindPropertyRelative("voiceVolume"), TipContent("Voice Volume"));
                        EditorGUILayout.PropertyField(option.FindPropertyRelative("waitForVoiceOver"), TipContent("Wait For Voice Over"));
                    }
                    EditorGUILayout.PropertyField(option.FindPropertyRelative("narration"), TipContent("Narration Optional"));
                    if (option.FindPropertyRelative("narration").objectReferenceValue != null)
                    {
                        EditorGUILayout.PropertyField(option.FindPropertyRelative("narrationVolume"), TipContent("Narration Volume"));
                        EditorGUILayout.PropertyField(option.FindPropertyRelative("waitForNarration"), TipContent("Wait For Narration"));
                    }
                    EditorGUILayout.PropertyField(option.FindPropertyRelative("extraWaitSeconds"), TipContent("Extra Wait After Result"));
                    EditorGUILayout.PropertyField(option.FindPropertyRelative("onSelected"), TipContent("Custom Event Optional"));
                    EditorGUI.indentLevel--;
                }
            }
        }
        else
        {
            EditorGUILayout.HelpBox("No scenario will play for this option. This is correct for a correct option when Correct Option Behaviour is Continue Story Immediately.", MessageType.None);
        }

        EditorGUILayout.EndVertical();
    }

    private void DrawChoiceScenarioActionCard(SerializedProperty actions, int index)
    {
        SerializedProperty action = actions.GetArrayElementAtIndex(index);
        if (action == null) return;

        SerializedProperty name = action.FindPropertyRelative("actionName");
        string title = "Action " + (index + 1) + "  |  " + (name != null ? name.stringValue : "Scenario Action");
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        action.isExpanded = EditorGUILayout.Foldout(action.isExpanded, title, true);
        if (action.isExpanded)
        {
            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(action.FindPropertyRelative("enabled"), TipContent("Use This Action", "Turn this action off without deleting it."));
            EditorGUILayout.PropertyField(name, TipContent("Action Name", "Simple note for your team. Example: Character mops, Door opens, King reacts."));
            SerializedProperty actionAnimator = action.FindPropertyRelative("animator");
            SerializedProperty actionClip = action.FindPropertyRelative("animationClip");
            EditorGUILayout.PropertyField(actionAnimator, TipContent("Animator To Play", "Drag the Animator of the object or character that should animate."));
            EditorGUILayout.PropertyField(actionClip, TipContent("Animation To Play", "Drag the animation clip for this action."));
            if (actionClip.objectReferenceValue != null && actionAnimator.objectReferenceValue == null)
            {
                EditorGUILayout.HelpBox("Teacher Mode: This action has an animation clip but no Animator. Drag the Animator from the object or character that should play this clip.", MessageType.Warning);
            }
            if (actionClip.objectReferenceValue != null)
            {
                EditorGUILayout.Slider(action.FindPropertyRelative("animationSpeed"), 0.1f, 5f, TipContent("Animation Speed", "1 normal. 2 double speed. 0.5 half speed."));
                EditorGUILayout.IntSlider(action.FindPropertyRelative("animationLoopCount"), 1, 20, TipContent("Animation Loop Count", "How many times this animation repeats before this action ends."));
                EditorGUILayout.PropertyField(action.FindPropertyRelative("waitForAnimation"), TipContent("Wait For Animation", "ON = wait for this animation and loop count before continuing."));
            }

            DrawActivityTransformFields(action);
            EditorGUILayout.PropertyField(action.FindPropertyRelative("soundEffect"), TipContent("Sound To Play"));
            if (action.FindPropertyRelative("soundEffect").objectReferenceValue != null)
                EditorGUILayout.PropertyField(action.FindPropertyRelative("soundVolume"), TipContent("Sound Volume"));
            EditorGUILayout.PropertyField(action.FindPropertyRelative("voiceOver"), TipContent("Voice To Play"));
            if (action.FindPropertyRelative("voiceOver").objectReferenceValue != null)
            {
                EditorGUILayout.PropertyField(action.FindPropertyRelative("voiceVolume"), TipContent("Voice Volume"));
                EditorGUILayout.PropertyField(action.FindPropertyRelative("waitForVoiceOver"), TipContent("Wait For Voice Over"));
            }
            EditorGUILayout.PropertyField(action.FindPropertyRelative("narration"), TipContent("Narration Optional"));
            if (action.FindPropertyRelative("narration").objectReferenceValue != null)
            {
                EditorGUILayout.PropertyField(action.FindPropertyRelative("narrationVolume"), TipContent("Narration Volume"));
                EditorGUILayout.PropertyField(action.FindPropertyRelative("waitForNarration"), TipContent("Wait For Narration"));
            }
            EditorGUILayout.PropertyField(action.FindPropertyRelative("objectsToTurnOn"), TipContent("Objects To Turn On Optional"), true);
            EditorGUILayout.PropertyField(action.FindPropertyRelative("objectsToTurnOff"), TipContent("Objects To Turn Off Optional"), true);
            EditorGUILayout.PropertyField(action.FindPropertyRelative("extraWaitSeconds"), TipContent("Extra Wait After Action"));
            EditorGUILayout.PropertyField(action.FindPropertyRelative("onActionPlayed"), TipContent("Custom Event Optional"));
            EditorGUI.indentLevel--;
        }

        EditorGUILayout.BeginHorizontal();
        GUILayout.FlexibleSpace();
        if (GUILayout.Button(TipContent("Remove This Action", "Remove this action from the option scenario."), GUILayout.Width(160)))
        {
            actions.DeleteArrayElementAtIndex(index);
        }
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.EndVertical();
    }

    private void DrawActivityTransformFields(SerializedProperty action)
    {
        SerializedProperty useTransform = action.FindPropertyRelative("useActivityTransform");
        if (useTransform == null)
            return;

        EditorGUILayout.Space(4);
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        EditorGUILayout.PropertyField(useTransform, TipContent("Move This Model Only During Activity", "Optional. OFF = never touch the story model position, rotation, or scale. ON = use a temporary activity pose only while this activity is active."));

        SerializedProperty preview = action.FindPropertyRelative("previewActivityTransformInEditor");
        if (!useTransform.boolValue)
        {
            if (preview != null && preview.boolValue)
            {
                preview.boolValue = false;
                RestorePreviewForAction(action);
            }
            EditorGUILayout.HelpBox("OFF = this activity must not move, rotate, or scale this model. The story position should stay unchanged.", MessageType.None);
            EditorGUILayout.EndVertical();
            return;
        }

        Transform previewTarget = ResolveActivityTransformTargetFromSerialized(action);
        if (previewTarget == null)
            EditorGUILayout.HelpBox("Assign an Animator or model above first. Then this section can move that model only during the activity.", MessageType.Warning);
        else
            EditorGUILayout.HelpBox("This affects only activity time. VFX, popup and story stay in the normal story position.", MessageType.None);

        EditorGUI.indentLevel++;

        SerializedProperty overrideObject = action.FindPropertyRelative("objectToMoveOrScale");
        Animator actionAnimator = action.FindPropertyRelative("animator") != null ? action.FindPropertyRelative("animator").objectReferenceValue as Animator : null;
        if (actionAnimator == null || overrideObject.objectReferenceValue != null)
            EditorGUILayout.PropertyField(overrideObject, TipContent("Model To Move Optional", "Usually leave empty. The assigned Animator model is used automatically."));

        EditorGUILayout.PropertyField(action.FindPropertyRelative("activityPosition"), TipContent("Activity Position"));
        EditorGUILayout.PropertyField(action.FindPropertyRelative("activityRotationEuler"), TipContent("Activity Rotation"));
        EditorGUILayout.PropertyField(action.FindPropertyRelative("activityScale"), TipContent("Activity Scale"));
        EditorGUILayout.PropertyField(action.FindPropertyRelative("restoreTransformAfterAction"), TipContent("Return To Story Position After Activity"));

        if (preview != null)
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Easy Position Buttons", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(TipContent("Use Current Position", "Move the model in the Scene view, then click this. This saves the current pose as the activity pose.")))
                CaptureActivityPoseFromCurrentScene(action);
            if (GUILayout.Button(TipContent("Back To Story Position", "Turns preview off and returns the model to its normal story position.")))
            {
                preview.boolValue = false;
                RestorePreviewForAction(action);
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(TipContent("Copy Position", "Copies the current Activity Position, Rotation and Scale so you can paste it into another action.")))
                CopyActivityTransformToClipboard(action);
            EditorGUI.BeginDisabledGroup(!hasActivityTransformClipboard);
            if (GUILayout.Button(TipContent("Paste Position", "Pastes copied Activity Position, Rotation and Scale into this action.")))
                PasteActivityTransformFromClipboard(action);
            EditorGUI.EndDisabledGroup();
            if (GUILayout.Button(TipContent("Clear", "Resets Activity Position to 0, Rotation to 0, and Scale to 1.")))
                ClearActivityTransform(action);
            EditorGUILayout.EndHorizontal();

            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(preview, TipContent("Preview Position", "ON = show the activity position in Scene view while setting up. OFF = go back to story position."));
            if (EditorGUI.EndChangeCheck())
            {
                if (preview.boolValue)
                    ApplyEditorPreviewForSerializedAction(action);
                else
                    RestorePreviewForAction(action);
            }

            if (preview.boolValue)
                ApplyEditorPreviewForSerializedAction(action);
        }

        EditorGUI.indentLevel--;
        EditorGUILayout.EndVertical();
    }

    private void CopyActivityTransformToClipboard(SerializedProperty action)
    {
        activityTransformClipboardPosition = action.FindPropertyRelative("activityPosition").vector3Value;
        activityTransformClipboardRotation = action.FindPropertyRelative("activityRotationEuler").vector3Value;
        activityTransformClipboardScale = action.FindPropertyRelative("activityScale").vector3Value;
        hasActivityTransformClipboard = true;
    }

    private void PasteActivityTransformFromClipboard(SerializedProperty action)
    {
        if (!hasActivityTransformClipboard) return;
        action.FindPropertyRelative("activityPosition").vector3Value = activityTransformClipboardPosition;
        action.FindPropertyRelative("activityRotationEuler").vector3Value = activityTransformClipboardRotation;
        action.FindPropertyRelative("activityScale").vector3Value = activityTransformClipboardScale;
        ApplyEditorPreviewForSerializedAction(action);
    }

    private void ClearActivityTransform(SerializedProperty action)
    {
        action.FindPropertyRelative("activityPosition").vector3Value = Vector3.zero;
        action.FindPropertyRelative("activityRotationEuler").vector3Value = Vector3.zero;
        action.FindPropertyRelative("activityScale").vector3Value = Vector3.one;
        ApplyEditorPreviewForSerializedAction(action);
    }

    private void DrawTargetActions(SerializedProperty actions)
    {
        if (actions == null)
            return;

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Target Actions", EditorStyles.boldLabel);
        if (GUILayout.Button(TipContent("+ Add Target Action", "Add one tappable model and the action it should play."), GUILayout.Width(150)))
        {
            int newIndex = actions.arraySize;
            actions.InsertArrayElementAtIndex(newIndex);
            SerializedProperty item = actions.GetArrayElementAtIndex(newIndex);
            if (item != null)
            {
                item.FindPropertyRelative("enabled").boolValue = true;
                item.FindPropertyRelative("actionName").stringValue = "Target Action";
                SerializedProperty scale = item.FindPropertyRelative("activityScale");
                if (scale != null) scale.vector3Value = Vector3.one;
                SerializedProperty restore = item.FindPropertyRelative("restoreTransformAfterAction");
                if (restore != null) restore.boolValue = true;
            }
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.HelpBox("Use this for greeting style activities. Example: child taps Lion or Fox, and both target actions can start together when Play Only Tapped Model is OFF.", MessageType.None);

        for (int i = 0; i < actions.arraySize; i++)
            DrawTargetActionCard(actions, i);

        EditorGUILayout.EndVertical();
    }

    private void DrawTargetActionCard(SerializedProperty actions, int index)
    {
        SerializedProperty action = actions.GetArrayElementAtIndex(index);
        if (action == null)
            return;

        SerializedProperty name = action.FindPropertyRelative("actionName");
        string title = "Target " + (index + 1) + "  |  " + (string.IsNullOrWhiteSpace(name.stringValue) ? "Target Action" : name.stringValue);
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        action.isExpanded = EditorGUILayout.Foldout(action.isExpanded, title, true);
        if (action.isExpanded)
        {
            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(action.FindPropertyRelative("enabled"), TipContent("Use This Target", "Turn this target off without deleting it."));
            EditorGUILayout.PropertyField(name, TipContent("Action Name", "Simple note. Example: Lion handshake or Fox handshake."));
            EditorGUILayout.PropertyField(action.FindPropertyRelative("tapObject"), TipContent("Model Child Can Tap", "Drag the model the child can tap. It must have a Collider."));
            EditorGUILayout.PropertyField(action.FindPropertyRelative("required"), TipContent("Required", "ON = this target must be tapped when the completion rule requires specific objects."));

            SerializedProperty animator = action.FindPropertyRelative("animator");
            SerializedProperty clip = action.FindPropertyRelative("animationClip");
            EditorGUILayout.PropertyField(animator, TipContent("Animator To Play", "Drag the Animator of the model that should animate."));
            EditorGUILayout.PropertyField(clip, TipContent("Animation To Play", "Drag the animation clip for this model."));
            if (clip.objectReferenceValue != null)
            {
                EditorGUILayout.PropertyField(action.FindPropertyRelative("animationSpeed"), TipContent("Animation Speed", "1 normal. 2 faster. 0.5 slower."));
                EditorGUILayout.PropertyField(action.FindPropertyRelative("waitForAnimation"), TipContent("Wait For Animation", "ON = wait until this animation finishes before the activity continues."));
            }

            DrawActivityTransformFields(action);

            EditorGUILayout.PropertyField(action.FindPropertyRelative("soundEffect"), TipContent("Sound To Play Optional"));
            if (action.FindPropertyRelative("soundEffect").objectReferenceValue != null)
                EditorGUILayout.PropertyField(action.FindPropertyRelative("soundVolume"), TipContent("Sound Volume"));
            EditorGUILayout.PropertyField(action.FindPropertyRelative("voiceOver"), TipContent("Voice To Play Optional"));
            if (action.FindPropertyRelative("voiceOver").objectReferenceValue != null)
            {
                EditorGUILayout.PropertyField(action.FindPropertyRelative("voiceVolume"), TipContent("Voice Volume"));
                EditorGUILayout.PropertyField(action.FindPropertyRelative("waitForVoiceOver"), TipContent("Wait For Voice"));
            }
            EditorGUILayout.PropertyField(action.FindPropertyRelative("objectsToTurnOn"), TipContent("Objects To Turn On Optional"), true);
            EditorGUILayout.PropertyField(action.FindPropertyRelative("objectsToTurnOff"), TipContent("Objects To Turn Off Optional"), true);
            EditorGUILayout.PropertyField(action.FindPropertyRelative("extraWaitSeconds"), TipContent("Extra Wait After Action"));
            EditorGUILayout.PropertyField(action.FindPropertyRelative("onTapped"), TipContent("Custom Event Optional"));
            EditorGUI.indentLevel--;
        }

        EditorGUILayout.BeginHorizontal();
        GUILayout.FlexibleSpace();
        if (GUILayout.Button(TipContent("Remove This Target", "Remove this target action."), GUILayout.Width(160)))
            actions.DeleteArrayElementAtIndex(index);
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.EndVertical();
    }

    private Transform ResolveResultActivityTransformTargetFromSerialized(SerializedProperty activity)
    {
        if (activity == null)
            return null;

        SerializedProperty overrideObject = activity.FindPropertyRelative("resultObjectToMoveOrScale");
        if (overrideObject != null && overrideObject.objectReferenceValue is GameObject go)
            return go.transform;

        SerializedProperty animatorProp = activity.FindPropertyRelative("resultAnimator");
        if (animatorProp != null && animatorProp.objectReferenceValue is Animator animator)
            return animator.transform;

        return null;
    }

    private void SaveResultStoryPoseFromCurrentScene(SerializedProperty activity)
    {
        Transform targetTransform = ResolveResultActivityTransformTargetFromSerialized(activity);
        if (activity == null || targetTransform == null)
            return;

        activity.FindPropertyRelative("resultStoryPosition").vector3Value = targetTransform.localPosition;
        activity.FindPropertyRelative("resultStoryRotationEuler").vector3Value = targetTransform.localEulerAngles;
        activity.FindPropertyRelative("resultStoryScale").vector3Value = targetTransform.localScale == Vector3.zero ? Vector3.one : targetTransform.localScale;
        activity.FindPropertyRelative("resultHasSavedStoryPose").boolValue = true;
        SerializedProperty preview = activity.FindPropertyRelative("resultPreviewActivityTransformInEditor");
        if (preview != null) preview.boolValue = false;

        serializedObject.ApplyModifiedProperties();
        EditorUtility.SetDirty(target);
        SceneView.RepaintAll();
    }

    private void EnsureResultSavedStoryPose(SerializedProperty activity, Transform targetTransform)
    {
        if (activity == null || targetTransform == null)
            return;
        SerializedProperty hasStoryPose = activity.FindPropertyRelative("resultHasSavedStoryPose");
        if (hasStoryPose == null || hasStoryPose.boolValue)
            return;
        activity.FindPropertyRelative("resultStoryPosition").vector3Value = targetTransform.localPosition;
        activity.FindPropertyRelative("resultStoryRotationEuler").vector3Value = targetTransform.localEulerAngles;
        activity.FindPropertyRelative("resultStoryScale").vector3Value = targetTransform.localScale == Vector3.zero ? Vector3.one : targetTransform.localScale;
        hasStoryPose.boolValue = true;
        serializedObject.ApplyModifiedProperties();
        EditorUtility.SetDirty(target);
    }

    private bool RestoreSavedResultStoryPose(SerializedProperty activity, Transform targetTransform)
    {
        if (activity == null || targetTransform == null)
            return false;
        SerializedProperty hasStoryPose = activity.FindPropertyRelative("resultHasSavedStoryPose");
        if (hasStoryPose == null || !hasStoryPose.boolValue)
            return false;
        targetTransform.localPosition = activity.FindPropertyRelative("resultStoryPosition").vector3Value;
        targetTransform.localEulerAngles = activity.FindPropertyRelative("resultStoryRotationEuler").vector3Value;
        targetTransform.localScale = SafeEditorActivityScale(activity.FindPropertyRelative("resultStoryScale").vector3Value);
        return true;
    }

    private void RestorePreviewForResult(SerializedProperty activity)
    {
        Transform targetTransform = ResolveResultActivityTransformTargetFromSerialized(activity);
        if (targetTransform == null)
            return;
        if (RestoreSavedResultStoryPose(activity, targetTransform))
        {
            activityTransformPreviewSnapshots.Remove(targetTransform.GetInstanceID());
            SceneView.RepaintAll();
            return;
        }
        RestorePreviewTarget(targetTransform.GetInstanceID(), targetTransform);
    }

    private void CopyCurrentScenePoseIntoResult(SerializedProperty activity)
    {
        Transform targetTransform = ResolveResultActivityTransformTargetFromSerialized(activity);
        if (targetTransform == null)
            return;
        activity.FindPropertyRelative("resultActivityPosition").vector3Value = targetTransform.localPosition;
        activity.FindPropertyRelative("resultActivityRotationEuler").vector3Value = targetTransform.localEulerAngles;
        activity.FindPropertyRelative("resultActivityScale").vector3Value = targetTransform.localScale;
        serializedObject.ApplyModifiedProperties();
        EditorUtility.SetDirty(target);
    }

    private void ResetResultActivityPoseValues(SerializedProperty activity)
    {
        if (activity == null) return;
        activity.FindPropertyRelative("resultActivityPosition").vector3Value = Vector3.zero;
        activity.FindPropertyRelative("resultActivityRotationEuler").vector3Value = Vector3.zero;
        activity.FindPropertyRelative("resultActivityScale").vector3Value = Vector3.one;
        serializedObject.ApplyModifiedProperties();
        EditorUtility.SetDirty(target);
        SceneView.RepaintAll();
    }

    private Transform ResolveActivityTransformTargetFromSerialized(SerializedProperty action)
    {
        if (action == null)
            return null;

        SerializedProperty overrideObject = action.FindPropertyRelative("objectToMoveOrScale");
        if (overrideObject != null && overrideObject.objectReferenceValue is GameObject go)
            return go.transform;

        SerializedProperty animatorProp = action.FindPropertyRelative("animator");
        if (animatorProp != null && animatorProp.objectReferenceValue is Animator animator)
            return animator.transform;

        return null;
    }

    private void CaptureActivityPoseFromCurrentScene(SerializedProperty action)
    {
        Transform targetTransform = ResolveActivityTransformTargetFromSerialized(action);
        if (action == null || targetTransform == null)
            return;

        SerializedProperty position = action.FindPropertyRelative("activityPosition");
        SerializedProperty rotation = action.FindPropertyRelative("activityRotationEuler");
        SerializedProperty scale = action.FindPropertyRelative("activityScale");
        if (position == null || rotation == null || scale == null)
            return;

        position.vector3Value = targetTransform.localPosition;
        rotation.vector3Value = targetTransform.localEulerAngles;
        scale.vector3Value = targetTransform.localScale == Vector3.zero ? Vector3.one : targetTransform.localScale;
        serializedObject.ApplyModifiedProperties();
        EditorUtility.SetDirty(target);
        SceneView.RepaintAll();
    }

    private void SaveStoryPoseFromCurrentScene(SerializedProperty action)
    {
        Transform targetTransform = ResolveActivityTransformTargetFromSerialized(action);
        if (action == null || targetTransform == null)
            return;

        SerializedProperty hasStoryPose = action.FindPropertyRelative("hasSavedStoryPose");
        SerializedProperty storyPosition = action.FindPropertyRelative("storyPosition");
        SerializedProperty storyRotation = action.FindPropertyRelative("storyRotationEuler");
        SerializedProperty storyScale = action.FindPropertyRelative("storyScale");
        SerializedProperty preview = action.FindPropertyRelative("previewActivityTransformInEditor");

        if (hasStoryPose == null || storyPosition == null || storyRotation == null || storyScale == null)
            return;

        storyPosition.vector3Value = targetTransform.localPosition;
        storyRotation.vector3Value = targetTransform.localEulerAngles;
        storyScale.vector3Value = targetTransform.localScale == Vector3.zero ? Vector3.one : targetTransform.localScale;
        hasStoryPose.boolValue = true;
        if (preview != null) preview.boolValue = false;

        serializedObject.ApplyModifiedProperties();
        EditorUtility.SetDirty(target);
        SceneView.RepaintAll();
    }

    private void ResetActivityPoseValues(SerializedProperty action)
    {
        if (action == null)
            return;

        SerializedProperty position = action.FindPropertyRelative("activityPosition");
        SerializedProperty rotation = action.FindPropertyRelative("activityRotationEuler");
        SerializedProperty scale = action.FindPropertyRelative("activityScale");

        if (position != null) position.vector3Value = Vector3.zero;
        if (rotation != null) rotation.vector3Value = Vector3.zero;
        if (scale != null) scale.vector3Value = Vector3.one;

        serializedObject.ApplyModifiedProperties();
        EditorUtility.SetDirty(target);
        SceneView.RepaintAll();
    }

    private void EnsureSavedStoryPose(SerializedProperty action, Transform targetTransform)
    {
        if (action == null || targetTransform == null)
            return;

        SerializedProperty hasStoryPose = action.FindPropertyRelative("hasSavedStoryPose");
        SerializedProperty storyPosition = action.FindPropertyRelative("storyPosition");
        SerializedProperty storyRotation = action.FindPropertyRelative("storyRotationEuler");
        SerializedProperty storyScale = action.FindPropertyRelative("storyScale");

        if (hasStoryPose == null || storyPosition == null || storyRotation == null || storyScale == null)
            return;

        if (hasStoryPose.boolValue)
            return;

        storyPosition.vector3Value = targetTransform.localPosition;
        storyRotation.vector3Value = targetTransform.localEulerAngles;
        storyScale.vector3Value = targetTransform.localScale == Vector3.zero ? Vector3.one : targetTransform.localScale;
        hasStoryPose.boolValue = true;
        serializedObject.ApplyModifiedProperties();
        EditorUtility.SetDirty(target);
    }

    private bool RestoreSavedStoryPose(SerializedProperty action, Transform targetTransform)
    {
        if (action == null || targetTransform == null)
            return false;

        SerializedProperty hasStoryPose = action.FindPropertyRelative("hasSavedStoryPose");
        SerializedProperty storyPosition = action.FindPropertyRelative("storyPosition");
        SerializedProperty storyRotation = action.FindPropertyRelative("storyRotationEuler");
        SerializedProperty storyScale = action.FindPropertyRelative("storyScale");

        if (hasStoryPose == null || storyPosition == null || storyRotation == null || storyScale == null)
            return false;
        if (!hasStoryPose.boolValue)
            return false;

        targetTransform.localPosition = storyPosition.vector3Value;
        targetTransform.localEulerAngles = storyRotation.vector3Value;
        targetTransform.localScale = SafeEditorActivityScale(storyScale.vector3Value);
        return true;
    }

    private void CopyCurrentScenePoseIntoAction(SerializedProperty action)
    {
        Transform targetTransform = ResolveActivityTransformTargetFromSerialized(action);
        if (targetTransform == null)
            return;

        action.FindPropertyRelative("activityPosition").vector3Value = targetTransform.localPosition;
        action.FindPropertyRelative("activityRotationEuler").vector3Value = targetTransform.localEulerAngles;
        action.FindPropertyRelative("activityScale").vector3Value = targetTransform.localScale;
        serializedObject.ApplyModifiedProperties();
        EditorUtility.SetDirty(target);
    }

    private void ApplyEditorActivityTransformPreviews()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            RestoreAllActivityTransformPreviewsStatic();
            return;
        }

        activePreviewTargetsThisDraw.Clear();
        ContentController controller = target as ContentController;
        if (controller == null || controller.Activities == null)
            return;

        foreach (ActivityStep step in controller.Activities)
        {
            if (step == null)
                continue;

            ApplyEditorPreviewForResult(step);

            if (step.targetActions != null)
            {
                foreach (ActivityTargetAction action in step.targetActions)
                    ApplyEditorPreviewForAction(action);
            }

            if (step.choiceOptions != null)
            {
                foreach (ActivityChoiceOption option in step.choiceOptions)
                {
                    if (option == null || option.scenarioActions == null)
                        continue;
                    foreach (ActivityScenarioAction action in option.scenarioActions)
                        ApplyEditorPreviewForAction(action);
                }
            }

            if (step.groupActions != null)
            {
                foreach (ActivityGroupAction action in step.groupActions)
                    ApplyEditorPreviewForAction(action);
            }

            if (step.reactions != null)
            {
                foreach (ActivityReaction reaction in step.reactions)
                    ApplyEditorPreviewForAction(reaction);
            }
        }

        RestoreInactiveActivityTransformPreviews();
    }

    private void ApplyEditorPreviewForResult(ActivityStep step)
    {
        if (step == null)
            return;
        ApplyEditorPreview(step.resultUseActivityTransform, step.resultPreviewActivityTransformInEditor, step.resultObjectToMoveOrScale, step.resultCopyTransformFrom, step.resultActivityPosition, step.resultActivityRotationEuler, step.resultActivityScale, step.resultAnimator);
    }

    private void ApplyEditorPreviewForSerializedAction(SerializedProperty action)
    {
        if (action == null)
            return;

        SerializedProperty useTransform = action.FindPropertyRelative("useActivityTransform");
        SerializedProperty preview = action.FindPropertyRelative("previewActivityTransformInEditor");
        SerializedProperty objectToMove = action.FindPropertyRelative("objectToMoveOrScale");
        SerializedProperty copyFrom = action.FindPropertyRelative("copyTransformFrom");
        SerializedProperty position = action.FindPropertyRelative("activityPosition");
        SerializedProperty rotation = action.FindPropertyRelative("activityRotationEuler");
        SerializedProperty scale = action.FindPropertyRelative("activityScale");
        SerializedProperty animator = action.FindPropertyRelative("animator");

        if (useTransform == null || preview == null || position == null || rotation == null || scale == null)
            return;

        GameObject objectValue = objectToMove != null ? objectToMove.objectReferenceValue as GameObject : null;
        Transform copyValue = copyFrom != null ? copyFrom.objectReferenceValue as Transform : null;
        Animator animatorValue = animator != null ? animator.objectReferenceValue as Animator : null;

        ApplyEditorPreview(useTransform.boolValue, preview.boolValue, objectValue, copyValue, position.vector3Value, rotation.vector3Value, scale.vector3Value, animatorValue);
    }

    private void ApplyEditorPreviewForAction(ActivityTargetAction action)
    {
        if (action == null)
            return;
        ApplyEditorPreview(action.useActivityTransform, action.previewActivityTransformInEditor, action.objectToMoveOrScale, action.copyTransformFrom, action.activityPosition, action.activityRotationEuler, action.activityScale, action.animator);
    }

    private void ApplyEditorPreviewForAction(ActivityScenarioAction action)
    {
        if (action == null)
            return;
        ApplyEditorPreview(action.useActivityTransform, action.previewActivityTransformInEditor, action.objectToMoveOrScale, action.copyTransformFrom, action.activityPosition, action.activityRotationEuler, action.activityScale, action.animator);
    }

    private void ApplyEditorPreviewForAction(ActivityGroupAction action)
    {
        if (action == null)
            return;
        ApplyEditorPreview(action.useActivityTransform, action.previewActivityTransformInEditor, action.objectToMoveOrScale, action.copyTransformFrom, action.activityPosition, action.activityRotationEuler, action.activityScale, action.animator);
    }


    private void ApplyEditorPreviewForAction(ActivityReaction reaction)
    {
        if (reaction == null)
            return;
        ApplyEditorPreview(reaction.useActivityTransform, reaction.previewActivityTransformInEditor, reaction.objectToMoveOrScale, reaction.copyTransformFrom, reaction.activityPosition, reaction.activityRotationEuler, reaction.activityScale, reaction.animator);
    }

    private void ApplyEditorPreview(bool useTransform, bool previewInEditor, GameObject objectToMoveOrScale, Transform copyTransformFrom, Vector3 activityPosition, Vector3 activityRotationEuler, Vector3 activityScale, Animator animator)
    {
        if (!useTransform || !previewInEditor)
            return;

        Transform targetTransform = objectToMoveOrScale != null ? objectToMoveOrScale.transform : (animator != null ? animator.transform : null);
        if (targetTransform == null)
            return;

        int id = targetTransform.GetInstanceID();
        activePreviewTargetsThisDraw.Add(id);

        if (!activityTransformPreviewSnapshots.ContainsKey(id))
        {
            activityTransformPreviewSnapshots[id] = new ActivityTransformPreviewSnapshot
            {
                localPosition = targetTransform.localPosition,
                localRotation = targetTransform.localRotation,
                localScale = targetTransform.localScale
            };
        }

        if (copyTransformFrom != null)
        {
            targetTransform.position = copyTransformFrom.position;
            targetTransform.rotation = copyTransformFrom.rotation;
            targetTransform.localScale = SafeEditorActivityScale(copyTransformFrom.localScale);
        }
        else
        {
            targetTransform.localPosition = activityPosition;
            targetTransform.localEulerAngles = activityRotationEuler;
            targetTransform.localScale = SafeEditorActivityScale(activityScale);
        }

        SceneView.RepaintAll();
    }

    private Vector3 SafeEditorActivityScale(Vector3 scale)
    {
        if (scale == Vector3.zero)
            return Vector3.one;
        if (Mathf.Approximately(scale.x, 0f)) scale.x = 1f;
        if (Mathf.Approximately(scale.y, 0f)) scale.y = 1f;
        if (Mathf.Approximately(scale.z, 0f)) scale.z = 1f;
        return scale;
    }

    private void RestorePreviewForAction(SerializedProperty action)
    {
        Transform targetTransform = ResolveActivityTransformTargetFromSerialized(action);
        if (targetTransform == null)
            return;

        if (RestoreSavedStoryPose(action, targetTransform))
        {
            activityTransformPreviewSnapshots.Remove(targetTransform.GetInstanceID());
            SceneView.RepaintAll();
            return;
        }

        RestorePreviewTarget(targetTransform.GetInstanceID(), targetTransform);
    }

    private void RestoreInactiveActivityTransformPreviews()
    {
        List<int> toRestore = new List<int>();
        foreach (int id in activityTransformPreviewSnapshots.Keys)
        {
            if (!activePreviewTargetsThisDraw.Contains(id))
                toRestore.Add(id);
        }

        foreach (int id in toRestore)
        {
            UnityEngine.Object obj = EditorUtility.InstanceIDToObject(id);
            if (obj is Transform transform)
                RestorePreviewTarget(id, transform);
            else
                activityTransformPreviewSnapshots.Remove(id);
        }
    }

    private void RestorePreviewTarget(int id, Transform targetTransform)
    {
        if (targetTransform == null || !activityTransformPreviewSnapshots.TryGetValue(id, out ActivityTransformPreviewSnapshot snapshot))
            return;

        targetTransform.localPosition = snapshot.localPosition;
        targetTransform.localRotation = snapshot.localRotation;
        targetTransform.localScale = snapshot.localScale;
        activityTransformPreviewSnapshots.Remove(id);
        SceneView.RepaintAll();
    }

    private void RestoreAllActivityTransformPreviews()
    {
        RestoreAllActivityTransformPreviewsStatic();
    }

    private static void RestoreSavedActivityPreviewPosesOnAllControllers()
    {
        ContentController[] controllers = Resources.FindObjectsOfTypeAll<ContentController>();
        if (controllers == null) return;

        for (int i = 0; i < controllers.Length; i++)
        {
            ContentController controller = controllers[i];
            if (controller == null || controller.Activities == null) continue;

            foreach (ActivityStep step in controller.Activities)
            {
                if (step == null) continue;

                RestoreSavedPoseForRuntimeResult(step);

                if (step.targetActions != null)
                {
                    foreach (ActivityTargetAction action in step.targetActions)
                        RestoreSavedPoseForRuntimeAction(action);
                }

                if (step.choiceOptions != null)
                {
                    foreach (ActivityChoiceOption option in step.choiceOptions)
                    {
                        if (option == null || option.scenarioActions == null) continue;
                        foreach (ActivityScenarioAction action in option.scenarioActions)
                            RestoreSavedPoseForRuntimeAction(action);
                    }
                }

                if (step.groupActions != null)
                {
                    foreach (ActivityGroupAction action in step.groupActions)
                        RestoreSavedPoseForRuntimeAction(action);
                }

                if (step.reactions != null)
                {
                    foreach (ActivityReaction reaction in step.reactions)
                        RestoreSavedPoseForRuntimeAction(reaction);
                }
            }

            EditorUtility.SetDirty(controller);
        }

        SceneView.RepaintAll();
    }

    private static void RestoreSavedPoseForRuntimeResult(ActivityStep step)
    {
        if (step == null) return;
        Transform targetTransform = step.resultObjectToMoveOrScale != null ? step.resultObjectToMoveOrScale.transform : (step.resultAnimator != null ? step.resultAnimator.transform : null);
        if (targetTransform == null || !step.resultHasSavedStoryPose) return;
        targetTransform.localPosition = step.resultStoryPosition;
        targetTransform.localEulerAngles = step.resultStoryRotationEuler;
        targetTransform.localScale = step.resultStoryScale == Vector3.zero ? Vector3.one : step.resultStoryScale;
        step.resultPreviewActivityTransformInEditor = false;
    }

    private static void RestoreSavedPoseForRuntimeAction(ActivityTargetAction action)
    {
        if (action == null) return;
        Transform targetTransform = action.objectToMoveOrScale != null ? action.objectToMoveOrScale.transform : (action.animator != null ? action.animator.transform : null);
        if (targetTransform == null || !action.hasSavedStoryPose) return;
        targetTransform.localPosition = action.storyPosition;
        targetTransform.localEulerAngles = action.storyRotationEuler;
        targetTransform.localScale = action.storyScale == Vector3.zero ? Vector3.one : action.storyScale;
        action.previewActivityTransformInEditor = false;
    }

    private static void RestoreSavedPoseForRuntimeAction(ActivityScenarioAction action)
    {
        if (action == null) return;
        Transform targetTransform = action.objectToMoveOrScale != null ? action.objectToMoveOrScale.transform : (action.animator != null ? action.animator.transform : null);
        if (targetTransform == null || !action.hasSavedStoryPose) return;
        targetTransform.localPosition = action.storyPosition;
        targetTransform.localEulerAngles = action.storyRotationEuler;
        targetTransform.localScale = action.storyScale == Vector3.zero ? Vector3.one : action.storyScale;
        action.previewActivityTransformInEditor = false;
    }

    private static void RestoreSavedPoseForRuntimeAction(ActivityGroupAction action)
    {
        if (action == null) return;
        Transform targetTransform = action.objectToMoveOrScale != null ? action.objectToMoveOrScale.transform : (action.animator != null ? action.animator.transform : null);
        if (targetTransform == null || !action.hasSavedStoryPose) return;
        targetTransform.localPosition = action.storyPosition;
        targetTransform.localEulerAngles = action.storyRotationEuler;
        targetTransform.localScale = action.storyScale == Vector3.zero ? Vector3.one : action.storyScale;
        action.previewActivityTransformInEditor = false;
    }

    private static void RestoreSavedPoseForRuntimeAction(ActivityReaction reaction)
    {
        if (reaction == null) return;
        Transform targetTransform = reaction.objectToMoveOrScale != null ? reaction.objectToMoveOrScale.transform : (reaction.animator != null ? reaction.animator.transform : null);
        if (targetTransform == null || !reaction.hasSavedStoryPose) return;
        targetTransform.localPosition = reaction.storyPosition;
        targetTransform.localEulerAngles = reaction.storyRotationEuler;
        targetTransform.localScale = reaction.storyScale == Vector3.zero ? Vector3.one : reaction.storyScale;
        reaction.previewActivityTransformInEditor = false;
    }

    private static void RestoreAllActivityTransformPreviewsStatic()
    {
        List<int> ids = new List<int>(activityTransformPreviewSnapshots.Keys);
        foreach (int id in ids)
        {
            UnityEngine.Object obj = EditorUtility.InstanceIDToObject(id);
            if (obj is Transform transform)
            {
                if (activityTransformPreviewSnapshots.TryGetValue(id, out ActivityTransformPreviewSnapshot snapshot))
                {
                    transform.localPosition = snapshot.localPosition;
                    transform.localRotation = snapshot.localRotation;
                    transform.localScale = snapshot.localScale;
                    SceneView.RepaintAll();
                }
                activityTransformPreviewSnapshots.Remove(id);
            }
            else
            {
                activityTransformPreviewSnapshots.Remove(id);
            }
        }
    }

    private int FindCorrectChoiceIndex(SerializedProperty options)
    {
        for (int i = 0; i < options.arraySize; i++)
        {
            if (options.GetArrayElementAtIndex(i).FindPropertyRelative("isCorrect").boolValue)
                return i;
        }
        return 0;
    }

    private void SetCorrectChoiceIndex(SerializedProperty options, int selected)
    {
        for (int i = 0; i < options.arraySize; i++)
            options.GetArrayElementAtIndex(i).FindPropertyRelative("isCorrect").boolValue = i == selected;
    }

    private void DrawWaitForStoryThenTapObjectFields(SerializedProperty activity)
    {
        EditorGUILayout.HelpBox("Simple flow: story reaches a point, story pauses, child taps the object, the object rises, breaks or changes, then story resumes.", MessageType.Info);

        DrawMiniHeader("1. Tap Object", "Drag the object the child should tap. Example: whole drum parent.");
        EditorGUILayout.PropertyField(activity.FindPropertyRelative("storyMomentTapObject"), TipContent("3D Object To Tap", "Drag the object the child taps. It must have a Collider."));
        EditorGUILayout.PropertyField(activity.FindPropertyRelative("storyMomentTapSound"), TipContent("Sound On Correct Tap Optional"));
        if (activity.FindPropertyRelative("storyMomentTapSound").objectReferenceValue != null)
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("storyMomentTapSoundVolume"), TipContent("Correct Tap Sound Volume"));

        DrawMiniHeader("2. Time And Progress", "Use one simple timing setup for this activity.");
        EditorGUILayout.PropertyField(activity.FindPropertyRelative("storyMomentTotalActivitySeconds"), TipContent("Total Activity Time", "Maximum time this activity can stay active. 0 means no limit."));
        EditorGUILayout.PropertyField(activity.FindPropertyRelative("storyMomentShowHintAfterSeconds"), TipContent("Show Hint After Seconds", "From activity start, show hint after this time if no correct tap happened."));
        EditorGUILayout.PropertyField(activity.FindPropertyRelative("storyMomentSkipAfterHintSeconds"), TipContent("Skip After Hint Seconds", "After the hint appears, skip if there is still no correct tap after this many seconds."));
        EditorGUILayout.PropertyField(activity.FindPropertyRelative("storyMomentHintText"), TipContent("Hint Text"));
        EditorGUILayout.HelpBox("Progress bar is controlled only in Section 7: Progress Bar Optional. This keeps setup in one place.", MessageType.None);

        DrawMiniHeader("3. What Completes The Activity", "Choose tap count or active tapping time.");
        DrawEnumPopup(activity.FindPropertyRelative("storyMomentCompletesBy"), StoryMomentCompleteValues, StoryMomentCompleteLabels, TipContent("Complete By"));
        StoryMomentTapCompletionMode completeBy = (StoryMomentTapCompletionMode)activity.FindPropertyRelative("storyMomentCompletesBy").enumValueIndex;
        EditorGUI.indentLevel++;
        if (completeBy == StoryMomentTapCompletionMode.RequiredTapCount)
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("storyMomentRequiredTaps"), TipContent("Required Correct Taps"));
        else
        {
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("storyMomentRequiredTappingSeconds"), TipContent("Required Active Tapping Seconds"));
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("storyMomentTapActiveWindowSeconds"), TipContent("Tap Stays Active For Seconds"));
        }
        EditorGUILayout.PropertyField(activity.FindPropertyRelative("storyMomentProgressDropsIfChildStops"), TipContent("Bar Goes Down If Child Stops"));
        if (activity.FindPropertyRelative("storyMomentProgressDropsIfChildStops").boolValue)
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("storyMomentProgressDropSpeed"), TipContent("How Fast It Goes Down"));
        EditorGUI.indentLevel--;

        DrawMiniHeader("4. Tap Feedback", "The tapped object rises and shakes while the child taps.");
        EditorGUILayout.PropertyField(activity.FindPropertyRelative("storyMomentMovingObject"), TipContent("Object That Moves", "Usually the whole object parent. Leave empty to move the 3D Object To Tap."));
        EditorGUILayout.Slider(activity.FindPropertyRelative("storyMomentMoveUpHeight"), 0f, 2f, TipContent("Move Up Height"));
        EditorGUILayout.Slider(activity.FindPropertyRelative("storyMomentMoveSmoothness"), 1f, 30f, TipContent("Move Smoothness"));
        EditorGUILayout.Slider(activity.FindPropertyRelative("storyMomentSlowTapShake"), 0f, 0.2f, TipContent("Slow Tap Shake"));
        EditorGUILayout.Slider(activity.FindPropertyRelative("storyMomentFastTapShake"), 0f, 0.5f, TipContent("Fast Tap Shake"));
        EditorGUILayout.Slider(activity.FindPropertyRelative("storyMomentTapShakeSeconds"), 0.03f, 0.6f, TipContent("Tap Shake Time"));
        EditorGUILayout.Slider(activity.FindPropertyRelative("storyMomentFastTapSpeed"), 1f, 12f, TipContent("Fast Tap Speed"));

        // ── 5. CHARACTER ANIMATION WHILE TAPPING ──────────────────────────────
        EditorGUILayout.Space(6);
        DrawMiniHeader("5. Character Animation While Tapping",
            "Should a character animate while the child is tapping?\n" +
            "Example: the fox should react as the drum gets hit.\n" +
            "Turn this ON and add the fox animation clips below.");

        EditorGUILayout.HelpBox(
            "HOW IT WORKS:\n" +
            "Add 3 animation clips → progress splits into 3 parts automatically.\n" +
            "  0% to 33%  → clip 1 plays and loops\n" +
            "  34% to 66% → clip 2 plays and loops\n" +
            "  67% to 100% → clip 3 plays and loops\n\n" +
            "The animation changes on its own. No percentages needed.",
            MessageType.Info);

        DrawAnimationWhileTappingFields(activity, "fox");

        // ── Hints While Doing Well (inline for Fox Drum and similar activities) ──
        EditorGUILayout.Space(6);
        DrawMilestoneHintsSection(activity);

        DrawMiniHeader("6. Change Object When Complete", "Hide the normal visible part during the heavy shake. Optional: show another object if your project needs it.");
        EditorGUILayout.PropertyField(activity.FindPropertyRelative("storyMomentObjectBeforeComplete"), TipContent("Object To Hide When Complete", "Drag the normal visible part. Example: top_head. It turns OFF during the heavy shake."));
        EditorGUILayout.PropertyField(activity.FindPropertyRelative("storyMomentObjectAfterComplete"), TipContent("Optional Object To Show", "Usually leave empty if the broken layer is already ON under the normal layer. Use only when another object must turn ON."));
        EditorGUILayout.Slider(activity.FindPropertyRelative("storyMomentBreakShakeAmount"), 0f, 0.5f, TipContent("Heavy Shake Amount"));
        EditorGUILayout.Slider(activity.FindPropertyRelative("storyMomentBreakShakeSeconds"), 0.05f, 2f, TipContent("Heavy Shake Time"));
        EditorGUILayout.Slider(activity.FindPropertyRelative("storyMomentSwitchAtShakePercent"), 0f, 1f, TipContent("Switch During Shake"));
        EditorGUILayout.Slider(activity.FindPropertyRelative("storyMomentDropBackSeconds"), 0.05f, 2f, TipContent("Drop Back Time"));
        EditorGUILayout.PropertyField(activity.FindPropertyRelative("storyMomentBreakSound"), TipContent("Break Sound Optional"));
        if (activity.FindPropertyRelative("storyMomentBreakSound").objectReferenceValue != null)
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("storyMomentBreakSoundVolume"), TipContent("Break Sound Volume"));
        EditorGUILayout.PropertyField(activity.FindPropertyRelative("storyMomentExtraWaitAfterComplete"), TipContent("Extra Wait After Complete"));

        DrawMiniHeader("7. Wrong Tap Feedback", "Wrong taps should guide the child, not move or break the object.");
        EditorGUILayout.PropertyField(activity.FindPropertyRelative("storyMomentWrongTapText"), TipContent("Wrong Tap Text"));
        EditorGUILayout.PropertyField(activity.FindPropertyRelative("storyMomentWrongTapSound"), TipContent("Wrong Tap Sound Optional"));
        if (activity.FindPropertyRelative("storyMomentWrongTapSound").objectReferenceValue != null)
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("storyMomentWrongTapSoundVolume"), TipContent("Wrong Tap Sound Volume"));
        EditorGUILayout.PropertyField(activity.FindPropertyRelative("pulseTargetObject"), TipContent("Heartbeat Correct Object"));
        if (activity.FindPropertyRelative("pulseTargetObject").boolValue)
        {
            EditorGUI.indentLevel++;
            EditorGUILayout.Slider(activity.FindPropertyRelative("targetPulseScale"), 1f, 1.6f, TipContent("Heartbeat Size"));
            EditorGUILayout.Slider(activity.FindPropertyRelative("targetPulseSeconds"), 0.1f, 1.2f, TipContent("One Heartbeat Time"));
            EditorGUILayout.IntSlider(activity.FindPropertyRelative("targetPulseRepeatCount"), 1, 6, TipContent("Heartbeat Count"));
            EditorGUI.indentLevel--;
        }

        DrawMiniHeader("8. Finish", "After the object drops back, story resumes from the paused point.");
        EditorGUILayout.PropertyField(activity.FindPropertyRelative("continueAfterComplete"), TipContent("Resume Story After Activity"));
        EditorGUILayout.HelpBox("This activity pauses story only while it runs. It should resume story from the same point, not restart from the beginning.", MessageType.None);
    }

    private void DrawProgressGateFields(SerializedProperty activity)
    {
        EditorGUILayout.HelpBox("Fill Meter By Tapping means: the child taps the assigned object until the meter is full. Only then should the story continue.", MessageType.None);

        EditorGUILayout.PropertyField(activity.FindPropertyRelative("targetObject"), TipContent("Object To Tap", "Drag the character or object the child should tap. It must have a Collider."));

        EditorGUILayout.Space(4);
        DrawMiniHeader("How This Progress Activity Completes", "Choose the simple requirement. The visible progress bar is controlled only in Progress Bar Optional.");
        EditorGUILayout.PropertyField(activity.FindPropertyRelative("progressGateCompletesBy"), TipContent("Activity Completes By", "Choose whether the progress logic completes by tap count, active tapping time, or tap speed."));
        ActivityProgressGateCompletionMode mode = (ActivityProgressGateCompletionMode)activity.FindPropertyRelative("progressGateCompletesBy").enumValueIndex;
        EditorGUI.indentLevel++;
        if (mode == ActivityProgressGateCompletionMode.RequiredTapCount)
        {
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("progressRequiredTaps"), TipContent("Required Taps", "How many correct taps are needed to fill the bar."));
        }
        else if (mode == ActivityProgressGateCompletionMode.RequiredActiveTappingTime)
        {
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("progressRequiredTappingSeconds"), TipContent("Required Active Tapping Time", "How many seconds of active tapping are needed to fill the bar."));
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("progressTapActiveWindowSeconds"), TipContent("Tap Stays Active For Seconds", "After each tap, this many seconds count as active tapping."));
        }
        else if (mode == ActivityProgressGateCompletionMode.RequiredTapSpeedForTime)
        {
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("progressRequiredTapsPerSecond"), TipContent("Required Taps Per Second", "How fast the child must tap before the progress bar fills."));
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("progressRequiredSpeedSeconds"), TipContent("Required Good Tapping Seconds", "How many seconds of fast enough tapping are needed to complete the bar."));
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("progressTapSpeedWindowSeconds"), TipContent("Tap Speed Window", "How many recent seconds are used to calculate tap speed."));
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("progressTapActiveWindowSeconds"), TipContent("Tap Stays Active For Seconds", "After each tap, this many seconds count as active tapping."));
        }
        EditorGUILayout.PropertyField(activity.FindPropertyRelative("progressDropsWhenNotTapping"), TipContent("If Child Stops", "ON = the bar goes down when the child stops. OFF = the bar stays where it is."));
        if (activity.FindPropertyRelative("progressDropsWhenNotTapping").boolValue)
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("progressLossPerSecond"), TipContent("How Fast Should It Go Down", "Percent per second. Example: 10 means the bar drops 10% every second."));
        EditorGUILayout.PropertyField(activity.FindPropertyRelative("progressAutoFinishAfterNoTapSeconds"), TipContent("If Child Stops, Finish After", "If the child started but stops tapping, wait this many seconds. Then play the remaining activity animations and continue story. 0 = wait forever."));
        EditorGUILayout.HelpBox("For monkey style: child taps to fill the bar. If child stops, the current progress animation keeps looping. After this wait time, remaining activity animations play once, then story continues.", MessageType.None);
        EditorGUI.indentLevel--;

        EditorGUILayout.Space(4);
        EditorGUILayout.HelpBox("No-input timeout is controlled in Section 5 Timing. Do not set a second hidden skip time here for new activities.", MessageType.None);
        bool showLegacyProgressTimeout = EditorGUILayout.Foldout(false, "Advanced: Old Progress Timeout", true);
        if (showLegacyProgressTimeout)
        {
            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("progressAutoStartStoryAfterSeconds"), TipContent("Legacy Start Story Anyway After Seconds"));
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("playResultWhenProgressAutoSkips"), TipContent("Legacy Play Result Even If Skipped"));
            EditorGUI.indentLevel--;
        }

        EditorGUILayout.Space(4);
        DrawMiniHeader("Correct Tap Feedback", "Use this for the object the child is supposed to tap. Example: the drum bumps or shakes smoothly on every correct tap.");
        SerializedProperty useCorrectTapFeedback = activity.FindPropertyRelative("progressUseCorrectTapFeedback");
        EditorGUILayout.PropertyField(useCorrectTapFeedback, TipContent("Move Or Shake Correct Tap Object", "ON = when the child taps Object To Tap, the assigned feedback object moves or shakes. This is the drum feedback."));
        if (useCorrectTapFeedback.boolValue)
        {
            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("progressCorrectTapFeedbackObject"), TipContent("Object That Moves Or Shakes", "Drag the drum or the parent object that should visibly move. Leave empty to use Object To Tap."));
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("progressCorrectTapFeedbackStyle"), TipContent("Correct Tap Feedback Style", "Smooth Bump = moves up and returns. Smooth Shake = shakes in place. Bump And Shake = both."));
            EditorGUILayout.Slider(activity.FindPropertyRelative("progressCorrectMoveUpHeight"), 0f, 0.5f, TipContent("Move Up Height", "How far the object moves upward on each correct tap. Use small values like 0.03 to 0.08."));
            EditorGUILayout.Slider(activity.FindPropertyRelative("progressCorrectShakeAmount"), 0f, 0.25f, TipContent("Shake Amount", "How strong the smooth shake is. Use small values like 0.02 to 0.08."));
            EditorGUILayout.Slider(activity.FindPropertyRelative("progressCorrectTapFeedbackSeconds"), 0.03f, 0.6f, TipContent("Feedback Time", "How long the bump or shake lasts after each correct tap."));
            EditorGUILayout.Slider(activity.FindPropertyRelative("progressCorrectReturnSeconds"), 0.01f, 0.4f, TipContent("Return Smooth Time", "How smoothly the object returns to its original position."));
            EditorGUI.indentLevel--;
        }
        EditorGUILayout.PropertyField(activity.FindPropertyRelative("progressTapSound"), TipContent("Correct Tap Sound Optional", "Optional short sound that plays on each correct tap."));
        if (activity.FindPropertyRelative("progressTapSound").objectReferenceValue != null)
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("progressTapSoundVolume"), TipContent("Correct Tap Sound Volume"));

        EditorGUILayout.Space(4);
        DrawMiniHeader("Wrong Tap Feedback", "Use this when the child taps a different object. It should guide the child back to the correct tap object.");
        SerializedProperty useWrongTapFeedback = activity.FindPropertyRelative("progressUseWrongTapFeedback");
        EditorGUILayout.PropertyField(useWrongTapFeedback, TipContent("Use Wrong Tap Feedback", "ON = wrong taps show message, optional sound, optional wrong-object shake, and optional pulse on the correct object."));
        if (useWrongTapFeedback.boolValue)
        {
            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("progressWrongTapText"), TipContent("Wrong Tap Text", "Text shown when the child taps the wrong object. Example: Tap the drum."));
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("progressWrongTapSound"), TipContent("Wrong Tap Sound Optional"));
            if (activity.FindPropertyRelative("progressWrongTapSound").objectReferenceValue != null)
                EditorGUILayout.PropertyField(activity.FindPropertyRelative("progressWrongTapSoundVolume"), TipContent("Wrong Tap Sound Volume"));
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("progressPulseCorrectObjectOnWrongTap"), TipContent("Pulse Correct Object", "ON = pulse or highlight the correct object after a wrong tap."));
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("progressShakeWrongTappedObject"), TipContent("Shake Wrong Tapped Object", "ON = the wrong object gets a small shake. Keep OFF if only the correct object should guide the child."));
            if (activity.FindPropertyRelative("progressShakeWrongTappedObject").boolValue)
            {
                EditorGUILayout.Slider(activity.FindPropertyRelative("progressWrongShakeAmount"), 0f, 0.25f, TipContent("Wrong Tap Shake Amount"));
                EditorGUILayout.Slider(activity.FindPropertyRelative("progressWrongTapFeedbackSeconds"), 0.03f, 0.6f, TipContent("Wrong Tap Feedback Time"));
                EditorGUILayout.Slider(activity.FindPropertyRelative("progressWrongReturnSeconds"), 0.01f, 0.4f, TipContent("Wrong Tap Return Smooth Time"));
            }
            EditorGUI.indentLevel--;
        }

        EditorGUILayout.Space(4);
        DrawMiniHeader("Reaction Character Optional", "Use this when the child taps one object but another character or object should react. Example: child taps drum and lion reacts.");
        SerializedProperty useReactionSequence = activity.FindPropertyRelative("progressUseReactionSequence");
        EditorGUILayout.PropertyField(useReactionSequence, TipContent("Use Reaction Target Animations", "ON = valid taps or progress percent play animations on another object or character."));
        if (useReactionSequence.boolValue)
        {
            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("progressReactionAnimator"), TipContent("Character That Reacts", "Drag the Animator of the character or object that should react while the meter fills. Example: lion Animator."));
            DrawAnimationClipsWithSounds(
                activity,
                activity.FindPropertyRelative("progressReactionAnimations"),
                "progressReactionGroupLoopSound",
                "progressReactionGroupLoopSoundVolume",
                "loopProgressReactionGroupSoundUntilAnimationEnds",
                "progressReactionAnimationSounds",
                "progressReactionAnimationSoundVolumes",
                "Reaction Animation Clips",
                "Sound For This Reaction Group Optional");
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("progressReactionOrder"), TipContent("Animation Selection", "Use By Meter Percent when animation should change based on the meter. With 5 clips: 0 to 20, 20 to 40, 40 to 60, 60 to 80, 80 to 100 percent."));
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("progressReactionPlaybackMode"), TipContent("Playback Mode", "Play On Each Tap plays a reaction every valid tap. Hold By Meter Percent keeps the character on the animation that matches the current meter range."));
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("progressReactionMinimumGapSeconds"), TipContent("Minimum Gap Between Reactions", "Prevents animation spam when the child taps very fast. Use 0 while testing."));
            EditorGUILayout.Slider(activity.FindPropertyRelative("progressReactionAnimationSpeed"), 0.1f, 3f, TipContent("Reaction Animation Speed", "1 is normal speed. 2 is double speed. Do not use 0."));
            EditorGUI.indentLevel--;
        }

        EditorGUILayout.Space(4);
        DrawMiniHeader("Character Animation While Tapping",
            "Should a character animate while the child fills the bar?\n" +
            "Example: monkey climbs as progress fills.\n" +
            "Add clips. Progress splits automatically.");
        EditorGUILayout.HelpBox(
            "HOW IT WORKS:\n" +
            "Add clips → progress is split automatically across them.\n" +
            "3 clips = 0-33%, 34-66%, 67-100%.\n" +
            "5 clips = 0-20%, 21-40%, 41-60%, 61-80%, 81-100%.\n" +
            "No percentages needed. Just add your clips.",
            MessageType.Info);
        DrawAnimationWhileTappingFields(activity, "monkey");

        EditorGUILayout.Space(4);
        DrawMilestoneHintsSection(activity);

        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Story Result After Progress", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("These play after the progress bar is filled, or after the skip timer if Play Result Even If Skipped is ON.", MessageType.None);
        EditorGUILayout.PropertyField(activity.FindPropertyRelative("resultAnimator"), TipContent("Result Animator", "Drag the Animator that should play after the progress is complete."));
        EditorGUILayout.PropertyField(activity.FindPropertyRelative("resultAnimationClip"), TipContent("Result Animation Clip", "Drag the animation that should play after the progress is complete."));
        if (activity.FindPropertyRelative("resultAnimationClip").objectReferenceValue != null)
        {
            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("resultAnimationSpeed"), TipContent("Result Animation Speed"));
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("waitForResultAnimation"), TipContent("Wait For Result Animation", "ON means story continues only after this animation finishes."));
            EditorGUI.indentLevel--;
        }
        EditorGUILayout.PropertyField(activity.FindPropertyRelative("resultVoiceOver"), TipContent("Result Voice Over", "Optional narration that starts after progress is complete."));
        if (activity.FindPropertyRelative("resultVoiceOver").objectReferenceValue != null)
        {
            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("resultVoiceVolume"), TipContent("Voice Volume"));
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("waitForResultVoiceOver"), TipContent("Wait For Voice Over", "ON means story continues only after the voice finishes."));
            EditorGUI.indentLevel--;
        }
        EditorGUILayout.PropertyField(activity.FindPropertyRelative("resultSoundEffect"), TipContent("Result Sound Effect", "Optional sound that starts with the result animation or voice."));
        if (activity.FindPropertyRelative("resultSoundEffect").objectReferenceValue != null)
        {
            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("resultSoundVolume"), TipContent("Sound Volume"));
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("waitForResultSound"), TipContent("Wait For Sound"));
            EditorGUI.indentLevel--;
        }

        DrawResultActivityTransformFields(activity);

        EditorGUILayout.PropertyField(activity.FindPropertyRelative("resultExtraWaitSeconds"), TipContent("Extra Wait After Result", "Optional extra wait before continuing the story."));
    }

    private void DrawResultActivityTransformFields(SerializedProperty activity)
    {
        SerializedProperty useTransform = activity.FindPropertyRelative("resultUseActivityTransform");
        if (useTransform == null)
            return;

        EditorGUILayout.Space(4);
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        EditorGUILayout.LabelField("Activity Position Optional", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(useTransform, TipContent("Move Result Model During Activity", "ON = the result model uses a temporary position, rotation, and scale only while this activity result plays."));

        SerializedProperty preview = activity.FindPropertyRelative("resultPreviewActivityTransformInEditor");
        if (!useTransform.boolValue)
        {
            if (preview != null && preview.boolValue)
            {
                preview.boolValue = false;
                RestorePreviewForResult(activity);
            }
            EditorGUILayout.HelpBox("OFF = the result model keeps its normal story position.", MessageType.None);
            EditorGUILayout.EndVertical();
            return;
        }

        Transform previewTarget = ResolveResultActivityTransformTargetFromSerialized(activity);
        if (previewTarget == null)
            EditorGUILayout.HelpBox("Assign Result Animator above first, or assign Different Object Optional below.", MessageType.Warning);
        else
            EditorGUILayout.HelpBox("Target: " + previewTarget.name + ". Story/VFX use Story Position. Activity Position is used only while this result plays.", MessageType.None);

        EditorGUI.indentLevel++;
        SerializedProperty overrideObject = activity.FindPropertyRelative("resultObjectToMoveOrScale");
        Animator resultAnimatorValue = activity.FindPropertyRelative("resultAnimator") != null ? activity.FindPropertyRelative("resultAnimator").objectReferenceValue as Animator : null;
        if (resultAnimatorValue == null || overrideObject.objectReferenceValue != null)
            EditorGUILayout.PropertyField(overrideObject, TipContent("Different Object Optional", "Usually leave empty. The Result Animator model is used automatically."));

        SerializedProperty copyFrom = activity.FindPropertyRelative("resultCopyTransformFrom");
        EditorGUILayout.PropertyField(copyFrom, TipContent("Copy From Helper Optional", "Optional. Drag an empty helper Transform if you prefer placing a helper in the Scene."));

        if (copyFrom.objectReferenceValue == null)
        {
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("resultActivityPosition"), TipContent("Activity Position", "Temporary local position used only while this activity result plays."));
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("resultActivityRotationEuler"), TipContent("Activity Rotation", "Temporary local rotation used only while this activity result plays."));
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("resultActivityScale"), TipContent("Activity Scale", "Temporary local scale used only while this activity result plays."));
        }

        EditorGUILayout.PropertyField(activity.FindPropertyRelative("resultRestoreTransformAfterAction"), TipContent("Return To Story Position After Activity", "Keep ON. The model returns to its saved story position after the result finishes, resets, or replay starts."));

        if (preview != null)
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Easy Setup", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(TipContent("1. Save Story Position", "Use this while the model is in the normal story/VFX position.")))
                SaveResultStoryPoseFromCurrentScene(activity);
            if (GUILayout.Button(TipContent("2. Back To Story Position", "Restores the model to the saved story position and turns preview off.")))
            {
                preview.boolValue = false;
                RestorePreviewForResult(activity);
            }
            EditorGUILayout.EndHorizontal();

            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(preview, TipContent("Preview / Edit Activity Position In Scene", "ON = show the temporary activity pose in Scene view. Turn OFF before checking story/VFX."));
            if (EditorGUI.EndChangeCheck())
            {
                if (preview.boolValue && previewTarget != null)
                    EnsureResultSavedStoryPose(activity, previewTarget);
                else
                    RestorePreviewForResult(activity);
            }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(TipContent("3. Set Activity Position From Current Scene", "Move the model in Scene view, then click this to save that pose as the activity-only pose.")))
                CopyCurrentScenePoseIntoResult(activity);
            if (GUILayout.Button(TipContent("Reset Activity Pose", "Reset the activity pose values to zero position, zero rotation, and scale one.")))
                ResetResultActivityPoseValues(activity);
            EditorGUILayout.EndHorizontal();
        }
        EditorGUI.indentLevel--;
        EditorGUILayout.EndVertical();
    }

    private void DrawGroupActionFields(SerializedProperty activity)
    {
        EditorGUILayout.HelpBox("Target Set / Group Action means: the child taps one or more allowed objects. Each tapped object can play its own animation, voice, state change, and event. Use this for greeting characters or group reactions.", MessageType.None);

        EditorGUILayout.LabelField("Completion Rule", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(activity.FindPropertyRelative("groupCompletionMode"), TipContent("Complete When", "Any object, all objects, required objects, or a required number of objects."));
        ActivityGroupCompletionMode mode = (ActivityGroupCompletionMode)activity.FindPropertyRelative("groupCompletionMode").enumValueIndex;
        EditorGUI.indentLevel++;
        if (mode == ActivityGroupCompletionMode.RequiredObjectCount)
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("groupRequiredObjectCount"), TipContent("Required Unique Object Count", "How many different objects must be tapped."));
        if (mode == ActivityGroupCompletionMode.RequiredObjects)
            EditorGUILayout.HelpBox("Mark Required inside each Target Action, or add objects to Required Objects if you use the old setup list.", MessageType.None);
        EditorGUI.indentLevel--;

        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Easy Target Actions Recommended", EditorStyles.boldLabel);
        DrawTargetActions(activity.FindPropertyRelative("targetActions"));
        EditorGUILayout.PropertyField(activity.FindPropertyRelative("groupIgnoreRepeatTaps"), TipContent("Ignore Same Object Twice", "ON = the same tapped object counts only once."));
        if (activity.FindPropertyRelative("groupIgnoreRepeatTaps").boolValue)
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("groupRepeatTapMessage"), TipContent("Repeat Tap Message", "Optional message if the child taps an already completed object."));
        EditorGUILayout.PropertyField(activity.FindPropertyRelative("groupPlayOnlyTappedObjectAction"), TipContent("Play Only Tapped Model", "ON = only the tapped model plays its own action. OFF = greeting mode: tap Lion or Fox, and all target actions start together."));

        EditorGUILayout.Space(4);
        showOldGroupSetup = EditorGUILayout.Foldout(showOldGroupSetup, "Advanced Old Group Setup Optional - Leave Closed For Beginner Setup", true);
        if (showOldGroupSetup)
        {
            EditorGUILayout.HelpBox("Advanced only. For beginner setup, use Target Actions above. Do not use this section unless you are maintaining old activity data.", MessageType.None);
            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("groupTapObjects"), TipContent("Allowed Objects To Tap", "Old setup. Drag every object that can start this action."), true);
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("groupRequiredObjects"), TipContent("Required Objects", "Old setup. Used only when completion mode is Required Objects."), true);
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("groupActions"), TipContent("Objects That React Together", "Old setup. Add every character or object that should animate, play sound, or speak."), true);
            EditorGUI.indentLevel--;
        }

        EditorGUILayout.Space(4);
        showAdvancedActivitySetup = EditorGUILayout.Foldout(showAdvancedActivitySetup, "Advanced Old No-Input Timer Optional - Usually Leave Closed", true);
        if (showAdvancedActivitySetup)
        {
            EditorGUILayout.HelpBox("Beginner setup should use Section 5 Timing above. This old timer is kept only for existing activities.", MessageType.None);
            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("groupAutoStartStoryAfterSeconds"), TipContent("Old Auto Play After Seconds", "Legacy timer. Prefer Section 5 Timing. If this runs, the action still plays before story continues."));
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("groupPlayActionsWhenAutoSkipped"), TipContent("Play Action Even If No Tap", "Keep ON. If the child gives no input, the result still plays before story continues."));
            EditorGUI.indentLevel--;
        }

        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("After Target Set Action", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(activity.FindPropertyRelative("groupWaitSecondsBeforeStory"), TipContent("Extra Wait Before Story Continues", "Optional wait after the target action starts."));

        EditorGUILayout.PropertyField(activity.FindPropertyRelative("groupLoopSound"), TipContent("Looping Sound For Whole Group", "Optional. Starts when the group animations start and stops when the group finishes."));
        if (activity.FindPropertyRelative("groupLoopSound").objectReferenceValue != null)
        {
            EditorGUI.indentLevel++;
            EditorGUILayout.Slider(activity.FindPropertyRelative("groupLoopSoundVolume"), 0f, 2f, TipContent("Loop Sound Volume"));
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("loopGroupSoundUntilGroupFinishes"), TipContent("Loop Until Group Finishes", "ON = sound loops until all group animations and voices finish."));
            EditorGUI.indentLevel--;
        }

        EditorGUILayout.PropertyField(activity.FindPropertyRelative("groupResultVoiceOver"), TipContent("Voice Over After Action", "Optional narration after the action starts."));
        if (activity.FindPropertyRelative("groupResultVoiceOver").objectReferenceValue != null)
        {
            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("groupResultVoiceVolume"), TipContent("Voice Volume"));
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("groupWaitForVoiceOver"), TipContent("Wait For Voice Over"));
            EditorGUI.indentLevel--;
        }
    }

    private void DrawHelpActionFields(SerializedProperty activity)
    {
        EditorGUILayout.PropertyField(activity.FindPropertyRelative("targetObject"), TipContent("Object Child Should Help", "Drag the monkey, animal, object, or invisible tap area the child should tap. It must have a Collider."));
        EditorGUILayout.HelpBox("Use Help Action when the story must continue even if the child does nothing. Taps fill the progress bar. If tapping stops, progress drops. When progress is full or the auto-continue time is reached, the animation plays and the story continues.", MessageType.None);

        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Progress", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(activity.FindPropertyRelative("helpProgressGainPerTap"), TipContent("Progress Added Per Tap", "How much the progress bar fills on each correct tap. Example: 20 means five good taps can fill the bar."));
        EditorGUILayout.PropertyField(activity.FindPropertyRelative("helpProgressLossPerSecond"), TipContent("Progress Lost Per Second", "How fast the progress bar falls when the child stops tapping. Use 0 if progress should not fall."));
        EditorGUILayout.PropertyField(activity.FindPropertyRelative("helpCompleteWhenProgressFull"), TipContent("Complete When Progress Is Full", "ON means the activity can finish early when the progress bar reaches 100 percent."));
        EditorGUILayout.PropertyField(activity.FindPropertyRelative("helpAutoContinueAfterSeconds"), TipContent("Auto Continue After Seconds", "Story safety timer. When this time is reached, the animation plays and the story continues even if the child did not fill the bar."));

        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Animation", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(activity.FindPropertyRelative("helpAnimator"), TipContent("Animator To Play", "Drag the Animator from the character or object that should animate."));
        EditorGUILayout.PropertyField(activity.FindPropertyRelative("helpAnimationClip"), TipContent("Animation Clip To Play", "Drag the one animation clip that should react while progress is active and finish before the story continues."));
        EditorGUILayout.PropertyField(activity.FindPropertyRelative("helpAnimationSpeed"), TipContent("Animation Speed", "1 is normal speed. Higher is faster. Lower is slower."));
        EditorGUILayout.PropertyField(activity.FindPropertyRelative("helpResetAnimationWhenProgressIsEmpty"), TipContent("Reset Animation When Progress Is Empty", "ON means the animation returns to the start when the child stops and progress reaches 0."));
        EditorGUILayout.PropertyField(activity.FindPropertyRelative("helpWaitForAnimationBeforeContinue"), TipContent("Wait For Animation Before Story Continues", "ON means the activity waits until the animation finishes before the next story part starts."));

        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Optional Tap Sound", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(activity.FindPropertyRelative("helpTapSound"), TipContent("Sound On Correct Tap Optional", "Optional short sound that plays when the child taps the correct object."));
        if (activity.FindPropertyRelative("helpTapSound").objectReferenceValue != null)
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("helpTapSoundVolume"), TipContent("Tap Sound Volume"));

        EditorGUILayout.Space(6);
        DrawMilestoneHintsSection(activity);
    }

    // ---------------------------------------------------------------
    // SHARED HELPER: Draw the Animation While Tapping fields.
    // Used by WaitForStoryThenTapObject, ProgressGate, and any other
    // tap activity. characterExample is just for the tooltip hint text.
    // ---------------------------------------------------------------
    private void DrawAnimationWhileTappingFields(SerializedProperty activity, string characterExample)
    {
        SerializedProperty useHelper = activity.FindPropertyRelative("progressUseHelperAnimationWhileTapping");

        EditorGUILayout.PropertyField(useHelper, TipContent(
            "Animate A Character While Child Taps",
            "ON = the " + characterExample + " animates as the child taps. Clips change with progress. OFF = no character animation."));

        if (!useHelper.boolValue)
        {
            EditorGUILayout.HelpBox("Turn this ON to make the " + characterExample + " animate while the child is tapping.", MessageType.None);
            return;
        }

        EditorGUI.indentLevel++;

        EditorGUILayout.PropertyField(activity.FindPropertyRelative("progressHelperAnimator"),
            TipContent("Animated Character",
            "Drag the Animator of the " + characterExample + " here. Example: drag the " + characterExample + " Animator from the Scene or Hierarchy."));

        SerializedProperty clips = activity.FindPropertyRelative("progressHelperAnimations");
        DrawAnimationClipsWithSounds(
            activity,
            clips,
            "progressHelperGroupLoopSound",
            "progressHelperGroupLoopSoundVolume",
            "loopProgressHelperGroupSoundUntilAnimationEnds",
            "progressHelperAnimationSounds",
            "progressHelperAnimationSoundVolumes",
            "Animation Clips",
            "Group Sound For These Clips Optional");

        int clipCount = clips.arraySize;
        if (clipCount == 0)
            EditorGUILayout.HelpBox("No clips added yet. Drag animation clips from the Project window into the list above.", MessageType.Warning);
        else if (clipCount == 1)
            EditorGUILayout.HelpBox("1 clip. It loops the whole time. Add more clips to change the animation as progress fills.", MessageType.Info);
        else
        {
            string preview = "";
            for (int ci = 0; ci < clipCount; ci++)
                preview += "  Clip " + (ci + 1) + ": " + Mathf.RoundToInt((float)ci / clipCount * 100f) + "% - " + Mathf.RoundToInt((float)(ci + 1) / clipCount * 100f) + "%\n";
            EditorGUILayout.HelpBox(clipCount + " clips. Progress split:\n" + preview.TrimEnd(), MessageType.Info);
        }

        SerializedProperty helperMode = activity.FindPropertyRelative("progressHelperAnimationSelection");
        DrawEnumPopup(helperMode, ProgressHelperSelectionValues, ProgressHelperSelectionLabels,
            TipContent("How Should The Animation Play",
            "Change With Progress = recommended. Animation changes as the bar fills. Same clip never restarts on every tap."));

        ProgressGatePreviewAnimationSelectionMode sel = (ProgressGatePreviewAnimationSelectionMode)helperMode.enumValueIndex;
        bool isProgressMode = sel == ProgressGatePreviewAnimationSelectionMode.PlayAllAnimationsByProgress ||
                              sel == ProgressGatePreviewAnimationSelectionMode.PlaySelectedNumbersByProgress;

        if (sel == ProgressGatePreviewAnimationSelectionMode.UseSelectedAnimationNumber)
            EditorGUILayout.IntSlider(activity.FindPropertyRelative("progressHelperSelectedAnimationNumber"),
                1, Mathf.Max(1, clipCount),
                TipContent("Which Clip Number", "1 = first clip. 2 = second clip. And so on."));

        if (sel == ProgressGatePreviewAnimationSelectionMode.PlaySelectedAnimationNumbers ||
            sel == ProgressGatePreviewAnimationSelectionMode.PlaySelectedNumbersByProgress)
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("progressHelperSelectedAnimationNumbers"),
                TipContent("Clip Numbers To Use", "Type numbers separated by commas. Example: 1,3,5 uses clips 1, 3, and 5 only."));

        if (isProgressMode)
            EditorGUILayout.HelpBox(
                "Progress mode is active.\n" +
                "- Animation changes when progress enters a new range.\n" +
                "- Same clip will NOT restart on every tap.\n" +
                "- If progress drops, animation goes back to the matching range clip.\n" +
                "- Current clip loops until progress moves to the next range.",
                MessageType.Info);

        EditorGUILayout.PropertyField(activity.FindPropertyRelative("progressHelperAnimationSpeed"),
            TipContent("Animation Speed", "1 = normal. 0.5 = half speed. 2 = double speed."));

        EditorGUILayout.PropertyField(activity.FindPropertyRelative("progressHelperLoopAnimation"),
            TipContent("Loop Current Animation",
            "Keep this ON. The current animation loops until progress enters the next range."));

        if (!isProgressMode)
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("progressHelperPauseWhenNotTapping"),
                TipContent("Pause When Child Stops", "ON = animation pauses when there is no tapping."));

        EditorGUI.indentLevel--;
    }

    private void DrawAnimationClipsWithSounds(
        SerializedProperty activity,
        SerializedProperty clips,
        string groupLoopSoundProperty,
        string groupLoopVolumeProperty,
        string loopGroupSoundProperty,
        string clipSoundsProperty,
        string clipVolumesProperty,
        string clipsLabel,
        string groupSoundLabel)
    {
        if (clips == null)
        {
            EditorGUILayout.HelpBox("Animation clip list is missing. Click Refresh Activity Template once.", MessageType.Warning);
            if (GUILayout.Button(TipContent("Refresh Activity Template", "Creates missing optional fields without changing the existing setup.")))
                RefreshSelectedController(force: true);
            return;
        }

        SerializedProperty groupLoopSound = activity.FindPropertyRelative(groupLoopSoundProperty);
        SerializedProperty groupLoopVolume = activity.FindPropertyRelative(groupLoopVolumeProperty);
        SerializedProperty loopGroupSound = activity.FindPropertyRelative(loopGroupSoundProperty);
        SerializedProperty clipSounds = activity.FindPropertyRelative(clipSoundsProperty);
        SerializedProperty clipVolumes = activity.FindPropertyRelative(clipVolumesProperty);

        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField(groupSoundLabel, EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("Use this when one sound should loop while these animation clips are active. Leave empty if each clip has its own sound only.", MessageType.None);

        if (groupLoopSound != null)
            EditorGUILayout.PropertyField(groupLoopSound, TipContent("Loop Sound", "Optional. One sound that loops while this animation group is active."));
        if (groupLoopVolume != null)
            EditorGUILayout.Slider(groupLoopVolume, 0f, 2f, TipContent("Loop Sound Volume", "0 = silent. 1 = normal. 2 = boosted."));
        if (loopGroupSound != null)
            EditorGUILayout.PropertyField(loopGroupSound, TipContent("Loop Until Clips Finish", "ON = sound stops automatically when the animation group finishes."));

        if (clipSounds == null || clipVolumes == null)
        {
            EditorGUILayout.HelpBox("Clip sound fields are missing. Click Refresh Activity Template once.", MessageType.Warning);
            if (GUILayout.Button(TipContent("Fix Clip Sound Fields Now", "Creates the missing optional sound lists without changing your existing clips.")))
                RefreshSelectedController(force: true);
            return;
        }

        while (clipSounds.arraySize < clips.arraySize)
            clipSounds.arraySize++;

        while (clipVolumes.arraySize < clips.arraySize)
        {
            int index = clipVolumes.arraySize;
            clipVolumes.arraySize++;
            clipVolumes.GetArrayElementAtIndex(index).floatValue = 1f;
        }

        bool hasGroupSound = groupLoopSound != null && groupLoopSound.objectReferenceValue != null;
        bool hasClipSound = false;
        for (int i = 0; i < Mathf.Min(clips.arraySize, clipSounds.arraySize); i++)
        {
            if (clipSounds.GetArrayElementAtIndex(i).objectReferenceValue != null)
            {
                hasClipSound = true;
                break;
            }
        }
        if (hasGroupSound && hasClipSound)
            EditorGUILayout.HelpBox("Both group sound and per-clip sounds are assigned. This is allowed, but it may sound busy.", MessageType.Info);

        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField(clipsLabel, EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("Add each animation and its sound together. Sound is optional for every clip.", MessageType.None);

        for (int i = 0; i < clips.arraySize; i++)
        {
            if (clipSounds.arraySize <= i) clipSounds.arraySize = i + 1;
            if (clipVolumes.arraySize <= i)
            {
                clipVolumes.arraySize = i + 1;
                clipVolumes.GetArrayElementAtIndex(i).floatValue = 1f;
            }

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Clip " + (i + 1), EditorStyles.boldLabel);
            if (GUILayout.Button("Remove", GUILayout.Width(80)))
            {
                int oldClipSize = clips.arraySize;
                clips.DeleteArrayElementAtIndex(i);
                if (clips.arraySize == oldClipSize && i < clips.arraySize) clips.DeleteArrayElementAtIndex(i);
                if (i < clipSounds.arraySize)
                {
                    int oldSoundSize = clipSounds.arraySize;
                    clipSounds.DeleteArrayElementAtIndex(i);
                    if (clipSounds.arraySize == oldSoundSize && i < clipSounds.arraySize) clipSounds.DeleteArrayElementAtIndex(i);
                }
                if (i < clipVolumes.arraySize) clipVolumes.DeleteArrayElementAtIndex(i);
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.EndVertical();
                break;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.PropertyField(clips.GetArrayElementAtIndex(i), TipContent("Animation Clip"));
            EditorGUILayout.PropertyField(clipSounds.GetArrayElementAtIndex(i), TipContent("Sound For This Clip", "Optional. Plays when this animation clip starts."));
            EditorGUILayout.Slider(clipVolumes.GetArrayElementAtIndex(i), 0f, 2f, TipContent("Sound Volume", "0 = silent. 1 = normal. 2 = boosted."));
            EditorGUILayout.EndVertical();
        }

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button(TipContent("+ Add Clip", "Adds one animation clip slot with its own optional sound.")))
        {
            int index = clips.arraySize;
            clips.arraySize++;
            clipSounds.arraySize = Mathf.Max(clipSounds.arraySize, index + 1);
            clipVolumes.arraySize = Mathf.Max(clipVolumes.arraySize, index + 1);
            clips.GetArrayElementAtIndex(index).objectReferenceValue = null;
            clipSounds.GetArrayElementAtIndex(index).objectReferenceValue = null;
            clipVolumes.GetArrayElementAtIndex(index).floatValue = 1f;
        }
        if (clips.arraySize > 0 && GUILayout.Button(TipContent("- Remove Last", "Removes the last animation clip slot and its sound.")))
        {
            int last = clips.arraySize - 1;
            int oldClipSize = clips.arraySize;
            clips.DeleteArrayElementAtIndex(last);
            if (clips.arraySize == oldClipSize && last < clips.arraySize) clips.DeleteArrayElementAtIndex(last);
            if (clipSounds.arraySize > last)
            {
                int oldSoundSize = clipSounds.arraySize;
                clipSounds.DeleteArrayElementAtIndex(last);
                if (clipSounds.arraySize == oldSoundSize && last < clipSounds.arraySize) clipSounds.DeleteArrayElementAtIndex(last);
            }
            if (clipVolumes.arraySize > last) clipVolumes.DeleteArrayElementAtIndex(last);
        }
        EditorGUILayout.EndHorizontal();
    }

    // Shared across all activity types where the child taps repeatedly.
    // The animation follows progress automatically, no percentages needed.
    // ---------------------------------------------------------------
    private void DrawTappingAnimationSection(SerializedProperty activity)
    {
        SerializedProperty input = activity.FindPropertyRelative("childInput");
        ActivityInputKind kind = (ActivityInputKind)input.enumValueIndex;

        // WaitForStoryThenTapObject and ProgressGate draw this section
        // directly inside their own fields (in section 4) so the user
        // sees it right where they are setting up the activity.
        // Other tap types use this outer section 5.
        bool showForThisType =
            kind == ActivityInputKind.TapAnywhere ||
            kind == ActivityInputKind.TapObject ||
            kind == ActivityInputKind.TapManyTimes ||
            kind == ActivityInputKind.KeepTapping ||
            kind == ActivityInputKind.HelpAction;

        if (!showForThisType) return;

        DrawStepBox("5. Character Animation While Tapping Optional",
            "Should a character or object animate WHILE the child taps?\n" +
            "This plays DURING tapping, not after.\n" +
            "Add clips and the progress splits automatically. No percentages needed.");

        DrawAnimationWhileTappingFields(activity, "character");
    }

    private void DrawHelpAndTimeoutSection(SerializedProperty activity)
    {
        SerializedProperty input = activity.FindPropertyRelative("childInput");
        ActivityInputKind kind = (ActivityInputKind)input.enumValueIndex;

        DrawStepBox("6. Timing", "All timing controls stay here. First decide the total time. Then decide what happens if the child gives no input.");
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);

        if (kind == ActivityInputKind.WaitForStoryThenTapObject)
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("storyMomentTotalActivitySeconds"), TipContent("Total Activity Time", "Maximum time this activity can run."));
        else if (kind == ActivityInputKind.WaitOnly)
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("waitOnlySeconds"), TipContent("Total Activity Time", "How long to wait before the activity completes."));
        else
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("activeTimeSeconds"), TipContent("Total Activity Time", "Maximum time this activity can run."));


        SerializedProperty useHint = activity.FindPropertyRelative("enableNoInputHelp");
        EditorGUILayout.PropertyField(useHint, TipContent("Show Hint If No Input", "ON = if the child does nothing, show a simple hint."));

        if (useHint.boolValue)
        {
            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("noInputHintAfterSeconds"), TipContent("Show Hint After", "How many seconds to wait before showing the hint."));
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("noInputHintText"), TipContent("Hint Text"));
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("useSameHintEffectsForNoInput"), TipContent("Use Same Object Hint", "ON = use the same highlight or pulse used for wrong input."));

            SerializedProperty action = activity.FindPropertyRelative("noInputActionAfterHint");
            bool doSomethingAfterHint = action.enumValueIndex != (int)ActivityNoInputAction.ShowHintOnly;
            bool nextDoSomething = EditorGUILayout.Toggle(TipContent("After Hint If Still No Input", "Optional. ON = after the hint, wait again and choose what the template should do."), doSomethingAfterHint);
            if (nextDoSomething != doSomethingAfterHint)
                action.enumValueIndex = nextDoSomething ? (int)ActivityNoInputAction.AutoPlayResultThenContinue : (int)ActivityNoInputAction.ShowHintOnly;

            if (nextDoSomething)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(activity.FindPropertyRelative("autoSkipAfterHintSeconds"), TipContent("Wait After Hint", "This timer starts only after the hint appears."));
                DrawEnumPopup(action, NoInputActionValues, NoInputActionLabels, TipContent("What Should Happen", "Recommended: Auto Play Activity Result Then Continue. This skips only waiting for the child, not the activity result."));
                EditorGUILayout.HelpBox("Recommended default: Auto Play Activity Result Then Continue. The child still sees the story-related activity result, then the story continues.", MessageType.None);
                EditorGUI.indentLevel--;
            }
            EditorGUI.indentLevel--;
        }
        else
        {
            EditorGUILayout.HelpBox("No hint or no-input action will run. Use this only when the activity must wait for the child.", MessageType.None);
        }

        EditorGUILayout.EndVertical();
    }

    private void DrawProgressBarSection(SerializedProperty activity)
    {
        DrawStepBox("7. Progress Bar Optional", "Use this only when the child should see a bar. Off means no bar appears, but the activity still works.");

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        SerializedProperty useProgressBar = activity.FindPropertyRelative("useProgressBar");
        EditorGUILayout.PropertyField(useProgressBar, TipContent("Show Progress Bar", "OFF = no bar. ON = show a bar to the child."));

        if (!useProgressBar.boolValue)
        {
            EditorGUILayout.HelpBox("Progress bar is Off. The activity can still complete by taps, objects, time, choices, or result animation.", MessageType.None);
            EditorGUILayout.EndVertical();
            return;
        }

        EditorGUI.indentLevel++;

        SerializedProperty behavior = activity.FindPropertyRelative("progressBarBehavior");
        if (behavior != null)
        {
            DrawEnumPopup(behavior, ProgressBehaviorValues, ProgressBehaviorLabels, TipContent("How Should The Bar Move", "Choose what should happen to the bar when the child taps or stops."));

            ActivityProgressBarBehavior selected = (ActivityProgressBarBehavior)behavior.enumValueIndex;
            if (selected == ActivityProgressBarBehavior.OnlyFillUp)
            {
                activity.FindPropertyRelative("progressBarFillMode").enumValueIndex = (int)ActivityProgressBarFillMode.FollowInputProgress;
                EditorGUILayout.HelpBox("Example: tap 1 = 10%, tap 2 = 20%. If the child stops, the bar stays where it is.", MessageType.None);
            }
            else if (selected == ActivityProgressBarBehavior.GoDownIfChildStops)
            {
                activity.FindPropertyRelative("progressBarFillMode").enumValueIndex = (int)ActivityProgressBarFillMode.FollowInputProgress;
                EditorGUILayout.HelpBox("Example: tap 3 times = 30%. If the child stops, the bar slowly goes down.", MessageType.None);
                EditorGUILayout.PropertyField(activity.FindPropertyRelative("progressGoDownPercentPerSecond"), TipContent("How Fast It Goes Down", "Percent per second. Example: 10 means the bar drops 10% every second."));
                SerializedProperty min = activity.FindPropertyRelative("progressMinimumPercent");
                EditorGUILayout.PropertyField(min, TipContent("Do Not Go Below Optional", "0 = normal. 50 = the bar never goes below halfway."));
            }
            else if (selected == ActivityProgressBarBehavior.FillWithTime)
            {
                activity.FindPropertyRelative("progressBarFillMode").enumValueIndex = (int)ActivityProgressBarFillMode.FollowActivityTime;
                EditorGUILayout.HelpBox("The bar fills using the activity time. Example: 10 seconds total means 5 seconds = 50%.", MessageType.None);
            }
            else if (selected == ActivityProgressBarBehavior.FillDuringResult)
            {
                activity.FindPropertyRelative("progressBarFillMode").enumValueIndex = (int)ActivityProgressBarFillMode.FillWhenResultPlays;
                EditorGUILayout.HelpBox("The bar fills when the result animation or effect plays.", MessageType.None);
            }
            else
            {
                DrawEnumPopup(activity.FindPropertyRelative("progressBarFillMode"), ProgressBarFillValues, ProgressBarFillLabels, TipContent("Advanced Fill Source"));
                EditorGUILayout.PropertyField(activity.FindPropertyRelative("progressGoDownPercentPerSecond"), TipContent("Go Down Speed Optional"));
                EditorGUILayout.PropertyField(activity.FindPropertyRelative("progressMinimumPercent"), TipContent("Minimum Percent Optional"));
            }
        }

        SerializedProperty resultTiming = activity.FindPropertyRelative("resultPlayTiming");
        if (resultTiming != null)
        {
            EditorGUILayout.Space(4);
            DrawEnumPopup(resultTiming, ResultPlayTimingValues, ResultPlayTimingLabels, TipContent("When Should Animation Or Effect Play", "Choose whether the result plays every tap or only after the needed input is done."));
            EditorGUILayout.HelpBox("Use 'Every Correct Input' when animation should happen on each tap. Use 'After Required Inputs' when animation should play only after the needed count is finished.", MessageType.None);
        }

        EditorGUI.indentLevel--;
        EditorGUILayout.EndVertical();
    }

    private void DrawWrongFeedbackSection(SerializedProperty activity)
    {
        SerializedProperty input = activity.FindPropertyRelative("childInput");
        ActivityInputKind kind = (ActivityInputKind)input.enumValueIndex;
        if (kind == ActivityInputKind.WaitOnly)
            return;

        DrawStepBox("8. What Happens If Wrong?", "Wrong input means the child taps the wrong object, wrong place, wrong option, or wrong UI. Wrong input must not complete the activity.");

        bool objectBased = kind == ActivityInputKind.TapObject || kind == ActivityInputKind.HelpAction || kind == ActivityInputKind.ProgressGate || kind == ActivityInputKind.GroupAction || kind == ActivityInputKind.TapObjectsInOrder || kind == ActivityInputKind.WaitForStoryThenTapObject || (kind == ActivityInputKind.TapManyTimes && activity.FindPropertyRelative("targetObject").objectReferenceValue != null);

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        SerializedProperty useWrong = activity.FindPropertyRelative("showHintWhenWrongInput");
        EditorGUILayout.PropertyField(useWrong, TipContent("Use Wrong Feedback", "ON = guide the child if they tap the wrong object or wrong place."));
        if (useWrong.boolValue)
        {
            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("wrongInputHintText"), TipContent("Wrong Message Optional"));
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("wrongInputSound"), TipContent("Wrong Sound Optional"));
            if (activity.FindPropertyRelative("wrongInputSound").objectReferenceValue != null)
                EditorGUILayout.PropertyField(activity.FindPropertyRelative("wrongInputSoundVolume"), TipContent("Wrong Sound Volume"));

            if (objectBased)
            {
                SerializedProperty showObjectHint = activity.FindPropertyRelative("pulseTargetObject");
                EditorGUILayout.PropertyField(showObjectHint, TipContent("Show Required Object Hint", "ON = pulse or highlight the object the child should tap."));
                if (showObjectHint.boolValue)
                {
                    EditorGUI.indentLevel++;
                    EditorGUILayout.PropertyField(activity.FindPropertyRelative("targetPulseScale"), TipContent("Pulse Size"));
                    EditorGUILayout.PropertyField(activity.FindPropertyRelative("targetPulseRepeatCount"), TipContent("Pulse Count"));
                    EditorGUILayout.PropertyField(activity.FindPropertyRelative("targetPulseSeconds"), TipContent("Pulse Time"));
                    EditorGUI.indentLevel--;
                }

                SerializedProperty glow = activity.FindPropertyRelative("tintTargetObject");
                EditorGUILayout.PropertyField(glow, TipContent("Glow Highlight Optional"));
                if (glow.boolValue)
                {
                    EditorGUI.indentLevel++;
                    EditorGUILayout.PropertyField(activity.FindPropertyRelative("targetTintColor"), TipContent("Glow Color"));
                    EditorGUILayout.PropertyField(activity.FindPropertyRelative("targetTintRepeatCount"), TipContent("Glow Count"));
                    EditorGUILayout.PropertyField(activity.FindPropertyRelative("targetTintSeconds"), TipContent("Glow Time"));
                    EditorGUI.indentLevel--;
                }

                SerializedProperty customHint = activity.FindPropertyRelative("showHintObject");
                EditorGUILayout.PropertyField(customHint, TipContent("Custom Hint Object Optional"));
                if (customHint.boolValue)
                {
                    EditorGUI.indentLevel++;
                    EditorGUILayout.PropertyField(activity.FindPropertyRelative("hintObject"), TipContent("Hint Object"));
                    EditorGUILayout.PropertyField(activity.FindPropertyRelative("hintObjectSeconds"), TipContent("Hint Visible Time"));
                    EditorGUI.indentLevel--;
                }
            }
            EditorGUI.indentLevel--;
        }
        EditorGUILayout.EndVertical();
    }

    private void DrawReactionSection(SerializedProperty activity)
    {
        SerializedProperty reactions = activity.FindPropertyRelative("reactions");
        bool hasReactions = reactions != null && reactions.arraySize > 0;

        EditorGUILayout.Space(6);
        reactions.isExpanded = EditorGUILayout.Foldout(reactions.isExpanded, hasReactions ? "9. Result Actions" : "9. Result Actions Hidden", true, EditorStyles.foldoutHeader);
        if (!reactions.isExpanded)
        {
            if (hasReactions)
                EditorGUILayout.HelpBox("Result actions are added but hidden. Expand this only if you need extra particles, animation, sound, object on/off, color, movement, or custom UnityEvents.", MessageType.None);
            return;
        }

        DrawStepBox("9. Result Actions", "Main actions that play during or after the activity: animation, sound, voice, VFX, object on/off, move, color, or custom event.");

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        EditorGUILayout.LabelField("Add Extra Reaction", EditorStyles.boldLabel);

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button(TipContent("+ Visual Effect", "Add particles or 3D effects such as flowers, petals, coins, leaves, or sparkles."), GUILayout.Height(26)))
            AddReaction(reactions, ActivityReactionType.VisualEffect, "Visual Effect", ActivityReactionMoment.EveryValidInput);
        if (GUILayout.Button(TipContent("+ Animation", "Add an extra animation reaction. Use this only if the selected activity type does not already have its own animation field."), GUILayout.Height(26)))
            AddReaction(reactions, ActivityReactionType.AnimationClip, "Animation", ActivityReactionMoment.IfReactionIsFree);
        if (GUILayout.Button(TipContent("+ Sound", "Add an extra sound-only reaction."), GUILayout.Height(26)))
            AddReaction(reactions, ActivityReactionType.SoundEffect, "Sound", ActivityReactionMoment.EveryValidInput);
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button(TipContent("+ Voice", "Add an extra voice-over reaction."), GUILayout.Height(24)))
            AddReaction(reactions, ActivityReactionType.VoiceOver, "Voice", ActivityReactionMoment.EveryValidInput);
        if (GUILayout.Button(TipContent("+ Object On", "Turn assigned objects on."), GUILayout.Height(24)))
            AddReaction(reactions, ActivityReactionType.EnableObjects, "Turn Object On", ActivityReactionMoment.EveryValidInput);
        if (GUILayout.Button(TipContent("+ Object Off", "Turn assigned objects off."), GUILayout.Height(24)))
            AddReaction(reactions, ActivityReactionType.DisableObjects, "Turn Object Off", ActivityReactionMoment.EveryValidInput);
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button(TipContent("+ Color", "Temporarily change material color. Advanced."), GUILayout.Height(24)))
            AddReaction(reactions, ActivityReactionType.MaterialColor, "Change Color", ActivityReactionMoment.EveryValidInput);
        if (GUILayout.Button(TipContent("+ Move", "Move an object by offset or toward a target transform. Advanced."), GUILayout.Height(24)))
            AddReaction(reactions, ActivityReactionType.MoveObject, "Move Object", ActivityReactionMoment.EveryValidInput);
        if (GUILayout.Button(TipContent("+ Custom", "Advanced. Call a UnityEvent when built-in reactions are not enough."), GUILayout.Height(24)))
            AddReaction(reactions, ActivityReactionType.CustomAction, "Custom Action", ActivityReactionMoment.EveryValidInput);
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.EndVertical();

        for (int i = 0; i < reactions.arraySize; i++)
            DrawReaction(reactions, reactions.GetArrayElementAtIndex(i), i);
    }

    private void DrawReaction(SerializedProperty reactions, SerializedProperty reaction, int index)
    {
        SerializedProperty reactionName = reaction.FindPropertyRelative("reactionName");
        SerializedProperty typeProp = reaction.FindPropertyRelative("type");
        SerializedProperty playWhen = reaction.FindPropertyRelative("playWhen");

        string title = string.IsNullOrWhiteSpace(reactionName.stringValue) ? "Reaction " + (index + 1) : reactionName.stringValue;

        EditorGUILayout.Space(6);
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);

        EditorGUILayout.BeginHorizontal();
        reaction.isExpanded = EditorGUILayout.Foldout(reaction.isExpanded, "Reaction " + (index + 1) + ": " + title, true, EditorStyles.foldoutHeader);
        if (GUILayout.Button(TipContent("Remove", "Remove this activity or reaction from the page."), GUILayout.Width(80)))
        {
            reactions.DeleteArrayElementAtIndex(index);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
            return;
        }
        EditorGUILayout.EndHorizontal();

        if (!reaction.isExpanded)
        {
            EditorGUILayout.LabelField("Type", NiceReactionType((ActivityReactionType)typeProp.enumValueIndex));
            EditorGUILayout.EndVertical();
            return;
        }

        EditorGUILayout.PropertyField(reaction.FindPropertyRelative("enabled"), TipContent("Use This Reaction"));
        EditorGUILayout.PropertyField(reactionName, TipContent("Reaction Name"));
        DrawEnumPopup(typeProp, ReactionTypeValues, ReactionTypeLabels, TipContent("What Should Happen"));
        DrawEnumPopup(playWhen, MomentValues, MomentLabels, TipContent("When Should This Run"));

        ActivityReactionType type = (ActivityReactionType)typeProp.enumValueIndex;

        switch (type)
        {
            case ActivityReactionType.VisualEffect:
                DrawVisualEffectReaction(reaction);
                break;
            case ActivityReactionType.AnimationClip:
                DrawAnimationReaction(reaction);
                break;
            case ActivityReactionType.SoundEffect:
                DrawSoundReaction(reaction);
                break;
            case ActivityReactionType.VoiceOver:
                DrawVoiceReaction(reaction);
                break;
            case ActivityReactionType.EnableObjects:
            case ActivityReactionType.DisableObjects:
                DrawObjectToggleReaction(reaction, type);
                break;
            case ActivityReactionType.MaterialColor:
                DrawColorReaction(reaction);
                break;
            case ActivityReactionType.MoveObject:
                DrawMoveReaction(reaction);
                break;
            case ActivityReactionType.CustomAction:
                DrawCustomReaction(reaction);
                break;
        }

        DrawReactionAdvanced(reaction);

        EditorGUILayout.EndVertical();
    }

    private void DrawVisualEffectReaction(SerializedProperty reaction)
    {
        DrawMiniHeader("Visual Effect", "Use this for flowers, petals, leaves, coins, sparkles, particles, or small 3D objects.");
        EditorGUILayout.PropertyField(reaction.FindPropertyRelative("vfxObjects"), TipContent("Original Petal Or Effect", "Drag the petal model here. It will be used only as a template. The visible falling petals will be new copies."), true);

        SerializedProperty hideSource = reaction.FindPropertyRelative("hideSourceObjectsUntilPlayed");
        EditorGUILayout.PropertyField(hideSource, TipContent("Hide Original Before Tap", "ON = the original petal is invisible. Only new falling copies appear when the child taps."));
        DrawSourceVisibilityButtons(reaction);
        DrawVisualEffectEditorTestButtons(reaction);

        DrawEnumPopup(reaction.FindPropertyRelative("visualEffectPlayMode"), VisualEffectPlayModeValues, VisualEffectPlayModeLabels, TipContent("If Child Taps Again"));
        EditorGUILayout.PropertyField(reaction.FindPropertyRelative("vfxSpawnOrigin"), TipContent("Start From Optional", "Empty = use the petal/source position. Assign a transform if copies should start from a specific place."));
        DrawEnumPopup(reaction.FindPropertyRelative("spawnAreaMode"), VfxSpawnAreaValues, VfxSpawnAreaLabels, TipContent("Where Should Petals Start", "For king welcome, choose Inside Rectangle Area. Petals start from random points inside that box, not from one fixed point."));
        ActivityVfxSpawnAreaMode spawnMode = (ActivityVfxSpawnAreaMode)reaction.FindPropertyRelative("spawnAreaMode").enumValueIndex;
        if (spawnMode == ActivityVfxSpawnAreaMode.SpreadAcrossPage)
            EditorGUILayout.PropertyField(reaction.FindPropertyRelative("pageSpreadSize"), TipContent("Cover Page Area", "X = left/right spread. Y = up/down spread. Increase this to cover more of the page."));
        else if (spawnMode == ActivityVfxSpawnAreaMode.InsideRectangleArea)
        {
            EditorGUILayout.PropertyField(reaction.FindPropertyRelative("rectangleSpawnArea"), TipContent("Petal Fall Area Box", "Move this box above the king. Petals start from random points inside this area."));
            SerializedProperty areaSize = reaction.FindPropertyRelative("rectangleSpawnAreaSize");
            Vector3 petalArea = areaSize.vector3Value;
            petalArea.x = EditorGUILayout.FloatField(TipContent("Petal Area Width", "Left to right area covered by petals. For a 20 cm book, start around 0.18 to 0.25 if your scene units are meters, or 1.8 if your project scale is larger."), petalArea.x);
            petalArea.y = EditorGUILayout.FloatField(TipContent("Petal Area Height", "Vertical start height range. Increase this so petals start from different heights instead of one line."), petalArea.y);
            petalArea.z = EditorGUILayout.FloatField(TipContent("Petal Area Depth", "Front to back area covered by petals."), petalArea.z);
            areaSize.vector3Value = petalArea;
            EditorGUILayout.HelpBox("Width + Depth = area covered. Height = vertical range where petals start. This only controls where new copies spawn, not the original petal source.", MessageType.None);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(TipContent("Create Petal Fall Area", "Creates a box above the king. Scale it like the flower shower area. Bigger box = wider petal fall.")))
                CreatePetalFallArea(reaction);
            if (GUILayout.Button(TipContent("Select Area", "Selects the assigned petal fall area in the Hierarchy.")))
                SelectPetalFallArea(reaction);
            EditorGUILayout.EndHorizontal();
        }
        EditorGUILayout.PropertyField(reaction.FindPropertyRelative("objectBurstCount"), TipContent("Petals Per Tap", "Each tap creates this many NEW petals. Old falling petals are not touched again."));
        EditorGUILayout.PropertyField(reaction.FindPropertyRelative("objectLifeSeconds"), TipContent("Petal Visible Time", "How long each new petal copy stays alive before it disappears."));
        EditorGUILayout.PropertyField(reaction.FindPropertyRelative("fadeOutSpawnedObjects"), TipContent("Fade Out Before Hiding"));
        if (reaction.FindPropertyRelative("fadeOutSpawnedObjects").boolValue)
            EditorGUILayout.PropertyField(reaction.FindPropertyRelative("fadeOutSeconds"), TipContent("Fade Time"));

        SerializedProperty fall = reaction.FindPropertyRelative("make3DObjectsFall");
        EditorGUILayout.PropertyField(fall, TipContent("Make Petals Fall", "ON = new petal copies fall or flutter down, then disappear."));
        if (fall != null && fall.boolValue)
        {
            EditorGUI.indentLevel++;
            DrawEnumPopup(reaction.FindPropertyRelative("fallingMotion"), FallingObjectMotionValues, FallingObjectMotionLabels, TipContent("Fall Style"));
            EditorGUILayout.PropertyField(reaction.FindPropertyRelative("fallDistance"), TipContent("How Far Down"));
            EditorGUILayout.PropertyField(reaction.FindPropertyRelative("fallDurationSeconds"), TipContent("Base Fall Time"));
            EditorGUILayout.PropertyField(reaction.FindPropertyRelative("randomStartDelayMaxSeconds"), TipContent("Random Start Gap", "Petals do not all start together. Example: 0.35 means each petal can start within the next 0.35 seconds."));
            EditorGUILayout.PropertyField(reaction.FindPropertyRelative("randomFallTimeExtraSeconds"), TipContent("Random Fall Speed", "Some petals fall faster and some slower. Higher number = more natural."));
            EditorGUILayout.PropertyField(reaction.FindPropertyRelative("fallSpreadSideways"), TipContent("Side Movement"));
            EditorGUILayout.PropertyField(reaction.FindPropertyRelative("fallFlutterAmount"), TipContent("Flutter Movement"));
            EditorGUILayout.PropertyField(reaction.FindPropertyRelative("fallSpinDegrees"), TipContent("Spin Amount"));
            EditorGUILayout.PropertyField(reaction.FindPropertyRelative("randomScaleMin"), TipContent("Smallest Petal Size"));
            EditorGUILayout.PropertyField(reaction.FindPropertyRelative("randomScaleMax"), TipContent("Biggest Petal Size"));
            EditorGUI.indentLevel--;
        }

        DrawCommonReactionAudio(reaction);
        DrawActivityTransformFields(reaction);
        EditorGUILayout.HelpBox("For a natural flower shower: keep Hide Original ON, choose Inside Rectangle Area, set Width/Height/Depth, use 15 to 30 petals, Flutter Like Petals, and add a small Random Start Gap.", MessageType.None);
    }

    private void DrawSourceVisibilityButtons(SerializedProperty reaction)
    {
        SerializedProperty list = reaction.FindPropertyRelative("vfxObjects");
        if (list == null || list.arraySize == 0) return;

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button(TipContent("Hide Source Now", "Hide assigned scene source objects now. Use this before testing so the original petal is not visible.")))
            SetReactionSceneSourcesVisible(reaction, false);
        if (GUILayout.Button(TipContent("Show Source For Editing", "Temporarily show assigned scene source objects so you can move or scale them.")))
            SetReactionSceneSourcesVisible(reaction, true);
        EditorGUILayout.EndHorizontal();
    }

    private void SetReactionSceneSourcesVisible(SerializedProperty reaction, bool visible)
    {
        SerializedProperty list = reaction.FindPropertyRelative("vfxObjects");
        if (list == null) return;

        for (int i = 0; i < list.arraySize; i++)
        {
            GameObject source = list.GetArrayElementAtIndex(i).objectReferenceValue as GameObject;
            if (source == null || !source.scene.IsValid()) continue;
            Undo.RecordObject(source, visible ? "Show Activity Effect Source" : "Hide Activity Effect Source");
            source.SetActive(visible);
            EditorUtility.SetDirty(source);
        }
        UnityEditor.SceneManagement.EditorSceneManager.MarkAllScenesDirty();
    }

    private void DrawVisualEffectEditorTestButtons(SerializedProperty reaction)
    {
        SerializedProperty list = reaction.FindPropertyRelative("vfxObjects");
        if (list == null || list.arraySize == 0) return;

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button(TipContent("Test Petal Shower In Scene", "Preview new falling petal copies in Edit Mode. This does not use Play Mode.")))
            TestVisualEffectInEditor(reaction);
        if (GUILayout.Button(TipContent("Clear Petal Preview", "Removes only the Edit Mode test petals.")))
            ClearVisualEffectEditorPreview();
        EditorGUILayout.EndHorizontal();
    }

    private void TestVisualEffectInEditor(SerializedProperty reaction)
    {
        serializedObject.ApplyModifiedProperties();
        ClearVisualEffectEditorPreview();

        if (reaction.FindPropertyRelative("hideSourceObjectsUntilPlayed").boolValue)
            SetReactionSceneSourcesVisible(reaction, false);

        SerializedProperty list = reaction.FindPropertyRelative("vfxObjects");
        if (list == null || list.arraySize == 0) return;

        Transform origin = reaction.FindPropertyRelative("vfxSpawnOrigin").objectReferenceValue as Transform;
        ContentController controller = target as ContentController;
        Transform fallback = controller != null ? controller.transform : null;

        int count = Mathf.Max(1, reaction.FindPropertyRelative("objectBurstCount").intValue);
        float life = Mathf.Max(0.1f, reaction.FindPropertyRelative("objectLifeSeconds").floatValue);
        float duration = Mathf.Max(0.1f, reaction.FindPropertyRelative("fallDurationSeconds").floatValue);
        float randomDelayMax = Mathf.Max(0f, reaction.FindPropertyRelative("randomStartDelayMaxSeconds").floatValue);
        float randomFallExtra = Mathf.Max(0f, reaction.FindPropertyRelative("randomFallTimeExtraSeconds").floatValue);
        float distance = Mathf.Max(0f, reaction.FindPropertyRelative("fallDistance").floatValue);
        float side = Mathf.Max(0f, reaction.FindPropertyRelative("fallSpreadSideways").floatValue);
        float flutter = Mathf.Max(0f, reaction.FindPropertyRelative("fallFlutterAmount").floatValue);
        float spin = reaction.FindPropertyRelative("fallSpinDegrees").floatValue;
        Vector2 pageSpread = reaction.FindPropertyRelative("pageSpreadSize").vector2Value;
        bool randomRotation = reaction.FindPropertyRelative("randomizeObjectRotation").boolValue;
        bool keepWorld = reaction.FindPropertyRelative("keepSpawnedObjectsInWorldSpace").boolValue;
        ActivityVfxSpawnAreaMode editorSpawnMode = (ActivityVfxSpawnAreaMode)reaction.FindPropertyRelative("spawnAreaMode").enumValueIndex;
        bool spreadAcrossPage = editorSpawnMode == ActivityVfxSpawnAreaMode.SpreadAcrossPage;
        bool insideRectangleArea = editorSpawnMode == ActivityVfxSpawnAreaMode.InsideRectangleArea;
        bool makeFall = reaction.FindPropertyRelative("make3DObjectsFall").boolValue;
        FallingObjectMotion motion = (FallingObjectMotion)reaction.FindPropertyRelative("fallingMotion").enumValueIndex;
        float scaleMin = Mathf.Max(0.01f, Mathf.Min(reaction.FindPropertyRelative("randomScaleMin").floatValue, reaction.FindPropertyRelative("randomScaleMax").floatValue));
        float scaleMax = Mathf.Max(scaleMin, Mathf.Max(reaction.FindPropertyRelative("randomScaleMin").floatValue, reaction.FindPropertyRelative("randomScaleMax").floatValue));

        for (int s = 0; s < list.arraySize; s++)
        {
            GameObject source = list.GetArrayElementAtIndex(s).objectReferenceValue as GameObject;
            if (source == null) continue;

            bool sourceIsSceneObject = source.scene.IsValid();
            Transform sourceTransform = source.transform;
            Transform rectangleArea = reaction.FindPropertyRelative("rectangleSpawnArea").objectReferenceValue as Transform;
            Vector3 rectangleSize = reaction.FindPropertyRelative("rectangleSpawnAreaSize").vector3Value;
            Transform usedOrigin = rectangleArea != null ? rectangleArea : (origin != null ? origin : (sourceIsSceneObject ? sourceTransform : fallback));
            Vector3 basePosition = rectangleArea != null ? rectangleArea.position : (origin != null ? origin.position : (sourceIsSceneObject ? sourceTransform.position : (fallback != null ? fallback.position : Vector3.zero)));
            Quaternion baseRotation = rectangleArea != null ? rectangleArea.rotation : (origin != null ? origin.rotation : (sourceIsSceneObject ? sourceTransform.rotation : Quaternion.identity));
            Vector3 baseScale = sourceTransform.localScale;
            // Editor preview uses the same rule as runtime: falling petals are one-shot world-space copies.
            Transform parent = makeFall ? null : (keepWorld ? null : (sourceIsSceneObject ? sourceTransform.parent : null));
            Vector3 spreadRight = usedOrigin != null ? usedOrigin.right : Vector3.right;
            Vector3 spreadUp = usedOrigin != null ? usedOrigin.up : Vector3.up;
            Vector3 spreadForward = usedOrigin != null ? usedOrigin.forward : Vector3.forward;

            for (int i = 0; i < count; i++)
            {
                Vector3 offset;
                if (insideRectangleArea)
                {
                    Vector3 size = rectangleSize;
                    float x = UnityEngine.Random.Range(-Mathf.Abs(size.x) * 0.5f, Mathf.Abs(size.x) * 0.5f);
                    float y = UnityEngine.Random.Range(-Mathf.Abs(size.y) * 0.5f, Mathf.Abs(size.y) * 0.5f);
                    float z = UnityEngine.Random.Range(-Mathf.Abs(size.z) * 0.5f, Mathf.Abs(size.z) * 0.5f);
                    offset = spreadRight * x + spreadUp * y + spreadForward * z;
                }
                else if (spreadAcrossPage)
                {
                    float x = UnityEngine.Random.Range(-pageSpread.x * 0.5f, pageSpread.x * 0.5f);
                    float z = UnityEngine.Random.Range(-pageSpread.y * 0.5f, pageSpread.y * 0.5f);
                    float y = UnityEngine.Random.Range(0f, Mathf.Max(0.02f, flutter * 2f));
                    offset = spreadRight * x + spreadUp * y + spreadForward * z;
                }
                else
                {
                    offset = UnityEngine.Random.insideUnitSphere * Mathf.Max(0f, reaction.FindPropertyRelative("objectSpreadRadius").floatValue);
                    offset.y = Mathf.Abs(offset.y);
                }

                GameObject clone = null;
                if (!sourceIsSceneObject)
                    clone = PrefabUtility.InstantiatePrefab(source) as GameObject;
                if (clone == null)
                    clone = Instantiate(source);

                clone.name = "EDITOR_TEST_" + source.name;
                Undo.RegisterCreatedObjectUndo(clone, "Test Activity Falling Effect");
                if (parent != null) clone.transform.SetParent(parent, true);
                clone.transform.position = basePosition + offset;
                clone.transform.rotation = randomRotation ? UnityEngine.Random.rotation : baseRotation;
                clone.transform.localScale = baseScale * UnityEngine.Random.Range(scaleMin, scaleMax);
                float delay = makeFall ? UnityEngine.Random.Range(0f, randomDelayMax) : 0f;
                float itemDuration = makeFall ? Mathf.Max(0.1f, duration + UnityEngine.Random.Range(0f, randomFallExtra)) : life;
                clone.SetActive(delay <= 0.001f);

                if (!makeFall)
                {
                    visualEffectEditorPreviewItems.Add(new VisualEffectEditorPreviewItem
                    {
                        go = clone,
                        startPosition = clone.transform.position,
                        startRotation = clone.transform.rotation,
                        startedAt = (float)EditorApplication.timeSinceStartup + delay,
                        hasStarted = delay <= 0.001f,
                        duration = life,
                        distance = 0f,
                        sideMovement = 0f,
                        flutter = 0f,
                        spin = 0f,
                        motion = motion,
                        seed = UnityEngine.Random.Range(0f, 1000f),
                        sideDirection = Vector3.right
                    });
                    continue;
                }

                Vector3 sideDir = UnityEngine.Random.insideUnitSphere;
                sideDir.y = 0f;
                if (sideDir.sqrMagnitude < 0.001f) sideDir = Vector3.right;
                sideDir.Normalize();

                visualEffectEditorPreviewItems.Add(new VisualEffectEditorPreviewItem
                {
                    go = clone,
                    startPosition = clone.transform.position,
                    startRotation = clone.transform.rotation,
                    startedAt = (float)EditorApplication.timeSinceStartup + delay,
                    hasStarted = delay <= 0.001f,
                    duration = itemDuration,
                    distance = distance,
                    sideMovement = side,
                    flutter = flutter,
                    spin = spin,
                    motion = motion,
                    seed = UnityEngine.Random.Range(0f, 1000f),
                    sideDirection = sideDir
                });
            }
        }

        RegisterVisualEffectEditorPreviewUpdate();
        SceneView.RepaintAll();
    }


    private void CreatePetalFallArea(SerializedProperty reaction)
    {
        serializedObject.ApplyModifiedProperties();

        SerializedProperty list = reaction.FindPropertyRelative("vfxObjects");
        GameObject firstSource = null;
        if (list != null && list.arraySize > 0)
            firstSource = list.GetArrayElementAtIndex(0).objectReferenceValue as GameObject;

        ContentController controller = target as ContentController;
        Vector3 position = firstSource != null && firstSource.scene.IsValid()
            ? firstSource.transform.position + Vector3.up * 0.35f
            : (controller != null ? controller.transform.position + Vector3.up * 0.35f : Vector3.up * 0.35f);

        GameObject area = new GameObject("Petal_Fall_Area");
        Undo.RegisterCreatedObjectUndo(area, "Create Petal Fall Area");
        area.transform.position = position;
        area.transform.rotation = firstSource != null && firstSource.scene.IsValid() ? firstSource.transform.rotation : Quaternion.identity;
        area.transform.localScale = new Vector3(1.8f, 0.05f, 1.2f);
        BoxCollider box = area.AddComponent<BoxCollider>();
        box.isTrigger = true;
        box.size = Vector3.one;

        reaction.FindPropertyRelative("rectangleSpawnArea").objectReferenceValue = area.transform;
        reaction.FindPropertyRelative("spawnAreaMode").enumValueIndex = (int)ActivityVfxSpawnAreaMode.InsideRectangleArea;
        serializedObject.ApplyModifiedProperties();
        Selection.activeGameObject = area;
        UnityEditor.SceneManagement.EditorSceneManager.MarkAllScenesDirty();
    }

    private void SelectPetalFallArea(SerializedProperty reaction)
    {
        Transform area = reaction.FindPropertyRelative("rectangleSpawnArea").objectReferenceValue as Transform;
        if (area != null)
            Selection.activeGameObject = area.gameObject;
    }

    private static void RegisterVisualEffectEditorPreviewUpdate()
    {
        if (visualEffectEditorPreviewUpdateRegistered) return;
        EditorApplication.update += UpdateVisualEffectEditorPreview;
        visualEffectEditorPreviewUpdateRegistered = true;
    }

    private static void UpdateVisualEffectEditorPreview()
    {
        float now = (float)EditorApplication.timeSinceStartup;

        for (int i = visualEffectEditorPreviewItems.Count - 1; i >= 0; i--)
        {
            VisualEffectEditorPreviewItem item = visualEffectEditorPreviewItems[i];
            if (item == null || item.go == null)
            {
                visualEffectEditorPreviewItems.RemoveAt(i);
                continue;
            }

            if (now < item.startedAt)
                continue;

            if (!item.hasStarted)
            {
                item.go.SetActive(true);
                item.hasStarted = true;
            }

            float n = Mathf.Clamp01((now - item.startedAt) / Mathf.Max(0.1f, item.duration));
            float ease = Mathf.SmoothStep(0f, 1f, n);
            Vector3 pos = item.startPosition + Vector3.down * item.distance * ease;

            switch (item.motion)
            {
                case FallingObjectMotion.GentleFall:
                    pos += item.sideDirection * item.sideMovement * ease;
                    break;
                case FallingObjectMotion.SwirlFall:
                    pos += new Vector3(Mathf.Sin((n * 8f) + item.seed), 0f, Mathf.Cos((n * 8f) + item.seed)) * item.sideMovement * n;
                    break;
                case FallingObjectMotion.BounceFall:
                    pos += item.sideDirection * item.sideMovement * ease;
                    pos += Vector3.up * Mathf.Sin(n * Mathf.PI * 3f) * item.flutter * (1f - n);
                    break;
                case FallingObjectMotion.FlutterFall:
                default:
                    pos += item.sideDirection * item.sideMovement * Mathf.Sin(n * Mathf.PI * 1.2f);
                    pos += new Vector3(Mathf.Sin((n * 14f) + item.seed), 0f, Mathf.Cos((n * 9f) + item.seed)) * item.flutter;
                    break;
            }

            item.go.transform.position = pos;
            item.go.transform.rotation = item.startRotation * Quaternion.Euler(item.spin * n, item.spin * 0.35f * n, item.spin * 0.6f * n);

            if (n >= 1f)
            {
                Undo.DestroyObjectImmediate(item.go);
                visualEffectEditorPreviewItems.RemoveAt(i);
            }
        }

        if (visualEffectEditorPreviewItems.Count == 0)
        {
            EditorApplication.update -= UpdateVisualEffectEditorPreview;
            visualEffectEditorPreviewUpdateRegistered = false;
        }

        SceneView.RepaintAll();
    }

    private static void ClearVisualEffectEditorPreview()
    {
        for (int i = visualEffectEditorPreviewItems.Count - 1; i >= 0; i--)
        {
            GameObject go = visualEffectEditorPreviewItems[i] != null ? visualEffectEditorPreviewItems[i].go : null;
            if (go != null)
                DestroyImmediate(go);
        }
        visualEffectEditorPreviewItems.Clear();
        if (visualEffectEditorPreviewUpdateRegistered)
        {
            EditorApplication.update -= UpdateVisualEffectEditorPreview;
            visualEffectEditorPreviewUpdateRegistered = false;
        }
        SceneView.RepaintAll();
    }

    private void DrawAnimationReaction(SerializedProperty reaction)
    {
        DrawMiniHeader("Animation", "Drag the Animator and choose whether to play one clip, random clip, all clips together, or all clips one by one.");
        EditorGUILayout.PropertyField(reaction.FindPropertyRelative("animator"), TipContent("Animator To Play"));
        DrawEnumPopup(reaction.FindPropertyRelative("animationPlayMode"), ReactionAnimationPlayModeValues, ReactionAnimationPlayModeLabels, TipContent("How Should Animations Play"));
        ActivityReactionAnimationPlayMode mode = (ActivityReactionAnimationPlayMode)reaction.FindPropertyRelative("animationPlayMode").enumValueIndex;
        if (mode == ActivityReactionAnimationPlayMode.SelectedClipOnly)
        {
            EditorGUILayout.PropertyField(reaction.FindPropertyRelative("animationClip"), TipContent("Animation Clip To Play"));
        }
        else
        {
            EditorGUILayout.PropertyField(reaction.FindPropertyRelative("animationClips"), TipContent("Animation Clips", "Add clips here. Random picks one. All Together starts all. One By One plays clips in order."), true);
        }
        DrawCommonReactionAudio(reaction);
        EditorGUILayout.PropertyField(reaction.FindPropertyRelative("doNotRestartWhilePlaying"), TipContent("Do Not Restart Same Animation", "ON = if the same animation is already playing, a fast tap will not restart it from frame 0."));
        EditorGUILayout.PropertyField(reaction.FindPropertyRelative("waitUntilFinished"), TipContent("Wait Until Animation Finishes"));
        EditorGUILayout.PropertyField(reaction.FindPropertyRelative("animationSpeed"), TipContent("Animation Speed"));
        EditorGUILayout.PropertyField(reaction.FindPropertyRelative("blocksNextInput"), TipContent("Block All Child Input While This Plays"));
        DrawActivityTransformFields(reaction);
    }

    private void DrawCommonReactionAudio(SerializedProperty reaction)
    {
        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Optional Audio For This Reaction", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(reaction.FindPropertyRelative("optionalSfx"), TipContent("Sound Effect Optional"));
        EditorGUILayout.PropertyField(reaction.FindPropertyRelative("reactionVoiceOver"), TipContent("Voice Line Optional"));
    }

    private void DrawSoundReaction(SerializedProperty reaction)
    {
        DrawMiniHeader("Sound", "Use for sound-only reactions.");
        EditorGUILayout.PropertyField(reaction.FindPropertyRelative("mainAudio"), TipContent("Audio Clip"));
        EditorGUILayout.PropertyField(reaction.FindPropertyRelative("mainAudioVolume"), TipContent("Volume"));
        DrawEnumPopup(reaction.FindPropertyRelative("sfxMode"), SfxValues, SfxLabels, TipContent("Audio Play Rule"));
    }

    private void DrawVoiceReaction(SerializedProperty reaction)
    {
        DrawMiniHeader("Voice", "Use for voice lines that are part of this reaction.");
        EditorGUILayout.PropertyField(reaction.FindPropertyRelative("reactionVoiceOver"), TipContent("Voice Clip"));
        EditorGUILayout.PropertyField(reaction.FindPropertyRelative("reactionVoiceVolume"), TipContent("Voice Volume"));
        EditorGUILayout.PropertyField(reaction.FindPropertyRelative("waitForReactionVoiceOver"), TipContent("Wait Until Voice Finishes"));
        EditorGUILayout.PropertyField(reaction.FindPropertyRelative("stopVoiceWhenReactionEnds"), TipContent("Stop Voice When Reaction Ends"));
    }

    private void DrawObjectToggleReaction(SerializedProperty reaction, ActivityReactionType type)
    {
        DrawMiniHeader(type == ActivityReactionType.EnableObjects ? "Turn Objects On" : "Turn Objects Off", "Drag one or more objects. Optional audio can play with the same action.");
        EditorGUILayout.PropertyField(reaction.FindPropertyRelative("objects"), TipContent("Objects"), true);
        DrawCommonReactionAudio(reaction);
    }

    private void DrawColorReaction(SerializedProperty reaction)
    {
        DrawMiniHeader("Change Color", "Temporarily changes a material color.");
        EditorGUILayout.PropertyField(reaction.FindPropertyRelative("targetRenderer"), TipContent("Object Renderer"));
        EditorGUILayout.PropertyField(reaction.FindPropertyRelative("targetColor"), TipContent("Target Color"));
        EditorGUILayout.PropertyField(reaction.FindPropertyRelative("colorChangeSeconds"), TipContent("Change Time"));
        EditorGUILayout.PropertyField(reaction.FindPropertyRelative("restoreColorAfterSeconds"), TipContent("Restore After Seconds 0 Never"));
        DrawCommonReactionAudio(reaction);
    }

    private void DrawMoveReaction(SerializedProperty reaction)
    {
        DrawMiniHeader("Move Object", "Move an object to a target transform or by an offset.");
        EditorGUILayout.PropertyField(reaction.FindPropertyRelative("objectToMove"), TipContent("Object To Move"));
        EditorGUILayout.PropertyField(reaction.FindPropertyRelative("moveTarget"), TipContent("Move Target Optional"));
        EditorGUILayout.PropertyField(reaction.FindPropertyRelative("moveOffset"), TipContent("Move Offset Optional"));
        EditorGUILayout.PropertyField(reaction.FindPropertyRelative("moveDurationSeconds"), TipContent("Move Time Seconds"));
        DrawActivityTransformFields(reaction);
        DrawCommonReactionAudio(reaction);
    }

    private void DrawCustomReaction(SerializedProperty reaction)
    {
        DrawMiniHeader("Custom Action", "Advanced. Call any UnityEvent if built-in reactions are not enough.");
        EditorGUILayout.PropertyField(reaction.FindPropertyRelative("customAction"), TipContent("Custom UnityEvent"));
        DrawCommonReactionAudio(reaction);
    }

    private void DrawReactionAdvanced(SerializedProperty reaction)
    {
        SerializedProperty advanced = reaction.FindPropertyRelative("showAdvancedOptions");
        advanced.boolValue = EditorGUILayout.Foldout(advanced.boolValue, "More Settings Optional", true);

        if (!advanced.boolValue)
            return;

        EditorGUI.indentLevel++;

        EditorGUILayout.LabelField("Timing", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(reaction.FindPropertyRelative("startDelaySeconds"), TipContent("Wait Before This Reaction Starts"));
        EditorGUILayout.PropertyField(reaction.FindPropertyRelative("extraWaitSeconds"), TipContent("Wait After This Reaction Ends"));
        EditorGUILayout.PropertyField(reaction.FindPropertyRelative("cooldownSeconds"), TipContent("Cooldown Before It Can Run Again"));
        EditorGUILayout.PropertyField(reaction.FindPropertyRelative("maxTriggerCount"), TipContent("Maximum Times This Can Run 0 Unlimited"));
        EditorGUILayout.PropertyField(reaction.FindPropertyRelative("reactionDurationSeconds"), TipContent("Manual Duration 0 Auto"));

        EditorGUILayout.Space(3);
        EditorGUILayout.LabelField("Audio", EditorStyles.boldLabel);
        DrawEnumPopup(reaction.FindPropertyRelative("sfxMode"), SfxValues, SfxLabels, TipContent("Sound Effect Play Rule"));
        EditorGUILayout.PropertyField(reaction.FindPropertyRelative("sfxVolume"), TipContent("Sound Effect Volume"));
        EditorGUILayout.PropertyField(reaction.FindPropertyRelative("sfxMinimumGapSeconds"), TipContent("Minimum Gap Between Sound Effects"));

        EditorGUILayout.Space(3);
        EditorGUILayout.LabelField("Visual Effect Extra", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(reaction.FindPropertyRelative("particleBurstCount"), TipContent("Particle Burst Amount"));
        EditorGUILayout.PropertyField(reaction.FindPropertyRelative("objectSpreadRadius"), TipContent("3D Object Spread"));
        EditorGUILayout.PropertyField(reaction.FindPropertyRelative("objectLaunchForce"), TipContent("3D Object Launch Force"));
        EditorGUILayout.PropertyField(reaction.FindPropertyRelative("randomizeObjectRotation"), TipContent("Random 3D Rotation"));
        EditorGUILayout.PropertyField(reaction.FindPropertyRelative("keepSpawnedObjectsInWorldSpace"), TipContent("Keep Spawned 3D Copies In World Space"));

        EditorGUI.indentLevel--;
    }

    private void DrawFinishSection(SerializedProperty activity)
    {
        DrawStepBox("10. When Is The Activity Complete?", "Choose what makes this activity finish. Timing values are kept in the Timing section above.");
        SerializedProperty finish = activity.FindPropertyRelative("finishWhen");
        DrawEnumPopup(finish, FinishValues, FinishLabels, TipContent("Activity Ends When"));

        ActivityFinishRule rule = (ActivityFinishRule)finish.enumValueIndex;
        if (rule == ActivityFinishRule.AfterRequiredInputs)
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("requiredInputCount"), TipContent("Required Input Count"));

        EditorGUILayout.PropertyField(activity.FindPropertyRelative("waitForRunningReactionsBeforeFinish"), TipContent("Wait For Result Actions Before Ending"));

        DrawStepBox("11. What Happens After Activity?", "ON = automatically continue based on where this activity was placed. Wrong option never uses this path.");
        EditorGUILayout.PropertyField(activity.FindPropertyRelative("continueAfterComplete"), TipContent("Automatically Continue After Activity"));
        EditorGUILayout.PropertyField(activity.FindPropertyRelative("retryIfFailed"), TipContent("Retry If Failed"));
        EditorGUILayout.PropertyField(activity.FindPropertyRelative("successMessage"), TipContent("Success Message Optional"));
        if (!string.IsNullOrWhiteSpace(activity.FindPropertyRelative("successMessage").stringValue))
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("successMessageSeconds"), TipContent("Show Success Message For Seconds"));
        EditorGUILayout.PropertyField(activity.FindPropertyRelative("tryAgainMessage"), TipContent("Try Again Message Optional"));

        SerializedProperty advancedTiming = activity.FindPropertyRelative("maxReactionWaitSeconds");
        advancedTiming.isExpanded = EditorGUILayout.Foldout(advancedTiming.isExpanded, "Advanced Optional Timing", true);
        if (advancedTiming.isExpanded)
        {
            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("maxReactionWaitSeconds"), TipContent("Safety Max Wait Seconds"));
            EditorGUILayout.PropertyField(activity.FindPropertyRelative("showTimerProgress"), TipContent("Show Timer Or Progress UI If Needed"));
            EditorGUILayout.HelpBox("No-input timing is controlled only in Section 5 Timing, so beginner setup stays in one place.", MessageType.None);
            EditorGUI.indentLevel--;
        }
    }

    private void DrawRequiredSetup()
    {
        EditorGUILayout.Space(6);
        showSetup = EditorGUILayout.Foldout(showSetup, "Required Setup, Assign Once", true, EditorStyles.foldoutHeader);
        if (!showSetup) return;

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        EditorGUILayout.HelpBox("Assign these once on the page. New activity setters should not need to change them unless UI, camera, or audio source changed.", MessageType.Info);
        EditorGUILayout.PropertyField(activityPanel, TipContent("Activity UI Panel"));
        EditorGUILayout.PropertyField(defaultRaycastCamera, TipContent("Camera For Object Taps"));
        EditorGUILayout.PropertyField(defaultAudioSource, TipContent("Audio Source For Sounds"));
        EditorGUILayout.EndVertical();
    }

    private void DrawPageOptions()
    {
        EditorGUILayout.Space(4);
        showPageOptions = EditorGUILayout.Foldout(showPageOptions, "Page Options", true, EditorStyles.foldoutHeader);
        if (!showPageOptions) return;

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        EditorGUILayout.PropertyField(completeImmediatelyWhenNoActivities, TipContent("No Activities Means Page Finishes Normally"));
        EditorGUILayout.PropertyField(restartImmediatelyOnReplay, TipContent("Start Activities Immediately On Replay"));
        EditorGUILayout.HelpBox("For AR story pages, usually keep Start Activities Immediately On Replay OFF.", MessageType.None);
        EditorGUILayout.EndVertical();
    }

    private void DrawControllerEvents()
    {
        EditorGUILayout.Space(4);
        showControllerEvents = EditorGUILayout.Foldout(showControllerEvents, "Advanced Events Optional", true, EditorStyles.foldoutHeader);
        if (!showControllerEvents) return;

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        EditorGUILayout.PropertyField(onActivitiesStarted, TipContent("When Activities Start"));
        EditorGUILayout.PropertyField(onActivitiesCompleted, TipContent("When All Activities Complete"));
        EditorGUILayout.PropertyField(onActivitiesReset, TipContent("When Activities Reset"));
        EditorGUILayout.EndVertical();
    }

    private static bool HasChoiceButtonLayout(ActivityPanel panel, int optionCount)
    {
        if (panel == null)
            return false;

        if (panel.dynamicButtonParent != null && panel.dynamicButtonPrefab != null)
            return true;

        switch (optionCount)
        {
            case 2:
                return panel.twoOptionGroup != null || HasEnoughAssignedButtons(panel.twoOptionButtons, 2);
            case 3:
                return panel.threeOptionGroup != null || HasEnoughAssignedButtons(panel.threeOptionButtons, 3);
            case 4:
                return panel.fourOptionGroup != null || HasEnoughAssignedButtons(panel.fourOptionButtons, 4);
            case 5:
                return panel.fiveOptionGroup != null || HasEnoughAssignedButtons(panel.fiveOptionButtons, 5);
            default:
                return false;
        }
    }

    private static bool HasEnoughAssignedButtons(UnityEngine.UI.Button[] buttons, int required)
    {
        if (buttons == null || buttons.Length < required)
            return false;
        for (int i = 0; i < required; i++)
        {
            if (buttons[i] == null)
                return false;
        }
        return true;
    }

    private static bool HasColliderInChildren(GameObject go)
    {
        return go != null && go.GetComponentInChildren<Collider>(true) != null;
    }

    private void AddPresetActivity(ActivityInputKind kind, string name)
    {
        AddActivity();
        SerializedProperty activity = activities.GetArrayElementAtIndex(activities.arraySize - 1);
        activity.FindPropertyRelative("activityName").stringValue = name;
        activity.FindPropertyRelative("childInput").enumValueIndex = (int)kind;
        activity.FindPropertyRelative("resultPlayTiming").enumValueIndex = (int)ActivityResultPlayTiming.OnEveryCorrectInput;

        if (kind == ActivityInputKind.ProgressGate)
        {
            activity.FindPropertyRelative("activityName").stringValue = "Progress Gate";
            activity.FindPropertyRelative("startWhen").enumValueIndex = (int)ActivityStartRule.AfterRevealFinishes;
            activity.FindPropertyRelative("instructionText").stringValue = "Keep tapping to continue";
            activity.FindPropertyRelative("finishWhen").enumValueIndex = (int)ActivityFinishRule.AfterActiveTimeEnds;
            activity.FindPropertyRelative("useProgressBar").boolValue = true;
            activity.FindPropertyRelative("progressGateCompletesBy").enumValueIndex = (int)ActivityProgressGateCompletionMode.RequiredTapCount;
            activity.FindPropertyRelative("progressRequiredTaps").intValue = 5;
            activity.FindPropertyRelative("progressRequiredTappingSeconds").floatValue = 5f;
            activity.FindPropertyRelative("progressTapActiveWindowSeconds").floatValue = 0.35f;
            activity.FindPropertyRelative("progressDropsWhenNotTapping").boolValue = true;
            activity.FindPropertyRelative("progressLossPerSecond").floatValue = 25f;
            activity.FindPropertyRelative("progressAutoStartStoryAfterSeconds").floatValue = 0f;
            activity.FindPropertyRelative("playResultWhenProgressAutoSkips").boolValue = true;
        }
        else if (kind == ActivityInputKind.GroupAction)
        {
            activity.FindPropertyRelative("activityName").stringValue = "Group Action";
            activity.FindPropertyRelative("startWhen").enumValueIndex = (int)ActivityStartRule.AfterRevealFinishes;
            activity.FindPropertyRelative("instructionText").stringValue = "Tap any character";
            activity.FindPropertyRelative("finishWhen").enumValueIndex = (int)ActivityFinishRule.AfterActiveTimeEnds;
            activity.FindPropertyRelative("groupAutoStartStoryAfterSeconds").floatValue = 0f;
            activity.FindPropertyRelative("groupPlayActionsWhenAutoSkipped").boolValue = true;
            activity.FindPropertyRelative("groupWaitSecondsBeforeStory").floatValue = 0f;
        }
        else if (kind == ActivityInputKind.AnswerQuestion || kind == ActivityInputKind.ChooseOption)
        {
            activity.FindPropertyRelative("activityName").stringValue = "Choose Correct Option";
            activity.FindPropertyRelative("startWhen").enumValueIndex = (int)ActivityStartRule.AfterRevealFinishes;
            activity.FindPropertyRelative("instructionText").stringValue = "Choose the correct option";
            activity.FindPropertyRelative("finishWhen").enumValueIndex = (int)ActivityFinishRule.AfterFirstValidInput;
            activity.FindPropertyRelative("retryIfFailed").boolValue = true;
            activity.FindPropertyRelative("continueAfterComplete").boolValue = true;
            activity.FindPropertyRelative("choiceCorrectBehaviour").enumValueIndex = (int)ActivityChoiceCorrectBehaviour.ContinueStoryImmediately;
            activity.FindPropertyRelative("choiceWrongOptionBehaviour").enumValueIndex = (int)ActivityChoiceWrongOptionBehaviour.DisableAndGrayOut;
            SerializedProperty options = activity.FindPropertyRelative("choiceOptions");
            options.arraySize = 3;
            for (int i = 0; i < options.arraySize; i++)
            {
                SerializedProperty option = options.GetArrayElementAtIndex(i);
                option.FindPropertyRelative("buttonText").stringValue = "Option " + (i + 1);
                option.FindPropertyRelative("isCorrect").boolValue = i == 0;
                option.FindPropertyRelative("playResultForThisOption").boolValue = i != 0;
            }
        }
        else if (kind == ActivityInputKind.HelpAction)
        {
            activity.FindPropertyRelative("activityName").stringValue = "Help Action";
            activity.FindPropertyRelative("startWhen").enumValueIndex = (int)ActivityStartRule.AfterRevealFinishes;
            activity.FindPropertyRelative("instructionText").stringValue = "Tap to help";
            activity.FindPropertyRelative("finishWhen").enumValueIndex = (int)ActivityFinishRule.AfterActiveTimeEnds;
            activity.FindPropertyRelative("useProgressBar").boolValue = true;
            activity.FindPropertyRelative("helpProgressGainPerTap").floatValue = 20f;
            activity.FindPropertyRelative("helpProgressLossPerSecond").floatValue = 25f;
            activity.FindPropertyRelative("helpAutoContinueAfterSeconds").floatValue = 5f;
            activity.FindPropertyRelative("helpAnimationSpeed").floatValue = 1f;
            activity.FindPropertyRelative("helpCompleteWhenProgressFull").boolValue = true;
            activity.FindPropertyRelative("helpResetAnimationWhenProgressIsEmpty").boolValue = true;
            activity.FindPropertyRelative("helpWaitForAnimationBeforeContinue").boolValue = true;
        }
        else if (kind == ActivityInputKind.WaitForStoryThenTapObject)
        {
            activity.FindPropertyRelative("activityName").stringValue = "Wait For Story Then Tap Object";
            activity.FindPropertyRelative("startWhen").enumValueIndex = (int)ActivityStartRule.AfterStoryObjectFinishes;
            activity.FindPropertyRelative("instructionText").stringValue = "Tap the highlighted object";
            activity.FindPropertyRelative("pauseStoryWhileActivity").boolValue = true;
            activity.FindPropertyRelative("storyMomentShowProgressBar").boolValue = true;
            activity.FindPropertyRelative("storyMomentTotalActivitySeconds").floatValue = 30f;
            activity.FindPropertyRelative("storyMomentShowHintAfterSeconds").floatValue = 5f;
            activity.FindPropertyRelative("storyMomentSkipAfterHintSeconds").floatValue = 10f;
            activity.FindPropertyRelative("storyMomentMoveUpHeight").floatValue = 0.25f;
            activity.FindPropertyRelative("storyMomentMoveSmoothness").floatValue = 12f;
            activity.FindPropertyRelative("storyMomentBreakShakeAmount").floatValue = 0.08f;
            activity.FindPropertyRelative("storyMomentBreakShakeSeconds").floatValue = 0.45f;
            activity.FindPropertyRelative("storyMomentSwitchAtShakePercent").floatValue = 0.5f;
            activity.FindPropertyRelative("storyMomentDropBackSeconds").floatValue = 0.45f;
            activity.FindPropertyRelative("continueAfterComplete").boolValue = true;
        }
        else if (kind == ActivityInputKind.TapManyTimes || kind == ActivityInputKind.KeepTapping)
            activity.FindPropertyRelative("finishWhen").enumValueIndex = (int)ActivityFinishRule.AfterRequiredInputs;
        else
            activity.FindPropertyRelative("finishWhen").enumValueIndex = (int)ActivityFinishRule.AfterActiveTimeEnds;
    }

    private void AddActivity()
    {
        int index = activities.arraySize;
        activities.InsertArrayElementAtIndex(index);
        SerializedProperty activity = activities.GetArrayElementAtIndex(index);

        activity.FindPropertyRelative("enabled").boolValue = true;
        activity.FindPropertyRelative("activityName").stringValue = "New Activity";
        activity.FindPropertyRelative("startWhen").enumValueIndex = (int)ActivityStartRule.AfterStoryEnds;
        activity.FindPropertyRelative("waitBeforeStart").floatValue = 0f;
        activity.FindPropertyRelative("instructionText").stringValue = "Tap to continue";
        activity.FindPropertyRelative("childInput").enumValueIndex = (int)ActivityInputKind.TapAnywhere;
        activity.FindPropertyRelative("nextInputRule").enumValueIndex = (int)ActivityNextInputRule.Immediately;
        activity.FindPropertyRelative("finishWhen").enumValueIndex = (int)ActivityFinishRule.AfterActiveTimeEnds;
        activity.FindPropertyRelative("resultPlayTiming").enumValueIndex = (int)ActivityResultPlayTiming.OnEveryCorrectInput;
        activity.FindPropertyRelative("activeTimeSeconds").floatValue = 10f;
        activity.FindPropertyRelative("continueAfterComplete").boolValue = true;
        activity.FindPropertyRelative("waitForRunningReactionsBeforeFinish").boolValue = true;
        activity.FindPropertyRelative("maxReactionWaitSeconds").floatValue = 30f;
        activity.FindPropertyRelative("showHintWhenWrongInput").boolValue = true;
        activity.FindPropertyRelative("wrongInputHintText").stringValue = "Try tapping the highlighted object";
        activity.FindPropertyRelative("wrongInputSoundVolume").floatValue = 1f;
        activity.FindPropertyRelative("enableNoInputHelp").boolValue = true;
        activity.FindPropertyRelative("noInputHintAfterSeconds").floatValue = 3f;
        activity.FindPropertyRelative("noInputHintText").stringValue = "Try the highlighted object";
        activity.FindPropertyRelative("noInputActionAfterHint").enumValueIndex = (int)ActivityNoInputAction.AutoPlayResultThenContinue;
        activity.FindPropertyRelative("autoSkipAfterHintSeconds").floatValue = 3f;
        activity.FindPropertyRelative("useSameHintEffectsForNoInput").boolValue = true;
        activity.FindPropertyRelative("helpProgressGainPerTap").floatValue = 20f;
        activity.FindPropertyRelative("helpProgressLossPerSecond").floatValue = 25f;
        activity.FindPropertyRelative("helpAutoContinueAfterSeconds").floatValue = 5f;
        activity.FindPropertyRelative("helpCompleteWhenProgressFull").boolValue = true;
        activity.FindPropertyRelative("helpAnimationSpeed").floatValue = 1f;
        activity.FindPropertyRelative("helpResetAnimationWhenProgressIsEmpty").boolValue = true;
        activity.FindPropertyRelative("helpWaitForAnimationBeforeContinue").boolValue = true;
        activity.FindPropertyRelative("helpTapSoundVolume").floatValue = 1f;
        activity.FindPropertyRelative("progressGateCompletesBy").enumValueIndex = (int)ActivityProgressGateCompletionMode.RequiredTapCount;
        activity.FindPropertyRelative("progressRequiredTaps").intValue = 5;
        activity.FindPropertyRelative("progressRequiredTappingSeconds").floatValue = 5f;
        activity.FindPropertyRelative("progressTapActiveWindowSeconds").floatValue = 0.35f;
        activity.FindPropertyRelative("progressDropsWhenNotTapping").boolValue = true;
        activity.FindPropertyRelative("progressLossPerSecond").floatValue = 25f;
        activity.FindPropertyRelative("progressAutoStartStoryAfterSeconds").floatValue = 0f;
        activity.FindPropertyRelative("playResultWhenProgressAutoSkips").boolValue = true;
        activity.FindPropertyRelative("progressTapSoundVolume").floatValue = 1f;
        activity.FindPropertyRelative("resultAnimationSpeed").floatValue = 1f;
        activity.FindPropertyRelative("waitForResultAnimation").boolValue = true;
        activity.FindPropertyRelative("resultVoiceVolume").floatValue = 1f;
        activity.FindPropertyRelative("waitForResultVoiceOver").boolValue = true;
        activity.FindPropertyRelative("resultSoundVolume").floatValue = 1f;
        activity.FindPropertyRelative("waitForResultSound").boolValue = false;
        activity.FindPropertyRelative("resultExtraWaitSeconds").floatValue = 0f;
        activity.FindPropertyRelative("groupAutoStartStoryAfterSeconds").floatValue = 0f;
        activity.FindPropertyRelative("groupPlayActionsWhenAutoSkipped").boolValue = true;
        activity.FindPropertyRelative("groupWaitSecondsBeforeStory").floatValue = 0f;
        activity.FindPropertyRelative("groupLoopSoundVolume").floatValue = 1f;
        activity.FindPropertyRelative("loopGroupSoundUntilGroupFinishes").boolValue = false;
        activity.FindPropertyRelative("groupResultVoiceVolume").floatValue = 1f;
        activity.FindPropertyRelative("groupWaitForVoiceOver").boolValue = true;
        activity.FindPropertyRelative("pulseTargetObject").boolValue = true;
        activity.FindPropertyRelative("targetPulseScale").floatValue = 1.18f;
        activity.FindPropertyRelative("targetPulseRepeatCount").intValue = 3;
        activity.FindPropertyRelative("targetPulseSeconds").floatValue = 0.45f;
        activity.FindPropertyRelative("tintTargetObject").boolValue = false;
        activity.FindPropertyRelative("targetTintColor").colorValue = Color.yellow;
        activity.FindPropertyRelative("targetTintRepeatCount").intValue = 3;
        activity.FindPropertyRelative("targetTintSeconds").floatValue = 0.45f;
        activity.FindPropertyRelative("showHintObject").boolValue = false;
        activity.FindPropertyRelative("hintObjectSeconds").floatValue = 1.5f;
        activity.isExpanded = true;
    }

    private void AddReaction(SerializedProperty reactions, ActivityReactionType type, string name, ActivityReactionMoment moment)
    {
        int index = reactions.arraySize;
        reactions.InsertArrayElementAtIndex(index);
        SerializedProperty reaction = reactions.GetArrayElementAtIndex(index);

        reaction.FindPropertyRelative("enabled").boolValue = true;
        reaction.FindPropertyRelative("reactionName").stringValue = name;
        reaction.FindPropertyRelative("type").enumValueIndex = (int)type;
        reaction.FindPropertyRelative("playWhen").enumValueIndex = (int)moment;
        if (reaction.FindPropertyRelative("visualEffectPlayMode") != null)
            reaction.FindPropertyRelative("visualEffectPlayMode").enumValueIndex = (int)VisualEffectPlayMode.AddNewEachInput;
        reaction.FindPropertyRelative("startDelaySeconds").floatValue = 0f;
        reaction.FindPropertyRelative("extraWaitSeconds").floatValue = 0f;
        reaction.FindPropertyRelative("cooldownSeconds").floatValue = 0f;
        reaction.FindPropertyRelative("maxTriggerCount").intValue = 0;
        reaction.FindPropertyRelative("reactionDurationSeconds").floatValue = 0f;
        reaction.FindPropertyRelative("particleBurstCount").intValue = 25;
        reaction.FindPropertyRelative("objectBurstCount").intValue = 1;
        reaction.FindPropertyRelative("objectLifeSeconds").floatValue = 2f;
        reaction.FindPropertyRelative("objectSpreadRadius").floatValue = 0.15f;
        reaction.FindPropertyRelative("objectLaunchForce").floatValue = 0f;
        reaction.FindPropertyRelative("randomizeObjectRotation").boolValue = true;
        reaction.FindPropertyRelative("sfxMinimumGapSeconds").floatValue = 0f;
        reaction.FindPropertyRelative("sfxVolume").floatValue = 1f;
        reaction.FindPropertyRelative("mainAudioVolume").floatValue = 1f;
        reaction.FindPropertyRelative("reactionVoiceVolume").floatValue = 1f;
        reaction.FindPropertyRelative("sfxMode").enumValueIndex = type == ActivityReactionType.AnimationClip ? (int)ReactionSfxMode.StopWhenReactionEnds : (int)ReactionSfxMode.PlayOnce;
        reaction.FindPropertyRelative("animationSpeed").floatValue = 1f;
        reaction.FindPropertyRelative("doNotRestartWhilePlaying").boolValue = type == ActivityReactionType.AnimationClip;
        reaction.FindPropertyRelative("blocksNextInput").boolValue = false;
        reaction.FindPropertyRelative("waitUntilFinished").boolValue = type == ActivityReactionType.AnimationClip || type == ActivityReactionType.VoiceOver;
        reaction.FindPropertyRelative("showAdvancedOptions").boolValue = false;
        reaction.isExpanded = true;
    }


    private static GUIContent TipContent(string text)
    {
        return new GUIContent(text, TooltipFor(text));
    }

    private static GUIContent TipContent(string text, string tooltip)
    {
        return new GUIContent(text, tooltip);
    }

    private static string TooltipFor(string text)
    {
        switch (text)
        {
            case "Use This Activity": return "Turn this activity on or off without deleting its setup.";
            case "Activity Name": return "Give the activity a simple team-readable name. Example: Welcome Tap, Drum Tapping, Choose Answer.";
            case "Start When": return "Choose when the activity becomes available in the story.";
            case "Extra Wait Before Start": return "Optional wait after the selected start moment. Use 0 for no extra wait.";
            case "Start Key Optional": return "Optional key used by Animation Event, Manual Start, or another script.";

            case "Text Shown To Child": return "Instruction shown to the child. Keep it short. Example: Tap to welcome the king.";
            case "Instruction Voice Optional": return "Optional spoken instruction that plays when the activity starts.";
            case "Wait Until Voice Finishes": return "If ON, the child cannot interact until the instruction voice finishes.";
            case "Background Audio During Activity Optional": return "Optional music or ambience while this activity is active.";
            case "Loop Background Audio": return "Keep background audio repeating until the activity ends.";
            case "Background Audio Volume": return "Volume for background audio. 0 is silent, 1 is full volume.";
            case "Fade Out When Activity Ends": return "Fade background audio out when the activity ends instead of stopping instantly.";
            case "Fade Seconds": return "How long the background audio fade takes.";

            case "Child Action": return "Choose what the child must do: tap screen, tap object, tap button, answer, wait, and so on.";
            case "Object To Tap": return "Drag the 3D object the child must tap. It should have a Collider.";
            case "Objects To Tap": return "Drag the objects the child must tap, usually for sequence activities like grass 1, grass 2, grass 3.";
            case "Must Tap In Order": return "If ON, the child must tap objects in this exact list order.";
            case "How Many Inputs Needed": return "Number of valid taps or inputs required.";
            case "Wait Seconds": return "For Wait Only activity, how long the activity waits before completing.";
            case "Button Texts": return "Button labels for button, choice, or question activities.";
            case "Show Timer / Progress If Needed": return "Show progress UI for timed, repeated tap, or sequence activities.";
            case "Next Input Is Accepted": return "Usually leave as Let Each Reaction Control Itself. Use blocking only when the whole activity should ignore input while a reaction plays.";
            case "Delay Before Next Input": return "Used only with fixed delay input control.";
            case "Max Time To First Input 0 No Limit": return "Optional timeout if the child does not give the first input. 0 means no timeout.";

            case "Use This Reaction": return "Turn this reaction on or off without deleting it.";
            case "Reaction Name": return "Name what this reaction does. Example: Flowers On Every Tap or Character Wave.";
            case "What Should Happen": return "Choose the type of result: visual effect, animation, sound, voice, object on/off, color, move, or custom.";
            case "When Should This Run": return "Choose when this reaction runs. Example: every valid input, only when free, activity start, activity end, or input fail.";

            case "Visual Effect Objects": return "Drag ParticleSystem objects or normal 3D prefabs like flowers, petals, leaves, coins, stars, or sparkles. ParticleSystems emit. Normal 3D prefabs spawn briefly like effects.";
            case "If Child Taps Again": return "Choose what happens if the child taps again while this visual effect is still visible. Add New keeps old effects and adds more. Restart starts the same effect again. Wait ignores new taps until the effect finishes.";
            case "Where Should It Appear From": return "Optional. Drag a Transform if spawned flowers, petals, or effects should appear from a specific place. Leave empty to use each effect object's own position.";
            case "3D Copies Per Input": return "For normal 3D prefabs, how many copies spawn each valid input.";
            case "3D Copy Visible Time": return "For normal 3D prefabs, how long each spawned copy stays before being removed.";
            case "Sound Effect Optional": return "Optional short sound for this reaction. Example: sparkle, clap, hit, whoosh, bell. Leave empty if this reaction does not need sound.";

            case "Animator To Play": return "Drag the Animator component on the character or object that should animate.";
            case "Animation Clip To Play": return "Drag the animation clip. No typing animation state names.";
            case "Voice Line Optional": return "Optional spoken voice for this reaction. Example: character dialogue, narrator line, or instruction voice. Leave empty if not needed.";
            case "Do Not Restart While Playing": return "If ON, this reaction will not restart while it is already running. Recommended for character animations.";
            case "Wait Until Animation Finishes": return "If ON, this reaction stays busy until the animation clip finishes.";
            case "Animation Speed": return "Animation playback speed. 1 is normal. 2 is double speed. 0.5 is half speed.";
            case "Block All Child Input While This Plays": return "If ON, every child input is ignored while this reaction plays. Leave OFF when only this reaction should block itself.";

            case "Audio Clip": return "Drag the sound or voice clip for this audio-only reaction.";
            case "Volume": return "Audio volume. 0 is silent, 1 is full volume.";
            case "Audio Play Rule": return "Choose how this audio behaves when triggered many times.";
            case "Voice Clip": return "Drag a spoken voice clip.";
            case "Voice Volume": return "Voice volume. 0 is silent, 1 is full volume.";
            case "Stop Voice When Reaction Ends": return "If ON, the voice stops when this reaction ends.";

            case "Objects": return "Drag one or more GameObjects affected by this reaction.";
            case "Object Renderer": return "Drag the Renderer of the object whose material color should change.";
            case "Target Color": return "Color to apply.";
            case "Change Time": return "How long the color change takes.";
            case "Restore After Seconds 0 Never": return "Time before restoring the old color. 0 means keep the new color.";
            case "Object To Move": return "Drag the object that should move.";
            case "Move Target Optional": return "Optional target Transform to move toward.";
            case "Move Offset Optional": return "Optional movement amount when no target is assigned.";
            case "Move Time Seconds": return "How long the movement takes.";
            case "Custom UnityEvent": return "Advanced. Use this only when built-in reaction types are not enough.";

            case "Wait Before This Reaction Starts": return "Optional delay before this reaction starts after valid input.";
            case "Wait After This Reaction Ends": return "Optional extra wait after this reaction completes.";
            case "Cooldown Before It Can Run Again": return "Minimum time before this reaction can run again.";
            case "Maximum Times This Can Run 0 Unlimited": return "Maximum number of times this reaction can run. 0 means unlimited.";
            case "Manual Duration 0 Auto": return "Optional manual duration. Use 0 to auto-detect from animation or audio when possible.";
            case "Sound Effect Play Rule": return "Choose whether the sound restarts, waits, loops, or stops with the reaction.";
            case "Sound Effect Volume": return "Volume for this reaction sound. 0 is silent, 1 is full volume.";
            case "Minimum Gap Between Sound Effects": return "Minimum time between repeated sound plays. Helps prevent noisy audio spam.";
            case "Particle Burst Amount": return "For ParticleSystem objects, how many particles to emit each input.";
            case "3D Object Spread": return "How far spawned 3D objects spread from the appear point.";
            case "3D Object Launch Force": return "Optional upward push for spawned objects that have Rigidbody.";
            case "Random 3D Rotation": return "Randomize spawned object rotation for a natural effect.";
            case "Keep Spawned 3D Copies In World Space": return "Advanced. If ON, spawned 3D flowers or petals are not parented under the moving AR page. This can reduce visible shaking, but objects will not follow the page after spawning.";
            case "Show Success Message For Seconds": return "How long the success message stays visible before the activity hides and continues.";

            case "Activity Ends When": return "Choose when this activity is complete.";
            case "How Long Can The Child Play": return "How long this activity accepts child input. After this time, no new input is accepted.";
            case "Required Inputs": return "How many valid inputs are required before activity completes.";
            case "Wait For Running Reactions Before Ending": return "If ON, activity waits for running reactions like animation, voice, or effects before continuing.";
            case "Safety Max Wait Seconds": return "Maximum safety wait so the activity cannot get stuck forever.";
            case "Continue Story / Next Page After Done": return "If ON, the story continues or the turn-page overlay appears after this activity completes.";
            case "Retry If Failed": return "If ON, timeout or wrong input can retry instead of ending.";
            case "Success Message Optional": return "Optional message shown when the activity succeeds.";
            case "Try Again Message Optional": return "Optional message shown when the child should try again.";
            case "Guide Child After Wrong Tap": return "If ON, the app guides the child when they tap the wrong place. Useful for Tap Object activities.";
            case "Wrong Tap Message": return "Message shown when the child taps the wrong place. Example: Try tapping the bull.";
            case "Wrong Tap Sound Optional": return "Optional sound when the child taps the wrong place. Example: soft pop or gentle hint sound.";
            case "Wrong Tap Sound Volume": return "Volume for the wrong tap sound. 0 is silent, 1 is full volume.";
            case "Pulse Target Object": return "If ON, the correct object smoothly grows and returns to normal. This is the safest eye-catching hint for kids and works with most 3D models.";
            case "Pulse Size": return "How large the object becomes during the hint pulse. 1.18 means 18 percent bigger. Use 1.12 for subtle, 1.2 for more visible.";
            case "Pulse Repeat Count": return "How many smooth pulse beats happen when the child taps the wrong place or does nothing.";
            case "One Pulse Time": return "How long one pulse beat takes. 0.35 to 0.5 seconds is usually smooth and easy for kids to notice.";
            case "Flash Target Color": return "If ON, the target softly flashes a color. Use this only if the model material supports color changes. Pulse is safer for all models.";
            case "Flash Color": return "Color used to flash the target object.";
            case "Color Flash Count": return "How many soft color flashes happen when the child needs help.";
            case "One Color Flash Time": return "How long one smooth color flash takes. 0.35 to 0.5 seconds is usually readable.";
            case "Show Arrow Or Ring Helper": return "If ON, a helper object like arrow, ring, or glow can appear near the target.";
            case "Arrow Or Ring Helper Object": return "Drag an optional arrow, ring, or helper object. It will show briefly when the child needs help.";
            case "Helper Visible Time": return "How long the arrow or ring helper stays visible.";
            case "Help If Child Does Nothing": return "If ON, the app can show help and optionally skip the activity if the child does not interact.";
            case "Show Help After No Input Seconds": return "How many seconds to wait before showing help when the child gives no correct input.";
            case "No Input Help Message": return "Message shown if the child does nothing. Example: Try tapping the bull.";
            case "After Showing Help": return "Choose what happens after the no-input help is shown.";
            case "Skip If Still No Input After Seconds": return "After help is shown, wait this many more seconds. If the child still does not interact correctly, skip this activity and continue.";
            case "Use Same Highlight For No Input": return "If ON, no-input help uses the same pulse, color flash, arrow, and sound settings as wrong tap help.";

            case "Activity UI Panel": return "Drag UI_Canvas or ARInteractionUI. Assign once per page. The system shows only the UI needed by each activity.";
            case "Camera For Object Taps": return "Drag ARCamera. Needed for Tap Object activities.";
            case "Audio Source For Sounds": return "Drag an AudioSource used as the parent/source for activity sounds and voices.";
            case "No Activities Means Page Finishes Normally": return "If no activities are added, this page behaves like a normal story page.";
            case "Start Activities Immediately On Replay": return "Usually OFF for AR story pages. Replay should wait until the story reaches the activity again.";
            case "When Activities Start": return "Advanced UnityEvent called when activity flow starts.";
            case "When All Activities Complete": return "Advanced UnityEvent called when all activities finish.";
            case "When Activities Reset": return "Advanced UnityEvent called when activities reset, such as replay.";
            case "Option Count": return "How many option buttons the child will see. Use 2 to 5. The matching option group should be assigned once in Activity UI Panel.";
            case "Wrong Option Behaviour": return "Choose whether wrong options stay tappable, or become grey and disabled after the child selects them.";
            case "Correct Option Behaviour": return "Continue Story Immediately means no animation is needed for the correct option. Play Correct Result Then Continue Story means this option result plays first.";
            case "Hide UI While Result Plays": return "If ON, the question buttons hide while the selected option result plays. This avoids double tapping and keeps the screen clean.";
            case "Block Input While Result Plays": return "If ON, the child cannot press another option while an option animation or sound is playing.";
            case "Return Question After Wrong Answer": return "If ON, the question and remaining options appear again after a wrong option result finishes.";
            case "Correct Option Number": return "Pick the one option that completes the activity. This prevents accidentally setting two correct answers.";
            case "Play Result For This Option": return "ON means this option plays its own animation, sound, voice, or event. OFF means selecting this option only marks correct or wrong.";
            case "Character That Reacts": return "Drag the Animator of the character or object that should react while the child fills the meter.";
            case "Animation Selection": return "Choose how reaction animations are selected. By Meter Percent changes animation based on progress.";
            case "Playback Mode": return "Choose whether reactions play each tap or hold the clip that matches the current meter percent.";
            default: return "This field controls: " + text + ".";
        }
    }

    private void DrawEnumPopup<TEnum>(SerializedProperty property, TEnum[] values, string[] labels, GUIContent label) where TEnum : Enum
    {
        int current = property.enumValueIndex;
        int selected = Array.IndexOf(values, (TEnum)Enum.ToObject(typeof(TEnum), current));
        if (selected < 0) selected = 0;
        int next = EditorGUILayout.Popup(label, selected, labels);
        property.enumValueIndex = Convert.ToInt32(values[next]);
    }

    private void DrawStepBox(string title, string message)
    {
        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(message, MessageType.None);
    }

    private void DrawMiniHeader(string title, string message)
    {
        EditorGUILayout.Space(5);
        EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(message, MessageType.None);
    }

    private string NiceStart(ActivityStartRule value)
    {
        int index = Array.IndexOf(StartValues, value);
        return index >= 0 ? StartLabels[index] : value.ToString();
    }

    private string NiceInput(ActivityInputKind value)
    {
        int index = Array.IndexOf(InputValues, value);
        return index >= 0 ? InputLabels[index] : value.ToString();
    }

    private string NiceReactionType(ActivityReactionType value)
    {
        int index = Array.IndexOf(ReactionTypeValues, value);
        return index >= 0 ? ReactionTypeLabels[index] : value.ToString();
    }
}
