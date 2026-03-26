using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Splines;

public class ARTrackableSplineMover : MonoBehaviour
{
    [Serializable]
    public struct Segment
    {
        [Range(0f, 1f)] public float startT;
        [Range(0f, 1f)] public float endT;
        [Min(0f)] public float moveSeconds;
        [Min(0f)] public float waitAfterSeconds;
    }

    [Header("Spline")]
    [SerializeField] private SplineContainer splineContainer;
    [SerializeField] private int splineIndex = 0;

    [Header("Target")]
    [SerializeField] private Transform objectToMove;

    [Header("Rotation")]
    [SerializeField] private bool faceAlongSpline = false;
    [SerializeField] private Vector3 upAxis = Vector3.up;

    [Header("Plan")]
    [SerializeField] private List<Segment> segments = new();

    [Header("Animator")]
    [SerializeField] private Animator animatorToControl;
    [SerializeField] private bool playAnimatorWhileMoving = true;
    [SerializeField] private bool freezeAnimatorOnComplete = true;

    public bool IsPlaying { get; private set; }
    public bool IsFinished { get; private set; }
    public bool IsPaused { get; private set; }

    private Coroutine _planRoutine;
    private Spline _spline;
    private bool _playRequested;

    // Position override — applied in onPreRender, after Animator + Vuforia
    private bool _hasPending;
    private Vector3 _pendingWorldPos;
    private Quaternion _pendingWorldRot;

    private static Vector3 ToV3(float3 v) => new Vector3(v.x, v.y, v.z);

    private void Awake()
    {
        if (objectToMove == null) objectToMove = transform;

        if (animatorToControl == null && objectToMove != null)
            animatorToControl = objectToMove.GetComponentInChildren<Animator>(true);

        CacheSpline();
    }

    private void OnEnable()
    {
        Camera.onPreRender += OnAnyPreRender;
    }

    private void OnDisable()
    {
        Camera.onPreRender -= OnAnyPreRender;
        _hasPending = false;
    }

    // Fires right before EACH camera renders — after Animator, after Vuforia, after all LateUpdates
    // This is the absolute last point before pixels are drawn
    private void OnAnyPreRender(Camera cam)
    {
        if (!_hasPending) return;
        if (objectToMove == null) { _hasPending = false; return; }

        objectToMove.SetPositionAndRotation(_pendingWorldPos, _pendingWorldRot);

        // Only clear after the main/first camera so multi-camera setups are fine
        _hasPending = false;
    }

    private void CacheSpline()
    {
        if (splineContainer == null) return;
        if (splineIndex < 0 || splineIndex >= splineContainer.Splines.Count) return;
        _spline = splineContainer.Splines[splineIndex];
    }

    public void PlayOnce()
    {
        _playRequested = true;
        IsFinished = false;
        IsPaused = false;

        if (_planRoutine == null)
            _planRoutine = StartCoroutine(PlayPlanRoutine());
    }

    public void Pause()
    {
        IsPaused = true;
        if (animatorToControl != null) animatorToControl.speed = 0f;
    }

    public void Resume()
    {
        IsPaused = false;
        if (animatorToControl != null) animatorToControl.speed = 1f;
    }

    public void Stop()
    {
        IsPlaying = false;
        _hasPending = false;
        if (_planRoutine != null)
        {
            StopCoroutine(_planRoutine);
            _planRoutine = null;
        }
    }

    public void ResetToStart()
    {
        CacheSpline();
        if (_spline == null || splineContainer == null || objectToMove == null) return;
        if (segments == null || segments.Count == 0) return;
        SetAtT(segments[0].startT);
        IsFinished = false;
    }

    private IEnumerator PlayPlanRoutine()
    {
        CacheSpline();
        if (!_playRequested || _spline == null || splineContainer == null
            || objectToMove == null || segments.Count == 0)
        {
            _planRoutine = null;
            yield break;
        }

        SetAtT(segments[0].startT);

        IsPlaying = true;
        if (animatorToControl != null && playAnimatorWhileMoving)
            animatorToControl.speed = 1f;

        foreach (var seg in segments)
        {
            yield return MoveAlongSpline(seg.startT, seg.endT, seg.moveSeconds);

            if (seg.waitAfterSeconds > 0f)
                yield return WaitPausable(seg.waitAfterSeconds);
        }

        IsPlaying = false;
        IsFinished = true;
        _playRequested = false;
        _planRoutine = null;

        if (animatorToControl != null && playAnimatorWhileMoving && freezeAnimatorOnComplete)
            animatorToControl.speed = 0f;
    }

    private IEnumerator MoveAlongSpline(float startT, float endT, float seconds)
    {
        if (seconds <= 0f)
        {
            SetAtT(endT);
            yield break;
        }

        float elapsed = 0f;
        while (elapsed < seconds)
        {
            if (IsPaused) { yield return null; continue; }

            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / seconds);
            t = t * t * (3f - 2f * t); // smoothstep

            float along = Mathf.Lerp(startT, endT, t);
            SetAtT(along);

            yield return null;
        }

        SetAtT(endT);
    }

    private IEnumerator WaitPausable(float seconds)
    {
        float remaining = seconds;
        while (remaining > 0f)
        {
            if (!IsPaused) remaining -= Time.deltaTime;
            yield return null;
        }
    }

    private void SetAtT(float t)
    {
        if (_spline == null || splineContainer == null || objectToMove == null) return;

        t = Mathf.Clamp01(t);

        Vector3 localPos = ToV3(SplineUtility.EvaluatePosition(_spline, t));
        Vector3 worldPos = splineContainer.transform.TransformPoint(localPos);

        Quaternion worldRot = objectToMove.rotation;
        if (faceAlongSpline)
        {
            Vector3 localTan = ToV3(SplineUtility.EvaluateTangent(_spline, t));
            Vector3 worldTan = splineContainer.transform.TransformDirection(localTan).normalized;
            Vector3 worldUp = splineContainer.transform.TransformDirection(upAxis).normalized;
            worldRot = Quaternion.LookRotation(worldTan, worldUp);
        }

        _pendingWorldPos = worldPos;
        _pendingWorldRot = worldRot;
        _hasPending = true;
    }
}