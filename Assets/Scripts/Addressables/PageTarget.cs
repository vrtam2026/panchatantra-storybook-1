using UnityEngine;

public class PageTarget : MonoBehaviour
{
    public int pageIndex;  // set in Inspector: 0 for page1, 1 for page2...
    ARWindowManager _manager;

    void Start() => _manager = FindFirstObjectByType<ARWindowManager>();

    public void OnTargetFound()
    {
        _manager.OnPageDetected(pageIndex);

        var model = _manager.GetLoadedAsset(pageIndex);
        if (model != null)
            Instantiate(model, transform);
        // if null, it's still downloading — handle with a loading spinner
    }
}
