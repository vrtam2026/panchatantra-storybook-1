using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Video;

public class ContentController : MonoBehaviour, IARContent
{
    private VideoPlayer[] vPlayers;
    private Animator[] anims;
    private AudioSource[] audios;
    private System.Action onCompleted;

    [SerializeField] private List<ContentStep> steps = new List<ContentStep>();

    private int currentStepIndex = 0;
    private int currentTapCount = 0;

    [SerializeField] float powerDecay = 0.5f;
    [SerializeField] float powerGain = 1.5f;

    Camera _cam;

    private Animator animator;
    private AudioSource audioSource;

    void Awake()
    {
        _cam = Camera.main;

        vPlayers = GetComponentsInChildren<VideoPlayer>();
        anims = GetComponentsInChildren<Animator>();
        audios = GetComponentsInChildren<AudioSource>();

        animator = GetComponent<Animator>();
        audioSource = GetComponent<AudioSource>();
    }

    public void SetCompletionCallback(System.Action callback)
    {
        onCompleted = callback;
    }

    public void PlayContent()
    {
        if (steps == null || steps.Count == 0)
        {
            //StartCoroutine(FallbackPlay());
            onCompleted?.Invoke();
            return;
        }
        currentStepIndex = 0;
        StartCoroutine(RunSteps());
    }

    /*IEnumerator FallbackPlay()
    {
        foreach (var v in vPlayers)
        {
            v.time = 0;
            v.Play();
        }

        foreach (var a in anims)
        {
            a.speed = 1f;
            a.Play(a.GetCurrentAnimatorStateInfo(0).fullPathHash, 0, 0f);
        }

        foreach (var s in audios)
        {
            s.Stop();
            s.Play();
        }

        yield return new WaitForSeconds(GetMaxDuration());

        onCompleted?.Invoke();
    }*/

    IEnumerator RunSteps()
    {
        while (currentStepIndex < steps.Count)
        {
            var step = steps[currentStepIndex];

            switch (step.type)
            {
                /*case StepType.WaitForTap:
                    yield return WaitForTapStep(step);
                    break;

                case StepType.WaitForHold:
                    yield return WaitForHoldStep(step.duration);
                    break;

                case StepType.WaitForSeconds:
                    yield return new WaitForSeconds(step.duration);
                    break;*/

                case StepType.GraffitiTap:
                    yield return GraffitiTapStep(step);
                    break;

                case StepType.WaitForScreenTap:
                    yield return WaitForScreenTapStep(step);
                    break;

                case StepType.WaitForModelTap:
                    yield return WaitForModelTapStep(step);
                    break;

                case StepType.ContinuousTap:
                    yield return ContinuousTapStep(step);
                    break;

                case StepType.WaitForMultipleTaps:
                    yield return WaitForMultipleTapsStep(step.tapCount);
                    break;

                case StepType.WaitForTargetTap:
                    yield return WaitForTargetTapStep(step);
                    break;

                case StepType.Quiz:
                    yield return QuizStep(step);
                    break;
            }

            currentStepIndex++;
        }

        // FINAL COMPLETE
        onCompleted?.Invoke();
    }

    [SerializeField] ParticleSystem leftGraffiti;
    [SerializeField] ParticleSystem rightGraffiti;

    IEnumerator GraffitiTapStep(ContentStep step)
    {
        float timer = 0f;

        Debug.Log("GraffitiTapStep");

        // SHOW instruction
        InstructionUI.Instance?.Show(step.instructionText);

        ModelInteraction.Current.EnableInteractionCallback((data) =>
        {
            if (data.type == InteractionData.InteractionType.ScreenTap)
            {
                PlayGraffiti();
            }
        });

        while (timer < step.duration)
        {
            timer += Time.deltaTime;
            yield return null;
        }

        // HIDE instruction
        InstructionUI.Instance?.Hide();

        ModelInteraction.Current.EnableInteractionCallback(null);
    }

