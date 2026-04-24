using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

// ─────────────────────────────────────────────────────────────────────────────
// QuizLoadingScreen.cs
// Attach to: QuizLoadingScreen GameObject (separate from story LoadingScreen)
// Canvas: Screen Space Overlay, Sort Order 20
// Children needed: LoadingImage (Image), LoadingText (TMP optional)
// ─────────────────────────────────────────────────────────────────────────────

public class QuizLoadingScreen : MonoBehaviour
{
    public static QuizLoadingScreen Instance { get; private set; }

    [System.Serializable]
    public class CharacterGroup
    {
        [Tooltip("Drag 2-4 sprites for this character.")]
        public List<Sprite> frames = new();
    }

    [Header("Character Groups")]
    [Tooltip("Add 2-5 characters. Each character has 2-4 frame images. " +
             "On show: picks a random character, then cycles its frames.")]
    public List<CharacterGroup> characterGroups = new();

    [Header("Animation")]
    [Tooltip("ON = frames keep looping until download finishes.")]
    public bool loopAnimation = true;

    [Header("Timing")]
    public float frameSpeed = 1.5f;
    public float fadeInDuration = 0.3f;
    public float fadeOutDuration = 0.3f;

    [Tooltip("Loading screen always stays visible for at least this long even if download is instant.")]
    public float minimumShowSeconds = 2f;

    [Header("UI References")]
    [Tooltip("Image that shows the character animation.")]
    [SerializeField] private Image characterImage;

    [Tooltip("Optional text label. Leave empty if not used.")]
    [SerializeField] private TextMeshProUGUI loadingText;

    [Header("SFX (Optional)")]
    public AudioClip appearSfx;

    // ── Internal ──────────────────────────────────────────────────────────────

    private CanvasGroup _canvasGroup;
    private AudioSource _audioSource;
    private Coroutine _fadeRoutine;
    private Coroutine _cycleRoutine;
    private Coroutine _minShowRoutine;
    private bool _isShowing = false;
    private float _showStartTime = 0f;

    public bool IsShowing => _isShowing;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;

        if (characterImage == null)
            characterImage = GetComponentInChildren<Image>();

        _canvasGroup = GetComponent<CanvasGroup>();
        if (_canvasGroup == null)
            _canvasGroup = gameObject.AddComponent<CanvasGroup>();

        _audioSource = GetComponent<AudioSource>();
        if (_audioSource == null)
            _audioSource = gameObject.AddComponent<AudioSource>();

        // Always stays active -- visibility via CanvasGroup only
        _canvasGroup.alpha = 0f;
        _canvasGroup.blocksRaycasts = false;
        _canvasGroup.interactable = false;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    private static void EnsureInstance()
    {
        if (Instance != null) return;

#if UNITY_2023_1_OR_NEWER
        var found = Object.FindFirstObjectByType<QuizLoadingScreen>(FindObjectsInactive.Include);
#else
        var found = Object.FindObjectOfType<QuizLoadingScreen>(true);
#endif
        if (found != null)
            found.gameObject.SetActive(true);
        else
            Debug.LogError("[QuizLoadingScreen] No QuizLoadingScreen found in scene.");
    }

    public static void Show()
    {
        EnsureInstance();
        if (Instance == null) return;
        Instance.ShowInternal();
    }

    public static void Hide()
    {
        if (Instance == null) return;
        Instance.HideInternal();
    }

    // ── Show / Hide ───────────────────────────────────────────────────────────

    private void ShowInternal()
    {
        if (_isShowing) return;
        _isShowing = true;
        _showStartTime = Time.time;

        StopAll();

        _canvasGroup.blocksRaycasts = true;
        _canvasGroup.interactable = true;

        if (loadingText != null)
            loadingText.text = "Quiz is loading...";

        if (appearSfx != null && _audioSource != null)
            _audioSource.PlayOneShot(appearSfx);

        _fadeRoutine = StartCoroutine(FadeIn());
        _cycleRoutine = StartCoroutine(CycleFrames());
    }

    private void HideInternal()
    {
        if (!_isShowing)
        {
            _canvasGroup.alpha = 0f;
            _canvasGroup.blocksRaycasts = false;
            _canvasGroup.interactable = false;
            return;
        }

        float elapsed = Time.time - _showStartTime;
        float remaining = minimumShowSeconds - elapsed;

        if (remaining > 0f)
        {
            if (_minShowRoutine != null) StopCoroutine(_minShowRoutine);
            _minShowRoutine = StartCoroutine(HideAfterDelay(remaining));
            return;
        }

        StopAll();
        _fadeRoutine = StartCoroutine(FadeOutAndHide());
    }

    private IEnumerator HideAfterDelay(float delay)
    {
        yield return new WaitForSeconds(delay);
        _minShowRoutine = null;
        StopAll();
        _fadeRoutine = StartCoroutine(FadeOutAndHide());
    }

    private void StopAll()
    {
        if (_minShowRoutine != null) { StopCoroutine(_minShowRoutine); _minShowRoutine = null; }
        if (_fadeRoutine != null) { StopCoroutine(_fadeRoutine); _fadeRoutine = null; }
        if (_cycleRoutine != null) { StopCoroutine(_cycleRoutine); _cycleRoutine = null; }
    }

    // ── Fade coroutines ───────────────────────────────────────────────────────

    private IEnumerator FadeIn()
    {
        _canvasGroup.alpha = 0f;
        float elapsed = 0f;
        while (elapsed < fadeInDuration)
        {
            elapsed += Time.deltaTime;
            _canvasGroup.alpha = Mathf.Clamp01(elapsed / fadeInDuration);
            yield return null;
        }
        _canvasGroup.alpha = 1f;
        _fadeRoutine = null;
    }

    private IEnumerator FadeOutAndHide()
    {
        float startAlpha = _canvasGroup.alpha;
        float elapsed = 0f;
        while (elapsed < fadeOutDuration)
        {
            elapsed += Time.deltaTime;
            _canvasGroup.alpha = Mathf.Lerp(startAlpha, 0f, elapsed / fadeOutDuration);
            yield return null;
        }
        _canvasGroup.alpha = 0f;
        _canvasGroup.blocksRaycasts = false;
        _canvasGroup.interactable = false;
        _isShowing = false;
        _fadeRoutine = null;
    }

    // ── Frame cycling ─────────────────────────────────────────────────────────

    private IEnumerator CycleFrames()
    {
        if (characterImage == null) yield break;
        if (characterGroups == null || characterGroups.Count == 0) yield break;

        int groupIndex = Random.Range(0, characterGroups.Count);
        CharacterGroup group = characterGroups[groupIndex];

        if (group == null || group.frames == null || group.frames.Count == 0) yield break;

        int frameIndex = Random.Range(0, group.frames.Count);

        while (true)
        {
            if (group.frames[frameIndex] != null)
                characterImage.sprite = group.frames[frameIndex];

            yield return new WaitForSeconds(frameSpeed);

            frameIndex = (frameIndex + 1) % group.frames.Count;

            if (!loopAnimation && frameIndex == 0) yield break;
        }
    }
}