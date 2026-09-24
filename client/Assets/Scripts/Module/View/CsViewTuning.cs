using Cs16.Core;
using UnityEngine;

namespace Cs16.Module.View
{
    /// <summary>
    ///
    /// <para><b>为什么单独一个文件</b>：与 <c>CsConst</c> 同语义（"改一处即可调表现"的旋钮），
    /// 但它们是视图层独有的，而 <c>CsConst</c> 是**契约文件（不许改）** ——
    /// 目的同样是：**业务脚本里不出现裸数字**。</para>
    ///
    /// <para><b>名字表就是"素材契约"</b>：<c>Assets/Resources/Art/**</c> 与
    /// <c>Assets/Resources/Sound/SFX/**</c> 下的文件名由 <c>Assets/Editor/Views/ArtSetup.cs</c>
    /// 按这里的常量生成；替换素材 = 直接换同名文件，逻辑一行都不用改。</para>
    /// </summary>
    /// <remarks>
    /// 这里刻意是 <c>public</c>（不是其它模块那种 <c>internal</c>）：<c>Assets/Editor/Views/**</c>
    /// 属于默认的 <c>Assembly-CSharp-Editor</c> 程序集，**看不到** <c>Cs16.asmdef</c> 里的 internal 成员，
    /// </remarks>
    public static class CsViewTuning
    {
        // ==================================================================
        //  资源路径（Resources 下，不带扩展名）
        // ==================================================================
        /// <summary>模型/预制体根目录。出处：**本项目新增**（Unity <c>Resources</c> 下的子目录命名 = 本工程素材契约，
        /// 与生成器 <c>Assets/Editor/Views/ArtSetup.cs</c> 共用同一份常量）。</summary>
        public const string ArtRoot = "Art/";

        /// <summary>阵营子目录名（Art/T/**、Art/CT/**）。出处：**本项目新增**（本工程归一的目录命名；
        /// 原版阵营模型目录名是 <c>models/player/{terror,leet,arctic,guerilla,urban,gsg9,sas,gign,vip}</c>，见规格 §5）。</summary>
        public const string TeamFolderT = "T";
        public const string TeamFolderCT = "CT";

        /// <summary>角色模型预制体名（Art/{阵营}/player）。出处：**本项目新增**（本工程预制体命名；
        /// 原版是 <c>models/player/&lt;皮肤&gt;/&lt;皮肤&gt;.mdl</c> ⇒ 名字不同源）。</summary>
        public const string PlayerModelName = "player";

        /// <summary>
        /// T 阵营可用的皮肤（CS 1.6 里每个玩家在队内随机一个皮肤，这里按 actorId 稳定分配）。
        /// </summary>
        public static readonly string[] SkinsT = { "player", "leet", "arctic", "guerilla" };

        /// <summary>CT 阵营可用的皮肤（同上；<c>Art/CT/player</c> 必须存在）。</summary>
        public static readonly string[] SkinsCT = { "player", "gsg9", "sas", "gign", "vip" };

        /// <summary>第一人称武器模型前缀（Art/{阵营}/viewmodel_{武器id}）。出处：**本项目新增**（本工程预制体命名；
        /// 原版第一人称模型文件名是 <c>models/v_&lt;武器&gt;.mdl</c>（见规格 §5）⇒ 前缀不同源）。</summary>
        public const string ViewModelPrefix = "viewmodel_";

        /// <summary>角色预制体里承载"整体缩放/贴地"的子节点名（名牌放它外面，才不会被蹲下压扁）。
        /// 出处：**本项目新增**（预制体内部节点名 = 本工程命名）。</summary>
        public const string BodyNodeName = "Body";

        /// <summary>命中区子节点名前缀：<c>Hitbox_{CsHitbox}</c>（Hitbox_Head / Hitbox_Chest / ...）。
        /// 出处：**本项目新增**（节点名前缀 = 本工程命名；命中区的**语义**出处 = <c>CsHitbox</c> 枚举，见 <c>Core/CsEnums.cs</c>）。</summary>
        public const string HitboxNodePrefix = "Hitbox_";

        /// <summary>名牌预制体实例在角色视图下的节点名。出处：**本项目新增**（本工程命名）。</summary>
        public const string PlateNodeName = "Plate";

