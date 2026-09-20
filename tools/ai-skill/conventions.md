# 本项目约定（clover-project-cs16）

## 形态（硬）

**单机**（`策划/策划案/CS1.6单机参考规格.md` 顶部已判定，只判一次、全程照做）：

- 没有 `server/`、没有 Go 代码、**不调 `CloverNet.Init`**、不探 etcd/redis/mysql、不跑 `env.exe`；
- `Game.Net == null` 是**正常状态**，业务代码一律不许碰 `Game.Net.*`；
- Find Servers 面板扫不到主机 = **真实行为**，不是降级（见验收表差异 #1）。

## 数值口径（硬）

原版有**两套口径**，一律按"**玩家实际体验**"取**随包 `server.cfg`**：

| 口径 | 出处 | 用途 |
| --- | --- | --- |
| 随包 `server.cfg`（**采信**） | `原版资源/cs16src/cs16game/app/cstrike/server.cfg` | 我方所有规则数值的取值依据 |
| `mp.dll` 出厂默认（**登记、不采信**） | `mp.dll:偏移`（见 `原版资源/解包产物/原版数值表.md` §1） | 只作为第二口径写进 `策划/对照表.md` |

- ⛔ `原版资源/cs-docker/files/server.cfg` 是**社区 docker 配置**，不算原版，不许采信；
- **任何常量 / 坐标 / 时长 / 颜色，写不出出处（`文件:行` 或 `文件:偏移`）不许进代码**（全局 §0.5）。

## 命名

- 命名空间：`Cs16.Core` / `Cs16.Module.<Xxx>` / `Cs16.UI` / `Cs16.App`；
- 面板 `XxxPanel`（预制体 `Assets/Resources/UI/{类名}.prefab`，**由 Editor 脚本生成，⛔ 不许手写 .prefab YAML**）；
- 模块 `XxxModule : MonoBehaviour`（门面接口 `IXxxModule` / `ICsXxx`）；
- 数值表 `CsConst` / `CsWeapons`（**本项目的"配表"形态**，见 `registry.md`）；
- 场景名一律走 `SceneNames`，事件名一律走 `Events` —— ⛔ 不许散落字符串字面量。

## 目录边界

| 目录 | 归谁 | 说明 |
| --- | --- | --- |
| `client/Assets/Scripts/Core/**` | **契约层** | `CsConst` / `CsWeapons` / `CsEnums` / `Events` / `CsHudSnapshot` / `CsMatchConfig`；改它要主 agent 统一裁决 |
| `client/Assets/Scripts/Module/**` | 业务模块 | 模块之间只经接口协作（App 注入），⛔ 禁止 `FindObjectOfType` / `GameObject.Find` / 单例互找 |
| `client/Assets/Scripts/UI/**` | 界面 | ⛔ **不许 `using Cs16.Module.*`**（只发/收事件 + `Game.UI.Open<T>`） |
| `client/Assets/Scripts/App/**` | 启动装配 | `Bootstrap` 落 `Game.Launch` 与场景常驻对象 |
| `client/Assets/Editor/**` | 生成器 | 场景 / 预制体 / 动画资产 / 地图烘焙的唯一产出路径 |
| `原版资源/**` | **原版载体（只读）** | 解包产物与导出脚本；⛔ 不许让 Unity 直接引用它，工程内素材必须**复制**过去 |
| `策划/**` | 规格 / 验收 / 对照 | 主 agent 的判定标准产地 |
| `.ai-tmp/test/**` | 一次性脚本 | 用完即删；⛔ 不许放项目根 / `client/_dev/` / `tools/` / `Assets/` |

## 与全局 skill 的差异（**只许记"加严"，不许记"放宽"**）

| 项 | 全局 skill 写法 | 本项目写法 | 性质 | 原因 |
| --- | --- | --- | --- | --- |
| 游戏形态 | 默认先判定形态 | **已判定为单机**，禁网络相关一切动作 | 加严 | 用户指定单机 |
| 数据 | "数据一律走配表（xlsx → tsv + 代码）" | 本项目**无服务端打表链路**，数值表 = `Core/CsConst.cs` + `Core/CsWeapons.cs`（手工维护，逐条带原版出处注释） | 加严 | 单机客户端项目，无 shared 表消费方 |
| 地图 | 无 | 地图唯一 `de_dust2`，几何**从原版 BSP 解析**后由 `Editor/MapGen` 生成（⛔ 不许手工摆坐标） | 加严 | §0.5 禁写条款 |
| 动画 | 走 `Game.Anim` | 角色/视模型动画**只能来自原版 mdl 序列**（`原版资源/cs16src/cs16_anim.py` → `.cs16anim` → `Editor/Views/AnimSetup.cs`），⛔ 不许做程序化替代动作 | 加严 | §0.5 禁写条款 |
