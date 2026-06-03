#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Editor-only helper for a Vuforia editor preview error:
/// "InvalidCastException: Vuforia.EditorClasses.TargetPreviewEditor.OnEnable".
/// This does not change runtime AR tracking. It only removes the optional Vuforia target preview
/// helper component that can break the Inspector in some Vuforia/Unity versions.
/// </summary>
public static class VuforiaTargetPreviewCleaner
{
    [MenuItem("Tools/AR Fix/Remove Vuforia Target Preview Components")]
    public static void RemoveTargetPreviewComponents()
    {
        int removed = 0;

        for (int sceneIndex = 0; sceneIndex < SceneManager.sceneCount; sceneIndex++)
        {
            Scene scene = SceneManager.GetSceneAt(sceneIndex);
            if (!scene.isLoaded) continue;

            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                removed += RemoveFromChildren(roots[i]);
            }
        }

        if (removed > 0)
        {
            EditorSceneManagerMarkAllDirtySafe();
            Debug.Log("[AR Fix] Removed " + removed + " Vuforia target preview component(s). Save the scene.");
        }
        else
        {
            Debug.Log("[AR Fix] No Vuforia target preview component was found. If the error remains, disable Enable Visualization on the Image Target Preview component manually.");
        }
    }

    private static int RemoveFromChildren(GameObject root)
    {
        if (root == null) return 0;

        int removed = 0;
        Component[] components = root.GetComponentsInChildren<Component>(true);

        for (int i = components.Length - 1; i >= 0; i--)
        {
            Component component = components[i];
            if (component == null) continue;

            Type type = component.GetType();
            string typeName = type.Name ?? string.Empty;
            string fullName = type.FullName ?? string.Empty;

            bool looksLikeVuforiaPreview =
                typeName.IndexOf("TargetPreview", StringComparison.OrdinalIgnoreCase) >= 0 ||
                fullName.IndexOf("TargetPreview", StringComparison.OrdinalIgnoreCase) >= 0 ||
                typeName.IndexOf("ImageTargetPreview", StringComparison.OrdinalIgnoreCase) >= 0 ||
                fullName.IndexOf("ImageTargetPreview", StringComparison.OrdinalIgnoreCase) >= 0;

            if (!looksLikeVuforiaPreview) continue;

            Undo.DestroyObjectImmediate(component);
            removed++;
        }

        return removed;
    }

    private static void EditorSceneManagerMarkAllDirtySafe()
    {
        try
        {
            for (int sceneIndex = 0; sceneIndex < SceneManager.sceneCount; sceneIndex++)
            {
                Scene scene = SceneManager.GetSceneAt(sceneIndex);
                if (scene.isLoaded)
                    UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
            }
        }
        catch
        {
            // Safe no-op. The fix already removed the component.
        }
    }
}
#endif
