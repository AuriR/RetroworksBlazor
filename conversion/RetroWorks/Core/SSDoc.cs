using System.Text.Json;

namespace RetroWorks.Core;

/// <summary>Spreadsheet document with formula engine. Ported from retroworks.py SSDoc.</summary>
public sealed class SSDoc : Doc, FormulaEngine.ISheet
{
    public override string Kind => "ss";

    public Dictionary<string, string> Cells { get; set; } = [];
    public Dictionary<string, int> Widths { get; set; } = [];

    private int _r = 1, _c = 0;
    private int _top = 1, _left = 0;
    private readonly Dictionary<string, object?> _cache = [];
    private readonly HashSet<string> _stack = [];
    private string _lastFind = "";
    private bool _rendering;   // true during Draw() — suppresses duplicate Error Log creation

    public SSDoc(App app, string name) : base(app, name) { }

    // ---- FormulaEngine.ISheet ----

    public double Num(string refStr)
    {
        FormulaEngine.SplitRef(refStr);
        var v = Value(refStr);
        return v is double d ? d : 0.0;
    }

    public List<double> RangeVals(string a, string b)
    {
        var (c1, r1) = FormulaEngine.SplitRef(a);
        var (c2, r2) = FormulaEngine.SplitRef(b);
        var outList = new List<double>();
        for (int c = Math.Min(c1, c2); c <= Math.Max(c1, c2); c++)
            for (int r = Math.Min(r1, r2); r <= Math.Max(r1, r2); r++)
            {
                var v = Value(FormulaEngine.RefName(c, r));
                if (v is double d) outList.Add(d);
            }
        return outList;
    }

    public string CellText(string refStr)
    {
        FormulaEngine.SplitRef(refStr);
        var v = Value(refStr);
        return v switch
        {
            null => "",
            double d => FormulaEngine.FmtNum(d, 20),
            SSError e => throw e,
            string s => s,
            _ => ""
        };
    }

    public List<List<string>> RangeCells(string a, string b)
    {
        var (c1, r1) = FormulaEngine.SplitRef(a);
        var (c2, r2) = FormulaEngine.SplitRef(b);
        int minC = Math.Min(c1, c2), maxC = Math.Max(c1, c2);
        int minR = Math.Min(r1, r2), maxR = Math.Max(r1, r2);
        var grid = new List<List<string>>();
        for (int r = minR; r <= maxR; r++)
        {
            var row = new List<string>();
            for (int c = minC; c <= maxC; c++)
                row.Add(CellText(FormulaEngine.RefName(c, r)));
            grid.Add(row);
        }
        return grid;
    }

    // ---- Evaluation ----

    public object? Value(string refStr)
    {
        if (_cache.TryGetValue(refStr, out var cached))
        {
            if (cached is SSError err) throw err;
            return cached;
        }

        string? raw = Cells.GetValueOrDefault(refStr);
        if (string.IsNullOrEmpty(raw)) return null;

        if (!FormulaEngine.IsValue(raw!))
        {
            string label = raw![0] == '"' ? raw[1..] : raw;
            _cache[refStr] = label;
            return label;
        }

        if (_stack.Contains(refStr)) throw new SSError(Localizer.S("CircularReference"));
        _stack.Add(refStr);
        try
        {
            string expr = raw![0] == '=' ? raw[1..] : raw!;
            var v = FormulaEngine.Parse(this, FormulaEngine.Tokenize(expr));
            if (v is double d && (double.IsNaN(d) || double.IsInfinity(d))) throw new SSError(Localizer.S("NumberOutOfRange"));
            _cache[refStr] = v;
            return v;
        }
        catch (SSError e) { _cache[refStr] = e; throw; }
        catch (StackOverflowException)
        {
            var e = new SSError(Localizer.S("FormulaChainTooDeep"));
            _cache[refStr] = e;
            throw e;
        }
        catch (Exception ex)
        {
            // UNEXPECTED exception (a bug, not a normal formula error). Do NOT let it escape and
            // freeze the app: log it to an "Error Log" document on the Desktop (once), then treat
            // this cell as 0 so the spreadsheet keeps working. During Draw() we skip logging to
            // avoid creating a new doc on every render frame.
            if (!_rendering)
                App.LogUnexpectedError(ex, Localizer.T("ErrorLogCell", refStr, raw));
            _cache[refStr] = 0.0;
            return 0.0;
        }
        finally { _stack.Remove(refStr); }
    }

