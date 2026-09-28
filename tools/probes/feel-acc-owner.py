# 只读工具：从原版 mp.dll 反汇编里取每把武器的 `m_flAccuracy`（acc，玩家实体 +0xF8）模型：
#   · 每发的精度更新常量（`base + m_iShotsFired³ / div`，m_iShotsFired @ +0x100）
#   · 每发的 `m_iShotsFired` 累加点
#   · 精度的**重置规则**（`m_iShotsFired` 递减点 + acc 归位常量）
#
#   用法（cwd = 工程根）：python tools/probes/feel-acc-owner.py
#                         python tools/probes/feel-acc-owner.py ak47 m4a1   # 只看这几把
#                         python tools/probes/feel-acc-owner.py --raw ak47  # 打完整反汇编窗口
#                         python tools/probes/feel-acc-owner.py --all        # 加跑全 .text 的 +0x100/+0xf8 站点
#
# 归属链（与 feel-kickback-owner.py 同一套）：
#   链 B（硬）= MSVC RTTI：`vtable[-4]` → `_RTTICompleteObjectLocator` → `+0xC` → `_TypeDescriptor+8` 名字串。
#   链 A（判据）= 武器类 vtable 的 `slot[87]`（部分类为 slot[88]）= `PrimaryAttack`；其函数体内被
#                `call` ≥2 次的那个目标 = 该武器的开火函数（见 tools/probes/feel-spread-args.py）。
#                acc 写入点在开火函数里；本工具对**该武器的全部槽函数 + 开火函数**扫 +0xF8 / +0x100。
#
# 需求：python3 + capstone + pefile。载体默认 = 原版资源/cs16src/cstrike/dlls/mp.dll。
import os, re, sys, struct
from capstone import Cs, CS_ARCH_X86, CS_MODE_32
import pefile

DLL = os.path.join("原版资源", "cs16src", "cstrike", "dlls", "mp.dll")
TEXT_VA, TEXT_RS = 0x10001000, 0x104800
DATA = ((0x10106000, 0x10160E08, 0x104C00, 0x15FC00),
        (0x10161000, 0x10170E00, 0x15FC00, 0x16FA00))

CLASSES = [("ak47", "CAK47"), ("aug", "CAUG"), ("awp", "CAWP"), ("deagle", "CDEAGLE"),
           ("elite", "CELITE"), ("famas", "CFamas"), ("fiveseven", "CFiveSeven"),
           ("g3sg1", "CG3SG1"), ("galil", "CGalil"), ("glock18", "CGLOCK18"), ("m249", "CM249"),
           ("m4a1", "CM4A1"), ("mac10", "CMAC10"), ("mp5", "CMP5N"), ("p228", "CP228"),
           ("p90", "CP90"), ("scout", "CSCOUT"), ("sg550", "CSG550"), ("sg552", "CSG552"),
           ("tmp", "CTMP"), ("ump45", "CUMP45"), ("usp", "CUSP"), ("m3", "CM3"),
           ("xm1014", "CXM1014")]

pe = pefile.PE(DLL, fast_load=True)
d = pe.__data__
md = Cs(CS_ARCH_X86, CS_MODE_32)
md.skipdata = True

RAW = "--raw" in sys.argv
ALL = "--all" in sys.argv
ACCWIN = "--accwin" in sys.argv
ARGS = [a for a in sys.argv[1:] if not a.startswith("--")]

RE_F8 = re.compile(r"\[\s*[a-z0-9]+\s*\+\s*0xf8\s*\]")
RE_100 = re.compile(r"\[\s*[a-z0-9]+\s*\+\s*0x100\s*\]")


def has_f8(tt):
    return bool(RE_F8.search(tt))


def has_100(tt):
    return bool(RE_100.search(tt))


def foff(va):
    if 0x10001000 <= va < 0x10105800:
        return va - 0x10000C00
    if 0x10106000 <= va < 0x10160E08:
        return va - 0x10001400
    if 0x10161000 <= va < 0x10170E00:
        return va - 0x10001400
    return None


def cstr(va, n=120):
    o = foff(va)
    if o is None or o >= len(d):
        return None
    e = d.find(b"\x00", o, o + n)
    if e < 0 or e - o < 2:
        return None
    s = d[o:e]
    return s.decode("latin1") if all(0x20 <= c < 0x7f for c in s) else None


