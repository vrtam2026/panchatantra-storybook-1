using System.Collections;
using UnityEngine;

public class KidsPopupStyles : MonoBehaviour
{
    public enum PopupStyle
    {
        BouncyPop,
        JellyPop,
        HeroPop
    }

    [Header("Style")]
    public PopupStyle style = PopupStyle.BouncyPop;

    [Header("Timing")]
    public float delay = 0.5f;
    public float duration = 0.6f;

    [Header("Loop Test")]
    public bool loop = false;
    public float loopDelay = 0.5f;

    [Header("Start Scale")]
    public Vector3 startScale = Vector3.zero;

    [Header("Animator")]
    public Animator animator;
    public bool playAnimatorAfterPopup = true;

    private Vector3 finalScale;

    void Awake()
    {
        finalScale = transform.localScale;

        if (animator != null)
            animator.enabled = false;
    }

    void Start()
    {
        StartCoroutine(PopupRoutine());
    }

    IEnumerator PopupRoutine()
    {
        do
        {
            transform.localScale = startScale;

            if (animator != null)
                animator.enabled = false;

            yield return new WaitForSeconds(delay);

            float time = 0f;

            while (time < duration)
            {
                time += Time.deltaTime;
                float t = Mathf.Clamp01(time / duration);

                Vector3 scaleValue = GetStyleScale(t);

                transform.localScale = new Vector3(
                    finalScale.x * scaleValue.x,
                    finalScale.y * scaleValue.y,
                    finalScale.z * scaleValue.z
                );

                yield return null;
            }

            transform.localScale = finalScale;

            if (playAnimatorAfterPopup && animator != null)
            {
                animator.enabled = true;
                animator.Play(0, 0, 0f);
            }

            if (loop)
                yield return new WaitForSeconds(loopDelay);

        } while (loop);
    }

    Vector3 GetStyleScale(float t)
    {
        switch (style)
        {
            case PopupStyle.BouncyPop:
                return BouncyPop(t);

            case PopupStyle.JellyPop:
                return JellyPop(t);

            case PopupStyle.HeroPop:
                return HeroPop(t);
        }

        return Vector3.one;
    }

    Vector3 BouncyPop(float t)
    {
        float s;

        if (t < 0.25f)
            s = Mathf.Lerp(0f, 1.25f, t / 0.25f);
        else if (t < 0.45f)
            s = Mathf.Lerp(1.25f, 0.88f, (t - 0.25f) / 0.20f);
        else if (t < 0.65f)
            s = Mathf.Lerp(0.88f, 1.10f, (t - 0.45f) / 0.20f);
        else if (t < 0.85f)
            s = Mathf.Lerp(1.10f, 0.96f, (t - 0.65f) / 0.20f);
        else
            s = Mathf.Lerp(0.96f, 1f, (t - 0.85f) / 0.15f);

        return new Vector3(s, s, s);
    }

    Vector3 JellyPop(float t)
    {
        float xz;
        float y;

        if (t < 0.20f)
        {
            xz = Mathf.Lerp(0f, 1.35f, t / 0.20f);
            y = Mathf.Lerp(0f, 0.65f, t / 0.20f);
        }
        else if (t < 0.40f)
        {
            xz = Mathf.Lerp(1.35f, 0.75f, (t - 0.20f) / 0.20f);
            y = Mathf.Lerp(0.65f, 1.25f, (t - 0.20f) / 0.20f);
        }
        else if (t < 0.65f)
        {
            xz = Mathf.Lerp(0.75f, 1.12f, (t - 0.40f) / 0.25f);
            y = Mathf.Lerp(1.25f, 0.92f, (t - 0.40f) / 0.25f);
        }
        else
        {
            xz = Mathf.Lerp(1.12f, 1f, (t - 0.65f) / 0.35f);
            y = Mathf.Lerp(0.92f, 1f, (t - 0.65f) / 0.35f);
        }

        return new Vector3(xz, y, xz);
    }

    Vector3 HeroPop(float t)
    {
        float xz;
        float y;

        if (t < 0.12f)
        {
            xz = Mathf.Lerp(0f, 0.70f, t / 0.12f);
            y = Mathf.Lerp(0f, 0.45f, t / 0.12f);
        }
        else if (t < 0.22f)
        {
            xz = Mathf.Lerp(0.70f, 0.82f, (t - 0.12f) / 0.10f);
            y = Mathf.Lerp(0.45f, 0.38f, (t - 0.12f) / 0.10f);
        }
        else if (t < 0.42f)
        {
            xz = Mathf.Lerp(0.82f, 1.28f, (t - 0.22f) / 0.20f);
            y = Mathf.Lerp(0.38f, 1.34f, (t - 0.22f) / 0.20f);
        }
        else if (t < 0.62f)
        {
            xz = Mathf.Lerp(1.28f, 0.94f, (t - 0.42f) / 0.20f);
            y = Mathf.Lerp(1.34f, 1.06f, (t - 0.42f) / 0.20f);
        }
        else if (t < 0.82f)
        {
            xz = Mathf.Lerp(0.94f, 1.03f, (t - 0.62f) / 0.20f);
            y = Mathf.Lerp(1.06f, 0.98f, (t - 0.62f) / 0.20f);
        }
        else
        {
            xz = Mathf.Lerp(1.03f, 1f, (t - 0.82f) / 0.18f);
            y = Mathf.Lerp(0.98f, 1f, (t - 0.82f) / 0.18f);
        }

        return new Vector3(xz, y, xz);
    }
}