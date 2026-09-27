# -*- coding: utf-8 -*-
"""逐行解析原版 CS 1.6 的 `sound/materials.txt`（贴图名 -> 材质字母），导出：

  1) 文本表   client/Assets/ThirdParty/Dust2/materials_letters.tsv
  2) C# 数据表（--csharp 打到 stdout：整块 `internal static class MaterialLetters`，
     粘进 `client/Assets/Scripts/Module/Audio/CsAudioTuning.cs`）

解析口径 = 原版 `PM_InitTextureTypes` 的逐语句搬运
--------------------------------------------------
出处（载体在盘，可复核）：`原版资源/hlsdk/pm_shared/pm_shared.c`
  * 行文法           :217-249   （memfgets 逐行；首个非空白字符是 '/' 或非字母 => 跳过；
                                 字母 = toupper(该字符)；再跳空白；名字扫到空白为止；
                                 `if (!buffer[j]) continue;` => **行尾没有空白/换行的行被丢弃**；
                                 `j = min(j, CBTEXTURENAMEMAX - 1 + i)` => 名字最长 16 字符）
  * 常量             :96       `CTEXTURESMAX 1024`
                     :98       `CBTEXTURENAMEMAX 17`
  * 排序             :178-190   冒泡，只在**严格大于**时交换 => 同名条目保留文件序
  * 查表             :258-289   `strnicmp(name, stored, CBTEXTURENAMEMAX - 1)`（大小写不敏感、
                                 按前 16 字符**整名相等**）；查不到 -> `CHAR_TEX_CONCRETE`（'C'）
  * 字母集           :100-111   CHAR_TEX_{CONCRETE 'C', METAL 'M', DIRT 'D', VENT 'V',
                                 GRATE 'G', TILE 'T', SLOSH 'S', WOOD 'W', COMPUTER 'P',
                                 GLASS 'Y', FLESH 'F', SNOW 'N'}

⛔ 与 `materials.txt` 自带头注释的两处出入（**以代码为准**，如实打印）：
  * 头注释（materials.txt:10）写「a maximum of 512 textures」，代码是 `CTEXTURESMAX 1024`；
  * 头注释（materials.txt:9）写「only the first 12 characters of the texture name are used」，
    代码按 `CBTEXTURENAMEMAX - 1` = **16** 字符比较。

自洽断言（跑不过就 exit 2，绝不输出半份表）
------------------------------------------
  1. 源文件在盘且 SHA256 与登记值一致（`策划/差异登记.tsv` #55 记的字节数/SHA256）；
  2. 每条通过的行：字母 ∈ A-Z、名字非空、名字长度 <= 16；
  3. 通过条目数 <= CTEXTURESMAX；
  4. 同一查表键（小写、前 16 字符）的条目若字母不同 => 打印该组并如实声明"原版对该情形不确定，
     本表按文件序取第一条"；
  5. 逐行计数：总行数 = 空白 + 注释/非字母 + 无名字 + 行尾无空白（被丢弃） + 通过条目。

用法
----
  python tools/probes/cs16_materials_table.py                 # 逐条明细 + 汇总 + dust2 交叉
  python tools/probes/cs16_materials_table.py --out <tsv 路径>
  python tools/probes/cs16_materials_table.py --csharp        # 只打印 C# 表块
只读载体与磁盘上的贴图目录名；--out 只在给定路径写文本。
"""

import argparse
import hashlib
import os
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
MATERIALS = os.path.join(ROOT, "原版资源", "cs16src", "cstrike", "sound", "materials.txt")
SURFACE = os.path.join(ROOT, "client", "Assets", "ThirdParty", "Dust2", "surface_flags.tsv")
OUT_DEFAULT = os.path.join(ROOT, "client", "Assets", "ThirdParty", "Dust2", "materials_letters.tsv")

# 登记在 策划/差异登记.tsv #55 里的载体指纹（不符 => 载体被换过，必须重核）
MATERIALS_BYTES = 11064
MATERIALS_SHA256 = "0D35D708CB3737FC99CAF50E41AE32680E197218D2A51488489ECB7561B3AB6C"

CTEXTURESMAX = 1024      # pm_shared.c:96
CBTEXTURENAMEMAX = 17    # pm_shared.c:98
NAME_MAX = CBTEXTURENAMEMAX - 1   # = 16，pm_shared.c:246 的截断上限
KEYLEN = CBTEXTURENAMEMAX - 1     # = 16，pm_shared.c:272 的比较长度

