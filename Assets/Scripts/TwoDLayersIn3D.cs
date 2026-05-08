using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Vuforia;

// ---------------------------------------------------------------
// TwoDLayersIn3D  v7 -- self contained, no external trigger needed
// Attach to 2D_Layers GO under any 3D page ImageTarget.
// Wires itself to Vuforia tracking automatically.
// ---------------------------------------------------------------

public class TwoDLayersIn3D : MonoBehaviour
{
    public enum MotionType
    {
        None,
        Sway,
        Shake,
        Breathe,
        Drift,
        Flutter,
        Bob
    }

    public enum FadeOrder
    {
        FirstToLast,
        LastToFirst
    }

    [System.Serializable]
    public class LayerEntry
    {
        [Tooltip("Drag any child GO here.")]
        public Transform layerTransform;

        public MotionType motionType = MotionType.Sway;

        [Range(0.1f, 3f)]
        public float speed = 0.8f;

        [Range(0.005f, 0.12f)]
        public float intensity = 0.05f;

        [Range(0f, 6.28f)]
        public float phaseOffset = 0f;

        [Range(1f, 20f)]
        public float smoothing = 7f;

        [Range(0.3f, 4f)]
        [Tooltip("How long this layer fades in. 1.0-1.5 recommended.")]
        public float fadeDuration = 1.2f;

        [System.NonSerialized] public Vector3 originPos;
        [System.NonSerialized] public Vector3 originScale;
        [System.NonSerialized] public Quaternion originRotation;
        [System.NonSerialized] public bool initialized;
        [System.NonSerialized] public Vector3 currentPos;
        [System.NonSerialized] public Vector3 posVelocity;
        [System.NonSerialized] public float currentAngle;
        [System.NonSerialized] public float angleVelocity;
        [System.NonSerialized] public float currentScale;
        [System.NonSerialized] public float scaleVelocity;
        [System.NonSerialized] public MotionType lastMotionType;
        [System.NonSerialized] public bool motionTypeReady;
        [System.NonSerialized] public float currentAlpha;
        [System.NonSerialized] public Coroutine fadeCoroutine;
    }

    // ── Inspector ─────────────────────────────────────────────────

    [Header("Layers")]
    public List<LayerEntry> layers = new List<LayerEntry>();

    [Header("Motion")]
    [Range(0.1f, 2f)]
    public float motionRampUpSeconds = 0.5f;
    public bool pauseWhenTrackingLost = true;

    [Header("Fade In")]
    public bool fadeInEnabled = true;

    [Range(0f, 6f)]
    [Tooltip("Wait this many seconds after tracking found before fading starts.\nMatch to your VFX totalRevealSeconds.")]
    public float fadeStartDelay = 2f;

    public FadeOrder fadeOrder = FadeOrder.LastToFirst;

    [Range(0f, 3f)]
    [Tooltip("Equal gap between each layer starting. All overlap while fading.")]
    public float delayBetweenLayers = 0.4f;

    // ── Multipliers ───────────────────────────────────────────────

    private const float SwayRotMult = 150f;
    private const float FlutterRotMult = 120f;
    private const float FlutterScaMult = 8f;
    private const float BreatheScaMult = 4f;
    private const float DriftPosMult = 4f;
    private const float BobPosMult = 3f;

    // ── Runtime ───────────────────────────────────────────────────

    private float _perlinSeedX;
    private float _perlinSeedY;
    private ObserverBehaviour _observer;
    private bool _isTracked = false;
    private bool _motionActive = false;
    private float _globalRamp = 0f;
    private Coroutine _fadeSequenceCoroutine;

    // ── Lifecycle ─────────────────────────────────────────────────

    private void Awake()
    {
        _perlinSeedX = Random.Range(0f, 100f);
        _perlinSeedY = Random.Range(100f, 200f);

        // Init transforms -- renderer cache done lazily later
        foreach (var e in layers)
        {
            if (e == null || e.layerTransform == null) continue;
            InitTransform(e);
        }
    }

