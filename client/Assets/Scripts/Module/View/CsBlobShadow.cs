using UnityEngine;
using UnityEngine.Rendering;

namespace Cs16.Module.View
{
    /// <summary>
    /// 角色脚下一块**贴地的圆形软边圆影**（blob shadow）。
    ///
    /// <para><b>形态</b>：一个两三角面的水平 quad（<see cref="SpriteRenderer"/> 的网格本身就是 4 顶点 2 三角），
    /// 半径为 <see cref="CsViewTuning.BlobShadowRadius"/>（= 玩家碰撞半径 0.36 m），纯黑 ×
    /// <see cref="CsViewTuning.BlobShadowAlpha"/>，随角色根节点移动。</para>
    ///
    /// <para><b>贴图来源</b>：原版载体里没有玩家投影的 spr
    /// （<c>原版资源/cs16src/valve/sprites</c> 只有 blood / blooddrop / bloodspray，
    /// <c>cstrike/sprites</c> 只有 HUD / 枪口焰 / 雷达 / 狙击镜，全目录 grep 文件名含 shadow 命中 0）
    /// ⇒ 圆斑（黑色 + 二次曲线软边）在这里**程序化生成一次**，全进程共用一张 64×64 贴图，
    /// 不落盘、不进 <c>Resources</c>。</para>
    ///
    /// <para><b>生命周期</b>：每个 <see cref="ActorView"/> 一份，作为角色根节点的子节点建**一次**；
    /// 角色视图进对象池复用时节点跟着走（不销毁、不重建），只复位透明度与尺寸。
    /// 渲染体不投不接收实时阴影（它只负责"接地"的读感）。</para>
    /// </summary>
    internal sealed class CsBlobShadow
    {
        /// <summary>子节点名（生成后固定，便于在层级里认出来）。</summary>
        public const string NodeName = "BlobShadow";

        /// <summary>程序化贴图的边长（像素）。64 足够让 0.72 m 直径在近处也不出锯齿。</summary>
        private const int TexSize = 64;

        /// <summary>全进程共用的圆斑贴图（首次创建时生成，之后一直复用）。</summary>
        private static Sprite _sharedSprite;

        private Transform _transform;
        private SpriteRenderer _renderer;
        private float _alpha;
        private float _scale = 1f;
        private bool _visible = true;

        /// <summary>已经建出节点（池复用的实例为 true，不会再建第二份）。</summary>
        public bool IsCreated => _renderer != null;

        /// <summary>圆影节点的 Transform（未建时为 <c>null</c>）。角色视图量身高 / 藏渲染时要把它排除在外。</summary>
        public Transform Root => _transform;

        /// <summary>当前透明度（0 = 完全看不见）；供取证 / 排障读取。</summary>
        public float Alpha => _alpha;

        /// <summary>把圆影挂到角色根节点下。**幂等**：已经建过就什么都不做。</summary>
        /// <param name="root">角色视图根节点（<see cref="ActorView"/> 所在的 GameObject）</param>
        public void Ensure(Transform root)
        {
            if (_renderer != null || root == null) return;

            var sprite = SharedSprite();
            if (sprite == null)
            {
                // 非预期分支：贴图生成失败 ⇒ 这具视图没有脚底圆影（留痕由调用方记）。
                return;
            }

            var go = new GameObject(NodeName);
            go.layer = root.gameObject.layer;
            _transform = go.transform;
            _transform.SetParent(root, false);
            // Sprite 的正面对着局部 +Z；转到 +Y 朝上 = 平铺在地面。
            _transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);
            _transform.localPosition = new Vector3(0f, CsViewTuning.BlobShadowLift, 0f);
            _transform.localScale = Vector3.one * (CsViewTuning.BlobShadowRadius * 2f);

            _renderer = go.AddComponent<SpriteRenderer>();
            _renderer.sprite = sprite;
            _renderer.color = new Color(0f, 0f, 0f, 0f);
            _renderer.shadowCastingMode = ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
            _renderer.lightProbeUsage = LightProbeUsage.Off;
            _renderer.enabled = false;
            _alpha = 0f;
            _scale = 1f;
        }

