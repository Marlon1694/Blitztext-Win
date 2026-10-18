using System.Runtime.InteropServices;

namespace Blitztext.Windows.Services;

/// <summary>
/// Uses the Windows spell checker to tell real words ("Cloud", "Blackbox") from misheard
/// names ("Clawd", "Blackboad"), so fuzzy term correction leaves real words alone.
/// German and English are checked when Windows has them.
/// </summary>
public sealed class DictionaryWordChecker
{
    private static readonly string[] Languages = ["de-DE", "en-US", "en-GB"];
    private readonly object _lock = new();
    private List<ISpellChecker>? _checkers;

    public static readonly DictionaryWordChecker Shared = new();

    public bool IsKnownWord(string word)
    {
        if (word.Length <= 1) return true;
        // The checkers are created and used on MTA threads only, so COM never has to marshal them.
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            return Task.Run(() => IsKnownWord(word)).GetAwaiter().GetResult();
        }
        lock (_lock)
        {
            try
            {
                _checkers ??= CreateCheckers();
                foreach (var checker in _checkers)
                {
                    if (checker.Check(word, out var errors) != 0 || errors is null) continue;
                    var result = errors.Next(out var error);
                    Marshal.ReleaseComObject(errors);
                    if (error is not null) Marshal.ReleaseComObject(error);
                    // S_FALSE: no spelling error in this language.
                    if (result == 1) return true;
                }
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                _checkers = [];
            }
            return false;
        }
    }

    private static List<ISpellChecker> CreateCheckers()
    {
        var result = new List<ISpellChecker>();
        try
        {
            var factory = (ISpellCheckerFactory)new SpellCheckerFactoryClass();
            foreach (var language in Languages)
            {
                if (factory.IsSupported(language, out var supported) == 0 && supported != 0
                    && factory.CreateSpellChecker(language, out var checker) == 0 && checker is not null)
                {
                    result.Add(checker);
                }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
        }
        return result;
    }

    // MARK: - COM (spellcheck.h, Windows 8 and later)

    [ComImport, Guid("7AB36653-1796-484B-BDFA-E74F1DB7C1DC")]
    private class SpellCheckerFactoryClass { }

    [ComImport, Guid("8E018A9D-2415-4677-BF08-794EA61F94BB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISpellCheckerFactory
    {
        [PreserveSig] int get_SupportedLanguages(out IntPtr value);
        [PreserveSig] int IsSupported([MarshalAs(UnmanagedType.LPWStr)] string languageTag, out int value);
        [PreserveSig] int CreateSpellChecker([MarshalAs(UnmanagedType.LPWStr)] string languageTag, out ISpellChecker? value);
    }

    [ComImport, Guid("B6FD0B71-E2BC-4653-8D05-F197E412770B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISpellChecker
    {
        [PreserveSig] int get_LanguageTag(out IntPtr value);
        [PreserveSig] int Check([MarshalAs(UnmanagedType.LPWStr)] string text, out IEnumSpellingError? value);
    }

    [ComImport, Guid("803E3BD4-2828-4410-8290-418D1D73C762"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumSpellingError
    {
        [PreserveSig] int Next([MarshalAs(UnmanagedType.IUnknown)] out object? value);
    }
}
