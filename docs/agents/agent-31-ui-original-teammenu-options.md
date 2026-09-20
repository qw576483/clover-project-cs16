# agent-31：把「选阵营」「设置」按原版资源重做（+ 菜单配色/字体改回原版）

> 项目：`clover-project-cs16`。**纯项目侧**。
> 用户投诉：「你的选择阵营界面也不是原版啊！设置界面也不是啊！你可以比原版少，但是**你起码界面是原版的**啊！」
> 本片和 **agent-32（主菜单）** 并行；⚠️ 两片都会动 `CsUiStyle.cs` 的**配色/字体** ⇒ 谁先改谁留下，另一方**只追加不改写**（见 §4）。

## 0. 硬事实（只读调研已钉死，⛔ 别再重查）

- 现在三个界面的**坐标/字号/配色全部自创**：坐标写死在各自面板类的 `BuildLayout()`（`TeamSelectPanel.cs:20-25`、`OptionsPanel.cs:21-41`），色/字号来自 `CsUiStyle.cs`。**没有一个字段来自原版 `.res`。**
- 原版资源**就在项目里**（第 1 级载体）：
  - `原版资源/cs16src/cs16game/app/cstrike/resource/ui/teammenu.res`（选阵营，**完整**：框 + 6 按钮 + 坐标 + 文案 key + 命令）
  - `原版资源/cs16src/cs16game/app/valve/resource/optionssub{audio,video,mouse,keyboard,multiplayer,voice,advanced}.res`（设置 **7 个子页**）
  - `原版资源/cs16src/cs16game/app/cstrike/resource/clientscheme.res`（**配色 + 字体**，`:24-33` 是 Colors、`:225-265`/`:389-403` 是 Fonts）
  - `原版资源/cs16src/cs16game/app/cstrike/resource/cstrike_english.txt`（文案英文原文，`:79,84-89` 等）
  - `原版资源/cs16src/cs16game/app/valve/resource/gameui_english.txt`（`#GameUI_*` 文案）
- **原版配色**：正文/高亮/暗字全是橙 **`255 176 0 255`**（`cstrike/resource/clientscheme.res:24-33` 的 `BaseText/BrightBaseText/DimBaseText/ControlText`）。现工程用 `#E8A33D`（`CsUiStyle.cs:46`）+ 白字（`:50`）——**错**。
- **原版字体**：**没有字体文件**，全取自系统字体；`cstrike/resource/clientscheme.res` 的 `Fonts` 块声明 `Default` = **Verdana**（按 yres 分 12/13/14/20/24 档）、`Title` = **Verdana Bold tall 18**（回退 Arial 16）。现工程用引擎兜底 `LegacyRuntime/Arial`（`UIWidgets.cs:44-68`）——**错**。
- `.res` 是 **Valve KeyValues/VDF**：首行裸串是根名 + `{...}`；键值/嵌套块都用双引号；支持 `//` 注释；**键大小写混用**（`command`/`Command` 都有）⇒ 解析按 token 流式切、键不区分大小写；文件**无 BOM**。

## 1. 坐标系换算（**必须先定死，两片共用**）

`.res` 的 `xpos/ypos/wide/tall` 是 **VGUI 设计空间的绝对整数像素**（左上原点、y 向下、子控件相对父控件）。设计空间 = **640×480**（自洽性证据：`teammenu.res` 的 `xpos 76 + wide 552 = 628 ≤ 640`）。
项目画布 = **1920×1080**（`FlowSetup.cs:42-43`）+ `CanvasScaler match=0.5`（等比）。
⇒ **统一换算：`scale = 1080 / 480 = 2.25`**（VGUI 的 `GetScaledValue` 口径 = 按屏幕高 / 设计高）。

⚠️ **这是本片采用的换算口径**，载体里没有直接写着"设计空间=640×480" ⇒ 在回报与代码注释里**如实标注为"由数据自洽性推断 + 与实机图对照确认"**，不许说成"原版写明的"。

## 2. 要做的

### 2.1 选阵营（`client/Assets/Scripts/UI/Flow/TeamSelectPanel.cs`）—— 按 `teammenu.res` **逐字段**重做

逐字照搬下表（×2.25 换算后落到项目坐标）：

