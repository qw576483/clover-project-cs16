# 参考游戏规格：Counter-Strike 1.6（单机版）

> **规模档位：L 档**（1:1 复刻 / 多系统 ⇒ **3-way + 全状态全边界 + 全因果交叉**）。
> 判据表 = skill `patterns/full-coverage-audit.md` §9.5；**判一次，只许上调**，⛔ 不许因赶时间下调。
> 落地：`策划/实体清单.tsv`（脚本枚举）+ `策划/状态矩阵.tsv` + `策划/验收表.md` §G（一行一实体判定），
> 闸门 = `tools/verify.ps1` 的 `coverage-matrix` / `scale-tier` / `impact-radius`。
>
> 参考游戏 A = **Counter-Strike 1.6**（Valve，2003）。
> 版本形态：**单机版（用户指定）** —— 不接 Clover 服务端，本地开图 + 机器人，与官方"New Game（本地监听服务器）"体验一致。
> 本文件是**动工前的规格真源**，也是 `策划/验收表.md` 的来源。任何一项没做到 = 没做完。

## 0. 口径（2026-09-19 按实测原版载体修订，**以下三条优先于本文其余处**）

1. **数值口径**：原版有"随包 `server.cfg`"与"`mp.dll` 出厂默认"两套值 —— 本规格一律取**随包 `server.cfg`（玩家实际生效值）**，
   `mp.dll` 出厂默认作为第二口径登记在 `策划/对照表.md`。出处与逐项实测见 `原版资源/解包产物/原版数值表.md`。
   ⇒ 因此：**冻结 4 s**（`server.cfg:51`）、**回合 105 s**（`:37` `mp_roundtime 1.75`）、**C4 35 s**（`:43`）、
   **买枪期独立 15 s**（`:42` `mp_buytime 0.25`，与冻结期是两个独立计时器）、起始 $800、`sv_gravity 800`、`sv_maxspeed 320`。
   ⛔ `原版资源/cs-docker/files/server.cfg` 是**社区 docker 配置**，不算原版、不采信。
2. **素材口径**：**必须用 A 自己的原版素材**（解包自原版 CS 1.6 客户端 ISO）。⛔ 不用 KayKit / Poly Haven / 内置几何体之类的通用兜底素材。
   获取历程见 `策划/素材调研.md`；载体位置与复现步骤见 `原版资源/清单.md`。
3. **地图口径**：几何**从原版 `de_dust2.bsp` 解析**（lump 几何 + 内嵌 miptex + 实体），
   由 `Editor/MapGen/Dust2Builder.cs` 生成 33 个材质组与 811 个阻挡盒，并写出 `de_dust2.bytes` 位图（127×145）；
  > 数字口径（2026-09-21 切片F 回写）：阻挡盒**只**由 `tools/probes/rebuild-blockers.py` 产出（按"人体高度带"逐格判定后贪心合并），
  > 且 `geo.bin` 阻挡盒数 == `.bytes` 头 `colliders` == `.bytes` 碰撞体段条数（**逐条 AABB 等价**）== 场景 `Blocker_*` 数 = **811**；
  > 数值变更来由（2026-09-21 切片AB 回写：**656 → 811**）：位图规则由「**存在**某层地面即整格可走」(`∃f`) 改为「**只看基层**」
  > （`∀ f ≤ f0+StepUpHeight`，`f0` = 该格最低地面候选；`tools/probes/rebuild-blockers.py` 文档 §④），
  > 逐格重判后阻挡格变多、贪心合并出的矩形也随之增加 ⇒ 656 → 811。判据 = `tools/probes/geom-check.py`（A1）+ `enumerate-entities.py` 的 D9 行。
  > 材质组 = `geo.bin` 的组数 = 场景 `Visual/*` 的 MeshFilter 节点数 = **33**。判据见 `tools/probes/geom-check.py`（A1/A2）。
   ⛔ **不许用"手工摆盒子"近似**（§0.5 禁写条款：参考物已有的东西只能解析搬运）。
   天空盒同样取原版本体 `gfx/env/des{ft,bk,lf,rt,up,dn}.tga`。

