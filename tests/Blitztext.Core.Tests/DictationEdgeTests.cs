using Blitztext.Core.Text;

namespace Blitztext.Core.Tests;

/// <summary>Ported from the macOS app's DictationEdgeTests.</summary>
public class HallucinationFilterTests
{
    [Fact]
    public void CreditsAreAlwaysInvented()
    {
        Assert.True(HallucinationFilter.IsInvented("Untertitel im Auftrag des ZDF, 2021", 5));
        Assert.True(HallucinationFilter.IsInvented("Untertitel der Amara.org-Community", 5));
        Assert.True(HallucinationFilter.IsInvented("Thanks for watching!", 5));
    }

    [Fact]
    public void PoliteWordsOnlyCountOnSilence()
    {
        Assert.True(HallucinationFilter.IsInvented("Thank you.", 0));
        Assert.True(HallucinationFilter.IsInvented("Vielen Dank.", 0.1));
        Assert.False(HallucinationFilter.IsInvented("Vielen Dank.", 1.2));
        Assert.False(HallucinationFilter.IsInvented("Vielen Dank für die Zahlen.", 0));
        Assert.False(HallucinationFilter.IsInvented("1234", 0));
    }

    [Fact]
    public void MeasuresVoicedAudio()
    {
        var random = new Random(7);
        var silence = new float[16_000];
        var roomNoise = Enumerable.Range(0, 16_000).Select(_ => (float)(random.NextDouble() * 0.006 - 0.003)).ToArray();
        var speech = Enumerable.Range(0, 16_000).Select(i => (float)Math.Sin(i / 8.0) * 0.2f).ToArray();
        Assert.Equal(0, HallucinationFilter.VoicedSeconds(silence));
        Assert.Equal(0, HallucinationFilter.VoicedSeconds(roomNoise));
        Assert.True(HallucinationFilter.VoicedSeconds(speech) > 0.9);
    }

    [Fact]
    public void RemovesTrailingCredits()
    {
        Assert.Equal("Wir sehen uns morgen.",
            HallucinationFilter.RemovingTrailingCredits("Wir sehen uns morgen. Untertitel im Auftrag des ZDF, 2021."));
        Assert.Equal("Bis morgen!",
            HallucinationFilter.RemovingTrailingCredits("Bis morgen! Untertitel der Amara.org-Community"));
        Assert.Equal("", HallucinationFilter.RemovingTrailingCredits("Danke fürs Zuschauen!"));
        Assert.Equal("Die Untertitel im Auftrag des ZDF sind gut.",
            HallucinationFilter.RemovingTrailingCredits("Die Untertitel im Auftrag des ZDF sind gut."));
    }
}

public class InsertionFitterTests
{
    [Fact]
    public void AddsSpaceBetweenWords()
    {
        Assert.Equal(" und dann los.", InsertionFitter.Fit("Und dann los.", "Wir starten", ""));
        Assert.Equal(" Weiter geht's.", InsertionFitter.Fit("Weiter geht's.", "Fertig.", ""));
        Assert.Equal("Hallo", InsertionFitter.Fit("Hallo", "Text ", ""));
    }

    [Fact]
    public void CapitalizesAtSentenceStart()
    {
        Assert.Equal("Danke dir.", InsertionFitter.Fit("danke dir.", "", ""));
        Assert.Equal("Danke dir.", InsertionFitter.Fit("danke dir.", "Hallo Anna,\n", ""));
        Assert.Equal("Danke dir.", InsertionFitter.Fit("danke dir.", "Gut so! ", ""));
    }

    [Fact]
    public void KeepsNounsAndPolitePronounsMidSentence()
    {
        Assert.Equal(" Montag passt.", InsertionFitter.Fit("Montag passt.", "Ich glaube,", ""));
        Assert.Equal(" Sie kommen.", InsertionFitter.Fit("Sie kommen.", "Ich hoffe,", ""));
        Assert.Equal(" I agree.", InsertionFitter.Fit("I agree.", "Honestly,", ""));
        Assert.Equal(" API läuft.", InsertionFitter.Fit("API läuft.", "Die", ""));
    }

    [Fact]
    public void DropsPeriodWhereTheSentenceGoesOn()
    {
        Assert.Equal("das Angebot", InsertionFitter.Fit("Das Angebot.", "Ich schicke dir ", " morgen früh."));
        Assert.Equal("Ich komme", InsertionFitter.Fit("Ich komme.", "", "."));
        Assert.Equal("Siehe Liste usw.", InsertionFitter.Fit("Siehe Liste usw.", "", " weiter"));
    }

    [Fact]
    public void AddsSpaceBeforeFollowingWord()
    {
        Assert.Equal("Hallo ", InsertionFitter.Fit("Hallo", "", "Welt"));
        Assert.Equal("Hallo", InsertionFitter.Fit("Hallo", "", " Welt"));
    }

    [Fact]
    public void RespectsBracketsQuotesAndDashes()
    {
        Assert.Equal("Siehe oben", InsertionFitter.Fit("Siehe oben", "(", ")"));
        Assert.Equal("Zitat", InsertionFitter.Fit("Zitat", "Er sagte „", ""));
        Assert.Equal("Zitat", InsertionFitter.Fit("Zitat", "Er sagte \"", ""));
        Assert.Equal(" Weiter", InsertionFitter.Fit("Weiter", "\"Ende\"", ""));
        Assert.Equal(" und weiter", InsertionFitter.Fit("Und weiter", "gut -", ""));
        Assert.Equal("Mail", InsertionFitter.Fit("Mail", "E-", ""));
    }

    [Fact]
    public void HandlesWindowsLineBreaks() =>
        Assert.Equal("Danke dir.", InsertionFitter.Fit("danke dir.", "Hallo Anna,\r\n", ""));
}

public class WebsiteHostTests
{
    [Fact]
    public void NormalizesInput()
    {
        Assert.Equal("mail.google.com", WebsiteHost.Normalized("https://www.Mail.google.com/mail/u/0"));
        Assert.Equal("notion.so", WebsiteHost.Normalized(" notion.so "));
        Assert.Null(WebsiteHost.Normalized("gmail"));
        Assert.Null(WebsiteHost.Normalized("mail google com"));
    }

    [Fact]
    public void MatchesSubdomains()
    {
        Assert.True(WebsiteHost.Matches("mail.google.com", "google.com"));
        Assert.True(WebsiteHost.Matches("mail.google.com", "mail.google.com"));
        Assert.False(WebsiteHost.Matches("notgoogle.com", "google.com"));
    }

    [Fact]
    public void ReadsBrowserAddresses()
    {
        Assert.Equal("mail.google.com", WebsiteHost.HostOfAddress("mail.google.com/mail/u/0/#inbox"));
        Assert.Equal("app.slack.com", WebsiteHost.HostOfAddress("https://app.slack.com/client/T1"));
        Assert.Null(WebsiteHost.HostOfAddress("Suchen oder Adresse eingeben"));
        Assert.Null(WebsiteHost.HostOfAddress("edge://settings"));
    }
}
