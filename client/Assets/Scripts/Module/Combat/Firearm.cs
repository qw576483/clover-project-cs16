using System.Collections.Generic;
using CloverEngine;
using Cs16.Core;
using Cs16.Module.Match;
using UnityEngine;

namespace Cs16.Module.Combat
{
    /// <summary>一次枪械射击的射线请求（由 <see cref="CombatModule"/> 组好）。</summary>
    internal struct ShotRequest
    {
        /// <summary>射手 actor id（用于跳过"自己的受击体"）。</summary>
        public long ShooterId;

        /// <summary>武器定义（射程 / 弹丸数 / 穿透力都从这里取）。</summary>
        public CsWeaponDef Def;

        /// <summary>射线起点（眼睛位置）。</summary>
        public Vector3 Origin;

        /// <summary>基准方向（含后坐力表现的单位向量）。</summary>
        public Vector3 Direction;

        /// <summary>本发的散布角（度）。</summary>
        public float Spread;

        /// <summary>射程（米）。</summary>
        public float Range;

        /// <summary>弹丸数（霰弹 &gt; 1）。</summary>
        public int Pellets;
    }

    /// <summary>
    /// 枪械射击的"射线解算"：散布 → 逐弹丸射线 → 命中部位 / 穿墙 → 组 <see cref="CsHitInfo"/>。
    ///
    /// <para><b>归谁</b>：引擎**不提供**射线 API（明确属业务自写），所以这里用 Unity 的
    /// <c>Physics.RaycastNonAlloc</c>。伤害结算不在这里 —— 命中结果由
    /// <see cref="CombatModule"/> 通过 <c>ICsMatch.ReportHit</c> 交回比赛模拟（agent-03）。</para>
    ///
    /// <para><b>为什么用 NonAlloc + 手动排序</b>：全自动武器每秒 10+ 发、霰弹一发 9 个弹丸，
    /// 每发都 <c>RaycastAll</c> 会持续产生垃圾；这里复用一块 <see cref="RaycastHit"/> 缓冲，
    /// 只对命中的前 <c>count</c> 项做插入排序（count ≤ 32，插入排序最快）。</para>
    ///
    /// <para><b>结果缓冲复用</b>：<see cref="Hits"/> / <see cref="TracerEnds"/> 每次 <see cref="Fire"/> 清空重填，
    /// 调用方**当帧用完即可**，不要跨帧持有。</para>
    /// </summary>
    internal sealed class Firearm
    {
        /// <summary>射线未给射程时的兜底（<c>CsWeaponDef.Range</c> 对枪械恒 &gt; 0）。</summary>
        private const float FallbackRange = 100f;

        private readonly RaycastHit[] _hits = new RaycastHit[CsCombatTuning.MaxRayHits];
        private readonly List<CsHitInfo> _hitBuffer = new List<CsHitInfo>(16);
        private readonly List<Vector3> _tracerEnds = new List<Vector3>(16);
        private readonly List<Vector3> _impactPoints = new List<Vector3>(16);
        private readonly List<Vector3> _impactNormals = new List<Vector3>(16);
        private readonly List<string> _impactMaterials = new List<string>(16);
        private int _bufferOverflowCount;

        /// <summary>本发命中的角色（霰弹一发可能命中多个目标）。</summary>
        public IReadOnlyList<CsHitInfo> Hits => _hitBuffer;

        /// <summary>各弹丸的终点（给弹道表现用）。</summary>
        public IReadOnlyList<Vector3> TracerEnds => _tracerEnds;

        /// <summary>
        /// 各弹丸**打在墙上**的落点 / 法线（给"弹痕 + 火星"用；打到角色身上的那一发不算）。
        /// 只记每发弹丸**第一层**墙（穿透后的第二层不再留第二枚弹痕 —— 原版 decal 也只贴在入射面）。
        /// </summary>
        public IReadOnlyList<Vector3> ImpactPoints => _impactPoints;

        /// <inheritdoc cref="ImpactPoints"/>
        public IReadOnlyList<Vector3> ImpactNormals => _impactNormals;

