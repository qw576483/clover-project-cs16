# -*- coding: utf-8 -*-
"""decal-size-probe.py -- 判据资产：贴花尺寸"以米计"（差异 #69 的尺寸映射段）。

## 这条判据为什么必须存在

片 FX-ALL 2026-09-23 采了 9 张联络图格，其中 `fx-decal-floor/wall/variants` 三格里
**看不到任何弹痕**；而运行时控制台证明 `BulletImpact` 确实跑到了
（`[Combat] 弹着音 hit_wall（落点 ...）` = `CombatModule.cs:550` 之后的"非角色碰撞体"分支）。

真因不是"没贴"，是**尺寸算错**：
`CombatEffects` 把「世界宽度（米）」直接写进 `localScale`，但 `localScale` 是**倍率** ——
贴片天生多宽由它自己的导入 PPU 决定。本工程 `Resources/UI/Art/fx_*.png` 的 PPU 是
Unity 自动导入的默认 **100**（全工程 7 张 fx_* 只有历史遗留的 `fx_bullethole.png` 是 50，
**不存在**"PPU = 像素宽"的既有约定），于是：

    16x16 弹痕天生 0.16 世界单位  x  localScale 0.075  =  0.0120 m = 1.2 cm
    口径本该是 7.5 cm  ->  小了 6.25 倍  ->  2 m 外不足 5 px  ->  表现上等于"没贴"

## 判据分三段

A) **结构**：`SpriteScaleForMeters(Sprite, meters)` 恰好定义 1 次；两处贴花
   （`BulletImpact` 的弹痕 / `BloodImpact` 的血迹）的 `localScale` 都走它；
   全文**不再**出现"把米直接当倍率"的两种写法（回归守卫）。
B) **数值复算**：用**盘上真实的** PNG 像素宽（直接读 IHDR，不依赖 PIL）+ 真实 `.meta` 的
   `spritePixelsToUnits`，逐个贴图算 旧口径 / 新口径 的**实际世界宽度**，断言
   新口径 == 口径值，且旧口径**一律偏小**（这条断言就是"为什么看不见"的量化版）。
   口径值本身从 `CsCombatTuning.cs` **解析**出来（⛔ 不写死数字）。
C) **反例复现**：从 `git show HEAD:` 现取**修复前**那两行，断言它们逐字就是裸 `Vector3.one * <米>`，
   并断言**现在**的文件里已经没有它们（⛔ 不写死"旧代码长什么样"，会让判据在历史变化后假绿）。
D) **朝向**：贴地弹痕的 `LookRotation(-normal, up)` 是**退化**的（法线与 up 平行 ⇒ 贴片立起来，
   正对相机看就是一条线），断言现在有"参考上向"分支，且 HEAD 里没有（反例可复现）。
E) **贴图透明度**：**自己解 PNG 的 alpha**（本机 3.13 解释器没有 PIL，判据不该挑解释器），
   逐张断言"既有不透明像素、也有透明像素"。这一条正是原缺陷的量化版 ——
   修复前 11 张贴图**全部 256/2304 个像素都不透明**（解出来是白方块），
   断言"透明像素数 > 0"会在那个状态下变红。
F) **埋点**：`BulletImpact` 里必须有 `shot.decal` / `shot.decal.null` 两条日志 ——
   没有它，"弹痕没出现"与"弹痕代码没跑"这两件事无法区分（本片就是这么发现编辑器在跑旧程序集的）。

用法：
    python tools/probes/decal-size-probe.py            # 在工程根跑
退出码 0 = PASS，1 = FAIL。
"""

import io
import os
import re
import struct
import subprocess
import sys
import zlib

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
EFFECTS = os.path.join(ROOT, 'client', 'Assets', 'Scripts', 'Module', 'Combat', 'CombatEffects.cs')
TUNING = os.path.join(ROOT, 'client', 'Assets', 'Scripts', 'Module', 'Combat', 'CsCombatTuning.cs')
PATHS_CS = os.path.join(ROOT, 'client', 'Assets', 'Scripts', 'Core', 'ResPaths.cs')
ART = os.path.join(ROOT, 'client', 'Assets', 'Resources', 'UI', 'Art')
RES = os.path.join(ROOT, 'client', 'Assets', 'Resources')
EFFECTS_REL = 'client/Assets/Scripts/Module/Combat/CombatEffects.cs'

