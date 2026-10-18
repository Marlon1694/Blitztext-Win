using Blitztext.Core.AI;
using Blitztext.Core.History;
using Blitztext.Core.Settings;
using Blitztext.Core.Text;
using Blitztext.Core.Workflows;

namespace Blitztext.Core.Tests;

public class SettingsTests
{
    [Fact]
    public void RoundTripsEverySection()
    {
        var container = new SettingsContainer();
        container.App.HotkeyMode = HotkeyMode.Toggle;
        container.App.CustomHotkeys[WorkflowType.TextImprover.Key()] = new KeyCombo(ModifierKeys.Ctrl | ModifierKeys.Alt, 0x44, "D");
        container.App.AppModeRules.Add(new AppModeRule { ProcessName = "outlook.exe", AppName = "Outlook", Mode = WorkflowType.TextImprover });
        container.App.WebsiteModeRules.Add(new WebsiteModeRule { Host = "mail.google.com", Mode = WorkflowType.EmojiText });
        container.TextImprovement.CustomTerms.Add("Blackboat");
        container.TextImprovement.Replacements.Add(new TermReplacement("n acht n", "n8n"));
        container.TextImprovement.Tone = TextTone.Formal;
        container.EmojiText.EmojiDensity = EmojiDensity.Viel;
        container.AiProvider = new AIProviderConfig(AIProvider.Anthropic);

        var loaded = SettingsSerializer.Deserialize(SettingsSerializer.Serialize(container));

        Assert.Equal(HotkeyMode.Toggle, loaded.App.HotkeyMode);
        Assert.Equal("Strg + Alt + D", loaded.App.Hotkey(WorkflowType.TextImprover).DisplayLabel);
        Assert.Equal("outlook.exe", loaded.App.AppModeRules.Single().ProcessName);
        Assert.Equal(WorkflowType.EmojiText, loaded.App.WebsiteModeRules.Single().Mode);
        Assert.Equal(["Blackboat"], loaded.TextImprovement.CustomTerms);
        Assert.Equal("n8n", loaded.TextImprovement.Replacements.Single().Written);
        Assert.Equal(TextTone.Formal, loaded.TextImprovement.Tone);
        Assert.Equal(EmojiDensity.Viel, loaded.EmojiText.EmojiDensity);
        Assert.Equal(AIProvider.Anthropic, loaded.AiProvider!.Provider);
        Assert.Equal("claude-opus-5-5", loaded.AiProvider.Model);
    }

    [Fact]
    public void MissingAndUnknownValuesKeepTheirDefaults()
    {
        const string json = """
            {
              "app": { "hotkeyMode": "somethingNew", "historyRetention": "week", "futureSetting": 3 },
              "textImprovement": { "tone": "poetic", "customTerms": ["Jamf"] },
              "emojiText": "not an object"
            }
            """;
        var loaded = SettingsSerializer.Deserialize(json);
        Assert.Equal(HotkeyMode.Hold, loaded.App.HotkeyMode);
        Assert.Equal(HistoryRetention.Week, loaded.App.HistoryRetention);
        Assert.True(loaded.App.RestoreClipboardAfterPaste);
        Assert.Equal(TextTone.Neutral, loaded.TextImprovement.Tone);
        Assert.Equal(["Jamf"], loaded.TextImprovement.CustomTerms);
        Assert.Equal(EmojiDensity.Mittel, loaded.EmojiText.EmojiDensity);
        Assert.Equal(ModePrompts.DampfAblassenDefault, loaded.DampfAblassen.SystemPrompt);
        Assert.Null(loaded.AiProvider);
    }

    [Fact]
    public void BrokenFileGivesDefaults()
    {
        var loaded = SettingsSerializer.Deserialize("{ not json");
        Assert.Equal(HotkeyMode.Hold, loaded.App.HotkeyMode);
        Assert.Equal("de", loaded.Transcription.Language);
    }

    [Fact]
    public void BuiltInShortcutsAreDistinct()
    {
        var combos = WorkflowTypes.All.Select(KeyCombo.Default).ToList();
        Assert.Equal(combos.Count, combos.Select(c => c.Modifiers).Distinct().Count());
        Assert.All(combos, c => Assert.True(c.IsModifierOnly && KeyCombo.ModifierCount(c.Modifiers) >= 2));
        Assert.Equal("Strg + Win", KeyCombo.Default(WorkflowType.TapTalk).DisplayLabel);
    }

    [Fact]
    public void ProtectedTermsIncludeReplacementsOnce()
    {
        var settings = new TextImprovementSettings
        {
            CustomTerms = ["n8n", "HubSpot"],
            Replacements = [new TermReplacement("en acht en", "N8N"), new TermReplacement("kai", "KI")],
        };
        Assert.Equal(["n8n", "HubSpot", "KI"], settings.ProtectedTerms);
        Assert.Contains("exakt so geschrieben werden: n8n, HubSpot, KI", ModePrompts.TextImprovement(settings));
    }

