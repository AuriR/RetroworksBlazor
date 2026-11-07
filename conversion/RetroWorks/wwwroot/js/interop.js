// RetroWorks browser interop layer.
// Responsibilities (per architecture): global keydown capture with preventDefault,
// file download/read via File API/Blob, and responsive font fitting for the 80×24 grid.
// This module NEVER mutates the grid DOM — Blazor owns that.

let dotNetRef = null;      // DotNetObjectReference to RetroWorks.razor
let keyHandler = null;     // bound keydown listener (for cleanup)

/** Map a KeyboardEvent to a RetroWorks key token, or null if not handled. */
function mapKey(e) {
    const k = e.key;

    // Named keys → tokens
    switch (k) {
        case 'ArrowUp': return 'UP';
        case 'ArrowDown': return 'DOWN';
        case 'ArrowLeft': return 'LEFT';
        case 'ArrowRight': return 'RIGHT';
        case 'Home': return 'HOME';
        case 'End': return 'END';
        case 'PageUp': return 'PGUP';
        case 'PageDown': return 'PGDN';
        case 'Backspace': return 'BKSP';
        case 'Delete': return 'DEL';
        case 'Enter': return 'ENTER';
        case 'Escape': return 'ESC';
        case 'Tab': return e.shiftKey ? 'BTAB' : 'TAB';
        case 'F1': return 'F1';
    }

    // Alt+Shift + letter → "AS-X" (download variant of an Alt command)
    if (e.altKey && e.shiftKey && k.length === 1) {
        return 'AS-' + k.toUpperCase();
    }

    // Alt + letter → "A-X" (Alt replaces the Open-Apple modifier)
    if (e.altKey && k.length === 1) {
        return 'A-' + k.toUpperCase();
    }

    // Plain printable character
    if (k.length === 1) {
        return k;
    }

    return null;
}

function onKeydown(e) {
    // If focus is on a native interactive element (e.g. the blazor-error-ui
    // "Reload" link), let the browser handle it — forwarding Enter to the app
    // while such an element is focused can trigger a page navigation/reload.
    const t = e.target;
    if (t && t.closest && t.closest('a, button, input, select, textarea')) return;

    const token = mapKey(e);
    if (token !== null) {
        e.preventDefault();   // stop browser shortcuts / scrolling
        dotNetRef.invokeMethodAsync('OnKey', token);
    }
}

/**
 * Initialize the global key handler.
 * @param {object} ref - DotNetObjectReference to the Blazor component.
 */
export function initKeyHandler(ref) {
    dotNetRef = ref;
    if (keyHandler) window.removeEventListener('keydown', keyHandler);
    keyHandler = onKeydown;
    window.addEventListener('keydown', keyHandler, true);

    // Fit the grid font to the viewport once fonts are ready.
    fitGrid();
    window.addEventListener('resize', fitGrid);
    if (document.fonts && document.fonts.ready) {
        document.fonts.ready.then(fitGrid);
    }
}

/**
 * Compute a font size so that 80 columns × 24 rows of VT323 fits the container.
 * VT323 has an aspect ratio of roughly 1:2 (width:height) per glyph.
 */
function fitGrid() {
    const el = document.getElementById('retro-screen');
    if (!el) return;
    const cw = window.innerWidth - 16;   // small page margin
    const ch = window.innerHeight - 16;
    // Each cell: width ≈ fontSize * 0.5, height ≈ fontSize * 1.0 (VT323 metrics)
    const byWidth = cw / (80 * 0.5);
    const byHeight = ch / (24 * 1.0);
    const size = Math.max(6, Math.floor(Math.min(byWidth, byHeight)));
    el.style.fontSize = size + 'px';
}

/**
 * Surface a .NET app-loop exception: browser console + a visible DOM node.
 */
