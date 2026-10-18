# RetroWorks ][

An integrated **Word Processor, Data Base and Spreadsheet** in the style of the
80‑column, green‑screen productivity software of the Apple ][ years, rebuilt
for today's Mac and Windows terminal.

The whole program is a **single Python file (~2,400 lines)** that uses only the
Python standard library.

![Main Menu](screenshots/main-menu.png)
![Add Files](screenshots/add-files-menu.png)
![Word Processor](screenshots/word-processor-menu.png)

## Features

- **Desktop & stacked menus.** Up to 12 open files. Each sub-menu opens on top of
  the last like a folder tab, and Escape steps back one layer. Ctrl‑Q
  (Quick Change) jumps straight to another file.
- **Word Processor.** Word wrap, insert/overwrite, find & replace,
  cut/copy/paste paragraphs, "print" to a .txt file, import a text file.
- **Spreadsheet.** Formulas like `+A1*B1` and `@SUM(A1...A10)`, about 20
  functions (`@AVG @MIN @MAX @ROUND @IF @SQRT`…), relative and absolute (`$A$1`)
  references, replicate, insert/delete rows, CSV import and export.
- **Data Base.** Multiple-record and single-record layouts (Ctrl‑Z), select,
  sort, edit categories, CSV import and export.

Because macOS keeps the Command key for itself, **Ctrl takes the place of the
Open‑Apple key**. Press **Ctrl‑K** in any file for its command list.

## Running it

**Mac:** double-click `RetroWorks.command`. The first time, macOS may block the
unsigned file: right-click it and choose **Open**, or allow it in System Settings →
Privacy & Security. You can also run it from Terminal:

    python3 retroworks.py

**Windows:** double-click `RetroWorks.bat`. On the first run it installs
`windows-curses`. Windows support has not been tested yet, so reports are welcome.

    pip install windows-curses
    python retroworks.py

Options: `--theme green|amber|white` and `--folder <path>` (default `~/RetroWorks`).

Files are saved as plain JSON: `.rwp` (word processor), `.rdb` (data base) and
`.rss` (spreadsheet).

## About

Written with Claude (Anthropic's AI). RetroWorks is an independent program
written from scratch. It is **not AppleWorks**, contains no Apple code, and is
not affiliated with or endorsed by Apple Inc.
