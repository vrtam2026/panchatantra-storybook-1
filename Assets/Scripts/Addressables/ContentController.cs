using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Playables;
using UnityEngine.Animations;

/// <summary>
/// Generic story activity builder.
/// Activity = Start + Instruction + Input + Reactions + Finish.
/// It does not own AR tracking, reveal popup, story voice, spline movement, or next page UI.
/// </summary>
public class ContentController : MonoBehaviour, IARContent
{
    public static bool AnyActivityRunning { get; private set; }
    public static event Action<bool> AnyActivityRunningChanged;

    private static void SetAnyActivityRunning(bool running)
    {
        if (AnyActivityRunning == running) return;
        AnyActivityRunning = running;
        AnyActivityRunningChanged?.Invoke(running);
    }

    [SerializeField] private List<ActivityStep> activities = new List<ActivityStep>();

    [SerializeField, HideInInspector] private int activityTemplateSchemaVersion = 0;
    private const int CurrentActivityTemplateSchemaVersion = 34;

    [Header("Required Setup")]
    [SerializeField] private ActivityPanel activityPanel;
    [SerializeField] private Camera defaultRaycastCamera;
    [SerializeField] private AudioSource defaultAudioSource;


    [Header("Flow")]
    [SerializeField] private bool completeImmediatelyWhenNoActivities = true;
    [SerializeField] private bool restartImmediatelyOnReplay = false;

    [Header("Events")]
    public UnityEvent onActivitiesStarted;
    public UnityEvent onActivitiesCompleted;
    public UnityEvent onActivitiesReset;

    public IReadOnlyList<ActivityStep> Activities => activities;
    public bool IsRunning => _flowRoutine != null;
    public int CurrentActivityIndex => _currentIndex;

    private Action _completionCallback;
    private Coroutine _flowRoutine;
    private int _currentIndex;

    private ARTrackedPageNode _pausedStoryNodeForActivity;
    private bool _storyPausedForActivity;

    private bool _storyEndReached;
    private bool _revealCompleteReached;
    private bool _voiceStartedReached;
    private bool _contentCompletionRequested;
    private bool _waitingForManualContinue;
    private readonly HashSet<string> _triggerKeys = new HashSet<string>();

    private bool _acceptingInput;
    private Func<ActivityInputData, bool> _inputValidator;
    private Action<ActivityInputData> _inputAccepted;
    private Action<ActivityInputData> _inputRejected;

    private int _acceptedInputCount;
    private int _sequenceIndex;
    private int _choiceIndex = -1;
    private bool _choiceAnswered;
    private bool _choiceCorrect;

    // Active choice UI restore state. Used when tracking is lost/found while a scenario activity is waiting.
    private ActivityStep _activeChoiceStep;
    private IList<string> _activeChoiceLabels;
    private bool[] _activeChoiceDisabledOptions;
    private Action<int> _activeChoiceClickHandler;

    private readonly HashSet<GameObject> _uniqueTappedGroupObjects = new HashSet<GameObject>();
    private ActivityInputData _lastAcceptedInput;

    private readonly Dictionary<ActivityReaction, Coroutine> _runningReactions = new Dictionary<ActivityReaction, Coroutine>();
    private readonly HashSet<ActivityReaction> _busyReactions = new HashSet<ActivityReaction>();
    private readonly Dictionary<ActivityReaction, int> _reactionTriggerCounts = new Dictionary<ActivityReaction, int>();
    private readonly Dictionary<ActivityReaction, float> _lastReactionTriggerTime = new Dictionary<ActivityReaction, float>();
    private readonly Dictionary<ActivityReaction, float> _lastReactionSfxTime = new Dictionary<ActivityReaction, float>();
    private readonly Dictionary<ActivityReaction, AudioSource> _reactionAudioSources = new Dictionary<ActivityReaction, AudioSource>();
    private readonly List<AudioSource> _activityAudioSources = new List<AudioSource>();
    private readonly List<PlayableGraph> _activeGraphs = new List<PlayableGraph>();
    private readonly List<PlayableGraph> _activeChoiceGraphs = new List<PlayableGraph>();
    // Template-wide activity animation safety.
    // Any animation played by the activity uses PlayableGraph and freezes the Animator Controller
    // so controller transitions cannot advance the story while an activity animation is playing.
    private readonly Dictionary<Animator, float> _activityAnimatorOriginalSpeeds = new Dictionary<Animator, float>();
    private readonly List<Coroutine> _activeChoiceAnimationRoutines = new List<Coroutine>();
    // Tracks coroutines that restore scenario transforms after a delay so they can be stopped on reset.
    private readonly List<Coroutine> _scenarioTransformRestoreRoutines = new List<Coroutine>();
    // Records the active state of every object that an activity will change so replay can restore them.
    private readonly Dictionary<GameObject, bool> _originalObjectActiveStates = new Dictionary<GameObject, bool>();

    // ── SFX runtime tracking ──────────────────────────────────────────────
    // Tracks the last time a correct tap sound played for gap enforcement.
    private float _lastCorrectTapSoundTime = -999f;
    // Tracks which helper animation clip index last played its sound (0-based).
    private int _lastHelperClipSoundIndex = -1;
    // Time the last helper clip sound played, for cooldown gate.
    private float _lastHelperClipSoundTime = -999f;
    // Active looping sound for the helper animation group. This is optional and stops with the activity.
    private AudioSource _activeProgressHelperGroupLoopSource;
    private ActivityStep _activeProgressHelperGroupLoopStep;
    // Tracks reaction-target animation clip sounds.
    private int _lastReactionClipSoundIndex = -1;
    private float _lastReactionClipSoundTime = -999f;
    // Active looping sound for reaction-target animation groups.
    private AudioSource _activeProgressReactionGroupLoopSource;
    private ActivityStep _activeProgressReactionGroupLoopStep;
    // Active coroutine that shows a milestone hint text then returns to instruction.
    private Coroutine _activeMilestoneTextCoroutine;
    private readonly List<GameObject> _spawnedVfxObjects = new List<GameObject>();
    private readonly Dictionary<ActivityReaction, List<GameObject>> _spawnedVisualEffectObjectsByReaction = new Dictionary<ActivityReaction, List<GameObject>>();
    private readonly Dictionary<Renderer, Color> _originalRendererColors = new Dictionary<Renderer, Color>();
    private readonly Dictionary<Transform, Vector3> _originalTargetScales = new Dictionary<Transform, Vector3>();
    private Coroutine _targetHintRoutine;
    private readonly Dictionary<Transform, Coroutine> _progressTapFeedbackRoutines = new Dictionary<Transform, Coroutine>();
    private readonly Dictionary<Transform, Vector3> _progressTapOriginalLocalPositions = new Dictionary<Transform, Vector3>();

    private bool _inputCycleLocked;
    private Coroutine _inputUnlockRoutine;
    private Coroutine _activityVoiceRoutine;

    private float _visibleProgressValue;
    private float _lastValidProgressInputTime;


    private void OnValidate()
    {
        RefreshActivityTemplateData(false);
    }

    public int RefreshActivityTemplateData(bool force)
    {
        int changed = 0;
        if (activities == null)
        {
            activities = new List<ActivityStep>();
            changed++;
        }

        for (int i = 0; i < activities.Count; i++)
            changed += NormalizeActivityStep(activities[i], i);

        if (force || changed > 0 || activityTemplateSchemaVersion != CurrentActivityTemplateSchemaVersion)
        {
            activityTemplateSchemaVersion = CurrentActivityTemplateSchemaVersion;
            changed++;
        }

        return changed;
    }

    private int NormalizeActivityStep(ActivityStep step, int index)
    {
        if (step == null) return 0;
        int changed = 0;

        if (string.IsNullOrWhiteSpace(step.activityName))
        {
            step.activityName = "Activity " + (index + 1);
            changed++;
        }

        step.requiredInputCount = Mathf.Max(1, step.requiredInputCount);
        step.progressRequiredTaps = Mathf.Max(1, step.progressRequiredTaps);
        step.progressAutoFinishAfterNoTapSeconds = Mathf.Max(0f, step.progressAutoFinishAfterNoTapSeconds);
        step.storyMomentRequiredTaps = Mathf.Max(1, step.storyMomentRequiredTaps);
        step.groupRequiredObjectCount = Mathf.Max(1, step.groupRequiredObjectCount);
        step.progressGoDownPercentPerSecond = Mathf.Max(0f, step.progressGoDownPercentPerSecond);
        step.progressMinimumPercent = Mathf.Clamp(step.progressMinimumPercent, 0f, 100f);
        // Progress bar display is optional. Legacy progress flags must never force the visible UI back ON.
        // If the setup person turns Progress Bar OFF, the activity logic still works but the UI stays hidden.
        if (!step.useProgressBar)
        {
            if (step.showTimerProgress) { step.showTimerProgress = false; changed++; }
            if (step.storyMomentShowProgressBar) { step.storyMomentShowProgressBar = false; changed++; }
        }

        // Old auto-start-story timers caused duplicate timing sections and could skip the activity result.
        // The only beginner no-input path is now: show hint -> selected no-input action -> result/continue.
        if (step.progressAutoStartStoryAfterSeconds > 0f) { step.progressAutoStartStoryAfterSeconds = 0f; changed++; }
        if (step.groupAutoStartStoryAfterSeconds > 0f) { step.groupAutoStartStoryAfterSeconds = 0f; changed++; }
        step.progressReactionAnimationSpeed = SafeSpeed(step.progressReactionAnimationSpeed);
        step.resultAnimationSpeed = SafeSpeed(step.resultAnimationSpeed);
        step.helpAnimationSpeed = SafeSpeed(step.helpAnimationSpeed);
        step.progressHelperAnimationSpeed = SafeSpeed(step.progressHelperAnimationSpeed);

        // Template rule: keep no-input behavior user selectable.
        // New activities default to AutoPlayResultThenContinue, but existing setups must not be overwritten here.

        // Template-wide safety: any result action with a 3D object can use the same activity-only transform flow.
        // Keep safe scale defaults so Refresh repairs old empty data without losing assignments.
        if (step.resultActivityScale == Vector3.zero)
        {
            step.resultActivityScale = Vector3.one;
            changed++;
        }

        if (step.targetObjects == null) { step.targetObjects = new List<GameObject>(); changed++; }
        if (step.objectsOnWhenActivityStarts == null) { step.objectsOnWhenActivityStarts = new List<GameObject>(); changed++; }
        if (step.objectsOffWhenActivityStarts == null) { step.objectsOffWhenActivityStarts = new List<GameObject>(); changed++; }
        if (step.objectsOnWhenActivityCompletes == null) { step.objectsOnWhenActivityCompletes = new List<GameObject>(); changed++; }
        if (step.objectsOffWhenActivityCompletes == null) { step.objectsOffWhenActivityCompletes = new List<GameObject>(); changed++; }
        if (step.progressReactionAnimations == null) { step.progressReactionAnimations = new List<AnimationClip>(); changed++; }
        if (step.progressReactionAnimationSounds == null) { step.progressReactionAnimationSounds = new List<AudioClip>(); changed++; }
        if (step.progressReactionAnimationSoundVolumes == null) { step.progressReactionAnimationSoundVolumes = new List<float>(); changed++; }
        while (step.progressReactionAnimationSounds.Count < step.progressReactionAnimations.Count) { step.progressReactionAnimationSounds.Add(null); changed++; }
        while (step.progressReactionAnimationSoundVolumes.Count < step.progressReactionAnimations.Count) { step.progressReactionAnimationSoundVolumes.Add(1f); changed++; }
        for (int sfxIndex = 0; sfxIndex < step.progressReactionAnimationSoundVolumes.Count; sfxIndex++)
        {
            float clampedVolume = Mathf.Clamp(step.progressReactionAnimationSoundVolumes[sfxIndex], 0f, 2f);
            if (!Mathf.Approximately(step.progressReactionAnimationSoundVolumes[sfxIndex], clampedVolume))
            {
                step.progressReactionAnimationSoundVolumes[sfxIndex] = clampedVolume;
                changed++;
            }
        }
        if (step.progressReactionGroupLoopSoundVolume < 0f || step.progressReactionGroupLoopSoundVolume > 2f) { step.progressReactionGroupLoopSoundVolume = Mathf.Clamp(step.progressReactionGroupLoopSoundVolume, 0f, 2f); changed++; }
        if (step.progressHelperAnimations == null) { step.progressHelperAnimations = new List<AnimationClip>(); changed++; }
        if (step.progressHelperAnimationSounds == null) { step.progressHelperAnimationSounds = new List<AudioClip>(); changed++; }
        if (step.progressHelperAnimationSoundVolumes == null) { step.progressHelperAnimationSoundVolumes = new List<float>(); changed++; }
        while (step.progressHelperAnimationSounds.Count < step.progressHelperAnimations.Count) { step.progressHelperAnimationSounds.Add(null); changed++; }
        while (step.progressHelperAnimationSoundVolumes.Count < step.progressHelperAnimations.Count) { step.progressHelperAnimationSoundVolumes.Add(1f); changed++; }
        for (int sfxIndex = 0; sfxIndex < step.progressHelperAnimationSoundVolumes.Count; sfxIndex++)
        {
            float clampedVolume = Mathf.Clamp(step.progressHelperAnimationSoundVolumes[sfxIndex], 0f, 2f);
            if (!Mathf.Approximately(step.progressHelperAnimationSoundVolumes[sfxIndex], clampedVolume))
            {
                step.progressHelperAnimationSoundVolumes[sfxIndex] = clampedVolume;
                changed++;
            }
        }
        if (step.progressMilestones == null) { step.progressMilestones = new List<ActivityProgressMilestone>(); changed++; }
        for (int milestoneIndex = 0; milestoneIndex < step.progressMilestones.Count; milestoneIndex++)
        {
            ActivityProgressMilestone milestone = step.progressMilestones[milestoneIndex];
            if (milestone == null) continue;
            float clampedMilestoneVolume = Mathf.Clamp(milestone.soundVolume, 0f, 2f);
            if (!Mathf.Approximately(milestone.soundVolume, clampedMilestoneVolume))
            {
                milestone.soundVolume = clampedMilestoneVolume;
                changed++;
            }
        }
        if (step.groupLoopSoundVolume < 0f || step.groupLoopSoundVolume > 2f) { step.groupLoopSoundVolume = Mathf.Clamp(step.groupLoopSoundVolume, 0f, 2f); changed++; }
        if (step.progressHelperGroupLoopSoundVolume < 0f || step.progressHelperGroupLoopSoundVolume > 2f) { step.progressHelperGroupLoopSoundVolume = Mathf.Clamp(step.progressHelperGroupLoopSoundVolume, 0f, 2f); changed++; }
        if (step.targetActions == null) { step.targetActions = new List<ActivityTargetAction>(); changed++; }
        if (step.groupTapObjects == null) { step.groupTapObjects = new List<GameObject>(); changed++; }
        if (step.groupRequiredObjects == null) { step.groupRequiredObjects = new List<GameObject>(); changed++; }
        if (step.groupActions == null) { step.groupActions = new List<ActivityGroupAction>(); changed++; }
        if (step.optionTexts == null) { step.optionTexts = new List<string>(); changed++; }
        if (step.reactions == null) { step.reactions = new List<ActivityReaction>(); changed++; }
        if (step.choiceOptions == null) { step.choiceOptions = new List<ActivityChoiceOption>(); changed++; }

        changed += NormalizeChoiceSetup(step);
        changed += NormalizeScenarioActions(step);
        changed += NormalizeTargetActions(step);
        changed += NormalizeReactions(step);

        return changed;
    }

    private int NormalizeChoiceSetup(ActivityStep step)
    {
        int changed = 0;
        bool isChoice = step.childInput == ActivityInputKind.ChooseOption || step.childInput == ActivityInputKind.AnswerQuestion;
        if (!isChoice) return changed;

        if (!step.choiceHideUiWhileResultPlays)
        {
            step.choiceHideUiWhileResultPlays = true;
            changed++;
        }
        if (!step.choiceBlockInputWhileResultPlays)
        {
            step.choiceBlockInputWhileResultPlays = true;
            changed++;
        }
        if (!step.choiceReturnQuestionAfterWrong)
        {
            step.choiceReturnQuestionAfterWrong = true;
            changed++;
        }
        if (!step.pauseStoryWhileActivity)
        {
            step.pauseStoryWhileActivity = true;
            changed++;
        }
        if (step.choiceWrongOptionBehaviour != ActivityChoiceWrongOptionBehaviour.DisableAndGrayOut)
        {
            step.choiceWrongOptionBehaviour = ActivityChoiceWrongOptionBehaviour.DisableAndGrayOut;
            changed++;
        }

        if (step.choiceOptions.Count == 0)
        {
            if (step.optionTexts == null || step.optionTexts.Count < 2)
            {
                step.optionTexts = new List<string> { "Option 1", "Option 2" };
                changed++;
            }

            int count = Mathf.Clamp(step.optionTexts.Count, 2, 5);
            for (int i = 0; i < count; i++)
            {
                step.choiceOptions.Add(new ActivityChoiceOption
                {
                    buttonText = string.IsNullOrWhiteSpace(step.optionTexts[i]) ? "Option " + (i + 1) : step.optionTexts[i],
                    isCorrect = i == Mathf.Clamp(step.correctOptionIndex, 0, count - 1),
                    playResultForThisOption = i != Mathf.Clamp(step.correctOptionIndex, 0, count - 1)
                });
            }
            changed++;
        }

        if (step.choiceOptions.Count < 2)
        {
            while (step.choiceOptions.Count < 2)
            {
                step.choiceOptions.Add(new ActivityChoiceOption { buttonText = "Option " + (step.choiceOptions.Count + 1) });
                changed++;
            }
        }

        int correct = Mathf.Clamp(step.correctOptionIndex, 0, Mathf.Max(0, step.choiceOptions.Count - 1));
        for (int i = 0; i < step.choiceOptions.Count; i++)
        {
            ActivityChoiceOption option = step.choiceOptions[i];
            if (option == null)
            {
                option = new ActivityChoiceOption();
                step.choiceOptions[i] = option;
                changed++;
            }

            if (string.IsNullOrWhiteSpace(option.buttonText))
            {
                option.buttonText = "Option " + (i + 1);
                changed++;
            }

            if (option.isCorrect)
                correct = i;
        }

        if (step.correctOptionIndex != correct)
        {
            step.correctOptionIndex = correct;
            changed++;
        }

        for (int i = 0; i < step.choiceOptions.Count; i++)
        {
            bool shouldBeCorrect = i == correct;
            if (step.choiceOptions[i].isCorrect != shouldBeCorrect)
            {
                step.choiceOptions[i].isCorrect = shouldBeCorrect;
                changed++;
            }
        }

        if (step.choiceOptions.Count > 5)
            Debug.LogWarning($"[Activity Template] {name}/{step.activityName}: Choose Correct Option has more than 5 options. The UI supports 2 to 5. Reduce option count in Inspector.", this);

        return changed;
    }

    private int NormalizeScenarioActions(ActivityStep step)
    {
        int changed = 0;
        if (step.choiceOptions == null) return changed;

        foreach (ActivityChoiceOption option in step.choiceOptions)
        {
            if (option == null) continue;
            if (option.scenarioActions == null)
            {
                option.scenarioActions = new List<ActivityScenarioAction>();
                changed++;
            }

            option.scenarioRepeatCount = Mathf.Max(1, option.scenarioRepeatCount);
            option.animationSpeed = SafeSpeed(option.animationSpeed);

            bool hasLegacy = option.animator != null || option.animationClip != null || option.soundEffect != null || option.voiceOver != null || option.narration != null || option.onSelected != null;
            if (option.scenarioActions.Count == 0 && hasLegacy)
            {
                ActivityScenarioAction migrated = new ActivityScenarioAction
                {
                    enabled = true,
                    actionName = "Migrated Single Result",
                    animator = option.animator,
                    animationClip = option.animationClip,
                    animationSpeed = SafeSpeed(option.animationSpeed),
                    animationLoopCount = 1,
                    waitForAnimation = option.waitForAnimation,
                    soundEffect = option.soundEffect,
                    soundVolume = option.soundVolume,
                    voiceOver = option.voiceOver,
                    voiceVolume = option.voiceVolume,
                    waitForVoiceOver = option.waitForVoiceOver,
                    narration = option.narration,
                    narrationVolume = option.narrationVolume,
                    waitForNarration = option.waitForNarration,
                    extraWaitSeconds = option.extraWaitSeconds,
                    // Do not migrate wrong-option custom events into scenario actions.
                    // Wrong options must never continue the story through an old UnityEvent.
                    onActionPlayed = option.isCorrect ? option.onSelected : null
                };
                option.scenarioActions.Add(migrated);
                changed++;
            }

            foreach (ActivityScenarioAction action in option.scenarioActions)
            {
                if (action == null) continue;
                if (string.IsNullOrWhiteSpace(action.actionName))
                {
                    action.actionName = "Scenario Action";
                    changed++;
                }
                action.animationSpeed = SafeSpeed(action.animationSpeed);
                action.animationLoopCount = Mathf.Max(1, action.animationLoopCount);
                action.soundVolume = Mathf.Clamp(action.soundVolume, 0f, 2f);
                action.voiceVolume = Mathf.Clamp(action.voiceVolume, 0f, 2f);
                action.narrationVolume = Mathf.Clamp(action.narrationVolume, 0f, 2f);
                if (action.activityScale == Vector3.zero)
                {
                    action.activityScale = Vector3.one;
                    changed++;
                }
                if (action.objectsToTurnOn == null) { action.objectsToTurnOn = new List<GameObject>(); changed++; }
                if (action.objectsToTurnOff == null) { action.objectsToTurnOff = new List<GameObject>(); changed++; }
            }
        }

        return changed;
    }

    private int NormalizeTargetActions(ActivityStep step)
    {
        int changed = 0;
        if (step.targetActions != null)
        {
            foreach (ActivityTargetAction action in step.targetActions)
            {
                if (action == null) continue;
                if (string.IsNullOrWhiteSpace(action.actionName)) { action.actionName = "Target Action"; changed++; }
                action.animationSpeed = SafeSpeed(action.animationSpeed);
                if (action.activityScale == Vector3.zero)
                {
                    action.activityScale = Vector3.one;
                    changed++;
                }
                if (action.objectsToTurnOn == null) { action.objectsToTurnOn = new List<GameObject>(); changed++; }
                if (action.objectsToTurnOff == null) { action.objectsToTurnOff = new List<GameObject>(); changed++; }
            }
        }
        if (step.groupActions != null)
        {
            foreach (ActivityGroupAction action in step.groupActions)
            {
                if (action == null) continue;
                if (string.IsNullOrWhiteSpace(action.actionName)) { action.actionName = "Group Action Item"; changed++; }
                action.animationSpeed = SafeSpeed(action.animationSpeed);
                if (action.activityScale == Vector3.zero)
                {
                    action.activityScale = Vector3.one;
                    changed++;
                }
            }
        }
        return changed;
    }

    private int NormalizeReactions(ActivityStep step)
    {
        int changed = 0;
        if (step.reactions == null) return changed;
        foreach (ActivityReaction reaction in step.reactions)
        {
            if (reaction == null) continue;
            if (string.IsNullOrWhiteSpace(reaction.reactionName)) { reaction.reactionName = "Reaction"; changed++; }
            reaction.animationSpeed = SafeSpeed(reaction.animationSpeed);
            reaction.sfxVolume = Mathf.Clamp(reaction.sfxVolume, 0f, 2f);
            reaction.mainAudioVolume = Mathf.Clamp(reaction.mainAudioVolume, 0f, 2f);
            reaction.reactionVoiceVolume = Mathf.Clamp(reaction.reactionVoiceVolume, 0f, 2f);
            if (reaction.vfxObjects == null) { reaction.vfxObjects = new List<GameObject>(); changed++; }
            if (reaction.objects == null) { reaction.objects = new List<GameObject>(); changed++; }
            if (reaction.animationClips == null) { reaction.animationClips = new List<AnimationClip>(); changed++; }
            if (reaction.activityScale == Vector3.zero)
            {
                reaction.activityScale = Vector3.one;
                changed++;
            }
            reaction.objectBurstCount = Mathf.Max(1, reaction.objectBurstCount);
            reaction.objectLifeSeconds = Mathf.Max(0.05f, reaction.objectLifeSeconds);
            reaction.fallDurationSeconds = Mathf.Max(0.05f, reaction.fallDurationSeconds);
            reaction.fallDistance = Mathf.Max(0f, reaction.fallDistance);
            reaction.fadeOutSeconds = Mathf.Max(0f, reaction.fadeOutSeconds);
            reaction.pageSpreadSize.x = Mathf.Max(0f, reaction.pageSpreadSize.x);
            reaction.pageSpreadSize.y = Mathf.Max(0f, reaction.pageSpreadSize.y);
            reaction.rectangleSpawnAreaSize.x = Mathf.Max(0.01f, Mathf.Abs(reaction.rectangleSpawnAreaSize.x));
            reaction.rectangleSpawnAreaSize.y = Mathf.Max(0.01f, Mathf.Abs(reaction.rectangleSpawnAreaSize.y));
            reaction.rectangleSpawnAreaSize.z = Mathf.Max(0.01f, Mathf.Abs(reaction.rectangleSpawnAreaSize.z));

            if (reaction.type == ActivityReactionType.VisualEffect && reaction.make3DObjectsFall)
            {
                if (reaction.visualEffectPlayMode != VisualEffectPlayMode.AddNewEachInput)
                {
                    reaction.visualEffectPlayMode = VisualEffectPlayMode.AddNewEachInput;
                    changed++;
                }
            }
        }
        return changed;
    }

    private static float SafeSpeed(float value)
    {
        return Mathf.Approximately(value, 0f) ? 1f : Mathf.Abs(value);
    }

    private void Awake()
    {
        if (activityPanel == null)
            activityPanel = FindFirstObjectByType<ActivityPanel>();

        if (activityPanel == null && ARInteractionUI.Instance != null)
            activityPanel = ARInteractionUI.Instance;

        if (defaultRaycastCamera == null)
            defaultRaycastCamera = Camera.main;

        if (defaultAudioSource == null)
            defaultAudioSource = GetComponent<AudioSource>();

        // Hard safety: editor preview or previous activity poses must never leak into story, VFX, or popup.
        // Restore story pose before any reveal/story system can use the objects.
        // Clear saved story poses first so replay never restores a stale position from a prior session.
        ClearAllSavedStoryPoses();
        RestoreAllActivityActionTransforms();
        PrepareAllVisualEffectSources();

        activityPanel?.ResetPanel();
        StopAllConfiguredVisualEffects(clear: true);
    }

    private void Start()
    {
        // Second safety pass after Unity has enabled scene objects. Story/VFX must start from story pose.
        RestoreAllActivityActionTransforms();
        PrepareAllVisualEffectSources();
        StopAllConfiguredVisualEffects(clear: true);
        StartCoroutine(StopConfiguredVisualEffectsNextFrame());
    }

    private IEnumerator StopConfiguredVisualEffectsNextFrame()
    {
        yield return null;
        StopAllConfiguredVisualEffects(clear: true);
    }

    private void OnEnable()
    {
        StoryActivityInputRouter.OnInput += HandleInput;
    }

    private void OnDisable()
    {
        StoryActivityInputRouter.OnInput -= HandleInput;
        ClearInput();
        StopAllReactionRoutines();
        StopAllReactionAudio();
        StopActivityAudio();
        StopAllGraphs();
        StopAllChoiceScenarioRoutines();
        StopAllChoiceGraphs();
        RestoreActivityAnimationAnimatorSpeeds();
        ClearSpawnedVfxObjects();
        StopTargetHintVisuals();
        StopProgressTapFeedbacks();
        RestoreMaterialColors();
        RestoreTargetScales();
        RestoreAllActivityActionTransforms();
        ClearActiveChoiceRestoreState();
        SetAnyActivityRunning(false);
    }

    public void SetCompletionCallback(Action callback)
    {
        _completionCallback = callback;
    }

    /// <summary>
    /// Existing app calls this when the story section completes.
    /// This is also used as a safety fallback. If the story has ended, reveal and voice-start
    /// moments must already have happened, so waiting activities should not get stuck forever.
    /// </summary>
    public void PlayContent()
    {
        _storyEndReached = true;
        _revealCompleteReached = true;
        _voiceStartedReached = true;
        _contentCompletionRequested = true;

        if (activities == null || activities.Count == 0)
        {
            if (completeImmediatelyWhenNoActivities)
                CompleteToStoryFlow();
            return;
        }

        EnsureFlowRunning();
    }


    /// <summary>
    /// Used by ARTrackedPageNode after VFX reveal completes.
    /// If the first pending activity starts After Reveal Finishes, it should run before the normal
    /// story animation, spline, and voice over start.
    /// </summary>
    public bool ShouldRunBeforeStoryAfterReveal()
    {
        if (activities == null || activities.Count == 0) return false;

        for (int i = Mathf.Clamp(_currentIndex, 0, activities.Count); i < activities.Count; i++)
        {
            ActivityStep step = activities[i];
            if (step == null || !step.enabled) continue;

            return step.startWhen == ActivityStartRule.AfterRevealFinishes;
        }

        return false;
    }

    /// <summary>
    /// Starts reveal-gated activities and calls onCompleted when they finish.
    /// This lets the AR story wait at the end of VFX reveal until the child completes or skips the activity.
    /// </summary>
    public void RunBeforeStoryAfterReveal(Action onCompleted)
    {
        SetCompletionCallback(onCompleted);
        _revealCompleteReached = true;
        _contentCompletionRequested = true;
        EnsureFlowRunning();
    }

    public void PauseContent()
    {
        // Tracking lost pause. Do not ClearInput here.
        // Clearing input/buttons destroys active scenario option handlers, so when the marker returns
        // the question text can appear without option buttons. Keep runtime state and only hide UI.
        activityPanel?.HideAllActivityUIForScenario();
    }

    public void ReplayContent()
    {
        ResetInteractions();
        if (restartImmediatelyOnReplay)
            PlayContent();
    }

    public void ResetInteractions()
    {
        if (_flowRoutine != null)
        {
            StopCoroutine(_flowRoutine);
            _flowRoutine = null;
        }

        if (_activityVoiceRoutine != null)
        {
            StopCoroutine(_activityVoiceRoutine);
            _activityVoiceRoutine = null;
        }

        _currentIndex = 0;
        _storyEndReached = false;
        _revealCompleteReached = false;
        _voiceStartedReached = false;
        _contentCompletionRequested = false;
        _waitingForManualContinue = false;
        _triggerKeys.Clear();

        _acceptedInputCount = 0;
        _sequenceIndex = 0;
        _choiceIndex = -1;
        _choiceAnswered = false;
        _choiceCorrect = false;
        _uniqueTappedGroupObjects.Clear();
        _lastAcceptedInput = default(ActivityInputData);

        _reactionTriggerCounts.Clear();
        _lastReactionTriggerTime.Clear();
        _lastReactionSfxTime.Clear();

        ClearInput();
        UnlockInputCycle();
        StopAllReactionRoutines();
        StopAllReactionAudio();
        StopActivityAudio();
        StopAllGraphs();
        StopAllChoiceScenarioRoutines();
        StopAllChoiceGraphs();
        RestoreActivityAnimationAnimatorSpeeds();
        ClearSpawnedVfxObjects();
        StopTargetHintVisuals();
        RestoreMaterialColors();
        RestoreTargetScales();
        RestoreAllObjectActiveStates();
        ClearAllSavedStoryPoses();
        RestoreAllActivityActionTransforms();
        PrepareAllVisualEffectSources();
        StopAllConfiguredVisualEffects(clear: true);
        HideActivityUI();
        ClearActiveChoiceRestoreState();
        SetAnyActivityRunning(false);
        // Reset milestone states so replay starts fresh
        ResetAllMilestoneStates();
        // Reset SFX gap tracking
        StopProgressHelperGroupLoopSound();
        StopProgressReactionGroupLoopSound();
        _lastCorrectTapSoundTime = -999f;
        _lastHelperClipSoundIndex = -1;
        _lastHelperClipSoundTime = -999f;
        _lastReactionClipSoundIndex = -1;
        _lastReactionClipSoundTime = -999f;
        // Stop any active milestone text coroutine
        if (_activeMilestoneTextCoroutine != null)
        {
            StopCoroutine(_activeMilestoneTextCoroutine);
            _activeMilestoneTextCoroutine = null;
        }
        onActivitiesReset?.Invoke();
    }

    public void NotifyRevealComplete()
    {
        _revealCompleteReached = true;
        // Re-hide all VFX source objects after reveal completes.
        // The reveal sequence may re-enable parent objects that contain petal or VFX sources.
        // This ensures sources stay hidden even if their parent was activated by the reveal system.
        PrepareAllVisualEffectSources();
        EnsureFlowRunning();
    }

    public void NotifyVoiceOverStarted()
    {
        _voiceStartedReached = true;
        EnsureFlowRunning();
    }

    public void NotifyStoryEnd()
    {
        PlayContent();
    }

    public void TriggerActivity(string key)
    {
        AddTriggerKey(key);
        EnsureFlowRunning();
    }

    public void TriggerAnimationEvent(string key)
    {
        AddTriggerKey(key);
        EnsureFlowRunning();
    }

    public void ContinueAfterActivity()
    {
        _waitingForManualContinue = false;
    }

