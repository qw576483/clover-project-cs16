using System.Collections.Generic;
using CloverEngine;
using Cs16.Core;
using Cs16.Module.Bot.Bt;
using Cs16.Module.Map;
using Cs16.Module.Match;
using UnityEngine;

namespace Cs16.Module.Bot
{
    public sealed partial class CsBotBrain
    {
        // ---- 交火节奏（点射）----
        /// <summary>本串点射的最后一发时刻（到点后进入停火段）。</summary>
        private float _burstStopAt;

        /// <summary>停火段结束时刻（在此之前不扣扳机，只保持指向）。</summary>
        private float _burstResumeAt;

        // ---- 残血后撤 ----
        /// <summary>本次后撤的结束时刻（0 = 不在后撤）。</summary>
        private float _retreatUntil;

        /// <summary>本 tick 是否已选定后撤落点。</summary>
        private bool _retreatGoalValid;

        /// <summary>后撤落点（世界坐标，已过可站立判定）。</summary>
        private Vector3 _retreatGoal;

        /// <summary>本次投掷的预测落点距自己的距离（米；进投掷日志）。</summary>
        private float _grenadeSelfGap;

        /// <summary>弹道遮挡检测的复用缓冲（<see cref="AimPathClear"/>；不分配托管内存）。</summary>
        private readonly RaycastHit[] _aimRay = new RaycastHit[16];

        /// <summary>自己扔出的危险投掷物（高爆 / 闪光）爆炸前，不许朝落点方向前进的截止时刻。</summary>
        private float _grenadeDangerUntil;

        /// <summary>自己扔出的那颗雷的落点（危险窗口内用来判断"要不要先离开它"）。</summary>
        private Vector3 _grenadeDangerPos;

        // ==================================================================
        //  交战
        // ==================================================================
        private CsBotIntent Engage(CsActor self, CsBotIntent intent, float now)
        {
            MaybeRetarget(self, now);

            var target = _match.Find(_targetId);
            if (target == null || !target.IsAlive)
            {
                _targetId = 0;
                intent.State = CsBotState.Idle;
                intent.AimPoint = LookAhead(self);
                return intent;
            }

            var dist = Vector3.Distance(self.EyePosition, target.EyePosition);
            var aimHead = ChooseHeadshot(now);
            var point = aimHead
                ? target.EyePosition
                : target.Position + Vector3.up * (target.Height * CsBotConst.ChestHeightRatio);

            // 预瞄提前量 = 半个决策 tick（决策到扣扳机的平均延迟）+ 按难度递增的提前量
            // （用 profile.AimSpeedDegrees 归一化，不引第二套难度表）；总提前量受
            // CsBotConst.PredictLastSeenMaxSeconds 上限约束。
            var predict = PredictSeconds();
            if (predict > 0.0001f)
            {
                point += new Vector3(target.Velocity.x, 0f, target.Velocity.z) * predict;
            }

            // 瞄头更难：额外加一份"低误差"，量 = AimErrorDegrees × 0.4（重采样节奏与模拟内部一致）
            if (aimHead) point += HeadWobbleOffset(self, point, now);

            var canFire = ApplyAimAndFire(self, point, now, ref intent);

            // ---- 姿态与走位 ----
            intent.Crouch = false;
            intent.Walk = false;

            // 本 tick 该不该站定：要么真的扣扳机，要么目标可见且瞄点已收敛（先停稳，速度落下来才打）。
            // 子弹散布直接读水平速度（`Firearm.ComputeSpread` 的 MoveFactor 与档位判据）⇒ 边跑边打就是把子弹打飞。
            // 站定判据比开火门限松：先停下来把水平速度落干净（子弹散布按速度放大），
            // 再等瞄向收敛到开火门限 —— 两者若同宽，瞄向没收敛时机器人会一直侧移，
            // 速度永远归不了零，扳机也就永远扣不下去。
            var standToFire = canFire ||
                              (_visibleNow &&
                               SubmittedAimAngle(self, intent.AimPoint) <= AimGate() * CsBotConst.StandGateScale);

            //
            //   持包者（T 且未下包）的走位目标是**本轮包点**，不回落到"与敌人保持偏好距离"：
            //   规格「T 持包到 B 点 → 下包」（`策划/策划案/CS1.6单机参考规格.md:131`）里，
            //   下包是持包者这一回合唯一的回合目标；三档表（同文件 :119-121）没有任何一档写"对峙"。
            // 只改"压上方向"：瞄准 / 开火 / 难度参数一字不动（`ApplyAimAndFire` 已在上方跑完）；
            //    不新增第二个半径 —— 停步半径复用既有 `CsBotConst.PlantStopRadius`（与
            //    `TryPlantOrPickup` 同值），"进包点判定区就下包"由 `CommitTacticalDecision.MustPlantFirst` 负责。
            var plantAim = Vector3.zero;
            var pressing = false;
            if (self.Team == CsTeam.T && self.HasBomb && !_match.BombPlanted)
            {
                pressing = TryGetPlantAim(self, out plantAim);
            }

            // 看不见目标时，"追"的位置是**最后所见位置**而不是敌人的实时坐标：
            // 隔墙跟着实时位置走 = 透视。
            var knownPos = _visibleNow || _lastSeenPos.sqrMagnitude < 0.0001f
                ? target.Position
                : _lastSeenPos;

            // 弹匣空了正在换弹、且已经在有效射程内 → 边退边换（背朝掩体方向退，不站在开阔地当靶子）。
            var retreating = intent.Reload && dist <= PreferredRange() * CsBotConst.AdvanceRangeFactor;

            if (pressing)
            {
                // 持包压点：遇敌要开枪就先停 `EngageShotHoldSeconds` 打完这一梭 / 这一串点射，
                // 再继续朝包点压 —— 不停下就没有 0 速开火帧（见 `CsBotConst.FireStandSpeed`）。
                if (standToFire) _engageHoldUntil = now + CsBotConst.EngageShotHoldSeconds;

                if (now < _engageHoldUntil)
                {
                    intent.Move = Vector3.zero;
                    LogEngageStance(self, true, "持包压点遇敌：站定打完这一梭再压");
                }
                else
                {
                    intent.Move = _nav.ComputeMove(self.Position, plantAim, CsBotConst.PlantStopRadius, now,
                                                   hullHeight: self.Height);
                    LogEngageStance(self, false, "持包压点（本 tick 不扣扳机）");
                }
            }
            else if (retreating)
            {
                var away = HorizontalDir(target.Position, self.Position);
                away.y = 0f;
                if (away.sqrMagnitude < 0.5f) away = -HorizontalDir(self.Position, target.Position);
                var back = self.Position + away.normalized * CsBotConst.RetreatDistance;
                intent.Move = _nav.ComputeMove(self.Position, back, 0.5f, now,
                                               viaRoute: false, hullHeight: self.Height);
                RateWarn("engage.retreat",
                    $"{_name} 弹匣已空 → 后退换弹（距目标 {dist:F1}m，后退 {CsBotConst.RetreatDistance:F1}m，" +
                    $"武器 {self.ActiveWeapon ?? "无"}，换弹中不站定当靶子）");
            }
            else if (standToFire)
            {
                // 站定射击：要开枪就 `Move = zero`（速度靠模拟摩擦落到 0，开火门限等它落完）。
                // 远距离蹲下压低受弹面；近身站着，换位更快。
                intent.Move = Vector3.zero;
                intent.Crouch = dist > CsBotConst.EngageCrouchDistance;
                LogEngageStance(self, true, canFire
                    ? $"开火（距目标 {dist:F1}m）"
                    : "瞄点已收敛，等水平速度归零再开火");
            }
            else if (dist > PreferredRange() * CsBotConst.AdvanceRangeFactor)
            {
                // 太远且还没瞄上：压上去（看得见就追人，看不见就追最后所见位置）
                intent.Move = HorizontalDir(self.Position, knownPos);
                LogEngageStance(self, false, $"距离 {dist:F1}m 超出推进门槛，压上去");
            }
            else if (!_visibleNow)
            {
                // 目标仍在记忆期内但此刻看不见 → 压到"最后所见位置"去搜，而不是原地横移。
                var dLast = knownPos - self.Position;
                dLast.y = 0f;
                intent.Move = dLast.magnitude > CsBotConst.WaypointArriveRadius
                    ? dLast.normalized
                    : Vector3.zero;
                LogEngageStance(self, false, "追最后所见位置");
            }
            else
            {
                // 看得见、但瞄点还没收敛（视角还在转）⇒ 横向走位等转向；一旦瞄上就进上面的站定分支。
                intent.Move = Strafe(self, now);
                LogEngageStance(self, false, "瞄点未收敛 → 侧移等转向");
            }

            // 纯后退超过 0.6s ⇒ 改成横向侧移（不许整段背对目标倒着走）
            ApplyBackpedalCap(self, ref intent, now);

            // 近身倾向换手枪（< 3m），拉开后换回主武器（> 6m）：滞回 + 冷却，防来回切
            intent.SwitchTo = NearWeaponChoice(self, dist, now);

            SetState(CsBotState.Engage, now);
            intent.State = CsBotState.Engage;
            // 注意：**不要再写 `intent.AimPoint = point`** —— ApplyAimAndFire 已经落过瞄点，
            // 且在"刚丢视野的宽限"里它会把瞄点改成"最后所见位置"（这一行会把它覆盖回去，
            // 于是模拟把视角转向一个此刻根本打不到的点、朝向差永远收敛不到门限里）。
            return intent;
        }



        /// <summary>开火门限：本 tick 提交的瞄向与理想瞄点的夹角足够小才开火（"抬枪→开火"，而不是边转边扫）。</summary>
        private bool AimSettled(CsActor self, Vector3 aimPoint)
        {
            return SubmittedAimAngle(self, aimPoint) <= AimGate();
        }



        /// <summary>
        /// 本 tick 提交的瞄向（<see cref="SmoothAim"/> 的 <c>_aimSmoothDir</c>，也就是子弹要飞的方向）
        /// 与到 <paramref name="aimPoint"/> 的方向之间的夹角（度）。平滑尚未建立时退回角色当前朝向。
        /// </summary>
        private float SubmittedAimAngle(CsActor self, Vector3 aimPoint)
        {
            var to = aimPoint - self.EyePosition;
            if (to.sqrMagnitude < 0.0001f) return 0f;

            var dir = _aimSmoothActor == self.Id && _aimSmoothDir.sqrMagnitude > 0.5f
                ? _aimSmoothDir
                : Forward(self.Yaw, self.Pitch);
            return Vector3.Angle(dir, to.normalized);
        }



        /// <summary>当前朝向与 <paramref name="aimPoint"/> 的夹角（度）。与 <see cref="AimSettled"/> 同一算法，
        /// 单独拆出来是为了让"开火被抑制"的诊断日志能打出**实际角度**而不是只有一个布尔。</summary>
        private float AimAngle(CsActor self, Vector3 aimPoint)
        {
            var to = aimPoint - self.EyePosition;
            if (to.sqrMagnitude < 0.0001f) return 0f;
            return Vector3.Angle(Forward(self.Yaw, self.Pitch), to.normalized);
        }



        /// <summary>本难度的开火角度门限（度）：<c>clamp(AimErrorDegrees×scale + extra, min, max)</c>。</summary>
        private float AimGate()
        {
            return Mathf.Clamp(
                _profile.AimErrorDegrees * CsBotConst.AimGateErrorScale + CsBotConst.AimGateExtraDegrees,
                CsBotConst.AimGateMinDegrees, CsBotConst.AimGateMaxDegrees);
        }