# materials.txt:12-22 头注释声明的字母集（与 pm_shared.c:100-111 合起来才是全字母表）
HEADER_LETTERS = "MVDSTGWPYNX"
# pm_shared.c:100-111 的全部字母（含头注释没写的 C / F）
CODE_LETTERS = "CMDVGTSPWYFN"

CMAX_MISS_LETTER = "C"   # pm_shared.c:288 的默认返回值 CHAR_TEX_CONCRETE


def is_ascii_alpha(c):
    """C locale 的 isalpha（pm_shared.c:227）—— 只认 A-Za-z，不用 Python 的 Unicode 版。"""
    return ("a" <= c <= "z") or ("A" <= c <= "Z")


def is_c_space(c):
    """C locale 的 isspace（pm_shared.c:221/232/240）。"""
    return c in " \t\n\v\f\r"


def parse_line(buffer):
    """逐语句搬运 pm_shared.c:219-248。

    -> ((letter, name), None) 通过；否则 (None, 原因)。
    原因 ∈ blank / nonalpha / no-name / no-trailing-space（原版 :243 丢弃）。
    """
    n = len(buffer)
    i = 0
    while i < n and is_c_space(buffer[i]):          # :221-222
        i += 1
    if i >= n:                                       # :224-225
        return None, "blank"
    if buffer[i] == "/" or not is_ascii_alpha(buffer[i]):   # :227-228
        return None, "nonalpha"

    letter = buffer[i].upper()                       # :230
    i += 1

    while i < n and is_c_space(buffer[i]):           # :232-233
        i += 1
    if i >= n:                                       # :235-236
        return None, "no-name"

    j = i
    while j < n and not is_c_space(buffer[j]):       # :240-241
        j += 1
    if j >= n:                                       # :243-244 行尾既无空白也无换行 => 丢弃
        return None, "no-trailing-space"

    j = min(j, NAME_MAX + i)                         # :246 名字最长 NAME_MAX 个字符
    return (letter, buffer[i:j]), None


def parse(text):
    """-> (entries, stats)。entries = [(line_no, letter, name)]，保持文件序（= 原版排序后的稳定序）。"""
    entries = []
    stats = {"lines": 0, "blank": 0, "nonalpha": 0, "no-name": 0, "no-trailing-space": 0}
    for line_no, raw in enumerate(text.splitlines(keepends=True), 1):
        stats["lines"] += 1
        got, why = parse_line(raw)
        if got is None:
            stats[why] += 1
            continue
        entries.append((line_no, got[0], got[1]))
        if len(entries) >= CTEXTURESMAX:
            break
    stats["entries"] = len(entries)
    return entries, stats


def key_of(name):
    """原版查表键：小写、前 KEYLEN 字符（pm_shared.c:272 的 strnicmp 语义）。"""
    return name[:KEYLEN].lower()


def letter_histogram(entries):
    hist = {}
    for _, letter, _ in entries:
        hist[letter] = hist.get(letter, 0) + 1
    return hist


def duplicate_groups(entries):
    """查表键重复的组（含字母不同的冲突组）。-> [(key, [(line,letter,name), ...]), ...]"""
    by_key = {}
    for line_no, letter, name in entries:
        by_key.setdefault(key_of(name), []).append((line_no, letter, name))
    return [(k, v) for k, v in by_key.items() if len(v) > 1]


