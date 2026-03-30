using UnityEngine;
using UnityEngine.Video;

public class ShaderWarmup : MonoBehaviour
{
    [Header("Render Scale")]
    [Tooltip("1.0 = full resolution. 0.92 = 92% resolution (saves GPU, invisible difference). Lower = faster but blurrier.")]
    [Range(0.5f, 1.0f)]
    public float renderScale = 0.92f;

    [Header("Frame Rate")]
    [Tooltip("Target FPS. 30 recommended for AR to save battery.")]
    public int targetFPS = 30;

    [Header("Video Player Settings")]
    [Tooltip("Auto-configure all VideoPlayers for smooth playback at startup")]
    public bool configureVideoPlayers = true;

    void Awake()
    {
        Screen.sleepTimeout = SleepTimeout.NeverSleep;
        Application.targetFrameRate = targetFPS;
        QualitySettings.vSyncCount = 0;
        Shader.WarmupAllShaders();

        var urpAsset = UnityEngine.Rendering.Universal.UniversalRenderPipeline.asset;
        if (urpAsset != null)
            urpAsset.renderScale = renderScale;
    }

    void Start()
    {
        if (configureVideoPlayers)
            ConfigureAllVideoPlayers();
    }

    private void ConfigureAllVideoPlayers()
    {
        VideoPlayer[] players = FindObjectsByType<VideoPlayer>(FindObjectsSortMode.None);
        for (int i = 0; i < players.Length; i++)
        {
            if (players[i] == null) continue;
            players[i].skipOnDrop = true;
            players[i].playOnAwake = false;
            players[i].waitForFirstFrame = false;
        }
    }
}