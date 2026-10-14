"""Independent, byte-level re-derivation of the layout of a multi-blend GoldSrc sequence.

Question: for a sequence with numblends=9, is the 12-byte channel-header array laid out
blend-major ([blend][bone], header of (b,k) at animindex + (b*numbones + k)*12) or
bone-major ([bone][blend], header of (b,k) at animindex + (k*numblends + b)*12)?

Three checks, all done on the raw bytes of the mdl -- none of them reuses the previous
slice's period test as the only argument:

  A. header extent  : animindex + numblends*numbones*12 must end at or before the next
                      sequence's animindex (the header array is a prefix of this
                      sequence's data region).
  B. channel tiling : every non-zero channel offset is walked as a block chain
                      ((num,total) then num int16, terminator total==0) and the resulting
                      byte ranges must not overlap each other.  A wrong layout produces
                      garbage headers => insane chains / overlaps.  Both layouts are run
                      and compared, so the test discriminates instead of assuming.
  C. functional grid: the 9 decoded poses (exporter conventions) must form a 3x3 grid --
                      3 distinct pitch levels, monotone yaw inside each level.

Read-only: opens mdl files, writes nothing.
"""
import glob
import math
import os
import struct
import sys

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
sys.stderr.reconfigure(encoding="utf-8", errors="replace")   # CJK paths must not be mangled
ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))   # -> <repo>
SRC = (sys.argv[1] if len(sys.argv) > 1 else os.environ.get("CS16_EXTRACT")) \
    or os.path.join(ROOT, "原版资源", "cs16src")
# The source models are a LOCAL extraction (原版资源/ is git-ignored) -- fail loudly, never skip.
if not os.path.isdir(SRC):
    raise SystemExit("extraction dir not found: %s\n"
                     "  pass it as argv[1] (or set CS16_EXTRACT); it must hold cs16_mdl.py, "
                     "cs16_anim.py and cstrike/cstrike__models__player__*.mdl" % SRC)
_missing = [p for p in ("cs16_mdl.py", "cs16_anim.py")
            if not os.path.isfile(os.path.join(SRC, p))]
if _missing:
    raise SystemExit("missing under %s: %s -- this is the local extraction of the original "
                     "game data, it is not in the repo" % (SRC, ", ".join(_missing)))
sys.path.insert(0, SRC)
sys.stdout.reconfigure(encoding="utf-8", errors="replace")
import cs16_anim  # noqa: E402
import cs16_mdl  # noqa: E402

PLAYER_MDLS = sorted(glob.glob(os.path.join(
    SRC, "cstrike", "cstrike__models__player__*.mdl")))
if not PLAYER_MDLS:
    raise SystemExit("no player models under %s\\cstrike "
                     "(expected cstrike__models__player__*.mdl)" % SRC)


def headers(b, base, n):
    """n x 6 int16 channel offsets starting at base."""
    out = []
    for i in range(n):
        out.append(struct.unpack_from("<6h", b, base + i * 12))
    return out


def chain_end(b, start, nframe):
    """Walk one channel block chain; return (end, blocks, sane)."""
    o = start
    f = 0
    nblk = 0
    end = start
    while True:
        if o + 2 > len(b):
            return end, nblk, False
        num, total = b[o], b[o + 1]
        if num < 1 or num > 255:
            return end, nblk, False
        end = o + 2 + 2 * num
        if end > len(b):
            return end, nblk, False
        nblk += 1
        if total == 0:
            return end, nblk, True
        f += total
        if f >= nframe:
            return end, nblk, True
        o = end


def region_report(b, base, hs, nframe, region_hi):
    """Every non-zero channel offset is relative to its OWN 12-byte header; walk each chain
    and check it stays inside this sequence's data region.  (The offsets are relative to the
    header, so this check is identical under both layouts -- it only proves the header array
    really describes this sequence's channel data.)"""
    sane = 0
    insane = 0
    oob = 0
    ends = []
    for i, h in enumerate(hs):
        for off in h:
            if off == 0:
                continue
            start = base + i * 12 + off
            if start % 2 != 0 or start <= base or start >= len(b):
                oob += 1
                continue
            end, _blk, ok = chain_end(b, start, nframe)
            if not ok:
                insane += 1
                continue
            if end > region_hi:
                oob += 1
                continue
            sane += 1
            ends.append(start)
    return dict(sane=sane, insane=insane, oob=oob, first=min(ends) if ends else 0,
                last=max(ends) if ends else 0)