def in_text(va):
    return TEXT_VA <= va < TEXT_VA + TEXT_RS


def vts():
    out = {}
    for lo, hi, flo, fhi in DATA:
        for off in range(flo, min(fhi, len(d)) - 4, 4):
            loc = struct.unpack_from("<I", d, off)[0]
            if not (lo <= loc < hi):
                continue
            f = foff(loc + 0xC)
            t = struct.unpack_from("<I", d, f)[0] if f else None
            s = cstr(t + 8, 120) if t else None
            if s and s.startswith(".?AV"):
                out.setdefault(s, lo + (off - flo) + 4)
    return out


def func_end(start, span=0x800):
    o = foff(start)
    if o is None:
        return start
    end = start
    for ins in md.disasm(d[o:o + span], start):
        end = ins.address + ins.size
        if ins.mnemonic == "ret":
            eo = foff(end)
            nxt = d[eo:eo + 1] if eo is not None else b""
            if nxt in (b"\xcc", b"\x90", b"\x00", b""):
                break
    return end


def fn_start_back(va):
    """从 va 往前找 `ret` 之后的 0xCC/0x90 填充 ⇒ 函数起点。"""
    o = foff(va)
    if o is None:
        return va
    for k in range(1, 0x600):
        if d[o - k] == 0xCC and d[o - k - 1] == 0xCC:
            return va - k + 1
    return va


def slots(vt, n=95):
    o = foff(vt)
    if o is None:
        return []
    out = []
    for i in range(n):
        v = struct.unpack_from("<I", d, o + 4 * i)[0]
        if not in_text(v):
            break
        out.append(v)
    return out


def cv(tt):
    if "[0x" not in tt:
        return None
    try:
        a = int(tt[tt.index("[0x") + 3:].split("]")[0].split("+")[0], 16)
    except Exception:
        return None
    fo = foff(a)
    if fo is None:
        return None
    if "qword" in tt:
        return struct.unpack_from("<d", d, fo)[0] if fo + 8 <= len(d) else None
    if fo + 4 <= len(d):
        return struct.unpack_from("<f", d, fo)[0]
    return None


def kind_of(x):
    tt, m = x.op_str, x.mnemonic
    if has_f8(tt) and m in ("fst", "fstp"):
        return "ACC-WRITE"
    if has_100(tt) and m == "inc":
        return "SHOTS-INC"
    if has_100(tt) and m == "dec":
        return "SHOTS-DEC"
    if has_100(tt) and m in ("mov", "fild", "fld", "cmp", "test", "add", "sub"):
        return "SHOTS-" + m.upper()
    if has_f8(tt) and m == "fld":
        return "ACC-READ"
    if has_f8(tt) and m in ("fsub", "fsubr"):
        return "ACC-1MINUS"
    return None


def dump_fn(fn, label, verbose, win=18):
    e = func_end(fn)
    o = foff(fn)
    if o is None or e <= fn:
        return []
    ins = list(md.disasm(d[o:o + (e - fn)], fn))
    lines = []
    for k, x in enumerate(ins):
        kind = kind_of(x)
        if kind is None:
            continue
        lo = max(0, k - win)
        wnd = ins[lo:k + 1]
        consts = []
        for w in wnd:
            if w.mnemonic in ("fmul", "fadd", "fsub", "fsubr", "fdiv", "fld", "fild"):
                c = cv(w.op_str)
                if c is not None:
                    consts.append("%s@0x%08X=%g" % (w.mnemonic, w.address, c))
        lines.append("  %-14s fn 0x%08X  %-12s 0x%08X  %s %s"
                     % (label, fn, kind, x.address, x.mnemonic, x.op_str))
        if consts:
            lines.append("       consts: " + "  ".join(consts))
        if verbose:
            for w in wnd:
                lines.append("         %08X  %s %s" % (w.address, w.mnemonic, w.op_str))
    return lines


