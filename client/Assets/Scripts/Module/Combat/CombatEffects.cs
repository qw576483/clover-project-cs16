using System.Collections.Generic;
using CloverEngine;
using Cs16.Core;
using Cs16.Module.Player;
using UnityEngine;

namespace Cs16.Module.Combat
{
    /// <summary>
    /// 枪口火焰 / 弹道 / 弹痕 / 爆炸的**程序化**表现（不依赖预制体；贴图走 <c>Resources/UI/Art/fx_*</c>）。
    ///
    /// <para><b>2026-09-20 修正（用户报"开枪有黄色球 / 墙上没有弹痕"）</b>：
    /// 上一版枪口火焰是 <c>CreatePrimitive(Sphere)</c> + 黄色 <see cref="Color"/> ⇒ 画面上就是**一颗黄球**
    /// （原版是一张 <c>sprites/muzzleflash*.spr</c> 星形亮斑）；而且**完全没有弹痕**（原版打在墙上会留
    /// <c>decals.wad</c> 的 <c>{shot*</c> 弹痕）。现在：</para>
    /// <list type="bullet">
    /// <item>枪口火焰 = <see cref="SpriteRenderer"/> 星形亮斑（面向相机）+ 一盏点光；</item>
    /// <item>命中墙 = **弹痕贴片**（按命中法线贴面）+ 一记小火星；</item>
    /// <item>贴图是**本项目程序化生成**的（原版 <c>.spr</c> / <c>decals.wad</c> 载体不在本仓库、
    /// archive.org 又连不上 ⇒ 按载体降级链退一格，登记在 <c>client/资源欠缺清单.md</c>，
    /// 拿到原版素材后**只换文件**，本类一行都不用改）。</item>
    /// </list>
    ///
    /// <para><b>为什么不用引擎对象池（<c>Game.Pool</c>）</b>：引擎的对象池是"按预制体 key 实例化"
    /// （内部走 <c>Resources.Load&lt;GameObject&gt;(key)</c>），而本模块的产出路径里没有、也不该有预制体
    /// （<c>Resources/UI/{类名}.prefab</c> 归 UI 生成器写）。key 缺失时引擎会**每次调用打一条 Error**，
    /// 高频路径上等于刷屏；所以这里自带一个极小特效池（<c>SetActive</c> 复用），语义与对象池一致。</para>
    ///
    /// <para><b>几何来源</b>：球/方块走 <c>GameObject.CreatePrimitive</c>（Unity 内置网格），
    /// 贴片走 <c>SpriteRenderer</c>（着色器是 Unity 内置的 <c>Sprites/Default</c>，**一定**被打进包，
    /// 不像 <c>Shader.Find</c> 那样有"打包后找不到、画面全空"的静默风险）。
    /// 自建物件上的 <c>Collider</c> 立刻 <c>DestroyImmediate</c> 掉：否则特效自己会被射线打中。</para>
    /// </summary>
    internal sealed class CombatEffects
    {
        private const string RootName = "CsCombatFX";

        /// <summary>池中同类特效的软上限（超出即复用最久的一个，不会无限增长）。</summary>
        private const int SoftPoolLimit = 256;

        private enum Shape
        {
            Sphere = 0,
            Cube = 1,
            Sprite = 2,
        }

        private sealed class EffectItem
        {
            public GameObject Go;
            public Transform Tr;
            public MeshRenderer Renderer;
            public SpriteRenderer Sprite;
            public Light Light;
            public Shape Shape;
            public Color Color;
            public float Life;
            public float MaxLife;
            public float StartScale;
            public float EndScale;
            public bool Persistent;
            /// <summary>每帧朝相机（枪口火焰 / 火星用；弹痕**不**朝相机，它贴在墙上）。</summary>
            public bool Billboard;
            /// <summary>是不是"弹痕"（弹痕有独立上限，满了复用最旧的一条）。</summary>
            public bool Decal;
        }

        private readonly CsModuleLog _log = new CsModuleLog("Combat");
        private readonly List<EffectItem> _items = new List<EffectItem>(128);
        private Transform _root;

        // ---- 贴图（异步加载；缺资源时只 Warn 一次并退化成"点光 + 无贴片"，⛔ 绝不退回黄色球）----
        private Sprite _sprFlash;
        private Sprite _sprHole;
        private Sprite _sprSpark;
        private bool _spritesRequested;
        private bool _spriteWarned;

        // ---- 颜色 ----
        /// <summary>枪口点光色（原版 spr 是暖白偏黄，照亮近处墙面）。</summary>
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

            RequestSprites();
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

