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

    public DBDoc(App app, string name) : base(app, name) { }

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

            for (int j = 0; j < vh; j++)
            {
                int fi = _ftop + j;
                if (fi >= Fields.Count) break;
                int y = 4 + j;
                App.Put(y, 2, Fields[fi][..Math.Min(nl, Fields[fi].Length)].PadLeft(nl) + ":", App.A_B);
                string val = rec[fi][..Math.Min(W - nl - 7, rec[fi].Length)];
                if (fi == _f) App.Put(y, nl + 4, val.Length > 0 ? val : " ", App.A_R);
                else App.Put(y, nl + 4, val);
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

                foreach (var (fi, x) in vis)
                {
                    int w = ColW(fi);
                    bool cur = ri == _cur && fi == _f;
                    string val = rec[fi][..Math.Min(w - 1, rec[fi].Length)].PadRight(w - 1);
                    App.Put(5 + j, x, val, cur ? App.A_R : App.A_N);
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
        var rec = new List<string>(new string[Fields.Count]);
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

        if (k.Length == 1 && char.IsLetterOrDigit(k[0]))
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
        return new() { ["fields"] = Fields, ["records"] = Records, ["widths"] = Widths };
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
    }

    protected override string HelpText() => Localizer.Help("DB");
}