    void PlayGraffiti()
    {
        Debug.Log("PlayGraffiti");

        if (leftGraffiti != null)
            leftGraffiti.Play();

        if (rightGraffiti != null)
            rightGraffiti.Play();
    }

    IEnumerator WaitForScreenTapStep(ContentStep step)
    {
        bool done = false;

        ModelInteraction.Current.EnableInteractionCallback((data) =>
        {
            if (data.type == InteractionData.InteractionType.ScreenTap)
            {
                Debug.Log("Screen Tapped");
                //step.uiToShow?.SetActive(true);
                done = true;
            }
        });

        while (!done) yield return null;

        ModelInteraction.Current.EnableInteractionCallback(null);
    }

    IEnumerator WaitForModelTapStep(ContentStep step)
    {
        float timer = 0f;

        // Show instruction
        InstructionUI.Instance?.Show(step.instructionText);

        ModelInteraction.Current.EnableInteractionCallback((data) =>
        {
            if (data.type == InteractionData.InteractionType.ModelTap)
            {
                PlayTapAudio(); // your BullMoo or similar
            }
        });

        while (timer < step.duration)
        {
            timer += Time.deltaTime;
            yield return null;
        }

        // Hide instruction
        InstructionUI.Instance?.Hide();

        ModelInteraction.Current.EnableInteractionCallback(null);
    }

    void PlayTapAudio()
    {
        /*if (animator != null)
        {
            animator.Play("Bull_moo");
        }*/

        if (audioSource == null) return;

        audioSource.Stop();   // restart sound each tap
        audioSource.Play();
    }

    IEnumerator ContinuousTapStep(ContentStep step)
    {
        float power = 0f;

        ModelInteraction.Current.EnableInteractionCallback((data) =>
        {
            if (data.type == InteractionData.InteractionType.ScreenTap)
            {
                power += Time.deltaTime * powerGain;
            }
        });

        while (power < 1f)
        {
            power -= Time.deltaTime * powerDecay;
            power = Mathf.Clamp01(power);

            // update UI + animation
            //UpdatePowerUI(power);
            //UpdatePullAnimation(power);

            yield return null;
        }

        ModelInteraction.Current.EnableInteractionCallback(null);
    }

    IEnumerator WaitForMultipleTapsStep(int requiredTaps)
    {
        int tapCount = 0;

        ModelInteraction.Current.EnableInteractionCallback((data) =>
        {
            if (data.type == InteractionData.InteractionType.ModelTap)
            {
                tapCount++;
            }
        });

        while (tapCount < requiredTaps)
            yield return null;

        // replace model
        //BreakModel();

        ModelInteraction.Current.EnableInteractionCallback(null);
    }

    IEnumerator WaitForTargetTapStep(ContentStep step)
    {
        bool done = false;

        ModelInteraction.Current.EnableInteractionCallback((data) =>
        {
            if (data.type == InteractionData.InteractionType.ModelTap)
            {
                if (data.hitObject.CompareTag("Head"))
                {
                    //PlayHeadAnimation();
                    done = true;
                }
                else if (data.hitObject.CompareTag("Body"))
                {
                    //PlayBodyAnimation();
                    done = true;
                }
            }
        });

        while (!done) yield return null;

        ModelInteraction.Current.EnableInteractionCallback(null);
    }

