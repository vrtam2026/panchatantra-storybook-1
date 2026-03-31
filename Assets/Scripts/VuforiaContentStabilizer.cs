using UnityEngine;
using Vuforia;

public class VuforiaContentStabilizer : MonoBehaviour
{
    [SerializeField] private ObserverBehaviour observer;

    [Header("Quality gate")]
    public bool ignoreLimited = true;

    [Header("Smoothing")]
    [Range(0.01f, 1f)] public float posLerp = 0.15f;
    [Range(0.01f, 1f)] public float rotSlerp = 0.15f;
    public float posDeadbandMeters = 0.0015f;
    public float rotDeadbandDeg = 0.25f;

    Vector3 localPosOffset;
    Quaternion localRotOffset;

    Vector3 filteredPos;
    Quaternion filteredRot;
    bool hasInit;

    Renderer[] renderers;
    Canvas[] canvases;

    void Awake()
    {
        if (!observer) observer = GetComponentInParent<ObserverBehaviour>();
        if (!observer)
        {
            Debug.LogError("VuforiaContentStabilizer: Assign the ImageTarget ObserverBehaviour.");
            enabled = false;
            return;
        }

        renderers = GetComponentsInChildren<Renderer>(true);
        canvases = GetComponentsInChildren<Canvas>(true);
    }

    void Start()
    {
        // Capture offset while still parented under ImageTarget, then detach.
        if (transform.parent == observer.transform)
        {
            localPosOffset = transform.localPosition;
            localRotOffset = transform.localRotation;
            transform.SetParent(null, true);
        }
        else
        {
            var t = observer.transform;
            localPosOffset = Quaternion.Inverse(t.rotation) * (transform.position - t.position);
            localRotOffset = Quaternion.Inverse(t.rotation) * transform.rotation;
        }

        observer.OnTargetStatusChanged += OnStatus;
        OnStatus(observer, observer.TargetStatus);
    }

    void OnDestroy()
    {
        if (observer) observer.OnTargetStatusChanged -= OnStatus;
    }

    void OnStatus(ObserverBehaviour _, TargetStatus status)
    {
        bool good =
            status.Status == Status.TRACKED ||
            status.Status == Status.EXTENDED_TRACKED;

        if (ignoreLimited)
        {
            // LIMITED is low accuracy; discard if you need exact alignment. :contentReference[oaicite:6]{index=6}
            good = good && status.StatusInfo == StatusInfo.NORMAL;
        }

        SetVisible(good);

        if (good)
        {
            var desired = GetDesiredPose();
            filteredPos = desired.pos;
            filteredRot = desired.rot;
            transform.SetPositionAndRotation(filteredPos, filteredRot);
            hasInit = true;
        }
        else
        {
            hasInit = false;
        }
    }

    void LateUpdate()
    {
        if (!hasInit) return;

        var desired = GetDesiredPose();

        float d = Vector3.Distance(filteredPos, desired.pos);
        float a = Quaternion.Angle(filteredRot, desired.rot);

        if (d > posDeadbandMeters)
            filteredPos = Vector3.Lerp(filteredPos, desired.pos, posLerp);

        if (a > rotDeadbandDeg)
            filteredRot = Quaternion.Slerp(filteredRot, desired.rot, rotSlerp);

        transform.SetPositionAndRotation(filteredPos, filteredRot);
    }

    (Vector3 pos, Quaternion rot) GetDesiredPose()
    {
        var t = observer.transform;
        return (t.TransformPoint(localPosOffset), t.rotation * localRotOffset);
    }

    void SetVisible(bool on)
    {
        foreach (var r in renderers) if (r) r.enabled = on;
        foreach (var c in canvases) if (c) c.enabled = on;
    }
}
