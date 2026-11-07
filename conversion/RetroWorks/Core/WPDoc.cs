using System.Text.Json;
using System.Text.RegularExpressions;

namespace RetroWorks.Core;

/// <summary>Word Processor document. Ported from retroworks.py WPDoc.</summary>
public sealed class WPDoc : Doc
{
    public override string Kind => "wp";

    public List<string> Paras { get; set; } = [""];
    public List<char> Dirs { get; set; } = ['L'];
    // Per-character style: '0' plain, '1' bold, '2' underline, '3' both
    public List<string> Sty { get; set; } = [""];

    private int _style;
    private int _pi, _ci, _top;
    private int? _goal;
    private bool _insert = true;
    private string _lastFind = "";

    // Per-paragraph formatting (Step 4): left/right indent in columns + hanging flag.
    public List<int> IndL { get; set; } = [0];
    public List<int> IndR { get; set; } = [0];
    public List<bool> Hang { get; set; } = [false];

    // Document header/footer (printed on every page; "Pg" in the footer = page number).
    private string _header = "";
    private string _footer = "";

    public WPDoc(App app, string name) : base(app, name) { }

    private char DefaultDir() => Localizer.Lang == "he" ? 'R' : 'L';

    public void DetectDirs()
    {
        char defDir = DefaultDir();
        Dirs = Paras.Select(p => { var fs = Bidi.FirstStrong(p); return (fs is not null && fs.Length > 0) ? fs[0] : defDir; }).ToList();
    }

    private void FixDirs()
    {
        int n = Paras.Count;
        while (Dirs.Count < n) Dirs.Add(DefaultDir());
        Dirs = Dirs.Take(n).ToList();
        NormalizeLists();
    }

    /// <summary>
    /// Ensure every per-paragraph list matches Paras.Count. This is the single
    /// self-healing point: any code path that changes Paras without resizing the
    /// formatting lists (Enter split, Backspace/Delete merge, Cut, programmatic
    /// creation such as Error Log documents) cannot crash Layout()/Draw() —
    /// they are normalized before anything indexes into them.
    /// </summary>
    private void NormalizeLists()
    {
        int n = Paras.Count;
        while (Sty.Count < n) Sty.Add("");
        Sty = Sty.Take(n).ToList();
        while (IndL.Count < n) IndL.Add(0);
        IndL = IndL.Take(n).ToList();
        while (IndR.Count < n) IndR.Add(0);
        IndR = IndR.Take(n).ToList();
        while (Hang.Count < n) Hang.Add(false);
        Hang = Hang.Take(n).ToList();
        for (int i = 0; i < n; i++)
        {
            if (Sty[i].Length != Paras[i].Length)
                Sty[i] = (Sty[i] + new string('0', Paras[i].Length))[..Paras[i].Length];
        }
    }

    public override Dictionary<string, object?> ToJson()
    {
        FixDirs();
        var data = new Dictionary<string, object?>
        {
            ["paras"] = Paras,
            ["dirs"] = new string(Dirs.ToArray()),
        };
        if (Sty.Any(st => st.Trim('0').Length > 0))
            data["styles"] = Sty;
        if (IndL.Any(v => v != 0) || IndR.Any(v => v != 0) || Hang.Any(v => v))
        {
            data["indl"] = IndL;
            data["indr"] = IndR;
            data["hang"] = Hang;
        }
        if (_header.Length > 0) data["header"] = _header;
        if (_footer.Length > 0) data["footer"] = _footer;
        return data;
    }

    public override int Size() => Paras.Sum(p => p.Length + 1);

