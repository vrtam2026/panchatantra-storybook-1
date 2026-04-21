using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

public class ModelInteraction : MonoBehaviour
{
    // ------------------------------------------------------------------
    // Page type -- controls which global bucket this page reads/writes.
    // TwoD pages share one global angle + scale.
    // ThreeD pages share a completely separate global angle + scale.
    // Changing slider on a 2D page never affects 3D pages and vice versa.
    // ------------------------------------------------------------------
    public enum PageType { TwoD, ThreeD }

    [Header("Page Type")]
    [Tooltip("TwoD = uses 2D global slider and scale state.\nThreeD = uses 3D global state. Set this on every 3D ImageTarget.")]
    public PageType pageType = PageType.TwoD;

    [Header("Toggles")]
    [Tooltip("ON = slider visible and active for this page.\nOFF = slider hidden regardless of page type.")]
    public bool canSliderRotate = true;
    public bool canPinchScale = true;

    [Header("Slider Rotation (vertical)")]
    public float sliderMinAngle = -45f;
    public float sliderMaxAngle = 45f;
    public Slider verticalSlider;

    [Header("Pinch Scale")]
    public float minScale = 0.5f;
    public float maxScale = 3f;

    Transform _model;
    Vector3 _originalLocalPosition;
    Quaternion _originalLocalRotation;
    Vector3 _originalLocalScale;

    float _currentDragAngle = 0f;
    float _currentSliderAngle = 0f;

    // ------------------------------------------------------------------
    // Separate global state -- 2D and 3D never share values
    // ------------------------------------------------------------------
    public static float GlobalSliderAngle2D = 0f;
    public static float GlobalSliderAngle3D = 0f;
    public static float GlobalScaleMultiplier2D = 1f;
    public static float GlobalScaleMultiplier3D = 1f;

    // Routing helpers -- automatically pick the right bucket
    private bool Is2D => pageType == PageType.TwoD;

    private float ActiveSliderAngle
    {
        get => Is2D ? GlobalSliderAngle2D : GlobalSliderAngle3D;
        set { if (Is2D) GlobalSliderAngle2D = value; else GlobalSliderAngle3D = value; }
    }

    private float ActiveScaleMultiplier
    {
        get => Is2D ? GlobalScaleMultiplier2D : GlobalScaleMultiplier3D;
        set { if (Is2D) GlobalScaleMultiplier2D = value; else GlobalScaleMultiplier3D = value; }
    }

    // pinch
    bool _isPinching = false;
    float _lastPinchDistance = 0f;

    public System.Action OnTapped;
    public static ModelInteraction Current;

    // ------------------------------------------------------------------
    // DetachSlider -- removes ONLY this instance's listener from the slider.
    //
    // ROOT CAUSE FIX:
    //   All ImageTarget pages share ONE verticalSlider UI element in the scene.
    //   Each ModelInteraction wires itself to that slider via AddListener.
    //   Unity's RemoveListener is INSTANCE-SPECIFIC -- calling it on instance B
    //   does NOT remove instance A's listener. So without this, every page scan
    //   accumulates another listener on the slider.
    //   Result: slider fires OnSliderChanged on every previously-visited instance,
    //   which writes its pageType's global (2D or 3D) with the current value.
    //   The fix: before any instance takes over the slider, it calls DetachSlider
    //   on the previous Current to surgically remove that listener.
    // ------------------------------------------------------------------
    public void DetachSlider()
    {
        if (verticalSlider != null)
            verticalSlider.onValueChanged.RemoveListener(OnSliderChanged);
    }

    // ------------------------------------------------------------------
    // Init -- called by CustomARHandler after addressable spawns
    // ------------------------------------------------------------------

    public void Init(GameObject spawnedModel)
    {
        // BUGFIX: detach the previous active instance's slider listener before
        // this instance takes over. Without this, the old page's OnSliderChanged
        // stays wired and continues writing to its own 2D/3D global,
        // causing cross-contamination between page types.
        if (Current != null && Current != this)
            Current.DetachSlider();

        Current = this;
        _model = spawnedModel.transform;

        _originalLocalPosition = _model.localPosition;
        _originalLocalRotation = _model.localRotation;
        _originalLocalScale = _model.localScale;

        _currentSliderAngle = ActiveSliderAngle;
        _model.localScale = _originalLocalScale * ActiveScaleMultiplier;

        // Wire slider listener -- visibility controlled by CustomARHandler.FadeUI
        if (verticalSlider != null && canSliderRotate)
        {
            verticalSlider.minValue = sliderMinAngle;
            verticalSlider.maxValue = sliderMaxAngle;

            verticalSlider.onValueChanged.RemoveListener(OnSliderChanged);
            verticalSlider.SetValueWithoutNotify(ActiveSliderAngle);
            verticalSlider.onValueChanged.AddListener(OnSliderChanged);
        }

        ApplyRotation();
    }

    // ------------------------------------------------------------------
    // Resume -- grace-time tracking restore.
    // Rewires slider only. Never re-captures original values.
    // ------------------------------------------------------------------

    public void Resume()
    {
        if (_model == null) return;

        // BUGFIX: same guard as Init -- detach any other active instance's
        // listener before this instance resumes control of the shared slider.
        if (Current != null && Current != this)
            Current.DetachSlider();

        Current = this;

        if (verticalSlider != null && canSliderRotate)
        {
            verticalSlider.onValueChanged.RemoveListener(OnSliderChanged);
            verticalSlider.SetValueWithoutNotify(_currentSliderAngle);
            verticalSlider.onValueChanged.AddListener(OnSliderChanged);
        }

        ApplyRotation();
    }

