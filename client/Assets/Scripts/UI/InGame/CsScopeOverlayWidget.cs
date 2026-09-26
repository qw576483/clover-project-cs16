using CloverEngine;
using UnityEngine;
using UnityEngine.UI;

namespace Cs16.UI
{
    /// <summary>
    /// 狙击镜遮罩（AWP / Scout / SG550 / G3SG1 开镜时压在画面上的黑底 + 镜筒 + 十字线）。
    ///
    /// <para><b>素材 = 原版自己的角弧 <c>cstrike/sprites/scope_arc*.tga</c></b>（每张 256×256）：
    /// 每张的透明区 = 以**内角**为圆心、半径 = 整张边长的四分之一圆 —— 于是把四张按"内角落在屏幕中心"
    /// 摆好，拼出的开孔是一个椭圆；原版客户端加载这四张的字符串在 <c>mp.dll</c> 的字面量区
    /// （<c>sprites/scope_arc.tga</c> / <c>_nw</c> / <c>_ne</c> / <c>_sw</c>，见
    /// <c>.ai-tmp/test/mp-strings.txt</c> 的 001100F0 / 00110108 / 00110124 / 00110140）。</para>
    ///
    /// <para><b>盘上只有 3 张</b>：<c>scope_arc</c>（不透明区在右下 ⇒ 贴右下角）、<c>_nw</c>、<c>_ne</c>。
    /// 右下角那张**水平镜像**即左下角（四张弧本来就是镜像关系）⇒ <c>_sw</c> 用镜像顶替，
    /// 不为它造一张假素材。</para>
    ///
    /// <para><b>开孔尺寸与十字线的来源 = 一张原版开镜实机原图</b>：
    /// <c>原版资源/参照物/viewmodel/viewmodel_awp_scoped.png</c>（640×360）逐像素量得
    /// 开孔半轴 <b>240×135 px</b>（= <see cref="OpenHalfWidth"/> / <see cref="OpenHalfHeight"/> × 屏幕尺寸）、
    /// 遮罩处像素为纯黑 <c>(0,0,0)</c>、十字线 = 把背景压暗约 0.64~0.77 倍的细线
    /// （≈ <see cref="CrossAlpha"/>，2 px @640 宽）。量法：
    /// <c>.ai-tmp/test/anim-weapon2/measure-scope4.py</c> 与 <c>measure-cross.py</c>。</para>
    ///
    /// <para><b>它必须压在 3D 画面之上、整组 HUD 之下</b>：原版开镜时金钱/血量/弹药仍在黑底之上，
    /// 所以本控件由调用方塞到面板根节点的第一个子节点（见 <c>HudPanel</c> 的刷新路径）。</para>
    /// </summary>
    public sealed class CsScopeOverlayWidget
    {
        private const string Tag = "UI";

        /// <summary>镜筒弧素材在 <c>Resources</c> 下的目录（真实文件 <c>Resources/UI/Scope/scope_arc*.tga</c>）。</summary>
        private const string ArcDir = "UI/Scope/";

        /// <summary>不透明区在**右下**、透明区朝左上的那张（⇒ 贴右下角；左下角用它的水平镜像）。</summary>
        private const string ArcKeySe = "scope_arc";

        /// <summary>不透明区在左上、透明区朝右下（⇒ 贴左上角）。</summary>
        private const string ArcKeyNw = "scope_arc_nw";

        /// <summary>不透明区在右上、透明区朝左下（⇒ 贴右上角）。</summary>
        private const string ArcKeyNe = "scope_arc_ne";

        /// <summary>开孔椭圆半宽（× 屏幕宽）。出处见类注释：原版开镜实机原图量得 240 / 640。</summary>
        private const float OpenHalfWidth = 0.375f;

        /// <summary>开孔椭圆半高（× 屏幕高）。出处同 <see cref="OpenHalfWidth"/>：135 / 360。</summary>
        private const float OpenHalfHeight = 0.375f;

        /// <summary>十字线厚度（× 屏幕宽）。出处：原版开镜实机原图的线宽 2 px @640 宽。</summary>
        private const float CrossThickness = 2f / 640f;

        /// <summary>十字线颜色：原版把背景压暗约 0.64~0.77 倍（实机原图两处比值）⇒ 等价于 30% 黑。</summary>
        private const float CrossAlpha = 0.30f;

        private RectTransform _root;
        private bool _built;
        private bool _spriteWarned;

        /// <summary>遮罩根节点（调用方用它把整块塞到 HUD 之下：<c>SetAsFirstSibling</c>）。</summary>
        public RectTransform Root => _root;

