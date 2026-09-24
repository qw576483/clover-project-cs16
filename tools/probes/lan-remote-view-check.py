#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""差异 #88「能开局」第②段的**独立进程判据**（片LAN-D）。

角色互换：本脚本是**主机**（第二个进程，真 socket），Unity 侧是**客户端**
（`Cs16.Module.Net.CsLanClient` + `Cs16.Module.View.CsLanRemoteView`）。

⛔ 为什么必须换到这个方向（而不是让 python 当客户端）：本片要判的是**客户端侧把快照画成了人**，
所以"发快照的那一端"必须是**我们完全可控**的 ⇒ 坐标是我们脚本化的、期望值是我们自己记录的，
Unity 侧的视图值由探针从**运行时对象**上读出来。这样"手上的值（视图 transform）"与
"收到的值（本脚本发出去的字节）"是**两个进程各自独立**产生的，不靠任何一方的日志叙述。

线格式（⛔ 与 `client/Assets/Scripts/Module/Net/CsLanGateway.cs` 逐字一致，两端必须同值）：
    客户端 -> 主机   CS16-LAN-JOIN/1|<json>
    主机   -> 客户端 CS16-LAN-WELCOME/1|<json>
    主机   -> 客户端 CS16-LAN-SNAP/1|<json>     每 0.1 s 一条
    客户端 -> 主机   CS16-LAN-BYE/1

判据（写死在这里，不靠人眼；每一条都能被打红）：
    J2 客户端在跑、握过手、快照 >= 15 条、非法行 = 0
    J3 **客户端侧生成了 N 个远端视图**，N == 本脚本发的 actor 数
       （两个口径都要对：探针按 `RemoteId>=0` 数出来的 = 系统自己的账 `CsLanRemoteView.ViewCount`）
    J4 首个视图的 pos / yaw 与本脚本**声明要发**的值逐字段一致（容差 0.002 m / 0.06°）
    J5 每个视图的 pos 都与声明值一致
    J6 已死的那位（alive=false）的存活标记跟着快照走（视图仍被保留，⛔ 不是被丢掉）
    J7 所有远端视图的 Collider 数 = 0（不参与本机碰撞/命中链）
    J8 池化（⛔ 不每帧新建）：累计新建的视图数 × 3 <= 快照条数
       —— 每帧新建的话它 ≈ 快照条数；受池复用后它 == actor 数（实测 3）
       ⚠️ 引擎池口径：回收 = 置非活跃 + 挂回池根（**不销毁**，见 ObjectPool.cs:164）
       ⇒ 观测里的「已回池实例」数**不必为 0**，它是"实例确实被复用"的证据

负控（`--corrupt`）：
    pos  = 第 2 段**实际发**的 x/z 与"声明要发"的值不同 ⇒ J4/J5 必须转红（判据真的在看字段）
    stop = 第 2 段**一条都不发**并断开 ⇒ J4/J5 必须转红（视图只跟快照走，不会自己动）

用法：
  python tools/probes/lan-remote-view-check.py --port 8012 \
      --base-file <...>/.ai-tmp/test/lan-remote-place.txt \
      --sent <...>/.ai-tmp/test/lan-remote-view-host.tsv \
      --obs  <...>/.ai-tmp/test/lan-remote-view-unity.tsv \
      --corrupt none
