using System.Collections.Generic;
using CloverEngine;
using Cs16.Core;
using Cs16.Module.Map;
using Cs16.Module.Match;
using Cs16.UI;
using UnityEngine;

namespace Cs16.Module.Flow
{
    /// <summary>
    /// 启动与菜单流程编排（<see cref="IAppFlow"/> 的唯一实现）。
    ///
    /// <para><b>职责边界</b>：状态机只标"我在哪个站点"，**面板与场景的开关全部写在这里**；
    /// 面板自己不发号施令（UI 只发 <c>Events.*</c>，本类订阅后驱动）。</para>
    ///
    /// <para><b>场景策略</b>：菜单类站点共用一个纯 UI 场景 <c>Menu</c>（切面板不切场景，否则切界面要等读条）；
    /// 只有进图时单加载 <c>StageDust2</c>，回主菜单时先卸舞台再加载 Menu。</para>
    ///
    /// <para><b>单机版</b>：全程不碰 <c>Game.Net</c>（按设计不调用 <c>CloverNet 的网络初始化</c>）。</para>
    /// </summary>
    public sealed class AppFlow : IAppFlow
    {
        private const string Tag = "Flow";

        /// <summary>舞台内定时器统一打的 scope 名（回主菜单时按它批量停）。</summary>
        private const string StageScope = "stage";

        /// <summary>引擎 <c>SceneModule</c> 在 <c>allowSceneActivation</c> 前把进度封顶在 0.9，这里归一化到 0~1。</summary>
        private const float SceneLoadProgressCeiling = 0.9f;

        /// <summary>场景/面板没按预期就绪时的兜底延时（秒，不受 timeScale 影响）。</summary>
        private const float MenuFallbackDelay = 1.5f;

        /// <summary>读条超时（秒）：场景或地图迟迟不就绪时放弃本次进图并回主菜单，绝不把玩家钉在读条屏。</summary>
        private const float StageLoadTimeout = 20f;

        /// <summary>站点名。</summary>
        private static class State
        {
            public const string Boot = "Boot";
            public const string MainMenu = "MainMenu";
            public const string ServerList = "ServerList";
            public const string NewGame = "NewGame";
            public const string Options = "Options";
            public const string TeamSelect = "TeamSelect";
            public const string Loading = "Loading";
            public const string Stage = "Stage";
            public const string Pause = "Pause";
        }

        /// <summary>转换 trigger（<c>AddTransition</c> 用）。</summary>
        private static class Trigger
        {
            public const string BootDone = "BootDone";
            public const string OpenServers = "OpenServers";
            public const string OpenOptions = "OpenOptions";
            public const string NewGame = "NewGame";
            public const string Loading = "Loading";
            public const string StageReady = "StageReady";
            public const string StageBegin = "StageBegin";
            public const string Pause = "Pause";
            public const string Resume = "Resume";
            public const string ToMain = "ToMain";
        }

        private readonly ICsMatch _match;
        private readonly ICsMap _map;

        /// <summary>当前已打开的面板名（用于"ESC 归谁处理"的判断；UI 层属性，不引用任何 Module 类型）。</summary>
        private readonly HashSet<string> _openPanels = new HashSet<string>();

        /// <summary>常驻 HUD 的面板名 —— 它开着不算"遮挡"，ESC 仍然归暂停菜单。</summary>
        private readonly string _hudPanelName = typeof(HudPanel).Name;

        private CsMatchConfig _pending;
        private bool _optionsFromPause;
        private bool _stageSceneReady;
        private bool _stageMapReady;
        private bool _menuLoadRequested;

        /// <summary>本场是否已经在 Stage 站点开过局（用来区分"首次进图"与"从暂停恢复"）。</summary>
        private bool _stageBegun;

        /// <summary>主菜单启动曲是否已经播过（原版口径：只在整个进程的**首次**主菜单播一次）。</summary>
        private bool _startupMusicPlayed;

        public string CurrentState => Game.Fsm != null ? Game.Fsm.Current : null;

