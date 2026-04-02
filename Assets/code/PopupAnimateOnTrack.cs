using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class PopupAnimateOnTrack : MonoBehaviour
{
    public enum PopupStyle
    {
        SoftKidPop,
        BouncyPop,
        JellyPop,
        HeroPop
    }

    public enum PlayMode
    {
        PopupThenAnimation,
        PopupAndAnimationTogether
    }

    [System.Serializable]
    public class ModelEntry
    {
        public Transform modelRoot;
        public Animator animator;
        public bool useCurrentModelScaleAsTarget = true;
        public Vector3 targetScale = Vector3.one;

        [HideInInspector] public Vector3 finalScale;
        [HideInInspector] public bool initialized;
    }

    [Header("Models")]
    [SerializeField] private List<ModelEntry> models = new List<ModelEntry>();

    [Header("Replay")]
    [SerializeField] private Button replayButton;

    [Header("Mode")]
    [SerializeField] private PlayMode playMode = PlayMode.PopupAndAnimationTogether;

    [Header("Style")]
    [SerializeField] private PopupStyle style = PopupStyle.SoftKidPop;

    [Header("Timing")]
    [SerializeField] private float duration = 0.55f;

    [Header("Scale")]
    [SerializeField] private Vector3 startScale = Vector3.zero;

    [Header("Behaviour")]
    [SerializeField] private bool resetOnLost = true;
    [SerializeField] private bool restartAnimationEachTrack = true;
    [SerializeField] private bool disableAnimatorOnLost = true;

    private Coroutine popupRoutine;

    private void Awake()
    {
        InitializeAll();
    }

    private void OnEnable()
    {
        HookReplayButton();
    }

    private void OnDisable()
    {
        UnhookReplayButton();
    }

    private void Start()
    {
        HideAllModelsAndStopAnimators();
    }

    private void HookReplayButton()
    {
        if (replayButton == null) return;

        replayButton.onClick.RemoveListener(ReplayPopupAndAnimation);
        replayButton.onClick.AddListener(ReplayPopupAndAnimation);
    }

    private void UnhookReplayButton()
    {
        if (replayButton == null) return;

        replayButton.onClick.RemoveListener(ReplayPopupAndAnimation);
    }

    private void InitializeAll()
    {
        for (int i = 0; i < models.Count; i++)
        {
            InitializeModel(models[i]);
        }
    }

    private void InitializeModel(ModelEntry model)
    {
        if (model == null || model.initialized) return;
        if (model.modelRoot == null) return;

        model.finalScale = model.useCurrentModelScaleAsTarget
            ? model.modelRoot.localScale
            : model.targetScale;

        model.initialized = true;
    }

    private void HideAllModelsAndStopAnimators()
    {
        for (int i = 0; i < models.Count; i++)
        {
            var model = models[i];
            if (model == null) continue;

            if (model.modelRoot != null)
                model.modelRoot.localScale = startScale;

            if (model.animator != null)
                model.animator.enabled = false;
        }
    }

    public void OnMarkerFound()
    {
        PlayPopupSequence();
    }

    public void OnMarkerLost()
    {
        if (popupRoutine != null)
        {
            StopCoroutine(popupRoutine);
            popupRoutine = null;
        }

        for (int i = 0; i < models.Count; i++)
        {
            var model = models[i];
            if (model == null) continue;

            if (model.animator != null && disableAnimatorOnLost)
                model.animator.enabled = false;

            if (resetOnLost && model.modelRoot != null)
                model.modelRoot.localScale = startScale;
        }
    }

    public void ReplayPopupAndAnimation()
    {
        PlayPopupSequence();
    }

    private void PlayPopupSequence()
    {
        InitializeAll();

        if (popupRoutine != null)
            StopCoroutine(popupRoutine);

        popupRoutine = StartCoroutine(PlayPopupFlow());
    }

    private IEnumerator PlayPopupFlow()
    {
        for (int i = 0; i < models.Count; i++)
        {
            var model = models[i];
            if (model == null || model.modelRoot == null) continue;

            model.modelRoot.localScale = startScale;
        }

        if (playMode == PlayMode.PopupAndAnimationTogether)
            StartAllAnimators();
        else
            StopAllAnimators();

        float time = 0f;

        while (time < duration)
        {
            time += Time.deltaTime;
            float t = Mathf.Clamp01(time / duration);

            Vector3 styleScale = GetStyleScale(t);

            for (int i = 0; i < models.Count; i++)
            {
                var model = models[i];
                if (model == null || model.modelRoot == null) continue;

                model.modelRoot.localScale = new Vector3(
                    model.finalScale.x * styleScale.x,
                    model.finalScale.y * styleScale.y,
                    model.finalScale.z * styleScale.z
                );
            }

            yield return null;
        }

        for (int i = 0; i < models.Count; i++)
        {
            var model = models[i];
            if (model == null || model.modelRoot == null) continue;

            model.modelRoot.localScale = model.finalScale;
        }

        if (playMode == PlayMode.PopupThenAnimation)
            StartAllAnimators();

        popupRoutine = null;
    }

    private void StartAllAnimators()
    {
        for (int i = 0; i < models.Count; i++)
        {
            var model = models[i];
            if (model == null || model.animator == null) continue;

            model.animator.enabled = true;

            if (restartAnimationEachTrack)
                model.animator.Play(0, 0, 0f);
        }
    }

    private void StopAllAnimators()
    {
        for (int i = 0; i < models.Count; i++)
        {
            var model = models[i];
            if (model == null || model.animator == null) continue;

            model.animator.enabled = false;
        }
    }

    private Vector3 GetStyleScale(float t)
    {
        switch (style)
        {
            case PopupStyle.SoftKidPop:
                return SoftKidPop(t);

            case PopupStyle.BouncyPop:
                return BouncyPop(t);

            case PopupStyle.JellyPop:
                return JellyPop(t);

            case PopupStyle.HeroPop:
                return HeroPop(t);

            default:
                return Vector3.one;
        }
    }

    private Vector3 SoftKidPop(float t)
    {
        float s;

        if (t < 0.35f)
            s = Mathf.Lerp(0f, 1.08f, t / 0.35f);
        else if (t < 0.65f)
            s = Mathf.Lerp(1.08f, 0.96f, (t - 0.35f) / 0.30f);
        else
            s = Mathf.Lerp(0.96f, 1f, (t - 0.65f) / 0.35f);

        return new Vector3(s, s, s);
    }

    private Vector3 BouncyPop(float t)
    {
        float s;

        if (t < 0.25f)
            s = Mathf.Lerp(0f, 1.18f, t / 0.25f);
        else if (t < 0.50f)
            s = Mathf.Lerp(1.18f, 0.93f, (t - 0.25f) / 0.25f);
        else if (t < 0.75f)
            s = Mathf.Lerp(0.93f, 1.04f, (t - 0.50f) / 0.25f);
        else
            s = Mathf.Lerp(1.04f, 1f, (t - 0.75f) / 0.25f);

        return new Vector3(s, s, s);
    }

    private Vector3 JellyPop(float t)
    {
        float xz;
        float y;

        if (t < 0.25f)
        {
            xz = Mathf.Lerp(0f, 1.12f, t / 0.25f);
            y = Mathf.Lerp(0f, 0.88f, t / 0.25f);
        }
        else if (t < 0.50f)
        {
            xz = Mathf.Lerp(1.12f, 0.95f, (t - 0.25f) / 0.25f);
            y = Mathf.Lerp(0.88f, 1.06f, (t - 0.25f) / 0.25f);
        }
        else
        {
            xz = Mathf.Lerp(0.95f, 1f, (t - 0.50f) / 0.50f);
            y = Mathf.Lerp(1.06f, 1f, (t - 0.50f) / 0.50f);
        }

        return new Vector3(xz, y, xz);
    }

    private Vector3 HeroPop(float t)
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
            xz = Mathf.Lerp(0.82f, 1.20f, (t - 0.22f) / 0.20f);
            y = Mathf.Lerp(0.38f, 1.22f, (t - 0.22f) / 0.20f);
        }
        else if (t < 0.62f)
        {
            xz = Mathf.Lerp(1.20f, 0.96f, (t - 0.42f) / 0.20f);
            y = Mathf.Lerp(1.22f, 1.04f, (t - 0.42f) / 0.20f);
        }
        else if (t < 0.82f)
        {
            xz = Mathf.Lerp(0.96f, 1.02f, (t - 0.62f) / 0.20f);
            y = Mathf.Lerp(1.04f, 0.99f, (t - 0.62f) / 0.20f);
        }
        else
        {
            xz = Mathf.Lerp(1.02f, 1f, (t - 0.82f) / 0.18f);
            y = Mathf.Lerp(0.99f, 1f, (t - 0.82f) / 0.18f);
        }

        return new Vector3(xz, y, xz);
    }
}