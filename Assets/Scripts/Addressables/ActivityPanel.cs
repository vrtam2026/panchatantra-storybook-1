using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Reusable child-facing activity UI.
/// Assign every UI reference once. The activity system will show only the UI needed by the current activity.
/// </summary>
public class ActivityPanel : MonoBehaviour
{
    public static ActivityPanel Instance { get; private set; }

    [Header("Instruction UI, assign once")]
    [Tooltip("Panel that holds the child instruction text. The system shows it only when the current activity needs instructions.")]
    public GameObject instructionPanel;
    [Tooltip("Text component that displays the child instruction. Example: Tap to continue.")]
    public TMP_Text instructionText;

    [Header("Feedback UI, optional")]
    [Tooltip("Optional panel for short messages like Well done or Try again. Leave empty if you do not use feedback messages.")]
    public GameObject feedbackPanel;
    [Tooltip("Optional text used for success or retry messages.")]
    public TMP_Text feedbackText;

    [Header("Progress UI, optional")]
    [Tooltip("Optional parent panel for progress or timer UI. It is shown only for activities that need progress.")]
    public GameObject progressPanel;
    [Tooltip("Optional slider for timers, repeated taps, or ordered targets.")]
    public Slider progressSlider;
    [Tooltip("Optional text beside progress. Example: 3 / 5 or 8 seconds left.")]
    public TMP_Text progressText;

    [Header("Two Choice Buttons, optional")]
    [Tooltip("Optional first button for simple button or two-choice activities.")]
    public Button leftButton;
    [Tooltip("Text shown inside the first button.")]
    public TMP_Text leftButtonText;
    [Tooltip("Optional second button for simple button or two-choice activities.")]
    public Button rightButton;
    [Tooltip("Text shown inside the second button.")]
    public TMP_Text rightButtonText;

    [Header("Prebuilt Option Groups, optional")]
    [Tooltip("Parent object for the 2 option layout. Assign the group that contains option 1 and option 2 buttons. Used by Choose Correct Option when Option Count is 2.")]
    public GameObject twoOptionGroup;
    [Tooltip("Buttons inside the 2 option layout. If empty, the script will auto-find Button components under 2 Option Group in hierarchy order.")]
    public Button[] twoOptionButtons = new Button[2];

    [Tooltip("Parent object for the 3 option layout. Assign the group that contains option 1, option 2, and option 3 buttons. Used by Choose Correct Option when Option Count is 3.")]
    public GameObject threeOptionGroup;
    [Tooltip("Buttons inside the 3 option layout. If empty, the script will auto-find Button components under 3 Option Group in hierarchy order.")]
    public Button[] threeOptionButtons = new Button[3];

    [Tooltip("Parent object for the 4 option layout. Assign the group that contains option 1 to option 4 buttons. Used by Choose Correct Option when Option Count is 4.")]
    public GameObject fourOptionGroup;
    [Tooltip("Buttons inside the 4 option layout. If empty, the script will auto-find Button components under 4 Option Group in hierarchy order.")]
    public Button[] fourOptionButtons = new Button[4];

    [Tooltip("Parent object for the 5 option layout. Assign the group that contains option 1 to option 5 buttons. Used by Choose Correct Option when Option Count is 5.")]
    public GameObject fiveOptionGroup;
    [Tooltip("Buttons inside the 5 option layout. If empty, the script will auto-find Button components under 5 Option Group in hierarchy order.")]
    public Button[] fiveOptionButtons = new Button[5];

    [Header("Dynamic Buttons, fallback optional")]
    [Tooltip("Fallback only. Parent object where generated answer buttons will be placed if no prebuilt option group is assigned.")]
    public Transform dynamicButtonParent;
    [Tooltip("Fallback only. Button prefab used to create choice buttons automatically if no prebuilt option group is assigned.")]
    public Button dynamicButtonPrefab;

    [Header("Behaviour Settings")]
    [Tooltip("If ON, instruction text hides while feedback is shown. Usually OFF for simple child instructions.")]
    public bool hideInstructionWhenFeedbackShows = false;
    [Tooltip("If ON, all assigned panels/buttons/progress are hidden when this object enables. Leave ON for activity UI.")]
    public bool hideAllOnEnable = true;