---

## 1. 官方 CS 1.6 主循环（从打开游戏到打完一场）

```
启动 → 主菜单
         ├─ New Game（新建游戏）→ 选地图 + 游戏规则 + bot 设置 → 开始 → 读条 → 选阵营 → 进图
         ├─ Find Servers（查找服务器）→ Internet / LAN / Favorites 标签 → 列表 → 加入
         ├─ Options（选项）→ Audio / Video / Mouse / Keyboard / Multiplayer(名字)
         └─ Quit（退出）

进图后（回合制循环）：
  冻结期(Freeze, 4s) 与买枪期(15s) **并行** → 回合进行(105s) → 回合结束(结算 5s) → 下一回合
  半场交换（默认 15 回合后换边）→ 比赛结束（比分/胜负画面）→ 回主菜单

游戏内随时：
  B  买枪菜单（仅在买枪区域 Buy Zone 内、买枪时间内）
  TAB 记分板
  H  H 菜单（机器人管理 / 回合设置 / 换图 / 重开）
  ESC 菜单（继续 / 选项 / 换阵营 / 断开）
  Z/X/C 无线电菜单
  E  使用（拆包 / 救人质）
  1/2/3/4/5 切换武器槽
  死亡后 → 观战（自由视角 / 跟随队友 / 跟随敌人）
```

---

## 2. 系统清单（逐项做完，不许砍）

### 2.1 启动与菜单链路
| # | 系统 | 官方表现 | 我们的实现 |
|---|---|---|---|
| M1 | 启动画面 | 黑底 + Valve/游戏 Logo + 版权字，几秒后进主菜单 | `BootPanel`（Boot 场景），2 秒后自动进主菜单，可按任意键跳过 |
| M2 | 主菜单 | 左侧竖排按钮：New Game / Find Servers / Options / Quit；背景为地图截图 | `MainMenuPanel`（Menu 场景） |
| M3 | New Game 对话框 | 地图列表 + 规则下拉（默认/自定义）+ 开始按钮 | `NewGamePanel`：地图选择（de_dust2）+ 规则设置 + Bot 设置 + 开始 |
| M4 | Find Servers | 标签页（Internet/LAN/Favorites）+ 服务器列表表格 + Refresh/加入 | `ServerListPanel`：调 `Game.LanBrowser.Scan()`，列 LAN 主机；空则显示"未找到服务器" |
| M5 | Options | Audio/Video/Mouse/Keyboard/Multiplayer 分页，改完保存 | `OptionsPanel`：5 个分页，全部真能改 + 落 `Game.Setting` |
| M6 | 选阵营 | 进图时弹阵营选择（CT/T/Spectator/Auto） | `TeamSelectPanel`：CT / T / 随机 / 观察者 |
| M7 | 读条 | 真进度条 + "Loading..." + 地图名 | `LoadingPanel`：`Game.Scene.Load` 进度驱动 |
| M8 | ESC 菜单 | 继续 / 选项 / 换阵营 / 断开（单机=回主菜单） | `PausePanel` |
| M9 | 退出 | 关窗口 | `Quit`（编辑器下停止 Play） |

