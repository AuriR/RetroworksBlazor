using System.Collections.Concurrent;
using System.Text.Json;

namespace RetroWorks.Core;

/// <summary>
/// The application shell: 24×80 screen buffer, async keyboard input, stacked menus,
/// Desktop management, and the main menu loop. Ported from retroworks.py App class.
/// </summary>
public sealed class App
{
    public const int MinH = 24;
    public const int MinW = 80;
    public const int MaxDesktop = 12;
    public const int ProgramK = 72;

    /// <summary>Single source of truth for the app version (splash + About screen).</summary>
    public const string Version = "1.7";

    // Memory configurations
    public static readonly (string name, int kb)[] MemorySizes =
    [
        ("Apple //e  128K", 128),
        ("RamWorks III  512K", 512),
        ("RamWorks III  1 MB", 1024),
        ("RamWorks III  3 MB", 3072),
    ];

    // ---- Screen buffer (24 rows × 80 cols) ----
    public char[,] VChars = new char[MinH, MinW];
    public int[,] VAttrs = new int[MinH, MinW];
    public (int y, int x)? CursorAt;

    // Attribute bit flags: 1=Bold, 2=Underline, 4=Reverse
    public const int A_N = 0;
    public const int A_B = 1;
    public const int A_U = 2;
    public const int A_R = 4;

    // ---- State ----
    public List<Doc> Desktop { get; } = [];
    public Doc? Current { get; set; }
    public string Msg { get; set; } = "";
    public int MemKb { get; set; } = 1024;
    public string Theme { get; set; } = "green"; // "green" or "amber"

    /// <summary>CRT display emulation (scanlines, screen-door, rounded glass). Off by default.</summary>
    public bool CrtEmulation { get; set; }

    // ---- Multiple Desktops (v1.7) ----
    /// <summary>All named Desktops known to the app (persisted in localStorage).</summary>
    public List<(string Id, string Name)> Desktops { get; private set; } = [( "d1", "Desktop")];

    /// <summary>The id of the Desktop whose files are currently loaded into <see cref="Desktop"/>.</summary>
    public string CurrentDesktopId { get; set; } = "d1";

    /// <summary>Display name of the current Desktop (for the status bar).</summary>
    public string CurrentDesktopName =>
        Desktops.FirstOrDefault(d => d.Id == CurrentDesktopId).Name ?? "Desktop";

    // ---- Cross-module clipboard (v1.7) ----
    /// <summary>
    /// Plain-text bridge shared by all three modules. Each module keeps its own native
    /// clipboard (WP paragraphs, DB record, SS cell); when a paste finds the native
    /// clipboard empty it falls back to this text so content can cross module boundaries
    /// (e.g. copy WP lines → paste as DB records). Copy operations populate both.
    /// </summary>
    public string ClipText { get; set; } = "";

    /// <summary>True if any clipboard (native or shared) holds content.</summary>
    public bool AnyClipboard => ClipText.Length > 0;

    // ---- Key input (async) ----
    private readonly ConcurrentQueue<string> _keyQueue = new();
    private TaskCompletionSource<string>? _keyTcs;

    /// <summary>Raised after each draw so the Blazor component can re-render.</summary>
    public event Action? RenderRequested;

    /// <summary>Increments on every render request; used as a Blazor parameter to force grid refresh.</summary>
    public int RenderTick { get; private set; }

    // ---- JS interop (set by the Blazor component) ----
    public Func<string, string, Task>? DownloadFileFunc;
    public Func<string, List<List<string>>, Task>? PrintPdfFunc;
    public Func<string, Task<string?>>? LoadFileFunc;

    // localStorage persistence ("the disk" that survives refresh/restart).
    // v1.7: every call is scoped to a Desktop id (multiple named Desktops).
    public Func<string, string, string, Task<bool>>? SaveLocalFileFunc;   // (desktopId, name, json)
    public Func<string, string, Task<string?>>? LoadLocalFileFunc;        // (desktopId, name)
    public Func<string, Task<List<string>>>? ListLocalFilesFunc;          // (desktopId)
    public Func<string, string, Task<bool>>? DeleteLocalFileFunc;         // (desktopId, name)

    // Desktop management (v1.7).
    public Func<Task<(List<(string Id, string Name)> desktops, string currentId)>>? ListDesktopsFunc;
    public Func<string, Task<string?>>? CreateDesktopFunc;                // (name) → new id
    public Func<string, string, Task<bool>>? RenameDesktopFunc;           // (id, name)
    public Func<string, Task<bool>>? DeleteDesktopFunc;                   // (id)
    public Func<string, Task<string?>>? SetCurrentDesktopFunc;            // (id) → new current id

    // Settings persistence (v1.7).
    public Func<Task<(int memKb, string theme, bool crt, string lang)>>? LoadSettingsFunc;
    public Func<int, string, bool, string, Task<bool>>? SaveSettingsFunc;  // (memKb, theme, crt, lang)

    /// <summary>Trigger a browser download of text content.</summary>
    public async Task DownloadFile(string fileName, string content)
    {
        if (DownloadFileFunc is not null)
            await DownloadFileFunc(fileName, content);
    }

    /// <summary>Render paginated lines to a PDF in the browser and trigger a download.</summary>
    public async Task PrintPdfAsync(string fileName, List<List<string>> pages)
    {
        if (PrintPdfFunc is not null)
            await PrintPdfFunc(fileName, pages);
    }

    /// <summary>Load a file from the browser (file picker). Returns JSON text or null.</summary>
    public async Task<string?> LoadFileAsync(string accept) =>
        LoadFileFunc is not null ? await LoadFileFunc(accept) : null;

    // ---- localStorage helpers (scoped to the current Desktop) ----

    /// <summary>Write a file's JSON to the current Desktop. Returns true on success.</summary>
    public async Task<bool> SaveLocalFileAsync(string name, string json) =>
        SaveLocalFileFunc is not null ? await SaveLocalFileFunc(CurrentDesktopId, name, json) : false;

    /// <summary>Read a file's JSON from the current Desktop. Returns null if absent.</summary>
    public async Task<string?> LoadLocalFileAsync(string name) =>
        LoadLocalFileFunc is not null ? await LoadLocalFileFunc(CurrentDesktopId, name) : null;

    /// <summary>List all file names stored in the current Desktop.</summary>
    public async Task<List<string>> ListLocalFilesAsync() =>
        ListLocalFilesFunc is not null ? await ListLocalFilesFunc(CurrentDesktopId) : [];

    /// <summary>Delete a file from the current Desktop. Returns true if it existed.</summary>
    public async Task<bool> DeleteLocalFileAsync(string name) =>
        DeleteLocalFileFunc is not null ? await DeleteLocalFileFunc(CurrentDesktopId, name) : false;

    // ---- Desktop management (v1.7) ----

    /// <summary>Refresh the known Desktop list from localStorage.</summary>
    public async Task RefreshDesktopsAsync()
    {
        if (ListDesktopsFunc is null) return;
        var (desktops, currentId) = await ListDesktopsFunc();
        Desktops = desktops;
        if (!string.IsNullOrEmpty(currentId)) CurrentDesktopId = currentId;
    }