export function reportError(msg) {
    console.error('[RetroWorks] ' + msg);
    let el = document.getElementById('retro-error');
    if (!el) {
        el = document.createElement('div');
        el.id = 'retro-error';
        el.style.cssText = 'position:fixed;bottom:0;left:0;right:0;z-index:9999;' +
            'background:#400;color:#f88;font:12px monospace;padding:6px;white-space:pre-wrap;';
        document.body.appendChild(el);
    }
    el.textContent = '[RetroWorks app error] ' + msg;
}

/**
 * Set the phosphor theme ("green"/"amber") and CRT emulation on the root.
 */
export function setTheme(theme, crt) {
    if (theme === 'amber') document.documentElement.setAttribute('data-theme', 'amber');
    else document.documentElement.removeAttribute('data-theme');
    if (crt) document.documentElement.classList.add('crt-on');
    else document.documentElement.classList.remove('crt-on');
}

/**
 * Trigger a browser download of text content as a file.
 */
export function downloadFile(fileName, content) {
    const blob = new Blob([content], { type: 'application/octet-stream' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = fileName;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    setTimeout(() => URL.revokeObjectURL(url), 1000);
}

/** Trigger a browser download of binary (Uint8Array) content as a file. */
function downloadBytes(fileName, bytes, mime) {
    const blob = new Blob([bytes], { type: mime });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = fileName;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    setTimeout(() => URL.revokeObjectURL(url), 1000);
}

/**
 * Dynamically load a UMD script and resolve when it's ready. Cached after first load.
 */
const _scriptCache = {};
function loadScript(src) {
    if (_scriptCache[src]) return _scriptCache[src];
    _scriptCache[src] = new Promise((resolve, reject) => {
        const s = document.createElement('script');
        s.src = src;
        s.onload = resolve;
        s.onerror = () => reject(new Error('Failed to load ' + src));
        document.head.appendChild(s);
    });
    return _scriptCache[src];
}

/**
 * Render paginated text to a PDF using the bundled ImgWriter NLQ font and trigger
 * a download. `pages` is an array of pages; each page is an array of display lines
 * (the C# side already composed header + body + footer per page). Monospace grid:
 * every character sits on a fixed column so it prints like the AppleWorks screen.
 */
export async function printPdf(fileName, pages) {
    // Load pdf-lib + fontkit on demand (UMD scripts set window.PDFLib / window.fontkit).
    await Promise.all([loadScript('/lib/pdf/pdf-lib.min.js'), loadScript('/lib/pdf/fontkit.umd.min.js')]);
    const PDFLib = window.PDFLib;
    const fontkit = window.fontkit;
    if (!PDFLib || !fontkit) throw new Error('pdf-lib / fontkit failed to initialize');

    // Load the bundled retro font (TrueType outlines — converted from OTF/CFF because
    // pdf-lib's fontkit chokes on the incomplete CFF Subrs table in this FontStruct font).
    const fontRes = await fetch('/fonts/imgwriter-nlq.ttf');
    if (!fontRes.ok) throw new Error('Could not load the print font (' + fontRes.status + ')');
    const fontBytes = new Uint8Array(await fontRes.arrayBuffer());

    const doc = await PDFLib.PDFDocument.create();
    doc.registerFontkit(fontkit);
    const font = await doc.embedFont(fontBytes);

    // US Letter, black on white. Monospace metrics from the font itself.
    const pageW = 612, pageH = 792;          // points (8.5 x 11 in)
    const margin = 40;
    const fontSize = 10;
    const charW = font.widthOfTextAtSize('M', fontSize);   // monospace advance width
    const lineH = fontSize * 1.25;
    const maxCols = Math.floor((pageW - 2 * margin) / charW);

    for (const lines of pages) {
        const page = doc.addPage([pageW, pageH]);
        // The C# side composes each page as [header, ...body..., footer].
        // Render the header + body top-down from the top margin, then pin the
        // footer centered at the bottom of the page (AppleWorks print style).
        const footer = lines.length > 0 ? lines[lines.length - 1] : '';
        const bodyLines = lines.slice(0, Math.max(0, lines.length - 1));

        let y = pageH - margin;              // top-down: start at the top margin
        for (let i = 0; i < bodyLines.length && y > margin + lineH * 2; i++) {
            let text = bodyLines[i] || '';
            if (text.length > maxCols) text = text.slice(0, maxCols);   // safety clip
            page.drawText(text, { x: margin, y: y - fontSize, size: fontSize, font: font, color: PDFLib.rgb(0, 0, 0) });
            y -= lineH;
        }

        // Footer: centered horizontally, sitting at the bottom margin.
        if (footer && footer.trim().length > 0) {
            const fw = font.widthOfTextAtSize(footer, fontSize);
            page.drawText(footer, { x: (pageW - fw) / 2, y: margin, size: fontSize, font: font, color: PDFLib.rgb(0, 0, 0) });
        }
    }

    const pdfBytes = await doc.save();
    downloadBytes(fileName, pdfBytes, 'application/pdf');
}

/**
 * Open the browser file picker and return the file's text content, or null.
 * @param {string} accept - File input accept attribute (e.g. ".rwp,.rdb,.rss").
 */
export async function loadFile(accept) {
    return new Promise((resolve) => {
        const input = document.createElement('input');
        input.type = 'file';
        if (accept) input.accept = accept;
        input.style.display = 'none';
        let settled = false;
        const done = (value) => {
            if (settled) return;
            settled = true;
            input.remove();
            resolve(value);
        };
        input.onchange = () => {
            const f = input.files && input.files[0];
            if (!f) { done(null); return; }
            const reader = new FileReader();
            reader.onload = () => done(String(reader.result));
            reader.onerror = () => done(null);
            reader.readAsText(f);
        };
        // If the user cancels the picker, 'cancel' fires (onchange does not).
        input.oncancel = () => done(null);
        document.body.appendChild(input);
        input.click();
        // Safety net: if neither event fires, give up after a while.
        setTimeout(() => done(null), 120000);
    });
}

// ---- localStorage persistence (RetroWorks "disk") -------------------------
// Saved files live under one namespaced key so they survive refresh/restart.
//
// v1.7 structure: multiple named Desktops, each with its own file set.
//   { desktops: [{ id, name, files: { "NAME.ext": json } }], currentId: "..." }
// Legacy (v≤1.6) was a flat map of filename → json; readStore() migrates it
// into a single "Desktop" entry so existing users keep their files.

const LS_KEY = 'retroworks.files';

function newDesktop(id, name) {
    return { id: id, name: name, files: {} };
}

/** Read + migrate the store. Always returns the v1.7 shape. */
function readStore() {
    let raw;
    try { raw = localStorage.getItem(LS_KEY); } catch { raw = null; }
    if (!raw) return { desktops: [newDesktop('d1', 'Desktop')], currentId: 'd1' };

    let store;
    try { store = JSON.parse(raw); } catch { return { desktops: [newDesktop('d1', 'Desktop')], currentId: 'd1' }; }

    // Already v1.7 shape.
    if (store && Array.isArray(store.desktops)) {
        if (!store.currentId || !store.desktops.some(d => d.id === store.currentId))
            store.currentId = store.desktops.length ? store.desktops[0].id : 'd1';
        return store;
    }

    // Legacy flat map → migrate into one "Desktop".
    const files = (store && typeof store === 'object') ? store : {};
    const migrated = { desktops: [newDesktop('d1', 'Desktop')], currentId: 'd1' };
    for (const k of Object.keys(files)) migrated.desktops[0].files[k] = files[k];
    writeStore(migrated);   // persist the migration immediately
    return migrated;
}

function writeStore(store) {
    localStorage.setItem(LS_KEY, JSON.stringify(store));
}

/** Find a desktop by id (or null). */
function findDesktop(store, id) {
    return store.desktops.find(d => d.id === id) || null;
}

/** Save a file's JSON content to the given Desktop. Returns true on success. */
export function saveLocalFile(desktopId, name, json) {
    try {
        const store = readStore();
        const d = findDesktop(store, desktopId);
        if (!d) return false;
        d.files[name] = json;
        writeStore(store);
        return true;
    } catch (e) {
        console.error('[RetroWorks] localStorage save failed:', e);
        return false;
    }
}

/** Load a file's JSON content from the given Desktop, or null if absent. */
export function loadLocalFile(desktopId, name) {
    const store = readStore();
    const d = findDesktop(store, desktopId);
    if (!d || !Object.prototype.hasOwnProperty.call(d.files, name)) return null;
    return d.files[name];
}

/** List all saved file names (with extensions) in the given Desktop. */
export function listLocalFiles(desktopId) {
    const store = readStore();
    const d = findDesktop(store, desktopId);
    return d ? Object.keys(d.files) : [];
}

/** Delete a file from the given Desktop. Returns true if it existed. */
export function deleteLocalFile(desktopId, name) {
    const store = readStore();
    const d = findDesktop(store, desktopId);
    if (!d || !Object.prototype.hasOwnProperty.call(d.files, name)) return false;
    delete d.files[name];
    writeStore(store);
    return true;
}

// ---- Desktop management (v1.7) -------------------------------------------

/** List all Desktops as [{id, name}] plus the current id. */
export function listDesktops() {
    const store = readStore();
    return { desktops: store.desktops.map(d => ({ id: d.id, name: d.name })), currentId: store.currentId };
}

/** Create a new Desktop with the given name. Returns its id (or null on failure). */
export function createDesktop(name) {
    try {
        const store = readStore();
        const id = 'd' + Date.now().toString(36);
        store.desktops.push(newDesktop(id, name));
        writeStore(store);
        return id;
    } catch (e) {
        console.error('[RetroWorks] createDesktop failed:', e);
        return null;
    }
}

/** Rename a Desktop. Returns true on success. */
export function renameDesktop(id, name) {
    try {
        const store = readStore();
        const d = findDesktop(store, id);
        if (!d) return false;
        d.name = name;
        writeStore(store);
        return true;
    } catch (e) {
        console.error('[RetroWorks] renameDesktop failed:', e);
        return false;
    }
}

/** Delete a Desktop and all its files. The last remaining Desktop cannot be deleted. */
export function deleteDesktop(id) {
    try {
        const store = readStore();
        if (store.desktops.length <= 1) return false;   // keep at least one
        const idx = store.desktops.findIndex(d => d.id === id);
        if (idx < 0) return false;
        store.desktops.splice(idx, 1);
        if (store.currentId === id) store.currentId = store.desktops[0].id;
        writeStore(store);
        return true;
    } catch (e) {
        console.error('[RetroWorks] deleteDesktop failed:', e);
        return false;
    }
}

/** Set which Desktop is current. Returns the new current id (or null). */
export function setCurrentDesktop(id) {
    try {
        const store = readStore();
        if (!findDesktop(store, id)) return null;
        store.currentId = id;
        writeStore(store);
        return id;
    } catch (e) {
        console.error('[RetroWorks] setCurrentDesktop failed:', e);
        return null;
    }
}

// ---- Settings persistence (v1.7) ------------------------------------------
const SETTINGS_KEY = 'retroworks.settings';

/** Load saved settings. Returns {memKb, theme, crt, lang} with defaults if absent. */
export function loadSettings() {
    try {
        const raw = localStorage.getItem(SETTINGS_KEY);
        if (!raw) return { memKb: 1024, theme: 'green', crt: false, lang: 'en' };
        const s = JSON.parse(raw);
        return {
            memKb: typeof s.memKb === 'number' ? s.memKb : 1024,
            theme: s.theme === 'amber' ? 'amber' : 'green',
            crt: !!s.crt,
            lang: s.lang === 'he' ? 'he' : 'en'
        };
    } catch {
        return { memKb: 1024, theme: 'green', crt: false, lang: 'en' };
    }
}

/** Save settings to localStorage. */
export function saveSettings(memKb, theme, crt, lang) {
    try {
        localStorage.setItem(SETTINGS_KEY, JSON.stringify({ memKb, theme, crt, lang }));
        return true;
    } catch (e) {
        console.error('[RetroWorks] saveSettings failed:', e);
        return false;
    }
}
