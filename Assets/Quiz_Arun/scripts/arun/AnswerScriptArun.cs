using UnityEngine;
using UnityEngine.UI;

// ─────────────────────────────────────────────────────────────────────────────
// AnswerScriptArun.cs  —  Attach to each Option Button.
// Wire OnClick() → AnswerScriptArun.Answer() on each button.
// References found automatically — no manual wiring needed after script replace.
// ─────────────────────────────────────────────────────────────────────────────

public class AnswerScriptArun : MonoBehaviour
{
    [Header("Option Index")]
    [Tooltip("Set this to 1, 2, 3, or 4 to match which button this is.")]
    [SerializeField] private int optionIndex = 1;

    [HideInInspector] public bool isCorrect;

    private Button button;
    private Image buttonImage;
    private QuizManagerArun quizManagerArun;
    private MultiAnswerArun multiAnswerArun;
    private QuizFeedbackArun quizFeedbackArun;

    private void Awake()
    {
        button = GetComponent<Button>();
        buttonImage = GetComponent<Image>();
        quizManagerArun = GetComponentInParent<QuizManagerArun>();
        multiAnswerArun = GetComponentInParent<MultiAnswerArun>();
        quizFeedbackArun = GetComponentInParent<QuizFeedbackArun>();

        Debug.Log($"[AnswerScript] {name} Awake — manager={quizManagerArun != null} multi={multiAnswerArun != null}");
    }

    public void Answer()
    {
        Debug.Log($"[Answer] Option{optionIndex} tapped. interactable={button?.interactable} IsMultiAnswer={quizManagerArun?.IsMultiAnswer}");
        if (button == null || !button.interactable) return;
        if (quizManagerArun == null) return;

        if (quizManagerArun.IsMultiAnswer)
        {
            if (multiAnswerArun != null)
                multiAnswerArun.OnOptionTapped(optionIndex, gameObject);
            if (UnityEngine.EventSystems.EventSystem.current != null)
                UnityEngine.EventSystems.EventSystem.current.SetSelectedGameObject(null);
        }
        else
        {
            if (buttonImage != null)
                buttonImage.color = isCorrect ? Color.green : Color.red;
            if (quizFeedbackArun != null)
            {
                if (isCorrect) quizFeedbackArun.PlayCorrect(gameObject);
                else quizFeedbackArun.PlayWrong(gameObject);
            }
            quizManagerArun.DisableAllButtons();
            quizManagerArun.OnAnswerSelected(isCorrect);
        }
    }

    public void SetCorrect(bool value) { isCorrect = value; }
}