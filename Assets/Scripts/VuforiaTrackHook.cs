using UnityEngine;
using Vuforia;

public class VuforiaTrackHook : MonoBehaviour
{
    [Header("Tracking Rules")]
    [SerializeField] private bool treatLimitedAsTracked = true;

    private ARTrackedPageNode _pageNode;
    private ObserverBehaviour _observer;

    private bool _isTracked;
    private bool _isDestroying;

    private void Awake()
    {
        _observer = GetComponent<ObserverBehaviour>();
    }

    private void OnEnable()
    {
        if (_observer == null)
            _observer = GetComponent<ObserverBehaviour>();

        if (_observer != null)
            _observer.OnTargetStatusChanged += OnTargetStatusChanged;
    }

    private void OnDisable()
    {
        if (_observer != null)
            _observer.OnTargetStatusChanged -= OnTargetStatusChanged;
    }

    private void OnDestroy()
    {
        _isDestroying = true;

        if (_observer != null)
            _observer.OnTargetStatusChanged -= OnTargetStatusChanged;
    }

    public void SetPageNode(ARTrackedPageNode node)
    {
        if (_isDestroying || !this) return;

        _pageNode = node;

        if (_isTracked && _pageNode != null)
            _pageNode.NotifyFound();
    }

    public void ClearPageNode()
    {
        if (_isDestroying || !this) return;

        if (_pageNode != null && _isTracked)
            _pageNode.NotifyLost();

        _pageNode = null;
    }

    public void ClearForReplay()
    {
        if (_isDestroying || !this) return;

        _pageNode = null;
    }

    private void OnTargetStatusChanged(ObserverBehaviour behaviour, TargetStatus targetStatus)
    {
        if (_isDestroying || !this) return;

        try
        {
            bool newTrackedState = IsTrackedStatus(targetStatus);

            if (newTrackedState == _isTracked)
                return;

            _isTracked = newTrackedState;

            if (_pageNode == null)
                return;

            if (_isTracked)
                _pageNode.NotifyFound();
            else
                _pageNode.NotifyLost();
        }
        catch (MissingReferenceException)
        {
            // Vuforia can send one late callback while the object is being disabled.
        }
    }

    private bool IsTrackedStatus(TargetStatus targetStatus)
    {
        if (targetStatus.Status == Status.TRACKED)
            return true;

        if (targetStatus.Status == Status.EXTENDED_TRACKED)
            return true;

        if (treatLimitedAsTracked && targetStatus.Status == Status.LIMITED)
            return true;

        return false;
    }
}