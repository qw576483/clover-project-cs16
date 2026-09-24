using System.Collections.Generic;
using CloverEngine;
using Cs16.Core;
using Cs16.Module.Match;
using Cs16.Module.Net;
using UnityEngine;

namespace Cs16.Module.View
{
    /// <summary>
    /// **远端角色的一具视图**（本项目新增，差异 #88「能开局」第②段：把快照画成场上的人）。
    ///
    /// <para><b>它画的是什么</b>：<see cref="CsLanClient"/> 从主机收到的一帧
    /// <see cref="CsLanRemoteActor"/>（对端权威的拷贝）—— 一个远端 <c>id</c> 一具视图，
    /// 位置 / 朝向 / 存活**直接来自快照**（不参与本机 <c>CsMatch</c> 的模拟：
    /// 本机没有它们的权威，去 Tick 它们就是"两个模拟抢同一具身体"）。</para>
    ///
    /// <para><b>复用哪条链</b>：模型预制体与 <c>Module/View/ActorView.cs</c> 走**同一套取法与路径** ——
    /// 路径 = <c>CsViewTuning.ArtRoot + {T|CT} + "/" + 皮肤名</c>（皮肤按 id 稳定挑，
    /// 与 <c>ViewModule.PrefabPathFor</c> 同口径），取资源走引擎对象池
    /// <c>Game.Pool.Spawn(key, …)</c>（引擎口径：角色**一律走池**，不裸 <c>Instantiate</c>）；
    /// 实例上挂的仍是工程既有的 <see cref="ActorView"/> ⇒ 身高量测/贴地/骨骼动画/倒地留场
    /// 全部沿用既有实现，本类只补"数据从快照来"这一段。因此：
    /// <list type="bullet">
    /// <item><b>锚点仍传给 <see cref="ActorView.Apply"/>**一个 <c>CsActor</c> 载体**</b> ——
    /// 那是 <c>ActorView</c> 的只读入参（它一个字段都不写），本类拿它当"数据盒子"用，
    /// **不把它注册进 <c>CsMatch</c>**（不 Tick、不在 <c>CsMatch.Actors</c> 里出现）。</item>
    /// <item>速度：快照里**没有**速度字段，本类按相邻两帧的位移差分算出来
    /// （只为了选对"跑 / 站"序列；不是自造数据 —— 位移本身就来自快照）。</item>
    /// </list></para>
    ///
    /// <para><b>本类刻意不做的事（写清楚，免得被当成漏做）</b>：
    /// <list type="number">
    /// <item>远端角色**不参与本机的碰撞 / 命中 / 音频链**：实例化后<b>销毁全部 Collider</b>
    /// （与"掉落武器视图"同一处置：世界物件绝不能进射线）⇒ 打不中它们、它们也不挡人。
    /// 这与 <c>ActorView</c> 里"本地玩家隐藏渲染但保留碰撞体"是**相反**的口径 ——
    /// 因为远端角色在本机**没有权威**，让它们可被打中就会变成"打中一个没人结算的目标"。</item>
    /// <item>不挂头顶名牌：名牌的显隐口径（队友/敌人、距离、视线）挂在本机自己的阵营与视野上，
    /// 属于另一段；这里只到"看得见（位置/朝向/生死）"这一层。</item>
    /// <item>不做预测 / 插值：快照 10 Hz 直接落到位置上。原版客户端有插值（<c>view.cpp</c> 的回放缓冲），
    /// 但那需要"序号 + 时间戳"，而 <c>CS16-LAN-SNAP/1</c> **没有这两个字段** ⇒ 没有出处就不自己编一种。</item>
    /// </list></para>
    ///
    /// <para><b>池化</b>：视图由 <see cref="SyncAll"/> 按 id 复用（一帧内只 <c>Spawn/Despawn</c> 变化的那几具），
    /// 不每帧 <c>Destroy</c> / 新建对象；<see cref="Dispose"/> 把实例**还回对象池**（不是销毁）。</para>
    /// </summary>
    public sealed class CsLanRemoteView : MonoBehaviour
    {
        private const string Tag = "LanRemoteView";