        /// <summary>头顶名牌的**世界空间**预制体（只在 Resources/UI 下放这一个）。
        /// 出处：**本项目新增**（本工程资源路径；原版队友名牌的绘制在 <c>client.dll</c>，没有对应预制体）。</summary>
        public const string NameplatePath = "UI/WorldNameplate";

        // ==================================================================
        //  角色视图
        // ==================================================================
        /// <summary>角色的目标身高（米）。与 <c>CsConst.StandHeight</c> 同源 —— 碰撞体/射线按它对齐。
        /// 出处：<c>Core/CsConst.cs</c> 的 <c>StandHeight = 1.80f</c>（本值即该常量，不另立数值）；
        /// 原版站高 72 unit × 0.0254 = 1.829 m，归一化口径见 <c>策划/对照表.md</c> N-25。</summary>
        public const float TargetHeight = CsConst.StandHeight;

        /// <summary>实例化后实测身高与目标值的容许偏差（超出即纠偏并告警）。
        /// 出处：**本项目新增**（生成期自检容差）。</summary>
        public const float HeightTolerance = 0.03f;

        /// <summary>
        /// 蹲下时整体高度缩放。**骨骼动画路线下固定 1（不压扁）**：
        /// 原版用 <c>crouch_idle</c> / <c>crouchrun</c> 两条序列表达蹲姿 ——
        /// 实测 <c>crouch_idle</c> 身高 47.95u，与绑定姿态 70.39u 同比例换算得 1.226m，
        /// 而同源常量 <c>CsConst.CrouchHeight</c> = 1.25m（差 2%）。再叠一个"整体压扁"就是蹲两次，
        /// "没有蹲动画"时期的替代品。
        /// </summary>
        public const float CrouchScale = 1f;

        /// <summary>站/蹲缩放平滑时间常数（秒）。</summary>
        public const float CrouchBlendTau = 0.08f;

        /// <summary>一次位置跳变超过它（米）就直接吸附，不做插值（出生 / 复活 / 传送）。</summary>
        public const float TeleportSnapDistance = 2.5f;

        /// <summary>
        /// 位置平滑时间常数（秒）。0 = 不平滑（直接用 <c>CsActor.Position</c>，模拟已是权威）。
        ///
        /// <para><b>这个 0 写不出原版出处</b>（原版客户端确实做插值 ——
        /// <c>HLSDK/cl_dll/view.cpp:719-785</c> 的 <c>ViewInterp</c> 环形缓冲，
        /// 但它的口径是"<c>Length(delta) &lt; 64</c> 才插值"的**位置回放**，不是指数平滑的 tau，
        /// <c>策划/验收表.md</c> 的「允许的差异」里（含为什么 / 出处 / 何时消除），不再留裸数字。</para>
        /// </summary>
        public const float PositionSmoothTau = 0f;

        // ==================================================================
        //  头顶名牌 / 血条
        // ==================================================================
        /// <summary>超过该距离不显示名牌（米）。</summary>
        public const float NameplateMaxDistance = 55f;

        /// <summary>从该距离开始淡出（米）。</summary>
        public const float NameplateFadeStart = 45f;

        /// <summary>名牌离脚底的高度（米）—— 略高于身高，避免与头部模型重叠。</summary>
        public const float NameplateHeight = (float)(CsConst.StandHeight + 0.20);

        /// <summary>名牌文字高度（米，世界空间）。</summary>
        public const float NameplateTextHeight = 0.16f;

        /// <summary>血条世界宽度 / 高度（米）。</summary>
        public const float NameplateBarWidth = 0.70f;
        public const float NameplateBarHeight = 0.075f;

        public const bool ShowEnemyHealth = true;

        /// <summary>是否给敌人显示**名字**（CS 1.6 原版只显示队友名字，默认关）。</summary>
        public const bool ShowEnemyName = false;

        /// <summary>视线判定打的是目标的躯干高度（米），不是头顶 —— 头顶容易被掩体挡住而误判。</summary>
        public const float LosTargetHeight = 1.10f;