    /// <summary>Display text for a formula value (for the status bar).</summary>
    private static string TextOfVal(object? v) => v switch
    {
        null => "",
        double d => FormulaEngine.FmtNum(d, 19),
        SSError e => "ERROR",
        string s => s,
        _ => ""
    };

    private (string text, string kind) Display(string refStr, int w)
    {
        string? raw = Cells.GetValueOrDefault(refStr);
        if (string.IsNullOrEmpty(raw)) return ("", "blank");
        try
        {
            var v = Value(refStr);
            if (v is SSError) return ("ERROR", "num");
            if (v is double d) return (FormulaEngine.FmtNum(d, w), "num");
            if (raw![0] == '\\' && raw.Length > 1)
            {
                string rep = new string(raw[1], w);
                return (rep[..w], "repeat");
            }
            return ((string)v!, "label");
        }
        catch (SSError) { return ("ERROR", "num"); }
    }

    // ---- Geometry ----

    private string Ref() => FormulaEngine.RefName(_c, _r);
    private int Width(int c) =>
        Math.Max(3, Math.Min(60, Widths.GetValueOrDefault(FormulaEngine.Cols[c].ToString(), 9)));
    private int Vh() => App.MinH - 6;

    private List<(int c, int x)> VisibleCols(int W)
    {
        var cols = new List<(int, int)>();
        int x = 5, c = _left;
        while (c < 26 && x + Width(c) <= W)
        {
            cols.Add((c, x));
            x += Width(c);
            c++;
        }
        return cols;
    }

    private void Move(int dr, int dc)
    {
        _r = Math.Max(1, Math.Min(FormulaEngine.MaxRow, _r + dr));
        _c = Math.Max(0, Math.Min(25, _c + dc));
    }

    // ---- Draw ----

    public override void Draw()
    {
        int H = App.MinH, W = App.MinW;
        _cache.Clear();
        _rendering = true;
        try
        {
            DrawBody(H, W);
        }
        finally { _rendering = false; }
    }

    private void DrawBody(int H, int W)
    {
        int vh = Vh();

        if (_r < _top) _top = _r;
        if (_r >= _top + vh) _top = _r - vh + 1;
        if (_c < _left) _left = _c;

        var cols = VisibleCols(W);
        while (cols.Count > 0 && _c > cols[^1].c) { _left++; cols = VisibleCols(W); }

        string refStr = Ref();
        string raw = Cells.GetValueOrDefault(refStr, "");

        App.Frame(Title(), "ReviewAddChange", "EscapeMainMenu", "TypeEntryOrAltCommands");
        App.Put(H - 3, 0, new string('=', W));
        App.Put(H - 2, 0, new string(' ', W));

        string kind = raw.Length == 0 ? "" : (FormulaEngine.IsValue(raw) ? Localizer.S("Value") : Localizer.S("Label"));
        App.Put(H - 2, 0, $"{refStr}: {kind}{raw}"[..Math.Min(W - 22, refStr.Length + kind.Length + raw.Length)], App.A_B);

        if (raw.Length > 0 && FormulaEngine.IsValue(raw))
        {
            try
            {
                var val = Value(refStr);
                string vs = "= " + (val is double dv ? FormulaEngine.FmtNum(dv, 19) : TextOfVal(val));
                App.Put(H - 2, Math.Max(W - vs.Length - 1, W - 40), vs[..Math.Min(39, vs.Length)]);
            }
            catch (SSError e)
            {
                string vs = Localizer.S("Error") + e.Message;
                App.Put(H - 2, Math.Max(W - vs.Length - 1, W - 40), vs[..Math.Min(39, vs.Length)]);
            }
        }

        // Column headers
        App.Put(2, 0, "    ", App.A_B);
        foreach (var (c, x) in cols)
        {
            int w = Width(c);
            string header = FormulaEngine.Cols[c].ToString().PadLeft(w / 2).PadRight(w);
            App.Put(2, x, header, c == _c ? App.A_R : App.A_B);
        }

        // Rows
        for (int i = 0; i < vh; i++)
        {
            int r = _top + i;
            if (r > FormulaEngine.MaxRow) break;
            // NOTE: C# interpolation has no alignment specifiers — {r:>4} would
            // render the literal text ">4". Use PadLeft for right-alignment.
            App.Put(3 + i, 0, r.ToString().PadLeft(4), r == _r ? App.A_R : App.A_B);

            foreach (var (c, x) in cols)
            {
                int w = Width(c);
                var (text, kind2) = Display(FormulaEngine.RefName(c, r), w);
                string seg;
                if (kind2 == "blank") seg = new string(' ', w);
                else if (kind2 == "num") seg = text.PadLeft(w - 1).PadRight(w);
                else if (kind2 == "repeat") seg = text[..Math.Min(w, text.Length)];
                else if (Bidi.FirstStrong(text) == "R") seg = text[..Math.Min(w, text.Length)].PadRight(w);
                else seg = text[..Math.Min(w, text.Length)].PadRight(w);

                bool cur = r == _r && c == _c;
                App.Put(3 + i, x, seg, cur ? App.A_R : App.A_N);
            }
        }
    }

