using UnityEngine;
using System.Collections.Generic;

[DisallowMultipleComponent]
public class ParallexWithAnimation : MonoBehaviour
{
    public enum GapAxis { LocalY, LocalZ }
    public enum InvertRule { None, All, Layer0, Last, Layer0AndLast }

    [Header("References")]
    public Transform cameraTransform;

    [Header("Layers (0 = far, last = near)")]
    public List<Transform> layers = new List<Transform>();

    [Header("Depth spacing")]
    public GapAxis gapAxis = GapAxis.LocalZ;
    public float gap = 0.01f;
    public bool hardLockGapAxis = true;
    public bool applyGapInEditMode = true;

    [Header("Parallax direction")]
    [Tooltip("Opposite means: camera right -> layer left.")]
    public InvertRule invertTranslation = InvertRule.All;

    [Tooltip("Opposite means: camera yaw right -> layer yaw-offset left.")]
    public InvertRule invertRotation = InvertRule.All;

    [Header("Parallax strength")]
    [Range(0f, 0.2f)] public float translationStrength = 0.05f;
    [Range(0f, 0.2f)] public float rotationStrength = 0.05f;

    [Header("Rotation handling")]
    [Range(0.5f, 15f)] public float rotationClampDegrees = 5f;
    public float rotationToOffsetScale = 0.0025f;

    [Header("Depth animation (immersive feel)")]
    [Tooltip("X: 0 far -> 1 near. Y: weight multiplier. For your case: FAR should be stronger than NEAR.")]
    public AnimationCurve depthWeightCurve = AnimationCurve.EaseInOut(0f, 1f, 1f, 0.1f);

    [Tooltip("Clamp XY offset magnitude (0 = no clamp).")]
    public float maxOffset = 0.03f;

    [Header("Anti-shake")]
    [Range(0.01f, 0.5f)] public float poseFilterTime = 0.12f;
    [Range(0f, 0.02f)] public float deadzone = 0.002f;

    [Header("Layer smoothing")]
    [Range(0.01f, 0.5f)] public float layerSmoothTime = 0.10f;

    [Header("Render order (prevents overlap flicker / vanishing)")]
    public bool enforceStableRenderOrder = true;
    public bool nearLayerOnTop = true;        // last layer draws on top
    public int sortingOrderBase = 0;
    public int sortingOrderStep = 1;
    public bool setSiblingOrderFallback = true; // for UI Images (no Canvas/SpriteRenderer on layer root)

    Vector3 camPosAnchorLocal;
    Quaternion camRotAnchorLocal;

    Vector3[] baseLocalPositions;
    Vector3[] layerVel;

    Vector3 camDeltaLocalFiltered;
    Vector2 rotOffsetFiltered;

    void Awake()
    {
        if (cameraTransform == null && Camera.main != null)
            cameraTransform = Camera.main.transform;
    }

    void Start()
    {
        // FIX #2: Apply gap first, then Reanchor so baseLocalPositions matches runtime positions
        ApplyEqualGapKeepOtherAxes();
        Reanchor();

        // FIX #1: Ensure stable render order when layers overlap
        ApplyStableRenderOrder();
    }

    void OnEnable()
    {
        if (cameraTransform != null && layers != null && layers.Count > 0)
            Reanchor();
    }

    public void Reanchor()
    {
        int count = (layers == null) ? 0 : layers.Count;

        baseLocalPositions = new Vector3[count];
        layerVel = new Vector3[count];

        for (int i = 0; i < count; i++)
            baseLocalPositions[i] = layers[i] ? layers[i].localPosition : Vector3.zero;

        if (cameraTransform)
        {
            camPosAnchorLocal = transform.InverseTransformPoint(cameraTransform.position);
            camRotAnchorLocal = Quaternion.Inverse(transform.rotation) * cameraTransform.rotation;

            camDeltaLocalFiltered = Vector3.zero;
            rotOffsetFiltered = Vector2.zero;
        }
    }

