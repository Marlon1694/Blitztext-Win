using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Blitztext.Core.Text;

// Local cleanup of a raw transcript before it is pasted or handed to a language model.
//
// Order matters: hesitation sounds go first so they can never be read as part of a term,
// then the user's fixed replacements, then words that clearly mean one of the user's
// custom terms get the user's spelling, and finally stutters and stray punctuation are
// tidied. Everything runs on the device in plain string code and only touches the words
// it changes; spacing and line breaks elsewhere stay exactly as dictated.

/// <summary>A fixed rule: whenever <see cref="Spoken"/> appears as whole words, write <see cref="Written"/> instead.</summary>
public sealed record TermReplacement(string Spoken, string Written)
{
    public string Id => Spoken.ToLowerInvariant();
}

public sealed class TranscriptPostProcessor
{
    public IReadOnlyList<string> CustomTerms { get; init; } = [];
    public IReadOnlyList<TermReplacement> Replacements { get; set; } = [];
    public bool CorrectCustomTerms { get; init; } = true;
    public bool RemoveFillerWords { get; init; } = true;
    public string Language { get; set; } = "de";

    /// <summary>
    /// Minimum <see cref="TermSimilarity"/> for a fuzzy term correction. On that scale the misheard
    /// "Clawd" scores 0.82 against "Claude", while real words a dictionary might not know
    /// stay below 0.67 ("glaube" and "Klause" against "Claude", "Kopie" 0.58 against
    /// "Copper"); 0.74 sits in the middle of that gap. Exact matches (same letters) always apply.
    /// </summary>
    public double CorrectionThreshold { get; init; } = 0.74;

    /// <summary>Returns true when a single word is a correctly spelled dictionary word.</summary>
    public Func<string, bool> IsDictionaryWord { get; set; } = _ => false;

    public static readonly TranscriptPostProcessor PassThrough = new() { CorrectCustomTerms = false, RemoveFillerWords = false };

    public string Process(string text)
    {
        var result = text;
        if (RemoveFillerWords)
        {
            result = RemoveFillers(result, Language);
        }
        // Fixed rules first, so the fuzzy matcher sees the intended spelling.
        result = ApplyReplacements(Replacements, result);
        if (CorrectCustomTerms)
        {
            result = ApplyCustomTerms(result, CustomTerms, CorrectionThreshold, IsDictionaryWord);
        }
        return Normalize(result, text);
    }

    // MARK: - Replacements

