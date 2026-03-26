using UnityEngine;
using UnityEngine.Profiling;
using Vuforia;

public class AppInitializer : MonoBehaviour
{
    [Header("Benchmark Settings")]
    [Tooltip("Seconds to run uncapped to measure GPU max FPS")]
    public float uncappedDurationSeconds = 30f;

    [Header("Production Settings")]
    public int targetFPS = 30;

    // ============ Phase Control ============
    private float timer = 0f;
    private bool isBenchmarkPhase = true;

    // ============ Phase 1 Tracking ============
    private float maxFpsRecorded = 0f;
    private float minFpsRecorded = float.MaxValue;
    private float fpsSum = 0f;
    private int fpsFrameCount = 0;
    private float avgFpsDuringBenchmark = 0f;

    // ============ Phase 2 Tracking ============
    private float prodMaxFps = 0f;
    private float prodMinFps = float.MaxValue;
    private float prodFpsSum = 0f;
    private int prodFpsCount = 0;

    // ============ FPS Calculation ============
    private float fpsTimer = 0f;
    private int frameCount = 0;
    private float currentFps = 0f;

    // ============ Temperature ============
    private float batteryTempCelsius = 0f;
    private float startBatteryTemp = 0f;
    private float maxBatteryTemp = 0f;
    private float phase1EndTemp = 0f;
    private float tempUpdateTimer = 0f;
    private string thermalWarning = "Cool";

    // ============ Battery ============
    private float startBatteryLevel = 0f;
    private float currentBatteryLevel = 0f;

    // ============ Memory ============
    private float allocatedMemoryMB = 0f;
    private float reservedMemoryMB = 0f;

    // ============ Session ============
    private float sessionTime = 0f;
    private float cpuFrameTimeMs = 0f;

    // ============ Vuforia ============
    private VuforiaBehaviour vuforiaBehaviour;
    private Camera arCamera;

    // ============ Benchmark Camera ============
    private Camera benchmarkCamera;
    private GameObject spinningCube;

    // ============ GUI ============
    private GUIStyle headerStyle;
    private GUIStyle subHeaderStyle;
    private GUIStyle normalStyle;
    private GUIStyle smallStyle;
    private Texture2D bgTexture;
    private bool stylesReady = false;

    void Awake()
    {
        Screen.sleepTimeout = SleepTimeout.NeverSleep;
        QualitySettings.vSyncCount = 0;

        startBatteryLevel = SystemInfo.batteryLevel;
        startBatteryTemp = GetBatteryTemperature();
        maxBatteryTemp = startBatteryTemp;

        // ===== PHASE 1 SETUP =====
        // Set FPS to 300 (not -1, because -1 defaults to 30 on some Android devices)
        Application.targetFrameRate = 300;

        // Disable Vuforia completely
        vuforiaBehaviour = FindFirstObjectByType<VuforiaBehaviour>();
        if (vuforiaBehaviour != null)
        {
            arCamera = vuforiaBehaviour.GetComponent<Camera>();
            vuforiaBehaviour.enabled = false;
            if (arCamera != null)
                arCamera.enabled = false;
        }

        // Create a simple benchmark camera (no phone camera, just rendering)
        GameObject camObj = new GameObject("BenchmarkCamera");
        benchmarkCamera = camObj.AddComponent<Camera>();
        benchmarkCamera.clearFlags = CameraClearFlags.SolidColor;
        benchmarkCamera.backgroundColor = new Color(0.05f, 0.05f, 0.1f);
        benchmarkCamera.nearClipPlane = 0.01f;
        benchmarkCamera.farClipPlane = 50f;
        benchmarkCamera.fieldOfView = 60f;
        camObj.transform.position = new Vector3(0, 0.2f, -0.4f);
        camObj.transform.LookAt(Vector3.zero);

        // Create a small spinning cube for visual confirmation that rendering is active
        spinningCube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        spinningCube.name = "BenchmarkCube";
        spinningCube.transform.position = Vector3.zero;
        spinningCube.transform.localScale = new Vector3(0.08f, 0.08f, 0.08f);

        // Remove collider (no physics)
        var col = spinningCube.GetComponent<Collider>();
        if (col != null) Destroy(col);

        // Bright material
        Material mat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
        mat.color = Color.green;
        spinningCube.GetComponent<Renderer>().material = mat;
    }

