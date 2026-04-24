using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
// ExitQuizButton.cs
// Attach to: the Exit Button inside the quiz Addressable prefab.
// Wire Button.onClick -> ExitQuizButton.OnClick() in the prefab Inspector.
// ─────────────────────────────────────────────────────────────────────────────

public class ExitQuizButton : MonoBehaviour
{
    // Called by the Exit button's onClick event
    public void OnClick()
    {
        if (CustomARHandler.Current == null)
        {
            Debug.LogWarning("[ExitQuizButton] CustomARHandler.Current is null.");
            return;
        }
        CustomARHandler.Current.ExitQuiz();
    }
}