using CloverEngine;
using Cs16.Core;
using Cs16.Module.Combat;
using Cs16.Module.Match;
using UnityEngine;

namespace Cs16.Module.Player
{
    /// <summary>
    /// 第一人称的**输入采集与视角**：鼠标转视角（yaw / pitch）、WASD / Shift / Ctrl / Space 意图采集。
    ///
    /// <para><b>它不移动任何人</b>：按主 agent 的裁决，本地玩家的位移由比赛模拟（agent-03）解算
    /// （<see cref="ICsMatch.SetLocalInput(in CsInputState)"/> → 内部 <c>ICsMap.ResolveMove</c>）。
    /// 本组件的唯一出口就是"把 <see cref="CsInputState"/> 填好"，因此不需要、也不允许写
    /// <c>CsActor.Position</c> —— 位置一律从 <c>_match.LocalPlayer.Position</c> 读。</para>
    ///
    /// <para><b>它是 yaw/pitch 的唯一权威</b>：模拟每帧把 <c>CsActor.Yaw/Pitch</c> 覆盖成本组件算出的值，
    /// 相机也读这里的值（而不是回头读 actor 的），这样"回合结算/死亡/观战"这些模拟不处理输入的阶段，
    /// 视角依然能自由转动（观战自由视角 / 回合结束张望）。</para>
    /// </summary>
    public sealed class PlayerMotor : MonoBehaviour
    {
        private const string Tag = "Player";

        private readonly CsModuleLog _log = new CsModuleLog(Tag);

        private ICsMatch _match;

        // ---- 视角 ----
        // 鼠标位移 → yaw/pitch 的累加 = **引擎件** <see cref="LookAccumulator"/>（E-core-18 下沉）：
        // 符号方向 / 夹紧口径 / 灵敏度口径（"每 count 多少度"、不乘 dt）**逐字照搬**本组件原实现
        // ⇒ 手感与改前逐位一致。俯仰限位从 <see cref="CsCombatTuning.PitchLimit"/> 传入。
        private readonly LookAccumulator _look = new LookAccumulator();

        // ---- 设置 ----
        private float _sensitivity = CsConst.DefaultSensitivity;
        private bool _invertY;

        // ---- 出生 / 接管检测 ----
        private long _lastActorId;
        private bool _lastAlive;
        private bool _lookInitialized;

        // ---- 诊断（只报一次）----
        private bool _warnedNoInput;
        private bool _reportedEngineMove;

        /// <summary>当前水平朝向（度，0 = +Z；与模拟的 <c>CsActor.Yaw</c> 同口径）。</summary>
        public float Yaw => _look.Yaw;

        /// <summary>当前俯仰（度，+ = 抬头；与模拟的 <c>CsActor.Pitch</c> 同口径）。</summary>
        public float Pitch => _look.Pitch;

        internal void Init(ICsMatch match)
        {
            _match = match;
        }

        /// <summary>把鼠标灵敏度/反转写进来（<c>PlayerModule</c> 从 <c>Game.Setting</c> 读好后调用）。</summary>
        internal void ApplySettings(float sensitivity, bool invertY)
        {
            if (sensitivity > 0.0001f) _sensitivity = sensitivity;
            _invertY = invertY;
        }

        /// <summary>把视角**直接**对齐到给定角度（出生 / 观战切换 / 接管本地玩家时用，不做平滑）。</summary>
        internal void ForceLook(float yaw, float pitch)
        {
            // 夹紧口径留在调用点（引擎件的 Reset 只做 yaw 的 360° 规范化，见 LookAccumulator 注释）；
            // 数值与原实现逐字一致：Mathf.Repeat(yaw, 360f) + Mathf.Clamp(pitch, ±PitchLimit)。
            _look.Reset(yaw, Mathf.Clamp(pitch, -CsCombatTuning.PitchLimit, CsCombatTuning.PitchLimit));
        }

