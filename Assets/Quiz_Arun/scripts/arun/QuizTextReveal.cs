using System;
using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// ─────────────────────────────────────────────────────────────────────────────
// QuizTextReveal.cs  —  Attach to Quiz_Panel.
//
// CORE RULE: Options are NEVER SetActive(false).
// They are hidden using CanvasGroup.alpha = 0 only.
// This means LayoutGroup never recalculates — zero position drift ever.
//
// Panel expand: localScale.x (0 → original). Text hidden during expand.
// ─────────────────────────────────────────────────────────────────────────────

public class QuizTextReveal : MonoBehaviour
{
    [Header("REFERENCES")]
    [Tooltip("Drag the 'Question' GameObject (yellow background panel).")]
    [SerializeField] private RectTransform questionContainer;

    [Tooltip("Drag Question_Text (TMP) here.")]
    [SerializeField] private TextMeshProUGUI questionText;

    [Tooltip("Element 0=Option1_Button  1=Option2_Button  2=Option3_Button  3=Option4_Button")]
    [SerializeField] private GameObject[] optionButtons;

    [Tooltip("Drag SubmitBtn here.")]
    [SerializeField] private GameObject submitButton;

    [Tooltip("Drag SkipBtn here.")]
    [SerializeField] private GameObject skipButton;

    [Tooltip("Drag Timer GameObject here.")]
    [SerializeField] private GameObject timerObject;

    [Header("SOUND EFFECTS")]
    [SerializeField] private AudioSource sfxSource;

    [Tooltip("Short tick — plays every word.")]
    [SerializeField] private AudioClip wordTickSound;

    [Tooltip("Pop — plays when each option finishes appearing.")]
    [SerializeField] private AudioClip optionPopSound;

    [Range(0f, 1f)][SerializeField] private float wordTickVolume = 0.35f;
    [Range(0f, 1f)][SerializeField] private float optionPopVolume = 0.7f;

    [Header("TIMING")]
    [Tooltip("Used only when Option Start Time = 0 on the question.\n60 = 60% question / 40% options.")]
    [Range(10, 90)]
    [SerializeField] private int questionPhasePercent = 60;

    [Tooltip("Used when question has no audio clip.")]
    [SerializeField] private float fallbackDuration = 8f;

    [Tooltip("Seconds before first word. 0 = start right after panel opens.")]
    [SerializeField] private float gapBeforeFirstWord = 0f;

    [Tooltip("Seconds after last word, before options start.")]
    [SerializeField] private float gapAfterQuestion = 0f;

    [Tooltip("Seconds after all options visible, before timer starts.")]
    [SerializeField] private float delayBeforeTimerStarts = 0.3f;

    [Header("SPEED")]
    [Tooltip("1=slowest(synced to audio)  5=fast  10=very fast")]
    [Range(1, 10)][SerializeField] private int textSpeed = 1;

    [Tooltip("1=slowest  5=fast  10=very fast")]
    [Range(1, 10)][SerializeField] private int optionSpeed = 3;

    [Header("WORD EFFECT")]
    [Tooltip("SlideUp = each word rises up into place. Fade = each word fades in.")]
    [SerializeField] private WordEffect wordEffect = WordEffect.SlideUp;

    public enum WordEffect { SlideUp, Fade }

    [Header("OPTION EFFECT")]
    [Tooltip("PopBounce = scale bounce + fade. SlideUp = slides from below + fade. FadeOnly = fade only.")]
    [SerializeField] private OptionEffect optionEffect = OptionEffect.PopBounce;

    [Tooltip("Pixels options travel for SlideUp style.")]
    [SerializeField] private float optionSlideDistance = 40f;

    public enum OptionEffect { PopBounce, SlideUp, FadeOnly }

    [Header("PANEL EXPAND")]
    [Tooltip("Seconds for panel to expand. 0.3=smooth  0.6=cinematic")]
    [SerializeField] private float panelExpandDuration = 0.4f;

    // ── Private ───────────────────────────────────────────────────────────────

    private Coroutine revealCoroutine;

    // ── Public API ────────────────────────────────────────────────────────────

    // coroutineHost: runs on QuizManager (stable inside Canvas)
    // If host is null or inactive, falls back to this object
    private MonoBehaviour coroutineHost;

    public void StartReveal(QuizQuestion question, AudioClip audio, Action onComplete, MonoBehaviour host = null)
    {
        // Pick active host — prefer the passed host, fall back to self
        coroutineHost = (host != null && host.gameObject.activeInHierarchy) ? host : this;

        if (revealCoroutine != null)
        {
            try { coroutineHost.StopCoroutine(revealCoroutine); } catch { }
            revealCoroutine = null;
        }

        // Read scale NOW before coroutine starts
        Vector3 originalScale = GetOriginalScale();

        revealCoroutine = coroutineHost.StartCoroutine(RevealSequence(question, audio, originalScale, onComplete));
        Debug.Log($"[QuizReveal] StartReveal on host={coroutineHost.name}");
    }

