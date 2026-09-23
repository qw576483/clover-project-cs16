# -*- coding: utf-8 -*-
"""bwr2-selftest.py -- two-direction self-test for the slice BW-R2 probes.

SKILL section 8 item 3: a gate must be verified in BOTH directions -- a known-correct sample must
PASS and a known-wrong sample must FAIL.  Otherwise a probe that is permanently green looks like
a clean project.

This driver materialises two throw-away trees under <root>/.ai-tmp/test/bwr2-fixtures/:

  bad/   deliberately broken: an unregistered `PlayerPrefs` hit, an unprotected `new GameObject`
         next to a gizmo-drawing component, a `// TODO 占位` trace, a LogOnly Options control and
         an empty differences registry.
  good/  the same shapes, but registered / protected / no traces.

and runs each BW-R2 probe against both with BWR2_ROOT pointed at them, asserting the exit code
and the headline counters.

A third tree (`edge/`) carries the two caliber edge cases of `bwr2-runtime-protection.py`:
  * a comment that merely mentions `AddComponent` must produce NO site (caliber fix A);
  * a site whose assignment sits in the same method body 200 lines away must be `same-block`,
    while one whose assignment sits in ANOTHER method 200 lines away must stay `same-file`
    (caliber fix B, both directions).
The same five samples are re-checked against the REAL tree, plus bu-v's acceptance baseline
`gizmo-drawing components = 0`, which must not regress.

It also pins the TRANSPARENCY requirement: the `strict-only differences` block must be printed by
every default run (so the OR rule's looseness is never invisible), must list the offending rows,
and must state `= 0 (no OR-only judgement)` when there are none -- a silent probe must never be
mistakable for a clean one.

Run:
  python tools/probes/bwr2-selftest.py
Exit code: 0 when every expectation is met, 1 otherwise.
ASCII-only stdout.
"""

import io
import os
import shutil
import subprocess
import sys

sys.dont_write_bytecode = True

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
FIX = os.path.join(ROOT, '.ai-tmp', 'test', 'bwr2-fixtures')

BAD_CS = u'''using UnityEngine;

namespace Fixture
{
    public class Bad
    {
        public bool GetFlag()
        {
            // TODO 占位：后续接真实实现
            return false;
        }

        public void Touch()
        {
            PlayerPrefs.SetInt("fixture", 1);
            var go = new GameObject("FixtureRoot");
            var listener = go.AddComponent<AudioListener>();
        }

        public bool ReadsTheFpsKey()
        {
            return Game.Setting.Get("cs.player.showFps", true);
        }
    }
}
'''

GOOD_CS = u'''using UnityEngine;

namespace Fixture
{
    public class Good
    {
        private int _count;

        public bool GetFlag()
        {
            return _count > 0;
        }

        public void Touch()
        {
            var go = new GameObject("FixtureRoot");
            go.hideFlags = HideFlags.HideInHierarchy;
            var listener = go.AddComponent<AudioListener>();
        }

        public bool ReadsTheFpsKey()
        {
            return Game.Setting.Get("cs.player.showFps", true);
        }
    }
}
'''

OPTIONS_CS = u'''namespace Fixture
{
    public class OptionsPanel
    {
        private static readonly ResCtrl[] VideoCtrls =
        {
            new ResCtrl("Windowed", Kind.Check, 33f, 160f, 165f, 24f, "Run in a window") { LogOnly = true },
        };
    }
}
'''

STORE_CS = u'''namespace Fixture
{
    public static class CsPlayerSettingsStore
    {
        private const string KeyPrefix = "cs.player.";

        public static void Save(CsPlayerSettings s)
        {
            var setting = Game.Setting;
            setting.Set(KeyPrefix + "showFps", s.ShowFps);
            setting.Save();
        }
    }
}
'''


COMMENT_ONLY_CS = u'''using UnityEngine;

namespace Fixture
{
    /// <summary>门面在 <c>Awake</c> 里就构造好（<c>AddComponent</c> 会同步触发 Awake），
    /// 另一处 <see cref="AddComponent"/> 也只是文档注释，不是运行时创建点。</summary>
    public class CommentOnly
    {
        public void Noop()
        {
        }
    }
}
'''


def pad(n):
    return '\n'.join('            // pad %d' % i for i in range(n))