    /// <summary>
    /// Case-insensitive, whole words only; spaces and hyphens between the spoken words
    /// may vary ("n acht n", "N-acht-N"). Longer rules win over shorter overlapping ones.
    /// </summary>
    public static string ApplyReplacements(IEnumerable<TermReplacement> replacements, string text)
    {
        var rules = replacements
            .Select(r => (Spoken: r.Spoken.Trim(), r.Written))
            .Where(r => r.Spoken.Length > 0)
            .OrderByDescending(r => r.Spoken.Length)
            .ToList();
        if (rules.Count == 0) return text;

        var result = text;
        foreach (var rule in rules)
        {
            var words = Regex.Split(rule.Spoken, @"[\s\-]+")
                .Where(w => w.Length > 0)
                .Select(Regex.Escape)
                .ToList();
            if (words.Count == 0) continue;
            var pattern = @"(?<![\p{L}\p{N}])" + string.Join(@"[\s\-]+", words) + @"(?![\p{L}\p{N}])";
            var written = rule.Written;
            result = Regex.Replace(result, pattern, _ => written, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        return result;
    }

    // MARK: - Custom Terms

    /// <summary>
    /// Recognizers split names ("Hub Spot"), spell digits out ("n acht n") and write what
    /// they hear ("Clawd"). Up to three neighbouring words are tried as one candidate.
    /// </summary>
    private const int MaxWordsPerCandidate = 3;

    /// <summary>
    /// With fewer letters one changed sound already makes another word ("Jams" vs. "Jamf"),
    /// so shorter words and terms are only corrected when the letters are the same.
    /// </summary>
    private const int MinimumFuzzyLength = 5;

    /// <summary>
    /// Gives every word or group of words that clearly means a custom term the term's
    /// spelling. Candidates are scored with <see cref="TermSimilarity"/>; the best ones win, and on a tie
    /// the longer span. A fuzzy match never changes real dictionary words, so "Cloud" stays
    /// "Cloud" next to the term "Claude". Punctuation and whitespace outside the replaced
    /// words stay untouched.
    /// </summary>
    public static string ApplyCustomTerms(
        string text,
        IEnumerable<string> customTerms,
        double threshold,
        Func<string, bool>? isDictionaryWord = null)
    {
        isDictionaryWord ??= _ => false;
        var matcher = new TermMatcher(customTerms);
        if (matcher.IsEmpty) return text;
        var tokens = Tokenize(text);
        var keys = tokens.Select(t => MatchKey(text.AsSpan(t.CoreStart, t.CoreEnd - t.CoreStart))).ToList();

        var candidates = new List<TermCandidate>();
        for (var first = 0; first < tokens.Count; first++)
        {
            if (keys[first].Length == 0) continue;
            var key = new List<int>();
            for (var last = first; last < Math.Min(first + MaxWordsPerCandidate, tokens.Count); last++)
            {
                if (last > first)
                {
                    // Punctuation or a line break between two words keeps them apart.
                    var previous = tokens[last - 1];
                    var current = tokens[last];
                    if (previous.HasTrailingPunctuation || current.HasLeadingPunctuation
                        || current.FollowsLineBreak || keys[last].Length == 0)
                    {
                        break;
                    }
                }
                key.AddRange(keys[last]);
                var match = matcher.BestMatch(key.ToArray(), last - first + 1, threshold);
                if (match is not { } found || !(found.IsExact || found.Score >= threshold)) continue;
                candidates.Add(new TermCandidate(first, last, found));
            }
        }
        if (candidates.Count == 0) return text;

        candidates.Sort((lhs, rhs) =>
        {
            if (lhs.Match.Score != rhs.Match.Score) return rhs.Match.Score.CompareTo(lhs.Match.Score);
            if (lhs.WordCount != rhs.WordCount) return rhs.WordCount.CompareTo(lhs.WordCount);
            return lhs.First.CompareTo(rhs.First);
        });

        var claimed = new bool[tokens.Count];
        var knownWords = new Dictionary<string, bool>();
        bool IsKnown(string word)
        {
            if (knownWords.TryGetValue(word, out var known)) return known;
            known = isDictionaryWord(word);
            knownWords[word] = known;
            return known;
        }

        var edits = new List<(int Start, int End, string Term)>();
        foreach (var candidate in candidates)
        {
            var span = Enumerable.Range(candidate.First, candidate.WordCount).ToList();
            if (span.Any(i => claimed[i])) continue;
            // Every candidate taken here claims its words, even when it changes nothing (the
            // term is already right, or the words are real ones), so no weaker candidate
            // around it can swallow them ("Hab Spot an" after "Hab Spot" was kept).
            foreach (var index in span) claimed[index] = true;
            // Fuzzy matches only change unknown words. Several words count as known when
            // written as one they are a word ("Black Box") or when each of them is ("über
            // den"); same letters as the term ("Hub Spot") still join, being exact.
            if (!candidate.Match.IsExact)
            {
                var words = span.Select(i => text[tokens[i].CoreStart..tokens[i].CoreEnd]).ToList();
                if (IsKnown(DictionaryForm(words)) || words.All(IsKnown)) continue;
            }
            var start = tokens[candidate.First].CoreStart;
            var end = tokens[candidate.Last].CoreEnd;
            var term = matcher.Terms[candidate.Match.Term];
            if (!string.Equals(text[start..end], term, StringComparison.Ordinal))
            {
                edits.Add((start, end, term));
            }
        }
        if (edits.Count == 0) return text;

        edits.Sort((a, b) => a.Start.CompareTo(b.Start));
        var result = new StringBuilder();
        var position = 0;
        foreach (var edit in edits)
        {
            result.Append(text, position, edit.Start - position);
            result.Append(edit.Term);
            position = edit.End;
        }
        result.Append(text, position, text.Length - position);
        return result.ToString();
    }

    /// <summary>
    /// How surely <paramref name="candidate"/> (one or more words) means <paramref name="term"/>, from 0 to 1.
    ///
    /// 1 means the same letters once case, umlaut spelling (ä/ae, ß/ss), spaces and
    /// punctuation are ignored; digits in the term may also be spoken in German or English
    /// ("n acht n", "n eight n" for "n8n"). Otherwise the score is the geometric mean of
    /// two similarities, each <c>1 - edits / longer length</c>: spelling (Damerau-Levenshtein
    /// distance of the letters) and sound (the same distance of the phonetic codes). The
    /// geometric mean needs both to agree: "Kobber" sounds like "Copper" (sound 1.0) but
    /// half its letters differ (spelling 0.5), so it scores 0.71 instead of an average of 0.75.
    ///
    /// The score is 0 when fuzzy matching makes no sense: fewer than five letters, digits,
    /// a different first sound (recognizers rarely miss it), the term plus an ending
    /// ("Magnussens" is already right) or only the start of the term ("Magnus" is a name of
    /// its own; one missing letter as in "Claud" is still allowed). Several words must also
    /// start with the term's first letter: a split term keeps it, a word placed in front
    /// ("in Hubspott") does not, even when that word starts with the same sound.
    /// </summary>
    public static double TermSimilarity(string candidate, string term)
    {
        var words = candidate.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        return new TermMatcher([term]).BestMatch(MatchKey(candidate), words, 0)?.Score ?? 0;
    }

    /// <summary>Similarity of the plain letters, 1 for "Müller" and "MUELLER".</summary>
    public static double SpellingSimilarity(string lhs, string rhs) => Similarity(MatchKey(lhs), MatchKey(rhs));

    /// <summary>Similarity of the phonetic codes, 1 for "Kopper" and "Copper".</summary>
    public static double SoundSimilarity(string lhs, string rhs) =>
        Similarity(SoundCode(MatchKey(lhs)), SoundCode(MatchKey(rhs)));

    private static double Similarity(int[] lhs, int[] rhs)
    {
        var length = Math.Max(lhs.Length, rhs.Length);
        if (length == 0) return 1;
        return 1 - (double)EditDistance(lhs, rhs) / length;
    }

    private static readonly string[] GermanDigitNames = ["null", "eins", "zwei", "drei", "vier", "fuenf", "sechs", "sieben", "acht", "neun"];
    private static readonly string[] EnglishDigitNames = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine"];

    private readonly record struct Match(int Term, double Score, bool IsExact);

    private readonly record struct TermCandidate(int First, int Last, Match Match)
    {
        public int WordCount => Last - First + 1;
    }

    /// <summary>
    /// The custom terms prepared once per call: every spelling a term may be dictated in,
    /// a lookup for exact matches and the fuzzy spellings grouped by their first sound.
    /// </summary>
    private sealed class TermMatcher
    {
        private readonly record struct Spelling(int Term, int[] Key, int[] Code);

        public List<string> Terms { get; } = [];
        private readonly Dictionary<int[], int> _exact = new(SequenceComparer.Instance);
        private readonly Dictionary<int, List<Spelling>> _fuzzyByFirstSound = [];

        public bool IsEmpty => Terms.Count == 0;

        public TermMatcher(IEnumerable<string> rawTerms)
        {
            foreach (var raw in rawTerms)
            {
                var term = raw.Trim();
                var key = MatchKey(term);
                if (key.Length == 0 || Terms.Contains(term)) continue;
                var index = Terms.Count;
                Terms.Add(term);
                foreach (var spelling in SpokenForms(key))
                {
                    _exact.TryAdd(spelling, index);
                    if (!AllowsFuzzyMatch(spelling)) continue;
                    var code = SoundCode(spelling);
                    if (code.Length == 0) continue;
                    if (!_fuzzyByFirstSound.TryGetValue(code[0], out var list))
                    {
                        list = [];
                        _fuzzyByFirstSound[code[0]] = list;
                    }
                    list.Add(new Spelling(index, spelling, code));
                }
            }
        }

        /// <summary>The key itself plus, when it contains digits, the digits spelled out in German and in English.</summary>
        private static IEnumerable<int[]> SpokenForms(int[] key)
        {
            yield return key;
            if (!key.Any(IsAsciiDigit)) yield break;
            foreach (var names in new[] { GermanDigitNames, EnglishDigitNames })
            {
                yield return key.SelectMany(value => IsAsciiDigit(value)
                    ? names[value - 48].Select(c => (int)c)
                    : [value]).ToArray();
            }
        }

        /// <summary>
        /// The best term for a candidate key made of <paramref name="words"/> words. Fuzzy scores below
        /// <paramref name="minimumScore"/> may be cut short and come back as null, so pass 0 to always get the true score.
        /// </summary>
        public Match? BestMatch(int[] key, int words, double minimumScore)
        {
            if (key.Length == 0) return null;
            if (_exact.TryGetValue(key, out var exactTerm)) return new Match(exactTerm, 1, true);
            if (!AllowsFuzzyMatch(key)) return null;
            var code = SoundCode(key);
            if (code.Length == 0 || !_fuzzyByFirstSound.TryGetValue(code[0], out var spellings)) return null;

            Match? best = null;
            var bestLength = 0;
            foreach (var spelling in spellings)
            {
                if (words != 1 && spelling.Key[0] != key[0]) continue;
                var score = FuzzyScore(key, code, spelling.Key, spelling.Code, minimumScore);
                if (score <= 0) continue;
                // Closest first; on a tie the longer term, then the one listed first.
                if (best is { } current
                    && (score < current.Score || (score == current.Score && spelling.Key.Length <= bestLength)))
                {
                    continue;
                }
                best = new Match(spelling.Term, score, false);
                bestLength = spelling.Key.Length;
            }
            return best;
        }
    }

    private static bool IsAsciiDigit(int value) => value is >= 48 and <= 57;

    /// <summary>Digits are never fuzzy: "Web2" is not a misheard "Web3".</summary>
    private static bool AllowsFuzzyMatch(int[] key) => key.Length >= MinimumFuzzyLength && !key.Any(IsAsciiDigit);

    /// <summary>See <see cref="TermSimilarity"/>. Returns 0 as soon as the score cannot reach <paramref name="minimumScore"/>.</summary>
    private static double FuzzyScore(int[] candidate, int[] candidateCode, int[] term, int[] termCode, double minimumScore)
    {
        if (candidateCode.Length == 0 || termCode.Length == 0 || candidateCode[0] != termCode[0]) return 0;

        // score = sqrt(spelling * sound), so each part must reach minimumScore² on its own.
        // Cheap bounds come first: this runs for every candidate and term.
        var needed = Math.Max(minimumScore, 0) * Math.Max(minimumScore, 0);
        var length = Math.Max(candidate.Length, term.Length);
        if (Math.Abs(candidate.Length - term.Length) > AllowedEdits(length, needed)) return 0;

        var codeLength = Math.Max(candidateCode.Length, termCode.Length);
        var soundLimit = AllowedEdits(codeLength, needed);
        var soundEdits = EditDistance(candidateCode, termCode, soundLimit);
        if (soundEdits > soundLimit || soundEdits >= codeLength) return 0;
        var sound = 1 - (double)soundEdits / codeLength;

        var spellingLimit = AllowedEdits(length, needed / sound);
        var spellingEdits = EditDistance(candidate, term, spellingLimit);
        if (spellingEdits > spellingLimit || spellingEdits >= length) return 0;
        var spelling = 1 - (double)spellingEdits / length;

        if (candidate.Length > term.Length && candidate.AsSpan().StartsWith(term)) return 0;
        if (term.Length - candidate.Length > 1 && term.AsSpan().StartsWith(candidate)) return 0;
        return Math.Sqrt(spelling * sound);
    }

    /// <summary>Most edits a string of <paramref name="length"/> may have and still reach <paramref name="similarity"/>.</summary>
    private static int AllowedEdits(int length, double similarity) =>
        Math.Max((int)Math.Floor((1 - similarity) * length + 1e-9), 0);

    /// <summary>Several words written as one the way a dictionary expects ("Black Box" -> "Blackbox").</summary>
    private static string DictionaryForm(IReadOnlyList<string> words)
    {
        if (words.Count == 0) return "";
        return words[0] + string.Concat(words.Skip(1).Select(w => w.ToLowerInvariant()));
    }

    // MARK: - Spelling and Sound

    /// <summary>
    /// Letters and digits only, lowercased, umlauts spelled out (ä -> ae, ß -> ss) and other
    /// accents dropped, so "Müller", "MUELLER" and "Mül-ler" compare equal.
    /// </summary>
    internal static int[] MatchKey(ReadOnlySpan<char> text)
    {
        var key = new List<int>(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            var value = rune.Value;
            switch (value)
            {
                case >= 'a' and <= 'z':
                case >= '0' and <= '9':
                    key.Add(value);
                    break;
                case >= 'A' and <= 'Z':
                    key.Add(value + 32);
                    break;
                case 'ä' or 'Ä':
                    key.Add(97); key.Add(101);
                    break;
                case 'ö' or 'Ö':
                    key.Add(111); key.Add(101);
                    break;
                case 'ü' or 'Ü':
                    key.Add(117); key.Add(101);
                    break;
                case 'ß' or 'ẞ':
                    key.Add(115); key.Add(115);
                    break;
                case 0x308 when key.Count > 0 && key[^1] is 97 or 111 or 117:
                    key.Add(101); // a decomposed umlaut, "u" followed by a combining diaeresis
                    break;
                default:
                    if (rune.IsAscii || !IsAlphabetic(rune)) continue;
                    var folded = Fold(rune);
                    foreach (var foldedRune in folded.EnumerateRunes())
                    {
                        if (IsAlphabetic(foldedRune)) key.Add(foldedRune.Value);
                    }
                    break;
            }
        }
        return key.ToArray();
    }

    internal static int[] MatchKey(string text) => MatchKey(text.AsSpan());

    private static bool IsAlphabetic(Rune rune) =>
        Rune.IsLetter(rune) || Rune.GetUnicodeCategory(rune) == UnicodeCategory.LetterNumber;

    /// <summary>Lowercased without diacritics, like Foundation's diacritic-insensitive folding.</summary>
    private static string Fold(Rune rune)
    {
        var lower = Rune.ToLowerInvariant(rune).ToString().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(lower.Length);
        foreach (var c in lower)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) builder.Append(c);
        }
        return builder.ToString() switch
        {
            "ø" => "o",
            "đ" => "d",
            "ł" => "l",
            "ħ" => "h",
            var other => other,
        };
    }

