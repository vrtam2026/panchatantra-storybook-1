using System.Collections;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
// QuizFeedback.cs  —  Attach to Quiz_Panel.
//
// Designed for age 5-12. Animations are fluid, natural, and minimal.
// Uses sine waves and spring easing — no robotic back-and-forth.
// ─────────────────────────────────────────────────────────────────────────────

public class QuizFeedback : MonoBehaviour
{
    // ── Wrong Feedback ────────────────────────────────────────────────────────

    [Header("WRONG FEEDBACK")]
    [Tooltip("Jiggle  = natural sine-wave wobble, decays to rest (most fluid)\n" +
             "Shake   = left-right with shrink — feels like 'oops'\n" +
             "Tilt    = gentle rotation tilt — soft and friendly")]
    [SerializeField] private WrongStyle wrongStyle = WrongStyle.Jiggle;

    [Tooltip("Total duration of wrong animation. 0.5=quick  0.8=slow")]
    [SerializeField] private float wrongDuration = 0.6f;

    [Tooltip("How strong the wrong animation is. Higher = more movement.")]
    [SerializeField] private float wrongStrength = 10f;

    public enum WrongStyle { Jiggle, Shake, Tilt }

    // ── Correct Feedback ──────────────────────────────────────────────────────

    [Header("CORRECT FEEDBACK")]
    [Tooltip("SpringPop  = grows fast, overshoots, springs back — feels alive\n" +
             "Heartbeat  = two quick gentle pulses — warm and satisfying\n" +
             "Celebrate  = grows + gentle sway — joyful")]
    [SerializeField] private CorrectStyle correctStyle = CorrectStyle.SpringPop;

    [Tooltip("Total duration of correct animation. 0.5=quick  0.8=slow")]
    [SerializeField] private float correctDuration = 0.6f;

    [Tooltip("Peak scale during correct animation. 1.2 = 20% bigger.")]
    [Range(1.05f, 1.4f)]
    [SerializeField] private float correctScale = 1.2f;

    public enum CorrectStyle { SpringPop, Heartbeat, Celebrate }

    // ── Public API ────────────────────────────────────────────────────────────

    public void PlayWrong(GameObject btn)
    {
        if (btn != null) StartCoroutine(WrongAnim(btn));
    }

    public void PlayCorrect(GameObject btn)
    {
        if (btn != null) StartCoroutine(CorrectAnim(btn));
    }

    // ═════════════════════════════════════════════════════════════════════════
    // WRONG ANIMATIONS
    // ═════════════════════════════════════════════════════════════════════════

    private IEnumerator WrongAnim(GameObject btn)
    {
        switch (wrongStyle)
        {
            case WrongStyle.Jiggle: yield return StartCoroutine(Jiggle(btn)); break;
            case WrongStyle.Shake: yield return StartCoroutine(Shake(btn)); break;
            case WrongStyle.Tilt: yield return StartCoroutine(Tilt(btn)); break;
        }
    }

    // ── Jiggle: sine-wave decay — most natural and fluid ─────────────────────
    // Oscillates like a spring that naturally comes to rest.
    private IEnumerator Jiggle(GameObject btn)
    {
        RectTransform rt = btn.GetComponent<RectTransform>();
        if (rt == null) yield break;

        Vector2 orig = rt.anchoredPosition;
        float elapsed = 0f;
        float freq = 18f; // oscillations per second

        while (elapsed < wrongDuration)
        {
            elapsed += Time.deltaTime;
            float t = elapsed / wrongDuration;
            float decay = 1f - t;                              // linear decay to zero
            float offset = Mathf.Sin(elapsed * freq) * wrongStrength * decay;
            rt.anchoredPosition = orig + new Vector2(offset, 0f);
            yield return null;
        }

        rt.anchoredPosition = orig;
    }

    // ── Shake: horizontal with slight shrink — oops feeling ──────────────────
    private IEnumerator Shake(GameObject btn)
    {
        RectTransform rt = btn.GetComponent<RectTransform>();
        if (rt == null) yield break;

        Vector2 orig = rt.anchoredPosition;
        float elapsed = 0f;
        float freq = 14f;

        while (elapsed < wrongDuration)
        {
            elapsed += Time.deltaTime;
            float t = elapsed / wrongDuration;
            float decay = 1f - t;
            float offset = Mathf.Sin(elapsed * freq) * wrongStrength * decay;

            // Slight shrink as it shakes
            float shrink = 1f - 0.06f * Mathf.Sin(t * Mathf.PI);

            rt.anchoredPosition = orig + new Vector2(offset, 0f);
            rt.localScale = new Vector3(shrink, shrink, 1f);
            yield return null;
        }

        rt.anchoredPosition = orig;
        rt.localScale = Vector3.one;
    }

