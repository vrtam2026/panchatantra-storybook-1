using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Reusable tap source for story activities.
/// Counts a tap only after a valid press-and-release.
/// UI buttons, sliders, scroll views, and drags are ignored, so child UI does not accidentally trigger screen tap activities.
/// </summary>
public class StoryActivityTapInput : MonoBehaviour
{
    [Header("Raycast")]
    [SerializeField] protected Camera raycastCamera;
    [SerializeField] protected LayerMask raycastLayers = ~0;
    [SerializeField] protected float maxRayDistance = 100f;

    [Header("Tap Safety")]
    [Tooltip("Recommended ON. Prevents UI buttons/sliders from being counted as story screen taps.")]
    [SerializeField] protected bool ignoreTapsOverUI = true;

    [Tooltip("Tap is accepted only when the finger/mouse is released. This prevents slider drag start from counting as a tap.")]
    [SerializeField] protected bool validateTapOnRelease = true;

    [Tooltip("Maximum movement allowed between press and release. Bigger movement is treated as drag, not tap.")]
    [SerializeField] protected float maxTapMovePixels = 25f;

    [Tooltip("Maximum time allowed for a tap. Longer press can be used by hold activities later and is not treated as a normal tap.")]
    [SerializeField] protected float maxTapDuration = 0.6f;

    [Tooltip("Small cooldown after touching UI. Prevents releasing a slider or button from leaking into screen tap activity.")]
    [SerializeField] protected float uiTapBlockSeconds = 0.12f;

    private readonly Dictionary<int, PointerTapState> _pointers = new Dictionary<int, PointerTapState>();
    private float _lastUIInteractionTime = -999f;
    private int _lastBroadcastFrame = -1;

    public Camera RaycastCamera => raycastCamera;

    public void UseCameraIfMissing(Camera cameraToUse)
    {
        if (raycastCamera == null && cameraToUse != null)
            raycastCamera = cameraToUse;
    }

    public void SetRaycastCamera(Camera cameraToUse)
    {
        if (cameraToUse != null)
            raycastCamera = cameraToUse;
    }

    protected virtual void Awake()
    {
        if (raycastCamera == null)
            raycastCamera = Camera.main;
    }

    protected virtual void Update()
    {
#if ENABLE_INPUT_SYSTEM
        UpdateNewInputSystem();
#elif ENABLE_LEGACY_INPUT_MANAGER
        UpdateLegacyInput();
#endif
    }

#if ENABLE_INPUT_SYSTEM
    private void UpdateNewInputSystem()
    {
        if (Mouse.current != null)
        {
            Vector2 pos = Mouse.current.position.ReadValue();
            if (Mouse.current.leftButton.wasPressedThisFrame)
                BeginPointer(-1, pos);
            if (Mouse.current.leftButton.isPressed)
                MovePointer(-1, pos);
            if (Mouse.current.leftButton.wasReleasedThisFrame)
                EndPointer(-1, pos, false);
        }

        if (Touchscreen.current != null)
        {
            UnityEngine.InputSystem.Controls.TouchControl touch = Touchscreen.current.primaryTouch;
            int id = touch.touchId.ReadValue();
            Vector2 pos = touch.position.ReadValue();

            if (touch.press.wasPressedThisFrame)
                BeginPointer(id, pos);
            if (touch.press.isPressed)
                MovePointer(id, pos);
            if (touch.press.wasReleasedThisFrame)
                EndPointer(id, pos, false);
        }
    }
#elif ENABLE_LEGACY_INPUT_MANAGER
    private void UpdateLegacyInput()
    {
        if (Input.GetMouseButtonDown(0))
            BeginPointer(-1, Input.mousePosition);
        if (Input.GetMouseButton(0))
            MovePointer(-1, Input.mousePosition);
        if (Input.GetMouseButtonUp(0))
            EndPointer(-1, Input.mousePosition, false);

        for (int i = 0; i < Input.touchCount; i++)
        {
            Touch touch = Input.GetTouch(i);
            int id = touch.fingerId;

            switch (touch.phase)
            {
                case TouchPhase.Began:
                    BeginPointer(id, touch.position);
                    break;

                case TouchPhase.Moved:
                case TouchPhase.Stationary:
                    MovePointer(id, touch.position);
                    break;

                case TouchPhase.Ended:
                    EndPointer(id, touch.position, false);
                    break;

                case TouchPhase.Canceled:
                    EndPointer(id, touch.position, true);
                    break;
            }
        }
    }
#endif

    public virtual void HandleTap(Vector2 screenPosition, int pointerId = -1)
    {
        // Compatibility entry point for older code. It still uses all safety checks.
        BeginPointer(pointerId, screenPosition);
        EndPointer(pointerId, screenPosition, false);
    }

