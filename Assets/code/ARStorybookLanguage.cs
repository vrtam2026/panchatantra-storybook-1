using UnityEngine;

/// <summary>
/// A language this storybook supports. There is nothing to configure inside it --
/// the ASSET'S OWN NAME (the file name you see in the Project window) is the
/// language's name everywhere else in the system.
///
/// ADD A LANGUAGE: right-click in the Project window → Create → AR Storybook →
///   Language → rename the new asset to the language's name (e.g. "Telugu"). Done.
///
/// REMOVE A LANGUAGE: select the asset, press Delete. This only removes this small
///   marker file -- it does NOT delete any recordings or audio pack files, so you
///   can bring the language back later just by creating the asset again with the
///   exact same name.
///
/// One of these named "English" should always exist -- it is treated as the
/// reference language that test tools copy voices FROM.
/// </summary>
[CreateAssetMenu(menuName = "AR Storybook/Language", fileName = "New Language")]
public class ARStorybookLanguage : ScriptableObject
{
    public string LanguageName => name;
}