        /// <summary>
        /// 名字/血条只在"大致看向他"时显示：相机前向与目标方向的点积下限（≈ cos 70°）。
        /// 与 CS 里"看着队友才见名字"的观感一致，也顺手省掉背后一堆名牌。
        /// </summary>
        public const float NameplateMinFacingDot = 0.34f;

        /// <summary>世界空间画布的局部缩放（UI 像素 → 世界米：0.01 表示 100px = 1m）。</summary>
        public const float NameplateCanvasScale = 0.01f;

        // 队色（与 CS 1.6 的雷达/记分板观感一致：T 偏红、CT 偏蓝）
        public static readonly Color TeamColorT = new Color(1.00f, 0.42f, 0.35f, 1f);
        public static readonly Color TeamColorCT = new Color(0.42f, 0.68f, 1.00f, 1f);
        public static readonly Color TeamColorSpec = new Color(0.75f, 0.75f, 0.78f, 1f);
        public static readonly Color HpHigh = new Color(0.35f, 0.85f, 0.30f, 1f);
        public static readonly Color HpMid = new Color(0.95f, 0.75f, 0.20f, 1f);
        public static readonly Color HpLow = new Color(0.90f, 0.20f, 0.20f, 1f);
        public static readonly Color HpBack = new Color(0.05f, 0.05f, 0.06f, 0.78f);

        // ==================================================================
        //  第一人称武器视图（viewmodel）
        // ==================================================================
        /// <summary>
        /// 武器模型在相机下的**局部位置**（米）= <c>(0, −1 unit, 0)</c>。
        ///
        /// <para><b>为什么不是零</b>：原版在把视图原点放到眼位之后，**又把它往下推了 1 unit**
        /// （<c>原版资源/cs16src/hlsdk/halflife-master/cl_dll/view.cpp:665</c>：
        /// <c>view-&gt;origin[2] -= 1;</c>，紧邻注释原文
        /// "pushing the view origin down off of the same X/Z plane as the ent's origin will give the
        /// gun a very nice 'shifting' effect when the player looks up/down"）——
        /// 效果是"抬头/低头时枪身有位移感"，代价就是**整个视图模型比眼位低 1 unit**。
        /// ⇒ 1 unit = 0.0254 m（<c>原版资源/cs16src/cs16_build.py:43</c> <c>HL_UNIT = 0.0254</c>）
        /// ⇒ <b>y = −0.0254 m</b>。</para>
        ///
        /// <para>⚙️ <b>其余分量仍为零</b>：<c>v_*</c> 模型是在"相机空间"里直接建模的
        /// （实测 idle 姿态包围盒 x∈[0.03,0.24] 偏右、y∈[−0.29,−0.06] 在准星下方、z∈[0.09,0.71] 向前），
        /// GoldSrc 也把原点直接放在相机原点 ⇒ x/z 不加偏移。不许自己编 x/z。</para>
        ///
        /// <para><b>原版那段 <c>viewsize</c> 补偿不用管（已核实为"不生效"）</b>：
        /// <c>view.cpp:667-684</c> 只给 110→+1 / 100→+2 / 90→+1 / 80→+0.5 unit 四档做补偿，
        /// 而原版 <c>viewsize</c> 的**出厂默认值实测 = <c>120</c></b>
        /// （出处：<c>原版资源/cs16src/cs16game/app/hw.dll</c> 偏移 <c>0x177684</c> 的 cvar 字面量区，
        /// 实测片段 <c>…"30" "viewsize" "120" "viewsize" …</c>；HLSDK 里 <c>viewsize</c> 只是
        /// <c>common/ref_params.h:50</c> 的一个字段、**没有注册**它 ⇒ 默认值由引擎填）——
        /// <c>120</c> **不落在那四档里** ⇒ 走**无补偿**分支
        /// ⇒ 原版净偏移就是 <c>view.cpp:665</c> 的 <b>−1 unit = −0.0254 m</b>，与本字段完全一致
        /// （<c>策划/对照表.md</c> A-06 差值 = 0）。</para>
        /// </summary>
        public static readonly Vector3 ViewModelLocalPosition = new Vector3(0f, -0.0254f, 0f);

        /// <summary>武器模型在相机下的局部欧拉角（度）。同样为零（模型自带朝向）。</summary>
        public static readonly Vector3 ViewModelLocalEuler = Vector3.zero;

