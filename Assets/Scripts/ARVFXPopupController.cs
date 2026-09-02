using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

[DefaultExecutionOrder(10000)]
public class ARVFXPopupController : MonoBehaviour
{
    public static event System.Action<ARVFXPopupController> OnRevealComplete;

    public enum PopupStyle
    {
        BouncyPop,
        JellyPop,
        HeroPop,
        SwirlPop,
        ZoomBlastPop
    }

    [System.Serializable]
    public class ModelRevealItem
    {
        [Header("Model")]
        public Transform modelTransform;

        [Header("Optional Visual Root")]
        [Tooltip("Leave empty if you are not using a separate reveal anchor.")]
        public Transform visualContentRoot;

        [Header("VFX Circle For This Model")]
        public Transform vfxWrapper;

        [Header("Timing")]
        [Range(0f, 6f)]
        public float startDelay = 0f;

        [Range(0.1f, 6f)]
        public float vfxIntroSeconds = 0.5f;

        [Range(0.1f, 6f)]
        public float modelPopupSeconds = 0.8f;

        [Range(0.1f, 6f)]
        public float vfxOutroSeconds = 0.25f;

        [Header("Popup")]
        public PopupStyle style = PopupStyle.HeroPop;

        [Header("Safety")]
        public bool lockModelDuringReveal = true;
    }

    [System.Serializable]
    public class GlobalSettings
    {
        [Header("Reveal Mode")]
        public bool sameTimeVfxRevealForAllModels = false;

        [Header("Fallback VFX")]
        public Transform fallbackVfxWrapper;

        [Header("VFX Circle Spin")]
        public bool enableSpin = true;

        [Range(0f, 720f)]
        public float spinSpeed = 120f;

        [Header("Replay Safety")]
        public bool disableAnimatorsDuringReveal = true;
        public bool enableAnimatorsAfterReveal = true;
        public bool disablePageMovementScriptsDuringReveal = true;
    }

    [System.Serializable]
    public class AudioSettings
    {
        [Header("VFX Reveal Sound")]
        public AudioClip vfxRevealSound;

        [Range(0f, 1f)]
        public float vfxRevealVolume = 0.85f;

        [Header("Popup Sound")]
        public AudioClip popupSound;

        [Range(0f, 1f)]
        public float popupVolume = 0.85f;

        [Header("Audio Mode")]
        [Range(0f, 1f)]
        public float spatialBlend = 0f;
    }

    private class SavedTransformState
    {
        public Vector3 localPosition;
        public Quaternion localRotation;
        public Vector3 localScale;
    }

    private class SavedVFXState
    {
        public Vector3 localPosition;
        public Quaternion localRotation;
        public Vector3 localScale;
        public CanvasGroup canvasGroup;
        public ParticleSystem[] particles;
        public List<MaterialAlphaState> materials = new List<MaterialAlphaState>();
    }

    private class RuntimeModel
    {
        public ModelRevealItem item;

        public Transform model;
        public Transform visualRoot;
        public Transform vfx;

        public Vector3 modelHomeLocalPosition;
        public Quaternion modelHomeLocalRotation;
        public Vector3 modelHomeLocalScale;

        public Vector3 visualHomeLocalPosition;
        public Quaternion visualHomeLocalRotation;
        public Vector3 visualHomeLocalScale;

        public Vector3 vfxHomeLocalPosition;
        public Quaternion vfxHomeLocalRotation;
        public Vector3 vfxHomeLocalScale;

        public Renderer[] visualRenderers;
        public Animator[] animators;

        public CanvasGroup vfxCanvasGroup;
        public ParticleSystem[] vfxParticles;
        public List<MaterialAlphaState> vfxMaterials = new List<MaterialAlphaState>();

        public bool hasFinished;
        public bool popupPlaying;
        public bool lockToHome;

        public Transform originalParent;
        public int originalSiblingIndex;

        public GameObject popupAnchorObject;
        public Transform popupAnchor;
        public Vector3 popupAnchorHomeLocalPosition;
        public Quaternion popupAnchorHomeLocalRotation;
        public Vector3 popupAnchorHomeLocalScale;

        public Vector3 anchoredModelLocalPosition;
        public Quaternion anchoredModelLocalRotation;
        public Vector3 anchoredModelLocalScale;
    }

    private struct PopupPose
    {
        public Vector3 scaleMultiplier;
        public Vector3 positionOffset;
        public Vector3 rotationOffset;
    }

    private struct MaterialAlphaState
    {
        public Material material;
        public int colorProperty;
        public Color originalColor;
    }

    [Header("Multi Model Reveal")]
    public List<ModelRevealItem> revealModels = new List<ModelRevealItem>();

    [Header("Global VFX Settings")]
    public GlobalSettings globalSettings = new GlobalSettings();

    [Header("Audio")]
    public AudioSettings audioSettings = new AudioSettings();

    [Header("Startup")]
    [Tooltip("For AR story pages keep this OFF. ARTrackedPageNode starts the reveal at the correct time.")]
    [SerializeField] private bool playAutomaticallyOnStart = false;

    private const float ModelJumpHeight = 0.08f;

    private readonly List<RuntimeModel> _runtimeModels = new List<RuntimeModel>();
    private readonly Dictionary<Transform, SavedTransformState> _savedModelStates = new Dictionary<Transform, SavedTransformState>();
    private readonly Dictionary<Transform, SavedTransformState> _savedVisualStates = new Dictionary<Transform, SavedTransformState>();
    private readonly Dictionary<Transform, SavedVFXState> _savedVfxStates = new Dictionary<Transform, SavedVFXState>();
    private readonly List<MonoBehaviour> _disabledPageMovementScripts = new List<MonoBehaviour>();

