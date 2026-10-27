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

const LS_KEY = 'retroworks.files';

function readStore() {
    try {
        const raw = localStorage.getItem(LS_KEY);
        return raw ? JSON.parse(raw) : {};
    } catch {
        return {};   // corrupt store → start fresh rather than crash the app
    }
}

function writeStore(store) {
    localStorage.setItem(LS_KEY, JSON.stringify(store));
}

/** Save a file's JSON content to localStorage. Returns true on success. */
export function saveLocalFile(name, json) {
    try {
        const store = readStore();
        store[name] = json;
        writeStore(store);
        return true;
    } catch (e) {
        console.error('[RetroWorks] localStorage save failed:', e);
        return false;
    }
}

/** Load a file's JSON content from localStorage, or null if absent. */
export function loadLocalFile(name) {
    const store = readStore();
    return Object.prototype.hasOwnProperty.call(store, name) ? store[name] : null;
}

/** List all saved file names (with extensions). */
export function listLocalFiles() {
    return Object.keys(readStore());
}

/** Delete a file from localStorage. Returns true if it existed. */
export function deleteLocalFile(name) {
    const store = readStore();
    if (!Object.prototype.hasOwnProperty.call(store, name)) return false;
    delete store[name];
    writeStore(store);
    return true;
}