        /// <summary>远端视图父节点名（DontDestroyOnLoad 的常驻根）。</summary>
        public const string RootName = "CsLanRemoteViews";

        /// <summary>节点名前缀（<c>LanRemote_&lt;id&gt;</c>）。</summary>
        public const string NodePrefix = "LanRemote_";

        /// <summary>对象池分组名（便于整组清理，见引擎 <c>IObjectPool.ClearGroup</c>）。</summary>
        public const string PoolGroup = "CsLanRemote";

        // ==================================================================
        //  实例状态
        // ==================================================================
        /// <summary>这具视图当前绑的是哪个远端 id（-1 = 尚未绑定）。</summary>
        private int _remoteId = -1;

        /// <summary>这具视图的阵营（决定模型目录与队色）。</summary>
        private CsTeam _team = CsTeam.Spectator;

        /// <summary>真正画东西的既有视图组件（预制体自带）。</summary>
        private ActorView _actor;

        /// <summary>喂给 <see cref="ActorView.Apply"/> 的**只读数据载体**（不进 <c>CsMatch</c>，见类注释）。</summary>
        private readonly CsActor _shadow = new CsActor();

        private Vector3 _lastSnapshotPos;
        private bool _haveLastSnapshotPos;
        private float _lastApplyAt = -1f;
        private bool _collidersStripped;
        private bool _noActorViewLogged;

        /// <summary>这具视图当前绑的远端 id（-1 = 未绑定；探针 / 日志用）。</summary>
        public int RemoteId { get { return _remoteId; } }

        /// <summary>这具视图的阵营。</summary>
        public CsTeam Team { get { return _team; } }

        /// <summary>底下的既有角色视图（可能为 null —— 预制体没挂 <see cref="ActorView"/> 时）。</summary>
        public ActorView Actor { get { return _actor; } }

        /// <summary>喂给 <see cref="ActorView"/> 的数据载体（探针要读"手上的值"时用它，别改它）。</summary>
        public CsActor Shadow { get { return _shadow; } }

        // ==================================================================
        //  绑定 / 应用 / 回收
        // ==================================================================
        /// <summary>
        /// 绑定一个远端 <c>id</c>（模型/身高校正/动画装配全交给既有 <see cref="ActorView.Bind"/>），
        /// 并把视图**先摆到**快照的位置（不先摆会看到"第一帧从池原点飞过来"）。
        /// </summary>
        public void Bind(CsLanRemoteActor a)
        {
            if (a == null) return;

            _remoteId = a.Id;
            _team = ParseTeam(a.Team);

            _actor = GetComponent<ActorView>();
            if (_actor == null)
            {
                if (!_noActorViewLogged)
                {
                    _noActorViewLogged = true;
                    Game.Logger.Error(Tag,
                        "远端角色预制体上没有 ActorView 组件 ⇒ 这具远端角色只是**一坨不可见的空对象**。" +
                        "请重跑菜单 Clover/CS16/整理视图与音效资源（生成器会写进 ActorView）");
                }
                return;
            }

            FillShadow(a, 0f);
            _actor.Bind(a.Id, _team, false, false);   // isBot=false / isLocal=false：远端角色属于"别人"
            StripColliders();
            _actor.SnapTo(_shadow);

            _lastSnapshotPos = _shadow.Position;
            _haveLastSnapshotPos = true;
            _lastApplyAt = Time.time;

            Game.Logger.Info(Tag,
                "远端角色视图已生成：id=" + a.Id + " name=\"" + a.Name + "\" team=" + a.Team +
                " pos=(" + Fmt(a.X) + "," + Fmt(a.Y) + "," + Fmt(a.Z) + ") yaw=" + Fmt(a.Yaw) +
                " hp=" + a.Hp + " alive=" + a.Alive + " w=\"" + a.Weapon + "\" 模型=" + PrefabPathFor(a.Id, _team));
        }

