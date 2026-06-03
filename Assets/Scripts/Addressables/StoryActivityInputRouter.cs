using System;
using UnityEngine;

/// <summary>
/// Project-independent input bus for story activities.
/// Any tap source can broadcast here: AR tap handler, desktop mouse handler, VR pointer, UI bridge, etc.
/// </summary>
public static class StoryActivityInputRouter
{
    public static event Action<ActivityInputData> OnInput;

    public static void Broadcast(ActivityInputData data)
    {
        OnInput?.Invoke(data);
    }

    public static void ClearAllListeners()
    {
        OnInput = null;
    }
}

public enum ActivityInputType
{
    ScreenTap,
    ModelTap,
    UIButton,
    HoldStart,
    HoldEnd,
    ContinuousTap
}

public struct ActivityInputData
{
    public ActivityInputType type;
    public GameObject hitObject;
    public GameObject[] hitObjects;
    public Vector2 screenPosition;
    public int optionIndex;

    public bool HasHit => hitObject != null || (hitObjects != null && hitObjects.Length > 0);
}