        /// <summary>三张特效精灵异步取一次（缺资源时只 Warn 一次，绝不每帧刷屏）。</summary>
        private void RequestSprites()
        {
            if (_spritesRequested) return;
            _spritesRequested = true;

            var res = Game.Res;
            if (res == null)
            {
                WarnSpriteOnce("Game.Res 为 null（CloverRes.Init 未执行？），枪口火焰 / 弹痕贴图不可用");
                return;
            }

            res.LoadAsset<Sprite>(ResPaths.FxMuzzleFlash, s => _sprFlash = s);
            res.LoadAsset<Sprite>(ResPaths.FxBulletHole, s => _sprHole = s);
            res.LoadAsset<Sprite>(ResPaths.FxSpark, s => _sprSpark = s);
        }

        private void WarnSpriteOnce(string message)
        {
            if (_spriteWarned) return;
            _spriteWarned = true;
            _log.Warn("fx.sprite", message);
        }

        /// <summary>
        /// 枪口火焰：**一张星形亮斑贴片**（面向相机）+ 一盏点光。
        /// 点光是第一人称里最有效的"开枪了"读感（墙与敌人会被照亮）。
        /// ⛔ 不许退回"一颗球"（用户报的就是它）。
        /// </summary>
        public void MuzzleFlash(Vector3 eyePosition, Vector3 direction)
        {
            var dir = direction.sqrMagnitude > 0.0001f ? direction.normalized : Vector3.forward;
            var right = Vector3.Cross(Vector3.up, dir).normalized;
            // 落点 = 枪口稍前方（原版 viewmodel 的枪口就在这个位置附近：右下偏内）。
            var pos = eyePosition + dir * 0.34f + right * 0.13f + Vector3.down * 0.09f;

            var item = AcquireSprite(Shape.Sprite, Color.white, CsCombatTuning.MuzzleFlashDuration);
            if (item == null) return;

            item.Tr.position = pos;
            item.Sprite.sprite = _sprFlash;
            item.Sprite.enabled = _sprFlash != null;
            item.Billboard = true;
            item.Tr.localScale = Vector3.one * CsCombatTuning.MuzzleFlashSize;
            if (_sprFlash == null) WarnSpriteOnce($"枪口火焰贴图缺失：Resources/{ResPaths.FxMuzzleFlash}.png（只保留点光）");

            if (item.Light != null)
            {
                // 点光**要**开：原版枪口火焰同时照亮近处墙面，是第一人称里最主要的"开枪了"读感。
                item.Light.enabled = true;
                item.Light.color = FlashColor;
                item.Light.range = 8f;
                item.Light.intensity = 4f;
            }
        }