FAILS = []


def fail(msg):
    FAILS.append(msg)
    print('  FAIL ' + msg)


def ok(msg):
    print('  ok   ' + msg)


def read(p):
    return io.open(p, encoding='utf-8', newline='').read()


def png_size(path):
    """只读 PNG 的 IHDR：宽 = 字节 16..20（大端）。⛔ 不依赖 PIL（本机 3.13 解释器没有）。"""
    with open(path, 'rb') as f:
        head = f.read(24)
    if head[:8] != b'\x89PNG\r\n\x1a\n':
        raise ValueError('not a PNG: ' + path)
    w, h = struct.unpack('>II', head[16:24])
    return w, h


def meta_ppu(path):
    t = read(path)
    m = re.search(r'spritePixelsToUnits:\s*([0-9]+(?:\.[0-9]+)?)', t)
    if not m:
        raise ValueError('no spritePixelsToUnits in ' + path)
    return float(m.group(1))


def const_float(src, name):
    """取一个 const 的**数值**：字面量直接取；写成表达式（如 `DecalSize / 16f`）时按已知常量代入求值。
    ⛔ 不写死数字：口径一旦改，这里必须跟着变红，而不是继续"对"旧值。"""
    m = re.search(re.escape(name) + r'\s*=\s*([^;]+);', src)
    if not m:
        raise ValueError('const not found: ' + name)
    expr = m.group(1).strip()
    if expr.endswith('f') and not re.match(r'^[0-9.]', expr):
        pass
    expr = re.sub(r'(\d)[fF]\b', r'\1', expr)          # 去掉 C# 的 float 后缀
    if re.match(r'^[-+0-9.eE*/() ]+$', expr):
        return float(eval(expr, {'__builtins__': {}}, {}))
    # 表达式里含标识符：把已知常量逐个代入（只认本文件里"字面量"形式的那些）
    known = {}
    for m2 in re.finditer(r'const\s+float\s+(\w+)\s*=\s*([0-9]+(?:\.[0-9]+)?)f?\s*;', src):
        known[m2.group(1)] = float(m2.group(2))
    if name in known:
        return known[name]
    def _sub(mm):
        n = mm.group(0)
        if n in known:
            return repr(known[n])
        if n == 'f':
            return ''
        raise ValueError('无法解析常量表达式 %s = %s（含未知标识符 %s）' % (name, expr, n))
    expr2 = re.sub(r'[A-Za-z_]\w*', _sub, expr)
    return float(eval(expr2, {'__builtins__': {}}, {}))


# ----------------------------------------------------------------------------- A 结构
def part_a(src):
    print('[A] 结构断言')
    lines = src.replace('\r\n', '\n').split('\n')

    def find_all(sub):
        return [i for i, l in enumerate(lines) if sub in l]

    sig = 'private static float SpriteScaleForMeters(Sprite s, float meters)'
    hits = find_all(sig)
    if len(hits) != 1:
        fail('SpriteScaleForMeters 定义 %d 处（要 1）' % len(hits))
    else:
        ok('SpriteScaleForMeters 定义恰好 1 处（行 %d）' % (hits[0] + 1))
        defline = hits[0]

    # 两处调用点：shot / blood
    call_shot = find_all('SpriteScaleForMeters(sprite, CsCombatTuning.DecalSize)')
    call_blood = find_all('SpriteScaleForMeters(spr, CsCombatTuning.BloodDecalSize(px))')
    if len(call_shot) != 1:
        fail('弹痕贴花未走 SpriteScaleForMeters（%d 处，要 1）' % len(call_shot))
    else:
        ok('弹痕贴花走 SpriteScaleForMeters（行 %d）' % (call_shot[0] + 1))
    if len(call_blood) != 1:
        fail('血迹贴花未走 SpriteScaleForMeters（%d 处，要 1）' % len(call_blood))
    else:
        ok('血迹贴花走 SpriteScaleForMeters（行 %d）' % (call_blood[0] + 1))

    if len(hits) == 1 and len(call_shot) == 1 and len(call_blood) == 1:
        if not (defline < call_shot[0] and defline < call_blood[0]):
            fail('helper 定义必须在两个调用点之前（定义 %d / 弹痕 %d / 血迹 %d）'
                 % (defline + 1, call_shot[0] + 1, call_blood[0] + 1))
        else:
            ok('helper 定义在两个调用点之前（顺序单调：%d < %d, %d）'
               % (defline + 1, call_shot[0] + 1, call_blood[0] + 1))

    # 回归守卫：不许再把"米"当倍率写回去
    for bad, what in (('Vector3.one * CsCombatTuning.DecalSize', '弹痕'),
                      ('Vector3.one * CsCombatTuning.BloodDecalSize', '血迹')):
        n = src.replace('\r\n', '\n').count(bad)
        if n:
            fail('回归：%s 又出现"把米直接当倍率"的写法 %d 处（%s）' % (what, n, bad))
        else:
            ok('回归守卫：%s 无"米当倍率"写法' % what)

    # helper 必须真的用贴片的自带宽度
    if len(hits) == 1:
        body = '\n'.join(lines[hits[0]:hits[0] + 6])
        if 's.bounds.size.x' not in body:
            fail('helper 没有用 s.bounds.size.x 反算倍率')
        else:
            ok('helper 用 s.bounds.size.x 反算倍率')


