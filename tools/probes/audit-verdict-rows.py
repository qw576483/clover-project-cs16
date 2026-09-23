# -*- coding: utf-8 -*-
"""audit-verdict-rows.py -- the two anti-gaming judgements over the coverage matrix.

WHY THIS EXISTS
---------------
`reference/anti-gaming.md` section 3 ("judge the process, not the result") and the
template items 23 / 25 ask for two things that `tools/verify.ps1` did not judge:

  * item 23 `evidence-anchor` -- every verdict row's EVIDENCE cell must carry at least one
    CHECKABLE anchor: a pointer a third party can re-open and re-read.  Without this the
    English column is just prose ("referenced (guid/short-name hit ...)") and a broken
    reference chain turns no row red.
  * item 25 `coverage-hit` -- every verdict row must be HIT BY ROW ID inside a probe
    output.  Row-count equality is NOT the criterion: padding both sides until the numbers
    match is far cheaper than actually judging the rows (measured on this project:
    `3798 == 3798` was PASS while 350 entity names had drifted and were never judged).

SECTION A -- `evidence-anchor`
------------------------------
Accepted anchor forms (a row is anchored iff ONE of them holds):
  F1  <path>:<line>   the file exists (resolved against the project root, then the plan
                      dir under judgement) and, for a text file, the line is in range
  F2  guid=<32 hex>   that guid is the guid of a `.meta` inside the project (client/**)
  F3  a path token with an artifact extension that exists on disk -- in backticks OR bare;
                      a BARE file name (no directory part) also resolves against the
                      evidence roots (.ai-tmp/screenshots, .ai-tmp/test, tools/probes), the
                      same way the shot-reference item resolves a cited png name
  F4  a numeric measurement AND a path token that exists on disk (the
                      "verts=95 tris=51 ... client/Assets/Scenes/StageDust2.unity" shape)
WHY the bare form is accepted alongside the backticked one: the real table carries
`client/Assets/Scenes/StageDust2.unity` with no backticks at all; requiring backticks would
report a perfectly reachable anchor as missing => a FALSE RED, and a check that cries wolf
is worse than no check (SKILL: template item 11's lesson).
WHY prose is not an anchor: "referenced (guid/short-name hit)" names no artifact, so nobody
can re-run it.  L1 text is not evidence (SKILL 4 item 7: evidence must be anchored+graded).

SECTION B -- `coverage-hit`
---------------------------
A CARRIER is a file under the probe roots whose FIRST line declares

    # probe-hits plan=<the plan dir this ledger belongs to>

and a HIT is a following data line whose FIRST tab-separated field is the row id.

WHY the declaration + the plan-dir binding (both halves are load-bearing):
  * "any file that merely contains this number" would be satisfied by the acceptance table
    itself, by a saved tool output, or by this script's own report -- i.e. a gate satisfied
    by its own output (the trap gate-selftest.ps1 section 11 documents);
  * binding the carrier to ONE plan dir keeps a SELF-TEST FIXTURE ledger from faking hits
    for the real table: the fixture lives under tools/probes/** (a default probe root), so
    without the binding it would report rows 1..N of the REAL table as hit.
  * AND a hit line that reproduces >= 3 of that row's OWN acceptance cells is rejected as a
    TABLE ECHO (function `table_echo`): a ledger that merely RE-RENDERS the verdict rows
    would otherwise satisfy the item without any probe having run -- the item satisfied by
    its own input (anti-gaming section 8; measured here 2026-09-23, when a re-render of the
    coverage block landed under tools/probes/ and turned this item green).  A real probe
    output records what the probe OBSERVED (log offset / measured value / scene); it does
    not repeat the entity name + judgement type + verdict strings of the row it exercised.

Run:
  python tools/probes/audit-verdict-rows.py [--plan <dir>] [--probe-roots <d1>;<d2>] [-o <tsv>]
ASCII-only stdout (cp936 console safe).  Exit 0 = both sections clean, 1 = not clean.
"""

import collections
import io
import os
import re
import sys

sys.dont_write_bytecode = True

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
PLAN = os.path.join(ROOT, '\u7b56\u5212')                                   # "ce hua" = plan
F_ACC = '\u9a8c\u6536\u8868' + '.md'                                        # acceptance table
F_FRG = '\u8986\u76d6\u77e9\u9635\u5224\u5b9a' + '.fragment.md'             # coverage fragment
CK_BEGIN = '<!-- COVERAGE-BEGIN -->'
CK_END = '<!-- COVERAGE-END -->'
P_HIT = '# probe-hits plan='

DIMS = ['D%d' % i for i in range(1, 13)] + ['S1', 'S2', 'S3']
# The two classes that behave differently for the hit contract (main-agent ruling 2026-09-23,
# option (b)).  Defined ONCE at module scope so the counting, the reporting and the rule can never
# drift apart -- tonight's recurring lesson: one semantic, one place.
ENUM_DIMS = ['D1', 'D5']                    # the entity enumerator's own output is their ledger
BEH_DIMS = ['D6', 'D7', 'D9', 'D10', 'D11', 'D12', 'S2', 'S3']   # REAL probes required
ANCHOR_NONE = '--'                          # the only legal anchor for an unresolved=1 line
# Measurement SHAPES the producer may emit.  MEASURED on the real ledger 2026-09-23 (P4 landed):
# file_bytes=1612 meta_line=1180 line=1002 unresolved=5 head_bytes=1 => unknown=0.  `bytes=`/`lines=`
# are the pre-P4 enumerator shape, kept so an older-but-valid ledger is not called a degradation.
DECLARED_SHAPES = set('line meta_line file_bytes head_bytes bytes lines unresolved'.split())
ROW_RE = re.compile(r'^\|\s*(\d+)\s*\|')
TOKEN_RE = re.compile(r'`([^`]+)`')

