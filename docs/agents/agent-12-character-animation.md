# agent-12：角色与第一人称武器**动画**（1:1 补齐）（clover-project-cs16）

> 项目根：`c:\Work\Server\full-dev\clover-project-cs16`
> **本片是本项目最大的交付缺口**：`client/资源欠缺清单.md` 第 13/14 项 ——
> 角色"**静态姿态（idle1 第 0 帧），没有播放任何动画**"、第一人称武器用"**程序化后坐**代替帧动画"。
> 按 skill §2 的 1:1 判定：**A 有动画 ⇒ 没有就是不可交付**。本片把 A 的动画搬过来。

## 0. 开工必做

1. `use_skill("clover-engine")`；**重点** §0.5（禁写条款：参考物已有的东西禁止重写，只能解析搬运）、§1.13（改动回路四拍）、§6（引擎能力表：动画走 `Game.Anim`）。
2. 引擎动画能力原文（**先读，别猜**）：
   - `clover-client-unity-engine/Runtime/Core/PresentationContracts.cs`（`IAnimPlayer` / `Anim.CreateAnimator` 的签名）
   - `clover-client-unity-engine/Runtime/Presentation/Animation.cs`（实现与限制）
   - `clover-client-unity-engine/Runtime/Presentation/Entity.cs:443` 附近（引擎自己怎么绑 Animator）
3. 数据层现状（**已经能取任意序列任意帧**，别重写）：
   - `原版资源/cs16src/cs16_build.py` → `class Mdl`：`_bones()` / `sequences()`（label/fps/numframes）/ `_anim_value()`（`mstudioanimvalue_t` run 解码）/ `pose_matrices(seq_label, frame)` / `triangles(seq_label, frame)`
   - 骨骼与顶点格式的**权威出处**：`原版资源/cs16src/hlsdk/**`（`studio.h` / `studio` 相关头）—— 顶点骨骼权重字段、`mstudioanim_t`、`mstudioseqdesc_t` 一律以它为准
4. 现有运行时/生成器（**扩展它们，别另起炉灶**）：
   - 运行时：`client/Assets/Scripts/Module/View/{ActorView,ViewModule,ViewModelRig,CsViewTuning}.cs`
   - 生成器：`client/Assets/Editor/Views/{ArtSetup,Cs16ModelData,NameplatePrefabBuilder}.cs`（现在生成的是 **MeshFilter + MeshRenderer 的静态分块 mesh**）
5. 离线编译入口：`.ai-tmp/test/compile-check.ps1`（本片的"编译通过"判据）

## 1. 第①拍（**只读取证，⛔ 这一拍不许写实现代码**）

产出一份**改动清单 + 技术路线**（写在回报里，不必新建文档），必须含：

1. **原版序列实测表**（对 `models/player/<skin>/*.mdl` 9 个 与 `models/v_*.mdl` 全部）：
   `模型 | 序列 label | fps | numframes | numbones`；
   至少要能对上：player 的 `idle1 / walk / run / crouch_idle / death1..3 / shoot…`、v_ 的 `idle / draw / shoot / reload…`（**以实测 label 为准，⛔ 不许照社区印象编**）。
2. **顶点骨骼权重格式**：从 `hlsdk` 头文件里取**确切结构**（字段名 + 偏移 + 权重语义），并说明 GoldSrc 是否单骨骼权重。
3. **技术路线二选一 + 理由 + 数据量估算**：
   - **路线 A：骨骼驱动**（Transform 层级 + `SkinnedMeshRenderer` + `AnimationClip` + `AnimatorController` + `Game.Anim`）—— 若模型有骨骼权重；
   - **路线 B：逐帧 mesh**（每帧一组 mesh，运行时切 `MeshFilter.sharedMesh`）—— 若确实是顶点动画且无权重可用。
   ⛔ 不许两条路都做；选定一条并给出"资产数量 × 体积"的估算。
