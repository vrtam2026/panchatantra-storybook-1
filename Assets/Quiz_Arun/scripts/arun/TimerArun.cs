using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.Events;

// ─────────────────────────────────────────────────────────────────────────────
// QuizTimer.cs  —  Attach to Timer GameObject.
// ─────────────────────────────────────────────────────────────────────────────

public class QuizTimer : MonoBehaviour
{
    [Header("SETTINGS")]
    public float timerDuration = 15f;

    [Header("DISPLAY")]
    public TextMeshProUGUI timerText;

    [Header("EVENT — wire QuizManager.OnTimerExpired() here")]
    public UnityEvent OnTimerEnd;

    [Header("HEARTBEAT")]
    [Tooltip("Heartbeat starts when this many seconds remain.")]
    public float heartbeatAt = 10f;

    [Tooltip("Urgent fast pulse starts when this many seconds remain.")]
    public float urgentAt = 3f;

    [Tooltip("Change text color during heartbeat and urgent phases.")]
    public bool changeTextColor = false;

    [Tooltip("Timer text color during heartbeat phase.")]
    public Color heartbeatColor = new Color(1f, 0.55f, 0f);

    [Tooltip("Timer text color during urgent phase.")]
    public Color urgentColor = Color.red;

    [Tooltip("Change panel background color during heartbeat and urgent phases.")]
    public bool changePanelColor = false;

    [Tooltip("Timer panel color during heartbeat phase.")]
    public Color heartbeatPanelColor = new Color(1f, 0.85f, 0.5f);

    [Tooltip("Timer panel color during urgent phase.")]
    public Color urgentPanelColor = new Color(1f, 0.3f, 0.3f);

    [Header("AUDIO")]
    [Tooltip("AudioSource on Timer GameObject.")]
    public AudioSource timerAudioSource;

    [Tooltip("Tick sound — plays every second during heartbeat and urgent phase.")]
    public AudioClip tickSound;

    [Header("STATE — Read Only")]
    public float timeRemaining;
    public bool isTimerPaused;

    // ── Private ───────────────────────────────────────────────────────────────

    private UnityEngine.UI.Image panelImage = null;
    private Color panelOriginalColor;
    private bool panelColorCached = false;
    private Coroutine timerCoroutine = null;
    private Coroutine heartbeatCoroutine = null;
    private Color originalColor;
    private Vector3 originalScale;
    private bool colorCached = false;
    private bool scaleCached = false;

    private void Awake()
    {
        // Cache scale immediately — before anything can change it
        RectTransform rt = GetComponent<RectTransform>();
        if (rt != null)
        {
            originalScale = rt.localScale;
            scaleCached = true;
        }

        // Cache panel image
        panelImage = GetComponent<UnityEngine.UI.Image>();
        if (panelImage != null)
        {
            panelOriginalColor = panelImage.color;
            panelColorCached = true;
        }
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public void StartTimer()
    {
        StopTimer();
        timeRemaining = timerDuration;
        isTimerPaused = false;

        if (!colorCached && timerText != null)
        {
            originalColor = timerText.color;
            colorCached = true;
        }

        if (!scaleCached)
        {
            RectTransform rt = GetComponent<RectTransform>();
            originalScale = rt != null ? rt.localScale : Vector3.one;
            scaleCached = true;
        }

        ResetVisuals();
        UpdateDisplay();
        timerCoroutine = StartCoroutine(RunTimer());
    }

    public void StopTimer()
    {
        if (timerCoroutine != null) { StopCoroutine(timerCoroutine); timerCoroutine = null; }
        if (heartbeatCoroutine != null) { StopCoroutine(heartbeatCoroutine); heartbeatCoroutine = null; }
        ResetVisuals();
    }

    public void PauseTimer(bool pause) { isTimerPaused = pause; }

    public void ResetDisplay()
    {
        StopTimer();
        timeRemaining = timerDuration;
        isTimerPaused = false;
        ResetVisuals();
        UpdateDisplay();
    }

    // ── Timer Loop ────────────────────────────────────────────────────────────

    private IEnumerator RunTimer()
    {
        while (timeRemaining > 0f)
        {
            while (isTimerPaused) yield return null;

            UpdateDisplay();
            TriggerHeartbeat();

            yield return new WaitForSeconds(1f);
            timeRemaining = Mathf.Max(0f, timeRemaining - 1f);
        }

        timeRemaining = 0f;
        UpdateDisplay();
        StopTimer();
        OnTimerEnd?.Invoke();
    }

    // ── Heartbeat ─────────────────────────────────────────────────────────────

    private void TriggerHeartbeat()
    {
        if (timeRemaining > heartbeatAt) return;

        bool urgent = timeRemaining <= urgentAt;

        if (changeTextColor && timerText != null)
            timerText.color = urgent ? urgentColor : heartbeatColor;
        if (changePanelColor && panelImage != null)
            panelImage.color = urgent ? urgentPanelColor : heartbeatPanelColor;

        // Play tick sound
        if (timerAudioSource != null)
        {
            if (tickSound != null) timerAudioSource.PlayOneShot(tickSound);
        }

        if (heartbeatCoroutine != null) StopCoroutine(heartbeatCoroutine);
        heartbeatCoroutine = StartCoroutine(
            Pop(urgent ? 1.3f : 1.15f, urgent ? 0.08f : 0.15f));
    }

    private IEnumerator Pop(float peakScale, float duration)
    {
        RectTransform rt = GetComponent<RectTransform>();
        if (rt == null) yield break;

        float elapsed = 0f;
        float freq = 12f;
        float damping = 9f;

        while (elapsed < duration * 5f)
        {
            elapsed += Time.deltaTime;
            float springVal = Mathf.Max(0f, (peakScale - 1f)
                * Mathf.Exp(-damping * elapsed)
                * Mathf.Cos(freq * elapsed));
            Vector3 baseScale = scaleCached ? originalScale : Vector3.one;
            rt.localScale = baseScale * (1f + springVal);
            yield return null;
        }

        rt.localScale = scaleCached ? originalScale : Vector3.one;
        heartbeatCoroutine = null;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void ResetVisuals()
    {
        RectTransform rt = GetComponent<RectTransform>();
        if (rt != null) rt.localScale = scaleCached ? originalScale : Vector3.one;
        if (timerText != null && colorCached) timerText.color = originalColor;
        if (panelImage != null && panelColorCached) panelImage.color = panelOriginalColor;
    }

    private void UpdateDisplay()
    {
        if (timerText != null)
            timerText.text = $"Timer :\n{Mathf.FloorToInt(timeRemaining)} secs";
    }
}