    /// <summary>
    /// Cologne phonetics: a sound code built for German names, so "Meier" and "Mayr" or
    /// "Kopper" and "Copper" get the same code.
    /// </summary>
    public static string ColognePhonetics(string word) =>
        string.Concat(CologneCode(MatchKey(word)).Select(d => d.ToString(CultureInfo.InvariantCulture)));

    /// <summary>
    /// The code the matcher compares: Cologne phonetics, except that a "w" after a vowel and
    /// before a consonant or the end is read as part of the vowel. That is how English names
    /// ("Shaw", "Brown") and recognizer spellings like "Clawd" sound, and northern German
    /// names too ("Pankow"). Plain Cologne codes would hear an extra "v" there.
    /// </summary>
    private static int[] SoundCode(int[] key)
    {
        static bool IsVowel(int? letter) => letter is { } value && LetterSet.Vowels.Contains(value);
        var letters = new List<int>(key.Length);
        for (var index = 0; index < key.Length; index++)
        {
            var letter = key[index];
            int? next = index + 1 < key.Length ? key[index + 1] : null;
            int? last = letters.Count > 0 ? letters[^1] : null;
            if (letter == 119 && IsVowel(last) && !IsVowel(next)) continue; // w
            letters.Add(letter);
        }
        return CologneCode(letters.ToArray());
    }