        /// <summary>
        /// 各弹丸打在墙上那一点的**材质名**（与 <see cref="ImpactPoints"/> 一一对应）。
        /// 来源：命中碰撞体所在物体的 <c>MeshRenderer.sharedMaterial.name</c>
        /// （= BSP 贴图组名，如 <c>csSandWallDJ1</c>）；阻挡盒没有 MeshRenderer ⇒ 退到节点名
        /// （<c>Blocker_*</c>）。切片K（D8）用它做 <c>hit_wall</c> 的"按材质分流"
        /// （分类口径见 <c>CsAudioTuning.ClassifyImpact</c>）。
        /// </summary>
        public IReadOnlyList<string> ImpactMaterials => _impactMaterials;

        // ==================================================================
        //  散布（纯函数，便于自证）
        // ==================================================================
        /// <summary>移动对散布的贡献系数（0 = 静止，1 = 达到步枪满速）。</summary>
        public static float MoveFactor(CsActor actor)
        {
            if (actor == null) return 0f;
            var v = actor.Velocity;
            var speed = new Vector2(v.x, v.z).magnitude;
            return Mathf.Clamp01(speed / CsConst.SpeedRifle);
        }

        /// <summary>
        /// 本发子弹的散布角（度）。输入是**纯数据**（不依赖 actor），便于离线自证。
        ///
        /// <para>口径：基础散布 + 移动附加 + 离地附加 + 连射累积惩罚，再乘（蹲下 / 开镜）的收敛系数。
        /// 与官方 CS 一致的三条手感：移动射击散、跳射更散、开镜极准、蹲下略准。</para>
        /// </summary>
        public static float ComputeSpread(CsWeaponDef def, float moveFactor, bool onGround, bool crouched,
            int consecutiveShots, bool scopedAccurate)
        {
            if (def == null) return 0f;

            var spread = def.Spread;
            spread += def.MoveSpread * Mathf.Clamp01(moveFactor);
            if (!onGround) spread += def.MoveSpread * CsCombatTuning.AirSpreadScale;

            var recoilPenalty = Mathf.Min(consecutiveShots * CsCombatTuning.RecoilSpreadPerShot,
                CsCombatTuning.RecoilSpreadMax);
            spread += Mathf.Max(0f, recoilPenalty);

            if (crouched) spread *= CsCombatTuning.CrouchSpreadScale;
            if (scopedAccurate) spread *= CsCombatTuning.ZoomSpreadScale;

            return spread;
        }

        /// <summary>按 actor 当前状态算散布（口径见上）。</summary>
        public static float ComputeSpread(CsActor shooter, CsWeaponDef def, bool scopedAccurate)
        {
            if (shooter == null) return ComputeSpread(def, 0f, true, false, 0, scopedAccurate);

            return ComputeSpread(def, MoveFactor(shooter), shooter.OnGround, shooter.IsCrouching,
                shooter.ConsecutiveShots, scopedAccurate);
        }

        /// <summary>按散布角给方向加随机偏移（与比赛模拟内部的 <c>ApplySpread</c> 同口径）。</summary>
        public static Vector3 ApplySpread(Vector3 dir, float spreadDegrees)
        {
            if (spreadDegrees <= 0f) return dir;
            var rot = Quaternion.Euler(
                UnityEngine.Random.Range(-spreadDegrees, spreadDegrees),
                UnityEngine.Random.Range(-spreadDegrees, spreadDegrees),
                0f);
            return (rot * dir).normalized;
        }

        // ==================================================================
        //  开火
        // ==================================================================
        /// <summary>打一发（内部按 <see cref="ShotRequest.Pellets"/> 循环弹丸）。</summary>
        public void Fire(in ShotRequest req)
        {
            _hitBuffer.Clear();
            _tracerEnds.Clear();
            _impactPoints.Clear();
            _impactNormals.Clear();
            _impactMaterials.Clear();

            var pellets = req.Pellets > 0 ? req.Pellets : 1;
            var range = req.Range > 0f ? req.Range : FallbackRange;

            for (var p = 0; p < pellets; p++)
            {
                var dir = ApplySpread(req.Direction, req.Spread);
                var end = ResolvePellet(req, dir, range);
                _tracerEnds.Add(end);
            }
        }