    private readonly List<Button> _spawnedButtons = new List<Button>();
    private bool _progressAllowed;
    private bool _buttonsAllowed;

    protected virtual void Awake()
    {
        Instance = this;
        if (hideAllOnEnable)
            ResetPanel();
    }

    protected virtual void OnEnable()
    {
        if (hideAllOnEnable)
            ResetPanel();
    }

    /// <summary>
    /// Called by ContentController when an activity starts.
    /// It hides all UI first, then shows only the instruction and later allows progress/buttons only if needed.
    /// </summary>
    public virtual void BeginActivity(string instruction, bool allowProgress, bool allowButtons)
    {
        _progressAllowed = allowProgress;
        _buttonsAllowed = allowButtons;
        ResetPanel();
        ShowInstruction(instruction);
    }

    /// <summary>
    /// Called by ContentController when an activity ends, resets, or replays.
    /// </summary>
    public virtual void EndActivity()
    {
        _progressAllowed = false;
        _buttonsAllowed = false;
        ResetPanel();
    }

    public virtual void ResetPanel()
    {
        HideInstruction();
        HideFeedback();
        HideProgress();
        HideButtons();
    }

    public virtual void ShowInstruction(string text)
    {
        bool hasText = !string.IsNullOrWhiteSpace(text);

        SetPanelActive(instructionPanel, UISection.Instruction, hasText);

        if (instructionText != null)
        {
            instructionText.gameObject.SetActive(hasText);
            instructionText.text = text ?? string.Empty;
        }
    }

    public virtual void HideInstruction()
    {
        SetPanelActive(instructionPanel, UISection.Instruction, false);

        if (instructionText != null)
        {
            instructionText.text = string.Empty;
            instructionText.gameObject.SetActive(false);
        }
    }

    public virtual void ShowFeedback(string text)
    {
        bool hasText = !string.IsNullOrWhiteSpace(text);

        if (hideInstructionWhenFeedbackShows && hasText)
            HideInstruction();

        SetPanelActive(feedbackPanel, UISection.Feedback, hasText);

        if (feedbackText != null)
        {
            feedbackText.gameObject.SetActive(hasText);
            feedbackText.text = text ?? string.Empty;
        }
        else if (feedbackPanel == null && instructionText != null && hasText)
        {
            ShowInstruction(text);
        }
    }

    public virtual void HideFeedback()
    {
        SetPanelActive(feedbackPanel, UISection.Feedback, false);

        if (feedbackText != null)
        {
            feedbackText.text = string.Empty;
            feedbackText.gameObject.SetActive(false);
        }
    }

    public virtual void ShowProgress(float normalized, string text = null)
    {
        if (!_progressAllowed)
            return;

        SetPanelActive(progressPanel, UISection.Progress, true);

        if (progressSlider != null)
            progressSlider.gameObject.SetActive(true);

        if (progressText != null)
            progressText.gameObject.SetActive(true);

        SetProgress(normalized, text);
    }

    public virtual void SetProgress(float normalized, string text = null)
    {
        normalized = Mathf.Clamp01(normalized);

        if (progressSlider != null)
            progressSlider.value = normalized;

        if (progressText != null)
            progressText.text = text ?? Mathf.RoundToInt(normalized * 100f) + "%";
    }

    public virtual void HideProgress()
    {
        SetPanelActive(progressPanel, UISection.Progress, false);

        if (progressSlider != null)
        {
            progressSlider.value = 0f;
            progressSlider.gameObject.SetActive(false);
        }

        if (progressText != null)
        {
            progressText.text = string.Empty;
            progressText.gameObject.SetActive(false);
        }
    }

    /// <summary>
    /// Strong hide used by scenario-style activities. Hides every activity UI piece,
    /// including manually assigned option buttons and prebuilt option groups.
    /// </summary>
    public virtual void HideAllActivityUIForScenario()
    {
        HideInstruction();
        HideFeedback();
        HideProgress();
        HideButtons();
    }

