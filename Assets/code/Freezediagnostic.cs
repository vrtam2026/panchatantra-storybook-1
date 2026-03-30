using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Video;

/// <summary>
/// Attach this to a GameObject in your scene.
/// It monitors every frame. When a freeze happens (frame takes longer than threshold),
/// it captures what was happening at that exact moment and displays it on screen.
/// 
/// This script does NOT fix anything. It only DIAGNOSES.
/// After reading the results, you'll know exactly what to fix.
/// </summary>
public class FreezeDiagnostic : MonoBehaviour
{
    [Header("Detection Settings")]
    [Tooltip("Frame time above this (ms) is considered a freeze")]
    public float freezeThresholdMs = 50f; // 50ms = below 20fps = noticeable stutter

    [Tooltip("Maximum number of freeze events to store")]
    public int maxStoredFreezes = 20;

    [Header("Display")]
    public bool showOnScreen = true;

    // ---- Freeze tracking ----
    private struct FreezeEvent
    {
        public float time;           // When it happened (seconds since app start)
        public float durationMs;     // How long the freeze lasted
        public string cause;         // What we think caused it
        public float memoryMB;       // Memory at time of freeze
        public int videoPlayersActive; // How many videos were playing
        public bool vuforiaTracking; // Was Vuforia tracking at that moment
    }

    private List<FreezeEvent> freezeLog = new List<FreezeEvent>();

    // ---- Frame monitoring ----
    private float lastFrameTime;
    private float worstFrameMs = 0f;
    private int totalFreezes = 0;
    private float sessionTime = 0f;

    // ---- GC tracking ----
    private long lastGCCount = 0;
    private long lastAllocatedMemory = 0;

    // ---- Shader tracking ----
    private bool shaderWarmupDone = false;
    private float shaderWarmupTime = 0f;

    // ---- Video tracking ----
    private Dictionary<VideoPlayer, bool> videoStates = new Dictionary<VideoPlayer, bool>();
    private bool videoJustStarted = false;
    private bool videoPreparing = false;

    // ---- GUI ----
    private GUIStyle headerStyle;
    private GUIStyle normalStyle;
    private GUIStyle redStyle;
    private GUIStyle yellowStyle;
    private GUIStyle greenStyle;
    private Texture2D bgTexture;
    private bool stylesReady = false;
    private Vector2 scrollPosition;
    private bool showFullLog = false;

    void Start()
    {
        lastFrameTime = Time.realtimeSinceStartup;
        lastGCCount = System.GC.CollectionCount(0);
        lastAllocatedMemory = Profiler.GetTotalAllocatedMemoryLong();

        // Find all video players in scene
        RefreshVideoPlayers();

        Debug.Log("[FreezeDiagnostic] Started. Threshold: " + freezeThresholdMs + "ms");
    }

    void Update()
    {
        sessionTime += Time.unscaledDeltaTime;
        float currentTime = Time.realtimeSinceStartup;
        float frameMs = (currentTime - lastFrameTime) * 1000f;
        lastFrameTime = currentTime;

        if (frameMs > worstFrameMs) worstFrameMs = frameMs;

        // Detect freeze
        if (frameMs > freezeThresholdMs)
        {
            totalFreezes++;
            string cause = DiagnoseFreezeCause(frameMs);

            FreezeEvent evt = new FreezeEvent
            {
                time = sessionTime,
                durationMs = frameMs,
                cause = cause,
                memoryMB = Profiler.GetTotalAllocatedMemoryLong() / (1024f * 1024f),
                videoPlayersActive = CountActiveVideos(),
                vuforiaTracking = IsVuforiaTracking()
            };

            if (freezeLog.Count < maxStoredFreezes)
                freezeLog.Add(evt);

            Debug.LogWarning("[FREEZE] " + frameMs.ToString("F0") + "ms | Cause: " + cause
                + " | Memory: " + evt.memoryMB.ToString("F0") + "MB"
                + " | Videos: " + evt.videoPlayersActive
                + " | Time: " + sessionTime.ToString("F1") + "s");
        }

        // Track GC
        lastGCCount = System.GC.CollectionCount(0);

        // Track memory changes
        lastAllocatedMemory = Profiler.GetTotalAllocatedMemoryLong();

        // Track video state changes
        TrackVideoStateChanges();
    }

