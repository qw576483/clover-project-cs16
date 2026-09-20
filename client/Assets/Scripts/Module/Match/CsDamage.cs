using System.Collections.Generic;
using CloverEngine;
using Cs16.Core;
using UnityEngine;

namespace Cs16.Module.Match
{
    /// <summary>
    /// 伤害模型与击杀结算。
    ///
    /// <para>公式（任务书 §4.4）：<c>base = def.Damage</c> → × 部位倍率 → × 距离衰减
    /// <c>(1 - def.FalloffPerMeter * dist)</c> → × 穿墙保留 → 护甲吸收
    /// <c>(1 - ArmorAbsorbRatio * (1 - def.ArmorPenetration))</c>，护甲损耗 <c>damage * ArmorDamageRatio</c>。
    /// 头盔只减免头部。</para>
    ///
    /// <para><b>高频降频</b>：伤害日志按「首次 + 每 50 次」输出（见 <see cref="CsMatch.RateInfo"/>）。</para>
    /// </summary>
    public sealed class CsDamage
    {
        private const string Tag = CsMatch.Tag;

        private readonly CsMatch _m;
        private readonly Dictionary<long, Dictionary<long, int>> _damageByVictim =
            new Dictionary<long, Dictionary<long, int>>();
        private readonly HashSet<long> _explosionHit = new HashSet<long>();
        private readonly Collider[] _overlap = new Collider[64];

        internal CsDamage(CsMatch m)
        {
            _m = m;
        }

        public void Reset()
        {
            _damageByVictim.Clear();
            _explosionHit.Clear();
        }

        // ==================================================================
        //  命中（射击模块 / 机器人射线 → 这里）
        // ==================================================================
        public void ApplyHit(CsActor shooter, CsActor victim, CsWeaponDef def, CsHitbox box,
            Vector3 point, float dist, bool throughWall)
        {
            if (def == null)
            {
                _m.RateWarn("hit.nodef", "ApplyHit：武器定义为空，本次命中已忽略");
                return;
            }
            if (victim == null || !victim.IsAlive) return;

            var dmg = def.Damage * HitboxMultiplier(box);

            var falloff = 1f - def.FalloffPerMeter * Mathf.Max(0f, dist);
            if (falloff < 0f) falloff = 0f;
            dmg *= falloff;

            ApplyDamage(shooter, victim, def, box, dmg, throughWall);
        }

        private static float HitboxMultiplier(CsHitbox box)
        {
            switch (box)
            {
                case CsHitbox.Head: return CsConst.HitHead;
                case CsHitbox.Chest: return CsConst.HitChest;
                case CsHitbox.Stomach: return CsConst.HitStomach;
                case CsHitbox.Leg: return CsConst.HitLeg;
                case CsHitbox.Generic: return CsConst.HitChest;
                default:
                    Game.Logger.Warn(Tag, $"未知命中部位 {box}，按胸部倍率处理");
                    return CsConst.HitChest;
            }
        }

        /// <summary>统一的伤害落地：队友伤害判定 → 穿墙衰减 → 护甲吸收 → 扣血 → 事件 → 致死结算。</summary>
        private void ApplyDamage(CsActor attacker, CsActor victim, CsWeaponDef def, CsHitbox box,
            float rawDamage, bool throughWall)
        {
            if (victim == null || !victim.IsAlive) return;
            if (rawDamage <= 0f) return;

            // 队友伤害（默认关）。自己打自己（如手雷）不受此限。
            if (attacker != null && attacker.Id != victim.Id && attacker.Team == victim.Team)
            {
                var cfg = _m.Cfg;
                if (cfg != null && !cfg.FriendlyFire)
                {
                    _m.RateInfo("damage.ff.blocked",
                        $"{attacker.Name} 命中队友 {victim.Name}，友好伤害关闭 → 伤害被忽略");
                    return;
                }
            }

            var dmg = rawDamage;
            if (throughWall) dmg *= CsMatchConst.PenetrationDamageScale;

            var armorPen = def != null ? def.ArmorPenetration : 0f;

            // 头盔只减免头部：头部命中且无头盔时护甲不生效。
            var armorApplies = victim.Armor > 0 && (box != CsHitbox.Head || victim.HasHelmet);
            var armorLoss = 0;
            if (armorApplies)
            {
                armorLoss = Mathf.RoundToInt(dmg * CsConst.ArmorDamageRatio);
                dmg *= 1f - CsConst.ArmorAbsorbRatio * (1f - armorPen);
            }

            var final = Mathf.RoundToInt(dmg);
            if (final <= 0)
            {
                _m.RateInfo("damage.zero", $"{victim.Name} 受到的伤害被护甲完全吸收（原始 {rawDamage:F1}）");
                return;
            }

            victim.Health -= final;
            if (armorLoss > 0) victim.Armor = Mathf.Max(0, victim.Armor - armorLoss);

            var headshot = box == CsHitbox.Head;
            var lethal = victim.Health <= 0;

            RecordDamage(attacker, victim, final);
            WriteLocalDamageIndicator(attacker, victim);

            _m.RateInfo($"damage.hit.{box}",
                $"{NameOf(attacker)} → {victim.Name} [{(def != null ? def.Id : "world")}/{box}] " +
                $"伤害 {final}（原始 {rawDamage:F1}，穿墙={throughWall}，护甲损耗 {armorLoss}，" +
                $"剩余 HP {Mathf.Max(0, victim.Health)}）{(headshot ? " 爆头" : string.Empty)}{(lethal ? " 致死" : string.Empty)}");

            _m.RaiseDamaged(victim, final, headshot, lethal);

            if (lethal) Kill(attacker, victim, def, headshot);
        }

