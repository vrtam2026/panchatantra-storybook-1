using System.Collections;
using UnityEngine;
using UnityEngine.Video;

public class ContentController : MonoBehaviour, IARContent
{
    private VideoPlayer[] vPlayers;
    private Animator[] anims;
    private AudioSource[] audios;
    private System.Action onCompleted;
    private Coroutine animCompletionCoroutine;

    void Awake()
    {
        vPlayers = GetComponentsInChildren<VideoPlayer>();
        anims = GetComponentsInChildren<Animator>();
        audios = GetComponentsInChildren<AudioSource>();
    }

    void Start()
    {
        // subscribe to video end events
        foreach (var v in vPlayers)
            v.loopPointReached += OnVideoEnded;
    }

    void OnDestroy()
    {
        foreach (var v in vPlayers)
            v.loopPointReached -= OnVideoEnded;
    }

    //IARContent 

    public void SetCompletionCallback(System.Action callback)
    {
        onCompleted = callback;
    }

    public void PlayContent()
    {
        foreach (var v in vPlayers) v.Play();
        foreach (var a in anims) a.speed = 1f;
        foreach (var s in audios) s.UnPause();

        // start watching for animation completion if no video
        if (vPlayers.Length == 0 && anims.Length > 0)
        {
            if (animCompletionCoroutine != null)
                StopCoroutine(animCompletionCoroutine);
            animCompletionCoroutine = StartCoroutine(WaitForAnimationEnd());
        }
    }

    public void PauseContent()
    {
        foreach (var v in vPlayers) v.Pause();
        foreach (var a in anims) a.speed = 0f;
        foreach (var s in audios) s.Pause();
    }

    public void ReplayContent()
    {
        // replay video
        foreach (var v in vPlayers)
        {
            v.time = 0;
            v.Play();
        }

        // replay animation from start
        foreach (var a in anims)
        {
            a.speed = 1f;
            a.Play(a.GetCurrentAnimatorStateInfo(0).fullPathHash, 0, 0f);
        }

        // replay audio
        foreach (var s in audios)
        {
            s.Stop();
            s.Play();
        }

        // restart animation completion check if no video
        if (vPlayers.Length == 0 && anims.Length > 0)
        {
            if (animCompletionCoroutine != null)
                StopCoroutine(animCompletionCoroutine);
            animCompletionCoroutine = StartCoroutine(WaitForAnimationEnd());
        }
    }

    // Completion 

    private void OnVideoEnded(VideoPlayer vp)
    {
        // only fire once — when the last video ends
        foreach (var v in vPlayers)
            if (v.isPlaying) return;

        onCompleted?.Invoke();
    }

    private IEnumerator WaitForAnimationEnd()
    {
        yield return null; // wait one frame for animation to start

        // wait for the longest animation to finish
        float maxLength = 0f;
        foreach (var a in anims)
        {
            float length = a.GetCurrentAnimatorStateInfo(0).length;
            if (length > maxLength) maxLength = length;
        }

        yield return new WaitForSeconds(maxLength);
        onCompleted?.Invoke();
    }
}

// Keep this interface for the Image Target script to talk to
public interface IARContent
{
    void PlayContent();
    void PauseContent();
    void ReplayContent();
    void SetCompletionCallback(System.Action onCompleted);
}