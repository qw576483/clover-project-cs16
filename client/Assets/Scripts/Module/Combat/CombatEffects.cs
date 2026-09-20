using System.Collections.Generic;
using Cs16.Module.Player;
using UnityEngine;

namespace Cs16.Module.Combat
{
    /// <summary>
    /// 枪口火焰 / 弹道 / 爆炸的**纯程序化**表现（不依赖预制体、贴图、也不做 <c>Shader.Find</c>）。
    ///
    /// <para><b>为什么不用引擎对象池（<c>Game.Pool</c>）</b>：引擎的对象池是"按预制体 key 实例化"
    /// （内部走 <c>Resources.Load&lt;GameObject&gt;(key)</c>），而本模块的产出路径里没有、也不该有预制体
    /// （<c>Assets/Resources/**</c> 不归 agent-04 写）。key 缺失时引擎会**每次调用打一条 Error**，
    /// 高频路径上等于刷屏；所以这里自带一个极小特效池（<c>SetActive</c> 复用），
    /// 语义与对象池一致（取 / 还 / 上限），但没有资源依赖。</para>
    ///
    /// <para><b>几何来源</b>：一律 <c>GameObject.CreatePrimitive</c>（Unity 内置网格 + 内置默认材质）。
    /// 刻意避开 <c>Shader.Find("Unlit/...")</c> 这类运行时查找 —— 打包后内置 shader 可能没被包含，
    /// 结果就是"代码没错、画面什么都没有"的静默失败。自建物件上的 <c>Collider</c> 立刻 <c>DestroyImmediate</c>
    /// 掉：否则特效自己会被射线打中（子弹会打在"火焰"上）。</para>
    /// </summary>
    internal sealed class CombatEffects
    {
        private const string RootName = "CsCombatFX";

        /// <summary>池中同类特效的软上限（超出即复用最久的一个，不会无限增长）。</summary>
        private const int SoftPoolLimit = 96;

        private enum Shape
        {
            Sphere = 0,
            Cube = 1,
        }

        private sealed class EffectItem
        {
            public GameObject Go;
            public Transform Tr;
            public MeshRenderer Renderer;
            public Light Light;
            public Shape Shape;
            public Color Color;
            public float Life;
            public float MaxLife;
            public float StartScale;
            public float EndScale;
            public bool Persistent;
        }

        private readonly CsModuleLog _log = new CsModuleLog("Combat");
        private readonly List<EffectItem> _items = new List<EffectItem>(64);
        private Transform _root;

        // ---- 颜色（"够显眼"优先，不做美术风格化，agent-07 有资源后可替换）----
        private static readonly Color FlashColor = new Color(1f, 0.93f, 0.62f, 1f);
        private static readonly Color TracerColor = new Color(1f, 0.97f, 0.78f, 1f);
        private static readonly Color BlastColor = new Color(1f, 0.58f, 0.20f, 1f);
        private static readonly Color NadeColor = new Color(0.30f, 0.34f, 0.26f, 1f);
        private static readonly Color SmokeColor = new Color(0.78f, 0.78f, 0.78f, 1f);

        /// <summary>创建常驻特效根节点（只做一次）。</summary>
        public void Init()
        {
            if (_root != null) return;
            var go = new GameObject(RootName);
            Object.DontDestroyOnLoad(go);
            _root = go.transform;
        }

        /// <summary>销毁全部特效物件（比赛结束 / 模块销毁时）。</summary>
        public void Dispose()
        {
            for (var i = 0; i < _items.Count; i++)
            {
                if (_items[i].Go != null) Object.Destroy(_items[i].Go);
            }
            _items.Clear();

            if (_root != null)
            {
                Object.Destroy(_root.gameObject);
                _root = null;
            }
        }

        /// <summary>
        /// 枪口火焰：眼睛前方一点的一颗亮点 + 一盏点光。
        /// 点光才是第一人称里最有效的"开枪了"读感（墙与敌人会被照亮）。
        /// </summary>
        public void MuzzleFlash(Vector3 eyePosition, Vector3 direction)
        {
            var dir = direction.sqrMagnitude > 0.0001f ? direction.normalized : Vector3.forward;
            var right = Vector3.Cross(Vector3.up, dir).normalized;
            var pos = eyePosition + dir * 0.36f + right * 0.14f + Vector3.down * 0.10f;

            var item = Acquire(Shape.Sphere, FlashColor, CsCombatTuning.MuzzleLightDuration);
            if (item == null) return;

            item.Tr.position = pos;
            SetScale(item, CsCombatTuning.MuzzleFlashScale);

            if (item.Light != null)
            {
                item.Light.color = FlashColor;
                item.Light.range = 8f;
                item.Light.intensity = 4f;
            }
        }

        /// <summary>弹道（一条细长的亮条，从枪口到终点）。</summary>
        public void Tracer(Vector3 from, Vector3 to)
        {
            var delta = to - from;
            var length = delta.magnitude;
            if (length < 0.05f) return;

            var item = Acquire(Shape.Cube, TracerColor, CsCombatTuning.TracerDuration);
            if (item == null) return;

            item.Tr.position = from + delta * 0.5f;
            item.Tr.rotation = Quaternion.LookRotation(delta / length);
            item.Tr.localScale = new Vector3(CsCombatTuning.TracerThickness, CsCombatTuning.TracerThickness, length);
        }