        /// <summary>
        /// "瞄这个点 + 现在该不该扣扳机"的统一计算（交战时、以及冲包点途中警戒时共用）。
        ///
        /// <para>开火要同时满足：目标可见（本 tick 的 <c>_visibleNow</c>，或"刚丢视野"的宽限
        /// <c>profile.ReactionTime + CsConst.BotTickInterval</c>）、已经过了难度反应时间、
        /// 朝向已经收敛到瞄点（<see cref="AimSettled"/>）、**已经站定**（在地面且水平速度
        /// ≤ <see cref="CsBotConst.FireStandSpeed"/>）、没在换弹、没被闪光致盲。
        /// 连发节奏不在这里 —— 那是模拟内部按 <c>FireBurst*/FirePause*</c> 执行的。</para>
        ///
        /// <para><b>宽限为什么要存在</b>：逐 tick 判"这一眼看不见"（射线被队友/墙角一瞬打断）
        /// 会让扳机永远不扣。宽限只放行
        /// "把这次已经看见的反应走完"（反应时间 + 一个 tick），瞄点用**最后所见位置**（不预测、不外推，
        /// 子弹照样被物理射线挡在墙上），超过就照旧"看不见 = 不开枪"。</para>
        ///
        /// <para><b>每一条不满足都留证</b>：<see cref="LogFireDenied"/> 每 <see cref="CsBotConst.FireDiagInterval"/> 秒
        /// 打一行"可见 / 反应 / 朝向差 / 视线判定 / 弹匣 / 致盲"，所以"是哪一条恒假"从日志就能读出来，不用猜。</para>
        /// </summary>
        /// <returns>本次是否表达"想开火"。</returns>
        private bool ApplyAimAndFire(CsActor self, Vector3 aimPoint, float now, ref CsBotIntent intent)
        {
            // ---- 刚丢视野的宽限：目标仍在记忆期内、且"距上次看见 ≤ 刚够走完这次反应" → 仍按"可打"处理 ----
            //
            // 宽限 = 反应时间 + 一个决策 tick（**不是写死的秒数**）。理由：Easy 的反应时间是 0.65s ——
            // 若宽限短于反应时间，"只看了一眼就丢"的目标永远等不到 reacted 变真，宽限等于不存在。
            // 上限含义：把"这次已经看见了的反应"走完、把这一两发打出去，然后照旧"看不见 = 不开枪"。
            var graceSeconds = _profile.ReactionTime + CsConst.BotTickInterval;
            var sinceSeen = now - _lastSeenTime;
            // 宽限只覆盖"视线仍通、只是一 tick 没判到人"（被队友受体 / 逐帧遮挡抖动打断）：
            // 最后所见位置已经被墙挡住时开火必然是空枪，那几发只会计进分母。
            var graceLineClear = !_visibleNow && _lastSeenPos.sqrMagnitude > 0.0001f &&
                                 _match.HasLineOfSight(self.EyePosition, _lastSeenPos, _profile.VisionRange);
            var grace = !_visibleNow
                        && _targetId != 0
                        && _lastSeenTime > 0f
                        && sinceSeen <= graceSeconds
                        && graceLineClear;

            // 看不见目标时一律瞄"最后所见位置"（不只宽限期内）：否则会隔着墙把准星钉在敌人的真实位置上，
            // 人一露头就已经被预瞄好了 —— 那是玩家感觉得到的"透视"。
            // 位置再叠一段目标速度外推（上限 CsBotConst.PredictLastSeenMaxSeconds）——
            // 目标转向 / 停步之后外推量会随"离开视野时长"失效，所以按离开时长线性增长、封顶。
            if (!_visibleNow && _lastSeenPos.sqrMagnitude > 0.0001f) aimPoint = LastSeenAimPoint(now);

            intent.AimPoint = aimPoint;

            var reacted = now - _firstSeenTime >= _profile.ReactionTime;
            // 夹角要量**这一发真正会打出去的方向**：子弹沿提交给模拟的瞄点方向飞（<see cref="SmoothAim"/>
            // 之后的 `_aimSmoothDir`），不是角色此刻的朝向 —— 朝向由模拟按难度的转视角速度追，通常落后
            // 十几度，用朝向量会把"还在转视角"当成"已经瞄准"，于是子弹沿偏了十几度的方向打进墙里。
            var angle = SubmittedAimAngle(self, aimPoint);
            var gate = AimGate();

            // 交战瞄准的俯仰角门限：相对地平线 |pitch| ≤ 45°（本项目新增；原版视角上限 89°，交战瞄准不应超过 45°）。
            // 超过即视为未锁定、不开火 —— 配合 HeadWobbleOffset 的竖直夹取，堵死"朝天开枪"。
            var to = aimPoint - self.EyePosition;
            var aimPitchDeg = to.sqrMagnitude < 0.0001f
                ? 0f
                : Mathf.Asin(Mathf.Clamp(to.y / to.magnitude, -1f, 1f)) * Mathf.Rad2Deg;
            var levelEnough = Mathf.Abs(aimPitchDeg) <= CsBotConst.AimPitchLimitDegrees;

            // 站定才扣扳机：子弹散布按水平速度放大（`Firearm.ComputeSpread` 的 MoveFactor 与档位判据），
            // 速度没落下来之前开枪就是把子弹打飞。与 Engage 的"要开枪就 Move = zero"是同一条约束的两端：
            // 那一端让 bot 停下来，这一端保证"停下来之前不扣扳机"。
            var hv = self.Velocity;
            var speed = new Vector2(hv.x, hv.z).magnitude;
            var planted = self.OnGround && speed <= CsBotConst.FireStandSpeed;

            // 开火前用**这一发真正要打的那个点**（已含预瞄外推 / 头部摆动）沿弹道验一次：
            // 感知判的是"眼位看得见"，而子弹打的是瞄点 —— 半掩体 / 矮墙后眼位可见、胸部不可见时，
            // 开枪就是把子弹打进墙里（自然对局实测：77% 的弹着点是墙 / 地面）。
            // 判据只看"弹道上第一个挡路的东西是不是目标"：是目标 ⇒ 这一发能打中；
            // 是墙 / 道具 / 队友 ⇒ 这一发必然打空（队友会吃子弹，等于白打）。
            var aimClear = (_visibleNow || grace) && AimPathClear(self, aimPoint, out _);

            var canFire = (_visibleNow || grace) && reacted && angle <= gate && levelEnough && planted && aimClear;

            if (grace && canFire)
            {
                _graceFireCount++;
                if (now >= _nextGraceLogAt)
                {
                    _nextGraceLogAt = now + CsBotConst.FireDiagInterval;
                    Game.Logger.Info(Tag,
                        $"{_name}（{_difficulty}）靠「刚丢视野宽限」开火（第 {_graceFireCount} 次）：" +
                        $"距上次看见 {sinceSeen:F2}s ≤ {graceSeconds:F2}s" +
                        $"（= 反应 {_profile.ReactionTime:F2}s + tick {CsConst.BotTickInterval:F2}s），瞄最后所见位置");
                }
            }

            // 交火节奏：远距只打 2~4 发点射、串间停火保持指向；近身放开连发。
            // 停火段里 `AimPoint` 照旧每 tick 更新（准星不离开目标），只是不扣扳机。
            var def = self.ActiveDef;
            var targetDist = TargetDistance(self);
            var secondsPerShot = def != null ? def.SecondsPerShot : 0f;
            if (canFire && !BurstAllowsFire(now, targetDist, secondsPerShot)) canFire = false;

            // 远距只打"慢目标"：提前量是按当前速度外推的，目标在跑动中变向 / 加减速时这条外推立刻失效，
            // 而在 <see cref="CsBotConst.FarFireRange"/> 之外，同样的角度偏差在横向已经超过一个身位 ⇒
            // 这一发几乎必空（弹着点是墙 / 地面）。这种情况下不扣扳机：压上去或等它停，别用空枪换暴露位置。
            if (canFire && targetDist > CsBotConst.FarFireRange)
            {
                var moving = _match.Find(_targetId);
                if (moving != null && moving.IsAlive)
                {
                    var tv = moving.Velocity;
                    tv.y = 0f;
                    if (tv.magnitude > CsBotConst.FarFireMaxTargetSpeed) canFire = false;
                }
            }

            // 弹药空了 → 请求换弹（模拟内部有完整校验）
            if (def != null && def.Magazine > 0 && self.GetAmmo(def.Id).inMag <= 0)
            {
                intent.Reload = true;
                canFire = false;
            }

            // 弹匣见底但还没打完：没有可见敌、或敌人在安全距离外 ⇒ 现在换，别等打空了在交火中换
            var ammoNow = def != null && def.Magazine > 0 ? self.GetAmmo(def.Id) : (inMag: 0, reserve: 0);
            if (def != null && def.Magazine > 0 &&
                ammoNow.inMag > 0 && ammoNow.reserve > 0 &&
                (float)ammoNow.inMag / def.Magazine < CsBotConst.ReloadLowMagRatio &&
                (!_visibleNow || targetDist > CsBotConst.ReloadSafeDistance) &&
                self.ReloadEndTime <= 0f)
            {
                intent.Reload = true;
                canFire = false;
            }

            // 被闪光弹致盲时不打（模拟只记致盲时间，不下降命中率 —— 这里补上行为）
            if (now < self.FlashEndTime) canFire = false;

            if (!canFire) LogFireDenied(self, now, reacted, angle, gate, aimPitchDeg, sinceSeen, grace, speed, aimClear);

            intent.Fire = canFire;
            if (canFire) LastFireIntentTime = now;
            return canFire;
        }



        /// <summary>
        /// "这一 tick 决定不开火"的诊断：把三条门限**连同数值**打出来 ——
        /// 这就是"到底哪一条恒假"的原始证据（降频：每 bot 每 <see cref="CsBotConst.FireDiagInterval"/> 秒一条，首次必打）。
        /// </summary>
        private void LogFireDenied(CsActor self, float now, bool reacted, float angle, float gate,
            float aimPitchDeg, float sinceSeen, bool grace, float speed, bool aimClear)
        {
            // 没有交战目标（例如冲包点路上的警戒开火）时的"不开火"是**预期行为**，不是异常分支 → 不记
            if (_targetId == 0) return;

            _fireDeniedCount++;
            if (now < _nextFireDiagAt) return;
            _nextFireDiagAt = now + CsBotConst.FireDiagInterval;

            var sight = _lastSight;
            var blocker = _lastSightBlocker;
            var target = _match.Find(_targetId);
            if (target != null && !target.IsAlive) target = null;

            if (!_visibleNow && target != null)
            {
                sight = BotSight.Check(_match, self.EyePosition, target.EyePosition, _profile.VisionRange,
                    out blocker, out _);
            }

            // 手里到底有没有枪 / 弹匣与备弹（"扣了扳机却没扣出子弹"的另一条死路：没枪 / 弹匣空且备弹也用光）
            var def = self.ActiveDef;
            var mag = -1;
            var reserve = -1;
            if (def != null)
            {
                var ammo = self.GetAmmo(def.Id);
                mag = ammo.inMag;
                reserve = ammo.reserve;
            }

            Game.Logger.Info(Tag,
                $"{_name}（{_difficulty}）交战中开火被抑制第 {_fireDeniedCount} 次：目标=actor {_targetId}" +
                $" 可见={_visibleNow}（距上次看见 {sinceSeen:F2}s）" +
                $" 反应={(reacted ? "过" : "未过")}（{now - _firstSeenTime:F2}/{_profile.ReactionTime:F2}s）" +
                $" 朝向差={angle:F1}° vs 门限={gate:F1}°" +
                $" 俯仰={aimPitchDeg:F1}° vs 上限={CsBotConst.AimPitchLimitDegrees:F0}°" +
                $" 水平速度={speed:F2}m/s vs 站定上限={CsBotConst.FireStandSpeed:F2}m/s" +
                $"（{(self.OnGround ? "在地面" : "离地")}）" +
                $" 视线={BotSight.Text(sight)}{(string.IsNullOrEmpty(blocker) ? string.Empty : $"（挡={blocker}）")}" +
                $" 瞄点视线={(aimClear ? "通" : "被挡")}" +
                $"{(grace ? "（宽限内）" : string.Empty)}" +
                $" 武器={self.ActiveWeapon ?? "无"} 弹匣={mag} 备弹={reserve}" +
                $" 致盲剩余={Mathf.Max(0f, self.FlashEndTime - now):F2}s");
        }



        /// <summary>本轮这一枪瞄不瞄头（按 <c>HeadshotChance</c> 掷，整段交战保持同一选择）。</summary>
        private bool ChooseHeadshot(float now)
        {
            if (now >= _headRollAt)
            {
                _headRollAt = now + CsBotConst.HeadshotRollSeconds;
                // 瞄不瞄头：**玩法**（直接改伤害与击杀数）。走本 bot 的 BotAimFlavor 流。
                _headRoll = _rngAim.NextFloat() < _profile.HeadshotChance;
            }
            return _headRoll;
        }



        private Vector3 HeadWobbleOffset(CsActor self, Vector3 point, float now)
        {
            if (now >= _wobbleRefreshAt)
            {
                _wobbleRefreshAt = now + CsBotConst.AimWobbleRefreshSeconds;
                // 头部摆动：**玩法**（它是瞄准偏移，直接进 AimPoint）。同一个 BotAimFlavor 流。
                _wobble = new Vector2(_rngAim.Range(-1f, 1f), _rngAim.Range(-1f, 1f));
            }

            if (Mathf.Approximately(_wobble.x, 0f) && Mathf.Approximately(_wobble.y, 0f)) return Vector3.zero;

            var to = point - self.EyePosition;
            var dist = to.magnitude;
            if (dist < 0.01f) return Vector3.zero;

            var dir = to / dist;
            var right = Vector3.Cross(Vector3.up, dir);
            if (right.sqrMagnitude < 0.0001f) right = Vector3.right;
            right.Normalize();

            var magDeg = _profile.AimErrorDegrees * CsBotConst.HeadAimExtraErrorScale;
            var scale = dist * Mathf.Tan(magDeg * Mathf.Deg2Rad);
            var offset = (right * _wobble.x + Vector3.up * _wobble.y) * scale;

            // 竖直分量夹到 ±0.3 米（本项目新增；CS 1.6 头部半径约 0.1m 量级，夹住防止瞄点飞出头部区域——
            // 原实现无界：远处 scale = dist×tan(AimError×HeadAimExtraErrorScale) 可达数十度仰角 ⇒ 朝天开枪）。水平分量保留。
            return new Vector3(offset.x, Mathf.Clamp(offset.y, -0.3f, 0.3f), offset.z);
        }



        /// <summary>
        /// 可见目标的预瞄提前量（秒）= 半个决策 tick（见 <see cref="CsBotConst.PredictTickLeadSeconds"/>）
        /// + 按难度递增的提前量（0 @ Normal 速度 → <see cref="CsBotConst.PredictSecondsMax"/> @ Hard），
        /// 总数夹在 <see cref="CsBotConst.PredictLastSeenMaxSeconds"/> 以内。
        /// </summary>
        private float PredictSeconds()
        {
            var t = Mathf.InverseLerp(CsBotConst.PredictSkillSpeedLo, CsBotConst.PredictSkillSpeedHi, _profile.AimSpeedDegrees);
            return Mathf.Min(CsBotConst.PredictTickLeadSeconds + CsBotConst.PredictSecondsMax * t,
                             CsBotConst.PredictLastSeenMaxSeconds);
        }