        /// <summary>武器模型的额外缩放（1 = 按 HL 单位换算后的真实尺寸）。</summary>
        public const float ViewModelScale = 1f;

        /// <summary>当前手持武器的视图预制体不存在时，退到哪里（避免"手上空着"）。</summary>
        public const string ViewModelFallbackWeapon = CsWeapons.Knife;

        // ==================================================================
        //  骨骼动画（原版序列驱动；**帧率/帧数/关键帧全部来自 mdl 实测**，见
        /// <summary>骨骼层级挂载节点名（生成器在角色预制体的 <c>Body</c> 下 / 视模型根下建它）。</summary>
        public const string SkeletonNodeName = "Skeleton";

        /// <summary>状态切换的过渡时长（秒）——原版序列本身就是连续的，这里只做极短过渡，
        /// 避免"跳帧"感；不要当成"手感参数"去调。</summary>
        public const float AnimCrossFade = 0.06f;

        /// <summary>
        /// 判定"在移动"的最小水平速度（米/秒）。低于它 = 静止（放静止序列）。
        ///
        /// <para><b>这个 0.15 写不出原版出处</b>：原版按速度选动画档位的那套阈值在服务端
        /// <c>cstrike/dlls/mp.dll</c> 里（<c>HLSDK/cl_dll/</c> **没有** CS 的角色动画选择），
        /// 的「允许的差异」里（含为什么 / 出处 / 何时消除），不再留裸数字。</para>
        /// </summary>
        public const float AnimMoveSpeedEpsilon = 0.15f;

        /// <summary>角色静止（站）。</summary>
        public static readonly string[] PStateIdle = { "idle1", "idle" };

        /// <summary>角色慢走（Shift）。</summary>
        public static readonly string[] PStateWalk = { "walk", "run" };

        /// <summary>角色跑。</summary>
        public static readonly string[] PStateRun = { "run", "walk" };

        /// <summary>角色蹲静止。</summary>
        public static readonly string[] PStateCrouchIdle = { "crouch_idle", "idle1", "idle" };

        /// <summary>角色蹲移动。</summary>
        public static readonly string[] PStateCrouchRun = { "crouchrun", "crouch_idle", "run" };

        /// <summary>角色在空中。</summary>
        public static readonly string[] PStateJump = { "jump", "idle1", "idle" };

        /// <summary>
        /// 角色死亡（原版 3 条，按 actorId 选一条 —— 与 CS 里"每次死亡的倒地姿势不同"一致）。
        /// </summary>
        public static readonly string[] PStateDeath = { "death1", "death2", "death3" };

        /// <summary>
        /// 倒地序列播完后，把 Animator 冻住的速度（**0 = 停在最后一帧**）。
        ///
        /// <para><b>为什么是 0 而不是"继续播"</b>：原版尸体是**躺在被打倒的地方**直到本局结束
        /// （差异 #73，用户原话"死亡动画没有，尸体怎么不在地上"）。倒地序列是一次性 clip，
        /// 播完不冻就会被状态机拉回 idle（尸体站起来）或按旧行为被整具隐藏（尸体消失）。
        /// 冻在最后一帧 = 尸体的姿势就是倒地序列的最后一帧。</para>
        /// </summary>
        public const float CorpseAnimSpeed = 0f;

        /// <summary>视模型：静止。</summary>
        public static readonly string[] VmStateIdle = { "idle" };

        /// <summary>视模型：第 1 发。</summary>
        public static readonly string[] VmStateFire1 = { "fire1" };

        /// <summary>视模型：第 2 发。</summary>
        public static readonly string[] VmStateFire2 = { "fire2", "fire1" };

        /// <summary>视模型：第 3 发。</summary>
        public static readonly string[] VmStateFire3 = { "fire3", "fire2", "fire1" };

        /// <summary>视模型：弹匣打空的那一发（原版手枪/部分步枪是 <c>shoot_empty</c>）。</summary>
        public static readonly string[] VmStateFireLast = { "firelast", "fire3", "fire2", "fire1" };

        /// <summary>视模型：换弹。</summary>
        public static readonly string[] VmStateReload = { "reload" };

