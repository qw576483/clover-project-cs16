# agent-32：主菜单按原版资源重做

> 项目：`clover-project-cs16`。**纯项目侧**。
> 用户投诉：「你的**菜单界面**也不是原版cs啊！……你可以比原版少，但是**你起码界面是原版的**啊！」
> 前一片（agent-31）已把**选阵营 / 设置**按 `teammenu.res` / `optionssub*.res` 逐字段重做并验收通过；本片只做**主菜单**。

## 0. 硬事实（前片只读调研已钉死，⛔ 别再重查）

- 现在主菜单（`client/Assets/Scripts/UI/Flow/MainMenuPanel.cs`）的**坐标/字号/配色全部自创**：按钮 `360×58`、`FirstButtonY=-210`、`ButtonStep=76`、`ButtonLeft=92`（`:17-23` 硬编码），底色 `#1B1B1B`、强调 `#E8A33D`（`CsUiStyle.cs`）。**没有一个字段来自原版。**
- **原版主菜单的布局拿不到**（它在 `hw.dll` 的 GameUI 里程序化生成）。能拿到的是这四样（**都在项目里**）：
  1. **菜单项清单 + 命令**：`原版资源/cs16src/cs16game/app/cstrike/resource/gamemenu.res` —— 局外可见的是第 `10 NewGame` / `11 FindServers` / `12 Options` / `13 Quit` 四项（`3/4/5` 是 `OnlyInGame=1` 的局内项，单机版不做）。**全文**（单行）：`"GameMenu" { "1" { "label" "Join random server" "command" "engine Connect random.gametracker.rs:27015" } … "10" { "label" "#GameUI_GameMenu_NewGame" "command" "OpenCreateMultiplayerGameDialog" } "11" { "label" "#GameUI_GameMenu_FindServers" "command" "OpenServerBrowser" } "12" { "label" "#GameUI_GameMenu_Options" "command" "OpenOptionsDialog" } "13" { "label" "#GameUI_GameMenu_Quit" "command" "Quit" } }`
  2. **文案英文原文**：`原版资源/cs16src/cs16game/app/valve/resource/gameui_english.txt` —— `GameUI_GameMenu_NewGame` = `New Game`（`:110`）、`FindServers` = `Find Servers`（`:115`）、`Options` = `Options`（`:118`）、`Quit` = `Quit`（`:119`）。
  3. **配色/字体**：`cstrike/resource/clientscheme.res`（正文橙 **`255 176 0`**，`:24-33`；`Fonts` 块 `Default`=Verdana 五档 12/13/14/**20**/24，`:225-265`；`Title`=Verdana Bold `tall 18`，`:389-403`）。**这些已由 agent-31 落进 `CsUiStyle.cs`（`ApplyOriginalFonts`/`ApplyTitleFont`/`OriginalFont`/`ResScale=2.25`）——直接用，⛔ 不许再改配色/字体的公共常量。**
  4. **素材（本片的主角）**：`client/Assets/Resources/UI/Art/game_menu.tga`（26 540 B，按钮贴图）、`game_menu_mouseover.tga`（26 540 B，**悬停态**）、`logo_game.tga`（65 580 B，CS logo）——**agent-31 已从原版复制进工程**。原件在 `原版资源/cs16src/cs16game/app/cstrike/resource/`。

## 1. 要做的

### 1.1 先用**贴图本身**定尺寸（这是本片唯一可靠的原版几何出处）

- 读 `game_menu.tga` / `game_menu_mouseover.tga` / `logo_game.tga` 的**图像头**（尺寸、位深、是否含 alpha）并把结论贴进回报。
- `game_menu.tga` 是**按钮条/菜单底纹**贴图：它的**像素宽度**就是原版按钮的宽度（在 800×600 设计空间下）⇒ 按 `CsUiStyle.ResScale`（=2.25，agent-31 已定：VGUI 设计空间 640×480 → 1080p）换算到 1920×1080。
- ⛔ 若 tga 里看不出"按钮高度/间距"（例如它只是整块底纹），**不许编**：改用"等分 / 居中排列"这类**可从贴图尺寸推出的**摆法，并把这一条**登记为允许的差异（本项目新增：按钮间距无载体出处）**。

### 1.2 主菜单按原版重做（改 `MainMenuPanel.cs`）

