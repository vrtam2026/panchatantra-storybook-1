using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

public class ARWindowManager : MonoBehaviour
{
    [Header("Settings")]
    public int totalPages = 10;
    public int windowSize = 2;         // just set this in Inspector, never changes
    public List<string> pageAddresses; // drag or type addresses in Inspector

    private Dictionary<int, AsyncOperationHandle> _loadedHandles = new();
    private int _currentPage = -1;

    void Start()
    {
        for (int i = 0; i < Mathf.Min(windowSize + 1, totalPages); i++)
            LoadPage(i);
    }

    public void OnPageDetected(int pageIndex)
    {
        if (pageIndex == _currentPage) return;
        _currentPage = pageIndex;
        UpdateWindow(pageIndex);
    }

    void UpdateWindow(int center)
    {
        int from = Mathf.Max(0, center - windowSize);
        int to = Mathf.Min(totalPages - 1, center + windowSize);

        List<int> toRelease = new();
        foreach (int page in _loadedHandles.Keys)
            if (page < from || page > to)
                toRelease.Add(page);

        foreach (int page in toRelease)
            ReleasePage(page);

        for (int i = from; i <= to; i++)
            if (!_loadedHandles.ContainsKey(i))
                LoadPage(i);
    }

    async void LoadPage(int pageIndex)
    {
        if (_loadedHandles.ContainsKey(pageIndex)) return;

        var handle = Addressables.LoadAssetAsync<GameObject>(pageAddresses[pageIndex]);
        _loadedHandles[pageIndex] = handle;
        await handle.Task;

        if (handle.Status != AsyncOperationStatus.Succeeded)
        {
            Debug.LogError($"Failed to load page {pageIndex + 1}");
            _loadedHandles.Remove(pageIndex);
        }
    }

    void ReleasePage(int pageIndex)
    {
        if (!_loadedHandles.TryGetValue(pageIndex, out var handle)) return;
        Addressables.Release(handle);
        _loadedHandles.Remove(pageIndex);
    }

    public GameObject GetLoadedAsset(int pageIndex)
    {
        if (_loadedHandles.TryGetValue(pageIndex, out var handle)
            && handle.Status == AsyncOperationStatus.Succeeded)
            return handle.Result as GameObject;
        return null;
    }
}
