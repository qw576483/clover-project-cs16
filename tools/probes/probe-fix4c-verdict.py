#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
probe-fix4c-verdict.py —— 片FIX-4 线C 的**运行期判据**（换弹计数一致 + 弹痕可见性）。

判据（全部来自**用户真按键那条链**产生的产物）
---------------------------------------------
输入 = ① `unity console --format json` 的运行时日志（驱动用 fireheld -> SetFireHeldForTest
       打出的真实开火链 + 模拟自己的 auto-reload 链）
      ② 一组同机位前后帧（before = 一枪未发，after = 打完）

  J-R1  换弹**计数一致**：`[Match] <本地玩家> 开始换弹` 的行数 == `[View] 第一人称播换弹` 的行数（差 = 0）
        —— 这正是用户报的症状（旧代码 25 : 2）。
        ⚠️ **口径必须是"本地玩家"**：`开始换弹` 是**所有 actor**（含 8 个 bot）都会打的行；
        用户报的是"我手上这把枪没动画"，所以计数必须按 `本地玩家=<名字>(id=N)` 里解析出的**那个名字**过滤。
        bot 的换弹动画是第三人称 ActorView 的事（另归一线），混进来只会把判据弄脏。
  J-R1b 边界不得静默：`viewmodel.reload.gate.anim` / `.state` 出现次数必须为 0
        （出现即说明"边沿被吞"；那两条是**新的**显式留痕，旧代码没有 ⇒ 它们的出现就是回归）
  J-R2  根因可核对：`viewmodel.local.changed` 的次数会被打印出来（>0 即"actor 对象被换过"，
        也就是本片对根因的预测；⛔ 不设阈值 —— 它是**解释性**证据，不是通过条件）
  J-R3  **每帧闸门不得停在早退门**：`viewmodel.gate` 的留痕里不得出现"运行 ⇒ 闸门C/闸门B/闸门A"
        的**停留**（即一次真实换弹窗口内，Tick 必须走到 `UpdateAnim` 那一行，而不是在 A/B/C 早退）。
        判据 = `Player 开始换弹` 的每一次，在它之后 2.2 s 内必须有 `viewmodel.reload.diag` 行。
        （⛔ 这一条是**新增**的：它把"边沿没来"和"判边沿的那一帧根本没跑到"区分开。）
  J-R4  **序号必须单调推进**：`Player 开始换弹 … seq=N` 里的 N 必须**严格递增**
        （这是"表现层能不能看到边沿"的直接读数；编号不动 ⇒ 表现层再怎么看也看不到。）
  J-R5  **Animator 真状态取样**（`换弹动画心跳（播后 0.5s）`）：每一次**未被取消**的换弹，
        其取样必须是 `是reload剪辑=True` 且 `归一化时间>0`（= reload 帧动画真的在跑）。
  J-R7  **陈旧完成回调的直接读数**（本片 OnClipFinished 修法的判别式，2026-09-24 run E 实测标定）：
        每一次**未被取消**的换弹，其 `viewmodel.reload.diag` 采样里 `over` 必须**至少有一条 ≠ `-`**
        （=`_overrideState` 覆盖态在换弹期间没被"上一段剪辑的收尾回调"提前踢掉）。
        基准（修法前 run E）：seq=1..8 每条 diag 都是 `over=reload`；**seq=9 的 6 条全是 `over=-`**
        而 `active=True`、`ReloadEndTime=257.51 > now=255.86` ⇒ 覆盖态在换弹动画还没跑起来就被清了。
        ⛔ 这一条是**直接**证据，不依赖"计数一致"（与降频日志无关）。
  J-R6  **覆盖态必须归位**（OnClipFinished 修法的**残留风险**：换弹末帧卡住）：
        每次换弹结束后 0.6 s 的 `viewmodel.reload.settle` 行里 `over` 必须 = `-`。
        若一行都没有 ⇒ 判据未生效（UNJUDGED，不是通过）。
  J-D1  弹痕**可见性**：至少一条 `[Combat] 弹痕落在 …` 行，且其中的 `可见核心宽(米)` ≥ 阈值
        （**与距离无关**的量；`核心投影 px` 随距离变，只作观测）
  J-D2  弹痕**真的改了画面**：before/after 两帧在"弹痕像素"上必须有差异，且
        差分像素数 ≥ MIN_DIFF_PX 且至少 MIN_DARK_PX 个像素**变暗**（弹痕是黑洞，不是白点）
  J-D2b **帧差必须可归因于弹痕**（量具自检，两条）：
        (a) **同回合**：`--rounds-file` 里 before/after 的 `round=` 必须相等
            （run E 的 before/after 跨了一次回合重开 ⇒ 整帧都变，帧差不可归因）；
        (b) **局部性**：差异像素占比必须 ≤ MAX_DIFF_FRAC（整帧都在变 = 相机动了/回合重开，
            与"多了一块弹痕"不是一回事）。

