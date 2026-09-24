# -*- coding: utf-8 -*-
"""判据资产｜清理前的**反向引用扫描**：待删/待搬项**被谁引用**？（输入域显式、可复现）

⛔ **与 `bwp-citation-census` 的区别**（防后来者混为一谈）：
   · 本脚本问：「**待删的那个路径**被谁引用」⇒ 清理前的自保，结论 = 可删 / 需搬 / 需改指；
   · `bwp-citation-census.tsv` 问：「判定行**证据列**里的引用可信度」⇒ 那些 `file:line` / `guid`
     能不能打开、窗口里有没有相关 token。
   两者**输入域、维度、结论都不同 ⇒ 不可互推、不可互相替代**（"看起来像覆盖、其实不同"）。

硬要求（team-lead 2026-09-23 裁定，四条并列 —— 都是被实测教训逼出来的）：
  ① 扫描根**必须含 `.ai-tmp/**`**，且用**递归列举**：⛔ 不依赖工具默认的 ignore 行为
     （实测 `ripgrep` **尊重 `.gitignore`**，而 `.ai-tmp` 被 ignore ⇒ 默认扫出"0 引用"= 不可信结论）；
  ② 打印 **scanned（总数 + 各根文件数）** 与 **hits（总数 + 各根命中数）**；
  ③ 打印**排除清单本身**：否则"排除"就是新一次静默缩小（本脚本的前身正因自己的排除项，
     产出过一份不可信的"双侧 0 引用"）；
  ④ 输出头打印 **cmd（原样 `sys.argv`）+ roots + excluded + scanned/hits** ⇒ 第三方**不猜**即可复跑。

用法：
  python tools/probes/reverse-ref-scan.py --items bwer-a.py,bwer-b2.py,...
  python tools/probes/reverse-ref-scan.py --items-file <清单.txt>        # 一行一项
输出：结论表 = `<待删项> -> refs=N -> 引用者(file:line) -> 结论(可删 / 需搬 / 需改指 / 仅文字提及)`
"""
import fnmatch
import io
import os
import sys

sys.dont_write_bytecode = True
ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

ROOTS = ['\u7b56\u5212', 'tools', '.ai-tmp', 'client', 'docs']       # ① .ai-tmp 必须在
SKIP_DIRS = {'.git', 'Library', 'Temp', 'obj', 'bin', 'Logs', 'node_modules',
             '__pycache__', '.vs', 'Build', 'Builds'}
SKIP_EXTS = {
    '.png', '.jpg', '.jpeg', '.bmp', '.gif', '.tga', '.dds', '.dll', '.exe', '.bin', '.bsp',
    '.wad', '.zip', '.7z', '.pyc', '.pdb', '.so', '.dylib', '.ttf', '.otf', '.mp3', '.wav',
    '.ogg', '.fbx', '.anim', '.asset', '.unity', '.prefab', '.mat', '.controller', '.meta',
    '.rsp', '.lock', '.tzst',
}
MAX_SIZE = 2 * 1024 * 1024
# "仅文字提及"：过程证据/日志/快照类文本 —— 它们**不是**判据引用（prose），但必须列出来给人看
PROSE_PAT = ('*-out.txt', '*.json', '*-stderr.txt', '*report*')


def parse_items(argv):
    items, files = [], []
    for i, a in enumerate(argv):
        if a.startswith('--items='):
            items += [x.strip() for x in a.split('=', 1)[1].split(',') if x.strip()]
        elif a == '--items' and i + 1 < len(argv):
            items += [x.strip() for x in argv[i + 1].split(',') if x.strip()]
        elif a.startswith('--items-file='):
            files.append(a.split('=', 1)[1])
        elif a == '--items-file' and i + 1 < len(argv):
            files.append(argv[i + 1])
    for f in files:
        items += [l.strip() for l in io.open(f, encoding='utf-8') if l.strip()]
    return sorted(set(items))


def walk_root(root):
    base = os.path.join(ROOT, root)
    out = []
    for dp, dn, fn in os.walk(base):
        dn[:] = [d for d in dn if d not in SKIP_DIRS]           # 递归列举，⛔ 不用工具默认 ignore
        for f in fn:
            p = os.path.join(dp, f)
            if os.path.splitext(f)[1].lower() in SKIP_EXTS:
                continue
            try:
                if os.path.getsize(p) > MAX_SIZE:
                    continue
            except OSError:
                continue
            out.append(p)
    return out


