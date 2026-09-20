# agent-26：UI 通用控件工厂下沉（引擎 `UIFactory`）

> **跨仓库片**：引擎 `clover-client-unity-engine` ＋ 项目 `clover-project-cs16`
> 本片是「通用能力下沉」的第 2 片（25 = 日志，已完成；26 = UI 控件；27 = 音效闸门，未派）。
> 用户已明确：**本机没开 Unity，不要求跑编辑器/PlayMode**；但**离线编译必须跑**。

## 0. 先读（别自己发明规则）

- 引擎 `结构规则.md` §2.1（各模块只引用 `Core`；模块间不互相引用）、§3.5 Presentation 允许/禁止、§4.4 复用规则（**已有同类能力不准再起第二套**）、§6 文档规则（不同步 = 未完成）。
- 引擎 `clover-client-unity-engine-index.md` §3.2 表现域（**UI 那一行要同步改**）。
- 上一片（agent-25）已确立的**下沉注释版式**：`Runtime/Core/LogThrottle.cs:1-26`（出处 / 没下沉什么 / 为什么下沉 / 语义约束）。本片新代码的顶部注释照此版式。

## 1. 现状（已核实，别重查）

引擎 `Runtime/Presentation/UIWidgets.cs:31` 的 `UIFactory` **只有**：
`DefaultFont()` / `CreateNode` / `Stretch` / `CreateCentered` / `CreatePanel` / `CreateText` / `CreateButton` / `UICamera`。

项目里**三处各自重造**同类控件（这是 §4.4 最典型的收敛信号）：

| 位置 | 内容 |
|---|---|
| `client/Assets/Scripts/UI/Flow/CsUiStyle.cs` | 布局助手 `StretchRoot/AnchoredTopLeft/AnchoredBottom/CreateBottomLabel/CreateFullScreen/CreateBoxRect/CreateLabel/CreateButton` ＋ **控件工厂** `CreateSelector`(:161) / `SetBarWidth`(:182) / `CreateSlider`(:200) / `CreateInputField`(:264) / `CreateToggleRow`(:336)，且自带 `Selector` / `ToggleRow` **两个类型** |
| `client/Assets/Scripts/UI/InGame/CsHudTheme.cs:405-521` | 又自建了一遍 `Slider` / `InputField` 等（`:473` 注释直言 `CsUiStyle.CreateInputField` 编译不过 ⇒ **两者互指对方有坑**） |
| `client/Assets/Scripts/UI/InGame/ConsolePanel.cs:81` | 第三遍 |

且 `CsUiStyle.SetBarWidth(:182)` 与引擎 `UIWidgets.cs:1159` 的 `WorldHpBar` 用的是**同一个"锚点宽度表达比例"口径**（同一个坑）。

## 2. 要做的（三件）

### 2.1 引擎 `UIFactory` 新增通用控件工厂

把上表里**与 CS 无关的构造件**搬进 `Runtime/Presentation/UIWidgets.cs` 的 `UIFactory`，并补进引擎（⛔ **配色/文案/尺寸一律参数化**，不许把任何 CS 取值写死进引擎）：

1. 布局助手（若引擎没有等价物）：`AnchoredTopLeft` / `AnchoredBottom` / `CreateBottomLabel` / `CreateFullScreen` / `CreateBoxRect` / `CreateLabel` / **`SetBarWidth(RectTransform fill, float progress01)`**；
   - ⚠️ `UIFactory.Stretch` 已存在，项目 `CsUiStyle.StretchRoot` 若与它等价 ⇒ **合并**（项目侧转发到引擎那个），⛔ 不许留两份；
2. 控件工厂：`CreateSlider` / `CreateInputField` / `CreateSelector` / `CreateToggleRow`（签名照项目现版搬，但**颜色/字体/回调/初值全部由参数传入**）；
3. 随之把 `Selector` / `ToggleRow` **两个类型**搬进引擎（`Presentation` 程序集，可与 `UIFactory` 同文件或同程序集新文件；⛔ 不许放进 `Core`，它们依赖 UnityEngine.UI）。

**搬移铁律**：⛔ 布局参数（pos / size / fontSize / 锚点 / 层级顺序）**逐字照搬**，不许"顺手优化"——本片只换归属，**不改任何视觉结果**。原代码里那些**实测坑的注释**（例如 `CsUiStyle.cs:267` 关于 `DefaultControls.CreateInputField`、`:200-254` 手搭 `Slider` 的原因、`CsHudTheme.cs:473` 说对方编译不过）**必须一并搬进引擎注释**，不许丢。

### 2.2 项目侧改为"薄包装 / 直接调引擎"

- `CsUiStyle.cs`：**保留**全部配色常量（那是 CS 业务）与项目特有的封装；构造实现改为**转发**引擎 `UIFactory`（不再自持一份）。`Selector` / `ToggleRow` 类型若已搬进引擎，项目侧改成 `using` 引擎类型（⛔ 不许留两份同名类型）。
- `CsHudTheme.cs:405-521` 的自建控件：改调引擎（**第三套必须消失**）。
- `ConsolePanel.cs:81` 同。
- 顺带**查清并记录** `CsHudTheme.cs:473` 说的"`CsUiStyle.CreateInputField` 编译不过"到底是什么问题——若在搬移中自然消失，写进回报；若仍存在，必须修掉并把原因写进注释（⛔ 不许把已知坑原样搬进引擎而不留说明）。

