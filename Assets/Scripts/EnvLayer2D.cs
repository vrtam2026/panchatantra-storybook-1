using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Vuforia;

#if UNITY_EDITOR
using UnityEditor;
#endif

public class EnvLayer2D : MonoBehaviour
{
    public enum FadeType
    {
        SoftAppear,
        PagePop,
        BrushReveal,
        BloomIn,
        CurtainReveal,
        LiftReveal,
        StorySpark
    }

    public enum MotionType
    {
        None,
        StoryBreath,
        LeafFlutter,
        CloudSwell,
        SproutLife,
        WaterRipple,
        MagicBloom,
        StoryAttention,
        TinyBounce
    }

    [Serializable]
    public class LayerSettings
    {
        [Header("Layer")]
        public Transform layerTransform;

        [Header("Fade")]
        public FadeType fadeType = FadeType.SoftAppear;

        [Range(0f, 6f)]
        public float fadeTime = 1.4f;

        [Range(0f, 6f)]
        public float delayBeforeFade = 0f;

        [Range(0f, 6f)]
        public float delayAfterFade = 0.3f;

        [Header("Motion")]
        public MotionType motionType = MotionType.None;

        [Range(0f, 6f)]
        public float motionSpeed = 1f;

        [Range(0f, 6f)]
        public float motionIntensity = 2f;

        [Range(0f, 6f)]
        public float motionSmoothing = 4f;

        [Header("Reset Defaults")]
        public bool resetFadeDefaults;
        public bool resetMotionDefaults;

        [HideInInspector] public int lastFadeType = -1;
        [HideInInspector] public int lastMotionType = -1;
    }

    private sealed class RendererAlphaEntry
    {
        public Renderer renderer;
        public int materialIndex;
        public int colorPropertyId;
        public Color baseColor;
        public MaterialPropertyBlock propertyBlock;
    }

    private sealed class RuntimeLayerState
    {
        public Transform transform;
        public Vector3 originalLocalScale;

        public SpriteRenderer[] spriteRenderers;
        public Color[] spriteBaseColors;

        public Graphic[] graphics;
        public Color[] graphicBaseColors;

        public CanvasGroup[] canvasGroups;
        public float[] canvasGroupBaseAlphas;

        public List<RendererAlphaEntry> rendererAlphaEntries = new List<RendererAlphaEntry>();

        public float fadeAlphaFactor = 0f;
        public Vector3 fadeScaleFactor = Vector3.one;
        public Vector3 motionScaleFactor = Vector3.one;
    }

    private struct FadeResult
    {
        public float alphaFactor;
        public Vector3 scaleFactor;

        public FadeResult(float alphaFactor, Vector3 scaleFactor)
        {
            this.alphaFactor = alphaFactor;
            this.scaleFactor = scaleFactor;
        }
    }

    private struct MotionResult
    {
        public Vector3 scaleFactor;

        public MotionResult(Vector3 scaleFactor)
        {
            this.scaleFactor = scaleFactor;
        }
    }

    [Header("Tracking")]
    [SerializeField] private ObserverBehaviour observerBehaviour;

    [Tooltip("Keep this OFF unless LIMITED Vuforia tracking should count as valid tracking.")]
    [SerializeField] private bool acceptLimitedTracking = false;

    [Tooltip("Prevents tiny tracking flickers from restarting the fade. Recommended: 0.2")]
    [Range(0f, 6f)]
    [SerializeField] private float fullLostConfirmTime = 0.2f;

    [Header("Playback")]
    [Tooltip("OFF = layers fade one by one. ON = all layers fade and start motion at the same time.")]
    [SerializeField] private bool startAllLayersTogether = false;

    [Header("Layers")]
    [SerializeField] private List<LayerSettings> layers = new List<LayerSettings>();

    [Header("Debug")]
    [SerializeField] private bool logWarnings = true;

    private readonly List<RuntimeLayerState> runtimeStates = new List<RuntimeLayerState>();

    private Coroutine sequenceCoroutine;

    private bool isTracking;
    private bool trackingSessionActive;
    private bool hasStartedForCurrentTracking;
    private bool isSubscribed;
    private bool cacheBuilt;
    private bool pendingExternalFadeTrigger;
    private bool warnedMissingObserver;

    private bool fullLostCandidate;
    private float fullLostTimer;

#if UNITY_EDITOR
    private bool editorPreviewActive;
    private double editorPreviewStartTime;
    private double editorPreviewLastTime;
#endif

    private static readonly int ColorId = Shader.PropertyToID("_Color");
    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

    private void Awake()
    {
        if (!Application.isPlaying)
            return;

        RebuildRuntimeCache(true);
        HideAllLayersImmediate();
    }

    private void OnEnable()
    {
        if (!Application.isPlaying)
            return;

        RebuildRuntimeCache(true);
        HideAllLayersImmediate();

        ResolveObserverBehaviour();
        SubscribeToObserver();
        PollTrackingState();

        if (pendingExternalFadeTrigger)
        {
            pendingExternalFadeTrigger = false;
            HandleTrackingFound();
        }
    }

    private void OnDisable()
    {
        if (!Application.isPlaying)
            return;

        UnsubscribeFromObserver();
        StopAllCoroutines();

        sequenceCoroutine = null;

        isTracking = false;
        trackingSessionActive = false;
        hasStartedForCurrentTracking = false;

        fullLostCandidate = false;
        fullLostTimer = 0f;

        HideAllLayersImmediate();
    }

    private void OnDestroy()
    {
        UnsubscribeFromObserver();
    }

