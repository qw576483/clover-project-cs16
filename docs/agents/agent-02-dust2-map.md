# agent-02：de_dust2 地图（几何 + 场景 + 烘焙 + 本地碰撞）

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
- `docs/步骤文档.md`（§3 契约）
- `策划/策划案/CS1.6单机参考规格.md` §3（de_dust2 规格：地标 + 连通）
- `clover-ai-skill/reference/engine-mental-model.md` §6（素材量测优先 / FBX 不带 Collider 的坑）
- `clover-ai-skill/patterns/client/3d-mmo-basics.md` §2（**客户端本地碰撞必须与服务端同一套规则**）
- **引擎地图管线源码**：`clover-client-unity-engine/Editor/MapBake/{MapBaker.cs,MapBakeOptions.cs,MapBakeWindow.cs}`
  与 `clover-server-engine/pkg/domain/mmo/mapdata/README.md`（CloverMap 格式契约）
- 契约文件：`Assets/Scripts/Core/*.cs`、`Assets/Scripts/Module/Map/ICsMap.cs`（含 `CsMarkers`）

## 1. 目标

1. 用 Editor 脚本**按布局数据批量生成 de_dust2 几何**（地面/墙/箱子/斜坡），带 Collider，命名符合标记约定；
2. 生成并保存 `Assets/Scenes/StageDust2.unity`，加入 Build Settings；
3. 用引擎的 `Clover/地图烘焙`（`CloverEngine.Editor.MapBaker.Export`）导出**同源两份**：
   `Assets/MapData/de_dust2.bytes`（服务端/参考）+ `Assets/Resources/MapData/de_dust2.bytes`（客户端运行时读）；
4. 实现 `CsMapModule`（`ICsMap`）：加载 `.bytes`、`WalkableAt`、**本地碰撞解算**（半径采样 / 分轴滑墙 / 扫掠细分 / 台阶）。

## 2. 任务边界

**只做**（这些路径只有你能写）：
- `Assets/Editor/MapGen/**`
- `Assets/Scenes/StageDust2.unity`
- `Assets/MapData/**`、`Assets/Resources/MapData/**`
- `Assets/Scripts/Module/Map/CsMap.cs`、`CsMapModule.cs`
- `Assets/Editor/MapGen/MapBakeRunner.cs`（命令行烘焙入口，供主 agent 用 unity-cli 调用）

**绝不做**：
- **不许改** `Assets/Scripts/Module/Map/ICsMap.cs`（契约，含 `CsMarkers`）；需要新增 → 回报主 agent；
- 不许碰 `Assets/Scripts/Module/{Match,Player,CameraRig,Combat,Bot,View,Audio}/**`、`UI/**`、`App/**`、`Core/**`；
- 不许碰 `Assets/Scenes/Boot.unity` / `Menu.unity`（agent-01 的）；
- **不许开子 agent**。

## 3. de_dust2 布局规格（必须全部实现且连通）

坐标约定：**X 向东为正，Z 向北为正，Y 向上**；地图范围 **X ∈ [-40, 40]、Z ∈ [-40, 40]**（80m × 80m），墙高 4m，格子 1m。

| 区域 | 建议中心 (X,Z) | 建议尺寸 | 说明与连通 |
|---|---|---|---|
| `Spawn_T` | (-22, -30) | 16 × 12 | 恐怖分子出生点（地图南偏西）；→ Long A 南下口、→ Mid 南口、→ B Tunnels |
| `Spawn_CT` | (18, 30) | 16 × 12 | 反恐精英出生点（地图北偏东）；→ A Site、→ Mid 北口、→ B（经 Catwalk/Mid） |
| `Bombsite_A` | (24, 16) | 18 × 16 | A 点（东北）：有平台（高 1m）+ 4 个木箱掩体；← Long A、← Catwalk、← CT Spawn |
| `Bombsite_B` | (-26, 6) | 18 × 16 | B 点（西偏北）：有 3 个木箱掩体 + 一处矮墙；← B Tunnels、← Mid 西口 |
| Mid（中路） | (0, 0) | 10 × 56（Z -28→28） | 南北长廊，两端各接 T/CT Spawn；C 形拐角处加两扇"门"（可作掩体的半高墙） |
| Long A（A 大道） | (26, -12) | 10 × 26（Z -25→1） | 长直道，接 T Spawn ↔ A Site；中段有"长门"（可开的缺口） |
| Catwalk / Short A | (12, 14) | 7 × 14（Z 7→21） | 连接 Mid 上段 ↔ A Site |
| B Tunnels | (-18, -16) | 9 × 22（Z -27→-5） | 隧道，接 T Spawn ↔ B Site；内部有一个拐角 |