        /// <summary>
        /// 目标不在视野里时的瞄点 = 最后所见位置 + 目标水平速度 × min(离开视野时长,
        /// <see cref="CsBotConst.PredictLastSeenMaxSeconds"/>)。
        /// 离开视野越久，外推越不可信 ⇒ 外推时长按离开时长封顶，而不是一直按同一个提前量推。
        /// </summary>
        private Vector3 LastSeenAimPoint(float now)
        {
            var point = _lastSeenPos;
            var target = _targetId != 0 ? _match.Find(_targetId) : null;
            if (target == null || !target.IsAlive) return point;

            var lead = Mathf.Clamp(now - _lastSeenTime, 0f, CsBotConst.PredictLastSeenMaxSeconds);
            if (lead <= 0.0001f) return point;

            var v = target.Velocity;
            return point + new Vector3(v.x, 0f, v.z) * lead;
        }



        /// <summary>
        /// 沿"眼睛 → 瞄点"这条弹道，第一个挡路的是不是**当前目标**。
        ///
        /// <para>与 <c>ICsMatch.HasLineOfSight</c> 的区别：那个函数把"命中点比终点近一个玩家半径"一律判挡，
        /// 于是瞄胸（终点在目标体内）会被目标自己的受体判成遮挡；这里按**弹道实际会先打到谁**判：
        /// 目标自己的受体 ⇒ 通；墙 / 地面 / 道具 / 队友 ⇒ 挡。自己的受体在抬手方向内，跳过。</para>
        /// </summary>
        private bool AimPathClear(CsActor self, Vector3 aimPoint, out string blocker)
        {
            blocker = null;

            var from = self.EyePosition;
            var delta = aimPoint - from;
            var dist = delta.magnitude;
            if (dist <= 0.01f) return true;

            var count = Physics.RaycastNonAlloc(from, delta / dist, _aimRay, dist, ~0,
                                                QueryTriggerInteraction.Ignore);
            if (count >= _aimRay.Length)
            {
                // 缓冲被打满 ⇒ 更远的命中会被丢掉（可能漏掉真正的挡点）。保守判挡并留痕。
                RateWarn("bot.aimpath.bufferfull",
                    $"{_name} 的弹道检测命中数达到缓冲上限 {_aimRay.Length} → 本次判为视线被挡");
                blocker = "命中缓冲已满";
                return false;
            }

            var best = float.MaxValue;
            long bestActor = 0;
            string bestName = null;

            for (var i = 0; i < count; i++)
            {
                var h = _aimRay[i];
                var proxy = h.collider != null ? h.collider.GetComponentInParent<CsHitboxProxy>() : null;
                var actorId = proxy != null ? proxy.ActorId : 0;
                if (proxy != null && actorId == self.Id) continue;      // 自己
                if (h.distance >= best) continue;

                best = h.distance;
                bestActor = actorId;
                bestName = h.collider != null ? h.collider.name : "（collider=null）";
            }

            if (best == float.MaxValue) return true;                    // 弹道打不到任何东西
            if (bestActor != 0 && bestActor == _targetId) return true;  // 第一个挡路的就是目标

            blocker = bestActor != 0 ? $"角色 actor {bestActor}" : bestName;
            return false;
        }



        /// <summary>当前交战目标到自身眼睛的距离（米）；没有存活目标 = <c>float.MaxValue</c>。</summary>
        private float TargetDistance(CsActor self)
        {
            var target = _targetId != 0 ? _match.Find(_targetId) : null;
            return target != null && target.IsAlive
                ? Vector3.Distance(self.EyePosition, target.EyePosition)
                : float.MaxValue;
        }



        /// <summary>
        /// 点射闸门：距离 ≤ <see cref="CsBotConst.BurstAutoRange"/> 时放开连发；更远时按
        /// "每串 <see cref="CsBotConst.BurstMinRounds"/>~<see cref="CsBotConst.BurstMaxRounds"/> 发、
        /// 串间停 <see cref="CsBotConst.BurstPauseSeconds"/> 秒"的节奏闸门。
        /// 每串时长 = 发数 × 武器 <see cref="CsWeaponDef.SecondsPerShot"/>（射速，不是自定的字段时间）。
        /// <paramref name="secondsPerShot"/> ≤ 0（射速未知）时不加闸门。
        /// </summary>
        /// <returns>本 tick 是否允许扣扳机。</returns>
        private bool BurstAllowsFire(float now, float dist, float secondsPerShot)
        {
            if (dist <= CsBotConst.BurstAutoRange) return true;
            if (secondsPerShot <= 0f) return true;
            if (now < _burstResumeAt) return false;
            if (now < _burstStopAt) return true;

            // 越远串越短：15m 打满 4 发，30m 外只打 2 发。
            var t = Mathf.InverseLerp(CsBotConst.BurstRoundsDropRange, CsBotConst.BurstFarRange, dist);
            var rounds = Mathf.RoundToInt(Mathf.Lerp(CsBotConst.BurstMinRounds, CsBotConst.BurstMaxRounds, t));
            var burstSeconds = Mathf.Max(rounds, 1) * secondsPerShot;

            _burstStopAt = now + burstSeconds;
            _burstResumeAt = _burstStopAt + CsBotConst.BurstPauseSeconds;
            return true;
        }



        /// <summary>
        /// 主动换弹的条件：弹匣低于 <see cref="CsBotConst.ReloadLowMagRatio"/>、有备弹、没有在换弹，
        /// 且（看不见敌人 或 敌人在 <see cref="CsBotConst.ReloadSafeDistance"/> 之外）。
        /// 判据只看可观测状态（弹匣 / 备弹 / 可见性 / 距离），不猜"等会儿会不会打起来"。
        /// </summary>
        private bool ShouldReloadNow()
        {
            var self = _tickSelf;
            if (self == null || !self.IsAlive) return false;

            var def = self.ActiveDef;
            if (def == null || def.Magazine <= 0) return false;
            if (self.ReloadEndTime > 0f) return false;

            var ammo = self.GetAmmo(def.Id);
            if (ammo.inMag <= 0 || ammo.reserve <= 0) return false;
            if ((float)ammo.inMag / def.Magazine >= CsBotConst.ReloadLowMagRatio) return false;

            return !_visibleNow || TargetDistance(self) > CsBotConst.ReloadSafeDistance;
        }



        /// <summary>
        /// 换弹叶子的动作：提交换弹、不扣扳机、朝掩体方向后退（不在开阔地站着换弹），
        /// 瞄点保持在最后所见方向 —— 换弹结束立刻能接战。
        /// </summary>
        private void ReloadStep(CsActor self, ref CsBotIntent intent, float now)
        {
            intent.Reload = true;
            intent.Fire = false;
            intent.Crouch = false;
            intent.Walk = false;

            var target = _targetId != 0 ? _match.Find(_targetId) : null;
            var threat = target != null && target.IsAlive
                ? target.Position
                : (_lastSeenPos.sqrMagnitude > 0.0001f ? _lastSeenPos : Vector3.zero);

            if (threat.sqrMagnitude > 0.0001f)
            {
                // 敌人可能还看得见（远距换弹）⇒ 边退到掩体边换；看不见就原地换，别为了躲不存在的枪口撞墙。
                intent.Move = _visibleNow ? CoverMove(self, threat, now) : Vector3.zero;
                intent.AimPoint = target != null && target.IsAlive ? target.EyePosition : threat;
            }
            else
            {
                intent.Move = Vector3.zero;
                intent.AimPoint = LookAhead(self);
            }

            SetState(CsBotState.Engage, now);
            intent.State = CsBotState.Engage;
        }



        /// <summary>
        /// 残血后撤条件：血量 &lt; <see cref="CsBotConst.RetreatHealthBelow"/>、护甲 &lt;
        /// <see cref="CsBotConst.RetreatArmorBelow"/>、目标在 <see cref="CsBotConst.RetreatTriggerRange"/>
        /// 之外（近距离撤离等于把背身交出去，所以近战不退）。
        /// </summary>
        private bool RetreatDue()
        {
            var self = _tickSelf;
            if (self == null || !self.IsAlive) return false;
            if (self.Health >= CsBotConst.RetreatHealthBelow) return false;
            if (self.Armor >= CsBotConst.RetreatArmorBelow) return false;
            return TargetDistance(self) > CsBotConst.RetreatTriggerRange;
        }



        /// <summary>
        /// 残血后撤叶子的动作：进入后撤窗口（<see cref="CsBotConst.RetreatHoldSeconds"/>）后朝
        /// "远离目标 + 最近掩体"的落点移动、期间不扣扳机；窗口结束返回 <c>Success</c>
        /// （外层 <see cref="BtCooldown"/> 据此开始冷却）。
        /// </summary>
        private BtStatus RetreatStep(CsActor self, ref CsBotIntent intent, float now)
        {
            if (_retreatUntil <= 0f)
            {
                _retreatUntil = now + CsBotConst.RetreatHoldSeconds;
                ChooseRetreatGoal(self);
                LogRetreat(self, now, "开始后撤");
            }

            intent.Fire = false;
            intent.Crouch = false;
            intent.Walk = false;
            intent.AimPoint = _lastSeenPos.sqrMagnitude > 0.0001f ? _lastSeenPos : LookAhead(self);
            intent.Move = _retreatGoalValid
                ? _nav.ComputeMove(self.Position, _retreatGoal, 0.5f, now,
                                   viaRoute: false, hullHeight: self.Height)
                : CoverMove(self, _lastSeenPos.sqrMagnitude > 0.0001f ? _lastSeenPos : self.Position, now);

            SetState(CsBotState.Engage, now);
            intent.State = CsBotState.Engage;

            if (now < _retreatUntil) return BtStatus.Running;

            _retreatUntil = 0f;
            _retreatGoalValid = false;
            LogRetreat(self, now, "后撤结束，重新交战");
            return BtStatus.Success;
        }



        /// <summary>
        /// 在"远离目标"方向的扇面里挑后撤落点：候选点必须在
        /// <see cref="CsBotConst.RetreatDistance"/> 处可站立（<c>ICsMap.CanStand</c>），
        /// 评分 = 到目标的水平距离 + 掩体权重（候选点到目标眼睛的视线被挡 = 有掩体）。
        /// </summary>
        private void ChooseRetreatGoal(CsActor self)
        {
            _retreatGoalValid = false;
            if (_lastSeenPos.sqrMagnitude < 0.0001f) return;

            if (!TryChooseCoverPoint(self, _lastSeenPos, out var point)) return;
            _retreatGoal = point;
            _retreatGoalValid = true;
        }



        /// <summary>在"远离 <paramref name="threat"/>"的扇面里挑一个可站立、且优先能挡视线的落点。</summary>
        private bool TryChooseCoverPoint(CsActor self, Vector3 threat, out Vector3 point)
        {
            point = self.Position;
            var away = HorizontalDir(threat, self.Position);
            if (away.sqrMagnitude < 0.0001f) away = -Forward(self.Yaw, 0f);

            var bestScore = float.MinValue;
            var found = false;

            for (var i = -CsBotConst.RetreatCoverSteps; i <= CsBotConst.RetreatCoverSteps; i++)
            {
                var dir = Quaternion.Euler(0f, i * CsBotConst.RetreatCoverStepDegrees, 0f) * away;
                var cand = self.Position + dir * CsBotConst.RetreatDistance;
                if (_map == null || !_map.CanStand(cand, CsConst.PlayerRadius, self.Height)) continue;

                var candEye = cand + Vector3.up * CsConst.EyeHeight;
                var covered = !_match.HasLineOfSight(candEye, threat, CsConst.BotVisionRange * 2f);
                var gap = HorizontalDistance(cand, threat);
                var score = gap + (covered ? CsBotConst.RetreatCoverWeightMeters : 0f);

                if (found && score <= bestScore) continue;
                bestScore = score;
                point = cand;
                found = true;
            }

            return found;
        }



        /// <summary>后撤的现场日志：血量 / 护甲 / 距目标 / 落点，起止各一条（每 bot 降频）。</summary>
        private void LogRetreat(CsActor self, float now, string phase)
        {
            var target = _targetId != 0 ? _match.Find(_targetId) : null;
            var dist = target != null && target.IsAlive ? Vector3.Distance(self.Position, target.Position) : -1f;
            RateWarn("engage.retreat.lowhp",
                $"{_name}（{_difficulty}）{phase}：HP {self.Health} / 护甲 {self.Armor}，" +
                $"距目标 {dist:F1}m（触发门槛 HP<{CsBotConst.RetreatHealthBelow} 护甲<" +
                $"{CsBotConst.RetreatArmorBelow} 距离>{CsBotConst.RetreatTriggerRange:F0}m），" +
                $"落点=({_retreatGoal.x:F1},{_retreatGoal.z:F1})，窗口 {CsBotConst.RetreatHoldSeconds:F1}s 内不开火");
        }



        /// <summary>朝"远离 <paramref name="threat"/> 且能挡视线"的方向走一步（没有可用落点时退回直线远离）。</summary>
        private Vector3 CoverMove(CsActor self, Vector3 threat, float now)
        {
            if (TryChooseCoverPoint(self, threat, out var point))
            {
                var move = _nav.ComputeMove(self.Position, point, 0.5f, now,
                                            viaRoute: false, hullHeight: self.Height);
                if (move.sqrMagnitude > 0.0001f) return move;
            }

            var away = HorizontalDir(threat, self.Position);
            return away.sqrMagnitude > 0.0001f ? away : Vector3.zero;
        }



        /// <summary>水平距离（米，忽略高度差）。</summary>
        private static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            var d = b - a;
            d.y = 0f;
            return d.magnitude;
        }



