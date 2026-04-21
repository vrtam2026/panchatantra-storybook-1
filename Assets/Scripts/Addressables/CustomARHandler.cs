using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.UI;
using UnityEngine.Video;
using Vuforia;

public class CustomARHandler : MonoBehaviour
{
    public string addressableKey;
    private GameObject instantiatedObject;
    private IARContent contentControl;
    private ModelInteraction modelInteraction;
    private QuizManager quizManager;

    private bool _isLoading = false;
    private bool _loadCancelled = false;
    private bool _contentCompleted = false;

    private Coroutine _releaseCoroutine;
    private string _activePageId;

    // Cached -- avoids FindFirstObjectByType on every tracking event
    private ARMediaManager _arMediaManager;

    [Header("UI Elements")]
    public GameObject replayButton;
    public GameObject nextPageImg;
    public GameObject backBtn;
    public GameObject sliderV;
    [Tooltip("Resets slider value and 3D model position/rotation only. Does not reload content.")]
    public GameObject resetButton;

    [Header("Auto Hide Settings")]
    [Tooltip("Seconds before UI auto hides after last interaction.")]
    public float autoHideSeconds = 5f;

    [Tooltip("How fast UI fades in and out in seconds.")]
    public float fadeDuration = 0.3f;

    private bool _uiVisible = false;
    private float _autoHideTimer = 0f;
    private float _uiShownAt = 0f;
    private const float MinUiToggleOffDelay = 0.8f;

    // Tracks whether the current touch/drag started on a UI element.
    // While touch is held on UI, auto-hide timer is frozen.
    // Prevents slider from stopping mid-drag when timer expires.
    private bool _touchHeldOnUI = false;

    private CanvasGroup _replayCG;
    private CanvasGroup _backBtnCG;
    private CanvasGroup _sliderCG;
    private CanvasGroup _resetCG;
    private Coroutine _fadeRoutine;

    private Coroutine _nextPageAnimRoutine;
    private Vector2 _nextPageImgOriginalPos;

    private VuforiaTrackHook _trackHook;
    private ARTrackedPageNode _pageNode;

    private readonly List<RaycastResult> _raycastResults = new List<RaycastResult>();

    public static CustomARHandler Current;

    // ----------------------------------------------------------------------
    // Unity lifecycle
    // ----------------------------------------------------------------------

    private void Awake()
    {
        if (nextPageImg != null)
            _nextPageImgOriginalPos = nextPageImg.GetComponent<RectTransform>().anchoredPosition;

        _replayCG = GetOrAddCanvasGroup(replayButton);
        _backBtnCG = GetOrAddCanvasGroup(backBtn);
        _sliderCG = GetOrAddCanvasGroup(sliderV);
        _resetCG = GetOrAddCanvasGroup(resetButton);

        if (resetButton != null)
        {
            var btn = resetButton.GetComponent<Button>();
            if (btn != null) btn.onClick.AddListener(OnResetButtonPressed);
        }

        // Cache on this GO immediately -- never null this out in release paths.
        // FadeUI reads canSliderRotate from this at any time.
        modelInteraction = GetComponent<ModelInteraction>();

        SetUIAlpha(0f);
        SetUIInteractable(false);
    }

    void Start()
    {
        HideAllUI();
        _trackHook = GetComponent<VuforiaTrackHook>();

        // Cache ARMediaManager once -- avoid FindFirstObjectByType on every event
        _arMediaManager = Object.FindFirstObjectByType<ARMediaManager>();

        var observer = GetComponent<ObserverBehaviour>();
        if (observer != null)
            observer.OnTargetStatusChanged += OnTargetStatusChanged;
    }

    private void OnEnable() { ARMediaManager.OnVoiceCompleted += OnVoiceCompleted; }
    private void OnDisable() { ARMediaManager.OnVoiceCompleted -= OnVoiceCompleted; }

