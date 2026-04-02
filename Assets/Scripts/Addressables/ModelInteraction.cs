using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

public class ModelInteraction : MonoBehaviour
{
    [Header("Toggles")]
    public bool canSliderRotate = true;
    public bool canPinchScale = true;

    /*[Header("Drag Rotation (horizontal)")]
    public float dragSpeed = 0.3f;
    public float dragMinAngle = -60f;
    public float dragMaxAngle = 60f;*/

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

    // drag
    /*bool _isDragging = false;
    Vector2 _lastDragPos;*/

    // pinch
    bool _isPinching = false;
    float _lastPinchDistance = 0f;

    public static ModelInteraction Current;

    public void Init(GameObject spawnedModel)
    {
        Current = this;
        _model = spawnedModel.transform;

        // store LOCAL instead of world
        _originalLocalPosition = _model.localPosition;
        _originalLocalRotation = _model.localRotation;
        _originalLocalScale = _model.localScale;

        if (verticalSlider != null && canSliderRotate)
        {
            verticalSlider.minValue = sliderMinAngle;
            verticalSlider.maxValue = sliderMaxAngle;
            verticalSlider.value = 0f;
            verticalSlider.onValueChanged.AddListener(OnSliderChanged);
        }
    }

    void Update()
    {
        if (_model == null) return;

        //HandleDragRotation();
        HandlePinchScale();
    }

    // ── Drag (horizontal rotation) ──────────────────────────

    /*void HandleDragRotation()
    {
        if (!canDragRotate) return;

        var touchscreen = Touchscreen.current;
        var mouse = Mouse.current;

        // touch
        if (touchscreen != null && touchscreen.primaryTouch.press.isPressed)
        {
            Vector2 touchPos = touchscreen.primaryTouch.position.ReadValue();

            if (touchscreen.primaryTouch.press.wasPressedThisFrame)
            {
                _isDragging = true;
                _lastDragPos = touchPos;
            }
            else if (_isDragging)
            {
                float delta = (touchPos.x - _lastDragPos.x) * dragSpeed;
                ApplyDragRotation(delta);
                _lastDragPos = touchPos;
            }
        }
        else if (touchscreen != null && touchscreen.primaryTouch.press.wasReleasedThisFrame)
        {
            _isDragging = false;
        }

        // mouse (editor testing)
        if (mouse != null)
        {
            if (mouse.leftButton.wasPressedThisFrame)
            {
                _isDragging = true;
                _lastDragPos = mouse.position.ReadValue();
            }
            else if (mouse.leftButton.isPressed && _isDragging)
            {
                Vector2 mousePos = mouse.position.ReadValue();
                float delta = (mousePos.x - _lastDragPos.x) * dragSpeed;
                ApplyDragRotation(delta);
                _lastDragPos = mousePos;
            }
            else if (mouse.leftButton.wasReleasedThisFrame)
            {
                _isDragging = false;
            }
        }
    }*/

    /*void ApplyDragRotation(float delta)
    {
        _currentDragAngle = Mathf.Clamp(
            _currentDragAngle - delta,
            dragMinAngle,
            dragMaxAngle
        );
        ApplyRotation();
    }*/

    // ── Slider (vertical rotation) ──────────────────────────

    void OnSliderChanged(float value)
    {
        if (!canSliderRotate) return;
        if (_model == null) return;

        _currentSliderAngle = value;
        ApplyRotation();
    }

    // ── Combined rotation ────────────────────────────────────

    void ApplyRotation()
    {
        if (_model == null) return;

        _model.localRotation = _originalLocalRotation
                * Quaternion.Euler(_currentSliderAngle, _currentDragAngle, 0f);
    }

    // ── Pinch Scale ──────────────────────────────────────────

    void HandlePinchScale()
    {
        if (!canPinchScale) return;

        var touchscreen = Touchscreen.current;
        if (touchscreen == null) return;

        // need exactly 2 touches
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

        Vector3 newScale = _model.localScale * scaleFactor;
        newScale.x = Mathf.Clamp(newScale.x, minScale, maxScale);
        newScale.y = Mathf.Clamp(newScale.y, minScale, maxScale);
        newScale.z = Mathf.Clamp(newScale.z, minScale, maxScale);

        _model.localScale = newScale;
        _lastPinchDistance = currentDistance;
    }

    // ── Reset ────────────────────────────────────────────────

    public static void ResetCurrent()
    {
        Current?.ResetModel();
    }

    public void ResetModel()
    {
        if (_model == null) return;

        _model.localPosition = _originalLocalPosition;
        _model.localRotation = _originalLocalRotation;
        _model.localScale = _originalLocalScale;

        _currentDragAngle = 0f;
        _currentSliderAngle = 0f;

        if (verticalSlider != null)
            verticalSlider.value = 0f;
    }

    public void Cleanup()
    {
        if (verticalSlider != null)
            verticalSlider.onValueChanged.RemoveListener(OnSliderChanged);

        if (Current == this) Current = null;
        _model = null;
    }
}