        /// <summary>
        /// 单个弹丸的射线解算：取最近的一批命中，按「先角色、后墙」的顺序判定，
        /// 墙是否可穿透看 <see cref="CsWeaponDef.ArmorPenetration"/>。返回终点（画弹道用）。
        /// </summary>
        private Vector3 ResolvePellet(in ShotRequest req, Vector3 dir, float range)
        {
            var count = Physics.RaycastNonAlloc(req.Origin, dir, _hits, range, ~0, QueryTriggerInteraction.Ignore);
            if (count <= 0) return req.Origin + dir * range;

            if (count >= _hits.Length)
            {
                // 缓冲区被打满：可能漏掉更远的目标。高频路径 → 降频日志（首次 + 每 50 次）。
                _bufferOverflowCount++;
                if (_bufferOverflowCount == 1 || _bufferOverflowCount % CsCombatTuning.LogRateEvery == 0)
                {
                    Game.Logger.Warn("Combat",
                        $"单发射线命中数达到缓冲上限 {_hits.Length}（同类第 {_bufferOverflowCount} 次）：" +
                        "可能有更远的目标被漏掉，酌情提高 CsCombatTuning.MaxRayHits");
                }
            }

            SortByDistance(count);

            var canPenetrate = req.Def != null &&
                               req.Def.ArmorPenetration > CsCombatTuning.PenetrationArmorThreshold;
            var walls = 0;
            var end = req.Origin + dir * range;

            for (var i = 0; i < count; i++)
            {
                var h = _hits[i];
                if (h.collider == null) continue;

                var proxy = h.collider.GetComponentInParent<CsHitboxProxy>();
                if (proxy != null)
                {
                    // 自己的受击体：跳过（第一人称从眼睛出发必然打到自己身上）
                    if (proxy.ActorId == req.ShooterId) continue;

                    _hitBuffer.Add(new CsHitInfo
                    {
                        ShooterId = req.ShooterId,
                        VictimId = proxy.ActorId,
                        WeaponId = req.Def != null ? req.Def.Id : null,
                        Hitbox = proxy.Hitbox,
                        Point = h.point,
                        Normal = h.normal,
                        Distance = h.distance,
                        ThroughWall = walls > 0,
                    });
                    return h.point;
                }

                // 非角色碰撞体 = 一层墙
                if (walls == 0)
                {
                    // 入射面 = 弹痕落点（穿透后的第二层不再记，与原版"decal 贴在入射面"一致）
                    _impactPoints.Add(h.point);
                    _impactNormals.Add(h.normal);
                    _impactMaterials.Add(ImpactMaterialName(h.collider));
                }
                walls++;
                end = h.point;
                if (!canPenetrate || walls > CsCombatTuning.MaxPenetrationLayers) return end;
            }

            // 穿完了也没打到人（或全被跳过）
            return end;
        }

        /// <summary>
        /// 取命中物的**材质名**（给 <c>hit_wall</c> 的材质分流用）：
        /// 优先 MeshRenderer 的 sharedMaterial（= <c>de_dust2_geo.bin</c> 的贴图组名，与
        /// <c>ThirdParty/Dust2/Textures/*.png</c> 同名）；阻挡盒（只有 BoxCollider）没有 MeshRenderer
        /// ⇒ 退到它自己的节点名（<c>Blocker_*</c>）。返回 null 时分类落 <c>unknown</c>。
        /// </summary>
        private static string ImpactMaterialName(Collider collider)
        {
            if (collider == null) return null;
            var renderer = collider.GetComponent<MeshRenderer>();
            if (renderer != null && renderer.sharedMaterial != null) return renderer.sharedMaterial.name;
            return collider.gameObject != null ? collider.gameObject.name : null;
        }

        /// <summary>对缓冲前 count 项按距离做插入排序（小数组，避免 Array.Sort 的委托分配）。</summary>
        private void SortByDistance(int count)
        {
            for (var i = 1; i < count; i++)
            {
                var cur = _hits[i];
                var j = i - 1;
                while (j >= 0 && _hits[j].distance > cur.distance)
                {
                    _hits[j + 1] = _hits[j];
                    j--;
                }
                _hits[j + 1] = cur;
            }
        }
    }
}
