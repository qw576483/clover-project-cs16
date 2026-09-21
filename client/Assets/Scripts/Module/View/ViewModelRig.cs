using CloverEngine;
using Cs16.Core;
using Cs16.Module.Match;
using Cs16.Module.Player;
using UnityEngine;

namespace Cs16.Module.View
{
    /// <summary>
    /// **第一人称武器视图**：把 CS 1.6 的 <c>v_*</c> 模型挂到相机下，并播放**原版自己的**
    /// 逐帧动画序列（idle / shoot1..3 / shoot_empty(shootlast) / reload / draw）。
    ///
    /// <para><b>为什么不再有"程序化后坐/换弹下沉/切枪抬起"</b>：那些是"没有帧动画"时期的替代品
    /// （旧实现的注释里写着"用程序化后坐代替帧动画"）。现在 <c>v_*.mdl</c> 的动画已经按原版
    /// 序列搬进来了 ⇒ 后坐、换弹、抬起全部由原版 <c>shoot*/reload/draw</c> 序列表达，
    /// 叠加程序化位移就是"动两次"。序列的 fps / 帧数一律取 mdl 实测（见
    /// <c>原版资源/cs16src/cs16_anim.py</c> 的导出日志）。</para>
    ///
    /// <para><b>为什么不需要"摆到右下角"的魔数</b>：CS 1.6 的 <c>v_*</c> 模型是在**相机空间**里建模的 ——
    /// 模型自带的坐标就已经把枪放到了右下（实测 <c>v_ak47</c> idle 姿态包围盒
    /// x∈[0.03,0.24] 偏右、y∈[-0.29,-0.06] 在准星下方、z∈[0.09,0.71] 向前），
    /// GoldSrc 也把模型原点直接放在相机原点 ⇒ 左右/前后都不需要加偏移。</para>
    ///
    /// <para><b>但上下有一个原版的固定偏移</b>：原版把视图原点放到眼位之后又下推 1 unit
    /// （<c>HLSDK/cl_dll/view.cpp:665</c> <c>view-&gt;origin[2] -= 1;</c>）⇒
    /// <see cref="CsViewTuning.ViewModelLocalPosition"/> 的 y = −0.0254 m。原因见那里的注释。</para>
    ///
    /// <para><b>开火/换弹怎么感知（只读）</b>：不去抢 agent-04 的射击队列（那是它独占消费的），
    /// 而是观察权威状态的变化：<c>CsActor.NextFireTime</c> 前推 = 打出了一发；
    /// <c>ReloadEndTime</c> 前推 = 开始换弹；<c>ActiveWeapon</c> 变化 = 切枪（播 <c>draw</c>）。
    /// 这样"表现"与"模拟"永远一致，也不会多打一发子弹。</para>
    /// </summary>
    public sealed class ViewModelRig : MonoBehaviour
    {
        private const string Tag = "View";

        private ICsMatch _match;
        private ViewModule _owner;
        private CsModuleLog _log;

        private GameObject _model;
        private string _modelKey;
        private Transform _modelRoot;

        // ---- 只读快照（用于检测"发生了什么"）----
        private float _preNextFireTime;
        private float _preReloadEndTime;
        private string _preWeapon;
        private bool _haveSnapshot;

        // ---- 动画状态 ----
        private Animator _animator;
        private IAnimPlayer _anim;
        private string _overrideState;      // 一次性动作（开火/换弹/抬起）占用的状态
        private string _lastState;          // 当前状态（避免每帧重复 CrossFade）
        private int _shotIndex;             // 连发时在 shoot1→2→3 之间轮换（与原版一致）
        private bool _stateWarned;

        private float _missingLogged;

        /// <summary>当前宿主相机（<see cref="ViewModule"/> 用它判断是否需要重绑）。</summary>
        public Camera Host { get; private set; }

        /// <summary>当前武器模型资源路径（诊断用）。</summary>
        public string CurrentModelKey => _modelKey;

        /// <summary>当前武器 id。</summary>
        public string CurrentWeaponId { get; private set; }

        // ==================================================================
        //  装配 / 拆除
        // ==================================================================
        internal void Init(ICsMatch match, ViewModule owner)
        {
            _match = match;
            _owner = owner;
            _log = owner != null ? owner.Log : new CsModuleLog(Tag);
            Host = GetComponent<Camera>();

            var rootGo = new GameObject("CsViewModel");
            rootGo.transform.SetParent(transform, false);
            _modelRoot = rootGo.transform;
            ApplyRigTransform();

            _log.Always($"第一人称武器视图就绪：宿主相机={name}，模型原点放在相机原点下方 " +
                        $"{CsViewTuning.ViewModelLocalPosition.y:F4} m（原版 view.cpp:665 的 view->origin[2] -= 1 unit）；" +
                        "后坐/换弹/抬起走原版序列");
        }

