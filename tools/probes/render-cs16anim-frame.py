#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
render-cs16anim-frame.py -- offline "original per-frame data" renderer for CS 1.6 models.

WHY THIS EXISTS (carrier fallback chain, level 1: raw data)
-----------------------------------------------------------
The A side (original Counter-Strike 1.6) cannot be captured on this machine: no
original client, no third-person screenshots of the original.  What we DO have is
`client/Assets/Editor/Views/ModelData/*.cs16anim`, which is the original per-frame
skeleton + skinning data exported straight out of the CS 1.6 `*.mdl` files.

This script therefore renders the A side from that raw data:
  * `--frame N`  -> sample every bone track at t = N / fps (the mdl keyframes are
                    per-frame, so this lands exactly on the original key),
  * rigid GoldSrc skinning (`vertinfoindex` = one bone per vertex) applied with
                    `posed = boneWorld(frame) * inverse(boneWorld(bind)) * bindVert`,
  * a PNG with the skinned wireframe/shaded geometry + the bone skeleton, and a
                    JSON with the mechanically comparable numbers
                    (per-bone local pos/quat + composed world position + a hash).

The JSON is what the executor reconciles against the live Unity runtime
(`.ai-tmp/drivers/cs16-play-driver.cs` -> `Entry.ForceFrame`), the PNG is the
A-side half of the side-by-side contact sheet.

Format (little endian; spec = client/Assets/Editor/Views/Cs16AnimData.cs:103-115)
--------------------------------------------------------------------------------
    'C16A' u32 version=1  u32 keyLen key[]
    f32 scale  f32[3] offset(ox,oy,oz)
    u32 numbones   each: u32 nameLen name[]  i32 parent  f32[3] pos  f32[4] quat
    u32 numcaps    each: u32 zoneLen zone[]  i32 bone     f32 centerY capH capR
    u32 numsubs    each: u32 texLen tex[]  u32 flags  u32 vc  u32 tc
                        f32[3*vc] verts  f32[2*vc] uvs  f32[3*vc] norms
                        u8[vc] boneIdx   u32[3*tc] tris
    u32 numclips   each: u32 nameLen name[]  f32 fps  u32 loop  u32 numbones
                        per bone: u32 keyCount  then keyCount x f32[8] = (t,pos.xyz,quat.xyzw)
    'ENDA'

Usage
-----
    python tools/probes/render-cs16anim-frame.py --list
    python tools/probes/render-cs16anim-frame.py --model player_T --clips
    python tools/probes/render-cs16anim-frame.py --model player_T --clip idle1 --frame 0 \
        --out .ai-tmp/test/player_T_idle1_f0
    python tools/probes/render-cs16anim-frame.py --all-clips --model player_T --outdir .ai-tmp/test

