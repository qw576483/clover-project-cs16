# agent-47 · 选兵种（classmenu）界面按原版载体重建（差异 r71 后半）

## 一句话
用户报的「**选人界面**」—— 买枪界面那半已由前序片做完（`BuyMenuPanel.cs` 已按原版 `.res` 重建，**别重做**），
本片做**另一半：选兵种界面**，落点是 `client/Assets/Scripts/UI/Flow/TeamSelectPanel.cs`（14 KB，现在还是自建布局）。

## 载体（**本轮刚取回，在盘**）
- `原版资源/cs16src/cstrike/cstrike__resource__UI__Classmenu_CT.res`（4,063 B）
- `原版资源/cs16src/cstrike/cstrike__resource__UI__Classmenu_TER.res`（4,087 B）
  （真实仓库路径是 `cstrike/resource/UI/Classmenu_CT.res`，大小写与清单不同，已按真实路径落盘；两份都是 KeyValues，含 `ClassMenu` 键）
- 参考已完成的同类做法：`client/Assets/Scripts/UI/InGame/BuyMenuPanel.cs`（它按 `weapon_*.txt` + `640hud*.spr` 重建了买枪界面 ⇒ **照它的口径做选兵种**）
- 精灵图集：`原版资源/cs16src/cstrike/cstrike__sprites__640hud1..20.spr`（20 张 256×256）、`cstrike__sprites__hud.txt`
- 差异台账：`策划/差异登记.tsv` **r71**（第三列「何时消除」逐条列了这项要什么）

## 目标与判据
1. **先查证**（⛔ 不许跳）：选兵种界面的面板几何 / 每个兵种格的位置与图标源矩形，到底落在 `.res` 的哪几行、哪个键（`"ControlName"` / `"fieldName"` / `xpos` / `ypos` / `wide` / `tall` / `texture` …）。
   - ⛔ `.res` 里的**非法/默认值**（如 `-1 0`）要如实标注，别当几何用。
   - ⛔ **查不到的 ⇒ 如实 BLOCKED**（写清试过什么、缺什么：例如缺 `classmenu_*.spr` 就明说），**不许继续拿自建常量冒充原版**。
2. **图标**：原版 `.res` 会引 `resource/UI/` 下的精灵；若那些 `.spr`/纹理不在盘 ⇒ 如实列 BLOCKED（本片**不负责下载**，缺就报）。
3. **中文**：用户此前明确要求界面中文（买枪那半已做）⇒ 选兵种同样要中文，且要**实机字形**（不是方框）。
4. 判据 = ① 一条命令的原始输出（面板与每个兵种格的上屏矩形，与搬运来源逐项对齐 `文件:行`）② 一张 1920×1080 实机截图（选兵种界面，能读出中文与兵种图标）。
   ⛔ 不填验收表、⛔ 不建覆盖矩阵。

## 硬约束
1. ⛔ 不改引擎源码、⛔ 不改 skill。
2. 写入范围：`client/Assets/Scripts/UI/Flow/TeamSelectPanel.cs`、`client/Assets/Scripts/UI/Flow/CsUiStyle.cs`（**仅当必要**，且回报里说明为什么）、
   `client/Assets/Editor/Flow/**`、`client/Assets/Resources/Art/**`、`.ai-tmp/**`、`策划/差异登记.tsv` **r71**（只这一行）。
   ⛔ **不写** `Module/Combat/**` 与 `Core/CsWeapons.cs`（agent-46 在改）、`Module/Bot/**`（agent-48）、`Module/View/**`。
3. **预制体改动走定向补丁**（`PrefabUtility.LoadPrefabContents` + `SaveAsPrefabAsset`），⛔ 不重跑整个 `Editor/Flow/FlowSetup.cs`（会连带重写 8 个预制体 + 两个场景）。
4. **Play 是独占资源**：进前看 `.ai-tmp/test/play-log.tsv` 最近 2 分钟有无 `OCCUPY`；**用户已明确"可以实机验证"** ⇒ 截图必须采。
5. 注释按 skill §3 第 3b 条：只写「现在是什么」——⛔ 不写批次 / 本轮 / 任务书 / 用户报的 / 日期 / 别的工程名。
6. 命令一律显式超时；超过 12 分钟把进度落盘 + 给 `main` 一条回报（**第一行写你的模型标识**）。

## 回报（`send_message` 给 `main`）
① 载体查证结论（哪一行 / 哪个键 / 逐字段值，`文件:行`）；② 改了哪些文件 + 每处一句话；③ 判据原始输出 + 截图路径；
④ 仍无出处的部分**逐条列名 BLOCKED**（含缺的载体）。⛔ 不要长解释。
