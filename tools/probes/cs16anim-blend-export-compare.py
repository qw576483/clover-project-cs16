"""Regression for the blend-dimension export change (offline, read-only w.r.t. the project).

Four things are proved:

  1. COMPATIBILITY / positive control -- for EVERY clip that already existed, blend 0 of the NEW
     file is bit-identical to the whole track set of the OLD exporter's output (times included),
     and every non-clip section (key/scale/offset/bones/caps/subs) is byte-identical.  So
     idle/run/crouch/reload/fire keep their bytes; the change is purely additive.
  2. ADDITIVE delta -- what the new file adds: aim clips that were previously skipped, and blends
     1..8 for the 9-blend sequences.  Printed with names and counts.
  3. FRESH EXPORT vs THE ASSET ON DISK -- reported separately, because the assets on disk are
     older than the current exporter for reasons unrelated to blends (measured, see the report).
  4. READINGS taken from the NEW FILE itself (forward kinematics on the file's own tracks), so
     "9 distinct aiming paths made it to disk" is shown on the artefact readers consume.

Usage: python tools/probes/cs16anim-blend-export-compare.py [extract_dir] [before_dir] [modeldata_dir]

The "before" (pre-change) exporter is rebuilt automatically: a copy of the live exporter is
reverse-patched with `tools/probes/cs16anim-blend-v3.patch` (that patch IS the whole
export-chain change, so it survives even though 原版资源/ is git-ignored):

    <before_dir>/cs16_{mdl,anim}.py = live copy  ->  git apply -R -p1  ->  pre-change

Missing inputs (extraction / patch / assets) are reported by name and the script exits non-zero;
it never silently skips a check.
"""
import hashlib
import math
import os
import shutil
import struct
import subprocess
import sys

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
sys.stderr.reconfigure(encoding="utf-8", errors="replace")   # CJK paths must not be mangled
ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))   # -> <repo>
PATCH = os.path.join(ROOT, "tools", "probes", "cs16anim-blend-v3.patch")


def _arg(i, default):
    return sys.argv[i] if len(sys.argv) > i else default


SRC = _arg(1, os.path.join(ROOT, "原版资源", "cs16src"))      # new exporter + source models
OLD = _arg(2, os.path.join(ROOT, ".ai-tmp", "test", "cs16anim-blend-before"))
MODELDATA = _arg(3, os.path.join(ROOT, "client", "Assets", "Editor", "Views", "ModelData"))
OUT_OLD = os.path.join(ROOT, ".ai-tmp", "test", "blendfix-out-old")
OUT_NEW = os.path.join(ROOT, ".ai-tmp", "test", "blendfix-out-new")


def need(name, path, how):
    if not os.path.exists(path):
        raise SystemExit("MISSING %s: %s\n  -> %s" % (name, path, how))


need("extraction dir", SRC, "pass it as argv[1] (LOCAL extraction, not in the repo)")
for _f in ("cs16_mdl.py", "cs16_anim.py"):
    need("extraction file " + _f, os.path.join(SRC, _f),
         "the new side must already carry the blend change, see cs16anim-blend-v3.patch")
need("assets", MODELDATA, "pass the ModelData dir as argv[3]")
need("blend patch", PATCH, "it ships next to this probe (tools/probes/)")


def rebuild_before():
    """live exporter + reverse patch = pre-change exporter (rebuilt from scratch every run)."""
    if os.path.isdir(OLD):
        shutil.rmtree(OLD)
    os.makedirs(OLD)
    for f in ("cs16_mdl.py", "cs16_anim.py"):
        shutil.copyfile(os.path.join(SRC, f), os.path.join(OLD, f))
    rel = os.path.relpath(OLD, ROOT).replace("\\", "/")
    r = subprocess.run(["git", "-C", ROOT, "apply", "-R", "-p1", "--directory=" + rel, PATCH],
                       capture_output=True)
    if r.returncode != 0:
        raise SystemExit("could not rebuild the pre-change exporter in %s:\n%s\n  -> run: "
                         "git -C %s apply -R -p1 --directory=%s %s"
                         % (OLD, r.stderr.decode("utf-8", "replace").strip(), ROOT, rel, PATCH))
    print("[before] rebuilt from the live exporter by reverse-applying %s"
          % os.path.relpath(PATCH, ROOT).replace("\\", "/"))


rebuild_before()

PLAYERS = [("player_T", "terror"), ("player_T_leet", "leet"), ("player_T_arctic", "arctic"),
           ("player_T_guerilla", "guerilla"), ("player_CT", "urban"), ("player_CT_gsg9", "gsg9"),
           ("player_CT_sas", "sas"), ("player_CT_gign", "gign"), ("player_CT_vip", "vip")]
OTHERS = [("vm_ak47", "cstrike__models__v_ak47.mdl", ["--view"]),
          ("w_usp", "cstrike__models__w_usp.mdl", ["--unit"])]