Exit code 0 = ok, 2 = bad args / unreadable data.
"""

import argparse
import hashlib
import json
import math
import os
import struct
import sys

try:
    from PIL import Image, ImageDraw
except Exception as exc:                                    # pragma: no cover
    print("PIL is required for the wireframe PNG: " + str(exc), file=sys.stderr)
    sys.exit(2)

MAGIC = b"C16A"
ENDMAGIC = b"ENDA"

# fixed light in export space (model space); only used to make the wireframe readable
LIGHT = (-0.35, 0.62, 0.70)

HERE = os.path.dirname(os.path.abspath(__file__))
PROJECT_ROOT = os.path.dirname(os.path.dirname(HERE))
MODEL_DIR = os.path.join(PROJECT_ROOT, "client", "Assets", "Editor", "Views", "ModelData")


# ---------------------------------------------------------------------------
#  binary reader
# ---------------------------------------------------------------------------
class R:
    def __init__(self, data):
        self.d = data
        self.o = 0

    def u32(self):
        v = struct.unpack_from("<I", self.d, self.o)[0]
        self.o += 4
        return v

    def i32(self):
        v = struct.unpack_from("<i", self.d, self.o)[0]
        self.o += 4
        return v

    def u8(self, n):
        v = self.d[self.o:self.o + n]
        self.o += n
        return v

    def f32(self, n=1):
        v = struct.unpack_from("<%df" % n, self.d, self.o)
        self.o += 4 * n
        return v

    def s(self):
        n = self.u32()
        if n <= 0:
            return ""
        raw = self.d[self.o:self.o + n]
        self.o += n
        return raw.decode("utf-8", "replace")


def read_anim(path):
    with open(path, "rb") as fh:
        data = fh.read()
    r = R(data)
    if data[0:4] != MAGIC:
        raise ValueError("%s: bad magic %r" % (path, data[0:4]))
    r.o = 4                                    # step over 'C16A'
    ver = r.u32()
    if ver != 1:
        raise ValueError("%s: version %d unsupported" % (path, ver))
    a = {"path": path, "key": r.s()}
    a["scale"] = r.f32()[0]
    a["offset"] = list(r.f32(3))

    n = r.u32()
    bones = []
    for _ in range(n):
        b = {"name": r.s(), "parent": r.i32()}
        b["pos"] = list(r.f32(3))
        b["quat"] = list(r.f32(4))
        bones.append(b)
    a["bones"] = bones

    n = r.u32()
    caps = []
    for _ in range(n):
        caps.append({"zone": r.s(), "bone": r.i32(),
                     "centerY": r.f32()[0], "capH": r.f32()[0], "capR": r.f32()[0]})
    a["caps"] = caps

    n = r.u32()
    subs = []
    for _ in range(n):
        s = {"tex": r.s(), "flags": r.u32()}
        vc = r.u32()
        tc = r.u32()
        s["vc"] = vc
        s["tc"] = tc
        s["verts"] = list(r.f32(3 * vc))
        s["uvs"] = list(r.f32(2 * vc))
        s["norms"] = list(r.f32(3 * vc))
        s["boneIdx"] = list(r.u8(vc))
        s["tris"] = list(struct.unpack_from("<%dI" % (3 * tc), r.d, r.o))
        r.o += 4 * 3 * tc
        subs.append(s)
    a["subs"] = subs

    n = r.u32()
    clips = []
    for _ in range(n):
        c = {"name": r.s(), "fps": r.f32()[0], "loop": r.u32() != 0}
        bc = r.u32()
        tracks = []
        for _b in range(bc):
            kc = r.u32()
            tt = [0.0] * kc
            pp = [0.0] * (3 * kc)
            qq = [0.0] * (4 * kc)
            for k in range(kc):
                vals = r.f32(8)
                tt[k] = vals[0]
                pp[k * 3:k * 3 + 3] = vals[1:4]
                qq[k * 4:k * 4 + 4] = vals[4:8]
            tracks.append({"kc": kc, "t": tt, "p": pp, "q": qq})
        c["tracks"] = tracks
        clips.append(c)
    a["clips"] = clips

    if data[r.o:r.o + 4] != ENDMAGIC:
        raise ValueError("%s: bad end magic %r" % (path, data[r.o:r.o + 4]))
    return a


# ---------------------------------------------------------------------------
#  small matrix / quaternion helpers (same conventions as Cs16AnimData.cs)
# ---------------------------------------------------------------------------
def quat_to_mat(q):
    x, y, z, w = q
    return [
        [1 - 2 * y * y - 2 * z * z, 2 * x * y - 2 * w * z, 2 * x * z + 2 * w * y],
        [2 * x * y + 2 * w * z, 1 - 2 * x * x - 2 * z * z, 2 * y * z - 2 * w * x],
        [2 * x * z - 2 * w * y, 2 * y * z + 2 * w * x, 1 - 2 * x * x - 2 * y * y],
    ], [0.0, 0.0, 0.0]


def mat4_of(pos, quat):
    """3x3 + translation -> (R, t)."""
    R, _ = quat_to_mat(quat)
    return R, list(pos)


def compose(outer, inner):
    """(Ro,to) x (Ri,ti) -> (Ro*Ri, Ro*ti + to)."""
    Ro, to = outer
    Ri, ti = inner
    out = [[0.0] * 3 for _ in range(3)]
    for a in range(3):
        for b in range(3):
            out[a][b] = Ro[a][0] * Ri[0][b] + Ro[a][1] * Ri[1][b] + Ro[a][2] * Ri[2][b]
    tv = [Ro[i][0] * ti[0] + Ro[i][1] * ti[1] + Ro[i][2] * ti[2] + to[i] for i in range(3)]
    return out, tv


def apply(m, v):
    R, t = m
    return [R[i][0] * v[0] + R[i][1] * v[1] + R[i][2] * v[2] + t[i] for i in range(3)]


def invert(m):
    R, t = m
    Rt = [[R[j][i] for j in range(3)] for i in range(3)]
    tt = [-sum(Rt[i][j] * t[j] for j in range(3)) for i in range(3)]
    return Rt, tt


def lerp(a, b, u):
    return a + (b - a) * u


def nlerp(q0, q1, u):
    d = sum(a * b for a, b in zip(q0, q1))
    if d < 0:
        q1 = [-v for v in q1]
    out = [lerp(a, b, u) for a, b in zip(q0, q1)]
    n = math.sqrt(sum(v * v for v in out)) or 1.0
    return [v / n for v in out]


def sample_track(tr, t):
    """Sample one bone track at time t (seconds) -> (pos, quat)."""
    kc = tr["kc"]
    if kc == 0:
        return None
    if kc == 1 or t <= tr["t"][0]:
        return tr["p"][0:3], tr["q"][0:4]
    if t >= tr["t"][kc - 1]:
        return tr["p"][(kc - 1) * 3:kc * 3], tr["q"][(kc - 1) * 4:kc * 4]
    # binary search for the bracket
    lo, hi = 0, kc - 1
    while hi - lo > 1:
        mid = (lo + hi) // 2
        if tr["t"][mid] <= t:
            lo = mid
        else:
            hi = mid
    t0, t1 = tr["t"][lo], tr["t"][hi]
    u = 0.0 if t1 <= t0 else (t - t0) / (t1 - t0)
    p0 = tr["p"][lo * 3:lo * 3 + 3]
    p1 = tr["p"][hi * 3:hi * 3 + 3]
    q0 = tr["q"][lo * 4:lo * 4 + 4]
    q1 = tr["q"][hi * 4:hi * 4 + 4]
    return [lerp(a, b, u) for a, b in zip(p0, p1)], nlerp(q0, q1, u)


def world_of(a, locals_):
    """locals_ = [(pos, quat)] per bone (index order) -> world matrices."""
    out = [None] * len(a["bones"])
    for i, b in enumerate(a["bones"]):
        m = mat4_of(locals_[i][0], locals_[i][1])
        p = b["parent"]
        out[i] = compose(out[p], m) if 0 <= p < i else m
    return out


def clip_length(c):
    """Unity's AnimationClip.length = largest last-keyframe time across curves."""
    m = 0.0
    for tr in c["tracks"]:
        if tr["kc"] > 0:
            m = max(m, tr["t"][tr["kc"] - 1])
    return m


