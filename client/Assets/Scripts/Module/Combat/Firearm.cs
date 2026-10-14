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
    /// <see cref="CombatModule"/> 通过 <c>ICsMatch.ReportHit</c> 交回比赛模拟。</para>
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
        private readonly List<Collider> _impactColliders = new List<Collider>(16);
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
        /// （分类口径见 <c>CsAudioTuning.ClassifyImpact</c>）。
        /// </summary>
        public IReadOnlyList<string> ImpactMaterials => _impactMaterials;

        /// <summary>
        /// 各弹丸打在墙上那一点的**碰撞体**（与 <see cref="ImpactPoints"/> 一一对应）。
        /// 破坏判定用它取到 <see cref="CsBreakable"/>（木箱那种"打得碎的世界几何"）。
        /// </summary>
        public IReadOnlyList<Collider> ImpactColliders => _impactColliders;

        // ==================================================================
        //  散布（纯函数，便于自证）
        // ==================================================================
        /// <summary>移动对散布的贡献系数（0 = 静止，1 = 达到**当前手持武器**的满速）。</summary>
        public static float MoveFactor(CsActor actor)
        {
            if (actor == null) return 0f;
            var v = actor.Velocity;
            var speed = new Vector2(v.x, v.z).magnitude;
            var reference = CsInventory.MoveReferenceSpeed(actor.ActiveDef);
            if (reference <= 0f) return 0f;
            return Mathf.Clamp01(speed / reference);
        }

        /// <summary>
        /// 本发子弹的散布角（度）。输入是**纯数据**（不依赖 actor），便于离线自证。
        ///
        /// <para>口径：基础散布 + 移动附加 + 离地附加 + 连射累积惩罚，再乘（蹲下 / 开镜）的收敛系数。
        /// 与官方 CS 一致的三条手感：移动射击散、跳射更散、开镜极准、蹲下略准。</para>
        /// </summary>
        public static float ComputeSpread(CsWeaponDef def, float moveFactor, bool onGround, bool crouched,
            int consecutiveShots, bool scopedAccurate, bool silenced = false, bool burst = false,
            float acc = -1f, float speedUnits = -1f)
        {
            if (def == null) return 0f;

            // 差异 #94：档位系数与 acc 模型都取到的武器走原版公式（`Add + Mul×acc` / `Mul×(1−acc)`，
            // 移动 / 离地由档位本身表达，不再叠加 MoveSpread）；其余武器沿用差异 #68 的两态系数 + 附加。
            var originalModel = acc >= 0f && CsWeapons.HasSpreadCoefficients(def);

            // 差异 #94：**绝对档族**（AWP / Scout / G3SG1）的原版判据是**绝对速度**（140 / 170 / 0），
            // 不是"任何移动"⇒ 只有这一族（或带慢移档的武器）改用 speedUnits 判档；
            // 其余武器保持 `moveFactor > 0f`（行为一字不变 —— 正控）。
            var bySpeed = (def.SpreadAbsolute || def.SpreadSlowSpeedUnits > 0f) && speedUnits >= 0f;
            var moving = bySpeed ? CsWeapons.IsMovingTier(def, speedUnits) : moveFactor > 0f;
            var slow = bySpeed && CsWeapons.IsSlowTier(def, speedUnits);

            float spread;
            if (originalModel)
            {
                spread = CsWeapons.SpreadFor(def, acc, onGround, moving, crouched, silenced, burst, slow);
            }
            else
            {
                spread = CsWeapons.StaticSpreadFor(def, silenced, burst);
                spread += def.MoveSpread * Mathf.Clamp01(moveFactor);
                if (!onGround) spread += def.MoveSpread * CsCombatTuning.AirSpreadScale;
            }

            // 连射累积：走原版 acc 模型的武器，累积已经由 `acc = base + shots^power / div`
            // 表达（mp.dll 各开火函数写 `[this+0xF8]` 的那条，见 CsWeapons.ApplyAccuracyValues）
            // ⇒ 不再叠一层线性惩罚；只有没有 acc 模型的武器才走本工程的自定线性档。
            if (!originalModel)
            {
                var recoilPenalty = Mathf.Min(consecutiveShots * CsCombatTuning.RecoilSpreadPerShot,
                    CsCombatTuning.RecoilSpreadMax);
                spread += Mathf.Max(0f, recoilPenalty);
            }

            // 原版公式里蹲档已经在档位系数内（只有手枪族有这一档）⇒ 不再叠一次通用蹲伏收敛。
            if (crouched && !originalModel) spread *= CsCombatTuning.CrouchSpreadScale;
            if (scopedAccurate) spread *= CsCombatTuning.ZoomSpreadScale;

            return spread;
        }

        /// <summary>按 actor 当前状态算散布（口径见上；差异 #94 起把 actor 的 acc 一起带进去）。</summary>
        public static float ComputeSpread(CsActor shooter, CsWeaponDef def, bool scopedAccurate)
        {
            if (shooter == null) return ComputeSpread(def, 0f, true, false, 0, scopedAccurate);

            return ComputeSpread(def, MoveFactor(shooter), shooter.OnGround, shooter.IsCrouching,
                shooter.ConsecutiveShots, scopedAccurate, shooter.Silenced, shooter.BurstMode,
                shooter.Accuracy, SpeedUnits(shooter));
        }

        /// <summary>水平速度（unit/s）—— 与 <c>CsInventory.FireSpread</c> 同一折算口径（差异 #94 绝对档判据用）。</summary>
        public static float SpeedUnits(CsActor actor)
        {
            if (actor == null) return 0f;
            var v = actor.Velocity;
            return new Vector2(v.x, v.z).magnitude / CsConst.UnitToMeter;
        }

        /// <summary>
        /// 按散布角给方向加随机偏移。**轴** = **瞄准局部**（原版把偏移加在玩家角度的基上 ——
        /// <c>cbase.cpp:1059</c> / <c>:1205</c> / <c>:1358</c> 的
        /// <c>vecDir = vecDirShooting + x*vecSpread.x*vecRight + y*vecSpread.y*vecUp</c>，
        /// 其中 <c>vecRight/v_up = gpGlobals->v_right/v_up</c>）：右 = <c>Cross(Vector3.up, dir)</c>、
        /// 上 = <c>Cross(dir, right)</c>。
        ///
        /// <para>**形状** = 每轴取两个均匀分布之和（三角分布；原版 <c>cbase.cpp:1341-1342</c> 的
        /// <c>FireBullets3</c> 玩家分支与 <c>:1051-1052</c> / <c>:1197-1198</c> 的霰弹那两条链都是这样）。
        /// <paramref name="pellets"/> &gt; 1（霰弹）时再对整对做单位圆截断（原版 <c>:1055</c> / <c>:1201</c>；
        /// <c>FireBullets3</c> 玩家分支**没有**这一条）。</para>
        ///
        /// <para><paramref name="spreadDegrees"/> 是**半角（度）**（霰弹由 `M3_CONE_VECTOR` 折 `atan` 得来，
        /// 见 <c>CsWeapons.ApplySpreadValues</c>）⇒ 用 <c>tan</c> 换成横向比例，极值角仍等于该半角。</para>
        /// </summary>
        public static Vector3 ApplySpread(Vector3 dir, float spreadDegrees, int pellets)
        {
            if (spreadDegrees <= 0f) return dir;
            // 散布：**玩法**（命中判定的输入 —— 同一发必须永远同结果）。走全项目唯一的 WeaponSpread 流 ——
            // 不用 UnityEngine.Random：它是全局静态状态，会推位模拟侧序列。
            var rng = CsRng.Stream(CsRngStream.WeaponSpread);
            float x, y;
            do
            {
                x = rng.Range(-0.5f, 0.5f) + rng.Range(-0.5f, 0.5f);
                y = rng.Range(-0.5f, 0.5f) + rng.Range(-0.5f, 0.5f);
            }
            while (pellets > 1 && x * x + y * y > 1f);
            var t = Mathf.Tan(spreadDegrees * Mathf.Deg2Rad);
            var right = Vector3.Cross(Vector3.up, dir);
            if (right.sqrMagnitude < 1e-6f) right = Vector3.right;
            right.Normalize();
            var up = Vector3.Cross(dir, right);
            return (dir + right * (x * t) + up * (y * t)).normalized;
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
            _impactColliders.Clear();

            var pellets = req.Pellets > 0 ? req.Pellets : 1;
            var range = req.Range > 0f ? req.Range : FallbackRange;

            for (var p = 0; p < pellets; p++)
            {
                var dir = ApplySpread(req.Direction, req.Spread, pellets);
                var end = ResolvePellet(req, dir, range);
                _tracerEnds.Add(end);
            }
        }

        /// <summary>
        /// 本武器能否穿墙、最多穿几层墙 —— 唯一的穿墙判据，闸门与层数上限都从这里取。
        ///
        /// <para><b>已确证</b>（<see cref="CsWeaponDef.PenetrationResolved"/>，原版 <c>iPenetration</c>
        /// 逐武器取证）⇒ 用它当层数上限；<c>Penetration == 0</c> 就是**确证不能穿墙**
        /// （原版霰弹枪走 <c>FireBuckshots</c>、该链路没有穿透参数，见
        /// <see cref="CsWeapons.ApplyPenetrationAndFalloffValues"/>）。</para>
        ///
        /// <para><b>没有开火点证据的武器</b>（刀 / 装备）按护甲穿透系数判：
        /// &gt; <see cref="CsCombatTuning.PenetrationArmorThreshold"/> 才可穿、层数上限
        /// <see cref="CsCombatTuning.MaxPenetrationLayers"/> —— ⛔ 不把"无证据"当成"已知为零"。</para>
        /// </summary>
        public static bool CanPenetrate(CsWeaponDef def, out int maxLayers)
        {
            maxLayers = 0;
            if (def == null) return false;

            if (def.PenetrationResolved)
            {
                maxLayers = def.Penetration;
                return def.Penetration > 0;
            }

            if (def.ArmorPenetration > CsCombatTuning.PenetrationArmorThreshold)
            {
                maxLayers = CsCombatTuning.MaxPenetrationLayers;
                return true;
            }
            return false;
        }

        /// <summary>
        /// 单个弹丸的射线解算：取最近的一批命中，按「先角色、后墙」的顺序判定，
        /// 墙是否可穿透与能穿几层由 <see cref="CanPenetrate"/> 决定。
        /// 返回终点（画弹道用）。
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

            var canPenetrate = CanPenetrate(req.Def, out var maxLayers);
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
                    _impactColliders.Add(h.collider);
                }
                walls++;
                end = h.point;
                // 层数上限与可穿与否同源（见 CanPenetrate）：有 iPenetration 用它，未取到回落旧值 2
                if (!canPenetrate || walls > maxLayers) return end;
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