    /// <summary>
    /// Letters map to digits by sound (vowels 0, b/p 1, d/t 2, f/v/w 3, g/k/q 4, l 5, m/n 6,
    /// r 7, s/z 8; c, d, t, p and x depend on their neighbours, h is silent). Repeated digits
    /// count once and vowels only at the start. Letters outside a-z and digits are skipped.
    /// Umlauts arrive spelled out (ä -> ae), which only departs from the published rules for
    /// a "c" right before an umlaut.
    /// </summary>
    private static int[] CologneCode(int[] key)
    {
        var letters = key.Where(v => v is >= 97 and <= 122).ToArray();
        var code = new List<int>(letters.Length);
        int? lastDigit = null;
        void Emit(int digit)
        {
            if (digit != lastDigit && (digit != 0 || code.Count == 0)) code.Add(digit);
            lastDigit = digit;
        }

        for (var index = 0; index < letters.Length; index++)
        {
            // 0 stands for "no letter" and is in none of the sets.
            var previous = index > 0 ? letters[index - 1] : 0;
            var next = index + 1 < letters.Length ? letters[index + 1] : 0;

            switch ((char)letters[index])
            {
                case 'a' or 'e' or 'i' or 'j' or 'o' or 'u' or 'y': Emit(0); break;
                case 'b': Emit(1); break;
                case 'p': Emit(next == 104 ? 3 : 1); break; // h
                case 'd' or 't': Emit(LetterSet.Csz.Contains(next) ? 8 : 2); break;
                case 'f' or 'v' or 'w': Emit(3); break;
                case 'g' or 'k' or 'q': Emit(4); break;
                case 'c':
                    var hard = index == 0
                        ? LetterSet.Ahkloqrux.Contains(next)
                        : !LetterSet.Sz.Contains(previous) && LetterSet.Ahkoqux.Contains(next);
                    Emit(hard ? 4 : 8);
                    break;
                case 'x':
                    if (!LetterSet.Ckq.Contains(previous)) Emit(4);
                    Emit(8);
                    break;
                case 'l': Emit(5); break;
                case 'm' or 'n': Emit(6); break;
                case 'r': Emit(7); break;
                case 's' or 'z': Emit(8); break;
                default: continue; // h
            }
        }
        return code.ToArray();
    }

