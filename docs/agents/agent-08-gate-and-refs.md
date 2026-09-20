# agent-08：收尾闸门升级 + 引用可达性修复（clover-project-cs16）

> 项目根：`clover-project-cs16`
> 主 agent 已用最新 clover-engine skill 核验，本片只修**流程层**缺口，**不碰业务代码**。

## 0. 开工必做

1. 工具集里有 `use_skill` → 执行 `use_skill("clover-engine")`。
2. **必读**（按序，命中即用）：
   - 全局 skill 规则层：`~/.codebuddy/skills/ai-skill/SKILL.md`（重点 §1.8 / §1.11 / §1.12 / §1.13）
   - **闸门模板（本片的规格真源）**：`clover-ai-skill/reference/verify-template.md`
     —— **第 102~259 行的骨架就是要求**，逐条落地，⛔ 不许自己另发明条目。
   - 现状：`tools/verify.ps1`（旧版，只有 1~9 条，缺第 10~15 条）

## 1. 只做（就这三件）

### 1.1 重写 `tools/verify.ps1`

按 `verify-template.md` 骨架的 **15 条** 落地，并适配本项目：

- `$root` = 项目根（`Split-Path $PSScriptRoot -Parent`）；
- 代码目录 = `client/Assets/Scripts`；截图目录 = `client/Assets/Screenshots`；
- **第 15 条 `implGlobs` 必须覆盖本项目的实现面**（实现不只在 Scripts 下）：
  `client/Assets/Scripts/**/*.cs`、`client/Assets/Editor/**/*.cs`、`client/Assets/Resources/**`（prefab/asset/bytes/wav/png 等生成物不强求逐一对账，但 Editor 生成器脚本要在范围内）；
- **必须 ASCII-only**：中文路径/关键词一律**码点拼**（模板第 114~120 行是样例）；
- 输出格式保持模板口径：逐行 `PASS / FAIL / HUMAN-ONLY` + 末尾汇总 `FAIL=n HUMAN-ONLY=m` + `exit code`；
- 第 3 条（验收表自洽）与第 12 条（每行标 `数值类`/`表现类`）**照模板实现**：行数用 `(?m)^\|\s*[A-Z]?\d+\s*\|` 统计；
- 第 13 条（工程外产物）：`$wsRoot` = 工作区根（`full-dev`），`$rootName` = `clover-project-cs16`，短名 token = `cs16`；宿主产物目录照模板用 `$env:APPDATA` 探测，探不到就留空。

### 1.2 修 `client/资源欠缺清单.md` 的失效引用

该文件多处引用**已不存在的工程外路径** `_assets_tmp/cs16src/...`（如第 16/17/54/62 行）。
真实位置已迁到项目内 `原版资源/cs16src/`（含 `cs16_build.py`、`hlsdk/`、`inno/`、`goldsrs/`、`cstrike-LiON.iso`）。

要求：
- 逐条改成**项目内真实路径**（相对项目根写，如 `原版资源/cs16src/cs16_build.py`）；
- 改完**每条路径都 `Test-Path` 过一次**，把通过的结论贴进回报；
- ⛔ 只改路径与失效描述，**不许改资源表的事实内容**（哪来的资源、什么状态照旧）。

### 1.3 清掉空目录

先确认**确实为空**（含隐藏文件），为空才删；非空就地回报、不许动：
- `策划/数值文档/`
- `策划/参考图/`
- `策划/自审对比/`

## 2. 判据（做完必须自己跑一遍并把原始输出贴进回报）

1. `powershell -NoProfile -ExecutionPolicy Bypass -File tools\verify.ps1` 能跑完，逐行输出状态；
2. `tools/verify.ps1` 字节里 `>127` 的个数 = 0（ASCII-only）；
3. `client/资源欠缺清单.md` 里每个路径引用 `Test-Path` 通过。

> 预期：`reference-table` 与 `evidence-freshness` 仍会是 FAIL —— 那两项由后续片处理，**不属你的范围**，如实贴上即可。

## 3. 不许

- ⛔ 不许改任何 skill（项目级 `tools/ai-skill/`、仓库源 `clover-ai-skill/`、宿主安装副本）；
- ⛔ 不许碰 `client/Assets/**` 下的业务代码 / 资源 / 场景 / prefab；
- ⛔ 不许改契约文件、不许改 `策划/验收表.md` 与 `策划/策划案/**`；
- ⛔ 不许开子 agent；
- ⛔ 临时文件只许放 `.ai-tmp/test/`，用完即删；不许产出交接/进度类 md。

## 4. 回报格式

```
产出物：<文件绝对路径清单>
自检：<实际执行的命令 + 原始输出>
未决：无 / <具体条目>
```