    IEnumerator QuizStep(ContentStep step)
    {
        int selected = -1;

        // enable UI
        foreach (var opt in step.quizOptions)
            opt.SetActive(true);

        ModelInteraction.Current.EnableInteractionCallback((data) =>
        {
            if (data.type == InteractionData.InteractionType.UI)
            {
                //selected = data.optionIndex;
            }
        });

        while (selected == -1)
            yield return null;

        // disable UI
        foreach (var opt in step.quizOptions)
            opt.SetActive(false);

        if (selected == step.correctOption)
        {
            Debug.Log("Correct!");
        }
        else
        {
            Debug.Log("Wrong!");
        }

        ModelInteraction.Current.EnableInteractionCallback(null);
    }
/*
    IEnumerator WaitForTapStep(ContentStep step)
    {
        yield return new WaitForSeconds(0.3f);
        if (step.uiToShow != null)
            step.uiToShow.SetActive(true);

        bool tapped = false;

        ModelInteraction.Current.EnableTapCallback(() =>
        {
            tapped = true;
        });

        float timeout = step.timeout > 0 ? step.timeout : 5f; // optional
        float timer = 0f;

        while (!tapped && timer < timeout)
        {
            timer += Time.deltaTime;
            yield return null;
        }

        if (step.uiToShow != null)
            step.uiToShow.SetActive(false);

        ModelInteraction.Current?.EnableTapCallback(null);
    }

    IEnumerator WaitForHoldStep(float holdTime)
    {
        float timer = 0f;

        while (timer < holdTime)
        {
            if (IsHolding()) // you implement this
                timer += Time.deltaTime;
            else
                timer = 0f;

            yield return null;
        }
    }

    IEnumerator WaitForMultipleTapsStep(int requiredTaps)
    {
        currentTapCount = 0;

        ModelInteraction.Current.EnableTapCallback(() =>
        {
            currentTapCount++;
        });

        while (currentTapCount < requiredTaps)
        {
            yield return null;
        }
        ModelInteraction.Current?.EnableTapCallback(null);
    }*/

    /*float GetMaxDuration()
    {
        float max = 0f;

        // VIDEOS
        foreach (var v in vPlayers)
        {
            if (v == null) continue;

            if (!v.isPrepared)
                v.Prepare();

            double length = v.length;

            if (length > max)
                max = (float)length;
        }

        //  ANIMATIONS 
        foreach (var a in anims)
        {
            if (a == null) continue;

            var clips = a.runtimeAnimatorController.animationClips;
            foreach (var clip in clips)
            {
                if (clip.length > max)
                    max = clip.length;
            }
        }

        // AUDIO
        foreach (var s in audios)
        {
            if (s == null || s.clip == null) continue;

            float length = s.clip.length;

            if (length > max)
                max = length;
        }

        return max;
    }*/

    public void PauseContent()
    {
        foreach (var v in vPlayers) v.Pause();
        foreach (var a in anims) a.speed = 0f;
        foreach (var s in audios) s.Pause();
    }

    public void ReplayContent()
    {
        StopAllCoroutines();

        currentStepIndex = 0;
        currentTapCount = 0;

        ModelInteraction.Current?.EnableInteractionCallback(null);

        PlayContent();
    }

    bool IsHolding()
    {
        if (_cam == null) return false;

        // TOUCH
        var touch = Touchscreen.current;
        if (touch != null && touch.primaryTouch.press.isPressed)
        {
            Vector2 pos = touch.primaryTouch.position.ReadValue();

            if (IsTouchingModel(pos))
                return true;
        }

        // MOUSE (editor testing)
        var mouse = Mouse.current;
        if (mouse != null && mouse.leftButton.isPressed)
        {
            Vector2 pos = mouse.position.ReadValue();

            if (IsTouchingModel(pos))
                return true;
        }

        return false;
    }

    bool IsTouchingModel(Vector2 screenPos)
    {
        Ray ray = _cam.ScreenPointToRay(screenPos);

        if (Physics.Raycast(ray, out RaycastHit hit))
        {
            // check if hit belongs to THIS prefab
            return hit.transform.IsChildOf(transform);
        }

        return false;
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

public enum StepType
{
    GraffitiTap,

    WaitForTap,
    WaitForHold,
    WaitForSeconds,

    WaitForScreenTap,
    WaitForModelTap,
    ContinuousTap,
    WaitForMultipleTaps,
    WaitForTargetTap,
    Quiz
}

[System.Serializable]
public class ContentStep
{
    public StepType type;

    public float duration; // for WaitForSeconds / Hold
    public int tapCount;   // for multiple taps

    public string instructionText;
    public float timeout;

    public int correctOption;
    public GameObject[] quizOptions;
}
