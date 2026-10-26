using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RetroWorks.Core;

/// <summary>
/// Right-to-left text support: the Unicode bidirectional algorithm (UAX #9), ported from
/// retroworks.py. Text is stored in logical (typing) order; <see cref="BidiVisual"/> works out
/// the order in which it must appear on screen for a paragraph whose base direction is 'L' or 'R'.
/// </summary>
public static class Bidi
{
    public static readonly Dictionary<char, char> Mirror = new()
    {
        { '(', ')' }, { ')', '(' },
        { '[', ']' }, { ']', '[' },
        { '{', '}' }, { '}', '{' },
        { '<', '>' }, { '>', '<' },
        { '\u00ab', '\u00bb' }, { '\u00bb', '\u00ab' },
    };

    public static readonly HashSet<char> OpenBrackets = new() { '(', '[', '{' };
    public static readonly HashSet<char> CloseBrackets = new() { ')', ']', '}' };

    public static readonly Regex RtlRe = new("[\u0590-\u08ff\uFB1D-\uFDFF\uFE70-\uFEFC]");

    /// <summary>Classify a character into a bidi type: L, R, EN, AN, ES, ET, CS, PS, PE,
    /// NSM, B, S, WS, ON, BN.</summary>
    public static string BType(char ch)
    {
        // Hebrew and Arabic ranges → RTL
        if ((ch >= '\u0590' && ch <= '\u08FF') ||
            (ch >= '\uFB1D' && ch <= '\uFDFF') ||
            (ch >= '\uFE70' && ch <= '\uFEFC'))
            return "R";

        var cat = char.GetUnicodeCategory(ch);
        switch (cat)
        {
            case UnicodeCategory.NonSpacingMark:
            case UnicodeCategory.SpacingCombiningMark:
            case UnicodeCategory.EnclosingMark:
                return "NSM";
            case UnicodeCategory.Control:
            case UnicodeCategory.Format:
            case UnicodeCategory.PrivateUse:
            case UnicodeCategory.Surrogate:
            case UnicodeCategory.OtherNotAssigned:
                return "BN";
            case UnicodeCategory.LineSeparator:
                return "S";
            case UnicodeCategory.ParagraphSeparator:
                return "B";
            case UnicodeCategory.SpaceSeparator:
                return "WS";
            case UnicodeCategory.DecimalDigitNumber:
                // Arabic-Indic digits (0660–0669, 06F0–06F9) are AN
                if ((ch >= '\u0660' && ch <= '\u0669') ||
                    (ch >= '\u06F0' && ch <= '\u06F9'))
                    return "AN";
                return "EN";
            case UnicodeCategory.OpenPunctuation:
                return "PS";
            case UnicodeCategory.ClosePunctuation:
                return "PE";
            case UnicodeCategory.InitialQuotePunctuation:
            case UnicodeCategory.FinalQuotePunctuation:
                // Quote marks act as ES/ET depending on context; default to ON
                return "ON";
            case UnicodeCategory.OtherNumber:
                return "EN";
            default:
                // Letters, punctuation, symbols → L (default) or ON
                if (char.IsLetter(ch))
                    return "L";
                return "ON";
        }
    }

    public static string? Strong(string t) => t == "L" ? "L" : (t is "R" or "EN" or "AN") ? "R" : null;