def write_tsv(path, entries, stats, sha256, size, dups):
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write("# 原版 CS 1.6 `sound/materials.txt` 的「贴图名 -> 材质字母」表（逐行解析，非人工抄录）\n")
        f.write("# 生成器 = tools/probes/cs16_materials_table.py（可重跑）\n")
        f.write("# 源 = 原版资源/cs16src/cstrike/sound/materials.txt（%d B / %d 行 / LF / SHA256 %s）\n"
                % (size, stats["lines"], sha256))
        f.write("# 解析口径 = 原版 `PM_InitTextureTypes` 的逐语句搬运，出处 原版资源/hlsdk/pm_shared/pm_shared.c：\n")
        f.write("#   行文法 :217-249（'/' 或非字母开头 => 跳过；字母 = toupper；名字扫到空白为止；\n")
        f.write("#             行尾无空白 => 丢弃；名字截断到 %d 字符）；常量 :96 CTEXTURESMAX=%d / :98 CBTEXTURENAMEMAX=%d\n"
                % (NAME_MAX, CTEXTURESMAX, CBTEXTURENAMEMAX))
        f.write("#   排序 :178-190（只在严格大于时交换 => 同名条目保留文件序）\n")
        f.write("#   查表 :258-289（strnicmp(name, stored, %d) = 大小写不敏感、按前 %d 字符整名相等；查不到 -> '%s'）\n"
                % (KEYLEN, KEYLEN, CMAX_MISS_LETTER))
        f.write("#   字母集 :100-111（含头注释没写的 'C'/'F'）\n")
        f.write("# 逐行计数：总 %d 行 = 空白 %d + 注释/非字母 %d + 无名字 %d + 行尾无空白(丢弃) %d + 通过条目 %d\n"
                % (stats["lines"], stats["blank"], stats["nonalpha"], stats["no-name"],
                   stats["no-trailing-space"], stats["entries"]))
        f.write("# 重复查表键：%d 组 —— 原版对同键不同字母的命中结果取决于二分路径（原版不确定）；\n"
                % len(dups))
        f.write("#   本表保留**全部**条目，运行期取文件序第一条（见 CsAudioTuning.MaterialLetters.TryGetLetter）。\n")
        f.write("# 列：line=源文件行号(1 基) letter=材质字母 name=贴图名(<=%d 字符) key=查表键(小写前 %d 字符) dup=该键条目数\n"
                % (NAME_MAX, KEYLEN))
        f.write("line\tletter\tname\tkey\tdup\n")
        counts = {}
        for _, letter, name in entries:
            counts[key_of(name)] = counts.get(key_of(name), 0) + 1
        for line_no, letter, name in entries:
            k = key_of(name)
            f.write("%d\t%s\t%s\t%s\t%d\n" % (line_no, letter, name, k, counts[k]))