# artifact extensions that count as "a path to something" (lower case, no dot)
ART_EXT = set("""cs py ps1 tsv txt json md csv log cfg res xml shader asmdef
unity prefab asset png jpg jpeg bmp wav ogg mp3 mdl bin mat anim controller meta
cs16mdl cs16anim tga wad bsp spr vbmp ttf otf dds exr hdr xlsx fbq fbx""".split())
# extensions we can count lines in (everything else: existence is the whole check)
TEXT_EXT = set("cs py ps1 tsv txt json md csv log cfg xml shader asmdef".split())

_EXT_ALT = '|'.join(sorted(ART_EXT))
PATHLINE_RE = re.compile(r'([^\s`|"\'(\)\[\],;]+?\.(' + _EXT_ALT + r')):(\d+)', re.I)
BARE_PATH_RE = re.compile(r'([0-9A-Za-z_\-./\\]+\.[A-Za-z0-9_]{1,12})')
GUID_TOK_RE = re.compile(r'guid\s*=\s*([0-9a-fA-F]{32})')
GUID_META_RE = re.compile(r'guid:\s*([0-9a-fA-F]{32})')

_path_cache = {}
_line_cache = {}
_guid_index = None
# Extra roots used to resolve a BARE file name (no directory part).  The real table cites
# its contact sheets as `contact-sheet-4-menu.png` + a cell id, and its probe outputs as
# `bw-facade-usage.tsv`; both are checkable, and flagging them as unanchored would be a
# false red (the same resolution the shot-reference item already uses for png names).
BASENAME_ROOTS = []


def read(path):
    with io.open(path, encoding='utf-8', errors='replace') as f:
        return f.read()


def ascii_(s):
    return s.encode('ascii', 'backslashreplace').decode('ascii')


def short(s, n=90):
    s = ' '.join(s.split())
    return s if len(s) <= n else s[:n] + '...'


def verdict_rows(plan_dir):
    """[(row_id, dim, kind, verdict, evidence)] -- the rows of the COVERAGE block.

    Source: the acceptance table's COVERAGE-BEGIN/END slice (the deliverable); when the
    markers are absent, the coverage fragment the generator writes.  Same family of rows
    that tools/probes/audit-coverage-reconcile.py judges, so the gate and the audit cannot
    disagree about "what a verdict row is".
    """
    acc = os.path.join(plan_dir, F_ACC)
    frg = os.path.join(plan_dir, F_FRG)
    out = []
    src = ''
    if os.path.exists(acc):
        txt = read(acc)
        i, j = txt.find(CK_BEGIN), txt.find(CK_END)
        if i >= 0 and j > i:
            src = acc
            body = txt[i:j]
        else:
            body = ''
    else:
        txt, body = '', ''
    if src == '' and os.path.exists(frg):
        src = frg
        body = read(frg)
    for ln in body.split('\n'):
        m = ROW_RE.match(ln)
        if not m:
            continue
        cs = [c.strip() for c in ln.split('|')]
        if len(cs) < 7:
            continue
        out.append((m.group(1), cs[2], cs[3], cs[4], cs[5], cs[6]))
    return out, src


def norm_path(p):
    p = p.strip().strip('`').strip('"').strip("'")
    p = p.replace('\\', '/')
    while p.startswith('./'):
        p = p[2:]
    return p


def resolve(p):
    """absolute path of p if it exists under the project root or the plan dir, else None."""
    if p in _path_cache:
        return _path_cache[p]
    full = None
    if p and not re.match(r'^[A-Za-z]:', p):
        for base in (ROOT, PLAN):
            cand = os.path.join(base, p.replace('/', os.sep))
            if os.path.isfile(cand):
                full = cand
                break
        if (full is None) and ('/' not in p) and ('\\' not in p):
            for base in BASENAME_ROOTS:
                cand = os.path.join(base, p)
                if os.path.isfile(cand):
                    full = cand
                    break
    elif p and os.path.isfile(p):
        full = p
    _path_cache[p] = full
    return full


def line_count(full):
    if full in _line_cache:
        return _line_cache[full]
    n = -1
    try:
        with io.open(full, 'r', encoding='utf-8') as f:
            n = sum(1 for _ in f)
    except Exception:
        n = -1                                          # binary / undecodable -> existence only
    _line_cache[full] = n
    return n


def guid_index():
    global _guid_index
    if _guid_index is not None:
        return _guid_index
    idx = set()
    base = os.path.join(ROOT, 'client')
    if os.path.isdir(base):
        for dp, dn, fn in os.walk(base):
            dn[:] = [d for d in dn if d not in ('Library', 'Temp', 'obj', 'bin')]
            for f in fn:
                if not f.endswith('.meta'):
                    continue
                try:
                    with io.open(os.path.join(dp, f), encoding='utf-8', errors='replace') as fh:
                        for i, ln in enumerate(fh):
                            if i > 12:
                                break
                            m = GUID_META_RE.search(ln)
                            if m:
                                idx.add(m.group(1).lower())
                                break
                except Exception:
                    continue
    _guid_index = idx
    return idx


