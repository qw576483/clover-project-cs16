using CloverEngine;
using Cs16.Core;
using Cs16.Module.Map;
using UnityEngine;

namespace Cs16.Module.Match
{
    /// <summary>
    /// 比赛模拟的 MonoBehaviour 宿主：把 <see cref="CsMatch"/> 挂进 Unity 帧循环，并负责拿到
    /// <see cref="ICsMap"/>（App 注入优先，其次同级 GameObject 上的 <see cref="CsMapModule"/>）。
    ///
    /// <para><b>装配方式</b>：把本组件挂到 Stage 场景的任意常驻 GameObject 上即可。它会自己订阅
    /// <see cref="Events.LaunchMatch"/>（由 Flow 在读条完成后发出）并开局 —— 这样 UI/Flow 不需要
    /// <c>FindObjectOfType</c> 就能驱动比赛（分层铁律：模块之间只经接口/事件协作）。</para>
    /// </summary>
    public sealed class MatchModule : MonoBehaviour
    {
        private const string Tag = CsMatch.Tag;

        /// <summary>
        /// 当前活着的比赛模块（静止引用；由 <see cref="Awake"/> 赋值、<see cref="OnDestroy"/> 清空）。
        ///
        /// <para><b>为什么需要它</b>：本组件的挂载点是 <c>Bootstrap</c> 那个 <c>DontDestroyOnLoad</c> 的
        /// GameObject，而 <c>Bootstrap</c> 只存在于 <c>Scenes/Boot.unity</c>。任何"从外部按类型找它"的代码
        /// 又会在"没从 Boot 场景进 Play"时**静默返回 null** —— 实测代价：<c>.ai-tmp/drivers/cs16-play-driver.cs</c>
        /// 的 <c>Apply()</c> 因此无声早退，pause / godmode / cam / input 全部不生效，
        /// 整场驱动看起来"挂了"却没有任何日志说明原因。</para>
        ///
        /// <para>所以对外只暴露这一个入口（与 <c>Bootstrap</c> 的单实例守卫同一套做法）：
        /// 取不到就是 <c>null</c>，取用方必须自己判空并**留痕**，不许静默继续。</para>
        /// </summary>
        public static MatchModule Instance { get; private set; }

        [Tooltip("勾上时本组件在自己 Awake 时记一条日志（调试用）。")]
        [SerializeField] private bool _verbose;

        private CsMatch _match;
        private ICsMap _injectedMap;
        private bool _warnedNoMap;
        private bool _subscribed;

        /// <summary>
        /// 对外只读门面：其余模块一律通过它访问比赛状态。
        /// **懒创建兜底**：门面只应在 <c>OnDestroy</c> 里被置空；若这里发现它为空（例如脚本执行顺序
        /// 让 <c>Awake</c> 没跑到、或组件曾被重建），就地补一个并留日志 —— 否则所有下游模块每帧 NRE。
        /// </summary>
        public ICsMatch Match
        {
            get
            {
                if (_match == null)
                {
                    _match = new CsMatch(ResolveMap());
                    Game.Logger.Warn(Tag,
                        $"Match 门面为空时被访问，已懒创建（go={gameObject.name} scene={gameObject.scene.name}）");
                }
                return _match;
            }
        }

        /// <summary>当前地图门面（可能为 null —— 没拿到时已打过 Error 并禁用自己）。</summary>
        public ICsMap Map => _match != null ? _match.Map : null;

        // ==================================================================
        //  生命周期
        // ==================================================================
        private void Awake()
        {
            Game.Logger.Info(Tag, $"MatchModule.Awake: go={gameObject.name} scene={gameObject.scene.name}");

            Instance = this;

            _match = new CsMatch(ResolveMap());

            if (_match.Map == null)
            {
                Game.Logger.Error(Tag,
                    "MatchModule 拿不到 ICsMap：既没有被 App 注入，同级 GameObject 上也没有可用的地图模块。" +
                    "比赛模拟已被禁用 —— 请让 Bootstrap 注入，或按契约把 CsMapModule 挂到本 GameObject 上。");
                _warnedNoMap = true;
                enabled = false;
            }

            Subscribe();

            if (_verbose)
            {
                Game.Logger.Info(Tag, $"MatchModule.Awake：地图={(Map != null ? Map.MapName : "null")}");
            }
        }

