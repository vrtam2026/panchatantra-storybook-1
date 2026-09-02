using UnityEngine;
using UnityEngine.Video;

// ─────────────────────────────────────────────────────────────────────────────
// QuizQuestion.cs
// Data class only — NOT a MonoBehaviour, never add as a component.
// ─────────────────────────────────────────────────────────────────────────────

[System.Serializable]
public class QuizQuestion
{
    [Header("Question")]
    public string questionText;

    [Header("Options")]
    public string option1;
    public string option2;
    public string option3;
    public string option4;

    // ── Correct Answers ───────────────────────────────────────────────────────
    // Tick ONE option  = single answer (that option is correct)
    // Tick 2+ options  = multi-answer  (user must select ALL ticked options)

    [Header("Correct Answers")]
    [Tooltip("Tick the correct answer(s).\n1 ticked = single answer.\n2+ ticked = multi-answer (Submit required).")]
    public bool option1IsCorrect = false;
    public bool option2IsCorrect = false;
    public bool option3IsCorrect = false;
    public bool option4IsCorrect = false;

    // Auto-detected — no need to set manually
    public bool IsMultiAnswer => CorrectCount > 1;

    public int CorrectCount
    {
        get
        {
            int c = 0;
            if (option1IsCorrect) c++;
            if (option2IsCorrect) c++;
            if (option3IsCorrect) c++;
            if (option4IsCorrect) c++;
            return c;
        }
    }

    public int SingleCorrectIndex
    {
        get
        {
            if (option1IsCorrect) return 1;
            if (option2IsCorrect) return 2;
            if (option3IsCorrect) return 3;
            if (option4IsCorrect) return 4;
            return 1;
        }
    }

    // ── Audio & Video ─────────────────────────────────────────────────────────

    [Header("Audio and Video")]
    [Tooltip("Sage reads this question aloud.")]
    public AudioClip questionAudio;

    [Tooltip("Sage question video. Plays on RawImage_sage_video.")]
    public VideoClip questionVideo;

    // ── Timing ────────────────────────────────────────────────────────────────

    [Header("Timing")]
    [Tooltip("The exact second in the audio where options should start appearing.\n\n" +
             "HOW TO SET:\n" +
             "1. Play the question audio\n" +
             "2. Note the timestamp when sage finishes reading the question\n" +
             "   and starts reading the options\n" +
             "3. Enter that number here\n\n" +
             "EXAMPLE: Audio is 10s. Sage reads question for 6s, then options.\n" +
             "Set this to 6. Options will start at 6s, finish by 10s.\n\n" +
             "LEAVE AT 0 to use the global Question Phase Percent instead.")]
    public float optionStartTime = 0f;
}