        /// <summary>解绑/换相机时清掉模型（避免把枪留在菜单相机上）。</summary>
        internal void Teardown()
        {
            if (_model != null) Destroy(_model);
            _model = null;
            _modelKey = null;
            CurrentWeaponId = null;
            _anim = null;
            _animator = null;
            _overrideState = null;
            _lastState = null;
            _haveSnapshot = false;
            if (_modelRoot != null) Destroy(_modelRoot.gameObject);
            _modelRoot = null;
        }

        private void OnDestroy() => Teardown();

        // ==================================================================
        //  每帧
        // ==================================================================
        internal void Tick()
        {
            var match = _match;
            if (match == null || !match.IsRunning)
            {
                SetModelVisible(false);
                return;
            }

            var local = match.LocalPlayer;
            if (local == null)
            {
                SetModelVisible(false);
                return;
            }

            // ---- 观战 / 死亡：把枪收起来（CS 里观战没有第一人称武器）----
            if (match.IsSpectating || !local.IsAlive)
            {
                SetModelVisible(false);
                ResetSnapshot(local);
                return;
            }

            SetModelVisible(true);
            SyncModel(local);
            UpdateAnim(local);
        }

        private void SetModelVisible(bool visible)
        {
            if (_modelRoot == null) return;
            if (_modelRoot.gameObject.activeSelf != visible) _modelRoot.gameObject.SetActive(visible);
        }

        private void ResetSnapshot(CsActor local)
        {
            _haveSnapshot = false;
            _preWeapon = local.ActiveWeapon;
            _preNextFireTime = local.NextFireTime;
            _preReloadEndTime = local.ReloadEndTime;
        }

        // ==================================================================
        //  模型切换
        // ==================================================================
        private void SyncModel(CsActor local)
        {
            var weaponId = local.ActiveWeapon;
            if (string.IsNullOrEmpty(weaponId))
            {
                weaponId = CsViewTuning.ViewModelFallbackWeapon;
            }

            if (weaponId == CurrentWeaponId && _model != null) return;

            var wasNull = CurrentWeaponId == null;
            CurrentWeaponId = weaponId;

            // 先隐藏旧模型：新模型加载是异步的，中间不能出现"两把枪"。
            if (_model != null) _model.SetActive(false);

            var path = ViewModelPath(local.Team, weaponId);
            if (_owner == null)
            {
                _log.Error("viewmodel.noowner", "ViewModelRig 未拿到 ViewModule（装配异常），武器模型无法加载");
                return;
            }

            if (_owner.TryGetViewModelPrefab(path, out var ready))
            {
                Attach(ready, path);
                return;
            }

            if (_owner.TryGetViewModelPrefab(ViewModelPath(local.Team, CsViewTuning.ViewModelFallbackWeapon), out var knife))
            {
                Attach(knife, path + "(回退 刀)");
            }

            _owner.RequestViewModelPrefab(path, go =>
            {
                if (this == null) return;
                if (go == null)
                {
                    OnModelMissing(path, local.Team);
                    return;
                }
                Attach(go, path);
            });

            // 刚开局（还没有任何模型）时先摆一个刀，避免"手上空着"
            if (wasNull && _model == null)
            {
                var fb = ViewModelPath(local.Team, CsViewTuning.ViewModelFallbackWeapon);
                _owner.RequestViewModelPrefab(fb, go =>
                {
                    if (this == null || _model != null) return;
                    if (go != null) Attach(go, fb + "(回退)");
                });
            }
        }

        private void OnModelMissing(string path, CsTeam team)
        {
            _missingLogged++;
            if (_missingLogged != 1 && _missingLogged % CsViewTuning.LogRateEvery != 0) return;
            _log.Warn("viewmodel.missing." + path,
                $"第一人称武器模型缺失：Resources/{path}.prefab（第 {_missingLogged} 次）—— " +
                $"该武器手上不显示。请先执行菜单 Clover/CS16/整理视图与音效资源（会从 CS 1.6 的 v_*.mdl 生成）");
        }

        private static string ViewModelPath(CsTeam team, string weaponId)
        {
            var folder = team == CsTeam.CT ? CsViewTuning.TeamFolderCT : CsViewTuning.TeamFolderT;
            return CsViewTuning.ArtRoot + folder + "/" + CsViewTuning.ViewModelPrefix + weaponId;
        }

