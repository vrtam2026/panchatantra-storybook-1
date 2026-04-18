using UnityEngine;
using Vuforia;

// ─────────────────────────────────────────────────────────────────────────────
// QuizObserver.cs
// Attach to quiz_story_1 (the ImageTarget GameObject).
// Fully replaces DefaultObserverEventHandler with all its functionality PLUS
// pause/resume quiz logic.
//
// INSPECTOR SETUP:
//   Quiz Canvas              → drag "Canvas" (child of quiz_story_1)
//   Quiz Manager             → drag "Quiz_Panel" (has QuizManager)
//   Visible When             → choose tracking status threshold (default: Tracked)
//   Use Smooth Transition    → matches DefaultObserverEventHandler's option
// ─────────────────────────────────────────────────────────────────────────────

public class QuizObserver : MonoBehaviour
{
    // ── Inspector ─────────────────────────────────────────────────────────────

    [Header("References")]
    [Tooltip("The Canvas that contains the entire quiz UI. Child of quiz_story_1.")]
    [SerializeField] private GameObject quizCanvas;

    [Tooltip("Quiz_Panel GameObject — the one with QuizManager attached.")]
    [SerializeField] private QuizManager quizManager;

    [Header("Tracking Settings")]
    [Tooltip("Tracked = image fully in view only.\nExtended Tracked = stays visible when image partially/not in view.\nLimited = includes low-confidence tracking.")]
    [SerializeField] private StatusFilter visibleWhen = StatusFilter.Tracked;

    [Tooltip("Use smooth transition on pose jump — matches DefaultObserverEventHandler option.")]
    [SerializeField] private bool useSmoothTransition = false;

    // ── Status Filter Enum — mirrors DefaultObserverEventHandler ──────────────

    public enum StatusFilter
    {
        Tracked,
        ExtendedTracked,
        Limited
    }

    // ── Internal ──────────────────────────────────────────────────────────────

    private ObserverBehaviour observer;
    private bool quizStarted = false;
    private bool isVisible = false;

    // ── Coroutine Runner — used by QuizManager ────────────────────────────
    // quiz_story_1 is NEVER deactivated by Vuforia — safe to run coroutines here

    public Coroutine RunCoroutine(System.Collections.IEnumerator routine)
    {
        return StartCoroutine(routine);
    }

    public void StopRunningCoroutine(Coroutine c)
    {
        if (c != null) StopCoroutine(c);
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void Start()
    {
        if (quizCanvas == null)
            Debug.LogError("[QuizObserver] Quiz Canvas not assigned.");
        if (quizManager == null)
            Debug.LogError("[QuizObserver] Quiz Manager not assigned.");

        // Hide canvas at start — shown only when target detected
        SetCanvasVisible(false);

        observer = GetComponent<ObserverBehaviour>();
        if (observer != null)
            observer.OnTargetStatusChanged += OnTargetStatusChanged;
        else
            Debug.LogError("[QuizObserver] No ObserverBehaviour on this GameObject.");
    }

    private void OnDestroy()
    {
        if (observer != null)
            observer.OnTargetStatusChanged -= OnTargetStatusChanged;
    }

    // ── Vuforia Callback ──────────────────────────────────────────────────────

    private void OnTargetStatusChanged(ObserverBehaviour behaviour, TargetStatus status)
    {
        bool shouldBeVisible = IsVisible(status);

        if (shouldBeVisible && !isVisible)
        {
            isVisible = true;
            OnTargetFound();
        }
        else if (!shouldBeVisible && isVisible)
        {
            isVisible = false;
            OnTargetLost();
        }

        // Smooth transition — logged only, no API call needed
        if (useSmoothTransition)
            Debug.Log("[QuizObserver] Smooth transition enabled.");
    }

    // ── Visibility Check — mirrors DefaultObserverEventHandler exactly ─────────

    private bool IsVisible(TargetStatus status)
    {
        switch (visibleWhen)
        {
            case StatusFilter.Tracked:
                return status.Status == Status.TRACKED;

            case StatusFilter.ExtendedTracked:
                return status.Status == Status.TRACKED ||
                       status.Status == Status.EXTENDED_TRACKED;

            case StatusFilter.Limited:
                // LIMITED status — includes weak/low-confidence tracking
                return status.Status == Status.TRACKED ||
                       status.Status == Status.EXTENDED_TRACKED ||
                       status.Status == Status.LIMITED;

            default:
                return false;
        }
    }

    // ── Target Found ──────────────────────────────────────────────────────────

    private void OnTargetFound()
    {
        SetCanvasVisible(true);

        if (!quizStarted)
        {
            // First detection — show canvas only.
            // StartPanel is visible. User must click StartButton to begin quiz.
            // StartButton calls quizManager.StartQuiz() directly.
            // quizStarted is set to true by MarkQuizStarted() called from QuizManager.
        }
        else
        {
            // Re-detection — resume from exact paused state
            if (quizManager != null)
                quizManager.PauseQuiz(false);
        }
    }

    // ── Called by QuizManager.StartQuiz() ─────────────────────────────────

    public void MarkQuizStarted()
    {
        quizStarted = true;
    }

    // ── Target Lost ───────────────────────────────────────────────────────────

    private void OnTargetLost()
    {
        // Freeze everything in place — timer, audio, video
        if (quizManager != null)
            quizManager.PauseQuiz(true);
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    private CanvasGroup canvasGroup;

    private void SetCanvasVisible(bool visible)
    {
        if (quizCanvas == null) return;

        // Keep Canvas always active — use CanvasGroup to show/hide
        // SetActive(false) kills coroutines inside Canvas permanently
        if (!quizCanvas.activeSelf)
            quizCanvas.SetActive(true);

        if (canvasGroup == null)
            canvasGroup = quizCanvas.GetComponent<CanvasGroup>();
        if (canvasGroup == null)
            canvasGroup = quizCanvas.AddComponent<CanvasGroup>();

        canvasGroup.alpha = visible ? 1f : 0f;
        canvasGroup.interactable = visible;
        canvasGroup.blocksRaycasts = visible;
    }
}