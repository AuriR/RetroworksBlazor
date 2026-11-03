using System.Text.RegularExpressions;

namespace RetroWorks.Core;

public class SSError : Exception
{
    public SSError(string message) : base(message) { }
}

/// <summary>Spreadsheet formula engine, ported from retroworks.py: tokenizer, recursive-descent
/// parser and about 20 functions. Cells are addressed A1..Z999.</summary>
public static class FormulaEngine
{
    public const string Cols = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    public const int MaxRow = 999;

    private static readonly Regex TokRe = new(
        @"(\d+(?:\.\d*)?(?:[eE][-+]?\d+)?|\.\d+(?:[eE][-+]?\d+)?)" +   // number
        @"|(\$?[A-Za-z]\$?\d+)(?![A-Za-z0-9])" +                       // cell ref
        @"|(@?[A-Za-z][A-Za-z0-9]*)" +                                 // function
        "|(\"[^\"]*\")" +                                              // string literal "..."
        @"|(\.\.\.|\.\.|<=|>=|<>|[-+*/^(),:<>=])",                     // operator
        RegexOptions.Compiled);

    private static readonly Regex RefAdjRe = new(
        @"(?<![A-Za-z0-9$])(\$?)([A-Za-z])(\$?)(\d+)(?![A-Za-z0-9])", RegexOptions.Compiled);

    public static bool IsValue(string raw) =>
        raw.Length > 0 && "0123456789+-.=@(".IndexOf(raw[0]) >= 0;

    /// <summary>Split a cell reference into (col 0-25, row 1-999).</summary>
    public static (int c, int r) SplitRef(string refStr)
    {
        if (!Regex.IsMatch(refStr, "^[A-Z]\\d+$")) throw new SSError("bad cell reference");
        int r = int.Parse(refStr.Substring(1));
        if (r < 1 || r > MaxRow) throw new SSError("bad cell reference");
        return (refStr[0] - 'A', r);
    }

    public static string RefName(int c, int r) => $"{Cols[c]}{r}";

    /// <summary>Adjust relative references (A1) by dr rows / dc cols; $A$1 stays.</summary>
    public static string ShiftRefs(string raw, int dr, int dc) => RefAdjRe.Replace(raw, m =>
    {
        bool absC = m.Groups[1].Value == "$";
        char col = char.ToUpperInvariant(m.Groups[2].Value[0]);
        bool absR = m.Groups[3].Value == "$";
        int row = int.Parse(m.Groups[4].Value);
        if (!absC) { var c = col - 'A' + dc; if (c < 0 || c > 25) return "#REF"; col = Cols[c]; }
        if (!absR) { row += dr; if (row < 1 || row > MaxRow) return "#REF"; }
        return $"{m.Groups[1].Value}{col}{m.Groups[3].Value}{row}";
    });

    /// <summary>Fix references after inserting (+1) or deleting (-1) row `at`.</summary>
    public static string FixRows(string raw, int at, int delta) => RefAdjRe.Replace(raw, m =>
    {
        bool absC = m.Groups[1].Value == "$";
        char col = char.ToUpperInvariant(m.Groups[2].Value[0]);
        bool absR = m.Groups[3].Value == "$";
        int row = int.Parse(m.Groups[4].Value);
        if (delta > 0 && row >= at) row++;
        else if (delta < 0)
        {
            if (row == at) return "#REF";
            if (row > at) row--;
        }
        if (row > MaxRow) return "#REF";
        return $"{m.Groups[1].Value}{col}{m.Groups[3].Value}{row}";
    });