    private void AddTriggerKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        _triggerKeys.Add(key.Trim());
    }

    private bool HasTriggerKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        return _triggerKeys.Contains(key.Trim());
    }

    private void EnsureFlowRunning()
    {
        if (!isActiveAndEnabled) return;
        if (_flowRoutine != null) return;
        if (activities == null || activities.Count == 0) return;
        _flowRoutine = StartCoroutine(RunActivityFlow());
    }

    private IEnumerator RunActivityFlow()
    {
        onActivitiesStarted?.Invoke();

        for (; _currentIndex < activities.Count; _currentIndex++)
        {
            ActivityStep step = activities[_currentIndex];
            if (step == null || !step.enabled)
                continue;

            yield return WaitForStart(step);
            yield return RunActivity(step);

            if (!step.continueAfterComplete)
            {
                _waitingForManualContinue = true;
                while (_waitingForManualContinue)
                    yield return null;
            }
        }

        _flowRoutine = null;
        ClearInput();
        StopAllReactionAudio();
        StopActivityAudio();
        ClearSpawnedVfxObjects();
        StopTargetHintVisuals();
        RestoreMaterialColors();
        RestoreTargetScales();
        RestoreAllActivityActionTransforms();
        HideActivityUI();
        SetAnyActivityRunning(false);
        onActivitiesCompleted?.Invoke();

        if (_contentCompletionRequested)
            CompleteToStoryFlow();
    }

    private IEnumerator WaitForStart(ActivityStep step)
    {
        switch (step.startWhen)
        {
            case ActivityStartRule.AfterStoryEnds:
                while (!_storyEndReached) yield return null;
                break;
            case ActivityStartRule.AfterRevealFinishes:
                while (!_revealCompleteReached) yield return null;
                break;
            case ActivityStartRule.AfterVoiceStarts:
                while (!_voiceStartedReached) yield return null;
                break;
            case ActivityStartRule.AfterWaitingTime:
                break;
            case ActivityStartRule.AfterPreviousActivity:
                break;
            case ActivityStartRule.FromAnimationEvent:
            case ActivityStartRule.ManualStart:
                while (!HasTriggerKey(step.startKey)) yield return null;
                break;
            case ActivityStartRule.AfterStoryObjectFinishes:
                yield return WaitForStoryObjectToFinish(step);
                break;
        }

        if (step.waitBeforeStart > 0f)
            yield return new WaitForSeconds(step.waitBeforeStart);
    }

    private IEnumerator RunActivity(ActivityStep step)
    {
        PrepareActivityForStart(step);
        PauseStoryForActivityIfNeeded(step);
        SetAnyActivityRunning(true);

        // Activity-only transform starts here, not during story, VFX, or popup.
        // This is the first moment where the child activity is actually ON.
        ApplyActivityOnlyTransformsAtActivityStart(step);

        bool showProgressForThisActivity = StepUsesProgress(step);
        if (!showProgressForThisActivity)
            activityPanel?.HideProgress();

        activityPanel?.BeginActivity(step.instructionText, showProgressForThisActivity, StepUsesButtons(step));

        if (!showProgressForThisActivity)
            activityPanel?.HideProgress();

        ShowProgressIfResultMode(step, 0f, "Ready");

        _acceptedInputCount = 0;
        _sequenceIndex = 0;
        _choiceIndex = -1;
        _choiceAnswered = false;
        _choiceCorrect = false;
        _reactionTriggerCounts.Clear();
        _lastReactionTriggerTime.Clear();
        StopAllChoiceScenarioRoutines();
        StopAllChoiceGraphs();
        _lastReactionSfxTime.Clear();

        step.onActivityStarted?.Invoke();
        StartActivityAudio(step);
        yield return PlayActivityVoice(step);
        RunReactions(step, ActivityReactionMoment.WhenActivityStarts);

        switch (step.childInput)
        {
            case ActivityInputKind.WaitOnly:
                yield return RunWaitOnly(step);
                break;
            case ActivityInputKind.HelpAction:
                yield return RunHelpAction(step);
                break;
            case ActivityInputKind.ProgressGate:
            case ActivityInputKind.TapObjectAndReact:
                // Compatibility: older Activity 5 editor builds used TapObjectAndReact as a Progress Gate variant.
                // Keeping it here prevents enum mismatch errors and keeps existing pages from breaking.
                yield return RunProgressGate(step);
                break;
            case ActivityInputKind.GroupAction:
                yield return RunGroupAction(step);
                break;
            case ActivityInputKind.WaitForStoryThenTapObject:
                yield return RunWaitForStoryThenTapObject(step);
                break;
            case ActivityInputKind.ChooseOption:
            case ActivityInputKind.AnswerQuestion:
                yield return RunChoiceActivity(step);
                break;
            default:
                yield return RunInputActivity(step);
                break;
        }

        ClearInput();
        activityPanel?.HideButtons();
        if (StepUsesProgress(step)) activityPanel?.HideProgress();

        // Stop any active milestone text coroutine before result plays
        if (_activeMilestoneTextCoroutine != null)
        {
            StopCoroutine(_activeMilestoneTextCoroutine);
            _activeMilestoneTextCoroutine = null;
        }

        // Play activity complete sound before result actions
        PlayActivityCompleteSound(step);

        RunReactions(step, ActivityReactionMoment.WhenActivityCompletes);
        if (step.waitForRunningReactionsBeforeFinish)
            yield return WaitForRunningReactions(step);

        if (!string.IsNullOrWhiteSpace(step.successMessage))
            yield return ShowFeedback(step.successMessage, step.successMessageSeconds);

        StopActivityAudio(step);
        StopTargetHintVisuals();
        RestoreMaterialColors();
        RestoreTargetScales();
        RestoreAllActivityActionTransforms();
        RestoreActivityAnimationAnimatorSpeeds();
        ApplyObjectStateList(step.objectsOnWhenActivityCompletes, true);
        ApplyObjectStateList(step.objectsOffWhenActivityCompletes, false);
        UnlockInputCycle();
        HideActivityUI();
        SetAnyActivityRunning(false);
        ResumeStoryAfterActivityIfNeeded();
        step.onActivityCompleted?.Invoke();
    }

    private void PrepareActivityForStart(ActivityStep step)
    {
        RestoreAllActivityActionTransforms();
        StopConfiguredVisualEffects(step, clear: true);
        PrepareVisualEffectSourcesForActivity(step);
        RecordActivityObjectStates(step);
        ApplyObjectStateList(step.objectsOnWhenActivityStarts, true);
        ApplyObjectStateList(step.objectsOffWhenActivityStarts, false);
        // Reset SFX gap tracking so each new activity starts clean
        _lastCorrectTapSoundTime = -999f;
        _lastHelperClipSoundIndex = -1;
        _lastHelperClipSoundTime = -999f;
        // Play the activity start sound here (before voice and before ambient loop).
        // Ambient loop (activityDurationAudio) is started in RunActivity after voice setup
        // at the correct point in the flow — do NOT call StartActivityAudio here.
        PlayActivityStartSound(step);
    }


    private IEnumerator WaitForStoryObjectToFinish(ActivityStep step)
    {
        if (step == null) yield break;

        float warningAfterSeconds = Mathf.Max(0f, step.storyWaitTimeoutSeconds);
        float startTime = Time.time;
        bool warningShown = false;

        Animator animator = ResolveStoryWaitAnimator(step);
        MonoBehaviour movementComponent = ResolveStoryWaitComponent(step);

        // Activity 6 rule:
        // This is a strict story moment watcher. It must NOT start the activity just because
        // the selected clip started, and it must NOT use a timeout to start early.
        // It waits for the selected clip/state to be seen, then waits until that selected
        // clip/state is finished or the Animator leaves it.
        if (animator != null)
        {
            AnimationClip clipToWaitFor = step.storyWaitAnimationClip;
            string stateNameToWaitFor = clipToWaitFor == null ? step.storyWaitAnimationStateName : string.Empty;
            bool waitForSpecificAnimation = clipToWaitFor != null || !string.IsNullOrWhiteSpace(stateNameToWaitFor);

            bool selectedHasStarted = !waitForSpecificAnimation;
            float selectedStartedAt = 0f;
            string selectedName = clipToWaitFor != null ? clipToWaitFor.name : stateNameToWaitFor;

            while (animator != null && animator.gameObject.activeInHierarchy)
            {
                if (warningAfterSeconds > 0f && !warningShown && Time.time - startTime >= warningAfterSeconds)
                {
                    warningShown = true;
                    Debug.LogWarning($"[Activity] Still waiting for selected story animation '{selectedName}' on '{animator.name}'. Activity will NOT start early. Check the Animator and selected clip if this never continues.");
                }

                AnimatorStateInfo info = animator.GetCurrentAnimatorStateInfo(0);
                bool inTransition = animator.IsInTransition(0);
                bool selectedIsPlayingNow = !waitForSpecificAnimation || IsAnimatorPlayingSelectedAnimation(animator, info, clipToWaitFor, stateNameToWaitFor);

                if (selectedIsPlayingNow && !selectedHasStarted && animator.speed > 0.001f)
                {
                    selectedHasStarted = true;
                    selectedStartedAt = Time.time;
                    Debug.Log($"[Activity] Selected story animation started: {selectedName}");
                }

                if (selectedHasStarted)
                {
                    if (waitForSpecificAnimation)
                    {
                        // If the Animator has moved to another state/clip, the selected story moment is complete.
                        // This supports future cases such as: wait until animation 2, 4, or 8 finishes.
                        if (!selectedIsPlayingNow)
                        {
                            Debug.Log("[Activity] Selected story animation finished because Animator moved to the next clip/state.");
                            break;
                        }

                        float animatorSpeed = Mathf.Max(0.01f, Mathf.Abs(animator.speed));
                        float expectedClipSeconds = clipToWaitFor != null ? Mathf.Max(0.05f, clipToWaitFor.length / animatorSpeed) : 0.05f;
                        float observedSeconds = Time.time - selectedStartedAt;

                        // If the Animator begins transitioning out after the selected clip has nearly played,
                        // start the activity before the next story animation has time to run visibly.
                        if (inTransition && observedSeconds >= expectedClipSeconds * 0.99f)
                        {
                            Debug.Log("[Activity] Selected story animation finished during transition to the next state.");
                            break;
                        }

                        // If a non-looping clip stays on the last frame, finish by normalized time.
                        bool canFinishByNormalizedTime = clipToWaitFor != null && !clipToWaitFor.isLooping && !info.loop;
                        if (canFinishByNormalizedTime && info.normalizedTime >= 0.99f && observedSeconds >= expectedClipSeconds * 0.99f)
                        {
                            Debug.Log("[Activity] Selected story animation finished by normalized time.");
                            break;
                        }
                    }
                    else
                    {
                        if (!inTransition && !info.loop && info.normalizedTime >= 0.99f)
                            break;
                    }
                }

                yield return null;
            }
        }
        else if (movementComponent != null)
        {
            while (movementComponent != null)
            {
                if (warningAfterSeconds > 0f && !warningShown && Time.time - startTime >= warningAfterSeconds)
                {
                    warningShown = true;
                    Debug.LogWarning($"[Activity] Still waiting for story movement '{movementComponent.name}'. Activity will NOT start early.");
                }

                bool hasFinishedValue = TryReadBoolMember(movementComponent, "IsFinished", out bool isFinished);
                bool hasPlayingValue = TryReadBoolMember(movementComponent, "IsPlaying", out bool isPlaying);

                if (hasFinishedValue && isFinished)
                    break;

                if (!hasFinishedValue && hasPlayingValue && !isPlaying)
                    break;

                yield return null;
            }
        }
    }


    private static bool IsAnimatorPlayingSelectedAnimation(Animator animator, AnimatorStateInfo info, AnimationClip clipToWaitFor, string stateNameToWaitFor)
    {
        if (animator == null) return false;

        if (!string.IsNullOrWhiteSpace(stateNameToWaitFor))
        {
            string stateName = stateNameToWaitFor.Trim();
            if (info.IsName(stateName)) return true;
            if (info.shortNameHash == Animator.StringToHash(stateName)) return true;
        }

        if (clipToWaitFor == null) return false;

        try
        {
            AnimatorClipInfo[] clips = animator.GetCurrentAnimatorClipInfo(0);
            for (int i = 0; i < clips.Length; i++)
            {
                AnimationClip current = clips[i].clip;
                if (current == null) continue;

                if (current == clipToWaitFor) return true;
                if (string.Equals(current.name, clipToWaitFor.name, StringComparison.Ordinal)) return true;
            }
        }
        catch
        {
            // Some Animator states may not expose clip info in editor preview. Fall through to state-name fallback.
        }

        string clipName = clipToWaitFor.name;
        if (!string.IsNullOrWhiteSpace(clipName))
        {
            if (info.IsName(clipName)) return true;
            if (info.shortNameHash == Animator.StringToHash(clipName)) return true;
        }

        return false;
    }

    private static Animator ResolveStoryWaitAnimator(ActivityStep step)
    {
        if (step == null) return null;
        if (step.storyWaitAnimator != null) return step.storyWaitAnimator;

        UnityEngine.Object watch = step.storyWaitObjectOrAnimator;
        if (watch == null) return null;

        if (watch is Animator directAnimator)
            return directAnimator;

        if (watch is GameObject go)
        {
            Animator a = go.GetComponent<Animator>();
            if (a != null) return a;
            return go.GetComponentInChildren<Animator>(true);
        }

        if (watch is Component component)
        {
            Animator a = component.GetComponent<Animator>();
            if (a != null) return a;
            return component.GetComponentInChildren<Animator>(true);
        }

        return null;
    }

    private static MonoBehaviour ResolveStoryWaitComponent(ActivityStep step)
    {
        if (step == null) return null;
        if (step.storyWaitComponent != null) return step.storyWaitComponent;

        UnityEngine.Object watch = step.storyWaitObjectOrAnimator;
        if (watch == null) return null;

        if (watch is MonoBehaviour mono)
            return mono;

        if (watch is GameObject go)
            return go.GetComponent<MonoBehaviour>();

        if (watch is Component component)
            return component as MonoBehaviour;

        return null;
    }

    // Resolving GetProperty/GetField by name is the expensive part of reflection --
    // this loop runs every frame for the duration of a story wait, so caching the
    // resolved member per (type, name) means only the first frame ever pays that
    // cost; every later frame (and every later wait, for any component of the same
    // type) just reuses it.
    private static readonly Dictionary<(Type, string), MemberInfo> _boolMemberCache = new();

    private static bool TryReadBoolMember(MonoBehaviour component, string memberName, out bool value)
    {
        value = false;
        if (component == null) return false;

        Type type = component.GetType();
        var key = (type, memberName);

        if (!_boolMemberCache.TryGetValue(key, out MemberInfo member))
        {
            member = (MemberInfo)type.GetProperty(memberName) ?? type.GetField(memberName);
            _boolMemberCache[key] = member;
        }

        switch (member)
        {
            case PropertyInfo property when property.PropertyType == typeof(bool):
                value = (bool)property.GetValue(component, null);
                return true;
            case FieldInfo field when field.FieldType == typeof(bool):
                value = (bool)field.GetValue(component);
                return true;
            default:
                return false;
        }
    }

    private void PauseStoryForActivityIfNeeded(ActivityStep step)
    {
        if (step == null) return;

        // Choice/scenario activities can happen in the middle of the story.
        // They must always hold the story until the correct option finishes.
        bool isChoiceScenario = step.childInput == ActivityInputKind.ChooseOption ||
                                step.childInput == ActivityInputKind.AnswerQuestion;

        bool mustPauseStory = step.pauseStoryWhileActivity ||
                              step.childInput == ActivityInputKind.WaitForStoryThenTapObject ||
                              isChoiceScenario;

        if (!mustPauseStory) return;
        if (_storyPausedForActivity) return;

        _pausedStoryNodeForActivity = GetComponentInParent<ARTrackedPageNode>();
        if (_pausedStoryNodeForActivity == null) return;

        _storyPausedForActivity = true;
        _pausedStoryNodeForActivity.PauseStoryForActivity();
    }

    private void ResumeStoryAfterActivityIfNeeded()
    {
        if (!_storyPausedForActivity) return;

        ARTrackedPageNode node = _pausedStoryNodeForActivity;
        _pausedStoryNodeForActivity = null;
        _storyPausedForActivity = false;

        if (node != null && node.gameObject.activeInHierarchy)
            node.ResumeStoryFromActivity();
    }

    private IEnumerator RunWaitOnly(ActivityStep step)
    {
        float wait = Mathf.Max(0f, step.waitOnlySeconds);
        if (wait > 0f)
            yield return new WaitForSeconds(wait);
    }

    /// <summary>
    /// Help Action is for story-assist moments.
    /// Example: child taps a monkey to help it pull. Progress increases while tapping,
    /// drops when the child stops, and the story still continues after the chosen time.
    /// </summary>
    private IEnumerator RunHelpAction(ActivityStep step)
    {
        bool complete = false;
        float startedAt = Time.time;
        float progress = 0f;
        bool noInputHintShown = false;
        float noInputHintShownAt = 0f;

        PlayableGraph helpGraph = default(PlayableGraph);
        AnimationClipPlayable helpPlayable = default(AnimationClipPlayable);
        bool hasHelpAnimation = CreateActivityAnimationGraph(step.helpAnimator, step.helpAnimationClip, 0f, out helpGraph, out helpPlayable);
        if (hasHelpAnimation && helpGraph.IsValid())
        {
            helpPlayable.SetTime(0f);
            helpPlayable.SetSpeed(0f);
            helpGraph.Evaluate(0f);
        }

        if (ProgressBarFollowsInput(step)) activityPanel?.ShowProgress(0f, "0%");

        BeginInput(step, data => IsInputValidForStep(step, data), data =>
        {
            _acceptedInputCount++;
            progress = Mathf.Clamp01(progress + Mathf.Max(0f, step.helpProgressGainPerTap) / 100f);

            if (step.helpTapSound != null)
                CreateTempAudioSource(step.helpTapSound, step.helpTapSoundVolume, false);
            else
                TryPlayCorrectTapSound(step); // shared fallback if no dedicated tap sound

            RunReactions(step, ActivityReactionMoment.EveryValidInput);
            RunReactions(step, ActivityReactionMoment.IfReactionIsFree);
        }, data =>
        {
            HandleProgressGateWrongInput(step, data);
        });

        while (!complete)
        {
            float elapsed = Time.time - startedAt;

            if (progress > 0f && step.helpProgressLossPerSecond > 0f)
                progress = Mathf.Max(0f, progress - (step.helpProgressLossPerSecond / 100f) * Time.deltaTime);

            if (ProgressBarFollowsInput(step)) activityPanel?.ShowProgress(progress, Mathf.RoundToInt(progress * 100f) + "%");

            if (hasHelpAnimation && helpGraph.IsValid())
            {
                if (progress > 0.001f)
                {
                    helpPlayable.SetSpeed(Mathf.Max(0.01f, step.helpAnimationSpeed));
                }
                else
                {
                    helpPlayable.SetSpeed(0f);
                    if (step.helpResetAnimationWhenProgressIsEmpty)
                    {
                        helpPlayable.SetTime(0f);
                        helpGraph.Evaluate(0f);
                    }
                }
            }

            if (step.helpCompleteWhenProgressFull && progress >= 0.999f)
            {
                complete = true;
                break;
            }

            if (step.helpAutoContinueAfterSeconds > 0f && elapsed >= step.helpAutoContinueAfterSeconds)
            {
                complete = true;
                break;
            }

            if (_acceptedInputCount == 0 && step.enableNoInputHelp && step.noInputHintAfterSeconds > 0f && !noInputHintShown)
            {
                if (elapsed >= step.noInputHintAfterSeconds)
                {
                    noInputHintShown = true;
                    noInputHintShownAt = Time.time;
                    ShowNoInputHint(step);
                }
            }

            if (_acceptedInputCount == 0 && noInputHintShown && (ShouldAutoPlayResultAfterNoInput(step) || ShouldSkipActivityAfterNoInput(step)))
            {
                float skipWait = Mathf.Max(0f, step.autoSkipAfterHintSeconds);
                if (Time.time - noInputHintShownAt >= skipWait)
                {
                    if (ShouldAutoPlayResultAfterNoInput(step))
                    {
                        yield return AutoPlayResultBecauseChildDidNothing(step);
                    }
                    complete = true;
                    break;
                }
            }

            yield return null;
        }

        ClearInput();

        if (hasHelpAnimation && helpGraph.IsValid())
        {
            float speed = Mathf.Max(0.01f, step.helpAnimationSpeed);
            double currentTime = helpPlayable.GetTime();
            float clipLength = step.helpAnimationClip != null ? Mathf.Max(0.01f, step.helpAnimationClip.length) : 0.01f;

            if (currentTime <= 0.01d || currentTime >= clipLength - 0.05f)
                helpPlayable.SetTime(0f);

            helpPlayable.SetSpeed(speed);

            if (step.helpWaitForAnimationBeforeContinue)
            {
                float remaining = Mathf.Max(0.05f, (clipLength - (float)helpPlayable.GetTime()) / speed);
                yield return new WaitForSeconds(remaining);
            }

            if (helpGraph.IsValid())
                helpGraph.Destroy();
            _activeGraphs.Remove(helpGraph);
        }
    }


    private bool ProgressHelperUsesProgressPercent(ActivityStep step)
    {
        if (step == null) return false;
        return step.progressHelperAnimationSelection == ProgressGatePreviewAnimationSelectionMode.PlayAllAnimationsByProgress ||
               step.progressHelperAnimationSelection == ProgressGatePreviewAnimationSelectionMode.PlaySelectedNumbersByProgress;
    }

    private List<AnimationClip> GetProgressHelperClipChoices(ActivityStep step)
    {
        List<AnimationClip> validClips = GetValidClips(step != null ? step.progressHelperAnimations : null);
        if (step == null || validClips.Count == 0)
            return validClips;

        bool useSelectedList = step.progressHelperAnimationSelection == ProgressGatePreviewAnimationSelectionMode.PlaySelectedAnimationNumbers ||
                               step.progressHelperAnimationSelection == ProgressGatePreviewAnimationSelectionMode.PlaySelectedNumbersByProgress;
        if (!useSelectedList)
            return validClips;

        List<AnimationClip> selected = new List<AnimationClip>();
        string raw = string.IsNullOrWhiteSpace(step.progressHelperSelectedAnimationNumbers)
            ? step.progressHelperSelectedAnimationNumber.ToString()
            : step.progressHelperSelectedAnimationNumbers;

        string[] parts = raw.Split(',', ' ', ';', '|');
        HashSet<int> used = new HashSet<int>();
        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out int oneBased))
                continue;

            int index = Mathf.Clamp(oneBased - 1, 0, validClips.Count - 1);
            if (used.Add(index) && validClips[index] != null)
                selected.Add(validClips[index]);
        }

        return selected.Count > 0 ? selected : validClips;
    }

    private AnimationClip SelectProgressHelperAnimation(ActivityStep step)
    {
        if (step == null)
            return null;

        List<AnimationClip> clips = GetProgressHelperClipChoices(step);
        if (clips.Count == 0)
            return null;

        switch (step.progressHelperAnimationSelection)
        {
            case ProgressGatePreviewAnimationSelectionMode.UseSelectedAnimationNumber:
                int requestedIndex = Mathf.Clamp(step.progressHelperSelectedAnimationNumber - 1, 0, clips.Count - 1);
                return clips[requestedIndex];

            case ProgressGatePreviewAnimationSelectionMode.PlaySelectedAnimationNumbers:
            case ProgressGatePreviewAnimationSelectionMode.PlayAllAnimationsInOrder:
                return clips[0];

            case ProgressGatePreviewAnimationSelectionMode.PickRandomAnimationOnce:
                return clips[UnityEngine.Random.Range(0, clips.Count)];

            case ProgressGatePreviewAnimationSelectionMode.PlayAllAnimationsByProgress:
            case ProgressGatePreviewAnimationSelectionMode.PlaySelectedNumbersByProgress:
            case ProgressGatePreviewAnimationSelectionMode.UseFirstAnimation:
            default:
                return clips[0];
        }
    }

    private AnimationClip SelectProgressHelperAnimationForProgress(ActivityStep step, float progress)
    {
        List<AnimationClip> clips = GetProgressHelperClipChoices(step);
        if (clips.Count == 0)
            return null;

        float p = Mathf.Clamp01(progress);
        int index = p >= 0.999f ? clips.Count - 1 : Mathf.Clamp(Mathf.FloorToInt(p * clips.Count), 0, clips.Count - 1);
        return clips[index];
    }

    private bool ProgressHelperShouldPlayAsSequence(ActivityStep step)
    {
        if (step == null || !step.progressUseHelperAnimationWhileTapping)
            return false;

        return step.progressHelperAnimationSelection == ProgressGatePreviewAnimationSelectionMode.PlayAllAnimationsInOrder ||
               step.progressHelperAnimationSelection == ProgressGatePreviewAnimationSelectionMode.PlaySelectedAnimationNumbers;
    }

    private IEnumerator PlayProgressHelperAnimationsOnce(ActivityStep step)
    {
        if (step == null || !step.progressUseHelperAnimationWhileTapping)
            yield break;

        Animator helperAnimator = step.progressHelperAnimator != null ? step.progressHelperAnimator : step.resultAnimator;
        if (helperAnimator == null)
            yield break;

        List<AnimationClip> clips = GetProgressHelperClipChoices(step);
        if (clips.Count == 0)
            yield break;

        float speed = Mathf.Max(0.01f, step.progressHelperAnimationSpeed);
        for (int i = 0; i < clips.Count; i++)
        {
            AnimationClip clip = clips[i];
            if (clip == null)
                continue;

            if (CreateActivityAnimationGraph(helperAnimator, clip, speed, out PlayableGraph graph, out AnimationClipPlayable playable))
            {
                float wait = Mathf.Max(0.01f, clip.length / speed);
                yield return new WaitForSeconds(wait);
                if (graph.IsValid())
                {
                    graph.Destroy();
                    _activeGraphs.Remove(graph);
                }
            }
        }
    }

    private IEnumerator PlayProgressHelperAnimationsFromProgressToEnd(ActivityStep step, float progress)
    {
        if (step == null || !step.progressUseHelperAnimationWhileTapping)
            yield break;

        Animator helperAnimator = step.progressHelperAnimator != null ? step.progressHelperAnimator : step.resultAnimator;
        if (helperAnimator == null)
            yield break;

        List<AnimationClip> clips = GetProgressHelperClipChoices(step);
        if (clips.Count == 0)
            yield break;

        int startIndex = Mathf.Clamp(Mathf.FloorToInt(Mathf.Clamp01(progress) * clips.Count), 0, clips.Count - 1);
        float speed = Mathf.Max(0.01f, step.progressHelperAnimationSpeed);

        for (int i = startIndex; i < clips.Count; i++)
        {
            AnimationClip clip = clips[i];
            if (clip == null)
                continue;

            if (CreateActivityAnimationGraph(helperAnimator, clip, speed, out PlayableGraph graph, out AnimationClipPlayable playable))
            {
                float wait = Mathf.Max(0.01f, clip.length / speed);
                yield return new WaitForSeconds(wait);
                if (graph.IsValid())
                {
                    graph.Destroy();
                    _activeGraphs.Remove(graph);
                }
            }
        }
    }

    private void UpdateProgressHelperAnimation(ActivityStep step, Animator helperAnimator, bool childIsActivelyTapping, float progress, ref bool hasHelperAnimation, ref PlayableGraph graph, ref AnimationClipPlayable playable, ref AnimationClip activeClip)
    {
        if (step == null || !step.progressUseHelperAnimationWhileTapping)
            return;

        if (helperAnimator == null)
            return;

        bool useProgressPercent = ProgressHelperUsesProgressPercent(step);

        // Progress animation rule:
        // In progress mode the animation follows the current bar value, not the latest tap.
        // If the child stops and the bar falls from 45% to 10%, the animation must move back
        // to the 10% range and keep looping there. Do not pause just because tapping stopped.
        bool shouldAnimate = useProgressPercent ? progress > 0.001f : (childIsActivelyTapping && progress > 0.001f);

        if (shouldAnimate)
            StartProgressHelperGroupLoopSound(step);
        else
            StopProgressHelperGroupLoopSound(step);

        if (!shouldAnimate)
        {
            if (hasHelperAnimation && graph.IsValid())
            {
                if (!useProgressPercent && step.progressHelperPauseWhenNotTapping)
                    playable.SetSpeed(0f);

                if (progress <= 0.001f && step.progressHelperResetWhenProgressEmpty)
                {
                    playable.SetTime(0f);
                    graph.Evaluate(0f);
                }
            }
            return;
        }

        AnimationClip neededClip = useProgressPercent ? SelectProgressHelperAnimationForProgress(step, progress) : (activeClip != null ? activeClip : SelectProgressHelperAnimation(step));
        if (neededClip == null)
            return;

        float speed = Mathf.Max(0.01f, step.progressHelperAnimationSpeed);
        if (!hasHelperAnimation || !graph.IsValid() || activeClip != neededClip)
        {
            if (graph.IsValid())
            {
                graph.Destroy();
                _activeGraphs.Remove(graph);
            }

            if (!CreateActivityAnimationGraph(helperAnimator, neededClip, speed, out graph, out playable))
            {
                hasHelperAnimation = false;
                activeClip = null;
                return;
            }

            hasHelperAnimation = true;
            activeClip = neededClip;

            // Play the sound for this clip (individual or shared fallback)
            if (useProgressPercent)
            {
                List<AnimationClip> clips = GetProgressHelperClipChoices(step);
                int soundClipIndex = clips.IndexOf(neededClip);
                TryPlayHelperClipSound(step, soundClipIndex >= 0 ? soundClipIndex : 0);
            }
            else
            {
                TryPlayHelperClipSound(step, 0);
            }

            // Proportional start time fix:
            // When progress drops and we switch to a lower clip, start that clip from the
            // proportional position matching where progress is within that clip's range.
            // This prevents the visual jump of always restarting from frame 0.
            float startTime = 0f;
            if (useProgressPercent && neededClip != null)
            {
                List<AnimationClip> clips = GetProgressHelperClipChoices(step);
                int clipCount = Mathf.Max(1, clips.Count);
                int clipIndex = clips.IndexOf(neededClip);
                if (clipIndex >= 0 && neededClip.length > 0.01f)
                {
                    float rangeStart = (float)clipIndex / clipCount;
                    float rangeEnd   = (float)(clipIndex + 1) / clipCount;
                    float rangeSize  = Mathf.Max(0.001f, rangeEnd - rangeStart);
                    float progressInRange = Mathf.Clamp01((Mathf.Clamp01(progress) - rangeStart) / rangeSize);
                    startTime = progressInRange * neededClip.length;
                }
            }

            playable.SetTime(startTime);
            playable.SetSpeed(speed);
            graph.Evaluate(0f);
            return;
        }

        playable.SetSpeed(speed);
        if (step.progressHelperLoopAnimation)
        {
            double clipLength = Mathf.Max(0.01f, activeClip.length);
            double currentTime = playable.GetTime();
            if (currentTime >= clipLength)
            {
                playable.SetTime(currentTime % clipLength);
                graph.Evaluate(0f);
            }
        }
    }

    private IEnumerator RunWaitForStoryThenTapObject(ActivityStep step)
    {
        if (step == null) yield break;

        GameObject tapObject = step.storyMomentTapObject != null ? step.storyMomentTapObject : step.targetObject;
        Transform movingObject = step.storyMomentMovingObject != null
            ? step.storyMomentMovingObject
            : (tapObject != null ? tapObject.transform : null);

        if (step.storyMomentObjectBeforeComplete != null)
            step.storyMomentObjectBeforeComplete.SetActive(true);
        if (step.storyMomentObjectAfterComplete != null)
            step.storyMomentObjectAfterComplete.SetActive(false);

        Vector3 originalLocalPosition = movingObject != null ? movingObject.localPosition : Vector3.zero;
        float startedAt = Time.time;
        float progress = 0f;
        int tapCount = 0;
        float lastCorrectTapTime = -999f;
        bool hintShown = false;
        float hintShownAt = 0f;
        bool completed = false;
        bool skipped = false;
        float tapShakeUntil = 0f;
        float tapShakeAmount = 0f;
        List<float> recentTapTimes = new List<float>();
        // Track drop state so the drop sound fires ONCE when bar starts falling.
        bool wasProgressDropping = false;

        if (StepUsesProgress(step))
            activityPanel?.ShowProgress(0f, "0%");

        // ---- Animation While Tapping (shared system) ----
        // Uses the same progressHelper fields as ProgressGate so the same
        // Animation While Tapping section in the Inspector works for this activity too.
        AnimationClip storyHelperClip = null;
        PlayableGraph storyHelperGraph = default(PlayableGraph);
        AnimationClipPlayable storyHelperPlayable = default(AnimationClipPlayable);
        bool hasStoryHelperAnimation = false;
        Animator storyHelperAnimator = null;

        if (step.progressUseHelperAnimationWhileTapping)
        {
            storyHelperAnimator = step.progressHelperAnimator != null ? step.progressHelperAnimator : step.resultAnimator;
            if (!ProgressHelperUsesProgressPercent(step))
            {
                storyHelperClip = SelectProgressHelperAnimation(step);
                hasStoryHelperAnimation = CreateActivityAnimationGraph(storyHelperAnimator, storyHelperClip, 0f, out storyHelperGraph, out storyHelperPlayable);
                if (hasStoryHelperAnimation && storyHelperGraph.IsValid())
                {
                    storyHelperPlayable.SetTime(0f);
                    storyHelperPlayable.SetSpeed(0f);
                    storyHelperGraph.Evaluate(0f);
                }
            }
        }
        // ---- End Animation While Tapping setup ----

        BeginInput(step, data => IsInputValidForStep(step, data), data =>
        {
            tapCount++;
            _acceptedInputCount++;
            lastCorrectTapTime = Time.time;
            recentTapTimes.Add(Time.time);

            if (step.storyMomentTapSound != null)
                CreateTempAudioSource(step.storyMomentTapSound, step.storyMomentTapSoundVolume, false);
            else
                TryPlayCorrectTapSound(step); // shared fallback if no dedicated tap sound

            float tapSpeed = GetTapSpeed(recentTapTimes, Mathf.Max(0.25f, step.storyMomentTapSpeedWindowSeconds));
            float fastT = Mathf.Clamp01(tapSpeed / Mathf.Max(0.1f, step.storyMomentFastTapSpeed));
            tapShakeAmount = Mathf.Lerp(Mathf.Max(0f, step.storyMomentSlowTapShake), Mathf.Max(0f, step.storyMomentFastTapShake), fastT);
            tapShakeUntil = Time.time + Mathf.Max(0.05f, step.storyMomentTapShakeSeconds);

            if (step.storyMomentCompletesBy == StoryMomentTapCompletionMode.RequiredTapCount)
            {
                int required = Mathf.Max(1, step.storyMomentRequiredTaps);
                progress = Mathf.Clamp01((float)tapCount / required);
            }
        }, data =>
        {
            if (!string.IsNullOrWhiteSpace(step.storyMomentWrongTapText))
                ShowQuickFeedback(step.storyMomentWrongTapText);
            if (step.storyMomentWrongTapSound != null)
                CreateTempAudioSource(step.storyMomentWrongTapSound, step.storyMomentWrongTapSoundVolume, false);
            HighlightTargetForStep(step);
        });

        while (!completed && !skipped)
        {
            float elapsed = Time.time - startedAt;
            bool activeTap = Time.time - lastCorrectTapTime <= Mathf.Max(0.05f, step.storyMomentTapActiveWindowSeconds);

            if (step.storyMomentCompletesBy == StoryMomentTapCompletionMode.RequiredActiveTappingTime && activeTap)
            {
                progress = Mathf.Clamp01(progress + Time.deltaTime / Mathf.Max(0.1f, step.storyMomentRequiredTappingSeconds));
            }

            if (step.storyMomentProgressDropsIfChildStops && !activeTap && progress > 0f)
            {
                float prevProgress = progress;
                progress = Mathf.Max(0f, progress - (Mathf.Max(0f, step.storyMomentProgressDropSpeed) / 100f) * Time.deltaTime);
                bool isDropping = progress < prevProgress;
                // Only fire drop sound on the FIRST frame of the drop, not every frame.
                if (isDropping && !wasProgressDropping)
                    PlayProgressDropSound(step);
                wasProgressDropping = isDropping;
            }
            else
            {
                wasProgressDropping = false;
            }

            if (movingObject != null)
            {
                Vector3 targetPosition = originalLocalPosition + Vector3.up * (Mathf.Max(0f, step.storyMomentMoveUpHeight) * progress);
                if (Time.time < tapShakeUntil && tapShakeAmount > 0f)
                    targetPosition += UnityEngine.Random.insideUnitSphere * tapShakeAmount;
                movingObject.localPosition = Vector3.Lerp(movingObject.localPosition, targetPosition, Time.deltaTime * Mathf.Max(1f, step.storyMomentMoveSmoothness));
            }

            if (StepUsesProgress(step))
                activityPanel?.ShowProgress(progress, Mathf.RoundToInt(progress * 100f) + "%");

            // Update the character's animation based on current progress.
            if (step.progressUseHelperAnimationWhileTapping)
                UpdateProgressHelperAnimation(step, storyHelperAnimator, activeTap, progress, ref hasStoryHelperAnimation, ref storyHelperGraph, ref storyHelperPlayable, ref storyHelperClip);

            // Check positive milestone hints every frame
            CheckAndFireProgressMilestones(step, progress);

            if (progress >= 0.999f)
            {
                PlayProgressFullSound(step);
                completed = true;
                break;
            }

            if (!hintShown && step.storyMomentShowHintAfterSeconds > 0f && elapsed >= step.storyMomentShowHintAfterSeconds)
            {
                hintShown = true;
                hintShownAt = Time.time;
                if (!string.IsNullOrWhiteSpace(step.storyMomentHintText))
                    ShowQuickFeedback(step.storyMomentHintText);
                HighlightTargetForStep(step);
            }

            if (hintShown && step.storyMomentSkipAfterHintSeconds > 0f && Time.time - hintShownAt >= step.storyMomentSkipAfterHintSeconds)
            {
                if (ShouldAutoPlayResultAfterNoInput(step))
                    completed = true;
                else
                    skipped = true;
                break;
            }

            if (step.storyMomentTotalActivitySeconds > 0f && elapsed >= step.storyMomentTotalActivitySeconds)
            {
                if (ShouldAutoPlayResultAfterNoInput(step))
                    completed = true;
                else
                    skipped = true;
                break;
            }

            yield return null;
        }

        ClearInput();

        // Clean up the tapping animation graph before result plays.
        if (hasStoryHelperAnimation && storyHelperGraph.IsValid())
        {
            storyHelperGraph.Destroy();
            _activeGraphs.Remove(storyHelperGraph);
        }

        if (StepUsesProgress(step))
            activityPanel?.HideProgress();

        if (completed)
        {
            yield return PlayStoryMomentBreakAndDrop(step, movingObject, originalLocalPosition);
            // Play shared result (animation, sound, voice) after the break-and-drop finishes.
            yield return PlayStoryResult(step);
        }
        else
        {
            // Skipped (no input timeout or hint skip): drop object back then play activity result.
            // Story must not continue before result finishes.
            if (movingObject != null)
                yield return MoveLocalPosition(movingObject, movingObject.localPosition, originalLocalPosition, Mathf.Max(0.01f, step.storyMomentDropBackSeconds));

            if (ShouldAutoPlayResultAfterNoInput(step))
                yield return AutoPlayResultBecauseChildDidNothing(step);
        }
    }

    private IEnumerator PlayStoryMomentBreakAndDrop(ActivityStep step, Transform movingObject, Vector3 originalLocalPosition)
    {
        if (step == null) yield break;

        if (step.storyMomentBreakSound != null)
            CreateTempAudioSource(step.storyMomentBreakSound, step.storyMomentBreakSoundVolume, false);

        bool switched = false;
        float shakeSeconds = Mathf.Max(0f, step.storyMomentBreakShakeSeconds);
        float elapsed = 0f;
        Vector3 topPosition = movingObject != null ? movingObject.localPosition : Vector3.zero;

        while (elapsed < shakeSeconds)
        {
            elapsed += Time.deltaTime;
            if (!switched && elapsed >= shakeSeconds * Mathf.Clamp01(step.storyMomentSwitchAtShakePercent))
            {
                switched = true;
                SwitchStoryMomentObjects(step);
            }

            if (movingObject != null)
            {
                Vector3 shake = UnityEngine.Random.insideUnitSphere * Mathf.Max(0f, step.storyMomentBreakShakeAmount);
                movingObject.localPosition = topPosition + shake;
            }

            yield return null;
        }

        if (!switched)
            SwitchStoryMomentObjects(step);

        if (movingObject != null)
            yield return MoveLocalPosition(movingObject, movingObject.localPosition, originalLocalPosition, Mathf.Max(0.01f, step.storyMomentDropBackSeconds));

        if (step.storyMomentExtraWaitAfterComplete > 0f)
            yield return new WaitForSeconds(step.storyMomentExtraWaitAfterComplete);
    }

    private void SwitchStoryMomentObjects(ActivityStep step)
    {
        if (step == null) return;
        if (step.storyMomentObjectBeforeComplete != null)
            step.storyMomentObjectBeforeComplete.SetActive(false);
        if (step.storyMomentObjectAfterComplete != null)
            step.storyMomentObjectAfterComplete.SetActive(true);
    }

    private IEnumerator MoveLocalPosition(Transform target, Vector3 from, Vector3 to, float seconds)
    {
        if (target == null) yield break;
        if (seconds <= 0f)
        {
            target.localPosition = to;
            yield break;
        }

        float elapsed = 0f;
        while (elapsed < seconds)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / seconds);
            t = t * t * (3f - 2f * t);
            target.localPosition = Vector3.Lerp(from, to, t);
            yield return null;
        }
        target.localPosition = to;
    }

    /// <summary>
    /// Progress Gate is for moments where the child must keep tapping until a progress bar is complete.
    /// If the child does not finish in time, the activity can still unlock the next story action so the story never gets stuck.
    /// </summary>
    private IEnumerator RunProgressGate(ActivityStep step)
    {
        bool complete = false;
        bool autoSkipped = false;
        bool skipResultAfterNoInput = false;
        bool autoFinishBecauseChildStopped = false;
        float autoFinishStartProgress = 0f;
        float startedAt = Time.time;
        float progress = 0f;
        int validTapCount = 0;
        float lastValidTapTime = -999f;
        bool noInputHintShown = false;
        float noInputHintShownAt = 0f;
        float lastSequenceReactionTime = -999f;
        int reactionSequenceIndex = 0;
        List<float> recentTapTimes = new List<float>();
        // Track drop state so the drop sound fires ONCE when the bar starts falling,
        // not every frame for the full duration of the drop.
        bool wasProgressDropping = false;

        AnimationClip selectedHelperClip = null;
        PlayableGraph helperGraph = default(PlayableGraph);
        AnimationClipPlayable helperPlayable = default(AnimationClipPlayable);
        bool hasHelperAnimation = false;

        PlayableGraph progressReactionTapGraph = default(PlayableGraph);
        AnimationClipPlayable progressReactionTapPlayable = default(AnimationClipPlayable);
        AnimationClip progressReactionTapClip = null;

        PlayableGraph progressReactionHoldGraph = default(PlayableGraph);
        AnimationClipPlayable progressReactionHoldPlayable = default(AnimationClipPlayable);
        AnimationClip progressReactionHoldClip = null;

        Animator helperAnimator = null;
        if (step.progressUseHelperAnimationWhileTapping)
        {
            helperAnimator = step.progressHelperAnimator != null ? step.progressHelperAnimator : step.resultAnimator;
            if (!ProgressHelperUsesProgressPercent(step))
            {
                selectedHelperClip = SelectProgressHelperAnimation(step);
                hasHelperAnimation = CreateActivityAnimationGraph(helperAnimator, selectedHelperClip, 0f, out helperGraph, out helperPlayable);
                if (hasHelperAnimation && helperGraph.IsValid())
                {
                    helperPlayable.SetTime(0f);
                    helperPlayable.SetSpeed(0f);
                    helperGraph.Evaluate(0f);
                }
            }
        }

        if (ProgressBarFollowsInput(step)) activityPanel?.ShowProgress(0f, "0%");

        BeginInput(step, data => IsInputValidForStep(step, data), data =>
        {
            validTapCount++;
            _acceptedInputCount++;
            lastValidTapTime = Time.time;
            recentTapTimes.Add(Time.time);

            if (step.progressGateCompletesBy == ActivityProgressGateCompletionMode.RequiredTapCount)
            {
                int required = Mathf.Max(1, step.progressRequiredTaps);
                progress = Mathf.Clamp01(progress + 1f / required);
            }

            PlayProgressCorrectTapFeedback(step, data);

            if (step.progressTapSound != null)
                CreateTempAudioSource(step.progressTapSound, step.progressTapSoundVolume, false);
            else
                TryPlayCorrectTapSound(step); // shared fallback if no dedicated tap sound

            float tapSpeed = GetTapSpeed(recentTapTimes, Mathf.Max(0.25f, step.progressTapSpeedWindowSeconds));
            if (step.progressReactionPlaybackMode == ActivityProgressReactionPlaybackMode.PlayOnValidTap)
                PlayProgressReactionSequence(step, progress, tapSpeed, ref reactionSequenceIndex, ref lastSequenceReactionTime, ref progressReactionTapGraph, ref progressReactionTapPlayable, ref progressReactionTapClip);

            RunReactions(step, ActivityReactionMoment.EveryValidInput);
            RunReactions(step, ActivityReactionMoment.IfReactionIsFree);
        }, data =>
        {
            HandleProgressGateWrongInput(step, data);
        });

        while (!complete)
        {
            float elapsed = Time.time - startedAt;
            float activeWindow = Mathf.Max(0.05f, step.progressTapActiveWindowSeconds);
            bool childIsActivelyTapping = Time.time - lastValidTapTime <= activeWindow;
            float tapSpeed = GetTapSpeed(recentTapTimes, Mathf.Max(0.25f, step.progressTapSpeedWindowSeconds));

            if (step.progressReactionPlaybackMode == ActivityProgressReactionPlaybackMode.HoldByProgressPercent)
                UpdateProgressHoldReaction(step, progress, tapSpeed, ref progressReactionHoldGraph, ref progressReactionHoldPlayable, ref progressReactionHoldClip);

            if (step.progressGateCompletesBy == ActivityProgressGateCompletionMode.RequiredActiveTappingTime)
            {
                if (childIsActivelyTapping)
                {
                    float requiredSeconds = Mathf.Max(0.1f, step.progressRequiredTappingSeconds);
                    progress = Mathf.Clamp01(progress + Time.deltaTime / requiredSeconds);
                }
            }
            else if (step.progressGateCompletesBy == ActivityProgressGateCompletionMode.RequiredTapSpeedForTime)
            {
                float requiredSpeed = Mathf.Max(0.1f, step.progressRequiredTapsPerSecond);
                if (tapSpeed >= requiredSpeed)
                {
                    float requiredSeconds = Mathf.Max(0.1f, step.progressRequiredSpeedSeconds);
                    progress = Mathf.Clamp01(progress + Time.deltaTime / requiredSeconds);
                }
            }

            if (step.progressDropsWhenNotTapping && !childIsActivelyTapping && progress > 0f)
            {
                float prevProgress = progress;
                progress = Mathf.Max(0f, progress - (Mathf.Max(0f, step.progressLossPerSecond) / 100f) * Time.deltaTime);
                bool isDropping = progress < prevProgress;
                // Play drop sound only on the FIRST frame the bar starts falling,
                // not every frame for the full duration of the drop.
                if (isDropping && !wasProgressDropping)
                    PlayProgressDropSound(step);
                wasProgressDropping = isDropping;
            }
            else
            {
                wasProgressDropping = false;
            }

            if (_acceptedInputCount > 0 && !childIsActivelyTapping &&
                step.progressAutoFinishAfterNoTapSeconds > 0f &&
                Time.time - lastValidTapTime >= step.progressAutoFinishAfterNoTapSeconds)
            {
                autoSkipped = true;
                skipResultAfterNoInput = false;
                autoFinishBecauseChildStopped = true;
                autoFinishStartProgress = progress;
                complete = true;
                break;
            }

            UpdateProgressHelperAnimation(step, helperAnimator, childIsActivelyTapping, progress, ref hasHelperAnimation, ref helperGraph, ref helperPlayable, ref selectedHelperClip);

            // Check positive milestone hints every frame
            CheckAndFireProgressMilestones(step, progress);

            string progressLabel = Mathf.RoundToInt(progress * 100f) + "%";
            if (step.progressGateCompletesBy == ActivityProgressGateCompletionMode.RequiredTapSpeedForTime)
                progressLabel += "  " + tapSpeed.ToString("0.0") + "/s";
            if (ProgressBarFollowsInput(step)) activityPanel?.ShowProgress(progress, progressLabel);

            if (progress >= 0.999f)
            {
                PlayProgressFullSound(step);
                complete = true;
                break;
            }

            // NOTE: progressAutoStartStoryAfterSeconds is intentionally zeroed by NormalizeActivityStep.
            // The no-input helper below handles auto-finish cleanly via AutoPlayResultBecauseChildDidNothing.

            if (_acceptedInputCount == 0 && step.enableNoInputHelp && step.noInputHintAfterSeconds > 0f && !noInputHintShown)
            {
                if (elapsed >= step.noInputHintAfterSeconds)
                {
                    noInputHintShown = true;
                    noInputHintShownAt = Time.time;
                    ShowNoInputHint(step);
                }
            }

            if (_acceptedInputCount == 0 && noInputHintShown && (ShouldAutoPlayResultAfterNoInput(step) || ShouldSkipActivityAfterNoInput(step)))
            {
                float skipWait = Mathf.Max(0f, step.autoSkipAfterHintSeconds);
                if (Time.time - noInputHintShownAt >= skipWait)
                {
                    autoSkipped = true;
                    // Auto Play = play configured result through the existing result path.
                    // Skip = finish without result.
                    skipResultAfterNoInput = ShouldSkipActivityAfterNoInput(step);
                    complete = true;
                    break;
                }
            }

            yield return null;
        }

        ClearInput();
        if (StepUsesProgress(step)) activityPanel?.HideProgress();

        if (hasHelperAnimation && helperGraph.IsValid())
        {
            helperGraph.Destroy();
            _activeGraphs.Remove(helperGraph);
        }

        if (progressReactionTapGraph.IsValid())
        {
            progressReactionTapGraph.Destroy();
            _activeGraphs.Remove(progressReactionTapGraph);
        }

        if (progressReactionHoldGraph.IsValid())
        {
            progressReactionHoldGraph.Destroy();
            _activeGraphs.Remove(progressReactionHoldGraph);
        }

        if (autoSkipped)
        {
            if (!skipResultAfterNoInput && step.playResultWhenProgressAutoSkips)
                yield return AutoPlayResultBecauseChildDidNothing(step, autoFinishBecauseChildStopped ? autoFinishStartProgress : 0f);
        }
        else
        {
            yield return PlayStoryResult(step);
        }
    }

    private void PlayProgressCorrectTapFeedback(ActivityStep step, ActivityInputData data)
    {
        if (step == null || !step.progressUseCorrectTapFeedback)
            return;

        Transform feedbackTarget = step.progressCorrectTapFeedbackObject;
        if (feedbackTarget == null && step.targetObject != null)
            feedbackTarget = step.targetObject.transform;
        if (feedbackTarget == null && data.hitObject != null)
            feedbackTarget = data.hitObject.transform;

        if (feedbackTarget != null)
            StartProgressTapFeedback(feedbackTarget, step.progressCorrectTapFeedbackStyle, step.progressCorrectMoveUpHeight, step.progressCorrectShakeAmount, step.progressCorrectTapFeedbackSeconds, step.progressCorrectReturnSeconds);
    }

    private void HandleProgressGateWrongInput(ActivityStep step, ActivityInputData data)
    {
        if (step == null) return;

        RunReactions(step, ActivityReactionMoment.WhenInputFails);

        if (!step.progressUseWrongTapFeedback)
        {
            if (step.showHintWhenWrongInput || !string.IsNullOrWhiteSpace(step.tryAgainMessage))
                HandleWrongInput(step);
            return;
        }

        string text = !string.IsNullOrWhiteSpace(step.progressWrongTapText) ? step.progressWrongTapText : step.wrongInputHintText;
        if (string.IsNullOrWhiteSpace(text))
            text = step.tryAgainMessage;
        if (!string.IsNullOrWhiteSpace(text))
            ShowQuickFeedback(text);

        AudioClip sound = step.progressWrongTapSound != null ? step.progressWrongTapSound : step.wrongInputSound;
        float volume = step.progressWrongTapSound != null ? step.progressWrongTapSoundVolume : step.wrongInputSoundVolume;
        if (sound != null)
            CreateTempAudioSource(sound, volume, false);

        if (step.progressShakeWrongTappedObject && data.hitObject != null)
            StartProgressTapFeedback(data.hitObject.transform, ActivityProgressTapFeedbackStyle.SmoothShake, 0f, step.progressWrongShakeAmount, step.progressWrongTapFeedbackSeconds, step.progressWrongReturnSeconds);

        if (step.progressPulseCorrectObjectOnWrongTap)
            HighlightTargetForStep(step);
    }

    private void StartProgressTapFeedback(Transform target, ActivityProgressTapFeedbackStyle style, float moveUpHeight, float shakeAmount, float seconds, float returnSeconds)
    {
        if (target == null) return;

        if (_progressTapFeedbackRoutines.TryGetValue(target, out Coroutine running) && running != null)
            StopCoroutine(running);

        if (!_progressTapOriginalLocalPositions.ContainsKey(target))
            _progressTapOriginalLocalPositions[target] = target.localPosition;

        Coroutine routine = StartCoroutine(ProgressTapFeedbackRoutine(target, style, moveUpHeight, shakeAmount, seconds, returnSeconds));
        _progressTapFeedbackRoutines[target] = routine;
    }

    private IEnumerator ProgressTapFeedbackRoutine(Transform target, ActivityProgressTapFeedbackStyle style, float moveUpHeight, float shakeAmount, float seconds, float returnSeconds)
    {
        if (target == null) yield break;

        Vector3 original = _progressTapOriginalLocalPositions.TryGetValue(target, out Vector3 cached) ? cached : target.localPosition;
        float duration = Mathf.Max(0.03f, seconds);
        float elapsed = 0f;

        bool useMove = style == ActivityProgressTapFeedbackStyle.SmoothBump || style == ActivityProgressTapFeedbackStyle.BumpAndShake;
        bool useShake = style == ActivityProgressTapFeedbackStyle.SmoothShake || style == ActivityProgressTapFeedbackStyle.BumpAndShake;

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / duration);
            float bump = Mathf.Sin(t * Mathf.PI);
            Vector3 offset = Vector3.zero;

            if (useMove)
                offset += Vector3.up * Mathf.Max(0f, moveUpHeight) * bump;

            if (useShake)
            {
                float shake = Mathf.Max(0f, shakeAmount) * bump;
                offset += new Vector3(Mathf.Sin(t * Mathf.PI * 8f), 0f, Mathf.Cos(t * Mathf.PI * 6f)) * shake;
            }

            target.localPosition = original + offset;
            yield return null;
        }

        float backSeconds = Mathf.Max(0.01f, returnSeconds);
        Vector3 from = target.localPosition;
        elapsed = 0f;
        while (elapsed < backSeconds)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / backSeconds);
            t = t * t * (3f - 2f * t);
            target.localPosition = Vector3.Lerp(from, original, t);
            yield return null;
        }

        target.localPosition = original;
        _progressTapFeedbackRoutines.Remove(target);
    }

    private void StopProgressTapFeedbacks()
    {
        foreach (var pair in _progressTapFeedbackRoutines)
        {
            if (pair.Value != null)
                StopCoroutine(pair.Value);
            if (pair.Key != null && _progressTapOriginalLocalPositions.TryGetValue(pair.Key, out Vector3 original))
                pair.Key.localPosition = original;
        }

        _progressTapFeedbackRoutines.Clear();
        _progressTapOriginalLocalPositions.Clear();
    }

    private float GetTapSpeed(List<float> recentTapTimes, float windowSeconds)
    {
        if (recentTapTimes == null || recentTapTimes.Count == 0)
            return 0f;

        float now = Time.time;
        for (int i = recentTapTimes.Count - 1; i >= 0; i--)
        {
            if (now - recentTapTimes[i] > windowSeconds)
                recentTapTimes.RemoveAt(i);
        }

        return recentTapTimes.Count / Mathf.Max(0.1f, windowSeconds);
    }

    private void PlayProgressReactionSequence(ActivityStep step, float progress, float tapSpeed, ref int sequenceIndex, ref float lastPlayTime, ref PlayableGraph graph, ref AnimationClipPlayable playable, ref AnimationClip activeClip)
    {
        if (step == null || !step.progressUseReactionSequence)
            return;

        if (step.progressReactionAnimator == null)
        {
            Debug.LogWarning("[ContentController] Progress Gate reaction skipped: Reaction Animator is missing.", this);
            return;
        }

        List<AnimationClip> clips = GetValidClips(step.progressReactionAnimations);
        if (clips.Count == 0)
        {
            Debug.LogWarning("[ContentController] Progress Gate reaction skipped: Reaction Animations list is empty.", this);
            return;
        }

        if (Time.time - lastPlayTime < Mathf.Max(0f, step.progressReactionMinimumGapSeconds))
            return;

        StartProgressReactionGroupLoopSound(step);

        AnimationClip clip = SelectProgressReactionClip(step, clips, progress, tapSpeed, sequenceIndex);
        if (clip == null)
            return;

        if (step.progressReactionOrder == ActivityReactionSequenceMode.InOrder)
            sequenceIndex++;

        float speed = Mathf.Max(0.01f, step.progressReactionAnimationSpeed);
        PrepareActivityAnimator(step.progressReactionAnimator, speed);

        if (activeClip == clip && graph.IsValid())
        {
            // Same progress stage. Keep looping instead of restarting from frame 0 on every tap.
            playable.SetSpeed(speed);
            double clipLength = Mathf.Max(0.01f, clip.length);
            if (playable.GetTime() >= clipLength)
            {
                playable.SetTime(0f);
                graph.Evaluate(0f);
            }
            lastPlayTime = Time.time;
            return;
        }

        // Important: only one progress reaction graph should drive this reaction animator at a time.
        // Multiple active PlayableGraphs targeting the same Animator can fight each other and make the model look frozen.
        if (graph.IsValid())
        {
            graph.Destroy();
            _activeGraphs.Remove(graph);
        }

        if (CreateActivityAnimationGraph(step.progressReactionAnimator, clip, speed, out graph, out playable))
        {
            activeClip = clip;
            TryPlayProgressReactionClipSound(step, clips.IndexOf(clip));
            playable.SetTime(0f);
            playable.SetSpeed(speed);
            graph.Evaluate(0f);
        }
        else
        {
            Debug.LogWarning($"[ContentController] Progress Gate reaction failed to play clip '{clip.name}'.", this);
        }

        lastPlayTime = Time.time;
    }

    private void UpdateProgressHoldReaction(ActivityStep step, float progress, float tapSpeed, ref PlayableGraph graph, ref AnimationClipPlayable playable, ref AnimationClip activeClip)
    {
        if (step == null || !step.progressUseReactionSequence) return;
        if (step.progressReactionAnimator == null) return;

        List<AnimationClip> clips = GetValidClips(step.progressReactionAnimations);
        if (clips.Count == 0) return;

        if (progress <= 0.001f)
        {
            StopProgressReactionGroupLoopSound(step);
            return;
        }

        StartProgressReactionGroupLoopSound(step);

        AnimationClip clip = SelectProgressReactionClip(step, clips, progress, tapSpeed, 0);
        if (clip == null) return;

        float speed = Mathf.Max(0.01f, step.progressReactionAnimationSpeed);
        PrepareActivityAnimator(step.progressReactionAnimator, speed);

        if (activeClip != clip || !graph.IsValid())
        {
            if (graph.IsValid())
            {
                graph.Destroy();
                _activeGraphs.Remove(graph);
            }

            if (CreateActivityAnimationGraph(step.progressReactionAnimator, clip, speed, out graph, out playable))
            {
                activeClip = clip;
                TryPlayProgressReactionClipSound(step, clips.IndexOf(clip));
                playable.SetTime(0f);
                playable.SetSpeed(speed);
                graph.Evaluate(0f);
            }
            else
            {
                Debug.LogWarning($"[ContentController] Progress Gate hold reaction failed to play clip '{clip.name}'.", this);
            }
        }
        else if (graph.IsValid())
        {
            playable.SetSpeed(speed);
            double clipLength = Mathf.Max(0.01f, clip.length);
            if (playable.GetTime() >= clipLength)
            {
                playable.SetTime(0f);
                graph.Evaluate(0f);
            }
        }
    }

    private AnimationClip SelectProgressReactionClip(ActivityStep step, List<AnimationClip> clips, float progress, float tapSpeed, int sequenceIndex)
    {
        if (step == null || clips == null || clips.Count == 0) return null;

        switch (step.progressReactionOrder)
        {
            case ActivityReactionSequenceMode.Random:
                return clips[UnityEngine.Random.Range(0, clips.Count)];
            case ActivityReactionSequenceMode.ByTapSpeed:
                int speedIndex = Mathf.Clamp(Mathf.FloorToInt(tapSpeed / Mathf.Max(0.1f, step.progressRequiredTapsPerSecond) * clips.Count), 0, clips.Count - 1);
                return clips[speedIndex];
            case ActivityReactionSequenceMode.ByProgress:
                // Beginner rule: with 5 clips, 1-20% uses clip 1, 21-40% uses clip 2, etc.
                // This avoids restarting the next clip exactly at 20% when the user expects 10% and 20% to stay in the same stage.
                int progressIndex = Mathf.Clamp(Mathf.CeilToInt(Mathf.Clamp01(progress) * clips.Count) - 1, 0, clips.Count - 1);
                return clips[progressIndex];
            case ActivityReactionSequenceMode.InOrder:
            default:
                return clips[Mathf.Abs(sequenceIndex) % clips.Count];
        }
    }

    private List<AnimationClip> GetValidClips(List<AnimationClip> clips)
    {
        List<AnimationClip> valid = new List<AnimationClip>();
        if (clips == null)
            return valid;

        for (int i = 0; i < clips.Count; i++)
        {
            if (clips[i] != null)
                valid.Add(clips[i]);
        }

        return valid;
    }

    /// <summary>
    /// Group Action is for moments where tapping any allowed character/object makes several assigned objects react together.
    /// Example: tap one character and two characters greet at the same time.
    /// </summary>
    private IEnumerator RunGroupAction(ActivityStep step)
    {
        bool complete = false;
        bool autoSkipped = false;
        bool skipGroupActionsAfterNoInput = false;
        float startedAt = Time.time;
        bool noInputHintShown = false;
        float noInputHintShownAt = 0f;
        int runningGroupActions = 0;
        _uniqueTappedGroupObjects.Clear();
        UpdateGroupActionProgress(step);

        // Activity transform for target actions is applied only while this activity is active.
        // This keeps VFX/story pose untouched before the activity starts, and restores after completion.
        ApplyTargetActivityTransformsForActivity(step);

        BeginInput(step, data => IsInputValidForStep(step, data), data =>
        {
            GameObject tappedObject = FindMatchingGroupTapObject(step, data.hitObject);
            if (tappedObject == null && data.hitObject != null)
                tappedObject = data.hitObject;

            if (step.groupIgnoreRepeatTaps && tappedObject != null && _uniqueTappedGroupObjects.Contains(tappedObject))
            {
                if (!string.IsNullOrWhiteSpace(step.groupRepeatTapMessage))
                    ShowQuickFeedback(step.groupRepeatTapMessage);
                return;
            }

            _acceptedInputCount++;
            if (tappedObject != null)
                _uniqueTappedGroupObjects.Add(tappedObject);

            // Play correct tap sound for group activities.
            // Each group action may also have its own sound (action.soundEffect) which plays
            // when the action animates. This shared tap sound plays immediately on tap.
            TryPlayCorrectTapSound(step);

            runningGroupActions++;
            StartCoroutine(PlayGroupActionsAndCount(step, tappedObject, () => runningGroupActions--));
            RunReactions(step, ActivityReactionMoment.EveryValidInput);
            RunReactions(step, ActivityReactionMoment.IfReactionIsFree);
            UpdateGroupActionProgress(step);

            if (IsGroupActionComplete(step))
                complete = true;
        }, data =>
        {
            HandleWrongInput(step);
        });

        while (!complete)
        {
            float elapsed = Time.time - startedAt;

            // NOTE: groupAutoStartStoryAfterSeconds is always 0 (zeroed by NormalizeActivityStep).
            // No-input handling below uses the shared result path so story never starts before result.

            if (_acceptedInputCount == 0 && step.enableNoInputHelp && step.noInputHintAfterSeconds > 0f && !noInputHintShown)
            {
                if (elapsed >= step.noInputHintAfterSeconds)
                {
                    noInputHintShown = true;
                    noInputHintShownAt = Time.time;
                    ShowNoInputHint(step);
                }
            }

            if (_acceptedInputCount == 0 && noInputHintShown && (ShouldAutoPlayResultAfterNoInput(step) || ShouldSkipActivityAfterNoInput(step)))
            {
                float skipWait = Mathf.Max(0f, step.autoSkipAfterHintSeconds);
                if (Time.time - noInputHintShownAt >= skipWait)
                {
                    autoSkipped = true;
                    skipGroupActionsAfterNoInput = ShouldSkipActivityAfterNoInput(step);
                    break;
                }
            }

            yield return null;
        }

        ClearInput();

        // Always wait for any in-flight group action coroutines before moving to result.
        while (runningGroupActions > 0)
            yield return null;

        if (autoSkipped)
        {
            // No-input path: play the activity result first (animations, voice, VFX).
            // Story must not continue until result finishes.
            if (!skipGroupActionsAfterNoInput)
            {
                if (step.groupPlayActionsWhenAutoSkipped)
                    yield return PlayGroupActions(step, null);
                yield return AutoPlayResultBecauseChildDidNothing(step);
            }
            // else: SkipActivityAndContinue - no result plays, go straight to cleanup.
        }
        else
        {
            // Normal completion: play group-specific result then shared result.
            if (step.groupWaitSecondsBeforeStory > 0f)
                yield return new WaitForSeconds(step.groupWaitSecondsBeforeStory);

            if (step.groupResultVoiceOver != null)
            {
                AudioSource voice = CreateTempAudioSource(step.groupResultVoiceOver, step.groupResultVoiceVolume, false);
                if (step.groupWaitForVoiceOver && voice != null)
                    yield return new WaitForSeconds(step.groupResultVoiceOver.length);
            }

            // Shared result: plays resultAnimator/resultAnimationClip/resultSoundEffect/resultVoiceOver
            // if the setup person configured them. Does nothing if not set.
            yield return PlayStoryResult(step);
        }

        RestoreTargetActivityTransformsForActivity(step);
    }

    private GameObject FindMatchingGroupTapObject(ActivityStep step, GameObject hitObject)
    {
        if (step == null || hitObject == null)
            return null;

        if (step.targetActions != null && step.targetActions.Count > 0)
        {
            for (int i = 0; i < step.targetActions.Count; i++)
            {
                ActivityTargetAction action = step.targetActions[i];
                if (action != null && action.enabled && IsTargetMatch(action.tapObject, hitObject))
                    return action.tapObject;
            }
        }

        if (step.groupTapObjects != null)
        {
            for (int i = 0; i < step.groupTapObjects.Count; i++)
            {
                if (IsTargetMatch(step.groupTapObjects[i], hitObject))
                    return step.groupTapObjects[i];
            }
        }

        if (step.targetObjects != null)
        {
            for (int i = 0; i < step.targetObjects.Count; i++)
            {
                if (IsTargetMatch(step.targetObjects[i], hitObject))
                    return step.targetObjects[i];
            }
        }

        if (IsTargetMatch(step.targetObject, hitObject))
            return step.targetObject;

        return null;
    }

    private bool IsGroupActionComplete(ActivityStep step)
    {
        if (step == null)
            return true;

        switch (step.groupCompletionMode)
        {
            case ActivityGroupCompletionMode.AllAllowedObjects:
                return _uniqueTappedGroupObjects.Count >= Mathf.Max(1, CountAllowedGroupTargets(step));

            case ActivityGroupCompletionMode.RequiredObjects:
                return AreRequiredGroupTargetsTapped(step);

            case ActivityGroupCompletionMode.RequiredObjectCount:
                return _uniqueTappedGroupObjects.Count >= Mathf.Max(1, step.groupRequiredObjectCount);

            case ActivityGroupCompletionMode.AnyAllowedObject:
            default:
                return _uniqueTappedGroupObjects.Count >= 1 || _acceptedInputCount >= 1;
        }
    }

    private int CountAllowedGroupTargets(ActivityStep step)
    {
        if (step.targetActions != null && step.targetActions.Count > 0)
        {
            int count = 0;
            for (int i = 0; i < step.targetActions.Count; i++)
            {
                if (step.targetActions[i] != null && step.targetActions[i].enabled && step.targetActions[i].tapObject != null)
                    count++;
            }
            return count;
        }

        if (step.groupTapObjects != null && step.groupTapObjects.Count > 0)
            return step.groupTapObjects.Count;

        if (step.targetObjects != null && step.targetObjects.Count > 0)
            return step.targetObjects.Count;

        return step.targetObject != null ? 1 : 0;
    }

    private bool AreRequiredGroupTargetsTapped(ActivityStep step)
    {
        bool foundRequired = false;

        if (step.targetActions != null && step.targetActions.Count > 0)
        {
            for (int i = 0; i < step.targetActions.Count; i++)
            {
                ActivityTargetAction action = step.targetActions[i];
                if (action == null || !action.enabled || !action.required)
                    continue;

                foundRequired = true;
                if (action.tapObject == null || !_uniqueTappedGroupObjects.Contains(action.tapObject))
                    return false;
            }
        }

        if (!foundRequired && step.groupRequiredObjects != null && step.groupRequiredObjects.Count > 0)
        {
            foundRequired = true;
            for (int i = 0; i < step.groupRequiredObjects.Count; i++)
            {
                GameObject required = step.groupRequiredObjects[i];
                if (required == null || !_uniqueTappedGroupObjects.Contains(required))
                    return false;
            }
        }

        return foundRequired && _uniqueTappedGroupObjects.Count > 0;
    }

    private IEnumerator PlayGroupActionsAndCount(ActivityStep step, GameObject tappedObject, Action onDone)
    {
        yield return PlayGroupActions(step, tappedObject);
        onDone?.Invoke();
    }

    private void UpdateGroupActionProgress(ActivityStep step)
    {
        if (step == null || activityPanel == null)
            return;

        int needed = 1;
        switch (step.groupCompletionMode)
        {
            case ActivityGroupCompletionMode.AllAllowedObjects:
                needed = Mathf.Max(1, CountAllowedGroupTargets(step));
                break;
            case ActivityGroupCompletionMode.RequiredObjects:
                needed = Mathf.Max(1, CountRequiredGroupTargets(step));
                break;
            case ActivityGroupCompletionMode.RequiredObjectCount:
                needed = Mathf.Max(1, step.groupRequiredObjectCount);
                break;
            case ActivityGroupCompletionMode.AnyAllowedObject:
            default:
                needed = 1;
                break;
        }

        int done = Mathf.Min(_uniqueTappedGroupObjects.Count, needed);
        if (ProgressBarFollowsInput(step)) activityPanel.ShowProgress(Mathf.Clamp01((float)done / needed), done + " / " + needed);
    }

    private int CountRequiredGroupTargets(ActivityStep step)
    {
        if (step == null)
            return 0;

        int count = 0;
        if (step.targetActions != null && step.targetActions.Count > 0)
        {
            for (int i = 0; i < step.targetActions.Count; i++)
            {
                ActivityTargetAction action = step.targetActions[i];
                if (action != null && action.enabled && action.required && action.tapObject != null)
                    count++;
            }
        }

        if (count == 0 && step.groupRequiredObjects != null)
        {
            for (int i = 0; i < step.groupRequiredObjects.Count; i++)
            {
                if (step.groupRequiredObjects[i] != null)
                    count++;
            }
        }

        return count;
    }

    private AudioSource StartGroupLoopSound(ActivityStep step)
    {
        if (step == null || !step.loopGroupSoundUntilGroupFinishes || step.groupLoopSound == null)
            return null;
        return CreateTempAudioSource(step.groupLoopSound, step.groupLoopSoundVolume, true);
    }

    private void StopGroupLoopSound(AudioSource source)
    {
        if (source != null)
            Destroy(source.gameObject);
    }

    private IEnumerator PlayGroupActions(ActivityStep step, GameObject tappedObject)
    {
        if (step == null) yield break;

        AudioSource groupLoopSource = StartGroupLoopSound(step);
        bool playedTargetSpecificAction = false;

        if (step.targetActions != null && step.targetActions.Count > 0)
        {
            List<ActivityTargetAction> actionsToPlayTogether = new List<ActivityTargetAction>();

            for (int i = 0; i < step.targetActions.Count; i++)
            {
                ActivityTargetAction action = step.targetActions[i];
                if (action == null || !action.enabled)
                    continue;

                bool shouldPlay = !step.groupPlayOnlyTappedObjectAction || tappedObject == null || IsTargetMatch(action.tapObject, tappedObject);
                if (!shouldPlay)
                    continue;

                actionsToPlayTogether.Add(action);
            }

            if (actionsToPlayTogether.Count > 0)
            {
                playedTargetSpecificAction = true;
                yield return PlayTargetActionsTogether(actionsToPlayTogether);
            }
        }

        if (!playedTargetSpecificAction && step.groupActions != null)
        {
            float waitTime = 0f;
            for (int i = 0; i < step.groupActions.Count; i++)
            {
                ActivityGroupAction action = step.groupActions[i];
                if (action == null || !action.enabled) continue;

                // Transform is already applied at activity start via ApplyGroupActivityTransformsAtStart.
                // Do not apply again here to avoid fighting the already-set position.

                if (action.soundEffect != null)
                    CreateTempAudioSource(action.soundEffect, action.soundVolume, false);

                if (action.voiceLine != null)
                {
                    AudioSource voice = CreateTempAudioSource(action.voiceLine, action.voiceVolume, false);
                    if (action.waitForVoiceLine && voice != null)
                        waitTime = Mathf.Max(waitTime, action.voiceLine.length);
                }

                if (action.animator != null && action.animationClip != null)
                {
                    float speed = Mathf.Max(0.01f, action.animationSpeed);
                    float length = Mathf.Max(0.01f, action.animationClip.length / speed);
                    if (CreateActivityAnimationGraph(action.animator, action.animationClip, speed, out PlayableGraph graph, out AnimationClipPlayable playable))
                    {
                        if (action.waitForAnimation || step.loopGroupSoundUntilGroupFinishes)
                            waitTime = Mathf.Max(waitTime, length);
                        StartCoroutine(DestroyGraphAfter(graph, length));
                    }
                }
            }

            if (waitTime > 0f)
                yield return new WaitForSeconds(waitTime);

            for (int i = 0; i < step.groupActions.Count; i++)
                RestoreGroupActivityTransform(step.groupActions[i]);
        }

        StopGroupLoopSound(groupLoopSource);
    }

    private IEnumerator PlayTargetActionsTogether(List<ActivityTargetAction> actions)
    {
        if (actions == null || actions.Count == 0)
            yield break;

        int running = 0;
        for (int i = 0; i < actions.Count; i++)
        {
            ActivityTargetAction action = actions[i];
            if (action == null || !action.enabled)
                continue;

            running++;
            StartCoroutine(PlayTargetActionAndCount(action, () => running--));
        }

        while (running > 0)
            yield return null;
    }

    private IEnumerator PlayTargetActionAndCount(ActivityTargetAction action, Action onDone)
    {
        yield return PlayTargetAction(action);
        onDone?.Invoke();
    }

    private IEnumerator PlayTargetAction(ActivityTargetAction action)
    {
        if (action == null) yield break;

        float waitTime = Mathf.Max(0f, action.extraWaitSeconds);

        // Target action transform is handled at activity start for group activities.
        // This prevents objects from jumping only after the tap and keeps them in activity pose while active.
        action.onTapped?.Invoke();

        if (action.objectsToTurnOff != null)
            ApplyObjectStateList(action.objectsToTurnOff, false);

        if (action.objectsToTurnOn != null)
            ApplyObjectStateList(action.objectsToTurnOn, true);

        if (action.soundEffect != null)
            CreateTempAudioSource(action.soundEffect, action.soundVolume, false);

        if (action.voiceOver != null)
        {
            AudioSource voice = CreateTempAudioSource(action.voiceOver, action.voiceVolume, false);
            if (action.waitForVoiceOver && voice != null)
                waitTime = Mathf.Max(waitTime, action.voiceOver.length);
        }

        if (action.animator != null && action.animationClip != null)
        {
            float speed = Mathf.Max(0.01f, action.animationSpeed);
            float length = Mathf.Max(0.01f, action.animationClip.length / speed);
            if (CreateActivityAnimationGraph(action.animator, action.animationClip, speed, out PlayableGraph graph, out AnimationClipPlayable playable))
            {
                if (action.waitForAnimation)
                    waitTime = Mathf.Max(waitTime, length);
                StartCoroutine(DestroyGraphAfter(graph, length));
            }
        }

        if (waitTime > 0f)
            yield return new WaitForSeconds(waitTime);

    }


    private void ApplyActivityOnlyTransformsAtActivityStart(ActivityStep step)
    {
        if (step == null)
            return;

        // Template-wide rule: every transform that is flagged as activity-only must apply
        // at the moment the activity starts, not mid-activity or on first tap.
        // This keeps story/VFX/popup free of activity poses before the child interacts.
        ApplyTargetActivityTransformsForActivity(step);
        ApplyGroupActivityTransformsAtStart(step);
    }

    // Applies group action transforms at activity start.
    // This matches target-action behavior and prevents models from jumping on the first tap.
    private void ApplyGroupActivityTransformsAtStart(ActivityStep step)
    {
        if (step == null || step.groupActions == null)
            return;

        for (int i = 0; i < step.groupActions.Count; i++)
        {
            ActivityGroupAction action = step.groupActions[i];
            if (action != null && action.useActivityTransform)
                ApplyGroupActivityTransform(action);
        }
    }

    private void ApplyTargetActivityTransformsForActivity(ActivityStep step)
    {
        if (step == null || step.targetActions == null)
            return;

        for (int i = 0; i < step.targetActions.Count; i++)
        {
            ActivityTargetAction action = step.targetActions[i];
            if (action == null || !action.enabled || !action.useActivityTransform)
                continue;

            ApplyTargetActivityTransform(action);
        }
    }

    private void RestoreTargetActivityTransformsForActivity(ActivityStep step)
    {
        if (step == null || step.targetActions == null)
            return;

        for (int i = 0; i < step.targetActions.Count; i++)
        {
            ActivityTargetAction action = step.targetActions[i];
            if (action == null)
                continue;

            RestoreTargetActivityTransform(action);
        }
    }

    private void RestoreAllActivityActionTransforms()
    {
        if (activities == null)
            return;

        for (int i = 0; i < activities.Count; i++)
        {
            ActivityStep step = activities[i];
            if (step == null)
                continue;

            RestoreTargetActivityTransformsForActivity(step);
            RestoreResultActivityTransform(step);

            if (step.choiceOptions != null)
            {
                for (int o = 0; o < step.choiceOptions.Count; o++)
                {
                    ActivityChoiceOption option = step.choiceOptions[o];
                    if (option == null || option.scenarioActions == null)
                        continue;

                    for (int a = 0; a < option.scenarioActions.Count; a++)
                        RestoreScenarioActivityTransformNow(option.scenarioActions[a]);
                }
            }

            if (step.groupActions != null)
            {
                for (int g = 0; g < step.groupActions.Count; g++)
                    RestoreGroupActivityTransform(step.groupActions[g]);
            }

            if (step.reactions != null)
            {
                for (int r = 0; r < step.reactions.Count; r++)
                    RestoreReactionActivityTransform(step.reactions[r]);
            }
        }
    }

    private IEnumerator PlayStoryResult(ActivityStep step)
    {
        if (step == null) yield break;

        ShowProgressIfResultMode(step, 1f, "Done");

        // Play result animation start sound immediately when result begins
        PlayResultAnimationStartSound(step);

        bool resultTransformApplied = false;
        if (step.resultUseActivityTransform)
        {
            ApplyResultActivityTransform(step);
            resultTransformApplied = true;
        }

        float waitTime = Mathf.Max(0f, step.resultExtraWaitSeconds);

        if (step.resultSoundEffect != null)
        {
            AudioSource sound = CreateTempAudioSource(step.resultSoundEffect, step.resultSoundVolume, false);
            if (step.waitForResultSound && sound != null)
                waitTime = Mathf.Max(waitTime, step.resultSoundEffect.length);
        }

        if (step.resultVoiceOver != null)
        {
            AudioSource voice = CreateTempAudioSource(step.resultVoiceOver, step.resultVoiceVolume, false);
            if (step.waitForResultVoiceOver && voice != null)
                waitTime = Mathf.Max(waitTime, step.resultVoiceOver.length);
        }

        if (step.resultAnimator != null && step.resultAnimationClip != null)
        {
            float speed = Mathf.Max(0.01f, step.resultAnimationSpeed);
            float length = Mathf.Max(0.01f, step.resultAnimationClip.length / speed);
            if (CreateActivityAnimationGraph(step.resultAnimator, step.resultAnimationClip, speed, out PlayableGraph graph, out AnimationClipPlayable playable))
            {
                if (step.waitForResultAnimation)
                    waitTime = Mathf.Max(waitTime, length);
                StartCoroutine(DestroyGraphAfter(graph, length));
            }
        }

        if (waitTime > 0f)
            yield return new WaitForSeconds(waitTime);

        if (resultTransformApplied)
            RestoreResultActivityTransform(step);
    }

    private IEnumerator PlayGroupActions(ActivityStep step)
    {
        if (step == null) yield break;

        AudioSource groupLoopSource = StartGroupLoopSound(step);
        float waitTime = Mathf.Max(0f, step.groupWaitSecondsBeforeStory);

        if (step.groupResultVoiceOver != null)
        {
            AudioSource voice = CreateTempAudioSource(step.groupResultVoiceOver, step.groupResultVoiceVolume, false);
            if (step.groupWaitForVoiceOver && voice != null)
                waitTime = Mathf.Max(waitTime, step.groupResultVoiceOver.length);
        }

        if (step.groupActions != null)
        {
            for (int i = 0; i < step.groupActions.Count; i++)
            {
                ActivityGroupAction action = step.groupActions[i];
                if (action == null || !action.enabled) continue;

                // Transform already applied at activity start. Do not re-apply here.

                if (action.soundEffect != null)
                    CreateTempAudioSource(action.soundEffect, action.soundVolume, false);

                if (action.voiceLine != null)
                {
                    AudioSource voice = CreateTempAudioSource(action.voiceLine, action.voiceVolume, false);
                    if (action.waitForVoiceLine && voice != null)
                        waitTime = Mathf.Max(waitTime, action.voiceLine.length);
                }

                if (action.animator != null && action.animationClip != null)
                {
                    float speed = Mathf.Max(0.01f, action.animationSpeed);
                    float length = Mathf.Max(0.01f, action.animationClip.length / speed);
                    if (CreateActivityAnimationGraph(action.animator, action.animationClip, speed, out PlayableGraph graph, out AnimationClipPlayable playable))
                    {
                        if (action.waitForAnimation || step.loopGroupSoundUntilGroupFinishes)
                            waitTime = Mathf.Max(waitTime, length);
                        StartCoroutine(DestroyGraphAfter(graph, length));
                    }
                }
            }
        }

        if (waitTime > 0f)
            yield return new WaitForSeconds(waitTime);

        StopGroupLoopSound(groupLoopSource);

        if (step.groupActions != null)
        {
            for (int i = 0; i < step.groupActions.Count; i++)
                RestoreGroupActivityTransform(step.groupActions[i]);
        }
    }

    private bool ShouldAutoPlayResultAfterNoInput(ActivityStep step)
    {
        return step != null && step.noInputActionAfterHint == ActivityNoInputAction.AutoPlayResultThenContinue;
    }

    private bool ShouldSkipActivityAfterNoInput(ActivityStep step)
    {
        return step != null && step.noInputActionAfterHint == ActivityNoInputAction.SkipActivityAndContinue;
    }

    private IEnumerator AutoPlayResultBecauseChildDidNothing(ActivityStep step, float helperStartProgress = 0f)
    {
        if (step == null) yield break;

        // No input means skip waiting for the child, not skip the activity.
        // Run only the activity result path. Do not directly start story animation here.
        ClearInput();
        activityPanel?.HideButtons();

        _acceptedInputCount = Mathf.Max(_acceptedInputCount, Mathf.Max(1, step.requiredInputCount));
        _visibleProgressValue = 1f;
        UpdateProgress(step);
        ShowProgressIfResultMode(step, 1f, "Auto");

        // If this is a progress/tapping activity, the helper animations are part of the activity result.
        // On no input, play them once in order before continuing. Do not touch story animation here.
        if (ProgressHelperShouldPlayAsSequence(step))
            yield return PlayProgressHelperAnimationsOnce(step);
        else if (ProgressHelperUsesProgressPercent(step))
            yield return PlayProgressHelperAnimationsFromProgressToEnd(step, helperStartProgress);

        // Prefer the new no-coder Result Actions.
        // Run old legacy result fields only when no result actions are configured, so no-input cannot accidentally replay story-side animation.
        bool includeLegacyResult = !HasRunnableActivityResultReactions(step);
        yield return PlayConfiguredActivityResult(step, includeLegacyResult);
    }

    private bool HasRunnableActivityResultReactions(ActivityStep step)
    {
        if (step == null || step.reactions == null) return false;
        for (int i = 0; i < step.reactions.Count; i++)
        {
            ActivityReaction reaction = step.reactions[i];
            if (reaction == null || !reaction.enabled) continue;
            if (reaction.playWhen == ActivityReactionMoment.EveryValidInput ||
                reaction.playWhen == ActivityReactionMoment.IfReactionIsFree)
                return true;
        }
        return false;
    }

    private bool ShouldPlayResultOnEachInput(ActivityStep step)
    {
        if (step == null) return true;

        if (step.resultPlayTiming == ActivityResultPlayTiming.OnEveryCorrectInput ||
            step.resultPlayTiming == ActivityResultPlayTiming.WhileChildIsInteracting)
            return true;

        // Backward-compatible safety for simple tap activities.
        // If the activity completes by time, there is no required-input finish point,
        // so Every Valid Input reactions must still run when the child taps.
        // This fixes activities like King Welcome: tap screen -> petals/animation play.
        if (step.resultPlayTiming == ActivityResultPlayTiming.AfterRequiredInputs &&
            step.finishWhen == ActivityFinishRule.AfterActiveTimeEnds)
            return true;

        return false;
    }

    private bool ShouldPlayResultAfterActivityInput(ActivityStep step)
    {
        if (step == null) return false;
        return step.resultPlayTiming == ActivityResultPlayTiming.AfterRequiredInputs ||
               step.resultPlayTiming == ActivityResultPlayTiming.WhenProgressIsFull ||
               step.resultPlayTiming == ActivityResultPlayTiming.AfterNoInputAutoPlay;
    }

    private IEnumerator PlayConfiguredActivityResult(ActivityStep step, bool includeLegacyResult)
    {
        if (step == null) yield break;

        bool startedBlockingReaction = false;
        startedBlockingReaction |= RunReactions(step, ActivityReactionMoment.EveryValidInput);
        startedBlockingReaction |= RunReactions(step, ActivityReactionMoment.IfReactionIsFree);

        if (includeLegacyResult)
            yield return PlayStoryResult(step);

        if (startedBlockingReaction || step.waitForRunningReactionsBeforeFinish)
            yield return WaitForRunningReactions(step);
    }

    private IEnumerator RunInputActivity(ActivityStep step)
    {
        bool complete = false;
        float startedAt = Time.time;
        bool noInputHintShown = false;
        bool autoPlayedResultAlready = false;
        float noInputHintShownAt = 0f;
        UpdateInputTimeProgress(step, startedAt);
        ResetVisibleProgress(step);

        BeginInput(step, data => IsInputValidForStep(step, data), data =>
        {
            if (_inputCycleLocked)
                return;

            _acceptedInputCount++;
            bool startedBlockingReaction = false;
            // Play correct tap sound for all generic tap activity types.
            // ProgressGate and WaitForStoryThenTapObject have their own dedicated tap sound fields.
            TryPlayCorrectTapSound(step);
            // A reaction marked Every Valid Input must run when a valid input happens.
            startedBlockingReaction |= RunReactions(step, ActivityReactionMoment.EveryValidInput);
            startedBlockingReaction |= RunReactions(step, ActivityReactionMoment.IfReactionIsFree);
            UpdateProgress(step);

            if (step.nextInputRule == ActivityNextInputRule.AfterReactionFinishes && startedBlockingReaction)
                LockInputUntilBlockingReactionsFinish(step);
            else if (step.nextInputRule == ActivityNextInputRule.AfterFixedDelay && step.nextInputDelaySeconds > 0f)
                LockInputForSeconds(step.nextInputDelaySeconds);

            if (ShouldFinishFromInput(step))
                complete = true;
        }, data =>
        {
            HandleWrongInput(step);
        });

        if (step.childInput == ActivityInputKind.TapButton || step.childInput == ActivityInputKind.TapAnywhereOrButton)
        {
            activityPanel?.ShowButtons(GetButtonLabels(step), index =>
            {
                StoryActivityInputRouter.Broadcast(new ActivityInputData
                {
                    type = ActivityInputType.UIButton,
                    optionIndex = index
                });
            });
        }

        while (!complete)
        {
            UpdateInputTimeProgress(step, startedAt);
            UpdateProgressIdleBehavior(step);

            if (step.finishWhen == ActivityFinishRule.AfterActiveTimeEnds && step.activeTimeSeconds > 0f)
            {
                if (Time.time - startedAt >= step.activeTimeSeconds)
                {
                    complete = true;
                    break;
                }
            }

            if (_acceptedInputCount == 0 && step.enableNoInputHelp && step.noInputHintAfterSeconds > 0f && !noInputHintShown)
            {
                if (Time.time - startedAt >= step.noInputHintAfterSeconds)
                {
                    noInputHintShown = true;
                    noInputHintShownAt = Time.time;
                    ShowNoInputHint(step);
                }
            }

            if (_acceptedInputCount == 0 && noInputHintShown && (ShouldAutoPlayResultAfterNoInput(step) || ShouldSkipActivityAfterNoInput(step)))
            {
                float skipWait = Mathf.Max(0f, step.autoSkipAfterHintSeconds);
                if (Time.time - noInputHintShownAt >= skipWait)
                {
                    if (ShouldAutoPlayResultAfterNoInput(step))
                    {
                        yield return AutoPlayResultBecauseChildDidNothing(step);
                        autoPlayedResultAlready = true;
                    }
                    complete = true;
                    break;
                }
            }

            if (step.maxTimeToFirstInput > 0f && _acceptedInputCount == 0 && Time.time - startedAt >= step.maxTimeToFirstInput)
            {
                if (step.retryIfFailed)
                    startedAt = Time.time;
                else
                    complete = true;

                HandleWrongInput(step);
            }

            yield return null;
        }

        ClearInput();
        activityPanel?.HideButtons();

        if (!autoPlayedResultAlready && ShouldPlayResultAfterActivityInput(step))
            yield return PlayConfiguredActivityResult(step, true);
        else if (step.waitForRunningReactionsBeforeFinish)
            yield return WaitForRunningReactions(step);
    }

    private IEnumerator RunChoiceActivity(ActivityStep step)
    {
        _choiceAnswered = false;
        _choiceCorrect = false;
        StopAllChoiceScenarioRoutines();
        StopAllChoiceGraphs();

        bool choiceBusy = false;
        IList<string> labels = GetButtonLabels(step);
        bool[] disabledOptions = new bool[labels != null ? labels.Count : 0];

        Action<int> clickHandler = null;
        clickHandler = index =>
        {
            if (choiceBusy && step.choiceBlockInputWhileResultPlays)
                return;
            if (index < 0 || index >= disabledOptions.Length)
                return;
            if (disabledOptions[index])
                return;

            choiceBusy = true;

            if (step.choiceHideUiWhileResultPlays)
                HideChoiceUIForScenario();

            StartCoroutine(HandleChoiceSelection(step, index, resultCorrect =>
            {
                _choiceIndex = index;
                _choiceAnswered = true;
                _choiceCorrect = resultCorrect;
                choiceBusy = false;

                if (!resultCorrect)
                {
                    // For Choose Correct Option, wrong answers never complete the activity.
                    // They only play the selected scenario, then return the question UI.
                    if (step.choiceWrongOptionBehaviour == ActivityChoiceWrongOptionBehaviour.DisableAndGrayOut && index >= 0 && index < disabledOptions.Length)
                        disabledOptions[index] = true;

                    _choiceAnswered = false;

                    if (step.choiceReturnQuestionAfterWrong)
                        ShowChoiceUIAfterWrong(step, labels, clickHandler, disabledOptions);
                }
            }));
        };

        bool grayDisabledInitial = step.choiceWrongOptionBehaviour == ActivityChoiceWrongOptionBehaviour.DisableAndGrayOut;
        _activeChoiceStep = step;
        _activeChoiceLabels = labels;
        _activeChoiceDisabledOptions = disabledOptions;
        _activeChoiceClickHandler = clickHandler;
        activityPanel?.ShowChoiceButtons(labels, clickHandler, disabledOptions, grayDisabledInitial);

        while (true)
        {
            if (_choiceAnswered && _choiceCorrect && !choiceBusy)
                break;
            yield return null;
        }

        HideChoiceUIForScenario();
        ClearActiveChoiceRestoreState();

        // Stop all choice animation graphs before story resumes.
        // The animation already finished fully above (forceWaitForScenario: true).
        // This is a safety cleanup in case any graph is still referenced.
        StopAllChoiceGraphs();
        StopAllChoiceScenarioRoutines();

        if (step.waitForRunningReactionsBeforeFinish)
            yield return WaitForRunningReactions(step);
    }

    private bool ShouldPlayChoiceOptionResult(ActivityStep step, ActivityChoiceOption option, bool correct)
    {
        if (option == null || !option.playResultForThisOption)
            return false;

        if (correct && step.choiceCorrectBehaviour == ActivityChoiceCorrectBehaviour.ContinueStoryImmediately)
            return false;

        return true;
    }

    private IEnumerator HandleChoiceSelection(ActivityStep step, int index, Action<bool> onDone)
    {
        bool correct = IsChoiceCorrect(step, index);
        ActivityChoiceOption option = GetChoiceOption(step, index);

        if (correct)
        {
            _acceptedInputCount++;
            RunReactions(step, ActivityReactionMoment.EveryValidInput);
            RunReactions(step, ActivityReactionMoment.IfReactionIsFree);
        }
        else
        {
            RunReactions(step, ActivityReactionMoment.WhenInputFails);
        }

        if (ShouldPlayChoiceOptionResult(step, option, correct))
        {
            // Every selected option — correct or wrong — must fully finish its animation,
            // voice, and sound before the system moves to the next step.
            // Wrong options return the question after finishing.
            // Correct options continue the story after finishing.
            // If a correct option has no animation and "Continue Story Immediately" is selected,
            // ShouldPlayChoiceOptionResult returns false above and this line never runs.
            yield return PlayChoiceOption(option, correct, forceWaitForScenario: true);
        }

        if (!correct && !string.IsNullOrWhiteSpace(step.tryAgainMessage))
            ShowQuickFeedback(step.tryAgainMessage);

        onDone?.Invoke(correct);
    }

    private ActivityChoiceOption GetChoiceOption(ActivityStep step, int index)
    {
        if (step != null && step.choiceOptions != null && index >= 0 && index < step.choiceOptions.Count)
            return step.choiceOptions[index];

        return null;
    }

    private bool IsChoiceCorrect(ActivityStep step, int index)
    {
        if (step != null && step.choiceOptions != null && step.choiceOptions.Count > 0)
        {
            ActivityChoiceOption option = GetChoiceOption(step, index);
            return option != null && option.isCorrect;
        }

        return step != null && index == step.correctOptionIndex;
    }

    private IEnumerator PlayChoiceOption(ActivityChoiceOption option, bool correct, bool forceWaitForScenario)
    {
        if (option == null) yield break;

        // Old custom events are allowed only for the correct option path.
        // This prevents wrong options from accidentally starting the full story.
        if (correct)
            option.onSelected?.Invoke();

        // New modular scenario flow. If actions are added, these actions fully control this option result.
        if (option.scenarioActions != null && option.scenarioActions.Count > 0)
        {
            int repeat = Mathf.Max(1, option.scenarioRepeatCount);
            for (int r = 0; r < repeat; r++)
            {
                if (option.scenarioPlayMode == ActivityScenarioPlayMode.Together)
                    yield return PlayChoiceScenarioActionsTogether(option.scenarioActions, allowCustomEvents: correct, forceWaitForScenario: forceWaitForScenario);
                else
                    yield return PlayChoiceScenarioActionsOneByOne(option.scenarioActions, allowCustomEvents: correct, forceWaitForScenario: forceWaitForScenario);
            }

            if (option.extraWaitSeconds > 0f)
                yield return new WaitForSeconds(option.extraWaitSeconds);
            yield break;
        }

        // Legacy single-result support. Existing projects still work if they already used these fields.
        ActivityScenarioAction legacy = new ActivityScenarioAction
        {
            enabled = true,
            actionName = "Legacy Result",
            animator = option.animator,
            animationClip = option.animationClip,
            animationSpeed = option.animationSpeed,
            animationLoopCount = 1,
            waitForAnimation = option.waitForAnimation,
            soundEffect = option.soundEffect,
            soundVolume = option.soundVolume,
            voiceOver = option.voiceOver,
            voiceVolume = option.voiceVolume,
            waitForVoiceOver = option.waitForVoiceOver,
            narration = option.narration,
            narrationVolume = option.narrationVolume,
            waitForNarration = option.waitForNarration,
            extraWaitSeconds = option.extraWaitSeconds,
            onActionPlayed = null
        };

        yield return PlayChoiceScenarioAction(legacy, allowCustomEvents: correct, forceWaitForScenario: forceWaitForScenario);
    }

    private IEnumerator PlayChoiceScenarioActionsOneByOne(List<ActivityScenarioAction> actions, bool allowCustomEvents, bool forceWaitForScenario)
    {
        for (int i = 0; i < actions.Count; i++)
        {
            ActivityScenarioAction action = actions[i];
            if (action == null || !action.enabled) continue;
            yield return PlayChoiceScenarioAction(action, allowCustomEvents, forceWaitForScenario);
        }
    }

    private IEnumerator PlayChoiceScenarioActionsTogether(List<ActivityScenarioAction> actions, bool allowCustomEvents, bool forceWaitForScenario)
    {
        float maxWait = 0f;

        for (int i = 0; i < actions.Count; i++)
        {
            ActivityScenarioAction action = actions[i];
            if (action == null || !action.enabled) continue;
            float wait = PlayChoiceScenarioActionImmediate(action, allowCustomEvents, forceWaitForScenario);
            maxWait = Mathf.Max(maxWait, wait);
        }

        if (maxWait > 0f)
            yield return new WaitForSeconds(maxWait);
    }

    private IEnumerator PlayChoiceScenarioAction(ActivityScenarioAction action, bool allowCustomEvents, bool forceWaitForScenario)
    {
        float waitTime = PlayChoiceScenarioActionImmediate(action, allowCustomEvents, forceWaitForScenario);
        if (waitTime > 0f)
            yield return new WaitForSeconds(waitTime);
    }

    private float PlayChoiceScenarioActionImmediate(ActivityScenarioAction action, bool allowCustomEvents, bool forceWaitForScenario)
    {
        if (action == null || !action.enabled) return 0f;

        float waitTime = Mathf.Max(0f, action.extraWaitSeconds);
        if (allowCustomEvents)
            action.onActionPlayed?.Invoke();

        ApplyScenarioActivityTransform(action, waitTime);

        if (action.objectsToTurnOff != null)
            ApplyObjectStateList(action.objectsToTurnOff, false);
        if (action.objectsToTurnOn != null)
            ApplyObjectStateList(action.objectsToTurnOn, true);

        if (action.soundEffect != null)
            CreateTempAudioSource(action.soundEffect, action.soundVolume, false);

        if (action.voiceOver != null)
        {
            AudioSource voice = CreateTempAudioSource(action.voiceOver, action.voiceVolume, false);
            if ((action.waitForVoiceOver || forceWaitForScenario) && voice != null)
                waitTime = Mathf.Max(waitTime, action.voiceOver.length);
        }

        if (action.narration != null)
        {
            AudioSource narration = CreateTempAudioSource(action.narration, action.narrationVolume, false);
            if ((action.waitForNarration || forceWaitForScenario) && narration != null)
                waitTime = Mathf.Max(waitTime, action.narration.length);
        }

        if (action.animator != null && action.animationClip != null)
        {
            float speed = Mathf.Max(0.01f, action.animationSpeed);
            int loops = Mathf.Max(1, action.animationLoopCount);
            float singleLength = Mathf.Max(0.01f, action.animationClip.length / speed);
            float totalLength = singleLength * loops;

            StartTrackedChoiceAnimationLoop(action.animator, action.animationClip, speed, loops, singleLength);

            if (action.waitForAnimation || forceWaitForScenario)
                waitTime = Mathf.Max(waitTime, totalLength);
        }

        ScheduleScenarioTransformRestore(action, waitTime);
        return waitTime;
    }


    private void ApplyResultActivityTransform(ActivityStep step)
    {
        if (step == null || !step.resultUseActivityTransform)
            return;

        Transform target = ResolveActivityTransformTarget(step.resultObjectToMoveOrScale, step.resultAnimator);
        if (target == null)
            return;

        StoreResultStoryPoseIfNeeded(step, target);

        if (!step._resultHasStoredTransform)
        {
            step._resultOriginalLocalPosition = target.localPosition;
            step._resultOriginalLocalEulerAngles = target.localEulerAngles;
            step._resultOriginalLocalScale = target.localScale;
            step._resultHasStoredTransform = true;
        }

        if (step.resultCopyTransformFrom != null)
        {
            target.position = step.resultCopyTransformFrom.position;
            target.rotation = step.resultCopyTransformFrom.rotation;
            target.localScale = SafeActivityScale(step.resultCopyTransformFrom.localScale);
            return;
        }

        target.localPosition = step.resultActivityPosition;
        target.localEulerAngles = step.resultActivityRotationEuler;
        target.localScale = SafeActivityScale(step.resultActivityScale);
    }

    private void RestoreResultActivityTransform(ActivityStep step)
    {
        if (step == null || !step.resultRestoreTransformAfterAction)
            return;
        if (!step.resultUseActivityTransform && !step._resultHasStoredTransform && !step.resultHasSavedStoryPose)
            return;

        Transform target = ResolveActivityTransformTarget(step.resultObjectToMoveOrScale, step.resultAnimator);
        if (target == null)
            return;

        if (step.resultHasSavedStoryPose)
        {
            RestoreStoryPose(target, true, step.resultStoryPosition, step.resultStoryRotationEuler, step.resultStoryScale);
        }
        else if (step._resultHasStoredTransform)
        {
            target.localPosition = step._resultOriginalLocalPosition;
            target.localEulerAngles = step._resultOriginalLocalEulerAngles;
            target.localScale = step._resultOriginalLocalScale;
        }

        step._resultHasStoredTransform = false;
    }


    private void ApplyTargetActivityTransform(ActivityTargetAction action)
    {
        if (action == null || !action.useActivityTransform)
            return;

        Transform target = ResolveActivityTransformTarget(action.objectToMoveOrScale, action.animator);
        if (target == null)
            return;

        StoreStoryPoseIfNeeded(action, target);

        if (!action._hasStoredTransform)
        {
            action._originalLocalPosition = target.localPosition;
            action._originalLocalEulerAngles = target.localEulerAngles;
            action._originalLocalScale = target.localScale;
            action._hasStoredTransform = true;
        }

        if (action.copyTransformFrom != null)
        {
            target.position = action.copyTransformFrom.position;
            target.rotation = action.copyTransformFrom.rotation;
            target.localScale = action.copyTransformFrom.localScale;
            return;
        }

        target.localPosition = action.activityPosition;
        target.localEulerAngles = action.activityRotationEuler;
        target.localScale = SafeActivityScale(action.activityScale);
    }

    private void RestoreTargetActivityTransform(ActivityTargetAction action)
    {
        if (action == null || !action.restoreTransformAfterAction)
            return;
        if (!action.useActivityTransform && !action._hasStoredTransform && !action.hasSavedStoryPose)
            return;

        Transform target = ResolveActivityTransformTarget(action.objectToMoveOrScale, action.animator);
        if (target == null)
            return;

        if (action.hasSavedStoryPose)
        {
            RestoreStoryPose(target, true, action.storyPosition, action.storyRotationEuler, action.storyScale);
        }
        else if (action._hasStoredTransform)
        {
            target.localPosition = action._originalLocalPosition;
            target.localEulerAngles = action._originalLocalEulerAngles;
            target.localScale = action._originalLocalScale;
        }

        action._hasStoredTransform = false;
    }

    private void ApplyScenarioActivityTransform(ActivityScenarioAction action, float currentWaitTime)
    {
        if (action == null || !action.useActivityTransform)
            return;

        Transform target = ResolveActivityTransformTarget(action.objectToMoveOrScale, action.animator);
        if (target == null)
            return;

        StoreStoryPoseIfNeeded(action, target);

        if (!action._hasStoredTransform)
        {
            action._originalLocalPosition = target.localPosition;
            action._originalLocalEulerAngles = target.localEulerAngles;
            action._originalLocalScale = target.localScale;
            action._hasStoredTransform = true;
        }

        if (action.copyTransformFrom != null)
        {
            target.position = action.copyTransformFrom.position;
            target.rotation = action.copyTransformFrom.rotation;
            target.localScale = action.copyTransformFrom.localScale;
            return;
        }

        target.localPosition = action.activityPosition;
        target.localEulerAngles = action.activityRotationEuler;
        target.localScale = SafeActivityScale(action.activityScale);
    }

    private void ScheduleScenarioTransformRestore(ActivityScenarioAction action, float waitSeconds)
    {
        if (action == null || !action.useActivityTransform || !action.restoreTransformAfterAction || !action._hasStoredTransform)
            return;

        Transform target = ResolveActivityTransformTarget(action.objectToMoveOrScale, action.animator);
        if (target == null)
            return;

        Vector3 restorePosition = action.hasSavedStoryPose ? action.storyPosition : action._originalLocalPosition;
        Vector3 restoreRotation = action.hasSavedStoryPose ? action.storyRotationEuler : action._originalLocalEulerAngles;
        Vector3 restoreScale = action.hasSavedStoryPose ? action.storyScale : action._originalLocalScale;

        // Track restore coroutine so StopAllChoiceScenarioRoutines can cancel it on fast replay.
        // Without tracking, fast replay stops the coroutine but the restore never runs, leaving model stuck.
        Coroutine c = StartCoroutine(RestoreTransformAfterDelay(target, restorePosition, restoreRotation, restoreScale, waitSeconds));
        if (c != null)
            _scenarioTransformRestoreRoutines.Add(c);

        action._hasStoredTransform = false;
    }

    private void RestoreScenarioActivityTransformNow(ActivityScenarioAction action)
    {
        if (action == null || !action.restoreTransformAfterAction)
            return;
        if (!action.useActivityTransform && !action._hasStoredTransform && !action.hasSavedStoryPose)
            return;

        Transform target = ResolveActivityTransformTarget(action.objectToMoveOrScale, action.animator);
        if (target == null)
            return;

        if (action.hasSavedStoryPose)
        {
            RestoreStoryPose(target, true, action.storyPosition, action.storyRotationEuler, action.storyScale);
        }
        else if (action._hasStoredTransform)
        {
            target.localPosition = action._originalLocalPosition;
            target.localEulerAngles = action._originalLocalEulerAngles;
            target.localScale = action._originalLocalScale;
        }

        action._hasStoredTransform = false;
    }


    private void ApplyGroupActivityTransform(ActivityGroupAction action)
    {
        if (action == null || !action.useActivityTransform)
            return;

        Transform target = ResolveActivityTransformTarget(action.objectToMoveOrScale, action.animator);
        if (target == null)
            return;

        StoreStoryPoseIfNeeded(action, target);

        if (!action._hasStoredTransform)
        {
            action._originalLocalPosition = target.localPosition;
            action._originalLocalEulerAngles = target.localEulerAngles;
            action._originalLocalScale = target.localScale;
            action._hasStoredTransform = true;
        }

        if (action.copyTransformFrom != null)
        {
            target.position = action.copyTransformFrom.position;
            target.rotation = action.copyTransformFrom.rotation;
            target.localScale = action.copyTransformFrom.localScale;
            return;
        }

        target.localPosition = action.activityPosition;
        target.localEulerAngles = action.activityRotationEuler;
        target.localScale = SafeActivityScale(action.activityScale);
    }

    private void RestoreGroupActivityTransform(ActivityGroupAction action)
    {
        if (action == null || !action.restoreTransformAfterAction)
            return;
        if (!action.useActivityTransform && !action._hasStoredTransform && !action.hasSavedStoryPose)
            return;

        Transform target = ResolveActivityTransformTarget(action.objectToMoveOrScale, action.animator);
        if (target == null)
            return;

        if (action.hasSavedStoryPose)
        {
            RestoreStoryPose(target, true, action.storyPosition, action.storyRotationEuler, action.storyScale);
        }
        else if (action._hasStoredTransform)
        {
            target.localPosition = action._originalLocalPosition;
            target.localEulerAngles = action._originalLocalEulerAngles;
            target.localScale = action._originalLocalScale;
        }

        action._hasStoredTransform = false;
    }

    private void ApplyReactionActivityTransform(ActivityReaction reaction)
    {
        if (reaction == null || !reaction.useActivityTransform)
            return;

        Transform target = ResolveActivityTransformTarget(reaction.objectToMoveOrScale, reaction.animator);
        if (target == null)
            return;

        StoreStoryPoseIfNeeded(reaction, target);

        if (!reaction._hasStoredTransform)
        {
            reaction._originalLocalPosition = target.localPosition;
            reaction._originalLocalEulerAngles = target.localEulerAngles;
            reaction._originalLocalScale = target.localScale;
            reaction._hasStoredTransform = true;
        }

        if (reaction.copyTransformFrom != null)
        {
            target.position = reaction.copyTransformFrom.position;
            target.rotation = reaction.copyTransformFrom.rotation;
            target.localScale = reaction.copyTransformFrom.localScale;
            return;
        }

        target.localPosition = reaction.activityPosition;
        target.localEulerAngles = reaction.activityRotationEuler;
        target.localScale = SafeActivityScale(reaction.activityScale);
    }

    private void RestoreReactionActivityTransform(ActivityReaction reaction)
    {
        if (reaction == null || !reaction.restoreTransformAfterAction)
            return;
        if (!reaction.useActivityTransform && !reaction._hasStoredTransform && !reaction.hasSavedStoryPose)
            return;

        Transform target = ResolveActivityTransformTarget(reaction.objectToMoveOrScale, reaction.animator);
        if (target == null)
            return;

        if (reaction.hasSavedStoryPose)
        {
            RestoreStoryPose(target, true, reaction.storyPosition, reaction.storyRotationEuler, reaction.storyScale);
        }
        else if (reaction._hasStoredTransform)
        {
            target.localPosition = reaction._originalLocalPosition;
            target.localEulerAngles = reaction._originalLocalEulerAngles;
            target.localScale = reaction._originalLocalScale;
        }

        reaction._hasStoredTransform = false;
    }

    private Transform ResolveActivityTransformTarget(GameObject objectToMoveOrScale, Animator animator)
    {
        if (objectToMoveOrScale != null)
            return objectToMoveOrScale.transform;
        if (animator != null)
            return animator.transform;
        return null;
    }

    private static Vector3 SafeActivityScale(Vector3 value)
    {
        return value == Vector3.zero ? Vector3.one : value;
    }

    private static bool IsValidActivityScale(Vector3 value)
    {
        return !Mathf.Approximately(value.x, 0f) && !Mathf.Approximately(value.y, 0f) && !Mathf.Approximately(value.z, 0f);
    }

    /// <summary>
    /// Clears all saved story poses across every activity, action, and reaction.
    /// Must be called before Awake restore and before replay so the next capture
    /// always reads the current object positions, not a stale pose from a previous session.
    /// </summary>
    private void ClearAllSavedStoryPoses()
    {
        if (activities == null) return;

        for (int i = 0; i < activities.Count; i++)
        {
            ActivityStep step = activities[i];
            if (step == null) continue;

            // Result transform
            step.resultHasSavedStoryPose = false;

            // Target actions
            if (step.targetActions != null)
                for (int j = 0; j < step.targetActions.Count; j++)
                    if (step.targetActions[j] != null)
                        step.targetActions[j].hasSavedStoryPose = false;

            // Choice option scenario actions
            if (step.choiceOptions != null)
                for (int j = 0; j < step.choiceOptions.Count; j++)
                {
                    ActivityChoiceOption opt = step.choiceOptions[j];
                    if (opt?.scenarioActions == null) continue;
                    for (int k = 0; k < opt.scenarioActions.Count; k++)
                        if (opt.scenarioActions[k] != null)
                            opt.scenarioActions[k].hasSavedStoryPose = false;
                }

            // Group actions
            if (step.groupActions != null)
                for (int j = 0; j < step.groupActions.Count; j++)
                    if (step.groupActions[j] != null)
                        step.groupActions[j].hasSavedStoryPose = false;

            // Reactions
            if (step.reactions != null)
                for (int j = 0; j < step.reactions.Count; j++)
                    if (step.reactions[j] != null)
                        step.reactions[j].hasSavedStoryPose = false;
        }
    }

    private static void StoreStoryPoseIfNeeded(ActivityTargetAction action, Transform target)
    {
        if (action == null || target == null || action.hasSavedStoryPose) return;
        action.storyPosition = target.localPosition;
        action.storyRotationEuler = target.localEulerAngles;
        action.storyScale = target.localScale;
        action.hasSavedStoryPose = true;
    }

    private static void StoreStoryPoseIfNeeded(ActivityScenarioAction action, Transform target)
    {
        if (action == null || target == null || action.hasSavedStoryPose) return;
        action.storyPosition = target.localPosition;
        action.storyRotationEuler = target.localEulerAngles;
        action.storyScale = target.localScale;
        action.hasSavedStoryPose = true;
    }

    private static void StoreStoryPoseIfNeeded(ActivityGroupAction action, Transform target)
    {
        if (action == null || target == null || action.hasSavedStoryPose) return;
        action.storyPosition = target.localPosition;
        action.storyRotationEuler = target.localEulerAngles;
        action.storyScale = target.localScale;
        action.hasSavedStoryPose = true;
    }

    private static void StoreStoryPoseIfNeeded(ActivityReaction reaction, Transform target)
    {
        if (reaction == null || target == null || reaction.hasSavedStoryPose) return;
        reaction.storyPosition = target.localPosition;
        reaction.storyRotationEuler = target.localEulerAngles;
        reaction.storyScale = target.localScale;
        reaction.hasSavedStoryPose = true;
    }

    private static void StoreResultStoryPoseIfNeeded(ActivityStep step, Transform target)
    {
        if (step == null || target == null || step.resultHasSavedStoryPose) return;
        step.resultStoryPosition = target.localPosition;
        step.resultStoryRotationEuler = target.localEulerAngles;
        step.resultStoryScale = target.localScale;
        step.resultHasSavedStoryPose = true;
    }

    private static void RestoreStoryPose(Transform target, bool hasStoryPose, Vector3 storyPosition, Vector3 storyRotationEuler, Vector3 storyScale)
    {
        if (target == null || !hasStoryPose) return;
        target.localPosition = storyPosition;
        target.localEulerAngles = storyRotationEuler;
        target.localScale = IsValidActivityScale(storyScale) ? storyScale : Vector3.one;
    }

    private IEnumerator RestoreTransformAfterDelay(Transform target, Vector3 localPosition, Vector3 localEulerAngles, Vector3 localScale, float waitSeconds)
    {
        if (waitSeconds > 0f)
            yield return new WaitForSeconds(waitSeconds);
        else
            yield return null;

        if (target == null)
            yield break;

        target.localPosition = localPosition;
        target.localEulerAngles = localEulerAngles;
        target.localScale = localScale;
    }

    private void StartTrackedChoiceAnimationLoop(Animator animator, AnimationClip clip, float speed, int loops, float singleLength)
    {
        Coroutine routine = null;
        routine = StartCoroutine(TrackedChoiceAnimationLoop(routine, animator, clip, speed, loops, singleLength));
        if (routine != null)
            _activeChoiceAnimationRoutines.Add(routine);
    }

    private IEnumerator TrackedChoiceAnimationLoop(Coroutine routine, Animator animator, AnimationClip clip, float speed, int loops, float singleLength)
    {
        yield return PlayChoiceAnimationLoops(animator, clip, speed, loops, singleLength);
        if (routine != null)
            _activeChoiceAnimationRoutines.Remove(routine);
    }

    private IEnumerator PlayChoiceAnimationLoops(Animator animator, AnimationClip clip, float speed, int loops, float singleLength)
    {
        if (animator == null || clip == null) yield break;

        // Scenario option clips must be isolated from the normal story Animator Controller.
        // We freeze the controller and drive only the selected clip through a temporary PlayableGraph.
        // This prevents Unity Animator transitions from jumping to the next story slot after a wrong/correct choice clip.
        animator.speed = 0f;

        for (int i = 0; i < Mathf.Max(1, loops); i++)
        {
            if (CreateIsolatedChoiceAnimationGraph(animator, clip, speed, out PlayableGraph graph, out AnimationClipPlayable playable))
            {
                _activeChoiceGraphs.Add(graph);
                yield return new WaitForSeconds(singleLength);
                if (graph.IsValid())
                    graph.Destroy();
                _activeChoiceGraphs.Remove(graph);

                // Keep the controller frozen after the graph is destroyed.
                // Story resumes only through ResumeStoryFromActivity after the activity is complete.
                if (animator != null)
                    animator.speed = 0f;
            }
            else
            {
                if (animator != null)
                    animator.speed = 0f;
                Debug.LogWarning($"[ContentController] Choice scenario animation could not play. Animator: {(animator != null ? animator.name : "Missing")}, Clip: {(clip != null ? clip.name : "Missing")}", this);
                yield break;
            }
        }
    }

    private bool CreateIsolatedChoiceAnimationGraph(Animator animator, AnimationClip clip, float speed, out PlayableGraph graph, out AnimationClipPlayable playable)
    {
        graph = default(PlayableGraph);
        playable = default(AnimationClipPlayable);

        if (animator == null || clip == null)
            return false;

        if (!animator.gameObject.activeInHierarchy)
            return false;

        animator.enabled = true;
        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        FreezeAnimatorControllerForActivity(animator);

        graph = PlayableGraph.Create("ChoiceScenario_Isolated_" + clip.name);
        graph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);

        playable = AnimationClipPlayable.Create(graph, clip);
        playable.SetSpeed(Mathf.Max(0.01f, speed));
        playable.SetApplyFootIK(false);

        AnimationPlayableOutput output = AnimationPlayableOutput.Create(graph, "ChoiceScenarioAnimation", animator);
        output.SetSourcePlayable(playable);

        graph.Play();
        return true;
    }

    private bool ShouldFinishFromInput(ActivityStep step)
    {
        switch (step.finishWhen)
        {
            case ActivityFinishRule.AfterFirstValidInput:
                return _acceptedInputCount >= 1;
            case ActivityFinishRule.AfterRequiredInputs:
                return _acceptedInputCount >= Mathf.Max(1, step.requiredInputCount);
            case ActivityFinishRule.AfterAllTargets:
                return step.childInput == ActivityInputKind.TapObjectsInOrder && _sequenceIndex >= step.targetObjects.Count;
            case ActivityFinishRule.AfterActiveTimeEnds:
            case ActivityFinishRule.Manual:
            default:
                return false;
        }
    }

    private IList<string> GetButtonLabels(ActivityStep step)
    {
        if (step != null && step.choiceOptions != null && step.choiceOptions.Count > 0)
        {
            List<string> labels = new List<string>();
            for (int i = 0; i < step.choiceOptions.Count; i++)
            {
                ActivityChoiceOption option = step.choiceOptions[i];
                labels.Add(option != null && !string.IsNullOrWhiteSpace(option.buttonText) ? option.buttonText : "Option " + (i + 1));
            }
            return labels;
        }

        if (step != null && step.optionTexts != null && step.optionTexts.Count > 0)
            return step.optionTexts;

        return new List<string> { "Left", "Right" };
    }

    private void ResetVisibleProgress(ActivityStep step)
    {
        _visibleProgressValue = 0f;
        _lastValidProgressInputTime = Time.time;
        if (ProgressBarFollowsInput(step))
            activityPanel?.ShowProgress(0f, "0%");
    }

    private void UpdateProgress(ActivityStep step)
    {
        if (!ProgressBarFollowsInput(step))
            return;

        float target = GetInputProgressValue(step);

        // Beginner rule:
        // Only Fill Up = progress never drops.
        // Go Down If Child Stops = valid input pushes progress up, idle time can reduce it later.
        if (step.progressBarBehavior == ActivityProgressBarBehavior.OnlyFillUp || step.progressBarBehavior == ActivityProgressBarBehavior.GoDownIfChildStops || step.progressBarBehavior == ActivityProgressBarBehavior.AdvancedCustom)
            _visibleProgressValue = Mathf.Max(_visibleProgressValue, target);
        else
            _visibleProgressValue = target;

        _lastValidProgressInputTime = Time.time;
        ShowVisibleInputProgress(step);
    }

    private float GetInputProgressValue(ActivityStep step)
    {
        if (step == null) return 0f;

        if (step.childInput == ActivityInputKind.TapObjectsInOrder && step.targetObjects != null && step.targetObjects.Count > 0)
            return Mathf.Clamp01((float)_sequenceIndex / Mathf.Max(1, step.targetObjects.Count));

        if (step.childInput == ActivityInputKind.GroupAction && step.groupCompletionMode == ActivityGroupCompletionMode.RequiredObjectCount)
            return Mathf.Clamp01((float)_uniqueTappedGroupObjects.Count / Mathf.Max(1, step.groupRequiredObjectCount));

        int required = Mathf.Max(1, step.requiredInputCount);
        return Mathf.Clamp01((float)_acceptedInputCount / required);
    }

    private void UpdateProgressIdleBehavior(ActivityStep step)
    {
        if (!ProgressBarFollowsInput(step)) return;
        if (step.progressBarBehavior != ActivityProgressBarBehavior.GoDownIfChildStops && step.progressBarBehavior != ActivityProgressBarBehavior.AdvancedCustom) return;

        float decrease = Mathf.Max(0f, step.progressGoDownPercentPerSecond) / 100f * Time.deltaTime;
        if (decrease <= 0f) return;

        float min = Mathf.Clamp01(step.progressMinimumPercent / 100f);
        float next = Mathf.Max(min, _visibleProgressValue - decrease);
        if (!Mathf.Approximately(next, _visibleProgressValue))
        {
            _visibleProgressValue = next;
            ShowVisibleInputProgress(step);
        }
    }

    private void ShowVisibleInputProgress(ActivityStep step)
    {
        if (!ProgressBarFollowsInput(step)) return;
        int percent = Mathf.RoundToInt(Mathf.Clamp01(_visibleProgressValue) * 100f);
        activityPanel?.ShowProgress(Mathf.Clamp01(_visibleProgressValue), percent + "%");
    }

    private bool StepUsesProgress(ActivityStep step)
    {
        if (step == null) return false;
        // Template rule: progress UI is optional for every activity.
        // The activity can still use tap counts or internal progress, but the visible bar appears only when the setup person enables it.
        return step.useProgressBar;
    }

    private bool ProgressBarFollowsInput(ActivityStep step)
    {
        return StepUsesProgress(step) && step.progressBarFillMode == ActivityProgressBarFillMode.FollowInputProgress;
    }

    private bool ProgressBarFollowsTime(ActivityStep step)
    {
        return StepUsesProgress(step) && step.progressBarFillMode == ActivityProgressBarFillMode.FollowActivityTime;
    }

    private bool ProgressBarFillsWhenResultPlays(ActivityStep step)
    {
        return StepUsesProgress(step) && step.progressBarFillMode == ActivityProgressBarFillMode.FillWhenResultPlays;
    }

    private void ShowProgressIfResultMode(ActivityStep step, float normalized, string label)
    {
        if (ProgressBarFillsWhenResultPlays(step))
            activityPanel?.ShowProgress(Mathf.Clamp01(normalized), label);
    }

    private bool StepUsesButtons(ActivityStep step)
    {
        if (step == null) return false;
        return step.childInput == ActivityInputKind.TapButton || step.childInput == ActivityInputKind.TapAnywhereOrButton || step.childInput == ActivityInputKind.ChooseOption || step.childInput == ActivityInputKind.AnswerQuestion;
    }

    private void BeginInput(ActivityStep step, Func<ActivityInputData, bool> validator, Action<ActivityInputData> accepted, Action<ActivityInputData> rejected)
    {
        _acceptingInput = true;
        _inputValidator = validator;
        _inputAccepted = accepted;
        _inputRejected = rejected;
    }

    private void ClearInput()
    {
        _acceptingInput = false;
        _inputValidator = null;
        _inputAccepted = null;
        _inputRejected = null;
        UnlockInputCycle();
    }

    private void HandleInput(ActivityInputData data)
    {
        if (!_acceptingInput) return;
        if (_inputCycleLocked) return;

        bool valid = _inputValidator == null || _inputValidator.Invoke(data);
        if (valid)
        {
            _lastAcceptedInput = data;
            _inputAccepted?.Invoke(data);
        }
        else
        {
            _inputRejected?.Invoke(data);
        }
    }

    private bool IsInputValidForStep(ActivityStep step, ActivityInputData data)
    {
        switch (step.childInput)
        {
            case ActivityInputKind.TapAnywhere:
            case ActivityInputKind.KeepTapping:
                return data.type == ActivityInputType.ScreenTap || data.type == ActivityInputType.ModelTap;

            case ActivityInputKind.TapAnywhereOrButton:
                return data.type == ActivityInputType.ScreenTap || data.type == ActivityInputType.ModelTap || data.type == ActivityInputType.UIButton;

            case ActivityInputKind.TapButton:
                return data.type == ActivityInputType.UIButton;

            case ActivityInputKind.TapObject:
                return data.type == ActivityInputType.ModelTap && IsTargetMatch(step.targetObject, data.hitObject);

            case ActivityInputKind.HelpAction:
                return data.type == ActivityInputType.ModelTap && IsTargetMatch(step.targetObject, data.hitObject);

            case ActivityInputKind.ProgressGate:
                if (step.targetObject == null)
                    return data.type == ActivityInputType.ScreenTap || data.type == ActivityInputType.ModelTap;
                return data.type == ActivityInputType.ModelTap && IsProgressGateTargetMatch(step.targetObject, data.hitObject);

            case ActivityInputKind.GroupAction:
                if (data.type != ActivityInputType.ModelTap) return false;
                return FindMatchingGroupTapObject(step, data.hitObject) != null;

            case ActivityInputKind.WaitForStoryThenTapObject:
                if (data.type != ActivityInputType.ModelTap) return false;
                GameObject storyTarget = step.storyMomentTapObject != null ? step.storyMomentTapObject : step.targetObject;
                return IsTargetMatch(storyTarget, data.hitObject);

            case ActivityInputKind.TapManyTimes:
                if (step.targetObject == null)
                    return data.type == ActivityInputType.ScreenTap || data.type == ActivityInputType.ModelTap;
                return data.type == ActivityInputType.ModelTap && IsTargetMatch(step.targetObject, data.hitObject);

            case ActivityInputKind.TapObjectsInOrder:
                if (data.type != ActivityInputType.ModelTap) return false;
                if (step.targetObjects == null || step.targetObjects.Count == 0) return false;

                if (step.mustTapInOrder)
                {
                    GameObject expected = step.targetObjects[Mathf.Clamp(_sequenceIndex, 0, step.targetObjects.Count - 1)];
                    if (!IsTargetMatch(expected, data.hitObject)) return false;
                    _sequenceIndex++;
                    return true;
                }

                for (int i = 0; i < step.targetObjects.Count; i++)
                {
                    if (IsTargetMatch(step.targetObjects[i], data.hitObject))
                    {
                        _sequenceIndex++;
                        return true;
                    }
                }
                return false;

            default:
                return false;
        }
    }

    private bool IsProgressGateTargetMatch(GameObject target, GameObject hit)
    {
        if (target == null || hit == null) return false;
        if (target == hit) return true;
        if (hit.transform.IsChildOf(target.transform)) return true;

        ActivityTarget activityTarget = hit.GetComponentInParent<ActivityTarget>();
        return activityTarget != null && activityTarget.gameObject == target;
    }

    private bool IsTargetMatch(GameObject target, GameObject hit)
    {
        if (target == null || hit == null) return false;
        if (target == hit) return true;
        if (hit.transform.IsChildOf(target.transform)) return true;
        if (target.transform.IsChildOf(hit.transform)) return true;

        ActivityTarget activityTarget = hit.GetComponentInParent<ActivityTarget>();
        return activityTarget != null && activityTarget.gameObject == target;
    }

    private void ApplyObjectStateList(List<GameObject> objects, bool active)
    {
        if (objects == null)
            return;

        for (int i = 0; i < objects.Count; i++)
        {
            if (objects[i] != null)
                objects[i].SetActive(active);
        }
    }

    // ---------------------------------------------------------------
    // OBJECT STATE RESTORE - ensures replay returns every object to
    // its pre-activity state (visible/hidden) so nothing stays stuck.
    // ---------------------------------------------------------------

    private void RecordActivityObjectStates(ActivityStep step)
    {
        if (step == null) return;

        // Record all four activity-level lists
        RecordObjectListStates(step.objectsOnWhenActivityStarts);
        RecordObjectListStates(step.objectsOffWhenActivityStarts);
        RecordObjectListStates(step.objectsOnWhenActivityCompletes);
        RecordObjectListStates(step.objectsOffWhenActivityCompletes);

        // Story moment before/after objects
        RecordSingleObjectState(step.storyMomentObjectBeforeComplete);
        RecordSingleObjectState(step.storyMomentObjectAfterComplete);

        // Target action object lists
        if (step.targetActions != null)
            for (int i = 0; i < step.targetActions.Count; i++)
            {
                ActivityTargetAction a = step.targetActions[i];
                if (a == null) continue;
                RecordObjectListStates(a.objectsToTurnOn);
                RecordObjectListStates(a.objectsToTurnOff);
            }

        // Scenario action object lists
        if (step.choiceOptions != null)
            for (int i = 0; i < step.choiceOptions.Count; i++)
            {
                ActivityChoiceOption opt = step.choiceOptions[i];
                if (opt?.scenarioActions == null) continue;
                for (int k = 0; k < opt.scenarioActions.Count; k++)
                {
                    ActivityScenarioAction a = opt.scenarioActions[k];
                    if (a == null) continue;
                    RecordObjectListStates(a.objectsToTurnOn);
                    RecordObjectListStates(a.objectsToTurnOff);
                }
            }
    }

    private void RecordObjectListStates(List<GameObject> objects)
    {
        if (objects == null) return;
        for (int i = 0; i < objects.Count; i++)
            RecordSingleObjectState(objects[i]);
    }

    private void RecordSingleObjectState(GameObject go)
    {
        // Only record once per object per activity session.
        // If called again for the same object, the first recorded value is kept (the true pre-activity state).
        if (go == null || _originalObjectActiveStates.ContainsKey(go)) return;
        _originalObjectActiveStates[go] = go.activeSelf;
    }

    private void RestoreAllObjectActiveStates()
    {
        foreach (var pair in _originalObjectActiveStates)
            if (pair.Key != null)
                pair.Key.SetActive(pair.Value);
        _originalObjectActiveStates.Clear();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // SFX SYSTEM — all sound helpers for the activity template
    // ═══════════════════════════════════════════════════════════════════════

    private void PlayActivityStartSound(ActivityStep step)
    {
        if (step?.activityStartSound == null) return;
        CreateTempAudioSource(step.activityStartSound, step.activityStartSoundVolume, false);
    }

    private void PlayActivityCompleteSound(ActivityStep step)
    {
        if (step?.activityCompleteSound == null) return;
        CreateTempAudioSource(step.activityCompleteSound, step.activityCompleteSoundVolume, false);
    }

    private void PlayProgressDropSound(ActivityStep step)
    {
        if (step?.progressDropSound == null) return;
        CreateTempAudioSource(step.progressDropSound, step.progressDropSoundVolume, false);
    }

    private void PlayProgressFullSound(ActivityStep step)
    {
        if (step?.progressFullSound == null) return;
        CreateTempAudioSource(step.progressFullSound, step.progressFullSoundVolume, false);
    }

    private void PlayResultAnimationStartSound(ActivityStep step)
    {
        if (step?.resultAnimationStartSound == null) return;
        CreateTempAudioSource(step.resultAnimationStartSound, step.resultAnimationStartSoundVolume, false);
    }

    private void PlayHintSound(AudioClip clip, float volume)
    {
        if (clip == null) return;
        CreateTempAudioSource(clip, Mathf.Clamp(volume, 0f, 2f), false);
    }

    /// <summary>
    /// Plays the correct tap sound for activities that do not have their own dedicated sound field.
    /// Respects the gap mode so fast tapping does not cause audio spam.
    /// </summary>
    private void TryPlayCorrectTapSound(ActivityStep step)
    {
        if (step?.generalCorrectTapSound == null) return;

        float gap = step.correctTapSoundGapMode switch
        {
            CorrectTapSoundGapMode.NoGap          => 0f,
            CorrectTapSoundGapMode.TinyGap_0_1s   => 0.10f,
            CorrectTapSoundGapMode.SmallGap_0_15s => 0.15f,
            CorrectTapSoundGapMode.MediumGap_0_2s => 0.20f,
            CorrectTapSoundGapMode.LargeGap_0_3s  => 0.30f,
            CorrectTapSoundGapMode.Custom         => Mathf.Max(0f, step.correctTapCustomGapSeconds),
            _                                      => 0.15f
        };

        if (Time.time - _lastCorrectTapSoundTime < gap) return;
        _lastCorrectTapSoundTime = Time.time;
        CreateTempAudioSource(step.generalCorrectTapSound, step.generalCorrectTapSoundVolume, false);
    }

    /// <summary>Starts the optional looping sound for the whole helper animation group.</summary>
    private void StartProgressHelperGroupLoopSound(ActivityStep step)
    {
        if (step == null || step.progressHelperGroupLoopSound == null) return;
        if (!step.loopProgressHelperGroupSoundUntilAnimationEnds) return;

        if (_activeProgressHelperGroupLoopSource != null && _activeProgressHelperGroupLoopStep == step)
            return;

        StopProgressHelperGroupLoopSound();

        _activeProgressHelperGroupLoopSource = CreateTempAudioSource(step.progressHelperGroupLoopSound, step.progressHelperGroupLoopSoundVolume, true);
        _activeProgressHelperGroupLoopStep = step;
        if (_activeProgressHelperGroupLoopSource != null && !_activityAudioSources.Contains(_activeProgressHelperGroupLoopSource))
            _activityAudioSources.Add(_activeProgressHelperGroupLoopSource);
    }

    /// <summary>Stops the optional looping sound for the helper animation group.</summary>
    private void StopProgressHelperGroupLoopSound(ActivityStep step = null)
    {
        if (step != null && _activeProgressHelperGroupLoopStep != step) return;

        if (_activeProgressHelperGroupLoopSource != null)
        {
            _activityAudioSources.Remove(_activeProgressHelperGroupLoopSource);
            Destroy(_activeProgressHelperGroupLoopSource.gameObject);
        }

        _activeProgressHelperGroupLoopSource = null;
        _activeProgressHelperGroupLoopStep = null;
    }

    /// <summary>
    /// Called by UpdateProgressHelperAnimation when progress enters a new clip's range.
    /// Plays the individual clip sound if assigned, or falls back to the shared sound.
    /// 0.5-second cooldown prevents double-fire when progress bounces at a range boundary.
    /// </summary>
    private void TryPlayHelperClipSound(ActivityStep step, int clipIndex)
    {
        if (step == null) return;
        if (clipIndex == _lastHelperClipSoundIndex && Time.time - _lastHelperClipSoundTime < 0.5f) return;

        AudioClip clip = null;
        float volume = 1f;

        // Individual clip sound takes priority over shared fallback.
        if (step.progressHelperAnimationSounds != null && clipIndex < step.progressHelperAnimationSounds.Count)
        {
            clip = step.progressHelperAnimationSounds[clipIndex];
            if (step.progressHelperAnimationSoundVolumes != null && clipIndex < step.progressHelperAnimationSoundVolumes.Count)
                volume = step.progressHelperAnimationSoundVolumes[clipIndex];
            else
                volume = step.sharedHelperAnimationSoundVolume;
        }

        // Fall back to shared sound if no individual sound assigned for this clip.
        if (clip == null && step.sharedHelperAnimationSound != null)
        {
            clip = step.sharedHelperAnimationSound;
            volume = step.sharedHelperAnimationSoundVolume;
        }

        if (clip == null) return;

        _lastHelperClipSoundIndex = clipIndex;
        _lastHelperClipSoundTime = Time.time;
        CreateTempAudioSource(clip, Mathf.Clamp(volume, 0f, 2f), false);
    }

    /// <summary>Starts the optional looping sound for the reaction-target animation group.</summary>
    private void StartProgressReactionGroupLoopSound(ActivityStep step)
    {
        if (step == null || step.progressReactionGroupLoopSound == null) return;
        if (!step.loopProgressReactionGroupSoundUntilAnimationEnds) return;

        if (_activeProgressReactionGroupLoopSource != null && _activeProgressReactionGroupLoopStep == step)
            return;

        StopProgressReactionGroupLoopSound();

        _activeProgressReactionGroupLoopSource = CreateTempAudioSource(step.progressReactionGroupLoopSound, step.progressReactionGroupLoopSoundVolume, true);
        _activeProgressReactionGroupLoopStep = step;
        if (_activeProgressReactionGroupLoopSource != null && !_activityAudioSources.Contains(_activeProgressReactionGroupLoopSource))
            _activityAudioSources.Add(_activeProgressReactionGroupLoopSource);
    }

    /// <summary>Stops the optional looping sound for the reaction-target animation group.</summary>
    private void StopProgressReactionGroupLoopSound(ActivityStep step = null)
    {
        if (step != null && _activeProgressReactionGroupLoopStep != step) return;

        if (_activeProgressReactionGroupLoopSource != null)
        {
            _activityAudioSources.Remove(_activeProgressReactionGroupLoopSource);
            Destroy(_activeProgressReactionGroupLoopSource.gameObject);
        }

        _activeProgressReactionGroupLoopSource = null;
        _activeProgressReactionGroupLoopStep = null;
    }

    /// <summary>Plays the optional sound attached to a reaction-target animation clip.</summary>
    private void TryPlayProgressReactionClipSound(ActivityStep step, int clipIndex)
    {
        if (step == null || clipIndex < 0) return;
        if (clipIndex == _lastReactionClipSoundIndex && Time.time - _lastReactionClipSoundTime < 0.5f) return;

        AudioClip clip = null;
        float volume = 1f;

        if (step.progressReactionAnimationSounds != null && clipIndex < step.progressReactionAnimationSounds.Count)
        {
            clip = step.progressReactionAnimationSounds[clipIndex];
            if (step.progressReactionAnimationSoundVolumes != null && clipIndex < step.progressReactionAnimationSoundVolumes.Count)
                volume = step.progressReactionAnimationSoundVolumes[clipIndex];
        }

        if (clip == null) return;

        _lastReactionClipSoundIndex = clipIndex;
        _lastReactionClipSoundTime = Time.time;
        CreateTempAudioSource(clip, Mathf.Clamp(volume, 0f, 2f), false);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // MILESTONE HINTS — positive encouragement during correct tapping
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Called every frame inside progress loops. Checks each milestone and fires
    /// text + sound if the threshold is crossed and the repeat mode allows it.
    /// </summary>
    private void CheckAndFireProgressMilestones(ActivityStep step, float progress)
    {
        if (step?.progressMilestones == null || step.progressMilestones.Count == 0) return;

        float progressPct = Mathf.Clamp01(progress) * 100f;

        for (int i = 0; i < step.progressMilestones.Count; i++)
        {
            ActivityProgressMilestone m = step.progressMilestones[i];
            if (m == null || !m.enabled) continue;

            bool shouldFire = false;
            switch (m.repeatMode)
            {
                case MilestoneRepeatMode.FireOnce:
                    shouldFire = !m._hasFired && progressPct >= m.progressPercent;
                    break;
                case MilestoneRepeatMode.EveryTimeCrossed:
                    shouldFire = progressPct >= m.progressPercent &&
                                 (m._lastFiredAtProgress < 0f || m._lastFiredAtProgress < m.progressPercent);
                    break;
                case MilestoneRepeatMode.FireAgainAfterDrop:
                    bool droppedBelow = m._lastFiredAtProgress >= 0f && m._lastFiredAtProgress < m.progressPercent;
                    shouldFire = progressPct >= m.progressPercent && (!m._hasFired || droppedBelow);
                    break;
            }

            if (!shouldFire) continue;

            m._hasFired = true;
            m._lastFiredAtProgress = progressPct;

            // Play milestone sound
            PlayHintSound(m.sound, m.soundVolume);

            // Show milestone text and keep it visible.
            // It is replaced only by the next milestone or cleared when the activity ends/resets.
            if (!string.IsNullOrWhiteSpace(m.hintText))
            {
                if (_activeMilestoneTextCoroutine != null)
                {
                    StopCoroutine(_activeMilestoneTextCoroutine);
                    _activeMilestoneTextCoroutine = null;
                }
                ShowQuickFeedback(m.hintText);
            }
        }

        // Track current progress for FireAgainAfterDrop AND EveryTimeCrossed modes.
        // Both modes need to know when progress dropped below a threshold so they can
        // re-fire when progress crosses back up. Without this, EveryTimeCrossed only
        // fires once because _lastFiredAtProgress is never updated after the first fire.
        for (int i = 0; i < step.progressMilestones.Count; i++)
        {
            ActivityProgressMilestone m = step.progressMilestones[i];
            if (m == null) continue;
            if (m.repeatMode != MilestoneRepeatMode.FireAgainAfterDrop &&
                m.repeatMode != MilestoneRepeatMode.EveryTimeCrossed) continue;
            // Only update after the first fire so the initial -1 sentinel is preserved until first fire.
            if (m._lastFiredAtProgress >= 0f)
                m._lastFiredAtProgress = progressPct;
        }
    }

    /// <summary>
    /// Legacy method kept for old compiled references. Milestone text now stays visible
    /// until the next milestone replaces it or the activity ends.
    /// </summary>
    private IEnumerator ShowMilestoneHintAndReturn(string milestoneText, float displayDuration, string instructionText)
    {
        ShowQuickFeedback(milestoneText);
        _activeMilestoneTextCoroutine = null;
        yield break;
    }

    /// <summary>Called from ResetInteractions so replay starts with all milestones fresh.</summary>
    private void ResetMilestoneStates(ActivityStep step)
    {
        if (step?.progressMilestones == null) return;
        for (int i = 0; i < step.progressMilestones.Count; i++)
        {
            ActivityProgressMilestone m = step.progressMilestones[i];
            if (m == null) continue;
            m._hasFired = false;
            m._lastFiredAtProgress = -1f;
        }
    }

    private void ResetAllMilestoneStates()
    {
        if (activities == null) return;
        for (int i = 0; i < activities.Count; i++)
            ResetMilestoneStates(activities[i]);
    }

    private void HandleWrongInput(ActivityStep step)
    {
        if (step == null) return;

        RunReactions(step, ActivityReactionMoment.WhenInputFails);

        // Stop any active milestone text — error text takes priority
        if (_activeMilestoneTextCoroutine != null)
        {
            StopCoroutine(_activeMilestoneTextCoroutine);
            _activeMilestoneTextCoroutine = null;
        }

        if (step.showHintWhenWrongInput)
        {
            string text = !string.IsNullOrWhiteSpace(step.wrongInputHintText) ? step.wrongInputHintText : step.tryAgainMessage;
            if (!string.IsNullOrWhiteSpace(text))
                ShowQuickFeedback(text);

            // Play wrong input hint sound
            PlayHintSound(step.wrongInputSound, step.wrongInputSoundVolume);

            PlayGuidanceSound(step);
            HighlightTargetForStep(step);
        }
        else if (!string.IsNullOrWhiteSpace(step.tryAgainMessage))
        {
            ShowQuickFeedback(step.tryAgainMessage);
        }
    }

    private void ShowNoInputHint(ActivityStep step)
    {
        if (step == null) return;

        if (step.noInputActionAfterHint == ActivityNoInputAction.DoNothing)
            return;

        // Stop any active milestone text — no-input hint takes priority
        if (_activeMilestoneTextCoroutine != null)
        {
            StopCoroutine(_activeMilestoneTextCoroutine);
            _activeMilestoneTextCoroutine = null;
        }

        string text = !string.IsNullOrWhiteSpace(step.noInputHintText) ? step.noInputHintText : step.wrongInputHintText;
        if (!string.IsNullOrWhiteSpace(text))
            ShowQuickFeedback(text);

        // Play no-input hint sound (separate from wrong-input sound)
        PlayHintSound(step.noInputHintSound, step.noInputHintSoundVolume);

        if (step.useSameHintEffectsForNoInput)
        {
            PlayGuidanceSound(step);
            HighlightTargetForStep(step);
        }
    }

    private void PlayGuidanceSound(ActivityStep step)
    {
        if (step == null || step.wrongInputSound == null) return;
        CreateTempAudioSource(step.wrongInputSound, step.wrongInputSoundVolume, false);
    }

    private GameObject GetCurrentTargetForStep(ActivityStep step)
    {
        if (step == null) return null;
        if (step.childInput == ActivityInputKind.WaitForStoryThenTapObject && step.storyMomentTapObject != null) return step.storyMomentTapObject;
        if (step.targetObject != null) return step.targetObject;
        if (step.targetObjects != null && step.targetObjects.Count > 0)
        {
            int index = Mathf.Clamp(_sequenceIndex, 0, step.targetObjects.Count - 1);
            return step.targetObjects[index];
        }
        if (step.groupTapObjects != null && step.groupTapObjects.Count > 0)
            return step.groupTapObjects[0];
        return null;
    }

    private void HighlightTargetForStep(ActivityStep step)
    {
        GameObject target = GetCurrentTargetForStep(step);
        if (target == null && step != null && step.hintObject != null)
            target = step.hintObject;

        if (_targetHintRoutine != null)
        {
            StopCoroutine(_targetHintRoutine);
            _targetHintRoutine = null;
        }

        _targetHintRoutine = StartCoroutine(TargetHintRoutine(step, target));
    }

    private IEnumerator TargetHintRoutine(ActivityStep step, GameObject target)
    {
        if (step == null) yield break;

        if (step.showHintObject && step.hintObject != null)
        {
            step.hintObject.SetActive(true);
            StartCoroutine(HideHintObjectAfter(step.hintObject, Mathf.Max(0.1f, step.hintObjectSeconds)));
        }

        int pulses = Mathf.Max(0, step.targetPulseRepeatCount);
        float pulseDuration = Mathf.Max(0.08f, step.targetPulseSeconds);
        int tintPulses = Mathf.Max(0, step.targetTintRepeatCount);
        float tintDuration = Mathf.Max(0.08f, step.targetTintSeconds);
        int totalLoops = Mathf.Max(pulses, tintPulses);

        if (target == null || totalLoops <= 0)
            yield break;

        Transform targetTransform = target.transform;
        if (!_originalTargetScales.ContainsKey(targetTransform))
            _originalTargetScales[targetTransform] = targetTransform.localScale;

        Renderer[] renderers = target.GetComponentsInChildren<Renderer>(true);
        CacheRendererColors(renderers);

        Vector3 baseScale = _originalTargetScales[targetTransform];
        Vector3 pulseScale = baseScale * Mathf.Max(1f, step.targetPulseScale);

        for (int i = 0; i < totalLoops; i++)
        {
            float loopDuration = Mathf.Max(pulseDuration, tintDuration);
            float halfDuration = loopDuration * 0.5f;

            float elapsed = 0f;
            while (elapsed < halfDuration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / halfDuration);
                t = t * t * (3f - 2f * t);

                if (step.pulseTargetObject && i < pulses && targetTransform != null)
                    targetTransform.localScale = Vector3.Lerp(baseScale, pulseScale, t);

                if (step.tintTargetObject && i < tintPulses)
                    LerpRendererTint(renderers, step.targetTintColor, t);

                yield return null;
            }

            elapsed = 0f;
            while (elapsed < halfDuration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / halfDuration);
                t = t * t * (3f - 2f * t);

                if (step.pulseTargetObject && i < pulses && targetTransform != null)
                    targetTransform.localScale = Vector3.Lerp(pulseScale, baseScale, t);

                if (step.tintTargetObject && i < tintPulses)
                    LerpRendererTint(renderers, step.targetTintColor, 1f - t);

                yield return null;
            }

            if (targetTransform != null)
                targetTransform.localScale = baseScale;
            RestoreRendererColors(renderers);
        }

        if (targetTransform != null)
            targetTransform.localScale = baseScale;
        RestoreRendererColors(renderers);
        _targetHintRoutine = null;
    }

    private IEnumerator HideHintObjectAfter(GameObject go, float seconds)
    {
        yield return new WaitForSeconds(seconds);
        if (go != null)
            go.SetActive(false);
    }

    private void SetRendererTint(Renderer[] renderers, Color color)
    {
        if (renderers == null) return;
        for (int i = 0; i < renderers.Length; i++)
        {
            if (renderers[i] != null && renderers[i].material != null)
                renderers[i].material.color = color;
        }
    }

    private void CacheRendererColors(Renderer[] renderers)
    {
        if (renderers == null) return;
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer renderer = renderers[i];
            if (renderer != null && renderer.material != null && !_originalRendererColors.ContainsKey(renderer))
                _originalRendererColors[renderer] = renderer.material.color;
        }
    }

    private void LerpRendererTint(Renderer[] renderers, Color tintColor, float amount)
    {
        if (renderers == null) return;
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer renderer = renderers[i];
            if (renderer == null || renderer.material == null) continue;

            Color baseColor = _originalRendererColors.TryGetValue(renderer, out Color original) ? original : renderer.material.color;
            renderer.material.color = Color.Lerp(baseColor, tintColor, Mathf.Clamp01(amount));
        }
    }

    private void RestoreRendererColors(Renderer[] renderers)
    {
        if (renderers == null) return;
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer renderer = renderers[i];
            if (renderer != null && _originalRendererColors.TryGetValue(renderer, out Color color) && renderer.material != null)
                renderer.material.color = color;
        }
    }

    private void StopTargetHintVisuals()
    {
        if (_targetHintRoutine != null)
        {
            StopCoroutine(_targetHintRoutine);
            _targetHintRoutine = null;
        }
    }

    private void RestoreTargetScales()
    {
        foreach (var kv in _originalTargetScales)
        {
            if (kv.Key != null)
                kv.Key.localScale = kv.Value;
        }
        _originalTargetScales.Clear();
    }

    private bool RunReactions(ActivityStep step, ActivityReactionMoment moment)
    {
        bool startedBlockingReaction = false;
        if (step == null || step.reactions == null) return false;

        for (int i = 0; i < step.reactions.Count; i++)
        {
            ActivityReaction reaction = step.reactions[i];
            if (!CanRunReaction(reaction, moment))
                continue;

            RegisterReactionTrigger(reaction);
            Coroutine routine = StartCoroutine(RunReaction(reaction));
            _runningReactions[reaction] = routine;
            if (reaction.blocksNextInput)
                startedBlockingReaction = true;
        }

        return startedBlockingReaction;
    }

    private bool CanRunReaction(ActivityReaction reaction, ActivityReactionMoment moment)
    {
        if (reaction == null || !reaction.enabled) return false;
        if (reaction.playWhen != moment) return false;

        bool independentFallingBatch = reaction.type == ActivityReactionType.VisualEffect
            && reaction.make3DObjectsFall
            && reaction.visualEffectPlayMode == VisualEffectPlayMode.AddNewEachInput;

        if (!independentFallingBatch && (reaction.playWhen == ActivityReactionMoment.IfReactionIsFree || reaction.doNotRestartWhilePlaying) && _busyReactions.Contains(reaction))
            return false;

        if (reaction.maxTriggerCount > 0 && _reactionTriggerCounts.TryGetValue(reaction, out int count) && count >= reaction.maxTriggerCount)
            return false;

        if (reaction.cooldownSeconds > 0f && _lastReactionTriggerTime.TryGetValue(reaction, out float last) && Time.time - last < reaction.cooldownSeconds)
            return false;

        return true;
    }

    private void RegisterReactionTrigger(ActivityReaction reaction)
    {
        if (reaction == null) return;

        if (!_reactionTriggerCounts.ContainsKey(reaction))
            _reactionTriggerCounts[reaction] = 0;
        _reactionTriggerCounts[reaction]++;
        _lastReactionTriggerTime[reaction] = Time.time;
    }

    private IEnumerator RunReaction(ActivityReaction reaction)
    {
        bool reactionTransformApplied = false;
        if (reaction != null && reaction.useActivityTransform)
        {
            ApplyReactionActivityTransform(reaction);
            reactionTransformApplied = true;
        }

        bool independentFallingBatch = reaction.type == ActivityReactionType.VisualEffect
            && reaction.make3DObjectsFall
            && reaction.visualEffectPlayMode == VisualEffectPlayMode.AddNewEachInput;
        bool shouldBeBusy = !independentFallingBatch && (reaction.playWhen == ActivityReactionMoment.IfReactionIsFree || reaction.doNotRestartWhilePlaying || reaction.blocksNextInput);
        if (shouldBeBusy)
            _busyReactions.Add(reaction);

        if (reaction.startDelaySeconds > 0f)
            yield return new WaitForSeconds(reaction.startDelaySeconds);

        if (reaction.sfxMode == ReactionSfxMode.WaitForPreviousThenPlay)
            yield return WaitForExistingReactionAudio(reaction);

        AudioSource reactionSfxSource = PlayReactionSfx(reaction);
        AudioSource reactionVoiceSource = PlayReactionVoice(reaction);

        float waitTime = 0f;

        switch (reaction.type)
        {
            case ActivityReactionType.VisualEffect:
                PlayVisualEffect(reaction);
                waitTime = Mathf.Max(waitTime, reaction.reactionDurationSeconds);
                break;

            case ActivityReactionType.AnimationClip:
                float animLength = PlayAnimationReaction(reaction);
                if (reaction.waitUntilFinished || shouldBeBusy || reaction.sfxMode == ReactionSfxMode.MatchReactionDuration)
                    waitTime = Mathf.Max(waitTime, animLength);
                waitTime = Mathf.Max(waitTime, reaction.reactionDurationSeconds);
                break;

            case ActivityReactionType.SoundEffect:
                if (reaction.mainAudio != null)
                {
                    AudioSource audio = CreateTempAudioSource(reaction.mainAudio, reaction.mainAudioVolume, false);
                    if (reaction.waitUntilFinished && audio != null)
                        waitTime = Mathf.Max(waitTime, reaction.mainAudio.length);
                }
                break;

            case ActivityReactionType.VoiceOver:
                if (reaction.mainAudio != null)
                {
                    AudioSource audio = CreateTempAudioSource(reaction.mainAudio, reaction.mainAudioVolume, false);
                    if (reaction.waitUntilFinished && audio != null)
                        waitTime = Mathf.Max(waitTime, reaction.mainAudio.length);
                }
                break;

            case ActivityReactionType.EnableObjects:
                SetObjectsActive(reaction.objects, true);
                break;

            case ActivityReactionType.DisableObjects:
                SetObjectsActive(reaction.objects, false);
                break;

            case ActivityReactionType.MaterialColor:
                yield return RunMaterialColorReaction(reaction);
                break;

            case ActivityReactionType.MoveObject:
                yield return RunMoveReaction(reaction);
                break;

            case ActivityReactionType.CustomAction:
                reaction.customAction?.Invoke();
                break;
        }

        if (reaction.waitForReactionVoiceOver && reactionVoiceSource != null && reaction.reactionVoiceOver != null)
            waitTime = Mathf.Max(waitTime, reaction.reactionVoiceOver.length);

        if (reaction.extraWaitSeconds > 0f)
            waitTime += reaction.extraWaitSeconds;

        if (waitTime > 0f)
            yield return new WaitForSeconds(waitTime);

        StopReactionSfxIfNeeded(reaction, reactionSfxSource);
        StopReactionVoiceIfNeeded(reaction, reactionVoiceSource);

        if (reactionTransformApplied)
            RestoreReactionActivityTransform(reaction);

        if (shouldBeBusy)
            _busyReactions.Remove(reaction);

        _runningReactions.Remove(reaction);
    }

    private void LockInputForSeconds(float seconds)
    {
        UnlockInputCycle();
        _inputCycleLocked = true;
        _inputUnlockRoutine = StartCoroutine(UnlockInputAfterSeconds(seconds));
    }

    private void LockInputUntilBlockingReactionsFinish(ActivityStep step)
    {
        UnlockInputCycle();
        _inputCycleLocked = true;
        _inputUnlockRoutine = StartCoroutine(UnlockInputWhenBlockingReactionsComplete(step));
    }

    private IEnumerator UnlockInputAfterSeconds(float seconds)
    {
        if (seconds > 0f)
            yield return new WaitForSeconds(seconds);
        _inputCycleLocked = false;
        _inputUnlockRoutine = null;
    }

    private IEnumerator UnlockInputWhenBlockingReactionsComplete(ActivityStep step)
    {
        float maxWait = step != null ? Mathf.Max(0.1f, step.maxReactionWaitSeconds) : 30f;
        float start = Time.time;

        while (_busyReactions.Count > 0)
        {
            if (Time.time - start >= maxWait)
            {
                Debug.LogWarning("[ContentController] Input lock exceeded safety wait. Unlocking input so activity cannot get stuck.");
                break;
            }
            yield return null;
        }

        _inputCycleLocked = false;
        _inputUnlockRoutine = null;
    }

    private void UnlockInputCycle()
    {
        if (_inputUnlockRoutine != null)
        {
            StopCoroutine(_inputUnlockRoutine);
            _inputUnlockRoutine = null;
        }
        _inputCycleLocked = false;
    }

    private void UpdateInputTimeProgress(ActivityStep step, float startedAt)
    {
        if (step == null || step.finishWhen != ActivityFinishRule.AfterActiveTimeEnds || step.activeTimeSeconds <= 0f)
            return;

        if (!ProgressBarFollowsTime(step))
            return;

        float normalized = Mathf.Clamp01((Time.time - startedAt) / step.activeTimeSeconds);
        float remaining = Mathf.Max(0f, step.activeTimeSeconds - (Time.time - startedAt));
        if (ProgressBarFollowsTime(step)) activityPanel?.ShowProgress(normalized, Mathf.CeilToInt(remaining).ToString());
    }

    private void PrepareAllVisualEffectSources()
    {
        if (activities == null) return;
        for (int i = 0; i < activities.Count; i++)
            PrepareVisualEffectSourcesForActivity(activities[i]);
    }

    private void PrepareVisualEffectSourcesForActivity(ActivityStep step)
    {
        if (step == null || step.reactions == null) return;

        for (int i = 0; i < step.reactions.Count; i++)
        {
            ActivityReaction reaction = step.reactions[i];
            if (reaction == null || reaction.type != ActivityReactionType.VisualEffect || !reaction.hideSourceObjectsUntilPlayed || reaction.vfxObjects == null)
                continue;

            for (int j = 0; j < reaction.vfxObjects.Count; j++)
            {
                GameObject source = reaction.vfxObjects[j];
                if (source == null) continue;

                // Only hide scene objects. Prefab assets are not visible in the scene.
                if (!source.scene.IsValid()) continue;

                source.SetActive(false);

                // Also disable all child renderers directly.
                // This is a safety net: if the reveal system re-enables the source object's
                // parent, the child renderers stay disabled so petals/VFX stay invisible.
                Renderer[] renderers = source.GetComponentsInChildren<Renderer>(true);
                for (int r = 0; r < renderers.Length; r++)
                    if (renderers[r] != null)
                        renderers[r].enabled = false;

                ParticleSystem[] particles = source.GetComponentsInChildren<ParticleSystem>(true);
                for (int p = 0; p < particles.Length; p++)
                    if (particles[p] != null)
                        particles[p].Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            }
        }
    }

    private void StopAllConfiguredVisualEffects(bool clear)
    {
        if (activities == null) return;
        for (int i = 0; i < activities.Count; i++)
            StopConfiguredVisualEffects(activities[i], clear);
    }

    private void StopConfiguredVisualEffects(ActivityStep step, bool clear)
    {
        if (step == null || step.reactions == null) return;
        for (int i = 0; i < step.reactions.Count; i++)
        {
            ActivityReaction reaction = step.reactions[i];
            if (reaction == null || reaction.type != ActivityReactionType.VisualEffect) continue;
            StopParticleObjects(reaction.vfxObjects, clear);
            if (clear)
                ClearSpawnedVisualEffectObjectsForReaction(reaction);
        }
    }

    private void StopParticleObjects(List<GameObject> objects, bool clear)
    {
        if (objects == null) return;
        for (int i = 0; i < objects.Count; i++)
        {
            GameObject go = objects[i];
            if (go == null) continue;

            ParticleSystem[] systems = go.GetComponentsInChildren<ParticleSystem>(true);
            for (int s = 0; s < systems.Length; s++)
            {
                ParticleSystem ps = systems[s];
                if (ps == null) continue;
                ps.Stop(true, clear ? ParticleSystemStopBehavior.StopEmittingAndClear : ParticleSystemStopBehavior.StopEmitting);
            }
        }
    }

    private void PlayVisualEffect(ActivityReaction reaction)
    {
        if (reaction == null || reaction.vfxObjects == null) return;

        // Falling 3D objects should always create a new batch. Existing falling petals must continue falling.
        if (reaction.visualEffectPlayMode == VisualEffectPlayMode.RestartSameEffect && !reaction.make3DObjectsFall)
            ClearSpawnedVisualEffectObjectsForReaction(reaction);

        if (reaction.visualEffectPlayMode == VisualEffectPlayMode.PlayOnlyWhenFinished && !reaction.make3DObjectsFall)
        {
            if (HasLiveSpawnedVisualEffectObjects(reaction) || AnyAssignedParticleIsPlaying(reaction))
                return;
        }

        for (int i = 0; i < reaction.vfxObjects.Count; i++)
        {
            GameObject go = reaction.vfxObjects[i];
            if (go == null) continue;

            // Petal shower / falling 3D objects must always spawn fresh copies.
            // Never replay or move the original object and never restart old falling copies.
            if (reaction.make3DObjectsFall)
            {
                SpawnObjectBurst(reaction, go);
                continue;
            }

            ParticleSystem[] systems = go.GetComponentsInChildren<ParticleSystem>(true);
            if (systems != null && systems.Length > 0)
            {
                PlayParticleSystems(reaction, systems);
            }
            else
            {
                SpawnObjectBurst(reaction, go);
            }
        }
    }

    private void PlayParticleSystems(ActivityReaction reaction, ParticleSystem[] systems)
    {
        for (int s = 0; s < systems.Length; s++)
        {
            ParticleSystem ps = systems[s];
            if (ps == null) continue;

            ps.gameObject.SetActive(true);

            if (reaction.visualEffectPlayMode == VisualEffectPlayMode.RestartSameEffect)
            {
                ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                ps.Play(true);
                if (reaction.particleBurstCount > 0)
                    ps.Emit(reaction.particleBurstCount);
                continue;
            }

            if (!ps.isPlaying)
                ps.Play(true);

            if (reaction.particleBurstCount > 0)
                ps.Emit(reaction.particleBurstCount);
        }
    }

    private bool AnyAssignedParticleIsPlaying(ActivityReaction reaction)
    {
        if (reaction == null || reaction.vfxObjects == null) return false;

        for (int i = 0; i < reaction.vfxObjects.Count; i++)
        {
            GameObject go = reaction.vfxObjects[i];
            if (go == null) continue;
            ParticleSystem[] systems = go.GetComponentsInChildren<ParticleSystem>(true);
            for (int s = 0; s < systems.Length; s++)
            {
                if (systems[s] != null && systems[s].IsAlive(true))
                    return true;
            }
        }

        return false;
    }

    private bool HasLiveSpawnedVisualEffectObjects(ActivityReaction reaction)
    {
        if (reaction == null) return false;
        if (!_spawnedVisualEffectObjectsByReaction.TryGetValue(reaction, out List<GameObject> list)) return false;

        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (list[i] == null)
                list.RemoveAt(i);
        }

        return list.Count > 0;
    }

    private Vector3 GetPetalSpawnAreaSize(ActivityReaction reaction)
    {
        if (reaction == null) return Vector3.one;
        Vector3 size = reaction.rectangleSpawnAreaSize;
        if (size == Vector3.zero && reaction.rectangleSpawnArea != null)
            size = reaction.rectangleSpawnArea.lossyScale;
        size.x = Mathf.Max(0.01f, Mathf.Abs(size.x));
        size.y = Mathf.Max(0.01f, Mathf.Abs(size.y));
        size.z = Mathf.Max(0.01f, Mathf.Abs(size.z));
        return size;
    }

    private void SpawnObjectBurst(ActivityReaction reaction, GameObject source)
    {
        if (reaction == null || source == null) return;

        int count = Mathf.Max(1, reaction.objectBurstCount);
        float life = Mathf.Max(0.05f, reaction.objectLifeSeconds);
        float spread = Mathf.Max(0f, reaction.objectSpreadRadius);
        Transform origin = reaction.vfxSpawnOrigin != null ? reaction.vfxSpawnOrigin : transform;
        bool sourceIsSceneObject = source.scene.IsValid();

        Transform rectangleArea = reaction.rectangleSpawnArea;
        Vector3 basePosition = rectangleArea != null
            ? rectangleArea.position
            : (reaction.vfxSpawnOrigin != null ? reaction.vfxSpawnOrigin.position : (sourceIsSceneObject ? source.transform.position : origin.position));
        Quaternion baseRotation = rectangleArea != null
            ? rectangleArea.rotation
            : (reaction.vfxSpawnOrigin != null ? reaction.vfxSpawnOrigin.rotation : (sourceIsSceneObject ? source.transform.rotation : origin.rotation));
        Vector3 baseScale = source.transform.localScale;
        // Falling petals are independent one-shot copies. Keep them in world space so later taps, parent movement,
        // or source-object changes cannot pull old petals back to the start point.
        Transform parent = reaction.make3DObjectsFall ? null : (reaction.keepSpawnedObjectsInWorldSpace ? null : (sourceIsSceneObject ? source.transform.parent : origin));
        Vector3 spreadRight = rectangleArea != null ? rectangleArea.right : (origin != null ? origin.right : Vector3.right);
        Vector3 spreadUp = origin != null ? origin.up : Vector3.up;
        Vector3 spreadForward = rectangleArea != null ? rectangleArea.forward : (origin != null ? origin.forward : Vector3.forward);

        if (!_spawnedVisualEffectObjectsByReaction.TryGetValue(reaction, out List<GameObject> spawnedForReaction))
        {
            spawnedForReaction = new List<GameObject>();
            _spawnedVisualEffectObjectsByReaction[reaction] = spawnedForReaction;
        }

        for (int i = 0; i < count; i++)
        {
            Vector3 offset;
            if (reaction.spawnAreaMode == ActivityVfxSpawnAreaMode.InsideRectangleArea)
            {
                // Natural flower shower: each copy starts from a unique point inside the box.
                // X = left/right, Y = height, Z = front/back. This prevents every petal from appearing in one stack.
                Vector3 size = GetPetalSpawnAreaSize(reaction);
                float x = UnityEngine.Random.Range(-Mathf.Abs(size.x) * 0.5f, Mathf.Abs(size.x) * 0.5f);
                float y = UnityEngine.Random.Range(-Mathf.Abs(size.y) * 0.5f, Mathf.Abs(size.y) * 0.5f);
                float z = UnityEngine.Random.Range(-Mathf.Abs(size.z) * 0.5f, Mathf.Abs(size.z) * 0.5f);
                offset = spreadRight * x + spreadUp * y + spreadForward * z;
            }
            else if (reaction.spawnAreaMode == ActivityVfxSpawnAreaMode.SpreadAcrossPage)
            {
                // Spread across a simple page-like rectangle. X = width, Y = depth. Height gets a small random offset.
                float x = UnityEngine.Random.Range(-reaction.pageSpreadSize.x * 0.5f, reaction.pageSpreadSize.x * 0.5f);
                float z = UnityEngine.Random.Range(-reaction.pageSpreadSize.y * 0.5f, reaction.pageSpreadSize.y * 0.5f);
                float y = UnityEngine.Random.Range(0f, Mathf.Max(0.02f, reaction.fallFlutterAmount * 2f));
                offset = spreadRight * x + spreadUp * y + spreadForward * z;
            }
            else
            {
                offset = spread > 0f ? UnityEngine.Random.insideUnitSphere * spread : Vector3.zero;
                offset.y = Mathf.Abs(offset.y);
            }

            Quaternion rotation = reaction.randomizeObjectRotation ? UnityEngine.Random.rotation : baseRotation;
            GameObject clone = Instantiate(source, basePosition + offset, rotation, parent);
            clone.name = source.name + "_ActivityVisualEffect";

            // Re-enable renderers on the copy. The source object has its renderers disabled
            // by PrepareVisualEffectSourcesForActivity to prevent it from showing during reveal.
            // Spawned copies are independent and must be fully visible.
            Renderer[] cloneRenderers = clone.GetComponentsInChildren<Renderer>(true);
            for (int r = 0; r < cloneRenderers.Length; r++)
                if (cloneRenderers[r] != null)
                    cloneRenderers[r].enabled = true;

            float randomScaleMin = Mathf.Max(0.01f, Mathf.Min(reaction.randomScaleMin, reaction.randomScaleMax));
            float randomScaleMax = Mathf.Max(randomScaleMin, Mathf.Max(reaction.randomScaleMin, reaction.randomScaleMax));
            float scaleMultiplier = UnityEngine.Random.Range(randomScaleMin, randomScaleMax);
            clone.transform.localScale = baseScale * scaleMultiplier;

            float startDelay = reaction.make3DObjectsFall
                ? UnityEngine.Random.Range(0f, Mathf.Max(0f, reaction.randomStartDelayMaxSeconds))
                : 0f;
            clone.SetActive(startDelay <= 0.001f);

            _spawnedVfxObjects.Add(clone);
            spawnedForReaction.Add(clone);
            StartCoroutine(RunSpawnedVisualObject(reaction, clone, life, startDelay));
        }
    }

    private IEnumerator RunSpawnedVisualObject(ActivityReaction reaction, GameObject go, float visibleSeconds, float startDelaySeconds)
    {
        if (reaction == null || go == null) yield break;

        if (startDelaySeconds > 0f)
            yield return new WaitForSeconds(startDelaySeconds);

        if (go == null) yield break;
        go.SetActive(true);

        Rigidbody rb = go.GetComponent<Rigidbody>();
        if (rb != null && reaction.objectLaunchForce > 0f)
        {
            Vector3 force = Vector3.up * reaction.objectLaunchForce + UnityEngine.Random.insideUnitSphere * reaction.objectLaunchForce * 0.35f;
            rb.AddForce(force, ForceMode.Impulse);
        }

        float fadeSeconds = reaction.fadeOutSpawnedObjects ? Mathf.Max(0f, reaction.fadeOutSeconds) : 0f;
        float fallDuration = Mathf.Max(0.1f, reaction.fallDurationSeconds + UnityEngine.Random.Range(0f, Mathf.Max(0f, reaction.randomFallTimeExtraSeconds)));

        if (reaction.make3DObjectsFall)
            yield return AnimateFalling3DObject(reaction, go, visibleSeconds, fallDuration);

        float waitBeforeFade = reaction.make3DObjectsFall
            ? Mathf.Max(0f, visibleSeconds - fallDuration - fadeSeconds)
            : Mathf.Max(0f, visibleSeconds - fadeSeconds);
        if (waitBeforeFade > 0f)
            yield return new WaitForSeconds(waitBeforeFade);

        if (go != null && fadeSeconds > 0f)
            yield return FadeSpawnedObject(go, fadeSeconds);

        if (go != null)
            Destroy(go);

        _spawnedVfxObjects.Remove(go);
        if (reaction != null && _spawnedVisualEffectObjectsByReaction.TryGetValue(reaction, out List<GameObject> list))
        {
            list.Remove(go);
            if (list.Count == 0)
                _spawnedVisualEffectObjectsByReaction.Remove(reaction);
        }
    }

    private IEnumerator AnimateFalling3DObject(ActivityReaction reaction, GameObject go, float lifeSeconds, float durationOverrideSeconds = -1f)
    {
        if (reaction == null || go == null) yield break;

        Transform t = go.transform;
        Vector3 start = t.position;
        Quaternion startRot = t.rotation;
        float duration = Mathf.Max(0.1f, durationOverrideSeconds > 0f ? durationOverrideSeconds : (reaction.fallDurationSeconds > 0f ? reaction.fallDurationSeconds : lifeSeconds));
        float distance = Mathf.Max(0f, reaction.fallDistance);
        float side = Mathf.Max(0f, reaction.fallSpreadSideways);
        float flutter = Mathf.Max(0f, reaction.fallFlutterAmount);
        float spin = reaction.fallSpinDegrees;

        Vector3 sideDir = UnityEngine.Random.insideUnitSphere;
        sideDir.y = 0f;
        if (sideDir.sqrMagnitude < 0.001f) sideDir = Vector3.right;
        sideDir.Normalize();

        float seed = UnityEngine.Random.Range(0f, 1000f);
        float elapsed = 0f;
        while (elapsed < duration && go != null)
        {
            elapsed += Time.deltaTime;
            float n = Mathf.Clamp01(elapsed / duration);
            float ease = Mathf.SmoothStep(0f, 1f, n);

            Vector3 pos = start + Vector3.down * distance * ease;

            switch (reaction.fallingMotion)
            {
                case FallingObjectMotion.GentleFall:
                    pos += sideDir * side * ease;
                    break;
                case FallingObjectMotion.SwirlFall:
                    pos += new Vector3(Mathf.Sin((n * 8f) + seed), 0f, Mathf.Cos((n * 8f) + seed)) * side * n;
                    break;
                case FallingObjectMotion.BounceFall:
                    pos += sideDir * side * ease;
                    pos += Vector3.up * Mathf.Sin(n * Mathf.PI * 3f) * flutter * (1f - n);
                    break;
                case FallingObjectMotion.FlutterFall:
                default:
                    pos += sideDir * side * Mathf.Sin(n * Mathf.PI * 1.2f);
                    pos += new Vector3(Mathf.Sin((n * 14f) + seed), 0f, Mathf.Cos((n * 9f) + seed)) * flutter;
                    break;
            }

            t.position = pos;
            t.rotation = startRot * Quaternion.Euler(spin * n, spin * 0.35f * n, spin * 0.6f * n);
            yield return null;
        }
    }

    private IEnumerator DestroySpawnedVfxObjectAfter(ActivityReaction reaction, GameObject go, float seconds)
    {
        float fadeSeconds = reaction != null && reaction.fadeOutSpawnedObjects ? Mathf.Max(0f, reaction.fadeOutSeconds) : 0f;
        float visibleSeconds = Mathf.Max(0f, seconds - fadeSeconds);

        if (visibleSeconds > 0f)
            yield return new WaitForSeconds(visibleSeconds);

        if (go != null && fadeSeconds > 0f)
            yield return FadeSpawnedObject(go, fadeSeconds);

        if (go != null)
            Destroy(go);
        _spawnedVfxObjects.Remove(go);
        if (reaction != null && _spawnedVisualEffectObjectsByReaction.TryGetValue(reaction, out List<GameObject> list))
        {
            list.Remove(go);
            if (list.Count == 0)
                _spawnedVisualEffectObjectsByReaction.Remove(reaction);
        }
    }

    private IEnumerator FadeSpawnedObject(GameObject go, float seconds)
    {
        if (go == null || seconds <= 0f) yield break;

        Renderer[] renderers = go.GetComponentsInChildren<Renderer>(true);
        List<Material> materials = new List<Material>();
        List<Color> startColors = new List<Color>();

        for (int r = 0; r < renderers.Length; r++)
        {
            if (renderers[r] == null) continue;
            Material[] mats = renderers[r].materials;
            for (int m = 0; m < mats.Length; m++)
            {
                Material mat = mats[m];
                if (mat == null || !mat.HasProperty("_Color")) continue;
                materials.Add(mat);
                startColors.Add(mat.color);
            }
        }

        float elapsed = 0f;
        while (elapsed < seconds && go != null)
        {
            elapsed += Time.deltaTime;
            float n = Mathf.Clamp01(elapsed / seconds);
            for (int i = 0; i < materials.Count; i++)
            {
                if (materials[i] == null) continue;
                Color c = startColors[i];
                c.a = Mathf.Lerp(startColors[i].a, 0f, n);
                materials[i].color = c;
            }
            yield return null;
        }
    }

    private void ClearSpawnedVisualEffectObjectsForReaction(ActivityReaction reaction)
    {
        if (reaction == null) return;
        if (!_spawnedVisualEffectObjectsByReaction.TryGetValue(reaction, out List<GameObject> list)) return;

        for (int i = list.Count - 1; i >= 0; i--)
        {
            GameObject go = list[i];
            if (go != null)
                Destroy(go);
            _spawnedVfxObjects.Remove(go);
        }

        list.Clear();
        _spawnedVisualEffectObjectsByReaction.Remove(reaction);
    }

    private void ClearSpawnedVfxObjects()
    {
        for (int i = _spawnedVfxObjects.Count - 1; i >= 0; i--)
        {
            if (_spawnedVfxObjects[i] != null)
                Destroy(_spawnedVfxObjects[i]);
        }
        _spawnedVfxObjects.Clear();
        _spawnedVisualEffectObjectsByReaction.Clear();
    }

    private void PrepareActivityAnimator(Animator animator, float speed)
    {
        if (animator == null) return;

        if (!animator.gameObject.activeSelf)
            animator.gameObject.SetActive(true);

        if (!animator.gameObject.activeInHierarchy)
        {
            Debug.LogWarning($"[ContentController] Animator '{animator.name}' is inactive in hierarchy. Activity animation cannot be visible until its parent is active.", animator);
            return;
        }

        if (!animator.enabled)
            animator.enabled = true;

        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        animator.Update(0f);
    }

    private void FreezeAnimatorControllerForActivity(Animator animator)
    {
        if (animator == null) return;
        if (!_activityAnimatorOriginalSpeeds.ContainsKey(animator))
            _activityAnimatorOriginalSpeeds[animator] = animator.speed;
        animator.speed = 0f;
    }

    private void RestoreActivityAnimationAnimatorSpeeds()
    {
        foreach (KeyValuePair<Animator, float> pair in _activityAnimatorOriginalSpeeds)
        {
            if (pair.Key != null)
                pair.Key.speed = pair.Value;
        }
        _activityAnimatorOriginalSpeeds.Clear();
    }

    private bool CreateActivityAnimationGraph(Animator animator, AnimationClip clip, float speed, out PlayableGraph graph, out AnimationClipPlayable playable)
    {
        graph = default(PlayableGraph);
        playable = default(AnimationClipPlayable);

        if (animator == null || clip == null)
            return false;

        PrepareActivityAnimator(animator, speed);
        if (!animator.gameObject.activeInHierarchy || !animator.enabled)
            return false;

        // Template-wide rule: activity animations must be isolated from Animator Controller transitions.
        // The clip is driven by this temporary PlayableGraph. The controller is frozen while the
        // activity owns the flow, so it cannot jump to the next story state in the background.
        FreezeAnimatorControllerForActivity(animator);

        graph = PlayableGraph.Create("StoryActivity_Isolated_" + clip.name);
        graph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);

        playable = AnimationClipPlayable.Create(graph, clip);
        playable.SetSpeed(Mathf.Max(0.01f, speed));
        playable.SetApplyFootIK(false);

        AnimationPlayableOutput output = AnimationPlayableOutput.Create(graph, "ActivityAnimation", animator);
        output.SetSourcePlayable(playable);

        graph.Play();
        _activeGraphs.Add(graph);
        return true;
    }

    private float PlayAnimationReaction(ActivityReaction reaction)
    {
        if (reaction == null || reaction.animator == null)
            return 0f;

        List<AnimationClip> clips = GetReactionAnimationClips(reaction);
        if (clips.Count == 0)
            return 0f;

        float speed = Mathf.Max(0.01f, reaction.animationSpeed);

        switch (reaction.animationPlayMode)
        {
            case ActivityReactionAnimationPlayMode.RandomClip:
            {
                AnimationClip clip = clips[UnityEngine.Random.Range(0, clips.Count)];
                return PlayOneReactionAnimation(reaction.animator, clip, speed);
            }
            case ActivityReactionAnimationPlayMode.AllTogether:
            {
                float longest = 0f;
                for (int i = 0; i < clips.Count; i++)
                    longest = Mathf.Max(longest, PlayOneReactionAnimation(reaction.animator, clips[i], speed));
                return longest;
            }
            case ActivityReactionAnimationPlayMode.AllOneByOne:
            {
                StartCoroutine(PlayReactionAnimationsOneByOne(reaction.animator, clips, speed));
                float total = 0f;
                for (int i = 0; i < clips.Count; i++)
                    total += clips[i] != null ? Mathf.Max(0.01f, clips[i].length / speed) : 0f;
                return total;
            }
            case ActivityReactionAnimationPlayMode.SelectedClipOnly:
            default:
                return PlayOneReactionAnimation(reaction.animator, clips[0], speed);
        }
    }

    private List<AnimationClip> GetReactionAnimationClips(ActivityReaction reaction)
    {
        List<AnimationClip> clips = new List<AnimationClip>();
        if (reaction == null) return clips;

        if (reaction.animationPlayMode == ActivityReactionAnimationPlayMode.SelectedClipOnly && reaction.animationClip != null)
        {
            clips.Add(reaction.animationClip);
            return clips;
        }

        if (reaction.animationClips != null)
        {
            for (int i = 0; i < reaction.animationClips.Count; i++)
            {
                if (reaction.animationClips[i] != null)
                    clips.Add(reaction.animationClips[i]);
            }
        }

        if (clips.Count == 0 && reaction.animationClip != null)
            clips.Add(reaction.animationClip);

        return clips;
    }

    private float PlayOneReactionAnimation(Animator animator, AnimationClip clip, float speed)
    {
        if (animator == null || clip == null)
            return 0f;

        float length = Mathf.Max(0.01f, clip.length / Mathf.Max(0.01f, speed));
        if (CreateActivityAnimationGraph(animator, clip, speed, out PlayableGraph graph, out AnimationClipPlayable playable))
            StartCoroutine(DestroyGraphAfter(graph, length));
        return length;
    }

    private IEnumerator PlayReactionAnimationsOneByOne(Animator animator, List<AnimationClip> clips, float speed)
    {
        if (animator == null || clips == null) yield break;
        for (int i = 0; i < clips.Count; i++)
        {
            AnimationClip clip = clips[i];
            if (clip == null) continue;
            float length = PlayOneReactionAnimation(animator, clip, speed);
            if (length > 0f)
                yield return new WaitForSeconds(length);
        }
    }

    private IEnumerator DestroyGraphAfter(PlayableGraph graph, float seconds)
    {
        if (seconds > 0f)
            yield return new WaitForSeconds(seconds);
        if (graph.IsValid())
            graph.Destroy();
        _activeGraphs.Remove(graph);
    }

    private IEnumerator RunMaterialColorReaction(ActivityReaction reaction)
    {
        if (reaction.targetRenderer == null) yield break;
        if (reaction.targetRenderer.material == null) yield break;

        if (!_originalRendererColors.ContainsKey(reaction.targetRenderer))
            _originalRendererColors[reaction.targetRenderer] = reaction.targetRenderer.material.color;

        Color start = reaction.targetRenderer.material.color;
        Color end = reaction.targetColor;
        float duration = Mathf.Max(0f, reaction.colorChangeSeconds);

        if (duration <= 0f)
        {
            reaction.targetRenderer.material.color = end;
        }
        else
        {
            float t = 0f;
            while (t < duration)
            {
                t += Time.deltaTime;
                reaction.targetRenderer.material.color = Color.Lerp(start, end, Mathf.Clamp01(t / duration));
                yield return null;
            }
            reaction.targetRenderer.material.color = end;
        }

        if (reaction.restoreColorAfterSeconds > 0f)
        {
            yield return new WaitForSeconds(reaction.restoreColorAfterSeconds);
            RestoreRendererColor(reaction.targetRenderer);
        }
    }

    private IEnumerator RunMoveReaction(ActivityReaction reaction)
    {
        if (reaction.objectToMove == null) yield break;

        Vector3 start = reaction.objectToMove.position;
        Vector3 end = reaction.moveTarget != null ? reaction.moveTarget.position : start + reaction.moveOffset;
        float duration = Mathf.Max(0f, reaction.moveDurationSeconds);

        if (duration <= 0f)
        {
            reaction.objectToMove.position = end;
            yield break;
        }

        float t = 0f;
        while (t < duration)
        {
            t += Time.deltaTime;
            reaction.objectToMove.position = Vector3.Lerp(start, end, Mathf.Clamp01(t / duration));
            yield return null;
        }
        reaction.objectToMove.position = end;
    }

    private void RestoreMaterialColors()
    {
        foreach (var kv in _originalRendererColors)
        {
            if (kv.Key != null && kv.Key.material != null)
                kv.Key.material.color = kv.Value;
        }
        _originalRendererColors.Clear();
    }

    private void RestoreRendererColor(Renderer renderer)
    {
        if (renderer == null) return;
        if (_originalRendererColors.TryGetValue(renderer, out Color color) && renderer.material != null)
            renderer.material.color = color;
    }

    private void SetObjectsActive(List<GameObject> objects, bool active)
    {
        if (objects == null) return;
        for (int i = 0; i < objects.Count; i++)
        {
            if (objects[i] != null)
                objects[i].SetActive(active);
        }
    }

    private IEnumerator PlayActivityVoice(ActivityStep step)
    {
        if (step.activityVoiceOver == null)
            yield break;

        if (_activityVoiceRoutine != null)
        {
            StopCoroutine(_activityVoiceRoutine);
            _activityVoiceRoutine = null;
        }

        AudioSource source = CreateTempAudioSource(step.activityVoiceOver, 1f, false);
        if (source != null)
            _activityAudioSources.Add(source);

        if (step.waitForActivityVoiceOver && step.activityVoiceOver.length > 0f)
            yield return new WaitForSeconds(step.activityVoiceOver.length);
    }

    private void StartActivityAudio(ActivityStep step)
    {
        if (step == null || step.activityDurationAudio == null) return;
        AudioSource source = CreateTempAudioSource(step.activityDurationAudio, step.activityDurationAudioVolume, step.loopActivityDurationAudio);
        if (source != null)
            _activityAudioSources.Add(source);
    }

    private void StopActivityAudio(ActivityStep step = null)
    {
        if (step != null && step.fadeActivityAudioOnEnd && step.activityAudioFadeSeconds > 0f)
        {
            for (int i = _activityAudioSources.Count - 1; i >= 0; i--)
            {
                if (_activityAudioSources[i] != null)
                    StartCoroutine(FadeAndDestroyAudio(_activityAudioSources[i], step.activityAudioFadeSeconds));
            }
            _activityAudioSources.Clear();
            _activeProgressHelperGroupLoopSource = null;
            _activeProgressHelperGroupLoopStep = null;
            _activeProgressReactionGroupLoopSource = null;
            _activeProgressReactionGroupLoopStep = null;
            return;
        }

        for (int i = _activityAudioSources.Count - 1; i >= 0; i--)
        {
            if (_activityAudioSources[i] != null)
                Destroy(_activityAudioSources[i].gameObject);
        }
        _activityAudioSources.Clear();
        _activeProgressHelperGroupLoopSource = null;
        _activeProgressHelperGroupLoopStep = null;
        _activeProgressReactionGroupLoopSource = null;
        _activeProgressReactionGroupLoopStep = null;
    }

    private IEnumerator FadeAndDestroyAudio(AudioSource source, float seconds)
    {
        if (source == null) yield break;
        float start = source.volume;
        float t = 0f;
        while (t < seconds && source != null)
        {
            t += Time.deltaTime;
            source.volume = Mathf.Lerp(start, 0f, Mathf.Clamp01(t / seconds));
            yield return null;
        }
        if (source != null)
            Destroy(source.gameObject);
    }

    private IEnumerator WaitForExistingReactionAudio(ActivityReaction reaction)
    {
        if (reaction == null) yield break;
        if (_reactionAudioSources.TryGetValue(reaction, out AudioSource source) && source != null)
        {
            while (source != null && source.isPlaying)
                yield return null;
        }
    }

    private AudioSource PlayReactionSfx(ActivityReaction reaction)
    {
        if (reaction == null || reaction.optionalSfx == null) return null;

        if (reaction.sfxMinimumGapSeconds > 0f)
        {
            if (_lastReactionSfxTime.TryGetValue(reaction, out float last) && Time.time - last < reaction.sfxMinimumGapSeconds)
                return null;
        }

        if (reaction.sfxMode == ReactionSfxMode.DoNotRestartWhilePlaying)
        {
            if (_reactionAudioSources.TryGetValue(reaction, out AudioSource existing) && existing != null && existing.isPlaying)
                return existing;
        }

        if (reaction.sfxMode == ReactionSfxMode.RestartOnEveryTrigger)
        {
            if (_reactionAudioSources.TryGetValue(reaction, out AudioSource old) && old != null)
                Destroy(old.gameObject);
            _reactionAudioSources.Remove(reaction);
        }

        _lastReactionSfxTime[reaction] = Time.time;

        bool loop = reaction.sfxMode == ReactionSfxMode.LoopUntilReactionEnds;
        AudioSource source = CreateTempAudioSource(reaction.optionalSfx, reaction.sfxVolume, loop);

        if (source != null && reaction.sfxMode != ReactionSfxMode.PlayOnce)
            _reactionAudioSources[reaction] = source;

        return source;
    }

    private AudioSource PlayReactionVoice(ActivityReaction reaction)
    {
        if (reaction == null || reaction.reactionVoiceOver == null) return null;
        AudioSource source = CreateTempAudioSource(reaction.reactionVoiceOver, reaction.reactionVoiceVolume, false);
        return source;
    }

    private void StopReactionSfxIfNeeded(ActivityReaction reaction, AudioSource source)
    {
        if (reaction == null || source == null) return;

        bool shouldStop = reaction.sfxMode == ReactionSfxMode.StopWhenReactionEnds || reaction.sfxMode == ReactionSfxMode.LoopUntilReactionEnds || reaction.sfxMode == ReactionSfxMode.MatchReactionDuration;
        if (shouldStop && source != null)
            Destroy(source.gameObject);

        if (_reactionAudioSources.ContainsKey(reaction) && shouldStop)
            _reactionAudioSources.Remove(reaction);
    }

    private void StopReactionVoiceIfNeeded(ActivityReaction reaction, AudioSource source)
    {
        if (source == null) return;
        if (reaction != null && reaction.stopVoiceWhenReactionEnds)
            Destroy(source.gameObject);
    }

    private AudioSource CreateTempAudioSource(AudioClip clip, float volume, bool loop)
    {
        if (clip == null) return null;
        GameObject audioGo = new GameObject("StoryActivityAudio_" + clip.name);
        audioGo.transform.SetParent(transform, false);
        AudioSource source = audioGo.AddComponent<AudioSource>();
        source.clip = clip;

        float safeVolume = Mathf.Clamp(volume, 0f, 2f);
        source.volume = Mathf.Clamp01(safeVolume);

        // Unity AudioSource volume is capped at 1. For activity SFX above 1,
        // use the existing AudioAmplifier component so setup can safely use up to 2x.
        if (safeVolume > 1f)
        {
            AudioAmplifier amplifier = audioGo.GetComponent<AudioAmplifier>();
            if (amplifier == null)
                amplifier = audioGo.AddComponent<AudioAmplifier>();
            amplifier.multiplier = safeVolume;
        }

        source.loop = loop;
        source.playOnAwake = false;
        source.Play();
        if (!loop)
            Destroy(audioGo, clip.length + 0.1f);
        return source;
    }

    private void StopAllReactionAudio()
    {
        foreach (var kv in _reactionAudioSources)
        {
            if (kv.Value != null)
                Destroy(kv.Value.gameObject);
        }
        _reactionAudioSources.Clear();
    }

    private IEnumerator WaitForRunningReactions(ActivityStep step)
    {
        float maxWait = step != null ? Mathf.Max(0.5f, step.maxReactionWaitSeconds) : 30f;
        float start = Time.time;

        while (_runningReactions.Count > 0 || _busyReactions.Count > 0)
        {
            if (Time.time - start >= maxWait)
            {
                Debug.LogWarning($"[ContentController] Running reactions exceeded safety wait. Forcing activity to continue. Running:{_runningReactions.Count} Busy:{_busyReactions.Count}", this);
                StopAllReactionRoutines();
                break;
            }
            yield return null;
        }
    }

    private void StopAllReactionRoutines()
    {
        foreach (KeyValuePair<ActivityReaction, Coroutine> kv in _runningReactions)
        {
            if (kv.Value != null)
                StopCoroutine(kv.Value);
        }
        _runningReactions.Clear();
        _busyReactions.Clear();
    }

    private void StopAllGraphs()
    {
        for (int i = _activeGraphs.Count - 1; i >= 0; i--)
        {
            PlayableGraph graph = _activeGraphs[i];
            if (graph.IsValid())
                graph.Destroy();
        }
        _activeGraphs.Clear();
    }

    private void StopAllChoiceScenarioRoutines()
    {
        for (int i = _activeChoiceAnimationRoutines.Count - 1; i >= 0; i--)
        {
            Coroutine routine = _activeChoiceAnimationRoutines[i];
            if (routine != null)
                StopCoroutine(routine);
        }
        _activeChoiceAnimationRoutines.Clear();

        // Stop any pending scenario transform restore coroutines.
        // If not stopped, a fast replay can leave models stuck in activity pose.
        for (int i = _scenarioTransformRestoreRoutines.Count - 1; i >= 0; i--)
        {
            Coroutine routine = _scenarioTransformRestoreRoutines[i];
            if (routine != null)
                StopCoroutine(routine);
        }
        _scenarioTransformRestoreRoutines.Clear();
    }

    private void StopAllChoiceGraphs()
    {
        for (int i = _activeChoiceGraphs.Count - 1; i >= 0; i--)
        {
            PlayableGraph graph = _activeChoiceGraphs[i];
            if (graph.IsValid())
                graph.Destroy();
        }
        _activeChoiceGraphs.Clear();
    }

    private void ShowQuickFeedback(string text)
    {
        if (!string.IsNullOrWhiteSpace(text))
            activityPanel?.ShowFeedback(text);
    }

    private IEnumerator ShowFeedback(string text, float seconds)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;
        activityPanel?.ShowFeedback(text);
        if (seconds > 0f)
            yield return new WaitForSeconds(seconds);
    }

    private void HideActivityUI()
    {
        activityPanel?.EndActivity();
    }

    private void HideChoiceUIForScenario()
    {
        if (activityPanel == null) return;
        activityPanel.HideAllActivityUIForScenario();
    }

    private void ShowChoiceUIAfterWrong(ActivityStep step, IList<string> labels, Action<int> clickHandler, bool[] disabledOptions)
    {
        if (activityPanel == null) return;
        _activeChoiceStep = step;
        _activeChoiceLabels = labels;
        _activeChoiceDisabledOptions = disabledOptions;
        _activeChoiceClickHandler = clickHandler;
        activityPanel.ShowInstruction(step != null ? step.instructionText : string.Empty);
        bool grayDisabled = step != null && step.choiceWrongOptionBehaviour == ActivityChoiceWrongOptionBehaviour.DisableAndGrayOut;
        activityPanel.ShowChoiceButtons(labels, clickHandler, disabledOptions, grayDisabled);
    }

    public void RestoreActivityUIAfterTrackingFound()
    {
        if (activityPanel == null) return;

        // If a choice/scenario activity was active before tracking was lost, rebuild the full question UI.
        // This restores option buttons and click handlers, not only the question text.
        if (_activeChoiceStep != null && _activeChoiceLabels != null && _activeChoiceClickHandler != null)
        {
            bool grayDisabled = _activeChoiceStep.choiceWrongOptionBehaviour == ActivityChoiceWrongOptionBehaviour.DisableAndGrayOut;
            activityPanel.ShowInstruction(_activeChoiceStep.instructionText);
            activityPanel.ShowChoiceButtons(_activeChoiceLabels, _activeChoiceClickHandler, _activeChoiceDisabledOptions, grayDisabled);
            return;
        }

        // Non-choice activities keep their input state alive during tracking loss.
        // Restore the visible instruction if an activity is running.
        if (_currentIndex >= 0 && activities != null && _currentIndex < activities.Count)
        {
            ActivityStep step = activities[_currentIndex];
            if (step != null && step.enabled)
            {
                activityPanel.ShowInstruction(step.instructionText);
                if (StepUsesProgress(step))
                    activityPanel.ShowProgress(_visibleProgressValue, string.Empty);
            }
        }
    }

    private void ClearActiveChoiceRestoreState()
    {
        _activeChoiceStep = null;
        _activeChoiceLabels = null;
        _activeChoiceDisabledOptions = null;
        _activeChoiceClickHandler = null;
    }

    private void CompleteToStoryFlow()
    {
        ClearInput();
        RestoreAllActivityActionTransforms();
        ClearSpawnedVfxObjects();
        StopTargetHintVisuals();
        RestoreMaterialColors();
        RestoreTargetScales();
        HideActivityUI();
        SetAnyActivityRunning(false);
        Action callback = _completionCallback;
        _completionCallback = null;
        callback?.Invoke();
    }
}