    public virtual void ShowButtons(IList<string> labels, Action<int> onClicked)
    {
        HideButtons();

        if (!_buttonsAllowed)
            return;

        if (labels == null || labels.Count == 0)
            return;

        if (dynamicButtonParent != null && dynamicButtonPrefab != null)
        {
            dynamicButtonParent.gameObject.SetActive(true);

            for (int i = 0; i < labels.Count; i++)
            {
                int index = i;
                Button button = Instantiate(dynamicButtonPrefab, dynamicButtonParent);
                button.gameObject.SetActive(true);
                SetButtonLabel(button, labels[i]);
                button.onClick.RemoveAllListeners();
                button.onClick.AddListener(() => onClicked?.Invoke(index));
                _spawnedButtons.Add(button);
            }

            return;
        }

        if (labels.Count >= 1 && leftButton != null)
        {
            leftButton.gameObject.SetActive(true);
            SetButtonLabel(leftButton, labels[0]);
            SetButtonEnabled(leftButton, true, true);
            leftButton.onClick.RemoveAllListeners();
            leftButton.onClick.AddListener(() => onClicked?.Invoke(0));
        }

        if (labels.Count >= 2 && rightButton != null)
        {
            rightButton.gameObject.SetActive(true);
            SetButtonLabel(rightButton, labels[1]);
            SetButtonEnabled(rightButton, true, true);
            rightButton.onClick.RemoveAllListeners();
            rightButton.onClick.AddListener(() => onClicked?.Invoke(1));
        }
    }

    public virtual void ShowChoiceButtons(IList<string> labels, Action<int> onClicked, bool[] disabledOptions, bool grayDisabledOptions)
    {
        HideButtons();

        if (!_buttonsAllowed)
            return;

        if (labels == null || labels.Count == 0)
            return;

        if (TryShowPrebuiltChoiceButtons(labels, onClicked, disabledOptions, grayDisabledOptions))
            return;

        if (dynamicButtonParent != null && dynamicButtonPrefab != null)
        {
            dynamicButtonParent.gameObject.SetActive(true);

            for (int i = 0; i < labels.Count; i++)
            {
                int index = i;
                Button button = Instantiate(dynamicButtonPrefab, dynamicButtonParent);
                button.gameObject.SetActive(true);
                SetButtonLabel(button, labels[i]);
                SetButtonEnabled(button, !IsOptionDisabled(disabledOptions, i), grayDisabledOptions);
                button.onClick.RemoveAllListeners();
                button.onClick.AddListener(() => onClicked?.Invoke(index));
                _spawnedButtons.Add(button);
            }

            return;
        }

        if (labels.Count >= 1 && leftButton != null)
        {
            leftButton.gameObject.SetActive(true);
            SetButtonLabel(leftButton, labels[0]);
            SetButtonEnabled(leftButton, !IsOptionDisabled(disabledOptions, 0), grayDisabledOptions);
            leftButton.onClick.RemoveAllListeners();
            leftButton.onClick.AddListener(() => onClicked?.Invoke(0));
        }

        if (labels.Count >= 2 && rightButton != null)
        {
            rightButton.gameObject.SetActive(true);
            SetButtonLabel(rightButton, labels[1]);
            SetButtonEnabled(rightButton, !IsOptionDisabled(disabledOptions, 1), grayDisabledOptions);
            rightButton.onClick.RemoveAllListeners();
            rightButton.onClick.AddListener(() => onClicked?.Invoke(1));
        }
    }