### 2.2 游戏内 HUD
| # | 系统 | 官方表现 | 我们的实现 |
|---|---|---|---|
| H1 | 血量/护甲 | 左下角 `♥ 100` / `🛡 100`，护甲有无头盔用不同图标 | `HudPanel` |
| H2 | 金钱 | `$` + 数字（绿色），买枪时变色提示买得起/买不起 | `HudPanel` |
| H3 | 弹药 | 右下角 `30 / 90`（弹匣/备弹） | `HudPanel` |
| H4 | 回合时间 | 顶部中间 `1:45` 倒计时 | `HudPanel` |
| H5 | 比分 | 顶部 `CT 3 - 2 T`（含小图标） | `HudPanel` |
| H6 | 雷达 | 左上角圆形雷达，显示队友/敌人（仅可见）/炸弹点 | `RadarWidget` |
| H7 | 准星 | 中心动态准星（随移动/射击扩散） | `CrosshairWidget` |
| H8 | 击杀信息 | 右上角滚动 `A [AK-47] B`（含爆头图标） | `KillFeedWidget` |
| H9 | 命中标记 | 准星处 X 型命中提示（普通/爆头不同色） | `CrosshairWidget` |
| H10 | 购买区域提示 | 底部 `Buy Zone` 文字 | `HudPanel` |
| H11 | 观战 HUD | 死亡后显示"观战中"+ 切换目标提示 | `SpectatorWidget` |
| H12 | 回合结算 | 中央大字 `Counter-Terrorists Win` / `Terrorists Win` + 原因 | `RoundEndPanel` |
| H13 | 比赛结束 | 结算画面（比分 + 胜方 + 回主菜单） | `MatchEndPanel` |
| H14 | 控制台 | `~` 调出，可输入命令 | 简化：`ConsolePanel`（显示日志 + 简单命令） |

### 2.3 玩法系统
| # | 系统 | 官方表现 | 我们的实现 |
|---|---|---|---|
| G1 | 阵营 | CT / T / Spectator；`mp_autoteambalance` | `CsTeam` 枚举 + 自动平衡 |
| G2 | 经济 | 起始 $800（`mp_startmoney`）；击杀奖励（武器相关 $300/$600/$1500）；回合胜 $3250；T 下包 +$800；连败递增；上限 $16000 | `CsEconomy` |
| G3 | 买枪菜单 | 买枪区（`func_buyzone` T/CT 各一块）+ **买枪期 15 s**（`mp_buytime 0.25` · `server.cfg:42`，与冻结期**独立计时**）内按 B 呼出；数字键分类（1 手枪 / 2 冲锋 / 3 步枪 / 4 机枪 / 5 霰弹 / 6 装备 / 7 手雷 / 8 回头）；显示价格与余额 | `BuyMenuPanel` |
| G4 | 武器系统 | 主武器/手枪/刀/手雷/炸弹 槽位；`1`-`5` 切换；拾取地上武器 | `CsWeapon` + `CsActor.Inventory` |
| G5 | 射击 | 左键射击；射速/弹匣/换弹（R）；后坐力（枪口上抬 + 准星扩散）；精度受移动/跳跃/蹲影响；穿透 | `Module/Combat/Firearm.cs` |
| G6 | 伤害模型 | 部位倍率（头 4x / 胸 1x / 腹 1.25x / 腿 0.75x）；护甲吸收；距离衰减；穿透衰减 | `CsDamage` |
| G7 | 炸弹（C4） | T 出生随机一人持包；在 B 点长按 E 下包（3s）；**35 s** 倒计时（`mp_c4timer 35` · `server.cfg:43`）+ 蜂鸣；CT 按 E 拆包（10s / 有拆弹器 5s） | `CsBomb` |
| G8 | 回合系统 | Freeze（**4 s**）→ Live（**105 s**）→ RoundEnd（5 s）；回合胜负（炸弹爆炸/拆包/全歼/超时）；半场交换；比分 | `CsRound` |
| G9 | 观战 | 死亡后第一人称观战；切换跟随对象；自由视角 | `SpectatorCamera` |
| G10 | 机器人 | H 菜单/New Game 添加；名字池；3 档难度；行为：巡逻/寻敌/瞄准/射击/买枪/下包/拆包/跟随 | `CsBotBrain` |
| G11 | 记分板 | TAB 按住显示：玩家名 / 击杀 / 死亡 / 延迟 / 状态（活着-死亡）/ 阵营分组 | `ScoreboardPanel` |
| G12 | H 菜单 | 快捷键 H：添加机器人 / 踢出机器人 / 机器人难度(3档) / 换地图 / 重开回合 / 暂停 / 队伍人数 | `HMenuPanel` |
| G13 | 无线电 | Z/X/C 三组无线电命令 + 聊天栏显示 | `RadioMenuPanel` |
| G14 | 雷达 | 显示自己/队友/可见敌人/炸弹点/炸弹 | `RadarWidget` |
| G15 | 死亡与复活 | 死亡→观战；下回合开始复活（满血、原点、原装备保留规则） | `CsActor.Respawn` |
| G16 | 脚步声 | 跑动有声、行走（Shift）无声、跳跃落地有声 | `Module/Audio` |
| G17 | 摄像机 | 第一人称：眼高 1.6m；跑动轻微 bob；受击晃动；瞄准（AWP 右键开镜） | `FirstPersonCamera` |
| G18 | 队友伤害 | `mp_friendlyfire` 开关（默认关） | `CsDamage` |
| G19 | 人质 | de_dust2 无需（de_ 地图是炸弹模式）→ 不做，明示排除 | — |

