using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Video;
using UnityEngine.UI;

public enum PageType { TwoD, ThreeD }

// Per-clip settings for a single Main Video. Index-matched to the mainVideos list.
// LEGACY — kept for existing prefab assignments. Do not rename or remove.
[System.Serializable]
public class MainVideoSettings
{
    public VuforiaVideoFrameFreezeController.FreezeMode freezeMode =
        VuforiaVideoFrameFreezeController.FreezeMode.None;
    [Min(0f)] public float freezeFirstSeconds = 0f;
    [Min(0f)] public float freezeLastSeconds = 0f;
    [Range(0.25f, 3f)] public float playbackSpeed = 1f;
    [Min(0f)] public float startDelay = 0f;
    public bool waitForPageEnd = false;
}

// Per-clip settings for a single Background Loop Video. Index-matched to backgroundLoopVideos.
// LEGACY — kept for existing prefab assignments. Do not rename or remove.
[System.Serializable]
public class BackgroundVideoSettings
{
    [Range(0.25f, 3f)] public float playbackSpeed = 1f;
    [Min(0f)] public float startDelay = 0f;
}

// ── 2D Story Parts — enums ──────────────────────────────────────────────────

public enum PartTiming2D
{
    AutoFromMainVideo,
    ManualDuration,
    WaitForPageAudioEnd,
    WaitForTap
}

public enum SmallMotionType
{
    None,
    DriftX,
    DriftY,
    PingPongX,
    PingPongY
}

public enum TimedAction2D
{
    Show,
    Hide,
    FadeIn,
    FadeOut,
    PlayVideo,
    StopVideo,
    PauseVideo,
    ResumeVideo,
    SetVideoSpeed
}

public enum PageEndTrigger3D
{
    VoiceEnd,       // Default — OverlayManager listens to ARMediaManager.OnVoiceCompleted (old behavior unchanged)
    AnimationEvent, // Call TriggerPageEnd() from an Animation Event on this prefab
    Manual          // Call StartPageEndFade() from a custom script or button
}

// ── 2D Story Parts — data classes ──────────────────────────────────────────

/// <summary>
/// One main video entry inside a Story Part.
/// VideoPlayer and all its settings are together in one Inspector block — no separate settings list.
/// </summary>
[System.Serializable]
public class StoryPartVideo2D
{
    [Tooltip("The VideoPlayer to play for this main video slot.")]
    public VideoPlayer video;

    [Tooltip("Playback speed multiplier. 1 = normal speed.")]
    [Range(0.1f, 3f)] public float playbackSpeed = 1f;

    [Tooltip("Seconds to wait before this video starts playing after the part begins.")]
    [Min(0f)] public float startDelay = 0f;

    [Tooltip("Freeze mode for the first or last frame of this video.")]
    public VuforiaVideoFrameFreezeController.FreezeMode freezeMode =
        VuforiaVideoFrameFreezeController.FreezeMode.None;

    [Tooltip("Hold the first frame for this many seconds before playing (freeze first frame).")]
    [Min(0f)] public float freezeFirstSeconds = 0f;

    [Tooltip("Hold the last frame for this many seconds after the video content ends.")]
    [Min(0f)] public float freezeLastSeconds = 0f;

    [Tooltip("Extra delay after this video ends before the part advances (only applies when this video gates part end).")]
    [Min(0f)] public float delayAfterFinish = 0f;

    [Tooltip("If ON, this video loops and will never naturally end.")]
    public bool loop = false;

    [Tooltip("If ON, the part waits for THIS video to finish before moving to the next part (Auto From Main Video timing).\n\nWARNING: Do not check this on a looping video — the part may never advance.")]
    public bool waitForPartEnd = false;

    [Tooltip("Stop this video when the part ends.")]
    public bool stopAtPartEnd = true;

    [Tooltip("Hide (deactivate) this video's GameObject when the part ends.")]
    public bool hideAtPartEnd = false;
}

/// <summary>
/// One background video entry inside a Story Part.
/// Background videos do not gate part ending unless explicitly set.
/// </summary>
[System.Serializable]
public class StoryPartBgVideo2D
{
    [Tooltip("The background VideoPlayer for this part.")]
    public VideoPlayer video;

    [Tooltip("Playback speed multiplier.")]
    [Range(0.1f, 3f)] public float playbackSpeed = 1f;

    [Tooltip("Seconds to wait before this background video starts.")]
    [Min(0f)] public float startDelay = 0f;

    [Tooltip("Loop this background video.")]
    public bool loop = true;

    [Tooltip("Hold the first frame for this many seconds before the background video starts playing.")]
    [Min(0f)] public float freezeFirstSeconds = 0f;

    [Tooltip("Hold the last frame for this many seconds after the background video content ends.")]
    [Min(0f)] public float freezeLastSeconds = 0f;

    [Tooltip("Stop this background video when the part ends.")]
    public bool stopAtPartEnd = true;

    [Tooltip("Hide (deactivate) this video's GameObject when the part ends.")]
    public bool hideAtPartEnd = false;

    [Tooltip("Fade this video layer in when the part starts.")]
    public bool fadeIn = false;
    [Min(0f)] public float fadeInDuration = 0.3f;

    [Tooltip("Fade this video layer out when the part ends.")]
    public bool fadeOut = false;
    [Min(0f)] public float fadeOutDuration = 0.3f;
}

/// <summary>
/// One visual layer (background image, PNG, effect, extra image) inside a Story Part.
/// Use this for any image or sprite layer — background, foreground, effect, or fix layer.
/// Small motion moves only the motionTarget CHILD, never the parallax parent.
/// </summary>
[System.Serializable]
public class VisualLayer2D
{
    [Tooltip("The layer GameObject or Transform (background, PNG, sprite, effect). Drag any object here.")]
    public Transform layer;

    [Tooltip("Show this layer when the part starts.")]
    public bool showAtPartStart = true;

    [Tooltip("Seconds to wait after the part starts before showing this layer.")]
    [Min(0f)] public float startDelay = 0f;

    [Tooltip("Fade this layer in when it appears.")]
    public bool fadeIn = true;
    [Min(0f)] public float fadeInDuration = 0.4f;

    [Tooltip("Hide this layer when the part ends.")]
    public bool hideAtPartEnd = true;

    [Tooltip("Fade this layer out when the part ends (only if Hide At Part End is ON).")]
    public bool fadeOut = true;
    [Min(0f)] public float fadeOutDuration = 0.3f;

    [Tooltip("Keep this layer visible when the next part starts. Overrides Hide At Part End.")]
    public bool keepVisibleIntoNextPart = false;

    [Tooltip("Optional: hide this layer after this many seconds from when it appeared. 0 = stay until part ends.")]
    [Min(0f)] public float visibleDuration = 0f;

    [Tooltip("Enable slow looping motion on the Motion Target child. Safe — does not move the parallax parent.")]
    public bool enableSmallMotion = false;

    [Tooltip("CHILD object to move for small motion. Must be a child of 'layer'. Do NOT assign the same object Parallex_With_Animation controls.")]
    public Transform motionTarget;

    public SmallMotionType motionType = SmallMotionType.DriftX;
    [Range(0.05f, 5f)] public float motionSpeed = 0.5f;
    [Range(0f, 0.3f)] public float motionAmplitude = 0.02f;
}

/// <summary>
/// A persistent layer visible for the entire page — sky, frame, shared background, static scenery.
/// Shown at page start, never hidden between Story Parts.
/// </summary>
[System.Serializable]
public class PersistentLayerSetup
{
    [Tooltip("The layer that should be visible for the full page (sky, frame, shared background).")]
    public Transform layer;

    [Tooltip("Show and activate this layer when the page starts.")]
    public bool showOnPageStart = true;

    [Tooltip("Fade this layer in when the page starts.")]
    public bool fadeIn = true;
    [Min(0f)] public float fadeInDuration = 0.4f;

    [Tooltip("Fade this layer out at the end of the last Story Part (before page-end black fade).")]
    public bool fadeOutAtPageEnd = false;
    [Min(0f)] public float fadeOutDuration = 0.3f;

    [Tooltip("Enable slow looping motion on a child visual object (motionTarget). Does not move the parallax parent.")]
    public bool enableSmallMotion = false;

    [Tooltip("CHILD object to move. Must be a child of 'layer'. Do NOT assign the parallax parent.")]
    public Transform motionTarget;

    public SmallMotionType motionType = SmallMotionType.DriftX;
    [Range(0.05f, 5f)] public float motionSpeed = 0.5f;
    [Range(0f, 0.3f)] public float motionAmplitude = 0.02f;
}

/// <summary>
/// A timed change fires at a specific second into the current Story Part.
/// Timing is pausable — freezes when tracking is lost, resumes from the same point.
/// </summary>
[System.Serializable]
public class TimedChange2D
{
    [Tooltip("Seconds into this Story Part when this action fires. Pausable — freezes when tracking is lost.")]
    [Min(0f)] public float atSeconds = 2f;

    public TimedAction2D action = TimedAction2D.Show;

    [Tooltip("The target GameObject. For video actions (Play/Stop/Pause/Resume/SetSpeed), this object must have a VideoPlayer component.")]
    public GameObject target;

    [Tooltip("Fade duration for FadeIn / FadeOut actions.")]
    [Min(0f)] public float fadeDuration = 0.4f;

    [Tooltip("Target speed for SetVideoSpeed action.")]
    [Range(0.1f, 3f)] public float videoSpeed = 1f;
}

/// <summary>
/// One sequential visual section of a 2D page.
/// Part 1 → Part 2 → ... → last part → page end fade.
/// </summary>
[System.Serializable]
public class StoryPart2D
{
    [Tooltip("Label for this part in the Inspector. Does not affect runtime.")]
    public string partName = "Part";

    [Tooltip("How the part decides when to move to the next part.\n• Auto From Main Video: advances when the main video(s) finish.\n• Manual Duration: advances after a fixed number of seconds.\n• Wait For Page Audio End: waits for voice audio to finish (uses ARMediaManager.OnVoiceCompleted).\n• Wait For Tap: waits for a screen tap.")]
    public PartTiming2D timing = PartTiming2D.AutoFromMainVideo;

    [Tooltip("Seconds before advancing to the next part. Used when timing is Manual Duration, or as fallback when no non-looping main video is assigned.")]
    [Min(0.1f)] public float manualDuration = 3f;

    [Tooltip("Background image shown behind the main video for this slot. Hides automatically when the slot ends.")]
    public Transform backgroundImage;

    [Tooltip("If true, the background video restarts from the beginning each time this slot starts. If false, it continues from where it was (useful when the same background video is reused across slots).")]
    public bool restartBackgroundOnSlotStart = true;

    [Tooltip("Main story videos for this part. Each entry includes the VideoPlayer and all its settings.\n\nPart advances when the checked 'Wait For Part End' videos finish. If none are checked, waits for all non-looping videos.")]
    public List<StoryPartVideo2D> mainVideos = new List<StoryPartVideo2D>();

