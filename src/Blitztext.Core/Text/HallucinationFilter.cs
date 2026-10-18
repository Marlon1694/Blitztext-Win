using System.Text;

namespace Blitztext.Core.Text;

/// <summary>
/// Drops text Whisper invents: subtitle credits from its training data ("Untertitel im
/// Auftrag des ZDF, 2021") and polite closings on silence ("Thank you.", "Vielen Dank.").
/// Whisper's no-speech probability is not reliable for these cases, so Blitztext checks
/// how much of the recording is loud enough to be speech instead.
/// </summary>
public static class HallucinationFilter
{
    /// <summary>Credits and sign-offs that are never meant as dictation. Normalized, without years.</summary>
    public static readonly HashSet<string> Credits =
    [
        "untertitel im auftrag des zdf",
        "untertitel im auftrag des zdf für funk",
        "untertitelung im auftrag des zdf",
        "untertitelung des zdf",
        "untertitel des zdf",
        "untertitel der amara org community",
        "untertitel von der amara org community",
        "untertitel von amara org",
        "untertitel von stephanie geiges",
        "copyright wdr",
        "copyright swr",
        "vielen dank fürs zuschauen",
        "vielen dank für s zuschauen",
        "danke fürs zuschauen",
        "danke für s zuschauen",
        "abonniert den kanal",
        "bitte abonnieren",
        "abonniere meinen kanal",
        "subtitles by the amara org community",
        "subtitles by amara org",
        "thanks for watching",
        "thank you for watching",
        "thank you so much for watching",
        "please subscribe",
        "like and subscribe",
        "please like and subscribe",
    ];

    /// <summary>Short phrases people do say, so they only count as invented on a silent recording.</summary>
    public static readonly HashSet<string> DoubtfulPhrases =
    [
        "vielen dank", "danke", "danke schön", "dankeschön", "tschüss", "tschüs",
        "bis zum nächsten mal", "bis bald", "bis dann", "das war s", "ende",
        "thank you", "thanks", "thank you very much", "you", "bye", "bye bye",
        "okay", "ok", "ja", "hmm", "hm", "mhm",
        "musik", "music", "applaus", "applause", "lachen",
        "swr", "wdr", "zdf", "ard",
    ];

    /// <summary>Less speech than this and a doubtful phrase counts as invented.</summary>
    public const double MinimumVoicedSeconds = 0.3;

    /// <summary>
    /// True when the whole transcript is a credit, or a doubtful phrase on a recording
    /// with hardly any speech.
    /// </summary>
    public static bool IsInvented(string text, double voicedSeconds)
    {
        var key = PhraseKey(text);
        if (key.Length == 0) return false;
        if (Credits.Contains(key)) return true;
        return voicedSeconds < MinimumVoicedSeconds && DoubtfulPhrases.Contains(key);
    }

    /// <summary>Seconds of audio loud enough to be speech: 30 ms frames above about -40 dBFS.</summary>
    public static double VoicedSeconds(ReadOnlySpan<float> samples, double sampleRate = 16_000)
    {
        var frameLength = Math.Max(1, (int)(sampleRate * 0.03));
        var voicedFrames = 0;
        for (var start = 0; start + frameLength <= samples.Length; start += frameLength)
        {
            float sum = 0;
            for (var index = start; index < start + frameLength; index++)
            {
                sum += samples[index] * samples[index];
            }
            if (MathF.Sqrt(sum / frameLength) > 0.01f) voicedFrames++;
        }
        return voicedFrames * frameLength / sampleRate;
    }

    /// <summary>
    /// Removes credits at the end of a transcript ("... bis morgen. Untertitel im Auftrag
    /// des ZDF, 2021."). Works on any engine's text, so Parakeet output is covered too.
    /// </summary>
    public static string RemovingTrailingCredits(string text)
    {
        var result = text.Trim();
        while (TrailingCreditStart(result) is { } cut)
        {
            result = result[..cut].Trim();
        }
        return result;
    }

    /// <summary>Start of the last one to three sentences when together they are a credit.</summary>
    private static int? TrailingCreditStart(string text)
    {
        if (text.Length == 0) return null;
        var starts = SentenceStarts(text);
        foreach (var start in starts.Skip(Math.Max(0, starts.Count - 3)).Reverse())
        {
            if (Credits.Contains(PhraseKey(text[start..]))) return start;
        }
        return null;
    }

    /// <summary>
    /// Index 0 and every position after ".", "!", "?" or "…" plus whitespace, or after a
    /// line break. "Amara.org" stays one sentence.
    /// </summary>
    private static List<int> SentenceStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            var next = index + 1;
            if (TextChars.IsNewline(character) && next < text.Length)
            {
                // "\r\n" is one line break.
                if (character == '\r' && text[next] == '\n') continue;
                starts.Add(next);
            }
            else if (".!?…".Contains(character) && next < text.Length && char.IsWhiteSpace(text[next]))
            {
                starts.Add(next);
            }
        }
        return starts;
    }

    /// <summary>Lowercased letters and digits, everything else becomes a single space.</summary>
    public static string Normalized(string text)
    {
        var result = new StringBuilder(text.Length);
        var pendingSpace = false;
        var lower = text.ToLowerInvariant();
        for (var index = 0; index < lower.Length; index++)
        {
            var c = lower[index];
            if (char.IsLetterOrDigit(c) || char.IsNumber(c) || (char.IsSurrogate(c) && char.IsLetterOrDigit(lower, index)))
            {
                if (pendingSpace && result.Length > 0) result.Append(' ');
                result.Append(c);
                pendingSpace = false;
            }
            else if (result.Length > 0 && TextChars.IsMark(c))
            {
                result.Append(c);
            }
            else
            {
                pendingSpace = true;
            }
        }
        return result.ToString();
    }

    /// <summary>Like <see cref="Normalized"/>, without numbers, so "ZDF, 2021" and "ZDF 2017" match "zdf".</summary>
    public static string PhraseKey(string text) =>
        string.Join(' ', Normalized(text)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(part => !part.All(char.IsNumber)));
}