    void OnDestroy()
    {
        var observer = GetComponent<ObserverBehaviour>();
        if (observer != null)
            observer.OnTargetStatusChanged -= OnTargetStatusChanged;
    }

    // ----------------------------------------------------------------------
    // Update -- tap detection and auto-hide
    // ----------------------------------------------------------------------

    void Update()
    {
        bool tapped = false;
        bool touchHeld = false;
        Vector2 tapPosition = Vector2.zero;

        if (Touchscreen.current != null)
        {
            var touch = Touchscreen.current.primaryTouch;
            if (touch.press.wasPressedThisFrame)
            {
                tapped = true;
                tapPosition = touch.position.ReadValue();
            }
            if (touch.press.isPressed)
                touchHeld = true;
        }

        if (Mouse.current != null)
        {
            if (Mouse.current.leftButton.wasPressedThisFrame)
            {
                tapped = true;
                tapPosition = Mouse.current.position.ReadValue();
            }
            if (Mouse.current.leftButton.isPressed)
                touchHeld = true;
        }

        // On new press: check if it landed on a UI element (called ONCE per tap, not per frame)
        if (tapped)
        {
            bool onUI = IsTapOnUIElement(tapPosition);
            _touchHeldOnUI = onUI; // remember for duration of this drag

            if (onUI)
            {
                // Tapped a UI element -- reset timer
                if (_uiVisible)
                    _autoHideTimer = autoHideSeconds;
            }
            else
            {
                // Tapped empty space
                if (!_uiVisible)
                {
                    FadeUI(true);
                    _uiShownAt = Time.time;
                    _autoHideTimer = autoHideSeconds;
                }
                else if (Time.time - _uiShownAt > MinUiToggleOffDelay)
                {
                    FadeUI(false); // intentional toggle-off
                }
                else
                {
                    _autoHideTimer = autoHideSeconds; // too soon -- reset instead
                }
            }
        }

        // Touch released -- clear held flag
        if (!touchHeld)
            _touchHeldOnUI = false;

        // While finger is held on a UI element, freeze the timer.
        // This prevents FadeUI(false) from firing mid-slider-drag.
        if (_uiVisible && _touchHeldOnUI)
            _autoHideTimer = autoHideSeconds;

        // Auto hide countdown
        if (_uiVisible)
        {
            _autoHideTimer -= Time.deltaTime;
            if (_autoHideTimer <= 0f)
                FadeUI(false);
        }
    }

    // ----------------------------------------------------------------------
    // UI raycast check -- called ONCE per tap, not per frame
    // ----------------------------------------------------------------------

    private bool IsTapOnUIElement(Vector2 screenPosition)
    {
        var eventSystem = EventSystem.current;
        if (eventSystem == null) return false;

        var pointerData = new PointerEventData(eventSystem) { position = screenPosition };

        _raycastResults.Clear();
        eventSystem.RaycastAll(pointerData, _raycastResults);

        return _raycastResults.Count > 0;
    }

    // ----------------------------------------------------------------------
    // Voice completed
    // ----------------------------------------------------------------------

    private void OnVoiceCompleted(string completedPageId)
    {
        if (_pageNode == null) return;
        if (completedPageId != _pageNode.PageId) return;

        _contentCompleted = true;

        nextPageImg?.SetActive(true);
        StopNextPageAnim();
        _nextPageAnimRoutine = StartCoroutine(NextPageAnimRoutine());
    }

    // ----------------------------------------------------------------------
    // CanvasGroup helpers
    // ----------------------------------------------------------------------

    private CanvasGroup GetOrAddCanvasGroup(GameObject go)
    {
        if (go == null) return null;
        var cg = go.GetComponent<CanvasGroup>();
        if (cg == null) cg = go.AddComponent<CanvasGroup>();
        return cg;
    }

    private void SetUIAlpha(float alpha)
    {
        if (_replayCG != null) _replayCG.alpha = alpha;
        if (_backBtnCG != null) _backBtnCG.alpha = alpha;
        if (_sliderCG != null) _sliderCG.alpha = alpha;
        if (_resetCG != null) _resetCG.alpha = alpha;
    }