    private AudioSource _vfxAudioSource;
    private AudioSource _popupAudioSource;

    private Coroutine _sequenceRoutine;

    private bool _paused;
    private bool _revealComplete;
    private bool _isDisabled;

    private static readonly int ColorId = Shader.PropertyToID("_Color");
    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

    public bool IsRevealComplete => _revealComplete;

    private void Awake()
    {
        SaveInitialStatesOnce();
        BuildRuntimeModels();
        CreateAudioSources();
    }

    private void Start()
    {
        // AR story pages are started by ARTrackedPageNode so the order is deterministic:
        // VFX/popup first, then voice + animator + spline together.
        if (playAutomaticallyOnStart)
            BeginReveal();
    }

    private void LateUpdate()
    {
        if (_revealComplete) return;

        for (int i = 0; i < _runtimeModels.Count; i++)
        {
            RuntimeModel runtime = _runtimeModels[i];

            if (runtime == null) continue;
            if (runtime.model == null) continue;
            if (!runtime.lockToHome) continue;
            if (!runtime.item.lockModelDuringReveal) continue;
            if (runtime.popupPlaying) continue;

            RestoreModelHome(runtime);
        }
    }

    private void OnDisable()
    {
        _isDisabled = true;
        StopRevealAudio();

        // The reveal sequence is about to be killed by StopAllCoroutines(). PrepareRevealState()
        // had already hidden every model, so without this they stay invisible forever AND
        // _revealComplete never becomes true -- which means ARTrackedPageNode never receives
        // OnRevealComplete, so the page never starts its story, its animators, or its video.
        // Put the visuals back and mark the sequence dead so ResumeReveal() knows it must
        // start a fresh one instead of merely un-pausing a coroutine that no longer exists.
        if (!_revealComplete)
        {
            for (int i = 0; i < _runtimeModels.Count; i++)
            {
                RuntimeModel runtime = _runtimeModels[i];
                if (runtime == null) continue;
                runtime.popupPlaying = false;
                SetVisualVisible(runtime, true);
            }
        }

        _sequenceRoutine = null;

        StopAllCoroutines();
        CleanupAllPopupAnchors(true);
        ReEnablePageMovementScriptsAfterReveal();
    }

    private void OnDestroy()
    {
        _isDisabled = true;
        StopRevealAudio();
        StopAllCoroutines();
        CleanupAllPopupAnchors(true);
        ReEnablePageMovementScriptsAfterReveal();
    }

    public void TriggerReplay()
    {
        //CustomARHandler.Current?.OnVFXReplayStarting();
        BeginReveal();
    }

    public void PauseReveal()
    {
        if (_revealComplete) return;

        _paused = true;

        if (_vfxAudioSource != null)
            _vfxAudioSource.Pause();

        if (_popupAudioSource != null)
            _popupAudioSource.Pause();
    }

    public void ResumeReveal()
    {
        if (_revealComplete) return;

        // A disabled/re-enabled page had its reveal coroutine destroyed in OnDisable.
        // Un-pausing cannot revive a dead coroutine, so the page would wait forever for
        // a reveal that is no longer running. Start a clean one instead.
        if (_sequenceRoutine == null && isActiveAndEnabled)
        {
            BeginReveal();
            return;
        }

        _paused = false;

        if (_vfxAudioSource != null)
            _vfxAudioSource.UnPause();

        if (_popupAudioSource != null)
            _popupAudioSource.UnPause();
    }

    private void BeginReveal()
    {
        if (!isActiveAndEnabled) return;

        _isDisabled = false;
        _revealComplete = false;

        StopAllCoroutines();
        _sequenceRoutine = null;

        StopRevealAudio();
        CleanupAllPopupAnchors(true);

        ReEnablePageMovementScriptsAfterReveal();

        BuildRuntimeModels();

        DisablePageMovementScriptsDuringReveal();

        RestoreEverythingToHome();
        PrepareRevealState();

        _sequenceRoutine = StartCoroutine(RevealSequence());
    }

    private void SaveInitialStatesOnce()
    {
        if (revealModels == null) return;

        for (int i = 0; i < revealModels.Count; i++)
        {
            ModelRevealItem item = revealModels[i];

            if (item == null) continue;

            Transform model = item.modelTransform;
            Transform visualRoot = item.visualContentRoot != null ? item.visualContentRoot : item.modelTransform;
            Transform vfx = item.vfxWrapper != null ? item.vfxWrapper : globalSettings.fallbackVfxWrapper;

            SaveTransformStateIfNeeded(model, visualRoot, _savedModelStates);
            SaveTransformStateIfNeeded(visualRoot, visualRoot, _savedVisualStates);
            SaveVFXStateIfNeeded(vfx);
        }

        SaveVFXStateIfNeeded(globalSettings.fallbackVfxWrapper);
    }

    private void SaveTransformStateIfNeeded(Transform target, Transform visualRoot, Dictionary<Transform, SavedTransformState> dictionary)
    {
        if (target == null) return;
        if (dictionary.ContainsKey(target)) return;

        dictionary.Add(target, new SavedTransformState
        {
            localPosition = target.localPosition,
            localRotation = target.localRotation,
            localScale = target.localScale
        });
    }

    private void SaveVFXStateIfNeeded(Transform vfx)
    {
        if (vfx == null) return;
        if (_savedVfxStates.ContainsKey(vfx)) return;

        SavedVFXState state = new SavedVFXState
        {
            localPosition = vfx.localPosition,
            localRotation = vfx.localRotation,
            localScale = vfx.localScale,
            canvasGroup = vfx.GetComponent<CanvasGroup>(),
            particles = vfx.GetComponentsInChildren<ParticleSystem>(true)
        };

        CacheVFXMaterials(vfx, state.materials);

        _savedVfxStates.Add(vfx, state);
    }