        private void Attach(GameObject prefab, string key)
        {
            if (_modelRoot == null) return;
            if (_model != null) Destroy(_model);

            _model = Instantiate(prefab, _modelRoot);
            _model.name = "ViewModel_" + key;
            _model.transform.localPosition = Vector3.zero;
            _model.transform.localRotation = Quaternion.identity;
            _model.transform.localScale = Vector3.one * CsViewTuning.ViewModelScale;
            _modelKey = key;

            // 武器模型绝不能参与射线：去掉它身上任何碰撞体（生成器没加，这里再兜一层）。
            var cols = _model.GetComponentsInChildren<Collider>(true);
            for (var i = 0; i < cols.Length; i++)
            {
                if (cols[i] != null) Destroy(cols[i]);
            }

            EnsureAlwaysAnimate(_model, key);
            SetupAnimator();
            _log.Info("viewmodel.attach",
                $"第一人称武器模型切换 → {key}（共 {cols.Length} 个多余碰撞体已移除）");
        }

        /// <summary>
        /// **必须恒更新蒙皮与骨骼变换**（本项目实测缺陷的运行时兜底）。
        ///
        /// <para>根因：<c>.cs16anim</c> 网格的顶点是**绑定姿态**坐标，而渲染姿态由动画决定 ——
        /// 以 v_* 视模型为例（实测）：绑定姿态 bbox = x∈[-0.9087,-0.1160]、y∈[-0.0469,+0.1763]、
        /// z∈[-0.1272,+0.1374]（**把相机原点夹在里面**）；而 idle 姿态 = x∈[-0.0260,+0.2390]、
        /// y∈[-0.2860,-0.0620]、z∈[-0.0870,+0.7050]。两者**完全不重合**。</para>
        ///
        /// <para>Unity 用 <c>SkinnedMeshRenderer.localBounds</c>（= 绑定姿态包围盒）同时做
        /// ① 视锥剔除 ② 蒙皮更新开关；Animator 默认的 <c>CullUpdateTransforms</c> 又拿"看不见"来决定
        /// 更不更新骨骼。一旦被判成不可见 ⇒ 骨骼**停在绑定姿态** ⇒ 画出来是**巨大到失真的手臂/枪**
        /// （实测绑定姿态有顶点投影到视口 x=155 —— 155 倍屏宽）。</para>
        ///
        /// <para>在**实例化时**把两个开关都设成"恒更新"，于是盘上**已烘好的旧预制体**也一并修好，
        /// 不必重跑生成器（生成器侧同样已改，见 <c>Editor/Views/AnimSetup.cs</c>）。</para>
        /// </summary>
        private void EnsureAlwaysAnimate(GameObject model, string key)
        {
            var fixedSmr = 0;
            var smrs = model.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (var i = 0; i < smrs.Length; i++)
            {
                if (smrs[i] == null || smrs[i].updateWhenOffscreen) continue;
                smrs[i].updateWhenOffscreen = true;
                fixedSmr++;
            }
            var fixedAnim = 0;
            var anims = model.GetComponentsInChildren<Animator>(true);
            for (var i = 0; i < anims.Length; i++)
            {
                if (anims[i] == null || anims[i].cullingMode == AnimatorCullingMode.AlwaysAnimate) continue;
                anims[i].cullingMode = AnimatorCullingMode.AlwaysAnimate;
                fixedAnim++;
            }
            if (fixedSmr > 0 || fixedAnim > 0)
            {
                _log.Info("viewmodel.cullfix",
                    $"第一人称武器 {key}：已修正 {fixedSmr} 个 SkinnedMeshRenderer.updateWhenOffscreen " +
                    $"与 {fixedAnim} 个 Animator.cullingMode（绑定姿态不得当可见性判据 ⇒ 否则巨大失真）");
            }
        }

