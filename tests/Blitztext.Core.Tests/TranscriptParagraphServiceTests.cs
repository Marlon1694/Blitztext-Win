using Blitztext.Core.Text;

namespace Blitztext.Core.Tests;

/// <summary>Ported from the checks in cmagnussen/blitztext-app PR #21.</summary>
public class TranscriptParagraphServiceTests
{
    private static readonly string[] Sentences =
    [
        "Heute schreiben wir einen Brief.",
        "Dieser Brief enthält wichtige Informationen.",
        "Wir schicken ihn morgen weiter.",
        "Die Antwort erwarten wir nächste Woche.",
        "Danach planen wir die nächsten Schritte.",
    ];

    private static string FirstFour => string.Join(' ', Sentences.Take(4));
    private static string Input => string.Join(' ', Sentences);
    private static string Expected => FirstFour + "\n\n" + Sentences[4];

    private static string Format(string text) => TranscriptParagraphService.Format(text, "de");

    [Fact]
    public void BreaksAfterFourSubstantialSentences()
    {
        Assert.Equal(Expected, Format(Input));
        Assert.Equal(FirstFour, Format(FirstFour));
    }

    [Fact]
    public void InterjectionsDoNotCount() =>
        Assert.Equal("Ja. OK. Nein. Gut. " + Expected, Format("Ja. OK. Nein. Gut. " + Input));

    [Fact]
    public void WordCapEndsAtSentenceBoundaryOnly()
    {
        var longSentence = string.Join(' ', Enumerable.Repeat("Wort", 46)) + ".";
        Assert.Equal(longSentence + "\n\nDanach folgt mehr Text.", Format(longSentence + " Danach folgt mehr Text."));
        Assert.Equal(longSentence, Format(longSentence));
    }

    [Fact]
    public void LeavesShortAndEmptyTextAlone()
    {
        Assert.Equal("Hallo 👋, das ist ein Test!", Format("Hallo 👋, das ist ein Test!"));
        Assert.Equal("", TranscriptParagraphService.Format(""));
        Assert.Equal("   \n", TranscriptParagraphService.Format("   \n"));
    }

    [Fact]
    public void KeepsExistingParagraphsAndIsIdempotent()
    {
        var existing = string.Join(' ', Sentences.Take(2)) + "\r\n \r\n" + string.Join(' ', Sentences.Skip(2));
        Assert.Equal(existing, Format(existing));
        Assert.Equal(Expected, Format(Expected));
    }

    [Fact]
    public void GermanAbbreviation()
    {
        var abbreviated = string.Join(' ', new[] { "Dr. Müller schreibt heute einen Brief." }.Concat(Sentences.Take(3)));
        Assert.Equal(abbreviated + "\n\n" + Sentences[4], Format(abbreviated + " " + Sentences[4]));
    }

    [Fact]
    public void AbbreviationsDoNotEndSentences()
    {
        var ranges = TranscriptParagraphService.SentenceRanges("Wir treffen z.B. Dr. Meier am 1. Mai. Danach gehen wir.");
        Assert.Equal(2, ranges.Count);
    }

    [Fact]
    public void PreservesWordsAndPunctuation()
    {
        var large = string.Join(' ', Enumerable.Repeat(Input, 100));
        static string WithoutWhitespace(string s) => new(s.Where(c => !char.IsWhiteSpace(c)).ToArray());
        Assert.Equal(WithoutWhitespace(large), WithoutWhitespace(Format(large)));
    }
}
