#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
RetroWorks ][  -  an original tribute to the 1980s integrated
"Word Processor / Data Base / Spreadsheet" programs of the Apple ][ era.

Runs in the macOS Terminal (Apple silicon M1-M4 or Intel) using only the
Python 3 standard library.

    python3 retroworks.py                  # start (green phosphor)
    python3 retroworks.py --theme amber    # amber monitor
    python3 retroworks.py --folder ~/Documents/RetroWorks

This is an independent program written from scratch. It is not AppleWorks,
contains no Apple code, and is not affiliated with or endorsed by Apple Inc.
"""

import argparse
import csv
import curses
import datetime
import json
import locale
import math
import os
import re
import sys
import textwrap
import unicodedata

VERSION = '1.2'
EXT = {'wp': '.rwp', 'db': '.rdb', 'ss': '.rss'}
KIND_NAME = {'wp': 'Word Processor', 'db': 'Data Base', 'ss': 'Spreadsheet'}
KIND_SHORT = {'wp': 'WP', 'db': 'DB', 'ss': 'SS'}
MAX_DESKTOP = 12
COLS = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ'
MAXROW = 999
MIN_H, MIN_W = 24, 80
# memory configurations offered under Other Activities > Memory size.
# The Desktop gets what is left after the program itself (72K).
MEMORY_SIZES = [('Apple //e  128K', 128),
                ('RamWorks III  512K', 512),
                ('RamWorks III  1 MB', 1024),
                ('RamWorks III  3 MB', 3072)]
PROGRAM_K = 72


# --------------------------------------------------------------------------
# languages
#
# Every piece of text the user sees goes through T().  A language is a
# dictionary that maps the English text to its translation; anything that
# is missing simply stays in English.  Hebrew is built in (bottom of this
# file).  More languages can be added without touching the program: put a
# file such as  languages/fr.json  next to retroworks.py containing
#   {"name": "Français", "strings": {"Main Menu": "Menu principal", ...}}
# --------------------------------------------------------------------------

LANG = 'en'
LANGUAGES = {'en': {'name': 'English', 'strings': {}, 'help': {}}}
CONFIG_PATH = os.path.expanduser('~/.retroworks.json')


def T(_s, **kw):
    tr = LANGUAGES.get(LANG, LANGUAGES['en'])['strings'].get(_s, _s)
    if not kw:
        return tr
    try:
        return tr.format(**kw)
    except (KeyError, IndexError, ValueError):
        return _s.format(**kw)


def help_text(key, english):
    return LANGUAGES.get(LANG, LANGUAGES['en']).get('help', {}).get(key) or english


def load_config():
    try:
        with open(CONFIG_PATH, encoding='utf-8') as f:
            cfg = json.load(f)
        return cfg if isinstance(cfg, dict) else {}
    except (OSError, ValueError):
        return {}


def save_config(cfg):
    try:
        with open(CONFIG_PATH, 'w', encoding='utf-8') as f:
            json.dump(cfg, f, ensure_ascii=False, indent=1)
    except OSError:
        pass


def load_language_files():
    folder = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'languages')
    try:
        names = sorted(os.listdir(folder))
    except OSError:
        return
    for fn in names:
        if not fn.endswith('.json'):
            continue
        try:
            with open(os.path.join(folder, fn), encoding='utf-8') as f:
                data = json.load(f)
            code = fn[:-5]
            LANGUAGES[code] = {'name': str(data.get('name', code)),
                               'strings': dict(data.get('strings', {})),
                               'help': dict(data.get('help', {}))}
        except (OSError, ValueError, TypeError, AttributeError):
            pass


def set_language(code):
    global LANG
    LANG = code if code in LANGUAGES else 'en'


def first_strong(s):
    """'R' if the first letter of s is right-to-left (Hebrew, Arabic...),
    'L' if it is left-to-right, None if s has no letters."""
    for ch in s:
        b = unicodedata.bidirectional(ch)
        if b in ('R', 'AL'):
            return 'R'
        if b == 'L':
            return 'L'
    return None


# --------------------------------------------------------------------------
# right-to-left text (the Unicode bidirectional algorithm, UAX #9)
#
# Text is stored in logical (typing) order.  bidi_visual() works out the
# order in which it must appear on screen for a paragraph whose base
# direction is 'L' or 'R'.
#
# Some terminals (macOS Terminal) also reorder Hebrew themselves, always as
# if every line were left-to-right.  For those, terminal_preimage() finds
# the text to send so that, after the terminal's own reordering, the screen
# shows exactly the visual line we want.
# --------------------------------------------------------------------------

MIRROR = {'(': ')', ')': '(', '[': ']', ']': '[', '{': '}', '}': '{',
          '<': '>', '>': '<', '\u00ab': '\u00bb', '\u00bb': '\u00ab'}
OPEN_BRACKETS = {'(': ')', '[': ']', '{': '}'}
RTL_RE = re.compile('[\u0590-\u08ff\ufb1d-\ufdff\ufe70-\ufefc]')
NEUTRALS = ('B', 'S', 'WS', 'ON', 'BN')


def _btype(ch):
    t = unicodedata.bidirectional(ch)
    if not t:
        return 'L'
    if t in ('LRE', 'RLE', 'LRO', 'RLO', 'PDF'):
        return 'BN'
    if t in ('LRI', 'RLI', 'FSI', 'PDI'):
        return 'ON'
    return t


def _strong(t):
    return 'L' if t == 'L' else 'R' if t in ('R', 'EN', 'AN') else None


def bidi_levels(s, base):
    n = len(s)
    e = 1 if base == 'R' else 0
    edir = 'R' if e else 'L'
    orig = [_btype(c) for c in s]
    t = list(orig)
    prev = edir                                     # W1
    for i in range(n):
        if t[i] == 'NSM':
            t[i] = prev
        elif t[i] != 'BN':
            prev = t[i]
    last = edir                                     # W2
    for i in range(n):
        if t[i] in ('L', 'R', 'AL'):
            last = t[i]
        elif t[i] == 'EN' and last == 'AL':
            t[i] = 'AN'
    t = ['R' if x == 'AL' else x for x in t]        # W3
    for i in range(1, n - 1):                       # W4
        if t[i] == 'ES' and t[i - 1] == 'EN' and t[i + 1] == 'EN':
            t[i] = 'EN'
        elif t[i] == 'CS' and t[i - 1] == t[i + 1] and t[i - 1] in ('EN', 'AN'):
            t[i] = t[i - 1]
    i = 0                                           # W5
    while i < n:
        if t[i] == 'ET':
            j = i
            while j < n and t[j] == 'ET':
                j += 1
            if (i > 0 and t[i - 1] == 'EN') or (j < n and t[j] == 'EN'):
                t[i:j] = ['EN'] * (j - i)
            i = j
        else:
            i += 1
    t = ['ON' if x in ('ES', 'ET', 'CS') else x for x in t]   # W6
    last = edir                                     # W7
    for i in range(n):
        if t[i] in ('L', 'R'):
            last = t[i]
        elif t[i] == 'EN' and last == 'L':
            t[i] = 'L'
    stack, pairs = [], []                           # N0: bracket pairs
    for i, c in enumerate(s):
        if t[i] != 'ON':
            continue
        if c in OPEN_BRACKETS:
            if len(stack) >= 63:
                break
            stack.append((OPEN_BRACKETS[c], i))
        elif c in (')', ']', '}'):
            for k in range(len(stack) - 1, -1, -1):
                if stack[k][0] == c:
                    pairs.append((stack[k][1], i))
                    del stack[k:]
                    break
    for a, b in sorted(pairs):
        inside = {_strong(t[k]) for k in range(a + 1, b)}
        if edir in inside:
            d = edir
        elif inside & {'L', 'R'}:
            d = edir
            for k in range(a - 1, -1, -1):
                if _strong(t[k]):
                    d = _strong(t[k])
                    break
        else:
            continue
        t[a] = t[b] = d
    i = 0                                           # N1, N2
    while i < n:
        if t[i] in NEUTRALS:
            j = i
            while j < n and t[j] in NEUTRALS:
                j += 1
            before = edir if i == 0 else (_strong(t[i - 1]) or edir)
            after = edir if j == n else (_strong(t[j]) or edir)
            d = before if before == after else edir
            t[i:j] = [d] * (j - i)
            i = j
        else:
            i += 1
    if e == 0:                                      # I1, I2
        lev = [1 if x == 'R' else 2 if x in ('AN', 'EN') else 0 for x in t]
    else:
        lev = [2 if x in ('L', 'EN', 'AN') else 1 for x in t]
    i = n - 1                                       # L1
    while i >= 0 and orig[i] in ('WS', 'BN'):
        lev[i] = e
        i -= 1
    for i in range(n):
        if orig[i] in ('S', 'B'):
            lev[i] = e
            k = i - 1
            while k >= 0 and orig[k] in ('WS', 'BN'):
                lev[k] = e
                k -= 1
    return lev


def bidi_visual(s, base='L'):
    """Return (visual string, order, levels): order[v] is the index in s of
    the character shown at visual position v."""
    n = len(s)
    if base != 'R' and not RTL_RE.search(s):
        return s, list(range(n)), [0] * n
    lev = bidi_levels(s, base)
    order = list(range(n))
    odd = [x for x in lev if x % 2]
    if odd:
        for level in range(max(lev), min(odd) - 1, -1):     # L2
            i = 0
            while i < n:
                if lev[order[i]] >= level:
                    j = i
                    while j < n and lev[order[j]] >= level:
                        j += 1
                    order[i:j] = order[i:j][::-1]
                    i = j
                else:
                    i += 1
    vis = ''.join(MIRROR.get(s[k], s[k]) if lev[k] % 2 else s[k] for k in order)
    return vis, order, lev


LRM = '\u200e'     # invisible left-to-right mark


def terminal_preimage(v):
    """Cells to send to a terminal that reorders every line left-to-right by
    itself, so that after its reordering the screen shows the visual line v.

    Every stretch of Hebrew in v (letters, and the spaces and punctuation
    between them) is sent reversed; the terminal reverses it back.  When a
    number follows such a stretch the terminal would pull the number into
    it, so an invisible LRM mark is added after the stretch to stop it.

    Returns (cells, src): cells[j] is what to write in column j (a character,
    sometimes followed by LRM) and src[j] the position in v it shows."""
    n = len(v)
    cells, src = list(v), list(range(n))
    if not RTL_RE.search(v):
        return cells, src
    types = [_btype(c) for c in v]
    i = 0
    while i < n:
        if types[i] not in ('R', 'AL'):
            i += 1
            continue
        last, k = i, i + 1
        while k < n and types[k] not in ('L', 'EN', 'AN'):
            if types[k] in ('R', 'AL'):
                last = k
            k += 1
        for pos, frm in zip(range(i, last + 1), range(last, i - 1, -1)):
            cells[pos] = MIRROR.get(v[frm], v[frm])    # the terminal mirrors it back
            src[pos] = frm
        if k < n and types[k] in ('EN', 'AN'):
            cells[last] += LRM
        i = last + 1
    return cells, src


# --------------------------------------------------------------------------
# small helpers
# --------------------------------------------------------------------------

def cursor(n):
    try:
        curses.curs_set(n)
    except curses.error:
        pass


def beep():
    try:
        curses.beep()
    except curses.error:
        pass


def clean(s):
    s = str(s).replace('\t', '    ')
    return ''.join(ch for ch in s if ch >= ' ' and ch != '\x7f' and not zero_width(ch))


def zero_width(ch):
    return unicodedata.combining(ch) != 0 or unicodedata.category(ch) in ('Mn', 'Me', 'Cf')


def safe_name(name):
    n = re.sub(r'[/\\:\x00-\x1f]', '_', name).strip().strip('.')
    return n or 'Untitled'


def wrap_starts(s, w):
    """Word-wrap: return the start offset of every display row of s."""
    starts = [0]
    i, n = 0, len(s)
    while n - i > w:
        sp = s.rfind(' ', i, i + w + 1)
        brk = sp + 1 if sp > i else i + w
        starts.append(brk)
        i = brk
    return starts


# --------------------------------------------------------------------------
# spreadsheet formula engine
# --------------------------------------------------------------------------

class SSError(Exception):
    pass


TOK = re.compile(
    r'\s*(?:'
    r'(\d+(?:\.\d*)?(?:[eE][-+]?\d+)?|\.\d+(?:[eE][-+]?\d+)?)'   # number
    r'|(\$?[A-Za-z]\$?\d+)(?![A-Za-z0-9])'                       # cell ref
    r'|(@?[A-Za-z][A-Za-z0-9]*)'                                 # function
    r'|(\.\.\.|\.\.|<=|>=|<>|[-+*/^(),:<>=])'                    # operator
    r')')
REF_ADJ = re.compile(r'(?<![A-Za-z0-9$])(\$?)([A-Za-z])(\$?)(\d+)(?![A-Za-z0-9])')


def is_value(raw):
    return bool(raw) and raw[0] in '0123456789+-.=@('


def split_ref(ref):
    m = re.fullmatch(r'([A-Z])(\d+)', ref)
    if not m:
        raise SSError('bad cell reference')
    r = int(m.group(2))
    if not 1 <= r <= MAXROW:
        raise SSError('bad cell reference')
    return ord(m.group(1)) - 65, r


def tokenize(s):
    s = s.rstrip()
    pos, out = 0, []
    while pos < len(s):
        m = TOK.match(s, pos)
        if not m or m.end() == pos:
            raise SSError('syntax error')
        pos = m.end()
        num, ref, name, op = m.groups()
        if num is not None:
            out.append(('n', float(num)))
        elif ref:
            out.append(('r', ref.upper().replace('$', '')))
        elif name:
            out.append(('f', name.upper().lstrip('@')))
        elif op:
            out.append(('o', op))
    return out


def shift_refs(raw, dr, dc):
    """Adjust relative references (A1) by dr rows / dc cols; $A$1 stays."""
    def rep(m):
        ca, col, ra, row = m.groups()
        c, r = ord(col.upper()) - 65, int(row)
        if not ca:
            c += dc
        if not ra:
            r += dr
        if not (0 <= c < 26 and 1 <= r <= MAXROW):
            return '#REF'
        return f'{ca}{COLS[c]}{ra}{r}'
    return REF_ADJ.sub(rep, raw)


def fix_rows(raw, at, delta):
    """Fix references after inserting (+1) or deleting (-1) row `at`."""
    def rep(m):
        ca, col, ra, row = m.groups()
        r = int(row)
        if delta > 0 and r >= at:
            r += 1
        elif delta < 0:
            if r == at:
                return '#REF'
            if r > at:
                r -= 1
        if r > MAXROW:
            return '#REF'
        return f'{ca}{col.upper()}{ra}{r}'
    return REF_ADJ.sub(rep, raw)


def _num_args(name, args, n):
    if len(args) != n or any(isinstance(a, list) for a in args):
        raise SSError(f'@{name} needs {n} value(s)')
    return args


def call_func(name, args):
    vals = []
    for a in args:
        vals.extend(a if isinstance(a, list) else [a])
    try:
        if name == 'SUM':
            return float(sum(vals))
        if name in ('AVG', 'AVERAGE'):
            if not vals:
                raise SSError('@AVG of nothing')
            return sum(vals) / len(vals)
        if name == 'MIN':
            if not vals:
                raise SSError('@MIN of nothing')
            return min(vals)
        if name == 'MAX':
            if not vals:
                raise SSError('@MAX of nothing')
            return max(vals)
        if name == 'COUNT':
            return float(len(vals))
        one = {'ABS': abs, 'INT': lambda x: float(math.trunc(x)), 'SQRT': math.sqrt,
               'EXP': math.exp, 'LN': math.log, 'LOG': math.log10, 'SIN': math.sin,
               'COS': math.cos, 'TAN': math.tan, 'ATAN': math.atan}
        if name in one:
            (x,) = _num_args(name, args, 1)
            return float(one[name](x))
        if name == 'ROUND':
            if len(args) == 1:
                args = args + [0.0]
            x, n = _num_args(name, args, 2)
            f = 10 ** int(n)
            return math.copysign(math.floor(abs(x) * f + 0.5) / f, x)
        if name == 'MOD':
            x, y = _num_args(name, args, 2)
            if y == 0:
                raise SSError('division by zero')
            return math.fmod(x, y)
        if name == 'IF':
            c, x, y = _num_args(name, args, 3)
            return x if c != 0 else y
    except (ValueError, OverflowError, ZeroDivisionError):
        raise SSError(f'@{name}: math error')
    raise SSError(f'unknown function @{name}')


class Parser:
    def __init__(self, sheet, toks):
        self.s, self.t, self.i = sheet, toks, 0

    def peek(self):
        return self.t[self.i] if self.i < len(self.t) else (None, None)

    def take(self):
        tok = self.peek()
        self.i += 1
        return tok

    def expect(self, op):
        if self.take() != ('o', op):
            raise SSError(f'expected "{op}"')

    def parse(self):
        if not self.t:
            raise SSError('empty formula')
        v = self.cmp()
        if self.i != len(self.t):
            raise SSError('syntax error')
        return v

    def cmp(self):
        a = self.add()
        k, v = self.peek()
        if k == 'o' and v in ('<', '>', '=', '<=', '>=', '<>'):
            self.take()
            b = self.add()
            return float({'<': a < b, '>': a > b, '=': a == b, '<=': a <= b,
                          '>=': a >= b, '<>': a != b}[v])
        return a

    def add(self):
        a = self.mul()
        while True:
            k, v = self.peek()
            if k == 'o' and v in ('+', '-'):
                self.take()
                b = self.mul()
                a = a + b if v == '+' else a - b
            else:
                return a

    def mul(self):
        a = self.unary()
        while True:
            k, v = self.peek()
            if k == 'o' and v in ('*', '/'):
                self.take()
                b = self.unary()
                if v == '*':
                    a *= b
                else:
                    if b == 0:
                        raise SSError('division by zero')
                    a /= b
            else:
                return a

    def unary(self):
        k, v = self.peek()
        if k == 'o' and v in ('-', '+'):
            self.take()
            x = self.unary()
            return -x if v == '-' else x
        return self.power()

    def power(self):
        a = self.primary()
        if self.peek() == ('o', '^'):
            self.take()
            b = self.unary()
            try:
                r = a ** b
            except (OverflowError, ZeroDivisionError):
                raise SSError('math error')
            if isinstance(r, complex):
                raise SSError('math error')
            return float(r)
        return a

    def primary(self):
        k, v = self.take()
        if k == 'n':
            return v
        if k == 'r':
            return self.s.num(v)
        if (k, v) == ('o', '('):
            x = self.cmp()
            self.expect(')')
            return x
        if k == 'f':
            if v == 'PI':
                if self.peek() == ('o', '('):
                    self.take()
                    self.expect(')')
                return math.pi
            self.expect('(')
            args = []
            if self.peek() != ('o', ')'):
                while True:
                    args.append(self.arg())
                    if self.peek() == ('o', ','):
                        self.take()
                        continue
                    break
            self.expect(')')
            return call_func(v, args)
        raise SSError('syntax error')

    def arg(self):
        k, v = self.peek()
        if k == 'r' and self.i + 1 < len(self.t):
            k2, v2 = self.t[self.i + 1]
            if k2 == 'o' and v2 in ('...', '..', ':'):
                self.take()
                self.take()
                k3, v3 = self.take()
                if k3 != 'r':
                    raise SSError('bad range')
                return self.s.range_vals(v, v3)
        return self.cmp()


def fmt_num(v, w):
    if v == int(v) and abs(v) < 1e15:
        s = str(int(v))
    else:
        s = ('%.10f' % v).rstrip('0').rstrip('.')
        if s in ('-0', ''):
            s = '0'
    if len(s) <= w - 1:
        return s
    if '.' in s:
        dec = w - 2 - s.index('.')
        if dec >= 0:
            s2 = f'{v:.{dec}f}'
            if len(s2) <= w - 1:
                return s2
    s3 = '%.*e' % (max(0, w - 8), v)
    if len(s3) <= w - 1:
        return s3
    return '#' * max(1, w - 1)


# --------------------------------------------------------------------------
# the application shell: screen, keyboard, menus, Desktop
# --------------------------------------------------------------------------

class App:
    KEYS = None

    def __init__(self, scr, folder, theme):
        self.scr = scr
        self.folder = folder
        self.desktop = []
        self.current = None
        self.msg = ''
        self.mem_kb = 128
        self.bidi_mode = 'terminal' if os.environ.get('TERM_PROGRAM') == 'Apple_Terminal' \
            else 'program'
        self.cursor_at = None
        self.clip_wp = []
        self.clip_wp_dirs = []
        self.clip_wp_sty = []
        self.clip_ss = None
        self.clip_db = None
        self._colors(theme)
        self.clear()

    # ---- screen -----------------------------------------------------------
    def _colors(self, theme):
        base = 0
        if curses.has_colors():
            try:
                curses.start_color()
                fg = {'green': curses.COLOR_GREEN, 'amber': curses.COLOR_YELLOW,
                      'white': curses.COLOR_WHITE}[theme]
                if curses.COLORS >= 256:
                    fg = {'green': 83, 'amber': 214, 'white': 255}[theme]
                curses.init_pair(1, fg, curses.COLOR_BLACK)
                base = curses.color_pair(1)
            except curses.error:
                base = 0
        self.A_N = base
        self.A_B = base | curses.A_BOLD
        self.A_R = base | curses.A_REVERSE
        try:
            self.scr.bkgd(' ', base)
        except curses.error:
            pass

    # The screen is drawn into a buffer of visual cells and sent to the
    # terminal one whole row at a time by flush(), which runs before every
    # key press.  Whole rows matter: a terminal that reorders Hebrew itself
    # looks at the entire line.
    def clear(self):
        H, W = self.scr.getmaxyx()
        self.vchars = [[' '] * W for _ in range(H)]
        self.vattrs = [[self.A_N] * W for _ in range(H)]
        self.scr.erase()

    def put(self, y, x, s, attr=None, base=None):
        """Write text given in logical order.  Hebrew is arranged right-to-left;
        the base direction comes from the first letter unless given."""
        if attr is None:
            attr = self.A_N
        if base == 'R' or RTL_RE.search(s):
            s = bidi_visual(s, base or first_strong(s) or 'L')[0]
        self.put_cells(y, x, s, attr)

    def put_cells(self, y, x, s, attrs):
        """Write characters that are already in visual order."""
        if y < 0 or y >= len(self.vchars):
            return
        row, arow = self.vchars[y], self.vattrs[y]
        for k, ch in enumerate(s):
            xx = x + k
            if 0 <= xx < len(row):
                row[xx] = ch
                arow[xx] = attrs if isinstance(attrs, int) else attrs[k]

    def set_cursor(self, y, x):
        self.cursor_at = (y, x)

    def flush(self):
        H, W = self.scr.getmaxyx()
        term = self.bidi_mode == 'terminal'
        for y in range(min(H, len(self.vchars))):
            row = ''.join(self.vchars[y])[:W]
            attrs = self.vattrs[y][:W]
            if term and RTL_RE.search(row):
                cells, src = terminal_preimage(row)
                attrs = [attrs[i] for i in src]
            else:
                cells = list(row)
            lim = min(len(cells), W - 1 if y == H - 1 else W)
            x = 0
            while x < lim:
                j = x
                while j < lim and attrs[j] == attrs[x]:
                    j += 1
                try:
                    self.scr.addstr(y, x, ''.join(cells[x:j]), attrs[x])
                except curses.error:
                    pass
                x = j
        if self.cursor_at:
            try:
                self.scr.move(*self.cursor_at)
            except curses.error:
                pass

    def frame(self, left, center='', right='Escape: Main Menu', foot='', foot_r=''):
        center, right = T(center), T(right)
        self.clear()
        H, W = self.scr.getmaxyx()
        room = W - (len(right) + 3 if right else 1)
        if len(left) > room:
            left = left[:max(0, room - 1)] + '…'
        self.put(0, 0, left)
        if center:
            self.put(0, max(len(left) + 2, (W - len(center)) // 2), center)
        if right:
            self.put(0, W - len(right) - 1, right)
        self.put(1, 0, '=' * W)
        self.put(H - 2, 0, '=' * W)
        if self.msg:
            self.put(H - 1, 0, T(self.msg), self.A_B)
        else:
            foot, foot_r = T(foot), T(foot_r)
            self.put(H - 1, 0, foot)
            if foot_r:
                self.put(H - 1, W - len(foot_r) - 1, foot_r)

    def too_small(self):
        H, W = self.scr.getmaxyx()
        if H < MIN_H or W < MIN_W:
            self.clear()
            self.put(0, 0, T('RetroWorks needs at least {w}x{h}.', w=MIN_W, h=MIN_H))
            self.put(1, 0, T('This window is {w}x{h}. Please make it larger.', w=W, h=H))
            cursor(0)
            self.flush()
            self.scr.refresh()
            return True
        return False

    def short_folder(self):
        home = os.path.expanduser('~')
        f = self.folder
        return '~' + f[len(home):] if f.startswith(home) else f

    # ---- keyboard ---------------------------------------------------------
    def key(self):
        if App.KEYS is None:
            App.KEYS = {
                curses.KEY_UP: 'UP', curses.KEY_DOWN: 'DOWN', curses.KEY_LEFT: 'LEFT',
                curses.KEY_RIGHT: 'RIGHT', curses.KEY_HOME: 'HOME', curses.KEY_END: 'END',
                curses.KEY_NPAGE: 'PGDN', curses.KEY_PPAGE: 'PGUP',
                curses.KEY_BACKSPACE: 'BKSP', curses.KEY_DC: 'DEL',
                curses.KEY_ENTER: 'ENTER', curses.KEY_RESIZE: 'RESIZE',
                curses.KEY_BTAB: 'BTAB', curses.KEY_F1: 'F1'}
        self.flush()
        try:
            k = self.scr.get_wch()
        except curses.error:
            return None
        except KeyboardInterrupt:
            return 'C-C'
        self.msg = ''
        if isinstance(k, int):
            return App.KEYS.get(k)
        o = ord(k)
        if o in (8, 127):
            return 'BKSP'
        if o in (10, 13):
            return 'ENTER'
        if o == 27:
            return 'ESC'
        if o == 9:
            return 'TAB'
        if o < 32:
            return 'C-' + chr(o + 64)
        if not k.isprintable() or zero_width(k):
            return None
        return k

    def line_edit(self, y, x, width, initial='', exits=('ENTER',), label=''):
        """One-line editor. Returns (text, key); key 'ESC' means cancelled."""
        if label:
            self.put(y, x, label, self.A_B)
            x += len(label)
        width = max(4, width)
        buf, off = list(initial), 0
        pos = len(buf)
        cursor(1)
        while True:
            if pos < off:
                off = pos
            if pos - off >= width:
                off = pos - width + 1
            base = first_strong(''.join(buf)) or ('R' if LANG == 'he' else 'L')
            vis, order, _ = bidi_visual(''.join(buf[off:off + width]).ljust(width), base)
            self.put_cells(y, x, vis, self.A_R)
            li = pos - off
            self.set_cursor(y, x + (order.index(li) if li in order else 0))
            k = self.key()
            if k is None or k == 'RESIZE':
                continue
            if base == 'R' and k in ('LEFT', 'RIGHT'):
                k = 'RIGHT' if k == 'LEFT' else 'LEFT'
            if k == 'ESC':
                return None, 'ESC'
            if k in exits or k == 'ENTER':
                return ''.join(buf), k
            if k == 'LEFT':
                pos = max(0, pos - 1)
            elif k == 'RIGHT':
                pos = min(len(buf), pos + 1)
            elif k in ('HOME', 'C-A', 'C-T'):
                pos = 0
            elif k in ('END', 'C-E', 'C-B'):
                pos = len(buf)
            elif k == 'BKSP':
                if pos > 0:
                    del buf[pos - 1]
                    pos -= 1
            elif k in ('DEL', 'C-D'):
                if pos < len(buf):
                    del buf[pos]
            elif k == 'C-Y':
                del buf[pos:]
            elif len(k) == 1:
                buf.insert(pos, k)
                pos += 1

    def ask(self, label, initial=''):
        label = T(label)
        H, W = self.scr.getmaxyx()
        self.put(H - 1, 0, ' ' * (W - 1))
        text, k = self.line_edit(H - 1, 0, W - len(label) - 2, initial, label=label)
        cursor(0)
        return None if k == 'ESC' else text

    def yesno(self, question, default=False):
        """True / False, or None when Escape is pressed."""
        H, W = self.scr.getmaxyx()
        self.put(H - 1, 0, ' ' * (W - 1))
        yes, no = T('Y'), T('N')
        self.put(H - 1, 0, f'{T(question)}  ({yes}/{no}) [{yes if default else no}]', self.A_B)
        cursor(0)
        while True:
            k = self.key()
            # also accept the Y/N keys while a Hebrew keyboard is active
            if k in ('y', 'Y', yes, '\u05d8'):
                return True
            if k in ('n', 'N', no, '\u05de'):
                return False
            if k == 'ENTER':
                return default
            if k == 'ESC':
                return None

    # ---- menus: stacked "file folder" layers ------------------------------
    def layer_geom(self, d):
        H, W = self.scr.getmaxyx()
        left = 2 + 3 * d
        title_row = 3 + 2 * d
        bottom = min(H - 6 + 2 * d, H - 3)
        right = min(W - 12 + 3 * d, W - 2)
        return left, title_row, bottom, right

    def avail(self):
        used = sum(d.size() for d in self.desktop)
        return max(0, self.mem_kb - PROGRAM_K - used // 1024)

    def draw_stack(self, path, esc='', foot='', foot_r=''):
        """Draw the header, every menu layer in `path` (outermost first) and
        the footer. Returns the geometry (left, title_row, bottom, right) of
        the innermost layer."""
        H, W = self.scr.getmaxyx()
        self.clear()
        grid = [[' '] * W for _ in range(H)]

        def hline(y, x1, x2, keep=False):
            for x in range(max(0, x1), min(W, x2 + 1)):
                if keep and grid[y][x] == '|':
                    continue
                grid[y][x] = '_'

        def clear(y, x1, x2):
            for x in range(max(0, x1), min(W, x2 + 1)):
                grid[y][x] = ' '

        hline(1, 0, W - 1)
        hline(H - 2, 0, W - 1)
        geom = None
        for d, title in enumerate(path):
            L, Tr, B, R = self.layer_geom(d)
            tabr = min(max(L + 27, L + 6 + len(T(title)) + 3), R - 2)
            clear(Tr - 1, L, tabr)
            clear(Tr, L, tabr)
            for y in range(Tr + 1, B + 1):
                clear(y, L, R)
            hline(Tr - 1, L + 1, tabr - 1)
            grid[Tr][L] = '|'
            for j, ch in enumerate(bidi_visual(T(title)[:tabr - L - 7],
                                               first_strong(T(title)) or 'L')[0]):
                grid[Tr][L + 6 + j] = ch
            grid[Tr][tabr] = '|'
            hline(Tr, tabr + 1, R - 1, keep=True)
            for y in range(Tr + 1, B + 1):
                grid[y][L] = '|'
                grid[y][R] = '|'
            hline(B, L + 1, R - 1)
            geom = (L, Tr, B, R)
        for y in range(1, H - 1):
            self.put_cells(y, 0, ''.join(grid[y]), self.A_N)
        disk = T('Disk: {disk}', disk=self.short_folder())
        center = T(path[-1]).upper() if path else ''
        cx = (W - len(center)) // 2
        room = cx - 2 if center else W - len(esc) - 3
        if len(disk) > room:
            disk = disk[:max(0, room - 1)] + '…'
        self.put(0, 0, disk)
        if center:
            self.put(0, cx, center)
        esc = T(esc)
        if esc:
            self.put(0, W - len(esc) - 1, esc)
        if self.msg:
            self.put(H - 1, 0, T(self.msg), self.A_B)
        else:
            foot, foot_r = T(foot), T(foot_r)
            self.put(H - 1, 0, foot)
            if foot_r:
                self.put(H - 1, W - len(foot_r) - 1, foot_r)
        return geom

    def default_esc(self, path):
        return T('Escape: {name}', name=T(path[-2])) if len(path) > 1 else ''

    def select(self, path, items, start=0, esc=None, foot_r=None):
        """Menu in a stacked layer. `items` holds strings (numbered choices),
        ('h', text) / ('H', text) headings, and None for blank lines.
        Returns the index of the chosen string item, or None on Escape."""
        choice_lines = [i for i, it in enumerate(items) if isinstance(it, str)]
        n = len(choice_lines)
        idx = min(max(start, 0), max(n - 1, 0))
        top, digits = 0, ''
        if esc is None:
            esc = self.default_esc(path)
        if foot_r is None:
            foot_r = 'Ctrl-K for Help' if len(path) == 1 else None
        cursor(0)
        while True:
            if self.too_small():
                if self.key() == 'ESC':
                    return None
                continue
            fr = foot_r if foot_r is not None else T('{n}K Avail.', n=self.avail())
            L, Tr, B, R = self.draw_stack(
                path, esc, 'Type number, or use arrows, then press Return', fr)
            ctop, cbot = Tr + 3, max(Tr + 3, B - 1)
            vis = cbot - ctop + 1
            cur_line = choice_lines[idx] if n else 0
            if idx == 0:
                top = 0
            if cur_line < top:
                top = cur_line
            if cur_line >= top + vis:
                top = cur_line - vis + 1
            num = {li: k + 1 for k, li in enumerate(choice_lines)}
            maxw = R - (L + 10) - 1
            for j in range(vis):
                li = top + j
                if li >= len(items):
                    break
                it, y = items[li], ctop + j
                if it is None:
                    continue
                if isinstance(it, tuple):
                    x = L + 10 if it[0] == 'h' else L + 6
                    self.put(y, x, T(it[1])[:R - x - 1])
                    continue
                self.put(y, L + 5, f'{num[li]:>2}.')
                text = T(it)[:maxw]
                if li == cur_line:
                    self.put(y, L + 9, ' ' + text + ' ', self.A_R)
                else:
                    self.put(y, L + 10, text)
            if not n:
                self.put(ctop, L + 10, T('(none)'))
            if top > 0:
                self.put(ctop - 1, R - 4, '▲')
            if top + vis < len(items):
                self.put(B, R - 4, '▼')
            k = self.key()
            if k == 'ESC':
                return None
            if k in ('C-K', 'F1', '?'):
                self.show_text('About RetroWorks', help_text('about', ABOUT))
                continue
            if not n or k is None:
                continue
            if k and len(k) == 1 and k.isdigit():
                digits = (digits + k)[-2:]
                v = int(digits)
                if not 1 <= v <= n:
                    digits, v = k, int(k)
                if 1 <= v <= n:
                    idx = v - 1
                continue
            digits = ''
            if k == 'UP':
                idx = (idx - 1) % n
            elif k == 'DOWN':
                idx = (idx + 1) % n
            elif k == 'PGUP':
                idx = max(0, idx - vis)
            elif k == 'PGDN':
                idx = min(n - 1, idx + vis)
            elif k in ('HOME', 'C-T'):
                idx = 0
            elif k in ('END', 'C-B'):
                idx = n - 1
            elif k == 'ENTER':
                return idx

    def box_prompt(self, path, label, initial='', width=30, esc=None):
        if esc is None:
            esc = self.default_esc(path) or 'Escape: Cancel'
        L, Tr, B, R = self.draw_stack(path, esc, 'Type entry, then press Return.   Escape cancels',
                                      T('{n}K Avail.', n=self.avail()))
        self.put(Tr + 3, L + 6, T(label)[:R - L - 8])
        w = max(8, min(width, R - L - 9))
        text, k = self.line_edit(Tr + 5, L + 6, w, initial)
        cursor(0)
        return None if k == 'ESC' else text

    def show_text(self, title, lines):
        top = 0
        cursor(0)
        while True:
            if self.too_small():
                if self.key() == 'ESC':
                    return
                continue
            H, W = self.scr.getmaxyx()
            vh = H - 4
            top = max(0, min(top, len(lines) - vh))
            self.frame(T(title), '', 'Escape: Go back',
                       T('Press Escape or Return to continue') +
                       (T('   (arrows scroll)') if len(lines) > vh else ''))
            for i, line in enumerate(lines[top:top + vh]):
                self.put(2 + i, 2, line)
            k = self.key()
            if k in ('ESC', 'ENTER', ' ', 'q'):
                return
            if k == 'UP':
                top -= 1
            elif k == 'DOWN':
                top += 1
            elif k == 'PGUP':
                top -= vh
            elif k == 'PGDN':
                top += vh

    def doc_label(self, d):
        return f'{d.name[:24]:<24}  {T(KIND_NAME[d.kind]):<14}' + ('  *' if d.dirty else '')

    def pick_desktop(self, path, esc=None):
        if not self.desktop:
            self.msg = 'There are no files on the Desktop. Use "Add files" first.'
            beep()
            return None
        start = self.desktop.index(self.current) if self.current in self.desktop else 0
        items = [('h', T('Name').ljust(26) + T('Type')), None] + \
            [self.doc_label(d) for d in self.desktop]
        i = self.select(path, items, start, esc=esc)
        return None if i is None else self.desktop[i]

    def quick_change(self):
        return self.pick_desktop(['Desktop Index'],
                                 esc=T('Escape: {name}', name=self.current.name[:20])
                                 if self.current else '')

    # ---- disk -------------------------------------------------------------
    def disk_files(self):
        try:
            names = sorted(os.listdir(self.folder), key=str.lower)
        except OSError:
            return []
        out = []
        for n in names:
            if n.startswith('.'):
                continue
            for k, e in EXT.items():
                if n.endswith(e):
                    out.append((n, k))
        return out

    def file_items(self, files):
        items = []
        for n, k in files:
            try:
                t = datetime.datetime.fromtimestamp(os.path.getmtime(os.path.join(self.folder, n)))
                ts = t.strftime('%d/%m/%y %H:%M')
            except OSError:
                ts = ''
            items.append(f'{n[:-len(EXT[k])][:22]:<22}  {KIND_SHORT[k]}   {ts}')
        return items

    def load_from_disk(self, path):
        files = self.disk_files()
        if not files:
            self.msg = T('There are no RetroWorks files on {disk}', disk=self.short_folder())
            beep()
            return None
        items = [('h', T('Name').ljust(24) + T('Type Modified')), None] + self.file_items(files)
        i = self.select(path + ['Get Files'], items)
        if i is None:
            return None
        fpath = os.path.join(self.folder, files[i][0])
        for d in self.desktop:
            if os.path.abspath(d.path) == os.path.abspath(fpath):
                self.msg = 'That file is already on the Desktop.'
                return d
        try:
            d = load_doc(self, fpath)
        except (OSError, ValueError, KeyError, TypeError) as e:
            self.msg = T('Could not read that file: {err}', err=e)
            beep()
            return None
        self.desktop.append(d)
        return d

    def name_new(self, path, kind, default=''):
        cls = {'wp': WPDoc, 'db': DBDoc, 'ss': SSDoc}[kind]
        while True:
            name = self.box_prompt(path, 'Type a name for this new file:', default, 30)
            if name is None:
                return None
            name = clean(name).strip()[:30]
            if not name:
                return None
            default = name
            if any(d.name.lower() == name.lower() for d in self.desktop):
                self.msg = 'A file with that name is already on the Desktop.'
                beep()
                continue
            d = cls(self, name)
            if os.path.exists(d.path):
                ans = self.yesno(T('"{file}" already exists on disk. Replace it when you save?',
                                   file=os.path.basename(d.path)))
                if ans is None:
                    return None
                if not ans:
                    continue
            return d

    def new_file(self, path, kind):
        d = self.name_new(path, kind)
        if d is None:
            return None
        if kind == 'db':
            fields = self.ask_categories(d.name)
            if not fields:
                return None
            d.fields = fields
            d.widths = [0] * len(fields)
        d.dirty = True
        self.desktop.append(d)
        return d

    def import_file(self, path, kind):
        fn = self.box_prompt(path, 'Type the complete pathname of the text file:',
                             self.folder + os.sep, 60)
        if not fn or not fn.strip():
            return None
        fn = os.path.abspath(os.path.expanduser(fn.strip()))
        try:
            with open(fn, encoding='utf-8', errors='replace', newline='') as f:
                text = f.read()
        except OSError as e:
            self.msg = T('Could not read that file: {err}', err=e.strerror or e)
            beep()
            return None
        base = os.path.splitext(os.path.basename(fn))[0][:30]
        d = self.name_new(path, kind, base)
        if d is None:
            return None
        lines = text.splitlines()
        if kind == 'wp':
            d.paras = [clean(l) for l in lines] or ['']
            d.detect_dirs()
            d.sty = ['0' * len(x) for x in d.paras]
        else:
            rows = [r for r in csv.reader(lines)]
            if not rows:
                self.msg = 'That file is empty.'
                beep()
                return None
            if kind == 'db':
                d.fields = [clean(x).strip()[:30] or T('Category {n}', n=i + 1)
                            for i, x in enumerate(rows[0][:30])]
                nf = len(d.fields)
                d.records = [([clean(v) for v in r] + [''] * nf)[:nf]
                             for r in rows[1:] if any(v.strip() for v in r)]
                d.widths = [0] * nf
            else:
                for r, row in enumerate(rows[:MAXROW]):
                    for c, v in enumerate(row[:26]):
                        v = clean(v).strip()
                        if not v:
                            continue
                        try:
                            float(v)
                        except ValueError:
                            if is_value(v):
                                v = '"' + v
                        d.cells[f'{COLS[c]}{r + 1}'] = v
        d.dirty = True
        self.desktop.append(d)
        return d

    def ask_categories(self, name):
        fields = []
        while True:
            H, W = self.scr.getmaxyx()
            self.frame(T('File: {name}', name=name), 'CHANGE NAME/CATEGORY', 'Escape: Cancel')
            self.put(3, 4, T('Type the names of the categories (fields), one at a time.'), self.A_B)
            self.put(4, 4, T('Press Return on an empty line when you are done.'))
            for j, f in enumerate(fields[-(H - 10):]):
                self.put(6 + j, 6, f)
            t = self.ask(T('Category {n} name: ', n=len(fields) + 1))
            if t is None:
                return None
            t = clean(t).strip()[:30]
            if not t:
                return fields or [T('Category {n}', n=1)]
            if t in fields:
                self.msg = 'You already have a category with that name.'
                beep()
                continue
            fields.append(t)
            if len(fields) >= 30:
                return fields

    # ---- main-menu activities --------------------------------------------
    def new_file_menu(self, path, kind):
        p = path + [KIND_NAME[kind]]
        src = {'wp': 'From a text (ASCII) file',
               'db': 'From a text (ASCII / CSV) file',
               'ss': 'From a text (CSV) file'}[kind]
        sel = 0
        while True:
            i = self.select(p, [('H', 'Make a new file:'), None,
                                'From scratch', None, src], sel)
            if i is None:
                return None
            sel = i
            d = self.new_file(p, kind) if i == 0 else self.import_file(p, kind)
            if d is not None:
                return d

    def add_files(self):
        P = ['Main Menu', 'Add Files']
        sel = 0
        while True:
            if len(self.desktop) >= MAX_DESKTOP:
                self.msg = T('The Desktop is full ({n} files). Remove a file first.', n=MAX_DESKTOP)
                beep()
                return None
            if self.avail() <= 0:
                self.msg = 'The Desktop memory is full. Remove a file or add memory.'
                beep()
                return None
            items = [('h', 'Get files from:'), None,
                     T('The current disk: {disk}', disk=self.short_folder()),
                     'A different disk', None,
                     ('h', 'Make a new file for the:'), None,
                     'Word Processor', 'Data Base', 'Spreadsheet']
            i = self.select(P, items, sel)
            if i is None:
                return None
            sel = i
            d = None
            if i == 0:
                d = self.load_from_disk(P)
            elif i == 1:
                t = self.box_prompt(P + ['A Different Disk'],
                                    'Type the pathname of the disk (folder):', self.folder, 60)
                if not t or not t.strip():
                    continue
                t = os.path.abspath(os.path.expanduser(t.strip()))
                if not os.path.isdir(t):
                    self.msg = T('There is no folder called {path}', path=t)
                    beep()
                    continue
                self.folder = t
                d = self.load_from_disk(P)
            else:
                d = self.new_file_menu(P, ['wp', 'db', 'ss'][i - 2])
            if d is not None:
                return d

    def save_menu(self):
        if not self.desktop:
            self.msg = 'There are no files on the Desktop.'
            return
        items = [('H', 'Save which file?'), None, 'All changed files', None] + \
            [self.doc_label(d) for d in self.desktop]
        i = self.select(['Main Menu', 'Save Files'], items)
        if i is None:
            return
        targets = [d for d in self.desktop if d.dirty] if i == 0 else [self.desktop[i - 1]]
        if not targets:
            self.msg = 'Nothing to save: no file has changed.'
            return
        ok = all([d.do_save() for d in targets])
        if ok and len(targets) > 1:
            self.msg = T('Saved {n} files to {disk}', n=len(targets), disk=self.short_folder())

    def remove_menu(self):
        d = self.pick_desktop(['Main Menu', 'Remove Files'])
        if d is None:
            return
        if d.dirty:
            ans = self.yesno(T('"{name}" has changes. Save it before removing?', name=d.name), True)
            if ans is None or (ans and not d.do_save()):
                return
        self.desktop.remove(d)
        if self.current is d:
            self.current = None
        self.msg = T('"{name}" removed from the Desktop.', name=d.name)

    def other(self):
        P = ['Main Menu', 'Other Activities']
        sel = 0
        while True:
            i = self.select(P, spaced(['Change current disk',
                                       'Delete files from disk',
                                       'Memory size',
                                       'Choose language  (Language / שפה)',
                                       'Right-to-left display',
                                       'About RetroWorks']), sel)
            if i is None:
                return
            sel = i
            if i == 0:
                t = self.box_prompt(P + ['Change Disk'],
                                    'Type the pathname of the new current disk (folder):',
                                    self.folder, 60)
                if t and t.strip():
                    t = os.path.abspath(os.path.expanduser(t.strip()))
                    if not os.path.isdir(t):
                        if not self.yesno(T('"{path}" does not exist. Create it?', path=t), True):
                            continue
                        try:
                            os.makedirs(t)
                        except OSError as e:
                            self.msg = T('Could not create folder: {err}', err=e)
                            beep()
                            continue
                    self.folder = t
                    self.msg = T('Current disk is now {disk}', disk=self.short_folder())
            elif i == 1:
                files = self.disk_files()
                if not files:
                    self.msg = T('There are no RetroWorks files on {disk}', disk=self.short_folder())
                    continue
                items = [('h', T('Name').ljust(24) + T('Type Modified')), None] + \
                    self.file_items(files)
                j = self.select(P + ['Delete Files'], items)
                if j is None:
                    continue
                fpath = os.path.join(self.folder, files[j][0])
                if any(os.path.abspath(d.path) == os.path.abspath(fpath) for d in self.desktop):
                    self.msg = 'That file is on the Desktop. Remove it from the Desktop first.'
                    beep()
                    continue
                if self.yesno(T('Permanently delete "{file}" from the disk?', file=files[j][0])):
                    try:
                        os.remove(fpath)
                        self.msg = T('Deleted {file}', file=files[j][0])
                    except OSError as e:
                        self.msg = T('Could not delete: {err}', err=e)
                        beep()
            elif i == 2:
                self.memory_menu(P)
            elif i == 3:
                self.language_menu(P)
            elif i == 4:
                self.bidi_menu(P)
            elif i == 5:
                self.show_text('About RetroWorks', help_text('about', ABOUT))

    def memory_menu(self, path):
        sizes = [kb for _, kb in MEMORY_SIZES]
        items = [('h', 'How much memory does this computer have?'), None]
        items += [f"{name:<22}{T('{n}K for the Desktop', n=kb - PROGRAM_K):<24}"
                  f"{'*' if kb == self.mem_kb else ''}" for name, kb in MEMORY_SIZES]
        i = self.select(path + ['Memory Size'], items,
                        sizes.index(self.mem_kb) if self.mem_kb in sizes else 0)
        if i is None:
            return
        self.mem_kb = sizes[i]
        cfg = load_config()
        cfg['memory'] = self.mem_kb
        save_config(cfg)
        self.msg = T('Memory: {name}  ({n}K available)', name=MEMORY_SIZES[i][0], n=self.avail())

    def bidi_menu(self, path):
        modes = ['terminal', 'program']
        items = [('h', 'Who arranges right-to-left text on the screen?'), None,
                 T('The terminal (macOS Terminal)') + ('  *' if self.bidi_mode == 'terminal' else ''),
                 T('RetroWorks (Windows Terminal, iTerm2, others)') +
                 ('  *' if self.bidi_mode == 'program' else ''),
                 None,
                 ('h', 'Pick the one that makes this line read correctly:'),
                 ('h', '\u05e9\u05dc\u05d5\u05dd! \u05d6\u05d4 \u05e0\u05d9\u05e1\u05d9\u05d5\u05df '
                       '\u05e2\u05dd English \u05d5-1+2.')]
        i = self.select(path + ['Right-to-left'], items, modes.index(self.bidi_mode))
        if i is None:
            return
        self.bidi_mode = modes[i]
        cfg = load_config()
        cfg['bidi'] = self.bidi_mode
        save_config(cfg)

    def language_menu(self, path):
        codes = sorted(LANGUAGES, key=lambda c: (c != 'en', LANGUAGES[c]['name'].lower()))
        items = [('h', 'Choose the language for menus and messages:'), None]
        items += [f"{LANGUAGES[c]['name']:<20}{'  *' if c == LANG else ''}" for c in codes]
        i = self.select(path + ['Language'], items,
                        codes.index(LANG) if LANG in codes else 0)
        if i is None:
            return
        set_language(codes[i])
        cfg = load_config()
        cfg['language'] = LANG
        save_config(cfg)
        self.msg = T('Language: {name}', name=LANGUAGES[LANG]['name'])

    def quit(self):
        had_changes = False
        for d in list(self.desktop):
            if d.dirty:
                had_changes = True
                ans = self.yesno(T('Save changes to "{name}" before quitting?', name=d.name), True)
                if ans is None:
                    return False
                if ans and not d.do_save():
                    return False
        if had_changes:
            return True
        return bool(self.yesno('Do you really want to quit?', False))

    def work(self, doc):
        while doc is not None:
            self.current = doc
            res = doc.run()
            doc = res if isinstance(res, Doc) else None
        cursor(0)

    def splash(self):
        cursor(0)
        while True:
            if self.too_small():
                self.key()
                continue
            H, W = self.scr.getmaxyx()
            self.clear()
            bw = 56
            x = (W - bw) // 2
            y = max(1, (H - 16) // 2)
            self.put(y, x, '╔' + '═' * (bw - 2) + '╗', self.A_B)
            for j in range(1, 6):
                self.put(y + j, x, '║' + ' ' * (bw - 2) + '║', self.A_B)
            self.put(y + 6, x, '╚' + '═' * (bw - 2) + '╝', self.A_B)
            t1 = 'R E T R O W O R K S   ] ['
            t2 = T('Word Processor  ·  Data Base  ·  Spreadsheet')
            self.put(y + 2, x + (bw - len(t1)) // 2, t1, self.A_B)
            self.put(y + 4, x + (bw - len(t2)) // 2, t2)
            today = datetime.date.today()
            date = today.strftime('%A, %d %B %Y') if LANG == 'en' else today.strftime('%d/%m/%Y')
            lines = [
                T('Version {v}', v=VERSION),
                T('An original tribute to the integrated software of the 1980s.'),
                T('Not affiliated with or endorsed by Apple Inc.'),
                '',
                T("Today's date: {date}", date=date),
                '',
                T('Ctrl takes the place of the Open-Apple key.'),
                T('Press Return to continue'),
            ]
            for j, line in enumerate(lines):
                self.put(y + 8 + j, (W - len(line)) // 2, line,
                         self.A_B if j == len(lines) - 1 else self.A_N)
            k = self.key()
            if k not in (None, 'RESIZE'):
                return

    def main_menu(self):
        items = spaced(['Add files to the Desktop',
                        'Work with one of the files on the Desktop',
                        'Save Desktop files to disk',
                        'Remove files from the Desktop',
                        'Other Activities',
                        'Quit'])
        sel = 0
        if self.desktop:
            self.work(self.desktop[0])
        while True:
            esc = T('Escape: {name}', name=self.current.name[:20]) \
                if self.current in self.desktop else ''
            i = self.select(['Main Menu'], items, sel, esc=esc)
            if i is None:
                if self.current in self.desktop:
                    self.work(self.current)
                continue
            sel = i
            if i == 0:
                d = self.add_files()
                if d is not None:
                    self.work(d)
            elif i == 1:
                d = self.pick_desktop(['Main Menu', 'Desktop Index'])
                if d is not None:
                    self.work(d)
            elif i == 2:
                self.save_menu()
            elif i == 3:
                self.remove_menu()
            elif i == 4:
                self.other()
            elif i == 5:
                if self.quit():
                    return


def spaced(items):
    """Double-space a list of menu choices (blank line between each)."""
    out = []
    for it in items:
        if out:
            out.append(None)
        out.append(it)
    return out


ABOUT = [
    f'RetroWorks ][   version {VERSION}',
    '',
    'An integrated Word Processor, Data Base and Spreadsheet in the style of',
    'the 80-column, green-screen productivity software of the Apple ][ years.',
    '',
    'Written from scratch in Python. It is not AppleWorks, contains no Apple',
    'code, and is not affiliated with or endorsed by Apple Inc.',
    '',
    'The Desktop holds up to 12 open files. Escape always takes you back to',
    'the Main Menu; Ctrl-Q ("Quick Change") jumps straight to another file.',
    '',
    'Files are plain JSON:  .rwp (word processor)  .rdb (data base)',
    '                       .rss (spreadsheet)',
    '',
    'Because macOS keeps the Command key for itself, every "Open-Apple"',
    'command is typed with Ctrl instead.  Ctrl-K shows the help for',
    'whichever kind of file you are working on.',
]


# --------------------------------------------------------------------------
# documents
# --------------------------------------------------------------------------

class Doc:
    kind = ''
    HELP = []

    def __init__(self, app, name, path=None):
        self.app = app
        self.name = name
        self.path = path or os.path.join(app.folder, safe_name(name) + EXT[self.kind])
        self.dirty = False

    def title(self):
        return T('File: {name}', name=self.name) + (' *' if self.dirty else '')

    def to_json(self):
        return {}

    def size(self):
        return 0

    def from_json(self, d):
        pass

    def save(self):
        data = {'app': 'RetroWorks', 'version': 1, 'type': self.kind}
        data.update(self.to_json())
        tmp = self.path + '.tmp'
        with open(tmp, 'w', encoding='utf-8') as f:
            json.dump(data, f, ensure_ascii=False, indent=1)
        os.replace(tmp, self.path)
        self.dirty = False

    def do_save(self):
        try:
            self.save()
            self.app.msg = T('Saved "{name}" to {file}', name=self.name,
                             file=os.path.basename(self.path))
            return True
        except OSError as e:
            self.app.msg = T('Could not save: {err}', err=e)
            beep()
            return False

    def run(self):
        a = self.app
        while True:
            if a.too_small():
                if a.key() == 'ESC':
                    return 'MENU'
                continue
            self.draw()
            k = a.key()
            if k is None or k == 'RESIZE':
                continue
            if k == 'ESC':
                cursor(0)
                return 'MENU'
            if k == 'C-Q':
                d = a.quick_change()
                if d is not None and d is not self:
                    return d
                continue
            if k == 'C-S':
                self.do_save()
                continue
            if k in ('C-K', 'F1'):
                a.show_text(T('{kind} Help', kind=T(KIND_NAME[self.kind])),
                            help_text(self.kind, self.HELP))
                continue
            self.handle(k)

    def draw(self):
        pass

    def handle(self, k):
        pass

    def export_path(self, ext, label):
        default = os.path.join(self.app.folder, safe_name(self.name) + ext)
        fn = self.app.ask(T(label), default)
        if not fn or not fn.strip():
            return None
        return os.path.abspath(os.path.expanduser(fn.strip()))


def load_doc(app, path):
    with open(path, encoding='utf-8') as f:
        data = json.load(f)
    kind = data.get('type')
    cls = {'wp': WPDoc, 'db': DBDoc, 'ss': SSDoc}[kind]
    base = os.path.basename(path)
    name = base[:-len(EXT[kind])] if base.endswith(EXT[kind]) else base
    d = cls(app, name, path)
    d.from_json(data)
    return d


# ---- Word Processor ------------------------------------------------------

class WPDoc(Doc):
    kind = 'wp'
    HELP = [
        'WORD PROCESSOR COMMANDS          (Ctrl replaces the Open-Apple key)',
        '',
        'Arrows ............ move the cursor     Ctrl-T / Ctrl-G .. top / bottom',
        'PgUp / PgDn ....... page up / down      Home / End ....... start / end of line',
        'Return ............ new paragraph       Ctrl-E ........... Insert / Overwrite',
        'Delete ............ delete left         Ctrl-D ........... delete right',
        'Tab ............... 4 spaces            Ctrl-Y ........... clear to end of paragraph',
        '',
        'Ctrl-X ............ cut paragraph       Ctrl-C ........... copy paragraph',
        'Ctrl-V ............ paste paragraph(s) above the cursor',
        '',
        'Ctrl-F ............ find (again)        Ctrl-R ........... replace all',
        'Ctrl-P ............ "print" to a plain .txt file (76 columns)',
        'Ctrl-L ............ paragraph direction: left-to-right / right-to-left',
        'Ctrl-B ............ bold on / off       Ctrl-U ........... underline on / off',
        '',
        'Ctrl-S ............ save                Ctrl-Q ........... quick change',
        'Escape ............ Main Menu           Ctrl-K ........... this help',
        '',
        'Bold and underline apply to what you type next; press the key again',
        'to turn them off. Printing to a .txt file keeps the text only.',
        '',
        'Text wraps automatically. The bottom line shows the line, column,',
        'cursor mode, word count and the direction of the current paragraph.',
        'A right-to-left paragraph is aligned to the right margin; new',
        'paragraphs keep the direction of the one you pressed Return in.',
    ]

    def __init__(self, app, name, path=None):
        super().__init__(app, name, path)
        self.paras = ['']
        self.dirs = [self.default_dir()]
        # per-character style, one digit per character: 0 plain, 1 bold,
        # 2 underline, 3 both
        self.sty = ['']
        self.style = 0
        self.pi = self.ci = self.top = 0
        self.goal = None
        self.insert = True
        self.last_find = ''

    @staticmethod
    def default_dir():
        return 'R' if LANG == 'he' else 'L'

    def detect_dirs(self):
        """Give each paragraph the direction of its first letter."""
        self.dirs = [first_strong(p) or self.default_dir() for p in self.paras]

    def fix_dirs(self):
        n = len(self.paras)
        if len(self.dirs) != n:
            self.dirs = (self.dirs + [self.default_dir()] * n)[:n]
        if len(self.sty) != n:
            self.sty = (self.sty + [''] * n)[:n]
        for i, p in enumerate(self.paras):
            if len(self.sty[i]) != len(p):
                self.sty[i] = (self.sty[i] + '0' * len(p))[:len(p)]

    def to_json(self):
        self.fix_dirs()
        data = {'paras': self.paras, 'dirs': ''.join(self.dirs)}
        if any(st.strip('0') for st in self.sty):
            data['styles'] = self.sty
        return data

    def size(self):
        return sum(len(p) + 1 for p in self.paras)

    def from_json(self, d):
        self.paras = [clean(p) for p in d.get('paras', [])] or ['']
        dirs = d.get('dirs')
        if isinstance(dirs, str) and len(dirs) == len(self.paras) and set(dirs) <= {'L', 'R'}:
            self.dirs = list(dirs)
        else:
            self.detect_dirs()
        sty = d.get('styles')
        self.sty = [''] * len(self.paras)
        if isinstance(sty, list) and len(sty) == len(self.paras):
            self.sty = [st if isinstance(st, str) and set(st) <= set('0123') else ''
                        for st in sty]
        self.fix_dirs()

    def geometry(self):
        H, W = self.app.scr.getmaxyx()
        ww = min(76, W - 4)
        return H, W, ww, max(2, (W - ww) // 2)

    def layout(self, ww):
        rows = []
        for pi, p in enumerate(self.paras):
            st = wrap_starts(p, ww)
            for r, s in enumerate(st):
                last = r == len(st) - 1
                rows.append((pi, s, len(p) if last else st[r + 1], last))
        return rows

    def crow(self, rows):
        for i, (pi, s, e, last) in enumerate(rows):
            if pi == self.pi and s <= self.ci and (self.ci < e or last):
                return i
        return 0

    def draw(self):
        a = self.app
        H, W, ww, x0 = self.geometry()
        rows = self.layout(ww)
        cr = self.crow(rows)
        vh = H - 4
        self.top = min(self.top, cr)
        if cr >= self.top + vh:
            self.top = cr - vh + 1
        self.fix_dirs()
        col = self.ci - rows[cr][1]
        words = sum(len(p.split()) for p in self.paras)
        rtl = self.dirs[self.pi] == 'R'
        a.frame(self.title(), 'REVIEW/ADD/CHANGE', 'Escape: Main Menu', 'Ctrl-K: Help',
                '   '.join([T('Insert' if self.insert else 'Overwrite')] +
                           ([T('Bold')] if self.style & 1 else []) +
                           ([T('Underline')] if self.style & 2 else []) + [
                            T('Right-to-left' if rtl else 'Left-to-right'),
                            T('Line {a} of {b}', a=cr + 1, b=len(rows)),
                            T('Column {n}', n=col + 1),
                            T('{n} words', n=words)]))
        ruler = ['='] * W
        for k in range(0, ww + 1, 5):
            if x0 + k < W:
                ruler[x0 + k] = '|'
        a.put(1, 0, ''.join(ruler))
        for i in range(vh):
            r = self.top + i
            if r >= len(rows):
                break
            self.draw_row(2 + i, rows[r], ww, x0)
        cursor(1 if self.insert else 2)
        seg, vis, order = self.row_visual(rows[cr], ww)
        li = self.ci - rows[cr][1]
        cx = x0 + (order.index(li) if li in order else 0)
        a.set_cursor(2 + cr - self.top, max(0, min(W - 1, cx)))

    def row_visual(self, row, ww):
        """A wrapped row arranged for the screen.  It is padded to the full
        width, so a right-to-left paragraph lines up with the right margin."""
        pi, s, e, last = row
        seg = self.paras[pi][s:e]
        vis, order, _ = bidi_visual(seg + ' ' * max(1, ww + 1 - len(seg)), self.dirs[pi])
        return seg, vis, order

    def draw_row(self, y, row, ww, x0):
        a = self.app
        pi, s, e, last = row
        seg, vis, order = self.row_visual(row, ww)
        st = self.sty[pi]
        attrs = []
        for li in order:
            code = st[s + li] if li < len(seg) and s + li < len(st) else '0'
            code = int(code) if code.isdigit() else 0
            attr = a.A_N
            if code & 1:
                attr |= curses.A_BOLD
            if code & 2:
                attr |= curses.A_UNDERLINE
            attrs.append(attr)
        a.put_cells(y, x0, vis, attrs)

    def handle(self, k):
        a = self.app
        H, W, ww, x0 = self.geometry()
        self.fix_dirs()
        p = self.paras[self.pi]
        st = self.sty[self.pi]
        if k not in ('UP', 'DOWN', 'PGUP', 'PGDN'):
            self.goal = None
        if self.dirs[self.pi] == 'R' and k in ('LEFT', 'RIGHT'):
            k = 'RIGHT' if k == 'LEFT' else 'LEFT'   # left arrow moves visually left
        if len(k) == 1 or k == 'TAB':
            s = k if len(k) == 1 else '    '
            code = str(self.style) * len(s)
            if self.insert or self.ci >= len(p):
                p = p[:self.ci] + s + p[self.ci:]
                st = st[:self.ci] + code + st[self.ci:]
            else:
                p = p[:self.ci] + s + p[self.ci + len(s):]
                st = st[:self.ci] + code + st[self.ci + len(s):]
            self.paras[self.pi] = p
            self.sty[self.pi] = st
            self.ci += len(s)
            self.dirty = True
        elif k == 'ENTER':
            self.fix_dirs()
            self.paras[self.pi] = p[:self.ci]
            self.paras.insert(self.pi + 1, p[self.ci:])
            self.dirs.insert(self.pi + 1, self.dirs[self.pi])
            self.sty[self.pi] = st[:self.ci]
            self.sty.insert(self.pi + 1, st[self.ci:])
            self.pi += 1
            self.ci = 0
            self.dirty = True
        elif k == 'BKSP':
            if self.ci > 0:
                self.paras[self.pi] = p[:self.ci - 1] + p[self.ci:]
                self.sty[self.pi] = st[:self.ci - 1] + st[self.ci:]
                self.ci -= 1
                self.dirty = True
            elif self.pi > 0:
                prev = self.paras[self.pi - 1]
                self.ci = len(prev)
                self.paras[self.pi - 1] = prev + p
                self.sty[self.pi - 1] += st
                del self.paras[self.pi]
                del self.dirs[self.pi]
                del self.sty[self.pi]
                self.pi -= 1
                self.dirty = True
        elif k in ('DEL', 'C-D'):
            if self.ci < len(p):
                self.paras[self.pi] = p[:self.ci] + p[self.ci + 1:]
                self.sty[self.pi] = st[:self.ci] + st[self.ci + 1:]
                self.dirty = True
            elif self.pi < len(self.paras) - 1:
                self.paras[self.pi] = p + self.paras[self.pi + 1]
                self.sty[self.pi] = st + self.sty[self.pi + 1]
                del self.paras[self.pi + 1]
                del self.dirs[self.pi + 1]
                del self.sty[self.pi + 1]
                self.dirty = True
        elif k == 'LEFT':
            if self.ci > 0:
                self.ci -= 1
            elif self.pi > 0:
                self.pi -= 1
                self.ci = len(self.paras[self.pi])
        elif k == 'RIGHT':
            if self.ci < len(p):
                self.ci += 1
            elif self.pi < len(self.paras) - 1:
                self.pi += 1
                self.ci = 0
        elif k in ('UP', 'DOWN', 'PGUP', 'PGDN', 'HOME', 'END'):
            rows = self.layout(ww)
            cr = self.crow(rows)
            pi, s, e, last = rows[cr]
            if k == 'HOME':
                self.ci = s
            elif k == 'END':
                self.ci = e if last else max(s, e - 1)
            else:
                if self.goal is None:
                    self.goal = self.ci - s
                step = {'UP': -1, 'DOWN': 1, 'PGUP': -(H - 5), 'PGDN': H - 5}[k]
                t = max(0, min(len(rows) - 1, cr + step))
                if k in ('PGUP', 'PGDN'):
                    self.top = max(0, self.top + step)
                pi, s, e, last = rows[t]
                mx = e if last else max(s, e - 1)
                self.pi = pi
                self.ci = min(s + self.goal, mx)
        elif k == 'C-T':
            self.pi = self.ci = 0
        elif k == 'C-G':
            self.pi = len(self.paras) - 1
            self.ci = len(self.paras[-1])
        elif k == 'C-E':
            self.insert = not self.insert
        elif k == 'C-Y':
            if self.ci < len(p):
                self.paras[self.pi] = p[:self.ci]
                self.sty[self.pi] = st[:self.ci]
                self.dirty = True
        elif k == 'C-X':
            a.clip_wp, a.clip_wp_dirs, a.clip_wp_sty = [p], [self.dirs[self.pi]], [st]
            if len(self.paras) > 1:
                del self.paras[self.pi]
                del self.dirs[self.pi]
                del self.sty[self.pi]
                self.pi = min(self.pi, len(self.paras) - 1)
            else:
                self.paras = ['']
                self.sty = ['']
            self.ci = 0
            self.dirty = True
            a.msg = 'Paragraph cut to the clipboard.'
        elif k == 'C-C':
            a.clip_wp, a.clip_wp_dirs, a.clip_wp_sty = [p], [self.dirs[self.pi]], [st]
            a.msg = 'Paragraph copied to the clipboard.'
        elif k == 'C-V':
            if not a.clip_wp:
                a.msg = 'The clipboard is empty.'
                beep()
            else:
                self.paras[self.pi:self.pi] = list(a.clip_wp)
                dirs = (list(a.clip_wp_dirs) + [self.default_dir()] * len(a.clip_wp))
                self.dirs[self.pi:self.pi] = dirs[:len(a.clip_wp)]
                stys = list(a.clip_wp_sty) + [''] * len(a.clip_wp)
                self.sty[self.pi:self.pi] = stys[:len(a.clip_wp)]
                self.fix_dirs()
                self.pi += len(a.clip_wp)
                self.dirty = True
                a.msg = 'Pasted above the cursor.'
        elif k == 'C-B':
            self.style ^= 1
            a.msg = 'Bold on.' if self.style & 1 else 'Bold off.'
        elif k == 'C-U':
            self.style ^= 2
            a.msg = 'Underline on.' if self.style & 2 else 'Underline off.'
        elif k == 'C-L':
            self.dirs[self.pi] = 'L' if self.dirs[self.pi] == 'R' else 'R'
            self.dirty = True
            a.msg = 'Paragraph is now right-to-left.' if self.dirs[self.pi] == 'R' \
                else 'Paragraph is now left-to-right.'
        elif k == 'C-F':
            self.find()
        elif k == 'C-R':
            self.replace()
        elif k == 'C-P':
            self.export()
        else:
            beep()

    def find(self):
        a = self.app
        q = a.ask('Find: ', self.last_find)
        if not q:
            return
        self.last_find = q
        ql, n = q.lower(), len(self.paras)
        for step in range(n + 1):
            pi = (self.pi + step) % n
            p = self.paras[pi].lower()
            if step == 0:
                j = p.find(ql, self.ci + 1)
            else:
                j = p.find(ql)
                if step == n and j > self.ci:
                    j = -1
            if j >= 0:
                self.pi, self.ci = pi, j
                return
        a.msg = T('"{text}" was not found.', text=q)
        beep()

    def replace(self):
        a = self.app
        q = a.ask('Replace: ', self.last_find)
        if not q:
            return
        r = a.ask(T('Replace "{text}" with: ', text=q))
        if r is None:
            return
        total = 0
        rx = re.compile(re.escape(q), re.I)
        self.fix_dirs()
        for i, p in enumerate(self.paras):
            st = self.sty[i]
            out, ost, last = [], [], 0
            for m in rx.finditer(p):
                out.append(p[last:m.start()] + r)
                ost.append(st[last:m.start()] + st[m.start()] * len(r))
                last = m.end()
                total += 1
            if out:
                self.paras[i] = ''.join(out) + p[last:]
                self.sty[i] = ''.join(ost) + st[last:]
        self.ci = min(self.ci, len(self.paras[self.pi]))
        if total:
            self.dirty = True
        a.msg = T('Replaced {n} occurrence(s).', n=total)

    def export(self):
        fn = self.export_path('.txt', 'Print to text file: ')
        if not fn:
            return
        lines = []
        for p in self.paras:
            lines.extend(textwrap.wrap(p, 76, replace_whitespace=False) or [''])
        try:
            with open(fn, 'w', encoding='utf-8') as f:
                f.write('\n'.join(lines) + '\n')
            self.app.msg = T('Printed {n} lines to {file}', n=len(lines), file=os.path.basename(fn))
        except OSError as e:
            self.app.msg = T('Could not write file: {err}', err=e)
            beep()


# ---- Spreadsheet ---------------------------------------------------------

class SSDoc(Doc):
    kind = 'ss'
    HELP = [
        'SPREADSHEET COMMANDS             (Ctrl replaces the Open-Apple key)',
        '',
        'Arrows / Tab ...... move              PgUp / PgDn ...... page',
        'Ctrl-T ............ go to A1          Ctrl-B ........... last used cell',
        'Ctrl-G ............ go to a cell      Ctrl-F ........... find text',
        '',
        'Just start typing to make an entry; Return, Tab or an arrow enters it.',
        'Return / Ctrl-U ... edit the current entry      Delete .... blank the cell',
        '',
        'VALUES start with a digit or  + - . = @ (   for example:',
        '   +A1*B1        =(B2+B3)/2        @SUM(A1...A10)     @AVG(B1:B9)',
        'LABELS are anything else. Start with " to force a label ("1984).',
        'A label that starts with \\ repeats across the cell:  \\-   \\=',
        '',
        'Functions: @SUM @AVG @MIN @MAX @COUNT @ABS @INT @ROUND(x,n) @SQRT',
        '           @MOD(x,y) @IF(cond,a,b) @EXP @LN @LOG @SIN @COS @TAN @ATAN @PI',
        'Operators: + - * / ^ and comparisons < > = <= >= <> (true = 1)',
        '$A$1 is an absolute reference; A1 is relative.',
        '',
        'Ctrl-C / Ctrl-X / Ctrl-V .. copy / cut / paste a cell (formulas adjust)',
        'Ctrl-R ............ replicate the cell down/across to a target cell',
        'Ctrl-N ............ insert a row      Ctrl-D ........... delete the row',
        'Ctrl-W ............ column width      Ctrl-P ........... export values to CSV',
        'Ctrl-S ............ save              Ctrl-Q ........... quick change',
        'Escape ............ Main Menu         Ctrl-K ........... this help',
    ]

    def __init__(self, app, name, path=None):
        super().__init__(app, name, path)
        self.cells = {}
        self.widths = {}
        self.r, self.c = 1, 0
        self.top, self.left = 1, 0
        self.cache = {}
        self.stack = set()
        self.last_find = ''

    def to_json(self):
        return {'cells': self.cells, 'widths': self.widths}

    def size(self):
        return sum(len(k) + len(v) + 2 for k, v in self.cells.items())

    def from_json(self, d):
        self.cells = {}
        for k, v in d.get('cells', {}).items():
            try:
                split_ref(k)
            except SSError:
                continue
            v = clean(v)
            if v:
                self.cells[k] = v
        self.widths = {k: max(3, min(60, int(v))) for k, v in d.get('widths', {}).items()
                       if k in COLS and len(k) == 1}

    # -- evaluation --
    def value(self, ref):
        if ref in self.cache:
            v = self.cache[ref]
            if isinstance(v, SSError):
                raise v
            return v
        raw = self.cells.get(ref)
        if not raw:
            return None
        if not is_value(raw):
            v = raw[1:] if raw.startswith('"') else raw
            self.cache[ref] = v
            return v
        if ref in self.stack:
            raise SSError('circular reference')
        self.stack.add(ref)
        try:
            v = Parser(self, tokenize(raw[1:] if raw.startswith('=') else raw)).parse()
            if math.isnan(v) or math.isinf(v):
                raise SSError('number out of range')
            self.cache[ref] = v
            return v
        except SSError as e:
            self.cache[ref] = e
            raise
        except RecursionError:
            e = SSError('formula chain too deep')
            self.cache[ref] = e
            raise e
        finally:
            self.stack.discard(ref)

    def num(self, ref):
        split_ref(ref)
        v = self.value(ref)
        return v if isinstance(v, float) else 0.0

    def range_vals(self, a, b):
        c1, r1 = split_ref(a)
        c2, r2 = split_ref(b)
        out = []
        for c in range(min(c1, c2), max(c1, c2) + 1):
            for r in range(min(r1, r2), max(r1, r2) + 1):
                v = self.value(f'{COLS[c]}{r}')
                if isinstance(v, float):
                    out.append(v)
        return out

    def display(self, ref, w):
        raw = self.cells.get(ref)
        if not raw:
            return '', 'blank'
        try:
            v = self.value(ref)
        except SSError:
            return 'ERROR', 'num'
        if isinstance(v, float):
            return fmt_num(v, w), 'num'
        if raw.startswith('\\') and len(raw) > 1:
            return (raw[1:] * w)[:w], 'repeat'
        return v, 'label'

    # -- geometry --
    def ref(self):
        return f'{COLS[self.c]}{self.r}'

    def width(self, c):
        return self.widths.get(COLS[c], 9)

    def vh(self):
        return self.app.scr.getmaxyx()[0] - 6

    def visible_cols(self, W):
        cols, x, c = [], 5, self.left
        while c < 26 and x + self.width(c) <= W:
            cols.append((c, x))
            x += self.width(c)
            c += 1
        return cols

    def move(self, dr, dc):
        self.r = max(1, min(MAXROW, self.r + dr))
        self.c = max(0, min(25, self.c + dc))

    def draw(self):
        a = self.app
        H, W = a.scr.getmaxyx()
        self.cache = {}
        vh = self.vh()
        if self.r < self.top:
            self.top = self.r
        if self.r >= self.top + vh:
            self.top = self.r - vh + 1
        if self.c < self.left:
            self.left = self.c
        cols = self.visible_cols(W)
        while cols and self.c > cols[-1][0]:
            self.left += 1
            cols = self.visible_cols(W)
        ref = self.ref()
        raw = self.cells.get(ref, '')
        a.frame(self.title(), 'REVIEW/ADD/CHANGE', 'Escape: Main Menu',
                'Type entry or use Ctrl commands        Ctrl-K: Help')
        a.put(H - 3, 0, '=' * W)
        a.put(H - 2, 0, ' ' * W)
        kind = '' if not raw else (T('(Value) ') if is_value(raw) else T('(Label) '))
        a.put(H - 2, 0, f'{ref}: {kind}{raw}'[:W - 22], a.A_B)
        if raw and is_value(raw):
            try:
                vs = '= ' + fmt_num(self.value(ref), 19)
            except SSError as e:
                vs = T('ERROR: ') + T(str(e))
            a.put(H - 2, max(W - len(vs) - 1, W - 40), vs[:39])
        a.put(2, 0, '    ', a.A_B)
        for c, x in cols:
            w = self.width(c)
            a.put(2, x, COLS[c].center(w), a.A_R if c == self.c else a.A_B)
        for i in range(vh):
            r = self.top + i
            if r > MAXROW:
                break
            a.put(3 + i, 0, f'{r:>4}', a.A_R if r == self.r else a.A_B)
            carry = ''
            for c, x in cols:
                w = self.width(c)
                text, kind = self.display(f'{COLS[c]}{r}', w)
                if kind == 'blank':
                    seg, carry = carry[:w].ljust(w), carry[w:]
                elif kind == 'num':
                    seg, carry = (text.rjust(w - 1) + ' ')[:w], ''
                elif kind == 'repeat':
                    seg, carry = text, ''
                elif first_strong(text) == 'R':
                    seg, carry = text[:w].ljust(w), ''    # right-to-left label: right-aligned
                else:
                    seg, carry = text[:w].ljust(w), text[w:]
                cur = r == self.r and c == self.c
                a.put(3 + i, x, seg, a.A_R if cur else a.A_N)
        cursor(0)

    # -- editing --
    def set_cell(self, ref, text):
        text = clean(text).strip()
        old = self.cells.get(ref)
        if text:
            self.cells[ref] = text
        else:
            self.cells.pop(ref, None)
        if old != (text or None):
            self.dirty = True
        self.cache = {}
        if text and is_value(text):
            try:
                self.value(ref)
            except SSError as e:
                self.app.msg = T('{ref}: ERROR - {err}', ref=ref, err=T(str(e)))
                beep()

    def edit(self, first):
        a = self.app
        H, W = a.scr.getmaxyx()
        ref = self.ref()
        init = first if first is not None else self.cells.get(ref, '')
        a.put(H - 1, 0, ' ' * (W - 1))
        text, k = a.line_edit(H - 1, 0, W - len(ref) - 4, init,
                              exits=('ENTER', 'UP', 'DOWN', 'TAB', 'BTAB'), label=ref + ': ')
        cursor(0)
        if k == 'ESC':
            return
        self.set_cell(ref, text)
        self.move(*{'ENTER': (1, 0), 'DOWN': (1, 0), 'UP': (-1, 0),
                    'TAB': (0, 1), 'BTAB': (0, -1)}[k])

    def used_extent(self):
        if not self.cells:
            return 1, 0
        refs = [split_ref(k) for k in self.cells]
        return max(r for c, r in refs), max(c for c, r in refs)

    def shift_rows(self, at, delta):
        new = {}
        for ref, raw in self.cells.items():
            c, r = split_ref(ref)
            if delta < 0 and r == at:
                continue
            if delta > 0 and r >= at:
                r += 1
            elif delta < 0 and r > at:
                r -= 1
            if r > MAXROW:
                continue
            new[f'{COLS[c]}{r}'] = fix_rows(raw, at, delta) if is_value(raw) else raw
        self.cells = new
        self.dirty = True
        self.cache = {}

    def handle(self, k):
        a = self.app
        if len(k) == 1:
            self.edit(k)
            return
        if k in ('ENTER', 'C-U'):
            self.edit(None)
            return
        moves = {'UP': (-1, 0), 'DOWN': (1, 0), 'LEFT': (0, -1), 'RIGHT': (0, 1),
                 'TAB': (0, 1), 'BTAB': (0, -1),
                 'PGUP': (-self.vh(), 0), 'PGDN': (self.vh(), 0)}
        ref = self.ref()
        raw = self.cells.get(ref)
        if k in moves:
            self.move(*moves[k])
        elif k in ('HOME', 'C-T'):
            self.r, self.c = 1, 0
        elif k in ('END', 'C-B'):
            self.r, self.c = self.used_extent()
        elif k in ('BKSP', 'DEL', 'C-Y'):
            self.set_cell(ref, '')
        elif k == 'C-C':
            if raw:
                a.clip_ss = (raw, self.r, self.c, 'copy')
                a.msg = T('{ref} copied.', ref=ref)
        elif k == 'C-X':
            if raw:
                a.clip_ss = (raw, self.r, self.c, 'cut')
                self.set_cell(ref, '')
                a.msg = T('{ref} cut.', ref=ref)
        elif k == 'C-V':
            if not a.clip_ss:
                a.msg = 'The clipboard is empty.'
                beep()
            else:
                craw, r0, c0, mode = a.clip_ss
                new = shift_refs(craw, self.r - r0, self.c - c0) \
                    if mode == 'copy' and is_value(craw) else craw
                self.set_cell(ref, new)
        elif k == 'C-R':
            self.replicate()
        elif k == 'C-G':
            t = a.ask('Go to cell: ')
            if t:
                try:
                    self.c, self.r = split_ref(t.strip().upper().replace('$', ''))
                except SSError:
                    a.msg = T('"{text}" is not a cell (try something like C12).', text=t)
                    beep()
        elif k == 'C-F':
            self.find()
        elif k == 'C-N':
            self.shift_rows(self.r, +1)
            a.msg = T('Row {n} inserted.', n=self.r)
        elif k == 'C-D':
            if a.yesno(T('Delete row {n}?', n=self.r)):
                self.shift_rows(self.r, -1)
                a.msg = 'Row deleted.'
        elif k == 'C-W':
            t = a.ask(T('Width of column {col} (3-60): ', col=COLS[self.c]), str(self.width(self.c)))
            if t:
                try:
                    self.widths[COLS[self.c]] = max(3, min(60, int(t)))
                    self.dirty = True
                except ValueError:
                    beep()
        elif k == 'C-P':
            self.export()
        else:
            beep()

    def replicate(self):
        a = self.app
        ref = self.ref()
        raw = self.cells.get(ref)
        if not raw:
            a.msg = 'Nothing to replicate: the current cell is blank.'
            beep()
            return
        ex = f'{COLS[self.c]}{min(MAXROW, self.r + 9)}'
        t = a.ask(T('Replicate {ref} through cell (e.g. {ex}): ', ref=ref, ex=ex))
        if not t:
            return
        try:
            c2, r2 = split_ref(t.strip().upper().replace('$', ''))
        except SSError:
            a.msg = T('"{text}" is not a cell.', text=t)
            beep()
            return
        n = 0
        for r in range(min(self.r, r2), max(self.r, r2) + 1):
            for c in range(min(self.c, c2), max(self.c, c2) + 1):
                if (r, c) == (self.r, self.c):
                    continue
                self.cells[f'{COLS[c]}{r}'] = \
                    shift_refs(raw, r - self.r, c - self.c) if is_value(raw) else raw
                n += 1
        if n:
            self.dirty = True
        self.cache = {}
        a.msg = T('Replicated {ref} into {n} cell(s).', ref=ref, n=n)

    def find(self):
        a = self.app
        q = a.ask('Find text in cells: ', self.last_find)
        if not q:
            return
        self.last_find = q
        ql = q.lower()
        hits = sorted((split_ref(k)[1], split_ref(k)[0])
                      for k, v in self.cells.items() if ql in v.lower())
        if not hits:
            a.msg = T('"{text}" was not found.', text=q)
            beep()
            return
        nxt = next((h for h in hits if h > (self.r, self.c)), hits[0])
        self.r, self.c = nxt

    def export(self):
        a = self.app
        if not self.cells:
            a.msg = 'The spreadsheet is empty.'
            return
        fn = self.export_path('.csv', 'Export values to CSV file: ')
        if not fn:
            return
        self.cache = {}
        maxr, maxc = self.used_extent()
        try:
            with open(fn, 'w', newline='', encoding='utf-8') as f:
                w = csv.writer(f)
                for r in range(1, maxr + 1):
                    row = []
                    for c in range(maxc + 1):
                        try:
                            v = self.value(f'{COLS[c]}{r}')
                        except SSError:
                            v = 'ERROR'
                        if v is None:
                            v = ''
                        elif isinstance(v, float) and v == int(v) and abs(v) < 1e15:
                            v = int(v)
                        row.append(v)
                    w.writerow(row)
            a.msg = T('Exported {n} rows to {file}', n=maxr, file=os.path.basename(fn))
        except OSError as e:
            a.msg = T('Could not write file: {err}', err=e)
            beep()


# ---- Data Base -----------------------------------------------------------

class DBDoc(Doc):
    kind = 'db'
    HELP = [
        'DATA BASE COMMANDS               (Ctrl replaces the Open-Apple key)',
        '',
        'Ctrl-Z ............ zoom: switch Multiple-record / Single-record layout',
        '',
        'Multiple records:  Up/Down = record   Left/Right/Tab = category',
        'Single record:     Up/Down = category Left/Right, PgUp/PgDn = record',
        'Ctrl-T / Ctrl-B ... first / last record',
        '',
        'Just start typing to change the highlighted entry.',
        'Return / Ctrl-U ... edit the entry (Return/Tab inside moves to the next)',
        'Delete ............ blank the entry',
        '',
        'Ctrl-N ............ add a new record (type each entry, Escape ends)',
        'Ctrl-D ............ delete this record',
        'Ctrl-C / Ctrl-V ... copy this record / paste it as a new record',
        '',
        'Ctrl-F ............ select only records containing some text',
        '                    (Return on an empty line selects all again)',
        'Ctrl-A ............ arrange (sort) by this category; again = reverse',
        'Ctrl-E ............ change categories: rename, insert, delete, move',
        'Ctrl-W ............ width of this category in the table',
        'Ctrl-P ............ export the selected records to CSV',
        'Ctrl-S ............ save              Ctrl-Q ........... quick change',
        'Escape ............ Main Menu         Ctrl-K ........... this help',
    ]

    def __init__(self, app, name, path=None):
        super().__init__(app, name, path)
        self.fields = [T('Category {n}', n=1)]
        self.records = []
        self.widths = [0]
        self.single = False
        self.cur = self.f = self.top = self.left = self.ftop = 0
        self.filter = ''
        self.sort_state = (None, False)
        self.adding = False

    def size(self):
        return sum(len(v) + 1 for rec in self.records for v in rec)

    def to_json(self):
        self.pad_widths()
        return {'fields': self.fields, 'records': self.records, 'widths': self.widths}

    def from_json(self, d):
        self.fields = [clean(f) for f in d.get('fields', [])] or ['Category 1']
        n = len(self.fields)
        self.records = [([clean(v) for v in rec] + [''] * n)[:n] for rec in d.get('records', [])]
        self.widths = [int(x) for x in d.get('widths', [])][:n]
        self.pad_widths()

    def pad_widths(self):
        n = len(self.fields)
        self.widths = (list(self.widths) + [0] * n)[:n]

    def w(self, i):
        if i < len(self.widths) and self.widths[i]:
            return max(3, min(60, self.widths[i]))
        return max(10, min(20, len(self.fields[i]) + 2))

    def rows(self):
        if not self.filter:
            return list(range(len(self.records)))
        q = self.filter.lower()
        return [i for i, rec in enumerate(self.records) if any(q in v.lower() for v in rec)]

    def vis_fields(self, W):
        out, x, f = [], 5, self.left
        while f < len(self.fields) and x + self.w(f) <= W:
            out.append((f, x))
            x += self.w(f)
            f += 1
        return out

    def draw(self):
        a = self.app
        H, W = a.scr.getmaxyx()
        rows = self.rows()
        self.cur = max(0, min(self.cur, len(rows) - 1)) if rows else 0
        self.f = max(0, min(self.f, len(self.fields) - 1))
        info = T('Record {a} of {b}', a=self.cur + 1 if rows else 0, b=len(rows))
        if self.filter:
            info += '   ' + T('({n} in file)', n=len(self.records))
        a.frame(self.title(), 'REVIEW/ADD/CHANGE', 'Escape: Main Menu', 'Ctrl-K: Help', info)
        sel = T('Selection: ') + (T('records containing "{text}"', text=self.filter)
                                  if self.filter else T('All records'))
        a.put(2, 0, sel)
        mode = T('Single-record layout' if self.single else 'Multiple-record layout')
        a.put(2, W - len(mode) - 1, mode)
        if not rows:
            a.put(5, 4, T('No records match. Press Ctrl-F, then Return, to select all.'
                          if self.filter else 'No records yet. Press Ctrl-N to add one.'), a.A_B)
            cursor(0)
            return
        if self.single:
            rec = self.records[rows[self.cur]]
            nl = min(30, max(len(f) for f in self.fields))
            vh = H - 7
            if self.f < self.ftop:
                self.ftop = self.f
            if self.f >= self.ftop + vh:
                self.ftop = self.f - vh + 1
            for j in range(vh):
                fi = self.ftop + j
                if fi >= len(self.fields):
                    break
                y = 4 + j
                a.put(y, 2, self.fields[fi][:nl].rjust(nl) + ':', a.A_B)
                val = rec[fi][:W - nl - 7]
                if fi == self.f:
                    a.put(y, nl + 4, (val or ' '), a.A_R)
                else:
                    a.put(y, nl + 4, val)
        else:
            if self.f < self.left:
                self.left = self.f
            vis = self.vis_fields(W)
            while vis and self.f > vis[-1][0]:
                self.left += 1
                vis = self.vis_fields(W)
            for fi, x in vis:
                w = self.w(fi)
                a.put(3, x, self.fields[fi][:w - 1].ljust(w), a.A_B)
                a.put(4, x, '-' * (w - 1))
            vh = H - 7
            if self.cur < self.top:
                self.top = self.cur
            if self.cur >= self.top + vh:
                self.top = self.cur - vh + 1
            for j in range(vh):
                ri = self.top + j
                if ri >= len(rows):
                    break
                rec = self.records[rows[ri]]
                a.put(5 + j, 0, f'{ri + 1:>4}', a.A_R if ri == self.cur else a.A_B)
                for fi, x in vis:
                    w = self.w(fi)
                    cur = ri == self.cur and fi == self.f
                    a.put(5 + j, x, rec[fi][:w - 1].ljust(w - 1), a.A_R if cur else a.A_N)
        cursor(0)

    def edit(self, first):
        a = self.app
        while True:
            rows = self.rows()
            if not rows:
                return
            self.cur = max(0, min(self.cur, len(rows) - 1))
            rec = self.records[rows[self.cur]]
            init = first if first is not None else rec[self.f]
            first = None
            self.draw()
            H, W = a.scr.getmaxyx()
            label = f'{self.fields[self.f]}: '
            a.put(H - 1, 0, ' ' * (W - 1))
            text, k = a.line_edit(H - 1, 0, W - len(label) - 2, init,
                                  exits=('ENTER', 'TAB', 'BTAB', 'UP', 'DOWN'), label=label)
            cursor(0)
            if k == 'ESC':
                self.adding = False
                return
            text = clean(text)
            if text != rec[self.f]:
                rec[self.f] = text
                self.dirty = True
            last = self.f >= len(self.fields) - 1
            if k in ('ENTER', 'TAB'):
                if not last:
                    self.f += 1
                elif self.adding:
                    self.adding = False
                    return
            elif k == 'BTAB':
                self.f = max(0, self.f - 1)
            elif k == 'UP':
                self.cur = max(0, self.cur - 1)
            elif k == 'DOWN':
                self.cur = min(len(rows) - 1, self.cur + 1)
            if not self.adding:
                return

    def insert_record(self, rec):
        if self.filter:
            self.filter = ''
        rows = self.rows()
        pos = rows[self.cur] + 1 if rows else 0
        self.records.insert(pos, rec)
        self.cur = pos
        self.dirty = True
        return pos

    def new_record(self):
        rec = [''] * len(self.fields)
        self.insert_record(rec)
        self.f = 0
        self.adding = True
        self.edit(None)
        self.adding = False
        if not any(rec):
            for i, x in enumerate(self.records):
                if x is rec:
                    del self.records[i]
                    self.cur = max(0, i - 1)
                    break
            self.app.msg = 'Empty record discarded.'

    def handle(self, k):
        a = self.app
        rows = self.rows()
        n = len(rows)
        if len(k) == 1:
            if not rows:
                a.msg = 'Press Ctrl-N to add a record first.'
                beep()
            else:
                self.edit(k)
            return
        if k in ('ENTER', 'C-U'):
            if rows:
                self.edit(None)
            else:
                self.new_record()
            return
        H = a.scr.getmaxyx()[0]
        nf = len(self.fields)
        if self.single:
            nav = {'UP': ('f', -1), 'BTAB': ('f', -1), 'DOWN': ('f', 1), 'TAB': ('f', 1),
                   'LEFT': ('r', -1), 'PGUP': ('r', -1), 'RIGHT': ('r', 1), 'PGDN': ('r', 1)}
        else:
            nav = {'UP': ('r', -1), 'DOWN': ('r', 1), 'PGUP': ('r', -(H - 7)),
                   'PGDN': ('r', H - 7), 'LEFT': ('f', -1), 'BTAB': ('f', -1),
                   'RIGHT': ('f', 1), 'TAB': ('f', 1)}
        if k in nav:
            what, d = nav[k]
            if what == 'f':
                self.f = max(0, min(nf - 1, self.f + d))
            else:
                self.cur = max(0, min(n - 1, self.cur + d))
        elif k in ('HOME', 'C-T'):
            self.cur = 0
        elif k in ('END', 'C-B'):
            self.cur = max(0, n - 1)
        elif k in ('BKSP', 'DEL', 'C-Y'):
            if rows and self.records[rows[self.cur]][self.f]:
                self.records[rows[self.cur]][self.f] = ''
                self.dirty = True
        elif k == 'C-N':
            self.new_record()
        elif k == 'C-D':
            if rows and a.yesno(T('Delete record {n}?', n=self.cur + 1)):
                del self.records[rows[self.cur]]
                self.dirty = True
                a.msg = 'Record deleted.'
        elif k == 'C-Z':
            self.single = not self.single
        elif k == 'C-F':
            t = a.ask('Select records containing (Return alone = all records): ', self.filter)
            if t is not None:
                self.filter = t.strip()
                self.cur = self.top = 0
                a.msg = T('{n} record(s) selected.', n=len(self.rows()))
        elif k == 'C-A':
            self.arrange()
        elif k == 'C-E':
            self.categories()
        elif k == 'C-W':
            t = a.ask(T('Width of "{name}" (3-60): ', name=self.fields[self.f]), str(self.w(self.f)))
            if t:
                try:
                    self.pad_widths()
                    self.widths[self.f] = max(3, min(60, int(t)))
                    self.dirty = True
                except ValueError:
                    beep()
        elif k == 'C-C':
            if rows:
                a.clip_db = list(self.records[rows[self.cur]])
                a.msg = 'Record copied.'
        elif k == 'C-V':
            if a.clip_db is None:
                a.msg = 'The clipboard is empty.'
                beep()
            else:
                self.insert_record((list(a.clip_db) + [''] * nf)[:nf])
                a.msg = 'Record pasted below.'
        elif k == 'C-P':
            self.export()
        else:
            beep()

    def arrange(self):
        fi = self.f
        field, desc = self.sort_state
        desc = (not desc) if field == fi else False
        self.sort_state = (fi, desc)

        def keyf(rec):
            v = rec[fi].strip()
            try:
                return (0, float(v.replace(',', '').replace('$', '')), '')
            except ValueError:
                return (1, 0.0, v.lower())
        full = [r for r in self.records if r[fi].strip()]
        empty = [r for r in self.records if not r[fi].strip()]
        full.sort(key=keyf, reverse=desc)
        self.records = full + empty
        self.cur = self.top = 0
        self.dirty = True
        self.app.msg = T('Arranged by "{name}" ', name=self.fields[fi]) + \
            T('(9 to 0, Z to A).' if desc else '(0 to 9, A to Z).')

    def categories(self):
        a = self.app
        name = self.fields[self.f]
        i = a.select([T('Category: {name}', name=name)], [
            'Rename this category',
            'Insert a new category after this one',
            'Delete this category',
            'Move this category left',
            'Move this category right'], esc='Escape: Review/Add/Change')
        if i is None:
            return
        self.pad_widths()
        if i == 0:
            t = a.ask('New name: ', name)
            if t and t.strip():
                self.fields[self.f] = clean(t).strip()[:30]
                self.dirty = True
        elif i == 1:
            t = a.ask('Name of the new category: ')
            if t and t.strip():
                p = self.f + 1
                self.fields.insert(p, clean(t).strip()[:30])
                self.widths.insert(p, 0)
                for rec in self.records:
                    rec.insert(p, '')
                self.f = p
                self.dirty = True
        elif i == 2:
            if len(self.fields) == 1:
                a.msg = 'A data base needs at least one category.'
                beep()
            elif a.yesno(T('Delete category "{name}" and everything in it?', name=name)):
                del self.fields[self.f]
                del self.widths[self.f]
                for rec in self.records:
                    del rec[self.f]
                self.f = max(0, self.f - 1)
                self.dirty = True
        else:
            j = self.f + (-1 if i == 3 else 1)
            if 0 <= j < len(self.fields):
                for lst in [self.fields, self.widths] + self.records:
                    lst[self.f], lst[j] = lst[j], lst[self.f]
                self.f = j
                self.dirty = True

    def export(self):
        a = self.app
        fn = self.export_path('.csv', 'Export records to CSV file: ')
        if not fn:
            return
        rows = self.rows()
        try:
            with open(fn, 'w', newline='', encoding='utf-8') as f:
                w = csv.writer(f)
                w.writerow(self.fields)
                for i in rows:
                    w.writerow(self.records[i])
            a.msg = T('Exported {n} records to {file}', n=len(rows), file=os.path.basename(fn))
        except OSError as e:
            a.msg = T('Could not write file: {err}', err=e)
            beep()


# --------------------------------------------------------------------------
# Hebrew  (עברית)
# --------------------------------------------------------------------------

LANGUAGES['he'] = {'name': 'עברית', 'strings': {
    # general
    'Y': 'כ', 'N': 'ל',
    'Escape: Main Menu': 'Escape: תפריט ראשי',
    'Escape: {name}': 'Escape: {name}',
    'Escape: Cancel': 'Escape: ביטול',
    'Escape: Go back': 'Escape: חזרה',
    'Escape: Review/Add/Change': 'Escape: חזרה לקובץ',
    'RetroWorks needs at least {w}x{h}.': 'RetroWorks צריכה חלון של {w}x{h} לפחות.',
    'This window is {w}x{h}. Please make it larger.': 'החלון הנוכחי הוא {w}x{h}. נא להגדיל אותו.',
    'Disk: {disk}': 'דיסק: {disk}',
    'Ctrl-K for Help': 'Ctrl-K לעזרה',
    '{n}K Avail.': '{n}K פנויים',
    'Type number, or use arrows, then press Return': 'הקלד מספר או השתמש בחצים, ואז לחץ Return',
    'Type entry, then press Return.   Escape cancels': 'הקלד ולחץ Return.   Escape לביטול',
    'Press Escape or Return to continue': 'לחץ Escape או Return להמשך',
    '   (arrows scroll)': '   (החצים גוללים)',
    '(none)': '(אין)',
    'Name': 'שם',
    'Type': 'סוג',
    'Type Modified': 'סוג שינוי אחרון',
    'Word Processor': 'מעבד תמלילים',
    'Data Base': 'מסד נתונים',
    'Spreadsheet': 'גיליון אלקטרוני',
    'Word Processor  ·  Data Base  ·  Spreadsheet': 'מעבד תמלילים  ·  מסד נתונים  ·  גיליון אלקטרוני',
    'REVIEW/ADD/CHANGE': 'סקירה/הוספה/שינוי',
    'Ctrl-K: Help': 'Ctrl-K: עזרה',
    'File: {name}': 'קובץ: {name}',
    '{kind} Help': 'עזרה - {kind}',
    'The clipboard is empty.': 'הלוח ריק.',
    'Could not write file: {err}': 'לא ניתן לכתוב את הקובץ: {err}',
    'Could not open {file}: {err}': 'לא ניתן לפתוח את {file}: {err}',
    'Thanks for using RetroWorks ][.  Your files are in {folder}':
        'תודה שהשתמשת ב-RetroWorks ][.  הקבצים שלך נמצאים ב-{folder}',

    # splash and about
    'Version {v}': 'גרסה {v}',
    'An original tribute to the integrated software of the 1980s.': 'מחווה מקורית לתוכנות המשולבות של שנות ה-80.',
    'Not affiliated with or endorsed by Apple Inc.': 'אינה קשורה ל-Apple Inc. ואינה מאושרת על ידה.',
    "Today's date: {date}": 'התאריך היום: {date}',
    'Ctrl takes the place of the Open-Apple key.': 'מקש Ctrl מחליף את מקש ה-Open-Apple.',
    'Press Return to continue': 'לחץ Return להמשך',
    'About RetroWorks': 'אודות RetroWorks',

    # main menu
    'Main Menu': 'תפריט ראשי',
    'Add files to the Desktop': 'הוספת קבצים לשולחן העבודה',
    'Work with one of the files on the Desktop': 'עבודה עם אחד הקבצים שעל שולחן העבודה',
    'Save Desktop files to disk': 'שמירת הקבצים שעל שולחן העבודה בדיסק',
    'Remove files from the Desktop': 'הסרת קבצים משולחן העבודה',
    'Other Activities': 'פעולות נוספות',
    'Quit': 'יציאה',
    'Do you really want to quit?': 'לצאת מהתוכנה?',
    'Save changes to "{name}" before quitting?': 'לשמור את השינויים ב-"{name}" לפני היציאה?',

    # add files
    'Add Files': 'הוספת קבצים',
    'Get files from:': 'טעינת קבצים מ:',
    'The current disk: {disk}': 'הדיסק הנוכחי: {disk}',
    'A different disk': 'דיסק אחר',
    'A Different Disk': 'דיסק אחר',
    'Make a new file for the:': 'יצירת קובץ חדש עבור:',
    'Make a new file:': 'יצירת קובץ חדש:',
    'From scratch': 'קובץ ריק',
    'From a text (ASCII) file': 'מקובץ טקסט (ASCII)',
    'From a text (ASCII / CSV) file': 'מקובץ טקסט (ASCII / CSV)',
    'From a text (CSV) file': 'מקובץ טקסט (CSV)',
    'Get Files': 'בחירת קובץ',
    'The Desktop is full ({n} files). Remove a file first.': 'שולחן העבודה מלא ({n} קבצים). יש להסיר קובץ קודם.',
    'Type the pathname of the disk (folder):': 'הקלד את הנתיב של הדיסק (התיקייה):',
    'There is no folder called {path}': 'אין תיקייה בשם {path}',
    'There are no RetroWorks files on {disk}': 'אין קובצי RetroWorks ב-{disk}',
    'That file is already on the Desktop.': 'הקובץ הזה כבר נמצא על שולחן העבודה.',
    'Could not read that file: {err}': 'לא ניתן לקרוא את הקובץ: {err}',
    'Type a name for this new file:': 'הקלד שם לקובץ החדש:',
    'A file with that name is already on the Desktop.': 'כבר יש על שולחן העבודה קובץ בשם הזה.',
    '"{file}" already exists on disk. Replace it when you save?': 'הקובץ "{file}" כבר קיים בדיסק. להחליף אותו בשמירה?',
    'Type the complete pathname of the text file:': 'הקלד את הנתיב המלא של קובץ הטקסט:',
    'That file is empty.': 'הקובץ ריק.',

    # desktop / save / remove
    'Desktop Index': 'הקבצים שעל שולחן העבודה',
    'There are no files on the Desktop. Use "Add files" first.': 'אין קבצים על שולחן העבודה. השתמש קודם ב"הוספת קבצים".',
    'There are no files on the Desktop.': 'אין קבצים על שולחן העבודה.',
    'Save Files': 'שמירת קבצים',
    'Save which file?': 'איזה קובץ לשמור?',
    'All changed files': 'כל הקבצים ששונו',
    'Nothing to save: no file has changed.': 'אין מה לשמור: אף קובץ לא שונה.',
    'Saved {n} files to {disk}': '{n} קבצים נשמרו ב-{disk}',
    'Saved "{name}" to {file}': '"{name}" נשמר בקובץ {file}',
    'Could not save: {err}': 'השמירה נכשלה: {err}',
    'Remove Files': 'הסרת קבצים',
    '"{name}" has changes. Save it before removing?': 'יש שינויים ב-"{name}". לשמור לפני ההסרה?',
    '"{name}" removed from the Desktop.': '"{name}" הוסר משולחן העבודה.',

    # other activities
    'Change current disk': 'החלפת הדיסק הנוכחי',
    'Delete files from disk': 'מחיקת קבצים מהדיסק',
    'Choose language  (Language / שפה)': 'בחירת שפה  (Language / שפה)',
    'Change Disk': 'החלפת דיסק',
    'Type the pathname of the new current disk (folder):': 'הקלד את הנתיב של הדיסק (התיקייה) החדש:',
    '"{path}" does not exist. Create it?': 'התיקייה "{path}" לא קיימת. ליצור אותה?',
    'Could not create folder: {err}': 'לא ניתן ליצור את התיקייה: {err}',
    'Current disk is now {disk}': 'הדיסק הנוכחי הוא עכשיו {disk}',
    'Delete Files': 'מחיקת קבצים',
    'That file is on the Desktop. Remove it from the Desktop first.': 'הקובץ נמצא על שולחן העבודה. יש להסיר אותו משולחן העבודה קודם.',
    'Permanently delete "{file}" from the disk?': 'למחוק את "{file}" מהדיסק לצמיתות?',
    'Deleted {file}': 'הקובץ {file} נמחק',
    'Could not delete: {err}': 'המחיקה נכשלה: {err}',
    'Language': 'שפה',
    'Memory size': 'גודל הזיכרון',
    'Right-to-left display': 'תצוגת ימין לשמאל',
    'Right-to-left': 'ימין לשמאל',
    'Who arranges right-to-left text on the screen?': 'מי מסדר טקסט מימין לשמאל על המסך?',
    'The terminal (macOS Terminal)': 'הטרמינל (Terminal של macOS)',
    'RetroWorks (Windows Terminal, iTerm2, others)': 'RetroWorks (Windows Terminal, iTerm2 ואחרים)',
    'Pick the one that makes this line read correctly:': 'בחר את האפשרות שבה השורה הזו נקראת נכון:',
    'Memory Size': 'גודל הזיכרון',
    'How much memory does this computer have?': 'כמה זיכרון יש במחשב הזה?',
    '{n}K for the Desktop': '{n}K לשולחן העבודה',
    'Memory: {name}  ({n}K available)': 'זיכרון: {name}  ({n}K פנויים)',
    'The Desktop memory is full. Remove a file or add memory.': 'הזיכרון של שולחן העבודה מלא. הסר קובץ או הוסף זיכרון.',
    'Choose the language for menus and messages:': 'בחר את שפת התפריטים וההודעות:',
    'Language: {name}': 'שפה: {name}',

    # data base categories screen
    'CHANGE NAME/CATEGORY': 'שינוי שם/קטגוריה',
    'Type the names of the categories (fields), one at a time.': 'הקלד את שמות הקטגוריות (השדות), אחד בכל פעם.',
    'Press Return on an empty line when you are done.': 'בסיום לחץ Return בשורה ריקה.',
    'Category {n} name: ': 'שם קטגוריה {n}: ',
    'Category {n}': 'קטגוריה {n}',
    'You already have a category with that name.': 'כבר יש קטגוריה בשם הזה.',

    # word processor
    'Insert': 'הוספה',
    'Overwrite': 'דריסה',
    'Right-to-left': 'ימין לשמאל',
    'Left-to-right': 'שמאל לימין',
    'Line {a} of {b}': 'שורה {a} מתוך {b}',
    'Column {n}': 'עמודה {n}',
    '{n} words': '{n} מילים',
    'Paragraph cut to the clipboard.': 'הפסקה נגזרה ללוח.',
    'Paragraph copied to the clipboard.': 'הפסקה הועתקה ללוח.',
    'Pasted above the cursor.': 'הודבק מעל הסמן.',
    'Paragraph is now right-to-left.': 'הפסקה עכשיו מימין לשמאל.',
    'Paragraph is now left-to-right.': 'הפסקה עכשיו משמאל לימין.',
    'Bold': 'הדגשה',
    'Underline': 'קו תחתון',
    'Bold on.': 'הדגשה פועלת.',
    'Bold off.': 'הדגשה כבויה.',
    'Underline on.': 'קו תחתון פועל.',
    'Underline off.': 'קו תחתון כבוי.',
    'Find: ': 'חיפוש: ',
    '"{text}" was not found.': '"{text}" לא נמצא.',
    'Replace: ': 'החלפה של: ',
    'Replace "{text}" with: ': 'להחליף את "{text}" ב: ',
    'Replaced {n} occurrence(s).': 'הוחלפו {n} מופעים.',
    'Print to text file: ': 'הדפסה לקובץ טקסט: ',
    'Printed {n} lines to {file}': '{n} שורות הודפסו לקובץ {file}',

    # spreadsheet
    'Type entry or use Ctrl commands        Ctrl-K: Help': 'הקלד ערך או השתמש בפקודות Ctrl        Ctrl-K: עזרה',
    '(Value) ': '(ערך) ',
    '(Label) ': '(תווית) ',
    'ERROR: ': 'שגיאה: ',
    '{ref}: ERROR - {err}': '{ref}: שגיאה - {err}',
    'circular reference': 'הפניה מעגלית',
    'number out of range': 'מספר מחוץ לטווח',
    'formula chain too deep': 'שרשרת נוסחאות ארוכה מדי',
    'division by zero': 'חלוקה באפס',
    'syntax error': 'שגיאת תחביר',
    'math error': 'שגיאה חשבונית',
    'bad cell reference': 'הפניה לא תקינה לתא',
    'empty formula': 'נוסחה ריקה',
    '{ref} copied.': '{ref} הועתק.',
    '{ref} cut.': '{ref} נגזר.',
    'Go to cell: ': 'מעבר לתא: ',
    '"{text}" is not a cell (try something like C12).': '"{text}" אינו תא (נסה למשל C12).',
    '"{text}" is not a cell.': '"{text}" אינו תא.',
    'Row {n} inserted.': 'שורה {n} נוספה.',
    'Delete row {n}?': 'למחוק את שורה {n}?',
    'Row deleted.': 'השורה נמחקה.',
    'Width of column {col} (3-60): ': 'רוחב עמודה {col} (3-60): ',
    'Nothing to replicate: the current cell is blank.': 'אין מה לשכפל: התא הנוכחי ריק.',
    'Replicate {ref} through cell (e.g. {ex}): ': 'לשכפל את {ref} עד התא (למשל {ex}): ',
    'Replicated {ref} into {n} cell(s).': '{ref} שוכפל ל-{n} תאים.',
    'Find text in cells: ': 'חיפוש טקסט בתאים: ',
    'The spreadsheet is empty.': 'הגיליון ריק.',
    'Export values to CSV file: ': 'ייצוא הערכים לקובץ CSV: ',
    'Exported {n} rows to {file}': '{n} שורות יוצאו לקובץ {file}',

    # data base
    'Record {a} of {b}': 'רשומה {a} מתוך {b}',
    '({n} in file)': '({n} בקובץ)',
    'Selection: ': 'בחירה: ',
    'records containing "{text}"': 'רשומות המכילות "{text}"',
    'All records': 'כל הרשומות',
    'Single-record layout': 'תצוגת רשומה בודדת',
    'Multiple-record layout': 'תצוגת טבלה',
    'No records match. Press Ctrl-F, then Return, to select all.': 'אין רשומות מתאימות. לחץ Ctrl-F ואז Return כדי לבחור הכול.',
    'No records yet. Press Ctrl-N to add one.': 'אין עדיין רשומות. לחץ Ctrl-N כדי להוסיף.',
    'Empty record discarded.': 'רשומה ריקה בוטלה.',
    'Press Ctrl-N to add a record first.': 'לחץ Ctrl-N כדי להוסיף רשומה קודם.',
    'Delete record {n}?': 'למחוק את רשומה {n}?',
    'Record deleted.': 'הרשומה נמחקה.',
    'Select records containing (Return alone = all records): ': 'בחירת רשומות המכילות (Return בלבד = הכול): ',
    '{n} record(s) selected.': 'נבחרו {n} רשומות.',
    'Width of "{name}" (3-60): ': 'רוחב "{name}" (3-60): ',
    'Record copied.': 'הרשומה הועתקה.',
    'Record pasted below.': 'הרשומה הודבקה למטה.',
    'Arranged by "{name}" ': 'מוין לפי "{name}" ',
    '(9 to 0, Z to A).': '(בסדר יורד).',
    '(0 to 9, A to Z).': '(בסדר עולה).',
    'Category: {name}': 'קטגוריה: {name}',
    'Rename this category': 'שינוי שם הקטגוריה',
    'Insert a new category after this one': 'הוספת קטגוריה חדשה אחרי זו',
    'Delete this category': 'מחיקת הקטגוריה',
    'Move this category left': 'הזזת הקטגוריה שמאלה',
    'Move this category right': 'הזזת הקטגוריה ימינה',
    'New name: ': 'שם חדש: ',
    'Name of the new category: ': 'שם הקטגוריה החדשה: ',
    'A data base needs at least one category.': 'מסד נתונים צריך לפחות קטגוריה אחת.',
    'Delete category "{name}" and everything in it?': 'למחוק את הקטגוריה "{name}" ואת כל התוכן שלה?',
    'Export records to CSV file: ': 'ייצוא הרשומות לקובץ CSV: ',
    'Exported {n} records to {file}': '{n} רשומות יוצאו לקובץ {file}',
}, 'help': {
    'about': [
        'RetroWorks ][   גרסה ' + VERSION,
        '',
        'מעבד תמלילים, מסד נתונים וגיליון אלקטרוני משולבים, בסגנון',
        'תוכנות העבודה של ה-Apple ][ : מסך ירוק ברוחב 80 עמודות.',
        '',
        'נכתבה מאפס ב-Python. זו אינה AppleWorks, אין בה קוד של Apple,',
        'והיא אינה קשורה ל-Apple Inc. ואינה מאושרת על ידה.',
        '',
        'על שולחן העבודה אפשר להחזיק עד 12 קבצים פתוחים.',
        'Escape   תמיד מחזיר אחורה, עד התפריט הראשי.',
        'Ctrl-Q   (Quick Change) קפיצה ישירה לקובץ אחר.',
        'Ctrl-K   עזרה עבור סוג הקובץ שעובדים עליו.',
        '',
        'הקבצים נשמרים כ-JSON פשוט:',
        '.rwp     מעבד תמלילים',
        '.rdb     מסד נתונים',
        '.rss     גיליון אלקטרוני',
        '',
        'מכיוון ש-macOS שומרת את מקש Command לעצמה, כל פקודות',
        'ה-Open-Apple מוקלדות עם Ctrl.',
    ],
    'wp': [
        'פקודות מעבד התמלילים      (Ctrl מחליף את מקש ה-Open-Apple)',
        '',
        'Arrows        הזזת הסמן',
        'Ctrl-T/G      תחילת / סוף המסמך',
        'PgUp/PgDn     עמוד למעלה / למטה',
        'Home/End      תחילת / סוף השורה',
        'Return        פסקה חדשה',
        'Delete        מחיקה לאחור',
        'Ctrl-D        מחיקה קדימה',
        'Ctrl-Y        מחיקה עד סוף הפסקה',
        'Ctrl-E        מצב הוספה / דריסה',
        'Tab           4 רווחים',
        'Ctrl-X / C    גזירה / העתקה של הפסקה',
        'Ctrl-V        הדבקה מעל הסמן',
        'Ctrl-F        חיפוש (שוב)',
        'Ctrl-R        החלפת הכול',
        'Ctrl-L        כיוון הפסקה: ימין לשמאל / שמאל לימין',
        'Ctrl-B        הדגשה (Bold) פועלת / כבויה',
        'Ctrl-U        קו תחתון פועל / כבוי',
        'Ctrl-P        "הדפסה" לקובץ טקסט (76 עמודות)',
        'Ctrl-S        שמירה',
        'Ctrl-Q        מעבר מהיר לקובץ אחר',
        'Escape        תפריט ראשי',
        '',
        'הדגשה וקו תחתון חלים על מה שמקלידים מעכשיו; לחיצה נוספת מכבה.',
        'הדפסה לקובץ טקסט שומרת את הטקסט בלבד.',
        '',
        'פסקה מימין לשמאל מיושרת לשוליים הימניים. פסקה חדשה מקבלת',
        'את הכיוון של הפסקה שבה לחצת Return. השורה התחתונה מציגה',
        'את מצב הסמן, כיוון הפסקה, השורה, העמודה ומספר המילים.',
    ],
    'ss': [
        'פקודות הגיליון האלקטרוני  (Ctrl מחליף את מקש ה-Open-Apple)',
        '',
        'Arrows/Tab    תנועה בין התאים',
        'Ctrl-T        מעבר ל-A1',
        'Ctrl-B        התא האחרון שבשימוש',
        'Ctrl-G        מעבר לתא',
        'Ctrl-F        חיפוש טקסט',
        'Return        עריכת התא (או פשוט להתחיל להקליד)',
        'Delete        ריקון התא',
        '',
        'ערך מתחיל בספרה או באחד התווים  + - . = @ (  למשל:',
        '   +A1*B1        =(B2+B3)/2        @SUM(A1...A10)     @AVG(B1:B9)',
        'כל השאר הוא תווית. " בהתחלה מכריח תווית ("1984).',
        'תווית שמתחילה ב-\\ חוזרת לרוחב כל התא:  \\-   \\=',
        '@SUM @AVG @MIN @MAX @COUNT @ABS @INT @ROUND(x,n) @SQRT',
        '@MOD(x,y) @IF(cond,a,b) @EXP @LN @LOG @SIN @COS @TAN @ATAN @PI',
        '$A$1 הפניה מוחלטת;  A1 הפניה יחסית.',
        '',
        'Ctrl-C/X/V    העתקה / גזירה / הדבקה של תא',
        'Ctrl-R        שכפול התא עד תא יעד',
        'Ctrl-N        הוספת שורה',
        'Ctrl-D        מחיקת שורה',
        'Ctrl-W        רוחב עמודה',
        'Ctrl-P        ייצוא הערכים ל-CSV',
        'Ctrl-S        שמירה',
        'Ctrl-Q        מעבר מהיר לקובץ אחר',
        'Escape        תפריט ראשי',
    ],
    'db': [
        'פקודות מסד הנתונים       (Ctrl מחליף את מקש ה-Open-Apple)',
        '',
        'Ctrl-Z        מעבר בין תצוגת טבלה לתצוגת רשומה בודדת',
        'Arrows        תנועה בין רשומות וקטגוריות',
        'Ctrl-T/B      רשומה ראשונה / אחרונה',
        'Return        עריכת השדה (או פשוט להתחיל להקליד)',
        'Delete        ריקון השדה',
        'Ctrl-N        רשומה חדשה (Escape מסיים)',
        'Ctrl-D        מחיקת הרשומה',
        'Ctrl-C / V    העתקת הרשומה / הדבקה כרשומה חדשה',
        'Ctrl-F        בחירת רשומות שמכילות טקסט',
        '              (Return בשורה ריקה בוחר שוב את כולן)',
        'Ctrl-A        מיון לפי הקטגוריה הזו; שוב = סדר הפוך',
        'Ctrl-E        שינוי קטגוריות: שם, הוספה, מחיקה, הזזה',
        'Ctrl-W        רוחב הקטגוריה בטבלה',
        'Ctrl-P        ייצוא הרשומות הנבחרות ל-CSV',
        'Ctrl-S        שמירה',
        'Ctrl-Q        מעבר מהיר לקובץ אחר',
        'Escape        תפריט ראשי',
    ],
}}


# --------------------------------------------------------------------------
# start-up
# --------------------------------------------------------------------------

def run(scr, args):
    try:
        curses.set_escdelay(25)
    except (AttributeError, curses.error):
        pass
    curses.raw()
    scr.keypad(True)
    app = App(scr, args.folder, args.theme)
    cfg = load_config()
    if cfg.get('memory') in [kb for _, kb in MEMORY_SIZES]:
        app.mem_kb = cfg['memory']
    if cfg.get('bidi') in ('terminal', 'program'):
        app.bidi_mode = cfg['bidi']
    for p in args.files:
        try:
            if len(app.desktop) < MAX_DESKTOP:
                app.desktop.append(load_doc(app, os.path.abspath(os.path.expanduser(p))))
        except (OSError, ValueError, KeyError, TypeError) as e:
            app.msg = T('Could not open {file}: {err}', file=p, err=e)
    try:
        app.splash()
        app.main_menu()
    except Exception:
        for d in app.desktop:
            if d.dirty:
                try:
                    d.path = os.path.join(app.folder, safe_name(d.name) + '-recovered' + EXT[d.kind])
                    d.save()
                except Exception:
                    pass
        raise


def main():
    ap = argparse.ArgumentParser(
        description='RetroWorks ][ - an 80s-style integrated word processor, '
                    'data base and spreadsheet for the Terminal.')
    ap.add_argument('files', nargs='*', help='RetroWorks files to put on the Desktop')
    ap.add_argument('--folder', default=os.path.expanduser('~/RetroWorks'),
                    help='folder used as the "current disk" (default: ~/RetroWorks)')
    ap.add_argument('--theme', choices=['green', 'amber', 'white'], default='green',
                    help='monitor colour (default: green)')
    ap.add_argument('--lang', help='language for menus and messages, e.g. en or he')
    args = ap.parse_args()
    load_language_files()
    cfg = load_config()
    env_lang = os.environ.get('LANG', '')[:2]
    set_language(args.lang or cfg.get('language') or (env_lang if env_lang in LANGUAGES else 'en'))
    if not sys.stdin.isatty() or not sys.stdout.isatty():
        sys.exit('RetroWorks must be run in a terminal window (e.g. macOS Terminal).')
    args.folder = os.path.abspath(os.path.expanduser(args.folder))
    os.makedirs(args.folder, exist_ok=True)
    locale.setlocale(locale.LC_ALL, '')
    os.environ.setdefault('ESCDELAY', '25')
    curses.wrapper(run, args)
    print(T('Thanks for using RetroWorks ][.  Your files are in {folder}', folder=args.folder))


if __name__ == '__main__':
    main()
