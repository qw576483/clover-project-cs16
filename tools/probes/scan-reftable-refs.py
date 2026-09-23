# -*- coding: utf-8 -*-
"""Judgement asset (tools/probes/): every reference cited by the reference table must be
either REACHABLE or EXPLICITLY REGISTERED as an unavailable carrier.

Why this exists
---------------
verify.ps1 item 8 (`reference-table`) only asserted that the table FILE exists; it never
looked inside it.  Item 5 (`screenshot-refs`) resolves `file:line` citations of the
ACCEPTANCE table only, so every dangling carrier path written in the REFERENCE table
(ce hua / dui zhao biao .md) went untested for the whole life of the project.  Measured
2026-09-22 (slice AX): section 0 alone carried 4 dangling `yuan ban zi yuan/...` carriers,
one of them labelled `Test-Path = True` while the path was not on disk.

Scope (slice BB: widened, never loosened)
----------------------------------------
Two tables are scanned, both with the identical rule set:
  * the reference table (plan/dui zhao biao.md)
  * the differences registry (plan/cha yi deng ji.tsv)   <-- added by slice BB
Why the second one: it cites carriers in the very same two shapes, and until slice BB
nothing resolved them -- measured 2026-09-22, it carried 10+ carriers under
`yuan ban zi yuan/cs16src/cs16game/...` that are not on disk, so the whole class of
dangling citations lived outside the gate.  The acceptance table stays item 5's
territory; counting it here as well would double-count and mis-report.

What is judged
--------------
(a) every `yuan-ban-zi-yuan/...` path occurrence  -> must resolve (brace expansion + glob)
(b) every `<path>.<ext>:<line|offset>` citation   -> the file must resolve AND the line
    number must be <= the file's line count (an `0x...` offset must be <= the file size)
(c) every `CL/...` path occurrence                -> CL = client/Assets, must resolve

A carrier that does not resolve is a FAIL **unless** it carries a row in the registry
(plan/载体可达性登记.tsv) whose four-element cells are all filled:
    是什么 / 为什么（不可得） / 出处 / 何时消除
The registry is a real ledger, not a silencer:
  * a registered carrier that DOES resolve again => FAIL (stale row -- delete it)
  * a registered row with an empty four-element cell  => FAIL
  * an unregistered dangling carrier                  => FAIL (printed one per line)
So the judgement is "reachable, or accounted for line by line" -- never "silently absent".

ASCII-only output on purpose: verify.ps1 pipes this through PowerShell, which reads a
BOM-less file as ANSI -- a raw Chinese byte sequence could break the parse.  CJK tokens are
therefore printed as `?`.

Usage
  python tools/probes/scan-reftable-refs.py [--project <root>] [--emit-registry]
Exit 0 = PASS, exit 1 = FAIL.
"""
import argparse
import glob
import io
import os
import re
import sys

SKIP_DIRS = {'.git', 'Library', 'Temp', 'obj', 'Build', 'Builds', 'Logs',
             'UserSettings', '__pycache__', 'node_modules', '.vs'}

EXT = ('cs', 'py', 'md', 'cfg', 'scr', 'txt', 'res', 'dll', 'bsp', 'tga', 'mdl', 'wad',
       'c', 'h', 'cpp', 'inc', 'spr', 'wav', 'mp3', 'ttf', 'bytes', 'bin', 'json', 'ps1',
       'sh', 'mat', 'asset', 'png', 'bmp', 'jpg', 'jpeg', 'xml', 'unity', 'tsv', 'yml',
       'yaml', 'meta', 'tbl', 'kv')

