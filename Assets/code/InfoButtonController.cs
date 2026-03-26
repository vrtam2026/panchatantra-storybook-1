using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using System.Collections;

[RequireComponent(typeof(Button))]
[RequireComponent(typeof(CanvasGroup))]
public class InfoButtonController : MonoBehaviour
{
    private Button button;
    private CanvasGroup canvasGroup;
    private RectTransform rectTransform;

    [Header("Visibility Settings")]
    [Tooltip("How long the button stays visible before hiding (in seconds).")]
    [SerializeField] private float visibilityDuration = 5f;
    [Tooltip("How long the transition lasts.")]
    [SerializeField] private float transitionDuration = 0.35f;
    [Tooltip("The scale the button starts from when hidden.")]
    [SerializeField] private float hiddenScale = 0.4f;
    [Tooltip("How much the button 'pops' past its target size (0 to 1).")]
    [Range(0f, 1f)]
    [SerializeField] private float overshootAmount = 0.3f;

    [Header("Feedback Settings")]
    [Tooltip("How much the button pulses when clicked.")]
    [SerializeField] private float clickPulseScale = 0.1f;
    [SerializeField] private float pulseDuration = 0.1f;

    private float visibilityTimer = 0f;
    private bool isVisible = false;
    private Coroutine currentTransition;
    private Coroutine currentPulse;

    void Start()
    {
        canvasGroup = GetComponent<CanvasGroup>();
        button = GetComponent<Button>();
        rectTransform = GetComponent<RectTransform>();

        // Auto-center pivot logic to ensure animation is perfectly centered
        Vector2 oldPivot = rectTransform.pivot;
        Vector2 newPivot = new Vector2(0.5f, 0.5f);
        Vector2 deltaPivot = newPivot - oldPivot;
        Vector3 deltaPosition = new Vector3(deltaPivot.x * rectTransform.rect.width, deltaPivot.y * rectTransform.rect.height, 0);
        rectTransform.pivot = newPivot;
        rectTransform.anchoredPosition += (Vector2)deltaPosition;

        button.onClick.AddListener(OnButtonClicked);

        // Initial state
        canvasGroup.alpha = 0f;
        rectTransform.localScale = Vector3.one * hiddenScale;
        SetVisibilityState(false);
    }

    void Update()
    {
        // Detect screen click (background)
        if (Pointer.current != null && Pointer.current.press.wasPressedThisFrame)
        {
            if (EventSystem.current != null && !EventSystem.current.IsPointerOverGameObject())
            {
                ShowButton();
            }
        }

        // Auto-hide timer
        if (isVisible)
        {
            visibilityTimer -= Time.deltaTime;
            if (visibilityTimer <= 0f)
            {
                HideButton();
            }
        }
    }

    public void ShowButton()
    {
        if (isVisible)
        {
            visibilityTimer = visibilityDuration;
            return;
        }

        SetVisibilityState(true);
        visibilityTimer = visibilityDuration;
        StartTransition(true);
    }

    public void HideButton()
    {
        if (!isVisible) return;

        SetVisibilityState(false);
        StartTransition(false);
    }

    private void SetVisibilityState(bool visible)
    {
        isVisible = visible;
        canvasGroup.interactable = visible;
        canvasGroup.blocksRaycasts = visible;
    }

    private void StartTransition(bool show)
    {
        if (currentTransition != null) StopCoroutine(currentTransition);
        currentTransition = StartCoroutine(TransitionRoutine(show));
    }

    private IEnumerator TransitionRoutine(bool show)
    {
        float startAlpha = canvasGroup.alpha;
        float targetAlpha = show ? 1f : 0f;

        Vector3 startScale = rectTransform.localScale;
        Vector3 targetScale = Vector3.one * (show ? 1f : hiddenScale);

        float elapsed = 0f;
        while (elapsed < transitionDuration)
        {
            elapsed += Time.deltaTime;
            float t = elapsed / transitionDuration;

            // Alpha: Smooth Ease-Out
            canvasGroup.alpha = Mathf.Lerp(startAlpha, targetAlpha, t * (2 - t));

            // Scale: Back-Out (Overshoot) for appearing, standard ease for disappearing
            float scaleT = show ? EaseOutBack(t) : t * (2 - t);
            rectTransform.localScale = Vector3.LerpUnclamped(startScale, targetScale, scaleT);

            yield return null;
        }

        canvasGroup.alpha = targetAlpha;
        rectTransform.localScale = targetScale;
    }

    // Professional Overshoot Easing Function
    private float EaseOutBack(float t)
    {
        float s = 1.70158f * overshootAmount * 5f; // Adjust intensity by overshootAmount
        t = t - 1;
        return t * t * ((s + 1) * t + s) + 1;
    }

    private void OnButtonClicked()
    {
        if (!isVisible) return;

        visibilityTimer = visibilityDuration; // Reset timer

        // Play feedback pulse
        if (currentPulse != null) StopCoroutine(currentPulse);
        currentPulse = StartCoroutine(PulseRoutine());
    }

    private IEnumerator PulseRoutine()
    {
        Vector3 originalScale = Vector3.one;
        Vector3 pulseScale = Vector3.one * (1f - clickPulseScale);

        float halfDuration = pulseDuration * 0.5f;

        // Shrink
        float elapsed = 0f;
        while (elapsed < halfDuration)
        {
            elapsed += Time.deltaTime;
            rectTransform.localScale = Vector3.Lerp(originalScale, pulseScale, elapsed / halfDuration);
            yield return null;
        }

        // Return to normal
        elapsed = 0f;
        while (elapsed < halfDuration)
        {
            elapsed += Time.deltaTime;
            rectTransform.localScale = Vector3.Lerp(pulseScale, originalScale, elapsed / halfDuration);
            yield return null;
        }

        rectTransform.localScale = originalScale;
    }
}
