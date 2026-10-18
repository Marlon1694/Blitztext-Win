namespace Blitztext.Core.Text;

/// <summary>
/// Fits a dictation into the text around the cursor: a space where words would touch, a
/// capital letter at a sentence start, a lowercase function word mid-sentence and no
/// period where the sentence goes on. Only the edges of the dictation change.
///
/// Plain rules instead of the language model, so it also works for word-for-word dictation.
/// </summary>
public static class InsertionFitter
{
    public static string Fit(string text, string before, string after)
    {
        var result = text.Trim();
        if (result.Length == 0) return text;

        if (StartsSentence(before))
        {
            result = UppercasingFirstLetter(result);
        }
        else if (ContinuesSentence(before))
        {
            result = LowercasingFunctionWord(result);
        }

        if (NeedsSpaceBefore(result, before))
        {
            result = " " + result;
        }

        if (after.Length > 0)
        {
            var next = after[0];
            if (result.EndsWith('.') && ".,;:!?".Contains(next))
            {
                result = result[..^1];
            }
            else if (result.EndsWith('.') && SentenceContinues(after) && EndsWithWord(result))
            {
                result = result[..^1];
            }
            if (char.IsLetter(next) || char.IsNumber(next) || "„(".Contains(next))
            {
                result += " ";
            }
        }
        return result;
    }

    // MARK: - Sentence position

    /// <summary>Text before the cursor without trailing spaces; line breaks stay.</summary>
    private static string TrimmedBefore(string before) => before.TrimEnd(' ', '\t', ' ');

    private static bool StartsSentence(string before)
    {
        var trimmed = TrimmedBefore(before);
        if (trimmed.Length == 0) return true;
        var last = trimmed[^1];
        return TextChars.IsNewline(last) || ".!?…".Contains(last);
    }

    private static bool ContinuesSentence(string before)
    {
        var trimmed = TrimmedBefore(before);
        if (trimmed.Length == 0) return false;
        var last = trimmed[^1];
        return char.IsLetter(last) || char.IsNumber(last) || ",;-–".Contains(last);
    }

    /// <summary>The text after the cursor goes on with a lowercase word on the same line.</summary>
    private static bool SentenceContinues(string after)
    {
        foreach (var c in after)
        {
            if (c is ' ' or '\t') continue;
            return char.IsLetter(c) && char.IsLower(c);
        }
        return false;
    }

    /// <summary>The last word has at least four letters, so "usw." or "z. B." keep their period.</summary>
    private static bool EndsWithWord(string text)
    {
        var body = text[..^1];
        var count = 0;
        for (var i = body.Length - 1; i >= 0 && char.IsLetter(body[i]); i--) count++;
        return count >= 4 && body.Length > 0 && char.IsLetter(body[^1]);
    }

    // MARK: - Spacing

    private static bool NeedsSpaceBefore(string text, string before)
    {
        if (before.Length == 0 || text.Length == 0) return false;
        var previous = before[^1];
        if (char.IsWhiteSpace(previous)) return false;
        var first = text[0];
        if (",.;:!?)]}…".Contains(first)) return false;
        if ("([{„‚@#_".Contains(previous)) return false;
        char? beforePrevious = before.Length >= 2 ? before[^2] : null;
        var previousStandsAlone = beforePrevious is null || char.IsWhiteSpace(beforePrevious.Value)
            || "([{".Contains(beforePrevious.Value);
        // "gut - und" is a dash and wants a space, "E-" or "und/" join the next word.
        if ("-/–".Contains(previous))
        {
            return previousStandsAlone;
        }
        // A straight or curly quote opens when nothing or a space comes before it.
        if ("\"'“‘«»".Contains(previous))
        {
            return !previousStandsAlone;
        }
        return true;
    }

    // MARK: - Capitalization

    private static string UppercasingFirstLetter(string text)
    {
        if (text.Length == 0 || !TextChars.IsLowercaseLetter(text[0])) return text;
        return TextChars.UppercaseFirst(text);
    }

    /// <summary>
    /// "Und dann" becomes "und dann" after "Ich gehe heim". Nouns keep their capital,
    /// and so do "Sie", "Ihr" and "I", whose meaning depends on it.
    /// </summary>
    private static string LowercasingFunctionWord(string text)
    {
        var word = new string(text.TakeWhile(char.IsLetter).ToArray());
        if (word.Length == 0 || !char.IsUpper(word[0])
            || !word.Skip(1).All(char.IsLower)
            || !FunctionWords.Contains(word.ToLowerInvariant()))
        {
            return text;
        }
        return TextChars.LowercaseFirst(text);
    }

    public static readonly HashSet<string> FunctionWords =
    [
        // German
        "aber", "als", "also", "am", "an", "auch", "auf", "aus", "außerdem", "bei", "bin", "bis", "bitte",
        "da", "dabei", "dafür", "damit", "danach", "dann", "darum", "das", "dass", "dein", "deine", "dem",
        "den", "denn", "der", "des", "deshalb", "die", "dies", "diese", "diesem", "diesen", "dieser", "dieses",
        "doch", "du", "durch", "eigentlich", "ein", "eine", "einem", "einen", "einer", "eines", "er", "es",
        "etwa", "falls", "für", "gegen", "gerade", "gern", "gerne", "heute", "hier", "ich", "im", "in", "ins",
        "ist", "ja", "jetzt", "kein", "keine", "man", "mein", "meine", "mit", "nach", "natürlich", "nicht",
        "noch", "nun", "nur", "ob", "oder", "ohne", "schon", "sehr", "sind", "so", "sonst", "sowie", "statt",
        "trotzdem", "um", "und", "uns", "unser", "unsere", "unter", "vielleicht", "vom", "von", "vor", "war",
        "was", "weil", "wenn", "wer", "wie", "wir", "wo", "zu", "zum", "zur", "zwar",
        // English
        "a", "about", "after", "also", "and", "are", "as", "at", "be", "because", "but", "by", "for", "from",
        "if", "is", "it", "its", "just", "maybe", "my", "not", "of", "on", "or", "our", "than", "that", "the",
        "then", "there", "these", "they", "this", "those", "to", "we", "what", "when", "where", "which",
        "while", "who", "with", "you", "your",
    ];
}
