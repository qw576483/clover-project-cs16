# -*- coding: utf-8 -*-
"""bwp-input-scan.py -- D11（输入，28 行）的**静态探针（真实版）**：产出可被闸门收下的 ledger 行。

⚠️ 本轮只能落在 `.ai-tmp/test/`（`tools/**` 在 audit-gate 终局窗口内冻结）；放行后原样复制到
   `tools/probes/bwp-input-scan.py`（判据资产），ledger 落 `tools/probes/bwp-input-hits.tsv`
   （或 `.ai-tmp/test/bwp-input-hits.tsv`）。

**契约（逐字照抄闸门 `coverage-hit` 打印的 carrier contract；缺一件就不计命中）**
  1. 真 ledger 首行：`# probe-hits plan=<plan 目录>`（且**文件名含 `hits`**、落在探针根下）；
  2. 每行 `<rowId>\\t<anchor>\\t…`，且**字段不得是判定行自己单元格的复述**
     （字段 ≥2 个与行单元格逐字相同 且 占比 ≥60% ⇒ 判**表格回声**、不计命中）；
  3. ⚠️ **行为类维度（D6,D7,D9,D10,D11,D12,S2,S3）必须带 `probe=<name>` + `measured=`（或 `run=`）**
     —— `unresolved=1` 是"自认没解析出东西"，**不算命中**。
  ⇒ 本脚本写的就是"探针**真看到的东西**"：抽到的 `GameKey` token、它在哪、该行 sha1、经没经过辅助函数。

**规则（主 agent 裁定 + 本片实测）**
  * 被引行（含 ±3 窗口）里没有字面 `GameKey.<X>` ⇒ **跟着辅助函数多读一步**：该行调了 `NumberKey(i)`，
    就在同一个文件里找它的定义（`:160-173` → `case 0: return GameKey.Num1;` …）⇒ 抽定义体里的 token。
    （实测：`3375 数字键@无线电菜单` 就是这样才判得了；不做这一步会**假弱**。）
    ⚠️ **边界（主 agent 裁定）**：**显式一层为限** —— 前提 = ① **同文件**、② **符号名出现在被引行上**
    （⛔ 不拿 ±3 相邻行的调用去跟）、③ 该符号是同文件里返回 `GameKey`/`KeyCode` 的定义；
    定义体里若还有键调用**不再往下跟**。ledger 里 `via=helper:<name>@<range>` 必须保留 ⇒
    "跟了（推出来的）"与"直接看到的"在证据里可分辨（将来复盘的人知道这一步是**推出来的**）。
  * `probe=` 写明 `bwp-input-scan(static)` —— **静态探针不许装成运行时读数**。
  * ⛔ 不复述实体名/结论串；⛔ 只写 `.ai-tmp/test/**`。

用法：
  python .ai-tmp\\test\\bwp-input-scan.py --selftest     # 沙盒 plan + 沙盒 ledger（⛔ 不污染真判据）
  python .ai-tmp\\test\\bwp-input-scan.py                # 用默认沙盒参数写 ledger + 自检
  python .ai-tmp\\test\\bwp-input-scan.py --real         # ⛔ 放行后：ledger 落 tools/probes/bwp-input-hits.tsv
  python .ai-tmp\\test\\bwp-input-scan.py --hits-out=<path>
                                                        # L2.1 一级「对照重跑」：真 plan + 产物落别处（⛔ 不碰真产物）
                                                        # ⚠️ <path> 的名字**不许含 `hits`**（否则它自己成了第二个 carrier）
                                                        # ⛔ 该自拒只是【调用面自拒】（只覆盖这一条路径），
                                                        #    **不是判据级防线**，不能替代"登记制"加固项（判据拥有者的活）
  python .ai-tmp\\test\\bwp-input-scan.py --selftest --demo-violation=<rowId>
                                                        # 自拒锁的**反样本入口**（去--demo才写盘）
"""
import hashlib
import importlib.util
import io
import os
import re
import shutil
import sys

