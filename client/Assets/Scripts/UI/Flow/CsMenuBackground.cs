using CloverEngine;
using Cs16.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Cs16.UI
{
    /// <summary>
    /// 原版菜单 / 读条界面的**背景拼图**（12 块原版 TGA 逐块摆放）。
    ///
    /// <para><b>为什么是"拼图"而不是一张大图</b>：原版 CS 1.6 自己就是这么放的 ——
    /// <c>原版资源/cs16src/cs16game/app/valve/resource/backgroundlayout.txt</c>（与
    /// <c>backgroundloadinglayout.txt</c> 逐字相同）声明了 12 个 ImagePanel 的 <c>scaled</c> 坐标：
    /// 4 列（0 / 256 / 512 / 768）× 3 行（0 / 256 / 512），拼成 <c>resolution 800 600</c>。
    /// 每块的尺寸取自贴图自身（实测 <c>800_{1,2}_a/b/c</c> = 256×256、<c>800_{1,2}_d</c> = 32×256、
    /// <c>800_3_a/b/c</c> = 256×88、<c>800_3_d</c> = 32×88；256×3+32 = 800、256+256+88 = 600 自洽）。
    /// 不许"拼成一张大图再拉伸" —— 那会改掉原版的缩放行为（每块各自按屏幕比缩放）。</para>
    ///
    /// <para><b>缩放口径</b>：<c>scaled</c> 的语义是"按屏幕缩放"，即设计空间 800×600 → 当前画布：
    /// <c>x * (canvasW / 800)</c>、<c>y * (canvasH / 600)</c>，尺寸同比值。画布尺寸取本组件所在
    /// RectTransform 的 <c>rect</c>（铺满父层级节点，见 <see cref="Attach"/>）。</para>
    ///
    /// <para><b>贴图从哪来</b>：12 张 TGA 已逐字节复制进 <c>Resources/Background/</c>，
    /// 路径经 <see cref="ResPaths.BackgroundMenuTile"/>（不散落字符串、不用 <c>Resources.Load</c>）。
    /// sprite 引用**由编辑器生成器（<c>Editor/Flow/FlowSetup.cs</c>）在生成期绑进预制体**
    /// —— 运行期没有 AssetDatabase；<see cref="EnsureSprites"/> 只是预制体被改坏时的兜底。</para>
    ///
    /// <para><b>失败不许静默</b>：任何一块取不到都 <c>Warn</c> 出**是哪一块**（文件名），
    /// 12 块全部就绪时**逐块**打一条 <c>Info</c>（文件名 + 原图尺寸 + 原设计坐标），
    /// 这样"路径大小写错 / 贴图没导入"这类静默失败在日志里第一眼就能看出来。</para>
    /// </summary>
    public sealed class CsMenuBackground : MonoBehaviour
    {
        private const string Tag = "UI";

        /// <summary>设计空间宽 = <c>backgroundlayout.txt</c> 的 <c>resolution 800 600</c> 的 800。</summary>
        private const float DesignWidth = 800f;

        /// <summary>设计空间高 = 同一行的 600。</summary>
        private const float DesignHeight = 600f;

        /// <summary>本组件节点的节点名（生成器 / 运行期兜底都用它，便于在层级里认出来）。</summary>
        private const string NodeName = "CsMenuBackground";

        /// <summary>
        /// 一块拼图的规格。<see cref="Name"/> 就是 <c>backgroundlayout.txt</c> 里那行文件名去扩展名
        /// （同时是 GameObject 名与 <see cref="ResPaths.BackgroundMenuTile"/> 的短名）。
        /// </summary>
        private struct TileSpec
        {
            public string Name;
            public float X;
            public float Y;
            public float Width;
            public float Height;
        }

        /// <summary>
        /// <b>逐行对应</b> `原版资源/cs16src/cs16game/app/valve/resource/backgroundlayout.txt`（:3-16）：
        /// 行序 = a/b/c/d；第 1/2 行 y = 0 / 256（高 256），第 3 行 y = 512（高 88）；
        /// 列 x = 0 / 256 / 512 / 768；宽 = 贴图实际宽度（d 列 = 32）。
        /// 这里的每个数都能与那份 .txt 逐字对上，不许"看起来差不多"就改。
        /// </summary>
        private static readonly TileSpec[] Specs =
        {
            new TileSpec { Name = "800_1_a_loading", X = 0f,   Y = 0f,   Width = 256f, Height = 256f },
            new TileSpec { Name = "800_1_b_loading", X = 256f, Y = 0f,   Width = 256f, Height = 256f },
            new TileSpec { Name = "800_1_c_loading", X = 512f, Y = 0f,   Width = 256f, Height = 256f },
            new TileSpec { Name = "800_1_d_loading", X = 768f, Y = 0f,   Width = 32f,  Height = 256f },
            new TileSpec { Name = "800_2_a_loading", X = 0f,   Y = 256f, Width = 256f, Height = 256f },
            new TileSpec { Name = "800_2_b_loading", X = 256f, Y = 256f, Width = 256f, Height = 256f },
            new TileSpec { Name = "800_2_c_loading", X = 512f, Y = 256f, Width = 256f, Height = 256f },
            new TileSpec { Name = "800_2_d_loading", X = 768f, Y = 256f, Width = 32f,  Height = 256f },
            new TileSpec { Name = "800_3_a_loading", X = 0f,   Y = 512f, Width = 256f, Height = 88f  },
            new TileSpec { Name = "800_3_b_loading", X = 256f, Y = 512f, Width = 256f, Height = 88f  },
            new TileSpec { Name = "800_3_c_loading", X = 512f, Y = 512f, Width = 256f, Height = 88f  },
            new TileSpec { Name = "800_3_d_loading", X = 768f, Y = 512f, Width = 32f,  Height = 88f  },
        };

        [SerializeField] private Image[] _tiles;

        private bool _requested;
        private bool _warnedMissing;
        private bool _logged;
        /// <summary>逐块告警去重（缺一块只报一次，不刷屏）。</summary>
        private readonly bool[] _warnedTile = new bool[Specs.Length];

        /// <summary>已就绪（有 sprite）的块数。</summary>
        public int ReadyCount
        {
            get
            {
                if (_tiles == null) return 0;
                var n = 0;
                for (var i = 0; i < _tiles.Length; i++)
                {
                    if (_tiles[i] != null && _tiles[i].sprite != null) n++;
                }
                return n;
            }
        }

        /// <summary>拼图块总数（= `backgroundlayout.txt` 的行数 = 12）。</summary>
        public static int TileCount => Specs.Length;

        /// <summary>本组件挂在哪个面板下（日志里区分主菜单 / 读条那一份）。</summary>
        private string Owner => transform.parent != null ? transform.parent.name : name;

        /// <summary>
        /// 在 <paramref name="parent"/> 下建出本组件（铺满父节点）并搭好 12 块拼图。
        /// 面板的 <c>BuildLayout</c> 调它 —— 生成器与运行期兜底走的是同一条路径。
        ///
        /// <para>必须建在**纯色 Backdrop 之后、其余内容之前**：这样拼图盖住纯色兜底，
        /// 而字标 / 菜单项 / 进度条仍压在拼图之上（层级不动）。</para>
        /// </summary>
        public static CsMenuBackground Attach(Transform parent)
        {
            if (parent == null)
            {
                Game.Logger?.Warn(Tag, $"{nameof(CsMenuBackground)} 的父节点为空，原版背景拼图建不出来（保留纯色兜底）");
                return null;
            }

            var node = UIFactory.CreateNode(NodeName, parent);
            var comp = node.gameObject.AddComponent<CsMenuBackground>();
            comp.BuildTiles();
            return comp;
        }

        /// <summary>按 <see cref="Specs"/> 建出 12 个 Image 子节点（贴图未就绪前 <c>enabled=false</c>，不许画成白块）。</summary>
        public void BuildTiles()
        {
            var parent = transform;
            _tiles = new Image[Specs.Length];
            for (var i = 0; i < Specs.Length; i++)
            {
                var img = UIFactory.CreatePanel(Specs[i].Name, parent, Color.white, false);
                img.enabled = false;
                _tiles[i] = img;
            }
        }

        /// <summary>
        /// 按设计空间坐标 × 画布缩放比摆放 12 块。画布尺寸随窗口变化（CanvasScaler 是 match=0.5），
        /// 所以这是**可重入**的 —— 尺寸变了就再摆一次。
        /// </summary>
        public void ApplyLayout()
        {
            var rt = transform as RectTransform;
            if (rt == null)
            {
                WarnOnce($"{nameof(CsMenuBackground)} 挂在非 RectTransform 节点上，拼图摆不出来");
                return;
            }
            if (_tiles == null || _tiles.Length != Specs.Length) return;

            var w = rt.rect.width;
            var h = rt.rect.height;
            // 画布还没算出来（矩形为 0）时先不动：铺满父节点后 OnRectTransformDimensionsChange 会再来一次。
            if (w <= 0f || h <= 0f) return;

            var sx = w / DesignWidth;
            var sy = h / DesignHeight;

            for (var i = 0; i < Specs.Length; i++)
            {
                var img = _tiles[i];
                if (img == null) continue;
                // 逐块按原坐标摆（不是拼一张大图）：AnchoredTopLeft 的 y 为负才是向下，.txt 的 y 向下为正。
                UIFactory.AnchoredTopLeft(img.rectTransform,
                    new Vector2(Specs[i].X * sx, -Specs[i].Y * sy),
                    new Vector2(Specs[i].Width * sx, Specs[i].Height * sy));
            }
        }

        /// <summary>
        /// 生成期（<c>FlowSetup</c> 造预制体时）绑入 12 张 sprite —— 运行期没有 AssetDatabase，
        /// 引用必须在编辑器里绑进预制体。缺块**逐块报出**是哪一张。
        /// </summary>
        public void BindSprites(Sprite[] sprites)
        {
            if (_tiles == null || _tiles.Length != Specs.Length)
            {
                WarnOnce($"拼图节点不完整（{(_tiles == null ? 0 : _tiles.Length)}/{Specs.Length}），已跳过 sprite 绑定");
                return;
            }

            for (var i = 0; i < Specs.Length; i++)
            {
                var s = sprites != null && i < sprites.Length ? sprites[i] : null;
                if (s == null)
                {
                    WarnTileOnce(i);
                    continue;
                }
                ApplyTile(i, s);
            }
            LogTilesOnce("生成期把 12 张 sprite 绑进预制体");
        }

        /// <summary>
        /// 运行期兜底：预制体里已经绑好 12 张就什么都不做；缺哪张就只请求哪张。
        /// <c>Game.Res</c> 为空（<c>CloverRes.Init</c> 没跑）时明确告警，绝不静默。
        /// </summary>
        public void EnsureSprites()
        {
            if (_tiles == null || _tiles.Length != Specs.Length)
            {
                WarnOnce($"拼图节点不完整（{(_tiles == null ? 0 : _tiles.Length)}/{Specs.Length}），运行期兜底加载无法进行");
                return;
            }
            if (MissingCount() == 0)
            {
                LogTilesOnce("运行期（预制体自带 sprite）");
                return;
            }
            if (_requested) return;

            var res = Game.Res;
            if (res == null)
            {
                WarnOnce("Game.Res 为 null（CloverRes.Init 未执行？），原版菜单背景加载不了 —— 保留纯色 Backdrop");
                return;
            }

            _requested = true;
            for (var i = 0; i < Specs.Length; i++)
            {
                if (_tiles[i] != null && _tiles[i].sprite != null) continue;
                var index = i;
                res.LoadAsset<Sprite>(ResPaths.BackgroundMenuTile(Specs[index].Name), s => OnTileLoaded(index, s));
            }
        }

        private void Awake()
        {
            if (_tiles == null || _tiles.Length != Specs.Length || HasNullTile())
            {
                Game.Logger?.Warn(Tag,
                    $"{NodeName} 预制体里没有可用的拼图节点（生成器没跑过？），运行期兜底重搭 {Specs.Length} 块");
                Rebuild();
            }
            ApplyLayout();
        }

        private void Start()
        {
            // Awake 时节点还没被挂到 UI 层级下（rect 可能是 0）⇒ 起帧再摆一次，保证这一次用的是真实画布尺寸。
            ApplyLayout();
            // 运行期的"12 块逐块落日志"：贴图缺失 / 路径写错在这里第一眼可见。
            LogTilesOnce("运行期（预制体自带 sprite）");
        }

        /// <summary>铺满父节点后矩形尺寸才确定，这里补一次摆放（CanvasScaler 的 match=0.5 ⇒ 尺寸随窗口变）。</summary>
        private void OnRectTransformDimensionsChange()
        {
            ApplyLayout();
        }

        private void OnTileLoaded(int index, Sprite sprite)
        {
            if (sprite == null)
            {
                _requested = false;
                WarnTileOnce(index);
                return;
            }
            ApplyTile(index, sprite);
            LogTilesOnce("运行期兜底加载");
        }

        private void ApplyTile(int index, Sprite sprite)
        {
            var img = _tiles[index];
            if (img == null)
            {
                WarnTileOnce(index);
                return;
            }

            img.sprite = sprite;
            img.color = Color.white;
            img.type = Image.Type.Simple;
            img.enabled = true;
        }

        /// <summary>
        /// 12 块**全部**就绪时，逐块打一条 Info（文件名 + 原图尺寸 + 原设计坐标 + 本帧画布块尺寸），
        /// 末尾再补一条汇总（设计空间 → 画布的缩放比）。只打一次。
        /// </summary>
        private void LogTilesOnce(string how)
        {
            if (_logged) return;
            if (ReadyCount != Specs.Length) return;
            _logged = true;

            for (var i = 0; i < Specs.Length; i++)
            {
                var img = _tiles[i];
                var size = img != null && img.rectTransform != null ? img.rectTransform.rect.size : Vector2.zero;
                Game.Logger?.Info(Tag,
                    $"[{Owner}] 原版菜单背景拼图 [{i + 1}/{Specs.Length}] 就绪：" +
                    $"Resources/{ResPaths.BackgroundMenuTile(Specs[i].Name)}.tga" +
                    $"（原图 {Specs[i].Width:0}×{Specs[i].Height:0}，原设计坐标 {Specs[i].X:0},{Specs[i].Y:0}" +
                    $" → 画布块 {size.x:0.#}×{size.y:0.#}）[{how}]");
            }

            var rt = transform as RectTransform;
            var canvas = rt != null ? rt.rect.size : Vector2.zero;
            Game.Logger?.Info(Tag,
                $"[{Owner}] 原版菜单背景拼图 {Specs.Length}/{Specs.Length} 块全部就绪（{how}）：" +
                $"backgroundlayout.txt 的 {DesignWidth:0}×{DesignHeight:0} 设计空间 → 本帧画布 " +
                $"{canvas.x:0.#}×{canvas.y:0.#}（x×{canvas.x / DesignWidth:0.###} y×{canvas.y / DesignHeight:0.###}）");
        }

        /// <summary>缺 sprite 的块数（与 <see cref="ReadyCount"/> 互补）。</summary>
        private int MissingCount()
        {
            if (_tiles == null) return Specs.Length;
            var n = 0;
            for (var i = 0; i < _tiles.Length; i++)
            {
                if (_tiles[i] == null || _tiles[i].sprite == null) n++;
            }
            return n;
        }

        private bool HasNullTile()
        {
            if (_tiles == null) return true;
            for (var i = 0; i < _tiles.Length; i++)
            {
                if (_tiles[i] == null) return true;
            }
            return false;
        }

        /// <summary>运行期兜底重搭：清掉现有子节点后按 <see cref="Specs"/> 重建。</summary>
        private void Rebuild()
        {
            for (var i = transform.childCount - 1; i >= 0; i--)
            {
                var child = transform.GetChild(i);
                if (child != null) Destroy(child.gameObject);
            }
            BuildTiles();
        }

        /// <summary>逐块告警一次（报出**是哪一块 / 是哪个路径**，并说明兜底行为）。</summary>
        private void WarnTileOnce(int index)
        {
            if (index < 0 || index >= Specs.Length || _warnedTile[index]) return;
            _warnedTile[index] = true;
            var path = ResPaths.BackgroundMenuTile(Specs[index].Name);
            Game.Logger?.Warn(Tag,
                $"[{Owner}] 原版菜单背景拼图 {Specs[index].Name} 加载失败（sprite 为空）：Resources/{path}.tga" +
                " 没导入成 Sprite？该块不显示，保留纯色 Backdrop 兜底");
        }

        private void WarnOnce(string message)
        {
            if (_warnedMissing) return;
            _warnedMissing = true;
            Game.Logger?.Warn(Tag, message);
        }
    }
}
