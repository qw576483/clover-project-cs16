# -*- coding: utf-8 -*-
"""CLI judge: does every weapon's aim state actually exist as a real Animator state?

Checks, by reading TEXT only (no Unity, no build, no editor):
  1. CsViewTuning.PlayerAnimClass's mapping (weapon id -> anim class, C# fall-through included);
  2. every id in CsWeapons' W(...) table resolves to an aim class that exists in ALL player_*.controller;
  3. no ref_aim_* state in those controllers is unreachable from the weapon table;
  4. the "no aim class" fallback is byte-identical to the idle candidate table;
  5. ActorView.LocomotionStates' standing band really calls PlayerAimStates, and the other bands
     are untouched.

Usage (from anywhere):
  python -X utf8 tools/probes/aim-blend-aimstates.py [--root <repo root>]
Exit: 0 = PASS, 1 = FAIL (assertion), 2 = preflight/missing input.
"""
import io
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", ".."))

argv = sys.argv[1:]
if "--root" in argv:
    ROOT = argv[argv.index("--root") + 1]

VIEW = os.path.join(ROOT, r"client\Assets\Scripts\Module\View\CsViewTuning.cs")
ACTOR = os.path.join(ROOT, r"client\Assets\Scripts\Module\View\ActorView.cs")
WEAP = os.path.join(ROOT, r"client\Assets\Scripts\Core\CsWeapons.cs")
ANIM = os.path.join(ROOT, r"client\Assets\Resources\Art\Anim")

# ---- preflight: say what is missing and where it was looked for; never skip silently ----
missing = [p for p in (VIEW, ACTOR, WEAP, ANIM) if not os.path.exists(p)]
if missing:
    print("ERROR: expected a clover-project-cs16 checkout at %s" % ROOT)
    for p in missing:
        print("  MISSING: %s" % p)
    print("  (-Probe style: pass --root <repo root> if the tree lives elsewhere)")
    sys.exit(2)

def read(p):
    return io.open(p, encoding="utf-8", errors="replace").read()

view, weap, actor = read(VIEW), read(WEAP), read(ACTOR)
fails = []

def check(ok, label):
    print(("ok   " if ok else "FAIL ") + label)
    if not ok:
        fails.append(label)

# ---- 1. PlayerAnimClass mapping (a bare `case CsWeapons.X:` falls THROUGH to the next return) ----
body = re.search(r'public static string PlayerAnimClass\(string weaponId\)\s*\{(.*?)\n        \}',
                 view, re.S)
if body is None:
    print("ERROR: PlayerAnimClass(string) not found in %s" % VIEW)
    sys.exit(2)
explicit_named, byclass, pending = {}, {}, []
for line in body.group(1).split("\n"):
    mc = re.match(r'\s*case CsWeapons\.(\w+):', line)
    mk = re.match(r'\s*case CsWeaponClass\.(\w+):', line)
    mr = re.search(r'return\s+"(\w+)"', line)
    if mc:
        pending.append(mc.group(1))
    elif mk:
        pending.append(("cls", mk.group(1)))
    if mr:
        for p in pending:
            if isinstance(p, tuple):
                byclass[p[1]] = mr.group(1)
            else:
                explicit_named[p] = mr.group(1)
        pending = []
print("explicit weapon cases = %s" % explicit_named)
print("class cases           = %s" % byclass)

# ---- 2. weapon id table ----
consts = dict(re.findall(r'public const string (\w+)\s*=\s*"([a-z0-9_]+)";', weap))
explicit = dict((consts[k], v) for k, v in explicit_named.items() if k in consts)
weapons = [(consts.get(n, n), c) for n, c in
           re.findall(r'W\((\w+),\s*"[^"]*",\s*CsWeaponClass\.(\w+)', weap)]