| 控件 | `.res` 值 | 换算后（×2.25） |
|---|---|---|
| Frame | `xpos 76, ypos 0, wide 552, tall 448` | `(171, 0, 1242, 1008)` |
| `joinTeam` 标题 | `(0, 22) 500×48`，`font "Title"`，`textAlignment west` | `(0, 49.5) 1125×108` |
| `terbutton` | `(0, 116) 148×20`，`west` | `(0, 261) 333×45` |
| `ctbutton` | `(0, 148) 148×20` | `(0, 333) 333×45` |
| `vipbutton` | `(0, 180) 148×20` | `(0, 405) 333×45` |
| `autobutton` | `(0, 212) 148×20` | `(0, 477) 333×45` |
| `specbutton` | `(0, 244) 148×20` | `(0, 549) 333×45` |
| `CancelButton` | `(0, 276) 148×20` | `(0, 621) 333×45` |
| `MapInfo` | `(168, 116) 316×286`，`ControlName HTML` | `(378, 261) 711×644` |

- **文案逐字**（`cstrike_english.txt`）：`#Cstrike_Join_Team` = `SELECT TEAM`、`#Cstrike_Terrorist_Forces` = `&1 TERRORIST FORCES`、`#Cstrike_CT_Forces` = `&2 CT FORCES`、`#Cstrike_VIP_Team` = `&3 VIP`、`#Cstrike_Team_AutoAssign` = `&5 AUTO ASSIGN`、`#Cstrike_Menu_Spectate` = `&6 SPECTATE`、`#Cstrike_Cancel` = `&0 CANCEL`（`&N` 是热键前缀，**照原样显示还是只留文字**？⇒ 按 VGUI 惯例 `&` 是热键标记、显示时**去掉 `&`**，把字母保留；这一条写进注释）。
- **命令语义**：`jointeam 1/2/3/5/6`、`vguicancel` ⇒ 映射到现有事件（`Events.TeamChosen` 等）+ VIP/自动分配/观察者；**VIP 在原版就是 `jointeam 3`**。
- **按钮形态**：原版是**纯文本左对齐按钮**（`textAlignment west`），**不是**现在这种"橙色大按钮 2×2 网格"。按钮文字色/底由 `clientscheme.res` 决定。
- 项目**现在多了**"随机"映射（`TeamSelectPanel.cs:93-99`）—— 原版对应的是 `AUTO ASSIGN`，按原版命名与命令改。
- 允许比原版少：**选兵种**（`classmenu_ct/ter.res`）本片可以不做（用户明说"可以比原版少"），但要在回报里写明"未做，原版有"。

### 2.2 设置（`client/Assets/Scripts/UI/Flow/OptionsPanel.cs`）—— 按 `optionssub*.res` 重做子页

- 页签按原版 **7 页**：`Audio / Video / Mouse / Keyboard / Multiplayer / Voice / Advanced`（现工程只有 5 页，缺 Voice/Advanced）。
- 每页控件**按对应 `.res` 的坐标/尺寸/控件类型**逐字搬（`scale=2.25`）：
  - `optionssubaudio.res`：`SFX Slider (40,57,160,36)`、`MP3 Volume (40,118,160,36)`、`Suit Slider (40,183,160,36)`、`Sound Quality ComboBox (256,64,180,24)`；label key `#GameUI_SoundEffectVolume / #GameUI_MP3Volume / #GameUI_HEVSuitVolume / #GameUI_SoundQuality`。
  - `optionssubvideo.res`：`Renderer (40,52,160,24)`、`Resolution (248,52,…)`、`AspectRatio (40,120,…)`、`ColorDepth (248,120,…)`、`Windowed CheckButton (33,160,165,24)`、`DetailTextures (241,160,…)`、`Brightness/Gamma CCvarSlider (40/248, 217, 160,50)`。
  - `optionssubmouse.res`：6 个 `CheckButton`（`xpos 36`、`wide 140`、y = 32/54/76/98/120/142）+ 右侧 `wide 300` 说明 Label（`xpos 184`）+ 底部 `Slider CCvarSlider (40,202,272,40)` 与 `TextEntry SensitivityLabel (320,202,48,24)`。
  - 其余 4 页同法（自己读，逐字照搬）。文案取 `#GameUI_*` 的英文原文。
  - ⚠️ **原视频页没有 FOV、没有"显示 FPS"** ⇒ 那两项是自创，**删掉或移到 Advanced 并标注为"本项目新增"**（二选一，回报里说明）。FOV 是玩家真实需要 ⇒ 建议放 `Advanced` 页并登记为允许差异（出处栏写"本项目新增"）。
- **外层 tab 条（页签列表 / tab 坐标）在载体里拿不到**（本包**缺** `OptionsDialog.res`，它在 GameUI 里）⇒ 按顺序「原版配色 + 原版字体 + 就近对齐」重建，并**逐条登记为"允许的差异（本项目新增：tab 条坐标无载体出处）"**。⛔ 不许声称有出处。

### 2.3 配色 / 字体改回原版（`client/Assets/Scripts/UI/Flow/CsUiStyle.cs`）

