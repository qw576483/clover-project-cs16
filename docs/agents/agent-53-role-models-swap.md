# agent-53 · 把 7 个新兵种模型转出并落地替换（#71 完整闭环）

## 背景：载体已全部到位，且**不需要官方客户端**（前一片刚取回）
`原版资源/cs16src/cstrike/` 下已有 **9/9** 原版兵种模型（都由同一 repack 渠道取回，逐件 `IDST + v10 + hdr.length==filesize` 校验通过，`gsg9` 另有两个独立源逐字节同 hash）：
```
cstrike__models__player__terror__terror.mdl      2340680   ← 已落地（player_T）
cstrike__models__player__urban__urban.mdl        2329144   ← 已落地（player_CT）
cstrike__models__player__gign__gign.mdl          2329328   ← 本片新
cstrike__models__player__gsg9__gsg9.mdl          2333832   ← 本片新
cstrike__models__player__sas__sas.mdl            2388900   ← 本片新
cstrike__models__player__vip__vip.mdl            2175676   ← 本片新
cstrike__models__player__leet__leet.mdl          2401992   ← 本片新
cstrike__models__player__arctic__arctic.mdl      2395944   ← 本片新
cstrike__models__player__guerilla__guerilla.mdl  2341764   ← 本片新
```
⚠️ **`militia` / `spetsnaz` 不属于 CS 1.6**（CZ 侧名字，已取证）⇒ 这两个**不要去找、不要造**。

## 可复用的现成链路（别重造）
- **转换**：`原版资源/cs16src/cs16_build.py <mdl> <key> <out> [--tex-dir DIR]` 与 `cs16_anim.py <mdl> <key> <out>`
  —— 用法就是 `mdl-converter`/`clip-track-fix` 修好的那份；key 用**目标皮肤键**（如 `player_CT_sas`，⛔ 不是 `_orig`）。
- **替换**：`modeldata-swap` 留下的七步链 `.ai-tmp/drivers/mswap-swap71.ps1`（含 `-Target T|CT` 开关、冻结 sha 门闸、备份、`--tex-dir`、重跑生成器、`DumpModels` 近景）—— **优先复用**，必要时按本片需要扩展成"逐兵种"。
- **参照已完成的两个样板**：`player_T`（terror）与 `player_CT`（urban）的落地方式——`ModelData/player_T.cs16anim` 1475878 B / clips 69 / `AnimSetup` 同名告警 0。

## 目标（7 件：CT 4 + T 3）
1. **逐件转换 + 落地**：`gign / gsg9 / sas / vip` → `player_CT_*`；`leet / arctic / guerilla` → `player_T_*`（皮肤键名照工程既有用法，自己 `grep` 确认）。
2. **内部 key 必须与目标皮肤键一致**（否则 `Verify()` 找不到对应 `.controller`）。
3. **贴图**走正式链 `--tex-dir`，落 `Art/Tex/`。
4. **重跑生成器**（会让 `Art/**` 一批文件重烘；`T`/`CT` 已完成的两个**必须验未退化**：`ModelData/player_T.cs16anim` sha16 应仍 `3a1ed44bd079292e`）。
5. **实机核到新网格 + 新材质**（像 `player_CT` 那次：`mesh0=player_CT_Skin0 mat=SEAL_Working1`）。4v4 里**未必抽到某个兵种** ⇒ 可像 `modeldata-swap` 那样**实例化 `Art/{T|CT}/<皮肤键>` 预制体 + 探针相机**取证（它踩过的两个坑：未绑定实例的 `ActorView.IsShown=false`；按 `actor.Yaw` 摆位会摆进墙，须按相机前向水平摆）。

## 硬约束
1. ⛔ 不改引擎、⛔ 不改 skill。
2. 写入范围：`client/Assets/Editor/Views/ModelData/**`、`client/Assets/Resources/Art/**`、`client/Assets/Editor/**`（生成器相关）、`.ai-tmp/**`、`策划/差异登记.tsv` **#71 行**（行号 72）。
   ⛔ **不写** `Module/Bot/**`（另一片 `height-reach` 正在改）、`Module/Combat/**`、`Module/Map/**`、`Core/**`、`UI/**`、`Module/View/**`。
3. **`.cs16anim` 尾巴 `ENDA` 4 字节必须原样在 + 回读校验**（漏了只差 4 字节但整份读不出）；`.ps1` 含非 ASCII 必须 **UTF-8 with BOM**。
4. **Play 是独占资源**：`height-reach` 片可能也在用 ⇒ 进前看 `.ai-tmp/test/play-log.tsv` 末行是否 `RELEASE` 且 `play-driver-log.tsv` 近 90 秒无新行；`OCCUPY`/`RELEASE` 配对、格式 `2026-09-25 HH:mm`；⛔ 别写共用 `state.txt`。
   ⚠️ 既有驱动进局链路**必须补 `Clk 'urban'`**（选兵种页是后来加的），否则卡在选兵种页。
5. **改资产前先 `editor_stop`**；⛔ 绝不 `Stop-Process`；⛔ 不开第二个编辑器实例；**备份到 `.ai-tmp/test/a53-bak/`**。
6. 判据 = 一条命令 + 原始输出（逐兵种 `clips` 数 / `AnimSetup` 同名告警数 / 实机 mesh+材质名）+ 截图；⛔ 不填验收表、⛔ 不建覆盖矩阵。
7. 注释按 skill §3 第 3b 条：只写「现在是什么」——⛔ 不写批次 / 本轮 / 任务书 / 用户报的 / 日期 / 别的工程名。
8. 命令一律显式超时；超 12 分钟把进度落盘 + 给 `main` 一条回报（**第一行写模型标识**）。

## 回报（`send_message` 给 `main`）
① 逐兵种一行：产物路径 | 字节 | `clips` 数 | `AnimSetup` 告警数 | 实机 mesh+材质名（采到的）；② 改了哪些文件；③ **T/CT 两个已完成样板未退化**的证据；④ 截图路径；⑤ 没做到的逐条 BLOCKED。⛔ 不要长解释。
