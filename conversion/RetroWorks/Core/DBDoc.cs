using System.Text.Json;

namespace RetroWorks.Core;

/// <summary>Data Base document. Ported from retroworks.py DBDoc.</summary>
public sealed class DBDoc : Doc
{
    public override string Kind => "db";

    public List<string> Fields { get; set; } = [Localizer.T("CategoryN", 1)];
    public List<List<string>> Records { get; set; } = [];
    public int[] Widths { get; set; } = [0];

    private bool _single;
    private int _cur, _f, _top, _left, _ftop;
    private string _filter = "";
    private (int field, bool desc)? _sortState;
    private bool _adding;

    // Step 5: per-field lookup lists (comma-separated allowed values) + highlight rules.
    public List<string?> Lookups { get; set; } = [];
    public List<(int Field, string Op, string Val)> Rules { get; set; } = [];

    public DBDoc(App app, string name) : base(app, name) { }

    private void PadLookups()
    {
        int n = Fields.Count;
        var l = Lookups.ToList();
        while (l.Count < n) l.Add(null);
        Lookups = [.. l.Take(n)];
    }

    private void PadWidths()
    {
        int n = Fields.Count;
        var w = Widths.ToList();
        while (w.Count < n) w.Add(0);
        Widths = [.. w.Take(n)];
    }

    private int ColW(int i) =>
        i < Widths.Length && Widths[i] > 0
            ? Math.Max(3, Math.Min(60, Widths[i]))
            : Math.Max(10, Math.Min(20, Fields[i].Length + 2));

    private List<int> Rows()
    {
        if (_filter.Length == 0) return [.. Enumerable.Range(0, Records.Count)];
        string q = _filter.ToLowerInvariant();
        return [.. Records.Select((rec, i) => (rec, i)).Where(x => x.rec.Any(v => v.Contains(q, StringComparison.OrdinalIgnoreCase))).Select(x => x.i)];
    }

    private List<(int f, int x)> VisFields(int width)
    {
        var outList = new List<(int, int)>();
        int x = 5, f = _left;
        while (f < Fields.Count && x + ColW(f) <= width)
        {
            outList.Add((f, x));
            x += ColW(f);
            f++;
        }
        return outList;
    }

    public override void Draw()
    {
        int H = App.MinH, W = App.MinW;
        var rows = Rows();
        _cur = rows.Count > 0 ? Math.Max(0, Math.Min(_cur, rows.Count - 1)) : 0;
        _f = Math.Max(0, Math.Min(_f, Fields.Count - 1));

        string info = Localizer.T("RecordAOfB", rows.Count > 0 ? _cur + 1 : 0, rows.Count);
        if (_filter.Length > 0) info += "   " + Localizer.T("NInFile", Records.Count);

        App.Frame(Title(), "ReviewAddChange", "EscapeMainMenu", "AltKHelp", info);

        string sel = Localizer.S("Selection") +
            (_filter.Length > 0 ? Localizer.T("RecordsContaining", _filter) : Localizer.S("AllRecords"));
        App.Put(2, 0, sel);
        string mode = Localizer.S(_single ? "SingleRecordLayout" : "MultipleRecordLayout");
        App.Put(2, W - mode.Length - 1, mode);

        if (rows.Count == 0)
        {
            App.Put(5, 4, Localizer.S(_filter.Length > 0 ? "NoRecordsMatch" : "NoRecordsYet"), App.A_B);
            return;
        }

        if (_single)
        {
            var rec = Records[rows[_cur]];
            int nl = Math.Min(30, Fields.Max(f => f.Length));
            int vh = H - 7;
            if (_f < _ftop) _ftop = _f;
            if (_f >= _ftop + vh) _ftop = _f - vh + 1;

            bool ruleHit = RuleMatch(rows[_cur]);
            for (int j = 0; j < vh; j++)
            {
                int fi = _ftop + j;
                if (fi >= Fields.Count) break;
                int y = 4 + j;
                App.Put(y, 2, Fields[fi][..Math.Min(nl, Fields[fi].Length)].PadLeft(nl) + ":", App.A_B);
                string val = rec[fi][..Math.Min(W - nl - 7, rec[fi].Length)];
                if (fi == _f) App.Put(y, nl + 4, val.Length > 0 ? val : " ", App.A_R);
                else App.Put(y, nl + 4, val, ruleHit ? App.A_R : App.A_N);
            }
        }
        else
        {
            if (_f < _left) _left = _f;
            var vis = VisFields(W);
            while (vis.Count > 0 && _f > vis[^1].f) { _left++; vis = VisFields(W); }

            foreach (var (fi, x) in vis)
            {
                int w = ColW(fi);
                App.Put(3, x, Fields[fi][..Math.Min(w - 1, Fields[fi].Length)].PadRight(w), App.A_B);
                App.Put(4, x, new string('-', w - 1));
            }

            int vh = H - 7;
            if (_cur < _top) _top = _cur;
            if (_cur >= _top + vh) _top = _cur - vh + 1;

            for (int j = 0; j < vh; j++)
            {
                int ri = _top + j;
                if (ri >= rows.Count) break;
                var rec = Records[rows[ri]];
                // NOTE: C# interpolation has no alignment specifiers — {x:>4} would
                // render the literal text ">4". Use PadLeft for right-alignment.
                App.Put(5 + j, 0, (ri + 1).ToString().PadLeft(4), ri == _cur ? App.A_R : App.A_B);

                bool ruleHit = RuleMatch(rows[ri]);
                foreach (var (fi, x) in vis)
                {
                    int w = ColW(fi);
                    bool cur = ri == _cur && fi == _f;
                    string val = rec[fi][..Math.Min(w - 1, rec[fi].Length)].PadRight(w - 1);
                    App.Put(5 + j, x, val, (cur || ruleHit) ? App.A_R : App.A_N);
                }
            }
        }
    }

