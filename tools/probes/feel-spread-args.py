# 只读工具：抽原版 mp.dll 里每把武器 `PrimaryAttack`（vtable slot[87]）传给开火函数的 flSpread 实参。
#
#   用法（cwd = 工程根）：python tools/probes/feel-spread-args.py [武器id ...]
#     • 不带参数 = 24 把武器全打一遍
#     • 只打某几把：python tools/probes/feel-spread-args.py ak47 m4a1
#
# 判据（"三档模板"）：
#   test [pev+0x1A4], 0x200 (FL_ONGROUND) 不成立 → 空中档 = Add + AirMul × acc
#   成立后 Length2D 与 140 比较，>140 → 移动档 = Add + MoveMul × acc
#   否则 → 静止档 = StillMul × acc
#   （acc = m_flAccuracy @ [this+0xF8]；空中/移动档共用同一个 Add 常量）
#   手写确证样例：CAK47 slot[87]=0x100D3F60（0.4/0.04 + 0.07/0.04 + 0.0275）。
#   ⚠️ 手枪/狙击族的分支树不是三档（4~8 个站点，含 duck / 消音 / 开镜 状态），本工具只把
#      **每个 call 站点**的常量按顺序打出来，不替调用方解释树语义。
#
# 需求：python3 + capstone + pefile。载体默认 = 原版资源/cs16src/cstrike/dlls/mp.dll。
import os, sys, struct
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
# FEEL_DUMP=1 → 额外逐条打印 PrimaryAttack 本体与开火函数体（"逐分支全量反汇编"用，§5.13 口径）。
DUMP = os.environ.get("FEEL_DUMP") == "1"


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


def cv(tt):
    """指令操作数里的浮点常量（按 qword/dword 分别读）。"""
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


VT = vts()
want = set(sys.argv[1:])
for wid, cls in CLASSES:
    if want and wid not in want:
        continue
    vt = None
    for nm, v in VT.items():
        if nm.startswith(".?AV" + cls + "@"):
            vt = v
            break
    if vt is None:
        print("=== %-10s vt NOT-FOUND" % wid)
        continue
    fn = struct.unpack_from("<I", d, foff(vt) + 4 * 87)[0]
    o = foff(fn)
    ins = list(md.disasm(d[o:o + 0x300], fn)) if o is not None else []
    calls = {}
    for k, x in enumerate(ins):
        if x.mnemonic == "call" and x.op_str.startswith("0x"):
            calls.setdefault(int(x.op_str, 16), []).append(k)
    print("=== %-10s PrimaryAttack(slot87) 0x%08X  calls=%s"
          % (wid, fn, ",".join("0x%08X x%d" % (t, len(k)) for t, k in calls.items())))
    if DUMP:
        # (b) 逐分支全量反汇编：把 PrimaryAttack 本体逐条打出来，交给调用方读分支树。
        #     每个 call 站点标 <CALL>，条件跳转标 <JCC>（目标地址一并给出，便于自己连 CFG）。
        for x in ins:
            tag = "<CALL>" if x.mnemonic == "call" else ("<JCC>" if x.mnemonic[0] == "j" and x.mnemonic != "jmp" else "")
            print("        %08X  %-28s %s" % (x.address, (x.mnemonic + " " + x.op_str + ("" if cv(x.op_str) is None else "  ;=%g" % cv(x.op_str)))[:46], tag))
    helper = None
    for tgt, ks in calls.items():
        if len(ks) >= 2 and in_text(tgt):
            helper = (tgt, ks)
            break
    if helper is None:
        continue
    hva, ks = helper
    print("    fire-helper = 0x%08X（被 call %d 次 = 分支档数）" % (hva, len(ks)))
    if DUMP:
        ho = foff(hva)
        if ho is not None:
            for x in md.disasm(d[ho:ho + 0x100], hva):
                tag = "<CALL>" if x.mnemonic == "call" else ("<JCC>" if x.mnemonic[0] == "j" and x.mnemonic != "jmp" else "")
                print("      H %08X  %-28s %s" % (x.address, (x.mnemonic + " " + x.op_str + ("" if cv(x.op_str) is None else "  ;=%g" % cv(x.op_str)))[:46], tag))
    for k in ks:
        consts, mode, tests = [], "?", []
        j = k - 1
        # ⛔ 逐分支读取（§5.13 口径）——**窗口法已被证伪**：
        #    AWP 蹲档的 site(0x100D54EF) 真值是 `fldz`(=0.0)，而 20 条回看窗口会把上一个分支的
        #    `fld [0x10141a38]`(=0.1) 带进来 ⇒ 报出 0.1。两个分支块之间只隔一个 `jne` 目标，
        #    所以正确做法是**往回走到第一个控制流指令（jcc/jmp/ret/call）就停**，只取本分支块内的常量。
        #    （本函数每个分支块以 `... fstp [esp]; call 0x…; pop esi; ret` 收尾，块内一般只有 1 个常量。）
        while j >= 0 and j > k - 20 and ins[j].mnemonic not in ("jmp", "ret", "call") \
                and not (ins[j].mnemonic[0] == "j" and ins[j].mnemonic != "jmp"):
            m, tt = ins[j].mnemonic, ins[j].op_str
            if m in ("fmul", "fadd", "fsub", "fsubr", "fdiv"):
                c = cv(tt)
                if c is not None:
                    consts.insert(0, "%s=%g" % (m, c))
            # 新增：`fld dword ptr [0x…]` = 直接把常量装进 x87 栈（没有 fmul 的档位就是这么写的）
            if m == "fld":
                c = cv(tt)
                if c is not None:
                    consts.insert(0, "fld=%g" % c)
            # `fldz` = 压入 0.0（AWP 蹲档就是它 ⇒ 蹲下散布 0）
            if m == "fldz":
                consts.insert(0, "fldz=0")
            if m == "fld" and "+0xf8" in tt:
                mode = "acc"
            if m == "fsub" and "+0xf8" in tt:
                mode = "1-acc"
            if m in ("test", "cmp") and "," in tt:
                tests.insert(0, tt)
            j -= 1
        # 守卫（guard）：从**分支块起点**再往前找最近的一条 `test/cmp` + 紧邻的条件跳转，
        #    直接回答"这条分支在什么条件下才走"（档位归属就靠这一列，不再靠窗口里的杂项）。
        g, gj, jj = [], None, j
        while jj >= 0 and jj > j - 8:
            m, tt = ins[jj].mnemonic, ins[jj].op_str
            if m in ("test", "cmp") and not g:
                g.insert(0, tt)
            if m[0] == "j" and m != "jmp" and gj is None:
                gj = "%s %s" % (m, tt)
            jj -= 1
        guard = ("%s  ;  %s" % ("; ".join(g), gj)) if (g or gj) else "-"
        print("    call@0x%08X [%s] %-22s guard: %s"
              % (ins[k].address, mode, " ".join(consts) if consts else "-", guard))
