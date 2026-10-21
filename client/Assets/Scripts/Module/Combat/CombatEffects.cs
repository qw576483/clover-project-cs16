using System.Collections.Generic;
using CloverEngine;
using Cs16.Core;
using Cs16.Module.Player;
using UnityEngine;

namespace Cs16.Module.Combat
{
    /// <summary>
    /// 枪口火焰 / 弹道 / 弹痕 / 血迹 / 爆炸的表现（不依赖预制体；贴图一律走 <c>Resources/UI/Art/fx_*</c>，
    /// 名字与载体出处集中在 <see cref="ResPaths"/>）。
    ///
    /// <para><b>贴图都是原版载体解出的像素</b>：枪口火焰 ← <c>sprites/muzzleflash*.spr</c>、
    /// 弹痕 ← <c>decals.wad</c> 的 <c>{shot1..5</c>、血迹贴花 ← <c>decals.wad</c> 的 <c>{blood1..6</c>、
    /// 命中主血雾 ← <c>valve/sprites/bloodspray.spr</c>（64×64×10 帧）、飞散血滴 ←
    /// <c>valve/sprites/blood.spr</c>（16×16×9 帧）；只有 <c>fx_spark</c> 一张是本项目生成
    /// （原版击中火星没有独立载体，登记在 <c>client/资源欠缺清单.md</c>）。
    /// 换素材是**同名覆盖 PNG**，本类一行不用改。</para>
    ///
    /// <para><b>尺寸 / 朝向的两条硬口径</b>：① <c>localScale</c> 是**倍率不是米** —— 一张贴片天生多宽
    /// 由它自己的导入 PPU 决定，凡是"米"都必须经 <see cref="SpriteScaleForMeters"/> 反算
    /// （见该方法的注释）；② 贴到面上的四元数必须用 <see cref="SurfaceUp"/> 给参考上向 ——
    /// 命中地面时 <c>-normal</c> 与 <c>Vector3.up</c> 平行，<c>LookRotation</c> 会退化。</para>
    ///
    /// <para><b>枪口火焰 = 贴片 + 点光</b>：原版是一张 <c>sprites/muzzleflash*.spr</c> 亮斑（不是球体），
    /// 落点按**相机局部系**给（<see cref="CsCombatTuning.MuzzleOffsetRight"/> 等横向 / 竖直两个分量 +
    /// **逐武器**的轴向距离 <see cref="CsCombatTuning.MuzzleForward"/>，后者取自该武器视模型实测的枪口
    /// 轴向距离，表见 <see cref="CsCombatTuning.MuzzleAxial"/>），贴片用 <c>SpriteRenderer</c>；
    /// 逐武器贴图见 <see cref="PickMuzzleFlash"/>，逐武器尺寸 / 时长见
    /// <see cref="CsCombatTuning.MuzzleFlashSize"/> / <see cref="CsCombatTuning.MuzzleFlashDuration"/>。
    /// 点光是第一人称里最有效的"开枪了"读感（墙面会被照亮）。</para>
    ///
    /// <para><b>物件来自引擎对象池</b>：本模块的产出路径里没有、也不该有预制体
    /// （<c>Resources/UI/{类名}.prefab</c> 归 UI 生成器写）⇒ 每个形状向 <c>Game.Pool</c> 注册一个
    /// **代码工厂**（<c>IObjectPool.Register</c>），由池负责实例化、挂父级、命名与归还；
    /// 复用策略（同形状 + 同颜色的空闲槽位、软上限 <see cref="SoftPoolLimit"/>）仍在本类，
    /// 因为每个物件的寿命 / 帧序列 / 速度是表现状态，不属于池。</para>
    ///
    /// <para><b>几何来源</b>：球/方块走 <c>GameObject.CreatePrimitive</c>（Unity 内置网格），
    /// 贴片走 <c>SpriteRenderer</c>。贴片的着色器有两档，两档都保证"一定被打进包"
    /// （⛔ 不用 <c>Shader.Find</c> —— 那有"打包后找不到、画面全空"的静默风险）：
    /// 默认一档是 Unity 内置的 <c>Sprites/Default</c>（贴片自带）；**墙上的弹痕**一档换成
    /// <see cref="ResPaths.DecalAlphaClipShader"/>，它相对 <c>Sprites/Default</c> 只多一步
    /// <c>clip(c.a - 0.25)</c>（原版贴花绘制时开着 <c>GL_ALPHA_TEST</c> 且阈值继承
    /// <c>gl_alphamin</c> = 0.25，出处见 <see cref="CsCombatTuning.DecalAlphaCutoff"/>）。
    /// 自建物件上的 <c>Collider</c> 立刻 <c>DestroyImmediate</c> 掉：否则特效自己会被射线打中。</para>
    /// </summary>
    internal sealed class CombatEffects
    {
        private const string RootName = "CsCombatFX";

        /// <summary>对象池里本模块的池分组（切场景时随组清理）。</summary>
        private const string PoolGroup = "CS16FX";

        /// <summary>池中同类特效的软上限（超出即复用最久的一个，不会无限增长）。</summary>
        private const int SoftPoolLimit = 256;

        /// <summary>烟团贴片的软边贴图边长（像素）。</summary>
        private const int SmokeTextureSize = 64;

        /// <summary>烟团三张交叉面绕 Y 轴的夹角（度）：任意视角都至少有一张正对相机。</summary>
        private static readonly float[] SmokeCrossAngles = { 0f, 60f, 120f };

        private enum Shape
        {
            Sphere = 0,
            Cube = 1,
            Sprite = 2,
            /// <summary>自带网格的实体（弹壳）：它的几何来自 <see cref="CsShellModels"/>，不是程序化建形状。</summary>
            Mesh = 3,
            /// <summary>半透明烟团：三张交叉的软边贴片，材质走 <c>Sprites/Default</c>（alpha 混合）。</summary>
            Smoke = 4,
        }

        private sealed class EffectItem
        {
            public GameObject Go;
            public Transform Tr;
            public MeshRenderer Renderer;
            public SpriteRenderer Sprite;
            /// <summary>网格类特效（弹壳）的网格槽：每次抛出时换成该武器那种壳的网格。</summary>
            public MeshFilter Mesh;
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
            /// <summary>是不是"弹痕/血迹"这类贴片（它们有独立上限，满了复用最旧的一条）。</summary>
            public bool Decal;

            /// <summary>是不是**血迹**贴花（与墙上弹痕分开计数、分开上限，见 <see cref="AcquireDecal"/>）。</summary>
            public bool Blood;

            /// <summary>逐帧播放的贴片（血雾）：为 true 时 <see cref="Frames"/> 按寿命均匀推进。</summary>
            public bool Animated;
            /// <summary>动画贴片的帧序列（血点 / 血雾各一套）。</summary>
            public Sprite[] Frames;
            /// <summary>当前帧下标。</summary>
            public int Frame;
            /// <summary>当前帧已过的时间（秒）。</summary>
            public float FrameTime;

            /// <summary>是不是飞行中的弹壳（每帧积分速度/自旋，撞到世界面反弹，见 <see cref="TickShell"/>）。</summary>
            public bool Shell;
            /// <summary>弹壳 / 血滴的速度（米/秒）。</summary>
            public Vector3 Velocity;
            /// <summary>弹壳自旋（度/秒，绕本地三轴）。</summary>
            public Vector3 Spin;

            /// <summary>血滴：每帧受慢重力（见 <see cref="CsCombatTuning.BloodDropGravityScale"/>）并沿速度平移。</summary>
            public bool Falling;

            /// <summary>是不是血滴（与主血雾分开计数、单独上限，见 <see cref="AcquireBloodDrop"/>）。</summary>
            public bool BloodDrop;

            /// <summary>贴片绕视线方向的滚转角（度）：血雾每张随机一个，避免整片正面朝上、每次看起来一模一样。</summary>
            public float Roll;

            /// <summary>烟团上升速度（米/秒）：C4 烟柱用，其余物件恒为 0。</summary>
            public float RiseSpeed;

            /// <summary>
            /// 本物件**自己克隆出来**的材质（<c>Renderer.material</c> 的实例）：
            /// Unity 不随 GameObject 一起销毁它 ⇒ <see cref="Dispose"/> 里手动销毁。
            /// 贴片用的是共享的 <c>Sprites/Default</c>，这里恒为 null（⛔ 不许销毁共享材质）。
            /// </summary>
            public Material OwnedMaterial;
        }

        private readonly CsModuleLog _log = new CsModuleLog("Combat");
        private readonly List<EffectItem> _items = new List<EffectItem>(128);

        /// <summary>在场弹壳的计数闸门（原版 cvar <c>max_shells</c>；容量见 <see cref="CsCombatTuning.MaxShells"/>）。</summary>
        private readonly CsCombatTuning.ShellCountGate<EffectItem> _shells =
            new CsCombatTuning.ShellCountGate<EffectItem>(CsCombatTuning.MaxShells);

        private Transform _root;

        // ---- 贴图（异步加载；缺资源时只 Warn 一次并退化成"点光 + 无贴片"，绝不退回黄色球）----
        private Sprite _sprFlash;
        /// <summary>枪口火焰**十字形**变体（原版 `sprites/muzzleflash3.spr` 帧 0）—— 差异 #89：B51/M249 用它。</summary>
        private Sprite _sprFlashCross;
        private Sprite _sprHole;
        private Sprite _sprSpark;
        /// <summary>弹壳贴图·步枪族 / 手枪族（原版 mdl 内嵌 bmp 解出，key 见 <see cref="ResPaths"/>）。</summary>
        private Texture2D _texShellRifle;
        private Texture2D _texShellPistol;
        private bool _shellWarned;
        /// <summary>特效池满、本次特效应丢弃的次数（日志按首次 + 每 <see cref="CsCombatTuning.LogRateEvery"/> 次输出）。</summary>
        private int _poolFullCount;
        /// <summary>血雾因池满被降级（主贴片或血滴少出）的次数，口径同上。</summary>
        private int _bloodPoolWarned;
        /// <summary>弹痕**五变体**（原版 `decals.wad` 的 `{shot1..5`，key 表见 <see cref="ResPaths.FxBulletHoleKeys"/>）。</summary>
        private readonly Sprite[] _sprShots = new Sprite[ResPaths.FxBulletHoleVariants];
        /// <summary>血迹**六变体**（原版 `decals.wad` 的 `{blood1..6`，key 表见 <see cref="ResPaths.FxBloodKeys"/>）。</summary>
        private readonly Sprite[] _sprBlood = new Sprite[ResPaths.FxBloodVariants];
        /// <summary>飞散血滴**九帧**（原版 `valve/sprites/blood.spr`；只取第 0 帧，key 表见 <see cref="ResPaths.FxBloodHitKeys"/>）。</summary>
        private readonly Sprite[] _sprBloodHit = new Sprite[ResPaths.FxBloodHitVariants];
        /// <summary>主血雾**十帧**（原版 `valve/sprites/bloodspray.spr`，key 表见 <see cref="ResPaths.FxBloodSprayKeys"/>）。</summary>
        private readonly Sprite[] _sprBloodSpray = new Sprite[ResPaths.FxBloodSprayVariants];
        /// <summary>碎裂族**三变体**（原版 `decals.wad` 的 `{break1..3`，48×48，key 表见 <see cref="ResPaths.FxBreakKeys"/>）。</summary>
        private readonly Sprite[] _sprBreaks = new Sprite[ResPaths.FxBreakVariants];
        /// <summary>「命中面的材质不在 flags 表里」的日志闸门 —— 只 Warn 一次（高频路径，不刷屏）。</summary>
        private readonly CsCombatTuning.OnceFlag _surfaceMissing = new CsCombatTuning.OnceFlag();
        private bool _spritesRequested;
        private bool _spriteWarned;
        private bool _bloodWarned;
        private bool _bloodHitWarned;
        private bool _bloodSprayWarned;