    /// <summary>The neighbour sets of the Cologne rules as bit masks over a-z, cheap to test.</summary>
    private readonly struct LetterSet(string letters)
    {
        private readonly uint _mask = letters.Aggregate(0u, (mask, c) => mask | 1u << (c - 97));

        public bool Contains(int letter) => letter is >= 97 and <= 122 && ((_mask >> (letter - 97)) & 1) == 1;

        public static readonly LetterSet Vowels = new("aeiouy");
        public static readonly LetterSet Csz = new("csz");
        public static readonly LetterSet Sz = new("sz");
        public static readonly LetterSet Ckq = new("ckq");
        public static readonly LetterSet Ahkoqux = new("ahkoqux");
        public static readonly LetterSet Ahkloqrux = new("ahkloqrux");
    }

    /// <summary>
    /// Optimal string alignment distance (Damerau-Levenshtein): insertions, deletions,
    /// substitutions and swaps of two neighbouring characters cost 1 each.
    /// </summary>
    public static int EditDistance(string lhs, string rhs) =>
        EditDistance(lhs.EnumerateRunes().Select(r => r.Value).ToArray(), rhs.EnumerateRunes().Select(r => r.Value).ToArray());

    /// <summary>Stops early and returns <c>limit + 1</c> once the distance is certain to exceed <paramref name="limit"/>.</summary>
    private static int EditDistance(int[] a, int[] b, int limit = int.MaxValue - 1)
    {
        if (a.Length == 0 || b.Length == 0) return Math.Max(a.Length, b.Length);
        if (Math.Abs(a.Length - b.Length) > limit) return limit + 1;

        // Three rows of the table in one buffer: two rows up (for swaps), previous, current.
        var width = b.Length + 1;
        Span<int> rows = 3 * width <= 1024 ? stackalloc int[3 * width] : new int[3 * width];
        int twoRowsUp = 0, previous = width, current = 2 * width;
        for (var j = 0; j < width; j++) rows[previous + j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            rows[current] = i;
            var rowMinimum = i;
            for (var j = 1; j < width; j++)
            {
                var substitution = rows[previous + j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1);
                var value = Math.Min(Math.Min(rows[previous + j] + 1, rows[current + j - 1] + 1), substitution);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                {
                    value = Math.Min(value, rows[twoRowsUp + j - 2] + 1);
                }
                rows[current + j] = value;
                rowMinimum = Math.Min(rowMinimum, value);
            }
            if (rowMinimum > limit) return limit + 1;
            (twoRowsUp, previous, current) = (previous, current, twoRowsUp);
        }
        return rows[previous + b.Length];
    }

