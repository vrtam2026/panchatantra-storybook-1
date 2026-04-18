using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class LoadingScreen : MonoBehaviour
{
    public static LoadingScreen Instance { get; private set; }

    [System.Serializable]
    public class CharacterGroup
    {
        [Tooltip("Drag 2-3 sprites for this character in order.")]
        public List<Sprite> frames = new();
    }

    [Header("Character Groups")]
    [Tooltip("Add one character per slot. Each has 2-3 frames in order.")]
    public List<CharacterGroup> characterGroups = new();

    [Header("Animation")]
    [Tooltip("ON = frames keep looping. OFF = plays once and stops.")]
    public bool loopAnimation = true;

    [Tooltip("ON = shows one random frame only. OFF = plays all frames in sequence.")]
    public bool randomFrame = false;

    [Header("Timing")]
    public float frameSpeed = 1.5f;
    public float fadeInDuration = 0.4f;
    public float fadeOutDuration = 0.4f;

    [Header("SFX (Optional)")]
    public AudioClip appearSfx;

    private Image _characterImage;
    private CanvasGroup _canvasGroup;
    private AudioSource _audioSource;
    private Coroutine _fadeRoutine;
    private Coroutine _cycleRoutine;
    private bool _isShowing = false;

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;

        _characterImage = GetComponentInChildren<Image>();
        _canvasGroup = GetComponent<CanvasGroup>();
        if (_canvasGroup == null)
            _canvasGroup = gameObject.AddComponent<CanvasGroup>();

        _audioSource = GetComponent<AudioSource>();
        if (_audioSource == null)
            _audioSource = gameObject.AddComponent<AudioSource>();

        _canvasGroup.alpha = 0f;
        _canvasGroup.blocksRaycasts = false;
        gameObject.SetActive(false);
    }

    public static void Show()
    {
        if (Instance == null) return;
        Instance.ShowInternal();
    }

    public static void Hide()
    {
        if (Instance == null) return;
        Instance.HideInternal();
    }

    private void ShowInternal()
    {
        if (_isShowing) return;
        _isShowing = true;
        StopAll();
        gameObject.SetActive(true);
        _canvasGroup.blocksRaycasts = true;
        if (appearSfx != null) _audioSource.PlayOneShot(appearSfx);
        _fadeRoutine = StartCoroutine(FadeIn());
        _cycleRoutine = StartCoroutine(CycleFrames());
    }

    private void HideInternal()
    {
        if (!_isShowing) return;
        StopAll();
        _fadeRoutine = StartCoroutine(FadeOutAndDisable());
    }

    private void StopAll()
    {
        if (_fadeRoutine != null) { StopCoroutine(_fadeRoutine); _fadeRoutine = null; }
        if (_cycleRoutine != null) { StopCoroutine(_cycleRoutine); _cycleRoutine = null; }
    }

    private IEnumerator FadeIn()
    {
        float elapsed = 0f;
        _canvasGroup.alpha = 0f;
        while (elapsed < fadeInDuration)
        {
            elapsed += Time.deltaTime;
            _canvasGroup.alpha = Mathf.Clamp01(elapsed / fadeInDuration);
            yield return null;
        }
        _canvasGroup.alpha = 1f;
    }

    private IEnumerator FadeOutAndDisable()
    {
        float elapsed = 0f;
        float startAlpha = _canvasGroup.alpha;
        while (elapsed < fadeOutDuration)
        {
            elapsed += Time.deltaTime;
            _canvasGroup.alpha = Mathf.Lerp(startAlpha, 0f, elapsed / fadeOutDuration);
            yield return null;
        }
        _canvasGroup.alpha = 0f;
        _canvasGroup.blocksRaycasts = false;
        gameObject.SetActive(false);
        _isShowing = false;
    }

    private IEnumerator CycleFrames()
    {
        if (_characterImage == null) yield break;
        if (characterGroups == null || characterGroups.Count == 0) yield break;

        int groupIndex = Random.Range(0, characterGroups.Count);
        CharacterGroup group = characterGroups[groupIndex];
        if (group == null || group.frames == null || group.frames.Count == 0) yield break;

        // Pick one random frame only -- no cycling
        if (randomFrame)
        {
            int randFrame = Random.Range(0, group.frames.Count);
            if (group.frames[randFrame] != null)
                _characterImage.sprite = group.frames[randFrame];
            yield break;
        }

        // Cycle frames in sequence
        int frameIndex = 0;
        while (true)
        {
            if (group.frames[frameIndex] != null)
                _characterImage.sprite = group.frames[frameIndex];

            yield return new WaitForSeconds(frameSpeed);
            frameIndex++;

            if (frameIndex >= group.frames.Count)
            {
                if (!loopAnimation) yield break;
                frameIndex = 0;
            }
        }
    }
}