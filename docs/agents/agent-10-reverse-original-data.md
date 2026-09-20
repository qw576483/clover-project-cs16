# agent-10：解除 BLOCKED-1 / BLOCKED-2 —— 从原版二进制取权威数值（clover-project-cs16）

> 项目根：`c:\Work\Server\full-dev\clover-project-cs16`
> 本片**纯离线**（Python / 二进制解析 / 图片量化），⛔ 不许碰 Unity 工程与 `client/Assets/**`。

## 0. 背景（主 agent 已核实）

`策划/对照表.md`（agent-09 产出）里有 2 个关键 BLOCKED，它们让"数值与公式"这块**没有原版出处**：

- **BLOCKED-2**：29 把武器的 `伤害/射速/弹匣/护甲穿透/射程/换弹/击杀奖励/每武器 maxspeed` —— 现在只能靠 `CsWeapons.cs` 自定值（**写不出出处 = 违规**）；
- **BLOCKED-1**：屏幕 HUD 的绝对像素坐标与 HUD 文字色。

两者都在**同包内的原版二进制**里，属 §0.5 载体降级链**第 2 级（可执行程序里的常量与公式）**：

- `原版资源/cs16src/cs16game/app/cstrike/dlls/mp.dll`（1,316,152 B）—— 服务器端武器/规则
- `原版资源/cs16src/cs16game/app/cstrike/cl_dlls/client.dll`（1,074,496 B）—— HUD 渲染
- 可直接参考的实现：`原版资源/cs16src/hlsdk/**`（GoldSrc SDK，含 `pm_shared.c` / `weapons.cpp` 等**同族**实现，注意区分 HL 与 CS 的差异）

## 1. 产物一：`原版资源/解包产物/原版数值表.md`

从 `mp.dll` 解析并落盘（每行必须带**字节偏移**）：

1. **cvar 默认值**（至少）：`mp_freezetime` `mp_roundtime` `mp_c4timer` `mp_buytime` `mp_startmoney` `mp_maxrounds` `mp_winlimit` `sv_gravity` `sv_maxspeed` `sv_friction` `sv_accelerate` `sv_airaccelerate` `mp_friendlyfire` `mp_autoteambalance` `mp_tkpunish`；
   > 做法提示：GoldSrc 的 `cvar_t` = `{ char* name; char* string; int flags; float value; cvar_t* next; }`（32 位）。先在 `.rdata` 找到 `"mp_freezetime"` 字符串，再在 `.data` 里找指向它的 4 字节引用，读同一结构体的 `value` 字段。**必须先验证结构自洽**（name 指针确实指向该字符串、string 字段能读成合理数字串）才能采信。
2. **武器数值表**（29 把：`weapon_*` 逐个）：伤害（含各部位/护甲公式）、射速（`m_flTimeWeaponIdle` / `CBasePlayerWeapon::GetNextAttackDelay`… 以你实测到的形状为准）、弹匣容量、护甲穿透、击杀奖励、每武器 `maxspeed`、换弹时长；
   > 做法提示：先找 `"weapon_ak47"` 这类类名串与武器信息结构（社区已知 CS 1.6 的 `WeaponInfo` 条目含 `iId / pszName / iPrice / iDamage / flArmorRatio / iMaxClip / iMaxAmmo / flRange / iKillAward` 等）。
3. **`mp_buytime` 与买枪区判定**（用于后续修"我方没有独立买枪期"这个缺口）。

**每条都要实测复现**：给出读法（Python 片段 + 偏移），自查 ≥5 条能重跑一致。

## 2. 产物二：`原版资源/解包产物/原版HUD布局.md`

1. **首选**：从 `client.dll` 里定位 HUD 元素坐标/字号（同上的字符串→结构法；CS 1.6 HUD 是 `HudFont` / `SPR_*` 绘制，**若 2 小时内定位不到就转 2**，并在文件中写明"二进制这条路试过什么、卡在哪"）；
2. **降级（合法，§0.5 降级链第 4 级）**：用**原版截图量化成表** —— 载体在项目内：`原版资源/cs16-maps/screenshots/**`、`原版资源/cs16-maps/screenshots_to_conv/**`、`原版资源/de_dust2/IMG/**`、`原版资源/解包产物/**`（先 `list_dir` 看有哪些、分辨率多少）；
3. 量化表格式：`元素 | 原版像素（x,y,w,h；注明基准分辨率与出处图） | 换算到 1080p | 我方值（文件:行） | 差值`；
4. 若两个来源都覆盖不到某个元素 ⇒ 如实进 BLOCKED，⛔ 不许编。

## 3. 产物三：更新 `策划/对照表.md`（只改"数值与公式"与"HUD"两块 + BLOCKED 两节）

- 把 BLOCKED-1 / BLOCKED-2 里**已解除**的条目改成正常行（原版值带新出处）；确实没解除的**保留在 BLOCKED**并补写"本次试了什么、卡在哪"；
- 已有行的"原版值"若与本片新解析结果**冲突** ⇒ 以本片实测为准并注明原因；
- 补齐本片新发现的原版值对应的行（如买枪期）；
- ⛔ 表格结构（列数、类别列口径）不许改；⛔ 不许删既有行。

## 4. 判据（自己跑，原始输出贴进回报）

1. 三份文件都在，行数/条目数如实报出；
2. 抽查 ≥5 条数值，用**可复跑命令**重读一次，输出原文贴出；
3. 报表里凡是"原版值"都有 `文件:偏移`（或截图坐标）—— ⛔ 0 条例外（拿不到的一律 BLOCKED）；
4. 与"我们现在的值"（`client/Assets/Scripts/Core/CsWeapons.cs`、`CsConst.cs`）**逐项对比一遍**，把全部**非零差值**列成一张清单放进回报（这张清单是下一片"数值对齐"的输入）。

## 5. 不许

- ⛔ 不许改 `client/**` 任何文件（本片只解析、只写文档）；
- ⛔ 不许改 `策划/验收表.md`、`策划/策划案/**`、`docs/步骤文档.md`、任何 skill；
- ⛔ 不许编造偏移/数值/截图像素；拿不到 ⇒ BLOCKED；
- ⛔ 不许读工作区里别的 `clover-project-*` 工程；
- ⛔ 不许开子 agent；临时脚本只放 `.ai-tmp/test/`，用完删；不许产出交接/进度类 md。

## 6. 回报格式

```
产出物：<文件绝对路径清单>
自检：<实际执行的验证命令 + 原始输出>
非零差值清单：<元素 | 原版 | 我方 | 差>
未决：无 / <具体条目>
```
