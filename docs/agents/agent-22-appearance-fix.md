# agent-22：外观批量修复（②③④ 拍：一次改完 → 一次编译 → 一条链 + 一次联络图）

> 项目根：`clover-project-cs16`
> 输入 = `策划/外观差异清单.md`（逐项含出处与目标值）+ `策划/对照表.md` §9（32 行）。
> 按新 skill §2/§3：**一次批量改完 → 一次编译 → 一条驱动链跑完 → 采一次联络图**。

## 0. 开工必做

1. `use_skill("clover-engine")`（新版：§2 成本闸门 / §3 四拍 / §4 证据契约）。
2. 读 `策划/外观差异清单.md` 与 `策划/对照表.md` §9 —— **那是本片的改动清单与判据**。
3. **⛔ 别信"状态机没生效"那条**（差异清单 E1 的措辞）：主 agent 已用脚本核实 ——
   `client/Assets/Resources/Art/Anim/player_CT_gsg9.controller` 的 state 名**已经是无前缀**的
   （`ref_shoot_knife` / `death2` / `crouch_reload_shotgun` …），agent-17 的修复**有效**。
   （E1 真正剩下的只有两条"写不出出处的量"：`CsViewTuning.PositionSmoothTau = 0f`、`AnimMoveSpeedEpsilon = 0.15f`。）

## 1. 只做这些（六项 + 两条常量）

| # | 改什么 | 目标值 / 出处 |
|---|---|---|
| A1 | `Core/CsConst.cs` 的 `DefaultFov`（现 90f）与 `ZoomFov` | 原版 `default_fov=90` 是**水平**（`HLSDK/cl_dll/hud.cpp:332`）；垂直按 `CalcFov`（`HLSDK/cl_dll/view.cpp:1737-1747`）= `2·atan(tan(fov_x/2)/aspect)` ⇒ 16:9 = **58.72°**。**实现成"按当前宽高比逐帧算"，⛔ 不许写死 58.72**（换分辨率就错）。判据：反算等效水平 = 90.0°±0.05° |
| A2 | `Module/View/CsViewTuning.cs` 的 `ViewModelLocalPosition` | 原版视图原点 = 眼位**下移 1 unit**（`view.cpp:665`）⇒ `y = −0.0254 m`（1 unit × 0.0254）。⛔ 其余分量不许自己编；`viewsize` 补偿（`:667-684`）若无法确定默认值 ⇒ 登记 BLOCKED，别瞎填 |
| B1 | `原版资源/解包产物/dust2_build.py` 的 miptex→PNG 映射表（`:45/:46/:52/:55` 等） | 原版 A 点最主要的几组是 `SandRoad` / `-0Sand` / **`SandCCrete`**（不是木箱）；**`cs_dust.wad`（项目内 `原版资源/**`，1,055,884 B / WAD3 / 28 lumps）里有原版像素** ⇒ 导出 `SandCCrete` / `SandRoad` / `SandTrim` / `_0Sand` 等缺失贴图，按 §1.9 复制进 `Assets/ThirdParty/Dust2/Textures/`，并把映射表改对。判据：`SandCCrete.png` 存在且尺寸 = 原版 miptex w×h；`FALLBACK` 表里不再出现 `'sandccrete'` |
| C1 | `Editor/MapGen/Dust2Builder.cs` 的天空盒 6 面绑定（`:343-344` 附近） | 原版像素环序实测 = `ft→lf→bk→rt`，我方得到 `ft→rt→bk→lf` ⇒ **互为镜像**；把 `_LeftTex / _RightTex`（或等价的 `_Front/_Back`）交换。判据：**离线复算**环代价从 152.60 落到 ≈42.77；实机 4 条竖边无硬边（`up` 面朝向区分度不足 ⇒ **标 BLOCKED，别编**） |
| D1 | 实现真正的第三人称（观战 / 跟随） | 原版：`cl_chasedist 112`（`HLSDK/cl_dll/view.cpp:1724`）⇒ 112 × 0.0254 = **2.8448 m**；`CAM_MIN_DIST 30.0`（`HLSDK/cl_dll/in_camera.cpp:31`）⇒ **0.762 m**；机位 = `vieworg += -ofs[2]*camForward`（`view.cpp:617-635`）、roll 归零、`cam_idealdist 64`/`cam_idealyaw 90`/`cam_snapto 0`（`in_camera.cpp:492-497`）。**要求**：机位在目标后方 2.8448 m、射线**收缩防穿墙**（打墙就把相机拉近，最小 0.762 m）、位置平滑，⛔ 不许凭空定"手感参数" |
| F1 | UI 三项 | ① 补 `Map: <地图名>` 文本（原版 dust2 图右上角实测有）；② 右上角上屏色：实测主色 `#AFAF32`/`#B77D0C`，与 `#FFB000` 不符 —— **先查是"配色常量错"还是"抗锯齿/压缩导致采样偏差"**，能定案则改成 `#FFB000`，定不了 ⇒ 登记 BLOCKED（需 1920×1080 重采定案）；③ `♥`/盾/`+` 字形 → 原版位图图标（若能从 `cstrike/sprites/` 解出对应 HUD 精灵就换，解不出 ⇒ BLOCKED） |
| E1 | `CsViewTuning.PositionSmoothTau = 0f`、`AnimMoveSpeedEpsilon = 0.15f` | 这两条**写不出出处** ⇒ 要么在原版载体里找到出处并写进注释，要么在 `策划/验收表.md`「允许的差异」登记一行（含"为什么 / 出处 / 何时消除"）。⛔ 不许留着裸数字不解释 |