    public override void FromJson(JsonElement d)
    {
        // Keep real tabs (WP stores them); other control chars are still stripped.
        Paras = [.. d.GetProperty("paras").EnumerateArray().Select(e => Text.CleanKeepTabs(e.GetString() ?? ""))];
        if (Paras.Count == 0) Paras = [""];

        if (d.TryGetProperty("dirs", out var dirsEl) && dirsEl.ValueKind == JsonValueKind.String)
        {
            string dirs = dirsEl.GetString()!;
            if (dirs.Length == Paras.Count && dirs.All(c => c is 'L' or 'R'))
                Dirs = dirs.Select(c => c).ToList();
            else DetectDirs();
        }
        else DetectDirs();

        Sty = Enumerable.Repeat("", Paras.Count).ToList();
        if (d.TryGetProperty("styles", out var styEl) && styEl.ValueKind == JsonValueKind.Array
            && styEl.GetArrayLength() == Paras.Count)
        {
            Sty = [.. styEl.EnumerateArray().Select(e =>
            {
                string s = e.GetString() ?? "";
                return s.All(c => "0123".Contains(c)) ? s : "";
            })];
        }
        else Sty = Paras.Select(_ => "").ToList();

        IndL = Enumerable.Repeat(0, Paras.Count).ToList();
        IndR = Enumerable.Repeat(0, Paras.Count).ToList();
        Hang = Enumerable.Repeat(false, Paras.Count).ToList();
        if (d.TryGetProperty("indl", out var il) && il.ValueKind == JsonValueKind.Array && il.GetArrayLength() == Paras.Count)
            IndL = [.. il.EnumerateArray().Select(e => e.GetInt32())];
        if (d.TryGetProperty("indr", out var ir) && ir.ValueKind == JsonValueKind.Array && ir.GetArrayLength() == Paras.Count)
            IndR = [.. ir.EnumerateArray().Select(e => e.GetInt32())];
        if (d.TryGetProperty("hang", out var hg) && hg.ValueKind == JsonValueKind.Array && hg.GetArrayLength() == Paras.Count)
            Hang = [.. hg.EnumerateArray().Select(e => e.GetBoolean())];

        _header = d.TryGetProperty("header", out var he) ? (he.GetString() ?? "") : "";
        _footer = d.TryGetProperty("footer", out var fe) ? (fe.GetString() ?? "") : "";

        // Self-heal on load: a saved file (or any doc built programmatically)
        // must have all per-paragraph lists sized to Paras before first Draw.
        FixDirs();
    }

    private (int H, int W, int ww, int x0) Geometry() => (App.MinH, App.MinW, Math.Min(76, App.MinW - 4), Math.Max(2, (App.MinW - Math.Min(76, App.MinW - 4)) / 2));

    private List<(int pi, int s, int e, bool last)> Layout(int ww)
    {
        var rows = new List<(int, int, int, bool)>();
        for (int pi = 0; pi < Paras.Count; pi++)
        {
            // Wrap within the space left between the left and right indents so the
            // paragraph visually ends at the right margin. Clamp to a sane minimum
            // in case both indents are set very wide.
            int wrapW = Math.Max(10, ww - IndL[pi] - IndR[pi]);
            var starts = WrapPara(Paras[pi], wrapW);
            for (int r = 0; r < starts.Length; r++)
            {
                bool last = r == starts.Length - 1;
                rows.Add((pi, starts[r], last ? Paras[pi].Length : starts[r + 1], last));
            }
        }
        return rows;
    }

    /// <summary>Word-wrap a paragraph at width w; breaks on spaces or tabs.</summary>
    private static int[] WrapPara(string s, int w)
    {
        var starts = new List<int> { 0 };
        int i = 0, n = s.Length;
        while (n - i > w)
        {
            int brk = i + w;
            for (int j = i + w; j > i; j--)
                if (s[j - 1] is ' ' or '\t') { brk = j; break; }
            starts.Add(brk);
            i = brk;
        }
        return [.. starts];
    }

    /// <summary>Column a tab at logical position pos advances to (8-column stops).</summary>
    private static int TabStop(int pos) => ((pos / 8) + 1) * 8;

    /// <summary>Effective left indent for a display row (hanging adds 4 cols to continuation lines).</summary>
    private int RowIndent(int pi, int s) => IndL[pi] + (Hang[pi] && s > 0 ? 4 : 0);

    private int Crow(List<(int pi, int s, int e, bool last)> rows)
    {
        for (int i = 0; i < rows.Count; i++)
        {
            var (pi, s, e, last) = rows[i];
            if (pi == _pi && s <= _ci && (_ci < e || last)) return i;
        }
        return 0;
    }

