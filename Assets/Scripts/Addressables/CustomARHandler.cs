using UnityEngine;
using UnityEngine.AddressableAssets;
using Vuforia;

public class CustomARHandler : MonoBehaviour
{
    public string addressableKey;
    private GameObject instantiatedObject;
    private IARContent contentControl;
    private ModelInteraction modelInteraction;
    private QuizManagerArun quizManager;  // directly from model

    private bool _isLoading = false;

    public GameObject replayButton;
    public GameObject nextPageImg;

    public static CustomARHandler Current;

    void Start()
    {
        // hide both buttons at start
        replayButton?.SetActive(false);
        nextPageImg?.SetActive(false);

        var observer = GetComponent<ObserverBehaviour>();
        if (observer != null)
            observer.OnTargetStatusChanged += OnTargetStatusChanged;
    }

    void OnDestroy()
    {
        var observer = GetComponent<ObserverBehaviour>();
        if (observer != null)
            observer.OnTargetStatusChanged -= OnTargetStatusChanged;
    }

    private void OnTargetStatusChanged(ObserverBehaviour behaviour, TargetStatus status)
    {
        if (status.Status == Status.TRACKED ||
            status.Status == Status.EXTENDED_TRACKED ||
            status.Status == Status.LIMITED)
        {
            OnTrackingFound();
        }
        else
        {
            OnTrackingLost();
        }
    }

    private void OnTrackingFound()
    {
        Current = this; // ADD THIS — register as active

        if (string.IsNullOrEmpty(addressableKey)) return;

        if (instantiatedObject == null && !_isLoading)
        {
            _isLoading = true;
            Addressables.InstantiateAsync(addressableKey, transform).Completed += handle =>
            {
                _isLoading = false;

                if (instantiatedObject != null)
                {
                    Addressables.ReleaseInstance(handle.Result);
                    return;
                }

                instantiatedObject = handle.Result;
                instantiatedObject.transform.localPosition = Vector3.zero;
                contentControl = instantiatedObject.GetComponent<IARContent>();
                modelInteraction = GetComponent<ModelInteraction>();
                modelInteraction?.Init(instantiatedObject);

                // get QuizManagerArun directly from model — null for non-quiz models

                var components = instantiatedObject.GetComponentsInChildren<QuizManagerArun>(true);
                if (components.Length > 0)
                {
                    quizManager = components[0]; // This is your script
                }
                quizManager?.PauseQuiz(false);

                contentControl?.PlayContent();

                // pass completion callback to content
                if (contentControl != null)
                    contentControl.SetCompletionCallback(OnContentCompleted);

                replayButton?.SetActive(true);
                nextPageImg?.SetActive(false); // hide until content ends
            };
        }
        else if (instantiatedObject != null)
        {
            ToggleRenderers(true);
            modelInteraction?.Init(instantiatedObject);
            quizManager?.PauseQuiz(false);
            contentControl?.PlayContent();
            replayButton?.SetActive(true);
        }
    }

    private void OnTrackingLost()
    {
        if (Current == this) Current = null; // ADD THIS — unregister

        if (instantiatedObject != null)
        {
            contentControl?.PauseContent();
            quizManager?.PauseQuiz(true);
            ToggleRenderers(false);

            // destroy instead of hide
            Addressables.ReleaseInstance(instantiatedObject);
            instantiatedObject = null;
            contentControl = null;
            modelInteraction = null;
            quizManager = null;

            // hide buttons when target lost
            replayButton?.SetActive(false);
            nextPageImg?.SetActive(false);
        }
    }

    // button calls this static method — works for any active target
    public static void ReplayCurrent()
    {
        Current?.OnReplayButtonPressed();
    }

    // ── Called by replay button OnClick in Inspector ──────────────────────────
    public void OnReplayButtonPressed()
    {
        if (contentControl == null) return;
        nextPageImg?.SetActive(false); // hide next page until content ends again
        contentControl.ReplayContent();
    }

    // ── Called internally when content finishes ───────────────────────────────
    private void OnContentCompleted()
    {
        nextPageImg?.SetActive(true); // show only when content ends
    }

    private void ToggleRenderers(bool visible)
    {
        if (instantiatedObject == null) return;

        var renderers = instantiatedObject.GetComponentsInChildren<Renderer>();
        foreach (var r in renderers) r.enabled = visible;
        var canvas = instantiatedObject.GetComponentsInChildren<Canvas>();
        foreach (var c in canvas) c.enabled = visible;
    }
}
