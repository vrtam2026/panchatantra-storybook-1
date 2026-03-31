using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public class SaveReturnThenOpenScene : MonoBehaviour
{
    public Button button;

    [Header("Open this scene")]
    public string targetSceneName = "AR-language_selection";

    [Header("Return key")]
    public string returnKey = "ReturnScene";

    private void Awake()
    {
        if (button == null) button = GetComponent<Button>();

        if (button != null)
        {
            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(Go);
        }
    }

    private void Go()
    {
        // save current scene as return
        string current = SceneManager.GetActiveScene().name;
        PlayerPrefs.SetString(returnKey, current);
        PlayerPrefs.Save();

        SceneManager.LoadScene(targetSceneName);
    }
}
