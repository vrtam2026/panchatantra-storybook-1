using System;
using UnityEngine;

public static class ARGlobalLanguage
{
    public const string PlayerPrefsKey = "AR_CURRENT_LANGUAGE";

    public static event Action<string> OnLanguageChanged;

    public static string GetCurrentLanguage()
    {
        return PlayerPrefs.GetString(PlayerPrefsKey, "English");
    }

    public static void SetCurrentLanguage(string language)
    {
        if (string.IsNullOrWhiteSpace(language)) return;

        string current = GetCurrentLanguage();
        if (string.Equals(current, language, StringComparison.Ordinal)) return;

        PlayerPrefs.SetString(PlayerPrefsKey, language);
        PlayerPrefs.Save();

        OnLanguageChanged?.Invoke(language);
    }

    // Backward compatible API (your LanguageSelectAndOpenScene expects this)
    public static void SetLanguage(string language)
    {
        SetCurrentLanguage(language);
    }

    // Optional alias if you ever referenced it elsewhere
    public static string GetLanguage()
    {
        return GetCurrentLanguage();
    }
}