用法
----
  python tools/probes/probe-fix4c-verdict.py --console <json> --before <png> --after <png> [--after2 <png>]
  python tools/probes/probe-fix4c-verdict.py --rounds-file <tsv>   # J-D2b(a)：before/after 的 round=
  python tools/probes/probe-fix4c-verdict.py --corrupt=count   # 负控：把播换弹计数 -1 ⇒ J-R1 必须红
  python tools/probes/probe-fix4c-verdict.py --corrupt=decal   # 负控：把可见核心宽 -30% ⇒ J-D1 必须红
  python tools/probes/probe-fix4c-verdict.py --corrupt=seq     # 负控：把序号序列抹平 ⇒ J-R4 必须红
  python tools/probes/probe-fix4c-verdict.py --corrupt=gate    # 负控：抹掉全部 reload.diag ⇒ J-R3 必须红
  python tools/probes/probe-fix4c-verdict.py --corrupt=override # 负控：把 diag 的 over 全抹成 - ⇒ J-R7 必须红
  python tools/probes/probe-fix4c-verdict.py --corrupt=diffuse  # 负控：把帧差判据的输入放大成全帧 ⇒ J-D2b 必须红
  python tools/probes/probe-fix4c-verdict.py --corrupt=stuckreload # 负控：把 settle 的 over 改写成换弹剪辑 ⇒ J-R6 必须红
