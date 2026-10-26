using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RetroWorks.Core;

/// <summary>Text helpers ported from retroworks.py.</summary>
public static class Text
{
    private static readonly Regex UnsafeNameRe = new("[/\\\\:\\x00-\\x1f]", RegexOptions.Compiled);

    /// <summary>Remove control characters, tabs (→ 4 spaces), and zero-width chars.</summary>
    public static string Clean(string s)
    {
        s = s.Replace("\t", "    ");
        var sb = new StringBuilder(s.Length);
        foreach (char ch in s)
        {
            if (ch >= ' ' && ch != '\x7f' && !IsZeroWidth(ch))
                sb.Append(ch);
        }
        return sb.ToString();
    }

    public static bool IsZeroWidth(char ch) =>
        char.GetUnicodeCategory(ch) is UnicodeCategory.NonSpacingMark
            or UnicodeCategory.EnclosingMark
            or UnicodeCategory.Format;

    /// <summary>Sanitize a file name: replace unsafe chars, strip dots/whitespace.</summary>
    public static string SafeName(string name)
    {
        string n = UnsafeNameRe.Replace(name, "_").Trim().Trim('.');
        return n.Length > 0 ? n : "Untitled";
    }

    /// <summary>Word-wrap: return the start offset of every display row of s at width w.</summary>
    public static int[] WrapStarts(string s, int w)
    {
        var starts = new List<int> { 0 };
        int i = 0;
        int n = s.Length;
        while (n - i > w)
        {
            int sp = s.LastIndexOf(' ', Math.Min(i + w, n - 1), Math.Min(w + 1, n - i));
            int brk = sp > i ? sp + 1 : i + w;
            starts.Add(brk);
            i = brk;
        }
        return [.. starts];
    }
}
