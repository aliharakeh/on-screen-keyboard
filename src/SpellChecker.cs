using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace OnScreenKeyboard;

/// <summary>Result of checking one word.</summary>
/// <param name="Replacement">A fix safe to auto-correct to, e.g. "teh" → "the"; null when unsure.</param>
public record SpellResult(string? Replacement, List<string> Suggestions);

/// <summary>
/// Thin wrapper over the Windows Spell Checking API (ISpellCheckerFactory, Windows 8+). Works for any
/// language whose spell-checking data is installed (Settings › Time &amp; language › Language).
/// </summary>
public sealed class SpellChecker
{
    private readonly ISpellCheckerFactory? _factory;
    private readonly List<string> _languages = [];
    private readonly Dictionary<string, ISpellChecker?> _checkers = [];

    public SpellChecker()
    {
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("7AB36653-1796-484B-BDFA-E74F1DB7C1DC"))!;
            _factory = (ISpellCheckerFactory)Activator.CreateInstance(type)!;
            _languages = ReadAll(_factory.GetSupportedLanguages());
        }
        catch { _factory = null; }
    }

    public IReadOnlyList<string> Languages => _languages;

    /// <summary>Returns null when the word is spelled correctly or no spell checker is available for its language.</summary>
    public SpellResult? Check(string word)
    {
        var checker = CheckerFor(Predictor.IsArabic(word) ? "ar" : "en");
        if (checker == null) return null;
        try
        {
            var errors = checker.Check(word);
            if (errors.Next(out var error) != 0 || error == null) return null;
            int action = error.GetCorrectiveAction();
            if (action is not (CorrectiveActionGetSuggestions or CorrectiveActionReplace)) return null;

            string? replacement = action == CorrectiveActionReplace ? error.GetReplacement() : null;
            if (string.IsNullOrEmpty(replacement)) replacement = null;
            var suggestions = ReadAll(checker.Suggest(word));
            // Windows rarely marks a fix as certain, so also trust a top suggestion that is one typo away
            // (teh → the, helo → hello). Capitalized words are likely names and are left alone.
            if (replacement == null && suggestions.Count > 0 && word.Length >= 3 && !char.IsUpper(word[0])
                && TypoDistance(word.ToLowerInvariant(), suggestions[0].ToLowerInvariant()) == 1)
                replacement = suggestions[0];
            if (replacement != null)
            {
                suggestions.Remove(replacement);
                suggestions.Insert(0, replacement);
            }
            return new SpellResult(replacement, suggestions);
        }
        catch { return null; }
    }

    private ISpellChecker? CheckerFor(string lang)
    {
        if (_factory == null) return null;
        if (_checkers.TryGetValue(lang, out var cached)) return cached;
        // Prefer the main regional variant, then any installed variant of the language.
        string preferred = lang == "ar" ? "ar-SA" : "en-US";
        string? tag = _languages.FirstOrDefault(l => l.Equals(preferred, StringComparison.OrdinalIgnoreCase))
            ?? _languages.FirstOrDefault(l => l.Equals(lang, StringComparison.OrdinalIgnoreCase)
                || l.StartsWith(lang + "-", StringComparison.OrdinalIgnoreCase));
        ISpellChecker? checker = null;
        try { if (tag != null) checker = _factory.CreateSpellChecker(tag); }
        catch { /* language data missing */ }
        return _checkers[lang] = checker;
    }

    /// <summary>Edit distance counting a swap of two neighbouring letters as one typo (optimal string alignment).</summary>
    public static int TypoDistance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
            }
        }
        return d[a.Length, b.Length];
    }

    private static List<string> ReadAll(IEnumString strings)
    {
        var result = new List<string>();
        var item = new string[1];
        while (strings.Next(1, item, 0) == 0) result.Add(item[0]);
        return result;
    }

    private const int CorrectiveActionGetSuggestions = 1, CorrectiveActionReplace = 2;
}

// ---- COM interfaces from spellcheck.h (only the vtable slots we use, in declaration order) ----

[ComImport, Guid("8E018A9D-2415-4677-BF08-794EA61F94BB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISpellCheckerFactory
{
    IEnumString GetSupportedLanguages();
    [return: MarshalAs(UnmanagedType.Bool)] bool IsSupported([MarshalAs(UnmanagedType.LPWStr)] string languageTag);
    ISpellChecker CreateSpellChecker([MarshalAs(UnmanagedType.LPWStr)] string languageTag);
}

[ComImport, Guid("B6FD0B71-E2BC-4653-8D05-F197E412770B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISpellChecker
{
    [return: MarshalAs(UnmanagedType.LPWStr)] string GetLanguageTag();
    IEnumSpellingError Check([MarshalAs(UnmanagedType.LPWStr)] string text);
    IEnumString Suggest([MarshalAs(UnmanagedType.LPWStr)] string word);
}

[ComImport, Guid("803E3BD4-2828-4410-8290-418D1D73C762"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IEnumSpellingError
{
    [PreserveSig] int Next(out ISpellingError? value);
}

[ComImport, Guid("B7C82D61-FBE8-4B47-9B27-6C0D2E0DE0A3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISpellingError
{
    uint GetStartIndex();
    uint GetLength();
    int GetCorrectiveAction();
    [return: MarshalAs(UnmanagedType.LPWStr)] string GetReplacement();
}