## 2. 取证纪律（新 skill §2 成本闸门，**逐条照做**）

- **进 Play 记账**：本片**只许进 1 次**（改完 + 编译通过后）。进之前在
  `<项目根>/.ai-tmp/test/play-log.tsv` 追加一行：`<ISO 时间>\tagent-22\t外观批量修复\t唯一一次：跑一条链采联络图`。
- **驱动先复用再新建**：优先复用 `.ai-tmp/drivers/` 与 `.ai-tmp/test/` 里已有的进图/截图脚本；
  本轮要复用的放 `.ai-tmp/drivers/`（交付前统一清），真一次性的放 `.ai-tmp/test/` 用完删。
- **采集即冻结**：采完联络图后**不许再改代码**；若必须再改 ⇒ 只重采受影响的行，并在回报里写明"第几次重采"。
- **能离线判的不许进 Play**：A1 的 FOV 换算、B1 的 miptex 尺寸、C1 的环代价、D1 的机位公式
  **都要在离线先算/先断言**（`.ai-tmp/hosts/*check` 或直接脚本），Play 只用来采"表现类"。
- **原版图仍缺**（原版客户端跑不起来）：需要原版图定案的三项（viewmodel 精确占比、天空盒 `up` 面朝向、UI 上屏色）
  ⇒ 如实标 `BLOCKED：需一张原版图`，⛔ 不许编。

## 3. 判据（自己跑，原始输出贴进回报）

1. **一次编译**：`powershell -NoProfile -ExecutionPolicy Bypass -File .ai-tmp\test\compile-check.ps1` ⇒ `csc exit=0`；
   编辑器侧 `recompile` + `recompile_status` 无错（编译失败 ⇒ 中止一切验证）；
2. **离线预演**（各一条断言，给原文）：A1 换算反算回 90.0°±0.05°；B1 新贴图尺寸=原版 miptex；C1 环代价 ≈42.77；
   D1 机位距离=2.8448 m 且射线命中墙时收缩到 ≥0.762 m；
3. **一次 Play + 一次联络图**：把 A1/A2/B1/C1/D1/F1 的证据点压进**一张**联络图（每格烧「格号+状态+关键数值」），
   放 `client/Assets/Screenshots/`；**你只读这张汇总图**（读图前先自检通道，读不到 ⇒ BLOCKED，⛔ 不许编）；
4. **更新** `策划/验收表.md` 里**受影响的那些行**的证据与状态（⛔ 不许改判据/类别/行；⛔ 不许全量重采）；
5. `powershell -NoProfile -ExecutionPolicy Bypass -File tools\verify.ps1` ⇒ 除 `HUMAN-ONLY` 外无 FAIL
   （既有那条 `P1` 过期行**归主 agent**，你别动）。

## 4. 不许

- ⛔ 不许改 `tools/ai-skill/**`、任何 skill、`.ai-tmp` 里的历史文件、别的 `clover-project-*` 工程；
- ⛔ 不许编造原版值 / 画面结论 / 出处；拿不到 ⇒ BLOCKED 并写清"试过什么"；
- ⛔ 不许改与本片六项无关的功能（**范围只许收不许放**）；⛔ 不许开子 agent；
- ⛔ 不许产出交接/进度类 md。

## 5. 回报格式

```
产出物：<文件绝对路径清单>
自检：<编译 + 六项离线断言原文 + 联络图与逐格结论 + 验收表受影响行 + 闸门逐行>
未决：无 / <具体条目>
```
