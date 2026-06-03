using System.Collections;
using UnityEngine;
using Vuforia;

public class VuforiaTrackHook : MonoBehaviour
{
    [Header("Tracking Stability")]
    [Tooltip("ON = if tracking is lost for a very short moment, the page will not be told to stop immediately. This reduces fast lost/found flicker for kids holding the book.")]
    public bool keepContentVisibleOnShortTrackingLoss = true;

    [Tooltip("How long to wait before treating tracking as really lost. Recommended: 0.4 to 0.8 seconds.")]
    [Min(0f)] public float lostTrackingGraceSeconds = 0.6f;

    private ARTrackedPageNode pageNode;
    private ObserverBehaviour _observer;
    private Coroutine _lostTrackingRoutine;

    private bool _pendingFound = false;
    private bool _isCurrentlyTracked = false;
    private bool _notifiedCurrentNode = false;
    private bool _isQuitting = false;

    private void Awake()
    {
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

        StopPendingLostRoutine();
    }

    private void OnApplicationQuit()
    {
        _isQuitting = true;
        StopPendingLostRoutine();
    }

    // Called from CustomARHandler after the addressable page prefab spawns.
    public void SetPageNode(ARTrackedPageNode node)
    {
        if (pageNode != node)
        {
            pageNode = node;
            _notifiedCurrentNode = false;
        }

        if (pageNode == null) return;

        if ((_pendingFound || _isCurrentlyTracked) && !_notifiedCurrentNode)
        {
            StopPendingLostRoutine();
            _pendingFound = false;
            _notifiedCurrentNode = true;
            pageNode.NotifyFound();
        }
    }

    // Called from CustomARHandler when the spawned content is released.
    public void ClearPageNode()
    {
        StopPendingLostRoutine();

        if (pageNode != null && _notifiedCurrentNode)
            SafeNotifyLost();

        pageNode = null;
        _pendingFound = false;
        _notifiedCurrentNode = false;
    }

    private void OnTargetStatusChanged(ObserverBehaviour behaviour, TargetStatus targetStatus)
    {
        if (_isQuitting)
            return;

        bool trackedNow = IsTrackedStatus(targetStatus.Status);

        if (trackedNow)
        {
            StopPendingLostRoutine();
            _isCurrentlyTracked = true;

            if (pageNode == null)
            {
                _pendingFound = true;
                return;
            }

            if (_notifiedCurrentNode)
                return;

            _pendingFound = false;
            _notifiedCurrentNode = true;
            pageNode.NotifyFound();
        }
        else
        {
            _isCurrentlyTracked = false;
            _pendingFound = false;

            if (!_notifiedCurrentNode)
                return;

            if (keepContentVisibleOnShortTrackingLoss && lostTrackingGraceSeconds > 0f)
            {
                if (_lostTrackingRoutine == null)
                    _lostTrackingRoutine = VuforiaTrackHookDelayRunner.Run(NotifyLostAfterGrace());
                return;
            }

            NotifyLostNow();
        }
    }

    private IEnumerator NotifyLostAfterGrace()
    {
        yield return new WaitForSeconds(lostTrackingGraceSeconds);
        _lostTrackingRoutine = null;

        if (_isQuitting || _isCurrentlyTracked)
            yield break;

        NotifyLostNow();
    }

    private void NotifyLostNow()
    {
        if (!_notifiedCurrentNode) return;

        _notifiedCurrentNode = false;
        SafeNotifyLost();
    }

    private void SafeNotifyLost()
    {
        if (pageNode == null)
            return;

        // Do not start work on a page object that is already inactive or being destroyed.
        if (!pageNode.gameObject.activeInHierarchy)
            return;

        pageNode.NotifyLost();
    }

    private void StopPendingLostRoutine()
    {
        if (_lostTrackingRoutine == null) return;

        VuforiaTrackHookDelayRunner.Stop(_lostTrackingRoutine);
        _lostTrackingRoutine = null;
    }

    private static bool IsTrackedStatus(Status status)
    {
        return status == Status.TRACKED ||
               status == Status.EXTENDED_TRACKED ||
               status == Status.LIMITED;
    }
}

internal sealed class VuforiaTrackHookDelayRunner : MonoBehaviour
{
    private static VuforiaTrackHookDelayRunner _instance;

    private static VuforiaTrackHookDelayRunner Instance
    {
        get
        {
            if (_instance != null)
                return _instance;

            GameObject go = new GameObject("Vuforia Track Hook Delay Runner");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<VuforiaTrackHookDelayRunner>();
            return _instance;
        }
    }

    public static Coroutine Run(IEnumerator routine)
    {
        if (routine == null)
            return null;

        return Instance.StartCoroutine(routine);
    }

    public static void Stop(Coroutine routine)
    {
        if (routine == null || _instance == null)
            return;

        _instance.StopCoroutine(routine);
    }

    private void OnDestroy()
    {
        if (_instance == this)
            _instance = null;
    }
}