    void LateUpdate()
    {
        if (cameraTransform == null) return;
        if (layers == null || layers.Count == 0) return;

        if (baseLocalPositions == null || baseLocalPositions.Length != layers.Count ||
            layerVel == null || layerVel.Length != layers.Count)
        {
            // FIX #2: same ordering fix here too
            ApplyEqualGapKeepOtherAxes();
            Reanchor();

            // Keep render order stable if list changed
            ApplyStableRenderOrder();
        }

        float dt = Time.deltaTime;
        if (dt <= 0f) return;

        Vector3 camPosLocalNow = transform.InverseTransformPoint(cameraTransform.position);
        Vector3 camDeltaLocal = camPosLocalNow - camPosAnchorLocal;

        Quaternion camRotLocalNow = Quaternion.Inverse(transform.rotation) * cameraTransform.rotation;
        Quaternion deltaRotLocal = camRotLocalNow * Quaternion.Inverse(camRotAnchorLocal);

        Vector3 e = deltaRotLocal.eulerAngles;
        float pitch = ClampAngle(e.x);
        float yaw = ClampAngle(e.y);

        pitch = Mathf.Clamp(pitch, -rotationClampDegrees, rotationClampDegrees);
        yaw = Mathf.Clamp(yaw, -rotationClampDegrees, rotationClampDegrees);

        Vector2 rotOffset = new Vector2(yaw, -pitch) * rotationToOffsetScale;

        float alpha = 1f - Mathf.Exp(-dt / Mathf.Max(0.0001f, poseFilterTime));
        camDeltaLocalFiltered = Vector3.Lerp(camDeltaLocalFiltered, camDeltaLocal, alpha);
        rotOffsetFiltered = Vector2.Lerp(rotOffsetFiltered, rotOffset, alpha);

        if (camDeltaLocalFiltered.magnitude < deadzone) camDeltaLocalFiltered = Vector3.zero;
        if (rotOffsetFiltered.magnitude < deadzone) rotOffsetFiltered = Vector2.zero;

        for (int i = 0; i < layers.Count; i++)
        {
            Transform layer = layers[i];
            if (!layer) continue;

            float t = (layers.Count == 1) ? 1f : (float)i / (layers.Count - 1);

            float w = Mathf.Max(0f, depthWeightCurve.Evaluate(1f - t));

            float dirT = ShouldInvert(i, layers.Count, invertTranslation) ? -1f : 1f;
            float dirR = ShouldInvert(i, layers.Count, invertRotation) ? -1f : 1f;

            Vector2 xy =
                new Vector2(camDeltaLocalFiltered.x, camDeltaLocalFiltered.y) * (translationStrength * w * dirT) +
                rotOffsetFiltered * (rotationStrength * w * dirR);

            if (maxOffset > 0f)
                xy = Vector2.ClampMagnitude(xy, maxOffset);

            Vector3 target = baseLocalPositions[i];

            if (layers[0])
            {
                if (gapAxis == GapAxis.LocalY) target.y = baseLocalPositions[0].y + gap * i;
                else target.z = baseLocalPositions[0].z + gap * i;
            }

            target.x = baseLocalPositions[i].x + xy.x;
            target.y = (gapAxis == GapAxis.LocalY) ? target.y : (baseLocalPositions[i].y + xy.y);

            Vector3 newPos = Vector3.SmoothDamp(layer.localPosition, target, ref layerVel[i], layerSmoothTime, Mathf.Infinity, dt);

            if (hardLockGapAxis)
            {
                if (gapAxis == GapAxis.LocalY) newPos.y = target.y;
                else newPos.z = target.z;
            }

            layer.localPosition = newPos;
        }
    }

#if UNITY_EDITOR
    void OnValidate()
    {
        if (!applyGapInEditMode) return;
        if (Application.isPlaying) return;
        ApplyEqualGapKeepOtherAxes();

        // Optional preview of stable ordering in editor
        ApplyStableRenderOrder();
    }
#endif

    void ApplyEqualGapKeepOtherAxes()
    {
        if (layers == null || layers.Count == 0) return;
        if (!layers[0]) return;

        float a0 = (gapAxis == GapAxis.LocalY) ? layers[0].localPosition.y : layers[0].localPosition.z;

        for (int i = 0; i < layers.Count; i++)
        {
            if (!layers[i]) continue;

            Vector3 p = layers[i].localPosition;
            if (gapAxis == GapAxis.LocalY) p.y = a0 + gap * i;
            else p.z = a0 + gap * i;

            layers[i].localPosition = p;
        }
    }

    void ApplyStableRenderOrder()
    {
        if (!enforceStableRenderOrder) return;
        if (layers == null || layers.Count == 0) return;

        // sibling fallback only works if all layers share the same parent
        bool sameParent = true;
        Transform parent = null;
        for (int i = 0; i < layers.Count; i++)
        {
            if (!layers[i]) continue;
            if (parent == null) parent = layers[i].parent;
            else if (layers[i].parent != parent) { sameParent = false; break; }
        }

        for (int i = 0; i < layers.Count; i++)
        {
            Transform layer = layers[i];
            if (!layer) continue;

            int idx = nearLayerOnTop ? i : (layers.Count - 1 - i);
            int order = sortingOrderBase + idx * sortingOrderStep;

            var sr = layer.GetComponent<SpriteRenderer>();
            if (sr != null)
            {
                sr.sortingOrder = order;
                continue;
            }

            var canvas = layer.GetComponent<Canvas>();
            if (canvas != null)
            {
                canvas.overrideSorting = true;
                canvas.sortingOrder = order;
                continue;
            }

            if (setSiblingOrderFallback && sameParent && parent != null)
            {
                // For UI Images under same parent: later sibling draws on top
                layer.SetSiblingIndex(i);
            }
        }
    }

    static bool ShouldInvert(int index, int count, InvertRule rule)
    {
        int last = count - 1;
        switch (rule)
        {
            case InvertRule.None: return false;
            case InvertRule.All: return true;
            case InvertRule.Layer0: return index == 0;
            case InvertRule.Last: return index == last;
            case InvertRule.Layer0AndLast: return index == 0 || index == last;
            default: return false;
        }
    }

    static float ClampAngle(float a)
    {
        if (a > 180f) a -= 360f;
        return a;
    }
}