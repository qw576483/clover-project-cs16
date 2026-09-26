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
    /// 血雾 ← <c>valve/sprites/bloodspray.spr</c>（64×64×10 帧）；只有 <c>fx_spark</c> 一张是本项目生成
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
    /// <para><b>为什么不用引擎对象池（<c>Game.Pool</c>）</b>：引擎的对象池是"按预制体 key 实例化"
    /// （内部走 <c>Resources.Load&lt;GameObject&gt;(key)</c>），而本模块的产出路径里没有、也不该有预制体
    /// （<c>Resources/UI/{类名}.prefab</c> 归 UI 生成器写）。key 缺失时引擎会**每次调用打一条 Error**，
    /// 高频路径上等于刷屏；所以这里自带一个极小特效池（<c>SetActive</c> 复用），语义与对象池一致。</para>
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

        /// <summary>池中同类特效的软上限（超出即复用最久的一个，不会无限增长）。</summary>
        private const int SoftPoolLimit = 256;

        private enum Shape
        {
            Sphere = 0,
            Cube = 1,
            Sprite = 2,
            /// <summary>自带网格的实体（弹壳）：它的几何来自 <see cref="CsShellModels"/>，不是程序化建形状。</summary>
            Mesh = 3,
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

            /// <summary>是不是飞行中的弹壳（每帧积分速度/自旋，撞到世界面反弹，见 <see cref="TickShell"/>）。</summary>
            public bool Shell;
            /// <summary>弹壳速度（米/秒）。</summary>
            public Vector3 Velocity;
            /// <summary>弹壳自旋（度/秒，绕本地三轴）。</summary>
            public Vector3 Spin;
        }

        private readonly CsModuleLog _log = new CsModuleLog("Combat");
        private readonly List<EffectItem> _items = new List<EffectItem>(128);
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
        /// <summary>弹痕**五变体**（原版 `decals.wad` 的 `{shot1..5`，key 表见 <see cref="ResPaths.FxBulletHoleKeys"/>）。</summary>
        private readonly Sprite[] _sprShots = new Sprite[ResPaths.FxBulletHoleVariants];
        /// <summary>血迹**六变体**（原版 `decals.wad` 的 `{blood1..6`，key 表见 <see cref="ResPaths.FxBloodKeys"/>）。</summary>
        private readonly Sprite[] _sprBlood = new Sprite[ResPaths.FxBloodVariants];
        /// <summary>血雾**十帧**（原版 `valve/sprites/bloodspray.spr`，key 表见 <see cref="ResPaths.FxBloodSprayKeys"/>）。</summary>
        private readonly Sprite[] _sprBloodSpray = new Sprite[ResPaths.FxBloodSprayVariants];
        private bool _spritesRequested;
        private bool _spriteWarned;
        private bool _bloodWarned;
        private bool _bloodSprayWarned;

        /// <summary>
        /// 墙上的贴花（弹痕 / 血迹）共用的材质：由 <see cref="ResPaths.DecalAlphaClipShader"/> 现造一次，
        /// <c>_Cutoff</c> = <see cref="CsCombatTuning.DecalAlphaCutoff"/>。着色器取不到时为 null
        /// ⇒ 贴花退回 <c>Sprites/Default</c>（不裁切、软晕也画出来）。
        /// </summary>
        private Material _decalMat;
        private bool _decalMatWarned;

        /// <summary>
        /// 贴片"自带"的那份材质（<c>AddComponent&lt;SpriteRenderer&gt;</c> 时 Unity 给的默认件，
        /// 名为 <c>Sprites-Default</c> / 着色器 <c>Sprites/Default</c>）：贴片每次从池里借出都复位成它。
        /// </summary>
        private Material _spriteMat;

        /// <summary>本发血雾用到的帧名（复用，只喂 <c>blood.spray</c> 日志，避免每次命中产生垃圾）。</summary>
        private readonly List<string> _puffFrames = new List<string>(CsCombatTuning.BloodSprayMinCount);

        // ---- 颜色 ----
        /// <summary>枪口点光色（原版 spr 是暖白偏黄，照亮近处墙面）。</summary>
        private static readonly Color FlashColor = new Color(1f, 0.93f, 0.62f, 1f);

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

            // 贴花材质是本类现造的 ⇒ 随本实例销毁；同时把"贴图已请求"的标记复位，让同一个实例
            // 被再次 Init 时会重新请求（否则 _decalMat 会永远停在 null，弹痕再也不会裁切）。
            if (_decalMat != null) Object.Destroy(_decalMat);
            _decalMat = null;
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

            // 血雾十帧（原版 bloodspray.spr）：同上，走字面量表。
            for (var i = 0; i < ResPaths.FxBloodSprayKeys.Length; i++)
            {
                var slot = i;
                res.LoadAsset<Sprite>(ResPaths.FxBloodSprayKeys[i], s => _sprBloodSpray[slot] = s);
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

        /// <summary>弹痕贴图：五变体随机；变体一张都没加载到 → 退到程序化替身 <see cref="ResPaths.FxBulletHole"/>。</summary>
        private Sprite PickBulletHole()
        {
            var s = PickRandom(_sprShots);
            if (s != null) return s;
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
            var radial = CsCombatTuning.MuzzleLateral(weaponId);
            var vertical = CsCombatTuning.MuzzleVertical(weaponId);
            var forward = CsCombatTuning.MuzzleForward(weaponId);
            var sizeMeters = CsCombatTuning.MuzzleFlashSize(weaponId);
            var duration = CsCombatTuning.MuzzleFlashDuration(weaponId);

            var pos = eyePosition + viewRotation * new Vector3(radial, vertical, forward);

            var sprite = PickMuzzleFlash(weaponId);

            var item = AcquireSprite(Shape.Sprite, Color.white, duration);
            if (item == null) return;

            item.Tr.position = pos;
            item.Sprite.sprite = sprite;
            item.Sprite.enabled = sprite != null;
            item.Billboard = true;
            // 必须按贴片自己的宽度反算倍率（见 SpriteScaleForMeters）：直接写米会小 1/PPU 倍。
            item.Tr.localScale = Vector3.one * SpriteScaleForMeters(sprite, sizeMeters);
            if (sprite == null) WarnSpriteOnce($"枪口火焰贴图缺失：Resources/{ResPaths.FxMuzzleFlash}.png（只保留点光）");

            if (item.Light != null)
            {
                // 点光**要**开：原版枪口火焰同时照亮近处墙面，是第一人称里最主要的"开枪了"读感。
                item.Light.enabled = true;
                item.Light.color = FlashColor;
                item.Light.range = 8f;
                item.Light.intensity = 4f;
            }

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
        /// 命中墙：**弹痕**（五变体随机、按命中面法线贴上去的贴片）+ 一记小火星。
        /// <paramref name="normal"/> 必须是世界法线（<c>RaycastHit.normal</c>）。
        /// </summary>
        public void BulletImpact(Vector3 point, Vector3 normal)
        {
            if (normal.sqrMagnitude < 0.0001f) normal = Vector3.up;
            normal.Normalize();

            // ---- 弹痕：贴面 + 沿法线抬起 1cm（防 z-fighting），尺寸 = 原版 16 单位（见 CsCombatTuning.DecalSize）----
            var sprite = PickBulletHole();
            var decal = AcquireDecal(blood: false);
            if (decal != null)
            {
                decal.Tr.position = point + normal * 0.01f;
                decal.Tr.rotation = Quaternion.LookRotation(-normal, SurfaceUp(normal));
                // 尺寸 = CsCombatTuning.DecalSize（整块 0.4064 m）—— 必须按贴片自己的宽度反算倍率，
                // 不能直接写 `Vector3.one * DecalSize`：那少乘 1/PPU 倍（见 SpriteScaleForMeters）。
                var scale = SpriteScaleForMeters(sprite, CsCombatTuning.DecalSize);
                decal.Tr.localScale = Vector3.one * scale;
                decal.Sprite.sprite = sprite;
                decal.Sprite.enabled = sprite != null;
                decal.Billboard = false;
                // 裁掉软晕、只画硬核（原版贴花绘制时开 GL_ALPHA_TEST + gl_alphamin=0.25）。
                ApplyDecalMaterial(decal.Sprite);

                // 可核对日志：贴图=null / 启用=False ⇒ 贴图没加载到；世界宽 不是 0.406 ⇒ 尺寸口径坏了。
                // 「可见核心宽」= 整块 × 载体实测的核心占比（alpha≥32 的墨迹水平跨度 4~5 texel / 16）——
                // 并给出它在**当前这一发的实际距离**上的屏幕投影像素数
                // （口径：0.127 m 的墨迹在 5 m 处约 24 px）。距离用相机（弹痕的观察者恒是本地相机）。
                var cam = Camera.main;
                var dist = cam != null ? Vector3.Distance(cam.transform.position, point) : 0f;
                var coreM = CsCombatTuning.DecalVisibleCoreMeters;
                var corePx = dist > 0.0001f ? coreM * CsCombatTuning.ScreenPixelsPerMeter(dist) : 0f;

                _log.Info("shot.decal",
                    $"弹痕落在 {point}（法线 {normal}）→ 贴图 {(sprite != null ? sprite.name : "null")}" +
                    $" 启用={decal.Sprite.enabled} 世界宽={CsCombatTuning.DecalSize:F3}m" +
                    $" 缩放={scale:F3} 变体数={ResPaths.FxBulletHoleVariants}" +
                    $" 可见核心宽={coreM:F4}m（占比 {CsCombatTuning.DecalOpaqueCoreRatio:F2}）" +
                    $" 本发距离={dist:F2}m ⇒ 核心投影 {corePx:F1}px");
            }
            else
            {
                // 取不到物件 = 特效池满（Create 返回 null）⇒ 这一发**静默不留弹痕**，必须留痕。
                _log.Warn("shot.decal.null", $"弹痕物件取不到（特效池满？）：本发落在 {point} 不留痕");
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
        /// **子弹打中角色**：命中点上一团血雾 + 在**后面的那个面**上贴一张血迹贴花。
        ///
        /// <para><b>两层都走原版载体</b>（两者是同一条原版消息里的两个模型索引）：
        /// ① 血雾 = <c>valve/sprites/bloodspray.spr</c> 的十帧（<see cref="ResPaths.FxBloodSprayKeys"/>）；
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
        /// <param name="headshot">是否爆头（目前只影响日志口径；不做"爆头喷更多血"这类无出处的放大）</param>
        /// <returns>是否真的落了血迹贴花（供自检 / 日志用）</returns>
        public bool BloodImpact(Vector3 point, Vector3 direction, bool headshot)
        {
            Init();

            var dir = direction.sqrMagnitude > 0.0001f ? direction.normalized : Vector3.forward;

            // ---- ① 血雾：命中点上一小团，**原版载体**的十帧里随机取一（每张各取一次）----
            // 张数取原版公式 clamp(amount/10, 3, 16) 的**下界**（自变量 amount 在受击者实体上，
            // 表现层拿不到伤害 ⇒ 只能取下界；出处与缺口见 CsCombatTuning.BloodSprayMinCount）。
            WarnBloodSprayOnce();
            var sprayRng = CsRng.Stream(CsRngStream.FxVariant);
            _puffFrames.Clear();
            for (var i = 0; i < CsCombatTuning.BloodSprayMinCount; i++)
            {
                // 染色走池子的 key（同色才复用），不在取到物件后再改色 —— 否则会把同池的白贴片一起染红。
                var puff = AcquireSprite(Shape.Sprite, BloodSprayTint, CsCombatTuning.BloodPuffDuration);
                if (puff == null) break;

                var frame = PickRandom(_sprBloodSpray);
                var spread = CsCombatTuning.BloodPuffSpread;
                puff.Tr.position = point + new Vector3(
                    sprayRng.Range(-spread, spread),
                    sprayRng.Range(-spread, spread),
                    sprayRng.Range(-spread, spread));
                puff.Tr.localScale = Vector3.one * SpriteScaleForMeters(frame, CsCombatTuning.BloodPuffSize);
                puff.Sprite.sprite = frame;
                puff.Sprite.enabled = frame != null;
                puff.Billboard = true;
                _puffFrames.Add(frame != null ? frame.name : "null");
            }

            // 可核对日志：张数 = clamp 下界；帧名逐个列出（看"是不是真的十帧都在用"）；
            // 世界宽从 CsCombatTuning.BloodPuffSize（米）经 SpriteScaleForMeters 反算。
            _log.Info("blood.spray",
                $"命中 {point}（爆头={headshot}）→ 血雾 {_puffFrames.Count} 张" +
                $"[{string.Join(",", _puffFrames)}] 世界宽={CsCombatTuning.BloodPuffSize:F3}m" +
                $"（载体帧数={ResPaths.FxBloodSprayVariants}）");

            // ---- ② 血迹贴花：从命中点继续向前找"后面的面" ----
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

            var px = spr != null ? (int)spr.rect.width : 48;
        // 尺寸按**这张贴花自己的像素宽**反算（48 px -> 1.219 m、64 px -> 1.626 m；口径见 CsCombatTuning）。
            decal.Tr.localScale = Vector3.one * SpriteScaleForMeters(spr, CsCombatTuning.BloodDecalSize(px));

            _log.Info("blood.decal",
                $"命中 {point}（爆头={headshot}）→ 血贴在 {hit.point}（面 {hit.collider?.name}，" +
                $"贴图 {(spr != null ? spr.name : "null")} {px}px → {CsCombatTuning.BloodDecalSize(px):F3}m）");
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

        /// <summary>血雾十帧一张都没加载到时只 Warn 一次（高频路径，绝不每帧刷屏）。</summary>
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

                if (item.Shell) TickShell(item, dt);

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
            target.Blood = false;
            target.Shell = false;
            target.Velocity = Vector3.zero;
            target.Spin = Vector3.zero;
            // 贴片池是按"形状 + 颜色"复用的（火焰 / 火星 / 弹痕同池）⇒ 每次借出都先把材质复位，
            // 否则一枚用过的弹痕被复用成枪口火焰时会带着弹痕的裁切材质（火焰软晕被切掉一片）。
            if (target.Sprite != null && _spriteMat != null) target.Sprite.sharedMaterial = _spriteMat;
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
            MeshFilter filter = null;

            if (shape == Shape.Sprite)
            {
                go = new GameObject("FX_Sprite");      // 贴片走 SpriteRenderer：没有碰撞体、自带 Sprites/Default
                sprite = go.AddComponent<SpriteRenderer>();
                sprite.color = color;
                sprite.enabled = false;                // 贴图到位前不显示（绝不画成白块）
                // 记下贴片自带的材质，池复用回非贴花用途时复位成它（见 Acquire）。
                if (_spriteMat == null) _spriteMat = sprite.sharedMaterial;
            }
            else if (shape == Shape.Mesh)
            {
                // 弹壳：几何来自 CsShellModels（原版 mdl 逐点搬运），网格由调用方每次换上。
                go = new GameObject("FX_Shell");
                filter = go.AddComponent<MeshFilter>();
                renderer = go.AddComponent<MeshRenderer>();
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
            light.color = color;
            light.enabled = false;

            var item = new EffectItem
            {
                Go = go,
                Tr = go.transform,
                Renderer = renderer,
                Sprite = sprite,
                Mesh = filter,
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
