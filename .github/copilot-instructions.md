# RetroWorks — Copilot Instructions

RetroWorks is a retro Apple II desktop (AppleWorks-style) with three document types:
**WP** (word processor), **DB** (database), **SS** (spreadsheet with formula engine).
80×24 character grid UI, phosphor CRT aesthetic, EN/HE localization.

## Repo Layout

- `retroworks.py` — the **original Python reference implementation** (curses-based). Treat as read-only ground truth for behavior. Do NOT modify it.
- `conversion/RetroWorks.sln` + `conversion/RetroWorks/RetroWorks.csproj` — the C# port (.NET 10, Blazor WebAssembly SDK, AOT disabled).
- `screenshots/`, `RetroWorks.bat`, `RetroWorks.command` — Python launcher artifacts.

## Build & Run

```powershell
dotnet build "c:\dev\RetroWorks\conversion\RetroWorks\RetroWorks.csproj" --nologo -v q
dotnet run --project "c:\dev\RetroWorks\conversion\RetroWorks\RetroWorks.csproj"
# → http://localhost:5114 (from Properties/launchSettings.json)
```

- **Standard is 0 warnings, 0 errors.** Never leave a build with warnings.
- The user often has the app open in a browser tab and actively using it. C# rebuilds + server restarts disrupt their session — warn before restarting, or batch changes. CSS/JS-only changes need no rebuild (just refresh).

## Architecture (C# port)

### `Core/App.cs` — main controller
- Grid buffers: `char[,] VChars` / `int[,] VAttrs`, fixed `MinH=24`, `MinW=80`.
- Attr bits: `A_N`=0, `A_B`(bold)=1, `A_U`(underline)=2, `A_R`(reverse)=4.
- Rendering: `event Action? RenderRequested` → component calls `StateHasChanged`; `RenderTick` int changes per tick to force re-render.
- Input: `KeyAsync()` drains a `ConcurrentQueue<string>` then awaits a TCS; `EnqueueKey(token)` is the entry point (called from JS via `[JSInvokable]`).
- Cursor: `SetCursor(y,x)` / `CursorAt` — AppleWorks-style blinking **underline** cursor.
- File I/O hooks: `Func<string,string,Task>? DownloadFileFunc`, `Func<string,Task<string?>>? LoadFileFunc`.
- **localStorage persistence** ("the disk"): `SaveLocalFileFunc` / `LoadLocalFileFunc` / `ListLocalFilesFunc` / `DeleteLocalFileFunc` + helper methods. All saved files live under one namespaced key `retroworks.files` in the browser's localStorage.
- Flow: `RunAsync()` = Splash → **RestoreDesktopFromLocalAsync()** (auto-restores saved files onto the Desktop) → MainMenu; memory model: `MemKb` (default 1024), `ProgramK=72`, `Avail() => MemKb - ProgramK - desktop file sizes`.
- **Quit = power cycle**: `QuitAsync()` saves dirty files to localStorage, then the main loop clears the Desktop and re-runs Splash → RestoreDesktopFromLocalAsync. The app never dies — quitting is like switching a real machine off and on (saved files come back).

### Key tokens are **strings** (not enums)
`"UP","DOWN","LEFT","RIGHT","HOME","END","PGUP","PGDN","BKSP","DEL","ENTER","ESC","TAB","BTAB","F1"`; Alt+letter = `"A-X"` (e.g. `Key.Alt("Q")` → `"A-Q"`); **Alt+Shift+letter = `"AS-X"`** (download variant, e.g. `"AS-S"`).
- **C# `switch` cases must use plain string literals** — no `Key.Alt(...)` calls in case labels (not compile-time constants).

