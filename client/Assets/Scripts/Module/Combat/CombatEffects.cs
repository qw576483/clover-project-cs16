using System.Collections.Generic;
using CloverEngine;
using Cs16.Core;
using Cs16.Module.Player;
using UnityEngine;

namespace Cs16.Module.Combat
{
    /// <summary>
    /// 枪口火焰 / 弹道 / 弹痕 / 血迹 / 爆炸的**程序化**表现（不依赖预制体；贴图走 <c>Resources/UI/Art/fx_*</c>）。
    ///
    /// <para><b>2026-09-24 补（片 FX-MUZZLE，差异 #89）</b>：用户复查「枪口火焰没有效果」。
    /// 根因**不是"没做"**（这条链一直在跑），是落点/尺寸两处口径错：
    /// ① 落点旧值 `eye + dir*0.34 + right*0.13 + down*0.09` 把火焰放进**枪身内部**
    ///    （视模型动画后包围盒 z 到 0.705）⇒ 被近端枪身几何深度盖掉；
    /// ② 尺寸旧值 `Vector3.one * 0.30f` 把"米"当**倍率**写，64 px/PPU=100 的贴片实际只有 0.192 m。
    /// 现在：落点走 <see cref="CsCombatTuning.MuzzleOffsetForward"/> 等三个**相机局部系**分量，
    /// 尺寸走 <see cref="SpriteScaleForMeters"/>，并按武器选贴图（B51/M249 → 十字形
    /// <see cref="ResPaths.FxMuzzleFlashCross"/>）。判据资产 = <c>tools/probes/probe-muzzleflash.cs</c>
    /// + <c>.ai-tmp/drivers/bz-muzzle.sh</c>。</para>
    ///
    /// <para><b>2026-09-23 补（片 FX-ALL，差异 #69 + #74）</b>：
    /// ① **弹痕从 1 张变 5 张**（原版 `decals.wad` 的 `{shot1..5`，5 张全解出、每次命中随机取一，
    /// 见 <see cref="PickBulletHole"/>）；
    /// ② **新增血迹**（<see cref="BloodImpact"/>）：子弹命中角色时"命中点出血雾 + 在后面的面上贴
    /// `{blood1..6` 血迹贴花"，载体与名表出处写在该方法自己的注释里。
    /// 两张新族都由 `tools/probes/wad3-extract.py` 从**已在盘**的 `decals.wad` 解出
    /// （落盘台账：`.ai-tmp/test/fx-decal-variants.tsv`）⇒ ⛔ 不是程序化替身。</para>
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
            /// <summary>是不是"弹痕/血迹"这类贴片（它们有独立上限，满了复用最旧的一条）。</summary>
            public bool Decal;