    /// <summary>Fix references after inserting (+1) or deleting (-1) column `at` (0-based).</summary>
    public static string FixCols(string raw, int at, int delta) => RefAdjRe.Replace(raw, m =>
    {
        bool absC = m.Groups[1].Value == "$";
        char col = char.ToUpperInvariant(m.Groups[2].Value[0]);
        bool absR = m.Groups[3].Value == "$";
        int row = int.Parse(m.Groups[4].Value);
        int c = col - 'A';
        if (!absC)
        {
            if (delta > 0 && c >= at) c++;
            else if (delta < 0)
            {
                if (c == at) return "#REF";
                if (c > at) c--;
            }
            if (c > 25) return "#REF";
            col = Cols[c];
        }
        return $"{m.Groups[1].Value}{col}{m.Groups[3].Value}{row}";
    });

    public static List<(string kind, string? text, double num)> Tokenize(string s)
    {
        s = s.TrimEnd();
        var outList = new List<(string, string?, double)>();
        int pos = 0;
        while (pos < s.Length)
        {
            var m = TokRe.Match(s, pos);
            if (!m.Success || m.Index != pos || m.Length == 0) throw new SSError("syntax error");
            pos += m.Length;
            if (m.Groups[1].Success) outList.Add(("n", null, double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)));
            else if (m.Groups[2].Success) outList.Add(("r", m.Groups[2].Value.ToUpperInvariant().Replace("$", ""), 0));
            else if (m.Groups[3].Success) outList.Add(("f", m.Groups[3].Value.TrimStart('@').ToUpperInvariant(), 0));
            else if (m.Groups[4].Success)
            {
                // String literal: strip the surrounding quotes.
                outList.Add(("s", m.Groups[4].Value[1..^1], 0));
            }
            else outList.Add(("o", m.Groups[5].Value, 0));
        }
        return outList;
    }

    /// <summary>Parse and evaluate a formula. Returns a number (double) or text (string).
    /// `num`/`rangeVals`/`text`/`value` resolve cell references.</summary>
    public static object? Parse(ISheet sheet, List<(string kind, string? text, double num)> toks) =>
        new Parser(sheet, toks).Parse();

    private sealed class Parser : ISheet
    {
        private readonly ISheet _sheet;
        private readonly List<(string kind, string? text, double num)> _t;
        private int _i;

        // ---- Mixed-type argument helpers (numbers as double, strings as string) ----

        private static double NumAt(string name, List<object?> args, int i)
        {
            if (i >= args.Count) throw new SSError($"@{name} needs more value(s)");
            return args[i] switch { double d => d, _ => throw new SSError($"@{name}: not a number") };
        }

        private static string StrAt(string name, List<object?> args, int i)
        {
            if (i >= args.Count) throw new SSError($"@{name} needs more value(s)");
            return args[i] switch
            {
                string s => s,
                double d => FmtNum(d, 20),
                _ => ""
            };
        }

        private static RangeArg RangeAt(string name, List<object?> args, int i)
        {
            if (i >= args.Count || args[i] is not RangeArg r) throw new SSError($"@{name} needs a range");
            return r;
        }

        /// <summary>Flatten function arguments to numbers: doubles pass through, ranges expand via
        /// the sheet, strings are parsed (blank -> skipped). Used by @SUM/@AVG/@MIN/@MAX/@COUNT.</summary>
        private static List<double> FlattenNums(ISheet sheet, List<object?> args)
        {
            var n = new List<double>();
            foreach (var a in args)
            {
                switch (a)
                {
                    case double d: n.Add(d); break;
                    case RangeArg r: n.AddRange(sheet.RangeVals(r.A, r.B)); break;
                    case string s when !string.IsNullOrWhiteSpace(s):
                        if (double.TryParse(s.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double sv)) n.Add(sv);
                        break;
                }
            }
            return n;
        }

        private static double NumSum(ISheet sheet, List<object?> args) => FlattenNums(sheet, args).Sum();

        public Parser(ISheet sheet, List<(string, string?, double)> toks)
        {
            _sheet = sheet;
            _t = toks;
        }

        // ISheet: delegate cell lookups to the real sheet.
        public double Num(string refStr) => _sheet.Num(refStr);
        public List<double> RangeVals(string a, string b) => _sheet.RangeVals(a, b);
        public string CellText(string refStr) => _sheet.CellText(refStr);
        public List<List<string>> RangeCells(string a, string b) => _sheet.RangeCells(a, b);
        public object? Value(string refStr) => _sheet.Value(refStr);

        private (string kind, string? text, double num) Peek() =>
            _i < _t.Count ? _t[_i] : ("", null, 0);

        private (string kind, string? text, double num) Take()
        {
            var tok = Peek();
            _i++;
            return tok;
        }

        private void Expect(string op)
        {
            if (Take().text != op) throw new SSError($"expected \"{op}\"");
        }

        // ---- Mixed-type helpers: a formula value is a number (double), text (string), or null/blank. ----

        /// <summary>Numeric view of an operand for arithmetic/comparison (null -> 0, text parsed).</summary>
        private static double NumOf(object? v) => v switch
        {
            double d => d,
            string s => ToNum(s),
            _ => 0
        };

        /// <summary>Text view of an operand for concatenation (numbers formatted, null -> "").</summary>
        private static string TextOf(object? v) => v switch
        {
            string s => s,
            double d => FmtNum(d, 20),
            _ => ""
        };

        public object? Parse()
        {
            if (_t.Count == 0) throw new SSError("empty formula");
            var v = Cmp();
            if (_i != _t.Count) throw new SSError("syntax error");
            return v;
        }

        private object? Cmp()
        {
            var a = Add();
            var (k, v, _) = Peek();
            if (k == "o" && v is "<" or ">" or "=" or "<=" or ">=" or "<>")
            {
                Take();
                var b = Add();
                // Numbers compare numerically; text compares lexicographically; mixed -> numeric view.
                bool bothText = a is string && b is string;
                int c = bothText ? string.CompareOrdinal(TextOf(a), TextOf(b)) : NumOf(a).CompareTo(NumOf(b));
                return v switch
                {
                    "<" => c < 0 ? 1.0 : 0.0,
                    ">" => c > 0 ? 1.0 : 0.0,
                    "=" => c == 0 ? 1.0 : 0.0,
                    "<=" => c <= 0 ? 1.0 : 0.0,
                    ">=" => c >= 0 ? 1.0 : 0.0,
                    _ => c != 0 ? 1.0 : 0.0,   // <>
                };
            }
            return a;
        }

        private object? Add()
        {
            var a = Mul();
            while (true)
            {
                var (k, v, _) = Peek();
                if (k == "o" && (v == "+" || v == "-"))
                {
                    Take();
                    var b = Mul();
                    // '+' concatenates when either side is text; '-' is numeric only.
                    a = v == "+" ? (a is string || b is string ? TextOf(a) + TextOf(b) : NumOf(a) + NumOf(b))
                                 : NumOf(a) - NumOf(b);
                }
                else return a;
            }
        }

        private object? Mul()
        {
            var a = Unary();
            while (true)
            {
                var (k, v, _) = Peek();
                if (k == "o" && (v == "*" || v == "/"))
                {
                    Take();
                    var b = Unary();
                    double x = NumOf(a), y = NumOf(b);
                    a = v == "*" ? x * y : (y == 0 ? throw new SSError("division by zero") : x / y);
                }
                else return a;
            }
        }

        private object? Unary()
        {
            var (k, v, _) = Peek();
            if (k == "o" && (v == "-" || v == "+"))
            {
                Take();
                var x = Unary();
                return v == "-" ? -NumOf(x) : x;
            }
            return Power();
        }

        private object? Power()
        {
            var a = Primary();
            var (k, v, _) = Peek();
            if (k == "o" && v == "^")
            {
                Take();
                var b = Unary();
                try
                {
                    double r = Math.Pow(NumOf(a), NumOf(b));
                    if (double.IsNaN(r) || double.IsInfinity(r)) throw new SSError("math error");
                    return r;
                }
                catch (OverflowException) { throw new SSError("math error"); }
            }
            return a;
        }

        private object? Primary() => Primary(false);

        /// <summary>Parse a primary. When `asValue` is true (function-argument context) a cell ref
        /// resolves to its mixed value (number or text) so string functions can read labels;
        /// otherwise it resolves numerically for arithmetic.</summary>
        private object? Primary(bool asValue)
        {
            var (k, v, num) = Take();
            if (k == "n") return num;
            if (k == "r") return asValue ? _sheet.Value(v!) : _sheet.Num(v!);
            if (k == "o" && v == "(")
            {
                var x = Cmp();
                Expect(")");
                return x;
            }
            if (k == "s") return v;   // bare string literal: its text value
            if (k == "f")
            {
                if (v == "PI")
                {
                    var p = Peek();
                    if (p.kind == "o" && p.text == "(") { Take(); Expect(")"); }
                    return Math.PI;
                }
                Expect("(");
                var args = new List<object?>();
                if (!(Peek().kind == "o" && Peek().text == ")"))
                {
                    while (true)
                    {
                        args.AddRange(Arg());
                        var p = Peek();
                        if (p.kind == "o" && p.text == ",") { Take(); continue; }
                        break;
                    }
                }
                Expect(")");
                return CallFunc(this, v!, args);
            }
            throw new SSError("syntax error");
        }

        /// <summary>One function argument: a number, a string literal, or a range (kept as RangeArg
        /// so 2-D functions like @Vlookup can use it; numeric consumers flatten via NumAt).</summary>
        private List<object?> Arg()
        {
            var (k, v, _) = Peek();
            if (k == "r" && _i + 1 < _t.Count)
            {
                var next = _t[_i + 1];
                if (next.kind == "o" && next.text is "..." or ".." or ":")
                {
                    Take(); // ref
                    Take(); // range operator
                    var end = Take();
                    if (end.kind != "r") throw new SSError("bad range");
                    return new List<object?> { new RangeArg { A = v!, B = end.text! } };
                }
            }
            if (k == "s") { Take(); return new List<object?> { v }; }
            // A bare cell ref in a function argument resolves to its mixed value (number or text),
            // so @Left(A1,2) can read a label; arithmetic contexts use Primary() which is numeric.
            if (k == "r") { Take(); return new List<object?> { _sheet.Value(v!) }; }
            return new List<object?> { Cmp() };
        }

        private object? CallFunc(Parser p, string name, List<object?> args)
        {
            try
            {
                switch (name)
                {
                    case "SUM": return NumSum(p._sheet, args);
                    case "AVG":
                    case "AVERAGE":
                        if (args.Count == 0) throw new SSError("@AVG of nothing");
                        { var n = FlattenNums(p._sheet, args); if (n.Count == 0) throw new SSError("@AVG of nothing"); return n.Average(); }
                    case "MIN":
                        if (args.Count == 0) throw new SSError("@MIN of nothing");
                        { var n = FlattenNums(p._sheet, args); if (n.Count == 0) throw new SSError("@MIN of nothing"); return n.Min(); }
                    case "MAX":
                        if (args.Count == 0) throw new SSError("@MAX of nothing");
                        { var n = FlattenNums(p._sheet, args); if (n.Count == 0) throw new SSError("@MAX of nothing"); return n.Max(); }
                    case "COUNT": { var n = FlattenNums(p._sheet, args); return n.Count; }
                    case "ABS": return Math.Abs(NumAt(name, args, 0));
                    case "INT": { double x = NumAt(name, args, 0); return x >= 0 ? Math.Floor(x) : Math.Ceiling(x); }
                    case "SQRT": return Math.Sqrt(NumAt(name, args, 0));
                    case "EXP": return Math.Exp(NumAt(name, args, 0));
                    case "LN": return Math.Log(NumAt(name, args, 0));
                    case "LOG": return Math.Log10(NumAt(name, args, 0));
                    case "SIN": return Math.Sin(NumAt(name, args, 0));
                    case "COS": return Math.Cos(NumAt(name, args, 0));
                    case "TAN": return Math.Tan(NumAt(name, args, 0));
                    case "ATAN": return Math.Atan(NumAt(name, args, 0));
                    case "ROUND":
                        if (args.Count == 1) args.Add(0.0);
                        if (args.Count != 2) throw new SSError("@ROUND needs 2 value(s)");
                        { double x = NumAt(name, args, 0), f = Math.Pow(10, (int)NumAt(name, args, 1)); return Math.Sign(x) * Math.Floor(Math.Abs(x) * f + 0.5) / f; }
                    case "MOD":
                        if (args.Count != 2) throw new SSError("@MOD needs 2 value(s)");
                        { double d = NumAt(name, args, 1); if (d == 0) throw new SSError("division by zero"); return IEEERemainder(NumAt(name, args, 0), d); }
                    case "IF":
                        if (args.Count != 3) throw new SSError("@IF needs 3 value(s)");
                        return NumAt(name, args, 0) != 0 ? NumAt(name, args, 1) : NumAt(name, args, 2);
                    // ---- String functions ----
                    case "LEFT": { string s = StrAt(name, args, 0); int n = (int)NumAt(name, args, 1); return s[..Math.Max(0, Math.Min(n, s.Length))]; }
                    case "RIGHT": { string s = StrAt(name, args, 0); int n = (int)NumAt(name, args, 1); if (n <= 0) return ""; return s[^Math.Min(n, s.Length)..]; }
                    case "MID":
                        if (args.Count != 3) throw new SSError("@Mid needs 3 value(s)");
                        { string s = StrAt(name, args, 0); int st = (int)NumAt(name, args, 1), n = (int)NumAt(name, args, 2);
                          if (st < 1 || n <= 0 || st > s.Length) return ""; return s[(st - 1)..Math.Min(st - 1 + n, s.Length)]; }
                    case "LEN": return StrAt(name, args, 0).Length;
                    case "UPPER": return StrAt(name, args, 0).ToUpperInvariant();
                    case "LOWER": return StrAt(name, args, 0).ToLowerInvariant();
                    case "JOIN": { var sb = new System.Text.StringBuilder(); foreach (var a in args) sb.Append(a switch { string s => s, double d => FmtNum(d, 20), _ => "" }); return sb.ToString(); }
                    case "FIND":
                        if (args.Count < 2) throw new SSError("@Find needs 2 value(s)");
                        { string hay = StrAt(name, args, 1); string needle = StrAt(name, args, 0);
                          int idx = hay.IndexOf(needle, StringComparison.Ordinal); if (idx < 0) throw new SSError("not found"); return idx + 1; }
                    case "TEXT":
                        if (args.Count == 1) args.Add(2.0);
                        { double x = NumAt(name, args, 0); int dec = Math.Max(0, (int)NumAt(name, args, 1)); return x.ToString("F" + dec, System.Globalization.CultureInfo.InvariantCulture); }
                    case "VAL":
                        if (!double.TryParse(StrAt(name, args, 0), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double nv)) throw new SSError("not a number");
                        return nv;
                    // ---- Date/time functions (Excel-style serial dates) ----
                    case "TODAY": return DateSerial(DateTime.Today);
                    case "NOW": { var n = DateTime.Now; return DateSerial(n.Date) + (n.Hour * 3600 + n.Minute * 60 + n.Second) / 86400.0; }
                    case "YEAR": return (int)DateFromSerial(NumAt(name, args, 0)).Year;
                    case "MONTH": return (int)DateFromSerial(NumAt(name, args, 0)).Month;
                    case "DAY": return (int)DateFromSerial(NumAt(name, args, 0)).Day;
                    // ---- Lookup ----
                    case "VLOOKUP":
                        if (args.Count < 3) throw new SSError("@Vlookup needs 3 value(s)");
                        { double key = NumAt(name, args, 0); var rg = RangeAt(name, args, 1); int col = (int)NumAt(name, args, 2);
                          return Vlookup(p._sheet, key, rg, col); }
                }
            }
            catch (SSError) { throw; }
            catch (Exception) { throw new SSError($"@{name}: math error"); }
            throw new SSError($"unknown function @{name}");
        }

        /// <summary>Exact-match vertical lookup: search the first column of the range for `key`,
        /// return the value `col` columns to the right (1-based).</summary>
        private static double Vlookup(ISheet sheet, double key, RangeArg rg, int col)
        {
            var grid = sheet.RangeCells(rg.A!, rg.B!);
            string ks = FmtNum(key, 20);
            foreach (var row in grid)
            {
                if (row.Count > 0 && row[0] == ks)
                {
                    if (col < 1 || col > row.Count) throw new SSError("@Vlookup: bad column");
                    return ToNum(row[col - 1]);
                }
            }
            throw new SSError("not found");
        }

        // C#'s % is the mathematical remainder; Python's fmod is IEEE 754 remainder.
        private static double IEEERemainder(double x, double y) => Math.IEEERemainder(x, y);

        /// <summary>Parse a string as a number for formula results (empty/blank -> 0).</summary>
        internal static double ToNum(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return 0;
            return double.TryParse(s.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d) ? d : 0;
        }

        /// <summary>Excel-style date serial: days since 1899-12-30 (includes the historical 1900 leap-year bug).</summary>
        internal static double DateSerial(DateTime dt)
        {
            var o = dt.ToOADate();
            return o > 60 ? o + 1 : o;   // bridge OLE Automation (1904) to the Excel 1900 system
        }

        internal static DateTime DateFromSerial(double serial)
        {
            double o = serial > 60 ? serial - 1 : serial;
            return DateTime.FromOADate(o);
        }
    }

    /// <summary>Format a number for display in a cell of width w (last column is a margin).</summary>
    public static string FmtNum(double v, int w)
    {
        string s;
        if (v == Math.Floor(v) && v >= 0 && Math.Abs(v) < 1e15)
            s = ((long)v).ToString(System.Globalization.CultureInfo.InvariantCulture);
        else if (v == Math.Ceiling(v) && v < 0 && Math.Abs(v) < 1e15)
            s = ((long)v).ToString(System.Globalization.CultureInfo.InvariantCulture);
        else
        {
            s = v.ToString("0.##############", System.Globalization.CultureInfo.InvariantCulture);
            if (s is "-0" or "") s = "0";
        }
        if (s.Length <= w - 1) return s;
        int dot = s.IndexOf('.');
        if (dot >= 0)
        {
            int dec = w - 2 - dot;
            if (dec >= 0)
            {
                string s2 = v.ToString($"F{dec}", System.Globalization.CultureInfo.InvariantCulture);
                if (s2.Length <= w - 1) return s2;
            }
        }
        string s3 = v.ToString("E" + Math.Max(0, w - 8), System.Globalization.CultureInfo.InvariantCulture);
        if (s3.Length <= w - 1) return s3;
        return new string('#', Math.Max(1, w - 1));
    }

    /// <summary>A range argument (A1:B2) passed to functions that need 2-D access, e.g. @Vlookup.</summary>
    public sealed class RangeArg { public required string A; public required string B; }

    /// <summary>The interface the parser uses to look up cells.</summary>
    public interface ISheet
    {
        double Num(string refStr);
        List<double> RangeVals(string a, string b);
        /// <summary>Display text of a cell (numbers formatted, labels verbatim). For string functions.</summary>
        string CellText(string refStr);
        /// <summary>Raw 2-D grid of display text for a rectangular range. For @Vlookup.</summary>
        List<List<string>> RangeCells(string a, string b);
        /// <summary>A cell's evaluated value: a number (double) or its text (string). Blank -> null.</summary>
        object? Value(string refStr);
    }
}
