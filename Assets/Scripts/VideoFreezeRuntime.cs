using System.Collections;
using UnityEngine;
using UnityEngine.Video;

/// <summary>
/// Per-VideoPlayer playback helper: handles freeze-first-frame, freeze-last-frame,
/// playback speed, and start delay. Shared by ARTrackedPageNode (simple pages) and
/// TwoDPageDirector (Story Parts pages) so freeze behavior stays identical everywhere.
///
/// This is a RUNTIME-ONLY helper (never serialized) — extracting it from ARTrackedPageNode
/// touches no saved data and changes no behavior.
/// </summary>
public sealed class VideoFreezeRuntime
{
    private readonly MonoBehaviour _host;
    private readonly VideoPlayer _vp;
    private VuforiaVideoFrameFreezeController.FreezeMode _mode;
    private float _firstSeconds;
    private float _lastSeconds;
    private float _playbackSpeed = 1f;
    private float _startDelay;
    private Coroutine _firstRoutine;
    private Coroutine _lastRoutine;
    private Coroutine _startRoutine;
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
        float firstSeconds, float lastSeconds,
        float playbackSpeed, float startDelay)
    {
        _mode = mode;
        _firstSeconds = Mathf.Max(0f, firstSeconds);
        _lastSeconds = Mathf.Max(0f, lastSeconds);
        _playbackSpeed = Mathf.Max(0.01f, playbackSpeed);
        _startDelay = Mathf.Max(0f, startDelay);
        StopRoutinesInternal();
        if (_vp == null) return;
        if (!_vp.gameObject.activeInHierarchy) return;
        _vp.Stop();
        _vp.time = 0;
        _vp.playbackSpeed = _playbackSpeed;
        _startRoutine = _host.StartCoroutine(StartAfterDelayRoutine());
    }

    // Honours per-clip Start Delay before kicking off freeze / playback.
    private IEnumerator StartAfterDelayRoutine()
    {
        if (_startDelay > 0f) yield return new WaitForSeconds(_startDelay);
        _startRoutine = null;
        if (_vp == null) yield break;
        if (!_vp.gameObject.activeInHierarchy) yield break;
        _vp.playbackSpeed = _playbackSpeed;
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
        if (_startRoutine != null) { _host.StopCoroutine(_startRoutine); _startRoutine = null; }
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
