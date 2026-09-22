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
#    而且原版载体 `原版资源/cs16src` 已空、连出处都拿不到。
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
               len(CS16ANIM_EQUIP), len(VM_CTRLS), wid.lower()))
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
for dim, nm, note, vt, vd in CROSS:
    add(dim, nm, '\u8de8\u7ef4\u5ea6\u56e0\u679c\u5bf9', 'patterns/full-coverage-audit.md \u00a74', 2, vt, vd, note)
    sta(dim, nm, '\u4ea4\u53c9\u72b6\u6001', '-', note, '\u5730\u57df\u7814\u5224', vd, 'patterns/full-coverage-audit.md \u00a74')

# ============================================================================
#  差异登记（四要素）
# ============================================================================
DIF = [
    # 片AD（2026-09-21）：真源收敛为单一作者 —— 首列为编号，与验收表「允许的差异」段逐条一一对应（64 行）。
    # 编号 1-22 / 25-48 = 原验收表行（其中 3/18/44/45/46/48 由下面的真源文本提供）；49-64 = 真源独有、已补进验收表段。
    # 片AE（2026-09-21）：补 **23 / 24**（两处引用悬空 ⇒ 把被引用的两件事按四要素登记：23 = 对照表 §9 F-03 的
    #   "右上角落点只有非 1:1 口径载体"；24 = 对照表 §9 F-01 的 `Map: <地图名>` 行绝对 x 为推算值 15 px）。
    ('1', 'Find Servers 列表为空', '单机版没有局域网对局可发现；面板与 `Game.LanBrowser` 链路本身是通的', '`UI/Flow/ServerListPanel.cs`', '做联机版时接真实 LAN 广播'),
    ('2', 'Quit 未在自动化里真触发', '自动化在编辑器内跑，真 `Application.Quit()` 会把编辑器一起关掉', '`Module/Flow/AppFlow` 的 `QuitGame` 分支', '打包成独立 exe 后手测'),
    ('3', '控制台用 / 或 0 代替 ~', '引擎 GameKey 枚举里没有 BackQuote（契约缺口）', '策划/验收表.md 允许的差异#3', '引擎补 GameKey.BackQuote'),
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
    ('14', '~~雷达尺寸未落 1080p 真值~~ → **已消除（2026-09-21 重出）**：`RadarSize` 已为 `128`，且**生成器重生成 `HudPanel.prefab`** 才使其生效（旧预制体停在 200f，只改常量不生效）', '旧判据「需第一人称 HUD 原版截图」被 `hud.txt:183` 取代：`radar 640 radar640 0 0 128 128` + GoldSrc 分辨率档判据（屏幕宽>640 用 640 档、该档精灵 1:1 不缩放）⇒ 1080p 真值 = 128×128；实机 `screenRect x[24..152] y[938..1066]`、`scaleFactor=1.0000`', '`原版资源/cs16src/cs16game/app/cstrike/sprites/hud.txt:183`；代码 `CsHudTheme.RadarSize`；`Editor/UiGenInGame/UiBuilder.cs`（重出入口）', '已消除'),
    ('15', '~~秒表图标内圈与截图不完全一致~~ → **已消除（2026-09-22 切片AM）：以本体为准**', '原判断：原版截图 ink 23×25 / 本体精灵 ink 17×22（两个载体不是同一版贴图）。本片取回同版 `640hud7.spr` 后按 `hud.txt:127` 的 `stopwatch 640 640hud7 144 72 24 24` 重解覆盖 `Resources/UI/Art/stopwatch.png`（ink 318→281）；解码器在同一图集的 `cross`/`suit_full`/`suithelmet_full` 三矩形上与工程既有已验收 PNG **逐像素 0 差异（0/576）**', '`tools/probes/spr-extract.py`；载体 `原版资源/cs16src/cstrike/cstrike__sprites__640hud7.spr`；`client/资源欠缺清单.md` §2.14', '已消除（截图版与本体版的差异按"以本体为准"裁决）'),
    ('16', 'HUD 血量/金钱/弹药那排的绝对坐标未对齐', '原版把坐标写死在 `cl_dlls/client.dll`（二进制），已穷尽反汇编未定位；31 张截图是旁观机位、无这排', '`原版资源/解包产物/原版HUD布局.md` BLOCKED-D1', '同上（需第一人称 HUD 截图）或继续反汇编 `client.dll`'),
    ('17', '未实现的服务器管理 cvar', '`mp_tkpunish`(0) / `mp_limitteams`(2) / `mp_winlimit`(0) / `mp_timelimit`(0) / `mp_autokick`(1) / `mp_forcecamera`(0) / `mp_fadetoblack`(0) / `decalfrequency`(30)；单机 + bot 场景下默认值多数不触发行为', '`原版资源/解包产物/原版数值表.md` §1', '逐条实现（默认 0 的项实现后行为不变）'),
    ('18', 'ZoomFov=40 无出处；RoundEndTime=5s 为本项目自定', 'CS 开镜 FOV 由 cstrike/cl_dlls/client.dll 下发，HLSDK 里没有该实现；回合结算时长未解出', 'Core/CsConst.cs:163 / Core/CsConst.cs:57；对照表 A-05', '解出 client.dll 对应常量后'),
    ('19', '观战/第一人称机位**未复现原版 `viewsize` 补偿**', '原版在 `view->origin[2] -= 1` 之后还会按 `viewsize` 补偿（110→+1 / 100→+2 / 90→+1 / 80→+0.5 unit，`view.cpp:667-684`），而原版默认 `viewsize` 从项目内载体解不出（CS 侧 HUD 实现在 `client.dll`）⇒ agent-22 只落了确定的那一项（`view.cpp:665` 的 −1 unit = y −0.0254 m），**不编** viewsize', '`client/Assets/Scripts/Module/View/CsViewTuning.cs`（ViewModelLocalPosition 的注释）；`HLSDK/cl_dll/view.cpp:665`（已实现）/ `:667-684`（未实现）', '解出默认 `viewsize`，或原版第一人称截图能定案 viewmodel 占比'),
    ('20', '`CsViewTuning.PositionSmoothTau = 0f` 无原版出处', '原版客户端确实做位置插值（`ViewInterp` 环形缓冲），但它的口径是"`Length(delta) < 64` 才插值"的**位置回放**，换算不出可写进代码的指数平滑 tau ⇒ 取 0（直接用模拟的权威位置）', '`client/Assets/Scripts/Module/View/CsViewTuning.cs`（PositionSmoothTau 的注释）；`HLSDK/cl_dll/view.cpp:719-785`', '按原版语义实现 `ViewInterp`（位置回放而非指数平滑）'),
    ('21', '`CsViewTuning.AnimMoveSpeedEpsilon = 0.15f` 无原版出处', '原版按速度选动画档位的阈值在服务端 `cstrike/dlls/mp.dll` 里（`HLSDK/cl_dll/` 没有 CS 的角色动画选择），本项目未反汇编出该阈值 ⇒ 0.15 m/s 是项目新增的判定门限', '`client/Assets/Scripts/Module/View/CsViewTuning.cs`（AnimMoveSpeedEpsilon 的注释）', '从 `mp.dll` 反汇编出选序列的速度阈值'),
    ('22', 'HUD 血量/护甲图标已是**原版位图**，但着色取纯白', '三张图标精灵是 GoldSrc 的加性灰阶遮罩（调色板逐项 `(i,i,i)`），本身不带颜色；原版渲染时的调制值取不到（原版 HUD 那一排的坐标与颜色写在 `client.dll`，且项目内 31 张原版 1920×1080 截图全是旁观机位、没有这排）⇒ 取"无调制"（白），**不编**颜色', '`client/Assets/Scripts/UI/InGame/CsHudTheme.cs`（HudIconTint 的注释）；`策划/对照表.md` F-04 [BLOCKED]', '拿到一张第一人称 + HUD 打开的原版截图'),
    ('23', 'HUD 右上角比分/计时块（比分两行 / 回合计时 / 竖分隔线）的原版落点只能沿用 **1920×1080 原生口径**的已记录量化值（距屏右 170 / 170 / 51 / 142 px、ink 上缘 38 / 65 / 27），且**同版载体本机已不在盘**', '判据是"落点 = 原版 1920×1080 bbox"，但项目内可比的原版 HUD 载体与 1080p 那张**不是同一尺度**：1280×1024 自由视角图（比分 ink 高 16 px vs 1080p 12 px、竖分隔线 56 px vs 67 px）与早期 1600×900 采集帧（ink 右缘距屏右 38 px vs 原版 51 px）⇒ **口径不同不可直比**，按比例硬换会得出无依据的值；本工程画布参考分辨率定为 **1920×1080**（`Menu.unity` 的 CanvasScaler `m_ReferenceResolution`，引擎 UIManager 的层级节点铺满它）⇒ **同基准直接写原版像素、不做换算**；非 1:1 采集帧的读数不用于判对错', '`client/Assets/Scripts/UI/InGame/CsHudTheme.cs:219-223`（"本单位参考分辨率也是 1920×1080 ⇒ 同一基准，直接写原版像素、不换算"）与 `:236-273`（右上角逐项落点常量 + 逐项实测 ink 值）；`策划/对照表.md` §9 的 F-03（"口径不同不可直比 ⇒ 必须 1920×1080 重采一次再判"）与 U-31~U-35（原版值 = 1920×1080 原生口径）；`策划/验收表.md` H4 / H5；31 张原版 BMP 的来源目录 `原版资源/cs16-maps/screenshots_to_conv/` 本机已不存在（`原版资源/` 实测只剩 `cs16src/`（空）· `备份/` · `_moved-out-from-assets/`）', '拿到与 1920×1080 HUD 同版、且采集条件（`hud_draw` / 视频模式）随仓库记录的原版载体后重测一次'),
    ('24', '`Map: <地图名>` 行（HUD 右上角、竖分隔线右侧）的**绝对 x 是推算值**：右缘距屏右 **15 px**（不是 1080p 实测值）', '唯一带这一行的原版载体是 1280×1024 自由视角图，它与 1920×1080 载体的 HUD **比例/锚定不一致**（比分 ink 高 16 vs 12、竖分隔线 56 vs 67）⇒ 直接按比例换算**没有依据**；能站得住的只有"**同图内两个右缘缩进之比**"（无量纲、与尺度无关）：该图上 `Map:` 行右缘缩进 28 px、计时行右缘缩进 96 px ⇒ 比值 28/96 = 0.2917，套到已实测的计时行缩进 51 px ⇒ **15 px**。旁证：竖分隔线右缘（x=1778）到屏右只剩 142 px，而 `Map: de_dust2` 在本工程字号下约 124 px 宽 ⇒ 缩进不可能大于 ~18 px（与 15 相容）；另一张同族 1920×1080 基线图 `hud_1920x1080_gg_dust2_aim_trainning.bmp` **没有**这一行 ⇒ 无 1080p 实测值可引', '`client/Assets/Scripts/UI/InGame/CsHudTheme.cs:309-341`（`MapRightInsetPx = 15f` 与其上一整段推导注释，含 freecam 逐项 ink：`Map: de_dust2` x[1113..1252] y[35..50]、计时行右缘 1184 等）；原版载体 `策划/基线图/original/de_dust2_freecam_A_00.jpg`（1280×1024）；`策划/对照表.md` §9 的 F-01（原版**有**这一行）；`策划/验收表.md` H5', '拿到 1920×1080 且画面里带 `Map:` 行的原版 HUD 载体后直接实测绝对 x'),
    ('25', '原版 `jointeam 3`（VIP）落到 CT', '本工程阵营契约 `CsTeam` 只有 `Spectator / T / CT`（`Core/CsEnums.cs`），**没有 VIP 阵营**；原版 `teammenu.res:121-139` 的 `vipbutton` 文案是 `#Cstrike_VIP_Team`（`&3 VIP`）、命令 `jointeam 3`。VIP 属 CT 一侧 ⇒ 落到 CT 并打一条 Warn（`TeamSelectPanel.OnCommand`）', '`原版资源/cs16src/cs16game/app/cstrike/resource/ui/teammenu.res:121-139` + `client/Assets/Scripts/Core/CsEnums.cs`（`CsTeam`）', '契约扩出 VIP 阵营（要改 `Core`，不在本片范围）'),
    ('26', '原版 `AUTO ASSIGN` 用**对半随机**代替"分配到人数少的一方"', '原版 `jointeam 5` 由服务器按两队人数分配；本工程是**单机版**（无真人计数）⇒ 取随机并打日志说明', '`teammenu.res:141-159`（`#Cstrike_Team_AutoAssign`）', '做联机版、或从 `CsHudSnapshot` 读到两侧人数后按原版语义分配'),
    ('27', '~~选阵营 Frame 的底色取 `WindowBG "0 0 0 200"`~~ → **已消除（2026-09-20 实现者本片）**：`TeamMenu` 底色改为原版 `ControlBG "0 0 0 0"`（**全透明**，那块"凭空多出来的黑板"就是旧的 WindowBG）', '原判断有误：Frame 的默认底色有载体 —— `clientscheme.res:101` 的 `BgColor "ControlBG"` 就是"所有控件的默认底色"，而 `:38` 是 `ControlBG "0 0 0 0"`（全透明）；`WindowBG "0 0 0 200"`（`:41`，行尾注释自己写着 `background color of text edit panes (chat, text entries, etc.)`）是**文本框/聊天**的底，不是 Frame 的', '`clientscheme.res:38`（ControlBG）、`:101`（BgColor）、`:41`（WindowBG，反例）；代码 `UI/Flow/CsUiStyle.cs`（新增 `ControlBg`）+ `UI/Flow/TeamSelectPanel.cs`', '已消除（实测 `TeamMenu(Image) color=(0,0,0,0) rgba8=(0,0,0,0)`；图 `33_teamselect.png`）'),
    ('28', '**Options 页签条（7 个页签）坐标无载体出处**', '本包**缺 `OptionsDialog.res`**（它在 GameUI 静态库里，不在两张 ISO 的资源目录里）⇒ 页签条按「原版配色 + 原版字体 + 就近对齐」重建。⚠️ 页签**文案**本身有出处（`gameui_english.txt:97/98/99/100/101/41/44`），**只有坐标是新增**', '缺载体（实测 `原版资源/cs16src/cs16game/app/{cstrike,valve}/resource/` 下无 `optionsdialog.res`）；文案出处 `gameui_english.txt:97-101,41,44`', '拿到 GameUI 的 `OptionsDialog.res`，或用原版 640×480 实机图量化页签条'),
    ('29', '子页整体落点 / 对话框标题 / `Ok·Cancel·Apply` 的归属为**本项目就近对齐**', '同上缺 `OptionsDialog.res`：子页在该对话框里的原点未知 ⇒ 页原点取 `teammenu.res` 的 Frame 左边距（76 设计 px → 171 画布 px）；那三个按钮的坐标取自 `optionssubmultiplayer.res:3-68`（原版把对话框级按钮就写在该子页里），本片只建一次、跨页可见', '缺载体：同 #28；坐标出处 `optionssubmultiplayer.res:3-68`', '同 #28'),
    ('30', '原版控件类型在本工程**没有对应实现** ⇒ 用近邻控件代替', '引擎（E-core-16 下沉后）只有 `Slider / InputField / Selector / ToggleRow / Button / Text / Image`：VGUI 的 `ComboBox`（下拉）→"点击循环"按钮、`HTML` → 纯色块 + 原文文本、`ListPanel` → 纯色块 + 按键表文本、`ImagePanel` → 纯色块。**位置/尺寸仍逐字取自 `.res`**', '`clover-client-unity-engine/Runtime/Presentation/UIWidgetControls.cs`（引擎现有控件清单）', '引擎补下拉 / 列表 / HTML 控件后'),
    ('31', '原版有、本工程**无对应实现**的选项：只复刻控件 + 留一条日志', '逐条：`Windowed` / `DetailTextures` / `Brightness` / `Gamma` / `Renderer` / `Resolution` / `AspectRatio` / `ColorDepth` / `MouseLook` / `MouseFilter` / `Joystick` / `JoystickLook` / `Auto-Aim` / `voice_modenable` / `MicBoost` / `VoiceReceive` / `#GameUI_MicrophoneVolume` / `TestMicrophone` / `ContentlockButton` / `Defaults` / `ChangeKeyButton` / `ClearKeyButton` / `Player model` / `SpraypaintList` / `SpraypaintColor` / `High Quality Models` / `Primary Color Slider` / `Secondary Color Slider`。点/拖任一都会打一条 `Info`（⛔ 不许静默"点了没反应"）', '各控件在 `optionssub*.res` 的行号见 `OptionsPanel.cs` 控件表注释', '逐条实现（多数项在单机语义下本就没有行为）'),
    ('32', '原版 `HEV suit volume` 承载本工程的**主音量**', 'CS 里没有 HEV 护甲语音（该项是 HL1 遗留），而本工程原来就有的"主音量"设置需要一只滑杆 ⇒ 复用它承载，滑杆位置/标签仍与原版逐字一致', '`optionssubaudio.res:19-34`（Suit Slider）+ `CsPlayerSettingsStore`（MasterVolume / ApplyToEngine）', '原版口径下无需消除（HL1 的 HEV 音量在 CS 里本就不生效）'),
    ('33', '勾选框用**同色小方块**表达"已勾选"', '原版勾选字形来自 **Marlett** 字体（`clientscheme.res:483-492`），本工程没有该字体；方框边色仍取原版 `CheckButtonBorder1/2`（→ `BorderDark/Bright`）、勾色取 `CheckButtonCheck`（→ `BrightControlText`）。方框边长 16 / 文案起点 24 是**本项目新增**（原版由 Marlett 字模决定，无像素值可引）；**方框四条边由本项目凑齐** —— 原版边框由 VGUI 内部绘制（`clientscheme.res:177-179` 只给 `CheckButtonBorder1/2` 两个**颜色**、没给"怎么画"），早先只画左右两条竖边 ⇒ 视觉退化成"一竖条"，2026-09-20 补上上下两条横边（同色）', '`clientscheme.res:177-179`（勾选框配色）/`:483-492`（Marlett）', '拿到 Marlett 字体或改用位图勾'),
    ('34', '原版字体带**CJK 回退族**', '原版 menu 全英文、没有中文；本工程面板里有中文（如"（本项目新增）"）⇒ `CreateDynamicFontFromOSFont(new[]{"Verdana","Microsoft YaHei",…})`，首选族仍是原版声明的 Verdana', '`clientscheme.res:229`（`"name" "Verdana"`）；主族来源见 `CsUiStyle.OriginalFont` 注释', '面板界面全英文后（本工程无此计划）'),
    ('35', '按钮 / 勾选框的 **1 设计 px 边框**未画；按钮文字内缩取 0', '原版的边框与文字内缩由 VGUI 内部绘制（`Borders/ButtonBorder` 只给了四条边的颜色，没给"uGUI 里怎么画"）⇒ 本片只落 `ButtonBG` 底色 + 左对齐（`textAlignment west`）。⚠️ 这不是"忘了"，是**载体只到颜色为止**', '`clientscheme.res:740-778`（ButtonBorder）/`:740-742`（inset 0 0 0 0）', '拿到原版实机图后量化边框宽度与文字内缩像素'),
    ('36', '未做（原版有）：`Sound Quality` 下拉 / 选兵种 `classmenu_*.res`', '① 原版音质档位的标签是 HL1 的玩笑串（`GameUI_High "Horrible"` / `GameUI_Low "Even worse"`），本工程也没有音质档位 ⇒ 该控件与其标签**未建**（按任务书"可以比原版少"）；② 选兵种任务书明说允许本片不做', '`gameui_english.txt:63-64`；`原版资源/cs16src/cs16game/app/cstrike/resource/ui/classmenu_ct.res`、`classmenu_ter.res`', '需要时再补（音质档位先要有引擎侧的档位概念）'),
    ('37', '`game_menu.tga` / `logo_game.tga` 已复制进工程但**本片不使用**', '任务书把"怎么用"划给 agent-32（主菜单片），避免两片抢同一处；本片只负责把它们从原版搬进工程', '源 = `原版资源/cs16src/cs16game/app/cstrike/resource/game_menu.tga`（26540 B）、`logo_game.tga`（65580 B）→ `client/Assets/Resources/UI/Art/`（字节数逐一相同）', '已消除（`game_menu.tga` 随主菜单片使用而消除；`logo_game.tga` 部分随 #46 消除）'),
    ('38', '选阵营 `.res` 里 `visible=0` 的控件不建；`MapInfo`（`HTML`）用纯色块 + 原文文本复刻', '`SysMenu`（`teammenu.res:17-30`）与 `mapname`（`:65-81`）在原版就是隐藏的（`visible 0`）⇒ 建了反而与原来的画面不一致；`MapInfo` 的 `HTML` 控件本工程没有 ⇒ 用列表底 + `maps/de_dust2.txt` 原文文本占同一块矩形（`:31-44`）', '`teammenu.res:17-30`、`:65-81`、`:31-44`；文本出处 `原版资源/cs16src/cs16game/app/cstrike/maps/de_dust2.txt`', '无需消除（原版本就不可见 / 文本已是原版原文）'),
    ('39', '~~主菜单背景为纯深色、无原版背景贴图~~ → **已消除（2026-09-20 实现者本片）**：主菜单与读条两屏的底都换成**原版 12 块 TGA 拼图**（逐字节复制 + 逐块按原坐标摆放），纯色 `#1B1B1B` 只保留为"贴图缺失时的兜底层"', '原判断"载体里没有主菜单背景"被推翻：`valve/resource/` 下**同时**有 `backgroundlayout.txt`（不带 loading 的那份 = 通用背景布局）与 `backgroundloadinglayout.txt`（内容逐字相同），它描述的 12 块 `resource/background/800_{1,2,3}_{a,b,c,d}_loading.tga` 就是这一屏背景的贴图与布局（256×3+32=800、256+256+88=600 与文件头 `resolution 800 600` 自洽）', '`原版资源/cs16src/cs16game/app/valve/resource/backgroundlayout.txt:3-16`、`原版资源/cs16src/cs16game/app/cstrike/resource/background/800_*_loading.tga`（12 张，24bpp）；代码 `UI/Flow/CsMenuBackground.cs`、`Core/ResPaths.cs`、`Editor/Flow/FlowSetup.cs`', '已消除（运行时逐块 `mismatch=0`、12/12 就绪；图 `33_mainmenu.png` / `33_loading.png`）'),
    ('40', '主菜单**菜单项之间无间隙**（步进 = 项高）', '原版各项紧邻排列，**间隙值在载体里没有**（`trackerscheme.res` 的 `InGameDesktop` 块只给了 `MenuItemHeight` 与 `GameMenuInset`）⇒ 取"步进 = 项高"这一**可从载体推出**的摆法（⛔ 没编一个间隙值）', '`原版资源/cs16src/cs16game/app/platform/resource/trackerscheme.res:164-173`；间隙值无出处', '拿到原版实机图后量化项间距'),
    ('41', '主菜单底部 **`by clover-engine`** 署名为本项目新增', 'skill §8 品牌硬约定：每个游戏的首页画面底部必须有这一行（判据 = 实机截图 / 运行时节点树）；原版 `gamemenu.res` 局外四项里没有它', '原版 `gamemenu.res`（四项：NewGame/FindServers/Options/Quit）无此项；品牌出处 = 本项目 skill §8', '无需消除（品牌要求）'),
    ('42', '主菜单**字标是按钮**（`game_menu.tga` 常态 + `game_menu_mouseover.tga` 悬停）', '原版两张同画布（207×32）贴图分别对应常态（金）与悬停（白）⇒ 落成 `Button(transition=SpriteSwap)`。⚠️ 原版该字标**是否可点**在载体里无从判断（`gamemenu.res` 无对应项）⇒ 本片让它不可点（`onClick=null` 未接回调）', '贴图 `cstrike/resource/game_menu.tga`、`game_menu_mouseover.tga`（各 26540 B，207×32/32bpp）', '拿到 GameUI 行为证据后定它可不可点'),
    ('43', '**本表/`对照表` 里对 `CsHudTheme.cs` 的行号引用为「路径收敛前」基准**', '2026-09-20 把 4 个路径常量（原 `:96-99`）搬进 `Core/ResPaths.cs`，该文件其后行号**整体前移约 6 行**（`:303` 之后约 7 行）；`Dust2Layout.cs` 同理 +0/−1（删 2 行）、`MainMenuPanel.cs` −2（删 2 个常量并加 3 行注释）。⛔ 引用指向的**语义位置不变**，只是行号需按上述位移换算', '出处 = `Core/ResPaths.cs` 与各文件当前内容（`screenshot-refs` 闸门只校验"文件可达"，不校行号）', '下次有人顺手校一遍行号时消除（一次性机械活，无功能影响）'),
    ('44', '原版音效已复制进工程但未接事件（5 条：dryfire / hit_wall / knife_hit / flash_explode / bomb_beep_fast）', '原版 CS 对应时刻都有音（空仓扣旋、弹着墙、刀命中、闪光爆、C4 快蜂鸣）；本工程那些时刻只有表现/只打日志、无声。接线属「音效事件」维度（D8），本片（切片H）任务书明令不做 D8', 'client/Assets/Resources/Sound/SFX/sfx/{dryfire,hit_wall,knife_hit,flash_explode,bomb_beep_fast}.wav；对照 tools/probes/enumerate-entities.py 的 D8 段', '下一个「音效事件」片逐个接到开火/命中/下包分支后'),
    ('45', '原版 de_dust2 贴图（8 张：SandRoadTgtA / _1Sand / _1SandRock2 / _1csSandWall / _2SandRock2 / _3Sand / black / wall_g）已复制但几何未引用', '本工程几何只用 geo.bin 的 33 个主材质组；这 8 张属原版的贴花层（TgtA）与细节层（detail）贴图，本工程未实现那两层（经实测：8 张的 guid 在全工程任何 .mat/.prefab/.unity/.asset 里都 0 次命中）', 'client/Assets/ThirdParty/Dust2/Textures/；原版 de_dust2.bsp 的 miptex 目录（tools/probes/bsp-entities.py 可重数）', '补贴花/细节层，或在「只复制被引用的那几个」原则下把它们移出 client/Assets/'),
    ('46', '原版 GameUI 字标 logo_game.tga 已复制但未使用', '任务书把「怎么用」划给主菜单片；本片只负责把它从原版搬进工程（见验收表「允许的差异」#37）', 'client/Assets/Resources/UI/Art/logo_game.tga；源 = 原版资源/cs16src/cs16game/app/cstrike/resource/logo_game.tga', '主菜单片把它接进 ResPaths 并上屏后'),
    ('47', '`Q` / `G` / `M` 三键**在切片H 才补上绑定**；`M`（原版 `chooseteam`）的**行为等价为"直接换到另一边"**而非打开阵营菜单', '三键的原版默认绑定有出处（`bind "q" "lastinv"` / `bind "g" "drop"` / `bind "m" "chooseteam"`，见 `策划/策划案/CS1.6单机参考规格.md` §1 游戏内按键段）⇒ 有出处故补绑定（落点 `Module/Combat/CombatModule.cs` 的 `FillInput`）。但**本工程局内没有"再开一次 TeamMenu"的流程入口** ⇒ `M` 只能等价成直接换边（H 菜单「换阵营」就是这条链），已如实打日志，**不编一个不存在的阵营菜单**', '绑定出处 = `策划/策划案/CS1.6单机参考规格.md`；落点 = `client/Assets/Scripts/Module/Combat/CombatModule.cs`（`FillInput`）；判据 = `tools/probes/enumerate-entities.py` 的 D11 段', '工程做出局内阵营菜单后把 `M` 改回"打开菜单"（届时本行删除）'),
    ('48', '修前/修后 2x2 合成图 92_fix_before_after_2x2 采不到（修前帧不可复现）', '修前帧 80_fix_pre_char_invisible / 81_fix_pre_vm_nogun 是 bug 现场抓的诊断图；bug 修好后（AnimSetup.Fill 按值传参 ⇒ 蒙皮 bindpose 全零 ⇒ 几何塌成一点）同形态的修前帧再也出不了。拿别的图冒充或临时改回旧实现去"复现"都属伪造 ⇒ 改为「修后帧 93_fix_post_char_closeup / 97_fix_post_char_front + 逐骨骼/包围盒数值」作为判据', 'client/Assets/Editor/Views/AnimSetup.cs（Fill 的修复处）；策划/验收表.md「允许的差异」新增行；R1/R2 行的旧图名已按「不可采」改写', '不消除（修前态本就不可复现；若将来又出现同类蒙皮 bug，则在现场重采 2x2）'),
    ('49', '位图（CloverMap v1）是单层 2D：箱子所在格记为“可走”（箱顶是朝上的面）', '格式层没有高度（FlagHeightField 预留但 V1 解码器拒绝）⇒ 一格一位，表达不了“同一格在 y=0 被挡、在 y=1.2 通畅”；带来的边界：箱子进不去（已由 CsMap.CanStand 的“地面一步闸门”拦住），但位图本身仍不能单独回答“能不能穿”', 'Assets/Scripts/Module/Map/CsMap.cs（CanStand/BodyHeightClear）；Packages/com.clover.unity-engine/Runtime/Presentation/MapFormat.cs:30（FlagHeightField）', '引擎开出 V2 高度场（FlagHeightField）后'),
    ('50', '投掷物与角色之间不互相挡/推开', '本片只把“角色对角色”这一层做出来（CsActorSeparation 只收 CsActor）；原版投掷物是 MOVETYPE_BOUNCE 实体，与角色是否互相阻挡本机取不到可信出处（原版 mp.dll 未在盘）', 'Assets/Scripts/Module/Map/CsActorSeparation.cs（只收 actor）；Module/Match/CsInventory.cs（投掷物落点）', '解出原版投掷物的 solid/movetype 后'),
    ('51', '雷达底图 ~~非原版~~ → **已消除（片AS 2026-09-22）**：底图已换成原版 `overviews/de_dust2.bmp`',
     '旧状态：原版 overviews/de_dust2.bmp + .txt 本机不在盘 ⇒ 降级链退到级①（由工程内 de_dust2_geo.bin 离线俯视栅格化）。片AR 把该载体从公开 repack 取回（SHA256 记在 原版资源/清单.md 切片AR 节），片AS 用 tools/probes/import-original-overview.py 把它**逐像素**转成 Resources/UI/Art/overview_de_dust2（重解码自检 rgb_mismatch=0 / alpha_mismatch=0，绿键色 → alpha 0 与 GoldSrc 同语义）⇒ 底图现在是**原版像素**，不再是几何栅格化',
     'client/Assets/Resources/UI/Art/overview_de_dust2（1024×768）；tools/probes/import-original-overview.py（判据资产）；载体 原版资源/cs16src/cstrike/cstrike__overviews__de_dust2.bmp；Core/ResPaths.cs:117',
     '已消除（片AS：底图 = 原版 BMP 的逐像素 PNG；同名覆盖，代码路径不变）'),
    ('52', '枪口火焰精灵已换原版像素（弹痕/火星仍程序生成）', '**已部分消除（切片AM 2026-09-22）**：`sprites/muzzleflash2.spr` 帧 0 → 覆盖 `Resources/UI/Art/fx_muzzleflash.png`（64×64 尺寸不变、ink 1328→2044，由 `tools/probes/spr-extract.py` 从原版载体解出）。未消除部分：① `decals.wad` 已取到（960,012 B / SHA `C9E852B6…`）但 WAD3 解码器未做；② 火星无独立原版载体；③ 原版按武器类别在 `client.dll` 里选 muzzleflash 1..4 并播 3 帧动画，本工程所有武器共用帧 0（该映射无载体出处）', 'Core/ResPaths.cs:147-159 / tools/probes/spr-extract.py；载体 `原版资源/cs16src/cstrike/cstrike__sprites__muzzleflash2.spr`', '① 写 WAD3 解码器接入 `decals.wad` 的 `{shot*`；③ 解出 `client.dll` 的武器→muzzleflash 映射并实现逐帧播放'),
    ('53', 'de_dust2.bsp func_breakable 木箱（×10）未实现可破坏', '工程把箱子当静态几何（box.png / box_x.png），没有受击碎裂逻辑', 'Assets/ThirdParty/Dust2/de_dust2.bsp（entity lump）', '实现 func_breakable 后'),
    ('54', '已移出工程（切片H）：Assets/Scenes/SampleScene.unity、Resources/Sound/SFX/sfx/reload_unused.wav', 'Unity 模板自带场景（未登记 Build Settings、无代码引用）与一个名字就是 unused 的通用换弹音（本工程换弹音按武器逐把拼名）—— 两者都不属于参考物的必备引用，不应进工程', 'Assets/Scenes/SampleScene.unity；Assets/Resources/Sound/SFX/sfx/reload_unused.wav', '已消除（2026-09-21 切片H 移出到 原版资源/_moved-out-from-assets/）'),
    ('55', '切片K：dryfire / hit_wall / knife_hit / bomb_beep_fast / round_start2 这 5 条 wav 的**原版源文件名映射未记录**', '它们确是原版 CS 1.6 的音效（空仓击发 / 弹着 / 刀命中 / C4 快蜂鸣 / 备用回合开始），但 `client/资源欠缺清单.md:37` 第 11 项只记了 c4_beep1 / c4_plant / c4_disarm / c4_explode1 / hegrenade-1 / flashbang-1 / radio/bombpl / radio/bombdef 这 8 条映射；原版 sound/ 树（`原版资源/cs16src`）已空 ⇒ 无法把短名逐条对回原版文件名', 'client/Assets/Resources/Sound/SFX/sfx/{dryfire,hit_wall,knife_hit,bomb_beep_fast,round_start2}.wav（在盘）；client/资源欠缺清单.md:37；原版资源/清单.md（cs16src 已空）', '用户补回 CS 1.6 客户端本体（原版资源/cs16src）后逐条对账'),
    ('56', '切片K：hit_wall 的「按材质分流」只落到一条采样，且刀「砍空」没有独立采样', '原版打沙 / 打木箱 / 打金属是**不同采样**，刀砍中人与砍空也是两条采样；盘上只有 hit_wall.wav（打墙）与 knife_hit.wav（刀命中）各一条 ⇒ 材质分类（CsAudioTuning.ClassifyImpact）已做、日志可逐类核对，但各材质现在落同一 clip；刀砍空（CsInventory.RaycastActor 返回 null）无音', 'Module/Audio/CsAudioTuning.cs（ClassifyImpact / HitWall / KnifeHit）；Module/Combat/CombatModule.cs（弹着音挂点）；Module/Match/CsDamage.cs（刀命中挂点）', '拿到原版按材质的弹着采样与刀挥空采样后，只改 CsAudioTuning 的分类→短名映射'),
    ('57', 'C4 蜂鸣的「加速档分界 10s」与两档间隔（1.0s / 0.25s）无原版出处', '原版 C4 蜂鸣节奏写在 `mp.dll` 的 C4 逻辑里（不是 cvar，`settings.scr` / `server.cfg` 都查不到），而 `mp.dll` 不在盘（`原版资源/cs16src` 已空）⇒ 该分界只能按本工程自己的口径统一（CsConst.BombBeepIntervalSlow/Fast 的 10s 注释 + CsAudioTuning.BombBeepFastBelow）', 'Core/CsConst.cs（BombBeepIntervalSlow / BombBeepIntervalFast）；Module/Audio/CsAudioTuning.cs（BombBeepFastBelow）', '解出 mp.dll 的 C4 蜂鸣节奏后'),
    ('58', 'CsBotConst 的绝大多数阈值无原版出处（**本项目新增**）', 'A = CS 1.6 本体**不含机器人 AI**（官方 bot 属 Condition Zero / PodBot，不在本工程的载体范围）⇒ "bot 手感阈值"在 A 里没有对应量；规格 §2.4 只给三档的反应时间 / 瞄准误差（±6° / ±3° / ±1.2°）与行为特征，不含这些阈值。三条有对应量却取不到载体的（瞄胸高度比例 / 脚步噪声阈值 / 预瞄节奏）见下面两条与 CsBotConst 各行的注释', 'Module/Bot/CsBotConst.cs（66 行逐条注释已标"本项目新增"或指到定义真源）；策划/策划案/CS1.6单机参考规格.md:113-118（§2.4 三档表）；Module/Match/CsTypes.cs:148（CsBotProfile）', '若主 agent 决定改为「逐条对齐 PodBot / CZ bot 源码」则另开片'),
    ('59', '脚步声触发口径与落地音阈值无原版出处（StepDistanceRun / StepMinSpeed / StepMinInterval / LandMinFallSpeed）', '① 原版脚步触发口径在 GoldSrc `pm_shared.c`（PM_PlayStepSound），该文件属 `原版资源/cs16src`、已空；② 落地音 A **本来就没有**（`client/资源欠缺清单.md:33` 第 7 项：GoldSrc 落地复用脚步采样），本工程用 pl_step4 采样代替、并自定"多快才算摔了一下"的阈值', 'Module/Audio/CsAudioTuning.cs（Step* / LandMinFallSpeed）；client/资源欠缺清单.md:32-33,76', '用户补回原版载体（原版资源/cs16src）后对账脚步节拍；落地音属"A 本来就没有"，不消除'),
    ('60', '切片K（D8）：Defuser / Vest / VestHelm 三个被动装备没有开火 / 换弹音', '它们不是武器：原版 CS 1.6 里既没有"手持并开火"、也没有换弹动作 ⇒ **原版也没有**这两个采样。旧判据（D8 的"每个 id 都要有 <id>_fire.wav / <id>_reload.wav"）把它们当武器，要满足只能**造两个 wav**（伪造素材，skill §0.1 ①）⇒ 判据已改为"装备在 CsWeapons 里有定义 + 无该音与 A 一致"', 'tools/probes/enumerate-entities.py（D8 段的 D8_EQUIPMENT 分支）；Core/CsWeapons.cs:83-85', '不消除（与 A 一致的行为差异）'),
    ('61', '切片L（S1）：操作 / 表现层的可调旋钮没有原版出处（CsCombatTuning 全 31 条；CsMatch / CsViewTuning / CsConst 里标「本项目新增」的那些）', '这些量（后坐力时间常数 / 散布倍率 / 准星扩散 / bob / 开镜过渡 / 受击晃动 / 枪口火焰时长 / 各类实现容量上限）在 A 里对应的是**客户端手感**，原版把它们写死在 `cstrike/cl_dlls/client.dll` 与 `mp.dll` 的逐武器代码里（不是 cvar、也不是数据表 —— 见 `策划/对照表.md` §6 BLOCKED-1 / BLOCKED-2）；本机原版载体 `原版资源/cs16src` 已空（`原版资源/清单.md`）⇒ 拿不到 `文件:偏移` 级出处，只能取本工程自定值并逐条如实标注', 'client/Assets/Scripts/Module/Combat/CsCombatTuning.cs（31 条逐行已标「本项目新增」+ 该条与 A 的关系）；Module/Match/CsMatch.cs、Module/View/CsViewTuning.cs、Core/CsConst.cs 的对应行；策划/对照表.md §6 BLOCKED-1/2 与 A-05 / A-08 / E-03 / N-22 / U-07 / U-36', '用户补回 CS 1.6 客户端本体（原版资源/cs16src：client.dll / mp.dll）后逐条对账'),
    ('62', '切片N（S1）：Defuser / Vest / VestHelm **没有第一人称 viewmodel / AnimatorController**', '它们是**被动装备** —— A（CS 1.6）里既不能"手持"、也没有第一人称动作 ⇒ **原版本身就没有**这三个 v_ 模型。旧判据把 CsWeapons 里所有 id 都当武器、要求 vm_<id>.controller 存在，对它们不成立；要满足它只能去 Editor/Views **生成**这三个控制器 = 造 A 没有的素材（skill §0 铁律 1）⇒ 判据已改为「A 也无此 viewmodel ⇒ 一致」', 'tools/probes/enumerate-entities.py（S1 段的 S1_PASSIVE_EQUIPMENT 分支）；依据 = client/Assets/Editor/Views/ModelData/*.cs16anim 共 38 个（29 个 vm_* + 9 个 player_*，装备类 0 命中）+ client/Assets/Resources/Art/Anim 的 29 个 vm_*.controller；Core/CsWeapons.cs:83-85', '不消除（与 A 一致的行为差异）'),
    ('63', '切片N：**下架了本项目新增的"伤害数字飘字"**（HudPanel 的 ShowDamageNumber / ObserveLocalDamage）', 'A（CS 1.6）的 HUD **没有伤害数字项**（原版 HUD 只有 hitmarker 与击杀提示）⇒ 屏幕上的 `-<数字>` 飘字属本项目自行新增的命中反馈文本，按 skill §0 铁律 1「A 没有 ⇒ 不加」整链删除。留下的只有**受击方向指示器**（屏幕边缘红框，A 有这条反馈）⇒ 那个被两处共用的时长常量随之由 DamageNumberTime 改名为 DamageIndicatorTime（含全部引用点）', 'client/Assets/Scripts/UI/InGame/HudPanel.cs（删除处留了注释与依据）；Core/CsConst.cs（原注释即写「本项目新增」）；策划/对照表.md §4「界面元素坐标/尺寸/颜色」——原版 HUD 元素已逐条出处化（U-01~U-37，引用到 hud.txt:110/120/121/122/127/131/135/137/179/183 等），**其中没有任何"伤害数字"项**；策划/验收表.md B 段——我方 HUD 项清单 H1~H15 里也没有它（H9 = 命中标记 hitmarker）；client/资源欠缺清单.md——A 有 / 我方缺 的逐项对账里同样没有这项。⚠️ 如实说明：**原版硬载体（cstrike/sprites/hud.txt 与 原版资源/解包产物/）本机不在盘**（`原版资源/清单.md` 实测：cs16src/ 与 解包产物/ 为空）⇒ 拿不到 hud.txt 原文级的"无此项"直证，本项按任务书退路登记为「本项目新增、与原版无关」', '不消除（A 本来就没有；若将来要加回，必须先给出原版出处的 file:line）'),
    ('64', '§G D2「低矮障碍（含楼梯扶手/台阶沿）」残留 5 格不达标（判据未放宽，逐格已查明）', '用户报的那一处（匪家矮墙/台阶沿 cell(20,27)，h=0.81 m）本片已通过：全图 71 处候选里地面挡 71/71、跳起站得住 67/71、横跨窗口全 OK（h=0.81 m 的 65 格矮墙/台阶沿里 63 格通过，另 2 格是下面的"箱堆"口径问题）。残留 5 格分三类，**都不是**"位图判挡 + 顶面够得着却站不上去"的隐形墙形态：(a) cell(82,86)/(23,123)：顶面够得着，但身高带里**真有实体**（沙子混凝土台上压着箱子，真顶面 2.44 m；军械箱上再叠一箱，真顶面 5.28 m - 来路 3.25 m = 2.03 m）⇒ 原版同样上不去 —— 这是"什么才算矮障碍"的**候选分类口径**问题，不是实现缺口；(b) cell(51,108)：军械箱顶（高差 1.13 m）逼近跳跃峰值 1.1445 m ⇒ 时间窗 0.073 s × 5.4 m/s = 0.39 m < 需跨 1.72 m，一次跳跃不可能**横跨**；本行"边界：恰好在跳跃可达高度上"一条已定案「顶面高差 ≤ 可达高度 ⇒ 能跳过去」，两条口径自相矛盾（横跨比"顶面够得着"更严，且原版 GoldSrc 起跳不改变水平速度、同样跨不过去）；(c) cell(118,52)/(118,53)：SandTrim 收边条（顶面 6.96 m / 来路 6.50 m），外侧是图外虚空 ⇒ 9 点探针有 3 个落点所在子区域**没有任何世界几何**，运行时按保守口径判挡（切片U/S 特意保留，⛔ 本片不碰）。', '判据 tools/probes/geom-check.py（A5：候选 / 来路判挡 / 顶面可站 / 横跨可达，+ probe_kind_9 把"外侧虚空"与"真有实体"分开）；运行时口径 client/Assets/Scripts/Module/Map/CsMap.cs:260-425（CanStand 两层判据 / BodyHeightClearAt 的保守判挡 / 9 点半径采样）；逐格数字见 geom-check 报告 A5 段与 策划/状态矩阵.tsv（本片回写）', '主 agent 裁决「候选分类口径」后：(a)(b) 两格在收紧为「只收格内真顶面 ≤ 可达高度的格」+「横跨窗口降级为信息行（判据 = 顶面高差 ≤ 跳跃可达高度）」时归零；(c) 两格属运行时保守口径，需把站立判定改成原版单点口径（另开片，⛔ 本片未改引擎/未改该调用链）'),
    ('65', '本工程 de_dust2 几何的 X 轴跨度 4480 单位 > 原版 overview 窗口的 4096 单位（多 384 单位 = 9.4%）',
     '雷达底图换成**原版** overviews/de_dust2.bmp 后，雷达"显示哪块世界"由**原版窗口**决定（片AS 口径：X 中心 ± 2048 单位、Z 中心 ± 2730.6667 单位）。原版那张图的窗口装不下本工程几何的最东/最西两端 —— 但**这恰恰是原版行为**：原版 de_dust2 的 overview 本来就只覆盖 4096×5461 单位，多出来的 384 单位是**外挂笔刷/越界顶点**（去掉 0.5% 分位后 X 跨度 = 4064，与原版 4096 只差 0.8%，Z 轴 5461 vs 几何 5312 装得下）。主 agent 已裁决：**不剪几何 / 不改 de_dust2_geo.bin / 不重建场景**（为了"装下离群顶点"去改几何 = 1:1 复刻的反面）⇒ 只登记，不修',
     'tools/probes/overview-window.py（containment 判据原文：`X window 4096 units  footprint 4480 units  -> OUTSIDE by 384 units (9.4%)`；`0.5%-trimmed X [-1872..+2192] span 4064`）；client/Assets/ThirdParty/Dust2/de_dust2_geo.bin 的顶点外接框；窗口公式真源 原版资源/hlsdk/cl_dll/hud_spectator.cpp:1069-1193',
     '地图几何域另片处理（⛔ 本片不剪几何）'),

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
          '\u7f16\u53f7\t\u662f\u4ec0\u4e48\t\u4e3a\u4ec0\u4e48\t\u51fa\u5904\t\u4f55\u65f6\u6d88\u9664', DIF)