print("weapons parsed = %d (consts resolved = %d)" % (len(weapons), len(explicit)))
if len(weapons) < 20 or not consts:
    print("ERROR: weapon table not parsed - did CsWeapons.cs change shape?")
    sys.exit(2)

def aim_class(wid, wcls):
    cls = explicit.get(wid) or byclass.get(wcls)     # `default: return null`
    return "dualpistols" if cls == "dualpistols_1" else cls

# ---- 3. real state names in every player controller ----
ctrls = sorted(f for f in os.listdir(ANIM) if f.startswith("player_") and f.endswith(".controller"))
state_sets = dict((f, set(re.findall(r'm_Name: (ref_aim_[a-z0-9_]+)', read(os.path.join(ANIM, f)))))
                  for f in ctrls)
print("player controllers = %d ; ref_aim states each = %s"
      % (len(ctrls), dict((f, len(v)) for f, v in state_sets.items())))
if len(ctrls) < 9:
    print("ERROR: expected 9 player controllers, found %d: %s" % (len(ctrls), ctrls))
    sys.exit(2)

no_aim, bad = [], []
for wid, wcls in weapons:
    cls = aim_class(wid, wcls)
    if cls is None:
        no_aim.append((wid, wcls))
        continue
    for f, names in state_sets.items():
        if "ref_aim_" + cls not in names:
            bad.append((wid, f, "ref_aim_" + cls))
print("weapons with no anim class (idle1 fallback) = %s" % no_aim)
for f in ctrls:
    miss = sorted(set(w for w, ff, _ in bad if ff == f))
    print("  %-28s states=%2d  missing-aim-classes=%s" % (f, len(state_sets[f]), miss))
check(not bad, "every weapon aim state exists in every player controller (missing=%d)" % len(bad))

reach = set("ref_aim_" + aim_class(w, c) for w, c in weapons if aim_class(w, c))
all_states = set()
for names in state_sets.values():
    all_states |= names
orphan = sorted(s for s in all_states if s not in reach and not s.startswith("ref_aim_shield"))
print("reachable aim classes = %s" % sorted(reach))
print("controller aim states unreachable from the weapon table (shield* excluded) = %s" % orphan)
check(not orphan, "no unreachable ref_aim_* state left in the controllers")

# ---- 4. fallback must be the old idle candidate table, verbatim ----
m_idle = re.search(r'PStateIdle\s*=\s*\{([^}]*)\}', view)
m_aim = re.search(r'AimStatesByClass\.TryGetValue\(cls, out cached\)\) return cached;\s*\n\s*'
                  r'cached = new\[\] \{([^}]*)\}', view)
if m_idle is None or m_aim is None:
    print("ERROR: PStateIdle / PlayerAimStates literal not found - did they get renamed?")
    sys.exit(2)
idle_txt = re.sub(r'\s+', '', m_idle.group(1))
aim_txt = re.sub(r'\s+', '', m_aim.group(1))
print("PStateIdle              = %s" % idle_txt)
print("PlayerAimStates literal = %s" % aim_txt)
check(aim_txt == '"ref_aim_"+cls,' + idle_txt, "aim candidate tail == PStateIdle verbatim")

# ---- 5. wiring ----
check("if (!moving) return CsViewTuning.PlayerAimStates(actor.ActiveWeapon);" in actor,
      "LocomotionStates standing branch calls PlayerAimStates(actor.ActiveWeapon)")
for band in ["if (!actor.OnGround) return CsViewTuning.PStateJump;",
             "return moving ? CsViewTuning.PStateCrouchRun : CsViewTuning.PStateCrouchIdle;",
             "return speed > CsViewTuning.AnimRunSpeedThreshold ? CsViewTuning.PStateRun : CsViewTuning.PStateWalk;"]:
    check(band in actor, "untouched band: " + band[:52])

print()
print("AIM-BLEND-AIMSTATES " + ("PASS" if not fails else "FAIL(%d)" % len(fails)))
sys.exit(0 if not fails else 1)
