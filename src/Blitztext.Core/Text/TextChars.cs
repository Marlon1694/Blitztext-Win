using System.Globalization;

namespace Blitztext.Core.Text;

/// <summary>
/// Character classes as Swift's <c>Character</c> sees them, for UTF-16 strings. A combining
/// mark right after a letter belongs to that letter ("u" + U+0308 is one "ü").
/// </summary>
internal static class TextChars
{
    public static bool IsNewline(char c) =>
        c is '\n' or '\r' or '\u000B' or '\u000C' or '\u0085' or (char)0x2028 or (char)0x2029;

    public static bool IsWhitespace(char c) => char.IsWhiteSpace(c);

    public static bool IsMark(char c)
    {
        var category = CharUnicodeInfo.GetUnicodeCategory(c);
        return category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.EnclosingMark;
    }

    /// <summary>Letter or number at <paramref name="index"/>, surrogate pairs included.</summary>
    public static bool IsLetterOrNumber(string text, int index) =>
        char.IsLetter(text, index) || char.IsNumber(text, index);

    public static bool IsLetter(string text, int index) => char.IsLetter(text, index);

    /// <summary>Length of the code point at <paramref name="index"/> in UTF-16 units.</summary>
    public static int Step(string text, int index) =>
        char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]) ? 2 : 1;

    public static string TrimWhitespace(string text) => text.Trim();

    public static bool IsLowercaseLetter(char c) => char.IsLetter(c) && char.IsLower(c);

    public static bool IsUppercaseLetter(char c) => char.IsLetter(c) && char.IsUpper(c);

    /// <summary>First character uppercased the way Swift's <c>uppercased()</c> does for one character.</summary>
    public static string UppercaseFirst(string text)
    {
        if (text.Length == 0) return text;
        var step = Step(text, 0);
        var first = text[..step].ToUpperInvariant();
        return first + text[step..];
    }

    public static string LowercaseFirst(string text)
    {
        if (text.Length == 0) return text;
        var step = Step(text, 0);
        return text[..step].ToLowerInvariant() + text[step..];
    }
}
