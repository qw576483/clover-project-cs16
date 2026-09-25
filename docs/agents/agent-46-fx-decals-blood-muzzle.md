# agent-46 · 特效三项：弹痕多变体+尺寸、受击血雾、枪口火焰逐武器映射

## 为什么三项同一片做
`Module/Combat/CombatEffects.cs`（38 KB）是这三项**共同的落点**（decal / blood / muzzle 都在这一个文件里），拆片必然互相覆盖。按 弹痕 → 血雾 → 枪口火焰 顺序做，每项做完各自取证。

## 载体（**已全部在盘，本轮刚取回，⛔ 不用再下载**）
- `原版资源/cs16src/valve/sprites/bloodspray.spr`（41,970 B）· `blood.spr`（3,294 B）· `blooddrop.spr`（1,362 B）
  ⚠️ 注意来源：这三件在该 repack 里**只在 `valve/sprites/`**（`cstrike/sprites/` 没有），落盘路径就是 `原版资源/cs16src/valve/sprites/`
- `原版资源/cs16src/cstrike/decals.wad`（弹痕/血迹贴花；`{shot1..5}`、`{bigshot*}`、`{blood*}`）
- `原版资源/cs16src/hw.dll`（1,840,440 B，**枪口贴图逐武器映射表 + FOV 调用点**）
- `原版资源/cs16src/cstrike/cstrike__sprites__640hud1..20.spr`、`cstrike__sprites__hud.txt`、`sprites/weapon_*.txt`（31 份）
- 工具：`tools/probes/spr-extract.py --info --in <file>`（`.spr` 两条恒等式校验）、`tools/probes/wad3-extract.py`（`.wad` 解包）
- 现况前情逐字在 `策划/差异登记.tsv` **r70 / r75 / r90** 三行（⛔ 别重查历史，直接读）

## 三项各自的目标与判据
### 1. 弹痕（r70）
- 把 `{shot1..5}` / `{bigshot*}` **全解出来**（现在只有 1 张变体）⇒ 工程侧支持**多变体 + 随机取一**；
- **尺寸按贴图原生比例**推世界单位（⛔ 不许拍一个"看起来差不多"的尺寸；写不出出处就如实 BLOCKED）；
- 判据：多变体解出清单 + 实机并排图（同一面墙、同一机位，多发射击出现不同弹痕）。

### 2. 受击血雾（r75）
- `decals.wad` 抽 `{blood*` 贴花 + `valve/sprites/{bloodspray,blood,blooddrop}.spr`；
- **定死"贴在哪 / 贴几张 / 触发时机"**（命中点方向、是否随伤害量、是否淡出）—— 定不下来就如实 BLOCKED 并写清缺什么，⛔ 不许拿"染色替身"充数；
- 判据：实机受击帧（能看出血雾/血迹）+ 与旧"无特效"的对照。

### 3. 枪口火焰逐武器映射（r90）
- M249 十字**已修**（别重做）；本片补**逐武器映射**：从 `hw.dll` 取每把枪的 muzzle 贴图/序列映射，落进 `Core/CsWeapons.cs` 的武器表；
- ⛔ 每把枪的映射都要有出处（`hw.dll` 偏移 + 字节 / 或 `muzzleflash*.spr` 的帧名 + `weapon_*.txt` 行）⇒ 无出处的枪如实 BLOCKED 列名；
- 判据：逐武器并排图（同一机位、同一帧序）+ 映射表（枪 | 贴图/帧 | 出处）。

## 硬约束
1. ⛔ 不改引擎源码（`client/Packages/com.clover.unity-engine/**`）、⛔ 不改 skill。
2. 写入范围：`client/Assets/Scripts/Module/Combat/**`、`client/Assets/Scripts/Core/ResPaths.cs`、`client/Assets/Scripts/Core/CsWeapons.cs`、
   `client/Assets/Resources/Art/**`、`client/Assets/Editor/**`、`.ai-tmp/**`、`策划/差异登记.tsv` **r70 / r75 / r90**（只这些行）。
   ⛔ **不写** `Module/Bot/**`（agent-48 在改）、`Module/View/**`（agent-47 在改）、`UI/**`、`Editor/Flow/**`。
3. **Play 是独占资源**：离线部分（解包、映射表、校验）做在前面；进 Play 前先看 `.ai-tmp/test/play-log.tsv` 最近 2 分钟有无 `OCCUPY` 未释放，有 ⇒ 错峰；⛔ 别开第二个编辑器会话。
   **用户已明确"可以实机验证"** ⇒ 该采的实机图必须采，别只给离线数字。
4. 注释按 skill §3 第 3b 条：只写「现在是什么」——⛔ 不写批次 / 本轮 / 任务书 / 用户报的 / 日期 / 别的工程名。
5. `.spr` 一律用 `spr-extract.py --info` 校验（两条恒等式）；`.cs16*` 若重写必须搬尾部标记 + **回读校验**（见踩坑清单：`.cs16anim` 尾部 `ENDA`）。
6. 判据 = **一条命令 + 原始输出 / 截图**；⛔ 不填验收表、⛔ 不建覆盖矩阵。
7. 命令一律显式超时；超过 12 分钟把进度落盘 + 给 `main` 一条回报（**第一行写你的模型标识**）。

## 回报（`send_message` 给 `main`）
① 三项各自：改了哪些文件 + 出处（文件:行 / 偏移 / 字节）+ 判据原始输出 + 截图路径；② 逐项无出处的部分**列名 BLOCKED**；③ 没做到的逐条原因。⛔ 不要长解释。
