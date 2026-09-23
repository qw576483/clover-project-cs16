# -*- coding: utf-8 -*-
"""bwr2-noprobe-selftest.py -- two-direction self-test for the `behaviour-no-probe` caliber.

WHAT IT PINS
------------
`behaviour-no-probe` used to be accumulated PER LINE, so a verdict row that a real probe DID hit
landed in `hit_ids` AND `noprobe_ids` (measured on the real table 2026-09-23: 24 rows, i.e.
124 printed vs 103 correct).  The fix aggregates a row's lines first and then judges the row.

A SAMPLE MUST PROVE IT HAS DISCRIMINATING POWER (rule `R-样本须证判别力`, 2026-09-23): a negative
sample that produces the SAME number under the broken and the fixed criterion has tested nothing.
The row arithmetic that makes the difference non-zero:
    old noprobe = bare-only rows + DOUBLE-counted rows
    new noprobe = bare-only rows + unresolved-only rows
    => difference = |double-counted| - |unresolved-only|
so a fixture only discriminates when those two counts DIFFER.  Measured with this fixture:
    POSITIVE   (rows 1,2 double-counted) old=4 new=3   -> discriminating
    NEGATIVE-A (row 1's probe= line dropped) old=4 new=4 -> NOT discriminating BY COUNT;
               it is judged by the invariant + the partition identity instead (see the check name)
    NEGATIVE-B (both probe= lines dropped) old=4 new=5  -> discriminating
Both numbers above are produced by `bwr2-noprobe-fixture-preview.py` (kept under .ai-tmp/test),
using the criterion module's own helpers -- no second criterion is invented here.

The rule the fix implements is EXISTS semantics: a row is HIT as soon as ANY carrier has an ok
line, so "no real probe" means EVERY carrier's view of it is not a real probe.  The old behaviour
was a FALSE RED (it reported a missing real probe although one already existed) -- the mirror of a
false green -- and that framing is asserted to be present in the criterion source too.

ANTI-LOOSENING is asserted separately from the fix: a row that merely APPEARS in a carrier but is
never ok must STILL count as no-probe; with the fixture rows 3,4 (present only in the bare carrier)
the positive case must print 3, not 0.

It also mechanically checks that the traceability notes landed in the criterion:
  * the caliber correction 124 (line level) -> 103 (row-set level);
  * the EXISTS semantics + the false-red/false-green mirror;
  * the sandbox lesson (a one-carrier sandbox hides this class, so a self-test must reproduce the
    real table's carrier composition);
  * the D8 known gap (BEH_DIMS excludes the 123 sound-effect rows; adding D8 without probes first
    would manufacture 123 unresolvable false reds);
  * noprobe is computed as a ROW SET, not by `noprobe_ids -= hit_ids`.

Run:
  python tools/probes/bwr2-noprobe-selftest.py
Exit code: 0 when every expectation holds, 1 otherwise.  ASCII-only stdout.
"""

import io
import os
import shutil
import subprocess
import sys

sys.stdout.reconfigure(encoding='utf-8', errors='replace')
sys.dont_write_bytecode = True

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
CRIT = os.path.join(HERE, 'audit-verdict-rows.py')
FIX = os.path.join(ROOT, '.ai-tmp', 'test', 'bwr2-noprobe-fixture')
PLAN = os.path.join(FIX, 'plan')
CAR = os.path.join(FIX, 'carriers')
OUT_DIR = os.path.join(ROOT, '.ai-tmp', 'test')

FRAG = (u'| # | \u7ef4\u5ea6 | \u5b9e\u4f53 | \u5224\u636e\u7c7b\u578b | \u7ed3\u8bba | \u8bc1\u636e |\n'
        u'|---|---|---|---|---|---|\n'
        u'| 1 | D11 | fixture-double-1 | \u811a\u672c\u65ad\u8a00 | \u4e00\u81f4 | fixture |\n'
        u'| 2 | D11 | fixture-double-2 | \u811a\u672c\u65ad\u8a00 | \u4e00\u81f4 | fixture |\n'
        u'| 3 | D6 | fixture-bare-1 | \u811a\u672c\u65ad\u8a00 | \u4e00\u81f4 | fixture |\n'
        u'| 4 | D12 | fixture-bare-2 | \u811a\u672c\u65ad\u8a00 | \u4e00\u81f4 | fixture |\n'
        u'| 5 | D9 | fixture-unres | \u811a\u672c\u65ad\u8a00 | \u4e00\u81f4 | fixture |\n')

