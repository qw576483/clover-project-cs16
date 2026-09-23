#!/usr/bin/env python
# -*- coding: utf-8 -*-
# 打印还原形态下命中行的原文（含时间戳）。
import io
import os
import sys

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')
PROJ = r'C:\Work\Server\f-v2\clover-project-cs16'
path = os.path.join(PROJ, '.ai-tmp', 'test', 'bu-r5-console.json')

raw = io.open(path, 'rb').read()
txt = raw.decode('utf-8', errors='replace').encode('gbk', errors='replace').decode('utf-8', errors='replace')

KEYS = ['安放 C4', '下包', 'C4 交给', '拾起', '掉落', 'C4 掉']
for k in KEYS:
    print('')
    print('=== %s ===' % k)
    start = 0
    n = 0
    while True:
        i = txt.find(k, start)
        if i < 0 or n >= 8:
            break
        # 往左找 message 字段起点
        j = txt.rfind('"message": "', 0, i)
        seg = txt[j + 12: i + 220] if j >= 0 else txt[max(0, i - 120): i + 200]
        seg = seg.split('\\n')[0].split('\n')[0]
        print('  %s' % seg[:260])
        start = i + 1
        n += 1
    if n == 0:
        print('  (0 hit)')
