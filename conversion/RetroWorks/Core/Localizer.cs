using System.Globalization;
using System.Resources;

namespace RetroWorks.Core;

/// <summary>
/// Localization service backed by RESX resource files (Strings.resx / Strings.he.resx).
/// Provides T() for translated strings with positional format placeholders.
/// </summary>
public sealed class Localizer
{
    private static readonly ResourceManager _rm = new(
        "RetroWorks.Resources.Strings", typeof(Localizer).Assembly);

    public static string Lang { get; set; } = "en";

    private static CultureInfo Culture =>
        Lang == "he" ? new CultureInfo("he-IL") : CultureInfo.InvariantCulture;

    /// <summary>Available language codes and display names.</summary>
    public static (string code, string name)[] Languages =>
    [
        ("en", "English"),
        ("he", "\u05E2\u05D1\u05E8\u05D9\u05EA"),
    ];

    /// <summary>Get a translated string by key. Falls back to the key itself if not found.</summary>
    public static string S(string key) => _rm.GetString(key, Culture) ?? key;

    /// <summary>Get a translated string and apply positional format arguments ({0}, {1}, ...).</summary>
    public static string T(string key, params object?[] args)
    {
        string s = S(key);
        if (args.Length == 0) return s;
        try { return string.Format(Culture, s, args); }
        catch (FormatException) { return s; }
    }

    /// <summary>Get help text (multi-line) for a given key.</summary>
    public static string Help(string key) => S("Help" + key);

    public static void SetLanguage(string code)
    {
        Lang = code is "en" or "he" ? code : "en";
    }
}