    private void Start()
    {
        // Find observer -- search broader than just parent
        // Needed because Addressables content loads after Awake
        _observer = GetComponentInParent<ObserverBehaviour>(true);

        if (_observer == null)
        {
            // Fallback: search whole scene
            _observer = Object.FindFirstObjectByType<ObserverBehaviour>();
        }

        if (_observer != null)
        {
            Debug.Log($"[2DLayers] Observer found: {_observer.gameObject.name}");
            _observer.OnTargetStatusChanged += OnTargetStatusChanged;
        }
        else
        {
            Debug.LogWarning("[2DLayers] No ObserverBehaviour found. Fade will not auto-trigger from tracking.");
        }

        // Snap all invisible at start if fade enabled
        if (fadeInEnabled)
        {
            foreach (var e in layers)
                SafeSetAlpha(e, 0f);
        }

        // Debug: print all layer states
        Debug.Log($"[2DLayers] Start -- {layers.Count} layers registered:");
        for (int i = 0; i < layers.Count; i++)
        {
            var e = layers[i];
            Debug.Log($"  Layer {i}: transform={(e?.layerTransform?.name ?? "NULL")}");
        }
    }

    private void OnDestroy()
    {
        if (_observer != null)
            _observer.OnTargetStatusChanged -= OnTargetStatusChanged;
    }

    private static void InitTransform(LayerEntry e)
    {
        var tr = e.layerTransform;
        e.originPos = tr.localPosition;
        e.originScale = tr.localScale;
        e.originRotation = tr.localRotation;
        e.currentPos = tr.localPosition;
        e.currentAngle = 0f;
        e.currentScale = 1f;
        e.posVelocity = Vector3.zero;
        e.angleVelocity = 0f;
        e.scaleVelocity = 0f;
        e.lastMotionType = e.motionType;
        e.motionTypeReady = true;
        e.initialized = true;
    }

    // ── Tracking ──────────────────────────────────────────────────

    private void OnTargetStatusChanged(ObserverBehaviour behaviour, TargetStatus status)
    {
        bool tracked = status.Status == Status.TRACKED
                    || status.Status == Status.EXTENDED_TRACKED
                    || status.Status == Status.LIMITED;

        bool wasTracked = _isTracked;
        _isTracked = tracked;

        if (tracked)
        {
            _motionActive = true;

            // Only auto-trigger fade on first track detection
            // Replay is handled via TriggerFadeIn() from ARTrackedPageNode
            // But if TriggerFadeIn was never wired, this acts as fallback
            if (!wasTracked)
            {
                Debug.Log("[2DLayers] Tracking found -- triggering fade.");
                TriggerFadeIn();
            }
        }
        else
        {
            if (pauseWhenTrackingLost)
            {
                _motionActive = false;
                _globalRamp = 0f;
                ResetAllToOrigin();
            }

            StopAllFades();

            if (fadeInEnabled)
            {
                foreach (var e in layers)
                    SafeSetAlpha(e, 0f);
            }
        }
    }

    // ── Public API ────────────────────────────────────────────────

    // Call from ARTrackedPageNode.StartFromBeginning() for replay support:
    //   GetComponentInChildren<TwoDLayersIn3D>(true)?.TriggerFadeIn();
    public void TriggerFadeIn()
    {
        if (!gameObject.activeInHierarchy) return;

        _motionActive = true;

        // Re-init any entry not yet ready (Addressables late load)
        foreach (var e in layers)
        {
            if (e == null || e.layerTransform == null) continue;
            if (!e.initialized) InitTransform(e);
        }

        if (fadeInEnabled)
        {
            StopAllFades();
            _fadeSequenceCoroutine = StartCoroutine(FadeSequenceRoutine());
        }
        else
        {
            foreach (var e in layers)
                SafeSetAlpha(e, 1f);
        }
    }

    // ── Fade ──────────────────────────────────────────────────────

    private void StopAllFades()
    {
        if (_fadeSequenceCoroutine != null)
        {
            StopCoroutine(_fadeSequenceCoroutine);
            _fadeSequenceCoroutine = null;
        }
        foreach (var e in layers)
        {
            if (e?.fadeCoroutine == null) continue;
            StopCoroutine(e.fadeCoroutine);
            e.fadeCoroutine = null;
        }
    }