def bind_locals(a):
    return [(b["pos"], b["quat"]) for b in a["bones"]]


def frame_locals(a, c, t):
    out = []
    for i in range(len(a["bones"])):
        tr = c["tracks"][i] if i < len(c["tracks"]) else None
        s = sample_track(tr, t) if tr is not None else None
        if s is None:
            b = a["bones"][i]
            s = (b["pos"], b["quat"])
        out.append(s)
    return out


# ---------------------------------------------------------------------------
#  renderer
# ---------------------------------------------------------------------------
def look_at(eye, target, up=(0.0, 1.0, 0.0)):
    f = [target[i] - eye[i] for i in range(3)]
    n = math.sqrt(sum(v * v for v in f)) or 1.0
    f = [v / n for v in f]
    upn = list(up)
    dot = sum(f[i] * upn[i] for i in range(3))
    upn = [upn[i] - dot * f[i] for i in range(3)]
    un = math.sqrt(sum(v * v for v in upn)) or 1.0
    upn = [v / un for v in upn]
    right = [f[1] * upn[2] - f[2] * upn[1], f[2] * upn[0] - f[0] * upn[2], f[0] * upn[1] - f[1] * upn[0]]
    return f, upn, right


def project(p, eye, fwd, up, right, fov_deg, w, h, znear=0.05, zfar=500.0):
    d = [p[i] - eye[i] for i in range(3)]
    z = sum(d[i] * fwd[i] for i in range(3))
    if z <= znear or z >= zfar:
        return None
    x = sum(d[i] * right[i] for i in range(3))
    y = sum(d[i] * up[i] for i in range(3))
    f = 1.0 / math.tan(math.radians(fov_deg) * 0.5)
    return (w * 0.5 + x * f / z * (h * 0.5),
            h * 0.5 - y * f / z * (h * 0.5),
            z)