        /// <summary>
        /// 墙上的贴花（弹痕 / 血迹）共用的材质：由 <see cref="ResPaths.DecalAlphaClipShader"/> 现造一次，
        /// <c>_Cutoff</c> = <see cref="CsCombatTuning.DecalAlphaCutoff"/>。着色器取不到时为 null
        /// ⇒ 贴花退回 <c>Sprites/Default</c>（不裁切、软晕也画出来）。
        /// </summary>
        private Material _decalMat;
        private bool _decalMatWarned;

        /// <summary>
        /// 枪口火焰贴片用的加色材质（由 <see cref="ResPaths.FxAdditiveShader"/> 现造一次，
        /// 出处见该常量的注释：载体 <c>textureFormat = 1 = SPR_ADDITIVE</c>）。取不到时为 null
        /// ⇒ 火焰退回 <c>Sprites/Default</c>（alpha 混合，只在远景下对比度差些，不会看不见）。
        /// </summary>
        private Material _fxAdditiveMat;
        private bool _fxAdditiveWarned;

        /// <summary>
        /// 烟团贴片：运行时生成的径向软边 alpha 贴图包成的 sprite（PPU = 贴图边长 ⇒ scale 1 时正好 1 米见方）。
        /// 池内所有烟团共用这一张，染色走 <c>SpriteRenderer.color</c>（见 <see cref="SmokeColor"/>）。
        /// </summary>
        private Sprite _sprSmoke;

        /// <summary>
        /// 贴片"自带"的那份材质（<c>AddComponent&lt;SpriteRenderer&gt;</c> 时 Unity 给的默认件，
        /// 名为 <c>Sprites-Default</c> / 着色器 <c>Sprites/Default</c>）：贴片每次从池里借出都复位成它。
        /// </summary>
        private Material _spriteMat;

        // ---- 颜色 ----
        /// <summary>
        /// 血雾贴片的染色 = 原版血液载体的基色 **#8B0000 (139,0,0)**。
        ///
        /// <para><b>为什么需要染色</b>：`valve/sprites/bloodspray.spr` 的像素是**灰阶**（调色板
        /// 0..254 是纯灰、255 是透明标记 (0,0,255)）⇒ 红色由渲染时的染色给出；原版也是这条链
        /// （`mp.dll` 的血迹临时实体里写着血色的字节 `BLOOD_COLOR_RED = 0xF7`，见
        /// <c>策划/差异登记.tsv</c> #74）。</para>
        ///
        /// <para><b>色值出处</b>：`valve/sprites/blooddrop.spr` 调色板第 255 项 = `(139,0,0)` ——
        /// 同一批原版血液载体里**唯一显式给出的血红基色**（`spr-extract.py` 的调色板 dump）。
        /// ⚠️ 引擎把 `BLOOD_COLOR_RED(0xF7)` 映射成 RGB 的那一步在 `hw.dll` 里未定位到
        /// （`hw.dll` 无 `float 247.0` 常量、血量临时实体的处理器未找到）⇒ 本值是**载体侧**出处，
        /// 引擎侧映射作为缺口登记。</para>
        /// </summary>
        private static readonly Color BloodSprayTint = new Color(139f / 255f, 0f, 0f, 1f);
        private static readonly Color TracerColor = new Color(1f, 0.97f, 0.78f, 1f);
        private static readonly Color BlastColor = new Color(1f, 0.58f, 0.20f, 1f);
        /// <summary>闪光弹在爆点的白光（与 <see cref="CsHudSnapshot.FlashAlpha"/> 驱动的全屏白同源）。</summary>
        private static readonly Color FlashBlastColor = new Color(0.92f, 0.96f, 1f, 1f);
        private static readonly Color NadeColor = new Color(0.30f, 0.34f, 0.26f, 1f);
        /// <summary>烟团颜色：白灰 + 半透明（与软边贴图的 alpha 相乘，交叉面叠出体积感）。</summary>
        private static readonly Color SmokeColor = new Color(0.78f, 0.78f, 0.78f, 0.70f);

        /// <summary>创建常驻特效根节点（只做一次）。</summary>
        public void Init()
        {
            if (_root != null) return;
            var go = new GameObject(RootName);
            Object.DontDestroyOnLoad(go);
            _root = go.transform;

            EnsureFactories();
            RequestSprites();
        }

        /// <summary>把每个形状的"程序化造物"注册成引擎对象池的代码工厂（本模块没有预制体）。</summary>
        private void EnsureFactories()
        {
            var pool = Game.Pool;
            if (pool == null) return;

            for (var s = Shape.Sphere; s <= Shape.Smoke; s++)
            {
                var shape = s;                       // 闭包捕获：循环变量会被共享
                var key = PoolKeyOf(shape);
                if (pool.IsRegistered(key)) continue;
                pool.Register(key, () => BuildObject(shape));
            }
        }

        /// <summary>形状 → 池键 / 节点名（池内对象按 key 命名，这里再改回可读名便于排障）。</summary>
        private static string PoolKeyOf(Shape shape)
        {
            switch (shape)
            {
                case Shape.Sphere: return "CS16FX/Sphere";
                case Shape.Cube: return "CS16FX/Cube";
                case Shape.Sprite: return "CS16FX/Sprite";
                case Shape.Mesh: return "CS16FX/Shell";
                default: return "CS16FX/Smoke";
            }
        }

        private static string NodeNameOf(Shape shape)
        {
            switch (shape)
            {
                case Shape.Sphere: return "FX_Sphere";
                case Shape.Cube: return "FX_Cube";
                case Shape.Sprite: return "FX_Sprite";
                case Shape.Mesh: return "FX_Shell";
                default: return "FX_Smoke";
            }
        }

        /// <summary>把池里的一件物件还给池（池不可用时退回销毁）。</summary>
        private static void ReleaseGo(GameObject go)
        {
            if (go == null) return;
            var pool = Game.Pool;
            if (pool != null) pool.Despawn(go);
            else Object.Destroy(go);
        }

        /// <summary>销毁全部特效物件（比赛结束 / 模块销毁时）。</summary>
        public void Dispose()
        {
            var pool = Game.Pool;
            for (var i = 0; i < _items.Count; i++)
            {
                // 先销毁本物件自己克隆的材质（Unity 不会跟着 GameObject 释放 renderer.material）。
                if (_items[i].OwnedMaterial != null) Object.Destroy(_items[i].OwnedMaterial);
                // 池不可用时没有别的东西会收这件物件，逐个销毁。
                if (pool == null && _items[i].Go != null) Object.Destroy(_items[i].Go);
            }
            _items.Clear();
            _shells.Clear();

            // 本模块的池按分组整组清理：对象与池条目一起走，不留悬挂引用。
            if (pool != null) pool.ClearGroup(PoolGroup);

            if (_root != null)
            {
                Object.Destroy(_root.gameObject);
                _root = null;
            }

            // 贴花 / 枪口火焰的材质都是本类现造的 ⇒ 随本实例销毁；同时把"贴图已请求"的标记复位，
            // 让同一个实例被再次 Init 时会重新请求（否则 _decalMat 会永远停在 null，弹痕再也不会裁切）。
            if (_decalMat != null) Object.Destroy(_decalMat);
            _decalMat = null;
            if (_fxAdditiveMat != null) Object.Destroy(_fxAdditiveMat);
            _fxAdditiveMat = null;
            // 烟团 sprite 与它的贴图都是运行时现造的（不是资产）⇒ 同样要手动销毁。
            if (_sprSmoke != null)
            {
                if (_sprSmoke.texture != null) Object.Destroy(_sprSmoke.texture);
                Object.Destroy(_sprSmoke);
            }
            _sprSmoke = null;

            // 碎裂族贴片是 Sprite.Create 现包的（不是资产）⇒ 同样要手动销毁，否则每次重开都留 3 张。
            for (var i = 0; i < _sprBreaks.Length; i++)
            {
                if (_sprBreaks[i] != null) Object.Destroy(_sprBreaks[i]);
                _sprBreaks[i] = null;
            }
            _spritesRequested = false;
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
            res.LoadAsset<Sprite>(ResPaths.FxMuzzleFlashCross, s => _sprFlashCross = s);
            res.LoadAsset<Sprite>(ResPaths.FxBulletHole, s => _sprHole = s);
            res.LoadAsset<Sprite>(ResPaths.FxSpark, s => _sprSpark = s);
            res.LoadAsset<Texture2D>(ResPaths.FxShellRifle, t => _texShellRifle = t);
            res.LoadAsset<Texture2D>(ResPaths.FxShellPistol, t => _texShellPistol = t);

            // 贴花材质：着色器走 Resources（不是 Shader.Find —— 那有"打包后被剥掉"的静默失败），
            // 材质在这里现造一次并设死 _Cutoff，之后所有墙上的贴花共用它。
            res.LoadAsset<Shader>(ResPaths.DecalAlphaClipShader, s => _decalMat = BuildDecalMaterial(s));

            // 枪口火焰的加色材质：同一条路（Resources 里的 .shader，⛔ 不是 Shader.Find）。
            res.LoadAsset<Shader>(ResPaths.FxAdditiveShader, s => _fxAdditiveMat = s != null ? new Material(s) : null);

            // 弹痕五变体：按 ResPaths 的**字面量 key 表**逐个加载（不拼串：拼出来的 key
            // 在静态扫描里看不见 ⇒ 闸门 coverage-diff/D1 会把这些贴图判成"文件在盘上但无人读"）。
            for (var i = 0; i < ResPaths.FxBulletHoleKeys.Length; i++)
            {
                var slot = i;                        // 闭包捕获：⛔ 不能直接用 i（循环变量会被共享）
                res.LoadAsset<Sprite>(ResPaths.FxBulletHoleKeys[i], s => _sprShots[slot] = s);
            }

            // 血迹六变体：同上，走字面量表。
            for (var i = 0; i < ResPaths.FxBloodKeys.Length; i++)
            {
                var slot = i;
                res.LoadAsset<Sprite>(ResPaths.FxBloodKeys[i], s => _sprBlood[slot] = s);
            }

            // 命中血点九帧（原版 blood.spr）：同上，走字面量表。
            for (var i = 0; i < ResPaths.FxBloodHitKeys.Length; i++)
            {
                var slot = i;
                res.LoadAsset<Sprite>(ResPaths.FxBloodHitKeys[i], s => _sprBloodHit[slot] = s);
            }

            // 爆头血雾十帧（原版 bloodspray.spr）：同上，走字面量表。
            for (var i = 0; i < ResPaths.FxBloodSprayKeys.Length; i++)
            {
                var slot = i;
                res.LoadAsset<Sprite>(ResPaths.FxBloodSprayKeys[i], s => _sprBloodSpray[slot] = s);
            }

            // 碎裂族三变体（原版 decals.wad 的 {break1..3，48×48）：走字面量表，但按 **Texture2D** 取回来
            // 再自己包成 sprite（见 MakeDecalSprite）—— 不依赖任何导入设置，也不为此写 .meta。
            for (var i = 0; i < ResPaths.FxBreakKeys.Length; i++)
            {
                var slot = i;                        // 闭包捕获：⛔ 不能直接用 i
                var key = ResPaths.FxBreakKeys[i];
                res.LoadAsset<Texture2D>(key, tex => _sprBreaks[slot] = MakeDecalSprite(key, tex));
            }
        }

