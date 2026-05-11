using UnityEngine;
using UnityEngine.InputSystem;

public class ARTapHandler : MonoBehaviour
{
    void Update()
    {
        // Mouse click
        if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
        {
            HandleTap(Mouse.current.position.ReadValue());
        }

        // Touch
        if (Touchscreen.current != null && Touchscreen.current.primaryTouch.press.wasPressedThisFrame)
        {
            HandleTap(Touchscreen.current.primaryTouch.position.ReadValue());
        }
    }

    /*void HandleTap(Vector2 inputPosition)
    {
        Ray ray = Camera.main.ScreenPointToRay(inputPosition);
        RaycastHit hit;

        if (Physics.Raycast(ray, out hit))
        {
            ARModelInteraction interaction = hit.collider.GetComponentInParent<ARModelInteraction>();

            if (interaction != null)
            {
                interaction.PlayInteraction();
            }
        }
    }*/

    void HandleTap(Vector2 inputPosition)
    {
        var data = new InteractionData
        {
            screenPosition = inputPosition,
            type = InteractionData.InteractionType.ScreenTap,
            hitObject = null
        };

        Ray ray = Camera.main.ScreenPointToRay(inputPosition);

        if (Physics.Raycast(ray, out RaycastHit hit))
        {
            data.type = InteractionData.InteractionType.ModelTap;
            data.hitObject = hit.collider.gameObject;
        }

        Debug.Log("HandleTap - Screen Tapped");

        ModelInteraction.Current?.OnInteraction?.Invoke(data);
    }
}