def classify_anchor(ev):
    """-> (form, token) of the first accepted anchor, or (None, None)."""
    if not ev:
        return (None, None)
    # F1 <path>:<line>
    for m in PATHLINE_RE.finditer(ev):
        raw, ext, ln = m.group(1), m.group(2).lower(), int(m.group(3))
        full = resolve(norm_path(raw))
        if not full:
            continue
        if ext in TEXT_EXT:
            n = line_count(full)
            if n >= 0 and (ln < 1 or ln > n):
                continue                                # line out of range -> not this anchor
        return ('F1', m.group(0).strip('`'))
    # F2 guid=<32 hex> present as a .meta guid inside the project
    m = GUID_TOK_RE.search(ev)
    if m:
        if m.group(1).lower() in guid_index():
            return ('F2', 'guid=' + m.group(1).lower())
    # F3 / F4 path token that exists on disk (backticked or bare)
    cands = list(TOKEN_RE.findall(ev))
    cands += [m.group(1) for m in BARE_PATH_RE.finditer(ev)]
    for tok in cands:
        p = norm_path(tok)
        if '.' not in os.path.basename(p):
            continue
        ext = p.rsplit('.', 1)[-1].lower()
        if ext not in ART_EXT:
            continue
        if resolve(p):
            if re.search(r'\d', ev):
                return ('F4', tok)
            return ('F3', tok)
    return (None, None)


def hit_quality(dim, line):
    """-> (ok, why): does this hit line COUNT as a probe hit for this row?

    Implements the hit CONTRACT in ONE place (main-agent ruling 2026-09-23, option (b) over (a)).
    WHY (a) was rejected -- and this is the reason the rule exists at all: option (a) would let the
    PRODUCER decide what counts as a hit ("the enumerator wrote down the anchor it intends to use"),
    i.e. the criterion satisfied by what the measured object says about itself.  That is the same
    family as tonight's three already-rejected green washes: the table echo (a re-render of the
    verdict rows), the never-firing threshold (v1), and a producer that ships unstable fields.  A
    self-declared failure must never count as success.
        R1 `unresolved=1` is legal ONLY on a line whose anchor field is `--`.
           An anchor PLUS `unresolved=1` is the producer contradicting itself -- measured on the
           real ledger: the F3/F4 "bare file name" rows wrote `unresolved=1` although an anchor was
           present (the real cause was a path-resolution mismatch, not "found nothing").  Such a
           line is a LIAR: it is counted and it turns the ITEM red, but the hit is NOT withheld
           (the anchor really is checkable -- see SUMMARY for why that distinction matters).
        R2 behaviour-class rows (D6,D7,D9,D10,D11,D12,S2,S3) need a REAL probe:
           `probe=<name>` plus a measured value (`measured=...` or `run=<id>`).  `unresolved=1` is
           the producer admitting it resolved nothing => NOT a hit.
           These rows are LEGITIMATELY RED until real probes exist; that red is the correct state,
           not a debt to be brought green by loosening this rule.
        R3 enumeration-class rows (D1,D5) pass without `probe=`: the enumerator's OWN output is
           their legitimate ledger (`bytes=... lines=...` are measurements it really took).  (b)
           must not turn those 2674 rows red -- that would also be wrong, just in the other
           direction.
    SUMMARY: returns (ok, why, lying).
        ok    = the line counts as a hit for this row;
        lying = the line writes `unresolved=1` although an anchor IS present.
    WHY `lying` is a SEPARATE flag rather than a reason to withhold the hit (measured 2026-09-23 on
    the real ledger, and this distinction is what keeps the two constraints from fighting): the
    F3/F4 "bare file name" rows carry a REAL anchor AND `unresolved=1`; the flag is wrong (the
    producer's own diagnosis: a path-resolution mismatch, not "found nothing") but the anchor is
    still checkable.  Withholding the hit would turn 1233 enumeration rows red for a flag typo,
    contradicting the ruling's own constraint that the enumeration class (D1,D5) stays green.  So
    the liar count makes the ITEM red (the defect is visible) without pretending those rows are
    unjudged.
    """
    fields = [f.strip() for f in line.split('\t')[1:] if f.strip()]
    anchor = fields[0] if fields else ''
    joined = ' '.join(fields)
    unresolved = re.search(r'(?<![\w=])unresolved=1(?![\d])', joined) is not None
    lying = unresolved and (anchor != ANCHOR_NONE)
    if unresolved and anchor == ANCHOR_NONE:
        return (False, 'unresolved', False)          # legal flag, but the producer resolved nothing
    if dim in BEH_DIMS:
        if unresolved:
            return (False, 'unresolved', lying)      # self-declared failure is never a hit
        has_probe = re.search(r'(?<![\w-])probe=\S+', joined) is not None
        has_meas = (re.search(r'(?<![\w-])measured=\S+', joined) is not None) or \
                   (re.search(r'(?<![\w-])run=\S+', joined) is not None)
        if not (has_probe and has_meas):
            return (False, 'behaviour-needs-probe', lying)
    return (True, 'ok', lying)


