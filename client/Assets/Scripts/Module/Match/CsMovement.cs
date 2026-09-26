using Cs16.Core;
using UnityEngine;

namespace Cs16.Module.Match
{
    /// <summary>
    /// 地面 / 空中移动解算 = 原版 <c>PM_Friction</c> + <c>PM_Accelerate</c> / <c>PM_AirAccelerate</c> 的机制形状。
    ///
    /// <para><b>为什么必须只有一份实现</b>：本地输入、远端输入回放、机器人驱动三条路都要走这里。
    /// 各写一份会让"起步、刹停、空中转向"在真人 / 远端 / bot 之间分叉成三种手感。</para>
    ///
    /// <para><b>机制出处（只证机制形状，cvar 数值另见 <see cref="CsConst"/>）</b> =
    /// <c>原版资源/hlsdk/pm_shared/pm_shared.c</c>：
    /// <c>PM_Friction</c>（:1059-1114）、<c>PM_Accelerate</c>（:899-924）、
    /// <c>PM_AirAccelerate</c>（:1116-1143）、<c>PM_WalkMove</c> 的 wishspeed 钳制与"几乎没动就归零"
    /// （:970-989）。原版 <c>PM_PlayerMove</c> 里摩擦只在<b>踩在地面</b>时施加，空中只做加速度。</para>
    ///
    /// <para><b>只改水平分量</b>：原版 <c>PM_WalkMove</c> 在加速前后都把 <c>velocity[2]</c> 置 0，
    /// 竖直分量由重力与贴地单独管；本工程的竖直分量同样归 <c>CsMatch.StepActorPhysics</c> 管，
    /// 所以这里只动 x/z。</para>
    /// </summary>
    public static class CsMovement
    {
        /// <summary>原版摩擦里"速度太小就直接返回"的阈值 = <c>0.1</c> unit/s ⇒ ×0.0254（<c>PM_Friction</c> :1074）。</summary>
        private const float FrictionMinSpeed = 0.1f * 0.0254f;

        /// <summary>原版"这一步几乎没动就把速度清掉"的阈值 = <c>1.0</c> unit/s ⇒ ×0.0254（<c>PM_WalkMove</c> :985）。</summary>
        private const float MinMoveSpeed = 1.0f * 0.0254f;

        /// <summary>空中加速的期望速度上限 = <c>PM_AirAccelerate</c> 里的 <c>30</c> unit/s ⇒ 0.762 m/s（:1127）。</summary>
        private const float AirWishSpeedCap = 30f * 0.0254f;

        /// <summary>
        /// 把 <paramref name="velocity"/> 推进一帧（<paramref name="dt"/> 秒）。
        ///
        /// <para><paramref name="wishDir"/> = 水平**单位**方向向量（没有输入时是零向量）；
        /// <paramref name="wishSpeed"/> = 本帧期望的水平速度（= 武器档最大速度 × 输入力度，≤ <see cref="CsConst.SvMaxSpeed"/>）。
        /// <paramref name="onGround"/> 取**上一帧**的贴地标志（与原版 <c>pmove-&gt;onground</c> 同口径）。</para>
        /// </summary>
        public static Vector3 Solve(Vector3 velocity, Vector3 wishDir, float wishSpeed, bool onGround, float dt)
        {
            if (dt <= 0f) return velocity;

            wishDir.y = 0f;
            var len = wishDir.magnitude;
            if (len > 1f)
            {
                wishDir /= len;
                len = 1f;
            }

            // PM_WalkMove：期望速度先被 sv_maxspeed 钳一次（原版 :970-974）。
            if (wishSpeed > CsConst.SvMaxSpeed) wishSpeed = CsConst.SvMaxSpeed;
            if (len <= 0f) wishSpeed = 0f;

            if (onGround)
            {
                velocity = Friction(velocity, dt);
                velocity = Accelerate(velocity, wishDir, wishSpeed, CsConst.SvAccelerate, dt);

                // PM_WalkMove :985 —— 水平速度小到几乎没有就清掉（否则会无限小地"滑"下去）。
                var h = new Vector2(velocity.x, velocity.z);
                if (h.magnitude < MinMoveSpeed)
                {
                    velocity.x = 0f;
                    velocity.z = 0f;
                }
            }
            else
            {
                velocity = AirAccelerate(velocity, wishDir, wishSpeed, CsConst.SvAirAccelerate, dt);
            }

            return velocity;
        }

        /// <summary><c>PM_Friction</c>（:1059-1114）：按"控制速度"减速，每帧丢掉 <c>control × friction × dt</c> 的比例。</summary>
        private static Vector3 Friction(Vector3 velocity, float dt)
        {
            var speed = new Vector2(velocity.x, velocity.z).magnitude;
            if (speed < FrictionMinSpeed) return velocity;

            // 原版的 edgefriction（前方没地面时 ×2）需要一次前瞻射线，本工程不实现 ⇒ 只取 sv_friction 本体。
            var control = speed < CsConst.SvStopSpeed ? CsConst.SvStopSpeed : speed;
            var drop = control * CsConst.SvFriction * dt;

            var newSpeed = speed - drop;
            if (newSpeed < 0f) newSpeed = 0f;
            var scale = newSpeed / speed;

            velocity.x *= scale;
            velocity.z *= scale;
            return velocity;
        }

        /// <summary><c>PM_Accelerate</c>（:899-924）：沿 wishdir 补速度，本帧最多补到 <c>addspeed</c>。</summary>
        private static Vector3 Accelerate(Vector3 velocity, Vector3 wishDir, float wishSpeed, float accel, float dt)
        {
            var current = velocity.x * wishDir.x + velocity.z * wishDir.z;
            var addSpeed = wishSpeed - current;
            if (addSpeed <= 0f) return velocity;

            var accelSpeed = accel * dt * wishSpeed;
            if (accelSpeed > addSpeed) accelSpeed = addSpeed;

            velocity.x += accelSpeed * wishDir.x;
            velocity.z += accelSpeed * wishDir.z;
            return velocity;
        }

        /// <summary><c>PM_AirAccelerate</c>（:1116-1143）：期望速度先被 <b>30 unit/s</b> 夹住，
        /// 于是空中转向能改变方向、但推不高速率（这就是原版的"空中加速"手感）。</summary>
        private static Vector3 AirAccelerate(Vector3 velocity, Vector3 wishDir, float wishSpeed, float accel, float dt)
        {
            var wishSpd = wishSpeed > AirWishSpeedCap ? AirWishSpeedCap : wishSpeed;

            var current = velocity.x * wishDir.x + velocity.z * wishDir.z;
            var addSpeed = wishSpd - current;
            if (addSpeed <= 0f) return velocity;

            var accelSpeed = accel * wishSpeed * dt;
            if (accelSpeed > addSpeed) accelSpeed = addSpeed;

            velocity.x += accelSpeed * wishDir.x;
            velocity.z += accelSpeed * wishDir.z;
            return velocity;
        }
    }
}
