using UnityEngine;
using Vuforia;

public class VuforiaTrackHook : MonoBehaviour
{
    [SerializeField] private ARTrackedPageNode pageNode;
    private ObserverBehaviour _observer;

    private void Awake()
    {
        if (pageNode == null) pageNode = GetComponent<ARTrackedPageNode>();
        _observer = GetComponent<ObserverBehaviour>();
    }

    private void OnEnable()
    {
        if (_observer != null)
            _observer.OnTargetStatusChanged += OnTargetStatusChanged;
    }

    private void OnDisable()
    {
        if (_observer != null)
            _observer.OnTargetStatusChanged -= OnTargetStatusChanged;
    }

    private void OnTargetStatusChanged(ObserverBehaviour behaviour, TargetStatus targetStatus)
    {
        if (pageNode == null) return;

        bool trackedNow =
            targetStatus.Status == Status.TRACKED ||
            targetStatus.Status == Status.EXTENDED_TRACKED;

        if (trackedNow) pageNode.NotifyFound();
        else pageNode.NotifyLost();
    }
}
