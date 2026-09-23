# -*- coding: utf-8 -*-
"""判据资产（tools/probes/）：16 流程态 × 16 面板 的「期望可见 vs 实际可见」对账。

为什么要有它（片BV-R 第 3 件事的判据）：
  用户原话「选阵营看地图时候，还有 ui 控件」只有两类解释：编辑器叠加层（片BV 已 A/B 证死）
  或**真实 UI 节点残留**（该隐藏的面板仍然 active）。后者只能靠运行时节点树的
  `activeInHierarchy` 判 —— 截图看不出空面板、源码里 grep 到 `Close<T>()` 也不等于运行时就关了。

判据的两边（⛔ 都不是手写的"我觉得"）：
  * 实际 = `.ai-tmp/test/bvr-ui-visible.tsv`（`probe-ui-visibility.cs` 逐态窗口原文；
    `#panel <label> <Type> <path> active=True/False ...`）；
  * 期望 = 下面 EXPECTED 表，**每一行都带源码锚点**（file:line + 该行必须出现的 needle）。
    脚本会先把每个锚点回读校验（锚点漂移 ⇒ 直接 FAIL，而不是"偷偷用错表"）。

用法：
  python tools/probes/check-ui-flow-matrix.py [--tsv <运行时TSV>] [--out <矩阵TSV>]
"""
import os
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

# 期望表：态 -> [(面板, 源码锚点 file:line:needle)]
# 每一条的出处 = 业务真正把该面板打开的**那一个调用行**（FSM 站点 = AppFlow 的 OnEnter*，
# 游戏内 = HudPanel 的按键路由 / 事件回调）。
EXPECTED = {
    'Boot': [('BootPanel', 'client/Assets/Scripts/Module/Flow/AppFlow.cs:173:Open<BootPanel>')],
    'MainMenu': [('MainMenuPanel', 'client/Assets/Scripts/Module/Flow/AppFlow.cs:266:Open<MainMenuPanel>')],
    'ServerList': [('ServerListPanel', 'client/Assets/Scripts/Module/Flow/AppFlow.cs:315:Open<ServerListPanel>')],
    'NewGame': [('NewGamePanel', 'client/Assets/Scripts/Module/Flow/AppFlow.cs:333:Open<NewGamePanel>')],
    'Options': [('OptionsPanel', 'client/Assets/Scripts/Module/Flow/AppFlow.cs:350:Open<OptionsPanel>')],
    'Loading': [('LoadingPanel', 'client/Assets/Scripts/Module/Flow/AppFlow.cs:416:Open<LoadingPanel>')],
    'TeamSelect': [('TeamSelectPanel', 'client/Assets/Scripts/Module/Flow/AppFlow.cs:507:Open<TeamSelectPanel>')],
    'Stage': [('HudPanel', 'client/Assets/Scripts/Module/Flow/AppFlow.cs:553:Open<HudPanel>')],
    # Pause 从 Stage 进（HudPanel 不关，AppFlow.OnEnterStage 没有配 OnExit）⇒ 期望 = HUD + 暂停框
    'Pause': [('HudPanel', 'client/Assets/Scripts/Module/Flow/AppFlow.cs:553:Open<HudPanel>'),
              ('PausePanel', 'client/Assets/Scripts/Module/Flow/AppFlow.cs:565:Open<PausePanel>')],
    'BuyMenu': [('HudPanel', 'client/Assets/Scripts/Module/Flow/AppFlow.cs:553:Open<HudPanel>'),
                ('BuyMenuPanel', 'client/Assets/Scripts/UI/InGame/HudPanel.cs:1019:Open<BuyMenuPanel>')],
    'HMenu': [('HudPanel', 'client/Assets/Scripts/Module/Flow/AppFlow.cs:553:Open<HudPanel>'),
              ('HMenuPanel', 'client/Assets/Scripts/UI/InGame/HudPanel.cs:1024:Open<HMenuPanel>')],
    'RadioMenu': [('HudPanel', 'client/Assets/Scripts/Module/Flow/AppFlow.cs:553:Open<HudPanel>'),
                  ('RadioMenuPanel', 'client/Assets/Scripts/UI/InGame/HudPanel.cs:1028:Open<RadioMenuPanel>')],
    'Console': [('HudPanel', 'client/Assets/Scripts/Module/Flow/AppFlow.cs:553:Open<HudPanel>'),
                ('ConsolePanel', 'client/Assets/Scripts/UI/InGame/HudPanel.cs:997:Open<ConsolePanel>')],
    'Scoreboard': [('HudPanel', 'client/Assets/Scripts/Module/Flow/AppFlow.cs:553:Open<HudPanel>'),
                   ('ScoreboardPanel', 'client/Assets/Scripts/UI/InGame/HudPanel.cs:1006:Open<ScoreboardPanel>')],
    'RoundEnd': [('HudPanel', 'client/Assets/Scripts/Module/Flow/AppFlow.cs:553:Open<HudPanel>'),
                 ('RoundEndPanel', 'client/Assets/Scripts/UI/InGame/HudPanel.cs:1097:Open<RoundEndPanel>')],
    'MatchEnd': [('HudPanel', 'client/Assets/Scripts/Module/Flow/AppFlow.cs:553:Open<HudPanel>'),
                 ('MatchEndPanel', 'client/Assets/Scripts/UI/InGame/HudPanel.cs:1107:Open<MatchEndPanel>')],
    # 观察者没有独立面板（原版也只是 HUD + 去掉名牌）⇒ 期望 = 仅 HUD，出处 = 阻塞式浮层清单里没有它
    'Spectate': [('HudPanel', 'client/Assets/Scripts/UI/InGame/HudPanel.cs:1053:IsOpen<MatchEndPanel>')],
}