- **按钮集合 = 原版 `gamemenu.res` 的局外四项**：`New Game` / `Find Servers` / `Options` / `Quit`（文案取 `gameui_english.txt` 原文，⛔ 不许用中文，⛔ 不许自己加项）。
  - 命令语义映射：NewGame → `Events.LaunchMatch` 前的 NewGame 面板；FindServers → 现有 ServerListPanel；Options → OptionsPanel；Quit → 退出。
- **按钮外观 = 用 `game_menu.tga`（常态）+ `game_menu_mouseover.tga`（悬停）**：`Image` 组件 + `Sprite`（tga 直接可用）+ 文字叠加。⛔ 不许再用纯色 `Image` 画按钮。
- **logo = `logo_game.tga`** 放在主菜单上方（原版主菜单顶部就是这张 logo，**不是**文字标题）。
- **删除自创元素**：`COUNTER-STRIKE 1.6` 文字标题、副标题"单机版 · de_dust2 · 与机器人对战"、`Player: -`、版权行（逐条在回报里列明删了什么）。
  - ⛔ **例外**：底部 `by clover-engine` **必须保留**（skill §8 品牌硬约定：首页画面底部必须有，判据 = 实机截图/运行时节点树）。原版没有这一行 ⇒ 把它**登记为允许的差异（引擎品牌署名）**。
- **背景**：原版主菜单背景是引擎画的深色底（本包无独立背景资源；`resource/background/800_*.tga` 那几张是**加载画面**背景）⇒ 用 `clientscheme` 的深色 + 现有纯色底，**登记为允许的差异（背景无载体出处）**。⛔ 不许把加载画面背景挪来当主菜单底。
- 布局：以 §1.1 的贴图尺寸为准，整体**居中或靠左**照 VGUI 惯例（⛔ 无据不要凭空摆）。

### 1.3 收尾

- 改完**必须重跑** `Clover/CS16/生成流程场景与面板`（`Editor/Flow/FlowSetup.cs` 的 `Generate()`）重建 `MainMenuPanel.prefab`。
- 一次 Play 采主菜单帧（1920×1080）+ 必要时拼进联络图。
- 验收表：更新主菜单相关行（M2 等）的证据列；**把本片新增的无出处项登记进「允许的差异」**（用 agent-31 已占用的序号之后的下一个号，⛔ 不许插在中间）。

## 2. 判据（自己跑，原始输出贴回报）

1. **tga 头信息**：三张图的 `宽×高×位深×有无 alpha`（原始输出）；
2. **对照表**：`.res`/`gameui_english.txt`/tga 尺寸 → 换算值 → 代码实际值（逐元素一行）；⛔ 只给结论不算；
3. 编译：`tools/probes/compile-check.ps1`（本工程既有配方）⇒ `exit=0`；
4. **一次 Play**：`32_mainmenu.png` + **运行时节点树**（每个按钮的实际坐标/尺寸/贴图名，证明与换算值一致）+ 悬停态截图（鼠标停在 New Game 上，证 `game_menu_mouseover` 生效）；
5. `tools/verify.ps1` ⇒ FAIL=0（若某行证据受影响只重采那几行，写明第几次重采）。

## 3. 不许

- ⛔ 不许改 `CsUiStyle.cs` 的**配色/字体常量**（agent-31 已按 `clientscheme.res` 落好，`ApplyOriginalFonts`/`ResScale` 直接用）；只许**追加**你需要的封装；
- ⛔ 不许改 `UI/InGame/**`（HUD 已验收）、不许改引擎 `clover-client-unity-engine/**`；
- ⛔ 不许改 `策划/**`（除 §1.3 说的验收表行与差异登记）、`docs/**`、`tools/**`、`.ai-tmp/test` 历史文件；
- ⛔ 不许用通用兜底素材；素材只从 `client/Assets/Resources/UI/Art/` 取（已复制好）；
- ⛔ 不许读别的 `clover-project-*` 工程；⛔ 不许开子 agent；⛔ 不许新增 README/交接类 md；临时脚本只放 `.ai-tmp/test/` 用完删；
- ⛔ **不许声称无出处的量有出处** —— 每条要么给出载体出处（`文件:行`），要么明确写"本项目新增 + 已登记差异"。

## 4. 回报格式

```
产出物：<文件清单>
tga 头：<三张图的原始信息>
对照表：<逐元素：原版值(出处) / 换算 / 代码值>
删掉的自创元素：<逐条>
新增的"无出处"登记：<差异序号 + 原文>
编译：<命令 + 末尾 + 退出码>
Play：<图名 + 运行时节点树原文 + 悬停态证据>
闸门：<verify.ps1 逐行>
未决：无 / <具体条目>
```