"""
import argparse
import json
import os
import select
import socket
import sys
import time

sys.stdout.reconfigure(encoding='utf-8')

JOIN = 'CS16-LAN-JOIN/1'
WELCOME = 'CS16-LAN-WELCOME/1'
SNAP = 'CS16-LAN-SNAP/1'
BYE = 'CS16-LAN-BYE/1'

# 三个远端角色：id / 名 / 阵营 / (沿**开阔方向**多少米, 垂直于它的**右手边**多少米) / 血 / 是否存活 / 武器
# 为什么用「沿开阔方向 / 右手边」而不是世界坐标或"玩家朝向"：远端角色要摆在一片**真的站得住、看得见**的地上。
#   ① 实测教训：CT 出生点朝向前方 ~2 m 就是墙 ⇒ 按朝向摆会把三个人放进墙里（第一版主图拍出来全是墙）；
#   ② 探针 `whereami` 从玩家身边沿 8 个方向各打 12 m（只打 CsWorld 层）算出**最开阔的方向** (ax,az)
#      ⇒ 本脚本按它摆。绝对坐标 = 底座 + a·沿 + r·右手边（右手边 = a 绕 Y 转 +90° = (az,0,-ax)）。
# 死亡那一位（id=43）是**刻意**的：判据要能看见"系统仍为死者留了一具视图"，而不只是"活人画出来了"。
ACTORS = [
    (41, 'LanProbeA', 'CT', (3.0, 1.2), 100, True, 'ak47'),
    (42, 'LanProbeB', 'T', (5.0, -1.2), 100, True, 'deagle'),
    (43, 'LanProbeC', 'T', (7.0, 0.4), 0, False, 'knife'),
]

# 第 2 段（t >= --phase2）把 id=41 挪到「沿开阔方向 5.0 m、右手边 -3.0 m」⇒ 相对 P1 位移 ≈4.9 m，
# 判据就能判"跟着快照走"，而不只是"第一帧摆对了"；负控 stop 时它会停在 P1 ⇒ 必然转红。
PHASE2_41_FS = (5.0, -3.0)
PHASE2_41_YAW = 45.0
YAW1 = {41: 90.0, 42: 271.5, 43: 0.0}

# 负控 pos：实际发出去但**不声明**的偏移（判据必须因此转红）
CORRUPT_POS_DELTA = (7.0, 0.0, 3.0)

PORT = 8012


def load_base(path, inline):
    """读 Unity 侧 `whereami` 落盘的底座：玩家位置 (x,y,z) + 朝向 (fx,fz) + **最开阔的水平方向** (ax,az)。

    `--base` 内联形态 = `x,y,z,fx,fz,ax,az`（7 个值）。
    """
    if inline:
        p = [float(x) for x in inline.split(',')]
        return p[0], p[1], p[2], p[3], p[4], p[5], p[6]
    t0 = time.time()
    while time.time() - t0 < 30:
        if os.path.exists(path) and os.path.getsize(path) > 0:
            vals = {}
            with open(path, 'r', encoding='utf-8') as f:
                for line in f:
                    if '=' in line:
                        k, _, v = line.partition('=')
                        try:
                            vals[k.strip()] = float(v.strip())
                        except ValueError:
                            pass
            if 'x' in vals and 'y' in vals and 'z' in vals and 'ax' in vals and 'az' in vals:
                def n2(a, b, da, db):
                    n = (a * a + b * b) ** 0.5
                    return (da, db) if n < 1e-6 else (a / n, b / n)
                fx, fz = n2(vals.get('fx', 0.0), vals.get('fz', 1.0), 0.0, 1.0)
                ax, az = n2(vals['ax'], vals['az'], 0.0, 1.0)
                return vals['x'], vals['y'], vals['z'], fx, fz, ax, az
        time.sleep(0.25)
    raise SystemExit('ERROR: 底座文件 %s 超过 30s 还没有有效内容（Unity 侧 whereami 没跑？'
                     '注意它必须含 ax/az = 探针现算的开阔方向）' % path)


def place(base, along, side):
    """底座 + 沿开阔方向*米 + 右手边*米 → 绝对坐标。

    Unity 里 yaw 绕 Y；把开阔方向 a 绕 Y 转 +90° 得"右手边" = (az,0,-ax)。
    ⛔ 不引入任何随机 / 假设：只用探针从运行时读出来的两个分量。
    """
    bx, by, bz, _fx, _fz, ax, az = base
    return (bx + ax * along + az * side, by, bz + az * along - ax * side)


def build_expected(base):
    """**声明要发**的值（判据的期望值）——每帧快照里每个 actor 的 pos/yaw/hp/alive。"""
    out = {}
    for (aid, name, team, fs, hp, alive, w) in ACTORS:
        x, y, z = place(base, fs[0], fs[1])
        out[aid] = dict(name=name, team=team, x=x, y=y, z=z, yaw=YAW1[aid], hp=hp, alive=alive, w=w)
    return out


def phase2_pos(base):
    """第 2 段 id=41 的**声明**位置（绝对坐标）。"""
    return place(base, PHASE2_41_FS[0], PHASE2_41_FS[1])


def frame_json(exp, phase2, corrupt, p2):
    """组装一条快照 JSON。phase2=True 时 id=41 已在第 2 段的位置（p2，绝对坐标）。"""
    actors = []
    for aid in sorted(exp.keys()):
        e = exp[aid]
        x, y, z, yaw = e['x'], e['y'], e['z'], e['yaw']
        if aid == 41 and phase2:
            x, y, z = p2
            yaw = PHASE2_41_YAW
            if corrupt == 'pos':
                x += CORRUPT_POS_DELTA[0]
                y += CORRUPT_POS_DELTA[1]
                z += CORRUPT_POS_DELTA[2]
        actors.append({
            'id': aid, 'name': e['name'], 'team': e['team'],
            'x': round(x, 3), 'y': round(y, 3), 'z': round(z, 3),
            'yaw': round(yaw, 1), 'hp': e['hp'], 'alive': e['alive'],
            'bot': False, 'w': e['w'],
        })
    obj = {'round': 1, 'phase': 'Live', 'scoreT': 0, 'scoreCT': 0,
           'aliveT': sum(1 for a in actors if a['alive'] and a['team'] == 'T'),
           'aliveCT': sum(1 for a in actors if a['alive'] and a['team'] == 'CT'),
           'dropped': 0, 'actors': actors}
    return json.dumps(obj, ensure_ascii=False, separators=(',', ':'))


def write_expected(path, exp, phase2_target):
    """把"声明要发的值"落盘（判据的期望值 + 我实际发的值分开记，便于事后核对）。"""
    lines = ['# 本脚本声明要发的值（判据期望）；第一段 = 初始位置，第二段 = id=41 挪到 PHASE2_41']
    for aid in sorted(exp.keys()):
        e = exp[aid]
        lines.append('phase1\t%d\t%.3f\t%.3f\t%.3f\t%.1f\t%d\t%s' %
                     (aid, e['x'], e['y'], e['z'], e['yaw'], e['hp'], str(e['alive']).lower()))
    e41 = exp[41]
    lines.append('phase2\t41\t%.3f\t%.3f\t%.3f\t%.1f\t%d\t%s' %
                 (phase2_target[0], phase2_target[1], phase2_target[2], PHASE2_41_YAW, e41['hp'], 'true'))
    with open(path, 'w', encoding='utf-8') as f:
        f.write('\n'.join(lines) + '\n')


def parse_obs(path):
    """读 Unity 侧探针落盘的观测 TSV：section\tkey\tvalue 三列。"""
    obs = {}
    with open(path, 'r', encoding='utf-8') as f:
        for line in f:
            line = line.rstrip('\n').rstrip('\r')
            if not line or line.startswith('#'):
                continue
            parts = line.split('\t')
            if len(parts) < 3:
                continue
            obs[(parts[0], parts[1])] = parts[2]

    def num(key, default=None):
        v = obs.get(key)
        if v is None:
            return default
        try:
            return float(v)
        except ValueError:
            return default
    return obs, num


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--port', type=int, default=PORT)
    ap.add_argument('--base', default='', help='底座 x,y,z,fx,fz,ax,az（不给就读 --base-file）')
    ap.add_argument('--base-file', default=os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                                       '..', '..', '.ai-tmp', 'test', 'lan-remote-place.txt'))
    ap.add_argument('--sent', required=True)
    ap.add_argument('--obs', required=True)
    ap.add_argument('--corrupt', default='none', choices=['none', 'pos', 'stop'])
    ap.add_argument('--phase2', type=float, default=4.0)
    ap.add_argument('--seconds', type=float, default=30.0)
    ap.add_argument('--linger', type=float, default=12.0, help='观测文件出现后继续发快照的秒数（截图期链路要活着）')
    ap.add_argument('--wait', type=float, default=40.0, help='等 Unity 侧连上来的最长时间')
    ap.add_argument('--obs-wait', type=float, default=40.0, help='等观测文件出现的最长时间')
    a = ap.parse_args()

    # 「观测文件必须比本轮新」的起点：取**本进程启动时刻**（不是 accept 返回之后）。
    #    报"观测文件一直没出现"（而文件明明在盘上、内容也对）。驱动程序在拉起本脚本**之前**
    #    会把观测文件截断 ⇒ "mtime >= 本进程启动时刻" 就是正确的"本轮新鲜"口径。
    obs_start = time.time()

    base = load_base(a.base_file, a.base)
    p2 = phase2_pos(base)
    exp = build_expected(base)
    write_expected(a.sent, exp, p2)
    print('[0] 底座 玩家pos=(%.3f,%.3f,%.3f) 朝向=(%.3f,%.3f) **开阔方向**=(%.3f,%.3f) 负控模式=%s phase2=%.1fs'
          % (base[0], base[1], base[2], base[3], base[4], base[5], base[6], a.corrupt, a.phase2))
    for aid in sorted(exp.keys()):
        e = exp[aid]
        print('    期望 id=%d %-9s %-2s P1=(%.3f,%.3f,%.3f) yaw=%.1f hp=%d alive=%s' %
              (aid, e['name'], e['team'], e['x'], e['y'], e['z'], e['yaw'], e['hp'], e['alive']))
    print('    期望 id=41 第二段=(%.3f,%.3f,%.3f) yaw=%.1f（= 底座 + 沿开阔方向/右手边 %s）'
          % (p2 + (PHASE2_41_YAW, PHASE2_41_FS)))
    if a.corrupt == 'pos':
        print('    ⚠ 负控 pos：实际发 id=41 第二段 = (%.3f,%.3f,%.3f)（与声明值不同 ⇒ 判据必须转红）' %
              (p2[0] + CORRUPT_POS_DELTA[0], p2[1] + CORRUPT_POS_DELTA[1],
               p2[2] + CORRUPT_POS_DELTA[2]))
    if a.corrupt == 'stop':
        print('    ⚠ 负控 stop：第二段一条都不发并断开（视图应停在 P1 ⇒ 判据必须转红）')

    # ---- 监听（本脚本 = 主机，第二个进程）----
    srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    srv.bind(('127.0.0.1', a.port))
    srv.listen(1)
    srv.settimeout(1.0)
    print('[1] 主机就绪：TCP 127.0.0.1:%d（等 Unity 侧 CsLanClient 连上来）' % a.port)

    conn = None
    t0 = time.time()
    while time.time() - t0 < a.wait:
        try:
            conn, addr = srv.accept()
            break
        except socket.timeout:
            continue
    if conn is None:
        print('[1] 没有客户端连上来（等了 %.0fs）' % a.wait)
        print('RESULT-LANREMOTE-CLIENT: FAIL\n  口径：Unity 侧 CsLanClient 从未连上 127.0.0.1:%d' % a.port)
        return 1
    conn.settimeout(1.0)
    print('[1] 客户端已连：%s:%d' % addr)

    buf = b''
    joined = False
    snaps = 0
    bye = False
    start = time.time()
    obs_seen_at = None

    def read_lines():
        """读 socket 里已经到的整行；返回 (join_payload, saw_bye)。

        ⚠️ 必须**非阻塞**（`select` 轮询，超时 0）：本函数在发快照的循环里被调，
        而 `recv` 带 1s 超时会把这个循环拖成 ~1 Hz —— 实测踩过：客户端 4 s 只收到 4 条
        快照（期望 ~40 条），看起来像"客户端的 10 Hz 没跑起来"，其实是主机自己发慢了。
        """
        nonlocal buf, joined, bye
        try:
            ready, _, _ = select.select([conn], [], [], 0)
        except Exception:
            bye = True
            return
        if not ready:
            return
        try:
            chunk = conn.recv(4096)
        except BlockingIOError:
            return
        except Exception:
            bye = True
            return
        if not chunk:
            bye = True
            return
        buf += chunk
        while b'\n' in buf:
            line, _, buf = buf.partition(b'\n')
            text = line.decode('utf-8', 'replace').strip('\r')
            if not text:
                continue
            magic, _, payload = text.partition('|')
            if magic == JOIN:
                joined = True
                print('[2] 收到 %s|%s' % (JOIN, payload))
                conn.sendall((WELCOME + '|' + json.dumps(
                    {'proto': 1, 'name': 'lan-remote-view-check', 'host': '127.0.0.1:%d' % a.port,
                     'maxPlayers': 10, 'snapHz': 10.0}, separators=(',', ':')) + '\n').encode('utf-8'))
                print('[2] 已回 %s' % WELCOME)
            elif magic == BYE:
                bye = True
                print('[2] 收到 %s（客户端主动断开）' % BYE)

    # ---- 发快照（0.1 s 一条）----
    phase2_done = False
    last_obs_mtime = 0.0
    while time.time() - start < a.seconds:
        read_lines()
        if bye:
            print('[3] 客户端断开，停止发快照（已发 %d 条）' % snaps)
            break

        if not joined:
            time.sleep(0.02)
            continue

        t = time.time() - start
        phase2 = t >= a.phase2

        if phase2 and not phase2_done:
            phase2_done = True
            if a.corrupt == 'stop':
                print('[3] 负控 stop：第 %.1fs 起不再发快照并断开' % t)
                try:
                    conn.close()
                except Exception:
                    pass
                break

        line = SNAP + '|' + frame_json(exp, phase2, a.corrupt, p2)
        try:
            conn.sendall((line + '\n').encode('utf-8'))
            snaps += 1
        except Exception as ex:
            print('[3] 发快照失败：%r' % (ex,))
            break

        # ---- 观测文件一到就进入 linger（截图期链路还得活着）----
        if obs_seen_at is None and os.path.exists(a.obs):
            mt = os.path.getmtime(a.obs)
            if mt >= obs_start and os.path.getsize(a.obs) > 0:
                obs_seen_at = time.time()
                last_obs_mtime = mt
                print('[3] 已看到 Unity 侧观测文件（第 %d 条快照后），进入 %.0fs linger（保持链路活着供截图）'
                      % (snaps, a.linger))
        if obs_seen_at is not None and time.time() - obs_seen_at > a.linger:
            print('[3] linger 结束，停止发快照（共 %d 条）' % snaps)
            break

        time.sleep(0.1)

    # ---- 等观测文件（如果上面没等到）----
    if obs_seen_at is None:
        t_obs = time.time()
        while time.time() - t_obs < a.obs_wait:
            if os.path.exists(a.obs) and os.path.getmtime(a.obs) >= obs_start and os.path.getsize(a.obs) > 0:
                obs_seen_at = time.time()
                break
            time.sleep(0.25)

    try:
        conn.close()
    except Exception:
        pass
    srv.close()

    print('[4] 本脚本共发出 %d 条 %s（JOIN=%s BYE=%s）' % (snaps, SNAP, joined, bye))

    # ---- 判据 ----
    if obs_seen_at is None:
        print('[5] 观测文件 %s 一直没出现/内容为空 ⇒ 判据无法比对' % a.obs)
        print('RESULT-LANREMOTE-CLIENT: FAIL\n  口径：Unity 侧探针没有落盘观测值')
        return 1

    obs, num = parse_obs(a.obs)
    isrunning = obs.get(('client', 'isrunning'), '?')
    welcomes = num(('client', 'welcomes'), -1)
    snapshots = num(('client', 'snapshots'), -1)
    malformed = num(('client', 'malformed'), -1)
    views = num(('client', 'views'), -1)
    registry = num(('client', 'registry'), -1)
    pooled = num(('client', 'pooled'), -1)
    created = num(('client', 'createdTotal'), -1)
    recycled = num(('client', 'recycledTotal'), -1)
    stripped = num(('client', 'strippedColliders'), -1)

    print('\n[5] Unity 侧手上的值（探针从运行时对象读） vs 本脚本声明要发的值：')
    ok_fields = True
    ok_views = True
    for aid in sorted(exp.keys()):
        vp = obs.get(('view', '%d.pos' % aid), '-')
        vy = obs.get(('view', '%d.yaw' % aid), '-')
        va = obs.get(('view', '%d.alive' % aid), '-')
        vact = obs.get(('view', '%d.active' % aid), '-')
        vcol = obs.get(('view', '%d.colliders' % aid), '-')
        # 期望：id=41 在第二段之后 = PHASE2_41（除非负控，那时"实际发的"与声明不同，判据**仍按声明**判）
        e = exp[aid]
        if aid == 41:
            ex, ey, ez, eyaw = p2[0], p2[1], p2[2], PHASE2_41_YAW
        else:
            ex, ey, ez, eyaw = e['x'], e['y'], e['z'], e['yaw']
        got = [float(x) for x in vp.split(',')] if vp not in ('-', '') else None
        d = None
        if got and len(got) == 3:
            d = max(abs(got[0] - ex), abs(got[1] - ey), abs(got[2] - ez))
        yok = vy not in ('-', '') and abs(float(vy) - eyaw) <= 0.06
        pos_ok = d is not None and d <= 0.002
        print('    id=%d 手上=(%s) yaw=%s alive=%s active=%s colliders=%s | 收到(声明)=(%.3f,%.3f,%.3f) yaw=%.1f '
              '| Δmax=%s pos_ok=%s yaw_ok=%s'
              % (aid, vp, vy, va, vact, vcol, ex, ey, ez, eyaw,
                 ('%.4f' % d) if d is not None else 'NA', pos_ok, yok))
        ok_fields = ok_fields and pos_ok and yok

    # J3：客户端侧生成了 N 个远端视图（N == 本脚本发的 actor 数）。
    # 两个口径都要对：`views` = 探针按 RemoteId>=0 数出来的；`registry` = CsLanRemoteView.ViewCount（系统自己的账）。
    ok_views = (views == len(ACTORS)) and (registry == len(ACTORS))
    ok_client = (isrunning == 'true') and welcomes >= 1 and snapshots >= 15 and malformed == 0
    # J6：死亡那一位的 alive 标记跟着快照走（视图仍被保留 ⇒ `views` 里数得到它）
    ok_dead = (obs.get(('view', '43.alive'), '?') == 'false')
    ok_colliders = all(obs.get(('view', '%d.colliders' % aid), '?') == '0' for aid in exp)
    # J8 池化（不每帧新建）：累计新建的视图数必须**远小于**帧数 —— 每帧新建的话它 ≈ 快照条数。
    # 引擎池口径：回收 = 置非活跃 + 挂回池根（**不销毁**）⇒ `pooled` 不必为 0，它是"实例确实被复用"的证据。
    ok_pooling = (created >= 1) and (created * 3 <= snapshots)

    print('\n[6] 客户端侧：isrunning=%s 收到WELCOME=%s 快照=%s 非法行=%s 远端视图=%s（期望 %d）'
          % (isrunning, welcomes, snapshots, malformed, views, len(ACTORS)))
    print('     注册表 CsLanRemoteView.ViewCount=%s（期望 %d）；已回池实例=%s 具（引擎池"不销毁"口径 ⇒ 不必为 0）'
          % (registry, len(ACTORS), pooled))
    print('     池化：累计新建=%s 累计回收=%s 销毁Collider=%s ｜ 快照=%s ⇒ 新建×3 <= 快照 = %s'
          % (created, recycled, stripped, snapshots, ok_pooling))
    print('     死亡那具（id=43）：alive=%s（必须 false）active=%s（渲染与否属既有 ActorView 的倒地留场策略，单列不设闸门）'
          % (obs.get(('view', '43.alive'), '?'), obs.get(('view', '43.active'), '?')))
    print('     不参与本机命中（所有视图 Collider=0）= %s' % ok_colliders)

    ok = ok_client and ok_views and ok_fields and ok_dead and ok_colliders and ok_pooling
    print('\nRESULT-LANREMOTE-CLIENT: %s' % ('PASS' if ok else 'FAIL'))
    print('  口径（独立进程判据）：客户端在跑=%s 握过手=%s 快照>=15=%s（=%s） 非法行=0=%s'
          % (isrunning == 'true', welcomes >= 1, snapshots >= 15, snapshots, malformed == 0))
    print('     客户端侧生成了 %s 个远端视图（= 发的 actor 数 %d）= %s；首个视图坐标与快照逐字段一致=%s；'
          '死亡标记跟着走=%s；Collider 全 0=%s；池化复用=%s；负控=%s'
          % (views, len(ACTORS), ok_views, ok_fields, ok_dead, ok_colliders, ok_pooling, a.corrupt))
    return 0 if ok else 1


if __name__ == '__main__':
    sys.exit(main())