def probe_hits(plan_dir, probe_roots):
    """-> (carrier_paths, [(carrier, line)], [(path, why)]) for carriers bound to THIS plan dir.

    SCOPE RULE (added 2026-09-23 after the relay slice `bw-evid-r` self-reported it): its TWO
    self-test sample files declared the REAL plan dir in their header
    (`# probe-hits plan=<real ce-hua>`) and were therefore collected as real ledgers -- the carrier
    count went 1 -> 3 and the samples' rows counted as real probe hits.  That is a SAMPLE POLLUTING
    THE REAL CRITERION, the mirror image of a fixture ledger faking hits (both were measured today).
    So a file is a carrier only when ALL THREE hold:
        (a) it lives under a probe root,
        (b) its NAME says it is a hit ledger (`*hits*.tsv`, case-insensitive) -- the ledger declares
            itself twice, in its name and in its header,
        (c) its first line declares exactly the plan dir under judgement.
    A file rejected ONLY by (b) is REPORTED, never silently dropped: an oddly named real ledger must
    show up as a visible line (a silent exclusion would be a false red of a different kind).
    """
    want = os.path.normcase(os.path.normpath(plan_dir))
    carriers = []
    lines = []
    rejected = []
    for root in probe_roots:
        if not os.path.isdir(root):
            continue
        for dp, dn, fn in os.walk(root):
            dn[:] = [d for d in dn if d not in ('__pycache__',)]
            for f in fn:
                if os.path.splitext(f)[1].lower() not in ('.tsv', '.txt', '.log', '.json'):
                    continue
                p = os.path.join(dp, f)
                try:
                    with io.open(p, encoding='utf-8', errors='replace') as fh:
                        first = fh.readline().strip().lstrip('\ufeff')
                        if not first.startswith(P_HIT):
                            continue                        # not a hit ledger -> never read on
                        declared = first[len(P_HIT):].strip().strip('"')
                        if os.path.normcase(os.path.normpath(declared)) != want:
                            continue                        # belongs to another plan dir
                        if 'hits' not in f.lower():
                            # declares the right plan dir but is not NAMED as a ledger: report it,
                            # do not collect it (see the SCOPE RULE above).
                            rejected.append((os.path.relpath(p, ROOT).replace('\\', '/'),
                                             'name does not contain "hits"'))
                            continue
                        carriers.append(p)
                        for ln2 in fh:
                            t = ln2.rstrip('\n').rstrip('\r')
                            if t.split('\t')[0].strip():
                                lines.append((p, t))
                except Exception:
                    continue
    return (carriers, lines, rejected)


def table_echo(row, line):
    """Fraction of the hit line's fields that are VERBATIM copies of this row's own cells.

    WHY THIS EXISTS (anti-gaming section 8, measured here on 2026-09-23): a coverage ledger
    that is simply a RE-RENDER of the verdict rows satisfies "the row id appears in an
    output" without any probe having run -- the item is then satisfied by its own input,
    the same failure as a gate satisfied by its own report.  A real probe output records
    what the probe OBSERVED (a log offset, a measured value, a scene); it does not repeat
    the entity name + judgement type + verdict strings of the row it exercised.
    CURRENT THRESHOLD (effective; the ONLY definition -- if another document quotes a different
    number, this one wins and that document is stale):
        echo  <=>  fields = line.split('\t')[1:], stripped, dropping empties
                  match  = how many of those fields appear VERBATIM among the row's own cells
                  echo   <=>  len(fields) >= 2  AND  match >= 2  AND  match / len(fields) >= 0.60
    THRESHOLD HISTORY (why the first value was abandoned -- recorded because a threshold that
    changed silently is exactly how two documents end up disagreeing):
        v1 (abandoned the same hour): "match >= 3 cells", SUBSTRING comparison over the row's
           cells, and the cell list did not include the entity column.  Measured on the real
           echo ledger at 08:57: it scored row 1 as only 2 (the evidence field gained an `F1:`
           prefix, and the dim cell `D1` is shorter than the 4-char guard) => 0 of 3800 rows were
           detected, i.e. v1 would have let the fake stay green.  A threshold that never fires is
           not a strict threshold, it is a bug.
        v2 (CURRENT): field-wise equality (not substring), entity column included, ratio-based
           with a 2-field floor so a one-field line (a probe that simply echoes the entity it
           measured) can never be misread as an echo.
        measured: the 08:57 fake ledger => 3800/3800 rows judged echo (red, correct);
                  the 09:19:40 genuine ledger => 0 rows judged echo, echo ratio 0.00 (green).
    KNOWN GAP (main-agent ruling 2026-09-23: NOT tightened, but recorded):
        a ledger that copies exactly ONE cell per row (e.g. only the entity name, or only the
        evidence cell) is NOT judged an echo, because the >= 2 matched-field floor exists to avoid
        punishing a REAL probe that only echoes the single thing it measured.
        * why the gap is tolerable: such a line can only assert "this id appeared", it cannot
          fabricate a match >= 2, so its cheating value is very low.
        * TRIGGER TO TIGHTEN (this is the condition that retires the gap): the moment such a
          one-cell ledger is observed in REAL data, drop the floor to >= 1 matched field and
          re-run the four-direction sample.  Keep this block with the trigger -- a known gap
          nobody can find and nobody knows when to close is how a gate rots.
    DECLARED-SHAPE CONVENTION (main-agent ruling 2026-09-23, same family as "a criterion must be
    provably red"; also recorded in gate-selftest.ps1's SEAM RULE block): the measurement shapes a
    ledger may emit are enumerated in DECLARED_SHAPES below, MEASURED from the real ledger rather
    than guessed.  An undeclared shape (e.g. `meta_missing=`) makes item 40 RED on purpose: a
    degraded shape otherwise looks exactly like a normal one (the relay slice's P4 v1: 1180 rows fell
    into a degradation shape while every field still had a value).  => WHENEVER A SHAPE IS ADDED,
    UPDATE DECLARED_SHAPES IN THE SAME CHANGE.  Observability criteria need maintenance too.
    """
    fields = [f.strip() for f in line.split('\t')[1:] if f.strip()]
    if len(fields) < 2:
        return 0.0
    cells = set(c.strip() for c in row[1:] if len(c.strip()) >= 2)
    m = sum(1 for f in fields if f in cells)
    if m < 2:
        return 0.0
    return m / float(len(fields))


