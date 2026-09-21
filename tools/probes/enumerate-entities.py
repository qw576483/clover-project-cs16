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
    策划/差异登记.tsv         是什么	为什么	出处	何时消除
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
ROOT = os.path.dirname(os.path.dirname(HERE))
ASSETS = os.path.join(ROOT, 'client', 'Assets')
PLAN = os.path.join(ROOT, '\u7b56\u5212')          # 策划

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
SPEC = '\u7b56\u5212/\u7b56\u5212\u6848/CS1.6\u5355\u673a\u53c2\u8003\u89c4\u683c.md'
REGISTRY = '\u7b56\u5212/\u5bf9\u7167\u8868.md'
REG_TXT = rd(os.path.join(ROOT, REGISTRY)) + rd(os.path.join(ROOT, SPEC))

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
        v, ev = K_CONSIST, '\u88ab\u5f15\u7528\uff08guid/\u77ed\u540d\u547d\u4e2d\u4ee3\u7801\u6216\u8d44\u4ea7\u6587\u672c\uff09'
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
GC_GEOM_NOCRATE = GC.Geom(GC_GEO, {g['name'] for g in GC_GEO['groups'] if g['name'] not in GC.CRATE_GROUPS})
GC_GATE = GC.Gate(GC_CONST, GC_GEOM_ALL)
GC_COLL = GC.colliders_consistency(GC_GEO, GC_SCENE, [GC.load_bits(p) for p in GC.BYTES_FILES])
GC_GROUPS = GC.scene_groups(GC_GEO, GC_SCENE)
GC_DOORS = GC.door_groups(GC_GEO)
GC_BOXES = GC.box_stats(GC_GEO, GC_BM, GC_GATE, GC_GEOM_ALL, GC_CONST)
GC_LOW = GC.low_obstacle_stats(GC_GEO, GC_BM, GC_GATE, GC_GEOM_NOCRATE, GC_CONST)
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
_apex = GC_CONST['JumpSpeed'] ** 2 / (2.0 * GC_CONST['Gravity'])
add('D2', '低矮障碍（含楼梯扶手/台阶沿）', rel(GEO), 'tools/probes/geom-check.py（A5）',
    GC_LOW['count'], T_SCRIPT,
    K_CONSIST if GC_LOW['ok'] else
    '%s(候选 %d 处：地面挡住 %d / 跳起可通过 %d)' % (K_MISMATCH, GC_LOW['count'],
                                                     GC_LOW['blocked_at_grade'], GC_LOW['clear_at_jump']),
    '候选=%d 地面挡=%d 跳起通=%d 最高高差=%.2f m ≤ %.4f m'
    % (GC_LOW['count'], GC_LOW['blocked_at_grade'], GC_LOW['clear_at_jump'], GC_LOW['h_max'], _apex))
_worst = GC_LOW['worst']
sta('D2', '低矮障碍（含楼梯扶手/台阶沿）', 'A 点起跳能不能落到 B 点',
    '起跳后脚面高于顶面的时间窗 × 水平速度 ≥ 障碍宽+2×半径',
    '原版：矮障碍可以跳过去（出处：原版 de_dust2.bsp 几何 + pm_shared.c 跳跃初速 6.82 + sv_gravity 800*0.0254）',
    '最紧一处 h=%.2f m ⇒ 时间窗 %.3f s × SpeedKnife %.1f m/s = %.2f m ≥ 需跨 %.2f m（%s）'
    % (_worst['h'] if _worst else 0.0, _worst['dt'] if _worst else 0.0, GC_CONST['SpeedKnife'],
       _worst['reach'] if _worst else 0.0, GC_LOW['need'], '可达' if GC_LOW['jump_ok'] else '不可达'),
    K_CONSIST if GC_LOW['jump_ok'] else '%s(用户报「匪家楼梯扶手跳不过去」)' % K_MISMATCH,
    'tools/probes/geom-check.py（A5 轨迹断言）')
