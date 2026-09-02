using System;
using System.Collections.Generic;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
using UnityEditorInternal;
#endif

[ExecuteAlways]
[DisallowMultipleComponent]
public class ParallaxLayerStack : MonoBehaviour
{
    public enum ParallaxPlane { LocalXZ, LocalXY, LocalYZ }

    [Serializable]
    private struct LayerState
    {
        public Transform t;
        public Vector3 baseLocalPos;
        public Quaternion baseLocalRot;
    }

    [Header("References")]
    public Transform cameraTransform;

    [Header("Layers (0 = far, last = near)")]
    [SerializeField] private List<Transform> layers = new List<Transform>();

    [Header("Stacking / Overlap control")]
    public bool enforceEqualGap = true;

    [Tooltip("Axis in THIS object's local space used for stacking layers. Example: (0,0,1) stacks along local Z.")]
    public Vector3 separationAxisLocal = new Vector3(0f, 0f, 1f);

    [Min(0f)]
    public float layerGap = 0.01f;

    [Tooltip("If enabled, you can move/rotate a layer in edit mode and it becomes the new base pose.")]
    public bool allowManualLayerEdits = true;

    [Header("Parallax Position")]
    public ParallaxPlane parallaxPlane = ParallaxPlane.LocalXZ;

    [Min(0f)]
    public float parallaxStrength = 0.02f;

    [Range(0f, 5f)]
    public float nearLayerMultiplier = 1f;

    [Tooltip("Optional curve to shape depth falloff. X=normalized depth (0 far -> 1 near). Y=multiplier (0..1 recommended).")]
    public bool useDepthCurve = true;

    public AnimationCurve depthCurve = new AnimationCurve(
        new Keyframe(0f, 0f),
        new Keyframe(1f, 1f)
    );

    [Header("AR stability (reduces shaking)")]
    [Tooltip("Ignore tiny camera jitter below this (local units).")]
    [Min(0f)] public float positionDeadzone = 0.0005f;

    [Tooltip("Clamp sudden jumps (local units).")]
    [Min(0f)] public float maxPositionDelta = 0.03f;

    [Header("Parallax Rotation (optional, AR-noisy)")]
    public bool enableRotationParallax = false;

    [Range(0f, 1f)]
    public float rotationInfluence = 0.15f;

    [Range(0f, 45f)]
    public float rotationClampDegrees = 5f;

    [Header("Smoothing")]
    [Min(0f)]
    public float follow = 14f;

    [Header("Editor Preview")]
    public bool previewInEditMode = true;
    public bool autoRefreshWhenLayersChange = true;
    public bool recaptureAnchorOnLayerRefresh = true;

    // Runtime
    private readonly List<LayerState> states = new List<LayerState>();
    private Vector3 anchorCamLocalPos;
    private Quaternion anchorCamLocalRot = Quaternion.identity;

    private Vector3 smoothedDeltaPos;
    private Quaternion smoothedDeltaRot = Quaternion.identity;

    // Change tracking
    private int lastLayerCount = -1;
    private float lastLayerGap = float.NaN;
    private Vector3 lastAxis = new Vector3(float.NaN, float.NaN, float.NaN);
    private bool lastEnforceEqualGap;

    private const float ManualEditEps = 0.0005f;

    private void OnEnable()
    {
        AutoAssignCameraIfMissing();
        RefreshLayersIfNeeded(force: true);

        if (recaptureAnchorOnLayerRefresh) CaptureAnchor();
        ApplyParallax(immediate: true);
    }

    private void Update()
    {
        if (cameraTransform == null) return;
        if (!Application.isPlaying && !previewInEditMode) return;

        RefreshLayersIfNeeded(force: false);
        MaybeBakeEqualGapIfParamsChanged(force: false);

        ApplyParallax(immediate: false);
    }

    private void OnValidate()
    {
        AutoAssignCameraIfMissing();

        RefreshLayersIfNeeded(force: true);
        MaybeBakeEqualGapIfParamsChanged(force: true);

        if (recaptureAnchorOnLayerRefresh) CaptureAnchor();
        ApplyParallax(immediate: true);
    }