    private IEnumerator FadeSequenceRoutine()
    {
        // Step 1: snap all to invisible
        foreach (var e in layers)
            SafeSetAlpha(e, 0f);

        Debug.Log($"[2DLayers] Fade sequence starting. Delay: {fadeStartDelay}s");

        // Step 2: wait for VFX to finish
        if (fadeStartDelay > 0f)
            yield return new WaitForSeconds(fadeStartDelay);

        // Step 3: build order
        // LastToFirst = last layer fades first (background first, foreground last)
        var order = new List<int>();
        if (fadeOrder == FadeOrder.LastToFirst)
            for (int i = layers.Count - 1; i >= 0; i--) order.Add(i);
        else
            for (int i = 0; i < layers.Count; i++) order.Add(i);

        // Step 4: fire each with equal delay -- they OVERLAP while fading
        int sequence = 1;
        foreach (int idx in order)
        {
            var e = layers[idx];
            if (e == null || e.layerTransform == null) continue;

            Debug.Log($"[2DLayers] Starting fade #{sequence} on Layer {idx} ({e.layerTransform.name})");
            sequence++;

            if (e.fadeCoroutine != null) StopCoroutine(e.fadeCoroutine);
            e.fadeCoroutine = StartCoroutine(FadeLayerIn(e));

            if (delayBetweenLayers > 0f)
                yield return new WaitForSeconds(delayBetweenLayers);
        }

        _fadeSequenceCoroutine = null;
        Debug.Log("[2DLayers] Fade sequence complete.");
    }

    private IEnumerator FadeLayerIn(LayerEntry e)
    {
        float elapsed = 0f;
        float duration = Mathf.Max(0.1f, e.fadeDuration);

        SafeSetAlpha(e, 0f);

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / duration);

            // Double SmoothStep -- cubic ease in-out, very smooth
            float alpha = Mathf.SmoothStep(0f, 1f, Mathf.SmoothStep(0f, 1f, t));