    // ---- Editing ----

    private void SetCell(string refStr, string text)
    {
        text = Text.Clean(text).Trim();
        string? old = Cells.GetValueOrDefault(refStr);
        if (text.Length > 0) Cells[refStr] = text;
        else Cells.Remove(refStr);

        if (!string.Equals(old, text.Length > 0 ? text : null)) Dirty = true;
        _cache.Clear();

        if (text.Length > 0 && FormulaEngine.IsValue(text))
        {
            try { Value(refStr); }
            catch (SSError e) { App.Msg = Localizer.T("RefError", refStr, e.Message); }
        }
    }

    private async Task EditAsync(string? first)
    {
        int H = App.MinH, W = App.MinW;
        string refStr = Ref();
        string init = first ?? Cells.GetValueOrDefault(refStr, "");
        App.Put(H - 1, 0, new string(' ', W - 1));

        string? text = await App.AskAsync("", init);
        // AskAsync uses the label as prefix; for cell editing we use a simpler approach
        if (text is null) return;

        SetCell(refStr, text);
        // Move down after edit
        _r = Math.Min(FormulaEngine.MaxRow, _r + 1);
    }

    private (int maxR, int maxC) UsedExtent()
    {
        if (Cells.Count == 0) return (1, 0);
        int maxR = 1, maxC = 0;
        foreach (var k in Cells.Keys)
        {
            var (c, r) = FormulaEngine.SplitRef(k);
            maxR = Math.Max(maxR, r);
            maxC = Math.Max(maxC, c);
        }
        return (maxR, maxC);
    }

    private void ShiftRows(int at, int delta)
    {
        var newCells = new Dictionary<string, string>();
        foreach (var (refStr, raw) in Cells)
        {
            var (c, r) = FormulaEngine.SplitRef(refStr);
            if (delta < 0 && r == at) continue;
            if (delta > 0 && r >= at) r++;
            else if (delta < 0 && r > at) r--;
            if (r > FormulaEngine.MaxRow) continue;
            newCells[FormulaEngine.RefName(c, r)] =
                FormulaEngine.IsValue(raw) ? FormulaEngine.FixRows(raw, at, delta) : raw;
        }
        Cells = newCells;
        Dirty = true;
        _cache.Clear();
    }

    /// <summary>Shift cells left/right when inserting (+1) or deleting (-1) column `at` (0-based).</summary>
    private void ShiftCols(int at, int delta)
    {
        var newCells = new Dictionary<string, string>();
        foreach (var (refStr, raw) in Cells)
        {
            var (c, r) = FormulaEngine.SplitRef(refStr);
            if (delta < 0 && c == at) continue;   // delete: drop the column's cells
            if (delta > 0 && c >= at) c++;
            else if (delta < 0 && c > at) c--;
            if (c > 25) continue;
            newCells[FormulaEngine.RefName(c, r)] =
                FormulaEngine.IsValue(raw) ? FormulaEngine.FixCols(raw, at, delta) : raw;
        }
        // Column widths shift with the columns.
        var newWidths = new Dictionary<string, int>();
        foreach (var (colName, w) in Widths)
        {
            int c = colName[0] - 'A';
            if (delta < 0 && c == at) continue;
            if (delta > 0 && c >= at) c++;
            else if (delta < 0 && c > at) c--;
            if (c <= 25) newWidths[FormulaEngine.Cols[c].ToString()] = w;
        }
        Widths = newWidths;
        Cells = newCells;
        Dirty = true;
        _cache.Clear();
    }

