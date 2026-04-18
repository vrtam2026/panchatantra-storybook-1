using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.Video;

// ─────────────────────────────────────────────────────────────────────────────
// QuizManager.cs  —  Attach to Quiz_Panel.
// ─────────────────────────────────────────────────────────────────────────────

public class QuizManager : MonoBehaviour
{
    [Header("Questions")]
    [SerializeField] private List<QuizQuestion> questions;

    [Header("UI")]
    [Tooltip("Drag StartPanel here.")]
    [SerializeField] private GameObject startPanel;

    [Tooltip("Element 0=Option1_Button  1=Option2_Button  2=Option3_Button  3=Option4_Button")]
    [SerializeField] private GameObject[] options;
    public GameObject[] Options => options;

    [SerializeField] private TextMeshProUGUI questionText;
    [SerializeField] private Sprite optionDefaultSprite;

    [Header("Multi Answer")]
    [SerializeField] private MultiAnswer multiAnswer;
    public bool IsMultiAnswer => currentQuestion != null && currentQuestion.IsMultiAnswer;

    [Header("Reveal")]
    [SerializeField] private QuizTextReveal quizTextReveal;

    [Header("Panels")]
    [SerializeField] private GameObject finishPanel;

    [Header("Correct Reaction Pairs")]
    [SerializeField] private List<ReactionPair> correctPairs;

    [Header("Wrong Reaction Pairs")]
    [SerializeField] private List<ReactionPair> wrongPairs;

    [Header("Audio Sources")]
    [SerializeField] private AudioSource questionAudioSource;
    [SerializeField] private AudioSource reactionAudioSource;

    [Header("Video")]
    [SerializeField] private VideoPlayer sageVideoPlayer;

    [Header("Timer")]
    [SerializeField] private QuizTimer quizTimer;

    [Header("Timing")]
    [SerializeField] private float cooldownDuration = 2f;

    [Header("State — Read Only")]
    [SerializeField] private int currentQuestionIndex = 0;
    [SerializeField] private int currentAttempt = 0;

    private QuizQuestion currentQuestion;
    private bool isPaused = false;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void Awake()
    {
        if (sageVideoPlayer != null)
        {
            sageVideoPlayer.playOnAwake = false;
            sageVideoPlayer.isLooping = false;
        }
    }

    private void Start()
    {
        if (startPanel != null) startPanel.SetActive(true);
    }

    private void OnDisable()
    {
        // Only stop audio/video/timer on disable.
        // Do NOT stop coroutines or cancel reveal here —
        // image tracker briefly deactivates canvas which fires OnDisable
        // and was killing the reveal coroutine on Q1.
        StopAllAudio();
        StopVideo();
        if (quizTimer != null) quizTimer.StopTimer();
    }

    // ── StartButton ───────────────────────────────────────────────────────────

    public void StartQuiz()
    {
        if (startPanel != null) startPanel.SetActive(false);

        currentQuestionIndex = 0;
        currentAttempt = 0;

        // Activate this GameObject — Quiz_Panel may be inactive
        // because QuizObserver hides the canvas at start.
        // Coroutines cannot run on inactive GameObjects.
        gameObject.SetActive(true);

        if (quizTextReveal != null) quizTextReveal.CancelReveal();
        StopAllCoroutines();
        StopAllAudio();
        StopVideo();
        if (quizTimer != null) quizTimer.StopTimer();

        LoadQuestion();
    }

    // ── PauseQuiz — called by QuizObserver ────────────────────────────────

    public void PauseQuiz(bool pause)
    {
        if (isPaused == pause) return;
        isPaused = pause;

        if (quizTimer != null) quizTimer.PauseTimer(pause);

        if (pause)
        {
            if (questionAudioSource != null) questionAudioSource.Pause();
            if (reactionAudioSource != null) reactionAudioSource.Pause();
            if (sageVideoPlayer != null) sageVideoPlayer.Pause();
        }
        else
        {
            if (questionAudioSource != null) questionAudioSource.UnPause();
            if (reactionAudioSource != null) reactionAudioSource.UnPause();
            if (sageVideoPlayer != null) sageVideoPlayer.Play();
        }
    }