GOOD = {1: '1\tF1:fixture/a.cs:1\tprobe=fixture-scan(static) measured=tokens=1 via=direct',
        2: '2\tF1:fixture/b.cs:2\tprobe=fixture-scan(static) measured=tokens=2 via=direct'}
BARE = ['1\tF1:fixture/a.cs:1\tline=1 hash=aaa',
        '2\tF1:fixture/b.cs:2\tline=2 hash=bbb',
        '3\tF1:fixture/c.cs:3\tline=3 hash=ccc',
        '4\tF1:fixture/d.cs:4\tline=4 hash=ddd']
UNRES = ['5\t--\tunresolved=1']


def write(path, text):
    d = os.path.dirname(path)
    if not os.path.isdir(d):
        os.makedirs(d)
    with io.open(path, 'w', encoding='utf-8', newline='\n') as f:
        f.write(text)


def read(path):
    with io.open(path, encoding='utf-8', errors='replace') as f:
        return f.read()


def data_lines(path):
    return [ln.rstrip('\n') for ln in read(path).split('\n') if ln.strip() and not ln.startswith('#')]


def build(good_rows):
    """good_rows = subset of {1,2} whose `probe=` line is present in hits-a.tsv."""
    if os.path.isdir(FIX):
        shutil.rmtree(FIX)
    write(os.path.join(PLAN, u'\u8986\u76d6\u77e9\u9635\u5224\u5b9a.fragment.md'), FRAG)
    header = '# probe-hits plan=' + os.path.normpath(PLAN) + '\n'
    a = [GOOD[i] for i in sorted(good_rows)]
    write(os.path.join(CAR, 'hits-a.tsv'), header + ('\n'.join(a) + '\n' if a else ''))
    write(os.path.join(CAR, 'hits-b.tsv'), header + '\n'.join(BARE) + '\n')
    write(os.path.join(CAR, 'hits-c.tsv'), header + '\n'.join(UNRES) + '\n')


