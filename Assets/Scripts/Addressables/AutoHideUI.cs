using UnityEngine;
using UnityEngine.InputSystem;

public class AutoHideUI : MonoBehaviour
{
    public float hideDelay = 3f;
    public GameObject uiRoot;

    float _timer;
    bool _isVisible = true;

    void Start()
    {
        ShowUI();
    }

    void Update()
    {
        bool touched = Touchscreen.current != null && Touchscreen.current.primaryTouch.press.isPressed
                    || Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame;

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
        uiRoot.SetActive(true);
        _isVisible = true;
        _timer = hideDelay;
    }

    void HideUI()
    {
        uiRoot.SetActive(false);
        _isVisible = false;
    }
}
