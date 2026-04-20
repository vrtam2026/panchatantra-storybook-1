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
    // Init -- called by CustomARHandler after addressable spawns
    // ------------------------------------------------------------------

    public void Init(GameObject spawnedModel)
    {
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

        // Renderer-bounds center compensation for off-center pivot
        Vector3 worldCenterBefore = GetRendererWorldCenter();
        _model.localScale = _originalLocalScale * ActiveScaleMultiplier;
        Vector3 worldCenterAfter = GetRendererWorldCenter();
        _model.position -= (worldCenterAfter - worldCenterBefore);

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
    // Cleanup
    // ------------------------------------------------------------------

    public void Cleanup()
    {
        if (verticalSlider != null)
            verticalSlider.onValueChanged.RemoveListener(OnSliderChanged);

        if (Current == this) Current = null;
        _model = null;
    }
}