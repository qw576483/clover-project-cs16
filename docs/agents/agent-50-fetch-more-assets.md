# agent-50 · 换渠道找素材：其余兵种模型 + 枪口映射表位置

## 背景：用户**没有原版游戏** ⇒ 只能靠公开渠道，⛔ 不许再指望"官方客户端"
现状（已知事实，别重查）：
- 已有渠道 `https://raw.githubusercontent.com/tonitaga/cstrike/main/` 只提供 **terror.mdl / urban.mdl** 两个 player 模型 ⇒ **其余 7 个角色拿不到**。
- `#71` 仍缺：`models/player/{gign,gsg9,sas,vip,leet,arctic,guerilla,militia,spetsnaz}/*.mdl`（本工程皮肤键只到 `player_CT[_gign|_gsg9|_sas|_vip]` / `player_T[_arctic|_guerilla|_leet]`，`militia`/`spetsnaz` 连键都没有）。
- `#89` 枪口逐武器映射：**已证明不在 `hw.dll`**（只 precache 3 张、`idx=(arg%10)%3`、选择例程 `0x1D30BF0` 在 hw.dll 里 **0 个直接调用点**、只作函数指针表第 19 项）⇒ 表在**调用方**。盘上还有 `cl_dlls/client.dll`(1,093,128 B) 与 `dlls/mp.dll`(1,640,960 B) **未被查过"哪把枪用哪张 muzzle 贴图"**。

## 任务（两件，件件要证据）
### A. 穷尽公开渠道找其余兵种模型（≥4 轮不同关键词 × ≥3 类站点）
- 关键词中英各半（如 `cs 1.6 player models gsg9 mdl`、`cstrike models/player repack`、`hldm player model github`…），⛔ 不换目标但可换源；
- **每件必须**：落地 `.ai-tmp/test/agent50-fetch/` 隔离区 → 校验（`.mdl` 头 `IDST` + `v10` + `hdr.length == filesize`；能解出 `numbones`/`numseq` 更好）→ 记 `URL | 时间 | 字节 | SHA256` → 复制到 `原版资源/cs16src/cstrike/cstrike__models__player__<兵种>__<兵种>.mdl`；
- ⛔ 不许把"看着像"当证据；⛔ 不许声称"与官方逐字节同版"（口径上限 = 与来源 repack 一致）；
- 台账 append-only：`原版资源/补充记录-agent50取件.md`（⛔ 别编辑 `原版资源/清单.md`）。
### B. 查 `client.dll` / `mp.dll` 里"哪把枪用哪张 muzzle 贴图"
- 目标：把 `fx-decals` 在 `hw.dll` 上没找到的那张表**在客户端 DLL 里找到**（贴图名或索引→武器的映射）。可用的入手点：`muzzleflash1/2/3` 字符串、`0x1E825C4` 那个函数指针表被谁取走、`client.dll` 里的 `CBaseEntity::FireBullets` 类路径。
- 判据 = **每个结论带 `文件 + 偏移 + 字节`**；找不到就 **BLOCKED 并写清试过什么**（⛔ 不许"推测表长什么样"）。
- 结论落 `.ai-tmp/test/agent50-muzzle-map.md`（一句话结论 + 逐条偏移），⛔ 不改任何产品代码（那由别的片做）。

## 硬约束
1. ⛔ 不改引擎、⛔ 不改 skill、⛔ 不改 `client/Assets/**`（本片只到"素材入库 + 出处"）。
2. 写入范围：`.ai-tmp/**`、`原版资源/**`。
3. 所有命令显式超时（`curl --max-time`）；超 15 分钟把进度落盘 + 给 `main` 一条回报（**第一行写模型标识**）。
4. 取件口径（前序已定，逐条照做）：`curl.exe -L --retry 3 --max-time 120 -o <out> <url>`；`http_code=000 ≠ 不存在` ⇒ **判"取没取到"只看字节数 + SHA256**。
5. ⛔ 别把整包搬进工程；进 `原版资源/` 的只有你实际要留的那几件。

## 回报（`send_message` 给 `main`）
① 已入库件数 / 未取到件数 + 逐件一行（path | 字节 | SHA256 前 8 | OK 或失败性质）；② muzzle 表结论（逐条偏移，或 BLOCKED + 试过什么）；③ 台账路径。⛔ 不填表、⛔ 不建矩阵、⛔ 不长解释。
