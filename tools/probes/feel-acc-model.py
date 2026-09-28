# 只读工具：抽原版 mp.dll 每把武器开火函数里的 `m_flAccuracy`（acc @ 玩家 +0xF8）更新式。
#
#   用法（cwd = 工程根）：python tools/probes/feel-acc-model.py [武器id ...]
#
# 输出每把一行：`shots@` = m_iShotsFired（+0x100）首个访问点；`acc@` = 首个 acc 写入点；
# 后面是这两点之间的 x87 算术序列（括号内 = 常量值、`f=` 后是常量 VA）。
# 归属链 = 武器类 RTTI vtable 的 slot[87]（PrimaryAttack）；其函数体内被 call ≥2 次的目标 = 开火函数。
#
# 需求：python3 + capstone + pefile。载体默认 = 原版资源/cs16src/cstrike/dlls/mp.dll。
import os, re, struct, sys
import pefile
from capstone import Cs, CS_ARCH_X86, CS_MODE_32

DLL = os.path.join("原版资源", "cs16src", "cstrike", "dlls", "mp.dll")
pe = pefile.PE(DLL, fast_load=True)
d = pe.__data__
md = Cs(CS_ARCH_X86, CS_MODE_32)
md.skipdata = True

CLASSES = [("ak47", "CAK47"), ("aug", "CAUG"), ("awp", "CAWP"), ("deagle", "CDEAGLE"),
           ("elite", "CELITE"), ("famas", "CFamas"), ("fiveseven", "CFiveSeven"),
           ("g3sg1", "CG3SG1"), ("galil", "CGalil"), ("glock18", "CGLOCK18"), ("m249", "CM249"),
           ("m4a1", "CM4A1"), ("mac10", "CMAC10"), ("mp5", "CMP5N"), ("p228", "CP228"),
           ("p90", "CP90"), ("scout", "CSCOUT"), ("sg550", "CSG550"), ("sg552", "CSG552"),
           ("tmp", "CTMP"), ("ump45", "CUMP45"), ("usp", "CUSP"), ("m3", "CM3"),
           ("xm1014", "CXM1014")]

RE100 = re.compile(r"\[\s*[a-z0-9]+\s*\+\s*0x100\s*\]")
REF8 = re.compile(r"\[\s*[a-z0-9]+\s*\+\s*0xf8\s*\]")


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


def intx(va):
    return 0x10001000 <= va < 0x10105800


def vts():
    out = {}
    for lo, hi, flo, fhi in ((0x10106000, 0x10160E08, 0x104C00, 0x15FC00),
                             (0x10161000, 0x10170E00, 0x15FC00, 0x16FA00)):
        for off in range(flo, min(fhi, len(d)) - 4, 4):
            loc = struct.unpack_from("<I", d, off)[0]
            if not (lo <= loc < hi):
                continue
            t = struct.unpack_from("<I", d, foff(loc + 0xC))[0]
            s = cstr(t + 8, 120)
            if s and s.startswith(".?AV"):
                out.setdefault(s, lo + (off - flo) + 4)
    return out


def fend(st, span=0x800):
    o = foff(st)
    end = st
    for ins in md.disasm(d[o:o + span], st):
        end = ins.address + ins.size
        if ins.mnemonic == "ret":
            eo = foff(end)
            nx = d[eo:eo + 1] if eo is not None else b""
            if nx in (b"\xcc", b"\x90", b"\x00", b""):
                break
    return end


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
    return struct.unpack_from("<f", d, fo)[0] if fo + 4 <= len(d) else None


def cva(tt):
    try:
        return int(tt[tt.index("[0x") + 3:].split("]")[0].split("+")[0], 16)
    except Exception:
        return None


VT = vts()
want = set(a for a in sys.argv[1:] if not a.startswith("--"))
for wid, cls in CLASSES:
    if want and wid not in want:
        continue
    vt = next((v for n, v in VT.items() if n.startswith(".?AV" + cls + "@")), None)
    if vt is None:
        print("%-9s vt NOT-FOUND" % wid)
        continue
    o = foff(vt)
    sl = []
    for i in range(95):
        v = struct.unpack_from("<I", d, o + 4 * i)[0]
        if not intx(v):
            break
        sl.append(v)
    if len(sl) < 88:
        print("%-9s slots=%d (<88)" % (wid, len(sl)))
        continue
    pa = sl[87]
    ins = list(md.disasm(d[foff(pa):foff(pa) + 0x300], pa))
    calls = {}
    for k, x in enumerate(ins):
        if x.mnemonic == "call" and x.op_str.startswith("0x"):
            calls.setdefault(int(x.op_str, 16), []).append(k)
    helper = next((t for t, ks in calls.items() if len(ks) >= 2 and intx(t)), None)
    if helper is None:
        print("%-9s no fire-helper (slot87=0x%08X)" % (wid, pa))
        continue
    e = fend(helper)
    body = list(md.disasm(d[foff(helper):foff(helper) + (e - helper)], helper))
    i0 = next((k for k, x in enumerate(body) if RE100.search(x.op_str)), None)
    i1 = next((k for k, x in enumerate(body)
               if REF8.search(x.op_str) and x.mnemonic in ("fst", "fstp")), None)
    seq = []
    if i0 is not None and i1 is not None:
        for x in body[i0:i1 + 1]:
            m = x.mnemonic
            if m in ("fmul", "fadd", "fsub", "fsubr", "fdiv", "fld", "fild",
                     "inc", "dec", "imul", "cmp", "jg", "fst", "fstp", "fcom", "fld1", "fxch",
                     "fucompp", "test", "jp", "jnp", "jne"):
                c = cv(x.op_str)
                if c is not None:
                    seq.append("%s(%g@0x%X)" % (m, c, cva(x.op_str)))
                else:
                    seq.append(m)
    tail = []
    if i1 is not None:
        for x in body[i1:i1 + 16]:
            if x.mnemonic in ("fld", "fld1", "fldz", "fcom", "fcomp", "fstp", "fst",
                              "test", "jp", "jnp", "jne"):
                c = cv(x.op_str)
                v = ("(%g@0x%X)" % (c, cva(x.op_str))) if c is not None else ""
                tail.append("%s%s%s" % (x.mnemonic, v, "" if v else (" " + x.op_str if not x.op_str.startswith("st") else "")))
    print("%-9s fire=0x%08X shots@%s acc@%s" % (
        wid, helper,
        ("0x%08X" % body[i0].address) if i0 is not None else "-",
        ("0x%08X" % body[i1].address) if i1 is not None else "-"))
    print("          " + " ".join(seq))
    print("          clamp-tail: " + " ".join(tail))