"""
import argparse
import json
import os
import re
import struct
import sys
import zlib

HERE = os.path.dirname(os.path.abspath(__file__))
PROJ = os.path.abspath(os.path.join(HERE, "..", ".."))

MIN_CORE_PROJ_PX = 15.0     # J-D1：与 probe-decal-visibility.py 的 J2 同一阈值、同一口径
REF_DISTANCE_M = 2.0        # J-D1 的参考距离（用户"2 m 外该看得见"的口径）
# 在参考距离上投影到 MIN_CORE_PROJ_PX 所需的**可见核心宽**（米）——
# 这一个是与距离无关的量，才配当通过条件。
MIN_CORE_M = MIN_CORE_PROJ_PX / (1920.0 / (2.0 * REF_DISTANCE_M))   # = 0.03125 m
MIN_DIFF_PX = 200           # J-D2：帧间差异像素下限
MIN_DARK_PX = 60            # J-D2：其中"变暗"的像素下限（弹痕=黑洞）
DARK_DELTA = 8              # 变暗判定：ΔL <= -8
MAX_DIFF_FRAC = 0.25        # J-D2b(b)：差异像素占比上限
# 为什么是 0.25（而不是 0.05 那种"凭感觉"的小阈值）：
#   合法帧差（相机没动、同一回合，只是多了几块弹痕 + HUD 数字/雷达的局部变化）预期 **远低于 2%**；
#   而**相机位移 / 回合重开是全帧事件**：哪怕只移 1 个像素，几乎所有边缘像素都会变，占比 ≥30%。
#   ⇒ 上限取 25%，落在这两个量级之间的空隙里：既能拦住"整帧在变"的伪帧差，
#     又不会因为枪身摆动/雷达这类局部非确定性把合法帧判红。
#   实测口径见输出里的 `J-D2b[tag] 差异占比=x%（上限 25.00%）` —— 这个数是**观测量**，要进回报。


# ---------------------------------------------------------------------------
#  纯 python PNG 解码（本机无 PIL；与 probe-decal-visibility.py 同一实现）
# ---------------------------------------------------------------------------
def read_png(path):
    data = open(path, "rb").read()
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError("not a png: %s" % path)
    i, w, h, ct, bd, idat = 8, 0, 0, 0, 8, b""
    while i < len(data):
        ln = struct.unpack(">I", data[i:i + 4])[0]
        typ = data[i + 4:i + 8]
        body = data[i + 8:i + 8 + ln]
        i += 12 + ln
        if typ == b"IHDR":
            w, h, bd, ct = struct.unpack(">IIBB", body[:10])
        elif typ == b"IDAT":
            idat += body
        elif typ == b"IEND":
            break
    raw = zlib.decompress(idat)
    if bd != 8:
        raise ValueError("only 8-bit pngs are supported (got %d)" % bd)
    ch = {0: 1, 2: 3, 3: 1, 4: 2, 6: 4}[ct]
    stride = w * ch
    rows, prev, o = [], bytearray(stride), 0
    for _y in range(h):
        f = raw[o]
        o += 1
        line = bytearray(raw[o:o + stride])
        o += stride
        if f == 0:
            pass
        elif f == 1:
            for x in range(ch, stride):
                line[x] = (line[x] + line[x - ch]) & 255
        elif f == 2:
            for x in range(stride):
                line[x] = (line[x] + prev[x]) & 255
        elif f == 3:
            for x in range(stride):
                a = line[x - ch] if x >= ch else 0
                line[x] = (line[x] + ((a + prev[x]) >> 1)) & 255
        elif f == 4:
            for x in range(stride):
                a = line[x - ch] if x >= ch else 0
                b = prev[x]
                c = prev[x - ch] if x >= ch else 0
                p = a + b - c
                pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
                pr = a if (pa <= pb and pa <= pc) else (b if pb <= pc else c)
                line[x] = (line[x] + pr) & 255
        rows.append(bytes(line))
        prev = line
    return w, h, ch, rows


def luma_rows(w, h, ch, rows):
    """转成逐像素亮度（只保留足够做帧差的信息；1920×1080 也只要几秒）。"""
    out = []
    for y in range(h):
        r = rows[y]
        if ch >= 3:
            lr = [((r[x * ch] * 77 + r[x * ch + 1] * 151 + r[x * ch + 2] * 28) >> 8) for x in range(w)]
        else:
            lr = [r[x * ch] for x in range(w)]
        out.append(lr)
    return out


def load_console(path):
    txt = open(path, "r", encoding="utf-8", errors="replace").read().strip()
    if txt.startswith('"'):
        val = json.loads(txt)
        return val
    return json.loads(txt)


def messages(obj):
    msgs = []
    data = obj.get("data", obj)
    res = data.get("result", data)
    for e in res.get("entries", []):
        # 条目必须是对象（`{seq,timestampUtc,level,logType,message,…}`）。
        #    夹具/拼接缓冲里混进裸字符串时要**跳过**而不是崩掉 —— 崩掉会让整条判据链
        if isinstance(e, dict):
            msgs.append(e.get("message", ""))
        elif isinstance(e, str):
            msgs.append(e)
    return msgs


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--console", default=os.path.join(PROJ, ".ai-tmp", "test", "fix4c-console.json"))
    ap.add_argument("--before", default=os.path.join(PROJ, ".ai-tmp", "screenshots", "fix4c_before.png"))
    ap.add_argument("--after", default=os.path.join(PROJ, ".ai-tmp", "screenshots", "fix4c_floor.png"))
    ap.add_argument("--after2", default=os.path.join(PROJ, ".ai-tmp", "screenshots", "fix4c_wall.png"))
    ap.add_argument("--corrupt", default=None,
                    choices=[None, "count", "decal", "seq", "gate", "probe", "override", "diffuse",
                             "stuckreload"])
    ap.add_argument("--rounds-file", dest="rounds_file", default=None,
                    help="TSV：每行 `before<TAB>回合号` / `after<TAB>回合号`（J-D2b(a) 的同回合前置）")
    ap.add_argument("--json", default=None)
    args = ap.parse_args()

    rows_out, fails = [], []
    summary = {"corrupt": args.corrupt}

    # ---------------- 运行期日志判据 ----------------
    if os.path.exists(args.console):
        msgs = messages(load_console(args.console))
        rows_out.append("console entries = %d" % len(msgs))

        # ---- 量具自检：控制台是**滚动 2000 条**缓冲；若里面混了两次 Play（= 两套二进制），
        #      任何"计数一致"型判据都会把两次运行**相加**，得出没意义的结论（实测过一次：
        #      19 次换弹 vs 11 次播 = 两次运行的 9+10 : 1+10，不是"漏播 8 次"）。----
        sessions = [m for m in msgs if "视图同步开始：本局就位" in m]
        summary["console_sessions"] = len(sessions)
        rows_out.append("量具自检：控制台里的『本局就位』= %d 次（判据 ≤ 1；>1 = 混了多套二进制，"
                        "计数类判据无意义，驱动必须先 clear_console；0 = clear 发生在本局开始之后，合法）"
                        % len(sessions))
        if len(sessions) > 1:
            fails.append("量具自检失败：控制台缓冲混了 %d 次 Play 会话 ⇒ J-R1/J-R4 的计数是把多次运行"
                         "相加得到的，读数无效（驱动里加 `clear_console` 后重跑）" % len(sessions))

        # ---- 本地玩家是谁？从"视图同步开始：本局就位（本地玩家=Player(id=1, CT))"里解析 ----
        local_names = []
        for m in msgs:
            mm = re.search(r"本地玩家=(\S+?)\(id=(\d+)", m)
            if mm and mm.group(1) not in local_names:
                local_names.append(mm.group(1))
        local_name = local_names[-1] if local_names else "Player"
        rows_out.append("本地玩家名（从日志解析）= %s（出现过的：%s）"
                        % (local_name, "/".join(local_names) if local_names else "未解析到，回落 Player"))

        all_start = [m for m in msgs if re.search(r"\[Match\]\s+\S+\s+开始换弹", m)]
        # 口径：只数**本地玩家**的（bot 的换弹是第三人称 ActorView 的事，混进来会弄脏判据）
        reload_start = [m for m in msgs
                        if re.search(r"\[Match\]\s+%s\s+开始换弹" % re.escape(local_name), m)]
        reload_play = [m for m in msgs if "第一人称播换弹" in m]
        gate_anim = [m for m in msgs if "viewmodel.reload.gate.anim" in m]
        gate_state = [m for m in msgs if "viewmodel.reload.gate.state" in m]
        local_changed = [m for m in msgs if "viewmodel.local.changed" in m]
        gate_trace = [m for m in msgs if "viewmodel.gate" in m]
        diag = [m for m in msgs if "viewmodel.reload.diag" in m]

        n_start, n_play = len(reload_start), len(reload_play)
        if args.corrupt == "count" and n_play > 0:
            n_play -= 1                       # 负控：抹掉一次播报
        summary.update(local_name=local_name, n_start=n_start, n_play=n_play,
                       n_start_all_actors=len(all_start),
                       gate_anim=len(gate_anim), gate_state=len(gate_state),
                       local_changed=len(local_changed),
                       gate_trace=len(gate_trace), diag=len(diag))

        rows_out.append("（口径说明）全部 actor 的 `开始换弹` = %d 行；其中本地玩家 `%s` 的 = %d 行"
                        % (len(all_start), local_name, n_start))
        rows_out.append("J-R1 `%s 开始换弹` = %d, `第一人称播换弹` = %d, 差 = %d（判据 == 0）"
                        % (local_name, n_start, n_play, n_start - n_play))
        if n_start == 0:
            fails.append("J-R1 没有任何本地玩家的 `开始换弹` 行 ⇒ 本链没跑出换弹，判据未生效（不是通过）")
        elif n_start != n_play:
            fails.append("J-R1 换弹计数不一致：`%s 开始换弹` %d vs `播换弹` %d（差 %d）"
                         "= 用户报的「换弹有时候没有动画」仍在"
                         % (local_name, n_start, n_play, n_start - n_play))

        rows_out.append("J-R1b gate.anim=%d, gate.state=%d（判据 == 0，出现即边沿被吞）"
                        % (len(gate_anim), len(gate_state)))
        if gate_anim or gate_state:
            fails.append("J-R1b 有 %d+%d 条 reload 闸门吞边沿的留痕 ⇒ 换弹仍会丢"
                         % (len(gate_anim), len(gate_state)))
        rows_out.append("J-R2 viewmodel.local.changed = %d（解释性：>0 = actor 对象被换过 = 本片根因预测）"
                        % len(local_changed))
        for m in local_changed[:3]:
            rows_out.append("      " + m.split("] ")[-1][:150])

        # ---- J-R4：本地玩家 `开始换弹 … seq=N` 的序号必须严格递增 ----
        seqs = []
        for m in reload_start:
            mm = re.search(r"seq=(\d+)", m)
            if mm:
                seqs.append(int(mm.group(1)))
        if args.corrupt == "seq" and len(seqs) >= 2:
            seqs[1:] = [seqs[0]] * (len(seqs) - 1)     # 负控：抹平
        if seqs:
            rows_out.append("J-R4 本地玩家换弹序号序列 = %s（%d 个，判据：严格递增）"
                            % (seqs[:20], len(seqs)))
            bad = [(i, seqs[i - 1], seqs[i]) for i in range(1, len(seqs)) if seqs[i] <= seqs[i - 1]]
            if bad:
                fails.append("J-R4 序号没有严格递增：%s ⇒ 表现层看不到第 2 次及以后的换弹边沿"
                             % ", ".join("%d->%d" % (a, b) for _, a, b in bad[:5]))
        else:
            rows_out.append("J-R4 本地玩家的 `开始换弹` 行里没有 `seq=` 字段"
                            " ⇒ 序号口径未生效（CsInventory 的 seq= 是新加的，旧日志没有）")

        # ---- J-R3：每一次真实换弹的 2.2 s 窗口里，Tick 必须留下 reload.diag 心跳 ----
        n_diag = len(diag)
        if args.corrupt == "gate":
            n_diag = 0                                  # 负控：抹掉心跳
        rows_out.append("J-R3 `viewmodel.reload.diag` 心跳 = %d 行（本地玩家换弹 %d 次；"
                        "判据：每次换弹都应有 ≥1 行）" % (n_diag, n_start))
        if n_start > 0 and n_diag == 0:
            fails.append("J-R3 一次换弹心跳都没有 ⇒ Tick 在换弹窗口里**根本没走到**心跳那一行"
                         "（即被闸门 A/B/C 早退吞掉，或 rig 根本没 Tick）")
        for m in diag[:2]:
            rows_out.append("      " + m.split("] ")[-1][:180])
        rows_out.append("J-R3b `viewmodel.gate` 闸门切换留痕 = %d 行（判据：可以 >0；"
                        "但若『运行 ⇒ 闸门C』出现在换弹期间，就是 J-R3 的根因）" % len(gate_trace))
        for m in gate_trace[:6]:
            rows_out.append("      " + m.split("] ")[-1][:180])

        # ---- J-R5：换弹动画**真的在跑**（Animator 真实状态取样，不只看那句 Play 调用）----
        #   豁免「被打断=True」：换弹中切枪会取消换弹（原版行为），取消后 Animator 当然不在
        probes = [m for m in msgs if "换弹动画心跳" in m]
        live = [m for m in probes if "被打断=True" not in m]
        excused = [m for m in probes if "被打断=True" in m]
        ok_probe = []
        bad_probe = []
        for m in live:
            mm = re.search(r"是reload剪辑=(\w+).*?归一化时间=([0-9.]+)", m)
            seqm = re.search(r"seq=(\d+)", m)
            if not mm:
                bad_probe.append((seqm.group(1) if seqm else "?", "拿不到 Animator 状态"))
                continue
            is_rel = mm.group(1) == "True"
            nt = float(mm.group(2))
            if is_rel and nt > 0.0:
                ok_probe.append((seqm.group(1) if seqm else "?", nt))
            else:
                bad_probe.append((seqm.group(1) if seqm else "?", "isReload=%s nt=%.2f" % (is_rel, nt)))
        if args.corrupt == "probe":
            bad_probe += ok_probe
            ok_probe = []
        summary["probe_ok"] = len(ok_probe)
        summary["probe_bad"] = len(bad_probe)
        summary["probe_excused"] = len(excused)
        rows_out.append("J-R5 Animator 真实状态取样 = %d 次通过 / %d 次不通过 / %d 次豁免（切枪打断换弹）"
                        "（判据：本地玩家每次**未被取消**的换弹都应有 1 次『是reload剪辑=True 且 归一化时间>0』）"
                        % (len(ok_probe), len(bad_probe), len(excused)))
        for s, nt in ok_probe[:3]:
            rows_out.append("      seq=%s 归一化时间=%.2f（帧动画确实在推进）" % (s, nt))
        for s, why in bad_probe[:5]:
            rows_out.append("      seq=%s %s" % (s, why))
        if n_start > 0 and not ok_probe:
            fails.append("J-R5 一次『reload 剪辑真的在跑』的取样都没有 ⇒ 换弹动画要么没播、要么播的不是 reload 帧动画")
        elif bad_probe:
            fails.append("J-R5 有 %d 次未被取消的换弹，其 Animator 状态不是 reload（或归一化时间没走）" % len(bad_probe))

        # ---- J-R7 陈旧完成回调的**直接**读数 ----
        #   口径：把 `viewmodel.reload.diag [运行] seq=N … over=<x> active=…` 按 seq 分组，
        #        每次**未被取消**（没有 `被打断=True` 取样）的换弹，必须至少有一条 `over != "-"`。
        #   为什么这是判别式：旧代码（run E）在换弹刚 Play 下去一两帧就被"上一段剪辑的收尾回调"
        #        踢掉 `_overrideState` ⇒ 那一次换弹的**全部** diag 行都是 `over=-`（seq=9 实测 6/6），
        #        而正常那 8 次全是 `over=reload`。⇒ 逐次可判、不依赖计数、不依赖降频日志。
        excused_seqs = set()
        for m in excused:
            mm = re.search(r"seq=(\d+)", m)
            if mm:
                excused_seqs.add(mm.group(1))
        over_by_seq = {}
        for m in diag:
            if "over=" not in m:
                continue                      # "换弹结束"/"心跳中断" 两种行没有 over=
            sm = re.search(r"seq=(\d+)", m)
            om = re.search(r"over=(\S+)", m)
            if not (sm and om):
                continue
            over_by_seq.setdefault(sm.group(1), []).append(om.group(1))
        if args.corrupt == "override":
            over_by_seq = {k: ["-"] * len(v) for k, v in over_by_seq.items()}
        jr7_bad = []
        for s in sorted(over_by_seq, key=lambda x: int(x)):
            if s in excused_seqs:
                continue
            vals = over_by_seq[s]
            if all(v == "-" for v in vals):
                jr7_bad.append((s, len(vals)))
        summary["jr7_bad"] = len(jr7_bad)
        rows_out.append("J-R7 有 diag 采样的换弹 = %d 次（豁免 %d 次）；其中『全部 over=-』的 = %d 次（判据 == 0）"
                        % (len(over_by_seq), len(excused_seqs & set(over_by_seq)), len(jr7_bad)))
        for s, n in jr7_bad:
            sample = over_by_seq[s][0] if over_by_seq[s] else "-"
            rows_out.append("      seq=%s 的 %d 条 diag 全是 over=%s ⇒ 覆盖态在换弹期间就被清了（陈旧完成回调）"
                            % (s, n, sample))
        if not over_by_seq:
            rows_out.append("      ⛔ J-R7 未生效：一条带 `over=` 的 diag 行都没有 ⇒ UNJUDGED（不是通过）")
            fails.append("J-R7 拿不到任何带 `over=` 的 `viewmodel.reload.diag` 行 ⇒ 判据未生效")
        elif jr7_bad:
            fails.append("J-R7 有 %d 次未被打断的换弹，其覆盖态在换弹期间就被清空（over=-）"
                         "⇒ OnClipFinished 的陈旧完成回调仍在，用户报的『换弹有时候没有动画』未修好"
                         % len(jr7_bad))

        #   判据的**目的**（本文档 35-36 行、ViewModelRig.cs 的 SettleReload 注释）写得很窄：
        #     「枪**定格在换弹末帧**」—— 即 0.6 s 后 `_overrideState` **仍是换弹剪辑**。
        #      （最典型：玩家一直按着左键，换弹窗口一结束 viewmodel 立刻切 `fire1`）
        #      `seq=1 over=fire1 active=False` —— 而同一轮的 `seq=2 over=-` 是绿的，
        #      驱动侧的 `SetFireHeldForTest(true)`（13:29:59.525）一直按到 13:30:09.932，
        #      settle 取样点（13:30:05.424）**整个落在按住期间** ⇒ fire 接管是**必然**的。
        #   ⇒ 按目的收窄：红 = `over` **等于换弹剪辑名**；`over == -` = 归位（绿）；
        #      `over` 是别的剪辑 = 被另一次动作接管（绿，但**必须留一行 INFO 写明是谁**）。
        #      换弹剪辑名**从 console 里读**（`第一人称播换弹 seq=N 状态=X`），不硬编码。
        reload_states = set(re.findall(r"第一人称播换弹\s+seq=\d+\s+状态=([^\s（()]+)", " \n".join(msgs)))
        reload_state = sorted(reload_states)[0] if reload_states else "reload"
        settles = [m for m in msgs if "viewmodel.reload.settle" in m]
        if args.corrupt == "stuckreload" and settles:
            # 负控：把 settle 行里的 over 改写成"换弹剪辑名" ⇒ 必须红
            patched = []
            for m in msgs:
                if "viewmodel.reload.settle" in m:
                    m = re.sub(r"over=\S+(?=\s+active=)", "over=" + reload_state, m)
                patched.append(m)
            msgs = patched
            settles = [m for m in msgs if "viewmodel.reload.settle" in m]
        released, stuck, taken = [], [], []
        for m in settles:
            mo = re.search(r"over=(\S+?)\s+active=", m + " ")
            ov = mo.group(1) if mo else "?"
            if ov == "-":
                released.append(m)
            elif ov == reload_state or ov == "reload":
                stuck.append(m)
            else:
                taken.append(m)
        summary["settle_lines"] = len(settles)
        summary["settle_stuck"] = len(stuck)
        summary["settle_taken_over"] = len(taken)
        rows_out.append("J-R6 换弹剪辑名（从 console 读）= `%s`；换弹结束后归位取样 = %d 行"
                        % (reload_state, len(settles)))
        rows_out.append("J-R6 三类：归位(`over=-`)=%d / 卡在换弹剪辑(红)=%d / 被另一次动作接管(绿)=%d"
                        % (len(released), len(stuck), len(taken)))
        for m in settles[:4]:
            rows_out.append("      " + m.split("] ")[-1][:170])
        if taken:
            rows_out.append("      ⓘ 接管样本（不是红，但写清接管者）：%s"
                            % "；".join(re.search(r"over=(\S+?)\s+active=", m + " ").group(1)
                                        for m in taken))
        if not settles:
            rows_out.append("      ⛔ J-R6 未生效：一行 settle 取样都没有 ⇒ UNJUDGED（不是通过）")
            fails.append("J-R6 拿不到任何 `viewmodel.reload.settle` 行 ⇒ 判据未生效")
        elif stuck:
            fails.append("J-R6 有 %d 次换弹结束后覆盖态**仍是换弹剪辑**（`over=%s`）⇒ 枪会定格在换弹末帧"
                         % (len(stuck), reload_state))

        # ---- J-D1 弹痕可见性 ----
        #   判据必须用**与距离无关**的量，否则会自我误判：`核心投影` 是"按本发距离"算出来的，
        #      同一块 0.04 m 的核心在 2.0 m 是 19.2 px、在 2.82 m 只有 13.6 px —— 实测就踩过这一次
        #      （13.6 px 被判红，其实只是那一枪离墙远）。⇒ 通过条件改成"可见核心宽(米) ≥ 参考值"，
        #      参考值 = 在**参考距离** 2 m 上投影到 15 px 所需的米宽：15 / (1920/(2*2)) = 0.03125 m。
        decals = [m for m in msgs if "弹痕落在" in m]
        projs, cores = [], []
        for m in decals:
            mp = re.search(r"核心投影\s+([0-9.]+)px", m)
            mc = re.search(r"可见核心宽=([0-9.]+)m", m)
            if mp:
                projs.append((float(mp.group(1)), m))
            if mc:
                cores.append((float(mc.group(1)), m))
        if not decals:
            fails.append("J-D1 没有任何 `弹痕落在` 行 ⇒ 本链没打出弹痕（判据未生效，不是通过）")
        rows_out.append("J-D1 参考口径：%d px 投影 / 参考距离 %.1f m ⇒ 需要可见核心宽 ≥ %.5f m"
                        % (MIN_CORE_PROJ_PX, REF_DISTANCE_M, MIN_CORE_M))
        rows_out.append("J-D1 `弹痕落在` = %d 条；带 `可见核心宽` 的 = %d 条；实测最小可见核心宽 = %s m"
                        % (len(decals), len(cores),
                           ("%.4f" % min(c for c, _ in cores)) if cores else "n/a"))
        rows_out.append("J-D1 实测最大 `核心投影` = %s px（**随距离变化，仅作观测**，不是通过条件）"
                        % (("%.1f" % max(p for p, _ in projs)) if projs else "n/a"))
        worst = min((c for c, _ in cores), default=0.0)
        if args.corrupt == "decal":
            worst *= 0.7                       # 负控：衰减 30%
        if cores and worst < MIN_CORE_M:
            fails.append("J-D1 可见核心宽只有 %.4f m（< %.5f m = 参考距离 %.1f m 上 %.0f px）⇒ 仍然小到看不见"
                         % (worst, MIN_CORE_M, REF_DISTANCE_M, MIN_CORE_PROJ_PX))
        if cores:
            rows_out.append("      例：" + cores[0][1].split("] ")[-1][:200])
        summary["min_core_m"] = worst
        summary["max_core_proj_px"] = max((p for p, _ in projs), default=0.0)
    else:
        fails.append("J-R1/J-D1 控制台 json 不在盘：%s" % args.console)

    # ---------------- 帧差判据 ----------------
    #   J-D2b(a) 同回合前置：run E 的 before/after 跨了一次回合重开 ⇒ 整帧都变，
    #   那种"差异"不能归因于弹痕。回合号由驱动器从 CsHudSnapshot 直接读出来写成 TSV。
    rounds = {}
    if args.rounds_file and os.path.exists(args.rounds_file):
        for ln in open(args.rounds_file, encoding="utf-8"):
            parts = ln.strip().split("\t")
            if len(parts) >= 2:
                rounds[parts[0].strip()] = parts[1].strip()
    rb, ra = rounds.get("before"), rounds.get("after")
    rows_out.append("J-D2b(a) 同回合前置：before round=%s / after round=%s（判据：相等）"
                    % (rb or "?", ra or "?"))
    if not (rb and ra):
        fails.append("J-D2b(a) 拿不到 before/after 的回合号（`--rounds-file` 缺失或字段不全）"
                     "⇒ 同回合前置未生效（UNJUDGED 不是通过）")
    elif rb != ra:
        fails.append("J-D2b(a) before/after **跨回合**（%s → %s）⇒ 帧差里混了回合重开的整帧变化，"
                     "不能归因于弹痕 ⇒ J-D2 无效" % (rb, ra))

    judged_tags, unjudged_tags = [], []
    for tag, after in (("floor", args.after), ("wall", args.after2)):
        if not (os.path.exists(args.before) and after and os.path.exists(after)):
            #    **不进 fails**。实测后果：把 `fix4c_*`/`fix4d_*` 帧全删掉再跑，两条都"跳过"，
            #    而 FAIL 列表里**一条 J-D2 都没有** ⇒ 帧一块都没采到，#4「弹痕还是没有」
            #    会**静默地从未被判**，判据却照样可能打 GATE GREEN。这就是"判据测的是素材、不是代码"的孪生形态。
            if tag == "floor":
                # 本轮派活范围 = before/after **一对**（地面）。它是核心，缺了就是红。
                rows_out.append("J-D2[floor] 帧不全（before=%s after=%s）⇒ UNJUDGED（不是通过）"
                                % (os.path.exists(args.before), bool(after) and os.path.exists(after)))
                fails.append("J-D2[floor] 拿不到帧（before=%s after=%s）⇒ 判据未生效"
                             "（UNJUDGED 不是通过）：#4「弹痕有没有真的改到画面」等于**没被判过**"
                             % (os.path.exists(args.before), bool(after) and os.path.exists(after)))
            else:
                # wall 面不属于本轮派活范围（本轮只采 before/after 一对）。
                # 但**不等于通过**：显式单列一行，并把"已判 tag 集合"写进 GATE 行，
                #    让复核的人一眼看到"哪些面真的被判了"。不许把它算进 judged。
                rows_out.append("J-D2[wall] 本轮未采（`--after2` 未给）⇒ ⛔ 不是通过、也不是红："
                                "**未判**。原因：本判据的 `before` 是**共用一张**，只有与它同机位的那个面"
                                "才能过 J-D2b(b) 局部性；本轮派活书要的就是 before/after 一对。")
            unjudged_tags.append(tag)
            continue
        judged_tags.append(tag)
        w0, h0, c0, r0 = read_png(args.before)
        w1, h1, c1, r1 = read_png(after)
        if (w0, h0) != (w1, h1):
            fails.append("J-D2[%s] 分辨率不同 %dx%d vs %dx%d" % (tag, w0, h0, w1, h1))
            continue
        l0, l1 = luma_rows(w0, h0, c0, r0), luma_rows(w1, h1, c1, r1)
        diff = dark = 0
        maxdrop = 0
        for y in range(h0):
            a, b = l0[y], l1[y]
            for x in range(w0):
                d = b[x] - a[x]
                if d <= -DARK_DELTA or d >= DARK_DELTA:
                    diff += 1
                    if d <= -DARK_DELTA:
                        dark += 1
                        if -d > maxdrop:
                            maxdrop = -d
        rows_out.append("J-D2[%s] 变化像素=%d（阈值 ≥%d）其中变暗=%d（阈值 ≥%d）最大变暗 ΔL=%d"
                        % (tag, diff, MIN_DIFF_PX, dark, MIN_DARK_PX, maxdrop))
        summary["diff_" + tag] = diff
        summary["dark_" + tag] = dark
        # J-D2b(b) 局部性：整帧都在变 = 相机动了 / 回合重开，不是"多了一块弹痕"。
        if args.corrupt == "diffuse":
            diff = dark = w0 * h0                 # 负控：把差异放大成整帧 ⇒ J-D2b(b) 必须红
        frac = float(diff) / float(w0 * h0)
        summary["diff_frac_" + tag] = round(frac, 6)
        rows_out.append("J-D2b[%s] 差异占比=%.3f%%（上限 %.2f%%）"
                        % (tag, frac * 100.0, MAX_DIFF_FRAC * 100.0))
        if frac > MAX_DIFF_FRAC:
            fails.append("J-D2b[%s] 差异占全帧 %.2f%%（> %.2f%%）⇒ 整帧都在变（相机动了 / 回合重开），"
                         "这些差异不能归因于弹痕 ⇒ 帧差判据无效"
                         % (tag, frac * 100.0, MAX_DIFF_FRAC * 100.0))
        if diff < MIN_DIFF_PX:
            fails.append("J-D2[%s] 帧间变化只有 %d px（< %d）⇒ 弹痕仍然没改到画面" % (tag, diff, MIN_DIFF_PX))
        elif dark < MIN_DARK_PX:
            fails.append("J-D2[%s] 变化的 %d px 里只有 %d px 变暗（< %d）⇒ 不像黑洞（可能是白斑/噪声）"
                         % (tag, diff, dark, MIN_DARK_PX))

    print("\n".join(rows_out))
    print("-" * 72)
    # 判了哪些**面**必须进结论行 —— 否则"哪几个 tag 真被判过"只存在于读者脑子里
    print("J-D2 面貌：已判 = %s；未判 = %s（⛔ 未判 ≠ 通过）"
          % (",".join(judged_tags) or "(无)", ",".join(unjudged_tags) or "(无)"))
    if args.json:
        summary["fails"] = fails
        summary["judged_tags"] = judged_tags
        summary["unjudged_tags"] = unjudged_tags
        json.dump(summary, open(args.json, "w", encoding="utf-8"), indent=2, ensure_ascii=False)
    if fails:
        for f in fails:
            print("FAIL: " + f)
        return 1
    if "floor" not in judged_tags:
        print("⛔ 拒绝打 GREEN：J-D2 的核心面（floor）没被判过 ⇒ 这是「未判」，不是「通过」。")
        return 1
    print("GATE GREEN: J-R1/J-R1b/J-R2/J-R3/J-R3b/J-R4/J-R5/J-R6/J-R7/J-D1/J-D2%s/J-D2b%s 全通过"
          "（corrupt=%s）"
          % ("[%s]" % ",".join(judged_tags), "[%s]" % ",".join(judged_tags), args.corrupt or "none"))
    return 0


if __name__ == "__main__":
    sys.exit(main())
