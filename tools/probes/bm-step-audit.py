# -*- coding: utf-8 -*-
"""判据资产（**新增**）：位图可走格之间的**地面高差**审计 —— "斜坡/台阶/楼梯/扶手边走过去会不会卡住"。

## 为什么需要它（缺口是有登记的，⛔ 不是我临时起意）
`策划/验收表.md` 的 §G 行（`全图楼梯 / 坡道 / 台阶（同类漏检：#1 只报了 B 旋转楼梯）`）原文：
> `geom-check.py` 的 A5 只覆盖"低矮障碍"，⇒ **全图所有楼梯/坡道/台阶没有任何 A→B 可走性判据**。

A5 判的是「**位图判挡** + 顶面高差 ∈ (一步台阶, 跳跃可达]」= "矮墙/扶手跳不跳得过去"。
它**看不见**另一半：两格**位图都说可走**，但走过去时地面**台阶式抬升**。

## 运行时口径（判据从代码读，⛔ 不写死）
`client/Assets/Scripts/Module/Map/CsMap.cs`：
* `CanStand(pos)` = `BitmapClear(pos, radius) && GroundWithinStep(pos)` **或** `BodyHeightClear(pos, radius)`（:290-300）
* `GroundWithinStep(pos)`: `SampleGround(pos).y - pos.y <= StepUpHeight` —— **单边**：只挡"往上抬"，**往下掉不挡**（:324-328）
* `TryStepUp(from, target)`: ① `WalkableAt(target)`；② `SampleGround(target).y - from.y > StepUpHeight` ⇒ **false**（:514-531）

⇒ 从低处往高处走，`h(高) − h(低) > StepUpHeight` 时，**两格位图都"可走"也过不去**
（第一层被 `GroundWithinStep` 否掉；第二层 `BodyHeightClear` 的身高带里横着那道抬升，也否掉；
`TryStepUp` 的高度闸门再否一次）。**这就是用户报的"斜坡会概率卡住"的确定性那一半**；
"概率"那一半来自半径 8 向采样 / 几何兜底谁先命中，由实机判据管。

## ⛔ 踩过的口径坑（写在这里，别重复踩）
第一版直接拿 `geom-check.up_face_by_cell()`（= 格内**最高**朝上面）当"地面高度"，
结果全图报出 176 对 Δ≈**8.94 m** 的"隐形墙" —— 全是**屋顶**：cell(66,95) 的格内最高朝上面是
房顶 5.69 m，而人在街上（-3.25 m）⇒ 拿"最高面"当"脚下面"必然自造假差异。
⇒ 本脚本因此**不取最高面**，而是给每格采集**全部**候选朝上面（去重到 0.05 m），
再按"两个候选集之间有没有一对高度差 ≤ 一步台阶"判可走。
（`geom-check` 的 A5 用它是有意的 —— 它要的是"格内真顶面"以判箱子/台沿，口径不同，不是它的错。）

## 判据（三条）
| 类 | 条件 | 含义 |
|---|---|---|
| **A 单向硬边界** | 存在某候选地面 `h1 ∈ S(此格)`，使 `S(彼格)` 里**没有任何** `h2 ≤ h1 + StepUpHeight` | 站在 `h1` 上时**无论如何走不过去**；若 `S(此格)` **全部**如此 ⇒ 该方向完全不通 |
| **B 完全不通（FAIL）** | 双向都是 A ⇒ 两格之间**没有任何**可走的层对 | 位图说"相邻可走"却上不去也下不来 ⇒ **位图/高度数据自相矛盾**，必须查 |
| **C 数据缺失（FAIL）** | 位图可走格没采到任何朝上面 | 该格没有地面 ⇒ 不许静默跳过 |

⛔ 可证伪在哪：B / C 一旦非 0 ⇒ 退出码 1。B=0 只能证明"不存在位图自相矛盾的硬边界"，
**不能**证明"斜坡一定不卡"。A 的条数是**清单**（台阶本该存在），不参与 PASS/FAIL。

用法（只读，不改盘）：
    python tools/probes/bm-step-audit.py              # 全图报告
    python tools/probes/bm-step-audit.py --quiet      # 只出 PASS/FAIL 汇总行
"""
import os
import sys
import importlib.util

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))

# ---- 复用 geom-check.py 的读取层（同一个口径只有一份，⛔ 不另写一套解析）----
_spec = importlib.util.spec_from_file_location('geomcheck', os.path.join(HERE, 'geom-check.py'))
GC = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(GC)