def render(a, c, t, out_png, w=560, h=760, yaw_deg=0.0, shade=True, geom=True):
    """Render one frame: skinned geometry (painter's algorithm) + bone skeleton."""
    bindL = bind_locals(a)
    bindW = world_of(a, bindL)
    frL = frame_locals(a, c, t)
    frW = world_of(a, frL)
    # posed = frameWorld * inverse(bindWorld) * bindVert   (rigid GoldSrc skinning)
    skin = [compose(frW[i], invert(bindW[i])) for i in range(len(a["bones"]))]

    # bounds (from the skinned vertices) -> camera
    lo = [1e9, 1e9, 1e9]
    hi = [-1e9, -1e9, -1e9]
    for s in a["subs"]:
        for v in range(s["vc"]):
            bi = s["boneIdx"][v]
            if bi >= len(skin):
                bi = len(skin) - 1
            p = apply(skin[bi], s["verts"][v * 3:v * 3 + 3])
            for k in range(3):
                lo[k] = min(lo[k], p[k])
                hi[k] = max(hi[k], p[k])
    cx = (lo[0] + hi[0]) * 0.5
    cy = (lo[1] + hi[1]) * 0.5
    cz = (lo[2] + hi[2]) * 0.5
    radius = 0.5 * math.sqrt(sum((hi[k] - lo[k]) ** 2 for k in range(3))) or 1.0
    dist = radius * 3.2

    yr = math.radians(yaw_deg)
    eye = [cx + dist * math.sin(yr), cy + 0.06 * radius, cz + dist * math.cos(yr)]
    fwd, up, right = look_at(eye, (cx, cy, cz))

    img = Image.new("RGB", (w, h), (16, 18, 22))
    dr = ImageDraw.Draw(img)

    if geom:
        tris = []
        for s in a["subs"]:
            for ti in range(s["tc"]):
                idx = s["tris"][ti * 3:ti * 3 + 3]
                pts = []
                ok = True
                depth = 0.0
                for vi in idx:
                    if vi >= s["vc"]:
                        ok = False
                        break
                    bi = s["boneIdx"][vi]
                    if bi >= len(skin):
                        bi = len(skin) - 1
                    pw = apply(skin[bi], s["verts"][vi * 3:vi * 3 + 3])
                    sp = project(pw, eye, fwd, up, right, 35.0, w, h)
                    if sp is None:
                        ok = False
                        break
                    pts.append(sp)
                    depth += sp[2]
                if not ok:
                    continue
                # flat shading from the mdl's own (outward) vertex normals + a fixed light
                n0 = s["norms"][idx[0] * 3:idx[0] * 3 + 3]
                lgt = n0[0] * LIGHT[0] + n0[1] * LIGHT[1] + n0[2] * LIGHT[2]
                tris.append((depth / 3.0, pts, 0.22 + 0.78 * max(0.0, lgt)))
        tris.sort(key=lambda x: -x[0])
        for depth, pts, lam in tris:
            c0 = int(200 * lam + 26)
            col = (c0, int(c0 * 0.96), int(c0 * 0.88))
            dr.polygon([(p[0], p[1]) for p in pts], fill=col, outline=(38, 40, 46))

    # skeleton
    for i, b in enumerate(a["bones"]):
        pw = frW[i][1]
        sp = project(pw, eye, fwd, up, right, 35.0, w, h)
        if sp is None:
            continue
        p = b["parent"]
        if 0 <= p < i:
            pp = project(frW[p][1], eye, fwd, up, right, 35.0, w, h)
            if pp is not None:
                dr.line([pp[0], pp[1], sp[0], sp[1]], fill=(90, 200, 255), width=2)
        dr.ellipse([sp[0] - 3, sp[1] - 3, sp[0] + 3, sp[1] + 3], fill=(255, 210, 90))

    dr.text((8, 6), "A-side (original .cs16anim)  %s / %s  t=%.4fs  frame=%s  fps=%g" %
            (a["key"], c["name"], t, fmt_frame(t, c["fps"]), c["fps"]), fill=(230, 230, 230))
    dr.text((8, 20), "yaw=%g deg  bones=%d  tris=%d  skinned by rigid GoldSrc vertinfoindex" %
            (yaw_deg, len(a["bones"]), sum(s["tc"] for s in a["subs"])), fill=(160, 200, 230))
    img.save(out_png)
    return bindW, frW