        /// <summary>把最新一帧快照刷到视图上（位置 / 朝向 / 生死）。</summary>
        public void Apply(CsLanRemoteActor a)
        {
            if (a == null) return;

            // <see cref="Bind"/> 里赋值，而"新建的视图"第一帧走进来时它必然是 null ——
            // 若只在 `a.Id != _remoteId` 时才 Bind，池里刚 Spawn 出来的这具**永远不会 Bind**：
            // `_remoteId` 一直是 -1（视图层按它认"在场"）⇒ 远端角色**一具都画不出来**。
            if (_actor == null || a.Id != _remoteId)
            {
                // 池化复用时也会走到这里（同一具实例换了远端 id）—— 重新绑定，不是每帧新建
                Bind(a);
                return;
            }

            var now = Time.time;
            var dt = _lastApplyAt < 0f ? 0f : now - _lastApplyAt;
            FillShadow(a, dt);
            _actor.Apply(_shadow, false, false, now);   // 名牌不显示（见类注释第 2 条）
            _lastApplyAt = now;
        }

        /// <summary>把实例**还回对象池**（不是销毁）。重复调用幂等。</summary>
        public void Dispose()
        {
            _remoteId = -1;
            _lastApplyAt = -1f;
            _haveLastSnapshotPos = false;

            var go = gameObject;
            if (go == null) return;

            var pool = Game.Pool;
            if (pool != null) pool.Despawn(go);
            else Object.Destroy(go);       // 池不可用（表现域没挂？）⇒ 退化为销毁，⛔ 不留悬挂对象
        }

        /// <summary>快照 → 数据载体的搬运（含"位移差分算速度"，见类注释）。</summary>
        private void FillShadow(CsLanRemoteActor a, float dt)
        {
            var p = new Vector3(a.X, a.Y, a.Z);

            var velocity = Vector3.zero;
            if (dt > 0.0001f && _haveLastSnapshotPos)
            {
                velocity = (p - _lastSnapshotPos) / dt;
                // 一帧位移超过"瞬移判定"阈值 ⇒ 当成出生 / 复活 / 传送：不算速度（否则会误播"跑"）
                if (velocity.sqrMagnitude >
                    CsViewTuning.TeleportSnapDistance * CsViewTuning.TeleportSnapDistance)
                {
                    velocity = Vector3.zero;
                }
            }

            _shadow.Id = a.Id;
            if (_shadow.Name != a.Name) _shadow.Name = a.Name;
            _shadow.Team = _team;
            _shadow.IsBot = false;
            _shadow.Position = p;
            _shadow.Velocity = velocity;
            _shadow.Yaw = a.Yaw;
            _shadow.OnGround = true;
            _shadow.IsCrouching = false;      // 快照里没有蹲姿字段 ⇒ 不假装是蹲（⛔ 不编）
            _shadow.IsWalking = false;
            _shadow.IsAlive = a.Alive;
            _shadow.Health = a.Hp;
            if (_shadow.ActiveWeapon != a.Weapon) _shadow.ActiveWeapon = a.Weapon;

            _lastSnapshotPos = p;
            _haveLastSnapshotPos = true;
        }

        /// <summary>
        /// 销毁这具视图上的**全部 Collider**：远端角色不参与本机碰撞 / 命中链（见类注释）。
        /// <para>为什么不只是 <c>enabled = false</c>：<c>ActorView</c> 复活时会把自己记下的碰撞体重新打开
        /// （<c>ReleaseCorpsePose</c>）⇒ 关掉会被它反悔；销毁是**不可逆**的，语义正好是"这具身体在本机不存在碰撞"。</para>
        /// </summary>
        private void StripColliders()
        {
            if (_collidersStripped) return;
            _collidersStripped = true;

            var cols = GetComponentsInChildren<Collider>(true);
            for (var i = 0; i < cols.Length; i++)
            {
                if (cols[i] != null) Object.Destroy(cols[i]);
            }
            StrippedCollidersTotal += cols.Length;
            if (cols.Length > 0)
            {
                Game.Logger.Info(Tag,
                    "远端角色视图「" + name + "」已销毁 " + cols.Length +
                    " 个 Collider（远端角色不参与本机碰撞/命中链，⛔ 打不中它们、它们也不挡人）");
            }
        }