    void Update()
    {
        sessionTime += Time.unscaledDeltaTime;

        // Spin the benchmark cube so user knows rendering is working
        if (spinningCube != null)
            spinningCube.transform.Rotate(0, 180f * Time.deltaTime, 45f * Time.deltaTime);

        // ---- FPS ----
        frameCount++;
        fpsTimer += Time.unscaledDeltaTime;
        if (fpsTimer >= 0.5f)
        {
            currentFps = frameCount / fpsTimer;
            frameCount = 0;
            fpsTimer = 0f;

            if (isBenchmarkPhase)
            {
                if (currentFps > maxFpsRecorded) maxFpsRecorded = currentFps;
                if (currentFps < minFpsRecorded && currentFps > 1) minFpsRecorded = currentFps;
                fpsSum += currentFps;
                fpsFrameCount++;
            }
            else
            {
                if (currentFps > prodMaxFps) prodMaxFps = currentFps;
                if (currentFps < prodMinFps && currentFps > 1) prodMinFps = currentFps;
                prodFpsSum += currentFps;
                prodFpsCount++;
            }
        }

        // ---- Temperature (every 2 sec) ----
        tempUpdateTimer += Time.unscaledDeltaTime;
        if (tempUpdateTimer >= 2f)
        {
            tempUpdateTimer = 0f;
            batteryTempCelsius = GetBatteryTemperature();
            if (batteryTempCelsius > maxBatteryTemp) maxBatteryTemp = batteryTempCelsius;

            if (batteryTempCelsius < 35f) thermalWarning = "Cool";
            else if (batteryTempCelsius < 38f) thermalWarning = "Normal";
            else if (batteryTempCelsius < 41f) thermalWarning = "WARM";
            else if (batteryTempCelsius < 44f) thermalWarning = "HOT!";
            else thermalWarning = "CRITICAL!";
        }

        // ---- Battery & Memory ----
        currentBatteryLevel = SystemInfo.batteryLevel;
        allocatedMemoryMB = Profiler.GetTotalAllocatedMemoryLong() / (1024f * 1024f);
        reservedMemoryMB = Profiler.GetTotalReservedMemoryLong() / (1024f * 1024f);
        cpuFrameTimeMs = Time.unscaledDeltaTime * 1000f;

        // ---- Phase switch ----
        if (isBenchmarkPhase)
        {
            timer += Time.unscaledDeltaTime;
            if (timer >= uncappedDurationSeconds)
                SwitchToAR();
        }
    }

    private void SwitchToAR()
    {
        // Save Phase 1 results
        avgFpsDuringBenchmark = fpsSum / Mathf.Max(fpsFrameCount, 1);
        phase1EndTemp = batteryTempCelsius;

        // Cleanup benchmark objects
        if (spinningCube != null) Destroy(spinningCube);
        if (benchmarkCamera != null) Destroy(benchmarkCamera.gameObject);

        // Enable Vuforia + AR camera
        if (vuforiaBehaviour != null)
        {
            vuforiaBehaviour.enabled = true;
            if (arCamera != null)
                arCamera.enabled = true;
        }

        // Cap to production frame rate
        Application.targetFrameRate = targetFPS;
        isBenchmarkPhase = false;
    }

    // =============================================
    // BATTERY TEMPERATURE (Android)
    // =============================================
    private float GetBatteryTemperature()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
            using (var intentFilter = new AndroidJavaObject("android.content.IntentFilter",
                       "android.intent.action.BATTERY_CHANGED"))
            using (var batteryIntent = activity.Call<AndroidJavaObject>("registerReceiver",
                       (AndroidJavaObject)null, intentFilter))
            {
                int temp = batteryIntent.Call<int>("getIntExtra", "temperature", 0);
                return temp / 10f;
            }
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("Temp read error: " + e.Message);
            return -1f;
        }
#else
        return 32f + (sessionTime * 0.05f);
