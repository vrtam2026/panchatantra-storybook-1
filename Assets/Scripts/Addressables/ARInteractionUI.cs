using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Backward-compatible name for existing projects that already have ARInteractionUI in the scene.
/// It inherits the reusable ActivityPanel behaviour.
/// </summary>
public class ARInteractionUI : ActivityPanel
{
    public new static ARInteractionUI Instance { get; private set; }

    protected override void Awake()
    {
        base.Awake();
        Instance = this;
    }

    // Legacy helper used by older ContentController code.
    public void ShowButtons()
    {
        ShowButtons(new List<string> { "Left", "Right" }, null);
    }
}