    // ── Load Question ─────────────────────────────────────────────────────────

    private void LoadQuestion()
    {
        if (questions == null || questions.Count == 0) return;

        if (currentQuestionIndex >= questions.Count)
        {
            FinishQuiz();
            return;
        }

        currentAttempt = 0;
        currentQuestion = questions[currentQuestionIndex];
        if (currentQuestion == null) return;

        string[] opts = {
            currentQuestion.option1, currentQuestion.option2,
            currentQuestion.option3, currentQuestion.option4
        };

        for (int i = 0; i < options.Length; i++)
        {
            if (options[i] == null) continue;
            TextMeshProUGUI tmp = options[i].GetComponentInChildren<TextMeshProUGUI>();
            if (tmp != null) tmp.text = opts[i];
            AnswerScript ans = options[i].GetComponent<AnswerScript>();
            if (ans != null) ans.SetCorrect(currentQuestion.SingleCorrectIndex == i + 1);
        }

        ResetButtonColors();
        DisableAllButtons();
        StopAllAudio();
        StopVideo();

        if (multiAnswer != null)
            multiAnswer.SetupQuestion(currentQuestion, options);

        if (quizTimer != null) quizTimer.ResetDisplay();

        PlayVideoAndAudio(currentQuestion.questionVideo, currentQuestion.questionAudio);

        if (quizTextReveal != null)
            quizTextReveal.StartReveal(currentQuestion, currentQuestion.questionAudio, OnRevealComplete, this);
        else
        {
            EnableAllButtons();
            if (quizTimer != null) quizTimer.StartTimer();
        }

        if (EventSystem.current != null)
            EventSystem.current.SetSelectedGameObject(null);
    }

    private void OnRevealComplete()
    {
        EnableAllButtons();
        if (quizTimer != null) quizTimer.StartTimer();
        Debug.Log("[QuizManager] OnRevealComplete — buttons enabled.");
    }

    // ── Answer Selected ───────────────────────────────────────────────────────

    public void OnAnswerSelected(bool isCorrect)
    {
        if (!isActiveAndEnabled) return;
        StopEverything();

        if (isCorrect)
        {
            PlayReaction(GetRandomPair(correctPairs));
            StartCoroutine(NextAfterCooldown());
        }
        else
        {
            if (currentAttempt == 0)
            {
                currentAttempt = 1;
                PlayReaction(GetRandomPair(wrongPairs));
                StartCoroutine(ReplayAfterReaction());
            }
            else
            {
                RevealCorrectAnswer();
                PlayReaction(GetRandomPair(wrongPairs));
                StartCoroutine(NextAfterCooldown());
            }
        }
    }

    // ── Timer Expired ─────────────────────────────────────────────────────────

    public void OnTimerExpired()
    {
        if (!isActiveAndEnabled) return;

        if (currentQuestion != null && currentQuestion.IsMultiAnswer && multiAnswer != null)
        {
            StopEverything();
            multiAnswer.AutoSubmit();
            return;
        }

        StopEverything();
        if (currentAttempt == 0)
        {
            currentAttempt = 1;
            DisableAllButtons();
            PlayReaction(GetRandomPair(wrongPairs));
            StartCoroutine(ReplayAfterReaction());
        }
        else
        {
            RevealCorrectAnswer();
            PlayReaction(GetRandomPair(wrongPairs));
            StartCoroutine(NextAfterCooldown());
        }
    }

    // ── Skip ──────────────────────────────────────────────────────────────────

    public void SkipQuestion()
    {
        StopEverything();
        currentQuestionIndex++;
        LoadQuestion();
    }

    // ── Coroutines ────────────────────────────────────────────────────────────

    private IEnumerator ReplayAfterReaction()
    {
        yield return new WaitForSeconds(0.2f);

        if (reactionAudioSource != null)
            yield return new WaitWhile(() => reactionAudioSource != null && reactionAudioSource.isPlaying);

        yield return new WaitForSeconds(0.3f);

        if (currentQuestion == null) yield break;

        // Retry: no animation — reset, show Submit, enable buttons, start timer
        StopVideo();
        ResetButtonColors();
        if (multiAnswer != null)
            multiAnswer.SetupQuestion(currentQuestion, options);
        EnableAllButtons();
        if (quizTimer != null) quizTimer.StartTimer();
    }

