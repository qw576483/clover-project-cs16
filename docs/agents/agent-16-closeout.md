# agent-16：收尾三件（闸门假阳性 / 验收表证据回填 / 对照表收口）

> 项目根：`clover-project-cs16`
> 上一棒（agent-15，实机取证）**产物已落地但回报丢了**：`client/Assets/Screenshots/` 现有 **41 张**截图（含 3 张联络图
> `contact-sheet-1-menu.png` / `contact-sheet-2-ingame.png` / `contact-sheet-3-map-assets.png`），
> 生成器产物 **615 `.anim` + 38 `.controller`** 已落盘，`evidence-freshness` 已转 PASS。
> 但它有**三件没做完**，本片接着做。

## 0. 现状（已实测）

```
PASS  stray-temp-files / hard-rules / acceptance-table(64 rows) / differences-registry(17 rows)
PASS  refs-reachable(40 shot refs) / evidence-freshness(41 shots newer than code) / gate-present
PASS  reference-table / no-handover-docs / scoping-window / row-category / no-escaped-artifacts / sampler-selfcheck
FAIL  impl-by-executor  1858/1962 changed impl file(s) have no dispatch record
HUMAN-ONLY  engine-credit
```

## 1. 只做这三件

### 1.1 修闸门第 15 条的**假阳性**（`tools/verify.ps1`）

**问题**：第 15 条把 `client/Assets/Resources/**` 整个当作"实现面"，于是 ArtSetup 批量产出的
1962 个资产文件（`.prefab` / `.anim` / `.controller` / `.asset` / `.mat` / `.mesh` / `.png` / `.wav`）全部被判成"没有派活留痕"。

**为什么这是假阳性**（skill §1.11 第 11 条 / "会误报的检查比没有检查更糟"）：
这些是**生成器的产物**（出处 = `Assets/Editor/**` 的生成器代码 + `原版资源/**` 的原始数据），
不是"某个人手写的实现文件" —— 用"逐文件对账派活"去判它们，永远为假。

**怎么改**（照此实现，别自己另发明）：
- 对账范围仍保留现有三棵树（`Scripts` / `Editor` / `Resources`），但**只统计"人写的文本源码"**：
  在 `Resources` 那一支上加扩展名白名单（`.cs` 即可；`.json` / `.txt` / `.csv` / `.tsv` 若将来有手写表再加），
  **生成物扩展名（`.prefab/.anim/.controller/.asset/.mat/.unity/.png/.wav/.bytes/.meta`）一律排除**；
- 在脚本注释里写清**为什么**（引 skill §1.11 第 11 条），并把这条口径写进 `tools/ai-skill/constraints.md` 的静默失败清单一行；
- 保持 **ASCII-only**（非 ASCII 用码点拼）；
- ⛔ 不许放宽第 15 条的本意：`Scripts` / `Editor` 下的 `.cs` **必须**继续逐文件对账（那才是"主 agent 有没有自己动手"的判据）。

**改完必须做两次自检**（skill §1.11 新增条款，缺一不可）：
1. **已知正确样本必须 PASS** —— 当前工程跑一遍，`impl-by-executor` 应为 PASS；
2. **已知错误样本必须 FAIL** —— 构造一个缺陷：临时改一个 `client/Assets/Scripts/**` 下的 `.cs`（改个空格即可）
   并确保它在 dispatch-log 里没有覆盖行 ⇒ 跑闸门应报 FAIL；**自检完把临时改动还原**（用 `git` 不行就手动改回，并核对字节一致）。

### 1.2 验收表证据回填（`策划/验收表.md`）

现状：仍有多处写着「（待重采）」——**图已经采好了，只是表没更新**。

- 逐行把「证据」列改成**实际存在的图名**（在 `client/Assets/Screenshots/` 里 `Test-Path` 过）与**真实日志行原文**；
- `表现类` 行的证据**必须能指到「联络图索引」里的格号**（三张联络图已存在）；
- `R5` / `R6`（角色动画 / 第一人称武器动画）按**实际生成结果**改状态与证据（615 `.anim` / 38 `.controller` 已落盘，
  实机是否在播要看你的 Play 结果 —— 拿不到就如实 BLOCKED，⛔ 不许编）；
- 「收尾自检」小节各条按本次实测更新（**汇总数字必须 = 表体**，闸门第 3 条会核）；
- ⛔ **不许改判据、类别、行**；⛔ 拿不到证据的行如实留 BLOCKED。

### 1.3 对照表收口（`策划/对照表.md`）

**任务书已在盘上**：`docs/agents/agent-14-reftable-closeout.md` —— **逐条照它做**（那是主 agent 定的契约），要点：
已对齐的行差值改 `0`（保留出处）、未对齐的写清现状（雷达 U-06 / BLOCKED-1/2）、差值≠0 的行注明验收表「允许的差异」#n、
全表引用可达性复核。⛔ 不许删行、不许改表结构、不许把"未实现"写成"0"。

## 2. 现场事实（别重复踩坑）

| 事实 | 说明 |
|---|---|
| **unity 命令必须在 `client` 目录内执行** | 否则 `unity status` 空表（那不是编辑器没开） |
| `unity command eval` 有主线程 5 s 上限 | 长任务用 `--detach` / job 轮询，见 `clover-tools/ai-skill/reference/pipeline-and-unity-cli.md` |
| `--code` 里不要写字符串字面量 | PowerShell 会把引号吃掉（`return "x";` → `return x;`） |
| 编辑器 PID / 状态 | 34268 / `ready`（若已关闭，先回报再定） |

## 3. 判据（自己跑，原始输出贴进回报）

1. `powershell -NoProfile -ExecutionPolicy Bypass -File tools\verify.ps1` ⇒ **除 `HUMAN-ONLY` 外无 FAIL**（贴逐行）；
2. 第 1.1 条的**两次自检**原始输出（正确样本 PASS / 错误样本 FAIL / 还原核对）；
3. `Select-String -Path 策划\验收表.md -Pattern '待重采'` ⇒ **0 命中**；
4. 验收表引用的每个图名逐个 `Test-Path` 结果；
5. 对照表：分块行数 + 差值分布 + 可达性检查（总数/修复数/剩余 0）；
6. **`M2` 的署名**：读 `contact-sheet-1-menu.png`（或 `21_mainmenu.png`），把你在图上**逐字**读到的签名文本（含大小写）写进回报；
   读不到图 ⇒ 报 `BLOCKED：图像通道不可用`，⛔ 不许编。

## 4. 不许

- ⛔ 不许改 `策划/策划案/**`、`tools/ai-skill/SKILL.md`（1.1 只允许动 `constraints.md` 一行）、任何全局 skill；
- ⛔ 不许改业务代码（`client/Assets/Scripts/**`、`Editor/**`）—— 本片是收尾，不是开发；
- ⛔ 不许编造证据 / 不许把 BLOCKED 写成通过；⛔ 不许读别的 `clover-project-*` 工程；⛔ 不许开子 agent；
- 一次性脚本只放 `.ai-tmp/test/`，用完删；证据图放 `client/Assets/Screenshots/`；不许产出交接/进度类 md。

## 5. 回报格式

```
产出物：<文件绝对路径清单>
自检：<verify.ps1 逐行 + 两次自检 + 待重采计数 + 可达性 + 署名逐字>
未决：无 / <具体条目>
```