        private void OnDestroy()
        {
            Game.Logger.Warn(Tag,
                $"MatchModule.OnDestroy: go={gameObject.name} scene={gameObject.scene.name} —— 门面即将被置空");
            if (Instance == this) Instance = null;
            Unsubscribe();
            _match?.Stop();
            _match = null;

            // 差异 #88 第②段：本模块是局域网客户端与远端视图的泵主 ⇒ 收尾时要一起停掉，
            //   否则线程会成为"没人管的后台线程"、远端角色会跟着常驻对象活到下一次进图。
            StopLanClientAndViews("MatchModule 销毁");
        }

        /// <summary>停局域网客户端 + 回收全部远端视图（幂等；两处调用点共用，不各写一套）。</summary>
        private void StopLanClientAndViews(string why)
        {
            if (Cs16.Module.Net.CsLanClient.IsRunning)
            {
                Game.Logger.Info(Tag, why + "：停止局域网客户端（" + Cs16.Module.Net.CsLanClient.Describe() + "）");
            }
            Cs16.Module.Net.CsLanClient.Stop();
            Cs16.Module.View.CsLanRemoteView.RecycleAll();
        }

        private void Update()
        {
            // 时钟注入点（全工程**唯一**一处调 `CsClock.Drive`）：本组件是引擎帧循环与业务之间
            //   的那一层，把引擎 Tick 的 dt 交给 `Core/CsClock`，模拟 / 战斗 / 玩家三侧再从
            //   `CsClock.Delta` 取同一个值（理由见 CsClock 的类注释：三处各自读 Time.deltaTime /
            //   Time.time 会把可复现性打死）。别在这里改成别的时钟源 —— 换时钟请注入 CsClock。
            CsClock.Drive();
            var dt = CsClock.Delta;

            // Tick 内部自己判 IsRunning / IsPaused；未开局时是廉价空转。
            _match?.Tick(dt);

            // 差异 #88「能开局」：局域网网关的快照泵。**必须在这里（主线程）**—— 网关只做
            // "读模拟 + 拼一行 JSON + 入队"，真正的 socket 写在它自己的后台线程里
            // （理由见 CsLanGateway 的线程模型注释）。未开网关时这一行是一次 bool 判断。
            if (Cs16.Module.Net.CsLanGateway.IsRunning)
            {
                Cs16.Module.Net.CsLanGateway.Pump(_match, dt);
            }

            //   主线程这里只做两件轻活：
            //   ① CsLanClient.Pump()：吐后台日志 + 纠偏"线程死了状态还在跑"（socket 归它的读线程，
            //      主线程一次都不读 —— 见 CsLanClient 的线程模型注释）；
            //   ② CsLanRemoteView.SyncAll()：让场上的远端视图与最新一帧快照对齐（新建成 / 有的刷 /
            //      快照里没有的回收）。客户端没在跑时两者都是"廉价空转 + 一次回收"，不会每帧扫字典。
            //   这两行**不在** `IsRunning` 判断里：客户端停掉时也要靠 SyncAll 把远端角色收干净。
            Cs16.Module.Net.CsLanClient.Pump();
            Cs16.Module.View.CsLanRemoteView.SyncAll();
        }

        // ==================================================================
        //  地图注入
        // ==================================================================
        /// <summary>由 Bootstrap 在装配完成后注入（优先路径）。后注入也会即时生效。</summary>
        public void Inject(ICsMap map)
        {
            if (map == null)
            {
                Game.Logger.Warn(Tag, "MatchModule.Inject(null)：注入被忽略");
                return;
            }

            _injectedMap = map;
            if (_match != null) _match.SetMap(map);

            if (!enabled)
            {
                enabled = true;
                Game.Logger.Info(Tag, "MatchModule 因 App 注入 ICsMap 而重新启用");
            }
            Game.Logger.Info(Tag, $"MatchModule 已注入地图：{map.MapName}");
        }