    [Tooltip("Background images, PNGs, sprites, or effect layers for this part. Each entry has its own show/hide/fade/motion settings.")]
    public List<VisualLayer2D> visualLayers = new List<VisualLayer2D>();

    [Tooltip("Background loop videos for this part. Background videos do not gate part ending unless Stop At Part End is used.")]
    public List<StoryPartBgVideo2D> backgroundVideos = new List<StoryPartBgVideo2D>();

    [Tooltip("Optional timed actions that fire at specific seconds during this part. Timing pauses when tracking is lost.")]
    public List<TimedChange2D> timedChanges = new List<TimedChange2D>();
}

// ───────────────────────────────────────────────────────────────────────────

// One 3D Model Slot — shown on its own, then hidden, before the next slot appears.
// The model's own Animator (if any) plays its animation automatically once the
// model is active — this does not trigger or control any animation itself.
[System.Serializable]
public class ModelSlot3D
{
    [Tooltip("The 3D model to show for this slot.")]
    public Transform model;

    [Tooltip("Position to place the model at when this slot starts. Adjust here directly instead of in the Hierarchy.")]
    public Vector3 position;

    [Tooltip("OFF (default) = use Show Duration below.\nON = automatically use this model's own animation length instead — no need to time it by hand.")]
    public bool matchAnimationLength = false;

    [Tooltip("How many seconds this model stays visible before hiding. Ignored if 'Match Animation Length' is ON.")]
    [Min(0f)] public float showDuration = 3f;

    [Tooltip("Pause after this model hides, before the next one appears. 0 = instant.")]
    [Min(0f)] public float gapBeforeNext = 0f;

    [Tooltip("OFF (default) = model switches on/off instantly, exactly as before.\nON = model grows in and shrinks out smoothly instead of an instant switch.")]
    public bool scaleInOut = false;

    [Tooltip("How long the grow-in / shrink-out takes, in seconds. Only used if 'Scale In/Out' is ON.")]
    [Min(0.01f)] public float scaleDuration = 0.4f;

    [Tooltip("Optional sound effect that plays the instant this model turns on. Leave empty for no sound.")]
    public AudioClip sfxClip;

    [Tooltip("Volume for the sound effect above. Only used if a clip is assigned.")]
    [Range(0f, 1f)] public float sfxVolume = 1f;

    // Internal only — the model's normal size, captured automatically the first
    // time it's assigned, so Scale In/Out knows what size to grow back to.
    [HideInInspector] public Vector3 originalScale = Vector3.one;

    // Internal only — used to detect when a new model was just dragged in, so its
    // current position can be auto-captured. Not shown in the Inspector.
    [HideInInspector] public Transform lastModel;
}

// ───────────────────────────────────────────────────────────────────────────

public class ARTrackedPageNode : MonoBehaviour
{
    [Header("IDs")]
    [SerializeField] private string pageId;

    [Header("References")]
    [SerializeField] private ARMediaManager mediaManager;

    [Header("Page Type")]
    [Tooltip("TwoD: shows the 2D Story Parts setup. ThreeD: shows the 3D setup with Animators and Splines.\nThe Inspector panel switches based on this selection.")]
    [SerializeField] private PageType pageType = PageType.TwoD;

    // ---------------------------------------------------------------
    // 2D PAGE SETUP
    // Shown in Inspector when Page Type = Two D.
    // Active at runtime only when storyParts has entries.
    // If storyParts is empty, the Legacy / Simple Video setup runs unchanged.
    // ---------------------------------------------------------------

    [Tooltip("The parent object that contains all 2D layers (sky, city, background, video, effects). Usually All_GameObject. Assign here so setup is clear.")]
    [SerializeField] private Transform layerRoot2D;

    [Tooltip("Background image visible for the FULL PAGE. Shown at page start, kept visible across ALL Story Parts, never hidden between parts. Use this for a shared scene background that never changes.")]
    [SerializeField] private Transform commonBackgroundImage;

    [Tooltip("Layers visible for the ENTIRE page — sky, frame, shared background, static scenery.\nShown at page start, never hidden between Story Parts.")]
    [SerializeField] private List<PersistentLayerSetup> persistentLayers2D = new List<PersistentLayerSetup>();

    [Tooltip("Sequential Story Parts for this 2D page. Each part plays its videos and shows its layers, then advances to the next.\n\nLEAVE EMPTY → old Legacy / Simple Video flow runs unchanged.")]
    [SerializeField] private List<StoryPart2D> storyParts = new List<StoryPart2D>();

    // ---------------------------------------------------------------
    // LEGACY / SIMPLE VIDEO SETUP
    // Used for old pages or when Story Parts is empty.
    // On 3D pages, Animators and Spline Movers handle the story.
    // These fields have existing prefab assignments — do not rename or remove.
    // ---------------------------------------------------------------

    [SerializeField] private List<VideoPlayer> mainVideos = new();
    [SerializeField] private List<VideoPlayer> backgroundLoopVideos = new();

    // Per-video settings, index-matched to the lists above.
    // ADDITIVE ONLY — never reorder/rename mainVideos or backgroundLoopVideos (that is where the
    // assigned VideoPlayer references live). These parallel lists only add per-clip control.
    [SerializeField] private List<MainVideoSettings> mainVideoSettings = new();
    [SerializeField] private List<BackgroundVideoSettings> backgroundVideoSettings = new();

    [Header("Animators (optional)")]
    [SerializeField] private List<Animator> animators = new();

    [Header("Spline movers (optional)")]
    [SerializeField] private List<ARTrackableSplineMover> splineMovers = new();

    [Header("Spline Path Movers (optional)")]
    [SerializeField] private List<SplinePathMover> splinePathMovers = new();

    [Header("Model Slots (optional) — shows one 3D model at a time, in order")]
    [SerializeField] private List<ModelSlot3D> modelSlots = new();

    [Tooltip("How page end is triggered on 3D pages. VoiceEnd (default) matches old behavior exactly.")]
    [SerializeField] private PageEndTrigger3D pageEndTrigger3D = PageEndTrigger3D.VoiceEnd;
    public PageEndTrigger3D PageEndTrigger => pageEndTrigger3D;

    [Header("Video Freeze (per page)")]
    [SerializeField]
    private VuforiaVideoFrameFreezeController.FreezeMode freezeMode =
        VuforiaVideoFrameFreezeController.FreezeMode.None;

    [Min(0f)] public float freezeFirstSeconds = 0f;
    [Min(0f)] public float freezeLastSeconds = 0f;

    [Header("BGM behavior")]
    [SerializeField] private bool loopBgmUntilVoiceEnds = true;
    [SerializeField] private bool stopBgmWhenVoiceEnds = true;

    // ---------------------------------------------------------------
    // PAGE END EFFECT
    // Per-page only: black overlay (world space child of this page node).
    // PageEndPanel and all characters are set ONCE on OverlayManager.
    // ---------------------------------------------------------------

    [Header("Page End Effect")]

    [Tooltip("Black plane child of this page node. Name it 'Black Screen'. Auto-found if left empty.")]
    [SerializeField] private Renderer blackOverlay;

    [Tooltip("Drag All_GameObjects here (2D pages). Auto-found if left empty. 3D pages leave empty.")]
    [SerializeField] private GameObject contentRoot;

    [Tooltip("Seconds to wait after audio ends before black fades in.")]
    [Min(0f)]
    [SerializeField] private float delayBeforeFade = 0.5f;

    [Tooltip("How long the black plane fades in (seconds).")]
    [Min(0f)]
    [SerializeField] private float fadeDuration = 1.5f;

    [Tooltip("Seconds to hold black before turn page overlay appears.")]
    [Min(0f)]
    [SerializeField] private float postFadeDelay = 0.5f;

    [Tooltip("Optional sound played when the page end panel appears.")]
    [SerializeField] private AudioClip pageTurnSound;

    // private state
    private Coroutine _revealCoroutine;
    private AudioSource _revealAudioSource;

    // 2D Story Parts runtime — only these coroutines are stopped on replay/reset
    private Coroutine _storyPartsRoutine;
    private Coroutine _timedChangesRoutine;
    private readonly List<Coroutine> _layerRoutines2D = new List<Coroutine>();
    private readonly List<Coroutine> _smallMotionRoutines = new List<Coroutine>();

    // 2D safety state
    // These lists keep runtime truth aligned with the Inspector: every active 2D video,
    // including timed-change videos, can be paused/resumed on tracking lost/found.
    private readonly List<VideoPlayer> _active2DVideos = new List<VideoPlayer>();
    private readonly Dictionary<VideoPlayer, VideoFreezeRuntime> _active2DVideoRuntime = new Dictionary<VideoPlayer, VideoFreezeRuntime>();
    private readonly Dictionary<Transform, Coroutine> _smallMotionByTarget2D = new Dictionary<Transform, Coroutine>();
    private readonly Dictionary<Transform, Vector3> _smallMotionBasePosition2D = new Dictionary<Transform, Vector3>();
    private System.Action<string> _audioEndHandler2D;
    private bool _audioEndReceived2D;

    private bool _2dPaused;
    private int _currentPartIndex2D = -1;

    // 3D Model Slots runtime
    private Coroutine _modelSlotsRoutine;
    private bool _3dSlotsPaused;
    private int _currentModelSlotIndex = -1;

    // Names kept visible after fade (frame stays, black screen removed separately)
    private static readonly string[] _keepVisible = {
        "frame_new_bendender", "frame", "Black Screen", "BlackOverlay", "black page"
    };
    // ---------------------------------------------------------------

    private bool _isTracked;
    private float _lastLostTime = -999f;

    private System.Action _onStorySystemsStarted;
    private bool _waitingForPopupReveal;

    // True while an interaction step must run before the normal story starts.
    private bool _storyBlockedByActivity;
    private bool _mediaAudioPausedForActivity;

    public string PageId => pageId;
    public PageType Type => pageType;
    public bool IsTracked => _isTracked;
    public bool LoopBgmUntilVoiceEnds => loopBgmUntilVoiceEnds;
    public bool StopBgmWhenVoiceEnds => stopBgmWhenVoiceEnds;

    public bool IsStoryBlockedByActivity => _storyBlockedByActivity;

    private readonly Dictionary<VideoPlayer, VideoFreezeRuntime> _videoRuntime = new();

    // Runs automatically in the Editor whenever a field changes in the Inspector.
    // Two jobs for Model Slots:
    //   1. When a NEW model is dragged into a slot, capture its current position
    //      automatically instead of leaving Position at 0,0,0.
    //   2. Whenever Position is edited afterward, move the model there immediately
    //      so you can see exactly where it'll be without pressing Play.
    private void OnValidate()
    {
        if (modelSlots == null) return;

        foreach (var slot in modelSlots)
        {
            if (slot == null) continue;

            if (slot.model != slot.lastModel)
            {
                slot.lastModel = slot.model;
                if (slot.model != null)
                {
                    slot.position = slot.model.localPosition;
                    if (slot.model.localScale != Vector3.zero)
                        slot.originalScale = slot.model.localScale;
                }
            }

            if (slot.model != null)
                slot.model.localPosition = slot.position;
        }
    }