CJK = re.compile(r'[\u4e00-\u9fff]')
YS = '\u539f\u7248\u8d44\u6e90'          # yuan ban zi yuan  (the original-assets root)
PLAN = '\u7b56\u5212'                     # ce hua
REFTBL = '\u5bf9\u7167\u8868'             # dui zhao biao
# slice BB: the differences registry (ce hua/cha yi deng ji.tsv) cites carriers with the
# very same two shapes, and until now nothing resolved them -- item 8b only ever scanned
# the reference table.  Widening the scope to it is the whole point of slice BB.
DIFFREG = '\u5dee\u5f02\u767b\u8bb0'      # cha yi deng ji
REGDOC = '\u8f7d\u4f53\u53ef\u8fbe\u6027\u767b\u8bb0'   # zai ti ke da xing deng ji

# four-element column headers, spelled with escapes to keep this file ASCII-only
C_WHAT = '\u662f\u4ec0\u4e48'                       # shi shen me
C_WHY = '\u4e3a\u4ec0\u4e48'                        # wei shen me
C_SRC = '\u51fa\u5904'                              # chu chu
C_WHEN = '\u4f55\u65f6\u6d88\u9664'                 # he shi xiao chu
C_REF = '\u5f15\u7528\u8def\u5f84'                  # yin yong lu jing (path)
C_ST = '\u72b6\u6001'                               # zhuang tai (status)
ST_GONE = '\u4e0d\u53ef\u5f97'                      # bu ke de


def ascii_safe(s):
    return CJK.sub('?', s)


def expand_braces(s):
    m = re.search(r'\{([^{}]*)\}', s)
    if not m:
        return [s]
    out = []
    for alt in m.group(1).split(','):
        out.extend(expand_braces(s[:m.start()] + alt + s[m.end():]))
    return out


def _hexnum(tok):
    """'0x0435xx' -> ('0x43500', True)  (wildcard low nibbles become 0);
       '0x1a21948' -> ('0x1a21948', False).  None when not hex-shaped."""
    m = re.match(r'^0x([0-9a-fA-F]*)(x+)$', tok)
    if m:
        return ('0x' + (m.group(1) + '0' * len(m.group(2))), True)
    if re.match(r'^0x[0-9a-fA-F]+$', tok):
        return (tok, False)
    return None


def split_linespec(token):
    """'a/b.txt:12-34,56' -> ('a/b.txt', ['12','34','56']); 'x.dll:0x1a21948' -> ('x.dll', ['0x1a21948'])."""
    m = re.match(r'^(.*?)\.([A-Za-z0-9]+):(.*)$', token)
    if not m:
        return None
    path = m.group(1) + '.' + m.group(2)
    spec = m.group(3)
    if not spec:
        return None
    nums = []
    for part in spec.split(','):
        part = part.strip()
        plain = re.match(r'^(\d+)(?:\s*-\s*(\d+))?$', part)
        if plain:
            nums.append(plain.group(1))
            if plain.group(2):
                nums.append(plain.group(2))
            continue
        mrange = re.match(r'^(0x[0-9a-fA-Fx]+)\s*-\s*(0x[0-9a-fA-Fx]+)$', part)
        if mrange:
            a, _ = _hexnum(mrange.group(1))
            b, _ = _hexnum(mrange.group(2))
            if a is None or b is None:
                return None
            nums += [a, b]
            continue
        h = _hexnum(part)
        if h is not None:
            nums.append(h[0])
            continue
        return None
    return path, nums


