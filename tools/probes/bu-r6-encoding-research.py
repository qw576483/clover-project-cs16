#!/usr/bin/env python
# -*- coding: utf-8 -*-
# 片BU-R6 §5：把 BU-R5 的 console dump 用"原样文本"和"还原文本"两种形态各搜一遍。
# 两种形态都搜（skill experience/verify-recipes.md §4：不许先做"编码修复"）。
import io
import os
import sys

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')

PROJ = r'C:\Work\Server\f-v2\clover-project-cs16'
TMP = os.path.join(PROJ, '.ai-tmp', 'test')

KEYS = ['安放 C4', '下包', 'C4 已安放', '交给自己', '拾起', 'TP', 'C4 交给']
CTRL = ['BOTFLIP', '[Bot]', '[Match]']


def main():
    path = os.path.join(TMP, 'bu-r5-console.json')
    if not os.path.exists(path):
        print('MISSING %s' % path)
        return 1
    raw = io.open(path, 'rb').read()
    print('%s bytes = %d' % (path, len(raw)))

    # 形态 1：原样（UTF-8 解码，不做任何转码）
    txt_raw = raw.decode('utf-8', errors='replace')
    # 形态 2："还原"（实测口径，见 B7 行）：原始 UTF-8 字节被当成 GBK 读 ⇒ 中文字符被 mojibake。
    #   还原 = utf8 解码 -> 把那些字符再按 GBK 编回字节 -> 按 UTF-8 解码。
    try:
        txt_fix = txt_raw.encode('gbk', errors='replace').decode('utf-8', errors='replace')
    except Exception as e:
        txt_fix = ''
        print('FIX-gbk-utf8 restore failed: %s' % e)

    for label, txt in (('RAW-utf8', txt_raw), ('FIX-gbk', txt_fix)):
        print('')
        print('--- %s ---' % label)
        for k in CTRL + KEYS:
            n = txt.count(k)
            print('  %-12s %d' % (k, n))

    # \uXXXX 转义形态：如果 dump 里把中文写成 \uXXXX
    import re
    print('')
    print('--- \\uXXXX escaped form ---')
    for k in KEYS:
        esc = ''.join('\\u%04X' % ord(c) for c in k)
        print('  %-12s %d' % (k, txt_raw.count(esc)))
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
