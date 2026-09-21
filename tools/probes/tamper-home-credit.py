# 判据资产（tools/probes/）：verify.ps1 第 25 条（home-credit-rendered）的**篡改验证**
# —— SKILL 0.6：一条**测不红**的规则不是闸门。把 dump 里那行署名改成大小写错的
# `By Clover-Engine`，闸门必须报 FAIL；还原后必须回到 PASS。
#   python tools/probes/tamper-home-credit.py tamper    -> 改 dump => verify 必须 FAIL
#   python tools/probes/tamper-home-credit.py restore   -> 还原（备份留在 .ai-tmp/test/）
# 实测（2026-09-21 片AF）：tamper => `FAIL home-credit-rendered 1 problem(s) ... no on-screen
# text node reads exactly "by clover-engine"`，还原 => `PASS ... bottom=1024.0`。
import io
import os
import shutil
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
DUMP = os.path.join(ROOT, 'tools', 'probes', 'home-screen-nodetree.txt')
BAK = os.path.join(ROOT, '.ai-tmp', 'test', 'af-nodetree-pristine.txt')
GOOD = "text='by clover-engine'"
BAD = "text='By Clover-Engine'"

mode = sys.argv[1] if len(sys.argv) > 1 else 'tamper'
if mode == 'tamper':
    if not os.path.exists(BAK):
        shutil.copyfile(DUMP, BAK)
    t = io.open(DUMP, encoding='utf-8').read()
    assert GOOD in t, 'pristine line not found'
    n = t.replace(GOOD, BAD, 1)
    io.open(DUMP, 'w', encoding='utf-8', newline='').write(n)
    print('TAMPERED: replaced %r -> %r (1 occurrence)' % (GOOD, BAD))
elif mode == 'restore':
    assert os.path.exists(BAK), 'no backup'
    shutil.copyfile(BAK, DUMP)
    t = io.open(DUMP, encoding='utf-8').read()
    print('RESTORED: pristine line present =', GOOD in t, '| tampered line present =', BAD in t)
else:
    print('usage: tamper | restore')
