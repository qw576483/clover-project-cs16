# agent-11：数值与 HUD 对齐（1:1）（clover-project-cs16）

> 项目根：`c:\Work\Server\full-dev\clover-project-cs16`
> **本片改 `client/**` 业务代码**（这是你的授权范围）。原版值出处已全部由前几棒解析落盘，**你只搬，不发明**。

## 0. 输入（先读，出处都在里面）

| 文件 | 用途 |
|---|---|
| `原版资源/解包产物/原版数值表.md` | §1 cvar 默认值（含 `mp.dll:偏移`）· §1.1 两套口径 · §2 引擎 cvar · §3 武器表 25 条（含偏移）· §4 买枪期 · §5 差值表 · §6 BLOCKED |
| `原版资源/解包产物/原版HUD布局.md` | HUD 侧 cvar 默认值（含 `client.dll:偏移`）· 原版截图量化的像素坐标 · BLOCKED-D1~D6 |
| `策划/对照表.md` | §5.2 两套口径总表 · §5.3 武器弹匣/备弹（W-01~W-27）· U 系列（HUD）· G/M/N/P/T/W 各块 |
| `.ai-tmp/test/compile-check.ps1` | **离线编译检查入口**（Unity 自带 Roslyn + `Cs16.csproj` 引用清单）—— 本片的"编译通过"判据 |

## 1. 数值口径（主 agent 已定，照做）

**口径 = 随包 `server.cfg`（玩家实际生效值）**；`mp.dll` 出厂默认作为第二口径**同时登记在对照表**，不混用。

## 2. 只做这五件

### 2.1 `client/Assets/Scripts/Core/CsConst.cs` 数值

| 常量 | 现在 | 改成 | 出处（写进注释） |
|---|---|---|---|
| `FreezeTime` | 6 | **4** | 随包 `server.cfg:51`（出厂默认 6 = `mp.dll:0x11b9c0`，登记在对照表） |
| `RoundTime` | 115 | **105** | `server.cfg:37` `mp_roundtime 1.75`（=105 s） |
| `BombTimer` | 40 | **35** | `server.cfg:43` `mp_c4timer 35` |
| `BuyTime` | （无） | **新增 15f** | `server.cfg:42` `mp_buytime 0.25`（=15 s）；出厂默认 90 s = `mp.dll:0x11b9a8` |
| `Gravity` | 20 | **20.32** | `sv_gravity 800` units/s²（`hw.dll.orig:0x189e90`）× 0.0254 m/unit |
| `JumpSpeed` | 5.2 | **6.82** | `sqrt(2*800*45)` = 268.328 u/s（`pm_shared.c:2596`）× 0.0254 |

> 单位换算常数 0.0254（1 unit = 1 inch）**必须写进注释并指到 `pm_shared.c`**（该文件里有 unit 换算的原始定义；若你找不到确切行，就在注释里只写"0.0254 m/unit（1 unit = 1 inch）"，⛔ 不许编行号）。

### 2.2 `CsWeapons.cs`

1. **价格**：Elite 1000 → **800**（`mp.dll:0x10f7e8` 的 `iCost`；`buy*.res` 同值）；
2. **逐把核对弹匣/备弹**：按 `对照表.md` §5.3 的 **W-01~W-27**（出处 = `mp.dll` `WeaponInfo[]` 的 `iMaxClip` / `iMaxAmmo`），**我方不一致的全部改成原版值**，并在每行注释里写上 `mp.dll:偏移`；
3. 若发现 §5.3 里我方"没有对应武器/槽位不符"的 ⇒ 回报，不许自行加武器。

### 2.3 买枪期（原版有、我方缺）

原版规则（`原版数值表.md` §4）：**买枪期与冻结期是两个独立计时器** —— 回合开始后 `mp_buytime`=15 s 内、且**身处 `func_buyzone`**（T/CT 各一块）、且**活着**，才能买；15 s 用尽即不能买。

- 现在我方只有"冻结期内可买"⇒ 把它改成**独立的 15 s 买枪窗口**（与冻结期解耦：冻结结束后只要还在 15 s 内且没出买枪区，仍可买）；
- 需要改的地方按现状自己找（`CsMatch` / `MatchModule` / `UI/InGame/BuyMenuPanel.cs` / `HudPanel` 的买枪区提示）；**先把现状读清楚再动**；
- 买枪区判定已存在（`func_buyzone` 已进地图标记表）—— 复用，不要另建；
- 边界与失败分支**必须打日志**（`Game.Logger.Info/Warn`，tag 归原模块）。