        /// <summary>
        /// 现造贴花材质：<c>_Cutoff</c> = <see cref="CsCombatTuning.DecalAlphaCutoff"/>
        /// （原版 <c>gl_alphamin</c> 默认值，出处见该常量）。
        /// </summary>
        private Material BuildDecalMaterial(Shader shader)
        {
            if (shader == null)
            {
                WarnDecalMaterialOnce(
                    $"贴花着色器缺失：Resources/{ResPaths.DecalAlphaClipShader}.shader（弹痕退回不裁切）");
                return null;
            }
            var mat = new Material(shader);
            mat.SetFloat("_Cutoff", CsCombatTuning.DecalAlphaCutoff);
            return mat;
        }

        /// <summary>
        /// 把墙上的贴花切到带 alpha 裁切的材质。材质不可用（着色器缺失 / 尚未加载完）时退回
        /// <c>Sprites/Default</c> —— 表现与不裁切一致，只 Warn 一次。
        /// </summary>
        private void ApplyDecalMaterial(SpriteRenderer sprite)
        {
            if (sprite == null) return;
            if (_decalMat == null)
            {
                WarnDecalMaterialOnce(
                    $"贴花材质不可用 ⇒ 本条弹痕不裁切（Resources/{ResPaths.DecalAlphaClipShader}）");
                return;
            }
            sprite.sharedMaterial = _decalMat;
        }

        private void WarnDecalMaterialOnce(string message)
        {
            if (_decalMatWarned) return;
            _decalMatWarned = true;
            _log.Warn("fx.decal.material", message);
        }

        private void WarnAdditiveOnce(string message)
        {
            if (_fxAdditiveWarned) return;
            _fxAdditiveWarned = true;
            _log.Warn("fx.additive.material", message);
        }

        /// <summary>从一组贴图里**均匀随机**取一张非空的（原版是五变体随机；不用"最旧的一张"之类的假随机）。</summary>
        private static Sprite PickRandom(Sprite[] pool)
        {
            var have = 0;
            for (var i = 0; i < pool.Length; i++)
            {
                if (pool[i] != null) have++;
            }
            if (have == 0) return null;
            if (have == 1)
            {
                for (var i = 0; i < pool.Length; i++)
                {
                    if (pool[i] != null) return pool[i];
                }
            }

            // 只在"真的加载到的那几张"之间等概率取（缺资源时不会把概率压到空槽上）。
            // 变体选择是**表现**（贴图好看与否，与命中判定无关），但**必须**走 CsRng：
            // UnityEngine.Random 是全局静态流，这里每命中一次就抽一次，
            // 用它会把玩法侧的散布/瞄准序列整体推位 ⇒ 同一局重放对不上。
            // 单独一路 FxVariant ⇒ 表现抽多少次都不影响玩法流。
            var pick = CsRng.Stream(CsRngStream.FxVariant).Next(0, have);
            for (var i = 0; i < pool.Length; i++)
            {
                if (pool[i] == null) continue;
                if (pick == 0) return pool[i];
                pick--;
            }
            return null;
        }

        /// <summary>取序列里第一张加载到的贴图（全缺时为 null）。</summary>
        private static Sprite FirstLoaded(Sprite[] frames)
        {
            for (var i = 0; i < frames.Length; i++)
            {
                if (frames[i] != null) return frames[i];
            }
            return null;
        }

        /// <summary>
        /// 按弹痕**族**选贴图 + 给出对应的世界尺寸（米）。
        ///
        /// <para>碎裂族（<see cref="CsCombatTuning.SurfaceDecals.Family.Break"/>）走 <see cref="_sprBreaks"/>
        /// （原版 `{break1..3`，48 texel ⇒ 1.2192 m）；其余族走五变体 <see cref="_sprShots"/>
        /// （16 texel ⇒ 0.4064 m）。</para>
        ///
        /// <para>尺寸跟着**实际用到的那套贴图**走，不跟着"判出来的族"走：碎裂族那一套一张都没加载到时
        /// 回落普通弹痕，尺寸也必须一起回落到 0.4064 m（否则会把 16×16 的贴片抻到 1.2192 m）。
        /// 两者都取不到时退回程序化替身 <see cref="ResPaths.FxBulletHole"/>（⛔ 不画白块）。</para>
        /// </summary>
        private Sprite PickBulletHole(CsCombatTuning.SurfaceDecals.Family family, out float sizeMeters)
        {
            sizeMeters = CsCombatTuning.SurfaceDecals.SizeMeters(family);
            var pool = family == CsCombatTuning.SurfaceDecals.Family.Break ? _sprBreaks : _sprShots;
            var s = PickRandom(pool);
            if (s != null) return s;

            if (family == CsCombatTuning.SurfaceDecals.Family.Break)
            {
                var fb = PickRandom(_sprShots);
                if (fb != null)
                {
                    WarnSpriteOnce($"碎裂族贴图缺失：Resources/{ResPaths.FxBreakKeys[0]}.png … " +
                                   $"{ResPaths.FxBreakKeys[ResPaths.FxBreakKeys.Length - 1]}.png" +
                                   "（木/玻璃命中回落普通弹痕，尺寸一并回落）");
                    sizeMeters = CsCombatTuning.DecalSize;
                    return fb;
                }
            }

            if (_sprHole == null) WarnSpriteOnce($"弹痕贴图缺失：Resources/{ResPaths.FxBulletHoleKeys[0]}.png（打墙不留痕）");
            return _sprHole;
        }

        private void WarnSpriteOnce(string message)
        {
            if (_spriteWarned) return;
            _spriteWarned = true;
            _log.Warn("fx.sprite", message);
        }

        /// <summary>
        /// 把一张贴片放大到「世界上 <paramref name="meters"/> 米宽」所需的 <c>localScale</c>。
        ///
        /// <para><b>为什么不能直接写 <c>Vector3.one * meters</c>（实测的坑）</b>：
        /// <c>localScale</c> 是**倍率**，不是米 —— 一张贴片"天生"多宽由它自己的导入 PPU
        /// （<c>spritePixelsToUnits</c>）决定。本工程的 <c>Resources/UI/Art/fx_*.png</c> 是 Unity
        /// 自动导入的默认值 <b>PPU=100</b>，所以 16x16 的弹痕天生只有 0.16 世界单位宽；
        /// 直接把它当米写就少乘 1/PPU 倍（0.4064 m 会画成 6.5 cm），远看就是没贴上。
        /// 用贴片自己的 <c>bounds</c> 反算成倍率后，<c>meters</c> 才真的是米，
        /// 且换任何 PPU / 任何像素宽的贴图都不会再错（不必依赖"meta 里的 PPU 别写错"这种共识 ——
        /// 重导一次就静默回退）。</para>
        ///
        /// </summary>
        /// <param name="s">贴片；null 时退回 <paramref name="meters"/>（调用方随后会把 renderer 关掉，不会显示）</param>
        /// <param name="meters">期望的世界宽度（米）</param>
        private static float SpriteScaleForMeters(Sprite s, float meters)
        {
            if (s == null) return meters;
            var unitWidth = s.bounds.size.x;                      // = 像素宽 / spritePixelsToUnits
            return unitWidth > 0.0001f ? meters / unitWidth : meters;
        }

        /// <summary>
        /// 贴一个"面朝向"的四元数时要用的**参考上向**。
        ///
        /// <para><b>为什么不能恒定用 <c>Vector3.up</c>（实测）</b>：
        /// <c>Quaternion.LookRotation(forward, up)</c> 要求 <c>up</c> 与 <c>forward</c> **不平行**。
        /// 命中**地面**时法线就是 (0,1,0)，<c>-normal</c> 与 <c>Vector3.up</c> 正好反向 ⇒
        /// 该四元数**退化**，Unity 只能保留一个非法旋转（"面向下 + 上向朝上"）⇒ 贴片**立起来**，
        /// 正对相机看就是一条线。表现上又是"没有弹痕"。
        /// 地板命中改用一个与法线不平行的参考上向（前向），四元数就良定义了。</para>
        ///
        /// <para>三处贴片（弹痕 / 火星 / 血迹）都必须走这里 —— 只覆盖一处等于把同一个坑
        /// 挪到另外两处等着复发。</para>
        /// </summary>
        private static Vector3 SurfaceUp(Vector3 normal)
        {
            return Mathf.Abs(Vector3.Dot(normal, Vector3.up)) > 0.9f ? Vector3.forward : Vector3.up;
        }


        /// <summary>
        /// 枪口火焰：**一张原版亮斑贴片**（面向相机）+ 一盏点光。
        /// 点光是第一人称里最有效的"开枪了"读感（墙与敌人会被照亮）。
        ///
        /// <list type="number">
        /// <item><b>落点</b> = <c>相机局部系</c>，**三个分量都随武器**：横向 <see cref="CsCombatTuning.MuzzleLateral"/>、
        /// 竖直 <see cref="CsCombatTuning.MuzzleVertical"/>、轴向 <see cref="CsCombatTuning.MuzzleForward"/>。
        /// 落点若落在视模型几何之内，就会被近端枪身/手臂在深度测试里盖掉（贴片走透明队列但**开**深度测试）
        /// ⇒ 表现上等于"没有枪口火焰"。</item>
        /// <item><b>尺寸</b>必须走 <see cref="SpriteScaleForMeters"/>：贴片导入 PPU=100，
        /// 直接写米会把 0.30 m 画成 0.192 m。同族坑见 <see cref="CsCombatTuning.DecalSize"/>。</item>
        /// <item><b>逐武器贴图</b>：见 <see cref="PickMuzzleFlash"/>（本工程只接了有一条证据的那一条映射）。</item>
        /// </list>
        /// </summary>
        /// <param name="eyePosition">视点（相机世界坐标）</param>
        /// <param name="viewRotation">相机世界旋转 —— 落点按**相机局部系**给（见 CsCombatTuning 的偏移口径），
        /// 不再用 world-up 叉乘算 right/up：那在俯仰接近 ±90° 时会退化。</param>
        /// <param name="weaponId">当前武器 id（决定轴向距离 / 尺寸 / 时长，以及用哪张原版贴图；未知/为空走默认档）</param>
        public void MuzzleFlash(Vector3 eyePosition, Quaternion viewRotation, string weaponId)
        {
            SpawnMuzzleFlash(
                eyePosition + viewRotation * new Vector3(
                    CsCombatTuning.MuzzleLateral(weaponId),
                    CsCombatTuning.MuzzleVertical(weaponId),
                    CsCombatTuning.MuzzleForward(weaponId)),
                weaponId);
        }

        /// <summary>
        /// **世界坐标版**枪口火焰：落点由调用方直接给世界位置（第三人称 = 射手的枪口节点，
        /// 见 <c>ActorView.TryGetMuzzle</c>）。
        ///
        /// <para>与 <see cref="MuzzleFlash"/> 的差别**只有落点怎么算**：贴图 / 尺寸 / 时长 / 点光
        /// 是同一套 —— 逐武器的那些量都出自 <see cref="CsCombatTuning"/>，与"谁在看"无关。</para>
        /// </summary>
        public void MuzzleFlashWorld(Vector3 worldPosition, string weaponId)
        {
            SpawnMuzzleFlash(worldPosition, weaponId);
        }