    // MARK: - Filler Words

    /// <summary>
    /// Hesitation sounds that are no word in any language, removed whatever the language.
    /// They also cover German, whose remaining candidates are all real words: "um" (um 10 Uhr),
    /// "eh" (das ist eh egal), "mhm" and "mh" (yes), "ah", "oh", "na" and "hä" (they carry
    /// meaning). "ähem" is left out too, people write it on purpose.
    /// </summary>
    private static readonly HashSet<string> FillerSounds = ["äh", "ähm", "äm", "öh", "öhm", "ehm", "uhm", "hm"];

    /// <summary>
    /// Sounds that are fillers in one language but words elsewhere: "um" is a German
    /// preposition, "uh" an interjection in some languages. English "er" stays everywhere,
    /// it is a German pronoun and "ER" a hospital ward; "eh" stays as in "nice, eh?".
    /// </summary>
    private static readonly Dictionary<string, HashSet<string>> FillerWordsByLanguage = new()
    {
        ["en"] = ["uh", "um", "erm"],
    };

    /// <summary>
    /// Removes whole-word hesitation sounds, ignoring case and stretched letters ("Ähhm",
    /// "HMMM"), together with a comma right after them. <see cref="Normalize"/> tidies the rest.
    /// </summary>
    public static string RemoveFillers(string text, string language)
    {
        var languageCode = new string(language.ToLowerInvariant().TakeWhile(char.IsLetter).ToArray());
        var fillers = new HashSet<string>(FillerSounds);
        if (FillerWordsByLanguage.TryGetValue(languageCode, out var extra)) fillers.UnionWith(extra);

        var result = new StringBuilder();
        var position = 0;
        var removedAny = false;
        foreach (var token in Tokenize(text))
        {
            if (!token.HasCore) continue;
            var word = text[token.CoreStart..token.CoreEnd];
            if (!word.All(char.IsLetter) || !fillers.Contains(WithoutRepeatedLetters(word.ToLowerInvariant()))) continue;
            var end = token.CoreEnd;
            if (end < text.Length && text[end] == ',') end++;
            result.Append(text, position, token.CoreStart - position);
            position = end;
            removedAny = true;
        }
        if (!removedAny) return text;
        result.Append(text, position, text.Length - position);
        return result.ToString();
    }