        /// <param name="match">比赛门面（<c>MatchModule.Match</c>）。</param>
        /// <param name="map">地图门面（<c>CsMapModule.Map</c>）。</param>
        public AppFlow(ICsMatch match, ICsMap map)
        {
            _match = match;
            _map = map;

            if (_match == null) Game.Logger?.Error(Tag, "注入的 ICsMatch 为 null：比赛将无法开始（MatchModule 门面未就绪？）");
            if (_map == null) Game.Logger?.Error(Tag, "注入的 ICsMap 为 null：地图数据不会加载，进图后空间查询会退化（CsMapModule 门面未就绪？）");
        }

        // ═══════════════════════════════ 启动 ═══════════════════════════════

        public void Enter()
        {
            if (Game.Fsm == null || Game.UI == null || Game.Event == null)
            {
                Game.Logger?.Error(Tag, "引擎未就绪（Game.Fsm / Game.UI / Game.Event 为 null），流程无法启动；请确认先调 Game.Launch");
                return;
            }

            RegisterStates();
            SubscribeEvents();

            Game.Fsm.Force(State.Boot);
            Game.Logger?.Info(Tag, $"启动链路已进入 {State.Boot} 站点");
        }

        private void RegisterStates()
        {
            Game.Fsm.RegisterState(State.Boot, OnEnterBoot, null, OnExitBoot);
            Game.Fsm.RegisterState(State.MainMenu, OnEnterMainMenu, null, OnExitMainMenu);
            Game.Fsm.RegisterState(State.ServerList, OnEnterServerList, OnTickServerList, OnExitServerList);
            Game.Fsm.RegisterState(State.NewGame, OnEnterNewGame, OnTickNewGame, OnExitNewGame);
            Game.Fsm.RegisterState(State.Options, OnEnterOptions, OnTickOptions, OnExitOptions);
            Game.Fsm.RegisterState(State.TeamSelect, OnEnterTeamSelect, OnTickTeamSelect, OnExitTeamSelect);
            Game.Fsm.RegisterState(State.Loading, OnEnterLoading, null, OnExitLoading);
            Game.Fsm.RegisterState(State.Stage, OnEnterStage, OnTickStage, null);
            Game.Fsm.RegisterState(State.Pause, OnEnterPause, OnTickPause, OnExitPause);

            Game.Fsm.AddTransition(Trigger.BootDone, State.MainMenu);
            Game.Fsm.AddTransition(Trigger.OpenServers, State.ServerList);
            Game.Fsm.AddTransition(Trigger.OpenOptions, State.Options);
            Game.Fsm.AddTransition(Trigger.NewGame, State.NewGame);
            Game.Fsm.AddTransition(Trigger.Loading, State.Loading);
            Game.Fsm.AddTransition(Trigger.StageReady, State.TeamSelect);
            Game.Fsm.AddTransition(Trigger.StageBegin, State.Stage);
            Game.Fsm.AddTransition(Trigger.Pause, State.Pause);
            Game.Fsm.AddTransition(Trigger.Resume, State.Stage);
            Game.Fsm.AddTransition(Trigger.ToMain, State.MainMenu);

            // 暂停/恢复比赛：只有真正进出"暂停族（Pause / 从暂停进来的 Options）"才动 SetPaused
            Game.Fsm.OnChange(OnFsmChanged);
        }

        /// <summary>
        /// 订阅流程事件。**一律用具名方法**：<c>Game.Event.Off</c> 没有句柄、必须传同一方法引用，
        /// 写匿名 lambda 就 Off 不掉。本类与 App 同生命周期（不需要 Off）。
        /// </summary>
        private void SubscribeEvents()
        {
            var bus = Game.Event;
            bus.On(Events.StartNewGame, OnStartNewGame);
            bus.On(Events.OpenServerList, OnOpenServerList);
            bus.On(Events.OpenOptions, OnOpenOptions);
            bus.On(Events.QuitGame, OnQuitGame);
            bus.On(Events.BackToMain, OnBackToMain);
            bus.On(Events.Disconnect, OnDisconnect);
            bus.On(Events.Resume, OnResumeRequested);
            bus.On(Events.RequestPause, OnRequestPause);
            bus.On<CsMatchConfig>(Events.LaunchMatch, OnLaunchMatch);
            bus.On<CsTeam>(Events.TeamChosen, OnTeamChosen);

            Game.UI.OnPanelOpened(OnPanelOpened);
            Game.UI.OnPanelClosed(OnPanelClosed);

            Game.Logger?.Info(Tag, "流程事件已订阅（StartNewGame/OpenServerList/OpenOptions/QuitGame/BackToMain/Disconnect/Resume/RequestPause/LaunchMatch/TeamChosen）");
        }