    private async Task EditAsync(string? first)
    {
        while (true)
        {
            var rows = Rows();
            if (rows.Count == 0) return;
            _cur = Math.Max(0, Math.Min(_cur, rows.Count - 1));
            var rec = Records[rows[_cur]];
            string init = first ?? rec[_f];
            first = null;

            Draw();
            int H = App.MinH, W = App.MinW;
            string label = Fields[_f] + ": ";
            App.Put(H - 1, 0, new string(' ', W - 1));

            // Lookup list: offer the predefined values as a numbered menu first.
            PadLookups();
            if (Lookups[_f] is { Length: > 0 } lk)
            {
                var opts = lk.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                if (opts.Count > 0)
                {
                    var labels = new List<object?>();
                    for (int oi = 0; oi < opts.Count && oi < 9; oi++) labels.Add((oi + 1).ToString() + ". " + opts[oi]);
                    labels.Add(Localizer.S("TypeOtherValue"));
                    int? pick = await App.SelectAsync([Localizer.T("LookupFor", Fields[_f])], labels, esc: "EscapeCancel");
                    if (pick is null) { _adding = false; return; }
                    if (pick < opts.Count) init = opts[pick.Value];   // picked a predefined value
                }
            }

            string? text = await App.AskAsync("", init);
            if (text is null) { _adding = false; return; }

            text = Text.Clean(text);
            if (text != rec[_f]) { rec[_f] = text; Dirty = true; }

            bool lastField = _f >= Fields.Count - 1;
            // Simplified: after edit, move to next field or record
            if (!lastField) _f++;
            else if (_adding) { _adding = false; return; }
            else _cur = Math.Min(rows.Count - 1, _cur + 1);

            if (!_adding) return;
        }
    }

    private void InsertRecord(List<string> rec)
    {
        if (_filter.Length > 0) _filter = "";
        var rows = Rows();
        int pos = rows.Count > 0 ? rows[_cur] + 1 : 0;
        Records.Insert(pos, rec);
        _cur = pos;
        Dirty = true;
    }

    private async Task NewRecordAsync()
    {
        // NOTE: must use empty strings, not `new string[n]` (which yields nulls in C#).
        var rec = Enumerable.Repeat("", Fields.Count).ToList();
        InsertRecord(rec);
        _f = 0;
        _adding = true;
        await EditAsync(null);
        _adding = false;

        if (!rec.Any(v => v.Length > 0))
        {
            int idx = Records.IndexOf(rec);
            if (idx >= 0)
            {
                Records.RemoveAt(idx);
                _cur = Math.Max(0, idx - 1);
            }
            App.Msg = Localizer.S("EmptyRecordDiscarded");
        }
    }

