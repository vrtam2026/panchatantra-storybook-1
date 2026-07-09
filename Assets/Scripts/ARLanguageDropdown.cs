using System;
using System.Collections.Generic;
using UnityEngine;
using TMPro;

/// <summary>
/// Runtime language dropdown for the AR scene.
/// Does NOT reload the scene. Calls ARGlobalLanguage.SetCurrentLanguage().
/// Requires a TMP_Dropdown component (on this GameObject or assigned in Inspector).
///
/// The list of languages is read automatically from the assigned audio catalog --
/// every distinct language name already present in its entries shows up here with
/// no manual editing. Add a new language (create the ARStorybookLanguage asset,
/// drop its audio files in, click "Set Up X Audio" in its Inspector) and the very
/// next time this scene runs, the dropdown lists it too.
/// </summary>
public class ARLanguageDropdown : MonoBehaviour
{
    [SerializeField] private TMP_Dropdown dropdown;

    [Tooltip("Audio catalog to read the language list from. Every distinct languageName in its entries becomes a dropdown option.")]
    [SerializeField] private ARAddressableAudioCatalog catalog;

    [Tooltip("Used only if no catalog is assigned or it has no entries yet.")]
    [SerializeField] private List<string> languages = new() { "English", "Hindi" };

    private List<string> _options = new();
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

        _options = BuildLanguageList();

        dropdown.ClearOptions();
        dropdown.AddOptions(new List<string>(_options));

        // Set initial value to match current saved language — suppress the event so it
        // doesn't fire OnLanguageChanged on startup (which would cause an unwanted restart)
        _suppressEvent = true;
        string current = ARGlobalLanguage.GetCurrentLanguage();
        int idx = _options.FindIndex(l => string.Equals(l, current, StringComparison.OrdinalIgnoreCase));
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

    // Reads every distinct languageName already present in the catalog's entries.
    // English (the reference language) is always sorted first when present; the
    // rest are alphabetical, so a freshly added language just slots in on its own.
    private List<string> BuildLanguageList()
    {
        var found = new List<string>();

        if (catalog != null)
        {
            foreach (var entry in catalog.GetAllEntries())
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.languageName)) continue;
                if (!found.Exists(l => string.Equals(l, entry.languageName, StringComparison.OrdinalIgnoreCase)))
                    found.Add(entry.languageName);
            }
        }

        if (found.Count == 0)
        {
            // This masks a real setup mistake (catalog not assigned, or assigned but
            // empty) as if only English/Hindi existed -- log it loudly so it's not
            // mistaken for the actual language list.
            Debug.LogWarning(catalog == null
                ? "[AR-LANG] No catalog assigned on ARLanguageDropdown -- falling back to the manual languages list. Assign the AudioLanguageCatalog asset in the Inspector so the dropdown reflects real languages automatically."
                : "[AR-LANG] Catalog assigned but has zero entries -- falling back to the manual languages list. Connect at least one language's audio first.");
            return new List<string>(languages);
        }

        found.Sort(StringComparer.OrdinalIgnoreCase);
        int englishIdx = found.FindIndex(l => l.Equals("English", StringComparison.OrdinalIgnoreCase));
        if (englishIdx > 0)
        {
            string english = found[englishIdx];
            found.RemoveAt(englishIdx);
            found.Insert(0, english);
        }
        return found;
    }

    private void OnDropdownChanged(int index)
    {
        if (_suppressEvent) return;
        if (index < 0 || index >= _options.Count) return;

        string selected = _options[index];
        Debug.Log($"[AR-LANG] Dropdown selected: {selected}");
        ARGlobalLanguage.SetCurrentLanguage(selected);
    }
}
