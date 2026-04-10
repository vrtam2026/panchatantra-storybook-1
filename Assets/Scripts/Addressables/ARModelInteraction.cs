using UnityEngine;

public class ARModelInteraction : MonoBehaviour
{
    private Animator animator;
    private AudioSource audioSource;

    void Awake()
    {
        animator = GetComponent<Animator>();
        audioSource = GetComponent<AudioSource>();
    }

    public void PlayInteraction()
    {
        if (animator != null)
        {
            animator.Play("Bull_moo");
        }

        if (audioSource != null && !audioSource.isPlaying)
        {
            audioSource.Play();
        }
    }
}