    protected override async Task HandleAsync(string k)
    {
        var rows = Rows();
        int n = rows.Count;

        // Any single printable character starts a field edit — matches the
        // Python original (len(k)==1).
        if (k.Length == 1)
        {
            if (n == 0) App.Msg = Localizer.S("PressAltNToAddRecord");
            else await EditAsync(k);
            return;
        }

        switch (k)
        {
            case Key.Enter:
            case "A-U":
                if (n > 0) await EditAsync(null);
                else await NewRecordAsync();
                break;

            case Key.Up:
                if (_single) _f = Math.Max(0, _f - 1);
                else _cur = Math.Max(0, _cur - 1);
                break;
            case Key.Down:
                if (_single) _f = Math.Min(Fields.Count - 1, _f + 1);
                else _cur = Math.Min(n - 1, _cur + 1);
                break;
            case Key.Left or Key.BTab:
                if (_single) _cur = Math.Max(0, _cur - 1);
                else _f = Math.Max(0, _f - 1);
                break;
            case Key.Right or Key.Tab:
                if (_single) _cur = Math.Min(n - 1, _cur + 1);
                else _f = Math.Min(Fields.Count - 1, _f + 1);
                break;
            case Key.PgUp:
                if (_single) _cur = Math.Max(0, _cur - 1);
                else _cur = Math.Max(0, _cur - (App.MinH - 7));
                break;
            case Key.PgDn:
                if (_single) _cur = Math.Min(n - 1, _cur + 1);
                else _cur = Math.Min(n - 1, _cur + (App.MinH - 7));
                break;

            case Key.Home:
            case "A-T": _cur = 0; break;
            case Key.End:
            case "A-B": _cur = Math.Max(0, n - 1); break;

            case Key.Backspace:
            case Key.Delete:
            case "A-Y":
                if (n > 0 && Records[rows[_cur]][_f].Length > 0)
                {
                    Records[rows[_cur]][_f] = "";
                    Dirty = true;
                }
                break;

            case "A-N": await NewRecordAsync(); break;

            case "A-D":
                if (n > 0 && await App.YesNoAsync(Localizer.T("DeleteRecordN", _cur + 1)) == true)
                {
                    Records.RemoveAt(rows[_cur]);
                    Dirty = true;
                    App.Msg = Localizer.S("RecordDeleted");
                }
                break;

            case "A-Z": _single = !_single; break;

            case "A-F":
                string? t = await App.AskAsync("SelectRecordsContaining", _filter);
                if (t is not null)
                {
                    _filter = t.Trim();
                    _cur = _top = 0;
                    App.Msg = Localizer.T("NRecordsSelected", Rows().Count);
                }
                break;

            case "A-A": Arrange(); break;

            case "A-E": await CategoriesAsync(); break;

            case "A-L": await LookupListAsync(); break;

            case "A-R": await RulesAsync(); break;

            case "A-W":
                string? tw = await App.AskAsync(Localizer.T("WidthOfName", Fields[_f]), ColW(_f).ToString());
                if (tw is not null && int.TryParse(tw, out int w))
                {
                    PadWidths();
                    Widths[_f] = Math.Max(3, Math.Min(60, w));
                    Dirty = true;
                }
                break;

            case "A-C":
                if (n > 0) { ClipDb = [.. Records[rows[_cur]]]; App.Msg = Localizer.S("RecordCopied"); }
                break;

            case "A-V":
                if (ClipDb is null) { App.Msg = Localizer.S("ClipboardEmpty"); }
                else
                {
                    int nf = Fields.Count;
                    var pasteRec = new List<string>(nf);
                    for (int pi = 0; pi < nf; pi++)
                        pasteRec.Add(pi < ClipDb.Count ? ClipDb[pi] : "");
                    InsertRecord(pasteRec);
                    App.Msg = Localizer.S("RecordPastedBelow");
                }
                break;

            case "A-G": await ReportAsync(); break;

            case "A-P": await ExportAsync(); break;
        }
    }

    private void Arrange()
    {
        int fi = _f;
        bool desc = _sortState?.field == fi ? !_sortState!.Value.desc : false;
        _sortState = (fi, desc);

        List<List<string>> full = Records.Where(r => r[fi].Trim().Length > 0).ToList();
        List<List<string>> empty = Records.Where(r => r[fi].Trim().Length == 0).ToList();

        full.Sort((a, b) =>
        {
            int cmp;
            try
            {
                double va = double.Parse(a[fi].Replace(",", "").Replace("$", ""));
                double vb = double.Parse(b[fi].Replace(",", "").Replace("$", ""));
                cmp = va.CompareTo(vb);
            }
            catch { cmp = string.Compare(a[fi], b[fi], StringComparison.OrdinalIgnoreCase); }
            return desc ? -cmp : cmp;
        });

        Records = [.. full, .. empty];
        _cur = _top = 0;
        Dirty = true;
        App.Msg = Localizer.T("ArrangedByName", Fields[fi]) + Localizer.S(desc ? "Descending" : "Ascending");
    }