# 匪家（T 出生区）：`de_dust2.bytes` 出生点段 40 个里，20 个落在 x∈[-16.5,-1.5] z∈[-51.5,-46.5]
#（另一簇 20 个是 CT：实测本地玩家选 CT 后 `eye=(18.5,-1.63,34.5)` 正落在那簇）
# ⇒ 取 T 簇外扩 25 m 的矩形当"家里"判据框（覆盖家门外的坡/扶手）。
REGION_T_HOME = (-58.0, -2.0, -58.0, -34.0)
HGT_Q = 0.05          # 候选高度去重量化（米）

def fmt(v):
    return 'None' if v is None else '%.3f' % v


def heights_by_cell(geo):
    """每格的**全部**候选朝上面高度（去重到 HGT_Q）—— ⛔ 不是最高面（见文件头口径坑）。"""
    out = {}
    for g in geo['groups']:
        V, I = g['verts'], g['idx']
        for k in range(0, len(I), 3):
            a, b, c = V[I[k]], V[I[k + 1]], V[I[k + 2]]
            if GC.tri_normal(a, b, c)[1] < GC.FLOOR_MIN_NY:
                continue
            xs = (a[0], b[0], c[0]); zs = (a[2], b[2], c[2]); ys = (a[1], b[1], c[1])
            x0, x1 = min(xs), max(xs); z0, z1 = min(zs), max(zs)
            yy = max(ys)
            nx_s = max(1, int((x1 - x0) / GC.SAMPLE) + 1)
            nz_s = max(1, int((z1 - z0) / GC.SAMPLE) + 1)
            for i in range(nx_s):
                for j in range(nz_s):
                    px = x0 + (x1 - x0) * ((i + 0.5) / nx_s)
                    pz = z0 + (z1 - z0) * ((j + 0.5) / nz_s)
                    key = GC.bm_cell(geo, px, pz)
                    st = out.get(key)
                    if st is None:
                        st = out[key] = set()
                    st.add(round(yy / HGT_Q))
    return out