### 2.4 机器人 AI（3 档难度，官方 PodBot 风格）
| 难度 | 反应时间 | 瞄准误差 | 命中率特征 | 行为 |
|---|---|---|---|---|
| Easy | 0.5~0.8s | 大（±6°） | 常打偏、射速慢、不追远敌 | 会走固定路线、会买便宜枪 |
| Normal | 0.25~0.4s | 中（±3°） | 一般 | 会走路线 + 会包抄 + 会买中等枪 |
| Hard | 0.1~0.2s | 小（±1.2°） | 精准、会点射/爆头倾向 | 会预瞄、会换位、会买最好枪、拆包果断 |

行为树（3 档共用，参数不同）：
```
巡逻（沿导航图去目标点）
  ↓ 发现敌人（视野 + 距离 + 朝向）
交战（停/蹲 → 瞄准 → 射击 → 走位）
  ↓ 目标死亡或丢失
回到巡逻 / 找目标（T 去炸弹点 / CT 去守卫点）
  ↓ T 持包到 B 点 → 下包；CT 听到拆包/看到包 → 去拆包
```

---

## 3. de_dust2 规格（唯一实现的地图）

原版 de_dust2 是沙漠小镇，核心地标（必须全部存在且连通）：

| 地标 | 说明 | 连接 |
|---|---|---|
| T Spawn | 恐怖分子出生点（地图东南） | → T 坡道 → Mid / Long A / B Tunnels |
| CT Spawn | 反恐精英出生点（地图西北） | → A Site / B Site / Mid |
| Bombsite A | A 点（东北），有平台与箱子 | ← Long A / Catwalk / CT Spawn |
| Bombsite B | B 点（西南），有箱子掩体 | ← B Tunnels / Mid → B Door |
| Mid | 中路（开阔长廊），两端连接 T Mid 与 CT Mid | 上接 CT Spawn，下接 T Spawn |
| Long A | A 大道（长直道，有门） | T Spawn ↔ A Site |
| Catwalk / Short A | A 小道 | Mid ↔ A Site |
| B Tunnels | B 通道（隧道） | T Spawn ↔ B Site |

实现方式：**解析原版 `de_dust2.bsp`**（lump 几何 + 内嵌 miptex + 实体），由 `client/Assets/Editor/MapGen/Dust2Builder.cs`
按材质批量生成 Mesh（**33 个材质组 / 242 张原版内嵌贴图**）+ **811 个阻挡盒**，再写出 `de_dust2.bytes`（**127×145 位图**）供 `Game.Map.WalkableAt` 使用；
天空盒取原版本体 `gfx/env/des{ft,bk,lf,rt,up,dn}.tga`。