def csharp_block(entries, stats, sha256, size):
    L = []
    L.append("        /// <summary>")
    L.append("        /// 原版 CS 1.6 `sound/materials.txt` 的「贴图名 -> 材质字母」表。")
    L.append("        ///")
    L.append("        /// <para><b>表本身是数据、不是手写常量</b>：由 <c>tools/probes/cs16_materials_table.py --csharp</c>")
    L.append("        /// 逐行打印，⚠️ 手改这张表等于绕过生成器 —— 要改就改生成器再重跑。")
    L.append("        /// 人读版本 = <c>client/Assets/ThirdParty/Dust2/materials_letters.tsv</c>。</para>")
    L.append("        ///")
    L.append("        /// <para><b>载体</b>：<c>原版资源/cs16src/cstrike/sound/materials.txt</c>"
             "（%d B / %d 行 / SHA256 <c>%s</c>）。</para>" % (size, stats["lines"], sha256))
    L.append("        ///")
    L.append("        /// <para><b>解析口径</b> = 原版 <c>PM_InitTextureTypes</c> 的逐语句搬运，出处")
    L.append("        /// <c>原版资源/hlsdk/pm_shared/pm_shared.c</c>：行文法 <c>:217-249</c>"
             "（<c>'/'</c> 或非字母开头跳过；字母 <c>toupper</c>；名字扫到空白；行尾无空白则丢弃；")
    L.append("        /// 名字截断到 <c>CBTEXTURENAMEMAX-1</c> = %d 字符）；查表 <c>:258-289</c>"
             "（<c>strnicmp(name, stored, %d)</c> = 大小写不敏感、按前 %d 字符整名相等；" % (NAME_MAX, KEYLEN, KEYLEN))
    L.append("        /// 查不到返回 <c>CHAR_TEX_CONCRETE</c> '<c>%s</c>'，<c>:288</c>）。</para>" % CMAX_MISS_LETTER)
    L.append("        ///")
    L.append("        /// <para><b>同名不同字母</b>：原版 <c>PM_SortTextures</c>（<c>:178-190</c>）只在严格大于时交换，"
             "同名条目保留文件序；")
    L.append("        /// 但 <c>PM_FindTextureType</c> 是二分查找，同键命中哪一条**原版自己不确定**。")
    L.append("        /// 本表保留全部条目，<see cref=\"TryGetLetter\"/> 取**文件序第一条**并只 Warn 一次（见调用点）。</para>")
    L.append("        /// </summary>")
    L.append("        public static class MaterialLetters")
    L.append("        {")
    L.append("            /// <summary>一行的两个字段。名字是原版文件里的原样（<= %d 字符），比较时才截断运行时名。</summary>" % NAME_MAX)
    L.append("            private struct Row")
    L.append("            {")
    L.append("                public readonly string Texture;")
    L.append("                public readonly char Letter;")
    L.append("")
    L.append("                public Row(string texture, char letter)")
    L.append("                {")
    L.append("                    Texture = texture;")
    L.append("                    Letter = letter;")
    L.append("                }")
    L.append("            }")
    L.append("")
    L.append("            /// <summary>表（文件序；%d 条，行号见 materials_letters.tsv）。</summary>" % stats["entries"])
    L.append("            private static readonly Row[] Table =")
    L.append("            {")
    for _, letter, name in entries:
        L.append("            new Row(\"%s\", '%s')," % (name.replace("\\", "\\\\").replace("\"", "\\\""), letter))
    L.append("            };")
    L.append("")
    L.append("            /// <summary>表里的行数（离线宿主用它核对「表没被截断」）。</summary>")
    L.append("            public static int TableCount => Table.Length;")
    L.append("")
    L.append("            /// <summary>按序号读表的一行（供离线自检遍历；越界返回 false）。</summary>")
    L.append("            public static bool TryGetRow(int index, out string texture, out char letter)")
    L.append("            {")
    L.append("                texture = null;")
    L.append("                letter = '\\0';")
    L.append("                if (index < 0 || index >= Table.Length) return false;")
    L.append("                texture = Table[index].Texture;")
    L.append("                letter = Table[index].Letter;")
    L.append("                return true;")
    L.append("            }")
    L.append("")
    L.append("            /// <summary>")
    L.append("            /// 按运行时材质名查材质字母（纯函数，不依赖 Unity，可离线断言）。")
    L.append("            ///")
    L.append("            /// <para><b>比较口径</b> = 原版 <c>strnicmp(name, stored, %d)</c>（<c>pm_shared.c:272</c>）：" % KEYLEN)
    L.append("            /// 大小写不敏感；比较到库里名字的结尾为止 ⇒ 等价于「**运行时名的前 %d 字符**与库里的名字**整名相等**」。" % KEYLEN)
    L.append("            /// （<c>strnicmp</c> 用进程当前 locale 折叠大小写，这里用 <c>OrdinalIgnoreCase</c> —— "
             "表里全是 ASCII，两者对本表等价。）</para>")
    L.append("            ///")
    L.append("            /// <para><b>查不到</b>返回 <c>false</c>（本方法只回答「表里有没有这个名字」）："
             "由调用点 <c>ClassifyImpact</c> 按原版口径落默认字母 '<c>%s</c>'（<c>pm_shared.c:288</c>）。</para>"
             % CMAX_MISS_LETTER)
    L.append("            /// </summary>")
    L.append("            public static bool TryGetLetter(string texture, out char letter)")
    L.append("            {")
    L.append("                letter = '\\0';")
    L.append("                if (string.IsNullOrEmpty(texture)) return false;")
    L.append("")
    L.append("                var key = texture.Length > %d ? texture.Substring(0, %d) : texture;" % (KEYLEN, KEYLEN))
    L.append("                for (var i = 0; i < Table.Length; i++)")
    L.append("                {")
    L.append("                    if (string.Equals(Table[i].Texture, key, System.StringComparison.OrdinalIgnoreCase))")
    L.append("                    {")
    L.append("                        letter = Table[i].Letter;")
    L.append("                        return true;")
    L.append("                    }")
    L.append("                }")
    L.append("                return false;")
    L.append("            }")
    L.append("        }")
    return "\n".join(L)


