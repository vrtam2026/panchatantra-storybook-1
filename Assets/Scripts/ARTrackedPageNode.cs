using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Video;
using UnityEngine.UI;

public class ARTrackedPageNode : MonoBehaviour
{
    [Header("IDs")]
    [SerializeField] private string pageId;

    [Header("References")]
    [SerializeField] private ARMediaManager mediaManager;

    [Header("Videos")]
    [SerializeField] private List<VideoPlayer> mainVideos = new();
    [SerializeField] private List<VideoPlayer> backgroundLoopVideos = new();

    [Header("Animators (optional)")]
    [SerializeField] private List<Animator> animators = new();

    [Header("Spline movers (optional)")]
    [SerializeField] private List<ARTrackableSplineMover> splineMovers = new();

    [Header("Spline Path Movers (optional)")]
    [SerializeField] private List<SplinePathMover> splinePathMovers = new();

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
    // This prevents ARVFXPopupController from accidentally allowing story animators
    // or spline movement to continue after reveal while the user is still expected to tap.
    private bool _storyBlockedByActivity;
    private bool _mediaAudioPausedForActivity;

    public string PageId => pageId;
    public bool IsTracked => _isTracked;
    public bool LoopBgmUntilVoiceEnds => loopBgmUntilVoiceEnds;
    public bool StopBgmWhenVoiceEnds => stopBgmWhenVoiceEnds;

    // Read-only flag used by ARMediaManager so story audio does not start while
    // an opt-in activity is blocking the story. Normal pages keep this false.
    public bool IsStoryBlockedByActivity => _storyBlockedByActivity;

