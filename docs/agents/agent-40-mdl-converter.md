# agent-40 · mdl → 工程中间格式（`.cs16mdl` / `.cs16anim`）转换链重建

> 为什么需要：用户报的两条都卡在这一步 ——
> - **#71**「人物模型还是不对，感觉你是社区版本的模型，不是原版模型」（`策划/差异登记.tsv` 第 72 行）
> - **#75**「枪也不在地上？」（第 76 行）—— 地上掉落枪要用原版 `w_*.mdl`
>
> 现状：工程内模型数据的全部来源是**一份社区 repack 的 mdl 经** `cs16_build.py` / `cs16_anim.py` 转成
> `client/Assets/Editor/Views/ModelData/*.cs16mdl|*.cs16anim`，而**那两个转换脚本已不在盘**（历史路径）
> ⇒ 现在拿到新 mdl（本轮刚取回，见下）也**进不了工程**。
> ⇒ 本片把这条链**重建**出来，并用「与工程内既有中间数据逐字节/逐字段对账」证明它是对的。

## 一、本轮刚取回的原版 mdl（在盘，可直接当输入）

`原版资源/cs16src/cstrike/` 下（命名 = `cstrike__models__<...>`）：
- `cstrike__models__player__terror__terror.mdl`（2,340,680 B）
- `cstrike__models__player__urban__urban.mdl`（2,329,144 B）
- `cstrike__models__w_usp.mdl` / `w_glock18.mdl` / `w_ak47.mdl` / `w_m4a1.mdl` / `w_awp.mdl` / `w_deagle.mdl`

工程内已有的中间数据（**目标格式样本**，38 份）：`client/Assets/Editor/Views/ModelData/**`。

## 二、要做完的三件

1. **找/建转换器**：先穷尽找现成的（工程内既然转过 `v_*.mdl`，链子存在过 —— 查 `client/Assets/Editor/**`、
   `tools/**`、`.ai-tmp/**` 里的历史脚本）。
   - 找到 ⇒ 跑通即可（写清它是谁、在哪）。
   - 找不到 ⇒ 按 `.cs16mdl` / `.cs16anim` 的**真实格式**重写一个（格式定义在**读**这两个格式的工程代码里 ——
     先找到读取方：`client/Assets/Editor/Views/**` 或运行时视图代码，据它逆推格式，⛔ 别拍脑袋定格式）。
2. **跑通并落盘**：至少用 `terror.mdl`（+ 上面 6 把 `w_*.mdl`）产出中间数据。
3. **对账（本片的核心判据）**：用重建的转换器把 `terror.mdl` 转出来，
   与工程内**已存在**的对应那份 `.cs16mdl`（自己按角色名找，如 `player_T*.cs16mdl`）**逐字段 / 逐字节对比**：
   - 一致 ⇒ 同时得到两个结论：转换器正确 + 工程内那份中间数据确实来自**这个渠道的 mdl**（如实写口径，⛔ 不许说"与官方客户端逐字节同版"）。
   - 不一致 ⇒ **逐项列出差在哪**（字段名 / 偏移 / 值），并给出"是格式版本差还是来源 mdl 不同"的判断依据。

## 三、边界

- 写入范围：`tools/probes/**`（**只许新增** `mdl-*` 前缀的文件）、`client/Assets/Editor/Views/**`（若转换器是 Editor 脚本）、
  `原版资源/**`、`.ai-tmp/**`、`docs/agents/agent-40-*.md`、`策划/差异登记.tsv` 第 72 / 76 行（只改这两行）。
- ⛔ 不改引擎源码、⛔ 不改 skill。
- ⛔ 不写 `client/Assets/Scripts/Module/**`、`client/Assets/Scripts/Core/**`、`client/Assets/Scripts/UI/**`
  （那些是别的片的地盘；要把新模型接进游戏是**下一片**的事，本片只到"转换器 + 中间数据落盘 + 对账"）。
- ⛔ 不写 `tools/probes/` 里的**既有**文件（另一片刚清理过它们的注释）——只许新增。
- 该片**全程离线**（不需要 Unity / Play），⛔ 别去抢 Play。
- 命令一律显式超时；超过 12 分钟把进度落盘 + 给 `main` 一条回报。
- 注释按 skill §3 第 3b 条：只写「现在是什么」——⛔ 不写批次 / 本轮 / 任务书 / 用户报的 / 日期 / 别的工程名。

## 四、回报（send_message 给 main）

① 转换器 = 找到的（路径）还是重写的（路径 + 依据哪个读取方逆推）；② 产出了哪几份中间数据（路径 + 字节）；
③ 对账结果：一致 / 逐项差异清单；④ 做不到的逐条 BLOCKED。⛔ 不填表、⛔ 不建矩阵。
