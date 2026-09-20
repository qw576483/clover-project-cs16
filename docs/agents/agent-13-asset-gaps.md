# agent-13：素材缺口补齐（天空盒 / 秒表图标 / 来源勘误 / 雷达比率）（clover-project-cs16）

> 项目根：`c:\Work\Server\full-dev\clover-project-cs16`
> 本片补的都是**原版 A 本体内就有、我方却用了次一级来源或没做**的东西（§4 闸门 3 / §0.5）。

## 0. 输入

- `策划/对照表.md`：T-05 / T-06（贴图与天空盒）、U-06（雷达尺寸）、U-34（秒表图标）、BLOCKED-3
- `原版资源/解包产物/原版HUD布局.md`：HUD 侧实测（含 `hud.txt` 行号、截图量化坐标）
- `client/资源欠缺清单.md`：第 19 项（地图贴图回落）、第 17 项（字体）
- 离线编译入口：`.ai-tmp/test/compile-check.ps1`

## 1. 只做这四件

### 1.1 天空盒改用**原版本体**的 des 六面

现状（`对照表.md` T-06）：我方 `CL/ThirdParty/Dust2/Skybox/sky_*.png` 来自**社区镜像**的同名 des 天空；
**原版本体里就有**：`原版资源/cs16src/cs16game/app/cstrike/gfx/env/des{ft,bk,lf,rt,up,dn}.tga`（256×256×32bpp）。

要求：
- 从**本体**这 6 个 TGA 转出 PNG（自己写转换，或复用 `原版资源/cs16src/cs16_build.py` 里的 TGA/图像工具；⛔ 别引入新依赖除非必要）；
- 按 §1.9 第 2 条**复制**到工程（`client/Assets/ThirdParty/Dust2/Skybox/`），保持现有文件名与引用路径不变；
- 更新 `client/Assets/ThirdParty/Dust2/SOURCES.txt` 里天空盒那几行的来源（改成"原版本体 gfx/env/des*.tga + 出处路径"）；
- 若本体 TGA 与社区镜像的像素**一致** ⇒ 也照做（口径统一到本体），并在回报里说一句"两者是否一致"。

### 1.2 秒表图标（原版 HUD 精灵）

现状（`对照表.md` U-34，`agent-11` 未决 4）：比分右上角那块原版有**秒表图标**（截图 bbox `x[1795..1817] y[58..82]`，≈23×25），我方没有。

要求：
- 从原版精灵文件解出（`hud.txt:127`：`stopwatch 640 640hud7 144 72 24 24` ⇒ 在 `cstrike/sprites/640hud7.spr` 的源矩形 `144,72,24,24`）；
- 产出 PNG 并按 §1.9 复制进工程（放哪、怎么被 UI 引用，按现有 UI 资源约定来 —— **先读** `client/Assets/Scripts/UI/InGame/HudPanel.cs` / `CsHudTheme.cs` / `Assets/Editor/UiGenInGame/UiBuilder.cs` 看现有面板怎么生成图标与 sprite，再决定）；
- 把图标接到比分那一块的布局里（坐标按 `原版HUD布局.md` 的截图量化值：`x[1795..1817] y[58..82]`，1920×1080 基准）；
- ⛔ 不许用 emoji / 自画图形 / 通用图标包顶替（§2 素材硬标准）。

### 1.3 来源勘误（三处，全部有实测依据）

1. `client/Assets/ThirdParty/Dust2/SOURCES.txt:5` 的字节数（写 `10,086,336`，实测 **1,086,336**）；
2. 同文件 `:24` 的转换脚本路径已过期且**越界到工程外**（写 `c:/Work/Server/full-dev/_assets_tmp/dust2_build.py`）—— 实际在项目内 `原版资源/解包产物/dust2_build.py`，改成项目内相对路径；
3. 同文件 `:13-18` 关于 WAD 不可得的说明与实际一致（**不用改**），但若你顺手发现别的过期引用，一并改成项目内真实路径（逐条 `Test-Path` 验证）。

### 1.4 雷达尺寸（把"反推不出"再试一次）

现状（`对照表.md` U-06；`agent-11` 未决 3）：原版 `hud.txt:183` `radar 640 radar640 0 0 128 128` @640 基准；我方 `CsHudTheme.RadarSize = 200f`（1080p），换比率反推不出来（两个 @640 元素的高度比互相冲突）。

要求：
- 用 `原版资源/cs16-maps/screenshots_to_conv/*.bmp`（31 张 1920×1080，**已知含 HUD**）**实测**雷达的实际像素尺寸（雷达是圆形/方形底色，找它最容易的判据：与周围底色不同的那块区域边界；`原版HUD布局.md` §0.5 的量化流程可直接复用）；
- 得到像素值后，**换成 1080p 基准写进代码**（并注明出处 = 哪张图 + 实测 bbox）；
- 若截图里雷达被 HUD 关闭/被观战机位遮住 ⇒ 如实回报 `BLOCKED`，**不许硬凑一个数字**。

## 2. 判据（自己跑，原始输出贴进回报）

1. 天空盒：6 个新 PNG 的**尺寸与来源哈希**（本体 TGA 的 SHA256 前 16 位 + PNG 哈希），并与旧 PNG 做一次"是否像素相同"的结论；
2. 秒表：新 PNG 的尺寸 + 在工程内的引用路径（`文件:行`），以及"UI 生成器能产出它"的证据（编译通过 + 生成器代码路径）；
3. `SOURCES.txt` 里每条路径 `Test-Path` 结果；
4. 雷达：实测 bbox + 换算依据（或 BLOCKED）；
5. 编译：`powershell -NoProfile -ExecutionPolicy Bypass -File .ai-tmp\test\compile-check.ps1` ⇒ `csc exit=0`（若本片动了 `.cs`）；
6. 若改了 `SOURCES.txt`/生成了新素材：**旧素材不要删**（§1.9：换素材 = 改源再复制一遍），在回报里说明新旧关系。

## 3. 不许

- ⛔ 不许改 `策划/**`、`docs/**`、`tools/**`、任何 skill、`.ai-tmp/test` 历史文件；
- ⛔ 不许碰动画/数值/契约（`agent-11`/`agent-12` 的地盘）；需要改 `IC*` / `CsTypes` / `Events` 成员 ⇒ 回报；
- ⛔ 不许用通用兜底素材；工程内引用必须**从 `原版资源/**` 复制**（⛔ 不许让 Unity 直接引用 `原版资源/`）；
- ⛔ 不许读别的 `clover-project-*` 工程；⛔ 不许开子 agent；
- 一次性脚本只放 `.ai-tmp/test/`，用完删；不许产出交接/进度类 md。

## 4. 回报格式

```
产出物：<文件绝对路径清单>
自检：<实测命令 + 原始输出 + 哈希/尺寸/bbox>
未决：无 / <具体条目>
```
