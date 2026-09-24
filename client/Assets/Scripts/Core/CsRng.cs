// ─────────────────────────────────────────────────────────────────────────────
// Cs16 · Core/CsRng.cs
// 本局随机源的**唯一入口**：把业务里散落的裸 `UnityEngine.Random` 收敛到
// 引擎的可复现随机器 <see cref="CloverEngine.Rng"/>（`clover-client-unity-engine/Runtime/Core/Rng.cs`）。
//
//   `UnityEngine.Random` 是**全局静态**状态。任何一个模块随手调一次，都会把别人的序列往后推一格
//   ⇒ 同一局重放对不上、bug 现场无法复现（引擎 `Rng.cs:13-17` 明令禁用）。
//
// 为什么在 `Core/`（而不是某个 Module 里）：
//   随机器是**跨模块通用底座**（玩法模拟 / bot AI / 弹道 / 表现 / 音效 / UI 自动选边都要），
//   而 `UI/**` 不许 `using Cs16.Module.*`（`conventions.md` 目录边界）⇒ 只能放契约层。
//   引擎自己也是按同一条规则把 `Rng` 下沉到 `Runtime/Core/`（见 `Rng.cs:9-11`）。
//   形态与本层既有设施一致（`CsHudSnapshot` 的静态状态、`ResPaths` 的字面量表）。
//
//   ① **一路一 salt**：每个用途（<see cref="CsRngStream"/>）派生一条**独立子序列**，
//      互不干扰（"别人多抽一次"不会改变你这一路的值）；按 actor 的场合再用 `Derive(流, id)` 再分一路。
//   ② 派生是纯函数：同一 (种子, 用途, salt) 恒定得同一子流（引擎 `Rng.Derive` 口径）。
//   ③ **seed 一定留痕**：定种子 / 变种子都打 `Info`（含 seed 与来源），所以任何一局
//      "从日志里的 seed 重放"都能复现 —— 这是引擎对"不可复现入口"的既有口径
//      （`Rng.cs:47-54`：唯一允许的时钟入口是 `Rng.FromTime()`，且必须打日志）。
//   ④ **绝不静默退到时间种子**：未定种子时不是"悄悄用当前时间"，而是**显式定一次**
//      并打出「来源=首次使用（时间）」；离线驱动 / 自检要确定性就走 `InjectSeed`（注入优先）。
//   ⑤ 非线程安全：主线程使用（与引擎 `Rng` 同约定）。
// ─────────────────────────────────────────────────────────────────────────────

using System.Collections.Generic;
using CloverEngine;

namespace Cs16.Core
{
    /// <summary>
    /// 随机流用途。**每个用途一条独立子序列**（salt = 枚举值），互不干扰。
    ///
    /// <para>命名口径 = 「谁在抽、抽来干什么」：同一份语义只允许一个流（例如全项目的"弹道散布"
    /// 共用一个 <see cref="WeaponSpread"/>，因为三处 <c>ApplySpread</c> 本来就是同一套口径）。</para>
    ///
    /// <para>新增取值只许**追加**，不许改已有取值的数字 —— 改了就等于换掉那一流的历史序列。</para>
    /// </summary>
    public enum CsRngStream
    {
        /// <summary>复活时的随机朝向（`CsMatch.RespawnActor`）。玩法：出生姿态。</summary>
        SpawnYaw = 1,

        /// <summary>每回合 C4 交给哪个 T（`CsMatch.GiveBombToRandomT`）。玩法：开局资源分配。</summary>
        BombCarrier = 2,

        /// <summary>bot 瞄准误差重采样（`CsMatch.AimBotAt`，按 actor 再派生）。玩法：命中率。</summary>
        BotAimError = 3,

        /// <summary>bot 连发/停火节奏（`CsMatch.BotTryFire`，按 actor 再派生）。玩法：射速观感 + 命中。</summary>
        BotFirePacing = 4,

        /// <summary>bot 瞄准手感（瞄头掷 + 头部摆动，`CsBotBrain`，按 actor 再派生）。玩法：命中率。</summary>
        BotAimFlavor = 5,

        /// <summary>bot 决策（选守点 / 换位择优，`CsBotBrain`，按 actor 再派生）。玩法：站位。</summary>
        BotDecision = 6,

        /// <summary>bot 买枪掷（AWP 概率，`BotBuyLogic`）。玩法：经济与火力分布。</summary>
        BotBuy = 7,

        /// <summary>弹道散布（`CsMatch.ApplySpread` / `CsInventory.ApplySpread` / `Firearm.ApplySpread`
        /// —— 三处本来就是同一套欧拉角扰动，共用一个流）。玩法：命中判定，必须可复现。</summary>
        WeaponSpread = 8,

        /// <summary>后坐力水平偏移（`CsInventory.TryDischarge`）。玩法：弹道累积。</summary>
        RecoilYaw = 9,

        /// <summary>标记点随机取用（`CsMap.TryGetPoint`）。玩法：AI 目标点。</summary>
        MapMarkerPick = 10,

        /// <summary>表现层变体（弹痕五选一 / 血迹六选一，`CombatEffects`）。表现：与玩法无关。</summary>
        FxVariant = 11,

        /// <summary>音效变体（脚步/死亡多采样，`SfxService`）。表现：与玩法无关。</summary>
        SfxVariant = 12,

