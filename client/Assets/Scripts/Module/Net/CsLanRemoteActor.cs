namespace Cs16.Module.Net
{
    /// <summary>
    /// **对端角色的只读快照**（本项目新增，差异 #88「能开局」第②段）。
    ///
    /// <para><b>为什么要有这个类</b>：主机每秒 10 次推来一行
    /// <c>CS16-LAN-SNAP/1|{"round":…,"actors":[{…}]}</c>（字段见
    /// <see cref="CsLanGateway"/> 的线格式说明）；客户端要把其中的 <c>actors[]</c> 画出来，
    /// 就需要一个**与线格式一一对应**的载体 —— 它就是那个载体：
    /// 一个字段对应 JSON 里的一个 key，不多不少（字段名/ 顺序都不许自己改）。</para>
    ///
    /// <para><b>它不是 <c>CsActor</c></b>（区别很重要，别混用）：
    /// <c>CsActor</c> 是**本机模拟的权威实体**（<c>CsMatch</c> 每帧 Tick 它、写它的位置/血量/弹药）；
    /// 本类只是**对端权威的一份拷贝**，本机既不 Tick 它、也不改它 ⇒
    /// 远端角色不参与本机的碰撞 / 命中 / 音频链（只到"看得见"这一层，见
    /// <see cref="Cs16.Module.View.CsLanRemoteView"/> 的类注释）。</para>
    ///
    /// <para><b>字段出处（逐字）</b>：<c>client/Assets/Scripts/Module/Net/CsLanGateway.cs</c>
    /// 的 <c>BuildSnapshot</c> 里 <c>actors[i]</c> 拼的就是这些键 ——
    /// <c>id / name / team / x / y / z / yaw / hp / alive / w</c>；
    /// 其中 <c>x/y/z</c> 是三位小数、<c>yaw</c> 一位小数（<c>team</c> / <c>w</c> 是字符串）。
    /// ⛔ 两端必须同值：改这里等于改线格式（本片不许改）。</para>
    /// </summary>
    public sealed class CsLanRemoteActor
    {
        /// <summary>对端 actor id（主机权威的 <c>CsActor.Id</c>）。</summary>
        public int Id;

        /// <summary>对端玩家名（主机权威的 <c>CsActor.Name</c>）。</summary>
        public string Name;

        /// <summary>对端阵营的**字符串**形态（<c>"T"</c> / <c>"CT"</c> / <c>"Spectator"</c> —— 与 <c>CsTeam.ToString()</c> 同值）。</summary>
        public string Team;

        /// <summary>世界坐标（米）——主机权威的 <c>CsActor.Position</c>。</summary>
        public float X, Y, Z;

        /// <summary>水平朝向（度，0=+Z）——主机权威的 <c>CsActor.Yaw</c>。</summary>
        public float Yaw;

        /// <summary>血量（主机权威的 <c>CsActor.Health</c>）。</summary>
        public int Hp;

        /// <summary>是否存活（主机权威的 <c>CsActor.IsAlive</c>）；false = 这具是尸体。</summary>
        public bool Alive;

        /// <summary>当前手持武器 id（主机权威的 <c>CsActor.ActiveWeapon</c>）。</summary>
        public string Weapon;
    }
}
