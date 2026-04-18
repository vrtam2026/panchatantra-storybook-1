using UnityEngine;
using UnityEngine.UI;

// ─────────────────────────────────────────────────────────────────────────────
// AnswerScript.cs  —  Attach to each Option Button.
// Wire OnClick() → AnswerScript.Answer() on each button.
// References found automatically — no manual wiring needed after script replace.
// ─────────────────────────────────────────────────────────────────────────────

public class AnswerScript : MonoBehaviour
{
    [Header("Option Index")]
    [Tooltip("Set this to 1, 2, 3, or 4 to match which button this is.")]
    [SerializeField] private int optionIndex = 1;

    [HideInInspector] public bool isCorrect;

    private Button button;
    private Image buttonImage;
    private QuizManager quizManager;
    private MultiAnswer multiAnswer;
    private QuizFeedback quizFeedback;

    private void Awake()
    {
        button = GetComponent<Button>();
        buttonImage = GetComponent<Image>();
        quizManager = GetComponentInParent<QuizManager>();
        multiAnswer = GetComponentInParent<MultiAnswer>();
        quizFeedback = GetComponentInParent<QuizFeedback>();

        Debug.Log($"[AnswerScript] {name} Awake — manager={quizManager != null} multi={multiAnswer != null}");
    }

    public void Answer()
    {
        Debug.Log($"[Answer] Option{optionIndex} tapped. interactable={button?.interactable} IsMultiAnswer={quizManager?.IsMultiAnswer}");
        if (button == null || !button.interactable) return;
        if (quizManager == null) return;

        if (quizManager.IsMultiAnswer)
        {
            if (multiAnswer != null)
                multiAnswer.OnOptionTapped(optionIndex, gameObject);
            if (UnityEngine.EventSystems.EventSystem.current != null)
                UnityEngine.EventSystems.EventSystem.current.SetSelectedGameObject(null);
        }
        else
        {
            if (buttonImage != null)
                buttonImage.color = isCorrect ? Color.green : Color.red;
            if (quizFeedback != null)
            {
                if (isCorrect) quizFeedback.PlayCorrect(gameObject);
                else quizFeedback.PlayWrong(gameObject);
            }
            quizManager.DisableAllButtons();
            quizManager.OnAnswerSelected(isCorrect);
        }
    }

    public void SetCorrect(bool value) { isCorrect = value; }
}