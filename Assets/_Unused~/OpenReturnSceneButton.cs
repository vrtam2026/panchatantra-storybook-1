using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public class OpenReturnSceneButton : MonoBehaviour
{
    public Button button;

    public string returnKey = "ReturnScene";
    public string fallbackSceneName = "AR-story_selection";

    private void Awake()
    {
        if (button == null) button = GetComponent<Button>();

        if (button != null)
        {
            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(Back);
        }
    }

    private void Back()
    {
        string sceneName = PlayerPrefs.GetString(returnKey, fallbackSceneName);
        SceneManager.LoadScene(sceneName);
    }
}