    // ---- Handle ----

    protected override Task HandleAsync(string k) => HandleImpl(k);

    private async Task HandleImpl(string k)
    {
        // Any single printable character starts a cell edit — matches the
        // Python original (len(k)==1). This is how you type "=A1+B2" etc.
        if (k.Length == 1)
        {
            await EditAsync(k);
            return;
        }

        string refStr = Ref();
        string raw = Cells.GetValueOrDefault(refStr, "");

        switch (k)
        {
            case Key.Enter:
            case "A-U":
                await EditAsync(null);
                break;

            case Key.Up: Move(-1, 0); break;
            case Key.Down: Move(1, 0); break;
            case Key.Left: Move(0, -1); break;
            case Key.Right or Key.Tab: Move(0, 1); break;
            case Key.BTab: Move(0, -1); break;
            case Key.PgUp: Move(-Vh(), 0); break;
            case Key.PgDn: Move(Vh(), 0); break;

            case Key.Home:
            case "A-T": _r = 1; _c = 0; break;
            case Key.End:
            case "A-B": (_r, _c) = UsedExtent(); break;

            case Key.Backspace:
            case Key.Delete:
            case "A-Y":
                SetCell(refStr, "");
                break;

            case "A-C":
                if (raw.Length > 0) { ClipSs = (raw, _r, _c, "copy"); App.ClipText = raw; App.Msg = Localizer.T("RefCopied", refStr); }
                break;

            case "A-X":
                if (raw.Length > 0) { ClipSs = (raw, _r, _c, "cut"); App.ClipText = raw; SetCell(refStr, ""); App.Msg = Localizer.T("RefCut", refStr); }
                break;

            case "A-V":
                if (ClipSs is not null)
                {
                    var (craw, r0, c0, mode) = ClipSs.Value;
                    string newRaw = mode == "copy" && FormulaEngine.IsValue(craw)
                        ? FormulaEngine.ShiftRefs(craw, _r - r0, _c - c0) : craw;
                    SetCell(refStr, newRaw);
                }
                else if (App.ClipText.Length > 0)
                {
                    // Cross-module paste: place the shared text in this cell. Multi-line text
                    // fills downward from the current cell (one line per row).
                    var lines = App.ClipText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                    for (int li = 0; li < lines.Length; li++)
                        SetCell(FormulaEngine.RefName(_c, _r + li), Text.Clean(lines[li]));
                }
                else App.Msg = Localizer.S("ClipboardEmpty");
                break;

            case "A-R": await ReplicateAsync(); break;

            case "A-G":
                string? t = await App.AskAsync("GoToCell");
                if (t is not null)
                {
                    try { (_c, _r) = FormulaEngine.SplitRef(t.Trim().ToUpperInvariant().Replace("$", "")); }
                    catch (SSError) { App.Msg = Localizer.T("NotACell", t); }
                }
                break;

            case "A-F": await FindAsync(); break;

            case "A-N":
                ShiftRows(_r, +1);
                App.Msg = Localizer.T("RowNInserted", _r);
                break;

            case "A-D":
                if (await App.YesNoAsync(Localizer.T("DeleteRowN", _r)) == true)
                {
                    ShiftRows(_r, -1);
                    App.Msg = Localizer.S("RowDeleted");
                }
                break;

            case "A-I":
                ShiftCols(_c, +1);
                App.Msg = Localizer.T("ColNInserted", FormulaEngine.Cols[_c].ToString());
                break;

            case "A-J":
                if (await App.YesNoAsync(Localizer.T("DeleteColN", FormulaEngine.Cols[_c].ToString())) == true)
                {
                    ShiftCols(_c, -1);
                    App.Msg = Localizer.S("ColDeleted");
                }
                break;

            case "A-W":
                string colName = FormulaEngine.Cols[_c].ToString();
                string? tw = await App.AskAsync(Localizer.T("WidthOfColumn", colName), Width(_c).ToString());
                if (tw is not null && int.TryParse(tw, out int w))
                    Widths[colName] = Math.Max(3, Math.Min(60, w));
                break;

            case "A-P": await ExportAsync(); break;
        }
    }

