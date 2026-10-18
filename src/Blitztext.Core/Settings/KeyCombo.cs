using System.Text.Json.Serialization;
using Blitztext.Core.Workflows;

namespace Blitztext.Core.Settings;

/// <summary>Modifier keys of a shortcut, left and right side alike.</summary>
[Flags]
public enum ModifierKeys
{
    None = 0,
    Ctrl = 1,
    Shift = 2,
    Alt = 4,
    Win = 8,
}

/// <summary>
/// A shortcut: modifiers plus one key, or modifiers alone (<see cref="VirtualKey"/> 0), like the
/// built-in "Strg + Win". Modifier-only shortcuts fire as soon as exactly these modifiers are held.
/// </summary>
public sealed record KeyCombo
{
    public ModifierKeys Modifiers { get; init; }

    /// <summary>Windows virtual-key code, 0 for a modifier-only shortcut.</summary>
    public int VirtualKey { get; init; }

    /// <summary>What the key is called on the keyboard, e.g. "D" or "F13".</summary>
    public string KeyLabel { get; init; } = "";

    [JsonIgnore]
    public bool IsModifierOnly => VirtualKey == 0;

    public KeyCombo() { }

    public KeyCombo(ModifierKeys modifiers, int virtualKey = 0, string keyLabel = "")
    {
        Modifiers = modifiers;
        VirtualKey = virtualKey;
        KeyLabel = keyLabel;
    }

    /// <summary>Parts for the key badges, e.g. ["Strg", "Win"].</summary>
    [JsonIgnore]
    public IReadOnlyList<string> Parts
    {
        get
        {
            var parts = new List<string>();
            if (Modifiers.HasFlag(ModifierKeys.Ctrl)) parts.Add("Strg");
            if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Umschalt");
            if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
            if (Modifiers.HasFlag(ModifierKeys.Win)) parts.Add("Win");
            if (!IsModifierOnly) parts.Add(KeyLabel);
            return parts;
        }
    }

    [JsonIgnore]
    public string DisplayLabel => string.Join(" + ", Parts);

    public static int ModifierCount(ModifierKeys modifiers) =>
        ((modifiers & ModifierKeys.Ctrl) != 0 ? 1 : 0) + ((modifiers & ModifierKeys.Shift) != 0 ? 1 : 0)
        + ((modifiers & ModifierKeys.Alt) != 0 ? 1 : 0) + ((modifiers & ModifierKeys.Win) != 0 ? 1 : 0);

    /// <summary>
    /// The built-in shortcuts. Strg + Win is the "fn" of Windows here: the other modes add a key
    /// to it or use Alt + Win. Holding a third modifier while recording switches to that mode,
    /// so the order the keys go down in does not matter.
    /// </summary>
    public static KeyCombo Default(WorkflowType type) => type switch
    {
        WorkflowType.TapTalk => new KeyCombo(ModifierKeys.Ctrl | ModifierKeys.Win),
        WorkflowType.TextImprover => new KeyCombo(ModifierKeys.Ctrl | ModifierKeys.Shift | ModifierKeys.Win),
        WorkflowType.DampfAblassen => new KeyCombo(ModifierKeys.Ctrl | ModifierKeys.Alt | ModifierKeys.Win),
        WorkflowType.EmojiText => new KeyCombo(ModifierKeys.Alt | ModifierKeys.Win),
        _ => new KeyCombo(ModifierKeys.Ctrl | ModifierKeys.Win),
    };

    public bool SameKeys(KeyCombo other) => Modifiers == other.Modifiers && VirtualKey == other.VirtualKey;
}
