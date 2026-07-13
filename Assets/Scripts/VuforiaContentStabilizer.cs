using UnityEngine;
using Vuforia;

// Adaptive tracking-noise filter ("1-Euro" style): unlike a plain Lerp/Slerp, this does
// NOT trade shake-reduction for lag. While the tracked pose is essentially still, it
// smooths hard (killing camera-noise jitter). The instant real movement is detected, it
// backs off smoothing almost completely so it tracks fast motion with minimal added delay.
// This is the standard technique for exactly this "jitter vs. lag" tradeoff in AR/VR.
public class VuforiaContentStabilizer : MonoBehaviour
{
    [SerializeField] private ObserverBehaviour observer;

    [Header("Quality gate")]
    public bool ignoreLimited = true;

    // Position (meters/sec) and rotation (degrees/sec) are different physical units at very
    // different natural scales, so they need their OWN tuning -- reusing one shared value
    // for both (as an earlier version of this script did) miscalibrates one of them badly.
    // Rotation is deliberately tuned calmer here: on a 3D model whose visible geometry sits
    // well above the tracked page (a "lever arm"), a small rotational tracking error becomes
    // a much larger apparent wobble at the top of the model than the same error would ever
    // produce on a flat page -- so rotation noise is the more visually important one to damp.
    [Header("Position smoothing (1-Euro, meters/sec)")]
    [Tooltip("Smoothing strength while the page is essentially still (Hz). Lower = calmer when still.")]
    public float posMinCutoff = 0.8f;
    [Tooltip("How fast smoothing backs off as real movement speeds up (meters/sec scale).")]
    public float posBeta = 0.3f;
    [Tooltip("Smoothing applied to the internal speed estimate itself.")]
    public float posDerivativeCutoff = 1.0f;

    [Header("Rotation smoothing (1-Euro, degrees/sec)")]
    [Tooltip("Smoothing strength while the page is essentially still (Hz). Lower = calmer when still.")]
    public float rotMinCutoff = 0.3f;
    [Tooltip("How fast smoothing backs off as real movement speeds up (degrees/sec scale -- naturally a much bigger number than meters/sec, needs its own much smaller beta).")]
    public float rotBeta = 0.15f;
    [Tooltip("Smoothing applied to the internal speed estimate itself.")]
    public float rotDerivativeCutoff = 1.0f;

    Vector3 localPosOffset;
    Quaternion localRotOffset;

    OneEuroFilterFloat filterX, filterY, filterZ;
    OneEuroFilterFloat rotSpeedFilter;

    Quaternion filteredRot;
    bool hasInit;

    Renderer[] renderers;
    Canvas[] canvases;

    void Awake()
    {
        if (!observer) observer = GetComponentInParent<ObserverBehaviour>();
        if (!observer)
        {
            Debug.LogError("VuforiaContentStabilizer: Assign the ImageTarget ObserverBehaviour.");
            enabled = false;
            return;
        }

        renderers = GetComponentsInChildren<Renderer>(true);
        canvases = GetComponentsInChildren<Canvas>(true);

        filterX = new OneEuroFilterFloat(posMinCutoff, posBeta, posDerivativeCutoff);
        filterY = new OneEuroFilterFloat(posMinCutoff, posBeta, posDerivativeCutoff);
        filterZ = new OneEuroFilterFloat(posMinCutoff, posBeta, posDerivativeCutoff);
        rotSpeedFilter = new OneEuroFilterFloat(rotMinCutoff, rotBeta, rotDerivativeCutoff);
    }

    // The anchor this stabilizer lives on is created once and reused across every scan
    // of its marker, but its child content (the actual page prefab) is instantiated
    // later, AFTER Awake() already cached (empty) renderers/canvases. Call this once
    // new content has been parented under the anchor so visibility toggling actually
    // reaches it.
    public void RefreshTrackedRenderers()
    {
        renderers = GetComponentsInChildren<Renderer>(true);
        canvases = GetComponentsInChildren<Canvas>(true);
    }

    void Start()
    {
        // Capture offset while still parented under ImageTarget, then detach.
        if (transform.parent == observer.transform)
        {
            localPosOffset = transform.localPosition;
            localRotOffset = transform.localRotation;
            transform.SetParent(null, true);
        }
        else
        {
            var t = observer.transform;
            localPosOffset = Quaternion.Inverse(t.rotation) * (transform.position - t.position);
            localRotOffset = Quaternion.Inverse(t.rotation) * transform.rotation;
        }

        observer.OnTargetStatusChanged += OnStatus;
        OnStatus(observer, observer.TargetStatus);
    }

    void OnDestroy()
    {
        if (observer) observer.OnTargetStatusChanged -= OnStatus;
    }