    private bool TryShowPrebuiltChoiceButtons(IList<string> labels, Action<int> onClicked, bool[] disabledOptions, bool grayDisabledOptions)
    {
        int count = labels != null ? labels.Count : 0;
        if (count < 2 || count > 5)
            return false;

        GameObject group = GetOptionGroup(count);
        Button[] buttons = GetOptionButtons(count);

        if ((group == null) && (buttons == null || buttons.Length == 0))
            return false;

        HideOptionGroupsExcept(count);

        if (group != null)
            group.SetActive(true);

        if ((buttons == null || buttons.Length == 0) && group != null)
            buttons = CollectButtonsFromGroup(group);

        if (buttons == null || buttons.Length < count)
            return false;

        for (int i = 0; i < buttons.Length; i++)
        {
            Button button = buttons[i];
            if (button == null)
                continue;

            button.onClick.RemoveAllListeners();

            bool shouldShow = i < count;
            button.gameObject.SetActive(shouldShow);
            if (!shouldShow)
                continue;

            int index = i;
            SetButtonLabel(button, labels[i]);
            SetButtonEnabled(button, !IsOptionDisabled(disabledOptions, i), grayDisabledOptions);
            button.onClick.AddListener(() => onClicked?.Invoke(index));
        }

        return true;
    }

    public virtual void HideButtons()
    {
        HideOptionGroupsExcept(0);

        if (leftButton != null)
        {
            leftButton.onClick.RemoveAllListeners();
            SetButtonEnabled(leftButton, true, true);
            leftButton.gameObject.SetActive(false);
        }

        if (rightButton != null)
        {
            rightButton.onClick.RemoveAllListeners();
            SetButtonEnabled(rightButton, true, true);
            rightButton.gameObject.SetActive(false);
        }

        for (int i = 0; i < _spawnedButtons.Count; i++)
        {
            if (_spawnedButtons[i] != null)
                Destroy(_spawnedButtons[i].gameObject);
        }

        _spawnedButtons.Clear();

        if (dynamicButtonParent != null)
            dynamicButtonParent.gameObject.SetActive(false);
    }

    public bool HasChoiceButtonGroupForCount(int count)
    {
        if (count < 2 || count > 5)
            return false;

        GameObject group = GetOptionGroup(count);
        Button[] buttons = GetOptionButtons(count);

        if (buttons != null && buttons.Length >= count)
        {
            int valid = 0;
            for (int i = 0; i < buttons.Length; i++)
            {
                if (buttons[i] != null)
                    valid++;
            }
            if (valid >= count)
                return true;
        }

        if (group == null)
            return false;

        Button[] collected = CollectButtonsFromGroup(group);
        return collected != null && collected.Length >= count;
    }

    private GameObject GetOptionGroup(int count)
    {
        switch (count)
        {
            case 2: return twoOptionGroup;
            case 3: return threeOptionGroup;
            case 4: return fourOptionGroup;
            case 5: return fiveOptionGroup;
            default: return null;
        }
    }

    private Button[] GetOptionButtons(int count)
    {
        switch (count)
        {
            case 2: return twoOptionButtons;
            case 3: return threeOptionButtons;
            case 4: return fourOptionButtons;
            case 5: return fiveOptionButtons;
            default: return null;
        }
    }

    private Button[] CollectButtonsFromGroup(GameObject group)
    {
        if (group == null)
            return null;

        Button[] buttons = group.GetComponentsInChildren<Button>(true);
        Array.Sort(buttons, CompareButtonHierarchyOrder);
        return buttons;
    }

    private int CompareButtonHierarchyOrder(Button a, Button b)
    {
        if (a == null && b == null) return 0;
        if (a == null) return 1;
        if (b == null) return -1;
        return GetHierarchyPath(a.transform).CompareTo(GetHierarchyPath(b.transform));
    }

    private string GetHierarchyPath(Transform t)
    {
        if (t == null)
            return string.Empty;

        string path = t.GetSiblingIndex().ToString("D4");
        Transform current = t.parent;
        while (current != null && current != transform)
        {
            path = current.GetSiblingIndex().ToString("D4") + "/" + path;
            current = current.parent;
        }
        return path;
    }

    private void HideOptionGroupsExcept(int activeCount)
    {
        SetOptionGroupActive(twoOptionGroup, activeCount == 2);
        SetOptionGroupActive(threeOptionGroup, activeCount == 3);
        SetOptionGroupActive(fourOptionGroup, activeCount == 4);
        SetOptionGroupActive(fiveOptionGroup, activeCount == 5);

        SetButtonArrayActive(twoOptionButtons, activeCount == 2);
        SetButtonArrayActive(threeOptionButtons, activeCount == 3);
        SetButtonArrayActive(fourOptionButtons, activeCount == 4);
        SetButtonArrayActive(fiveOptionButtons, activeCount == 5);
    }