    private string DiagnoseFreezeCause(float frameMs)
    {
        StringBuilder causes = new StringBuilder();

        // Check 1: GC spike
        long currentGC = System.GC.CollectionCount(0);
        if (currentGC > lastGCCount)
        {
            causes.Append("GC_COLLECTION ");
        }

        // Check 2: Memory spike (large allocation)
        long currentMemory = Profiler.GetTotalAllocatedMemoryLong();
        float memDiffMB = (currentMemory - lastAllocatedMemory) / (1024f * 1024f);
        if (memDiffMB > 5f) // More than 5MB allocated in one frame
        {
            causes.Append("LARGE_ALLOC(" + memDiffMB.ToString("F1") + "MB) ");
        }

        // Check 3: Video just started or is preparing
        if (videoJustStarted)
        {
            causes.Append("VIDEO_START ");
            videoJustStarted = false;
        }
        if (videoPreparing)
        {
            causes.Append("VIDEO_PREPARE ");
        }

        // Check 4: Shader compilation (heuristic - large frame time with no other cause)
        if (frameMs > 200f && causes.Length == 0)
        {
            causes.Append("SHADER_COMPILE(likely) ");
        }

        // Check 5: Very long freeze (5+ seconds) = likely asset loading
        if (frameMs > 5000f)
        {
            causes.Append("ASSET_LOAD(likely) ");
        }

        // Check 6: Medium freeze with video active = video decode stall
        if (frameMs > 50f && frameMs < 500f && CountActiveVideos() > 0 && causes.Length == 0)
        {
            causes.Append("VIDEO_DECODE_STALL ");
        }

        // Check 7: Vuforia tracking state
        if (!IsVuforiaTracking() && causes.Length == 0)
        {
            causes.Append("VUFORIA_REACQUIRE ");
        }

        if (causes.Length == 0)
            causes.Append("UNKNOWN");

        return causes.ToString().Trim();
    }

    private void TrackVideoStateChanges()
    {
        videoJustStarted = false;
        videoPreparing = false;

        VideoPlayer[] players = FindObjectsByType<VideoPlayer>(FindObjectsSortMode.None);
        foreach (var vp in players)
        {
            if (vp == null) continue;

            bool wasPlaying = false;
            if (videoStates.ContainsKey(vp))
                wasPlaying = videoStates[vp];

            bool isNowPlaying = vp.isPlaying;

            if (isNowPlaying && !wasPlaying)
                videoJustStarted = true;

            if (vp.isPrepared == false && vp.enabled)
                videoPreparing = true;

            videoStates[vp] = isNowPlaying;
        }
    }

    private int CountActiveVideos()
    {
        int count = 0;
        VideoPlayer[] players = FindObjectsByType<VideoPlayer>(FindObjectsSortMode.None);
        foreach (var vp in players)
        {
            if (vp != null && vp.isPlaying) count++;
        }
        return count;
    }

    private bool IsVuforiaTracking()
    {
        // Simple check: if any image target children are active
        // This is a heuristic since we can't directly query Vuforia state without coupling
        var targets = FindObjectsByType<Vuforia.ObserverBehaviour>(FindObjectsSortMode.None);
        foreach (var t in targets)
        {
            if (t != null && t.enabled && t.gameObject.activeInHierarchy)
            {
                // Check if target has active children (content visible = tracking)
                if (t.transform.childCount > 0)
                {
                    for (int i = 0; i < t.transform.childCount; i++)
                    {
                        if (t.transform.GetChild(i).gameObject.activeSelf)
                            return true;
                    }
                }
            }
        }
        return false;
    }

    private void RefreshVideoPlayers()
    {
        videoStates.Clear();
        VideoPlayer[] players = FindObjectsByType<VideoPlayer>(FindObjectsSortMode.None);
        foreach (var vp in players)
        {
            if (vp != null)
                videoStates[vp] = vp.isPlaying;
        }
    }