def run(script_dir, mdl, key, out, extra):
    cmd = [sys.executable, os.path.join(script_dir, "cs16_anim.py"), mdl, key, out] + extra
    r = subprocess.run(cmd, capture_output=True)
    if r.returncode != 0:
        raise SystemExit("export failed: %s\n%s" % (" ".join(cmd),
                                                    r.stderr.decode("utf-8", "replace")))
    return r.stdout.decode("utf-8", "replace").strip()


def sha(path):
    return hashlib.sha256(open(path, "rb").read()).hexdigest()


def parse(path):
    b = open(path, "rb").read()
    assert b[0:4] == b"C16A", path
    o = 4
    version = struct.unpack_from("<I", b, o)[0]
    o += 4

    def s():
        nonlocal o
        n = struct.unpack_from("<I", b, o)[0]
        o += 4
        v = b[o:o + n].decode("utf-8") if n else ""
        o += n
        return v

    key = s()
    scale = struct.unpack_from("<f", b, o)[0]
    o += 16
    nb = struct.unpack_from("<I", b, o)[0]
    o += 4
    bones = []
    for _ in range(nb):
        nm = s()
        par = struct.unpack_from("<i", b, o)[0]
        p = struct.unpack_from("<3f", b, o + 4)
        q = struct.unpack_from("<4f", b, o + 16)
        o += 32
        bones.append((nm, par, p, q))
    nc = struct.unpack_from("<I", b, o)[0]
    o += 4
    nf = 4 if version >= 2 else 3
    for _ in range(nc):
        s()
        o += 4 + 4 * nf
    ns = struct.unpack_from("<I", b, o)[0]
    o += 4
    for _ in range(ns):
        s()
        o += 4
        vc, tc = struct.unpack_from("<II", b, o)
        o += 8 + 33 * vc + 12 * tc
    pre_clips = o

    nk = struct.unpack_from("<I", b, o)[0]
    o += 4
    clips = []
    for _ in range(nk):
        name = s()
        fps, loop = struct.unpack_from("<fI", b, o)
        o += 8
        nbl = struct.unpack_from("<I", b, o)[0] if version >= 3 else 1
        if version >= 3:
            o += 4
        bc = struct.unpack_from("<I", b, o)[0]
        o += 4
        blends = []
        for _bl in range(nbl):
            tracks = []
            for _bn in range(bc):
                cnt = struct.unpack_from("<I", b, o)[0]
                o += 4
                vals = struct.unpack_from("<%df" % (cnt * 8), b, o)
                o += cnt * 32
                tracks.append([vals[i * 8:(i + 1) * 8] for i in range(cnt)])
            blends.append(tracks)
        clips.append(dict(name=name, fps=fps, loop=loop, numblends=nbl, bones=bc,
                          blends=blends))
    return dict(version=version, key=key, scale=scale, bones=bones, pre=b[8:pre_clips],
                clips=clips, tail=b[o:], size=len(b))


def values_only(tracks):
    """Tracks with the key TIMES dropped (to tell 'key-density only' apart from real motion)."""
    return [[k[1:] for k in tr] for tr in tracks]


def quat_mat(q):
    x, y, z, w = q
    return [[1 - 2 * y * y - 2 * z * z, 2 * x * y - 2 * w * z, 2 * x * z + 2 * w * y],
            [2 * x * y + 2 * w * z, 1 - 2 * x * x - 2 * z * z, 2 * y * z - 2 * w * x],
            [2 * x * z - 2 * w * y, 2 * y * z + 2 * w * x, 1 - 2 * x * x - 2 * y * y]]


def fk(bones, tracks):
    """World position of every bone at key 0, from the file's own bind table + tracks."""
    world = []
    for i, (nm, par, bp, bq) in enumerate(bones):
        tr = tracks[i] if i < len(tracks) and tracks[i] else None
        p = list(tr[0][1:4]) if tr else list(bp)
        q = tuple(tr[0][4:8]) if tr else tuple(bq)
        R = quat_mat(q)
        local = (R, p)
        if 0 <= par < i:
            Rp, tp = world[par]
            R2 = [[sum(Rp[r][k] * R[k][c] for k in range(3)) for c in range(3)] for r in range(3)]
            t2 = [sum(Rp[r][k] * p[k] for k in range(3)) + tp[r] for r in range(3)]
            world.append((R2, t2))
        else:
            world.append(local)
    return world