    private void BuildRuntimeModels()
    {
        _runtimeModels.Clear();

        if (revealModels == null || revealModels.Count == 0)
            return;

        for (int i = 0; i < revealModels.Count; i++)
        {
            ModelRevealItem item = revealModels[i];

            if (item == null) continue;
            if (item.modelTransform == null) continue;

            Transform model = item.modelTransform;
            Transform visualRoot = item.visualContentRoot != null ? item.visualContentRoot : item.modelTransform;
            Transform vfx = item.vfxWrapper != null ? item.vfxWrapper : globalSettings.fallbackVfxWrapper;

            SaveTransformStateIfNeeded(model, visualRoot, _savedModelStates);
            SaveTransformStateIfNeeded(visualRoot, visualRoot, _savedVisualStates);
            SaveVFXStateIfNeeded(vfx);

            SavedTransformState modelState = _savedModelStates[model];
            SavedTransformState visualState = _savedVisualStates[visualRoot];

            RuntimeModel runtime = new RuntimeModel
            {
                item = item,

                model = model,
                visualRoot = visualRoot,
                vfx = vfx,

                modelHomeLocalPosition = modelState.localPosition,
                modelHomeLocalRotation = modelState.localRotation,
                modelHomeLocalScale = modelState.localScale,
                visualHomeLocalPosition = visualState.localPosition,
                visualHomeLocalRotation = visualState.localRotation,
                visualHomeLocalScale = visualState.localScale,

                visualRenderers = visualRoot.GetComponentsInChildren<Renderer>(true),
                animators = visualRoot.GetComponentsInChildren<Animator>(true),

                hasFinished = false,
                popupPlaying = false,
                lockToHome = false,

                originalParent = model.parent,
                originalSiblingIndex = model.GetSiblingIndex()
            };

            if (vfx != null && _savedVfxStates.TryGetValue(vfx, out SavedVFXState vfxState))
            {
                runtime.vfxHomeLocalPosition = vfxState.localPosition;
                runtime.vfxHomeLocalRotation = vfxState.localRotation;
                runtime.vfxHomeLocalScale = vfxState.localScale;
                runtime.vfxCanvasGroup = vfxState.canvasGroup;
                runtime.vfxParticles = vfxState.particles;
                runtime.vfxMaterials = vfxState.materials;
            }

            _runtimeModels.Add(runtime);
        }

        WarnIfSharedVFXUsed();
    }

    private void WarnIfSharedVFXUsed()
    {
        for (int i = 0; i < _runtimeModels.Count; i++)
        {
            for (int j = i + 1; j < _runtimeModels.Count; j++)
            {
                if (_runtimeModels[i].vfx != null && _runtimeModels[i].vfx == _runtimeModels[j].vfx)
                {
                    Debug.LogWarning("[ARVFXPopupController] Two models are using the same VFX Wrapper. Use separate VFX objects if you want separate positions.");
                    return;
                }
            }
        }
    }

    private void RestoreEverythingToHome()
    {
        CleanupAllPopupAnchors(true);

        for (int i = 0; i < _runtimeModels.Count; i++)
        {
            RuntimeModel runtime = _runtimeModels[i];

            if (runtime == null) continue;

            RestoreModelHome(runtime);
            RestoreVisualHome(runtime);
            RestoreVFXHome(runtime);
        }
    }

    private void RestoreOnlyModelsToHome()
    {
        for (int i = 0; i < _runtimeModels.Count; i++)
        {
            RuntimeModel runtime = _runtimeModels[i];

            if (runtime == null) continue;

            RestoreModelHome(runtime);
            RestoreVisualHome(runtime);
        }
    }

    private void ForceHideAllVFX()
    {
        for (int i = 0; i < _runtimeModels.Count; i++)
        {
            RuntimeModel runtime = _runtimeModels[i];

            if (runtime == null) continue;
            if (runtime.vfx == null) continue;

            if (runtime.vfxParticles != null)
            {
                foreach (ParticleSystem ps in runtime.vfxParticles)
                {
                    if (ps == null) continue;
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                }
            }

            runtime.vfx.localScale = Vector3.zero;
            SetVFXAlpha(runtime, 0f);
            runtime.vfx.gameObject.SetActive(false);
        }
    }

    private void RestoreModelHome(RuntimeModel runtime)
    {
        if (runtime == null || runtime.model == null) return;

        // While the popup anchor is active, the model is temporarily parented under that anchor.
        // Do not write the original parent-local values until the model is returned to its real parent.
        if (runtime.popupAnchor != null && runtime.model.parent == runtime.popupAnchor)
            return;

        runtime.model.localPosition = runtime.modelHomeLocalPosition;
        runtime.model.localRotation = runtime.modelHomeLocalRotation;
        runtime.model.localScale = runtime.modelHomeLocalScale;
    }

    private void RestoreVisualHome(RuntimeModel runtime)
    {
        if (runtime == null || runtime.visualRoot == null) return;
        if (runtime.visualRoot == runtime.model) return;

        runtime.visualRoot.localPosition = runtime.visualHomeLocalPosition;
        runtime.visualRoot.localRotation = runtime.visualHomeLocalRotation;
        runtime.visualRoot.localScale = runtime.visualHomeLocalScale;
    }

    private void RestoreVFXHome(RuntimeModel runtime)
    {
        if (runtime == null || runtime.vfx == null) return;

        runtime.vfx.localPosition = runtime.vfxHomeLocalPosition;
        runtime.vfx.localRotation = runtime.vfxHomeLocalRotation;
        runtime.vfx.localScale = runtime.vfxHomeLocalScale;
    }

