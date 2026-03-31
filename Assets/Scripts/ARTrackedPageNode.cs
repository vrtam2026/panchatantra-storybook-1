using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Video;

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

        // Auto-find ALL SplinePathMovers in children
        if (splinePathMovers.Count == 0)
        {
            var found = GetComponentsInChildren<SplinePathMover>(true);
            foreach (var m in found)
                splinePathMovers.Add(m);
        }

        // Auto-find ARTrackableSplineMover in children
        if (splineMovers.Count == 0)
        {
            var mover = GetComponentInChildren<ARTrackableSplineMover>(true);
            if (mover != null) splineMovers.Add(mover);
        }

        // Auto-find Animator in children
        if (animators.Count == 0)
        {
            var anim = GetComponentInChildren<Animator>(true);
            if (anim != null) animators.Add(anim);
        }

        RebuildVideoRuntimeCache();
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

        // NEW: trigger SplinePathMovers
        for (int i = 0; i < splinePathMovers.Count; i++)
        {
            var m = splinePathMovers[i];
            if (m == null) continue;
            m.Stop();
            m.ResetToStart();
            m.PlayOnce();
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

        // NEW
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

        // NEW
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