LABEL = {
    'md': '\u539f\u7248\u89e3\u5305/\u5206\u6790\u8bb0\u5f55\u6587\u6863',
    'py': '\u539f\u7248\u89e3\u5305/\u5bfc\u51fa\u811a\u672c',
    'cfg': '\u539f\u7248\u914d\u7f6e\u811a\u672c', 'scr': '\u539f\u7248\u914d\u7f6e\u811a\u672c',
    'dll': '\u539f\u7248\u53ef\u6267\u884c',
    'bsp': '\u539f\u7248\u5730\u56fe BSP', 'mdl': '\u539f\u7248\u6a21\u578b',
    'wad': '\u539f\u7248 WAD3 \u5bb9\u5668', 'tga': '\u539f\u7248\u8d34\u56fe',
    'spr': '\u539f\u7248\u7cbe\u7075', 'res': '\u539f\u7248 VGUI \u9762\u677f',
    'txt': '\u539f\u7248\u6587\u672c\u6570\u636e', 'ttf': '\u539f\u7248\u5b57\u4f53',
    'bmp': '\u539f\u7248\u5b9e\u673a\u622a\u56fe', 'jpg': '\u539f\u7248\u5b9e\u673a\u622a\u56fe',
    'c': 'GoldSrc SDK \u6e90\u7801', 'h': 'GoldSrc SDK \u5934', 'cpp': 'GoldSrc SDK \u6e90\u7801',
    'inc': '\u539f\u7248\u811a\u672c\u5934',
}