# 驱动里写的 label -> 矩阵里的态（同一态多采几次是因为 Loading 只有秒级窗口）
LABEL_TO_STATE = {
    'Boot': 'Boot', 'MainMenu': 'MainMenu', 'ServerList': 'ServerList', 'NewGame': 'NewGame',
    'Options': 'Options',
    'Loading1': 'Loading', 'Loading2': 'Loading', 'Loading3': 'Loading',
    'TeamSelect': 'TeamSelect', 'TeamSelect2': 'TeamSelect',
    'Stage': 'Stage', 'BuyMenu': 'BuyMenu', 'HMenu': 'HMenu', 'RadioMenu': 'RadioMenu',
    'Console': 'Console', 'Scoreboard': 'Scoreboard', 'Pause': 'Pause',
    'RoundEnd': 'RoundEnd', 'MatchEnd': 'MatchEnd', 'Spectate': 'Spectate',
}
# 选哪一个 label 作为该态的"实际"（多采时优先列表前面的；Loading 另按 fsm 过滤）
PREFERRED = {
    'Boot': ['BootEarly', 'BootLate'],
    'Loading': ['Loading0', 'Loading1', 'Loading2', 'Loading3'],
    'TeamSelect': ['TeamSelect', 'TeamSelect2'],
    'Scoreboard': ['ScoreboardTabHeld', 'Scoreboard'],
}

# 这些态是 FSM 站点：采样那一帧的 fsm 必须**正好等于**态名，否则说明窗口没赶上
# （实测：片BV-R 第一次采 Boot 时那帧 fsm=MainMenu ⇒ 整段判"未采到"，⛔ 不许当成 UI 缺陷）
FSM_STATES = {'Boot', 'MainMenu', 'ServerList', 'NewGame', 'Options', 'Loading',
              'TeamSelect', 'Stage', 'Pause'}


def check_anchors():
    """回读每个期望行的源码锚点；漂移 ⇒ 返回错误清单。"""
    bad = []
    for state, rows in EXPECTED.items():
        for panel, cite in rows:
            path, line, needle = cite.split(':', 2)
            full = os.path.join(ROOT, path.replace('/', os.sep))
            if not os.path.exists(full):
                bad.append('%s %s anchor file missing: %s' % (state, panel, path))
                continue
            with open(full, 'rb') as f:
                lines = f.read().decode('utf-8', 'replace').replace('\r\n', '\n').split('\n')
            n = int(line)
            if n - 1 >= len(lines) or needle not in lines[n - 1]:
                bad.append('%s %s anchor drifted: %s:%s does not contain %r' % (state, panel, path, line, needle))
    return bad


def read_runtime(tsv):
    """-> [(label, [dump_meta]), ...] 按出现顺序；每个 dump 段内含 panel 行。"""
    dumps = []
    cur = None
    with open(tsv, 'rb') as f:
        text = f.read().decode('utf-8', 'replace').replace('\r\n', '\n')
    for line in text.split('\n'):
        if not line.strip():
            continue
        p = line.split('\t')
        if p[0] == '#dump':
            meta = {}
            for kv in p[2:]:
                if '=' in kv:
                    k, v = kv.split('=', 1)
                    meta[k] = v
            cur = {'label': p[1], 'meta': meta, 'panels': {}, 'uiroots': []}
            dumps.append(cur)
        elif p[0] == '#panel' and cur is not None:
            d = {}
            for kv in p[4:]:
                if '=' in kv:
                    k, v = kv.split('=', 1)
                    d[k] = v
            cur['panels'][p[2]] = {
                'path': p[3],
                'active': d.get('active') == 'True',
                'activeInstances': d.get('activeInstances'),
                'instances': d.get('instances'),
            }
        elif p[0] == '#uiroot' and cur is not None:
            cur['uiroots'].append((p[2], p[3]))
    return dumps


