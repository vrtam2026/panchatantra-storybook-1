using UnityEngine;
using UnityEngine.InputSystem;

public class testPinchScale : MonoBehaviour
{
    public float speed = 0.002f;
    public float minScale = 0.5f;
    public float maxScale = 3f;

    float lastDistance;

    void Update()
    {
        var touch = Touchscreen.current;
        if (touch == null) return;

        if (touch.touches[0].press.isPressed && touch.touches[1].press.isPressed)
        {
            Vector2 p1 = touch.touches[0].position.ReadValue();
            Vector2 p2 = touch.touches[1].position.ReadValue();

            float currentDistance = Vector2.Distance(p1, p2);

            if (lastDistance != 0)
            {
                float scaleChange = (currentDistance - lastDistance) * speed;
                float newScale = transform.localScale.x + scaleChange;
                newScale = Mathf.Clamp(newScale, minScale, maxScale);
                transform.localScale = new Vector3(newScale, newScale, newScale);
            }

            lastDistance = currentDistance;
        }
        else
        {
            lastDistance = 0;
        }
    }
}