        // ═══════════════════════════════ 站点：Boot / MainMenu ═══════════════════════════════

        private void OnEnterBoot()
        {
            Game.UI.Open<BootPanel>();
            Game.Logger?.Info(Tag, $"Boot 站点：启动画面已开（当前场景 {DescribeScene()}）");
        }

        private void OnExitBoot()
        {
            Game.UI.Close<BootPanel>();
        }

        private void OnEnterMainMenu()
        {
            EnterMenuScene();
        }

        private void OnExitMainMenu()
        {
            Game.UI.Close<MainMenuPanel>();
        }

        /// <summary>
        /// 菜单类站点统一住在 <c>Menu</c> 纯 UI 场景：已在就不重复加载。
        /// 从舞台回来时要先把舞台卸掉（它是重场景，留着会残留并且单加载会把它一起关掉）。
        /// </summary>
        private void EnterMenuScene()
        {
            if (Game.Scene == null)
            {
                Game.Logger?.Error(Tag, "Game.Scene 为 null，无法加载 Menu 场景；直接用面板兜底");
                Game.UI.Open<MainMenuPanel>();
                PlayStartupMusicOnce();
                return;
            }

            var current = Game.Scene.CurrentScene;
            if (current == SceneNames.Menu)
            {
                OnMenuSceneReady();
                return;
            }

            if (current == SceneNames.StageDust2)
            {
                _menuLoadRequested = false;
                Game.Logger?.Info(Tag, $"Menu 站点：先卸载舞台场景 {SceneNames.StageDust2}");
                Game.Scene.Unload(SceneNames.StageDust2, OnStageUnloaded);
                // 兜底：Unload 在极端情况下只告警不回调（引擎 SceneModule.Unload 的实现如此），
                // 那样玩家会停在空场景且没有主菜单 —— 必须有后续动作。
                Game.Timer.AfterUnscaled(MenuFallbackDelay, OnMenuFallback);
                return;
            }

            LoadMenuScene();
        }

        private void OnStageUnloaded()
        {
            Game.Logger?.Info(Tag, $"舞台场景已卸载（当前场景 {DescribeScene()}）");
            LoadMenuScene();
        }

        private void LoadMenuScene()
        {
            if (_menuLoadRequested) return;
            _menuLoadRequested = true;

            // 场景不在 Build Settings 时引擎自己会打 Error（SceneModule.Load 的 "Scene not found"），
            // 这里不再重复探测；加载不回调的情况由 OnMenuFallback 兜底。
            Game.Scene.Load(SceneNames.Menu, null, OnMenuSceneReady);
        }

        private void OnMenuFallback()
        {
            if (Game.UI.IsOpen<MainMenuPanel>()) return;
            if (CurrentState != State.MainMenu) return;

            var current = DescribeScene();
            if (_menuLoadRequested)
            {
                Game.Logger?.Warn(Tag, $"主菜单 {MenuFallbackDelay}s 内未就绪（CurrentScene={current}），兜底直接开面板");
                Game.UI.Open<MainMenuPanel>();
                PlayStartupMusicOnce();
                return;
            }

            Game.Logger?.Warn(Tag, $"舞台卸载未回调（CurrentScene={current}），兜底单加载 Menu 场景");
            LoadMenuScene();
            Game.Timer.AfterUnscaled(MenuFallbackDelay, OnMenuFallback);
        }

        private void OnMenuSceneReady()
        {
            _menuLoadRequested = false;
            Game.Logger?.Info(Tag, $"Menu 场景就绪：{SceneNames.Menu}");
            Game.UI.Open<MainMenuPanel>();
            PlayStartupMusicOnce();
        }