**标记对象命名（**必须严格用 `CsMarkers` 里的字符串**，否则 AI/买枪区/包点全部失效）**：
```
Spawn_T / Spawn_CT                      ← 出生点（空 GameObject，放 5 个以上，分散开）
Bombsite_A / Bombsite_B                 ← 包点中心（空 GameObject，半径取 CsMarkers.BombsiteRadius）
BuyZone_T / BuyZone_CT                  ← 买枪区中心（空 GameObject，覆盖出生点附近）
Route_T_To_A / Route_T_To_B / Route_T_Mid        ← T 进攻锚点（3~6 个/条）
Route_CT_To_A / Route_CT_To_B / Route_CT_Mid     ← CT 防守锚点（3~6 个/条）
Route_Patrol                            ← 通用巡逻散点（8~12 个，覆盖全图主要通道）
```
> 这些标记点会被 `ICsMap.Points(marker)` 读到，**agent-03/05 完全依赖它们** —— 少了点或名字写错 = 机器人不动，必须自查（生成后打印每个标记的点数）。

**视觉要求（"像 dust2"是硬要求）**：
- 沙色墙（`#C9A66B` 系）+ 地面沙土（`#BFA26B`）+ 木箱（棕 `#8B5A2B`，1m 立方）；
- A/B 点地面用不同色块 + 文字标识（用 `TextMesh` 或贴图）；
- 天空盒：沙漠色（淡黄）；方向光带暖色 + 阴影；
- **不要出现纯白/纯灰的"白盒"观感** —— 用材质球区分墙/地/箱。

## 4. 产出物要求

1. `Assets/Editor/MapGen/Dust2Layout.cs` —— **布局数据**（房间/走廊/箱子的矩形列表，纯数据；扩展/调整只改这里）；
2. `Assets/Editor/MapGen/Dust2Builder.cs` —— 按数据批量生成：
   - 地面（`Box` 拉伸 + `BoxCollider`）、墙（`BoxCollider`）、箱子、斜坡；
   - 每个几何体挂到父节点 `Level` 下，材质分三类；
   - 生成 `Spawn_*` / `Bombsite_*` / `BuyZone_*` / `Route_*` 空对象（**位置合理：包点在开阔处、买枪区在出生点内、路线点在通道中**）；
   - 静态批处理标记（`StaticBatchingUtility` 或 `isStatic`）；
   - 菜单 `Clover/CS16/生成 de_dust2 场景`；
3. `Assets/Editor/MapGen/MapBakeRunner.cs` —— 静态方法 `public static void BakeDust2()`：调用 `CloverEngine.Editor.MapBaker.Export`（**先读源码确认签名**），产出两份 `.bytes`（路径见 §1）；
4. `Assets/Scripts/Module/Map/CsMap.cs` + `CsMapModule.cs`：
   - `CsMapModule : MonoBehaviour`，暴露 `public ICsMap Map { get; }`；在 `Start()` 里自动 `LoadAsync(CsConst.MapDataResourcePath, ...)`；失败必须 `Game.Logger.Error` 并给出 `Status`；
   - `WalkableAt` → 转发 `Game.Map.WalkableAt`；
   - `CanStand(pos, r)`：中心 + 8 向（半径 r）全可走才算站得下；
   - `ResolveMove(from, to, r)`：**分轴解算（先 X 后 Z）+ 扫掠细分（每段 ≤0.25m）+ 台阶（≤`CsConst.StepUpHeight` 直接迈上）**；撞墙时贴墙滑行；
     **额外加解除卡死规则**：`if (!CanStand(from, r)) return WalkableAt(to) ? to : from;`
   - `SampleGround`、`GetSpawnPoint(index)`（从 `Spawn_T`+`Spawn_CT` 标记汇总）、`Points(marker)`、`TryGetPoint(marker, out p)`（随机取一个）；
   - 未加载时的行为必须打一次 Warn（不是静默返回 true）。

## 5. 验收标准

- [ ] 生成后 `Assets/Scenes/StageDust2.unity` 打开有内容（不是空场景）、物体都有 Collider
- [ ] 烘焙日志出现「计为障碍=N」且 **N > 0**（= 0 说明物体没 Collider，是典型静默失败）
- [ ] `Assets/Resources/MapData/de_dust2.bytes` 存在；运行时 `Game.Map.Loaded == true` 且 `BlockedCount > 0`
- [ ] **连通性自证**：写一个 Editor 探针（`Assets/Editor/MapGen/MapConnectivityProbe.cs`），
      从 `Spawn_T` 到 `Bombsite_A`、`Bombsite_B`、`Spawn_CT` 各做一次网格 BFS（用 `WalkableAt`），
      断言**全部可达**，并打印路径长度；不可达必须打 Error 并指出断开处
- [ ] 每个标记名的点数打印出来（`Spawn_T≥5`、`Bombsite_A≥1`、`Bombsite_B≥1`、`BuyZone_*≥1`、每条 `Route_*≥3`、`Route_Patrol≥8`）
- [ ] 与 de_dust2 参考图对照：地标位置关系（A 东北 / B 西 / T 南 / CT 北 / Mid 居中）**一致**，截图存档

## 6. 约束

- **必须先读 `MapBaker` 源码确认导出方式与产物路径**，不许猜；
- 所有非预期分支必须打日志（加载失败/标记缺失/可达性失败）。
- 完成后回报：已完成 / 未完成 / 下一棒从哪个文件接着做 + 产出物路径 + 未决问题。