            SafeSetAlpha(e, alpha);
            yield return null;
        }

        SafeSetAlpha(e, 1f);
        e.fadeCoroutine = null;

        Debug.Log($"[2DLayers] Layer '{e.layerTransform.name}' fade complete.");
    }

    // ── Alpha write ───────────────────────────────────────────────

    private static void SafeSetAlpha(LayerEntry e, float alpha)
    {
        if (e == null || e.layerTransform == null) return;

        e.currentAlpha = alpha;

        // SpriteRenderer -- direct color alpha
        var sr = e.layerTransform.GetComponentInChildren<SpriteRenderer>(true);
        if (sr != null)
        {
            var c = sr.color; c.a = alpha;
            sr.color = c;
            return;
        }

        // MeshRenderer -- URP material _BaseColor or legacy _Color
        var mr = e.layerTransform.GetComponentInChildren<MeshRenderer>(true);
        if (mr != null && mr.material != null)
        {
            if (mr.material.HasProperty("_BaseColor"))
            {
                var c = mr.material.GetColor("_BaseColor");
                c.a = alpha;
                mr.material.SetColor("_BaseColor", c);
            }
            else if (mr.material.HasProperty("_Color"))
            {
                var c = mr.material.GetColor("_Color");
                c.a = alpha;
                mr.material.SetColor("_Color", c);
            }
        }
    }

    // ── Update ────────────────────────────────────────────────────

    private void Update()
    {
        _globalRamp = Mathf.MoveTowards(
            _globalRamp, _motionActive ? 1f : 0f,
            Time.deltaTime / Mathf.Max(0.01f, motionRampUpSeconds));

        float t = Time.time;

        foreach (var e in layers)
        {
            if (e == null || e.layerTransform == null) continue;
            if (!e.initialized) { InitTransform(e); continue; }

            if (e.motionTypeReady && e.motionType != e.lastMotionType)
            {
                ResetEntryToOrigin(e);
                e.lastMotionType = e.motionType;
            }

            if (_globalRamp <= 0.001f) continue;
            if (e.motionType == MotionType.None) continue;

            ApplyMotion(e, t);
        }
    }

    private void ApplyMotion(LayerEntry e, float time)
    {
        var tr = e.layerTransform;
        float phase = time * e.speed + e.phaseOffset;
        float ramp = _globalRamp;
        float smooth = 1f / Mathf.Max(1f, e.smoothing);

        switch (e.motionType)
        {
            case MotionType.Sway:
                tr.localPosition = e.originPos;
                tr.localScale = e.originScale;
                float swayT = (Mathf.Sin(phase) + 0.18f * Mathf.Sin(phase * 2.7f))
                              * e.intensity * SwayRotMult * ramp;
                e.currentAngle = Mathf.SmoothDamp(e.currentAngle, swayT, ref e.angleVelocity, smooth);
                tr.localRotation = e.originRotation * Quaternion.Euler(0f, 0f, e.currentAngle);
                break;

            case MotionType.Shake:
                tr.localRotation = e.originRotation;
                tr.localScale = e.originScale;
                float px = (Mathf.PerlinNoise(_perlinSeedX + time * e.speed, 0f) - 0.5f)
                           * 2f * e.intensity * ramp;
                float py = (Mathf.PerlinNoise(0f, _perlinSeedY + time * e.speed) - 0.5f)
                           * e.intensity * ramp;
                e.currentPos = Vector3.SmoothDamp(e.currentPos,
                    e.originPos + new Vector3(px, py, 0f), ref e.posVelocity, smooth);
                tr.localPosition = e.currentPos;
                break;

            case MotionType.Breathe:
                tr.localRotation = e.originRotation;
                tr.localPosition = e.originPos;
                float bT = 1f + (Mathf.Sin(phase) + 0.1f * Mathf.Sin(phase * 3f))
                           * e.intensity * BreatheScaMult * ramp;
                e.currentScale = Mathf.SmoothDamp(e.currentScale, bT, ref e.scaleVelocity, smooth);
                tr.localScale = e.originScale * e.currentScale;
                break;

            case MotionType.Drift:
                tr.localRotation = e.originRotation;
                tr.localScale = e.originScale;
                float dtx = e.originPos.x + Mathf.Sin(phase) * e.intensity * DriftPosMult * ramp;
                float dty = e.originPos.y + Mathf.Sin(phase * 0.6f) * e.intensity * 0.8f * ramp;
                e.currentPos = Vector3.SmoothDamp(e.currentPos,
                    new Vector3(dtx, dty, e.originPos.z), ref e.posVelocity, smooth);
                tr.localPosition = e.currentPos;
                break;

            case MotionType.Flutter:
                tr.localPosition = e.originPos;
                float fA = (Mathf.Sin(phase) + 0.2f * Mathf.Sin(phase * 3.1f))
                           * e.intensity * FlutterRotMult * ramp;
                float fS = 1f + Mathf.Sin(phase * 1.7f) * e.intensity * FlutterScaMult * ramp;
                e.currentAngle = Mathf.SmoothDamp(e.currentAngle, fA, ref e.angleVelocity, smooth);
                e.currentScale = Mathf.SmoothDamp(e.currentScale, fS, ref e.scaleVelocity, smooth);
                tr.localRotation = e.originRotation * Quaternion.Euler(0f, 0f, e.currentAngle);
                tr.localScale = e.originScale * e.currentScale;
                break;

            case MotionType.Bob:
                tr.localRotation = e.originRotation;
                tr.localScale = e.originScale;
                float bty = e.originPos.y + Mathf.Sin(phase) * e.intensity * BobPosMult * ramp;
                float btx = e.originPos.x + Mathf.Sin(phase * 0.7f) * e.intensity * 0.5f * ramp;
                e.currentPos = Vector3.SmoothDamp(e.currentPos,
                    new Vector3(btx, bty, e.originPos.z), ref e.posVelocity, smooth);
                tr.localPosition = e.currentPos;
                break;
        }
    }

    private void ResetAllToOrigin()
    {
        foreach (var e in layers)
        {
            if (e == null || e.layerTransform == null || !e.initialized) continue;
            ResetEntryToOrigin(e);
        }
    }

    private static void ResetEntryToOrigin(LayerEntry e)
    {
        e.layerTransform.localPosition = e.originPos;
        e.layerTransform.localRotation = e.originRotation;
        e.layerTransform.localScale = e.originScale;
        e.currentPos = e.originPos;
        e.currentAngle = 0f;
        e.currentScale = 1f;
        e.posVelocity = Vector3.zero;
        e.angleVelocity = 0f;
        e.scaleVelocity = 0f;
    }

    public void SetMotionActive(bool active)
    {
        _motionActive = active;
        if (!active) { _globalRamp = 0f; ResetAllToOrigin(); }
    }
}