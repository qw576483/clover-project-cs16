using CloverEngine;
using Cs16.Module.View;
using UnityEngine;
using UnityEngine.UI;

namespace Cs16.EditorTools
{
    /// <summary>
    /// 生成 <c>Assets/Resources/UI/WorldNameplate.prefab</c> —— 头顶**世界空间**名字 + 血条。
    ///
    /// <para><b>结构（运行期由 <see cref="Nameplate"/> 驱动）</b>：</para>
    /// <code>
    /// WorldNameplate            (Nameplate 组件；运行期被挂到角色视图根下，每帧 billboard)
    ///   └─ Canvas               (World Space，localScale 0.01 → 100 UI px = 1 m)
    ///        ├─ Name           (Text，上半区，队色，水平溢出允许 → 不折行)
    ///        ├─ HpBack         (Image，底部居中，细长)
    ///        │    └─ HpFill    (Image，锚点宽度表达血量比例)
    /// </code>
    ///
    /// <para>两个 Image 都用**真实的 1×1 白 sprite**（不留空 sprite）：空 sprite 的 Image 在
    /// 部分路径下不渲染，而 Filled 类型更是会退化成整块矩形（扣血看不出来）——
    /// 所以这里用"锚点宽度"表达比例，不依赖 sprite 类型。</para>
    /// </summary>
    internal static class NameplatePrefabBuilder
    {
        public const string PrefabAssetPath = "Assets/Resources/UI/WorldNameplate.prefab";

        private const string RootName = "WorldNameplate";
        private const string CanvasName = "Canvas";
        private const string NameName = "Name";
        private const string BackName = "HpBack";
        private const string FillName = "HpFill";

        // UI 像素尺寸（乘 Canvas 的 0.01 缩放 = 世界米）
        private const float CanvasWidth = 130f;
        private const float CanvasHeight = 44f;
        private const float BarWidth = CsViewTuning.NameplateBarWidth / CsViewTuning.NameplateCanvasScale;   // 70
        private const float BarHeight = CsViewTuning.NameplateBarHeight / CsViewTuning.NameplateCanvasScale; // 7.5
        private const int NameFontSize = 18;

        /// <summary>生成预制体；<paramref name="white"/> 是 1×1 白 sprite（Image 必须有 sprite）。</summary>
        public static bool Build(Sprite white, out string message)
        {
            message = null;

            var root = new GameObject(RootName, typeof(RectTransform));
            var rootRt = (RectTransform)root.transform;
            rootRt.sizeDelta = new Vector2(CanvasWidth, CanvasHeight);

            // 画布本体：世界空间 + 缩小到米
            var canvasGo = new GameObject(CanvasName, typeof(RectTransform));
            var canvasRt = (RectTransform)canvasGo.transform;
            canvasRt.SetParent(root.transform, false);
            canvasRt.anchorMin = canvasRt.anchorMax = new Vector2(0.5f, 0.5f);
            canvasRt.pivot = new Vector2(0.5f, 0.5f);
            canvasRt.sizeDelta = new Vector2(CanvasWidth, CanvasHeight);
            canvasRt.localPosition = Vector3.zero;
            canvasRt.localScale = Vector3.one * CsViewTuning.NameplateCanvasScale;

            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.overrideSorting = false;
            canvas.sortingOrder = 0;
            // 世界空间画布不接收输入：不要 GraphicRaycaster（否则射线/输入会去点它）

            // 名字（上半区）
            var nameText = UIFactory.CreateText(NameName, canvasRt, "Player", NameFontSize,
                TextAnchor.MiddleCenter, Color.white);
            var nameRt = nameText.rectTransform;
            nameRt.anchorMin = new Vector2(0f, 0.45f);
            nameRt.anchorMax = new Vector2(1f, 1f);
            nameRt.offsetMin = Vector2.zero;
            nameRt.offsetMax = Vector2.zero;
            nameText.horizontalOverflow = HorizontalWrapMode.Overflow;   // 名字长也不折行
            nameText.verticalOverflow = VerticalWrapMode.Overflow;

            // 血条底槽（下半区，居中定尺）
            var back = UIFactory.CreatePanel(BackName, canvasRt, CsViewTuning.HpBack, false);
            var backRt = back.rectTransform;
            backRt.anchorMin = backRt.anchorMax = new Vector2(0.5f, 0.5f);
            backRt.pivot = new Vector2(0.5f, 0.5f);
            backRt.sizeDelta = new Vector2(BarWidth, BarHeight);
            backRt.anchoredPosition = new Vector2(0f, -CanvasHeight * 0.30f);
            back.sprite = white;                     // 必须有 sprite（见类注释）
            back.type = Image.Type.Simple;

            // 血条填充：锚点宽度表达比例
            var fill = UIFactory.CreatePanel(FillName, backRt, CsViewTuning.HpHigh, false);
            var fillRt = fill.rectTransform;
            fillRt.anchorMin = new Vector2(0f, 0f);
            fillRt.anchorMax = new Vector2(1f, 1f);
            fillRt.offsetMin = Vector2.zero;
            fillRt.offsetMax = Vector2.zero;
            fill.sprite = white;
            fill.type = Image.Type.Simple;

            // 驱动组件 + 显式接线（存进预制体，运行期不用再找）
            var plate = root.AddComponent<Nameplate>();
            if (plate == null)
            {
                Object.DestroyImmediate(root);
                message = "Nameplate 组件添加失败";
                return false;
            }
            plate.EditorWire(canvas, nameText, back, fill);

            EnsureFolder("Assets/Resources/UI");
            var saved = UnityEditor.PrefabUtility.SaveAsPrefabAsset(root, PrefabAssetPath);
            Object.DestroyImmediate(root);

            if (saved == null)
            {
                message = $"预制体保存失败：{PrefabAssetPath}";
                return false;
            }

            message = $"{PrefabAssetPath}（名字+血条，世界空间画布，血条用锚点宽度）";
            return true;
        }

        private static void EnsureFolder(string path)
        {
            if (UnityEditor.AssetDatabase.IsValidFolder(path)) return;
            var parent = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent)) parent = parent.Replace('\\', '/');
            var leaf = System.IO.Path.GetFileName(path);
            if (!string.IsNullOrEmpty(parent) && !UnityEditor.AssetDatabase.IsValidFolder(parent))
                EnsureFolder(parent);
            UnityEditor.AssetDatabase.CreateFolder(parent, leaf);
        }
    }
}