        /// <summary>
        /// 交战中的横向走位（"走位"）。
        ///
        ///
        /// <para><b>出处（不是本项目自创）</b>：规格 <c>策划/策划案/CS1.6单机参考规格.md</c>
        /// —— "走位"是**三档共有的行为**，档位差别只在参数上。本方法里随档位变化的参数本来就有
        /// （<c>period = max(0.6, RepathInterval)</c>：Easy 1.6s / Normal 1.0s / Hard 0.5s ⇒ 换向节奏不同），
        /// 所以修法 = **去掉"低难度不产生走位"这个非参数性的门**，把三档交回同一个行为。</para>
        /// </summary>
        private Vector3 Strafe(CsActor self, float now)
        {
            var period = Mathf.Max(0.6f, _profile.RepathInterval);
            if (now >= _strafeFlipAt)
            {
                _strafeFlipAt = now + period;
                _strafeSign = -_strafeSign;
            }

            var yawRad = self.Yaw * Mathf.Deg2Rad;
            var right = new Vector3(Mathf.Cos(yawRad), 0f, -Mathf.Sin(yawRad));
            return right * _strafeSign;
        }



        /// <summary>
        /// 交战走位的现场日志（按 <see cref="CsBotConst.StateLogMinInterval"/> 限频）：
        /// 这一 tick 为什么站着不动（<c>engage.stand</c>）或为什么还在走（<c>engage.move</c>）。
        /// 站定的三个原因 = 开火 / 瞄点收敛等速度归零 / 持包遇敌；移动的原因 = 压点 / 换弹后撤 / 压上 / 追最后所见 / 侧移等转向。
        /// </summary>
        private void LogEngageStance(CsActor self, bool standing, string why)
        {
            var key = "bot.engage." + (standing ? "stand" : "move") + ":" + _name;
            if (!CloverEngine.LogThrottle.ShouldLog(key, CsBotConst.StateLogMinInterval)) return;

            var v = self != null ? self.Velocity : Vector3.zero;
            var speed = new Vector2(v.x, v.z).magnitude;
            Game.Logger.Info(Tag,
                $"engage.{(standing ? "stand" : "move")} {_name}（{_difficulty}）" +
                $"水平速度 {speed:F2}m/s 蹲={(self != null && self.IsCrouching)} 原因={why}");
        }



        private string NearWeaponChoice(CsActor self, float dist, float now)
        {
            if (now < _switchCooldownUntil) return null;

            var primary = self.PrimaryWeapon;
            var secondary = self.SecondaryWeapon;
            if (string.IsNullOrEmpty(primary)) return null;

            if (dist < CsBotConst.MeleeSwitchRange)
            {
                if (!string.IsNullOrEmpty(secondary) && self.ActiveWeapon != secondary)
                {
                    _switchCooldownUntil = now + CsBotConst.WeaponSwitchCooldown;
                    return secondary;
                }
                return null;
            }

            // 手上有狙且目标在远距：优先主武器（远距离用手枪既打不准也压不住）。
            // 与下面的"拉开距离换回主武器"同一条滞回，只是门槛更远、且只在主武器是狙时生效。
            if (dist > CsBotConst.SniperRange && self.ActiveWeapon != primary)
            {
                var primaryDef = CsWeapons.Get(primary);
                if (primaryDef != null && primaryDef.Class == CsWeaponClass.Sniper)
                {
                    _switchCooldownUntil = now + CsBotConst.WeaponSwitchCooldown;
                    return primary;
                }
            }

            if (dist > CsBotConst.MeleeBackRange && self.ActiveWeapon != primary)
            {
                _switchCooldownUntil = now + CsBotConst.WeaponSwitchCooldown;
                return primary;
            }

            return null;
        }



        // ==================================================================
        //  投掷物（角色分工）
        // ==================================================================
        /// <summary>
        /// 本 tick 是否提交"投掷"意图（true = 已被投掷占用，调用方直接返回该意图）。
        ///
        /// <para><b>为什么放在顶层分支之前</b>：投掷要么用来"开打前先炸/闪"，要么在推进途中朝目标点投
        /// （到点封烟、冲点前清角）—— 只挂在交战分支里会漏掉后一种，只挂在推进里会漏掉前一种。</para>
        ///
        /// <para><b>投什么</b>：按角色偏好表（<see cref="CsBotRoles.PreferredGrenades"/>）取**手上有的**第一种：
        /// 突破 = 高爆 → 闪光，支援 = 高爆 → 烟，侦察 = 闪光 → 烟，守点 = 烟 → 高爆。
        /// 出处分段见 <see cref="CsBotRoles.PreferredGrenades"/>（三种雷都在原版 mp.dll 的 bot 购买序列里，
        /// "哪个角色先拿哪一种"本项目新增）。</para>
        ///
        /// <para><b>该不该投</b>：目标（敌人 / 包点 / 本轮目标点）的水平距离必须 ≥ 该类型的下界，
        /// 落点取 <c>min(距离, GrenadeMaxRange)</c>（见 <see cref="TryAimWithinReach"/>）——
        /// 投掷在本工程里是"沿瞄向给初速 <c>CsConst.GrenadeThrowSpeed(仰角)</c> + 重力"，最远 ≈ 10.04m（仰角 ≈ 55°），
        /// 更远的目标物理上扔不到 ⇒ 落在朝它方向的最远处；
        /// 下界按类型取（烟雾 = <see cref="CsBotConst.GrenadeMinRange"/>，
        /// 高爆/闪光 = <see cref="CsBotConst.GrenadeSelfSafeRange"/>，即"自己不在爆心半径里"）。</para>
        ///
        /// <para><b>切雷与转视角都要等</b>：切枪、引信、爆炸都在模拟里，本类只能"等换手（
        /// <see cref="GrenadeSwitchTimeout"/> 兜底）→ 等视角转到瞄点（<see cref="CsBotConst.GrenadeAimGateDegrees"/>）
        /// → 扣扳机"。</para>
        /// </summary>
        private bool TryThrowGrenade(CsActor self, ref CsBotIntent intent, float now)
        {
            // 手上还剩同一种雷时，投完那一颗模拟不会自动换回枪（CsInventory.ThrowGrenade 只在"这一类归零"时
            //   才 SelectBestWeapon）⇒ 这里补一次"换回主武器"，否则机器人抱着手雷站着不开枪。
            if (_grenadeReturnToWeapon)
            {
                var back = !string.IsNullOrEmpty(self.PrimaryWeapon) ? self.PrimaryWeapon : self.KnifeWeapon;
                if (string.IsNullOrEmpty(back) || self.ActiveWeapon == back)
                {
                    _grenadeReturnToWeapon = false;
                }
                else
                {
                    intent.SwitchTo = back;
                    intent.Fire = false;
                    intent.Move = Vector3.zero;
                    intent.AimPoint = _goalValid ? WithEyeHeight(_goalPos) : LookAhead(self);
                    return true;
                }
            }

            // 正在下包/拆包时不许打断（一移动进度就清零，与模拟判定一致）；投满上限也不再投。
            if (self.UseProgress >= 0f || _grenadeThrows >= CsBotConst.GrenadeMaxPerRound)
            {
                if (self.UseProgress >= 0f)
                {
                    NadeDiag(self, "在下包拆包", $"正在下包/拆包（进度 {self.UseProgress:P0}），本轮投掷让位");
                }
                else
                {
                    NadeDiag(self, "已投满", $"本回合已投 {_grenadeThrows} 颗（上限 {CsBotConst.GrenadeMaxPerRound}）");
                }
                ClearGrenadeAttempt();
                return false;
            }

            if (_grenadePendingId == null && !TryBeginGrenade(self, now)) return false;

            if (!string.IsNullOrEmpty(_grenadePendingId) && self.ActiveWeapon != _grenadePendingId)
            {
                if (now > _grenadeSwitchDeadline)
                {
                    RateWarn("nade.switch.timeout",
                        $"{_name} 切到 {_grenadePendingId} 超过 {CsBotConst.GrenadeSwitchTimeout:F1}s 仍未换手 → 放弃这次投掷");
                    NadeDiag(self, "切雷超时", $"切到 {_grenadePendingId} 超过 {CsBotConst.GrenadeSwitchTimeout:F1}s 仍未换手");
                    _nextGrenadeAt = now + CsBotConst.GrenadeThrowSpacing;
                    ClearGrenadeAttempt();
                    return false;
                }

                intent.SwitchTo = _grenadePendingId;
                intent.Fire = false;
                intent.Move = Vector3.zero;
                intent.AimPoint = _grenadeAim;
                return true;
            }

            intent.SwitchTo = null;
            intent.Move = Vector3.zero;
            intent.Crouch = false;
            intent.AimPoint = _grenadeAim;

            // 切枪动作由模拟执行：ActiveWeapon 立即变成那颗雷，但 SwitchEndTime 之前扣扳机会被拒
            // （CsInventory.TryDischarge 的 `now < a.SwitchEndTime`），所以这里要等换手完成。
            if (now < self.SwitchEndTime || AimAngle(self, _grenadeAim) > CsBotConst.GrenadeAimGateDegrees)
            {
                // 等不出来的情况必须留痕并放弃：否则机器人会抱着手雷一直"等视角"，既不开枪也不移动。
                if (now > _grenadeSwitchDeadline)
                {
                    NadeDiag(self, "视角未收敛",
                        $"切到 {_grenadePendingId} 后视角 {AimAngle(self, _grenadeAim):F1}° 未收敛" +
                        $"（门限 {CsBotConst.GrenadeAimGateDegrees:F0}°）→ 放弃本次投掷");
                    _nextGrenadeAt = now + CsBotConst.GrenadeThrowSpacing;
                    ClearGrenadeAttempt();
                    return false;
                }

                intent.Fire = false;
                return true;
            }

            // 释放前按**当前实际朝向**再预测一次落点：决定飞行方向的是 a.Yaw/a.Pitch（模拟按它给初速），
            // 不是规划瞄点 `_grenadeAim`。视角仍在往瞄点收敛时两者能差一个收敛门限，
            // 只在"决定投掷"那一刻检查一次会漏掉这个差。
            var actualAim = self.EyePosition + Forward(self.Yaw, self.Pitch) * CsBotConst.AimFallbackDistance;
            var actualLanding = PredictGrenadeLanding(self, actualAim);
            var actualGap = Vector3.Distance(self.Position, actualLanding);
            if (_grenadePendingId != CsWeapons.SmokeGrenade && actualGap < CsBotConst.GrenadeSelfSafeRange)
            {
                NadeDiag(self, "释放时落点太近",
                    $"释放瞬间的朝向会把雷扔到 ({actualLanding.x:F1},{actualLanding.y:F1},{actualLanding.z:F1})，" +
                    $"距自己 {actualGap:F1}m < 爆心半径 {CsBotConst.GrenadeSelfSafeRange:F1}m " +
                    $"→ 本次放弃（{CsBotConst.GrenadeSelfRetrySeconds:F0}s 后重试）");
                _nextGrenadeAt = now + CsBotConst.GrenadeSelfRetrySeconds;
                ClearGrenadeAttempt();
                return false;
            }

            intent.Fire = true;
            _grenadeThrows++;
            _nextGrenadeAt = now + CsBotConst.GrenadeThrowSpacing;
            // 这一颗投出去后手上还剩同类雷（如闪光弹上限 2 颗）⇒ 下一 tick 要主动换回主武器。
            _grenadeReturnToWeapon = CountGrenade(self, _grenadePendingId) - 1 > 0;

            var def = CsWeapons.Get(_grenadePendingId);
            var mateText = _grenadeMateDist < float.MaxValue ? $"{_grenadeMateDist:F1}m" : "无";
            Game.Logger.Info(Tag,
                $"{_name}({self.Team}/{CsBotRoles.Label(_role)}) 投掷 {def?.DisplayName ?? _grenadePendingId}" +
                $"（{_grenadeWhy}；放行={_grenadeGateWhy}，预测落点=({_grenadeLanding.x:F1},{_grenadeLanding.y:F1}," +
                $"{_grenadeLanding.z:F1})，距目标 {_grenadeRange:F1}m，距自己 {_grenadeSelfGap:F1}m，最近队友 {mateText}，" +
                $"本回合第 {_grenadeThrows} 颗，剩余 {Mathf.Max(0, CountGrenade(self, _grenadePendingId) - 1)} 颗）");

            ClearGrenadeAttempt();
            return true;
        }



