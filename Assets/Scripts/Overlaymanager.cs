using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// OverlayManager -- set up ONCE in the scene.
// Handles PageEndPanel and LostTrackingPanel for ALL pages automatically.
// No per-page setup needed. Just assign this once and it works for all 100+ pages.

public class OverlayManager : MonoBehaviour
{
    public static OverlayManager Instance { get; private set; }

    // ---------------------------------------------------------------
    // CHARACTER GROUP -- shared by both panels
    // ---------------------------------------------------------------
    [System.Serializable]
    public class CharacterGroup
    {
        [Tooltip("Drag 2-3 sprites for this character in order.")]
        public List<Sprite> frames = new();
    }

    // ---------------------------------------------------------------
    // PAGE END PANEL
    // Shown when video content finishes on any page.
    // ---------------------------------------------------------------
    [Header("Page End Panel")]

    [Tooltip("Drag PageEndPanel CanvasGroup from UI_Canvas here.")]
    [SerializeField] private CanvasGroup pageEndPanel;

    [Tooltip("Drag PageEndImage (UI Image inside PageEndPanel) here.")]
    [SerializeField] private Image pageEndImage;

    [Tooltip("Add one character per slot. Each has 2-3 frames in order.")]
    [SerializeField] private List<CharacterGroup> pageEndCharacters = new();

    [Tooltip("How long PageEndPanel fades in (seconds).")]
    [Min(0f)]
    [SerializeField] private float pageEndFadeDuration = 1f;

    // ---------------------------------------------------------------
    // LOST TRACKING PANEL
    // Shown instantly when image marker is lost.
    // ---------------------------------------------------------------
    [Header("Lost Tracking Panel")]

    [Tooltip("Drag LostTrackingPanel CanvasGroup from UI_Canvas here.")]
    [SerializeField] private CanvasGroup lostTrackingPanel;

    [Tooltip("Drag LostTrackingImage (UI Image inside LostTrackingPanel) here.")]
    [SerializeField] private Image lostTrackingImage;

    [Tooltip("Add one character per slot. Each has 2-3 frames in order.")]
    [SerializeField] private List<CharacterGroup> lostTrackingCharacters = new();

    // ---------------------------------------------------------------
    // SHARED ANIMATION SETTINGS
    // ---------------------------------------------------------------
    [Header("Animation")]

    [Tooltip("ON = frames keep looping. OFF = plays once and stops.")]
    [SerializeField] private bool loopAnimation = true;

    [Tooltip("ON = shows one random frame only. OFF = plays all frames in sequence.")]
    [SerializeField] private bool randomFrame = false;

    [Tooltip("Seconds each frame stays before switching to next.")]
    [Min(0f)]
    [SerializeField] private float frameSpeed = 1.5f;

    // ---------------------------------------------------------------
    // PUBLIC ACCESSORS -- used by ARTrackedPageNode
    // ---------------------------------------------------------------
    public CanvasGroup PageEndPanel => pageEndPanel;
    public Image PageEndImage => pageEndImage;
    public float ImageFadeDuration => pageEndFadeDuration;

    // ---------------------------------------------------------------
    // PRIVATE STATE
    // ---------------------------------------------------------------
    private Coroutine _pageEndFrameCoroutine;
    private Coroutine _lostTrackingFrameCoroutine;

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;

        // Make sure both panels start hidden
        if (pageEndPanel != null)
        {
            pageEndPanel.alpha = 0f;
            pageEndPanel.gameObject.SetActive(false);
        }

        if (lostTrackingPanel != null)
        {
            lostTrackingPanel.alpha = 0f;
            lostTrackingPanel.gameObject.SetActive(false);
        }
    }

    // ---------------------------------------------------------------
    // PAGE END PANEL CONTROLS
    // Called by ARTrackedPageNode when video ends
    // ---------------------------------------------------------------

    public void StartPageEndCycle()
    {
        if (_pageEndFrameCoroutine != null)
        {
            StopCoroutine(_pageEndFrameCoroutine);
            _pageEndFrameCoroutine = null;
        }

        if (pageEndImage != null && pageEndCharacters != null && pageEndCharacters.Count > 0)
            _pageEndFrameCoroutine = StartCoroutine(CycleFrames(pageEndImage, pageEndCharacters));
    }

    public void HidePageEndPanel()
    {
        if (_pageEndFrameCoroutine != null)
        {
            StopCoroutine(_pageEndFrameCoroutine);
            _pageEndFrameCoroutine = null;
        }

        if (pageEndPanel != null)
        {
            pageEndPanel.alpha = 0f;
            pageEndPanel.gameObject.SetActive(false);
        }
    }

    // ---------------------------------------------------------------
    // LOST TRACKING PANEL CONTROLS
    // Called by ARMediaManager on tracking lost/found
    // ---------------------------------------------------------------

    public void ShowLostTrackingPanel()
    {
        if (lostTrackingPanel == null) return;

        if (_lostTrackingFrameCoroutine != null)
        {
            StopCoroutine(_lostTrackingFrameCoroutine);
            _lostTrackingFrameCoroutine = null;
        }

        lostTrackingPanel.gameObject.SetActive(true);
        lostTrackingPanel.alpha = 1f;

        if (lostTrackingImage != null && lostTrackingCharacters != null && lostTrackingCharacters.Count > 0)
            _lostTrackingFrameCoroutine = StartCoroutine(CycleFrames(lostTrackingImage, lostTrackingCharacters));
    }

    public void HideLostTrackingPanel()
    {
        if (_lostTrackingFrameCoroutine != null)
        {
            StopCoroutine(_lostTrackingFrameCoroutine);
            _lostTrackingFrameCoroutine = null;
        }

        if (lostTrackingPanel != null)
        {
            lostTrackingPanel.alpha = 0f;
            lostTrackingPanel.gameObject.SetActive(false);
        }
    }

    // ---------------------------------------------------------------
    // SHARED FRAME CYCLING
    // Used by both panels -- picks random character, cycles their frames
    // ---------------------------------------------------------------

    private IEnumerator CycleFrames(Image targetImage, List<CharacterGroup> characters)
    {
        if (targetImage == null) yield break;
        if (characters == null || characters.Count == 0) yield break;

        int groupIndex = Random.Range(0, characters.Count);
        CharacterGroup group = characters[groupIndex];
        if (group == null || group.frames == null || group.frames.Count == 0) yield break;

        // Random single frame mode
        if (randomFrame)
        {
            int randFrame = Random.Range(0, group.frames.Count);
            if (group.frames[randFrame] != null)
                targetImage.sprite = group.frames[randFrame];
            yield break;
        }

        // Sequence mode
        int frameIndex = 0;
        while (true)
        {
            if (group.frames[frameIndex] != null)
                targetImage.sprite = group.frames[frameIndex];

            yield return new WaitForSeconds(frameSpeed);
            frameIndex++;

            if (frameIndex >= group.frames.Count)
            {
                if (!loopAnimation) yield break;
                frameIndex = 0;
            }
        }
    }
}