    private void Update()
    {
        if (!Application.isPlaying)
            return;

        if (observerBehaviour == null)
        {
            ResolveObserverBehaviour();
            SubscribeToObserver();
        }

        PollTrackingState();

        if (pendingExternalFadeTrigger)
        {
            pendingExternalFadeTrigger = false;
            HandleTrackingFound();
        }
    }

    public void TriggerFadeIn()
    {
#if UNITY_EDITOR
        if (!Application.isPlaying)
        {
            EditorTestFadeAndMotion();
            return;
        }
#endif

        if (!isActiveAndEnabled)
        {
            pendingExternalFadeTrigger = true;
            return;
        }

        HandleTrackingFound();
    }

    public void TriggerHideImmediate()
    {
#if UNITY_EDITOR
        if (!Application.isPlaying)
        {
            EditorStopTestAndRestore();
            return;
        }
#endif

        HandleTrackingFullyLost();
    }

    private void ResolveObserverBehaviour()
    {
        if (observerBehaviour != null)
            return;

        observerBehaviour = GetComponentInParent<ObserverBehaviour>(true);

        if (observerBehaviour == null && logWarnings && !warnedMissingObserver)
        {
            warnedMissingObserver = true;
            Debug.LogWarning("[EnvLayer2D] No Vuforia ObserverBehaviour found. Auto tracking will not work unless TriggerFadeIn is called externally.", this);
        }
    }

    private void SubscribeToObserver()
    {
        if (observerBehaviour == null || isSubscribed)
            return;

        observerBehaviour.OnTargetStatusChanged += OnTargetStatusChanged;
        isSubscribed = true;
    }

    private void UnsubscribeFromObserver()
    {
        if (observerBehaviour == null || !isSubscribed)
            return;

        observerBehaviour.OnTargetStatusChanged -= OnTargetStatusChanged;
        isSubscribed = false;
    }

    private void OnTargetStatusChanged(ObserverBehaviour behaviour, TargetStatus targetStatus)
    {
        ProcessTrackingStatus(targetStatus);
    }

    private void PollTrackingState()
    {
        if (observerBehaviour == null)
            return;

        ProcessTrackingStatus(observerBehaviour.TargetStatus);
    }

    private void ProcessTrackingStatus(TargetStatus targetStatus)
    {
        if (IsTrackingStatusValid(targetStatus))
        {
            HandleTrackingFound();
            return;
        }

        if (IsTrackingFullyLost(targetStatus))
        {
            RegisterFullLostCandidate();
            return;
        }

        fullLostCandidate = false;
        fullLostTimer = 0f;
    }

    private bool IsTrackingStatusValid(TargetStatus targetStatus)
    {
        if (targetStatus.Status == Status.TRACKED)
            return true;

        if (targetStatus.Status == Status.EXTENDED_TRACKED)
            return true;

        if (acceptLimitedTracking && targetStatus.Status == Status.LIMITED)
            return true;

        return false;
    }

    private bool IsTrackingFullyLost(TargetStatus targetStatus)
    {
        return targetStatus.Status == Status.NO_POSE;
    }

    private void RegisterFullLostCandidate()
    {
        if (!trackingSessionActive && !hasStartedForCurrentTracking)
            return;

        if (!fullLostCandidate)
        {
            fullLostCandidate = true;
            fullLostTimer = 0f;
        }

        fullLostTimer += Time.deltaTime;

        if (fullLostConfirmTime <= 0f || fullLostTimer >= fullLostConfirmTime)
            HandleTrackingFullyLost();
    }

    private void HandleTrackingFound()
    {
        isTracking = true;
        trackingSessionActive = true;

        fullLostCandidate = false;
        fullLostTimer = 0f;

        if (hasStartedForCurrentTracking)
            return;

        StartFadeSequenceFromBeginning();
    }

    private void HandleTrackingFullyLost()
    {
        if (!trackingSessionActive && !hasStartedForCurrentTracking)
            return;

        isTracking = false;
        trackingSessionActive = false;
        hasStartedForCurrentTracking = false;

        fullLostCandidate = false;
        fullLostTimer = 0f;

        StopAllCoroutines();
        sequenceCoroutine = null;

        HideAllLayersImmediate();
    }

    private void StartFadeSequenceFromBeginning()
    {
        if (!isActiveAndEnabled)
        {
            pendingExternalFadeTrigger = true;
            return;
        }

        if (hasStartedForCurrentTracking)
            return;

        hasStartedForCurrentTracking = true;
        trackingSessionActive = true;
        isTracking = true;

        StopAllCoroutines();
        sequenceCoroutine = null;

        RebuildRuntimeCache(true);
        PrepareLayersForFreshSequence();

        sequenceCoroutine = StartCoroutine(FadeSequenceRoutine());
    }

    private IEnumerator FadeSequenceRoutine()
    {
        if (startAllLayersTogether)
        {
            yield return FadeAllLayersTogetherRoutine();
        }
        else
        {
            yield return FadeLayersSequentialRoutine();
        }

        sequenceCoroutine = null;
    }

    private IEnumerator FadeLayersSequentialRoutine()
    {
        for (int i = 0; i < layers.Count; i++)
        {
            if (!isTracking)
                break;

            if (!TryGetValidLayer(i, out LayerSettings layer, out RuntimeLayerState state))
                continue;

            yield return WaitWhileTracking(layer.delayBeforeFade);

            if (!isTracking)
                break;

            yield return FadeLayerRoutine(layer, state);

            if (!isTracking)
                break;

            StartMotionForLayer(layer, state);

            yield return WaitWhileTracking(layer.delayAfterFade);
        }
    }

