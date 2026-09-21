# -*- coding: utf-8 -*-
# 片AC：用 compose-contact-sheet.py 的**同参数**（cell 480x320 + title）重拼受影响的联络图，
# 几何尺寸必须与重拼前一致（1920x722 / 1440x1068 / 1920x2106）。
import importlib.util
import os
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
CCS = os.path.join(ROOT, 'tools', 'probes', 'compose-contact-sheet.py')
spec = importlib.util.spec_from_file_location('ccs', CCS)
ccs = importlib.util.module_from_spec(spec)
spec.loader.exec_module(ccs)

JOBS = [
    ('22_appearance-sheet.png', 4, '480x320',
     '22 外观联络图（HUD 图标行 / 第一人称 FOV / viewmodel / 3 号槽 / H 菜单 / 记分板 / 准星 / CT 局内整层；片AC 同批重采）',
     'sliceO-22-appearance.manifest.tsv'),
    ('23_appearance-sheet.png', 3, '480x320',
     '23 外观联络图（1:1 HUD / 五个武器槽 / 第三人称 / 角色正面 / 第一人称 viewmodel；片AC 同批重采）',
     'sliceO-23-appearance.manifest.tsv'),
    ('contact-sheet-2-ingame.png', 4, '480x320',
     '联络图2 · 局内（HUD / 买枪 / H 菜单 / 无线电 / 控制台 / 记分板 / 观察 / 回合与比赛结束；片AC 同批重采）',
     'sliceO-sheet-2-ingame.manifest.tsv'),
]

for out, cols, cell, title, man in JOBS:
    sys.argv = ['ccs', '--out', os.path.join(ROOT, '.ai-tmp', 'screenshots', out),
                '--cols', str(cols), '--cell-size', cell, '--title', title,
                '--manifest', os.path.join(ROOT, 'tools', 'probes', man)]
    print('==== ' + out + ' ====')
    ccs.main()
