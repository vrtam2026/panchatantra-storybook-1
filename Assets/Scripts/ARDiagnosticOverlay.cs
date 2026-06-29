using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// AR Addressable Diagnostics overlay.
/// Attach to the "AR_Debug_Stats" GameObject.
/// Tap the [i] button (top-right) to toggle the panel.
/// Disable or remove AR_Debug_Stats for production builds.
/// </summary>
public class ARDiagnosticOverlay : MonoBehaviour
{
    [Header("Auto-found if left empty")]
    [SerializeField] private ARAddressableAudioService audioService;

    [Header("Settings")]
    [SerializeField] private float refreshRate = 0.5f;

    // ── Runtime refs ──────────────────────────────────────────────────────────
    private Canvas      _canvas;
    private GameObject  _panel;
    private Text        _bodyText;
    private Text        _toggleLabel;
    private bool        _visible;
    private CustomARHandler _handler;

    // ── Colors ────────────────────────────────────────────────────────────────
    static readonly Color CHeader   = new Color(0.40f, 0.90f, 1.00f);
    static readonly Color COk       = new Color(0.20f, 1.00f, 0.40f);
    static readonly Color CBusy     = new Color(1.00f, 0.85f, 0.10f);
    static readonly Color CNotLoad  = new Color(0.55f, 0.55f, 0.55f);
    static readonly Color CError    = new Color(1.00f, 0.30f, 0.30f);
    static readonly Color CWhite    = Color.white;
    static readonly Color CDim      = new Color(0.50f, 0.50f, 0.50f);

    // ─────────────────────────────────────────────────────────────────────────

    void Awake()
    {
        if (audioService == null)
            audioService = FindFirstObjectByType<ARAddressableAudioService>();
        _handler = FindFirstObjectByType<CustomARHandler>();
        BuildUI();
        SetVisible(false);
        StartCoroutine(RefreshLoop());
    }

    void Update()
    {
#if ENABLE_INPUT_SYSTEM
        if (UnityEngine.InputSystem.Keyboard.current != null &&
            UnityEngine.InputSystem.Keyboard.current.dKey.wasPressedThisFrame)
            SetVisible(!_visible);
#else
        if (Input.GetKeyDown(KeyCode.D)) SetVisible(!_visible);
#endif
    }

    void SetVisible(bool on)
    {
        _visible = on;
        _panel.SetActive(on);
        _toggleLabel.text = on ? "✕" : "i";
    }

    // ─────────────────────────────────────────────────────────────────────────
    IEnumerator RefreshLoop()
    {
        var wait = new WaitForSeconds(refreshRate);
        while (true)
        {
            if (_visible)
            {
                if (_handler == null)
                    _handler = FindFirstObjectByType<CustomARHandler>();
                Refresh();
            }
            yield return wait;
        }
    }

