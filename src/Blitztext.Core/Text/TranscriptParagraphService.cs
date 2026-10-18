using System.Text;
using System.Text.RegularExpressions;

namespace Blitztext.Core.Text;

/// <summary>
/// Formats transcript whitespace without rewriting words or punctuation: long dictation
/// gets a blank line at a sentence end after 45 words or four substantial sentences.
///
/// Contributed by dan-rot to Blitztext (cmagnussen/blitztext-app PR #21, MIT). The macOS
/// version finds sentences with Apple's NLTokenizer; this one uses plain rules with a list
/// of common abbreviations.
/// </summary>
public static class TranscriptParagraphService
{
    private static readonly Regex ParagraphSeparator = new(@"\r?\n[\t ]*\r?\n(?:[\t ]*\r?\n)*", RegexOptions.Compiled);
    private static readonly Regex Word = new(@"[\p{L}\p{N}\p{M}]+(?:['’\-][\p{L}\p{N}\p{M}]+)*", RegexOptions.Compiled);

    private const string SentenceEnds = ".!?…。！？";
    private const string ClosingMarks = "\"'“”„«»‹›‘’)]}";

    /// <summary>Words whose period does not end a sentence, lowercased without the period.</summary>
    private static readonly HashSet<string> Abbreviations =
    [
        // German
        "dr", "prof", "hr", "fr", "frl", "nr", "str", "bzw", "ca", "vgl", "usw", "etc", "ggf", "evtl",
        "inkl", "exkl", "zzgl", "abs", "art", "bd", "bsp", "geb", "gest", "hrsg", "jh", "jr", "sen",
        "mio", "mrd", "tel", "abt", "dipl", "ing", "mag", "verh", "u.a", "z.b", "d.h", "u.ä", "o.ä",
        "z.t", "s.o", "s.u", "v.a", "i.d.r", "u.u", "m.e", "u.v.m", "bspw", "sog", "allg", "betr",
        // English
        "mr", "mrs", "ms", "st", "vs", "e.g", "i.e", "approx", "dept", "est", "inc", "ltd", "co", "corp",
        "jan", "feb", "mar", "apr", "jun", "jul", "aug", "sep", "sept", "oct", "nov", "dec",
    ];

    public static string Format(string text, string language = "")
    {
        if (text.Length == 0) return text;
        // Keep existing paragraph separators exactly, and restart counting after each one.
        var result = new StringBuilder(text.Length + 16);
        var start = 0;
        foreach (Match match in ParagraphSeparator.Matches(text))
        {
            result.Append(FormatParagraph(text[start..match.Index]));
            result.Append(match.Value);
            start = match.Index + match.Length;
        }
        result.Append(FormatParagraph(text[start..]));
        return result.ToString();
    }

    private static string FormatParagraph(string text)
    {
        var ranges = SentenceRanges(text);
        if (ranges.Count <= 1) return text;

        var result = new StringBuilder(text.Length + 8);
        var copyStart = 0;
        var wordCount = 0;
        var substantialSentences = 0;
        for (var index = 0; index < ranges.Count - 1; index++)
        {
            var (rangeStart, rangeEnd) = ranges[index];
            var sentenceWords = Word.Matches(text, rangeStart).TakeWhile(m => m.Index < rangeEnd).Count();
            wordCount += sentenceWords;
            if (sentenceWords >= 4) substantialSentences++;
            if (wordCount < 45 && substantialSentences < 4) continue;

            var end = rangeEnd;
            while (end > rangeStart && char.IsWhiteSpace(text[end - 1])) end--;
            // A boundary at a line break alone must not split an unfinished sentence.
            var ending = text[rangeStart..end].Trim(ClosingMarks.ToCharArray());
            if (ending.Length == 0 || !SentenceEnds.Contains(ending[^1])) continue;

            var next = ranges[index + 1].Start;
            while (next < text.Length && char.IsWhiteSpace(text[next])) next++;
            if (next < end || !text[end..next].All(char.IsWhiteSpace)) continue;
            result.Append(text, copyStart, end - copyStart);
            result.Append("\n\n");
            copyStart = next;
            wordCount = 0;
            substantialSentences = 0;
        }
        result.Append(text, copyStart, text.Length - copyStart);
        return result.ToString();
    }

    /// <summary>
    /// Sentences as [start, end) ranges; each range keeps the whitespace after it, so the
    /// ranges cover the whole text. A sentence ends after ".", "!", "?" or "…" (plus closing
    /// quotes or brackets) when whitespace and no lowercase word follow, and at a line break.
    /// Abbreviations, initials and ordinal numbers ("am 1. Mai") keep the sentence going.
    /// </summary>
    internal static List<(int Start, int End)> SentenceRanges(string text)
    {
        var ranges = new List<(int Start, int End)>();
        var start = 0;
        var index = 0;
        while (index < text.Length)
        {
            var c = text[index];
            int? boundary = null;
            if (TextChars.IsNewline(c))
            {
                boundary = index + 1;
            }
            else if (SentenceEnds.Contains(c))
            {
                var after = index + 1;
                while (after < text.Length && SentenceEnds.Contains(text[after])) after++;
                while (after < text.Length && ClosingMarks.Contains(text[after])) after++;
                if (after < text.Length && char.IsWhiteSpace(text[after]) && !IsAbbreviationPeriod(text, index, c))
                {
                    var next = after;
                    while (next < text.Length && char.IsWhiteSpace(text[next]) && !TextChars.IsNewline(text[next])) next++;
                    if (next >= text.Length || TextChars.IsNewline(text[next]) || !TextChars.IsLowercaseLetter(text[next]))
                    {
                        boundary = after;
                    }
                }
                index = after - 1;
            }

            if (boundary is { } b)
            {
                var next = b;
                while (next < text.Length && char.IsWhiteSpace(text[next])) next++;
                if (next >= text.Length) break;
                ranges.Add((start, next));
                start = next;
                index = next;
                continue;
            }
            index++;
        }
        if (start < text.Length || ranges.Count == 0)
        {
            ranges.Add((start, text.Length));
        }
        return ranges;
    }

    private static bool IsAbbreviationPeriod(string text, int periodIndex, char mark)
    {
        if (mark != '.') return false;
        var wordStart = periodIndex;
        while (wordStart > 0 && !char.IsWhiteSpace(text[wordStart - 1]) && !"([{\"„“'".Contains(text[wordStart - 1]))
        {
            wordStart--;
        }
        var word = text[wordStart..periodIndex];
        if (word.Length == 0) return false;
        // Initials ("J. R. R. Tolkien") and ordinals ("am 1. Mai").
        if (word.Length == 1 && char.IsLetter(word[0])) return true;
        if (word.All(char.IsDigit) && word.Length <= 2) return true;
        return Abbreviations.Contains(word.ToLowerInvariant());
    }
}