    private void PrepareRevealState()
    {
        _paused = false;
        _revealComplete = false;

        for (int i = 0; i < _runtimeModels.Count; i++)
        {
            RuntimeModel runtime = _runtimeModels[i];

            if (runtime == null) continue;

            runtime.hasFinished = false;
            runtime.popupPlaying = false;
            runtime.lockToHome = true;

            RestoreModelHome(runtime);
            RestoreVisualHome(runtime);

            if (globalSettings.disableAnimatorsDuringReveal)
                ResetAndDisableAnimators(runtime);

            RestoreModelHome(runtime);
            RestoreVisualHome(runtime);

            SetVisualVisible(runtime, false);
            PrepareVFXHidden(runtime);
        }
    }

    private void PrepareVFXHidden(RuntimeModel runtime)
    {
        if (runtime == null || runtime.vfx == null) return;

        RestoreVFXHome(runtime);

        runtime.vfx.gameObject.SetActive(true);
        runtime.vfx.localScale = Vector3.zero;

        SetVFXAlpha(runtime, 0f);

        if (runtime.vfxParticles != null)
        {
            foreach (ParticleSystem ps in runtime.vfxParticles)
            {
                if (ps == null) continue;
                ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            }
        }

        runtime.vfx.gameObject.SetActive(false);
    }

    private IEnumerator RevealSequence()
    {
        if (_runtimeModels.Count == 0)
        {
            CompleteReveal();
            yield break;
        }

        for (int i = 0; i < _runtimeModels.Count; i++)
        {
            RuntimeModel runtime = _runtimeModels[i];

            if (runtime == null || runtime.model == null)
            {
                if (runtime != null)
                    runtime.hasFinished = true;

                continue;
            }

            StartCoroutine(RevealSingleModelBlock(runtime));
        }

        while (!AllModelRevealsFinished())
        {
            while (_paused) yield return null;
            yield return null;
        }

        for (int i = 0; i < _runtimeModels.Count; i++)
        {
            RuntimeModel runtime = _runtimeModels[i];

            if (runtime == null) continue;

            runtime.popupPlaying = false;
            runtime.lockToHome = true;

            RestoreModelHome(runtime);
            RestoreVisualHome(runtime);
            SetVisualVisible(runtime, true);

            // If a gated activity must run immediately after reveal, do not let
            // normal story animators start here. The page node will start them
            // after the activity completes. Activity animations can still enable
            // their own animator when the child taps.
            if (globalSettings.enableAnimatorsAfterReveal && !ShouldHoldStorySystemsForActivityGate())
                EnableAnimators(runtime);

            RestoreModelHome(runtime);
            RestoreVisualHome(runtime);
        }

        CompleteReveal();
    }

    private bool AllModelRevealsFinished()
    {
        for (int i = 0; i < _runtimeModels.Count; i++)
        {
            if (_runtimeModels[i] != null && !_runtimeModels[i].hasFinished)
                return false;
        }

        return true;
    }

    private void CompleteReveal()
    {
        if (_isDisabled) return;

        _revealComplete = true;
        _sequenceRoutine = null;

        ForceHideAllVFX();

        // Movement scripts are story systems. If an activity is configured to
        // run after reveal and block the story, keep movement disabled until
        // ARTrackedPageNode starts the story after the activity.
        if (!ShouldHoldStorySystemsForActivityGate())
            ReEnablePageMovementScriptsAfterReveal();

        RestoreOnlyModelsToHome();

        OnRevealComplete?.Invoke(this);

        for (int i = 0; i < _runtimeModels.Count; i++)
        {
            RuntimeModel runtime = _runtimeModels[i];

            if (runtime == null) continue;

            runtime.lockToHome = false;
            runtime.popupPlaying = false;
        }
    }

    private IEnumerator RevealSingleModelBlock(RuntimeModel runtime)
    {
        float delay = globalSettings.sameTimeVfxRevealForAllModels
            ? 0f
            : Mathf.Max(0f, runtime.item.startDelay);

        yield return WaitWithPause(delay);

        if (runtime.vfx != null)
        {
            RestoreVFXHome(runtime);

            runtime.vfx.gameObject.SetActive(true);
            runtime.vfx.localScale = Vector3.zero;
            SetVFXAlpha(runtime, 0f);

            yield return null;

            if (runtime.vfxParticles != null)
            {
                foreach (ParticleSystem ps in runtime.vfxParticles)
                {
                    if (ps == null) continue;
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                    ps.Play(true);
                }
            }

            PlayVFXRevealSound();

            yield return VFXIntro(runtime, runtime.item.vfxIntroSeconds);
        }

        yield return RevealModelPopup(runtime);

        if (runtime.vfx != null)
        {
            yield return VFXOutro(runtime, runtime.item.vfxOutroSeconds);

            if (runtime.vfxParticles != null)
            {
                foreach (ParticleSystem ps in runtime.vfxParticles)
                {
                    if (ps == null) continue;
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                }
            }

            runtime.vfx.localScale = Vector3.zero;
            SetVFXAlpha(runtime, 0f);
            runtime.vfx.gameObject.SetActive(false);
        }

        runtime.popupPlaying = false;
        runtime.lockToHome = true;

        RestoreModelHome(runtime);
        RestoreVisualHome(runtime);

        runtime.hasFinished = true;
    }