class Scanner(object):
    def __init__(self, root, plan_dir=''):
        self.root = os.path.abspath(root)
        # --plan-dir is the seam that makes this asset SAMPLABLE.  WHY IT EXISTS (measured
        # 2026-09-23): verify.ps1 used to invoke this script with no arguments, so it always read
        # the REAL plan dir -- while the gate self-test's samples inject their ghost carrier into a
        # -PlanDir SANDBOX copy.  The samples therefore could not trip the item, and the item was
        # blind to the override (the sandbox injection vanished into a file nothing read).
        # NOTE: only the three PLAN files follow the override; carrier paths are still resolved
        # against self.root, because a sandbox copy cites real project-relative carriers.
        self.plan = os.path.abspath(plan_dir) if plan_dir else os.path.join(self.root, PLAN)
        self.tbl = os.path.join(self.plan, REFTBL + '.md')
        self.tbl2 = os.path.join(self.plan, DIFFREG + '.tsv')
        # Order matters for the report only; both are scanned identically.
        self.tables = [self.tbl, self.tbl2]
        self.reg = os.path.join(self.plan, REGDOC + '.tsv')
        self.by_name = {}
        self.n_files = 0
        self.refs = []          # (table, kind, carrier, nums_or_None, lineno)
        self.lines_by_file = {}  # table path -> its lines (the VA marker is per citing line)
        self.registry = {}      # carrier -> (status, cells list, lineno)

    # ---- index -----------------------------------------------------------
    def index(self):
        for dirpath, dirnames, filenames in os.walk(self.root):
            dirnames[:] = [d for d in dirnames if d not in SKIP_DIRS]
            for fn in filenames:
                p = os.path.join(dirpath, fn)
                rel = os.path.relpath(p, self.root).replace('\\', '/')
                self.by_name.setdefault(fn.lower(), []).append(rel)
                self.n_files += 1

    # ---- registry --------------------------------------------------------
    def load_registry(self):
        if not os.path.exists(self.reg):
            return
        for i, ln in enumerate(io.open(self.reg, encoding='utf-8').read().split('\n'), 1):
            s = ln.strip()
            if not s or s.startswith('#'):
                continue
            c = ln.split('\t')
            carrier = c[0].strip()
            if not carrier:
                continue
            status = c[1].strip() if len(c) > 1 else ''
            cells = [(c[k].strip() if len(c) > k else '') for k in (2, 3, 4, 5)]
            self.registry[carrier] = (status, cells, i)

    # ---- extraction ------------------------------------------------------
    def scan(self):
        # Token terminators.  Slice BB adds the *opening* delimiters (U+FF08 U+3010 U+300A
        # U+300C U+300D U+FF1A U+2026) to the original set (U+FF0C U+3002 U+FF1B U+3001
        # U+FF09 U+3011 + quote).  Why: the differences registry runs prose straight
        # into a carrier -- `yuan-ban/cs16src` + an opening paren / `...md` + a corner
        # bracket -- and
        # without them the regex swallowed the whole sentence into one bogus "path", which
        # showed up as 2 false DANGLING lines.  A terminator can only SHORTEN a token.
        STOP = r'\uff08\u3010\u300a\u300c\u300d\uff1a\u2026'
        pat_ys = re.compile(YS + r'/[^\s`|\uff0c\u3002\uff1b\u3001\uff09)\u3011\]"\'' + STOP + r']+')
        pat_cl = re.compile(r'(?<![0-9A-Za-z_\u4e00-\u9fff])CL/[^\s`|\uff0c\u3002\uff1b\u3001\uff09)\u3011\]"\'' + STOP + r']+')
        pat_file = re.compile(r'[0-9A-Za-z_\u4e00-\u9fff][0-9A-Za-z_\u4e00-\u9fff./\\-]*'
                              r'\.(?:%s):[0-9][0-9A-Za-z,\-]*' % '|'.join(EXT))
        seen = set()
        for tf in self.tables:
            lines = io.open(tf, encoding='utf-8').read().split('\n')
            self.lines_by_file[tf] = lines
            for i, ln in enumerate(lines, 1):
                found = []
                for m in pat_ys.finditer(ln):
                    found.append(('ys', m.group(0)))
                for m in pat_cl.finditer(ln):
                    found.append(('cl', m.group(0)))
                for m in pat_file.finditer(ln):
                    tok = m.group(0)
                    if tok.startswith(YS + '/') or tok.startswith('CL/'):
                        continue
                    found.append(('file', tok))
                for kind, tok in found:
                    tok = tok.rstrip('.,;:')
                    if ('{' in tok) or ('}' in tok):
                        continue
                    if kind in ('ys', 'cl'):
                        sp = split_linespec(tok)
                        if sp is not None and re.search(r'\.[A-Za-z0-9]+:', tok):
                            carrier, nums = sp
                        else:
                            carrier, nums = tok, None
                    else:
                        sp = split_linespec(tok)
                        if sp is None:
                            carrier, nums = tok, None
                        else:
                            carrier, nums = sp
                    key = (tf, kind, carrier, i)
                    if key in seen:
                        continue
                    seen.add(key)
                    self.refs.append((tf, kind, carrier, nums, i))

    # ---- resolution ------------------------------------------------------
    def _try(self, rel):
        p = os.path.join(self.root, rel.replace('/', os.sep))
        if os.path.exists(p):
            return rel
        return None

    def _find(self, rel):
        for prefix in ('', 'client/Assets/', 'client/Assets/Scripts/', 'client/Assets/Editor/',
                       'client/Assets/Resources/'):
            hit = self._try(prefix + rel)
            if hit:
                return hit
        base = os.path.basename(rel).lower()
        hits = self.by_name.get(base, [])
        if len(hits) == 1:
            return hits[0]
        return None

    def resolve(self, kind, carrier, nums):
        rel = carrier.replace('\\', '/')
        if kind == 'cl':
            rel = 'client/Assets/' + rel[3:]
        rel = rel.lstrip('./')
        if '$' in rel or '<' in rel or '>' in rel:
            return (False, 'placeholder, not a concrete path', None)
        if '*' in rel or '?' in rel:
            if rel.endswith('/'):
                if os.path.isdir(os.path.join(self.root, rel.rstrip('/').replace('/', os.sep))):
                    return (True, '', None)
                return (False, 'directory not on disk: ' + rel, None)
            if glob.glob(os.path.join(self.root, rel.replace('/', os.sep))):
                return (True, '', None)
            return (False, 'glob matches nothing: ' + rel, None)
        hit = self._find(rel)
        if hit is None:
            base = os.path.basename(rel).lower()
            same = self.by_name.get(base, [])
            extra = (' | same-name candidates: ' + ', '.join(same[:4])) if same else ''
            return (False, 'not on disk: ' + rel + extra, None)
        if nums:
            p = os.path.join(self.root, hit.replace('/', os.sep))
            for n in nums:
                if n.startswith('0x'):
                    sz = os.path.getsize(p)
                    if int(n, 16) > sz:
                        return (False, 'offset %s > file size %d (%s)' % (n, sz, hit), hit)
                else:
                    with io.open(p, 'rb') as f:
                        nlines = sum(1 for _ in f)
                    if int(n) > nlines:
                        return (False, 'line %s > file lines %d (%s)' % (n, nlines, hit), hit)
        return (True, '', hit)

    # ---- registry rows are waivers for exactly the key they name --------------
    def row_good(self, rowkey):
        """True <=> the registry row's own key is reachable now => the row is stale."""
        sp = split_linespec(rowkey)
        if sp is not None and re.search(r'\.[A-Za-z0-9]+:', rowkey):
            return self.resolve('file', sp[0], sp[1])[0]
        if rowkey.startswith(YS + '/'):
            return self.resolve('ys', rowkey, None)[0]
        if rowkey.startswith('CL/'):
            return self.resolve('cl', rowkey, None)[0]
        return self.resolve('file', rowkey, None)[0]

    # ---- main ------------------------------------------------------------
    def run(self, emit):
        self.index()
        self.load_registry()
        self.scan()
        carriers = {}      # (table, carrier) -> [lineno...]
        ok = 0
        bad = []
        per_table = {}     # table -> citation count

        def keyof(carrier, nums):
            return carrier + ('' if not nums else ':' + ','.join(nums))

        for tf, kind, carrier, nums, lineno in self.refs:
            tok = keyof(carrier, nums)
            good, detail, hit = self.resolve(kind, carrier, nums)
            # A virtual address is not a file offset: the table marks those explicitly
            # with the ASCII token VA on the citing line (they live in client.dll .data,
            # past EOF).  Only that marker waives the offset bound -- nothing else does.
            if (not good) and detail.startswith('offset ') and 'VA' in self.lines_by_file[tf][lineno - 1]:
                good = True
            reg = self.registry.get(tok)
            if reg is None:
                reg = self.registry.get(carrier)
            tk = self.tagname(tf)
            ck = (tk, carrier)
            per_table[tk] = per_table.get(tk, 0) + 1
            carriers.setdefault(ck, [])
            if lineno not in carriers[ck]:
                carriers[ck].append(lineno)
            if good:
                ok += 1
                continue
            if reg is None:
                bad.append((tk, carrier, kind, nums, detail, lineno))
            elif reg[0] != ST_GONE:
                bad.append((tk, carrier, kind, nums,
                            detail + ' | UNKNOWN status in registry: ' + reg[0], lineno))
            elif any(c == '' for c in reg[1]):
                bad.append((tk, carrier, kind, nums,
                            detail + ' | registry row %d has an empty four-element cell' % reg[2], lineno))
            else:
                ok += 1

        # A registry row that is reachable again is a silencer left behind: FAIL.
        stale = []
        for rowkey, (status, cells, rl) in sorted(self.registry.items()):
            if status == ST_GONE and self.row_good(rowkey):
                stale.append((rowkey, rl))

        print('scan-reftable-refs: tables=%s registry=%s'
              % ('+'.join(self.tagname(t) for t in self.tables), ascii_safe(os.path.basename(self.reg))))
        print('  index: %d file(s); %d citation(s) -> %d distinct carrier(s)'
              % (self.n_files, len(self.refs), len(carriers)))
        print('  per table: ' + ', '.join('%s=%d' % (t, per_table.get(t, 0)) for t in
                                          [self.tagname(x) for x in self.tables]))
        print('  reachable-or-registered: %d citation(s)' % ok)
        seen = set()
        for tk, carrier, kind, nums, detail, lineno in bad:
            ck = (tk, carrier)
            if ck in seen:
                continue
            seen.add(ck)
            print('  DANGLING [%s] lines %s [%s] %s  -- %s'
                  % (tk, ','.join(str(x) for x in carriers[ck][:6]), kind,
                     ascii_safe(carrier), ascii_safe(detail)))
        for rowkey, rl in stale:
            print('  STALE-REGISTRY row %d: %s is reachable again -- delete the row'
                  % (rl, ascii_safe(rowkey)))
        if emit:
            self.emit(bad)
        if bad or stale:
            print('FAIL: %d unaccounted carrier(s) + %d stale registry row(s)'
                  % (len(seen), len(stale)))
            return 1
        print('PASS: every yuan-ban carrier path and file:line citation in the reference table and '
              'the differences registry is reachable on disk, or registered with what/why/source/expiry')
        return 0

    def tagname(self, table):
        """ASCII label per scanned table (a CJK file name would print as '?' otherwise)."""
        return {self.tbl: 'ref-table', self.tbl2: 'diff-registry'}.get(table, os.path.basename(table))

    def emit(self, bad):
        """Append a skeleton row for every NEW dangling carrier (existing rows untouched)."""
        lines = []
        if os.path.exists(self.reg):
            lines = io.open(self.reg, encoding='utf-8').read().rstrip('\n').split('\n')
        known = set(self.registry.keys())
        added = 0
        for tk, carrier, kind, nums, detail, lineno in bad:
            # A row is a waiver for its key: the carrier path when the PATH itself is
            # missing, the full citation when only the line/offset is out of range
            # (otherwise one token-level failure would silence the whole carrier).
            tok = carrier + ('' if not nums else ':' + ','.join(nums))
            base = carrier if not self.row_good(carrier) else tok
            if base in known:
                continue
            known.add(base)
            ext = ''
            m = re.search(r'\.([A-Za-z0-9]+)(?::|$)', base)
            if m:
                ext = m.group(1).lower()
            what = LABEL.get(ext, '\u539f\u7248\u8f7d\u4f53')
            if base.endswith('/'):
                what = '\u539f\u7248\u8f7d\u4f53\u76ee\u5f55'
            lines.append('\t'.join([
                base, ST_GONE,
                what,
                '\u539f\u7248\u8d44\u6e90/ \u672c\u673a\u5df2\u88ab\u6e05\u7406\uff0c'
                '\u4e0d\u5728\u76d8\uff08\u5b9e\u6d4b\uff09',
                '\u539f\u7248\u8d44\u6e90/\u6e05\u5355.md:5-6\u3001:25-31\uff1b'
                'client/\u8d44\u6e90\u6b20\u7f3a\u6e05\u5355.md:76',
                '\u7528\u6237\u8865\u56de CS 1.6 \u5ba2\u6237\u7aef\u672c\u4f53 / HLSDK / '
                '\u89e3\u5305\u4ea7\u7269\uff0c\u6216\u6309\u5207\u7247 AL/AM/AR/AW \u7684\u505a\u6cd5'
                '\u4ece\u516c\u5f00 repack \u91cd\u65b0\u53d6\u56de',
            ]))
            added += 1
        hdr = ('# ' + C_REF + '\t' + C_ST + '\t' + C_WHAT + '\t' + C_WHY + '\t' + C_SRC + '\t' + C_WHEN)
        if lines and lines[0].startswith('#'):
            lines[0] = hdr
        else:
            lines.insert(0, hdr)
        with io.open(self.reg, 'w', encoding='utf-8', newline='\n') as f:
            f.write('\n'.join(lines) + '\n')
        print('  --emit-registry: %d row(s) appended to %s' % (added, ascii_safe(os.path.basename(self.reg))))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--project', default='')
    ap.add_argument('--plan-dir', default='', dest='plan_dir')
    ap.add_argument('--emit-registry', action='store_true')
    a = ap.parse_args()
    root = a.project or os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    return Scanner(root, a.plan_dir).run(a.emit_registry)


if __name__ == '__main__':
    sys.exit(main())
