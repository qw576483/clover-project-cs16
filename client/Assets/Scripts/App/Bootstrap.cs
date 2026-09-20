using CloverEngine;
using Cs16.Module.Audio;
using Cs16.Module.Bot;
using Cs16.Module.Flow;
using Cs16.Module.Map;
using Cs16.Module.Match;
using Cs16.Module.Player;
using Cs16.Module.View;
using Cs16.UI;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Cs16.App
{
    /// <summary>
    /// **唯一组装点**：启动引擎 → 挂各模块 → 把流程跑起来。这里不写任何业务逻辑
    /// （伤害 / 胜负 / 逐帧玩法一律归各 Module）。
    ///
    /// <para>启动顺序（硬要求，顺序错了就是"点不动 / 找不到预制体"）：</para>
    /// <list type="number">
    /// <item><c>Game.Launch</c>（核心子系统 + 表现域模块自动挂载）；</item>
    /// <item><c>CloverRes.Init</c>（资源）→ <c>CloverInput.Init</c>（输入 + EventSystem，**必须早于建 UI**）；</item>
    /// <item>装配兄弟模块并注入门面；</item>
    /// <item><c>AppFlow.Enter()</c>（启动画面 → 主菜单，**不许直接进游戏场景**）。</item>
    /// </list>
    ///
    /// <para><b>单机版</b>：**不调用 <c>CloverNet 的网络初始化</c>**。因此 <c>Game.Net == null</c> 是正常状态，
    /// 业务侧（含本类）一律不碰 <c>Game.Net.*</c>；也刻意不订阅 <c>Net.OnKicked</c> 这类"只有联网才有意义"的事件。</para>
    /// </summary>
    public class Bootstrap : MonoBehaviour
    {
        private const string Tag = "App";
        private const string ResourceRoot = "Assets/Resources";

        /// <summary>
        /// <c>CloverRes.Init</c> 的参数语义是「**Resources 下的子目录前缀**」，**不是** Assets 路径：
        /// 空串 = 以 Resources 根为根（本工程资源就在 <c>Resources/{MapData,UI,Sound,Art}</c> 下）。
        /// 传 <c>"Assets/Resources"</c> 会让资源模块以为要启热更、去 AppData 找内容目录，
        /// 结果**所有资源静默加载失败**（实测踩过：地图/名牌/音效全报"不存在"，而文件就在磁盘上）。
        /// </summary>
        private const string ResourceRootPrefix = "";

        /// <summary>常驻事件系统所在的场景名（引擎自建的 EventSystem 是 DontDestroyOnLoad）。</summary>
        private const string PersistentSceneName = "DontDestroyOnLoad";

        private IAppFlow _flow;
        private bool _started;

        /// <summary>常驻主实例（单实例守卫的判据；见 <see cref="Awake"/>）。</summary>
        private static Bootstrap _instance;

        private void Awake()
        {
            // 编辑器窗口失焦时也要跑帧（否则第一次点 Play 后切出去就会"卡住不动"）
            Application.runInBackground = true;

            // 单实例守卫：用**静态引用**而不是 FindObjectsByType 的返回值顺序。
            // 切场景时新场景里的 Bootstrap 会先创建，此刻旧的那个可能还没被 DontDestroyOnLoad 之外的
            // 扫描看到，靠"找到几个"判断不可靠 —— 静态引用是确定的（谁先 Awake 谁就是主实例）。
            if (_instance != null && _instance != this)
            {
                Game.Logger?.Warn(Tag,
                    $"已存在常驻 Bootstrap「{_instance.name}」(scene={_instance.gameObject.scene.name})，" +
                    $"本实例「{name}」(scene={gameObject.scene.name}) 自毁");
                Destroy(gameObject);
                return;
            }
            _instance = this;

            // 各模块组件就挂在本 GameObject 上，而菜单/舞台之间会切场景（单加载会关掉旧场景）——
            // 不常驻的话切一次场景模块全被销毁，游戏直接停摆。
            DontDestroyOnLoad(gameObject);
        }

        private void Start()
        {
            if (_started) return;       // 域重载 / 重复 Start 的守卫
            _started = true;

            // ① 引擎
            Game.Launch(new GameConfig
            {
                LogDir = "logs",
                ResourceRoot = ResourceRoot,
            });

            // ② 资源 + 输入（CloverInput 会顺便建好 EventSystem；必须在建 UI 之前）
            CloverRes.Init(ResourceRootPrefix);
            CloverInput.Init();

            // 单机版：这里**没有** CloverNet 的网络初始化。Game.Net 保持 null 是设计如此。
            Game.Logger?.Info(Tag, "单机版启动：按设计未初始化网络（不调用 CloverNet 的网络初始化）");

            // ③ 玩家设置（Game.Setting）
            var settings = CsPlayerSettingsStore.Load();
            CsPlayerSettingsStore.ApplyToEngine(settings);
            Game.Logger?.Info(Tag,
                $"玩家设置已加载并应用: name={settings.PlayerName} sens={settings.MouseSensitivity:0.##} " +
                $"fov={settings.Fov} showFps={settings.ShowFps}");

            // ④ 装配兄弟模块（全部挂在本对象上；门面由各模块自己构造）
            //
            // 顺序有讲究：**地图模块必须先于比赛模块**。MatchModule.Awake 里会自己找 ICsMap
            // （同 GameObject 上的组件），地图还没挂上时它会打 Error 并禁用自己。
            var mapModule = gameObject.AddComponent<CsMapModule>();
            var matchModule = gameObject.AddComponent<MatchModule>();
            gameObject.AddComponent<PlayerModule>();
            gameObject.AddComponent<BotModule>();
            gameObject.AddComponent<ViewModule>();
            gameObject.AddComponent<AudioModule>();

            var match = matchModule != null ? matchModule.Match : null;
            var map = mapModule != null ? mapModule.Map : null;

            // 装配完成后显式注入地图（MatchModule 文档里的"优先路径"）：这样即使它 Awake 时
            // 没扫到地图而自禁用，也会因为注入而重新启用。
            if (matchModule != null && map != null) matchModule.Inject(map);

            if (match == null) Game.Logger?.Error(Tag, "MatchModule.Match 为 null（门面还没就绪？），比赛将无法开始");
            if (map == null) Game.Logger?.Error(Tag, "CsMapModule.Map 为 null（门面还没就绪？），地图数据不会加载");

            // 场景切换后清掉"场景自带的重复 EventSystem"：引擎的 EventSystem 是常驻唯一实例
            if (Game.Scene != null) Game.Scene.OnSceneLoaded(OnSceneLoaded);

            // ⑤ 进流程（启动画面 → 主菜单；不许直接进游戏场景）
            _flow = new AppFlow(match, map);
            _flow.Enter();

            Game.Logger?.Info(Tag, $"启动链路已发起: flow={_flow.CurrentState}");
        }

        /// <summary>
        /// 每个场景加载完成后做一遍去重。
        ///
        /// <para>
        /// 为什么需要：场景模板（<c>Menu.unity</c>）里按要求留了一份 EventSystem，
        /// 而引擎的 EventSystem 是 <c>CloverInput.Init</c> 建的常驻对象（DontDestroyOnLoad）。
        /// 两份 EventSystem 并存时 uGUI 只会用 <c>EventSystem.current</c>（= 最先启用的那份），
        /// 且 Editor 会刷 "There are 2 event systems in the scene" 警告 —— 这里把场景里那份收掉。
        /// </para>
        /// </summary>
        private void OnSceneLoaded(string sceneName)
        {
            var systems = FindObjectsByType<EventSystem>(FindObjectsSortMode.None);
            if (systems.Length <= 1) return;

            var persistentCount = 0;
            for (var i = 0; i < systems.Length; i++)
            {
                if (systems[i] != null && systems[i].gameObject.scene.name == PersistentSceneName)
                    persistentCount++;
            }

            if (persistentCount == 0)
            {
                Game.Logger?.Error(Tag,
                    $"场景 {sceneName} 里有 {systems.Length} 个 EventSystem 且没有常驻实例；不敢乱删，请检查 CloverInput.Init 是否被调用");
                return;
            }

            for (var i = 0; i < systems.Length; i++)
            {
                var es = systems[i];
                if (es == null) continue;
                if (es.gameObject.scene.name == PersistentSceneName) continue;

                Game.Logger?.Warn(Tag, $"场景 {sceneName} 自带重复 EventSystem「{es.name}」，已移除（常驻实例唯一）");
                Destroy(es.gameObject);
            }
        }
    }
}