    private async Task CategoriesAsync()
    {
        string name = Fields[_f];
        int? i = await App.SelectAsync(
            [Localizer.T("CategoryName", name)],
            ["RenameThisCategory", "InsertNewCategoryAfter", "DeleteThisCategory", "MoveCategoryLeft", "MoveCategoryRight"],
            esc: "EscapeCancel");

        if (i is null) return;
        PadWidths();

        switch (i.Value)
        {
            case 0:
                string? t = await App.AskAsync("NewName", name);
                if (t is not null && t.Trim().Length > 0)
                    Fields[_f] = Text.Clean(t).Trim()[..Math.Min(30, Text.Clean(t).Trim().Length)];
                Dirty = true;
                break;

            case 1:
                string? tn = await App.AskAsync("NameOfNewCategory");
                if (tn is not null && tn.Trim().Length > 0)
                {
                    int p = _f + 1;
                    Fields.Insert(p, Text.Clean(tn).Trim()[..Math.Min(30, Text.Clean(tn).Trim().Length)]);
                    var w = Widths.ToList();
                    w.Insert(p, 0);
                    Widths = [.. w];
                    foreach (var rec in Records) rec.Insert(p, "");
                    _f = p;
                    Dirty = true;
                }
                break;

            case 2:
                if (Fields.Count == 1) { App.Msg = Localizer.S("NeedsAtLeastOneCategory"); }
                else if (await App.YesNoAsync(Localizer.T("DeleteCategoryAndEverything", name)) == true)
                {
                    Fields.RemoveAt(_f);
                    var w2 = Widths.ToList();
                    w2.RemoveAt(_f);
                    Widths = [.. w2];
                    foreach (var rec in Records) rec.RemoveAt(_f);
                    _f = Math.Max(0, _f - 1);
                    Dirty = true;
                }
                break;

            default:
                int j = _f + (i == 3 ? -1 : 1);
                if (j >= 0 && j < Fields.Count)
                {
                    (Fields[_f], Fields[j]) = (Fields[j], Fields[_f]);
                    var w3 = Widths.ToList();
                    (w3[_f], w3[j]) = (w3[j], w3[_f]);
                    Widths = [.. w3];
                    foreach (var rec in Records) (rec[_f], rec[j]) = (rec[j], rec[_f]);
                    _f = j;
                    Dirty = true;
                }
                break;
        }
    }

    // ---- Step 5: lookup lists & category rules ----

    /// <summary>True if the record at index recIdx matches any highlight rule.</summary>
    private bool RuleMatch(int recIdx)
    {
        var rec = Records[recIdx];
        foreach (var (fi, op, val) in Rules)
        {
            if (fi >= rec.Count) continue;
            string v = rec[fi].Trim();
            bool hit = op switch
            {
                "contains" => v.Contains(val, StringComparison.OrdinalIgnoreCase),
                "equals" => string.Equals(v, val, StringComparison.OrdinalIgnoreCase),
                "starts" => v.StartsWith(val, StringComparison.OrdinalIgnoreCase),
                _ => false,
            };
            if (hit) return true;
        }
        return false;
    }

    private async Task LookupListAsync()
    {
        PadLookups();
        string? t = await App.AskAsync(Localizer.T("LookupFor", Fields[_f]), Lookups[_f] ?? "");
        if (t is null) return;
        Lookups[_f] = t.Trim().Length > 0 ? t.Trim() : null;
        Dirty = true;
        App.Msg = Localizer.S(Lookups[_f] is null ? "LookupCleared" : "LookupSet");
    }

