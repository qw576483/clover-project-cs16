# agent-23：修非二次幂贴图被拉伸（并顺带补齐 3 处证据缺口）

> 项目根：`clover-project-cs16`
> 承接 agent-22 的未决 4（它发现但**没动**这个缺陷，因为不在它那片的六项里）。

## 0. 现象与影响（agent-22 实测）

`client/Assets/ThirdParty/Dust2/Textures/*.png` 的导入设置全是 **`nPOTScale: 1`（ToNearest）** ⇒
**非二次幂的原版 miptex 被 Unity 拉伸到最近的 2 的幂**再上传，而 UV 仍按原 `w×h` 计算
⇒ **所有非 POT 贴图比例失真**。实测例子：`SandCCrete.png` 原版 **256×192** → 导入成 **256×256**。

**为什么必须修**：agent-22 刚从 `cs_dust.wad` 导出的 21 张原版 miptex 里，多数不是 POT
（`256×192` / `96×32` / `192×160` …）—— 贴图像素换对了、**但比例被拉**，等于 A 点地面材质的修复被抵消一半。

原版依据：GoldSrc 的 miptex 用**原始尺寸**直接贴（没有 POT 要求），所以正确的目标 = **导入后尺寸 == 原图尺寸**。

## 1. 只做这两件

### 1.1 让非 POT 贴图**不缩放**（幂等、由生成器保证）

- 目标：`TextureImporter.npotScale = TextureImporterNPOTScale.None`
  （对 Standalone 目标平台合法；⛔ 不许改成 `ToLarger`/`ToSmaller` 这类"换个尺寸拉"的做法）；
- **必须由生成器统一设置**（在导出/生成链路里，例如 `原版资源/解包产物/cs16_asset_extract.py` 或
  `client/Assets/Editor/MapGen/Dust2Builder.cs` 的资源整理段），⛔ **不许手点 Inspector**、
  ⛔ 不许只改现有 `.meta` 而不改生成器（重跑一次就回退）；
- **幂等**：重跑生成器不炸、结果一致；
- ⛔ **不许改贴图像素本身**（`png` 内容一个字节都不许动）；
- **同时检查 UV**：确认材质/网格的 UV 计算用的是"原图 w×h"而不是"导入后尺寸"
  （若 UV 依赖导入尺寸，改 npotScale 后要一并修正，并把这一步写进生成器）。

### 1.2 顺带补齐 3 处证据缺口（**同一条链、同一次 Play**，不许为此各进一次）

> 依据 skill §2：**N 个验收行 ≠ N 次 Play** —— 这三项都挂在"进游戏内那一条链"上，一次跑完。

1. **G4 的 1 / 4 / 5 号槽位图**（agent-22 没采到）：原因是回合起始 `$800` 买不起主武器、买枪窗口只有 15 s。
   正解 = 在新一局里用**足够的起始金钱**（`NewGame` 面板可设 `StartMoney`，或进图后用既有驱动给本地玩家加钱）
   → 买一把主武器 → 按 `4`（手雷）/ `5`（C4，仅 T）各截一帧，文件名沿用 `22_slot*` 风格（如 `23_slot1_primary.png`）；
2. **D1 的第三人称构图**：agent-22 那次帧被记分板盖住（`hold-tab` 驱动每 0.01 s 重灌 Tab）。
   **先修驱动**（Tab 只按一次或按需释放），再采一帧**不含记分板**的第三人称图；
3. **F1② 的上屏色**（若可解）：agent-22 已证明**代码色是对的**（运行时 `color=#FFB000`），
   只是编辑器 Game 视图是 1096×500、16 px 字按 ~8 设备像素渲染 ⇒ 采样不到满覆盖纯色。
   ⇒ **试着把 Game 视图设成 1920×1080**（Editor API，例如 `GameViewSizes` + `GameView` 反射设置；
   查 `reference/pipeline-and-unity-cli.md` 有无现成命令）。设不了 ⇒ 如实 BLOCKED 并写清"需要用户把 Game 视图设成 1920×1080"。

## 2. 取证纪律（新 skill §2）

- **进 Play 记账**：先在 `<项目根>/.ai-tmp/test/play-log.tsv` 追加一行（`<ISO>\tagent-23\t非POT贴图修复\t一条链：…`）；
  **目标 1 次**（若必须第二次，写清原因；≥3 次 ⇒ 停下回报）。
- **驱动先复用**：优先用 `.ai-tmp/drivers/` 与 `.ai-tmp/test/` 里已有的；本轮要复用的放 `.ai-tmp/drivers/`。
- **能离线判的不许进 Play**：贴图导入尺寸、UV 口径必须在离线断言里过（见 §3），Play 只采表现类。
- **采集即冻结**：采完不许再改代码。

## 3. 判据（自己跑，原始输出贴进回报）

1. **离线断言**：遍历 `Assets/ThirdParty/Dust2/Textures/*.png`，逐张报 `原图 w×h` 与
   `AssetImporter`（或导入后 `Texture2D`）的 `width×height` ⇒ **全部相等**；非 POT 的那几张单独列出（改前 vs 改后）；
2. **生成器幂等**：重跑一次生成器，再跑第 1 条断言 ⇒ 结果不变；
3. **一次编译**：`compile-check.ps1` ⇒ `csc exit=0`；编辑器 `recompile_status` 无错；
4. **一次 Play + 一张联络图**：把"地面贴图比例（A 点）+ 三个槽位 + 第三人称构图"压进**一张**联络图，
   放 `client/Assets/Screenshots/`；**只读这张汇总图**（读图前自检通道，读不到 ⇒ BLOCKED）；
5. **更新** `策划/验收表.md` 里**受影响的那些行**（P1 / G4 / D1 等）的证据与状态（⛔ 不许改判据/类别/行）；
6. `tools/verify.ps1` ⇒ 除 `HUMAN-ONLY` 外无 FAIL（若 `evidence-freshness` 因你改了 `Dust2Builder.cs` 而报出**某几行**过期 ⇒ 按 §1.2 的同一次链把它们一并重采，**⛔ 不许全量重采**）。

## 4. 不许

- ⛔ 不许改 `tools/ai-skill/**`、任何 skill、`.ai-tmp` 历史文件、别的 `clover-project-*` 工程；
- ⛔ 不许改贴图像素；⛔ 不许手点 Inspector 代替生成器；
- ⛔ 不许编造"已修好"（读不到图/断言不过 ⇒ BLOCKED）；⛔ 不许开子 agent；
- ⛔ 不许动与本片无关的功能（范围只许收不许放）；⛔ 不许产出交接/进度类 md。

## 5. 回报格式

```
产出物：<文件绝对路径清单>
自检：<离线断言原文（逐图 w×h）+ 幂等复跑 + 编译 + 联络图逐格 + 验收表受影响行 + 闸门逐行>
未决：无 / <具体条目>
```