    public void CancelReveal()
    {
        if (revealCoroutine != null)
        {
            if (coroutineHost != null) coroutineHost.StopCoroutine(revealCoroutine);
            else StopCoroutine(revealCoroutine);
            revealCoroutine = null;
        }

        // Restore everything visible
        ShowText(true);
        SetAllOptionsAlpha(1f);

        // Restore panel if it was collapsed
        if (questionContainer != null)
        {
            Vector3 s = questionContainer.localScale;
            if (s.x < 0.01f)
                questionContainer.localScale = new Vector3(s.y > 0.01f ? s.y : 2.15f, s.y, s.z);
        }
    }

    // ── Reveal ────────────────────────────────────────────────────────────────

    private IEnumerator RevealSequence(QuizQuestion question, AudioClip audio, Vector3 originalScale, Action onComplete)
    {
        // Scale was already read in StartReveal() before this coroutine started.
        // Wait one frame for any pending UI events to settle.
        yield return null;

        // ── 1. Hide options (alpha only — keeps layout stable) ────────────────
        SetAllOptionsAlpha(0f);
        Show(timerObject, false);
        Show(skipButton, false);
        Show(submitButton, false);
        ShowText(false);

        // Collapse panel
        if (questionContainer != null)
            questionContainer.localScale = new Vector3(0f, originalScale.y, originalScale.z);

        yield return null;

        // ── 2. Calculate timing ───────────────────────────────────────────────
        float total = (audio != null && audio.length > 0.1f) ? audio.length : fallbackDuration;
        float qPhase, oPhase;

        if (question != null && question.optionStartTime > 0f)
        {
            qPhase = Mathf.Clamp(question.optionStartTime, 0.1f, total - 0.5f);
            oPhase = total - qPhase;
        }
        else
        {
            float split = questionPhasePercent / 100f;
            qPhase = total * split;
            oPhase = total - qPhase;
        }

        float wordTime = Mathf.Max(0.1f, qPhase - gapBeforeFirstWord - panelExpandDuration);
        float optTime = Mathf.Max(0.4f, oPhase - gapAfterQuestion - delayBeforeTimerStarts);
        int btnCount = optionButtons != null ? optionButtons.Length : 4;
        float slot = optTime / Mathf.Max(1, btnCount);
        int wordCount = GetWordCount(question);
        float wordInterval = Mathf.Max(0.02f, (wordTime / Mathf.Max(1, wordCount)) / textSpeed);
        float popTime = Mathf.Max(0.1f, (slot * 0.5f) / optionSpeed);
        float waitBetween = Mathf.Max(0f, slot - popTime);

        // ── 3. Gap before first word ──────────────────────────────────────────
        if (gapBeforeFirstWord > 0f)
            yield return new WaitForSeconds(gapBeforeFirstWord);

        // ── 4. Expand panel left to right ─────────────────────────────────────
        float elapsed = 0f;
        while (elapsed < panelExpandDuration)
        {
            elapsed += Time.deltaTime;
            float t = 1f - Mathf.Pow(1f - Mathf.Clamp01(elapsed / panelExpandDuration), 3f);
            if (questionContainer != null)
                questionContainer.localScale = new Vector3(originalScale.x * t, originalScale.y, originalScale.z);
            yield return null;
        }
        if (questionContainer != null)
            questionContainer.localScale = originalScale;

        // ── 5. Show text + reveal word by word ────────────────────────────────
        ShowText(true);

        if (question != null && questionText != null && !string.IsNullOrEmpty(question.questionText))
        {
            string[] words = question.questionText.Split(
                new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string built = "";
            foreach (string word in words)
            {
                built += (built.Length > 0 ? " " : "") + word;
                questionText.text = built;
                PlaySFX(wordTickSound, wordTickVolume);

                if (wordEffect == WordEffect.SlideUp)
                    yield return StartCoroutine(SlideUpWord(wordInterval));
                else
                    yield return new WaitForSeconds(wordInterval);
            }
        }

        // ── 6. Gap after question ─────────────────────────────────────────────
        if (gapAfterQuestion > 0f)
            yield return new WaitForSeconds(gapAfterQuestion);

        // ── 7. Options pop in one by one ──────────────────────────────────────
        if (optionButtons != null)
        {
            for (int i = 0; i < optionButtons.Length; i++)
            {
                if (optionButtons[i] == null) continue;

                yield return StartCoroutine(PopOption(optionButtons[i], popTime));
                PlaySFX(optionPopSound, optionPopVolume);

                if (waitBetween > 0f)
                    yield return new WaitForSeconds(waitBetween);
            }
        }

        // ── 8. Show skip, submit, timer ───────────────────────────────────────
        Show(skipButton, true);
        if (question != null && question.IsMultiAnswer)
            Show(submitButton, true);
        Show(timerObject, true);

        // ── 9. Delay then callback ────────────────────────────────────────────
        if (delayBeforeTimerStarts > 0f)
            yield return new WaitForSeconds(delayBeforeTimerStarts);

        revealCoroutine = null;
        onComplete?.Invoke();
    }

    // ── Pop Option — alpha 0→1 + scale 0→1.12→1.0 ────────────────────────────
    // Scale is on the button's own localScale.
    // This is safe because LayoutGroup uses RectTransform size, not localScale.

    // ── Option pop in ─────────────────────────────────────────────────────────

    private IEnumerator PopOption(GameObject btn, float duration)
    {
        if (btn == null) yield break;

        RectTransform rt = btn.GetComponent<RectTransform>();
        CanvasGroup cg = btn.GetComponent<CanvasGroup>();
        if (cg == null) cg = btn.AddComponent<CanvasGroup>();

        Vector2 originalPos = rt != null ? rt.anchoredPosition : Vector2.zero;

        cg.alpha = 0f;

        switch (optionEffect)
        {
            case OptionEffect.PopBounce:
                btn.transform.localScale = Vector3.zero;
                break;
            case OptionEffect.SlideUp:
                btn.transform.localScale = Vector3.one;
                if (rt != null) rt.anchoredPosition = originalPos - new Vector2(0f, optionSlideDistance);
                break;
            case OptionEffect.FadeOnly:
                btn.transform.localScale = Vector3.one;
                break;
        }

        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / duration);
            float eased = 1f - Mathf.Pow(1f - t, 3f); // ease out cubic

            cg.alpha = eased;

            switch (optionEffect)
            {
                case OptionEffect.PopBounce:
                    float s;
                    if (t < 0.6f) { float p = t / 0.6f; s = (1f - Mathf.Pow(1f - p, 3f)) * 1.12f; }
                    else { float p = (t - 0.6f) / 0.4f; s = Mathf.Lerp(1.12f, 1f, 1f - Mathf.Pow(1f - p, 3f)); }
                    btn.transform.localScale = new Vector3(s, s, 1f);
                    break;
                case OptionEffect.SlideUp:
                    if (rt != null)
                        rt.anchoredPosition = Vector2.LerpUnclamped(
                            originalPos - new Vector2(0f, optionSlideDistance),
                            originalPos, eased);
                    break;
            }

            yield return null;
        }

