#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""差异 #88「能开局」判据的**第二个进程** —— 一个真的局域网客户端。

⛔ 为什么必须是独立进程：同一个 Unity 进程里自问自答证明不了"能开局"
（引擎里就有一个回环应答器干这个）。本脚本只做一件事：
按 `CsLanGateway` 定死的线格式连上主机的 TCP 网关、握手、收世界快照。

线格式（⛔ 与 client/Assets/Scripts/Module/Net/CsLanGateway.cs 必须逐字一致）：
    客户端 -> 主机   CS16-LAN-JOIN/1|<json>
    主机   -> 客户端 CS16-LAN-WELCOME/1|<json>
    主机   -> 客户端 CS16-LAN-SNAP/1|<json>     （每 0.1s 一条）
    客户端 -> 主机   CS16-LAN-INPUT/1|<json>     （本片只计数）

判据（写死在这里，不靠人眼看）：
    PASS <=> ① 连上了（TCP 握手成功）
             ② 收到 WELCOME（>0 条）
             ③ 收到 SNAP（>=5 条，快照是**连续**的而不只是"回了一包就走"）
             ④ 最后一条 SNAP 能解析出 JSON，且 actors 数 >= 2（世界里有多个角色）

用法：
    python tools/probes/lan-gateway-client.py --addr 127.0.0.1:8002 [--seconds 12] [--name py-client]
"""
import argparse
import io
import json
import socket
import sys
import time

sys.stdout.reconfigure(encoding='utf-8')

JOIN = 'CS16-LAN-JOIN/1'
WELCOME = 'CS16-LAN-WELCOME/1'
SNAP = 'CS16-LAN-SNAP/1'
INPUT = 'CS16-LAN-INPUT/1'
BYE = 'CS16-LAN-BYE/1'


def parse_addr(text):
    host, _, port = text.rpartition(':')
    return host, int(port)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--addr', default='127.0.0.1:8002')
    ap.add_argument('--seconds', type=float, default=12.0)
    ap.add_argument('--name', default='py-client')
    ap.add_argument('--wait', type=float, default=30.0, help='等主机起来的最长时间（秒）')
    a = ap.parse_args()

    host, port = parse_addr(a.addr)

    # ---- ① 连接（主机可能还没起好，给它时间）----
    sock = None
    t0 = time.time()
    last_err = None
    while time.time() - t0 < a.wait:
        try:
            sock = socket.create_connection((host, port), timeout=3.0)
            break
        except Exception as ex:
            last_err = ex
            time.sleep(0.5)
    if sock is None:
        print('[A] 连接 %s:%d 失败（等了 %.0fs）：%r' % (host, port, a.wait, last_err))
        print('RESULT-LANCLIENT: FAIL\n  口径：连不上主机的 TCP 网关')
        return 1
    sock.settimeout(0.5)
    print('[A] 已连接 %s:%d（耗时 %.2fs）' % (host, port, time.time() - t0))

    # ---- ② 发 JOIN ----
    hello = json.dumps({'name': a.name, 'proto': 1}, ensure_ascii=False, separators=(',', ':'))
    sock.sendall((JOIN + '|' + hello + '\n').encode('utf-8'))
    print('[B] 已发 %s|%s' % (JOIN, hello))

    # ---- ③ 收报文（一行一条，以 \n 结束）----
    buf = b''
    welcomes = 0
    snaps = 0
    malformed = 0
    last_snap = None
    welcome_text = None
    first_snap_at = None
    deadline = time.time() + a.seconds
    while time.time() < deadline:
        try:
            chunk = sock.recv(4096)
        except socket.timeout:
            continue
        except Exception as ex:
            print('[C] 收包异常：%r' % (ex,))
            break
        if not chunk:
            print('[C] 主机关闭了连接')
            break
        buf += chunk
        while b'\n' in buf:
            line, _, buf = buf.partition(b'\n')
            text = line.decode('utf-8', 'replace').strip('\r')
            if not text:
                continue
            magic, _, payload = text.partition('|')
            if magic == WELCOME:
                welcomes += 1
                welcome_text = payload
                print('[D] WELCOME #%d %s' % (welcomes, payload))
            elif magic == SNAP:
                snaps += 1
                if first_snap_at is None:
                    first_snap_at = time.time()
                last_snap = payload
            else:
                malformed += 1
                print('[D] 未知报文 magic=%r' % (magic,))

    # ---- ④ 发一条 INPUT（证明双向都通；本片主机只计数）----
    inp = json.dumps({'move': [0, 1], 'yaw': 180.0, 'jump': False}, separators=(',', ':'))
    try:
        sock.sendall((INPUT + '|' + inp + '\n').encode('utf-8'))
        print('[E] 已发 %s|%s' % (INPUT, inp))
    except Exception as ex:
        print('[E] 发 INPUT 失败：%r' % (ex,))

    try:
        sock.sendall((BYE + '|{}\n').encode('utf-8'))
    except Exception:
        pass
    sock.close()

    # ---- ⑤ 解析最后一条 SNAP ----
    actors = -1
    snap_ok = False
    if last_snap:
        try:
            obj = json.loads(last_snap)
            actors = len(obj.get('actors', []))
            snap_ok = True
            print('[F] 最后一条 SNAP：round=%s phase=%s score CT %s : %s T alive CT=%s T=%s 角色数=%d'
                  % (obj.get('round'), obj.get('phase'), obj.get('scoreCT'), obj.get('scoreT'),
                     obj.get('aliveCT'), obj.get('aliveT'), actors))
            for ac in obj.get('actors', [])[:4]:
                print('      id=%s %-10s %-2s pos=(%s,%s,%s) hp=%s alive=%s w=%s'
                      % (ac.get('id'), ac.get('name'), ac.get('team'),
                         ac.get('x'), ac.get('y'), ac.get('z'), ac.get('hp'), ac.get('alive'), ac.get('w')))
        except Exception as ex:
            print('[F] 最后一条 SNAP 解析失败：%r' % (ex,))

    dur = (time.time() - first_snap_at) if first_snap_at else 0.0

    ok1 = True
    ok2 = welcomes > 0
    ok3 = snaps >= 5
    ok4 = snap_ok and actors >= 2
    print('\n[G] WELCOME=%d SNAP=%d（收到首条起持续 %.1fs）未知报文=%d 角色数=%d'
          % (welcomes, snaps, dur, malformed, actors))
    print('RESULT-LANCLIENT: %s' % ('PASS' if (ok1 and ok2 and ok3 and ok4) else 'FAIL'))
    print('  口径：连上=True 收到WELCOME>=1=%s（=%d） 收到SNAP>=5=%s（=%d） 解析出>=2个角色=%s（=%d）'
          % (ok2, welcomes, ok3, snaps, ok4, actors))
    return 0 if (ok1 and ok2 and ok3 and ok4) else 1


if __name__ == '__main__':
    sys.exit(main())