    private void AutoAssignCameraIfMissing()
    {
        if (cameraTransform != null) return;
        var cam = Camera.main;
        if (cam != null) cameraTransform = cam.transform;
    }

    public void CaptureAnchor()
    {
        if (cameraTransform == null) return;

        anchorCamLocalPos = transform.InverseTransformPoint(cameraTransform.position);
        anchorCamLocalRot = Quaternion.Inverse(transform.rotation) * cameraTransform.rotation;

        smoothedDeltaPos = Vector3.zero;
        smoothedDeltaRot = Quaternion.identity;
    }

    public void AutoFillFromChildren()
    {
        layers.Clear();
        for (int i = 0; i < transform.childCount; i++)
            layers.Add(transform.GetChild(i));

        RefreshLayersIfNeeded(force: true);
        if (recaptureAnchorOnLayerRefresh) CaptureAnchor();
        ApplyParallax(immediate: true);
    }

    public void RefreshLayersIfNeeded(bool force)
    {
        if (!autoRefreshWhenLayersChange && !force) return;

        layers.RemoveAll(l => l == null);

        if (!force && layers.Count == lastLayerCount) return;
        lastLayerCount = layers.Count;

        states.Clear();
        for (int i = 0; i < layers.Count; i++)
        {
            var t = layers[i];
            if (t == null) continue;

            states.Add(new LayerState
            {
                t = t,
                baseLocalPos = t.localPosition,
                baseLocalRot = t.localRotation
            });
        }

        // force re-eval
        lastLayerGap = float.NaN;
        lastAxis = new Vector3(float.NaN, float.NaN, float.NaN);
        lastEnforceEqualGap = !enforceEqualGap;

        if (enforceEqualGap) BakeEqualGapNow(recordUndo: false);
    }

    private void MaybeBakeEqualGapIfParamsChanged(bool force)
    {
        if (!enforceEqualGap)
        {
            lastEnforceEqualGap = false;
            return;
        }

        bool changed =
            force ||
            lastEnforceEqualGap != enforceEqualGap ||
            !Mathf.Approximately(lastLayerGap, layerGap) ||
            (lastAxis - separationAxisLocal).sqrMagnitude > 0.000001f;

        if (!changed) return;

        lastEnforceEqualGap = enforceEqualGap;
        lastLayerGap = layerGap;
        lastAxis = separationAxisLocal;

        BakeEqualGapNow(recordUndo: !Application.isPlaying);
    }

    public void BakeEqualGapNow()
    {
        BakeEqualGapNow(recordUndo: !Application.isPlaying);
    }

    private void BakeEqualGapNow(bool recordUndo)
    {
        if (states.Count <= 1) return;

        Vector3 axis = separationAxisLocal;
        if (axis.sqrMagnitude < 0.0001f) axis = Vector3.forward;
        axis.Normalize();

        Vector3 start = states[0].t != null ? states[0].t.localPosition : Vector3.zero;

#if UNITY_EDITOR
        if (recordUndo && !Application.isPlaying)
        {
            for (int i = 0; i < states.Count; i++)
            {
                var tr = states[i].t;
                if (tr == null) continue;
                Undo.RecordObject(tr, "Bake Parallax Layer Gap");
            }
        }
#endif

        for (int i = 0; i < states.Count; i++)
        {
            var tr = states[i].t;
            if (tr == null) continue;

            Vector3 p = start + axis * layerGap * i;
            tr.localPosition = p;

            var st = states[i];
            st.baseLocalPos = p;
            states[i] = st;
        }
    }

