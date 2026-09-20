using CloverEngine;
using Cs16.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Cs16.Module.View
{
    /// <summary>
    /// 头顶**世界空间**名牌：一行名字 + 一条血条。挂在
    /// <c>Assets/Resources/UI/WorldNameplate.prefab</c> 上（由
    /// <c>Assets/Editor/Views/WorldNameplateBuilder.cs</c> 生成）。
    ///
    /// <para><b>血条为什么用"锚点宽度"而不是 <c>Image.fillAmount</c></b>：无 sprite 的 Filled Image
    /// 会退化成整块矩形（扣血看不出来）—— 这是引擎注释里明确记过的坑。这里改成
    /// <c>anchorMax.x = ratio</c> + 左右 offset 归零：比例由**布局**表达，不依赖 sprite 类型，
    /// 也不依赖 <c>Image.type</c>。预制体里的两个 Image 都挂了真实的 1×1 白 sprite（不留空 sprite）。</para>
    ///
    /// <para><b>广告牌朝向</b>：本组件所在的节点是角色视图（会随 yaw 转）的子物体，所以必须
    /// **每帧**写世界朝向；朝向取 <c>cam.transform.rotation</c> —— 与引擎的
    /// <c>CloverEngine.WorldHpBar</c> 同一口径（uGUI 的世界空间画布从它的 -Z 侧看才是正读的）。</para>
    /// </summary>
    public sealed class Nameplate : MonoBehaviour
    {
        [Tooltip("世界空间画布（留空则自动在本节点上找）。")]
        [SerializeField] private Canvas _canvas;

        [Tooltip("名字文本。")]
        [SerializeField] private Text _nameText;

        [Tooltip("血条底槽（深色）。")]
        [SerializeField] private Image _barBack;

        [Tooltip("血条填充（按锚点宽度表达比例）。")]
        [SerializeField] private Image _barFill;

        private Camera _cam;
        private bool _visible = true;
        private float _shown;
        private string _lastName;

        /// <summary>由生成器写进预制体的引用（也可运行期兜底自找）。</summary>
        public void EditorWire(Canvas canvas, Text nameText, Image barBack, Image barFill)
        {
            _canvas = canvas;
            _nameText = nameText;
            _barBack = barBack;
            _barFill = barFill;
        }

        private void Awake()
        {
            // 兜底：预制体引用丢了也要能工作（按固定子节点名找）。
            if (_canvas == null) _canvas = GetComponentInChildren<Canvas>(true);
            if (_nameText == null)
            {
                var t = FindDeep("Name");
                if (t != null) _nameText = t.GetComponent<Text>();
            }
            if (_barBack == null)
            {
                var t = FindDeep("HpBack");
                if (t != null) _barBack = t.GetComponent<Image>();
            }
            if (_barFill == null)
            {
                var t = FindDeep("HpFill");
                if (t != null) _barFill = t.GetComponent<Image>();
            }

            if (_canvas == null || _nameText == null || _barFill == null)
            {
                Game.Logger.Warn("View",
                    $"名牌「{name}」预制体引用不完整（canvas={( _canvas != null)} name={(_nameText != null)} " +
                    $"fill={(_barFill != null)}）—— 头顶名字/血条可能不显示，请重跑 ArtSetup 生成 WorldNameplate.prefab");
            }
        }

        private Transform FindDeep(string childName)
        {
            var all = GetComponentsInChildren<Transform>(true);
            for (var i = 0; i < all.Length; i++)
            {
                if (all[i] != null && all[i].name == childName) return all[i];
            }
            return null;
        }

        /// <summary>整块名牌的显示/隐藏（死亡 / 超出距离 / 无视线时关掉）。</summary>
        public void SetVisible(bool visible)
        {
            if (_visible == visible)
            {
                // 仍需在"可见"时保持广告牌朝向；隐藏时不必动
                return;
            }
            _visible = visible;
            if (_canvas != null) _canvas.enabled = visible;
        }

        /// <summary>刷新名字与血量。比例由**锚点宽度**表达（见类注释）。</summary>
        public void Refresh(string displayName, int hp, int maxHp, Color color, bool showName, bool showBar)
        {
            if (!_visible) return;

            if (_nameText != null)
            {
                _nameText.enabled = showName;
                if (showName)
                {
                    var nm = string.IsNullOrEmpty(displayName) ? "?" : displayName;
                    if (_lastName != nm)
                    {
                        _lastName = nm;
                        _nameText.text = nm;
                    }
                    if (_nameText.color != color) _nameText.color = color;
                }
            }

            if (_barBack != null && _barBack.enabled != showBar) _barBack.enabled = showBar;

            if (_barFill != null)
            {
                if (_barFill.enabled != showBar) _barFill.enabled = showBar;
                if (showBar)
                {
                    var ratio = maxHp > 0 ? Mathf.Clamp01((float)hp / maxHp) : 0f;
                    var rt = _barFill.rectTransform;
                    // 锚点宽度表达比例：左锚 0、右锚 ratio，左右 padding 归零。
                    rt.anchorMin = new Vector2(0f, 0f);
                    rt.anchorMax = new Vector2(ratio, 1f);
                    rt.offsetMin = Vector2.zero;
                    rt.offsetMax = Vector2.zero;

                    var c = ratio > 0.5f ? CsViewTuning.HpHigh
                        : ratio > 0.25f ? CsViewTuning.HpMid
                        : CsViewTuning.HpLow;
                    if (_barFill.color != c) _barFill.color = c;
                }
            }
        }

        private void LateUpdate()
        {
            if (!_visible) return;
            if (_cam == null) _cam = Camera.main;
            if (_cam == null) return;

            // 父节点（角色视图根）会随 yaw 旋转，所以必须每帧写**世界**朝向。
            transform.rotation = _cam.transform.rotation;
        }
    }
}