def run_criterion():
    p = subprocess.Popen([sys.executable, '-X', 'utf8', CRIT, '--plan', PLAN,
                          '--probe-roots', CAR],
                         stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    out, _ = p.communicate()
    return out.decode('utf-8', 'replace')


def grab(out, prefix):
    for ln in out.split('\n'):
        if ln.startswith(prefix):
            return ln
    return ''


def value_of(out, prefix):
    ln = grab(out, prefix)
    if not ln:
        return None
    try:
        return int(ln.split('=', 1)[1].split()[0])
    except (IndexError, ValueError):
        return None


def invariant_lines(out):
    return [ln for ln in out.split('\n') if ln.startswith('invariant ')]


def all_invariants_ok(out):
    lines = invariant_lines(out)
    return bool(lines) and all('= OK' in ln for ln in lines)


def main():
    checks = []

    # ---- POSITIVE: rows 1,2 carry BOTH a probe= line and a bare line --------------------
    build((1, 2))
    a_before = data_lines(os.path.join(CAR, 'hits-a.tsv'))
    out = run_criterion()
    n = value_of(out, 'behaviour-no-probe')
    inv = invariant_lines(out)
    detail = ('behaviour-no-probe = %s\ninvariant lines:\n%s' % (n, '\n'.join('  ' + x for x in inv)))
    checks.append(('POSITIVE (EXISTS semantics): a row ok in carrier A and NOT ok in carrier B is '
                   'NOT no-probe -- "no real probe" means EVERY carrier lacks one (expect 3, old '
                   'caliber 4)', n == 3, detail))
    checks.append(('POSITIVE: hit_ids & noprobe_ids = 0 and the partition identity holds',
                   all_invariants_ok(out) and any('5 = 2 + 3' in x for x in inv), detail))
    checks.append(('ANTI-LOOSENING: a row that merely APPEARS in a carrier but is never ok still '
                   'counts as no-probe (rows 3,4 appear in the bare carrier; expect 3 -- an '
                   '"appeared => exempt" rule would print 0)',
                   n == 3, detail))

    # ---- NEGATIVE-A: row 1's probe= line removed (NOT count-discriminating by design) ---
    build((2,))
    a_after = data_lines(os.path.join(CAR, 'hits-a.tsv'))
    print('injection proof NEGATIVE-A: hits-a.tsv data lines %d -> %d (changed=%s)'
          % (len(a_before), len(a_after), len(a_before) != len(a_after)))
    print('  before: %s' % a_before)
    print('  after : %s' % a_after)
    out = run_criterion()
    n = value_of(out, 'behaviour-no-probe')
    inv = invariant_lines(out)
    detail = ('behaviour-no-probe = %s\ninvariant lines:\n%s' % (n, '\n'.join('  ' + x for x in inv)))
    checks.append(('NEGATIVE-A: row 1 now IS in noprobe -- judged by the invariant + partition '
                   'identity, because the COUNT alone is 4 under BOTH calibers (expect 4 and '
                   '"5 = 1 + 4"; the old code prints no invariant line at all)',
                   n == 4 and all_invariants_ok(out) and any('5 = 1 + 4' in x for x in inv), detail))

    # ---- NEGATIVE-B: both probe= lines removed -> every behaviour row must be in noprobe -
    build(())
    a_after2 = data_lines(os.path.join(CAR, 'hits-a.tsv'))
    print('injection proof NEGATIVE-B: hits-a.tsv data lines %d -> %d (changed=%s)'
          % (len(a_after), len(a_after2), len(a_after) != len(a_after2)))
    out = run_criterion()
    n = value_of(out, 'behaviour-no-probe')
    inv = invariant_lines(out)
    detail = ('behaviour-no-probe = %s\ninvariant lines:\n%s' % (n, '\n'.join('  ' + x for x in inv)))
    checks.append(('NEGATIVE-B: with zero ok hits EVERY behaviour row is in noprobe '
                   '(expect 5, old caliber 4)', n == 5 and all_invariants_ok(out), detail))

    # ---- the two traceability notes must be in the criterion source ---------------------
    src = read(CRIT)
    checks.append(('traceability: the 124 -> 103 caliber correction is recorded',
                   '124 -> 103' in src and 'CALIBER CORRECTION' in src, ''))
    checks.append(('traceability: the D8 known gap + the "prepare the probe first" warning is recorded',
                   "add 'D8' to BEH_DIMS" in src and '123' in src, ''))
    checks.append(('traceability: order dependence named as a reason for aggregate-first',
                   'ORDER DEPENDENCE' in src, ''))
    checks.append(('traceability: the EXISTS semantics + the false-red/false-green mirror is named',
                   'EXISTS SEMANTICS' in src and 'FALSE RED' in src, ''))
    checks.append(('traceability: the sandbox-carrier-composition lesson is recorded',
                   'SANDBOX NOTE' in src and 'carrier composition' in src, ''))
    checks.append(('traceability: noprobe is computed as a row set, NOT as `noprobe_ids -= hit_ids`',
                   'r[0] not in hit_ids' in src, ''))
    checks.append(('traceability: "net is a COMPOSITE, report ADDED/REMOVED/net separately" recorded',
                   'NET IS NOT A COMPONENT' in src and 'REMOVED 24' in src and 'ADDED 3' in src, ''))

    if not os.path.isdir(OUT_DIR):
        os.makedirs(OUT_DIR)

    ok = 0
    for name, passed, detail in checks:
        if passed:
            ok += 1
            print('OK   %s' % name)
        else:
            print('FAIL %s' % name)
            for ln in (detail or '').split('\n'):
                if ln.strip():
                    print('       | %s' % ln)
    print('===== bwr2-noprobe-selftest summary: %d/%d expectations met =====' % (ok, len(checks)))
    return 0 if ok == len(checks) else 1


if __name__ == '__main__':
    sys.exit(main())
