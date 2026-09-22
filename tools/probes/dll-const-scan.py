#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
dll-const-scan.py -- 降级链第②级「可执行里的常量」探针。

WHY THIS EXISTS (slice cs16-AW, 差异 #16/#18/#21/#22/#23/#24/#50/#57/#59/#61)
--------------------------------------------------------------------------
原版 CS 1.6 把 HUD 坐标 / 观战 FOV / 回合时间 / 移动阈值 / 投掷物 solid / C4 蜂鸣节奏
等量**写死在 `cstrike/cl_dlls/client.dll` 与 `cstrike/dlls/mp.dll` 的立即数里**
（不是 cvar、也不是数据表）。本片把这两个可执行从同一 repack 取回盘（URL / 大小 /
SHA256 见 `原版资源/清单.md`「切片AW」节），本工具给出：

  1) PE 头 / 节表（确认拿到的是 i386 PE32 动态库、各节 file offset 区间）；
  2) `--scan-cs <file.cs>`：把工程里的常量（`public const float X = 1.2f;` /
     `public const int X = 3;`）逐条扫进 `.text/.rdata/.data` 的**原始字节**里，
     命中即给出 `文件:偏移（节内偏移）`——这就是"该值在可执行里存在"的最小证据；
     未命中 ⇒ 明确打印 MISS（= 我们的值与原版可执行里找不到同值，需要人判是否差异）；
  3) `--strings <kw>`：按关键词过滤可执行里的 ASCII 字符串（用于找 cvar 名 /
     标识符 / HUD 精灵名这类"名字级"证据，名字存在是"机制存在"的证据，不是值的证据）。

证据强度（照 SKILL §4.7 分级）
-----------------------------
本工具产出 = **L2（脚本采集产物）**：字节偏移可被任何人用 `xxd` 复核。
⛔ 命中 == 该 4 字节序列在文件里出现 ⇒ **不**等于"这就是那个语义的常量"
（浮点常量会被编译器共用 / 也可能出现在无关数据里）⇒ 回报里必须写明这一点，
不许把它说成"已 1:1 对齐"。

Exit 0 = 扫描完成（有没有命中都算完成）；2 = 输入不可读 / 不是 PE。
"""

import argparse
import os
import re
import struct
import sys

CONST_RE = re.compile(
    r"public\s+const\s+(float|int|double)\s+(\w+)\s*=\s*([-+0-9.eEfF]+)\s*;")


def parse_pe(raw):
    if raw[:2] != b"MZ":
        raise ValueError("not an MZ/PE file")
    pe_off = struct.unpack_from("<I", raw, 0x3C)[0]
    if raw[pe_off:pe_off + 4] != b"PE\x00\x00":
        raise ValueError("no PE signature at 0x%X" % pe_off)
    machine, nsec = struct.unpack_from("<HH", raw, pe_off + 4)
    opt_size = struct.unpack_from("<H", raw, pe_off + 20)[0]
    opt_off = pe_off + 24
    magic = struct.unpack_from("<H", raw, opt_off)[0]
    sec_off = opt_off + opt_size
    secs = []
    for i in range(nsec):
        b = sec_off + i * 40
        name = raw[b:b + 8].split(b"\x00")[0].decode("latin-1")
        vsize, vaddr, rsize, raddr = struct.unpack_from("<IIII", raw, b + 8)
        chars = struct.unpack_from("<I", raw, b + 36)[0]
        secs.append(dict(name=name, vsize=vsize, vaddr=vaddr,
                         rawsize=rsize, rawaddr=raddr, chars=chars))
    return dict(machine=machine, nsec=nsec, magic=magic, sections=secs)


def section_of(secs, off):
    for s in secs:
        if s["rawaddr"] <= off < s["rawaddr"] + s["rawsize"]:
            return s
    return None


def parse_consts(cs_path):
    src = open(cs_path, encoding="utf-8").read()
    out = []
    for kind, name, lit in CONST_RE.findall(src):
        raw = lit.rstrip("fF")
        try:
            out.append((name, kind, float(raw)))
        except ValueError:
            continue
    return out


def scan(raw, secs, value, kind):
    if kind == "int":
        if float(value) != int(value):
            return []
        blob = struct.pack("<i", int(value))
    else:
        blob = struct.pack("<f", float(value))
    hits = []
    start = 0
    while True:
        i = raw.find(blob, start)
        if i < 0:
            break
        s = section_of(secs, i)
        hits.append((i, s["name"] if s else "(hdr/overlay)"))
        start = i + 1
    return hits


def section_strings(raw, secs, minlen, keywords):
    kws = [k.lower() for k in keywords]
    out = []
    for s in secs:
        data = raw[s["rawaddr"]:s["rawaddr"] + s["rawsize"]]
        for m in re.finditer(rb"[\x20-\x7e]{%d,}" % minlen, data):
            txt = m.group().decode("latin-1")
            low = txt.lower()
            if any(k in low for k in kws):
                out.append((s["name"], s["rawaddr"] + m.start(), txt))
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dll", required=True)
    ap.add_argument("--label", default=None)
    ap.add_argument("--scan-cs", action="append", default=[])
    ap.add_argument("--strings", nargs="*", default=None)
    ap.add_argument("--minlen", type=int, default=5)
    ap.add_argument("--limit", type=int, default=6)
    a = ap.parse_args()

    raw = open(a.dll, "rb").read()
    label = a.label or os.path.basename(a.dll)
    try:
        pe = parse_pe(raw)
    except ValueError as e:
        print("FAIL %s: %s" % (a.dll, e), file=sys.stderr)
        return 2

    print("=== %s  (%d bytes) ===" % (label, len(raw)))
    print("PE      : machine=0x%04X (%s)  nsec=%d  optmagic=0x%X"
          % (pe["machine"], "i386" if pe["machine"] == 0x14C else "?",
             pe["nsec"], pe["magic"]))
    print("sections:")
    for s in pe["sections"]:
        print("  %-8s vaddr=0x%-8X vsize=%-8d rawaddr=0x%-8X rawsize=%d"
              % (s["name"], s["vaddr"], s["vsize"], s["rawaddr"], s["rawsize"]))

    if a.strings is not None:
        print("\n--- strings (len>=%d, keywords=%s) ---" % (a.minlen, a.strings))
        for sec, off, txt in section_strings(raw, pe["sections"], a.minlen, a.strings):
            print("  %-8s 0x%-8X %s" % (sec, off, txt))

    if a.scan_cs:
        consts = []
        for p in a.scan_cs:
            consts += parse_consts(p)
        print("\n--- const scan (%d consts from %d file(s)) ---"
              % (len(consts), len(a.scan_cs)))
        hitn = missn = 0
        for name, kind, val in consts:
            hits = scan(raw, pe["sections"], val, kind)
            if hits:
                hitn += 1
                shown = ", ".join("0x%X(%s)" % (o, s) for o, s in hits[:a.limit])
                more = "" if len(hits) <= a.limit else " +%d more" % (len(hits) - a.limit)
                print("  HIT  %-26s %-5s %-10s x%-3d %s%s"
                      % (name, kind, val, len(hits), shown, more))
            else:
                missn += 1
                print("  MISS %-26s %-5s %-10s (该值不在本可执行的字节流里)"
                      % (name, kind, val))
        print("  ---- HIT=%d MISS=%d ----" % (hitn, missn))
    return 0


if __name__ == "__main__":
    sys.exit(main())