    private IEnumerator FadeAllLayersTogetherRoutine()
    {
        float longestFadeTime = 0f;

        for (int i = 0; i < layers.Count; i++)
        {
            if (!TryGetValidLayer(i, out LayerSettings layer, out RuntimeLayerState state))
                continue;

            state.fadeAlphaFactor = 0f;
            state.fadeScaleFactor = Vector3.one;
            state.motionScaleFactor = Vector3.one;

            ApplyVisualState(state);

            float fadeTime = Mathf.Max(0.1f, layer.fadeTime);
            longestFadeTime = Mathf.Max(longestFadeTime, fadeTime);

            StartCoroutine(FadeLayerRoutine(layer, state));
            StartMotionForLayer(layer, state);
        }

        yield return WaitWhileTracking(longestFadeTime);
    }

    private IEnumerator FadeLayerRoutine(LayerSettings layer, RuntimeLayerState state)
    {
        if (layer == null || state == null || state.transform == null)
            yield break;

        float fadeTime = Mathf.Max(0.1f, layer.fadeTime);
        float elapsed = 0f;

        state.fadeAlphaFactor = 0f;
        state.fadeScaleFactor = Vector3.one;

        ApplyVisualState(state);

        while (elapsed < fadeTime)
        {
            if (!isTracking || state.transform == null)
                yield break;

            elapsed += Time.deltaTime;

            float t = Mathf.Clamp01(elapsed / fadeTime);
            FadeResult result = EvaluateFade(layer.fadeType, t);

            state.fadeAlphaFactor = result.alphaFactor;
            state.fadeScaleFactor = result.scaleFactor;

            ApplyVisualState(state);

            yield return null;
        }

        state.fadeAlphaFactor = 1f;
        state.fadeScaleFactor = Vector3.one;

        ApplyVisualState(state);
    }

    private IEnumerator WaitWhileTracking(float seconds)
    {
        if (seconds <= 0f)
            yield break;

        float elapsed = 0f;

        while (elapsed < seconds)
        {
            if (!isTracking)
                yield break;

            elapsed += Time.deltaTime;
            yield return null;
        }
    }

    private void StartMotionForLayer(LayerSettings layer, RuntimeLayerState state)
    {
        if (layer == null || state == null || state.transform == null)
            return;

        if (layer.motionType == MotionType.None)
            return;

        StartCoroutine(MotionRoutine(layer, state));
    }

    private IEnumerator MotionRoutine(LayerSettings layer, RuntimeLayerState state)
    {
        float motionTime = 0f;
        float seed = Mathf.Abs(state.transform.GetInstanceID() % 1000) * 0.01f;

        while (isTracking && layer != null && state != null && state.transform != null)
        {
            float effectiveSpeed = GetEffectiveMotionSpeed(layer.motionSpeed);
            motionTime += Time.deltaTime * effectiveSpeed;
            motionTime = Mathf.Repeat(motionTime, 1000f);

            MotionResult target = EvaluateMotion(layer.motionType, motionTime, layer.motionIntensity, seed);
            float damping = GetSmoothingDamping(layer.motionSmoothing, Time.deltaTime);

            state.motionScaleFactor = Vector3.Lerp(state.motionScaleFactor, target.scaleFactor, damping);

            ApplyVisualState(state);

            yield return null;
        }
    }

    private FadeResult EvaluateFade(FadeType fadeType, float t)
    {
        t = Mathf.Clamp01(t);

        switch (fadeType)
        {
            case FadeType.PagePop:
                {
                    float alpha = EaseOutCubic(t);
                    float pop = Mathf.Sin(t * Mathf.PI);
                    float scale = Mathf.Lerp(0.82f, 1f, EaseOutCubic(t)) + pop * 0.055f;

                    return new FadeResult(
                        Mathf.Clamp01(alpha),
                        Vector3.one * scale
                    );
                }

            case FadeType.BrushReveal:
                {
                    float alphaA = Mathf.SmoothStep(0f, 1f, t);
                    float alphaB = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((t - 0.22f) / 0.78f));
                    float brush = 0.94f + Mathf.Sin(t * Mathf.PI * 4.5f) * 0.06f;

                    float alpha = Mathf.Clamp01((alphaA * 0.55f + alphaB * 0.45f) * brush);

                    float x = Mathf.Lerp(0.88f, 1f, SmootherStep(t));
                    float y = Mathf.Lerp(1.06f, 1f, SmootherStep(t));

                    return new FadeResult(
                        alpha,
                        new Vector3(x, y, 1f)
                    );
                }

            case FadeType.BloomIn:
                {
                    float alphaA = 1f - Mathf.Pow(1f - t, 2.4f);
                    float alphaB = Mathf.SmoothStep(0f, 1f, t);
                    float alpha = alphaA * 0.6f + alphaB * 0.4f;

                    float bloom = Mathf.Sin(t * Mathf.PI) * 0.06f;
                    float scale = Mathf.Lerp(0.94f, 1f, SmootherStep(t)) + bloom;

                    return new FadeResult(
                        Mathf.Clamp01(alpha),
                        Vector3.one * scale
                    );
                }

            case FadeType.CurtainReveal:
                {
                    float alpha = Mathf.SmoothStep(0f, 1f, t);
                    float x = Mathf.Lerp(0.18f, 1f, EaseOutCubic(t));
                    float y = Mathf.Lerp(1.04f, 1f, SmootherStep(t));

                    return new FadeResult(
                        Mathf.Clamp01(alpha),
                        new Vector3(x, y, 1f)
                    );
                }

            case FadeType.LiftReveal:
                {
                    float alpha = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / 0.85f));
                    float x = Mathf.Lerp(0.96f, 1f, SmootherStep(t));
                    float y = Mathf.Lerp(0.35f, 1f, EaseOutCubic(t));