        /// <summary>UI 自动选边（`TeamSelectPanel` = 原版 `jointeam 5`）。发生在**开局之前**，
        /// 所以它自己的首次使用会按「首次使用（时间）」定种子（见 <see cref="CsRng"/> ④）。</summary>
        UiAutoAssign = 13,

        /// <summary>受击摇晃方向（`FirstPersonCamera` 交给引擎 rig 的 `AddShake`）。表现：与玩法无关，
        /// 但同样走本入口 —— 引擎禁用全局静态随机器（`Runtime/Core/Rng.cs:13`），
        /// 只许**追加**取值。</summary>
        CameraShake = 14,
    }

    /// <summary>
    /// 本局随机源注册表：**一处定种子**（<see cref="BeginMatch"/> / <see cref="InjectSeed"/>），
    /// 各处按用途取独立子流（<see cref="Stream"/> / <see cref="Derive"/>）。
    ///
    /// <para><b>典型用法</b></para>
    /// <code>
    /// var rng = CsRng.Stream(CsRngStream.WeaponSpread);
    /// var dir = rng.Range(-spread, spread);
    /// // 按 actor 分路（同一 actor 恒定拿同一条）：
    /// var mine = CsRng.Derive(CsRngStream.BotDecision, a.Id);
    /// </code>
    ///
    /// <para><b>生命周期</b>：`CsMatch.Start` 调 <see cref="BeginMatch"/>（清空全部子流 + 定新种子）；
    /// 离线驱动 / `BotSelfTest` 在跑之前调 <see cref="InjectSeed"/>（注入**优先于**时钟，且一直生效）。</para>
    /// </summary>
    public static class CsRng
    {
        private const string Tag = "CsRng";

        /// <summary>(用途, salt) → 子流。**必须缓存**：同一路反复 `Derive` 若每次新建实例，
        /// 每次都从序列头开始 ⇒ 抽到的永远是同一个值（"冻结"而不是"随机"）。</summary>
        private static readonly Dictionary<long, Rng> Streams = new Dictionary<long, Rng>(32);

        private static int _seed;
        private static bool _settled;
        private static bool _injected;
        private static int _injectedSeed;

        /// <summary>当前本局种子（日志/存档要它 —— 拿着它就能重放这一局）。</summary>
        public static int MatchSeed => _seed;

        /// <summary>本局种子是否已定（未定时首次取流会按「首次使用（时间）」定一次并留痕）。</summary>
        public static bool IsSettled => _settled;

        /// <summary>
        /// **注入**本局种子：同一 seed ⇒ 同一整局（离线驱动 / 自检用）。
        ///
        /// <para>注入是**黏的** —— 之后每次 <see cref="BeginMatch"/> 都用它，直到再次注入；
        /// 这样"一局自检里多次 Start"也仍然是同一个受控种子（否则第 2 次 Start 就滑回时钟了）。</para>
        /// </summary>
        public static void InjectSeed(int seed)
        {
            _injected = true;
            _injectedSeed = seed;
            Settle(seed, "注入");
        }

        /// <summary>开始新一局：清空全部子流并确定本局种子（注入优先，否则时钟 + 留痕）。返回所用 seed。</summary>
        public static int BeginMatch()
        {
            if (_injected) Settle(_injectedSeed, "注入");
            else Settle(Rng.FromTime().Seed, "时间");
            return _seed;
        }

        /// <summary>取某个用途的随机流（同一用途恒定返回同一条实例 ⇒ 序列连续推进）。</summary>
        public static Rng Stream(CsRngStream stream) => Derive(stream, 0L);

        /// <summary>
        /// 在某个用途上再按 <paramref name="salt"/> 派生一条子流（典型 salt = actor id）。
        /// 同一 (用途, salt) 恒定返回同一条实例；不同 salt 互不干扰。
        /// </summary>
        public static Rng Derive(CsRngStream stream, long salt)
        {
            EnsureSettled();

            var key = ((long)(int)stream << 32) | (uint)(int)salt;
            if (Streams.TryGetValue(key, out var cached)) return cached;

            // 与引擎 `Rng.Derive` 同一口径：子 seed = 父 seed * 31 + salt（纯函数 ⇒ 可重放）。
            var rng = new Rng(StreamSeed((int)stream));
            if (salt != 0L) rng = rng.Derive((int)salt);
            Streams[key] = rng;
            return rng;
        }

        /// <summary>某一用途的父 seed（= 本局 seed 按用途 salt 派生；与 `Rng.DeriveSeed` 同式）。</summary>
        private static int StreamSeed(int stream) => unchecked(_seed * 31 + stream);

        /// <summary>
        /// 兜底（约束 ④）：**未定种子**时显式定一次 —— 走引擎唯一允许的时钟入口
        /// <see cref="Rng.FromTime"/>（它自带 `Info: FromTime seed=…`），我们再打一条说明来源。
        /// 不是"静默退回时间"：日志里既有 seed 也有"来源=首次使用（时间）"。
        /// </summary>
        private static void EnsureSettled()
        {
            if (_settled) return;
            Settle(Rng.FromTime().Seed, "首次使用（时间）");
        }

        private static void Settle(int seed, string source)
        {
            _seed = seed;
            _settled = true;
            Streams.Clear();
            Game.Logger?.Info(Tag,
                $"本局随机种子={seed}（来源={source}）—— 一路一 salt，同种子⇒同序列；" +
                "要重放请拿这个 seed 走 CsRng.InjectSeed");
        }
    }
}