    /// <summary>Compute the embedding level of every character (UAX #9, rules W1..N2, I1/I2, L1/L2).</summary>
    public static int[] BidiLevels(string s, string baseDir)
    {
        int n = s.Length;
        if (n == 0) return Array.Empty<int>();

        int e = baseDir == "R" ? 1 : 0;
        string edir = e == 1 ? "R" : "L";
        var orig = new string[n];
        for (int i = 0; i < n; i++) orig[i] = BType(s[i]);

        var t = (string[])orig.Clone();

        // W1: NSM takes the type of the previous character.
        string prev = edir;
        for (int i = 0; i < n; i++)
        {
            if (t[i] == "NSM") t[i] = prev;
            else if (t[i] != "BN") prev = t[i];
        }

        // W2: EN following AL becomes AN.
        string last = edir;
        for (int i = 0; i < n; i++)
        {
            if (t[i] is "L" or "R" or "AL") last = t[i];
            else if (t[i] == "EN" && last == "AL") t[i] = "AN";
        }

        // W3: AL becomes R.
        for (int i = 0; i < n; i++) if (t[i] == "AL") t[i] = "R";

        // W4: ES/CS between two EN/AN become EN.
        for (int i = 1; i < n - 1; i++)
        {
            if (t[i] == "ES" && t[i - 1] == "EN" && t[i + 1] == "EN") t[i] = "EN";
            else if (t[i] == "CS" && t[i - 1] == t[i + 1] && (t[i - 1] == "EN" || t[i - 1] == "AN"))
                t[i] = t[i - 1];
        }

        // W5: ET adjacent to EN becomes EN.
        int wi = 0;
        while (wi < n)
        {
            if (t[wi] == "ET")
            {
                int j = wi;
                while (j < n && t[j] == "ET") j++;
                if ((wi > 0 && t[wi - 1] == "EN") || (j < n && t[j] == "EN"))
                    for (int k = wi; k < j; k++) t[k] = "EN";
                wi = j;
            }
            else wi++;
        }

        // W6: ES, ET, CS become ON.
        for (wi = 0; wi < n; wi++) if (t[wi] is "ES" or "ET" or "CS") t[wi] = "ON";

        // W7: EN following L becomes L.
        last = edir;
        for (wi = 0; wi < n; wi++)
        {
            if (t[wi] is "L" or "R") last = t[wi];
            else if (t[wi] == "EN" && last == "L") t[wi] = "L";
        }

        // N0: bracket pairs.
        var stack = new List<(char close, int pos)>();
        var pairs = new List<(int a, int b)>();
        for (wi = 0; wi < n; wi++)
        {
            if (t[wi] != "ON") continue;
            char c = s[wi];
            if (OpenBrackets.Contains(c))
            {
                if (stack.Count >= 63) break;
                stack.Add((Mirror[c], wi));
            }
            else if (CloseBrackets.Contains(c))
            {
                for (int k = stack.Count - 1; k >= 0; k--)
                {
                    if (stack[k].close == c)
                    {
                        pairs.Add((stack[k].pos, wi));
                        stack.RemoveRange(k + 1, stack.Count - k - 1);
                        stack.RemoveAt(k);
                        break;
                    }
                }
            }
        }

        foreach (var (a, b) in pairs.OrderBy(p => p.a))
        {
            var inside = new HashSet<string>();
            for (int k = a + 1; k < b; k++)
            {
                var st = Strong(t[k]);
                if (st != null) inside.Add(st);
            }
            string d;
            if (inside.Contains(edir)) d = edir;
            else if (inside.Count > 0)
            {
                d = edir;
                for (int k = a - 1; k >= 0; k--)
                    if (Strong(t[k]) != null) { d = Strong(t[k])!; break; }
            }
            else continue;
            t[a] = t[b] = d;
        }

        // N1/N2: neutral runs.
        wi = 0;
        while (wi < n)
        {
            if (t[wi] is "B" or "S" or "WS" or "ON")
            {
                int j = wi;
                while (j < n && t[j] is "B" or "S" or "WS" or "ON") j++;
                string before = wi == 0 ? edir : (Strong(t[wi - 1]) ?? edir);
                string after = j == n ? edir : (Strong(t[j]) ?? edir);
                string d = before == after ? before : edir;
                for (int k = wi; k < j; k++) t[k] = d;
                wi = j;
            }
            else wi++;
        }

        // I1/I2: initial levels.
        var lev = new int[n];
        if (e == 0)
            for (wi = 0; wi < n; wi++)
                lev[wi] = t[wi] == "R" ? 1 : (t[wi] is "AN" or "EN") ? 2 : 0;
        else
            for (wi = 0; wi < n; wi++)
                lev[wi] = t[wi] is "L" or "EN" or "AN" ? 2 : 1;

        // L1: trailing neutrals take the embedding level.
        wi = n - 1;
        while (wi >= 0 && (orig[wi] == "WS" || orig[wi] == "BN")) { lev[wi] = e; wi--; }

        // L2: S/B reset following neutrals to the embedding level.
        for (wi = 0; wi < n; wi++)
        {
            if (orig[wi] is "S" or "B")
            {
                lev[wi] = e;
                int k = wi - 1;
                while (k >= 0 && (orig[k] == "WS" || orig[k] == "BN")) { lev[k] = e; k--; }
            }
        }

        return lev;
    }

    /// <summary>Return the visual string for a logical string with the given base direction.
    /// Brackets in reversed runs are mirrored.</summary>
    public static string BidiVisual(string s, string baseDir = "L")
    {
        int n = s.Length;
        if (baseDir != "R" && !RtlRe.IsMatch(s)) return s;

        var lev = BidiLevels(s, baseDir);
        var order = new List<int>(Enumerable.Range(0, n));

        // L2: reverse runs at each odd level, highest first.
        int maxLevel = lev.Max();
        for (int level = maxLevel; level >= 1; level--)
        {
            if (level % 2 == 0) continue;
            int i = 0;
            while (i < n)
            {
                if (lev[order[i]] >= level)
                {
                    int j = i;
                    while (j < n && lev[order[j]] >= level) j++;
                    for (int a = i, b = j - 1; a < b; a++, b--)
                        (order[a], order[b]) = (order[b], order[a]);
                    i = j;
                }
                else i++;
            }
        }

        var sb = new StringBuilder(n);
        for (int v = 0; v < n; v++)
        {
            char c = s[order[v]];
            if (lev[order[v]] % 2 == 1) c = Mirror.TryGetValue(c, out var m) ? m : c;
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>'R' if the first letter of s is right-to-left, 'L' if left-to-right,
    /// null if s has no letters.</summary>
    public static string? FirstStrong(string s)
    {
        foreach (char ch in s)
        {
            var t = BType(ch);
            if (t is "R" or "AL") return "R";
            if (t == "L") return "L";
        }
        return null;
    }
}