                    return new FadeResult(
                        Mathf.Clamp01(alpha),
                        new Vector3(x, y, 1f)
                    );
                }

            case FadeType.StorySpark:
                {
                    float alpha = Mathf.SmoothStep(0f, 1f, t);
                    float sparkle = 0.88f + Mathf.Sin(t * Mathf.PI * 9f) * 0.12f;
                    float finalAlpha = Mathf.Lerp(alpha * sparkle, 1f, t);

                    float pop = Mathf.Sin(t * Mathf.PI) * 0.075f;
                    float scale = Mathf.Lerp(0.9f, 1f, EaseOutCubic(t)) + pop;

                    return new FadeResult(
                        Mathf.Clamp01(finalAlpha),
                        Vector3.one * scale
                    );
                }

            case FadeType.SoftAppear:
            default:
                {
                    float alpha = Mathf.SmoothStep(0f, 1f, t);

                    return new FadeResult(
                        Mathf.Clamp01(alpha),
                        Vector3.one
                    );
                }
        }
    }

    private MotionResult EvaluateMotion(MotionType motionType, float time, float intensityValue, float seed)
    {
        float strength = ToMotionStrength(intensityValue);

        float waveA = Mathf.Sin((time + seed) * Mathf.PI * 2f);
        float waveB = Mathf.Sin((time * 0.41f + seed + 0.29f) * Mathf.PI * 2f);
        float waveC = Mathf.Sin((time * 1.37f + seed + 0.61f) * Mathf.PI * 2f);
        float waveD = Mathf.Sin((time * 2.7f + seed + 0.17f) * Mathf.PI * 2f);

        float softA = (waveA + 1f) * 0.5f;
        float softB = (waveB + 1f) * 0.5f;
        float softC = (waveC + 1f) * 0.5f;

        switch (motionType)
        {
            case MotionType.StoryBreath:
                {
                    float breath = Mathf.SmoothStep(0f, 1f, softB);
                    float scale = 1f + breath * 0.070f * strength;

                    return new MotionResult(Vector3.one * scale);
                }

            case MotionType.LeafFlutter:
                {
                    float flutter = waveC * 0.75f + waveD * 0.25f;

                    float x = 1f + flutter * 0.105f * strength;
                    float y = 1f - flutter * 0.035f * strength;

                    return new MotionResult(new Vector3(x, y, 1f));
                }

            case MotionType.CloudSwell:
                {
                    float puff = Mathf.SmoothStep(0f, 1f, softB);
                    float settle = Mathf.SmoothStep(0f, 1f, softC);

                    float x = 1f + puff * 0.115f * strength;
                    float y = 1f + settle * 0.035f * strength;

                    return new MotionResult(new Vector3(x, y, 1f));
                }

            case MotionType.SproutLife:
                {
                    float grow = Mathf.SmoothStep(0f, 1f, softA);
                    float sway = waveB;

                    float x = 1f + sway * 0.025f * strength;
                    float y = 1f + grow * 0.125f * strength;

                    return new MotionResult(new Vector3(x, y, 1f));
                }

            case MotionType.WaterRipple:
                {
                    float ripple = waveD * 0.7f + waveC * 0.3f;

                    float x = 1f + ripple * 0.160f * strength;
                    float y = 1f - ripple * 0.030f * strength;

                    return new MotionResult(new Vector3(x, y, 1f));
                }

            case MotionType.MagicBloom:
                {
                    float noiseA = Mathf.PerlinNoise(time * 1.3f + seed, 0.21f);
                    float noiseB = Mathf.PerlinNoise(time * 3.8f + seed, 0.77f);
                    float sparkle = Mathf.Clamp01(noiseA * 0.45f + noiseB * 0.55f);

                    float pulse = Mathf.Pow(sparkle, 1.8f);
                    float scale = 1f + pulse * 0.145f * strength;

                    return new MotionResult(Vector3.one * scale);
                }

            case MotionType.StoryAttention:
                {
                    float phase = Mathf.Repeat(time, 1f);

                    float beatOne = Mathf.Exp(-Mathf.Pow((phase - 0.16f) / 0.045f, 2f));
                    float beatTwo = Mathf.Exp(-Mathf.Pow((phase - 0.34f) / 0.070f, 2f)) * 0.55f;
                    float rest = Mathf.Clamp01(beatOne + beatTwo);

                    float scale = 1f + rest * 0.130f * strength;

                    return new MotionResult(Vector3.one * scale);
                }

            case MotionType.TinyBounce:
                {
                    float phase = Mathf.Repeat(time * 1.25f, 1f);
                    float bounce = Mathf.Sin(phase * Mathf.PI);
                    bounce = Mathf.Pow(Mathf.Max(0f, bounce), 2.4f);

                    float squashTime = Mathf.Clamp01(Mathf.Sin((phase + 0.15f) * Mathf.PI));
                    squashTime = Mathf.Pow(squashTime, 3f);

                    float x = 1f + squashTime * 0.065f * strength;
                    float y = 1f + bounce * 0.150f * strength;

                    return new MotionResult(new Vector3(x, y, 1f));
                }

            case MotionType.None:
            default:
                {
                    return new MotionResult(Vector3.one);
                }
        }
    }

    private float GetEffectiveMotionSpeed(float speedValue)
    {
        if (speedValue <= 0f)
            return 0f;

        return Mathf.Lerp(0.25f, 2.15f, To01(speedValue));
    }

    private float GetSmoothingDamping(float smoothingValue, float deltaTime)
    {
        float smoothing = To01(smoothingValue);
        float responsiveness = Mathf.Lerp(18f, 3.2f, smoothing);
        return 1f - Mathf.Exp(-responsiveness * Mathf.Max(0.001f, deltaTime));
    }

    private float SmootherStep(float t)
    {
        t = Mathf.Clamp01(t);
        return t * t * t * (t * (t * 6f - 15f) + 10f);
    }

    private float EaseOutCubic(float t)
    {
        t = Mathf.Clamp01(t);
        return 1f - Mathf.Pow(1f - t, 3f);
    }

    private static float To01(float value)
    {
        return Mathf.Clamp01(value / 6f);
    }

    private static float ToMotionStrength(float value)
    {
        if (value <= 0f)
            return 0f;

        return Mathf.Lerp(0.25f, 1f, Mathf.Clamp01(value / 6f));
    }

    private void RebuildRuntimeCache(bool force = false)
    {
        if (!force && cacheBuilt && runtimeStates.Count == layers.Count)
            return;

        runtimeStates.Clear();

        for (int i = 0; i < layers.Count; i++)
        {
            RuntimeLayerState state = null;

            try
            {
                LayerSettings layer = layers[i];

                if (layer == null || layer.layerTransform == null)
                {
                    if (logWarnings)
                        Debug.LogWarning("[EnvLayer2D] Layer " + i + " has no Transform. Skipping this layer.", this);

                    runtimeStates.Add(null);
                    continue;
                }

                state = BuildRuntimeState(layer.layerTransform);
            }
            catch (Exception exception)
            {
                if (logWarnings)
                    Debug.LogWarning("[EnvLayer2D] Failed to cache layer " + i + ". Skipping this layer. " + exception.Message, this);
            }

            runtimeStates.Add(state);
        }

        cacheBuilt = true;
    }

    private RuntimeLayerState BuildRuntimeState(Transform layerTransform)
    {
        RuntimeLayerState state = new RuntimeLayerState();

        state.transform = layerTransform;
        state.originalLocalScale = layerTransform.localScale;

        state.spriteRenderers = layerTransform.GetComponentsInChildren<SpriteRenderer>(true);
        state.spriteBaseColors = new Color[state.spriteRenderers.Length];

        for (int i = 0; i < state.spriteRenderers.Length; i++)
        {
            if (state.spriteRenderers[i] != null)
                state.spriteBaseColors[i] = state.spriteRenderers[i].color;
        }

        state.canvasGroups = layerTransform.GetComponentsInChildren<CanvasGroup>(true);
        state.canvasGroupBaseAlphas = new float[state.canvasGroups.Length];

        for (int i = 0; i < state.canvasGroups.Length; i++)
        {
            if (state.canvasGroups[i] != null)
                state.canvasGroupBaseAlphas[i] = state.canvasGroups[i].alpha;
        }

        if (state.canvasGroups.Length == 0)
        {
            state.graphics = layerTransform.GetComponentsInChildren<Graphic>(true);
            state.graphicBaseColors = new Color[state.graphics.Length];

            for (int i = 0; i < state.graphics.Length; i++)
            {
                if (state.graphics[i] != null)
                    state.graphicBaseColors[i] = state.graphics[i].color;
            }
        }
        else
        {
            state.graphics = new Graphic[0];
            state.graphicBaseColors = new Color[0];
        }

        Renderer[] renderers = layerTransform.GetComponentsInChildren<Renderer>(true);

        for (int r = 0; r < renderers.Length; r++)
        {
            Renderer renderer = renderers[r];

            if (renderer == null)
                continue;

            if (renderer is SpriteRenderer)
                continue;

            Material[] materials = renderer.sharedMaterials;

            for (int m = 0; m < materials.Length; m++)
            {
                Material material = materials[m];

                if (material == null)
                    continue;

                int propertyId = 0;

                if (material.HasProperty(BaseColorId))
                    propertyId = BaseColorId;
                else if (material.HasProperty(ColorId))
                    propertyId = ColorId;
                else
                    continue;

                RendererAlphaEntry entry = new RendererAlphaEntry();

                entry.renderer = renderer;
                entry.materialIndex = m;
                entry.colorPropertyId = propertyId;
                entry.baseColor = material.GetColor(propertyId);
                entry.propertyBlock = new MaterialPropertyBlock();

                state.rendererAlphaEntries.Add(entry);
            }
        }

        return state;
    }

    private bool TryGetValidLayer(int index, out LayerSettings layer, out RuntimeLayerState state)
    {
        layer = null;
        state = null;

        if (index < 0 || index >= layers.Count)
            return false;

        layer = layers[index];

        if (layer == null || layer.layerTransform == null)
        {
            if (logWarnings)
                Debug.LogWarning("[EnvLayer2D] Layer " + index + " has a missing Transform. Skipping.", this);

            return false;
        }

        if (index >= runtimeStates.Count)
            RebuildRuntimeCache(false);

        if (index >= runtimeStates.Count)
            return false;

        state = runtimeStates[index];

        if (state == null || state.transform == null)
        {
            if (logWarnings)
                Debug.LogWarning("[EnvLayer2D] Layer " + index + " has no valid runtime state. Skipping.", this);

            return false;
        }

        return true;
    }

    private void PrepareLayersForFreshSequence()
    {
        for (int i = 0; i < runtimeStates.Count; i++)
        {
            RuntimeLayerState state = runtimeStates[i];

            if (state == null || state.transform == null)
                continue;

            state.fadeAlphaFactor = 0f;
            state.fadeScaleFactor = Vector3.one;
            state.motionScaleFactor = Vector3.one;

            state.transform.localScale = state.originalLocalScale;

            ApplyVisualState(state);
        }
    }

    private void HideAllLayersImmediate()
    {
        RebuildRuntimeCache(false);

        for (int i = 0; i < runtimeStates.Count; i++)
        {
            RuntimeLayerState state = runtimeStates[i];

            if (state == null || state.transform == null)
                continue;

            state.fadeAlphaFactor = 0f;
            state.fadeScaleFactor = Vector3.one;
            state.motionScaleFactor = Vector3.one;

            state.transform.localScale = state.originalLocalScale;

            ApplyVisualState(state);
        }
    }

    private void RestoreAllLayersImmediate()
    {
        RebuildRuntimeCache(false);

        for (int i = 0; i < runtimeStates.Count; i++)
        {
            RuntimeLayerState state = runtimeStates[i];

            if (state == null || state.transform == null)
                continue;

            state.fadeAlphaFactor = 1f;
            state.fadeScaleFactor = Vector3.one;
            state.motionScaleFactor = Vector3.one;

            state.transform.localScale = state.originalLocalScale;

            ApplyVisualState(state);
        }
    }

    private void ApplyVisualState(RuntimeLayerState state)
    {
        if (state == null || state.transform == null)
            return;

        float finalAlphaFactor = Mathf.Clamp01(state.fadeAlphaFactor);

        Vector3 finalScale = Vector3.Scale(state.originalLocalScale, state.fadeScaleFactor);
        finalScale = Vector3.Scale(finalScale, state.motionScaleFactor);

        state.transform.localScale = finalScale;

        ApplySpriteAlpha(state, finalAlphaFactor);
        ApplyGraphicAlpha(state, finalAlphaFactor);
        ApplyCanvasGroupAlpha(state, finalAlphaFactor);
        ApplyRendererAlpha(state, finalAlphaFactor);
    }

    private void ApplySpriteAlpha(RuntimeLayerState state, float alphaFactor)
    {
        if (state.spriteRenderers == null)
            return;

        for (int i = 0; i < state.spriteRenderers.Length; i++)
        {
            SpriteRenderer spriteRenderer = state.spriteRenderers[i];

            if (spriteRenderer == null)
                continue;

            Color color = state.spriteBaseColors[i];
            color.a *= alphaFactor;

            spriteRenderer.color = color;
        }
    }

    private void ApplyGraphicAlpha(RuntimeLayerState state, float alphaFactor)
    {
        if (state.graphics == null)
            return;

        for (int i = 0; i < state.graphics.Length; i++)
        {
            Graphic graphic = state.graphics[i];

            if (graphic == null)
                continue;

            Color color = state.graphicBaseColors[i];
            color.a *= alphaFactor;

            graphic.color = color;
        }
    }

    private void ApplyCanvasGroupAlpha(RuntimeLayerState state, float alphaFactor)
    {
        if (state.canvasGroups == null)
            return;

        for (int i = 0; i < state.canvasGroups.Length; i++)
        {
            CanvasGroup canvasGroup = state.canvasGroups[i];

            if (canvasGroup == null)
                continue;

            canvasGroup.alpha = state.canvasGroupBaseAlphas[i] * alphaFactor;
        }
    }

    private void ApplyRendererAlpha(RuntimeLayerState state, float alphaFactor)
    {
        if (state.rendererAlphaEntries == null)
            return;

        for (int i = 0; i < state.rendererAlphaEntries.Count; i++)
        {
            RendererAlphaEntry entry = state.rendererAlphaEntries[i];

            if (entry == null || entry.renderer == null)
                continue;

            Color color = entry.baseColor;
            color.a *= alphaFactor;

            entry.renderer.GetPropertyBlock(entry.propertyBlock, entry.materialIndex);
            entry.propertyBlock.SetColor(entry.colorPropertyId, color);
            entry.renderer.SetPropertyBlock(entry.propertyBlock, entry.materialIndex);
        }
    }

    private void OnValidate()
    {
        fullLostConfirmTime = ClampZeroToSixRounded(fullLostConfirmTime);

        if (layers == null)
            return;

        for (int i = 0; i < layers.Count; i++)
        {
            LayerSettings layer = layers[i];

            if (layer == null)
                continue;

            if (layer.resetFadeDefaults || layer.lastFadeType != (int)layer.fadeType)
            {
                ApplyFadeDefaults(layer);
                layer.resetFadeDefaults = false;
                layer.lastFadeType = (int)layer.fadeType;
            }

            if (layer.resetMotionDefaults || layer.lastMotionType != (int)layer.motionType)
            {
                ApplyMotionDefaults(layer);
                layer.resetMotionDefaults = false;
                layer.lastMotionType = (int)layer.motionType;
            }

            layer.fadeTime = ClampZeroToSixRounded(layer.fadeTime);
            layer.delayBeforeFade = ClampZeroToSixRounded(layer.delayBeforeFade);
            layer.delayAfterFade = ClampZeroToSixRounded(layer.delayAfterFade);

            layer.motionSpeed = ClampZeroToSixRounded(layer.motionSpeed);
            layer.motionIntensity = ClampZeroToSixRounded(layer.motionIntensity);
            layer.motionSmoothing = ClampZeroToSixRounded(layer.motionSmoothing);
        }

#if UNITY_EDITOR
        if (!editorPreviewActive)
            cacheBuilt = false;
#else
        cacheBuilt = false;
#endif
    }

    private static float ClampZeroToSixRounded(float value)
    {
        value = Mathf.Clamp(value, 0f, 6f);
        return Mathf.Round(value * 10f) / 10f;
    }

    private static void ApplyFadeDefaults(LayerSettings layer)
    {
        switch (layer.fadeType)
        {
            case FadeType.PagePop:
                layer.fadeTime = 1.2f;
                layer.delayBeforeFade = 0f;
                layer.delayAfterFade = 0.2f;
                break;

            case FadeType.BrushReveal:
                layer.fadeTime = 1.5f;
                layer.delayBeforeFade = 0f;
                layer.delayAfterFade = 0.3f;
                break;

            case FadeType.BloomIn:
                layer.fadeTime = 1.6f;
                layer.delayBeforeFade = 0.1f;
                layer.delayAfterFade = 0.3f;
                break;

            case FadeType.CurtainReveal:
                layer.fadeTime = 1.5f;
                layer.delayBeforeFade = 0f;
                layer.delayAfterFade = 0.3f;
                break;

            case FadeType.LiftReveal:
                layer.fadeTime = 1.4f;
                layer.delayBeforeFade = 0f;
                layer.delayAfterFade = 0.3f;
                break;

            case FadeType.StorySpark:
                layer.fadeTime = 1.3f;
                layer.delayBeforeFade = 0.1f;
                layer.delayAfterFade = 0.2f;
                break;

            case FadeType.SoftAppear:
            default:
                layer.fadeTime = 1.4f;
                layer.delayBeforeFade = 0f;
                layer.delayAfterFade = 0.3f;
                break;
        }
    }

    private static void ApplyMotionDefaults(LayerSettings layer)
    {
        switch (layer.motionType)
        {
            case MotionType.StoryBreath:
                layer.motionSpeed = 0.8f;
                layer.motionIntensity = 2.0f;
                layer.motionSmoothing = 5.4f;
                break;

            case MotionType.LeafFlutter:
                layer.motionSpeed = 1.8f;
                layer.motionIntensity = 2.5f;
                layer.motionSmoothing = 4.2f;
                break;

            case MotionType.CloudSwell:
                layer.motionSpeed = 0.7f;
                layer.motionIntensity = 2.4f;
                layer.motionSmoothing = 5.3f;
                break;

            case MotionType.SproutLife:
                layer.motionSpeed = 1.0f;
                layer.motionIntensity = 2.4f;
                layer.motionSmoothing = 5.0f;
                break;

            case MotionType.WaterRipple:
                layer.motionSpeed = 2.1f;
                layer.motionIntensity = 2.7f;
                layer.motionSmoothing = 3.8f;
                break;

            case MotionType.MagicBloom:
                layer.motionSpeed = 1.6f;
                layer.motionIntensity = 2.9f;
                layer.motionSmoothing = 4.1f;
                break;

            case MotionType.StoryAttention:
                layer.motionSpeed = 1.2f;
                layer.motionIntensity = 2.8f;
                layer.motionSmoothing = 4.6f;
                break;

            case MotionType.TinyBounce:
                layer.motionSpeed = 1.9f;
                layer.motionIntensity = 2.6f;
                layer.motionSmoothing = 3.9f;
                break;

            case MotionType.None:
            default:
                layer.motionSpeed = 0f;
                layer.motionIntensity = 0f;
                layer.motionSmoothing = 0f;
                break;
        }
    }