#endif
    }

    // =============================================
    // GUI DISPLAY
    // =============================================
    void OnGUI()
    {
        if (!Debug.isDebugBuild) return;

        InitStyles();

        if (isBenchmarkPhase)
            DrawPhase1();
        else
            DrawPhase2();
    }

    private void InitStyles()
    {
        if (stylesReady) return;

        bgTexture = new Texture2D(1, 1);
        bgTexture.SetPixel(0, 0, new Color(0, 0, 0, 0.85f));
        bgTexture.Apply();

        headerStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 28,
            fontStyle = FontStyle.Bold
        };

        subHeaderStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 21,
            fontStyle = FontStyle.Bold
        };

        normalStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 22
        };

        smallStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 19
        };

        stylesReady = true;
    }

    // =============================================
    // PHASE 1: GPU MAX CAPABILITY
    // =============================================
    private void DrawPhase1()
    {
        float w = 580;
        float h = 600;
        float x = 10;
        float y = 10;
        float px = x + 14;
        float cw = w - 28;
        float cy = y + 12;

        GUI.DrawTexture(new Rect(x, y, w, h), bgTexture);

        // Header
        headerStyle.normal.textColor = Color.yellow;
        GUI.Label(new Rect(px, cy, cw, 32), "PHASE 1: DEVICE MAX CAPABILITY", headerStyle);
        cy += 36;

        normalStyle.normal.textColor = Color.yellow;
        float remaining = uncappedDurationSeconds - timer;
        GUI.Label(new Rect(px, cy, cw, 26), "No Vuforia | No Camera | No FPS Limit", normalStyle);
        cy += 28;
        GUI.Label(new Rect(px, cy, cw, 26), "Time remaining: " + remaining.ToString("F0") + " seconds", normalStyle);
        cy += 36;

        // GPU RENDERING SPEED
        SectionHeader(ref cy, px, cw, "GPU RENDERING SPEED");
        cy += 6;

        Color fpsColor = currentFps >= 60 ? Color.green : currentFps >= 30 ? Color.cyan : Color.red;
        DataLine(ref cy, px, cw, "Current:", currentFps.ToString("F0") + " fps", fpsColor);
        DataLine(ref cy, px, cw, "Highest:", maxFpsRecorded.ToString("F0") + " fps", Color.green);
        DataLine(ref cy, px, cw, "Lowest:", (minFpsRecorded == float.MaxValue ? 0 : minFpsRecorded).ToString("F0") + " fps", Color.white);
        float avg = fpsFrameCount > 0 ? fpsSum / fpsFrameCount : 0;
        DataLine(ref cy, px, cw, "Average:", avg.ToString("F0") + " fps", Color.cyan);
        DataLine(ref cy, px, cw, "Frame time:", cpuFrameTimeMs.ToString("F1") + " ms", Color.white);
        cy += 12;

        // TEMPERATURE
        SectionHeader(ref cy, px, cw, "TEMPERATURE");
        cy += 6;

        Color tc = batteryTempCelsius < 35f ? Color.green : batteryTempCelsius < 41f ? Color.yellow : Color.red;
        DataLine(ref cy, px, cw, "Battery Temp:", batteryTempCelsius.ToString("F1") + " C  [" + thermalWarning + "]", tc);
        DataLine(ref cy, px, cw, "Rise:", "+" + (batteryTempCelsius - startBatteryTemp).ToString("F1") + " C from start", Color.white);
        cy += 12;

        // MEMORY
        SectionHeader(ref cy, px, cw, "MEMORY");
        cy += 6;

        Color mc = allocatedMemoryMB < 300 ? Color.green : Color.yellow;
        DataLine(ref cy, px, cw, "Allocated:", allocatedMemoryMB.ToString("F0") + " MB", mc);
        DataLine(ref cy, px, cw, "Reserved:", reservedMemoryMB.ToString("F0") + " MB", Color.white);
        cy += 12;

        // BATTERY
        SectionHeader(ref cy, px, cw, "BATTERY");
        cy += 6;

        float used = (startBatteryLevel - currentBatteryLevel) * 100f;
        DataLine(ref cy, px, cw, "Level:", (currentBatteryLevel * 100).ToString("F0") + "%", Color.green);
        DataLine(ref cy, px, cw, "Used:", used.ToString("F1") + "%", Color.white);
        cy += 12;

        // DEVICE
        SectionHeader(ref cy, px, cw, "DEVICE");
        cy += 6;

        smallStyle.normal.textColor = Color.cyan;
        GUI.Label(new Rect(px, cy, cw, 22), SystemInfo.deviceModel, smallStyle);
        cy += 24;
        GUI.Label(new Rect(px, cy, cw, 22), "GPU: " + SystemInfo.graphicsDeviceName, smallStyle);
        cy += 24;
        GUI.Label(new Rect(px, cy, cw, 22), "CPU: " + SystemInfo.processorType, smallStyle);
        cy += 24;
        GUI.Label(new Rect(px, cy, cw, 22), "RAM: " + SystemInfo.systemMemorySize + " MB", smallStyle);
    }

    // =============================================
    // PHASE 2: AR MODE
    // =============================================
    private void DrawPhase2()
    {
        float w = 580;
        float h = 780;
        float x = 10;
        float y = 10;
        float px = x + 14;
        float cw = w - 28;
        float cy = y + 12;

        GUI.DrawTexture(new Rect(x, y, w, h), bgTexture);

        float arTime = sessionTime - uncappedDurationSeconds;

        // Header
        headerStyle.normal.textColor = Color.green;
        GUI.Label(new Rect(px, cy, cw, 32), "PHASE 2: AR MODE (Vuforia ON)", headerStyle);
        cy += 36;

        normalStyle.normal.textColor = Color.green;
        GUI.Label(new Rect(px, cy, cw, 26), "Capped " + targetFPS + " fps | AR running: " + (arTime / 60f).ToString("F1") + " min", normalStyle);
        cy += 36;

        // DEVICE CAPABILITY (from Phase 1)
        SectionHeader(ref cy, px, cw, "DEVICE CAPABILITY (Phase 1 Results)");
        cy += 6;

        DataLine(ref cy, px, cw, "Max FPS:", maxFpsRecorded.ToString("F0"), Color.cyan);
        DataLine(ref cy, px, cw, "Avg FPS:", avgFpsDuringBenchmark.ToString("F0"), Color.cyan);
        DataLine(ref cy, px, cw, "Min FPS:", (minFpsRecorded == float.MaxValue ? 0 : minFpsRecorded).ToString("F0"), Color.white);

        // Headroom
        float headroom = avgFpsDuringBenchmark - targetFPS;
        Color hc;
        string ht;
        if (headroom > 30) { hc = Color.green; ht = "EXCELLENT"; }
        else if (headroom > 15) { hc = Color.green; ht = "GOOD"; }
        else if (headroom > 5) { hc = Color.yellow; ht = "TIGHT"; }
        else { hc = Color.red; ht = "INSUFFICIENT"; }
        DataLine(ref cy, px, cw, "Headroom:", "+" + headroom.ToString("F0") + " fps  [" + ht + "]", hc);

        // Vuforia cost
        float arAvg = prodFpsCount > 0 ? prodFpsSum / prodFpsCount : targetFPS;
        float vuforiaCost = avgFpsDuringBenchmark - arAvg;
        DataLine(ref cy, px, cw, "Vuforia cost:", "~" + vuforiaCost.ToString("F0") + " fps reduction", Color.white);
        cy += 12;

        // AR PERFORMANCE
        SectionHeader(ref cy, px, cw, "AR PERFORMANCE (Live)");
        cy += 6;

        Color fc = currentFps >= 28 ? Color.green : currentFps >= 20 ? Color.yellow : Color.red;
        DataLine(ref cy, px, cw, "Current:", currentFps.ToString("F0") + " fps", fc);
        DataLine(ref cy, px, cw, "AR Avg:", (prodFpsCount > 0 ? prodFpsSum / prodFpsCount : 0).ToString("F0") + " fps", Color.white);
        DataLine(ref cy, px, cw, "AR Min:", (prodMinFps == float.MaxValue ? 0 : prodMinFps).ToString("F0") + " fps", Color.white);
        DataLine(ref cy, px, cw, "AR Max:", prodMaxFps.ToString("F0") + " fps", Color.white);
        DataLine(ref cy, px, cw, "Frame time:", cpuFrameTimeMs.ToString("F1") + " ms", Color.white);
        cy += 12;

        // TEMPERATURE
        SectionHeader(ref cy, px, cw, "TEMPERATURE");
        cy += 6;

        Color tc = batteryTempCelsius < 35f ? Color.green : batteryTempCelsius < 41f ? Color.yellow : Color.red;
        DataLine(ref cy, px, cw, "Current:", batteryTempCelsius.ToString("F1") + " C  [" + thermalWarning + "]", tc);
        DataLine(ref cy, px, cw, "Start:", startBatteryTemp.ToString("F1") + " C", Color.white);
        DataLine(ref cy, px, cw, "Phase 1 End:", phase1EndTemp.ToString("F1") + " C", Color.white);
        DataLine(ref cy, px, cw, "Total Rise:", "+" + (batteryTempCelsius - startBatteryTemp).ToString("F1") + " C",
            (batteryTempCelsius - startBatteryTemp) > 8 ? Color.red : Color.white);
        DataLine(ref cy, px, cw, "Max Reached:", maxBatteryTemp.ToString("F1") + " C", maxBatteryTemp >= 41f ? Color.red : Color.white);
        cy += 12;

        // BATTERY
        SectionHeader(ref cy, px, cw, "BATTERY");
        cy += 6;

        float used = (startBatteryLevel - currentBatteryLevel) * 100f;
        float rate = sessionTime > 60f ? (used / (sessionTime / 60f)) : 0f;
        float perHour = rate * 60f;

        DataLine(ref cy, px, cw, "Level:", (currentBatteryLevel * 100).ToString("F0") + "%",
            currentBatteryLevel < 0.2f ? Color.red : Color.green);
        DataLine(ref cy, px, cw, "Used:", used.ToString("F1") + "%", Color.white);
        DataLine(ref cy, px, cw, "Drain rate:", rate.ToString("F2") + "%/min  (" + perHour.ToString("F1") + "%/hr)",
            rate > 0.5f ? Color.red : Color.white);
        cy += 12;

        // MEMORY
        SectionHeader(ref cy, px, cw, "MEMORY");
        cy += 6;

        Color mc = allocatedMemoryMB < 300 ? Color.green : allocatedMemoryMB < 500 ? Color.yellow : Color.red;
        DataLine(ref cy, px, cw, "Allocated:", allocatedMemoryMB.ToString("F0") + " MB", mc);
        DataLine(ref cy, px, cw, "Reserved:", reservedMemoryMB.ToString("F0") + " MB", Color.white);
        cy += 12;

        // DEVICE
        SectionHeader(ref cy, px, cw, "DEVICE");
        cy += 6;

        smallStyle.normal.textColor = Color.cyan;
        GUI.Label(new Rect(px, cy, cw, 22), SystemInfo.deviceModel + " | " + SystemInfo.graphicsDeviceName, smallStyle);
        cy += 24;
        GUI.Label(new Rect(px, cy, cw, 22), "RAM: " + SystemInfo.systemMemorySize + " MB | Session: " + (sessionTime / 60f).ToString("F1") + " min", smallStyle);
    }

    // =============================================
    // UI HELPERS
    // =============================================
    private void SectionHeader(ref float cy, float px, float cw, string title)
    {
        subHeaderStyle.normal.textColor = new Color(1f, 0.8f, 0.2f);
        GUI.Label(new Rect(px, cy, cw, 24), "-- " + title + " --", subHeaderStyle);
        cy += 26;
    }

    private void DataLine(ref float cy, float px, float cw, string label, string value, Color valueColor)
    {
        // Label in grey
        normalStyle.normal.textColor = new Color(0.7f, 0.7f, 0.7f);
        GUI.Label(new Rect(px, cy, 180, 26), label, normalStyle);

        // Value in color
        normalStyle.normal.textColor = valueColor;
        GUI.Label(new Rect(px + 185, cy, cw - 185, 26), value, normalStyle);

        cy += 28;
    }
}