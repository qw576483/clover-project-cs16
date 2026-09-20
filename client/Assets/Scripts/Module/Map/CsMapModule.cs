using System;
using CloverEngine;
using Cs16.Core;
using UnityEngine;

namespace Cs16.Module.Map
{
    /// <summary>
    /// 地图模块的 MonoBehaviour 宿主：把 <see cref="CsMap"/> 挂进 Unity 生命周期，并在
    /// <c>Start()</c> 里自动把地图数据拉起来。
    ///
    /// <para><b>装配约定</b>（<c>Bootstrap</c> 已按此顺序 AddComponent，改顺序会出问题）：</para>
    /// <list type="number">
    /// <item>本组件必须**先于** <c>MatchModule</c> 挂上 —— 比赛模块在 <c>Awake</c> 里就要拿
    /// <see cref="Map"/>；</item>
    /// <item>门面在 <c>Awake</c> 里就构造好（<c>AddComponent</c> 会同步触发 Awake），
    /// 所以"刚 AddComponent 完就读 <see cref="Map"/>" 拿到的一定不是 null；</item>
    /// <item>数据加载放在 <c>Start</c>：那时 <c>CloverRes.Init</c> 已经跑过（Bootstrap 里资源先于装配）。</item>
    /// </list>
    ///
    /// <para>加载失败**必须** <c>Game.Logger.Error</c> 并给出 <see cref="ICsMap.Status"/>：
    /// 地图没数据的症状是"哪儿都能走 / 出生在原点"，不报日志就没人知道是数据没烘。</para>
    /// </summary>
    public sealed class CsMapModule : MonoBehaviour
    {
        private const string Tag = "Map";

        /// <summary>地图门面（<see cref="ICsMap"/>）。<c>Awake</c> 后恒非 null。</summary>
        public ICsMap Map { get; private set; }

        /// <summary>具体实现（给需要标记就绪状态的调用方；跨模块请只用 <see cref="ICsMap"/>）。</summary>
        public CsMap Instance => Map as CsMap;

        private bool _loadRequested;

        private void Awake()
        {
            Map = new CsMap();
        }

        private void Start()
        {
            if (Map == null)
            {
                // 非预期分支：Awake 没跑（脚本没编译进来 / 组件被禁用过）—— 这里补一次，别让整条链路悬着
                Game.Logger?.Error(Tag, "CsMapModule.Awake 未构造门面，Start 里补建（检查脚本是否正常编译）");
                Map = new CsMap();
            }
            if (_loadRequested) return;
            _loadRequested = true;

            // 进图前先把数据装好（AppFlow 进舞台时也会调一次 LoadAsync —— 引擎侧对重复请求有守卫，
            // 这里再挡一层，免得两个调用方各自发起一遍）
            Map.LoadAsync(ResPaths.MapDust2,
                () => Game.Logger?.Info(Tag, $"地图数据就绪：{Map.Status}"),
                err => Game.Logger?.Error(Tag,
                    $"地图数据加载失败：{err}（Game.Map.Loaded={Map.IsLoaded}）—— " +
                    "进图后 WalkableAt 会一律放行、出生点会退化，请先跑 Clover/CS16/烘焙 de_dust2"));
        }

        private void OnDestroy()
        {
            // 卸载场景/退出时把位图与标记表放掉（重新进图会重新加载）
            try
            {
                Map?.Unload();
            }
            catch (Exception e)
            {
                // 非预期分支：销毁期异常不能往外抛（会把后面的销毁链带崩），但要留痕
                Game.Logger?.Warn(Tag, "CsMapModule.OnDestroy 卸载地图时异常：" + e.Message);
            }
        }
    }
}