    protected virtual void BeginPointer(int pointerId, Vector2 screenPosition)
    {
        bool overUI = ignoreTapsOverUI && IsScreenPositionOverUI(screenPosition, pointerId);
        if (overUI)
            _lastUIInteractionTime = Time.unscaledTime;

        _pointers[pointerId] = new PointerTapState
        {
            pointerId = pointerId,
            startPosition = screenPosition,
            lastPosition = screenPosition,
            startTime = Time.unscaledTime,
            startedOverUI = overUI,
            movedTooFar = false
        };

        if (!validateTapOnRelease && !overUI)
            BroadcastTap(screenPosition);
    }

    protected virtual void MovePointer(int pointerId, Vector2 screenPosition)
    {
        if (!_pointers.TryGetValue(pointerId, out PointerTapState state))
            return;

        state.lastPosition = screenPosition;
        if (Vector2.Distance(state.startPosition, screenPosition) > maxTapMovePixels)
            state.movedTooFar = true;

        if (ignoreTapsOverUI && IsScreenPositionOverUI(screenPosition, pointerId))
            _lastUIInteractionTime = Time.unscaledTime;

        _pointers[pointerId] = state;
    }

    protected virtual void EndPointer(int pointerId, Vector2 screenPosition, bool cancelled)
    {
        if (!_pointers.TryGetValue(pointerId, out PointerTapState state))
            return;

        _pointers.Remove(pointerId);

        if (cancelled)
            return;

        bool endedOverUI = ignoreTapsOverUI && IsScreenPositionOverUI(screenPosition, pointerId);
        if (endedOverUI)
            _lastUIInteractionTime = Time.unscaledTime;

        float duration = Time.unscaledTime - state.startTime;
        float movement = Vector2.Distance(state.startPosition, screenPosition);
        bool recentlyTouchedUI = ignoreTapsOverUI && Time.unscaledTime - _lastUIInteractionTime <= uiTapBlockSeconds;

        if (state.startedOverUI || endedOverUI || recentlyTouchedUI)
            return;

        if (state.movedTooFar || movement > maxTapMovePixels)
            return;

        if (maxTapDuration > 0f && duration > maxTapDuration)
            return;

        BroadcastTap(screenPosition);
    }

    protected virtual void BroadcastTap(Vector2 screenPosition)
    {
        // Avoid duplicate event if both mouse and touch report the same press in the same frame.
        if (_lastBroadcastFrame == Time.frameCount)
            return;

        _lastBroadcastFrame = Time.frameCount;

        if (raycastCamera == null)
            raycastCamera = Camera.main;

        ActivityInputData data = new ActivityInputData
        {
            type = ActivityInputType.ScreenTap,
            screenPosition = screenPosition,
            hitObject = null,
            optionIndex = -1
        };

        if (raycastCamera != null)
        {
            Ray ray = raycastCamera.ScreenPointToRay(screenPosition);
            RaycastHit[] hits = Physics.RaycastAll(ray, maxRayDistance, raycastLayers, QueryTriggerInteraction.Collide);

            if (hits != null && hits.Length > 0)
            {
                Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

                List<GameObject> hitObjects = new List<GameObject>(hits.Length);
                for (int i = 0; i < hits.Length; i++)
                {
                    if (hits[i].collider == null)
                        continue;

                    GameObject hitObject = hits[i].collider.gameObject;
                    if (hitObject != null && !hitObjects.Contains(hitObject))
                        hitObjects.Add(hitObject);
                }

                if (hitObjects.Count > 0)
                {
                    data.type = ActivityInputType.ModelTap;
                    data.hitObject = hitObjects[0];
                    data.hitObjects = hitObjects.ToArray();
                }
            }
        }

        StoryActivityInputRouter.Broadcast(data);
    }

    protected virtual bool IsScreenPositionOverUI(Vector2 screenPosition, int pointerId)
    {
        if (EventSystem.current == null)
            return false;

        // Native Unity check first.
        if (pointerId >= 0 && EventSystem.current.IsPointerOverGameObject(pointerId))
            return true;
        if (pointerId < 0 && EventSystem.current.IsPointerOverGameObject())
            return true;

        // Manual raycast is more reliable for slider/scroll cases and mixed input projects.
        PointerEventData eventData = new PointerEventData(EventSystem.current)
        {
            position = screenPosition
        };

        List<RaycastResult> results = new List<RaycastResult>();
        EventSystem.current.RaycastAll(eventData, results);
        return results.Count > 0;
    }

    private struct PointerTapState
    {
        public int pointerId;
        public Vector2 startPosition;
        public Vector2 lastPosition;
        public float startTime;
        public bool startedOverUI;
        public bool movedTooFar;
    }
}