def main(argv):
    items = parse_items(argv)
    if not items:
        print('usage: python tools/probes/reverse-ref-scan.py --items <a,b,c>   # 见文件头')
        return 2
    per_root, allf = {}, []
    for r in ROOTS:
        fs = walk_root(r)
        per_root[r] = len(fs)
        allf += fs
    # ---- ④ 输出头：cmd / roots / excluded / scanned ----
    print('# reverse-ref-scan   (probe asset: who references the items we are about to delete/move)')
    print('# cmd     : %s' % ' '.join(sys.argv))
    print('# roots   : %s   (recursive enumeration; NOT the tool default ignore behaviour)'
          % ' '.join(ROOTS))
    print('# excluded: dirs=%s' % ','.join(sorted(SKIP_DIRS)))
    print('#           ext(%d)=%s' % (len(SKIP_EXTS), ','.join(sorted(SKIP_EXTS))))
    print('#           max_file_size=%d bytes' % MAX_SIZE)
    print('#           prose-class (NOT a criterion reference): %s' % ','.join(PROSE_PAT))
    print('# scanned : total=%d  (%s)'
          % (len(allf), ' / '.join('%s %d' % (r, per_root[r]) for r in ROOTS)))
    # ---- 扫描 ----
    hits = dict((t, []) for t in items)
    total_hits = 0
    for p in allf:
        try:
            txt = io.open(p, encoding='utf-8', errors='replace').read()
        except OSError:
            continue
        if not txt:
            continue
        rel = os.path.relpath(p, ROOT).replace('\\', '/')
        for i, line in enumerate(txt.split('\n'), 1):
            for t in items:
                if t in line:
                    hits[t].append((rel, i, line.strip()[:110]))
                    total_hits += 1
    hit_root = {}
    for t in items:
        for rel, _i, _l in hits[t]:
            r0 = rel.split('/')[0]
            hit_root[r0] = hit_root.get(r0, 0) + 1
    print('# hits    : total=%d  (%s)'
          % (total_hits, ' / '.join('%s %d' % (r, hit_root.get(r, 0)) for r in ROOTS)))
    print('# ---- conclusion table ----')
    for t in items:
        refs = hits[t]
        prose = [r for r in refs if any(fnmatch.fnmatch(os.path.basename(r[0]), g) for g in PROSE_PAT)]
        code = [r for r in refs if r not in prose]
        inner = [r for r in code if os.path.basename(r[0]) in items]
        outer = [r for r in code if r not in inner]
        # 按"**能不能是可执行的引用**"分档：注释行（`#` 开头）与过程日志都**不是**依赖，
        #    只有"代码/字符串里的真实路径"才算"需改指"。判据是**行首是否 `#`**（精确），
        #    不按"在不在字符串里"判 —— 真实引用恰恰长在字符串里（`os.path.join(...,'x.py')`）。
        def _cl(rec):
            return rec[2].lstrip().startswith('#')
        outer_code = [r for r in outer if not _cl(r)]
        outer_comment = [r for r in outer if _cl(r)]
        verdict = ('no reference anywhere' if not refs else
                   ('same batch only (internal)' if not outer and not prose else
                    # 这里**不**断言"必须改指"：命中行在 `.py` 里也可能是**来源注记**
                    #    （docstring/注释）⇒ 只报"有代码侧命中、须人判"，不替人下结论。
                    ('OUTER ref(s) -- judge by hand (repoint? or provenance note?): %s'
                     % ','.join(sorted(set(r[0] for r in outer_code)))
                     if outer_code else
                     ('comment/prose mentions only (%d comment, %d prose) -- no executable dependency'
                      % (len(outer_comment), len(prose))))))
        print('# %s  -> refs=%d -> %s' % (t, len(refs), verdict))
        for rel, i, l in refs:
            tag = ('prose' if (rel, i, l) in prose else
                   ('inner' if (rel, i, l) in inner else
                    ('comment' if l.lstrip().startswith('#') else 'OUTER')))
            print('#     [%s] %s:%d  %s' % (tag, rel, i, l))
    print('# NOTE: prose 类命中 = 过程日志/快照里**提到**了该名字，不是判据引用（⛔ 不据此定"可删"）。')
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
