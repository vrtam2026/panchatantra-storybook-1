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

    // Quiz pages use the same CustomARHandler addressable flow,
    // but once the quiz opens it should no longer depend on marker tracking.
    private bool _isQuizContent = false;
    private bool _pausedVuforiaForQuiz = false;

    [Header("Quiz Safety")]
    [Tooltip("ON = while quiz is open, normal story page taps, replay, reset, slider, next, and background UI cannot appear behind the quiz.")]
    [SerializeField] private bool blockBackgroundTouchesWhileQuizOpen = true;

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

    [Header("Replay Option")]
    [Tooltip("ON = Replay button stays visible while this AR page is loaded. OFF = Replay button shows and hides with Back, Slider, and Reset.")]
    public bool keepReplayButtonAlwaysVisible = false;

    private bool _uiVisible = false;
    private float _autoHideTimer = 0f;
    private float _uiShownAt = 0f;
    private const float MinUiToggleOffDelay = 0.8f;

    // Tracks whether the current touch/drag started on a UI element.
    // While touch is held on UI, auto-hide timer is frozen.
    // Prevents slider from stopping mid-drag when timer expires.
    private bool _touchHeldOnUI = false;
    private bool _replayButtonBound = false;

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

        BindReplayButtonIfNeeded();

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

    private void OnEnable()
    {
        ARMediaManager.OnVoiceCompleted += OnVoiceCompleted;
        ARMediaManager.OnPageRestarted += OnPageRestarted;
    }

    private void OnDisable()
    {
        ARMediaManager.OnVoiceCompleted -= OnVoiceCompleted;
        ARMediaManager.OnPageRestarted -= OnPageRestarted;
    }

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
        // Quiz rule:
        // Once quiz is open, the child should interact only with quiz UI.
        // Do not allow normal story page taps to show replay/reset/slider/next UI behind the quiz.
        if (ShouldBlockBackgroundTouchesForQuiz())
        {
            HidePageControlsWhileQuizIsOpen();
            return;
        }

        BindReplayButtonIfNeeded();
        RefreshReplayButtonVisibility();

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
        if (_contentCompleted)
            return;

        if (_pageNode == null) return;
        if (completedPageId != _pageNode.PageId) return;

        _contentCompleted = true;

        /*nextPageImg?.SetActive(true);
        StopNextPageAnim();
        _nextPageAnimRoutine = StartCoroutine(NextPageAnimRoutine());*/

        // NEW: run interaction flow first
        if (contentControl != null)
        {
            contentControl.SetCompletionCallback(OnInteractionCompleted);
            contentControl.PlayContent();
        }
        else
        {
            // fallback if no interaction system
            ShowNextPage();
        }
    }

    private void OnPageRestarted(string pageId)
    {
        if (_pageNode == null) return;
        if (_pageNode.PageId != pageId) return;

        PrepareForReplayReset();
    }

    private void PrepareForReplayReset()
    {
        _contentCompleted = false;

        if (contentControl is ContentController controller)
            controller.ResetInteractions();

        ResetPageFlow();
    }

    void OnInteractionCompleted()
    {
        ShowNextPage();
        OverlayManager.Instance?.OnStoryCompleted();
    }

    void ShowNextPage()
    {
        nextPageImg?.SetActive(true);
        StopNextPageAnim();
        _nextPageAnimRoutine = StartCoroutine(NextPageAnimRoutine());
    }


    // ----------------------------------------------------------------------
    // Replay button helper
    // ----------------------------------------------------------------------

    private void BindReplayButtonIfNeeded()
    {
        if (_replayButtonBound || replayButton == null) return;

        Button btn = replayButton.GetComponent<Button>();
        if (btn == null) return;

        // Keep existing Inspector events, but make sure this page handler also receives replay clicks.
        btn.onClick.RemoveListener(OnReplayButtonPressed);
        btn.onClick.AddListener(OnReplayButtonPressed);
        _replayButtonBound = true;
    }

    private bool ShouldKeepReplayVisible()
    {
        // Only keep Replay visible when this page content exists.
        // Before scanning or after content release, Replay should stay hidden.
        return keepReplayButtonAlwaysVisible && instantiatedObject != null;
    }

    // Runs every frame from Update(), so only actually touch SetActive/CanvasGroup when
    // the visible/not-visible state genuinely changes -- these calls dirty the object
    // and Canvas even when the values written are identical to what's already there.
    private bool _replayVisibilityApplied = false;

    private void RefreshReplayButtonVisibility()
    {
        if (!ShouldKeepReplayVisible() || replayButton == null || _replayCG == null)
        {
            _replayVisibilityApplied = false;
            return;
        }

        if (_replayVisibilityApplied) return;

        replayButton.SetActive(true);
        _replayCG.alpha = 1f;
        _replayCG.interactable = true;
        _replayCG.blocksRaycasts = true;
        _replayVisibilityApplied = true;
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
        if (_replayCG != null) _replayCG.alpha = ShouldKeepReplayVisible() ? 1f : alpha;
        if (_backBtnCG != null) _backBtnCG.alpha = alpha;
        if (_sliderCG != null) _sliderCG.alpha = alpha;
        if (_resetCG != null) _resetCG.alpha = alpha;
    }

    private void SetUIInteractable(bool state)
    {
        if (_replayCG != null)
        {
            bool replayState = ShouldKeepReplayVisible() ? true : state;
            _replayCG.interactable = replayState;
            _replayCG.blocksRaycasts = replayState;
        }
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
            if (!ShouldKeepReplayVisible())
                replayButton?.SetActive(false);
            else
                replayButton?.SetActive(true);

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

    private bool ShouldBlockBackgroundTouchesForQuiz()
    {
        return blockBackgroundTouchesWhileQuizOpen && IsRelaxedQuizOpen();
    }

    private void HidePageControlsWhileQuizIsOpen()
    {
        if (_fadeRoutine != null)
        {
            StopCoroutine(_fadeRoutine);
            _fadeRoutine = null;
        }

        StopNextPageAnim();

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
    }


    // ----------------------------------------------------------------------
    // Quiz relaxed mode helpers
    // ----------------------------------------------------------------------

    private bool IsQuizPageKey()
    {
        return !string.IsNullOrEmpty(addressableKey) &&
               addressableKey.ToLowerInvariant().Contains("quiz");
    }

    private bool IsRelaxedQuizOpen()
    {
        return _isQuizContent && instantiatedObject != null && quizManager != null;
    }

    private void PrepareQuizForRelaxedMode(GameObject quizRoot)
    {
        if (quizRoot == null) return;

        // Detach from the image target so losing marker does not move or hide the quiz.
        quizRoot.transform.SetParent(null, true);

        // Force quiz UI to behave like normal full-screen UI.
        Canvas[] canvases = quizRoot.GetComponentsInChildren<Canvas>(true);
        foreach (Canvas canvas in canvases)
        {
            if (canvas == null) continue;
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = Mathf.Max(canvas.sortingOrder, 20);
            canvas.enabled = true;

            CanvasScaler scaler = canvas.GetComponent<CanvasScaler>();
            if (scaler == null) scaler = canvas.gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080f, 1920f);
            scaler.matchWidthOrHeight = 0.5f;
        }

        quizRoot.SetActive(true);
        OverlayManager.Instance?.HideLostTracking();
        HidePageControlsWhileQuizIsOpen();
    }

    private void PauseARCameraForQuiz()
    {
        if (_pausedVuforiaForQuiz) return;

        VuforiaBehaviour behaviour = VuforiaBehaviour.Instance;
        if (behaviour != null && behaviour.enabled)
        {
            behaviour.enabled = false;
            _pausedVuforiaForQuiz = true;
            Debug.Log("[Quiz] Quiz opened. AR tracking paused so the child can relax.");
        }
    }

    private void ResumeARCameraAfterQuiz()
    {
        if (!_pausedVuforiaForQuiz) return;

        VuforiaBehaviour behaviour = VuforiaBehaviour.Instance;
        if (behaviour != null)
            behaviour.enabled = true;

        _pausedVuforiaForQuiz = false;
        Debug.Log("[Quiz] Quiz closed. AR tracking resumed.");
    }

    public bool OwnsQuizManager(QuizManager manager)
    {
        return manager != null && quizManager == manager;
    }

    public void ExitQuizFromQuizManager(QuizManager manager)
    {
        if (manager != null && quizManager != null && manager != quizManager)
        {
            Debug.LogWarning("[Quiz] Exit ignored because this CustomARHandler does not own that QuizManager.");
            return;
        }

        Debug.Log("[Quiz] Exit requested. Closing quiz and returning to AR scan mode.");

        if (_releaseCoroutine != null)
        {
            StopCoroutine(_releaseCoroutine);
            _releaseCoroutine = null;
        }

        LoadingScreen.Hide();
        OverlayManager.Instance?.HideAll();
        StopNextPageAnim();
        nextPageImg?.SetActive(false);

        if (instantiatedObject != null)
            Addressables.ReleaseInstance(instantiatedObject);

        instantiatedObject = null;
        contentControl = null;
        quizManager = null;
        _pageNode = null;
        _activePageId = null;
        _contentCompleted = false;
        _isQuizContent = false;
        _isLoading = false;
        _loadCancelled = false;

        _trackHook?.ClearPageNode();
        _arMediaManager?.NotifyContentReleased();

        HideAllUI();
        ResumeARCameraAfterQuiz();

        if (Current == this) Current = null;
    }

    // ----------------------------------------------------------------------
    // Vuforia tracking
    // ----------------------------------------------------------------------

    private void OnTargetStatusChanged(ObserverBehaviour behaviour, TargetStatus status)
    {
        // Guard: Vuforia may fire after GameObject is destroyed
        if (this == null || !gameObject) return;

        // Only Status.TRACKED means the camera is genuinely seeing the printed image right
        // now. EXTENDED_TRACKED means Vuforia has stopped seeing the image and is instead
        // guessing its position from the phone's own motion (device tracking) -- this is
        // exactly what made content appear to "follow your hand": once the marker is
        // covered, the content kept moving with the phone/camera instead of pausing.
        // LIMITED means low-confidence but still real image tracking, so it still counts
        // as found. Treating EXTENDED_TRACKED as lost sends it through the normal grace
        // period (pause in place, then release) instead of drifting with device motion.
        if (status.Status == Status.TRACKED ||
            status.Status == Status.LIMITED)
            OnTrackingFound();
        else
            OnTrackingLost();
    }

    private void OnTrackingFound()
    {
        Current = this;

        if (string.IsNullOrEmpty(addressableKey)) return;

        // Notify window manager so it can release out-of-window pages and preload neighbours
        ARWindowManager.Instance?.OnPageDetected(addressableKey);

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

                if (handle.Status != AsyncOperationStatus.Succeeded)
                {
                    Debug.LogError($"[AR] Download failed for '{addressableKey}': {handle.OperationException?.Message}");
                    OverlayManager.Instance?.ShowLostTracking();
                    return;
                }

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
                {
                    quizManager = components[0];
                    _isQuizContent = true;
                    quizManager.RegisterARHandler(this);
                    PrepareQuizForRelaxedMode(instantiatedObject);
                    PauseARCameraForQuiz();
                }
                else
                {
                    quizManager = null;
                    _isQuizContent = false;
                }

                quizManager?.PauseQuiz(false);

                _pageNode = instantiatedObject.GetComponentInChildren<ARTrackedPageNode>();
                _activePageId = _pageNode != null ? _pageNode.PageId : addressableKey;
                _trackHook?.SetPageNode(_pageNode);
                RefreshReplayButtonVisibility();

                // Warm the audio cache in parallel — so audio is ready by the time the page
                // finishes its intro reveal and ARMediaManager calls PlayPageAudioFromBeginning.
                if (_pageNode != null && ARAddressableAudioService.Instance != null)
                {
                    string lang = ARGlobalLanguage.GetCurrentLanguage();
                    ARAddressableAudioService.Instance.PreloadAudioPack(lang, _pageNode.PageId);
                    Debug.Log($"[AR-AUDIO] Preloading audio: audio/{lang}/{_pageNode.PageId}");
                }

                //contentControl?.PlayContent();
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
            RefreshReplayButtonVisibility();
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

        // Quiz page rule: marker is needed only once to start loading.
        // If the child moves away while quiz is loading, keep loading in the background.
        if (_isLoading && IsQuizPageKey())
        {
            Debug.Log("[Quiz] Marker lost while quiz is loading. Download continues.");
            return;
        }

        if (_isLoading)
        {
            _loadCancelled = true;
            LoadingScreen.Hide();
            OverlayManager.Instance?.HideAll();
            HideAllUI();
            return;
        }

        // Quiz page rule: once quiz is open, losing marker must not hide, pause, or release it.
        if (IsRelaxedQuizOpen())
        {
            Debug.Log("[Quiz] Marker lost, but quiz stays open until Exit is clicked.");
            OverlayManager.Instance?.HideLostTracking();
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
                _isQuizContent = false;
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

            float grace = _arMediaManager != null ? _arMediaManager.ResumeGraceSeconds : 1f;
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
            _isQuizContent = false;
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

    public void ResetPageFlow()
    {
        StopNextPageAnim();
        nextPageImg?.SetActive(false);

        OverlayManager.Instance?.HideAll();
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
        if (_pageNode == null && contentControl == null) return;

        RestartExperience();
    }

    public void RestartExperience()
    {
        OverlayManager.Instance?.StopWatching();
        OverlayManager.Instance?.HideAll();

        if (_arMediaManager == null)
            _arMediaManager = Object.FindFirstObjectByType<ARMediaManager>();

        if (_arMediaManager != null)
        {
            // The media manager owns the full replay sequence:
            // stop voice, reset page systems, play VFX/popup, then start voice + animation + spline.
            _arMediaManager.ReplayActivePage();
            return;
        }

        // Fallback only if no ARMediaManager exists in the scene.
        PrepareForReplayReset();
        _pageNode?.StartFromBeginning();
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

       /* var particles = instantiatedObject.GetComponentsInChildren<ParticleSystem>(true);
        foreach (var p in particles)
        {
            if (visible) p.Play();
            else p.Stop();
        }*/
    }

    // -------------------------------------------------------------------------
    // Window-based release — called by ARWindowManager
    // -------------------------------------------------------------------------

    /// <summary>
    /// Silently releases prefab content when this page falls outside the active window.
    /// No overlay is shown — the user has already moved away from this page.
    /// </summary>
    public void ForceRelease()
    {
        if (_releaseCoroutine != null)
        {
            StopCoroutine(_releaseCoroutine);
            _releaseCoroutine = null;
        }

        if (_isLoading)
            _loadCancelled = true;

        if (instantiatedObject != null)
        {
            _trackHook?.ClearPageNode();
            Addressables.ReleaseInstance(instantiatedObject);
            instantiatedObject  = null;
            contentControl      = null;
            quizManager         = null;
            _isQuizContent      = false;
            _pageNode           = null;
            _activePageId       = null;
            _contentCompleted   = false;
            _isLoading          = false;
            _loadCancelled      = false;
        }

        _arMediaManager?.NotifyContentReleased();

        HideAllUI();
        Debug.Log($"[AR-WINDOW] ForceRelease: {addressableKey}");
    }

    // -------------------------------------------------------------------------
    // Diagnostics — used by ARDiagnosticOverlay
    // -------------------------------------------------------------------------

    public struct DiagnosticInfo
    {
        public string pageId;         // active page ID (empty if none)
        public string addressableKey; // the Addressable address for this handler
        public string prefabStatus;   // "None" | "Downloading" | "Loaded" | "Released"
    }

    public DiagnosticInfo GetDiagnosticInfo()
    {
        string status;
        if (instantiatedObject != null)
            status = "Loaded";
        else if (!string.IsNullOrEmpty(_activePageId))
            status = "Released";
        else if (!string.IsNullOrEmpty(addressableKey))
            status = "Downloading";
        else
            status = "None";

        return new DiagnosticInfo
        {
            pageId         = _activePageId ?? "",
            addressableKey = addressableKey ?? "",
            prefabStatus   = status
        };
    }
}