        // ═══════════════════════════ 主菜单启动曲（原版 media/gamestartup.mp3）═══════════════════════════
        //
        // 原版口径（GoldSrc）：引擎在**主菜单首次出现**时播放 <gamedir>/media/gamestartup.mp3
        // （CS 1.6 即 cstrike/media/gamestartup.mp3），**开始连接地图后停止**，回主菜单**不重播**；
        // MP3 音量由设置里的 "MP3 Volume" 控制（本工程 = SoundGroup.BGM，见 CsPlayerSettingsStore）。
        // 本工程沿用同名文件：Resources/Sound/BGM/gamestartup.mp3（引擎 PlayBGM 拼 Sound/BGM/{name}）。

        /// <summary>
        /// 播主菜单启动曲：**整个进程只播一次**（三个"打开主菜单"的路径都调用它，由 <see cref="_startupMusicPlayed"/>
        /// 去重）。缺 <c>Game.Sound</c>（表现域未挂载）时只 Warn 一次，绝不静默。
        /// </summary>
        private void PlayStartupMusicOnce()
        {
            if (_startupMusicPlayed) return;
            _startupMusicPlayed = true;

            var sound = Game.Sound;
            if (sound == null)
            {
                Game.Logger?.Warn(Tag, $"Game.Sound 为 null（表现域未挂载？），主菜单启动曲不会播放：{ResPaths.BgmGameStartup}");
                return;
            }

            sound.PlayBGM(ResPaths.BgmGameStartup);
            Game.Logger?.Info(Tag,
                $"主菜单启动曲已播放：Sound/BGM/{ResPaths.BgmGameStartup}（原版 media/gamestartup.mp3 同名文件，音量走 SoundGroup.BGM）");
        }

        /// <summary>
        /// 停主菜单启动曲：进图（原版 = 开始连接地图）时停。⛔ 回主菜单**不重播**（<see cref="PlayStartupMusicOnce"/>
        /// 只认首次），与原版一致。
        /// </summary>
        private void StopStartupMusic()
        {
            var sound = Game.Sound;
            if (sound == null) return;

            sound.StopBGM();
            Game.Logger?.Info(Tag, "进图：主菜单启动曲已停止");
        }

        // ═══════════════════════════════ 站点：ServerList / NewGame / Options ═══════════════════════════════

        private void OnEnterServerList()
        {
            Game.UI.Open<ServerListPanel>();
        }

        /// <summary>面板被玩家"Back"关掉后，站点自己退回主菜单（面板只管关自己，不切状态机）。</summary>
        private void OnTickServerList(float dt)
        {
            if (Game.UI.IsOpen<ServerListPanel>()) return;
            Game.Logger?.Info(Tag, "服务器列表面板已关闭 → 回主菜单站点");
            Game.Fsm.Transition(State.MainMenu);
        }

        private void OnExitServerList()
        {
            Game.UI.Close<ServerListPanel>();
        }

        private void OnEnterNewGame()
        {
            Game.UI.Open<NewGamePanel>(_pending);
        }

        private void OnTickNewGame(float dt)
        {
            if (Game.UI.IsOpen<NewGamePanel>()) return;
            Game.Logger?.Info(Tag, "New Game 面板已关闭 → 回主菜单站点");
            Game.Fsm.Transition(State.MainMenu);
        }

        private void OnExitNewGame()
        {
            Game.UI.Close<NewGamePanel>();
        }

        private void OnEnterOptions()
        {
            Game.UI.Open<OptionsPanel>();
        }

        private void OnTickOptions(float dt)
        {
            if (Game.UI.IsOpen<OptionsPanel>()) return;
            var back = _optionsFromPause ? State.Pause : State.MainMenu;
            Game.Logger?.Info(Tag, $"Options 面板已关闭 → 回 {back} 站点");
            Game.Fsm.Transition(back);
        }

        private void OnExitOptions()
        {
            Game.UI.Close<OptionsPanel>();
        }

        // ═══════════════════════════════ 站点：Loading / TeamSelect / Stage / Pause ═══════════════════════════════

