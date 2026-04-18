using UnityEngine;
using UnityEngine.UI;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.Video;
using UnityEngine.InputSystem;
using UnityEngine.EventSystems;
using System.Collections.Generic;
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

    [Header("UI Elements")]
    public GameObject replayButton;
    public GameObject nextPageImg;
    public GameObject backBtn;
    public GameObject sliderV;

    [Header("Auto Hide Settings")]
    [Tooltip("Seconds before UI auto hides after last tap on empty space.")]
    public float autoHideSeconds = 3f;

    [Tooltip("How fast UI fades in and out in seconds.")]
    public float fadeDuration = 0.3f;

    private bool _uiVisible = false;
    private float _autoHideTimer = 0f;
    private Coroutine _fadeRoutine;

    private CanvasGroup _replayCG;
    private CanvasGroup _backBtnCG;
    private CanvasGroup _sliderCG;

    private Coroutine _nextPageAnimRoutine;
    private Vector2 _nextPageImgOriginalPos;

    private VuforiaTrackHook _trackHook;
    private ARTrackedPageNode _pageNode;

    // Reusable raycast list — avoids GC allocation every frame
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

        SetUIAlpha(0f);
        SetUIInteractable(false);
    }

    void Start()
    {
        HideAllUI();
        _trackHook = GetComponent<VuforiaTrackHook>();

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

    void OnDestroy()
    {
        var observer = GetComponent<ObserverBehaviour>();
        if (observer != null)
            observer.OnTargetStatusChanged -= OnTargetStatusChanged;
    }

    void Update()
    {
        bool tapped = false;
        Vector2 tapPosition = Vector2.zero;

        if (Touchscreen.current != null && Touchscreen.current.primaryTouch.press.wasPressedThisFrame)
        {
            tapped = true;
            tapPosition = Touchscreen.current.primaryTouch.position.ReadValue();
        }

        if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
        {
            tapped = true;
            tapPosition = Mouse.current.position.ReadValue();
        }

        if (tapped)
        {
            bool onUI = IsTapOnUIElement(tapPosition);

            if (onUI)
            {
                // Tapped a UI element (slider, button etc) — reset timer, keep UI visible
                // The UI element handles its own interaction normally
                if (_uiVisible)
                    _autoHideTimer = autoHideSeconds;
            }
            else
            {
                // Tapped empty space — toggle UI
                if (_uiVisible)
                    FadeUI(false);
                else
                {
                    FadeUI(true);
                    _autoHideTimer = autoHideSeconds;
                }
            }
        }

        // Auto hide countdown
        if (_uiVisible)
        {
            _autoHideTimer -= Time.deltaTime;
            if (_autoHideTimer <= 0f)
                FadeUI(false);
        }
    }

    // ----------------------------------------------------------------------
    // Touch on UI detection
    // Checks if the tap position landed on any UI element
    // If yes, slider/buttons handle their own input normally
    // ----------------------------------------------------------------------

    private bool IsTapOnUIElement(Vector2 screenPosition)
    {
        var eventSystem = EventSystem.current;
        if (eventSystem == null) return false;

        var pointerData = new PointerEventData(eventSystem)
        {
            position = screenPosition
        };

        _raycastResults.Clear();
        eventSystem.RaycastAll(pointerData, _raycastResults);

        return _raycastResults.Count > 0;
    }

    // ----------------------------------------------------------------------
    // Voice completed — fired by ARMediaManager with the pageId that finished
    // ----------------------------------------------------------------------

    private void OnVoiceCompleted(string completedPageId)
    {
        // Only react if the completed page matches this handler's page
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
    }

    private void SetUIInteractable(bool state)
    {
        if (_replayCG != null) { _replayCG.interactable = state; _replayCG.blocksRaycasts = state; }
        if (_backBtnCG != null) { _backBtnCG.interactable = state; _backBtnCG.blocksRaycasts = state; }
        if (_sliderCG != null) { _sliderCG.interactable = state; _sliderCG.blocksRaycasts = state; }
    }

    private void FadeUI(bool show)
    {
        _uiVisible = show;

        if (show)
        {
            backBtn?.SetActive(true);
            sliderV?.SetActive(true);
            if (_contentCompleted)
                replayButton?.SetActive(true);
            SetUIInteractable(true);
        }
        else
        {
            SetUIInteractable(false);
        }

        if (_fadeRoutine != null) StopCoroutine(_fadeRoutine);
        _fadeRoutine = StartCoroutine(FadeRoutine(show ? 1f : 0f));
    }

    private System.Collections.IEnumerator FadeRoutine(float targetAlpha)
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
            backBtn?.SetActive(false);
            sliderV?.SetActive(false);
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
        nextPageImg?.SetActive(false);
        backBtn?.SetActive(false);
        sliderV?.SetActive(false);

        SetUIAlpha(0f);
        SetUIInteractable(false);

        _uiVisible = false;
        _autoHideTimer = 0f;
    }

    // ----------------------------------------------------------------------
    // Vuforia tracking
    // ----------------------------------------------------------------------

    private void OnTargetStatusChanged(ObserverBehaviour behaviour, TargetStatus status)
    {
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

        if (instantiatedObject == null && !_isLoading)
        {
            _isLoading = true;
            _loadCancelled = false;
            _contentCompleted = false;

            StopNextPageAnim();
            nextPageImg?.SetActive(false);

            LoadingScreen.Show();

            Addressables.InstantiateAsync(addressableKey, transform).Completed += handle =>
            {
                _isLoading = false;
                LoadingScreen.Hide();

                if (_loadCancelled)
                {
                    Debug.Log($"[AR] Load cancelled for '{addressableKey}' — releasing.");
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
                modelInteraction = GetComponent<ModelInteraction>();
                modelInteraction?.Init(instantiatedObject);

                var components = instantiatedObject.GetComponentsInChildren<QuizManager>(true);
                if (components.Length > 0)
                    quizManager = components[0];

                quizManager?.PauseQuiz(false);

                _pageNode = instantiatedObject.GetComponentInChildren<ARTrackedPageNode>();
                _trackHook?.SetPageNode(_pageNode);

                contentControl?.PlayContent();

                // NOTE: ContentController completion callback removed intentionally
                // NextPageImg is triggered ONLY by voice audio completion via ARMediaManager.OnVoiceCompleted
                // This prevents early triggers when video ends before voice finishes
            };
        }
        else if (instantiatedObject != null)
        {
            ToggleRenderers(true);
            modelInteraction?.Init(instantiatedObject);
            quizManager?.PauseQuiz(false);
            _trackHook?.SetPageNode(_pageNode);
            contentControl?.PlayContent();

            if (_contentCompleted)
                nextPageImg?.SetActive(true);
        }
    }

    private void OnTrackingLost()
    {
        if (Current == this) Current = null;

        if (_isLoading)
        {
            _loadCancelled = true;
            LoadingScreen.Hide();
            HideAllUI();
            return;
        }

        if (instantiatedObject != null)
        {
            contentControl?.PauseContent();
            quizManager?.PauseQuiz(true);
            _trackHook?.ClearPageNode();

            ToggleRenderers(false);

            Addressables.ReleaseInstance(instantiatedObject);
            instantiatedObject = null;
            contentControl = null;
            modelInteraction = null;
            quizManager = null;
            _pageNode = null;

            HideAllUI();
        }
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

    private System.Collections.IEnumerator NextPageAnimRoutine()
    {
        if (nextPageImg == null) yield break;

        RectTransform rt = nextPageImg.GetComponent<RectTransform>();
        if (rt == null) yield break;

        // Pop in: scale 0 to 1.2
        nextPageImg.transform.localScale = Vector3.zero;
        float elapsed = 0f;

        while (elapsed < 0.15f)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / 0.15f);
            nextPageImg.transform.localScale = Vector3.one * Mathf.Lerp(0f, 1.2f, t);
            yield return null;
        }

        // Settle: scale 1.2 to 1
        elapsed = 0f;
        while (elapsed < 0.1f)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / 0.1f);
            nextPageImg.transform.localScale = Vector3.one * Mathf.Lerp(1.2f, 1f, t);
            yield return null;
        }

        nextPageImg.transform.localScale = Vector3.one;

        // Loop bounce left
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
    // Replay
    // ----------------------------------------------------------------------

    public static void ReplayCurrent()
    {
        Current?.OnReplayButtonPressed();
    }

    public void OnReplayButtonPressed()
    {
        if (contentControl == null) return;

        _contentCompleted = false;
        StopNextPageAnim();
        nextPageImg?.SetActive(false);

        contentControl.ReplayContent();
    }

    // ----------------------------------------------------------------------
    // Renderer toggle
    // ----------------------------------------------------------------------

    private void ToggleRenderers(bool visible)
    {
        if (instantiatedObject == null) return;

        var renderers = instantiatedObject.GetComponentsInChildren<Renderer>();
        foreach (var r in renderers) r.enabled = visible;
        var canvas = instantiatedObject.GetComponentsInChildren<Canvas>();
        foreach (var c in canvas) c.enabled = visible;

        var videos = instantiatedObject.GetComponentsInChildren<VideoPlayer>(true);
        foreach (var v in videos) v.enabled = visible;

        var particles = instantiatedObject.GetComponentsInChildren<ParticleSystem>(true);
        foreach (var p in particles)
        {
            if (visible) p.Play();
            else p.Stop();
        }
    }
}