sys.stdout.reconfigure(encoding='utf-8', errors='replace')
sys.dont_write_bytecode = True
HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
PLAN = os.path.join(ROOT, '\u7b56\u5212')
FRAGMENT = os.path.join(PLAN, '\u8986\u76d6\u77e9\u9635\u5224\u5b9a.fragment.md')
SANDBOX = os.path.join(HERE, 'bwp-scan-selftest')
SB_PLAN = os.path.join(SANDBOX, 'plan')                 # ⛔ 沙盒 plan：与真 策划/ 不同 ⇒ 真判据不收
SB_LEDGER = os.path.join(SANDBOX, 'bwp-input-hits.tsv')  # 名字含 `hits`（沙盒内自证契约用）
_SITE_RE = re.compile(r'\u7ed1\u5b9a\u70b9\s+([0-9A-Za-z_\-./\\]+\.cs):(\d+)')
_GK_RE = re.compile(r'GameKey\.([A-Za-z0-9_]+)')
_CALL_RE = re.compile(r'(?<![\w.])([A-Za-z_][A-Za-z0-9_]*)\s*\(')
_NOT_HELPER = set('if for while switch return using lock nameof typeof catch new'.split())


def load_avr():
    """真判据：⛔ 不把阈值抄成第二份。"""
    p = os.path.join(ROOT, 'tools', 'probes', 'audit-verdict-rows.py')
    spec = importlib.util.spec_from_file_location('bwp_avr4', p)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def d11_rows():
    rows = []
    for ln in io.open(FRAGMENT, encoding='utf-8', errors='replace').read().split('\n'):
        m = re.match(r'^\|\s*(\d+)\s*\|', ln)
        if not m:
            continue
        c = [x.strip() for x in ln.split('|')]
        if len(c) > 6 and c[2] == 'D11':
            rows.append([c[1], c[2], c[3], c[4], c[5], c[6]])
    return rows


def resolve_cited(rel):
    full = os.path.join(ROOT, rel.replace('/', os.sep))
    if os.path.isfile(full):
        return full
    for pre in (os.path.join('client', 'Assets', 'Scripts'), os.path.join('client', 'Assets')):
        p = os.path.join(ROOT, pre, rel.replace('/', os.sep))
        if os.path.isfile(p):
            return p
    return None


def helper_tokens(lines, idx):
    """被引行 ±3 没有字面 `GameKey` ⇒ 跟着辅助函数**多读一步**（主 agent 裁定：显式一层为限）。

    跟的**三个前提**（⛔ 缺一个就不跟，防止从"太严"翻成"太宽"）：
      1. **同文件** —— 不跨文件、不跟外部类；
      2. **符号名出现在被引行上** —— 只认 `lines[idx]` 这一行里调用的名字，
         ⛔ 不拿相邻行（±3 窗口）的调用去跟；
      3. **该符号是同文件里返回 `GameKey`/`KeyCode` 的定义**（`def_re`）⇒ 不是随便一个同名函数。
    ⛔ **只跟一层**：定义体里若还有 `GameKey.Xxx()` 这类调用，**不再往下跟**。
    ⚠️ 返回值里必带 `via=helper:<name>@<range>` ⇒ 让"跟了（推出来的）"与"直接看到的"在证据里可分辨。
    """
    names = []
    for n in _CALL_RE.findall(lines[idx]):          # 前提 2：只认被引行本身
        if n not in _NOT_HELPER and n not in names:
            names.append(n)
    for n in names:
        def_re = re.compile(r'\b(?:private|public|internal|protected|static|\s)*'
                            r'(?:GameKey|KeyCode)\s+' + re.escape(n) + r'\s*\(')
        for j, x in enumerate(lines):
            if def_re.search(x):                     # 前提 1（同文件）+ 前提 3（返回键）
                body = lines[j:min(len(lines), j + 14)]
                toks = sorted({t for b in body for t in _GK_RE.findall(b)})
                if toks:
                    return toks, 'helper:%s@%d-%d' % (n, j + 1, min(len(lines), j + 14))
    return [], 'none'