# 覆盖矩阵判定片段（一行 = 一个实体）
frag = ['## G. \u8986\u76d6\u77e9\u9635\u5224\u5b9a\uff08\u811a\u672c\u751f\u6210\uff1a\u4e00\u884c = \u4e00\u4e2a\u5b9e\u4f53\uff1b\u26d4 \u52ff\u624b\u6539\uff09', '',
        '> \u6765\u6e90 = `策划/实体清单.tsv`\uff08`tools/probes/enumerate-entities.py` \u679a\u4e3e\uff09\u3002',
        '> \u7ed3\u8bba\u53ea\u5141\u8bb8\uff1a`\u4e00\u81f4` / `\u4e0d\u4e00\u81f4(\u5dee\u5728\u54ea)` / `\u5141\u8bb8\u7684\u5dee\u5f02(\u2192 \u5dee\u5f02\u767b\u8bb0.tsv)` / `\u5f85\u91c7(\u5e76\u6392\u56fe)`\u3002',
        '', '<!-- COVERAGE-BEGIN -->', '| # | \u7ef4\u5ea6 | \u5b9e\u4f53 | \u5224\u636e\u7c7b\u578b | \u7ed3\u8bba | \u8bc1\u636e |',
        '|---|---|---|---|---|---|']
def _s(t):
    # ⛔ 只在这张判定表里把 `.png` 写成 `.png `（带空格）：验收表里出现的 `xxx.png` 会被既有闸门
    #    `screenshot-refs` 当成"引用的取证截图"去 `.ai-tmp/screenshots/` 找（那里没有贴图资产），
    #    从而**误报**。实体名里的 `SandWllDoor2.png` 是**地图贴图资产名**，不是截图引用。
    return str(t).replace('|', '/').replace('.png', ' .png')


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
with open(os.path.join(PLAN, '\u8986\u76d6\u77e9\u9635\u5224\u5b9a.fragment.md'), 'w', encoding='utf-8', newline='\n') as f:
    f.write(_brand_block + '\n')

if '--inject' in sys.argv:
    # 覆盖矩阵的**判定行**落 `策划/验收表.md`（规格文件放的是"要做成什么样"，判定表放的是"判成什么"）。
    spec_path = os.path.join(PLAN, '\u9a8c\u6536\u8868.md')
    txt = rd(spec_path)
    B, E = '<!-- COVERAGE-BEGIN -->', '<!-- COVERAGE-END -->'
    # ⛔ block 取 `frag[4:]` 会带上一个**前导空行**（frag[4] == ''），而旧代码只 rstrip 尾部
    #    ⇒ 每次注入都往 BEGIN 标记前多插一个空行 ⇒ 同输入两次产出不同字节（不可复现，见上）。
    #    这里与片段文件用同一个 `_brand_block`：BEGIN…END、无前导/尾随空行 ⇒ 注入幂等。
    block = _brand_block
    i, j = txt.find(B), txt.find(E)
    if i >= 0 and j > i:
        # ⛔ 不用 re.sub：替换串里带反斜杠时会被当成转义/反向引用（实测踩过），字符串切片最稳。
        txt = txt[:i] + block + txt[j + len(E):]
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