4. **状态 → 序列映射表**（我方 actor 状态 ↔ 原版序列 label）：静止/走/跑/蹲/落地/死亡/开枪/换弹/切枪，逐一给出映射，写不出映射的标 BLOCKED。
5. **引擎契合性**：`Game.Anim` 能否满足（`Play/CrossFade/SetFloat/...`）；`AnimatorController` 只能用 Editor 脚本生成 ⇒ 说明生成器要产出哪些资产。

## 2. 第②拍：批量实现（按清单一次写完，别改一处编译一次）

1. **数据导出**：扩展 `原版资源/cs16src/cs16_build.py`（或在其同目录新增脚本），把选定路线的数据落盘到 `client/Assets/Editor/Views/ModelData/**`
   （格式自定，但要**可复跑**：一条命令从原版 mdl 重建）；
2. **Unity 生成器**：扩展 `client/Assets/Editor/Views/ArtSetup.cs`（或新增生成器），生成动画所需资产
   （`AnimationClip` / `AnimatorController` / 骨骼层级 / `SkinnedMeshRenderer` 的 prefab），保持**幂等**（重跑不炸）；
3. **运行时**：`ActorView` 按映射表在**状态变化时**切换序列（含朝向/位置以外的视觉状态）；`ViewModelRig` 用原版 v_ 序列替换现有"程序化后坐/换弹下沉/切枪抬起"
   —— ⛔ **不许把程序化动作留在里面当"补充"**，要么用原版序列，要么在回报里写明哪一段为何必须保留（并给出这个决定的原版依据）；
4. **帧率/时长**：一律取 mdl 的 `fps` 与 `numframes`；⛔ 不许自己调"看起来顺眼"的速度；
5. 每条非预期分支（序列缺失 / 资产缺失 / 模型无骨骼）**必须打 `Game.Logger.Warn/Error` 日志**。

## 3. 第③拍：一次编译 + 预演

- `powershell -NoProfile -ExecutionPolicy Bypass -File .ai-tmp\test\compile-check.ps1` ⇒ **`csc exit=0`**（贴原始输出）；
- 产出**预演报告**：逐项写清"已就绪、可实机采图"与"仍需编辑器（生成器未跑 / 资产未生成）"；
- ⛔ 编译失败 ⇒ 停下修，不许继续。

## 4. 第④拍：证据

- 本机 `unity status` 当前是**空的**（没有活编辑器）⇒ 表现类证据按 skill §1.12 第 2 条报 **`BLOCKED：需活编辑器 + 一次 Play`**，⛔ **不许编造"已实测播放"**；
- 数值类证据你要给出：**原版 mdl 实测的序列/fps/帧数** vs **生成器实际读到的值**（逐项对照表）；
- 数据导出的可复跑命令与输出原文。

## 5. 不许

- ⛔ 不许改 `策划/**`、`docs/**`、`tools/**`、任何 skill、`.ai-tmp/test` 里的历史文件；
- ⛔ 不许改契约成员签名（`ICsMatch` / `CsTypes` / `Events`）；需要新增成员 ⇒ 回报，由主 agent 定；
- ⛔ 不许自己编动画（序列名 / 时长 / 帧率 / 关键帧）；
- ⛔ 不许用通用兜底素材（KayKit 之类）；素材只能来自项目内 `原版资源/**`；工程内引用必须**复制**过去（§1.9 第 2 条）；
- ⛔ 不许读别的 `clover-project-*` 工程；⛔ 不许开子 agent；
- 一次性脚本只放 `.ai-tmp/test/`（自检宿主放 `.ai-tmp/hosts/`），用完删；不许产出交接/进度类 md。

## 6. 回报格式

```
产出物：<文件绝对路径清单>
第①拍结论：<序列实测表 + 路线 + 数据量估算 + 映射表>
自检：<compile-check 原始输出 + 数值类对照 + BLOCKED 清单>
未决：无 / <具体条目>
```