    private async Task RulesAsync()
    {
        while (true)
        {
            var labels = new List<object?>();
            for (int ri = 0; ri < Rules.Count; ri++)
                labels.Add((ri + 1) + ". " + RuleText(Rules[ri]));
            if (Rules.Count == 0) labels.Add(Localizer.S("NoRulesYet"));
            labels.Add("---");
            labels.Add(Localizer.S("AddRule"));
            labels.Add(Localizer.S("ClearAllRules"));

            int? sel = await App.SelectAsync([Localizer.S("HighlightRules")], labels, esc: "EscapeCancel");
            if (sel is null) return;

            // Menu layout (0-based list indices): [rules… | "NoRulesYet"] + "---" + "Add rule" + "Clear all".
            // When there are no rules the "NoRulesYet" placeholder occupies a slot, shifting the
            // trailing actions down by one — so compute their positions from the separator.
            int sepIdx = Rules.Count + (Rules.Count == 0 ? 1 : 0);   // position of "---"
            int addIdx = sepIdx + 1;                                 // "Add rule"
            int clearIdx = sepIdx + 2;                               // "Clear all rules"

            if (sel.Value == addIdx)
            {
                var fi = await PickFieldAsync();
                if (fi is null) continue;
                int? opk = await App.SelectAsync([Localizer.S("RuleCondition")],
                    ["Contains", "Equals", "StartsWith", Localizer.S("IsEmpty")], esc: "EscapeCancel");
                if (opk is null) continue;
                string op = opk.Value switch { 0 => "contains", 1 => "equals", 2 => "starts", _ => "empty" };
                string val = "";
                if (op != "empty")
                {
                    string? tv = await App.AskAsync("RuleValue", "");
                    if (tv is null) continue;
                    val = tv.Trim();
                }
                Rules.Add((fi.Value, op, val));
                Dirty = true;
                App.Msg = Localizer.S("RuleAdded");
            }
            else if (sel.Value == clearIdx)
            {
                Rules.Clear();
                Dirty = true;
                App.Msg = Localizer.S("RulesCleared");
            }
            // SelectAsync returns a 0-based list index: rules occupy slots 0..Rules.Count-1.
            else if (sel.Value >= 0 && sel.Value < Rules.Count)
            {
                int removed = sel.Value + 1;   // 1-based for the message
                Rules.RemoveAt(sel.Value);
                Dirty = true;
                App.Msg = Localizer.T("RuleDeleted", removed);
            }
        }
    }

    private string RuleText((int Field, string Op, string Val) r)
    {
        string opName = r.Op switch { "contains" => "Contains", "equals" => "Equals", "starts" => "StartsWith", _ => Localizer.S("IsEmpty") };
        return Fields[r.Field] + " " + opName + (r.Op == "empty" ? "" : " \"" + r.Val + "\"");
    }

    private async Task<int?> PickFieldAsync()
    {
        var labels = new List<object?>();
        for (int i = 0; i < Fields.Count; i++) labels.Add((i + 1) + ". " + Fields[i]);
        int? p = await App.SelectAsync([Localizer.S("WhichField")], labels, esc: "EscapeCancel");
        return p;
    }

    // ---- Step 6: report formats (table & label) exported to .txt ----

    private async Task ReportAsync()
    {
        int? kind = await App.SelectAsync([Localizer.S("ReportMenu")],
            [Localizer.S("TableReport"), Localizer.S("LabelReport")], esc: "EscapeCancel");
        if (kind is null) return;

        var rows = Rows();
        if (rows.Count == 0) { App.Msg = Localizer.S("NoRecordsYet"); return; }

        string? fn = kind.Value == 0
            ? await ExportPathAsync(".txt", "ReportToTextFile")
            : await ExportPathAsync(".txt", "LabelReportToFile");
        if (fn is null) return;

        var sb = new System.Text.StringBuilder();
        if (kind.Value == 0)
            await BuildTableReport(sb, rows);
        else
            await BuildLabelReport(sb, rows);

        await DownloadText(fn, sb.ToString());
        App.Msg = Localizer.T("ReportSavedTo", fn);
    }

    /// <summary>Aligned text table: pick columns (comma list or all), auto widths.</summary>
    private async Task BuildTableReport(System.Text.StringBuilder sb, List<int> rows)
    {
        string? colInput = await App.AskAsync("WhichColumns", "");
        if (colInput is null) return;

        var cols = ParseColumnList(colInput);
        int[] w = new int[cols.Count];
        for (int c = 0; c < cols.Count; c++)
        {
            int m = Fields[cols[c]].Length;
            foreach (int ri in rows) m = Math.Max(m, Records[ri][cols[c]].Length);
            w[c] = Math.Min(24, Math.Max(3, m));
        }

        sb.AppendLine(string.Join(" ", cols.Zip(w, (ci, wi) => Fields[ci].PadRight(wi))));
        sb.AppendLine(string.Join(" ", w.Select(x => new string('-', x))));
        foreach (int ri in rows)
            sb.AppendLine(string.Join("  ", cols.Zip(w, (ci, wi) => Records[ri][ci].PadRight(wi))));
    }