    private void ApplyParallax(bool immediate)
    {
        if (cameraTransform == null) return;
        if (states.Count == 0) return;

        Vector3 camLocal = transform.InverseTransformPoint(cameraTransform.position);
        Quaternion camLocalRot = Quaternion.Inverse(transform.rotation) * cameraTransform.rotation;

        Vector3 rawDeltaPos = ProjectToPlane(camLocal - anchorCamLocalPos);

        // AR stability: deadzone + clamp
        if (rawDeltaPos.magnitude < positionDeadzone)
            rawDeltaPos = Vector3.zero;

        if (maxPositionDelta > 0f)
            rawDeltaPos = Vector3.ClampMagnitude(rawDeltaPos, maxPositionDelta);

        float dt = Application.isPlaying ? Time.deltaTime : (1f / 60f);

        if (follow <= 0f || immediate)
        {
            smoothedDeltaPos = rawDeltaPos;
        }
        else
        {
            float a = 1f - Mathf.Exp(-follow * dt);
            smoothedDeltaPos = Vector3.Lerp(smoothedDeltaPos, rawDeltaPos, a);
        }

        Quaternion rawDeltaRot = Quaternion.Inverse(anchorCamLocalRot) * camLocalRot;
        rawDeltaRot = enableRotationParallax ? ClampRotation(rawDeltaRot, rotationClampDegrees) : Quaternion.identity;

        if (follow <= 0f || immediate)
        {
            smoothedDeltaRot = rawDeltaRot;
        }
        else
        {
            float a = 1f - Mathf.Exp(-follow * dt);
            smoothedDeltaRot = Quaternion.Slerp(smoothedDeltaRot, rawDeltaRot, a);
        }

        int n = states.Count;

        // IMPORTANT: layers[0] = far, layers[last] = near
        for (int i = 0; i < n; i++)
        {
            var st = states[i];
            if (st.t == null) continue;

            float near01 = (n <= 1) ? 1f : (float)i / (n - 1);  // 0 far -> 1 near

            float curveMul = useDepthCurve ? Mathf.Clamp01(depthCurve.Evaluate(near01)) : near01;
            float depthFactor = curveMul * Mathf.Lerp(1f, nearLayerMultiplier, near01);

            Vector3 offset = -smoothedDeltaPos * parallaxStrength * depthFactor;

            Quaternion rotOffset = Quaternion.identity;
            if (enableRotationParallax && rotationInfluence > 0f)
                rotOffset = Quaternion.Slerp(Quaternion.identity, smoothedDeltaRot, rotationInfluence * depthFactor);

#if UNITY_EDITOR
            bool editMode = !Application.isPlaying;
            if (editMode && allowManualLayerEdits && previewInEditMode)
            {
                Vector3 expectedPos = st.baseLocalPos + offset;
                if ((st.t.localPosition - expectedPos).sqrMagnitude > (ManualEditEps * ManualEditEps))
                {
                    st.baseLocalPos = st.t.localPosition - offset;
                }

                Quaternion expectedRot = st.baseLocalRot * rotOffset;
                float angle = Quaternion.Angle(st.t.localRotation, expectedRot);
                if (angle > 0.05f)
                {
                    st.baseLocalRot = st.t.localRotation * Quaternion.Inverse(rotOffset);
                }

                states[i] = st;
            }
#endif

            st.t.localPosition = st.baseLocalPos + offset;
            st.t.localRotation = st.baseLocalRot * rotOffset;
        }

#if UNITY_EDITOR
        if (!Application.isPlaying) SceneView.RepaintAll();
#endif
    }

    private Vector3 ProjectToPlane(Vector3 v)
    {
        switch (parallaxPlane)
        {
            case ParallaxPlane.LocalXY: return new Vector3(v.x, v.y, 0f);
            case ParallaxPlane.LocalYZ: return new Vector3(0f, v.y, v.z);
            default: return new Vector3(v.x, 0f, v.z);
        }
    }

    private Quaternion ClampRotation(Quaternion q, float clampDeg)
    {
        if (clampDeg <= 0f) return Quaternion.identity;

        Vector3 e = q.eulerAngles;
        e.x = NormalizeAngle(e.x);
        e.y = NormalizeAngle(e.y);
        e.z = NormalizeAngle(e.z);

        e.x = Mathf.Clamp(e.x, -clampDeg, clampDeg);
        e.y = Mathf.Clamp(e.y, -clampDeg, clampDeg);
        e.z = Mathf.Clamp(e.z, -clampDeg, clampDeg);

        return Quaternion.Euler(e);
    }

