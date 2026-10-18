using System.Text.Json;
using System.Text.Json.Nodes;
using Blitztext.Core.AI;

namespace Blitztext.Core.Settings;

/// <summary>Everything Blitztext stores in settings.json.</summary>
public sealed class SettingsContainer
{
    public AppSettings App { get; set; } = new();
    public TranscriptionSettings Transcription { get; set; } = new();
    public TextImprovementSettings TextImprovement { get; set; } = new();
    public DampfAblassenSettings DampfAblassen { get; set; } = new();
    public EmojiTextSettings EmojiText { get; set; } = new();
    /// <summary>The connected model provider; the API key itself lives in the Windows Credential Manager.</summary>
    public AIProviderConfig? AiProvider { get; set; }
}

public static class SettingsSerializer
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = null,
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string Serialize(SettingsContainer container) => JsonSerializer.Serialize(container, Options);

    /// <summary>
    /// Reads settings section by section: a section this version cannot read falls back to its
    /// defaults instead of resetting everything.
    /// </summary>
    public static SettingsContainer Deserialize(string json)
    {
        var container = new SettingsContainer();
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            }) as JsonObject;
        }
        catch (JsonException)
        {
            return container;
        }
        if (root is null) return container;

        container.App = Section(root, "app", container.App);
        container.Transcription = Section(root, "transcription", container.Transcription);
        container.TextImprovement = Section(root, "textImprovement", container.TextImprovement);
        container.DampfAblassen = Section(root, "dampfAblassen", container.DampfAblassen);
        container.EmojiText = Section(root, "emojiText", container.EmojiText);
        container.AiProvider = Section<AIProviderConfig?>(root, "aiProvider", null);
        return container;
    }

    private static T Section<T>(JsonObject root, string name, T fallback)
    {
        var node = root.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
        if (node is null) return fallback;
        try
        {
            return node.Deserialize<T>(Options) ?? fallback;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            return fallback;
        }
    }

    public static T Clone<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options)!;
}