        private static string NameOf(CsActor a)
        {
            return a != null ? a.Name : "环境";
        }

        private void WriteLocalDamageIndicator(CsActor attacker, CsActor victim)
        {
            if (victim != _m.Local) return;
            if (attacker == null || attacker.Id == victim.Id) return;

            CsHudSnapshot.DamageIndicatorTime = CsConst.DamageNumberTime;

            var to = attacker.Position - victim.Position;
            var ang = Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg - victim.Yaw;
            CsHudSnapshot.DamageFromYaw = Mathf.DeltaAngle(0f, ang);
        }

        // ==================================================================
        //  助攻记录
        // ==================================================================
        private void RecordDamage(CsActor attacker, CsActor victim, int dmg)
        {
            if (attacker == null || attacker.Id == victim.Id) return;

            if (!_damageByVictim.TryGetValue(victim.Id, out var map))
            {
                map = new Dictionary<long, int>();
                _damageByVictim[victim.Id] = map;
            }
            map.TryGetValue(attacker.Id, out var cur);
            map[attacker.Id] = cur + dmg;
        }

        private long TakeAssister(long victimId, long killerId)
        {
            if (!_damageByVictim.TryGetValue(victimId, out var map)) return 0;
            _damageByVictim.Remove(victimId);

            long best = 0;
            var bestDmg = 0;
            foreach (var kv in map)
            {
                if (kv.Key == killerId) continue;
                if (kv.Value > bestDmg)
                {
                    bestDmg = kv.Value;
                    best = kv.Key;
                }
            }
            if (best == 0) return 0;

            var a = _m.Find(best);
            if (a != null)
            {
                a.Assists++;
                return best;
            }
            return 0;
        }

        // ==================================================================
        //  击杀
        // ==================================================================
        private void Kill(CsActor killer, CsActor victim, CsWeaponDef def, bool headshot)
        {
            if (victim == null || !victim.IsAlive) return;

            victim.IsAlive = false;
            victim.Health = 0;
            victim.Velocity = Vector3.zero;
            victim.UseProgress = -1f;
            victim.Deaths++;
            victim.RoundKills = 0;

            long assisterId = 0;
            if (killer != null && killer.Id != victim.Id && killer.IsAlive)
            {
                killer.Kills++;
                killer.RoundKills++;
                killer.Score += CsMatchConst.ScorePerKill;

                var reward = def != null ? def.KillReward : 0;
                if (reward > 0)
                {
                    _m.Economy.Add(killer, reward, $"击杀 {victim.Name}（{def.DisplayName}）");
                }
                else
                {
                    _m.RateWarn("kill.noreward",
                        $"击杀 {victim.Name} 的武器 {(def != null ? def.Id : "world")} 击杀奖励为 0");
                }

                assisterId = TakeAssister(victim.Id, killer.Id);
            }
            else
            {
                // 自杀 / 环境击杀：不算击杀者
                killer = null;
                TakeAssister(victim.Id, 0);
            }

            _m.OnActorDied(victim);
            _m.OnKilled(killer, victim, def != null ? def.Id : "world", headshot, assisterId);
        }