        public void GoStage(CsMatchConfig cfg)
        {
            if (cfg == null)
            {
                Game.Logger?.Error(Tag, "GoStage 收到 null 配置，已忽略（不会进图）");
                return;
            }

            if (Game.Scene == null || Game.UI == null)
            {
                Game.Logger?.Error(Tag, "引擎表现域未就绪（Game.Scene / Game.UI 为 null），无法进图");
                return;
            }

            _pending = cfg.Clone();
            _stageSceneReady = false;
            _stageMapReady = false;
            _stageBegun = false;

            Game.Logger?.Info(Tag,
                $"GoStage: map={_pending.MapName} bots/队={_pending.BotsPerTeam} diff={_pending.BotDifficulty} " +
                $"rounds/半场={_pending.RoundsPerHalf} money={_pending.StartMoney} ff={_pending.FriendlyFire} team={_pending.PlayerTeam}");

            StopStartupMusic();                          // 原版：开始连接地图后主菜单音乐停
            Game.Fsm.Trigger(Trigger.Loading);          // → Loading 站点（自动开读条面板）

            StartMapLoad();
            // 场景不在 Build Settings / 未生成时引擎会打 Error 且**不会回调 onDone**，
            // 所以必须有超时兜底（否则玩家被永久钉在读条屏）。
            Game.Scene.Load(SceneNames.StageDust2, OnStageLoadProgress, OnStageSceneLoaded);
            Game.Timer.AfterUnscaled(StageLoadTimeout, OnStageLoadTimeout);
        }

        /// <summary>读条超时兜底：放弃本次进图，回主菜单并明确告知玩家（不静默、不卡死）。</summary>
        private void OnStageLoadTimeout()
        {
            if (CurrentState != State.Loading) return;

            Game.Logger?.Error(Tag,
                $"读条 {StageLoadTimeout:0}s 超时（sceneReady={_stageSceneReady} mapReady={_stageMapReady}），" +
                $"放弃本次进图并回主菜单（检查 {SceneNames.StageDust2}.unity 是否已生成并加入 Build Settings）");
            Game.UI.Toast($"加载 {SceneNames.StageDust2} 失败或超时，已返回主菜单", 3f);
            GoMainMenu();
        }

        private void OnEnterLoading()
        {
            var mapName = _pending != null ? _pending.MapName : CsConst.MapDust2;
            Game.UI.Open<LoadingPanel>(mapName);
            Game.Logger?.Info(Tag, $"读条开始：scene={SceneNames.StageDust2} map={mapName}");
        }

        private void OnExitLoading()
        {
            Game.UI.Close<LoadingPanel>();
        }

        private void OnStageLoadProgress(float progress)
        {
            var panel = Game.UI.Get<LoadingPanel>();
            if (panel == null)
            {
                Game.Logger?.Warn(Tag, "读条面板不在了，进度无法显示（可能是被提前关闭）");
                return;
            }

            // 引擎给的是 Unity 的 op.progress，在 allowSceneActivation 之前封顶 0.9 → 归一化到 0~1
            panel.SetProgress(Mathf.Clamp01(progress / SceneLoadProgressCeiling));
        }

        private void OnStageSceneLoaded()
        {
            _stageSceneReady = true;
            Game.Logger?.Info(Tag, $"舞台场景加载完成：{SceneNames.StageDust2}");
            TryFinishStageLoad();
        }

        private void StartMapLoad()
        {
            if (_map == null)
            {
                // 地图门面没拿到 = 这次进图的"空间事实"是空的，比赛跑不起来（不是静默降级）
                Game.Logger?.Error(Tag, "地图门面为 null，跳过地图加载：进图后 WalkableAt/出生点都会是退化值");
                _stageMapReady = true;
                return;
            }

            var want = _pending != null ? _pending.MapName : CsConst.MapDust2;
            if (_map.IsLoaded)
            {
                if (_map.MapName == want)
                {
                    _stageMapReady = true;
                    Game.Logger?.Info(Tag, $"地图数据已加载且同名（{want}），跳过重复加载");
                    return;
                }

                Game.Logger?.Warn(Tag, $"已加载地图 {_map.MapName} 与请求的 {want} 不一致，先卸载再加载");
                _map.Unload();
            }

            Game.Logger?.Info(Tag, $"加载地图数据：{ResPaths.MapDust2}");
            _map.LoadAsync(ResPaths.MapDust2, OnMapLoaded, OnMapLoadFailed);
        }

        private void OnMapLoaded()
        {
            _stageMapReady = true;
            Game.Logger?.Info(Tag, "地图数据加载完成");
            TryFinishStageLoad();
        }