        /// <summary>枪口火焰的唯一实现（<see cref="MuzzleFlash"/> / <see cref="MuzzleFlashWorld"/> 共用）。</summary>
        private void SpawnMuzzleFlash(Vector3 pos, string weaponId)
        {
            var radial = CsCombatTuning.MuzzleLateral(weaponId);
            var vertical = CsCombatTuning.MuzzleVertical(weaponId);
            var forward = CsCombatTuning.MuzzleForward(weaponId);
            var sizeMeters = CsCombatTuning.MuzzleFlashSize(weaponId);
            var duration = CsCombatTuning.MuzzleFlashDuration(weaponId);

            var sprite = PickMuzzleFlash(weaponId);

            var item = AcquireSprite(Shape.Sprite, Color.white, duration);
            if (item == null) return;

            item.Tr.position = pos;
            item.Sprite.sprite = sprite;
            item.Sprite.enabled = sprite != null;
            item.Billboard = true;
            // 加色（出处 = 载体 textureFormat = SPR_ADDITIVE，见 CsFxAdditive.shader 头）：
            // 材质取不到就保持池里复位过的 Sprites/Default，只报一次。
            if (_fxAdditiveMat != null) item.Sprite.sharedMaterial = _fxAdditiveMat;
            else WarnAdditiveOnce($"枪口火焰加色着色器缺失：Resources/{ResPaths.FxAdditiveShader}.shader（退回 alpha 混合）");
            // 必须按贴片自己的宽度反算倍率（见 SpriteScaleForMeters）：直接写米会小 1/PPU 倍。
            item.Tr.localScale = Vector3.one * SpriteScaleForMeters(sprite, sizeMeters);
            if (sprite == null) WarnSpriteOnce($"枪口火焰贴图缺失：Resources/{ResPaths.FxMuzzleFlash}.png（只保留点光）");

            // 枪口**不带点光**。出处：原版 muzzle flash 例程（`原版资源/cs16src/hw.dll`，VA 0x1D30BF0）
            // 写下的那条记录只有三个旋钮 —— 白（0x1D30C99–0x1D30CBD 连写 `0xFF`×3）、存活 0.01 s
            // （`[0x1E498A0]` = f64 0.01）、`scale = (arg/10) × 0.1`（`[0x1E49910]`，默认 0.5f）
            // —— **没有强度 / 半径 / 衰减**这类点光参数。本工程此前那盏 `Light`（intensity 4 / range 8）
            // 没有任何载体出处，实机上把开火那一帧打成一屏近白（1.0 m 机位近白占屏 55%、mean 200.7，
            // 同机位基线 0~33 / 47.5~102.8），而原版录像同一次开火的整帧亮度增量只有 +34%
            // ⇒ 按"写不出出处的量不许进工程"去掉；火焰的读感由**加色贴片**承担。
            // 缺口与两侧读数登记：`策划/差异登记.tsv` #178。
            if (item.Light != null) item.Light.enabled = false;

            // 可核对字段（判据 = flashAxial / flashSize / flashDur 逐武器**不恒定**、且 flashAxial 不小于
            // 该武器枪口轴向距离）：落点落在枪身几何之内时"没有火焰"，只看得见这三个数。
            _log.Info("shot.muzzle",
                $"武器 {weaponId ?? "-"} 贴图 {(sprite != null ? sprite.name : "null")} 启用={item.Sprite.enabled}" +
                $" flashAxial={forward:F4} flashLat={radial:F4}" +
                $" flashUp={vertical:F4} flashSize={sizeMeters:F3}" +
                $" flashDur={duration:F3} 落点={pos} 缩放={item.Tr.localScale.x:F3}");
        }

        /// <summary>
        /// 逐武器选枪口火焰贴图。本工程只接了**有一条证据**的那条映射：<c>m249 → 十字形</c>
        /// （<see cref="ResPaths.FxMuzzleFlashCross"/>）；其余武器一律走默认那张。
        ///
        /// <para><b>为什么其余武器一条都没有</b>（引擎侧的硬事实，`hw.dll` 在盘、已逐条查过）：
        /// 引擎 `hw.dll` 只 precache **三张**（`sprites/muzzleflash1/2/3.spr`，串在文件偏移
        /// <c>0x16d780 / 0x16d79c / 0x16d7b8</c>，precache 序列在 <c>0x2e090</c>–<c>0x2e0c7</c>，
        /// 句柄存进全局 <c>0x2CD3D70 / 74 / 78</c>；`muzzleflash4.spr` 引擎**根本不引用**），
        /// 选哪一张由调用方传进来的**一个整数**决定 —— 选择例程在 VA <c>0x1D30BF0</c>：
        /// <c>idx = (arg % 10) % 3</c>（<c>0x1D30C0A</c>–<c>0x1D30C14</c>）+ 缩放 <c>(arg / 10) * K</c>，
        /// 表基址 <c>[ebx*4 + 0x2CD3D70]</c>（<c>0x1D30C55</c>）。</para>
        ///
        /// <para>而该例程在 `hw.dll` 里**没有任何直接调用点**（`call rel32` 0 处、
        /// `call [0x1E825C4]` 0 处），它只作为**函数指针表的第 19 项**存在（表首 VA <c>0x1E82578</c>，
        /// 该项文件偏移 <c>0x1825c4</c>，表首地址仅被 <c>0x166be4</c> 的一处引用取走 = 交给游戏 DLL）
        /// ⇒ **逐武器映射不在 `hw.dll`，是调用方（游戏/客户端 DLL）传进来的整数**，
        /// 本机盘上拿不到那张表。其余武器因此维持默认贴图，⛔ 不许凭"哪张好看"编映射。
        /// 缺口逐武器列名登记在 `策划/差异登记.tsv` #89。</para>
        /// </summary>
        private Sprite PickMuzzleFlash(string weaponId)
        {
            if (weaponId == CsWeapons.M249 && _sprFlashCross != null) return _sprFlashCross;
            return _sprFlash;
        }

        /// <summary>
        /// 抛壳：从抛壳窗甩出一枚**原版弹壳模型**（逐武器一种，见 <see cref="CsShellModels.KindFor"/>），
        /// 之后受重力、撞世界面反弹、自旋（见 <see cref="TickShell"/>）。
        ///
        /// <list type="number">
        /// <item><b>几何与贴图全来自载体</b>：顶点/法线/UV/三角形 = 原版 <c>models/{rshell,pshell,rshell_big}.mdl</c>
        /// 逐点搬运（<see cref="CsShellModels"/>）；贴图 = 该 mdl 内嵌 bmp 解出的 PNG。</item>
        /// <item><b>落点与初速都在相机局部系</b>：抛壳口的三项偏移与初速的三个分量逐条来自原版
        /// <c>mp.dll</c> 的 EjectBrassLate（常量与地址见 <see cref="CsCombatTuning"/> 弹壳段）。</item>
        /// <item><b>初速还要叠上玩家自身速度</b>：原版是 <c>vecShellVelocity = pev-&gt;velocity + …</c>，
        /// 所以跑动中开枪时弹壳会跟着人走。</item>
        /// <item><b>没有点光</b>：枪口那一盏在 <see cref="MuzzleFlash"/> 里，弹壳自己不发亮。</item>
        /// <item><b>数量上限</b>：在场弹壳按原版 cvar <c>max_shells</c>（<see cref="CsCombatTuning.MaxShells"/> = 120）
        /// 计数，满员时新来者挤掉**最老的一枚**（<c>CsCombatTuning.ShellCountGate</c>）；寿命到期是另一条
        /// 并存的清除路径（见 <see cref="Tick"/>）。</item>
        /// <item><b>霰弹枪不抛</b>：原版抛 <c>models/shotgunshell.mdl</c>，该件在本机可取的载体里不存在
        /// ⇒ <see cref="CsShellModels.KindFor"/> 返回 <c>None</c>，本方法直接返回。</item>
        /// </list>
        /// </summary>
        /// <param name="eyePosition">视点（相机世界坐标）</param>
        /// <param name="viewRotation">相机世界旋转（落点与初速都在相机局部系里给）</param>
        /// <param name="weaponId">当前武器 id（决定用哪种壳模型）</param>
        /// <param name="shooterVelocity">射击者的世界速度（米/秒），原版初速里加的那一项</param>
        public void ShellEject(Vector3 eyePosition, Quaternion viewRotation, string weaponId,
            Vector3 shooterVelocity)
        {
            var model = CsShellModels.Get(CsShellModels.KindFor(weaponId));
            if (model == null) return;

            var item = Acquire(Shape.Mesh, Color.white, CsCombatTuning.ShellLife);
            if (item == null) return;
            if (item.Light != null) item.Light.enabled = false;

            var port = new Vector3(
                CsCombatTuning.ShellPortLateral,
                CsCombatTuning.ShellPortVertical,
                CsCombatTuning.ShellPortForward);
            item.Tr.position = eyePosition + viewRotation * port;
            item.Tr.rotation = viewRotation;
            // 网格坐标已经是米（CsShellModels 按 0.0254 m/unit 搬），localScale 必须是 1 ——
            // 写成别的值就是同族的"米当倍率"错（见 SpriteScaleForMeters 的类注释）。
            item.Tr.localScale = Vector3.one;

            var tex = model.Texture == ResPaths.FxShellPistol ? _texShellPistol : _texShellRifle;
            if (item.Mesh != null) item.Mesh.sharedMesh = model.Mesh;
            if (item.Renderer != null)
            {
                var mat = item.Renderer.material;
                if (mat != null)
                {
                    mat.mainTexture = tex;
                    mat.color = Color.white;
                }
            }
            if (tex == null)
            {
                WarnShellOnce($"弹壳贴图缺失：Resources/{model.Texture}.png（弹壳会是一枚纯白小柱）");
            }

            // 初速与自旋是**表现**（弹壳飞哪去不影响命中）⇒ 走单独一路 FxVariant：
            // UnityEngine.Random 是全局静态流，用它抽会把玩法侧的散布/瞄准序列整体推位。
            var rng = CsRng.Stream(CsRngStream.FxVariant);
            item.Velocity = shooterVelocity + viewRotation * new Vector3(
                rng.Range(CsCombatTuning.ShellSpeedRightMin, CsCombatTuning.ShellSpeedRightMax),
                rng.Range(CsCombatTuning.ShellSpeedUpMin, CsCombatTuning.ShellSpeedUpMax),
                CsCombatTuning.ShellSpeedForward);
            item.Spin = new Vector3(
                rng.Range(CsCombatTuning.ShellSpinXMin, CsCombatTuning.ShellSpinXMax),
                rng.Range(CsCombatTuning.ShellSpinYMin, CsCombatTuning.ShellSpinYMax),
                rng.Range(CsCombatTuning.ShellSpinZMin, CsCombatTuning.ShellSpinZMax));
            item.Shell = true;

            // 计数闸门：满员时挤出**最老的一枚**（原版 max_shells 上限 ⇒ 新来者挤掉最老的那个；
            // 寿命到期是另一条并存的清除路径，见 Tick）。
            var evicted = _shells.Register(item);
            if (evicted != null)
            {
                if (evicted.Go == null || !_items.Contains(evicted))
                {
                    _log.Warn("fx.shell.gate.foreign",
                        "计数闸门挤出的对象不在特效池里 ⇒ 未回收（闸门与实际不同步）");
                }
                else
                {
                    evicted.Shell = false;
                    evicted.Life = 0f;
                    evicted.Velocity = Vector3.zero;
                    evicted.Spin = Vector3.zero;
                    if (evicted.Go.activeSelf) evicted.Go.SetActive(false);
                }
                _log.Info("fx.shell.gate",
                    $"弹壳数达上限 {CsCombatTuning.MaxShells}（原版 max_shells）⇒ 回收最老的一枚，在场 {_shells.Count}");
            }

            _log.Info("shell.eject",
                $"弹壳 {model.SourceName}（{model.Size.x:F3}x{model.Size.y:F3}x{model.Size.z:F3} m）" +
                $" 出现点={item.Tr.position} 初速={item.Velocity.magnitude:F2} m/s" +
                $" 贴图={(tex != null ? tex.name : "null")} 寿命={CsCombatTuning.ShellLife:F2}s");
        }