        // ==================================================================
        //  手雷：HE 半径伤害
        // ==================================================================
        public void ApplyHeExplosion(Vector3 center, CsActor owner, CsTeam ownerTeam)
        {
            _explosionHit.Clear();

            var radius = CsConst.GrenadeHeRadius;
            var count = Physics.OverlapSphereNonAlloc(center, radius, _overlap, ~0, QueryTriggerInteraction.Ignore);

            Game.Logger.Info(Tag,
                $"HE 手雷爆炸于 {center}（半径 {radius:F1}m，最大伤害 {CsConst.GrenadeHeMaxDamage:F0}），" +
                $"命中 {count} 个碰撞体");

            for (var i = 0; i < count; i++)
            {
                var col = _overlap[i];
                if (col == null) continue;

                var proxy = col.GetComponentInParent<CsHitboxProxy>();
                if (proxy == null) continue;
                if (!_explosionHit.Add(proxy.ActorId)) continue;   // 同一 actor 的多个受体只算一次

                var victim = _m.Find(proxy.ActorId);
                if (victim == null || !victim.IsAlive) continue;

                var dist = Vector3.Distance(center, victim.Position);
                if (dist > radius) continue;

                // 简化遮挡：爆心到眼睛没有墙才吃满伤害，被墙挡住则伤害减半。
                var blocked = !_m.HasLineOfSight(center, victim.EyePosition, radius);

                var factor = 1f - dist / radius;
                if (factor < 0f) factor = 0f;
                var dmg = CsConst.GrenadeHeMaxDamage * factor;
                if (blocked) dmg *= CsMatchConst.PenetrationDamageScale;

                ApplyDamage(owner, victim, null, CsHitbox.Generic, dmg, false);
            }
        }

        // ==================================================================
        //  手雷：闪光
        // ==================================================================
        public void ApplyFlash(Vector3 center, CsActor owner, CsTeam ownerTeam)
        {
            var radius = CsMatchConst.FlashRadius;
            var list = _m.ActorList;

            var affected = 0;
            for (var i = 0; i < list.Count; i++)
            {
                var a = list[i];
                if (!a.IsAlive) continue;
                if (a.Team != CsTeam.T && a.Team != CsTeam.CT) continue;

                var eye = a.EyePosition;
                var dist = Vector3.Distance(center, eye);
                if (dist > radius) continue;
                if (!_m.HasLineOfSight(center, eye, radius)) continue;

                var distFactor = 1f - dist / radius;

                // 背对着闪光弹 → 影响小（用视线方向与"指向闪光"方向的夹角）。
                var toFlash = (center - eye).normalized;
                var fwd = CsInventory.AimDirection(a);
                // (dot + 1) / 2 只是把 [-1,1] 线性重映射到 [0,1]（正对=1、背对=0），
                // 是纯数学归一化，不是可调数值 —— 与 CsConst 里的任何常量无关。
                var dot = Vector3.Dot(fwd, toFlash);
                var angleFactor = Mathf.Clamp01((dot + 1f) * 0.5f);

                var duration = CsConst.GrenadeFlashDuration * distFactor * angleFactor;
                if (duration <= 0f) continue;

                a.FlashEndTime = Mathf.Max(a.FlashEndTime, _m.Now + duration);
                affected++;

                Game.Logger.Info(Tag,
                    $"闪光弹致盲 {a.Name}：{duration:F2}s（距离 {dist:F1}m，朝向系数 {angleFactor:F2}）");
            }

            if (affected == 0)
            {
                _m.RateInfo("flash.nohit", $"闪光弹于 {center} 爆炸，但没有致盲任何目标（被墙挡住或距离过远）");
            }
        }

        // ==================================================================
        //  C4 爆炸
        // ==================================================================
        public void ApplyBombExplosion(Vector3 center)
        {
            _explosionHit.Clear();

            var radius = CsMatchConst.BombExplosionRadius;
            var count = Physics.OverlapSphereNonAlloc(center, radius, _overlap, ~0, QueryTriggerInteraction.Ignore);

            for (var i = 0; i < count; i++)
            {
                var col = _overlap[i];
                if (col == null) continue;

                var proxy = col.GetComponentInParent<CsHitboxProxy>();
                if (proxy == null) continue;
                if (!_explosionHit.Add(proxy.ActorId)) continue;

                var victim = _m.Find(proxy.ActorId);
                if (victim == null || !victim.IsAlive) continue;

                var dist = Vector3.Distance(center, victim.Position);
                if (dist > radius) continue;

                var factor = 1f - dist / radius;
                if (factor < 0f) factor = 0f;
                var dmg = CsMatchConst.BombExplosionMaxDamage * factor;

                // C4 不分敌我（attacker = null → 跳过队友伤害判定）。
                ApplyDamage(null, victim, null, CsHitbox.Generic, dmg, false);
            }
        }
    }
}
