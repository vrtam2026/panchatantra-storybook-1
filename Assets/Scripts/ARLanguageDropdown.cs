using System.Collections.Generic;
using UnityEngine;
using TMPro;

/// <summary>
/// Runtime language dropdown for the AR scene.
/// Does NOT reload the scene. Calls ARGlobalLanguage.SetCurrentLanguage().
/// Requires a TMP_Dropdown component (on this GameObject or assigned in Inspector).
/// Safe to add more languages later by adding to the languages list.
/// </summary>
public class ARLanguageDropdown : MonoBehaviour
{
    [SerializeField] private TMP_Dropdown dropdown;

    [Tooltip("Languages shown in the dropdown. Must match ARGlobalLanguage values exactly.")]
    [SerializeField] private List<string> languages = new() { "English", "Hindi" };

    private bool _suppressEvent = false;

    // ---------------------------------------------------------------

    private void Awake()
    {
        if (dropdown == null)
            dropdown = GetComponent<TMP_Dropdown>();

        if (dropdown == null)
        {
            Debug.LogError("[AR-LANG] ARLanguageDropdown: No TMP_Dropdown found. " +
                           "Add TMP_Dropdown to this GameObject or assign it in Inspector.");
            return;
        }

        // Build options from the languages list
        dropdown.ClearOptions();
        dropdown.AddOptions(new List<string>(languages));

        // Set initial value to match current saved language — suppress the event so it
        // doesn't fire OnLanguageChanged on startup (which would cause an unwanted restart)
        _suppressEvent = true;
        string current = ARGlobalLanguage.GetCurrentLanguage();
        int idx = languages.FindIndex(l => string.Equals(l, current, System.StringComparison.OrdinalIgnoreCase));
        dropdown.value = idx >= 0 ? idx : 0;
        dropdown.RefreshShownValue();
        _suppressEvent = false;

        dropdown.onValueChanged.AddListener(OnDropdownChanged);
    }

    private void OnDestroy()
    {
        if (dropdown != null)
            dropdown.onValueChanged.RemoveListener(OnDropdownChanged);
    }

    // ---------------------------------------------------------------

    private void OnDropdownChanged(int index)
    {
        if (_suppressEvent) return;
        if (index < 0 || index >= languages.Count) return;

        string selected = languages[index];
        Debug.Log($"[AR-LANG] Dropdown selected: {selected}");
        ARGlobalLanguage.SetCurrentLanguage(selected);
    }
}
