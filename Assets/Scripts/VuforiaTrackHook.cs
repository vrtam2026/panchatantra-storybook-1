using UnityEngine;
using Vuforia;

public class VuforiaTrackHook : MonoBehaviour
{
    private ARTrackedPageNode pageNode;
    private ObserverBehaviour _observer;
    private bool _pendingFound = false; // target found before node was ready

    private void Awake()
    {
        Debug.Log($"[AR] Observer = {_observer}");

        /*        if (pageNode == null) pageNode = GetComponent<ARTrackedPageNode>();*/
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

    // called from CustomARHandler after model spawns
    public void SetPageNode(ARTrackedPageNode node)
    {
        Debug.Log($"[AR] SetPageNode called. Pending = {_pendingFound}");

        pageNode = node;

        // if target was already found before model finished downloading
        if (_pendingFound)
        {
            _pendingFound = false;
            pageNode.NotifyFound();
        }
    }

    // called from CustomARHandler when model is destroyed
    public void ClearPageNode()
    {
        if (pageNode != null)
            pageNode.NotifyLost();
        pageNode = null;
        _pendingFound = false;
    }

    private void OnTargetStatusChanged(ObserverBehaviour behaviour, TargetStatus targetStatus)
    {
        Debug.Log($"[AR] Status Changed: {targetStatus.Status}");

        bool trackedNow =
     targetStatus.Status == Status.TRACKED ||
     targetStatus.Status == Status.EXTENDED_TRACKED;

        if (trackedNow)
        {
            if (pageNode == null)
            {
                Debug.Log("[AR] Target found but pageNode not ready - pending");
                _pendingFound = true;
            }
            else
            {
                Debug.Log("[AR] Target found - NotifyFound()");
                pageNode.NotifyFound();
            }
        }
        else
        {
            Debug.Log("[AR] Target lost");
            _pendingFound = false;
            pageNode?.NotifyLost();
        }
    }
}
