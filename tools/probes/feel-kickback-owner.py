# 只读工具：把 mp.dll 里某个共享 callee 的全部调用点，按"武器类"归属。
#
#   用法（cwd = 工程根）：python tools/probes/feel-kickback-owner.py 0x10097F10
#                         python tools/probes/feel-kickback-owner.py 0x10097F10 names   # 只列 类名→vtable
#                         python tools/probes/feel-kickback-owner.py 0x10097F10 names 0x1013FF54  # 列该类的逐槽函数边界
#
# 两条链：
#   链 B（硬）= MSVC RTTI：`vtable[-4]` → `_RTTICompleteObjectLocator` → `+0xC` → `_TypeDescriptor+8` 的名字串。
#   链 A（判据）= 调用点与**已由 RTTI 确证**的 `FireBullets3` 开火点在同一函数体内
#                （两者之间不存在 `ret` + 0xCC/0x90 填充的函数收尾）⇒ 同一武器类。
#                FireBullets3 的 22 把归属见 .ai-tmp/test/mpdll-reverse/PEN-FILL.md §1。
#
# 需求：python3 + capstone + pefile。默认载体 = 原版资源/cs16src/cstrike/dlls/mp.dll。
import sys, os, struct
from capstone import Cs, CS_ARCH_X86, CS_MODE_32
import pefile

DEFAULT_DLL = os.path.join("原版资源", "cs16src", "cstrike", "dlls", "mp.dll")
DLL = sys.argv[3] if len(sys.argv) > 4 else DEFAULT_DLL

TEXT_VA, TEXT_RS, TEXT_RAW = 0x10001000, 0x104800, 0x400
DATA = ((0x10106000, 0x10160E08, 0x104C00, 0x15FC00),
        (0x10161000, 0x10170E00, 0x15FC00, 0x16FA00))

pe = pefile.PE(DLL, fast_load=True)
d = pe.__data__
md = Cs(CS_ARCH_X86, CS_MODE_32)
md.skipdata = True


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


def rd32(va):
    o = foff(va)
    return struct.unpack_from("<I", d, o)[0] if o is not None and o + 4 <= len(d) else None


def in_text(va):
    return TEXT_VA <= va < TEXT_VA + TEXT_RS


def class_vtables():
    out = {}
    for lo, hi, flo, fhi in DATA:
        for off in range(flo, min(fhi, len(d)) - 4, 4):
            loc = struct.unpack_from("<I", d, off)[0]
            if not (lo <= loc < hi):
                continue
            t = rd32(loc + 0xC)
            if t is None:
                continue
            s = cstr(t + 8)                     # _TypeDescriptor = {pVFTable; spare; name[]}
            if s and s.startswith(".?AV"):
                out.setdefault(s, lo + (off - flo) + 4)   # 该 dword = vtable[-1]
    return out


def func_end(start):
    """从 start 线性反汇编到函数末尾（ret 后面是 0xCC/0x90 填充或空隙）。"""
    o = foff(start)
    if o is None:
        return start
    end = start
    for ins in md.disasm(d[o:o + 0x800], start):
        end = ins.address + ins.size
        if ins.mnemonic == "ret":
            eo = foff(end)
            nxt = d[eo:eo + 1] if eo is not None else b""
            if nxt in (b"\xcc", b"\x90", b"\x00", b""):
                break
    return end


def slots(vt):
    o = foff(vt)
    if o is None:
        return []
    out = []
    for i in range(96):
        v = struct.unpack_from("<I", d, o + 4 * i)[0]
        if not in_text(v):
            break
        out.append(v)
    return out


WEAPON_KEY = ["AK47", "AUG", "AWP", "DEAGLE", "ELITE", "FAMAS", "FIVESEVEN", "G3SG1", "GALIL",
              "GLOCK18", "M249", "M4A1", "MAC10", "MP5", "P228", "P90", "SCOUT", "SG550", "SG552",
              "TMP", "UMP45", "USP", "XM1014", "CM3"]

# 已由 RTTI 链两重确证的 FireBullets3 开火点（PEN-FILL.md §1）：同一函数 ⇒ 同一武器类。
FIRE = {"ak47": 0x100D41CB, "aug": 0x100D4C07, "awp": 0x100D573C, "deagle": 0x100D6DC9,
        "elite": 0x100D7787, "famas": 0x100D81C4, "fiveseven": 0x100D8AF9, "g3sg1": 0x100D9F8D,
        "galil": 0x100DA7DC, "glock18": 0x100DB443, "m249": 0x100DDEAB, "m4a1": 0x100DF18E,
        "mac10": 0x100DFB1B, "mp5": 0x100E0394, "p228": 0x100E0D49, "p90": 0x100E1560,
        "scout": 0x100E1F89, "sg550": 0x100E2943, "sg552": 0x100E31C7, "tmp": 0x100E446B,
        "ump45": 0x100E4CE0, "usp": 0x100E58A3}


def same_func(a, b):
    lo, hi = (a, b) if a < b else (b, a)
    o = foff(lo)
    if o is None:
        return None
    for ins in md.disasm(d[o:o + (hi - lo) + 0x20], lo):
        if ins.mnemonic == "ret":
            eo = foff(ins.address + ins.size)
            nxt = d[eo:eo + 1] if eo is not None else b""
            if nxt in (b"\xcc", b"\x90", b"\x00", b"") and ins.address < hi:
                return False
        if ins.address + ins.size > hi:
            return True
    return None


def main():
    callee = int(sys.argv[1], 16)
    mode = sys.argv[2] if len(sys.argv) > 2 else ""
    vts = class_vtables()
    pools = sorted(((n, vt) for n, vt in vts.items()
                    if not n.startswith(".?AVCC")
                    and any(k.lower() in n.lower() for k in WEAPON_KEY)), key=lambda kv: kv[1])

    if mode == "names":
        only = int(sys.argv[3], 16) if len(sys.argv) > 3 and sys.argv[3].startswith("0x") else 0
        for n, vt in pools:
            if only and vt != only:
                continue
            print("  %-30s vt 0x%08X slots=%d" % (n, vt, len(slots(vt))))
            if only:
                for i, t in enumerate(slots(vt)):
                    e = func_end(t)
                    print("      slot[%2d] 0x%08X..0x%08X (len 0x%X)" % (i, t, e, e - t))
        return

    sites = []
    for i in range(0, TEXT_RS - 5):
        if d[TEXT_RAW + i] != 0xE8:
            continue
        if TEXT_VA + i + 5 + struct.unpack_from("<i", d, TEXT_RAW + i + 1)[0] == callee:
            sites.append(TEXT_VA + i)
    print("dll=%s  callee 0x%08X -> %d call site(s)" % (DLL, callee, len(sites)))
    print("vtable pools = %d" % len(pools))

    bounds = [(t, func_end(t), n, i, vt) for n, vt in pools for i, t in enumerate(slots(vt))]
    for s in sites:
        by_fire = sorted(w for w, fp in FIRE.items() if same_func(s, fp) is True)
        hits = [(n, i, t, e, vt) for (t, e, n, i, vt) in bounds if t <= s < e]
        hits.sort(key=lambda h: h[2])
        slot = ("slot %s[%2d] fn 0x%08X..0x%08X" % (hits[-1][0], hits[-1][1], hits[-1][2], hits[-1][3])) if hits else "-"
        print("  site 0x%08X -> fire-point class: %-22s | vtable slot: %s"
              % (s, ",".join(by_fire) if by_fire else "???", slot))


main()