- 正文/强调色改 **`255 176 0`**（出处 `cstrike/resource/clientscheme.res:24-33` 的 `BaseText`/`ControlText`）；暗字用 `DimBaseText` 同值；按钮底 `ButtonBG "0 0 0 64"`（半透明黑）；滑块轨 `SliderTrackColor "31 31 31 255"`、`ButtonFocusBorder "64 48 0 255"`（都在 `clientscheme.res` 的 `Colors`/`Borders` 里，自己读并逐条引行号）。
- 字体：按 `clientscheme.res` 的 `Fonts` 块 —— `Default` = **Verdana**（项目取 `tall 16` 档）、`Title` = **Verdana Bold tall 18**。用 `Font.CreateDynamicFontFromOSFont("Verdana", n)`（**原版也是取系统字体**，没有字体文件）；取不到时回退现有兜底并 `Warn` 一次。
- **背景**：原版主菜单背景是 `cstrike/resource/game_menu.tga`、logo 是 `logo_game.tga` ⇒ 本片**只负责把它们复制进工程**（`client/Assets/Resources/UI/Art/`，`.tga` 直接可用），**具体怎么用归 agent-32**（主菜单片），避免两片抢同一处。
- ⛔ **不许改 HUD 相关配色**（`UI/InGame/CsHudTheme.cs` 和它的取值）—— HUD 已验收（H7 准星 `50 250 50` 等），改了会让 H1~H15 的证据作废。

### 2.4 收尾

- 改完**必须重跑** `Clover/CS16/生成流程场景与面板`（`Editor/Flow/FlowSetup.cs` 的 `Generate()`）重建 prefab（坐标写死在 `BuildLayout`，prefab 是快照）。
- 一次 Play 采联络图：主菜单 / 选阵营 / 设置（各 1 帧，1920×1080），放 `client/Assets/Screenshots/`（命名 `31_*.png`）。
- 验收表：若**已有**这三个界面的行 ⇒ 更新其证据列；若**没有** ⇒ 新增行（每行标 `表现类`）+ 同步汇总数字（`acceptance-table` 会查自洽）。

## 3. 判据（自己跑，原始输出贴回报）

1. **逐字段对照表**：`.res` 原值 → 换算值 → 代码里的实际值（三列，逐控件一行，⛔ 不许只给结论）；
2. `.res` 解析：把你写的解析器对 `teammenu.res` 跑一遍的输出贴出来（证明它能正确切 keyvalue/嵌套/大小写）；
3. 编译：项目侧 Roslyn（参考集用闸门最新 `csc-out\CloverEngine.*.dll`）⇒ `exit=0`；
4. **一次 Play** 的 3 张联络图 + 运行时节点树（界面上每个控件的实际坐标/字号，证明与换算值一致）；
5. `tools/verify.ps1` ⇒ FAIL=0（若某行证据受影响，只重采那几行并写明第几次重采）。

## 4. 与 agent-32 的边界

- 本片负责：**选阵营 + 设置 + `CsUiStyle` 配色/字体 + 把 `game_menu.tga`/`logo_game.tga` 复制进工程**。
- agent-32 负责：**主菜单**（按 `gamemenu.res` 的菜单项 + `gameui_english.txt` 文案 + 用上面那两张 tga 做背景/logo）。
- ⛔ 两片都不许**整行替换** `CsUiStyle.cs`（并行会互相覆盖）——只许改自己那几行常量，改完回报里贴出你改的行的原文。

## 5. 不许

- ⛔ 不许改引擎 `clover-client-unity-engine/**`；⛔ 不许改 `UI/InGame/**`（HUD）；
- ⛔ 不许改 `策划/**`（除 §2.4 的验收表行）、`docs/**`、`tools/**`、`.ai-tmp/test` 历史文件；
- ⛔ 不许用通用兜底素材；工程内素材必须从 `原版资源/**` **复制**（⛔ 不许让 Unity 引用 `原版资源/` 路径）；
- ⛔ 不许读别的 `clover-project-*` 工程；⛔ 不许开子 agent；⛔ 不许新增 README/交接类 md；临时脚本只放 `.ai-tmp/test/` 用完删。

## 6. 回报格式

```
产出物：<文件清单>
2.1：<选阵营逐字段对照表 + 改后原文要点>
2.2：<设置 7 页的逐字段对照表；tab 条"无出处"的登记原文>
2.3：<配色/字体改动的行 + 出处行号；素材复制后的路径与尺寸>
坐标换算：<scale=2.25 的依据与自洽性验算>
解析器输出：<原始输出>
编译：<命令 + 末尾 + 退出码>
Play：<3 张图名 + 运行时控件坐标/A 字号原文>
闸门：<verify.ps1 逐行>
未决：无 / <具体条目>
```
