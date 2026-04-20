using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// ---------------------------------------------------------------
// OverlayManager -- set up ONCE in the scene.
// Handles all overlay panels for ALL pages automatically.
// No per-page setup needed.
//
// STATES (only one active at a time, no overlap possible):
//   None         = panel hidden, AR content visible
//   PageEnd      = video done, turn page character showing
//   LostTracking = marker lost, point camera character showing
// ---------------------------------------------------------------

public class OverlayManager : MonoBehaviour
{
    public static OverlayManager Instance { get; private set; }

    // ---------------------------------------------------------------
    // CHARACTER GROUP
    // ---------------------------------------------------------------
    [System.Serializable]
    public class CharacterGroup
    {
        [Tooltip("Drag 2-3 sprites for this character in order.")]
        public List<Sprite> frames = new();
    }

    // ---------------------------------------------------------------
    // ONE PANEL -- shared by all states
    // ---------------------------------------------------------------
    [Header("Overlay Panel (assign ONCE)")]

    [Tooltip("Drag OverlayPanel CanvasGroup from UI_Canvas here.")]
    [SerializeField] private CanvasGroup overlayPanel;

    [Tooltip("Drag OverlayImage (UI Image inside OverlayPanel) here.")]
    [SerializeField] private Image overlayImage;

    // ---------------------------------------------------------------
    // CHARACTERS PER STATE
    // ---------------------------------------------------------------
    [Header("Page End Characters")]
    [Tooltip("Characters shown when video content finishes. Add one per character, 2-3 frames each.")]
    [SerializeField] private List<CharacterGroup> pageEndCharacters = new();

    [Header("Lost Tracking Characters")]
    [Tooltip("Characters shown when image marker is lost. Add one per character, 2-3 frames each.")]
    [SerializeField] private List<CharacterGroup> lostTrackingCharacters = new();

    // ---------------------------------------------------------------
    // ANIMATION SETTINGS (global, applies to all states)
    // ---------------------------------------------------------------
    [Header("Animation")]
    [Tooltip("ON = turn page character keeps looping. OFF = plays once and stops.")]
    [SerializeField] private bool loopPageEnd = true;

    [Tooltip("ON = lost tracking character keeps looping. OFF = plays once and stops.")]
    [SerializeField] private bool loopLostTracking = true;

    [Tooltip("ON = shows one random frame only, no cycling. OFF = plays all frames in sequence.")]
    [SerializeField] private bool randomFrame = false;

    [Tooltip("Seconds each frame stays before switching to next.")]
    [Min(0f)]
    [SerializeField] private float frameSpeed = 1.5f;

    [Tooltip("How long the overlay panel fades in when PageEnd activates (seconds).")]
    [Min(0f)]
    [SerializeField] private float panelFadeInDuration = 1f;

    [Tooltip("Seconds to wait after audio ends before showing turn page. Gives time for last animation frame to settle.")]
    [Min(0f)]
    [SerializeField] private float pageEndDelay = 2f;

    [Tooltip("Seconds to wait before showing Lost Tracking overlay. Prevents flicker when quickly turning pages.")]
    [Min(0f)]
    [SerializeField] private float lostTrackingDelay = 1f;

    // ---------------------------------------------------------------
    // PUBLIC ACCESSORS -- used by ARTrackedPageNode
    // ---------------------------------------------------------------
    public CanvasGroup OverlayPanel => overlayPanel;
    public float PanelFadeInDuration => panelFadeInDuration;

    // ---------------------------------------------------------------
    // STATE MACHINE
    // ---------------------------------------------------------------
    private enum OverlayState { None, PageEnd, LostTracking }
    private OverlayState _currentState = OverlayState.None;
    private OverlayState _stateBeforeLost = OverlayState.None;

    private Coroutine _frameCoroutine;
    private Coroutine _fadeCoroutine;
    private Coroutine _watchCoroutine;
    private Coroutine _lostTrackingCoroutine;

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;

