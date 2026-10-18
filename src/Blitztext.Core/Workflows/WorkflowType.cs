using System.Text.Json.Serialization;

namespace Blitztext.Core.Workflows;

/// <summary>The four modes. JSON names match the macOS app's settings keys.</summary>
[JsonConverter(typeof(TolerantEnumConverter<WorkflowType>))]
public enum WorkflowType
{
    TapTalk,
    TextImprover,
    DampfAblassen,
    EmojiText,
}

public static class WorkflowTypes
{
    public static readonly WorkflowType[] All =
        [WorkflowType.TapTalk, WorkflowType.TextImprover, WorkflowType.DampfAblassen, WorkflowType.EmojiText];

    public static readonly WorkflowType[] AiModes =
        [WorkflowType.TextImprover, WorkflowType.DampfAblassen, WorkflowType.EmojiText];

    public static string Key(this WorkflowType type) => type switch
    {
        WorkflowType.TapTalk => "tapTalk",
        WorkflowType.TextImprover => "textImprover",
        WorkflowType.DampfAblassen => "dampfAblassen",
        WorkflowType.EmojiText => "emojiText",
        _ => "tapTalk",
    };

    public static WorkflowType? FromKey(string key) =>
        All.Cast<WorkflowType?>().FirstOrDefault(t => t!.Value.Key() == key);

    public static string DisplayName(this WorkflowType type) => type switch
    {
        WorkflowType.TapTalk => "Blitztext",
        WorkflowType.TextImprover => "Blitztext+",
        WorkflowType.DampfAblassen => "Blitztext $%&!",
        WorkflowType.EmojiText => "Blitztext :)",
        _ => "Blitztext",
    };

    public static string Subtitle(this WorkflowType type) => type switch
    {
        WorkflowType.TapTalk => "Sprache rein. Text raus.",
        WorkflowType.TextImprover => "Gesprochen rein. Geschrieben raus.",
        WorkflowType.DampfAblassen => "Frust rein. Entspannt raus.",
        WorkflowType.EmojiText => "Text rein. Emojis dazu.",
        _ => "",
    };

    public static bool IsAiMode(this WorkflowType type) => type != WorkflowType.TapTalk;

    /// <summary>Segoe Fluent Icons / Segoe MDL2 Assets glyph for the mode.</summary>
    public static string Glyph(this WorkflowType type) => type switch
    {
        WorkflowType.TapTalk => "",       // Microphone
        WorkflowType.TextImprover => "",  // Edit
        WorkflowType.DampfAblassen => "", // LightningBolt
        WorkflowType.EmojiText => "",     // Emoji2
        _ => "",
    };

    /// <summary>Accent of the mode as RGB, the same in light and dark.</summary>
    public static uint AccentColor(this WorkflowType type) => type switch
    {
        WorkflowType.TapTalk => 0x3E41F4,
        WorkflowType.TextImprover => 0x7C4DDB,
        WorkflowType.DampfAblassen => 0xE0662B,
        WorkflowType.EmojiText => 0x0E9BB5,
        _ => 0x3E41F4,
    };
}

/// <summary>Where a workflow is in its life: recording or processing, finished or failed.</summary>
public abstract record WorkflowPhase
{
    public sealed record Idle : WorkflowPhase;
    public sealed record Running(string Message) : WorkflowPhase;
    public sealed record Done(string Text) : WorkflowPhase;
    public sealed record Error(string Message) : WorkflowPhase;

    public static readonly WorkflowPhase IdlePhase = new Idle();

    public bool IsActive => this is not Idle;
}

public enum WorkflowLaunchSource
{
    Manual,
    HotkeyBackground,
}
