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

    [Header("UI Visibility Per Page")]
    [Tooltip("Uncheck to hide Replay button on this page.")]
    public bool showReplayButton = true;

    [Tooltip("Uncheck to hide Back button on this page.")]
    public bool showBackButton = true;

    [Tooltip("Uncheck to hide Slider on this page.")]
    public bool showSlider = true;

    [Tooltip("Uncheck to hide Reset button on this page.")]
    public bool showResetButton = true;

    [Tooltip("Uncheck to hide Next Page image on this page.")]
    public bool showNextPageImg = true;

    [Header("Tracking Stability")]
    [Tooltip("For storybook AR, keep this true. LIMITED usually means weak but usable tracking.")]
    [SerializeField] private bool treatLimitedAsTracked = true;

    [Tooltip("Small delay before accepting tracking found. Keep 0 for instant response.")]
    [SerializeField] private float foundConfirmSeconds = 0f;

    [Tooltip("Small delay before accepting tracking lost. Prevents flicker and shaking.")]
    [SerializeField] private float lostConfirmSeconds = 0.25f;

    private bool _uiVisible = false;
    private float _autoHideTimer = 0f;
    private float _uiShownAt = 0f;
    private const float MinUiToggleOffDelay = 0.8f;

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
    private GameObject _arCamera;

    private readonly List<RaycastResult> _raycastResults = new List<RaycastResult>();

    public static CustomARHandler Current;
    private bool _isDestroying;

    private bool _hasTrackingState;
    private bool _rawTracked;
    private bool _stableTracked;
    private Coroutine _trackingStateRoutine;

    private bool IsQuizPage => !string.IsNullOrEmpty(addressableKey) &&
        addressableKey.IndexOf("quiz", System.StringComparison.OrdinalIgnoreCase) >= 0;

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

        modelInteraction = GetComponent<ModelInteraction>();

        if (replayButton != null)
        {
            var btn = replayButton.GetComponentInChildren<Button>(true);
            if (btn != null) btn.onClick.AddListener(OnReplayButtonPressed);
        }

        SetUIAlpha(0f);
        SetUIInteractable(false);
    }

    private void Start()
    {
        HideAllUI();

        _trackHook = GetComponent<VuforiaTrackHook>();
        _arMediaManager = Object.FindFirstObjectByType<ARMediaManager>();

        var observer = GetComponent<ObserverBehaviour>();
        if (observer != null)
            observer.OnTargetStatusChanged += OnTargetStatusChanged;
    }

    private void OnEnable()
    {
        ARMediaManager.OnVoiceCompleted += OnVoiceCompleted;
    }

    private void OnDisable()
    {
        ARMediaManager.OnVoiceCompleted -= OnVoiceCompleted;
    }

    private void OnDestroy()
    {
        _isDestroying = true;

        var observer = GetComponent<ObserverBehaviour>();
        if (observer != null)
            observer.OnTargetStatusChanged -= OnTargetStatusChanged;

        if (_trackingStateRoutine != null)
        {
            StopCoroutine(_trackingStateRoutine);
            _trackingStateRoutine = null;
        }

        if (replayButton != null)
        {
            var btn = replayButton.GetComponentInChildren<Button>(true);
            if (btn != null) btn.onClick.RemoveListener(OnReplayButtonPressed);
        }

        UnsubscribeReveal();
    }

    private void Update()
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

        if (tapped)
        {
            bool onUI = IsTapOnUIElement(tapPosition);
            _touchHeldOnUI = onUI;

            if (onUI)
            {
                if (_uiVisible)
                    _autoHideTimer = autoHideSeconds;
            }
            else
            {
                if (!_uiVisible)
                {
                    FadeUI(true);
                    _uiShownAt = Time.time;
                    _autoHideTimer = autoHideSeconds;
                }
                else if (Time.time - _uiShownAt > MinUiToggleOffDelay)
                {
                    FadeUI(false);
                }
                else
                {
                    _autoHideTimer = autoHideSeconds;
                }
            }
        }

        if (!touchHeld)
            _touchHeldOnUI = false;

        if (_uiVisible && _touchHeldOnUI)
            _autoHideTimer = autoHideSeconds;

        if (_uiVisible)
        {
            _autoHideTimer -= Time.deltaTime;

            if (_autoHideTimer <= 0f)
                FadeUI(false);
        }
    }

    private bool IsTapOnUIElement(Vector2 screenPosition)
    {
        var eventSystem = EventSystem.current;
        if (eventSystem == null) return false;

        var pointerData = new PointerEventData(eventSystem) { position = screenPosition };

        _raycastResults.Clear();
        eventSystem.RaycastAll(pointerData, _raycastResults);

        return _raycastResults.Count > 0;
    }

    private void OnVoiceCompleted(string completedPageId)
    {
        if (_pageNode == null) return;
        if (completedPageId != _pageNode.PageId) return;

        _contentCompleted = true;

        if (showNextPageImg)
        {
            nextPageImg?.SetActive(true);
            StopNextPageAnim();
            _nextPageAnimRoutine = StartCoroutine(NextPageAnimRoutine());
        }
    }

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
        if (_replayCG != null)
        {
            _replayCG.interactable = state;
            _replayCG.blocksRaycasts = state;
        }

        if (_backBtnCG != null)
        {
            _backBtnCG.interactable = state;
            _backBtnCG.blocksRaycasts = state;
        }

        if (_sliderCG != null)
        {
            _sliderCG.interactable = state;
            _sliderCG.blocksRaycasts = state;
        }

        if (_resetCG != null)
        {
            _resetCG.interactable = state;
            _resetCG.blocksRaycasts = state;
        }
    }

    private void FadeUI(bool show)
    {
        _uiVisible = show;

        if (show)
        {
            if (showBackButton) backBtn?.SetActive(true);

            bool sliderAllowed = showSlider && modelInteraction != null && modelInteraction.canSliderRotate;
            sliderV?.SetActive(sliderAllowed);

            if (showReplayButton) replayButton?.SetActive(true);
            if (showResetButton) resetButton?.SetActive(true);

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
            t = t * t * (3f - 2f * t);

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
        }

        _fadeRoutine = null;
    }

    private void HideAllUI()
    {
        if (_fadeRoutine != null)
        {
            StopCoroutine(_fadeRoutine);
            _fadeRoutine = null;
        }

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

    private void OnTargetStatusChanged(ObserverBehaviour behaviour, TargetStatus status)
    {
        if (_isDestroying || !this) return;

        try
        {
            bool tracked = IsTrackedStatus(status);

            if (!_hasTrackingState)
            {
                _hasTrackingState = true;
                _rawTracked = tracked;
                _stableTracked = tracked;

                if (tracked)
                    OnTrackingFound();
                else
                    OnTrackingLost();

                return;
            }

            if (tracked == _rawTracked)
                return;

            _rawTracked = tracked;

            if (_trackingStateRoutine != null)
            {
                StopCoroutine(_trackingStateRoutine);
                _trackingStateRoutine = null;
            }

            if (tracked == _stableTracked)
                return;

            float delay = tracked ? foundConfirmSeconds : lostConfirmSeconds;

            if (delay <= 0f || !CanStartCoroutineSafely())
            {
                ApplyStableTrackingState(tracked);
            }
            else
            {
                _trackingStateRoutine = StartCoroutine(ConfirmTrackingChange(tracked, delay));
            }
        }
        catch (MissingReferenceException)
        {
            // Vuforia can fire one late callback while this target is being disabled or destroyed.
        }
    }

    private bool IsTrackedStatus(TargetStatus status)
    {
        if (status.Status == Status.TRACKED)
            return true;

        if (status.Status == Status.EXTENDED_TRACKED)
            return true;

        if (treatLimitedAsTracked && status.Status == Status.LIMITED)
            return true;

        return false;
    }

    private IEnumerator ConfirmTrackingChange(bool targetTrackedState, float delay)
    {
        yield return new WaitForSeconds(delay);

        _trackingStateRoutine = null;

        if (_isDestroying || !this) yield break;
        if (_rawTracked != targetTrackedState) yield break;

        ApplyStableTrackingState(targetTrackedState);
    }

    private void ApplyStableTrackingState(bool tracked)
    {
        if (tracked == _stableTracked)
            return;

        _stableTracked = tracked;

        if (tracked)
            OnTrackingFound();
        else
            OnTrackingLost();
    }

    private void OnTrackingFound()
    {
        if (_isDestroying || !this) return;

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

            if (OverlayManager.Instance != null)
                OverlayManager.Instance.HideAll();

            if (IsQuizPage)
                QuizLoadingScreen.Show();
            else
                LoadingScreen.Show();

            var loadOp = IsQuizPage
                ? Addressables.InstantiateAsync(addressableKey)
                : Addressables.InstantiateAsync(addressableKey, transform);

            loadOp.Completed += handle =>
            {
                if (_isDestroying || !this)
                {
                    if (handle.Status == AsyncOperationStatus.Succeeded && handle.Result != null)
                        Addressables.ReleaseInstance(handle.Result);

                    return;
                }

                _isLoading = false;

                if (IsQuizPage)
                    QuizLoadingScreen.Hide();
                else
                    LoadingScreen.Hide();

                if (OverlayManager.Instance != null)
                    OverlayManager.Instance.HideAll();

                if (_loadCancelled)
                {
                    Debug.Log($"[AR] Load cancelled for '{addressableKey}'.");

                    if (handle.Status == AsyncOperationStatus.Succeeded && handle.Result != null)
                        Addressables.ReleaseInstance(handle.Result);

                    return;
                }

                if (handle.Status != AsyncOperationStatus.Succeeded || handle.Result == null)
                {
                    Debug.LogWarning($"[AR] Failed to load addressable '{addressableKey}'.");
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

                modelInteraction?.Init(instantiatedObject);

                var components = instantiatedObject.GetComponentsInChildren<QuizManager>(true);
                if (components.Length > 0)
                    quizManager = components[0];

                if (quizManager != null)
                {
                    SetupQuizOnScreen(instantiatedObject);
                    StartCoroutine(ShowQuizAfterLoading(instantiatedObject));
                }
                else
                {
                    _pageNode = instantiatedObject.GetComponentInChildren<ARTrackedPageNode>();
                    _activePageId = _pageNode != null ? _pageNode.PageId : addressableKey;

                    var vfxCtrl = instantiatedObject.GetComponentInChildren<ARVFXPopupController>(true);

                    if (vfxCtrl != null)
                    {
                        SubscribeReveal();
                    }
                    else
                    {
                        _trackHook?.SetPageNode(_pageNode);
                        contentControl?.PlayContent();
                    }
                }
            };
        }
        else if (instantiatedObject != null)
        {
            if (OverlayManager.Instance != null)
                OverlayManager.Instance.HideLostTracking();

            ToggleRenderers(true);

            modelInteraction?.Resume();
            quizManager?.PauseQuiz(false);

            var vfxCtrl = instantiatedObject.GetComponentInChildren<ARVFXPopupController>(true);

            if (vfxCtrl != null && !vfxCtrl.IsRevealComplete)
            {
                SubscribeReveal();
                vfxCtrl.ResumeReveal();
                return;
            }

            _trackHook?.SetPageNode(_pageNode);
            contentControl?.PlayContent();
        }
    }

    private void OnTrackingLost()
    {
        if (_isDestroying || !this) return;

        if (IsQuizPage)
        {
            modelInteraction?.DetachSlider();
            return;
        }

        if (Current == this)
            Current = null;

        modelInteraction?.DetachSlider();

        if (_isLoading)
        {
            _loadCancelled = true;

            LoadingScreen.Hide();

            if (OverlayManager.Instance != null)
                OverlayManager.Instance.HideAll();

            HideAllUI();
            return;
        }

        if (instantiatedObject != null)
        {
            contentControl?.PauseContent();
            quizManager?.PauseQuiz(true);

            var vfxCtrl = instantiatedObject.GetComponentInChildren<ARVFXPopupController>(true);

            if (vfxCtrl != null && !vfxCtrl.IsRevealComplete)
                vfxCtrl.PauseReveal();

            if (_contentCompleted)
            {
                if (_releaseCoroutine != null)
                {
                    StopCoroutine(_releaseCoroutine);
                    _releaseCoroutine = null;
                }

                StopNextPageAnim();
                nextPageImg?.SetActive(false);

                if (OverlayManager.Instance != null)
                    OverlayManager.Instance.HideAll();

                UnsubscribeReveal();

                _trackHook?.ClearPageNode();

                Addressables.ReleaseInstance(instantiatedObject);

                instantiatedObject = null;
                contentControl = null;
                quizManager = null;
                _pageNode = null;
                _activePageId = null;
                _contentCompleted = false;

                _arMediaManager?.NotifyContentReleased();

                HideAllUI();

                if (OverlayManager.Instance != null)
                    OverlayManager.Instance.ShowLostTracking();

                return;
            }

            ToggleRenderers(false);

            if (OverlayManager.Instance != null)
                OverlayManager.Instance.ShowLostTracking();

            if (_releaseCoroutine != null)
            {
                StopCoroutine(_releaseCoroutine);
                _releaseCoroutine = null;
            }

            float grace = _arMediaManager != null ? _arMediaManager.ResumeGraceSeconds : 4f;

            if (CanStartCoroutineSafely())
            {
                _releaseCoroutine = StartCoroutine(ReleaseAfterGrace(grace));
            }
            else
            {
                _trackHook?.ClearPageNode();

                if (instantiatedObject != null)
                {
                    Addressables.ReleaseInstance(instantiatedObject);

                    instantiatedObject = null;
                    contentControl = null;
                    quizManager = null;
                    _pageNode = null;
                    _activePageId = null;
                }

                _arMediaManager?.NotifyContentReleased();
                HideAllUI();
            }
        }
    }

    private bool CanStartCoroutineSafely()
    {
        if (_isDestroying || !this) return false;

        try
        {
            return gameObject != null && gameObject.activeInHierarchy;
        }
        catch (MissingReferenceException)
        {
            return false;
        }
    }

    private IEnumerator ReleaseAfterGrace(float grace)
    {
        yield return new WaitForSeconds(grace);

        if (_isDestroying || !this) yield break;

        if (instantiatedObject != null)
        {
            UnsubscribeReveal();

            _trackHook?.ClearPageNode();

            Addressables.ReleaseInstance(instantiatedObject);

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
            if (rt != null)
                rt.anchoredPosition = _nextPageImgOriginalPos;
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

    private void SetupQuizOnScreen(GameObject quizRoot)
    {
        Canvas canvas = quizRoot.GetComponent<Canvas>();

        if (canvas == null)
            canvas = quizRoot.GetComponentInChildren<Canvas>(true);

        if (canvas != null)
        {
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 15;

            CanvasScaler scaler = canvas.GetComponent<CanvasScaler>();

            if (scaler == null)
                scaler = canvas.gameObject.AddComponent<CanvasScaler>();

            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080, 1920);
            scaler.matchWidthOrHeight = 0.5f;
        }
        else
        {
            Debug.LogWarning("[AR] SetupQuizOnScreen: No Canvas found in quiz prefab.");
        }

        SetARCameraActive(false);

        quizRoot.SetActive(true);
    }

    private IEnumerator ShowQuizAfterLoading(GameObject quizRoot)
    {
        CanvasGroup quizCG = quizRoot.GetComponent<CanvasGroup>();

        if (quizCG == null)
            quizCG = quizRoot.AddComponent<CanvasGroup>();

        quizCG.alpha = 0f;
        quizCG.blocksRaycasts = false;
        quizCG.interactable = false;

        yield return new WaitWhile(() =>
            QuizLoadingScreen.Instance != null && QuizLoadingScreen.Instance.IsShowing);

        yield return new WaitForSeconds(0.1f);

        quizCG.alpha = 1f;
        quizCG.blocksRaycasts = true;
        quizCG.interactable = true;
    }

    public void ExitQuiz()
    {
        quizManager = null;

        if (instantiatedObject != null)
        {
            Addressables.ReleaseInstance(instantiatedObject);
            instantiatedObject = null;
        }

        contentControl = null;
        _pageNode = null;
        _activePageId = null;
        _contentCompleted = false;
        _isLoading = false;
        _loadCancelled = false;

        HideAllUI();

        SetARCameraActive(true);

        Debug.Log("[AR] Quiz exited. AR camera resumed.");
    }

    private void SetARCameraActive(bool active)
    {
        if (_arCamera == null)
        {
#if UNITY_2023_1_OR_NEWER
            var cam = Object.FindFirstObjectByType<Vuforia.VuforiaBehaviour>();
#else
            var cam = Object.FindObjectOfType<Vuforia.VuforiaBehaviour>();
#endif
            if (cam != null)
                _arCamera = cam.gameObject;
        }

        if (_arCamera == null)
        {
            Debug.LogWarning("[AR] SetARCameraActive: ARCamera not found in scene.");
            return;
        }

        var vuforia = _arCamera.GetComponent<Vuforia.VuforiaBehaviour>();

        if (vuforia != null)
            vuforia.enabled = active;
    }

    private void SubscribeReveal()
    {
        ARVFXPopupController.OnRevealComplete -= OnVFXRevealComplete;
        ARVFXPopupController.OnRevealComplete += OnVFXRevealComplete;
    }

    private void UnsubscribeReveal()
    {
        ARVFXPopupController.OnRevealComplete -= OnVFXRevealComplete;
    }

    private void OnVFXRevealComplete(ARVFXPopupController controller)
    {
        if (_isDestroying || instantiatedObject == null || controller == null) return;

        if (!controller.transform.IsChildOf(instantiatedObject.transform) && controller.gameObject != instantiatedObject)
            return;

        UnsubscribeReveal();

        _trackHook?.SetPageNode(_pageNode);
    }

    public void OnVFXReplayStarting()
    {
        contentControl?.PauseContent();

        if (_arMediaManager != null)
            _arMediaManager.StopAudioForVFXReplay();

        _pageNode?.PrepareForReplay();
        _trackHook?.ClearForReplay();

        SubscribeReveal();

        _contentCompleted = false;

        StopNextPageAnim();
        nextPageImg?.SetActive(false);
    }

    public static void ReplayCurrent()
    {
        Current?.OnReplayButtonPressed();
    }

    public void OnReplayButtonPressed()
    {
        if (instantiatedObject == null) return;

        var vfxCtrl = instantiatedObject.GetComponentInChildren<ARVFXPopupController>(true);

        if (vfxCtrl != null)
        {
            _contentCompleted = false;

            StopNextPageAnim();
            nextPageImg?.SetActive(false);

            vfxCtrl.TriggerReplay();
            return;
        }

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

    private void ToggleRenderers(bool visible)
    {
        if (instantiatedObject == null) return;

        var renderers = instantiatedObject.GetComponentsInChildren<Renderer>(true);
        foreach (var r in renderers)
            r.enabled = visible;

        var canvas = instantiatedObject.GetComponentsInChildren<Canvas>(true);
        foreach (var c in canvas)
            c.enabled = visible;

        var videos = instantiatedObject.GetComponentsInChildren<VideoPlayer>(true);
        foreach (var v in videos)
        {
            if (v.targetMaterialRenderer != null)
                v.targetMaterialRenderer.enabled = visible;
        }

        var particles = instantiatedObject.GetComponentsInChildren<ParticleSystem>(true);
        foreach (var p in particles)
        {
            if (visible)
                p.Play();
            else
                p.Stop();
        }
    }
}