using System.Collections.Generic;
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

        /// <summary>
        /// 角色/武器**贴图目录**（<c>Resources</c> 路径，不带扩展名）。
        /// 出处：**本项目新增**（生成器 <c>ArtSetup</c> 把 PNG 落在 <c>Assets/Resources/Art/Tex</c>）。
        ///
        /// <para><b>为什么运行时也要它</b>：一批历史材质的 <c>_MainTex</c> 是空的（生成器只找
        /// 「裸贴图名」PNG，而盘上只有带模型前缀的 <c>player_T_arctic_ARTIC_Working1.png</c> 这类文件）
        /// ⇒ <c>ActorView</c> 用 <c>Resources.LoadAll&lt;Texture2D&gt;</c> 从这里按名字回绑贴图。</para>
        /// </summary>
        public const string TexArtRoot = ArtRoot + "Tex";

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

        /// <summary>世界掉落武器模型前缀（Art/world_{武器id}）。出处：**本项目新增**（本工程预制体命名；
        /// 原版世界模型文件名是 <c>models/w_&lt;武器&gt;.mdl</c>（见规格 §5）⇒ 前缀不同源）。
        /// 无阵营之分：原版世界模型两边共用一份。</summary>
        public const string WorldModelPrefix = "world_";

        /// <summary>
        /// **玩家模型里"背上的 C4 包"那一块的材质名关键字**。
        ///
        /// <para><b>出处</b>：原版玩家模型 <c>models/player/*/*.mdl</c> 的
        /// <c>bodyparts[1]</c> 名为 <c>backpack</c>，两个子模型 = 空壳与包本身；内嵌贴图名是
        /// <c>Backpack1</c>（T 侧）/ <c>Backpack2</c>（CT 侧）。生成器按内嵌贴图名给材质命名，
        /// 因此这一块在预制体里是**独立的一个节点**（<c>SkinN</c> + 一个 <c>*Backpack*</c> 材质）
        /// ⇒ 用材质名认它，就能整块开关（等价于原版对该 bodygroup 的开关）。</para>
        ///
        /// <para><b>语义</b>：只有持 C4 的角色显示它（下包 / 掉包 / 交包后立即隐藏）。</para>
        /// </summary>
        public const string BackpackMaterialToken = "Backpack";

        /// <summary>角色预制体里承载"整体缩放/贴地"的子节点名。
        /// 出处：**本项目新增**（预制体内部节点名 = 本工程命名）。</summary>
        public const string BodyNodeName = "Body";

        /// <summary>命中区子节点名前缀：<c>Hitbox_{CsHitbox}</c>（Hitbox_Head / Hitbox_Chest / ...）。
        /// 出处：**本项目新增**（节点名前缀 = 本工程命名；命中区的**语义**出处 = <c>CsHitbox</c> 枚举，见 <c>Core/CsEnums.cs</c>）。</summary>
        public const string HitboxNodePrefix = "Hitbox_";

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
        /// 而同源常量 <c>CsConst.CrouchHeight</c> = 1.27m（差 3.5%）。再叠一个"整体压扁"就是蹲两次，
        /// "没有蹲动画"时期的替代品。
        /// </summary>
        public const float CrouchScale = 1f;

        /// <summary>站/蹲缩放平滑时间常数（秒）。</summary>
        public const float CrouchBlendTau = 0.08f;

        /// <summary>
        /// 逐帧脚底贴地对齐量的上限（米）。超过它说明包围盒或姿态异常（例如最低点根本不是脚），
        /// 宁可按上限对齐也不把整具模型拉飞。
        ///
        /// <para>量级参照：本工程角色模型 <c>idle1 / walk / crouch_idle</c> 各自需要的对齐量实测都在
        /// <b>±0.04 m</b> 内（行走途中双脚同时离地最大约 3.5 cm、蹲静止悬空约 1.1 cm）；原版这些姿态的
        /// 脚底校正由引擎按骨骼做，其数值载体不在盘 ⇒ 本上限是**本项目新增**的防御性封顶值。</para>
        /// </summary>
        public const float FootAlignLimit = 0.12f;

        /// <summary>一次位置跳变超过它（米）就直接吸附，不做插值（出生 / 复活 / 传送）。</summary>
        public const float TeleportSnapDistance = 2.5f;

        // ---- 脚底贴地圆影（blob shadow，见 CsBlobShadow）----
        /// <summary>
        /// 脚底圆影的半径（米）。取值 = <see cref="CsConst.PlayerRadius"/>（玩家碰撞半径 0.36 m）
        /// ⇒ 影子足迹与碰撞足迹同宽。原版 GoldSrc 没有玩家投影的贴图载体
        /// （<c>原版资源/cs16src/*/sprites/</c> 下只有 blood 系与 HUD 系 spr，逐一查过），
        /// 圆斑由 <see cref="CsBlobShadow"/> 程序化生成。
        /// </summary>
        public const float BlobShadowRadius = CsConst.PlayerRadius;

        /// <summary>脚底圆影的不透明度（纯黑 × 该 alpha）。取常用区间的中值 0.42
        /// （区间 0.35~0.5：低于下界在 dust2 的浅色地面看不出来，高于上界像一块脏斑）。</summary>
        public const float BlobShadowAlpha = 0.42f;

        /// <summary>圆影离脚面的抬升（米）：0.015 m 避免与地面共面 z-fighting，又不到肉眼可辨的悬空。</summary>
        public const float BlobShadowLift = 0.015f;

        /// <summary>离地（跳跃 / 下落）时圆影缩到的比例 —— 越高离得越远，影子越小。</summary>
        public const float BlobShadowAirScale = 0.55f;

        /// <summary>圆影透明度 / 尺寸的平滑时间常数（秒）：跳起时 0.08 s 内淡出，落地时同速淡回。</summary>
        public const float BlobShadowBlendTau = 0.08f;

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
        /// <c>view.cpp:669</c> 的 <c>view->origin[2] -= 1</c> 之后，<c>view.cpp:673-687</c> 只给
        /// 110→+1 / 100→+2 / 90→+1 / 80→+0.5 unit 四档做补偿，
        /// 而原版 <c>viewsize</c> 的**出厂默认值实测 = <c>120</c></b>
        /// （出处：<c>原版资源/cs16src/hw.dll</c> 的 cvar 字面量区 —— <c>viewsize</c> 串在 <c>@1537468</c>
        /// 与 <c>@1537668</c>（= <c>0x177684</c>，同一处字面量区的两份副本、**不是两个地址**），
        /// 各自的 <c>120</c> 在 <c>@1537480</c> / <c>@1537680</c>；
        /// 实测片段 <c>…"30" "viewsize" "120" "viewsize" …</c>；HLSDK 里 <c>viewsize</c> 只是
        /// <c>common/ref_params.h:50</c> 的一个字段、**没有注册**它 ⇒ 默认值由引擎填）——
        /// <c>120</c> **不落在那四档里** ⇒ 走**无补偿**分支
        /// ⇒ 原版净偏移就是 <c>view.cpp:669</c> 的 <b>−1 unit = −0.0254 m</b>，与本字段完全一致
        /// （<c>策划/对照表.md</c> A-06 差值 = 0）。</para>
        /// </summary>
        public static readonly Vector3 ViewModelLocalPosition = new Vector3(0f, -0.0254f, 0f);

        /// <summary>武器模型在相机下的局部欧拉角（度）。同样为零（模型自带朝向）。</summary>
        public static readonly Vector3 ViewModelLocalEuler = Vector3.zero;

        /// <summary>武器模型的额外缩放（1 = 按 HL 单位换算后的真实尺寸）。</summary>
        public const float ViewModelScale = 1f;

        /// <summary>世界掉落武器模型的额外缩放（1 = 按 HL 单位换算后的真实尺寸）。</summary>
        public const float WorldModelScale = 1f;

        /// <summary>
        /// 第三人称手持武器的挂点（<c>Body</c> 局部坐标，米）。
        ///
        /// <para><b>出处与降级</b>：原版第三人称武器 = 引擎把 <c>models/p_*.mdl</c> 自动对齐到
        /// <c>player.mdl</c> 的手部骨骼上（<c>原版资源/cs16src/hlsdk</c> 的
        /// <c>CBasePlayerItem::SetModel</c> + studiomdl 骨骼对齐）；本工程的角色预制体是
        /// **命中盒式**（<see cref="ActorView"/> 层级约定：Body + 5 个 Hitbox，无手部骨骼）
        /// ⇒ 没有骨骼可挂，退到按用户实机截图量测：持枪姿态双手位于胸前 ≈ <c>0.72 × 身高</c>
        /// = <c>1.8 × 0.72 ≈ 1.30 m</c>、武器居中偏右 <c>0.10 m</c>、身前 <c>0.18 m</c>。
        /// 已登记 <c>策划/差异登记.tsv</c>（与 #75 同族的挂点降级）。</para>
        /// </summary>
        public static readonly Vector3 HeldWeaponLocalPosition = new Vector3(0.10f, 1.30f, 0.18f);

        /// <summary>当前手持武器的视图预制体不存在时，退到哪里（避免"手上空着"）。</summary>
        public const string ViewModelFallbackWeapon = CsWeapons.Knife;

        // ==================================================================
        //  第三人称持枪：逐武器对齐（手骨局部）
        // ==================================================================
        /// <summary>
        /// 第三人称持枪时的**手骨局部变换**：位置（米）、欧拉角（度，Unity 口径 <c>Z→X→Y</c>），
        /// 以及枪口点（手骨局部，米，第三人称开火的火光落点）。
        ///
        /// <para><b>三个数各自的出处（都不是猜的）</b>：原版第三人称不是"把世界模型挂到手上"，
        /// 而是引擎把 <c>models/p_&lt;武器&gt;.mdl</c> 蒙皮到玩家模型的右手骨
        /// （<c>原版资源/cs16src/cstrike/p_&lt;武器&gt;.mdl</c>，本工程的玩家骨架与它逐骨一致到 1e-4 unit）。
        /// 那两份模型装的是**同一份武器几何**：<c>p_</c> 的顶点在"手骨坐标系"里、<c>w_</c> 的在"自己的原点"里
        /// ⇒ 手骨局部变换 = <c>(p_ 手骨绑定世界)<sup>-1</sup> · (p_ 武器骨绑定世界) · (w_ 武器骨绑定世界)<sup>-1</sup>，
        /// 逐顶点校验残差 ≤ 4.4e-16 m。</para>
        ///
        /// <para><b>枪口列</b>：原版 <c>p_</c> 模型里那根 <c>flash / Mflash</c> 骨的世界位置
        /// （少数枪不带该骨 —— 只能取武器几何离武器骨最远的那个顶点，见
        /// <c>策划/差异登记.tsv</c> 的手部对齐行）。</para>
        ///
        /// <para><b>导出空间口径</b>：与工程内 <c>.cs16anim</c> 一致
        /// （<c>P(x,y,z)=(y,z,x)</c>、角色按"模型高 1.8 m"归一，出处
        /// <c>原版资源/cs16src/cs16_anim.py</c> 的空间与骨骼两段）。</para>
        /// </summary>
        public struct HeldWeaponAlign
        {
            /// <summary>枪身原点在手骨局部的位置（米）。</summary>
            public Vector3 LocalPosition;
            /// <summary>枪身在手骨局部的朝向（度，<c>Quaternion.Euler</c> 口径）。</summary>
            public Vector3 LocalEuler;
            /// <summary>枪口点（手骨局部，米）——第三人称开火的火光落在这里。</summary>
            public Vector3 MuzzleLocalPosition;

            public HeldWeaponAlign(Vector3 pos, Vector3 euler, Vector3 muzzle)
            {
                LocalPosition = pos;
                LocalEuler = euler;
                MuzzleLocalPosition = muzzle;
            }
        }

        /// <summary>表里没有的武器（理论到不了：只有带 <c>w_</c> 世界模型的武器才走这张表）。</summary>
        public static readonly HeldWeaponAlign DefaultHeldWeaponAlign =
            new HeldWeaponAlign(Vector3.zero, Vector3.zero, Vector3.zero);

        /// <summary>
        /// 角色预制体骨架比例 / 世界模型比例（<c>0.025918 / 0.0254</c>）。
        ///
        /// <para>角色骨骼按"模型高 = 1.8 m"归一（terror.mdl 绑定高 69.450 unit ⇒ 1 unit = 0.0259183 m），
        /// 世界武器按原版固定单位（1 unit = <see cref="CsConst.UnitToMeter"/> = 0.0254 m）
        /// ⇒ 武器作为手骨的子节点时必须补上这个比，否则枪比拿它的人小 2%。</para>
        /// </summary>
        public const float HeldWeaponScale = 1.02040f;

        /// <summary>
        /// 找不到右手骨骼时（挂点退回 <c>Body</c> 固定节点）的枪口落点（<c>Body</c> 局部，米）。
        /// 出处：**本项目新增** —— 这条是挂点降级路径的兜底；正常路径的枪口见上表的枪口列。
        /// </summary>
        public static readonly Vector3 HeldWeaponMuzzleFallbackLocal = new Vector3(0.10f, 1.20f, 0.60f);

        /// <summary>
        /// 逐武器的第三人称持枪对齐表。
        ///
        /// <para>数据出处与推导见 <see cref="HeldWeaponAlign"/> 的注释；每一行都来自
        /// <c>原版资源/cs16src/cstrike/p_&lt;武器&gt;.mdl</c> + <c>cstrike__models__w_&lt;武器&gt;.mdl</c>
        /// 两份原版模型的实测值（不是"看起来合理"的目测值）。</para>
        /// </summary>
        private static readonly Dictionary<string, HeldWeaponAlign> HeldWeaponAligns =
            new Dictionary<string, HeldWeaponAlign>(32)
        {
            { CsWeapons.Ak47, new HeldWeaponAlign(
                new Vector3( 0.01645f,   0.01089f,   0.27892f), new Vector3(  24.439f,    -3.255f,    91.057f),
                new Vector3(-0.04913f,  -0.05312f,   0.77788f)) },
            { CsWeapons.M4A1, new HeldWeaponAlign(
                new Vector3( 0.00963f,   0.01375f,   0.38153f), new Vector3( -21.041f,    -4.633f,    91.415f),
                new Vector3(-0.05787f,  -0.09512f,   0.92193f)) },
            { CsWeapons.Sg552, new HeldWeaponAlign(
                new Vector3( 0.01794f,   0.05256f,   0.19111f), new Vector3(  49.561f,    -0.905f,    92.003f),
                new Vector3(-0.03222f,   0.01007f,   0.62860f)) },
            { CsWeapons.Aug, new HeldWeaponAlign(
                new Vector3( 0.01673f,   0.03300f,   0.11799f), new Vector3(  39.936f,    -2.886f,    91.255f),
                new Vector3(-0.03464f,  -0.00487f,   0.61859f)) },
            { CsWeapons.Galil, new HeldWeaponAlign(
                new Vector3(-0.01938f,   0.01756f,   0.32789f), new Vector3( -83.253f,  -148.891f,  -125.500f),
                new Vector3(-0.10454f,  -0.10080f,   1.01104f)) },
            { CsWeapons.Famas, new HeldWeaponAlign(
                new Vector3( 0.02018f,   0.06249f,   0.14319f), new Vector3( -52.047f,    -4.925f,    91.564f),
                new Vector3(-0.04551f,   0.01691f,   0.75637f)) },
            { CsWeapons.Awp, new HeldWeaponAlign(
                new Vector3( 0.01282f,  -0.02785f,   0.31266f), new Vector3(  27.955f,    -4.181f,    91.090f),
                new Vector3(-0.05537f,  -0.07955f,   0.86144f)) },
            { CsWeapons.Scout, new HeldWeaponAlign(
                new Vector3( 0.00606f,   0.01193f,   0.28731f), new Vector3( -12.330f,    -4.651f,    89.799f),
                new Vector3(-0.06167f,  -0.06820f,   0.85288f)) },
            { CsWeapons.G3sg1, new HeldWeaponAlign(
                new Vector3( 0.01634f,   0.00766f,   0.26013f), new Vector3(  27.999f,    -4.601f,    86.372f),
                new Vector3(-0.04381f,  -0.04012f,   0.83493f)) },
            { CsWeapons.Sg550, new HeldWeaponAlign(
                new Vector3( 0.00913f,   0.01440f,   0.31177f), new Vector3(  35.937f,    -2.994f,    91.188f),
                new Vector3(-0.04998f,  -0.06460f,   0.88044f)) },
            { CsWeapons.M249, new HeldWeaponAlign(
                new Vector3( 0.05970f,   0.08320f,   0.32005f), new Vector3(  -3.135f,     3.754f,   100.078f),
                new Vector3( 0.04675f,   0.24514f,   0.78557f)) },
            { CsWeapons.Mp5, new HeldWeaponAlign(
                new Vector3(-0.05054f,   0.05054f,   0.32351f), new Vector3(   0.000f,   -99.000f,  -180.000f),
                new Vector3(-0.03823f,  -0.00726f,   0.55821f)) },
            { CsWeapons.Tmp, new HeldWeaponAlign(
                new Vector3( 0.01440f,   0.04386f,   0.16361f), new Vector3(  52.349f,     2.544f,    91.561f),
                new Vector3( 0.00591f,   0.02740f,   0.43292f)) },
            { CsWeapons.Mac10, new HeldWeaponAlign(
                new Vector3(-0.01666f,  -0.12746f,  -0.12352f), new Vector3(   9.452f,     0.978f,   -86.547f),
                new Vector3(-0.00030f,   0.04723f,   0.29410f)) },
            { CsWeapons.Ump45, new HeldWeaponAlign(
                new Vector3( 0.00790f,   0.00868f,   0.10767f), new Vector3( -10.648f,     1.630f,    90.969f),
                new Vector3( 0.00510f,   0.02179f,   0.40967f)) },
            { CsWeapons.P90, new HeldWeaponAlign(
                new Vector3( 0.01462f,   0.06006f,   0.12127f), new Vector3(  41.352f,     2.148f,    91.270f),
                new Vector3( 0.00564f,   0.01688f,   0.44162f)) },
            { CsWeapons.M3, new HeldWeaponAlign(
                new Vector3(-0.00260f,   0.00666f,   0.23400f), new Vector3(  46.941f,    -5.304f,    88.480f),
                new Vector3(-0.05683f,  -0.05275f,   0.73517f)) },
            { CsWeapons.Xm1014, new HeldWeaponAlign(
                new Vector3(-0.02731f,   0.09951f,   0.33404f), new Vector3(  75.869f,     9.472f,   -84.372f),
                new Vector3( 0.03817f,   0.19497f,   0.76418f)) },
            { CsWeapons.Glock18, new HeldWeaponAlign(
                new Vector3( 0.00862f,   0.02351f,   0.12873f), new Vector3(  26.371f,    -0.214f,    88.834f),
                new Vector3( 0.00092f,   0.04505f,   0.25871f)) },
            { CsWeapons.Usp, new HeldWeaponAlign(
                new Vector3( 0.01221f,   0.05649f,   0.20081f), new Vector3( -16.132f,     0.963f,    89.432f),
                new Vector3( 0.00216f,   0.02936f,   0.42383f)) },
            { CsWeapons.P228, new HeldWeaponAlign(
                new Vector3( 0.00742f,   0.05120f,   0.12596f), new Vector3( -20.167f,     0.503f,    89.423f),
                new Vector3( 0.00045f,   0.05473f,   0.26495f)) },
            { CsWeapons.Deagle, new HeldWeaponAlign(
                new Vector3( 0.00783f,   0.02317f,   0.14219f), new Vector3(  26.364f,     1.282f,    91.065f),
                new Vector3( 0.00038f,   0.04315f,   0.30375f)) },
            { CsWeapons.FiveSeven, new HeldWeaponAlign(
                new Vector3( 0.00344f,   0.04771f,   0.12237f), new Vector3( -32.429f,    -0.218f,    91.310f),
                new Vector3(-0.00479f,   0.05852f,   0.25929f)) },
            { CsWeapons.Elite, new HeldWeaponAlign(
                new Vector3(-0.15777f,   0.02388f,  -1.71264f), new Vector3( -23.843f,    -7.537f,    99.345f),
                new Vector3( 0.02787f,   0.06716f,   0.29094f)) },
            { CsWeapons.Knife, new HeldWeaponAlign(
                new Vector3(-0.23997f,   0.43944f,   0.01622f), new Vector3(  58.354f,   149.854f,   155.512f),
                new Vector3( 0.06596f,  -0.14660f,   0.00370f)) },
            { CsWeapons.C4, new HeldWeaponAlign(
                new Vector3(-0.01282f,   0.00116f,  -0.09356f), new Vector3(  89.700f,    89.997f,    89.997f),
                new Vector3(-0.18868f,   0.04924f,   0.04095f)) },
        };

        /// <summary>取某把武器的第三人称持枪对齐；不在表里返回 <see cref="DefaultHeldWeaponAlign"/>。</summary>
        public static HeldWeaponAlign HeldWeaponAlignOf(string weaponId)
        {
            if (!string.IsNullOrEmpty(weaponId) && HeldWeaponAligns.TryGetValue(weaponId, out var a)) return a;
            return DefaultHeldWeaponAlign;
        }

        // ==================================================================
        //  骨骼动画（原版序列驱动；**帧率/帧数/关键帧全部来自 mdl 实测**，见
        /// <summary>骨骼层级挂载节点名（生成器在角色预制体的 <c>Body</c> 下 / 视模型根下建它）。</summary>
        public const string SkeletonNodeName = "Skeleton";

        /// <summary>
        /// 状态切换的过渡时长（**秒**）—— 原版序列的 blend 默认 0.1 s。
        ///
        /// <para>引擎 <c>IAnimPlayer.CrossFade(state, seconds)</c> 的第二参就是秒（内部走
        /// <c>Animator.CrossFadeInFixedTime</c>），直接传本值即可。</para>
        /// </summary>
        public const float AnimCrossFade = 0.1f;

        /// <summary>
        /// 判定"在移动"的最小水平速度（米/秒）。不高于它 = 静止（放静止序列）。
        ///
        /// <para><b>出处 = 0</b>：原版 <c>regamedll/dlls/player.cpp:5443-5453</c> 的选序列入口 ——
        /// <c>if (pev-&gt;velocity.x || pev-&gt;velocity.y) SetAnimation(PLAYER_WALK); else SetAnimation(PLAYER_IDLE);</c>；
        /// 判据是**水平两个分量是否为 0**（GoldSrc 是 Z-up，x/y 即水平面），**没有 epsilon**。
        /// 这里是同一口径的标量写法（水平速度 &gt; 0 即算在移动）。</para>
        /// </summary>
        public const float AnimMoveSpeedEpsilon = 0f;

        /// <summary>
        /// 判定"跑步档"的最小水平速度（米/秒）——速度不高于它 = 走路档。
        ///
        /// <para><b>出处</b>：原版 <c>cstrike/dlls/mp.dll</c> 的 <c>CBasePlayer::SetAnimation</c> ——
        /// <c>0x100673DE</c> 的 <c>fld dword ptr [0x101421B0]</c> 取的常量实测 = <c>135.0f</c>（unit/s），
        /// 紧随的 <c>0x100673E4</c> 用它和本帧速度比较：<b>速度 &gt; 135 ⇒ 序列 4、否则序列 3</b>
        /// （<c>terror.mdl</c> seq[3] = <c>walk</c> fps30/33帧、seq[4] = <c>run</c> fps60/37帧）。
        /// 135 unit/s × <see cref="CsConst.UnitToMeter"/> = <b>3.429 m/s</b>。</para>
        ///
        /// <para>原版这一步**不看 Shift 标志**：按下 Shift 只是把期望速度乘到 0.42 ⇒ 实际速度自然落到
        /// 阈值以下，动画随之转 <c>walk</c>。反过来，减速到停的过程中速度从 250 一路降到 0，
        /// 落到阈值以下的那一段也必须转 <c>walk</c> —— 按 Shift 标志切档就会在整段减速里一直播
        /// 为 250 u/s 设计的 <c>run</c>（60fps 循环）⇒ 腿在飞、人在挪。</para>
        /// </summary>
        public const float AnimRunSpeedThreshold = 135f * CsConst.UnitToMeter;

        /// <summary>
        /// 走 / 跑分档的迟滞带（米/秒）：在 <see cref="AnimRunSpeedThreshold"/> 两侧各留这么宽，
        /// 速度落在带内时保持当前档，不重新发起 0.1 s 过渡。
        ///
        /// <para>原版是直接换整段序列（无过渡），不需要这条带；本工程的位移序列按走过的距离推进、
        /// 切换时走 <c>CrossFade</c>。速度在阈值附近逐帧抖动时（撞墙清零 / 加速交替），不带迟滞
        /// 会每帧重启一次过渡 —— 过渡期间 Animator 报不出当前 clip，画面退回绑定姿态。</para>
        /// </summary>
        public const float AnimRunSpeedHysteresis = 0.35f;

        /// <summary>
        /// 位移序列的**设计速度**（米/秒）= 该序列一个循环走过的地面距离 ÷ 循环时长。
        ///
        /// <para><b>出处</b>：原版 <c>mstudioseqdesc_t.linearmovement.x</c> 与 fps/numframes，
        /// 逐条读自 <c>原版资源/cs16src/cstrike/cstrike__models__player__terror__terror.mdl</c>
        /// （结构见 <c>原版资源/cs16src/cs16_mdl.py</c> 的 <c>Sequence</c>；<c>gsg9.mdl</c> 同值）：
        /// <c>walk</c> 64 u / 1.067 s = 60 u/s、<c>run</c> 150 u / 0.600 s = 250 u/s、
        /// <c>crouchrun</c> 75 u / 1.000 s = 75 u/s。</para>
        ///
        /// <para><b>为什么要它</b>：原版按**走过的距离**推进步态
        /// （<c>player.cpp:9192-9195</c>：<c>m_flGaitframe += (m_flGaitMovement / linearmovement.x) * numframes</c>）
        /// ⇒ 播放速率 = 实际速度 ÷ 本值。固定 1 倍速会让慢走时脚步周期比地面速度慢数倍（滑步）。</para>
        /// </summary>
        /// <returns>该状态的设计速度（m/s）；不是位移序列返回 0（调用方保持 1 倍速）。</returns>
        public static float LocoDesignSpeed(string state)
        {
            if (string.IsNullOrEmpty(state)) return 0f;
            if (string.Equals(state, PStateWalk[0], System.StringComparison.Ordinal)) return 60f * CsConst.UnitToMeter;
            if (string.Equals(state, PStateRun[0], System.StringComparison.Ordinal)) return 250f * CsConst.UnitToMeter;
            if (string.Equals(state, PStateCrouchRun[0], System.StringComparison.Ordinal)) return 75f * CsConst.UnitToMeter;
            return 0f;
        }

        /// <summary>角色静止（站）。</summary>
        public static readonly string[] PStateIdle = { "idle1", "idle" };

        /// <summary>角色走路档（水平速度不高于 <see cref="AnimRunSpeedThreshold"/>）。</summary>
        public static readonly string[] PStateWalk = { "walk", "run" };

        /// <summary>角色跑步档（水平速度高于 <see cref="AnimRunSpeedThreshold"/>）。</summary>
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
        /// 蹲姿死亡。出处：<c>mp.dll</c> <c>0x100676F7</c> 的
        /// <c>test dword ptr [ecx+0x1A4], 0x4000</c>（<c>0x4000</c> = GoldSrc 的 <c>FL_DUCKING</c>；
        /// <c>ecx</c> = <c>[this+4]</c> = <c>pev</c>），命中即 <c>0x10067703</c> 的
        /// <c>SetSequenceByName("crouch_die")</c>（字符串在 <c>0x10118AA8</c>）。
        /// 实测 <c>crouch_die</c> = fps30/31帧；原版模型里**没有**蹲姿的 death1..3，
        /// 所以蹲着死只有这一条正解（候选表只放它，缺状态时由 <see cref="PStateDeath"/> 兜底）。
        /// </summary>
        public static readonly string[] PStateDeathCrouch = { "crouch_die" };

        /// <summary>
        /// 受击抖动（中弹**未死**）：原版在同一分支里用 <c>RANDOM(0,1)</c> 二选一。出处：
        /// <c>mp.dll</c> <c>0x10067439</c>（<c>push 1; push 0; call RANDOM</c>，返回值在 <c>eax</c>）——
        /// <c>test eax,eax / je</c> 为假（= 1）走 <c>0x1006744C</c> 引用 <c>0x10118A68</c>
        /// （<c>"head_flinch"</c>）、为真（= 0）走 <c>0x10067453</c> 引用 <c>0x10118A74</c>
        /// （<c>"gut_flinch"</c>）。两条实测均 fps30/2帧（约 67 ms），播完接回位移状态。
        /// </summary>
        public static readonly string[] PStateFlinchHead = { "head_flinch" };
        public static readonly string[] PStateFlinchGut = { "gut_flinch" };

        /// <summary>
        /// 倒地序列播完后，把 Animator 冻住的速度（**0 = 停在最后一帧**）。
        ///
        /// <para><b>为什么是 0 而不是"继续播"</b>：原版尸体是**躺在被打倒的地方**直到本局结束。
        /// 倒地序列是一次性 clip，播完不冻就会被状态机拉回 idle（尸体站起来），或整具被隐藏（尸体消失）。
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

        // ==================================================================
        //  视模型：**消音器两态**（原版 `v_usp.mdl` 把两套序列都烘在同一份模型里）
        //
        //  出处 = `原版资源/cs16src/cstrike/cstrike__models__v_usp.mdl` 实测（16 条序列，与
        //  `regamedll/dlls/weapons.h` 的 `enum usp_e` **逐项同序**，本工程读取器 `cs16_mdl.py`）：
        //    0..6  = idle / shoot1 / shoot2 / shoot3 / shootlast / reload / draw      = **装消音**档
        //    7     = add_silencer          （116 帧 @37 fps）
        //    8..14 = idle / shoot1..3 / shootlast / reload / draw 后缀 `_unsil`       = **未装**档
        //    15    = detach_silencer       （116 帧 @37 fps）
        //  `_unsil` 后缀是**枚举自己的名字**（`USP_UNSIL_IDLE` …）⇒ 哪一个档带消音器由名字直接读出。
        //  ⚠️ 序号 0..6 那一套的名字在本工程里是 `idle` / `fire1..3` / `firelast`（`shoot*` → `fire*`
        //  由本工程既有的视模型导出链改名）。
        //
        //  **候选表两段式**：第一段是"该态自己那一条"，第二段回落到装消音那一套 —— 这样即使
        //  新 clip 还没导出（`.anim` / controller 尚未重建），挑不到 `_unsil` 名时仍回落到既有序列，
        //  行为与今天完全一致（零回归）；clip 一到就自动生效。
        // ==================================================================

        /// <summary>视模型：静止（未装消音档）。出处 = 序列 8 `idle_unsil`（<c>USP_UNSIL_IDLE</c>）。</summary>
        public static readonly string[] VmStateIdleUnsil = { "idle_unsil", "idle" };

        /// <summary>视模型：第 1 发（未装消音档）。出处 = 序列 9 `shoot1_unsil`（<c>USP_UNSIL_SHOOT1</c>）。</summary>
        public static readonly string[] VmStateFire1Unsil = { "fire1_unsil", "fire1" };

        /// <summary>视模型：第 2 发（未装消音档）。出处 = 序列 10 `shoot2_unsil`。</summary>
        public static readonly string[] VmStateFire2Unsil = { "fire2_unsil", "fire2", "fire1_unsil", "fire1" };

        /// <summary>视模型：第 3 发（未装消音档）。出处 = 序列 11 `shoot3_unsil`。</summary>
        public static readonly string[] VmStateFire3Unsil = { "fire3_unsil", "fire3", "fire2_unsil", "fire2", "fire1_unsil", "fire1" };

        /// <summary>视模型：弹匣打空那一发（未装消音档）。出处 = 序列 12 `shootlast_unsil`。</summary>
        public static readonly string[] VmStateFireLastUnsil = { "firelast_unsil", "firelast", "fire3_unsil", "fire3", "fire2_unsil", "fire2", "fire1_unsil", "fire1" };

        /// <summary>视模型：换弹（未装消音档）。出处 = 序列 13 `reload_unsil`（<c>USP_UNSIL_RELOAD</c>）。</summary>
        public static readonly string[] VmStateReloadUnsil = { "reload_unsil", "reload" };

        /// <summary>视模型：切枪抬起（未装消音档）。出处 = 序列 14 `draw_unsil`（<c>USP_UNSIL_DRAW</c>）。</summary>
        public static readonly string[] VmStateDrawUnsil = { "draw_unsil", "draw" };

        /// <summary>视模型：**装上**消音器。出处 = 序列 7 `add_silencer`（<c>USP_ATTACH_SILENCER</c>，116 帧 @37 fps）。</summary>
        public static readonly string[] VmStateAddSilencer = { "add_silencer" };

        /// <summary>视模型：**拆下**消音器。出处 = 序列 15 `detach_silencer`（<c>USP_DETACH_SILENCER</c>，116 帧 @37 fps）。</summary>
        public static readonly string[] VmStateDetachSilencer = { "detach_silencer" };

        /// <summary>双手手枪在**射击 / 换弹 / 瞄准组**里的动画类后缀 —— mdl 实际标签是
        /// <c>dualpistols</c>（第二条射击组是 <c>shoot2_dualpistols</c>），原版 <c>Deploy()</c> 的
        /// <c>m_szAnimExtension</c> 也是 <c>"dualpistols"</c>，见
        /// <c>原版资源/cs16src/regamedll/dlls/wpn_shared/wpn_elite.cpp:71</c>。</summary>
        private const string DualPistolsSeq = "dualpistols";

        /// <summary><see cref="PlayerAnimClass"/> 给双手手枪的返回值（带 <c>_1</c> 后缀，与 mdl 的
        /// <c>shoot2</c> 组区分开；取序列名时必须折成 <see cref="DualPistolsSeq"/>）。</summary>
        private const string DualPistolsClass = "dualpistols_1";

        /// <summary>
        /// **武器 → 角色模型动画类**。原版把角色模型的射击/换弹序列按"武器动画类"命名
        /// （<c>ref_shoot_&lt;类&gt;</c>），类名集合由 mdl 自己决定 —— 实测 <c>models/player/*/*.mdl</c>
        /// 的 group0 序列里只有这些后缀：<c>carbine / onehanded / dualpistols / rifle / mp5 /
        /// shotgun / m249 / grenade / c4 / knife / ak47 / shield*</c>。
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
        /// <param name="variant">双手手枪的左右手轮换位（0 = 左手组 <c>shoot</c>、1 = 右手组 <c>shoot2</c>）；
        /// 其它武器忽略。原版 <c>CELITE::PrimaryAttack</c> 逐发翻 <c>WPNSTATE_ELITE_LEFT</c> 并分别
        /// <c>SetAnimation(PLAYER_ATTACK1/2)</c>（<c>wpn_shared/wpn_elite.cpp:172-195</c>）⇒ 两条射击组交替播。</param>
        public static string[] PlayerShootStates(string weaponId, bool crouch, int variant = 0)
        {
            var cls = PlayerAnimClass(weaponId);
            if (string.IsNullOrEmpty(cls)) return new string[0];
            // 双手手枪的射击组在 mdl 里叫 ref_shoot_dualpistols / ref_shoot2_dualpistols
            // （= 原版 GetAnimDesired("shoot"/"shoot2") 拼出的那两条，见 player.cpp:2825 / :2840），
            // 没有 ref_shoot_dualpistols_1。
            if (cls == DualPistolsClass)
            {
                cls = DualPistolsSeq;
                if ((variant & 1) != 0)
                {
                    return crouch
                        ? new[] { "crouch_shoot2_" + cls, "crouch_shoot_" + cls, "ref_shoot_" + cls }
                        : new[] { "ref_shoot2_" + cls, "ref_shoot_" + cls, "crouch_shoot_" + cls };
                }
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
            // 双手手枪的换弹组同样没有 *_dualpistols_1（mdl 里是 ref_reload_dualpistols）。
            if (cls == DualPistolsClass) cls = DualPistolsSeq;
            // 原版 mdl 里 ref_reload_ak47 与 ref_reload_rifle 的轨道逐字节相同
            // （两条 clip 的全部骨骼关键帧一致）⇒ 统一走 rifle；grenade / knife 没有换弹序列。
            if (cls == "ak47") cls = "rifle";
            if (cls == "grenade" || cls == "knife") return new string[0];
            return crouch
                ? new[] { "crouch_reload_" + cls, "ref_reload_" + cls }
                : new[] { "ref_reload_" + cls };
        }

        /// <summary>
        /// 角色**站定**时要播的瞄准姿势 state 候选。原版 <c>CBasePlayer::SetAnimation</c> 的 <c>ACT_WALK</c>
        /// 分支里 <c>speed &lt;= 135 u/s</c>（静止 / 慢速档）走 <c>GetAnimDesired("aim", ANIM_CROUCH)</c>，
        /// 拼出的序列名 = <c>ref_aim_&lt;武器动画类&gt;</c>
        /// （出处 <c>原版资源/cs16src/regamedll/dlls/player.cpp:2645-2652</c> 与 <c>:2875-2929</c>）。
        ///
        /// <para><b>类名映射</b>：<see cref="PlayerAnimClass"/> 给双手手枪返 <c>dualpistols_1</c>
        /// （用来区分 <c>shoot2</c> 组），而瞄准组在 mdl 里只有 <c>ref_aim_dualpistols</c>
        /// ⇒ 这里折成 <c>dualpistols</c>；其余类名各组同名。</para>
        ///
        /// <para><b>回落</b>：取不到武器类（空手 / 未知武器）与状态不存在（该皮肤缺这条序列）都落到
        /// 候选表尾部的 <c>idle1</c> / <c>idle</c>（<see cref="ActorView.ResolveState"/> 按顺序取第一个存在的）
        /// ⇒ 两条路的行为都与"没有瞄准姿势"时逐字相同。</para>
        /// </summary>
        public static string[] PlayerAimStates(string weaponId)
        {
            var cls = PlayerAnimClass(weaponId);
            if (string.IsNullOrEmpty(cls)) return PStateIdle;
            if (cls == DualPistolsClass) cls = DualPistolsSeq;
            string[] cached;
            if (AimStatesByClass.TryGetValue(cls, out cached)) return cached;
            cached = new[] { "ref_aim_" + cls, "idle1", "idle" };
            AimStatesByClass[cls] = cached;
            return cached;
        }

        /// <summary>
        /// 角色**蹲姿瞄准**时要播的 state 候选。原版 <c>GetAnimDesired("aim", ANIM_CROUCH)</c> 在
        /// <c>FL_DUCKING</c> 时给序列名加 <c>crouch_</c> 前缀
        /// （出处 <c>原版资源/cs16src/regamedll/dlls/player.cpp:2647</c>）⇒ <c>crouch_aim_&lt;武器动画类&gt;</c>。
        ///
        /// <para>与 <see cref="PlayerAimStates"/> 同口径折 <c>dualpistols_1 → dualpistols</c>；
        /// 取不到武器 / 该皮肤缺这条序列时回落到站姿瞄准（<see cref="PlayerAimStates"/>）——
        /// 上身层不能没有姿势，否则露出绑定姿态的手臂。</para>
        /// </summary>
        public static string[] PlayerCrouchAimStates(string weaponId)
        {
            var cls = PlayerAnimClass(weaponId);
            if (string.IsNullOrEmpty(cls)) return PlayerAimStates(weaponId);
            if (cls == DualPistolsClass) cls = DualPistolsSeq;
            string[] cached;
            if (CrouchAimStatesByClass.TryGetValue(cls, out cached)) return cached;
            var stand = PlayerAimStates(weaponId);
            cached = new[] { "crouch_aim_" + cls, stand[0] };
            CrouchAimStatesByClass[cls] = cached;
            return cached;
        }

        /// <summary>武器动画类 → <see cref="PlayerAimStates"/> 的候选数组（**共用**：该方法在位移状态选择里
        /// 每帧被调用一次，不能每帧新建数组）。</summary>
        private static readonly Dictionary<string, string[]> AimStatesByClass =
            new Dictionary<string, string[]>(System.StringComparer.Ordinal);

        /// <summary>武器动画类 → <see cref="PlayerCrouchAimStates"/> 的候选数组（同上，共用）。</summary>
        private static readonly Dictionary<string, string[]> CrouchAimStatesByClass =
            new Dictionary<string, string[]>(System.StringComparer.Ordinal);

        // ==================================================================
        //  日志
        // ==================================================================
        /// <summary>同类日志的打点间隔（首次 + 每 N 次）—— 与其它模块同口径。</summary>
        public const int LogRateEvery = 50;
    }
}