def main():
    quiet = '--quiet' in sys.argv

    geo = GC.load_geo()
    bm = GC.load_bits()
    const = GC.const_or_die()
    raw = heights_by_cell(geo)
    H = {k: sorted(v * HGT_Q for v in vs) for k, vs in raw.items()}
    del raw

    step = const['StepUpHeight']
    apex = const['JumpSpeed'] ** 2 / (2.0 * const['Gravity'])
    cell = bm['cell']
    ox, oz = bm['ox'], bm['oz']

    walk = [(ix, iz) for iz in range(bm['d']) for ix in range(bm['w'])
            if GC.bm_walk(bm, ix, iz)]
    have = [c for c in walk if c in H]
    missing = [c for c in walk if c not in H]

    def world(ix, iz):
        return ox + (ix + 0.5) * cell, oz + (iz + 0.5) * cell

    def blocked_up(s_from, s_to):
        """站在 from 的某个层上，能否走到 to（往上 ≤ 一步台阶）。返回 (全被挡?, 最高能上到哪层)"""
        # 对 from 的每一层 h1：to 里要有 h2 ≤ h1 + step
        reach = []
        for h1 in s_from:
            ok = [h2 for h2 in s_to if h2 - h1 <= step]
            reach.append(ok[-1] if ok else None)
        return all(r is None for r in reach), reach

    pairs = []
    seen = set()
    for (ix, iz) in have:
        for dx, dz in ((1, 0), (0, 1)):
            nb = (ix + dx, iz + dz)
            if nb not in H or not GC.bm_walk(bm, nb[0], nb[1]):
                continue
            if (nb, (ix, iz)) in seen:
                continue
            seen.add((ix, iz))
            a, b = (ix, iz), nb
            a_all, a_reach = blocked_up(H[a], H[b])
            b_all, b_reach = blocked_up(H[b], H[a])
            if not a_all and not b_all:
                continue                      # 双向都能走 ⇒ 无差异
            lo, hi = (a, b) if min(H[a]) <= min(H[b]) else (b, a)
            pairs.append(dict(a=a, b=b, a_all=a_all, b_all=b_all,
                              dmin=abs(min(H[a]) - min(H[b])),
                              xa=world(*a)[0], za=world(*a)[1],
                              xb=world(*b)[0], zb=world(*b)[1],
                              Ha=H[a], Hb=H[b]))

    cls_a = [p for p in pairs if not (p['a_all'] and p['b_all'])]
    cls_b = [p for p in pairs if p['a_all'] and p['b_all']]
    cls_a.sort(key=lambda p: -p['dmin'])
    cls_b.sort(key=lambda p: -p['dmin'])

    say = (lambda *a: None) if quiet else print
    if not quiet:
        print('# bm-step-audit —— 位图可走格之间的地面高差（斜坡/台阶/楼梯/扶手的 A→B 可走性）')
        print('# 常量（读自 Core/CsConst.cs）：StepUpHeight=%.4f JumpSpeed=%.4f Gravity=%.4f '
              '⇒ 跳跃可达高度=%.4f m' % (step, const['JumpSpeed'], const['Gravity'], apex))
        print('# 位图：w=%d d=%d cell=%.2f origin=(%.1f,%.1f)  读取=%s'
              % (bm['w'], bm['d'], cell, ox, oz,
                 os.path.relpath(bm['file'], ROOT).replace('\\', '/')))
        print('# 可走格=%d；采到地面候选=%d，缺=%d；相邻可走格对（去重）=%d'
              % (len(walk), len(have), len(missing), len(seen)))
        print('# 候选地面层数：单层格=%d 多层格=%d'
              % (len([c for c in have if len(H[c]) == 1]), len([c for c in have if len(H[c]) > 1])))
        print('# A 单向硬边界=%d 对（台阶/台沿，合法几何，清单不判红）' % len(cls_a))
        print('# B 完全不通=%d 对（这份必须为空）' % len(cls_b))

        if missing:
            print('\n## C 位图可走但一格地面都没采到 —— 全部（这份必须为空）')
            for (ix, iz) in missing:
                x, z = world(ix, iz)
                print('   cell(%3d,%3d) xz=(%8.2f,%8.2f)' % (ix, iz, x, z))

        if cls_b:
            print('\n## B 完全不通 —— 全部（这份必须为空）')
            for p in cls_b:
                print('   cell%s H=%s  <->  cell%s H=%s  xz=(%.2f,%.2f)/(%.2f,%.2f)'
                      % (p['a'], ['%.2f' % v for v in p['Ha']], p['b'],
                         ['%.2f' % v for v in p['Hb']], p['xa'], p['za'], p['xb'], p['zb']))

        print('\n## A 单向硬边界 —— 最小层差最大的 30 对')
        for p in cls_a[:30]:
            dirn = 'A->B不通' if p['a_all'] else ('B->A不通' if p['b_all'] else '双向各有一层通')
            print('   %s  cell%s H=%s  <->  cell%s H=%s  xz=(%.2f,%.2f)/(%.2f,%.2f)'
                  % (dirn, p['a'], ['%.2f' % v for v in p['Ha']], p['b'],
                     ['%.2f' % v for v in p['Hb']], p['xa'], p['za'], p['xb'], p['zb']))

        x0, x1, z0, z1 = REGION_T_HOME
        inreg = [p for p in pairs
                 if x0 <= p['xa'] <= x1 and z0 <= p['za'] <= z1
                 and x0 <= p['xb'] <= x1 and z0 <= p['zb'] <= z1]
        inreg.sort(key=lambda p: -p['dmin'])
        print('\n## 匪家（T 出生区外扩框 x∈[%.0f,%.0f] z∈[%.0f,%.0f]）内 —— %d 对'
              % (x0, x1, z0, z1, len(inreg)))
        for p in inreg[:40]:
            dirn = 'A->B不通' if p['a_all'] else ('B->A不通' if p['b_all'] else '双向各有一层通')
            print('   %s  cell%s H=%s  <->  cell%s H=%s  xz=(%.2f,%.2f)/(%.2f,%.2f)'
                  % (dirn, p['a'], ['%.2f' % v for v in p['Ha']], p['b'],
                     ['%.2f' % v for v in p['Hb']], p['xa'], p['za'], p['xb'], p['zb']))
        if not inreg:
            print('   （无 —— 该框内没有"相邻可走格之间无任何可走层对"的地方）')

    ok = (not cls_b) and (not missing)
    print('RESULT-STEP: %s  （A单向硬边界=%d B完全不通=%d C缺地面=%d / 台阶=%.2f 跳跃可达=%.4f）'
          % ('PASS' if ok else 'FAIL', len(cls_a), len(cls_b), len(missing), step, apex))
    return 0 if ok else 1


if __name__ == '__main__':
    sys.exit(main())
