using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class ARMediaManager : MonoBehaviour
{
    [Header("Global UI")]
    [SerializeField] private Button replayButton;
    // Lost Tracking panel is handled by OverlayManager -- no drag needed here

    [Header("Audio")]
    [SerializeField] private ARAddressableAudioService audioService;

    [Tooltip("Real amplification multiplier. 1 = original, 5 = 5x louder.")]
    [Range(1f, 10f)]
    [SerializeField] private float amplifyMultiplier = 5f;

    [Header("Post-Voice BGM")]
    [Tooltip("This BGM plays on ALL pages after voice ends. Loops until next page is scanned. Leave empty to skip.")]
    [SerializeField] private AudioClip postVoiceBgm;

    [Tooltip("Volume of the post-voice BGM. 0 to 1.")]
    [Range(0f, 1f)]
    [SerializeField] private float postVoiceBgmVolume = 0.5f;

    [Header("Behavior")]
    [SerializeField, Min(0f)] private float resumeGraceSeconds = 1f;
    [SerializeField] private string defaultLanguage = "English";

    // Audio sources
    private AudioSource _voiceSource;
    private AudioSource _bgmSource;      // BGM1 — plays with voice
    private AudioSource _bgm2Source;     // BGM2 — plays after voice ends, global

    private AudioAmplifier _voiceAmplifier;
    private AudioAmplifier _bgmAmplifier;
    private AudioAmplifier _bgm2Amplifier;

    private readonly HashSet<ARTrackedPageNode> _nodes = new HashSet<ARTrackedPageNode>();
    private ARTrackedPageNode _activeNode;

    private Coroutine _voiceRoutine;
    private Coroutine _bgmDelayRoutine;
    private bool _paused;
    private int _voiceIndex;
    private float _delayTimer;
    private float _lastLostTime = -999f;
    private int _startRequestId = 0;
    private int _langSwitchId = 0;
    private int _lastReplayFrame = -1;

    private enum VoiceStage { None, DelayBefore, Playing, DelayAfter }
    private VoiceStage _stage = VoiceStage.None;

    // Fires when voice audio fully completes — carries pageId
    public static event System.Action<string> OnVoiceCompleted;

    // Fires before a page replay starts so page-specific activity/UI state can reset.
    public static event System.Action<string> OnPageRestarted;

    // Current active page id -- used by OverlayManager to verify page before showing turn page
    public static string ActivePageId { get; private set; }

    public bool IsVoiceSequenceActive => _stage != VoiceStage.None;

    // ----------------------------------------------------------------------
    // Unity lifecycle
    // ----------------------------------------------------------------------

    private void Awake()
    {
        EnsureAudioSources();
        AudioListener.volume = 1f;

        if (!PlayerPrefs.HasKey(ARGlobalLanguage.PlayerPrefsKey))
        {
            string lang = string.IsNullOrWhiteSpace(defaultLanguage) ? "English" : defaultLanguage;
            ARGlobalLanguage.SetCurrentLanguage(lang);
        }

        if (replayButton != null)
        {
            replayButton.onClick.RemoveListener(OnReplayPressed);
            replayButton.onClick.AddListener(OnReplayPressed);
            replayButton.gameObject.SetActive(false);
        }
    }

    private void OnEnable()
    {
        ARGlobalLanguage.OnLanguageChanged += OnLanguageChanged;
    }

    private void OnDisable()
    {
        ARGlobalLanguage.OnLanguageChanged -= OnLanguageChanged;
        if (replayButton != null)
            replayButton.onClick.RemoveListener(OnReplayPressed);
    }

    // ----------------------------------------------------------------------
    // Audio source setup
    // ----------------------------------------------------------------------

    private void EnsureAudioSources()
    {
        _voiceSource = GetOrCreateChannelSource("VoiceChannel");
        _bgmSource = GetOrCreateChannelSource("BGMChannel");
        _bgm2Source = GetOrCreateChannelSource("BGM2Channel");

        _voiceSource.playOnAwake = false;
        _bgmSource.playOnAwake = false;
        _bgm2Source.playOnAwake = false;

        _voiceSource.volume = 1f;
        _bgmSource.volume = 1f;
        _bgm2Source.volume = postVoiceBgmVolume;

        _bgm2Source.loop = true;

        _voiceAmplifier = GetOrAddAmplifier(_voiceSource);
        _bgmAmplifier = GetOrAddAmplifier(_bgmSource);
        _bgm2Amplifier = GetOrAddAmplifier(_bgm2Source);

        ApplyAmplifier(_voiceAmplifier);
        ApplyAmplifier(_bgmAmplifier);
        ApplyAmplifier(_bgm2Amplifier);
    }

    private AudioSource GetOrCreateChannelSource(string childName)
    {
        Transform existing = transform.Find(childName);
        if (existing != null)
        {
            var s = existing.GetComponent<AudioSource>();
            if (s != null) return s;
        }
        var go = new GameObject(childName);
        go.transform.SetParent(transform);
        return go.AddComponent<AudioSource>();
    }

    private AudioAmplifier GetOrAddAmplifier(AudioSource src)
    {
        var existing = src.GetComponent<AudioAmplifier>();
        if (existing != null) return existing;
        return src.gameObject.AddComponent<AudioAmplifier>();
    }

    private void ApplyAmplifier(AudioAmplifier amp)
    {
        if (amp == null) return;
        amp.multiplier = amplifyMultiplier;
    }

    // ----------------------------------------------------------------------
    // BGM2 helpers
    // ----------------------------------------------------------------------

    public void StartPostVoiceBgm()
    {
        if (_bgm2Source == null) return;
        if (postVoiceBgm == null) return;

        _bgm2Source.clip = postVoiceBgm;
        _bgm2Source.loop = true;
        _bgm2Source.volume = postVoiceBgmVolume;
        _bgm2Source.Play();
    }

    private void StopPostVoiceBgm()
    {
        if (_bgm2Source == null) return;
        _bgm2Source.Stop();
        _bgm2Source.clip = null;
    }

    // ----------------------------------------------------------------------
    // Node registration
    // ----------------------------------------------------------------------

    public float ResumeGraceSeconds => resumeGraceSeconds;

    public void RegisterNode(ARTrackedPageNode node)
    {
        if (node == null) return;
        _nodes.Add(node);
    }

    public void UnregisterNode(ARTrackedPageNode node)
    {
        if (node == null) return;
        _nodes.Remove(node);

        if (_activeNode == node)
        {
            StopAllAudio();
            _activeNode = null;
            HideReplay();
        }
    }

    // ----------------------------------------------------------------------
    // Tracking events
    // ----------------------------------------------------------------------

    public void NotifyTrackingFound(ARTrackedPageNode node)
    {
        if (node == null) return;

        bool isSamePage = ActivePageId != null && node.PageId == ActivePageId;
        bool isSameNode = _activeNode == node;

        if (!isSamePage && _activeNode != null)
        {
            // Genuinely different page -- stop everything cleanly
            _activeNode.OnBecameInactiveByManager();
            StopAllAudio();
            HideReplay();
            _lastLostTime = -999f;
        }

        _activeNode = node;
        ActivePageId = node.PageId;

        // Grace time: same page found again within grace period
        bool canResume = isSamePage &&
                         _lastLostTime > 0f &&
                         (Time.time - _lastLostTime) <= resumeGraceSeconds;

        Debug.Log($"[AR] TrackingFound pageId:'{node.PageId}' isSamePage:{isSamePage} canResume:{canResume} timeSinceLost:{Time.time - _lastLostTime:F1}s");

        // If a middle-story activity is active, tracking found must not restart or resume story.
        // It should only restore the page root and keep story visuals/audio frozen until the activity finishes.
        if (node.IsStoryBlockedByActivity)
        {
            Debug.Log($"[AR-AUDIO-SKIP] pageId:'{node.PageId}' — blocked by an activity/quiz gate, audio not (re)started.");
            _lastLostTime = -999f;
            HideReplay();
            node.ResumeVisuals();
            return;
        }

        if (!canResume)
        {
            _lastLostTime = -999f;
            HideReplay();
            StopAllAudio(); // always stop old audio before restarting
            StartNodeFromBeginningThenAudio(node);
        }
        else
        {
            _lastLostTime = -999f;
            HideReplay();
            ResumeAll();
            node.ResumeVisuals();
        }
    }

    public void NotifyTrackingLost(ARTrackedPageNode node)
    {
        if (node == null) return;
        if (_activeNode != node) return;

        // Record lost time -- survives Addressables node recreation
        _lastLostTime = Time.time;

        PauseAll();
        node.PauseVisuals();
        // Note: Lost tracking overlay is now shown by CustomARHandler directly
        // This avoids double-showing the overlay
    }

    // Called by CustomARHandler after grace time expires and content is released
    // Resets grace timer so next scan of same page starts fresh, not resume
    public void NotifyContentReleased()
    {
        _lastLostTime = -999f;
        _startRequestId++;
        // Keep ActivePageId so isSamePage check still works correctly
        // but canResume will be false because _lastLostTime is reset
    }

    // ----------------------------------------------------------------------
    // Replay and language
    // ----------------------------------------------------------------------

    private void OnReplayPressed()
    {
        ReplayActivePage();
    }

    public void ReplayActivePage()
    {
        if (_activeNode == null) return;

        // Protect against the same Unity button being wired to both ARMediaManager
        // and CustomARHandler. Without this guard, one click can start two replays.
        if (_lastReplayFrame == Time.frameCount) return;
        _lastReplayFrame = Time.frameCount;

        ARTrackedPageNode replayNode = _activeNode;

        HideReplay();
        StopAllAudio();

        OnPageRestarted?.Invoke(replayNode.PageId);

        StartNodeFromBeginningThenAudio(replayNode);
    }

    private void StartNodeFromBeginningThenAudio(ARTrackedPageNode node)
    {
        if (node == null) return;

        int requestId = ++_startRequestId;
        ARTrackedPageNode requestedNode = node;

        requestedNode.StartFromBeginning(() =>
        {
            if (requestId != _startRequestId)
            {
                Debug.Log($"[AR-AUDIO-SKIP] pageId:'{requestedNode.PageId}' — a newer request superseded this one, audio not started.");
                return;
            }
            if (_activeNode != requestedNode)
            {
                Debug.Log($"[AR-AUDIO-SKIP] pageId:'{requestedNode.PageId}' — a different page became active first, audio not started.");
                return;
            }
            if (!requestedNode.IsTracked)
            {
                Debug.Log($"[AR-AUDIO-SKIP] pageId:'{requestedNode.PageId}' — tracking was lost before audio could start.");
                return;
            }
            if (!requestedNode.gameObject.activeInHierarchy)
            {
                Debug.Log($"[AR-AUDIO-SKIP] pageId:'{requestedNode.PageId}' — page object is inactive, audio not started.");
                return;
            }
            if (requestedNode.IsStoryBlockedByActivity)
            {
                Debug.Log($"[AR-AUDIO-SKIP] pageId:'{requestedNode.PageId}' — blocked by an activity/quiz gate, audio not started.");
                return;
            }

            // Pass the SAME requestId so NotifyContentReleased firing during
            // the audio download correctly suppresses the stale callback.
            PlayPageAudioFromBeginning(
                requestedNode.PageId,
                requestedNode.LoopBgmUntilVoiceEnds,
                requestedNode.StopBgmWhenVoiceEnds,
                requestId
            );
        });
    }

    private void OnLanguageChanged(string newLanguage)
    {
        Debug.Log($"[AR-LANG] Language changed → {newLanguage}");

        if (_activeNode == null || !_activeNode.IsTracked || !_activeNode.gameObject.activeInHierarchy)
        {
            Debug.Log("[AR-LANG] No active tracked node — language saved, will apply on next scan.");
            return;
        }

        HideReplay();
        StopAllAudio();

        string pageId = _activeNode.PageId;

        if (audioService != null)
        {
            // Load new language audio pack first, THEN restart the full page.
            // This avoids visuals playing in silence while audio downloads.
            int switchId = ++_langSwitchId;
            Debug.Log($"[AR-AUDIO] Loading audio pack: audio/{newLanguage}/{pageId}");

            audioService.LoadAudioPack(newLanguage, pageId, switchId, () => _langSwitchId, (pack, success) =>
            {
                if (switchId != _langSwitchId) return;
                if (_activeNode == null || !_activeNode.IsTracked) return;

                Debug.Log($"[AR-LANG] Audio ready — restarting page from zero: {newLanguage}/{pageId}");

                ARTrackedPageNode node = _activeNode;
                StopAllAudio();
                OnPageRestarted?.Invoke(node.PageId);
                StartNodeFromBeginningThenAudio(node);
            });
        }
        else
        {
            Debug.LogError("[AR-LANG] ARAddressableAudioService not assigned — language switch has no audio effect.");
        }
    }

    // ----------------------------------------------------------------------
    // Replay button UI
    // ----------------------------------------------------------------------

    private void HideReplay()
    {
        if (replayButton != null)
            replayButton.gameObject.SetActive(false);
    }

    private void ShowReplayIfActiveAndTracked()
    {
        if (replayButton == null) return;
        if (_activeNode == null) return;
        if (!_activeNode.IsTracked) return;
        replayButton.gameObject.SetActive(true);
    }

    // ----------------------------------------------------------------------
    // Pause / Resume / Stop
    // ----------------------------------------------------------------------

    private void ShowPointCamera(bool show)
    {
        // Handled by OverlayManager -- set up ONCE in scene, works for all pages
        if (OverlayManager.Instance != null)
        {
            if (show) OverlayManager.Instance.ShowLostTracking();
            else OverlayManager.Instance.HideLostTracking();
        }
    }

    private void PauseAll()
    {
        _paused = true;
        if (_voiceSource != null && _voiceSource.isPlaying) _voiceSource.Pause();
        if (_bgmSource != null && _bgmSource.isPlaying) _bgmSource.Pause();
        if (_bgm2Source != null && _bgm2Source.isPlaying) _bgm2Source.Pause();
    }

    private void ResumeAll()
    {
        _paused = false;
        if (_voiceSource != null && _voiceSource.clip != null) _voiceSource.UnPause();
        if (_bgmSource != null && _bgmSource.clip != null) _bgmSource.UnPause();
        if (_bgm2Source != null && _bgm2Source.clip != null) _bgm2Source.UnPause();
    }

    private void StopAllAudio()
    {
        _paused = false;

        if (_voiceRoutine != null) { StopCoroutine(_voiceRoutine); _voiceRoutine = null; }
        if (_bgmDelayRoutine != null) { StopCoroutine(_bgmDelayRoutine); _bgmDelayRoutine = null; }

        _stage = VoiceStage.None;
        _voiceIndex = 0;
        _delayTimer = 0f;

        if (_voiceSource != null) { _voiceSource.Stop(); _voiceSource.clip = null; }
        if (_bgmSource != null) { _bgmSource.Stop(); _bgmSource.clip = null; }

        StopPostVoiceBgm();
    }

    // ----------------------------------------------------------------------
    // Audio playback
    // ----------------------------------------------------------------------

    // reqId must be the SAME id already incremented by StartNodeFromBeginningThenAudio.
    // Passing it here ensures NotifyContentReleased() correctly invalidates a download
    // that started AFTER visuals began but before audio arrived from CCD.
    private void PlayPageAudioFromBeginning(string pageId, bool loopBgmRequested, bool stopBgmWhenVoiceEnds, int reqId)
    {
        _paused = false;

        if (audioService == null)
        {
            Debug.LogError("[AR-AUDIO] ARAddressableAudioService not assigned in Inspector — no audio will play.");
            return;
        }

        string lang = ARGlobalLanguage.GetCurrentLanguage();
        Debug.Log($"[AR-AUDIO] Loading pack: {lang}/{pageId} (reqId={reqId})");

        bool handled = audioService.LoadAudioPack(lang, pageId, reqId, () => _startRequestId, (pack, success) =>
        {
            if (pack != null)
            {
                PlayPageAudioFromPack(pack, loopBgmRequested, stopBgmWhenVoiceEnds);
            }
            else
                Debug.LogWarning($"[AR-AUDIO] No audio pack for {lang}/{pageId} — page plays in silence.");
        });

        if (!handled)
            Debug.LogWarning($"[AR-AUDIO] No catalog entry for {lang}/{pageId} — page plays in silence.");
    }

    private IEnumerator DelayedPlay(AudioSource src, float delay)
    {
        if (src == null) yield break;
        float t = delay;
        while (t > 0f) { t -= Time.deltaTime; yield return null; }
        if (src != null && src.clip != null) src.Play();
    }

    // ----------------------------------------------------------------------
    // Addressable audio pack playback
    // ----------------------------------------------------------------------

    private void PlayPageAudioFromPack(ARPageAudioPack pack, bool loopBgmRequested, bool stopBgmWhenVoiceEnds)
    {
        if (pack == null) return;
        _paused = false;

        StartBgmFromPack(pack, loopBgmRequested);

        _voiceIndex = 0;
        _stage = VoiceStage.DelayBefore;
        _delayTimer = 0f;

        if (_voiceRoutine != null) StopCoroutine(_voiceRoutine);
        if (!isActiveAndEnabled || !gameObject.activeInHierarchy) return;
        _voiceRoutine = StartCoroutine(VoiceSequenceRoutineFromPack(pack, stopBgmWhenVoiceEnds));
    }

    private void StartBgmFromPack(ARPageAudioPack pack, bool loopBgmRequested)
    {
        if (_bgmSource == null || pack == null || pack.bgmClips == null || pack.bgmClips.Count == 0) return;

        var seg = pack.bgmClips[0];
        if (seg == null || seg.clip == null) return;

        _bgmSource.clip = seg.clip;
        _bgmSource.loop = loopBgmRequested || seg.loop;
        _bgmSource.volume = Mathf.Clamp01(seg.volume);

        if (seg.delayBefore > 0f)
            _bgmDelayRoutine = StartCoroutine(DelayedPlay(_bgmSource, seg.delayBefore));
        else
            _bgmSource.Play();
    }

    private IEnumerator VoiceSequenceRoutineFromPack(ARPageAudioPack pack, bool stopBgmWhenVoiceEnds)
    {
        if (_voiceSource == null || pack == null || pack.voiceClips == null) yield break;

        bool anyClipPlayed = false;

        while (_voiceIndex < pack.voiceClips.Count)
        {
            var seg = pack.voiceClips[_voiceIndex];

            Debug.Log($"[AR] Voice Clip Index {_voiceIndex} → {seg?.clip}");

            if (seg == null || seg.clip == null)
            {
                Debug.LogWarning($"[AR] Skipping NULL audio at index {_voiceIndex}");
                _voiceIndex++;
                continue;
            }

            _stage = VoiceStage.DelayBefore;
            _delayTimer = seg.delayBefore;
            while (_delayTimer > 0f) { if (!_paused) _delayTimer -= Time.deltaTime; yield return null; }

            // A looping clip anywhere but the LAST slot would play forever and never
            // advance to the next clip in the sequence -- silently breaking multi-clip
            // pages (e.g. "57-1" then "57-2"). Only the final segment is allowed to loop.
            bool isLastSegment = _voiceIndex == pack.voiceClips.Count - 1;
            bool effectiveLoop = seg.loop && isLastSegment;
            if (seg.loop && !isLastSegment)
                Debug.LogWarning($"[AR] Voice clip at index {_voiceIndex} has Loop enabled but isn't the last " +
                                  "clip in this page's sequence -- ignoring loop here so the next clip can still play.");

            _stage = VoiceStage.Playing;
            _voiceSource.clip = seg.clip;
            _voiceSource.loop = effectiveLoop;
            _voiceSource.volume = Mathf.Clamp01(seg.volume);
            _voiceSource.Play();
            anyClipPlayed = true;

            while (_voiceSource != null && _voiceSource.clip != null)
            {
                if (_paused) { yield return null; continue; }
                if (!effectiveLoop && !_voiceSource.isPlaying) break;
                yield return null;
            }

            _stage = VoiceStage.DelayAfter;
            _delayTimer = seg.delayAfter;
            while (_delayTimer > 0f) { if (!_paused) _delayTimer -= Time.deltaTime; yield return null; }

            _voiceIndex++;
        }

        _stage = VoiceStage.None;
        _voiceRoutine = null;

        if (stopBgmWhenVoiceEnds && _bgmSource != null)
        {
            _bgmSource.Stop();
            _bgmSource.clip = null;
        }

        ShowReplayIfActiveAndTracked();
        StartPostVoiceBgm();

        if (anyClipPlayed && _activeNode != null)
        {
            Debug.Log($"[AR] Voice completed (pack) for page: '{_activeNode.PageId}'");
            OnVoiceCompleted?.Invoke(_activeNode.PageId);
        }
    }
}