        /// <summary>
        /// 命中墙：**弹痕**（按命中面法线贴上去的贴片）+ 一记小火星。
        /// <paramref name="normal"/> 必须是世界法线（<c>RaycastHit.normal</c>）。
        /// </summary>
        public void BulletImpact(Vector3 point, Vector3 normal)
        {
            if (normal.sqrMagnitude < 0.0001f) normal = Vector3.up;
            normal.Normalize();

            // ---- 弹痕：贴面 + 沿法线抬起 1cm（防 z-fighting），尺寸按原版 decal 的观感（~7cm）----
            var decal = AcquireDecal();
            if (decal != null)
            {
                decal.Tr.position = point + normal * 0.01f;
                decal.Tr.rotation = Quaternion.LookRotation(-normal, Vector3.up);
                decal.Tr.localScale = Vector3.one * CsCombatTuning.DecalSize;
                decal.Sprite.sprite = _sprHole;
                decal.Sprite.enabled = _sprHole != null;
                decal.Billboard = false;
                if (_sprHole == null) WarnSpriteOnce($"弹痕贴图缺失：Resources/{ResPaths.FxBulletHole}.png（打墙不留痕）");
            }

            // ---- 火星：一记极短的白点（原版打在墙上会有一小撮灰/火星）----
            var spark = AcquireSprite(Shape.Sprite, Color.white, CsCombatTuning.SparkDuration);
            if (spark == null) return;
            spark.Tr.position = point + normal * 0.02f;
            spark.Tr.rotation = Quaternion.LookRotation(-normal, Vector3.up);
            spark.Tr.localScale = Vector3.one * CsCombatTuning.SparkSize;
            spark.Sprite.sprite = _sprSpark;
            spark.Sprite.enabled = _sprSpark != null;
            spark.Billboard = true;
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

        /// <summary>每帧推进生命周期 + 把"朝相机"的贴片转向相机。</summary>
        public void Tick(float dt)
        {
            if (dt < 0f) dt = 0f;

            var cam = Camera.main;
            for (var i = 0; i < _items.Count; i++)
            {
                var item = _items[i];
                if (item.Persistent || item.Go == null || !item.Go.activeSelf) continue;

                if (item.Billboard && cam != null)
                {
                    item.Tr.rotation = Quaternion.LookRotation(item.Tr.position - cam.transform.position, cam.transform.up);
                }

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
            target.Billboard = false;
            target.Decal = false;
            if (target.Light != null) target.Light.enabled = shape != Shape.Sprite;
            target.Go.SetActive(true);
            return target;
        }

        /// <summary>取一枚贴片（火焰 / 火星 / 弹痕共用；颜色恒为白，靠贴图自己的颜色）。</summary>
        private EffectItem AcquireSprite(Shape shape, Color color, float life)
        {
            var item = Acquire(shape, color, life);
            if (item == null) return null;
            if (item.Light != null) item.Light.enabled = false;
            return item;
        }

        /// <summary>
        /// 取一枚**弹痕**：超过 <see cref="CsCombatTuning.MaxDecals"/> 就复用"剩得最少"的那一枚
        /// （= 最老的一条），这样连续扫射不会把池撑爆、也不会让最旧的弹痕永远不消失。
        /// </summary>
        private EffectItem AcquireDecal()
        {
            Init();
            RequestSprites();

            EffectItem oldest = null;
            var decals = 0;
            for (var i = 0; i < _items.Count; i++)
            {
                var item = _items[i];
                if (item.Shape != Shape.Sprite || !item.Decal) continue;
                decals++;
                if (item.Persistent || item.Go == null || !item.Go.activeSelf)
                {
                    oldest = item;      // 已经有空闲的弹痕 ⇒ 直接用
                    break;
                }
                if (oldest == null || item.Life < oldest.Life) oldest = item;
            }

            if (decals >= CsCombatTuning.MaxDecals && oldest != null)
            {
                oldest.Go.SetActive(true);
                oldest.Life = CsCombatTuning.DecalDuration;
                oldest.MaxLife = oldest.Life;
                oldest.StartScale = oldest.EndScale = 0f;
                return oldest;
            }

            var created = AcquireSprite(Shape.Sprite, Color.white, CsCombatTuning.DecalDuration);
            if (created != null) created.Decal = true;
            return created;
        }

        private EffectItem Create(Shape shape, Color color)
        {
            if (_items.Count >= SoftPoolLimit)
            {
                _log.Warn("fx.pool.full",
                    $"特效池已达上限 {SoftPoolLimit}，本次特效被丢弃（考虑降低开火频率或提高上限）");
                return null;
            }

            GameObject go;
            MeshRenderer renderer = null;
            SpriteRenderer sprite = null;

            if (shape == Shape.Sprite)
            {
                go = new GameObject("FX_Sprite");      // 贴片走 SpriteRenderer：没有碰撞体、自带 Sprites/Default
                sprite = go.AddComponent<SpriteRenderer>();
                sprite.color = color;
                sprite.enabled = false;                // 贴图到位前不显示（绝不画成白块）
            }
            else
            {
                var primitive = shape == Shape.Sphere ? PrimitiveType.Sphere : PrimitiveType.Cube;
                go = GameObject.CreatePrimitive(primitive);
                if (go == null)
                {
                    _log.Error("fx.create.fail", $"CreatePrimitive({primitive}) 失败，特效不可用");
                    return null;
                }
                go.name = shape == Shape.Sphere ? "FX_Sphere" : "FX_Cube";

                // 自建特效不能带碰撞体：否则弹道会打在"火焰/弹道"上（射线命中非角色碰撞体 = 一层墙）。
                var collider = go.GetComponent<Collider>();
                if (collider != null) Object.DestroyImmediate(collider);

                renderer = go.GetComponent<MeshRenderer>();
                if (renderer != null)
                {
                    renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    renderer.receiveShadows = false;
                    // 取一次 .material 会给本实例克隆一份材质（Unity 语义），随后改颜色不会影响别的物件。
                    var mat = renderer.material;
                    if (mat != null) mat.color = color;
                }
            }

            go.transform.SetParent(_root, false);
            go.SetActive(false);

            // 片BV-R 实测（同一冻结帧、屏幕级 A/B，数字见 .ai-tmp/test/bvr-diff.txt）：
            // Unity 编辑器会在 Game view 之上给每个 **Light** 画一个「太阳」图标，而枪口点光就在
            // 相机正前方几厘米处 ⇒ 图标被投影成一大块（实测：只把这组宿主设成 HideInHierarchy 后，
            // 差集 26 101 像素整块消失 / 占比 3.48% / maxAbsDiff 219；把这组复原后画面与基线
            // **MD5 完全相同** ⇒ 因果干净）。
            // HideInHierarchy 只改"编辑器叠加层画不画"：⛔ 不改渲染、⛔ 不动物理射线、⛔ 不影响池复用
            // （**不用** HideAndDontSave —— 那会让对象在切场景时不被销毁，池的语义就变了）。
            go.hideFlags = HideFlags.HideInHierarchy;

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
                Sprite = sprite,
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