> 保真度目标：**几何来自原版 BSP 本体**（不是"搭一个像的"）；连通性与地标逐项可自证（`Clover/CS16/地图连通性自证（de_dust2）`），
> 逐元素原版值对照见 `策划/对照表.md` 的 **G（几何）/ T（贴图）** 两块。
> ⚠️ 原版编译期 WAD（`cs_dust.wad`）本项目内不可得 ⇒ 11 张来自 BSP 内嵌、15 张按角色回落到同族沙漠贴图，
> 已登记在对照表 **T-05** 与验收表「允许的差异」。

---

## 4. 排除项（明确不做，写清楚而不是偷偷砍）

- 联网对战 / 真实服务器（**用户指定单机**）；
- 人质模式 / as_ / cs_ 地图；除 de_dust2 外的所有地图；
- Steam 成就 / VAC / 服务器插件（AMX/AMXX）；
- 手雷的完整物理弹跳（做简化抛物线 + 爆炸伤害）；
- 逐像素地图像素级复刻。

---

## 5. 素材来源（2026-09-19 修订：实际用的是 **A 自己的原版素材**）

- **一级来源（已用）= A 自己的原版素材**：原版 CS 1.6 客户端 ISO（archive.org `cstrike-LiON.iso`）→ `innoextract` 解包 → `cstrike/**`
  - 角色模型 `models/player/{terror,leet,arctic,guerilla,urban,gsg9,sas,gign,vip}/*.mdl`（9 皮肤，GoldSrc MDL v10）
  - 第一人称武器 `models/v_*.mdl`（29 把）
  - 音效 `sound/{weapons,player,radio,items}/*.wav`（91 个）
  - 地图 `maps/de_dust2.bsp`（几何 + 内嵌 miptex + 实体）、天空盒 `gfx/env/des*.tga`、HUD 精灵 `sprites/640hud7.spr`
  - **动画**：同一批 mdl 内的原版序列（`idle1 / walk / run / crouch_* / jump / death1..3 / ref_shoot_* / ref_reload_*`），帧率与帧数取自 mdl 本体
- 获取历程与站点类别：`策划/素材调研.md`；载体位置 / 解包命令 / 复制目标：`原版资源/清单.md`
- **本项目非商用 ⇒ 不设许可门槛**；每项来源已记在 `client/资源欠缺清单.md`
- ⛔ 不许换成通用兜底素材（KayKit / Quaternius / 内置几何体）；程序化自产的只有 `ui_white.png`（已在资源表标注"非原版"）


---

## 6. 取证经济性（闸门口径，2026-09-22 片AI 落纸）

> 出处 = clover-engine skill §1.13 T0（⛔ 不许逐行截图 / 不许逐项进出 Play）与模板第 18 条 `evidence-economy`。
> 本节把「该采多少图」变成**可算的数字**，避免"N 行验收表"被读成"要拍 N 张图"。

- **一个交付单元 = 一次联络图**：N 个证据点压进 1 张联络图（格号 + 状态 + 关键数值），AI 只读汇总图。
- **联络图索引是唯一入口**：`tools/probes/*.manifest.tsv`（格号 / 图路径 / 逐格结论）或 `.ai-tmp/screenshots/*.index.tsv`。
- **散图预算**：不出现在任何索引里的 png（"散图"）**≤ max(12, 表现类行数 x 2)**；超出即判"逐行截图"（闸门第 35 条 `evidence-economy` 判红）。
  依据 = 模板第 18 条给出的同一公式；本项目 2026-09-22 实测：表现类 35 行、9 份索引、入库 67 张、散图 19 ≤ 预算 70。
- **零引用 png = 0**：`.ai-tmp/screenshots/` 里每张图必须被某个表 / 清单 / 脚本引用（闸门第 26 条 `shot-refs-audited`，⛔ 不在第 35 条重复实现）。
- ⛔ 取证图不许进 `client/Assets/**`（闸门第 30 条 `no-assets-screenshots`）；一次性产物只许 `.ai-tmp/test/`。
