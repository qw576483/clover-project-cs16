# shimphysics —— 代码级「卡死」复现宿主

**跑真 `CsMap.cs`，把物理层换成解析几何。** 与 `tools/probes/phys-stuck-probe.py`（机制级：
探针自己复刻判据）配对使用 —— 本宿主证明的是**产品代码本身**在这种几何里一步都走不动。

## 跑

```
dotnet run --project tools/probes/hosts/shimphysics/ShimPhysics.csproj
```

判据行（末行）：`RESULT-SHIMSTUCK: PASS|FAIL`，exit `0` = PASS。

## 里外分别是什么（改之前先看这张表）

| 部分 | 真 / 假 | 说明 |
|---|---|---|
| `client/Assets/Scripts/Module/Map/CsMap.cs` | **真** | 逐字编译，一行不改、不生成副本 |
| `client/Assets/Scripts/Module/Map/ICsMap.cs` | **真** | 同上 |
| `client/Assets/Scripts/Core/CsConst.cs` | **真** | 判据用到的常量（半径/身高/台阶/贴地）都取自它 |
| `client/Assets/Scripts/Core/CsWeapons.cs`、`CsEnums.cs`、`CsRng.cs` | **真** | `CsConst` 的编译闭包 |
| `Runtime/Core/Rng.cs`、`LogThrottle.cs`（引擎） | **真** | `CsRng` 的编译闭包 |
| `Shim/UnityShim.cs` | 假 | `UnityEngine` 的 `Vector3`/`Mathf`/`LayerMask`/`RaycastHit`/`Vector2Int`/`Time` 等最小替身 |
| `Shim/PhysicsShim.cs` | 假 | `UnityEngine.Physics.Raycast` / `CheckCapsule` → `ShimWorld` |
| `Shim/ShimWorld.cs` | 假 | 解析几何世界：AABB + 地面平面 + 单层 2D 位图 |
| `Shim/EngineStub.cs` | 假 | `CloverEngine.Game`（`Map` 可注入）+ `IMapData` 收集器 |

**为什么要假一层 `UnityEngine`**：`UnityEngine.Physics.*` 在 Unity 里是 native extern ——
编得过、编辑器外一调用就抛。所以本宿主**不引用任何 Unity 程序集**，由 `Shim/` 提供类型与实现；
这也是本目录不 import 上级 `hosts/Directory.Build.props`（那条 `CloverCheckUnityEditorRoot` 只服务
引用 Unity DLL 的 compilecheck 族）的原因。

## 世界与判据

世界与 `phys-stuck-probe.py` **同参数**：两堵墙之间 0.50 m 的缝、无限地面 `y=0`、格 1.00 m；
身体直径 `2 × PlayerRadius = 0.72 m > 0.50 m`。

- 每帧给**满额** wish（`CsMap.MaxSweepStep` = 0.25 m），连续 8 帧；
- **STUCK** ⇔ 8 帧累计位移 < 1e-4 m 且每帧 wish ≥ 0.24 m；
- **MOVING** ⇔ 对照组（同方向、开阔地 x=5）累计位移 ≥ 8 × 0.24 m。

## 已知边界（别把结论读宽）

- 世界是**解析几何**（两个 AABB + 平面），不是 de_dust2 的真几何 ⇒ 结论范围 = 「这套闸门在这类
  缝里必然一步不动」，不是「de_dust2 的某一格卡死」。后者要换世界（把 `de_dust2` 的碰撞网格喂进来）。
- shim 的 `Raycast` 实现了 `queriesHitBackfaces = false` 的语义（起点在盒内 ⇒ 该盒不给交点），
  与 `CsMap.BodyHeightClearAt` 的注释同口径；`CheckCapsule` 按竖直胶囊（CsMap 的两处调用都是）。
- 位图由同一份解析几何生成（同一份空间事实一块数据），因此**不复现**实机上「位图 vs 真几何
  两份载体不一致」那一类差异（那类见 `差异登记.tsv` #64 / #76）。