def sheet(paths, out_png):
    """Tile several rendered views side by side into one contact image (one read, all views)."""
    imgs = [Image.open(p) for p in paths]
    w = sum(i.width for i in imgs)
    h = max(i.height for i in imgs)
    sheet_img = Image.new("RGB", (w, h), (10, 11, 14))
    x = 0
    for im in imgs:
        sheet_img.paste(im, (x, 0))
        x += im.width
    sheet_img.save(out_png)
    return sheet_img.size


def fmt_frame(t, fps):
    if fps <= 0:
        return "?"
    return "%.3f" % (t * fps)


def pose_report(a, c, t, bones_filter=None):
    frL = frame_locals(a, c, t)
    frW = world_of(a, frL)
    rows = []
    for i, b in enumerate(a["bones"]):
        nm = b["name"]
        if bones_filter and nm not in bones_filter:
            continue
        rows.append({
            "i": i,
            "name": nm,
            "parent": b["parent"],
            "localPos": [round(v, 6) for v in frL[i][0]],
            "localQuat": [round(v, 6) for v in frL[i][1]],
            "world": [round(v, 6) for v in frW[i][1]],
        })
    return rows


def bone_hash(rows):
    blob = json.dumps([[r["name"], r["world"], r["localQuat"]] for r in rows],
                      sort_keys=True, separators=(",", ":"))
    return hashlib.sha256(blob.encode("utf-8")).hexdigest()[:16]


# ---------------------------------------------------------------------------
#  cli
# ---------------------------------------------------------------------------
def load_models():
    out = {}
    if not os.path.isdir(MODEL_DIR):
        print("model dir missing: " + MODEL_DIR, file=sys.stderr)
        sys.exit(2)
    for fn in sorted(os.listdir(MODEL_DIR)):
        if not fn.endswith(".cs16anim"):
            continue
        p = os.path.join(MODEL_DIR, fn)
        try:
            out[os.path.splitext(fn)[0]] = read_anim(p)
        except Exception as exc:
            print("skip %s: %s" % (fn, exc), file=sys.stderr)
    return out