        /// <summary>选出这一颗要投的雷与瞄点（成功即进入"切雷"阶段；理由写进日志）。</summary>
        private bool TryBeginGrenade(CsActor self, float now)
        {
            if (now < _nextGrenadeAt)
            {
                NadeDiag(self, "投掷间隔中", $"距下次允许投掷还有 {_nextGrenadeAt - now:F1}s");
                return false;
            }

            var prefs = CsBotRoles.PreferredGrenades(_role);
            var id = (string)null;
            for (var i = 0; i < prefs.Length; i++)
            {
                if (CountGrenade(self, prefs[i]) > 0) { id = prefs[i]; break; }
            }
            if (id == null)
            {
                NadeDiag(self, "没有雷", $"手上没有偏好表里的投掷物（偏好 {CsBotRoles.GrenadeText(_role)}）");
                return false;
            }

            if (!TryPickGrenadeTarget(self, id, out var point, out var why)) return false;

            if (!GrenadeAllowed(self, id, point, now, out var gateWhy, out var landing, out var mateDist))
            {
                NadeDiag(self, "投掷门槛", gateWhy);
                return false;
            }

            _grenadeAim = GrenadeAimPoint(self, point, out var range);

            // 真实落点 = 按这一瞄向抛出的积分结果（模拟按 a.Yaw/a.Pitch 给初速，不按 point）。
            // 前方有墙 / 护栏 / 台阶时雷会提前落地，规划点与实际落点能差十几米 ——
            // 只按规划点判距离就是自伤的来源，所以这里用同一套积分预测，再看自己是否在爆心半径内。
            // 规划落点（按这一瞄向）离自己多远：前方有墙 / 护栏时雷会提前落地，规划点很远也没用。
            var predicted = PredictGrenadeLanding(self, _grenadeAim);
            var selfGap = Vector3.Distance(self.Position, predicted);
            var safeGap = CsBotConst.GrenadeSelfSafeRange + CsBotConst.GrenadeSelfSafetyMargin;
            if (id != CsWeapons.SmokeGrenade && selfGap < safeGap)
            {
                NadeDiag(self, "会炸到自己",
                    $"预测落点 ({predicted.x:F1},{predicted.y:F1},{predicted.z:F1}) 距自己 {selfGap:F1}m " +
                    $"< 安全距离 {safeGap:F1}m（爆心半径 {CsBotConst.GrenadeSelfSafeRange:F1}m + 余量 " +
                    $"{CsBotConst.GrenadeSelfSafetyMargin:F1}m）→ 放弃这次投掷");
                _nextGrenadeAt = now + CsBotConst.GrenadeThrowSpacing;
                return false;
            }

            _grenadePendingId = id;
            _grenadeWhy = why;
            _grenadeRange = range;
            _grenadeGateWhy = gateWhy;
            _grenadeLanding = predicted;
            _grenadeMateDist = mateDist;
            _grenadeSelfGap = selfGap;
            _grenadeSwitchDeadline = now + CsBotConst.GrenadeSwitchTimeout;
            return true;
        }



        /// <summary>
        /// 投掷目标点，按优先级：① 记忆期内的敌人（此时才叫"该炸"）；② 最近的包点标记
        /// （进点封烟 / 冲点前清角，与下包/拆包同用 <see cref="NearestBombsitePoint"/> 的口径）；
        /// ③ 本轮目标点（巡逻点 / 守位）。
        ///
        /// <para>下界按类型：烟雾 <see cref="CsBotConst.GrenadeMinRange"/>（3m，别扔在脚下）、
        /// 高爆/闪光 <see cref="CsBotConst.GrenadeSelfSafeRange"/>（= 爆心半径 5.5m，自己不吃自己的雷）。</para>
        /// </summary>
        private bool TryPickGrenadeTarget(CsActor self, string grenadeId, out Vector3 point, out string why)
        {
            point = Vector3.zero;
            why = null;

            var min = grenadeId == CsWeapons.SmokeGrenade
                ? CsBotConst.GrenadeMinRange
                : CsBotConst.GrenadeSelfSafeRange;

            var target = _targetId != 0 ? _match.Find(_targetId) : null;
            if (target != null && target.IsAlive && target.Team != self.Team)
            {
                var raw = target.Position + Vector3.up * (target.Height * CsBotConst.ChestHeightRatio);
                if (TryAimWithinReach(self, raw, min, _visibleNow ? "交战投掷" : "朝刚丢视野的目标位置投", out point, out why))
                    return true;
            }

            var site = NearestBombsitePoint(self.Position, out var siteDist, out var siteName);
            if (siteDist < float.MaxValue && siteName != null)
            {
                var raw = site + Vector3.up * CsConst.EyeHeight;
                if (TryAimWithinReach(self, raw, min, $"{siteName} 进点投掷", out point, out why)) return true;
            }

            if (_goalValid)
            {
                var raw = _goalPos + Vector3.up * CsConst.EyeHeight;
                var baseWhy = _goalIsSite ? "目标点投掷" : "朝本轮目标点投";
                if (TryAimWithinReach(self, raw, min, baseWhy, out point, out why)) return true;
            }

            NadeDiag(self, "没有目标",
                $"没有投得出手的目标：敌人目标={(_targetId != 0 ? _targetId.ToString() : "无")}，" +
                $"离最近包点标记 {siteDist:F1}m，本轮目标点=" +
                (_goalValid ? Vector3.Distance(self.Position, _goalPos).ToString("F1") + "m" : "无"));
            return false;
        }



        /// <summary>
        /// 投掷时机 / 队友安全闸门（true = 可以投）。
        /// <list type="number">
        /// <item>开局 <see cref="CsBotConst.GrenadeNoThrowSeconds"/> 秒内一律不投（出生点附近全是队友）；</item>
        /// <item>没有可见 / 记忆期敌人时，T 只在"进攻准备位"（到本轮包点 ≤
        /// <see cref="CsBotConst.GrenadeApproachRange"/>）投，CT 只在已下包回防时投；</item>
        /// <item>烟只在进点 / 回防时投；</item>
        /// <item>落点 <see cref="CsBotConst.GrenadeTeammateSafeRadius"/> 内、或投掷锥内该半径内有队友 ⇒ 不投。</item>
        /// </list>
        /// </summary>
        /// <param name="gateWhy">放行理由（进日志）。</param>
        /// <param name="landing">本次落点（= 门槛检查用的坐标）。</param>
        /// <param name="mateDist">落点 / 锥内最近的队友距离（没有队友 = <c>float.MaxValue</c>）。</param>
        private bool GrenadeAllowed(CsActor self, string id, Vector3 point, float now,
            out string gateWhy, out Vector3 landing, out float mateDist)
        {
            gateWhy = null;
            landing = point;
            mateDist = float.MaxValue;

            var elapsed = now - _roundStartAt;
            if (elapsed < CsBotConst.GrenadeNoThrowSeconds)
            {
                gateWhy = $"开局 {elapsed:F1}s < {CsBotConst.GrenadeNoThrowSeconds:F0}s";
                return false;
            }

            var hasEnemy = _targetId != 0 && now - _lastSeenTime <= CsBotConst.PlantEnemyMemorySeconds;
            var siteDist = float.MaxValue;
            var approach = self.Team == CsTeam.T && AtAttackApproach(self, out siteDist);
            var siteDistText = siteDist < float.MaxValue ? siteDist.ToString("F1") : "∞";

            if (id == CsWeapons.SmokeGrenade && !approach && !(self.Team == CsTeam.CT && _match.BombPlanted))
            {
                gateWhy = $"烟只在进点/回防时投（没有可见敌人，到本轮包点 {siteDistText}m）";
                return false;
            }

            if (!hasEnemy && !approach && !(self.Team == CsTeam.CT && _match.BombPlanted))
            {
                gateWhy = $"没有可见/记忆期敌人，且不在进攻准备位（到本轮包点 {siteDistText}m > " +
                          $"{CsBotConst.GrenadeApproachRange:F0}m）";
                return false;
            }

            mateDist = NearestTeammateToThrow(self, point, out var mateName, out var inCone);
            if (mateDist < CsBotConst.GrenadeTeammateSafeRadius)
            {
                gateWhy = inCone
                    ? $"投掷锥内 {mateDist:F1}m 有队友 {mateName}"
                    : $"落点 {mateDist:F1}m 内有队友 {mateName}";
                return false;
            }

            gateWhy = hasEnemy
                ? "有可见/记忆期敌人"
                : approach ? $"进攻准备位（到本轮包点 {siteDistText}m）" : "CT 回防（包已下）";
            return true;
        }



        /// <summary>是否在"进攻准备位"：本轮目标是包点，且到该包点标记 ≤ <see cref="CsBotConst.GrenadeApproachRange"/>。</summary>
        private bool AtAttackApproach(CsActor self, out float siteDist)
        {
            siteDist = float.MaxValue;
            if (!_goalValid || !_goalIsSite) return false;

            var site = NearestBombsitePoint(_goalPos, out var d, out _);
            if (d > CsBotConst.SiteRadius) return false;

            var v = site - self.Position;
            v.y = 0f;
            siteDist = v.magnitude;
            return siteDist <= CsBotConst.GrenadeApproachRange;
        }



        /// <summary>
        /// 落点 / 投掷锥内最近的队友距离。锥 = 从自己指向落点的方向 ±
        /// <see cref="CsBotConst.GrenadeTeammateConeDegrees"/>，半径 <see cref="CsBotConst.GrenadeTeammateSafeRadius"/>。
        /// </summary>
        private float NearestTeammateToThrow(CsActor self, Vector3 point, out string mateName, out bool inCone)
        {
            mateName = null;
            inCone = false;

            var toPoint = point - self.EyePosition;
            toPoint.y = 0f;
            var aimDir = toPoint.sqrMagnitude > 0.0001f ? toPoint.normalized : Vector3.zero;

            var best = float.MaxValue;
            var actors = _match.Actors;
            for (var i = 0; i < actors.Count; i++)
            {
                var a = actors[i];
                if (a == null || !a.IsAlive || a.Id == self.Id || a.Team != self.Team) continue;

                var dp = a.Position - point;
                dp.y = 0f;
                var landingDist = dp.magnitude;

                var toMate = a.Position - self.Position;
                toMate.y = 0f;
                var mateDist = toMate.magnitude;
                var cone = aimDir != Vector3.zero && mateDist <= CsBotConst.GrenadeTeammateSafeRadius &&
                           Vector3.Angle(aimDir, toMate) <= CsBotConst.GrenadeTeammateConeDegrees;

                if (landingDist < best)
                {
                    best = landingDist;
                    mateName = a.Name;
                    inCone = false;
                }

                if (cone && mateDist < best)
                {
                    best = mateDist;
                    mateName = a.Name;
                    inCone = true;
                }
            }

            return best;
        }



        /// <summary>
        /// 把"想投的地方"折算成**投得出去**的落点：水平距离 &lt; <paramref name="min"/>（贴脸 / 脚下）⇒ 不投；
        /// 否则落在 <c>min(距离, GrenadeMaxRange)</c> 处 —— 目标更远时，雷落在**朝它方向的最远处**
        /// （"朝那个方向扔一颗"，预炸 / 预封，落点仍在投掷物理能达到的范围内）。
        /// </summary>
        private static bool TryAimWithinReach(CsActor self, Vector3 raw, float min, string baseWhy,
            out Vector3 point, out string why)
        {
            point = Vector3.zero;
            why = null;

            var flat = raw - self.EyePosition;
            flat.y = 0f;
            var d = flat.magnitude;
            if (d < min || d < 0.01f) return false;

            var reach = d <= CsBotConst.GrenadeMaxRange ? d : CsBotConst.GrenadeMaxRange;
            point = self.EyePosition + flat.normalized * reach;
            why = d <= CsBotConst.GrenadeMaxRange
                ? baseWhy
                : $"{baseWhy}（超出投掷距离 {d:F1}m → 落在朝该方向的 {CsBotConst.GrenadeMaxRange:F1}m 处）";
            return true;
        }



        /// <summary>
        /// 低弧解算：把瞄点抬到"按这个仰角投出，正好落在目标点水平距离上"的位置。
        /// 解的就是本工程投掷用的那套运动（<c>CsInventory.ThrowGrenade</c> 给初速
        /// <c>CsConst.GrenadeThrowSpeed(仰角)</c>、之后只受 <c>CsConst.Gravity</c>）：
        /// 平抛落点 <c>d(θ) = v(θ)²·sin(2θ)/g</c>，其中 <c>v</c> **自己也是 θ 的函数**
        /// （原版口径 <c>(90 - angThrow.x) × 4</c>）⇒ 定值 v 的闭式解 <c>sin(2θ) = d·g/v²</c> 不再成立，
        /// 改为在**单调上升段** <c>[0°, 55°]</c> 上二分求 θ —— 该段的右端点由
        /// <c>CsBotConst.GrenadeMaxRange</c> 的注释给出（峰值 ≈ 10.04 m @ 55° &gt; 9.6 m 上限
        /// ⇒ 解必落在该段内）。取上升段那一支 = 低弧（近弹道、飞行时间短、不易被墙挡）。
        /// </summary>
        private static Vector3 GrenadeAimPoint(CsActor self, Vector3 point, out float range)
        {
            var flat = point - self.EyePosition;
            flat.y = 0f;
            var d = flat.magnitude;
            range = d;
            if (d < 0.01f) return point;

            var lo = 0f;
            var hi = 55f * Mathf.Deg2Rad;
            for (var i = 0; i < 24; i++)
            {
                var mid = (lo + hi) * 0.5f;
                var v = CsConst.GrenadeThrowSpeed(mid * Mathf.Rad2Deg);
                var reach = v * v * Mathf.Sin(2f * mid) / CsConst.Gravity;
                if (reach < d) lo = mid;
                else hi = mid;
            }
            var pitch = (lo + hi) * 0.5f;

            return self.EyePosition + flat.normalized * d + Vector3.up * (d * Mathf.Tan(pitch));
        }



