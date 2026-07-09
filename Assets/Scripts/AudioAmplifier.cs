using UnityEngine;

/// <summary>
/// Attach to any GameObject with an AudioSource.
/// Multiplies raw audio samples directly — genuine amplification beyond Unity's volume cap.
/// Set multiplier to 5 for 5x volume.
/// </summary>
[RequireComponent(typeof(AudioSource))]
public class AudioAmplifier : MonoBehaviour
{
    [Range(1f, 10f)]
    public float multiplier = 5f;

    // OnAudioFilterRead runs on the audio thread, directly on PCM sample data.
    // Multiplying each sample is real amplification — not Unity's volume slider trick.
    private void OnAudioFilterRead(float[] data, int channels)
    {
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = Mathf.Clamp(data[i] * multiplier, -1f, 1f);
        }
    }
}