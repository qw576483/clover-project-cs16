# agent-07：角色/武器视图 + 音效（CS 1.6 现成素材）

## 0. 技能（开工必做，不许跳）

```
(1) 拿到 skill：工具集里有 use_skill 就用它加载 clover-engine + unity-cli；
    没有就直接读文件（命中即用）：
      · tools/ai-skill/SKILL.md   ← 项目级，首选
      · clover-ai-skill/SKILL.md                ← 仓库源
      · ~/.codebuddy/skills/ai-skill/SKILL.md                                 ← 安装副本
    （unity-cli 同理）
    都找不到 → 回报调用方要路径，**不许凭记忆写代码**。
(2) 然后按 skill 的「混合模式找依据」查代码（用户 > 引擎 > 联网/自创），**不许编 API**。
```

**必读**：
- `docs/步骤文档.md` §3
- `clover-ai-skill/reference/asset-sources.md`（**素材四级链 + 自动获取**）
- `clover-ai-skill/reference/engine-mental-model.md` §4/§6（`Game.Res`/`Game.Anim`/`Game.Pool`、**量测优先**）
- `clover-ai-skill/patterns/client/3d-mmo-basics.md` §7/§8（动画四件事、头顶血条、占位物必须移除）
- `clover-ai-skill/patterns/client/resource.md`（资源清单模板）
- 契约：`Assets/Scripts/Core/*.cs`、`Assets/Scripts/Module/Match/ICsMatch.cs`（`CsHitboxProxy` / `CsActor`）

## 1. 目标

1. **角色视图**：玩家与机器人有可见模型（CS 1.6 的 CT/T 模型，或最接近的现成模型），模型上挂 `CsHitboxProxy`
   （头/胸/腹/腿四段），带**头顶名字 + 血条**；死亡后隐藏、复活后重现；
2. **第一人称手部/武器 viewmodel**：屏幕上能看到自己的枪（跟随相机）；
3. **音效**：脚步（跑/走/落地）、枪声（按武器）、换弹、命中、爆头、死亡、回合开始/结束、炸弹蜂鸣
   —— **用 CS 1.6 的现成 wav**；
4. 生成 `client/资源欠缺清单.md`（哪些是占位、怎么换）。

## 2. 任务边界

**只做**（这些路径只有你能写）：
- `Assets/Scripts/Module/View/**`（`ViewModule.cs` / `ActorView.cs` / `ViewModelRig.cs`）
- `Assets/Scripts/Module/Audio/**`（`AudioModule.cs` / `SfxService.cs`）
- `Assets/Editor/Views/**`（模型导入/克隆/预制体生成）
- `Assets/Resources/Art/**`、`Assets/Resources/Sound/**`
- `Assets/Resources/UI/WorldNameplate.prefab`（头顶名字/血条的**世界空间**预制体）

**绝不做**：
- 不许改契约文件与其它模块目录；
- 不许实现玩法逻辑（伤害/回合/买枪）；
- **不许在 View 里 `Game.Net`**（单机没有网络）；
- **不许开子 agent**。

## 3. 素材获取（**优先 CS 1.6 现成货**，按四级链）

| 级别 | 做法 |
|---|---|
| ① **CS 1.6 自己的素材（首选）** | 找 CS 1.6 的模型/音效资源包（`cs 1.6 player model fbx`、`cs 1.6 sound pack wav`、`counter-strike 1.6 mdl to fbx`、社区提取/复刻资源）。**能下就下**（本项目非商用，不设许可门槛），记一行来源 |
| ② 同类成套 | 找不到就找 CS:S / CS:GO 风格的成套 FPS 角色模型 + 音效 |
| ③ 兜底（**先跑起来，别停工**） | CC0 成套（如 KayKit 角色）、Unity 内置 `Capsule` + 程序生成音（或引擎无音频时的日志钩子） |
| ④ 让用户下 | 只有"需要登录/付费"才交给用户，给清单（包名 + 链接 + 落地目录） |

> **硬规矩**：**不许因为"素材还没到"就停工** —— 先用兜底素材把视图/音效接好（走同一套 API），
> 素材到位后**只换文件不改逻辑**。凡最终仍是占位的，**必须登记进 `client/资源欠缺清单.md` 并在回报首行高亮**。

## 4. 产出物与要求

