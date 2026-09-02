using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Simple auto-hide helper for non-activity UI such as menu/replay controls.
/// It pauses while a story activity is running so activity taps do not toggle or hide UI unexpectedly.
/// </summary>
public class AutoHideUI : MonoBehaviour
{
    [Tooltip("How many seconds the normal menu UI stays visible after the screen is touched.")]
    public float hideDelay = 3f;
    [Tooltip("The normal menu UI to show and auto-hide. Do not use the ActivityPanel here.")]
    public GameObject uiRoot;

    [Header("Activity Safety")]
    [Tooltip("Recommended ON. While an activity is active, this script will not toggle or hide UI from screen taps.")]
    public bool pauseWhileActivityIsRunning = true;

    float _timer;
    bool _isVisible = true;

    void Start()
    {
        ShowUI();
    }

    void Update()
    {
        // The menu panel (slider / back / replay / reset) only means anything while a page
        // is actually being scanned. CustomARHandler.Current is the page currently tracked;
        // null means nothing is tracked right now.
        //
        // Without this check, this script re-showed the panel on the very next screen tap
        // even though tracking was lost -- so the slider sat on top of the "point the camera
        // at the page" prompt and never went away. CustomARHandler hides Slider_V, but this
        // script independently owns Slider_V's PARENT (MenuPanel), and the two had no shared
        // notion of whether a page was being tracked.
        if (CustomARHandler.Current == null)
        {
            if (_isVisible)
                HideUI();
            return;
        }

        if (pauseWhileActivityIsRunning && ContentController.AnyActivityRunning)
        {
            if (_isVisible)
                _timer = hideDelay;
            return;
        }

        bool touched = false;

#if ENABLE_INPUT_SYSTEM
        touched = (Touchscreen.current != null && Touchscreen.current.primaryTouch.press.wasPressedThisFrame)
               || (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame);
#else
        touched = Input.GetMouseButtonDown(0) || Input.touchCount > 0;
#endif

        if (touched)
        {
            ShowUI();
        }
        else if (_isVisible)
        {
            _timer -= Time.deltaTime;
            if (_timer <= 0f)
                HideUI();
        }
    }

    void ShowUI()
    {
        if (uiRoot == null) return;
        uiRoot.SetActive(true);
        _isVisible = true;
        _timer = hideDelay;
    }

    void HideUI()
    {
        if (uiRoot == null) return;
        uiRoot.SetActive(false);
        _isVisible = false;
    }
}