def pick(dumps, state):
    """按 PREFERRED 选该态的 dump；Loading 额外要求 fsm==Loading。"""
    labels = PREFERRED.get(state, [state])
    cands = [d for d in dumps if d['label'] in labels]
    if state == 'Loading':
        cands = [d for d in cands if d['meta'].get('fsm') == 'Loading']
    if not cands:
        return None
    cands.sort(key=lambda d: labels.index(d['label']))
    return cands[0]


def main():
    args = sys.argv[1:]
    tsv = os.path.join(ROOT, '.ai-tmp', 'test', 'bvr-ui-visible.tsv')
    out = os.path.join(ROOT, '.ai-tmp', 'test', 'bvr-ui-flow-matrix.tsv')
    i = 0
    while i < len(args):
        if args[i] == '--tsv':
            tsv = args[i + 1]; i += 2
        elif args[i] == '--out':
            out = args[i + 1]; i += 2
        else:
            i += 1

    bad = check_anchors()
    if bad:
        print('ANCHOR CHECK FAILED:')
        for b in bad:
            print('  ' + b)
        return 1
    print('anchor check: %d expected row(s), every source anchor resolves' %
          sum(len(v) for v in EXPECTED.values()))

    dumps = read_runtime(tsv)
    all_panels = sorted({p for d in dumps for p in d['panels']})
    print('runtime dumps: %d, panel types seen: %d' % (len(dumps), len(all_panels)))

    rows = []
    extra_rows = []
    missing_rows = []
    notcaptured = 0
    for state in EXPECTED:
        exp = {p for p, _ in EXPECTED[state]}
        d = pick(dumps, state)
        if d is None:
            for p in all_panels:
                rows.append((state, p, '<no dump>', 'visible' if p in exp else 'hidden', '<not captured>', '未采到'))
            notcaptured += len(all_panels)
            print('  !! state %s: no dump matched (captured labels: %s)' %
                  (state, ','.join(sorted({x['label'] for x in dumps}))))
            continue
        fsm = d['meta'].get('fsm', '?')
        stale = state in FSM_STATES and fsm != state
        if stale:
            print('  !! state %s: sampled frame had fsm=%s -> whole dump judged 未采到' % (state, fsm))
        for p in all_panels:
            info = d['panels'].get(p)
            if info is None:
                continue
            actual = info['active']
            want = p in exp
            if stale:
                verdict = '未采到(采样时刻 fsm=%s)' % fsm
                notcaptured += 1
            elif info['instances'] == '0' and not want:
                # 该态下这个面板**根本没被实例化** ⇒ 它当然不可见，判 OK（不是"没采到"）
                verdict = 'OK'
            elif info['instances'] == '0' and want:
                verdict = '该显示却没显示(未实例化)'
                missing_rows.append((state, p, info['path']))
            elif want == actual:
                verdict = 'OK'
            elif want and not actual:
                verdict = '该显示却没显示'
                missing_rows.append((state, p, info['path']))
            else:
                verdict = '不该显示却显示'
                extra_rows.append((state, p, info['path']))
            rows.append((state, p, info['path'],
                         'visible' if want else 'hidden',
                         'visible' if actual else 'hidden',
                         verdict))

    with open(out, 'wb') as f:
        head = '态\t面板\t节点路径\t期望\t实际\t判定\tfsm(运行时)\n'
        body = ''
        for r in rows:
            d = pick(dumps, r[0])
            fsm = d['meta'].get('fsm', '?') if d else '?'
            body += '\t'.join(list(r) + [fsm]) + '\n'
        f.write((head + body).encode('utf-8'))

    print('matrix -> %s  (%d rows)' % (out, len(rows)))
    ok_n = len(rows) - len(extra_rows) - len(missing_rows) - notcaptured
    print('verdicts: OK=%d  expected-but-hidden=%d  visible-but-unexpected=%d  not-captured=%d' %
          (ok_n, len(missing_rows), len(extra_rows), notcaptured))
    if missing_rows:
        print('-- expected-but-hidden --')
        for r in missing_rows:
            print('   %s / %s / %s' % r)
    if extra_rows:
        print('-- visible-but-unexpected --')
        for r in extra_rows:
            print('   %s / %s / %s' % r)
    # 观察者/游戏内态额外给出"屏上 UI 根"（= 用户说的"ui 控件"）
    for state in ('TeamSelect', 'Stage', 'Spectate'):
        d = pick(dumps, state)
        if d is None:
            continue
        act = [p for p in d['uiroots'] if 'activeInHierarchy=True' in p[1]]
        print('%s: active UI roots on screen = %s' % (state, '; '.join(a[0] for a in act) or '(none)'))
    return 0


if __name__ == '__main__':
    sys.exit(main())