#if UNITY_EDITOR
    public bool EditorIsPreviewActive()
    {
        return editorPreviewActive;
    }

    public void EditorTestFadeAndMotion()
    {
        if (Application.isPlaying)
        {
            TriggerHideImmediate();
            TriggerFadeIn();
            return;
        }

        RebuildRuntimeCache(true);
        PrepareLayersForFreshSequence();

        editorPreviewStartTime = EditorApplication.timeSinceStartup;
        editorPreviewLastTime = editorPreviewStartTime;
        editorPreviewActive = true;

        EditorUtility.SetDirty(this);
        SceneView.RepaintAll();
    }

    public void EditorStopTestAndRestore()
    {
        if (Application.isPlaying)
        {
            TriggerHideImmediate();
            return;
        }

        editorPreviewActive = false;

        RestoreAllLayersImmediate();

        EditorUtility.SetDirty(this);
        SceneView.RepaintAll();
    }

    public void EditorPreviewTick(double currentEditorTime)
    {
        if (Application.isPlaying)
            return;

        if (!editorPreviewActive)
            return;

        float elapsed = Mathf.Max(0f, (float)(currentEditorTime - editorPreviewStartTime));
        float deltaTime = Mathf.Clamp((float)(currentEditorTime - editorPreviewLastTime), 0.001f, 0.05f);

        editorPreviewLastTime = currentEditorTime;

        if (startAllLayersTogether)
            EvaluateEditorPreviewAllTogether(elapsed, deltaTime);
        else
            EvaluateEditorPreviewSequential(elapsed, deltaTime);

        SceneView.RepaintAll();
    }

    private void EvaluateEditorPreviewSequential(float elapsed, float deltaTime)
    {
        if (runtimeStates.Count != layers.Count)
            RebuildRuntimeCache(true);

        float cursor = 0f;

        for (int i = 0; i < layers.Count; i++)
        {
            if (!TryGetValidLayer(i, out LayerSettings layer, out RuntimeLayerState state))
                continue;

            float delayBefore = Mathf.Max(0f, layer.delayBeforeFade);
            float fadeTime = Mathf.Max(0.1f, layer.fadeTime);
            float delayAfter = Mathf.Max(0f, layer.delayAfterFade);

            float fadeStart = cursor + delayBefore;
            float fadeEnd = fadeStart + fadeTime;
            float layerDoneTime = fadeEnd + delayAfter;

            ApplyEditorLayerPreviewSequential(layer, state, elapsed, deltaTime, fadeStart, fadeEnd);

            cursor = layerDoneTime;
        }
    }

    private void EvaluateEditorPreviewAllTogether(float elapsed, float deltaTime)
    {
        if (runtimeStates.Count != layers.Count)
            RebuildRuntimeCache(true);

        for (int i = 0; i < layers.Count; i++)
        {
            if (!TryGetValidLayer(i, out LayerSettings layer, out RuntimeLayerState state))
                continue;

            float fadeTime = Mathf.Max(0.1f, layer.fadeTime);

            if (elapsed <= fadeTime)
            {
                float t = Mathf.Clamp01(elapsed / fadeTime);
                FadeResult fadeResult = EvaluateFade(layer.fadeType, t);

                state.fadeAlphaFactor = fadeResult.alphaFactor;
                state.fadeScaleFactor = fadeResult.scaleFactor;
            }
            else
            {
                state.fadeAlphaFactor = 1f;
                state.fadeScaleFactor = Vector3.one;
            }

            ApplyEditorMotionPreview(layer, state, elapsed, deltaTime);
            ApplyVisualState(state);
        }
    }

    private void ApplyEditorLayerPreviewSequential(
        LayerSettings layer,
        RuntimeLayerState state,
        float elapsed,
        float deltaTime,
        float fadeStart,
        float fadeEnd
    )
    {
        if (elapsed < fadeStart)
        {
            state.fadeAlphaFactor = 0f;
            state.fadeScaleFactor = Vector3.one;
            state.motionScaleFactor = Vector3.one;
        }
        else if (elapsed <= fadeEnd)
        {
            float fadeTime = Mathf.Max(0.1f, fadeEnd - fadeStart);
            float t = Mathf.Clamp01((elapsed - fadeStart) / fadeTime);
            FadeResult fadeResult = EvaluateFade(layer.fadeType, t);

            state.fadeAlphaFactor = fadeResult.alphaFactor;
            state.fadeScaleFactor = fadeResult.scaleFactor;
            state.motionScaleFactor = Vector3.one;
        }
        else
        {
            state.fadeAlphaFactor = 1f;
            state.fadeScaleFactor = Vector3.one;

            float motionElapsed = elapsed - fadeEnd;
            ApplyEditorMotionPreview(layer, state, motionElapsed, deltaTime);
        }

        ApplyVisualState(state);
    }

    private void ApplyEditorMotionPreview(
        LayerSettings layer,
        RuntimeLayerState state,
        float elapsed,
        float deltaTime
    )
    {
        if (layer.motionType == MotionType.None)
        {
            state.motionScaleFactor = Vector3.one;
            return;
        }

        float seed = Mathf.Abs(state.transform.GetInstanceID() % 1000) * 0.01f;
        float effectiveSpeed = GetEffectiveMotionSpeed(layer.motionSpeed);
        float motionTime = Mathf.Repeat(elapsed * effectiveSpeed, 1000f);

        MotionResult motionResult = EvaluateMotion(
            layer.motionType,
            motionTime,
            layer.motionIntensity,
            seed
        );

        float damping = GetSmoothingDamping(layer.motionSmoothing, deltaTime);

        state.motionScaleFactor = Vector3.Lerp(
            state.motionScaleFactor,
            motionResult.scaleFactor,
            damping
        );
    }