    private void Awake()
    {
        if (mediaManager == null)
        {
#if UNITY_2023_1_OR_NEWER
            mediaManager = Object.FindFirstObjectByType<ARMediaManager>();
#else
            mediaManager = Object.FindObjectOfType<ARMediaManager>();
#endif
        }

        if (splinePathMovers.Count == 0)
        {
            var found = GetComponentsInChildren<SplinePathMover>(true);
            foreach (var m in found)
                splinePathMovers.Add(m);
        }

        RebuildSplineMoverLists();

        if (animators.Count == 0)
        {
            var found = GetComponentsInChildren<Animator>(true);
            foreach (var anim in found)
                if (anim != null) animators.Add(anim);
        }

        RebuildVideoRuntimeCache();

        SetupReveal();
    }

    private void RebuildSplineMoverLists()
    {
        if (splineMovers == null) splineMovers = new List<ARTrackableSplineMover>();
        if (splinePathMovers == null) splinePathMovers = new List<SplinePathMover>();

        splineMovers.RemoveAll(m => m == null);
        splinePathMovers.RemoveAll(m => m == null);

        ARTrackableSplineMover[] foundTrackable = GetComponentsInChildren<ARTrackableSplineMover>(true);
        for (int i = 0; i < foundTrackable.Length; i++)
        {
            if (foundTrackable[i] != null && !splineMovers.Contains(foundTrackable[i]))
                splineMovers.Add(foundTrackable[i]);
        }

        SplinePathMover[] foundPath = GetComponentsInChildren<SplinePathMover>(true);
        for (int i = 0; i < foundPath.Length; i++)
        {
            if (foundPath[i] != null && !splinePathMovers.Contains(foundPath[i]))
                splinePathMovers.Add(foundPath[i]);
        }
    }

    private void OnDestroy()
    {
        ARVFXPopupController.OnRevealComplete -= HandlePopupRevealComplete;
        DisposeVideoRuntimeCache();
    }

    private void OnEnable()
    {
        if (mediaManager != null) mediaManager.RegisterNode(this);
    }

    private void OnDisable()
    {
        if (mediaManager != null) mediaManager.UnregisterNode(this);

        ARVFXPopupController.OnRevealComplete -= HandlePopupRevealComplete;
        _waitingForPopupReveal = false;
        _storyBlockedByActivity = false;
        _mediaAudioPausedForActivity = false;
        _onStorySystemsStarted = null;

        Stop2DCoroutines();
        Unsubscribe2DAudioEndEvent();
    }

    // ---------------------------------------------------------------
    // REVEAL SETUP
    // ---------------------------------------------------------------

    private void SetupReveal()
    {
        if (blackOverlay == null)
        {
            foreach (var r in GetComponentsInChildren<Renderer>(true))
            {
                if (r.gameObject.name == "Black Screen" ||
                    r.gameObject.name == "BlackOverlay" ||
                    r.gameObject.name == "black page")
                { blackOverlay = r; break; }
            }
        }

        if (pageTurnSound != null)
        {
            _revealAudioSource = gameObject.AddComponent<AudioSource>();
            _revealAudioSource.clip = pageTurnSound;
            _revealAudioSource.playOnAwake = false;
            _revealAudioSource.loop = false;
        }

        if (blackOverlay != null)
        {
            blackOverlay.material.renderQueue = 3500;
            Color c = blackOverlay.material.GetColor("_BaseColor");
            c.a = 0f;
            blackOverlay.material.SetColor("_BaseColor", c);
            blackOverlay.gameObject.SetActive(true);
        }
    }

    private GameObject GetContentRoot()
    {
        if (contentRoot != null) return contentRoot;

        foreach (Transform child in transform)
        {
            if (child.name == "All_GameObjects" || child.name == "All_GameObject")
            {
                contentRoot = child.gameObject;
                return contentRoot;
            }
        }

        foreach (Transform child in transform)
        {
            var deep = child.Find("All_GameObjects") ?? child.Find("All_GameObject");
            if (deep != null) { contentRoot = deep.gameObject; return contentRoot; }
        }

        return null;
    }

    private void HideAllChildren(bool hide)
    {
        foreach (Transform child in transform)
        {
            bool keep = false;
            foreach (var n in _keepVisible)
                if (child.name.Contains(n)) { keep = true; break; }
            if (!keep) child.gameObject.SetActive(!hide);
        }
    }

    private void StartWatchingVideo()
    {
        if (!gameObject.activeInHierarchy) return;
        if (blackOverlay == null && OverlayManager.Instance == null) return;

        if (_revealCoroutine != null) { StopCoroutine(_revealCoroutine); _revealCoroutine = null; }

        bool hasVideo = mainVideos != null && mainVideos.Count > 0 && mainVideos[0] != null;

        if (hasVideo)
        {
            List<VideoPlayer> watchList = GetPageEndWatchVideos();
            if (watchList.Count > 0)
                _revealCoroutine = StartCoroutine(WatchVideosThenFade(watchList));
            else
                _revealCoroutine = StartCoroutine(WatchVideoThenFade(mainVideos[0]));
        }
    }

    private List<VideoPlayer> GetPageEndWatchVideos()
    {
        var result = new List<VideoPlayer>();
        if (mainVideos == null || mainVideoSettings == null) return result;
        for (int i = 0; i < mainVideos.Count; i++)
        {
            if (mainVideos[i] == null) continue;
            if (i < mainVideoSettings.Count &&
                mainVideoSettings[i] != null &&
                mainVideoSettings[i].waitForPageEnd)
                result.Add(mainVideos[i]);
        }
        return result;
    }

    private IEnumerator WatchVideosThenFade(List<VideoPlayer> videos)
    {
        int n = videos.Count;
        bool[] started = new bool[n];
        bool[] ended = new bool[n];

        float overallTimeout = Time.time + 600f;
        while (Time.time < overallTimeout)
        {
            if (!_isTracked) { yield return new WaitUntil(() => _isTracked); }

            bool allEnded = true;
            for (int i = 0; i < n; i++)
            {
                var vp = videos[i];
                if (vp == null) { ended[i] = true; continue; }
                if (ended[i]) continue;

                if (!started[i])
                {
                    if (vp.isPlaying && vp.time > 0.1) started[i] = true;
                    allEnded = false;
                }
                else
                {
                    if (!vp.isPlaying) ended[i] = true;
                    else allEnded = false;
                }
            }
            if (allEnded) break;
            yield return null;
        }

        Debug.Log("[AR] Reveal: all 'Wait For Page End' videos ended. Starting fade.");
        _revealCoroutine = null;
        _revealCoroutine = StartCoroutine(FadeAndReveal());
    }

    private IEnumerator WatchVideoThenFade(VideoPlayer vp)
    {
        float startTimeout = Time.time + 10f;
        while (!vp.isPlaying && Time.time < startTimeout)
            yield return null;

        if (!vp.isPlaying)
        {
            Debug.LogWarning("[AR] Reveal: video never started.");
            yield break;
        }

        float phaseTimeout = Time.time + 15f;
        while (Time.time < phaseTimeout)
        {
            if (!_isTracked) { yield return new WaitUntil(() => _isTracked); }
            if (vp.isPlaying && vp.time > 0.1) break;
            yield return null;
        }

        Debug.Log($"[AR] Reveal: past freeze. time={vp.time:F2}");

        while (true)
        {
            if (!_isTracked) { yield return new WaitUntil(() => _isTracked); }
            if (!vp.isPlaying)
            {
                Debug.Log("[AR] Reveal: video ended. Starting fade.");
                break;
            }
            yield return null;
        }

        _revealCoroutine = null;
        _revealCoroutine = StartCoroutine(FadeAndReveal());
    }

    public void TriggerPageEnd()
    {
        if (OverlayManager.Instance != null)
            OverlayManager.Instance.ShowPageEnd();
    }

    public void StartPageEndFade()
    {
        if (!gameObject.activeInHierarchy) return;
        if (_revealCoroutine != null) { StopCoroutine(_revealCoroutine); _revealCoroutine = null; }
        _revealCoroutine = StartCoroutine(FadeAndReveal());
    }

    private IEnumerator FadeAndReveal()
    {
        if (delayBeforeFade > 0f)
            yield return new WaitForSeconds(delayBeforeFade);

        if (blackOverlay != null)
        {
            blackOverlay.gameObject.SetActive(true);
            Color c = blackOverlay.material.GetColor("_BaseColor");
            c.a = 0f;
            blackOverlay.material.SetColor("_BaseColor", c);

            float elapsed = 0f;
            while (elapsed < fadeDuration)
            {
                elapsed += Time.deltaTime;
                c.a = Mathf.Clamp01(elapsed / fadeDuration);
                blackOverlay.material.SetColor("_BaseColor", c);
                yield return null;
            }
            c.a = 1f;
            blackOverlay.material.SetColor("_BaseColor", c);
        }

        var root = GetContentRoot();
        if (root != null)
            root.SetActive(false);
        else
            HideAllChildren(true);

        if (blackOverlay != null)
            blackOverlay.gameObject.SetActive(false);

        if (postFadeDelay > 0f)
            yield return new WaitForSeconds(postFadeDelay);

        if (OverlayManager.Instance != null)
            OverlayManager.Instance.ShowPageEnd();

        if (_revealAudioSource != null && pageTurnSound != null)
            _revealAudioSource.Play();

        _revealCoroutine = null;
    }

    private void ResetReveal()
    {
        if (_revealCoroutine != null) { StopCoroutine(_revealCoroutine); _revealCoroutine = null; }

        var root = GetContentRoot();
        if (root != null)
            root.SetActive(true);
        else
            HideAllChildren(false);

        if (blackOverlay != null)
        {
            blackOverlay.gameObject.SetActive(true);
            Color c = blackOverlay.material.GetColor("_BaseColor");
            c.a = 0f;
            blackOverlay.material.SetColor("_BaseColor", c);
        }

        if (OverlayManager.Instance != null)
        {
            OverlayManager.Instance.StopWatching();
            OverlayManager.Instance.HideAll();
        }
    }

    // ---------------------------------------------------------------

    private void RebuildVideoRuntimeCache()
    {
        DisposeVideoRuntimeCache();
        if (!Is2DStoryMode())
        {
            AddToRuntime(mainVideos);
            AddToRuntime(backgroundLoopVideos);
        }
    }

    private void DisposeVideoRuntimeCache()
    {
        foreach (var kv in _videoRuntime)
            kv.Value.Dispose();

        _videoRuntime.Clear();
    }

    private void AddToRuntime(List<VideoPlayer> list)
    {
        if (list == null) return;
        for (int i = 0; i < list.Count; i++)
        {
            var vp = list[i];
            if (vp == null) continue;
            if (_videoRuntime.ContainsKey(vp)) continue;
            _videoRuntime.Add(vp, new VideoFreezeRuntime(this, vp));
        }
    }

