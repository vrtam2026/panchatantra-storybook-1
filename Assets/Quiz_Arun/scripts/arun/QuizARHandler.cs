using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.UI;
using Vuforia;

// ─────────────────────────────────────────────────────────────────────────────
// QuizARHandler.cs
// Attach to: Story_1_page_22-quiz (ImageTarget) -- REPLACES CustomARHandler
//
// Behavior:
//   1. User scans image target -> download starts immediately
//   2. QuizLoadingScreen shows (user can put phone down)
//   3. Tracking lost during download -> download continues (NOT cancelled)
//   4. Download complete -> quiz launches as 2D Screen Space Overlay
//   5. Exit button in quiz -> AR resumes, quiz canvas hides
// ─────────────────────────────────────────────────────────────────────────────

public class QuizARHandler : MonoBehaviour
{
    public static QuizARHandler Instance { get; private set; }

    [Header("Addressable")]
    [Tooltip("Must match the Addressable key exactly. e.g. Story_1_page_22-quiz")]
    public string addressableKey;

    [Header("Quiz Screen Canvas")]
    [Tooltip("Drag QuizScreenCanvas here (Screen Space Overlay, Sort Order 20). " +
             "This is the fullscreen container for the quiz UI.")]
    [SerializeField] private GameObject quizScreenCanvas;

    // ── Internal state ────────────────────────────────────────────────────────

    private bool _downloadStarted = false;
    private bool _downloadComplete = false;
    private bool _quizActive = false;

    private GameObject _instantiatedQuiz;
    private QuizManager _quizManager;
    private ObserverBehaviour _observer;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
    }

    private void Start()
    {
        _observer = GetComponent<ObserverBehaviour>();
        if (_observer != null)
            _observer.OnTargetStatusChanged += OnTargetStatusChanged;

        // Quiz screen hidden at launch
        SetQuizScreenVisible(false);

        if (string.IsNullOrEmpty(addressableKey))
            Debug.LogError("[QuizARHandler] Addressable Key is empty. Assign in Inspector.");
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;

        if (_observer != null)
            _observer.OnTargetStatusChanged -= OnTargetStatusChanged;
    }

    // ── Vuforia tracking callback ─────────────────────────────────────────────

    private void OnTargetStatusChanged(ObserverBehaviour behaviour, TargetStatus status)
    {
        bool tracked = status.Status == Status.TRACKED ||
                       status.Status == Status.EXTENDED_TRACKED ||
                       status.Status == Status.LIMITED;

        if (tracked)
            OnTrackingFound();

        // Tracking LOST is intentionally ignored:
        // - If download is in progress: let it finish
        // - If quiz is active: quiz runs on screen, camera not needed
    }

    private void OnTrackingFound()
    {
        // Ignore if download already started or quiz already running
        if (_downloadStarted || _downloadComplete || _quizActive) return;

        if (string.IsNullOrEmpty(addressableKey)) return;

        _downloadStarted = true;

        Debug.Log($"[QuizARHandler] Tracking found. Starting download: {addressableKey}");

        // Show quiz loading screen immediately
        QuizLoadingScreen.Show();

        // Start download -- this continues even if tracking is lost
        Addressables.InstantiateAsync(addressableKey).Completed += OnDownloadComplete;
    }

    // ── Download complete callback ─────────────────────────────────────────────

    private void OnDownloadComplete(AsyncOperationHandle<GameObject> handle)
    {
        _downloadStarted = false;

        // Always hide loading screen on completion (success or fail)
        QuizLoadingScreen.Hide();

        if (handle.Status != AsyncOperationStatus.Succeeded)
        {
            Debug.LogError($"[QuizARHandler] Download failed for '{addressableKey}'");
            _downloadComplete = false;
            return;
        }

        _downloadComplete = true;
        _instantiatedQuiz = handle.Result;

        Debug.Log("[QuizARHandler] Download complete. Launching quiz on screen.");

        // Parent the quiz to the screen canvas and set it up
        SetupQuizOnScreen(_instantiatedQuiz);

        // Find QuizManager and auto-start (no start button needed)
        _quizManager = _instantiatedQuiz.GetComponentInChildren<QuizManager>(true);

        if (_quizManager == null)
        {
            Debug.LogError("[QuizARHandler] QuizManager not found in downloaded prefab.");
            return;
        }

        _quizActive = true;
        _quizManager.StartQuiz();
    }

    // ── Quiz canvas setup ─────────────────────────────────────────────────────

    private void SetupQuizOnScreen(GameObject quizRoot)
    {
        if (quizScreenCanvas != null)
        {
            // Parent quiz content into the pre-built screen canvas
            quizRoot.transform.SetParent(quizScreenCanvas.transform, false);
            quizRoot.transform.localPosition = Vector3.zero;
            quizRoot.transform.localScale = Vector3.one;
            SetQuizScreenVisible(true);
            return;
        }

        // Fallback: change the prefab's own Canvas to Screen Space Overlay
        Canvas canvas = quizRoot.GetComponent<Canvas>();
        if (canvas == null)
            canvas = quizRoot.GetComponentInChildren<Canvas>(true);

        if (canvas != null)
        {
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 20;

            // Ensure CanvasScaler is set for screen scaling
            CanvasScaler scaler = canvas.GetComponent<CanvasScaler>();
            if (scaler == null)
                scaler = canvas.gameObject.AddComponent<CanvasScaler>();

            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080, 1920);
            scaler.matchWidthOrHeight = 0.5f;
        }
        else
        {
            Debug.LogWarning("[QuizARHandler] No Canvas found in quiz prefab. " +
                             "Assign QuizScreenCanvas in Inspector.");
        }

        quizRoot.SetActive(true);
    }

    private void SetQuizScreenVisible(bool visible)
    {
        if (quizScreenCanvas != null)
            quizScreenCanvas.SetActive(visible);
    }

    // ── Exit Quiz ─────────────────────────────────────────────────────────────
    // Wire this to the Exit button inside the quiz UI via ExitQuizButton.cs

    public void ExitQuiz()
    {
        Debug.Log("[QuizARHandler] ExitQuiz called. Returning to AR.");

        _quizActive = false;
        _quizManager = null;

        SetQuizScreenVisible(false);

        if (_instantiatedQuiz != null)
        {
            Addressables.ReleaseInstance(_instantiatedQuiz);
            _instantiatedQuiz = null;
        }

        // Reset state so user can re-trigger quiz by scanning again
        _downloadComplete = false;
        _downloadStarted = false;
    }
}