        // ==================================================================
        //  每帧：采集 → 填 CsInputState
        // ==================================================================
        /// <param name="blockLook">面板/暂停/比赛未开始 —— 连视角都不给转。</param>
        /// <param name="blockMove">冻结期 / 回合结算 / 死亡 / 面板打开 —— 不给移动、跳跃、开火之外的装备操作。</param>
        internal CsInputState BuildInput(bool blockLook, bool blockMove)
        {
            var cmd = default(CsInputState);

            var local = _match != null ? _match.LocalPlayer : null;

            SyncLookWithActor(local);

            if (!blockLook) ApplyMouseLook();

            cmd.Yaw = _look.Yaw;
            cmd.Pitch = _look.Pitch;

            if (local == null || !local.IsAlive)
            {
                // 死亡/未开局：只保留视角（观战自由视角需要它），其余全零。
                return cmd;
            }

            if (blockMove) return cmd;

            cmd.Move = ReadMoveIntent();
            cmd.Crouch = Game.Input.GetKey(GameKey.LeftCtrl) || Game.Input.GetKey(GameKey.RightCtrl);
            cmd.Walk = Game.Input.GetKey(GameKey.LeftShift);

            // 跳跃用 GetKeyDown：官方 CS 默认没有 autojump（按住空格不会连跳）。
            cmd.Jump = Game.Input.GetKeyDown(GameKey.Space);

            return cmd;
        }

        // ==================================================================
        //  视角
        // ==================================================================
        private void ApplyMouseLook()
        {
            var input = Game.Input;
            if (input == null)
            {
                if (!_warnedNoInput)
                {
                    _warnedNoInput = true;
                    _log.Error("input.missing", "Game.Input 为 null（CloverInput.Init 未执行？），视角与移动将完全无响应");
                }
                return;
            }

            var delta = input.MouseDelta;
            if (delta.sqrMagnitude <= 0f) return;   // 位移为零本就不该动视角（与原实现同一道提前返回）

            // 灵敏度口径 = **每 count 多少度**（设置值 × 玩法换算系数），不乘 dt（鼠标位移本身就是增量）。
            var degPerCount = _sensitivity * CsCombatTuning.DegreesPerMouseCount;

            _look.Add(delta.x, delta.y, degPerCount, _invertY, CsCombatTuning.PitchLimit);
        }

        /// <summary>
        /// 首次接管本地玩家 / 每回合重生时，把视角对齐到模拟给的出生朝向
        /// （否则玩家会带着上一回合（甚至上一局）的视线出现在新出生点）。
        /// </summary>
        private void SyncLookWithActor(CsActor local)
        {
            if (local == null)
            {
                _lastAlive = false;
                return;
            }

            if (!_lookInitialized || _lastActorId != local.Id)
            {
                _lastActorId = local.Id;
                _lookInitialized = true;
                ForceLook(local.Yaw, local.Pitch);
                _log.Always($"接管本地玩家：{local.Name}（id={local.Id}，{local.Team}），" +
                            $"初始视角 yaw={_look.Yaw:F1} pitch={_look.Pitch:F1}");
                _lastAlive = local.IsAlive;
                return;
            }

            if (local.IsAlive && !_lastAlive)
            {
                ForceLook(local.Yaw, local.Pitch);
                _log.Always($"本回合出生：视角对齐到出生朝向 yaw={_look.Yaw:F1} pitch={_look.Pitch:F1}");
            }

            _lastAlive = local.IsAlive;
        }

        // ==================================================================
        //  移动意图
        // ==================================================================
        /// <summary>
        /// 读移动意图。**优先用引擎的高层向量**（<see cref="InputState.MoveDirection"/>，后端无关、手柄也能用），
        /// 但 <c>Game.Input.Tick()</c> 由引擎的 <c>EngineRunner</c>（默认执行顺序）驱动，
        /// 与业务 <c>Update</c> 的先后不保证 —— 所以再直读一次 WASD，非零者优先，消除"移动慢一帧"。
        /// </summary>
        private Vector2 ReadMoveIntent()
        {
            var input = Game.Input;
            if (input == null) return Vector2.zero;

            var keyMove = new Vector2(
                (input.GetKey(GameKey.D) ? 1f : 0f) - (input.GetKey(GameKey.A) ? 1f : 0f),
                (input.GetKey(GameKey.W) ? 1f : 0f) - (input.GetKey(GameKey.S) ? 1f : 0f));

            if (keyMove.sqrMagnitude > 0.0001f) return keyMove.normalized;

            var state = input.State;
            if (state == null) return Vector2.zero;

            var dir = state.MoveDirection;
            if (dir.sqrMagnitude > 1f) dir.Normalize();

            // 引擎给了移动向量、而 WASD 直读为零 —— 说明当前生效的是"非键盘"输入源（手柄/轴映射）。
            // 这种情况值得留一条（只报一次），免得将来有人以为是按键坏了。
            if (dir.sqrMagnitude > 0.0001f && !_reportedEngineMove)
            {
                _reportedEngineMove = true;
                _log.Always($"移动意图来自引擎 MoveDirection（{dir}）而不是 WASD 直读 —— 当前输入源不是键盘");
            }

            return dir;
        }
    }
}