        /// <summary>
        /// 预测这颗雷的落点：与 <c>CsInventory.TickProjectiles</c> 同一套积分 ——
        /// 初速 = <c>CsConst.GrenadeThrowSpeed(俯仰)</c>，之后只受 <c>CsConst.Gravity</c>；
        /// 每步先采样地形高度（<c>ICsMap.SampleGround</c>），再对世界几何做线段检测
        /// （角色受击体不算碰撞，与模拟一致）。命中即停 —— 那颗雷就落在那里。
        /// 只用于"这颗雷会不会炸到自己"的前置判定。
        /// </summary>
        private Vector3 PredictGrenadeLanding(CsActor self, Vector3 aimPoint, float pitchOffsetDegrees = 0f)
        {
            var eye = self.EyePosition;
            var dir = aimPoint - eye;
            if (dir.sqrMagnitude < 0.0001f) return self.Position;
            dir.Normalize();

            var yawDeg = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
            var pitchDeg = Mathf.Asin(Mathf.Clamp(dir.y, -1f, 1f)) * Mathf.Rad2Deg + pitchOffsetDegrees;
            pitchDeg = Mathf.Clamp(pitchDeg, -89f, 89f);

            var yawRad = yawDeg * Mathf.Deg2Rad;
            var pitchRad = pitchDeg * Mathf.Deg2Rad;
            dir = new Vector3(Mathf.Cos(pitchRad) * Mathf.Sin(yawRad),
                              Mathf.Sin(pitchRad),
                              Mathf.Cos(pitchRad) * Mathf.Cos(yawRad));

            var pos = eye + dir * CsConst.PlayerRadius;
            var velocity = dir * CsConst.GrenadeThrowSpeed(pitchDeg);

            // 与模拟的帧步长同量级：步长越小越贴近真实积分，1/60 s 足够（落地误差 ≪ 1m）。
            const float step = 1f / 60f;
            for (var t = 0f; t < CsConst.GrenadeFuse; t += step)
            {
                velocity.y -= CsConst.Gravity * step;
                var next = pos + velocity * step;

                if (_map != null && _map.IsLoaded)
                {
                    var groundY = _map.SampleGround(next);
                    if (!float.IsNegativeInfinity(groundY) && next.y <= groundY && velocity.y <= 0f)
                    {
                        return new Vector3(next.x, groundY, next.z);
                    }
                }

                if (Physics.Linecast(pos, next, out var hit, ~0, QueryTriggerInteraction.Ignore))
                {
                    var proxy = hit.collider != null ? hit.collider.GetComponentInParent<CsHitboxProxy>() : null;
                    if (proxy == null) return hit.point;
                }

                pos = next;
            }

            return pos;
        }



        /// <summary>手上这类投掷物有几颗（与 <c>CsInventory.GrenadeCount</c> 同读法：弹药表的弹匣位）。</summary>
        private static int CountGrenade(CsActor self, string grenadeId)
        {
            return self.Ammo.TryGetValue(grenadeId, out var v) ? v.inMag : 0;
        }



        /// <summary>
        /// 投掷诊断：说明"为什么没投出去"。**按 (回合, 原因) 去重**（每回合每类原因最多一条）——
        /// 只按回合去重会让"先因为没雷报一次"把后面真正的原因（切雷超时 / 视角没收敛 / 没有目标）永久吞掉。
        /// ⛔ 不许静默：没这条就无法区分"没实现"与"条件没满足"。
        /// </summary>
        private void NadeDiag(CsActor self, string key, string message)
        {
            if (!_grenadeDiagKeys.Add(_match.RoundNumber + ":" + key)) return;
            Game.Logger.Info(Tag, $"{_name}({self.Team}/{CsBotRoles.Label(_role)}) 未投掷[{key}]：{message}");
        }



        /// <summary>清掉"进行中的投掷"（不影响本回合已投颗数与下一次允许时刻）。</summary>
        private void ClearGrenadeAttempt()
        {
            _grenadePendingId = null;
            _grenadeWhy = null;
            _grenadeRange = 0f;
            _grenadeGateWhy = null;
            _grenadeLanding = Vector3.zero;
            _grenadeMateDist = float.MaxValue;
        }



        /// <summary>
        /// 交战中的纯后退上限：连续后退（与朝向夹角 &gt; 120°）超过
        /// <see cref="CsBotConst.MaxBackpedalSeconds"/> 就改成横向侧移 —— 允许边打边侧移，
        /// 但不许整段背对目标倒着走。
        /// </summary>
        private void ApplyBackpedalCap(CsActor self, ref CsBotIntent intent, float now)
        {
            var move = intent.Move;
            move.y = 0f;
            if (move.sqrMagnitude < 0.04f)
            {
                _backpedalSince = 0f;
                return;
            }

            move.Normalize();
            if (Vector3.Dot(move, Forward(self.Yaw, 0f)) >= CsBotConst.BackpedalDot)
            {
                _backpedalSince = 0f;
                return;
            }

            if (_backpedalSince <= 0f)
            {
                _backpedalSince = now;
                return;
            }

            if (now - _backpedalSince < CsBotConst.MaxBackpedalSeconds) return;

            var yawRad = self.Yaw * Mathf.Deg2Rad;
            var right = new Vector3(Mathf.Cos(yawRad), 0f, -Mathf.Sin(yawRad));
            intent.Move = right * (Vector3.Dot(move, right) >= 0f ? 1f : -1f);
            _backpedalSince = 0f;

            if (now >= _nextBackpedalLogAt)
            {
                _nextBackpedalLogAt = now + CsBotConst.StateLogMinInterval;
                var target = _targetId != 0 ? _match.Find(_targetId) : null;
                var dist = -1f;
                if (target != null)
                {
                    var d = target.Position - self.Position;
                    d.y = 0f;
                    dist = d.magnitude;
                }

                Game.Logger.Info(Tag,
                    $"{_name} 交战纯后退超过 {CsBotConst.MaxBackpedalSeconds:F1}s → 改为横向侧移（距目标 {dist:F1}m）");
            }
        }



        // ==================================================================
        //  多目标威胁排序
        // ==================================================================
        // 出处：原版 `cs_bot_vision.cpp` 的 `FindMostDangerousThreat()`（`cs_bot.h:955`）从视野内所有敌人里
        // 挑一个"最危险"的（原始注释：feeds into reaction time queue）。本工程能观测的只有位置 / 朝向 /
        // 血量 / 手持武器 ⇒ 按这四项加权；权重值**本项目新增**（A 的 profile 数据不在盘，见差异 #58）。
        /// <summary>"对方正在瞄我"的朝向夹角（度）：与感知选目标同一口径（敌人朝向与我方夹角 ≤ 它）。</summary>
        private const float ThreatAimingConeDegrees = 20f;

        /// <summary>残血敌人在威胁排序里的血量门槛：低于它就优先补枪。</summary>
        private const int ThreatLowHealth = 50;

        /// <summary>换目标所需的威胁提升比例：新目标必须高出 20% 才换，防两个目标交替抖动。</summary>
        private const float ThreatSwitchRatio = 1.2f;

        /// <summary>判定"这个敌人本 tick 可见"的时间容差（秒）：感知在同一个 <c>now</c> 刷新记忆。</summary>
        private const float ThreatVisibleEpsilonSeconds = 0.02f;

        /// <summary>
        /// 换到新目标后的锁定窗口（秒）：感知每 tick 按"最近优先"重新选目标，会把刚换的目标立刻改回去
        /// （实测同一对 actor 一秒内来回换 4 次）。窗口内以这里的选择为准，窗口过了再交给威胁排序。
        /// </summary>
        private const float RetargetHoldSeconds = 1.5f;

        /// <summary>
        /// 关掉片 C 的三条交战分支（狙蹲守 / 掩体 / peek），只留威胁排序。
        /// 测试入口：用于同一环境里对"有 / 无这三条分支"的命中率做对照，不改变任何玩法数值。
        /// </summary>
        public static bool BranchDisabledForTest { get; set; }

        /// <summary>锁定的目标 id（0 = 没锁）；<see cref="_pinnedUntil"/> 到期后失效。</summary>
        private long _pinnedTargetId;

        private float _pinnedUntil;

        /// <summary>一次威胁评估的分项（数值用于比较，布尔用于日志里的原因文字）。</summary>
        private struct ThreatParts
        {
            public float Score;
            public float Distance;
            public bool AimingAtMe;
            public bool LowHealth;
            public bool PrimaryWeapon;
        }

        /// <summary>
        /// 交战目标重选：从本 tick 可见的敌人里取威胁最高者；只有高出当前目标
        /// <see cref="ThreatSwitchRatio"/> 倍时才换（防抖），换时打一行"换目标"。
        /// 当前目标本 tick 不可见（记忆期内）时不设门限 —— 有可见敌人就先打看得见的。
        /// </summary>
        private void MaybeRetarget(CsActor self, float now)
        {
            // 锁定窗口内以刚换的目标为准（感知会按"最近优先"把它改回去，这里静默掰回来，不重复记日志）。
            if (_pinnedTargetId != 0)
            {
                if (now >= _pinnedUntil) _pinnedTargetId = 0;
                else if (VisibleThisTick(_pinnedTargetId))
                {
                    var pinned = _match.Find(_pinnedTargetId);
                    if (pinned != null && pinned.IsAlive)
                    {
                        if (_targetId != _pinnedTargetId) AdoptTarget(self, pinned, now);
                        return;
                    }
                }
                else
                {
                    _pinnedTargetId = 0;
                }
            }

            var current = default(ThreatParts);
            var currentVisible = false;
            var best = default(ThreatParts);
            CsActor bestActor = null;

            var actors = _match.Actors;
            for (var i = 0; i < actors.Count; i++)
            {
                var enemy = actors[i];
                if (enemy == null || !enemy.IsAlive || enemy.Id == self.Id) continue;
                if (enemy.Team == self.Team) continue;
                if (enemy.Team != CsTeam.T && enemy.Team != CsTeam.CT) continue;
                if (!_enemyMemory.TryGetValue(enemy.Id, out var memory)) continue;
                if (Mathf.Abs(memory.LastSeenTime - now) > ThreatVisibleEpsilonSeconds) continue;

                var parts = ThreatOf(self, enemy);
                if (enemy.Id == _targetId)
                {
                    current = parts;
                    currentVisible = true;
                    continue;
                }

                if (bestActor != null && parts.Score <= best.Score) continue;
                best = parts;
                bestActor = enemy;
            }

            if (bestActor == null) return;

            var switchOver = !currentVisible || best.Score > current.Score * ThreatSwitchRatio;
            if (!switchOver) return;

            var oldId = _targetId;
            var oldScore = currentVisible ? current.Score : 0f;
            AdoptTarget(self, bestActor, now);
            _pinnedTargetId = bestActor.Id;
            _pinnedUntil = now + RetargetHoldSeconds;
            Game.Logger.Info(Tag,
                $"换目标 actor {oldId} → actor {bestActor.Id}：威胁 {oldScore:F2} → {best.Score:F2}" +
                $"（{(currentVisible ? $"新目标高出 {(ThreatSwitchRatio - 1f):P0} 才换" : "原目标本 tick 不可见")}；" +
                $"{ThreatText(best)}）");
        }

        /// <summary>威胁 = 1/距离 + 正在瞄我 0.5 + 血量低 0.3 + 手持主武器 0.2（分项含义见 <see cref="ThreatText"/>）。</summary>
        private static ThreatParts ThreatOf(CsActor self, CsActor enemy)
        {
            var dist = Vector3.Distance(self.EyePosition, enemy.EyePosition);
            var parts = new ThreatParts { Distance = dist, Score = 1f / Mathf.Max(1f, dist) };

            var toMe = self.EyePosition - enemy.EyePosition;
            toMe.y = 0f;
            if (toMe.sqrMagnitude > 0.0004f &&
                Vector3.Angle(Forward(enemy.Yaw, 0f), toMe) <= ThreatAimingConeDegrees)
            {
                parts.AimingAtMe = true;
                parts.Score += 0.5f;
            }

            if (enemy.Health <= ThreatLowHealth)
            {
                parts.LowHealth = true;
                parts.Score += 0.3f;
            }

            var def = string.IsNullOrEmpty(enemy.ActiveWeapon) ? null : CsWeapons.Get(enemy.ActiveWeapon);
            if (def != null &&
                (def.Class == CsWeaponClass.Rifle || def.Class == CsWeaponClass.SMG ||
                 def.Class == CsWeaponClass.Shotgun || def.Class == CsWeaponClass.MachineGun ||
                 def.Class == CsWeaponClass.Sniper))
            {
                parts.PrimaryWeapon = true;
                parts.Score += 0.2f;
            }

            return parts;
        }

        /// <summary>威胁分项的可读文字（写进"换目标"日志：为什么它更危险）。</summary>
        private static string ThreatText(ThreatParts parts)
        {
            var text = $"距 {parts.Distance:F1}m";
            if (parts.AimingAtMe) text += "、正瞄我";
            if (parts.LowHealth) text += $"、残血≤{ThreatLowHealth}";
            if (parts.PrimaryWeapon) text += "、持主武器";
            return text;
        }

        /// <summary>
        /// 把交战目标切到 <paramref name="enemy"/>，并把"最后所见 / 反应计时 / 可打点"一起搬到它身上
        /// —— 与 <see cref="UpdatePerception"/> 选中目标时的写法同口径（反应计时绑敌人身份，不重算已见过的敌人）。
        /// </summary>
        private void AdoptTarget(CsActor self, CsActor enemy, float now)
        {
            var memorySeconds = CsBotConst.MemorySeconds(_difficulty);
            if (!_enemySeenAt.TryGetValue(enemy.Id, out var seenAt) || now - seenAt > memorySeconds)
            {
                _firstSeenTime = now;
                _wobbleRefreshAt = 0f;
                _headRollAt = 0f;
            }

            _enemySeenAt[enemy.Id] = now;
            _targetId = enemy.Id;
            _lastSeenTime = now;
            _lastSeenPos = enemy.EyePosition;
            _lastSeenVel = enemy.Velocity;
            _visibleNow = true;
            _closestVisibleDist = Vector3.Distance(self.EyePosition, enemy.EyePosition);

            var chest = enemy.Position + Vector3.up * (enemy.Height * CsBotConst.ChestHeightRatio);
            BotSight.CheckBody(_match, self.EyePosition, enemy.EyePosition, chest, _profile.VisionRange,
                out var aimPoint, out _, out _);
            _targetAimPoint = aimPoint;
        }