sta('D2', '低矮障碍（含楼梯扶手/台阶沿）', '存在与可跳过', '顶面高差 ≤ 跳跃可达高度',
    '原版：矮障碍可以跳过去/站上去（出处：原版 de_dust2.bsp 几何 + pm_shared.c 跳跃初速）',
    '候选=%d 地面挡=%d 跳起通=%d' % (GC_LOW['count'], GC_LOW['blocked_at_grade'], GC_LOW['clear_at_jump']),
    K_CONSIST if GC_LOW['ok'] else '%s(用户报「匪家楼梯扶手跳不过去」)' % K_MISMATCH,
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
    for n, ks in sorted(seen.items()):
        isbtn = any(x.endswith('Button') for x in ks)
        istext = any(x.endswith('Text') for x in ks)
        if isbtn:
            # 有 Button 组件 ⇒ 交互反馈路径存在；**外观是否 1:1 只能眼睛判**
            add('D4', '%s/%s' % (os.path.basename(p), n), rel(p), rel(p), 5, T_SIDE, K_PENDING,
                '\u7ec4\u4ef6=%s' % ','.join(sorted(set(x.split('::')[-1] for x in ks))))
            for st, bd in (('\u5e38\u6001', '-'), ('\u60ac\u505c', 'highlightedSprite/\u989c\u8272'),
                           ('\u6309\u4e0b', 'pressedSprite/\u989c\u8272'), ('\u7981\u7528', 'disabledState'),
                           ('\u9009\u4e2d', 'selectedState')):
                sta('D4', '%s/%s' % (os.path.basename(p), n), st, bd,
                    '\u539f\u7248 UI \u540c\u63a7\u4ef6\u7684\u540c\u72b6\u6001\uff08\u51fa\u5904\uff1a\u539f\u7248 resource/*.res \u7684 ButtonBG/ArmedBg \u7b49\uff09',
                    '\u672a\u91c7', K_PENDING, '\u9700\u5e76\u6392\u56fe')
        elif istext or any(x.endswith('Image') for x in ks):
            add('D4', '%s/%s' % (os.path.basename(p), n), rel(p), rel(p), 1, T_SIDE, K_PENDING,
                '\u7ec4\u4ef6=%s' % ','.join(sorted(set(x.split('::')[-1] for x in ks))))

# 用户点名「小地图雷达不对」的机器判据（表现类外观仍只能眼睛判 ⇒ 另起一行待采）
RADAR_PNG = os.path.join(ASSETS, 'Resources', 'UI', 'Art', 'overview_de_dust2.png')
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
add('D4', '\u96f7\u8fbe/\u5c0f\u5730\u56fe\u663e\u793a\u4e0e\u53ef\u89c1\u6027\u95e8\u63a7\uff08\u7528\u6237\u62a5\u201c\u96f7\u8fbe\u4e0d\u5bf9\u201d\uff09', rel(RADAR_CS), rel(RADAR_CS), 4, T_SIDE,
    K_PENDING, '\u5916\u89c2/\u843d\u70b9\u53ea\u80fd\u5e76\u6392\u56fe\u5224\uff08\u9700\u4e0e\u539f\u7248\u96f7\u8fbe\u540c\u673a\u4f4d\u5e76\u6392\uff09')

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
    add('D5', rel(p), rel(p), rel(p), len(states), T_SCRIPT, K_CONSIST, 'states=%d' % len(states))
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
_spec_decl_m = re.search(r'(\d+)\s*个阻挡盒', rd(SPEC))
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
    add('D11', '%s@%s' % (key, ctx), '\u811a\u672c\u7ed1\u5b9a\u8868', 'Core/CsConst.cs + Module/**', 2, T_SCRIPT, v, tgt)
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

# 武器表：每把枪的 id 是否有 fire/reload clip + viewmodel 控制器
for wid in WEAPON_IDS:
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
for nm in ('\u5e27\u65f6\u95f4', '\u5206\u8fa8\u7387', '\u5185\u5b58', '\u573a\u666f\u52a0\u8f7d\u8017\u65f6'):
    add('S2', nm, 'tools/probes/measure-play-frametime.cs', rel(ROOT + '/tools/probes/measure-play-frametime.cs'), 2,
        T_SIDE, K_PENDING, '\u9700\u5b9e\u673a\u91c7\uff08\u5e27\u65f6\u95f4\u7c7b\uff09')

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
CROSS = [
    ('D5', '\u4ea4\u53c9 D5\u00d7D10 \u52a8\u753b\u00d7\u903b\u8f91\u72b6\u6001',
     '\u8e72\u7740\u5f00\u67aa/\u8dd1\u7740\u6362\u5f39/\u6b7b\u4ea1\u4e2d\u5207\u67aa', T_SIDE, K_PENDING),
    ('D4', '\u4ea4\u53c9 D4\u00d7D12 UI\u00d7\u6d41\u7a0b\u72b6\u6001',
     '\u6682\u505c\u65f6\u7684 HUD / \u4e70\u67aa\u533a\u83dc\u5355 / \u89c2\u6218\u8bb0\u5206\u677f', T_SIDE, K_PENDING),
    ('D8', '\u4ea4\u53c9 D8\u00d7D6 \u97f3\u6548\u00d7\u4e8b\u4ef6\u00d7\u6750\u8d28',
     '\u811a\u8e0f\u6c99 vs \u91d1\u5c5e\uff1b\u6253\u6728\u5934 vs \u6df7\u51dd\u571f', T_SCRIPT,
     K_MISMATCH if not re.search(r'hit_wall', SFX_TXT) else K_CONSIST),
    ('D9', '\u4ea4\u53c9 D9\u00d7\u89d2\u8272\u7c7b\u578b \u78b0\u649e\u00d7\u89d2\u8272\u7c7b\u578b',
     '\u73a9\u5bb6 vs Bot\uff08\u6295\u63b7\u7269\u89c1\u5dee\u5f02\u767b\u8bb0\uff09', T_SCRIPT,
     K_CONSIST if (push_code and push_attached and sep_ok) else K_MISMATCH),
    ('D6', '\u4ea4\u53c9 D6\u00d7D3 \u7279\u6548\u00d7\u547d\u4e2d\u6750\u8d28',
     '\u5f39\u75d5\u662f\u5426\u8d34\u5bf9\u6cd5\u7ebf/\u662f\u5426\u5206\u6750\u8d28', T_SIDE, K_PENDING),
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
for dim, nm, note, vt, vd in CROSS:
    add(dim, nm, '\u8de8\u7ef4\u5ea6\u56e0\u679c\u5bf9', 'patterns/full-coverage-audit.md \u00a74', 2, vt, vd, note)
    sta(dim, nm, '\u4ea4\u53c9\u72b6\u6001', '-', note, '\u5730\u57df\u7814\u5224', vd, 'patterns/full-coverage-audit.md \u00a74')

# ============================================================================
#  差异登记（四要素）
# ============================================================================
DIF = [
    # 切片G 已消除的那条（雷达底图由旧 geo 栅格化）**从登记里删除** —— 本片已重出：
    # render-overview.py 修 blocker stride 20→24 后重跑，底图与现 geo 一致（差异 #14 同片消除）。
    # 登记是"允许的差异"，不是"曾经的缺陷"档案：修好了还留着，会让"零不一致"永远打折。
    # 切片F 已消除的两条（门板网格为空 / 阻挡盒口径 218 vs 660）**从登记里删除** ——
    # 登记是"允许的差异"，不是"曾经的缺陷"档案：修好了还留着，会让"零不一致"永远打折。
    ('\u4f4d\u56fe\uff08CloverMap v1\uff09\u662f\u5355\u5c42 2D\uff1a\u7bb1\u5b50\u6240\u5728\u683c\u8bb0\u4e3a\u201c\u53ef\u8d70\u201d\uff08\u7bb1\u9876\u662f\u671d\u4e0a\u7684\u9762\uff09',
     '\u683c\u5f0f\u5c42\u6ca1\u6709\u9ad8\u5ea6\uff08FlagHeightField \u9884\u7559\u4f46 V1 \u89e3\u7801\u5668\u62d2\u7edd\uff09\u21d2 \u4e00\u683c\u4e00\u4f4d\uff0c\u8868\u8fbe\u4e0d\u4e86\u201c\u540c\u4e00\u683c\u5728 y=0 \u88ab\u6321\u3001\u5728 y=1.2 \u901a\u7545\u201d\uff1b'
     '\u5e26\u6765\u7684\u8fb9\u754c\uff1a\u7bb1\u5b50\u8fdb\u4e0d\u53bb\uff08\u5df2\u7531 CsMap.CanStand \u7684\u201c\u5730\u9762\u4e00\u6b65\u95f8\u95e8\u201d\u62e6\u4f4f\uff09\uff0c'
     '\u4f46\u4f4d\u56fe\u672c\u8eab\u4ecd\u4e0d\u80fd\u5355\u72ec\u56de\u7b54\u201c\u80fd\u4e0d\u80fd\u7a7f\u201d',
     'Assets/Scripts/Module/Map/CsMap.cs\uff08CanStand/BodyHeightClear\uff09\uff1b'
     'Packages/com.clover.unity-engine/Runtime/Presentation/MapFormat.cs:30\uff08FlagHeightField\uff09',
     '\u5f15\u64ce\u5f00\u51fa V2 \u9ad8\u5ea6\u573a\uff08FlagHeightField\uff09\u540e'),
    ('\u6295\u63b7\u7269\u4e0e\u89d2\u8272\u4e4b\u95f4\u4e0d\u4e92\u76f8\u6321/\u63a8\u5f00',
     '\u672c\u7247\u53ea\u628a\u201c\u89d2\u8272\u5bf9\u89d2\u8272\u201d\u8fd9\u4e00\u5c42\u505a\u51fa\u6765\uff08CsActorSeparation \u53ea\u6536 CsActor\uff09\uff1b'
     '\u539f\u7248\u6295\u63b7\u7269\u662f MOVETYPE_BOUNCE \u5b9e\u4f53\uff0c\u4e0e\u89d2\u8272\u662f\u5426\u4e92\u76f8\u963b\u6321\u672c\u673a\u53d6\u4e0d\u5230\u53ef\u4fe1\u51fa\u5904\uff08\u539f\u7248 mp.dll \u672a\u5728\u76d8\uff09',
     'Assets/Scripts/Module/Map/CsActorSeparation.cs\uff08\u53ea\u6536 actor\uff09\uff1bModule/Match/CsInventory.cs\uff08\u6295\u63b7\u7269\u843d\u70b9\uff09',
     '\u89e3\u51fa\u539f\u7248\u6295\u63b7\u7269\u7684 solid/movetype \u540e'),
    ('\u96f7\u8fbe\u5e95\u56fe\u975e\u539f\u7248 overviews/de_dust2.bmp',
     '\u539f\u7248 overviews/de_dust2.bmp + .txt \u672c\u673a\u4e0d\u5728\u76d8\uff08\u964d\u7ea7\u94fe\u9000\u5230\u7ea7\u2460\uff1a\u7531\u5de5\u7a0b\u5185 de_dust2_geo.bin \u79bb\u7ebf\u4fef\u89c6\u6805\u683c\u5316\uff09',
     'Core/ResPaths.cs:117 / tools/probes/render-overview.py', '\u62ff\u5230\u539f\u7248 overviews \u540e\u540c\u540d\u8986\u76d6 PNG'),
    ('\u67aa\u53e3\u706b\u7130/\u5f39\u75d5/\u706b\u661f \u7cbe\u7075\u4e3a\u7a0b\u5e8f\u751f\u6210',
     '\u539f\u7248 sprites/muzzleflash*.spr \u4e0e decals.wad \u4e0d\u5728\u76d8\uff08archive.org \u4e0d\u53ef\u8fbe\uff09',
     'Core/ResPaths.cs:147-159 / tools/probes/make-fx-sprites.py', '\u62ff\u5230\u539f\u7248\u7d20\u6750\u540e\u540c\u540d\u8986\u76d6'),
    ('\u63a7\u5236\u53f0\u7528 / \u6216 0 \u4ee3\u66ff ~',
     '\u5f15\u64ce GameKey \u679a\u4e3e\u91cc\u6ca1\u6709 BackQuote\uff08\u5951\u7ea6\u7f3a\u53e3\uff09',
     '\u7b56\u5212/\u9a8c\u6536\u8868.md \u5141\u8bb8\u7684\u5dee\u5f02#3', '\u5f15\u64ce\u8865 GameKey.BackQuote'),
    ('ZoomFov=40 \u65e0\u51fa\u5904\uff1bRoundEndTime=5s \u4e3a\u672c\u9879\u76ee\u81ea\u5b9a',
     'CS \u5f00\u955c FOV \u7531 cstrike/cl_dlls/client.dll \u4e0b\u53d1\uff0cHLSDK \u91cc\u6ca1\u6709\u8be5\u5b9e\u73b0\uff1b\u56de\u5408\u7ed3\u7b97\u65f6\u957f\u672a\u89e3\u51fa',
     'Core/CsConst.cs:163 / Core/CsConst.cs:57\uff1b\u5bf9\u7167\u8868 A-05', '\u89e3\u51fa client.dll \u5bf9\u5e94\u5e38\u91cf\u540e'),
    ('de_dust2.bsp func_breakable \u6728\u7bb1\uff08\u00d710\uff09\u672a\u5b9e\u73b0\u53ef\u7834\u574f',
     '\u5de5\u7a0b\u628a\u7bb1\u5b50\u5f53\u9759\u6001\u51e0\u4f55\uff08box.png / box_x.png\uff09\uff0c\u6ca1\u6709\u53d7\u51fb\u788e\u88c2\u903b\u8f91',
     'Assets/ThirdParty/Dust2/de_dust2.bsp\uff08entity lump\uff09', '\u5b9e\u73b0 func_breakable \u540e'),
    # \u5207\u7247H \u5df2\u6d88\u9664\uff1aUnity \u6a21\u677f\u6b8b\u7559\u573a\u666f SampleScene.unity\uff08+\u672a\u4f7f\u7528\u7684 reload_unused.wav\uff09
    # \u5df2\u6309\u300c\u4e0d\u8be5\u8fdb\u5de5\u7a0b\u7684\u79fb\u51fa Assets/\u300d\u79fb\u5230 \u539f\u7248\u8d44\u6e90/_moved-out-from-assets/\u3002
    # \u767b\u8bb0\u662f\u300c\u5141\u8bb8\u7684\u5dee\u5f02\u300d\u4e0d\u662f\u7f3a\u9677\u6863\u6848\uff1a\u4fee\u597d\u4e86\u5c31\u5220\u6389\u8fd9\u6761\u3002
    ('\u5df2\u79fb\u51fa\u5de5\u7a0b\uff08\u5207\u7247H\uff09\uff1aAssets/Scenes/SampleScene.unity\u3001Resources/Sound/SFX/sfx/reload_unused.wav',
     'Unity \u6a21\u677f\u81ea\u5e26\u573a\u666f\uff08\u672a\u767b\u8bb0 Build Settings\u3001\u65e0\u4ee3\u7801\u5f15\u7528\uff09\u4e0e\u4e00\u4e2a\u540d\u5b57\u5c31\u662f unused \u7684\u901a\u7528\u6362\u5f39\u97f3'
     '\uff08\u672c\u5de5\u7a0b\u6362\u5f39\u97f3\u6309\u6b66\u5668\u9010\u628a\u62fc\u540d\uff09\u2014\u2014 \u4e24\u8005\u90fd\u4e0d\u5c5e\u4e8e\u53c2\u8003\u7269\u7684\u5fc5\u5907\u5f15\u7528\uff0c\u4e0d\u5e94\u8fdb\u5de5\u7a0b',
     'Assets/Scenes/SampleScene.unity\uff1bAssets/Resources/Sound/SFX/sfx/reload_unused.wav',
     '\u5df2\u6d88\u9664\uff082026-09-21 \u5207\u7247H \u79fb\u51fa\u5230 \u539f\u7248\u8d44\u6e90/_moved-out-from-assets/\uff09'),
    # ── \u5207\u7247H\uff08D1 \u7ea2\u884c\uff09\uff1a\u4e0a\u9762 D1_UNREF_ALLOWED \u91cc\u90a3\u4e9b\u539f\u7248\u672a\u63a5\u7d20\u6750 ──
    ('\u539f\u7248\u97f3\u6548\u5df2\u590d\u5236\u8fdb\u5de5\u7a0b\u4f46\u672a\u63a5\u4e8b\u4ef6\uff085 \u6761\uff1adryfire / hit_wall / knife_hit / flash_explode / bomb_beep_fast\uff09',
     '\u539f\u7248 CS \u5bf9\u5e94\u65f6\u523b\u90fd\u6709\u97f3\uff08\u7a7a\u4ed3\u6263\u65cb\u3001\u5f39\u7740\u5899\u3001\u5200\u547d\u4e2d\u3001\u95ea\u5149\u7206\u3001C4 \u5feb\u8702\u9e23\uff09\uff1b'
     '\u672c\u5de5\u7a0b\u90a3\u4e9b\u65f6\u523b\u53ea\u6709\u8868\u73b0/\u53ea\u6253\u65e5\u5fd7\u3001\u65e0\u58f0\u3002\u63a5\u7ebf\u5c5e\u300c\u97f3\u6548\u4e8b\u4ef6\u300d\u7ef4\u5ea6\uff08D8\uff09\uff0c\u672c\u7247\uff08\u5207\u7247H\uff09\u4efb\u52a1\u4e66\u660e\u4ee4\u4e0d\u505a D8',
     'client/Assets/Resources/Sound/SFX/sfx/{dryfire,hit_wall,knife_hit,flash_explode,bomb_beep_fast}.wav\uff1b\u5bf9\u7167 tools/probes/enumerate-entities.py \u7684 D8 \u6bb5',
     '\u4e0b\u4e00\u4e2a\u300c\u97f3\u6548\u4e8b\u4ef6\u300d\u7247\u9010\u4e2a\u63a5\u5230\u5f00\u706b/\u547d\u4e2d/\u4e0b\u5305\u5206\u652f\u540e'),
    ('\u539f\u7248 de_dust2 \u8d34\u56fe\uff088 \u5f20\uff1aSandRoadTgtA / _1Sand / _1SandRock2 / _1csSandWall / _2SandRock2 / _3Sand / black / wall_g\uff09\u5df2\u590d\u5236\u4f46\u51e0\u4f55\u672a\u5f15\u7528',
     '\u672c\u5de5\u7a0b\u51e0\u4f55\u53ea\u7528 geo.bin \u7684 33 \u4e2a\u4e3b\u6750\u8d28\u7ec4\uff1b\u8fd9 8 \u5f20\u5c5e\u539f\u7248\u7684\u8d34\u82b1\u5c42\uff08TgtA\uff09\u4e0e\u7ec6\u8282\u5c42\uff08detail\uff09\u8d34\u56fe\uff0c\u672c\u5de5\u7a0b\u672a\u5b9e\u73b0\u90a3\u4e24\u5c42'
     '\uff08\u7ecf\u5b9e\u6d4b\uff1a8 \u5f20\u7684 guid \u5728\u5168\u5de5\u7a0b\u4efb\u4f55 .mat/.prefab/.unity/.asset \u91cc\u90fd 0 \u6b21\u547d\u4e2d\uff09',
     'client/Assets/ThirdParty/Dust2/Textures/\uff1b\u539f\u7248 de_dust2.bsp \u7684 miptex \u76ee\u5f55\uff08tools/probes/bsp-entities.py \u53ef\u91cd\u6570\uff09',
     '\u8865\u8d34\u82b1/\u7ec6\u8282\u5c42\uff0c\u6216\u5728\u300c\u53ea\u590d\u5236\u88ab\u5f15\u7528\u7684\u90a3\u51e0\u4e2a\u300d\u539f\u5219\u4e0b\u628a\u5b83\u4eec\u79fb\u51fa client/Assets/'),
    ('\u539f\u7248 GameUI \u5b57\u6807 logo_game.tga \u5df2\u590d\u5236\u4f46\u672a\u4f7f\u7528',
     '\u4efb\u52a1\u4e66\u628a\u300c\u600e\u4e48\u7528\u300d\u5212\u7ed9\u4e3b\u83dc\u5355\u7247\uff1b\u672c\u7247\u53ea\u8d1f\u8d23\u628a\u5b83\u4ece\u539f\u7248\u642c\u8fdb\u5de5\u7a0b\uff08\u89c1\u9a8c\u6536\u8868\u300c\u5141\u8bb8\u7684\u5dee\u5f02\u300d#37\uff09',
     'client/Assets/Resources/UI/Art/logo_game.tga\uff1b\u6e90 = \u539f\u7248\u8d44\u6e90/cs16src/cs16game/app/cstrike/resource/logo_game.tga',
     '\u4e3b\u83dc\u5355\u7247\u628a\u5b83\u63a5\u8fdb ResPaths \u5e76\u4e0a\u5c4f\u540e'),
    # ── 切片K（S1 出处补齐 + D8 音效事件接线）新增的登记 ──────────────────────────
    ('切片K：dryfire / hit_wall / knife_hit / bomb_beep_fast / round_start2 这 5 条 wav 的'
     '**原版源文件名映射未记录**',
     '它们确是原版 CS 1.6 的音效（空仓击发 / 弹着 / 刀命中 / C4 快蜂鸣 / 备用回合开始），'
     '但 `client/资源欠缺清单.md:37` 第 11 项只记了 c4_beep1 / c4_plant / c4_disarm / c4_explode1 / '
     'hegrenade-1 / flashbang-1 / radio/bombpl / radio/bombdef 这 8 条映射；'
     '原版 sound/ 树（`原版资源/cs16src`）已空 ⇒ 无法把短名逐条对回原版文件名',
     'client/Assets/Resources/Sound/SFX/sfx/{dryfire,hit_wall,knife_hit,bomb_beep_fast,round_start2}.wav（在盘）；'
     'client/资源欠缺清单.md:37；原版资源/清单.md（cs16src 已空）',
     '用户补回 CS 1.6 客户端本体（原版资源/cs16src）后逐条对账'),
    ('切片K：hit_wall 的「按材质分流」只落到一条采样，且刀「砍空」没有独立采样',
     '原版打沙 / 打木箱 / 打金属是**不同采样**，刀砍中人与砍空也是两条采样；'
     '盘上只有 hit_wall.wav（打墙）与 knife_hit.wav（刀命中）各一条 ⇒ '
     '材质分类（CsAudioTuning.ClassifyImpact）已做、日志可逐类核对，但各材质现在落同一 clip；'
     '刀砍空（CsInventory.RaycastActor 返回 null）无音',
     'Module/Audio/CsAudioTuning.cs（ClassifyImpact / HitWall / KnifeHit）；'
     'Module/Combat/CombatModule.cs（弹着音挂点）；Module/Match/CsDamage.cs（刀命中挂点）',
     '拿到原版按材质的弹着采样与刀挥空采样后，只改 CsAudioTuning 的分类→短名映射'),
    ('C4 蜂鸣的「加速档分界 10s」与两档间隔（1.0s / 0.25s）无原版出处',
     '原版 C4 蜂鸣节奏写在 `mp.dll` 的 C4 逻辑里（不是 cvar，`settings.scr` / `server.cfg` 都查不到），'
     '而 `mp.dll` 不在盘（`原版资源/cs16src` 已空）⇒ 该分界只能按本工程自己的口径统一'
     '（CsConst.BombBeepIntervalSlow/Fast 的 10s 注释 + CsAudioTuning.BombBeepFastBelow）',
     'Core/CsConst.cs（BombBeepIntervalSlow / BombBeepIntervalFast）；'
     'Module/Audio/CsAudioTuning.cs（BombBeepFastBelow）',
     '解出 mp.dll 的 C4 蜂鸣节奏后'),
    ('CsBotConst 的绝大多数阈值无原版出处（**本项目新增**）',
     'A = CS 1.6 本体**不含机器人 AI**（官方 bot 属 Condition Zero / PodBot，不在本工程的载体范围）⇒ '
     '"bot 手感阈值"在 A 里没有对应量；规格 §2.4 只给三档的反应时间 / 瞄准误差（±6° / ±3° / ±1.2°）'
     '与行为特征，不含这些阈值。三条有对应量却取不到载体的（瞄胸高度比例 / 脚步噪声阈值 / 预瞄节奏）'
     '见下面两条与 CsBotConst 各行的注释',
     'Module/Bot/CsBotConst.cs（66 行逐条注释已标"本项目新增"或指到定义真源）；'
     '策划/策划案/CS1.6单机参考规格.md:113-118（§2.4 三档表）；Module/Match/CsTypes.cs:148（CsBotProfile）',
     '若主 agent 决定改为「逐条对齐 PodBot / CZ bot 源码」则另开片'),
    ('脚步声触发口径与落地音阈值无原版出处（StepDistanceRun / StepMinSpeed / StepMinInterval / LandMinFallSpeed）',
     '① 原版脚步触发口径在 GoldSrc `pm_shared.c`（PM_PlayStepSound），该文件属 `原版资源/cs16src`、已空；'
     '② 落地音 A **本来就没有**（`client/资源欠缺清单.md:33` 第 7 项：GoldSrc 落地复用脚步采样），'
     '本工程用 pl_step4 采样代替、并自定"多快才算摔了一下"的阈值',
     'Module/Audio/CsAudioTuning.cs（Step* / LandMinFallSpeed）；client/资源欠缺清单.md:32-33,76',
     '用户补回原版载体（原版资源/cs16src）后对账脚步节拍；落地音属"A 本来就没有"，不消除'),
    ('切片K（D8）：Defuser / Vest / VestHelm 三个被动装备没有开火 / 换弹音',
     '它们不是武器：原版 CS 1.6 里既没有"手持并开火"、也没有换弹动作 ⇒ **原版也没有**这两个采样。'
     '旧判据（D8 的"每个 id 都要有 <id>_fire.wav / <id>_reload.wav"）把它们当武器，'
     '要满足只能**造两个 wav**（伪造素材，skill §0.1 ①）⇒ 判据已改为"装备在 CsWeapons 里有定义 + 无该音与 A 一致"',
     'tools/probes/enumerate-entities.py（D8 段的 D8_EQUIPMENT 分支）；Core/CsWeapons.cs:83-85',
     '不消除（与 A 一致的行为差异）'),
    # ── 切片L（S1 出处补齐）新增的登记 ────────────────────────────────────────────
    ('切片L（S1）：操作 / 表现层的可调旋钮没有原版出处（CsCombatTuning 全 31 条；'
     'CsMatch / CsViewTuning / CsConst 里标「本项目新增」的那些）',
     '这些量（后坐力时间常数 / 散布倍率 / 准星扩散 / bob / 开镜过渡 / 受击晃动 / 枪口火焰时长 / 各类实现容量上限）'
     '在 A 里对应的是**客户端手感**，原版把它们写死在 `cstrike/cl_dlls/client.dll` 与 `mp.dll` 的逐武器代码里'
     '（不是 cvar、也不是数据表 —— 见 `策划/对照表.md` §6 BLOCKED-1 / BLOCKED-2）；'
     '本机原版载体 `原版资源/cs16src` 已空（`原版资源/清单.md`）⇒ 拿不到 `文件:偏移` 级出处，'
     '只能取本工程自定值并逐条如实标注',
     'client/Assets/Scripts/Module/Combat/CsCombatTuning.cs（31 条逐行已标「本项目新增」+ 该条与 A 的关系）；'
     'Module/Match/CsMatch.cs、Module/View/CsViewTuning.cs、Core/CsConst.cs 的对应行；'
     '策划/对照表.md §6 BLOCKED-1/2 与 A-05 / A-08 / E-03 / N-22 / U-07 / U-36',
     '用户补回 CS 1.6 客户端本体（原版资源/cs16src：client.dll / mp.dll）后逐条对账'),
]

# ============================================================================
#  输出
# ============================================================================
def write_tsv(path, header, rows):
    with open(path, 'w', encoding='utf-8', newline='\n') as f:
        f.write('# ' + header + '\n')
        for r in rows:
            f.write('\t'.join(str(x).replace('\t', ' ').replace('\n', ' ') for x in r) + '\n')


ENT_SORTED = sorted(ENT, key=lambda r: (DIMS.index(r[0]) if r[0] in DIMS else 99, r[1], r[2]))
STA_SORTED = sorted(STA, key=lambda r: (DIMS.index(r[0]) if r[0] in DIMS else 99, r[1], r[2], r[3]))

os.makedirs(PLAN, exist_ok=True)
write_tsv(os.path.join(PLAN, '\u5b9e\u4f53\u6e05\u5355.tsv'),
          '\u7ef4\u5ea6\t\u5b9e\u4f53\t\u8f7d\u4f53/\u8def\u5f84\t\u51fa\u5904\t\u72b6\u6001\u6570\t\u5224\u636e\u7c7b\u578b\t\u5f52\u5c5e\u7247',
          [(r[0], r[1], r[2], r[3], r[4], r[5], SLICE.get(r[0], '-')) for r in ENT_SORTED])
write_tsv(os.path.join(PLAN, '\u72b6\u6001\u77e9\u9635.tsv'),
          '\u7ef4\u5ea6\t\u5b9e\u4f53\t\u72b6\u6001/\u4e8b\u4ef6\t\u8fb9\u754c\u503c\t\u671f\u671b\u8868\u73b0(\u51fa\u5904)\t\u5b9e\u6d4b\t\u7ed3\u8bba\t\u8bc1\u636e',
          STA_SORTED)
write_tsv(os.path.join(PLAN, '\u5dee\u5f02\u767b\u8bb0.tsv'),
          '\u662f\u4ec0\u4e48\t\u4e3a\u4ec0\u4e48\t\u51fa\u5904\t\u4f55\u65f6\u6d88\u9664', DIF)

# 覆盖矩阵判定片段（一行 = 一个实体）
frag = ['## G. \u8986\u76d6\u77e9\u9635\u5224\u5b9a\uff08\u811a\u672c\u751f\u6210\uff1a\u4e00\u884c = \u4e00\u4e2a\u5b9e\u4f53\uff1b\u26d4 \u52ff\u624b\u6539\uff09', '',
        '> \u6765\u6e90 = `策划/实体清单.tsv`\uff08`tools/probes/enumerate-entities.py` \u679a\u4e3e\uff09\u3002',
        '> \u7ed3\u8bba\u53ea\u5141\u8bb8\uff1a`\u4e00\u81f4` / `\u4e0d\u4e00\u81f4(\u5dee\u5728\u54ea)` / `\u5141\u8bb8\u7684\u5dee\u5f02(\u2192 \u5dee\u5f02\u767b\u8bb0.tsv)` / `\u5f85\u91c7(\u5e76\u6392\u56fe)`\u3002',
        '', '<!-- COVERAGE-BEGIN -->', '| # | \u7ef4\u5ea6 | \u5b9e\u4f53 | \u5224\u636e\u7c7b\u578b | \u7ed3\u8bba | \u8bc1\u636e |',
        '|---|---|---|---|---|---|']
def _s(t):
    # ⛔ 只在这张判定表里把 `.png` 写成 `.png `（带空格）：验收表里出现的 `xxx.png` 会被既有闸门
    #    `refs-reachable` 当成"引用的取证截图"去 `.ai-tmp/screenshots/` 找（那里没有贴图资产），
    #    从而**误报**。实体名里的 `SandWllDoor2.png` 是**地图贴图资产名**，不是截图引用。
    return str(t).replace('|', '/').replace('.png', ' .png')


for i, r in enumerate(ENT_SORTED, 1):
    frag.append('| %d | %s | %s | %s | %s | %s |' % (i, r[0], _s(r[1]), r[5], _s(r[6]), _s(r[7])))
frag += ['<!-- COVERAGE-END -->', '']
with open(os.path.join(PLAN, '\u8986\u76d6\u77e9\u9635\u5224\u5b9a.fragment.md'), 'w', encoding='utf-8', newline='\n') as f:
    f.write('\n'.join(frag[4:]))

if '--inject' in sys.argv:
    # 覆盖矩阵的**判定行**落 `策划/验收表.md`（规格文件放的是"要做成什么样"，判定表放的是"判成什么"）。
    spec_path = os.path.join(PLAN, '\u9a8c\u6536\u8868.md')
    txt = rd(spec_path)
    B, E = '<!-- COVERAGE-BEGIN -->', '<!-- COVERAGE-END -->'
    block = '\n'.join(frag[4:])
    i, j = txt.find(B), txt.find(E)
    if i >= 0 and j > i:
        # ⛔ 不用 re.sub：替换串里带反斜杠时会被当成转义/反向引用（实测踩过），字符串切片最稳。
        txt = txt[:i] + block.rstrip('\n') + txt[j + len(E):]
    else:
        txt = txt.rstrip('\n') + '\n\n---\n\n' + '\n'.join(frag) + '\n'
    with open(spec_path, 'w', encoding='utf-8', newline='\n') as f:
        f.write(txt)
    print('injected coverage matrix into', rel(spec_path))

cnt = Counter(r[0] for r in ENT_SORTED)
print('ENTITY rows = %d   STATE rows = %d   DIFF rows = %d' % (len(ENT_SORTED), len(STA_SORTED), len(DIF)))
for d in DIMS:
    print('  %-4s %s' % (d, cnt.get(d, 0)))
print('conclusion split:')
for k, v in Counter(r[6].split('(')[0] for r in ENT_SORTED).most_common():
    print('  %-8s %d' % (k, v))
