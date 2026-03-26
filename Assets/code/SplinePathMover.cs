using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Splines;

public class SplinePathMover : MonoBehaviour
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

    [Header("Rotation")]
    [SerializeField] private bool faceAlongSpline = false;
    [SerializeField] private Vector3 upAxis = Vector3.up;

    [Header("Plan")]
    [SerializeField] private List<Segment> segments = new();

    [Header("Animator (child)")]
    [SerializeField] private Animator animatorToControl;
    [SerializeField] private bool playAnimatorWhileMoving = true;
    [SerializeField] private bool freezeAnimatorOnComplete = true;

    public bool IsPlaying { get; private set; }
    public bool IsFinished { get; private set; }
    public bool IsPaused { get; private set; }

    private Coroutine _planRoutine;
    private Spline _spline;

    private static Vector3 ToV3(float3 v) => new Vector3(v.x, v.y, v.z);

    private void Awake()
    {
        // Auto-find animator in children if not assigned
        if (animatorToControl == null)
            animatorToControl = GetComponentInChildren<Animator>(true);

        CacheSpline();
    }

    private void CacheSpline()
    {
        if (splineContainer == null) return;
        if (splineIndex < 0 || splineIndex >= splineContainer.Splines.Count)
        {
            Debug.LogError($"[SplinePathMover] Spline index {splineIndex} out of range. Container has {splineContainer.Splines.Count} spline(s).", this);
            return;
        }
        _spline = splineContainer.Splines[splineIndex];
    }

    // Called by ARTrackedPageNode
    public void PlayOnce()
    {
        IsFinished = false;
        IsPaused = false;

        if (_planRoutine != null)
        {
            StopCoroutine(_planRoutine);
            _planRoutine = null;
        }

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
        if (animatorToControl != null && IsPlaying) animatorToControl.speed = 1f;
    }

    public void Stop()
    {
        IsPlaying = false;
        IsPaused = false;

        if (_planRoutine != null)
        {
            StopCoroutine(_planRoutine);
            _planRoutine = null;
        }
    }

    public void ResetToStart()
    {
        CacheSpline();
        if (_spline == null || segments == null || segments.Count == 0) return;
        ApplyT(segments[0].startT);
        IsFinished = false;
    }

    private IEnumerator PlayPlanRoutine()
    {
        CacheSpline();

        if (_spline == null || splineContainer == null || segments == null || segments.Count == 0)
        {
            Debug.LogWarning("[SplinePathMover] Cannot play -- spline or segments not set.", this);
            _planRoutine = null;
            yield break;
        }

        // Snap to start immediately
        ApplyT(segments[0].startT);

        IsPlaying = true;

        if (animatorToControl != null && playAnimatorWhileMoving)
            animatorToControl.speed = 1f;

        foreach (var seg in segments)
        {
            yield return MoveSegment(seg.startT, seg.endT, seg.moveSeconds);

            if (seg.waitAfterSeconds > 0f)
                yield return WaitPausable(seg.waitAfterSeconds);
        }

        IsPlaying = false;
        IsFinished = true;
        _planRoutine = null;

        if (animatorToControl != null && playAnimatorWhileMoving && freezeAnimatorOnComplete)
            animatorToControl.speed = 0f;
    }

    private IEnumerator MoveSegment(float startT, float endT, float seconds)
    {
        if (seconds <= 0f)
        {
            ApplyT(endT);
            yield break;
        }

        float elapsed = 0f;

        while (elapsed < seconds)
        {
            if (IsPaused) { yield return null; continue; }

            elapsed += Time.deltaTime;
            float raw = Mathf.Clamp01(elapsed / seconds);
            float smooth = raw * raw * (3f - 2f * raw); // smoothstep
            ApplyT(Mathf.Lerp(startT, endT, smooth));

            yield return null;
        }

        ApplyT(endT);
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

    // Moves THIS GameObject (All_GameObject) — no Animator on this object, zero conflict
    private void ApplyT(float t)
    {
        if (_spline == null || splineContainer == null) return;

        t = Mathf.Clamp01(t);

        Vector3 localPos = ToV3(SplineUtility.EvaluatePosition(_spline, t));
        Vector3 worldPos = splineContainer.transform.TransformPoint(localPos);

        if (faceAlongSpline)
        {
            Vector3 localTan = ToV3(SplineUtility.EvaluateTangent(_spline, t));
            Vector3 worldTan = splineContainer.transform.TransformDirection(localTan).normalized;
            Vector3 worldUp = splineContainer.transform.TransformDirection(upAxis).normalized;
            Quaternion worldRot = Quaternion.LookRotation(worldTan, worldUp);
            transform.SetPositionAndRotation(worldPos, worldRot);
        }
        else
        {
            transform.position = worldPos;
        }
    }
}