    private void SetOptionGroupActive(GameObject group, bool active)
    {
        if (group != null)
            group.SetActive(active);
    }

    private void SetButtonArrayActive(Button[] buttons, bool active)
    {
        if (buttons == null)
            return;

        for (int i = 0; i < buttons.Length; i++)
        {
            Button button = buttons[i];
            if (button == null) continue;

            button.onClick.RemoveAllListeners();
            SetButtonEnabled(button, true, true);
            button.gameObject.SetActive(active);
        }
    }

    private bool IsOptionDisabled(bool[] disabledOptions, int index)
    {
        return disabledOptions != null && index >= 0 && index < disabledOptions.Length && disabledOptions[index];
    }

    private void SetButtonEnabled(Button button, bool enabled, bool grayDisabledOptions)
    {
        if (button == null)
            return;

        button.interactable = enabled || !grayDisabledOptions;

        CanvasGroup group = button.GetComponent<CanvasGroup>();
        if (group == null)
            group = button.gameObject.AddComponent<CanvasGroup>();

        group.alpha = enabled ? 1f : (grayDisabledOptions ? 0.45f : 1f);
        group.blocksRaycasts = enabled || !grayDisabledOptions;
        group.interactable = enabled || !grayDisabledOptions;
    }

    private void SetButtonLabel(Button button, string label)
    {
        if (button == null) return;

        TMP_Text tmp = button.GetComponentInChildren<TMP_Text>(true);
        if (tmp != null)
        {
            tmp.text = label ?? string.Empty;
            return;
        }

        Text legacyText = button.GetComponentInChildren<Text>(true);
        if (legacyText != null)
            legacyText.text = label ?? string.Empty;
    }

    private void SetPanelActive(GameObject panel, UISection section, bool active)
    {
        if (panel == null)
            return;

        if (!active && IsSharedPanel(panel, section))
            return;

        panel.SetActive(active);
    }

    private bool IsSharedPanel(GameObject panel, UISection section)
    {
        if (panel == null)
            return false;

        // Never hide the object that owns this script as a sub-panel. This lets you assign all UI once safely.
        if (panel == gameObject)
            return true;

        if (section != UISection.Instruction && Contains(panel, instructionPanel)) return true;
        if (section != UISection.Instruction && Contains(panel, instructionText)) return true;

        if (section != UISection.Feedback && Contains(panel, feedbackPanel)) return true;
        if (section != UISection.Feedback && Contains(panel, feedbackText)) return true;

        if (section != UISection.Progress && Contains(panel, progressPanel)) return true;
        if (section != UISection.Progress && Contains(panel, progressSlider)) return true;
        if (section != UISection.Progress && Contains(panel, progressText)) return true;

        if (section != UISection.Buttons && Contains(panel, leftButton)) return true;
        if (section != UISection.Buttons && Contains(panel, leftButtonText)) return true;
        if (section != UISection.Buttons && Contains(panel, rightButton)) return true;
        if (section != UISection.Buttons && Contains(panel, rightButtonText)) return true;
        if (section != UISection.Buttons && Contains(panel, twoOptionGroup)) return true;
        if (section != UISection.Buttons && Contains(panel, threeOptionGroup)) return true;
        if (section != UISection.Buttons && Contains(panel, fourOptionGroup)) return true;
        if (section != UISection.Buttons && Contains(panel, fiveOptionGroup)) return true;
        if (section != UISection.Buttons && dynamicButtonParent != null && Contains(panel, dynamicButtonParent.gameObject)) return true;

        return false;
    }

    private bool Contains(GameObject parent, Component child)
    {
        if (child == null)
            return false;
        return Contains(parent, child.gameObject);
    }

    private bool Contains(GameObject parent, GameObject child)
    {
        if (parent == null || child == null)
            return false;

        if (parent == child)
            return true;

        return child.transform.IsChildOf(parent.transform);
    }

    private enum UISection
    {
        Instruction,
        Feedback,
        Progress,
        Buttons
    }
}