    /// <summary>Create a new named Desktop. Returns its id, or null on failure.</summary>
    public async Task<string?> CreateDesktopAsync(string name) =>
        CreateDesktopFunc is not null ? await CreateDesktopFunc(name) : null;

    /// <summary>Rename a Desktop. Returns true on success.</summary>
    public async Task<bool> RenameDesktopAsync(string id, string name) =>
        RenameDesktopFunc is not null ? await RenameDesktopFunc(id, name) : false;

    /// <summary>Delete a Desktop (and its files). The last one cannot be deleted.</summary>
    public async Task<bool> DeleteDesktopAsync(string id) =>
        DeleteDesktopFunc is not null ? await DeleteDesktopFunc(id) : false;

    /// <summary>Switch to the given Desktop: save current, load the target's files.</summary>
    public async Task SwitchDesktopAsync(string id)
    {
        if (id == CurrentDesktopId) return;

        // Persist any dirty files on the outgoing Desktop first.
        foreach (var d in Desktop.Where(d => d.Dirty))
            await d.DoSaveAsync();

        // Power off the current set and load the target Desktop's files.
        Current = null;
        Desktop.Clear();
        if (SetCurrentDesktopFunc is not null)
        {
            var newId = await SetCurrentDesktopFunc(id);
            if (newId is not null) CurrentDesktopId = newId;
        }
        await RefreshDesktopsAsync();
        await RestoreDesktopFromLocalAsync();
    }

    // ---- Screen buffer operations ----

    public void Clear()
    {
        for (int y = 0; y < MinH; y++)
            for (int x = 0; x < MinW; x++)
            {
                VChars[y, x] = ' ';
                VAttrs[y, x] = A_N;
            }
        CursorAt = null;
    }

    /// <summary>Write text in logical order. Hebrew is arranged right-to-left via bidi.</summary>
    public void Put(int y, int x, string s, int attr = A_N, string? baseDir = null)
    {
        if (baseDir == "R" || Bidi.RtlRe.IsMatch(s))
            s = Bidi.BidiVisual(s, baseDir ?? Bidi.FirstStrong(s) ?? "L");
        PutCells(y, x, s, attr);
    }

    /// <summary>Write characters that are already in visual order.</summary>
    public void PutCells(int y, int x, string s, int attr)
    {
        if (y < 0 || y >= MinH) return;
        for (int k = 0; k < s.Length; k++)
        {
            int xx = x + k;
            if (xx >= 0 && xx < MinW)
            {
                VChars[y, xx] = s[k];
                VAttrs[y, xx] = attr;
            }
        }
    }

    public void SetCursor(int y, int x) => CursorAt = (y, x);

    /// <summary>Draw the standard frame: top line, bottom line, message/footer.</summary>
    public void Frame(string left, string center = "", string right = "Escape: Main Menu",
        string foot = "", string footR = "")
    {
        Clear();
        int W = MinW;
        string r = Localizer.S(right);
        int room = W - ((r.Length > 0 ? r.Length + 3 : 1));
        if (left.Length > room) left = left[..Math.Max(0, room - 1)] + "…";
        Put(0, 0, left);
        string c = Localizer.S(center);
        if (c.Length > 0)
            Put(0, Math.Max(left.Length + 2, (W - c.Length) / 2), c);
        if (r.Length > 0)
            Put(0, W - r.Length - 1, r);
        Put(1, 0, new string('=', W));
        Put(MinH - 2, 0, new string('=', W));
        if (Msg.Length > 0)
            Put(MinH - 1, 0, Localizer.S(Msg), A_B);
        else
        {
            Put(MinH - 1, 0, Localizer.S(foot));
            string fr = Localizer.S(footR);
            if (fr.Length > 0) Put(MinH - 1, W - fr.Length - 1, fr);
        }
    }

    // ---- Keyboard input ----

    /// <summary>
    /// Await the next key. Renders the current buffer first, then blocks until JS delivers a key.
    /// A small queue handles fast typing (keys pressed while we're processing).
    /// </summary>
    public async Task<string> KeyAsync()
    {
        // Drain any queued keys first (fast typing)
        if (_keyQueue.TryDequeue(out string? k)) return k;

        RenderTick++;
        RenderRequested?.Invoke();

        _keyTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        string key = await _keyTcs.Task;
        Msg = "";
        return key;
    }

    /// <summary>Called by the Blazor component when JS delivers a key token.</summary>
    public void EnqueueKey(string token)
    {
        if (_keyTcs is not null)
        {
            var tcs = _keyTcs;
            _keyTcs = null;
            tcs.TrySetResult(token);
        }
        else
        {
            _keyQueue.Enqueue(token);
        }
    }

    // ---- Menu system: stacked "file folder" layers ----

    private (int left, int titleRow, int bottom, int right) LayerGeom(int d)
    {
        int H = MinH, W = MinW;
        int left = 2 + 3 * d;
        int titleRow = 3 + 2 * d;
        int bottom = Math.Min(H - 6 + 2 * d, H - 3);
        int right = Math.Min(W - 12 + 3 * d, W - 2);
        return (left, titleRow, bottom, right);
    }

    public int Avail() => Math.Max(0, MemKb - ProgramK - Desktop.Sum(d => d.Size()) / 1024);

    /// <summary>Draw the header, every menu layer in path (outermost first) and the footer.</summary>
    private (int left, int titleRow, int bottom, int right) DrawStack(
        List<string> path, string esc = "", string foot = "", string footR = "")
    {
        int H = MinH, W = MinW;
        Clear();

        // Build a grid of chars for the box drawing
        var grid = new char[H, W];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++) grid[y, x] = ' ';

        void HLine(int y, int x1, int x2, bool keep)
        {
            for (int x = Math.Max(0, x1); x < Math.Min(W, x2 + 1); x++)
                if (!(keep && grid[y, x] == '|')) grid[y, x] = '_';
        }

        void ClearRow(int y, int x1, int x2)
        {
            for (int x = Math.Max(0, x1); x < Math.Min(W, x2 + 1); x++) grid[y, x] = ' ';
        }

        HLine(1, 0, W - 1, false);
        HLine(H - 2, 0, W - 1, false);

        (int left, int titleRow, int bottom, int right) geom = (0, 0, 0, 0);
        for (int d = 0; d < path.Count; d++)
        {
            var (L, Tr, B, R) = LayerGeom(d);
            string title = Localizer.S(path[d]);
            int tabr = Math.Min(Math.Max(L + 27, L + 6 + title.Length + 3), R - 2);

            ClearRow(Tr - 1, L, tabr);
            ClearRow(Tr, L, tabr);
            for (int y = Tr + 1; y <= B; y++) ClearRow(y, L, R);

            HLine(Tr - 1, L + 1, tabr - 1, false);
            grid[Tr, L] = '|';
            string visTitle = Bidi.BidiVisual(title[..Math.Min(title.Length, tabr - L - 7)],
                Bidi.FirstStrong(title) ?? "L");
            for (int j = 0; j < visTitle.Length && L + 6 + j < tabr; j++)
                grid[Tr, L + 6 + j] = visTitle[j];
            grid[Tr, tabr] = '|';
            HLine(Tr, tabr + 1, R - 1, true);

            for (int y = Tr + 1; y <= B; y++)
            {
                grid[y, L] = '|';
                grid[y, R] = '|';
            }
            HLine(B, L + 1, R - 1, false);
            geom = (L, Tr, B, R);
        }

