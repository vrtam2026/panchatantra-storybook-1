using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// Attach this to the Download button for a language card.
/// On click: set language, then load the target AR scene.
/// </summary>
[DisallowMultipleComponent]
public class LanguageSelectAndOpenScene : MonoBehaviour
{
    [Header("Config")]
    public string languageName = "English";
    public string targetSceneName = "AR-main-scene";

    [Header("Optional")]
    [SerializeField] private Button button;

    private void Reset()
    {
        button = GetComponent<Button>();
    }

    private void Awake()
    {
        if (button == null) button = GetComponent<Button>();
        if (button != null)
        {
            button.onClick.RemoveListener(OnClick);
            button.onClick.AddListener(OnClick);
        }
        else
        {
            Debug.LogWarning($"{nameof(LanguageSelectAndOpenScene)}: No Button found on this GameObject.", this);
        }
    }

    private void OnDestroy()
    {
        if (button != null)
        {
            button.onClick.RemoveListener(OnClick);
        }
    }

    private void OnClick()
    {
        ARGlobalLanguage.SetLanguage(languageName);
        SceneManager.LoadScene(targetSceneName);
    }
}
