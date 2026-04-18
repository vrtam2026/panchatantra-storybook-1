using System.Collections;
using System.Collections.Generic;
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

    [Tooltip("Black plane child of this page node. Name it 'BlackOverlay'. Auto-found if left empty.")]
    [SerializeField] private Renderer blackOverlay;

    [Tooltip("Seconds to wait after video ends before black fades in.")]
    [Min(0f)]
    [SerializeField] private float delayBeforeFade = 0.5f;

    [Tooltip("How long the black plane fades in (seconds).")]
    [Min(0f)]
    [SerializeField] private float fadeDuration = 1.5f;

    [Tooltip("How long to hold fully black before page end panel appears (seconds).")]
    [Min(0f)]
    [SerializeField] private float holdDuration = 0.3f;

    [Tooltip("Optional sound played when the page end panel appears.")]
    [SerializeField] private AudioClip pageTurnSound;

    // Auto-found at runtime from OverlayManager -- no drag needed
    private CanvasGroup _pageEndPanel;
    private UnityEngine.UI.Image _pageEndImage;

    // private state
    private Coroutine _revealCoroutine;
    private Coroutine _pageEndFrameCoroutine;
    private AudioSource _revealAudioSource;
    // ---------------------------------------------------------------

    private bool _isTracked;
    private float _lastLostTime = -999f;

    public string PageId => pageId;
    public bool IsTracked => _isTracked;
    public bool LoopBgmUntilVoiceEnds => loopBgmUntilVoiceEnds;
    public bool StopBgmWhenVoiceEnds => stopBgmWhenVoiceEnds;

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

        if (splineMovers.Count == 0)
        {
            var mover = GetComponentInChildren<ARTrackableSplineMover>(true);
            if (mover != null) splineMovers.Add(mover);
        }

        if (animators.Count == 0)
        {
            var anim = GetComponentInChildren<Animator>(true);
            if (anim != null) animators.Add(anim);
        }

        RebuildVideoRuntimeCache();

        // REVEAL SETUP
        SetupReveal();
    }

    private void OnDestroy()
    {
        foreach (var kv in _videoRuntime)
            kv.Value.Dispose();
        _videoRuntime.Clear();
    }

    private void OnEnable()
    {
        if (mediaManager != null) mediaManager.RegisterNode(this);
    }

    private void OnDisable()
    {
        if (mediaManager != null) mediaManager.UnregisterNode(this);
    }

    // ---------------------------------------------------------------
    // REVEAL SETUP
    // ---------------------------------------------------------------

    private void SetupReveal()
    {
        // Auto-find black overlay in children if not assigned
        if (blackOverlay == null)
        {
            var r = GetComponentInChildren<Renderer>(true);
            if (r != null && r.gameObject.name == "BlackOverlay")
                blackOverlay = r;
        }

        // Auto-find OverlayManager panels -- set ONCE in scene, used by all pages
        var overlayManager = OverlayManager.Instance;
        if (overlayManager != null)
        {
            _pageEndPanel = overlayManager.PageEndPanel;
            _pageEndImage = overlayManager.PageEndImage;
        }

        // Setup audio source for page turn sound
        if (pageTurnSound != null)
        {
            _revealAudioSource = gameObject.AddComponent<AudioSource>();
            _revealAudioSource.clip = pageTurnSound;
            _revealAudioSource.playOnAwake = false;
            _revealAudioSource.loop = false;
        }

        // Black plane: force render queue above video (ChromaKey = 3000), start invisible
        if (blackOverlay != null)
        {
            blackOverlay.material.renderQueue = 3500;
            Color c = blackOverlay.material.GetColor("_BaseColor");
            c.a = 0f;
            blackOverlay.material.SetColor("_BaseColor", c);
        }
    }

    // Called from StartFromBeginning - starts watching the video
    private void StartWatchingVideo()
    {
        if (blackOverlay == null && _pageEndPanel == null) return;
        if (mainVideos == null || mainVideos.Count == 0 || mainVideos[0] == null) return;

        if (_revealCoroutine != null)
        {
            StopCoroutine(_revealCoroutine);
            _revealCoroutine = null;
        }

        _revealCoroutine = StartCoroutine(WatchVideoThenFade(mainVideos[0]));
    }

    private IEnumerator WatchVideoThenFade(VideoPlayer vp)
    {
        // PHASE 1: Wait for video to start playing at all
        float startTimeout = Time.time + 10f;
        while (!vp.isPlaying && Time.time < startTimeout)
            yield return null;

        if (!vp.isPlaying)
        {
            Debug.LogWarning("[AR] Reveal: video never started.");
            yield break;
        }

        // PHASE 2: Wait for time to move past freeze
        // FreezeFirstFrame pauses on frame 0 -- vp.isPlaying can be false or time stays near 0
        // Wait until time moves clearly past 0.1s AND video is playing again
        float phaseTimeout = Time.time + 15f;
        while (Time.time < phaseTimeout)
        {
            if (!_isTracked) { yield return new WaitUntil(() => _isTracked); }
            if (vp.isPlaying && vp.time > 0.1) break;
            yield return null;
        }

        Debug.Log($"[AR] Reveal: past freeze. time={vp.time:F2}");

        // PHASE 3: Wait for video to stop (true end, not freeze)
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

    private IEnumerator FadeAndReveal()
    {
        // Refresh panel references from OverlayManager in case they changed
        if (OverlayManager.Instance != null)
        {
            _pageEndPanel = OverlayManager.Instance.PageEndPanel;
            _pageEndImage = OverlayManager.Instance.PageEndImage;
        }

        // Step 1: short delay after video ends
        if (delayBeforeFade > 0f)
            yield return new WaitForSeconds(delayBeforeFade);

        // Step 2: fade world space black plane from transparent to solid
        if (blackOverlay != null)
        {
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

        // Step 3: hold fully black
        if (holdDuration > 0f)
            yield return new WaitForSeconds(holdDuration);

        // Step 4: show page end panel and fade it in
        if (_pageEndPanel != null)
        {
            _pageEndPanel.gameObject.SetActive(true);
            _pageEndPanel.alpha = 0f;

            // Start character frame cycling via OverlayManager
            if (OverlayManager.Instance != null)
                OverlayManager.Instance.StartPageEndCycle();

            float elapsed = 0f;
            float duration = OverlayManager.Instance != null ? OverlayManager.Instance.ImageFadeDuration : 1f;
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                _pageEndPanel.alpha = Mathf.Clamp01(elapsed / duration);
                yield return null;
            }
            _pageEndPanel.alpha = 1f;
        }

        // Step 5: start BGM and sound when panel is visible
        if (mediaManager != null)
            mediaManager.StartPostVoiceBgm();

        if (_revealAudioSource != null && pageTurnSound != null)
            _revealAudioSource.Play();

        _revealCoroutine = null;
    }

    private void ResetReveal()
    {
        // Stop coroutines
        if (_revealCoroutine != null) { StopCoroutine(_revealCoroutine); _revealCoroutine = null; }
        if (_pageEndFrameCoroutine != null) { StopCoroutine(_pageEndFrameCoroutine); _pageEndFrameCoroutine = null; }

        // Reset black plane to invisible
        if (blackOverlay != null)
        {
            Color c = blackOverlay.material.GetColor("_BaseColor");
            c.a = 0f;
            blackOverlay.material.SetColor("_BaseColor", c);
        }

        // Hide page end panel via OverlayManager
        if (OverlayManager.Instance != null)
            OverlayManager.Instance.HidePageEndPanel();
        else if (_pageEndPanel != null)
        {
            _pageEndPanel.alpha = 0f;
            _pageEndPanel.gameObject.SetActive(false);
        }
    }

    // ---------------------------------------------------------------

    private void RebuildVideoRuntimeCache()
    {
        _videoRuntime.Clear();
        AddToRuntime(mainVideos);
        AddToRuntime(backgroundLoopVideos);
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

    public void StartFromBeginning()
    {
        // RESET REVEAL
        ResetReveal();

        RebuildVideoRuntimeCache();

        RestartVideosWithFreeze(mainVideos, freezeMode, freezeFirstSeconds, freezeLastSeconds);
        RestartVideosNoFreeze(backgroundLoopVideos);

        for (int i = 0; i < animators.Count; i++)
        {
            var a = animators[i];
            if (a == null) continue;
            a.speed = 1f;
            a.Rebind();
            a.Update(0f);
        }

        for (int i = 0; i < splineMovers.Count; i++)
        {
            var m = splineMovers[i];
            if (m == null) continue;
            m.Stop();
            m.ResetToStart();
            m.PlayOnce();
        }

        for (int i = 0; i < splinePathMovers.Count; i++)
        {
            var m = splinePathMovers[i];
            if (m == null) continue;
            m.Stop();
            m.ResetToStart();
            m.PlayOnce();
        }

        // Start watching for video end to trigger reveal
        StartWatchingVideo();
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
    }

    public void ResumeVisuals()
    {
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
            if (vp == null) continue;
            if (!vp.gameObject.activeInHierarchy) continue;
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