def main():
    ap = argparse.ArgumentParser(description="Render CS 1.6 .cs16anim frames (A-side ground truth).")
    ap.add_argument("--model", help="model key, e.g. player_T (file name without extension)")
    ap.add_argument("--clip", help="clip / state name, e.g. idle1, run, death1, ref_shoot_rifle")
    ap.add_argument("--frame", type=float, default=0.0, help="frame index (t = frame / fps)")
    ap.add_argument("--time", type=float, help="sample time in seconds (overrides --frame)")
    ap.add_argument("--out", help="output base path (writes <out>.png and <out>.json)")
    ap.add_argument("--outdir", help="output dir for --all-clips")
    ap.add_argument("--all-clips", action="store_true", help="render frame 0 of every clip")
    ap.add_argument("--list", action="store_true", help="list models")
    ap.add_argument("--clips", action="store_true", help="list clips of --model")
    ap.add_argument("--no-geom", action="store_true", help="skeleton only (no shaded geometry)")
    ap.add_argument("--yaw", type=float, default=0.0, help="camera yaw around the model, degrees")
    ap.add_argument("--yaws", help="comma separated yaw list -> one tiled contact image instead of --out")
    args = ap.parse_args()

    models = load_models()
    if args.list or not args.model:
        print("models (%d):" % len(models))
        for k in sorted(models):
            a = models[k]
            print("  %-22s bones=%-3d subs=%-2d clips=%-3d key=%s" %
                  (k, len(a["bones"]), len(a["subs"]), len(a["clips"]), a["key"]))
        return 0

    a = models.get(args.model)
    if a is None:
        print("no such model: %s (have: %s)" % (args.model, ", ".join(sorted(models))), file=sys.stderr)
        return 2

    if args.clips:
        print("%s: %d clips" % (args.model, len(a["clips"])))
        for c in a["clips"]:
            print("  %-34s fps=%-6g len=%.4fs frames=%.0f keys=%s loop=%s" %
                  (c["name"], c["fps"], clip_length(c), clip_length(c) * c["fps"],
                   "/".join(str(t["kc"]) for t in c["tracks"][:3]) + "...", c["loop"]))
        return 0

    names = [c["name"] for c in a["clips"]]
    if args.all_clips:
        outdir = args.outdir or "."
        os.makedirs(outdir, exist_ok=True)
        summary = []
        for c in a["clips"]:
            t = args.time if args.time is not None else args.frame / (c["fps"] or 30.0)
            base = os.path.join(outdir, "%s_%s_f0" % (args.model, c["name"]))
            _, frW = render(a, c, t, base + ".png", yaw_deg=args.yaw, geom=not args.no_geom)
            rows = pose_report(a, c, t)
            with open(base + ".json", "w", encoding="utf-8") as fh:
                json.dump({"model": a["key"], "clip": c["name"], "fps": c["fps"],
                           "clipLength": clip_length(c), "time": t,
                           "boneHash": bone_hash(rows), "bones": rows}, fh,
                          ensure_ascii=False, indent=1)
            summary.append((c["name"], c["fps"], clip_length(c), bone_hash(rows)))
            print("  %-34s len=%.4f hash=%s" % (c["name"], clip_length(c), summary[-1][3]))
        print("rendered %d clips into %s" % (len(summary), outdir))
        return 0

    if not args.clip:
        print("need --clip (or --clips / --all-clips)", file=sys.stderr)
        return 2
    c = None
    for x in a["clips"]:
        if x["name"] == args.clip:
            c = x
            break
    if c is None:
        print("no such clip %r in %s; have: %s" % (args.clip, args.model, ", ".join(names)), file=sys.stderr)
        return 2

    t = args.time if args.time is not None else args.frame / (c["fps"] or 30.0)
    base = args.out or os.path.join(".", "%s_%s_f%g" % (args.model, args.clip, args.frame))
    d = os.path.dirname(os.path.abspath(base))
    os.makedirs(d, exist_ok=True)

    if args.yaws:
        parts = []
        for y in [float(v) for v in args.yaws.split(",") if v.strip()]:
            p = base + "_yaw%g.png" % y
            render(a, c, t, p, yaw_deg=y, geom=not args.no_geom)
            parts.append(p)
        size = sheet(parts, base + "_views.png")
        print("views sheet: " + base + "_views.png (%dx%d)" % size)
    else:
        render(a, c, t, base + ".png", yaw_deg=args.yaw, geom=not args.no_geom)
    rows = pose_report(a, c, t)
    with open(base + ".json", "w", encoding="utf-8") as fh:
        json.dump({"model": a["key"], "clip": c["name"], "fps": c["fps"],
                   "clipLength": clip_length(c), "time": t, "frame":
                   (t * c["fps"]), "boneHash": bone_hash(rows), "bones": rows},
                  fh, ensure_ascii=False, indent=1)
    print("%s / %s  t=%.4fs (frame %.3f)  fps=%g  clipLength=%.4f  boneHash=%s" %
          (a["key"], c["name"], t, t * c["fps"], c["fps"], clip_length(c), bone_hash(rows)))
    print("  png : " + base + ".png")
    print("  json: " + base + ".json")
    return 0


if __name__ == "__main__":
    sys.exit(main())
