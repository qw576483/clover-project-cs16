using CloverEngine;
using Cs16.Core;
using UnityEngine;

namespace Cs16.Module.Combat
{
    /// <summary>
    /// 原版 <c>func_breakable</c> 箱体的**运行时可破坏**行为（只用于"会被伤害打碎"的箱体）。
    ///
    /// <para><b>原版语义</b>：<c>CBreakable::TakeDamage</c> 扣 <c>pev->health</c>，≤ 0 就走
    /// <c>CBreakable::Die()</c> —— 关掉刷体（<c>UTIL_Remove</c>）+ 发 <c>TE_BREAKMODEL</c> 碎块。</para>
    ///
    /// <para><b>de_dust2 的 10 个箱子不走这条路径</b>：它们的 <c>spawnflags</c> 全是 1
    /// （= <c>SF_BREAK_TRIGGER_ONLY</c>，HLSDK <c>dlls/util.h:441</c>）⇒ <c>CBreakable::Spawn</c>
    /// 把 <c>pev->takedamage</c> 置为 <c>DAMAGE_NO</c> 并 <c>SetTouch(NULL)</c>
    /// （<c>dlls/func_break.cpp:148-151</c> / <c>:168-169</c>）：子弹 / 手雷 / 撞击都不会让它碎，
    /// 只有 trigger 触发才会碎。⇒ 地图生成器不生成它们（<c>Dust2Breakables</c> 的
    /// <c>BreakableByDamage</c> 判否），箱面留在合并静态网格里、保留原版贴图与 MeshCollider。
    /// <c>health 1</c> 只在 <c>takedamage = DAMAGE_YES</c> 时才有意义，不能单凭它推出"一发即碎"。</para>
    ///
    /// <para><b>碎块用什么</b>：原版 <c>gibmodel</c> 是 <c>models/mil_crategibs.mdl</c>
    /// （载体在 <c>原版资源/cs16src/valve/models/valve__models__mil_crategibs.mdl</c>：一台军用木箱里的
    /// 装备堆，12 张内嵌 miptex）。生成器用 Views 转换链把它变成
    /// <c>Assets/Editor/Views/ModelData/gibs_mil_crate.cs16mdl</c>（金源 1 unit = 0.0254 m），
    /// 碎块 = 该模型**每张贴图下的全部三角面**各一片（12 片）——顶点 / UV / 法线逐顶点照搬，
    /// 各片按重心扣在箱体中心、片间相对位置 = 原版模型里的相对位置 ⇒ 几何与贴图都是原版的。
    /// 片数由模型的贴图数决定，⛔ 不写死数字。差异登记见 `策划/差异登记.tsv`。</para>
    ///
    /// <para><b>碎块运动</b>：出处 = 原版 <c>CBreakable::Die()</c>（Half-Life SDK 游戏逻辑，Valve 版权）：
    /// <c>vecVelocity = (m_Explosion == expDirected) ? g_vecAttackDir * 200.0f : 0</c>；
    /// de_dust2 的箱体实体**没有 <c>explosion</c> 键**，即原版出厂速度是 0、散开由客户端按
    /// <c>randomization = 10</c> 决定（那段在引擎 <c>hw.dll</c> 里，本工程未复刻）。
    /// 这里改用**同一条逐字公式** <c>g_vecAttackDir * 200</c>，方向取真实弹道方向（本项目新增口径，已登记）。</para>
    /// </summary>
    public sealed class CsBreakable : MonoBehaviour
    {
        private const string LogTag = "breakable";

        // ---- 生成器（Dust2Builder）写进来；运行时只读 ----
        [SerializeField] private int _brushModel;
        [SerializeField] private float _health = 1f;
        [SerializeField] private string _gibModel = string.Empty;
        [SerializeField] private Vector3 _size;

        // ---- 碎块：顶点已相对**各自碎片中心**；每个碎片占连续的 3×三角面数个顶点 ----
        [SerializeField] private Vector3[] _fragVertices = null;
        [SerializeField] private Vector2[] _fragUvs = null;
        [SerializeField] private Vector3[] _fragNormals = null;
        [SerializeField] private int[] _fragStart = null;            // 每个碎片的起始三角面下标
        [SerializeField] private int[] _fragTriangleCount = null;    // 每个碎片的三角面数
        [SerializeField] private Vector3[] _fragCenter = null;       // 每个碎片中心（箱体局部坐标）
        [SerializeField] private Material[] _fragMaterials = null;   // 逐片材质（原版 gib 模型的贴图）

        private static int _brokenTotal;

        /// <summary>本箱已经碎了吗（碎过的箱子不再受击、不再出碎块）。</summary>
        public bool Broken { get; private set; }

        /// <summary>已碎箱体数（自检 / 日志用）。</summary>
        public static int BrokenTotal => _brokenTotal;

        /// <summary>原版刷模型号（日志与自检用它对上 breakables.tsv）。</summary>
        public int BrushModel => _brushModel;

        /// <summary>当前血量（原版 <c>pev->health</c>，de_dust2 全 1）。</summary>
        public float Health => _health;

        /// <summary>碎块片数（生成器按原版 gib 模型的贴图子网格拆出来的片数）。</summary>
        public int FragmentCount => _fragStart != null ? _fragStart.Length : 0;

        /// <summary>箱体尺寸（米）。</summary>
        public Vector3 Size => _size;