### 2.3 判据（自己跑，原始输出贴回报）

- 引擎侧：`Select-String UIWidgets.cs -Pattern 'public static '` 列出新增方法；
- 项目侧：`CsUiStyle.cs` 里**已无**自建 `Slider`/`InputField`/`Selector`/`ToggleRow` 的构造实现（只剩转发/配色）；`CsHudTheme.cs:405-521` 段内的自建构造已改调引擎；
- **同名类型只剩一份**：`Select-String -Path (全项目 + 引擎) -Pattern 'class Selector|class ToggleRow'` ⇒ 各 1 命中（在引擎）；
- **视觉零改动**：搬移前后把关键 `pos/size/fontSize` 字面量列表打出来对比（回报里贴前后对照，证明逐字未变）。

## 3. 离线编译（**必跑**，不需要 Unity）

- 引擎：`.codebuddy\doc-audit\tools\compile-check-client.ps1`（先读头部确认用法）⇒ 要 `RESULT: ALL PASS` / `GATE_EXITCODE=0`，且 `Presentation` 那一段 OK；⛔ `Core` 的 asmdef 引用列表必须**仍为空**（本片不该动 Core）。
- 项目：agent-25 用编辑器自带 Roslyn 按 `Cs16.asmdef` 边界编过一次（脚本已删）。你**自己重建**一个等价命令即可（读 agent-25 的回报格式或自己按 asmdef 参考集拼），要求 `EXITCODE=0`；也可带 `/define:UNITY_EDITOR` 再跑一次覆盖编辑器路径（注意参考集里**别同时放**单体 `Managed\UnityEditor.dll` 与模块化 `UnityEditor.CoreModule.dll`，会 CS0433 二义）。
- 回报贴**原始输出末尾 + 退出码**。⛔ 不许为了过闸门改脚本/asmdef。

## 4. 文档与注释同步（**必须一起做**）

1. `clover-client-unity-engine-index.md` **§3.2 表现域 UI 行**：补上新增的通用控件（`Slider` / `InputField` / `Selector` / `ToggleRow` / 布局助手 / `SetBarWidth`），并写清"业务面板仍需自行处理安全区"这条既有边界不动。
2. `结构规则.md`：§4.4 **必查能力清单**的 `UI` + `UIWidgets` 处补"通用控件工厂（含 Slider / InputField / Selector / ToggleRow / SetBarWidth）"；若 §3.5 有"允许放入"清单，同步一句话。
3. 引擎新代码顶部注释：出处写 `clover-project-cs16 的 client/Assets/Scripts/UI/Flow/CsUiStyle.cs`（＋ `CsHudTheme.cs` 段），并写明「**未下沉**的是配色与文案（业务）」。

## 5. skill 登记（只补**同一处**）

上一片已找到唯一落点：`clover-tools\ai-skill\reference\engine-mental-model.md` §4 能力表的 **`UI` / `UIWidgets` 行**（表头是"表现域能力边界（先查再写）"）。
- 把新增控件补进该行（或紧随其后一行），格式与相邻行一致（含 `E-` 编号占位：**留空写"E 号：待主 agent 统一编"**，⛔ 不许自行取号，避免与并行片撞号）；
- ⛔ 不许新建小节、不许改规则句、不许动 `client-modules.md` / `rules-full.md` 等其它清单（那些是"Launch 时构造的模块"清单，静态工厂不适用）。

## 6. 不许

- ⛔ 不许改任何 CS 配色常量与文案（那是业务）；⛔ 不许改布局参数（本片只换归属，不改视觉）；
- ⛔ 不许让模块之间互相引用 asmdef，不许让 `Core` 引用别人；⛔ 不许把 `UnityEngine.UI` 依赖的类型塞进 `Core`；
- ⛔ 不许改项目 `策划/**`、`docs/**`、`tools/**`、`.ai-tmp/test` 历史文件；⛔ 不许读别的 `clover-project-*` 工程；⛔ 不许开子 agent；⛔ 不许新增 README / 交接类 md；
- 临时脚本只放 `.ai-tmp/test/`，用完删。

## 7. 回报格式

```
产出物：<引擎侧 + 项目侧文件清单，绝对路径>
2.1 判据：UIFactory 新增方法签名清单 / Selector+ToggleRow 各仅 1 处定义
2.2 判据：CsUiStyle 自建实现残留命中数 / CsHudTheme 405-521 段改后摘要 / 编译不过那个坑的结论
视觉零改动：搬移前后 pos/size/fontSize 前后对照
编译：<两条命令 + 原始输出末尾 + 退出码>
文档：index §3.2 UI 行、结构规则 §4.4 的改动后原文
skill：改到哪一行、改成什么
未决：无 / <具体条目>
```