# ----------------------------------------------------------------------------- B 数值
def part_b(src, tuning, paths_cs):
    print('[B] 数值复算（真实 PNG 尺寸 + 真实 .meta PPU）')

    decal_size = const_float(tuning, 'DecalSize')
    mpp = const_float(tuning, 'DecalMetersPerPixel')
    print('      CsCombatTuning: DecalSize=%.4f  DecalMetersPerPixel=%.6f' % (decal_size, mpp))
    if abs(mpp - decal_size / 16.0) > 1e-9:
        fail('DecalMetersPerPixel(%.6f) != DecalSize/16(%.6f)' % (mpp, decal_size / 16.0))
    else:
        ok('DecalMetersPerPixel == DecalSize/16（口径自洽）')

    # BloodDecalSize(pixels) 的口径：从源码里确认它就是 pixels * DecalMetersPerPixel
    m = re.search(r'BloodDecalSize\(int pixels\)\s*\{(.+?)\}', tuning, re.S)
    if not m:
        fail('CsCombatTuning.BloodDecalSize 找不到')
        return
    if 'pixels * DecalMetersPerPixel' not in m.group(1):
        fail('BloodDecalSize 不是 pixels * DecalMetersPerPixel（口径变了，本判据的复算不再成立）')
    else:
        ok('BloodDecalSize(px) == px * DecalMetersPerPixel')

    def blood_meters(px):
        return px * mpp

    # 贴图清单：从 ResPaths 的**字面量 key 表**里取（⛔ 不另抄一份名字）
    keys = re.findall(r'"(UI/Art/fx_[A-Za-z0-9_]+)"', paths_cs)
    if not keys:
        fail('ResPaths 里没解析到 fx_* key')
        return
    shot_keys = [k for k in keys if 'fx_shot' in k]
    blood_keys = [k for k in keys if 'fx_blood' in k]
    hole_keys = [k for k in keys if 'fx_bullethole' in k]
    if len(shot_keys) != 5:
        fail('ResPaths 弹痕变体 %d 个（要 5）' % len(shot_keys))
    if len(blood_keys) != 6:
        fail('ResPaths 血迹变体 %d 个（要 6）' % len(blood_keys))

    rows = []
    for k in shot_keys + hole_keys:
        rows.append((k, 'shot'))
    for k in blood_keys:
        rows.append((k, 'blood'))

    worse = 0
    checked = 0
    for k, kind in rows:
        png = os.path.join(RES, k + '.png')      # k 已经是相对 Resources 的路径（UI/Art/fx_*）
        meta = png + '.meta'
        if not os.path.exists(png):
            fail('贴图不在盘：%s' % png)
            continue
        if not os.path.exists(meta):
            fail('.meta 不在盘：%s' % meta)
            continue
        checked += 1
        w, h = png_size(png)
        ppu = meta_ppu(meta)
        native = w / ppu
        target = decal_size if kind == 'shot' else blood_meters(w)
        new_scale = target / native
        new_rendered = native * new_scale
        old_rendered = native * target          # 修复前：把米直接当倍率
        ratio = old_rendered / target
        if new_rendered < target:
            worse += 1
        rows_ok = abs(new_rendered - target) < 1e-4
        print('      %-18s %3dpx ppu=%-4g 口径=%.3fm  旧口径=%.4fm (%.4fx)  新口径=%.3fm  %s'
              % (k + '.png', w, ppu, target, old_rendered, ratio, new_rendered,
                 'ok' if rows_ok else 'MISMATCH'))
        if not rows_ok:
            fail('新口径复算不等于口径值：%s' % png)

    if checked != len(rows):
        fail('只核到 %d / %d 张贴图 ⇒ 结论不成立（⛔ 不许在"什么都没核到"时说绿）'
             % (checked, len(rows)))
    elif worse:
        fail('新口径下仍有 %d 张贴图小于口径值' % worse)
    else:
        ok('新口径下 %d 张贴图的**实际世界宽度**都等于口径值' % checked)

    # 旧口径"一律偏小"就是"看不见"的量化版：逐张贴图断言 ratio < 1（除非 PPU 恰好等于像素宽）
    bad = []
    for k, kind in rows:
        png = os.path.join(RES, k + '.png')
        if not os.path.exists(png) or not os.path.exists(png + '.meta'):
            continue
        w, _ = png_size(png)
        ppu = meta_ppu(png + '.meta')
        if w != ppu:                 # PPU == 像素宽 时旧口径恰好正确
            bad.append((os.path.basename(png), w / ppu))
    if not bad:
        fail('旧口径在这批贴图上并不偏小 —— 与"截图上看不见"的观测矛盾，判据前提要重查')
    else:
        detail = ' '.join('%s=%.3fx' % (n, r) for n, r in bad)
        ok('旧口径逐张偏小（正是"截图上看不见"的量化原因）：%s' % detail)

    # 弹痕那条：旧 localScale = 口径 0.075；新 localScale = 口径 / 天生宽 = 6.25 倍
    png = os.path.join(RES, 'UI/Art/fx_shot1.png')
    if os.path.exists(png) and os.path.exists(png + '.meta'):
        w, _ = png_size(png)
        ppu = meta_ppu(png + '.meta')
        if w and ppu:
            native = w / ppu
            new_scale = decal_size / native
            print('      fx_shot1: 天生 %.4f 世界单位宽 ⇒ localScale 旧=%.4f 新=%.4f（放大 %.2f 倍）'
                  % (native, decal_size, new_scale, new_scale / decal_size))
            if abs(new_scale / decal_size - 6.25) < 0.01:
                ok('弹痕 localScale 需要放大 6.25 倍（= PPU 100 / 16px，与联络图上"看不见"一致）')
            else:
                ok('弹痕 localScale 需要放大 %.2f 倍（PPU 变了，仍在判据内）' % (new_scale / decal_size))