    // ------------------------------------------------------------------
    // Unity lifecycle
    // ------------------------------------------------------------------

    void Update()
    {
        if (_model == null) return;
        HandlePinchScale();
    }

    // ------------------------------------------------------------------
    // Slider rotation
    // ------------------------------------------------------------------

    void OnSliderChanged(float value)
    {
        if (!canSliderRotate) return;
        if (_model == null) return;

        _currentSliderAngle = value;
        ActiveSliderAngle = value;   // writes to correct 2D or 3D global

        ApplyRotation();
    }

    void ApplyRotation()
    {
        if (_model == null) return;
        _model.localRotation = _originalLocalRotation
            * Quaternion.Euler(_currentSliderAngle, _currentDragAngle, 0f);
    }

    // ------------------------------------------------------------------
    // Pinch scale -- separate global per page type
    // ------------------------------------------------------------------

    void HandlePinchScale()
    {
        if (!canPinchScale) return;

        // BUGFIX: only the currently active instance processes pinch.
        // Without this, any ModelInteraction whose _model is still alive
        // (grace-period page) also runs HandlePinchScale every frame.
        // They receive the same touch delta and each write to their own
        // 2D or 3D global -- so pinching on a 3D page also mutates the
        // 2D scale global via a still-alive grace-period 2D instance.
        if (Current != this) return;

        var touchscreen = Touchscreen.current;
        if (touchscreen == null) return;

        bool touch0Active = touchscreen.touches[0].press.isPressed;
        bool touch1Active = touchscreen.touches[1].press.isPressed;

        if (!touch0Active || !touch1Active)
        {
            _isPinching = false;
            return;
        }

        Vector2 pos0 = touchscreen.touches[0].position.ReadValue();
        Vector2 pos1 = touchscreen.touches[1].position.ReadValue();
        float currentDistance = Vector2.Distance(pos0, pos1);

        if (!_isPinching)
        {
            _lastPinchDistance = currentDistance;
            _isPinching = true;
            return;
        }

        float delta = currentDistance - _lastPinchDistance;
        float scaleFactor = 1f + delta * 0.002f;

        ActiveScaleMultiplier = Mathf.Clamp(ActiveScaleMultiplier * scaleFactor, minScale, maxScale);

        // Zoom centered on the object's visual center.
        //
        // WHY parent-local space:
        //   _model is a child of an ImageTarget (Vuforia-controlled).
        //   The old code used _model.position (world space) for the compensation.
        //   Unity converts world position to localPosition via parent.InverseTransformPoint.
        //   If the parent has any rotation (Vuforia can apply this), subtracting a
        //   world-space delta from a world position gives the wrong localPosition --
        //   the axes are rotated, so world X != local X.
        //   Fix: measure the center drift in PARENT-LOCAL space, then adjust localPosition
        //   directly. This is immune to any parent rotation or Vuforia repositioning.
        Transform parent = _model.parent;

        // Step 1: center position in parent-local space BEFORE scale
        Vector3 centerWorldBefore = GetRendererWorldCenter();
        Vector3 centerInParentBefore = parent != null
            ? parent.InverseTransformPoint(centerWorldBefore)
            : centerWorldBefore;

        // Step 2: apply scale
        _model.localScale = _originalLocalScale * ActiveScaleMultiplier;

        // Step 3: center position in parent-local space AFTER scale
        Vector3 centerWorldAfter = GetRendererWorldCenter();
        Vector3 centerInParentAfter = parent != null
            ? parent.InverseTransformPoint(centerWorldAfter)
            : centerWorldAfter;

        // Step 4: shift localPosition to bring center back to where it was
        _model.localPosition += centerInParentBefore - centerInParentAfter;

        _lastPinchDistance = currentDistance;
    }

    private Vector3 GetRendererWorldCenter()
    {
        var renderers = _model.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0) return _model.position;

        Bounds combined = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
            combined.Encapsulate(renderers[i].bounds);

        return combined.center;
    }

    // ------------------------------------------------------------------
    // Tap callback
    // ------------------------------------------------------------------

    public void EnableTapCallback(System.Action callback)
    {
        OnTapped = callback;
    }

    // ------------------------------------------------------------------
    // Reset -- affects only this page type's globals
    // ------------------------------------------------------------------

    public static void ResetCurrent()
    {
        Current?.FullReset();
    }

    public void FullReset()
    {
        if (_model == null) return;

        ActiveSliderAngle = 0f;
        ActiveScaleMultiplier = 1f;

        _currentDragAngle = 0f;
        _currentSliderAngle = 0f;

        _model.localPosition = _originalLocalPosition;
        _model.localRotation = _originalLocalRotation;
        _model.localScale = _originalLocalScale;

        if (verticalSlider != null && canSliderRotate)
            verticalSlider.SetValueWithoutNotify(0f);
    }

    public void ResetModel()
    {
        if (_model == null) return;

        _model.localPosition = _originalLocalPosition;
        _model.localRotation = _originalLocalRotation;
        _model.localScale = _originalLocalScale * ActiveScaleMultiplier;

        _currentDragAngle = 0f;

        if (verticalSlider != null && canSliderRotate)
            verticalSlider.value = ActiveSliderAngle;
    }

    // ------------------------------------------------------------------
    // Cleanup -- full teardown, called on handler destroy
    // ------------------------------------------------------------------

    public void Cleanup()
    {
        if (verticalSlider != null)
            verticalSlider.onValueChanged.RemoveListener(OnSliderChanged);

        if (Current == this) Current = null;
        _model = null;
    }
}