public interface IARContent
{
    void PlayContent();
    void PauseContent();
    void ReplayContent();
    void SetCompletionCallback(Action onCompleted);
}

public enum ActivityStartRule
{
    AfterStoryEnds,
    AfterRevealFinishes,
    AfterVoiceStarts,
    AfterWaitingTime,
    AfterPreviousActivity,
    FromAnimationEvent,
    ManualStart,
    AfterStoryObjectFinishes
}

public enum ActivityInputKind
{
    TapAnywhere,
    TapObject,
    TapManyTimes,
    TapObjectsInOrder,
    KeepTapping,
    ChooseOption,
    AnswerQuestion,
    WaitOnly,
    TapButton,
    TapAnywhereOrButton,
    HelpAction,
    ProgressGate,
    GroupAction,
    TapObjectAndReact,
    WaitForStoryThenTapObject
}

public enum ActivityProgressGateCompletionMode
{
    RequiredTapCount,
    RequiredActiveTappingTime,
    RequiredTapSpeedForTime
}

public enum StoryMomentTapCompletionMode
{
    RequiredTapCount,
    RequiredActiveTappingTime
}

public enum ActivityReactionSequenceMode
{
    InOrder,
    Random,
    ByTapSpeed,
    ByProgress,

    // Compatibility aliases used by earlier custom editor versions.
    ByMeterPercent,
    UseSelectedAnimationNumberAndLoop
}

