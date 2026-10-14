#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""把 **原版 CS 1.6** 的 `cstrike/sound/**` wav 按「保持源采样率」导入工程音效目录。

背景：工程里 90/95 个 wav 的采样率字段是 **3500 Hz**（= 原版 22050 被降采样 6.3 倍，
详见 .ai-tmp/test/report-aifix.md §10），高频被砍到 1.75 kHz ⇒ 枪声发闷。
本脚本只做"忠实搬运"：**保持源采样率**、8-bit → 16-bit、单声道原样，绝不改时长/音高。

用法：
  python tools/sfx-import.py --src <原版 sound 目录> --out <工程 sfx 目录> [--only m4a1] [--dry-run]
  --dry-run 只列"哪些原版文件在/缺"，不写盘（用户补件后用它对账）。
"""
import argparse
import os
import struct
import sys
import wave

# 工程 id : [(工程文件名后缀, 原版文件名, 凭据), ...] —— 原版名 None = 未核对，跳过
#   `工程文件名后缀` 直接拼在 id 后：`_fire` → `<id>_fire.wav`（运行期 `CsWeapons.SoundFire`）；
#   `_fire_unsil` → `<id>_fire_unsil.wav`（运行期 `CsWeapons.FireSound(def, silenced:false)`，
#   目标名由 playserial 定并已写死在代码里，⛔ 别改名）。
# ⛔ 不猜：每个原版名都核对过"存在于盘上原版名单"（`client.dll` 169 条 `weapons/*.wav` 串，
#    证据 .ai-tmp/test/aifix-weaponsfx-names.txt）；带 `SRC` 的另有源码行/反汇编点作凭据。
MAP = {
    'knife':        [('_fire', 'knife_slash1.wav', '名单（另有 knife_slash2/knife_stab；取 1）')],
    'glock18':      [('_fire', 'glock18-1.wav', 'SRC wpn_glock18.cpp:37（另有 -2）')],
    'usp':          [('_fire', 'usp1.wav', '名单（消音态）'),
                     ('_fire_unsil', 'usp_unsil-1.wav', 'SRC client.dll file 0x4a64/0x4a99 二选一分支（playserial 反汇编）')],
    'p228':         [('_fire', 'p228-1.wav', '名单')],
    'deagle':       [('_fire', 'deagle-1.wav', '名单（另有 -2）')],
    'fiveseven':    [('_fire', 'fiveseven-1.wav', '名单')],
    'elite':        [('_fire', 'elite_fire.wav', 'SRC wpn_elite.cpp:31')],
    'mp5':          [('_fire', 'mp5-1.wav', '名单（另有 -2）')],
    'tmp':          [('_fire', 'tmp-1.wav', '名单（另有 -2）')],
    'mac10':        [('_fire', 'mac10-1.wav', '名单')],
    'ump45':        [('_fire', 'ump45-1.wav', '名单')],
    'p90':          [('_fire', 'p90-1.wav', '名单')],
    'galil':        [('_fire', 'galil-1.wav', '名单（另有 -2）')],
    'famas':        [('_fire', 'famas-1.wav', 'SRC wpn_famas.cpp:33（另有 -2）')],
    'ak47':         [('_fire', 'ak47-1.wav', '名单（另有 -2）')],
    'm4a1':         [('_fire', 'm4a1-1.wav', 'SRC wpn_m4a1.cpp:34（消音态）'),
                     ('_fire_unsil', 'm4a1_unsil-1.wav', 'SRC wpn_m4a1.cpp:35（-2 的取用点未反汇编，本轮只接第 1 支）')],
    'sg552':        [('_fire', 'sg552-1.wav', '名单（另有 -2）')],
    'aug':          [('_fire', 'aug-1.wav', '名单')],
    'scout':        [('_fire', 'scout_fire-1.wav', '名单（注意不是 scout-1）')],
    'awp':          [('_fire', 'awp1.wav', '名单（注意不是 awp-1）')],
    'g3sg1':        [('_fire', 'g3sg1-1.wav', '名单')],
    'sg550':        [('_fire', 'sg550-1.wav', '名单')],
    'm3':           [('_fire', 'm3-1.wav', 'SRC wpn_m3.cpp:34')],
    'xm1014':       [('_fire', 'xm1014-1.wav', '名单')],
    'm249':         [('_fire', 'm249-1.wav', '名单（另有 -2）')],
    'hegrenade':    [('_fire', 'pinpull.wav', 'SRC wpn_hegrenade.cpp:34（三雷共用拔销音）')],
    'flashbang':    [('_fire', 'pinpull.wav', 'SRC wpn_flashbang.cpp:33（三雷共用拔销音）')],
    'smokegrenade': [('_fire', 'pinpull.wav', 'SRC wpn_smokegrenade.cpp:31（三雷共用拔销音）')],
    'c4':           [('_fire', 'c4_plant.wav', 'SRC wpn_c4.cpp:226/362 EMIT_SOUND')],
}

# 非武器族（目标名是**固定文件名**，不是 `<id>_后缀`）：逐条映射出自 `client/资源欠缺清单.md` row 5-11
#   证件：`row N` = 资源欠缺清单第 N 行；`名单` = 盘上 dll 串表里存在该原版名。
MAP_OTHER = {
    'hitmarker.wav':       ('player/bhit_flesh-1.wav',   '资源欠缺清单 row 5'),
    'hitmarker_head.wav':  ('player/bhit_helmet-1.wav',  '资源欠缺清单 row 5'),
    'step.wav':            ('player/pl_step1.wav',       '资源欠缺清单 row 6（名单）'),
    'step2.wav':           ('player/pl_step2.wav',       '资源欠缺清单 row 6（名单）'),
    'step3.wav':           ('player/pl_step3.wav',       '资源欠缺清单 row 6（名单）'),
    'step4.wav':           ('player/pl_step4.wav',       '资源欠缺清单 row 6（名单）'),
    'jump.wav':            ('player/pl_jump1.wav',       '资源欠缺清单 row 6（注意：该名不在盘上 dll 串表，只有文档记录；抓到的原件是 11025Hz）'),
    'land.wav':            ('player/pl_step4.wav',       '资源欠缺清单 row 7（原版无独立落地音 ⇒ 复用脚步 4）'),
    'hit_flesh.wav':       ('player/bhit_flesh-2.wav',   '资源欠缺清单 row 8'),
    'hit_kevlar.wav':      ('player/bhit_kevlar-1.wav',  '资源欠缺清单 row 8'),
    'death.wav':           ('player/die1.wav',           '资源欠缺清单 row 9（名单）'),
    'death2.wav':          ('player/die2.wav',           '资源欠缺清单 row 9（名单）'),
    'death3.wav':          ('player/die3.wav',           '资源欠缺清单 row 9（名单）'),
    'round_start.wav':     ('radio/letsgo.wav',          '资源欠缺清单 row 10（名单：mp.dll）'),
    'win_t.wav':           ('radio/terwin.wav',          '资源欠缺清单 row 10'),
    'win_ct.wav':          ('radio/ctwin.wav',           '资源欠缺清单 row 10'),
    'bomb_beep.wav':       ('weapons/c4_beep1.wav',      '资源欠缺清单 row 11（名单：mp.dll）'),
    'bomb_plant.wav':      ('weapons/c4_plant.wav',      '资源欠缺清单 row 11'),
    'bomb_defuse.wav':     ('weapons/c4_disarm.wav',     '资源欠缺清单 row 11（名单：mp.dll）'),
    'bomb_explode.wav':    ('weapons/c4_explode1.wav',   '资源欠缺清单 row 11（名单：mp.dll）'),
    'grenade_explode.wav': ('weapons/hegrenade-1.wav',   '资源欠缺清单 row 11'),
    'flash_explode.wav':   ('weapons/flashbang-1.wav',   '资源欠缺清单 row 11'),
    'bomb_plant_radio.wav': ('radio/bombpl.wav',         '资源欠缺清单 row 11'),
    'bomb_defuse_radio.wav': ('radio/bombdef.wav',       '资源欠缺清单 row 11'),
    'zoom.wav':            ('weapons/zoom.wav',          '名单（client.dll/ mp.dll 串表均有）'),
}

GUARD_MIN_RATE = 8000   # 低于它说明源文件本身已被降采样过 ⇒ 拒绝写（防把坏素材又搬一遍）


def read_wav(p):
    with wave.open(p, 'rb') as w:
        return (w.getnchannels(), w.getsampwidth(), w.getframerate(),
                w.readframes(w.getnframes()), w.getnframes())


def write_wav16(p, ch, rate, raw, sw):
    if sw == 1:
        data = b''.join(struct.pack('<h', (b - 128) * 256) for b in raw)
    else:
        data = raw
    with wave.open(p, 'wb') as w:
        w.setnchannels(ch)
        w.setsampwidth(2)
        w.setframerate(rate)
        w.writeframes(data)
    return len(data) + 44, len(data) // (2 * ch)


def main():
    # 控制台可能是 GBK：把不可编码字符按替换符写出，别让一次 print 崩掉整批导入
    try:
        sys.stdout.reconfigure(errors='replace')
    except Exception:
        pass
    ap = argparse.ArgumentParser()
    ap.add_argument('--src', required=True)
    ap.add_argument('--out', required=True)
    ap.add_argument('--only')
    ap.add_argument('--dry-run', action='store_true')
    ap.add_argument('--tsv', help='把逐件结果写成 tsv（目标名/原版名/字节/帧/采样率/时长/凭据）')
    ap.add_argument('--src-root', help='原版 sound 根（非武器族用；缺省 = --src 的上级目录）')
    ap.add_argument('--skip-other', action='store_true', help='只跑武器族，不跑 MAP_OTHER')
    a = ap.parse_args()
    if a.tsv and not os.path.exists(a.tsv):
        with open(a.tsv, 'w', encoding='utf-8') as f:
            f.write('target\torig\tbytes\tframes\trate\tdur\tevid\n')

    root = a.src_root or os.path.dirname(os.path.normpath(a.src))
    jobs = []
    for wid, entries in sorted(MAP.items()):
        if a.only and wid != a.only:
            continue
        for suffix, orig, evid in entries:
            jobs.append((wid + suffix + '.wav', orig,
                         os.path.join(a.src, orig) if orig else None, evid))
    if not a.only and not a.skip_other:
        for tgt, (orig, evid) in sorted(MAP_OTHER.items()):
            jobs.append((tgt, orig, os.path.join(root, orig.replace('/', os.sep)), evid))

    done = skipped = 0
    for tgt, orig, src, evid in jobs:
        if not orig:
            print('  跳过 %s：映射未核对（不猜原版文件名）' % tgt)
            skipped += 1
            continue
        if not os.path.exists(src):
            print('  缺原件 %s（%s｜%s）' % (src, tgt, evid))
            skipped += 1
            continue
        ch, sw, rate, raw, n = read_wav(src)
        if rate < GUARD_MIN_RATE:
            print('  拒绝 %s：源采样率 %d Hz < %d（源本身已降采样）' % (orig, rate, GUARD_MIN_RATE))
            skipped += 1
            continue
        out = os.path.join(a.out, tgt)
        if a.dry_run:
            print('  [dry] %s → %s（ch=%d %dbit %dHz %.3fs｜%s）'
                  % (orig, os.path.basename(out), ch, sw * 8, rate, n / float(rate), evid))
            done += 1
            continue
        size, frames = write_wav16(out, ch, rate, raw, sw)
        if a.tsv:
            with open(a.tsv, 'a', encoding='utf-8') as f:
                f.write('%s\t%s\t%d\t%d\t%d\t%.4f\t%s\n'
                        % (os.path.basename(out), orig, size, frames, rate, frames / float(rate), evid))
        print('  写入 %s（%d B，%d 帧，%d Hz，%.3fs｜%s）'
              % (os.path.basename(out), size, frames, rate, frames / float(rate), evid))
        done += 1
    print('完成：%d 件，跳过 %d 件' % (done, skipped))
    return 0


if __name__ == '__main__':
    sys.exit(main())