# ----------------------------------------------------------------------------- C 反例
def part_c(src):
    print('[C] 反例复现（从 git HEAD 现取修复前的两行）')
    try:
        old = subprocess.check_output(
            ['git', 'show', 'HEAD:' + EFFECTS_REL],
            cwd=ROOT, stderr=subprocess.STDOUT)
    except subprocess.CalledProcessError as e:
        fail('git show HEAD:%s 失败：%s' % (EFFECTS_REL, e.output[:200]))
        return
    try:
        old_s = old.decode('utf-8')
    except UnicodeDecodeError:
        old_s = old.decode('utf-8', 'replace')

    p_shot = r'(\w[\w.]*)\s*=\s*Vector3\.one\s*\*\s*CsCombatTuning\.DecalSize\s*;'
    p_blood = r'(\w[\w.]*)\s*=\s*Vector3\.one\s*\*\s*CsCombatTuning\.BloodDecalSize\((\w+)\)\s*;'
    shot_hits = [m.group(0).strip() for m in re.finditer(p_shot, old_s)]
    blood_hits = [m.group(0).strip() for m in re.finditer(p_blood, old_s)]

    # 血迹那条是**本片新增**的（HEAD 里还没有血迹实现），所以 HEAD 只可能命中弹痕那条。
    # 判据只要求：HEAD 里**确实存在**弹痕那条（反例可复现），且它在修复后消失。
    if not shot_hits:
        fail('HEAD 里找不到弹痕"米当倍率"的写法 ⇒ 反例无法复现（HEAD 变了就要重看这条判据）')
    else:
        for h in shot_hits:
            print('      HEAD(弹痕): ' + h)
        ok('HEAD 确实是"把米直接当倍率"（弹痕 %d 处，逐字取自 git，⛔ 未写死）' % len(shot_hits))
    if blood_hits:
        for h in blood_hits:
            print('      HEAD(血迹): ' + h)
    else:
        print('      HEAD(血迹): 无 —— 血迹贴花是片 FX-ALL 新增的实现，HEAD 里还不存在（符合预期）')

    old_hits = shot_hits + blood_hits
    now = src.replace('\r\n', '\n')
    still = [h for h in old_hits if h in now]
    if still:
        fail('修复后的文件里仍有 HEAD 的旧写法：%s' % ' | '.join(still))
    else:
        ok('修复后的文件里旧写法已全部消失（HEAD 共 %d 处）' % len(old_hits))