        // Copy grid to VChars
        for (int y = 1; y < H - 1; y++)
            for (int x = 0; x < W; x++)
            {
                VChars[y, x] = grid[y, x];
                VAttrs[y, x] = A_N;
            }

        // Header: disk/desktop indicator. With a single desktop, just "Disk:"
        // (original AppleWorks feel). With multiple, "Desktop: <name>" signals
        // the multi-desktop feature is active.
        string disk = Desktops.Count > 1 ? Localizer.T("DesktopLabel", CurrentDesktopName) : Localizer.S("DiskOnly");
        string center = path.Count > 0 ? Localizer.S(path[^1]).ToUpperInvariant() : "";
        int cx = (W - center.Length) / 2;
        int roomD = center.Length > 0 ? cx - 2 : W - (esc.Length + 3);
        if (disk.Length > roomD) disk = disk[..Math.Max(0, roomD - 1)] + "…";
        Put(0, 0, disk);
        if (center.Length > 0) Put(0, cx, center);
        string e = Localizer.S(esc);
        if (e.Length > 0) Put(0, W - e.Length - 1, e);

        // Footer
        if (Msg.Length > 0)
            Put(H - 1, 0, Localizer.S(Msg), A_B);
        else
        {
            Put(H - 1, 0, Localizer.S(foot));
            string fr = Localizer.S(footR);
            if (fr.Length > 0) Put(H - 1, W - fr.Length - 1, fr);
        }