# ⛔ 落地时**不许**被写出的行（主 agent L3 判据）：`3372/3373/3376` 证据列无绑定点、`3377` 引用指错对象。
#    出现即说明探针在放宽 / 在凑数 ⇒ 本脚本**自己拒发**（不是靠人事后检查）。
FORBIDDEN_ROWS = {'3372', '3373', '3376', '3377'}
# ⛔ 自拒这把锁也必须**双向可证**（同闸门 `window-ledger-check` 的纪律：该静默时静默、该出声时出声）：
#    `--demo-violation=<一个真能解析出来的 rowId>` 把它临时算作禁用行 ⇒ **必须**打印 CONTRACT-VIOLATION 且不发该行。
#    只用来自证"锁会响"，⛔ 不改变 24 行的正常输出。
#
# ⚠️ **这个 seam 的性质（主 agent 2026-09-23 要求写清）**：
#    `--demo-violation` 是**「自拒锁的反样本入口」，不是功能开关**，⛔ 不许为了"干净"删掉（删了锁就不可证）。
#    它注入的反样本是**构造的、不是历史缺陷** —— 与"反样本优先取真实缺陷"（§4.3.4 第 2 条）的区别正在这里：
#    这类"拒绝/停手"防线**没有历史先例可引** ⇒ 构造反样本合法，但必须**标明是构造的**，让后人分得清两类反样本。
# ⚠️ **演示产物自带毒 ⇒ 必须自报毒**（今晚已出过"沙盒 ledger 差点被收"）：
#    ① 开头打 `!! DEMO MODE - this ledger is NOT a deliverable`；
#    ② **演示模式一律不落盘**（`out_ledger = None`）—— 哪怕沙盒路径也不写，只写 stdout
#       （比"禁止落真 ledger 路径"更强：脏产物天生不该有文件形态）。
_DEMO = [a.split('=', 1)[1] for a in sys.argv if a.startswith('--demo-violation=')]


