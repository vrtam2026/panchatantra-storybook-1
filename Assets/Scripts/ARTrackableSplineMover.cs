using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Splines;
using UnityEngine.Rendering;

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

    // Position applied via LateUpdate  works in both URP and Built-in RP
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

    // LateUpdate runs after Animator, after physics  reliable in both URP and Built-in
    private void LateUpdate()
    {
        if (!_hasPending) return;
        if (objectToMove == null) { _hasPending = false; return; }
        objectToMove.SetPositionAndRotation(_pendingWorldPos, _pendingWorldRot);
        _hasPending = false;
    }

    private void CacheSpline()
    {
        if (splineContainer == null) return;
        if (splineIndex < 0 || splineIndex >= splineContainer.Splines.Count) return;
        _spline = splineContainer.Splines[splineIndex];
    }

    public bool ForceRestartFromBeginning(out string reason)
    {
        reason = string.Empty;

        Stop();
        _playRequested = false;
        IsFinished = false;
        IsPaused = false;
        IsPlaying = false;
        _hasPending = false;

        if (!ValidateSetup(out reason))
            return false;

        SetAtT(segments[0].startT);
        LateUpdate();

        _playRequested = true;
        _planRoutine = StartCoroutine(PlayPlanRoutine());
        reason = "Started";
        return true;
    }

    private bool ValidateSetup(out string reason)
    {
        reason = string.Empty;
        CacheSpline();

        if (!isActiveAndEnabled || !gameObject.activeInHierarchy)
        {
            reason = "Mover component or GameObject is inactive";
            return false;
        }
        if (splineContainer == null)
        {
            reason = "Spline Container is missing";
            return false;
        }
        if (splineIndex < 0 || splineIndex >= splineContainer.Splines.Count)
        {
            reason = $"Spline index {splineIndex} is out of range. Container has {splineContainer.Splines.Count} spline(s)";
            return false;
        }
        if (_spline == null)
        {
            reason = "Spline could not be cached";
            return false;
        }
        if (objectToMove == null)
        {
            reason = "Object To Move is missing";
            return false;
        }
        if (segments == null || segments.Count == 0)
        {
            reason = "No movement segments assigned";
            return false;
        }

        return true;
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
        if (!_playRequested)
        {
            _planRoutine = null;
            yield break;
        }
        if (!ValidateSetup(out string reason))
        {
            Debug.LogWarning($"[ARTrackableSplineMover] Cannot play '{name}': {reason}", this);
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