            /// <summary>是不是**血迹**贴花（与墙上弹痕分开计数、分开上限，见 <see cref="AcquireDecal"/>）。</summary>
            public bool Blood;
        }

        private readonly CsModuleLog _log = new CsModuleLog("Combat");
        private readonly List<EffectItem> _items = new List<EffectItem>(128);
        private Transform _root;

        // ---- 贴图（异步加载；缺资源时只 Warn 一次并退化成"点光 + 无贴片"，⛔ 绝不退回黄色球）----
        private Sprite _sprFlash;
        /// <summary>枪口火焰**十字形**变体（原版 `sprites/muzzleflash3.spr` 帧 0）—— 差异 #89：B51/M249 用它。</summary>
        private Sprite _sprFlashCross;
        private Sprite _sprHole;
        private Sprite _sprSpark;
        /// <summary>弹痕**五变体**（原版 `decals.wad` 的 `{shot1..5`，key 表见 <see cref="ResPaths.FxBulletHoleKeys"/>）。</summary>
        private readonly Sprite[] _sprShots = new Sprite[ResPaths.FxBulletHoleVariants];
        /// <summary>血迹**六变体**（原版 `decals.wad` 的 `{blood1..6`，key 表见 <see cref="ResPaths.FxBloodKeys"/>）。</summary>
        private readonly Sprite[] _sprBlood = new Sprite[ResPaths.FxBloodVariants];
        private bool _spritesRequested;
        private bool _spriteWarned;
        private bool _bloodWarned;

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
            res.LoadAsset<Sprite>(ResPaths.FxMuzzleFlashCross, s => _sprFlashCross = s);
            res.LoadAsset<Sprite>(ResPaths.FxBulletHole, s => _sprHole = s);
            res.LoadAsset<Sprite>(ResPaths.FxSpark, s => _sprSpark = s);

            // 弹痕五变体：按 ResPaths 的**字面量 key 表**逐个加载（⛔ 不拼串：拼出来的 key
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
        }

        /// <summary>从一组贴图里**均匀随机**取一张非空的（原版是五变体随机；⛔ 不用"最旧的一张"之类的假随机）。</summary>
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
        /// <para><b>为什么不能直接写 <c>Vector3.one * meters</c>（片FX-ALL 2026-09-23 实测的坑）</b>：
        /// <c>localScale</c> 是**倍率**，不是米 —— 一张贴片"天生"多宽由它自己的导入 PPU
        /// （<c>spritePixelsToUnits</c>）决定。本工程的 <c>Resources/UI/Art/fx_*.png</c> 是 Unity
        /// 自动导入的默认值 <b>PPU=100</b>，所以 16x16 的弹痕天生只有 0.16 世界单位宽；那一行
        /// <c>Vector3.one * 0.075f</c> 实际画出的是 <b>1.2 cm</b>（本应 7.5 cm，小了 6.25 倍），
        /// 2 m 外不足 5 px ⇒ 截图与肉眼都"看不见"，表现上等于**没贴**。
        /// 用贴片自己的 <c>bounds</c> 反算成倍率后，<c>meters</c> 才真的是米，
        /// 且换任何 PPU / 任何像素宽的贴图都不会再错（不必依赖"meta 里的 PPU 别写错"这种共识 ——
        /// 重导一次就静默回退）。</para>
        ///
        /// <para>⛔ 本片只把两处**贴花**改走这里：枪口火焰 / 火星 / 血雾三处直接写
        /// <c>Vector3.one * &lt;米&gt;</c>，同一因子仍在（它们也被缩小了），但那三样是**已验收**的
        /// 成片观感，本片不动，只在差异 #69 的"尺寸映射"段登记。</para>
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
        /// <para><b>为什么不能恒定用 <c>Vector3.up</c>（片FX-ALL 2026-09-23 实测）</b>：
        /// <c>Quaternion.LookRotation(forward, up)</c> 要求 <c>up</c> 与 <c>forward</c> **不平行**。
        /// 命中**地面**时法线就是 (0,1,0)，<c>-normal</c> 与 <c>Vector3.up</c> 正好反向 ⇒
        /// 该四元数**退化**，Unity 只能保留一个非法旋转（"面向下 + 上向朝上"）⇒ 贴片**立起来**，
        /// 正对相机看就是一条线。表现上又是"没有弹痕"。
        /// 地板命中改用一个与法线不平行的参考上向（前向），四元数就良定义了。</para>
        ///
        /// <para>⛔ 三处贴片（弹痕 / 火星 / 血迹）都必须走这里 —— 只修一处等于把同一个坑
        /// 挪到另外两处等着复发（本片就是这么又捞出来两处的）。</para>
        /// </summary>
        private static Vector3 SurfaceUp(Vector3 normal)
        {
            return Mathf.Abs(Vector3.Dot(normal, Vector3.up)) > 0.9f ? Vector3.forward : Vector3.up;
        }


        /// <summary>
        /// 枪口火焰：**一张原版亮斑贴片**（面向相机）+ 一盏点光。
        /// 点光是第一人称里最有效的"开枪了"读感（墙与敌人会被照亮）。
        /// ⛔ 不许退回"一颗球"（用户报的就是它）。
        ///
        /// <para><b>2026-09-24 修（差异 #89，用户报「枪口火焰没有效果」）</b>：</para>
        /// <list type="number">
        /// <item><b>落点</b>从"相机前方 0.34 m"改成 <see cref="CsCombatTuning.MuzzleOffsetForward"/>
        /// （= 0.72 m）—— 旧值把火焰埋在**枪身内部**（视模型动画后包围盒 z 到 0.705），
        /// 被近端枪身几何在深度测试里盖掉。三个分量都在 <c>CsCombatTuning</c> 里逐条带推导。</item>
        /// <item><b>尺寸</b>改走 <see cref="SpriteScaleForMeters"/>：旧代码直接写
        /// <c>Vector3.one * 0.30f</c>，而贴片 PPU=100、64 px 天生宽 0.64 世界单位 ⇒ 实际只画出
        /// 0.192 m（小 3.1 倍）。同族坑见 <see cref="CsCombatTuning.DecalSize"/>。</item>
        /// <item><b>逐武器贴图</b>：原版按武器选 <c>muzzleflash1..4</c>（B51/M249 是**十字形**，
        /// 对应载体 <c>muzzleflash3.spr</c>）。映射出自引擎 <c>hw.dll</c>（不在盘）⇒ 只有
        /// <c>m249 → 十字</c> 这一条有证据（用户实机 + 载体形态唯一匹配，见
        /// <see cref="ResPaths.FxMuzzleFlashCross"/>），其余武器维持原贴图。</item>
        /// </list>
        /// </summary>
        /// <param name="eyePosition">视点（相机世界坐标）</param>
        /// <param name="viewRotation">相机世界旋转 —— 落点按**相机局部系**给（见 CsCombatTuning 的三个偏移），
        /// ⛔ 不再用 world-up 叉乘算 right/up：那在俯仰接近 ±90° 时会退化。</param>
        /// <param name="weaponId">当前武器 id（决定用哪张原版贴图；未知/为空走默认那张）</param>
        public void MuzzleFlash(Vector3 eyePosition, Quaternion viewRotation, string weaponId)
        {
            var pos = eyePosition + viewRotation * new Vector3(
                CsCombatTuning.MuzzleOffsetRight,
                CsCombatTuning.MuzzleOffsetUp,
                CsCombatTuning.MuzzleOffsetForward);

            var sprite = PickMuzzleFlash(weaponId);

            var item = AcquireSprite(Shape.Sprite, Color.white, CsCombatTuning.MuzzleFlashDuration);
            if (item == null) return;

            item.Tr.position = pos;
            item.Sprite.sprite = sprite;
            item.Sprite.enabled = sprite != null;
            item.Billboard = true;
            // ⛔ 必须按贴片自己的宽度反算倍率（见 SpriteScaleForMeters）：直接写米会小 1/PPU 倍。
            item.Tr.localScale = Vector3.one * SpriteScaleForMeters(sprite, CsCombatTuning.MuzzleFlashSize);
            if (sprite == null) WarnSpriteOnce($"枪口火焰贴图缺失：Resources/{ResPaths.FxMuzzleFlash}.png（只保留点光）");

            if (item.Light != null)
            {
                // 点光**要**开：原版枪口火焰同时照亮近处墙面，是第一人称里最主要的"开枪了"读感。
                item.Light.enabled = true;
                item.Light.color = FlashColor;
                item.Light.range = 8f;
                item.Light.intensity = 4f;
            }

            _log.Info("shot.muzzle",
                $"枪口火焰落在 {pos}（相机局部 {CsCombatTuning.MuzzleOffsetRight}/{CsCombatTuning.MuzzleOffsetUp}/" +
                $"{CsCombatTuning.MuzzleOffsetForward} m）→ 武器 {weaponId ?? "-"} 贴图 " +
                $"{(sprite != null ? sprite.name : "null")} 启用={item.Sprite.enabled} " +
                $"世界宽={CsCombatTuning.MuzzleFlashSize:F3}m 缩放={item.Tr.localScale.x:F3}");
        }

        /// <summary>
        /// 逐武器选枪口火焰贴图。原版按武器类别在**引擎**里选 <c>muzzleflash1..4</c>
        /// （四张载体在盘、但选择表在 `hw.dll`，不在盘）⇒ 只有一条映射有证据：
        /// <b>B51 / M249 用十字形</b>（用户 2026-09-24 实机记忆 + 四张载体里**只有**
        /// <c>muzzleflash3.spr</c> 是十字/X 形，唯一匹配）。其余武器维持默认那张
        /// （<see cref="ResPaths.FxMuzzleFlash"/> = <c>muzzleflash2.spr</c> 帧 0，旧版即如此）。
        /// ⛔ 不许凭"哪张好看"给别的武器编映射 —— 缺口登记在 `策划/差异登记.tsv` #89。
        /// </summary>
        private Sprite PickMuzzleFlash(string weaponId)
        {
            if (weaponId == CsWeapons.M249 && _sprFlashCross != null) return _sprFlashCross;
            return _sprFlash;
        }

        /// <summary>
        /// 命中墙：**弹痕**（五变体随机、按命中面法线贴上去的贴片）+ 一记小火星。
        /// <paramref name="normal"/> 必须是世界法线（<c>RaycastHit.normal</c>）。
        /// </summary>
        public void BulletImpact(Vector3 point, Vector3 normal)
        {
            if (normal.sqrMagnitude < 0.0001f) normal = Vector3.up;
            normal.Normalize();

            // ---- 弹痕：贴面 + 沿法线抬起 1cm（防 z-fighting），尺寸按原版 decal 的观感（~7cm）----
            var sprite = PickBulletHole();
            var decal = AcquireDecal(blood: false);
            if (decal != null)
            {
                decal.Tr.position = point + normal * 0.01f;
                decal.Tr.rotation = Quaternion.LookRotation(-normal, SurfaceUp(normal));
                // 尺寸 = 7.5 cm（口径见 CsCombatTuning）—— 必须按贴片自己的宽度反算倍率，
                // ⛔ 不能直接写 `Vector3.one * DecalSize`：那等于把 7.5 cm 画成 1.2 cm（见 SpriteScaleForMeters）。
                var scale = SpriteScaleForMeters(sprite, CsCombatTuning.DecalSize);
                decal.Tr.localScale = Vector3.one * scale;
                decal.Sprite.sprite = sprite;
                decal.Sprite.enabled = sprite != null;
                decal.Billboard = false;

                // 可核对日志（字段是照着"看不见弹痕"的三个岔口设计的，见文件头）：
                // 贴图=null / 启用=False ⇒ 贴图没加载到；世界宽 不是 0.128 ⇒ 尺寸口径坏了。
                // 片FIX-4（2026-09-24，用户第 4 次报「弹痕还是没有」）：再补两个**可见性**字段 ——
                // 「可见核心宽」= 整块 × 载体实测的核心占比（alpha≥160 只有 2~4 px / 256）——
                // 并给出它在**当前这一发的实际距离**上的屏幕投影像素数。
                // 判据 = 「可见核心投影 ≥ 6 px 且出现在画面内」，肉眼看不到时第一件事是核这一对数
                // （不是去猜"贴没贴"）。距离用相机（弹痕的观察者恒是本地相机）。
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
            spark.Tr.localScale = Vector3.one * CsCombatTuning.SparkSize;
            spark.Sprite.sprite = _sprSpark;
            spark.Sprite.enabled = _sprSpark != null;
            spark.Billboard = true;
        }

        /// <summary>
        /// **子弹打中角色**：命中点上一小团血雾 + 在**后面的那个面**上贴一张血迹贴花。
        ///
        /// <para><b>口径（差异 #74，片 FX-ALL 2026-09-23）</b>：原版受击的载体证据两层 ——
        /// ① 贴花载体 `<c>decals.wad</c>` 里有 `{blood1..6`（红）与 `{yblood1..6`（黄，`violence_ablood`），
        /// 在 `mp.dll` 的贴花名表里是连续两项（索引 13..18 / 19..24）；
        /// ② `mp.dll` 里另有 `sprites/bloodspray.spr`、`sprites/blood.spr` 两个串 —— **这两个 `.spr` 不在盘**。
        /// 所以：**血迹贴花用真载体**（六变体随机），**血雾只能用替身**（缺载体，登记在 `client/资源欠缺清单.md`）。</para>
        ///
        /// <para><b>为什么从命中点再往前打一条射线</b>：原版是"在角色**后面的墙**上贴血迹"，
        /// 不是贴在角色身上（贴角色身上会随它动，原版没有这种表现）。
        /// 追不到面时**只出血雾、不贴贴花**（不硬塞到空气里）。</para>
        /// </summary>
        /// <param name="point">命中点（世界坐标，来自射线）</param>
        /// <param name="direction">弹道方向（单位向量，从射手指向命中点）</param>
        /// <param name="headshot">是否爆头（目前只影响日志口径；⛔ 不做"爆头喷更多血"这类无出处的放大）</param>
        /// <returns>是否真的落了血迹贴花（供自检 / 日志用）</returns>
        public bool BloodImpact(Vector3 point, Vector3 direction, bool headshot)
        {
            Init();

            var dir = direction.sqrMagnitude > 0.0001f ? direction.normalized : Vector3.forward;

            // ---- ① 血雾：命中点上一小团（`sprites/bloodspray.spr` 的降级替身，用真血迹贴图染色）----
            var puff = AcquireSprite(Shape.Sprite, Color.white, CsCombatTuning.BloodPuffDuration);
            if (puff != null)
            {
                puff.Tr.position = point;
                puff.Tr.localScale = Vector3.one * CsCombatTuning.BloodPuffSize;
                puff.Sprite.sprite = PickRandom(_sprBlood);
                puff.Sprite.enabled = puff.Sprite.sprite != null;
                puff.Billboard = true;
            }

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
            // 尺寸按**这张贴花自己的像素宽**反算（48 px -> 0.225 m、64 px -> 0.30 m；口径见 CsCombatTuning），
            // 再按**贴片天生宽度**换成 localScale 倍率（⛔ 同上的坑：直接写米会小 1/PPU 倍）。
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
                "（受击只出无贴图的雾点）—— 用 tools/probes/wad3-extract.py 从 decals.wad 解出后重跑");
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
            target.Blood = false;
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
