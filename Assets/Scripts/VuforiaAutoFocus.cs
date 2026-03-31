using UnityEngine;
using Vuforia;

public class VuforiaAutoFocus : MonoBehaviour
{
    void Start()
    {
        VuforiaApplication.Instance.OnVuforiaStarted += OnVuforiaStarted;
    }

    void OnDestroy()
    {
        VuforiaApplication.Instance.OnVuforiaStarted -= OnVuforiaStarted;
    }

    void OnVuforiaStarted()
    {
        bool ok = VuforiaBehaviour.Instance.CameraDevice
            .SetFocusMode(FocusMode.FOCUS_MODE_CONTINUOUSAUTO);

        if (!ok) Debug.Log("Continuous autofocus not supported on this device.");
    }
}