public enum ActivityProgressReactionPlaybackMode
{
    PlayOnValidTap,
    HoldByProgressPercent,
}

public enum ActivityProgressTapFeedbackStyle
{
    SmoothBump,
    SmoothShake,
    BumpAndShake,
}

public enum ActivityChoiceCorrectBehaviour
{
    ContinueStoryImmediately,
    PlayCorrectResultThenContinueStory
}

public enum ActivityChoiceWrongOptionBehaviour
{
    KeepSelectable,
    DisableAndGrayOut
}

public enum ActivityGuidanceTargetMode
{
    None,
    TargetObject,
    TapObject,
    TappedObject,
    CorrectObject,
    CustomObject,
    UseTargetObject,
    UseTappedObject,
    UseCorrectObject,
    UseCustomObject,
    SameAsTargetObject,
    SameAsTappedObject,
    SameAsCorrectObject
}

public enum ActivityGroupCompletionMode
{
    AnyAllowedObject,
    AllAllowedObjects,
    RequiredObjects,
    RequiredObjectCount
}

public enum ProgressGatePreviewAnimationSelectionMode
{
    UseFirstAnimation,
    PickRandomAnimationOnce,
    UseSelectedAnimationNumber,
    PlaySelectedAnimationNumbers,
    PlayAllAnimationsByProgress,
    PlaySelectedNumbersByProgress,
    PlayAllAnimationsInOrder
}

