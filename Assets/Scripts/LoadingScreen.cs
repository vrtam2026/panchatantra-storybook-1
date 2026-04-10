using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class LoadingScreen : MonoBehaviour
{
    public static LoadingScreen Instance { get; private set; }

    [Header("Sprites")]
    [Tooltip("Drag all your loading sprites here. A random one is picked each time.")]
    public List<Sprite> sprites = new();

    [Header("Timing")]
    public float frameDuration = 1.5f;
    public float fadeInDuration = 0.4f;
    public float fadeOutDuration = 0.4f;

    [Header("SFX (Optional)")]
    public AudioClip appearSfx;

    // Found automatically at runtime — no need to assign in Inspector
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

        // Auto-find components on this GameObject
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

        if (appearSfx != null)
            _audioSource.PlayOneShot(appearSfx);

        _fadeRoutine = StartCoroutine(FadeIn());
        _cycleRoutine = StartCoroutine(CycleSprites());
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

    private IEnumerator CycleSprites()
    {
        if (_characterImage == null || sprites == null || sprites.Count == 0) yield break;

        int index = Random.Range(0, sprites.Count);

        while (true)
        {
            if (sprites[index] != null)
                _characterImage.sprite = sprites[index];

            yield return new WaitForSeconds(frameDuration);
            index = (index + 1) % sprites.Count;
        }
    }
}