        /// <summary>
        /// 每帧摆位与淡入淡出。
        /// </summary>
        /// <param name="rootPosition">角色根节点世界坐标（脚底位置）</param>
        /// <param name="groundY">影子贴的那一层地面的世界 y（空中时由调用方给"起跳落点"的高度）</param>
        /// <param name="onGround">角色是否着地</param>
        /// <param name="dt">帧时长（秒）</param>
        public void Tick(Vector3 rootPosition, float groundY, bool onGround, float dt)
        {
            if (_renderer == null) return;

            var wantAlpha = (onGround && _visible) ? CsViewTuning.BlobShadowAlpha : 0f;
            var wantScale = (onGround || !_visible) ? 1f : CsViewTuning.BlobShadowAirScale;
            var t = dt <= 0f
                ? 1f
                : 1f - Mathf.Exp(-dt / Mathf.Max(0.0001f, CsViewTuning.BlobShadowBlendTau));

            _alpha = Mathf.Lerp(_alpha, wantAlpha, t);
            _scale = Mathf.Lerp(_scale, wantScale, t);

            if (_transform != null)
            {
                _transform.position = new Vector3(rootPosition.x, groundY + CsViewTuning.BlobShadowLift, rootPosition.z);
                _transform.localScale = Vector3.one * (CsViewTuning.BlobShadowRadius * 2f * _scale);
            }

            _renderer.color = new Color(0f, 0f, 0f, _alpha);
            var show = _alpha > 0.004f;
            if (_renderer.enabled != show) _renderer.enabled = show;
        }

        /// <summary>
        /// 显示 / 隐藏（死亡、相机进入本角色眼位时整块收掉）。隐藏是**立即**的：
        /// 尸体与第一人称本体都不该留一块悬空的黑斑。
        /// </summary>
        public void SetVisible(bool visible)
        {
            _visible = visible;
            if (visible) return;

            _alpha = 0f;
            _scale = 1f;
            if (_renderer == null) return;
            _renderer.color = new Color(0f, 0f, 0f, 0f);
            _renderer.enabled = false;
        }

        /// <summary>对象池复用前的复位：下次由 <see cref="ActorView"/> 重新打开。</summary>
        public void ResetForReuse()
        {
            _alpha = 0f;
            _scale = 1f;
            SetVisible(false);
        }

        /// <summary>
        /// 圆形软边贴图：中心不透明、半径 1 处收到 0（<c>alpha = (1 - r)²</c>）。
        /// 一张 64×64 的 <c>RGBA32</c>，创建后不再需要 CPU 侧读回。
        /// </summary>
        private static Sprite SharedSprite()
        {
            if (_sharedSprite != null) return _sharedSprite;

            var tex = new Texture2D(TexSize, TexSize, TextureFormat.RGBA32, false)
            {
                name = "CS16_BlobShadow",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.DontSave
            };

            var pixels = new Color32[TexSize * TexSize];
            var half = TexSize * 0.5f;
            for (var y = 0; y < TexSize; y++)
            {
                for (var x = 0; x < TexSize; x++)
                {
                    var dx = (x + 0.5f - half) / half;
                    var dy = (y + 0.5f - half) / half;
                    var r = Mathf.Sqrt(dx * dx + dy * dy);
                    var a = r >= 1f ? 0f : (1f - r) * (1f - r);
                    pixels[y * TexSize + x] = new Color32(0, 0, 0, (byte)Mathf.RoundToInt(a * 255f));
                }
            }

            tex.SetPixels32(pixels);
            tex.Apply(false, true);

            // pixelsPerUnit = 贴图边长 ⇒ 一张贴图的世界尺寸正好 1×1，缩放直接按"直径米数"给。
            _sharedSprite = Sprite.Create(tex, new Rect(0f, 0f, TexSize, TexSize), new Vector2(0.5f, 0.5f), TexSize);
            _sharedSprite.name = "CS16_BlobShadow";
            return _sharedSprite;
        }
    }
}