def dust2_cross(entries):
    """surface_flags.tsv 的贴图名逐条查原版表 -> [(name, letter or None)]。"""
    table = [key_of(n) for _, _, n in entries]
    out = []
    with open(SURFACE, encoding="utf-8") as f:
        for line in f:
            if line.startswith("#") or not line.strip():
                continue
            c = line.rstrip("\n").split("\t")
            if c[0] == "model":      # 表头行（materials_letters.tsv 同款：表头首列是列名）
                continue
            if not c or not c[0]:
                continue
            out.append(c[0])
    hit = []
    for name in out:
        k = key_of(name)
        letter = None
        for (ln, lt, nm) in entries:
            if key_of(nm) == k:
                letter = lt
                break
        hit.append((name, letter))
    return hit


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--materials", default=MATERIALS)
    ap.add_argument("--out", default=OUT_DEFAULT)
    ap.add_argument("--csharp", action="store_true")
    a = ap.parse_args()

    if not os.path.isfile(a.materials):
        print("FATAL: 源文件不在盘：%s" % a.materials)
        return 2
    raw = open(a.materials, "rb").read()
    sha256 = hashlib.sha256(raw).hexdigest().upper()
    text = raw.decode("latin-1")     # 载体是纯 ASCII；latin-1 保证字节与字符一一对应

    entries, stats = parse(text)
    dups = duplicate_groups(entries)
    hist = letter_histogram(entries)

    # ---------------- 自洽断言 ----------------
    errs = []
    if len(raw) != MATERIALS_BYTES or sha256 != MATERIALS_SHA256:
        errs.append("载体指纹不符：实测 %d B / %s，登记 %d B / %s"
                    % (len(raw), sha256, MATERIALS_BYTES, MATERIALS_SHA256))
    for ln, lt, nm in entries:
        if len(lt) != 1 or not ("A" <= lt <= "Z"):
            errs.append("行 %d：字母非 A-Z：%r" % (ln, lt))
        if not nm:
            errs.append("行 %d：名字为空" % ln)
        if len(nm) > NAME_MAX:
            errs.append("行 %d：名字超长 %d > %d：%r" % (ln, len(nm), NAME_MAX, nm))
    if len(entries) > CTEXTURESMAX:
        errs.append("通过条目 %d > CTEXTURESMAX %d" % (len(entries), CTEXTURESMAX))
    total = stats["blank"] + stats["nonalpha"] + stats["no-name"] + stats["no-trailing-space"] + stats["entries"]
    if total != stats["lines"]:
        errs.append("逐行计数不自洽：%d != 总行数 %d" % (total, stats["lines"]))

    if errs:
        print("SELFCHECK FAILED（不输出半份表）：")
        for e in errs:
            print("  -", e)
        return 2

    if not a.csharp:
        print("源        : %s" % os.path.relpath(a.materials, ROOT).replace("\\", "/"))
        print("字节/指纹 : %d B / SHA256 %s" % (len(raw), sha256))
        print("逐行计数  : 总 %d 行 = 空白 %d + 注释/非字母 %d + 无名字 %d + 行尾无空白(丢弃) %d + 通过条目 %d"
              % (stats["lines"], stats["blank"], stats["nonalpha"], stats["no-name"],
                 stats["no-trailing-space"], stats["entries"]))
        print("selfcheck : OK（字母/名字长度/CTEXTURESMAX/逐行计数 全过）")
        print()
        print("字母分布（%d 个字母 / %d 条）：" % (len(hist), stats["entries"]))
        for lt in sorted(hist):
            mark = "" if lt in HEADER_LETTERS else ("  <- 头注释没写" if lt in CODE_LETTERS else "  <- 表外字母")
            print("  %s : %3d%s" % (lt, hist[lt], mark))
        missing = [c for c in CODE_LETTERS if c not in hist]
        print("  pm_shared.c:100-111 里本文件没出现的字母：%s" % ("、".join(missing) or "无"))
        print()
        print("重复查表键（%d 组；本表取文件序第一条，原版对同键不同字母不确定）：" % len(dups))
        for k, v in dups:
            letters = "".join(sorted({x[1] for x in v}))
            flag = "  <== 同键不同字母" if len(letters) > 1 else ""
            print("  %-18s x%d  字母=%s  行=%s%s"
                  % (k, len(v), letters, ",".join(str(x[0]) for x in v), flag))
        print()
        print("== dust2 交叉：surface_flags.tsv 的贴图名查原版表 ==")
        cross = dust2_cross(entries)
        hits = [(n, lt) for n, lt in cross if lt]
        print("  surface_flags.tsv 贴图名 %d 个 -> 命中 %d 个" % (len(cross), len(hits)))
        for n, lt in cross:
            print("    %-18s -> %s" % (n, lt if lt else "未收录（原版此处默认 '%s'）" % CMAX_MISS_LETTER))
        print()

    if a.out and not a.csharp:
        write_tsv(a.out, entries, stats, sha256, len(raw), dups)
        print("wrote %s  (%d rows)" % (a.out, len(entries)))

    if a.csharp:
        print(csharp_block(entries, stats, sha256, len(raw)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