    // ── Tilt: gentle rotation — soft and friendly ─────────────────────────────
    private IEnumerator Tilt(GameObject btn)
    {
        RectTransform rt = btn.GetComponent<RectTransform>();
        if (rt == null) yield break;

        float elapsed = 0f;
        float freq = 10f;

        while (elapsed < wrongDuration)
        {
            elapsed += Time.deltaTime;
            float t = elapsed / wrongDuration;
            float decay = 1f - t;
            float angle = Mathf.Sin(elapsed * freq) * wrongStrength * 1.5f * decay;
            rt.localRotation = Quaternion.Euler(0f, 0f, angle);
            yield return null;
        }

        rt.localRotation = Quaternion.identity;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // CORRECT ANIMATIONS
    // ═════════════════════════════════════════════════════════════════════════

    private IEnumerator CorrectAnim(GameObject btn)
    {
        switch (correctStyle)
        {
            case CorrectStyle.SpringPop: yield return StartCoroutine(SpringPop(btn)); break;
            case CorrectStyle.Heartbeat: yield return StartCoroutine(Heartbeat(btn)); break;
            case CorrectStyle.Celebrate: yield return StartCoroutine(Celebrate(btn)); break;
        }
    }

    // ── SpringPop: grows fast, overshoots, springs to rest — feels alive ──────
    // Uses dampened spring formula: scale oscillates and decays to 1.
    private IEnumerator SpringPop(GameObject btn)
    {
        RectTransform rt = btn.GetComponent<RectTransform>();
        if (rt == null) yield break;

        float elapsed = 0f;
        float freq = 14f;  // spring frequency
        float damping = 5f;   // how fast it settles

        while (elapsed < correctDuration)
        {
            elapsed += Time.deltaTime;
            // Dampened spring: starts at 0, peaks at correctScale, settles at 1
            float spring = 1f + (correctScale - 1f)
                * Mathf.Exp(-damping * elapsed)
                * Mathf.Cos(freq * elapsed);

            rt.localScale = new Vector3(spring, spring, 1f);
            yield return null;
        }

        rt.localScale = Vector3.one;
    }

    // ── Heartbeat: two quick gentle pulses — warm and satisfying ─────────────
    private IEnumerator Heartbeat(GameObject btn)
    {
        RectTransform rt = btn.GetComponent<RectTransform>();
        if (rt == null) yield break;

        float beatTime = correctDuration * 0.4f;
        float restTime = correctDuration * 0.1f;
        float peak = correctScale;
        float midPeak = 1f + (correctScale - 1f) * 0.6f;

        // First beat (bigger)
        yield return StartCoroutine(ScaleTo(rt, 1f, peak, beatTime * 0.4f));
        yield return StartCoroutine(ScaleTo(rt, peak, 1f, beatTime * 0.6f));
        yield return new WaitForSeconds(restTime);

        // Second beat (softer)
        yield return StartCoroutine(ScaleTo(rt, 1f, midPeak, beatTime * 0.4f));
        yield return StartCoroutine(ScaleTo(rt, midPeak, 1f, beatTime * 0.6f));

        rt.localScale = Vector3.one;
    }

    private IEnumerator ScaleTo(RectTransform rt, float from, float to, float duration)
    {
        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / duration);
            float ease = t < 0.5f
                ? 2f * t * t
                : 1f - Mathf.Pow(-2f * t + 2f, 2f) / 2f; // smooth ease in-out
            float s = Mathf.Lerp(from, to, ease);
            rt.localScale = new Vector3(s, s, 1f);
            yield return null;
        }
    }

    // ── Celebrate: grows + gentle sway — joyful ──────────────────────────────
    private IEnumerator Celebrate(GameObject btn)
    {
        RectTransform rt = btn.GetComponent<RectTransform>();
        if (rt == null) yield break;

        float elapsed = 0f;
        float growEnd = correctDuration * 0.35f;

        // Grow up smoothly
        while (elapsed < growEnd)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / growEnd);
            float s = Mathf.Lerp(1f, correctScale, 1f - Mathf.Pow(1f - t, 3f));
            rt.localScale = new Vector3(s, s, 1f);
            yield return null;
        }

        // Sway gently while big, then shrink back
        float swayTime = correctDuration * 0.65f;
        float swayElapsed = 0f;
        float freq = 8f;

        while (swayElapsed < swayTime)
        {
            swayElapsed += Time.deltaTime;
            float t = swayElapsed / swayTime;
            float decay = 1f - t;

            // Scale gently shrinks back to 1
            float s = Mathf.Lerp(correctScale, 1f, t);
            float angle = Mathf.Sin(swayElapsed * freq) * 6f * decay;

            rt.localScale = new Vector3(s, s, 1f);
            rt.localRotation = Quaternion.Euler(0f, 0f, angle);
            yield return null;
        }

        rt.localScale = Vector3.one;
        rt.localRotation = Quaternion.identity;
    }
}