    public override void Draw()
    {
        // Ensure per-paragraph lists (IndL/IndR/Hang/Sty/Dirs) match Paras.Count
        // BEFORE Layout() indexes into them. Several edit paths (Enter, Backspace
        // merge, Delete merge, Cut) change Paras without resizing these lists;
        // calling FixDirs here is the single safety point that prevents
        // IndexOutOfRange on the next frame.
        FixDirs();

        var (H, W, ww, x0) = Geometry();
        var rows = Layout(ww);
        int cr = Crow(rows);
        int vh = H - 4;
        _top = Math.Min(_top, cr);
        if (cr >= _top + vh) _top = cr - vh + 1;
        int col = _ci - rows[cr].s;
        int words = Paras.Sum(p => p.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
        bool rtl = Dirs[_pi] == 'R';

        var footParts = new List<string> { Localizer.S(_insert ? "Insert" : "Overwrite") };
        if ((_style & 1) != 0) footParts.Add(Localizer.S("Bold"));
        if ((_style & 2) != 0) footParts.Add(Localizer.S("Underline"));
        footParts.Add(Localizer.S(rtl ? "RightToLeft" : "LeftToRight"));
        footParts.Add(Localizer.T("PgN", PageOf(cr, rows)));
        if (IndL[_pi] > 0) footParts.Add(Localizer.T("IndentN", IndL[_pi]));
        if (IndR[_pi] > 0) footParts.Add(Localizer.T("RIndN", IndR[_pi]));
        if (Hang[_pi]) footParts.Add(Localizer.S("Hanging"));
        footParts.Add(Localizer.T("LineAOfB", cr + 1, rows.Count));
        footParts.Add(Localizer.T("ColumnN", col + 1));
        footParts.Add(Localizer.T("NWords", words));

        App.Frame(Title(), "ReviewAddChange", "EscapeMainMenu", "AltKHelp", string.Join("   ", footParts));

        // Ruler: ticks every 5 columns; 'v' marks the current paragraph's left indent.
        var ruler = new char[W];
        Array.Fill(ruler, '=');
        for (int k = 0; k <= ww && x0 + k < W; k += 5)
            ruler[x0 + k] = '|';
        int indMark = RowIndent(_pi, rows[cr].s);
        if (indMark > 0 && x0 + indMark < W) ruler[x0 + indMark] = 'v';
        App.Put(1, 0, new string(ruler));

        // Text rows
        for (int i = 0; i < vh; i++)
        {
            int r = _top + i;
            if (r >= rows.Count) break;
            DrawRow(2 + i, rows[r], ww, x0);
        }

        // Cursor: display column of the logical position (tabs expand to 8-col stops).
        var (seg, widths, _) = RowVisual(rows[cr], ww);
        int li = _ci - rows[cr].s;
        int cx = x0 + RowIndent(_pi, rows[cr].s) + DisplayCol(seg, widths, Math.Min(li, seg.Length));
        App.SetCursor(2 + cr - _top, Math.Max(0, Math.Min(W - 1, cx)));
    }

    /// <summary>Display width of each char in a row segment (a tab spans to the next 8-col stop).</summary>
    private static int[] CharWidths(string seg)
    {
        var w = new int[seg.Length];
        int col = 0;
        for (int i = 0; i < seg.Length; i++)
        {
            if (seg[i] == '\t') { int stop = TabStop(col); w[i] = Math.Max(1, stop - col); col = stop; }
            else { w[i] = 1; col++; }
        }
        return w;
    }

    /// <summary>Display column of logical index i within a segment (sum of expanded widths).</summary>
    private static int DisplayCol(string seg, int[] widths, int i)
    {
        int c = 0;
        for (int j = 0; j < i && j < widths.Length; j++) c += widths[j];
        return c;
    }

    /// <summary>Page number of a display row: header+footer take 2 lines, 60 text lines per page.</summary>
    private static int PageOf(int cr, List<(int pi, int s, int e, bool last)> rows)
    {
        int per = Math.Max(1, (App.MinH - 4) - 2);
        return cr / per + 1;
    }

    private (string seg, int[] widths, string vis) RowVisual((int pi, int s, int e, bool last) row, int ww)
    {
        string seg = Paras[row.pi][row.s..row.e];
        var widths = CharWidths(seg);
        // Pad to at least one column so an empty line still renders.
        int total = 0;
        foreach (int x in widths) total += x;
        if (total < 1)
        {
            seg = " ";
            widths = [1];
        }
        string vis = Bidi.BidiVisual(seg, Dirs[row.pi].ToString());
        return (seg, widths, vis);
    }

    private void DrawRow(int y, (int pi, int s, int e, bool last) row, int ww, int x0)
    {
        var (seg, widths, _) = RowVisual(row, ww);
        string st = Sty[row.pi];
        int ind = RowIndent(pi: row.pi, s: row.s);

        // LTR: place each char at its expanded display column. RTL: keep the legacy
        // bidi-visual path (indents are cosmetic there).
        if (Dirs[row.pi] == 'L')
        {
            int col = ind;
            for (int li = 0; li < seg.Length; li++)
            {
                int attr = App.A_N;
                int logPos = row.s + li;
                if (logPos < st.Length)
                {
                    int code = st[logPos] - '0';
                    if (code is 1 or 3) attr |= App.A_B;
                    if (code is 2 or 3) attr |= App.A_U;
                }
                for (int w = 0; w < widths[li]; w++)
                {
                    int x = x0 + col + w;
                    if (x >= App.MinW) return;
                    char ch = w == 0 ? seg[li] : ' ';   // tab renders as its first cell, rest blank
                    App.PutCells(y, x, ch.ToString(), attr);
                }
                col += widths[li];
            }
        }
        else
        {
            string vis = Bidi.BidiVisual(seg + new string(' ', Math.Max(1, ww + 1 - seg.Length)), "R");
            for (int k = 0; k < vis.Length && x0 + ind + k < App.MinW; k++)
            {
                int attr = App.A_N;
                int logPos = -1;
                for (int li = 0; li < seg.Length; li++)
                    if (vis[k] == seg[li] && (logPos = row.s + li) >= 0) break;
                if (logPos >= 0 && logPos < st.Length)
                {
                    int code = st[logPos] - '0';
                    if (code is 1 or 3) attr |= App.A_B;
                    if (code is 2 or 3) attr |= App.A_U;
                }
                App.PutCells(y, x0 + ind + k, vis[k].ToString(), attr);
            }
        }
    }

    protected override async Task HandleAsync(string k)
    {
        var (H, W, ww, x0) = Geometry();
        FixDirs();
        string p = Paras[_pi];
        string st = Sty[_pi];

        if (k is not (Key.Up or Key.Down or Key.PgUp or Key.PgDn)) _goal = null;
        if (Dirs[_pi] == 'R' && k is Key.Left or Key.Right)
            k = k == Key.Left ? Key.Right : Key.Left;

        // Typing a character
        if (k.Length == 1 || k == Key.Tab)
        {
            string s = k.Length == 1 ? k : "    ";
            string code = new string((char)('0' + _style), s.Length);
            if (_insert || _ci >= p.Length)
            {
                p = p[.._ci] + s + p[_ci..];
                st = st[.._ci] + code + st[_ci..];
            }
            else
            {
                p = p[.._ci] + s + p[(_ci + s.Length)..];
                st = st[.._ci] + code + st[(_ci + s.Length)..];
            }
            Paras[_pi] = p;
            Sty[_pi] = st;
            _ci += s.Length;
            Dirty = true;
            return;
        }

        switch (k)
        {
            case Key.Enter:
                FixDirs();
                Paras[_pi] = p[.._ci];
                Paras.Insert(_pi + 1, p[_ci..]);
                Dirs.Insert(_pi + 1, Dirs[_pi]);
                Sty[_pi] = st[.._ci];
                Sty.Insert(_pi + 1, st[_ci..]);
                _pi++;
                _ci = 0;
                Dirty = true;
                break;

            case Key.Backspace:
                if (_ci > 0)
                {
                    Paras[_pi] = p[..(_ci - 1)] + p[_ci..];
                    Sty[_pi] = st[..(_ci - 1)] + st[_ci..];
                    _ci--;
                    Dirty = true;
                }
                else if (_pi > 0)
                {
                    string prev = Paras[_pi - 1];
                    _ci = prev.Length;
                    Paras[_pi - 1] = prev + p;
                    Sty[_pi - 1] += st;
                    Paras.RemoveAt(_pi);
                    Dirs.RemoveAt(_pi);
                    Sty.RemoveAt(_pi);
                    _pi--;
                    Dirty = true;
                }
                break;

            case Key.Delete:
            case "A-D":
                if (_ci < p.Length)
                {
                    Paras[_pi] = p[.._ci] + p[(_ci + 1)..];
                    Sty[_pi] = st[.._ci] + st[(_ci + 1)..];
                    Dirty = true;
                }
                else if (_pi < Paras.Count - 1)
                {
                    Paras[_pi] = p + Paras[_pi + 1];
                    Sty[_pi] = st + Sty[_pi + 1];
                    Paras.RemoveAt(_pi + 1);
                    Dirs.RemoveAt(_pi + 1);
                    Sty.RemoveAt(_pi + 1);
                    Dirty = true;
                }
                break;

            case Key.Left:
                if (_ci > 0) _ci--;
                else if (_pi > 0) { _pi--; _ci = Paras[_pi].Length; }
                break;

            case Key.Right:
                if (_ci < p.Length) _ci++;
                else if (_pi < Paras.Count - 1) { _pi++; _ci = 0; }
                break;

            case Key.Up or Key.Down or Key.PgUp or Key.PgDn or Key.Home or Key.End:
                var rows = Layout(ww);
                int cr = Crow(rows);
                var (rpi, rs, re, rlast) = rows[cr];
                if (k == Key.Home) _ci = rs;
                else if (k == Key.End) _ci = rlast ? re : Math.Max(rs, re - 1);
                else
                {
                    _goal ??= _ci - rs;
                    int step = k switch { Key.Up => -1, Key.Down => 1, Key.PgUp => -(H - 5), _ => H - 5 };
                    int t = Math.Max(0, Math.Min(rows.Count - 1, cr + step));
                    if (k is Key.PgUp or Key.PgDn) _top = Math.Max(0, _top + step);
                    var (tpi, ts, te, tlast) = rows[t];
                    int mx = tlast ? te : Math.Max(ts, te - 1);
                    _pi = tpi;
                    _ci = Math.Min(ts + _goal.Value, mx);
                }
                break;

            case "A-T": _pi = _ci = 0; break;
            case "A-G": _pi = Paras.Count - 1; _ci = Paras[^1].Length; break;
            case "A-E": _insert = !_insert; break;
            case "A-Y":
                if (_ci < p.Length) { Paras[_pi] = p[.._ci]; Sty[_pi] = st[.._ci]; Dirty = true; }
                break;

            case "A-X":
                ClipWp = [p]; ClipWpDirs = [Dirs[_pi]]; ClipWpSty = [st];
                App.ClipText = p;   // cross-module text bridge (v1.7)
                if (Paras.Count > 1) { Paras.RemoveAt(_pi); Dirs.RemoveAt(_pi); Sty.RemoveAt(_pi); _pi = Math.Min(_pi, Paras.Count - 1); }
                else { Paras = [""]; Sty = [""]; }
                _ci = 0; Dirty = true;
                App.Msg = Localizer.S("ParagraphCutToClipboard");
                break;

            case "A-C":
                ClipWp = [p]; ClipWpDirs = [Dirs[_pi]]; ClipWpSty = [st];
                App.ClipText = p;   // cross-module text bridge (v1.7)
                App.Msg = Localizer.S("ParagraphCopiedToClipboard");
                break;

            case "A-V":
                if (ClipWp.Count > 0)
                {
                    Paras.InsertRange(_pi, ClipWp);
                    Dirs.InsertRange(_pi, ClipWpDirs.Take(ClipWp.Count));
                    Sty.InsertRange(_pi, ClipWpSty.Take(ClipWp.Count));
                    FixDirs();
                    _pi += ClipWp.Count;
                    Dirty = true;
                    App.Msg = Localizer.S("PastedAboveCursor");
                }
                else if (App.ClipText.Length > 0)
                {
                    // Cross-module paste: insert the shared text as new paragraphs.
                    var lines = App.ClipText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                    Paras.InsertRange(_pi, lines);
                    Dirs.InsertRange(_pi, Enumerable.Repeat('L', lines.Length));
                    Sty.InsertRange(_pi, Enumerable.Repeat("", lines.Length));
                    FixDirs();
                    _pi += lines.Length;
                    Dirty = true;
                    App.Msg = Localizer.S("PastedAboveCursor");
                }
                else App.Msg = Localizer.S("ClipboardEmpty");
                break;

            case "A-B":
                _style ^= 1;
                App.Msg = Localizer.S((_style & 1) != 0 ? "BoldOn" : "BoldOff");
                break;

            case "A-U":
                _style ^= 2;
                App.Msg = Localizer.S((_style & 2) != 0 ? "UnderlineOn" : "UnderlineOff");
                break;

            case "A-L":
                Dirs[_pi] = Dirs[_pi] == 'R' ? 'L' : 'R';
                Dirty = true;
                App.Msg = Localizer.S(Dirs[_pi] == 'R' ? "ParagraphNowRTL" : "ParagraphNowLTR");
                break;

            case "A-I":   // left indent: 0 → 4 → 8 → … → 24 → 0
                IndL[_pi] = (IndL[_pi] + 4) % 28;
                Dirty = true;
                App.Msg = Localizer.T("LeftIndentN", IndL[_pi]);
                break;

            case "A-O":   // right indent: 0 → 4 → … → 16 → 0 (narrows the wrap width on screen and in print)
                IndR[_pi] = (IndR[_pi] + 4) % 20;
                Dirty = true;
                App.Msg = Localizer.T("RightIndentN", IndR[_pi]);
                break;

            case "A-H":   // hanging indent: continuation lines start 4 cols in
                Hang[_pi] = !Hang[_pi];
                Dirty = true;
                App.Msg = Localizer.S(Hang[_pi] ? "HangingOn" : "HangingOff");
                break;

            case "AS-H":   // Alt-Shift-H: set header
                string? h = await App.AskAsync("HeaderPrompt", _header);
                if (h is not null) { _header = h; Dirty = true; }
                break;

            case "AS-F":   // Alt-Shift-F: set footer ("Pg" → page number when printing)
                string? f = await App.AskAsync("FooterPrompt", _footer);
                if (f is not null) { _footer = f; Dirty = true; }
                break;

            case "A-F": await FindAsync(); break;
            case "A-R": await ReplaceAllAsync(); break;
            case "A-W": await SpellCheckAsync(); break;
            case "A-P": await ExportAsync(); break;

            case "AS-P":   // Alt-Shift-P: print to PDF (Alt-P is the .txt export)
                string? pdfFn = await PrinterEmulatorPdfService.PrintWpAsync(App, this);
                if (pdfFn is not null) App.Msg = Localizer.T("PdfSavedTo", pdfFn);
                break;
        }
    }

    private async Task FindAsync()
    {
        string? q = await App.AskAsync("Find", _lastFind);
        if (string.IsNullOrEmpty(q)) return;
        _lastFind = q;
        string ql = q.ToLowerInvariant();
        int n = Paras.Count;

        for (int step = 0; step <= n; step++)
        {
            int pi = (_pi + step) % n;
            string p = Paras[pi].ToLowerInvariant();
            int j = step == 0 ? p.IndexOf(ql, _ci + 1, StringComparison.Ordinal) : p.IndexOf(ql, StringComparison.Ordinal);
            if (step == n && j > _ci) j = -1;
            if (j >= 0) { _pi = pi; _ci = j; return; }
        }
        App.Msg = Localizer.T("TextNotFound", q);
    }

    private async Task ReplaceAllAsync()
    {
        string? q = await App.AskAsync("Replace", _lastFind);
        if (string.IsNullOrEmpty(q)) return;
        string? r = await App.AskAsync(Localizer.T("ReplaceTextWith", q));
        if (r is null) return;

        int total = 0;
        var rx = new Regex(Regex.Escape(q), RegexOptions.IgnoreCase);
        FixDirs();
        for (int i = 0; i < Paras.Count; i++)
        {
            string p = Paras[i];
            string st = Sty[i];
            var matches = rx.Matches(p);
            if (matches.Count == 0) continue;

            var sb = new System.Text.StringBuilder();
            var ssb = new System.Text.StringBuilder();
            int last = 0;
            foreach (Match m in matches)
            {
                sb.Append(p[last..m.Index]).Append(r);
                ssb.Append(st[last..m.Index]).Append(st[m.Index] * r.Length);
                last = m.Index + m.Length;
                total++;
            }
            sb.Append(p[last..]);
            ssb.Append(st[last..]);
            Paras[i] = sb.ToString();
            Sty[i] = ssb.ToString();
        }
        _ci = Math.Min(_ci, Paras[_pi].Length);
        if (total > 0) Dirty = true;
        App.Msg = Localizer.T("ReplacedNOccurrences", total);
    }

    // ---- Spell Check (Alt-W) ----

    /// <summary>
    /// Retro-style spell check: scans the document for unknown Latin words and
    /// offers to replace, ignore, or skip each one. Hebrew text is skipped.
    /// </summary>
    private async Task SpellCheckAsync()
    {
        int H = App.MinH, W = App.MinW;
        var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int checkedCount = 0, replacedCount = 0;

        // A document spell check always starts at the top — save the user's
        // cursor and restore it when we finish (or they cancel).
        int savePi = _pi, saveCi = _ci;
        _pi = 0; _ci = 0;

        while (true)
        {
            // Find the next unknown word at or after the scan position.
            var hit = NextUnknownWord(ignored);
            if (hit is null) break;

            var (pi, start, end, word) = hit.Value;
            checkedCount++;

            // Position the cursor on the flagged word.
            _pi = pi; _ci = start;
            Draw();

            // Build the prompt line:  WORD   [1]word2  [2]word3  (A)accept (R)eplace (I)gnore (S)kip
            string? s1 = null, s2 = null;
            var sugg = SpellCheck.Suggest(word);
            if (sugg.Count > 0) s1 = sugg[0];
            if (sugg.Count > 1) s2 = sugg[1];

            string prompt = Localizer.S("SpellPrompt");   // "Spelling:"
            string line = $"{prompt} {word}";
            if (s1 is not null) line += $"  [1]{s1}";
            if (s2 is not null) line += $"  [2]{s2}";
            // Key legend, e.g. "[A]ccept [R]eplace [I]gnore [S]kip" — localizable.
            line += "   " + Localizer.S("SpellKeys");

            App.Put(H - 1, 0, new string(' ', W));
            App.Put(H - 1, 0, line[..Math.Min(W, line.Length)], App.A_B);
            // Draw() already placed the blinking cursor on the word (via _pi/_ci).

            // Wait for the user's choice.
            while (true)
            {
                string k = await App.KeyAsync();
                if (k == Key.Escape) { _pi = savePi; _ci = saveCi; App.CursorAt = null; return; }   // cancel

                char c = k.Length == 1 ? char.ToLowerInvariant(k[0]) : '\0';

                string? chosen = null;    // replacement text, if the user picked one
                bool acceptAsIs = false;   // A / I — treat word as fine for this session

                switch (c)
                {
                    case 'a':
                    case 'i':
                        acceptAsIs = true;
                        break;
                    case '1' when s1 is not null: chosen = s1; break;
                    case '2' when s2 is not null: chosen = s2; break;
                    case 'r':
                        if (s1 is not null) { chosen = s1; }
                        else
                        {
                            App.Put(H - 1, 0, new string(' ', W));
                            var typed = await App.AskAsync("SpellReplaceWith", word);
                            if (typed is null) { _pi = savePi; _ci = saveCi; App.CursorAt = null; return; }
                            chosen = typed;
                        }
                        break;
                    case 's':
                        break;   // skip — no change, just advance past it
                    default:
                        continue;   // not a spell-check key; keep waiting
                }

                if (acceptAsIs)
                {
                    ignored.Add(word);   // won't be flagged again this session
                    _ci = end;           // advance past it
                    break;
                }
                else if (chosen is not null)
                {
                    ReplaceWord(pi, start, end, chosen);
                    replacedCount++;
                    break;
                }
                else   // skip
                {
                    _ci = end;           // move past so we don't re-flag this occurrence
                    break;
                }
            }
        }

        _pi = savePi; _ci = saveCi;   // restore the user's original cursor
        App.CursorAt = null;
        App.Msg = Localizer.T("SpellDone", checkedCount, replacedCount);
    }

    /// <summary>Find the next unknown Latin word at or after the cursor position.</summary>
    private (int pi, int start, int end, string word)? NextUnknownWord(HashSet<string> ignored)
    {
        var hit = SpellCheck.NextWord(Paras, _pi, _ci);
        if (hit is null) return null;
        var (pi, start, end, word) = hit.Value;
        if (SpellCheck.IsKnown(word) || ignored.Contains(word))
            return NextUnknownWordAfter(pi, end, ignored);
        return (pi, start, end, word);
    }

    private (int pi, int start, int end, string word)? NextUnknownWordAfter(int pi, int ci, HashSet<string> ignored)
    {
        var hit = SpellCheck.NextWord(Paras, pi, ci);
        if (hit is null) return null;
        var (p2, s2, e2, w2) = hit.Value;
        if (SpellCheck.IsKnown(w2) || ignored.Contains(w2))
            return NextUnknownWordAfter(p2, e2, ignored);
        return (p2, s2, e2, w2);
    }

    /// <summary>Replace a word in the paragraph, preserving style codes.</summary>
    private void ReplaceWord(int pi, int start, int end, string replacement)
    {
        string p = Paras[pi];
        string st = Sty[pi];

        // Build new paragraph: before + replacement + after.
        string newP = p[..start] + replacement + p[end..];

        // Style: keep the style of the first char of the old word for all new chars.
        string styleCode = st.Length > start ? st[start].ToString() : "0";
        string newSt = st[..start] + new string(styleCode[0], replacement.Length) + st[end..];

        Paras[pi] = newP;
        Sty[pi] = newSt;
        _ci = start + replacement.Length;
        Dirty = true;
    }

    private async Task ExportAsync()
    {
        string? fn = await ExportPathAsync(".txt", "PrintToTextFile");
        if (fn is null) return;

        var pages = BuildPrintPages();
        int totalLines = pages.Sum(p => p.Count - 2);   // minus header + footer per page
        var flat = new List<string>(totalLines);
        foreach (var pg in pages) flat.AddRange(pg);

        await DownloadText(fn, string.Join('\n', flat) + '\n');
        App.Msg = Localizer.T("PrintedNLinesToFile", totalLines, fn);
    }

    /// <summary>
    /// Compose the document into fixed-width print pages: each page is a header line,
    /// up to 58 body lines (76 columns, indents applied, tabs expanded), and a footer
    /// where the literal "Pg" becomes the page number. Shared by the .txt export and the
    /// PDF printer so both render identically.
    /// </summary>
    public List<List<string>> BuildPrintPages()
    {
        const int width = 76;          // print line width
        const int perPage = 58;        // text lines between header and footer

        // Single pass: wrap each paragraph between the left and right indents,
        // apply indent + expand tabs to 8-column stops.
        var outLines = new List<string>();
        for (int pi = 0; pi < Paras.Count; pi++)
        {
            string p = Paras[pi];
            int wrapW = Math.Max(10, width - IndL[pi] - IndR[pi]);   // space between indents
            // Wrap into segments; an empty paragraph yields one blank line.
            var segs = new List<(string text, int start)>();
            if (p.Length == 0)
                segs.Add(("", 0));
            else
            {
                int i = 0;
                while (true)
                {
                    if (p.Length - i <= wrapW) { segs.Add((p[i..], i)); break; }
                    int brk = i + wrapW;
                    for (int j = i + wrapW; j > i; j--)
                        if (p[j - 1] is ' ' or '\t') { brk = j; break; }
                    segs.Add((p[i..brk], i));
                    i = brk;
                }
            }

            for (int r = 0; r < segs.Count; r++)
            {
                var (seg, start) = segs[r];
                int ind = RowIndent(pi, start);
                var sb = new System.Text.StringBuilder();
                int col = 0;
                foreach (char ch in seg)
                {
                    if (ch == '\t') { int stop = TabStop(col); sb.Append(new string(' ', Math.Max(1, stop - col))); col = stop; }
                    else { sb.Append(ch); col++; }
                }
                outLines.Add(new string(' ', ind) + sb.ToString());
            }
        }

        // Paginate: header line, up to perPage text lines, footer ("Pg" → page number).
        var pages = new List<List<string>>();
        for (int pg = 0; pg * perPage < outLines.Count || pg == 0; pg++)
        {
            int page = pg + 1;
            var lines = new List<string> { _header };
            for (int i = pg * perPage; i < Math.Min(outLines.Count, (pg + 1) * perPage); i++)
                lines.Add(outLines[i]);
            string foot = _footer.Replace("Pg", page.ToString());
            if (_footer.Length == 0) foot = Localizer.T("PrintPageN", page);
            lines.Add(foot);
            pages.Add(lines);
        }
        return pages;
    }

    /// <summary>Help text for the Word Processor (includes spell check).</summary>
    protected override string HelpText() => Localizer.Help("WP");
}
