# 判据资产（tools/probes/）：**生成器可复现性**检查 —— 把 `enumerate-entities.py --inject`
# 再跑一遍，证明产出与盘上**逐字节一致**（并且手写在验收表里的段落没被注入抹掉）。
# 为什么必须有：SKILL 2.4「采集即冻结」/0.6「必然性规则必须做成闸门」—— "重跑生成器产出与盘上
# 一致"这条判据只有在**生成器幂等**时才立得住。切片 AF 用它抓到一个真缺陷：注入分支
# `block = '\n'.join(frag[4:])` 前面多一个空行、而只 rstrip 尾部 ⇒ **每跑一次就往 COVERAGE-BEGIN
# 前多插一个空行**（验收表里已堆 35 个 = 该分支跑过 35 次）⇒ 同输入两次产出不同字节。
# 修好后连跑两次 = BYTE-IDENTICAL。（本脚本会**真跑**注入，但注入现在幂等 ⇒ 可重复执行。）
import hashlib
import os
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
FILES = [
    os.path.join(ROOT, '策划', '覆盖矩阵判定.fragment.md'),
    os.path.join(ROOT, '策划', '验收表.md'),
    os.path.join(ROOT, '策划', '实体清单.tsv'),
    os.path.join(ROOT, '策划', '差异登记.tsv'),
]


def hashes():
    out = {}
    for p in FILES:
        with open(p, 'rb') as f:
            out[p] = hashlib.sha256(f.read()).hexdigest()
    return out


def p(s):
    # console is GBK on this box: keep the evidence ASCII so nothing dies mid-print
    print(str(s).encode('ascii', 'replace').decode('ascii'))


before = hashes()
p('--- BEFORE ---')
for f in FILES:
    p('%s  %s' % (os.path.relpath(f, ROOT), before[f]))

r = subprocess.run([sys.executable, os.path.join(ROOT, 'tools', 'probes', 'enumerate-entities.py'), '--inject'],
                   cwd=ROOT, capture_output=True, text=True, encoding='utf-8', errors='replace')
p('--- generator rc = %d ---' % r.returncode)
for ln in (r.stdout or '').strip().split('\n')[-4:]:
    p('  ' + ln)
if r.returncode != 0:
    p(r.stderr[-2000:])

after = hashes()
p('--- AFTER ---')
bad = 0
for f in FILES:
    same = before[f] == after[f]
    flag = 'IDENTICAL' if same else 'CHANGED'
    if not same:
        bad += 1
    p('%s  %s  %s' % (os.path.relpath(f, ROOT), after[f], flag))
p('BYTE-IDENTICAL' if bad == 0 else ('DRIFT in %d file(s)' % bad))