def dump_accwin(fn, label, tail=6):
    e = func_end(fn)
    o = foff(fn)
    if o is None or e <= fn:
        return []
    ins = list(md.disasm(d[o:o + (e - fn)], fn))
    idx = [k for k, x in enumerate(ins) if has_100(x.op_str) or has_f8(x.op_str)]
    if not idx:
        return []
    wr = [k for k, x in enumerate(ins) if has_f8(x.op_str) and x.mnemonic in ("fst", "fstp")]
    last_write = max(wr) if wr else idx[-1]
    lo, hi = max(0, idx[0] - 5), min(len(ins), last_write + tail)
    out = ["  [%s] fn 0x%08X  acc-window 0x%08X..0x%08X (%d insns)"
           % (label, fn, ins[lo].address, ins[hi - 1].address, hi - lo)]
    for x in ins[lo:hi]:
        extra = ""
        c = cv(x.op_str)
        if c is not None:
            a = int(x.op_str[x.op_str.index("[0x") + 3:].split("]")[0].split("+")[0], 16)
            extra = "   ; const=%g @0x%08X" % (c, a)
        out.append("     %08X  %-8s %s%s" % (x.address, x.mnemonic, x.op_str, extra))
    return out


VT = vts()


def fire_helper(pafn):
    o = foff(pafn)
    if o is None:
        return None
    ins = list(md.disasm(d[o:o + 0x300], pafn))
    calls = {}
    for k, x in enumerate(ins):
        if x.mnemonic == "call" and x.op_str.startswith("0x"):
            calls.setdefault(int(x.op_str, 16), []).append(k)
    for tgt, ks in calls.items():
        if len(ks) >= 2 and in_text(tgt):
            return tgt
    return None


# 全局函数 → 武器归属（槽函数 + 开火函数）
OWNER = {}
for wid, cls in CLASSES:
    vt = next((v for nm, v in VT.items() if nm.startswith(".?AV" + cls + "@")), None)
    if vt is None:
        continue
    sl = slots(vt)
    for i, fn in enumerate(sl):
        OWNER.setdefault(fn, "%s.slot%d" % (wid, i))
    if len(sl) > 87:
        h = fire_helper(sl[87])
        if h:
            OWNER.setdefault(h, "%s.fire" % wid)

for wid, cls in CLASSES:
    if ARGS and wid not in ARGS:
        continue
    vt = next((v for nm, v in VT.items() if nm.startswith(".?AV" + cls + "@")), None)
    if vt is None:
        print("=== %-10s vt NOT-FOUND" % wid)
        continue
    sl = slots(vt)
    helper = fire_helper(sl[87]) if len(sl) > 87 else None
    if ACCWIN:
        print("=== %-10s vt 0x%08X slots=%d  slot87=0x%08X  fire-helper=%s"
              % (wid, vt, len(sl), sl[87] if len(sl) > 87 else 0,
                 ("0x%08X" % helper) if helper else "-"))
        for i, fn in enumerate(sl):
            if i in (0, 64, 89, 87, 88):
                for ln in dump_accwin(fn, "slot[%d]" % i):
                    print(ln)
        if helper:
            for ln in dump_accwin(helper, "fire"):
                print(ln)
        continue
    print("=== %-10s vt 0x%08X slots=%d  slot87=0x%08X  fire-helper=%s"
          % (wid, vt, len(sl), sl[87] if len(sl) > 87 else 0,
             ("0x%08X" % helper) if helper else "-"))
    for i, fn in enumerate(sl):
        for ln in dump_fn(fn, "slot[%2d]" % i, RAW):
            print(ln)
    if helper:
        for ln in dump_fn(helper, "fire", RAW):
            print(ln)

if ALL:
    print("### 全 .text 扫描（+0x100 / +0xf8 站点，按函数归属）")
    o0 = foff(TEXT_VA)
    body = d[o0:o0 + TEXT_RS]
    for x in md.disasm(body, TEXT_VA):
        kind = kind_of(x)
        if kind is None:
            continue
        fs = fn_start_back(x.address)
        if fs in OWNER:
            label = OWNER[fs]
        else:
            label = "?"
        # 未归属时按最近前置武器模型串兜底
        print("  0x%08X  %-12s %-22s fn 0x%08X  %s %s"
              % (x.address, kind, label, fs, x.mnemonic, x.op_str))
