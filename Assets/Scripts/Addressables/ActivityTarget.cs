using UnityEngine;

/// <summary>
/// Optional marker for tappable activity objects.
/// Use this to make reusable steps independent from object names/tags.
/// </summary>
public class ActivityTarget : MonoBehaviour
{
    [Header("Identity")]
    [Tooltip("Optional readable ID, e.g. Bull, Drum, Grass_01. Use when the same prefab appears in many scenes.")]
    public string targetId;

    [Header("Optional Visual Hint")]
    [Tooltip("Optional highlight ring, glow, arrow, outline, or VFX shown while this target is active.")]
    public GameObject highlightObject;

    private void Awake()
    {
        SetHighlight(false);
    }

    public void SetHighlight(bool visible)
    {
        if (highlightObject != null)
            highlightObject.SetActive(visible);
    }

    public static ActivityTarget From(GameObject obj)
    {
        if (obj == null) return null;
        return obj.GetComponentInParent<ActivityTarget>();
    }

    public bool Matches(GameObject obj)
    {
        if (obj == null) return false;
        return obj == gameObject || obj.transform.IsChildOf(transform);
    }

    public bool MatchesId(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        return string.Equals(targetId, id, System.StringComparison.OrdinalIgnoreCase);
    }
}