        /// <summary>弹壳贴图缺失时只 Warn 一次（每发一壳，绝不每帧刷屏）。</summary>
        private void WarnShellOnce(string message)
        {
            if (_shellWarned) return;
            _shellWarned = true;
            _log.Warn("fx.shell.missing", message);
        }

        /// <summary>
        /// 弹壳的一帧：重力积分 + 撞到世界面就反射（速度按法线反射并衰减）+ 自旋。
        ///
        /// <para><b>为什么用一条从上一帧位置打到这一帧位置的射线</b>：弹壳 3 cm 量级、每帧位移可达
        /// 2 cm 以上，靠"下一帧位置是否在面内"判会直接穿过去（真空中穿掉地板）。
        /// 命中即把位置贴到命中点抬起一个壳半径，避免陷进面里。</para>
        ///
        /// <para>重力取 <see cref="CsConst.Gravity"/>（= 原版 <c>sv_gravity 800</c> × 0.0254）
        /// —— 弹壳与人物、手雷落的是同一条重力。</para>
        /// </summary>
        private void TickShell(EffectItem item, float dt)
        {
            var v = item.Velocity;
            v.y -= CsConst.Gravity * dt;

            var delta = v * dt;
            var len = delta.magnitude;
            var pos = item.Tr.position;

            if (len > 0.0001f &&
                Physics.Raycast(pos, delta / len, out var hit, len + CsCombatTuning.ShellRadius,
                    ~0, QueryTriggerInteraction.Ignore))
            {
                var n = hit.normal.sqrMagnitude > 0.0001f ? hit.normal.normalized : Vector3.up;
                pos = hit.point + n * CsCombatTuning.ShellRadius;
                v = Vector3.Reflect(v, n) * CsCombatTuning.ShellBounceDamping;
                if (v.sqrMagnitude < CsCombatTuning.ShellRestSpeed * CsCombatTuning.ShellRestSpeed)
                {
                    // 弹壳太小：反弹后速度低于阈值就"躺下"（否则会在面上反复弹、看着在抖）。
                    v = Vector3.zero;
                    item.Spin = Vector3.zero;
                }
            }
            else
            {
                pos += delta;
            }

            item.Tr.position = pos;
            item.Velocity = v;
            if (item.Spin != Vector3.zero) item.Tr.Rotate(item.Spin * dt, Space.Self);
        }

        /// <summary>
        /// 命中墙：**弹痕**（按命中面的族选贴图、按法线贴上去）+ 一记小火星。
        /// <paramref name="normal"/> 必须是世界法线（<c>RaycastHit.normal</c>）；
        /// <paramref name="materialName"/> = 命中面的材质名（= 命中 Mesh 的 <c>sharedMaterial.name</c>，
        /// 即 <c>Firearm.ImpactMaterials</c> 的那一项）。
        ///
        /// <para><b>弹痕族</b>由 <see cref="CsCombatTuning.SurfaceDecals"/> 决定：材质名查表得到
        /// 原版判据的两个字段（`[surf+0x8C]` / `[surf+0x68]`），再走原版三步选族 —— 碎裂族 1.2192 m、
        /// 防弹族 / 普通族 0.4064 m。表里没有该材质（含 null）⇒ 回落普通弹痕并只 Warn 一次。</para>
        /// </summary>
        /// <param name="placeDecal">false = 只出火星、不贴弹痕。用于"这一发就把木箱打碎"的命中：
        /// 碎掉的箱体会关渲染（<c>CsBreakable.Break</c>），先贴上去的弹痕会留在空气里。</param>
        public void BulletImpact(Vector3 point, Vector3 normal, string materialName, bool placeDecal = true)
        {
            if (normal.sqrMagnitude < 0.0001f) normal = Vector3.up;
            normal.Normalize();

            // ---- 族判据：材质名 -> 表里的 flags -> 原版三步选族 ----
            var material = materialName;
            if (!CsCombatTuning.SurfaceDecals.TrySelectFamily(material, out var family))
            {
                family = CsCombatTuning.SurfaceDecals.Family.Shot;
                if (_surfaceMissing.Take())
                {
                    _log.Warn("fx.decal.surface.missing",
                        $"命中面材质「{material ?? "null"}」不在 flags 表里（{CsCombatTuning.SurfaceDecals.TableCount} 行，" +
                        "真源 = 原版 de_dust2 BSP）⇒ 本条及以后一律按普通弹痕处理（只报一次）");
                }
            }

            // ---- 弹痕：贴面 + 沿法线抬起 1cm（防 z-fighting），尺寸随族（碎裂 1.2192 m / 其余 0.4064 m）----
            if (!placeDecal)
            {
                _log.Info("shot.decal.skip",
                    $"弹痕落在 {point}（材质「{material ?? "null"}」族 {family}）：命中面本发就碎（木箱）⇒ 不贴弹痕");
            }
            else
            {
                var sprite = PickBulletHole(family, out var sizeMeters);
                var decal = AcquireDecal(blood: false);
                if (decal != null)
                {
                    decal.Tr.position = point + normal * 0.01f;
                    decal.Tr.rotation = Quaternion.LookRotation(-normal, SurfaceUp(normal));
                    // 尺寸是"整块画布"的米数，必须按贴片自己的宽度反算倍率，
                    // 不能直接写 `Vector3.one * sizeMeters`：那少乘 1/PPU 倍（见 SpriteScaleForMeters）。
                    var scale = SpriteScaleForMeters(sprite, sizeMeters);
                    decal.Tr.localScale = Vector3.one * scale;
                    decal.Sprite.sprite = sprite;
                    decal.Sprite.enabled = sprite != null;
                    decal.Billboard = false;
                    // 裁掉软晕、只画硬核（原版贴花绘制时开 GL_ALPHA_TEST + gl_alphamin=0.25）。
                    ApplyDecalMaterial(decal.Sprite);

                    // 可核对日志：贴图=null / 启用=False ⇒ 贴图没加载到；世界宽只允许是**该族**的那一个值
                    // （碎裂 1.2192 / 其余 0.4064），对不上就是尺寸口径坏了。
                    // 「可见核心宽」= 整块 × 载体实测的核心占比（`{shot*`：alpha≥32 的墨迹水平跨度 4~5 texel / 16）
                    // —— 并给出它在**当前这一发的实际距离**上的屏幕投影像素数
                    // （口径：0.127 m 的墨迹在 5 m 处约 24 px）。距离用相机（弹痕的观察者恒是本地相机）。
                    var cam = Game.Camera != null ? Game.Camera.Main : null;
                    var dist = cam != null ? Vector3.Distance(cam.transform.position, point) : 0f;
                    // 可见核心宽按**这一发实际用的族尺寸**算（碎裂族的 48 texel 贴图核心占比与 {shot* 不同，
                    // 故只用整块宽度乘占比这一个口径，不写死）。
                    var coreM = sizeMeters * CsCombatTuning.DecalOpaqueCoreRatio;
                    var corePx = dist > 0.0001f ? coreM * CsCombatTuning.ScreenPixelsPerMeter(dist) : 0f;

                    _log.Info("shot.decal",
                        $"弹痕落在 {point}（法线 {normal}）→ 材质「{material ?? "null"}」族 {family}" +
                        $" 贴图 {(sprite != null ? sprite.name : "null")}" +
                        $" 启用={decal.Sprite.enabled} 世界宽={sizeMeters:F3}m" +
                        $" 缩放={scale:F3} 变体数={ResPaths.FxBulletHoleVariants}/{ResPaths.FxBreakVariants}" +
                        $" 可见核心宽={coreM:F4}m（占比 {CsCombatTuning.DecalOpaqueCoreRatio:F2}）" +
                        $" 本发距离={dist:F2}m ⇒ 核心投影 {corePx:F1}px");
                }
                else
                {
                    // 取不到物件 = 特效池满（Create 返回 null）⇒ 这一发**静默不留弹痕**，必须留痕。
                    _log.Warn("shot.decal.null", $"弹痕物件取不到（特效池满？）：本发落在 {point} 不留痕");
                }
            }

            // ---- 火星：一记极短的白点（原版打在墙上会有一小撮灰/火星）----
            var spark = AcquireSprite(Shape.Sprite, Color.white, CsCombatTuning.SparkDuration);
            if (spark == null) return;
            spark.Tr.position = point + normal * 0.02f;
            spark.Tr.rotation = Quaternion.LookRotation(-normal, SurfaceUp(normal));
            spark.Tr.localScale = Vector3.one * SpriteScaleForMeters(_sprSpark, CsCombatTuning.SparkSize);
            spark.Sprite.sprite = _sprSpark;
            spark.Sprite.enabled = _sprSpark != null;
            spark.Billboard = true;
        }