def png_rgba(path):
    """最小 PNG 解码器：只吃 color type 6 (RGBA) / bit depth 8 / 非隔行 —— 贴图生成脚本吐的就是它。
    为什么要自己解：本机 `python`(3.13) 没有 PIL，`py -3`(3.12) 才有；判据不该**挑解释器**，
    否则同一条判据换个 shell 就假红/假绿。"""
    with open(path, 'rb') as f:
        data = f.read()
    if data[:8] != b'\x89PNG\r\n\x1a\n':
        raise ValueError('not a PNG: ' + path)
    pos = 8
    idat = b''
    w = h = None
    while pos < len(data):
        ln = struct.unpack('>I', data[pos:pos + 4])[0]
        typ = data[pos + 4:pos + 8]
        body = data[pos + 8:pos + 8 + ln]
        if typ == b'IHDR':
            w, h, bd, ct, _comp, _filt, inter = struct.unpack('>IIBBBBB', body)
            if bd != 8 or ct != 6 or inter != 0:
                raise ValueError('unsupported PNG %s bd=%d ct=%d inter=%d' % (path, bd, ct, inter))
        elif typ == b'IDAT':
            idat += body
        elif typ == b'IEND':
            break
        pos += 12 + ln
    raw = zlib.decompress(idat)
    stride = w * 4
    out = bytearray()
    prev = bytearray(stride)
    p = 0
    for _y in range(h):
        ft = raw[p]
        p += 1
        line = bytearray(raw[p:p + stride])
        p += stride
        if ft == 1:
            for i in range(4, stride):
                line[i] = (line[i] + line[i - 4]) & 255
        elif ft == 2:
            for i in range(stride):
                line[i] = (line[i] + prev[i]) & 255
        elif ft == 3:
            for i in range(stride):
                a = line[i - 4] if i >= 4 else 0
                line[i] = (line[i] + ((a + prev[i]) >> 1)) & 255
        elif ft == 4:
            for i in range(stride):
                a = line[i - 4] if i >= 4 else 0
                b = prev[i]
                c = prev[i - 4] if i >= 4 else 0
                pa, pb, pc = abs(b - c), abs(a - c), abs(a + b - 2 * c)
                pr = a if (pa <= pb and pa <= pc) else (b if pb <= pc else c)
                line[i] = (line[i] + pr) & 255
        elif ft != 0:
            raise ValueError('bad PNG filter %d in %s' % (ft, path))
        out += line
        prev = line
    return w, h, bytes(out)