public enum ActivityFinishRule
{
    AfterFirstValidInput,
    AfterActiveTimeEnds,
    AfterRequiredInputs,
    AfterAllTargets,
    Manual
}

public enum ActivityNextInputRule
{
    Immediately,
    AfterReactionFinishes,
    AfterFixedDelay
}

public enum ActivityNoInputAction
{
    DoNothing,
    ShowHintOnly,
    SkipActivityAndContinue,
    AutoPlayResultThenContinue
}

public enum ActivityProgressBarFillMode
{
    FollowInputProgress,
    FollowActivityTime,
    FillWhenResultPlays
}

public enum ActivityProgressBarBehavior
{
    OnlyFillUp,
    GoDownIfChildStops,
    FillWithTime,
    FillDuringResult,
    AdvancedCustom
}

public enum ActivityResultPlayTiming
{
    OnEveryCorrectInput,
    AfterRequiredInputs,
    WhenProgressIsFull,
    WhileChildIsInteracting,
    AfterNoInputAutoPlay
}

public enum ActivityReactionAnimationPlayMode
{
    SelectedClipOnly,
    RandomClip,
    AllTogether,
    AllOneByOne
}

public enum ActivityVfxSpawnAreaMode
{
    FromSourceOrSpawnPoint,
    SpreadAcrossPage,
    InsideRectangleArea
}

