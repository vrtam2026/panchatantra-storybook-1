using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Video;
using UnityEngine.InputSystem;

/// <summary>
/// OPTIONAL Story Parts director for complex 2D pages.
///
/// A 2D page is split into sequential Story Parts. Each part has its own main video(s) and
/// visual layers. When a part's main video finishes, the director advances to the next part —
/// no manual timeline seconds needed. Persistent layers stay visible across all parts.
///
/// This component is OPTIONAL. Pages without it use ARTrackedPageNode's original main-video path
/// unchanged. ARTrackedPageNode delegates the per-part sequence + page-end trigger to this when present.
///
/// Voice/BGM stay page-level (ARMediaManager) — the director only sequences VISUALS.
/// </summary>
[DisallowMultipleComponent]
public class TwoDPageDirector : MonoBehaviour
{
    public enum PartTiming { AutoFromMainVideo, ManualDuration, WaitForTap }

    [System.Serializable]
    public class PartLayer
    {
        public Transform layer;
        [Min(0f)] public float startDelay = 0f;
        public bool fadeIn = true;
        [Min(0f)] public float fadeInDuration = 0.4f;
        public bool hideAfterPart = true;
        [Min(0f)] public float fadeOutDuration = 0.4f;
        public bool keepVisibleIntoNextPart = false;
    }

    [System.Serializable]
    public class StoryPart
    {
        public string partName = "Part";
        public List<VideoPlayer> mainVideos = new();
        public List<MainVideoSettings> mainVideoSettings = new();   // reuses ARTrackedPageNode's class
        public List<PartLayer> layers = new();
        public PartTiming timing = PartTiming.AutoFromMainVideo;
        [Min(0f)] public float manualDuration = 3f;
    }

    [Tooltip("Root that holds the 2D layers (usually All_GameObject). Auto-found if left empty.")]
    [SerializeField] private Transform layerRoot;

    [Tooltip("Layers that stay visible for the WHOLE page (sky, frame, common background). Never hidden between parts.")]
    [SerializeField] private List<Transform> persistentLayers = new();

    [SerializeField] private List<StoryPart> parts = new();

    // ── runtime ───────────────────────────────────────────────────────────────
    private ARTrackedPageNode _node;
    private Coroutine _runRoutine;
    private readonly List<Coroutine> _layerRoutines = new();
    private readonly Dictionary<VideoPlayer, VideoFreezeRuntime> _videoRuntime = new();
    private bool _paused;
    private bool _waitingForTap;
    private bool _tapRequested;
    private int _currentPartIndex = -1;

    public IReadOnlyList<StoryPart> Parts => parts;
    public Transform LayerRoot => layerRoot;

    private void Awake()
    {
        if (layerRoot == null) layerRoot = transform;
        _node = GetComponentInParent<ARTrackedPageNode>(true);
        BuildVideoRuntime();
    }

    private void OnDestroy()
    {
        foreach (var rt in _videoRuntime.Values) rt.Dispose();
        _videoRuntime.Clear();
    }

    private void BuildVideoRuntime()
    {
        foreach (var rt in _videoRuntime.Values) rt.Dispose();
        _videoRuntime.Clear();

        if (parts == null) return;
        foreach (var part in parts)
        {
            if (part?.mainVideos == null) continue;
            foreach (var vp in part.mainVideos)
            {
                if (vp == null || _videoRuntime.ContainsKey(vp)) continue;
                _videoRuntime.Add(vp, new VideoFreezeRuntime(this, vp));
            }
        }
    }

    private void Update()
    {
        if (!_waitingForTap || _paused) return;

        bool tapped =
            (Touchscreen.current != null && Touchscreen.current.primaryTouch.press.wasPressedThisFrame) ||
            (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame);

        if (tapped) _tapRequested = true;
    }

    // External input relays (optional) can advance a Wait-For-Tap part too.
    public void NotifyTap() => _tapRequested = true;

    // ── public control surface (called by ARTrackedPageNode) ────────────────────

    public void BeginFromStart()
    {
        StopRun();
        BuildVideoRuntime();           // pick up any inspector edits
        _paused = false;
        _waitingForTap = false;
        _tapRequested = false;

        StopAllPartVideos();
        InitializeLayersToStart();

        _currentPartIndex = -1;
        _runRoutine = StartCoroutine(RunParts());
    }

    public void Pause()
    {
        _paused = true;
        foreach (var rt in _videoRuntime.Values) rt.Pause();
    }

