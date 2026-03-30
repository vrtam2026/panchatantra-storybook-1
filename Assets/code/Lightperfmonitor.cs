using UnityEngine;
using UnityEngine.Profiling;

/// <summary>
/// Lightweight performance monitor. Zero GC allocations per frame.
/// Does NOT affect app performance (unlike the old FreezeDiagnostic).
/// 
/// How it works:
/// - Tracks frame times using only primitive types (no strings, no arrays, no Find calls)
/// - Every 5 seconds, logs a summary to Android Logcat (viewable via USB)
/// - On screen: shows only a tiny colored dot (green/yellow/red) in the corner
/// - No FindObjectsByType, no GetComponent, no string operations in Update
/// 
/// How to read the results:
/// 1. Connect phone via USB
/// 2. In Unity: Window > Analysis > Android Logcat (or use ADB)
/// 3. Filter by tag: "PerfMon"
/// 4. You'll see logs like:
///    [PerfMon] FPS:29 | Freezes:2 | Worst:156ms | GC:1 | Mem:185MB | VideoPrepare:1
/// 
/// To disable on-screen dot: uncheck "Show Dot On Screen" in Inspector
/// To remove from release build: just delete the GameObject or uncheck the script
/// </summary>
public class LightPerfMonitor : MonoBehaviour
{
    [Header("Settings")]
    [Tooltip("Frame time above this (ms) counts as a freeze")]
    public float freezeThresholdMs = 50f;

    [Tooltip("How often to print summary to Logcat (seconds)")]
    public float logIntervalSeconds = 5f;

    [Tooltip("Show a tiny colored dot on screen (green=good, yellow=ok, red=bad)")]
    public bool showDotOnScreen = true;

    [Tooltip("Show text stats on screen (more info but slightly more overhead)")]
    public bool showTextOnScreen = true;

    // ---- Pre-allocated tracking variables (zero GC) ----
    private float _lastFrameTime;
    private float _logTimer;

    // Per-interval counters (reset every logIntervalSeconds)
    private int _freezeCount;
    private float _worstFrameMs;
    private int _gcCount;
    private long _lastGCGen0;
    private int _totalFrames;
    private float _totalFrameTime;

    // Lifetime counters
    private int _lifetimeFreezes;
    private float _lifetimeWorstMs;
    private float _sessionSeconds;

    // Current frame (for dot color)
    private float _currentFrameMs;

    // Pre-allocated GUI objects (created once, reused forever)
    private Texture2D _dotTexture;
    private GUIStyle _textStyle;
    private bool _guiReady;

    void Start()
    {
        _lastFrameTime = Time.realtimeSinceStartup;
        _lastGCGen0 = System.GC.CollectionCount(0);
        _logTimer = logIntervalSeconds;

        Debug.Log("[PerfMon] Started. Threshold: " + freezeThresholdMs + "ms. Log every " + logIntervalSeconds + "s");
    }

    void Update()
    {
        // ---- Frame time measurement (zero allocation) ----
        float now = Time.realtimeSinceStartup;
        _currentFrameMs = (now - _lastFrameTime) * 1000f;
        _lastFrameTime = now;

        _totalFrames++;
        _totalFrameTime += _currentFrameMs;
        _sessionSeconds += Time.unscaledDeltaTime;

        // Track worst frame
        if (_currentFrameMs > _worstFrameMs)
            _worstFrameMs = _currentFrameMs;
        if (_currentFrameMs > _lifetimeWorstMs)
            _lifetimeWorstMs = _currentFrameMs;

        // Count freezes
        if (_currentFrameMs > freezeThresholdMs)
        {
            _freezeCount++;
            _lifetimeFreezes++;
        }

        // Count GC collections (zero allocation - just comparing longs)
        long currentGC = System.GC.CollectionCount(0);
        if (currentGC > _lastGCGen0)
        {
            _gcCount += (int)(currentGC - _lastGCGen0);
            _lastGCGen0 = currentGC;
        }

        // ---- Periodic log to Logcat (only allocates string once every 5 seconds) ----
        _logTimer -= Time.unscaledDeltaTime;
        if (_logTimer <= 0f)
        {
            float avgFps = _totalFrames > 0 ? (1000f / (_totalFrameTime / _totalFrames)) : 0f;
            long memMB = Profiler.GetTotalAllocatedMemoryLong() / (1024 * 1024);

            // This is the ONLY string allocation, and it happens once every 5 seconds (not every frame)
            Debug.Log("[PerfMon] FPS:" + avgFps.ToString("F0")
                + " | Freezes:" + _freezeCount
                + " | Worst:" + _worstFrameMs.ToString("F0") + "ms"
                + " | GC:" + _gcCount
                + " | Mem:" + memMB + "MB"
                + " | Session:" + _sessionSeconds.ToString("F0") + "s"
                + " | LifetimeFreezes:" + _lifetimeFreezes);

            // Reset per-interval counters
            _freezeCount = 0;
            _worstFrameMs = 0f;
            _gcCount = 0;
            _totalFrames = 0;
            _totalFrameTime = 0f;
            _logTimer = logIntervalSeconds;
        }
    }

    // ---- Minimal on-screen display (tiny dot + optional text) ----
    void OnGUI()
    {
        if (!showDotOnScreen && !showTextOnScreen) return;
        if (!Debug.isDebugBuild) return;

        InitGUI();

        if (showDotOnScreen)
        {
            // Tiny 20x20 colored dot in top-left corner
            Color dotColor;
            if (_currentFrameMs < 36f)
                dotColor = Color.green;       // Good: under 36ms (above 27fps)
            else if (_currentFrameMs < 50f)
                dotColor = Color.yellow;      // OK: 20-27fps
            else
                dotColor = Color.red;         // Bad: freeze or very low fps

            _dotTexture.SetPixel(0, 0, dotColor);
            _dotTexture.Apply();

            GUI.DrawTexture(new Rect(10, 10, 20, 20), _dotTexture);
        }

        if (showTextOnScreen)
        {
            float fps = _currentFrameMs > 0.001f ? 1000f / _currentFrameMs : 0f;
            long memMB = Profiler.GetTotalAllocatedMemoryLong() / (1024 * 1024);

            // Compact single-line display
            string info = "FPS:" + fps.ToString("F0")
                + " Mem:" + memMB + "MB"
                + " F:" + _lifetimeFreezes
                + " W:" + _lifetimeWorstMs.ToString("F0") + "ms";

            // Black shadow for readability
            _textStyle.normal.textColor = Color.black;
            GUI.Label(new Rect(36, 9, 400, 25), info, _textStyle);

            // Colored text on top
            Color textColor;
            if (_currentFrameMs < 36f) textColor = Color.green;
            else if (_currentFrameMs < 50f) textColor = Color.yellow;
            else textColor = Color.red;

            _textStyle.normal.textColor = textColor;
            GUI.Label(new Rect(35, 8, 400, 25), info, _textStyle);
        }
    }

    private void InitGUI()
    {
        if (_guiReady) return;

        _dotTexture = new Texture2D(1, 1);
        _dotTexture.SetPixel(0, 0, Color.green);
        _dotTexture.Apply();

        _textStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 18,
            fontStyle = FontStyle.Bold
        };

        _guiReady = true;
    }
}