    private IEnumerator NextAfterCooldown()
    {
        yield return new WaitForSeconds(cooldownDuration);
        if (!isActiveAndEnabled) yield break;
        currentQuestionIndex++;
        LoadQuestion();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void StopEverything()
    {
        if (quizTextReveal != null) quizTextReveal.CancelReveal();
        StopAllCoroutines();
        StopAllAudio();
        StopVideo();
        if (quizTimer != null) quizTimer.StopTimer();
    }

    public void DisableAllButtons()
    {
        foreach (GameObject btn in options)
        {
            if (btn == null) continue;
            Button b = btn.GetComponent<Button>();
            if (b != null) b.interactable = false;
        }
    }

    private void EnableAllButtons()
    {
        foreach (GameObject btn in options)
        {
            if (btn == null) continue;
            Button b = btn.GetComponent<Button>();
            if (b != null) b.interactable = true;
        }
    }

    private void ResetButtonColors()
    {
        foreach (GameObject btn in options)
        {
            if (btn == null) continue;
            Button b = btn.GetComponent<Button>();
            Image img = btn.GetComponent<Image>();
            if (b != null)
            {
                ColorBlock cb = b.colors;
                cb.fadeDuration = 0f;
                cb.disabledColor = cb.normalColor;
                b.colors = cb;
            }
            if (img != null)
            {
                img.color = Color.white;
                if (optionDefaultSprite != null) img.sprite = optionDefaultSprite;
            }
        }
    }

    private void RevealCorrectAnswer()
    {
        foreach (GameObject btn in options)
        {
            if (btn == null) continue;
            AnswerScript ans = btn.GetComponent<AnswerScript>();
            if (ans != null && ans.isCorrect)
            {
                Image img = btn.GetComponent<Image>();
                if (img != null) img.color = Color.green;
                break;
            }
        }
    }

    private void FinishQuiz()
    {
        StopAllAudio();
        StopVideo();
        if (quizTimer != null) quizTimer.StopTimer();
        if (finishPanel != null) finishPanel.SetActive(true);
    }

    private void PlayVideoAndAudio(VideoClip video, AudioClip audio)
    {
        if (sageVideoPlayer != null && video != null)
        {
            sageVideoPlayer.Stop();
            sageVideoPlayer.isLooping = false;
            sageVideoPlayer.clip = video;
            sageVideoPlayer.Play();
        }
        if (questionAudioSource != null && audio != null)
        {
            questionAudioSource.clip = audio;
            questionAudioSource.Play();
        }
    }

    private void PlayReaction(ReactionPair pair)
    {
        if (pair == null) return;
        if (reactionAudioSource != null && pair.audio != null)
        {
            reactionAudioSource.Stop();
            reactionAudioSource.clip = pair.audio;
            reactionAudioSource.Play();
        }
        if (sageVideoPlayer != null && pair.video != null)
        {
            sageVideoPlayer.Stop();
            sageVideoPlayer.isLooping = false;
            sageVideoPlayer.clip = pair.video;
            sageVideoPlayer.Play();
        }
    }

    private ReactionPair GetRandomPair(List<ReactionPair> pool)
    {
        if (pool == null || pool.Count == 0) return null;
        List<ReactionPair> valid = new List<ReactionPair>();
        foreach (ReactionPair p in pool)
            if (p != null && p.audio != null && p.video != null) valid.Add(p);
        if (valid.Count == 0) return null;
        return valid[Random.Range(0, valid.Count)];
    }

    private void StopAllAudio()
    {
        if (questionAudioSource != null) questionAudioSource.Stop();
        if (reactionAudioSource != null) reactionAudioSource.Stop();
    }

    private void StopVideo()
    {
        if (sageVideoPlayer != null) sageVideoPlayer.Stop();
    }
}