def main():
    os.makedirs(OUT_OLD, exist_ok=True)
    os.makedirs(OUT_NEW, exist_ok=True)
    print("=== 1/2: old vs new exporter (blend 0 must be bit-identical, sections too) ===")
    allok = True
    for (key, skin) in PLAYERS + [(k, None) for (k, _m, _e) in OTHERS]:
        if skin is not None:
            mdl, extra = os.path.join(SRC, "cstrike",
                                      "cstrike__models__player__%s__%s.mdl" % (skin, skin)), []
        else:
            mdl = os.path.join(SRC, "cstrike", [m for (k, m, _e) in OTHERS if k == key][0])
            extra = [e for (k, _m, e) in OTHERS if k == key][0]
        o_old = os.path.join(OUT_OLD, key + ".cs16anim")
        o_new = os.path.join(OUT_NEW, key + ".cs16anim")
        run(OLD, mdl, key, o_old, extra)
        run(SRC, mdl, key, o_new, extra)
        po, pn = parse(o_old), parse(o_new)
        names_o = [c["name"] for c in po["clips"]]
        bl0_same = all(next((c for c in pn["clips"] if c["name"] == co["name"]),
                            None) is not None
                       and next(c for c in pn["clips"] if c["name"] == co["name"])["blends"][0]
                       == co["blends"][0] for co in po["clips"])
        nb_same = all(cn["numblends"] == 1 for cn in pn["clips"] if cn["name"] in names_o
                      and cn["numblends"] > 1 and False)
        added = [c["name"] for c in pn["clips"] if c["name"] not in names_o]
        multi = [(c["name"], c["numblends"]) for c in pn["clips"] if c["numblends"] > 1]
        aim_added = [n for n in added if "_aim" in n]
        # fresh export vs the asset on disk -- reported, not asserted (see report)
        disk = os.path.join(MODELDATA, key + ".cs16anim")
        dd = parse(disk)
        dnames = [c["name"] for c in dd["clips"]]
        keys_only = []
        real_diff = []
        for c in po["clips"]:
            dc = next((x for x in dd["clips"] if x["name"] == c["name"]), None)
            if dc is None:
                continue
            if dc["blends"][0] == c["blends"][0]:
                continue
            # "key-density only" = every bone agrees at every key time present in BOTH tracks
            sane = True
            for a, b in zip(c["blends"][0], dc["blends"][0]):
                ta = {round(k[0], 6): k[1:] for k in a}
                tb = {round(k[0], 6): k[1:] for k in b}
                for t in set(ta) & set(tb):
                    if ta[t] != tb[t]:
                        sane = False
                        break
                if not sane:
                    break
            if sane:
                keys_only.append(c["name"])
            else:
                real_diff.append(c["name"])
        ok = po["pre"] == pn["pre"] and bl0_same and po["tail"] == pn["tail"]
        allok &= ok
        print("  %-14s sections:%s blend0==old:%s tail:%s | clips %d->%d (+%d, aim %d) "
              "multi-blend %d | disk v%d: keys-only %d %s real-diff %d %s"
              % (key, po["pre"] == pn["pre"], bl0_same, po["tail"] == pn["tail"],
                 len(po["clips"]), len(pn["clips"]), len(added), len(aim_added),
                 len(multi), dd["version"], len(keys_only), keys_only[:4],
                 len(real_diff), real_diff[:4]))
        if key == "player_T":
            print("      added clip names (%d): %s" % (len(added), added))
    print("  -> all models: sections+blend0+tail identical: %s" % allok)

    print("")
    print("=== 4: per-blend read-outs, forward kinematics ON THE NEW FILE (player_T) ===")
    pn = parse(os.path.join(OUT_NEW, "player_T.cs16anim"))
    byname = {c["name"]: c for c in pn["clips"]}
    idx = {nm: i for i, (nm, _p, _bp, _bq) in enumerate(pn["bones"])}
    for label in ["ref_aim_rifle", "ref_shoot_rifle", "ref_aim_ak47", "ref_aim_carbine",
                  "idle1", "run", "fire_placeholder"]:
        c = byname.get(label)
        if c is None:
            print("  %-18s : absent" % label)
            continue
        print("  %-18s numblends=%d bones=%d keys/blend[0]=%d" %
              (label, c["numblends"], c["bones"], len(c["blends"][0][0])))
        seen = set()
        for bl in range(c["numblends"]):
            w = fk(pn["bones"], c["blends"][bl])
            rh = w[idx["Bip01 R Hand"]][1]
            lh = w[idx["Bip01 L Hand"]][1]
            nk = w[idx["Bip01 Neck"]][1]
            mid = [(rh[k] + lh[k]) / 2.0 - nk[k] for k in range(3)]
            yaw = math.degrees(math.atan2(mid[0], mid[2]))
            pitch = math.degrees(math.atan2(mid[1], math.sqrt(mid[0] ** 2 + mid[2] ** 2)))
            seen.add((round(yaw, 2), round(pitch, 2)))
            print("      b%d  RHand=(%+.4f,%+.4f,%+.4f)  aimYaw=%+7.2f  aimPitch=%+7.2f"
                  % (bl, rh[0], rh[1], rh[2], yaw, pitch))
        print("      distinct (yaw,pitch) over %d blends: %d" % (c["numblends"], len(seen)))


if __name__ == "__main__":
    main()