public enum ActivityReactionType
{
    VisualEffect,
    AnimationClip,
    SoundEffect,
    VoiceOver,
    EnableObjects,
    DisableObjects,
    MaterialColor,
    MoveObject,
    CustomAction
}

public enum ActivityReactionMoment
{
    EveryValidInput,
    IfReactionIsFree,
    WhenActivityStarts,
    WhenActivityCompletes,
    WhenInputFails
}


public enum VisualEffectPlayMode
{
    AddNewEachInput,
    RestartSameEffect,
    PlayOnlyWhenFinished
}

public enum FallingObjectMotion
{
    GentleFall,
    SwirlFall,
    BounceFall,
    FlutterFall
}

public enum ReactionSfxMode
{
    PlayOnce,
    RestartOnEveryTrigger,
    DoNotRestartWhilePlaying,
    StopWhenReactionEnds,
    LoopUntilReactionEnds,
    WaitForPreviousThenPlay,
    MatchReactionDuration
}

[Serializable]
public class ActivityStep
{
    public bool enabled = true;
    public string activityName = "New Activity";

    public ActivityStartRule startWhen = ActivityStartRule.AfterStoryEnds;
    public float waitBeforeStart = 0f;
    public string startKey;

    [Header("Story Moment Start")]
    [Tooltip("Drag the story model or Animator to watch. Example: drag the fox model or its Animator. The activity starts after the selected animation finishes.")]
    public UnityEngine.Object storyWaitObjectOrAnimator;
    [Tooltip("Optional legacy field. Drag a movement component here only if you want to wait for a movement script that exposes IsFinished or IsPlaying.")]
    public MonoBehaviour storyWaitComponent;
    [Tooltip("Optional. Drag the Animator on the story model. If empty, the system tries to find it from Story Model Or Animator To Watch.")]
    public Animator storyWaitAnimator;
    [Tooltip("Drag the exact story animation clip to wait for. Recommended. Example: fox walk 01.")]
    public AnimationClip storyWaitAnimationClip;
    [Tooltip("Advanced fallback only. Usually leave empty. If an animation clip is assigned, this typed state name is ignored.")]
    public string storyWaitAnimationStateName;
    [Tooltip("Optional warning time. If the selected story animation is not reached after this many seconds, a Console warning is shown. The activity will not start early. 0 means no warning.")]
    public float storyWaitTimeoutSeconds = 0f;
    [Tooltip("ON = pause the current story while this activity is running, then resume from the same point.")]
    public bool pauseStoryWhileActivity = false;