    public void NotifyFound()
    {
        _isTracked = true;
        Debug.Log($"[AR] NotifyFound — pageId: '{pageId}'");
        if (mediaManager != null)
            mediaManager.NotifyTrackingFound(this);
        else
            StartFromBeginning();
    }

    public void NotifyLost()
    {
        _isTracked = false;
        _lastLostTime = Time.time;
        if (mediaManager != null)
            mediaManager.NotifyTrackingLost(this);
        else
            PauseVisuals();
    }

    public bool CanResume(float graceSeconds)
    {
        if (_lastLostTime < 0f) return false;
        return (Time.time - _lastLostTime) <= graceSeconds;
    }

    public void OnBecameInactiveByManager()
    {
        PauseVisuals();
    }

    public void StartFromBeginning(System.Action onStorySystemsStarted = null)
    {
        ARVFXPopupController.OnRevealComplete -= HandlePopupRevealComplete;

        _waitingForPopupReveal = false;
        _storyBlockedByActivity = false;
        _onStorySystemsStarted = onStorySystemsStarted;

        if (Is2DStoryMode())
        {
            Stop2DCoroutines();
            _2dPaused = false;
            _currentPartIndex2D = -1;
            Reset2DStoryPartsVideos();
        }

        ResetReveal();

        RebuildVideoRuntimeCache();

        ResetStorySystemsToStartPaused();

        ARVFXPopupController popup = GetComponentInChildren<ARVFXPopupController>(true);

        if (popup != null && popup.isActiveAndEnabled)
        {
            _waitingForPopupReveal = true;
            ARVFXPopupController.OnRevealComplete += HandlePopupRevealComplete;

            popup.TriggerReplay();
            return;
        }

        NotifyContentControllerRevealComplete();
        BeginStorySystemsNow();
    }

    private void NotifyContentControllerRevealComplete()
    {
        ContentController[] controllers = GetComponentsInChildren<ContentController>(true);
        for (int i = 0; i < controllers.Length; i++)
        {
            if (controllers[i] != null)
                controllers[i].NotifyRevealComplete();
        }
    }

    public bool HasBlockingAfterRevealActivity()
    {
        ContentController[] controllers = GetComponentsInChildren<ContentController>(true);
        for (int i = 0; i < controllers.Length; i++)
        {
            if (controllers[i] != null && controllers[i].ShouldRunBeforeStoryAfterReveal())
                return true;
        }
        return false;
    }

    public void TriggerStoryPointActivity()
    {
        TriggerStoryPointActivity(string.Empty);
    }

    public void TriggerStoryPointActivity(string key)
    {
        ContentController[] controllers = GetComponentsInChildren<ContentController>(true);
        if (controllers == null || controllers.Length == 0) return;

        string triggerKey = string.IsNullOrWhiteSpace(key) ? "StoryPoint" : key;
        for (int i = 0; i < controllers.Length; i++)
        {
            if (controllers[i] != null)
                controllers[i].TriggerActivity(triggerKey);
        }
    }

    public void TriggerStoryPointActivity(int key)
    {
        TriggerStoryPointActivity(key.ToString());
    }

    public void TriggerStoryPointActivity(float key)
    {
        TriggerStoryPointActivity(key.ToString());
    }

    private void HandlePopupRevealComplete(ARVFXPopupController controller)
    {
        if (!_waitingForPopupReveal) return;
        if (controller == null) return;

        bool belongsToThisPage =
            controller.transform == transform ||
            controller.transform.IsChildOf(transform);

        if (!belongsToThisPage) return;

        if (HasBlockingAfterRevealActivity())
        {
            ARVFXPopupController.OnRevealComplete -= HandlePopupRevealComplete;
            _waitingForPopupReveal = false;
            _storyBlockedByActivity = true;

            PauseStorySystemsForActivityGate();

            RunBeforeStoryActivitiesSequentially(() =>
            {
                _storyBlockedByActivity = false;
                BeginStorySystemsNow();
            });
            return;
        }

        NotifyContentControllerRevealComplete();
        BeginStorySystemsNow();
    }

    private void RunBeforeStoryActivitiesSequentially(System.Action onComplete)
    {
        ContentController[] controllers = GetComponentsInChildren<ContentController>(true);
        List<ContentController> blocking = new List<ContentController>();
        for (int i = 0; i < controllers.Length; i++)
        {
            if (controllers[i] != null && controllers[i].ShouldRunBeforeStoryAfterReveal())
                blocking.Add(controllers[i]);
        }

        if (blocking.Count == 0)
        {
            onComplete?.Invoke();
            return;
        }

        int index = 0;
        System.Action runNext = null;
        runNext = () =>
        {
            if (!gameObject.activeInHierarchy || !_isTracked)
                return;

            if (index >= blocking.Count)
            {
                onComplete?.Invoke();
                return;
            }

            ContentController controller = blocking[index++];
            if (controller == null)
            {
                runNext?.Invoke();
                return;
            }

            controller.RunBeforeStoryAfterReveal(runNext);
        };

        runNext.Invoke();
    }

    private void PauseStorySystemsForActivityGate()
    {
        PauseVideos(mainVideos);
        PauseVideos(backgroundLoopVideos);
        ResetAnimatorsToFrameZeroPaused();
        StopSplinesForIntro();
    }

    private void PauseStorySystemsAtCurrentFrameForActivity()
    {
        PauseVideos(mainVideos);
        PauseVideos(backgroundLoopVideos);

        for (int i = 0; i < animators.Count; i++)
        {
            Animator a = animators[i];
            if (a == null) continue;
            a.speed = 0f;
        }

        for (int i = 0; i < splineMovers.Count; i++)
        {
            ARTrackableSplineMover m = splineMovers[i];
            if (m == null) continue;
            m.Pause();
        }

        for (int i = 0; i < splinePathMovers.Count; i++)
        {
            SplinePathMover m = splinePathMovers[i];
            if (m == null) continue;
            m.Pause();
        }

        ARVFXPopupController popup = GetComponentInChildren<ARVFXPopupController>(true);
        if (popup != null)
            popup.PauseReveal();
    }

    private void ResetStorySystemsToStartPaused()
    {
        RebuildSplineMoverLists();
        ResetVideosToStartPaused(mainVideos);
        ResetVideosToStartPaused(backgroundLoopVideos);
        ResetAnimatorsToFrameZeroPaused();
        StopSplinesForIntro();
    }

    private void ResetVideosToStartPaused(List<VideoPlayer> list)
    {
        if (list == null) return;

        for (int i = 0; i < list.Count; i++)
        {
            VideoPlayer vp = list[i];
            if (vp == null) continue;
            if (!vp.gameObject.activeInHierarchy) continue;
            if (!vp.enabled) continue;

            vp.Stop();
            vp.time = 0;
        }
    }

    private void ResetAnimatorsToFrameZeroPaused()
    {
        for (int i = 0; i < animators.Count; i++)
        {
            Animator a = animators[i];
            if (a == null) continue;

            a.enabled = true;
            a.speed = 0f;

            if (a.gameObject.activeInHierarchy)
            {
                a.Rebind();
                a.Update(0f);
            }
        }
    }

    private void StopSplinesForIntro()
    {
        for (int i = 0; i < splineMovers.Count; i++)
        {
            ARTrackableSplineMover m = splineMovers[i];
            if (m == null) continue;
            m.Stop();
        }

        for (int i = 0; i < splinePathMovers.Count; i++)
        {
            SplinePathMover m = splinePathMovers[i];
            if (m == null) continue;
            m.Stop();
        }
    }

    private void BeginStorySystemsNow()
    {
        if (!gameObject.activeInHierarchy) return;

        if (!_isTracked)
        {
            _onStorySystemsStarted = null;
            return;
        }

        ARVFXPopupController.OnRevealComplete -= HandlePopupRevealComplete;
        _waitingForPopupReveal = false;
        _storyBlockedByActivity = false;

        RebuildVideoRuntimeCache();

        if (Is2DStoryMode())
        {
            // Start the visuals FIRST, then signal that audio can begin -- previously this
            // was reversed, so the audio-start callback (which plays the voice/BGM) fired
            // before Begin2DStoryParts() had even been called, making audio noticeably
            // precede the visual on real devices (where downloads/rendering take real time,
            // unlike the Editor where everything is already local and instant).
            Begin2DStoryParts();

            System.Action callback2d = _onStorySystemsStarted;
            _onStorySystemsStarted = null;
            callback2d?.Invoke();
            return;
        }

        RestartMainVideosWithPerClipSettings();
        RestartBackgroundVideosWithPerClipSettings();

        ResetAnimatorsToFrameZeroPaused();
        StartAnimatorsAfterIntro();
        StartSplinesAfterIntro();
        BeginModelSlots3D();

        StartWatchingVideo();

        System.Action callback = _onStorySystemsStarted;
        _onStorySystemsStarted = null;
        callback?.Invoke();
    }

    private void StartAnimatorsAfterIntro()
    {
        for (int i = 0; i < animators.Count; i++)
        {
            Animator a = animators[i];
            if (a == null) continue;
            a.enabled = true;
            a.speed = 1f;
        }
    }

    private void EnsureSplineMoverCanStart(Component mover)
    {
        if (mover == null) return;
        if (!mover.gameObject.activeSelf)
            mover.gameObject.SetActive(true);

        MonoBehaviour behaviour = mover as MonoBehaviour;
        if (behaviour != null && !behaviour.enabled)
            behaviour.enabled = true;
    }

    private void StartSplinesAfterIntro()
    {
        RebuildSplineMoverLists();
        Debug.Log($"[AR] Starting spline movers for page '{pageId}'. Trackable:{splineMovers.Count} Path:{splinePathMovers.Count}", this);

        for (int i = 0; i < splineMovers.Count; i++)
        {
            ARTrackableSplineMover m = splineMovers[i];
            if (m == null) continue;

            EnsureSplineMoverCanStart(m);

            if (!m.gameObject.activeInHierarchy)
            {
                Debug.LogWarning($"[AR] Spline mover '{m.name}' is inactive. It cannot start until its GameObject is active.", m);
                continue;
            }

            try
            {
                string reason;
                if (m.ForceRestartFromBeginning(out reason))
                    Debug.Log($"[AR] Started ARTrackableSplineMover '{m.name}' for page '{pageId}'.", m);
                else
                    Debug.LogWarning($"[AR] ARTrackableSplineMover '{m.name}' did not start: {reason}", m);
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[AR] Failed to start ARTrackableSplineMover '{m.name}': {ex.Message}", m);
            }
        }

        for (int i = 0; i < splinePathMovers.Count; i++)
        {
            SplinePathMover m = splinePathMovers[i];
            if (m == null) continue;

            EnsureSplineMoverCanStart(m);

            if (!m.gameObject.activeInHierarchy)
            {
                Debug.LogWarning($"[AR] Spline path mover '{m.name}' is inactive. It cannot start until its GameObject is active.", m);
                continue;
            }

            try
            {
                string reason;
                if (m.ForceRestartFromBeginning(out reason))
                    Debug.Log($"[AR] Started SplinePathMover '{m.name}' for page '{pageId}'.", m);
                else
                    Debug.LogWarning($"[AR] SplinePathMover '{m.name}' did not start: {reason}", m);
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[AR] Failed to start SplinePathMover '{m.name}': {ex.Message}", m);
            }
        }
    }