#endif
}

#if UNITY_EDITOR
[CustomEditor(typeof(EnvLayer2D))]
public class EnvLayer2DEditor : Editor
{
    private void OnEnable()
    {
        EditorApplication.update += OnEditorUpdate;
    }

    private void OnDisable()
    {
        EnvLayer2D envLayer = target as EnvLayer2D;

        if (envLayer != null && envLayer.EditorIsPreviewActive() && !Application.isPlaying)
            envLayer.EditorStopTestAndRestore();

        EditorApplication.update -= OnEditorUpdate;
    }

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        EditorGUILayout.Space(12);
        EditorGUILayout.LabelField("Scene Test Control", EditorStyles.boldLabel);

        EnvLayer2D envLayer = (EnvLayer2D)target;

        string buttonText = envLayer.EditorIsPreviewActive()
            ? "Stop Loop Test And Restore"
            : "Test Fade And Loop Motion";

        if (GUILayout.Button(buttonText, GUILayout.Height(32)))
        {
            if (envLayer.EditorIsPreviewActive())
            {
                envLayer.EditorStopTestAndRestore();
            }
            else
            {
                envLayer.EditorTestFadeAndMotion();
            }
        }

        EditorGUILayout.HelpBox(
            "Runs fade once. Motion loops until you stop the test. If Start All Layers Together is ON, every layer fades and starts motion at the same time. Delay Before Fade and Delay After Fade are ignored only in that mode.",
            MessageType.Info
        );
    }

    private void OnEditorUpdate()
    {
        if (target == null)
            return;

        EnvLayer2D envLayer = target as EnvLayer2D;

        if (envLayer == null)
            return;

        envLayer.EditorPreviewTick(EditorApplication.timeSinceStartup);

        Repaint();
    }
}
#endif