    private void SetUIInteractable(bool state)
    {
        if (_replayCG != null) { _replayCG.interactable = state; _replayCG.blocksRaycasts = state; }
        if (_backBtnCG != null) { _backBtnCG.interactable = state; _backBtnCG.blocksRaycasts = state; }
        if (_sliderCG != null) { _sliderCG.interactable = state; _sliderCG.blocksRaycasts = state; }
        if (_resetCG != null) { _resetCG.interactable = state; _resetCG.blocksRaycasts = state; }
    }

    private void FadeUI(bool show)
    {
        _uiVisible = show;

        if (show)
        {
            backBtn?.SetActive(true);
            // Slider visibility controlled by canSliderRotate on this page's ModelInteraction.
            // modelInteraction cached in Awake -- always valid, never nulled.
            bool showSlider = modelInteraction != null && modelInteraction.canSliderRotate;
            sliderV?.SetActive(showSlider);
            replayButton?.SetActive(true);
            resetButton?.SetActive(true);
            SetUIInteractable(true);
        }
        else
        {
            SetUIInteractable(false);
        }

        if (_fadeRoutine != null) StopCoroutine(_fadeRoutine);
        _fadeRoutine = StartCoroutine(FadeRoutine(show ? 1f : 0f));
    }

    private IEnumerator FadeRoutine(float targetAlpha)
    {
        float startAlpha = _backBtnCG != null ? _backBtnCG.alpha : (targetAlpha == 1f ? 0f : 1f);
        float elapsed = 0f;

        while (elapsed < fadeDuration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / fadeDuration);
            t = t * t * (3f - 2f * t); // smoothstep
            SetUIAlpha(Mathf.Lerp(startAlpha, targetAlpha, t));
            yield return null;
        }

        SetUIAlpha(targetAlpha);

        if (targetAlpha == 0f)
        {
            replayButton?.SetActive(false);
            resetButton?.SetActive(false);
            backBtn?.SetActive(false);
            sliderV?.SetActive(false);
            // nextPageImg excluded -- has its own lifecycle via OnVoiceCompleted
        }