        private void OnMapLoadFailed(string error)
        {
            // 地图缺失不该把玩家永久卡在读条屏：记错误日志后照常进图，
            // 空间查询会退化（引擎语义：未加载时 WalkableAt 返回 true），比赛侧会再报错。
            Game.Logger?.Error(Tag, $"地图数据加载失败：{error}（继续进图，空间查询将退化）");
            _stageMapReady = true;
            TryFinishStageLoad();
        }

        /// <summary>场景与地图都就绪才结束读条 —— 这样进度条走完时图真的能跑。</summary>
        private void TryFinishStageLoad()
        {
            if (!_stageSceneReady || !_stageMapReady) return;
            if (CurrentState != State.Loading)
            {
                Game.Logger?.Warn(Tag, $"读条完成但当前不在 Loading 站点（{CurrentState}），忽略");
                return;
            }

            var panel = Game.UI.Get<LoadingPanel>();
            if (panel != null) panel.SetProgress(1f);

            Game.Fsm.Trigger(Trigger.StageReady);       // → TeamSelect 站点（读条面板随之关闭）
        }

        private void OnEnterTeamSelect()
        {
            Game.UI.Open<TeamSelectPanel>();
            Game.Logger?.Info(Tag, "选阵营站点：等待玩家选边（选完即 _match.Start）");
        }

        private void OnTickTeamSelect(float dt)
        {
            if (Game.UI.IsOpen<TeamSelectPanel>()) return;
            Game.Logger?.Info(Tag, "选阵营被取消 → 回主菜单站点");
            Game.Fsm.Transition(State.MainMenu);
        }

        private void OnExitTeamSelect()
        {
            Game.UI.Close<TeamSelectPanel>();
        }

        private void OnEnterStage()
        {
            if (_pending == null)
            {
                Game.Logger?.Error(Tag, "进入 Stage 站点但没有待跑配置（流程异常），比赛不会开始");
            }
            else if (_match == null)
            {
                Game.Logger?.Error(Tag, "比赛门面为 null，比赛不会开始");
            }
            else if (_stageBegun)
            {
                // 已经是"从暂停恢复"，绝不能再来一次 Start（那会把打到一半的回合清掉）
                Game.Logger?.Info(Tag, "从暂停恢复，不重复 Start");
            }
            else
            {
                _stageBegun = true;

                // MatchModule 自己也订阅了 Events.LaunchMatch 并会就地 Start —— 那是"读条之前"的
                // 过早开局（地图/场景还没就绪）。这里在场景与地图都就绪之后重开一次，
                // Start 的契约是"重复调用 = 先 Stop 再 Start"，因此拿到的永远是干净的开局。
                if (_match.IsRunning)
                    Game.Logger?.Warn(Tag, "MatchModule 已在 LaunchMatch 时提前开过局，这里重开一次以从干净状态开始");

                _match.Start(_pending);
                Game.Logger?.Info(Tag,
                    $"比赛已开始：map={_pending.MapName} team={_pending.PlayerTeam} bots/队={_pending.BotsPerTeam} diff={_pending.BotDifficulty}");
            }

            Game.UI.Open<HudPanel>();
        }

        private void OnTickStage(float dt)
        {
            // 买枪菜单 / H 菜单等面板开着时，ESC 归它们（不该同时弹暂停菜单）
            if (HasBlockingOverlay()) return;
            if (Game.Input != null && Game.Input.GetKeyDown(GameKey.Escape)) OnRequestPause();
        }

        private void OnEnterPause()
        {
            Game.UI.Open<PausePanel>();
        }

        private void OnTickPause(float dt)
        {
            if (Game.Input != null && Game.Input.GetKeyDown(GameKey.Escape)) OnResumeRequested();
        }

        private void OnExitPause()
        {
            Game.UI.Close<PausePanel>();
        }

        // ═══════════════════════════════ 回主菜单 / 清场 ═══════════════════════════════

        public void GoMainMenu()
        {
            if (Game.Fsm == null) return;

            if (CurrentState == State.MainMenu && Game.UI.IsOpen<MainMenuPanel>())
            {
                Game.Logger?.Info(Tag, "已在主菜单，忽略重复的 GoMainMenu");
                return;
            }

            ClearStage();
            Game.Fsm.Trigger(Trigger.ToMain);       // → MainMenu 站点（onEnter 里卸舞台 + 加载 Menu 场景）
        }

