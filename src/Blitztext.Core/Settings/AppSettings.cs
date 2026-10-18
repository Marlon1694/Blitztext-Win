using System.Text.Json.Serialization;
using Blitztext.Core.AI;
using Blitztext.Core.Text;
using Blitztext.Core.Workflows;

namespace Blitztext.Core.Settings;

[JsonConverter(typeof(TolerantEnumConverter<HotkeyMode>))]
public enum HotkeyMode
{
    /// <summary>Tasten halten = aufnehmen, loslassen = stoppen.</summary>
    Hold,
    /// <summary>Einmal drücken = starten, nochmal oder Escape = stoppen.</summary>
    Toggle,
}

[JsonConverter(typeof(TolerantEnumConverter<HistoryRetention>))]
public enum HistoryRetention
{
    Day,
    Off,
    Week,
}

public static class SettingsDisplay
{
    public static string DisplayName(this HotkeyMode mode) => mode == HotkeyMode.Hold ? "Halten" : "Drücken";

    public static string Description(this HotkeyMode mode) => mode == HotkeyMode.Hold
        ? "Tasten halten zum Aufnehmen, loslassen zum Stoppen"
        : "Einmal drücken zum Starten, nochmal oder Escape zum Stoppen";

    public static string DisplayName(this HistoryRetention retention) => retention switch
    {
        HistoryRetention.Off => "Aus",
        HistoryRetention.Day => "24 Std.",
        HistoryRetention.Week => "7 Tage",
        _ => "",
    };

    public static TimeSpan? MaxAge(this HistoryRetention retention) => retention switch
    {
        HistoryRetention.Day => TimeSpan.FromHours(24),
        HistoryRetention.Week => TimeSpan.FromDays(7),
        _ => null,
    };
}

/// <summary>One rule: dictating with the Blitztext shortcut in this app uses <see cref="Mode"/> instead.</summary>
public sealed record AppModeRule
{
    /// <summary>Executable name, lowercased, e.g. "outlook.exe".</summary>
    public string ProcessName { get; init; } = "";
    public string AppName { get; init; } = "";
    public WorkflowType Mode { get; set; } = WorkflowType.TextImprover;
}

/// <summary>The same for a website in a browser, e.g. Gmail. <see cref="Host"/> matches subdomains too.</summary>
public sealed record WebsiteModeRule
{
    public string Host { get; init; } = "";
    public WorkflowType Mode { get; set; } = WorkflowType.TextImprover;
}

public sealed class AppSettings
{
    public HotkeyMode HotkeyMode { get; set; } = HotkeyMode.Hold;
    public bool HasSeenOnboarding { get; set; }
    public string SelectedLocalTranscriptionModelName { get; set; } = "";
    /// <summary>Optional own shortcuts per mode (keyed by <see cref="WorkflowTypes.Key"/>); they replace the built-in one.</summary>
    public Dictionary<string, KeyCombo> CustomHotkeys { get; set; } = [];
    public bool RestoreClipboardAfterPaste { get; set; } = true;
    public bool CorrectCustomTermsEnabled { get; set; } = true;
    public bool RemoveFillerWordsEnabled { get; set; } = true;
    public HistoryRetention HistoryRetention { get; set; } = HistoryRetention.Day;
    /// <summary><c>WhatsNew.Version</c> the user already confirmed; null before the first note.</summary>
    public string? LastSeenWhatsNewVersion { get; set; }
    /// <summary>"Modus je App": the Blitztext shortcut uses another mode in these apps.</summary>
    public List<AppModeRule> AppModeRules { get; set; } = [];
    /// <summary>The same for websites in a browser, e.g. Gmail. Wins over a rule for the browser.</summary>
    public List<WebsiteModeRule> WebsiteModeRules { get; set; } = [];
    /// <summary>Spaces and capitals of a dictation follow the text around the cursor.</summary>
    public bool FitToSurroundingText { get; set; } = true;
    /// <summary>The AI modes get the text around the cursor as context.</summary>
    public bool AiSeesFieldContext { get; set; }
    /// <summary>The sound output is muted while Blitztext records.</summary>
    public bool MuteOutputWhileRecording { get; set; } = true;
    /// <summary>Long word-for-word dictation gets paragraph breaks at sentence ends.</summary>
    public bool AutomaticParagraphs { get; set; }
    /// <summary>A small lilac pill at the bottom of the screen while recording and processing.</summary>
    public bool ShowsRecordingIndicator { get; set; } = true;
    /// <summary>Microphone endpoint ID; empty for the Windows default.</summary>
    public string MicrophoneDeviceId { get; set; } = "";
    /// <summary>Look for new versions on the GitHub releases page.</summary>
    public bool CheckForUpdates { get; set; } = true;