    public void Resume()
    {
        _paused = false;
        // Resume only the videos belonging to the part currently on screen.
        if (_currentPartIndex >= 0 && _currentPartIndex < parts.Count)
        {
            var part = parts[_currentPartIndex];
            foreach (var vp in part.mainVideos)
            {
                if (vp == null) continue;
                if (_videoRuntime.TryGetValue(vp, out var rt)) rt.Resume();
                else if (vp.gameObject.activeInHierarchy) vp.Play();
            }
        }
    }

    private void StopRun()
    {
        if (_runRoutine != null) { StopCoroutine(_runRoutine); _runRoutine = null; }
        StopLayerRoutines();
        _waitingForTap = false;
    }

    // ── core sequence ───────────────────────────────────────────────────────────

    private IEnumerator RunParts()
    {
        if (parts == null || parts.Count == 0)
        {
            // Nothing to sequence — go straight to page end so the page still turns.
            _runRoutine = null;
            if (_node != null) _node.StartPageEndFade();
            yield break;
        }

        for (int p = 0; p < parts.Count; p++)
        {
            _currentPartIndex = p;
            StoryPart part = parts[p];
            if (part == null) continue;

            ShowPartLayers(part);
            StartPartVideos(part);

            yield return WaitForPartEnd(part);

            HidePartLayers(part);
            StopPartVideos(part);
        }

        _runRoutine = null;
        _currentPartIndex = -1;

        // All parts finished → run the page's normal black-fade page-end sequence.
        if (_node != null) _node.StartPageEndFade();
    }

    private IEnumerator WaitForPartEnd(StoryPart part)
    {
        bool hasVideo = HasAnyVideo(part);

        switch (part.timing)
        {
            case PartTiming.AutoFromMainVideo:
                if (hasVideo) yield return WaitForPartVideosEnd(part);
                else yield return WaitPausable(Mathf.Max(0.1f, part.manualDuration)); // fallback when no video
                break;

            case PartTiming.ManualDuration:
                yield return WaitPausable(Mathf.Max(0.1f, part.manualDuration));
                break;

            case PartTiming.WaitForTap:
                _tapRequested = false;
                _waitingForTap = true;
                yield return new WaitUntil(() => _tapRequested);
                _waitingForTap = false;
                break;
        }
    }