        /// <summary>回主菜单 / 重进游戏的清场清单：**漏一项第二次进图就是脏的**。</summary>
        private void ClearStage()
        {
            Game.Logger?.Info(Tag, "清场：比赛 / UI / 实体 / 对象池 / 舞台定时器 / 音效 / HUD 快照");

            if (_match != null && _match.IsRunning)
            {
                _match.Stop();
            }
            else if (_match != null)
            {
                Game.Logger?.Info(Tag, "清场时比赛未在运行（跳过 Stop）");
            }

            Game.UI?.CloseAll();
            Game.Entity?.ClearAll();
            Game.Pool?.ClearAll();
            Game.Timer?.StopScope(StageScope);
            Game.Sound?.StopAll();

            // HUD 快照是静态的，不清会在"快速进入 Play 模式"下跨局残留（见 skill P-3）
            CsHudSnapshot.Reset();

            _pending = null;
            _stageSceneReady = false;
            _stageMapReady = false;
            _stageBegun = false;

            // 单机版没有 WorldSync；真有就说明装配错了，不能静默
            if (Game.Sync != null)
                Game.Logger?.Warn(Tag, "单机版不应存在 WorldSync（Game.Sync 非 null），已跳过 Game.Sync.Clear");
        }

        // ═══════════════════════════════ 事件处理 ═══════════════════════════════

        /// <summary>
        /// <see cref="Events.StartNewGame"/> 有两个含义（事件契约里只有一个常量，不新增）：
        /// Boot 站点收到 = 启动画面结束 → 主菜单；主菜单站点收到 = 点 New Game → 打开配置面板。
        /// </summary>
        private void OnStartNewGame()
        {
            var current = CurrentState;

            if (current == State.Boot)
            {
                Game.Logger?.Info(Tag, $"{Events.StartNewGame}（Boot 站点）→ 进主菜单");
                Game.Fsm.Trigger(Trigger.BootDone);
                return;
            }

            if (current == State.MainMenu)
            {
                Game.Logger?.Info(Tag, $"{Events.StartNewGame}（主菜单）→ 打开 New Game 配置");
                Game.Fsm.Trigger(Trigger.NewGame);
                return;
            }

            Game.Logger?.Warn(Tag, $"{Events.StartNewGame} 在 {current} 站点被触发，无处可去，已忽略");
        }

        private void OnOpenServerList()
        {
            if (CurrentState != State.MainMenu)
            {
                Game.Logger?.Warn(Tag, $"{Events.OpenServerList} 只在主菜单站点点得动（当前 {CurrentState}），已忽略");
                return;
            }
            Game.Fsm.Trigger(Trigger.OpenServers);
        }

        private void OnOpenOptions()
        {
            var current = CurrentState;
            if (current != State.MainMenu && current != State.Pause && current != State.Options)
            {
                Game.Logger?.Warn(Tag, $"{Events.OpenOptions} 只在主菜单/暂停站点点得动（当前 {current}），已忽略");
                return;
            }

            if (current == State.Options)
            {
                Game.Logger?.Info(Tag, $"{Events.OpenOptions}：Options 已打开，忽略");
                return;
            }

            _optionsFromPause = current == State.Pause;
            Game.Fsm.Trigger(Trigger.OpenOptions);
        }

        private void OnLaunchMatch(CsMatchConfig cfg)
        {
            if (cfg == null)
            {
                Game.Logger?.Error(Tag, $"{Events.LaunchMatch} 的参数不是 CsMatchConfig（收到 null），已忽略");
                return;
            }

            Game.Logger?.Info(Tag, $"{Events.LaunchMatch}: map={cfg.MapName} bots/队={cfg.BotsPerTeam} diff={cfg.BotDifficulty}");

            if (CurrentState == State.Stage || CurrentState == State.Pause)
            {
                // 已在局内（eg. H 菜单重开一局）：直接重开，不重走读条流程
                _pending = cfg.Clone();
                if (_match != null) _match.Start(_pending);
                else Game.Logger?.Error(Tag, "局内重开比赛但比赛门面为 null");
                return;
            }

            GoStage(cfg);
        }