def static_bones(b, base, numbones_total, blends):
    """Under blend-major: for each mdl bone k, is its (b,k) header all-zero in every blend?"""
    out = []
    for k in range(numbones_total):
        in_any = False
        for bb in range(blends):
            h = struct.unpack_from("<6h", b, base + (bb * numbones_total + k) * 12)
            if any(x != 0 for x in h):
                in_any = True
                break
        out.append(in_any)
    return out


def grid(seq, m, exp, bones, nb, bone_major=False):
    def sample_blend(src, frame, blend):
        i = (src * seq.numblends + blend) if bone_major else (blend * nb + src)
        base = seq.animindex + i * 12
        bone = bones[src]
        pos, ang = [], []
        for j in range(3):
            off = struct.unpack_from("<h", m.b, base + j * 2)[0]
            v = m._channel(base, off, frame)
            pos.append(bone.value[j] if v is None else bone.value[j] + v * bone.scale[j])
        for j in range(3):
            off = struct.unpack_from("<h", m.b, base + 6 + j * 2)[0]
            v = m._channel(base, off, frame)
            ang.append(bone.value[j + 3] if v is None else bone.value[j + 3] + v * bone.scale[j + 3])
        return pos, ang

    def pose(blend):
        locs = []
        for src in exp.keep:
            pos, ang = sample_blend(src, 0, blend)
            p = list(pos)
            if bones[src].parent < 0:
                p = [p[j] - exp.lo[j] for j in range(3)]
            R = cs16_anim.perm_mat(cs16_mdl.quat_mat(cs16_mdl.angle_quat(*ang)))
            locs.append((R, [c * exp.scale for c in cs16_anim.perm3(p)]))
        return exp.world_of(locs)

    def named(world, name):
        for i, src in enumerate(exp.keep):
            if bones[src].name == name:
                return world[i][1]
        return None

    rows = []
    for b in range(seq.numblends):
        w = pose(b)
        rh, lh, nk = (named(w, "Bip01 R Hand"), named(w, "Bip01 L Hand"),
                      named(w, "Bip01 Neck"))
        if rh is None or lh is None or nk is None:
            return None
        mid = [(rh[k] + lh[k]) / 2.0 for k in range(3)]
        d = [mid[k] - nk[k] for k in range(3)]
        horiz = math.sqrt(d[0] * d[0] + d[2] * d[2])
        rows.append((b, math.degrees(math.atan2(d[0], d[2])),
                     math.degrees(math.atan2(d[1], horiz))))
    by_pitch = sorted(rows, key=lambda r: -r[2])
    levels = [by_pitch[i:i + 3] for i in range(0, 9, 3)]
    for lv in levels:
        lv.sort(key=lambda r: r[1])
    # structural claim: rows of the grid are the three b-blocks (b//3 = pitch row, 0 = aim up)
    # and inside a block b%3 is monotone in yaw
    blocks_ok = sorted(r[0] for lv in levels for r in lv) == list(range(9)) and \
        all(len(set(r[0] // 3 for r in lv)) == 1 for lv in levels)
    yaw_ok = all(lv[0][1] < lv[1][1] < lv[2][1] for lv in levels)
    gaps = [levels[0][0][2] - levels[1][0][2], levels[1][0][2] - levels[2][0][2]]
    return dict(levels=[[r[0] for r in lv] for lv in levels],
                pitches=[round(lv[0][2], 2) for lv in levels],
                yaws=[[round(r[1], 1) for r in lv] for lv in levels],
                blocks_ok=blocks_ok, yaw_ok=yaw_ok, gaps=[round(g, 2) for g in gaps],
                grid_ok=(blocks_ok and yaw_ok and gaps[0] > 5 and gaps[1] > 5))


def main():
    labels = ["ref_aim_ak47", "ref_aim_rifle", "ref_aim_onehanded", "crouch_aim_carbine"]
    print("=== A/B: layout checks over every player mdl ===")
    bad = 0
    for path in PLAYER_MDLS:
        m = cs16_mdl.Mdl(path)
        bones = m.bones()
        nb = len(bones)
        seqs = m.sequences()
        starts = sorted(s.animindex for s in seqs)
        name = os.path.basename(path)
        n_multi = 0
        n_fit = 0
        n_first_ok = 0
        n_p_nb = 0
        n_p_bl = 0
        demon = []
        for s in seqs:
            if s.numblends <= 1:
                continue
            n_multi += 1
            n = s.numblends * nb
            nxt = min([x for x in starts if x > s.animindex] or [len(m.b)])
            hdr_fit = s.animindex + n * 12 <= nxt
            hs = headers(m.b, s.animindex, n)
            r = region_report(m.b, s.animindex, hs, max(s.numframes, 1), nxt)
            pat = [all(x == 0 for x in h) for h in hs]

            def period_holds(p, pat=pat):
                return 0 < p < len(pat) and all(pat[i] == pat[i + p]
                                                for i in range(len(pat) - p))
            p_nb = period_holds(nb)
            p_bl = period_holds(s.numblends)
            n_fit += 1 if hdr_fit else 0
            n_first_ok += 1 if r["first"] == s.animindex + n * 12 else 0
            n_p_nb += 1 if p_nb else 0
            n_p_bl += 1 if p_bl else 0
            if not (hdr_fit and r["insane"] == 0 and r["first"] == s.animindex + n * 12) or p_bl:
                bad += 1
            if r["first"] != s.animindex + n * 12:
                print("      ! firstData mismatch: %s first=animindex+%d expect=%d"
                      % (s.label, r["first"] - s.animindex, n * 12))
            if len(demon) < 2 and (s.label.startswith("ref_aim") or s.label.startswith("crouch_aim")):
                st = static_bones(m.b, s.animindex, nb, s.numblends)
                demon.append((s.label, s.numblends, nb, hdr_fit,
                              [bones[k].name for k in range(nb) if not st[k]],
                              [bones[k].name for k in range(nb) if st[k]], p_nb, p_bl,
                              r["first"] - s.animindex, n, r["sane"], r["insane"], r["oob"]))
        print("  %-48s nb=%d  multi-blend seqs=%d" % (name, nb, n_multi))
        print("      hdrFit=%d/%d  firstData==animindex+N*12 in %d/%d  period(numbones)=True %d/%d  period(numblends)=True %d/%d"
              % (n_fit, n_multi, n_first_ok, n_multi, n_p_nb, n_multi, n_p_bl, n_multi))
        for (lab, nbz, nbb, fit, still, mov, p_nb, p_bl, first_off, n, sane, ins, oob) in demon:
            print("      [%s] blends=%d nb=%d hdrFit=%s firstData=animindex+%d (expect %d) sane=%d insane=%d oob=%d"
                  % (lab, nbz, nbb, fit, first_off, n * 12, sane, ins, oob))
            print("           period(numbones)=%s period(numblends)=%s" % (p_nb, p_bl))
            print("           static in all %d blends (%d): %s" % (nbz, len(still), still))
            print("           animated in >=1 blend (%d): %s" % (len(mov), mov))
    print("  -> sequences failing the blend-major checks: %d" % bad)

    print("")
    print("=== C: functional grid (blend-major decode) ===")
    terror = os.path.join(SRC, "cstrike", "cstrike__models__player__terror__terror.mdl")
    m = cs16_mdl.Mdl(terror)
    exp = cs16_anim.Exporter(m, "terror")
    exp.prepare()
    bones = m.bones()
    nb = len(bones)
    for label in labels:
        seq = next((s for s in m.sequences() if s.label == label), None)
        if seq is None or seq.numblends != 9:
            print("  %-22s : not a 9-blend sequence" % label)
            continue
        g = grid(seq, m, exp, bones, nb, bone_major=False)
        gb = grid(seq, m, exp, bones, nb, bone_major=True)
        if g is None or gb is None:
            print("  %-22s : missing hand/neck bone" % label)
            continue
        print("  %-22s BLEND-major: grid=%s pitches=%s gaps=%s blocks=%s yawAsc=%s grid_ok=%s"
              % (label, g["levels"], g["pitches"], g["gaps"], g["blocks_ok"], g["yaw_ok"],
                 g["grid_ok"]))
        print("  %-22s BONE-major : grid=%s pitches=%s gaps=%s blocks=%s yawAsc=%s grid_ok=%s"
              % ("", gb["levels"], gb["pitches"], gb["gaps"], gb["blocks_ok"], gb["yaw_ok"],
                 gb["grid_ok"]))
        for lv in g["yaws"]:
            print("      yaw(blend-major):", lv)


if __name__ == "__main__":
    main()