        // ==================================================================
        //  集合同步（由 MatchModule.Update 每帧调一次）
        // ==================================================================
        private static readonly Dictionary<int, CsLanRemoteView> Views = new Dictionary<int, CsLanRemoteView>(32);
        private static readonly List<CsLanRemoteActor> Scratch = new List<CsLanRemoteActor>(32);
        private static readonly List<int> Stale = new List<int>(8);
        private static Transform _root;
        private static int _createFailures;
        private static bool _poolMissingLogged;

        /// <summary>当前场上的远端视图数（探针 / 判据用）。</summary>
        public static int ViewCount { get { return Views.Count; } }

        /// <summary>累计新建过的远端视图数（池化复用 ⇒ 它会小于"快照里出现过的 id 数 × 帧数"）。</summary>
        public static int CreatedTotal { get; private set; }

        /// <summary>累计回收过的远端视图数。</summary>
        public static int RecycledTotal { get; private set; }

        /// <summary>累计销毁的 Collider 数（"不参与命中"的量化口径）。</summary>
        public static int StrippedCollidersTotal { get; private set; }

        /// <summary>累计"预制体缺失/池不可用"导致的建视图失败次数（失败必须留痕，不静默）。</summary>
        public static int CreateFailures { get { return _createFailures; } }

        /// <summary>一行状态（日志 / 探针 / 报告共用）。</summary>
        public static string Describe()
        {
            return "远端视图 在场=" + Views.Count + " 累计新建=" + CreatedTotal +
                   " 累计回收=" + RecycledTotal + " 销毁Collider=" + StrippedCollidersTotal +
                   " 建失败=" + _createFailures;
        }

        /// <summary>
        /// 让场上的远端视图与**最新一帧快照**对齐：新的建、有的刷、快照里没有的回收。
        ///
        /// <para>调用口径：主线程每帧一次（<c>MatchModule.Update</c>）。客户端没在跑时是
        /// "回收一次 + 廉价空转"（不会每帧扫字典）。</para>
        /// </summary>
        /// <returns>当前在场的远端视图数</returns>
        public static int SyncAll()
        {
            if (!CsLanClient.IsRunning)
            {
                if (Views.Count > 0)
                {
                    Game.Logger.Info(Tag, "局域网客户端已停 ⇒ 回收全部远端角色视图（" + Views.Count + " 具）");
                    RecycleAll();
                }
                return 0;
            }

            Scratch.Clear();
            if (!CsLanClient.TryGetActors(Scratch))
            {
                return Views.Count;      // 还没收到任何一帧快照：保持现状（⛔ 不把上一帧的人凭空抹掉）
            }

            // ---- 新建 / 刷新 ----
            for (var i = 0; i < Scratch.Count; i++)
            {
                var a = Scratch[i];
                if (a == null) continue;

                CsLanRemoteView view;
                if (!Views.TryGetValue(a.Id, out view) || view == null)
                {
                    view = Create(a);
                    if (view == null) continue;      // 已在 Create 里留痕
                    Views[a.Id] = view;
                }
                view.Apply(a);
            }

            // ---- 回收：这一帧快照里已经没有的 id（对端退出 / 换局）----
            if (Views.Count > 0)
            {
                Stale.Clear();
                foreach (var kv in Views)
                {
                    if (!ContainsId(Scratch, kv.Key)) Stale.Add(kv.Key);
                }
                for (var i = 0; i < Stale.Count; i++)
                {
                    var id = Stale[i];
                    var view = Views[id];
                    Views.Remove(id);
                    if (view != null) view.Dispose();
                    RecycledTotal++;
                    Game.Logger.Info(Tag, "远端角色 id=" + id + " 已不在最新快照里 ⇒ 视图已回收（" + Describe() + "）");
                }
            }

            return Views.Count;
        }

        /// <summary>回收全部远端视图（模块停 / 退出这局时调；幂等）。</summary>
        public static void RecycleAll()
        {
            if (Views.Count == 0) return;

            foreach (var kv in Views)
            {
                if (kv.Value != null) kv.Value.Dispose();
            }
            RecycledTotal += Views.Count;
            Views.Clear();
            Game.Logger.Info(Tag, "远端角色视图已全部回收（" + Describe() + "）");
        }