def edge_far_away():
    """A site whose hideFlags assignment is in the SAME method body, 200 lines later."""
    return (u'using UnityEngine;\n\nnamespace Fixture\n{\n    public class FarAway\n    {\n'
            u'        public void Build()\n        {\n'
            u'            var go = new GameObject("FarAwayRoot");\n'
            + pad(200) + u'\n'
            u'            go.hideFlags = HideFlags.HideInHierarchy;\n'
            u'        }\n    }\n}\n')


def edge_other_method():
    """A site in method Build, its file's only hideFlags in method Protect, 200 lines later."""
    return (u'using UnityEngine;\n\nnamespace Fixture\n{\n    public class OtherMethod\n    {\n'
            u'        public void Build()\n        {\n'
            u'            var go = new GameObject("OtherMethodRoot");\n'
            u'        }\n\n'
            + pad(200) + u'\n'
            u'        public void Protect()\n        {\n'
            u'            var other = new GameObject("Other");\n'
            u'            other.hideFlags = HideFlags.HideInHierarchy;\n'
            u'        }\n    }\n}\n')


def edge_window_neighbour():
    """A site whose own method sets nothing, with a FOREIGN object's assignment ~5 lines above.

    Pins the accepted cost of the OR rule: the window path is positional, so it labels this
    `same-block` even though the assignment protects `_other`, not `made`.
    """
    return (u'using UnityEngine;\n\nnamespace Fixture\n{\n    public class WindowNeighbour\n'
            u'    {\n'
            u'        private GameObject _other;\n\n'
            u'        private void Pre()\n        {\n'
            u'            _other = new GameObject("Other");\n'
            u'            _other.hideFlags = HideFlags.HideInHierarchy;\n'
            u'        }\n\n'
            u'        private void Build()\n        {\n'
            u'            var made = new GameObject("NeighbourRoot");\n'
            u'        }\n    }\n}\n')


def write(path, text):
    d = os.path.dirname(path)
    if not os.path.isdir(d):
        os.makedirs(d)
    with io.open(path, 'w', encoding='utf-8', newline='\n') as f:
        f.write(text)


def build(tree, cs_text, registry_text):
    base = os.path.join(FIX, tree)
    if os.path.isdir(base):
        shutil.rmtree(base)
    write(os.path.join(base, 'client', 'Assets', 'Scripts', 'Fixture.cs'), cs_text)
    write(os.path.join(base, 'client', 'Assets', 'Scripts', 'UI', 'Flow', 'OptionsPanel.cs'), OPTIONS_CS)
    write(os.path.join(base, 'client', 'Assets', 'Scripts', 'UI', 'Flow', 'CsPlayerSettingsStore.cs'), STORE_CS)
    write(os.path.join(base, u'策划', u'差异登记.tsv'), registry_text)


def build_edge():
    """Third tree: only the two caliber edge cases of `bwr2-runtime-protection.py`."""
    base = os.path.join(FIX, 'edge')
    if os.path.isdir(base):
        shutil.rmtree(base)
    for name, text in (('CommentOnly.cs', COMMENT_ONLY_CS),
                       ('FarAway.cs', edge_far_away()),
                       ('OtherMethod.cs', edge_other_method()),
                       ('WindowNeighbour.cs', edge_window_neighbour())):
        write(os.path.join(base, 'client', 'Assets', 'Scripts', name), text)
    write(os.path.join(base, u'策划', u'差异登记.tsv'), u'# id\twhat\twhy\tsource\texpiry\n')
    return base


def run(script, root=None, extra=()):
    env = dict(os.environ)
    if root:
        env['BWR2_ROOT'] = root
    else:
        env.pop('BWR2_ROOT', None)
    env['PYTHONIOENCODING'] = 'utf-8'
    p = subprocess.Popen([sys.executable, '-X', 'utf8', os.path.join(HERE, script)] + list(extra),
                         stdout=subprocess.PIPE, stderr=subprocess.STDOUT, env=env)
    out, _ = p.communicate()
    return p.returncode, out.decode('utf-8', 'replace')