    private IEnumerator RevealModelPopup(RuntimeModel runtime)
    {
        if (runtime == null || runtime.model == null)
            yield break;

        runtime.lockToHome = false;
        runtime.popupPlaying = true;

        RestoreModelHome(runtime);
        RestoreVisualHome(runtime);

        if (globalSettings.disableAnimatorsDuringReveal)
            ResetAndDisableAnimators(runtime);

        RestoreModelHome(runtime);
        RestoreVisualHome(runtime);

        // Important for multi-model pages:
        // scale/rotation must happen around this model's own VFX circle, not around
        // the model mesh/root pivot and not around the page center.
        CreatePopupAnchor(runtime);

        SetVisualVisible(runtime, false);
        ApplyPopupPose(runtime, 0f);

        SetVisualVisible(runtime, true);
        ApplyPopupPose(runtime, 0f);

        PlayPopupSound();

        float duration = Mathf.Max(0.1f, runtime.item.modelPopupSeconds);
        float elapsed = 0f;

        while (elapsed < duration)
        {
            while (_paused) yield return null;

            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / duration);

            ApplyPopupPose(runtime, t);

            yield return null;
        }

        runtime.popupPlaying = false;

        DestroyPopupAnchor(runtime, true);
        RestoreModelHome(runtime);
        RestoreVisualHome(runtime);