        /// <summary>生成器用：把原版键值与碎块几何写进组件。</summary>
        public void Configure(int brushModel, float health, string gibModel, Vector3 size,
                              Vector3[] fragVertices, Vector2[] fragUvs, Vector3[] fragNormals,
                              int[] fragStart, int[] fragTriangleCount,
                              Vector3[] fragCenter, Material[] fragMaterials)
        {
            _brushModel = brushModel;
            _health = health;
            _gibModel = gibModel;
            _size = size;
            _fragVertices = fragVertices;
            _fragUvs = fragUvs;
            _fragNormals = fragNormals;
            _fragStart = fragStart;
            _fragTriangleCount = fragTriangleCount;
            _fragCenter = fragCenter;
            _fragMaterials = fragMaterials;
        }

        /// <summary>取命中物所属的箱体（不是箱体 ⇒ null）。</summary>
        public static CsBreakable Find(Collider collider)
            => collider != null ? collider.GetComponentInParent<CsBreakable>() : null;

        /// <summary>
        /// 一发子弹打在箱体上：按原版扣血（<c>pev->health -= flDamage</c>），≤ 0 即碎。
        /// <paramref name="attackDir"/> = 这一发的弹道方向（原版 <c>g_vecAttackDir</c>），碎块沿它飞。
        /// </summary>
        /// <returns>这一发是否把箱子打碎了。</returns>
        public bool ApplyBulletDamage(float damage, Vector3 attackDir, Vector3 point)
        {
            if (Broken) return false;

            _health -= damage;
            if (_health > 0f)
            {
                Game.Logger.Info(LogTag + ".hit",
                    $"木箱 brush{_brushModel} 中弹：伤害 {damage:F0}，剩余血量 {_health:F0}");
                return false;
            }

            Break(attackDir, point);
            return true;
        }

        /// <summary>碎：关碰撞体 + 关渲染 + 出碎块（原版 <c>CBreakable::Die</c>）。</summary>
        public void Break(Vector3 attackDir, Vector3 point)
        {
            if (Broken) return;
            Broken = true;
            _brokenTotal++;

            var colliders = GetComponentsInChildren<Collider>(true);
            var disabled = 0;
            for (var i = 0; i < colliders.Length; i++)
            {
                if (colliders[i] == null || !colliders[i].enabled) continue;
                colliders[i].enabled = false;
                disabled++;
            }

            foreach (var r in GetComponentsInChildren<Renderer>(true))
                if (r != null) r.enabled = false;

            var spawnDir = attackDir.sqrMagnitude > 1e-6f ? attackDir.normalized : Vector3.up;
            var shards = SpawnShards(spawnDir);

            Game.Logger.Info(LogTag + ".broken",
                $"木箱 brush{_brushModel} 碎裂：关碰撞体 {disabled} 个｜碎块 {shards} 片" +
                $"（初速 {CsCombatTuning.BreakGibSpeed:F2} m/s 沿弹道方向，寿命 {CsCombatTuning.BreakGibLife:F1}s）" +
                $"｜碎块 = 原版 gibmodel「{_gibModel}」的贴图子网格（几何 / UV / 贴图逐顶点照搬，" +
                "差异登记见 策划/差异登记.tsv）");
        }

        private int SpawnShards(Vector3 spawnDir)
        {
            if (_fragStart == null || _fragVertices == null || _fragCenter == null) return 0;

            var made = 0;
            for (var f = 0; f < _fragStart.Length; f++)
            {
                var triCount = _fragTriangleCount[f];
                if (triCount <= 0) continue;

                var vCount = triCount * 3;
                var vBase = _fragStart[f] * 3;
                if (vBase + vCount > _fragVertices.Length)
                {
                    Game.Logger.Warn(LogTag + ".shard.range",
                        $"碎块 {f} 的顶点区间 [{vBase},{vBase + vCount}) 越界（共 {_fragVertices.Length} 顶点）⇒ 跳过该片");
                    continue;
                }

                var mesh = new Mesh { name = $"CrateShard_{_brushModel}_{f}" };
                var vs = new Vector3[vCount];
                var uvs = new Vector2[vCount];
                var ns = new Vector3[vCount];
                var idx = new int[vCount];
                System.Array.Copy(_fragVertices, vBase, vs, 0, vCount);
                if (_fragUvs != null && _fragUvs.Length >= vBase + vCount)
                    System.Array.Copy(_fragUvs, vBase, uvs, 0, vCount);
                if (_fragNormals != null && _fragNormals.Length >= vBase + vCount)
                    System.Array.Copy(_fragNormals, vBase, ns, 0, vCount);
                for (var i = 0; i < vCount; i++) idx[i] = i;
                mesh.vertices = vs;
                mesh.uv = uvs;
                mesh.normals = ns;
                mesh.SetTriangles(idx, 0, false);
                mesh.RecalculateBounds();

                var go = new GameObject($"CrateShard_{_brushModel}_{f}");
                go.transform.SetPositionAndRotation(transform.TransformPoint(_fragCenter[f]), transform.rotation);
                go.layer = gameObject.layer;
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = go.AddComponent<MeshRenderer>();
                var mats = _fragMaterials;
                mr.sharedMaterial = mats != null && f < mats.Length ? mats[f] : null;
                if (mr.sharedMaterial == null)
                {
                    Game.Logger.Warn(LogTag + ".shard.nomaterial",
                        $"碎块 {f} 取不到材质（片数 {_fragStart.Length} / 材质数 {(mats != null ? mats.Length : 0)}）" +
                        "⇒ 该片不可见，碎块材质没写进组件");
                }

                go.AddComponent<CsBreakDebris>()
                  .Launch(spawnDir * CsCombatTuning.BreakGibSpeed, CsCombatTuning.BreakGibLife, mesh);
                made++;
            }
            return made;
        }
    }
}
