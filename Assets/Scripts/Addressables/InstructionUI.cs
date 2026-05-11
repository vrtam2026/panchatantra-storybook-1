using UnityEngine;
using TMPro;
public class InstructionUI : MonoBehaviour
{
    public static InstructionUI Instance;

    [SerializeField] GameObject panel;
    [SerializeField] TMP_Text messageText;

    void Awake()
    {
        Instance = this;
        panel.SetActive(false);
    }

    public void Show(string message)
    {
        messageText.text = message;
        panel.SetActive(true);
    }

    public void Hide()
    {
        panel.SetActive(false);
    }
}
