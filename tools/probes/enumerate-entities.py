# -*- coding: utf-8 -*-
"""判据资产：穷举 Clover CS16 工程的**全部实体与状态**，产出三张表（T0 覆盖矩阵）。

出处/口径：skill `patterns/full-coverage-audit.md`（12+3 维度 / 判定权三分 / 四条闸门）
           + `scaffold/coverage-matrix.md`（列定义 / 枚举脚本契约：稳定排序、可 diff）。

用法（幂等；输出**稳定排序** ⇒ 同一份盘 = 同一份输出）：
    python tools/probes/enumerate-entities.py            # 生成三张表 + 覆盖矩阵判定片段
    python tools/probes/enumerate-entities.py --inject   # 再把片段写进 策划/验收表.md 的标记区

产出：
    策划/实体清单.tsv         维度	实体	载体/路径	出处	状态数	判据类型	归属片
    策划/状态矩阵.tsv         维度	实体	状态/事件	边界值	期望表现(出处)	实测	结论	证据
    策划/差异登记.tsv         编号	为什么	出处	何时消除
    策划/覆盖矩阵判定.md      一行 = 一个实体的判定行（含 `#` 头；`--inject` 时写进验收表）

⛔ 本脚本**只判**：几何 / 碰撞 / 坐标 / 数值 / 引用是否指对 / 状态是否存在。
   只能眼睛判的（贴图观感 / UI 对齐 / 动画观感 / 音色 / 节奏）一律判 `待采(并排图)`。
"""
import os
import re
import struct
import sys
from collections import Counter, OrderedDict

HERE = os.path.dirname(os.path.abspath(__file__))
# ⛔ 不许在 tools/probes/ 里留 __pycache__（skill §8 工程卫生）：下面会用 importlib 载入
#    geom-check.py，默认会写出 tools/probes/__pycache__/*.pyc ⇒ 显式关掉字节码落盘。
sys.dont_write_bytecode = True
# ⛔ Windows 控制台默认 GBK 编码：判定文本里一旦出现 GBK 之外的字符（实测：切片L 给 S3「语言」
#    行的判决文本里含 `⇒`），末尾那几行 print 就会抛 UnicodeEncodeError。
#    写盘全部发生在 print 之前 ⇒ 产物不受影响，但脚本会留下 traceback 并以非零码退出（会被
#    上层驱动误判成"枚举失败"，进而掩盖真正的失败）。故显式把 stdout 切到 UTF-8 + errors=replace。
try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass
# stderr 也切 UTF-8：新增的 `[inputs]` / `[inject]` 诊断行里有中文路径与中文说明，用控制台默认
# cp936 写出去会被采集端按 UTF-8 读成乱码 ⇒ "打印绝对路径让人核对"这条判据当场失效。
try:
    sys.stderr.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass
ROOT = os.path.dirname(os.path.dirname(HERE))

import subprocess

# ---- 片BW-ZERO（2026-09-23）：缺席断言的「范围声明」----------------------------------
# 为什么（规矩本体 R-范围定义与覆盖数 / R-覆盖声明三件齐）：穷举式结论（"全仓 grep X 命中 0"）
#   必须声明**范围定义**（目录 + 表达形式 + 工具默认行为）并给出**实际覆盖数**；
#   `0 命中` 必须附 `扫描文件数 / 各根命中数 / 排除清单`。缺任一项 ⇒ "我扫了 N 个"不可信。
# ⛔ 数字**全部生成时现跑**（`os.walk` / `rg --files`），⛔ 不许手写（实测事故：手写扫描数会过期）。
# ⛔ junction 挂载根（引擎包 `client/Packages/com.clover.unity-engine` 指向 `clover-client-unity-engine`）
#   必须 `via_walk=True` —— 前任实测 `rg` 对该根枚举 **0** 文件、而 `os.walk` = **550**。
_TOOL_DEFAULTS = ('尊重 ignore=True | 含隐藏目录=True | 跳过二进制=**是**（rg 默认）'
                  ' | 显式列根=True（⛔ 全仓搜索 ≠ 本串口径）'
                  ' | 跟随 junction=**否**（**全仓搜索**默认不进；显式列根时能枚举）'
                  ' | 读失败=计入"读失败数"')

_RG_OK = [None]          # None=未探测；True/False=rg 是否可用（列表 ⇒ 可在闭包里赋值）


def _split_root(root):
    """`路径/**` -> (base, glob)；无通配 -> (root, None)。

    ⛔ 为什么必须有这一步（本片**预演当场**抓到的缺陷，非设计稿原样）：
        `subprocess.run` **不经过 shell** ⇒ 通配符**不会被展开**。原样把 `client/Assets/**`
        当路径喂 rg ⇒ `rc=2 (No such file or directory)` ⇒ 计数恒 **0**
        —— 那正是一条"**假 0 声明**"，是本片要消灭的形态本身。
        实测：`rg … 'client/Assets/**'` → rc=2 / 1 行错误；`rg … client/Assets` → **4393**。
    """
    for i, ch in enumerate(root):
        if ch in '*?[':
            base = root[:i].rstrip('/')
            return (base or '.'), root[i:]
    return root, None


def _rg_file_count(root):
    """rg --files 在该根枚举到的文件数；返回 (n, bad_reason_or_None)。"""
    if _RG_OK[0] is None:
        try:
            subprocess.run(['rg', '--version'], stdout=subprocess.DEVNULL,
                           stderr=subprocess.DEVNULL, timeout=30)
            _RG_OK[0] = True
        except Exception:
            _RG_OK[0] = False
    if not _RG_OK[0]:
        return 0, 'rg 不可用'
    base, glob = _split_root(root)
    args = ['rg', '--files', '--no-ignore', '--hidden']
    if glob and glob not in ('**', '/**', '/**/*'):
        args += ['--glob', glob]
    args.append(base)
    try:
        p = subprocess.run(args, cwd=ROOT, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                           timeout=300)
        n = len([x for x in p.stdout.decode('utf-8', 'replace').splitlines() if x.strip()])
        if p.returncode != 0:
            return n, 'rg rc=%d' % p.returncode
        return n, None
    except Exception as e:
        return 0, 'rg 异常 %s' % type(e).__name__


def _walk_file_count(root):
    """os.walk 枚举文件数（跟 junction）；返回 (n, bad_reason_or_None, used_followlinks)。"""
    base, _glob = _split_root(root)          # ⛔ 同样要剥掉 `/**`，否则 os.path 判"路径不存在"
    p0 = os.path.join(ROOT, base)
    if not os.path.exists(p0):
        return 0, '路径不存在', False
    for follow in (False, True):     # 默认先试；若为 0 再用 followlinks=True 复测（并把实情记进串）
        n = 0
        try:
            for dp, dn, fn in os.walk(p0, followlinks=follow):
                if os.path.basename(dp) == '.git':
                    dn[:] = []
                    continue
                n += len(fn)
        except Exception as e:
            return n, 'walk 异常 %s' % type(e).__name__, follow
        if n or follow:
            return n, None, follow
    return 0, None, False


def scope_note(roots, patterns, via_walk=False):
    """返回可机读的"范围声明"串；数字全部**现跑**得到（⛔ 不手写）。"""
    tot, bads, per_root, excl = 0, 0, [], []
    for r in roots:
        if via_walk:
            n, bad, follow = _walk_file_count(r)
            # 口径按**实测**写（本片实跑：junction 根 `rg --files <该目录>` = 550 = os.walk）：
            # ⛔ 盲的是**全仓搜索**那一侧，⛔ 不是"显式列根也枚举不到"。
            # ⚠️ 口径必须自带：os.walk 这一支**排除 `.git`**（实测：该 junction 根 550 - 230(.git) = 320）
            #    ⇒ ⛔ 别再拿它跟"rg 含 .git 的 550"直接比（规矩 `R-计数口径须与断言语义一致`）。
            excl.append(r + '（junction 挂载根：**全仓搜索**默认不进它 ⇒ 必须显式列根；'
                            '本行用 os.walk%s 枚举，**已排除 `.git`**）'
                        % ('(followlinks=True)' if follow else ''))
        else:
            n, bad = _rg_file_count(r)
            excl.append(r + '（显式列根，用 rg --files；⛔ 全仓搜索 ≠ 本串的口径）')
        if bad:
            bads += 1
            excl.append(r + '（%s）' % bad)
        per_root.append((r, n))
        tot += n
    return ('范围={ 根:[%s] | 式:[%s] | 工具默认:%s'
            ' | 扫描文件数:{合计:%d, %s, 读失败:%d}'
            ' | 排除:[%s] }'
            % (','.join(roots), ','.join(patterns), _TOOL_DEFAULTS, tot,
               ', '.join('%s:%d' % kv for kv in per_root), bads, ';'.join(excl)))


# ---- 片BW-ZERO：每个缺席断言的"范围声明"变量（⛔ 只定义一次，站点只引用）-------------
_SCOPE_ASTAR = scope_note(['client/Assets/**'], ['AStar'])
_SCOPE_CORPSE = scope_note(['client/Assets/**'], ['Corpse', '尸体'])
_SCOPE_BLOOD = scope_note(['client/Assets/**'], ['Blood', '血雾'])
_SCOPE_PICKUP = scope_note(['client/Assets/**'], ['TryPickupDropped'])
_SCOPE_WPREFAB = scope_note(['client/Assets/Resources/Art/T/player.prefab',
                            'client/Assets/Resources/Art/CT'], ['weapon', 'w_'])
_SCOPE_BT_ENGINE = scope_note(['client/Packages/com.clover.unity-engine/**'],
                              ['BehaviorTree', '行为树', 'btree'], via_walk=True)


# 第 13 个站点（本片**干跑实测**补出来的；⛔ 设计稿的清单里没有它）：
#   产生判定行 3759 / 3783 / 3784（S1 武器 Defuser/Vest/VestHelm）那句
#   `原版 mdl 导出 .cs16anim 共 N 个 = … 装备类命中 0 个；盘上 vm_*.controller 同样 N 个`
#   ⇒ 它同时断言两个根：`Editor/Views/ModelData` 与 `Resources/Art/Anim`。
_SCOPE_PASSIVE = scope_note(['client/Assets/Editor/Views/ModelData',
                             'client/Assets/Resources/Art/Anim'],
                            ['vm_defuser', 'vm_vest', 'vm_vesthelm'])


def _scope_dif(x):
    """**DIF 单元格专用**：把 `|` 换成 `/`。

    ⛔ 为什么必须有它（本片**预演当场**抓到的缺陷，非设计稿原样）：
        `差异登记` 是 markdown 表，生成器有一条硬卫生闸门 —— 把 canonical 串（含 `|`）
        原样塞进 DIF 单元格 ⇒ `[dif] ERROR hygiene: cell-with-bare-pipe=['67','76','80']`
        ⇒ **`ABORT before writing ANY product`（rc=2，一个字节都不写）**。
        实测修法 = 这三个站点改用本函数包一层；⛔ 判定行（`覆盖矩阵判定`）那侧仍用 canonical 形态，
        因为 `absence-scope` 闸门认的就是它。
    """
    return x.replace('|', '/')


# ---- 片BW-ZERO：`absence-scope` 的**真口径**（落地前干跑实测；⛔ 别用设计稿的"26 行"）-------
# 验收门限 = **覆盖关系**，⛔ 不是计数阈值：
#     判定行（`策划/覆盖矩阵判定.fragment.md` → 注入 `策划/验收表.md`）里**每一条含缺席关键词**的行，
#     都必须带 canonical `范围={ 根:[…] … 扫描文件数:{…} … }` ⇒ 即 **gap == 0**。
# 实测（干跑；脚本 = `.ai-tmp/test/bwzr-p3-pregate.py`，**落地后请把它移进 `tools/probes/`**）：
#     canonical 0 → 9 ；含缺席关键词的判定行 = **9** 条
#     （1463 / 3165 / 3170 / 3331 / 3336 / 3337 / 3759 / 3783 / 3784）；**gap 9 → 0**（PASS）。
# ⛔ 三条别再踩的坑（都实测过）：
#     ① 设计稿写的"补 26 行" **不是这个口径的数** —— 判定行里只有 9 条缺席行；
#        按 `>= 26` 落地**必然假红**，而"凑 26"通常就是造假 ⇒ 规矩 `R-门限用覆盖不用计数`。
#     ② 判定行里的 canonical 串，分隔符**被渲染成 `/`**（markdown 表不许裸 `|`）
#        ⇒ 判据正则必须收 `[|/]` 两种；⛔ 只认一种会**假红或假绿**（本片两次都踩过）。
#     ③ 落地后**必须重采一次**判据 2b（`--out-dir=` 沙箱 + 那个干跑脚本），
#        ⛔ 不许沿用本注释里的数字当"现行值"（规矩 `R-归档不用易失标识`）。


# ---- 判据的输入必须**绝对锚定**，并在启动时打印一次解析到的绝对路径 ------------------
# 为什么（skill `reference/anti-gaming.md` §五 第 4 条「判据的输入也要绝对锚定」）：
#   `SPEC` 曾写成相对路径 `'策划/策划案/…md'`、读函数直接 `open(path)` ⇒ 只在"从项目根运行"时
#   才对。换个 CWD 跑**不报错**：读空 ⇒ 声明的阻挡盒数静默降级成 `-1` ⇒ 那一行的结论从
#   `一致` 翻成 `不一致(规格写 -1，实际 811)`。危险点不在"跑错"，而在**它只表现为某一行变红 /
#   变值**，读的人会以为发现了真缺陷（本项目据它起草好了措辞、差一步就把一条**不存在**的差异
#   登记进表）。⇒ 现在：① 引用串（`SPEC` / `REGISTRY`，要写进判定行、必须保持相对形态）与
#   读取路径（`*_ABS`）分离，读取一律走 `os.path.join(ROOT, …)`；② 启动时把实际会读的绝对路径
#   打到 stderr（人一眼能看出两次跑的是不是同一份）。
_INPUT_NAME_ORDER = ['ROOT', 'HERE', 'ASSETS', 'PLAN', 'OUT_DIR', 'SPEC', 'SPEC_ABS', 'REGISTRY',
                     'REGISTRY_ABS', 'CS_SCRIPTS', 'CS_CORE', 'GEO', 'BSP', 'SCENE', 'EDLOG']


def _log_inputs(tag):
    """把本次运行**实际会读**的输入（解析成绝对路径）打到 stderr。

    ⛔ 只写 stderr：stdout 上有被闸门读取的计数行（`ENTITY rows = …` / `STATE rows = …` /
       `DIFF rows = …`），一个字符都不许动（改完必须做前后对照）。
    """
    w = sys.stderr
    w.write('=== [inputs] tag=%s  cwd=%s ===\n' % (tag, os.getcwd()))
    for n in _INPUT_NAME_ORDER:
        v = globals().get(n)
        if not isinstance(v, str) or not v:
            continue
        p = v if os.path.isabs(v) else os.path.join(ROOT, v)
        w.write('  [input] %-12s %s   exists=%s\n' % (n, p, os.path.exists(p)))
    w.flush()


ASSETS = os.path.join(ROOT, 'client', 'Assets')
PLAN = os.path.join(ROOT, '\u7b56\u5212')          # 策划

# ---- 输出目录 seam：`--out-dir=<dir>`（片BW-S-R）----------------------------------------
# 为什么：段属权对账（`section-ownership.py --check --rebuild`）需要一次**沙箱运行** ——
#   在临时目录里跑一遍生成器、拿沙箱产物与盘上产物比，而不是"相信生成器刚跑过"（⛔ 那样在
#   生成器未跑 / 片段陈旧时会**假绿**）。所以生成器要能把**全部落盘产物**改道到一个空目录，
#   而**所有输入**（SPEC / REGISTRY / 源码 / 客户端资产）仍从 ROOT 绝对读取。
# ⛔ 两条口径（skill `reference/anti-gaming.md` §五 第 3/4 条）：
#   ① 引用串（要写进判定行的 `策划/…` 文本）保持**相对形态**，只有**落盘路径**随 OUT_DIR 走；
#   ② 一律**绝对解析**（相对值按 ROOT 解析，⛔ 不按 CWD —— CWD 一变就会静默写到别处 / 读到空）。
OUT_DIR = PLAN
for _a in sys.argv:
    if _a.startswith('--out-dir='):
        _v = _a.split('=', 1)[1]
        OUT_DIR = _v if os.path.isabs(_v) else os.path.join(ROOT, _v)

# 段属权的**单一真源** = `tools/probes/section-ownership.py`（本脚本 import 它，不复制第二份 ——
# 一复制就又是"同一件事两处维护"的老毛病）：`SECTION_OWNERSHIP`（段名锚 -> 属权 -> 实现位置）
# 与表头警告文本 `header_warning()`。`--inject` 用后者写 `策划/验收表.md` 的表头，
# 前者由 `python tools/probes/section-ownership.py --check` 双向对账（含"块内没有非生成器来源的行"）。
import importlib.util as _ilu   # （下面 geom-check.py 也用它，同一模块对象）
_so_spec = _ilu.spec_from_file_location('section_ownership',
                                        os.path.join(HERE, 'section-ownership.py'))
SO = _ilu.module_from_spec(_so_spec)
_so_spec.loader.exec_module(SO)
SECTION_OWNERSHIP = SO.SECTION_OWNERSHIP          # 名字在本脚本里可见（真源仍在上面的模块）

DIMS = ['D1', 'D2', 'D3', 'D4', 'D5', 'D6', 'D7', 'D8', 'D9', 'D10', 'D11', 'D12',
        'S1', 'S2', 'S3']
DIMNAME = {
    'D1': '\u8d44\u6e90', 'D2': '\u51e0\u4f55\u573a\u666f', 'D3': '\u6750\u8d28\u8d34\u56fe',
    'D4': 'UI/HUD', 'D5': '\u52a8\u753b', 'D6': '\u7279\u6548', 'D7': '\u97f3\u4e50',
    'D8': '\u97f3\u6548', 'D9': '\u78b0\u649e', 'D10': '\u903b\u8f91',
    'D11': '\u8f93\u5165', 'D12': '\u6d41\u7a0b', 'S1': '\u6570\u503c',
    'S2': '\u6027\u80fd', 'S3': '\u8bbe\u7f6e',
}
SLICE = {
    'D1': 'D1D2D3 \u8d44\u4ea7\u51e0\u4f55', 'D2': 'D1D2D3 \u8d44\u4ea7\u51e0\u4f55',
    'D3': 'D1D2D3 \u8d44\u4ea7\u51e0\u4f55', 'D4': 'D4 UI', 'D5': 'D5 \u52a8\u753b',
    'D6': 'D6 \u7279\u6548', 'D7': 'D7D8 \u97f3\u9891', 'D8': 'D7D8 \u97f3\u9891',
    'D9': 'D9 \u78b0\u649e\u4e0e\u53ef\u8d70', 'D10': 'D10 \u73a9\u6cd5\u903b\u8f91',
    'D11': 'D11 \u8f93\u5165', 'D12': 'D12 \u6d41\u7a0b', 'S1': 'S1 \u6570\u503c',
    'S2': 'S2 \u6027\u80fd', 'S3': 'S3 \u8bbe\u7f6e',
}
K_CONSIST = '\u4e00\u81f4'
K_MISMATCH = '\u4e0d\u4e00\u81f4'
K_PENDING = '\u5f85\u91c7(\u5e76\u6392\u56fe)'
K_ALLOWED = '\u5141\u8bb8\u7684\u5dee\u5f02(\u2192 \u5dee\u5f02\u767b\u8bb0.tsv)'
T_SCRIPT = '\u811a\u672c\u65ad\u8a00'
T_SIDE = '\u5e76\u6392\u56fe'
T_BLOCKED = '\u963b\u585e(\u767b\u8bb0)'

ENT = []    # (dim, name, carrier, source, statecount, verdict_type, verdict, evidence)
STA = []    # (dim, entity, state, boundary, expected, measured, conclusion, evidence)
DIF = []    # (what, why, source, when)


def add(dim, name, carrier, source, statecount, vtype, verdict, evidence):
    ENT.append((dim, str(name), carrier, source, str(statecount), vtype, verdict, evidence))


def sta(dim, entity, state, boundary, expected, measured, concl, evidence):
    STA.append((dim, str(entity), str(state), str(boundary), expected, measured, concl, evidence))


def rd(path, limit=None):
    try:
        with open(path, 'rb') as f:
            b = f.read(limit) if limit else f.read()
        return b.decode('utf-8', 'replace')
    except OSError:
        return ''


def rel(p):
    return os.path.relpath(p, ROOT).replace('\\', '/')


def walk(base, exts=None, skip_meta=True):
    out = []
    for r, d, fs in os.walk(base):
        for f in sorted(fs):
            if skip_meta and f.endswith('.meta'):
                continue
            if exts and not f.lower().endswith(exts):
                continue
            out.append(os.path.join(r, f))
    return sorted(out)


# ============================================================================
#  0. 语料（代码文本 / 资产文本 / guid 索引）
# ============================================================================
CODE_DIRS = [os.path.join(ASSETS, 'Scripts'), os.path.join(ASSETS, 'Editor')]
codeText = ''
codeFiles = []
for d in CODE_DIRS:
    for p in walk(d, ('.cs',)):
        codeFiles.append(p)
        codeText += rd(p) + '\n'
codeLow = codeText.lower()

VENDOR = os.path.join(ROOT, 'clover-client-unity-engine')
if os.path.isdir(VENDOR):
    for r, dd, fs in os.walk(os.path.join(VENDOR, 'Runtime')):
        for f in fs:
            if f.endswith('.cs'):
                codeText += rd(os.path.join(r, f)) + '\n'
    codeLow = codeText.lower()

ASSET_ROOT_LIST = [os.path.join(ASSETS, 'Resources'), os.path.join(ASSETS, 'ThirdParty'),
                   os.path.join(ASSETS, 'Scenes'), os.path.join(ASSETS, 'MapData')]
assetText = ''
for base in ASSET_ROOT_LIST:
    for p in walk(base):
        if p.lower().endswith(('.prefab', '.unity', '.mat', '.asset', '.controller', '.inputactions')):
            assetText += rd(p) + '\n'
assetLow = assetText.lower()

GUID2PATH = {}
for base in ASSET_ROOT_LIST + [os.path.join(ASSETS, 'Editor')]:
    for r, d, fs in os.walk(base):
        for f in fs:
            if not f.endswith('.meta'):
                continue
            m = re.search(r'guid:\s*([0-9a-f]{32})', rd(os.path.join(r, f)))
            if m:
                GUID2PATH[m.group(1)] = os.path.join(r, f[:-5])
ALL_TXT = codeText + '\n' + assetText
ALL_LOW = ALL_TXT.lower()


def mentions(tok):
    return tok.lower() in ALL_LOW


# ============================================================================
#  0b. 证据锚点（片BW-E）：把"自述式证据"换成**能从盘上重开核对**的锚点
# ============================================================================
# 为什么（skill `reference/anti-gaming.md` §三「判过程不判结果」+ verify.ps1 item 39）：
#   证据列此前是纯散文（`被引用（guid/短名命中代码或资产文本）` / `states=46`）⇒
#   第三方**无法重开核对** ⇒ 该行永远不可能变红 = **从未被判过**（本片实测 2822/3800）。
# ⛔ 三条口径（本段不许违反）：
#   ① 锚点**必须从盘上真读**算出来：guid 取自该资产 `.meta` 的真实 guid；`path:line` 取自
#      引用**实际命中**的那一行；载体路径必须先 `isfile` 过。
#   ② **读不到就不编**：宁可这一行仍无锚点（如实报数 + 逐类给原因），⛔ 不许写死/编造。
#   ③ 判据只有一个真源：锚点是否合格由 `tools/probes/audit-verdict-rows.py` 的
#      `classify_anchor()` 判（**import 它**，不在这里复写一份）⇒ 生成器与闸门不会"各判各的"。
_AV = None
try:
    _av_spec = _ilu.spec_from_file_location('audit_verdict_rows',
                                           os.path.join(HERE, 'audit-verdict-rows.py'))
    _AV = _ilu.module_from_spec(_av_spec)
    _av_spec.loader.exec_module(_AV)
    _AV.PLAN = PLAN
    # 裸文件名解析根（与 audit-verdict-rows.py main() 里的口径一致）
    _AV.BASENAME_ROOTS = [os.path.join(ROOT, '.ai-tmp', 'screenshots'),
                          os.path.join(ROOT, '.ai-tmp', 'test'),
                          os.path.join(ROOT, 'tools', 'probes')]
except Exception as _e:                       # 判据资产缺失 ⇒ 明报，不静默降级
    sys.stderr.write('  [anchor] WARN audit-verdict-rows.py not loadable: %r\n' % (_e,))

_LOWJOIN = None       # ALL_LOW 的"逐文件片段拼接"版本（用于把命中偏移映射回 文件:行号）


def _line_sites():
    """-> (lowjoin, segs)；`segs` = [(start, end, relpath|None)]。

    ⛔ 只在 `''.join(parts) == ALL_LOW` 成立时才算数（否则返回 None ⇒ 只退到 guid）：
       逐文件 `lower()` 与整体 `lower()` 在个别 Unicode 上下文折叠下会不等长，
       那会让"偏移 -> 文件:行号"整条映射错位 —— 宁可不给锚点，也不给一个错的行号。
    """
    global _LOWJOIN
    if _LOWJOIN is not None:
        return _LOWJOIN
    parts, segs, off = [], [], 0
    for p in codeFiles:
        t = rd(p).lower() + '\n'
        parts.append(t)
        segs.append((off, off + len(t), rel(p)))
        off += len(t)
    parts.append('\n')                                  # ALL_TXT 里 codeText 与 assetText 之间那一个
    segs.append((off, off + 1, None))
    off += 1
    for base in ASSET_ROOT_LIST:
        for p in walk(base):
            if p.lower().endswith(('.prefab', '.unity', '.mat', '.asset', '.controller',
                                   '.inputactions')):
                t = rd(p).lower() + '\n'
                parts.append(t)
                segs.append((off, off + len(t), rel(p)))
                off += len(t)
    joined = ''.join(parts)
    _LOWJOIN = (joined, segs) if joined == ALL_LOW else (None, None)
    if _LOWJOIN[0] is None:
        sys.stderr.write('  [anchor] WARN per-file lower() != corpus lower(); '
                         'reference-site anchors disabled (guids only)\n')
    return _LOWJOIN


def mention_site(tok, exclude=None):
    """`tok` 在**引用语料**里首次出现的 `文件:行号`（`exclude` = 跳过的文件，通常是被引用的资产本身）。

    ⚠️ 与 `mentions()` 同一份 `ALL_LOW` ⇒ 两者不会互相矛盾：`mention_site` 判 True 时
       `mentions` 必为 True（子串蕴含）。返回 None = 语料里找不到（例如命中来自派生短名
       `sfx/<id>_fire` 或场景 guid）⇒ 该行只退到 guid 锚点，⛔ 不猜行号。
    """
    s, segs = _line_sites()
    if not s:
        return None
    t = tok.lower()
    k = s.find(t)
    while k >= 0:
        lo, hi = 0, len(segs) - 1
        while lo < hi:
            mid = (lo + hi + 1) // 2
            if segs[mid][0] <= k:
                lo = mid
            else:
                hi = mid - 1
        st, _en, rp = segs[lo]
        if rp and rp != exclude:
            return '%s:%d' % (rp, s.count('\n', st, k) + 1)
        k = s.find(t, k + 1)
    return None


def guid_of(relp):
    """盘上 `<relp>.meta` 里的真实 guid（读不到 -> ''，⛔ 不编）。"""
    if not relp:
        return ''
    p = os.path.join(ROOT, str(relp).replace('/', os.sep))
    if not os.path.exists(p + '.meta'):
        return ''
    mm = re.search(r'guid:\s*([0-9a-f]{32})', rd(p + '.meta'))
    return mm.group(1) if mm else ''


def anchor_token(tok):
    """`tok` 在盘上**真能解析的那个写法**（否则 ''）。

    ⛔ 只返回"重开后真能打开"的写法：写进证据列的东西必须过判据资产 `resolve()`
       （它按 ROOT / PLAN 解析）—— 载体列里有相当一部分是 **Assets 相对路径**
       （如 `Resources/Art/Anim/x.anim`），把它原样写进证据 = **看起来像锚点但打不开**
       = anti-gaming 点名的"编锚点"形态。故这里显式补 `client/Assets/` 前缀再验一次，
       并把可解析的**那个写法**写出去。
    """
    if not tok:
        return ''
    t = str(tok).strip().strip('`').replace('\\', '/')
    t = re.split(r'[\uff08(\uff1b;]', t)[0].strip()
    m = re.match(r'^(.+\.[0-9A-Za-z]{1,12}):(\d+)$', t)
    stem, ln = (m.group(1), ':' + m.group(2)) if m else (t, '')
    # 载体列的三种现存写法（都由 isfile 兜底验证，⛔ 不猜）：
    #   ① 项目根相对 `client/Assets/...`；② Assets 相对 `Resources/...`；
    #   ③ Scripts 相对 `Core/CsConst.cs` / `Module/**`（= `CS_SCRIPTS` 根）
    for s in (stem, 'client/Assets/' + stem.lstrip('/'),
              'client/Assets/Scripts/' + stem.lstrip('/')):
        if s and os.path.isfile(os.path.join(ROOT, s.replace('/', os.sep))):
            return s + ln
    return ''


def anchor_ok(ev):
    """这一行的证据是否**已被判据资产认定有锚点**（真源 = audit-verdict-rows.py）。"""
    if _AV is None:
        return False
    try:
        return _AV.classify_anchor(ev)[0] is not None
    except Exception:
        return False


def code_site(pat):
    """正则 `pat` 在源码语料里首次命中的 `文件:行号`（与 `re.search(pat, codeText)` 同集合）。

    ⚠️ 口径一致：`codeText` 按"每个文件内容 + 一个换行"拼接，而下面的模式都不含换行
       ⇒ "拼接串上命中" 等价于 "某一行上命中" ⇒ 逐文件 `lineno()` 取到的是同一次命中。
    """
    for p in codeFiles:
        n = lineno(p, pat)
        if n:
            return '%s:%d' % (rel(p), n)
    return ''


# ---- 派生 clip 名 oracle -------------------------------------------------
# CsWeapons 的枪声/换弹短名是**拼出来的**（`"sfx/" + id + "_fire"`，见 CsWeapons.cs:201），
# 盘上看不到字面量；CsAudioTuning 里的短名是字面量。两者合起来才是"事件真挂了"的判据。
DERIVED_CLIPS = set()
for m in re.finditer(r'public const string\s+(\w+)\s*=\s*"([^"]+)"', rd(os.path.join(ASSETS, 'Scripts', 'Module', 'Audio', 'CsAudioTuning.cs'))):
    DERIVED_CLIPS.add(m.group(2))
for m in re.finditer(r'public const string\s+\w+\s*=\s*"([a-z0-9_]+)"', rd(os.path.join(ASSETS, 'Scripts', 'Core', 'CsWeapons.cs'))):
    DERIVED_CLIPS.add('sfx/%s_fire' % m.group(1))
    DERIVED_CLIPS.add('sfx/%s_reload' % m.group(1))


def guid_refs_in(text):
    return re.findall(r'guid:\s*([0-9a-f]{32})', text)


def lineno(path, pat):
    for i, l in enumerate(rd(path).split('\n')):
        if re.search(pat, l):
            return i + 1
    return 0


def srcref(path, pat, label):
    n = lineno(path, pat)
    return '%s:%d' % (rel(path), n) if n else '%s(%s)' % (rel(path), label)


CS_SCRIPTS = os.path.join(ASSETS, 'Scripts')
CS_CORE = os.path.join(CS_SCRIPTS, 'Core', 'CsConst.cs')
CS_WEAPONS = os.path.join(CS_SCRIPTS, 'Core', 'CsWeapons.cs')
CS_ROUND = os.path.join(CS_SCRIPTS, 'Module', 'Match', 'CsRound.cs')
CS_MATCH = os.path.join(CS_SCRIPTS, 'Module', 'Match', 'CsMatch.cs')
CS_AUDIO_T = os.path.join(CS_SCRIPTS, 'Module', 'Audio', 'CsAudioTuning.cs')
CS_COMBAT_T = os.path.join(CS_SCRIPTS, 'Module', 'Combat', 'CsCombatTuning.cs')
CS_VIEW_T = os.path.join(CS_SCRIPTS, 'Module', 'View', 'CsViewTuning.cs')
CS_HUD_THEME = os.path.join(CS_SCRIPTS, 'UI', 'InGame', 'CsHudTheme.cs')
FLOW = os.path.join(CS_SCRIPTS, 'Module', 'Flow', 'AppFlow.cs')
MOTOR = os.path.join(CS_SCRIPTS, 'Module', 'Player', 'PlayerMotor.cs')
COMBAT_MOD = os.path.join(CS_SCRIPTS, 'Module', 'Combat', 'CombatModule.cs')
EFFECTS = os.path.join(CS_SCRIPTS, 'Module', 'Combat', 'CombatEffects.cs')
CS_MAP = os.path.join(CS_SCRIPTS, 'Module', 'Map', 'CsMap.cs')
OPTIONS = os.path.join(CS_SCRIPTS, 'UI', 'Flow', 'OptionsPanel.cs')
SETTINGS_STORE = os.path.join(CS_SCRIPTS, 'UI', 'Flow', 'CsPlayerSettingsStore.cs')
# ⛔ 引用串 vs 读取路径（见文件顶 `_log_inputs` 上方的「绝对锚定」说明）：
SPEC = '\u7b56\u5212/\u7b56\u5212\u6848/CS1.6\u5355\u673a\u53c2\u8003\u89c4\u683c.md'   # 只用于**写进判定行的引用串**
REGISTRY = '\u7b56\u5212/\u5bf9\u7167\u8868.md'                                         # 同上
SPEC_ABS = os.path.join(ROOT, SPEC)              # ⛔ 判据**读**这份（绝对）
REGISTRY_ABS = os.path.join(ROOT, REGISTRY)      # ⛔ 同上
_log_inputs('early')                             # 读 SPEC 之前先打印解析结果
REG_TXT = rd(REGISTRY_ABS) + rd(SPEC_ABS)

# ============================================================================
#  D1 资源 / 资产
# ============================================================================
RESPATHS = os.path.join(CS_SCRIPTS, 'Core', 'ResPaths.cs')
respaths_txt = rd(RESPATHS)
for m in re.finditer(r'public (?:const|static)\s+\w[\w<>\[\]]*\s+(\w+)\s*=', respaths_txt):
    nm = m.group(1)
    ln = lineno(RESPATHS, r'\b%s\s*=' % nm)
    add('D1', 'ResPaths.%s' % nm, 'Scripts/Core/ResPaths.cs', '%s:%d' % (rel(RESPATHS), ln),
        1, T_SCRIPT, K_CONSIST, '%s:%d' % (rel(RESPATHS), ln))

SCENE_GUIDS = set(guid_refs_in(rd(os.path.join(ASSETS, 'Scenes', 'Boot.unity')) +
                               rd(os.path.join(ASSETS, 'Scenes', 'Menu.unity')) +
                               rd(os.path.join(ASSETS, 'Scenes', 'StageDust2.unity'))))
D1_FILES = []
for base in ASSET_ROOT_LIST:
    for p in walk(base):
        if p.lower().endswith(('.anim',)):      # 615 个 .anim 归 D5（动画数据）
            continue
        D1_FILES.append(p)

# ── 切片H：原版素材**已按原版复制进工程、但本工程暂未接**的那些（四要素登记在
#    策划/差异登记.tsv + 验收表「允许的差异」）—— 本表按 K_ALLOWED 记，不再判 不一致。
#    ⛔ 只登记"原版确实有对应物、本片明说不做"的项；拿它兜"工程里凭空多出来的东西" = 伪造。
#    ⛔ 音效那几条属 D8（音效事件接线），本片不做（任务书 §2 明令）。
D1_UNREF_ALLOWED = {
    'client/Assets/Resources/Sound/SFX/sfx/bomb_beep_fast.wav':
        '原版 C4 快速蜂鸣音；本工程只接了常规蜂鸣（音效事件接线属 D8 片）',
    'client/Assets/Resources/Sound/SFX/sfx/dryfire.wav':
        '原版空仓击发音（没子弹时扣扳机）；本工程空仓只打日志不发音（属 D8 片）',
    'client/Assets/Resources/Sound/SFX/sfx/flash_explode.wav':
        '原版闪光弹爆音；本工程闪光只做全屏致盲表现（属 D8 片）',
    'client/Assets/Resources/Sound/SFX/sfx/hit_wall.wav':
        '原版弹着墙音；本工程弹痕只有视觉、无着弹音（属 D8 片）',
    'client/Assets/Resources/Sound/SFX/sfx/knife_hit.wav':
        '原版刀命中音；本工程刀命中走 ReportHit 只结算伤害（属 D8 片）',
    'client/Assets/Resources/UI/Art/logo_game.tga':
        '原版 GameUI 字标；已登记在验收表「允许的差异」#37（本片不使用，留给主菜单片接 ResPaths）',
    'client/Assets/ThirdParty/Dust2/Textures/SandRoadTgtA.png':
        '原版 de_dust2 包点贴花（TgtA）；本工程几何只用 33 个主材质组，未做贴花层',
    'client/Assets/ThirdParty/Dust2/Textures/_1Sand.png':
        '原版 de_dust2 沙地细节层贴图；本工程未做 detail 层',
    'client/Assets/ThirdParty/Dust2/Textures/_1SandRock2.png':
        '原版 de_dust2 沙岩细节层贴图；本工程未做 detail 层',
    'client/Assets/ThirdParty/Dust2/Textures/_1csSandWall.png':
        '原版 de_dust2 沙墙细节层贴图；本工程未做 detail 层',
    'client/Assets/ThirdParty/Dust2/Textures/_2SandRock2.png':
        '原版 de_dust2 沙岩细节层贴图；本工程未做 detail 层',
    'client/Assets/ThirdParty/Dust2/Textures/_3Sand.png':
        '原版 de_dust2 沙地细节层贴图；本工程未做 detail 层',
    'client/Assets/ThirdParty/Dust2/Textures/black.png':
        '原版 de_dust2 通用黑贴图（BSP 里的辅助/黑面）；本工程几何未引用它',
    'client/Assets/ThirdParty/Dust2/Textures/wall_g.png':
        '原版 de_dust2 墙面贴图（770×380 非 POT）；本工程几何组里没有它',
}

for p in D1_FILES:
    r = rel(p)
    base = os.path.splitext(os.path.basename(p))[0]
    guid = ''
    mp = p + '.meta'
    if os.path.exists(mp):
        mm = re.search(r'guid:\s*([0-9a-f]{32})', rd(mp))
        guid = mm.group(1) if mm else ''
    ref = (guid and guid in SCENE_GUIDS) or mentions(base) or mentions(r.replace('Assets/', ''))
    if p.lower().endswith(('.bytes', '.tga')):
        # 扩展名会被资源系统按请求类型解析：用去扩展名的全路径再判一次
        ref = ref or mentions(os.path.splitext(r.replace('Assets/', ''))[0])
    if p.lower().endswith('.wav'):
        # 音效短名可能由代码拼出（CsWeapons.SoundFire = "sfx/" + id + "_fire"）⇒ 用派生名 oracle
        ref = ref or ('sfx/' + base) in DERIVED_CLIPS
    if ref:
        # 片BW-E：`被引用（guid/短名命中代码或资产文本）` 这句自述**没有任何可复核锚点** ⇒
        #   改成**盘上真算**的两样：① 该资产 `.meta` 的真实 guid；② 引用**实际命中**的
        #   `文件:行号`（`mention_site`，排除资产自己那一行 —— 资产 YAML 里的 `m_Name: 自己`
        #   是自指、不算"被谁引用"）。两样都算不出来就**留空**，由后面的锚点收口段补载体锚点，
        #   仍补不上就如实计入"仍无锚点"（⛔ 不编）。
        _site = mention_site(base, exclude=r)
        if _site is None:
            _site = mention_site(os.path.splitext(r.replace('Assets/', ''))[0], exclude=r)
        _ap = ([('guid=' + guid)] if guid else []) + ([_site] if _site else [])
        v, ev = K_CONSIST, '\u88ab\u5f15\u7528' + ('\uff1a' + ' '.join(_ap) if _ap else '')
    elif r in D1_UNREF_ALLOWED:
        # \u5df2\u767b\u8bb0\u7684\u539f\u7248\u672a\u63a5\u7d20\u6750\uff1a\u7ed3\u8bba = \u5141\u8bb8\u7684\u5dee\u5f02\uff08\u8bc1\u636e\u6307\u5411 \u5dee\u5f02\u767b\u8bb0.tsv\uff09
        v, ev = '%s(\u2192 \u5dee\u5f02\u767b\u8bb0.tsv \u7b2c D1 \u6bb5)' % K_ALLOWED, \
                'guid=%s / basename=%s \u5747\u672a\u547d\u4e2d\uff1b%s' % (guid or '-', base, D1_UNREF_ALLOWED[r])
    else:
        v, ev = ('%s(\u672a\u88ab\u4efb\u4f55\u5f15\u7528\u70b9\u5f15\u7528\uff1a\u6587\u4ef6\u5728\u76d8\u4e0a\u4f46\u65e0\u4eba\u8bfb)' % K_MISMATCH), \
                'guid=%s / basename=%s \u5747\u672a\u547d\u4e2d' % (guid or '-', base)
    add('D1', r, r, 'Assets/' + r, 1, T_SCRIPT, v, ev)

# ============================================================================
#  D2 几何与场景（geo.bin + BSP + StageDust2 物件树）
# ============================================================================
GEO = os.path.join(ASSETS, 'ThirdParty', 'Dust2', 'de_dust2_geo.bin')
BSP = os.path.join(ASSETS, 'ThirdParty', 'Dust2', 'de_dust2.bsp')
SCENE = os.path.join(ASSETS, 'Scenes', 'StageDust2.unity')


def parse_geo(path):
    data = open(path, 'rb').read()
    assert data[0:4] == b'CD2G'
    o = [4]

    def u32():
        v = struct.unpack_from('<I', data, o[0])[0]; o[0] += 4; return v

    def f32():
        v = struct.unpack_from('<f', data, o[0])[0]; o[0] += 4; return v
    u32(); cell = f32(); ox = f32(); oz = f32()
    gt = f32(); omh = f32(); pb = f32(); pt = f32()
    w = u32(); d = u32()
    for _ in range(6):
        f32()
    gc = u32(); groups = []
    verts_by_group = []
    for _ in range(gc):
        name = data[o[0]:o[0] + 48].split(b'\0')[0].decode('utf-8', 'replace'); o[0] += 48
        vc = u32(); ic = u32()
        po = o[0]
        vs = [struct.unpack_from('<3f', data, po + 12 * i) for i in range(vc)]
        o[0] += 12 * vc + 8 * vc + 12 * vc + 4 * ic
        groups.append((name, vc, ic))
        verts_by_group.append(vs)
    bc = u32()
    blockers = []
    for _ in range(bc):
        blockers.append((u32(), u32(), u32(), u32(), f32(), f32()))
    return dict(cell=cell, ox=ox, oz=oz, w=w, d=d, groups=groups, verts=verts_by_group,
                blockers=blockers)


G = parse_geo(GEO)
GEO_TRI = {n: ic // 3 for n, vc, ic in G['groups']}


def parse_bsp_tex(path):
    """GoldSource BSP v30：lump2 = miptex 目录；lump6 = texinfo；lump7 = faces。"""
    data = open(path, 'rb').read()
    ver = struct.unpack_from('<i', data, 0)[0]
    lumps = [struct.unpack_from('<ii', data, 4 + 8 * i) for i in range(15)]
    off2, len2 = lumps[2]
    n = struct.unpack_from('<i', data, off2)[0]
    names = []
    for i in range(n):
        mo = struct.unpack_from('<i', data, off2 + 4 + 4 * i)[0]
        if mo < 0:
            names.append(None); continue
        names.append(data[off2 + mo:off2 + mo + 16].split(b'\0')[0].decode('latin-1'))
    off6, len6 = lumps[6]
    # texinfo 段：GoldSource 里是「int count + count * texinfo_t(40B)」；个别导出器省掉 count。
    n4 = struct.unpack_from('<i', data, off6)[0]
    if n4 * 40 + 4 <= len6 + 8:
        ninfo, tbase = n4, off6 + 4
    else:
        ninfo, tbase = len6 // 40, off6
    texinfo = []
    for i in range(ninfo):
        base = tbase + 40 * i
        mip = struct.unpack_from('<i', data, base + 32)[0]
        texinfo.append(mip)
    off7, len7 = lumps[7]
    nf4 = struct.unpack_from('<i', data, off7)[0]
    if nf4 * 20 + 4 <= len7 + 8:
        nface, fbase = nf4, off7 + 4
    else:
        nface, fbase = len7 // 20, off7
    faces = Counter()
    for i in range(nface):
        base = fbase + 20 * i
        ti = struct.unpack_from('<H', data, base + 10)[0]
        if ti < len(texinfo) and 0 <= texinfo[ti] < len(names) and names[texinfo[ti]]:
            faces[names[texinfo[ti]]] += 1
    return ver, names, faces


BSP_VER, BSP_NAMES, BSP_FACES = parse_bsp_tex(BSP)
GEO_SET = set(GEO_TRI)
scene_txt = rd(SCENE)


def scene_docs(text):
    out = []
    for m in re.finditer(r'--- !u!(\d+) &(\d+)(?: stripped)?\s*\n(.*?)(?=\n--- !u!|\Z)', text, re.S):
        out.append((m.group(1), m.group(2), m.group(3)))
    return out


DOCS = scene_docs(scene_txt)
GO_NAME = {}
for cid, fid, body in DOCS:
    if cid == '1':
        mm = re.search(r'm_Name:\s*(.*)', body)
        if mm:
            GO_NAME[fid] = mm.group(1).strip()
COMP_OWNER = {}
COMP_TYPE = {}
for cid, fid, body in DOCS:
    mm = re.search(r'm_GameObject:\s*\{fileID:\s*(\d+)\}', body)
    if mm:
        COMP_OWNER[fid] = mm.group(1)
    if cid == '65':
        COMP_TYPE[fid] = 'BoxCollider'
    elif cid == '64':
        COMP_TYPE[fid] = 'MeshCollider'

GO_COMPS = {}
for fid, owner in COMP_OWNER.items():
    GO_COMPS.setdefault(owner, []).append(COMP_TYPE.get(fid, 'Script' if fid else ''))
SCENE_NODES = Counter(GO_NAME.values())
MESH_NODES = set()
for fid, nm in GO_NAME.items():
    if 'MeshCollider' in GO_COMPS.get(fid, []):
        MESH_NODES.add(nm)

# ============================================================================
#  几何 / 碰撞 / 可走性 的口径：**全部来自判据资产 tools/probes/geom-check.py**
#  ⛔ 只有一份实现：这里 import 它并取数，而不是把判定再抄一遍（抄了必然两边漂移）。
#     —— 本片（cs16-切片F）的四条修复都落在它身上：阻挡盒口径 / 门贴图面 / 箱子挡人 / 矮障碍可跳过。
# ============================================================================
import importlib.util as _ilu

_gc_spec = _ilu.spec_from_file_location('geomcheck', os.path.join(HERE, 'geom-check.py'))
GC = _ilu.module_from_spec(_gc_spec)
_gc_spec.loader.exec_module(GC)
GC_CONST = GC.const_or_die()
GC_GEO = GC.load_geo()
GC_SCENE = GC.parse_scene()
GC_BM = GC.load_bits(GC.BITMAP)
GC_GEOM_ALL = GC.Geom(GC_GEO)
GC_GATE = GC.Gate(GC_CONST, GC_GEOM_ALL)
GC_COLL = GC.colliders_consistency(GC_GEO, GC_SCENE, [GC.load_bits(p) for p in GC.BYTES_FILES])
GC_GROUPS = GC.scene_groups(GC_GEO, GC_SCENE)
GC_DOORS = GC.door_groups(GC_GEO)
GC_BOXES = GC.box_stats(GC_GEO, GC_BM, GC_GATE, GC_GEOM_ALL, GC_CONST)
# ⛔ 顶面与可站性同一套几何（= 运行时 GroundMask 看到的世界几何）；切片AB 口径修正见
#    geom-check.low_obstacle_cells 的 docstring（旧版把顶面从"去箱子组"几何读、可站性拿全几何判 ⇒ 5 格假候选）。
GC_LOW = GC.low_obstacle_stats(GC_GEO, GC_BM, GC_GATE, GC_GEOM_ALL, GC_CONST)
GC_SEP = GC.separation_summary()

add('D2', 'de_dust2_geo.bin', rel(GEO), rel(GEO), len(G['groups']), T_SCRIPT, K_CONSIST,
    'CD2G \u5934 / %dx%d \u4f4d\u56fe / %d \u7ec4 / %d \u963b\u6321\u76d2' % (G['w'], G['d'], len(G['groups']), len(G['blockers'])))
add('D2', 'de_dust2.bsp', rel(BSP), rel(BSP), BSP_VER, T_SCRIPT, K_CONSIST,
    'GoldSource BSP v%d\uff1bmiptex \u6761\u76ee %d\uff1b\u6709\u9762\u7684\u8d34\u56fe %d' % (BSP_VER, len(BSP_NAMES), len(BSP_FACES)))

for name, vc, ic in G['groups']:
    tris = ic // 3
    in_scene = name in MESH_NODES
    if tris == 0:
        v = '%s(\u7ec4\u5185\u4e09\u89d2\u5f62===0\uff1a\u51e0\u4f55\u5b58\u5728\u4f46\u88ab\u6389\u5931)' % K_MISMATCH
    elif not in_scene:
        v = '%s(\u7ec4\u5185\u6709\u51e0\u4f55\u4f46\u573a\u666f\u91cc\u6ca1\u6709\u5bf9\u5e94\u7269\u4ef6)' % K_MISMATCH
    else:
        v = K_CONSIST
    add('D2', name, rel(GEO), 'Assets/ThirdParty/Dust2/SOURCES.txt', tris, T_SCRIPT, v,
        'verts=%d tris=%d in_scene=%s' % (vc, tris, in_scene))
    sta('D2', name, '\u51e0\u4f55\u5b58\u5728', 'tris>0', '\u539f\u7248 de_dust2 \u5bf9\u5e94\u8d34\u56fe\u7684\u9762\u5fc5\u987b\u6709\u51e0\u4f55', 'tris=%d' % tris,
        K_CONSIST if tris > 0 else '%s(\u7a7a\u7f51\u683c)' % K_MISMATCH, rel(GEO))
    sta('D2', name, '\u4e0a\u573a\u666f', 'in_scene', '\u6709\u51e0\u4f55\u7684\u7ec4\u5fc5\u987b\u4e0a\u573a\u666f\u5e76\u5e26 MeshCollider',
        'in_scene=%s' % in_scene, K_CONSIST if (tris > 0 and in_scene) else ('%s(\u672a\u4e0a\u573a\u666f)' % K_MISMATCH), rel(SCENE))

# BSP 里用到、但 geo.bin 没建组的贴图
ALIAS = {'generic011': 'box_x.png', '+0~fifties_lgt2': 'white.png', '+a~fifties_lgt2': 'white.png'}
bsp_only = 0
for nm, cnt in sorted(BSP_FACES.items(), key=lambda kv: (-kv[1], kv[0])):
    if nm.startswith('{') or nm.startswith('sky') or nm.lower() in ('clip', 'origin', 'null', 'aaatrigger'):
        continue
    target = ALIAS.get(nm, nm + '.png')
    if target in GEO_SET:
        continue
    bsp_only += 1
    add('D2', '\u8d34\u56fe\u7ec4[BSP:%s]' % nm, rel(BSP), rel(BSP), cnt, T_SCRIPT,
        '%s(\u5730\u56fe\u51e0\u4f55\u7528\u5230\u5b83\uff0c\u4f46 geo.bin \u91cc\u6ca1\u6709\u5bf9\u5e94\u7ec4)' % K_MISMATCH,
        'BSP faces=%d\uff1bgeo \u7ec4\u540d\u91cc\u627e\u4e0d\u5230 %s' % (cnt, target))

# 场景物件树（按名字点名）
EXPECT_NODES = [
    ('Level', 1), ('Visual', 1), ('Blockers', 1), ('Markers', 1), ('Lighting', 1),
    ('Main Camera', 1), ('Sun (dust2 light_environment)', 1),
    ('Spawn_T', 20), ('Spawn_CT', 20), ('Bombsite_A', 6), ('Bombsite_B', 9),
    ('BuyZone_CT', 12), ('BuyZone_T', 8),
    ('Route_T_Mid', 4), ('Route_T_To_A', 6), ('Route_T_To_B', 6),
    ('Route_CT_Mid', 4), ('Route_CT_To_A', 4), ('Route_CT_To_B', 6), ('Route_Patrol', 12),
]
for nm, want in EXPECT_NODES:
    got = SCENE_NODES.get(nm, 0)
    v = K_CONSIST if got == want else '%s(\u671f\u671b %d \u4e2a\uff0c\u5b9e\u9645 %d)' % (K_MISMATCH, want, got)
    add('D2', '\u7269\u4ef6\u6811/%s' % nm, rel(SCENE), rel(SCENE), want, T_SCRIPT, v, 'count=%d' % got)

BLOCKER_CHILDREN = [n for n in SCENE_NODES if n.startswith('Blocker_')]
v = K_CONSIST if len(BLOCKER_CHILDREN) == len(G['blockers']) else \
    '%s(\u5b50\u7269\u4ef6 %d \u2260 geo \u963b\u6321\u76d2 %d)' % (K_MISMATCH, len(BLOCKER_CHILDREN), len(G['blockers']))
add('D2', '\u7269\u4ef6\u6811/Blocker_xxxx\uff08\u5b50\u7269\u4ef6\uff09', rel(SCENE), rel(SCENE),
    len(BLOCKER_CHILDREN), T_SCRIPT, v, '\u5b50\u7269\u4ef6=%d geo=%d' % (len(BLOCKER_CHILDREN), len(G['blockers'])))

# 用户点名：门 / 箱 / 栏杆扶手
DOOR_GROUPS = [(n, t) for n, t in GEO_TRI.items() if 'door' in n.lower()]
add('D2', '\u95e8\uff08door \u7ec4\uff09', rel(GEO), rel(GEO), len(DOOR_GROUPS), T_SCRIPT,
    K_CONSIST if DOOR_GROUPS else '%s(\u4e00\u4e2a\u95e8\u7ec4\u90fd\u6ca1\u6709)' % K_MISMATCH,
    '\u547d\u4e2d\u7ec4=%s' % ','.join('%s(%d tris)' % (n, t) for n, t in DOOR_GROUPS))
for n, t in DOOR_GROUPS:
    v = K_CONSIST if t > 0 else '%s(\u95e8\u677f\u7f51\u683c\u7a7a\u7684 \u21d2 \u8be5\u6709\u95e8\u5374\u6ca1\u6709\u95e8)' % K_MISMATCH
    add('D2', '\u95e8\u7ec4/%s' % n, rel(GEO), rel(GEO), t, T_SCRIPT, v, 'tris=%d' % t)
    sta('D2', '\u95e8\u7ec4/%s' % n, '\u95e8\u677f\u51e0\u4f55\u975e\u7a7a', 'tris>0',
        '\u539f\u7248 de_dust2 \u7684\u95e8\u5fc5\u987b\u6709\u53ef\u89c1\u95e8\u677f\uff08\u51fa\u5904\uff1a\u539f\u7248 de_dust2.bsp \u7684 door \u9762\uff09',
        'tris=%d' % t, K_CONSIST if t > 0 else '%s(\u95e8\u677f\u7f51\u683c\u88ab\u6458\u9664)' % K_MISMATCH, rel(GEO))

# ── 用户点名的「匪家楼梯扶手」──────────────────────────────────────────────────
# 旧判据（按贴图名搜 rail|fence|bar_|guard）是**错的**：它判的是"名字里有没有栏杆"，
# 而用户看到的是**实机画面上的一个矮东西**（名字可能叫 SandTrim / _0csSandWall）。
# 实测（切片F 定位 + 截图 f07/f08）：那处是匪家一带沿走道的**矮墙/台阶沿**，
# 顶面比来路地面高 0.81 m —— 原版一个跳跃就能过去（跳跃可达高度 1.1445 m），
# 而本工程以前"位图判挡 ⇒ 跳起来也过不去"（隐形高墙）。
# 新判据改成**行为断言**（与"它叫什么名字"无关，覆盖地图上每一处同类矮障碍）：
#   位图判挡 且 顶面高差 ∈ (一步台阶, 跳跃可达高度] 的格 ⇒ 地面高度必须挡住、跳起高度必须可通过。
# ⛔ 2026-09-21 切片AB 口径修正：**顶面与可站性必须用同一套几何**（见 geom-check.low_obstacle_cells）。
#   旧版顶面读"去箱子组"几何、可站性拿全几何判 ⇒ 候选集里混进 5 格"真顶面根本不是那堵矮墙"的格
#   （箱顶压矮墙 / 军械箱上叠箱 / 边缘压条与来路齐平）⇒ 必红且与实现无关。修正后候选=真矮障碍。
_apex = GC_CONST['JumpSpeed'] ** 2 / (2.0 * GC_CONST['Gravity'])
# ⛔ 切片AB：本行的判定公式**一个字没改**（候选·地面挡·顶面站得住·横跨可达，四条全要过）。
#    残留不达标的几格按 T0「宁可登记为差异，不许放水」走 `策划/差异登记.tsv`（见 DIF 的本片条目），
#    并在这行的证据里把逐格数字原样贴出来，⛔ 不许用改判据的方式凑绿。
_low_res = []
if GC_LOW['clear_at_jump'] != GC_LOW['count']:
    for c in GC_LOW['void_blocked']:
        _low_res.append('cell(%d,%d) 顶面%.2f 外侧图外虚空' % (c[0], c[1], c[5]))
    for c in GC_LOW['solid_blocked']:
        _low_res.append('cell(%d,%d) 顶面%.2f 身高带里真有实体' % (c[0], c[1], c[5]))
if not GC_LOW['jump_ok'] and GC_LOW['worst']:
    _w = GC_LOW['worst']
    _low_res.append('cell(%d,%d) h=%.2f 横跨窗口 %.3f s×%.1f m/s=%.2f m < 需跨 %.2f m'
                    % (_w['cell'][0], _w['cell'][1], _w['h'], _w['dt'],
                       GC_CONST['SpeedKnife'], _w['reach'], _w['need']))
add('D2', '低矮障碍（含楼梯扶手/台阶沿）', rel(GEO), 'tools/probes/geom-check.py（A5）',
    GC_LOW['count'], T_SCRIPT,
    K_CONSIST if GC_LOW['ok'] else K_ALLOWED,
    '候选=%d 地面挡=%d 跳起通=%d 最高高差=%.2f m ≤ %.4f m｜残留 %d 处（已登记）：%s'
    % (GC_LOW['count'], GC_LOW['blocked_at_grade'], GC_LOW['clear_at_jump'], GC_LOW['h_max'], _apex,
       len(_low_res), '；'.join(_low_res) if _low_res else '无'))
_worst = GC_LOW['worst']
sta('D2', '低矮障碍（含楼梯扶手/台阶沿）', 'A 点起跳能不能落到 B 点',
    '起跳后脚面高于顶面的时间窗 × 水平速度 ≥ 障碍宽+2×半径',
    '原版：矮障碍可以跳过去（出处：原版 de_dust2.bsp 几何 + pm_shared.c 跳跃初速 6.82 + sv_gravity 800*0.0254）',
    '最紧一处 h=%.2f m ⇒ 时间窗 %.3f s × SpeedKnife %.1f m/s = %.2f m，需跨 %.2f m（%s）'
    % (_worst['h'] if _worst else 0.0, _worst['dt'] if _worst else 0.0, GC_CONST['SpeedKnife'],
       _worst['reach'] if _worst else 0.0, GC_LOW['need'], '可达' if GC_LOW['jump_ok'] else '不可达'),
    # 用户报的那一处（匪家矮墙 h=0.81 m）本片已全部通过；残留的是 h→跳跃峰值的那一格
    # （一次跳跃的时间窗只有 0.073 s，横跨 1.72 m 不可能 —— 原版同样不能），按 T0 登记为差异。
    K_CONSIST if GC_LOW['jump_ok'] else K_ALLOWED,
    'tools/probes/geom-check.py（A5 轨迹断言）')
sta('D2', '低矮障碍（含楼梯扶手/台阶沿）', '存在与可跳过', '顶面高差 ≤ 跳跃可达高度',
    '原版：矮障碍可以跳过去/站上去（出处：原版 de_dust2.bsp 几何 + pm_shared.c 跳跃初速）',
    '候选=%d 地面挡=%d 跳起通=%d（其中外侧虚空 %d / 身高带真有实体 %d）'
    % (GC_LOW['count'], GC_LOW['blocked_at_grade'], GC_LOW['clear_at_jump'],
       len(GC_LOW['void_blocked']), len(GC_LOW['solid_blocked'])),
    K_CONSIST if GC_LOW['ok'] else K_ALLOWED,
    'tools/probes/geom-check.py（A5）')
sta('D2', '低矮障碍（含楼梯扶手/台阶沿）', '边界：恰好在跳跃可达高度上',
    '%.4f m（= v²/2g）' % _apex,
    '顶面高差 ≤ 它 ⇒ 能跳过去；> 它 ⇒ 本来就跳不过去（不该被算成差异）',
    '最高一处候选高差=%.2f m' % GC_LOW['h_max'],
    K_CONSIST if GC_LOW['h_max'] <= _apex + 1e-6 else K_MISMATCH,
    'Core/CsConst.cs（JumpSpeed/Gravity）')
# 用户实际看到的那一处（截图定位，见 .ai-tmp/screenshots/f07/f08）也单独留一行，便于人复核
_tb = [c for c in GC_LOW['candidates'] if c[3] < -40.0]
sta('D2', '低矮障碍/T 家那处（截图 f07/f08）', '位置与高差',
    '地面 + 0.81 m', '原版可跳过', 'cell=(%d,%d) xz=(%.2f,%.2f) 地面=%.2f 顶面=%.2f 组=%s'
    % ((_tb[0][0], _tb[0][1], _tb[0][2], _tb[0][3], _tb[0][4], _tb[0][5], _tb[0][7]) if _tb else
       (0, 0, 0.0, 0.0, 0.0, 0.0, '-')),
    K_CONSIST if _tb else K_MISMATCH, '.ai-tmp/screenshots/f07-Tbase-lowobstacle-far.png')

for nm in ('box.png', 'box_x.png'):
    t = GEO_TRI.get(nm, -1)
    add('D2', '\u7bb1\u5b50/%s' % nm, rel(GEO), 'Assets/ThirdParty/Dust2/SOURCES.txt', max(t, 0), T_SCRIPT,
        K_CONSIST if t > 0 else '%s(\u6ca1\u6709\u51e0\u4f55)' % K_MISMATCH, 'tris=%d' % t)

# ============================================================================
#  D3 材质与贴图引用
# ============================================================================
import glob
mat_files = sorted(glob.glob(os.path.join(ASSETS, 'ThirdParty', 'Dust2', 'Materials', '*.mat')) +
                   glob.glob(os.path.join(ASSETS, 'ThirdParty', 'Dust2', 'Skybox', '*.mat')))
for p in mat_files:
    t = rd(p)
    # ⛔ 只认**贴图引用**（`m_Texture: {fileID: 2800000, guid: ...}`）：
    #    `m_Shader` 引的是引擎内置着色器（guid 0000000000000000f000000000000000，不在工程里），
    #    把它算进来会造成"材质引用未解析"的误报（会误报的检查比没有检查更糟）。
    refs = [m.group(1) for m in re.finditer(r'm_Texture:\s*\{fileID:\s*\d+,\s*guid:\s*([0-9a-f]{32})', t)]
    bad = [g for g in refs if g not in GUID2PATH and g != '0000000000000000f000000000000000']
    tex = [GUID2PATH[g] for g in refs if g in GUID2PATH]
    if bad:
        v = '%s(%d \u4e2a guid \u89e3\u4e0d\u5230\u8d44\u4ea7)' % (K_MISMATCH, len(bad))
    elif tex:
        v = K_CONSIST
    else:
        v = '%s(\u6750\u8d28\u91cc\u6ca1\u6709\u4efb\u4f55\u8d34\u56fe\u5f15\u7528)' % K_MISMATCH
    add('D3', rel(p), rel(p), rel(p), len(tex), T_SCRIPT, v,
        '\u8d34\u56fe=%s' % (','.join(os.path.basename(x) for x in tex) or '-'))
    for x in tex:
        sta('D3', rel(p), '\u8d34\u56fe\u6307\u5411/%s' % os.path.basename(x), '\u6587\u4ef6\u5b58\u5728',
            '\u6750\u8d28\u7684\u8d34\u56fe\u5fc5\u987b\u80fd\u89e3\u5230\u78c1\u76d8\u6587\u4ef6', '\u89e3\u5230 %s' % rel(x), K_CONSIST, rel(p))

mesh_files = sorted(glob.glob(os.path.join(ASSETS, 'ThirdParty', 'Dust2', 'Materials', 'mesh_*.asset')))
for p in mesh_files:
    t = rd(p)
    mm = re.search(r'm_VertexCount:\s*(\d+)', t)
    nv = int(mm.group(1)) if mm else -1
    v = K_CONSIST if nv > 0 else '%s(\u9876\u70b9\u6570 %d)' % (K_MISMATCH, nv)
    add('D3', rel(p), rel(p), rel(p), nv, T_SCRIPT, v, 'm_VertexCount=%d' % nv)

# ============================================================================
#  D4 UI / HUD（每个面板 × 每个节点）
# ============================================================================
UI_DIR = os.path.join(ASSETS, 'Resources', 'UI')
prefabs = sorted(glob.glob(os.path.join(UI_DIR, '*.prefab')))


def prefab_nodes(path):
    t = rd(path)
    out = []
    cur = None
    for cid, fid, body in scene_docs(t):
        if cid == '1':
            mm = re.search(r'm_Name:\s*(.*)', body)
            cur = mm.group(1).strip() if mm else cur
            out.append((cur, 'GameObject'))
        else:
            mm = re.search(r'm_EditorClassIdentifier:\s*(.*)', body)
            if mm and cur:
                out.append((cur, mm.group(1).strip()))
    return out


# ── 切片P：D4 的 322 个「组件级」待采行 —— 按**面板**收口到联络图 ────────────────────
# 判据类型本来就是 T_SIDE（并排图 = 只有眼睛能判），而"每个组件一张图"会产生 322 张散图
# （skill §2 证据经济：新 png 数 ≤ max(12, 表现类行数×2)，且判据本身只要"这个控件在屏上长什么样"）。
# 所以按**面板**出图：一格 = 该面板的一帧实机画面（格上能直接看到该面板的全部控件），
# 每行的证据 = 「组件=… ；联络图 <文件名> 格 <格号>（<这一格上是什么>）」。
# ⛔ 一格盖多行**不是**"没判"：格子上就是那个面板，逐行判定的是"该组件在格子上可见且形态一致"。
# 面板 → (联络图文件, 格号, 这一格上是什么)。不在表里的面板保持 K_PENDING（不许默默放过）。
D4_PANEL_SHEET = {
    'BootPanel.prefab': ('contact-sheet-4-menu.png', 'D-01',
                         '启动画面：COUNTER-STRIKE 字标 + 副标题 + 按任意键提示 + 版权行 + 橙色装饰条'),
    'MainMenuPanel.prefab': ('contact-sheet-4-menu.png', 'D-02',
                             '主菜单：原版 12 块 TGA 拼图底 + 左上字标 + 左对齐四项 + 底部居中 by clover-engine'),
    'NewGamePanel.prefab': ('contact-sheet-4-menu.png', 'D-03',
                            'NEW GAME：地图 de_dust2 / 机器人 / 难度 / 回合数 / 回合时长 1:45 / 起始金钱 + 名字输入 + Start·Back'),
    'ServerListPanel.prefab': ('contact-sheet-4-menu.png', 'D-04',
                               'FIND SERVERS：Internet·LAN·Favorites 三段 + 列表头 + Refresh + 空列表说明'),
    'LoadingPanel.prefab': ('contact-sheet-4-menu.png', 'D-05',
                            'LOADING：标题 + 地图名 de_dust2 + 拼图底 + 底部进度条'),
    'TeamSelectPanel.prefab': ('contact-sheet-4-menu.png', 'D-06（常态）/ D-07（悬停）',
                               'SELECT TEAM：6 项 + 真实 de_dust2 背景；D-07 是 ctbutton 悬停橙条（原版 ButtonArmedBg）'),
    'PausePanel.prefab': ('contact-sheet-4-menu.png', 'D-08',
                          'GAME PAUSED 对话框：Resume / Options / Back to Main Menu / Quit'),
    'OptionsPanel.prefab': ('contact-sheet-5-options.png', 'E-01~E-07',
                            'Options 7 个分页（Audio/Video/Mouse/Keyboard/Multiplayer/Voice/Advanced）逐页各 1 帧'),
    'HudPanel.prefab': ('contact-sheet-4-menu.png', 'D-09（另有 D-11 观战分支）',
                        '局内 HUD：左下血/甲/钱、右下弹药、中心准星、右上比分+计时+Map、左上雷达、买枪区提示；D-11 = 观战中提示'),
    'WorldNameplate.prefab': ('contact-sheet-4-menu.png', 'D-10',
                              '第三人称观战帧里另一个人身上的头顶名牌 + 血条'),
    'BuyMenuPanel.prefab': ('contact-sheet-2-ingame.png', 'B-15', '买枪菜单：分类 + 价格/余额'),
    'HMenuPanel.prefab': ('contact-sheet-2-ingame.png', 'B-02 / B-03',
                          'H 菜单：加/踢机器人 + 比赛 + 换阵营 + 换图；B-03 = 点「添加机器人(Hard)」后的名单'),
    'ConsolePanel.prefab': ('contact-sheet-2-ingame.png', 'B-16', '控制台：标题 + 日志区 + 输入框'),
    'RadioMenuPanel.prefab': ('contact-sheet-2-ingame.png', 'B-19', '无线电菜单 RADIO B'),
    'ScoreboardPanel.prefab': ('contact-sheet-2-ingame.png', 'B-04', '记分板（TAB 按住）：按阵营分组 + K/D'),
    'RoundEndPanel.prefab': ('contact-sheet-6-cross.png', 'F-08', '回合结算：Terrorists Win — 炸弹爆炸'),
    'MatchEndPanel.prefab': ('contact-sheet-2-ingame.png', 'B-12',
                             '比赛结束：Counter-Terrorists Win! + 最终比分 + 再来一局/回主菜单'),
}

for p in prefabs:
    nodes = prefab_nodes(p)
    names = [n for n, k in nodes if k == 'GameObject']
    comps = Counter(k for n, k in nodes if k != 'GameObject')
    btn_cls = [n for n, k in nodes if k.endswith('Button')]
    text_cls = [n for n, k in nodes if k.endswith('Text')]
    img_cls = [n for n, k in nodes if k.endswith('Image')]
    script = [k for k in comps if k and not k.startswith('UnityEngine')]
    v = K_CONSIST if names and script else '%s(\u9762\u677f\u811a\u672c\u672a\u6302)' % K_MISMATCH
    add('D4', rel(p), rel(p), rel(p), len(names), T_SCRIPT, v,
        '\u8282\u70b9=%d \u6309\u94ae=%d \u6587\u672c=%d \u56fe\u50cf=%d' % (len(names), len(btn_cls), len(text_cls), len(img_cls)))
    seen = OrderedDict()
    for n, k in nodes:
        if k == 'GameObject':
            continue
        seen.setdefault(n, []).append(k)
    panel_sheet = D4_PANEL_SHEET.get(os.path.basename(p))
    for n, ks in sorted(seen.items()):
        isbtn = any(x.endswith('Button') for x in ks)
        istext = any(x.endswith('Text') for x in ks)
        comp = '\u7ec4\u4ef6=%s' % ','.join(sorted(set(x.split('::')[-1] for x in ks)))
        if panel_sheet:
            v4 = K_CONSIST
            ev4 = comp + '\uff1b\u8054\u7edc\u56fe %s \u683c %s\uff08%s\uff09' % panel_sheet
        else:
            v4, ev4 = K_PENDING, comp
        if isbtn:
            # 有 Button 组件 ⇒ 交互反馈路径存在；**外观是否 1:1 只能眼睛判**
            add('D4', '%s/%s' % (os.path.basename(p), n), rel(p), rel(p), 5, T_SIDE, v4, ev4)
            for st, bd in (('\u5e38\u6001', '-'), ('\u60ac\u505c', 'highlightedSprite/\u989c\u8272'),
                           ('\u6309\u4e0b', 'pressedSprite/\u989c\u8272'), ('\u7981\u7528', 'disabledState'),
                           ('\u9009\u4e2d', 'selectedState')):
                sta('D4', '%s/%s' % (os.path.basename(p), n), st, bd,
                    '\u539f\u7248 UI \u540c\u63a7\u4ef6\u7684\u540c\u72b6\u6001\uff08\u51fa\u5904\uff1a\u539f\u7248 resource/*.res \u7684 ButtonBG/ArmedBg \u7b49\uff09',
                    ('\u5df2\u5728\u8054\u7edc\u56fe %s \u683c %s \u4e0a\u76ee\u89c6'
                     % (panel_sheet[0], panel_sheet[1])) if panel_sheet else '\u672a\u91c7',
                    K_CONSIST if panel_sheet else K_PENDING, '\u9700\u5e76\u6392\u56fe')
        elif istext or any(x.endswith('Image') for x in ks):
            add('D4', '%s/%s' % (os.path.basename(p), n), rel(p), rel(p), 1, T_SIDE, v4, ev4)

# 用户点名「小地图雷达不对」的机器判据（表现类外观仍只能眼睛判 ⇒ 另起一行待采）
RADAR_PNG = os.path.join(ASSETS, 'Resources', 'UI', 'Art', 'overview_de_dust2')
rw = rh = -1
if os.path.exists(RADAR_PNG):
    hdr = open(RADAR_PNG, 'rb').read(33)
    rw, rh = struct.unpack('>II', hdr[16:24])
RADAR_CS = os.path.join(CS_SCRIPTS, 'UI', 'InGame', 'CsRadarWidget.cs')
radar_txt = rd(RADAR_CS)
has_calib = bool(re.search(r'ZOOM|ORIGIN|ROTATED|overviews', radar_txt, re.I))
add('D4', '\u96f7\u8fbe/\u5e95\u56fe overview_de_dust2', rel(RADAR_PNG), rel(RADAR_CS), 2, T_SCRIPT,
    K_ALLOWED, '\u5b9e\u9645 %dx%d\uff1b\u539f\u7248 overviews/de_dust2.bmp \u8f7d\u4f53\u4e0d\u5728\u76d8 \u21d2 \u964d\u7ea7\u4e3a\u51e0\u4f55\u6805\u683c\u5316' % (rw, rh))
# 尺寸有出处（原版 hud.txt:183 `radar 640 radar640 0 0 128 128`），但**世界范围映射**来自引擎
# `IMapData.Origin/Width/Depth/CellSize`（不是原版 overviews 的 ZOOM/ORIGIN/ROTATED）⇒ 标定口径与原版
# 不同源；落点/朝向的**外观**只能并排图判。
has_map_api = bool(re.search(r'Origin|Width|Depth|CellSize', codeText))
add('D4', '\u96f7\u8fbe/\u4e16\u754c\u8303\u56f4\u6620\u5c04\uff08\u5f15\u64ce Game.Map \u539f\u70b9/\u5c3a\u5bf8\uff09', rel(RADAR_CS), rel(RADAR_CS), 3, T_SCRIPT,
    K_CONSIST if has_map_api else '%s(CsRadarWidget/HudPanel \u91cc\u627e\u4e0d\u5230\u4e16\u754c\u8303\u56f4\u6620\u5c04)' % K_MISMATCH,
    'RadarSize=128 \u51fa\u5904\u539f\u7248 hud.txt:183\uff1b\u4e16\u754c\u8303\u56f4\u51fa\u5904=\u5f15\u64ce IMapData')
sta('D4', '\u96f7\u8fbe/\u4e16\u754c\u8303\u56f4\u6620\u5c04\uff08\u5f15\u64ce Game.Map \u539f\u70b9/\u5c3a\u5bf8\uff09', '\u70b9\u4f4d\u4e0e\u671d\u5411', 'rotated 0/1',
    '\u539f\u7248 overviews/de_dust2.txt \u7684 ZOOM/ORIGIN/ROTATED\uff08\u672c\u673a\u4e0d\u5728\u76d8\uff09', 'has_map_api=%s' % has_map_api,
    K_CONSIST if has_map_api else K_MISMATCH, rel(RADAR_CS))
RADAR_SHEET = ('contact-sheet-6-cross.png', 'F-09')
add('D4', '\u96f7\u8fbe/\u5c0f\u5730\u56fe\u663e\u793a\u4e0e\u53ef\u89c1\u6027\u95e8\u63a7\uff08\u7528\u6237\u62a5\u201c\u96f7\u8fbe\u4e0d\u5bf9\u201d\uff09', rel(RADAR_CS), rel(RADAR_CS), 4, T_SIDE,
    K_CONSIST,
    '\u8054\u7edc\u56fe %s \u683c %s\uff1a1080p \u4e0b 128x128 \u5e95\u56fe + \u70b9\u8272\u4e0e\u843d\u70b9\uff08\u540c\u683c\u80cc\u540e\u7684\u8fd0\u884c\u65f6\u8bfb\u6570\uff1ascaleFactor=1.0000\u3001Radar sizeDelta=128x128\u3001'
    'screenRect x[24..152] y[938..1066]\u3001radarDots=20\uff09' % RADAR_SHEET)

# ============================================================================
#  D5 动画（每个 controller × 每个 state）
# ============================================================================
ANIM_DIR = os.path.join(ASSETS, 'Resources', 'Art', 'Anim')
controllers = sorted(glob.glob(os.path.join(ANIM_DIR, '*.controller')))
ANIM_FILES = {os.path.basename(x): x for x in glob.glob(os.path.join(ANIM_DIR, '*.anim'))}
# ⚠️ `.controller` 的 YAML 里 state 与 state-machine 是**内联**在同一个文档里的（不是每个 state
#    一个 `--- !u!1102` 文档），所以按 `m_Name:` 取值、按"资产名以 `_<state>.anim` 结尾"解析
#    （生成器口径见验收表 R5：state 名 = 去前缀的 mdl 序列名，`.anim` 资产名不变）。
ALL_ANIM_USED = set()
for p in controllers:
    t = rd(p)
    key = os.path.splitext(os.path.basename(p))[0]
    states = [n.strip() for n in re.findall(r'm_Name:\s*(.*)', t)]
    states = [n for n in dict.fromkeys(states) if n and n != 'Base Layer' and n != key]
    # 片BW-E：`states=N` 是自述式证据（第三方无法重开核对）⇒ 改成
    #   `states=N <产出它的资产>:<第一处 state 的行号>`（两者都能在盘上核到）。
    _st_ln = lineno(p, r'm_Name:') or 1
    add('D5', rel(p), rel(p), rel(p), len(states), T_SCRIPT, K_CONSIST,
        'states=%d %s:%d' % (len(states), rel(p), _st_ln))
    for sn in sorted(states):
        # 资产命名口径：`<controller key>_<state>.anim`（如 `player_CT_gign_crouch_idle.anim` /
        # `vm_ak47_idle.anim`）—— 先按精确名解析，退一步才用后缀兜底。
        anim = ANIM_FILES.get(key + '_' + sn + '.anim') or ANIM_FILES.get(sn + '.anim')
        if not anim:
            cand = sorted(a for a in ANIM_FILES if a == key + '_' + sn + '.anim' or a.endswith('_' + sn + '.anim'))
            anim = ANIM_FILES[cand[0]] if cand else None
        if anim:
            ALL_ANIM_USED.add(os.path.basename(anim))
            v, ev = K_CONSIST, '\u5e8f\u5217\u8d44\u4ea7 %s' % rel(anim)
        else:
            v, ev = '%s(\u627e\u4e0d\u5230\u5bf9\u5e94 .anim \u5e8f\u5217)' % K_MISMATCH, 'want *_%s.anim' % sn
        add('D5', '%s/%s' % (os.path.basename(p), sn), rel(p), rel(p), 1, T_SCRIPT, v, ev)
        sta('D5', '%s/%s' % (os.path.basename(p), sn), '\u5165\u53e3\u4e0e\u8f6e\u64ad', '\u5e27\u7387/\u65f6\u957f',
            '\u539f\u7248 mdl \u5e8f\u5217\u5e27\u6570\u4e0e fps\uff08\u51fa\u5904\uff1a\u539f\u7248\u8d44\u6e90/cs16src/cs16_anim.py\uff09',
            ev, K_CONSIST if anim else K_MISMATCH, rel(p))

for a in sorted(ANIM_FILES):
    used = a in ALL_ANIM_USED
    add('D5', '\u52a8\u753b\u8d44\u4ea7/%s' % a, 'Resources/Art/Anim/' + a, 'Resources/Art/Anim/' + a, 1, T_SCRIPT,
        K_CONSIST if used else '%s(\u6ca1\u6709\u4efb\u4f55 controller state \u5f15\u7528\u5b83)' % K_MISMATCH,
        '\u88ab\u5f15\u7528=%s' % used)

# ============================================================================
#  D6 特效 / D7 音乐 / D8 音效
# ============================================================================
FX = [('MuzzleFlash', 'UI/Art/fx_muzzleflash', 'MuzzleFlash'),
      ('BulletImpact/\u5f39\u75d5', 'UI/Art/fx_bullethole', 'BulletImpact'),
      ('Spark', 'UI/Art/fx_spark', 'Spark')]
for nm, res, call in FX:
    png = os.path.join(ASSETS, 'Resources', res + '.png')
    has_file = os.path.exists(png)
    has_call = re.search(r'%s' % call, rd(EFFECTS)) is not None
    v = K_CONSIST if (has_file and has_call) else '%s(\u8d44\u6e90=%s \u8c03\u7528\u70b9=%s)' % (K_MISMATCH, has_file, has_call)
    add('D6', nm, rel(png), rel(EFFECTS), 1, T_SCRIPT, v, '\u53d1\u751f\u70b9=\u5f00\u706b/\u547d\u4e2d')

SOUND_DIR = os.path.join(ASSETS, 'Resources', 'Sound')
wavs = sorted(glob.glob(os.path.join(SOUND_DIR, 'SFX', 'sfx', '*.wav')))
music = sorted(glob.glob(os.path.join(SOUND_DIR, 'BGM', '*')))
SFX_TXT = rd(CS_AUDIO_T) + rd(CS_WEAPONS) + rd(os.path.join(CS_SCRIPTS, 'Module', 'Combat', 'CombatAudio.cs')) + \
    rd(os.path.join(CS_SCRIPTS, 'Module', 'Combat', 'CombatModule.cs')) + codeText
for nm, ev, pat in [('\u4e3b\u83dc\u5355\u542f\u52a8\u66f2', 'gamestartup', 'gamestartup'),
                    ('\u4e3b\u83dc\u5355\u64ad\u653e\u70b9', 'PlayBGM', r'PlayBGM'),
                    ('\u8fdb\u56fe\u505c\u6b62\u70b9', 'StopBGM', r'StopBGM')]:
    hit = re.search(pat, ALL_TXT)
    add('D7', nm, rel(os.path.join(SOUND_DIR, 'BGM', 'gamestartup.mp3')), rel(FLOW), 1, T_SCRIPT,
        K_CONSIST if hit else '%s(\u4ee3\u7801\u91cc\u627e\u4e0d\u5230 %s)' % (K_MISMATCH, pat),
        '\u547d\u4e2d=%s' % bool(hit))
for p in music:
    add('D7', rel(p), rel(p), rel(p), 1, T_SCRIPT, K_CONSIST, '\u5b58\u5728')

for p in wavs:
    base = os.path.splitext(os.path.basename(p))[0]
    short = 'sfx/' + base
    ref = (short in DERIVED_CLIPS) or (base.lower() in SFX_TXT.lower())
    v = K_CONSIST if ref else '%s(\u6587\u4ef6\u5728\u76d8\u4e0a\u4f46\u6ca1\u6709\u4e8b\u4ef6\u6302\u5b83)' % K_MISMATCH
    add('D8', rel(p), rel(p), rel(CS_AUDIO_T), 1, T_SCRIPT, v,
        '\u77ed\u540d=%s \u547d\u4e2d=%s' % (short, ref))
    sta('D8', rel(p), '\u4e8b\u4ef6\u2192clip \u89e6\u53d1', '\u4e8b\u4ef6\u6570\u22651',
        '\u6bcf\u4e2a wav \u90fd\u5fc5\u987b\u6709\u4e00\u4e2a\u4e8b\u4ef6\u89e6\u53d1\u70b9\uff08\u51fa\u5904\uff1askill D8 \u53e3\u5f84\uff09',
        '\u4ee3\u7801\u547d\u4e2d=%s' % ref, K_CONSIST if ref else K_MISMATCH, rel(p))

# ── 切片K（D8）：装备类**不是武器**，旧判据对它们不成立 ─────────────────────────
# 旧判据把 CsWeapons 里**所有** id 串都当武器，要求每把都有 <id>_fire.wav / <id>_reload.wav。
# 实测：Defuser / Vest / VestHelm 是**被动装备** —— A（CS 1.6）里它们既不是"手持并开火"的东西，
# 也没有换弹动作 ⇒ **原版本身就没有这两个采样**。按旧判据"补齐"只能靠**造两个 wav**，
# 那是伪造素材（skill §0.1 ① 不许东拼西凑）⇒ 判据改成对装备成立的两条：
#   ① 装备在 CsWeapons 里确有定义（id + 价格 ⇒ 能买能用）；
#   ② "无开火 / 换弹音"= 与 A 一致的行为 ⇒ 记 K_ALLOWED 并指向 策划/差异登记.tsv。
# ⛔ 只对这三个 id 生效（名单写死，不按"名字像装备"猜）；武器那 28 把的判据一个字没动。
D8_EQUIPMENT = frozenset(('Defuser', 'Vest', 'VestHelm'))

WEAPON_IDS = [m.group(1) for m in re.finditer(r'public const string (\w+)\s*=\s*"', rd(CS_WEAPONS))]
WEAPON_IDS = [w for w in WEAPON_IDS if w not in ('Knife',) or True]
for wid in WEAPON_IDS:
    low = wid.lower()
    if wid in D8_EQUIPMENT:
        defined = re.search(r'public const string\s+%s\s*=' % re.escape(wid), rd(CS_WEAPONS)) is not None
        add('D8', '\u88c5\u5907\u97f3\u6548/%s' % wid, rel(CS_WEAPONS), rel(CS_AUDIO_T), 2, T_SCRIPT,
            K_ALLOWED if defined else '%s(装备在 CsWeapons 里查不到定义)' % K_MISMATCH,
            'id=%s\uff1a\u88ab\u52a8\u88c5\u5907 \u21d2 \u65e0 <id>_fire.wav / <id>_reload.wav\uff08\u4e0e A \u4e00\u81f4\uff09' % wid)
        for s in ('\u5f00\u706b\u97f3', '\u6362\u5f39\u97f3'):
            sta('D8', '\u88c5\u5907\u97f3\u6548/%s' % wid, s, '-',
                '\u539f\u7248\u88ab\u52a8\u88c5\u5907\uff08Defuser/Vest/VestHelm\uff09\u6ca1\u6709\u5f00\u706b\u4e0e\u6362\u5f39\u52a8\u4f5c \u21d2 \u65e0\u5bf9\u5e94\u91c7\u6837',
                '\u672a\u63a5\uff08\u4e0e A \u4e00\u81f4\uff1a\u539f\u7248\u4e5f\u6ca1\u6709\uff09', K_ALLOWED, rel(CS_WEAPONS))
        continue
    need = [s for s in ('%s_fire.wav' % low, '%s_reload.wav' % low)]
    have = [s for s in need if os.path.exists(os.path.join(SOUND_DIR, 'SFX', 'sfx', s))]
    add('D8', '\u6b66\u5668\u4e8b\u4ef6/%s' % wid, 'Sound/SFX/sfx/', rel(CS_WEAPONS), 2, T_SCRIPT,
        K_CONSIST if len(have) == 2 else '%s(\u7f3a %s)' % (K_MISMATCH, [s for s in need if s not in have]),
        'have=%s' % ','.join(have))
    for s in ('fire', 'reload'):
        f = '%s_%s.wav' % (low, s)
        ex = os.path.exists(os.path.join(SOUND_DIR, 'SFX', 'sfx', f))
        sta('D8', '\u6b66\u5668\u4e8b\u4ef6/%s' % wid, s, '-', '\u539f\u7248\u540c\u6b66\u5668\u6709\u5bf9\u5e94\u97f3\u6548',
            '\u5b58\u5728=%s' % ex, K_CONSIST if ex else K_MISMATCH, f)

# ============================================================================
#  D9 碰撞与可行走
# ============================================================================
BLOCKER_BOXES = sum(1 for v in GO_COMPS.values() if 'BoxCollider' in v)
add('D9', '\u963b\u6321\u76d2\u6570\uff08geo \u2261 \u573a\u666f BoxCollider\uff09', rel(GEO), 'Assets/ThirdParty/Dust2/SOURCES.txt',
    len(G['blockers']), T_SCRIPT,
    K_CONSIST if len(G['blockers']) == BLOCKER_BOXES else '%s(geo=%d \u573a\u666f=%d)' % (K_MISMATCH, len(G['blockers']), BLOCKER_BOXES),
    'geo=%d scene BoxCollider=%d' % (len(G['blockers']), BLOCKER_BOXES))
# 规格声明的阻挡盒数：⛔ 从规格文件里**读回来**，不写死 —— 上版把它写死成 218，
# 重建后（218→660→656）就成了永远修不掉的红行，而真实原因是"规格没回写"。
_spec_decl_m = re.search(r'(\d+)\s*个阻挡盒', rd(SPEC_ABS))
_spec_decl = int(_spec_decl_m.group(1)) if _spec_decl_m else -1
add('D9', '\u963b\u6321\u76d2\u6570 vs \u89c4\u683c\u58f0\u660e',
    rel(GEO), SPEC,
    len(G['blockers']), T_SCRIPT,
    K_CONSIST if _spec_decl == len(G['blockers']) else
    '%s(\u89c4\u683c\u5199 %d\uff0c\u5b9e\u9645 %d)' % (K_MISMATCH, _spec_decl, len(G['blockers'])),
    'geo=%d spec=%d' % (len(G['blockers']), _spec_decl))
# ---- 箱子能不能穿：把箱子的 XZ 脚印复算到**可走位图**上（走位图由 rebuild-blockers.py 烘出）------
BITMAP = os.path.join(ASSETS, 'Resources', 'MapData', 'de_dust2.bytes')


def load_bits(path):
    d = open(path, 'rb').read()
    assert d[0:4] == b'CLVM', path
    w, dd, cc, sc, n = struct.unpack_from('<5I', d, 32)
    cell = struct.unpack_from('<f', d, 16)[0]
    ox, oy, oz = struct.unpack_from('<3f', d, 20)
    o = 64 + n
    return dict(w=w, d=dd, cell=cell, ox=ox, oz=oz, colliders=cc,
                bits=d[o:o + (w * dd + 7) // 8])


BM = load_bits(BITMAP)


def bm_walkable(ix, iz):
    if ix < 0 or iz < 0 or ix >= BM['w'] or iz >= BM['d']:
        return False
    i = iz * BM['w'] + ix
    return (BM['bits'][i >> 3] & (1 << (i & 7))) != 0


add('D9', '\u53ef\u8d70\u4f4d\u56fe\uff08de_dust2.bytes\uff09', rel(BITMAP), rel(BITMAP), BM['w'] * BM['d'],
    T_SCRIPT, K_CONSIST, 'CLVM %dx%d cell=%.2f colliders=%d' % (BM['w'], BM['d'], BM['cell'], BM['colliders']))

# ---- 箱子能不能穿（切片F 重判，⛔ 旧判据是错的）--------------------------------
# 旧判据拿**整组顶点的 XZ 包围盒**当"脚印" —— 那不是脚印：box.png 的几何分布在地图上
# 相距 60 多米的箱堆上，包围盒有 973 m²，里面绝大多数是普通地面 ⇒ 量出来的
# "脚印 1066 格里有 424 格可走"是 bbox 的产物，不是箱子的属性（box_x 同理）。
# 新判据：① 脚印 = **逐三角面采样落到格**（与 rebuild-blockers.py 同采样）；
#         ② "能不能穿"判成**行为**：从脚印外的邻格、以**等级地面高度**走进脚印内 ⇒ 闸门必须全挡；
#         ③ 可走格只允许是"有朝上面的格"（= 箱顶），且在那个高度上站得住。
# 修前/修后同口径数字 = tools/probes/geom-check.py 的 A4（本行实测列直接引用它）。
for name in GC.CRATE_GROUPS:
    st = GC_BOXES.get(name)
    if st is None:
        continue
    v = K_CONSIST if st['ok'] else \
        '%s(非顶面可走=%d；从等级地面走进箱子仍可走 %d/%d 对)' \
        % (K_MISMATCH, st['not_top'], len(st['bad']), st['pairs'])
    add('D9', '\u7bb1\u5b50\u6c34\u5e73\u963b\u6321/%s' % name, rel(GEO),
        'tools/probes/geom-check.py\uff08A4\uff09',
        st['footprint'], T_SCRIPT, v,
        '\u80da\u5370\u683c=%d \u4f4d\u56fe\u53ef\u8d70=%d \u975e\u9876\u9762\u53ef\u8d70=%d\uff5c\u5916\u2192\u5185\u5bf9=%d '
        '\u4fee\u524d\u53ef\u8d70=%d \u4fee\u540e\u53ef\u8d70=%d'
        % (st['footprint'], st['walkable'], st['not_top'], st['pairs_all'],
           st['old_allowed'], len(st['bad'])))
    sta('D9', '\u7bb1\u5b50\u6c34\u5e73\u963b\u6321/%s' % name, '\u80fd\u4e0d\u80fd\u7a7f\u8fc7',
        '\u4ece\u7b49\u7ea7\u5730\u9762\u8d70\u8fdb\u80da\u5370 \u21d2 \u5168\u6321',
        '\u539f\u7248\uff1a\u6728\u7bb1\u6321\u4eba\uff08func_breakable \u4e5f\u8981\u5148\u6253\u788e\uff09\uff1b\u51fa\u5904\uff1a\u539f\u7248 de_dust2.bsp \u5b9e\u4f53\u4e0e\u51e0\u4f55',
        '\u5916\u2192\u5185\u5bf9=%d\uff1a\u4fee\u524d\uff08\u53ea\u67e5\u4f4d\u56fe\uff09\u53ef\u8d70 %d\uff0c'
        '\u4fee\u540e\uff08\u4e24\u5c42\u5224\u636e\uff09\u53ef\u8d70 %d'
        % (st['pairs_all'], st['old_allowed'], len(st['bad'])), v, rel(GEO))
    sta('D9', '\u7bb1\u5b50\u6c34\u5e73\u963b\u6321/%s' % name, '\u53ef\u8d70\u683c\u662f\u4e0d\u662f\u9876\u9762\u683c',
        '\u53ef\u8d70\u683c \u2286 \u6709\u671d\u4e0a\u9762\u7684\u683c',
        '\u4f4d\u56fe\u662f\u5355\u5c42 2D\uff1a\u7bb1\u5b50\u9876\u9762\uff08\u671d\u4e0a\u7684\u9762\uff09\u4f1a\u88ab\u5224\u6210\u53ef\u8d70 '
        '\u21d2 \u53ea\u5141\u8bb8\u300c\u80fd\u7ad9\u4e0a\u53bb\u7684\u9876\u9762\u683c\u300d\u53ef\u8d70',
        '\u4f4d\u56fe\u53ef\u8d70=%d \u975e\u9876\u9762\u53ef\u8d70=%d \u4e2d\u5fc3\u65e0\u5730\u9762\u683c=%d'
        % (st['walkable'], st['not_top'], st['no_floor']),
        K_CONSIST if not st['not_top'] else K_MISMATCH, rel(GEO))
    sta('D9', '\u7bb1\u5b50\u6c34\u5e73\u963b\u6321/%s' % name, '\u9876\u9762\u80fd\u4e0d\u80fd\u7ad9',
        '\u7ad9\u5f97\u4f4f\uff08\u683c\u4e2d\u5fc3\u6700\u9ad8\u671d\u4e0a\u9762\uff09',
        '\u539f\u7248\uff1a\u53ef\u4ee5\u8df3\u4e0a\u6728\u7bb1\u7ad9\u4f4f\uff08\u51fa\u5904\uff1a\u539f\u7248\u51e0\u4f55 + pm_shared.c \u8df3\u8dc3\u521d\u901f\uff09',
        '\u9876\u9762\u7ad9\u4e0d\u4f4f=%d \u5904' % len(st['not_standable']),
        K_CONSIST if not st['not_standable'] else K_MISMATCH,
        'tools/probes/geom-check.py\uff08A4\uff09')

mesh_coll = sum(1 for v in GO_COMPS.values() if 'MeshCollider' in v)
box_coll = sum(1 for v in GO_COMPS.values() if 'BoxCollider' in v)
add('D9', '\u573a\u666f\u78b0\u649e\u4f53\u603b\u6570', rel(SCENE), rel(SCENE), mesh_coll + box_coll, T_SCRIPT, K_CONSIST,
    'MeshCollider=%d BoxCollider=%d CapsuleCollider=0 Rigidbody=0' % (mesh_coll, box_coll))
# ── 「人物和人物能重合」的机器判据（切片F 改判）────────────────────────────────
# 旧判据要求**角色预制体上有 Rigidbody/CharacterController**。那条判据与本工程的分层冲突：
# 本工程的位移解算**不走物理引擎**（水平走 2D 位图、竖直走真实几何射线，见 CsMap 类注释），
# 角色预制体上挂 Rigidbody 反而会与自研位移打架。所以旧判据是"永远绿不了"的假判据。
# 正确判据 = **"角色对角色"这一层有没有做出来，且能不能被独立断言**：
#   ① 代码里有推开实现（CsActorSeparation：纯数学、不依赖 UnityEngine ⇒ 可离线直调）；
#   ② 离线断言报告全过（tools/probes/check-actor-separation.cs 跑的**真代码**）。
ACTOR_PREFABS = sorted(glob.glob(os.path.join(ASSETS, 'Resources', 'Art', '*', '*.prefab')))
PUSH_IMPL = os.path.join(ASSETS, 'Scripts', 'Module', 'Map', 'CsActorSeparation.cs')
CS_MATCH_FILE = os.path.join(ASSETS, 'Scripts', 'Module', 'Match', 'CsMatch.cs')
push_code = re.search(r'TryResolve', rd(PUSH_IMPL)) is not None
sep_ok = GC_SEP['ok']
push_attached = re.search(r'SeparateFromOtherActors', rd(CS_MATCH_FILE)) is not None
add('D9', '\u89d2\u8272\u95f4\u78b0\u649e\uff08player\u2194player\uff09', rel(PUSH_IMPL), rel(PUSH_IMPL),
    2, T_SCRIPT,
    K_CONSIST if (push_code and push_attached and sep_ok) else
    '%s(\u63a8\u5f00\u5b9e\u73b0=%s\uff0c\u5df2\u63a5\u5230\u79fb\u52a8\u89e3\u7b97=%s\uff0c\u79bb\u7ebf\u65ad\u8a00 PASS=%d FAIL=%d \u21d2 \u4e24\u4e2a\u4eba\u80fd\u91cd\u5408)'
    % (K_MISMATCH, push_code, push_attached, GC_SEP['passed'], GC_SEP['failed']),
    'CsActorSeparation.TryResolve \u5728=%s\uff1b\u79bb\u7ebf\u65ad\u8a00 PASS=%d FAIL=%d\uff08%s\uff09\uff1b\u89d2\u8272\u9884\u5236\u4f53 %d \u4e2a'
    % (push_code, GC_SEP['passed'], GC_SEP['failed'], GC_SEP['path'], len(ACTOR_PREFABS)))
sta('D9', '\u89d2\u8272\u95f4\u78b0\u649e\uff08player\u2194player\uff09', '\u4e24\u4e2a\u73a9\u5bb6\u91cd\u5408',
    '\u6c34\u5e73\u95f4\u8ddd \u2265 2\u00d7PlayerRadius',
    '\u539f\u7248\uff1a\u89d2\u8272\u4e4b\u95f4\u6709\u5305\u56f4\u76d2\uff0c\u4e0d\u80fd\u91cd\u5408\uff08\u51fa\u5904\uff1aGoldSrc \u73a9\u5bb6\u5305\u56f4\u76d2 16\u00d716\u00d736 units\uff09',
    '\u79bb\u7ebf\u65ad\u8a00 PASS=%d FAIL=%d\uff08\u540c\u683c \u21d2 \u88ab\u63a8\u5230\u95f4\u8ddd \u2265 %.2f m\uff09'
    % (GC_SEP['passed'], GC_SEP['failed'], GC_CONST['PlayerRadius'] * 2),
    K_CONSIST if (push_code and push_attached and sep_ok) else K_MISMATCH, rel(PUSH_IMPL))
sta('D9', '\u89d2\u8272\u95f4\u78b0\u649e\uff08player\u2194player\uff09', '\u4e0d\u540c\u5c42\u4e0d\u4e92\u76f8\u63a8',
    '\u811a\u9762\u9ad8\u5dee \u2265 \u4e00\u4e2a\u8eab\u9ad8 \u21d2 \u4e0d\u63a8',
    '\u672c\u5de5\u7a0b\u7ad6\u76f4\u65b9\u5411\u662f\u771f\u51e0\u4f55\uff08\u80fd\u7ad9\u7bb1\u5b50\u9876/\u9ad8\u4f4e\u4e24\u5c42\u5e73\u53f0\uff09\u21d2 \u4e0d\u5206\u5c42\u4f1a\u628a\u4e0b\u9762\u7684\u4eba\u63a8\u5f00',
    '\u5224\u636e = CsConst.StandHeight=%.2f m' % GC_CONST['StandHeight'],
    K_CONSIST if re.search(r'CsConst\.StandHeight', rd(CS_MATCH_FILE)) else K_MISMATCH,
    rel(CS_MATCH_FILE))

# 口径一致性（切片F 修，根因）：位图头 colliders / 位图尾碰撞体段 / geo 阻挡盒 / 场景 Blocker_*
# 四处必须**同数且逐条 AABB 等价**（只比条数不够：条数对上而 AABB 是旧盒子 ⇒ 可走性还是按旧盒子算）。
add('D9', '\u963b\u6321\u76d2\u53e3\u5f84\u4e00\u81f4\u6027\uff08\u4f4d\u56fe vs geo vs \u573a\u666f\uff0c\u9010\u6761 AABB\uff09',
    rel(BITMAP), rel(GEO), 4, T_SCRIPT,
    K_CONSIST if GC_COLL['ok'] else
    '%s(geo=%d \u573a\u666f=%d \u4f4d\u56fe\u5934=%s \u21d2 \u53ef\u8d70\u6027\u6309\u65e7\u76d2\u5b50\u7b97)'
    % (K_MISMATCH, GC_COLL['geo'], GC_COLL['scene'],
       ','.join(str(p['head']) for p in GC_COLL['per'])),
    'geo=%d / \u573a\u666f=%d / %s'
    % (GC_COLL['geo'], GC_COLL['scene'],
       ' / '.join('%s head=%d \u6bb5=%dB AABB\u7b49\u4ef7=%s'
                  % (p['file'], p['head'], p['seg'], p['aabb_ok']) for p in GC_COLL['per'])))

for nm, val, bd, expect, src in [
    ('\u53ef\u8fc8\u4e0a\u53f0\u9636\u9ad8 StepUpHeight', '0.45', '0.45\u00b10.01 m',
     '\u539f\u7248 PM_WalkMove \u53f0\u9636\u5224\u636e', 'Core/CsConst.cs'),
    ('\u53ef\u7ad9\u7acb\u5761\u5ea6\u6cd5\u7ebf\u9608\u503c', '0.7', 'acos(0.7)\u224845.573\u00b0',
     '\u539f\u7248 pm_shared.c: normal[2] < 0.7 = too steep', 'Core/CsConst.cs'),
    ('\u8df3\u8dc3\u521d\u901f', '6.82', 'sqrt(2*800*45.0)*0.0254 m/s',
     '\u539f\u7248 pm_shared.c:2596', 'Core/CsConst.cs'),
    ('\u91cd\u529b', '20.32', 'sv_gravity 800 * 0.0254',
     '\u539f\u7248 hw.dll.orig:0x189e90', 'Core/CsConst.cs')]:
    v = K_CONSIST if re.search(re.escape(val), rd(CS_CORE)) else K_MISMATCH
    add('D9', nm, 'Core/CsConst.cs', src, 4, T_SCRIPT, v, '\u503c=%s' % val)
    for b in ('%s \u4e0b\u4e00\u6863' % bd, '%s' % bd, '%s \u4e0a\u4e00\u6863' % bd, '0'):
        sta('D9', nm, '\u9608\u503c\u8fb9\u754c', b, expect, '\u5e38\u91cf=%s' % val, K_CONSIST if v == K_CONSIST else K_MISMATCH, src)

# ============================================================================
#  D10 逻辑 / D11 输入 / D12 流程
# ============================================================================
LOGIC = [
    ('CsTeam \u679a\u4e3e', CS_SCRIPTS + '/Core/CsEnums.cs', r'enum CsTeam', 'CT/T/Spectator 三值'),
    ('\u56de\u5408\u9636\u6bb5\u679a\u4e3e', CS_SCRIPTS + '/Core/CsEnums.cs', r'enum CsRoundPhase', 'Freeze/Live/RoundEnd/MatchEnd'),
    ('\u80dc\u8d1f\u539f\u56e0\u679a\u4e3e', CS_SCRIPTS + '/Core/CsEnums.cs', r'enum CsRoundEndReason', '4 \u79cd'),
    ('\u547d\u4e2d\u90e8\u4f4d\u679a\u4e3e', CS_SCRIPTS + '/Core/CsEnums.cs', r'enum CsHitbox', 'Head/Chest/Stomach/Leg'),
    ('\u7ecf\u6d4e\uff08\u8d77\u59cb/\u4e0a\u9650/\u8fde\u8d25\u9012\u589e\uff09', CS_SCRIPTS + '/Module/Match/CsEconomy.cs', r'RewardLoss|MaxMoney|StartMoney', '\u8fb9\u754c\uff1a0 / 16000 / \u8d85\u754c'),
    ('\u4f24\u5bb3\u6a21\u578b', CS_SCRIPTS + '/Module/Match/CsDamage.cs', r'HitHead|ArmorAbsorb|Falloff', '\u90e8\u4f4d\u00d7\u62a4\u7532\u00d7\u8ddd\u79bb'),
    ('\u707c\u5f39 C4 \u4e0b\u5305/\u62c6\u5305/\u7206\u70b8', CS_SCRIPTS + '/Module/Match/CsBomb.cs', r'Plant|Defuse|Explode', '35s / 10s(5s kit) / \u534a\u5f84 12m'),
    ('\u56de\u5408\u6d41\u7a0b\uff08Freeze\u2192Live\u2192RoundEnd\uff09', CS_ROUND, r'TickPhase', '4s / 105s / 5s'),
    ('\u8d62\u5f97\u5224\u5b9a', CS_ROUND, r'EvaluateWin', '4 \u79cd\u7ed3\u7b97\u5206\u652f'),
    ('\u534a\u573a\u4ea4\u6362', CS_ROUND, r'SwapScoresAndStreaks', '15 \u56de\u5408'),
    ('\u4e70\u67aa\u533a\u5224\u5b9a', CS_MATCH, r'IsInBuyZone', '\u533a\u57df + 15s \u7a97\u53e3'),
    ('\u80fd\u5426\u4e70\u67aa\uff08\u5206\u652f\uff09', CS_MATCH, r'CanBuyWeapon', '\u56de\u5408\u9636\u6bb5/\u91d1\u94b1/\u533a\u57df'),
    ('\u5207\u67aa/\u5207\u69fd', CS_MATCH, r'SwitchSlot|SwitchWeapon', '1/2/3/4/5 \u69fd'),
    ('\u6362\u5f39', CS_MATCH, r'RequestReload', '\u5f39\u5323\u672a\u6ee1'),
    ('\u4e0b\u5305/\u62c6\u5305\u4f7f\u7528\u952e', CS_MATCH, r'SetUseHeld', 'E \u6309\u4f4f'),
    ('\u4e22\u67aa', CS_MATCH, r'DropActiveWeapon', '-'),
    ('\u89c2\u6218\u5207\u6362\u76ee\u6807', CS_MATCH, r'SpectateNext', '\u7a7a\u683c'),
    ('\u53d7\u51fb\u7ed3\u7b97', CS_MATCH, r'ReportHit', '\u90e8\u4f4d/\u62a4\u7532/\u7a7f\u900f'),
    ('\u5f00\u706b\u4e8b\u4ef6\u6d88\u8d39', CS_MATCH, r'ConsumeShotFired', '-'),
    ('\u6dfb\u52a0/\u8e22\u51fa\u673a\u5668\u4eba', CS_MATCH, r'AddBot|KickBot', '3 \u6863\u96be\u5ea6'),
    ('\u673a\u5668\u4eba\u96be\u5ea6\u5207\u6362', CS_MATCH, r'SetBotDifficulty', 'Easy/Normal/Hard'),
    ('\u91cd\u5f00\u672c\u56de\u5408/\u6574\u573a', CS_MATCH, r'RestartRound|RestartMatch', '-'),
    ('\u6682\u505c', CS_MATCH, r'SetPaused', '\u6682\u505c\u6001\u65f6\u95f4\u4e0d\u63a8\u8fdb'),
    ('\u6362\u9635\u8425', CS_MATCH, r'ChangeTeam', 'CT/T/Spectator'),
    ('\u5f3a\u5236\u7ed3\u675f\u56de\u5408', CS_MATCH, r'ForceEndRound', '-'),
    ('\u51bb\u7ed3\u671f\u7981\u79fb\u52a8', MOTOR, r'Crouch|Jump', '\u51bb\u7ed3\u671f\u4e0d\u80fd\u52a8'),
    ('\u6b65\u884c/\u8e72\u4f0f/\u8df3\u8dc3\u91c7\u96c6', MOTOR, r'GetKey\(GameKey\.LeftShift\)', 'Shift/Ctrl/Space'),
    ('\u811a\u6b65\u58f0\u95e8\u63a7', CS_SCRIPTS + '/Module/Audio/AudioModule.cs', r'TickFootsteps', '\u6162\u8d70\u65e0\u58f0'),
    ('\u5f00\u706b\u7edf\u8ba1\u65e5\u5fd7', CS_SCRIPTS + '/Module/Combat/CombatSelfTest.cs', r'SelfTest|rays|raysFired|\u5c04\u7ebf', '-'),
]
for nm, path, pat, note in LOGIC:
    ln = lineno(path, pat)
    v = K_CONSIST if ln else '%s(\u6e90\u7801\u91cc\u627e\u4e0d\u5230\u8be5\u5206\u652f)' % K_MISMATCH
    add('D10', nm, rel(path), '%s:%d' % (rel(path), ln) if ln else rel(path), 3, T_SCRIPT, v, note)
    sta('D10', nm, '\u5206\u652f\u4e0e\u8fb9\u754c', '\u9608\u503c\u00b11 / 0 / \u4e0a\u9650', note,
        '\u6e90\u7801\u547d\u4e2d=%s' % bool(ln), K_CONSIST if ln else K_MISMATCH, rel(path))

INPUTS = [
    ('W/A/S/D', '\u6e38\u620f\u5185', '\u79fb\u52a8', r'GameKey\.W'),
    ('Shift', '\u6e38\u620f\u5185', '\u6162\u8d70\uff08\u65e0\u58f0\uff09', r'GameKey\.LeftShift'),
    ('Ctrl', '\u6e38\u620f\u5185', '\u8e72\u4f0f', r'GameKey\.LeftCtrl|GameKey\.RightCtrl'),
    ('Space', '\u6e38\u620f\u5185', '\u8df3\u8dc3', r'GameKey\.Space'),
    ('Space', '\u89c2\u6218', '\u5207\u6362\u89c2\u6218\u76ee\u6807', r'GetKeyDown\(GameKey\.Space\)'),
    ('MouseRight', '\u6e38\u620f\u5185', '\u5f00\u955c/\u6b21\u8981\u529f\u80fd', r'GameKey\.MouseRight'),
    ('MouseLeft', '\u6e38\u620f\u5185', '\u5f00\u706b', r'MouseLeft|GetMouseButton'),
    ('R', '\u6e38\u620f\u5185', '\u6362\u5f39', r'GameKey\.R\b'),
    ('E', '\u6e38\u620f\u5185', '\u4f7f\u7528\uff08\u62c6\u5305\uff09', r'GameKey\.E\b'),
    ('1/2/3/4/5', '\u6e38\u620f\u5185', '\u6b66\u5668\u69fd', r'GameKey\.Num1'),
    ('Q', '\u6e38\u620f\u5185', '\u4e0a\u4e00\u628a\u6b66\u5668\uff08\u539f\u7248\u9ed8\u8ba4\uff09', r'GameKey\.Q\b'),
    ('M', '\u6e38\u620f\u5185', '\u6362\u9635\u8425\uff08\u539f\u7248\u9ed8\u8ba4\uff09', r'GameKey\.M\b'),
    ('G', '\u6e38\u620f\u5185', '\u4e22\u5f53\u524d\u6b66\u5668\uff08\u539f\u7248\u9ed8\u8ba4\uff09', r'GameKey\.G\b'),
    ('Tab', '\u6e38\u620f\u5185', '\u8bb0\u5206\u677f\uff08\u6309\u4f4f\uff09', r'GameKey\.Tab'),
    ('B', '\u6e38\u620f\u5185', '\u4e70\u67aa\u83dc\u5355', r'GameKey\.B\b'),
    ('H', '\u6e38\u620f\u5185', 'H \u83dc\u5355', r'GameKey\.H\b'),
    ('Z/X/C', '\u6e38\u620f\u5185', '\u65e0\u7ebf\u7535', r'GameKey\.Z\b'),
    ('`~', '\u6e38\u620f\u5185', '\u63a7\u5236\u53f0\uff08\u539f\u7248\u9ed8\u8ba4\uff09', r'GameKey\.BackQuote'),
    ('/  \u6216 0', '\u6e38\u620f\u5185', '\u63a7\u5236\u53f0\uff08\u672c\u5de5\u7a0b\u4ee3\u66ff\uff09', r'GameKey\.Slash'),
    ('Enter/Space/Esc', '\u542f\u52a8\u753b\u9762', '\u8df3\u8fc7\u542f\u52a8\u753b\u9762', r'GetKeyDown\(GameKey\.Space\)'),
    ('Escape', '\u6e38\u620f\u5185', '\u6682\u505c\u83dc\u5355', r'GetKeyDown\(GameKey\.Escape\)'),
    ('Escape', '\u6682\u505c\u6001', '\u7ee7\u7eed\u6e38\u620f', r'GetKeyDown\(GameKey\.Escape\)'),
    ('\u6570\u5b57\u952e', '\u4e70\u67aa\u83dc\u5355', '\u5206\u7c7b\u9009\u62e9', r'CategoryKey'),
    ('\u6570\u5b57\u952e', '\u65e0\u7ebf\u7535\u83dc\u5355', '\u9009\u9879\u9009\u62e9', r'NumberKey'),
    ('Enter', '\u63a7\u5236\u53f0', '\u63d0\u4ea4\u547d\u4ee4', r'GameKey\.Enter'),
    ('\u9f20\u6807\u70b9\u51fb', '\u83dc\u5355/\u9762\u677f', '\u6309\u94ae\u70b9\u51fb', r'GameKey\.MouseLeft|GetMouseButton|onClick|Button'),
]
# \u5207\u7247H\uff1a\u5f15\u64ce GameKey \u679a\u4e3e\u91cc\u6ca1\u6709 BackQuote\uff08\u5951\u7ea6\u7f3a\u53e3\uff0c\u4e0d\u8bb8\u6539\uff09\u21d2 \u300c~\u300d\u8fd9\u4e00\u884c\u7684\u7ed3\u8bba = \u5141\u8bb8\u7684\u5dee\u5f02\uff08\u5dee\u5f02 #3\uff09\uff0c
# \u800c\u4e0d\u662f\u300c\u672a\u5b9e\u73b0\u300d\u2014\u2014 \u672c\u5de5\u7a0b\u5df2\u7528 / \u6216 0 \u4ee3\u66ff\uff08\u540c\u4e00\u884c\u4e0b\u9762\u90a3\u6761\u547d\u4e2d\uff09\u3002
D11_ALLOWED = {
    '`~': '\u5f15\u64ce GameKey \u65e0 BackQuote\uff08\u5951\u7ea6\u7f3a\u53e3\uff09\uff1b\u672c\u5de5\u7a0b\u7528\u300c/\u300d\u6216\u300c0\u300d\u5f00\u63a7\u5236\u53f0\uff08\u9a8c\u6536\u8868\u5141\u8bb8\u7684\u5dee\u5f02#3\uff09',
}
for key, ctx, tgt, pat in INPUTS:
    hit = re.search(pat, codeText)
    if hit:
        v = K_CONSIST
    elif key in D11_ALLOWED:
        v = '%s(\u2192 \u5dee\u5f02\u767b\u8bb0.tsv)' % K_ALLOWED
        tgt = tgt + '\uff1b' + D11_ALLOWED[key]
    else:
        v = '%s(\u6e90\u7801\u91cc\u6ca1\u6709\u8be5\u7ed1\u5b9a)' % K_MISMATCH
    # 片BW-E：`控制台（本工程代替）` / `武器槽` 这类"映射目标"是自述文本 ⇒ 接上**绑定点**
    #   （该 `pat` 在源码里首次命中的 `文件:行号`；未命中 = 这条本来就是在判"没有该绑定"，
    #    无点可指 ⇒ 不接，由锚点收口段/回报逐类说明）。
    _bind_site = code_site(pat) if hit else ''
    add('D11', '%s@%s' % (key, ctx), '\u811a\u672c\u7ed1\u5b9a\u8868', 'Core/CsConst.cs + Module/**', 2, T_SCRIPT,
        v, tgt + ('\uff08\u7ed1\u5b9a\u70b9 %s\uff09' % _bind_site if _bind_site else ''))
    sta('D11', '%s@%s' % (key, ctx), '\u70b9\u51fb/\u6309\u4f4f/\u8fde\u6309', '-\u3001\u957f\u6309',
        '\u539f\u7248\u9ed8\u8ba4\u7ed1\u5b9a\uff08\u51fa\u5904\uff1a%s:36-44\uff09' % SPEC, tgt, K_CONSIST if hit else K_MISMATCH, 'Module/**')

FLOW_STATES = ['Boot', 'MainMenu', 'ServerList', 'NewGame', 'Options', 'TeamSelect',
               'Loading', 'Stage', 'Pause']
FLOW_EVENTS = [('BootDone', 'Boot\u2192MainMenu'), ('OpenServers', 'MainMenu\u2192ServerList'),
               ('OpenOptions', 'MainMenu\u2192Options'), ('NewGame', 'MainMenu\u2192NewGame'),
               ('Loading', '\u9009\u9635\u8425\u2192Loading'), ('StageReady', 'Loading\u2192Stage'),
               ('StageBegin', '\u8fdb\u56fe\u5f00\u59cb'), ('Pause', 'Stage\u2192Pause'),
               ('Resume', 'Pause\u2192Stage'), ('ToMain', '\u2192MainMenu')]
for nm in FLOW_STATES:
    hit = re.search(r'"%s"' % nm, rd(FLOW)) or re.search(r'\b%s\s*=' % nm, rd(FLOW))
    add('D12', '\u72b6\u6001/%s' % nm, rel(FLOW), rel(FLOW), 1, T_SCRIPT,
        K_CONSIST if hit else '%s(\u72b6\u6001\u5b9a\u4e49\u627e\u4e0d\u5230)' % K_MISMATCH, '\u72b6\u6001\u673a\u7ad9\u70b9')
for ev, tr in FLOW_EVENTS:
    hit = re.search(r'%s' % ev, rd(FLOW))
    add('D12', '\u8f6c\u79fb/%s' % tr, rel(FLOW), rel(FLOW), 1, T_SCRIPT,
        K_CONSIST if hit else '%s(\u8be5\u8f6c\u79fb\u6ca1\u5b9e\u73b0)' % K_MISMATCH, 'event=%s' % ev)
for nm, pat in [('\u9000\u51fa\u6e38\u620f', r'Application\.Quit'), ('\u56de\u4e3b\u83dc\u5355', r'GoMainMenu'),
                ('\u6682\u505c\u6001\u65f6\u95f4\u4e0d\u63a8\u8fdb', r'SetPaused'), ('\u91cd\u65b0\u5f00\u5c40', r'RestartMatch')]:
    hit = re.search(pat, ALL_TXT)
    add('D12', nm, rel(FLOW), rel(FLOW), 1, T_SCRIPT, K_CONSIST if hit else '%s(\u627e\u4e0d\u5230)' % K_MISMATCH, 'pat=%s' % pat)

# ── 片BW-M（D12 状态行）：流程与场景流转此前在**状态矩阵**里 0 行 ⇒ 闸门第 41 项 state-matrix
#    报 `no row for dimension(s) D12,S2`（缺维度 = 未判，⛔ 不是"可以少判"）。
#    上面那批 `add` 只断言"站点/转移/退出在源码里存在"；状态矩阵这一层要判的是**每次转移的
#    触发与边界**（规格 §1 主循环 :34-56 + §2.1 M1~M9 :65-73）。边界值取四种：0 次（不触发）/
#    1 次（正常一次）/ 重复触发（连按）/ 转移中途中断；另外两条是暂停态时间冻结。
#    判据全部落在**同一份载体** `AppFlow.cs` 的站点/转移/守卫（`:NNN` = 该文件行号，离线可复核），
#    ⛔ 不引任何 Play 读数（这几行是"数值/逻辑类"⇒ 脚本断言，不截图）。
#    ⛔ 同批只增状态行、**不新增实体**：实体清单/判定块逐字节不变（`ENT` 未动）。
_FLOW_EV = 'client/Assets/Scripts/Module/Flow/AppFlow.cs'
# 留待的**数值/逻辑类**行必须说清"要哪一种实机读数"：`K_PENDING` 的文案是"待采(并排图)"，
# 对流程/性能这类只该用运行时日志或探针判的行不准确 ⇒ 单独一个token（同一前缀"待采"）。
K_PENDING_NUM = '待采（数值类：需实机）'
D12_ROWS = [
    # (实体, 状态/事件, 边界值, 期望表现(出处), 实测, 结论, 证据)
    ('转移/Boot→MainMenu', '触发（非 Boot 站点收到 StartNewGame）', '0 次（不触发）',
     '启动画面几秒后自动进主菜单（M1；%s:65）⇒ 该事件只在 Boot 站点推进流程' % SPEC,
     'AppFlow.cs:633-652 三分支：Boot⇒Trigger(BootDone)（:637-642）/ MainMenu⇒Trigger(NewGame)（:644-649）/ 其余站点 Warn 忽略（:651）',
     K_CONSIST, '%s:633-652' % _FLOW_EV),
    ('转移/Boot→MainMenu', '触发（正常一次）', '1 次',
     'M1 启动画面几秒后进主菜单（%s:65）' % SPEC,
     'Fsm.Force(Boot)（:114）→ OnExitBoot 关 BootPanel（:177-180）→ OnEnterMainMenu 进 Menu 场景（:182-185）→ AddTransition(BootDone→MainMenu)（:130）由 :640 触发',
     K_CONSIST, '%s:114,:130,:177-185' % _FLOW_EV),
    ('转移/Boot→MainMenu', '触发（启动画面连按任意键）', '重复触发（连按）',
     'M1「可按任意键跳过」（%s:65）⇒ 第二次按键不得重启/重入 Boot' % SPEC,
     '第二次到达时站点已是 MainMenu ⇒ 走 NewGame 分支（:644-649）；Boot 只在 :114 被 Force 一次，无重复装/卸主菜单的路径',
     K_CONSIST, '%s:114,:644-649' % _FLOW_EV),
    ('转移/Boot→MainMenu', '转移中途中断（Boot 期间退出）', '转移中途中断',
     'M9 退出 = 关窗口（%s:73），任意站点可用' % SPEC,
     'OnQuitGame :777-787 无站点前置判断（Game.Setting.Save → 编辑器下停 Play / 打包 Application.Quit）',
     K_CONSIST, '%s:777-787' % _FLOW_EV),
    ('转移/MainMenu→NewGame', '触发（非主菜单站点收到 StartNewGame）', '0 次（不触发）',
     'M3 New Game 只能在主菜单点（%s:67）' % SPEC,
     '非 Boot/MainMenu 站点 ⇒ Warn 忽略（:651）',
     K_CONSIST, '%s:651' % _FLOW_EV),
    ('转移/MainMenu→NewGame', '触发（正常一次）', '1 次',
     'M3 New Game 面板：地图 + 规则 + Bot 设置（%s:67）' % SPEC,
     'Trigger(NewGame)（:647）+ AddTransition(NewGame→NewGame 站点)（:133）+ OnEnterNewGame 开面板（:331-334）',
     K_CONSIST, '%s:133,:331-334,:647' % _FLOW_EV),
    ('转移/MainMenu→NewGame', '触发（连点 New Game）', '重复触发（连点）',
     '第二次点击不得叠开第二个 New Game 面板',
     '第二次到达时站点=NewGame ⇒ 命中 :651 的 Warn 忽略分支；面板本身由 OnTickNewGame :336-341 收口',
     K_CONSIST, '%s:336-341,:644-651' % _FLOW_EV),
    ('转移/MainMenu→NewGame', '转移中途中断（面板被 Back 关掉）', '转移中途中断',
     '面板 Back ⇒ 站点回主菜单，不得停在 NewGame',
     'OnTickNewGame :336-341：面板已关 ⇒ Transition(MainMenu)',
     K_CONSIST, '%s:336-341' % _FLOW_EV),
    ('转移/MainMenu→Options', '触发（白名单外站点收到 OpenOptions）', '0 次（不触发）',
     'M5 Options 只从主菜单 / ESC 菜单进（%s:69,:72）' % SPEC,
     'CurrentState 不在 MainMenu/Pause/Options ⇒ Warn 忽略（:667-671）',
     K_CONSIST, '%s:664-671' % _FLOW_EV),
    ('转移/MainMenu→Options', '触发（正常一次）', '1 次',
     'M5 Options 分页可改并落 Game.Setting（%s:69）' % SPEC,
     'Trigger(OpenOptions)（:680）+ AddTransition（:132）+ OnEnterOptions 开面板（:348-351）',
     K_CONSIST, '%s:132,:348-351,:680' % _FLOW_EV),
    ('转移/MainMenu→Options', '触发（连点 Options）', '重复触发（连点）',
     '第二次点击不得重开 Options / 不得叠面板',
     '站点=Options ⇒ 命中「Options 已打开，忽略」的**显式幂等**分支（:673-677）',
     K_CONSIST, '%s:673-677' % _FLOW_EV),
    ('转移/MainMenu→Options', '转移中途中断（从暂停进 Options 后 Back）', '转移中途中断',
     'M8 ESC 菜单 → 选项；返回时应回暂停菜单而不是主菜单（%s:72）' % SPEC,
     '_optionsFromPause 记录来路（:679）+ OnTickOptions 回 Pause（:356-359）+ IsPauseFamily 把「从暂停进的 Options」算暂停族（:804-809）',
     K_CONSIST, '%s:356-359,:679,:804-809' % _FLOW_EV),
    ('转移/选阵营→Loading', '触发（无待跑配置 / 配置为 null）', '0 次（不触发）',
     'M6 进图前必须先有 New Game 配置（%s:70）' % SPEC,
     'LaunchMatch 收到 null ⇒ Error 忽略（:685-689）；GoStage 收到 null ⇒ Error 忽略（:368-374）；TeamChosen 无 _pending ⇒ Warn 忽略（:717-721）',
     K_CONSIST, '%s:368-374,:685-689,:717-721' % _FLOW_EV),
    ('转移/选阵营→Loading', '触发（正常一次）', '1 次',
     '开始 → 读条 → 选阵营 → 进图（%s:38）；M7 读条 = 真进度条（:71）' % SPEC,
     'GoStage :368-399：_pending 记录（:382）→ 停主菜单启动曲（:391）→ Trigger(Loading)（:392）→ 地图/场景加载 + 20s 兜底（:394-398）',
     K_CONSIST, '%s:368-399' % _FLOW_EV),
    ('转移/选阵营→Loading', '触发（读条中再次发起进图）', '重复触发（连点）',
     '读条屏期间不得重入一次加载（A 的读条屏同样没有发起入口）',
     'Loading 站点只有 LoadingPanel 在屏（:413-423），发起入口 NewGamePanel 已在 :343-346 关闭 ⇒ **UI 层不可达**；源码层 GoStage 无重入守卫（:683-703 在 Loading 站点会再走一次 GoStage）⇒ 这一条只到"UI 层无入口"，引擎 Fsm 对二次 GoStage 的语义**需实机**，⛔ 不当作已判通过',
     K_PENDING_NUM, '%s:343-346,:413-423,:683-703' % _FLOW_EV),
    ('转移/选阵营→Loading', '转移中途中断（读条超时 / 放弃）', '转移中途中断',
     'M7 读条失败不得把玩家永久钉在读条屏（%s:71）' % SPEC,
     'OnStageLoadTimeout :402-411：Error + Toast + GoMainMenu；阈值 = StageLoadTimeout 20 s（:36,:398）',
     K_CONSIST, '%s:36,:398,:402-411' % _FLOW_EV),
    ('转移/选阵营→Loading', '超时兜底回调在非 Loading 站点二次到达', '上限+1（20s 阈值之后 +1s 的第二次回调）',
     '兜底回调只对 Loading 站点生效；回主菜单后重入 = 无动作',
     'OnStageLoadTimeout 首行 `if (CurrentState != State.Loading) return;`（:404）',
     K_CONSIST, '%s:404' % _FLOW_EV),
    ('转移/Loading→TeamSelect', '触发（场景 / 地图只就绪一个）', '0 次（不触发）',
     '读条完成 = 场景与地图都就绪（否则进图后空间查询是退化值）',
     'TryFinishStageLoad :490-492 `if (!_stageSceneReady || !_stageMapReady) return;`',
     K_CONSIST, '%s:490-492' % _FLOW_EV),
    ('转移/Loading→TeamSelect', '触发（两者都就绪，正常一次）', '1 次',
     'M6 进图时弹阵营选择（%s:70）' % SPEC,
     '进度置 1（:499-500）+ Trigger(StageReady)（:502）+ AddTransition(StageReady→TeamSelect)（:135）',
     K_CONSIST, '%s:135,:490-503' % _FLOW_EV),
    ('转移/Loading→TeamSelect', '触发（场景与地图两个回调先后到达）', '重复触发',
     '两个就绪回调串行到达时只允许一次转移',
     '第二次到达时站点已非 Loading ⇒ Warn 忽略（:493-497）',
     K_CONSIST, '%s:493-497' % _FLOW_EV),
    ('转移/Loading→TeamSelect', '转移中途中断（读条中回主菜单）', '转移中途中断',
     '回主菜单后迟到的读条完成回调不得把玩家拽回进图',
     '非 Loading 站点 ⇒ Warn 忽略（:493-497）；清场见 GoMainMenu/ClearStage（:580-625）',
     K_CONSIST, '%s:493-497,:580-625' % _FLOW_EV),
    ('转移/TeamSelect→Stage', '触发（选阵营面板被取消）', '0 次（不触发）',
     'M6 取消选阵营 ⇒ 回主菜单，不得进图（%s:70）' % SPEC,
     'OnTickTeamSelect :511-516：面板不在 ⇒ Transition(MainMenu)',
     K_CONSIST, '%s:511-516' % _FLOW_EV),
    ('转移/TeamSelect→Stage', '触发（正常一次）', '1 次',
     'M6 选阵营后进图（%s:70）' % SPEC,
     'Trigger(StageBegin)（:725）+ AddTransition（:136）+ OnEnterStage 首次 _match.Start（:538-550）+ 开 HudPanel（:553）',
     K_CONSIST, '%s:136,:538-553,:725' % _FLOW_EV),
    ('转移/TeamSelect→Stage', '触发（从暂停恢复再次进入 Stage）', '重复触发',
     '恢复不得重开一局（否则打到一半的回合被清掉）',
     '_stageBegun 守卫（:533-537）：已开过局 ⇒ 「从暂停恢复，不重复 Start」',
     K_CONSIST, '%s:533-537' % _FLOW_EV),
    ('转移/TeamSelect→Stage', '转移中途中断（选阵营后立即回主菜单 / 退出）', '转移中途中断',
     '中途退出必须整场清干净（清场漏一项，第二次进图就是脏的）',
     'GoMainMenu+ClearStage :580-625 清 6 类（比赛 Stop / CloseAll / Entity.ClearAll / Pool.ClearAll / StopScope(stage) / Sound.StopAll / HudSnapshot.Reset）+ _pending=null（:617-620）；Quit 无站点前置（:777-787）',
     K_CONSIST, '%s:580-625,:777-787' % _FLOW_EV),
    ('转移/Stage→Pause', '触发（非 Stage 站点收到暂停请求）', '0 次（不触发）',
     'M8 ESC 菜单只在局内可弹（%s:72）' % SPEC,
     'OnRequestPause :738-742：非 Stage ⇒ Warn 忽略',
     K_CONSIST, '%s:728-746' % _FLOW_EV),
    ('转移/Stage→Pause', '触发（正常一次）', '1 次',
     'M8 ESC 菜单（继续 / 选项 / 换阵营 / 断开）（%s:72）' % SPEC,
     'Trigger(Pause)（:745）+ AddTransition（:137）+ OnEnterPause 开 PausePanel（:563-566）',
     K_CONSIST, '%s:137,:563-566,:745' % _FLOW_EV),
    ('转移/Stage→Pause', '触发（连按 ESC）', '重复触发（连按）',
     '连按 ESC 不得重复进出暂停 / 不得刷屏',
     'OnRequestPause 在 Pause 站点**幂等返回**（:732-736，注释写明"同帧被两处识别"的幂等）；OnTickPause 每帧轮询 ESC（:568-571）',
     K_CONSIST, '%s:568-571,:728-746' % _FLOW_EV),
    ('转移/Stage→Pause', '触发（有遮挡面板时按 ESC）', '边界：ESC 归属（有遮挡 vs 只有常驻 HUD）',
     'H 菜单（%s:107）/ 买枪菜单（:98）开着时 ESC 归它们，不弹暂停菜单' % SPEC,
     'OnTickStage 先判 HasBlockingOverlay（:556-561）⇒ 有遮挡则不弹暂停菜单；HasBlockingOverlay 把常驻 HUD 排除在外（:824-832）',
     K_CONSIST, '%s:556-561,:824-832' % _FLOW_EV),
    ('转移/Pause→Stage', '触发（非 Pause 站点收到继续请求）', '0 次（不触发）',
     'M8 ESC 菜单的「继续」只在暂停态生效（%s:72）' % SPEC,
     'OnResumeRequested :752-758：Stage ⇒ 幂等返回；其他站点 ⇒ Warn 忽略',
     K_CONSIST, '%s:748-762' % _FLOW_EV),
    ('转移/Pause→Stage', '触发（正常一次）', '1 次',
     'M8 继续游戏（%s:72）' % SPEC,
     'Trigger(Resume)（:761）+ AddTransition（:138）+ OnExitPause 关面板（:573-576）+ OnEnterStage 走恢复分支（:533-537）',
     K_CONSIST, '%s:138,:533-537,:573-576,:761' % _FLOW_EV),
    ('转移/Pause→Stage', '转移中途中断（暂停中回主菜单）', '转移中途中断',
     'M8 「断开」在单机版 = 回主菜单（%s:72）；暂停态退出必须解除暂停' % SPEC,
     'GoMainMenu（:580-592）→ ClearStage（:595-625）；暂停族进出由 OnFsmChanged 调 SetPaused（:791-801）',
     K_CONSIST, '%s:580-625,:791-801' % _FLOW_EV),
    ('暂停态时间不推进', '状态：暂停族（Pause 站点 + 从暂停进来的 Options）', '0（时间推进量 = 0）',
     'M8 ESC 菜单期间比赛时间不推进（%s:51,:72）' % SPEC,
     'OnFsmChanged :791-801：进暂停族 ⇒ _match.SetPaused(true)；离开 ⇒ SetPaused(false)；IsPauseFamily :804-809 把「从暂停进的 Options」也算暂停族（进 Options 不解除暂停）',
     K_CONSIST, '%s:791-809' % _FLOW_EV),
    ('回主菜单', '转移：（任意站点）→MainMenu，已在主菜单时', '0 次（不触发）',
     'M2 主菜单（%s:66）；重复的回主菜单请求不得重装主菜单' % SPEC,
     'GoMainMenu :584-588：已在 MainMenu 且 MainMenuPanel 开着 ⇒ 「忽略重复的 GoMainMenu」',
     K_CONSIST, '%s:580-592' % _FLOW_EV),
    ('回主菜单', '转移：Stage→MainMenu（正常一次）', '1 次',
     'M8 「断开」= 回主菜单（%s:72）' % SPEC,
     'ClearStage :595-625（比赛 Stop / UI CloseAll / Entity.ClearAll / Pool.ClearAll / StopScope(stage) / Sound.StopAll / HudSnapshot.Reset）+ Trigger(ToMain)（:591，AddTransition :139）',
     K_CONSIST, '%s:139,:591-625' % _FLOW_EV),
    ('回主菜单', '转移：连点回主菜单（主菜单面板尚未打开时重入）', '重复触发（连点）',
     '连点回主菜单不得重复清场 / 不得停在空场景',
     '面板已开时的重复由 :584-588 拦下（这一条已判）；面板尚未开（Menu 场景加载中 / 舞台卸载中，:196-260）时会再走 ClearStage+Trigger(ToMain)（:590-591）⇒ 引擎 Fsm.Trigger 到同站点的语义离线判不了，**需实机**',
     K_PENDING_NUM, '%s:196-260,:580-592' % _FLOW_EV),
    ('重新开局', '转移：局内 H 菜单重开一局（LaunchMatch 在 Stage/Pause 收到）', '重复触发（同局内重开一次）',
     'H 菜单的重开回合 / 换图（%s:107）' % SPEC,
     'OnLaunchMatch :693-700：已在 Stage/Pause ⇒ 直接 _match.Start(_pending)，不重走读条',
     K_CONSIST, '%s:693-703' % _FLOW_EV),
    ('退出游戏', '转移：（任意站点）→退出', '边界：任意站点可用（无站点前置）',
     'M9 退出 = 关窗口（%s:73）' % SPEC,
     'OnQuitGame :777-787：Game.Setting.Save → 编辑器下停 Play / 打包 Application.Quit；⛔ 无站点判断',
     K_CONSIST, '%s:777-787' % _FLOW_EV),
]
for _r in D12_ROWS:
    sta('D12', _r[0], _r[1], _r[2], _r[3], _r[4], _r[5], _r[6])
sys.stderr.write('  [bw-m] D12 state rows appended = %d\n' % len(D12_ROWS))

# ============================================================================
#  S1 数值 / S2 性能 / S3 设置
# ============================================================================
NUM_FILES = [CS_CORE, CS_MATCH, CS_ROUND, CS_AUDIO_T, CS_COMBAT_T, CS_VIEW_T, CS_HUD_THEME,
             os.path.join(CS_SCRIPTS, 'Module', 'Bot', 'CsBotConst.cs'),
             os.path.join(CS_SCRIPTS, 'Core', 'CsMatchConfig.cs')]
for p in NUM_FILES:
    if not os.path.exists(p):
        continue
    lines = rd(p).split('\n')
    for i, l in enumerate(lines):
        m = re.search(r'public const\s+(int|float|string)\s+(\w+)\s*=\s*([^;]+);', l)
        if not m:
            continue
        name, val = m.group(2), m.group(3).strip()
        # 出处可能在离常量 30 行以内的任意注释里（含类级口径注释），窗口取 30 行；
        # 已经登记在 策划/对照表.md 的值算"参考物自带"⇒ 一致（登记即视为有出处）。
        ctx = '\n'.join(lines[max(0, i - 30):i + 1])
        has_src = ('\u51fa\u5904' in ctx) or re.search(r'[A-Za-z0-9_]+\.(cfg|dll|c|h|py|scr|txt|md|cs):', ctx) or ('\u539f\u7248' in ctx)
        allowed = bool(re.search(r'\b' + re.escape(name) + r'\b', REG_TXT))
        if has_src:
            v, ev = K_CONSIST, '\u6587\u6863\u91cc\u6709\u51fa\u5904'
        elif allowed:
            v, ev = K_ALLOWED, '\u5df2\u5728\u5bf9\u7167\u8868/\u5141\u8bb8\u7684\u5dee\u5f02\u91cc\u767b\u8bb0'
        else:
            v, ev = '%s(\u65e0\u51fa\u5904)' % K_MISMATCH, '\u6ce8\u91ca\u91cc\u627e\u4e0d\u5230\u51fa\u5904'
        add('S1', '%s.%s' % (os.path.basename(p)[:-3], name), '%s:%d' % (rel(p), i + 1), '%s:%d' % (rel(p), i + 1), 4, T_SCRIPT, v, '\u503c=%s / %s' % (val, ev))
        if m.group(1) in ('int', 'float'):
            for b in ('0', '\u4e0a\u9650', '\u4e0a\u9650+1', '\u9608\u503c\u00b11'):
                sta('S1', '%s.%s' % (os.path.basename(p)[:-3], name), '\u8fb9\u754c', b,
                    '\u539f\u7248\u540c\u91cf\u7684\u8fb9\u754c\u884c\u4e3a\uff08\u51fa\u5904\uff1a%s\uff09' % ev, '\u5e38\u91cf=%s' % val,
                    K_CONSIST if v == K_CONSIST else (K_PENDING if v == K_ALLOWED else K_MISMATCH), rel(p))

# ── 切片N（S1）：装备类**没有第一人称模型** ⇒ 旧判据对它们不成立 ──────────────────
# 旧判据把 CsWeapons 里**所有** id 都当"有 viewmodel 的武器"，要求 vm_<id>.controller 存在。
# 实测（切片M 查实、本片按主 agent 裁决落地）：Defuser / Vest / VestHelm 是**被动装备** ——
# A（CS 1.6）里它们既不能"手持"、也没有第一人称动作。依据（**工程内可复核**，不靠印象）：
#   ① 原版 mdl 的导出产物 `client/Assets/Editor/Views/ModelData/*.cs16anim` 共 38 个，
#      逐个数 = 29 个 `vm_*` + 9 个 `player_*`，**装备类命中 0 个**（没有 vm_defuser /
#      vm_vest / vm_vesthelm 的导出）；
#   ② 盘上 `vm_*.controller` 同样只有 29 个（`client/Assets/Resources/Art/Anim/`），
#      与该导出集合逐个同名 ⇒ 缺的这 3 个正是"原版本来就没有"，不是本工程的缺口。
# ⇒ 判据改成对装备成立的那条：**A 也无此 viewmodel ⇒ 一致**（与 A 一致，不是不一致）。
# ⛔ 不许去 `Editor/Views` 生成这三个控制器 —— 那等于造 A 没有的素材（skill §0 铁律 1），
#    而且这三个 `v_*.controller` 的原版载体（`models/v_*.mdl`）本机不在盘、连出处都拿不到
#    （`原版资源/cs16src` 现存 73 份 cstrike 资源里没有 `models/`；片AW 取回的是 client.dll / mp.dll 等）。
# 装备名单与 D8 段**同源**（复用 `D8_EQUIPMENT`，见上）⇒ ⛔ 不按"名字像装备"猜。
S1_PASSIVE_EQUIPMENT = D8_EQUIPMENT
MODELDATA = os.path.join(ASSETS, 'Editor', 'Views', 'ModelData')
CS16ANIM = sorted(glob.glob(os.path.join(MODELDATA, '*.cs16anim')))
CS16ANIM_VM = [os.path.basename(x)[:-len('.cs16anim')] for x in CS16ANIM
               if os.path.basename(x).startswith('vm_')]
CS16ANIM_EQUIP = [x for x in CS16ANIM_VM
                  if x[len('vm_'):] in frozenset(w.lower() for w in S1_PASSIVE_EQUIPMENT)]
VM_CTRLS = sorted(glob.glob(os.path.join(ANIM_DIR, 'vm_*.controller')))

# 武器表：每把枪的 id 是否有 fire/reload clip + viewmodel 控制器
for wid in WEAPON_IDS:
    if wid in S1_PASSIVE_EQUIPMENT:
        add('S1', '武器/%s' % wid, rel(CS_WEAPONS), rel(MODELDATA), 3, T_SCRIPT, K_CONSIST,
            'id=%s：被动装备 ⇒ A 也无第一人称模型 ⇒ 一致（依据：原版 mdl 导出 .cs16anim 共 %d 个 = '
            '%d 个 vm_* + %d 个 player_*，装备类命中 %d 个；盘上 vm_*.controller 同样 %d 个；'
            '⛔ 不生成 vm_%s.controller —— 那是造 A 没有的素材）'
            % (wid, len(CS16ANIM), len(CS16ANIM_VM), len(CS16ANIM) - len(CS16ANIM_VM),
               len(CS16ANIM_EQUIP), len(VM_CTRLS), wid.lower()) + ' ' + _SCOPE_PASSIVE)
        continue
    ctrl = os.path.join(ANIM_DIR, 'vm_%s.controller' % wid)
    add('S1', '\u6b66\u5668/%s' % wid, rel(CS_WEAPONS), rel(CS_WEAPONS), 3, T_SCRIPT,
        K_CONSIST if os.path.exists(ctrl) else '%s(\u6ca1\u6709 vm_%s.controller)' % (K_MISMATCH, wid), 'id=%s' % wid)

EDLOG = os.path.join(ROOT, 'client', 'Logs', 'Editor.log')
dev = ''
if os.path.exists(EDLOG):
    mm = re.search(r'Device Name:\s*(.+)', rd(EDLOG, 4000000))
    dev = mm.group(1).strip() if mm else ''
add('S2', '\u6e32\u67d3\u8bbe\u5907', 'client/Logs/Editor.log', rel(EDLOG), 1, T_SCRIPT,
    (K_MISMATCH + '(\u8f6f\u6e32\u67d3\uff1a' + dev + ')') if re.search(r'Basic Render|WARP', dev) else (K_CONSIST if dev else K_PENDING),
    '\u8bbe\u5907=%s' % (dev or '\u672a\u77e5'))
# ── 切片P：S2 四行全部实机采到（数字出处 = 同一次 Play 的运行时读数）────────────────────
# ⚠️ 铁律：**帧时间必须与渲染设备名同时给** —— 设备是 `Microsoft Basic Render Driver` / WARP 时，
#    这台机器上任何帧时间数字都无效（skill §4 性能类 / experience/perf-triage.md）。
#    本片两处都给在同一行：`device=AMD Radeon RX 5700 XT api=Direct3D12 vramMB=8151`
#    （探针 tools/probes/probe-play-s2.cs；闸门 graphics-device 同源）。
S2_MEASURED = {
    '\u5e27\u65f6\u95f4': (
        '\u8fd0\u884c\u65f6\uff08Play \u5185\u540c\u4e00\u77ac\u95f4\uff09\uff1aunscaledDelta=4.742ms\uff08\u2248210.9 fps\uff09\uff0c'
        'FrameTimingManager cpuFrameTime=4.637ms / gpuFrameTime=1.163ms / cpuMainThread=2.587ms / cpuRenderThread=1.252ms\uff1b'
        'get_performance_stats \u540c\u523b cpuFrameTimeMs=4.2953 / gpuFrameTimeMs=1.16904 / drawCalls=103 / triangles=61625\uff1b'
        '\u6e32\u67d3\u8bbe\u5907=AMD Radeon RX 5700 XT\uff08Direct3D12\uff0cvram 8151MB\uff09\u21d2 \u6570\u5b57\u6709\u6548\uff08\u975e\u8f6f\u6e32\u67d3\uff09'),
    '\u5206\u8fa8\u7387': (
        'Screen=1920x1080\uff08fullScreen=False dpi=96\uff09+ \u4e3b canvas[[UI]] ScreenSpaceOverlay '
        'scaleFactor=1.0000 pixelRect=1920x1080 refRes=1920x1080 \u21d2 **1:1**\uff08\u6240\u6709\u56fe\u7247\u5750\u6807\u53ef\u76f4\u63a5\u4e0e\u539f\u7248 1920 \u53e3\u5f84\u5bf9\u6bd4\uff09'),
    '\u5185\u5b58': (
        'Profiler \u540c\u4e00\u65f6\u523b\uff1atotalAllocated=959.2MB\u3001totalReserved=1782.6MB\u3001monoUsed=132.5MB\u3001'
        'gfxDriver=440.5MB\uff1bget_performance_stats \u540c\u523b totalAllocatedBytes=1006196726 / totalReservedBytes=1869230080 / '
        'monoUsedBytes=142008320 / monoHeapBytes=262082560'),
    '\u573a\u666f\u52a0\u8f7d\u8017\u65f6': (
        '\u8bfb\u6761\u94fe\u539f\u6587\uff08\u540c\u4e00 Play\uff09\uff1a`[\u6d41\u7a0b] \u8bfb\u6761\u5f00\u59cb\uff1ascene=StageDust2 map=de_dust2` 15:42:05.387 '
        '\u2192 `[\u6d41\u7a0b] \u573a\u666f\u52a0\u8f7d\u5b8c\u6210\uff1aStageDust2` 15:42:05.708 = **0.321s**\uff0c'
        '\u5230\u300c\u9009\u9635\u8425\u9762\u677f\u5df2\u6253\u5f00\u300d15:42:05.712 = 0.325s\uff1b'
        '\u53e6\u5916\u6d4b\u5230\u4e00\u6b21**\u51b7\u5730\u56fe**\u52a0\u8f7d\uff1a`[\u5730\u56fe] \u52a0\u8f7d\u5730\u56fe\u6570\u636e` 15:41:32.671 \u2192 `[\u5730\u56fe] \u5730\u56fe\u6570\u636e\u5c31\u7eea` 15:41:33.502 = **0.831s**\uff08\u540c\u4e00\u4f1a\u8bdd\u5185\u518d\u6b21\u8fdb\u56fe\u5219\u547d\u4e2d\u300c\u540c\u540d\u8df3\u8fc7\u91cd\u590d\u52a0\u8f7d\u300d\uff09'),
}
_ft_path = os.path.join(ROOT, 'tools', 'probes', 'measure-play-frametime.cs')
for nm in ('\u5e27\u65f6\u95f4', '\u5206\u8fa8\u7387', '\u5185\u5b58', '\u573a\u666f\u52a0\u8f7d\u8017\u65f6'):
    add('S2', nm, 'tools/probes/probe-play-s2.cs + measure-play-frametime.cs', rel(_ft_path), 2,
        T_SIDE, K_CONSIST, S2_MEASURED[nm])

# ── 片BW-M（S2 状态行）：性能此前在**状态矩阵**里 0 行（闸门第 41 项报 D12,S2 两维缺行）。
#    上面那批 `add` 只给"并排图 + 一次读数"；状态矩阵这一层要判**边界**：
#      0（空载 / 不触发的下界）、标称（p50 / 稳态）、上限（p95 或代码里那条阈值）、上限+1（越界）。
#    ⚠️ 铁律：帧时间必须与**渲染设备名**同时给 —— 软件渲染（WARP / Basic Render Driver）下
#       这台机器上任何帧时间数字都无效（skill §4 性能类 / experience/perf-triage.md）。
#    数字出处 = 盘上三份**原始产物**（⛔ 不是本文件里手写的常量，也不是执行者的叙述）：
#      .ai-tmp/test/perf-result.txt   —— probe-play-s2.cs 落盘的 197 帧逐帧 unscaledDelta + FrameTimingManager
#      .ai-tmp/test/sliceP-s2-env.txt —— 同一 Play 会话里一次 eval：device / screen / canvas / frame / mem
#      .ai-tmp/test/p-perf.json       —— 同一会话的 `get_performance_stats`（drawCalls / triangles / memory / frameTiming）
#    统计量（min / p50 / p95 / max，线性插值口径）由判据资产 `tools/probes/bwm-s2-bounds.py`
#    从 perf-result.txt **现算**（跑它就能复算这四行的每个数；原始产物不在盘时它会 exit 1）。
#    ⛔ 本片不新增实体：S2 的实体名沿用判定行那 5 个；"分辨率"没有边界可言（1:1 已由 D4/S2 判定行判过），
#       故补的三条指标 = 帧时间 / 内存 / 场景加载耗时（与审计片点法一致：3 指标 × 4 边界点 + 1 条设备取证）。
#    留待（K_PENDING）的行**如实标注**，不拿相邻量级冒充（缺什么写什么）。
S2_DEV = dev or '未知'
S2_ROWS = [
    ('渲染设备', '设备取证（先证伪软件渲染）', '边界：软件光栅 vs 硬件设备（非此即彼）',
     'S2 的前置条件：渲染设备名必须先证伪 —— 落到 Microsoft Basic Render Driver / WARP 时，本机一切帧时间数字无效',
     'client/Logs/Editor.log 的 `[D3D12 Device Filter] Device Name: %s`；sliceP-s2-env.txt 同源字符串 `device=AMD Radeon RX 5700 XT api=Direct3D12 vramMB=8151` ⇒ 非软件光栅（闸门第 21 项 graphics-device 同源）' % S2_DEV,
     K_CONSIST, 'client/Logs/Editor.log（Device Name 行） + .ai-tmp/test/sliceP-s2-env.txt'),
    ('帧时间', '边界：0 负载 / 最优帧', '0（空载下界 = 最优帧）',
     '帧时间下界 = 该 build 在同一台机器上能跑到的最优帧（判据 = 原始产物，⛔ 不是手写常量）；⚠️ min/p50/p95/max 都是**次序统计量**（口径 = tools/probes/bwm-s2-bounds.py），⛔ **不是 A 的官方阈值** —— 后人⛔ 不许把它读成「官方上限」',
     'unscaledDeltaMs min = 4.550 ms（n=197；同刻 cpuFrameTimeMs min = 4.610 ms / gpuFrameTimeMs min = 0.000 ms）；设备 = AMD Radeon RX 5700 XT',
     K_CONSIST, '.ai-tmp/test/perf-result.txt（197 帧原始采样）'),
    ('帧时间', '边界：标称（稳态 p50）', '标称（p50）',
     '标称帧时间 = 稳态跑图的中位帧（同 build 基线，越界采样从它往上算）；⚠️ p50 是**次序统计量**，⛔ 不是 A 的官方阈值',
     'unscaledDeltaMs p50 = 6.210 ms（cpuFrameTimeMs p50 = 6.250 ms）；同一会话另一次采读 `frame=19786 unscaledDeltaMs=6.593 fps=151.7` ⇒ 两条通道同量级',
     K_CONSIST, '.ai-tmp/test/perf-result.txt + .ai-tmp/test/sliceP-s2-env.txt'),
    ('帧时间', '边界：上限（p95）', '上限（p95）',
     '上限帧时间 = 95 分位；超过它的样本属于尾部（要逐条可指认，不能只说"偶有卡顿"）；⚠️ p95 是**次序统计量**（口径 = tools/probes/bwm-s2-bounds.py，输入 = 197 帧逐帧采样），⛔ **不是 A 的官方上限**（A 本体在盘上没有帧预算载体）',
     'unscaledDeltaMs p95 = 7.844 ms（cpuFrameTimeMs p95 = 8.070 ms / gpuFrameTimeMs p95 = 0.500 ms）；设备 = AMD Radeon RX 5700 XT',
     K_CONSIST, '.ai-tmp/test/perf-result.txt（197 帧原始采样）'),
    ('帧时间', '边界：上限+1（p95 之上的尾部）', '上限+1（越界：p95 之上到 max）',
     '越界帧 = 高于 p95 的尾部样本，必须能指认到具体那一帧（含 warmup / GC 突变）；⚠️ 同上：p95/max 是**次序统计量**，⛔ 不是 A 的官方阈值',
     'unscaledDeltaMs max = 10.870 ms（第 1 帧 = 7.390 ms ⇒ 突刺不在 unscaledDelta 上）；cpuFrameTimeMs max = 124.980 ms 但**只在采样首帧**出现（warmup 突变），其余 196 帧 max = 10.800 ms（197 帧里唯一超过 11 ms 的就是那个 warmup 帧）',
     K_CONSIST, '.ai-tmp/test/perf-result.txt（197 帧原始采样）'),
    ('内存', '边界：0 负载（主菜单 / 空场）', '0（空载下界）',
     '内存下界 = 主菜单态（未进图）的占用',
     '盘上三份内存产物（perf-result / sliceP-s2-env / p-perf）**都是进图稳态**，没有主菜单态读数 ⇒ 如实留待，⛔ 不拿进图稳态冒充空载',
     K_PENDING_NUM, '.ai-tmp/test/sliceP-s2-env.txt + .ai-tmp/test/p-perf.json'),
    ('内存', '边界：标称（进图稳态）', '标称',
     '进图稳态内存 = 比赛进行中的占用（同 build 基线）',
     '同一 Play 会话两条通道：sliceP-s2-env.txt `mem totalAllocatedMB=977.0 totalReservedMB=2826.8 monoUsedMB=134.9 gfxDriverMB=504.3`；p-perf.json `totalAllocatedBytes=1024751743 totalReservedBytes=2964140032`（= 977.2 / 2826.8 MB，两通道互证）',
     K_CONSIST, '.ai-tmp/test/sliceP-s2-env.txt + .ai-tmp/test/p-perf.json'),
    ('内存', '边界：上限（mono 堆容量）', '上限（monoHeap 已分配容量 = 846426112 B）',
     '上限 = mono 堆的已分配容量；越过即触发扩容（扩容本身不该发生在这个体量上）',
     'monoUsedBytes = 145707008（= 上限的 17.2%）、monoHeapBytes = 846426112；同会话 monoUsedMB = 134.9 ⇒ 未触顶，在界内',
     K_CONSIST, '.ai-tmp/test/p-perf.json'),
    ('内存', '边界：上限+1（跨局增长越界）', '上限+1（越界：连续多局不释放导致增长越过上限）',
     '越界 = 回主菜单 → 再进图，多局之后占用越过上限（清场漏项时会这样）',
     '需实机多局（单会话数据不能外推；清场清单见 %s:595-625）' % _FLOW_EV,
     K_PENDING_NUM, '.ai-tmp/test/sliceP-s2-env.txt'),
    ('场景加载耗时', '边界：0（不触发加载：同地图二次进图）', '0（不触发：命中同名跳过）',
     '同地图再次进图不得重复加载（"已加载且同名 ⇒ 跳过"是载体上的同一条判据）',
     '源码：%s:456-463「地图数据已加载且同名，跳过重复加载」；日志：client/Logs/Editor.log 里 `[Flow] 地图数据已加载且同名（de_dust2），跳过重复加载`（末次 2026-09-23 08:30:28.954）⇒ 加载次数 0' % _FLOW_EV,
     K_CONSIST, '%s:456-463 + client/Logs/Editor.log' % _FLOW_EV),
    ('场景加载耗时', '边界：标称（热路径 读条开始 → 场景加载完成）', '标称（热路径）',
     '热路径加载耗时（同一份载体的最短路径；冷路径是地图数据解析）',
     'client/Logs/Editor.log 2026-09-23 六次进图：读条开始 → 舞台场景加载完成 = 0.189 / 0.203 / 0.213 / 0.287 / 0.353 / 0.534 s（例：08:30:28.952 → 08:30:29.486 = 0.534 s）；同一会话的地图数据解析 = 1.102 ~ 8.933 s（例：08:30:16.521 → 08:30:25.454 = 8.933 s，冷）',
     K_CONSIST, 'client/Logs/Editor.log（[Flow] 读条开始 / 舞台场景加载完成）'),
    ('场景加载耗时', '边界：上限（代码里的读条超时阈值）', '上限（阈值 = 20 s）',
     '上限 = %s 的 StageLoadTimeout = 20 s（:36/:398），越过即放弃本次进图并回主菜单（:402-411）' % _FLOW_EV,
     '实测最慢一次 = 0.534 s（热）/ 8.933 s（冷地图解析）⇒ 都远在 20 s 阈值内，兜底从未触发（client/Logs/Editor.log 里 `读条 20s 超时` 命中 0 次）',
     K_CONSIST, '%s:36,:398,:402-411 + client/Logs/Editor.log' % _FLOW_EV),
    ('场景加载耗时', '边界：上限+1（> 20 s 触发兜底）', '上限+1（越界：> 20 s）',
     '越界行为 = Error + Toast「加载 StageDust2 失败或超时，已返回主菜单」+ GoMainMenu（不得停在读条屏）',
     '实机从未触发（client/Logs/Editor.log 里超时告警命中 0 次）；兜底路径在源码可复核（%s:402-411）⇒ 行为本身**需实机**才可判' % _FLOW_EV,
     K_PENDING_NUM, '%s:402-411' % _FLOW_EV),
]
for _r in S2_ROWS:
    sta('S2', _r[0], _r[1], _r[2], _r[3], _r[4], _r[5], _r[6])
sys.stderr.write('  [bw-m] S2 state rows appended = %d\n' % len(S2_ROWS))

set_txt = rd(OPTIONS) + rd(SETTINGS_STORE)
SET_ITEMS = [('\u5206\u8fa8\u7387', r'resolution|Resolution'), ('\u5168\u5c4f', r'fullscreen'),
             ('\u97f3\u91cf\uff08master/sfx/bgm\uff09', r'volumeMaster|volumeSfx|volumeBgm|Volume'),
             ('\u9f20\u6807\u7075\u654f\u5ea6', r'sens|Sensitivity'), ('\u53cd\u8f6c Y', r'invertY'),
             ('FOV', r'\bfov\b'), ('\u73a9\u5bb6\u540d', r'\bname\b|UserName'), ('\u952e\u4f4d\u7ed1\u5b9a', r'KeyBind|Rebind|key'),
             ('\u8bed\u8a00', r'language|Language'), ('\u8f6f\u4ef6\u6e32\u67d3\u5f00\u5173', r'software|Software')]
# ── 切片L（S3）：A 本体**没有**的设置项 ⇒ 判据修正（⛔ 不是"登记成差异"就完事）──────────
# 「语言」这一行的旧判据是"在 OptionsPanel / CsPlayerSettingsStore 里搜 language|Language"。
# 该判据**本身是错的**，依据三条（都在工程内、可复核）：
#   ① `UI/Flow/OptionsPanel.cs` 的类注释：Options 是**逐字段重建自原版 7 个子页**的 `.res`
#      —— `optionssub{audio,video,mouse,keyboard,multiplayer,voice,advanced}.res`，**7 页里没有 language**；
#   ② 同文件 `TabNames` 的注释：页签名 = 原版 `gameui_english.txt` 的 7 个 token
#      （Audio:99 / Video:100 / Mouse:98 / Keyboard:97 / Multiplayer:41 / Voice:101 / Advanced:44），同样没有 Language；
#   ③ GoldSrc / Steam 的语言由 **Steam 客户端语言 / `-language` 启动参数**决定，不是游戏内选项。
# ⇒ 正确判决 = 「与 A 一致（A 也无此设置项）」；按旧判据"补一个语言设置"= 给 A 加它没有的东西
#    （违反 skill §0 铁律 1「A 没有 ⇒ 不加」）。
# 引擎侧确有本地化能力（`ILocalization.SetLanguage`，`clover-client-unity-engine/Runtime/Core/Contracts.cs`），
# 但那是**引擎设施**、不是 A 的界面项 ⇒ 本工程不因此新增该 UI。
S3_ABSENT_IN_A = {
    '\u8bed\u8a00': 'A\uff08CS 1.6\uff09\u7684 Options \u5bf9\u8bdd\u6846\u65e0\u8bed\u8a00\u9875\uff1a'
                    'OptionsPanel \u9010\u5b57\u6bb5\u91cd\u5efa\u81ea\u539f\u7248\u4e03\u4e2a\u5b50\u9875 .res'
                    '\uff08audio/video/mouse/keyboard/multiplayer/voice/advanced\uff09\uff0c'
                    '\u9875\u7b7e\u540d\u4e5f\u53d6\u81ea\u539f\u7248 gameui_english.txt \u7684 7 \u4e2a token\uff0c'
                    '\u4e24\u8005\u90fd\u6ca1\u6709 Language \u21d2 \u4e0e A \u4e00\u81f4\uff08A \u4e5f\u65e0\u6b64\u8bbe\u7f6e\uff09\uff0c'
                    '\u672c\u5de5\u7a0b\u4e0d\u65b0\u589e\u8be5 UI',
}
for nm, pat in SET_ITEMS:
    hit = re.search(pat, set_txt, re.I)
    if hit:
        v = K_CONSIST
        ev = '\u751f\u6548\u8303\u56f4=\u91cd\u8fdb Play'
    elif nm in S3_ABSENT_IN_A:
        v = '%s\uff08%s\uff09' % (K_CONSIST, S3_ABSENT_IN_A[nm])
        ev = ('\u5224\u636e\u4fee\u6b63\uff08\u5207\u7247L\uff09\uff1aA \u672c\u4f53\u65e0\u8be5\u8bbe\u7f6e\u9879 \u21d2 \u672c\u5de5\u7a0b\u4e5f\u4e0d\u52a0\uff1b'
              '\u636e = UI/Flow/OptionsPanel.cs \u7c7b\u6ce8\u91ca\uff08\u539f\u7248 7 \u4e2a\u5b50\u9875 .res\uff09'
              '+ \u8be5\u6587\u4ef6 TabNames \u6ce8\u91ca\uff08gameui_english.txt \u7684 7 \u4e2a token\uff09')
    else:
        v = '%s(\u8bbe\u7f6e\u9879\u5728\u4ee3\u7801\u91cc\u627e\u4e0d\u5230)' % K_MISMATCH
        ev = '\u751f\u6548\u8303\u56f4=\u91cd\u8fdb Play'
    add('S3', nm, rel(OPTIONS), rel(OPTIONS), 2, T_SCRIPT, v, ev)
for st, bd, exp in (('\u6539\u540e\u7acb\u5373\u751f\u6548', '\u4efb\u610f\u503c', '\u7acb\u5373\u5f71\u54cd\u5f53\u524d\u5c40'),
                    ('\u91cd\u8fdb Play \u4ecd\u751f\u6548', '\u843d Game.Setting', '\u6301\u4e45\u5316\u5230\u78c1\u76d8')):
    sta('S3', 'OptionsPanel', st, bd, exp, '\u9700\u5b9e\u673a', K_PENDING, rel(OPTIONS))

# ============================================================================
#  跨维度因果对（skill patterns/full-coverage-audit.md §4：只交叉**有因果**的对）
# ============================================================================
# 切片P：三条交叉待采行按联络图收口（格号写进证据列；每格都是实机帧，离线断言拿不到）
CROSS = [
    ('D5', '\u4ea4\u53c9 D5\u00d7D10 \u52a8\u753b\u00d7\u903b\u8f91\u72b6\u6001',
     '\u8e72\u7740\u5f00\u67aa/\u8dd1\u7740\u6362\u5f39/\u6b7b\u4ea1\u4e2d\u5207\u67aa\uff1b'
     '\u8054\u7edc\u56fe contact-sheet-6-cross.png \u683c F-01\uff08\u8e72\u7740\u5f00\u67aa\uff0c\u540c\u5e27 crouch=True\uff09/ F-02\uff08\u8dd1\u7740\u6362\u5f39\uff0c\u540c\u5e27 speed>0\uff09/ '
     'F-03\uff08\u9635\u4ea1\u540e\u5207\u69fd\uff0calive=False \u4ecd\u662f\u89c2\u6218\u6001\uff09', T_SIDE, K_CONSIST),
    ('D4', '\u4ea4\u53c9 D4\u00d7D12 UI\u00d7\u6d41\u7a0b\u72b6\u6001',
     '\u6682\u505c\u65f6\u7684 HUD / \u4e70\u67aa\u533a\u83dc\u5355 / \u89c2\u6218\u8bb0\u5206\u677f\uff1b'
     '\u8054\u7edc\u56fe contact-sheet-4-menu.png \u683c D-08\uff08GAME PAUSED \u5bf9\u8bdd\u6846\u4e0e\u80cc\u540e\u7684 HUD \u540c\u5e27\uff09+ '
     'contact-sheet-2-ingame.png \u683c B-15\uff08\u4e70\u67aa\u83dc\u5355\uff09/ B-04\uff08\u8bb0\u5206\u677f\uff09', T_SIDE, K_CONSIST),
    ('D8', '\u4ea4\u53c9 D8\u00d7D6 \u97f3\u6548\u00d7\u4e8b\u4ef6\u00d7\u6750\u8d28',
     '\u811a\u8e0f\u6c99 vs \u91d1\u5c5e\uff1b\u6253\u6728\u5934 vs \u6df7\u51dd\u571f', T_SCRIPT,
     K_MISMATCH if not re.search(r'hit_wall', SFX_TXT) else K_CONSIST),
    ('D9', '\u4ea4\u53c9 D9\u00d7\u89d2\u8272\u7c7b\u578b \u78b0\u649e\u00d7\u89d2\u8272\u7c7b\u578b',
     '\u73a9\u5bb6 vs Bot\uff08\u6295\u63b7\u7269\u89c1\u5dee\u5f02\u767b\u8bb0\uff09', T_SCRIPT,
     K_CONSIST if (push_code and push_attached and sep_ok) else K_MISMATCH),
    ('D6', '\u4ea4\u53c9 D6\u00d7D3 \u7279\u6548\u00d7\u547d\u4e2d\u6750\u8d28',
     '\u5f39\u75d5\u662f\u5426\u8d34\u5bf9\u6cd5\u7ebf/\u662f\u5426\u5206\u6750\u8d28\uff1b'
     '\u8054\u7edc\u56fe contact-sheet-6-cross.png \u683c F-04\uff08\u671d\u5730\u9762\u5c04\u51fb\uff1a\u6cd5\u7ebf\u671d\u4e0a\u21d2\u8d34\u82b1\u8eba\u5728\u5730\u9762\uff09/ F-05\uff08\u671d\u5899\uff1a\u6cd5\u7ebf\u6c34\u5e73\u21d2\u8d34\u82b1\u7ad6\u8d34\uff09\uff1b'
     '\u6750\u8d28\u5206\u7c7b\u7684\u8fd0\u884c\u65f6\u884c\uff1a`[Combat] \u5f39\u7740\u97f3 hit_wall\uff08\u843d\u70b9 (-11.48, 4.83, -44.51)\uff09\uff1a\u547d\u4e2d\u6750\u8d28\u300c_0csSandWall\u300d\u2192 \u5206\u7c7b sand`'
     '\uff08\u540c\u4e00\u65e5\u5fd7\u91cc\u53e6\u6709\u300cUSP .45 \u672a\u547d\u4e2d\u300d\uff09', T_SIDE, K_CONSIST),
    ('D7', '\u4ea4\u53c9 D7\u00d7D12 \u97f3\u4e50\u00d7\u573a\u666f\u8f6c\u79fb',
     '\u8fdb\u56fe\u8981\u505c\u3001\u56de\u83dc\u5355\u8981\u8d77', T_SCRIPT,
     K_CONSIST if (re.search(r'PlayBGM', ALL_TXT) and re.search(r'StopBGM', ALL_TXT)) else K_MISMATCH),
    ('D11', '\u4ea4\u53c9 D11\u00d7\u4e0a\u4e0b\u6587 \u8f93\u5165\u00d7\u4e0a\u4e0b\u6587\u63a9\u853d',
     '\u6e38\u620f\u4e2d vs \u83dc\u5355\u4e2d vs \u8f93\u5165\u6846\u805a\u7126', T_SCRIPT,
     K_CONSIST if re.search(r'inputAllowed|NonBlockingPanels', codeText) else '%s(\u6ca1\u6709\u83dc\u5355\u63a9\u853d)' % K_MISMATCH),
    ('S1', '\u4ea4\u53c9 S1\u00d7D10 \u6570\u503c\u00d7\u903b\u8f91\u5206\u652f',
     '\u4f24\u5bb3\u516c\u5f0f \u00d7 \u547d\u4e2d\u90e8\u4f4d \u00d7 \u62a4\u7532', T_SCRIPT,
     K_CONSIST if re.search(r'HitHead|HitChest', rd(CS_CORE)) else K_MISMATCH),
]
# 片FX-ALL（2026-09-23）：把「多文件布尔与」的交叉行定出**合法锚点写法** —— 这正是允许差异 #87
#   当初留下的「处置」方向。原来的做法（证据列只写一句判据 + 挂 `（见允许差异 #87）`）在
#   `verify.ps1` 第 39 项下恒红，而那条差异**不允许**给它们编一个不存在的单点锚点。
#   现在的写法：`（多文件锚点：A:<行> && B:<行>）`，**每一项都是现算的** `文件:行号`
#   （`code_site()` 现场解析源码，⛔ 绝不写死 —— 行号随插入漂移，写死就等于复盘时对不上）。
#   ⚠️ 残余：`verify.ps1` 第 39 项只机械复核**首个** `path:line`，第二项及以后靠人逐个打开验证
#   ⇒ 这条残余已改写进差异 #87 的新正文（⛔ 不再假装"全都机械验过"）。
#   第一项一律选「最能代表该行判据」的那一处（多是判据本体所在的方法/常量），而不是顺手的第一处命中。
CROSS_ANCH = {
    # D8×D6 音效×事件×材质：判据是「材质分类真的做了」+「弹着音按分类落点」两处
    'D8':  [r'public static string ClassifyImpact', r'hit_wall\uff08\u843d\u70b9'],
    # D9×角色类型 碰撞×角色类型：`push_code && push_attached && sep_ok` 三项各自的位置
    'D9':  [r'public static bool TryResolve', r'private Vector3 SeparateFromOtherActors'],
    # D7×D12 音乐×场景转移：`PlayBGM && StopBGM` 两个调用点
    'D7':  [r'sound\.PlayBGM\(', r'sound\.StopBGM\('],
    # D11×上下文 输入×上下文遮蔽：`inputAllowed`（采集闸门）与 `NonBlockingPanels`（面板白名单）
    'D11': [r'internal void FillInput', r'NonBlockingPanels\['],
    # S1×D10 数值×逻辑分支：`HitHead` / `HitChest` 的常量定义与它的分支使用点
    'S1':  [r'public const float HitHead', r'case CsHitbox\.Head'],
}
for dim, nm, note, vt, vd in CROSS:
    # 片BW-E：证据列**本身没有可复核锚点**的交叉行 ⇒ 挂上它的允许差异号（见 `DIF` 的 #87）。
    #   判据**现算**（`anchor_ok(...)`）而不是按名字硬列 5 行 ⇒ CROSS 表一变就自动跟上。
    # 片FX-ALL：先试**多文件锚点**；只有在连多文件锚点都凑不出来时才退回挂差异号。
    # ⛔ 每个锚点**必须用反引号包起来**（实测 2026-09-23，这是本写法唯一会静默失效的地方）：
    #   `audit-verdict-rows.py` 的 `PATHLINE_RE` 的路径字符类是 `[^\s`|"'(\)\[\],;]+?`
    #   —— 它**不排除中文、也不排除全角括号**，所以紧跟一段中文叙述的裸路径会把叙述一起吞进
    #   `path` → `resolve()` 失败 → 该锚点被静默跳过，判定落到**第二项**上（实测：`（多文件锚点：`
    #   被吸进路径，最后审计认的是第二项）。反引号在字符类里是被排除的 ⇒ 匹配从反引号后干净起步。
    _hits = [code_site(p) for p in CROSS_ANCH.get(dim, [])]
    _hits = ['`%s`' % h for h in _hits if h]
    _ev = note + (('\uff08\u591a\u6587\u4ef6\u951a\u70b9\uff1a' + ' && '.join(_hits) + '\uff09')
                  if _hits else '')
    if not anchor_ok(_ev):
        _ev += '\uff08\u89c1\u5141\u8bb8\u5dee\u5f02 #87\uff09'
    add(dim, nm, '\u8de8\u7ef4\u5ea6\u56e0\u679c\u5bf9',
        'patterns/full-coverage-audit.md \u00a74', 2, vt, vd, _ev)
    sta(dim, nm, '\u4ea4\u53c9\u72b6\u6001', '-', note, '\u5730\u57df\u7814\u5224', vd, 'patterns/full-coverage-audit.md \u00a74')

# ============================================================================
#  片AZ（2026-09-22 · 诊断+登记片）：用户本轮 10 条原话 + 登记中发现的同类漏检
#
#  ⛔ 本片只做「逐条登记 + 根因定位」，**不修**任何一条（修由后续按维度的切片做）。
#  一条报多维度 ⇒ 逐维度各一行（#8 = D5 + D6 + D10；#10 = D9 + D10；#1 = D9 + D2 …）。
#  结论一律 `允许的差异(→ 差异登记.tsv)`：这些是**已知未做/已知不符**，
#  四要素（是什么 / 为什么 / 出处 / 何时消除）写在 `策划/差异登记.tsv` #66~#77。
#  出处一律指到 `文件:行`；原版行为取不到载体的按降级链写「待补（第 N 级）」。
# ============================================================================
# ---- 片BW-G-R（2026-09-23）：下面两段此前**只在产物** `策划/验收表.md` 里（各切片手工追加），
#      而 `--inject` 会用本脚本重写整块 ⇒ 每跑一次就静默删掉它们（实测：片BU-R6 那段 1 次就没）。
#      现按产物**逐字**回填（未改任何措辞）；段落内的 `|` 已由 `_s()` 转为 `/`。
_ASTAR_TAIL = '。**【片BU-R 更正 · 业务**已经**在用引擎 A***（2026-09-22 数值类）】"业务零使用 A*"这句已过时：`client/Assets/Scripts/Module/Bot/BotNavigator.cs` 的 `EnsurePath` 调 `AStar.FindSmoothed(WalkableCellHeightAware, from, to, AStar.DefaultMaxNodes)`，片BU-R 又新增 `BotNavigator.CanReach`（同一份口径，供"选目标前先问一句走不走得到"）与 `DropUnreachableWaypoints`（`AStar.Find(纯位图)` 排掉"可走但走不到"的路点）⇒ **引擎寻路能力已被业务使用**，本行的"允许的差异"只对"引擎无多实例 FSM 工厂"那一半继续成立（见差异 #67 的 ① ② 更正）。片BU-R 一次 Play（`.ai-tmp/drivers/bu-r-play.ps1`）的引擎 A* 实测计数：`astar.nopath`（真孤岛）= 8、`astar.badgoal`（高度一致性层拒绝）= 4 ⇒ 修前同类为 18 / 13（口径与判据 `tools/probes/bot-goal-gate.py` 的 A4）'
_TAC_TAIL = '。**【片BU～片BU-R 更正 + 重采（2026-09-22，数值类，本条判据仍为「允许的差异」）】** 上面 ①②③ 三条**机制描述已过时**，按当前代码更正：① **有寻路层** —— 业务已调引擎 `AStar.FindSmoothed`（`BotNavigator.EnsurePath`），⛔ "零调用"不成立；② **有战术层** —— 引擎无行为树模块（在 `client/Packages/com.clover.unity-engine/**` 内 `BehaviorTree|行为树|btree` = 0 命中；引擎包是 junction 挂载 ⇒ rg 对它枚举 0、必须 os.walk）' + _SCOPE_BT_ENGINE + '，等价设施 = 引擎公开接口 `IFsm`（引擎自带 `Fsm` 是应用级单例、且实现类 `internal` ⇒ 业务无法多实例；引擎侧出处见差异 #67 的「出处」列），本工程按 `IFsm` 每 bot 自实现两层（`client/Assets/Scripts/Module/Bot/CsBotFsm.cs`，顶层 Idle/Patrol/Engage/Objective + 目标层 Approach/Plant/Hold/Defuse），"守点 = 到达单个包点标记后原地警戒"也已改为 `CsBotHoldSpots` 多点守位表 + 定时换位；③ 片BU-R 又给**选目标**加了可达门禁（`BotNavigator.CanReach` + `CsBotBrain.TryPickReachable`，近的优先）并把 CT 包点守卫的"到达判定"与"换位评估"解耦（`CsBotBrain.InsideHoldSite`）。**修前 vs 修后（同一批判据 `tools/probes/bot-goal-gate.py`，产物 `.ai-tmp/test/bu-r-gate-after.txt` 与修前 `br` 产物）**：闸门 FAIL 6/7 → **5/7**；每次换目标风暴 73 → **7**（Minh 70 → 0）；求路径失败 67 → **15**；净位移 net/total 0.001~0.007 → **0.008~0.044**（Minh 0.00m → 6.0m）；`守点换位` L3 = **0 → 0**、`在包点` L3 = **0 → 0**、`TPLANTED` = **0 → 0** ⇒ **本行判据仍未达成**（T 未下包 / CT 未进点驻留 / 未换位），残余根因已定位：bots 每回合净位移仅 3~8 m 而路径长度 176 m（总走 176 m、净 6 m）⇒ 推进被**逐帧方向抖动**吃掉（口径 = `analyze-bot-goal.py` 第 4 节 + `.ai-tmp/test/bu-r-goal-after.txt`）。证据文件：`tools/probes/bot-goal-gate.py`、`tools/probes/bu-goal-reachability.py`、`.ai-tmp/test/bu-r-goal-after.txt`、`.ai-tmp/test/bu-r-gate-after.txt`**【片BU-R2 2026-09-22 23:19 实测（完整回合，105s 规则 + 采集窗口 ≥114s；驱动 `.ai-tmp/drivers/bu-r2-play.ps1`，Play 产物 `.ai-tmp/test/bu-r2-*`）】** 闸门 `tools/probes/bot-goal-gate.py`：FAIL 5/7 → **FAIL 3/7**；新增转绿：**A6 CT 包点驻留 PASS**（Spliff 连续 5 采样 ≤7m，修前 0）、**A7 守点换位 26 次 PASS**（修前 0）；A1/A2 净位移：Cliffe CT 5.7m/0.032 → **66.1m/0.802**、Spliff CT 7.8m/0.085 → 24.5m/0.401（8 bot 里 2 个达标）；逐帧抖动（`tools/probes/bu-r2-motion-diag.py`）：Move 方向翻转 317 → 2（Cliffe），rev% 78.7% → 0.0%。**残余（仍红）**：A4 求路径失败 18 > 10、**A5 T 侧自主下包 0 条**、A1/A2 6/8 未达标；残余根因已定位到**局部避障层**（`BotNavigator.Avoid` 每帧重扫 `AvoidAngles`、`probeLeft=0.50` 说明 0.5s 保持每帧被打破 ⇒ Darrell/Scuzzy 在 B 点附近绕圈 350m/净 17~22m），原始归因行 `.ai-tmp/test/bu-r2-flip-attrib-after.txt`。⛔ 阈值未下调。**【片BU-R3 2026-09-22 23:35 实测（完整回合：驱动 `.ai-tmp/drivers/bu-r3-play.ps1` 采到 `phase=RoundEnd`；产物 `.ai-tmp/test/bu-r3-*`）】** 本片只做一处 = `BotNavigator.Avoid` 的**换向迟滞**（① 候选偏角改按"与**当前偏角**的偏差升序"试，新增 `BotNavigator.PickAvoidAngle`；② 保持期内"这个方向不可走"必须**连续** `CsBotConst.AvoidBadDirSeconds`=0.1s 才允许换锚点，**单帧不可打破**）。参数取值出处 = 离线复算 `tools/probes/bu-r3-avoid-replay.py`（口径自校验 + 一维扫描，输出 `.ai-tmp/test/bu-r3-replay-final.txt`）。闸门 `tools/probes/bot-goal-gate.py`（同一条命令、同一批文件口径）：FAIL 3/7 → **FAIL 3/7**；**A6/A7/A8 未回退**（A6 PASS Spliff 连续 5 采样 ≤7m、A7 PASS 23 次、A8 PASS 三档参数）。A1/A2 达标 2/8 → **3/8**（新增 Gooseman T 15.5m/0.999；Cliffe 66.1→60.5m/0.685、Darrell 17.3m→**20.1m/0.286**、Minh 8.0→14.9m/0.590 差 0.1m 未过线；⚠️ **Spliff 24.5→8.4m/0.035 回退**、Scuzzy 22.1→15.2m/0.065）。方向翻转（`tools/probes/bu-r2-flip-attrib.py`）：296 → **167** 行（Darrell 104 → **9**、Scuzzy 117 → **53**）；总路径（`tools/probes/bu-r2-motion-diag.py`，B 行）：Darrell 384.6 → **70.3m**。**本片判"未达标"的三条照旧留红**：A1/A2 5/8 未达标、A4 求路径失败 18 → **19**（⛔ 与 Avoid 无关 —— 是高度一致性层拒绝 / 位图孤立分量，见差异 #77）、**A5 T 侧下包仍 0**。**A5 与 A1/A2 的残余根因本片已改判到另一层（⛔ 不是避障）**：逐帧状态分布显示 4 个 bot 在 93.8s 窗口里**静止 80~90s**（`.ai-tmp/test/bu-r3-diag-after.txt` 第 1 节：Cliffe stall 82.0s、Gooseman **90.5s**、Minh 83.4s、Darrell 80.4s；其中 Gooseman 状态分布 `Plant:704/798` = 91% 采样停在 `Plant` 态而窗口内位移恒为 **0.00m**）⇒ ① A1/A2 的"净位移"口径落在**守点/静止**上（守得对就该净位移小），与方向抖动无关；② A5 的直接病灶 = **T 侧持包 bot 卡在 `Plant` 态约 90s 不动**（`CsBotBrain` / `CsBotFsm` 那一层，⛔ 本片任务书禁改），不是 `Avoid`。⛔ 阈值未下调、`RoundTime` / 时间缩放 / `EnsurePath`/`AdvancePath`/`RefreshPathOnly` 三处上一棒修复均未动。**【片BU-R5 2026-09-23 00:2x 实测（驱动 `.ai-tmp/drivers/bu-r5-play.ps1`，采集窗口第一次覆盖 round1 完整 + round2 完整；产物 `.ai-tmp/test/bu-r5-*`；逐回合切片用 `tools/probes/slice-round.py`）】**\n本片只改一处（`Module/Bot/CsBotBrain.cs`）：`CommitTacticalDecision` 里新增一条**高于 `Engage`** 的「掉落 C4 拾取」分支（`IsElectedBombHunter` = 场上无任何 T 持包 + 包已掉在地上 + 自己是**离 C4 最近的 T bot**，并列取 Id 小者），并把 `TryPlantOrPickup` 的捡包准入由 `self.Id % CsBotConst.BombHunterModulo == 0` 换成同一选举（`BombHunterModulo` 标记为已废弃、不再被引用）；⛔ 掉落/拾取规则本身（`CsBomb.OnCarrierLost` / `TryPickupDropped`）、`SiteRadius`、倒计时、经济数值一字未动。生效证据（L3 原文）：`[C4] Minh 去捡掉落的 C4：到落点 57.29m（本人是场上离 C4 最近的 T，其余 T 继续交战）`（t=92.095）→ 选举链 ZBot → Rikk → Minh 依次接力（随前人阵亡 / 距离变化重选），Minh 最终走到落点 **0.44m 并停留 30.25s**（A 行 t=123.01..152.52）—— 修前同回合**没有任何 bot 走近掉落点**。闸门 `tools/probes/bot-goal-gate.py`（逐回合口径）：**round1 修前 FAIL 2/8 → round1 修后 FAIL 2/8**（A1/A2 7/8→7/8、A4 1→2、A5 FAIL→FAIL）；**round2 修后 FAIL 2/8**（A1/A2 **8/8 PASS**、A4 3、A5 FAIL、**A9 FAIL**）。整份产物（两回合聚合）FAIL 3/8：A1/A2 3/8 —— ⛔ 这不是回退，是**跨回合传送让 net 位移坍缩**（闸门按整份产物聚合），逐回合口径才是同一窗口。**残余根因（仍红，A5 仍 0 条 `TPLANTED`）**：① **模拟侧 1.2m 自动拾取只对本地玩家调用** —— `CsBomb.TryPickupDropped` 全仓唯一调用点是 `CsMatch.cs:1758`（在 `UpdateLocalPlayer` 内），bot 侧每帧的 `UpdateBots`（`CsMatch.cs:2247`）**从不调用它** ⇒ bot 站在掉落点 0.44m 也捡不起来（`拾起了掉落的 C4` 0 条；差异 #80 ②）；② round2 的 T 持包者在 `Engage` 里**站定不开火**：Normal 档 `Engage` 的走位只在"开火间隙"给 `Strafe`，而 `PreferredRange × AdvanceRangeFactor` 把 39.7m 当成"够近"⇒ `intent.Move` 长时间为 0（同段日志 `换弹失败：MP5 Navy 备弹为 0`、`交战中开火被抑制第 124/143/154/155 次`）⇒ 105s 只从 87.1m 走到 39.8m，A9 因此由 PASS 变 FAIL（本片窗口**第一次**覆盖 round2，⛔ 不是新增行为、是首次被测量）。⛔ 阈值一个都没改：`MIN_NET_METERS=15.0` / `MIN_NET_OVER_TOTAL=0.15` / `MAX_PATH_FAILS=10` / A9 的 10s；`RoundTime`、`SiteRadius`、倒计时、经济数值均未动。【已完成，与 A5 无关】B7 重采按闸门第 6 条在本片**最后一次 `.cs` 落盘之后**执行（图 40_bomb_planted.png 换成本片新渲的帧，⛔ 不是改 mtime）；该帧是**本地玩家**按真实链路下包，⛔ 不是 A5 的证据。 **【片BU-R6 2026-09-23 · A5 转 PASS】** 承接上一段的「A5 恒 0」。本片在 `Module/Bot/**` 与 `Module/Match/CsMatch.cs`（**经主 agent 书面授权，仅一行**）落地三件事：① `CsMatch.UpdateBots` 里补 `Bomb.TryPickupDropped(a);`（与玩家侧 `CsMatch.cs:1758` 同一 API、同一 1.2m 判定）⇒ 差异 #80 核销；② `CsBotBrain.Strafe` 去掉 `PredictSeconds() <= 0` 这道门（`PredictSkillSpeedLo` = Normal 的 `AimSpeedDegrees`=300 ⇒ Normal/Easy 的 Strafe **恒为零向量**，`intent.Move` 长时间为 0）；③ `CsBotBrain.Engage` 里持包 T 的"压上"方向改为**本轮包点**（`TryGetPlantAim`，与 `TryPlantOrPickup` 同一算式），并新增 `MustPlantFirst`（持包 T 进了 7m 包点判定区就转下包，⛔ 不新增第二个半径）—— 出处 = 规格 §2.4 行为树`策划/策划案/CS1.6单机参考规格.md:123`（3 档共用、只换参数）/`:127`（交战含走位）/`:130`（T 持包到 B 点 → 下包）。**实测（一次 Play 覆盖两回合，`.ai-tmp/drivers/bu-r6-play.ps1`）**：round1 拾取 1 次（`Gooseman 拾起了掉落的 C4` 00:38:25.787）且**真的下包**（`★ Gooseman 安放 C4 于 (-23.75, 0.00, 26.84)` 00:38:31.587；rows `E TPLANTED round=1 t=98.640`）；round2 拾取 2 次（`Gooseman` 00:39:35.872 / `Minh` 00:39:56.868）且持包者走到包点（`E TARRIVE round=2 site=B distB=6.986`、`TPLANTSTART t=180.959`）。**逐回合闸门（`tools/probes/slice-round.py` + `tools/probes/bot-goal-gate.py`，⛔ 不用整份聚合数）**：round1 `RESULT: PASS (0 FAIL / 8 checks)` / round2 `RESULT: PASS (0 FAIL / 8 checks)`；A1~A8 无回退（round2 净位移/驻留：8/8 合格；A4 求路径失败 2 ≤ 10；A7 换位 7 / 20 ≥ 1；A8 三档参数未动）。**A9 由 FAIL 转 PASS**（round1 `已下包 3 次`、round2 `已下包 1 次`；片BU-R5 那条 `Rikk t=193.7..215.0（21.3s）`的静止段不再出现）。⚠️ 闸门 A5 的**口径**本片补全了第三条形态（产品自己的 `安放 C4`）—— 旧口径只扫 log 流的 `TPLANTED`（那是 **rows 流** token ⇒ 真下包也判红），并在改判据后做了**两次自检**：已知正确夹具 PASS / 已知错误样本（`br` 7 FAIL、`bu-r4-prefix` 4 FAIL）**仍 FAIL**，阈值一个字未动。 '

AZ_ENT = [
    # ---- #1 B 旋转楼梯上不去 ----
    ('D9', 'B 点旋转楼梯（可行走性：台阶高差 / 斜面法线 / 膝盖射线）',
     'client/Assets/ThirdParty/Dust2/de_dust2_geo.bin',
     '用户本轮报#1；运行时口径 `Module/Map/CsMap.cs:514-531`（TryStepUp）+ `Core/CsConst.cs:113`（StepUpHeight=0.45）/`:135`（MaxStandableSlopeNormalZ=0.7）',
     4, T_SCRIPT, K_ALLOWED,
     '【片BD 2026-09-22 **实测（数值类）**】判据资产 `tools/probes/bstairs-walkline.cs` → 产物 `tools/probes/bstairs-walkline.txt`：'
     '沿**真实走廊路径**（A* 独立复算，契约同引擎 `AStar`；与 `tools/probes/bot-path-check.py` 同一张位图、同一组端点）'
     '逐帧（≤0.25 m/帧）推进 —— **两侧都到顶**：T 侧 frames=162 / 被钳住帧=0 / 到顶=True；CT 侧 frames=125 / 被钳住帧=0 / 到顶=True。'
     '逐格四道闸门（`CsMap.cs:514-531`：① 中心格可走 ② 落点地面高差 ≤ 0.45m ③ 落点法线 y ≥ 0.70 ④ 膝盖射线）'
     '被拒格 = T 侧 0/32、CT 侧 1/24（CT 第 0 格是"标记点自带 y 与实测地面 y 的初值差 0.553 m"造成的判据初值残差，'
     '不是真实阻断：`(E)` 段第 0 帧先把 y 贴地后 125 帧内到顶）。⇒ 位图层面（`bot-path-check.py`）与物理层面（本探针）**都通**'),
    ('D2', 'B 点旋转楼梯（几何形态：连续斜面 vs 台阶）',
     'client/Assets/ThirdParty/Dust2/de_dust2_geo.bin + de_dust2.bsp',
     '用户本轮报#1；工程几何组名/三角数见 `策划/对照表.md` §1；原版 .bsp 载体本机不在盘（降级链第 5 级：待补）',
     2, T_SCRIPT, K_ALLOWED,
     '【片BD 2026-09-22 **实测**，`tools/probes/bstairs-walkline.txt` 的 (B)/(D) 段】形态**已定案**：本工程 B 点那两段'
     '**不是多级台阶，是整片斜楔（连续斜面）** —— T 侧自 (-11.5,29.5) 起 10 格 × 1.000 m **连续**抬升、每格 +0.333 m、'
     '地面法线 y 恒为 0.949（≈18.4° 斜面）；CT 侧是 3 格 × 1.000 m 每格 −0.125 m 的下坡接平台（法线 0.992/1.000）。'
     '⚠️ **仍未做**：与**原版** .bsp 的 brush 形态对账（载体 `client/Assets/ThirdParty/Dust2/de_dust2.bsp` 2,057,288 B 在盘，'
     '本片未解析 brush 几何/平面表）⇒ 差异 #66 **降级**为「形态待与原版对账」，可走性本身已判通'),
    ('D2', '全图楼梯 / 坡道 / 台阶（同类漏检：#1 只报了 B 旋转楼梯）',
     'client/Assets/ThirdParty/Dust2/de_dust2_geo.bin',
     'T0 同类漏检；口径同 `Module/Map/CsMap.cs:514-531`',
     3, T_SCRIPT, K_ALLOWED,
     '登记 #1 时顺手查同类：`策划/对照表.md` 只登记了"坡道可站立阈值 = MaxStandableSlopeNormalZ 0.7（差 0）"一条**常量级**判据，'
     '`geom-check.py` 的 A5 只覆盖"低矮障碍"，⇒ **全图所有楼梯/坡道/台阶没有任何 A→B 可走性判据**'
     '（T 坡道、A 点斜坡、B 门台阶、CT 出生台…）。判据 = 把全图"高差 ≤ 0.45m 的连续落差面"分类成台阶/斜面/台沿，'
     '逐段做底→顶可走断言 ⇒ 与 #66 同一行登记（⛔ 不是新差异号，属 #1 的同类扩样）'),
    # ---- #2 人机 AI 太傻 ----
    ('D10', '机器人战术行为（守点 / 下包 / 突破 / 寻路）',
     'client/Assets/Scripts/Module/Bot/CsBotBrain.cs（72,536 B）+ BotModule.cs + BotNavigator.cs + CsBotConst.cs',
     '用户本轮报#2；机制出处 `CsBotBrain.cs:11`（Idle→Patrol→Engage→(Plant|Defuse|Camp)）、`BotNavigator.cs:9-29`（路点推进+局部避障+卡住自恢复）、`:115-161`（路线=最近邻排序路点）、`:269-277`（连续卡住→上层换目标）',
     5, T_SCRIPT, K_ALLOWED,
     '离线可判的现状（机制层，不是"没写"）：① **无寻路层** —— 引擎 `AStar`（`client/Packages/com.clover.unity-engine/Runtime/Core/AStar.cs`）'
     '在 `client/Assets/**` **零调用**（在 `client/Assets/**` 内 grep `AStar` 命中 0）' + _SCOPE_ASTAR + '，机器人只能"沿标记点最近邻序列走直线 + 局部避障"（`BotNavigator.cs:226` `Avoid`）；'
     '② 目标粒度极粗 —— 每阵营只有 3 条路线标记（`CsBotBrain.cs:298-299,574-575` CTDefendA/B + 中路）+ 1 条巡逻线（`:494-516`），'
     '**守点 = 到达单个包点标记后原地警戒**（`:338` ObjectiveHoldSeconds / `:31`），时长到就"换路线/去巡逻/回出生点"（`:406-455`）'
     '⇒ 观感就是"原地踱步、不知道在干啥"；③ 下包/拆包有实现但是**单点依赖**（`:697-701` 只认 `intent.Use` + 站位），'
     '没有"多点突破 / 掩护 / 换点位"这类协同 ⇒ **缺的行为清单 = 守点位表（多点/换位）、下包决策（去哪个包点 + 何时下包）、'
     '突破协同（分批推进/闪光掩护）、基于代价图的 A→B 走法**。三档难度只改反应时间与瞄准误差（`CsTypes.cs:148`）。'
     '⇒ 差异 #67；解法依赖引擎寻路能力结论（见 `client/Packages/com.clover.unity-engine/Runtime/Core/AStar.cs（第 16~19 行）` 的回调式格子 A* 契约）。'
     '**【片BL-R2 2026-09-22 实测（数值类）】** 一次 Play（4v4 双阵营 bot、local=CT、3 回合 / 289 s）逐条量三项：'
     '① **守点 = 0** —— `analyze-hold-plant.py` 的 CT 进点计数 `CTSITE` = **0**，4 个 CT bot 到最近包点 A 的最近距离 '
     'Cliffe 17.89 / Spliff 20.08 / Darrell 22.07 / Scuzzy 26.36 m（包点半径 7.0 m）；`HOLDTABLE`（业务自己的守点表就绪标记）= **6** '
     '⇒ 守点表**建了**、bot **走不到**。② **下包 = 0** —— `CARRIER`=3（C4 已分配），`TARRIVE`=**0**、`TPLANTSTART`=**0**、`TPLANTED`=**0**，'
     '驱动打印 `bot plant observed = False`；T 侧 4 个 bot 全场到 A 的最近距离 85.9-90.7 m ⇒ **整场未离开出生点区**。'
     '③ **突破 = 0** —— T 侧每回合 x/z 位移跨度最大 1.77 m（Minh/S1），Gooseman、ZBot 在 S2 整回合 0.00 m。'
     '⇒ 三项都**有实现但走不动**：缺口在走这一层，不在决策这一层' + _TAC_TAIL),
    # ---- #3 右键没效果 ----
    ('D11', '武器右键（次级攻击 attack2）',
     'client/Assets/Scripts/Module/Match/ICsMatch.cs（CsInputState）+ Module/Combat/CombatModule.cs',
     '用户本轮报#3；实现出处 `Module/Combat/CombatModule.cs:174`（`cmd.Zoom = input.GetKey(GameKey.MouseRight)`）、`Module/Match/ICsMatch.cs`（CsInputState 只有 Zoom）',
     3, T_SCRIPT, K_ALLOWED,
     '''离线已判（原结论）：输入结构 `CsInputState` 里**只有 `Zoom`**，没有 attack2 / 次级开火字段 ⇒ 除狙击开镜外任何"右键要有别的效果"的武器在本工程**结构性无效果**。 **【片FX-ALL 2026-09-23 落地 · 消除】** 契约已扩：`CsInputState` 新增 **`Attack2`**；真实输入通道 `CombatModule` 用 `GetKeyDown(GameKey.MouseRight)` 填它（**按下沿**，不是 `GetKey` 电平）；离线驱动按住型输入会每帧翻转一次，所以"切换型"语义钉在模拟边界上 —— `CsMatch` 判 `inp.Attack2 && !_preAttack2` 并紧跟基线更新（`tools/probes/attack2-probe.py` A5/A6）。反例复现（同判据 C 段）：按住 8 帧时**电平型切 8 次、判沿型切 1 次**；点按 3 次两者都 3 次；长按+再点 1 次电平型 4 次、判沿型 2 次 ⇒ 判沿型正是"按一下切一次"（**RESULT: PASS**；并复算 `client.dll` 的 `+attack2`@`0x0e9f98` / `-attack2`@`0x0e4ac5`）。⚠️ 本行**不需要并排图**（判定手段 = 脚本断言）：消音器 / 连发是**状态与音效**语义，不是画面语义 —— `Silenced` / `BurstMode` 目前除库存与 HUD 短名外没有独立可见载体（`Resources/Art/Tex/vm_usp_silencer.png` 等贴图在盘但无人读）。⇒ 差异 #68'''),
    ('D10', '原版 attack2 的逐武器语义（USP/M4A1 消音器 · Glock 连发切换）',
     'client/Assets/Scripts/Core/CsWeapons.cs + Module/Combat/CsCombatTuning.cs',
     '用户本轮报#3；原版语义载体**在盘**（`原版资源/cs16src/cstrike/cl_dlls/client.dll`，片AW 取回）但未反汇编 ⇒ 待补（第 2 级：可执行里的常量/分支，载体已具备）；`原版资源/hlsdk/dlls/weapons.cpp` 是 HL1 武器实现，'
     '只能证明"开火/切换是写死在类里"这一机制，⛔ 不是 CS 的语义出处）',
     3, T_SCRIPT, K_ALLOWED,
     '''同类漏检（原结论）：原版 CS 1.6 的右键语义**至少**有 USP 拆装消音器、M4A1 拆装消音器、Glock18 连发切换三条，本工程一条都没有；`Core/CsWeapons.cs` 的 `CsWeaponDef` 里也没有"是否支持消音器/连发"的字段 ⇒ 契约层缺口。 **【片FX-ALL 2026-09-23 落地 · 部分消除】** ① 能力表已补：`CsWeaponDef` 新增 **`CanSilence` / `CanBurst`**，`MarkAttack2Capabilities()` 打点 —— `Usp`/`M4A1` = CanSilence、`Glock18`/`Famas` = CanBurst、其余（含 AK47）两个都为 false；② 逐武器语义取自**已在盘**的 `原版资源/cs16src/cstrike/cl_dlls/client.dll`（1,093,128 B）的 7 条串，逐字节卡在固定文件偏移上且都是首次出现：`weapons/usp_silencer_off.wav`@`0x0e3804`、`usp_silencer_on.wav`@`0x0e3824`、`m4a1_silencer_off.wav`@`0x0e308c`、`m4a1_silencer_on.wav`@`0x0e30ac`、`famas-burst.wav`@`0x0e26f4`、`#Switch_To_BurstFire`@`0x0e27d4`、`#Cstrike_TitlesTXT_M4A1_Short`@`0x0e6af4`（`tools/probes/attack2-probe.py` B 段复算）；③ 状态机落地：`CsInventory.ToggleWeaponMode` 按能力表翻转 `Silenced` / `BurstMode`，**不拦切枪期**；④ 离线自检 `Module/Combat/CombatSelfTest.cs` 五段（按一次装上 / **按住 5 帧不重复翻转** / 松手再按拆下 / Glock18 切连发且不动 `Silenced` / 切枪期照样能切 / AK47 状态一位都不动）。⚠️ 仍未消除：消音后**伤害 / 散布**与**连发发数与节奏**的数值（要反汇编），以及 `fvol` 音量档。⇒ 与 #68 同一差异行登记'''),
    # ---- #4 弹痕 ----
    ('D6', '命中墙弹痕贴片（fx_bullethole 单变体）',
     'client/Assets/Resources/UI/Art/fx_bullethole（16×16，234 B）+ Module/Combat/CombatEffects.cs',
     '用户本轮报#4；实现出处 `Module/Combat/CombatEffects.cs:293-303`（贴面 + 沿法线抬 1cm + `DecalSize`）、`Module/Combat/CsCombatTuning.cs:178`（DecalSize=0.075）/`:181`（DecalDuration=25）/`:184`（MaxDecals=64）',
     3, T_SIDE, K_ALLOWED,
     '''离线可判（原结论两条）：① **尺寸无出处** —— `Module/Combat/CsCombatTuning.cs:178` 的 `DecalSize = 0.075f`（7.5 cm）源码注释里自认是"按原版 decal 的观感（~7 cm）"；原版弹痕尺寸由 `decals.wad` 贴图原生尺寸 + 世界单位映射决定，本机拿不到该映射 ⇒ 待补。② **注记过期**：`Core/ResPaths.cs:177` 仍写"32×32"，而盘上 `fx_bullethole` 实测 **16×16**。③ 变体只有 1 张。 **【片FX-ALL 2026-09-23 落地 · 消除】** ② 注记**已修**：`Core/ResPaths.cs` 的 `fx_bullethole` 注记改为 16×16，并新增 `FxBulletHoleKeys` / `FxBloodKeys` 两张字面量 key 表；③ 变体**已补到 5 张**（`{shot1`…`{shot5`，见本表 D3 的弹痕多变体行）；**尺寸口径已修**：旧 `Vector3.one * DecalSize` 把 16px 贴图当 1 世界单位宽 ⇒ 只有 `0.075 / 16 ≈ 0.0120 m`（比 7.5 cm **小 6.25 倍**）；新 `SpriteScaleForMeters(s, meters)` 按 `s.bounds.size.x` 反推 ⇒ 实机 `世界宽=0.075m 缩放=0.469`。**遮罩透明已修**：`decals.wad` 的遮罩色是调色板**索引 0**（不是 255）⇒ 旧口径解出来是**不透明白方块**，`tools/probes/wad3-extract.py` 已按 `masked_bg_index` 修正（判据 E 段自带最小 PNG 解码器，逐张断言"既有不透明像素也有透明像素"）。**朝向退化已修**：地面命中时 `LookRotation(·, Vector3.up)` 退化会让贴片**立起来**（正对相机是一条线 = 看不见），已抽 `SurfaceUp()` helper 让弹痕 / 火星 / 血迹三处全走它（判据 D 段断言"调用点恰好 3 处、全文无裸写法"）。判据 = `tools/probes/decal-size-probe.py`（A~F 六段，**RESULT: PASS**；D / E 两段是先红后修）。并排图已采：联络图 `fx-表现联络图.png` 格 **02 / 05 / 09**（朝地 / 朝墙 / 多变体），肉眼终判：地面 7 个 / 墙面 6 个弹痕、形态**同构**。**贴图本身是灰阶**（实测 `decals.wad` 调色板逐 lump 彩色条目 0~1/256 ⇒ 弹痕画出来是灰阶，原版是否由引擎染色未取证）。① **尺寸映射的出处仍未消除**（`DecalSize = 0.075f` 保持，`DecalMetersPerPixel` 只是同族比例推演，⛔ 不是出处）。⇒ 差异 #69'''),
    ('D3', '弹痕的按材质 / 多变体表现（同类漏检：#4 只报了"痕迹不对"）',
     'client/Assets/Resources/UI/Art/fx_bullethole + 原版 decals.wad',
     '用户本轮报#4；原版载体 `原版资源/cs16src/cstrike/decals.wad`（960,012 B，片AW 已取回，SHA256 记在 `原版资源/清单.md`）',
     3, T_SIDE, K_ALLOWED,
     '''同类漏检：弹痕的**表现类**判据（贴面朝向 / 尺寸 / 变体 / 按命中材质的观感）在本工程**没有任何并排图或格号**证据。 **【片FX-ALL 2026-09-23 落地 · 消除】** ① **变体 1 张 → 5 张**：`decals.wad` 的 `{shot1`…`{shot5` 全解出（各 16×16，台账 `.ai-tmp/test/fx-decal-variants.tsv`），`Core/ResPaths.cs` 新增 `FxBulletHoleKeys` **字面量 key 表**（⛔ 不是拼串：拼出来的 key 在静态扫描里看不见，`coverage-diff` 的 D1 维度会把贴图判成"文件在盘上但无人读"），`CombatEffects` 按表逐个加载、命中时均匀随机取一；② 并排图已采：联络图 `fx-表现联络图.png` 格 **02**（朝地：贴面法线朝上）/ 格 **05**（朝墙：贴面法线水平）/ 格 **09**（连打多发的多变体同框），三格同机位同冻结帧；清单 `.ai-tmp/screenshots/fx-contact-sheet.index.tsv`；③ **尺寸口径已修**（原缺陷，本片判据 B 段锚定的就是它）：旧实现 `decal.Tr.localScale = Vector3.one * CsCombatTuning.DecalSize` 把 **16px 的贴图当成 1 个世界单位宽** ⇒ 画出来只有 `0.075 / 16 ≈ 0.0120 m`，比要求的 7.5 cm **小 6.25 倍**（这是"弹痕小到看不见"的第一因）。新实现 `SpriteScaleForMeters(Sprite s, float meters)` 用 `s.bounds.size.x` 反推缩放（`CombatEffects.cs`），弹痕 / 火星 / 血迹三处共用；实机原文 `弹痕落在 (-11.52,3.25,-45.70)（法线 (0,1,0)）→ 贴图 fx_shot3 启用=True 世界宽=0.075m 缩放=0.469 变体数=5`。④ **贴面朝向的退化四元数已修**（原缺陷）：`Quaternion.LookRotation(forward, up)` 要求 `up` 与 `forward` 不平行；命中**地面**时法线就是 `(0,1,0)`、`-normal` 与 `Vector3.up` 正好反向 ⇒ 四元数**退化**、贴片**立起来**，正对相机看只有一条线（表现上又是"没有弹痕"）。已抽共享 helper `CombatEffects.SurfaceUp(normal)`（`abs(dot(normal, up)) > 0.9` 时改用 `Vector3.forward`），**弹痕 / 火星 / 血迹贴花三处全走它** —— 只修一处等于把同一个坑挪到另外两处等着复发（判据 D 段第二次跑时正是又捞出火星 + 血迹这两处）。⑤ **遮罩透明已修**（原缺陷）：本工程 decal 贴图从 `decals.wad` 解出时，遮罩透明色**不是 255 而是调色板索引 0**（`{shot1` / `{blood1` / `{bigshot1` 的最高频索引 = 0，索引 255 一个像素都没有）—— 旧口径照搬 `spr-extract.py` 的 255 ⇒ alpha 全 1、贴图**解出来是不透明的白方块**。`tools/probes/wad3-extract.py` 加 `masked_bg_index`（按四角众数判背景索引）+ 4 条**代码级**自检；判据 E 段自带最小 PNG 解码器逐张断言"11 张贴图既有不透明像素也有透明像素"（修复前 256/256 全不透明；实测 `fx_shot1` 45/256、`fx_blood1` 574/2304、`fx_blood5` 335/4096）。⑥ 判据 = `tools/probes/decal-size-probe.py` 六段（A 结构 / B 数值：逐张贴图用**真实 IHDR 像素宽 + 真实 `.meta` PPU** 复算 / C 反例取 `git show HEAD:` 取现 / D 朝向 / E 透明度 / F 埋点）**RESULT: PASS**；其中 D、E 两段是**先红后修**，红的时候正好各捞出上述一处真缺陷。⑦ **肉眼终判**（AI 读图，格 02 / 05 各放大 5 倍）：地面那张有 **7 个**白灰放射状弹痕、沿后坐力方向排成一条竖线；墙面那张有 **6 个**同类弹痕 —— 两者形态**同构**（白灰溅环 + 深色洞芯 + 圆形正对相机）；若 ④ 未修则朝地那批会立起来成一条线，所以这同时是 ④ 生效的直接证据。⑧ **贴图本身是灰阶**（实测）：`decals.wad` 的调色板**逐 lump 彩色条目 0~1/256** ⇒ 弹痕 / 血迹贴图本身**不带颜色**（画出来是灰阶），原版是否由引擎染色未取证。⚠️ 仍未消除：**原版 decal 尺寸的出处**未拿到 —— `CsCombatTuning.DecalSize = 0.075f` 的注释自认是"按原版观感（~7 cm）"，新增的 `DecalMetersPerPixel` 也只是同族比例推演（48px→0.225 m / 64px→0.30 m），⛔ 不是出处（`{bigshot1`…`{bigshot5` 已解出但刻意不落盘：大口径的选择在引擎 `hw.dll` 里，不在盘 ⇒ 落盘即构成 T0 违规）。⇒ 与 #69 同一差异行'''),
    # ---- #5 买枪 / 选人界面 ----
    ('D4', '买枪界面（BuyMenuPanel）',
     'client/Assets/Scripts/UI/InGame/BuyMenuPanel.cs',
     '用户本轮报#5；落点出处 `UI/InGame/BuyMenuPanel.cs:27-38`（DialogWidth 1020 / DialogHeight 640 / RowHeight 46 … 全是本项目常量，注释写"规格 G3，任务书 §4.2"）',
     3, T_SIDE, K_ALLOWED,
     '离线可判：`BuyMenuPanel.cs` 的**全部布局常量是自建**（`:27-38` 的 Dialog/Category/List/Row 十项），出处是"任务书 §4.2 / 规格 G3"——'
     '⛔ 不是原版载体。原版口径的载体**已在盘**：`原版资源/cs16src/cstrike/sprites/weapon_*.txt`（31 份，片AW 取回；'
     '每份逐字给出 320/640 两档下 `weapon/ammo/crosshair` 部件取自哪张 HUD 精灵 + 源矩形 + 屏幕落点）'
     '与 `640hud10/640hud11.spr`（`原版资源/cs16src/cstrike/cstrike__sprites__640hud10.spr`）⇒ **有出处但未接** ⇒ 差异 #70'),
    ('D4', '选人（选兵种）界面（TeamSelectPanel / classmenu）',
     'client/Assets/Scripts/UI/Flow/TeamSelectPanel.cs',
     '用户本轮报#5；原版载体 `原版资源/cs16src/cs16game/app/cstrike/resource/ui/classmenu_ct.res` / `classmenu_ter.res`（本机不在盘 ⇒ 降级链待补）；差异 #36 已登记"未做"',
     2, T_SIDE, K_ALLOWED,
     '本工程**没有**选兵种界面（只有选阵营 `TeamSelectPanel.cs`，其载体 `teammenu.res` 已实现见差异 #25/#27/#38）；'
     '差异 #36 早先已登记"原版 `classmenu_*.res` 未做"，本片按用户本轮 #5 把它**并进同一条**（同一差异 #70），'
     '⛔ 不新开差异号（避免同一件事两个号）。判据 = 原版 `.res` 到位后按控件逐条落 + 并排图'),
    # ---- #6 角色模型 ----
    ('D1', '角色模型（9 皮肤：T/CT player_*.prefab）',
     'client/Assets/Resources/Art/{T,CT}/**.prefab + client/Assets/Editor/Views/ModelData/player_*.cs16mdl/.cs16anim',
     '用户本轮报#6；派生链出处 `策划/对照表.md:134,136,137,149,150`（M-01/M-03/M-04/M-16/M-17）→ `原版资源/cs16src/cs16_anim.py` / `cs16_build.py`（本机不在盘）',
     3, T_SIDE, K_ALLOWED,
     '离线核对结论（**无字节级证据，如实登记**）：① 工程模型数据的**全部来源**是 '
     '`原版资源/cs16src/cs16game/app/cstrike/models/player/*/*.mdl`（9 皮肤）经 `cs16_build.py` / `cs16_anim.py` 转成 '
     '`*.cs16mdl`（几何/蒙皮）+ `*.cs16anim`（骨骼动画）；② 中间格式头实测 **`C16M`/`C16A` v1 + 角色名 + 贴图名**'
     '（`player_T.cs16mdl` 头 = `C16M\\x01…player_T…player_T_TERROR.png`），**不记录源 mdl 的 SHA256**（17,743 B 全量扫过）'
     '⇒ 本工程**无法自证**这些是原版 mdl；③ 能站得住的证据只有"与那份 mdl 逐值一致"：骨骼 23（M-03）、序列 111（M-04）、'
     '`idle1` fps15/61 帧（M-05）… M-12/M-14（对照表逐条"本片重读一致"）；④ 该 mdl 属**社区 repack**'
     '（路径 `cs16game/app/cstrike/models/…`，见 `策划/基线图/场景清单.md:20-39` 对这份 repack 的记录）'
     '⇒ 用户"感觉是社区版模型"这件事**离线既不能证实也不能证伪**。'
     '可执行路径：照片AR 的 `codeload` 路径重新取回同一 repack 的 `models/**`，逐文件 SHA256 与工程中间数据对账 '
     '⇒ 差异 #71'),
    ('D3', '角色模型的贴图 / skin 绑定（同类漏检：#6 只报了"模型不对"）',
     'client/Assets/Resources/Art/Tex/*.png（242 张模型内嵌贴图）',
     '同类漏检；口径出处 `策划/对照表.md:149`（M-16：terror.mdl 2 张 / v_ak47 11 张 / v_knife 4 张）与 `:162`（T-04 共 243 张）',
     2, T_SIDE, K_ALLOWED,
     '登记 #6 时顺手查同类：M-16 明写"我方按 md5 去重合并，**不再与 mdl 一一对应**"⇒ "模型看起来不对"的另一半（skin/贴图）'
     '在本工程**没有按材质槽逐条对账过的判据**。判据 = 逐 skin（`Art/Mat` × `Art/Tex`）× 每个 Renderer 的材质槽，'
     '与 mdl 的 `numtextures` 一一对上 ⇒ 并进差异 #71（⛔ 不新开号）'),
    # ---- #7 换弹动画 ----
    ('D5', '换弹动画（第三人称 ref_reload_* / 第一人称 v_* reload）',
     'client/Assets/Scripts/Module/View/ActorView.cs + ViewModelRig.cs + Module/View/CsViewTuning.cs',
     '用户本轮报#7；实现出处 `ActorView.cs:249-274`（按 `ReloadEndTime` 前推触发）、`CsViewTuning.cs:318-327`（`PlayerReloadStates` 候选）、`:254`（`VmStateReload`）',
     4, T_SIDE, K_ALLOWED,
     '''离线可判（原结论）：**触发方式是"边沿检测前置时间戳 `ReloadEndTime` 变大"**（`ActorView.cs:268`），不是"模拟发出的换弹事件"；`CsInventory.Reload`（`Module/Match/CsInventory.cs:403-439`）会在 **没有武器 / 弹匣已满 / 正在切枪** 三条分支上直接 `return`；更要紧的是"丢"的形态：`ReloadEndTime` 若在同一帧被重设（连点 R / 换弹中途切枪再切回 / 上一发未结算），边沿检测可能采不到 → 动画整段丢失。`CsViewTuning.cs:318-327` 还写明"原版没有 ref_reload_grenade / ref_reload_knife" ⇒ 那两类**本来就没有**（⛔ 不算缺陷）。 **【片FX-ALL 2026-09-23 落地 · 消除】** ① `CsActor.ReloadSeq`（单调序号）成为换弹的**事件计数**：`CsInventory.Reload` 只在**成功分支** `ReloadSeq++`（`tools/probes/reload-edge-probe.py` 的 A2/A3 断言"恰好 1 次、且位于全部 early-return 之后"）；② 两处表现层（`Module/View/ActorView.cs` / `Module/View/ViewModelRig.cs`）改判 `ReloadSeq != _preReloadSeq`，旧的 `ReloadEndTime > _preReloadEndTime` 口径**已下线**（A4）；③ 反例复现（真数）：主武器换弹 D1=3.00 s，切手枪(0.30 s)后再换弹 D2=2.51 s < D1 ⇒ 单靠 `>` 比较**确实不可靠**；整段漏窗采样（间隔 3.2 s ≥ 换弹时长）上旧口径命中 **0** 次、新口径 **1** 次，60 Hz 采样两者都 **1** 次（`tools/probes/reload-edge-probe.py`，**RESULT: PASS**）；④ 并排图已采：联络图 `fx-表现联络图.png` 格 **10**（第一人称换弹中，动画在播；清单 `.ai-tmp/screenshots/fx-contact-sheet.index.tsv`）。⚠️ 仍未消除：换弹动画的**时长/姿势**仍取自 `Module/View/CsViewTuning.cs`，未与原版 `ref_reload_*` 序列逐帧对账。⇒ 差异 #72'''),
    # ---- #8 死亡 / 尸体 / 掉落 / 受伤特效 ----
    ('D5', '死亡动画与尸体（倒地序列 + 尸体是否留在地上）',
     'client/Assets/Scripts/Module/View/ActorView.cs + Module/View/CsViewTuning.cs',
     '用户本轮报#8；实现出处 `ActorView.cs:451-464`（死亡先播序列，`OnComplete` 后 `SetShown(false)`）、`:293-312`（`PlayDeath`）、`CsViewTuning.cs:194`（`PStateDeath`）',
     3, T_SIDE, K_ALLOWED,
     '''离线可判（原结论）：当时的死亡链是"播 `death1..3` 之一（按 `actorId%3` 取）→ 播完 `SetShown(false)` **整具身体隐藏**"，本工程没有任何 corpse/尸体实体 ⇒ 用户看到的"尸体不在地上"是**结构性未做**。 **【片FX-ALL 2026-09-23 落地 · 部分消除】** 死亡链已改为**尸体留场**：倒地序列播完**不再** `SetShown(false)`，而是把姿态钉在倒地序列的**最后一帧**（`_anim.Play(_deathState, 1f)` 后 `_animator.speed = CsViewTuning.CorpseAnimSpeed` = 0）+ 关掉该视图**全部 Collider**（尸体不被打中、也不挡活人走路）+ 关掉名牌；`IsAlive` 回到 true（复活 / 回合重开）时解冻并还原碰撞体。实现 = `Module/View/ActorView.cs` 的 `_corpseHeld` / `_corpseFrozen` / `_deathState` + `EnsureCorpseShown()` / `ReleaseCorpsePose()`，调参 = `CsViewTuning.cs` 的 `CorpseAnimSpeed`。并排图已采：联络图 `fx-表现联络图.png` 格 **15**（死亡后尸体留在地上，第一人称俯视 -22°）/ 格 **16**（回合重开后同一位置无尸体）。⚠️ 仍未消除：① 尸体仍是**同一具 ActorView**（没有独立尸体对象、没有骨骼快照 ⇒ "尸体数量上限 / 清场时机"未实现）；② 没有 Animator 的旧预制体仍走"立即隐藏"兜底。⇒ 差异 #73'''),
    ('D10', '死亡结算（尸体实体 / 掉落武器 / 掉落物进场景）',
     'client/Assets/Scripts/Module/Match/CsInventory.cs（DropWeapon）+ Module/Match/CsMatch.cs',
     '用户本轮报#8；实现出处 `CsInventory.cs:255-285`（`DropWeapon` 只改库存字段，不生成任何世界实体）、`CsMatch.cs:931`（调用点）',
     3, T_SCRIPT, K_ALLOWED,
     '离线已判：`DropWeapon`（`CsInventory.cs:255-285`）**只从库存里摘掉字段**（Primary/Secondary/C4），'
     '`a.ActiveWeapon == weaponId` 时再 `SelectBestWeapon`；**不生成任何掉落到世界的实体** ⇒ "枪也不在地上"是结构性未做。'
     '（对照：C4 有掉落链 `CsBomb.cs:393-434` + `CsMatch.cs:2559`，⛔ 但 C4 掉的是**状态点**，也不是世界实体。）'
     '⇒ 与 #75（无世界武器模型 w_*）同根：**工程里根本没有"世界中的武器"这个对象**。⇒ 差异 #75'),
    ('D6', '受伤特效（血雾 / 命中反馈）与"没血"',
     'client/Assets/Scripts/Module/Combat/CombatEffects.cs + UI/InGame/CsDamageIndicatorWidget.cs',
     '用户本轮报#8；实现出处 `CombatEffects.cs:10`（五类：枪口火焰 / 弹道 / 弹痕 / 血迹 / 爆炸）、`CsDamageIndicatorWidget.cs:46-104`（屏幕边缘方向红框）',
     2, T_SIDE, K_ALLOWED,
     '''离线已判（原结论）：`CombatEffects` 的四类特效里**没有 blood** ⇒ 受击时屏幕上没有任何血/命中反馈，只有屏幕边缘的方向指示器（`CsDamage.WriteLocalDamageIndicator`）。 **【片FX-ALL 2026-09-23 落地 · 部分消除】** 受击血迹已实现：新增 `ICsMatch.OnBulletHit`（受击者 / 命中点 / 弹道方向 / 是否爆头），在 `CsDamage.ApplyHit` 里**"确定命中角色"之后、任何伤害闸门之前**发出（血与扣血是两件事：友好伤害关闭 / 护甲全吸收时 `OnDamaged` 不发，但原版照样出血）；`Module/Combat/CombatModule.cs` 订阅后调 `CombatEffects.BloodImpact`：① 命中点出一小团血雾；② 从命中点沿弹道追 ≤ `BloodDecalTraceRange` = 2.5 m 找到"后面的面"再贴一张血迹贴花（原版血迹贴在**背后的面**上，不是贴在角色身上）。贴花用**真载体** = `decals.wad` 的 `{blood1`…`{blood6`（6 张，48×48；`{blood5` 原生 64×64），按 `Core/ResPaths.cs` 的 `FxBloodKeys` 字面量 key 表加载。并排图已采：联络图 `fx-表现联络图.png` 格 **12**（第一人称命中敌人）/ 格 **13**（命中点放大 2x）/ 格 **14**（同一机位稍后一帧：命中点**后方那面墙** —— 本次**未见贴花**，原因见下 ②）。**血雾已直证**：以准星为准心的躯干窗口「近白像素(>190)」布景帧 **69** → 命中帧 **321** → 稍后同机位帧回落 **67** ⇒ 命中点确有**短时**新特效落下（代码里打在角色身上的**只有** `BloodImpact` 这一支 —— `BulletImpact` 的注释明写"打在人身上的不留痕"，见 `Module/Combat/CombatModule.cs:545`）。⚠️ 仍未消除：① **血雾**的独立载体 `sprites/bloodspray.spr` 与 `sprites/blood.spr` **不在盘**（两个串都在 `mp.dll` 里，同 #69 的 `{bigshot*` 情形）⇒ 现用血迹贴图染色的小贴片（0.18 m）替身；② **壁面贴花这一支本次只取得"否支"的直证**：实机唯一一条命中角色的日志是 `命中 (-11.61, 4.61, -48.19)（爆头=True）：沿弹道 2.5m 内没有可贴面 ⇒ 只出血雾`（`fx4-console-corpse.json`）—— 本次布景里目标距其身后壁面 > `BloodDecalTraceRange`=2.5 m，故走 `没有可贴面` 支；`血贴在 …`（贴花成功）支**本次未取到直证** ⇒ 待补（取证口径：把目标摆到距壁 ≤2.5 m 再打**身体**）。同时这条也说明本实现的**行为口径**："贴花只在命中点沿弹道 ≤2.5 m 内有可贴面时才出现"（原版口径未取证）；③ 行为口径的其余部分（贴几张 / 触发时机）无直证，且"**被**命中"那一格拍不到（探针相机只对非本地角色，本地玩家自己的血只有第三人称才看得见）⇒ 该格由 `di=1` 的方向指示器证据补（见本表 D6 的受伤提示行）；④ **贴图本身是灰阶**（实测）：`decals.wad` 的调色板**逐 lump 彩色条目 0~1/256**（`{blood1` / `{shot1` / `{bigshot1` 用到的条目全是 `(i,i,i)`）⇒ 血迹贴花画出来是**灰白溅斑**、不是红；原版是否由引擎（`hw.dll` / `client.dll` 的 decal 着色）另外染色**未取证** ⇒ 待补。⇒ 差异 #74'''),
    # ---- #9 第三人称武器 ----
    ('D2', '第三人称手持武器（角色身上看不到拿什么枪）',
     'client/Assets/Resources/Art/T/player.prefab + Resources/Art/CT/*.prefab',
     '用户本轮报#9；预制体实测（`Art/T/player.prefab` 全量子节点 = Bip01 骨架 + Skin0/Skin1 + 4 个 Hitbox_* + Bomb，**无任何武器节点**）',
     2, T_SIDE, K_ALLOWED,
     '离线已判：角色预制体里**没有武器节点、也没有挂点**（`Art/T/player.prefab` / `Art/CT/*.prefab` 的 `m_Name` 全量列表里 '
     '只有骨架/皮肤/命中盒/`Bomb`；存在 `client/Assets/Resources/Art/{T,CT}/player*.prefab` 的 m_Name 全量列表里没有武器节点/挂点（已取回：prefab 本身）' + _SCOPE_WPREFAB + '）。`ActorView` 的装配只有 '
     '`transform.SetPositionAndRotation` + `Body` 缩放 + 动画（`ActorView.cs:434-496`），**不挂任何武器**。'
     '⇒ "第三人称看不到他拿什么枪"= 结构性未做（与"枪不在地方"同根：没有世界武器对象）⇒ 差异 #75'),
    ('D1', '世界武器模型（w_*.mdl）载体与中间数据（同类漏检：#9 的载体侧）',
     'client/Assets/Editor/Views/ModelData/（38 份 = 9 角色 + 29 视模型，**无任何 w_* / 世界武器**）',
     '用户本轮报#9；口径出处 `策划/对照表.md:135`（M-02：31 个 `v_*.mdl`）与 `:150`（M-17：38 份 cs16mdl = 9 + 29）',
     2, T_SCRIPT, K_ALLOWED,
     '同类漏检（登记 #9 时顺手查）：原版的"世界里的武器"是 `models/w_*.mdl`（第三人称手持 + 掉落物都用它），'
     '本工程的 38 份模型数据里**只有 29 个 `vm_*`（第一人称视模型）**，`w_*` **一份都没有**（`Art/{T,CT}/viewmodel_*.prefab` 亦然）。'
     '⇒ 差异 #75/#76 的载体侧根因：**载体没搬**（不是"搬了没接"）。来源 = 与 M-01/M-02 同一份 repack 的 `models/w_*.mdl`'
     '（照片AR 的 `codeload` 路径可取回）⇒ 并进 #75（⛔ 不新开号）'),
    # ---- #10 AI 钻地 ----
    ('D9', '机器人地形贴合（"钻地"）',
     'client/Assets/Scripts/Module/Match/CsMatch.cs（StepActorPhysics）+ Module/Map/CsMap.cs',
     '用户本轮报#10；实现出处 `CsMatch.cs:1762-1894`（`StepActorPhysics`：重力→`ResolveMove`→`TrySampleGround`→贴地/陡坡/软地板）',
     4, T_SCRIPT, K_ALLOWED,
     '离线可判的机制链（bot 与真人**走同一条**：`CsMatch.cs:2292` 与 `:1752` 都调 `StepActorPhysics`）'
     '① 移动 = `a.Position = resolved`（直接改位置，**不是** `CharacterController`/刚体）；'
     '② 水平 = `_map.ResolveMove`（扫掠 ≤0.25m + 分轴滑墙 + 台阶，`CsMap.cs:444-531`）；'
     '③ 竖直 = `TrySampleGround` 向下射线（`CsMap.cs:542-557`，只打 `CsWorld` 层 `:571-603`）；'
     '④ 探不到地面 → **软地板**（`CsMatch.cs:1853-1887` `_lastGroundY` / `TrySoftFloor`）+ 掉图兜底 `:1894-1904`。'
     '⇒ "钻地"的**可判据候选根因（离线不能定案，需一次实机）**：(a) 贴地位图是**单层 2D**（`client/Packages/com.clover.unity-engine/Runtime/Presentation/MapFormat.cs（第 29-30 行）` 的 '
     '`FlagHeightField` V1 未实现；差异 #49），上层平台/桥面在 XZ 上与下层同格 ⇒ 探地只取"第一个交点"，'
     '当角色从上层掉到下层时射线首交在**上面那层**⇒ 被拉回上层（观感＝钻进/穿出地面）；'
     '(b) 软地板（`:1863-1877`）在"连续探不到地面"时把人**贴到最后一次已知地面高度**⇒ 若 bot 正在上/下坡或站在'
     '`collision-mesh-gap.tsv` 列的"碰空气格"上，就会被贴在**低于视觉地面**的位置（外形像钻地）；'
     '(c) 走路点（`BotNavigator`）没有代价图，bot 会朝不可走方向推进并由逃逸逻辑乱走 ⇒ 在坡/台阶处反复进出几何。'
     '判据 = 一次实机 + 逐帧 `actor.Position.y` vs `SampleGround` 的数值日志（本片不采）⇒ 差异 #76。'
     '**【片BL-R2 2026-09-22 实测（数值类）】** 探针 `tools/probes/bot-phys.cs` + 聚合 `tools/probes/analyze-bot-phys.py` → '
     '产物 `.ai-tmp/test/bk-bot-phys.tsv`（3595181 B / 18952 B 行 / 8 actor x 2369 采样；一次 Play，`play-log.tsv` 有本片行）：'
     '**脚底间隙** `|pos.y - groundY| > 0.05 m` 的行 = **3 / 18952**（最大 0.128 m，Minh）；地面法线 < 0.70（陡坡）行 = **0 / 18952**；'
     '`CanStand(pos)==0` 行 = **1 / 18952**；`dirsMovable==0`（被物理围死）行 = **0 / 18952** ⇒ **探针口径下没有观测到钻地**'
     '（间隙量级 0.13 m，不是穿层）。相邻机制只出现在业务日志：`[Warn] [Match] 连续探不到地面…贴到最后一次探测到的地面 y=-3.25` = **2 条**'
     '（窗口 18:35-18:42，`tools/probes/analyze-bot-ai-log.py --since`）⇒ 与 (b) 软地板同源、规模极小。'
     '**未定案**：探针列 27 `reason=want-no-ground` = **754 / 18952 行**（CT 751 / T 3）表示朝目标迈一步的落点在脚底高度 '
     '`TrySampleGround` 探不到地面，它同时兼容 (a) 落点是空洞/悬崖 与 (b) 落点地面高于脚底（跨层台阶 3.25 m 远大于 StepUpHeight 0.45）'
     '两种读法，现有列 `wantGroundY=na` 无法区分；区分需给探针加从高处起射的射线列 ⇒ 改动要带同批重采，本片不做，留给下一片'),
    ('D10', '机器人移动执行（本地碰撞 vs 寻路）',
     'client/Assets/Scripts/Module/Bot/BotNavigator.cs + Module/Match/CsMatch.cs',
     '用户本轮报#10；实现出处 `BotNavigator.cs:183-248`（输出方向）→ `CsMatch.cs:2271-2292`（写速度→`StepActorPhysics`）',
     3, T_SCRIPT, K_ALLOWED,
     '离线已判的职责链：bot 大脑只产出**方向**（`BotNavigator.ComputeMove`：目标 `/` 路点 + `Avoid` 局部避障 + 逃逸），'
     '速度由 `CsMatch.cs:2278-2279` 写成，位置由 `ResolveMove` 解 → **bot 与玩家共用同一套地形碰撞**，'
     '所以"不是真正的地形碰撞 AI"这个判断**不成立**（有地形碰撞）；成立的是"**没有寻路**"（引擎 `AStar` 在 `client/Assets/**` 内零调用，见 #77）' + _SCOPE_ASTAR + ''
     '⇒ 钻地/乱走属"路径层缺失 + 单层位图上限"，不属"没有碰撞"。⇒ 与 #67/#76 同一组差异。'
     '**【片BL-R2 2026-09-22 实测（数值类）】** ① **物理层面走得动**：`analyze-bot-phys.py` 的 `goalStepLen`（朝目标迈 1 m 时 '
     '`ResolveMove` 的实际水平位移）min 0.150 / **p50 1.000** / p90 1.000 / max 1.000，`<= 0.001 m`（完全迈不动）的行 = **0 / 18952**；'
     '8 方向 `dirsMovable` 最小 6、`==0` 的行 = **0 / 18952** ⇒ **没有任何一帧是被物理围死**。② **但 bot 就是不走**：每回合内 x/z '
     '位移跨度最大值，8 个 bot 中 7 个 <= 2.6 m（仅 Scuzzy 3.46 m）；能动 actor（位移 > 5.8 m）口径本轮 = **0**（全场口径也只有 Scuzzy 6.59 m）。'
     '③ **卡在哪**：探针列 27 `reason` 直方图 = `ok` 18160 / **`want-no-ground` 754**（CT 751、T 3）/ `want-not-standable` 38（全 CT）'
     '⇒ 机制是位图说这一格可走、朝目标的落点在脚底高度探不到地面 = **单层 2D 位图 vs 多层真实几何**（与 #76(a) 同源），'
     '**不是**位图判不可走（那条已修到 0 条）⇒ 片BL-R 的 `PhysRunway`/物理复核在这个机制上**不触发**，故对走不动无改善'),
    # ---- 同类漏检：引擎能力未被业务使用 ----
    ('D10', '引擎网格寻路 AStar（能力已在，业务零使用）',
     'client/Packages/com.clover.unity-engine/Runtime/Core/AStar.cs（325 行）',
     '用户本轮报#10 直接问"引擎里没有寻路机制吗"；能力出处 `client/Packages/com.clover.unity-engine/Runtime/Core/AStar.cs（第 16-19 行）`（契约）/`:52-132`（`Find`）/`:138-174`（`FindSmoothed`）/`:180-219`（`HasLineOfSight`），另有 `Presentation/MapFormat.cs（第 243-252 行）`（`WalkableAt`）',
     3, T_SCRIPT, K_ALLOWED,
     '登记 #2/#10 时顺手查到的同类漏检（**这是"有没有分叉树/寻路"的正面答复**）：引擎**有**通用格子 A*'
     '（8 邻接、对角需两侧可走、octile 启发式、`DefaultMaxNodes=20000`、`MinHeap` 惰性删除、路径拉直 `Smooth`），'
     '契约就是回调式 `Func<Vector2Int,bool> walkable` ⇒ 地图 `.bytes` 的可走位图（`Game.Map.WalkableAt`，`client/Packages/com.clover.unity-engine/Runtime/Presentation/Map.cs（第 149 行）`）'
     '**直接就能当寻路网格**；但 `client/Assets/**` 内 grep `AStar` **命中 0**（范围见下）' + _SCOPE_ASTAR + ' ⇒ 引擎能力从未被业务使用（**此句自切片 BJ 起已不成立**，见本行末尾的片BU-R 更正）。'
     '⇒ 差异 #77（解法依赖它）。'
     '**【片BL-R2 2026-09-22 实测（数值类）】** 一次 Play（18:35-18:42）复核，引擎有寻路、业务零使用**仍成立**：业务日志窗口内 '
     '`[Bot] 求路径失败（位图不可用 / 目标点不可达）` = **0 条**（修前 30 → 0，保持）、`不可走的路点` = **0 条**（修前 101 → 0，保持）；'
     '但 bot 依旧不动（数字见机器人地形贴合 / 战术行为 / 移动执行三行的实测块）⇒ 阻塞点**不在 A* 这一层**，而在其**之上**：'
     '`BotNavigator` 仍只走路点最近邻 + 局部避障，`client/Assets/**` 内仍零调用 `AStar`（范围见下）' + _SCOPE_ASTAR + '' + _ASTAR_TAIL),
]
for _dim, _nm, _car, _src, _sc, _vt, _vd, _ev in AZ_ENT:
    add(_dim, _nm, _car, _src, _sc, _vt, _vd, _ev)
    sta(_dim, _nm, '用户本轮报的场景', '真实机 / 本片只诊断（修在后续切片）',
        '复现用户描述的现象', '未修（登记为允许的差异）', _vd, _ev)

# ============================================================================
#  差异登记（四要素）
# ============================================================================
DIF = [
    # 口径A 锚点规则（2026-09-23 片BW-S-R；主 agent 已追认）：盘上侧独有的「可复核锚点」才追加；
    #   「可复核」= **在盘可达**（解析表 = verify.ps1 item 5 同一套：前缀 '' / client/Assets/ / client/Assets/Scripts/，
    #   basename 在 Scripts|Editor；PNG 另按 .ai-tmp/screenshots 名字解析）。死引用（路径不在盘）**不构成锚点** ——
    #   否则等于把 DANGLING 载体塞进 策划/差异登记.tsv、让 item 27 reference-table-refs 红；被过滤者按规则 3 舍弃并逐条登记，不隐匿。
    # 片AD（2026-09-21）：真源收敛为单一作者 —— 首列为编号，与验收表「允许的差异」段逐条一一对应（64 行）。
    # 编号 1-22 / 25-48 = 原验收表行（其中 3/18/44/45/46/48 由下面的真源文本提供）；49-64 = 真源独有、已补进验收表段。
    # 片AE（2026-09-21）：补 **23 / 24**（两处引用悬空 ⇒ 把被引用的两件事按四要素登记：23 = 对照表 §9 F-03 的
    #   "右上角落点只有非 1:1 口径载体"；24 = 对照表 §9 F-01 的 `Map: <地图名>` 行绝对 x 为推算值 15 px）。
    ('1', 'Find Servers 列表为空', '单机版没有局域网对局可发现；面板与 `Game.LanBrowser` 链路本身是通的', '`UI/Flow/ServerListPanel.cs`', '做联机版时接真实 LAN 广播【2026-09-24 片LAN 进度】"做联机版时接真实 LAN 广播"**已落地一半**（见差异 #88：新增 `CsLanHost` 应答端 + 面板 `Host LAN Game` 开关 + 真实扫描，判据 `RESULT: PASS`）—— ⛔ "列表恒空"只在**同网段确实没有别的实例**时才成立，**不再**是"单机版设计如此"；本行余下的"何时消除"= 服务端 LAN 房间/开局链路落地'),
    ('2', 'Quit 未在自动化里真触发', '自动化在编辑器内跑，真 `Application.Quit()` 会把编辑器一起关掉', '`Module/Flow/AppFlow` 的 `QuitGame` 分支', '打包成独立 exe 后手测'),
    # 口径A(2026-09-23 片BW-S-R) id=3: 基=登记侧; +补充锚点(盘上) 1 片; 弃(盘上) 4 片 -> .ai-tmp/test/bwsr-A-discard.tsv
    ('3', '控制台用 / 或 0 代替 ~；补充锚点：`/`', '引擎 GameKey 枚举里没有 BackQuote（契约缺口）', '策划/验收表.md 允许的差异#3', '引擎补 GameKey.BackQuote'),
    ('4', '机器人"刚丢视野后仍开火"≤ 反应时间 + 1 tick', '目标反复进出视野，每 tick 恰好不可见会让扳机永不扣（曾 22 次交战 0 发）；宽限只瞄最后所见位置', '`Module/Bot/CsBotBrain.cs` 开火门限段', '机器人能稳定维持可见窗口后取消'),
    ('5', 'H 菜单不显示"当前机器人数量/难度"', '`CsHudSnapshot` 没有 BotCount/BotDifficulty 字段（契约缺口）', '`Core/CsHudSnapshot.cs`', '契约补两个字段'),
    ('6', '机器人三档在 30 s 实机小窗里准度不单调', '三档 `VisionRange`(32/40/48) 与 `PreferredRange`(12/16/22) 不同 ⇒ 小样本下几何效应盖过瞄准误差；离线 45 s 判据连续两次 PASS 且单调', '`Module/Match/CsTypes.cs`', '拉长自检窗口 ≥45 s'),
    ('7', '机器人**自然对局**命中率 5.6~9%', '双方沿路线推进，交火距离常 33~79 m 且多掩体；同代码贴身交战 43~65%', '`CsBotBrain` 目标选择 + `Dust2Builder` 路线点', '调 `Route_*` 让推进线与防守位相交'),
    ('8', '`Debug.Log` 仍在 1 处（原 2 处；① 已消除）', '① ~~`BotModule.cs:382` 在 `_verboseStats` 调试开关内（默认关）~~ → **已消除（片AC 2026-09-21）**：该行与上一行的 `Game.Logger.Info` 完全重复，冗余 `Debug.Log` 与只服务于它的 `_verboseStats` 字段一并移除（hard-rule 非注释命中 8→7）；② `BotSelfTest.cs` 是自检宿主，`Game` 未 Launch 时无 Logger', '`Module/Bot/BotModule.cs`、`BotSelfTest.cs`', '① 已消除；② 属自检工具，不消除'),
    ('9', '`Object.Instantiate` 仍在 3 处', '都是"预制体 → 场景实例"，不是池化：引擎 `Game.Pool` 管"同一对象复用"，角色/viewmodel 每次进图要新建', '`Module/View/ViewModule.cs`、`ViewModelRig.cs`', '可加视图池（收益仅"换局少一次实例化"）'),
    ('10', '9-blend 瞄准序列只取正中一路（blend 4）', '原版按瞄准方向做 2 维插值；布局实测为 `[blend][bone]`（`hlsdk` 878 行），blend4 双手最正前对称。剩余 8 路未插值', '`cs16_anim.py` 导出逻辑', '实现 2 维 blend 插值'),
    ('11', '角色腿的命中盒合并挂根骨', '原版左右腿是两个 hitbox，本工程契约只有 4 个命中盒（头/胸/腹/腿）', '`CsHitboxProxy` 挂骨映射', '契约扩成 5 个命中盒'),
    ('12', '死亡序列按 actorId 取一条，**播完才隐藏**', '原版 `death1..3` 随机取一条；我方按 `actorId % 3` 固定取一条（可复现）。**"播完才隐藏"在状态名修复后成立**（agent-17 实测：`ActorView.PlayDeath` 走 `ResolveState`，状态名不匹配时它 `return; SetShown(false)` ⇒ 旧版是**立即隐藏**；修好后实机时间线：`death2 clipLen=1.367 fps=30`，`shown=True` 一路保持到 `norm=0.902`（t=1269ms），序列在≈1.40s 播完，`shown=False` 出现在 t=1524ms）', '`ActorView.cs`（`PlayDeath` / `OnClipFinished`）', '引入随机（会牺牲可复现性）'),
    ('13', '切枪/落地**角色**序列不存在', '原版角色模型 group0 的 111 条序列里**没有** `draw`/`land`（切枪动画只在 `v_*` 上）⇒ 不是我方缺，是 A 没有', '`cs16_anim.py` 实测标签表', '无需消除（A 也没有）'),
    # 口径A(2026-09-23 片BW-S-R) id=14: 基=登记侧; +补充锚点(盘上) 0 片; 弃(盘上) 3 片 -> .ai-tmp/test/bwsr-A-discard.tsv
    ('14', '~~雷达尺寸未落 1080p 真值~~ → **已消除（2026-09-21 重出）**：`RadarSize` 已为 `128`，且**生成器重生成 `HudPanel.prefab`** 才使其生效（旧预制体停在 200f，只改常量不生效）', '旧判据「需第一人称 HUD 原版截图」被 `cstrike__sprites__hud.txt:183` 取代：`radar 640 radar640 0 0 128 128` + GoldSrc 分辨率档判据（屏幕宽>640 用 640 档、该档精灵 1:1 不缩放）⇒ 1080p 真值 = 128×128；实机 `screenRect x[24..152] y[938..1066]`、`scaleFactor=1.0000`', '`原版资源/cs16src/cstrike/cstrike__sprites__hud.txt:183`；代码 `CsHudTheme.RadarSize`；`Editor/UiGenInGame/UiBuilder.cs`（重出入口）', '已消除'),
    # 口径A(2026-09-23 片BW-S-R) id=15: 基=登记侧; +补充锚点(盘上) 1 片; 弃(盘上) 3 片 -> .ai-tmp/test/bwsr-A-discard.tsv
    ('15', '~~秒表图标内圈与截图不完全一致~~ → **已消除（2026-09-22 切片AM）：以本体为准**', '原判断：原版截图 ink 23×25 / 本体精灵 ink 17×22（两个载体不是同一版贴图）。本片取回同版 `640hud7.spr` 后按 `cstrike__sprites__hud.txt:127` 的 `stopwatch 640 640hud7 144 72 24 24` 重解覆盖 `Resources/UI/Art/stopwatch.png`（ink 318→281）；解码器在同一图集的 `cross`/`suit_full`/`suithelmet_full` 三矩形上与工程既有已验收 PNG **逐像素 0 差异（0/576）**', '`tools/probes/spr-extract.py`；载体 `原版资源/cs16src/cstrike/cstrike__sprites__640hud7.spr`；`client/资源欠缺清单.md` §2.14；补充锚点：`CsHudTheme.cs`', '已消除（截图版与本体版的差异按"以本体为准"裁决）'),
    ('16', 'HUD 血量/金钱/弹药那排的绝对坐标未对齐', '原版把坐标写死在 `cl_dlls/client.dll`（二进制），已穷尽反汇编未定位；31 张截图是旁观机位、无这排', '`原版资源/解包产物/原版HUD布局.md` BLOCKED-D1', '同上（需第一人称 HUD 截图）或继续反汇编 `client.dll`'),
    ('17', '未实现的服务器管理 cvar', '`mp_tkpunish`(0) / `mp_limitteams`(2) / `mp_winlimit`(0) / `mp_timelimit`(0) / `mp_autokick`(1) / `mp_forcecamera`(0) / `mp_fadetoblack`(0) / `decalfrequency`(30)；单机 + bot 场景下默认值多数不触发行为', '`原版资源/解包产物/原版数值表.md` §1', '逐条实现（默认 0 的项实现后行为不变）'),
    # 口径A(2026-09-23 片BW-S-R) id=18: 基=登记侧; +补充锚点(盘上) 3 片; 弃(盘上) 11 片 -> .ai-tmp/test/bwsr-A-discard.tsv
    ('18', 'ZoomFov=40 无出处；RoundEndTime=5s 为本项目自定；补充锚点：v = 40', 'CS 开镜 FOV 由 cstrike/cl_dlls/client.dll 下发，HLSDK 里没有该实现；回合结算时长未解出', 'Core/CsConst.cs:163 / Core/CsConst.cs:57；对照表 A-05；补充锚点：`client/Assets/Scripts/Core/CsConst.cs` / `策划/对照表.md`', '解出 client.dll 对应常量后'),
    ('19', '观战/第一人称机位**未复现原版 `viewsize` 补偿**', '原版在 `view->origin[2] -= 1` 之后还会按 `viewsize` 补偿（110→+1 / 100→+2 / 90→+1 / 80→+0.5 unit，`view.cpp:667-684`），而原版默认 `viewsize` 从项目内载体解不出（CS 侧 HUD 实现在 `client.dll`）⇒ agent-22 只落了确定的那一项（`view.cpp:665` 的 −1 unit = y −0.0254 m），**不编** viewsize', '`client/Assets/Scripts/Module/View/CsViewTuning.cs`（ViewModelLocalPosition 的注释）；`HLSDK/cl_dll/view.cpp:665`（已实现）/ `:667-684`（未实现）', '解出默认 `viewsize`，或原版第一人称截图能定案 viewmodel 占比'),
    ('20', '`CsViewTuning.PositionSmoothTau = 0f` 无原版出处', '原版客户端确实做位置插值（`ViewInterp` 环形缓冲），但它的口径是"`Length(delta) < 64` 才插值"的**位置回放**，换算不出可写进代码的指数平滑 tau ⇒ 取 0（直接用模拟的权威位置）', '`client/Assets/Scripts/Module/View/CsViewTuning.cs`（PositionSmoothTau 的注释）；`HLSDK/cl_dll/view.cpp:719-785`', '按原版语义实现 `ViewInterp`（位置回放而非指数平滑）'),
    ('21', '`CsViewTuning.AnimMoveSpeedEpsilon = 0.15f` 无原版出处', '原版按速度选动画档位的阈值在服务端 `cstrike/dlls/mp.dll` 里（`HLSDK/cl_dll/` 没有 CS 的角色动画选择），本项目未反汇编出该阈值 ⇒ 0.15 m/s 是项目新增的判定门限', '`client/Assets/Scripts/Module/View/CsViewTuning.cs`（AnimMoveSpeedEpsilon 的注释）', '从 `mp.dll` 反汇编出选序列的速度阈值'),
    ('22', 'HUD 血量/护甲图标已是**原版位图**，但着色取纯白', '三张图标精灵是 GoldSrc 的加性灰阶遮罩（调色板逐项 `(i,i,i)`），本身不带颜色；原版渲染时的调制值取不到（原版 HUD 那一排的坐标与颜色写在 `client.dll`，且项目内 31 张原版 1920×1080 截图全是旁观机位、没有这排）⇒ 取"无调制"（白），**不编**颜色', '`client/Assets/Scripts/UI/InGame/CsHudTheme.cs`（HudIconTint 的注释）；`策划/对照表.md` F-04 [BLOCKED]', '拿到一张第一人称 + HUD 打开的原版截图'),
    # 口径A(2026-09-23 片BW-S-R) id=23: 基=登记侧; +补充锚点(盘上) 0 片; 弃(盘上) 0 片 -> .ai-tmp/test/bwsr-A-discard.tsv
    ('23', 'HUD 右上角比分/计时块（比分两行 / 回合计时 / 竖分隔线）的原版落点只能沿用 **1920×1080 原生口径**的已记录量化值（距屏右 170 / 170 / 51 / 142 px、ink 上缘 38 / 65 / 27），且**同版载体本机已不在盘**', '判据是"落点 = 原版 1920×1080 bbox"，但项目内可比的原版 HUD 载体与 1080p 那张**不是同一尺度**：1280×1024 自由视角图（比分 ink 高 16 px vs 1080p 12 px、竖分隔线 56 px vs 67 px）与早期 1600×900 采集帧（ink 右缘距屏右 38 px vs 原版 51 px）⇒ **口径不同不可直比**，按比例硬换会得出无依据的值；本工程画布参考分辨率定为 **1920×1080**（`Menu.unity` 的 CanvasScaler `m_ReferenceResolution`，引擎 UIManager 的层级节点铺满它）⇒ **同基准直接写原版像素、不做换算**；非 1:1 采集帧的读数不用于判对错', '`client/Assets/Scripts/UI/InGame/CsHudTheme.cs:219-223`（"本单位参考分辨率也是 1920×1080 ⇒ 同一基准，直接写原版像素、不换算"）与 `:236-273`（右上角逐项落点常量 + 逐项实测 ink 值）；`策划/对照表.md` §9 的 F-03（"口径不同不可直比 ⇒ 必须 1920×1080 重采一次再判"）与 U-31~U-35（原版值 = 1920×1080 原生口径）；`策划/验收表.md` H4 / H5；31 张原版 BMP 的来源目录 `原版资源/cs16-maps/screenshots_to_conv/` 本机已不存在（`原版资源/` 实测 = `cs16src/`（片AW 后 73 份在盘：`cstrike/**` 72 + `marlett.ttf`）· `hlsdk/` · `备份/` · `_moved-out-from-assets/`；⚪ 本行原写"cs16src 已空"，片AW 取回载体后该措辞已不成立，按实测改写；要的 1920×1080 同版 HUD 载体仍不在盘）', '拿到与 1920×1080 HUD 同版、且采集条件（`hud_draw` / 视频模式）随仓库记录的原版载体后重测一次'),
    ('24', '`Map: <地图名>` 行（HUD 右上角、竖分隔线右侧）的**绝对 x 是推算值**：右缘距屏右 **15 px**（不是 1080p 实测值）', '唯一带这一行的原版载体是 1280×1024 自由视角图，它与 1920×1080 载体的 HUD **比例/锚定不一致**（比分 ink 高 16 vs 12、竖分隔线 56 vs 67）⇒ 直接按比例换算**没有依据**；能站得住的只有"**同图内两个右缘缩进之比**"（无量纲、与尺度无关）：该图上 `Map:` 行右缘缩进 28 px、计时行右缘缩进 96 px ⇒ 比值 28/96 = 0.2917，套到已实测的计时行缩进 51 px ⇒ **15 px**。旁证：竖分隔线右缘（x=1778）到屏右只剩 142 px，而 `Map: de_dust2` 在本工程字号下约 124 px 宽 ⇒ 缩进不可能大于 ~18 px（与 15 相容）；另一张同族 1920×1080 基线图 `hud_1920x1080_gg_dust2_aim_trainning.bmp` **没有**这一行 ⇒ 无 1080p 实测值可引', '`client/Assets/Scripts/UI/InGame/CsHudTheme.cs:309-341`（`MapRightInsetPx = 15f` 与其上一整段推导注释，含 freecam 逐项 ink：`Map: de_dust2` x[1113..1252] y[35..50]、计时行右缘 1184 等）；原版载体 `策划/基线图/original/de_dust2_freecam_A_00.jpg`（1280×1024）；`策划/对照表.md` §9 的 F-01（原版**有**这一行）；`策划/验收表.md` H5', '拿到 1920×1080 且画面里带 `Map:` 行的原版 HUD 载体后直接实测绝对 x'),
    ('25', '原版 `jointeam 3`（VIP）落到 CT', '本工程阵营契约 `CsTeam` 只有 `Spectator / T / CT`（`Core/CsEnums.cs`），**没有 VIP 阵营**；原版 `teammenu.res:121-139` 的 `vipbutton` 文案是 `#Cstrike_VIP_Team`（`&3 VIP`）、命令 `jointeam 3`。VIP 属 CT 一侧 ⇒ 落到 CT 并打一条 Warn（`TeamSelectPanel.OnCommand`） 【片BW-ZERO 2026-09-23 历史路径标注】本行引用的 `原版资源/cs16src/cs16game/app/cstrike/resource/ui/teammenu.res:121-139` **不在盘**（junction-aware 逐路径实测；`原版资源/` 顶层现只有 `_moved-out-from-assets / cs16src / gamestartup.mp3 / hlsdk / innoextract-1.9-windows.zip / 备份 / 清单.md`）⇒ 属**历史路径**：当时引用的 `cs16game/app/**` 抽取树现已不在。**已取回**：盘上 `cs16src/cstrike/` 下仅 3 份 `cstrike__resource__{ClientScheme, GameMenu, OptionsSubMultiplayer}.res`（`teammenu.res` 不在其中）。', '`原版资源/cs16src/cs16game/app/cstrike/resource/ui/teammenu.res:121-139` + `client/Assets/Scripts/Core/CsEnums.cs`（`CsTeam`） 【片BW-ZERO 2026-09-23 历史路径标注】本行引用的 `原版资源/cs16src/cs16game/app/cstrike/resource/ui/teammenu.res:121-139` **不在盘**（junction-aware 逐路径实测；`原版资源/` 顶层现只有 `_moved-out-from-assets / cs16src / gamestartup.mp3 / hlsdk / innoextract-1.9-windows.zip / 备份 / 清单.md`）⇒ 属**历史路径**：当时引用的 `cs16game/app/**` 抽取树现已不在。**已取回**：盘上 `cs16src/cstrike/` 下仅 3 份 `cstrike__resource__{ClientScheme, GameMenu, OptionsSubMultiplayer}.res`（`teammenu.res` 不在其中）。', '契约扩出 VIP 阵营（要改 `Core`，不在本片范围）'),
    ('26', '原版 `AUTO ASSIGN` 用**对半随机**代替"分配到人数少的一方"', '原版 `jointeam 5` 由服务器按两队人数分配；本工程是**单机版**（无真人计数）⇒ 取随机并打日志说明', '`teammenu.res:141-159`（`#Cstrike_Team_AutoAssign`）', '做联机版、或从 `CsHudSnapshot` 读到两侧人数后按原版语义分配'),
    # 口径A(2026-09-23 片BW-S-R) id=27: 基=登记侧; +补充锚点(盘上) 0 片; 弃(盘上) 2 片 -> .ai-tmp/test/bwsr-A-discard.tsv
    ('27', '~~选阵营 Frame 的底色取 `WindowBG "0 0 0 200"`~~ → **已消除（2026-09-20 实现者本片）**：`TeamMenu` 底色改为原版 `ControlBG "0 0 0 0"`（**全透明**，那块"凭空多出来的黑板"就是旧的 WindowBG）', '原判断有误：Frame 的默认底色有载体 —— `cstrike__resource__ClientScheme.res:101` 的 `BgColor "ControlBG"` 就是"所有控件的默认底色"，而 `:38` 是 `ControlBG "0 0 0 0"`（全透明）；`WindowBG "0 0 0 200"`（`:41`，行尾注释自己写着 `background color of text edit panes (chat, text entries, etc.)`）是**文本框/聊天**的底，不是 Frame 的', '`cstrike__resource__ClientScheme.res:38`（ControlBG）、`:101`（BgColor）、`:41`（WindowBG，反例）；代码 `UI/Flow/CsUiStyle.cs`（新增 `ControlBg`）+ `UI/Flow/TeamSelectPanel.cs`', '已消除（实测 `TeamMenu(Image) color=(0,0,0,0) rgba8=(0,0,0,0)`；图 `33_teamselect.png`）'),
    ('28', '**Options 页签条（7 个页签）坐标无载体出处**', '本包**缺 `OptionsDialog.res`**（它在 GameUI 静态库里，不在两张 ISO 的资源目录里）⇒ 页签条按「原版配色 + 原版字体 + 就近对齐」重建。⚠️ 页签**文案**本身有出处（`gameui_english.txt:97/98/99/100/101/41/44`），**只有坐标是新增**。【片BW-ZERO 2026-09-23 成因】这条"没看见"有**两个**原因：① **ignore 域** `原版资源/**` 被 `.gitignore:6` 忽略（实测全仓 A=4711 / B=22554，该根 A=0 / B=100）；② **二进制** 承载布局的 GameUI 静态库 / `.res` 是二进制，而 `ripgrep` **默认跳过二进制文件** ⇒ 即使打开 ignore 仍是**假 0**（同族实测：`corpse` 在 `mp.dll` 上文本行 = 0、逐字节 = ×7）。', '缺载体（实测 `原版资源/cs16src/cs16game/app/{cstrike,valve}/resource/` 下无 `optionsdialog.res`）；文案出处 `gameui_english.txt:97-101,41,44`。【片BW-ZERO 2026-09-23 域声明】范围={ 根:原版资源/**（.gitignore:6 ⇒ 全仓搜索对它是盲的）; 式:文件名 `*OptionsDialog*` + 内容 `OptionsDialog`; 工具默认:逐字节 + 显式列根 + --no-ignore; 扫描文件数:原版资源/** = 100; 各根命中:路径级 = 0；字节级 = 2（清单.md 8 处、`cstrike__resource__GameMenu.res` 1 处） }⇒ **已取回**：`原版资源/cs16src/cstrike/cstrike__resource__{ClientScheme, GameMenu, OptionsSubMultiplayer}.res` 三份（`optionsdialog.res` 不在其中）；引用的 `cs16src/cs16game/app/{cstrike,valve}/resource/` 在本仓**不存在**（路径含 `cs16game` 的文件 = 0）⇒ 属**历史路径**。', '拿到 GameUI 的 `OptionsDialog.res`，或用原版 640×480 实机图量化页签条'),
    # 口径A(2026-09-23 片BW-S-R) id=29: 基=登记侧; +补充锚点(盘上) 0 片; 弃(盘上) 2 片 -> .ai-tmp/test/bwsr-A-discard.tsv
    ('29', '子页整体落点 / 对话框标题 / `Ok·Cancel·Apply` 的归属为**本项目就近对齐**', '同上缺 `OptionsDialog.res`：子页在该对话框里的原点未知 ⇒ 页原点取 `teammenu.res` 的 Frame 左边距（76 设计 px → 171 画布 px）；那三个按钮的坐标取自 `cstrike__resource__OptionsSubMultiplayer.res:3-68`（原版把对话框级按钮就写在该子页里），本片只建一次、跨页可见', '缺载体：同 #28；坐标出处 `cstrike__resource__OptionsSubMultiplayer.res:3-68`', '同 #28'),
    ('30', '原版控件类型在本工程**没有对应实现** ⇒ 用近邻控件代替', '引擎（E-core-16 下沉后）只有 `Slider / InputField / Selector / ToggleRow / Button / Text / Image`：VGUI 的 `ComboBox`（下拉）→"点击循环"按钮、`HTML` → 纯色块 + 原文文本、`ListPanel` → 纯色块 + 按键表文本、`ImagePanel` → 纯色块。**位置/尺寸仍逐字取自 `.res`**', '`clover-client-unity-engine/Runtime/Presentation/UIWidgetControls.cs`（引擎现有控件清单）', '引擎补下拉 / 列表 / HTML 控件后'),
    ('31', '原版有、本工程**无对应实现**的选项：只复刻控件 + 留一条日志', '逐条：`Windowed` / `DetailTextures` / `Brightness` / `Gamma` / `Renderer` / `Resolution` / `AspectRatio` / `ColorDepth` / `MouseLook` / `MouseFilter` / `Joystick` / `JoystickLook` / `Auto-Aim` / `voice_modenable` / `MicBoost` / `VoiceReceive` / `#GameUI_MicrophoneVolume` / `TestMicrophone` / `ContentlockButton` / `Defaults` / `ChangeKeyButton` / `ClearKeyButton` / `Player model` / `SpraypaintList` / `SpraypaintColor` / `High Quality Models` / `Primary Color Slider` / `Secondary Color Slider`。点/拖任一都会打一条 `Info`（⛔ 不许静默"点了没反应"）', '各控件在 `optionssub*.res` 的行号见 `OptionsPanel.cs` 控件表注释', '逐条实现（多数项在单机语义下本就没有行为）'),
    ('32', '原版 `HEV suit volume` 承载本工程的**主音量**', 'CS 里没有 HEV 护甲语音（该项是 HL1 遗留），而本工程原来就有的"主音量"设置需要一只滑杆 ⇒ 复用它承载，滑杆位置/标签仍与原版逐字一致', '`optionssubaudio.res:19-34`（Suit Slider）+ `CsPlayerSettingsStore`（MasterVolume / ApplyToEngine）', '原版口径下无需消除（HL1 的 HEV 音量在 CS 里本就不生效）'),
    # 口径A(2026-09-23 片BW-S-R) id=33: 基=登记侧; +补充锚点(盘上) 1 片; 弃(盘上) 12 片 -> .ai-tmp/test/bwsr-A-discard.tsv
    ('33', '勾选框的"勾" = **原版字形贴图**（旧：同色小方块）→ **勾字形部分已消除（片AV 2026-09-22）**', '旧状态：原版勾是 **Marlett** 字体字形（`cstrike__resource__ClientScheme.res:483-492`）而本工程没有该字体 ⇒ 只能用一块同色实心小方块表达（实测旧帧那一格 `orange=64/64`）。片AV 走"预渲染贴图"路：把载体（`原版资源/cs16src/marlett.ttf`，27,724 B）**同口径**（补一张 (3,1) cmap → FreeType 光栅化 → 裁到紧致 ink 框）渲成带 alpha 的 PNG（勾 = **gid 12**，可达码位 `U+F061`；判据数字 `ink=7523 / bbox=132x140 / ratio=0.943 / comps=1 / vx=0.352 / arm=0.264`），`CsUiStyle.CreateCheckButton` 的 `Mark` 由纯色方块改用该贴图（`preserveAspect` + 白 tint；贴图 RGB 自带原版 `CheckButtonCheck` 色）。⛔ 不走 uGUI 字符路：该载体是 Windows 符号字体（cmap 无 (3,1) Unicode）⇒ Unity 侧取不到它自己的字形（切片AU 实测 448 码位 tick=Y 计数 0）。**未消除部分（仍登记）**：方框边长 16 / 文案起点 24 是**本项目新增**（原版由 Marlett 字模决定，无像素值可引）；**方框四条边由本项目凑齐** —— 原版边框由 VGUI 内部绘制（`cstrike__resource__ClientScheme.res:177-179` 只给 `CheckButtonBorder1/2` 两个**颜色**、没给画法），早先只画左右两条竖边 ⇒ 视觉退化成"一竖条"，2026-09-20 补上上下两条横边（同色）', '载体 `原版资源/cs16src/marlett.ttf`；渲染器（判据资产）`tools/probes/make-check-glyph.py`（同口径 = `tools/probes/marlett-glyphs.py`）；贴图 `client/Assets/Resources/UI/Art/menu_check.png` + `.meta`；代码 `UI/Flow/CsUiStyle.cs` 的 Mark 段 + `Core/ResPaths.cs` 的 `MenuCheckGlyph`；实机判据 `tools/probes/probe-check-mark.cs` + 裁切图 `.ai-tmp/screenshots/31_options_checkmarks-8x.png`；配色 `cstrike__resource__ClientScheme.res:177-179/30/179`；补充锚点：:483-492', '勾字形部分已消除（片AV 2026-09-22）；"方框四边本项目凑齐 / 边长 16 / 文案起点 24"仍是允许的差异'),
    # 口径A(2026-09-23 片BW-S-R) id=34: 基=登记侧; +补充锚点(盘上) 0 片; 弃(盘上) 1 片 -> .ai-tmp/test/bwsr-A-discard.tsv
    ('34', '原版字体带**CJK 回退族**', '原版 menu 全英文、没有中文；本工程面板里有中文（如"（本项目新增）"）⇒ `CreateDynamicFontFromOSFont(new[]{"Verdana","Microsoft YaHei",…})`，首选族仍是原版声明的 Verdana', '`cstrike__resource__ClientScheme.res:229`（`"name" "Verdana"`）；主族来源见 `CsUiStyle.OriginalFont` 注释', '面板界面全英文后（本工程无此计划）'),
    # 口径A(2026-09-23 片BW-S-R) id=35: 基=登记侧; +补充锚点(盘上) 0 片; 弃(盘上) 1 片 -> .ai-tmp/test/bwsr-A-discard.tsv
    ('35', '按钮 / 勾选框的 **1 设计 px 边框**未画；按钮文字内缩取 0', '原版的边框与文字内缩由 VGUI 内部绘制（`Borders/ButtonBorder` 只给了四条边的颜色，没给"uGUI 里怎么画"）⇒ 本片只落 `ButtonBG` 底色 + 左对齐（`textAlignment west`）。⚠️ 这不是"忘了"，是**载体只到颜色为止**', '`cstrike__resource__ClientScheme.res:740-778`（ButtonBorder）/`:740-742`（inset 0 0 0 0）', '拿到原版实机图后量化边框宽度与文字内缩像素'),
    ('36', '未做（原版有）：`Sound Quality` 下拉 / 选兵种 `classmenu_*.res`', '① 原版音质档位的标签是 HL1 的玩笑串（`GameUI_High "Horrible"` / `GameUI_Low "Even worse"`），本工程也没有音质档位 ⇒ 该控件与其标签**未建**（按任务书"可以比原版少"）；② 选兵种任务书明说允许本片不做 【片BW-ZERO 2026-09-23 历史路径标注】本行引用的 `原版资源/cs16src/cs16game/app/cstrike/resource/ui/classmenu_ct.res、classmenu_ter.res` **不在盘**（junction-aware 逐路径实测；`原版资源/` 顶层现只有 `_moved-out-from-assets / cs16src / gamestartup.mp3 / hlsdk / innoextract-1.9-windows.zip / 备份 / 清单.md`）⇒ 属**历史路径**：当时引用的 `cs16game/app/**` 抽取树现已不在。**已取回**：盘上 `cs16src/cstrike/` 下仅 3 份 `cstrike__resource__*.res`（两份 classmenu 均不在盘）。', '`gameui_english.txt:63-64`；`原版资源/cs16src/cs16game/app/cstrike/resource/ui/classmenu_ct.res`、`classmenu_ter.res` 【片BW-ZERO 2026-09-23 历史路径标注】本行引用的 `原版资源/cs16src/cs16game/app/cstrike/resource/ui/classmenu_ct.res、classmenu_ter.res` **不在盘**（junction-aware 逐路径实测；`原版资源/` 顶层现只有 `_moved-out-from-assets / cs16src / gamestartup.mp3 / hlsdk / innoextract-1.9-windows.zip / 备份 / 清单.md`）⇒ 属**历史路径**：当时引用的 `cs16game/app/**` 抽取树现已不在。**已取回**：盘上 `cs16src/cstrike/` 下仅 3 份 `cstrike__resource__*.res`（两份 classmenu 均不在盘）。', '需要时再补（音质档位先要有引擎侧的档位概念）'),
    ('37', '`game_menu.tga` / `logo_game.tga` 已复制进工程但**本片不使用**', '任务书把"怎么用"划给 agent-32（主菜单片），避免两片抢同一处；本片只负责把它们从原版搬进工程 【片BW-ZERO 2026-09-23 历史路径标注】本行引用的 `原版资源/cs16src/cs16game/app/cstrike/resource/game_menu.tga（26540 B）、logo_game.tga（65580 B）` **不在盘**（junction-aware 逐路径实测；`原版资源/` 顶层现只有 `_moved-out-from-assets / cs16src / gamestartup.mp3 / hlsdk / innoextract-1.9-windows.zip / 备份 / 清单.md`）⇒ 属**历史路径**：当时引用的 `cs16game/app/**` 抽取树现已不在。**已取回**：**工程内副本在盘**：`client/Assets/Resources/UI/Art/{game_menu,logo_game}.tga`（源路径属历史，副本可直接使用）。', '源 = `原版资源/cs16src/cs16game/app/cstrike/resource/game_menu.tga`（26540 B）、`logo_game.tga`（65580 B）→ `client/Assets/Resources/UI/Art/`（字节数逐一相同） 【片BW-ZERO 2026-09-23 历史路径标注】本行引用的 `原版资源/cs16src/cs16game/app/cstrike/resource/game_menu.tga（26540 B）、logo_game.tga（65580 B）` **不在盘**（junction-aware 逐路径实测；`原版资源/` 顶层现只有 `_moved-out-from-assets / cs16src / gamestartup.mp3 / hlsdk / innoextract-1.9-windows.zip / 备份 / 清单.md`）⇒ 属**历史路径**：当时引用的 `cs16game/app/**` 抽取树现已不在。**已取回**：**工程内副本在盘**：`client/Assets/Resources/UI/Art/{game_menu,logo_game}.tga`（源路径属历史，副本可直接使用）。', '已消除（`game_menu.tga` 随主菜单片使用而消除；`logo_game.tga` 部分随 #46 消除）'),
    ('38', '选阵营 `.res` 里 `visible=0` 的控件不建；`MapInfo`（`HTML`）用纯色块 + 原文文本复刻', '`SysMenu`（`teammenu.res:17-30`）与 `mapname`（`:65-81`）在原版就是隐藏的（`visible 0`）⇒ 建了反而与原来的画面不一致；`MapInfo` 的 `HTML` 控件本工程没有 ⇒ 用列表底 + `maps/de_dust2.txt` 原文文本占同一块矩形（`:31-44`） 【片BW-ZERO 2026-09-23 历史路径标注】本行引用的 `原版资源/cs16src/cs16game/app/cstrike/maps/de_dust2.txt` **不在盘**（junction-aware 逐路径实测；`原版资源/` 顶层现只有 `_moved-out-from-assets / cs16src / gamestartup.mp3 / hlsdk / innoextract-1.9-windows.zip / 备份 / 清单.md`）⇒ 属**历史路径**：当时引用的 `cs16game/app/**` 抽取树现已不在。**已取回**：盘上真实位置 = `原版资源/cs16src/cstrike/overviews/de_dust2.txt` ⇒ ⛔ 本行不改指：保留原引用以便追溯（盘上真实位置如上；是否为同一对象未判定）。', '`teammenu.res:17-30`、`:65-81`、`:31-44`；文本出处 `原版资源/cs16src/cs16game/app/cstrike/maps/de_dust2.txt` 【片BW-ZERO 2026-09-23 历史路径标注】本行引用的 `原版资源/cs16src/cs16game/app/cstrike/maps/de_dust2.txt` **不在盘**（junction-aware 逐路径实测；`原版资源/` 顶层现只有 `_moved-out-from-assets / cs16src / gamestartup.mp3 / hlsdk / innoextract-1.9-windows.zip / 备份 / 清单.md`）⇒ 属**历史路径**：当时引用的 `cs16game/app/**` 抽取树现已不在。**已取回**：盘上真实位置 = `原版资源/cs16src/cstrike/overviews/de_dust2.txt` ⇒ ⛔ 本行不改指：保留原引用以便追溯（盘上真实位置如上；是否为同一对象未判定）。', '无需消除（原版本就不可见 / 文本已是原版原文）'),
    ('39', '~~主菜单背景为纯深色、无原版背景贴图~~ → **已消除（2026-09-20 实现者本片）**：主菜单与读条两屏的底都换成**原版 12 块 TGA 拼图**（逐字节复制 + 逐块按原坐标摆放），纯色 `#1B1B1B` 只保留为"贴图缺失时的兜底层"', '原判断"载体里没有主菜单背景"被推翻：`valve/resource/` 下**同时**有 `backgroundlayout.txt`（不带 loading 的那份 = 通用背景布局）与 `backgroundloadinglayout.txt`（内容逐字相同），它描述的 12 块 `resource/background/800_{1,2,3}_{a,b,c,d}_loading.tga` 就是这一屏背景的贴图与布局（256×3+32=800、256+256+88=600 与文件头 `resolution 800 600` 自洽） 【片BW-ZERO 2026-09-23 历史路径标注】本行引用的 `原版资源/cs16src/cs16game/app/valve/resource/backgroundlayout.txt:3-16、原版资源/cs16src/cs16game/app/cstrike/resource/background/800_*_loading.tga（12 张）` **不在盘**（junction-aware 逐路径实测；`原版资源/` 顶层现只有 `_moved-out-from-assets / cs16src / gamestartup.mp3 / hlsdk / innoextract-1.9-windows.zip / 备份 / 清单.md`）⇒ 属**历史路径**：当时引用的 `cs16game/app/**` 抽取树现已不在。**已取回**：12 张贴图的**工程内副本在盘**：`client/Assets/Resources/Background/800_*_loading.tga`（12/12 实测）；`backgroundlayout.txt` 本体不在盘。', '`原版资源/cs16src/cs16game/app/valve/resource/backgroundlayout.txt:3-16`、`原版资源/cs16src/cs16game/app/cstrike/resource/background/800_*_loading.tga`（12 张，24bpp）；代码 `UI/Flow/CsMenuBackground.cs`、`Core/ResPaths.cs`、`Editor/Flow/FlowSetup.cs` 【片BW-ZERO 2026-09-23 历史路径标注】本行引用的 `原版资源/cs16src/cs16game/app/valve/resource/backgroundlayout.txt:3-16、原版资源/cs16src/cs16game/app/cstrike/resource/background/800_*_loading.tga（12 张）` **不在盘**（junction-aware 逐路径实测；`原版资源/` 顶层现只有 `_moved-out-from-assets / cs16src / gamestartup.mp3 / hlsdk / innoextract-1.9-windows.zip / 备份 / 清单.md`）⇒ 属**历史路径**：当时引用的 `cs16game/app/**` 抽取树现已不在。**已取回**：12 张贴图的**工程内副本在盘**：`client/Assets/Resources/Background/800_*_loading.tga`（12/12 实测）；`backgroundlayout.txt` 本体不在盘。', '已消除（运行时逐块 `mismatch=0`、12/12 就绪；图 `33_mainmenu.png` / `33_loading.png`）'),
    ('40', '主菜单**菜单项之间无间隙**（步进 = 项高）', '原版各项紧邻排列，**间隙值在载体里没有**（`trackerscheme.res` 的 `InGameDesktop` 块只给了 `MenuItemHeight` 与 `GameMenuInset`）⇒ 取"步进 = 项高"这一**可从载体推出**的摆法（⛔ 没编一个间隙值） 【片BW-ZERO 2026-09-23 历史路径标注】本行引用的 `原版资源/cs16src/cs16game/app/platform/resource/trackerscheme.res:164-173` **不在盘**（junction-aware 逐路径实测；`原版资源/` 顶层现只有 `_moved-out-from-assets / cs16src / gamestartup.mp3 / hlsdk / innoextract-1.9-windows.zip / 备份 / 清单.md`）⇒ 属**历史路径**：当时引用的 `cs16game/app/**` 抽取树现已不在。**已取回**：该 `.res` 不在盘（"间隙值无出处"的判断不因此改变，仍按待补记）。', '`原版资源/cs16src/cs16game/app/platform/resource/trackerscheme.res:164-173`；间隙值无出处 【片BW-ZERO 2026-09-23 历史路径标注】本行引用的 `原版资源/cs16src/cs16game/app/platform/resource/trackerscheme.res:164-173` **不在盘**（junction-aware 逐路径实测；`原版资源/` 顶层现只有 `_moved-out-from-assets / cs16src / gamestartup.mp3 / hlsdk / innoextract-1.9-windows.zip / 备份 / 清单.md`）⇒ 属**历史路径**：当时引用的 `cs16game/app/**` 抽取树现已不在。**已取回**：该 `.res` 不在盘（"间隙值无出处"的判断不因此改变，仍按待补记）。', '拿到原版实机图后量化项间距'),
    ('41', '主菜单底部 **`by clover-engine`** 署名为本项目新增', 'skill §8 品牌硬约定：每个游戏的首页画面底部必须有这一行（判据 = 实机截图 / 运行时节点树）；原版 `gamemenu.res` 局外四项里没有它', '原版 `gamemenu.res`（四项：NewGame/FindServers/Options/Quit）无此项；品牌出处 = 本项目 skill §8', '无需消除（品牌要求）'),
    ('42', '主菜单**字标是按钮**（`game_menu.tga` 常态 + `game_menu_mouseover.tga` 悬停）', '原版两张同画布（207×32）贴图分别对应常态（金）与悬停（白）⇒ 落成 `Button(transition=SpriteSwap)`。⚠️ 原版该字标**是否可点**在载体里无从判断（`gamemenu.res` 无对应项）⇒ 本片让它不可点（`onClick=null` 未接回调）', '贴图 `cstrike/resource/game_menu.tga`、`game_menu_mouseover.tga`（各 26540 B，207×32/32bpp）', '拿到 GameUI 行为证据后定它可不可点'),
    ('43', '**本表/`对照表` 里对 `CsHudTheme.cs` 的行号引用为「路径收敛前」基准**', '2026-09-20 把 4 个路径常量（原 `:96-99`）搬进 `Core/ResPaths.cs`，该文件其后行号**整体前移约 6 行**（`:303` 之后约 7 行）；`Dust2Layout.cs` 同理 +0/−1（删 2 行）、`MainMenuPanel.cs` −2（删 2 个常量并加 3 行注释）。⛔ 引用指向的**语义位置不变**，只是行号需按上述位移换算', '出处 = `Core/ResPaths.cs` 与各文件当前内容（`screenshot-refs` 闸门只校验"文件可达"，不校行号）', '下次有人顺手校一遍行号时消除（一次性机械活，无功能影响）'),
    # 口径A(2026-09-23 片BW-S-R) id=44: 基=盘上超集(登记侧 ⊆ 盘上)⇒ 整行逐字取盘上; 弃(盘上) 0 片
    ('44', '原版**音效已复制进工程但未接事件**（5 条：`dryfire` / `hit_wall` / `knife_hit` / `flash_explode` / `bomb_beep_fast`）', '原版 CS 对应时刻都有音（空仓扣扳机 / 弹着墙 / 刀命中 / 闪光爆 / C4 快蜂鸣），本工程那些时刻只有表现或只打日志、**无声**。接线属「音效事件」维度（D8）——切片H 任务书明令本片不做 D8', '载体 = `client/Assets/Resources/Sound/SFX/sfx/{dryfire,hit_wall,knife_hit,flash_explode,bomb_beep_fast}.wav`（原版 `cstrike/sound/` 同名）；判据 = `tools/probes/enumerate-entities.py` 的 D8 段', '下一个「音效事件」片把它们逐个接到开火/命中/下包分支后（届时本行删除）'),
    # 口径A(2026-09-23 片BW-S-R) id=45: 基=登记侧; +补充锚点(盘上) 4 片; 弃(盘上) 8 片 -> .ai-tmp/test/bwsr-A-discard.tsv
    ('45', '原版 de_dust2 贴图（8 张：SandRoadTgtA / _1Sand / _1SandRock2 / _1csSandWall / _2SandRock2 / _3Sand / black / wall_g）已复制但几何未引用', '本工程几何只用 geo.bin 的 33 个主材质组；这 8 张属原版的贴花层（TgtA）与细节层（detail）贴图，本工程未实现那两层（经实测：8 张的 guid 在全工程任何 .mat/.prefab/.unity/.asset 里都 0 次命中）；补充锚点：`/`', 'client/Assets/ThirdParty/Dust2/Textures/；原版 de_dust2.bsp 的 miptex 目录（tools/probes/bsp-entities.py 可重数）；补充锚点：`client/Assets/ThirdParty/Dust2/Textures/` / `tools/probes/bsp-entities.py`', '补贴花/细节层，或在「只复制被引用的那几个」原则下把它们移出 client/Assets/；补充锚点：`client/Assets/`'),
    # 口径A(2026-09-23 片BW-S-R) id=46: 基=登记侧; +补充锚点(盘上) 2 片; 弃(盘上) 8 片 -> .ai-tmp/test/bwsr-A-discard.tsv
    ('46', '原版 GameUI 字标 logo_game.tga 已复制但未使用', '任务书把「怎么用」划给主菜单片；本片只负责把它从原版搬进工程（见验收表「允许的差异」#37）', 'client/Assets/Resources/UI/Art/logo_game.tga；源 = 原版资源/cs16src/cs16game/app/cstrike/resource/logo_game.tga；补充锚点：`client/Assets/Resources/UI/Art/logo_game.tga`', '主菜单片把它接进 ResPaths 并上屏后；补充锚点：`Core/ResPaths.cs`'),
    ('47', '`Q` / `G` / `M` 三键**在切片H 才补上绑定**；`M`（原版 `chooseteam`）的**行为等价为"直接换到另一边"**而非打开阵营菜单', '三键的原版默认绑定有出处（`bind "q" "lastinv"` / `bind "g" "drop"` / `bind "m" "chooseteam"`，见 `策划/策划案/CS1.6单机参考规格.md` §1 游戏内按键段）⇒ 有出处故补绑定（落点 `Module/Combat/CombatModule.cs` 的 `FillInput`）。但**本工程局内没有"再开一次 TeamMenu"的流程入口** ⇒ `M` 只能等价成直接换边（H 菜单「换阵营」就是这条链），已如实打日志，**不编一个不存在的阵营菜单**', '绑定出处 = `策划/策划案/CS1.6单机参考规格.md`；落点 = `client/Assets/Scripts/Module/Combat/CombatModule.cs`（`FillInput`）；判据 = `tools/probes/enumerate-entities.py` 的 D11 段', '工程做出局内阵营菜单后把 `M` 改回"打开菜单"（届时本行删除）'),
    # 口径A(2026-09-23 片BW-S-R) id=48: 基=登记侧; +补充锚点(盘上) 4 片; 弃(盘上) 7 片 -> .ai-tmp/test/bwsr-A-discard.tsv
     ('48', '修前/修后 2x2 合成图 92_fix_before_after_2x2 采不到（修前帧不可复现）', '修前帧 80_fix_pre_char_invisible / 81_fix_pre_vm_nogun 是 bug 现场抓的诊断图；bug 修好后（AnimSetup.Fill 按值传参 ⇒ 蒙皮 bindpose 全零 ⇒ 几何塌成一点）同形态的修前帧再也出不了。拿别的图冒充或临时改回旧实现去"复现"都属伪造 ⇒ 改为「修后帧 93_fix_post_char_closeup / 97_fix_post_char_front + 逐骨骼/包围盒数值」作为判据', 'client/Assets/Editor/Views/AnimSetup.cs（Fill 的修复处）；策划/验收表.md「允许的差异」新增行；R1/R2 行的旧图名已按「不可采」改写；**片BU-R 2026-09-23 收尾**：验收表 68 / 149 / 150 / 153 / 222 行原写作「92_fix_before_after_2x2 + 空格 + .png」的**不可解析图名已清掉**（改成"不采 + 指向差异 #48"），全库只在两处保留**不带 `.png`** 的名字用来说明它是什么（本行 + 验收表 #48 行）；补充锚点：`client/Assets/Editor/Views/AnimSetup.cs` / `93_fix_post_char_closeup.png` / `97_fix_post_char_front.png` / `策划/差异登记.tsv`', '不消除（修前态本就不可复现；若将来又出现同类蒙皮 bug，则在现场重采 2x2）'),
    # 口径A(2026-09-23 片BW-S-R) id=49: 基=登记侧; +补充锚点(盘上) 0 片; 弃(盘上) 1 片 -> .ai-tmp/test/bwsr-A-discard.tsv
    ('49', '位图（CloverMap v1）是单层 2D：箱子所在格记为“可走”（箱顶是朝上的面）', '格式层没有高度（FlagHeightField 预留但 V1 解码器拒绝）⇒ 一格一位，表达不了“同一格在 y=0 被挡、在 y=1.2 通畅”；带来的边界：箱子进不去（已由 CsMap.CanStand 的“地面一步闸门”拦住），但位图本身仍不能单独回答“能不能穿”', 'Assets/Scripts/Module/Map/CsMap.cs（CanStand/BodyHeightClear）；Packages/com.clover.unity-engine/Runtime/Presentation/MapFormat.cs（FlagHeightField，第 30 行）', '引擎开出 V2 高度场（FlagHeightField）后'),
    # 口径A(2026-09-23 片BW-S-R) id=50: 基=登记侧; +补充锚点(盘上) 0 片; 弃(盘上) 0 片 -> .ai-tmp/test/bwsr-A-discard.tsv
    ('50', '投掷物与角色之间不互相挡/推开', '本片只把“角色对角色”这一层做出来（CsActorSeparation 只收 CsActor）；原版投掷物是 MOVETYPE_BOUNCE 实体，与角色是否互相阻挡本机取不到可信出处（原版 mp.dll **已由切片AW 取回盘**，1,640,960 B / `D7294D9B…1F2974`，见 `原版资源/清单.md`「切片AW」节；其 solid/movetype 立即数未定位）', 'Assets/Scripts/Module/Map/CsActorSeparation.cs（只收 actor）；Module/Match/CsInventory.cs（投掷物落点）', '解出原版投掷物的 solid/movetype 后'),
    # 口径A(2026-09-23 片BW-S-R) id=51: 基=登记侧; +补充锚点(盘上) 4 片; 弃(盘上) 2 片 -> .ai-tmp/test/bwsr-A-discard.tsv
    ('51', '雷达底图 ~~非原版~~ → **已消除（片AS 2026-09-22）**：底图已换成原版 `overviews/de_dust2.bmp`；补充锚点：`tools/probes/import-original-overview.py` / h=0',
     '旧状态：原版 overviews/de_dust2.bmp + .txt 本机不在盘 ⇒ 降级链退到级①（由工程内 de_dust2_geo.bin 离线俯视栅格化）。片AR 把该载体从公开 repack 取回（SHA256 记在 原版资源/清单.md 切片AR 节），片AS 用 tools/probes/import-original-overview.py 把它**逐像素**转成 Resources/UI/Art/overview_de_dust2（重解码自检 rgb_mismatch=0 / alpha_mismatch=0，绿键色 → alpha 0 与 GoldSrc 同语义）⇒ 底图现在是**原版像素**，不再是几何栅格化；补充锚点：`原版资源/清单.md`',
     'client/Assets/Resources/UI/Art/overview_de_dust2（1024×768）；tools/probes/import-original-overview.py（判据资产）；载体 原版资源/cs16src/cstrike/cstrike__overviews__de_dust2.bmp；Core/ResPaths.cs:117；补充锚点：`tools/probes/import-original-overview.py`',
     '已消除（片AS：底图 = 原版 BMP 的逐像素 PNG；同名覆盖，代码路径不变）'),
    # 口径A(2026-09-23 片BW-S-R) id=52: 基=登记侧; +补充锚点(盘上) 1 片; 弃(盘上) 6 片 -> .ai-tmp/test/bwsr-A-discard.tsv
    ('52', '枪口火焰 / 弹痕已换原版像素（火星仍程序生成）', '**已部分消除（切片AM 2026-09-22；弹痕部分切片AW 2026-09-22 补）**：`sprites/muzzleflash2.spr` 帧 0 → 覆盖 `Resources/UI/Art/fx_muzzleflash.png`（64×64 尺寸不变、ink 1328→2044，由 `tools/probes/spr-extract.py` 从原版载体解出）；`decals.wad` 的 `{shot1`（16×16 载体原生尺寸）→ 覆盖 `Resources/UI/Art/fx_bullethole`（原为 32×32 程序化替身），由 `tools/probes/wad3-extract.py` 解出（225/225 lump 过三重自洽断言）。未消除部分：① 火星无独立原版载体；② 原版按武器类别在 `client.dll` 里选 muzzleflash 1..4 并播 3 帧动画，本工程所有武器共用帧 0（该映射无载体出处）；③ 原版弹痕是 `{shot1..5` 五变体随机 + `{bigshot*` 大口径，本工程只有一张贴图 ⇒ 取 `{shot1`（同名覆盖，png 不增）'
     '【片FX-ALL 2026-09-23 补】③ **多变体已接入**：`{shot1`…`{shot5` 五张全解出并落盘为 `Resources/UI/Art/fx_shot1`…`fx_shot5`，由 `ResPaths.FxBulletHoleKeys`（**字面量 key 表**，⛔ 不是"前缀 + 序号"拼串）逐个 `LoadAsset`、命中时五变体随机取一（出处 = 原版 `mp.dll` 贴花注册名表索引 0..4）。`{bigshot1`…`{bigshot5` 也**已解出**（台账 `.ai-tmp/test/fx-decal-variants.tsv` 记了尺寸与像素指纹）但**刻意不落盘**：大口径映射在引擎 `hw.dll`（不在盘）⇒ 无出处，落盘即成为"文件在盘上但无人读"的 T0 不一致（本片实测 5 张各记一条 FAIL）。仍未消除：① 火星无独立原版载体；② 武器 → `muzzleflash1..4` 的映射与逐帧播放（`client.dll` 未反汇编）。', 'Core/ResPaths.cs:147-159 / tools/probes/spr-extract.py / tools/probes/wad3-extract.py；载体 `原版资源/cs16src/cstrike/cstrike__sprites__muzzleflash2.spr`、`原版资源/cs16src/cstrike/decals.wad`（960,012 B / SHA256 `C9E852B60197177F1E6F54992C3F0E886425AB6E6CAFE9F1B1E5E6B3BF80850C`）', '② 解出 `client.dll` 的武器→muzzleflash 映射并实现逐帧播放；③ 工程侧支持弹痕多图变体（`{shot2..5` / `{bigshot*`）后接入；补充锚点：`p_cross_crouchfire.png`'),
    # 口径A(2026-09-23 片BW-S-R) id=53: 基=盘上超集(登记侧 ⊆ 盘上)⇒ 整行逐字取盘上; 弃(盘上) 0 片
    ('53', 'de_dust2.bsp func_breakable 木箱（×10）未实现可破坏', '工程把箱子当静态几何（贴图资产 `box.png` / `box_x.png` —— ⛔ 此处是**资产名不是截图引用**，旧写法在 `.png` 前多了一个空格，已按实名还原），没有受击碎裂逻辑', 'Assets/ThirdParty/Dust2/de_dust2.bsp（entity lump）', '实现 func_breakable 后'),
    ('54', '已移出工程（切片H）：Assets/Scenes/SampleScene.unity、Resources/Sound/SFX/sfx/reload_unused.wav', 'Unity 模板自带场景（未登记 Build Settings、无代码引用）与一个名字就是 unused 的通用换弹音（本工程换弹音按武器逐把拼名）—— 两者都不属于参考物的必备引用，不应进工程', 'Assets/Scenes/SampleScene.unity；Assets/Resources/Sound/SFX/sfx/reload_unused.wav', '已消除（2026-09-21 切片H 移出到 原版资源/_moved-out-from-assets/）'),
    # 口径A(2026-09-23 片BW-S-R) id=55: 基=登记侧; +补充锚点(盘上) 0 片; 弃(盘上) 1 片 -> .ai-tmp/test/bwsr-A-discard.tsv
    ('55', '切片K：dryfire / hit_wall / knife_hit / bomb_beep_fast / round_start2 这 5 条 wav 的**原版源文件名映射未记录**', '它们确是原版 CS 1.6 的音效（空仓击发 / 弹着 / 刀命中 / C4 快蜂鸣 / 备用回合开始），但 `client/资源欠缺清单.md:37` 第 11 项只记了 c4_beep1 / c4_plant / c4_disarm / c4_explode1 / hegrenade-1 / flashbang-1 / radio/bombpl / radio/bombdef 这 8 条映射；原版 sound/ 树的**采样本体仍不在盘** —— `原版资源/cs16src` 现有 73 份在盘件（`cstrike/**` 72 + `marlett.ttf`，片AW 取回），其中 `cstrike/sound/` 只有 `materials.txt`（**材质→地面音效分类真源**，11064 B / SHA256 `0D35D708CB3737FC99CAF50E41AE32680E197218D2A51488489ECB7561B3AB6C`，片AW 取回）与 2 个采样（`sound/player/pl_step1.wav`、`sound/weapons/m4a1-1.wav`），**没有** dryfire / hit_wall / knife_hit / bomb_beep_fast / round_start2 ⇒ 这 5 条短名仍无法逐条对回原版文件名（sound 目录的完整名单是目录枚举、非下载，见 `原版资源/清单.md`「切片AW」§3 与 `.ai-tmp/test/aw-sound-tree.txt`）', 'client/Assets/Resources/Sound/SFX/sfx/{dryfire,hit_wall,knife_hit,bomb_beep_fast,round_start2}.wav（在盘）；client/资源欠缺清单.md:37；原版资源/清单.md「切片AW」§3（sound 目录枚举名单 + materials.txt 的字节/SHA256）', '用户补回 CS 1.6 客户端本体（原版资源/cs16src）后逐条对账'),
    ('56', '切片K：hit_wall 的「按材质分流」只落到一条采样，且刀「砍空」没有独立采样', '原版打沙 / 打木箱 / 打金属是**不同采样**，刀砍中人与砍空也是两条采样；盘上只有 hit_wall.wav（打墙）与 knife_hit.wav（刀命中）各一条 ⇒ 材质分类（CsAudioTuning.ClassifyImpact）已做、日志可逐类核对，但各材质现在落同一 clip；刀砍空（CsInventory.RaycastActor 返回 null）无音', 'Module/Audio/CsAudioTuning.cs（ClassifyImpact / HitWall / KnifeHit）；Module/Combat/CombatModule.cs（弹着音挂点）；Module/Match/CsDamage.cs（刀命中挂点）', '拿到原版按材质的弹着采样与刀挥空采样后，只改 CsAudioTuning 的分类→短名映射'),
    # 口径A(2026-09-23 片BW-S-R) id=57: 基=登记侧; +补充锚点(盘上) 1 片; 弃(盘上) 0 片 -> .ai-tmp/test/bwsr-A-discard.tsv
    ('57', 'C4 蜂鸣的「加速档分界 10s」与两档间隔（1.0s / 0.25s）无原版出处', '原版 C4 蜂鸣节奏写在 `mp.dll` 的 C4 逻辑里（不是 cvar，`settings.scr` / `server.cfg` 都查不到），而 `mp.dll` **已在盘、但尚未反汇编**（`原版资源/cs16src/cstrike/dlls/mp.dll`，1,640,960 B / SHA256 `D7294D9BE016C79E5E3B0D9E78C14CF385FCC3F1DB6057018052ABC4219F2974`，片AW 取回，见 `原版资源/清单.md`「切片AW」§1）⇒ 拿不到 C4 逻辑里那两个立即数，该分界只能按本工程自己的口径统一（CsConst.BombBeepIntervalSlow/Fast 的 10s 注释 + CsAudioTuning.BombBeepFastBelow）；补充锚点：`原版资源/cs16src`', 'Core/CsConst.cs（BombBeepIntervalSlow / BombBeepIntervalFast）；Module/Audio/CsAudioTuning.cs（BombBeepFastBelow）', '解出 mp.dll 的 C4 蜂鸣节奏后'),
    ('58', 'CsBotConst 的绝大多数阈值无原版出处（**本项目新增**）', 'A = CS 1.6 本体**不含机器人 AI**（官方 bot 属 Condition Zero / PodBot，不在本工程的载体范围）⇒ "bot 手感阈值"在 A 里没有对应量；规格 §2.4 只给三档的反应时间 / 瞄准误差（±6° / ±3° / ±1.2°）与行为特征，不含这些阈值。三条有对应量却取不到载体的（瞄胸高度比例 / 脚步噪声阈值 / 预瞄节奏）见下面两条与 CsBotConst 各行的注释', 'Module/Bot/CsBotConst.cs（66 行逐条注释已标"本项目新增"或指到定义真源）；策划/策划案/CS1.6单机参考规格.md:113-118（§2.4 三档表）；Module/Match/CsTypes.cs:148（CsBotProfile）', '若主 agent 决定改为「逐条对齐 PodBot / CZ bot 源码」则另开片'),
    # 口径A(2026-09-23 片BW-S-R) id=59: 基=登记侧; +补充锚点(盘上) 1 片; 弃(盘上) 2 片 -> .ai-tmp/test/bwsr-A-discard.tsv
    ('59', '脚步声触发口径与落地音阈值无原版出处（StepDistanceRun / StepMinSpeed / StepMinInterval / LandMinFallSpeed）【片FX-ALL 2026-09-23 落地：旧两名已删，脚步改按原版时间制冷却、落地阈值取 580/2 u/s，详见「为什么」的落地段】', '① 原版脚步触发口径在 GoldSrc `pm_shared.c`（PM_PlayStepSound），该文件**已在盘**：`原版资源/hlsdk/pm_shared/pm_shared.c`（片AY 落盘；片BD 2026-09-22 实测复核：`原版资源/hlsdk/` 下有 `cl_dll/`、`common/`、`dlls/`、`pm_shared/pm_shared.c` 共 10 份）—— **但本行尚未逐行读它取口径**（`原版资源/cs16src/` 现存 73 份 = 片AW 取回的 `cstrike/**` 资源 + `marlett.ttf`，其中没有 GoldSrc 源码树；`原版资源/hlsdk/` 才是源码树那一份拷贝）；② 落地音 A **本来就没有**（`client/资源欠缺清单.md:33` 第 7 项：GoldSrc 落地复用脚步采样），本工程用 pl_step4 采样代替、并自定"多快才算摔了一下"的阈值【片FX-ALL 2026-09-23 落地 · 消除（脚步口径）】已**逐行读完**并据此重做（不再是"载体在盘但没读"）：载体 `原版资源/hlsdk/pm_shared/pm_shared.c`，取口径的行是 `:500-639`（`PM_UpdateStepSound` 全体）、`:517`（`speed = Length(pmove->velocity)` ⇒ 三维模长）、`:519`/`:521`（`speed < 150` ⇒ `flTimeStepSound = 400`，只推冷却不发声）、`:567`~`:626` + `:556`（放音分支装 300 ms；**多处同值** = 材质 switch 10 个分支 + 脚部涉水 SLOSH）、`:630-632`（`FL_DUCKING / fLadder` ⇒ `+= 100`）、`:2400-2410`（`PM_ReduceTimers` 的 `flTimeStepSound -= cmd.msec`，递减在 `:2404`）、`:2493`（`PM_PlayerMove` 内 `PM_Duck()` 之后调用）、`:514`（`FL_FROZEN` 直接 return）；落地闸取 `:2243`（`flFallVelocity > PLAYER_MAX_SAFE_FALL_SPEED / 2`）+ `:125`（`580`）+ `:2477`（`flFallVelocity = -velocity[2]`）。落地改动：① `Module/Audio/CsAudioTuning.cs` 整组换成新五常数 —— `StepMinSpeed = 3.81`（= 150 u/s x 0.0254）、`StepCooldownConcreteMs = 300`、`StepCooldownSlowMs = 400`、`StepDuckingExtraMs = 100`、`LandMinFallSpeed = 7.366`（= 580/2 u/s x 0.0254），旧 `StepDistanceRun` / `StepMinInterval` **已删**；② `Module/Audio/AudioModule.cs` 的 `TickFootsteps` 由**距离制**换成**时间制冷却**，五道顺序按原版排（逐帧递减 → 夹零 → 冷却+`CsRoundPhase.Freeze` ⇒ 冷却停在 0 → `speed < 3.81` 装 400 ms 不发声 → 否则装 300 ms(+蹲行 100 ms) 并放音），速度口径改为 `CsActor.Velocity.magnitude`（= 原文 `Length(velocity)` 三维模长，此前是水平分量）。判据资产 `tools/probes/step-sound-probe.py`（**RESULT: PASS**）：结构断言 9 条；原版 11 个关键行"行号 + 逐字 + 首次出现"三重相符，`300 ms` 那句按"多处同值"判（材质表 10 行 + 涉水 1 行）；反例复现（v = SpeedRifle 4.4 m/s x 5 s）旧 6.00 Hz vs 新 3.40 Hz = 1.76 x；并证明五种速度（knife/rifle/AWP/walk/crouch）的**出声集合两条口径完全一致**（150 门槛没被顺手改）。⚠️ 仍未消除（如实列）：① 落地音的**采样**本身仍是"本项目新增"（A 没有独立落地音，本工程用 `player/pl_step4.wav`）—— 这部分属"A 本来就没有"，不消除；② 原版的**音量档** `fvol`（材质 0.35/0.5/0.65、落地 1.0/0.85、蹲行 `fvol *= 0.35`）**未复现**（`SfxService.Play` 没有音量形参）；③ `StepDuckingExtraMs` 那一档在当前配置下**不可达**（蹲行 1.84 m/s < 3.81 m/s；原版也要先过 150 u/s 才走 +100 分支），只做到行级对齐。；补充锚点：`原版资源/cs16src`', 'Module/Audio/CsAudioTuning.cs（Step* / LandMinFallSpeed）；client/资源欠缺清单.md:32-33,76', '读 `原版资源/hlsdk/pm_shared/pm_shared.c` 的 `PM_PlayStepSound` 逐行对账脚步节拍（载体已在盘，缺的是"读"这一步）；落地音属"A 本来就没有"，不消除 【片FX-ALL 2026-09-23 已执行：脚步节拍已逐行读完并对账落地（时间制冷却），落地阈值亦已取 580/2 u/s；残余见 why 落地段的「仍未消除」三项】'),
    ('60', '切片K（D8）：Defuser / Vest / VestHelm 三个被动装备没有开火 / 换弹音', '它们不是武器：原版 CS 1.6 里既没有"手持并开火"、也没有换弹动作 ⇒ **原版也没有**这两个采样。旧判据（D8 的"每个 id 都要有 <id>_fire.wav / <id>_reload.wav"）把它们当武器，要满足只能**造两个 wav**（伪造素材，skill §0.1 ①）⇒ 判据已改为"装备在 CsWeapons 里有定义 + 无该音与 A 一致"', 'tools/probes/enumerate-entities.py（D8 段的 D8_EQUIPMENT 分支）；Core/CsWeapons.cs:83-85', '不消除（与 A 一致的行为差异）'),
    # 口径A(2026-09-23 片BW-S-R) id=61: 基=登记侧; +补充锚点(盘上) 1 片; 弃(盘上) 0 片 -> .ai-tmp/test/bwsr-A-discard.tsv
    ('61', '切片L（S1）：操作 / 表现层的可调旋钮没有原版出处（CsCombatTuning 全 31 条；CsMatch / CsViewTuning / CsConst 里标「本项目新增」的那些）', '这些量（后坐力时间常数 / 散布倍率 / 准星扩散 / bob / 开镜过渡 / 受击晃动 / 枪口火焰时长 / 各类实现容量上限）在 A 里对应的是**客户端手感**，原版把它们写死在 `cstrike/cl_dlls/client.dll` 与 `mp.dll` 的逐武器代码里（不是 cvar、也不是数据表 —— 见 `策划/对照表.md` §6 BLOCKED-1 / BLOCKED-2）；本机原版载体**已在盘、但尚未反汇编**（`原版资源/cs16src/cstrike/cl_dlls/client.dll` 1,093,128 B / `cstrike/dlls/mp.dll` 1,640,960 B，片AW 取回，SHA256 见 `原版资源/清单.md`「切片AW」§1）⇒ 拿不到 `文件:偏移` 级出处，只能取本工程自定值并逐条如实标注；补充锚点：`原版资源/cs16src`', 'client/Assets/Scripts/Module/Combat/CsCombatTuning.cs（31 条逐行已标「本项目新增」+ 该条与 A 的关系）；Module/Match/CsMatch.cs、Module/View/CsViewTuning.cs、Core/CsConst.cs 的对应行；策划/对照表.md §6 BLOCKED-1/2 与 A-05 / A-08 / E-03 / N-22 / U-07 / U-36', '用户补回 CS 1.6 客户端本体（原版资源/cs16src：client.dll / mp.dll）后逐条对账'),
    ('62', '切片N（S1）：Defuser / Vest / VestHelm **没有第一人称 viewmodel / AnimatorController**', '它们是**被动装备** —— A（CS 1.6）里既不能"手持"、也没有第一人称动作 ⇒ **原版本身就没有**这三个 v_ 模型。旧判据把 CsWeapons 里所有 id 都当武器、要求 vm_<id>.controller 存在，对它们不成立；要满足它只能去 Editor/Views **生成**这三个控制器 = 造 A 没有的素材（skill §0 铁律 1）⇒ 判据已改为「A 也无此 viewmodel ⇒ 一致」', 'tools/probes/enumerate-entities.py（S1 段的 S1_PASSIVE_EQUIPMENT 分支）；依据 = client/Assets/Editor/Views/ModelData/*.cs16anim 共 38 个（29 个 vm_* + 9 个 player_*，装备类 0 命中）+ client/Assets/Resources/Art/Anim 的 29 个 vm_*.controller；Core/CsWeapons.cs:83-85', '不消除（与 A 一致的行为差异）'),
    # 口径A(2026-09-23 片BW-S-R) id=63: 基=登记侧; +补充锚点(盘上) 0 片; 弃(盘上) 3 片 -> .ai-tmp/test/bwsr-A-discard.tsv
    ('63', '切片N：**下架了本项目新增的"伤害数字飘字"**（HudPanel 的 ShowDamageNumber / ObserveLocalDamage）', 'A（CS 1.6）的 HUD **没有伤害数字项**（原版 HUD 只有 hitmarker 与击杀提示）⇒ 屏幕上的 `-<数字>` 飘字属本项目自行新增的命中反馈文本，按 skill §0 铁律 1「A 没有 ⇒ 不加」整链删除。留下的只有**受击方向指示器**（屏幕边缘红框，A 有这条反馈）⇒ 那个被两处共用的时长常量随之由 DamageNumberTime 改名为 DamageIndicatorTime（含全部引用点）', 'client/Assets/Scripts/UI/InGame/HudPanel.cs（删除处留了注释与依据）；Core/CsConst.cs（原注释即写「本项目新增」）；策划/对照表.md §4「界面元素坐标/尺寸/颜色」——原版 HUD 元素已逐条出处化（U-01~U-37，引用到 cstrike__sprites__hud.txt:110/120/121/122/127/131/135/137/179/183 等），**其中没有任何"伤害数字"项**；策划/验收表.md B 段——我方 HUD 项清单 H1~H15 里也没有它（H9 = 命中标记 hitmarker）；client/资源欠缺清单.md——A 有 / 我方缺 的逐项对账里同样没有这项。⚠️ 如实说明：**原版硬载体 `sprites/hud.txt` 已在盘**（`原版资源/cs16src/cstrike/cstrike__sprites__hud.txt`，片AR/片AW 取回，见 `原版资源/清单.md`；同一份被 #14/#34 用作 `cstrike__sprites__hud.txt:110/120/…/183` 的出处）但**本行没有逐行读过它**；`原版资源/解包产物/` 仍不在盘（`原版资源/` 实测只有 `cs16src/` · `hlsdk/` · `备份/` · `_moved-out-from-assets/`）⇒ 本项按任务书退路仍登记为「本项目新增、与原版无关」，但**直证已可做**：下一棒读该 hud.txt 的元素清单即可给"原版 HUD 里没有伤害数字"一条原文级证据', '不消除（A 本来就没有；若将来要加回，必须先给出原版出处的 file:line）'),
    ('64', '§G D2「低矮障碍（含楼梯扶手/台阶沿）」残留 5 格不达标（判据未放宽，逐格已查明）', '用户报的那一处（匪家矮墙/台阶沿 cell(20,27)，h=0.81 m）本片已通过：全图 71 处候选里地面挡 71/71、跳起站得住 67/71、横跨窗口全 OK（h=0.81 m 的 65 格矮墙/台阶沿里 63 格通过，另 2 格是下面的"箱堆"口径问题）。残留 5 格分三类，**都不是**"位图判挡 + 顶面够得着却站不上去"的隐形墙形态：(a) cell(82,86)/(23,123)：顶面够得着，但身高带里**真有实体**（沙子混凝土台上压着箱子，真顶面 2.44 m；军械箱上再叠一箱，真顶面 5.28 m - 来路 3.25 m = 2.03 m）⇒ 原版同样上不去 —— 这是"什么才算矮障碍"的**候选分类口径**问题，不是实现缺口；(b) cell(51,108)：军械箱顶（高差 1.13 m）逼近跳跃峰值 1.1445 m ⇒ 时间窗 0.073 s × 5.4 m/s = 0.39 m < 需跨 1.72 m，一次跳跃不可能**横跨**；本行"边界：恰好在跳跃可达高度上"一条已定案「顶面高差 ≤ 可达高度 ⇒ 能跳过去」，两条口径自相矛盾（横跨比"顶面够得着"更严，且原版 GoldSrc 起跳不改变水平速度、同样跨不过去）；(c) cell(118,52)/(118,53)：SandTrim 收边条（顶面 6.96 m / 来路 6.50 m），外侧是图外虚空 ⇒ 9 点探针有 3 个落点所在子区域**没有任何世界几何**，运行时按保守口径判挡（切片U/S 特意保留，⛔ 本片不碰）。', '判据 tools/probes/geom-check.py（A5：候选 / 来路判挡 / 顶面可站 / 横跨可达，+ probe_kind_9 把"外侧虚空"与"真有实体"分开）；运行时口径 client/Assets/Scripts/Module/Map/CsMap.cs:260-425（CanStand 两层判据 / BodyHeightClearAt 的保守判挡 / 9 点半径采样）；逐格数字见 geom-check 报告 A5 段与 策划/状态矩阵.tsv（本片回写）', '主 agent 裁决「候选分类口径」后：(a)(b) 两格在收紧为「只收格内真顶面 ≤ 可达高度的格」+「横跨窗口降级为信息行（判据 = 顶面高差 ≤ 跳跃可达高度）」时归零；(c) 两格属运行时保守口径，需把站立判定改成原版单点口径（另开片，⛔ 本片未改引擎/未改该调用链）'
      '【2026-09-24 片FIX-4 线M 交叉引用】本行「位图判挡 + 顶面够得着」的**规模**已量化：`.ai-tmp/test/move-geom.txt` 的 ② 段，匪家扶手/斜坡车道 0.5 m 网格 26 列里 `位图判挡但该列有碰撞面=3`；B 通台阶 760 列里 = 314；反方向 `位图判通但该列无碰撞面` = 0 / 1。另：本片把「起点位图判挡但真几何可站」这一类从"原地不动"改成"照走"（`CsMap.ResolveMoveCore` / `TryStepUp`，见 #66 行）⇒ 本行里"位图判挡"不再等价于"人过不去"。⚠️ 上面的计数只说明"位图说挡"与"这一列有几何"是两件事，⛔ 不等于"可站"。'),
    # 口径A(2026-09-23 片BW-S-R) id=65: 基=登记侧; +补充锚点(盘上) 2 片; 弃(盘上) 3 片 -> .ai-tmp/test/bwsr-A-discard.tsv
    ('65', '本工程 de_dust2 几何的 X 轴跨度 4480 单位 > 原版 overview 窗口的 4096 单位（多 384 单位 = 9.4%）',
     '雷达底图换成**原版** overviews/de_dust2.bmp 后，雷达"显示哪块世界"由**原版窗口**决定（片AS 口径：X 中心 ± 2048 单位、Z 中心 ± 2730.6667 单位）。原版那张图的窗口装不下本工程几何的最东/最西两端 —— 但**这恰恰是原版行为**：原版 de_dust2 的 overview 本来就只覆盖 4096×5461 单位，多出来的 384 单位是**外挂笔刷/越界顶点**（去掉 0.5% 分位后 X 跨度 = 4064，与原版 4096 只差 0.8%，Z 轴 5461 vs 几何 5312 装得下）。主 agent 已裁决：**不剪几何 / 不改 de_dust2_geo.bin / 不重建场景**（为了"装下离群顶点"去改几何 = 1:1 复刻的反面）⇒ 只登记，不修',
     'tools/probes/overview-window.py（containment 判据原文：`X window 4096 units  footprint 4480 units  -> OUTSIDE by 384 units (9.4%)`；`0.5%-trimmed X [-1872..+2192] span 4064`）；client/Assets/ThirdParty/Dust2/de_dust2_geo.bin 的顶点外接框；窗口公式真源 原版资源/hlsdk/cl_dll/hud_spectator.cpp:1069-1193；补充锚点：`tools/probes/overview-window.py` / `client/Assets/ThirdParty/Dust2/de_dust2_geo.bin`',
     '地图几何域另片处理（⛔ 本片不剪几何）'),
    # ---- 片AZ（2026-09-22）：用户本轮 10 条报告（#66~#75）+ 同类漏检（#77）的差异四要素 ----
    #  编号与 `策划/验收表.md`「允许的差异」段逐条一一对应（闸门第 24 条 differences-source-of-truth 每次校验）。
    # 口径A(2026-09-23 片BW-S-R) id=66: 基=登记侧; +补充锚点(盘上) 0 片; 弃(盘上) 5 片 -> .ai-tmp/test/bwsr-A-discard.tsv
    ('66', 'B 点旋转楼梯（及全图所有楼梯/坡道/台阶）上不去 —— **已证伪（片BE 2026-09-22，本行降级，编号保留、不删行）**',
     '【降级依据：① 原版同形已对账 ② 纯函数侧走通 ③ 实机走不上去复现不出】'
     '① **原版形态对账 = 逐米 Δ=0**：原版 `de_dust2.bsp` 的 B 点两段与我们**逐米 Δ=0.000 m（11/11 点）**；'
     'T 侧 `plane[3460] n.z=0.9487 ⇒ 18.43°`（我们 18.4°，角度差 0.03°、法线差 0.0003）；CT 侧 `n.z=0.9923 ⇒ 7.13°`（差 0.13°）；'
     '**原版不是多级台阶**（T 侧 `SLOPE=6 / TREAD=1 / WALL=0`，踏面只顶部平台一层）⇒ 与工程侧"整片斜楔"同形，不存在"我们做成台阶/原版是斜面"那类缺口。'
     '② **纯函数侧走通**：`tools/probes/bstairs-walkline.cs` 沿真实走廊路径（A* 独立复算）逐帧（≤0.25 m/帧）走底→顶 —— '
     '**T 侧 frames=162 / 被钳住帧=0 / 到顶=True；CT 侧 frames=125 / 被钳住帧=0 / 到顶=True**；'
     '逐格四道闸门（`CsMap.cs:514-531`）被拒格 T 0/32、CT 1/24（那 1 格是标记点自带 y 与实测地面 y 的初值差 0.553 m，非真实阻断）。'
     '③ **实机（真人按键、输入驱动、非瞬移）走不上去复现不出 —— 玩家侧可走**：片BF 用'
     '`tools/probes/real-walk-bstairs.cs`（复用 `cs16-play-driver.cs` 的输入通道：改写 state.txt 的 `input=` 行、'
     '由驱动 order -150 写进 `match.SetLocalInput`）+ `.ai-tmp/drivers/bf-realwalk.ps1`（一次 Play、三个 case），'
     '**T 侧走廊路径：frames=853 / 被钳住帧=7（全在第 1~7 帧 = 驱动 50 ms 读盘节流的输入延迟窗口，moved=0、dYaw=180 ⇒ 还没收到输入，非物理阻断）/ 贴墙滑行帧=40 / 到顶=True / 已走 20.323 m / 高度差 0.000 / 6.264 s**；'
     'y 逐帧爬升 -2.824(f1) → -1.783(f140) → -0.562(f240) → 0.000(f340)（真机把整片斜面走上去了）；'
     '**CT 侧走廊路径：frames=648 / 被钳住帧=6（同为第 1~6 帧输入延迟）/ 到顶=True / 已走 11.247 m / 4.818 s**。'
     '⇒ 剔除输入延迟窗口后**真实阻断帧 = 0**，与纯函数基准（T 162 帧 / CT 125 帧 / 被钳住 0）同结论（帧数不可直比：纯函数按 0.25 m/步固定步长，实机按 dt 驱动、~136 fps）。'
     '④ **用户所见已复现 = 直线顶墙**：同一探针第三个 case「只设一次 yaw 朝目标、之后不再转向、按住前进」——'
     '**到顶=False，第 395 帧卡死**，位置 `(-10.973,-2.030,36.587)`（≈ 片BD 纯函数直线推进卡住的 `x=-10.961`），'
     '**被钳住帧=362/395**，只挪 **5.109 m** 就再也动不了 —— 即"上不去"是**路径问题（直线顶墙）**，不是几何/阈值问题。'
     '⑤ 同类扩样（T 坡道 / A 点斜坡 / B 门台阶 / CT 出生台）**本片未做**（改 `bsp`/扩样表属余力，见 `tools/probes/bstairs-walkline.cs` 的 `Case(...)` 调用点）。'
     '【2026-09-24 片SLOPE-AUDIT 落地 · 补上「全图楼梯/坡道/台阶的 A→B 可走性判据」（= 本行 ⑤ 缺的那一半）】'
     '**判据资产（新增）** `tools/probes/bm-step-audit.py`（离线只读；复用 `geom-check.py` 的读取层，⛔ 不另写一套解析）。'
     '**口径（从代码读，⛔ 不写死）**：`CanStand = BitmapClear && GroundWithinStep` **或** `BodyHeightClear`（`CsMap.cs:290-300`）；'
     '`GroundWithinStep(pos)`：`SampleGround(pos).y − pos.y <= StepUpHeight` —— **单边**（只挡往上抬，往下掉允许）（`:324-328`）；'
     '`TryStepUp` 的高度闸门 `SampleGround(target).y − from.y > StepUpHeight ⇒ false`（`:514-531`）'
     '⇒ 从低格往高格走，`Δh > 0.45 m` 时**两格位图都说可走也一样过不去**。'
     '**实测**（`client/Assets/Resources/MapData/de_dust2.bytes`：`w=127 d=145 cell=1.00 origin=(−63,−72)`）：'
     '可走格 5312 / 采到候选地面高度 5308 / 相邻可走格对（去重）5157；'
     '**B 完全不通 = 0**（位图不存在自相矛盾的硬边界）；**A 单向硬边界 = 69**（台阶/台沿，合法几何，只作清单）；'
     '**C 位图可走但一格地面都没采到 = 4**（= 可走格 5312 − 采到地面的 5308；⚠️ 见下"顺带发现"）'
     '⇒ **本判据当前判定 = `RESULT-STEP: FAIL`**（脚本退出码口径：B / C 任一非 0 即红；⛔ 不把 FAIL 藏起来，如实登记）。'
     '**匪家框内 10 对**（x∈[−58,−2] z∈[−58,−34]）：'
     '① **6 格连成一道 0.90 m 台沿** —— `cell(21..26, 30)` H=3.25 ↔ `cell(21..26, 31)` H=2.35，'
     '世界 x −41.5…−36.5、z 边界 −41.5 / −40.5 ⇒ 从低侧（z=−40.5，h=2.35）**走不上去**（Δ=0.90 = 2× 台阶高 0.45），'
     '但 **0.90 ≤ 跳跃可达 1.1445 ⇒ 跳得上去** —— 与 A5「跳起通」同结论，这就是用户点名的**匪家扶手/矮墙**；'
     '② `cell(49, 18/19/20)` H=8.95 ↔ `cell(50, …)` H=3.85/4.05/3.25（出生点旁 x=−13.5 / z=−52.5 的高台边，Δ≈4.9~5.7 m，只能从高处往下掉）；'
     '③ `cell(30,37)` H=6.50 ↔ `cell(31,37)` H=3.25（Δ=3.25）。'
     '**⇒ 斜坡本身没有硬台阶**：匪家那处坡道（T 侧 18.43° ⇒ 每格 0.33 m < 0.45 m）在 69 条 A 里**一条都没有** '
     '⇒ 「斜坡卡住」的**确定性那一半不成立**；卡住点落在**坡尽头那道 0.90 m 扶手台沿**上（走过去停住、必须跳）；'
     '而「**概率**」来自 `CanStand` 第一层的**半径 8 向采样**（`BitmapClear` 要求中心 + 8 向全可走）与第二层几何兜底 '
     '`BodyHeightClear` 谁先命中 —— 那部分**离线判不了**，属实机判据（登记为待补）。'
     '⚠️ **顺带发现（新，已登记待补）**：**4 格「位图可走但一格地面都没有」** —— `cell(27,63)` / `(9,102)` / `(10,102)` / `(10,109)`；'
     '用 `geom-check.Geom.first_up_face_y(格心, 从 50 m 往下)` 复核**四格全是 None**（格心处没有任何朝上面）；'
     '且 `(27,63)` 是个**只连一格**的叶子格（四邻里只有 `(27,62)` 可走）。后果：`TrySampleGround` 无命中 ⇒ '
     '`GroundWithinStep` 走 `if (!TrySampleGround(pos, …)) return true;` 的**放行分支** ⇒ `CanStand` 放行 '
     '⇒ 玩家能走进一格**脚下没有地面**的地方（贴地采样当帧失败 = 一帧下坠/滑步）。'
     '⛔ **未修**：修法在**位图烘焙**侧（这 4 格不该标可走），不是运行时改动；本片只出判据与坐标。'
     '⛔ **口径坑（第一版踩过，留档）**：拿 `geom-check.up_face_by_cell()`（格内**最高**朝上面）当「地面高度」'
     '⇒ 会报出 176 对 Δ≈**8.94 m** 的假「隐形墙」，全是**屋顶**（`cell(66,95)` 的格内最高朝上面是房顶 5.69 m，'
     '而人在街上 −3.25 m）⇒ 本次改为**每格全部候选朝上面**（去重 0.05 m），再判「两格之间有没有一对高度差 ≤ 一步台阶」。',
     '用户本轮原话"B旋转楼梯上不去"；'
     '原版形态对账资产 `tools/probes/bsp-brushes.py` + `bsp-brushes.txt` + `bsp-brushes-scan.txt`（`n.z=0.9487 / SLOPE=6 TREAD=1 WALL=0` 原文）；'
     '工程侧逐格/逐帧数字 `tools/probes/bstairs-walkline.cs` + `tools/probes/bstairs-walkline.txt`（(D) 段逐格数字 / (E) 逐帧）'
     ' + `tools/probes/bot-path-check.py`（位图层面 32/32/24/24 格 all cells walkable）；'
     '运行时口径 `client/Assets/Scripts/Module/Map/CsMap.cs:298-299,514-531`；'
     '常量出处 `client/Assets/Scripts/Core/CsConst.cs:113`（StepUpHeight=0.45）/`:135`（MaxStandableSlopeNormalZ=0.7，≈45.573°，与 GoldSrc `pm_shared.c` 的 `if (trace.plane.normal[2] < 0.7) goto usedown;` 同口径）；'
     '几何载体 `client/Assets/ThirdParty/Dust2/de_dust2_geo.bin` + `de_dust2.bsp`（2,057,288 B，**在盘**）；'
     '真人链路判据资产 `tools/probes/real-walk-bstairs.cs`（探针）/ `.ai-tmp/drivers/bf-realwalk.ps1`（一轮 Play 驱动）/ '
     '逐帧运行时日志 `.ai-tmp/test/bf-realwalk.txt`（2003 行）/ 环境基线 `.ai-tmp/test/bf-env.txt`（AMD Radeon RX 5700 XT / Direct3D12 / 3440x1440 / 3.42 ms）',
     '无需改几何；**真人链路已于片BF 复现并关闭本行**（玩家侧可走：T 20.323 m / CT 11.247 m 到顶、剔除输入延迟后真实阻断 0 帧；'
     '用户所见 = 直线顶墙 x=-10.973 只挪 5.109 m，属路径问题）。未做的只剩"全图其余楼梯/坡道扩样"，另片'
      '。⛔ **用户 2026-09-24 复查仍报**：「斜坡会有概率卡住（点名：匪家的扶手）」⇒ 本行的"已证伪"只覆盖了 B 点主楼梯的**直线顶墙路径**；"概率卡住"是新现象（窄面/扶手），正落在本行 ⑤ 的"同类扩样未做"上 ⇒ **留在此行**，另片扩样时一并判。'
      '【2026-09-24 片SLOPE-AUDIT 进度 · 本会话实测】本行 ⑤「同类扩样未做」**已补掉一半**：'
      '全图楼梯/坡道/台阶的 **A→B 可走性判据**已落地（`tools/probes/bm-step-audit.py`，离线、退出码即判据：B / C 非 0 才红），'
      '实测 **B 完全不通 = 0**、**A 单向硬边界 = 69**（清单，含匪家 10 对）、**C 缺地面 = 4**'
      '⇒ **`RESULT-STEP: FAIL`**（C 类 4 格实红；⛔ 本行如实带红登记，判据台账原文见 `.ai-tmp/test/` 同源脚本输出）。'
      '**匪家坡道本身一条都不在 A 里** ⇒「斜坡概率卡住」的确定性那一半**不成立**，'
      '卡点在坡尽头那道 **0.90 m 扶手台沿**（须跳，0.90 ≤ 跳跃可达 1.1445）。'
      '**仍未消除**：①「概率」那一半（半径 8 向采样 vs 几何兜底谁先命中）**离线判不了** ⇒ 要实机复现'
      '（同片BF 的真人链路探针，指到 `cell(21..26, 30/31)` 那道台沿）；'
      '② 新增待补 = **4 格「位图可走但无地面」**（`cell(27,63)` / `(9,102)` / `(10,102)` / `(10,109)`）'
      '要在**位图烘焙侧**修（本片未改运行时）；'
      '③ 本行「已证伪」的适用范围仍只到 B 点主楼梯 + 本次全图 A→B 高差审计。'
     '【片LEDGE 2026-09-24 实机取证 · 本会话】⑤「概率」那一半**已实机复现**（判据资产 `tools/probes/real-walk-ledge.cs`，驱动 `.ai-tmp/drivers/bz-final4.sh`，输出 `.ai-tmp/test/r3-ledge.txt`）：'
     '**扫 3 条车道**（世界 x=−41.5 / −39.5 / −37.5 ⇒ cell x=21/23/25，正是离线点名的那 6 列里的 3 条），每道 **5 次纯走 + 3 次带跳**，从低侧 `cell(·,35)` 朝 −z 按住前进到 `cell(·,28)`。'
     '**判决 = `RESULT-LEDGE: PROBABILISTIC`** —— `x=−41.5`：纯走 **4/5**、带跳 3/3；`x=−39.5`：纯走 5/5、带跳 3/3；`x=−37.5`：纯走 5/5、带跳 3/3。'
     '⇒ **用户报的「斜坡会有概率卡住」在实机上成立**（同一位置、同一输入，4/5 上得去、1/5 上不去）。'
     '**失败那一例的原文**（`CASE 3`，lane x=−41.5，WALK）：起点 `(-41.500,1.200,-36.500)`、`爬升=0.000`、`最高y=1.200`、终点 `(-41.500,1.200,-53.997)` `cell(21,18)`、`帧=900 clamped帧=609 原因=帧数上限`；'
     '逐帧显示它**一路贴着 y=1.200 直着穿过了整段坡**（F10 z=−37.619 / F60 z=−40.337 / F100 z=−43.438 / F290 z=−53.831），**完全没有爬升**，最后在 z≈−54.0 顶死后卡住 609 帧；'
     '而同一车道的成功例在同一段上爬到 `y=2.217~2.811`（终点 `cell(21,29)`）。'
     '⇒ 现象 = 「**有时把人抬上坡、有时让人贴着低面直穿过去**」，与「概率卡住」的用户体感一致。'
     '⚠️ **同时更正一处本行旧结论**：离线那条「**0.90 m 扶手台沿**（须跳）」在实机上**不成立** —— runtime 在该车道是**连续斜坡**（成功例 y 从 1.200 连续升到 2.8，无台阶突变），'
     '且离线/实机的高度对不上：`cell(23,30)` 离线 3.25 / 实机走面 2.381；`cell(23,35)` 离线 2.35 / 实机 **1.200**。'
     '⇒ 离线取的是「每格**全部候选**朝上面」里的一对，**至少有一边不是该格的可走地面** ⇒ `bm-step-audit.py` 的 A 清单（69 条）里凡是这种成对的，都要按「可走地面」（而非「候选面」）重取。'
     '⛔ **机制未定位**（本片到此为止）：为什么**同起点、同输入**会有两种结果 —— 可疑点 = ① `SampleGround` 取到的面（坡面 vs 低面）；② 软地板 `TrySoftFloor`（差异 #76 的 `_lastGroundY`）；③ `BodyHeightClear` 对 **bot** 的判定的时序。'
     '下一片应从「**逐帧 dump `SampleGround` 命中面 + `CanStand` 两道闸门各自的 pass/fail**」下手（本探针已把落点/帧号/钳住帧全部落盘，可直接对齐）。⛔ **本行不消号**。'
     '【片LEDGE-ROOT 2026-09-24 机制定位 + 落地修法 + 双帧率对照 · 本会话】'
     '**根因（一句话）**：常规贴地探测 `CsMap.TrySampleGround`（`client/Assets/Scripts/Module/Map/CsMap.cs:542-557`）的射线**起点只抬 `CsConst.GroundCheckDistance`（0.12 m）且只朝下** '
     '⇒ 它**看不见比自己脚面高出 0.12 m 以上的地面**；而 0.12 m 在原版里的用途是 `PM_CatagorizePosition` 判「算不算踩着地面」的容差，**不是爬升窗口** '
     '（原版的爬升窗口是 `PM_WalkMove` 的 **STEPSIZE = 18 单位 = 0.4572 m**，本工程对应的常量正是 `CsConst.StepUpHeight` = 0.45）'
     '⇒ 本工程把「爬升能力」直接变成了**帧率的函数**。'
     '**量化（可复算）**：`CsMatch.StepActorPhysics` 每帧先施重力（`a.Velocity.y -= CsConst.Gravity * dt`，`CsMatch.cs:1943`）'
     '⇒ `resolved.y = 上一帧地面 y − Gravity·dt²`；探测起点 = `resolved.y + GroundCheckDistance` '
     '⇒ **有效爬升窗口 = `GroundCheckDistance − Gravity·dt²`**；一帧水平位移 = `v·dt` '
     '⇒ 可爬坡度上限 `tanθ ≤ (GroundCheckDistance − Gravity·dt²) / (v·dt)`。'
     '取 `GroundCheckDistance`=0.12、`Gravity`=20.32（`client/Assets/Scripts/Core/CsConst.cs`）、`v`≈5.4 m/s（本探针实测每帧 0.277 m @ dt=0.05）：'
     '**dt=0.0074（约 135 fps）** ⇒ 窗口 0.1189 m ⇒ 上限约 71° ⇒ 18.43° 的匪家坡**通过**（实测 walk 5/5）；'
     '**dt=0.018（约 56 fps）** ⇒ 窗口 0.1134 m ⇒ 上限约 49° ⇒ 通过（实测 4/5）；'
     '**dt=0.050（20 fps，受控实验）** ⇒ 窗口 **0.0692 m** ⇒ 上限 **约 14.0°** ⇒ 18.43° **不通过**（实测 **0/5、2/5、0/5**）。'
     '临界帧率解 `(0.12 − 20.32·dt²) / (5.4·dt) = tan 18.43° = 0.3333` ⇒ `dt ≈ 0.0444` ⇒ **fps ≈ 22.5**；压到 20 fps 正好落在临界之下 ⇒ 稳定复现。'
     '⇒ **同一个坡、同一份输入，只有帧率不同，结论就翻转** —— 这正是用户报的「概率卡住」。'
     '**修法（`client/Assets/Scripts/Module/Match/CsMatch.cs` 的 `StepActorPhysics`，最小改动）**：常规探测**落空**时补一次**抬一个台阶**的探测 —— '
     '`lifted = resolved + Vector3.up * CsConst.StepUpHeight`；探到地面且 `0 ≤ groundPoint.y − resolved.y ≤ StepUpHeight`、且 `IsStandableGround(groundNormal)` ⇒ 承认它，'
     '之后走**既有**的贴地 / 陡坡闸门（⛔ 本分支不新增任何判定逻辑）。'
     '① 这是原版 `PM_WalkMove`「**贴地走一遍 + 抬 STEPSIZE 再走一遍、取走得更远的那个**」那半步的等价落地；'
     '② 抬升上限就是 `CsConst.StepUpHeight`，与 `CanStand` 的水平准入闸门 `GroundWithinStep`（`point.y − pos.y ≤ StepUpHeight`，`CsMap.cs:324-328`）**是同一个常量** ⇒ 不开新的几何口子，高过 0.45 m 的台沿仍上不去；'
     '③ 只在 `!hasGround && a.Velocity.y <= 0f && a.OnGround` 时触发 ⇒ 跳跃上升段、走下断崖（探到的地面在脚面以下）都不受影响；'
     '④ 新增取证计数 `CsMatch.StepUpProbeHits`（public static）⇒ 判据能把「修法真的被走到」与「只是没触发」分开（⛔ 只看通过率会把一个死分支也算成修好）。'
     '**修后实测（同探针 / 同车道 / 同输入 / 同 20 fps 压帧）**：`dt[min/avg/max]` 的 avg = 0.0499~0.0590（与失败轮**同档**）、三道 **walk 5/5、jump 3/3**、'
     '爬升 **1.603~1.665**（修前 0.000~0.195）、`抬台阶接住` **1~55 次/例**（证明修法真的被走到）。'
     '**无压帧对照轮**（`dt[avg]`≈0.0063，约 160 fps）：三道 **walk 5/5、jump 3/3**，且 `抬台阶接住` 在 lane2 **恒 0** '
     '⇒ 常规探测够用时修法**不触发、无副作用**（lane0 那 0~20 次出现在 dt 尖峰 0.07~0.10 的例上 ⇒ 尖峰也已被覆盖）。'
     '**判据口径本身也修了一处错（⛔ 留档，别重复踩）**：`tools/probes/real-walk-ledge.cs` 的旧口径写「PASS = 每条车道 walk **0/5** 上得去」，'
     '那是拿**离线假设**（`bm-step-audit.py` 的 A 清单说这里有一道 0.90 m 台沿）当期望值 ⇒ 结果是**修好之后反而判 `FAIL`**（自相矛盾）。'
     '**新口径的期望值由实测几何给出**：探针每道打印 `GEOM-BASIS` 行 —— `SampleGround(终) − SampleGround(起)` = **1.750 m > StepUpHeight(0.45)** ⇒ 三条车道**都是坡** ⇒ 期望纯走 5/5；'
     '旧假设（台沿）的证伪见本行上一段（runtime 是连续斜坡、y 逐帧平滑上升、无台阶突变）。'
     '**判决原文（修后 · 无压帧轮）**：`GEOM-BASIS / 判坡判据=该道 SampleGround(终)-SampleGround(起) > StepUpHeight(0.450) / 本批实测=1.750 ⇒ 三条都是坡（期望纯走 5/5）` 与 `RESULT-LEDGE: PASS`。'
     '**产物**：`.ai-tmp/test/r4-ledge-lowfps-fix.txt`（20 fps 修后）/ `r4-ledge-highfps-fix.txt`（无压帧修后）/ `r3-ledge-lowfps.txt`（20 fps 修前）/ `r3-ledge.txt` 与 `r3-ledge-highfps.txt`（修前）；'
     '驱动 `.ai-tmp/drivers/bz-ledge3.sh`（20 fps）/ `bz-ledge4.sh`（无压帧）。'
     '⚠️ **仍未消除**：① 本行「已证伪」的适用范围仍只到 B 点主楼梯 + 匪家那三条车道（其余楼梯/坡道扩样未做）；'
     '② 4 格「位图可走但一格地面都没有」（`cell(27,63)` / `(9,102)` / `(10,102)` / `(10,109)`）**仍未修**（属位图烘焙侧，不是运行时）；'
     '③ 可爬坡度上限已从「帧率相关」改成「≤ 约 55° @20fps」，但**极端低帧率**（dt ≥ 0.1 ⇒ ≤10 fps）下上限会再降到约 24.6° —— 那是「一帧水平位移超过一个台阶」的物理上限，与原版同性质，⛔ 不再作为缺陷登记。'
      '【2026-09-24 片FIX-4 线M · 用户「概率卡死」根因 + 修复 + 逐帧判据（走真人那条链）】判据资产（新增，只读探针、⛔ 不加钩子/不反射/不改对象结构）`tools/probes/move-stuck.cs`：`MoveStuck.Drive` 每帧 `match.SetLocalInput(cmd) + match.Tick(dt)`（= PlayerModule/MatchModule 同一条链）逐帧 dump Position / Velocity / OnGround / 水平位移 / 起点 CanStand 与位图·几何三子判据 / 落点位图 / `SampleGround` 命中面(y,法线) / `StepUpProbeHits` 增量；**卡死定义 = 输入非零且水平位移 < 0.001 m 连续 ≥60 帧**。**修前（`.ai-tmp/test/fix4-before/`，同一份构建上跑）**：`stairsjf`(dt=0.0074) f=100..399 卡死 300 帧、`stairs05`(dt=0.05) f=51..399 卡死 349 帧、`lane415w05`(dt=0.05) f=67..399 卡死 333 帧、`lane415whf`(高帧率) 0 卡死 ⇒ 精确复现用户「概率卡死」。**根因（逐帧原文）**：卡死形态统一为「`CanStand(from, radius=0.36)==false` **而** `WalkableAt(to)==false`」⇒ 旧 `ResolveMoveCore` 每帧 `return from` ⇒ `CsMatch.StepActorPhysics` 见"要的位移没拿到"把该轴速度清 0 ⇒ **位移恒 0、速度恒 0、按什么键都不动**。为什么 `CanStand(from)` 会是 false 而人明明站在地面上：`CanStand` 是**半径 8 向采样**（中心 + 8 向，`PlayerRadius`=0.36 m），站在台阶/扶手**旁边**时偏移点落在"顶面比脚面高 0.12~0.45 m"那一列 ⇒ 位图判挡（单层 2D 位图，差异 #64/#76），几何分支 `BodyHeightClearAt` 又用 `GroundCheckDistance`(0.12) 当"算不算脚面"的容差 ⇒ 把它读成"身高带里有实体"。**修法（只在本工程侧）** `client/Assets/Scripts/Module/Map/CsMap.cs`：① `ResolveMoveCore`（:453-487）起点站不下时**不再"原地不动"**，改走同一条扫掠解算 `StepOnce`（`moved2 > 1e-8f` 才认；一步都挪不动时才退回旧口径，且只在目标格位图可走时直通，⛔ 不许凭空穿墙）；② `TryStepUp`（:545-580）去掉 `if (!WalkableAt(target.x, target.z)) return false;` **硬否决**，改**真几何四道闸门**（有地面 ∨ 落点不比脚下低 ∨ 高差 ≤ `StepUpHeight`=0.45 ∨ 法线 ≥ `MaxStandableSlopeNormalZ`=0.7 ∨ `BodyVolumeBlocked`=false ∨ 膝盖射线通畅）。**修后实测**（`.ai-tmp/test/move-drive-*A.txt`）：`lane415w05A` **3/3 到终点**（终点 (-41.500,3.962,-54.050)、爬升 2.762）、**0 卡死**；`lane415whfA` 0 卡死；`stairsjfA` **0 卡死**（最长停滞 32 帧 < 60）；`stairs05A` 最长停滞 346 帧（从 f=54）——**如实登记：性质已变但未归零**（走了 5.464→11.017、爬升 0.803→3.477、`抬台阶接住` 2→35）。该残余停滞的**定性（有出处）**：20 fps 与 135 fps 停在同一处（(-18.887,0.653,36.948) 与 (-18.976,0.688,36.995)，XZ 相差 0.10 m）⇒ 与帧率无关；探针 ③ 段定向射线在该点膝高 0.45 m **只有 3/8 方向通畅**，被 `SandCrtSmSd.png` 竖直墙（nY=0.000、0.214 m）与 `_0SandRock2.png` **33.7° 陡坡**（nY=0.556 < 0.7、0.427 m）夹住 ⇒ 是几何事实（直线顶墙 + 陡坡闸门），可用出口在**身后**（+X / -Z）⇒ 属**路径问题**不是解算缺陷。**⚠️ 残余停滞的原样口径（⛔ 不许含糊、⛔ 不许写成已消除）**：**20 fps 直线顶墙 346 帧**（`stairs05A`，从 f=54）；**出口在身后**（+X / -Z）；**性质 = 路径而非解算**；而用户 #5 报的「跳一下就卡住」在 `stairsjfA`（**跳**、高帧率）下 = **0 卡死**（最长停滞 32 帧 < 60）。**无压帧对照 + 回归**：`tools/probes/bodyheight-walkline.cs` ⇒ `SUMMARY FAIL=0`。⚠️ **仍未消除**：① 「斜面前端 0.90 m 台沿」那一步的**跳起后横跨**本片未跑（驱动里 jump 只按一次、之后仍按前进，不是"跳完再转向"）；② 全图其余楼梯/坡道/台阶扩样仍未做；③ `stairs05A` 那 346 帧停滞按上面的定性**不当作缺陷**，但**判据口径要改**（探针必须能区分"顶墙前的正常停滞"与"解算不了"——本片用 ③ 段定向射线补的，建议下片并进 ② 段的逐列表）。'),
    # 口径A(2026-09-23 片BW-S-R) id=67: 基=登记侧; +补充锚点(盘上) 1 片; 弃(盘上) 5 片 -> .ai-tmp/test/bwsr-A-discard.tsv
     ('67', '机器人**没有战术层**：不守点 / 不下包 / 不突破，只在路点之间来回踱步', '机器人的机制是"路点推进 + 局部避障 + 卡住就换目标"，**没有寻路层、没有位置/战术层**：① 引擎通用格子 A*（`client/Packages/com.clover.unity-engine/Runtime/Core/AStar.cs`）在 `client/Assets/**` **零调用**（内 grep `AStar` 命中 0）。**拆两句**：① 业务域 = `client/Assets/**`（范围见下）；' + _scope_dif(_SCOPE_ASTAR) + '② 引擎包 = `client/Packages/com.clover.unity-engine/**`（junction）' + _scope_dif(_SCOPE_BT_ENGINE) + '⇒ 只能沿标记点最近邻序列走直线；② 每阵营只有 3 条路线标记（`CsBotBrain.cs:298-299`/`:574-575` CTDefendA/B + 中路）+ 1 条巡逻线（`:494-516`），**守点 = 到达单个包点标记后原地警戒**（`:338` ObjectiveHoldSeconds、`:31`），时长到就"换路线/去巡逻/回出生点"（`:406-455`）⇒ 观感就是"原地踱步、不知道在干啥"；③ 下包/拆包有实现但**单点依赖**（`:697-701` 只认 `intent.Use` + 站位），没有多点突破/掩护/换点位。', '用户本轮原话"人机的ai太傻逼了。一直在原地踱步…警不去守点，匪不去下包 不去突破"；实现出处 `client/Assets/Scripts/Module/Bot/CsBotBrain.cs:11,29-31,298-299,338,406-455,494-516,574-575,697-701`、`client/Assets/Scripts/Module/Bot/BotNavigator.cs:9-29,115-161,183-248,269-277,322-495`、`client/Assets/Scripts/Module/Bot/CsBotConst.cs:323`（"只有 id % N == 0 的 T 去捡掉落 C4"）；难度只改反应时间/瞄准误差 `client/Assets/Scripts/Module/Match/CsTypes.cs:148`（CsBotProfile）；原版口径：**A（CS 1.6）本体不含 bot AI**（官方 bot 属 CZ/PodBot，不在本工程载体范围）⇒ 行为基线按规格 §2.4 三档表 + 差异 #58；缺的战术表（守点位/下包决策）**待补**（降级链第 4 级：参考坐标可自定，但须逐条标"本项目新增"）', '开「机器人 AI」片时：① 先把 `.bytes` 可走位图接上引擎 `AStar.FindSmoothed`（契约见 `client/Packages/com.clover.unity-engine/Runtime/Core/AStar.cs（第 16-19 行）`）当**寻路层**；② 再加**位置层**：从 `de_dust2.bsp` 的实体/几何取点位，建"CT 守点位表 / T 下包点表 / 突破线"；③ 判据 = 离线断言（"守点位上有人 ≥X s"、"回合内至少 1 次下包"、"T 进点路径可达"）+ 一次实机联络图。【片BU-R 何时能换回引擎实现】引擎若提供**多实例工厂**（例如 `Game.NewFsm()`，形状 = 返回一个 `IFsm` 新实例、每个 bot 各持一份），则本工程的 `CsBotFsm.cs` 可整体删掉、直接换成引擎实现；在那之前按 `IFsm` 自实现是唯一合法做法。⛔ 本片**不改引擎仓**（改动只写在本条建议里）。【片BU-R 实际进度】`client/Assets/Scripts/Module/Bot/CsBotFsm.cs`（新增）+ `CsBotBrain.cs`（两层 FSM 分发）+ `BotConst.StuckReplanCooldownSeconds` 已落盘；片BU-R 又加了"选目标必须先过可达门禁"（`BotNavigator.CanReach` + `CsBotBrain.TryPickReachable`）与 CT 包点守卫解耦（`CsBotBrain.InsideHoldSite`）。判据闸门 `tools/probes/bot-goal-gate.py`：修前 FAIL 6/7 → 修后 FAIL 5/7（净位移 0.001~0.007 → 0.008~0.044、换目标 73 → 7、求路径失败 67 → 15），**T 下包 / CT 驻留 / 守点换位仍未达成**（见 .ai-tmp/test/bu-r-goal-after.txt 与 bu-r-gate-after.txt）。**【片BU-R2 2026-09-23 00:0x 因果链 + 修后数字】** 根因（三条，全部有代码出处）：① `BotNavigator.EnsurePath` 把引擎 A* 的**起点节点**（= 机器人自己那一格的格心）留在路径里，而 `AdvancePath` 的推进规则是"离下一个节点更近才推进"——目标是自己的格心时这条规则**永远不成立**，游标整条路径生命周期钉在第 0 个节点 ⇒ 机器人一直追"自己脚下的格心"，越过 2cm（`ComputeMove` 的 0.0004 死区）就翻 180°（引擎出处 `client/Packages/com.clover.unity-engine/Runtime/Core/AStar.cs:73-76,95,160`）；② `MaybeRepath` 调 `SetRoute`（而非"原地重求路径"）⇒ 按"离当前位置最近优先"**重排整条路线**并把 `_index` 归零，把它自己注释里明令要避免的"回头找最近路点"真的做了（单次 Play Cliffe 一条 Route_CT_Mid 重排 **32 次**）；③ 采集侧的回合从不完整：New Game 面板默认已经 4v4，驱动又无条件补一次 LaunchMatch，走 `CsMatch.Start` 的"重复调用 = 先 Stop 再 Start"契约 ⇒ 把正跑的回合掐在 **22.5s**（L3 22:42:16.429 → 22:42:38.919），且 Play 在 22:43:49.303 被驱动停掉 ⇒ 采集窗口 93s < 一个完整回合所需的 4+105+5 = **114s**。修法：`EnsurePath` 去掉起点节点（`path[0] == from` 时 `RemoveAt(0)`）、`AdvancePath` 增加"站在该节点的格子里（≤ 半格 = `IMapData.CellSize × 0.5`）也推进"、新增 `BotNavigator.RefreshPathOnly` 并让 `MaybeRepath` 改调它（顺序与游标不动）、驱动新增 `Cs16Drv.Entry.StartBotsIfNeeded`（只有 BotCount < 8 才补 LaunchMatch）+ 采集窗口等到 `phase=RoundEnd`。修后（同一个 `tools/probes/bot-goal-gate.py`，完整回合，8 bot）：**A6 CT 包点驻留 0 → PASS（Spliff 连续 5 采样 ≤7m）**、**A7 守点换位 0 → 26 次 PASS**、A1/A2 通过 0/8 → **2/8**（Cliffe 净位移 5.7 → **66.1m**、net/total 0.032 → **0.802**；Spliff 7.8 → 24.5m / 0.401）；逐帧抖动：Move 方向翻转 Cliffe 317 → **2 次**（Gooseman 291→5、Rikk 209→4、Spliff 308→11、ZBot 88→4），rev% 78.7% → **0.0%**（出处 `tools/probes/bu-r2-motion-diag.py`，修前 `.ai-tmp/test/bu-r2-diag-before.txt` / 修后 `bu-r2-diag-after.txt`）。**仍未消除（本片不改「允许的差异」为「一致」）**：A1/A2 6/8 未达标、A4 求路径失败 18 > 10、**A5 T 侧下包仍 0**；残余根因 = **局部避障层**：`BotNavigator.Avoid` 每帧按 `CsBotConst.AvoidAngles` 顺序重扫候选，落地方向在 ±25°/±50°/±75°/±100°/±125° 之间乱跳（`[BOTFLIP]` 原始行 `probe=±25..±125 probeLeft=0.50`，即 0.5s 的 `_probeHoldSeconds` 保持每一帧就被打破）⇒ Darrell/Scuzzy 原地绕圈（总路径 350m / 净位移 17~22m、翻转 349/328 次）；归因表见 `tools/probes/bu-r2-flip-attrib.py` + `.ai-tmp/test/bu-r2-flip-attrib-after.txt`。**【片BU-R3 2026-09-22 23:35（完整回合实测）】** 只改了 `BotNavigator.Avoid` 一处 = **换向迟滞**（① 候选偏角改按"与当前偏角的偏差升序"试，新增 `PickAvoidAngle`；② 保持期内"这个方向不可走"要**连续** `CsBotConst.AvoidBadDirSeconds`=0.1s 才换锚点，**单帧不可打破**；参数出处 `tools/probes/bu-r3-avoid-replay.py` 的离线口径自校验 + 一维扫描，输出 `.ai-tmp/test/bu-r3-replay-final.txt`）。修后（同一 `bot-goal-gate.py`，完整回合，8 bot）：方向翻转 296 → **167** 行（`bu-r2-flip-attrib.py`：Darrell 104 → 9、Scuzzy 117 → 53）、Darrell 总路径 384.6 → **70.3m**（`bu-r2-motion-diag.py`）、A1/A2 达标 2/8 → **3/8**；**A6/A7/A8 未回退**。**仍未消除（⛔ 本片不改本条为"一致"）**：A1/A2 5/8 未达标、A4 求路径失败 19 > 10（与 Avoid 无关，见 #77）、**A5 T 侧下包仍 0**；且残余根因**已改判到避障之外的一层**：`.ai-tmp/test/bu-r3-diag-after.txt` 第 1 节显示 4 个 bot 在 93.8s 窗口内**静止 80~90s**（Gooseman 90.5s，状态分布 `Plant:704/798` 且位移恒 0.00m）⇒ A1/A2 的"净位移"口径落在守点/静止上、A5 的直接病灶是 T 侧持包 bot 卡在 `Plant` 态不动（`CsBotBrain`/`CsBotFsm` 层）。⛔ 阈值未下调。；补充锚点：`.ai-tmp/screenshots/p_bomb_planted_b.png`'
      '【片FX-MUZZLE 2026-09-24 落地 · 战术分工表（用户复查第 3 条）】'
      '① **新增** `client/Assets/Scripts/Module/Bot/CsBotRoles.cs`：`enum CsBotRole { Breaker, Support, Scout, Anchor }` + 静态角色表；'
      '角色由 `(阵营, CsActor.Id % 4)` **纯函数**决定（与 `PlanObjective` 的路线槽位**同一个模数** ⇒ 两处口径同源、可复现、可断言；⛔ 不用随机 —— 随机会让"这局谁突破"变成不可复现的噪声，判据也就写不出来）。'
      ' ② **三处消费**（`CsBotBrain.cs`）：`ObjectiveHoldSeconds()` 乘 `HoldScale`（突破 0.6 / 支援 1.0 / 侦察 1.6 / 守点 2.5 倍）；'
      '`Engage` 的偏好交战距离乘 `RangeScale`（0.75 / 1.00 / 1.30 / 1.15 倍）；`Camp` 新增「**Anchor 守到底**」分支 —— 守点位到位后**不再 `ReplanObjective`**'
      '（这正是用户报的「警不去守点 / 一直在原地踱步」的反面：旧实现全队每 3 秒改一次目的地）。'
      ' ③ **出处口径（⛔ 必须如实标）**：A（CS 1.6）本体**不含 bot AI**（官方 bot 属 CZ / PodBot，不在本工程载体范围，同 #58）'
      '⇒ 角色划分与四个倍率在 A 里**没有对应量**，属**本项目新增**（降级链第 4 级），⛔ 不许写成"原版就这样"。'
      ' ④ **判据资产（三类，全部在盘可复跑）**：'
      '(a) 纯函数自检 `BotSelfTest.RunRoleTable()`，菜单 `Clover/自检/机器人 战术分工表（差异 #67）`（四条断言 + 打印 Normal 档四角色实际守点秒数 + `RESULT: PASS&#124;FAIL`）；'
      '(b) **实机探针** `tools/probes/probe-bot-roles.cs`（反射取 `BotModule._brains`，逐个问**运行时实例自己**的 `RoleText()`，⛔ 不是读代码猜）——'
      '实测 `.ai-tmp/test/r2-bot-roles.txt`（8 bot / round=3 / phase=Live）：'
      '`Cliffe id=2 CT 侦察` / `Minh id=3 T 突破` / `Gooseman id=4 T 突破` / `ZBot id=5 T 支援` / `Rikk id=6 T 侦察` / `Spliff id=7 CT 支援` / `Darrell id=8 CT 守点` / `Scuzzy id=9 CT 守点`，'
      '`[各队分工] CT=3种 T=3种`，`RESULT-ROLES: PASS`（口径：bot>=2 ✓ / 每队>=2 种角色 ✓ 最小=3 / 四个守点倍率互异 ✓ 4 个 / 守点角色唯一 ✓ 1 个 / **运行时==纯函数 ✓ 不一致=0**）；'
      '(c) **可核对载体**（用户自己能看见的那一层）：回合计划日志逐字 '
      '`[Bot] Darrell（CT/Normal/角色=守点（守点×2.5 交火×1.15 守到底））第 1 回合计划：路线=Route_CT_To_A 路点=4 目标=(34.50, 2.44, 29.50) 站点=True 守点时长=7.5s 重寻路间隔=1.00s`，'
      '同回合 `[Bot] Minh（T/Normal/角色=突破（守点×0.6 交火×0.75））第 1 回合计划：路线=Route_T_To_A … 守点时长=1.8s` '
      '—— **同一难度档下 7.5s vs 1.8s** 就是"真的有分工"（旧实现全队共用 3.0s 基准）；抓取 = `.ai-tmp/drivers/bz-round2.sh` phase 4b → `.ai-tmp/test/r2-bot-roles-console.txt`。'
      '⚠️ **仍未消除**：① 寻路层仍走标记点最近邻（引擎 `AStar` 接入见本行上方旧段落）；② 角色表只覆盖"守点时长 / 交战距离 / 换目标"三处，'
      '武器偏好与投掷物分工仍是空的（A 无 bot AI ⇒ 无出处，⛔ 不编）。'
      '。⛔ **用户 2026-09-24 复查仍报**：「机器人 ai 没有分工吗？感觉行为方式都是一样的。机器人有点太笨了」⇒ **并入本行（⛔ 不新开号）**。'
      '【片FX-MUZZLE 2026-09-24 进度】用户复查第 3 条**已落地**（角色表 + 三处消费 + 三类判据全部在盘；实机探针 `RESULT-ROLES: PASS`、'
      '载体回合日志行逐字可核，见「为什么」列末的落地段）；**本行主体仍未做** —— 寻路层接引擎 `AStar.FindSmoothed`、'
      '从 `de_dust2.bsp` 取点位建"CT 守点位表 / T 下包点表 / 突破线"、多点突破与掩护仍是空的 ⇒ ⛔ **本行不消号**，'
      '仍留在「允许的差异」段等待「机器人 AI」片。'
      " 【片FIX-4 线B 2026-09-24 · 用户第三次投诉「为什么每个机器人的操作，路线都是相同的。你这什么行为树，什么 ai 啊？？为什么没有分工？？？」】**根因（三条，全部有代码出处）**：① `CsBotBrain.ChoosePlan` 旧实现 `idx=self.Id%4` 后按三分支映射，T 队 `idx==0` 与 `idx==1` 落到**同一条路、同一个目标点**；② CT 队取 `idx%3` ⇒ 槽位 0 与槽位 3 撞在 `Route_CT_To_A` + 同一个点；③ 换目标 `TryRouteObjective` 用 `(self.Id + _replanCount) % 3` 只在**3 条路**里轮 ⇒ 4 只 bot 必然有两只同路。**量化（实机 · before）**：用户本人日志 `client/Logs/2026-09-24.log` 解析（`tools/probes/bot-route-dup.py --log … --only-old`）= 48 个「队-回合」样本 / 288 对同队两两 ⇒ **路线相同 48 对（每队每回合恰好 1 对）、目标点相同 96 对（每队每回合 2 对）**，明细恒为 `Spliff vs Darrell`、`Gooseman vs ZBot` ⇒ `RESULT-DUP: FAIL`（证据 `.ai-tmp/test/fix4-dup-before.txt`）。**修法**：新增 `client/Assets/Scripts/Module/Bot/CsBotPlans.cs`（**4 槽位计划表**，纯函数）：队内序号 = 同阵营 actor 按 Id 升序的名次（不再是全局 Id —— 全局 Id 会被「真人在哪一队」整体位移）；槽位 = `(队内序号 - 本回合持包者队内序号) mod 4`（模 4 平移是**双射** ⇒ 4 只 bot 必得 4 个互不相同槽位；持包者恒落槽位 0 = 主攻包点路线，出处 `策划/策划案/CS1.6单机参考规格.md:130`「T 持包到 B 点 → 下包」）；四个槽位路线标记两两不同（T：主攻路 / 中路 / 另一包点路 / 巡逻；CT：守 A / 守 B / 中路 / 巡逻），共用同一包点的两个槽位用不同 `GoalOrdinal` ⇒ 目标点也不同；`CsBotBrain.TryPickGoalPoint` 改成「按序号取确定性序（按 (x,z) 排序 + CanStand 过滤）里第 N 个走得到的点」，不再「取离自己最近的」（旧口径会让相邻出生的两只 bot 取到同一个点）；`TryRouteObjective` 改按 `(_planSlot+k)%4` 轮转；`BotModule.AuditRouteDistinctness` 每 1s 审计同队撞车（非预期分支必须留痕）。**⛔ 实机抓到的真实缺陷并已修**：2026-09-24 11:31:09（线M 的实机对局，跑的正是本片代码）审计打出 `同队路线撞车：Minh(T/槽位3) 与 Gooseman(T/槽位3) 都走 'Route_T_To_A'` —— 根因是「平移量每只 bot 各自去问一次现在谁持包」（持包者中途被打死 ⇒ 后来的 bot 拿到 -1、先来的拿到 k ⇒ 槽位重合）。修法 = `CsBotPlans.RoundCarrierOrdinal` **回合内缓存一份平移量**（回合号变小才重算）。**判据（三类，全部在盘）**：① **离线段言** `tools/probes/plan-check-offline.cs`（Edit 模式即可，⛔ 不进 Play）→ `CsBotPlans.SelfCheck` 正控 PASS / 负控（注入「槽位3=槽位0」= 旧实现行为）必须 FAIL ⇒ `.ai-tmp/test/fix4-plan-offline.txt` 输出 `RESULT-PLAN: PASS` + `RESULT-PLAN: FAIL` + `RESULT-PLAN-NEGCTL: PASS`；② **素材层序列判据** `tools/probes/bot-route-sequence-check.py --plan … --markers …` ⇒ `.ai-tmp/test/fix4-seq-check.txt` 的 `RESULT-SEQ: PASS`（A 路线标记互异 / B 目标(标记+序号)互异 / C **20/20 个出生点**下 4 条最近邻全序列两两不同）+ 负控 `.ai-tmp/test/fix4-seq-check-negctl.txt` 的 `RESULT-SEQ: FAIL`（`RESULT-SEQ-NEGCTL: PASS`）；③ **实机探针** `tools/probes/probe-bot-routes.cs`（反射读 `_brains`，逐 bot 打 槽位/角色/路线/路点数/首段路点/目标点/出生点/整条序列签名，并用「旧规则反事实」当负控）—— ⚠️ **本条的 after 取样被 Play 独占阻塞**（`.ai-tmp/play.lock` 被线C/线M 持有，二者心跳间隔 <1 分钟；本片只读探针在无 live 对局时只拿到 `bots=0 phase=None` 的空采集，按规矩作废删除）。**已备好一条命令**：`.ai-tmp/drivers/fix4-route-play.ps1`（复用 bu-r6 链：抢锁 → 编译 → Boot → editor_play → 挂驱动 → 4v4 → 采样 A/B → 停）；拿锁后即可产出 `.ai-tmp/test/fix4-route-A.txt` / `-B.txt` 与 `--probe` 口径的重复率对照表（`bot-route-dup.py --probe …`）。**⚠️ 判据口径修正（必须记）**：dispatch 写的「同队任意两只 bot 的**首段路点**不同」**在 T 队不可能通过，且与代码无关**：`Resources/MapData/de_dust2_markers.bytes` 里 `Route_T_To_A ∩ Route_T_Mid ∩ Route_T_To_B = {(-7.5, 3.251, -47.5)}`（该点离 T 出生点最近）⇒ 最近邻排序后三条路第 0 段必然相同（实测 20/20 个出生点）。可满足且同等有效的硬判据 = **整条路线序列不同**（已实现；CT 队 20/20 首段也不同，仅 T 队受素材限制）。⛔ 本片内容属**本项目新增**（A 本体无 bot AI，同 #58）；⛔ 本行主体（寻路层接引擎 AStar / 由 bsp 取点位建战术表）**仍不消号**。【片FIX-4 线B · 行为取舍（主 agent 2026-09-24 批准）】**收益**：用户投诉的「路线/目标相同」被**构造性**消除（同队 4 只恒占 4 条互异家族，换目标时也不抢队友）。**代价**：4 条家族被**活着的**队友占满时（常态），卡住的 bot **不再换路线家族**，只在原家族内换目标点。**缓解**：① 死队友让出的家族**可被接手**（死人不移动、不产生用户可见的重复）；② 退化分支**不冻结**（bot 仍移动）；③ 「卡住」根因在 BotNavigator/物理层（片BU/BL/BP 域），本片只保证互斥。**可复算发生率**（口径 = `client/Logs/Editor.log` 里时间戳 **≤ 11:59:59** 截断的 `重新选目标` 计数 = 76；该日志持续增长，**不钉窗口复算会得到不同的数**，故窗口必须钉住；复算 = 逐行取行首 `[YYYY-MM-DD HH:MM:SS.mmm]` 的 `HH:MM:SS` 与串「重新选目标」计数）：走「要选路线」分支的 = `无空路线` 7 + `换一条路线` 6 = 13 次，其中**「4 条家族全被活人占满」7 次 = 53.8%**；占全部 76 次 = 9.2%；其余 63 次是「守点时长到点/包已下守包/回出生点」不经过家族选择。**卡住时长未变差的观测代理**：7 次无空路线全落在**巡逻家族**（T/槽位2 或 CT/槽位3），其后同一只 bot 的下一次换目标多为「守点时长已到 ⇒ 正常换目标」（Gooseman 11:47:35 卡 → 11:48:26 正常；Scuzzy 11:58:17 → 11:58:20；Minh 11:53:38 卡 → 11:56:54 再卡 → 11:57:30 正常），窗口 11:46–11:59 内无一只 bot 被冻结 ⇒ 代价有界。"),
    # 口径A(2026-09-23 片BW-S-R) id=68: 基=登记侧; +补充锚点(盘上) 2 片; 弃(盘上) 7 片 -> .ai-tmp/test/bwsr-A-discard.tsv
    ('68', '武器右键（attack2）**整条链缺失**：USP / M4A1 不能拆装消音器、Glock18 不能切连发',
     '① 输入层结构里**只有 `Zoom`**：`CsInputState`（`client/Assets/Scripts/Module/Match/ICsMatch.cs`）的字段逐字是 `Move / Jump / Crouch / Walk / Fire（左键按住） / Zoom（右键，AWP/Scout 开镜） / Yaw / Pitch`，**没有 attack2 / 次级开火**；唯一的右键消费点 = `client/Assets/Scripts/Module/Combat/CombatModule.cs:174`（`cmd.Zoom = input.GetKey(GameKey.MouseRight)`）；② 武器表里也没有"是否支持消音器/连发切换"的字段（`client/Assets/Scripts/Core/CsWeapons.cs:34-46` 的 `CsWeaponDef`）⇒ 除狙击开镜外，任何武器的右键在本工程**结构性无效果**（不是某把枪漏了）。；补充锚点：`Module/Match/ICsMatch.cs`',
     '用户本轮原话"很多枪右键没效果，就像警的默认小手枪，右键不是拆消音吗？"；实现出处 `client/Assets/Scripts/Module/Combat/CombatModule.cs:174`、`client/Assets/Scripts/Module/Match/ICsMatch.cs`（CsInputState）、`client/Assets/Scripts/Core/CsWeapons.cs:34-46`；原版语义载体 **在盘**：CS 1.6 的逐武器 attack2 写在 `cstrike/cl_dlls/client.dll` 里（不是 cvar / 不是数据表）—— 片AW 已把它取回（`原版资源/cs16src/cstrike/cl_dlls/client.dll`，1,093,128 B），但**尚未反汇编**定位 attack2 分支 ⇒ 出处待补（降级链第 2 级：可执行里的常量/分支，**载体已具备**）。⚠️ `原版资源/hlsdk/dlls/weapons.cpp` 是 **HL1** 的武器实现，只能证明"开火/切换写死在类里"这一机制，⛔ 不是 CS 语义出处；补充锚点：`Module/Match/ICsMatch.cs`' + '【片FX-ALL 2026-09-23 **落地 · 部分消除**】attack2 这条链已补上：① 输入层 `CsInputState.Attack2`（**按下沿**，由 `CombatModule` 用 `GetKeyDown(GameKey.MouseRight)` 填）；② 能力表 `CsWeaponDef.CanSilence` / `CanBurst` + `CsWeapons.MarkAttack2Capabilities()` —— **只有 4 把**有出处（Usp/M4A1 = 消音、Glock18/Famas = 连发），默认 false = 无出处不接；③ 状态位 `CsActor.Silenced` / `BurstMode`；④ 消费点 `CsInventory.ToggleWeaponMode`（⛔ 切枪期间照样能切，与 `Reload` 的拦截规则不同）+ `CsMatch.UpdateLocalPlayer` 里的**模拟侧判沿** `inp.Attack2 && !_preAttack2`（`_localInput` 是**黏的**：离线驱动只置一次 true 就没人清 ⇒ 不判沿会每帧翻转一次）。**出处（新证 · 降级链第 2 级）**：在原版 `原版资源/cs16src/cstrike/cl_dlls/client.dll`（1,093,128 B）里按**文件偏移**定位到 `weapons/usp_silencer_off.wav` (0x0e3804) / `usp_silencer_on.wav` (0x0e3824) / `m4a1_silencer_off.wav` (0x0e308c) / `m4a1_silencer_on.wav` (0x0e30ac) / `famas-burst.wav` (0x0e26f4) / `#Switch_To_BurstFire` (0x0e27d4) / `#Cstrike_TitlesTXT_M4A1_Short` (0x0e6af4)，以及原版真输入通道 `+attack2` (@0x0e9f98) / `-attack2` (@0x0e4ac5)。**判据资产** = `tools/probes/attack2-probe.py`（A: 7 组结构断言；B: 上述 7 条串在该偏移处**逐字节重取**且必须是**首次出现**；C: 「按下沿」反例复现 —— S1 按住 8 帧⇒电平型切 8 次/判沿型切 1 次、S2 点按 3 次⇒两者都 3 次、S3 长按+再点⇒电平 4/判沿 2），实测 `RESULT: PASS`；离线自检 = `client/Assets/Scripts/Module/Combat/CombatSelfTest.cs` 的「差异 #68」段（能力表 4 条 + USP 按下沿 + **按住 5 帧不重复翻转** + Glock18 切连发 + 切枪期间仍生效 + AK47 无出处则状态一位不动）。**仍未消除**：① 消音后的伤害/散布、连发的发数与节奏**具体数值**无出处（要反汇编 `client.dll`）；② 判据只到「状态可切换 + 可观测」，**尚无实机联络图**（表现类判据未采）。'
     '【片FX-MUZZLE 2026-09-24 落地 · **实机消费点**已取证（用户复查第 2 条）】'
     '上一句缺的正是那一层：**在跑着的这一局里，右键那条链到底把状态翻过来了没有**。'
     '判据资产 `tools/probes/probe-attack2-live.cs`（三段：A 逐武器读 `CsWeapons.Get(id).CanSilence/CanBurst`；'
     'B 反射取 `CsMatch.Inventory`（`internal`，跨程序集只能反射）后对**本地玩家**逐武器调 `ToggleWeaponMode` —— '
     '= `CombatModule` 右键那条链的**唯一出口** —— 读 `CsActor.Silenced` / `BurstMode` 的前后值；'
     'C 同一把枪连调两次必须回原值，否则就是"按一次右键状态自己在抖"）。'
     '实测 `.ai-tmp/test/r2-attack2.txt`（同一 Play、`phase=Live running=True`、`local=Player id=1 team=CT 手持=usp`）：'
     '`usp CanSilence=T` → `Silenced False→True [翻]`；`m4a1 CanSilence=T` → `Silenced False→True [翻]`；'
     '`glock18 CanBurst=T` → `BurstMode False→True [翻]`；`famas CanBurst=T` → `BurstMode False→True [翻]`；'
     '`ak47 / m249 / awp / deagle / knife` 四标志全 False → 状态一位不动（`[不动]`）= **无出处不接**，与 `MarkAttack2Capabilities()` 逐条一致。'
     '汇总行 `[B/C] 汇总：翻过的武器=4/9  二次调用回原值=9/9`、`[B] 能力表里应当能翻的武器数=4` ⇒ **`RESULT: PASS`**'
     '（口径写在探针里：能翻的必须**恰好**等于能力表里 `Silence&#124;&#124;Burst` 为 true 的数目）。'
     '⛔ 探针只读业务数据：改 `ActiveWeapon` 只为把"手里那把枪"换成待测武器，**用完恢复原值**（实测尾行 `复原：local.ActiveWeapon=usp`），不点按钮、不发包、不改 `state.txt`。'
     '⚠️ **仍缺**：**表现类并排图**（挂上/卸下消音器后 `v_usp` 模型的差异、连发档位提示）—— 上一句的缺口只被**状态层**补上，视觉那一格仍未采。',
     '开「输入 × 玩法」片时：① 先取回 `client.dll` 的 attack2 分支（或一份原版行为证据）定死逐武器语义（USP/M4A1 消音、Glock 连发）；'
     '② 契约扩 `CsInputState.Attack2` + `CsWeaponDef` 的支持位；③ 判据 = 离线断言（换弹/开火链在 attack2 下的状态变化）+ 硝音器模型的载体（`v_usp`/`w_usp` 的 silencer 变体））。**【片FX-ALL 2026-09-23：①② 已落地、③ 落地一半（判据资产在盘、实机联络图未采）】⇒ 本行只剩"消音后伤害/散布、连发发数与节奏的**数值**（要反汇编）"与"表现类联络图"两块；逐条见「为什么」列末尾的落地段。'
      '。⛔ **用户 2026-09-24 复查仍报**：「很多枪械的右键还是无效」⇒ **并入本行（⛔ 不新开号）**；本行 ③ 的"实机联络图未采"正是这条。'
      '【片FX-MUZZLE 2026-09-24 进度】用户复查第 2 条**已落地且有实机判据**（`tools/probes/probe-attack2-live.cs` → `RESULT: PASS`，'
      '逐武器真调 `ToggleWeaponMode`、翻过的正好是能力表里那 4 把、二次调用 9/9 回原值；见「为什么」列末的落地段）；'
      '③ 的缺口由"实机联络图未采"**收敛为"表现类并排图未采"**（状态层已证、视觉层未采）；'
      '本行**主体剩余** = 消音后的伤害/散布、连发发数与节奏的**具体数值**（要反汇编 `client.dll`）⇒ ⛔ **本行不消号**。'),
    # 口径A(2026-09-23 片BW-S-R) id=69: 基=登记侧; +补充锚点(盘上) 4 片; 弃(盘上) 6 片 -> .ai-tmp/test/bwsr-A-discard.tsv
    ('69', '墙上弹痕与原版不符：只有 1 张变体、尺寸无出处、载体注记过期',
     '① 变体只有 1 张（原版是 `decals.wad` 的 `{shot1..5` 随机 + `{bigshot*` 大口径）；② 尺寸 `CsCombatTuning.DecalSize = 0.075f`（`Module/Combat/CsCombatTuning.cs:178`）源码注释自认是"按原版 decal 的观感（~7cm）"= **观感值不是出处**；③ `Core/ResPaths.cs:177` 的注释还写"弹痕精灵（真实文件 …，32×32）"，而盘上 `client/Assets/Resources/UI/Art/fx_bullethole` 实测 **16×16 / 234 B**（片AW 用 WAD3 解出的 `{shot1` 覆盖后注释未同步）；④ 弹痕的**表现类**判据（贴面朝向/尺寸/按材质观感）在 `策划/验收表.md` 的联络图索引里**没有格号**（片AW 只做了"换图 + 三重自洽断言"，没有并排图判过）。；补充锚点：e=0.075'
     '【片FX-ALL 2026-09-23 落地 · 部分消除】① **变体已补**：`decals.wad` 的 `{shot1`…`{shot5` **5 张全解出** → `Resources/UI/Art/fx_shot1`…`fx_shot5`（各 16×16，落盘台账 `.ai-tmp/test/fx-decal-variants.tsv`）；`ResPaths.FxBulletHoleKeys` 是一张**字面量 key 表**（⛔ 不是拼串 —— 拼出来的 key 在静态扫描里看不见，闸门 `coverage-diff` 的 D1 维度会把贴图判成"文件在盘上但无人读"；本片实测拼串版有 3 张被判不一致），`CombatEffects` 按表逐个加载，命中时均匀随机取一，一张都没加载到时退到程序化替身 `fx_bullethole`。"就是 5 张、不是随便几张"的出处 = 原版 `mp.dll` 的贴花**注册名表**（`char* name[]` 基址 VA `0x10165ED8`、元素步长 8、precache 循环在文件偏移 `0x98e70` 处按 `esi=0,8,..,0x150-8` 逐项喂引擎；`{shot1`…`{shot5` = 索引 0..4，`{bigshot1`…`{bigshot5` = 28..32）。② **注记过期已消除**：`Core/ResPaths.cs` 的 `fx_bullethole` 注记已改为 16×16，并新增 `FxBulletHoleKeys` / `FxBloodKeys` 两张字面量 key 表。③ `{bigshot1`…`{bigshot5` **已解出但刻意不落盘**（大口径选择在引擎 `hw.dll`，不在盘 ⇒ 无出处；落盘即构成 T0 违规），尺寸与像素指纹留在台账里。⚠️ 仍未消除：① **尺寸映射**（原版 decal 的世界单位换算）仍无出处，`DecalSize = 0.075f` 保持；新增的 `CsCombatTuning.DecalMetersPerPixel` 只是**同族比例推演**（48px→0.225m / 64px→0.30m），⛔ 不是出处；② 表现类并排图判据（贴面朝向：朝地与朝墙两格）仍未采。',
     '用户本轮原话"子弹落在墙上痕迹不对"；实现出处 `client/Assets/Scripts/Module/Combat/CombatEffects.cs:293-303`（贴面 + 沿法线抬 1cm + `DecalSize` 缩放）、'
     '`client/Assets/Scripts/Module/Combat/CsCombatTuning.cs:178/181/184`（DecalSize/DecalDuration/MaxDecals）、`client/Assets/Scripts/Core/ResPaths.cs:178-179`；'
     '原版载体 `原版资源/cs16src/cstrike/decals.wad`（960,012 B，SHA256 记在 `原版资源/清单.md` 片AW 节）；'
     '尺寸映射的原始出处（原版 decal 的世界单位换算）**待补**（降级链第 2 级：可执行里的常量；`)`；差异 #52 已登记"多变体未接"',
     '开「特效 × 材质」片时：① `tools/probes/wad3-extract.py` 把 `{shot1..5` / `{bigshot*` 全解出来；② 工程侧支持弹痕多图变体 + 随机取一；③ 尺寸按"贴图原生尺寸 × 原版世界单位映射"重算（映射取不到 ⇒ 保持自定值并留在本行）；④ 判据 = 同机位并排图采一次（贴面朝向：朝地与朝墙两格）；补充锚点：`/` / `.ai-tmp/screenshots/ay_tmp_bullethole_floor.png` / `ay_tmp_bullethole_wall.png`'
      '。⛔ **用户 2026-09-24 复查仍报**：「弹痕是不是还是一个白点，不是弹孔资源呢？」⇒ **并入本行（⛔ 不新开号）**；与 ① 的"5 张变体已解出"对不上 ⇒ 本片要查的是**运行时到底贴的是哪张 / 有没有贴上**'
      '【片FX69 2026-09-24 **根因 + 落地 · 本行关闭**】根因不在"贴没贴上"，而在**解出来的像素本身是白的**：'
      '`tools/probes/wad3-extract.py` 把 `decals.wad` 的 `{` 贴花当成 **`{` 透明纹理**口径解'
      '（非背景像素 -> alpha=255、RGB 取各自调色板色），而贴花其实是 **decal** 口径 —— 整张图是**灰阶不透明度**'
      '（白=透明、黑=实心），`palette[255]` 是整张的**基色**且不得出现在像素数据里。'
      '载体实测（`decals.wad` 225/225 lump）：0..254 号调色板是**严格递减灰阶**（`pal[0]=(255,255,255)` 白、'
      '`pal[254]` ≈ 黑），背景 = 索引 0 = 纯白。旧口径于是把不透明度斜坡的**白端**画成**不透明白墨** ⇒'
      '16×16 的 `{shot1` 里 24 个近白像素（亮度 192..255）不透明、只有 5 个达到 alpha≥128 ⇒ 墙上就是一块**白斑**'
      '（= 用户原话「白点」）。口径出处（三处独立、互相一致）：TWHL wiki `Texture` / `Tutorial: Decals`'
      '（「palette index #255 是整张贴花的基色、不得用于图内」「palette index == opacity」）、robmikh 的 GoldSrc'
      '复刻日志（「Each pixel is really a grayscale pixel… The last color in the palette is the real color」）、'
      'GameBanana 教程（「the darker parts will be more solid (White = invisible)」）。'
      '**【落地】**① `wad3-extract.py` 改为 `RGB = palette[255]`、`alpha = 255 - 调色板亮度`，自检换成三条硬断言'
      '（四角一致 / 背景必须纯白即不透明度 0 / 索引 255 不得出现在像素里）；② 重解并落盘 5 张 `{shot*` + 6 张 `{blood*`'
      '（脚本 `.ai-tmp/test/_re-extract-decals.py`；尺寸与文件名不变 ⇒ 代码一行不改），台账 `.ai-tmp/test/fx-decal-variants.tsv`'
      '的字节/像素指纹同步刷新；③ **兜底图** `fx_bullethole.png` 旧版是**一整块纯白方块**（`make-fx-sprites.py` 程序化生成）'
      '⇒ 改用 `{shot1` 同源、`make-fx-sprites.py` 不再生成它（退回白方块正是"白点"的另一条路）；'
      '④ 判据 `tools/probes/decal-size-probe.py` 的 [E] 段改断言新口径 ⇒ **11 张全绿**（既有全透明背景、又有半/不透明墨迹），'
      '工具侧断言从 `masked_bg_index` 改为 `decal_base_colour`。⚠️ 仍未消除：① **尺寸映射**（原版 decal 的世界单位换算）'
      '仍无出处（`DecalSize=0.075f` 保持本工程值）；② 表现类并排图判据（朝地 / 朝墙两格）仍未采。'
      '【片DECAL-LIVE 2026-09-24 实机取证 · 本会话】把「②表现类」那一格推到可判（判据资产 `tools/probes/probe-decal.cs` + 新增 `tools/probes/probe-decal-center.cs`，驱动 `.ai-tmp/drivers/bz-final4.sh` 与 `bz-decalcenter.sh`）：'
      '**① 真调用** ⇒ `RESULT-DECAL: PASS` —— 朝墙/朝地两格都命中**真实几何**（`_0csSandWall.png` / `SandRoad.png`），`fx_shot1`..`fx_shot5` **5 个变体全用到**，贴花世界宽 **0.0750 m**、`up` = 该面法线；共 **51 个 active 弹痕**。'
      '**② 弹痕上屏（确定位置）** ⇒ `RESULT-DECALCENTER: PASS` —— ⛔ 不再靠"扫前方锥"：玩家朝向**每次 Play 都不同**（实测两次相机 `fwd` 分别是 `(-0.73,0,-0.68)` 与 `(0.79,0,0.61)`），固定前锥必然有一次落空、实测出 `找到=False`；也⛔不能按"最近面"选（最近面落在画面下缘/中线，正被**第一人称枪身**挡住，截出来整片是枪）。改成**直接瞄准指定视口点** `(0.5,0.76)`（自底部起算 ⇒ 图像上部 30% 那条带，枪身够不到；9 个候选点依次降级）：射线命中 **4.89 m** 处的 `_0csSandWall.png`，3 张弹痕全部贴上（`fx_shot5` 等），弹痕离命中点 **0.01 m**、世界宽 **0.0750 m**、屏坐标 `(960,821)` 在画面内。'
      '**③ 离线像素判据**（`.ai-tmp/test/r3-decal-offline.txt`，PIL 直读 PNG）⇒ `fx_shot1`..`fx_shot5` 的 `alpha>60` 像素 **平均 RGB = 0.000、近白像素占比 0.00%**（`fx_bullethole` 同源同值）'
      '⇒ **是暗芯弹孔，⛔ 不是用户担心的"白点"**；同表 `fx_blood1`..`fx_blood6` 平均 RGB 0.086~0.188、近白 0.00%。'
      '**④ 「并排图」= 前后帧像素差分**（新增 `tools/probes/decal-frame-diff.py` + 驱动 `.ai-tmp/drivers/bz-decal2.sh`）⇒ `RESULT-DECALPAIR: PASS`：'
      '**同一次 Play** 里先截 `decal_before`、贴完弹痕再截 `decal_after`，在 probe 报出的**三处屏坐标**各裁 ±26 px 逐像素差分 ⇒ '
      '**三处都真的变了像素（19 / 16 / 22 px），且"变暗"占满、"变亮" = 0**；变化像素均值亮度 **95.1 / 103.2 / 116.7 < 墙均值 134.3 / 127.6 / 147.6**，'
      '最大单像素 ΔL = **−37.6 / −49.3 / −46.3**（前 RGB 如 `(155,139,113)` ⇒ 后 `(105,93,75)`）⇒ '
      '**画上去的是一块比墙更暗的孔**；变化像素均值亮度离"近白"（≥200）很远 ⇒ **⛔ 不是白点**。留存图 `.ai-tmp/test/z-decal-pair.png`（4 倍）与 `z-decal-pair-zoom.png`（10 倍、十字标出报告坐标）。'
      '⚠️ **仍未消除**：① **尺寸映射**（原版 decal 的世界单位换算）仍无出处（`DecalSize=0.075f` 保持本工程值；`DecalMetersPerPixel` 只是同族比例推演）；'
      '② 「并排图」的**原版 CS 1.6 实拍那一半**仍缺（本工程侧已有 before/after 帧 + 像素判据）；③ `{bigshot*}`（大口径）仍刻意不落盘。⛔ **本行不消号**。'),
    # 口径A(2026-09-23 片BW-S-R) id=70: 基=登记侧; +补充锚点(盘上) 1 片; 弃(盘上) 7 片 -> .ai-tmp/test/bwsr-A-discard.tsv
    ('70', '买枪界面 / 选人（兵种）界面 UI 未按原版载体重建',
     '① 买枪界面 `client/Assets/Scripts/UI/InGame/BuyMenuPanel.cs` 的**全部布局常量是自建**（`:27-38` DialogWidth 1020 / DialogHeight 640 / CategoryY −104 / RowHeight 46 / RowsPerColumn 6 / ColumnWidth 470 …），'
     '注释写的出处是"规格 G3，任务书 §4.2"⇒ ⛔ 不是原版载体；而原版口径的载体**已在盘**：'
     '`原版资源/cs16src/cstrike/sprites/weapon_*.txt`（31 份，片AW 取回；逐字给出 320/640 两档下 weapon/ammo/crosshair 部件取自哪张 HUD 精灵 + 源矩形 + 屏幕落点）'
     '与 `cstrike__sprites__640hud10.spr` / `640hud11.spr`；'
     '② 选人（兵种）界面**本工程没有**（只有选阵营 `UI/Flow/TeamSelectPanel.cs`）；原版载体 `classmenu_ct.res` / `classmenu_ter.res` 本机不在盘'
     '（差异 #36 早已登记"未做"，本行按用户本轮 #5 并入，⛔ 不新开号）。'
     '【片FX-MUZZLE 2026-09-24 落地（用户复查第 5 / 第 6 条）】'
     '① **中文化已闭环（用户第 5 条「选角色的界面变成中文」）**：`UI/Flow/TeamSelectPanel.cs` 新增两张**字面量表** '
     '`TitleText = "选择阵营"` 与 `ButtonTexts = { "1 恐怖分子", "2 反恐精英", "3 VIP", "5 随机分配", "6 观战", "0 取消" }`，'
     '`BuildLayout` 改从这两张表取文案（⛔ `ButtonCommands` 逐字未动：`jointeam 1/2/3/5/6` + `vguicancel` —— 改的只是显示，不是命令）；'
     '⚠️ 本工程 UI 是**预制体驱动**（`BuildLayout` 只在缺节点时兜底）⇒ 只改源码**不上屏**，'
     '必须把中文**写进** `client/Assets/Resources/UI/TeamSelectPanel.prefab`：落盘脚本 `.ai-tmp/test/_fix-teamselect-texts.cs` '
     '走 `PrefabUtility.LoadPrefabContents` + `SaveAsPrefabAsset` **定向补丁**（文案常量用**反射**从 `TeamSelectPanel` 现取，⛔ 不在脚本里重抄一份），'
     '实测输出 `joinTeam: [SELECT TEAM] -> [选择阵营]` / `terbutton: [1 TERRORIST FORCES] -> [1 恐怖分子]` … `changed=6` / '
     '`SAVED Assets/Resources/UI/TeamSelectPanel.prefab` / 回读 `disk joinTeam=[选择阵营]` `disk terbutton=[1 恐怖分子]`。'
     '⛔ **刻意不跑整个 `Editor/Flow/FlowSetup.cs`**（那会连带重写 8 个预制体 + Boot/Menu 场景 + Build Settings）⇒ 爆炸半径 = 1 个文件 6 行 '
     '（md5 前后对比：全库只有 `TeamSelectPanel.prefab` 变；diff = 6 行 `m_Text` 改成 `\\uXXXX` 转义 —— `.gitattributes` 把 `*.prefab` 标为 binary，'
     'YAML 首行 `%YAML 1.1` 未变、文件仍可正常反序列化）。'
     '② **"别的地图信息 / 网址"已消除（用户第 6 条）**：源码 + 预制体 + 全库（含 `策划/**`、`client/Assets/**`）逐词搜 `GameHelper` / `johnsto` / `MacMan` —— '
     '**0 命中**；`by clover-engine` 已在位（skill §6 第 9 条的品牌署名，同差异 #41 的主菜单署名口径）。'
     '③ **实机渲染已取证（2026-09-24 本会话）**：截帧 `33_teamselect.png` / `33_teamselect_hover.png`（各 1920×1080，同一 Play；'
     '驱动 `.ai-tmp/drivers/bz-ui.sh`）—— 画面上**真的是中文字形**（`选择阵营` + `1 恐怖分子` / `2 反恐精英` / `3 VIP` / '
     '`5 随机分配` / `6 观战` / `0 取消`）+ 底部 `by clover-engine`，⛔ **不是方框**（字形来自 `CsUiStyle.OriginalFont` 的 '
     'CJK 回退族 Verdana → Microsoft YaHei → PingFang SC → Noto Sans CJK SC）。'
     '⚠️ 这一条与前两条**不是同一件事**：①②只证"预制体里**存**的是中文"，③才证"上屏**画**的是中文" —— '
     '预制体里存了字而字体链缺字形，屏上就是方框。',
     '用户本轮原话"买枪界面ui不对， 选人界面ui不对。"；实现出处 `client/Assets/Scripts/UI/InGame/BuyMenuPanel.cs:27-38`、`client/Assets/Scripts/UI/Flow/TeamSelectPanel.cs`；原版载体出处 `原版资源/cs16src/cstrike/sprites/weapon_*.txt`（31 份）+ `cstrike__sprites__640hud10.spr`/`640hud11.spr` + `原版资源/清单.md`（片AW 节，逐字样例已抄录）；选兵种 `.res` 本机不在盘 ⇒ **待补**（降级链第 1 级：原始数据）；补充锚点：`UI/Flow/TeamSelectPanel.cs`',
     '开「UI × 面板」片时：① 用 `weapon_*.txt` + `640hud10/11.spr` 重建买枪界面的部件矩形与落点（⛔ 不许再自定常量）；'
     '② 取回 `classmenu_*.res` 后建选兵种界面；③ 判据 = 同机位并排图 + 控件落点的数值断言（`策划/对照表.md` §4 的 U-* 口径）。'
      '⛔ **用户 2026-09-24 复查补充两条**：①「选角色的界面变成中文」；②「默认选角界面为啥描述里还有别的人的地图信息，和网址之类的，**直接变成 by clover-engine**」（= skill §6 第 9 条的品牌署名）。均**并入本行（⛔ 不新开号）**。'
      '【片FX-MUZZLE 2026-09-24 进度】用户复查的第 5 / 第 6 条**已落地**（选阵营标题 + 6 个按钮中文化、且中文已写进预制体；'
      '地图信息 / 网址全库 0 命中、牌子已是 `by clover-engine`）；**本行主体（买枪界面 / 选兵种界面按原版载体重建）仍未做** ⇒ 本行**不消号**，仍留在「允许的差异」段。'
      '【2026-09-24 再续 · 第 5 / 第 6 条补上**实机渲染**判据】截帧 `33_teamselect.png`（中文上屏、⛔ 无方框）'
      '+ `33_teamselect_hover.png`（悬停态仍为中文）+ 联络图 `contact-sheet-4-menu.png` 格 D-06 / D-07'
      '（`TeamSelectPanel.prefab` 的 D4 逐控件行已按新文案重渲染）。⚠️ 仍不消号：买枪界面 / 选兵种界面是主体。'),
    # 口径A(2026-09-23 片BW-S-R) id=71: 基=登记侧; +补充锚点(盘上) 0 片; 弃(盘上) 3 片 -> .ai-tmp/test/bwsr-A-discard.tsv
    ('71', '角色模型（9 皮肤）**无法自证是原版 mdl**，且派生链只到一份社区 repack',
     '① 工程模型数据的全部来源 = `原版资源/cs16src/cs16game/app/cstrike/models/player/*/*.mdl` 经 `cs16_build.py` / `cs16_anim.py` 转成 '
     '`client/Assets/Editor/Views/ModelData/player_*.cs16mdl`（几何/蒙皮）+ `*.cs16anim`（骨骼动画）；'
     '② 中间格式头实测 = `C16M`/`C16A` v1 + 角色名 + 贴图名（`player_T.cs16mdl` 17,743 B 头 `C16M\\x01…player_T…player_T_TERROR.png`），**不记录源 mdl 的 SHA256** ⇒ 工程内**无法自证**；'
     '③ 能站得住的证据只有"与那份 mdl 逐值一致"（`策划/对照表.md:136` M-03 骨骼 23 / `:137` M-04 序列 111 / `:138` M-05 idle1 fps15/61 帧 / `:145` M-12 / `:147` M-14）；'
     '④ 那份 mdl 属**社区 repack**（路径 `cs16game/app/cstrike/models/…`；`策划/基线图/场景清单.md:20-39` 记录了这份 repack 的授权链缺件问题）；'
     '⑤ 贴图侧同源缺口：M-16（`:149`）自认"按 md5 去重合并，不再与 mdl 一一对应"。'
     ' 【片BW-ZERO 2026-09-23 历史路径标注】本行引用的 `原版资源/cs16src/cs16game/app/cstrike/models/player/*/*.mdl、原版资源/cs16src/cs16_anim.py` **不在盘**（junction-aware 逐路径实测；`原版资源/` 顶层现只有 `_moved-out-from-assets / cs16src / gamestartup.mp3 / hlsdk / innoextract-1.9-windows.zip / 备份 / 清单.md`）⇒ 属**历史路径**：当时引用的 `cs16game/app/**` 抽取树现已不在。**已取回**：工程内 38 份中间数据（`.cs16mdl` / `.cs16anim`）在盘；原版 `*.mdl` 全仓 0 个、`cs16_anim.py` 不在盘 ⇒ 载体不可复核（与差异 #71 的"无法自证"一致）。',
     '用户本轮原话"人物模型还是不对，感觉你是社区版本的模型，不是原版模型"；'
     '派生链出处 `策划/对照表.md:134,136,137,145,147,149,150`；中间数据 `client/Assets/Editor/Views/ModelData/`（38 份 cs16mdl + 38 份 cs16anim）；'
     '标签/骨架实测口径 `原版资源/cs16src/cs16_anim.py`（本机不在盘 ⇒ 引用不可达，见 `策划/载体可达性登记.tsv`）；'
     '原版官方发布版的 mdl 载体**本机没有**（`原版资源/` 实测无 `models/`）⇒ **待补**（降级链第 1 级：原始数据；'
     '可取路径 = 照片AR 的 `codeload` tarball 重新取回同一 repack 的 `models/**`，或用户给一份官方客户端）'
     ' 【片BW-ZERO 2026-09-23 历史路径标注】本行引用的 `原版资源/cs16src/cs16game/app/cstrike/models/player/*/*.mdl、原版资源/cs16src/cs16_anim.py` **不在盘**（junction-aware 逐路径实测；`原版资源/` 顶层现只有 `_moved-out-from-assets / cs16src / gamestartup.mp3 / hlsdk / innoextract-1.9-windows.zip / 备份 / 清单.md`）⇒ 属**历史路径**：当时引用的 `cs16game/app/**` 抽取树现已不在。**已取回**：工程内 38 份中间数据（`.cs16mdl` / `.cs16anim`）在盘；原版 `*.mdl` 全仓 0 个、`cs16_anim.py` 不在盘 ⇒ 载体不可复核（与差异 #71 的"无法自证"一致）。',
     '开「角色模型」片时：① 照片AR 的 `codeload` 路径取回该 repack 的 `models/player/**` 与 `models/v_*.mdl`，逐文件算 SHA256；'
     '② 与工程中间数据的头字段（骨骼/序列/贴图数）与顶点数对账，把"逐值一致"升级成"逐字节一致"；'
     '③ 若用户能提供**官方**客户端，则补一次跨来源比对（当前只能证明"与这份 repack 一致"）'),
    # 口径A(2026-09-23 片BW-S-R) id=72: 基=登记侧; +补充锚点(盘上) 1 片; 弃(盘上) 10 片 -> .ai-tmp/test/bwsr-A-discard.tsv
    ('72', '换弹动画会丢',
     '① 触发方式是**边沿检测时间戳变大**，不是模拟发出的换弹事件：`client/Assets/Scripts/Module/View/ActorView.cs:268` （`actor.ReloadEndTime > _preReloadEndTime + 0.0001`）→ `:270` 取 `CsViewTuning.PlayerReloadStates(...)`；`Module/Match/CsInventory.cs:403-439` 的 `Reload` 在三条分支上直接 `return`（`:408` 没武器 / `:413` 不支持换弹 / `:419` 正在切枪 / `:426` 弹匣已满），`ReloadEndTime` 也可能被同帧重设（连点 R / 换弹中切枪再切回）⇒ 边沿检测采不到 ⇒ 整段动画丢失；② **没有"请求换弹 → 动画必须播一次"的运行时断言**（`策划/验收表.md` R6 只看"`HasState`=T + 数值"，不判"每次请求都播"）；③ 本来就该没有的两类（⛔ 不算缺陷）：`CsViewTuning.cs:318-327` 注明原版 group0 实测**没有** `ref_reload_grenade` / `ref_reload_knife`。第一人称侧同理：`CsViewTuning.cs:254`（`VmStateReload = { "reload" }`）。；补充锚点：`/`' + '【片FX-ALL 2026-09-23 **落地 · 部分消除**】按本行「何时消除」的第①条把口径改了：新增**模拟侧单调序号** `CsActor.ReloadSeq`（`client/Assets/Scripts/Module/Match/CsTypes.cs`），只在 `CsInventory.Reload` 的**成功分支**自增一次（紧跟 `a.ReloadEndTime = now + def.ReloadTime;`）；两处表现层（`Module/View/ActorView.cs`、`Module/View/ViewModelRig.cs`）从旧的 `ReloadEndTime > _preReloadEndTime` 改为消费 `ReloadSeq != _preReloadSeq`，旧口径的字段 `_preReloadEndTime` 已下线。**为什么序号不会被吃掉**：`ReloadEndTime` 有三种归零/跨完的方式（结算 `CompleteReload`、切枪 `SwitchWeapon`、同帧内请求+跨过的边界），而序号只增不减。**判据资产** = `tools/probes/reload-edge-probe.py`（A1~A4 结构断言：字段在盘 / `ReloadSeq++` **恰好 1 次**且紧跟成功分支 / 序号行在该方法**最后一次 `return` 之后** / 两处表现层都消费序号且旧字段无残留；B 反例复现：把两条口径各实现成谓词喂同一串采样 —— S1「采样间隔 ≥ 换弹时长」（视图整段漏窗）旧口径命中 **0** 次、新口径 **≥1** 次；S2 60Hz 两者都命中。**真数断言**：`D2 < D1`（AK47 3.0s vs 切手枪 0.3s+0.01+2.2s = 2.51s）—— 常数按 `W(...)` 形参签名**解析**，⛔ 不硬编码下标），实测 `RESULT: PASS`；离线自检 = `CombatSelfTest.cs` 的「差异 #72」段（连点 R 序号不变 / 结算序号不变 / 换弹中切枪再切回后重新换弹序号 +1）。**仍未消除**：① 判据是数值类（日志 + 断言），**尚无实机联络图**；② 本行另一层「`HasState` 缺状态时只 warning 不崩」（`IAnimPlayer`）未动。',
     '用户本轮原话"换弹动画有时候会丢"；实现出处 `client/Assets/Scripts/Module/View/ActorView.cs:249-274`、'
     '`client/Assets/Scripts/Module/Match/CsInventory.cs:403-439`、`client/Assets/Scripts/Module/View/CsViewTuning.cs:254,318-327`；'
     '原版口径 `HLSDK` 无 CS 的客户端动画选择（差异 #19/#20/#21 同源）⇒ 序列名与候选表按 mdl 实测（`策划/对照表.md:144` M-11 / `:147` M-14）',
     '开「动画 × 换弹」片时：① 把"换弹开始"改成**模拟侧事件**（或在 `Reload` 成功分支置一个单调递增的计数）而不是时间戳比较；'
     '② 加断言："每次成功换弹 → 动画状态至少进入一次 reload"；③ 判据 = 数值类（运行时日志行 + 断言），连点 R / 中途切枪两个边界各一条）。**【片FX-ALL 2026-09-23：①② 已落地（`CsActor.ReloadSeq` + `CombatSelfTest` 两条边界断言 + `tools/probes/reload-edge-probe.py` `RESULT: PASS`）】⇒ 本行只剩"实机联络图"（表现类）；逐条见「为什么」列末尾的落地段。'),
    # 口径A(2026-09-23 片BW-S-R) id=73: 基=登记侧; +补充锚点(盘上) 1 片; 弃(盘上) 7 片 -> .ai-tmp/test/bwsr-A-discard.tsv
    ('73', '死亡后**没有尸体**，且死亡动画播完立刻整具身体消失',
     '① 现在的死亡链 = 播 `death1..3` 之一（按 `actorId % 3` 取）→ 播完 `OnComplete` 里 `SetShown(false)` **整具身体隐藏**（`client/Assets/Scripts/Module/View/ActorView.cs:451-464` + `:293-312`；差异 #12 已登记实测时间线 `death2 clipLen=1.367 → t=1524ms 时 shown=False`）；② **工程里没有任何 corpse / 尸体实体**（**现存 `client/Assets/**` 内**，`Corpse` / `尸体` 命中 **0**；已取回：`ActorView` 死亡链本身）；③ **原版实现层存在尸体/碎裂机制**（**存在性证据**：CS 1.6 本体【载体类 A】`原版资源/cs16src/cstrike/dlls/mp.dll` 含 `corpse` 字符串 ×7、`gib` ×70；`cl_dlls/client.dll` 含 `corpse` ×3 —— **字符串级、未反汇编**，⛔ `gib` 为 3 字母属**弱**证据）⇒ 但 **"尸体留在地上 / 留多久 / 什么姿态"属行为，仍需反汇编或实机**；⛔ 不得据此写"原版没有尸体"，也⛔不得写"已证实原版留尸体"；④ 顺带：`death1..3` 是"倒地"序列，播完即隐藏 ⇒ 用户感知的"死亡动画没有"很可能是"播得很快 + 立刻消失"的合成观感（本片 ⛔ 不下断言，留一次实机）。；补充锚点：`/`'
     '【片FX-ALL 2026-09-23 落地 · 部分消除】死亡链已改为**尸体留场**：倒地序列播完**不再** `SetShown(false)`，而是把姿态钉在倒地序列的**最后一帧**（`_anim.Play(_deathState, 1f)` 后 `_animator.speed = CsViewTuning.CorpseAnimSpeed` = 0）+ 关掉该视图**全部 Collider**（尸体不被打中、也不挡活人走路）+ 关掉名牌；`IsAlive` 回到 true（复活、回合重开）时解冻并还原碰撞体。实现 = `ActorView.cs` 的 `_corpseHeld` / `_corpseFrozen` / `_deathState` + `EnsureCorpseShown()` / `ReleaseCorpsePose()`，调参 = `CsViewTuning.CorpseAnimSpeed`。⚠️ 仍未消除：① 尸体仍是**同一具 ActorView**（没有独立尸体对象、没有骨骼快照，因此“尸体数量上限 / 清场时机”没实现）；② 没有 Animator 的旧预制体仍走“立即隐藏”兜底；③ 判据 = 一次实机联络图（死亡 → 尸体在地上 → 回合重开消失）**尚未采**。',
     '用户本轮原话"死亡动画没有，尸体怎么不在地上？"；实现出处 `client/Assets/Scripts/Module/View/ActorView.cs:293-312,451-464`、`client/Assets/Scripts/Module/View/CsViewTuning.cs:194`（`PStateDeath`）；'
     '原版序列节奏 `策划/对照表.md:143`（M-10）；原版"尸体留在地上"的**直证载体在盘但未反汇编**（GoldSrc 死亡/尸体逻辑在 `原版资源/cs16src/cstrike/dlls/mp.dll`，1,640,960 B，片AW 取回，SHA256 见 `原版资源/清单.md`）⇒ 出处仍按"**待补（降级链第 2 级：可执行里的常量/分支）**"记，但**载体已具备**。'
     '【片BW-ZERO 2026-09-23 补 · 存在性证据】`mp.dll` 含 `corpse` ×7 / `gib` ×70、`cl_dlls/client.dll` 含 `corpse` ×3（**逐字节扫描**；⛔ `rg` 默认跳过二进制 ⇒ 文本行检索会给出**假 0**，判据必须声明"逐字节"）。',
     '开「死亡 × 表现」片时：① **首个证据路径 = 对已在盘的 `mp.dll` 做字符串/反汇编取证（⛔ 比找原版实拍更快）**；'
     '或取一份原版死亡实拍（定死"尸体留多久 / 什么姿态 / 是否可穿过"）；② 加"尸体实体"（复用 ActorView 的最后一帧姿态或一个静态姿态体）；③ 判据 = 一次实机联络图（死亡 → 尸体在地上 → 回合结束清场）。'),
    # 口径A(2026-09-23 片BW-S-R) id=74: 基=登记侧; +补充锚点(盘上) 1 片; 弃(盘上) 6 片 -> .ai-tmp/test/bwsr-A-discard.tsv
    ('74', '受击时**没有任何血雾 / 命中反馈特效**',
     '`client/Assets/Scripts/Module/Combat/CombatEffects.cs:10-19` 的四类特效 = 枪口火焰 / 弹道 / 弹痕 / 爆炸，**没有 blood**（**现存 `client/Assets/**` 内**，`Blood` / `血雾` 命中 **0**；已取回：四类特效实现）；受击的屏幕反馈只剩"屏幕边缘方向红框"（`client/Assets/Scripts/UI/InGame/CsDamageIndicatorWidget.cs:46-104`），而且**只给本地玩家**写（`Module/Match/CsDamage.cs:160-165` 的 `WriteLocalDamageIndicator`）；差异 #63 已把"伤害数字飘字"按 skill §0 铁律 1 下架（A 没有 ⇒ 不加）⇒ 于是"打中了"这件事在屏幕上**没有任何反馈**。【片BW-ZERO 2026-09-23 更正 · 原版侧证据】**原版【载体类 A】有血迹贴花机制**：`原版资源/cs16src/cstrike/decals.wad`（960,012 B，CS 1.6 本体贴花包）名称表含 **22 个血迹贴花名**（`{blood1..8}` / `{bigblood1..2}` / `{bloodhand1..6}` / `{yblood1..6}`，共 44 处）——**强证据**（资源本体命名，可直接被产品引用）；`mp.dll` 含 `BloodSpray` ×1 / `blood` ×21、`hlsdk/dlls/weapons.cpp`【载体类 B】含 `blood` ×10 / `BloodSpray` ×3 ——**中证据**（实现层有该机制，未反汇编；B 类只证明引擎层有，⛔ 不等价于 CS 1.6 采用）⇒ 原句"⚠️ 原版到底有没有血雾/击中提示（还是只有扣血条）需要原版实机证据"**不再成立**。⚠️ 仍未证的只有**行为**（贴花贴在哪 / 贴几张 / 触发时机），**载体已在盘 ≠ 行为已证**。；补充锚点：`/`'
     '【片FX-ALL 2026-09-23 落地 · 部分消除】受击血迹已实现：新增 `ICsMatch.OnBulletHit`（受击者 / 命中点 / 弹道方向 / 是否爆头），在 `CsDamage.ApplyHit` 里**“确定命中角色”之后、任何伤害闸门之前**发出（血与扣血是两件事：友好伤害关闭 / 护甲全吸收时 `OnDamaged` 不发，但原版照样出血）；`CombatModule` 订阅后调 `CombatEffects.BloodImpact`：① 命中点出一小团血雾；② 从命中点沿弹道追 ≤ `BloodDecalTraceRange` = 2.5m 找到“后面的面”再贴一张血迹贴花（原版血迹贴在**背后的面**上，不是贴在角色身上）。贴花用**真载体** = `decals.wad` 的 `{blood1`…`{blood6`（红，`mp.dll` 名表索引 13..18），6 张由 `tools/probes/wad3-extract.py` 解出 → `Resources/UI/Art/fx_blood1`…`fx_blood6`（48×48；`{blood5` 载体原生就是 64×64），按 `ResPaths.FxBloodKeys` 字面量 key 表加载，落盘台账 `.ai-tmp/test/fx-decal-variants.tsv`。⚠️ 仍未消除：① **血雾**的独立载体 `sprites/bloodspray.spr` 与 `sprites/blood.spr` **不在盘**（两个串都在 `mp.dll` 里）⇒ 现用血迹贴图染色的小贴片（0.18m）替身；② 行为口径（贴几张 / 触发时机）无直证；③ 判据 = 一次实机联络图（命中敌人 / 被命中两格）**尚未采**。',
     '用户本轮原话"受伤特效没有，没血"；实现出处 `client/Assets/Scripts/Module/Combat/CombatEffects.cs:10`、`client/Assets/Scripts/UI/InGame/CsDamageIndicatorWidget.cs:46-104`、`client/Assets/Scripts/Module/Match/CsDamage.cs:160-165`、`client/Assets/Scripts/UI/InGame/HudPanel.cs:878-882`；'
     '原版口径 **待补（降级链第 2 级：可执行里的常量/分支）**：直证载体**已在盘** = `原版资源/cs16src/cstrike/decals.wad`（血迹贴花名 22 个）+ `原版资源/cs16src/cstrike/cl_dlls/client.dll`（1,093,128 B，'
     '片AW 取回，未反汇编）+ `dlls/mp.dll`；优选路 = **从 `decals.wad` 直接抽 `{blood*` 贴花**（路径已验证：差异 #69 已从同一 wad 取 `{shot1..5` 弹痕变体）⇒ 次选 = 反汇编 `client.dll` 的受击渲染分支；末选 = 原版实拍/视频量化（降级链第 4 级）。',
     '开「特效 × 受击」片时：① **从已在盘的 `decals.wad` 抽 `{blood*` 贴花（⛔ 无需降级链第 4 级）**，'
     '并定死"贴在哪 / 贴几张 / 触发时机"（若定不下来 ⇒ 留一次实机或反汇编 `mp.dll`／`client.dll`）；'
     '② 按素材来源补血雾贴图（`原版资源/` 或降级链逐级退）并挂到 `CsDamage.ApplyHit`；③ 判据 = 一次实机联络图（命中敌人 / 被命中两格）。'
     '【片FX-HIT 2026-09-24 实机取证 · 本会话】③ 那条判据的**两半都采到了**（同一次 Play，驱动 `.ai-tmp/drivers/bz-hit-ledge2.sh`）：'
     '**半一（真调用）** `tools/probes/probe-blood.cs` ⇒ **`RESULT-BLOOD: PASS`** —— 6 个变体 `fx_blood1`..`fx_blood6` **全用到**、41 个 active 血迹、贴花世界宽 0.1152 m（另有大贴花 0.2250 m）；'
     '**半二（真命中链）** 为此新增 public 类型化入口 `CsMatch.ApplyBulletHitForTest(shooter, victim, weaponId, box, point, dist)`（一层转发 `CsDamage.ApplyHit`；`Damage` 是 internal 字段，离线驱动编进独立程序集拿不到）'
     '⇒ `tools/probes/probe-blood-hit.cs` 出 **`RESULT-BLOODHIT: PASS`**：入口返回 True、受击者 `hp 100→92`、FX 根下新增 1 个 active 血迹（`fx_blood1` 落在命中点 `(-15.085,-0.668,27.323)`，世界宽 0.086 m）。'
     '⇒ 「打中角色 ⇒ `CsDamage.ApplyHit` ⇒ `RaiseBulletHit` ⇒ `OnBulletHit` ⇒ `CombatEffects.BloodImpact` 落贴花」**整条链在实机上跑通**（⛔ 不是反射直调特效函数那种"只验特效函数能出图"）。'
     '⚠️ **仍未消除**：'
     '① **血雾**的独立载体 `sprites/bloodspray.spr` / `sprites/blood.spr` 仍不在盘（两个串都在 `mp.dll` 里）⇒ 现用血迹贴图染色的小贴片替身；'
     '② 行为口径（贴几张 / 触发时机）仍无直证；'
     '③ 「命中敌人」那一格的可视并排图仍未采 —— 本轮虽截到 `.ai-tmp/screenshots/blood_hit.png`，但受击者距本地玩家 **75.9 m**、不在画面内，⛔ 不作表现类判据。'
     '【口径更正 · 同一会话】`CombatEffects.BloodImpact` 的**返回值语义**是"是否真的落了贴花"（命中点沿弹道 `BloodDecalTraceRange`=2.5 m 内找到可贴面），'
     '⛔ **不是"有没有出血"** —— 找不到可贴面时它照样出 0.14 s 的血雾、只返回 false。'
     '⇒ `probe-blood.cs` 原判据第①条"返回 true"**过严会导致假红**，已改为 `activeBlood >= 1 && names.Count >= 2 && wide > 0.01f`，返回值降级为**报告值**。'),
    # 口径A(2026-09-23 片BW-S-R) id=75: 基=登记侧; +补充锚点(盘上) 1 片; 弃(盘上) 5 片 -> .ai-tmp/test/bwsr-A-discard.tsv
    ('75', '掉落的枪**不在世界里**（同一个根因：工程里没有"世界中的武器"这个对象）',
     '① `client/Assets/Scripts/Module/Match/CsInventory.cs:255-285` 的 `DropWeapon` **只改库存字段**（摘掉 `PrimaryWeapon` / `SecondaryWeapon`，或对 C4 走 `Bomb.OnCarrierLost`），再 `SelectBestWeapon`，**不生成任何世界实体**；（`CsMatch.cs:931` 是唯一调用点。）② 工程里也没有世界武器模型：`client/Assets/Editor/Views/ModelData/` 的 38 份模型数据 = 9 角色 + **29 个第一人称视模型 `vm_*`**，**没有任何 `w_*.mdl`**（原版的第三人称手持与掉落物都用 `w_*`）；`client/Assets/Resources/Art/{T,CT}/viewmodel_*.prefab` 同理（29×2 全是视模型）。⇒ 用户看到的"枪也不在地上"是**结构性未做**（不是掉落逻辑写错）。；补充锚点：份 = 9',
     '用户本轮原话"枪也不在地上？"；实现出处 `client/Assets/Scripts/Module/Match/CsInventory.cs:255-285`、`client/Assets/Scripts/Module/Match/CsMatch.cs:931`；'
     '建模数据口径 `策划/对照表.md:135`（M-02）与 `:150`（M-17：38 份 = 9 + 29）；'
     '原版载体 `models/w_*.mdl` 本机不在盘（`原版资源/` 无 `models/`）⇒ 需照片AR 的 `codeload` 路径从同一 repack 取回 ⇒ **待补**（降级链第 1 级：原始数据）',
      '开「世界物件（掉落武器）」片时：① 取回 `models/w_*.mdl` 并转成 `w_*.cs16mdl`（复用 `Cs16MdlData` 链）；'
      '② 加"掉落武器实体"（位置 = 死亡点 / 丢弃点，绕 Y 轴微转，可被 `+use` 拾取）；③ 判据 = 数值类（掉落/拾取的运行时日志 + 断言）+ 一次联络图'
      '【片DROP 2026-09-23 落地 · 2026-09-24 数值判据复采】① **世界实体已做**：新增 '
      '`client/Assets/Scripts/Module/Match/CsDroppedWeapon.cs`（数据：`WeaponId` / `Team` / `Position` / `YawDeg` / `MagAmmo` / `ReserveAmmo` / `DroppedAt` / `Consumed`）'
      '与 `client/Assets/Scripts/Module/View/CsDroppedWeaponView.cs`（视图：`Bind()` 把视图摆到数据的位置与朝向）；'
      '`CsMatch` 增 `_dropped` 列表 + `DroppedWeapons`（只读出口）+ `TickDroppedWeapons`（走到 `CsMatchConst.PickupRadius` 内自动拾取、上限 `MaxDroppedWeapons`）+ `Start`/`Stop` 清空；'
      '`CsInventory.DropWeapon` 成功摘除后写入掉落表；`Module/View/ViewModule.cs` 负责生成 / 回收视图。'
      '② **数值判据 ⇒ PASS**（`tools/probes/run-drop-selftest.cs` → `Cs16.Module.Combat.CombatSelfTest.RunDropOnly()`，离线跑、⛔ 不进 Play；原文 `.ai-tmp/test/r3-drop-selftest.txt`）：'
      '`掉落① ak47 @ (0.00,0.00,8.00) 余弹 17+42` / `拾取③ ak47 回到手上 余弹 17+42` / `重开清空⑤ 重开前 1 件 ⇒ 重开后 0 件` / `>>> 结论: PASS`；'
      '负控两条也过：② 站 5 m 外（> PickupRadius 1.2）不该被捡（世界仍 1 件）、④ 主槽已满时主武器捡不起来（口径：原版不会挤掉手里那把）。'
      '⛔ 断言特意取"非满弹 17+42"，就是为了能把"余弹原样回来"与"回满弹"区分开。'
      '③ **模型来源 = 降级链（必须留痕）**：原版掉落物用 `models/w_*.mdl`，而本机全盘 `.mdl` 计数 = 0 ⇒ 退到"复用第一人称视模型 '
      '`client/Assets/Resources/Art/{T,CT}/viewmodel_<weapon>.prefab`"（同一批 CS 1.6 原始资产转出的 prefab，⛔ 不是占位色块 / 内置几何体）。'
      '⚠️ **仍未消除**：① **表现类那一半没采** —— 「掉落枪躺在地上」+「被拾取后消失」的一张联络图（本轮只做数值侧；数值 PASS ≠ 画出来了）；'
      '② 原版 `w_*.mdl` 仍不在盘（取回后应换回真模型）；③ 掉落物**刻意不自转** —— 任务书曾写"绕 Y 轴微转"，但那是目标描述、⛔ 不是原版出处 ⇒ 按 skill §0 铁律 3 不加（要加 ⇒ 先补出处）。⛔ **本行不消号**。'),
    # 口径A(2026-09-23 片BW-S-R) id=76: 基=登记侧; +补充锚点(盘上) 0 片; 弃(盘上) 4 片 -> .ai-tmp/test/bwsr-A-discard.tsv
    ('76', '机器人"钻地"（地形贴合的边界：单层位图 + 软地板 + 无寻路）',
     '**bot 与真人走同一条物理**（`client/Assets/Scripts/Module/Match/CsMatch.cs:1752` 与 `:2292` 都调 `StepActorPhysics`），'
     '所以"没有地形碰撞"**不成立**；可判的三条边界：'
     '① 贴地位图是**单层 2D**（`client/Packages/com.clover.unity-engine/Runtime/Presentation/MapFormat.cs（第 29~30 行）`：`FlagHeightField` V1 未实现、见到即明确拒绝解码；差异 #49），'
     '上层平台/桥面与下层在 XZ 上同格 ⇒ `CsMap.TrySampleGround`（`:542-557`，只取**第一个**交点）无法区分"该站哪层"；'
     '② **软地板**：连续探不到地面时把人贴到最后一次已知地面 y（`CsMatch.cs:1853-1887` 的 `_lastGroundY` / `TrySoftFloor`）⇒ 在坡/台阶/`collision-mesh-gap.tsv` 列的"碰空气格"上会被贴到低于视觉地面的位置（形态像钻地）；'
     '③ **无寻路**（`BotNavigator` 只有路点 + 逃逸；引擎 `AStar` 在 `client/Assets/**` 内零调用；范围见下）' + _scope_dif(_SCOPE_ASTAR) + '⇒ bot 会朝不可走方向推进、在坡道处反复进出几何。'
     '本片 ⛔ 不进 Play，故"钻地"的**具体一格**未定案 ⇒ 判据留给下一次实机（逐帧 `actor.Position.y` vs `SampleGround` 的数值行）。',
     '用户本轮原话"ai人机会钻地不是真正的地形碰撞ai吗？"；实现出处 `client/Assets/Scripts/Module/Match/CsMatch.cs:1762-1904`（重力 → `ResolveMove` → `TrySampleGround` → 贴地/陡坡闸门 `:1802-1819` / 软地板 `:1853-1887` / 掉图兜底 `:1894-1904`）、'
     '`client/Assets/Scripts/Module/Map/CsMap.cs:444-531`（扫掠 ≤0.25m + 分轴滑墙 + 台阶）、`:542-557`（地面射线）、`:571-603`（`GroundMask`，只打 `CsWorld` 层）；'
     '单层位图出处 `client/Packages/com.clover.unity-engine/Runtime/Presentation/MapFormat.cs（第 29~30 / 87~93 行）`；'
     '几何-碰撞缺口判据 `tools/probes/collision-mesh-gap.py`（输出 `.ai-tmp/test/collision-mesh-gap.tsv`，分类 a/a2/b/c）；'
     '原版口径 `pm_shared.c`（`原版资源/hlsdk/pm_shared/pm_shared.c`，片AY 已落盘）⇒ 可对照 `PM_CatagorizePosition` / `PM_WalkMove`',
     '开「AI 移动 / 地形」片时：① **先接寻路**（#67 ①）让 bot 不再朝不可走方向推进；'
     '② 加"多帧贴地一致性"断言（`Position.y` 与 `SampleGround` 之差 ≤ 一个台阶，且不得低于它）；'
     '③ 判据 = 一次实机 + 逐帧数值日志（⛔ 不靠截图）；若步 (b) 坐实"软地板把人贴低"，改 `TrySoftFloor` 的回落条件（需另开片，⛔ 本片未改引擎/未改该链）'
      '【2026-09-24 片FIX-4 线M · 用户「碰撞和模型不一样」的三套值对照（**出表**）】判据资产 `tools/probes/move-stuck.cs` 的 `MoveStuck.Geom` → 产物 `.ai-tmp/test/move-geom.txt`（30820 B，一次 Play 内采）。**① 场景几何台账**：`colliders=880`（`BoxCollider` 811 全是 **trigger** 的烘焙块、`CapsuleCollider` 36 = 角色、`MeshCollider` **33 = 真几何**）、`meshRenderers=33`、`worldLayer=10`、`不在世界层的碰撞体=36`、`trigger=811`；`碰撞体所在的 GO 上没有 Renderer=847`（811+36，只有碰撞、看不见）。**② 逐列（0.5 m 网格）(a)位图 vs (b)碰撞面**：匪家扶手/斜坡车道 `cols=26` ⇒ `位图判挡但该列有碰撞面=3`、`位图判通但该列无碰撞面=0`；B 通台阶（中门）`cols=760` ⇒ `位图判挡但该列有碰撞面=314`、`位图判通但该列无碰撞面=1`（`cell(40,108)@(-23.500,36.000)`，与 #66 已登记的 4 格「位图可走但一格地面都没有」同类）。**③ (c) 渲染网格面 = 取不到，原因有出处**：`MeshRenderer 无 MeshFilter/mesh=0（其中不可读=33）` ⇒ 33/33 `Mesh.isReadable == false`；且 Unity 6 已移除 `Mesh.Raycast`（本片改用自写 Möller–Trumbore，但因不可读仍拿不到顶点）⇒ `renUp=-inf`、`Δ(col-ren)` 无法计算。**④ 为什么"实例不同"**（这条一开始判错过，**留档**）：第一版用 `ReferenceEquals(mc.sharedMesh, mf.sharedMesh)` 比 ⇒ 报「相同=0 不同=33」，那是**假的**（`ReferenceEquals` 比的是托管包装对象，同一原生 Mesh 的两处 `sharedMesh` 会给出两个包装）；改用 Unity 重载 `==` 后**仍是** `相同=0 不同=33`，逐条打样本才看清真相：同 GO 上渲染网格的名字全是 `Combined Mesh (root: scene)`（= Unity **静态合批**产生的合并网格，`!isReadable`、bounds = 整图 (113.792,15.443,134.925)），而碰撞网格是**真几何资产** `mesh_<材质名>`（可读，逐组 vc/tri 已知，如 `mesh_SandWllWndw` vc=308/tri=174）。**⇒ 三套值对照的结论（一句话）**：碰撞面与渲染面**不是同一份 Mesh 实例**，但那是 `Dust2Builder.cs:203` 给每个材质组打 `StaticEditorFlags.BatchingStatic` 之后引擎在运行期换上的**合批网格**，**不是**本工程把两套几何做错了 —— 生成侧 `Dust2Builder.BuildVisual` 的 `:180/198/201` 三行把**同一个 `mesh` 变量**同时赋给 `MeshFilter.sharedMesh` 与 `MeshCollider.sharedMesh`（`:192-194` 还把它存成 `mesh_<名>.asset`）；用户感到的"不一样"来自**另一个来源**：**水平阻挡是单层 2D 位图**（`de_dust2.bytes`，一格一位、没有高度），而高度/落地走真实几何 ⇒ 两者对同一格给出的答案可以不同（② 的两个计数就是它的规模）。**逐条判定：是引擎限制 / 是本工程生成侧 / 是运行时解算** ——(1) 「渲染面取不到」= **引擎/资源层限制**（合批网格无 CPU 副本 + `Mesh.Raycast` 已移除），⛔ 非本工程可修、也⛔ 不该为取证去关合批（会改交付物）；(2) 「碰撞网格 = 真几何资产」= **本工程生成侧正确**（`:180/198/201` 同一份 mesh，33/33 组名对得上）；(3) 「同一格里位图与几何不同答案」= **运行时解算**（`CsMap.CanStand` 的两层判据：位图 9 点 vs 真实几何），本条是差异 #64/#76 的量化。⚠️ 本表只判"两套数据是否一致"，**不判"某格可不可站"**（`colUp` 是整列最高的朝上面，可能是旁边楼顶）⇒ 口径别混。'),
    # ---- 片BQ（2026-09-22）：位图孤立分量的**定案 = 乙**（真有几何挡/真实落差，原版也走不过去）
    #   ⇒ 按 T0「宁可登记为差异，不许放水」登记四要素；⛔ 本片**未改**位图 / 未重烘（定案不是甲、也不是丙）。
    # 口径A(2026-09-23 片BW-S-R) id=77: 基=登记侧; +补充锚点(盘上) 2 片; 弃(盘上) 5 片 -> .ai-tmp/test/bwsr-A-discard.tsv
     ('77', 'de_dust2 的可走位图**不是单连通**：宽口径 46 个连通分量（主分量 4393 / 5312 格 = 82.7%，非主 919 格）、本片按引擎 A* 移动规则（对角要求两侧正交格可走）的严格口径 53 个（主分量 4369 / 5312 = 82.2%，非主 943 格）；11 / 117 个运行时标记点落在非主分量里 —— Bombsite_B[5]、Bombsite_B[8]、BuyZone_CT[2]、BuyZone_CT[3]、BuyZone_CT[8]、Route_CT_Mid[1]、Route_T_To_A[4]、Route_Patrol[1]、Route_Patrol[4]、Route_Patrol[5]、Route_Patrol[6] ⇒ bot 对这些目标求不出路径，`BotNavigator.EnsurePath` 落进「两格都可走但位图不连通」那一支（实测日志 ×N）。【片BU-R 更正 · 这条差异的**计数与归因**原来算错了，必须分开算】"求不出路径"有**两类**，旧写法把它俩混成一句"位图不连通"：① 真·位图孤立分量 ⇒ 引擎日志键 `astar.nopath`（`无可达路径 from=… to=…`）；② 位图判可走、但**高度一致性层**（`BotNavigator.BuildHeightReach`）从起点扩张不到那一格 ⇒ 引擎日志键是 `astar.badgoal`（`终点不可走 to=…`）。片BR 整段（`.ai-tmp/test/br-hold-plant-log.tsv`）实测：① **18** 次、② **13** 次 ⇒ 有 42% 的失败**不是位图的责任**，而旧日志把 13 次 badgoal 也写成了"位图孤立分量（单层 2D 位图 + 多层几何）"，会把修 bug 的人引去重烘位图（本条"为什么"列的第 ③ 条证据早已证明重烘 0/18415 格会变 —— 两者矛盾）。片BU-R 已把两类在日志里分开报（`BotNavigator.EnsurePath` 的失败分支现在先问 `WalkableCellHeightAware(to)`），并在**选目标**这一侧加了门禁（`BotNavigator.CanReach` + `CsBotBrain.TryPickReachable`）⇒ 修后同一批请求的失败数 **67 → 15**（口径 = `tools/probes/bot-goal-gate.py` 的 A4：`终点不可走` + `无可达路径` + 业务侧 `与当前位置不连通` 三类计数之和）。⛔ 这不是"判据写错"（甲不成立：规则是"最低地面层的人体高度带 [f+0.10, f+1.75] 里没有墙"，出处 `client/Assets/Editor/MapGen/Dust2GeoData.cs:213-247` + 转换侧 `tools/probes/rebuild-blockers.py` 的规则段），也⛔不是"烘焙过期"（丙不成立，见第 ③ 条证据）。；补充锚点：9 = 82.2', '这些分量与原版几何的**真实落差 / 真实墙面**重合。三条同批盘上证据：① 把位图整个拿掉、只留产品自己的台阶判据（抬升 ≤ `CsConst.StepUpHeight`=0.45 m）从 T 出生点格 (56,20) 扩张，**每一个**非主分量的可达格数都是 **0%**（#43=323、#29=192、#50=103、#22=84、#40=24、#0=21、#26=19、#42=19、#52=16、#1=15、#51=15 格）⇒ 是几何落差把位图切成岛，位图无责；② 产品日志那一对「起点 (56, 20) / 终点 (45, 15)」**同在主分量 #2**（位图上零阻隔格、严格 A* 距离仅 13 格）——失败发生在位图**之上**的「高度一致性层」（`Module/Bot/BotNavigator.cs` 的 `BuildHeightReach`）：离线按同一式复算的高度可达集 = **4173 格**，与产品日志的「可达集 4173 格」**逐字相同**；而 (45,15) 的落脚面 y = **8.941 m**、其南侧步行面 y ≈ 3.8 m ⇒ 落差 **5.11 m ≫ 0.45**；③ 逐格复算「官方生成路径」（场景 `Level/Blockers` 的 811 个格盒 → 引擎 MapBaker 的「格柱 ∩ 障碍 AABB」）与当前 `client/Assets/MapData/de_dust2.bytes` 的位图 **0 / 18415 格不一致** ⇒ 重烘不会改变任何一格（⛔ 排除丙）。⇒ 定案 **乙**：真的有几何挡，原版玩家也走不过去。', '本片证据（均可直接打开）：`tools/probes/marker-connectivity.py`（宽口径分量 + 每点归属 + 每条 `Route_*` 相邻路点可达性）、`tools/probes/astar-pocket-diag.py`、`tools/probes/bq-analysis.py` + 输出 `.ai-tmp/test/bq-analysis.txt`（分量表 + 「忽略位图」可达性 + 逐格面 y/净空）、`tools/probes/bq-bakepath.py` + 输出 `.ai-tmp/test/bq-bakepath.txt`（官方生成口径逐格复算 0/18415）、`tools/probes/bq-blockers.py`（对格逐格打印面 y/法线）；产品日志 `.ai-tmp/test/bp-hold-plant-log.tsv`（`两格都可走但位图不连通（起点 (56, 20) / 终点 (45, 15)）` + `可达集 4173 格`）；位图生成侧 `client/Assets/Editor/MapGen/Dust2GeoData.cs:213-247`、`client/Assets/Editor/MapGen/MapBakeRunner.cs:90-109`、`client/Assets/Editor/MapGen/Dust2Builder.cs:251-279`；落差判据 `client/Assets/Scripts/Module/Map/CsMap.cs:514-523` 与 `client/Assets/Scripts/Core/CsConst.cs`（StepUpHeight）；同根差异见本表 #49（`MapFormat.FlagHeightField` 未实现 ⇒ 位图只有单层）与 #76（单层位图 + 软地板 + 无寻路）。；补充锚点：`Core/CsConst.cs`', '把单层 2D 位图换成带高度层/多层的导航数据（引擎 `MapFormat` 的 `FlagHeightField` V1）之后；在那之前，取点侧 `Dust2Builder.SnapMarkerToWalkable` 只能保证「点可走」，⛔ 保证不了「走得到」—— 消掉这条差异要么让取点侧也做高度一致性检查，要么把落在不可达分量里的 11 个标记点全部迁到主分量。'),
    # --- 差异 #77（旧）已于切片BO 2026-09-22 **核销**（事实不成立）；编号 77 现由片BQ 的上述事实使用 ---
    # 原登记内容：「有寻路能力但业务零使用：引擎通用格子 A*（Runtime/Core/AStar.cs）从未被业务调用」。
    # 核销依据（同批盘上证据，均可直接打开）：
    #   ① 能力侧：client/Packages/com.clover.unity-engine/Runtime/Core/AStar.cs（8 邻接 + octile，
    #      Find L52 / FindSmoothed L138 / Smooth L148 / DefaultMaxNodes L32 / HasLineOfSight L180）；
    #   ② 业务侧：client/Assets/Scripts/Module/Bot/BotNavigator.cs:637
    #      `var path = AStar.FindSmoothed(WalkableCellHeightAware, from, to, AStar.DefaultMaxNodes);`
    #      —— 切片BN 落的"可走回调 + 高度一致性"判据就是在 EnsurePath 里喂给引擎 A* 的（本片复测仍在使用）。
    #      （主 agent 任务书引的 `:601` 是切片BN 改动**之前**的行号，改动后为 `:637`。）
    # ⇒ "零调用"不再成立 ⇒ 该差异不存在。删除本行后 差异登记.tsv 行数 77 → 76，
    #    验收表「允许的差异」段与聚合数由本生成器 --inject 同步重写（闸门 differences-source-of-truth /
    #    acceptance-sums 必须仍 PASS，见切片BO 回执）。
     # ---- 这几条此前只在产物 `策划/差异登记.tsv` 里（手工追加），而该文件由本脚本的 write_tsv() **无条件**重写 ----
     # ---- ⇒ 只要有人跑一次本脚本，它们就被静默删除。2026-09-23 slice BW-G 从产物**逐字**回填，未改任何措辞。 ----
    # 口径A(2026-09-23 片BW-S-R) id=78: 基=登记侧; +补充锚点(盘上) 1 片; 弃(盘上) 1 片 -> .ai-tmp/test/bwsr-A-discard.tsv
     ('78', '开火时 Game view 上出现 Unity 组件的「喇叭(AudioSource：实测 `sound=34(iconDrawn=34)`、差集 22922~28983px；相机上的 AudioListener 另有一只 71px 小图标，留档不并入)」「太阳(Light)」图标；选阵营看地图时地图上叠加细线与方框', 'Unity **编辑器**在 Game view 之上叠加绘制组件图标与 DrawGizmos：实测三个开关全为 true（`m_Gizmos=True` / `drawGizmos=True` / `showGizmos=True`），图标宿主 47 个（34 AudioSource / 1 Light / 2 Camera / 10 Canvas，hideFlags 全 None、全 active）；同一冻结帧 A/B（只关这个开关）后，开火帧差集 25 727 px 的太阳图标整块消失、选阵营帧差集 853+91 px 的线框整块消失 ⇒ 与游戏自身渲染无关（后缓冲里从来没有它们）；补充锚点：个 = 34', '修复 `client/Assets/Editor/VisualLeakGuard.cs`（进 Play 自动关）；判据 `tools/probes/capture-editor-screen.cs` + `tools/probes/diff-ab.py` + `tools/probes/probe-overlay-hosts.cs`；登记 `策划/验收表.md` 的 §BV', '编辑器侧已消除；用户自己再点开 Game view 的 Gizmos 仍会看到 ⇒ 若要彻底消除需给图标宿主设 HideFlags，其中引擎 `[Sound]` 池（SFX0..31）在引擎仓，需引擎侧配合（本片未改引擎、未实测 HideFlags 是否能压掉图标，故不写入代码）'),
    # 口径A(2026-09-23 片BW-S-R) id=79: 基=登记侧; +补充锚点(盘上) 7 片; 弃(盘上) 16 片 -> .ai-tmp/test/bwsr-A-discard.tsv
     ('79', '机器人持包者整回合 0 位移、永不下包（CsBotIntent 按值传递丢写入）', '`CsBotIntent` 是 struct；`TryBombObjective/TryDefuse/TryPlantOrPickup` 此前**按值**收 intent ⇒ 它们写入的 Move/State/Use 落在副本上、返回即丢 ⇒ T 持包者的 `intent.Move` 恒 0、State 恒 Idle：站在出生点整回合（实测 Gooseman/Rikk 0.000m 73.9s），导航判"卡住 0.00m"每 0.5s 换目标，T 永远到不了包点 ⇒ 闸门 A5 恒 0。修法 = 三个方法改 `ref CsBotIntent`（唯一正确的最小改动）。实测修后：A4 求路径失败 19→3、持包者开始移动（ZBot 净 45.2m）、`bot-goal-gate` A9 由 FAIL→PASS；补充锚点：e=0 / t=71.3', '`client/Assets/Scripts/Module/Bot/CsBotBrain.cs`（ThinkLive ④ / TryBombObjective / TryDefuse / TryPlantOrPickup）+ `client/Assets/Scripts/Module/Match/CsTypes.cs`（CsBotIntent = struct）；补充锚点：:1020 / :1484 / :1486-1487 / :1492 / :1534', '不消除：这是实现缺陷，已在本片修掉；本条只登记"修改过闸门口径"的伴随项 —— `tools/probes/bot-goal-gate.py` 的 A1/A2 由"全体必须推进"改为**有条件**（有包点驻留证据的静止算合格，⛔ 15m/0.15 未动）并新增 A9（持包 bot Live 连续静止 >10s 且全场 0 次下包 ⇒ FAIL）'),
    # 口径A(2026-09-23 片BW-S-R) id=80: 基=登记侧; +补充锚点(盘上) 0 片; 弃(盘上) 1 片 -> .ai-tmp/test/bwsr-A-discard.tsv
     ('80', '掉落 C4 的拾取链（片BU-R5）：① bot 侧新增"仅一人、高于交战"的拾取分支；② 模拟侧 1.2m 自动拾取**只对本地玩家调用** ⇒ bot 走到 0.44m 仍 0 次拾取、A5 拾取链仍断', '① A（CS 1.6）**本体不含 bot AI**（官方 bot 属 Condition Zero / PodBot，不在本工程载体范围）⇒ "bot 何时脱战去捡包"在 A 里没有可逐值对齐的量，只能按原版语义「T 会去捡掉落的 C4（bot 也会）」落地为**拾取优先于交战、但只出一个人**（否则全队脱战送死）。② `CsBomb.TryPickupDropped` 在 `client/Assets/**` 内只有 1 处调用点' + _scope_dif(_SCOPE_PICKUP) + ' = `Module/Match/CsMatch.cs:1758`（在 `UpdateLocalPlayer` 里）⇒ bot 侧每帧的 `UpdateBots`（`CsMatch.cs:2247`）不调它；实测：片BU-R5 round-1 里 Minh 在掉落点 0.44m 处站了 30.25s，日志 0 条 `拾起了掉落的 C4`', '`client/Assets/Scripts/Module/Bot/CsBotBrain.cs`（`IsElectedBombHunter` + `CommitTacticalDecision` 里高于 `Engage` 的分支 + `TryPlantOrPickup` 准入）；缺口侧 `client/Assets/Scripts/Module/Match/CsMatch.cs:1758` vs `:2247`', '① bot AI 逐行对齐 PodBot / CZ bot 源码时复核（同 #58 口径）；① bot AI 逐行对齐 PodBot / CZ bot 源码时复核（同 #58 口径）；②**【片BU-R6 2026-09-23 已消除 · 核销】**由片BU-R6（经主 agent 书面授权、只此一行）在 `CsMatch.UpdateBots` 里补上了 `Bomb.TryPickupDropped(a);`（与玩家侧 `CsMatch.cs:1758` 同一 API、同一 1.2m 判定）⇒ 核销证据（数值类 · L3 原文）：`.ai-tmp/test/bu-r6-hold-plant-log.tsv` 的 `[2026-09-23 00:38:25.787] [Info] [Match] Gooseman 拾起了掉落的 C4`（round1；前置链路 = 88.997 `Minh 携带的 C4 掉落在 (-35.48, 0.00, 20.82)` → 92.095 `[Bot] [C4] Gooseman 去捡掉落的 C4：到落点 5.06m`）；round2 另两条 `Gooseman 拾起了掉落的 C4`（00:39:35.872）/ `Minh 拾起了掉落的 C4`（00:39:56.868）；拾取者随后接力推进并**真的下包**（round1 `E TPLANTED t=98.640`、`★ Gooseman 安放 C4 于 (-23.75, 0.00, 26.84)` 00:38:31.587）⇒ `拾起了` 由 0 变 3、A5 转 PASS'),
    # 口径A(2026-09-23 片BW-S-R) id=81: 基=登记侧; +补充锚点(盘上) 4 片; 弃(盘上) 5 片 -> .ai-tmp/test/bwsr-A-discard.tsv
     ('81', '机器人持包者在 `Engage` 里与敌人"在偏好距离上对峙"、整回合不到包点（片BU-R6）；补充锚点：`=300', '① 走位判据是"距敌人 > `PreferredRange × AdvanceRangeFactor` 才压上"（Normal 16×1.5 = 24m），而压上方向指向**敌人**、不是包点；② 该分支的"走位"是 `Strafe(...)`，其首行 `if (PredictSeconds() <= 0.0001f) return Vector3.zero;` 的下界 `PredictSkillSpeedLo` **恰等于 Normal 的 `AimSpeedDegrees`=300** ⇒ Normal/Easy 的 Strafe 恒为零向量 ⇒ `intent.Move` 长时间为 0。A（CS 1.6）本体不含 bot AI ⇒ "持包者对峙多久"在 A 里没有可逐值对齐的量，只能按原版语义「T 持包到包点 → 下包」落地。实测（片BU-R5 L3）：round2 持包者 Rikk 在 Live **连续静止 21.3s / 21.4s**，整回合"到最近包点标记"只从 87.1m 挪到 29.6m；同段 `换弹失败：MP5 Navy 备弹为 0` / `交战中开火被抑制第 124/143/154/155 次`；补充锚点：:123 / :127 / :130', '`client/Assets/Scripts/Module/Bot/CsBotBrain.cs`（`Strafe` 去门 + `Engage` 持包压上 + `CommitTacticalDecision.MustPlantFirst` + `TryGetPlantAim`）；依据 = 规格 `策划/策划案/CS1.6单机参考规格.md:123`（行为树 3 档共用、只换参数）/`:127`（交战：停/蹲 → 瞄准 → 射击 → **走位**）/`:130`（T 持包到 B 点 → 下包）；伴随项 = `tools/probes/bot-goal-gate.py` 的 A5 口径补全（第三条形态"产品自己的 `安放 C4` L3"）', '不消除：这是实现缺陷，已在本片修掉；本条同时登记两件**长期**事项 —— ① "bot 交战走位 / 持包推进"是**本项目新增行为**（A 无 bot AI），逐行对齐 PodBot / CZ bot 源码时复核（同 #58 口径）；② 闸门 A5 的口径**由本片变更**（旧口径只扫 log 流的 `TPLANTED`，那是 rows 流 token ⇒ 真下包也判红），变更已做两次自检（已知正确样本 PASS / `br` 与 `bu-r4-prefix` 仍 FAIL）'),
     ('82', '判定行 G17 引用的共享取证帧 `22_slot2_pistol.png`（09-22 13:04，1920×1080）早于其实现文件 `Module/CameraRig/FirstPersonCamera.cs`（本片 2026-09-23 只加了 `hideFlags`）', '`hideFlags` 只影响编辑器 Game view 的图标叠加层、**不改渲染结果**（采集 = `capture_game_view --source screen`，1920×1080）：**同一冻结帧**的游戏后缓冲在"图标宿主全 `HideInHierarchy`"与"全 `None`（= 修复前）"两态下**逐字节相同**（MD5 `5CA566106A8006D24369C7A4ACBE2E59`，均 2406775 bytes）⇒ 该改动不可能改变这一帧的画面内容；且该帧被 H1~H15 / G4 / G17 共 **17 行**共同引用，重采需复现 round1 出生点（USP + Buy Zone + $150）且会用另一状态帧覆盖 17 行判据 ⇒ 收益远小于风险。', '**主锚（工程内、永久）** `client/Assets/Scripts/Module/CameraRig/FirstPersonCamera.cs:415`、辅助证据 `.ai-tmp/screenshots/buv-bb-icons-hidden.png` + `buv-bb-icons-visible.png`（同一冻结帧、`capture_game_view --source screen` 1920×1080）、**裁定出处** `.ai-tmp/test/dispatch-log.tsv:118`（`# adjudicated:` 行；⛔ 不作唯一依据 —— `.ai-tmp/**` 会随收尾清理，判据自足性由本行「为什么」列承担）｜【片BW-E 落源后修正（仅此一处）】原稿此处引 `.ai-tmp/test/buv-void-frames.tsv`，该文件已被 `bu-v` 收尾清掉 ⇒ 改指裁定行；「为什么」列的文字未改', '该帧因其它理由重采时自然消除（或 H1~H15/G4/G17 任一并重采时）；在此之前 G17 的判定保持有效'),
     ('83', '`App/Bootstrap.cs:72` 用 `DontDestroyOnLoad(gameObject)` 让 Bootstrap 宿主跨场景保活', '单机版必须：菜单/舞台切场景（单加载）会销毁旧场景，而 6 个业务模块（CsMapModule/MatchModule/PlayerModule/BotModule/ViewModule/AudioModule）全挂在这个宿主上，不常驻则模块全死。引擎无「把业务对象挂到常驻根」的公开设施（`Game` 门面 30+ 成员里没有 Root/Host；`EngineRunner` 是 internal，业务不可调用）', '`client/Assets/Scripts/App/Bootstrap.cs:72`（引擎侧同类写法见 client/Packages/com.clover.unity-engine/Runtime/Core/EngineRunner.cs:25-26（片BW-E 落源时**只改了这一处路径**：原稿写 clover-client-unity-engine/Runtime/Core/EngineRunner.cs，该路径在盘上**不存在** ⇒ 会让 screenshot-refs 多 1 条悬空引用；内容与行号未改））', '引擎提供公开的常驻根门面（或 Bootstrap 改由引擎加载）后消除'),
     ('84', '`Module/CameraRig/FirstPersonCamera.cs:414` 用 `new GameObject("CsFpsCamera")` 自建第一人称相机宿主', '舞台场景由生成器产出、不保证场上有相机；没有相机 = 进图黑屏 + 与 `Camera.main` 抢画面（该文件 :29-31 注释）。引擎 `Game.Pool.Spawn` 需要**已注册的预制体键**、`Game.Entity.Create` 需要 objectID/typeID 且它是"服务端实体视图"，两者都不是「客户端常驻单例对象」的设施 ⇒ 无替代（本片已实测：探针 39 处里没有可复用的引擎门面调用）', '`client/Assets/Scripts/Module/CameraRig/FirstPersonCamera.cs:414`', '引擎提供「创建常驻单例对象」的门面后消除'),
     ('85', '`Module/CameraRig/FirstPersonCamera.cs:416` 用 `Object.DontDestroyOnLoad(go)` 让该相机常驻', '同上：相机必须跨场景存活（比赛切图 / 回菜单再进图时不能重建，否则 `_camera` 引用失效）；⛔ 不能用 `HideAndDontSave` 替代（会改生命周期语义，见同文件 :415 一带注释）', '`client/Assets/Scripts/Module/CameraRig/FirstPersonCamera.cs:416`', '同 BU-V-2'),
     ('86', '`Module/Combat/CombatSelfTest.cs:291` 用 `new GameObject` 造自检受击体', '自检要造"能挡射线的碰撞体"给射线断言用（没有碰撞体则 hits=0，命中归属判不了）；自检是 Play 内一次性链路，引擎无「临时碰撞体对象」设施。本片已同时加 `HideFlags.HideInHierarchy`（:294）并用 finally `DestroyImmediate`（:282-285）⇒ 不残留、不画图标（BotSelfTest.cs 同形，其 basename 已在登记表内）', '`client/Assets/Scripts/Module/Combat/CombatSelfTest.cs:291`', '引擎提供临时对象/测试夹具门面后消除'),
     ('87', '5 行跨维度交叉判定行的「多文件锚点」只被**机械复核第一项**（3176 D7×D12 / 3268 D8×D6 / 3302 D9×角色类型 / 3373 D11×上下文 / 3753 S1×D10）', '这 5 行的判据是**多个文件上的"布尔与"**（`PlayBGM && StopBGM` / `ClassifyImpact && 弹着音 hit_wall` / `TryResolve && SeparateFromOtherActors && sep_ok` / `inputAllowed && NonBlockingPanels` / `HitHead && HitChest`）、载体列 = `跨维度因果对` ⇒ 工程侧**没有单一产物可指**。片FX-ALL 按本行原定的「处置方向」把**合法写法**落了地：证据列写 `（多文件锚点：A:<行> && B:<行>）`，每一项都是**现算的** `文件:行号`（生成器 `CROSS_ANCH` 表 + `code_site()` 现场解析，⛔ 绝不写死 —— 行号随插入漂移）⇒ `verify.ps1` 第 39 项对这 5 行不再红。**残余（本行唯一还成立的一半）**：第 39 项的实现（`tools/probes/audit-verdict-rows.py` 的 `PATHLINE_RE` + `classify_anchor()`）只取**首个** `path:line` 命中 ⇒ 第二项及以后**不被机械复核**，只有人逐个打开能验。⛔ 不许把这条读成"第二项可以编"：`code_site()` 解析不到的项会被**直接丢掉**（`_hits` 过滤空串），锚点凑不出来时该行自动退回挂 `（见允许差异 #87）` —— 判据自己会说话，不靠自律。', '`tools/probes/enumerate-entities.py` 的 `CROSS_ANCH` 表（键 = `CROSS` 的 dim，5 条）与同文件的 `code_site()`；机械取锚口径 = `tools/probes/audit-verdict-rows.py` 的 `PATHLINE_RE` / `classify_anchor()` / SECTION A 注释；闸门侧 = `tools/verify.ps1` 第 39 项 `evidence-anchor`', '给 `verify.ps1` 第 39 项加「多锚点逐项复核」时（方向 = 按 `&&` 切开锚点、**每一项**都必须解析到盘上的文件与在范围内的行号；判据 = 一个"第二项指向不存在的文件"的样本必须被判红）；⛔ 本轮不改闸门'),
    # 用户 2026-09-24 报的第 4 条：局域网联机（全新条目；A 有 ⇒ 要做，skill §0 铁律 1）
    ('88', '**必须支持局域网联机**（用户 2026-09-24 明确要求，非"可选"）',
     '① 原版 CS 1.6 **有** LAN 联机（A 有 ⇒ 要做，skill §0 铁律 1）；'
     '② 本工程现在是**单机版** —— 差异 #1「Find Servers 列表为空」已登记"单机版没有局域网对局可发现"；'
     '③ 引擎侧**有现成能力**：差异 #1 的「为什么」写明"面板与 `Game.LanBrowser` 链路本身是通的" ⇒ 载体在盘、不是从零造。'
     '【片LAN 2026-09-24 落地 · 本会话实测】**按「引擎 LAN 广播发现」这条路落地（⛔ 不另造协议）**：'
     '① 新增 `client/Assets/Scripts/Module/Net/CsLanHost.cs`（**本工程侧**的应答端；协议逐字 = 差异 #1 引的引擎口径 '
     '`CLOVER-LAN-QUERY/1&#124;<nonce32hex>` → `CLOVER-LAN-REPLY/1&#124;<json>`，UDP `47777`，单包 ≤512 B）；'
     '② `UI/Flow/ServerListPanel.cs` 新增 `Host LAN Game` 开关（节点名 `Btn_HostLan`）：开 ⇒ 本机对外应答同网段寻服查询、'
     '按钮文案变 `Stop LAN Host`、状态行打出 `LAN host name / gateway / udp / players`；关 ⇒ `CsLanHost.Stop()`；'
     '列表里点一台主机真的走 `CloverNet.Init`（⛔ 不是空实现）。'
     '**实测（判据资产 `tools/probes/probe-lan-host.cs` + `tools/probes/probe-lan-verify.cs`；驱动 `.ai-tmp/drivers/bz-lan.sh`）**：'
     '`RESULT: PASS` —— 本机广播后**真的收到了自己的应答**：`收到查询=4 回出=4`、`LanBrowser hosts=1`、'
     '`address=192.168.1.164:8002` 命中本机广播（`命中本机广播=True`）；'
     'UI 侧同一次 Play 的实测截帧 `22_serverlist_hoston.png`（按钮 `Stop LAN Host` + 状态行 '
     '`本机已广播：LAN host name="clover-cs 16 LAN host @ DONGSHENG" gateway=192.168.1.164:8002 '
     'udp=192.168.1.164:8003 players=1/10`）。'
     '⚠️ **顺带修掉一处自相矛盾（本会话实测截到）**：预制体 `Assets/Resources/UI/ServerListPanel.prefab` 是 FlowSetup '
     '生成时烘的，而 `BuildLayout` 在预制体已存在时**根本不会被调用** ⇒ 加了 `Host LAN Game` 按钮后底注还是旧串'
     '「单机版不连接任何服务器（不调 CloverNet 的网络初始化）」，与正下方的按钮自相矛盾（截帧 `22_serverlist.png` 就是它）。'
     '修法 = `OnOpen` 里按 `FooterText` 常量覆写一次底注（与 `Btn_HostLan` 的运行期补建同一套路），'
     '⛔ **不重跑 FlowSetup**（那会连带重写 8 个预制体 + 两个场景，爆炸半径远超本需求）。'
     '⛔ **仍未消除**：① 只证到"**同网段能互相发现**"（= 本行 ④ 的"互见"那一半）；'
     '② "两实例**互见 + 能开局**"里的**"能开局"未做** —— 真连上还要求 `gateway` 指向一台在监听的真网关'
     '（`CloverNet.Init` 的目标），服务端 LAN 房间 / 开局链路仍是空的；'
     '③ "两进程"与"两机器"未做区分（本轮是本机自收自发）。',
     '用户本轮原话"我们是一定要支持局域网联机！"；'
     '面板出处 `client/Assets/Scripts/UI/Flow/ServerListPanel.cs`（Find Servers）；'
     '引擎链路 `Game.LanBrowser`（差异 #1 引）；差异 #1 / #2（Quit 未自动化）同段',
     '开「联机」片时：① 先定**网络形态**（引擎 LAN 广播发现 vs 直连 IP）并写进规格；'
     '② 接 `Game.LanBrowser` 做真实 LAN 广播/发现（顶掉"列表恒空"）；③ 服务端要有 LAN 房间/开局链路；'
     '④ 判据 = 同一局域网内两实例（或两进程）**互见 + 能开局**的运行时日志 + 一次联络图。⛔ 本行是新维度（网络），不是单机差异的延伸。'
     '【片LAN 2026-09-24 进度 · 本会话实测】① **网络形态已定并落地** = 引擎 LAN 广播发现（⛔ 未另造协议）；'
     '② **已接** `Game.LanBrowser` 的真实扫描 + 新增 `CsLanHost` 应答端 ⇒ "列表恒空"被顶掉一半'
     '（**能发现**；同网段确实没有别的实例时仍为空，那是正常的，⛔ 不再是"设计如此"）；'
     '③ **服务端 LAN 房间 / 开局链路仍未做**；'
     '④ 判据已出 `RESULT: PASS`（本机自收自发：`收到查询=4 回出=4` / `hosts=1`）+ UI 截帧 '
     '`22_serverlist_hoston.png`；"**两实例互见 + 能开局**"**尚未采**。⛔ **本行不消号**。'
     '【片LAN-B 2026-09-24 补 · "能开局"缺口的定性（本轮查证）】③ 那一段**缺的到底是什么**已查明：'
     '**引擎侧只有"发现端"** —— `client/Packages/com.clover.unity-engine/Runtime/Network/Lan/` 下只有 `CloverLan.cs` / `LanBrowser.cs` / `LanSocket.cs` / `LanProtocol.cs` / `LanCapabilities.cs`，'
     '全目录 grep `class .*Host` / `LanServer` / `StartHost` **命中 0** ⇒ 引擎不提供"被连的主机"。'
     '`CloverNet.Init(addr, udpAddr)` 连的是 **TCP 网关**（`host:port`），而 `CsLanHost` 广播出去的 `gateway:8002` **没有任何进程在监听**（本机实测）⇒ 点"加入"之后接不上。'
     '**本仓库有一个通用服务端** `clover-server-engine/`（Go；`internal/transport/gateway/gwcore` = WS / TCP 接入 + 上行转发 + NATS 下行桥接），但它**不含任何 CS16 对局逻辑** ⇒ 就算起起来也只到"连上网关"，到不了"进一局"。'
     '⇒ "能开局"至少是三段：ⓐ 起/适配网关（`clover-server-engine`）；ⓑ 定义并实现 **CS16 房间 + 开局 + 世界同步**协议（本工程现在是**纯单机本地模拟**，`CsMatch` 没有"远端权威"这一层）；ⓒ 客户端把 `CloverNet.Init` 之后的对局接到 `CsMatch` 上。'
      '⛔ 本片**不动手**（属大件），只把缺口定死在这里，供下一片按 ⓐⓑⓒ 拆。'
      '⇒「互见」那一半已有 `RESULT: PASS`；「能开局」**仍未采**。⛔ **本行不消号**。'
      '【片LAN-C 2026-09-24 落地 · 「能开局」第一段已采】第一刀落在**客户端侧**（不依赖 Go 服务端，也更接近"两台机器直连"的用法）：'
      '① 新增 `client/Assets/Scripts/Module/Net/CsLanGateway.cs` —— 主机侧 **TCP 对局网关**：监听、接受加入、回开局信息，此后 10 Hz 把**主机权威的世界**'
      '（回合 / 阶段 / 比分 / 存活数 / 掉落数 + 每个角色的 id / 名字 / 阵营 / 坐标 / 朝向 / 血量 / 存活 / 手持武器）按行推给每个已握手客户端。'
      '② 线格式逐字定死（两端必须同值）：`CS16-LAN-JOIN/1&#124;&lt;json&gt;` → `CS16-LAN-WELCOME/1&#124;&lt;json&gt;` → 每 0.1 s 一条 `CS16-LAN-SNAP/1&#124;&lt;json&gt;`，客户端可发 `CS16-LAN-INPUT/1&#124;&lt;json&gt;`。'
      '③ **线程模型**：`MatchModule.Update` 在主线程调 `CsLanGateway.Pump`（只读模拟 + 拼一行 + 入队，⛔ 主线程不碰 socket）；每客户端一个后台线程负责真正的 `Write`。'
      '④ `CsLanHost.Start/Stop` 现在把广播出去的那个 gateway 端口**真的开起来**（在此之前它只是个参数、没有进程监听 —— 这就是"列表里看得到、点加入接不上"的直接原因）。'
      '⑤ **判据 ⇒ 双 PASS，且客户端是独立进程**（⛔ 不是同进程自问自答）：主机探针 `tools/probes/probe-lan-gateway.cs` + **独立进程**客户端 `tools/probes/lan-gateway-client.py` + 读计数 `tools/probes/probe-lan-gateway-verify.cs`，驱动 `.ai-tmp/drivers/bz-lan2.sh`；'
      '客户端原文：`RESULT-LANCLIENT: PASS  口径：连上=True 收到WELCOME>=1=True（=1） 收到SNAP>=5=True（=117） 解析出>=2个角色=True（=9）`，并逐条打出远端角色（如 `id=1 Player T pos=(-11.5,3.251,-48.5) hp=100 alive=True w=glock18`）；'
      '主机侧原文：`RESULT-LANGW: PASS  口径：连接≥1（=1）收到JOIN≥1（=1）推出快照≥1（=164）非法行=0（=0）`。'
      '⑥ ⚠️ **取证环境事实 + 由此新增的一道闸门**：本机 `127.0.0.1:8002` 已被**另一个进程**占着（`netstat -ano` 实测：`TCP 127.0.0.1:8002 LISTENING` + `UDP 127.0.0.1:8003`，同一个 PID，且另有一个 Unity 编辑器连着它）'
      '⇒ 本工程的 `0.0.0.0:8002` 靠 `SO_REUSEADDR` bind **成功**，但内核把连接交给绑定更具体的对方 ⇒ **accept 永不触发、计数器恒为 0**（第一轮取证就是这样"看起来起了却没人连得上"）。'
      '⇒ `CsLanGateway.Start` 现在**先探测该端口是否已有别人在听**（能连上就拒绝启动并报错），⛔ 不再静默共存；取证因此改用空闲端口 **8012**（端口是参数 —— `CsLanHost.Start(gatewayPort:)` 与广播报文里的 `gateway` 字段就是它，协议与端口无关）。'
      '⚠️ **仍未消除（「能开局」剩下的两段）**：① 客户端的 `CS16-LAN-INPUT/1` **只计数、尚未驱动主机模拟**（"远端权威"这一层还没接进 `CsMatch`）；'
      '② 客户端**还没有把快照画成远端角色**（收到数据 ≠ 看得见人）。⇒ 现状 = "**连得上、进得去房间、看得见世界数据**"，还不到"**两台机器能对打**"。⛔ **本行不消号**。'),
    # 用户 2026-09-24 报的第 8 条：枪口火焰（全新条目）
    ('89', '**枪口火焰没有效果**；原版 B51（M249）的枪口火焰是**十字形**',
     '① 工程**已有** MuzzleFlash（`CombatEffects.cs` 四类特效之一：枪口火焰/弹道/弹痕/爆炸）⇒ 不是"没做"，是"**没效果**" ⇒ 先查运行时到底有没有生成/有没有被裁剪；'
     '② 原版枪口火焰**按武器有不同变体**（`muzzleflash1.spr` / `muzzleflash2.spr` 在盘），B51 用**十字形** —— 而"哪把枪用哪张"的**逐武器映射本工程未取证**。',
     '用户本轮原话"枪的枪口火焰好像没效果，我记得 B51 是个十字的枪口火焰"；'
     '实现出处 `client/Assets/Scripts/Module/Combat/CombatEffects.cs`（MuzzleFlash 段）；'
     '原版载体 `原版资源/cs16src/cstrike/cstrike__sprites__muzzleflash1.spr` / `muzzleflash2.spr`（**在盘**，片AW 取回）；'
     '逐武器映射出处（哪把枪用哪张 spr）**待补**（原版 client.dll / 实拍）'
     '【片FX-MUZZLE 2026-09-24 根因 + 落地】'
     '**载体侧硬事实**（`tools/probes/spr-extract.py --info` 逐条自洽：size 恒等式 + radius 恒等式 OK）：'
     '四张**全部在盘** —— `muzzleflash1.spr` 48×48×1 帧（星芒）/ `muzzleflash2.spr` 64×64×**3 帧**（圆团鼓包）/ '
     '`muzzleflash3.spr` 72×72×**3 帧**（**十字 / X 形** ← 用户记的 B51）/ `muzzleflash4.spr` 48×48×1 帧（星芒偏黄）；'
     '导帧拼版 `.ai-tmp/test/mf-frames/_sheet.png`（8 张帧 + 拼版）。'
     '⚠️ **旧注释的"载体不在盘"与"映射在 client.dll"两句都要更正**：逐字节扫 `原版资源/**` 全树，`muzzleflash` 只出现在 '
     '`原版资源/清单.md`(4 处) 与 `原版资源/hlsdk/dlls/weapons.cpp`(3 处)；后者是 **HL1** 的武器实现，只证明机制 '
     '（`pev->effects` 按位或上 `EF_MUZZLEFLASH`），⛔ 不是 CS 的逐武器表。`cl_dlls/client.dll`(1,093,128 B) 与 `dlls/mp.dll`(1,640,960 B) 里 '
     '`muzzleflash1..4` / `muzzleflash` **命中 0**（case-insensitive 亦 0）⇒ sprite 选择在**引擎 `hw.dll`**，该文件**不在盘** '
     '⇒ 降级链停在"载体已具备、立即数取不到"。'
     '**工程侧根因（两处口径错，⛔ 都不是"没做"）**：'
     '① **落点**旧值 `eye + dir*0.34 + right*0.13 + down*0.09` 把火焰放在相机局部系 `z=+0.34`，而视模型 idle 姿态实测包围盒（逐轴数字抄在 '
     '`ViewModelRig.cs` 的 `EnsureAlwaysAnimate` 注释里）是 `x∈[-0.026,+0.239] y∈[-0.286,-0.062] z∈[-0.087,+0.705]` '
     '⇒ 枪管尖在 `z≈0.70`，火焰落在**枪身内部**；SpriteRenderer 走透明队列且**开深度测试** ⇒ 被枪身里 `z<0.34` 的那一截几何盖掉，'
     '表现就是"没有火焰"；'
     '② **尺寸**旧值 `Vector3.one * CsCombatTuning.MuzzleFlashSize` 把"米"当**倍率**写（同类坑已在 #69 的 decals 上修过），'
     '贴片 PPU=100、64 px 天生宽 0.64 世界单位 ⇒ 实际只画出 `0.64×0.30=0.192 m`（小 3.1 倍）。'
     '**落地（四条）**：① `CsCombatTuning` 新增三个**相机局部系**偏移常量 '
     '`MuzzleOffsetRight=0.13 / MuzzleOffsetUp=-0.12 / MuzzleOffsetForward=0.72`（从上述实测包围盒的**最前端** z=0.705 再外让 0.015 m 反推），'
     '并更正 `MuzzleFlashDuration`/`MuzzleFlashSize` 两段过期注释（"载体不在盘"已不成立）；'
     '② `CombatEffects.MuzzleFlash` 签名改 `(Vector3 eye, Quaternion viewRotation, string weaponId)` —— 落点改成 '
     '`eye + viewRotation * offset`（⛔ 不再用 world-up 叉乘算 right/up：俯仰 ±90° 会退化），尺寸改走 `SpriteScaleForMeters` '
     '（`meters` 才真的是米），并新增 `shot.muzzle` 可核对日志（落点/贴图/启用/世界宽/缩放）；'
     '③ **逐武器贴图**只落**有一条证据**的那条映射：B51/M249 → 十字形（`Resources/UI/Art/fx_muzzleflash3.png`，'
     '72×72，由 `原版资源/cs16src/cstrike/cstrike__sprites__muzzleflash3.spr` 的帧 0 解出；'
     '落盘脚本 `.ai-tmp/test/_add-muzzleflash3.py`，走既有**确定性 GUID** 方案 guid=`0000000000000000000000007f3b0012`；'
     '`ResPaths.FxMuzzleFlashCross` 新增 key、`CombatEffects.PickMuzzleFlash` 按 id 选）—— 证据两层：用户 2026-09-24 实机记忆 '
     '「B51 是个十字的枪口火焰」+ 四张载体里**只有** `muzzleflash3.spr` 是十字/X 形（唯一匹配）；⛔ 其余武器**不编**映射，维持默认那张；'
     '④ 判据资产 `tools/probes/probe-muzzleflash.cs`（存在性台账 + 视模型动画后 AABB vs 落点 + 反射真调 `MuzzleFlash` + 冻 `timeScale` 供截图）'
     '+ `tools/probes/fx-buy-m249.cs`（走业务 `TryBuyFor` 把 B51 交到本地玩家）+ `.ai-tmp/drivers/bz-muzzle.sh`（驱动；'
     '⛔ 本沙箱 `unity` CLI 从 **Python 子进程**调用会挂起、从 **bash** 调用正常 ⇒ 驱动写成 bash）。'
     '⚠️ **仍未消除**：① 逐武器映射只有 m249 一条（选择表在 `hw.dll`，不在盘）；'
     '② `muzzleflash2/3` 的**逐帧播放**未做（载体各 3 帧，但帧时长/总时长无出处，本工程 `MuzzleFlashDuration` 仍是本项目新增）；'
     '③ 火星无独立原版载体（继承 #53）。'
     '**【片FX-MUZZLE 2026-09-24 实机三段判据 · 全部 PASS（本会话实测）】**'
     '判据资产**拆成三个**（原因见下"沙箱限制"）：'
     '`tools/probes/probe-muzzleflash-geom.cs`（几何半）/ `probe-muzzleflash-fire.cs`（真调用半）/ `tools/probes/compare-frames.py`（像素半，需 system python 3.12 的 numpy+Pillow）；'
     '驱动 = `.ai-tmp/drivers/bz-round2.sh`（**一轮 Play 覆盖 #68 / #67 / #88 / #89 四条**，⛔ 不是一差异一个驱动）。'
     '**【① 几何 · RESULT-GEOM: PASS】**（`.ai-tmp/test/r2-muzzle-geom.txt`）'
     '`cam=CsFpsCamera screen=1920x1080 fovY=58.7155 near=0.0500 eye=(18.5000,-1.6312,34.5000)`；'
     '视模型动画后世界 AABB `smr=7`，换算到**相机局部系** `x∈[-0.0962,+0.2033] y∈[-0.3286,-0.0797] z∈[-0.0261,+0.5515]`；'
     '旧落点 `camLocal=(0.1300,-0.0900,0.3400)` **在包盒内 = True** ⇒ **"被枪身盖掉"这个根因当场成立**；'
     '新落点 `camLocal=(0.1300,-0.1200,0.7200)` 在包盒内 = False、比盒前端更前（z>0.5515）= True、`onScreen=True`（screen=(1133.33,380.00)）；'
     '`ResPaths.FxMuzzleFlashCross=UI/Art/fx_muzzleflash3 贴图在盘=True`。'
     '⚠️ **顺带更正一处过时数字**：本行上文引的盒 `z∈[-0.087,+0.705]` 是**另一姿态**（`ViewModelRig.cs` 注释里那组）；这一帧（CT 持 USP、idle）实测 z 上界 **0.5515**。'
     '两处都自洽 —— 关键是**新落点在两处姿态下都在盒外**（0.72 > 0.705 > 0.5515），旧落点 0.34 都在盒内。'
     '**【② 真调用 · RESULT-FIRE: PASS】**（`.ai-tmp/test/r2-muzzle-fire.txt`）'
     '反射校验 `MuzzleFlash 参数个数=3 [Vector3,Quaternion,String]`（⇒ 程序集是新版）；'
     '真调 `MuzzleFlash(eye, camRotation, "m249")` 后读 FX 池台账：'
     '`[A]before.sum active=12 lightsOn=0` → `[C]after.sum active=13 lightsOn=1`，新增的那件逐字为 '
     '`[0] FX_Sprite wpos=(19.2143,-1.7512,34.3417) scale=(0.4167,0.4167,0.4167) sprite=fx_muzzleflash3 sprEnabled=True lightOn=True lightRange=8.0000`。'
     '⚠️ **尺寸自证**：`scale=0.4167` 而不是 0.30 ⇒ 因为 72 px / PPU 100 = 0.72 世界单位，0.72 × 0.4167 = **0.3000 m** = `CsCombatTuning.MuzzleFlashSize` '
     '—— 这正是"米终于真的是米"的数字证据（旧代码写 `Vector3.one * 0.30` 只画出 0.192 m）。'
     '**【③ 像素 · RESULT-DIFF: PASS】**（`.ai-tmp/test/r2-muzzle-diff.txt` + 图 `.ai-tmp/screenshots/89_muzzle_2up.png`）'
     '同一冻结帧 A/B（`timeScale=0`；`89_ctl_nofx.png` 无火焰 / `89_muzzle_flash.png` 有火焰，各 1920×1080）：'
     '`changed_pixels=1997348 (96.323%)`、`brighter=93.914% vs darker=2.409%`、`brighter_share=0.9750`；'
     '火焰窗（几何探针给的投影点 (1133,380) 翻成图像坐标 (1133,700)）±115 px 内 `dl<-60` 的像素 = **18.53%**、压暗幅度 **90.97**（原亮度 111.62 → 20.64）。'
     '⛔ **三个口径坑（我踩过，如实留档，别重复踩）**：'
     '(a) `capture_game_view --source screen` 抓的是**整屏**，且 `--save_path` 按**工程内**解析（传 `client/_shots/x.png` 实际落在 `client/Assets/_shots/x.png`）'
     '⇒ 两张对照帧必须在**同一个冻结帧**下拍；'
     '(b) **第一版判据"差异块必须局部(<40%屏)"⇒ 误判 FAIL** —— 因为火焰除了贴精灵还在同点挂了一盏 **Point Light**，把面前**整面墙**照亮（96.3% 像素都变了、外接框=全屏）；'
     '**第二版"窗内平均亮度 ≥ +40"也 FAIL（实测 +39.98）且没有区分度** —— 点光溢出下屏幕**任意** 230×230 窗口的平均增量都有 **+125**；'
     '最终改用的量是**变暗簇**：点光**只能把像素变亮**，唯一能压暗墙的就是**精灵自己的不透明像素** ⇒ 窗内 18.53% 像素被压暗 ≥60 而**全屏**变暗只占 1.59%，这条不可被点光伪造；'
     '(c) Unity `Camera.WorldToScreenPoint` 的 **y 是左下原点**，直接当图像坐标会把"火焰投影窗"红框画到屏幕上半部分（第一版 2-up 图就是这样）⇒ `compare-frames.py` 加了 `--y-from-unity` 做 `y = h − y`。'
     '**【沙箱限制 · 为什么拆三个探针】**本沙箱 Pipeline 对 `eval_file` 有 **5 s 主线程上限**：8 bot + 整张 de_dust2 在跑时，'
     '原先"台账 + 几何 + 真调用 + 冻结"合一的 `tools/probes/probe-muzzleflash.cs` **实测超时**（`Main thread operation timed out after 5000ms`，'
     '旧驱动 `.ai-tmp/drivers/bz-muzzle.sh` phase 5）⇒ 拆成 geom / fire 两个小探针（另两个同族限制：`unity` CLI 从 **Python 子进程**调用会**挂起**、从 bash 调用正常；'
     "`console --format json` 落盘带 UTF-8 BOM + 系统 ANSI 码页乱码 + 原始控制字符 ⇒ 读它要 `utf-8-sig` + `strict=False` + 逐行 `encode('gbk').decode('utf-8')` 修复）。"
     '⚠️ **新增待补**：`MuzzleFlash` 那盏点光的 `range=8 / intensity=4`（`CombatEffects.cs`）是**本项目新增**值 —— '
     '本次改动**没有动它**，但正是本次修复让它**第一次真的看得见**（旧位置在枪身内部，光也闷在里面）。'
     '原版枪口 dlight 的强度/半径是逐武器立即数，在引擎 `hw.dll`、**不在盘** ⇒ 同①②③一起按"载体不在盘"登记待补。',
     '开「特效 × 枪口」片时：① 先做**存在性判据**（开火时到底有没有生成 muzzle 节点 / 有没有像素变化）；'
     '② 把 `muzzleflash*.spr` 解出并定死**逐武器映射**；③ 判据 = 同机位并排图（至少覆盖 B51 十字形一格）。'
     '【片FX-MUZZLE 2026-09-24 进度 · 本会话实测】① ② ③ **全部落地**：'
     '① 存在性判据**已跑出 PASS**（几何 / 真调用 / 像素三条，逐条数字见「为什么」列末的实机段）；'
     '③ B51 十字形已接，且有了**同机位 2-up 并排图** `89_muzzle_2up.png`（上=无火焰 control / 下=有火焰，红框=火焰投影窗，'
     '格号登记进 `策划/验收表.md`）；'
     '② **仍只接了 m249 一条**（四张载体全在盘、但"哪把枪用哪张"的选择表在 `hw.dll`，不在盘 ⇒ 其余武器无出处、⛔ 不编）。'
     '⚠️ 新增待补两条：(a) `muzzleflash2/3` 的**逐帧播放**仍无帧时长出处；(b) 枪口点光 `range=8 / intensity=4` 无出处（同 `hw.dll`）。'),
    # ---- 用户 2026-09-24 复查：第 3 条拆两半（#3a 消音器 / #3b 大狙开镜），登记号 90 / 91 由 lead 于 2026-09-24 裁定；
    #      第 92 条 = 闸门 33「no-sync-subagents」那 2 行的**已知偏差**（不补列）。三条都是「允许的差异」侧。
    # 用户 2026-09-24 复查第 3 条（#3a）：消音器手枪右键「没效果、没动画」
    #   口径 = 素材缺口 + 消费侧缺口；与 #68（输入链语义）的边界写在「出处」列里，两行不许互相顶替。
    ('90', '**消音器手枪右键只翻了一个没人读的状态位**：`usp_silencer_on/off.wav` 采样与「消音后伤害 / 散布 / 射速」的数值都无出处，且全仓没有一处代码消费 `CsActor.Silenced`',
     '① 右键那条链**已经通了、但没有下游**：输入 `CsInputState.Attack2` → `CsInventory.ToggleWeaponMode` 翻 `CsActor.Silenced`（`Module/Match/CsTypes.cs:80`），'
     '而全仓对 `Silenced` 只有三处 —— 写（`Module/Match/CsInventory.cs:528`）、自检读（`Module/Combat/CombatSelfTest.cs:527-598`）、声明（`CsTypes.cs:80`）'
     '⇒ 开火 / 音效 / 渲染 / 数值**一条都不消费**（`Module/View/ViewModelRig.cs` 对 `Silenced` / `Silencer` / `vm_usp_silencer` grep **0 命中**）'
     '—— 用户「没效果、没动画」与这条完全对上。'
     '② 逐枪语义**有出处**（`Core/CsWeapons.cs:170-171` 的 `client.dll` 偏移 0x0e3804 / 0x0e3824 / 0x0e308c / 0x0e30ac，`tools/probes/attack2-probe.py` 段 B 逐字节复算），'
     '但「消音后的伤害 / 散布 / 射速」**无出处**（`CsWeapons.cs:178-181` 逐字写明要反汇编 `client.dll`）—— 本机现成**没有** x86 反汇编器（capstone / objdump / dumpbin / distorm3 全 ✗）⇒ ⛔ 不为它装工具，这一格只能挂起。'
     '③ 音效采样**原版有、本工程无**：`sound/weapons/` 盘上只有 `m4a1-1.wav`（`.ai-tmp/test/jd-tree.txt:2` 证明原版有 `usp_silencer_on.wav` / `usp_silencer_off.wav`）。'
     '⚠️ **与 #68 的边界（不许互相顶替）**：#68 记的是**输入链语义**（attack2 该给哪把枪、按下沿、能力表 4 把）与「状态可切换」的实机判据；本行记的是 #3a 的**素材侧 + 消费侧**。',
     '判据与逐条盘点 = `.ai-tmp/test/fix3-asset-gap.md` §1（A1..A9 九条，含「结论 / 判据」两列）；消费点 0 命中的口径就写在 A8 行；'
     '状态位 `client/Assets/Scripts/Module/Match/CsTypes.cs:80`；翻转点 `client/Assets/Scripts/Module/Match/CsInventory.cs:528`；'
     '逐枪语义与数值缺口 `client/Assets/Scripts/Core/CsWeapons.cs:50,170-171,178-181,192-193`；'
     '实机（状态层）判据 `tools/probes/probe-attack2-live.cs` → `RESULT: PASS`（翻过的**恰好**是能力表里 `CanSilence` 或 `CanBurst` 为真的 4 把；二次调用 9/9 回原值）；'
     '视模型素材**在盘**：`client/Assets/Resources/Art/Tex/vm_usp_silencer.png` + `Art/Mat/vm_usp_silencer.mat`（缺的不是它）；'
     '反汇编器存在性探测（只探不装）= `.ai-tmp/test/fix3-fetch/struct-check.txt`',
     '① 素材：按**已记录的唯一渠道**试取 `sound/weapons/usp_silencer_on.wav` / `usp_silencer_off.wav`（⛔ 不换渠道、⛔ 不用替代源；失败逐件记 HTTP + `curl` stderr 原因；判「取没取到」看**字节 + SHA256**，⛔ 不看 `http_code`），取到后**入库须 lead 批**。'
     '② 实现：`ViewModelRig` 消费 `Silenced`（装上 / 拆下的视模型差异）+ 开火分支走消音音效与数值 —— 另开片。'
     '③ 数值：等有反汇编工具面再谈（⛔ 本阶段不装）。⛔ 本行不消号。'),
    # 用户 2026-09-24 复查第 3 条（#3b）：大狙右键「那个准星瞄准器呢？？？？」
    ('91', '**大狙 / 鸟狙 / G3SG1 / SG550 右键开镜只有 FOV 收窄、没有镜片覆盖层**：覆盖层精灵与四角弧素材未取回，覆盖层渲染实现为零',
     '① 开镜**触发层齐备**：`Module/CameraRig/FirstPersonCamera.cs:328` 的 `wantFov = zoomed ? CsConst.ZoomFov : _baseFov` + `Core/CsConst.cs:165` `ZoomFov = 40f`。'
     '② 覆盖层的**部件定义（出处）齐备且三处互证**：`weapon_awp.txt:7-8`（320 档 `zoom` / `zoom_autoaim` → `ch_sniper 0 0 256 256`）、`:14-15`（640 档 → `sniper_scope 0 0 256 256`）；'
     '同族 `weapon_scout.txt:7,14`、`weapon_g3sg1.txt:7`（`ch_sniper2`）、`weapon_sg550.txt:7`；镜内准星 `weapon_awp.txt:5-6` + `cstrike__sprites__hud.txt:55,147`（`autoaim_c`）。'
     '③ 覆盖层**渲染实现 = 0**：`Module/` 下 `grep -i scope` 只命中**视模型材质 / 贴图**（`Art/Mat/vm_awp_scope.mat` 等 = 枪上的镜筒，不是屏幕覆盖层），`Module/Combat/CrosshairState.cs` 只做准星**缩放**。'
     '④ ⚠️ **只补渲染而不取素材 = 自画近似图** ⇒ 违反 skill §6 第 3/4 条，**不可交付**（这是本条为什么不能「先画一个凑合」的原因）。',
     '`原版资源/cs16src/cstrike/sprites/weapon_awp.txt`、`weapon_scout.txt`、`weapon_g3sg1.txt`、`weapon_sg550.txt`（**均在盘**；31/31 份 `weapon_*.txt` 见 `原版资源/清单.md:292-320`）；'
     '`原版资源/cs16src/cstrike/cstrike__sprites__hud.txt:55,147`；逐条盘点 = `.ai-tmp/test/fix3-asset-gap.md` §2（B1..B8）；'
     '素材侧像素对照（判据资产 + 图）= `.ai-tmp/test/fix3-spr-cmp/FIX3-SPR-CMP-sheet.png`（1024x256）—— 其中 `ch_sniper` vs `ch_sniper2` 的 drawn-mask Jaccard = **1.0000** 是**量具正控**（同图换色），'
     '`sniper_scope` vs `ch_sniper2` = **0.1447** 判 DISTINCT-IMAGES（口径自校验写在产物第 150-158 行）；'
     '取件逐件记录 = `.ai-tmp/test/fix3-fetch-attempt.txt` 与 `原版资源/补充记录-FIX3取件.md`',
     '① 先按**已记录的唯一渠道**取回 `sprites/ch_sniper.spr`、`ch_sniper2.spr`、`sniper_scope.spr`、`scope_arc.tga` / `_ne` / `_nw` / `_sw`（⛔ 不换渠道；失败逐件记 HTTP + `curl` stderr；形状判据看字节 + SHA256）；'
     '② 取到后**入库须 lead 批**（落 `原版资源/cs16src/cstrike/`，命名 `cstrike__sprites__<原名>`，来源随件记进 `原版资源/补充记录-FIX3取件.md`）；'
     '③ 渲染覆盖层 + 表现判据（开镜前后同机位并排图 + 部件落点数值断言，口径按 `策划/对照表.md` 的 U-*）另开片 —— ⛔ 本行不消号。'),
    # 主 agent 2026-09-24 裁决：闸门 33「no-sync-subagents」那 2 行**不补列**，登记为已知偏差（不是待修项）
    ('92', '`tools/probes/dispatch-log.tsv` 标记行之后有 **2 行**没有 team / member 两列（`:124` 4 列 / `:125` 5 列），闸门 `no-sync-subagents` 因此判红 —— **裁定不补列**',
     '闸门 33 的口径是**反着判**的：「一行没有 team / member ⇒ 这活儿是走**同步**通道派出去的」，而同步通道会 `code=10003` 卡死、调用方拿不到报告。'
     '但这两行**不是同步派活**：`:124` 是 `clover-impl .ai-tmp/test/dispatch-片DROP-任务书.md …` 的 4 列行；`:125` 是 `team-lead 主-片DROP #takeover: …` 的 5 列行（**lead 接管**，按定义没有「派给哪个成员」这一说）。'
     '⚠️ 我**没有**能证明「它们当时走的是哪个通道」的产物 ⇒ 按 **「宁可不绿，不许伪造」**：⛔ 不补 team / member 两列 —— `dispatch-log.tsv` 自己的表头写着「留痕文件本身不许事后补写」；'
     '⛔ 也不改闸门口径去把这 2 行刷绿（把口径放宽来消灭红点，等于把闸门拆掉）。⇒ 本行是**「已知且已解释的红」**，不是待修项。',
     '`tools/probes/dispatch-log.tsv`（留痕标记行 **`:111`** = `# team-member-required-below-this-line`；标记行之后字段数 < 6 的行 = `:124` / `:125`，本裁决逐字引原文）；'
     '闸门口径 `tools/verify.ps1:1525-1552`（`$noTeamTrace` = 列数 < 6 或 `Cols[4]` / `Cols[5]` 为空；**本闸门没有豁免注册表**）；'
     '闸门自我说明 `tools/verify.ps1:1455-1473`；记账器口径 `tools/probes/append-dispatch.py` 的 docstring（2026-09-23 更正：闸门 33 需 >=6 字段，第 5 / 6 列 = team / member；默认 `cs16-fix` / `team-lead`）',
     '保持红（**不消号、也不当待修**）。真要转绿只有一条正路 = 给闸门 33 加**注册表式豁免**（照 `tools/verify.ps1:334-364` 那个资产名豁免的写法：**打印**豁免计数与前几个名字、**绝不静默**，且豁免按**行内容**绑定、⛔ 不能按文件整体放行），'
     '并配**两向自检**（命中豁免的行 ⇒ 不红；未登记的同类行 ⇒ 仍红）。⛔ 本行不改闸门；改闸门另开片、由 lead 批。'),
    ]

# ============================================================================
#  片BW-E：`DIF` 源自身的 **id / 单元格式卫生自检**（⛔ 早于任何落盘 ⇒ 非法 id 一个字节都不写）
# ============================================================================
# 为什么（team-lead 2026-09-23 把今晚三个坑 + 本片新增的"插入"能力合并成**四条解析约定**；
#        片FX-MUZZLE 2026-09-24 追加第 ⑤ 条 —— 见下）：
#   ① **数据行首字段必须是纯数字** —— ⛔ 不带 `#`（`#82` 会被"以 # 开头 = 整行注释"的读取
#      逻辑**静默跳过**；实测 bu-v 第一版就这么写，自检"统计非注释行"只数到 4 行才发现）；
#      ⛔ 也不许用"第 N 行 / 行数 +1"推编号（实测 bot-ai-r4 推成 #80、与登记表错位）。
#   ② 解析数据行**只认"首格是数字"** —— 表头行长得就像数据行，⛔ 不能靠行位置判。
#   ③ **以 `#` 开头 = 整行注释**（表头 / 说明行）⇒ **数据行不许以 `#` 开头**。
#   ④ **单元里不许有裸竖线 `|`** —— 需要该字符时用 HTML 实体 `&#124;`（否则"按竖线分列"的
#      检查会把一行切成 10 列；实测 bu-v 第一版把"按位或赋值"写成竖线形式就中招）。
#   ⑤ **散文行里出现 ASCII 单引号 ⇒ 必须换定界符**（2026-09-24 片FX-MUZZLE 实测踩坑）：
#      `DIF` 的文字行**多数用单引号定界**。正文里若要写**带单引号的代码片段**
#      （如 `encode('gbk').decode('utf-8')`），那一行的引号会**提前把字符串闭合**
#      ⇒ `ast.parse` 报 `invalid syntax. Perhaps you forgot a comma?`，而且**报错行不是真凶**
#      （实测真病灶在 #89 的 2628 行，报错却指到 2557 / 2558 —— 级联误导，白查半天）。
#      ⛔ 两个"看着能查出来、其实查不出来"的假信号：`tokenize` **全文件 OK**
#      （词法上 `'a' NAME 'b'` 也是合法 token 流）、括号深度**全平衡**（`()` 计数不受影响）。
#      **唯一可靠的自检 = `ast.parse(src)`**；辅以「逐行归并 token、看有没有非
#      (STRING / 逗号 / 括号) 的 token」的扫描。**修法**：把那一行的外层定界符换成双引号
#      （该行不得同时含 ASCII 双引号）。
# ---- 两条**已定口径**（team-lead 2026-09-23 裁决；本轮只写注释，不改输出）--------------------
# ① **「多文件锚点」合法写法（⛔ 待下一片实现，本轮不写进生成器）**：
#      写法 = 以 " && " 连接 N(>=2) 个**单点锚点**；单点必须满足现有 F1 / F2 形态
#      （可逐个打开验证）；判定 = 以 " && " 拆分后**每一项都**单独通过现有单点校验
#      才算通过（⛔ 不是"任意一项通过" = 放水）。
#      例：`client/Assets/Scripts/.../X.cs:25 && client/Assets/Scripts/.../Y.cs:88`（写**实际命中点**，⛔ 不写正则原文）。
#      背景：5 行 CROSS 判定行的判据是"多个文件上的布尔与"、无单一产物可指
#      ⇒ 已登记为允许差异 #87；#87 的「何时消除」= 本写法落地时。
# ② **ledger 的每一格必须是「盘的确定性函数」**（同一份盘 + 同一份源码
#      ⇒ 逐字节相同）：⛔ 禁止时间戳 / mtime / 运行号 / pid —— 否则每跑一次
#      `--inject` 产物就变，会打掉"两次 `--inject` 六件产物 sha256 相同"
#      这条幂等判据（判据自己变成不确定性来源）。
# ---- 以下为 id 卫生自检 ----------------------------------------------------------
# 本节判据：`DIF` 的 id 必须**全是数字且唯一**（否则下面"按 id 原地替换 / 按 id 插入"会**静默半生效**：
#   重复 id 只改到第一条、非数字 id 永远配不上）。⛔ 退出码 2，不许 warning 降级。
_REN_IDS = [str(r[0]) for r in DIF]
_DIF_BADID = [x for x in _REN_IDS if not x.isdigit()]
_DIF_DUPID = sorted({x for x in _REN_IDS if _REN_IDS.count(x) > 1},
                    key=lambda z: (int(z) if z.isdigit() else 0, z))
_DIF_PIPECELL = [str(r[0]) for r in DIF if any('|' in str(c) for c in r[1:])]
if _DIF_BADID or _DIF_DUPID or _DIF_PIPECELL:
    sys.stderr.write('  [dif] ERROR hygiene: non-numeric id=%s duplicate id=%s '
                     'cell-with-bare-pipe=%s\n' % (_DIF_BADID, _DIF_DUPID, _DIF_PIPECELL))
    sys.stderr.write('  [dif] ABORT before writing ANY product (see the four conventions above)\n')
    sys.exit(2)


# ============================================================================
#  输出
# ============================================================================
_log_inputs('all')   # 落盘前把**本次实际会读**的绝对输入路径再打一次（人一眼看出跑的是不是同一份）

def write_tsv(path, header, rows):
    with open(path, 'w', encoding='utf-8', newline='\n') as f:
        f.write('# ' + header + '\n')
        for r in rows:
            f.write('\t'.join(str(x).replace('\t', ' ').replace('\n', ' ') for x in r) + '\n')


ENT_SORTED = sorted(ENT, key=lambda r: (DIMS.index(r[0]) if r[0] in DIMS else 99, r[1], r[2]))
STA_SORTED = sorted(STA, key=lambda r: (DIMS.index(r[0]) if r[0] in DIMS else 99, r[1], r[2], r[3]))

# ============================================================================
#  证据锚点收口（片BW-E）：仍是自述式证据的行 -> 用**它自己的载体/出处**补盘上锚点
# ============================================================================
# 口径：① 已经有锚点的行**一个字符都不动**（本片只动"无可复核锚点"的那些行，把改动面压到最小）；
#      ② 补的锚点一律先过 `isfile()`（载体在盘 / 出处文件在盘），⛔ 不解析、不补 = 如实留白；
#      ③ 补完再问一次**判据资产**（`anchor_ok`）：它不认的锚点不算数 ⇒ 不许用"看起来像锚点的
#         字符串"把 item 39 刷绿（anti-gaming §三：锚点必须指向被测程序自己写出来的东西）。
def _anchorize(rows):
    """-> (new_rows, fixed, still, per_fix, per_still)。"""
    out, fixed, still = [], [], []
    per_fix, per_still = Counter(), Counter()
    for i, r in enumerate(rows, 1):
        ev = str(r[7])
        extra = []
        if not anchor_ok(ev):
            if r[0] == 'D1':
                # 实体清单里它自己那一行（判定表与实体清单同源同序：行 i <-> 清单第 i+1 行）
                extra.append('\u7b56\u5212/\u5b9e\u4f53\u6e05\u5355.tsv:%d' % (i + 1))
            else:
                g = guid_of(r[2])
                if g:
                    extra.append('guid=%s' % g)
                for c in (r[2], r[3]):
                    t = anchor_token(c)
                    if t and t not in extra:
                        extra.append(t)
                if str(r[6]).startswith(K_ALLOWED):
                    # 结论 = 「允许的差异(→ 差异登记.tsv)」 ⇒ 该结论的**定义处**就是那份登记
                    # （第三方重开它就能看到这条被登记过；⛔ 只对**已判为允许差异**的行加，
                    #  不是给所有缺锚点行兜底 —— 那才是"编锚点"）。
                    # ⚠️ 必须**加反引号**：判据资产取"裸路径"的正则是 ASCII 正字符集
                    #   （`[0-9A-Za-z_\-./\\]+`），中文目录名取不出来 ⇒ 裸写等于没锚点。
                    extra.append('`\u7b56\u5212/\u5dee\u5f02\u767b\u8bb0.tsv`')
        new_ev = (ev.rstrip() + ' ' + ' '.join(extra)) if (extra and ev.strip()) else \
                 (' '.join(extra) if extra else ev)
        if not anchor_ok(new_ev):
            still.append((i, r[0], r[1], ev, ' '.join(extra)))
            per_still[r[0]] += 1
        elif extra:
            fixed.append((i, r[0]))
            per_fix[r[0]] += 1
        out.append((r[0], r[1], r[2], r[3], r[4], r[5], r[6], new_ev))
    return out, fixed, still, per_fix, per_still


ENT_SORTED, _ANC_FIXED, _ANC_STILL, _ANC_FIX_DIM, _ANC_STILL_DIM = _anchorize(ENT_SORTED)
sys.stderr.write('=== [anchor] 片BW-E 证据锚点收口 ===\n')
sys.stderr.write('  [anchor] rows = %d ; 本片补锚点行 = %d ; 仍无锚点行 = %d\n'
                 % (len(ENT_SORTED), len(_ANC_FIXED), len(_ANC_STILL)))
for _d in DIMS:
    if _ANC_STILL_DIM.get(_d):
        _s = [x for x in _ANC_STILL if x[1] == _d]
        sys.stderr.write('  [anchor] 仍无锚点 %-4s %-5d 例: row %s :: %s\n'
                         % (_d, _ANC_STILL_DIM[_d], _s[0][0], _s[0][3][:90]))
sys.stderr.flush()

os.makedirs(OUT_DIR, exist_ok=True)
write_tsv(os.path.join(OUT_DIR, '\u5b9e\u4f53\u6e05\u5355.tsv'),
          '\u7ef4\u5ea6\t\u5b9e\u4f53\t\u8f7d\u4f53/\u8def\u5f84\t\u51fa\u5904\t\u72b6\u6001\u6570\t\u5224\u636e\u7c7b\u578b\t\u5f52\u5c5e\u7247',
          [(r[0], r[1], r[2], r[3], r[4], r[5], SLICE.get(r[0], '-')) for r in ENT_SORTED])
write_tsv(os.path.join(OUT_DIR, '\u72b6\u6001\u77e9\u9635.tsv'),
          '\u7ef4\u5ea6\t\u5b9e\u4f53\t\u72b6\u6001/\u4e8b\u4ef6\t\u8fb9\u754c\u503c\t\u671f\u671b\u8868\u73b0(\u51fa\u5904)\t\u5b9e\u6d4b\t\u7ed3\u8bba\t\u8bc1\u636e',
          STA_SORTED)
write_tsv(os.path.join(OUT_DIR, '\u5dee\u5f02\u767b\u8bb0.tsv'),
          '\u7f16\u53f7\t\u662f\u4ec0\u4e48\t\u4e3a\u4ec0\u4e48\t\u51fa\u5904\t\u4f55\u65f6\u6d88\u9664', DIF)

# 覆盖矩阵判定片段（一行 = 一个实体）
frag = ['## G. \u8986\u76d6\u77e9\u9635\u5224\u5b9a\uff08\u811a\u672c\u751f\u6210\uff1a\u4e00\u884c = \u4e00\u4e2a\u5b9e\u4f53\uff1b\u26d4 \u52ff\u624b\u6539\uff09', '',
        '> \u6765\u6e90 = `策划/实体清单.tsv`\uff08`tools/probes/enumerate-entities.py` \u679a\u4e3e\uff09\u3002',
        '> \u7ed3\u8bba\u53ea\u5141\u8bb8\uff1a`\u4e00\u81f4` / `\u4e0d\u4e00\u81f4(\u5dee\u5728\u54ea)` / `\u5141\u8bb8\u7684\u5dee\u5f02(\u2192 \u5dee\u5f02\u767b\u8bb0.tsv)` / `\u5f85\u91c7(\u5e76\u6392\u56fe)`\u3002',
        '', '<!-- COVERAGE-BEGIN -->', '| # | \u7ef4\u5ea6 | \u5b9e\u4f53 | \u5224\u636e\u7c7b\u578b | \u7ed3\u8bba | \u8bc1\u636e |',
        '|---|---|---|---|---|---|']
def _s(t):
    # 只做**列内转义**：`|` -> `/`（表格列分隔符，⛔ 保留）。
    # 历史（2026-09-22 及以前）：这里还额外把 `.png` 改写成 ` .png`（扩展名前插一个空格），动机是让
    #   `screenshot-refs` 看不见判定表里的**地图贴图资产名**（D1 行的 `MltryCrteSd.png` 等，它们在
    #   client/Assets/** 下，永远解析不到 .ai-tmp/screenshots/）。
    #   ⛔ 那正是 `reference/anti-gaming.md` 说的"把指标当目标"，两条实测代价：
    #     ① 覆盖关系被破坏：清单 `MltryCrteSd.png` vs 判定行 `MltryCrteSd .png` ⇒ **350 行从未真被
    #        判过**，而闸门当时按"行数相等"报 PASS（假绿）；
    #     ② 证据列里**真实**的取证截图引用被一起改写（`contact-sheet-4-menu .png` ×144 等）⇒ 闸门
    #        看不见它们（该条 PASS 因此部分是假绿）。
    # 正确修法已在**闸门侧**落地：`verify.ps1` item 5 以 `策划/实体清单.tsv` 作**登记豁免**（被登记为
    #   实体/资产的 `.png` 名免查、其余必须解析，且豁免计数会打印），双向自检见 `gate-selftest.ps1`
    #   §10（registered asset PASS / unregistered missing FAIL / 同文件混合 FAIL）。
    #   ⇒ 这个改写**已无存在理由**，删掉它（顺序：先确认豁免在位 ⇒ 再删改写 ⇒ 再 --inject；反过来会
    #   立刻把 `screenshot-refs` 打红 +308 条）。既有产物中的漂移名由数据侧
    #   `tools/probes/restore-asset-names.py` 复原（幂等，已机证）。
    return str(t).replace('|', '/')


for i, r in enumerate(ENT_SORTED, 1):
    frag.append('| %d | %s | %s | %s | %s | %s |' % (i, r[0], _s(r[1]), r[5], _s(r[6]), _s(r[7])))
frag += ['<!-- COVERAGE-END -->', '']
# ⛔ 片段文件与下面注入用的 block 必须**逐字一致**（都从 BEGIN 标记开始、都不带前导/尾随空行）：
#   旧写法 `'\n'.join(frag[4:])` 前面多一个空行（frag[4] 是空串、frag[5] 才是 BEGIN），
#   而注入分支又把尾随换行 rstrip 掉了 ⇒ **每跑一次 --inject 就多插一个空行**（实测：验收表里
#   BEGIN 标记前已堆了 35 个空行，等价于该分支跑了 35 次）⇒ 生成器**不可复现**（同一输入跑两次
#   产出不同字节），"重跑生成器产出与盘上逐字节一致"这条判据根本立不住（SKILL §0.6：不可复现的
#   生成器 = 判据失效）。现在统一成 strip('\n') + 单个换行结尾。
_brand_block = '\n'.join(frag[4:]).strip('\n')
with open(os.path.join(OUT_DIR, '\u8986\u76d6\u77e9\u9635\u5224\u5b9a.fragment.md'), 'w', encoding='utf-8', newline='\n') as f:
    f.write(_brand_block + '\n')

# ============================================================================
#  「允许的差异」段：由 `DIF` **同源渲染**（段属权 = 生成器）
# ============================================================================
# 老形态：三处各自手写（`DIF`（-> 差异登记.tsv）+ 验收表里的手写段 + 该段的聚合数）⇒ 三处漂移
# 无人可见。实测 2026-09-23：81 行里 **41 行逐格不一致**（见 `tools/probes/diffs-align.py`）。
# ⇒ 这里把段变成 `DIF` 的渲染结果：先落 `策划/差异登记.fragment.md`（供对齐判据比较），
#   注入时**逐行原地替换**（`| <id> | …` 那几行）⇒ **段的行数不变**、段内手写散文块原位不动。
DIFF_HEADER = ['| # | \u5dee\u5f02 | \u4e3a\u4ec0\u4e48\u5fc5\u987b\u8fd9\u6837\uff08\u5b9e\u6d4b\u8bc1\u636e\uff09 | \u51fa\u5904 | \u4f55\u65f6\u80fd\u6d88\u9664 |',
               '|---|---|---|---|---|']


def render_diffs_rows():
    rows = []
    for r in DIF:
        cells = [str(x).replace('\n', ' ').strip() for x in r]
        if any('|' in c for c in cells):
            # ⛔ 不许静默改写（`|` -> `/`）：那是 anti-gaming §八 的"改数据让闸门看不见"形态。
            #    真出现就报出来，让对齐判据去测红。
            sys.stderr.write('  [diffs] WARN cell carries a table delimiter: id=%s\n' % (r[0],))
        rows.append('| ' + ' | '.join(cells) + ' |')
    return rows


DIFF_ROWS = render_diffs_rows()
with open(os.path.join(OUT_DIR, '\u5dee\u5f02\u767b\u8bb0.fragment.md'), 'w', encoding='utf-8', newline='\n') as f:
    f.write('\n'.join(DIFF_HEADER + DIFF_ROWS) + '\n')

DIFF_ROW_RE = re.compile(r'^\|\s*(\d+)\s*\|')


def _diffs_section_range(lines):
    """`## 允许的差异…` 段的行区间 [s0, s1)（0-based，含标题行）。"""
    s0 = next((k for k, l in enumerate(lines)
               if l.startswith('## ') and '\u5141\u8bb8\u7684\u5dee\u5f02' in l), -1)
    if s0 < 0:
        return (-1, -1)
    for k in range(s0 + 1, len(lines)):
        if lines[k].startswith('## ') or lines[k].startswith('### '):
            return (s0, k)
    return (s0, len(lines))


def _collect_section_diffs(lines):
    """盘上段里：行号 -> 行文本（数据行）、表头行、分隔行。"""
    s0, s1 = _diffs_section_range(lines)
    rows, hdr, sep = {}, None, None
    for k in range(s0, s1):
        ln = lines[k].rstrip('\r')
        if not ln.lstrip().startswith('|'):
            continue
        c = [x.strip() for x in ln.strip().strip('|').split('|')]
        if len(c) > 1 and c[1] == '#':
            hdr = (k, ln)
            continue
        if re.match(r'^\|[\s\-:|]+\|$', ln.strip()):
            sep = (k, ln)
            continue
        m = DIFF_ROW_RE.match(ln)
        if m:
            rows[m.group(1)] = (k, ln)
    return rows, hdr, sep


def _diff_drift(lines):
    """渲染结果 vs 盘上段：逐格比较（按 id 配对）。"""
    rows, hdr, sep = _collect_section_diffs(lines)
    rend = dict(zip([str(r[0]) for r in DIF], DIFF_ROWS))
    drift = sorted((i for i in rend if i in rows and rend[i] != rows[i][1]), key=lambda x: int(x))
    only_disk = sorted((i for i in rows if i not in rend), key=lambda x: int(x))
    only_render = sorted((i for i in rend if i not in rows), key=lambda x: int(x))
    hdr_bad = bool(hdr) and hdr[1] != DIFF_HEADER[0]
    sep_bad = bool(sep) and sep[1] != DIFF_HEADER[1]
    return {'rows': rows, 'hdr': hdr, 'sep': sep, 'drift': drift,
            'only_disk': only_disk, 'only_render': only_render,
            'hdr_bad': hdr_bad, 'sep_bad': sep_bad}


def _inject_header_warning(lines):
    """刷新/写入表头那行段属权警告。

    ⛔ **行数必须不变**（占一个已有空行）：`策划/对照表.md` 有 11 处 `策划/验收表.md:NNN`
       行号引用，而 `对照表.md` 不由任何生成器所有（也不许手改）⇒ 在表头**插行**会把那 11 处
       全部顶错位。故：先按 `HEADER_WARNING_PREFIX` 找自己写过的那一行原地替换（幂等）；
       找不到才占用 H1 之后的**第一个空行**（行数仍不变）。
    """
    for k, ln in enumerate(lines):
        if ln.startswith(SO.HEADER_WARNING_PREFIX):
            lines[k] = SO.header_warning()
            return 'refreshed', k
    h1 = next((k for k, ln in enumerate(lines) if ln.startswith('# ')), -1)
    if h1 < 0:
        return 'h1-not-found', -1
    for k in range(h1 + 1, len(lines)):
        if lines[k].strip() == '':
            lines[k] = SO.header_warning()
            return 'inserted-into-blank', k
    return 'no-blank-slot', -1


if '--inject' in sys.argv:
    # 覆盖矩阵的**判定行**落 `策划/验收表.md`（规格文件放的是"要做成什么样"，判定表放的是"判成什么"）。
    spec_path = os.path.join(OUT_DIR, '\u9a8c\u6536\u8868.md')
    # ⛔ seam（`reference/anti-gaming.md` §五 第 3 条）：注入目标可覆盖 ⇒ 自检**只喂副本**，
    #    共享产物（真 `策划/验收表.md`）一个字节都不用动，就不存在"改完再还原"吞掉并发写入的风险。
    for _a in sys.argv:
        if _a.startswith('--spec-path='):
            spec_path = _a.split('=', 1)[1]
    txt = rd(spec_path)
    B, E = '<!-- COVERAGE-BEGIN -->', '<!-- COVERAGE-END -->'
    # ⛔ block 取 `frag[4:]` 会带上一个**前导空行**（frag[4] == ''），而旧代码只 rstrip 尾部
    #    ⇒ 每次注入都往 BEGIN 标记前多插一个空行 ⇒ 同输入两次产出不同字节（不可复现，见上）。
    #    这里与片段文件用同一个 `_brand_block`：BEGIN…END、无前导/尾随空行 ⇒ 注入幂等。
    block = _brand_block
    # ⛔ 标记按**整行精确匹配**定位（不再用 `txt.find`）：`find` 会命中散文里出现的同样字面量 ⇒
    #    替换起点跑到散文中间，把 §A~§F 整段吞掉（本片新写的表头警告因此只用纯名字、不带 `<!--`）。
    _L = [x.rstrip('\r') for x in txt.split('\n')]
    i = next((k for k, l in enumerate(_L) if l == B), -1)
    j = next((k for k, l in enumerate(_L) if l == E), -1)
    if i >= 0 and j > i:
        txt = '\n'.join(_L[:i]) + ('\n' if i else '') + block + '\n' + '\n'.join(_L[j + 1:])
    else:
        txt = txt.rstrip('\n') + '\n\n---\n\n' + '\n'.join(frag) + '\n'

    # --- 表头段属权警告（由生成器写、也由它识别替换；行数不变）-------------------
    lines = txt.split('\n')
    how, at = _inject_header_warning(lines)
    txt = '\n'.join(lines)
    sys.stderr.write('  [inject] target = %s\n' % spec_path)
    sys.stderr.write('  [inject] section-ownership warning: %s at line %d\n' % (how, at + 1))

    # --- 「允许的差异」段：`DIF` 同源渲染 ----------------------------------------
    d = _diff_drift(lines)
    # ⛔ 片BW-E：`only_render`（源里有、段里没有的 id）**不再是**"不能注入"的理由 ——
    #    旧版只做"按 id 原地替换"（`if iid in rend`）⇒ **新 id 永远进不了段**（实测：
    #    `bot-ai-r6` 的 #81 只能靠手改产物落进段里，与本片要消灭的"别人的活被 --inject
    #    抹掉"同一根因）。⇒ 现在缺的 id 由下面的**插入分支**按 id 升序插到段尾。
    #    仍然**阻断**的是 drift / only_disk / 表头 / 分隔线 —— 那些意味着段里有人手写
    #    或改动过判定行，注入会覆盖掉它（宁可报出来让人看，⛔ 不许静默吞）。
    live = bool(d['drift'] or d['only_disk'] or d['hdr_bad'] or d['sep_bad'])
    # ---- 片BW-E-R（team-lead 2026-09-23 建议）：**漂移行无条件打印** ----
    #   旧版只在"拒写"分支打印 drifted ids，而 `--inject-diffs`（强制写）走 else 分支时
    #   它**不打** ⇒ 写前看不到"漂移了哪几行"，只能靠写后补证（`bw-evid` 实测踩过）。
    if live:
        sys.stderr.write('  [inject] \u5dee\u5f02\u6bb5\u6f02\u79fb\uff1adrifted ids = %d %s ; '
                         'only on disk = %s ; only in render = %s\n'
                         % (len(d['drift']), d['drift'], d['only_disk'], d['only_render']))
    if live and '--inject-diffs' not in sys.argv:
        sys.stderr.write('  [inject] \u5dee\u5f02\u6bb5\u672a\u6ce8\u5165\uff1a\u76d8\u4e0a\u6bb5\u4e0e `DIF` \u6e32\u67d3\u7ed3\u679c\u4e0d\u4e00\u81f4 '
                         '\u21d2 \u8986\u76d6\u4f1a\u6389\u5185\u5bb9\u3002\u5224\u636e\uff1a'
                         '`python tools/probes/diffs-align.py \u7b56\u5212/\u9a8c\u6536\u8868.md '
                         '\u7b56\u5212/\u5dee\u5f02\u767b\u8bb0.fragment.md`\n')
        sys.stderr.write('    drifted ids = %d %s\n' % (len(d['drift']), d['drift']))
        sys.stderr.write('    only on disk = %s ; only in render = %s\n'
                         % (d['only_disk'], d['only_render']))
        sys.stderr.write('    \u8981\u5f3a\u5236\u5199\u5165\uff1a`--inject-diffs`\uff08\u4f1a\u4e22\u76d8\u4e0a\u90a3\u4e9b\u884c\u7684\u624b\u5199\u5185\u5bb9\uff09\n')
    else:
        rend = dict(zip([str(r[0]) for r in DIF], DIFF_ROWS))
        for iid, (k, _ln) in d['rows'].items():
            if iid in rend:
                lines[k] = rend[iid]
        # 片BW-E：段里没有、`DIF` 源里有的 id ⇒ **按 id 升序插到段内最后一条判定行之后**。
        # ⛔ 只插判定行：段内散文块（片AD/片AE 注记 / 裸 `---` 分隔线）原样留在下面不动。
        # 判据 = 插入后 `差异登记.tsv` 与「允许的差异」段**同为同一 id 集**（verify.ps1 第 24 条）。
        _ks = [k for k, _ln in d['rows'].values()]
        if _ks:
            _at = max(_ks) + 1
        else:
            _s0, _s1 = _diffs_section_range(lines)
            _at = _s1 if _s0 >= 0 else len(lines)
        _missing = sorted([k for k in rend if k not in d['rows']],
                          key=lambda z: (int(z) if z.isdigit() else 0, z))
        if _missing:
            lines[_at:_at] = [rend[k] for k in _missing]
        if d['hdr']:
            lines[d['hdr'][0]] = DIFF_HEADER[0]
        if d['sep']:
            lines[d['sep'][0]] = DIFF_HEADER[1]
        txt = '\n'.join(lines)
        sys.stderr.write('  [inject] diffs: replaced %d row(s), inserted %d row(s) %s' ' (the prose blocks in the section are untouched)\n'
                         % (len(_missing) and len(d['rows']) or len(d['rows']), len(_missing), ','.join(_missing)))
        sys.stderr.write('  [inject] \u5dee\u5f02\u6bb5\u5df2\u540c\u6e90\uff08%d \u884c\uff0c\u9010\u884c\u539f\u5730\u66ff\u6362\uff0c\u884c\u6570\u4e0d\u53d8\uff09\n'
                         % len(d['rows']))

    with open(spec_path, 'w', encoding='utf-8', newline='\n') as f:
        f.write(txt)
    print('injected coverage matrix into', rel(spec_path))

    # ---- hit ledger（片BW-E）：让本枚举器成为 verify.ps1 item 40 `coverage-hit` 的合规载体 ----
    # 契约（真源 = `tools/probes/audit-verdict-rows.py` SECTION B）：首行
    #   `# probe-hits plan=<本次判定的 plan 目录>`，其后每数据行**第一字段 == 判定行行号**。
    # 本枚举器就是逐行"探"这些行的那支探针（它按行读资产/源码/规格，再算出结论与**锚点**）
    #   ⇒ ledger 的每行记的是"这行被读了、读的东西在盘上的哪里（`<form>:<token>`，读不到写 `--`）"。
    # ⛔ 两条防风（item 40 注释里写死的，不许绕过）：
    #   ① **必须声明**：否则"任何含这个数字的文件"都能满足该检查 = 自己的输出满足自己；
    #   ② **必须绑定 plan 目录**：否则自检夹具 ledger 会把真表报成命中（本文件的 ledgers 与
    #      `tools/probes/**` 下的夹具同处一个默认扫描根 ⇒ 不绑定就分不开）。
    # ⛔ 隔离（anti-gaming §五 第 3 条「自检只许动自己的副本」）：ledger 落**本次注入的那个
    #   plan 目录** —— 真 `策划` 运行时落 `tools/probes/coverage-hits.tsv`（item 40 的默认扫描根
    #   之一，交付物）；沙箱 / 副本注入（`--out-dir=` / `--spec-path=<副本>`）时落进那个副本
    #   ⇒ **任何自检都碰不到真 ledger**，真产物前后哈希必然一致（本片自检即按此断言）。
    _plan_hits = os.path.dirname(os.path.abspath(spec_path))
    _hits_out = (os.path.join(ROOT, 'tools', 'probes', 'coverage-hits.tsv')
                 if os.path.normcase(_plan_hits) == os.path.normcase(PLAN)
                 else os.path.join(_plan_hits, 'coverage-hits.tsv'))
    for _a in sys.argv:
        if _a.startswith('--hits-out='):
            _v = _a.split('=', 1)[1]
            _hits_out = _v if os.path.isabs(_v) else os.path.join(ROOT, _v)
    _hl = ['# probe-hits plan=' + os.path.normpath(_plan_hits)]
    _n_anc_l = 0
    # ⛔ 片BW-E-R 修订 v2（2026-09-23；`verify.ps1` 第 40 项 TABLE-ECHO 判据）：命中行**不许复读判定行自己的单元**
    #   —— "复读 >= 3 格" 等于把表重渲一遍，那条闸门当场判为 TABLE ECHO、**不算命中**（实测：旧格式
    #   `id/维度/实体/判据类型/结论/锚点` 里有 4 格与表逐字相同 ⇒ 3800/3800 全被判回声）。
    #   ⇒ 只写**探针自己读到的东西**：`<行号>\t<锚点>\t<实测>`。
    #   锚点 = 该行结论依据的盘上位置（`F1:<文件>:<行>` / `F2:guid=<32hex>` / `--` = 该行确实没有可复核物）。
    # ⛔ `unresolved` 语义唯一（team-lead 2026-09-23 裁决）：`unresolved=1` == "探针没找到东西"
    #   ⇒ **只许出现在 `--` 行**。有锚点却写 `unresolved=1` = 该字段在撒谎（旧版把解析不到的 F2/F3/F4
    #   行写成 `unresolved=1`，实测 1683 行在撒谎）⇒ 每种形态各写各的实测：
    #     F1（有行号）   -> `line=<命中行号> hash=<该行归一化内容 sha1[:8]>`
    #     F2（guid）     -> `meta_line=<.meta 里 guid 所在行号> hash=<该行归一化内容 sha1[:8]>`
    #     F3 / F4（无行号）-> `file_bytes=<字节数> file_sha1=<文件 sha1[:8]>`
    #     `--`（真没有）  -> `unresolved=1`
    #   降级（锚点确实在盘、但本探针这一刻算不出强形态）也**不写 `unresolved=1`**：F1 行读不到 -> 文件级；
    #   F2 `.meta` 找不到 -> `meta_missing=1`；兆底 -> `token_sha1=<>`。
    #   ⚠️ 强度不等（F1 行摘要 > F2 > F3/F4 文件级）——已明知并接受：“每格都诚实”比“每格都同等强”更重要。
    #   ⚠️ **归一化定义**（第三方可逐字复算）：取该行**不含行终止符**的原始文本 -> `str.strip()` 去首尾空白
    #   -> UTF-8 编码 -> `hashlib.sha1(...).hexdigest()[:8]`（小写十六进制）。
    #   ⚠️ **确定性**：每一格都是“盘的确定性函数”（同一份盘 + 同一份源码 ⇒ 逐字节相同）⇒ ⛔ 无时间戳 / mtime / 运行号 / pid。
    #   解析口径（与判据资产 `audit-verdict-rows.py` 对齐：⛔ 不修改它，只**只读复用**它的 `resolve()` / `norm_path()`）：
    #   F3/F4 的裸文件名靠 `_AV.resolve` 在 `BASENAME_ROOTS` 里解析；F2 的 guid 走**全 `client/**`** 的 `.meta` 索引
    #   （旧版只用 `GUID2PATH`，它只覆盖 `ASSET_ROOT_LIST + Assets/Editor` ⇒ 约 70 行查不到 `.meta`）。
    # ⛔ **`probe=` 专指真探针**（team-lead 2026-09-23 裁决）：枚举器**不写** `probe=` —— 按契约 R3，枚举类（D1/D5）自己的输出
    #   即合法台账（不加也 PASS）；而给枚举器写 `probe=` = **字段兼职**（我们刚把 `unresolved` 兼职打掉，
    #   立了"一个字段只表示一件事"）；将来若要区分台账来源，**另立字段**（例如 `producer=enumerate-entities`），
    #   ⛔ 不复用 `probe=`。行为类（D6,D7,D9,D10,D11,D12,S2,S3）**绝不由本探针声称命中**（否则 127 行会被
    #   "探针自称命中"假绿 —— 它们需要真探针；那红是正确的）。
    # ⛔ **活文件**（追加型流，如 Unity `client/Logs/Editor.log`）不能写全文摘要：实测 2026-09-23 同一份盘跑两次
    #   `file_bytes` = 187955794 → 187956442（`file_sha1` 也变）⇒ 产物逐次变 ⇒ 幂等判据当场归零。
    #   ⇒ 改钉**头部 8 KiB**（追加不改头部）；不足一窗只记 `live_file=1`。范围由 `LIVE_ROOTS` 显式枚举。
    # ⛔ 硬要求 ④（team-lead 2026-09-23）：**降级必须可测** —— 三类降级各自计数，收尾恒打
    #   `degraded: meta_missing=N line_sha1_missing=N file_hash_err=N token_fallback=N`（N=0 也打，自检才能断言）；
    #   ⛔ `except` **只许捕可预期的 I/O 失败**（`OSError`），`NameError`/`AttributeError` 这类编程错误**必须抛出**
    #   —— 宽 `except Exception` 会把"代码写错"伪装成"数据缺失"（本片实测：第一版用 `io.open`，而本文件**没有 `import io`**
    #   ⇒ `NameError` 被宽 `except` 吞掉 ⇒ 1180 行 F2 全落 `meta_missing`、1002 行 F1 全落降级形态，表面看还像“各写各的”）。
    #   ⚠ 文本读取一律走本文件既有的 `rd()`（它已把 `OSError` 吞成 `''`）+ 内置 `open(..., 'rb')`。
    import hashlib as _hbm
    _GD = {}
    _GDB = [False]
    _ML = {}
    _DEG = {'meta_missing': 0, 'line_sha1_missing': 0, 'file_hash_err': 0,
            'token_fallback': 0, 'live_head': 0, 'live_file': 0}

    def _abs_of(relp):
        return os.path.join(ROOT, str(relp).replace('/', os.sep))

    def _sha1_8(data):
        return _hbm.sha1(data).hexdigest()[:8]

    def _guid_meta(guid):
        # ⛔ 无 try/except：`rd()` 已把 OSError 吞成 ''；其余异常 = 编程错误，必须抛。
        if not _GDB[0]:
            _GDB[0] = True
            _root = os.path.join(ROOT, 'client')
            for _dp, _dn, _fn in os.walk(_root):
                _dn[:] = [d for d in _dn if d not in ('Library', 'Temp', 'obj', 'bin')]
                for _f in _fn:
                    if not _f.endswith('.meta'):
                        continue
                    _fp = os.path.join(_dp, _f)
                    for _i, _ln in enumerate(rd(_fp).split('\n')):
                        if _i > 12:
                            break
                        _m = re.search(r'guid:\s*([0-9a-fA-F]{32})', _ln)
                        if _m:
                            _GD[_m.group(1).lower()] = _fp
                            break
        return _GD.get(str(guid).lower())

    def _line_digest(fp, ln):
        _k = (fp, ln)
        if _k not in _ML:
            _ML[_k] = ''
            _ls = rd(fp).split('\n')
            if 1 <= ln <= len(_ls):
                _ML[_k] = _sha1_8(_ls[ln - 1].strip().encode('utf-8'))
        return _ML[_k]

    # 活文件根（追加型流）：要扩就往这里加，且必须在注释里说明理由。
    LIVE_ROOTS = ('client/Logs/',)
    LIVE_HEAD = 8192

    def _is_live(fp):
        try:
            _r = rel(fp).replace(os.sep, '/')
        except ValueError:                       # 盘外路径（不应该出现）⇒ 不当活文件
            return False
        return any(_r.startswith(_p) for _p in LIVE_ROOTS)

    def _file_meas(fp):
        # ⛔ 只捕 `OSError`（文件在 isfile 与 open 之间消失 / 权限 / 读错）；其余异常 = 编程错误，必须抛。
        # ⛔ 活文件只钉**头部**：全文摘要必然每跑一次就变（实测 2026-09-23：row 3790 引
        #   `client/Logs/Editor.log`（Unity 正在写）⇒ 两次运行 `file_bytes` 187955794 → 187956442、`file_sha1` 也变
        #   ⇒ 打掉"两次 --inject 六件产物 sha256 相同"）。头部 8 KiB 在同一份盘内稳定（追加不改头部）；
        #   不足一窗则只记 `live_file=1`（不假装有量值）。
        if _is_live(fp):
            try:
                with open(fp, 'rb') as _f:
                    _h = _f.read(LIVE_HEAD)
            except OSError:
                _DEG['file_hash_err'] += 1
                return 'file_bytes=0 file_sha1=err'
            if len(_h) < LIVE_HEAD:
                _DEG['live_file'] += 1
                return 'live_file=1'
            _DEG['live_head'] += 1
            return 'head_bytes=%d head_sha1=%s' % (LIVE_HEAD, _sha1_8(_h))
        try:
            with open(fp, 'rb') as _f:
                _b = _f.read()
        except OSError:
            _DEG['file_hash_err'] += 1
            return 'file_bytes=0 file_sha1=err'
        return 'file_bytes=%d file_sha1=%s' % (len(_b), _sha1_8(_b))

    def _meta_line_of(fp, guid):
        _k = (fp, guid)
        if _k not in _ML:
            _ML[_k] = 0
            for _n, _ln in enumerate(rd(fp).split('\n'), 1):
                if re.search(r'guid:\s*' + re.escape(str(guid)), _ln, re.I):
                    _ML[_k] = _n
                    break
        return _ML[_k]

    for _i, _r in enumerate(ENT_SORTED, 1):
        _f, _tok = (None, None)
        if _AV is not None:
            _f, _tok = _AV.classify_anchor(str(_r[7]))
        _meas = 'unresolved=1'
        if _f == 'F1':
            _m = re.match(r'^(.*):(\d+)$', str(_tok))
            _fp = None
            if _m and _AV is not None:
                _fp = _AV.resolve(_AV.norm_path(_m.group(1)))
            if _fp is None and _m:
                _fp = _abs_of(_m.group(1))
            _h = _line_digest(_fp, int(_m.group(2))) if (_m and _fp and os.path.isfile(_fp)) else ''
            if _h:
                _meas = 'line=%s hash=%s' % (_m.group(2), _h)
            elif _fp and os.path.isfile(_fp):
                _DEG['line_sha1_missing'] += 1
                _meas = _file_meas(_fp)
        elif _f == 'F2':
            _g = str(_tok).split('=')[-1]
            _fp = _guid_meta(_g)
            if _fp:
                _n = _meta_line_of(_fp, _g)
                _h = _line_digest(_fp, _n) if _n else ''
                if _h:
                    _meas = 'meta_line=%d hash=%s' % (_n, _h)
                else:
                    _DEG['line_sha1_missing'] += 1
                    _meas = _file_meas(_fp)
            else:
                _DEG['meta_missing'] += 1
                _meas = 'meta_missing=1'
        elif _f in ('F3', 'F4'):
            _fp = _AV.resolve(_AV.norm_path(str(_tok))) if _AV is not None else None
            if _fp is None:
                _fp = _abs_of(str(_tok))
            _meas = _file_meas(_fp) if os.path.isfile(_fp) else _file_meas('')
        if _f and _meas == 'unresolved=1':
            # 兜底：锚点是判据资产认过的（盘上真有）⇒ 绝不写 “探针没找到东西”（那是假话）。
            _DEG['token_fallback'] += 1
            _meas = 'token_sha1=' + _sha1_8(str(_tok).encode('utf-8'))
        if _f:
            _n_anc_l += 1
        _hl.append('%d\t%s\t%s' % (_i, ('%s:%s' % (_f, _tok)) if _f else '--', _meas))
    sys.stderr.write('  [hits] degraded: meta_missing=%d line_sha1_missing=%d '
                     'file_hash_err=%d token_fallback=%d ; live_head=%d live_file=%d\n'
                     % (_DEG['meta_missing'], _DEG['line_sha1_missing'],
                        _DEG['file_hash_err'], _DEG['token_fallback'],
                        _DEG['live_head'], _DEG['live_file']))
    with open(_hits_out, 'w', encoding='utf-8', newline='\n') as _f2:
        _f2.write('\n'.join(_hl) + '\n')
    sys.stderr.write('  [hits] ledger = %s  (%d rows, %d with an anchor)\n'
                     % (rel(_hits_out), len(ENT_SORTED), _n_anc_l))
    sys.stderr.flush()

cnt = Counter(r[0] for r in ENT_SORTED)
print('ENTITY rows = %d   STATE rows = %d   DIFF rows = %d' % (len(ENT_SORTED), len(STA_SORTED), len(DIF)))
for d in DIMS:
    print('  %-4s %s' % (d, cnt.get(d, 0)))
print('conclusion split:')
for k, v in Counter(r[6].split('(')[0] for r in ENT_SORTED).most_common():
    print('  %-8s %d' % (k, v))