def main():
    avr = load_avr()
    ts = '--selftest' in sys.argv
    hits_out = [a.split('=', 1)[1] for a in sys.argv if a.startswith('--hits-out=')]
    # ⛔ 只在放行后跑；写 tools/probes/（判据资产）
    real = ('--real' in sys.argv) or bool(hits_out)
    if _DEMO:
        # ⚠️ 自报毒（要求 ②）：演示模式的产物**只该存在于 stdout**，⛔ 不许有任何文件形态。
        print('!! DEMO MODE - this ledger is NOT a deliverable')
        print('!! (自拒锁的反样本入口；注入项是**构造的**，不是历史缺陷；本模式一律不落盘)')
    canon = os.path.join(ROOT, 'tools', 'probes', 'bwp-input-hits.tsv')
    out_ledger = (canon if real
                  else SB_LEDGER if ts else os.path.join(HERE, 'bwp-input-sandbox-ledger.tsv'))
    if hits_out:
        # `--hits-out=<path>` = **L2.1 一级（对照重跑）** 的 seam：声明**真 plan**、但产物落到别处
        #   ⇒ 与真产物比哈希即可证"确定性与新鲜度"（磁盘上那份 == 现在从源跑出来的那份），⛔ 不碰真产物。
        # ⚠️ **本片实测出的单点风险（主 agent 已登记为收尾后加固项）**：carrier 三条件里
        #   "**文件名含 `hits`**"是**唯一在承重**的一层（对照产物的首行 / plan 绑定**全是对的**）
        #   ⇒ 所以这里**自拒**：名字含 `hits` 的对照产物 = 立刻会被真判据收成第二个 carrier。
        #   ⛔ 不改判据（那是 audit-gate 的活）；只在**我这份探针**里把命名约定变成**会响的锁**。
        #
        # ⛔⛔ **这把锁的定位（主 agent 2026-09-23 要求写明，防后人误读）**：
        #   这是「**调用面自拒**」—— 只覆盖 `--hits-out` 这**一条路径**，**⛔ 不是判据级防线**。
        #   ⇒ **它不能替代"登记制"加固项**（"只有被探针/生成器自己声明过的 carrier 名才会被收"
        #     仍留给**判据拥有者**，收尾后做，且必须配两次自检）。
        #   ⇒ 读到这把锁的人**不许**以为"命名单点已经被彻底修好" —— 那正是登记它的原因。
        #   规矩（主 agent 通用做法第 7 条）：**"自我约束"（我方不再产生脏东西）谁都能做、不需授权；
        #     "改判据"（改变"收什么"）只有拥有者能做**。两件事不是一件。
        _base = os.path.basename(hits_out[0]).lower()
        if 'hits' in _base:
            print('REFUSED: --hits-out 的产物名含 "hits"（%s）' % _base)
            print('  理由：其首行是真 "# probe-hits plan=<真 策划>"、plan 绑定也正确 ⇒ 它会立刻被真判据')
            print('  收成**第二个 carrier**；挡它的只有文件名 ⇒ 对照产物必须改名（不含 hits）后重试。')
            return 2
        out_ledger = hits_out[0]
    if _DEMO:
        out_ledger = None                             # ② 更强的一条：连沙盒路径也不写
    plan_decl = PLAN if real else (SB_PLAN if ts else os.path.join(HERE, 'bwp-demo-plan-sandbox'))
    if real:
        # 唯一正解落点（主 agent 裁决）：ledger=判据资产，必须与 `coverage-hits.tsv` 同处 `tools/probes/`。
        # ⛔ 不落 `.ai-tmp/**` —— 那里会随收尾清理 ⇒ 判据失去载体（`coverage-hit` 会莫名回红）。
        if os.path.normcase(os.path.abspath(out_ledger)) == os.path.normcase(os.path.abspath(canon)):
            print('REAL LANDING: ledger -> tools/probes/bwp-input-hits.tsv ; plan -> ' + plan_decl)
            print('REMINDER: 开窗（.ai-tmp/test/gate-selftest-window.tsv 追 start/end 两行，同一命令内相邻）')
        else:
            print('REGEN COMPARE (L2.1 tier-1 对照重跑): 真 plan -> ' + plan_decl)
            print('  对照产物 -> ' + out_ledger + '   (⛔ 不碰真产物；改完与真产物比哈希)')
    if ts:
        os.makedirs(SB_PLAN, exist_ok=True)
        shutil.copyfile(FRAGMENT, os.path.join(SB_PLAN, os.path.basename(FRAGMENT)))
        copy_acc = os.path.join(PLAN, '\u9a8c\u6536\u8868.md')
        if os.path.isfile(copy_acc):
            shutil.copyfile(copy_acc, os.path.join(SB_PLAN, os.path.basename(copy_acc)))
        print('SANDBOX plan dir prepared (a COPY, the real 策划/ is never written): ' + SB_PLAN)
    print('INPUT fragment = ' + FRAGMENT)
    print('OUT   ledger   = ' + (out_ledger if out_ledger else '<stdout only -- DEMO MODE refuses to write>'))
    print('DECL  plan     = ' + plan_decl)

    rows = d11_rows()
    cell_by_id = {r[0]: r for r in rows}
    lines_out = ['# probe-hits plan=' + plan_decl]
    stat = {'hit': 0, 'ok': 0, 'no-site': 0, 'no-token': 0, 'refused': 0, 'violation': 0}
    echo_max = 0.0
    for rid, dim, ent, state, verdict, ev in rows:
        forbidden = rid in FORBIDDEN_ROWS or rid in _DEMO
        m = _SITE_RE.search(ev)
        if not m:
            stat['no-site'] += 1
            if forbidden:
                stat['refused'] += 1
                print('  %-5s REFUSED-BY-CONTRACT  %s 证据列无绑定点（应然，不写）' % (rid, ent[:28]))
            continue
        rel, no = m.group(1), int(m.group(2))
        full = resolve_cited(rel)
        if not full:
            stat['no-token'] += 1
            continue
        src = io.open(full, encoding='utf-8', errors='replace').read().split('\n')
        idx = no - 1
        if idx >= len(src):
            stat['no-token'] += 1
            continue
        lo, hi = max(0, idx - 3), min(len(src), idx + 4)
        toks = sorted({t for x in src[lo:hi] for t in _GK_RE.findall(x)})
        via = 'direct'
        if not toks:
            toks, via = helper_tokens(src, idx)
        if not toks:
            stat['no-token'] += 1
            if forbidden:
                stat['refused'] += 1
                print('  %-5s REFUSED-BY-CONTRACT  %-28s %s:%d 无 token（应然，不写；见 §5.0）'
                      % (rid, ent[:28], rel, no))
            else:
                print('  %-5s NO-TOKEN  %-34s %s:%d (+-3 + helper)' % (rid, ent[:34], rel, no))
            continue
        if forbidden:
            # ⛔ 自拒（L3 判据落在工具里，不靠人事后看）：这一行本来就"写不出命中"
            #    （`3372/3373/3376` 无绑定点、`3377` 引用指错对象）⇒ 竟然抽出 token 才是问题。
            stat['violation'] += 1
            print('  %-5s !!CONTRACT-VIOLATION  %s 抽出 toks=%s ⇒ 停手查因（口径被放宽/凑数）'
                  % (rid, ent[:28], ','.join(toks)))
            continue
        sha = hashlib.sha1(src[idx].strip().encode('utf-8')).hexdigest()[:12]
        key = ent.split('@')[0]
        line = ('%s\tF1:%s:%d\tprobe=bwp-input-scan(static) measured=gamekey_tokens=%s '
                'via=%s line_sha1=%s key=%s'
                % (rid, rel, no, ','.join('GameKey.' + t for t in toks), via, sha, key))
        ok, why, lying = avr.hit_quality('D11', line)   # 真判据返回三元组 (ok, why, lying)
        ech = avr.table_echo(cell_by_id[rid][1:], line)
        echo_max = max(echo_max, ech)
        stat['hit'] += 1
        stat['ok'] += (1 if ok else 0)
        print('  %-5s HIT       %-34s %s:%d  toks=%s via=%s  hit_quality=%s echo=%.2f'
              % (rid, ent[:34], rel, no, ','.join(toks), via, why, ech))
        lines_out.append(line)

    if out_ledger is None:
        print('')
        print('--- ledger lines (stdout only; DEMO MODE writes NO file) ---')
        for ln in lines_out:
            print(ln)
    else:
        with io.open(out_ledger, 'w', encoding='utf-8', newline='\n') as f:
            f.write('\n'.join(lines_out) + '\n')
    print('')
    print('D11 rows = %d ; ledger lines = %d (hit_quality=ok %d) ; no-site=%d no-token=%d '
          'refused-by-contract=%d violations=%d ; max echo ratio = %.2f'
          % (len(rows), stat['hit'], stat['ok'], stat['no-site'], stat['no-token'],
             stat['refused'], stat['violation'], echo_max))
    print('SELF-CHECK: 每行 hit_quality=ok 且 echo<0.60 且 forbidden 行 0 命中 = %s'
          % (stat['hit'] > 0 and stat['ok'] == stat['hit'] and echo_max < 0.60
             and stat['violation'] == 0))
    if out_ledger is None:
        print('LEDGER NOT WRITTEN (DEMO MODE -- 演示产物不许有文件形态；⛔ 更不许落真 ledger 路径)')
        print('输出仅供 stdout 阅读；要真产物请去掉 --demo-violation')
    else:
        print('ledger written: ' + out_ledger + '  (header = "# probe-hits plan=%s")' % plan_decl)
    return 0


if __name__ == '__main__':
    sys.exit(main())
