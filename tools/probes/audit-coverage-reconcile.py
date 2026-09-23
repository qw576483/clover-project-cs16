# -*- coding: utf-8 -*-
"""audit-coverage-reconcile.py -- reconcile the coverage matrix against its own sources.

WHY THIS EXISTS
---------------
`patterns/full-coverage-audit.md` section 7 asks for a `coverage-rows` gate that judges the
COVERAGE RELATION (every enumerated entity has >=1 verdict row; every verdict row points at
an enumerated entity), not a bare row-count equality.  Section 6 (`game-delivery` clause 2)
additionally wants "the number you checked against" to be a NUMBER, produced by a script.

Nothing in `tools/verify.ps1` currently checks:
  * entity-set identity between `plan/entity-list.tsv` and the coverage block inside
    `plan/acceptance.md` (row COUNT alone can match while the sets differ);
  * that every "allowed difference" row carries the 4 mandatory fields and that every
    difference id referenced by the coverage block exists in `plan/diff-registry.tsv`;
  * that the whole `state-matrix.tsv` is non-empty per dimension;
  * that the `evidence` column of a verdict row actually contains a reachable anchor
    (a `path:line` or at least a file token) instead of prose.

This script is READ-ONLY on the plan files (it never regenerates them - the generator is
`enumerate-entities.py`, whose output must not be clobbered while other slices are editing
the same files).

Run:  python tools/probes/audit-coverage-reconcile.py
Exit 0 = PASS, 1 = FAIL.  ASCII-only stdout (cp936 console safe).

Optional: -o <path> writes a machine-readable TSV of every finding.
"""

import io
import os
import re
import sys
import collections

sys.dont_write_bytecode = True

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
PLAN = os.path.join(ROOT, '\u7b56\u5212')          # plan/
# --plan <dir>: read the five plan files from another directory.  Added for the gate's
# own two-sample self-test (reference/anti-gaming.md section 5): the coverage relation
# must be judged on a KNOWN-GOOD sample too, and the project's real data is the
# known-bad one.  Default is unchanged, so the project gate keeps judging the real plan.
if '--plan' in sys.argv:
    PLAN = sys.argv[sys.argv.index('--plan') + 1]
F_ENT = '\u5b9e\u4f53\u6e05\u5355.tsv'             # entity-list.tsv
F_STA = '\u72b6\u6001\u77e9\u9635.tsv'             # state-matrix.tsv
F_DIF = '\u5dee\u5f02\u767b\u8bb0.tsv'             # diff-registry.tsv
F_ACC = '\u9a8c\u6536\u8868.md'                    # acceptance.md
F_FRG = '\u8986\u76d6\u77e9\u9635\u5224\u5b9a.fragment.md'
COV_BEGIN = '<!-- COVERAGE-BEGIN -->'
COV_END = '<!-- COVERAGE-END -->'

DIMS = ['D%d' % i for i in range(1, 13)] + ['S1', 'S2', 'S3']
CONSISTENT = '\u4e00\u81f4'                        # "consistent"
INCONSISTENT = '\u4e0d\u4e00\u81f4'                # "inconsistent"
ALLOWED = '\u5141\u8bb8\u7684\u5dee\u5f02'         # "allowed difference"

ROW_RE = re.compile(r'^\|\s*(\d+)\s*\|')
FIELD_RE = re.compile(r'^\|\s*\d+\s*\|')


def read(path):
    with io.open(path, encoding='utf-8', errors='replace') as f:
        return f.read()


def ascii_(s):
    return s.encode('ascii', 'backslashreplace').decode('ascii')


def cells(line):
    return [c.strip() for c in line.split('|')]


def tcols(line):
    """Columns of a generated TSV data row (tab separated)."""
    return [c.strip() for c in line.split('\t')]


def tsv_rows(text):
    """Data rows of a generated TSV (leading '# header' line skipped)."""
    out = []
    for ln in text.split('\n'):
        if not ln.strip():
            continue
        if ln.startswith('#'):
            continue
        out.append(ln)
    return out


