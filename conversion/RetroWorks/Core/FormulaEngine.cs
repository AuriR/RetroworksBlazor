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
            else outList.Add(("o", m.Groups[4].Value, 0));
        }
        return outList;
    }

    /// <summary>Parse and evaluate a formula. `num` resolves a cell reference to its numeric value,
    /// `rangeVals` evaluates a rectangular range to its list of numbers.</summary>
    public static double Parse(ISheet sheet, List<(string kind, string? text, double num)> toks) =>
        new Parser(sheet, toks).Parse();

    private sealed class Parser : ISheet
    {
        private readonly ISheet _sheet;
        private readonly List<(string kind, string? text, double num)> _t;
        private int _i;

        public Parser(ISheet sheet, List<(string, string?, double)> toks)
        {
            _sheet = sheet;
            _t = toks;
        }

        // ISheet: delegate cell lookups to the real sheet.
        public double Num(string refStr) => _sheet.Num(refStr);
        public List<double> RangeVals(string a, string b) => _sheet.RangeVals(a, b);

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

        public double Parse()
        {
            if (_t.Count == 0) throw new SSError("empty formula");
            double v = Cmp();
            if (_i != _t.Count) throw new SSError("syntax error");
            return v;
        }

        private double Cmp()
        {
            double a = Add();
            var (k, v, _) = Peek();
            if (k == "o" && v is "<" or ">" or "=" or "<=" or ">=" or "<>")
            {
                Take();
                double b = Add();
                return v switch
                {
                    "<" => a < b ? 1 : 0,
                    ">" => a > b ? 1 : 0,
                    "=" => a == b ? 1 : 0,
                    "<=" => a <= b ? 1 : 0,
                    ">=" => a >= b ? 1 : 0,
                    _ => a != b ? 1 : 0,
                };
            }
            return a;
        }

        private double Add()
        {
            double a = Mul();
            while (true)
            {
                var (k, v, _) = Peek();
                if (k == "o" && (v == "+" || v == "-"))
                {
                    Take();
                    double b = Mul();
                    a = v == "+" ? a + b : a - b;
                }
                else return a;
            }
        }

        private double Mul()
        {
            double a = Unary();
            while (true)
            {
                var (k, v, _) = Peek();
                if (k == "o" && (v == "*" || v == "/"))
                {
                    Take();
                    double b = Unary();
                    if (v == "*") a *= b;
                    else
                    {
                        if (b == 0) throw new SSError("division by zero");
                        a /= b;
                    }
                }
                else return a;
            }
        }

        private double Unary()
        {
            var (k, v, _) = Peek();
            if (k == "o" && (v == "-" || v == "+"))
            {
                Take();
                double x = Unary();
                return v == "-" ? -x : x;
            }
            return Power();
        }

        private double Power()
        {
            double a = Primary();
            var (k, v, _) = Peek();
            if (k == "o" && v == "^")
            {
                Take();
                double b = Unary();
                try
                {
                    double r = Math.Pow(a, b);
                    if (double.IsNaN(r) || double.IsInfinity(r)) throw new SSError("math error");
                    return r;
                }
                catch (OverflowException) { throw new SSError("math error"); }
            }
            return a;
        }

        private double Primary()
        {
            var (k, v, num) = Take();
            if (k == "n") return num;
            if (k == "r") return _sheet.Num(v!);
            if (k == "o" && v == "(")
            {
                double x = Cmp();
                Expect(")");
                return x;
            }
            if (k == "f")
            {
                if (v == "PI")
                {
                    var p = Peek();
                    if (p.kind == "o" && p.text == "(") { Take(); Expect(")"); }
                    return Math.PI;
                }
                Expect("(");
                var args = new List<double>();
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
                return CallFunc(v!, args);
            }
            throw new SSError("syntax error");
        }

        private List<double> Arg()
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
                    return _sheet.RangeVals(v!, end.text!);
                }
            }
            return new List<double> { Cmp() };
        }

        private static double CallFunc(string name, List<double> args)
        {
            try
            {
                switch (name)
                {
                    case "SUM": return args.Sum();
                    case "AVG":
                    case "AVERAGE":
                        if (args.Count == 0) throw new SSError("@AVG of nothing");
                        return args.Average();
                    case "MIN":
                        if (args.Count == 0) throw new SSError("@MIN of nothing");
                        return args.Min();
                    case "MAX":
                        if (args.Count == 0) throw new SSError("@MAX of nothing");
                        return args.Max();
                    case "COUNT": return args.Count;
                    case "ABS": return Math.Abs(One(name, args));
                    case "INT": { double x = One(name, args); return x >= 0 ? Math.Floor(x) : Math.Ceiling(x); }
                    case "SQRT": return Math.Sqrt(One(name, args));
                    case "EXP": return Math.Exp(One(name, args));
                    case "LN": return Math.Log(One(name, args));
                    case "LOG": return Math.Log10(One(name, args));
                    case "SIN": return Math.Sin(One(name, args));
                    case "COS": return Math.Cos(One(name, args));
                    case "TAN": return Math.Tan(One(name, args));
                    case "ATAN": return Math.Atan(One(name, args));
                    case "ROUND":
                        if (args.Count == 1) args.Add(0);
                        if (args.Count != 2) throw new SSError("@ROUND needs 2 value(s)");
                        {
                            double x = args[0], f = Math.Pow(10, (int)args[1]);
                            return Math.Sign(x) * Math.Floor(Math.Abs(x) * f + 0.5) / f;
                        }
                    case "MOD":
                        if (args.Count != 2) throw new SSError("@MOD needs 2 value(s)");
                        if (args[1] == 0) throw new SSError("division by zero");
                        return IEEERemainder(args[0], args[1]);
                    case "IF":
                        if (args.Count != 3) throw new SSError("@IF needs 3 value(s)");
                        return args[0] != 0 ? args[1] : args[2];
                }
            }
            catch (SSError) { throw; }
            catch (Exception) { throw new SSError($"@{name}: math error"); }
            throw new SSError($"unknown function @{name}");
        }

        private static double One(string name, List<double> args)
        {
            if (args.Count != 1) throw new SSError($"@{name} needs 1 value(s)");
            return args[0];
        }

        // C#'s % is the mathematical remainder; Python's fmod is IEEE 754 remainder.
        private static double IEEERemainder(double x, double y) => Math.IEEERemainder(x, y);
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

    /// <summary>The interface the parser uses to look up cells.</summary>
    public interface ISheet
    {
        double Num(string refStr);
        List<double> RangeVals(string a, string b);
    }
}
