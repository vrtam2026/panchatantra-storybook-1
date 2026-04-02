using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class ARMediaManager : MonoBehaviour
{
    [Header("Global UI")]
    [SerializeField] private Button replayButton;

    [Header("Audio")]
    [SerializeField] private ARAudioLocalizationDatabase audioDatabase;

    [Tooltip("Master volume multiplier for ALL audio (Voice + BGM). Range 0 to 2. Default 1.")]
    [Range(0f, 2f)]
    [SerializeField] private float masterVolume = 1f;

    [Header("Behavior")]
    [SerializeField, Min(0f)] private float resumeGraceSeconds = 2f;
    [SerializeField] private string defaultLanguage = "English";

    private AudioSource _voiceSource;
    private AudioSource _bgmSource;

    private readonly HashSet<ARTrackedPageNode> _nodes = new HashSet<ARTrackedPageNode>();
    private ARTrackedPageNode _activeNode;

    private Coroutine _voiceRoutine;
    private bool _paused;

    private int _voiceIndex;
    private float _delayTimer;

    private enum VoiceStage { None, DelayBefore, Playing, DelayAfter }
    private VoiceStage _stage = VoiceStage.None;

    private void Awake()
    {
        EnsureAudioSources();

        if (!PlayerPrefs.HasKey(ARGlobalLanguage.PlayerPrefsKey))
        {
            string lang = string.IsNullOrWhiteSpace(defaultLanguage) ? "English" : defaultLanguage;
            ARGlobalLanguage.SetCurrentLanguage(lang);
        }

        if (replayButton != null)
        {
            replayButton.onClick.RemoveListener(OnReplayPressed);
            replayButton.onClick.AddListener(OnReplayPressed);

            // Hide by default
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

    private void EnsureAudioSources()
    {
        var sources = GetComponents<AudioSource>();
        if (sources.Length == 0)
        {
            _voiceSource = gameObject.AddComponent<AudioSource>();
            _bgmSource = gameObject.AddComponent<AudioSource>();
        }
        else if (sources.Length == 1)
        {
            _voiceSource = sources[0];
            _bgmSource = gameObject.AddComponent<AudioSource>();
        }
        else
        {
            _voiceSource = sources[0];
            _bgmSource = sources[1];
        }

        _voiceSource.playOnAwake = false;
        _bgmSource.playOnAwake = false;
    }

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

    public void NotifyTrackingFound(ARTrackedPageNode node)
    {
        if (node == null) return;

        // If switching pages, stop old one
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

        // Do NOT show replay on lost; it should show only after voice completes while page is active/tracked
    }

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
        // If language changes while tracked, restart only audio from beginning
        if (_activeNode == null) return;
        if (!_activeNode.IsTracked) return;

        HideReplay();
        StopAllAudio();
        PlayPageAudioFromBeginning(_activeNode.PageId, _activeNode.LoopBgmUntilVoiceEnds, _activeNode.StopBgmWhenVoiceEnds);
    }

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

    private void PauseAll()
    {
        _paused = true;

        if (_voiceSource != null && _voiceSource.isPlaying) _voiceSource.Pause();
        if (_bgmSource != null && _bgmSource.isPlaying) _bgmSource.Pause();
    }

    private void ResumeAll()
    {
        _paused = false;

        if (_voiceSource != null && _voiceSource.clip != null) _voiceSource.UnPause();
        if (_bgmSource != null && _bgmSource.clip != null) _bgmSource.UnPause();
    }

    private void StopAllAudio()
    {
        _paused = false;

        if (_voiceRoutine != null)
        {
            StopCoroutine(_voiceRoutine);
            _voiceRoutine = null;
        }

        _stage = VoiceStage.None;
        _voiceIndex = 0;
        _delayTimer = 0f;

        if (_voiceSource != null)
        {
            _voiceSource.Stop();
            _voiceSource.clip = null;
        }

        if (_bgmSource != null)
        {
            _bgmSource.Stop();
            _bgmSource.clip = null;
        }
    }

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

        // Start BGM (optional)
        StartBgm(pageAudio, loopBgmRequested);

        // Start voice sequence
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

        // If either requested OR segment itself says loop, loop it.
        _bgmSource.loop = loopBgmRequested || seg.loop;

        // Apply master volume (0..2)
        float v = Mathf.Clamp(seg.volume * masterVolume, 0f, 2f);
        _bgmSource.volume = v;

        if (seg.delayBefore > 0f)
            StartCoroutine(DelayedPlay(_bgmSource, seg.delayBefore));
        else
            _bgmSource.Play();
    }

    private IEnumerator DelayedPlay(AudioSource src, float delay)
    {
        if (src == null) yield break;

        float t = delay;
        while (t > 0f)
        {
            t -= Time.deltaTime;
            yield return null;
        }

        if (src != null && src.clip != null)
            src.Play();
    }

    private IEnumerator VoiceSequenceRoutine(ARAudioLocalizationDatabase.PageAudio pageAudio, bool stopBgmWhenVoiceEnds)
    {
        if (_voiceSource == null) yield break;
        if (pageAudio == null || pageAudio.voiceClips == null) yield break;

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

            // Delay before
            _stage = VoiceStage.DelayBefore;
            _delayTimer = seg.delayBefore;
            while (_delayTimer > 0f)
            {
                if (!_paused) _delayTimer -= Time.deltaTime;
                yield return null;
            }

            // Play voice
            _stage = VoiceStage.Playing;
            _voiceSource.clip = seg.clip;
            _voiceSource.loop = seg.loop;

            // Apply master volume (0..2)
            float v = Mathf.Clamp(seg.volume * masterVolume, 0f, 2f);
            _voiceSource.volume = v;

            _voiceSource.Play();

            // Wait until clip finishes (if loop, it never ends until stopped)
            while (_voiceSource != null && _voiceSource.clip != null)
            {
                if (_paused)
                {
                    yield return null;
                    continue;
                }

                if (!seg.loop && !_voiceSource.isPlaying)
                    break;

                yield return null;
            }

            // Delay after
            _stage = VoiceStage.DelayAfter;
            _delayTimer = seg.delayAfter;
            while (_delayTimer > 0f)
            {
                if (!_paused) _delayTimer -= Time.deltaTime;
                yield return null;
            }

            _voiceIndex++;
        }

        // Voice finished بالكامل
        _stage = VoiceStage.None;
        _voiceRoutine = null;

        // Stop BGM only if requested
        if (stopBgmWhenVoiceEnds && _bgmSource != null)
        {
            _bgmSource.Stop();
            _bgmSource.clip = null;
        }

        // Show Replay ONLY when voice is completed
        ShowReplayIfActiveAndTracked();
    }
}