    // EXTENDED_TRACKED is deliberately treated as NOT good, matching CustomARHandler's own
    // OnTargetStatusChanged logic — EXTENDED_TRACKED is Vuforia guessing position from device
    // motion once the real marker is out of view, which is exactly what made content appear
    // to "follow your hand" before that was fixed. Smoothing must not keep tracking that guess.
    void OnStatus(ObserverBehaviour _, TargetStatus status)
    {
        bool good = status.Status == Status.TRACKED || status.Status == Status.LIMITED;

        if (ignoreLimited)
        {
            // LIMITED is low accuracy; discard if you need exact alignment.
            good = good && status.StatusInfo == StatusInfo.NORMAL;
        }

        SetVisible(good);

        if (good)
        {
            var desired = GetDesiredPose();
            filterX.Reset(desired.pos.x);
            filterY.Reset(desired.pos.y);
            filterZ.Reset(desired.pos.z);
            rotSpeedFilter.Reset(0f);
            filteredRot = desired.rot;
            transform.SetPositionAndRotation(desired.pos, filteredRot);
            hasInit = true;
        }
        else
        {
            hasInit = false;
        }
    }

    void LateUpdate()
    {
        if (!hasInit) return;

        float dt = Mathf.Max(Time.deltaTime, 0.0001f);
        var desired = GetDesiredPose();

        // Position: an independent adaptive filter per axis. Each one smooths hard while
        // that axis is essentially static, and opens up automatically the instant it starts
        // moving for real -- so real motion is never lagged, only genuine noise is removed.
        Vector3 filteredPos = new Vector3(
            filterX.Filter(desired.pos.x, dt),
            filterY.Filter(desired.pos.y, dt),
            filterZ.Filter(desired.pos.z, dt));

        // Rotation: same adaptive principle, applied via Slerp driven by an adaptive alpha
        // derived from how fast the raw tracked rotation is actually changing.
        float rawAngleDelta = Quaternion.Angle(filteredRot, desired.rot);
        float rawAngularSpeed = rawAngleDelta / dt;
        float smoothedSpeed = rotSpeedFilter.Filter(rawAngularSpeed, dt);
        float rotCutoff = rotMinCutoff + rotBeta * smoothedSpeed;
        float rotAlpha = OneEuroFilterFloat.ComputeAlpha(rotCutoff, dt);
        filteredRot = Quaternion.Slerp(filteredRot, desired.rot, rotAlpha);

        transform.SetPositionAndRotation(filteredPos, filteredRot);
    }

    (Vector3 pos, Quaternion rot) GetDesiredPose()
    {
        var t = observer.transform;
        return (t.TransformPoint(localPosOffset), t.rotation * localRotOffset);
    }

    void SetVisible(bool on)
    {
        foreach (var r in renderers) if (r) r.enabled = on;
        foreach (var c in canvases) if (c) c.enabled = on;
    }
}

// Standard "1-Euro Filter" (Casiez, Roussel, Vogel 2012) adapted for a single float value.
// Smooths hard at low speed, opens up automatically at high speed -- removes noise without
// adding the fixed lag a plain Lerp/SmoothDamp would.
public class OneEuroFilterFloat
{
    readonly float _minCutoff;
    readonly float _beta;
    readonly float _derivativeCutoff;

    float _lastValue;
    float _lastDerivative;
    bool _initialized;

    public OneEuroFilterFloat(float minCutoff, float beta, float derivativeCutoff)
    {
        _minCutoff = minCutoff;
        _beta = beta;
        _derivativeCutoff = derivativeCutoff;
    }

    public void Reset(float value)
    {
        _lastValue = value;
        _lastDerivative = 0f;
        _initialized = true;
    }

    public float Filter(float value, float dt)
    {
        if (!_initialized)
        {
            Reset(value);
            return value;
        }

        float derivative = (value - _lastValue) / dt;
        float derivativeAlpha = ComputeAlpha(_derivativeCutoff, dt);
        float smoothedDerivative = Mathf.Lerp(_lastDerivative, derivative, derivativeAlpha);

        float cutoff = _minCutoff + _beta * Mathf.Abs(smoothedDerivative);
        float alpha = ComputeAlpha(cutoff, dt);
        float result = Mathf.Lerp(_lastValue, value, alpha);

        _lastValue = result;
        _lastDerivative = smoothedDerivative;
        return result;
    }

    public static float ComputeAlpha(float cutoff, float dt)
    {
        float tau = 1.0f / (2f * Mathf.PI * Mathf.Max(cutoff, 0.0001f));
        return 1.0f / (1.0f + tau / dt);
    }
}
