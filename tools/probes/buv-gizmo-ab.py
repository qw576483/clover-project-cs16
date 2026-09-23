# -*- coding: utf-8 -*-
"""片BU-V 判据资产：**只在编辑器 Game 视图叠加层里**的 gizmo 图标的同帧 A/B 配方（可复跑）。

要回答的问题：业务代码把"运行时创建对象的图标宿主"设成 HideFlags.HideInHierarchy 之后，
用户把 Game view 的 Gizmos 打开时，那些图标还会不会画出来？—— 必须给**运行时数字**，不许推断。

产物（判据资产，flat 落 .ai-tmp/screenshots/；索引 .ai-tmp/screenshots/buv-gizmo-ab.index.tsv）：
  buv-01-fixed.png        修复态（FirstPersonCamera 的相机宿主已 HideInHierarchy）
  buv-02-restored.png     同一冻结帧，把每个图标宿主的 hideFlags 恢复成 None（= 修复前）
  buv-03-nolight.png      同帧 + 隐藏 Light（场景 Sun）
  buv-04-nocam.png        同帧 + 再隐藏 Camera / AudioListener
  buv-05-nosound.png      同帧 + 再隐藏 34 个引擎 [Sound] AudioSource
  buv-diff-myfix.png      A/B 差集可视化：01 vs 02（本片修复贡献的像素）
  buv-diff-maincam.png    A/B 差集可视化：03 vs 04（场景 Main Camera 的 Camera+AudioListener）
  buv-diff-speakers.png   A/B 差集可视化：04 vs 05（引擎 [Sound] 的 34 个喇叭）
  汇总联络图：.ai-tmp/screenshots/buv-contact-sheet.png（人只读这一张）

口径：同一冻结帧（Play 内 timeScale=0）、Gizmos 开，两次采集之间**只**改 hideFlags
（不改渲染、不动物理、不换镜头）⇒ 每一条差集都是干净的同帧 A/B。
数字原始出处：.ai-tmp/test/buv-diff.txt；活链驱动：.ai-tmp/drivers/bu-v-play.ps1。
"""
import os
import subprocess
import sys

sys.dont_write_bytecode = True

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
SHOTS = os.path.join(ROOT, '.ai-tmp', 'screenshots')
DIFF = os.path.join(HERE, 'diff-ab.py')
OUT = os.path.join(ROOT, '.ai-tmp', 'test', 'buv-diff.txt')

# (tag, A, B, 可视化输出 or None)
PAIRS = [
    ('BU-V-myfix-fpscam-hideFlags', 'buv-01-fixed.png', 'buv-02-restored.png', 'buv-diff-myfix.png'),
    ('BU-V-sun-light', 'buv-02-restored.png', 'buv-03-nolight.png', None),
    ('BU-V-maincam+AudioListener', 'buv-03-nolight.png', 'buv-04-nocam.png', 'buv-diff-maincam.png'),
    ('BU-V-34-engine-AudioSource-speakers', 'buv-04-nocam.png', 'buv-05-nosound.png', 'buv-diff-speakers.png'),
]


def main():
    rc = 0
    chunks = []
    for tag, a, b, viz in PAIRS:
        pa, pb = os.path.join(SHOTS, a), os.path.join(SHOTS, b)
        if not (os.path.isfile(pa) and os.path.isfile(pb)):
            print('MISSING input for %s: %s / %s' % (tag, pa, pb))
            rc = 1
            continue
        cmd = [sys.executable, DIFF, pa, pb, '--tag', tag]
        if viz:
            cmd += ['--out', os.path.join(SHOTS, viz)]
        out = subprocess.run(cmd, capture_output=True, text=True).stdout
        chunks.append(out.rstrip('\n'))
        print(out.rstrip('\n'))
    text = '\n'.join(chunks) + '\n'
    with open(OUT, 'w', encoding='utf-8', newline='\n') as f:
        f.write(text)
    print('written: %s' % os.path.relpath(OUT, ROOT).replace('\\', '/'))
    return rc


if __name__ == '__main__':
    sys.exit(main())
