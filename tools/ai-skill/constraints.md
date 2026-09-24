# 约束与风险（clover-project-cs16）

## 引擎 / 平台固有约束（改不掉的，别硬碰）

| 约束 | 现象 | 结论 |
| --- | --- | --- |
| 引擎**没有第一人称相机** | `Game.Camera.Follow` 是锁 Z 的简单跟随 | FPS 相机业务自写（`Module/CameraRig/FirstPersonCamera.cs`） |
| 引擎**没有射线 API** | — | 命中判定用 Unity `Physics.Raycast`（业务自写） |
| `GameKey` 枚举**没有 `BackQuote`** | 控制台没法用 `~` 唤出 | 现用 `/` 或 `Num0`（验收表差异 #3；引擎补枚举后改回） |
| `AnimatorController` 只能 Editor 脚本生成 | 运行期建不出控制器 | 动画资产一律由 `Editor/Views/AnimSetup.cs` 产出并写进预制体 |
| 预制体**只能由 Editor 脚本生成** | ⛔ 不许手写 `.prefab` YAML | 改布局/坐标 = 改生成器 + **重跑生成器** |
| `Game.Anim.CreateAnimator` 的 `OnComplete` | **循环状态每次过 `normalizedTime≥1` 也会回调** | 只在有一次性动作（`_overrideState` 非空）时才响应 |
| `Game.Pool` 管"同一对象复用" | 不管"预制体 → 场景实例" | 角色/viewmodel 每次进图 `Instantiate` 是**合规的**（验收表差异 #9） |
| 引擎 `Game.LanBrowser` 单机扫不到主机 | 列表为空 | **真实行为**，不是降级（差异 #1） |

## 静默失败风险（不报错但结果错）

| 风险 | 现象 | 结论 |
| --- | --- | --- |
| **改了 UI 布局但没重跑生成器** | 运行期用序列化预制体里的旧坐标 ⇒ **只改代码看不到变化** | 改 `HudPanel` / `UiBuilder` 后**必须重跑 `Clover/CS16/生成游戏内面板`**（或走运行期重放兜底 `ApplyScoreBlock()`） |
| `CsPanelBase.Awake` 见有子节点就跳过 `BuildLayout` | 同上 | 同上 |
| 生成器**没跑过** | prefab / `.anim` / `.controller` 不存在 ⇒ 动画 `_anim == null`、模型不显示 | 首次拿工程必须先跑 4 个生成器（见 `registry.md`） |
| 离线编译通过 ≠ 运行正确 | `compile-check.ps1` 只验"签名存在 + 编译过" | 运行时行为必须进 Play 验（或实机截图） |
| `Resources` 路径大小写敏感 | 路径写错 = 资源静默缺失（只 Warn 一次） | 路径一律走常数（`CsViewTuning` / `CsHudTheme` / `CsUiStyle`），⛔ 不许散落字符串 |
| 截图证据可能比代码旧 | 旧的"已实测"截图为废证据 | 要查时看一眼图与被验源码的 mtime（`tools/verify.ps1` 的 `evidence-freshness` 是可选脚本，⛔ 不必跑） |
| `.ps1` 含 CJK 且无 BOM | PS 5.1 按 ANSI 解析 ⇒ **中文匹配静默失效**（脚本照跑、逻辑全错） | `.ai-tmp/**/*.ps1` 一律 ASCII-only 或带 BOM；`tools/verify.ps1` 第 14 条会查 |
| **对账检查把生成物当手写实现** | 生成器产出（`Resources/**` 的 `prefab/anim/controller/asset/mat/unity/png/wav/bytes` 与每个 `.meta`）被逐文件判"没派活留痕" ⇒ **近 2000 条假阳性**，真违规被淹没 | `tools/verify.ps1` 第 15 条的对账**只统计人写的文本源码**（`Scripts`/`Editor`/`Resources` 三棵树都只取 `.cs`），生成物扩展名一律排除；`Scripts`/`Editor` 下的 `.cs` 必须继续逐文件对账（skill §1.11 第 11 条：**会误报的检查比没有检查更糟**） |

## 未决问题

| 问题 | 待谁定 | 时间 |
| --- | --- | --- |
| HUD 血量/护甲/金钱/弹药那排的**绝对坐标**（写死在 `client.dll`，反汇编未定位） | 需一张**第一人称 + HUD 打开**的原版截图（人给）或继续逆向 | 2026-09-19 |
| **雷达尺寸**落 1080p 真值（同一张截图即可解） | 同上 | 2026-09-19 |
| 9-blend 瞄准序列的 2 维插值（现只取正中一路） | 主 agent 排期 | 2026-09-19 |
| 秒表图标内圈与原版截图不完全一致（两个载体不是同一版贴图） | 人裁决（以本体为准？） | 2026-09-19 |
| 规格文件缺 §2.5「通用交付项 X1~X8」明细（`docs/步骤文档.md` §1 引用了它） | 规格作者（人 / 主 agent） | 2026-09-19 |
| `.ai-tmp/test/compile-check.ps1` 是长期要用的离线编译入口，位置属"临时目录" | 主 agent（建议迁 `tools/`） | 2026-09-19 |