    public KeyCombo? CustomHotkey(WorkflowType type) =>
        CustomHotkeys.TryGetValue(type.Key(), out var combo) ? combo : null;

    /// <summary>The shortcut a mode listens to: the own one when set, else the built-in one.</summary>
    public KeyCombo Hotkey(WorkflowType type) => CustomHotkey(type) ?? KeyCombo.Default(type);

    public Dictionary<WorkflowType, KeyCombo> ResolvedHotkeys() =>
        WorkflowTypes.All.ToDictionary(t => t, Hotkey);

    public AppSettings Clone() => SettingsSerializer.Clone(this);
}

public sealed class TranscriptionSettings
{
    public string Language { get; set; } = "de";
}

[JsonConverter(typeof(TolerantEnumConverter<TextTone>))]
public enum TextTone
{
    Neutral,
    Formal,
    Casual,
}

[JsonConverter(typeof(TolerantEnumConverter<EmojiDensity>))]
public enum EmojiDensity
{
    Mittel,
    Wenig,
    Viel,
}

public static class ModeSettingsDisplay
{
    public static string DisplayName(this TextTone tone) => tone switch
    {
        TextTone.Formal => "Formell",
        TextTone.Neutral => "Neutral",
        TextTone.Casual => "Locker",
        _ => "",
    };

    public static string DisplayName(this EmojiDensity density) => density switch
    {
        EmojiDensity.Wenig => "Wenig",
        EmojiDensity.Mittel => "Mittel",
        EmojiDensity.Viel => "Viel",
        _ => "",
    };

    public static readonly TextTone[] Tones = [TextTone.Formal, TextTone.Neutral, TextTone.Casual];
    public static readonly EmojiDensity[] Densities = [EmojiDensity.Wenig, EmojiDensity.Mittel, EmojiDensity.Viel];
}

public sealed class TextImprovementSettings
{
    public string SystemPrompt { get; set; } = "";
    public List<string> CustomTerms { get; set; } = [];
    /// <summary>Fixed rules for words that do not sound like their spelling ("n acht n" -> "n8n").</summary>
    public List<TermReplacement> Replacements { get; set; } = [];
    public string Context { get; set; } = "";
    public TextTone Tone { get; set; } = TextTone.Neutral;
    public string CustomName { get; set; } = "";

    /// <summary>
    /// Everything the language model must keep spelled exactly: custom terms and the
    /// written side of the replacements.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<string> ProtectedTerms
    {
        get
        {
            var seen = new HashSet<string>();
            return CustomTerms.Concat(Replacements.Select(r => r.Written))
                .Where(term => seen.Add(term.ToLowerInvariant()))
                .ToList();
        }
    }
}

public sealed class DampfAblassenSettings
{
    public string SystemPrompt { get; set; } = ModePrompts.DampfAblassenDefault;
    public string CustomName { get; set; } = "";
}

public sealed class EmojiTextSettings
{
    public EmojiDensity EmojiDensity { get; set; } = EmojiDensity.Mittel;
    public string CustomName { get; set; } = "";
}