        // ==================================================================
        //  掩体选择（Hide）
        // ==================================================================
        // 出处：原版 `HideState`（`cs_bot.h:195`）的 `SetHidingSpot` / `SetDuration` —— 先选一个藏身点、
        // 再按持续时间躲。本工程没有 nav 藏身点表 ⇒ 以自身为圆心在 3~8m 环形采样可站立点，
        // 用物理射线判"这一点能不能挡住敌人视线"（旧的"反方向后退"不判遮挡，方向对但可能仍暴露）。
        /// <summary>残血躲掩体的血量门槛（HP 低于它且敌人可见 ⇒ 找掩体）。</summary>
        private const float HideHealthBelow = 35f;

        /// <summary>被这么多敌人同时看见（视野内可见敌人数）⇒ 找掩体。</summary>
        private const int HideVisibleEnemies = 2;

        /// <summary>触发找掩体的敌人距离上限（米）：更远的敌人不值得脱离交火。</summary>
        private const float HideThreatRange = 30f;

        /// <summary>掩体候选采样的最近半径（米）。</summary>
        private const float HideCoverMin = 3f;

        /// <summary>掩体候选采样的最远半径（米）。</summary>
        private const float HideCoverMax = 8f;

        /// <summary>掩体候选采样的半径步长（米）：3/4/5/6/7/8 共 6 圈。</summary>
        private const float HideCoverRingStep = 1f;

        /// <summary>每圈的采样点数（15° 一个）。</summary>
        private const int HideCoverAngles = 24;

        /// <summary>掩体点到敌人的最小水平距离（米）：贴脸点挡不住也来不及蹲。</summary>
        private const float HideEnemyMinGap = 6f;

        /// <summary>到达掩体点后蹲下的时长（秒）。</summary>
        private const float HideCrouchSeconds = 1.2f;

        /// <summary>从开始移动到掩体点的兜底时限（秒）：超时按"没走到"收尾，不无限卡在移动里。</summary>
        private const float HideTravelSeconds = 4f;

        /// <summary>两次找掩体之间的最小间隔（秒）：条件（残血 / 多敌可见）会持续成立，没有它就会每 tick 重试。</summary>
        private const float HideCooldown = 6f;

        /// <summary>下次允许尝试找掩体的时刻（失败也起算，避免同一 tick 反复刷"失败"）。</summary>
        private float _hideRetryAt;

        /// <summary>选中的可达掩体点最多试几个（按离自己由近到远）：每个要跑一次 A*，只在小范围内试。</summary>
        private const int HideReachProbes = 6;

        /// <summary>掩体候选（一个可站立点 + 它挡住的敌人数 / 到最近敌人的距离）。</summary>
        private struct CoverCandidate
        {
            public Vector3 Point;
            public float SelfDistance;
            public float EnemyGap;
            public int Covered;
        }

        /// <summary>本次搜索的候选缓冲（只在进入掩体分支时填充，不在每 tick 上分配）。</summary>
        private readonly List<CoverCandidate> _coverCandidates = new List<CoverCandidate>(128);

        /// <summary>本次躲掩体的兜底截止时刻（0 = 不在躲）。</summary>
        private float _hideDeadline;

        /// <summary>选定的掩体点（世界坐标）。</summary>
        private Vector3 _hideGoal;

        /// <summary>是否已走到掩体点。</summary>
        private bool _hideArrived;

        /// <summary>到达掩体点后蹲到这一刻。</summary>
        private float _hideCrouchUntil;

        /// <summary>掩体候选的敌人缓冲（当前目标排第 0 位，其余可见敌人在后）。</summary>
        private readonly CsActor[] _coverEnemies = new CsActor[4];

        /// <summary>
        /// 是否该找掩体：有可见敌人、最近可见敌人在 <see cref="HideThreatRange"/> 内，
        /// 且（HP &lt; <see cref="HideHealthBelow"/> 或 同时被 <see cref="HideVisibleEnemies"/> 人可见）。
        /// </summary>
        private bool HideDue()
        {
            var self = _tickSelf;
            if (self == null || !self.IsAlive) return false;
            if (CsClock.Now < _hideRetryAt) return false;
            if (!_visibleNow || _targetId == 0) return false;
            if (_closestVisibleDist > HideThreatRange) return false;
            if (self.Health < HideHealthBelow) return true;
            return _visibleEnemies >= HideVisibleEnemies;
        }

        /// <summary>
        /// 掩体分支：进入时选点（成功 → 打"找掩体 成功"并走过去；失败 → 打"找掩体 失败"并让出分支，
        /// 由后续的交战 / 后撤分支接手）。到点后蹲 <see cref="HideCrouchSeconds"/> 秒再结束。
        /// </summary>
        private BtStatus HideStep(CsActor self, ref CsBotIntent intent, float now)
        {
            intent.Fire = false;
            intent.Walk = false;
            intent.AimPoint = _lastSeenPos.sqrMagnitude > 0.0001f ? _lastSeenPos : LookAhead(self);

            if (_hideDeadline <= 0f)
            {
                if (!TryChooseCover(self, out var point, out var coverCount, out var enemies, out var gap))
                {
                    Game.Logger.Info(Tag,
                        $"找掩体 失败：{_name}（{_difficulty}）HP {self.Health}、可见敌 {_visibleEnemies} 人，" +
                        $"{HideCoverMin:F0}~{HideCoverMax:F0}m 内没有既挡视线、距敌又 ≥{HideEnemyMinGap:F0}m 的落点" +
                        $" → 退回后撤");
                    _hideRetryAt = now + HideCooldown;
                    return BtStatus.Failure;
                }

                _hideGoal = point;
                _hideArrived = false;
                _hideCrouchUntil = 0f;
                _hideDeadline = now + HideTravelSeconds;
                Game.Logger.Info(Tag,
                    $"找掩体 成功：{_name}（{_difficulty}）HP {self.Health}、可见敌 {_visibleEnemies} 人 → " +
                    $"掩体点 ({point.x:F1},{point.z:F1})，距自己 {HorizontalDistance(self.Position, point):F1}m，" +
                    $"距最近敌人 {gap:F1}m（对 {coverCount}/{enemies} 个可见敌人都挡住视线）");
            }

            SetState(CsBotState.Engage, now);
            intent.State = CsBotState.Engage;

            if (!_hideArrived)
            {
                if (HorizontalDistance(self.Position, _hideGoal) <= 0.6f)
                {
                    _hideArrived = true;
                    _hideCrouchUntil = now + HideCrouchSeconds;
                    intent.Move = Vector3.zero;
                    intent.Crouch = true;
                    Game.Logger.Info(Tag,
                        $"{_name} 已到掩体点 ({_hideGoal.x:F1},{_hideGoal.z:F1}) → 蹲下 {HideCrouchSeconds:F1}s 等交火窗口");
                }
                else if (now >= _hideDeadline)
                {
                    _hideDeadline = 0f;
                    Game.Logger.Info(Tag,
                        $"{_name} 走到掩体点超时（{HideTravelSeconds:F1}s，仍在 {HorizontalDistance(self.Position, _hideGoal):F1}m 外）" +
                        $" → 就地交战");
                    return BtStatus.Success;
                }
                else
                {
                    intent.Move = _nav.ComputeMove(self.Position, _hideGoal, 0.5f, now,
                                                   viaRoute: false, hullHeight: self.Height);
                    intent.Crouch = false;
                    return BtStatus.Running;
                }
            }

            intent.Move = Vector3.zero;
            intent.Crouch = true;

            if (now < _hideCrouchUntil) return BtStatus.Running;

            _hideDeadline = 0f;
            return BtStatus.Success;
        }

        /// <summary>
        /// 在自身周围 <see cref="HideCoverMin"/>~<see cref="HideCoverMax"/> 的环上找掩体点：
        /// 对每个可站立点做 <c>Physics.Linecast(敌人眼 → 点+1.6m)</c>，要求当前目标与其它可见敌人都被**世界几何**挡住
        /// （只被角色挡住不算掩体），且到每个敌人 ≥ <see cref="HideEnemyMinGap"/>；在合格点里取离自己最近的。
        /// </summary>
        private bool TryChooseCover(CsActor self, out Vector3 point, out int coverCount, out int enemies, out float gap)
        {
            point = self.Position;
            coverCount = 0;
            enemies = 0;
            gap = float.MaxValue;

            if (_map == null || !_map.IsLoaded) return false;

            enemies = CollectVisibleEnemies(self, _coverEnemies);
            if (enemies == 0) return false;

            _coverCandidates.Clear();

            for (var ring = 0; ring < 6; ring++)
            {
                var radius = HideCoverMin + ring * HideCoverRingStep;
                for (var a = 0; a < HideCoverAngles; a++)
                {
                    var cand = self.Position +
                               Quaternion.Euler(0f, a * (360f / HideCoverAngles), 0f) * Vector3.forward * radius;
                    if (!_map.CanStand(cand, CsConst.PlayerRadius, self.Height)) continue;

                    var candEye = cand + Vector3.up * CsConst.EyeHeight;
                    var covered = 0;
                    var nearest = float.MaxValue;
                    var usable = true;

                    for (var i = 0; i < enemies; i++)
                    {
                        var enemy = _coverEnemies[i];
                        var d = HorizontalDistance(cand, enemy.Position);
                        if (d < HideEnemyMinGap)
                        {
                            usable = false;
                            break;
                        }

                        if (d < nearest) nearest = d;
                        if (LineBlockedByWorld(enemy.EyePosition, candEye)) covered++;
                        else if (i == 0)
                        {
                            // 第 0 位是当前交战目标：这个点必须挡住它（"能挡住当前敌人视线的点"）；
                            // 其余可见敌只作为加分项，不强求一次挡住所有人（否则开阔地永远找不到）。
                            usable = false;
                            break;
                        }
                    }

                    if (!usable) continue;

                    _coverCandidates.Add(new CoverCandidate
                    {
                        Point = cand,
                        SelfDistance = HorizontalDistance(self.Position, cand),
                        EnemyGap = nearest,
                        Covered = covered,
                    });
                }
            }

            if (_coverCandidates.Count == 0) return false;

            // 取最近的那几个候选，逐个问"导航走得到吗"：CanStand 只说明这一点站得住，
            // 不说明它和现在的位置连通（隔墙的落点会让机器人顶着墙推 2s，变成卡住告警）。
            _coverCandidates.Sort((x, y) => x.SelfDistance.CompareTo(y.SelfDistance));
            var probes = Mathf.Min(HideReachProbes, _coverCandidates.Count);
            for (var i = 0; i < probes; i++)
            {
                var c = _coverCandidates[i];
                if (!_nav.CanReach(self.Position, c.Point)) continue;

                point = c.Point;
                coverCount = c.Covered;
                gap = c.EnemyGap;
                return true;
            }

            return false;
        }

        /// <summary>
        /// 这一段"敌人眼 → 目标点"是否被**世界几何**挡住。只被角色受击体挡住不算掩体
        /// （CS 1.6 里其它玩家不挡视线，与 <see cref="BotSight"/> 同一语义）。
        /// </summary>
        private static bool LineBlockedByWorld(Vector3 from, Vector3 to)
        {
            var delta = to - from;
            if (delta.sqrMagnitude < 0.0001f) return false;
            if (!Physics.Linecast(from, to, out var hit, ~0, QueryTriggerInteraction.Ignore)) return false;
            var proxy = hit.collider != null ? hit.collider.GetComponentInParent<CsHitboxProxy>() : null;
            return proxy == null;
        }

        /// <summary>收集本 tick 可见的敌人（当前目标排第 0 位）；返回写入缓冲的条数。</summary>
        private int CollectVisibleEnemies(CsActor self, CsActor[] buffer)
        {
            var n = 0;
            var target = _targetId != 0 ? _match.Find(_targetId) : null;
            if (target != null && target.IsAlive && VisibleThisTick(target.Id))
            {
                buffer[n++] = target;
            }

            var actors = _match.Actors;
            for (var i = 0; i < actors.Count && n < buffer.Length; i++)
            {
                var enemy = actors[i];
                if (enemy == null || !enemy.IsAlive || enemy.Id == self.Id) continue;
                if (enemy.Team == self.Team) continue;
                if (enemy.Team != CsTeam.T && enemy.Team != CsTeam.CT) continue;
                if (target != null && enemy.Id == target.Id) continue;
                if (!VisibleThisTick(enemy.Id)) continue;
                buffer[n++] = enemy;
            }

            return n;
        }

        /// <summary>这个敌人在本 tick 的感知里是否可见（记忆在同一个 <c>now</c> 刷新）。</summary>
        private bool VisibleThisTick(long actorId)
        {
            return _enemyMemory.TryGetValue(actorId, out var memory) &&
                   Mathf.Abs(memory.LastSeenTime - CsClock.Now) <= ThreatVisibleEpsilonSeconds;
        }



        // ==================================================================
        //  Peek 节奏
        // ==================================================================
        // 出处：原版 `AttackState`（`cs_bot.h:80`）的 `DodgeStateType`（`cs_bot.h:92`：STEADY_ON / SLIDE_LEFT /
        // SLIDE_RIGHT / JUMP）—— 交火期按"站定 / 侧滑"的节奏换姿态，而不是一直站着。这里的 peek =
        // 蹲着藏 1.5s、起身探 0.5s 的循环（周期 2s），只在贴着实体掩体时用。
        /// <summary>贴掩体多久之后才值得探身（秒）：刚躲进来先稳住，不立刻探头。</summary>
        private const float PeekCoverSeconds = 3f;