        // ==================================================================
        //  动画（原版序列）
        // ==================================================================
        private void SetupAnimator()
        {
            _animator = _model != null ? _model.GetComponent<Animator>() : null;
            _anim = null;
            _overrideState = null;
            _lastState = null;
            _shotIndex = 0;
            _stateWarned = false;

            if (_animator == null)
            {
                _log.Warn("viewmodel.noanimator",
                    $"武器预制体 {_modelKey} 上没有 Animator —— 手上这把枪不会播放任何动作。" +
                    "请重跑菜单 Clover/CS16/整理视图与音效资源");
                return;
            }
            var ctrl = _animator.runtimeAnimatorController;
            if (ctrl == null)
            {
                _log.Warn("viewmodel.nocontroller",
                    $"武器预制体 {_modelKey} 的 Animator 没有 controller —— 手上这把枪不会播放任何动作");
                return;
            }
            var anim = Game.Anim;
            if (anim == null)
            {
                _log.Warn("viewmodel.nomanager", "Game.Anim 为 null —— 第一人称武器不会播放任何动作");
                return;
            }
            _anim = anim.CreateAnimator(_model, ctrl);
            if (_anim == null) return;
            _anim.OnComplete(OnClipFinished);

            // 切枪：原版切枪会先播 draw（抬起）—— 与"新模型刚挂上"一一对应。
            var st = ResolveState(CsViewTuning.VmStateDraw);
            if (st != null)
            {
                _anim.Play(st, 0f);
                _overrideState = st;
            }
            else
            {
                var idle = ResolveState(CsViewTuning.VmStateIdle);
                if (idle != null) { _anim.Play(idle, 0f); _lastState = idle; }
            }
        }

        private void OnClipFinished()
        {
            if (_overrideState == null) return;
            _overrideState = null;
            _lastState = null;      // 强制下一帧回到 idle
        }

        /// <summary>按候选顺序取 Animator 里真实存在的状态（各武器序列命名不统一，实测见回报）。</summary>
        private string ResolveState(string[] candidates)
        {
            if (_animator == null || candidates == null) return null;
            for (var i = 0; i < candidates.Length; i++)
            {
                if (string.IsNullOrEmpty(candidates[i])) continue;
                if (_animator.HasState(0, Animator.StringToHash(candidates[i]))) return candidates[i];
            }
            if (!_stateWarned)
            {
                _stateWarned = true;
                _log.Warn("viewmodel.state.missing",
                    $"武器 {_modelKey} 的状态机里找不到 {string.Join("/", candidates)} —— " +
                    "该动作不播放（预制体与 .cs16anim 不同步？请重跑生成器）");
            }
            return null;
        }

        /// <summary>只读权威状态 → 切原版序列（开火 / 换弹 / 静止）。</summary>
        private void UpdateAnim(CsActor local)
        {
            if (_anim == null) return;

            if (!_haveSnapshot)
            {
                _haveSnapshot = true;
                _preWeapon = local.ActiveWeapon;
                _preNextFireTime = local.NextFireTime;
                _preReloadEndTime = local.ReloadEndTime;
                return;
            }

            if (local.ActiveWeapon != _preWeapon)
            {
                _preWeapon = local.ActiveWeapon;
                _shotIndex = 0;
            }

            // 换弹：ReloadEndTime 前推 = 开始换弹
            if (local.ReloadEndTime > _preReloadEndTime + 0.0001f)
            {
                var st = ResolveState(CsViewTuning.VmStateReload);
                if (st != null) { _anim.Play(st, 0f); _overrideState = st; }
            }
            _preReloadEndTime = local.ReloadEndTime;

            // 开火：NextFireTime 前推 = 确实打出了一发
            if (local.NextFireTime > _preNextFireTime + 0.0001f)
            {
                var mag = 1;
                if (!string.IsNullOrEmpty(local.ActiveWeapon)
                    && local.Ammo.TryGetValue(local.ActiveWeapon, out var ammo))
                {
                    mag = ammo.inMag;      // **只读**：直接查字典，不用会在缺项时写回的 CsActor.GetAmmo
                }

                string[] cand;
                if (mag <= 0) cand = CsViewTuning.VmStateFireLast;            // 弹匣打空的那一发
                else if (_shotIndex == 0) cand = CsViewTuning.VmStateFire1;
                else if (_shotIndex == 1) cand = CsViewTuning.VmStateFire2;
                else cand = CsViewTuning.VmStateFire3;
                _shotIndex = (_shotIndex + 1) % 3;

                var st = ResolveState(cand);
                if (st != null) { _anim.Play(st, 0f); _overrideState = st; }
            }
            _preNextFireTime = local.NextFireTime;

            if (_overrideState != null) return;    // 一次性动作播完前不回 idle

            var idle = ResolveState(CsViewTuning.VmStateIdle);
            if (idle != null && idle != _lastState)
            {
                _lastState = idle;
                _anim.CrossFade(idle, CsViewTuning.AnimCrossFade);
            }
        }

        // ==================================================================
        //  模型根变换
        // ==================================================================
        private void ApplyRigTransform()
        {
            if (_modelRoot == null) return;
            _modelRoot.localPosition = CsViewTuning.ViewModelLocalPosition;
            _modelRoot.localRotation = Quaternion.Euler(CsViewTuning.ViewModelLocalEuler);
            _modelRoot.localScale = Vector3.one;
        }
    }
}