    private async Task ReplicateAsync()
    {
        string refStr = Ref();
        string raw = Cells.GetValueOrDefault(refStr, "");
        if (raw.Length == 0) { App.Msg = Localizer.S("NothingToReplicate"); return; }

        string ex = FormulaEngine.RefName(_c, Math.Min(FormulaEngine.MaxRow, _r + 9));
        string? t = await App.AskAsync(Localizer.T("ReplicateRefThroughCell", refStr, ex));
        if (t is null) return;

        int c2, r2;
        try { (c2, r2) = FormulaEngine.SplitRef(t.Trim().ToUpperInvariant().Replace("$", "")); }
        catch (SSError) { App.Msg = Localizer.T("NotACellShort", t); return; }

        int n = 0;
        for (int r = Math.Min(_r, r2); r <= Math.Max(_r, r2); r++)
            for (int c = Math.Min(_c, c2); c <= Math.Max(_c, c2); c++)
            {
                if ((r, c) == (_r, _c)) continue;
                Cells[FormulaEngine.RefName(c, r)] =
                    FormulaEngine.IsValue(raw) ? FormulaEngine.ShiftRefs(raw, r - _r, c - _c) : raw;
                n++;
            }

        if (n > 0) Dirty = true;
        _cache.Clear();
        App.Msg = Localizer.T("ReplicatedRefIntoNCells", refStr, n);
    }

    private async Task FindAsync()
    {
        string? q = await App.AskAsync("FindTextInCells", _lastFind);
        if (string.IsNullOrEmpty(q)) return;
        _lastFind = q;
        string ql = q.ToLowerInvariant();

        var hits = Cells.Where(kv => kv.Value.Contains(ql, StringComparison.OrdinalIgnoreCase))
            .Select(kv => FormulaEngine.SplitRef(kv.Key))
            .OrderBy(x => x.r).ThenBy(x => x.c)
            .ToList();

        if (hits.Count == 0) { App.Msg = Localizer.T("TextNotFound", q); return; }
        var next = hits.FirstOrDefault(h => h.r > _r || (h.r == _r && h.c > _c));
        if (next == default) next = hits[0];
        (_c, _r) = next;
    }

    private async Task ExportAsync()
    {
        if (Cells.Count == 0) { App.Msg = Localizer.S("SpreadsheetIsEmpty"); return; }
        string? fn = await ExportPathAsync(".csv", "ExportValuesToCsv");
        if (fn is null) return;

        _cache.Clear();
        var (maxR, maxC) = UsedExtent();
        var sb = new System.Text.StringBuilder();
        for (int r = 1; r <= maxR; r++)
        {
            var rowVals = new List<string>();
            for (int c = 0; c <= maxC; c++)
            {
                try
                {
                    var v = Value(FormulaEngine.RefName(c, r));
                    if (v is null) rowVals.Add("");
                    else if (v is double f && f == Math.Floor(f) && Math.Abs(f) < 1e15) rowVals.Add(((long)f).ToString());
                    else if (v is double f2) rowVals.Add(f2.ToString("R"));
                    else rowVals.Add(CsvEscape((string)v!));
                }
                catch (SSError) { rowVals.Add("ERROR"); }
            }
            sb.AppendLine(string.Join(",", rowVals));
        }

        await DownloadText(fn, sb.ToString());
        App.Msg = Localizer.T("ExportedNRowsToFile", maxR, fn);
    }

    private static string CsvEscape(string s) =>
        s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    // ---- JSON ----

    public override Dictionary<string, object?> ToJson() =>
        new() { ["cells"] = Cells, ["widths"] = Widths };

    public override int Size() => Cells.Sum(kv => kv.Key.Length + kv.Value.Length + 2);

    public override void FromJson(JsonElement d)
    {
        Cells = [];
        if (d.TryGetProperty("cells", out var cellsEl))
            foreach (var prop in cellsEl.EnumerateObject())
            {
                try { FormulaEngine.SplitRef(prop.Name); } catch { continue; }
                string v = Text.Clean(prop.Value.GetString() ?? "");
                if (v.Length > 0) Cells[prop.Name] = v;
            }

        Widths = [];
        if (d.TryGetProperty("widths", out var widthsEl))
            foreach (var prop in widthsEl.EnumerateObject())
                if (prop.Name.Length == 1 && FormulaEngine.Cols.Contains(prop.Name)
                    && int.TryParse(prop.Value.GetString(), out int w))
                    Widths[prop.Name] = Math.Max(3, Math.Min(60, w));
    }

    protected override string HelpText() => Localizer.Help("SS");
}