    [Fact]
    public void FieldContextIsQuoted()
    {
        var prompt = ModePrompts.WithFieldContext("Anweisung", "Hallo Anna");
        Assert.StartsWith("Anweisung\n\nKontext aus dem Textfeld", prompt);
        Assert.Contains("\"\"\"\nHallo Anna\n\"\"\"", prompt);
        Assert.Equal("Anweisung", ModePrompts.WithFieldContext("Anweisung", null));
    }
}

public class AIClientTests
{
    [Theory]
    [InlineData("claude-opus-5-5", true)]
    [InlineData("claude-sonnet-5-5", true)]
    [InlineData("claude-haiku-5-5", true)]
    [InlineData("claude-haiku-4-5", false)]
    [InlineData("claude-sonnet-4-5", false)]
    public void EffortOnlyWhereSupported(string model, bool expected) =>
        Assert.Equal(expected, AIClient.SupportsEffort(model));

    [Theory]
    [InlineData("claude-opus-5-5", true)]
    [InlineData("claude-fable-5-1", true)]
    [InlineData("claude-haiku-5-5", false)]
    [InlineData("claude-opus-4-8", false)]
    public void ServerSideFallbackOnlyWhereSupported(string model, bool expected) =>
        Assert.Equal(expected, AIClient.SupportsServerSideFallback(model));

    [Fact]
    public void MasksKeys()
    {
        var store = new InMemoryApiKeyStore();
        store.Save(AIProvider.OpenAI, "  sk-1234567890abcd ");
        Assert.Equal("sk-…abcd", store.MaskedKey(AIProvider.OpenAI));
        Assert.True(store.IsConfigured(new AIProviderConfig(AIProvider.OpenAI)));
        Assert.False(store.IsConfigured(new AIProviderConfig(AIProvider.Mistral)));
        Assert.True(store.IsConfigured(new AIProviderConfig(AIProvider.Custom)));
    }

    [Fact]
    public async Task MissingKeyFailsBeforeAnyRequest()
    {
        var client = new AIClient(new InMemoryApiKeyStore());
        var error = await Assert.ThrowsAsync<AIClientException>(() =>
            client.CompleteAsync("x", "y", new AIProviderConfig(AIProvider.OpenAI)));
        Assert.Contains("OpenAI", error.Message);
    }

    [Fact]
    public async Task UnreachableLocalServerSaysSo()
    {
        var client = new AIClient(new InMemoryApiKeyStore());
        var error = await Assert.ThrowsAsync<AIClientException>(() =>
            client.CompleteAsync("x", "y", new AIProviderConfig(AIProvider.Custom, baseUrl: "http://127.0.0.1:9/v1")));
        Assert.Contains("Keine Verbindung", error.Message);
    }
}

public class HistoryStoreTests
{
    private sealed class XorProtector : IDataProtector
    {
        public byte[] Protect(byte[] data) => data.Select(b => (byte)(b ^ 0x5A)).ToArray();
        public byte[]? Unprotect(byte[] data) => Protect(data);
    }

    [Fact]
    public void KeepsRecentEntriesEncryptedOnDisk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"blitztext-history-{Guid.NewGuid()}");
        try
        {
            var now = DateTimeOffset.Parse("2026-10-18T12:00:00Z");
            var store = new TranscriptHistoryStore(path, new XorProtector(), HistoryRetention.Day, () => now);
            store.Add("  Hallo Welt  ", WorkflowType.TapTalk);
            store.Add("Zweiter", WorkflowType.EmojiText);
            Assert.Equal("Zweiter", store.Entries[0].Text);
            Assert.DoesNotContain("Hallo", File.ReadAllText(path));

            var reloaded = new TranscriptHistoryStore(path, new XorProtector(), HistoryRetention.Day, () => now.AddHours(1));
            Assert.Equal(["Zweiter", "Hallo Welt"], reloaded.Entries.Select(e => e.Text));

            var expired = new TranscriptHistoryStore(path, new XorProtector(), HistoryRetention.Day, () => now.AddDays(2));
            Assert.Empty(expired.Entries);
            Assert.False(File.Exists(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void OffClearsEverything()
    {
        var path = Path.Combine(Path.GetTempPath(), $"blitztext-history-{Guid.NewGuid()}");
        var store = new TranscriptHistoryStore(path, new XorProtector(), HistoryRetention.Week);
        store.Add("Text", WorkflowType.TapTalk);
        store.UpdateRetention(HistoryRetention.Off);
        Assert.Empty(store.Entries);
        Assert.False(File.Exists(path));
        store.Add("Mehr", WorkflowType.TapTalk);
        Assert.Empty(store.Entries);
    }
}