        /// <summary>建节点（幂等）。<paramref name="parent"/> = HUD 面板根节点。</summary>
        public void Build(RectTransform parent)
        {
            if (_built || parent == null) return;
            _built = true;

            _root = UIFactory.CreateNode("ScopeOverlay", parent);
            UIFactory.Stretch(_root);
            // 未开镜不显示；**不是**没有 sprite 就先画白块（无 sprite 的 Image 会被 uGUI 画成实心白）。
            _root.gameObject.SetActive(false);

            // ---- ① 开孔以外的纯黑：开孔是中间那个 0.75×0.75 的盒子内的椭圆，盒外四条带铺黑 ----
            AddBand("ScopeBandTop", new Vector2(0f, 0.5f + OpenHalfHeight), new Vector2(1f, 1f));
            AddBand("ScopeBandBottom", new Vector2(0f, 0f), new Vector2(1f, 0.5f - OpenHalfHeight));
            AddBand("ScopeBandLeft",
                new Vector2(0f, 0.5f - OpenHalfHeight), new Vector2(0.5f - OpenHalfWidth, 0.5f + OpenHalfHeight));
            AddBand("ScopeBandRight",
                new Vector2(0.5f + OpenHalfWidth, 0.5f - OpenHalfHeight), new Vector2(1f, 0.5f + OpenHalfHeight));

            // ---- ② 四角的镜筒弧（把开孔盒的四个角切圆）----
            AddArc("ScopeArcNW", ArcKeyNw,
                new Vector2(0.5f - OpenHalfWidth, 0.5f), new Vector2(0.5f, 0.5f + OpenHalfHeight), false);
            AddArc("ScopeArcNE", ArcKeyNe,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f + OpenHalfWidth, 0.5f + OpenHalfHeight), false);
            AddArc("ScopeArcSE", ArcKeySe,
                new Vector2(0.5f, 0.5f - OpenHalfHeight), new Vector2(0.5f + OpenHalfWidth, 0.5f), false);
            AddArc("ScopeArcSW", ArcKeySe,
                new Vector2(0.5f - OpenHalfWidth, 0.5f - OpenHalfHeight), new Vector2(0.5f, 0.5f), true);

            // ---- ③ 十字线（两条各自贯穿整屏；画在最后 ⇒ 压在黑底之上）----
            AddCross("ScopeCrossV", true);
            AddCross("ScopeCrossH", false);

            Game.Logger?.Info(Tag,
                $"开镜遮罩就绪：开孔半轴 = {OpenHalfWidth:F3}×屏宽 / {OpenHalfHeight:F3}×屏高，" +
                $"线宽 = {CrossThickness:F5}×屏宽（素材 = 原版 scope_arc*.tga，左下角为右下角的水平镜像）");
        }

        /// <summary>开镜时显示、否则整块隐藏（每帧调；状态没变不动 UI）。</summary>
        public void Refresh(bool zoomed)
        {
            if (_root == null) return;
            if (_root.gameObject.activeSelf == zoomed) return;
            _root.gameObject.SetActive(zoomed);
        }

        private void AddBand(string name, Vector2 anchorMin, Vector2 anchorMax)
        {
            var img = UIFactory.CreatePanel(name, _root, Color.black, false);
            Anchor(img.rectTransform, anchorMin, anchorMax);
        }

        private void AddArc(string name, string arcKey, Vector2 anchorMin, Vector2 anchorMax, bool mirrorX)
        {
            var rt = UIFactory.CreateNode(name, _root);
            Anchor(rt, anchorMin, anchorMax);
            if (mirrorX) rt.localScale = new Vector3(-1f, 1f, 1f);

            var img = rt.gameObject.AddComponent<Image>();
            img.raycastTarget = false;
            img.color = Color.white;
            img.type = Image.Type.Simple;
            // 先关着：**没有 sprite 的 Image 会被 uGUI 画成实心白块**，取到素材才开。
            img.enabled = false;
            LoadArcSprite(arcKey, img);
        }

        private void AddCross(string name, bool vertical)
        {
            var img = UIFactory.CreatePanel(name, _root, new Color(0f, 0f, 0f, CrossAlpha), false);
            var rt = img.rectTransform;
            // 线宽按屏宽的比例给（原版量的是相对 640 宽的 2 px ⇒ 与分辨率成比例，不写死像素）。
            var a0 = 0.5f - CrossThickness * 0.5f;
            var a1 = 0.5f + CrossThickness * 0.5f;
            if (vertical) Anchor(rt, new Vector2(a0, 0f), new Vector2(a1, 1f));
            else Anchor(rt, new Vector2(0f, a0), new Vector2(1f, a1));
        }

        /// <summary>锚点式定位：矩形 = 屏内的一段比例区间（换分辨率自动跟着走，不必算像素）。</summary>
        private static void Anchor(RectTransform rt, Vector2 min, Vector2 max)
        {
            rt.anchorMin = min;
            rt.anchorMax = max;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        /// <summary>
        /// 取原版 tga 当 sprite。原版素材是 <c>.tga</c>（不是本工程 HUD 图标那种解出的 PNG），
        /// 按 <c>Texture2D</c> 取回来再包成 sprite —— 不依赖任何导入设置（tga 默认导入就是 Texture），
        /// 也**不许**为此手写 <c>.meta</c>。引擎的 <c>Game.Res.LoadAsset</c> 只有**异步**版。
        /// 取不到只报一次 Warn（宁可只剩黑圈，也不要白块）。
        /// </summary>
        private void LoadArcSprite(string arcKey, Image target)
        {
            if (Game.Res == null)
            {
                WarnMissing(arcKey, "Game.Res 为 null（未 Launch）");
                return;
            }

            Game.Res.LoadAsset<Texture2D>(ArcDir + arcKey, tex =>
            {
                if (target == null) return;                 // 面板已销毁：回调丢弃
                if (tex == null)
                {
                    WarnMissing(arcKey, "取不到（Resources 下没有这个 tga？）");
                    return;
                }
                tex.wrapMode = TextureWrapMode.Clamp;
                tex.filterMode = FilterMode.Bilinear;
                var sprite = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height),
                    new Vector2(0.5f, 0.5f), 100f);
                sprite.name = arcKey;
                target.sprite = sprite;
                target.enabled = true;
            });
        }

        private void WarnMissing(string arcKey, string why)
        {
            if (_spriteWarned) return;
            _spriteWarned = true;
            Game.Logger?.Warn(Tag,
                $"缺原版镜筒素材 Resources/{ArcDir}{arcKey}.tga：{why} ⇒ 开镜只剩纯黑圈没有弧边" +
                "（素材来源见 CsScopeOverlayWidget 类注释）");
        }
    }
}
