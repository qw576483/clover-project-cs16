using CloverEngine;
using Cs16.Core;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Events;

namespace Cs16.UI
{
    /// <summary>
    /// 流程面板基类：把"布局由代码搭"收敛到一处。
    ///
    /// <para>
    /// 为什么布局写代码而不是在预制体里手摆：本工程的面板预制体由 Editor 生成器
    /// （<c>Assets/Editor/Flow/FlowSetup.cs</c>）**用代码产出**（禁止手写 .prefab YAML），
    /// 生成器与运行期必须共用同一份布局定义，否则"编辑器里看到的"与"跑起来看到的"必然分叉。
    /// </para>
    ///
    /// <para>两条路径：</para>
    /// <list type="number">
    /// <item>生成器：<c>AddComponent&lt;T&gt;()</c> → <see cref="BuildLayout"/> → 存成预制体
    /// （子节点与 <c>[SerializeField]</c> 引用一起被序列化进去）；</item>
    /// <item>运行期兜底：预制体里没有子节点时（生成器没跑过 / 被改坏）在 <c>Awake</c> 里补搭一遍并打警告。</item>
    /// </list>
    /// </summary>
    public abstract class CsPanelBase : UIPanel
    {
        /// <summary>日志 tag（客户端统一 <c>Game.Logger</c>，禁止裸 UnityEngine.Debug 打日志）。</summary>
        protected const string Tag = "UI";

        /// <summary>
        /// 用 <see cref="CsUiStyle"/> / <see cref="UIFactory"/> 搭出本面板的全部控件，
        /// 并把结果写回各 <c>[SerializeField]</c> 字段。
        /// </summary>
        public abstract void BuildLayout(RectTransform root);

        protected virtual void Awake()
        {
            var rt = transform as RectTransform;
            if (rt == null)
            {
                Game.Logger?.Error(Tag, $"{GetType().Name} 根节点不是 RectTransform（预制体生成有问题，面板会渲染不出来）");
                return;
            }

            // 面板根节点必须铺满父层级节点：没铺满的表现是"点击只有一小块区域有效"。
            CsUiStyle.StretchRoot(rt);

            if (rt.childCount > 0) return;

            Game.Logger?.Warn(Tag, $"{GetType().Name} 预制体没有子节点，运行期用 UIFactory 兜底搭建");
            BuildLayout(rt);
        }

        /// <summary>引用缺失时留一条可定位的日志（静默的空白面板是最难查的一类问题）。</summary>
        protected void WarnIfNull(Object obj, string what)
        {
            if (obj != null) return;
            Game.Logger?.Warn(Tag, $"{GetType().Name} 缺少「{what}」引用：预制体未由 FlowSetup 生成或已被改动");
        }

        /// <summary>
        /// 绑定按钮点击。
        ///
        /// <para>
        /// **必须在 <c>OnOpen</c> 里重新绑一次**：<c>BuildLayout</c> 里挂的是运行时监听器
        /// （<c>onClick.AddListener(delegate)</c>），它**不会**被序列化进预制体 ——
        /// 预制体存盘再实例化后按钮是死的。只有 <c>OnOpen</c>（每次打开都跑）里绑才可靠。
        /// </para>
        /// </summary>
        protected void Bind(Button button, UnityAction action, string what)
        {
            if (button == null)
            {
                Game.Logger?.Warn(Tag, $"{GetType().Name} 按钮「{what}」引用缺失，事件未绑定");
                return;
            }
            button.onClick.RemoveAllListeners();
            AddClickSfx(button);            // UI 点击音（见 AddClickSfx 的注释）
            button.onClick.AddListener(action);
        }

        /// <summary>
        /// 给按钮挂一条 **UI 点击音**（A 的 <c>sound/ui/buttonclick.wav</c> ⇒ 本工程
        /// <c>Resources/Sound/SFX/sfx/menu_click.wav</c>）。
        ///
        /// <para><b>为什么放在这里</b>：这是本项目**唯一**的按钮绑定入口（<see cref="Bind"/>），
        /// 每个面板的 <c>OnOpen</c> 都要走它；而 <c>Bind</c> 第一件事就是
        /// <c>RemoveAllListeners()</c> ⇒ 点击音必须**紧跟其后**加，否则会被清掉。
        /// 自己 <c>RemoveAllListeners()</c> 重绑的按钮（如 <c>OptionsPanel.WireCtrls</c>）
        /// 要显式再调一次本方法。</para>
        ///
        /// <para>⛔ UI 层不许 using 任何 <c>Module</c> ⇒ 不能用 <c>Module/Audio</c> 的
        /// <c>SfxService</c>（那是给游戏内音效的转发层），这里直接走引擎 <c>Game.Sound</c>；
        /// 短名真源 = <see cref="ResPaths.SfxMenuClick"/>（Core 层，UI 可以引）。</para>
        /// </summary>
        protected static void AddClickSfx(Button button)
        {
            if (button == null) return;
            button.onClick.AddListener(PlayClickSfx);
        }

        /// <summary>点击音的播放体（缺资源/缺 Sound 域时引擎自己会限频告警一次，这里不重复判）。</summary>
        private static void PlayClickSfx()
        {
            Game.Sound?.PlaySFX(ResPaths.SfxMenuClick);
        }

        /// <summary>绑定滑块数值变化（同样必须在 <c>OnOpen</c> 里绑，理由见 <see cref="Bind"/>）。</summary>
        protected void BindSlider(Slider slider, UnityAction<float> action, string what)
        {
            if (slider == null)
            {
                Game.Logger?.Warn(Tag, $"{GetType().Name} 滑块「{what}」引用缺失，该项不可调");
                return;
            }
            slider.onValueChanged.RemoveAllListeners();
            slider.onValueChanged.AddListener(action);
        }

        /// <summary>绑定输入框文本变化（同样必须在 <c>OnOpen</c> 里绑，理由见 <see cref="Bind"/>）。</summary>
        protected void BindInput(InputField field, UnityAction<string> action, string what)
        {
            if (field == null)
            {
                Game.Logger?.Warn(Tag, $"{GetType().Name} 输入框「{what}」引用缺失，该项不可编辑");
                return;
            }
            field.onValueChanged.RemoveAllListeners();
            field.onValueChanged.AddListener(action);
        }
    }
}