    [TextArea(2, 4)] public string instructionText;
    public AudioClip activityVoiceOver;
    public bool waitForActivityVoiceOver = false;

    public AudioClip activityDurationAudio;
    public bool loopActivityDurationAudio = false;
    [Range(0f, 2f)] public float activityDurationAudioVolume = 1f;
    public bool fadeActivityAudioOnEnd = false;
    public float activityAudioFadeSeconds = 0.5f;

    [Header("Simple Object State Changes")]
    [Tooltip("Objects turned ON as soon as this activity starts. Example: show an unbroken object before the child taps.")]
    public List<GameObject> objectsOnWhenActivityStarts = new List<GameObject>();
    [Tooltip("Objects turned OFF as soon as this activity starts.")]
    public List<GameObject> objectsOffWhenActivityStarts = new List<GameObject>();
    [Tooltip("Objects turned ON when this activity successfully completes. Example: show the broken version after the child taps.")]
    public List<GameObject> objectsOnWhenActivityCompletes = new List<GameObject>();
    [Tooltip("Objects turned OFF when this activity successfully completes. Example: hide the unbroken version after the child taps.")]
    public List<GameObject> objectsOffWhenActivityCompletes = new List<GameObject>();

    public ActivityInputKind childInput = ActivityInputKind.TapAnywhere;
    public GameObject targetObject;
    public List<GameObject> targetObjects = new List<GameObject>();
    public bool mustTapInOrder = true;
    public int requiredInputCount = 1;
    public float waitOnlySeconds = 1f;
    public float activeTimeSeconds = 10f;
    public float maxTimeToFirstInput = 0f;

    [Header("Help Action")]
    public float helpProgressGainPerTap = 20f;
    public float helpProgressLossPerSecond = 25f;
    public float helpAutoContinueAfterSeconds = 5f;
    public bool helpCompleteWhenProgressFull = true;
    public Animator helpAnimator;
    public AnimationClip helpAnimationClip;
    public float helpAnimationSpeed = 1f;
    public bool helpResetAnimationWhenProgressIsEmpty = true;
    public bool helpWaitForAnimationBeforeContinue = true;
    public AudioClip helpTapSound;
    [Range(0f, 2f)] public float helpTapSoundVolume = 1f;

    [Header("Progress Gate")]
    public ActivityProgressGateCompletionMode progressGateCompletesBy = ActivityProgressGateCompletionMode.RequiredTapCount;
    public int progressRequiredTaps = 5;
    public float progressRequiredTappingSeconds = 5f;
    public float progressTapActiveWindowSeconds = 0.35f;
    [Tooltip("For tap speed mode, how many taps per second the child must reach before the progress bar fills.")]
    public float progressRequiredTapsPerSecond = 3f;
    [Tooltip("For tap speed mode, how many good seconds are needed to fill the bar.")]
    public float progressRequiredSpeedSeconds = 5f;
    [Tooltip("For tap speed mode, the time window used to calculate taps per second.")]
    public float progressTapSpeedWindowSeconds = 1f;
    public bool progressDropsWhenNotTapping = true;
    public float progressLossPerSecond = 25f;
    public float progressAutoStartStoryAfterSeconds = 0f;
    public bool playResultWhenProgressAutoSkips = true;
    public AudioClip progressTapSound;
    [Range(0f, 2f)] public float progressTapSoundVolume = 1f;

    [Header("Correct Tap Feedback")]
    [Tooltip("ON = the object tapped by the child can move or shake on every correct tap.")]
    public bool progressUseCorrectTapFeedback = false;
    [Tooltip("Object that moves or shakes on a correct tap. Leave empty to use Object To Tap.")]
    public Transform progressCorrectTapFeedbackObject;
    public ActivityProgressTapFeedbackStyle progressCorrectTapFeedbackStyle = ActivityProgressTapFeedbackStyle.BumpAndShake;
    public float progressCorrectMoveUpHeight = 0.04f;
    public float progressCorrectShakeAmount = 0.03f;
    public float progressCorrectTapFeedbackSeconds = 0.16f;
    public float progressCorrectReturnSeconds = 0.08f;

    [Header("Wrong Tap Feedback")]
    [Tooltip("ON = wrong taps can show text, play sound, pulse the correct object, or shake the wrong object.")]
    public bool progressUseWrongTapFeedback = true;
    public string progressWrongTapText = "Try tapping the highlighted object";
    public AudioClip progressWrongTapSound;
    [Range(0f, 2f)] public float progressWrongTapSoundVolume = 1f;
    public bool progressPulseCorrectObjectOnWrongTap = true;
    public bool progressShakeWrongTappedObject = false;
    public float progressWrongShakeAmount = 0.03f;
    public float progressWrongTapFeedbackSeconds = 0.18f;
    public float progressWrongReturnSeconds = 0.08f;

    [Header("Reaction Target While Progress Is Filling")]
    [Tooltip("ON = each valid tap can play one animation from the reaction list on another object or character.")]
    public bool progressUseReactionSequence = false;
    [Tooltip("Animator on the object or character that reacts when the input object is tapped.")]
    public Animator progressReactionAnimator;
    [Tooltip("Animations that can play on the reaction target while the child taps.")]
    public List<AnimationClip> progressReactionAnimations = new List<AnimationClip>();
    [Tooltip("How the reaction animation is selected: in order, random, by tap speed, or by progress percent.")]
    public ActivityReactionSequenceMode progressReactionOrder = ActivityReactionSequenceMode.InOrder;
    [Tooltip("Play On Valid Tap plays a reaction each tap. Hold By Progress Percent keeps the reaction target on the clip that matches the progress percentage.")]
    public ActivityProgressReactionPlaybackMode progressReactionPlaybackMode = ActivityProgressReactionPlaybackMode.PlayOnValidTap;
    [Tooltip("Minimum seconds between reaction animations, prevents animation spam.")]
    public float progressReactionMinimumGapSeconds = 0.15f;
    [Tooltip("Speed used when playing the reaction animation. 1 is normal speed.")]
    public float progressReactionAnimationSpeed = 1f;
    [Tooltip("Optional looping sound for this whole reaction animation group. Starts when reaction animations are active and stops when the group/activity ends.")]
    public AudioClip progressReactionGroupLoopSound;
    [Range(0f, 2f)] public float progressReactionGroupLoopSoundVolume = 1f;
    [Tooltip("ON = the reaction group sound loops until reaction animations finish or the activity ends.")]
    public bool loopProgressReactionGroupSoundUntilAnimationEnds = false;
    [Tooltip("Optional sound per reaction animation clip. Position 1 matches clip 1, position 2 matches clip 2, and so on.")]
    public List<AudioClip> progressReactionAnimationSounds = new List<AudioClip>();
    [Tooltip("Volume per reaction animation sound. Position 1 matches clip 1. 1 = normal, 2 = boosted.")]
    public List<float> progressReactionAnimationSoundVolumes = new List<float>();

    [Header("Helper Animation While Filling Progress")]
    public bool progressUseHelperAnimationWhileTapping = false;
    public Animator progressHelperAnimator;
    public List<AnimationClip> progressHelperAnimations = new List<AnimationClip>();
    public ProgressGatePreviewAnimationSelectionMode progressHelperAnimationSelection = ProgressGatePreviewAnimationSelectionMode.PickRandomAnimationOnce;
    public int progressHelperSelectedAnimationNumber = 1;
    [Tooltip("Optional. Type clip numbers like 1,3,4,5. Used by selected animation modes. Numbers start from 1.")]
    public string progressHelperSelectedAnimationNumbers = "1";
    public float progressHelperAnimationSpeed = 1f;
    public bool progressHelperLoopAnimation = true;
    public bool progressHelperPauseWhenNotTapping = true;
    public bool progressHelperResetWhenProgressEmpty = false;
    [Tooltip("If the child stops after starting, wait this many seconds, then auto-play the remaining activity animations and finish. 0 = wait forever.")]
    public float progressAutoFinishAfterNoTapSeconds = 5f;

    [Header("Story Result After Progress")]
    public Animator resultAnimator;
    public AnimationClip resultAnimationClip;
    public float resultAnimationSpeed = 1f;
    public bool waitForResultAnimation = true;
    public AudioClip resultVoiceOver;
    [Range(0f, 2f)] public float resultVoiceVolume = 1f;
    public bool waitForResultVoiceOver = true;
    public AudioClip resultSoundEffect;
    [Range(0f, 2f)] public float resultSoundVolume = 1f;
    public bool waitForResultSound = false;
    public float resultExtraWaitSeconds = 0f;

    [Header("Result Activity Transform Optional")]
    [Tooltip("ON = temporarily move, rotate, or scale the result model only while this activity result plays.")]
    public bool resultUseActivityTransform = false;
    [Tooltip("Editor only. ON = show the result activity pose in Scene view while setting up. Turn OFF or use Back To Story Position before testing story/VFX.")]
    public bool resultPreviewActivityTransformInEditor = false;
    [Tooltip("Object to move or scale. If empty, Result Animator object is used.")]
    public GameObject resultObjectToMoveOrScale;
    [Tooltip("Optional. Copy position, rotation, and scale from this helper transform instead of typing values manually.")]
    public Transform resultCopyTransformFrom;
    [Tooltip("Local position used only while this activity result plays.")]
    public Vector3 resultActivityPosition = Vector3.zero;
    [Tooltip("Local rotation used only while this activity result plays.")]
    public Vector3 resultActivityRotationEuler = Vector3.zero;
    [Tooltip("Local scale used only while this activity result plays.")]
    public Vector3 resultActivityScale = Vector3.one;
    [Tooltip("ON = return the result model to story position when the activity result finishes, resets, or replay starts.")]
    public bool resultRestoreTransformAfterAction = true;
    [HideInInspector] public bool resultHasSavedStoryPose = false;
    [HideInInspector] public Vector3 resultStoryPosition = Vector3.zero;
    [HideInInspector] public Vector3 resultStoryRotationEuler = Vector3.zero;
    [HideInInspector] public Vector3 resultStoryScale = Vector3.one;
    [NonSerialized] public Vector3 _resultOriginalLocalPosition;
    [NonSerialized] public Vector3 _resultOriginalLocalEulerAngles;
    [NonSerialized] public Vector3 _resultOriginalLocalScale;
    [NonSerialized] public bool _resultHasStoredTransform;