        /// <summary>取（必要时创建）远端视图的常驻父节点。</summary>
        private static Transform Root()
        {
            if (_root != null) return _root;
            var go = new GameObject(RootName);
            Object.DontDestroyOnLoad(go);
            _root = go.transform;
            return _root;
        }

        /// <summary>按远端 id 建一具视图（走引擎对象池；不裸 <c>Instantiate</c>）。</summary>
        private static CsLanRemoteView Create(CsLanRemoteActor a)
        {
            var pool = Game.Pool;
            if (pool == null)
            {
                _createFailures++;
                if (!_poolMissingLogged)
                {
                    _poolMissingLogged = true;
                    Game.Logger.Error(Tag,
                        "Game.Pool 为 null（表现域未挂 / CloverPresentation 未 Init）⇒ 远端角色无法生成。" +
                        "工程口径：角色视图一律走对象池，⛔ 不裸 Instantiate");
                }
                return null;
            }

            var path = PrefabPathFor(a.Id, ParseTeam(a.Team));
            var go = pool.Spawn(path, Root(), PoolGroup);
            if (go == null)
            {
                // 非预期分支：预制体缺失（池内部已记 Error）⇒ 这里累计并留痕（不静默）
                _createFailures++;
                Game.Logger.Warn(Tag,
                    "远端角色 id=" + a.Id + " 的预制体 " + path + " 取不到（池返回 null）⇒ 这具看不见。" +
                    "请重跑菜单 Clover/CS16/整理视图与音效资源；累计失败 " + _createFailures + " 次");
                return null;
            }

            go.name = NodePrefix + a.Id;
            var view = go.GetComponent<CsLanRemoteView>();
            if (view == null) view = go.AddComponent<CsLanRemoteView>();
            CreatedTotal++;
            return view;
        }

        /// <summary>
        /// 阵营 + id → 角色模型预制体路径（<c>Art/{T|CT}/{皮肤}</c>）。
        /// 与 <c>ViewModule.PrefabPathFor</c> **同一套口径**：皮肤按 id 稳定挑（同一局内不变），
        /// 常量取自 <c>CsViewTuning</c>（本文件不出现任何路径字面量）。
        /// </summary>
        public static string PrefabPathFor(int id, CsTeam team)
        {
            var folder = team == CsTeam.CT ? CsViewTuning.TeamFolderCT : CsViewTuning.TeamFolderT;
            var skins = team == CsTeam.CT ? CsViewTuning.SkinsCT : CsViewTuning.SkinsT;
            if (skins == null || skins.Length == 0)
            {
                return CsViewTuning.ArtRoot + folder + "/" + CsViewTuning.PlayerModelName;
            }

            var h = unchecked((ulong)(long)id * 2654435761UL + 12345UL);
            var idx = (int)(h % (ulong)skins.Length);
            return CsViewTuning.ArtRoot + folder + "/" + skins[idx];
        }

        /// <summary>快照里的阵营串 → 枚举（认不出来 = 非预期分支，按观战处理并留痕一次）。</summary>
        private static CsTeam ParseTeam(string team)
        {
            if (team == "T") return CsTeam.T;
            if (team == "CT") return CsTeam.CT;
            if (team == "Spectator") return CsTeam.Spectator;

            if (!_badTeamLogged)
            {
                _badTeamLogged = true;
                Game.Logger.Warn(Tag,
                    "快照里的阵营串认不出来：\"" + (team ?? "<null>") + "\"（期望 T / CT / Spectator）⇒ 按观战处理");
            }
            return CsTeam.Spectator;
        }

        private static bool _badTeamLogged;

        private static bool ContainsId(List<CsLanRemoteActor> list, int id)
        {
            for (var i = 0; i < list.Count; i++)
            {
                if (list[i] != null && list[i].Id == id) return true;
            }
            return false;
        }

        private static string Fmt(float v)
        {
            return v.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