        private void OnTeamChosen(CsTeam team)
        {
            if (CurrentState == State.Stage || CurrentState == State.Pause)
            {
                // 局内换边（H 菜单）
                Game.Logger?.Info(Tag, $"{Events.TeamChosen}：局内换阵营 → {team}");
                if (_pending != null) _pending.PlayerTeam = team;
                if (_match != null) _match.ChangeTeam(team);
                else Game.Logger?.Error(Tag, "局内换阵营但比赛门面为 null");
                return;
            }

            if (_pending == null)
            {
                Game.Logger?.Warn(Tag, $"{Events.TeamChosen} 在没有待跑配置时收到（team={team}），已忽略");
                return;
            }

            _pending.PlayerTeam = team;
            Game.Logger?.Info(Tag, $"玩家选了阵营 {team} → 开始进图");
            Game.Fsm.Trigger(Trigger.StageBegin);
        }

        private void OnRequestPause()
        {
            var current = CurrentState;

            if (current == State.Pause)
            {
                // 同一帧内 ESC 被两处识别（本类轮询 + 游戏内 UI 事件）时会走到这：幂等忽略，不刷屏
                return;
            }

            if (current != State.Stage)
            {
                Game.Logger?.Warn(Tag, $"{Events.RequestPause} 只在比赛站点点得动（当前 {current}），已忽略");
                return;
            }

            Game.Logger?.Info(Tag, "暂停请求 → Pause 站点");
            Game.Fsm.Trigger(Trigger.Pause);
        }

        private void OnResumeRequested()
        {
            var current = CurrentState;

            if (current == State.Stage) return;     // 已恢复：幂等

            if (current != State.Pause)
            {
                Game.Logger?.Warn(Tag, $"{Events.Resume} 只在暂停站点点得动（当前 {current}），已忽略");
                return;
            }

            Game.Logger?.Info(Tag, "继续游戏 → Stage 站点");
            Game.Fsm.Trigger(Trigger.Resume);
        }

        private void OnBackToMain()
        {
            Game.Logger?.Info(Tag, $"{Events.BackToMain}：回主菜单");
            GoMainMenu();
        }

        private void OnDisconnect()
        {
            // 单机版没有网络连接，"Disconnect" 语义退化为"回主菜单"（不能假装没事留在游戏里）
            Game.Logger?.Warn(Tag, $"{Events.Disconnect} 在单机版被触发（全程未调用 CloverNet 的网络初始化），按回主菜单处理");
            GoMainMenu();
        }

        private void OnQuitGame()
        {
            Game.Logger?.Info(Tag, "退出游戏（先把设置落盘）");
            Game.Setting?.Save();

#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        // ═══════════════════════════════ 小工具 ═══════════════════════════════

        private void OnFsmChanged(string from, string to)
        {
            if (_match == null || !_match.IsRunning) return;

            var wasPaused = IsPauseFamily(from);
            var nowPaused = IsPauseFamily(to);
            if (wasPaused == nowPaused) return;

            _match.SetPaused(nowPaused);
            Game.Logger?.Info(Tag, nowPaused ? $"比赛已暂停（{from} → {to}）" : $"比赛已恢复（{from} → {to}）");
        }

        /// <summary>"暂停族" = Pause 站点，以及"从暂停菜单进来的 Options"（进 Options 不该顺手解除暂停）。</summary>
        private bool IsPauseFamily(string state)
        {
            if (state == State.Pause) return true;
            if (state == State.Options && _optionsFromPause) return true;
            return false;
        }

        private void OnPanelOpened(string panelName)
        {
            if (string.IsNullOrEmpty(panelName)) return;
            _openPanels.Add(panelName);
        }

        private void OnPanelClosed(string panelName)
        {
            if (string.IsNullOrEmpty(panelName)) return;
            _openPanels.Remove(panelName);
        }

        /// <summary>除常驻 HUD 外还有面板开着 = 有遮挡（ESC 归它，不弹暂停菜单）。</summary>
        private bool HasBlockingOverlay()
        {
            foreach (var name in _openPanels)
            {
                if (name == _hudPanelName) continue;
                return true;
            }
            return false;
        }

        private string DescribeScene()
        {
            if (Game.Scene == null) return "<no scene module>";
            return Game.Scene.CurrentScene ?? "<none>";
        }
    }
}