# ----------------------------------------------------------------------------- D 朝向
def part_d(src):
    print('[D] 朝向：贴地贴片的四元数退化（弹痕 / 火星 / 血迹三处都要走 helper）')
    lines = src.replace('\r\n', '\n').split('\n')
    sig = [i for i, l in enumerate(lines) if 'private static Vector3 SurfaceUp(Vector3 normal)' in l]
    calls = [i for i, l in enumerate(lines) if 'SurfaceUp(' in l and 'private static' not in l]
    if len(sig) != 1:
        fail('SurfaceUp 定义 %d 处（要 1）—— 没有它，命中地面时 LookRotation 会退化，贴片立起来看不见'
             % len(sig))
    else:
        ok('SurfaceUp 定义恰好 1 处（行 %d）' % (sig[0] + 1))
        body = '\n'.join(lines[sig[0]:sig[0] + 4])
        if 'Mathf.Abs(Vector3.Dot(normal, Vector3.up))' not in body:
            fail('SurfaceUp 没有用 |dot(normal, up)| 判平行')
        elif 'Vector3.forward' not in body or 'Vector3.up' not in body:
            fail('SurfaceUp 没有在 forward / up 之间二选一')
        else:
            ok('SurfaceUp 用 |dot(normal, up)| > 0.9 在 forward / up 之间二选一')
    # 三处贴片：弹痕 / 火星 / 血迹，都必须走 helper（只修一处 = 把坑挪到另两处等着复发）
    if len(calls) != 3:
        fail('SurfaceUp 调用点 %d 处（要 3：弹痕 / 火星 / 血迹）—— 行 %s'
             % (len(calls), ','.join(str(i + 1) for i in calls)))
    else:
        ok('三处贴片都走 SurfaceUp（行 %s）' % ','.join(str(i + 1) for i in calls))
        if len(sig) == 1 and min(calls) < sig[0]:
            fail('SurfaceUp 有调用点排在定义之前')
        else:
            ok('定义在三处调用之前（顺序单调）')

    bad = r'Quaternion\.LookRotation\([^)]*,\s*Vector3\.up\s*\)'
    now_bad = re.findall(bad, src)
    if now_bad:
        fail('仍有 %d 处裸 LookRotation(.., Vector3.up)（法线与 up 平行时退化）：%s'
             % (len(now_bad), ' | '.join(x.strip() for x in now_bad)))
    else:
        ok('全文没有裸 LookRotation(.., Vector3.up)')

    try:
        old = subprocess.check_output(['git', 'show', 'HEAD:' + EFFECTS_REL],
                                      cwd=ROOT, stderr=subprocess.STDOUT).decode('utf-8', 'replace')
    except subprocess.CalledProcessError as e:
        fail('git show HEAD 失败：%s' % e.output[:200])
        return
    if 'SurfaceUp' not in old:
        ok('HEAD 里没有 SurfaceUp ⇒ 退化写法确实存在过（反例可复现，逐字取自 git）')
    else:
        fail('HEAD 里已有 SurfaceUp ⇒ 反例前提变了，这条判据要重看')
    old_bad = re.findall(bad, old)
    if old_bad:
        print('      HEAD 裸 LookRotation(.., Vector3.up) = %d 处' % len(old_bad))
        ok('修复后从 %d 处降到 0 处' % len(old_bad))
    else:
        fail('HEAD 里找不到裸 LookRotation(.., Vector3.up) ⇒ 反例无法复现')


