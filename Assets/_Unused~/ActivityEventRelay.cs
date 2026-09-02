using UnityEngine;

/// <summary>
/// Small bridge for Animator Events, Timeline Signals, buttons, or other scripts.
/// Put this on the page root or animated model and call these methods from animation events.
/// </summary>
public class ActivityEventRelay : MonoBehaviour
{
    [Tooltip("Drag the page ContentController here. Leave empty if this script is on the same page root or inside the page object.")]
    [SerializeField] private ContentController controller;

    [Tooltip("Optional. Drag the ARTrackedPageNode if this relay is used from a middle-of-story animation event that must pause and resume the story.")]
    [SerializeField] private ARTrackedPageNode pageNode;

    private void Awake()
    {
        if (controller == null)
            controller = GetComponentInParent<ContentController>();

        if (pageNode == null)
            pageNode = GetComponentInParent<ARTrackedPageNode>();
    }

    public void SetController(ContentController newController)
    {
        controller = newController;
    }

    public void NotifyRevealComplete()
    {
        controller?.NotifyRevealComplete();
    }

    public void NotifyVoiceOverStarted()
    {
        controller?.NotifyVoiceOverStarted();
    }

    public void NotifyStoryEnd()
    {
        controller?.PlayContent();
    }

    public void TriggerActivity(string key)
    {
        controller?.TriggerActivity(key);
    }

    public void TriggerAnimationEvent(string key)
    {
        if (pageNode != null)
            pageNode.TriggerStoryPointActivity(key);
        else
            controller?.TriggerAnimationEvent(key);
    }

    public void TriggerStoryPointActivity(string key)
    {
        if (pageNode != null)
            pageNode.TriggerStoryPointActivity(key);
        else
            controller?.TriggerAnimationEvent(key);
    }

    public void ContinueAfterActivity()
    {
        controller?.ContinueAfterActivity();
    }
}