    private static string WithoutRepeatedLetters(string word)
    {
        var result = new StringBuilder(word.Length);
        foreach (var c in word)
        {
            if (result.Length == 0 || result[^1] != c) result.Append(c);
        }
        return result.ToString();
    }

    // MARK: - Normalization

    /// <summary>
    /// "ich ich ich ich glaube" -> "ich glaube": three or more equal words in a row (case and
    /// punctuation ignored) are a stutter. The first one stays with its case, the last one
    /// hands over its punctuation. Two in a row are valid German ("das das"), and a sentence
    /// end in between means deliberate repetition ("Nein. Nein. Nein.").
    /// </summary>
    public static string CollapseStutters(string text)
    {
        var tokens = Tokenize(text);
        var words = tokens
            .Select(t => new string(text[t.CoreStart..t.CoreEnd].Where(char.IsLetter).ToArray()).ToLowerInvariant())
            .ToList();

        var result = new StringBuilder();
        var position = 0;
        var collapsedAny = false;
        var first = 0;
        while (first < tokens.Count)
        {
            var last = first;
            while (words[first].Length > 0 && last + 1 < tokens.Count && words[last + 1] == words[first]
                   && !EndsSentence(tokens[last], text))
            {
                last++;
            }
            if (last - first >= 2)
            {
                result.Append(text, position, tokens[first].CoreEnd - position);
                position = tokens[last].CoreEnd;
                collapsedAny = true;
            }
            first = last + 1;
        }
        if (!collapsedAny) return text;
        result.Append(text, position, text.Length - position);
        return result.ToString();
    }