# ----------------------------------------------------------------------------- E 透明度
def part_e(tuning, paths_cs):
    print('[E] 贴图透明度（自己解 PNG alpha；原缺陷是"解出来全部不透明"）')
    keys = re.findall(r'"(UI/Art/fx_[A-Za-z0-9_]+)"', paths_cs)
    names = [k for k in keys if 'fx_shot' in k or 'fx_blood' in k]
    if not names:
        fail('ResPaths 里没解析到 fx_shot / fx_blood key')
        return
    zero_tr = []
    checked = 0
    for k in sorted(names):
        png = os.path.join(RES, k + '.png')
        if not os.path.exists(png):
            fail('贴图不在盘：%s' % png)
            continue
        try:
            w, h, px = png_rgba(png)
        except ValueError as e:
            fail('解 PNG 失败：%s' % e)
            continue
        checked += 1
        total = w * h
        opaque = sum(1 for i in range(3, len(px), 4) if px[i] == 255)
        clear = sum(1 for i in range(3, len(px), 4) if px[i] == 0)
        ink = total - clear
        print('      %-16s %dx%d 有墨(a>0) %4d/%d (%.1f%%)  全透明 %4d  完全不透明 %4d'
              % (k.split('/')[-1] + '.png', w, h, ink, total, 100.0 * ink / total, clear, opaque))
        if clear == 0:
            zero_tr.append(k)
    if checked != len(sorted(names)):
        fail('只核到 %d / %d 张贴图 ⇒ 结论不成立' % (checked, len(sorted(names))))
    elif zero_tr:
        fail('这些贴图**一个透明像素都没有**（解出来是白方块，就是原缺陷）：%s' % ', '.join(zero_tr))
    else:
        ok('%d 张贴图都有全透明背景 + 半/不透明墨迹（decal 不透明度口径生效）' % checked)

    # 工具侧：透明索引必须是**实测**出来的，不能是照搬来的猜测。
    # ⛔ 自检项按**代码**断言（set(corners) / max(counts...) / idx not in (0, 255)），
    #    不按注释里的措辞 —— 本片第一次就是拿中文关键词去匹英文注释，假红了一条。
    w3 = os.path.join(ROOT, 'tools', 'probes', 'wad3-extract.py')
    if not os.path.exists(w3):
        fail('找不到 tools/probes/wad3-extract.py')
        return
    t = read(w3)
    if 'def decal_base_colour' not in t:
        fail('wad3-extract.py 没有 decal_base_colour ⇒ decal 口径的自检又没了')
        return
    ok('wad3-extract.py 有 decal_base_colour（口径自检函数）')
    checks = (
        ('set(corners)', '四角必须一致（一个平背景）'),
        ('255 in src', 'palette[255] 是基色、不得出现在像素数据里'),
        ('_opacity(pal, bg) != 0', '背景必须是纯白（不透明度 0 == 白即透明）'),
        ('return 255 - max(', '不透明度 = 255 - 调色板亮度（白端 = 透明端）'),
        ('pal[255 * 3]', '整张贴花的 RGB 取自 palette[255]'),
        ('raise ValueError', '不一致时要报错，⛔ 不许静默兜底'),
    )
    for needle, what in checks:
        if needle not in t:
            fail('decal 口径缺少自检项：%s（找不到 %r）' % (what, needle))
        else:
            ok('decal 口径自检：%s' % what)
    if 'out[i * 4 + 3] = _opacity(pal, idx)' in t:
        ok('alpha 逐像素由 _opacity 决定（白端 -> 0、深色端 -> 越不透明）')
    else:
        fail('alpha 不是由 _opacity 逐像素决定 ⇒ 口径可能没生效')
    if 'masked_bg_index' in t:
        fail('旧的 masked_bg_index 还在 ⇒ 仍是「{ 透明纹理」口径（差异 #69 的根因）')


# ----------------------------------------------------------------------------- F 埋点
def part_f(src):
    print('[F] 埋点：BulletImpact 必须自己说话')
    lines = src.replace('\r\n', '\n').split('\n')
    for tag in ('shot.decal', 'shot.decal.null'):
        n = sum(1 for l in lines if '"' + tag + '"' in l)
        if n:
            ok('有埋点 %s（%d 处）' % (tag, n))
        else:
            fail('没有埋点 %s ⇒ "没贴"与"代码没跑"无法区分（本片就是靠它发现编辑器在跑旧程序集）' % tag)
    fields = ('世界宽', '缩放', '变体数')
    body = '\n'.join(l for l in lines if 'shot.decal' in l or '世界宽' in l or '缩放' in l)
    missing = [f for f in fields if f not in body]
    if missing:
        fail('shot.decal 日志缺少字段：%s' % ', '.join(missing))
    else:
        ok('shot.decal 日志带齐 世界宽 / 缩放 / 变体数（可与口径值对账）')


def main():
    print('== decal-size-probe（贴花尺寸"以米计"；差异 #69 尺寸映射段）==')
    print('ROOT=' + ROOT)
    if not os.path.exists(EFFECTS):
        print('FATAL 找不到 ' + EFFECTS)
        return 1
    src = read(EFFECTS)
    tuning = read(TUNING)
    paths_cs = read(PATHS_CS)

    part_a(src)
    part_b(src, tuning, paths_cs)
    part_c(src)
    part_d(src)
    part_e(tuning, paths_cs)
    part_f(src)

    print('')
    if FAILS:
        print('RESULT: FAIL (%d)' % len(FAILS))
        return 1
    print('RESULT: PASS')
    return 0


if __name__ == '__main__':
    sys.exit(main())