### 4.1 `ViewModule : MonoBehaviour`（`Cs16.Module.View`）
- `Start()`：拿 `ICsMatch`（`GetComponent<MatchModule>()`，拿不到 → Error + 禁用）；
- `LateUpdate()`：对 `_match.Actors` 做**视图集合同步**（存在则更新、消失则回收）；
- 每个 actor 的视图：
  - 模型：按阵营取 `Art/CT/<name>` 或 `Art/T/<name>`（`Game.Res.LoadAsset<GameObject>`）；
  - **量测优先**：实例化后实测包围盒 → 按目标高度（1.8m）反算缩放 → 贴地（**不许假定 FBX 单位/枢轴**）；
  - **碰撞体**：FBX 不带 Collider ⇒ **自己 `AddComponent<CapsuleCollider>`**，并挂 4 个 `CsHitboxProxy` 子物体
    （`Head` 1.6m±0.15 / `Chest` 1.1~1.5 / `Stomach` 0.9~1.1 / `Leg` 0~0.9，半径约 0.28m），
    **层用 `PhysicsLayers.Bot`**（机器人）或 `PhysicsLayers.Player`（真人玩家——本地玩家自己的模型可隐藏但碰撞体要留）；
  - **头顶血条 + 名字**：用 `WorldNameplate.prefab`（世界空间 Canvas 或用 `CloverEngine.WorldHpBar`，二选一，读源码确认后再写）；
    血条用**锚点宽度**或 1×1 白 sprite 的 Filled（**不要留空 sprite**）；
  - 只对**可见的敌人/队友**显示名牌（`CheckSphere` 或 AoI 简化为距离 + 视线）；
  - 死亡 → `SetActive(false)`；复活 → 位置重置 + 显示；
- **第一人称 viewmodel（`ViewModelRig`）**：相机子节点下放一把枪的模型（CS 1.6 的 `v_*` 模型或最接近的现成模型），
  位置右下角、随跑动轻微摆动、开火时后坐动画（订阅射击事件）；
- 本地玩家自己的第三人称模型：**隐藏 Renderer 但保留 Collider**（否则自己被射线挡住）。

### 4.2 `AudioModule : MonoBehaviour`（`Cs16.Module.Audio`）
- 订阅 `ICsMatch` 的事件与查询：
  - `ConsumeShotFired(out weaponId)` → `Game.Sound.PlaySFXAt(def.SoundFire + suffix, 位置)`（3D 定位）；
  - 脚步：遍历 alive actor，**位置在移动且 `!IsWalking`** 时按速度间隔播 `step`（自己用 `PlaySFX`，别人用 `PlaySFXAt`）；
  - 换弹 / 命中 / 爆头 / 死亡 / 回合开始（`Events.RoundStarted`）/ 回合结束（`RoundEnded` 按原因播不同音）/ 炸弹蜂鸣
    （按 `CsConst.BombBeepInterval*` 计时）；
  - 音量：订阅设置变化 → `Game.Sound.SetVolume(SoundGroup.SFX, v)`；
- **高频防护**：脚步/枪声要有并发上限（比如同时最多 8 个枪声），超了丢弃 + 首次 Warn。

### 4.3 `Assets/Editor/Views/ArtSetup.cs`
- 把下载到的模型/音效**规整到** `Assets/Resources/Art/**` 与 `Assets/Resources/Sound/{SFX,BGM}/**`；
- 生成 `Assets/Resources/UI/WorldNameplate.prefab`；
- 菜单 `Clover/CS16/整理视图与音效资源`；
- 资源命名规范（写死在代码里，替换 = 换文件）：
  ```
  Resources/Art/T/{player,viewmodel_ak47,...}.prefab
  Resources/Art/CT/{player,viewmodel_m4a1,...}.prefab
  Resources/Sound/SFX/{ak47_fire,glock18_fire,reload,hit,headshot,death,step,bomb_beep,round_start,round_win,round_lose}
  ```

### 4.4 `client/资源欠缺清单.md`
按 `patterns/client/resource.md` 模板，逐行登记占位资源：资源名 / 类型 / 尺寸 / 当前占位 / 用户需提供 / 替换方法 / 影响文件 / 是否阻塞可玩。

## 5. 验收标准

- [ ] 进图后能看到**其他角色模型**（不是胶囊堆）与**自己的枪**
- [ ] 模型上 4 个 `CsHitboxProxy` 都在（用 `eval_file` 打印数量断言，每个角色 4 个）
- [ ] 头顶名字/血条可见，且**打中敌人时血条下降**
- [ ] 开枪有枪声、跑动有脚步声、命中/爆头/死亡有音效；`Game.Sound.PlaySFX` 的路径全部能在 `Resources/Sound/` 找到（否则 `Select-String` 逐个核对）
- [ ] 占位物在模型加载成功后**立即移除**（不许出现一堆胶囊/橙色球）
- [ ] 素材来源逐条记入 `client/资源欠缺清单.md`（含 URL 一行）
- [ ] 与 CS 1.6 对照：角色轮廓、枪械外形、枪声听感 —— 写进回报（"像/不像"）

## 6. 约束

- **不许停工等素材**：先接好逻辑与 API，再换资源。
- 每个非预期分支（资源加载失败 / 模型缺失 / 音效缺失）必须 `Game.Logger.Warn/Error("View"|"Audio", ...)`，
  且**降频**（同一资源只报一次）。
- 完成后回报：已完成 / 未完成 / 下一棒从哪个文件接着做 + **素材来源清单** + 未决问题。