    [Header("Group / Target Set Action")]
    [Tooltip("How the tapped objects complete this activity. Any one, all, required objects, or a required count.")]
    public ActivityGroupCompletionMode groupCompletionMode = ActivityGroupCompletionMode.AnyAllowedObject;
    [Tooltip("Easy setup list. Add each tappable object and what should happen only for that object.")]
    public List<ActivityTargetAction> targetActions = new List<ActivityTargetAction>();
    [Tooltip("Older setup list. Use this if one tap should make the same group reaction play.")]
    public List<GameObject> groupTapObjects = new List<GameObject>();
    [Tooltip("Required objects for Required Objects mode when you are using the older Allowed Objects list.")]
    public List<GameObject> groupRequiredObjects = new List<GameObject>();
    [Tooltip("Used by Required Object Count mode. Example: 2 means any two unique objects must be tapped.")]
    public int groupRequiredObjectCount = 1;
    [Tooltip("ON = the same object cannot count twice. Recommended for greeting or collect-style activities.")]
    public bool groupIgnoreRepeatTaps = true;
    [Tooltip("Message shown if the child taps the same completed target again. Leave empty to show nothing.")]
    public string groupRepeatTapMessage = "Already done. Tap another one.";
    [Tooltip("ON = only the tapped object's own action plays. OFF = every action in the target list can play together.")]
    public bool groupPlayOnlyTappedObjectAction = true;
    [Tooltip("Older group reaction list. Use this if one tap should make several assigned objects react together.")]
    public List<ActivityGroupAction> groupActions = new List<ActivityGroupAction>();
    public float groupAutoStartStoryAfterSeconds = 0f;
    public bool groupPlayActionsWhenAutoSkipped = true;
    public float groupWaitSecondsBeforeStory = 0f;
    [Tooltip("Optional looping sound for the whole group action. Starts when the group animations start and stops when the group finishes.")]
    public AudioClip groupLoopSound;
    [Range(0f, 2f)] public float groupLoopSoundVolume = 1f;
    [Tooltip("ON = groupLoopSound loops until all group animations and voices finish.")]
    public bool loopGroupSoundUntilGroupFinishes = false;
    public AudioClip groupResultVoiceOver;
    [Range(0f, 2f)] public float groupResultVoiceVolume = 1f;
    public bool groupWaitForVoiceOver = true;

    [Header("Wait For Story Then Tap Object")]
    [Tooltip("The object the child should tap. Example: the drum.")]
    public GameObject storyMomentTapObject;
    [Tooltip("Choose whether the activity completes by tap count or active tapping time.")]
    public StoryMomentTapCompletionMode storyMomentCompletesBy = StoryMomentTapCompletionMode.RequiredTapCount;
    [Tooltip("Used when completion is by tap count.")]
    public int storyMomentRequiredTaps = 5;
    [Tooltip("Used when completion is by active tapping time.")]
    public float storyMomentRequiredTappingSeconds = 5f;
    [Tooltip("After each tap, this many seconds still count as active tapping.")]
    public float storyMomentTapActiveWindowSeconds = 0.35f;
    [Tooltip("ON = show a progress bar while the child taps.")]
    public bool storyMomentShowProgressBar = false;
    [Tooltip("Maximum time this activity can stay active. 0 means no maximum.")]
    public float storyMomentTotalActivitySeconds = 30f;
    [Tooltip("Show hint if there is no correct tap after this many seconds.")]
    public float storyMomentShowHintAfterSeconds = 5f;
    [Tooltip("After hint is shown, skip if there is still no correct input after this many seconds.")]
    public float storyMomentSkipAfterHintSeconds = 10f;
    [Tooltip("Text shown when the child needs help.")]
    public string storyMomentHintText = "Tap the highlighted object";
    [Tooltip("Sound played for each correct tap.")]
    public AudioClip storyMomentTapSound;
    [Range(0f, 2f)] public float storyMomentTapSoundVolume = 1f;
    [Tooltip("If ON, progress slowly goes down when the child stops tapping.")]
    public bool storyMomentProgressDropsIfChildStops = false;
    [Tooltip("How fast progress drops each second, in percent.")]
    public float storyMomentProgressDropSpeed = 15f;

    [Header("Tap Object Feedback")]
    [Tooltip("Object that rises and drops. Usually the whole object parent, not just the top piece.")]
    public Transform storyMomentMovingObject;
    [Tooltip("How high the object rises when progress reaches full.")]
    public float storyMomentMoveUpHeight = 0.25f;
    [Tooltip("How smoothly the object follows the progress height.")]
    public float storyMomentMoveSmoothness = 12f;
    [Tooltip("Small shake used for slow tapping.")]
    public float storyMomentSlowTapShake = 0.01f;
    [Tooltip("Bigger shake used for fast tapping.")]
    public float storyMomentFastTapShake = 0.05f;
    [Tooltip("How long each tap shake lasts.")]
    public float storyMomentTapShakeSeconds = 0.12f;
    [Tooltip("Tap speed that counts as fast tapping.")]
    public float storyMomentFastTapSpeed = 5f;
    [Tooltip("Recent seconds used to calculate tap speed.")]
    public float storyMomentTapSpeedWindowSeconds = 1f;

    [Header("Break Result")]
    [Tooltip("Object visible before completion. Example: unbroken top.")]
    public GameObject storyMomentObjectBeforeComplete;
    [Tooltip("Object visible after completion. Example: broken top.")]
    public GameObject storyMomentObjectAfterComplete;
    [Tooltip("Heavy shake before swapping to the broken object.")]
    public float storyMomentBreakShakeAmount = 0.08f;
    [Tooltip("How long the heavy break shake lasts.")]
    public float storyMomentBreakShakeSeconds = 0.45f;
    [Tooltip("When to switch objects during the shake. 0.5 = middle of shake.")]
    [Range(0f, 2f)] public float storyMomentSwitchAtShakePercent = 0.5f;
    [Tooltip("How long the moved object takes to drop back to its original Unity position.")]
    public float storyMomentDropBackSeconds = 0.45f;
    [Tooltip("Optional sound when the object breaks or changes.")]
    public AudioClip storyMomentBreakSound;
    [Range(0f, 2f)] public float storyMomentBreakSoundVolume = 1f;
    [Tooltip("Optional wait after the object has dropped back.")]
    public float storyMomentExtraWaitAfterComplete = 0.2f;

    [Header("Wrong Tap Feedback For Story Moment")]
    public string storyMomentWrongTapText = "Tap the highlighted object";
    public AudioClip storyMomentWrongTapSound;
    [Range(0f, 2f)] public float storyMomentWrongTapSoundVolume = 1f;

    [Header("Wrong Input Help")]
    public bool showHintWhenWrongInput = true;
    public string wrongInputHintText = "Try tapping the highlighted object";
    public AudioClip wrongInputSound;
    [Range(0f, 2f)] public float wrongInputSoundVolume = 1f;

    [Header("No Input Help")]
    public bool enableNoInputHelp = true;
    public float noInputHintAfterSeconds = 3f;
    public string noInputHintText = "Try the highlighted object";
    public ActivityNoInputAction noInputActionAfterHint = ActivityNoInputAction.AutoPlayResultThenContinue;
    public float autoSkipAfterHintSeconds = 3f;
    public bool useSameHintEffectsForNoInput = true;

    [Header("Target Highlight")]
    public bool pulseTargetObject = true;
    public float targetPulseScale = 1.18f;
    public int targetPulseRepeatCount = 3;
    public float targetPulseSeconds = 0.45f;
    public bool tintTargetObject = false;
    public Color targetTintColor = Color.yellow;
    public int targetTintRepeatCount = 3;
    public float targetTintSeconds = 0.45f;
    public bool showHintObject = false;
    public GameObject hintObject;
    public float hintObjectSeconds = 1.5f;

    public ActivityNextInputRule nextInputRule = ActivityNextInputRule.Immediately;
    public float nextInputDelaySeconds = 0f;
    [Tooltip("Optional. ON = show the ActivityPanel progress bar for this activity. OFF = no progress bar is shown, even if the activity uses counts or progress internally.")]
    public bool useProgressBar = false;
    [Tooltip("Choose how the visible progress bar increases. Input Progress follows valid taps or required objects. Activity Time fills with time. Fill When Result Plays stays empty until the activity result starts.")]
    public ActivityProgressBarFillMode progressBarFillMode = ActivityProgressBarFillMode.FollowInputProgress;
    [Tooltip("Simple beginner setting for how the visible progress bar behaves.")]
    public ActivityProgressBarBehavior progressBarBehavior = ActivityProgressBarBehavior.OnlyFillUp;
    [Tooltip("How many percent the progress bar should go down per second when the child stops. Used only by Go Down If Child Stops.")]
    public float progressGoDownPercentPerSecond = 10f;
    [Tooltip("Progress will not go below this percent. Keep 0 for normal setup. Example: 50 means the bar never goes below halfway.")]
    [Range(0f, 100f)] public float progressMinimumPercent = 0f;
    [Tooltip("Choose when result actions should play. This makes the same template support animation on every tap or only after required inputs.")]
    public ActivityResultPlayTiming resultPlayTiming = ActivityResultPlayTiming.AfterRequiredInputs;
    [Tooltip("Legacy. Kept only so old scenes do not lose data. Use Use Progress Bar in the beginner Inspector.")]
    public bool showTimerProgress = false;

    [Header("Choice Options")]
    [Tooltip("Recommended setup for choice activities. Each option can have its own text, animation, voice, narration, and correct/wrong setting.")]
    public List<ActivityChoiceOption> choiceOptions = new List<ActivityChoiceOption>();
    [Tooltip("Older simple button text list. Use only for simple two-button activities or older scenes.")]
    public List<string> optionTexts = new List<string> { "Option 1", "Option 2" };
    public int correctOptionIndex = 0;

    [Header("Choice Behaviour")]
    [Tooltip("If Continue Story Immediately is selected, the correct option does not need animation or audio. The normal story starts right away.")]
    public ActivityChoiceCorrectBehaviour choiceCorrectBehaviour = ActivityChoiceCorrectBehaviour.ContinueStoryImmediately;
    [Tooltip("Choose what happens to wrong options after the child selects them.")]
    public ActivityChoiceWrongOptionBehaviour choiceWrongOptionBehaviour = ActivityChoiceWrongOptionBehaviour.DisableAndGrayOut;
    [Tooltip("ON = hide option buttons while the selected option result plays. Recommended for story activities.")]
    public bool choiceHideUiWhileResultPlays = true;
    [Tooltip("ON = ignore other option taps while animation or audio is playing.")]
    public bool choiceBlockInputWhileResultPlays = true;
    [Tooltip("ON = show the question again after a wrong option result finishes.")]
    public bool choiceReturnQuestionAfterWrong = true;

    public List<ActivityReaction> reactions = new List<ActivityReaction>();

    public ActivityFinishRule finishWhen = ActivityFinishRule.AfterFirstValidInput;
    public bool waitForRunningReactionsBeforeFinish = true;
    public bool continueAfterComplete = true;
    public bool retryIfFailed = true;

    public string successMessage = "";
    public string tryAgainMessage = "Try again";
    public float successMessageSeconds = 0.8f;
    public float maxReactionWaitSeconds = 30f;

    public UnityEvent onActivityStarted;
    public UnityEvent onActivityCompleted;

    // ── ACTIVITY-LEVEL SOUNDS ──────────────────────────────────────────────
    [Header("Activity Sounds")]
    [Tooltip("Plays once the moment this activity begins, before the child does anything.")]
    public AudioClip activityStartSound;
    [Range(0f, 2f)] public float activityStartSoundVolume = 1f;

    [Tooltip("Plays after the child completes the activity, before the result animation starts.")]
    public AudioClip activityCompleteSound;
    [Range(0f, 2f)] public float activityCompleteSoundVolume = 1f;

    // ── CORRECT TAP SOUNDS ────────────────────────────────────────────────
    [Header("Correct Tap Sounds")]
    [Tooltip("Sound played on every correct tap. Shared across activity types that do not have their own tap sound. ProgressGate uses progressTapSound and WaitForStoryThenTapObject uses storyMomentTapSound.")]
    public AudioClip generalCorrectTapSound;
    [Range(0f, 2f)] public float generalCorrectTapSoundVolume = 1f;
    [Tooltip("Gap mode controls how fast the correct tap sound can repeat. Prevents noise on fast tapping.")]
    public CorrectTapSoundGapMode correctTapSoundGapMode = CorrectTapSoundGapMode.SmallGap_0_15s;
    [Tooltip("Used only when Gap Mode is Custom.")]
    public float correctTapCustomGapSeconds = 0.15f;

    // ── PROGRESS SOUNDS ───────────────────────────────────────────────────
    [Header("Progress Sounds")]
    [Tooltip("Plays when the progress bar drops because the child stopped tapping.")]
    public AudioClip progressDropSound;
    [Range(0f, 2f)] public float progressDropSoundVolume = 1f;

    [Tooltip("Plays the moment progress reaches 100 percent.")]
    public AudioClip progressFullSound;
    [Range(0f, 2f)] public float progressFullSoundVolume = 1f;

    // ── HINT SOUNDS ───────────────────────────────────────────────────────
    [Header("Hint Sounds")]
    [Tooltip("Sound played when the no-input hint appears (child has done nothing for too long).")]
    public AudioClip noInputHintSound;
    [Range(0f, 2f)] public float noInputHintSoundVolume = 1f;

    // wrongInputSound already exists at line 6124 — used here for wrong-input hint

    // ── RESULT SOUNDS ─────────────────────────────────────────────────────
    [Header("Result Sounds")]
    [Tooltip("Plays at the start of the result animation. Separate from resultSoundEffect which plays after.")]
    public AudioClip resultAnimationStartSound;
    [Range(0f, 2f)] public float resultAnimationStartSoundVolume = 1f;

    // ── ANIMATION WHILE TAPPING SOUNDS ───────────────────────────────────
    [Header("Animation While Tapping Sounds")]
    [Tooltip("Shared sound for all animation clips. Plays when progress enters any clip's range. Ignored if that clip has its own sound assigned below.")]
    public AudioClip sharedHelperAnimationSound;
    [Range(0f, 2f)] public float sharedHelperAnimationSoundVolume = 1f;
    [Tooltip("Optional looping sound for the whole helper animation group. Starts when helper animation begins and stops when the activity/helper animation ends.")]
    public AudioClip progressHelperGroupLoopSound;
    [Range(0f, 2f)] public float progressHelperGroupLoopSoundVolume = 1f;
    [Tooltip("ON = Progress Helper Group Loop Sound loops until the helper animation/activity ends.")]
    public bool loopProgressHelperGroupSoundUntilAnimationEnds = false;
    [Tooltip("Individual sound per animation clip. Position 1 matches clip 1, position 2 matches clip 2, and so on. Leave empty to use the shared sound above.")]
    public List<AudioClip> progressHelperAnimationSounds = new List<AudioClip>();
    [Tooltip("Volume per clip. Position 1 matches clip 1. Leave empty or shorter than the clip list to use 1.0 for remaining clips.")]
    public List<float> progressHelperAnimationSoundVolumes = new List<float>();

    // ── PROGRESS MILESTONES (Hints While Doing Well) ──────────────────────
    [Header("Hints While Doing Well")]
    [Tooltip("Add milestones that show encouraging text when the child reaches a progress percentage. Uses the same UI text slot as instruction and hint text.")]
    public List<ActivityProgressMilestone> progressMilestones = new List<ActivityProgressMilestone>();
}

/// <summary>
/// One milestone the setup person adds to a progress activity.
/// When progress crosses progressPercent the hint text shows and the sound plays.
/// </summary>
[Serializable]
public class ActivityProgressMilestone
{
    [Tooltip("Turn this milestone off without deleting it.")]
    public bool enabled = true;

    [Range(0f, 100f)]
    [Tooltip("Progress percentage (0-100) that triggers this milestone. Example: 50 = fires at halfway.")]
    public float progressPercent = 50f;

    [TextArea(1, 2)]
    [Tooltip("Text shown when this milestone is reached. Example: Keep going! You are halfway there!")]
    public string hintText = "";

    [Tooltip("How long this hint text stays visible in seconds before returning to the instruction text.")]
    public float displayDurationSeconds = 2f;

    [Tooltip("FireOnce = shows once per run. EveryTimeCrossed = fires every time progress goes up past this. FireAgainAfterDrop = fires again after progress drops below and rises above this.")]
    public MilestoneRepeatMode repeatMode = MilestoneRepeatMode.FireOnce;

    [Tooltip("Optional sound that plays when this milestone is reached.")]
    public AudioClip sound;
    [Range(0f, 2f)]
    public float soundVolume = 1f;

    // Runtime tracking — not saved to scene
    [NonSerialized] public bool _hasFired;
    [NonSerialized] public float _lastFiredAtProgress = -1f;
}

[Serializable]
public class ActivityTargetAction
{
    [Tooltip("Turn this item off without deleting it from the setup.")]
    public bool enabled = true;
    [Tooltip("Readable name for the Inspector. Example: First Character Greeting.")]
    public string actionName = "Target Action";
    [Tooltip("The object the child can tap. It must have a Collider.")]
    public GameObject tapObject;
    [Tooltip("ON = this target must be tapped when completion mode is Required Objects.")]
    public bool required = false;
    [Header("Activity Transform Optional")]
    [Tooltip("ON = temporarily move, rotate, or scale this object only for this activity action.")]
    public bool useActivityTransform = false;
    [Tooltip("Editor only. ON = show this activity pose in the Scene view while setting up. Turn OFF to restore the normal story pose.")]
    public bool previewActivityTransformInEditor = false;
    [Tooltip("Object to move or scale. If empty, Animator object is used.")]
    public GameObject objectToMoveOrScale;
    [Tooltip("Optional. Copy position, rotation, and scale from this transform instead of typing values manually.")]
    public Transform copyTransformFrom;
    [Tooltip("Local position while this activity action plays.")]
    public Vector3 activityPosition = Vector3.zero;
    [Tooltip("Local rotation while this activity action plays.")]
    public Vector3 activityRotationEuler = Vector3.zero;
    [Tooltip("Local scale while this activity action plays.")]
    public Vector3 activityScale = Vector3.one;
    [Tooltip("ON = restore original position, rotation, and scale after this action.")]
    public bool restoreTransformAfterAction = true;
    [HideInInspector] public bool hasSavedStoryPose = false;
    [HideInInspector] public Vector3 storyPosition = Vector3.zero;
    [HideInInspector] public Vector3 storyRotationEuler = Vector3.zero;
    [HideInInspector] public Vector3 storyScale = Vector3.one;
    [NonSerialized] public Vector3 _originalLocalPosition;
    [NonSerialized] public Vector3 _originalLocalEulerAngles;
    [NonSerialized] public Vector3 _originalLocalScale;
    [NonSerialized] public bool _hasStoredTransform;

    [Tooltip("Animator that should play after this target is tapped.")]
    public Animator animator;
    [Tooltip("Animation that should play after this target is tapped.")]
    public AnimationClip animationClip;
    [Tooltip("Animation speed. 1 is normal speed.")]
    public float animationSpeed = 1f;
    [Tooltip("ON = wait for this animation before the story continues.")]
    public bool waitForAnimation = false;
    [Tooltip("Optional short sound effect after this target is tapped.")]
    public AudioClip soundEffect;
    [Range(0f, 2f)] public float soundVolume = 1f;
    [Tooltip("Optional voice or dialogue after this target is tapped.")]
    public AudioClip voiceOver;
    [Range(0f, 2f)] public float voiceVolume = 1f;
    [Tooltip("ON = wait for the voice before the story continues.")]
    public bool waitForVoiceOver = false;
    [Tooltip("Objects turned ON after this target is tapped.")]
    public List<GameObject> objectsToTurnOn = new List<GameObject>();
    [Tooltip("Objects turned OFF after this target is tapped.")]
    public List<GameObject> objectsToTurnOff = new List<GameObject>();
    [Tooltip("Optional extra wait after this target action.")]
    public float extraWaitSeconds = 0f;
    [Tooltip("Optional UnityEvent for custom logic after this target is tapped.")]
    public UnityEvent onTapped;
}


public enum ActivityScenarioPlayMode
{
    OneByOne,
    Together
}

[Serializable]
public class ActivityChoiceOption
{
    [Tooltip("Text shown on the button.")]
    public string buttonText = "Option";
    [Tooltip("ON = this is the correct option that continues the story. Controlled by Correct Option Number in the Inspector.")]
    public bool isCorrect = false;
    [Tooltip("OFF = selecting this option only changes correct or wrong state. No scenario actions are played for this option.")]
    public bool playResultForThisOption = true;

    [Header("Scenario Actions")]
    [Tooltip("How this option scenario plays its action list. One By One is safer for story-style scenarios. Together starts all actions at the same time.")]
    public ActivityScenarioPlayMode scenarioPlayMode = ActivityScenarioPlayMode.OneByOne;
    [Tooltip("How many times the full scenario action list repeats. 1 means play once.")]
    public int scenarioRepeatCount = 1;
    [Tooltip("Each item is one action inside this option scenario. Add multiple actions if one button should animate several objects or play several clips.")]
    public List<ActivityScenarioAction> scenarioActions = new List<ActivityScenarioAction>();

    [Header("Legacy Single Result Optional")]
    [Tooltip("Legacy fallback. Used only if Scenario Actions is empty.")]
    public Animator animator;
    [Tooltip("Legacy fallback. Used only if Scenario Actions is empty.")]
    public AnimationClip animationClip;
    [Tooltip("Legacy fallback animation speed. 1 is normal speed.")]
    public float animationSpeed = 1f;
    [Tooltip("Legacy fallback. ON = wait for this animation before accepting the next choice or continuing.")]
    public bool waitForAnimation = true;
    [Tooltip("Legacy fallback optional short sound effect.")]
    public AudioClip soundEffect;
    [Range(0f, 2f)] public float soundVolume = 1f;
    [Tooltip("Legacy fallback optional voice line.")]
    public AudioClip voiceOver;
    [Range(0f, 2f)] public float voiceVolume = 1f;
    [Tooltip("Legacy fallback. ON = wait for the voice line before accepting the next choice or continuing.")]
    public bool waitForVoiceOver = true;
    [Tooltip("Legacy fallback optional narration clip.")]
    public AudioClip narration;
    [Range(0f, 2f)] public float narrationVolume = 1f;
    [Tooltip("Legacy fallback. ON = wait for narration before accepting the next choice or continuing.")]
    public bool waitForNarration = true;
    [Tooltip("Optional extra wait after this option plays.")]
    public float extraWaitSeconds = 0f;
    [Tooltip("Optional UnityEvent after this option is selected.")]
    public UnityEvent onSelected;
}

[Serializable]
public class ActivityScenarioAction
{
    [Tooltip("Turn this action off without deleting it.")]
    public bool enabled = true;
    [Tooltip("Simple name for this action. Example: Servant mop, Character reaction, Door opens.")]
    public string actionName = "Scenario Action";
    [Header("Activity Transform Optional")]
    [Tooltip("ON = temporarily move, rotate, or scale this object only while this scenario action plays.")]
    public bool useActivityTransform = false;
    [Tooltip("Editor only. ON = show this activity pose in the Scene view while setting up. Turn OFF to restore the normal story pose.")]
    public bool previewActivityTransformInEditor = false;
    [Tooltip("Object to move or scale. If empty, Animator object is used.")]
    public GameObject objectToMoveOrScale;
    [Tooltip("Optional. Copy position, rotation, and scale from this transform instead of typing values manually.")]
    public Transform copyTransformFrom;
    [Tooltip("Local position while this scenario action plays.")]
    public Vector3 activityPosition = Vector3.zero;
    [Tooltip("Local rotation while this scenario action plays.")]
    public Vector3 activityRotationEuler = Vector3.zero;
    [Tooltip("Local scale while this scenario action plays.")]
    public Vector3 activityScale = Vector3.one;
    [Tooltip("ON = restore original position, rotation, and scale after this scenario action.")]
    public bool restoreTransformAfterAction = true;
    [HideInInspector] public bool hasSavedStoryPose = false;
    [HideInInspector] public Vector3 storyPosition = Vector3.zero;
    [HideInInspector] public Vector3 storyRotationEuler = Vector3.zero;
    [HideInInspector] public Vector3 storyScale = Vector3.one;
    [NonSerialized] public Vector3 _originalLocalPosition;
    [NonSerialized] public Vector3 _originalLocalEulerAngles;
    [NonSerialized] public Vector3 _originalLocalScale;
    [NonSerialized] public bool _hasStoredTransform;

    [Tooltip("Animator that should play this action.")]
    public Animator animator;
    [Tooltip("Animation clip for this action.")]
    public AnimationClip animationClip;
    [Tooltip("Animation speed. 1 is normal. 2 is double speed. 0.5 is half speed.")]
    public float animationSpeed = 1f;
    [Tooltip("How many times this animation repeats before this action is finished.")]
    public int animationLoopCount = 1;
    [Tooltip("ON = wait for this animation and its loop count before moving to the next action.")]
    public bool waitForAnimation = true;
    [Tooltip("Optional sound effect for this action.")]
    public AudioClip soundEffect;
    [Range(0f, 2f)] public float soundVolume = 1f;
    [Tooltip("Optional voice line for this action.")]
    public AudioClip voiceOver;
    [Range(0f, 2f)] public float voiceVolume = 1f;
    [Tooltip("ON = wait for voice line before moving to the next action.")]
    public bool waitForVoiceOver = true;
    [Tooltip("Optional narration clip for this action.")]
    public AudioClip narration;
    [Range(0f, 2f)] public float narrationVolume = 1f;
    [Tooltip("ON = wait for narration before moving to the next action.")]
    public bool waitForNarration = true;
    [Tooltip("Objects to turn ON when this action plays.")]
    public List<GameObject> objectsToTurnOn = new List<GameObject>();
    [Tooltip("Objects to turn OFF when this action plays.")]
    public List<GameObject> objectsToTurnOff = new List<GameObject>();
    [Tooltip("Optional wait after this action.")]
    public float extraWaitSeconds = 0f;
    [Tooltip("Optional advanced event for this action.")]
    public UnityEvent onActionPlayed;
}

[Serializable]
public class ActivityGroupAction
{
    public bool enabled = true;
    public string actionName = "Group Action Item";
    [Header("Activity Transform Optional")]
    [Tooltip("ON = temporarily move, rotate, or scale this object while this action plays.")]
    public bool useActivityTransform = false;
    [Tooltip("Editor only. ON = show this activity pose in the Scene view while setting up. Turn OFF to restore the normal story pose.")]
    public bool previewActivityTransformInEditor = false;
    [Tooltip("Object to move or scale. If empty, Animator object is used.")]
    public GameObject objectToMoveOrScale;
    [Tooltip("Optional. Copy position, rotation, and scale from this transform instead of typing values manually.")]
    public Transform copyTransformFrom;
    [Tooltip("Local position while this activity action plays.")]
    public Vector3 activityPosition = Vector3.zero;
    [Tooltip("Local rotation while this activity action plays.")]
    public Vector3 activityRotationEuler = Vector3.zero;
    [Tooltip("Local scale while this activity action plays.")]
    public Vector3 activityScale = Vector3.one;
    [Tooltip("ON = restore original position, rotation, and scale after this action.")]
    public bool restoreTransformAfterAction = true;
    [HideInInspector] public bool hasSavedStoryPose = false;
    [HideInInspector] public Vector3 storyPosition = Vector3.zero;
    [HideInInspector] public Vector3 storyRotationEuler = Vector3.zero;
    [HideInInspector] public Vector3 storyScale = Vector3.one;
    [NonSerialized] public Vector3 _originalLocalPosition;
    [NonSerialized] public Vector3 _originalLocalEulerAngles;
    [NonSerialized] public Vector3 _originalLocalScale;
    [NonSerialized] public bool _hasStoredTransform;
    public Animator animator;
    public AnimationClip animationClip;
    public float animationSpeed = 1f;
    public bool waitForAnimation = true;
    public AudioClip soundEffect;
    [Range(0f, 2f)] public float soundVolume = 1f;
    public AudioClip voiceLine;
    [Range(0f, 2f)] public float voiceVolume = 1f;
    public bool waitForVoiceLine = false;
}

[Serializable]
public class ActivityReaction
{
    public bool enabled = true;
    public string reactionName = "New Reaction";
    public ActivityReactionType type = ActivityReactionType.VisualEffect;
    public ActivityReactionMoment playWhen = ActivityReactionMoment.EveryValidInput;

    [Header("Visual Effect Play Style")]
    public VisualEffectPlayMode visualEffectPlayMode = VisualEffectPlayMode.AddNewEachInput;
    [Tooltip("ON = assigned 3D source objects are hidden when the activity starts. Only spawned copies appear after child input.")]
    public bool hideSourceObjectsUntilPlayed = true;
    [Tooltip("Choose whether spawned 3D copies appear from one point or spread across the page area.")]
    public ActivityVfxSpawnAreaMode spawnAreaMode = ActivityVfxSpawnAreaMode.FromSourceOrSpawnPoint;
    [Tooltip("Used by Spread Across Page. X is left/right spread, Y is up/down spread, in world units.")]
    public Vector2 pageSpreadSize = new Vector2(1.2f, 0.7f);
    [Tooltip("Optional rectangle/box area. Petals spawn randomly inside this area. Recommended for King welcome flower shower.")]
    public Transform rectangleSpawnArea;
    [Tooltip("Used if Rectangle Area is selected but no helper object is assigned. X = width, Z = depth.")]
    public Vector3 rectangleSpawnAreaSize = new Vector3(1.8f, 0.35f, 1.2f);
    [Tooltip("ON = spawned 3D copies fade before disappearing. Works best with transparent-capable materials.")]
    public bool fadeOutSpawnedObjects = true;
    [Tooltip("How long the spawned 3D copy fades before disappearing.")]
    public float fadeOutSeconds = 0.35f;

    [Header("Timing")]
    public float startDelaySeconds = 0f;
    public float extraWaitSeconds = 0f;
    public float cooldownSeconds = 0f;
    public int maxTriggerCount = 0;
    public float reactionDurationSeconds = 0f;

    [Header("Reaction Audio")]
    public AudioClip optionalSfx;
    public ReactionSfxMode sfxMode = ReactionSfxMode.PlayOnce;
    [Range(0f, 2f)] public float sfxVolume = 1f;
    public float sfxMinimumGapSeconds = 0f;
    public AudioClip reactionVoiceOver;
    [Range(0f, 2f)] public float reactionVoiceVolume = 1f;
    public bool waitForReactionVoiceOver = false;
    public bool stopVoiceWhenReactionEnds = false;

    [Header("Visual Effect")]
    public List<GameObject> vfxObjects = new List<GameObject>();
    public Transform vfxSpawnOrigin;
    public int particleBurstCount = 25;
    public int objectBurstCount = 1;
    public float objectLifeSeconds = 2f;
    public float objectSpreadRadius = 0.15f;
    public float objectLaunchForce = 0f;
    public bool randomizeObjectRotation = true;
    public bool keepSpawnedObjectsInWorldSpace = false;

    [Header("Falling 3D Object Effect Optional")]
    public bool make3DObjectsFall = false;
    public FallingObjectMotion fallingMotion = FallingObjectMotion.FlutterFall;
    public float fallDistance = 1.2f;
    public float fallDurationSeconds = 1.6f;
    [Tooltip("Random extra delay before each copy appears. Example: 0.35 means petals start at different moments within 0.35 seconds.")]
    public float randomStartDelayMaxSeconds = 0.35f;
    [Tooltip("Random change added to fall time so some petals fall fast and some fall slow.")]
    public float randomFallTimeExtraSeconds = 0.5f;
    public float fallSpreadSideways = 0.25f;
    public float fallSpinDegrees = 180f;
    public float fallFlutterAmount = 0.08f;
    [Tooltip("Smallest random size for each spawned copy. 1 means original size.")]
    public float randomScaleMin = 0.85f;
    [Tooltip("Largest random size for each spawned copy. 1 means original size.")]
    public float randomScaleMax = 1.15f;

    [Header("Activity Transform Optional")]
    public bool useActivityTransform = false;
    public bool previewActivityTransformInEditor = false;
    public GameObject objectToMoveOrScale;
    public Transform copyTransformFrom;
    public Vector3 activityPosition = Vector3.zero;
    public Vector3 activityRotationEuler = Vector3.zero;
    public Vector3 activityScale = Vector3.one;
    public bool restoreTransformAfterAction = true;
    [HideInInspector] public bool hasSavedStoryPose = false;
    [HideInInspector] public Vector3 storyPosition = Vector3.zero;
    [HideInInspector] public Vector3 storyRotationEuler = Vector3.zero;
    [HideInInspector] public Vector3 storyScale = Vector3.one;
    [NonSerialized] public Vector3 _originalLocalPosition;
    [NonSerialized] public Vector3 _originalLocalEulerAngles;
    [NonSerialized] public Vector3 _originalLocalScale;
    [NonSerialized] public bool _hasStoredTransform;

    [Header("Animation")]
    public Animator animator;
    public AnimationClip animationClip;
    [Tooltip("Optional. Use this when one reaction should choose from or play multiple animation clips.")]
    public List<AnimationClip> animationClips = new List<AnimationClip>();
    [Tooltip("Choose how the animation clips are played.")]
    public ActivityReactionAnimationPlayMode animationPlayMode = ActivityReactionAnimationPlayMode.SelectedClipOnly;
    public float animationSpeed = 1f;
    public bool doNotRestartWhilePlaying = true;
    public bool blocksNextInput = false;

    [Header("Audio / Voice")]
    public AudioClip mainAudio;
    [Range(0f, 2f)] public float mainAudioVolume = 1f;

    [Header("Objects")]
    public List<GameObject> objects = new List<GameObject>();

    [Header("Material Color")]
    public Renderer targetRenderer;
    public Color targetColor = Color.white;
    public float colorChangeSeconds = 0.25f;
    public float restoreColorAfterSeconds = 0f;

    [Header("Move Object")]
    public Transform objectToMove;
    public Transform moveTarget;
    public Vector3 moveOffset;
    public float moveDurationSeconds = 1f;

    [Header("Custom")]
    public UnityEvent customAction;

    public bool waitUntilFinished = true;
    public bool showAdvancedOptions = false;
}

// ── New enums added for SFX system and milestone hints ──────────────────────

/// <summary>Controls when a progress milestone hint fires again after it has fired once.</summary>
public enum MilestoneRepeatMode
{
    /// <summary>Fires one time only per activity run. Does not fire again even if progress drops.</summary>
    FireOnce,
    /// <summary>Fires every time progress crosses this percentage going upward.</summary>
    EveryTimeCrossed,
    /// <summary>Fires again only if progress drops below this percentage and then crosses it again going up.</summary>
    FireAgainAfterDrop
}

/// <summary>Controls the minimum gap between plays of the correct tap sound so it does not feel noisy.</summary>
public enum CorrectTapSoundGapMode
{
    /// <summary>Every correct tap plays the sound, no gap enforced.</summary>
    NoGap,
    /// <summary>Minimum 0.1 second gap between plays.</summary>
    TinyGap_0_1s,
    /// <summary>Minimum 0.15 second gap between plays. Good default for most drum activities.</summary>
    SmallGap_0_15s,
    /// <summary>Minimum 0.2 second gap between plays.</summary>
    MediumGap_0_2s,
    /// <summary>Minimum 0.3 second gap between plays.</summary>
    LargeGap_0_3s,
    /// <summary>Use the Custom Gap Seconds value below.</summary>
    Custom
}