        // Panel starts fully hidden
        if (overlayPanel != null)
        {
            overlayPanel.alpha = 0f;
            overlayPanel.blocksRaycasts = false;
            overlayPanel.gameObject.SetActive(false);

            // Push overlay to first sibling -- renders behind all other UI elements
            // UI buttons (back, slider, reset) will naturally render on top
            overlayPanel.transform.SetAsFirstSibling();
        }
    }

    private void OnEnable()
    {
        // Listen for voice/audio completion on any page
        ARMediaManager.OnVoiceCompleted += OnAudioCompleted;
    }

    private void OnDisable()
    {
        ARMediaManager.OnVoiceCompleted -= OnAudioCompleted;
    }

    private void OnAudioCompleted(string pageId)
    {
        // Only react if this matches the CURRENTLY active page
        if (ARMediaManager.ActivePageId != pageId)
        {
            Debug.Log($"[AR] OverlayManager: ignoring audio completion for '{pageId}' -- active page is '{ARMediaManager.ActivePageId}'");
            return;
        }

        // Don't start countdown if loading screen is active
        if (LoadingScreen.Instance != null && LoadingScreen.Instance.IsShowing)
        {
            Debug.Log("[AR] OverlayManager: loading screen active -- skipping PageEnd.");
            return;
        }

        if (_watchCoroutine != null) { StopCoroutine(_watchCoroutine); _watchCoroutine = null; }
        _watchCoroutine = StartCoroutine(DelayThenShowPageEnd(pageId));
        Debug.Log($"[AR] OverlayManager: audio done for '{pageId}'. Waiting {pageEndDelay}s before turn page.");
    }

    private IEnumerator DelayThenShowPageEnd(string pageId)
    {
        yield return new WaitForSeconds(pageEndDelay);
        _watchCoroutine = null;

        // Re-verify page is still active and loading screen is not showing
        if (ARMediaManager.ActivePageId != pageId)
        {
            Debug.Log($"[AR] OverlayManager: page changed during delay -- cancelling PageEnd.");
            yield break;
        }

        if (LoadingScreen.Instance != null && LoadingScreen.Instance.IsShowing)
        {
            Debug.Log("[AR] OverlayManager: loading screen appeared during delay -- cancelling PageEnd.");
            yield break;
        }

        // Don't show turn page while lost tracking overlay is visible
        if (_currentState == OverlayState.LostTracking)
        {
            _stateBeforeLost = OverlayState.PageEnd;
            Debug.Log("[AR] OverlayManager: currently lost tracking -- PageEnd will show on restore.");
            yield break;
        }

        Debug.Log("[AR] OverlayManager: showing turn page.");
        var mediaManager = Object.FindFirstObjectByType<ARMediaManager>();
        if (mediaManager != null) mediaManager.StartPostVoiceBgm();

        ShowPageEnd();
    }

    // ---------------------------------------------------------------
    // PUBLIC CONTROLS
    // Called by ARTrackedPageNode and ARMediaManager
    // ---------------------------------------------------------------

    // Called by ARTrackedPageNode when video finishes
    public void ShowPageEnd()
    {
        if (_currentState == OverlayState.LostTracking)
        {
            // If tracking is lost, remember PageEnd for when tracking returns
            _stateBeforeLost = OverlayState.PageEnd;
            return;
        }

        SetState(OverlayState.PageEnd);
    }

    // Called by ARMediaManager when marker is lost
    public void ShowLostTracking()
    {
        // Stop any pending page end countdown -- avoid double trigger on resume
        if (_watchCoroutine != null) { StopCoroutine(_watchCoroutine); _watchCoroutine = null; }

        // Remember what was showing before so we can restore it
        _stateBeforeLost = _currentState;

        // Cancel any previous pending lost tracking show
        if (_lostTrackingCoroutine != null) { StopCoroutine(_lostTrackingCoroutine); _lostTrackingCoroutine = null; }

        // Delay before showing -- prevents flicker when quickly turning pages
        if (lostTrackingDelay > 0f)
            _lostTrackingCoroutine = StartCoroutine(DelayThenShowLostTracking());
        else
            SetState(OverlayState.LostTracking);
    }

    private IEnumerator DelayThenShowLostTracking()
    {
        yield return new WaitForSeconds(lostTrackingDelay);
        _lostTrackingCoroutine = null;
        SetState(OverlayState.LostTracking);
    }

    // Called by ARMediaManager when marker is found again
    public void HideLostTracking()
    {
        // Stop pending countdown
        if (_watchCoroutine != null) { StopCoroutine(_watchCoroutine); _watchCoroutine = null; }

        // Cancel lost tracking show if it hasn't fired yet -- tracking restored in time
        if (_lostTrackingCoroutine != null) { StopCoroutine(_lostTrackingCoroutine); _lostTrackingCoroutine = null; }

        // Only restore if we are still in LostTracking state
        // If HideAll was already called (page changed), stay at None
        if (_currentState == OverlayState.LostTracking)
            SetState(_stateBeforeLost);

        _stateBeforeLost = OverlayState.None;
    }

    // Stop watcher -- called on page reset
    public void StopWatching()
    {
        if (_watchCoroutine != null) { StopCoroutine(_watchCoroutine); _watchCoroutine = null; }
    }



    // Called by ARTrackedPageNode on page reset (new scan or replay)
    public void HideAll()
    {
        if (_watchCoroutine != null) { StopCoroutine(_watchCoroutine); _watchCoroutine = null; }
        if (_lostTrackingCoroutine != null) { StopCoroutine(_lostTrackingCoroutine); _lostTrackingCoroutine = null; }
        _stateBeforeLost = OverlayState.None;
        SetState(OverlayState.None);
    }

    // Called when content is completed and tracking is lost -- hides turn page overlay
    // User is intentionally moving to next page, don't keep turn page on screen
    public void HidePageEnd()
    {
        if (_currentState != OverlayState.PageEnd) return;
        if (_watchCoroutine != null) { StopCoroutine(_watchCoroutine); _watchCoroutine = null; }
        SetState(OverlayState.None);
    }

    // ---------------------------------------------------------------
    // STATE MACHINE CORE
    // ---------------------------------------------------------------

    private void SetState(OverlayState newState)
    {
        _currentState = newState;

        // Stop any running frame cycle
        if (_frameCoroutine != null) { StopCoroutine(_frameCoroutine); _frameCoroutine = null; }
        if (_fadeCoroutine != null) { StopCoroutine(_fadeCoroutine); _fadeCoroutine = null; }

        switch (newState)
        {
            case OverlayState.None:
                HidePanelImmediate();
                break;

            case OverlayState.PageEnd:
                ShowPanelWithCharacters(pageEndCharacters, fade: true, loop: loopPageEnd);
                break;

            case OverlayState.LostTracking:
                ShowPanelWithCharacters(lostTrackingCharacters, fade: false, loop: loopLostTracking);
                break;
        }
    }

    private void HidePanelImmediate()
    {
        if (overlayPanel == null) return;
        overlayPanel.alpha = 0f;
        overlayPanel.blocksRaycasts = false;  // ensure taps pass through when hidden
        overlayPanel.gameObject.SetActive(false);
    }

    private void ShowPanelWithCharacters(List<CharacterGroup> characters, bool fade, bool loop = true)
    {
        if (overlayPanel == null) return;

        overlayPanel.gameObject.SetActive(true);
        overlayPanel.blocksRaycasts = false;  // overlay is display only -- never intercept taps

        // Start frame cycling immediately
        if (overlayImage != null && characters != null && characters.Count > 0)
            _frameCoroutine = StartCoroutine(CycleFrames(characters, loop));

        // Fade in or appear instantly
        if (fade)
            _fadeCoroutine = StartCoroutine(FadePanel(0f, 1f, panelFadeInDuration));
        else
            overlayPanel.alpha = 1f;
    }

    // ---------------------------------------------------------------
    // FRAME CYCLING
    // ---------------------------------------------------------------

    private IEnumerator CycleFrames(List<CharacterGroup> characters, bool loop = true)
    {
        if (overlayImage == null) yield break;
        if (characters == null || characters.Count == 0) yield break;

        int groupIndex = Random.Range(0, characters.Count);
        CharacterGroup group = characters[groupIndex];
        if (group == null || group.frames == null || group.frames.Count == 0) yield break;

        // Random single frame mode
        if (randomFrame)
        {
            int randFrame = Random.Range(0, group.frames.Count);
            if (group.frames[randFrame] != null)
                overlayImage.sprite = group.frames[randFrame];
            yield break;
        }

        // Sequence mode -- loop param controls whether it repeats
        int frameIndex = 0;
        while (true)
        {
            if (group.frames[frameIndex] != null)
                overlayImage.sprite = group.frames[frameIndex];

            yield return new WaitForSeconds(frameSpeed);
            frameIndex++;

            if (frameIndex >= group.frames.Count)
            {
                if (!loop) yield break; // stop after one cycle
                frameIndex = 0;        // restart cycle
            }
        }
    }

    // ---------------------------------------------------------------
    // PANEL FADE
    // ---------------------------------------------------------------

    private IEnumerator FadePanel(float from, float to, float duration)
    {
        if (overlayPanel == null) yield break;

        overlayPanel.alpha = from;
        float elapsed = 0f;

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            overlayPanel.alpha = Mathf.Lerp(from, to, elapsed / duration);
            yield return null;
        }

        overlayPanel.alpha = to;
    }
}