    private static bool EndsSentence(Token token, string text)
    {
        var trailing = text[token.CoreEnd..token.End];
        // An ellipsis is a pause inside the sentence, which is where stutters happen.
        if (trailing.EndsWith("...", StringComparison.Ordinal) || trailing.EndsWith('…')) return false;
        return trailing.Any(c => c is '.' or '!' or '?');
    }

    private static readonly Regex SpaceBeforePunctuation = new(@"[ \t]+([,.])", RegexOptions.Compiled);
    private static readonly Regex RepeatedCommas = new(",{2,}", RegexOptions.Compiled);
    private static readonly Regex LeadingPunctuation = new(@"^[,. \t]+", RegexOptions.Compiled);
    private static readonly Regex RepeatedSpaces = new(@"[ \t]{2,}", RegexOptions.Compiled);

    public static string Normalize(string text, string capitalizeFirstLetterLike)
    {
        var result = CollapseStutters(text);
        // Remove orphaned punctuation left behind by filler removal (", , ok" -> ", ok").
        result = SpaceBeforePunctuation.Replace(result, "$1");
        result = RepeatedCommas.Replace(result, ",");
        result = LeadingPunctuation.Replace(result, "");
        result = RepeatedSpaces.Replace(result, " ");
        result = result.Trim();

        var originalFirstLetter = capitalizeFirstLetterLike.FirstOrDefault(char.IsLetter);
        if (originalFirstLetter != default && char.IsUpper(originalFirstLetter)
            && result.Length > 0 && char.IsLower(result[0]))
        {
            result = TextChars.UppercaseFirst(result);
        }
        return result;
    }

    // MARK: - Tokens

    /// <summary>
    /// Text between whitespace, with the word inside it (core, first to last letter or
    /// digit) kept apart from the punctuation around it, so edits never move a comma,
    /// period or bracket.
    /// </summary>
    private readonly record struct Token(int Start, int End, int CoreStart, int CoreEnd, bool FollowsLineBreak)
    {
        /// <summary>False when the token has no letter or digit, like a lone dash.</summary>
        public bool HasCore => CoreEnd > CoreStart;
        public bool HasLeadingPunctuation => CoreStart > Start;
        public bool HasTrailingPunctuation => CoreEnd < End;
    }

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var index = 0;
        var sawLineBreak = false;
        while (index < text.Length)
        {
            var c = text[index];
            if (char.IsWhiteSpace(c))
            {
                sawLineBreak = sawLineBreak || TextChars.IsNewline(c);
                index++;
                continue;
            }
            var start = index;
            int? coreStart = null;
            var coreEnd = start;
            while (index < text.Length && !char.IsWhiteSpace(text[index]))
            {
                var step = TextChars.Step(text, index);
                if (TextChars.IsLetterOrNumber(text, index) || (coreStart != null && TextChars.IsMark(text[index])))
                {
                    coreStart ??= index;
                    coreEnd = index + step;
                }
                index += step;
            }
            tokens.Add(coreStart is { } cs
                ? new Token(start, index, cs, coreEnd, sawLineBreak)
                : new Token(start, index, start, start, sawLineBreak));
            sawLineBreak = false;
        }
        return tokens;
    }

    /// <summary>Compares int arrays by content, for dictionary keys.</summary>
    private sealed class SequenceComparer : IEqualityComparer<int[]>
    {
        public static readonly SequenceComparer Instance = new();

        public bool Equals(int[]? x, int[]? y) => x is not null && y is not null && x.AsSpan().SequenceEqual(y);

        public int GetHashCode(int[] obj)
        {
            var hash = new HashCode();
            foreach (var value in obj) hash.Add(value);
            return hash.ToHashCode();
        }
    }
}
