using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
// ExitQuizButton.cs
// Attach to: the Exit Button inside the quiz Addressable prefab.
// Wire Button.onClick -> ExitQuizButton.OnClick() in the prefab Inspector.
//
// The button exits through QuizManager, because the quiz is loaded by
// CustomARHandler in this project.
// ─────────────────────────────────────────────────────────────────────────────

public class ExitQuizButton : MonoBehaviour
{
    public void OnClick()
    {
        QuizManager manager = GetComponentInParent<QuizManager>(true);

        if (manager == null)
        {
            Debug.LogWarning("[ExitQuizButton] No QuizManager found in parent. Cannot exit quiz.");
            return;
        }

        manager.ExitQuiz();
    }
}