    // ── Model Slots (3D) ─────────────────────────────────────────────────────
    // Optional, additive: shows one 3D model at a time, in order, each with its
    // own position, show duration, and gap before the next one appears. Each
    // model's own Animator (whichever clip/state it's already set up with) is
    // restarted from the beginning the instant its model turns on — not before.
    // Does nothing if the list is empty, so existing 3D pages that don't use
    // this are unaffected.

    private void BeginModelSlots3D()
    {
        if (modelSlots == null || modelSlots.Count == 0) return;

        if (_modelSlotsRoutine != null) { StopCoroutine(_modelSlotsRoutine); _modelSlotsRoutine = null; }
        _3dSlotsPaused = false;
        _currentModelSlotIndex = -1;

        foreach (var slot in modelSlots)
        {
            if (slot?.model == null) continue;
            slot.model.gameObject.SetActive(false);
            slot.model.localScale = slot.originalScale; // undo any leftover scale from a previous run
        }

        _modelSlotsRoutine = StartCoroutine(RunModelSlots3D());
    }

    private IEnumerator RunModelSlots3D()
    {
        for (int i = 0; i < modelSlots.Count; i++)
        {
            _currentModelSlotIndex = i;
            ModelSlot3D slot = modelSlots[i];
            if (slot?.model == null) continue;

            slot.model.localPosition = slot.position;
            if (slot.scaleInOut) slot.model.localScale = Vector3.zero;
            slot.model.gameObject.SetActive(true);

            // Model turns on right here — start its own animation fresh from the
            // beginning at this exact moment, not whatever state it was left in.
            Animator modelAnimator = slot.model.GetComponentInChildren<Animator>(true);
            if (modelAnimator != null)
            {
                modelAnimator.Rebind();
                modelAnimator.Update(0f);
            }

            if (slot.sfxClip != null)
                AudioSource.PlayClipAtPoint(slot.sfxClip, slot.model.position, slot.sfxVolume);

            if (slot.scaleInOut)
                yield return ScaleModelRoutine(slot.model, Vector3.zero, slot.originalScale, slot.scaleDuration);
            else
                slot.model.localScale = slot.originalScale;

            float duration = slot.matchAnimationLength && modelAnimator != null && modelAnimator.GetCurrentAnimatorStateInfo(0).length > 0f
                ? modelAnimator.GetCurrentAnimatorStateInfo(0).length
                : slot.showDuration;

            yield return WaitPausable3D(duration);

            if (slot.scaleInOut)
                yield return ScaleModelRoutine(slot.model, slot.originalScale, Vector3.zero, slot.scaleDuration);

            slot.model.gameObject.SetActive(false);
            slot.model.localScale = slot.originalScale; // reset for next time, in case scaling was used

            if (slot.gapBeforeNext > 0f)
                yield return WaitPausable3D(slot.gapBeforeNext);
        }

        _modelSlotsRoutine = null;
        _currentModelSlotIndex = -1;
    }

    // Universal, safe "reveal" effect: scales the model from one size to another.
    // Works on any model regardless of shader/material setup (unlike an opacity
    // fade, which only works if the model's material actually supports
    // transparency) — this is why it changes size instead of see-through fading.
    private IEnumerator ScaleModelRoutine(Transform model, Vector3 fromScale, Vector3 toScale, float duration)
    {
        float t = 0f;
        duration = Mathf.Max(0.01f, duration);
        while (t < duration)
        {
            if (!_3dSlotsPaused) t += Time.deltaTime;
            model.localScale = Vector3.Lerp(fromScale, toScale, t / duration);
            yield return null;
        }
        model.localScale = toScale;
    }

    private IEnumerator WaitPausable3D(float seconds)
    {
        float remaining = seconds;
        while (remaining > 0f)
        {
            if (!_3dSlotsPaused) remaining -= Time.deltaTime;
            yield return null;
        }
    }

    private void PauseModelSlots3D()
    {
        _3dSlotsPaused = true;
    }

    private void ResumeModelSlots3D()
    {
        _3dSlotsPaused = false;

        // Re-assert the currently active slot's model is actually showing —
        // same safety idea as the 2D Video Slots resume fix: if tracking
        // flickered off at an awkward moment, this guarantees the right
        // model is visible again rather than leaving it stuck off.
        if (_currentModelSlotIndex < 0 || modelSlots == null || _currentModelSlotIndex >= modelSlots.Count) return;
        ModelSlot3D slot = modelSlots[_currentModelSlotIndex];
        if (slot?.model == null) return;
        if (!slot.model.gameObject.activeSelf) slot.model.gameObject.SetActive(true);
    }

    public void PauseStoryForActivity()
    {
        _storyBlockedByActivity = true;
        PauseStorySystemsAtCurrentFrameForActivity();
        PauseMediaAudioForActivity();
    }

    public void ResumeStoryFromActivity()
    {
        _storyBlockedByActivity = false;
        ResumeVisuals();
        ResumeMediaAudioFromActivity();
    }

    private void PauseMediaAudioForActivity()
    {
        if (mediaManager == null || _mediaAudioPausedForActivity) return;
        InvokeMediaManagerPrivateMethod("PauseAll");
        _mediaAudioPausedForActivity = true;
    }

    private void ResumeMediaAudioFromActivity()
    {
        if (mediaManager == null || !_mediaAudioPausedForActivity) return;
        InvokeMediaManagerPrivateMethod("ResumeAll");
        _mediaAudioPausedForActivity = false;
    }

