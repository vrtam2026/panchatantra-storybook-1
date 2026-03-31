using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

#if UNITY_EDITOR
using UnityEditor;
#endif

[RequireComponent(typeof(Button))]
public class OpenSceneButton : MonoBehaviour
{
    [Header("Pick ONE")]
    [SerializeField] private string sceneName;

#if UNITY_EDITOR
    [SerializeField] private SceneAsset sceneAsset; // drag & drop scene here (Editor only)
#endif

    private Button _btn;

    private void Awake()
    {
        _btn = GetComponent<Button>();
        _btn.onClick.RemoveListener(LoadScene); // avoid duplicate listeners
        _btn.onClick.AddListener(LoadScene);
    }

    public void LoadScene()
    {
        string target = sceneName;

#if UNITY_EDITOR
        // If sceneAsset is assigned, use its name automatically
        if (sceneAsset != null)
            target = sceneAsset.name;
#endif

        if (string.IsNullOrEmpty(target))
        {
            Debug.LogError($"[{nameof(OpenSceneButton)}] No scene set on {gameObject.name}");
            return;
        }

        SceneManager.LoadScene(target);
    }
}