        /// <summary>爆炸（手雷 / C4）：一团快速膨胀的橙色亮球 + 点光。</summary>
        public void Explosion(Vector3 position)
        {
            var item = Acquire(Shape.Sphere, BlastColor, CsCombatTuning.ExplosionVisualDuration);
            if (item == null) return;

            item.Tr.position = position;
            item.StartScale = 0.35f;
            item.EndScale = CsCombatTuning.ExplosionVisualMaxRadius;
            SetScale(item, item.StartScale);

            if (item.Light != null)
            {
                item.Light.color = BlastColor;
                item.Light.range = 16f;
                item.Light.intensity = 6f;
            }
        }

        /// <summary>借出一个**不参与生命周期**的物件（手雷飞行体这类由调用方自己管的视觉）。</summary>
        public GameObject RentSphere(Color color, float scale)
        {
            var item = Acquire(Shape.Sphere, color, 0f, persistent: true);
            if (item == null) return null;
            SetScale(item, scale);
            return item.Go;
        }

        /// <summary>归还 <see cref="RentSphere"/> 借出的物件。</summary>
        public void Return(GameObject go)
        {
            if (go == null) return;
            for (var i = 0; i < _items.Count; i++)
            {
                var item = _items[i];
                if (item.Go != go) continue;
                item.Persistent = false;
                item.Life = 0f;
                item.Go.SetActive(false);
                return;
            }
            Object.Destroy(go);
        }

        /// <summary>手雷体 / 烟雾球这类"常驻一段时间"的视觉：颜色与尺寸自定义。</summary>
        public GameObject RentSphereGrenade(float scale) => RentSphere(NadeColor, scale);

        /// <summary>烟雾体（半透做不了就先用淡灰实心球，够读）——由 GrenadeThrower 借用。</summary>
        public GameObject RentSmoke(float scale) => RentSphere(SmokeColor, scale);

        /// <summary>每帧推进生命周期。</summary>
        public void Tick(float dt)
        {
            if (dt < 0f) dt = 0f;

            for (var i = 0; i < _items.Count; i++)
            {
                var item = _items[i];
                if (item.Persistent || item.Go == null || !item.Go.activeSelf) continue;

                item.Life -= dt;
                if (item.Life <= 0f)
                {
                    item.Go.SetActive(false);
                    continue;
                }

                if (item.EndScale > item.StartScale)
                {
                    var t = 1f - Mathf.Clamp01(item.Life / item.MaxLife);
                    SetScale(item, Mathf.Lerp(item.StartScale, item.EndScale, t));
                }
            }
        }

        // ==================================================================
        //  池
        // ==================================================================
        private EffectItem Acquire(Shape shape, Color color, float life, bool persistent = false)
        {
            Init();

            EffectItem reusable = null;
            for (var i = 0; i < _items.Count; i++)
            {
                var item = _items[i];
                if (item.Persistent || item.Go == null || item.Go.activeSelf) continue;
                if (item.Shape != shape) continue;
                if (item.Color != color) continue;
                reusable = item;
                break;
            }

            var target = reusable ?? Create(shape, color);
            if (target == null) return null;

            target.Persistent = persistent;
            target.Life = life;
            target.MaxLife = life > 0f ? life : 1f;
            target.StartScale = target.EndScale = 0f;
            if (target.Light != null) target.Light.enabled = true;
            target.Go.SetActive(true);
            return target;
        }

        private EffectItem Create(Shape shape, Color color)
        {
            if (_items.Count >= SoftPoolLimit)
            {
                _log.Warn("fx.pool.full",
                    $"特效池已达上限 {SoftPoolLimit}，本次特效被丢弃（考虑降低开火频率或提高上限）");
                return null;
            }

            var primitive = shape == Shape.Sphere ? PrimitiveType.Sphere : PrimitiveType.Cube;
            var go = GameObject.CreatePrimitive(primitive);
            if (go == null)
            {
                _log.Error("fx.create.fail", $"CreatePrimitive({primitive}) 失败，特效不可用");
                return null;
            }

            go.name = shape == Shape.Sphere ? "FX_Sphere" : "FX_Cube";
            go.transform.SetParent(_root, false);
            go.SetActive(false);

            // 自建特效不能带碰撞体：否则弹道会打在"火焰/弹道"上（射线命中非角色碰撞体 = 一层墙）。
            var collider = go.GetComponent<Collider>();
            if (collider != null) Object.DestroyImmediate(collider);

            var renderer = go.GetComponent<MeshRenderer>();
            if (renderer != null)
            {
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                // 取一次 .material 会给本实例克隆一份材质（Unity 语义），随后改颜色不会影响别的物件。
                var mat = renderer.material;
                if (mat != null) mat.color = color;
            }

            var light = go.AddComponent<Light>();
            light.type = LightType.Point;
            light.shadows = LightShadows.None;
            light.color = color;
            light.enabled = false;

            var item = new EffectItem
            {
                Go = go,
                Tr = go.transform,
                Renderer = renderer,
                Light = light,
                Shape = shape,
                Color = color,
            };
            _items.Add(item);
            return item;
        }

        private static void SetScale(EffectItem item, float scale)
        {
            if (item?.Tr == null) return;
            item.Tr.localScale = Vector3.one * Mathf.Max(0.001f, scale);
        }
    }
}