    // =============================================
    // ON-SCREEN DISPLAY
    // =============================================
    void OnGUI()
    {
        if (!showOnScreen) return;
        if (!Debug.isDebugBuild) return;

        InitStyles();

        float w = 600;
        float x = 10;
        float y = Screen.height - 420;

        // Compact summary always visible
        float summaryH = showFullLog ? 400 : 200;
        GUI.DrawTexture(new Rect(x, y, w, summaryH), bgTexture);

        float cy = y + 8;
        float px = x + 12;
        float cw = w - 24;

        // Header
        headerStyle.normal.textColor = totalFreezes > 0 ? Color.red : Color.green;
        GUI.Label(new Rect(px, cy, cw, 28),
            "FREEZE DIAGNOSTIC | Freezes: " + totalFreezes + " | Worst: " + worstFrameMs.ToString("F0") + "ms",
            headerStyle);
        cy += 32;

        // Current frame info
        float currentFrameMs = Time.unscaledDeltaTime * 1000f;
        Color frameColor = currentFrameMs < 35 ? Color.green : currentFrameMs < 50 ? Color.yellow : Color.red;
        normalStyle.normal.textColor = frameColor;
        GUI.Label(new Rect(px, cy, cw, 24),
            "Frame: " + currentFrameMs.ToString("F1") + "ms | FPS: " + (1f / Time.unscaledDeltaTime).ToString("F0")
            + " | Videos: " + CountActiveVideos()
            + " | Memory: " + (Profiler.GetTotalAllocatedMemoryLong() / (1024f * 1024f)).ToString("F0") + "MB",
            normalStyle);
        cy += 28;

        // Last 3 freezes
        normalStyle.normal.textColor = Color.yellow;
        GUI.Label(new Rect(px, cy, cw, 24), "-- RECENT FREEZES --", normalStyle);
        cy += 26;

        int startIdx = Mathf.Max(0, freezeLog.Count - 3);
        for (int i = startIdx; i < freezeLog.Count; i++)
        {
            FreezeEvent evt = freezeLog[i];
            Color c = evt.durationMs > 1000 ? Color.red : evt.durationMs > 200 ? new Color(1f, 0.6f, 0f) : Color.yellow;
            normalStyle.normal.textColor = c;
            normalStyle.fontSize = 18;
            GUI.Label(new Rect(px, cy, cw, 22),
                "#" + (i + 1) + " | " + evt.durationMs.ToString("F0") + "ms | " + evt.cause
                + " | Mem:" + evt.memoryMB.ToString("F0") + "MB"
                + " | @" + evt.time.ToString("F1") + "s",
                normalStyle);
            normalStyle.fontSize = 22;
            cy += 24;
        }

        if (freezeLog.Count == 0)
        {
            normalStyle.normal.textColor = Color.green;
            GUI.Label(new Rect(px, cy, cw, 24), "No freezes detected yet. Keep testing...", normalStyle);
            cy += 24;
        }

        // Toggle full log
        normalStyle.normal.textColor = Color.cyan;
        if (GUI.Button(new Rect(px, cy, 200, 30), showFullLog ? "Hide Full Log" : "Show Full Log"))
        {
            showFullLog = !showFullLog;
        }

        if (showFullLog && freezeLog.Count > 0)
        {
            cy += 35;
            normalStyle.fontSize = 16;

            // Cause summary
            Dictionary<string, int> causeCounts = new Dictionary<string, int>();
            foreach (var evt in freezeLog)
            {
                string[] parts = evt.cause.Split(' ');
                foreach (string part in parts)
                {
                    string key = part.Trim();
                    if (string.IsNullOrEmpty(key)) continue;
                    if (causeCounts.ContainsKey(key))
                        causeCounts[key]++;
                    else
                        causeCounts[key] = 1;
                }
            }

            normalStyle.normal.textColor = new Color(1f, 0.8f, 0.2f);
            GUI.Label(new Rect(px, cy, cw, 20), "-- CAUSE SUMMARY --", normalStyle);
            cy += 22;

            foreach (var kv in causeCounts)
            {
                normalStyle.normal.textColor = Color.white;
                GUI.Label(new Rect(px, cy, cw, 20),
                    kv.Key + ": " + kv.Value + " times", normalStyle);
                cy += 20;
            }

            normalStyle.fontSize = 22;
        }
    }

    private void InitStyles()
    {
        if (stylesReady) return;

        bgTexture = new Texture2D(1, 1);
        bgTexture.SetPixel(0, 0, new Color(0, 0, 0, 0.85f));
        bgTexture.Apply();

        headerStyle = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold };
        normalStyle = new GUIStyle(GUI.skin.label) { fontSize = 22 };

        stylesReady = true;
    }
}