    void Refresh()
    {
        var sb = new StringBuilder();

        string lang   = ARGlobalLanguage.GetCurrentLanguage();
        string pageId = "";
        string prefabStatus = "None";
        string addressableKey = "";

        if (_handler != null)
        {
            var info    = _handler.GetDiagnosticInfo();
            pageId      = info.pageId;
            prefabStatus = info.prefabStatus;
            addressableKey = info.addressableKey;
        }

        // ════════════════════════════════════════════════════════════════════
        // 1. CURRENT SESSION
        // ════════════════════════════════════════════════════════════════════
        Line(sb, "══ CURRENT SESSION ══════════════════════════", CHeader, bold: true);
        KV(sb, "Language",    lang,   CHeader);
        KV(sb, "Page ID",     string.IsNullOrEmpty(pageId) ? "(scanning...)" : pageId, CWhite);

        Color pc = prefabStatus == "Loaded" ? COk : prefabStatus == "Downloading" ? CBusy : CNotLoad;
        KV(sb, "Prefab",      prefabStatus + (string.IsNullOrEmpty(addressableKey) ? "" : "  →  " + addressableKey), pc);
        sb.AppendLine();

        // ════════════════════════════════════════════════════════════════════
        // 2. AUDIO PACK — current page
        // ════════════════════════════════════════════════════════════════════
        Line(sb, "══ CURRENT PAGE AUDIO ═══════════════════════", CHeader, bold: true);
        if (audioService == null)
        {
            Line(sb, "  ✖  AudioService not found!", CError);
        }
        else if (string.IsNullOrEmpty(pageId))
        {
            Line(sb, "  —  Waiting for a page to be scanned", CDim);
        }
        else
        {
            var cache = audioService.GetCacheInfo();
            AppendPackLine(sb, lang,                                 pageId, cache);
            AppendPackLine(sb, lang == "English" ? "Hindi" : "English", pageId, cache);
        }
        sb.AppendLine();

        // ════════════════════════════════════════════════════════════════════
        // 3. DOWNLOADING NOW
        // ════════════════════════════════════════════════════════════════════
        Line(sb, "══ DOWNLOADING NOW ══════════════════════════", CHeader, bold: true);

        List<ARAddressableAudioService.PackStatus> allPacks = null;
        int cachedCount = 0, downloadingCount = 0, notLoadedCount = 0, failedCount = 0;

        if (audioService != null)
        {
            allPacks = audioService.GetFullStatus();
            foreach (var p in allPacks)
            {
                switch (p.State)
                {
                    case ARAddressableAudioService.PackState.Cached:      cachedCount++;      break;
                    case ARAddressableAudioService.PackState.Downloading: downloadingCount++; break;
                    case ARAddressableAudioService.PackState.NotLoaded:   notLoadedCount++;   break;
                    case ARAddressableAudioService.PackState.Failed:      failedCount++;      break;
                }
            }

            if (downloadingCount == 0)
            {
                Line(sb, "  —  Nothing downloading right now", CDim);
            }
            else
            {
                foreach (var p in allPacks)
                {
                    if (p.State != ARAddressableAudioService.PackState.Downloading) continue;
                    float pct = p.Progress * 100f;
                    string bar = Bar(p.Progress, 14);
                    sb.AppendLine(Col($"  ⟳  [{bar}] {pct:0}%  {p.Language} / {p.PageId}", CBusy));
                }
            }
        }
        else
        {
            Line(sb, "  ✖  AudioService not found!", CError);
        }
        sb.AppendLine();

        // ════════════════════════════════════════════════════════════════════
        // 4. SUMMARY COUNTS  — big clear numbers
        // ════════════════════════════════════════════════════════════════════
        Line(sb, "══ ALL ADDRESSABLES ══════════════════════════", CHeader, bold: true);
        if (allPacks != null)
        {
            sb.AppendLine(Col($"  Total packs in catalog :  {allPacks.Count}", CWhite));
            sb.AppendLine(Col($"  ✓  IN MEMORY           :  {cachedCount}", COk));
            sb.AppendLine(Col($"  ⟳  Downloading now     :  {downloadingCount}", downloadingCount > 0 ? CBusy : CDim));
            sb.AppendLine(Col($"  ○  Not loaded yet      :  {notLoadedCount}", CNotLoad));
            if (failedCount > 0)
                sb.AppendLine(Col($"  ✖  Failed              :  {failedCount}", CError));
        }
        sb.AppendLine();

        // ════════════════════════════════════════════════════════════════════
        // 5. IN MEMORY LIST (up to 10 then count)
        // ════════════════════════════════════════════════════════════════════
        Line(sb, "══ IN MEMORY ═════════════════════════════════", CHeader, bold: true);
        if (allPacks == null || cachedCount == 0)
        {
            Line(sb, "  — nothing cached yet", CDim);
        }
        else
        {
            int shown = 0;
            foreach (var p in allPacks)
            {
                if (p.State != ARAddressableAudioService.PackState.Cached) continue;
                if (shown < 10)
                    sb.AppendLine(Col($"  ✓  {p.Language}  /  {p.PageId}", COk));
                shown++;
            }
            if (shown > 10)
                sb.AppendLine(Col($"  ... +{shown - 10} more", CDim));
        }
        sb.AppendLine();

        // ════════════════════════════════════════════════════════════════════
        // 6. NOT LOADED — just count, no list (would be 90+ lines)
        // ════════════════════════════════════════════════════════════════════
        Line(sb, "══ NOT YET LOADED ════════════════════════════", CHeader, bold: true);
        if (allPacks == null || notLoadedCount == 0)
            Line(sb, "  — all packs are in memory or downloading", COk);
        else
            Line(sb, $"  {notLoadedCount} packs not downloaded  (will load on demand when page is scanned)", CNotLoad);
        sb.AppendLine();

        // ════════════════════════════════════════════════════════════════════
        // 7. SYSTEM
        // ════════════════════════════════════════════════════════════════════
        Line(sb, "══ SYSTEM ════════════════════════════════════", CHeader, bold: true);
        float fps = 1f / Mathf.Max(Time.deltaTime, 0.0001f);
        float mem = System.GC.GetTotalMemory(false) / 1048576f;
        KV(sb, "FPS",      $"{fps:0}", CWhite);
        KV(sb, "Mem (MB)", $"{mem:0.0}", CWhite);
        KV(sb, "Time",     System.DateTime.Now.ToString("HH:mm:ss"), CDim);

        _bodyText.text = sb.ToString();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    void AppendPackLine(StringBuilder sb, string language, string pageId,
                        ARAddressableAudioService.CacheInfo cache)
    {
        string key = $"{language.ToLower()}:{pageId.ToLower()}";
        var dl = cache.Downloading.Find(d => d.Key == key);
        if (dl != null)
        {
            string bar = Bar(dl.Progress, 10);
            sb.AppendLine(Col($"  ⟳  [{bar}] {dl.Progress * 100f:0}%  {language}", CBusy));
        }
        else if (cache.Cached.Contains(key))
            sb.AppendLine(Col($"  ✓  IN MEMORY     {language}", COk));
        else
            sb.AppendLine(Col($"  ○  Not loaded    {language}", CNotLoad));
    }

    static string Bar(float t, int w)
    {
        int f = Mathf.RoundToInt(Mathf.Clamp01(t) * w);
        return new string('█', f) + new string('░', w - f);
    }

    static void Line(StringBuilder sb, string txt, Color c, bool bold = false)
    {
        string s = Col(txt, c);
        sb.AppendLine(bold ? $"<b>{s}</b>" : s);
    }

    static void KV(StringBuilder sb, string label, string val, Color vc)
        => sb.AppendLine($"  <b>{label}:</b>  {Col(val, vc)}");

    static string Col(string txt, Color c)
        => $"<color=#{ColorUtility.ToHtmlStringRGB(c)}>{txt}</color>";

    // ─────────────────────────────────────────────────────────────────────────
    // UI Builder
    // ─────────────────────────────────────────────────────────────────────────

    void BuildUI()
    {
        // Root canvas — sorting order 9999 so it's always on top
        var cgo = new GameObject("ARDiag_Canvas");
        DontDestroyOnLoad(cgo);
        _canvas = cgo.AddComponent<Canvas>();
        _canvas.renderMode   = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = 9999;
        var scaler = cgo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode         = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1080, 1920);
        scaler.matchWidthOrHeight  = 0.5f;
        cgo.AddComponent<GraphicRaycaster>();

        // ── Always-visible [i] toggle button — top-right corner ──────────────
        var btn = MakeRect("DiagBtn", cgo.transform);
        Anchor(btn, new Vector2(1,1), new Vector2(1,1), new Vector2(1,1));
        btn.sizeDelta      = new Vector2(90, 90);
        btn.anchoredPosition = new Vector2(-12, -12);
        var btnImg = btn.gameObject.AddComponent<Image>();
        btnImg.color = new Color(0.08f, 0.35f, 0.55f, 0.95f);
        var b = btn.gameObject.AddComponent<Button>();
        b.targetGraphic = btnImg;
        var bc = b.colors;
        bc.highlightedColor = new Color(0.2f, 0.6f, 0.85f);
        bc.pressedColor     = new Color(0.04f, 0.18f, 0.30f);
        b.colors = bc;
        b.onClick.AddListener(() => SetVisible(!_visible));

        var lblRect = MakeRect("BtnLbl", btn);
        FillRect(lblRect, Vector2.zero, Vector2.zero);
        _toggleLabel = lblRect.gameObject.AddComponent<Text>();
        _toggleLabel.font      = BuiltinFont();
        _toggleLabel.text      = "i";
        _toggleLabel.fontSize  = 42;
        _toggleLabel.fontStyle = FontStyle.Bold;
        _toggleLabel.color     = Color.white;
        _toggleLabel.alignment = TextAnchor.MiddleCenter;

        // ── Main panel — left 55 % of screen, full height ─────────────────────
        _panel = new GameObject("DiagPanel");
        _panel.transform.SetParent(cgo.transform, false);
        var pr = _panel.AddComponent<RectTransform>();
        pr.anchorMin     = Vector2.zero;
        pr.anchorMax     = new Vector2(0.58f, 1f);
        pr.offsetMin     = new Vector2(8, 8);
        pr.offsetMax     = new Vector2(-4, -8);
        _panel.AddComponent<Image>().color = new Color(0f, 0.02f, 0.07f, 0.94f);

        // Header bar
        var hdr = MakeRect("Header", pr);
        Anchor(hdr, new Vector2(0,1), new Vector2(1,1), new Vector2(0.5f,1));
        hdr.sizeDelta = new Vector2(0, 56);
        hdr.gameObject.AddComponent<Image>().color = new Color(0.04f, 0.24f, 0.44f, 1f);

        var htRect = MakeRect("HdrTxt", hdr);
        FillRect(htRect, new Vector2(12, 0), new Vector2(-60, 0));
        var ht = htRect.gameObject.AddComponent<Text>();
        ht.font = BuiltinFont(); ht.fontSize = 22; ht.fontStyle = FontStyle.Bold;
        ht.color = Color.white; ht.alignment = TextAnchor.MiddleLeft;
        ht.text = "  AR DIAGNOSTICS";

        // Close (✕) button in header
        var xr = MakeRect("CloseBtn", hdr);
        Anchor(xr, new Vector2(1,0), new Vector2(1,1), new Vector2(1,0.5f));
        xr.sizeDelta = new Vector2(56, 0);
        var xi = xr.gameObject.AddComponent<Image>(); xi.color = new Color(0.75f, 0.12f, 0.12f);
        var xb = xr.gameObject.AddComponent<Button>(); xb.targetGraphic = xi;
        xb.onClick.AddListener(() => SetVisible(false));
        var xtRect = MakeRect("X", xr);
        FillRect(xtRect, Vector2.zero, Vector2.zero);
        var xt = xtRect.gameObject.AddComponent<Text>();
        xt.font = BuiltinFont(); xt.text = "✕"; xt.fontSize = 28;
        xt.color = Color.white; xt.alignment = TextAnchor.MiddleCenter;

        // Body text — placed directly in panel, no ScrollRect (avoids sizing loop)
        var bodyGO = new GameObject("Body");
        bodyGO.transform.SetParent(_panel.transform, false);
        var br = bodyGO.AddComponent<RectTransform>();
        br.anchorMin     = Vector2.zero;
        br.anchorMax     = Vector2.one;
        br.offsetMin     = new Vector2(10, 10);
        br.offsetMax     = new Vector2(-10, -62);  // 62 = 56px header + 6px gap
        _bodyText = bodyGO.AddComponent<Text>();
        _bodyText.font               = BuiltinFont();
        _bodyText.fontSize           = 18;
        _bodyText.color              = Color.white;
        _bodyText.supportRichText    = true;
        _bodyText.horizontalOverflow = HorizontalWrapMode.Wrap;
        _bodyText.verticalOverflow   = VerticalWrapMode.Overflow;
        _bodyText.lineSpacing        = 1.3f;
        _bodyText.alignment          = TextAnchor.UpperLeft;
        _bodyText.text               = Col("Loading diagnostics...", CDim);
    }

    // ── Rect helpers ──────────────────────────────────────────────────────────

    static RectTransform MakeRect(string name, Transform parent)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        return go.AddComponent<RectTransform>();
    }

    static RectTransform MakeRect(string name, RectTransform parent)
        => MakeRect(name, parent.transform);

    static void FillRect(RectTransform r, Vector2 oMin, Vector2 oMax)
    {
        r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one;
        r.offsetMin = oMin;        r.offsetMax = oMax;
    }

    static void Anchor(RectTransform r, Vector2 min, Vector2 max, Vector2 pivot)
    {
        r.anchorMin = min; r.anchorMax = max; r.pivot = pivot;
    }

    static Font BuiltinFont() => Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
}
