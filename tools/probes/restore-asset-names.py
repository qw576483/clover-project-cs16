# -*- coding: utf-8 -*-
"""restore-asset-names.py -- undo the `_s()` asset-name mangling in the generated plan tables.

WHY THIS EXISTS (slice BW-G phase 2, main-agent ruling 2026-09-22)
-----------------------------------------------------------------
`tools/probes/enumerate-entities.py` (see `_s()` around line 1853 and its single call site
around line 1861) deliberately rewrites every `.png` in three generated columns as
`.png ` (a space in FRONT of the extension):

    def _s(t): return str(t).replace('|', '/').replace('.png', ' .png')

Its comment says the motive is to stop the `screenshot-refs` gate from treating MAP
TEXTURE ASSET NAMES (D1 rows such as `MltryCrteSd.png`, which live under Assets/) as
evidence-screenshot citations. That is textbook gate-gaming (`reference/anti-gaming.md`:
the metric became the target), and it has two measured costs:

  * the coverage relation broke -- the entity list keeps `MltryCrteSd.png` while the
    verdict row said `MltryCrteSd .png`, so those rows were never actually reconciled;
  * REAL evidence-screenshot citations inside the §G EVIDENCE column were mangled too
    (`contact-sheet-4-menu .png` x144, `-5-options .png` x89, ...) -- i.e. the mangling
    also hid citations that the gate is supposed to resolve.

The correct fix is on the GATE side (exempt asset names, keep judging evidence shots);
removing the mangling is the other half. This script does the DATA side of that pair, and
it is deliberately read-only unless `--apply` is given.

Judgement-relevant properties
-----------------------------
  * idempotent: a second run reports 0 changes (a repair you cannot re-run is not a repair);
  * counted: it prints drift counts BEFORE and AFTER, per file and per region (the
    acceptance table's COVERAGE-BEGIN/END block vs the hand-written body), so the numbers
    in the report can be re-derived mechanically;
  * backed up: `--apply` first copies each target next to a backup dir;
  * narrow: only `<name-char><space>.png` is touched (that is exactly the shape the
    generator writes), never a bare `.png` or an already-canonical name.

Usage
-----
  python tools/probes/restore-asset-names.py                     # dry run, prints counts
  python tools/probes/restore-asset-names.py --plan <dir>        # dry run on another dir
  python tools/probes/restore-asset-names.py --apply --backup-dir <dir>

Exit 0 = dry run finished / apply finished; 1 = a target file is missing.
ASCII-only stdout (cp936 console safe).
"""

import io
import os
import re
import sys

sys.dont_write_bytecode = True

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
PLAN = os.path.join(ROOT, '\u7b56\u5212')                                   # plan/
F_ACC = '\u9a8c\u6536\u8868.md'                                             # acceptance table
F_FRG = '\u8986\u76d6\u77e9\u9635\u5224\u5b9a.fragment.md'                   # coverage fragment
B, E = '<!-- COVERAGE-BEGIN -->', '<!-- COVERAGE-END -->'

# the exact shape the generator writes: one name character, one space, then `.png`
DRIFT = re.compile(r'([A-Za-z0-9_\-]) \.png')

TARGETS = [F_ACC, F_FRG]


def apply_overrides(argv):
    global PLAN
    if '--plan' in argv:
        PLAN = argv[argv.index('--plan') + 1]


def read(p):
    with io.open(p, encoding='utf-8', errors='replace') as f:
        return f.read()


def counts(text):
    """(total, inside the coverage block, outside it) drift-name counts."""
    inside = 0
    outside = 0
    i, j = text.find(B), text.find(E)
    if i >= 0 and j > i:
        inside = len(DRIFT.findall(text[i:j]))
        outside = len(DRIFT.findall(text[:i])) + len(DRIFT.findall(text[j:]))
    else:
        outside = len(DRIFT.findall(text))
    return inside + outside, inside, outside


def main():
    argv = sys.argv[1:]
    apply_overrides(argv)
    do_apply = '--apply' in argv
    backup = argv[argv.index('--backup-dir') + 1] if '--backup-dir' in argv else None
    rc = 0
    for name in TARGETS:
        p = os.path.join(PLAN, name)
        if not os.path.exists(p):
            print('FAIL: missing ' + p.encode('ascii', 'backslashreplace').decode('ascii'))
            rc = 1
            continue
        txt = read(p)
        t0, in0, out0 = counts(txt)
        new = DRIFT.sub(lambda m: m.group(1) + '.png', txt)
        t1, in1, out1 = counts(new)
        print('%-42s drift before=%4d (block=%4d body=%4d) -> after=%4d'
              % (name.encode('ascii', 'backslashreplace').decode('ascii'), t0, in0, out0, t1))
        if t0 == 0 and t1 == 0:
            continue
        if t1 != 0:
            print('  WARN: %d drift name(s) survived the rewrite -- inspect' % t1)
        if do_apply:
            if backup:
                if not os.path.isdir(backup):
                    os.makedirs(backup)
                with io.open(os.path.join(backup, name), 'w', encoding='utf-8', newline='') as f:
                    f.write(txt)
            with io.open(p, 'w', encoding='utf-8', newline='') as f:
                f.write(new)
            print('  applied; backup in ' + (backup if backup else '(none requested)'))
            # re-read to prove the write landed and is idempotent
            t2, in2, out2 = counts(read(p))
            print('  re-read: drift=%d (block=%d body=%d)' % (t2, in2, out2))
        else:
            print('  dry run (pass --apply to write)')
    return rc


if __name__ == '__main__':
    sys.exit(main())