        cg.alpha = 1f;
        btn.transform.localScale = Vector3.one;
        if (rt != null) rt.anchoredPosition = originalPos;
    }

    // ── Word slide up — text drops slightly then rises as word appears ─────────

    private IEnumerator SlideUpWord(float totalTime)
    {
        if (questionText == null) { yield return new WaitForSeconds(totalTime); yield break; }

        float slideDuration = Mathf.Min(0.12f, totalTime * 0.6f);
        float holdTime = totalTime - slideDuration;
        float dropAmount = 6f; // pixels to drop before rising

        RectTransform rt = questionText.GetComponent<RectTransform>();
        Vector2 origPos = rt != null ? rt.anchoredPosition : Vector2.zero;

        // Instant drop
        if (rt != null) rt.anchoredPosition = origPos - new Vector2(0f, dropAmount);

        // Rise back up smoothly
        float elapsed = 0f;
        while (elapsed < slideDuration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / slideDuration);
            float eased = 1f - Mathf.Pow(1f - t, 3f);
            if (rt != null)
                rt.anchoredPosition = Vector2.LerpUnclamped(
                    origPos - new Vector2(0f, dropAmount), origPos, eased);
            yield return null;
        }

        if (rt != null) rt.anchoredPosition = origPos;

        if (holdTime > 0f)
            yield return new WaitForSeconds(holdTime);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void SetAllOptionsAlpha(float alpha)
    {
        if (optionButtons == null) return;
        foreach (GameObject btn in optionButtons)
        {
            if (btn == null) continue;
            CanvasGroup cg = btn.GetComponent<CanvasGroup>();
            if (cg == null) cg = btn.AddComponent<CanvasGroup>();
            cg.alpha = alpha;
            // Also reset scale when hiding
            if (alpha <= 0f) btn.transform.localScale = Vector3.zero;
            else btn.transform.localScale = Vector3.one;
        }
    }

    private void ShowText(bool visible)
    {
        if (questionText != null)
            questionText.gameObject.SetActive(visible);
    }

    private void Show(GameObject obj, bool active)
    {
        if (obj != null) obj.SetActive(active);
    }

    private void PlaySFX(AudioClip clip, float vol)
    {
        if (sfxSource != null && clip != null)
            sfxSource.PlayOneShot(clip, vol);
    }

    private int GetWordCount(QuizQuestion q)
    {
        if (q == null || string.IsNullOrEmpty(q.questionText)) return 1;
        return q.questionText.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Length;
    }

    private Vector3 GetOriginalScale()
    {
        if (questionContainer == null) return new Vector3(2.15f, 2.15f, 2.15f);
        Vector3 s = questionContainer.localScale;
        if (s.x < 0.01f)
        {
            float v = s.y > 0.01f ? s.y : 2.15f;
            return new Vector3(v, s.y > 0.01f ? s.y : 2.15f, s.z > 0.01f ? s.z : 2.15f);
        }
        return s;
    }
}