        runtime.lockToHome = true;
    }

    private IEnumerator VFXIntro(RuntimeModel runtime, float duration)
    {
        if (runtime == null || runtime.vfx == null)
            yield break;

        duration = Mathf.Max(0.1f, duration);

        float elapsed = 0f;

        while (elapsed < duration)
        {
            while (_paused) yield return null;

            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / duration);

            float scale = EaseOutBack(t, 1.35f);

            runtime.vfx.localScale = runtime.vfxHomeLocalScale * scale;
            SetVFXAlpha(runtime, Mathf.SmoothStep(0f, 1f, t));

            if (globalSettings.enableSpin)
                runtime.vfx.Rotate(0f, globalSettings.spinSpeed * Time.deltaTime, 0f, Space.Self);

            yield return null;
        }

        runtime.vfx.localScale = runtime.vfxHomeLocalScale;
        SetVFXAlpha(runtime, 1f);
    }

    private IEnumerator VFXOutro(RuntimeModel runtime, float duration)
    {
        if (runtime == null || runtime.vfx == null)
            yield break;

        duration = Mathf.Max(0.1f, duration);

        float elapsed = 0f;

        while (elapsed < duration)
        {
            while (_paused) yield return null;

            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / duration);

            float smooth = Mathf.SmoothStep(0f, 1f, t);
            float scale = 1f - smooth;

            runtime.vfx.localScale = runtime.vfxHomeLocalScale * scale;
            SetVFXAlpha(runtime, 1f - smooth);

            if (globalSettings.enableSpin)
                runtime.vfx.Rotate(0f, -globalSettings.spinSpeed * Time.deltaTime, 0f, Space.Self);

            yield return null;
        }

        runtime.vfx.localScale = Vector3.zero;
        SetVFXAlpha(runtime, 0f);
        runtime.vfx.gameObject.SetActive(false);
    }

    private IEnumerator WaitWithPause(float seconds)
    {
        if (seconds <= 0f)
            yield break;

        float elapsed = 0f;

        while (elapsed < seconds)
        {
            while (_paused) yield return null;

            elapsed += Time.deltaTime;
            yield return null;
        }
    }

    private void ApplyPopupPose(RuntimeModel runtime, float t)
    {
        if (runtime == null || runtime.model == null) return;

        t = Mathf.Clamp01(t);

        PopupPose pose = GetPopupPose(runtime.item.style, t);

        if (runtime.popupAnchor != null && runtime.model.parent == runtime.popupAnchor)
        {
            ApplyPopupPoseFromVFXAnchor(runtime, pose);
            return;
        }

        // Fallback for pages without a valid VFX anchor.
        // This keeps the earlier safe behavior: no renderer-bounds center calculation.
        RestoreModelHome(runtime);

        runtime.model.localPosition = runtime.modelHomeLocalPosition;

        runtime.model.localScale = new Vector3(
            runtime.modelHomeLocalScale.x * pose.scaleMultiplier.x,
            runtime.modelHomeLocalScale.y * pose.scaleMultiplier.y,
            runtime.modelHomeLocalScale.z * pose.scaleMultiplier.z
        );

        runtime.model.localRotation = runtime.modelHomeLocalRotation * Quaternion.Euler(pose.rotationOffset);
    }

    private PopupPose GetPopupPose(PopupStyle style, float t)
    {
        switch (style)
        {
            case PopupStyle.BouncyPop:
                return GetBouncyPopPose(t);

            case PopupStyle.JellyPop:
                return GetJellyPopPose(t);

            case PopupStyle.HeroPop:
                return GetHeroPopPose(t);

            case PopupStyle.SwirlPop:
                return GetSwirlPopPose(t);

            case PopupStyle.ZoomBlastPop:
                return GetZoomBlastPopPose(t);

            default:
                return GetHeroPopPose(t);
        }
    }

    private void CreatePopupAnchor(RuntimeModel runtime)
    {
        if (runtime == null || runtime.model == null) return;

        DestroyPopupAnchor(runtime, true);

        runtime.originalParent = runtime.model.parent;
        runtime.originalSiblingIndex = runtime.model.GetSiblingIndex();

        Vector3 pivotWorldPosition = runtime.vfx != null
            ? runtime.vfx.position
            : runtime.model.position;

        GameObject anchorObject = new GameObject("__PopupAnchor_" + runtime.model.name);
        Transform anchor = anchorObject.transform;

        if (runtime.originalParent != null)
        {
            anchor.SetParent(runtime.originalParent, false);
            anchor.localPosition = runtime.originalParent.InverseTransformPoint(pivotWorldPosition);
            anchor.localRotation = Quaternion.identity;
            anchor.localScale = Vector3.one;
        }
        else
        {
            anchor.position = pivotWorldPosition;
            anchor.rotation = Quaternion.identity;
            anchor.localScale = Vector3.one;
        }

        runtime.popupAnchorObject = anchorObject;
        runtime.popupAnchor = anchor;
        runtime.popupAnchorHomeLocalPosition = anchor.localPosition;
        runtime.popupAnchorHomeLocalRotation = anchor.localRotation;
        runtime.popupAnchorHomeLocalScale = anchor.localScale;

        // Keep the model visually where the artist placed it while changing the popup pivot.
        runtime.model.SetParent(anchor, true);

        runtime.anchoredModelLocalPosition = runtime.model.localPosition;
        runtime.anchoredModelLocalRotation = runtime.model.localRotation;
        runtime.anchoredModelLocalScale = runtime.model.localScale;
    }

    private void ApplyPopupPoseFromVFXAnchor(RuntimeModel runtime, PopupPose pose)
    {
        if (runtime == null) return;
        if (runtime.popupAnchor == null) return;
        if (runtime.model == null) return;

        // Keep the portal/VFX position fixed.
        // Do not move the popup anchor away from the assigned VFX circle.
        runtime.popupAnchor.localPosition = runtime.popupAnchorHomeLocalPosition;
        runtime.popupAnchor.localRotation = runtime.popupAnchorHomeLocalRotation * Quaternion.Euler(pose.rotationOffset);

        runtime.popupAnchor.localScale = new Vector3(
            runtime.popupAnchorHomeLocalScale.x * pose.scaleMultiplier.x,
            runtime.popupAnchorHomeLocalScale.y * pose.scaleMultiplier.y,
            runtime.popupAnchorHomeLocalScale.z * pose.scaleMultiplier.z
        );

        // Keep the model's offset from the VFX anchor stable.
        // Only the anchor is animated, so the model pops from its own VFX circle.
        runtime.model.localPosition = runtime.anchoredModelLocalPosition;
        runtime.model.localRotation = runtime.anchoredModelLocalRotation;
        runtime.model.localScale = runtime.anchoredModelLocalScale;
    }

    private void DestroyPopupAnchor(RuntimeModel runtime, bool restoreModelToOriginalParent)
    {
        if (runtime == null) return;

        Transform anchor = runtime.popupAnchor;

        if (restoreModelToOriginalParent && runtime.model != null && anchor != null && runtime.model.parent == anchor)
        {
            runtime.model.SetParent(runtime.originalParent, false);

            if (runtime.originalParent != null && runtime.model.parent == runtime.originalParent)
            {
                int maxIndex = runtime.originalParent.childCount - 1;
                int safeIndex = Mathf.Clamp(runtime.originalSiblingIndex, 0, maxIndex);
                runtime.model.SetSiblingIndex(safeIndex);
            }

            runtime.model.localPosition = runtime.modelHomeLocalPosition;
            runtime.model.localRotation = runtime.modelHomeLocalRotation;
            runtime.model.localScale = runtime.modelHomeLocalScale;
        }

        if (runtime.popupAnchorObject != null)
        {
            if (Application.isPlaying)
                Destroy(runtime.popupAnchorObject);
            else
                DestroyImmediate(runtime.popupAnchorObject);
        }

        runtime.popupAnchorObject = null;
        runtime.popupAnchor = null;
        runtime.popupAnchorHomeLocalPosition = Vector3.zero;
        runtime.popupAnchorHomeLocalRotation = Quaternion.identity;
        runtime.popupAnchorHomeLocalScale = Vector3.one;
        runtime.anchoredModelLocalPosition = Vector3.zero;
        runtime.anchoredModelLocalRotation = Quaternion.identity;
        runtime.anchoredModelLocalScale = Vector3.one;
    }

    private void CleanupAllPopupAnchors(bool restoreModelsToOriginalParents)
    {
        for (int i = 0; i < _runtimeModels.Count; i++)
            DestroyPopupAnchor(_runtimeModels[i], restoreModelsToOriginalParents);
    }

    private void SetVisualVisible(RuntimeModel runtime, bool visible)
    {
        if (runtime == null || runtime.visualRenderers == null) return;

        for (int i = 0; i < runtime.visualRenderers.Length; i++)
        {
            Renderer renderer = runtime.visualRenderers[i];

            if (renderer == null) continue;

            renderer.enabled = visible;
        }
    }

    private void ResetAndDisableAnimators(RuntimeModel runtime)
    {
        if (runtime == null || runtime.animators == null) return;

        for (int i = 0; i < runtime.animators.Length; i++)
        {
            Animator animator = runtime.animators[i];

            if (animator == null) continue;

            if (animator.gameObject.activeInHierarchy)
            {
                animator.enabled = true;
                animator.speed = 0f;
                animator.Rebind();
                animator.Update(0f);
            }

            animator.speed = 0f;
            animator.enabled = false;
        }

        RestoreModelHome(runtime);
        RestoreVisualHome(runtime);
    }

    private void EnableAnimators(RuntimeModel runtime)
    {
        if (runtime == null || runtime.animators == null) return;

        for (int i = 0; i < runtime.animators.Length; i++)
        {
            Animator animator = runtime.animators[i];

            if (animator == null) continue;

            animator.enabled = true;
            animator.speed = 1f;
        }

        RestoreModelHome(runtime);
        RestoreVisualHome(runtime);
    }

    private void DisablePageMovementScriptsDuringReveal()
    {
        if (!globalSettings.disablePageMovementScriptsDuringReveal)
            return;

        _disabledPageMovementScripts.Clear();

        MonoBehaviour[] behaviours = GetComponentsInChildren<MonoBehaviour>(true);

        for (int i = 0; i < behaviours.Length; i++)
        {
            MonoBehaviour behaviour = behaviours[i];

            if (behaviour == null) continue;
            if (behaviour == this) continue;

            string typeName = behaviour.GetType().Name.ToLowerInvariant();

            bool isMovementScript =
                typeName.Contains("spline") ||
                typeName.Contains("mover") ||
                typeName.Contains("move") ||
                typeName.Contains("path") ||
                typeName.Contains("follow");

            if (!isMovementScript) continue;

            behaviour.StopAllCoroutines();

            TryInvokeNoArgMethod(behaviour, "Stop");

            // Do NOT call ResetToStart() from the VFX system.
            // ResetToStart can move the model to the spline start while popup is still running.
            // ARTrackedPageNode resets and starts spline only after popup completion.

            if (behaviour.enabled)
            {
                behaviour.enabled = false;
                _disabledPageMovementScripts.Add(behaviour);
            }
        }
    }


    private bool ShouldHoldStorySystemsForActivityGate()
    {
        ARTrackedPageNode pageNode = GetComponentInParent<ARTrackedPageNode>(true);
        if (pageNode != null)
            return pageNode.HasBlockingAfterRevealActivity();

        ContentController controller = GetComponentInParent<ContentController>(true);
        return controller != null && controller.ShouldRunBeforeStoryAfterReveal();
    }

    private void ReEnablePageMovementScriptsAfterReveal()
    {
        for (int i = 0; i < _disabledPageMovementScripts.Count; i++)
        {
            MonoBehaviour behaviour = _disabledPageMovementScripts[i];

            if (behaviour == null) continue;

            behaviour.enabled = true;
        }

        _disabledPageMovementScripts.Clear();
    }

    private void TryInvokeNoArgMethod(MonoBehaviour behaviour, string methodName)
    {
        if (behaviour == null) return;

        MethodInfo method = behaviour.GetType().GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            System.Type.EmptyTypes,
            null
        );

        if (method == null) return;

        method.Invoke(behaviour, null);
    }

    private PopupPose GetBouncyPopPose(float t)
    {
        float scale = OvershootSettleScale(t, 1.32f, 0.86f);
        float wobble = Mathf.Sin(t * Mathf.PI * 5f) * Mathf.Pow(1f - t, 1.25f);

        float xz = scale * (1f + wobble * 0.12f);
        float yScale = scale * (1f - wobble * 0.08f);

        float y = Mathf.Sin(t * Mathf.PI) * ModelJumpHeight * 1.5f;
        y += Mathf.Sin(t * Mathf.PI * 5f) * Mathf.Pow(1f - t, 1.5f) * ModelJumpHeight * 0.35f;

        float rotZ = Mathf.Sin(t * Mathf.PI * 5f) * Mathf.Pow(1f - t, 1.35f) * 12f;
        float rotY = Mathf.Sin(t * Mathf.PI * 3f) * Mathf.Pow(1f - t, 1.2f) * 9f;

        return new PopupPose
        {
            scaleMultiplier = new Vector3(xz, yScale, xz),
            positionOffset = new Vector3(0f, y, 0f),
            rotationOffset = new Vector3(0f, rotY, rotZ)
        };
    }

    private PopupPose GetJellyPopPose(float t)
    {
        float baseScale = EaseOutBack(t, 1.75f);
        float jelly = Mathf.Sin(t * Mathf.PI * 6.5f) * Mathf.Pow(1f - t, 1.1f);

        float xz = baseScale * (1f + jelly * 0.23f);
        float yScale = baseScale * (1f - jelly * 0.28f);

        float y = Mathf.Sin(t * Mathf.PI) * ModelJumpHeight;
        float rotZ = Mathf.Sin(t * Mathf.PI * 5.5f) * Mathf.Pow(1f - t, 1.3f) * 10f;
        float rotX = Mathf.Sin(t * Mathf.PI * 4f) * Mathf.Pow(1f - t, 1.4f) * 6f;

        return new PopupPose
        {
            scaleMultiplier = new Vector3(xz, yScale, xz),
            positionOffset = new Vector3(0f, y, 0f),
            rotationOffset = new Vector3(rotX, 0f, rotZ)
        };
    }

    private PopupPose GetHeroPopPose(float t)
    {
        float scale = EaseOutBack(t, 1.25f);

        float lift = -ModelJumpHeight * 2f * (1f - EaseOutCubic(t));
        lift += Mathf.Sin(t * Mathf.PI) * ModelJumpHeight * 1.35f;

        float lean = Mathf.Lerp(-20f, 0f, EaseOutCubic(t));
        float turn = Mathf.Lerp(-30f, 0f, EaseOutCubic(t));

        float landing = Mathf.Clamp01((t - 0.68f) / 0.32f);
        float shake = Mathf.Sin(landing * Mathf.PI * 4f) * (1f - landing) * 5.5f;

        return new PopupPose
        {
            scaleMultiplier = new Vector3(scale, scale, scale),
            positionOffset = new Vector3(0f, lift, 0f),
            rotationOffset = new Vector3(lean + shake, turn, shake * 0.7f)
        };
    }

    private PopupPose GetSwirlPopPose(float t)
    {
        float scale = EaseOutBack(t, 1.85f);

        float spin = Mathf.Lerp(-260f, 0f, EaseOutCubic(t));
        float tilt = Mathf.Sin(t * Mathf.PI * 4.5f) * Mathf.Pow(1f - t, 1.2f) * 14f;

        float y = Mathf.Sin(t * Mathf.PI) * ModelJumpHeight * 1.2f;
        float x = Mathf.Sin(t * Mathf.PI * 2f) * Mathf.Pow(1f - t, 1.5f) * ModelJumpHeight * 0.3f;

        return new PopupPose
        {
            scaleMultiplier = new Vector3(scale, scale, scale),
            positionOffset = new Vector3(x, y, 0f),
            rotationOffset = new Vector3(0f, spin, tilt)
        };
    }

    private PopupPose GetZoomBlastPopPose(float t)
    {
        float scale;

        if (t < 0.25f)
        {
            scale = Mathf.Lerp(0f, 1.45f, EaseOutCubic(t / 0.25f));
        }
        else
        {
            float u = Mathf.Clamp01((t - 0.25f) / 0.75f);
            scale = 1f + Mathf.Sin(u * Mathf.PI * 5.5f) * Mathf.Pow(1f - u, 1.35f) * 0.2f;
        }

        float y = Mathf.Sin(t * Mathf.PI) * ModelJumpHeight * 1.7f;
        float z = -ModelJumpHeight * 0.65f * (1f - EaseOutCubic(t));

        float rotX = Mathf.Sin(t * Mathf.PI * 5f) * Mathf.Pow(1f - t, 1.3f) * 12f;
        float rotY = Mathf.Sin(t * Mathf.PI * 3f) * Mathf.Pow(1f - t, 1.2f) * 12f;
        float rotZ = Mathf.Sin(t * Mathf.PI * 7f) * Mathf.Pow(1f - t, 1.3f) * 15f;

        return new PopupPose
        {
            scaleMultiplier = new Vector3(scale, scale, scale),
            positionOffset = new Vector3(0f, y, z),
            rotationOffset = new Vector3(rotX, rotY, rotZ)
        };
    }

    private void CreateAudioSources()
    {
        _vfxAudioSource = gameObject.AddComponent<AudioSource>();
        _vfxAudioSource.playOnAwake = false;
        _vfxAudioSource.loop = false;

        _popupAudioSource = gameObject.AddComponent<AudioSource>();
        _popupAudioSource.playOnAwake = false;
        _popupAudioSource.loop = false;

        ApplyAudioSettings();
    }

    private void ApplyAudioSettings()
    {
        if (_vfxAudioSource != null)
        {
            _vfxAudioSource.volume = audioSettings.vfxRevealVolume;
            _vfxAudioSource.spatialBlend = audioSettings.spatialBlend;
        }

        if (_popupAudioSource != null)
        {
            _popupAudioSource.volume = audioSettings.popupVolume;
            _popupAudioSource.spatialBlend = audioSettings.spatialBlend;
        }
    }

    private void PlayVFXRevealSound()
    {
        ApplyAudioSettings();

        if (_vfxAudioSource == null) return;
        if (audioSettings.vfxRevealSound == null) return;

        _vfxAudioSource.PlayOneShot(audioSettings.vfxRevealSound, audioSettings.vfxRevealVolume);
    }

    private void PlayPopupSound()
    {
        ApplyAudioSettings();

        if (_popupAudioSource == null) return;
        if (audioSettings.popupSound == null) return;

        _popupAudioSource.PlayOneShot(audioSettings.popupSound, audioSettings.popupVolume);
    }

    private void StopRevealAudio()
    {
        if (_vfxAudioSource != null)
            _vfxAudioSource.Stop();

        if (_popupAudioSource != null)
            _popupAudioSource.Stop();
    }

    private void CacheVFXMaterials(Transform vfx, List<MaterialAlphaState> targetList)
    {
        targetList.Clear();

        if (vfx == null) return;

        Renderer[] renderers = vfx.GetComponentsInChildren<Renderer>(true);

        foreach (Renderer renderer in renderers)
        {
            if (renderer == null) continue;

            Material[] materials = renderer.materials;

            foreach (Material mat in materials)
            {
                if (mat == null) continue;

                int prop = -1;

                if (mat.HasProperty(BaseColorId))
                    prop = BaseColorId;
                else if (mat.HasProperty(ColorId))
                    prop = ColorId;

                if (prop == -1) continue;

                targetList.Add(new MaterialAlphaState
                {
                    material = mat,
                    colorProperty = prop,
                    originalColor = mat.GetColor(prop)
                });
            }
        }
    }

    private void SetVFXAlpha(RuntimeModel runtime, float alpha)
    {
        if (runtime == null) return;

        alpha = Mathf.Clamp01(alpha);

        if (runtime.vfxCanvasGroup != null)
            runtime.vfxCanvasGroup.alpha = alpha;

        for (int i = 0; i < runtime.vfxMaterials.Count; i++)
        {
            MaterialAlphaState state = runtime.vfxMaterials[i];

            if (state.material == null) continue;

            Color c = state.originalColor;
            c.a = state.originalColor.a * alpha;
            state.material.SetColor(state.colorProperty, c);
        }
    }

    private static float OvershootSettleScale(float t, float overshoot, float dip)
    {
        if (t < 0.36f)
            return Mathf.Lerp(0f, overshoot, EaseOutCubic(t / 0.36f));

        if (t < 0.56f)
            return Mathf.Lerp(overshoot, dip, Smooth01((t - 0.36f) / 0.2f));

        if (t < 0.82f)
            return Mathf.Lerp(dip, 1.08f, Smooth01((t - 0.56f) / 0.26f));

        return Mathf.Lerp(1.08f, 1f, Smooth01((t - 0.82f) / 0.18f));
    }

    private static float EaseOutBack(float t, float overshoot)
    {
        t = Mathf.Clamp01(t);

        float c1 = overshoot;
        float c3 = c1 + 1f;

        return 1f + c3 * Mathf.Pow(t - 1f, 3f) + c1 * Mathf.Pow(t - 1f, 2f);
    }

    private static float EaseOutCubic(float t)
    {
        t = Mathf.Clamp01(t);
        return 1f - Mathf.Pow(1f - t, 3f);
    }

    private static float Smooth01(float t)
    {
        t = Mathf.Clamp01(t);
        return t * t * (3f - 2f * t);
    }
}