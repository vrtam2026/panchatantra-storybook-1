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
    [SerializeField] private ARAudioLocalizationDatabase audioDatabase;

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
    [SerializeField, Min(0f)] private float resumeGraceSeconds = 2f;
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
    private bool _paused;
    private int _voiceIndex;
    private float _delayTimer;

    private enum VoiceStage { None, DelayBefore, Playing, DelayAfter }
    private VoiceStage _stage = VoiceStage.None;

    // Fires when voice audio fully completes — carries pageId
    public static event System.Action<string> OnVoiceCompleted;

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

        // Hide point camera overlay immediately
        ShowPointCamera(false);

        if (_activeNode != null && _activeNode != node)
        {
            _activeNode.OnBecameInactiveByManager();
            StopAllAudio();
            HideReplay();
        }

        _activeNode = node;
        bool canResume = node.CanResume(resumeGraceSeconds);

        if (!canResume)
        {
            HideReplay();
            node.StartFromBeginning();
            PlayPageAudioFromBeginning(node.PageId, node.LoopBgmUntilVoiceEnds, node.StopBgmWhenVoiceEnds);
        }
        else
        {
            HideReplay();
            ResumeAll();
            node.ResumeVisuals();
        }
    }

    public void NotifyTrackingLost(ARTrackedPageNode node)
    {
        if (node == null) return;
        if (_activeNode != node) return;

        PauseAll();
        node.PauseVisuals();

        // Show point camera overlay instantly
        ShowPointCamera(true);
    }

    // ----------------------------------------------------------------------
    // Replay and language
    // ----------------------------------------------------------------------

    private void OnReplayPressed()
    {
        if (_activeNode == null) return;
        HideReplay();
        StopAllAudio();
        _activeNode.StartFromBeginning();
        PlayPageAudioFromBeginning(_activeNode.PageId, _activeNode.LoopBgmUntilVoiceEnds, _activeNode.StopBgmWhenVoiceEnds);
    }

    private void OnLanguageChanged(string newLanguage)
    {
        if (_activeNode == null) return;
        if (!_activeNode.IsTracked) return;
        HideReplay();
        StopAllAudio();
        PlayPageAudioFromBeginning(_activeNode.PageId, _activeNode.LoopBgmUntilVoiceEnds, _activeNode.StopBgmWhenVoiceEnds);
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
            if (show) OverlayManager.Instance.ShowLostTrackingPanel();
            else OverlayManager.Instance.HideLostTrackingPanel();
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

    private void PlayPageAudioFromBeginning(string pageId, bool loopBgmRequested, bool stopBgmWhenVoiceEnds)
    {
        Debug.Log($"[AR] PlayPageAudio — pageId: '{pageId}', lang: '{ARGlobalLanguage.GetCurrentLanguage()}'");

        if (audioDatabase == null)
        {
            Debug.LogError("[AR] audioDatabase is NULL — assign it in Inspector");
            return;
        }

        string lang = ARGlobalLanguage.GetCurrentLanguage();

        if (!audioDatabase.TryGetPageAudio(lang, pageId, out var pageAudio) || pageAudio == null)
        {
            Debug.LogError($"[AR] No audio found for lang:'{lang}' pageId:'{pageId}'");
            return;
        }

        Debug.Log($"[AR] Found pageAudio — voiceClips:{pageAudio.voiceClips.Count}, bgmClips:{pageAudio.bgmClips.Count}");

        StartBgm(pageAudio, loopBgmRequested);

        _voiceIndex = 0;
        _stage = VoiceStage.DelayBefore;
        _delayTimer = 0f;

        if (_voiceRoutine != null) StopCoroutine(_voiceRoutine);
        _voiceRoutine = StartCoroutine(VoiceSequenceRoutine(pageAudio, stopBgmWhenVoiceEnds));
    }

    private void StartBgm(ARAudioLocalizationDatabase.PageAudio pageAudio, bool loopBgmRequested)
    {
        if (_bgmSource == null) return;
        if (pageAudio == null || pageAudio.bgmClips == null || pageAudio.bgmClips.Count == 0) return;

        var seg = pageAudio.bgmClips[0];
        if (seg == null || seg.clip == null) return;

        _bgmSource.clip = seg.clip;
        _bgmSource.loop = loopBgmRequested || seg.loop;
        _bgmSource.volume = 1f;

        if (seg.delayBefore > 0f)
            StartCoroutine(DelayedPlay(_bgmSource, seg.delayBefore));
        else
            _bgmSource.Play();
    }

    private IEnumerator DelayedPlay(AudioSource src, float delay)
    {
        if (src == null) yield break;
        float t = delay;
        while (t > 0f) { t -= Time.deltaTime; yield return null; }
        if (src != null && src.clip != null) src.Play();
    }

    // ----------------------------------------------------------------------
    // Voice sequence
    // ----------------------------------------------------------------------

    private IEnumerator VoiceSequenceRoutine(ARAudioLocalizationDatabase.PageAudio pageAudio, bool stopBgmWhenVoiceEnds)
    {
        if (_voiceSource == null) yield break;
        if (pageAudio == null || pageAudio.voiceClips == null) yield break;

        bool anyClipPlayed = false;

        while (_voiceIndex < pageAudio.voiceClips.Count)
        {
            var seg = pageAudio.voiceClips[_voiceIndex];

            Debug.Log($"[AR] Voice Clip Index {_voiceIndex} → {seg?.clip}");

            if (seg == null || seg.clip == null)
            {
                Debug.LogWarning($"[AR] Skipping NULL audio at index {_voiceIndex}");
                _voiceIndex++;
                continue;
            }

            _stage = VoiceStage.DelayBefore;
            _delayTimer = seg.delayBefore;
            while (_delayTimer > 0f)
            {
                if (!_paused) _delayTimer -= Time.deltaTime;
                yield return null;
            }

            _stage = VoiceStage.Playing;
            _voiceSource.clip = seg.clip;
            _voiceSource.loop = seg.loop;
            _voiceSource.volume = 1f;
            _voiceSource.Play();

            anyClipPlayed = true;

            while (_voiceSource != null && _voiceSource.clip != null)
            {
                if (_paused) { yield return null; continue; }
                if (!seg.loop && !_voiceSource.isPlaying) break;
                yield return null;
            }

            _stage = VoiceStage.DelayAfter;
            _delayTimer = seg.delayAfter;
            while (_delayTimer > 0f)
            {
                if (!_paused) _delayTimer -= Time.deltaTime;
                yield return null;
            }

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

        if (anyClipPlayed && _activeNode != null)
        {
            Debug.Log($"[AR] Voice completed for page: '{_activeNode.PageId}'");
            OnVoiceCompleted?.Invoke(_activeNode.PageId);
        }
    }
}