        return geom;
    }

    private string DefaultEsc(List<string> path) =>
        path.Count > 1 ? Localizer.T("EscapeName", Localizer.S(path[^2])) : "";

    /// <summary>
    /// Show a menu in a stacked layer. Items are strings (numbered choices),
   /// ("h"/"H", text) for headings, or null for blank lines.
    /// Returns the index of the chosen string item, or null on Escape.
    /// </summary>
    public async Task<int?> SelectAsync(List<string> path, List<object?> items, int start = 0,
        string? esc = null, string? footR = null)
    {
        var choiceLines = new List<int>();
        for (int i = 0; i < items.Count; i++)
            if (items[i] is string) choiceLines.Add(i);

        int n = choiceLines.Count;
        int idx = Math.Min(Math.Max(start, 0), Math.Max(n - 1, 0));
        int top = 0;
        string digits = "";

        if (esc is null) esc = DefaultEsc(path);
        if (footR is null) footR = path.Count == 1 ? "AltKForHelp" : null;

        while (true)
        {
            string fr = footR ?? Localizer.T("KAavail", Avail());
            var (L, Tr, B, R) = DrawStack(path, esc!,
                "TypeNumberOrArrows", fr);

            int ctop = Tr + 3;
            int cbot = Math.Max(Tr + 3, B - 1);
            int vis = cbot - ctop + 1;
            int curLine = n > 0 ? choiceLines[idx] : 0;

            if (idx == 0) top = 0;
            if (curLine < top) top = curLine;
            if (curLine >= top + vis) top = curLine - vis + 1;

            // Build number map: raw list index -> its 1-based position among selectable items.
            var numMap = new Dictionary<int, int>();
            for (int ki = 0; ki < choiceLines.Count; ki++)
                numMap[choiceLines[ki]] = ki + 1;

            int maxW = R - (L + 10) - 1;

            for (int j = 0; j < vis; j++)
            {
                int li = top + j;
                if (li >= items.Count) break;
                var it = items[li];
                int y = ctop + j;

                if (it is null) continue;

                // Check for header tuple: ("h", "text") or ("H", "text")
                if (it is ValueTuple<string, string> vt)
                {
                    string tag = vt.Item1;
                    string hText = vt.Item2;
                    if (tag == "h" || tag == "H")
                    {
                        int x = tag == "h" ? L + 10 : L + 6;
                        Put(y, x, Localizer.S(hText)[..Math.Min(Localizer.S(hText).Length, R - x - 1)]);
                        continue;
                    }
                }

                string s = it as string ?? "";
                int num = numMap[li];
                // NOTE: C# interpolation has no alignment specifiers — {num:>2} would
                // render the literal text ">2". Use PadLeft for right-alignment.
                Put(y, L + 5, num.ToString().PadLeft(2) + ".");
                string lineText = Localizer.S(s)[..Math.Min(Localizer.S(s).Length, maxW)];
                if (li == curLine)
                    Put(y, L + 9, " " + lineText + " ", A_R);
                else
                    Put(y, L + 10, lineText);
            }

            if (n == 0) Put(ctop, L + 10, Localizer.S("None"));
            if (top > 0) Put(ctop - 1, R - 4, "\u25B2");
            if (top + vis < items.Count) Put(B, R - 4, "\u25BC");

            string keyTok = await KeyAsync();
            if (keyTok == Key.Escape) return null;
            if (keyTok == Key.Alt("K") || keyTok == Key.F1 || keyTok == "?")
            {
                await ShowTextAsync(Localizer.S("AboutRetroWorks"), Localizer.T("HelpAbout", Version).Split('\n'));
                continue;
            }
            if (n == 0) continue;

            if (keyTok.Length == 1 && char.IsDigit(keyTok[0]))
            {
                digits += keyTok;
                if (digits.Length > 2) digits = digits[^2..];
                int v = int.Parse(digits);
                if (v < 1 || v > n) { digits = keyTok; v = int.Parse(keyTok); }
                if (v >= 1 && v <= n) idx = v - 1;
                continue;
            }
            digits = "";

            switch (keyTok)
            {
                case Key.Up: idx = (idx - 1 + n) % n; break;
                case Key.Down: idx = (idx + 1) % n; break;
                case Key.PgUp: idx = Math.Max(0, idx - vis); break;
                case Key.PgDn: idx = Math.Min(n - 1, idx + vis); break;
                case Key.Home:
                case "A-T": idx = 0; break;
                case Key.End:
                case "A-B": idx = n - 1; break;
                case Key.Enter: return idx;
            }
        }
    }

    /// <summary>Single-line text input in a menu layer. Returns text or null on Escape.</summary>
    public async Task<string?> BoxPromptAsync(List<string> path, string labelKey,
        string initial = "", int width = 30, string? esc = null, int maxLen = 0)
    {
        if (esc is null) esc = DefaultEsc(path) ?? "EscapeCancel";
        var (L, Tr, B, R) = DrawStack(path, esc!, "TypeEntryThenReturn", Localizer.T("KAavail", Avail()));
        Put(Tr + 3, L + 6, Localizer.S(labelKey)[..Math.Min(Localizer.S(labelKey).Length, R - L - 8)]);
        int w = Math.Max(8, Math.Min(width, R - L - 9));
        return await LineEditAsync(Tr + 5, L + 6, w, initial, maxLen: maxLen);
    }

    /// <summary>One-line editor. Returns text or null on Escape.</summary>
    private async Task<string?> LineEditAsync(int y, int x, int width, string initial = "",
        string label = "", int maxLen = 0)
    {
        if (label.Length > 0)
        {
            Put(y, x, label, A_B);
            x += label.Length;
        }
        width = Math.Max(4, width);

        var buf = new List<char>(initial);
        int off = 0;
        int pos = buf.Count;

        while (true)
        {
            if (pos < off) off = pos;
            if (pos - off >= width) off = pos - width + 1;

            string baseDir = Bidi.FirstStrong(new string(buf.ToArray())) ?? "L";
            string visible = new string(buf[off..Math.Min(off + width, buf.Count)].ToArray()).PadRight(width);
            string vis = Bidi.BidiVisual(visible, baseDir);
            PutCells(y, x, vis, A_R);

            int li = pos - off;
            // Find visual position of the cursor
            int cx = 0;
            for (int i = 0; i < vis.Length; i++)
                if (i == li) { cx = i; break; }
            SetCursor(y, x + cx);

            string k = await KeyAsync();
            if (baseDir == "R" && k is Key.Left or Key.Right)
                k = k == Key.Left ? Key.Right : Key.Left;

            // Editing is over — hide the blinking cursor.
            CursorAt = null;
            if (k == Key.Escape) return null;
            if (k == Key.Enter) return new string(buf.ToArray());

            switch (k)
            {
                case Key.Left: pos = Math.Max(0, pos - 1); break;
                case Key.Right: pos = Math.Min(buf.Count, pos + 1); break;
                case Key.Home:
                case "A-A":
                case "A-T": pos = 0; break;
                case Key.End:
                case "A-E":
                case "A-B": pos = buf.Count; break;
                case Key.Backspace:
                    if (pos > 0) { buf.RemoveAt(pos - 1); pos--; }
                    break;
                case Key.Delete:
                case "A-D":
                    if (pos < buf.Count) buf.RemoveAt(pos);
                    break;
                case "A-Y":
                    buf.RemoveRange(pos, buf.Count - pos);
                    break;
                default:
                    // Accept any printable single character (letters, digits,
                    // punctuation like = ( ) - * / . , " etc.) — matches the
                    // Python original which inserts any len(k)==1 key.
                    if (k.Length == 1 && !char.IsControl(k[0])
                        && (maxLen <= 0 || buf.Count < maxLen))
                    {
                        buf.Insert(pos, k[0]);
                        pos++;
                    }
                    break;
            }
        }
    }

    /// <summary>Prompt for text on the bottom line. Returns text or null on Escape.</summary>
    public async Task<string?> AskAsync(string labelKey, string initial = "")
    {
        int H = MinH, W = MinW;
        Put(H - 1, 0, new string(' ', W - 1));
        return await LineEditAsync(H - 1, 0, W - Localizer.S(labelKey).Length - 2, initial,
            label: Localizer.S(labelKey));
    }

    /// <summary>Yes/No prompt. Returns true/false, or null on Escape.</summary>
    public async Task<bool?> YesNoAsync(string questionKey, bool defaultVal = false)
    {
        int H = MinH, W = MinW;
        Put(H - 1, 0, new string(' ', W - 1));
        string yes = Localizer.S("Yes");
        string no = Localizer.S("No");
        string def = defaultVal ? yes : no;
        Put(H - 1, 0, $"{Localizer.S(questionKey)}  ({yes}/{no}) [{def}]", A_B);

        while (true)
        {
            string k = await KeyAsync();
            if (k is "y" or "Y" or "\u05D8") return true;
            if (k is "n" or "N" or "\u05DE") return false;
            if (k == Key.Enter) return defaultVal;
            if (k == Key.Escape) return null;
        }
    }

    /// <summary>Show a scrollable text page. Returns when Escape/Enter is pressed.</summary>
    public async Task ShowTextAsync(string title, string[] lines)
    {
        int top = 0;
        while (true)
        {
            int H = MinH;
            int vh = H - 4;
            top = Math.Max(0, Math.Min(top, Math.Max(0, lines.Length - vh)));

            Frame(title, "", "EscapeGoBack",
                Localizer.S("PressEscapeOrReturn") +
                (lines.Length > vh ? Localizer.S("ArrowsScroll") : ""));

            for (int i = 0; i < vh && top + i < lines.Length; i++)
                Put(2 + i, 2, lines[top + i]);

            string k = await KeyAsync();
            if (k is Key.Escape or Key.Enter or " " or "q") return;
            if (k == Key.Up) top--;
            else if (k == Key.Down) top++;
            else if (k == Key.PgUp) top -= vh;
            else if (k == Key.PgDn) top += vh;
        }
    }

    // ---- Desktop management ----

    private string DocLabel(Doc d) =>
        $"{d.Name[..Math.Min(24, d.Name.Length)],-24}  {Localizer.S(Doc.KindName[d.Kind]),-14}" +
        (d.Dirty ? "  *" : "");

    public async Task<Doc?> PickDesktopAsync(List<string> path, string? esc = null)
    {
        if (Desktop.Count == 0)
        {
            Msg = Localizer.S("NoFilesOnDesktop");
            return null;
        }
        int start = Current is not null ? Desktop.IndexOf(Current) : 0;
        var items = new List<object?>
        {
            ("h", Localizer.S("Name").PadRight(26) + Localizer.S("Type")),
            null,
        };
        items.AddRange(Desktop.Select(DocLabel));

        int? i = await SelectAsync(path, items, start, esc);
        return i is null ? null : Desktop[i.Value];
    }

    public async Task<Doc?> QuickChangeAsync() =>
        await PickDesktopAsync(["DesktopIndex"],
            Current is not null ? Localizer.T("EscapeName", Current.Name[..Math.Min(20, Current.Name.Length)]) : "");

    // ---- Main menu activities ----

    private async Task<Doc?> NewFileMenuAsync(List<string> path, string kind)
    {
        var p = new List<string>(path) { Doc.KindName[kind] };
        string srcKey = kind switch
        {
            "wp" => "FromTextFile",
            "db" => "FromCsvFile",
            _ => "FromCsvFile"
        };
        int sel = 0;
        while (true)
        {
            int? i = await SelectAsync(p,
                [("H", Localizer.S("MakeNewFile")), null, Localizer.S("FromScratch"), null, srcKey], sel);
            if (i is null) return null;
            sel = i.Value;

            Doc? d = null;
            if (i == 0) d = await NewFileAsync(p, kind);
            else d = await ImportFileAsync(p, kind);
            if (d is not null) return d;
        }
    }

    public async Task<Doc?> AddFilesAsync()
    {
        var P = new List<string> { "Main Menu", "Add Files" };
        int sel = 0;
        while (true)
        {
            if (Desktop.Count >= MaxDesktop)
            {
                Msg = Localizer.T("DesktopFull", MaxDesktop);
                return null;
            }
            if (Avail() <= 0)
            {
                Msg = Localizer.S("DesktopMemoryFull");
                return null;
            }

            var items = new List<object?>
            {
                ("h", Localizer.S("GetFilesFrom")),
                null,
                "LoadFromFile",
                null,
                ("h", Localizer.S("MakeNewFileFor")),
                null,
                "WordProcessor", "DataBase", "Spreadsheet"
            };

            int? i = await SelectAsync(P, items, sel);
            if (i is null) return null;
            sel = i.Value;

            Doc? d = null;
            if (i == 0)
                d = await LoadFromFileAsync();
            else
            {
                // i is the 1-based position among selectable items:
                // WordProcessor=1, DataBase=2, Spreadsheet=3.
                string[] kinds = { "wp", "db", "ss" };
                d = await NewFileMenuAsync(P, kinds[i.Value - 1]);
            }

            if (d is not null) return d;
        }
    }

    /// <summary>Load a file from the browser file picker.</summary>
    private async Task<Doc?> LoadFromFileAsync()
    {
        string? json = await LoadFileAsync(".rwp,.rdb,.rss");
        if (json is null) return null;
        try
        {
            var d = Doc.LoadFromJson(this, json);
            if (d is null)
            {
                Msg = Localizer.T("CouldNotReadFile", "unknown type");
                return null;
            }
            Desktop.Add(d);
            return d;
        }
        catch (Exception e)
        {
            Msg = Localizer.T("CouldNotReadFile", e.Message);
            return null;
        }
    }

    private async Task<Doc?> NewFileAsync(List<string> path, string kind)
    {
        while (true)
        {
            string? name = await BoxPromptAsync(path, "TypeNameForNewFile", maxLen: 30);
            if (name is null) return null;
            name = Text.Clean(name).Trim();
            if (name.Length == 0) return null;

            if (Desktop.Any(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                Msg = Localizer.S("NameAlreadyOnDesktop");
                continue;
            }

            Doc d = kind switch
            {
                "wp" => new WPDoc(this, name),
                "db" => new DBDoc(this, name),
                _ => new SSDoc(this, name)
            };

            if (kind == "db" && d is DBDoc dbd)
            {
                var fields = await AskCategoriesAsync(d.Name);
                if (fields is null || fields.Count == 0) return null;
                dbd.Fields = fields;
                dbd.Widths = new int[fields.Count];
            }

            d.Dirty = true;
            Desktop.Add(d);
            return d;
        }
    }

    private async Task<Doc?> ImportFileAsync(List<string> path, string kind)
    {
        // In the web app, "import" means loading a text/CSV file from the browser
        string? content = await LoadFileAsync(".txt,.csv");
        if (content is null || content.Length == 0) return null;

        string baseName = "Imported";
        Doc d = kind switch
        {
            "wp" => new WPDoc(this, baseName),
            "db" => new DBDoc(this, baseName),
            _ => new SSDoc(this, baseName)
        };

        var lines = content.Split('\n');
        if (kind == "wp" && d is WPDoc wpd)
        {
            var paras = lines.Select(l => Text.Clean(l.TrimEnd('\r'))).ToList();
            if (paras.Count == 0) paras.Add("");
            wpd.Paras = paras;
            wpd.DetectDirs();
            wpd.Sty = Enumerable.Repeat("", wpd.Paras.Count).ToList();
        }
        else if (kind == "db" && d is DBDoc dbd)
        {
            var rows = ParseCsv(lines);
            if (rows.Count > 0)
            {
                dbd.Fields = rows[0].Take(30)
                    .Select(x =>
                    {
                        string clean = Text.Clean(x).Trim();
                        return clean.Length > 0 ? clean[..Math.Min(30, clean.Length)] : Localizer.T("CategoryN", 1);
                    })
                    .ToList();
                int nf = dbd.Fields.Count;
                dbd.Widths = new int[nf];
                dbd.Records = rows.Skip(1)
                    .Where(r => r.Any(v => v.Trim().Length > 0))
                    .Select(r => r.Select(v => Text.Clean(v)).Take(nf).ToList())
                    .ToList();
            }
        }
        else if (d is SSDoc ssd)
        {
            var rows = ParseCsv(lines);
            for (int r = 0; r < Math.Min(rows.Count, FormulaEngine.MaxRow); r++)
                for (int c = 0; c < Math.Min(rows[r].Count, 26); c++)
                {
                    string v = Text.Clean(rows[r][c]).Trim();
                    if (v.Length == 0) continue;
                    if (!double.TryParse(v, out _))
                        if (FormulaEngine.IsValue(v)) v = "\"" + v;
                    ssd.Cells[FormulaEngine.RefName(c, r + 1)] = v;
                }
        }

        d.Dirty = true;
        Desktop.Add(d);
        return d;
    }

    private static List<List<string>> ParseCsv(string[] lines)
    {
        var rows = new List<List<string>>();
        foreach (string line in lines)
        {
            if (line.Trim().Length == 0) continue;
            // Simple CSV parse (handles quoted fields)
            var row = new List<string>();
            var cur = new System.Text.StringBuilder();
            bool inQuotes = false;
            foreach (char ch in line.TrimEnd('\r'))
            {
                if (ch == '"') inQuotes = !inQuotes;
                else if (ch == ',' && !inQuotes) { row.Add(cur.ToString()); cur.Clear(); }
                else cur.Append(ch);
            }
            row.Add(cur.ToString());
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>
    /// Safety net for UNEXPECTED exceptions (bugs) — as opposed to normal SSError formula errors.
    /// Creates a Word Processor "Error Log" document on the Desktop with full details so the user
    /// can view and share it, shows a message, and keeps the app running. The caller should return 0.
    /// </summary>
    private bool _loggingError;

    public void LogUnexpectedError(Exception ex, string? context = null)
    {
        if (_loggingError) return;   // never log while logging (recursion guard)
        _loggingError = true;
        try
        {
            Msg = Localizer.S("InternalError");
            var name = "Error Log " + DateTime.Now.ToString("yyyy-MM-dd HHmmss");
            if (Desktop.Any(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                name += "-2";

            var paras = new List<string>
            {
                Localizer.S("ErrorLogTitle"),
                "",
                Localizer.T("ErrorLogWhen", DateTime.Now.ToString(Localizer.Lang == "en" ? "dddd, dd MMMM yyyy HH:mm:ss" : "dd/MM/yyyy HH:mm:ss")),
                Localizer.T("ErrorLogType", ex.GetType().FullName ?? ex.GetType().Name),
            };
            if (context is not null && context.Length > 0)
                paras.Add(Localizer.T("ErrorLogContext", context));
            paras.AddRange(new[] { "", "Message:", ex.Message, "", "Stack trace:" });
            foreach (var line in (ex.StackTrace ?? "").Split('\n'))
                if (line.Trim().Length > 0) paras.Add(line.TrimEnd());

            var d = new WPDoc(this, name);
            d.Paras = paras;
            // NOTE: assigning Paras directly leaves IndL/IndR/Hang at their
            // 1-element defaults — the doc self-heals on first Draw via
            // FixDirs(), but keep Sty in sync here too so nothing indexes a
            // stale list between creation and the first frame.
            d.DetectDirs();
            d.Sty = Enumerable.Repeat("", d.Paras.Count).ToList();
            d.Dirty = true;
            Desktop.Add(d);
        }
        catch { /* the log itself failed — the Msg above is all we can do */ }
        finally { _loggingError = false; }
    }

    private async Task<List<string>?> AskCategoriesAsync(string name)
    {
        var fields = new List<string>();
        while (true)
        {
            int H = MinH;
            Frame(Localizer.T("File", name), "ChangeNameCategory", "EscapeCancel");
            Put(3, 4, Localizer.S("TypeCategoryNames"), A_B);
            Put(4, 4, Localizer.S("PressReturnWhenDone"));
            for (int j = 0; j < fields.Count && 6 + j < H - 4; j++)
                Put(6 + j, 6, fields[j]);

            string? t = await AskAsync(Localizer.T("CategoryNName", fields.Count + 1));
            if (t is null) return null;
            t = Text.Clean(t).Trim()[..Math.Min(30, Text.Clean(t).Trim().Length)];
            if (t.Length == 0) return fields.Count > 0 ? fields : [Localizer.T("CategoryN", 1)];
            if (fields.Contains(t))
            {
                Msg = Localizer.S("AlreadyHaveCategory");
                continue;
            }
            fields.Add(t);
            if (fields.Count >= 30) return fields;
        }
    }

    public async Task SaveMenuAsync()
    {
        if (Desktop.Count == 0)
        {
            Msg = Localizer.S("NoFilesOnDesktopShort");
            return;
        }
        var items = new List<object?>
        {
            ("H", Localizer.S("SaveWhichFile")), null, "AllChangedFiles"
        };
        items.AddRange(Desktop.Select(DocLabel));

        int? i = await SelectAsync(["Main Menu", "Save Files"], items);
        if (i is null) return;

        List<Doc> targets = i == 0
            ? Desktop.Where(d => d.Dirty).ToList()
            : new List<Doc> { Desktop[i.Value - 1] };
        if (targets.Count == 0)
        {
            Msg = Localizer.S("NothingToSave");
            return;
        }

        bool ok = true;
        foreach (var d in targets)
        {
            // Persist to localStorage AND trigger a browser download so the
            // on-disk copy stays in sync with the persisted one.
            string json = d.SaveJson();
            string key = d.Name + d.Ext;
            bool saved = await SaveLocalFileAsync(key, json);
            if (saved)
            {
                // Clean up a stale entry left by an earlier name (rename).
                if (d.SavedKey is not null && d.SavedKey != key)
                    await DeleteLocalFileAsync(d.SavedKey);
                d.SavedKey = key;

                await DownloadFile(key, json);
                d.Dirty = false;   // only clear on success — a failed write must stay dirty
            }
            ok &= saved;
        }

        if (!ok) return;
        Msg = Localizer.T("SavedNFiles", targets.Count);
    }

    public async Task RemoveMenuAsync()
    {
        var d = await PickDesktopAsync(["Main Menu", "Remove Files"]);
        if (d is null) return;
        if (d.Dirty)
        {
            bool? ans = await YesNoAsync(Localizer.T("HasChangesSaveBeforeRemoving", d.Name), true);
            if (ans is null) return;
            if (ans == true)
            {
                // Download a copy before removing (the file will be deleted from
                // localStorage below, so a plain DoSaveAsync would be pointless).
                await d.DoDownloadAsync();
            }
        }
        Desktop.Remove(d);
        if (ReferenceEquals(Current, d)) Current = null;

        // Also remove from localStorage so it doesn't reappear on next startup.
        await DeleteLocalFileAsync(d.Name + d.Ext);

        Msg = Localizer.T("RemovedFromDesktop", d.Name);
    }

    public async Task OtherActivitiesAsync()
    {
        var P = new List<string> { "Main Menu", "Other Activities" };
        int sel = 0;
        while (true)
        {
            var items = Spaced(["Settings", "ManageDesktops", "AboutRetroWorks"]);
            int? i = await SelectAsync(P, items, sel);
            if (i is null) return;
            sel = i.Value;

            switch (i)
            {
                case 0: await SettingsAsync(); break;
                case 1: await ManageDesktopsAsync(); break;
                case 2: await ShowTextAsync(Localizer.S("AboutRetroWorks"), Localizer.T("HelpAbout", Version).Split('\n')); break;
            }
        }
    }

    /// <summary>
    /// Consolidated settings dialog (v1.7). Shows memory, theme, CRT, and language
    /// on one screen; arrow keys navigate, Left/Right cycle values, Enter confirms.
    /// </summary>
    private async Task SettingsAsync()
    {
        var P = new List<string> { "Main Menu", "Other Activities", "Settings" };
        int sel = 0;
        int memIdx = Array.FindIndex(MemorySizes, m => m.kb == MemKb);
        if (memIdx < 0) memIdx = 2;   // default to 1 MB

        while (true)
        {
            // Build the settings rows: label + current value.
            string memVal = MemorySizes[memIdx].name;
            string themeVal = Theme == "green" ? Localizer.S("GreenPhosphor") : Localizer.S("AmberPhosphor");
            string crtVal = CrtEmulation ? Localizer.S("On") : Localizer.S("Off");
            var langCodes = Localizer.Languages;
            int langIdx = Array.FindIndex(langCodes, c => c.code == Localizer.Lang);
            if (langIdx < 0) langIdx = 0;
            string langVal = langCodes[langIdx].name;

            // Draw the settings screen.
            var (L, Tr, B, R) = DrawStack(P, "EscapeCancel", "ArrowsCycleEnterSave");
            int rowY = Tr + 3;
            string[] labels = { Localizer.S("MemorySize"), Localizer.S("Theme"), Localizer.S("CrtEmulation"), Localizer.S("ChooseLanguage") };
            string[] values = { memVal, themeVal, crtVal, langVal };

            for (int i = 0; i < 4; i++)
            {
                int y = rowY + i * 2;
                if (y > B - 1) break;
                string label = labels[i];
                string val = values[i];
                if (i == sel)
                    Put(y, L + 5, " " + label.PadRight(20) + val + " ", A_R);
                else
                    Put(y, L + 6, label.PadRight(20) + val);
            }

            string k = await KeyAsync();
            if (k == Key.Escape) return;

            if (k is Key.Up or Key.Down)
            {
                sel = (sel + (k == Key.Up ? 3 : 1)) % 4;
                continue;
            }

            // Left/Right cycles the value of the selected setting.
            int dir = k == Key.Left ? -1 : 1;
            if (k is Key.Right or Key.Left)
            {
                switch (sel)
                {
                    case 0: memIdx = (memIdx + dir + MemorySizes.Length) % MemorySizes.Length; MemKb = MemorySizes[memIdx].kb; break;
                    case 1: Theme = Theme == "green" ? "amber" : "green"; break;
                    case 2: CrtEmulation = !CrtEmulation; break;
                    case 3: langIdx = (langIdx + dir + langCodes.Length) % langCodes.Length; Localizer.SetLanguage(langCodes[langIdx].code); break;
                }
                continue;
            }

            // Enter saves and closes.
            if (k == Key.Enter)
            {
                await SaveSettingsAsync();
                Msg = Localizer.S("SettingsSaved");
                return;
            }
        }
    }

    /// <summary>
    /// Quit = power cycle. Saves any dirty files to disk, then returns "RESTART"
    /// so the app goes back through the splash screen and re-restores the
    /// desktop from localStorage — like switching a real machine off and on.
    /// Returns null if the user cancels.
    /// </summary>
    private async Task<string?> QuitAsync()
    {
        bool hadChanges = false;
        foreach (var d in Desktop.ToList())
        {
            if (!d.Dirty) continue;
            hadChanges = true;
            bool? ans = await YesNoAsync(Localizer.T("SaveChangesBeforeQuitting", d.Name), true);
            if (ans is null) return null;
            if (ans == true && !await d.DoSaveAsync()) return null;
        }
        if (hadChanges) return "RESTART";
        return await YesNoAsync("ReallyQuit", false) == true ? "RESTART" : null;
    }

    // ---- Main loop ----

    public async Task WorkAsync(Doc doc)
    {
        Doc? current = doc;
        while (current is not null)
        {
            Current = current;
            object? res;
            try
            {
                res = await current.RunAsync();
            }
            catch (Exception ex)
            {
                // Safety net: an unexpected bug inside a document must not kill the app.
                // Log it as an "Error Log" file on the Desktop and return to the menu.
                LogUnexpectedError(ex, context: Localizer.T("ErrorLogInDoc", current.Name));
                res = null;
            }
            current = res as Doc;
        }
    }

    public async Task SplashAsync()
    {
        while (true)
        {
            int H = MinH, W = MinW;
            Clear();
            int bw = 56;
            int x = (W - bw) / 2;
            // Box starts at row 3 so the credit lines below fit within 24 rows.
            int y = Math.Max(1, (H - 16) / 2 - 1);

            Put(y, x, "╔" + new string('═', bw - 2) + "╗", A_B);
            for (int j = 1; j < 6; j++)
                Put(y + j, x, "║" + new string(' ', bw - 2) + "║", A_B);
            Put(y + 6, x, "╚" + new string('═', bw - 2) + "╝", A_B);

            string t1 = "R E T R O W O R K S   ] [";
            string t2 = Localizer.S("WPDBSS");
            Put(y + 2, x + (bw - t1.Length) / 2, t1, A_B);
            Put(y + 4, x + (bw - t2.Length) / 2, t2);

            string date = DateTime.Today.ToString(Localizer.Lang == "en" ? "dddd, dd MMMM yyyy" : "dd/MM/yyyy");
            var lines = new[]
            {
                Localizer.T("Version", Version),
                Localizer.S("OriginalTribute"),
                Localizer.S("NotAffiliated"),
                "",
                Localizer.T("TodaysDate", date),
                "",
                Localizer.S("AltTakesPlaceOfApple"),
                "",
                Localizer.S("CreditBlazor"),
                Localizer.S("CreditPython"),
                "",
                Localizer.S("StartRetroWorks"),
                Localizer.S("WhatsNew"),
            };
            for (int j = 0; j < lines.Length; j++)
                Put(y + 7 + j, (W - lines[j].Length) / 2, lines[j],
                    j >= lines.Length - 2 ? A_B : A_N);

            string k = await KeyAsync();
            if (k == "1" || k == "") return;
            if (k == "2")
            {
                // Show What's New screen
                string whatsnew = @"Version 1.7 - Current Release

- Multiple Desktops: keep separate file sets for different
  projects (Other Activities > Manage Desktops). Switch,
  create, rename, or delete named Desktops; each is saved
  to disk.
- Cross-module clipboard: copy in one module and paste in
  another (WP text -> DB records / SS cells, and back).
- Settings dialog (Other Activities > Settings): memory,
  theme, CRT emulation, and language on one screen. All
  settings persist across sessions.

Version 1.6 - Previous Release

Word Processor:
- Tab stops (8 columns) and per-paragraph indents
  Alt-I left, Alt-O right, Alt-H hanging indent
- Header & footer with page numbers (Alt-Shift-H / F),
  shown on screen as Pg N and in the printed file
- PDF print (Alt-Shift-P): prints to a real PDF using the
  ImageWriter NLQ retro font for an authentic Apple II look

Data Base:
- Lookup lists per field (Alt-L): editing a field offers
  the allowed values as a numbered menu
- Highlight rules (Alt-R): matching records show in reverse
  video (contains / equals / starts with / is empty)
- Reports (Alt-G): aligned table report (pick columns) or
  mailing-label report, saved to .txt

Version 1.5 - Earlier Release

- Expanded spell check dictionary (5,000+ words)
- Added spreadsheet functions: @Left, @Right, @Mid, @Len,
  @Upper, @Lower, @Join, @Find, @Text, @Val, @Today, @Now,
  @Year, @Month, @Day, @Vlookup
- Column insert/delete in spreadsheets
- Splash screen now shows What's New option

Version 1.3 - First C# Port
- Initial C# port from Python reference implementation
- Word processor with spell check and thesaurus
- Database with sort, find, and report features
- Spreadsheet with formula engine";
                string[] whatsnewLines = whatsnew.Split('\n');
                await ShowTextAsync(Localizer.S("WhatsNewTitle"), whatsnewLines);
            }
        }
    }

    /// <summary>Load persisted settings (memory, theme, CRT, language) from localStorage.</summary>
    private async Task LoadSettingsAsync()
    {
        if (LoadSettingsFunc is null) return;
        try
        {
            var (memKb, theme, crt, lang) = await LoadSettingsFunc();
            MemKb = memKb;
            Theme = theme;
            CrtEmulation = crt;
            Localizer.SetLanguage(lang);
        }
        catch { /* defaults are fine */ }
    }

    /// <summary>Persist current settings to localStorage.</summary>
    private async Task SaveSettingsAsync()
    {
        if (SaveSettingsFunc is not null)
            await SaveSettingsFunc(MemKb, Theme, CrtEmulation, Localizer.Lang);
    }

    /// <summary>Entry point: splash, restore saved files, then main menu.</summary>
    public async Task RunAsync()
    {
        await LoadSettingsAsync();
        await SplashAsync();
        await RestoreDesktopFromLocalAsync();
        await MainMenuAsync();
    }

    /// <summary>
    /// The power-cycle loop. Each iteration is one "machine on" session: the main
    /// menu (which auto-opens the first desktop file). Quitting saves dirty files,
    /// powers off (clears the Desktop), and loops back through splash + restore —
    /// so a restarted session behaves exactly like a cold start.
    /// </summary>
    public async Task MainMenuAsync()
    {
        while (true)
        {
            await MainMenuBodyAsync();

            // Quit was confirmed: power off and cycle back through the splash screen.
            Current = null;
            Desktop.Clear();
            await SplashAsync();
            await RestoreDesktopFromLocalAsync();
        }
    }

    /// <summary>One session at the main menu. Returns when the user quits.</summary>
    private async Task MainMenuBodyAsync()
    {
        var items = Spaced(["AddFilesToDesktop", "WorkWithFileOnDesktop", "SaveDesktopFiles",
            "RemoveFilesFromDesktop", "OtherActivities", "Quit"]);
        int sel = 0;

        if (Desktop.Count > 0)
            await WorkAsync(Desktop[0]);

        while (true)
        {
            string esc = Current is not null && Desktop.Contains(Current)
                ? Localizer.T("EscapeName", Current.Name[..Math.Min(20, Current.Name.Length)]) : "";

            int? i = await SelectAsync(["Main Menu"], items, sel, esc);
            if (i is null)
            {
                if (Current is not null && Desktop.Contains(Current))
                    await WorkAsync(Current);
                continue;
            }
            sel = i.Value;

            switch (i)
            {
                case 0:
                    var d0 = await AddFilesAsync();
                    if (d0 is not null) await WorkAsync(d0);
                    break;
                case 1:
                    var d1 = await PickDesktopAsync(["Main Menu", "DesktopIndex"]);
                    if (d1 is not null) await WorkAsync(d1);
                    break;
                case 2: await SaveMenuAsync(); break;
                case 3: await RemoveMenuAsync(); break;
                case 4: await OtherActivitiesAsync(); break;
                case 5:
                    var quit = await QuitAsync();
                    if (quit is null) continue;          // cancelled — stay in the menu
                    return;                              // confirmed — MainMenuAsync powers off & cycles
            }
        }
    }

    /// <summary>Manage named Desktops: switch, new, rename, delete (v1.7).</summary>
    private async Task ManageDesktopsAsync()
    {
        await RefreshDesktopsAsync();

        while (true)
        {
            // SelectAsync auto-numbers string items and returns the list index, so we
            // pass plain names (no manual prefix). Desktops occupy 0..Count-1; the three
            // actions follow immediately after.
            var labels = new List<object?>();
            for (int i = 0; i < Desktops.Count; i++)
                labels.Add(Desktops[i].Name + (Desktops[i].Id == CurrentDesktopId ? "  *" : ""));
            labels.Add(Localizer.S("NewDesktop"));
            labels.Add(Localizer.S("RenameDesktop"));
            labels.Add(Localizer.S("DeleteDesktop"));

            int? sel = await SelectAsync([Localizer.S("ManageDesktops")], labels, esc: "EscapeCancel");
            if (sel is null) return;

            int n = Desktops.Count;
            if (sel.Value >= 0 && sel.Value < n)
            {
                // Switch to that Desktop.
                var target = Desktops[sel.Value];
                if (target.Id != CurrentDesktopId)
                    await SwitchDesktopAsync(target.Id);
                return;
            }

            int action = sel.Value - n;   // 0=New, 1=Rename, 2=Delete
            if (action == 0)
            {
                string? name = await BoxPromptAsync([Localizer.S("ManageDesktops"), "NewDesktop"], "TypeNameForNewFile", maxLen: 20);
                if (name is null) continue;
                name = Text.Clean(name).Trim();
                if (name.Length == 0) continue;
                var id = await CreateDesktopAsync(name);
                if (id is not null)
                {
                    Msg = Localizer.T("DesktopCreated", name);
                    await SwitchDesktopAsync(id);   // jump to the fresh Desktop
                }
            }
            else if (action == 1)
            {
                var pick = await SelectAsync([Localizer.S("RenameWhich")],
                    Desktops.Select(d => d.Name).Cast<object?>().ToList(), esc: "EscapeCancel");
                if (pick is null) continue;
                string? name = await BoxPromptAsync([Localizer.S("ManageDesktops"), "NewDesktop"], "TypeNewName", maxLen: 20);
                if (name is null) continue;
                name = Text.Clean(name).Trim();
                if (name.Length == 0) continue;
                if (await RenameDesktopAsync(Desktops[pick.Value].Id, name))
                {
                    // Refresh the in-memory list so the menu and status bar show
                    // the new name immediately (localStorage was already updated).
                    await RefreshDesktopsAsync();
                    Msg = Localizer.T("DesktopRenamed", name);
                }
            }
            else if (action == 2)
            {
                if (Desktops.Count <= 1) { Msg = Localizer.S("CannotDeleteLastDesktop"); continue; }
                var pick = await SelectAsync([Localizer.S("DeleteWhich")],
                    Desktops.Select(d => d.Name).Cast<object?>().ToList(), esc: "EscapeCancel");
                if (pick is null) continue;
                var target = Desktops[pick.Value];
                bool? ok = await YesNoAsync("ConfirmDeleteDesktop", false);
                if (ok != true) continue;
                // If deleting the current one, switch to another first.
                if (target.Id == CurrentDesktopId && Desktops.Count > 1)
                    await SwitchDesktopAsync(Desktops.First(d => d.Id != target.Id).Id);
                if (await DeleteDesktopAsync(target.Id))
                {
                    Msg = Localizer.T("DesktopDeleted", target.Name);
                    await RefreshDesktopsAsync();
                }
            }
        }
    }

    /// <summary>Restore previously saved files from localStorage onto the Desktop.</summary>
    private async Task RestoreDesktopFromLocalAsync()
    {
        await RefreshDesktopsAsync();

        List<string> names;
        try { names = await ListLocalFilesAsync(); }
        catch { return; }   // interop not ready — start with an empty desktop

        int skipped = 0;
        foreach (var name in names)
        {
            if (Desktop.Count >= MaxDesktop || Avail() <= 0) { skipped++; continue; }
            string? json = await LoadLocalFileAsync(name);
            if (json is null) continue;
            try
            {
                var d = Doc.LoadFromJson(this, json);
                if (d is null) { skipped++; continue; }
                // The saved file name carries the extension ("REPORT.rwp"); strip it.
                int dot = name.LastIndexOf('.');
                if (dot > 0) d.Name = Text.SafeName(name[..dot]);
                d.SavedKey = name;   // remember the storage key so a rename can clean it up
                Desktop.Add(d);
            }
            catch { skipped++; }   // corrupt entry — leave it in storage, don't crash
        }

        if (Desktop.Count > 0)
            Msg = Localizer.T("RestoredNFiles", Desktop.Count);
        if (skipped > 0)
            Msg += " " + Localizer.T("SkippedNFiles", skipped);
    }

    // ---- Helpers ----

    private static List<object?> Spaced(string[] items)
    {
        var outList = new List<object?>();
        for (int i = 0; i < items.Length; i++)
        {
            if (i > 0) outList.Add(null);
            outList.Add(items[i]);
        }
        return outList;
    }
}