    private readonly Dictionary<VideoPlayer, VideoFreezeRuntime> _videoRuntime = new();

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
            // Auto-find ALL animators in children -- not just the first one
            var found = GetComponentsInChildren<Animator>(true);
            foreach (var anim in found)
                if (anim != null) animators.Add(anim);
        }

        RebuildVideoRuntimeCache();

        // REVEAL SETUP
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
    }

    // ---------------------------------------------------------------
    // REVEAL SETUP
    // ---------------------------------------------------------------

    private void SetupReveal()
    {
        // Auto-find black overlay by name
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

        // Setup audio source for page turn sound
        if (pageTurnSound != null)
        {
            _revealAudioSource = gameObject.AddComponent<AudioSource>();
            _revealAudioSource.clip = pageTurnSound;
            _revealAudioSource.playOnAwake = false;
            _revealAudioSource.loop = false;
        }

        // Black plane: force render queue above video, start invisible
        if (blackOverlay != null)
        {
            blackOverlay.material.renderQueue = 3500;
            Color c = blackOverlay.material.GetColor("_BaseColor");
            c.a = 0f;
            blackOverlay.material.SetColor("_BaseColor", c);
            blackOverlay.gameObject.SetActive(true);
        }
    }

    // Lazy-find All_GameObjects -- runs after Addressables clone is ready
    private GameObject GetContentRoot()
    {
        if (contentRoot != null) return contentRoot;

        // Search direct children for All_GameObjects (2D page)
        foreach (Transform child in transform)
        {
            if (child.name == "All_GameObjects" || child.name == "All_GameObject")
            {
                contentRoot = child.gameObject;
                return contentRoot;
            }
        }

        // Search one level deeper
        foreach (Transform child in transform)
        {
            var deep = child.Find("All_GameObjects") ?? child.Find("All_GameObject");
            if (deep != null) { contentRoot = deep.gameObject; return contentRoot; }
        }

        return null; // 3D page -- use HideAllChildren instead
    }

    // For 3D pages -- hide all direct children except frame and black screen
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

    // Called from StartFromBeginning -- auto detects content type and watches accordingly
    private void StartWatchingVideo()
    {
        if (!gameObject.activeInHierarchy) return;
        if (blackOverlay == null && OverlayManager.Instance == null) return;

        if (_revealCoroutine != null) { StopCoroutine(_revealCoroutine); _revealCoroutine = null; }

        bool hasVideo = mainVideos != null && mainVideos.Count > 0 && mainVideos[0] != null;
        bool hasAnimators = animators != null && animators.Count > 0;
        bool hasSplines = (splineMovers != null && splineMovers.Count > 0) ||
                          (splinePathMovers != null && splinePathMovers.Count > 0);

        if (hasVideo)
        {
            // 2D page -- watch the main video
            _revealCoroutine = StartCoroutine(WatchVideoThenFade(mainVideos[0]));
        }
        // 3D pages are handled automatically via ARMediaManager.OnVoiceCompleted
        // OverlayManager listens to that event and shows turn page after audio ends
        // If neither -- nothing to watch, no page end triggered
    }

    // ---------------------------------------------------------------
    // 2D PAGE WATCHER -- polls video until it truly ends
    // ---------------------------------------------------------------
    private IEnumerator WatchVideoThenFade(VideoPlayer vp)
    {
        // PHASE 1: Wait for video to start playing
        float startTimeout = Time.time + 10f;
        while (!vp.isPlaying && Time.time < startTimeout)
            yield return null;

        if (!vp.isPlaying)
        {
            Debug.LogWarning("[AR] Reveal: video never started.");
            yield break;
        }

        // PHASE 2: Wait past freeze first frame (time stays near 0 during freeze)
        float phaseTimeout = Time.time + 15f;
        while (Time.time < phaseTimeout)
        {
            if (!_isTracked) { yield return new WaitUntil(() => _isTracked); }
            if (vp.isPlaying && vp.time > 0.1) break;
            yield return null;
        }

        Debug.Log($"[AR] Reveal: past freeze. time={vp.time:F2}");

        // PHASE 3: Wait for video to stop (true end)
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

    // Called from animation event on 3D pages if manual trigger is preferred
    public void TriggerPageEnd()
    {
        // Manual trigger from animation event -- OverlayManager handles BGM via OnVoiceCompleted
        if (OverlayManager.Instance != null)
            OverlayManager.Instance.ShowPageEnd();
    }

    private IEnumerator FadeAndReveal()
    {
        // Step 1: delay after audio ends
        if (delayBeforeFade > 0f)
            yield return new WaitForSeconds(delayBeforeFade);

        // Step 2: ensure Black Screen is active and fade it in
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

        // Step 3: once fully black -- hide all content
        var root = GetContentRoot();
        if (root != null)
            root.SetActive(false);   // 2D: turn off All_GameObjects
        else
            HideAllChildren(true);   // 3D: turn off all children except frame

        // Step 4: turn off Black Screen -- clean frame visible
        if (blackOverlay != null)
            blackOverlay.gameObject.SetActive(false);

        // Step 5: hold for postFadeDelay before showing overlay
        if (postFadeDelay > 0f)
            yield return new WaitForSeconds(postFadeDelay);

        // Step 6: show turn page overlay
        if (OverlayManager.Instance != null)
            OverlayManager.Instance.ShowPageEnd();

        // Step 6: play page turn sound
        if (_revealAudioSource != null && pageTurnSound != null)
            _revealAudioSource.Play();

        _revealCoroutine = null;
    }




    private void ResetReveal()
    {
        if (_revealCoroutine != null) { StopCoroutine(_revealCoroutine); _revealCoroutine = null; }

        // Restore content visibility for next scan
        var root = GetContentRoot();
        if (root != null)
            root.SetActive(true);
        else
            HideAllChildren(false);

        // Restore Black Screen -- active but fully transparent
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
        AddToRuntime(mainVideos);
        AddToRuntime(backgroundLoopVideos);
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
        Debug.Log($"[AR] NotifyFound � pageId: '{pageId}'");
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

        // Reset page-end reveal / overlay state.
        ResetReveal();

        RebuildVideoRuntimeCache();

        // Reset visual systems but keep them paused until popup finishes.
        ResetStorySystemsToStartPaused();

        ARVFXPopupController popup = GetComponentInChildren<ARVFXPopupController>(true);

        if (popup != null && popup.isActiveAndEnabled)
        {
            _waitingForPopupReveal = true;
            ARVFXPopupController.OnRevealComplete += HandlePopupRevealComplete;

            // First scan and replay both use this same path.
            popup.TriggerReplay();
            return;
        }

        // Pages without VFX/popup start immediately. Also wake activity watchers that wait for a selected story animation.
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

    // Called by ActivityEventRelay from an animation event, timeline signal, or button.
    // It forwards the signal to ContentController without restarting the story.
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

    // Extra overloads keep Unity Animation Events safe if the event sends a number.
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

        // Important activity gate:
        // Some pages must run a child activity immediately after VFX reveal, before story animation,
        // spline movement, and voice over start. If ContentController has a pending After Reveal
        // activity, let it run first. When it completes, BeginStorySystemsNow continues the normal story.
        if (HasBlockingAfterRevealActivity())
        {
            ARVFXPopupController.OnRevealComplete -= HandlePopupRevealComplete;
            _waitingForPopupReveal = false;
            _storyBlockedByActivity = true;

            // ARVFXPopupController may re-enable child animators and movement scripts
            // just before firing OnRevealComplete. For an activity that must happen
            // before the story, pause them again immediately. Otherwise the activity
            // text appears while the story animation/spline starts underneath it.
            PauseStorySystemsForActivityGate();

            RunBeforeStoryActivitiesSequentially(() =>
            {
                _storyBlockedByActivity = false;
                BeginStorySystemsNow();
            });
            return;
        }

        // Wake activities that are waiting for a selected story animation or movement.
        // This is required for middle-story activities. Otherwise they only start when the full story ends.
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

        // Pre-story gate only: keep story at first frame until a before-story activity completes.
        ResetAnimatorsToFrameZeroPaused();

        // Stop movement. It will be reset and started by BeginStorySystemsNow()
        // after the activity is completed.
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

        // Do not move the model to spline start during popup.
        // Only stop movement here; ResetToStart happens after popup completion.
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

        // If tracking was lost while the popup was running, do not start
        // animator/spline/audio. ResumeVisuals will continue the paused reveal on re-track.
        if (!_isTracked)
        {
            _onStorySystemsStarted = null;
            return;
        }

        ARVFXPopupController.OnRevealComplete -= HandlePopupRevealComplete;
        _waitingForPopupReveal = false;

        _storyBlockedByActivity = false;

        RebuildVideoRuntimeCache();

        RestartVideosWithFreeze(mainVideos, freezeMode, freezeFirstSeconds, freezeLastSeconds);
        RestartVideosNoFreeze(backgroundLoopVideos);

        // Always start the story from a clean first frame. This is important after
        // pre-story activities because the activity may have played temporary
        // animation clips on the same animators.
        ResetAnimatorsToFrameZeroPaused();
        StartAnimatorsAfterIntro();
        StartSplinesAfterIntro();

        // Start watching for video end to trigger reveal only after real playback starts.
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

        // Some story pages keep the spline mover object disabled until the story begins.
        // After a blocking activity finishes, story/spline must start normally, so activate
        // the mover object itself. If a parent page is inactive because tracking is lost,
        // BeginStorySystemsNow already exits before this method is reached.
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

    public void PauseStoryForActivity()
    {
        // Middle-story activity lock.
        // Freeze the story exactly where it is. Do not reset story animators to frame zero.
        _storyBlockedByActivity = true;
        PauseStorySystemsAtCurrentFrameForActivity();
        PauseMediaAudioForActivity();
    }

    public void ResumeStoryFromActivity()
    {
        // Only the activity completion path may release this lock.
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

        ARVFXPopupController popup = GetComponentInChildren<ARVFXPopupController>(true);
        if (popup != null)
            popup.PauseReveal();
    }

    public void ResumeVisuals()
    {
        // Reset black overlay to transparent -- critical for grace time resume
        // Without this, black plane stays opaque if fade was in progress when lost
        if (blackOverlay != null)
        {
            blackOverlay.gameObject.SetActive(true);
            Color c = blackOverlay.material.GetColor("_BaseColor");
            c.a = 0f;
            blackOverlay.material.SetColor("_BaseColor", c);
        }

        // Restore content root if it was hidden
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
            // Tracking found while an activity is active.
            // Restore the page root but keep the story frozen at its current frame.
            PauseStorySystemsAtCurrentFrameForActivity();
            return;
        }

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

    private void RestartVideosWithFreeze(
        List<VideoPlayer> list,
        VuforiaVideoFrameFreezeController.FreezeMode mode,
        float firstSeconds, float lastSeconds)
    {
        if (list == null) return;
        for (int i = 0; i < list.Count; i++)
        {
            var vp = list[i];
            if (vp == null) continue;
            if (!vp.gameObject.activeInHierarchy) continue;
            if (_videoRuntime.TryGetValue(vp, out var rt))
                rt.RestartWithFreeze(mode, firstSeconds, lastSeconds);
            else { vp.time = 0; vp.Play(); }
        }
    }

    private static void RestartVideosNoFreeze(List<VideoPlayer> list)
    {
        if (list == null) return;
        for (int i = 0; i < list.Count; i++)
        {
            var vp = list[i];
            if (vp == null) continue;
            if (!vp.gameObject.activeInHierarchy) continue;
            vp.time = 0;
            vp.Play();
        }
    }

    private void PauseVideos(List<VideoPlayer> list)
    {
        if (list == null) return;
        for (int i = 0; i < list.Count; i++)
        {
            var vp = list[i];
            if (vp == null) return;
            if (!vp.gameObject.activeInHierarchy) continue;
            if (!vp.enabled) continue;  // disabled VideoPlayer cannot be paused
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

    private sealed class VideoFreezeRuntime
    {
        private readonly MonoBehaviour _host;
        private readonly VideoPlayer _vp;
        private VuforiaVideoFrameFreezeController.FreezeMode _mode;
        private float _firstSeconds;
        private float _lastSeconds;
        private Coroutine _firstRoutine;
        private Coroutine _lastRoutine;
        private bool _frameHooked;
        private bool _pausedOnFirstFrame;

        public VideoFreezeRuntime(MonoBehaviour host, VideoPlayer vp)
        {
            _host = host;
            _vp = vp;
            _vp.loopPointReached += OnLoopPointReached;
            _vp.waitForFirstFrame = true;
        }

        public void Dispose()
        {
            StopRoutinesInternal();
            if (_vp != null) _vp.loopPointReached -= OnLoopPointReached;
        }

        public void RestartWithFreeze(
            VuforiaVideoFrameFreezeController.FreezeMode mode,
            float firstSeconds, float lastSeconds)
        {
            _mode = mode;
            _firstSeconds = Mathf.Max(0f, firstSeconds);
            _lastSeconds = Mathf.Max(0f, lastSeconds);
            StopRoutinesInternal();
            if (_vp == null) return;
            if (!_vp.gameObject.activeInHierarchy) return;
            _vp.Stop();
            _vp.time = 0;
            if (ModeHasFirst(_mode))
                _firstRoutine = _host.StartCoroutine(FreezeFirstRoutine());
            else
                _vp.Play();
        }

        public void Pause()
        {
            StopRoutinesInternal();
            if (_vp == null) return;
            if (!_vp.enabled) return;  // disabled VideoPlayer cannot be paused
            _vp.Pause();
        }

        public void Resume()
        {
            if (_vp == null) return;
            _vp.Play();
        }

        private void OnLoopPointReached(VideoPlayer source)
        {
            if (_vp == null) return;
            if (!ModeHasLast(_mode)) return;
            if (_lastRoutine != null) return;
            _lastRoutine = _host.StartCoroutine(FreezeLastRoutine());
        }

        private IEnumerator FreezeFirstRoutine()
        {
            _pausedOnFirstFrame = false;
            _vp.waitForFirstFrame = true;
            _vp.sendFrameReadyEvents = true;
            if (!_frameHooked) { _vp.frameReady += OnFrameReady; _frameHooked = true; }
            _vp.Prepare();
            float timeout = Time.realtimeSinceStartup + 5f;
            while (!_vp.isPrepared && Time.realtimeSinceStartup < timeout) yield return null;
            _vp.time = 0;
            _vp.Play();
            float waitTimeout = Time.realtimeSinceStartup + 0.75f;
            while (!_pausedOnFirstFrame && Time.realtimeSinceStartup < waitTimeout) yield return null;
            if (!_pausedOnFirstFrame) _vp.Pause();
            CleanupFrameReadyHook();
            if (_firstSeconds > 0f) { yield return new WaitForSeconds(_firstSeconds); _vp.Play(); }
            _firstRoutine = null;
        }

        private void OnFrameReady(VideoPlayer source, long frameIdx)
        {
            if (_pausedOnFirstFrame) return;
            if (frameIdx <= 0) { _pausedOnFirstFrame = true; source.Pause(); }
        }

        private IEnumerator FreezeLastRoutine()
        {
            if (_vp.frameCount > 0) _vp.frame = (long)_vp.frameCount - 1;
            else if (_vp.length > 0.0001)
            { double t = _vp.length - 0.033; if (t < 0) t = 0; _vp.time = t; }
            _vp.Pause();
            if (_lastSeconds > 0f) yield return new WaitForSeconds(_lastSeconds);
            _lastRoutine = null;
        }

        private void StopRoutinesInternal()
        {
            if (_firstRoutine != null) { _host.StopCoroutine(_firstRoutine); _firstRoutine = null; }
            if (_lastRoutine != null) { _host.StopCoroutine(_lastRoutine); _lastRoutine = null; }
            CleanupFrameReadyHook();
        }

        private void CleanupFrameReadyHook()
        {
            if (_vp == null) return;
            _vp.sendFrameReadyEvents = false;
            if (_frameHooked) { _vp.frameReady -= OnFrameReady; _frameHooked = false; }
        }

        private static bool ModeHasFirst(VuforiaVideoFrameFreezeController.FreezeMode mode)
            => mode.ToString().Contains("First");

        private static bool ModeHasLast(VuforiaVideoFrameFreezeController.FreezeMode mode)
            => mode.ToString().Contains("Last");
    }
}