using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

public class ARPreloader : MonoBehaviour
{
    public string labelToPreload = "Preload";

    void Start()
    {
        // This starts the download from CCD immediately
        AsyncOperationHandle downloadHandle = Addressables.DownloadDependenciesAsync(labelToPreload);

        downloadHandle.Completed += (handle) => {
            if (handle.Status == AsyncOperationStatus.Succeeded)
            {
                Debug.Log("Video Prefab cached and ready!");
            }
            Addressables.Release(handle);
        };
    }
}