        /// <summary>视模型：切枪抬起（原版 <c>draw</c> / <c>deploy</c>）。</summary>
        public static readonly string[] VmStateDraw = { "draw" };

        /// <summary>
        /// **武器 → 角色模型动画类**。原版把角色模型的射击/换弹序列按"武器动画类"命名
        /// （<c>ref_shoot_&lt;类&gt;</c>），类名集合由 mdl 自己决定 —— 实测 <c>models/player/*/*.mdl</c>
        /// 的 group0 序列里只有这些后缀：<c>carbine / onehanded / dualpistols_1 / dualpistols_2 /
        /// rifle / mp5 / shotgun / m249 / grenade / c4 / knife / ak47 / shield*</c>。
        ///
        /// <para><b>逐把枪的出处</b>：mp.dll 的 <c>WeaponInfo::m_szAnimExtension</c> 实测到
        /// <c>aug→carbine</c> / <c>deagle→onehanded</c> / <c>elite→dualpistols</c>
        /// （该字段的字符串在文件里与 <c>models/p_*.mdl</c> 相邻可读）；其余按本工程
        /// <see cref="CsWeaponDef.Class"/> 归到 mdl 真正拥有的那个类。</para>
        /// </summary>
        public static string PlayerAnimClass(string weaponId)
        {
            switch (weaponId)
            {
                case CsWeapons.Ak47: return "ak47";
                case CsWeapons.Aug: return "carbine";
                case CsWeapons.Sg552: return "carbine";
                case CsWeapons.Elite: return "dualpistols_1";
                case CsWeapons.Knife: return "knife";
                case CsWeapons.HeGrenade:
                case CsWeapons.Flashbang:
                case CsWeapons.SmokeGrenade: return "grenade";
                case CsWeapons.C4: return "c4";
            }

            var def = CsWeapons.Get(weaponId);
            if (def == null) return null;
            switch (def.Class)
            {
                case CsWeaponClass.Pistol: return "onehanded";
                case CsWeaponClass.SMG: return "mp5";
                case CsWeaponClass.Rifle: return "rifle";
                case CsWeaponClass.Sniper: return "rifle";
                case CsWeaponClass.Shotgun: return "shotgun";
                case CsWeaponClass.MachineGun: return "m249";
                default: return null;
            }
        }

        /// <summary>角色开枪时要播的 state 候选（蹲姿优先，其次站姿）。</summary>
        public static string[] PlayerShootStates(string weaponId, bool crouch)
        {
            var cls = PlayerAnimClass(weaponId);
            if (string.IsNullOrEmpty(cls)) return new string[0];
            if (cls == "dualpistols_1")
            {
                // 原版双手手枪只有左右手两组（shoot_left*/shoot_right*），没有 ref_shoot_dualpistols_1
                // 的“站姿单序列”——用原版自己的左手组作代表。
                return crouch
                    ? new[] { "crouch_shoot_dualpistols_1", "ref_shoot_dualpistols_1" }
                    : new[] { "ref_shoot_dualpistols_1", "crouch_shoot_dualpistols_1" };
            }
            return crouch
                ? new[] { "crouch_shoot_" + cls, "ref_shoot_" + cls }
                : new[] { "ref_shoot_" + cls, "crouch_shoot_" + cls };
        }

        /// <summary>角色换弹时要播的 state 候选（蹲姿优先，其次站姿）。</summary>
        public static string[] PlayerReloadStates(string weaponId, bool crouch)
        {
            var cls = PlayerAnimClass(weaponId);
            if (string.IsNullOrEmpty(cls)) return new string[0];
            // 原版**没有** ref_reload_ak47 / ref_reload_grenade / ref_reload_knife（实测 group0 标签表）
            if (cls == "ak47") cls = "rifle";
            if (cls == "grenade" || cls == "knife") return new string[0];
            return crouch
                ? new[] { "crouch_reload_" + cls, "ref_reload_" + cls }
                : new[] { "ref_reload_" + cls };
        }

        // ==================================================================
        //  日志
        // ==================================================================
        /// <summary>同类日志的打点间隔（首次 + 每 N 次）—— 与其它模块同口径。</summary>
        public const int LogRateEvery = 50;
    }
}