        private ICsMap ResolveMap()
        {
            if (_injectedMap != null) return _injectedMap;

            // ① 类型无关的扫描：任何实现了 ICsMap 的 MonoBehaviour 都收（不依赖具体类名）。
            var behaviours = GetComponents<MonoBehaviour>();
            for (var i = 0; i < behaviours.Length; i++)
            {
                if (behaviours[i] is ICsMap onSelf) return onSelf;
            }
            var child = GetComponentsInChildren<MonoBehaviour>(true);
            for (var i = 0; i < child.Length; i++)
            {
                if (child[i] is ICsMap onChild) return onChild;
            }

            var module = GetComponent<CsMapModule>();
            if (module != null && module.Map != null) return module.Map;

            return null;
        }

        // ==================================================================
        //  事件接线（UI / Flow 只发事件，不引用 Module）
        // ==================================================================
        private void Subscribe()
        {
            if (_subscribed) return;
            var bus = Game.Event;
            if (bus == null)
            {
                Game.Logger.Warn(Tag, "Game.Event 为空（引擎未 Launch？），MatchModule 未能订阅事件");
                return;
            }

            // 开局权归 Flow 独占：Flow 在「场景 + 地图都就绪」后才调 MatchModule.Match.Start(cfg)。
            //   这里**不再**订阅 Events.LaunchMatch —— 否则地图还没加载完就会被提前 Start 一次（并打一条无谓的 Error）。
            bus.On<CsBotDifficulty>(Events.AddBot, OnAddBot);
            bus.On(Events.KickBot, OnKickBot);
            bus.On<CsBotDifficulty>(Events.SetBotDifficulty, OnSetBotDifficulty);
            bus.On(Events.RestartRound, OnRestartRound);
            bus.On(Events.RestartMatch, OnRestartMatch);
            bus.On(Events.TogglePause, OnTogglePause);
            bus.On(Events.SpectateNext, OnSpectateNext);
            bus.On<CsTeam>(Events.ChangeTeam, OnChangeTeam);
            bus.On<string>(Events.BuyWeapon, OnBuyWeapon);
            bus.On<string>(Events.RadioCommand, OnRadioCommand);
            bus.On<string>(Events.SwitchMap, OnSwitchMap);
            bus.On(Events.Disconnect, OnDisconnect);

            _subscribed = true;
        }

        private void Unsubscribe()
        {
            if (!_subscribed) return;
            var bus = Game.Event;
            if (bus == null)
            {
                _subscribed = false;
                return;
            }

            bus.Off<CsBotDifficulty>(Events.AddBot, OnAddBot);
            bus.Off(Events.KickBot, OnKickBot);
            bus.Off<CsBotDifficulty>(Events.SetBotDifficulty, OnSetBotDifficulty);
            bus.Off(Events.RestartRound, OnRestartRound);
            bus.Off(Events.RestartMatch, OnRestartMatch);
            bus.Off(Events.TogglePause, OnTogglePause);
            bus.Off(Events.SpectateNext, OnSpectateNext);
            bus.Off<CsTeam>(Events.ChangeTeam, OnChangeTeam);
            bus.Off<string>(Events.BuyWeapon, OnBuyWeapon);
            bus.Off<string>(Events.RadioCommand, OnRadioCommand);
            bus.Off<string>(Events.SwitchMap, OnSwitchMap);
            bus.Off(Events.Disconnect, OnDisconnect);

            _subscribed = false;
        }

        // ==================================================================
        //  事件处理
        // ==================================================================
        private void OnLaunchMatch(CsMatchConfig cfg)
        {
            if (cfg == null)
            {
                Game.Logger.Error(Tag, $"{Events.LaunchMatch} 收到 null 配置，开局被取消");
                return;
            }

            if (_match == null) _match = new CsMatch(ResolveMap());

            if (_match.Map == null)
            {
                var again = ResolveMap();
                if (again != null)
                {
                    _match.SetMap(again);
                    enabled = true;
                }
            }

            if (_match.Map == null)
            {
                Game.Logger.Error(Tag,
                    $"开局失败：{Events.LaunchMatch} 到达时仍拿不到 ICsMap（地图未加载/未挂载地图模块）");
                enabled = false;
                return;
            }

            if (!enabled) enabled = true;
            _match.Start(cfg);
        }