    private void InvokeMediaManagerPrivateMethod(string methodName)
    {
        if (mediaManager == null || string.IsNullOrWhiteSpace(methodName)) return;

        MethodInfo method = mediaManager.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (method == null) return;

        try
        {
            method.Invoke(mediaManager, null);
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"[AR] Could not call ARMediaManager.{methodName}: {ex.Message}");
        }
    }

    public void PauseVisuals()
    {
        if (Is2DStoryMode())
        {
            _2dPaused = true;
            Pause2DCurrentPartVideos();
        }

        PauseVideos(mainVideos);
        PauseVideos(backgroundLoopVideos);

        for (int i = 0; i < animators.Count; i++)
        {
            var a = animators[i];
            if (a == null) continue;
            a.speed = 0f;
        }

        for (int i = 0; i < splineMovers.Count; i++)
        {
            var m = splineMovers[i];
            if (m == null) continue;
            m.Pause();
        }

        for (int i = 0; i < splinePathMovers.Count; i++)
        {
            var m = splinePathMovers[i];
            if (m == null) continue;
            m.Pause();
        }

        PauseModelSlots3D();

        ARVFXPopupController popup = GetComponentInChildren<ARVFXPopupController>(true);
        if (popup != null)
            popup.PauseReveal();
    }

    public void ResumeVisuals()
    {
        if (blackOverlay != null)
        {
            blackOverlay.gameObject.SetActive(true);
            Color c = blackOverlay.material.GetColor("_BaseColor");
            c.a = 0f;
            blackOverlay.material.SetColor("_BaseColor", c);
        }

        var root = GetContentRoot();
        if (root != null && !root.activeSelf)
            root.SetActive(true);

        ARVFXPopupController popup = GetComponentInChildren<ARVFXPopupController>(true);

        if (_waitingForPopupReveal && popup != null && !popup.IsRevealComplete)
        {
            popup.ResumeReveal();
            return;
        }

        if (_storyBlockedByActivity)
        {
            PauseStorySystemsAtCurrentFrameForActivity();
            ContentController controller = GetComponentInChildren<ContentController>(true);
            if (controller != null)
                controller.RestoreActivityUIAfterTrackingFound();
            return;
        }

        if (Is2DStoryMode())
        {
            _2dPaused = false;
            Resume2DCurrentPartVideos();
            ReapplyCurrentPart2D();
        }

        ResumeModelSlots3D();

        ResumeVideos(mainVideos);
        ResumeVideos(backgroundLoopVideos);

        for (int i = 0; i < animators.Count; i++)
        {
            var a = animators[i];
            if (a == null) continue;
            a.speed = 1f;
        }

        for (int i = 0; i < splineMovers.Count; i++)
        {
            var m = splineMovers[i];
            if (m == null) continue;
            m.Resume();
        }

        for (int i = 0; i < splinePathMovers.Count; i++)
        {
            var m = splinePathMovers[i];
            if (m == null) continue;
            m.Resume();
        }

        if (popup != null)
            popup.ResumeReveal();
    }

    private void RestartMainVideosWithPerClipSettings()
    {
        if (mainVideos == null) return;
        for (int i = 0; i < mainVideos.Count; i++)
        {
            var vp = mainVideos[i];
            if (vp == null) continue;
            if (!vp.gameObject.activeInHierarchy) continue;

            MainVideoSettings s =
                (mainVideoSettings != null && i < mainVideoSettings.Count) ? mainVideoSettings[i] : null;

            var mode  = s != null ? s.freezeMode         : freezeMode;
            var first = s != null ? s.freezeFirstSeconds : freezeFirstSeconds;
            var last  = s != null ? s.freezeLastSeconds  : freezeLastSeconds;
            var speed = s != null ? s.playbackSpeed      : 1f;
            var delay = s != null ? s.startDelay         : 0f;

            if (_videoRuntime.TryGetValue(vp, out var rt))
                rt.RestartWithFreeze(mode, first, last, speed, delay);
            else { vp.time = 0; vp.playbackSpeed = Mathf.Max(0.01f, speed); vp.Play(); }
        }
    }

    private void RestartBackgroundVideosWithPerClipSettings()
    {
        if (backgroundLoopVideos == null) return;
        for (int i = 0; i < backgroundLoopVideos.Count; i++)
        {
            var vp = backgroundLoopVideos[i];
            if (vp == null) continue;
            if (!vp.gameObject.activeInHierarchy) continue;

            BackgroundVideoSettings s =
                (backgroundVideoSettings != null && i < backgroundVideoSettings.Count) ? backgroundVideoSettings[i] : null;

            var speed = s != null ? s.playbackSpeed : 1f;
            var delay = s != null ? s.startDelay    : 0f;

            if (_videoRuntime.TryGetValue(vp, out var rt))
                rt.RestartWithFreeze(VuforiaVideoFrameFreezeController.FreezeMode.None, 0f, 0f, speed, delay);
            else { vp.time = 0; vp.playbackSpeed = Mathf.Max(0.01f, speed); vp.Play(); }
        }
    }

    private void PauseVideos(List<VideoPlayer> list)
    {
        if (list == null) return;
        for (int i = 0; i < list.Count; i++)
        {
            var vp = list[i];
            if (vp == null) continue;
            if (!vp.gameObject.activeInHierarchy) continue;
            if (!vp.enabled) continue;
            if (_videoRuntime.TryGetValue(vp, out var rt)) rt.Pause();
            else if (vp.isPlaying) vp.Pause();
        }
    }

    private void ResumeVideos(List<VideoPlayer> list)
    {
        if (list == null) return;
        for (int i = 0; i < list.Count; i++)
        {
            var vp = list[i];
            if (vp == null) continue;
            if (!vp.gameObject.activeInHierarchy) continue;
            if (_videoRuntime.TryGetValue(vp, out var rt)) rt.Resume();
            else vp.Play();
        }
    }

    // ── 2D Story Parts ──────────────────────────────────────────────────────────

    private bool Is2DStoryMode()
    {
        return pageType == PageType.TwoD && storyParts != null && storyParts.Count > 0;
    }

    private void Begin2DStoryParts()
    {
        Stop2DCoroutines();
        _2dPaused = false;
        _currentPartIndex2D = -1;
        _storyPartsRoutine = StartCoroutine(Run2DStoryParts());
    }

    private void Stop2DCoroutines()
    {
        if (_storyPartsRoutine != null) { StopCoroutine(_storyPartsRoutine); _storyPartsRoutine = null; }
        if (_timedChangesRoutine != null) { StopCoroutine(_timedChangesRoutine); _timedChangesRoutine = null; }

        foreach (var c in _layerRoutines2D) if (c != null) StopCoroutine(c);
        _layerRoutines2D.Clear();

        StopAllSmallMotions2D();
        DisposeActive2DVideoRuntime();
        _active2DVideos.Clear();
        Unsubscribe2DAudioEndEvent();
    }

    private void Reset2DStoryPartsVideos()
    {
        if (storyParts == null) return;
        foreach (var part in storyParts)
        {
            if (part == null) continue;
            if (part.mainVideos != null)
                foreach (var entry in part.mainVideos)
                {
                    if (entry?.video == null) continue;
                    entry.video.Stop();
                    entry.video.time = 0;
                    if (entry.hideAtPartEnd) entry.video.gameObject.SetActive(false);
                }
            if (part.backgroundVideos != null)
                foreach (var entry in part.backgroundVideos)
                {
                    if (entry?.video == null) continue;
                    entry.video.Stop();
                    if (entry.hideAtPartEnd) entry.video.gameObject.SetActive(false);
                }
            HidePartLayersInstant2D(part);
        }
    }

    private void Pause2DCurrentPartVideos()
    {
        for (int i = 0; i < _active2DVideos.Count; i++)
        {
            VideoPlayer vp = _active2DVideos[i];
            if (vp == null || !vp.gameObject.activeInHierarchy || !vp.enabled) continue;
            if (_active2DVideoRuntime.TryGetValue(vp, out var rt)) rt.Pause();
            else if (vp.isPlaying) vp.Pause();
        }
    }

    private void Resume2DCurrentPartVideos()
    {
        for (int i = 0; i < _active2DVideos.Count; i++)
        {
            VideoPlayer vp = _active2DVideos[i];
            if (vp == null || !vp.gameObject.activeInHierarchy || !vp.enabled) continue;
            if (_active2DVideoRuntime.TryGetValue(vp, out var rt)) rt.Resume();
            else if (!vp.isPlaying) vp.Play();
        }
    }

    // Re-asserts the CURRENTLY ACTIVE slot's full visual state — background image,
    // layers, main video, background video — every time tracking is found again.
    //
    // Why this exists: if tracking flickers off for a moment while a slot is mid-way
    // through starting, a step can get silently skipped (e.g. showing the background,
    // or starting the video) because the page's own GameObject was briefly inactive
    // at that exact instant. Nothing else would ever notice or correct that on its
    // own. This function treats "show the current slot correctly" as one single,
    // repeatable action — safe to run again even if everything is already correct —
    // so tracking coming back always leaves the slot in the right state, regardless
    // of what happened during the interruption.
    //
    // Only uses direct, synchronous calls (no StartCoroutine) so it can never fail
    // the same way the interrupted step did.
    private void ReapplyCurrentPart2D()
    {
        if (_currentPartIndex2D < 0 || storyParts == null || _currentPartIndex2D >= storyParts.Count) return;

        StoryPart2D part = storyParts[_currentPartIndex2D];
        if (part == null) return;

        if (part.backgroundImage != null)
        {
            part.backgroundImage.gameObject.SetActive(true);
            ApplyAlpha2D(part.backgroundImage, 1f);
        }

        if (part.visualLayers != null)
            foreach (var vl in part.visualLayers)
            {
                if (vl?.layer == null) continue;
                if (!vl.showAtPartStart) continue;
                vl.layer.gameObject.SetActive(true);
                ApplyAlpha2D(vl.layer, 1f);
            }

        if (part.mainVideos != null)
            foreach (var entry in part.mainVideos)
            {
                if (entry?.video == null) continue;
                if (!entry.video.gameObject.activeSelf) entry.video.gameObject.SetActive(true);
                if (entry.video.enabled && !entry.video.isPlaying) entry.video.Play();
            }

        if (part.backgroundVideos != null)
            foreach (var entry in part.backgroundVideos)
            {
                if (entry?.video == null) continue;
                if (!entry.video.gameObject.activeSelf) entry.video.gameObject.SetActive(true);
                if (entry.video.enabled && !entry.video.isPlaying) entry.video.Play();
            }
    }

    // ── Core sequence ───────────────────────────────────────────────────────────

    private IEnumerator Run2DStoryParts()
    {
        if (persistentLayers2D != null)
        {
            foreach (var pl in persistentLayers2D)
            {
                if (pl?.layer == null) continue;
                if (!pl.showOnPageStart) continue;
                pl.layer.gameObject.SetActive(true);
                if (pl.fadeIn && pl.fadeInDuration > 0f)
                    _layerRoutines2D.Add(StartCoroutine(FadeLayer2D(pl.layer, 0f, 1f, pl.fadeInDuration)));
                else
                    ApplyAlpha2D(pl.layer, 1f);
                if (pl.enableSmallMotion && pl.motionTarget != null)
                    StartSmallMotion2D(pl.motionTarget, pl.motionType, pl.motionSpeed, pl.motionAmplitude);
            }
        }

        if (commonBackgroundImage != null)
        {
            commonBackgroundImage.gameObject.SetActive(true);
            ApplyAlpha2D(commonBackgroundImage, 1f);
        }

        foreach (var part in storyParts)
        {
            if (part == null) continue;
            HidePartLayersInstant2D(part);
        }

        for (int p = 0; p < storyParts.Count; p++)
        {
            _currentPartIndex2D = p;
            var part = storyParts[p];
            if (part == null) continue;

            StopAllOtherPartVideos2D(p);

            ShowPartBackgroundImage2D(part);
            ShowPartLayers2D(part);
            StartPartBackgroundVideos2D(part);
            StartPartMainVideos2D(part);

            if (part.timedChanges != null && part.timedChanges.Count > 0)
                _timedChangesRoutine = StartCoroutine(RunTimedChanges2D(part));

            yield return WaitForPartEnd2D(part);

            if (_timedChangesRoutine != null) { StopCoroutine(_timedChangesRoutine); _timedChangesRoutine = null; }

            HidePartLayers2D(part);
            HidePartBackgroundImage2D(part);
            StopPartMainVideos2D(part);
            StopPartBackgroundVideos2D(part);
        }

        if (persistentLayers2D != null)
        {
            foreach (var pl in persistentLayers2D)
            {
                if (pl?.layer == null || !pl.fadeOutAtPageEnd) continue;
                yield return FadeLayer2D(pl.layer, 1f, 0f, pl.fadeOutDuration > 0f ? pl.fadeOutDuration : 0.3f);
            }
        }

        StopAllSmallMotions2D();
        _storyPartsRoutine = null;
        _currentPartIndex2D = -1;

        StartPageEndFade();
    }

    // ── Part timing ─────────────────────────────────────────────────────────────

    private IEnumerator WaitForPartEnd2D(StoryPart2D part)
    {
        switch (part.timing)
        {
            case PartTiming2D.AutoFromMainVideo:
                yield return WaitForPartVideosEnd2D(part);
                break;

            case PartTiming2D.ManualDuration:
                yield return WaitPausable2D(Mathf.Max(0.1f, part.manualDuration));
                break;

            case PartTiming2D.WaitForPageAudioEnd:
                yield return WaitForPageAudioEnd2D();
                break;

            case PartTiming2D.WaitForTap:
                yield return new WaitUntil(() =>
                    !_2dPaused && (Input.touchCount > 0 || Input.GetMouseButtonDown(0)));
                break;
        }
    }

    private IEnumerator WaitForPartVideosEnd2D(StoryPart2D part)
    {
        if (part?.mainVideos == null || part.mainVideos.Count == 0)
        {
            yield return WaitPausable2D(Mathf.Max(0.1f, part.manualDuration));
            yield break;
        }

        var markedVideos = new List<StoryPartVideo2D>();
        var nonLoopVideos = new List<StoryPartVideo2D>();
        bool allLooping = true;

        foreach (var entry in part.mainVideos)
        {
            if (entry?.video == null) continue;

            if (entry.waitForPartEnd) markedVideos.Add(entry);
            if (!entry.loop) { nonLoopVideos.Add(entry); allLooping = false; }

            if (entry.waitForPartEnd && entry.loop)
                Debug.LogWarning($"[AR] StoryPart '{part.partName}': video '{entry.video.name}' is looping and also marked Wait For Part End. This part may never advance. Use Manual Duration if this is intentional.", entry.video);
        }

        List<StoryPartVideo2D> watchList = markedVideos.Count > 0 ? markedVideos : nonLoopVideos;

        if (watchList.Count == 0)
        {
            if (allLooping)
                Debug.LogWarning($"[AR] StoryPart '{part.partName}': all main videos are looping in Auto From Main Video mode. Falling back to Manual Duration ({part.manualDuration}s).", this);
            yield return WaitPausable2D(Mathf.Max(0.1f, part.manualDuration));
            yield break;
        }

        int n = watchList.Count;
        bool[] started = new bool[n];
        bool[] ended = new bool[n];

        float elapsedTimeout = 0f;
        const float timeoutSeconds = 600f;

        while (elapsedTimeout < timeoutSeconds)
        {
            if (_2dPaused) { yield return null; continue; }
            elapsedTimeout += Time.deltaTime;

            bool allEnded = true;
            for (int i = 0; i < n; i++)
            {
                StoryPartVideo2D entry = watchList[i];
                VideoPlayer vp = entry.video;

                if (vp == null) { ended[i] = true; continue; }
                if (ended[i]) continue;

                if (!started[i])
                {
                    if (vp.isPlaying && vp.time > 0.1) started[i] = true;
                    allEnded = false;
                }
                else
                {
                    if (!vp.isPlaying)
                    {
                        if (entry.delayAfterFinish > 0f)
                            yield return WaitPausable2D(entry.delayAfterFinish);
                        ended[i] = true;
                    }
                    else
                    {
                        allEnded = false;
                    }
                }
            }

            if (allEnded) yield break;
            yield return null;
        }

        Debug.LogWarning($"[AR] StoryPart '{part.partName}' Auto From Main Video reached safety timeout. Advancing to next part to prevent a stuck page.", this);
    }

    private IEnumerator WaitForPageAudioEnd2D()
    {
        Unsubscribe2DAudioEndEvent();
        _audioEndReceived2D = false;
        _audioEndHandler2D = (id) =>
        {
            if (id == pageId) _audioEndReceived2D = true;
        };

        ARMediaManager.OnVoiceCompleted += _audioEndHandler2D;

        float elapsedTimeout = 0f;
        const float timeoutSeconds = 600f;
        while (!_audioEndReceived2D && elapsedTimeout < timeoutSeconds)
        {
            if (!_2dPaused) elapsedTimeout += Time.deltaTime;
            yield return null;
        }

        if (!_audioEndReceived2D)
            Debug.LogWarning($"[AR] Page '{pageId}' waited for page audio end but no matching OnVoiceCompleted event arrived. Advancing safely.", this);

        Unsubscribe2DAudioEndEvent();
    }

    private void Unsubscribe2DAudioEndEvent()
    {
        if (_audioEndHandler2D == null) return;
        ARMediaManager.OnVoiceCompleted -= _audioEndHandler2D;
        _audioEndHandler2D = null;
        _audioEndReceived2D = false;
    }

    private IEnumerator WaitPausable2D(float seconds)
    {
        float remaining = seconds;
        while (remaining > 0f)
        {
            if (!_2dPaused) remaining -= Time.deltaTime;
            yield return null;
        }
    }

    // ── Timed changes ────────────────────────────────────────────────────────────

    private IEnumerator RunTimedChanges2D(StoryPart2D part)
    {
        float elapsed = 0f;
        var changes = part.timedChanges;
        bool[] fired = new bool[changes.Count];

        while (true)
        {
            if (!_2dPaused) elapsed += Time.deltaTime;

            bool allDone = true;
            for (int i = 0; i < changes.Count; i++)
            {
                if (fired[i]) continue;
                allDone = false;
                var tc = changes[i];
                if (tc == null || tc.target == null) { fired[i] = true; continue; }
                if (elapsed >= tc.atSeconds)
                {
                    fired[i] = true;
                    ApplyTimedAction2D(tc);
                }
            }
            if (allDone) yield break;
            yield return null;
        }
    }

    private void ApplyTimedAction2D(TimedChange2D tc)
    {
        if (tc.target == null) return;

        switch (tc.action)
        {
            case TimedAction2D.Show:
                tc.target.SetActive(true);
                break;

            case TimedAction2D.Hide:
                tc.target.SetActive(false);
                break;

            case TimedAction2D.FadeIn:
                tc.target.SetActive(true);
                _layerRoutines2D.Add(StartCoroutine(FadeLayer2D(tc.target.transform, 0f, 1f, tc.fadeDuration > 0f ? tc.fadeDuration : 0.4f)));
                break;

            case TimedAction2D.FadeOut:
                _layerRoutines2D.Add(StartCoroutine(FadeLayer2D(tc.target.transform, 1f, 0f, tc.fadeDuration > 0f ? tc.fadeDuration : 0.4f)));
                break;

            case TimedAction2D.PlayVideo:
            {
                VideoPlayer vp = tc.target.GetComponent<VideoPlayer>();
                if (vp != null && vp.gameObject.activeInHierarchy && vp.enabled)
                {
                    TrackActive2DVideo(vp);
                    vp.time = 0;
                    vp.Play();
                }
                break;
            }

            case TimedAction2D.StopVideo:
            {
                VideoPlayer vp = tc.target.GetComponent<VideoPlayer>();
                if (vp != null)
                {
                    vp.Stop();
                    vp.time = 0;
                    UntrackActive2DVideo(vp);
                }
                break;
            }

            case TimedAction2D.PauseVideo:
            {
                VideoPlayer vp = tc.target.GetComponent<VideoPlayer>();
                if (vp != null && vp.isPlaying) vp.Pause();
                break;
            }

            case TimedAction2D.ResumeVideo:
            {
                VideoPlayer vp = tc.target.GetComponent<VideoPlayer>();
                if (vp != null && vp.gameObject.activeInHierarchy && vp.enabled)
                {
                    TrackActive2DVideo(vp);
                    if (!vp.isPlaying) vp.Play();
                }
                break;
            }

            case TimedAction2D.SetVideoSpeed:
            {
                VideoPlayer vp = tc.target.GetComponent<VideoPlayer>();
                if (vp != null) vp.playbackSpeed = Mathf.Max(0.1f, tc.videoSpeed);
                break;
            }
        }
    }

    // ── Layer show / hide ────────────────────────────────────────────────────────

    private void ShowPartLayers2D(StoryPart2D part)
    {
        if (part?.visualLayers == null) return;
        foreach (var vl in part.visualLayers)
        {
            if (vl?.layer == null) continue;
            if (!vl.showAtPartStart) continue;
            _layerRoutines2D.Add(StartCoroutine(ShowVisualLayer2DRoutine(vl)));
            if (vl.enableSmallMotion && vl.motionTarget != null)
                StartSmallMotion2D(vl.motionTarget, vl.motionType, vl.motionSpeed, vl.motionAmplitude);
        }
    }

    private IEnumerator ShowVisualLayer2DRoutine(VisualLayer2D vl)
    {
        if (vl.startDelay > 0f) yield return WaitPausable2D(vl.startDelay);
        vl.layer.gameObject.SetActive(true);
        if (vl.fadeIn && vl.fadeInDuration > 0f)
            yield return FadeLayer2D(vl.layer, 0f, 1f, vl.fadeInDuration);
        else
            ApplyAlpha2D(vl.layer, 1f);

        if (vl.visibleDuration > 0f)
        {
            yield return WaitPausable2D(vl.visibleDuration);
            if (vl.layer != null)
            {
                if (vl.fadeOut && vl.fadeOutDuration > 0f)
                    yield return FadeLayer2D(vl.layer, 1f, 0f, vl.fadeOutDuration);
                ApplyAlpha2D(vl.layer, 0f);
                vl.layer.gameObject.SetActive(false);
            }
        }
    }

    private void HidePartLayers2D(StoryPart2D part)
    {
        if (part?.visualLayers == null) return;
        foreach (var vl in part.visualLayers)
        {
            if (vl?.layer == null) continue;
            if (vl.keepVisibleIntoNextPart) continue;
            if (!vl.hideAtPartEnd) continue;
            _layerRoutines2D.Add(StartCoroutine(HideVisualLayer2DRoutine(vl)));
        }
    }

    private IEnumerator HideVisualLayer2DRoutine(VisualLayer2D vl)
    {
        if (vl.fadeOut && vl.fadeOutDuration > 0f)
            yield return FadeLayer2D(vl.layer, 1f, 0f, vl.fadeOutDuration);
        if (vl.layer != null)
        {
            StopSmallMotion2D(vl.motionTarget);
            ApplyAlpha2D(vl.layer, 0f);
            vl.layer.gameObject.SetActive(false);
        }
    }

    private void HidePartLayersInstant2D(StoryPart2D part)
    {
        if (part == null) return;
        if (part.visualLayers != null)
        {
            foreach (var vl in part.visualLayers)
            {
                if (vl?.layer == null) continue;
                StopSmallMotion2D(vl.motionTarget);
                ApplyAlpha2D(vl.layer, 0f);
                vl.layer.gameObject.SetActive(false);
            }
        }
        if (part.backgroundImage != null)
        {
            ApplyAlpha2D(part.backgroundImage, 0f);
            part.backgroundImage.gameObject.SetActive(false);
        }
    }

    private void ShowPartBackgroundImage2D(StoryPart2D part)
    {
        if (part?.backgroundImage == null) return;
        part.backgroundImage.gameObject.SetActive(true);
        ApplyAlpha2D(part.backgroundImage, 1f);
    }

    private void HidePartBackgroundImage2D(StoryPart2D part)
    {
        if (part?.backgroundImage == null) return;
        ApplyAlpha2D(part.backgroundImage, 0f);
        part.backgroundImage.gameObject.SetActive(false);
    }

    // ── Videos ──────────────────────────────────────────────────────────────────

    // Safety net: force-clears every OTHER slot's main video, background video,
    // background image, and decorative layers before the given slot starts, so
    // nothing left over from another slot can ever be visibly showing at the same
    // time as the active slot. Works for any number of slots — not tied to any
    // specific page.
    //
    // Background videos with stopAtPartEnd = false, and layers with
    // keepVisibleIntoNextPart = true, are intentionally allowed to carry on into
    // the next slot — this safety net does not touch those, so it never breaks
    // that intentional carry-over behavior.
    private void StopAllOtherPartVideos2D(int exceptIndex)
    {
        if (storyParts == null) return;

        for (int i = 0; i < storyParts.Count; i++)
        {
            if (i == exceptIndex) continue;
            StoryPart2D other = storyParts[i];
            if (other == null) continue;

            if (other.mainVideos != null)
                foreach (var entry in other.mainVideos)
                {
                    if (entry?.video == null) continue;
                    if (entry.video.isPlaying) entry.video.Stop();
                    entry.video.gameObject.SetActive(false);
                }

            if (other.backgroundImage != null)
            {
                ApplyAlpha2D(other.backgroundImage, 0f);
                other.backgroundImage.gameObject.SetActive(false);
            }

            if (other.visualLayers != null)
                foreach (var vl in other.visualLayers)
                {
                    if (vl?.layer == null) continue;
                    if (vl.keepVisibleIntoNextPart) continue; // intentional carry-over — leave alone
                    ApplyAlpha2D(vl.layer, 0f);
                    vl.layer.gameObject.SetActive(false);
                }

            if (other.backgroundVideos != null)
                foreach (var entry in other.backgroundVideos)
                {
                    if (entry?.video == null) continue;
                    if (!entry.stopAtPartEnd) continue; // intentional carry-over — leave alone
                    if (entry.video.isPlaying) entry.video.Stop();
                }
        }
    }

    private void StartPartMainVideos2D(StoryPart2D part)
    {
        if (part?.mainVideos == null) return;

        foreach (var entry in part.mainVideos)
        {
            if (entry?.video == null) continue;
            VideoPlayer vp = entry.video;

            if (!vp.gameObject.activeSelf) vp.gameObject.SetActive(true);
            if (!vp.gameObject.activeInHierarchy || !vp.enabled) continue;

            vp.Stop();
            vp.time = 0;
            vp.isLooping = entry.loop;
            vp.playbackSpeed = Mathf.Max(0.01f, entry.playbackSpeed);

            TrackActive2DVideo(vp);

            // Derive freeze mode from the Freeze First/Last seconds fields shown in
            // the Inspector — matches how background videos derive their freeze mode,
            // so typing a value into Freeze First/Last always takes effect here too.
            var freezeMode = entry.freezeFirstSeconds > 0f
                ? VuforiaVideoFrameFreezeController.FreezeMode.FreezeFirstFrame
                : entry.freezeLastSeconds > 0f
                    ? VuforiaVideoFrameFreezeController.FreezeMode.FreezeLastFrameThenStop
                    : VuforiaVideoFrameFreezeController.FreezeMode.None;

            VideoFreezeRuntime runtime = GetOrCreate2DVideoRuntime(vp);
            runtime.RestartWithFreeze(freezeMode, entry.freezeFirstSeconds, entry.freezeLastSeconds, entry.playbackSpeed, entry.startDelay);
        }
    }

    private IEnumerator DelayedPlayVideo2D(VideoPlayer vp, float delay)
    {
        yield return WaitPausable2D(delay);
        if (vp != null && vp.gameObject.activeInHierarchy && vp.enabled && !vp.isPlaying)
        {
            TrackActive2DVideo(vp);
            vp.Play();
        }
    }

    private void StopPartMainVideos2D(StoryPart2D part)
    {
        if (part?.mainVideos == null) return;

        foreach (var entry in part.mainVideos)
        {
            if (entry?.video == null) continue;

            VideoPlayer vp = entry.video;

            // When a slot ends, its main video ALWAYS stops and its picture ALWAYS
            // disappears — so the next slot starts on a clean screen with no leftover
            // frame showing underneath it. This does not depend on the stopAtPartEnd
            // flag, which was hidden from the Inspector and could be left "off".
            if (vp.enabled && vp.gameObject.activeInHierarchy)
            {
                vp.Stop();
                vp.time = 0;
            }

            UntrackActive2DVideo(vp);

            vp.gameObject.SetActive(false);
        }
    }

    private void StartPartBackgroundVideos2D(StoryPart2D part)
    {
        if (part?.backgroundVideos == null) return;

        foreach (var entry in part.backgroundVideos)
        {
            if (entry?.video == null) continue;
            VideoPlayer vp = entry.video;

            if (!vp.gameObject.activeSelf) vp.gameObject.SetActive(true);
            if (!vp.gameObject.activeInHierarchy || !vp.enabled) continue;

            vp.Stop();
            if (part.restartBackgroundOnSlotStart) vp.time = 0;
            vp.isLooping = entry.loop;

            TrackActive2DVideo(vp);

            if (entry.fadeIn && entry.fadeInDuration > 0f)
                _layerRoutines2D.Add(StartCoroutine(FadeLayer2D(vp.transform, 0f, 1f, entry.fadeInDuration)));
            else
                ApplyAlpha2D(vp.transform, 1f);

            bool hasFreezeEffect = entry.freezeFirstSeconds > 0f || entry.freezeLastSeconds > 0f;
            if (hasFreezeEffect)
            {
                // FreezeFirstFrame or FreezeLastFrameThenStop — pick first if both set
                var fMode = entry.freezeFirstSeconds > 0f
                    ? VuforiaVideoFrameFreezeController.FreezeMode.FreezeFirstFrame
                    : VuforiaVideoFrameFreezeController.FreezeMode.FreezeLastFrameThenStop;

                VideoFreezeRuntime rt = GetOrCreate2DVideoRuntime(vp);
                rt.RestartWithFreeze(fMode, entry.freezeFirstSeconds, entry.freezeLastSeconds, entry.playbackSpeed, entry.startDelay);
            }
            else if (entry.startDelay > 0f)
            {
                vp.playbackSpeed = Mathf.Max(0.01f, entry.playbackSpeed);
                _layerRoutines2D.Add(StartCoroutine(DelayedPlayVideo2D(vp, entry.startDelay)));
            }
            else
            {
                vp.playbackSpeed = Mathf.Max(0.01f, entry.playbackSpeed);
                vp.Play();
            }
        }
    }

    private void StopPartBackgroundVideos2D(StoryPart2D part)
    {
        if (part?.backgroundVideos == null) return;

        foreach (var entry in part.backgroundVideos)
        {
            if (entry?.video == null) continue;
            // Videos with stopAtPartEnd=false carry on into the next slot intentionally.
            // They remain tracked so pause/resume on tracking-lost still works.
            if (!entry.stopAtPartEnd) continue;
            UntrackActive2DVideo(entry.video);
            _layerRoutines2D.Add(StartCoroutine(StopBackgroundVideoAfterFade2D(entry)));
        }
    }

    private IEnumerator StopBackgroundVideoAfterFade2D(StoryPartBgVideo2D entry)
    {
        VideoPlayer vp = entry?.video;
        if (vp == null) yield break;

        if (entry.fadeOut && entry.fadeOutDuration > 0f)
            yield return FadeLayer2D(vp.transform, 1f, 0f, entry.fadeOutDuration);

        if (vp.enabled && vp.gameObject.activeInHierarchy)
        {
            vp.Stop();
            vp.time = 0;
        }

        UntrackActive2DVideo(vp);

        if (entry.hideAtPartEnd)
            vp.gameObject.SetActive(false);
    }

    private void TrackActive2DVideo(VideoPlayer vp)
    {
        if (vp == null) return;
        if (!_active2DVideos.Contains(vp)) _active2DVideos.Add(vp);
    }

    private void UntrackActive2DVideo(VideoPlayer vp)
    {
        if (vp == null) return;
        _active2DVideos.Remove(vp);
        if (_active2DVideoRuntime.TryGetValue(vp, out var rt))
        {
            rt.Dispose();
            _active2DVideoRuntime.Remove(vp);
        }
    }

    private VideoFreezeRuntime GetOrCreate2DVideoRuntime(VideoPlayer vp)
    {
        if (vp == null) return null;
        if (!_active2DVideoRuntime.TryGetValue(vp, out var rt) || rt == null)
        {
            rt = new VideoFreezeRuntime(this, vp);
            _active2DVideoRuntime[vp] = rt;
        }
        return rt;
    }

    private void DisposeActive2DVideoRuntime()
    {
        foreach (var kv in _active2DVideoRuntime)
            kv.Value?.Dispose();
        _active2DVideoRuntime.Clear();
    }

    // ── Small motion ─────────────────────────────────────────────────────────────

    private void StartSmallMotion2D(Transform motionTarget, SmallMotionType motionType, float motionSpeed, float motionAmplitude)
    {
        if (motionTarget == null) return;

        StopSmallMotion2D(motionTarget);

        if (!_smallMotionBasePosition2D.ContainsKey(motionTarget))
            _smallMotionBasePosition2D[motionTarget] = motionTarget.localPosition;

        Coroutine c = StartCoroutine(SmallMotionRoutine2D(motionTarget, motionType, motionSpeed, motionAmplitude));
        _smallMotionByTarget2D[motionTarget] = c;
        _smallMotionRoutines.Add(c);
    }

    private void StopSmallMotion2D(Transform motionTarget)
    {
        if (motionTarget == null) return;

        if (_smallMotionByTarget2D.TryGetValue(motionTarget, out var c) && c != null)
        {
            StopCoroutine(c);
            _smallMotionRoutines.Remove(c);
        }

        _smallMotionByTarget2D.Remove(motionTarget);

        if (_smallMotionBasePosition2D.TryGetValue(motionTarget, out var basePos))
        {
            motionTarget.localPosition = basePos;
            _smallMotionBasePosition2D.Remove(motionTarget);
        }
    }

    private void StopAllSmallMotions2D()
    {
        foreach (var kv in _smallMotionByTarget2D)
        {
            if (kv.Value != null) StopCoroutine(kv.Value);
            if (kv.Key != null && _smallMotionBasePosition2D.TryGetValue(kv.Key, out var basePos))
                kv.Key.localPosition = basePos;
        }

        foreach (var c in _smallMotionRoutines)
            if (c != null) StopCoroutine(c);

        _smallMotionByTarget2D.Clear();
        _smallMotionBasePosition2D.Clear();
        _smallMotionRoutines.Clear();
    }

    private IEnumerator SmallMotionRoutine2D(Transform motionTarget, SmallMotionType motionType, float motionSpeed, float motionAmplitude)
    {
        if (motionTarget == null) yield break;

        Vector3 baseLocalPos = _smallMotionBasePosition2D.TryGetValue(motionTarget, out var stored)
            ? stored
            : motionTarget.localPosition;

        float elapsed = 0f;

        while (true)
        {
            if (!_2dPaused) elapsed += Time.deltaTime;

            Vector3 pos = baseLocalPos;
            switch (motionType)
            {
                case SmallMotionType.DriftX:
                    pos.x = baseLocalPos.x + Mathf.Sin(elapsed * motionSpeed) * motionAmplitude;
                    break;
                case SmallMotionType.DriftY:
                    pos.y = baseLocalPos.y + Mathf.Sin(elapsed * motionSpeed) * motionAmplitude;
                    break;
                case SmallMotionType.PingPongX:
                    pos.x = baseLocalPos.x + Mathf.PingPong(elapsed * motionSpeed, motionAmplitude * 2f) - motionAmplitude;
                    break;
                case SmallMotionType.PingPongY:
                    pos.y = baseLocalPos.y + Mathf.PingPong(elapsed * motionSpeed, motionAmplitude * 2f) - motionAmplitude;
                    break;
            }

            motionTarget.localPosition = pos;
            yield return null;
        }
    }

    // ── Alpha / Fade helpers ─────────────────────────────────────────────────────

    private IEnumerator FadeLayer2D(Transform layer, float from, float to, float duration)
    {
        if (layer == null) yield break;

        ApplyAlpha2D(layer, from);

        if (duration <= 0f)
        {
            ApplyAlpha2D(layer, to);
            yield break;
        }

        float t = 0f;
        while (t < duration)
        {
            if (_2dPaused) { yield return null; continue; }
            t += Time.deltaTime;
            ApplyAlpha2D(layer, Mathf.Lerp(from, to, Mathf.Clamp01(t / duration)));
            yield return null;
        }

        ApplyAlpha2D(layer, to);
    }

    private static void ApplyAlpha2D(Transform layer, float a)
    {
        if (layer == null) return;

        CanvasGroup cg = layer.GetComponent<CanvasGroup>();
        if (cg != null)
        {
            cg.alpha = a;
            return;
        }

        foreach (SpriteRenderer sr in layer.GetComponentsInChildren<SpriteRenderer>(true))
        {
            if (sr == null) continue;
            Color c = sr.color;
            c.a = a;
            sr.color = c;
        }

        foreach (Graphic g in layer.GetComponentsInChildren<Graphic>(true))
        {
            if (g == null) continue;
            Color c = g.color;
            c.a = a;
            g.color = c;
        }

        foreach (Renderer r in layer.GetComponentsInChildren<Renderer>(true))
        {
            if (r == null || r is SpriteRenderer) continue;
            Material[] mats = r.materials;
            for (int i = 0; i < mats.Length; i++)
            {
                Material m = mats[i];
                if (m == null) continue;

                if (m.HasProperty("_BaseColor"))
                {
                    Color c = m.GetColor("_BaseColor");
                    c.a = a;
                    m.SetColor("_BaseColor", c);
                }
                else if (m.HasProperty("_Color"))
                {
                    Color c = m.GetColor("_Color");
                    c.a = a;
                    m.SetColor("_Color", c);
                }
            }
        }
    }

}