def main():
    findings = []   # (severity, kind, detail)
    problems = 0

    def bad(kind, detail):
        findings.append(('FAIL', kind, detail))

    # ---------------------------------------------------------------- load
    for name in (F_ENT, F_STA, F_DIF, F_ACC, F_FRG):
        if not os.path.exists(os.path.join(PLAN, name)):
            print('FAIL: missing ' + ascii_(os.path.join(PLAN, name)))
            return 1

    ent = tsv_rows(read(os.path.join(PLAN, F_ENT)))
    sta = tsv_rows(read(os.path.join(PLAN, F_STA)))
    dif = tsv_rows(read(os.path.join(PLAN, F_DIF)))
    acc = read(os.path.join(PLAN, F_ACC))
    frg = read(os.path.join(PLAN, F_FRG))

    # ------------------------------------------------- 1) judge rows source
    i, j = acc.find(COV_BEGIN), acc.find(COV_END)
    if i < 0 or j < 0:
        bad('coverage-block', 'acceptance.md carries no COVERAGE-BEGIN/END block')
        acc_judge = []
    else:
        acc_judge = [ln for ln in acc[i:j].split('\n') if ROW_RE.match(ln)]
    frg_judge = [ln for ln in frg.split('\n') if ROW_RE.match(ln)]

    print('entity-list rows      = %d' % len(ent))
    print('state-matrix rows     = %d' % len(sta))
    print('diff-registry rows    = %d' % len(dif))
    print('judge rows (fragment) = %d' % len(frg_judge))
    print('judge rows (accepted) = %d' % len(acc_judge))
    if len(acc_judge) != len(frg_judge):
        bad('coverage-block',
            'acceptance.md block has %d judge rows but fragment has %d'
            % (len(acc_judge), len(frg_judge)))

    # ------------------------------------------------- 2) entity-set identity
    def entity_of(line, col):
        cs = cells(line)
        return cs[col] if len(cs) > col else ''

    ent_names = set()
    for ln in ent:
        cs = tcols(ln)
        if len(cs) >= 3:
            ent_names.add(cs[1])
    judge_names = set()
    for ln in frg_judge:
        cs = cells(ln)
        if len(cs) >= 4:
            judge_names.add(cs[3])
    only_ent = sorted(ent_names - judge_names)
    only_judge = sorted(judge_names - ent_names)
    print('entity names: list=%d judge=%d unjudged=%d phantom=%d'
          % (len(ent_names), len(judge_names), len(only_ent), len(only_judge)))
    if only_ent:
        bad('coverage-rows', '%d listed entity(ies) have no verdict row' % len(only_ent))
        # bounded drift list: the red must be actionable (which names drifted), and it is
        # what the data-side slice needs. Print every name when few, else the first 10.
        for n in only_ent[:10]:
            print('    unjudged: ' + ascii_(n))
        if len(only_ent) > 10:
            print('    ... %d more' % (len(only_ent) - 10))
    if only_judge:
        bad('coverage-rows', '%d verdict row(s) point at unlisted entity(ies)' % len(only_judge))
        for n in only_judge[:10]:
            print('    phantom : ' + ascii_(n))
        if len(only_judge) > 10:
            print('    ... %d more' % (len(only_judge) - 10))

    # duplicate verdict rows for the same entity is legal (entity x state) - only flag
    # an entity that appears >1 time with the SAME (verdict, evidence) pair (copy-paste).
    dup = collections.Counter()
    for ln in frg_judge:
        cs = cells(ln)
        if len(cs) >= 7:
            dup[(cs[3], cs[5], cs[6])] += 1
    clones = sum(v - 1 for v in dup.values() if v > 1)
    if clones:
        bad('coverage-rows', '%d verdict row(s) are exact clones of another row' % clones)

    # ------------------------------------------------- 3) verdict / evidence
    verdicts = collections.Counter()
    empty_verdict = empty_ev = no_anchor = 0
    nonconsistent = []
    for ln in frg_judge:
        cs = cells(ln)
        if len(cs) < 7:
            bad('coverage-filled', 'malformed verdict row: ' + ascii_(ln[:40]))
            continue
        v, ev = cs[5], cs[6]
        if not v:
            empty_verdict += 1
        elif v.startswith(ALLOWED):
            verdicts['allowed'] += 1
        elif v.startswith(INCONSISTENT):
            verdicts['inconsistent'] += 1
            nonconsistent.append(ln)
        elif v.startswith(CONSISTENT):
            verdicts['consistent'] += 1
        else:
            verdicts['other'] += 1
        if not ev:
            empty_ev += 1
        elif not re.search(r':\d+', ev) and not re.search(
                r'\.(png|jpg|bmp|tsv|cs|py|ps1|md|txt|json|mdl|wav|prefab)', ev, re.I):
            no_anchor += 1
    print('verdicts: consistent=%d inconsistent=%d allowed=%d other=%d empty=%d'
          % (verdicts['consistent'], verdicts['inconsistent'], verdicts['allowed'],
             verdicts['other'], empty_verdict))
    print('evidence: empty=%d no-anchor-token=%d' % (empty_ev, no_anchor))
    if empty_verdict:
        bad('coverage-filled', '%d verdict row(s) carry an empty conclusion' % empty_verdict)
    if empty_ev:
        bad('coverage-filled', '%d verdict row(s) carry an empty evidence cell' % empty_ev)
    if no_anchor:
        bad('coverage-evidence',
            '%d verdict row(s) have an evidence cell with no path/anchor token' % no_anchor)
    if verdicts['other']:
        bad('coverage-filled', '%d verdict row(s) use a non-standard conclusion' % verdicts['other'])

    # ------------------------------------------------- 4) dimension coverage
    dimcount = collections.Counter()
    for ln in frg_judge:
        cs = cells(ln)
        if len(cs) >= 3:
            dimcount[cs[2]] += 1
    missing_dims = [d for d in DIMS if dimcount.get(d, 0) == 0]
    print('dimensions covered    = %d of %d' % (len(DIMS) - len(missing_dims), len(DIMS)))
    print('dim row counts        = ' + ' '.join('%s=%d' % (d, dimcount.get(d, 0)) for d in DIMS))
    if missing_dims:
        bad('coverage-dimensions', 'no verdict row for ' + ','.join(missing_dims))

    # ------------------------------------------------- 5) diff registry
    # referenced ids: "#<n>" anywhere in the coverage block, state matrix and acceptance body
    ref = collections.Counter()
    scan = frg + '\n' + read(os.path.join(PLAN, F_STA))
    for m in re.finditer(r'#\s*(\d+)', scan):
        ref[int(m.group(1))] += 1
    reg = set()
    bad_fields = []
    for ln in dif:
        cs = tcols(ln)
        if len(cs) < 2 or not cs[0].isdigit():
            continue
        reg.add(int(cs[0]))
        filled = [c for c in cs[1:5]]
        if len(filled) < 4 or any(c == '' for c in filled):
            bad_fields.append((cs[0], len([c for c in filled if c])))
    ref_ids = set(ref)
    dangling = sorted(ref_ids - reg)
    unused = sorted(reg - ref_ids)
    print('diff ids: registry=%d referenced=%d dangling=%d unreferenced=%d field-incomplete=%d'
          % (len(reg), len(ref_ids), len(dangling), len(unused), len(bad_fields)))
    if dangling:
        bad('coverage-diff', '%d referenced diff id(s) absent from the registry: %s'
            % (len(dangling), ','.join(str(x) for x in dangling[:20])))
    if bad_fields:
        bad('coverage-diff', '%d registry row(s) miss one of the 4 mandatory fields' % len(bad_fields))
    if unused:
        # informational only: a registered difference nobody references is not fatal,
        # but a registry that nothing points at is an un-audited claim.
        findings.append(('WARN', 'coverage-diff',
                         '%d registered diff id(s) are referenced nowhere: %s'
                         % (len(unused), ','.join(str(x) for x in unused[:20]))))

    # ------------------------------------------------- 6) state matrix shape
    sta_dims = collections.Counter()
    for ln in sta:
        cs = tcols(ln)
        if len(cs) >= 1:
            sta_dims[cs[0]] += 1
    sta_missing = [d for d in DIMS if sta_dims.get(d, 0) == 0]
    print('state-matrix dims     = %d of %d (rows/entity ratio %.2f)'
          % (len(DIMS) - len(sta_missing), len(DIMS),
             (len(sta) / float(len(ent))) if ent else 0.0))
    if sta_missing:
        bad('state-matrix', 'state matrix has no row for dimension(s) ' + ','.join(sta_missing))

    # ------------------------------------------------- report
    for sev, kind, detail in findings:
        if sev == 'FAIL':
            problems += 1
        print('  %-4s %-20s %s' % (sev, kind, ascii_(detail)))

    out = sys.argv[sys.argv.index('-o') + 1] if '-o' in sys.argv else None
    if out:
        with io.open(out, 'w', encoding='utf-8', newline='\n') as f:
            f.write('# severity\tkind\tdetail\n')
            for sev, kind, detail in findings:
                f.write('%s\t%s\t%s\n' % (sev, kind, detail.replace('\t', ' ').replace('\n', ' ')))
        print('findings written to ' + ascii_(out))

    if problems:
        print('RESULT: FAIL -- %d blocking finding(s)' % problems)
        return 1
    print('RESULT: PASS -- coverage matrix reconciles with entity list / diff registry')
    return 0


if __name__ == '__main__':
    sys.exit(main())
