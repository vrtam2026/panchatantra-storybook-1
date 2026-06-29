using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// ─────────────────────────────────────────────────────────────────────────────
// MultiAnswer.cs  —  Attach to Quiz_Panel.
// QuizManager reference found automatically — no manual wiring needed.
// ─────────────────────────────────────────────────────────────────────────────

public class MultiAnswer : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Drag SubmitBtn GameObject here.")]
    [SerializeField] private GameObject submitButton;

    [Tooltip("Drag Quiz_Panel (the one with QuizFeedback) here.")]
    [SerializeField] private QuizFeedback quizFeedback;

    [Header("Colors")]
    public Color selectedColor = new Color(1f, 0.85f, 0f);
    public Color correctColor = Color.green;
    public Color wrongColor = Color.red;
    public Color defaultColor = Color.white;

    private QuizManager quizManager;
    private List<int> selectedIndices = new List<int>();
    private QuizQuestion currentQuestion;
    private GameObject[] optionButtons;

    private void Awake()
    {
        quizManager = GetComponentInParent<QuizManager>();
        Debug.Log($"[MultiAnswer] Awake — quizManager={quizManager != null}  submitButton={submitButton != null}  feedback={quizFeedback != null}");
    }

    // ── Called by QuizManager every question ──────────────────────────────

    public void SetupQuestion(QuizQuestion question, GameObject[] buttons)
    {
        currentQuestion = question;
        optionButtons = buttons;
        selectedIndices.Clear();

        if (buttons != null)
            foreach (GameObject btn in buttons)
            {
                if (btn == null) continue;
                Image img = btn.GetComponent<Image>();
                if (img != null) img.color = defaultColor;
            }

        bool isMulti = question != null && question.IsMultiAnswer;
        Debug.Log($"[MultiAnswer] SetupQuestion isMultiAnswer={isMulti}");
        if (submitButton != null) submitButton.SetActive(isMulti);
    }

    // ── Called by AnswerScript ────────────────────────────────────────────

    public void OnOptionTapped(int index, GameObject button)
    {
        Debug.Log($"[MultiAnswer] OnOptionTapped index={index}  currentQuestion null={currentQuestion == null}");
        if (currentQuestion == null || button == null) return;

        Image img = button.GetComponent<Image>();
        if (selectedIndices.Contains(index))
        {
            selectedIndices.Remove(index);
            if (img != null) img.color = defaultColor;
            Debug.Log($"[MultiAnswer] Deselected {index}. Now: {string.Join(",", selectedIndices)}");
        }
        else
        {
            selectedIndices.Add(index);
            if (img != null) img.color = selectedColor;
            Debug.Log($"[MultiAnswer] Selected {index}. Now: {string.Join(",", selectedIndices)}");
        }
    }

    // ── Called by timer expiry ────────────────────────────────────────────────

    public void AutoSubmit()
    {
        Debug.Log($"[MultiAnswer] AutoSubmit selectedCount={selectedIndices.Count}");
        if (currentQuestion == null) return;
        if (selectedIndices.Count == 0)
        {
            if (submitButton != null) submitButton.SetActive(false);
            if (quizManager != null) { quizManager.DisableAllButtons(); quizManager.OnAnswerSelected(false); }
            return;
        }
        Evaluate();
    }

    // ── Called by SubmitBtn OnClick() ─────────────────────────────────────────

    public void OnSubmit()
    {
        Debug.Log($"[MultiAnswer] OnSubmit called. currentQuestion null={currentQuestion == null}  selected={selectedIndices.Count}");
        if (currentQuestion == null || selectedIndices.Count == 0) return;
        Evaluate();
    }

    // ── Core evaluation ───────────────────────────────────────────────────────

    private void Evaluate()
    {
        List<int> correct = new List<int>();
        if (currentQuestion.option1IsCorrect) correct.Add(1);
        if (currentQuestion.option2IsCorrect) correct.Add(2);
        if (currentQuestion.option3IsCorrect) correct.Add(3);
        if (currentQuestion.option4IsCorrect) correct.Add(4);

        bool isCorrect = IsExactMatch(selectedIndices, correct);
        Debug.Log($"[MultiAnswer] Selected={string.Join(",", selectedIndices)}  Correct={string.Join(",", correct)}  Match={isCorrect}");

        // ── Color + animate every selected button individually ────────────────
        if (optionButtons != null)
        {
            for (int i = 0; i < optionButtons.Length; i++)
            {
                if (optionButtons[i] == null) continue;
                Image img = optionButtons[i].GetComponent<Image>();
                if (img == null) continue;
                int idx = i + 1;
                bool wasSelected = selectedIndices.Contains(idx);
                bool isCorrectBtn = correct.Contains(idx);

                if (wasSelected && isCorrectBtn)
                {
                    img.color = correctColor;
                    if (quizFeedback != null)
                        quizFeedback.PlayCorrect(optionButtons[i]); // animate this button
                }
                else if (wasSelected)
                {
                    img.color = wrongColor;
                    if (quizFeedback != null)
                        quizFeedback.PlayWrong(optionButtons[i]);   // animate this button
                }
                // not selected = leave white, no animation
            }
        }

        if (submitButton != null) submitButton.SetActive(false);
        if (quizManager != null) { quizManager.DisableAllButtons(); quizManager.OnAnswerSelected(isCorrect); }
    }

    private bool IsExactMatch(List<int> selected, List<int> correct)
    {
        if (selected.Count != correct.Count) return false;
        foreach (int i in correct)
            if (!selected.Contains(i)) return false;
        return true;
    }
}