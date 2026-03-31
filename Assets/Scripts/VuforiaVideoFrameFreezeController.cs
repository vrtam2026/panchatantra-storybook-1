using System.Collections;
using UnityEngine;
using UnityEngine.Video;

public static class VuforiaVideoFrameFreezeController
{
    public enum FreezeMode
    {
        None = 0,
        FreezeFirstFrame = 1,
        FreezeLastFrameThenStop = 2
    }

    public static IEnumerator ApplyFreeze(VideoPlayer videoPlayer, FreezeMode mode, float seconds = 0.25f)
    {
        if (videoPlayer == null) yield break;

        switch (mode)
        {
            case FreezeMode.FreezeFirstFrame:
                yield return FreezeFirstFrame(videoPlayer);
                break;
            case FreezeMode.FreezeLastFrameThenStop:
                yield return FreezeLastFrameThenStop(videoPlayer, seconds);
                break;
        }
    }

    public static IEnumerator FreezeFirstFrame(VideoPlayer videoPlayer)
    {
        if (videoPlayer == null) yield break;

        if (!videoPlayer.isPrepared)
        {
            videoPlayer.Prepare();
            while (!videoPlayer.isPrepared) yield return null;
        }

        videoPlayer.time = 0;
        videoPlayer.Play();
        yield return null;
        videoPlayer.Pause();
    }

    public static IEnumerator FreezeLastFrameThenStop(VideoPlayer videoPlayer, float seconds)
    {
        if (videoPlayer == null) yield break;

        if (!videoPlayer.isPrepared)
        {
            videoPlayer.Prepare();
            while (!videoPlayer.isPrepared) yield return null;
        }

        double targetTime = Mathf.Max(0f, (float)videoPlayer.length - seconds);
        videoPlayer.time = targetTime;
        videoPlayer.Play();

        float t = 0f;
        while (t < seconds)
        {
            t += Time.deltaTime;
            yield return null;
        }

        videoPlayer.Pause();
    }
}