        private void OnAddBot(CsBotDifficulty diff)
        {
            if (!EnsureMatch()) return;

            // 加到人少的一队（对应 G1 的 mp_autoteambalance）。
            var t = Match.PlayerCount(CsTeam.T);
            var ct = Match.PlayerCount(CsTeam.CT);
            var team = t <= ct ? CsTeam.T : CsTeam.CT;

            // 证据链的中间跳：UI（H 菜单 / 控制台）→ 事件总线 → 本方法 → CsMatch.AddBot → CsBotBrain。
            //   这条日志（含事件里收到的难度 + 当前人数 + 判定出来的阵营）能把链条钉死。
            Game.Logger.Info(Tag, $"{Events.AddBot} 收到难度={diff}（当前人数 T={t} CT={ct}）→ 加到 {team}");

            Match.AddBot(team, diff);
        }

        private void OnKickBot()
        {
            if (!EnsureMatch()) return;
            Match.KickBot();
        }

        private void OnSetBotDifficulty(CsBotDifficulty diff)
        {
            if (!EnsureMatch()) return;
            Match.SetBotDifficulty(diff);
        }

        private void OnRestartRound()
        {
            if (!EnsureMatch()) return;
            Match.RestartRound();
        }

        private void OnRestartMatch()
        {
            if (!EnsureMatch()) return;
            Match.RestartMatch();
        }

        private void OnTogglePause()
        {
            if (!EnsureMatch()) return;
            Match.SetPaused(!Match.IsPaused);
        }

        /// <summary>
        /// 观战切目标（键位归 <c>Module/Player</c> —— UI 不许直接调 Module，见 <c>Events.SpectateNext</c>）。
        ///
        /// <para>方向固定 +1（官方 CS 的"下一个观战目标"）。非观战状态按下时静默丢会给玩家"按键坏了"的错觉，
        /// 所以这里显式记一条 Info（<c>CsMatch.SpectateNext</c> 自己那条是降频日志，不保证每次都有）。</para>
        /// </summary>
        private void OnSpectateNext()
        {
            if (!EnsureMatch()) return;

            if (!_match.IsSpectating)
            {
                Game.Logger.Info(Tag,
                    $"{Events.SpectateNext} 到达，但本地玩家当前不是观战状态（还活着 / 比赛未运行），已忽略");
                return;
            }

            _match.SpectateNext(1);
        }

        private void OnChangeTeam(CsTeam team)
        {
            if (!EnsureMatch()) return;
            Match.ChangeTeam(team);
        }

        private void OnBuyWeapon(string weaponId)
        {
            if (!EnsureMatch()) return;
            if (string.IsNullOrEmpty(weaponId))
            {
                Game.Logger.Warn(Tag, $"{Events.BuyWeapon} 收到空武器 id，已忽略");
                return;
            }
            Match.TryBuy(weaponId, out _);
        }

        private void OnRadioCommand(string text)
        {
            if (_match == null) return;
            if (string.IsNullOrEmpty(text)) return;
            _match.PublishMessage(Match.LocalPlayer, text);
        }

        private void OnSwitchMap(string mapName)
        {
            // 换图 = 重新加载 Stage 场景，属 Flow 的职责；这里只把"收到但未处理"讲清楚，不静默。
            Game.Logger.Warn(Tag,
                $"收到换图请求 '{mapName}'，但换图需要 Flow 重新加载 Stage 场景（本组件不做场景切换），已忽略");
        }

        private void OnDisconnect()
        {
            // 差异 #88 第②段：断开请求 = 退出这一局 ⇒ 局域网客户端也要停（否则远端角色会留在场上，
            //   "我已经退出了却还看得见别人"）。
            StopLanClientAndViews("收到断开请求");

            if (_match == null || !_match.IsRunning) return;
            Game.Logger.Info(Tag, "收到断开请求，停止比赛模拟");
            _match.Stop();
        }

        private bool EnsureMatch()
        {
            if (_match == null)
            {
                Game.Logger.Warn(Tag, "请求到达时比赛尚未创建，已忽略");
                return false;
            }
            if (_match.Map == null)
            {
                if (!_warnedNoMap)
                {
                    _warnedNoMap = true;
                    Game.Logger.Error(Tag, "比赛未绑定地图，请求被忽略（本条只报一次）");
                }
                return false;
            }
            return true;
        }
    }
}