def read_protection_tsv(root=None):
    base = root or ROOT
    path = os.path.join(base, '.ai-tmp', 'test', 'bwr2-runtime-protection.tsv')
    if not os.path.isfile(path):
        return {}
    tsv = {}
    with io.open(path, encoding='utf-8') as f:
        for ln in f:
            if ln.startswith('#'):
                continue
            parts = ln.rstrip('\n').split('\t')
            if len(parts) >= 5:
                tsv[(parts[0], parts[1])] = (parts[3], parts[4], parts[5] if len(parts) > 5 else '')
    return tsv


def real_rows_for(suffix):
    rows = read_protection_tsv(None)
    return {k: v for k, v in rows.items() if k[0].endswith(suffix)}


def main():
    bad = os.path.join(FIX, 'bad')
    good = os.path.join(FIX, 'good')
    build('bad', BAD_CS, u'# id\twhat\twhy\tsource\texpiry\n')
    build('good', GOOD_CS, u'# id\twhat\twhy\tsource\texpiry\n'
                           u'1\tFixture.cs:28 PlayerPrefs...\tunused\tFixture.cs\tnever\n'
                           u'2\tOptionsPanel LogOnly\tA has it, we do not\tOptionsPanel.cs\tsomeday\n')

    checks = []

    rc, out = run('bwr2-forbidden-api.py', bad)
    checks.append(('forbidden-api / bad tree must FAIL',
                   rc == 1 and 'UNREGISTERED' in out and 'PlayerPrefs' in out, rc, out))
    rc, out = run('bwr2-forbidden-api.py', good)
    checks.append(('forbidden-api / good tree must PASS',
                   rc == 0 and 'hits mentioned nowhere in the registry = 0' in out, rc, out))

    rc, out = run('bwr2-stub-scan.py', bad)
    checks.append(('stub-scan / bad tree must report the TODO trace',
                   ('TODO' in out) and ('traces = 0' not in out), rc, out))
    rc, out = run('bwr2-stub-scan.py', good)
    checks.append(('stub-scan / good tree must be clean',
                   'traces = 0' in out, rc, out))

    rc, out = run('bwr2-runtime-protection.py', bad)
    checks.append(('runtime-protection / bad tree must FAIL on the gizmo site',
                   (rc == 0) and ('GIZMO-UNPROTECTED' in out) and ('sites NOT protected = 2' in out),
                   rc, out))
    rc, out = run('bwr2-runtime-protection.py', good)
    checks.append(('runtime-protection / good tree must be protected',
                   'sites NOT protected = 0' in out, rc, out))
    checks.append(('runtime-protection / the differences block self-proves the ZERO case',
                   'strict-only differences = 0 (no OR-only judgement)' in out, rc, out))

    # --- caliber fix A: a comment that merely mentions AddComponent is not a site ---
    edge = build_edge()
    rc, out = run('bwr2-runtime-protection.py', edge)
    edge_tsv = read_protection_tsv(edge)
    comment_rows = [k for k in edge_tsv if k[0].endswith('CommentOnly.cs')]
    checks.append(('runtime-protection / comment-only AddComponent must NOT be a site (A)',
                   len(comment_rows) == 0, rc, out))

    # --- caliber fix B: same method body, assignment 200 lines away => same-block ---
    far = [k for k in edge_tsv if k[0].endswith('FarAway.cs')]
    far_state = edge_tsv[far[0]][0] if far else 'MISSING'
    checks.append(('runtime-protection / same-method assignment 200 lines away => same-block (B)',
                   far_state == 'same-block', rc, 'FarAway.cs state = %s\n%s' % (far_state, out)))

    # --- caliber fix B anti-example: assignment in ANOTHER method must stay same-file ---
    other = [k for k in edge_tsv
             if k[0].endswith('OtherMethod.cs') and 'OtherMethodRoot' in edge_tsv[k][2]]
    other_state = edge_tsv[other[0]][0] if other else 'MISSING'
    checks.append(('runtime-protection / other-method assignment must stay same-file (B anti)',
                   other_state == 'same-file', rc,
                   'OtherMethod.cs state = %s\n%s' % (other_state, out)))

    # --- accepted cost of the OR rule, pinned both ways (not a correctness assertion) ---
    neigh = [k for k in edge_tsv
             if k[0].endswith('WindowNeighbour.cs') and 'NeighbourRoot' in edge_tsv[k][2]]
    neigh_or = edge_tsv[neigh[0]][0] if neigh else 'MISSING'
    rc2, out2 = run('bwr2-runtime-protection.py', edge, extra=('--strict-containment',))
    strict_tsv = read_protection_tsv(edge)
    neigh2 = [k for k in strict_tsv
              if k[0].endswith('WindowNeighbour.cs') and 'NeighbourRoot' in strict_tsv[k][2]]
    neigh_strict = strict_tsv[neigh2[0]][0] if neigh2 else 'MISSING'
    checks.append(('runtime-protection / OR window path IS loose on a foreign-object assignment '
                   '(accepted cost: %s vs strict %s)' % (neigh_or, neigh_strict),
                   neigh_or == 'same-block' and neigh_strict == 'same-file', rc,
                   'OR=%s strict=%s\n%s' % (neigh_or, neigh_strict, out)))

    # --- the OR-vs-strict delta must be printed, not silent (edge tree) ---
    rc3, out3 = run('bwr2-runtime-protection.py', edge)
    neigh_lines = [ln for ln in out3.split('\n') if 'WindowNeighbour.cs' in ln and '(OR:' in ln]
    checks.append(('runtime-protection / the OR-vs-strict delta is printed, not silent (edge)',
                   len(neigh_lines) == 1 and '(OR: same-block / strict: same-file)' in neigh_lines[0],
                   rc3, out3))

    # --- real data: the two reported false positives are gone, bu-v's samples survive ---
    rc, out = run('bwr2-runtime-protection.py', None)
    real = read_protection_tsv(None)
    gone = [k for k in real if k[0].endswith('CsMapModule.cs') or k[0].endswith('CsHitboxProxy.cs')]
    checks.append(('runtime-protection / real tree: the 2 comment false positives are gone (A)',
                   len(gone) == 0, rc, out))
    effects = {k[1]: v[0] for k, v in real.items() if k[0].endswith('CombatEffects.cs')}
    checks.append(('runtime-protection / real tree: CombatEffects 394/395/402 => same-block (B)',
                   all(effects.get(x) == 'same-block' for x in ('394', '395', '402')),
                   rc, 'CombatEffects states = %s' % effects))
    checks.append(('runtime-protection / real tree: CombatEffects 91/92 stay unprotected (B anti)',
                   effects.get('91') == 'same-file' and effects.get('92') == 'same-file',
                   rc, 'CombatEffects states = %s' % effects))
    keep = []
    for suffix, line in (('PlayerModule.cs', '120'), ('FirstPersonCamera.cs', '417')):
        hit = [(k, v) for k, v in real.items() if k[0].endswith(suffix) and k[1] == line]
        keep.append(bool(hit))
    checks.append(('runtime-protection / real tree: bu-v counter-examples still present',
                   all(keep), rc, out))
    checks.append(('runtime-protection / real tree: gizmo baseline must stay 0',
                   'of which gizmo-drawing components = 0' in out, rc, out))
    checks.append(('runtime-protection / real tree: |= is counted and the caliber is labelled',
                   'hideFlags assignment lines = 5 (includes |=)' in out, rc, out))
    cand = [ln for ln in out.split('\n') if '(OR:' in ln]
    checks.append(('runtime-protection / real tree: the OR-only rows are listed on every run',
                   'strict-only differences = 1' in out and len(cand) == 1
                   and 'FirstPersonCamera.cs:441' in cand[0], rc, out))

    rc, out = run('bwr2-settings-effect.py', bad)
    checks.append(('settings-effect / bad tree must FAIL on the unregistered LogOnly control',
                   rc == 1 and 'NOT registered in' in out and '= 1' in out, rc, out))
    rc, out = run('bwr2-settings-effect.py', good)
    checks.append(('settings-effect / good tree must PASS in both halves',
                   rc == 0 and '= 0' in out, rc, out))

    ok = 0
    for name, passed, rc, out in checks:
        if passed:
            ok += 1
            print('OK   %s' % name)
        else:
            print('FAIL %s  (rc=%d)' % (name, rc))
            for ln in out.split('\n'):
                if ln.strip():
                    print('       | %s' % ln)
    print('===== bwr2-selftest summary: %d/%d expectations met =====' % (ok, len(checks)))
    return 0 if ok == len(checks) else 1


if __name__ == '__main__':
    sys.exit(main())