    private float NormalizeAngle(float deg)
    {
        deg %= 360f;
        if (deg > 180f) deg -= 360f;
        return deg;
    }
}

#if UNITY_EDITOR
[CustomEditor(typeof(ParallaxLayerStack))]
public class ParallaxLayerStackEditor : Editor
{
    private ReorderableList layerList;
    private SerializedProperty cameraTransform;
    private SerializedProperty layers;

    private SerializedProperty enforceEqualGap;
    private SerializedProperty separationAxisLocal;
    private SerializedProperty layerGap;
    private SerializedProperty allowManualLayerEdits;

    private SerializedProperty parallaxPlane;
    private SerializedProperty parallaxStrength;
    private SerializedProperty nearLayerMultiplier;
    private SerializedProperty useDepthCurve;
    private SerializedProperty depthCurve;

    private SerializedProperty positionDeadzone;
    private SerializedProperty maxPositionDelta;

    private SerializedProperty enableRotationParallax;
    private SerializedProperty rotationInfluence;
    private SerializedProperty rotationClampDegrees;

    private SerializedProperty follow;

    private SerializedProperty previewInEditMode;
    private SerializedProperty autoRefreshWhenLayersChange;
    private SerializedProperty recaptureAnchorOnLayerRefresh;

    private void OnEnable()
    {
        cameraTransform = serializedObject.FindProperty("cameraTransform");
        layers = serializedObject.FindProperty("layers");

        enforceEqualGap = serializedObject.FindProperty("enforceEqualGap");
        separationAxisLocal = serializedObject.FindProperty("separationAxisLocal");
        layerGap = serializedObject.FindProperty("layerGap");
        allowManualLayerEdits = serializedObject.FindProperty("allowManualLayerEdits");

        parallaxPlane = serializedObject.FindProperty("parallaxPlane");
        parallaxStrength = serializedObject.FindProperty("parallaxStrength");
        nearLayerMultiplier = serializedObject.FindProperty("nearLayerMultiplier");
        useDepthCurve = serializedObject.FindProperty("useDepthCurve");
        depthCurve = serializedObject.FindProperty("depthCurve");

        positionDeadzone = serializedObject.FindProperty("positionDeadzone");
        maxPositionDelta = serializedObject.FindProperty("maxPositionDelta");

        enableRotationParallax = serializedObject.FindProperty("enableRotationParallax");
        rotationInfluence = serializedObject.FindProperty("rotationInfluence");
        rotationClampDegrees = serializedObject.FindProperty("rotationClampDegrees");

        follow = serializedObject.FindProperty("follow");

        previewInEditMode = serializedObject.FindProperty("previewInEditMode");
        autoRefreshWhenLayersChange = serializedObject.FindProperty("autoRefreshWhenLayersChange");
        recaptureAnchorOnLayerRefresh = serializedObject.FindProperty("recaptureAnchorOnLayerRefresh");

        layerList = new ReorderableList(serializedObject, layers, true, true, true, true);
        layerList.drawHeaderCallback = rect => EditorGUI.LabelField(rect, "Layers (0 = far, last = near)");
        layerList.elementHeight = EditorGUIUtility.singleLineHeight + 6;

        layerList.drawElementCallback = (rect, index, isActive, isFocused) =>
        {
            rect.y += 3;
            rect.height = EditorGUIUtility.singleLineHeight;
            EditorGUI.PropertyField(rect, layers.GetArrayElementAtIndex(index), GUIContent.none);
        };
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();
        var s = (ParallaxLayerStack)target;

        EditorGUILayout.LabelField("Parallax Layer Stack", EditorStyles.boldLabel);

        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("1) References", EditorStyles.boldLabel);
        using (new EditorGUILayout.VerticalScope("box"))
        {
            EditorGUILayout.PropertyField(cameraTransform, new GUIContent("Camera Transform"));
            if (cameraTransform.objectReferenceValue == null)
                EditorGUILayout.HelpBox("If empty, it tries Camera.main.", MessageType.None);
        }

        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("2) Layers", EditorStyles.boldLabel);
        using (new EditorGUILayout.VerticalScope("box"))
        {
            layerList.DoLayoutList();

            EditorGUILayout.PropertyField(enforceEqualGap, new GUIContent("Enforce Equal Gap"));
            using (new EditorGUI.DisabledScope(!enforceEqualGap.boolValue))
            {
                EditorGUILayout.PropertyField(separationAxisLocal, new GUIContent("Separation Axis (Local)"));
                EditorGUILayout.PropertyField(layerGap, new GUIContent("Layer Gap"));

                if (GUILayout.Button("Bake Equal Gap Now"))
                    s.BakeEqualGapNow();
            }

            EditorGUILayout.PropertyField(allowManualLayerEdits, new GUIContent("Allow Manual Layer Edits"));

            if (GUILayout.Button("Auto Fill Layers From Children"))
            {
                Undo.RecordObject(s, "Auto Fill Parallax Layers");
                s.AutoFillFromChildren();
                EditorUtility.SetDirty(s);
            }
        }

        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("3) Parallax Position", EditorStyles.boldLabel);
        using (new EditorGUILayout.VerticalScope("box"))
        {
            EditorGUILayout.PropertyField(parallaxPlane, new GUIContent("Parallax Plane"));
            EditorGUILayout.PropertyField(parallaxStrength, new GUIContent("Strength"));
            EditorGUILayout.PropertyField(nearLayerMultiplier, new GUIContent("Near Layer Boost"));

            EditorGUILayout.PropertyField(useDepthCurve, new GUIContent("Use Depth Curve"));
            using (new EditorGUI.DisabledScope(!useDepthCurve.boolValue))
                EditorGUILayout.PropertyField(depthCurve, new GUIContent("Depth Curve"));
        }

        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("4) AR Stability", EditorStyles.boldLabel);
        using (new EditorGUILayout.VerticalScope("box"))
        {
            EditorGUILayout.PropertyField(positionDeadzone, new GUIContent("Position Deadzone"));
            EditorGUILayout.PropertyField(maxPositionDelta, new GUIContent("Max Position Delta"));
        }

        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("5) Rotation Parallax", EditorStyles.boldLabel);
        using (new EditorGUILayout.VerticalScope("box"))
        {
            EditorGUILayout.PropertyField(enableRotationParallax, new GUIContent("Enable Rotation Parallax"));
            using (new EditorGUI.DisabledScope(!enableRotationParallax.boolValue))
            {
                EditorGUILayout.PropertyField(rotationInfluence, new GUIContent("Rotation Influence"));
                EditorGUILayout.PropertyField(rotationClampDegrees, new GUIContent("Clamp Degrees"));
            }
        }

        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("6) Smoothing", EditorStyles.boldLabel);
        using (new EditorGUILayout.VerticalScope("box"))
        {
            EditorGUILayout.PropertyField(follow, new GUIContent("Follow"));
        }

        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("7) Editor Preview", EditorStyles.boldLabel);
        using (new EditorGUILayout.VerticalScope("box"))
        {
            EditorGUILayout.PropertyField(previewInEditMode, new GUIContent("Preview In Edit Mode"));
            EditorGUILayout.PropertyField(autoRefreshWhenLayersChange, new GUIContent("Auto Refresh On Layer Change"));
            EditorGUILayout.PropertyField(recaptureAnchorOnLayerRefresh, new GUIContent("Recapture Anchor On Refresh"));

            if (GUILayout.Button("Capture Anchor"))
            {
                Undo.RecordObject(s, "Capture Parallax Anchor");
                s.CaptureAnchor();
                EditorUtility.SetDirty(s);
            }
        }

        serializedObject.ApplyModifiedProperties();
    }
}
#endif