        _fadeRoutine = null;
    }

    // ----------------------------------------------------------------------
    // UI helpers
    // ----------------------------------------------------------------------

    private void HideAllUI()
    {
        if (_fadeRoutine != null) { StopCoroutine(_fadeRoutine); _fadeRoutine = null; }

        replayButton?.SetActive(false);
        resetButton?.SetActive(false);
        nextPageImg?.SetActive(false);
        backBtn?.SetActive(false);
        sliderV?.SetActive(false);

        SetUIAlpha(0f);
        SetUIInteractable(false);

        _uiVisible = false;
        _autoHideTimer = 0f;
        _touchHeldOnUI = false;
        _contentCompleted = false;
    }

    // ----------------------------------------------------------------------
    // Vuforia tracking
    // ----------------------------------------------------------------------

    private void OnTargetStatusChanged(ObserverBehaviour behaviour, TargetStatus status)
    {
        // Guard: Vuforia may fire after GameObject is destroyed
        if (this == null || !gameObject) return;

        if (status.Status == Status.TRACKED ||
            status.Status == Status.EXTENDED_TRACKED ||
            status.Status == Status.LIMITED)
            OnTrackingFound();
        else
            OnTrackingLost();
    }

    private void OnTrackingFound()
    {
        Current = this;

        if (string.IsNullOrEmpty(addressableKey)) return;

        if (_releaseCoroutine != null)
        {
            StopCoroutine(_releaseCoroutine);
            _releaseCoroutine = null;
        }

        if (instantiatedObject == null && !_isLoading)
        {
            _isLoading = true;
            _loadCancelled = false;
            _contentCompleted = false;

            StopNextPageAnim();
            nextPageImg?.SetActive(false);

            OverlayManager.Instance?.HideAll();
            LoadingScreen.Show();

            Addressables.InstantiateAsync(addressableKey, transform).Completed += handle =>
            {
                _isLoading = false;
                LoadingScreen.Hide();
                OverlayManager.Instance?.HideAll();

                if (_loadCancelled)
                {
                    Debug.Log($"[AR] Load cancelled for '{addressableKey}' -- releasing.");
                    Addressables.ReleaseInstance(handle.Result);
                    return;
                }

                if (instantiatedObject != null)
                {
                    Addressables.ReleaseInstance(handle.Result);
                    return;
                }

                instantiatedObject = handle.Result;
                instantiatedObject.transform.localPosition = Vector3.zero;
                contentControl = instantiatedObject.GetComponent<IARContent>();

                // modelInteraction cached in Awake -- do NOT reassign here.
                // Only call Init to set up the model transform and slider values.
                modelInteraction?.Init(instantiatedObject);

                var components = instantiatedObject.GetComponentsInChildren<QuizManager>(true);
                if (components.Length > 0)
                    quizManager = components[0];

                quizManager?.PauseQuiz(false);

                _pageNode = instantiatedObject.GetComponentInChildren<ARTrackedPageNode>();
                _activePageId = _pageNode != null ? _pageNode.PageId : addressableKey;
                _trackHook?.SetPageNode(_pageNode);

                contentControl?.PlayContent();
            };
        }
        else if (instantiatedObject != null)
        {
            // Grace time resume -- content still alive
            OverlayManager.Instance?.HideLostTracking();

            ToggleRenderers(true);
            modelInteraction?.Resume();
            quizManager?.PauseQuiz(false);
            _trackHook?.SetPageNode(_pageNode);
            contentControl?.PlayContent();
        }
    }

    private void OnTrackingLost()
    {
        if (Current == this) Current = null;

        // BUGFIX: detach this instance's slider listener immediately on tracking lost.
        // Without this, the invisible model continues receiving slider events during
        // the grace period and corrupts its own 2D or 3D global value.
        // Init() / Resume() on the next active page will re-attach the correct listener.
        modelInteraction?.DetachSlider();

        if (_isLoading)
        {
            _loadCancelled = true;
            LoadingScreen.Hide();
            OverlayManager.Instance?.HideAll();
            HideAllUI();
            return;
        }

        if (instantiatedObject != null)
        {
            contentControl?.PauseContent();
            quizManager?.PauseQuiz(true);

            if (_contentCompleted)
            {
                // Content done -- release immediately, no grace time
                if (_releaseCoroutine != null) { StopCoroutine(_releaseCoroutine); _releaseCoroutine = null; }

                StopNextPageAnim();
                nextPageImg?.SetActive(false);
                OverlayManager.Instance?.HideAll();

                _trackHook?.ClearPageNode();
                Addressables.ReleaseInstance(instantiatedObject);

                // Clear content objects only -- NOT modelInteraction (it's this GO's component)
                instantiatedObject = null;
                contentControl = null;
                quizManager = null;
                _pageNode = null;
                _activePageId = null;
                _contentCompleted = false;

                _arMediaManager?.NotifyContentReleased();

                HideAllUI();
                OverlayManager.Instance?.ShowLostTracking();
                return;
            }

            // Content still playing -- grace period
            ToggleRenderers(false);
            OverlayManager.Instance?.ShowLostTracking();

            if (_releaseCoroutine != null) { StopCoroutine(_releaseCoroutine); _releaseCoroutine = null; }

            float grace = _arMediaManager != null ? _arMediaManager.ResumeGraceSeconds : 4f;
            _releaseCoroutine = StartCoroutine(ReleaseAfterGrace(grace));
        }
    }

    private IEnumerator ReleaseAfterGrace(float grace)
    {
        yield return new WaitForSeconds(grace);

        if (instantiatedObject != null)
        {
            _trackHook?.ClearPageNode();
            Addressables.ReleaseInstance(instantiatedObject);

            // Clear content objects only -- NOT modelInteraction
            instantiatedObject = null;
            contentControl = null;
            quizManager = null;
            _pageNode = null;
            _activePageId = null;
        }

        _arMediaManager?.NotifyContentReleased();

        HideAllUI();
        _releaseCoroutine = null;
    }

    // ----------------------------------------------------------------------
    // NextPageImg animation
    // ----------------------------------------------------------------------

    private void StopNextPageAnim()
    {
        if (_nextPageAnimRoutine != null)
        {
            StopCoroutine(_nextPageAnimRoutine);
            _nextPageAnimRoutine = null;
        }

        if (nextPageImg != null)
        {
            nextPageImg.transform.localScale = Vector3.one;
            var rt = nextPageImg.GetComponent<RectTransform>();
            if (rt != null) rt.anchoredPosition = _nextPageImgOriginalPos;
        }
    }

    private IEnumerator NextPageAnimRoutine()
    {
        if (nextPageImg == null) yield break;

        RectTransform rt = nextPageImg.GetComponent<RectTransform>();
        if (rt == null) yield break;

        nextPageImg.transform.localScale = Vector3.zero;
        float elapsed = 0f;

        while (elapsed < 0.15f)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / 0.15f);
            nextPageImg.transform.localScale = Vector3.one * Mathf.Lerp(0f, 1.2f, t);
            yield return null;
        }

        elapsed = 0f;
        while (elapsed < 0.1f)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / 0.1f);
            nextPageImg.transform.localScale = Vector3.one * Mathf.Lerp(1.2f, 1f, t);
            yield return null;
        }

        nextPageImg.transform.localScale = Vector3.one;

        Vector2 startPos = _nextPageImgOriginalPos;
        Vector2 leftPos = startPos + new Vector2(-25f, 0f);

        while (true)
        {
            elapsed = 0f;
            while (elapsed < 0.4f)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / 0.4f);
                t = t * t * (3f - 2f * t);
                rt.anchoredPosition = Vector2.Lerp(startPos, leftPos, t);
                yield return null;
            }

            elapsed = 0f;
            while (elapsed < 0.4f)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / 0.4f);
                t = t * t * (3f - 2f * t);
                rt.anchoredPosition = Vector2.Lerp(leftPos, startPos, t);
                yield return null;
            }

            yield return new WaitForSeconds(0.3f);
        }
    }

    // ----------------------------------------------------------------------
    // Replay / Reset
    // ----------------------------------------------------------------------

    public static void ReplayCurrent() { Current?.OnReplayButtonPressed(); }

    public void OnReplayButtonPressed()
    {
        if (contentControl == null) return;

        _contentCompleted = false;
        StopNextPageAnim();
        nextPageImg?.SetActive(false);

        contentControl.ReplayContent();
    }

    public void OnResetButtonPressed()
    {
        ModelInteraction.ResetCurrent();
    }

    // ----------------------------------------------------------------------
    // Renderer toggle (grace time hide/show)
    // ----------------------------------------------------------------------

    private void ToggleRenderers(bool visible)
    {
        if (instantiatedObject == null) return;

        var renderers = instantiatedObject.GetComponentsInChildren<Renderer>();
        foreach (var r in renderers) r.enabled = visible;

        var canvas = instantiatedObject.GetComponentsInChildren<Canvas>();
        foreach (var c in canvas) c.enabled = visible;

        var videos = instantiatedObject.GetComponentsInChildren<VideoPlayer>(true);
        foreach (var v in videos)
        {
            if (v.targetMaterialRenderer != null)
                v.targetMaterialRenderer.enabled = visible;
        }

        var particles = instantiatedObject.GetComponentsInChildren<ParticleSystem>(true);
        foreach (var p in particles)
        {
            if (visible) p.Play();
            else p.Stop();
        }
    }
}