    // Waits until every main video in the part has started and then finished.
    private IEnumerator WaitForPartVideosEnd(StoryPart part)
    {
        var videos = new List<VideoPlayer>();
        foreach (var vp in part.mainVideos) if (vp != null) videos.Add(vp);
        int n = videos.Count;
        if (n == 0) yield break;

        bool[] started = new bool[n];
        bool[] ended = new bool[n];

        float overallTimeout = Time.time + 600f;
        while (Time.time < overallTimeout)
        {
            if (_paused) { yield return null; continue; }

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
    }

    private IEnumerator WaitPausable(float seconds)
    {
        float remaining = seconds;
        while (remaining > 0f)
        {
            if (!_paused) remaining -= Time.deltaTime;
            yield return null;
        }
    }

    // ── videos ──────────────────────────────────────────────────────────────────

    private void StartPartVideos(StoryPart part)
    {
        for (int i = 0; i < part.mainVideos.Count; i++)
        {
            var vp = part.mainVideos[i];
            if (vp == null) continue;
            if (!vp.gameObject.activeSelf) vp.gameObject.SetActive(true);
            if (!vp.gameObject.activeInHierarchy) continue;

            MainVideoSettings s = (part.mainVideoSettings != null && i < part.mainVideoSettings.Count)
                ? part.mainVideoSettings[i] : null;

            var mode  = s != null ? s.freezeMode         : VuforiaVideoFrameFreezeController.FreezeMode.None;
            var first = s != null ? s.freezeFirstSeconds : 0f;
            var last  = s != null ? s.freezeLastSeconds  : 0f;
            var speed = s != null ? s.playbackSpeed      : 1f;
            var delay = s != null ? s.startDelay         : 0f;

            if (_videoRuntime.TryGetValue(vp, out var rt))
                rt.RestartWithFreeze(mode, first, last, speed, delay);
            else { vp.time = 0; vp.playbackSpeed = Mathf.Max(0.01f, speed); vp.Play(); }
        }
    }

    private void StopPartVideos(StoryPart part)
    {
        foreach (var vp in part.mainVideos)
        {
            if (vp == null) continue;
            if (_videoRuntime.TryGetValue(vp, out var rt)) rt.Pause();
            if (vp.enabled && vp.gameObject.activeInHierarchy) { vp.Stop(); vp.time = 0; }
        }
    }

    private void StopAllPartVideos()
    {
        foreach (var part in parts)
        {
            if (part?.mainVideos == null) continue;
            StopPartVideos(part);
        }
    }

    private static bool HasAnyVideo(StoryPart part)
    {
        if (part?.mainVideos == null) return false;
        foreach (var vp in part.mainVideos) if (vp != null) return true;
        return false;
    }

    // ── layers ──────────────────────────────────────────────────────────────────

    private void InitializeLayersToStart()
    {
        // Hide every per-part layer (alpha 0 + inactive).
        if (parts != null)
        {
            foreach (var part in parts)
            {
                if (part?.layers == null) continue;
                foreach (var pl in part.layers)
                {
                    if (pl?.layer == null) continue;
                    ApplyAlpha(pl.layer, 0f);
                    pl.layer.gameObject.SetActive(false);
                }
            }
        }

        // Persistent layers stay visible (override any of the above).
        if (persistentLayers != null)
        {
            foreach (var t in persistentLayers)
            {
                if (t == null) continue;
                t.gameObject.SetActive(true);
                ApplyAlpha(t, 1f);
            }
        }
    }

    private void ShowPartLayers(StoryPart part)
    {
        if (part?.layers == null) return;
        foreach (var pl in part.layers)
        {
            if (pl?.layer == null) continue;
            _layerRoutines.Add(StartCoroutine(ShowLayerRoutine(pl)));
        }
    }

    private IEnumerator ShowLayerRoutine(PartLayer pl)
    {
        if (pl.startDelay > 0f) yield return WaitPausable(pl.startDelay);

        pl.layer.gameObject.SetActive(true);

        if (pl.fadeIn && pl.fadeInDuration > 0f)
            yield return FadeRoutine(pl.layer, 0f, 1f, pl.fadeInDuration);
        else
            ApplyAlpha(pl.layer, 1f);
    }

    private void HidePartLayers(StoryPart part)
    {
        if (part?.layers == null) return;
        foreach (var pl in part.layers)
        {
            if (pl?.layer == null) continue;
            if (pl.keepVisibleIntoNextPart) continue;   // carries into the next part
            if (!pl.hideAfterPart) continue;            // explicitly kept on screen
            _layerRoutines.Add(StartCoroutine(HideLayerRoutine(pl)));
        }
    }

    private IEnumerator HideLayerRoutine(PartLayer pl)
    {
        if (pl.fadeOutDuration > 0f)
            yield return FadeRoutine(pl.layer, 1f, 0f, pl.fadeOutDuration);

        ApplyAlpha(pl.layer, 0f);
        pl.layer.gameObject.SetActive(false);
    }

    private void StopLayerRoutines()
    {
        foreach (var c in _layerRoutines) if (c != null) StopCoroutine(c);
        _layerRoutines.Clear();
    }

    // Self-contained alpha fade. Works for CanvasGroup, UI Image/RawImage, SpriteRenderer,
    // or a material with a color property — whichever the layer uses.
    private IEnumerator FadeRoutine(Transform layer, float from, float to, float duration)
    {
        var cg = layer.GetComponent<CanvasGroup>();
        SpriteRenderer[] sprites = cg == null ? layer.GetComponentsInChildren<SpriteRenderer>(true) : null;
        Graphic[] graphics       = cg == null ? layer.GetComponentsInChildren<Graphic>(true) : null;

        void Apply(float a)
        {
            if (cg != null) { cg.alpha = a; return; }
            if (sprites != null) foreach (var sr in sprites) { if (sr == null) continue; var c = sr.color; c.a = a; sr.color = c; }
            if (graphics != null) foreach (var g in graphics) { if (g == null) continue; var c = g.color; c.a = a; g.color = c; }
        }

        Apply(from);
        float t = 0f;
        while (t < duration)
        {
            if (_paused) { yield return null; continue; }
            t += Time.deltaTime;
            Apply(Mathf.Lerp(from, to, Mathf.Clamp01(t / duration)));
            yield return null;
        }
        Apply(to);
    }

    private static void ApplyAlpha(Transform layer, float a)
    {
        if (layer == null) return;

        var cg = layer.GetComponent<CanvasGroup>();
        if (cg != null) { cg.alpha = a; return; }

        var sprites = layer.GetComponentsInChildren<SpriteRenderer>(true);
        foreach (var sr in sprites) { if (sr == null) continue; var c = sr.color; c.a = a; sr.color = c; }

        var graphics = layer.GetComponentsInChildren<Graphic>(true);
        foreach (var g in graphics) { if (g == null) continue; var c = g.color; c.a = a; g.color = c; }
    }
}