        /// <summary>一次探身循环的周期（秒）。</summary>
        private const float PeekCycleSeconds = 2f;

        /// <summary>每个周期里探身（站起、可开火）的时长（秒）。</summary>
        private const float PeekExposeSeconds = 0.5f;

        /// <summary>每次探身朝敌人平移的距离上限（米）。</summary>
        private const float PeekOffsetMeters = 0.8f;

        /// <summary>探身的最小 / 最大交战距离（米）：太近没得探，太远不属于同一个掩体交战。</summary>
        private const float PeekMinRange = 10f;
        private const float PeekMaxRange = 45f;

        /// <summary>判定"贴着实体掩体"的探测半径（米）。</summary>
        private const float PeekProbeRadius = 1.2f;

        /// <summary>贴掩体探测的方位数（8 方位）。</summary>
        private const int PeekProbeCount = 8;

        /// <summary>开始贴掩体的时刻（0 = 当前没贴掩体）。</summary>
        private float _coveredSince;

        /// <summary>本次 peek 循环的起点时刻（0 = 不在循环里）。</summary>
        private float _peekCycleStart;

        /// <summary>本次探身的起点位置（用来把"向敌平移"限在 <see cref="PeekOffsetMeters"/> 内）。</summary>
        private Vector3 _peekOrigin;

        /// <summary>上一 tick 是否处在"探出"相位（用来只在相位边沿打日志）。</summary>
        private bool _peekExposing;

        /// <summary>本回合累计探身次数（日志计数）。</summary>
        private int _peekCount;

        /// <summary>
        /// 是否该走 peek 节奏：有交战目标、手里有子弹、距离在
        /// <see cref="PeekMinRange"/>~<see cref="PeekMaxRange"/>，且**身边 1.2m 内存在能挡住敌人视线的实锹掩体**、
        /// 已经贴了 <see cref="PeekCoverSeconds"/> 秒。目标的可见性用记忆窗口判（蹲下时看不见是正常的）。
        /// </summary>
        private bool PeekDue(CsActor self)
        {
            if (self == null || !self.IsAlive || _targetId == 0)
            {
                ResetPeek();
                return false;
            }

            var target = _match.Find(_targetId);
            if (target == null || !target.IsAlive)
            {
                ResetPeek();
                return false;
            }

            var def = self.ActiveDef;
            if (def == null || def.Magazine <= 0 || self.GetAmmo(def.Id).inMag <= 0)
            {
                ResetPeek();
                return false;
            }

            // 只对"此刻真的看得见"的敌人探身：看不见还说自己在探身，就会演成躲在墙后不出来。
            if (!_visibleNow)
            {
                ResetPeek();
                return false;
            }

            var dist = Vector3.Distance(self.Position, target.Position);
            if (dist < PeekMinRange || dist > PeekMaxRange || !CoverAdjacent(self, target))
            {
                ResetPeek();
                return false;
            }

            var now = CsClock.Now;
            if (_lastSeenTime <= 0f || now - _lastSeenTime > CsBotConst.TargetMemorySeconds)
            {
                ResetPeek();
                return false;
            }

            if (_coveredSince <= 0f) _coveredSince = now;
            return now - _coveredSince >= PeekCoverSeconds;
        }

        /// <summary>peek 分支：探出相位跑一次 <see cref="Engage"/>（照常瞄 / 开火），再叠加"起身 + 向敌平移"；
        /// 缩回相位蹲下、不扣扳机、准星仍留在敌人身上。</summary>
        private BtStatus PeekStep(CsActor self, ref CsBotIntent intent, float now)
        {
            // 叶子返回 Running 之后，树会从叶子本身续跑、不再回看前置条件 ⇒ 条件失效必须在这里退出。
            if (!PeekDue(self)) return BtStatus.Failure;

            var target = _match.Find(_targetId);
            if (target == null || !target.IsAlive) return BtStatus.Failure;

            if (_peekCycleStart <= 0f) _peekCycleStart = now;
            var phase = (now - _peekCycleStart) % PeekCycleSeconds;
            var exposing = phase < PeekExposeSeconds;
            var toEnemy = HorizontalDir(self.Position, target.Position);
            if (toEnemy.sqrMagnitude < 0.0001f) toEnemy = Forward(self.Yaw, 0f);

            if (exposing)
            {
                intent = Engage(self, intent, now);

                if (!_peekExposing)
                {
                    _peekOrigin = self.Position;
                    _peekCount++;
                    Game.Logger.Info(Tag,
                        $"peek 探身第 {_peekCount} 次：{_name}（{_difficulty}）距敌 " +
                        $"{Vector3.Distance(self.Position, target.Position):F1}m，已贴掩体 " +
                        $"{now - _coveredSince:F1}s → 站起 {PeekExposeSeconds:F1}s（周期 {PeekCycleSeconds:F1}s，" +
                        $"向敌平移 {PeekOffsetMeters:F1}m）");
                }

                // 平移方向先看一格可走：探身那 0.5s 顶着墙推，会变成一条"站住不动"的卡住告警。
                var moved = HorizontalDistance(self.Position, _peekOrigin);
                var stepOk = _map == null || !_map.IsLoaded ||
                             _map.WalkableAt(self.Position.x + toEnemy.x * PeekOffsetMeters,
                                             self.Position.z + toEnemy.z * PeekOffsetMeters);
                intent.Move = moved < PeekOffsetMeters && stepOk ? toEnemy : Vector3.zero;
                intent.Crouch = false;
                _peekExposing = true;
                return BtStatus.Running;
            }

            _peekExposing = false;
            intent.Fire = false;
            intent.Move = Vector3.zero;
            intent.Crouch = true;
            intent.AimPoint = target.EyePosition;
            SetState(CsBotState.Engage, now);
            intent.State = CsBotState.Engage;
            return BtStatus.Running;
        }

        /// <summary>身边 <see cref="PeekProbeRadius"/> 内是否存在"敌人眼看不到"的实心点（= 贴着掩体）。</summary>
        private bool CoverAdjacent(CsActor self, CsActor enemy)
        {
            for (var i = 0; i < PeekProbeCount; i++)
            {
                var dir = Quaternion.Euler(0f, i * (360f / PeekProbeCount), 0f) * Vector3.forward;
                var probe = self.Position + dir * PeekProbeRadius + Vector3.up * CsConst.EyeHeight;
                if (LineBlockedByWorld(enemy.EyePosition, probe)) return true;
            }

            return false;
        }

        /// <summary>清掉 peek 的贴掩体计时与循环起点（条件不成立时调用）。</summary>
        private void ResetPeek()
        {
            if (_peekExposing) _peekExposing = false;
            _coveredSince = 0f;
            _peekCycleStart = 0f;
        }



        // ==================================================================
        //  狙击蹲守
        // ==================================================================
        // 出处：原版 `cs_bot.h:438` 的 `TaskType::SNIPING` / `MOVE_TO_SNIPER_SPOT`，以及
        // `cs_bot.h:640` 的 `IsSniping()` 注释（moving to sniper spot or settled in）。
        // 开镜（scope）由模拟按武器档位推进，Bot 侧没有开镜意图位 ⇒ 这里管住"站定不动 + 蹲下 + 只在
        // 准星 ±3° 内开火"，用站定保证不出现"带镜跑动"。
        /// <summary>持狙蹲守的敌人距离下限（米）：<see cref="CsBotConst.SniperRange"/>(30m) 之上再收一档。</summary>
        private const float SniperHoldRange = 35f;

        /// <summary>被近身到这个距离（米）就切手枪（原版 sniper 的近距离处置）。</summary>
        private const float SniperFallbackRange = 12f;

        /// <summary>蹲守时允许开火的准星夹角（度）：敌人没进准星就不开枪（AWP 一发定生死）。</summary>
        private const float SniperFireConeDegrees = 3f;

        /// <summary>本回合第几次蹲守（日志计数）。</summary>
        private int _sniperHoldCount;

        /// <summary>蹲守日志已记过的回合 / 敌人（同一次蹲守只记一行）。</summary>
        private int _sniperHoldRound = -1;
        private long _sniperHoldTarget;

        /// <summary>是否该走狙击蹲守：手持狙，且（可见敌人 &gt; <see cref="SniperHoldRange"/> 或 敌人 &lt; <see cref="SniperFallbackRange"/>）。</summary>
        private bool SniperHoldDue(CsActor self)
        {
            if (self == null || !self.IsAlive) return false;

            var def = self.ActiveDef;
            if (def == null || def.Class != CsWeaponClass.Sniper) return false;

            var target = _targetId != 0 ? _match.Find(_targetId) : null;
            if (target == null || !target.IsAlive) return false;

            var dist = Vector3.Distance(self.EyePosition, target.EyePosition);
            if (dist < SniperFallbackRange) return true;
            return _visibleNow && dist > SniperHoldRange;
        }

        /// <summary>
        /// 狙击分支：近身（&lt; 12m）切副武器；远距（&gt; 35m 且可见）站定蹲下，
        /// 只在提交瞄向与瞄点夹角 ≤ <see cref="SniperFireConeDegrees"/> 时扣扳机。
        /// </summary>
        private BtStatus SniperHoldStep(CsActor self, ref CsBotIntent intent, float now)
        {
            var target = _match.Find(_targetId);
            if (target == null || !target.IsAlive) return BtStatus.Failure;

            var dist = Vector3.Distance(self.EyePosition, target.EyePosition);
            intent.Move = Vector3.zero;
            intent.Walk = false;
            intent.AimPoint = _targetAimPoint.sqrMagnitude > 0.0001f ? _targetAimPoint : target.EyePosition;

            if (dist < SniperFallbackRange)
            {
                var secondary = self.SecondaryWeapon;
                if (!string.IsNullOrEmpty(secondary) && self.ActiveWeapon != secondary)
                {
                    intent.SwitchTo = secondary;
                    intent.Fire = false;
                    intent.Crouch = false;
                    SetState(CsBotState.Engage, now);
                    intent.State = CsBotState.Engage;
                    Game.Logger.Info(Tag,
                        $"狙近身：{_name}（{_difficulty}）距敌 {dist:F1}m < {SniperFallbackRange:F0}m → " +
                        $"切副武器 {secondary}（{self.ActiveWeapon} 收枪）");
                    return BtStatus.Success;
                }

                return BtStatus.Failure;
            }

            // 蹲守：站定 + 蹲下；开火只看"子弹方向 vs 敌人"的夹角（SubmittedAimAngle 就是这一发的飞行方向）。
            var speed = self.Velocity;
            var horizontalSpeed = new Vector2(speed.x, speed.z).magnitude;
            var angle = SubmittedAimAngle(self, intent.AimPoint);
            var clear = AimPathClear(self, intent.AimPoint, out _);
            var ammo = self.ActiveDef != null ? self.GetAmmo(self.ActiveDef.Id) : (inMag: 0, reserve: 0);
            var canFire = _visibleNow &&
                          angle <= SniperFireConeDegrees &&
                          clear &&
                          self.OnGround && horizontalSpeed <= CsBotConst.FireStandSpeed &&
                          now >= self.FlashEndTime &&
                          (self.ActiveDef == null || self.ActiveDef.Magazine <= 0 || ammo.inMag > 0);

            intent.Crouch = true;
            intent.Fire = canFire;

            if (_sniperHoldRound != _match.RoundNumber || _sniperHoldTarget != _targetId)
            {
                _sniperHoldRound = _match.RoundNumber;
                _sniperHoldTarget = _targetId;
                _sniperHoldCount++;
                Game.Logger.Info(Tag,
                    $"狙蹲守 第 {_sniperHoldCount} 次：{_name}（{_difficulty}）持 {self.ActiveWeapon}，" +
                    $"距敌 {dist:F1}m > {SniperHoldRange:F0}m → 站定蹲下，只在准星 ±{SniperFireConeDegrees:F0}° 内开火" +
                    $"（当前夹角 {angle:F1}°，弹道{(clear ? "通" : "被挡")}）");
            }

            SetState(CsBotState.Engage, now);
            intent.State = CsBotState.Engage;
            return BtStatus.Success;
        }



        // ==================================================================
        //  片 C 的树叶子（挂点见 CsBotBrain.Tree.cs 的 engage-body）
        // ==================================================================
        /// <summary>掩体分支：选点 → 走过去 → 蹲 1.2s → 结束（外层冷却由 <c>BtCooldown</c> 给）。</summary>
        private sealed class LeafHide : BtNode
        {
            public LeafHide() { Name = "engage-hide"; }

            public override BtStatus Tick(CsBotBrain b, float dt, float now)
                => b.HideStep(b._tickSelf, ref b._tickIntent, now);
        }

        /// <summary>Peek 分支：蹲着藏 / 起身探的 2s 循环。</summary>
        private sealed class LeafPeek : BtNode
        {
            public LeafPeek() { Name = "engage-peek"; }

            public override BtStatus Tick(CsBotBrain b, float dt, float now)
                => b.PeekStep(b._tickSelf, ref b._tickIntent, now);
        }

        /// <summary>狙击分支：远距蹲守 / 近身切手枪。</summary>
        private sealed class LeafSniperHold : BtNode
        {
            public LeafSniperHold() { Name = "engage-sniper-hold"; }

            public override BtStatus Tick(CsBotBrain b, float dt, float now)
                => b.SniperHoldStep(b._tickSelf, ref b._tickIntent, now);
        }

    }
}