### 2.4 HUD（**色/时长/布局**，全部有原版出处）

| 项 | 原版值（出处） | 我方现在 | 要求 |
|---|---|---|---|
| HUD 文字色 | `#FFB000`（`clientscheme.res:24` `BaseText`，并由原版截图掩膜 `(255,176,0)` 逐张验证） | `#ECECE0`（`CsHudTheme.cs:34`） | 改成原版色 |
| 准星色 | `50 250 50`（`client.dll:0x0e6e3c` `cl_crosshair_color`） | G=255（`CsHudTheme.cs:64`） | 改成 250 |
| 击杀条停留时长 | 6 s（`client.dll:0x0e77f8` `hud_deathnotice_time`） | 4 s（`CsHudTheme.cs:84`） | 改成 6 s |
| 比分 + 计时布局 | **右上角**：两行（y[38..49] / y[65..76]）、x[1543..1750] 与 x[1626..1750]、秒表图标 x[1796..1816] y[58..82]（21×25）、竖分隔线 x[1776..1777] y[27..93]（2×67）—— 均取自原版 1920×1080 截图（`screenshots_to_conv/*.bmp`，31/31 张一致） | 顶部中央三件套 | 按原版坐标搬到**右上角**（直接按 1920×1080 基准写坐标；我方 UI 是 1080p 基准，与截图同基准 ⇒ **不换算**） |
| 雷达 | `radar 640 radar640 0 0 128 128`（`hud.txt:183`，@640 基准） | `RadarSize = 200f`（`CsHudTheme.cs:87`） | 按同一基准换算后对齐（640 基准 → 1080p 的换比率自己从原版其它元素反推并写明；**写不出就回报，别硬凑**） |

> ⚠️ 布局改动会动 `HudPanel.cs` 里比分/计时那几件的锚点与坐标 —— 改完**必须**在回报里给出"原版坐标 → 我方坐标"逐项对照，方便下一片实机核图。
> ⛔ 截图里没有血量/护甲/金钱/弹药那排的坐标（原版截图是旁观机位）⇒ **那几件的绝对坐标不许自己定**，保持现状并回报（它们已在 `对照表.md` BLOCKED-D1 登记）。

### 2.5 出处注释（每条改动都要）

每个被改的常量/坐标，**在代码注释里写清出处**（`文件:偏移` 或 `server.cfg:行`）。这是 §0.5「写不出出处不许进工程」的唯一落地形式。

## 3. 判据（做完自己跑，原始输出贴进回报）

1. **离线编译通过**：`powershell -NoProfile -ExecutionPolicy Bypass -File .ai-tmp\test\compile-check.ps1` ⇒ `csc exit=0`（把原始输出贴出来）；
2. **逐条对齐表**：`元素 | 原版值(出处) | 改前 | 改后`，覆盖 §2.1~§2.4 每一行；
3. 改动文件清单（`文件:行`）—— 只许落在 `client/Assets/Scripts/**`（若被迫动 `Assets/Editor/**`，在回报里说明原因）。

## 4. 不许

- ⛔ 不许改 `策划/**`、`docs/**`、`tools/**`、任何 skill、`.ai-tmp/test` 里的历史文件；
- ⛔ 不许碰动画/模型/音效（下一片）；
- ⛔ 不许改契约（`ICsMatch` / `CsTypes` / `Events` 的**成员签名**）—— 确实需要新增成员 ⇒ 回报，由主 agent 定；
- ⛔ 不许读别的 `clover-project-*` 工程；⛔ 不许开子 agent；
- ⛔ 不许 `Resources.Load` / `PlayerPrefs` / 裸 `Debug.Log` / 直连 `UnityEngine.Input`（引擎有就用的老规矩，见 `docs/步骤文档.md` §3.1）；
- 一次性脚本只放 `.ai-tmp/test/`，用完删；不许产出交接/进度类 md。

## 5. 回报格式

```
产出物：<文件绝对路径清单>
自检：<compile-check 原始输出 + 逐条对齐表 + 改动行号>
未决：无 / <具体条目>
```