### Documents (`Core/Doc.cs`, `WPDoc.cs`, `DBDoc.cs`, `SSDoc.cs`)
- `Doc.RunAsync` loop: `Draw()` → `KeyAsync()` → Escape→"MENU", Alt-Q rename, **Alt-S save to disk (localStorage)**, **Alt-Shift-S (`"AS-S"`) download file**, Alt-K/F1 help, else `HandleAsync(k)`.
- **Save vs. download:** `DoSaveAsync()` writes to localStorage only (no browser download); `DoDownloadAsync()` triggers a browser download. The main-menu "Save Desktop files" does BOTH (localStorage + download) so the on-disk copy stays in sync. Removing a file deletes it from localStorage too.
- **Any single printable character starts editing** in SS/DB (matches Python's `len(k)==1`). Do NOT filter to letters/digits — that breaks formula entry (`=A1+B2`, parentheses).

### Formula engine (`Core/FormulaEngine.cs`)
- Tokenizer regex supports numbers, cell refs (`$A$1`), functions, and operators `[-+*/^(),:<>=]`.
- `IsValue(raw)` = first char in `"0123456789+-.=@("`.
- Circular refs → `SSError`; cache cleared on every edit.

### Components & interop
- `Components/RetroScreen.razor` — renders the whole grid as **one `MarkupString`** (StringBuilder over VChars/VAttrs). No per-cell DOM diffing. Adds `" cursor"` class to the cell at `App.CursorAt`. HTML-escapes `< > & "`.
- `Components/RetroApp.razor` — holds `_appRef = DotNetObjectReference.Create(this)`; imports JS module in `OnAfterRenderAsync`; wires `RenderRequested`→`StateHasChanged`, download/load/localStorage funcs; `SyncTheme()` calls `setTheme(theme, crtEmulation)` so both the phosphor color and the CRT class stay in sync; app-loop catch → `jsModule.InvokeVoidAsync("reportError", ...)`.
- **`[JSInvokable] OnKey(string token)` must live on the component (`RetroApp.razor`), NOT on `Core.App`** — JS can only invoke methods on DotNetObjectReference targets. This was a real bug: invocations to Core.App silently failed and the app froze at splash.
- `wwwroot/js/interop.js` — `mapKey()` maps KeyboardEvent→token (named keys, Alt+Shift+letter → `"AS-X"`, Alt+letter → `"A-X"`, any other single char passes through); global keydown with `preventDefault`; `reportError(msg)` shows a fixed red banner; `loadFile` has oncancel + settled flag + 120s timeout. localStorage functions: `saveLocalFile` / `loadLocalFile` / `listLocalFiles` / `deleteLocalFile` (all under key `retroworks.files`; corrupt store → start fresh, never crash).
- `wwwroot/css/app.css` — phosphor theme: VT323 font, `--bg #050805`, `--fg-green #33ff66`, amber via `data-theme`; `.retro-cell { width: 0.5em }`. **CRT emulation** (toggle in Other Activities): `html.crt-on .retro-screen` gets rounded glass edges + a screen-door/scanline `::before` overlay with a slow rolling animation; colors derive from the theme vars so it works for green and amber alike.

### Localization
- RESX: `Resources/Strings.resx` (EN) + `Strings.he.resx` (Hebrew). **Every new user-facing string needs BOTH files.**
- API: `Localizer.S(key)` static, `Localizer.T(key, args...)` = string.Format, `Localizer.Help(key)`.
- Hebrew is RTL — use `Bidi.FirstStrong()` / `Bidi.BidiVisual()` for text layout; line editor swaps Left/Right arrows in RTL context.

## Hard-Won Lessons (do not repeat these bugs)

1. **Python f-string alignment does NOT port to C# interpolation.** `{x:>4}` in a C# interpolated string renders the literal text `>4` (treated as format specifier on a non-numeric). Use `.ToString().PadLeft(N)` / `.PadRight(N)`. This bug caused broken menu numbering (`">2."`) and spreadsheet row numbers.
2. **Blazor WASM dev-server caching:** after rebuilds, existing tabs keep the old in-memory runtime; new tabs can hit stale-fingerprint 404s (e.g. `RetroWorks.styles.css`). Fix: hard-reload with a cache-busting query param (`/?t=Date.now()`). Verify real content via text-grid extraction, not just "page loaded".
3. **Input filtering:** the line editor and SS/DB edit-start must accept any printable char (`!char.IsControl`), never `IsLetterOrDigit` — formulas need `= ( ) - * / . , "`.
4. **Cursor rendering:** use `box-shadow: inset 0 -0.12em 0 currentColor` for the underline cursor, NOT `border-bottom` — borders shift layout by 1px and misalign rows. Blink via `animation: cw-blink 1s steps(2,start) infinite`.
5. **Menu digit selection:** guard against over-long input (`if (digits.Length > 2) digits = digits[^2..]`) — slicing a 1-char string with `[^2..]` throws and silently kills the app loop.
6. **File picker cancel** must resolve the await (oncancel + settled flag) or the app hangs forever.
7. **Kind mapping off-by-one:** when mapping menu choice N to an array, use `kinds[i.Value - 1]`, not `-2`.

## Verification Workflow

- Verify UI by extracting the text grid with Playwright (user prefers NO screenshots):
  ```js
  // rows are .retro-row divs containing .retro-cell spans
  Array.from(el.querySelectorAll('.retro-row'))
    .map(row => Array.from(row.children).map(c => c.textContent).join(''))
    .join('\n');
  ```
- Drive the app with `page.keyboard.press/type` — tokens map 1:1 to physical keys.
- Menu flow: type digit → press Enter (two steps, not one).

## Conventions

- Calm, muted retro aesthetic; phosphor green/amber only. No modern UI chrome inside the grid.
- Porting rule: when behavior is in question, **check `retroworks.py` first** — it is the spec.
- Keep comments explaining non-obvious porting decisions (e.g. PadLeft NOTEs).

## Architecture.md maintenance (living document)

- `Architecture.md` at repo root is a **living architecture doc** (data model, screen flows, entry points, business rules; mermaid diagrams). It must be updated whenever non-trivial changes are made to the app (new doc type, new persistence mechanism, significant refactor of flows, version bumps, etc.). If you change code and don't update it, revert the change.
- **No authentication** in this project — omit any auth section and do not add authenticated/guest flow diagrams.
- Mermaid diagrams must parse cleanly (GitHub renders them). Rules: no HTML entities (`&lt;`/`&gt;`) inside node labels; quote subgraph titles that contain parentheses (`subgraph X["Title (paren)"]`); avoid pipe-delimited edge labels in `erDiagram`. Validate with the official Mermaid CLI before relying on GitHub rendering.
- Keep temp/validation scripts out of the repo — delete them after use.