        /// <summary>
        /// 把取回的 <see cref="Texture2D"/> 现包成贴花 sprite。
        ///
        /// <para><b>为什么按 Texture2D 取</b>：<c>fx_break*.png</c> 是脚本现落的文件，带不带 Sprite 导入设置
        /// 取决于编辑器（本片不写 <c>.meta</c>，也不该依赖某个导入设置）；按纹理取回来自己包成 sprite，
        /// 默认导入（Texture）也拿得到（先例见 <c>UI/InGame/CsScopeOverlayWidget</c> 的取图方式）。</para>
        ///
        /// <para>PPU 取 <see cref="CsCombatTuning.DecalSpritePixelsPerUnit"/>：贴花的米数由
        /// <see cref="SpriteScaleForMeters"/> 按 sprite 自己的宽度反算，PPU 只决定"1 米 = 多少像素"这一个换算。</para>
        /// </summary>
        private static Sprite MakeDecalSprite(string key, Texture2D tex)
        {
            if (tex == null) return null;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            var sprite = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height),
                new Vector2(0.5f, 0.5f), CsCombatTuning.DecalSpritePixelsPerUnit);
            sprite.name = key;
            return sprite;
        }

        /// <summary>
        /// 命中点上的**血雾** = 原版 <c>TE_BLOODSPRITE</c>（115）的两层贴片：
        ///
        /// <list type="number">
        /// <item><b>sprite1</b>（<c>bloodspray.spr</c> 十帧）**1 张**：命中点上方的动画血雾，
        /// 帧序列按 <see cref="CsCombatTuning.BloodPuffDuration"/>（10 帧播 0.25 s）逐帧推进，
        /// 世界宽由 <see cref="CsCombatTuning.BloodSprayWidthMeters"/> 按 size 随机给。</item>
        /// <item><b>sprite2</b>（<c>blood.spr</c> 第 0 帧）**size + rand(1,8) + rand(1,8) 张**：从命中点
        /// 向四周飞散的血滴，带慢重力，生命 rand(1,3) s（见 <see cref="TickBloodDrop"/>）。</item>
        /// </list>
        ///
        /// <para><b>size 从哪来</b>：原版是 <c>clamp(amount * 2 / 10, 3, 16)</c>（多人模式先翻倍），
        /// <c>amount</c> 就是这一发的伤害量 —— 由 <c>CsDamage.ApplyHit</c> 随 <c>OnBulletHit</c> 一起
        /// 送进来，不再按"爆头 / 普通"猜两档。伤害量 ≤ 0 时退到按爆头取上界。</para>
        ///
        /// <para><b>贴片染色</b> = <see cref="BloodSprayTint"/>（两张载体都是灰阶像素）。</para>
        /// </summary>
        /// <param name="point">命中点（世界坐标）</param>
        /// <param name="direction">弹道方向（保留给调用方语义；血滴初速是原版的世界向随机，不取这个方向）</param>
        /// <param name="headshot">是否爆头（只在伤害量不可达时决定 size 的兜底档，并写进日志）</param>
        /// <param name="amount">这一发打出的伤害量（原版 TE_BLOODSPRITE 的 <c>amount</c>）</param>
        public void BloodSpray(Vector3 point, Vector3 direction, bool headshot, int amount)
        {
            Init();

            var size = amount > 0
                ? CsCombatTuning.BloodSpraySizeForAmount(amount)
                : (headshot ? CsCombatTuning.BloodSpraySizeMax : CsCombatTuning.BloodSpraySizeMin);
            var rng = CsRng.Stream(CsRngStream.FxVariant);

            // ---- sprite1：主血雾（1 张，十帧动画；原版在命中点抬高 2..4 unit）----
            var spray = FirstLoaded(_sprBloodSpray);
            if (spray == null) WarnBloodSprayOnce();
            var main = AcquireSprite(Shape.Sprite, BloodSprayTint, CsCombatTuning.BloodPuffDuration);
            var mainWidth = 0f;
            if (main != null)
            {
                mainWidth = CsCombatTuning.BloodSprayWidthMeters(size, rng.Range(0f, 1f));
                main.Tr.position = point + Vector3.up * rng.Range(2f, 4f) * CsConst.UnitToMeter;
                main.Tr.localScale = Vector3.one * SpriteScaleForMeters(spray, mainWidth);
                main.Frames = _sprBloodSpray;
                main.Frame = 0;
                main.FrameTime = 0f;
                main.Animated = true;
                main.Sprite.sprite = spray;
                main.Sprite.enabled = spray != null;
                main.Billboard = true;
                main.Roll = rng.Range(0f, 360f);
            }
            else
            {
                WarnBloodPoolOnce($"血雾主贴片取不到（特效池 {_items.Count}/{SoftPoolLimit}）");
            }

            // ---- sprite2：血滴（blood.spr 第 0 帧；原版 FTENT_ROTATE 不播帧）----
            var drop = FirstLoaded(_sprBloodHit);
            if (drop == null) WarnBloodHitOnce();
            var want = size + rng.Next(1, 9) + rng.Next(1, 9);
            var spawned = 0;
            if (drop != null)
            {
                for (var i = 0; i < want; i++)
                {
                    // 染色走池子的 key（同色才复用），不在取到物件后再改色 —— 否则会把同池的白贴片一起染红。
                    var item = AcquireBloodDrop(
                        rng.Range(CsCombatTuning.BloodDropLifeMin, CsCombatTuning.BloodDropLifeMax));
                    if (item == null) break;

                    item.Tr.position = point;
                    item.Tr.localScale = Vector3.one * SpriteScaleForMeters(drop,
                        CsCombatTuning.BloodDropWidthMeters(size, rng.Range(0f, 1f)));
                    item.Sprite.sprite = drop;
                    item.Sprite.enabled = true;
                    item.Billboard = true;
                    item.Roll = rng.Range(0f, 360f);
                    item.Falling = true;
                    item.Velocity = new Vector3(
                        rng.Range(-CsCombatTuning.BloodDropSpeedUnits, CsCombatTuning.BloodDropSpeedUnits - 1f),
                        rng.Range(-CsCombatTuning.BloodDropSpeedUnits, CsCombatTuning.BloodDropSpeedUnits - 1f),
                        rng.Range(CsCombatTuning.BloodDropUpMinUnits, CsCombatTuning.BloodDropUpMaxUnits))
                        * CsConst.UnitToMeter;
                    spawned++;
                }
            }

            if (spawned < want)
            {
                WarnBloodPoolOnce($"血滴只出到 {spawned}/{want} 张（特效池 {_items.Count}/{SoftPoolLimit}）");
            }

            // 可核对日志：主血雾张数 / 世界宽、血滴实际张数 / 期望张数，以及两者各自的寿命。
            _log.Info("blood.spray",
                $"命中 {point}（爆头={headshot} 伤害量={amount} → size={size}）→ " +
                $"主血雾 {(main != null ? 1 : 0)}/1 张 世界宽={mainWidth:F3}m 帧={_sprBloodSpray.Length} " +
                $"寿命={CsCombatTuning.BloodPuffDuration:F2}s；血滴 {spawned}/{want} 张 " +
                $"世界宽={CsCombatTuning.BloodDropWidthMeters(size, 0f):F3}–" +
                $"{CsCombatTuning.BloodDropWidthMeters(size, 1f):F3}m " +
                $"寿命={CsCombatTuning.BloodDropLifeMin:F0}–{CsCombatTuning.BloodDropLifeMax:F0}s");
        }

        /// <summary>
        /// **子弹打中角色**：命中点上一层血雾（<see cref="BloodSpray"/>）+ 在**后面的那个面**上贴一张血迹贴花。
        ///
        /// <para><b>两层都走原版载体</b>：
        /// ① 血雾 = <c>valve/sprites/bloodspray.spr</c> 的十帧（主贴片）+ <c>valve/sprites/blood.spr</c>
        /// 的第 0 帧（血滴），见 <see cref="BloodSpray"/>；
        /// ② 血迹贴花 = <c>decals.wad</c> 的 <c>{blood1..6</c>（<see cref="ResPaths.FxBloodKeys"/>；
        /// 红/黄分组由 cvar <c>violence_hblood</c> / <c>violence_ablood</c> 选，人类角色恒红）。
        /// 两族都出自 <c>mp.dll</c> 自己的 precache（<c>sprites/bloodspray.spr</c> 串在文件偏移
        /// <c>0x933a6</c> → 全局 <c>0x101aeddc</c>；<c>decals.wad</c> 的 <c>{blood*</c> 名表见
        /// <c>策划/差异登记.tsv</c> #74）。</para>
        ///
        /// <para><b>为什么从命中点再往前打一条射线</b>：原版是在角色**后面的墙**上贴血迹，
        /// 不是贴在角色身上（贴角色身上会随它动，原版没有这种表现）。
        /// 追不到面时**只出血雾、不贴贴花**（不硬塞到空气里）。</para>
        /// </summary>
        /// <param name="point">命中点（世界坐标，来自射线）</param>
        /// <param name="direction">弹道方向（单位向量，从射手指向命中点）</param>
        /// <param name="headshot">是否爆头（只影响日志与 size 的兜底档；见 <see cref="BloodSpray"/>）</param>
        /// <param name="amount">这一发打出的伤害量（原版 TE_BLOODSPRITE 的 <c>amount</c>）</param>
        /// <returns>是否真的落了血迹贴花（供自检 / 日志用）</returns>
        public bool BloodImpact(Vector3 point, Vector3 direction, bool headshot, int amount)
        {
            BloodSpray(point, direction, headshot, amount);
            return BloodDecal(point, direction, headshot);
        }

        /// <summary>
        /// **血迹贴花**：从命中点沿弹道继续向前找"后面的面"，贴一张 <c>decals.wad</c> 的 <c>{blood1..6</c>。
        /// 追不到面时返回 false（只留血雾，不把血硬塞到空气里）。
        /// </summary>
        /// <param name="point">命中点（世界坐标，来自射线）</param>
        /// <param name="direction">弹道方向（单位向量，从射手指向命中点）</param>
        /// <param name="headshot">是否爆头（只影响日志口径）</param>
        /// <returns>是否真的落了血迹贴花</returns>
        public bool BloodDecal(Vector3 point, Vector3 direction, bool headshot)
        {
            Init();

            var dir = direction.sqrMagnitude > 0.0001f ? direction.normalized : Vector3.forward;

            // ---- 血迹贴花：从命中点继续向前找"后面的面" ----
            WarnBloodOnce();
            if (!Physics.Raycast(point + dir * 0.02f, dir, out var hit,
                    CsCombatTuning.BloodDecalTraceRange, ~0, QueryTriggerInteraction.Ignore))
            {
                _log.Info("blood.nosurface",
                    $"命中 {point}（爆头={headshot}）：沿弹道 {CsCombatTuning.BloodDecalTraceRange:F1}m 内没有可贴面 ⇒ 只出血雾");
                return false;
            }

            var spr = PickRandom(_sprBlood);
            var decal = AcquireDecal(blood: true);
            if (decal == null) return false;

            var n = hit.normal.sqrMagnitude > 0.0001f ? hit.normal.normalized : Vector3.up;
            decal.Tr.position = hit.point + n * 0.012f;      // 比弹痕抬得稍多：血贴在弹痕之上
            decal.Tr.rotation = Quaternion.LookRotation(-n, SurfaceUp(n));
            decal.Sprite.sprite = spr;
            decal.Sprite.enabled = spr != null;
            decal.Billboard = false;
            // 与墙上的弹痕同一条 alpha 裁切（原版贴花统一开 GL_ALPHA_TEST + gl_alphamin = 0.25）。
            ApplyDecalMaterial(decal.Sprite);

            var px = spr != null ? (int)spr.rect.width : 48;
            // 尺寸 = 标称画布宽（px × 0.0254 m）。血迹贴花与原版同为**整块四边形**贴图
            // （`{blood1..6` 的导入设置 spriteMeshType=0 / FullRect）⇒ 渲染宽 = `Sprite.bounds` 宽 = 标称，
            // 不需要任何补偿；透明像素由 alpha test 丢弃、不参与几何裁剪。
            var meters = CsCombatTuning.BloodDecalSize(px);
            var scale = SpriteScaleForMeters(spr, meters);
            decal.Tr.localScale = Vector3.one * scale;

            _log.Info("blood.decal",
                $"命中 {point}（爆头={headshot}）→ 血贴在 {hit.point}（面 {hit.collider?.name}，" +
                $"贴图 {(spr != null ? spr.name : "null")} {px}px → 画布宽 {meters:F3}m、" +
                $"缩放 {scale:F3}（整块 quad，无补偿））");
            return true;
        }

        private void WarnBloodOnce()
        {
            if (_bloodWarned) return;
            var have = 0;
            for (var i = 0; i < _sprBlood.Length; i++)
            {
                if (_sprBlood[i] != null) have++;
            }
            if (have > 0) return;
            _bloodWarned = true;
            _log.Warn("fx.blood.missing",
                $"血迹贴图一张都没加载到：Resources/{ResPaths.FxBloodKeys[0]}.png … " +
                $"{ResPaths.FxBloodKeys[ResPaths.FxBloodKeys.Length - 1]}.png" +
                "（受击时血雾照出、墙上不留血）");
        }

        /// <summary>命中血点九帧一张都没加载到时只 Warn 一次（高频路径，绝不每帧刷屏）。</summary>
        private void WarnBloodHitOnce()
        {
            if (_bloodHitWarned) return;
            var have = 0;
            for (var i = 0; i < _sprBloodHit.Length; i++)
            {
                if (_sprBloodHit[i] != null) have++;
            }
            if (have > 0) return;
            _bloodHitWarned = true;
            _log.Warn("fx.bloodhit.missing",
                $"血滴贴图一张都没加载到：Resources/{ResPaths.FxBloodHitKeys[0]}.png … " +
                $"{ResPaths.FxBloodHitKeys[ResPaths.FxBloodHitKeys.Length - 1]}.png" +
                "（命中时只有主血雾、没有飞散的血滴）");
        }

        /// <summary>
        /// 血雾因**特效池满**被降级时的日志：首次 + 每 <see cref="CsCombatTuning.LogRateEvery"/> 次。
        /// 连发时一帧可能少出十几张血滴，不降频会刷屏。
        /// </summary>
        private void WarnBloodPoolOnce(string message)
        {
            _bloodPoolWarned++;
            if (_bloodPoolWarned == 1 || _bloodPoolWarned % CsCombatTuning.LogRateEvery == 0)
            {
                _log.Warn("fx.blood.pool", $"{message}（第 {_bloodPoolWarned} 次）");
            }
        }

        /// <summary>主血雾十帧一张都没加载到时只 Warn 一次（高频路径，绝不每帧刷屏）。</summary>
        private void WarnBloodSprayOnce()
        {
            if (_bloodSprayWarned) return;
            var have = 0;
            for (var i = 0; i < _sprBloodSpray.Length; i++)
            {
                if (_sprBloodSpray[i] != null) have++;
            }
            if (have > 0) return;
            _bloodSprayWarned = true;
            _log.Warn("fx.bloodspray.missing",
                $"血雾贴图一张都没加载到：Resources/{ResPaths.FxBloodSprayKeys[0]}.png … " +
                $"{ResPaths.FxBloodSprayKeys[ResPaths.FxBloodSprayKeys.Length - 1]}.png" +
                "（受击时命中点没有血雾）");
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

        /// <summary>爆炸（HE 手雷 / C4）：一团快速膨胀的橙色亮球 + 点光。</summary>
        public void Explosion(Vector3 position)
        {
            Explosion(position, BlastColor);
        }

        /// <summary>
        /// 爆炸的**唯一实现**：颜色由调用方给（HE = 橙 <see cref="BlastColor"/>、
        /// 闪光弹 = 白 <see cref="FlashBlastColor"/>），膨胀曲线与点光一致。
        /// </summary>
        public void Explosion(Vector3 position, Color color)
        {
            Explosion(position, color, CsCombatTuning.ExplosionVisualMaxRadius);
        }

        /// <summary>爆炸（自定义最终半径）：C4 的可见半径取爆炸伤害半径。</summary>
        public void Explosion(Vector3 position, Color color, float maxRadius)
        {
            var item = Acquire(Shape.Sphere, color, CsCombatTuning.ExplosionVisualDuration);
            if (item == null) return;

            item.Tr.position = position;
            item.StartScale = 0.35f;
            item.EndScale = Mathf.Max(0.35f, maxRadius);
            SetScale(item, item.StartScale);

            // 点光只在这里点亮：Acquire 对所有借出的物件一律关灯（弹道 / 手雷体 / 烟雾球共用池槽）。
            if (item.Light != null)
            {
                item.Light.color = color;
                item.Light.range = Mathf.Clamp(1.5f * maxRadius, 16f, 32f);
                item.Light.intensity = 6f;
                item.Light.enabled = true;
            }
        }

        /// <summary>
        /// C4 爆炸的世界特效：火光球（半径 = 爆炸伤害半径）+ 一根上升的半透明烟柱。
        /// 烟柱沿竖直方向排一列烟团，各自边升边涨；烟团贴片是软边 alpha 混合。
        /// </summary>
        public void BombBlast(Vector3 position, float radius)
        {
            // Unity 球体的 localScale = 直径 ⇒ 传 2 × 伤害半径，火光的可视半径才等于爆炸半径。
            Explosion(position, BlastColor, 2f * radius);

            for (var i = 0; i < CsCombatTuning.BombSmokePuffs; i++)
            {
                var t = CsCombatTuning.BombSmokePuffs > 1
                    ? i / (float)(CsCombatTuning.BombSmokePuffs - 1)
                    : 0f;
                var item = Acquire(Shape.Smoke, SmokeColor, CsCombatTuning.BombSmokeLifetime);
                if (item == null) return;

                item.Tr.position = position + Vector3.up * (radius * (0.15f + 0.55f * t));
                item.StartScale = radius * 0.45f;
                item.EndScale = radius * (0.75f + 0.35f * t);
                item.RiseSpeed = radius * 0.22f;
                SetScale(item, item.StartScale);
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
                if (item.Shell && !_shells.Unregister(item))
                {
                    _log.Warn("fx.shell.gate.desync",
                        "归还的弹壳不在计数队列里 ⇒ 闸门与实际不同步");
                }
                item.Go.SetActive(false);
                return;
            }
            ReleaseGo(go);
        }

        /// <summary>手雷体 / 烟雾球这类"常驻一段时间"的视觉：颜色与尺寸自定义。</summary>
        public GameObject RentSphereGrenade(float scale) => RentSphere(NadeColor, scale);

        /// <summary>半透明烟团（三张软边交叉面）——由 GrenadeThrower / C4 烟柱借用。</summary>
        public GameObject RentSmoke(float scale)
        {
            var item = Acquire(Shape.Smoke, SmokeColor, 0f, persistent: true);
            if (item == null) return null;
            SetScale(item, scale);
            return item.Go;
        }

        /// <summary>每帧推进生命周期 + 把"朝相机"的贴片转向相机。</summary>
        public void Tick(float dt)
        {
            if (dt < 0f) dt = 0f;

            var cam = Game.Camera != null ? Game.Camera.Main : null;
            for (var i = 0; i < _items.Count; i++)
            {
                var item = _items[i];
                if (item.Persistent || item.Go == null || !item.Go.activeSelf) continue;

                if (item.Billboard && cam != null)
                {
                    var look = Quaternion.LookRotation(item.Tr.position - cam.transform.position, cam.transform.up);
                    // 原版每张血雾/血滴给自己一个随机 roll（entity.angles[2] = rand(0,360)）：
                    // 不滚转的话同一帧序列每次都长一个样。
                    item.Tr.rotation = look * Quaternion.Euler(0f, 0f, item.Roll);
                }

                if (item.Animated) TickFrames(item, dt);

                item.Life -= dt;
                if (item.Life <= 0f)
                {
                    // 弹壳寿命到期：从计数闸门里摘掉（槽位不销毁，留给 Acquire 复用）。
                    if (item.Shell && !_shells.Unregister(item))
                    {
                        _log.Warn("fx.shell.gate.desync",
                            "弹壳寿命到期却不在计数队列里 ⇒ 闸门与实际不同步");
                    }
                    item.Go.SetActive(false);
                    continue;
                }

                if (item.Shell) TickShell(item, dt);
                if (item.Falling) TickBloodDrop(item, dt);

                if (item.RiseSpeed != 0f)
                {
                    item.Tr.position += Vector3.up * (item.RiseSpeed * dt);
                }

                if (item.EndScale > item.StartScale)
                {
                    var t = 1f - Mathf.Clamp01(item.Life / item.MaxLife);
                    SetScale(item, Mathf.Lerp(item.StartScale, item.EndScale, t));
                }
            }
        }

        /// <summary>
        /// 逐帧推进动画贴片：帧时长 = 寿命 / 帧数，到最后一帧就停在那里；寿命到点由 <see cref="Tick"/> 回池。
        /// </summary>
        private static void TickFrames(EffectItem item, float dt)
        {
            if (item.Sprite == null) return;
            var frames = item.Frames;
            var n = frames != null ? frames.Length : 0;
            if (n == 0) return;
            var frameTime = item.MaxLife > 0f ? item.MaxLife / n : 0f;
            if (frameTime <= 0f) return;

            item.FrameTime += dt;
            while (item.FrameTime >= frameTime && item.Frame < n - 1)
            {
                item.FrameTime -= frameTime;
                item.Frame++;
                var sprite = frames[item.Frame];
                if (sprite != null) item.Sprite.sprite = sprite;
            }
            item.Sprite.enabled = item.Sprite.sprite != null;
        }

        /// <summary>
        /// 血滴的一帧：原版 <c>FTENT_ROTATE | FTENT_SLOWGRAVITY</c> —— 沿初速平移，按
        /// <see cref="CsCombatTuning.BloodDropGravityScale"/> × <see cref="CsConst.Gravity"/> 下落。
        /// 不做碰撞：贴片走透明队列、深度测试照常，飞进墙/地的血滴会被几何遮住，
        /// 画面上与"撞到世界面即停"等价，且省掉每滴每帧一条射线。
        /// </summary>
        private static void TickBloodDrop(EffectItem item, float dt)
        {
            var v = item.Velocity;
            v.y -= CsConst.Gravity * CsCombatTuning.BloodDropGravityScale * dt;
            item.Tr.position += v * dt;
            item.Velocity = v;
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
            target.Blood = false;
            target.Shell = false;
            target.Velocity = Vector3.zero;
            target.Spin = Vector3.zero;
            target.Falling = false;
            target.BloodDrop = false;
            target.Roll = 0f;
            target.RiseSpeed = 0f;
            target.Animated = false;
            target.Frames = null;
            target.Frame = 0;
            target.FrameTime = 0f;
            // 贴片池是按"形状 + 颜色"复用的（火焰 / 火星 / 弹痕同池）⇒ 每次借出都先把材质复位，
            // 否则一枚用过的弹痕被复用成枪口火焰时会带着弹痕的裁切材质（火焰软晕被切掉一片）。
            if (target.Sprite != null && _spriteMat != null) target.Sprite.sharedMaterial = _spriteMat;
            // 点光只在爆炸里点亮（见 Explosion）：弹道 / 手雷飞行体 / 烟雾球共用这些槽位，
            // 一借出就点亮等于满场点光（每发弹道一盏、每颗雷一盏）。
            if (target.Light != null) target.Light.enabled = false;
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
        /// 取一枚**墙上的贴片**（弹痕 / 血迹）：超过该类的数量上限就复用"剩得最少"的那一枚
        /// （= 最老的一条），这样连续扫射不会把池撑爆、也不会让最旧的贴片永远不消失。
        ///
        /// <para><b>为什么血迹与弹痕**分开计数**</b>：原版是两套独立的东西（`{shot*` 与 `{blood*}` 两组
        /// 名字、两套上限）。共用一本账的话，一梭子扫墙就能把场上的血迹全挤掉 —— 表现就是"打死了人、
        /// 血立刻没了"。</para>
        /// </summary>
        /// <param name="blood">true = 血迹贴花（上限 <see cref="CsCombatTuning.MaxBloodDecals"/>），
        /// false = 弹痕（上限 <see cref="CsCombatTuning.MaxDecals"/>）</param>
        private EffectItem AcquireDecal(bool blood)
        {
            Init();
            RequestSprites();

            var cap = blood ? CsCombatTuning.MaxBloodDecals : CsCombatTuning.MaxDecals;
            var life = blood ? CsCombatTuning.BloodDecalDuration : CsCombatTuning.DecalDuration;

            EffectItem oldest = null;
            var decals = 0;
            for (var i = 0; i < _items.Count; i++)
            {
                var item = _items[i];
                if (item.Shape != Shape.Sprite || !item.Decal || item.Blood != blood) continue;
                decals++;
                if (item.Persistent || item.Go == null || !item.Go.activeSelf)
                {
                    oldest = item;      // 已经有空闲的 ⇒ 直接用
                    break;
                }
                if (oldest == null || item.Life < oldest.Life) oldest = item;
            }

            if (decals >= cap && oldest != null)
            {
                oldest.Go.SetActive(true);
                oldest.Life = life;
                oldest.MaxLife = life;
                oldest.StartScale = oldest.EndScale = 0f;
                return oldest;
            }

            var created = AcquireSprite(Shape.Sprite, Color.white, life);
            if (created != null)
            {
                created.Decal = true;
                created.Blood = blood;
            }
            return created;
        }

        /// <summary>
        /// 取一枚**血滴**：与主血雾分开计数，超过 <see cref="CsCombatTuning.MaxBloodDrops"/> 时
        /// 复用"剩得最少"的那一滴（= 最旧的一条），不挤占枪口火焰 / 弹痕 / 弹壳的池位。
        /// </summary>
        private EffectItem AcquireBloodDrop(float life)
        {
            Init();

            EffectItem oldest = null;
            var count = 0;
            for (var i = 0; i < _items.Count; i++)
            {
                var item = _items[i];
                if (!item.BloodDrop) continue;
                count++;
                if (item.Go == null || !item.Go.activeSelf)
                {
                    oldest = item;      // 已经有空闲的 ⇒ 直接用
                    break;
                }
                if (oldest == null || item.Life < oldest.Life) oldest = item;
            }

            if (count >= CsCombatTuning.MaxBloodDrops && oldest != null)
            {
                oldest.Go.SetActive(true);
                oldest.Life = life;
                oldest.MaxLife = life;
                oldest.StartScale = oldest.EndScale = 0f;
                oldest.Animated = false;
                oldest.Frames = null;
                oldest.Frame = 0;
                oldest.FrameTime = 0f;
                oldest.Falling = true;
                if (oldest.Sprite != null && _spriteMat != null) oldest.Sprite.sharedMaterial = _spriteMat;
                return oldest;
            }

            var created = AcquireSprite(Shape.Sprite, BloodSprayTint, life);
            if (created != null) created.BloodDrop = true;
            return created;
        }

        private EffectItem Create(Shape shape, Color color)
        {
            if (_items.Count >= SoftPoolLimit)
            {
                // 连发时一帧可能连续取很多张（血滴），这里只报首次 + 每 N 次，避免刷屏。
                _poolFullCount++;
                if (_poolFullCount == 1 || _poolFullCount % CsCombatTuning.LogRateEvery == 0)
                {
                    _log.Warn("fx.pool.full",
                        $"特效池已达上限 {SoftPoolLimit}，本次特效被丢弃（第 {_poolFullCount} 次；" +
                        "可降低开火频率或提高上限）");
                }
                return null;
            }

            Init();

            var pool = Game.Pool;
            if (pool == null)
            {
                _log.Error("fx.nopool", "Game.Pool 为 null（Game.Launch 未执行？），特效无法生成");
                return null;
            }

            EnsureFactories();
            var go = pool.Spawn(PoolKeyOf(shape), _root, PoolGroup);
            if (go == null)
            {
                _log.Error("fx.spawn.fail",
                    $"特效物件生成失败（对象池返回 null）：{PoolKeyOf(shape)}（形状 {shape}）");
                return null;
            }
            go.name = NodeNameOf(shape);

            var item = Bind(go, shape);
            item.Color = color;
            ApplyColor(item, color);
            _items.Add(item);
            return item;
        }

        /// <summary>
        /// 池的代码工厂：只造物件与所需组件（形状 / 材质实例 / 点光 / 编辑器隐藏标记），
        /// 颜色与寿命由 <see cref="Bind"/> / <see cref="Acquire"/> 给。
        /// </summary>
        private GameObject BuildObject(Shape shape)
        {
            GameObject go;

            if (shape == Shape.Smoke)
            {
                go = CreateSmokeVisual(SmokeColor);
            }
            else if (shape == Shape.Sprite)
            {
                go = new GameObject("FX_Sprite");      // 贴片走 SpriteRenderer：没有碰撞体、自带 Sprites/Default
                var sprite = go.AddComponent<SpriteRenderer>();
                sprite.enabled = false;                // 贴图到位前不显示（绝不画成白块）
            }
            else if (shape == Shape.Mesh)
            {
                // 弹壳：几何来自 CsShellModels（原版 mdl 逐点搬运），网格由调用方每次换上。
                go = new GameObject("FX_Shell");
                go.AddComponent<MeshFilter>();
                var renderer = go.AddComponent<MeshRenderer>();
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                // 新建 MeshRenderer 自带的内置 Default-Material（Standard，**一定**被打进包）——
                // 取一次 .material 得到本实例的克隆，之后换贴图不会影响别的物件。
                var shellMat = renderer.material;
                if (shellMat != null) shellMat.color = Color.white;
            }
            else
            {
                var primitive = shape == Shape.Sphere ? PrimitiveType.Sphere : PrimitiveType.Cube;
                go = GameObject.CreatePrimitive(primitive);
                if (go == null) return null;
                go.name = NodeNameOf(shape);

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
                    if (mat != null) mat.color = Color.white;
                }
            }

            // Unity 编辑器会在 Game view 之上给每个 **Light** 画一个「太阳」图标，而枪口点光就在
            // 相机正前方几厘米处 ⇒ 图标被投影成一大块（实测：只把这组宿主设成 HideInHierarchy 后，
            // 差集 26 101 像素整块消失 / 占比 3.48% / maxAbsDiff 219；把这组复原后画面与基线
            // **MD5 完全相同** ⇒ 因果干净）。
            // HideInHierarchy 只改"编辑器叠加层画不画"：不改渲染、不动物理射线、不影响池复用
            // （**不用** HideAndDontSave —— 那会让对象在切场景时不被销毁，池的语义就变了）。
            go.hideFlags = HideFlags.HideInHierarchy;

            var light = go.AddComponent<Light>();
            light.type = LightType.Point;
            light.shadows = LightShadows.None;
            light.enabled = false;

            go.SetActive(false);
            return go;
        }

        /// <summary>把池里出来的物件绑成本类的表现状态（组件只在工厂里加一次，这里只取引用）。</summary>
        private EffectItem Bind(GameObject go, Shape shape)
        {
            var renderer = go.GetComponent<MeshRenderer>();
            var sprite = go.GetComponent<SpriteRenderer>();
            var filter = go.GetComponent<MeshFilter>();
            var light = go.GetComponent<Light>();

            Material owned = null;
            if (renderer != null)
            {
                // 本物件克隆的材质（贴片除外）：Unity 不随 GameObject 一起销毁它，见 Dispose。
                owned = renderer.material;
            }

            if (sprite != null)
            {
                sprite.enabled = false;
                // 记下贴片自带的材质，池复用回非贴花用途时复位成它（见 Acquire）。
                if (_spriteMat == null) _spriteMat = sprite.sharedMaterial;
            }

            return new EffectItem
            {
                Go = go,
                Tr = go.transform,
                Renderer = renderer,
                Sprite = sprite,
                Mesh = filter,
                Light = light,
                Shape = shape,
                OwnedMaterial = owned,
            };
        }

        /// <summary>把颜色落到该形状实际参与渲染的部件上（贴片染色 / 材质染色 / 点光颜色 / 烟团每一面）。</summary>
        private static void ApplyColor(EffectItem item, Color color)
        {
            if (item.Sprite != null)
            {
                item.Sprite.color = color;
            }
            else if (item.OwnedMaterial != null)
            {
                item.OwnedMaterial.color = color;
            }
            else if (item.Go != null)
            {
                var faces = item.Go.GetComponentsInChildren<SpriteRenderer>(true);
                for (var i = 0; i < faces.Length; i++) faces[i].color = color;
            }

            if (item.Light != null) item.Light.color = color;
        }

        /// <summary>
        /// 建一个烟团：三张绕 Y 轴交叉的软边贴片，每张一个 <see cref="SpriteRenderer"/>
        /// （<c>Sprites/Default</c>：alpha 混合、双面绘制），尺寸由 <see cref="RentSmoke"/> 的调用方逐帧控制。
        /// </summary>
        private GameObject CreateSmokeVisual(Color color)
        {
            var root = new GameObject("FX_Smoke");
            var sprite = GetSmokeSprite();

            for (var i = 0; i < SmokeCrossAngles.Length; i++)
            {
                var face = new GameObject("FX_SmokeFace");
                var renderer = face.AddComponent<SpriteRenderer>();
                renderer.sprite = sprite;
                renderer.color = color;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                // 贴图没造出来就整面不画（⛔ 不画成白块）。
                renderer.enabled = sprite != null;

                face.transform.SetParent(root.transform, false);
                face.transform.localRotation = Quaternion.Euler(0f, SmokeCrossAngles[i], 0f);
            }

            return root;
        }

        /// <summary>
        /// 烟团 sprite：把运行时生成的径向软边贴图包成 1 米见方（PPU = 贴图边长），
        /// 着色走 <see cref="SpriteRenderer"/> 自带的 <c>Sprites/Default</c>
        /// （⛔ 不用 <c>Shader.Find</c> —— 那有"打包后被剥掉、烟团整片消失"的静默风险）。
        /// </summary>
        private Sprite GetSmokeSprite()
        {
            if (_sprSmoke != null) return _sprSmoke;

            var tex = CreateSmokeTexture();
            // FullRect：贴图 Apply 时已 makeNoLongerReadable，Tight 网格会去读像素生成轮廓。
            _sprSmoke = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height),
                new Vector2(0.5f, 0.5f), SmokeTextureSize, 0, SpriteMeshType.FullRect);
            _sprSmoke.name = "FX_SmokeSoftEdge";
            return _sprSmoke;
        }

        /// <summary>
        /// 径向软边 alpha 贴图：半径 0.5 以内是实心核、之后平滑降到 0（白色 + alpha，染色由贴片颜色给）。
        /// 实心核是"烟团"与"一层薄雾"的差别 —— 只有软边渐变时，半透明叠色后中心也留不住密度。
        /// </summary>
        private static Texture2D CreateSmokeTexture()
        {
            var tex = new Texture2D(SmokeTextureSize, SmokeTextureSize, TextureFormat.RGBA32, false)
            {
                name = "FX_SmokeSoftEdge",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };

            var pixels = new Color32[SmokeTextureSize * SmokeTextureSize];
            for (var y = 0; y < SmokeTextureSize; y++)
            {
                for (var x = 0; x < SmokeTextureSize; x++)
                {
                    var dx = (x + 0.5f) / SmokeTextureSize * 2f - 1f;
                    var dy = (y + 0.5f) / SmokeTextureSize * 2f - 1f;
                    var r = Mathf.Sqrt(dx * dx + dy * dy);
                    // 0.5 半径内实心，0.5 → 1.0 平滑落到 0。
                    var a = Mathf.Clamp01((1f - r) / 0.5f);
                    a = a * a * (3f - 2f * a);
                    pixels[y * SmokeTextureSize + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            }

            tex.SetPixels32(pixels);
            tex.Apply(false, true);
            return tex;
        }

        private static void SetScale(EffectItem item, float scale)
        {
            if (item?.Tr == null) return;
            item.Tr.localScale = Vector3.one * Mathf.Max(0.001f, scale);
        }
    }
}