def main():
    global BASENAME_ROOTS, PLAN
    plan_dir = PLAN
    probe_roots = [os.path.join(ROOT, 'tools', 'probes'), os.path.join(ROOT, '.ai-tmp', 'test')]
    shot_dir = os.path.join(ROOT, '.ai-tmp', 'screenshots')
    out_tsv = None
    if '--plan' in sys.argv:
        plan_dir = os.path.abspath(sys.argv[sys.argv.index('--plan') + 1])
    if '--probe-roots' in sys.argv:
        probe_roots = [os.path.abspath(x) for x in
                       sys.argv[sys.argv.index('--probe-roots') + 1].split(';') if x.strip()]
    if '--shot-dir' in sys.argv:
        shot_dir = os.path.abspath(sys.argv[sys.argv.index('--shot-dir') + 1])
    if '-o' in sys.argv:
        out_tsv = os.path.abspath(sys.argv[sys.argv.index('-o') + 1])
    # the anchor resolver reads the plan dir too: an anchor may be a path relative to the
    # plan dir under judgement (that is what makes a fixture self-contained)
    PLAN = plan_dir
    BASENAME_ROOTS = [shot_dir, os.path.join(ROOT, '.ai-tmp', 'test'),
                      os.path.join(ROOT, 'tools', 'probes')] + list(probe_roots)

    rows, src = verdict_rows(plan_dir)
    print('INPUT plan  = ' + ascii_(src if src else '(no acceptance table / fragment)'))
    print('INPUT probe = ' + ' ; '.join(ascii_(p) for p in probe_roots))
    print('verdict rows        = %d' % len(rows))
    if not rows:
        print('FAIL: no verdict row found -- nothing to judge is not a pass')
        return 1

    # --------------------------------------------------------------- section A
    per_dim = collections.OrderedDict()
    kind_dist = collections.defaultdict(collections.Counter)
    samples = collections.defaultdict(list)
    anchored = 0
    form_count = collections.Counter()
    for rid, dim, _ent, kind, _verd, ev in rows:
        per_dim.setdefault(dim, [0, 0])
        kind_dist[dim][kind or '(none)'] += 1
        f, _tok = classify_anchor(ev)
        if f:
            anchored += 1
            form_count[f] += 1
            per_dim[dim][0] += 1
        else:
            per_dim[dim][1] += 1
            if len(samples[dim]) < 3:
                samples[dim].append((rid, ev))
    unanch = len(rows) - anchored
    print('--- section A: evidence-anchor (a checkable anchor per verdict row) ---')
    print('rows with an anchor = %d   rows with NO anchor = %d' % (anchored, unanch))
    print('anchor forms in use = ' + ' '.join('%s=%d' % (k, form_count[k])
                                              for k in sorted(form_count)) or 'none')
    print('per-dimension (anchored / no-anchor):')
    for d in [x for x in DIMS if x in per_dim] + [x for x in per_dim if x not in DIMS]:
        a, u = per_dim[d]
        print('  %-4s anchored=%-6d no-anchor=%-6d kind: %s' %
              (d, a, u, '; '.join('%s=%d' % kv for kv in sorted(kind_dist[d].items()))))
    if unanch:
        print('  sample unanchored row(s) (id :: evidence):')
        for d in list(per_dim.keys())[:40]:
            for rid, ev in samples.get(d, [])[:1]:
                print('    row %s [%s] :: %s' % (rid, ascii_(d), ascii_(short(ev))))

    # --------------------------------------------------------------- section B
    carriers, hit_lines, rejected = probe_hits(plan_dir, probe_roots)
    by_id = dict((r[0], r) for r in rows)
    want_ids = set(by_id.keys())
    echo_ids = set()
    echo_ex = []
    liar_ids = set()        # unresolved=1 on a line that HAS an anchor (the field is lying)
    # --- AGGREGATE FIRST, JUDGE ONCE (caliber fix 2026-09-23) --------------------------------
    # TWO reasons, both load-bearing:
    #  (1) DOUBLE COUNT: the old loop added the row id to a bucket PER LINE.  A row can have
    #      several lines -- `bwp-input-hits.tsv` with `probe=` AND `coverage-hits.tsv` without --
    #      so a row a real probe DID hit landed in hit_ids AND noprobe_ids (measured 2026-09-23 on
    #      the real table: 24 rows), and the printed "without a REAL probe hit" was FALSE for them.
    #  (2) ORDER DEPENDENCE: because those branches were an if/elif CHAIN, which bucket a row ended
    #      in depended on the order the carriers happened to be walked in => the number was not
    #      reproducible.  Aggregating first removes the order dependence entirely.
    # A row is now judged by its BEST line: any ok line => the row is HIT; else, if any line is a
    # legal `unresolved=1` on a "--" anchor, the row is unresolved-only; else it has NO real probe.
    #
    # EXISTS SEMANTICS (this is the rule the aggregation implements): a row counts as HIT as soon as
    # ANY carrier has a line that hit_quality calls ok -- "no real probe" means "EVERY carrier's view
    # of this row is not a real probe", NOT "some carrier's view is not".  The defect this replaces
    # was a FALSE RED: the report said a row lacked a real probe although a real probe already
    # existed.  That is the exact mirror of a false green (a criterion saying OK about work nobody
    # did) -- both are one signal being read as two different things, which is why the fix is a
    # semantics fix and not a cosmetic recount.
    # NET IS NOT A COMPONENT: the 124 -> 103 move is a COMPOSITE -- REMOVED 24 (rows a real probe
    # DID hit: they had an ok line AND a bare line) minus ADDED 3 (rows whose only evidence is a
    # legal "-- unresolved=1" line, which the old elif chain pushed into unres_ids) = net 21.  Never
    # infer either side from the net: 21 is not "24 minus nothing", nor "nothing minus 3".  Report
    # ADDED / REMOVED / net as THREE separate numbers; a reader who only sees 103 will otherwise
    # reconstruct a wrong single-change model and misjudge whether a gap is fixed.
    # SANDBOX NOTE: the D11 ledger's own sandbox self-test printed `behaviour-no-probe = 0` because
    # that sandbox contained ONE carrier; with a single carrier the double-count cannot happen, so
    # "correct in the single case" hid "wrong in the combination".  A sandbox self-test must
    # reproduce the REAL table's carrier composition (>= 2 carriers, at least one of them bare).
    #
    # CALIBER CORRECTION (same date -- this is a CALIBER correction, NOT a change in the data, and
    # it must never be read as a loosening): `behaviour-no-probe` moves 124 -> 103 because the
    # metric now means what its own wording ALWAYS said -- the row SET of behaviour rows with no ok
    # hit -- instead of "how many hit lines lacked probe=".  The separate drop of `rows NOT hit`
    # 129 -> 105 is UNRELATED: bw-m landed the real D11 ledger (+24 rows hit).  Two different
    # changes; and 103 > 0, so the item stays red, which is its correct state.
    saw_ok = set()
    saw_unres = set()
    for car, ln in hit_lines:
        rid = ln.split('\t')[0].strip()
        r = by_id.get(rid)
        if r is None:
            continue
        if table_echo(r, ln) >= 0.6:
            echo_ids.add(rid)
            if len(echo_ex) < 3:
                echo_ex.append((os.path.relpath(car, ROOT).replace('\\', '/'), ln[:100]))
            continue
        ok, why, lying = hit_quality(r[1], ln)
        if lying:
            liar_ids.add(rid)               # its own defect: counted, and it turns the ITEM red
        if ok:
            saw_ok.add(rid)
        elif why == 'unresolved':
            saw_unres.add(rid)
    hit_ids = saw_ok
    unres_ids = saw_unres - saw_ok          # "its ONLY hits are unresolved" (unchanged at 5)
    # ROW-SET level, and computed over `rows` (NOT over hit_lines) on purpose: a behaviour row with
    # NO carrier line at all belongs here too.  0 such rows today, but the old per-line loop could
    # never see them -- it silently dropped them.
    #
    # KNOWN GAP (recorded 2026-09-23, deliberately NOT changed here): BEH_DIMS (module scope)
    # excludes D8 -- 123 sound-effect rows, the "the clip exists but no event is wired" family,
    # 3.2% of the matrix -- so those rows pass under the enumeration rule without a real probe.
    # That is a caliber difference against the 12+3 dimension table in
    # patterns/full-coverage-audit.md.  DO NOT just add 'D8' to BEH_DIMS: it would turn those 123
    # rows red at once, and without real D8 probes first that is manufacturing 123 unresolvable
    # false reds.  Prepare the probe first, THEN move the class.
    noprobe_ids = set(r[0] for r in rows if r[1] in BEH_DIMS and r[0] not in hit_ids)
    echo_only = echo_ids - hit_ids
    hit = len(want_ids & hit_ids)
    miss = len(want_ids - hit_ids)
    print('--- section B: coverage-hit (every verdict row hit by row id in a probe output) ---')
    print('hit carriers        = %d %s' % (len(carriers), ascii_('; '.join(
        os.path.relpath(c, ROOT).replace('\\', '/') for c in carriers[:6]))))
    print('rows hit by id      = %d   rows NOT hit = %d' % (hit, miss))
    print('table-echo hits     = %d line(s) of %d hit line(s); rows whose ONLY hits are echoes = %d'
          % (len(echo_ids), len(hit_lines), len(echo_only)))
    for rp, rw in rejected:
        print('  not-a-carrier (declares this plan dir but is NOT named as a ledger): %s -- %s' % (ascii_(rp), rw))
    print('carrier contract    = first line "# probe-hits plan=<plan dir>"; '
          'then "<rowId>\\t<...>" per row; a line whose fields are >= 60%% verbatim copies of the row\'s own '
          'acceptance cells is a TABLE ECHO (a re-render of the plan), not a probe hit')
    print('behaviour-no-probe   = %d   behaviour-class row(s) (D6,D7,D9,D10,D11,D12,S2,S3) with NO ok '
          'probe hit, at ROW-SET level (a row carrying one probe= line AND one bare line counts as '
          'HIT; a behaviour row with no carrier line at all also counts here); need probe=<name> + '
          'measured=/run=: LEGITIMATELY RED until real probes exist -- that red is the CORRECT '
          'state, NOT a debt to be greened by loosening this rule'
          % len(noprobe_ids))
    print('unresolved-lying     = %d   row(s) writing unresolved=1 although an anchor IS present '
          '(the field is lying; unresolved=1 is legal ONLY on a "--" line)' % len(liar_ids))
    print('unresolved-only      = %d   row(s) whose only hits are unresolved=1 on a "--" line '
          '(the producer says it resolved nothing => not a hit)' % len(unres_ids))

    # ---- INVARIANTS (added with the aggregate-first fix 2026-09-23) ---------------------------
    # WHY: the numbers above are only meaningful if they PARTITION the behaviour class.  Two equal
    # COUNTS can still be different SETS (measured: a 3-row fixture gave 3 under both the old and
    # the new caliber, i.e. it proved nothing), so the partition is ASSERTED, not eyeballed.
    _bh = set(r[0] for r in rows if r[1] in BEH_DIMS)
    inv = []
    inv.append(('hit_ids & noprobe_ids = 0', not (hit_ids & noprobe_ids),
                '%d' % len(hit_ids & noprobe_ids)))
    inv.append(('hit_ids & unres_ids = 0', not (hit_ids & unres_ids),
                '%d' % len(hit_ids & unres_ids)))
    inv.append(('|BEH| = |BEH & hit| + |BEH & noprobe|',
                len(_bh) == len(_bh & hit_ids) + len(_bh & noprobe_ids),
                '%d = %d + %d' % (len(_bh), len(_bh & hit_ids), len(_bh & noprobe_ids))))
    # noprobe & unres is EXPECTED to be non-empty: a behaviour row whose only evidence is a legal
    # "-- unresolved=1" line has no real probe AND the producer resolved nothing -- both statements
    # are true, and the overlap is PRINTED rather than hidden.  (The alternative -- excluding those
    # rows from noprobe -- would break the partition identity above, which is the worse trade.)
    inv.append(('noprobe_ids & unres_ids subset of BEH',
                (noprobe_ids & unres_ids) <= _bh,
                '%d' % len(noprobe_ids & unres_ids)))
    inv_bad = [x for x in inv if not x[1]]
    for _name, _good, _val in inv:
        print('invariant %-38s = %s (%s)' % (_name, 'OK' if _good else 'VIOLATION', _val))

    # ---- SHAPE HISTOGRAM (main-agent approved 2026-09-23) -------------------------------
    # WHY: the relay slice's first P4 attempt looked correct in every visible way while 1180 rows
    # silently fell into a degradation shape (a NameError swallowed by a wide except).  "Every field
    # has a value" is NOT "every field is right", so the SHAPE of the measurement is made visible:
    #    * the histogram is read-only observability -- it is how a human spots "hmm, 1180 rows all
    #      say meta_missing today";
    #    * an UNDECLARED shape additionally turns the item red, because the whole point is that a
    #      degraded shape otherwise looks like a normal one.  The declared set is measured, not
    #      guessed (real ledger 2026-09-23: file_bytes=1612 meta_line=1180 line=1002 unresolved=5
    #      head_bytes=1 => unknown=0), so this rule adds no false red today.
    shapes = collections.Counter()
    unknown_shapes = []
    n_unknown = 0
    for car, ln in hit_lines:
        rid = ln.split('\t')[0].strip()
        if rid not in by_id:
            continue
        fields = [f.strip() for f in ln.split('\t')[1:] if f.strip()]
        sh = '(none)'
        for f in fields[1:]:
            mm = re.match(r'^([a-z_]+)=', f)
            if mm and mm.group(1) not in ('probe', 'measured', 'run'):
                sh = mm.group(1)
                break
        shapes[sh] += 1
        if (sh != '(none)') and (sh not in DECLARED_SHAPES):
            n_unknown += 1
            if len(unknown_shapes) < 3:
                unknown_shapes.append((rid, sh, ln[:90]))
    print('shape histogram      = ' + ' '.join('%s=%d' % (k, shapes[k]) for k in sorted(shapes)))
    print('unknown-shape        = %d line(s) whose measurement shape is not declared (declared: %s) '
          '-- a DEGRADED shape looks exactly like a normal one, which is how a silent failure hides'
          % (n_unknown, ','.join(sorted(DECLARED_SHAPES))))
    for rid, sh, ln in unknown_shapes:
        print('    unknown-shape sample: row %s shape=%s :: %s' % (rid, ascii_(sh), ascii_(ln)))
    if echo_ex:
        print('  table-echo sample(s): a probe output records what the probe SAW, it does not '
              'repeat the row\'s own cells')
        for c, t in echo_ex:
            print('    table-echo sample: ' + ascii_(c) + ' :: ' + ascii_(t))
    if miss:
        miss_ids = sorted(want_ids - hit_ids, key=lambda x: int(x))
        print('  sample un-hit row id(s) = ' + ','.join(miss_ids[:12]))

    # ---- LAYERED contract (main-agent ruling 2026-09-23) -------------------------------
    # A green TOTAL must never hide an empty class, so the hit rate is reported per dimension and
    # rolled up into the two classes that behave differently:
    #   * enumeration-class (D1, D5): the ENTITY ENUMERATOR's own output is a legitimate ledger --
    #     those rows say "this entity exists in this carrier", which is exactly what the enumerator
    #     measures, so demanding a separate probe would be ceremony;
    #   * behaviour-class (D6/D7/D9/D10/D11/D12/S2/S3): these are where "geometry right + collision
    #     wrong" and "data present + never played" live, so they MUST be hit by a REAL probe.
    # NO THRESHOLD is set here on purpose: a threshold anyone can tune is how a gate turns green
    # without the rows being judged.  The item stays red until the behaviour class is probed.
    # ENUM_DIMS / BEH_DIMS come from MODULE scope (see the top of the file): the classes are used
    # by the layered rollup AND by hit_quality's behaviour rule, so defining them twice is exactly
    # how two parts of one criterion start disagreeing.
    per = {}
    for r in rows:
        s = per.setdefault(r[1], {'tot': 0, 'hit': 0, 'echo': 0})
        s['tot'] += 1
        if r[0] in hit_ids:
            s['hit'] += 1
        elif r[0] in echo_ids:
            s['echo'] += 1
    print('per-dimension hit/total (a row is "hit" only when a probe output hit it BY ID; '
          'echo-only rows count as NOT hit):')
    for d in [x for x in DIMS if x in per] + [x for x in per if x not in DIMS]:
        print('  %-4s %5d/%-5d echo-only=%d' % (d, per[d]['hit'], per[d]['tot'], per[d]['echo']))

    def _agg(dims):
        t = sum(per[d]['tot'] for d in dims if d in per)
        h = sum(per[d]['hit'] for d in dims if d in per)
        return (h, t)

    _eh, _et = _agg(ENUM_DIMS)
    _bh, _bt = _agg(BEH_DIMS)
    print('layered contract: enumeration-class (%s) hit=%d/%d [enumerator output is a legitimate '
          'ledger] ; behaviour-class (%s) hit=%d/%d [REAL probes required]'
          % (','.join(ENUM_DIMS), _eh, _et, ','.join(BEH_DIMS), _bh, _bt))
    if out_tsv:
        with io.open(out_tsv, 'a', encoding='utf-8', newline='\n') as f:
            f.write('# per-dimension hit/total: ' + ' '.join(
                ('%s=%d/%d' % (d, per[d]['hit'], per[d]['tot'])) for d in DIMS if d in per) + '\n')
            f.write('# layered: enumeration %d/%d ; behaviour %d/%d\n' % (_eh, _et, _bh, _bt))

    # --------------------------------------------------------------- deliverable TSV
    if out_tsv:
        base = ['# \u7ef4\u5ea6', '\u65e0\u951a\u70b9\u884c\u6570', '\u5224\u636e\u7c7b\u578b\u5206\u5e03',
                '\u62bd\u68371(\u884c\u53f7 + \u8bc1\u636e\u5217\u539f\u6587)',
                '\u62bd\u68372', '\u62bd\u68373', '\u5df2\u6709\u951a\u70b9\u884c\u6570']
        lines = ['\t'.join(base)]
        for d in [x for x in DIMS if x in per_dim] + [x for x in per_dim if x not in DIMS]:
            a, u = per_dim[d]
            dist = ';'.join('%s=%d' % kv for kv in sorted(kind_dist[d].items()))
            ss = []
            for rid, ev in samples.get(d, []):
                ss.append(('row ' + rid + ' :: ' + short(ev, 120)).replace('\t', ' '))
            while len(ss) < 3:
                ss.append('')
            lines.append('\t'.join([d, str(u), dist] + ss + [str(a)]))
        lines.append('\t'.join(['TOTAL', str(unanch), '', '', '', '', str(anchored)]))
        with io.open(out_tsv, 'w', encoding='utf-8', newline='\n') as f:
            f.write('\n'.join(lines) + '\n')
        print('per-dimension unanchored list written to ' + ascii_(out_tsv))

    ok = (unanch == 0) and (miss == 0) and (len(echo_only) == 0) \
        and (len(noprobe_ids) == 0) and (len(liar_ids) == 0) and (len(unres_ids) == 0) \
        and (n_unknown == 0) and (not inv_bad)
    print('RESULT: %s -- evidence anchors %d/%d ; probe hits %d/%d ; echo-only rows %d ; '
          'behaviour-no-probe %d ; unresolved-lying %d' %
          ('PASS' if ok else 'FAIL', anchored, len(rows), hit, len(rows), len(echo_only),
           len(noprobe_ids), len(liar_ids)))
    return 0 if ok else 1


if __name__ == '__main__':
    sys.exit(main())