    /// <summary>Mailing-label blocks: first N categories become the label lines.</summary>
    private async Task BuildLabelReport(System.Text.StringBuilder sb, List<int> rows)
    {
        string? perInput = await App.AskAsync("LinesPerLabel", "4");
        if (perInput is null) return;
        int per = int.TryParse(perInput.Trim(), out int p) ? Math.Max(1, Math.Min(6, p)) : 4;

        for (int k = 0; k < rows.Count; k++)
        {
            var rec = Records[rows[k]];
            for (int j = 0; j < per && j < Fields.Count; j++)
                sb.AppendLine(rec[j]);
            if (k < rows.Count - 1) sb.AppendLine();   // blank line between labels
        }
    }

    /// <summary>Parse "1,3,5" → field indices [0,2,4]; blank/invalid → all fields in order.</summary>
    private List<int> ParseColumnList(string input)
    {
        var result = new List<int>();
        foreach (var part in input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (int.TryParse(part, out int n) && n >= 1 && n <= Fields.Count)
                result.Add(n - 1);
        if (result.Count == 0) result.AddRange(Enumerable.Range(0, Fields.Count));
        return result;
    }

    private async Task ExportAsync()
    {
        string? fn = await ExportPathAsync(".csv", "ExportRecordsToCsv");
        if (fn is null) return;

        var rows = Rows();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(string.Join(",", Fields.Select(CsvEscape)));
        foreach (int i in rows)
            sb.AppendLine(string.Join(",", Records[i].Select(CsvEscape)));

        await DownloadText(fn, sb.ToString());
        App.Msg = Localizer.T("ExportedNRecordsToFile", rows.Count, fn);
    }

    private static string CsvEscape(string s) =>
        s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    // ---- JSON ----

    public override Dictionary<string, object?> ToJson()
    {
        PadWidths();
        PadLookups();
        var data = new Dictionary<string, object?> { ["fields"] = Fields, ["records"] = Records, ["widths"] = Widths };
        if (Lookups.Any(l => l is not null)) data["lookups"] = Lookups;
        if (Rules.Count > 0)
            data["rules"] = Rules.Select(r => new { field = r.Field, op = r.Op, val = r.Val }).ToList();
        return data;
    }

    public override int Size() => Records.Sum(rec => rec.Sum(v => v.Length + 1));

    public override void FromJson(JsonElement d)
    {
        Fields = [.. d.GetProperty("fields").EnumerateArray().Select(e => Text.Clean(e.GetString() ?? ""))];
        if (Fields.Count == 0) Fields = ["Category 1"];
        int n = Fields.Count;
        Records = d.GetProperty("records").EnumerateArray().Select(rec =>
            rec.EnumerateArray().Select(e => Text.Clean(e.GetString() ?? ""))
                .Take(n).ToList()).ToList();
        var w = new List<int>();
        if (d.TryGetProperty("widths", out var widthsEl))
            w.AddRange(widthsEl.EnumerateArray().Select(e => e.GetInt32()));
        Widths = [.. w];
        PadWidths();

        Lookups = Enumerable.Repeat<string?>(null, n).ToList();
        if (d.TryGetProperty("lookups", out var lkEl) && lkEl.ValueKind == JsonValueKind.Array)
        {
            int li = 0;
            foreach (var e in lkEl.EnumerateArray())
            {
                if (li >= n) break;
                Lookups[li] = e.ValueKind == JsonValueKind.String ? (e.GetString() ?? "") : null;
                li++;
            }
        }

        Rules = [];
        if (d.TryGetProperty("rules", out var ruEl) && ruEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in ruEl.EnumerateArray())
            {
                int fi = e.GetProperty("field").GetInt32();
                string op = e.GetProperty("op").GetString() ?? "contains";
                string val = e.TryGetProperty("val", out var ve) ? (ve.GetString() ?? "") : "";
                Rules.Add((fi, op, val));
            }
        }
    }

    protected override string HelpText() => Localizer.Help("DB");
}
