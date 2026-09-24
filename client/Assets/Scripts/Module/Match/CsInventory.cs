using System.Collections.Generic;
using CloverEngine;
using Cs16.Core;
using UnityEngine;

namespace Cs16.Module.Match
{
    /// <summary>空中的手雷（抛物线 + 引信倒计时；引信用倒计时而非绝对时间，天然免疫暂停）。</summary>
    internal sealed class CsGrenadeProjectile
    {
        public string WeaponId;
        public long OwnerId;
        public CsTeam OwnerTeam;
        public Vector3 Position;
        public Vector3 Velocity;
        public float FuseLeft;
        public bool Landed;
    }

    /// <summary>地面上的烟雾体积（简化球体）。</summary>
    internal sealed class CsSmokeVolume
    {
        public Vector3 Position;
        public float TimeLeft;
    }

    /// <summary>
    /// 武器 / 弹药 / 槽位 / 开火 / 换弹 / 手雷。
    ///
    /// <para><b>手雷的存放方式</b>：契约里的 <see cref="CsActor"/> 只有
    /// PrimaryWeapon / SecondaryWeapon / KnifeWeapon / ActiveWeapon 四个槽，
    /// <b>没有手雷列表</b>。因此手雷用 <c>Ammo</c> 字典承载 —— <c>Ammo[grenadeId].inMag</c> 即持有数量，
    /// 这不需要改契约。</para>
    /// </summary>
    public sealed class CsInventory
    {
        private const string Tag = CsMatch.Tag;

        private readonly CsMatch _m;
        private readonly List<CsGrenadeProjectile> _grenades = new List<CsGrenadeProjectile>(16);
        private readonly List<CsSmokeVolume> _smokes = new List<CsSmokeVolume>(8);
        private readonly RaycastHit[] _hits = new RaycastHit[16];
        private readonly List<string> _ammoKeys = new List<string>(8);

        internal CsInventory(CsMatch m)
        {
            _m = m;
        }

        internal void Reset()
        {
            _grenades.Clear();
            _smokes.Clear();
        }

        // ==================================================================
        //  速度
        // ==================================================================
        /// <summary>按当前手持武器给移动速度（官方 CS1.6 的 Knife/Pistol/Rifle/AWP 四档）。</summary>
        public static float MovementSpeed(CsActor a)
        {
            var def = a.ActiveDef;

            float speed;
            if (def == null)
            {
                speed = CsConst.SpeedPistol;
            }
            else
            {
                switch (def.Class)
                {
                    case CsWeaponClass.Knife:
                    case CsWeaponClass.Grenade:
                    case CsWeaponClass.Bomb:
                        speed = CsConst.SpeedKnife;
                        break;
                    case CsWeaponClass.Pistol:
                    case CsWeaponClass.Equipment:
                        speed = CsConst.SpeedPistol;
                        break;
                    case CsWeaponClass.Sniper:
                        // AWP 最慢；Scout/连狙按步枪档（CsConst 只给了 SpeedAWP 这一档）。
                        speed = def.Id == CsWeapons.Awp ? CsConst.SpeedAWP : CsConst.SpeedRifle;
                        break;
                    case CsWeaponClass.SMG:
                    case CsWeaponClass.Rifle:
                    case CsWeaponClass.Shotgun:
                    case CsWeaponClass.MachineGun:
                        speed = CsConst.SpeedRifle;
                        break;
                    default:
                        // 本方法是 static（CsMatch 也直接调用它），故不经过 _m 的降频器。
                        Game.Logger.Warn(Tag, $"未知武器大类 {def.Class}，移动速度按步枪档处理");
                        speed = CsConst.SpeedRifle;
                        break;
                }
            }

            if (a.IsCrouching) speed *= CsConst.SpeedCrouchMultiplier;
            else if (a.IsWalking) speed *= CsConst.SpeedWalkMultiplier;

            return speed;
        }

        public static float SwitchTimeFor(CsWeaponDef def)
        {
            if (def == null) return CsConst.SwitchTimePistol;
            switch (def.Slot)
            {
                case CsWeaponSlot.Primary: return CsConst.SwitchTimePrimary;
                case CsWeaponSlot.Secondary: return CsConst.SwitchTimePistol;
                case CsWeaponSlot.Knife: return CsConst.SwitchTimeKnife;
                case CsWeaponSlot.Grenade: return CsConst.SwitchTimePistol;
                case CsWeaponSlot.Bomb: return CsConst.SwitchTimePistol;
                default: return CsConst.SwitchTimePistol;
            }
        }

        // ==================================================================
        //  装备
        // ==================================================================
        /// <summary>清空装备（阵亡 / 换阵营时用）。只留名字，刀由 ApplyDefaultLoadout 补。</summary>
        public void ClearLoadout(CsActor a)
        {
            a.PrimaryWeapon = null;
            a.SecondaryWeapon = null;
            a.KnifeWeapon = CsWeapons.Knife;
            a.Ammo.Clear();
            a.ActiveWeapon = null;
            a.Armor = 0;
            a.HasHelmet = false;
            a.HasDefuser = false;
            a.ConsecutiveShots = 0;
            a.RecoilPitch = 0f;
            a.RecoilYaw = 0f;
        }

        /// <summary>默认装备：刀恒有 + 阵营默认手枪（T=Glock18 / CT=USP）。</summary>
        public void ApplyDefaultLoadout(CsActor a)
        {
            a.KnifeWeapon = CsWeapons.Knife;

            var pistolId = CsWeapons.DefaultPistol(a.Team);
            var def = CsWeapons.Get(pistolId);
            if (def == null)
            {
                _m.RateWarn("loadout.pistol.missing", $"默认手枪 {pistolId} 不在武器表中");
                return;
            }

            a.SecondaryWeapon = pistolId;
            a.SetAmmo(pistolId, def.Magazine, def.ReserveAmmo);
            a.ActiveWeapon = pistolId;
        }

        /// <summary>把持有的每把武器弹药补满（回合开始 / 购买后）。</summary>
        public void ResetAmmo(CsActor a)
        {
            _ammoKeys.Clear();
            foreach (var kv in a.Ammo) _ammoKeys.Add(kv.Key);
            for (var i = 0; i < _ammoKeys.Count; i++)
            {
                var id = _ammoKeys[i];
                var def = CsWeapons.Get(id);
                if (def == null)
                {
                    _m.RateWarn("ammo.unknown." + id, $"弹药重置：未知武器 {id}，已跳过");
                    continue;
                }
                a.SetAmmo(id, def.Magazine, def.ReserveAmmo);
            }
        }

        /// <summary>挑一把"最应该拿在手里"的武器（主武器 → 手枪 → 刀 → 手雷 → C4）。</summary>
        public void SelectBestWeapon(CsActor a)
        {
            if (!string.IsNullOrEmpty(a.PrimaryWeapon)) { a.ActiveWeapon = a.PrimaryWeapon; return; }
            if (!string.IsNullOrEmpty(a.SecondaryWeapon)) { a.ActiveWeapon = a.SecondaryWeapon; return; }
            if (!string.IsNullOrEmpty(a.KnifeWeapon)) { a.ActiveWeapon = a.KnifeWeapon; return; }

            var g = FirstGrenade(a);
            if (!string.IsNullOrEmpty(g)) { a.ActiveWeapon = g; return; }

            if (a.HasBomb) { a.ActiveWeapon = CsWeapons.C4; return; }
            a.ActiveWeapon = null;
        }

        /// <summary>购买成功后的装备落地（价格已由 CsEconomy 扣掉）。</summary>
        public void GivePurchased(CsActor a, CsWeaponDef def, float now)
        {
            if (def == null) return;

            if (def.Class == CsWeaponClass.Grenade)
            {
                var cur = GrenadeCount(a, def.Id);
                a.SetAmmo(def.Id, cur + 1, 0);
                SwitchWeapon(a, def.Id, now);
                return;
            }

            if (def.Class == CsWeaponClass.Equipment)
            {
                if (def.Id == CsWeapons.Vest)
                {
                    a.Armor = CsConst.MaxArmor;
                }
                else if (def.Id == CsWeapons.VestHelm)
                {
                    a.Armor = CsConst.MaxArmor;
                    a.HasHelmet = true;
                }
                else if (def.Id == CsWeapons.Defuser)
                {
                    if (a.Team != CsTeam.CT)
                    {
                        _m.RateWarn("buy.defuser.team", $"非 CT 购买了拆弹器（{a.Name} / {a.Team}）");
                    }
                    a.HasDefuser = true;
                }
                else
                {
                    _m.RateWarn("buy.equipment.unknown", $"未知装备 {def.Id}（{a.Name}）");
                }
                return;
            }

            if (def.Class == CsWeaponClass.Bomb)
            {
                _m.RateWarn("buy.bomb", $"C4 不可购买（{a.Name}）");
                return;
            }

            switch (def.Slot)
            {
                case CsWeaponSlot.Primary:
                    a.PrimaryWeapon = def.Id;
                    a.SetAmmo(def.Id, def.Magazine, def.ReserveAmmo);
                    SwitchWeapon(a, def.Id, now);
                    break;
                case CsWeaponSlot.Secondary:
                    a.SecondaryWeapon = def.Id;
                    a.SetAmmo(def.Id, def.Magazine, def.ReserveAmmo);
                    SwitchWeapon(a, def.Id, now);
                    break;
                case CsWeaponSlot.Knife:
                    a.KnifeWeapon = def.Id;
                    break;
                default:
                    _m.RateWarn("buy.slot.unknown", $"{def.DisplayName} 没有可装备的槽位（{a.Name}）");
                    break;
            }
        }

        public void DropWeapon(CsActor a, string weaponId)
        {
            if (string.IsNullOrEmpty(weaponId)) return;

            // 差异 #75：掉落要把"那把枪当时的余弹"一起带到世上 —— 所以在摘除字典**之前**抓一份。
            var dropMag = 0;
            var dropReserve = 0;
            if (a.Ammo.TryGetValue(weaponId, out var before))
            {
                dropMag = before.inMag;
                dropReserve = before.reserve;
            }

            if (weaponId == a.PrimaryWeapon)
            {
                a.PrimaryWeapon = null;
                a.Ammo.Remove(weaponId);
            }
            else if (weaponId == a.SecondaryWeapon)
            {
                a.SecondaryWeapon = null;
                a.Ammo.Remove(weaponId);
            }
            else if (weaponId == CsWeapons.C4)
            {
                if (!a.HasBomb)
                {
                    _m.RateWarn("drop.c4", $"{a.Name} 并不持有 C4，丢弃被忽略");
                    return;
                }
                _m.Bomb.OnCarrierLost(a);
            }
            else
            {
                _m.RateWarn("drop.notowned." + weaponId, $"{a.Name} 没有武器 {weaponId}，丢弃被忽略");
                return;
            }

            // 差异 #75：真正把"世界中的武器"生成出来。
            // C4 不在这里生成 —— 它的世界掉落走 Bomb 链（CsBomb.OnCarrierLost + TryPickupDropped），
            //    两套并存会变成"同一次掉落能被捡两次"。
            if (weaponId != CsWeapons.C4)
                _m.SpawnDroppedWeapon(weaponId, a.Team, a.Position, a.Yaw, dropMag, dropReserve);

            if (a.ActiveWeapon == weaponId) SelectBestWeapon(a);
        }

        /// <summary>
        /// 差异 #75：把世界上的掉落武器**收回**到角色手上
        /// （原版口径 = 走到 1.2 m 内自动拾取，与 <c>CsBomb.TryPickupDropped</c> 同一条口径）。
        ///
        /// <para><b>槽位规则</b>：主武器（步枪 / 冲锋 / 狙击 / 机枪 / 霰弹）进
        /// <see cref="CsActor.PrimaryWeapon"/>，其余（手枪 / 刀 / 手雷 / 装备）进
        /// <see cref="CsActor.SecondaryWeapon"/>；**对应槽位已被占用 ⇒ 不拾取**
        /// （原版不会把手里那把枪挤掉，是"捡不起来"）。</para>
        /// </summary>
        /// <returns>真的收下了返回 true。</returns>
        public bool PickupDropped(CsActor a, CsDroppedWeapon d)
        {
            if (a == null || d == null || d.Consumed) return false;
            var def = CsWeapons.Get(d.WeaponId);
            if (def == null)
            {
                Game.Logger.Warn("drop.pickup.unknown", $"掉落物武器 id 不认得：{d.WeaponId}，拾取被忽略");
                return false;
            }

            var sidearm = def.Class == CsWeaponClass.Pistol || def.Class == CsWeaponClass.Knife
                          || def.Class == CsWeaponClass.Grenade || def.Class == CsWeaponClass.Equipment;
            if (sidearm)
            {
                if (!string.IsNullOrEmpty(a.SecondaryWeapon)) return false;
                a.SecondaryWeapon = d.WeaponId;
            }
            else
            {
                if (!string.IsNullOrEmpty(a.PrimaryWeapon)) return false;
                a.PrimaryWeapon = d.WeaponId;
            }

            a.SetAmmo(d.WeaponId, d.MagAmmo, d.ReserveAmmo);
            d.Consumed = true;
            if (string.IsNullOrEmpty(a.ActiveWeapon)) SelectBestWeapon(a);

            Game.Logger.Info("drop.pickup", $"{a.Name} 拾起了 {def.DisplayName}（余弹 {d.MagAmmo}+{d.ReserveAmmo}）");
            return true;
        }

        // ==================================================================
        //  手雷计数
        // ==================================================================
        public int GrenadeCount(CsActor a, string grenadeId)
        {
            return a.Ammo.TryGetValue(grenadeId, out var v) ? v.inMag : 0;
        }

        public int TotalGrenades(CsActor a)
        {
            var n = 0;
            _ammoKeys.Clear();
            foreach (var kv in a.Ammo) _ammoKeys.Add(kv.Key);
            for (var i = 0; i < _ammoKeys.Count; i++)
            {
                var def = CsWeapons.Get(_ammoKeys[i]);
                if (def != null && def.Class == CsWeaponClass.Grenade) n += a.Ammo[_ammoKeys[i]].inMag;
            }
            return n;
        }

        public bool CanCarryMoreGrenades(CsActor a, string grenadeId)
        {
            if (TotalGrenades(a) >= CsMatchConst.MaxGrenadesTotal) return false;

            if (grenadeId == CsWeapons.HeGrenade) return GrenadeCount(a, grenadeId) < CsMatchConst.MaxHeGrenades;
            if (grenadeId == CsWeapons.Flashbang) return GrenadeCount(a, grenadeId) < CsMatchConst.MaxFlashbangs;
            if (grenadeId == CsWeapons.SmokeGrenade) return GrenadeCount(a, grenadeId) < CsMatchConst.MaxSmokes;

            _m.RateWarn("grenade.unknown." + grenadeId, $"未知手雷 {grenadeId}，按不可携带处理");
            return false;
        }

        private string FirstGrenade(CsActor a)
        {
            _ammoKeys.Clear();
            foreach (var kv in a.Ammo) _ammoKeys.Add(kv.Key);
            for (var i = 0; i < _ammoKeys.Count; i++)
            {
                var id = _ammoKeys[i];
                var def = CsWeapons.Get(id);
                if (def != null && def.Class == CsWeaponClass.Grenade && a.Ammo[id].inMag > 0) return id;
            }
            return null;
        }

        // ==================================================================
        //  槽位 / 切换
        // ==================================================================
        public void SwitchSlot(CsActor a, int slot, float now)
        {
            string id;
            switch ((CsWeaponSlot)slot)
            {
                case CsWeaponSlot.Primary: id = a.PrimaryWeapon; break;
                case CsWeaponSlot.Secondary: id = a.SecondaryWeapon; break;
                case CsWeaponSlot.Knife: id = a.KnifeWeapon; break;
                case CsWeaponSlot.Grenade: id = FirstGrenade(a); break;
                case CsWeaponSlot.Bomb: id = a.HasBomb ? CsWeapons.C4 : null; break;
                default:
                    _m.RateWarn("slot.invalid." + slot, $"{a.Name} 切换槽位 {slot}：非法槽位（只支持 1-{ (int)CsWeaponSlot.Bomb }）");
                    return;
            }

            if (string.IsNullOrEmpty(id))
            {
                _m.RateInfo("slot.empty." + slot, $"{a.Name} 的槽位 {slot} 是空的");
                return;
            }

            SwitchWeapon(a, id, now);
        }

        public void SwitchWeapon(CsActor a, string weaponId, float now)
        {
            if (string.IsNullOrEmpty(weaponId)) return;
            if (a.ActiveWeapon == weaponId) return;

            if (!Owns(a, weaponId))
            {
                _m.RateWarn("switch.notowned." + weaponId, $"{a.Name} 没有武器 {weaponId}，切换被忽略");
                return;
            }

            var def = CsWeapons.Get(weaponId);
            if (def == null)
            {
                _m.RateWarn("switch.nodef." + weaponId, $"{a.Name} 切换武器 {weaponId}：武器表里没有它");
                return;
            }

            a.ActiveWeapon = weaponId;
            a.SwitchEndTime = now + SwitchTimeFor(def);
            a.ReloadEndTime = 0f;
            a.ConsecutiveShots = 0;
            a.RecoilPitch = 0f;
            a.RecoilYaw = 0f;

            Game.Logger.Info(Tag, $"{a.Name} 切换到 {def.DisplayName}（{SwitchTimeFor(def):F2}s 后可用）");
        }

        private bool Owns(CsActor a, string id)
        {
            if (id == a.PrimaryWeapon) return true;
            if (id == a.SecondaryWeapon) return true;
            if (id == a.KnifeWeapon) return true;
            if (id == CsWeapons.C4) return a.HasBomb;

            var def = CsWeapons.Get(id);
            if (def != null && def.Class == CsWeaponClass.Grenade) return GrenadeCount(a, id) > 0;
            return false;
        }

        // ==================================================================
        //  换弹
        // ==================================================================
        public void Reload(CsActor a, float now)
        {
            var def = a.ActiveDef;
            if (def == null)
            {
                _m.RateWarn("reload.noweapon", $"{a.Name} 没有手持武器，无法换弹");
                return;
            }
            if (def.Magazine <= 0)
            {
                _m.RateWarn("reload.nomag." + def.Id, $"{a.Name} 的 {def.DisplayName} 不支持换弹");
                return;
            }
            if (a.ReloadEndTime > 0f) return;
            if (now < a.SwitchEndTime)
            {
                _m.RateInfo("reload.switching", $"{a.Name} 正在切枪，换弹请求被忽略");
                return;
            }

            var ammo = a.GetAmmo(def.Id);
            if (ammo.inMag >= def.Magazine)
            {
                _m.RateInfo("reload.full." + def.Id, $"{a.Name} 的 {def.DisplayName} 弹匣已满，无需换弹");
                return;
            }
            if (ammo.reserve <= 0)
            {
                Game.Logger.Warn(Tag, $"{a.Name} 换弹失败：{def.DisplayName} 备弹为 0");
                return;
            }

            a.ReloadEndTime = now + def.ReloadTime;
            // 差异 #72：单调序号 = 表现层判"换弹开始"的唯一信号。ReloadEndTime 会被 ① 结算归零
            // ② 切枪归零 ③ 同帧跨完 而漏掉边沿；序号不会被这三种情况吃掉。
            a.ReloadSeq++;
            a.ConsecutiveShots = 0;
            a.RecoilPitch = 0f;
            a.RecoilYaw = 0f;
            // 唯一必须先确定的事实是"序号到底有没有推进"（表现层只认序号边沿）。
            // 不改任何判定，只是把已经存在的字段写进日志。
            Game.Logger.Info(Tag, $"{a.Name} 开始换弹 {def.DisplayName}（{def.ReloadTime:F2}s）seq={a.ReloadSeq}");
        }

        /// <summary>
        /// 差异 #68：attack2（右键按下沿）→ 切换**消音器**（USP·M4A1）或**连发模式**（Glock18·FAMAS）。
        ///
        /// <para>只对 <c>CsWeaponDef.CanSilence</c> / <c>CanBurst</c> 为真的武器有动作；其余一律
        /// **不动状态**、只记一条 rate-limited 日志（无出处就不许"顺手给点什么效果"）。</para>
        ///
        /// <para>切枪期间按右键**照样切**（与 <see cref="Reload"/> 不同）：原版的消音器拆装不受
        /// 切枪影响，硬拦反而会造出一个原版没有的规则。</para>
        ///
        /// <para>本方法只改「状态」。**数值影响未落地**（消音后的伤害/散布、连发的发数与节奏都没有
        /// 出处）⇒ 缺口留在 <c>策划/差异登记.tsv</c> #68，判据 = 离线断言（状态切换可观测）。</para>
        /// </summary>
        public void ToggleWeaponMode(CsActor a, float now)
        {
            if (a == null || !a.IsAlive) return;
            var def = a.ActiveDef;
            if (def == null)
            {
                _m.RateInfo("attack2.noweapon", $"{a.Name} 手里没有武器，右键无动作（差异 #68）");
                return;
            }

            if (def.CanSilence)
            {
                a.Silenced = !a.Silenced;
                Game.Logger.Info(Tag,
                    $"{a.Name} {(a.Silenced ? "装上" : "拆下")} {def.DisplayName} 的消音器（attack2 · 差异 #68）");
                return;
            }

            if (def.CanBurst)
            {
                a.BurstMode = !a.BurstMode;
                Game.Logger.Info(Tag,
                    $"{a.Name} 把 {def.DisplayName} 切到{(a.BurstMode ? "连发" : "单发")}（attack2 · 差异 #68）");
                return;
            }

            _m.RateInfo("attack2.noeffect." + def.Id,
                $"{a.Name} 的 {def.DisplayName} 右键无动作：原版 attack2 的逐武器分支里没有它（差异 #68；⛔ 无出处不加效果）");
        }

        internal void CompleteReload(CsActor a)
        {
            var def = a.ActiveDef;
            if (def == null) return;

            var ammo = a.GetAmmo(def.Id);
            if (ammo.inMag >= def.Magazine) return;

            var need = def.Magazine - ammo.inMag;
            var take = Mathf.Min(need, ammo.reserve);
            if (take <= 0)
            {
                _m.RateWarn("reload.empty." + def.Id, $"{a.Name} 换弹结算时备弹为 0（{def.DisplayName}）");
                return;
            }

            a.SetAmmo(def.Id, ammo.inMag + take, ammo.reserve - take);
        }

        // ==================================================================
        //  开火
        // ==================================================================
        /// <summary>
        /// 扣弹药 / 限射速 / 累后坐力 / 记录"本帧有射击"。
        ///
        /// <para><paramref name="localPlayer"/>=true 时**只做结算、不做枪械射线** ——
        /// 真人玩家的射线由 Module/Combat 负责（它拿到 <see cref="ICsMatch.ConsumeShotFired"/> 后自行射线并回调
        /// <see cref="ICsMatch.ReportHit"/>）。刀与手雷两种近身/投掷动作仍由本类直接结算。</para>
        /// </summary>
        public bool TryDischarge(CsActor a, float now, out string weaponId, bool localPlayer)
        {
            weaponId = null;
            if (a == null || !a.IsAlive) return false;

            var def = a.ActiveDef;
            if (def == null)
            {
                _m.RateWarn("fire.noweapon", $"{a.Name} 没有手持武器，无法开火");
                return false;
            }

            if (now < a.ReloadEndTime)
            {
                _m.RateInfo("fire.reloading", $"{a.Name} 换弹中，无法开火");
                return false;
            }
            if (now < a.SwitchEndTime)
            {
                _m.RateInfo("fire.switching", $"{a.Name} 切枪中，无法开火");
                return false;
            }
            if (now < a.NextFireTime) return false;   // 射速限制：高频，静默

            if (def.Class == CsWeaponClass.Grenade)
            {
                if (GrenadeCount(a, def.Id) <= 0)
                {
                    _m.RateWarn("fire.nogrenade." + def.Id, $"{a.Name} 手上没有 {def.DisplayName} 了");
                    return false;
                }
                a.NextFireTime = now + CsConst.SwitchTimePistol;
                ThrowGrenade(a, def, now);
                weaponId = def.Id;
                return true;
            }

            if (def.Class == CsWeaponClass.Bomb)
            {
                _m.RateWarn("fire.bomb", $"{a.Name} 拿着 C4，无法开火（按 E 下包）");
                return false;
            }

            // 弹药
            if (def.Magazine > 0)
            {
                var ammo = a.GetAmmo(def.Id);
                if (ammo.inMag <= 0)
                {
                    _m.RateInfo("fire.empty." + def.Id, $"{a.Name} 的 {def.DisplayName} 没子弹了（按 R 换弹）");
                    return false;
                }
                a.SetAmmo(def.Id, ammo.inMag - 1, ammo.reserve);
            }

            a.NextFireTime = now + def.SecondsPerShot;
            a.ConsecutiveShots++;
            a.RecoilPitch += def.RecoilVert;
            // 后坐力水平偏移：**玩法**（逐发累积进 a.RecoilYaw，决定后续弹道往哪偏）。
            // 独立一路 RecoilYaw —— 不与散布流共用：散布每发抽 2 次（yaw/pitch），
            // 后坐力每发抽 1 次，混在一起会让"改散布"悄悄改掉后坐力序列。
            a.RecoilYaw += CsRng.Stream(CsRngStream.RecoilYaw).Range(-def.RecoilHoriz, def.RecoilHoriz);

            weaponId = def.Id;
            _m.RecordShotFired(def.Id);

            if (def.Class == CsWeaponClass.Knife) FireKnife(a, def);
            else if (!localPlayer) FireGun(a, def);

            return true;
        }

        private void FireGun(CsActor a, CsWeaponDef def)
        {
            var origin = a.EyePosition;
            var baseDir = AimDirection(a);

            var spread = def.Spread;
            if (a.Velocity.x != 0f || a.Velocity.z != 0f) spread += def.MoveSpread;
            if (!a.OnGround) spread += def.MoveSpread;

            var range = def.Range > 0f ? def.Range : CsConst.BotVisionRange;
            var pellets = def.Pellets > 0 ? def.Pellets : 1;

            for (var p = 0; p < pellets; p++)
            {
                var dir = ApplySpread(baseDir, spread);
                var victim = RaycastActor(a, origin, dir, range, out var hitbox, out var point, out var dist);
                if (victim == null) continue;
                _m.Damage.ApplyHit(a, victim, def, hitbox, point, dist, false);
            }
        }

        private void FireKnife(CsActor a, CsWeaponDef def)
        {
            var victim = RaycastActor(a, a.EyePosition, AimDirection(a), CsConst.KnifeRange,
                out var hitbox, out var point, out var dist);
            if (victim == null) return;
            _m.Damage.ApplyHit(a, victim, def, hitbox, point, dist, false);
        }

        /// <summary>从眼睛沿方向射线，返回"最近的一个不是射手自己的 actor"（穿墙不穿透，简化）。</summary>
        private CsActor RaycastActor(CsActor shooter, Vector3 origin, Vector3 dir, float range,
            out CsHitbox hitbox, out Vector3 point, out float distance)
        {
            hitbox = CsHitbox.Generic;
            point = origin;
            distance = 0f;

            var count = Physics.RaycastNonAlloc(origin, dir, _hits, range, ~0, QueryTriggerInteraction.Ignore);
            if (count <= 0) return null;

            var best = -1;
            var bestDist = float.MaxValue;
            for (var i = 0; i < count; i++)
            {
                var h = _hits[i];
                var proxy = h.collider != null ? h.collider.GetComponentInParent<CsHitboxProxy>() : null;
                if (proxy != null && proxy.ActorId == shooter.Id) continue;   // 打到自己
                if (h.distance < bestDist)
                {
                    bestDist = h.distance;
                    best = i;
                }
            }
            if (best < 0) return null;

            var hit = _hits[best];
            point = hit.point;
            distance = hit.distance;

            var p2 = hit.collider != null ? hit.collider.GetComponentInParent<CsHitboxProxy>() : null;
            if (p2 == null) return null;    // 打在墙上

            var victim = _m.Find(p2.ActorId);
            if (victim == null)
            {
                _m.RateWarn("ray.actor.missing", $"射线命中的受体 ActorId={p2.ActorId} 找不到对应 actor");
                return null;
            }
            if (!victim.IsAlive) return null;

            hitbox = p2.Hitbox;
            return victim;
        }

        internal static Vector3 AimDirection(CsActor a)
        {
            var yawRad = a.Yaw * Mathf.Deg2Rad;
            var pitchRad = a.Pitch * Mathf.Deg2Rad;
            return new Vector3(
                Mathf.Cos(pitchRad) * Mathf.Sin(yawRad),
                Mathf.Sin(pitchRad),
                Mathf.Cos(pitchRad) * Mathf.Cos(yawRad));
        }

        private static Vector3 ApplySpread(Vector3 dir, float spreadDegrees)
        {
            if (spreadDegrees <= 0f) return dir;
            // 散布：**玩法**。与 CsMatch.ApplySpread / Firearm.ApplySpread 共用全项目唯一的
            // WeaponSpread 流（三处是同一套欧拉角扰动，同一口径只允许一个流）。
            var rng = CsRng.Stream(CsRngStream.WeaponSpread);
            var rot = Quaternion.Euler(
                rng.Range(-spreadDegrees, spreadDegrees),
                rng.Range(-spreadDegrees, spreadDegrees),
                0f);
            return (rot * dir).normalized;
        }

        // ==================================================================
        //  手雷投掷 / 抛射物 / 烟雾
        // ==================================================================
        private void ThrowGrenade(CsActor a, CsWeaponDef def, float now)
        {
            var count = GrenadeCount(a, def.Id);
            if (count <= 0) return;

            if (count - 1 <= 0) a.Ammo.Remove(def.Id);
            else a.SetAmmo(def.Id, count - 1, 0);

            var dir = AimDirection(a);
            var proj = new CsGrenadeProjectile
            {
                WeaponId = def.Id,
                OwnerId = a.Id,
                OwnerTeam = a.Team,
                Position = a.EyePosition + dir * CsConst.PlayerRadius,
                Velocity = dir * CsConst.GrenadeThrowForce,
                FuseLeft = CsConst.GrenadeFuse,
                Landed = false,
            };
            _grenades.Add(proj);

            Game.Logger.Info(Tag, $"{a.Name} 投出 {def.DisplayName}（剩余 {Mathf.Max(0, count - 1)} 颗）");

            // 手上这颗没了就自动换回武器（官方行为）。
            if (GrenadeCount(a, def.Id) <= 0) SelectBestWeapon(a);
        }

        public void TickProjectiles(float dt, float now)
        {
            for (var i = _grenades.Count - 1; i >= 0; i--)
            {
                var g = _grenades[i];

                if (!g.Landed)
                {
                    g.Velocity.y -= CsConst.Gravity * dt;
                    var to = g.Position + g.Velocity * dt;

                    var map = _m.Map;
                    var grounded = false;

                    if (map != null && map.IsLoaded)
                    {
                        var groundY = map.SampleGround(to);
                        if (!float.IsNegativeInfinity(groundY) && to.y <= groundY && g.Velocity.y <= 0f)
                        {
                            to.y = groundY;
                            grounded = true;
                        }
                    }

                    if (!grounded &&
                        Physics.Linecast(g.Position, to, out var hit, ~0, QueryTriggerInteraction.Ignore))
                    {
                        var proxy = hit.collider != null ? hit.collider.GetComponentInParent<CsHitboxProxy>() : null;
                        if (proxy == null)
                        {
                            to = hit.point;
                            grounded = true;
                        }
                    }

                    if (grounded)
                    {
                        g.Landed = true;
                        g.Velocity = Vector3.zero;
                    }
                    g.Position = to;
                }

                g.FuseLeft -= dt;
                if (g.FuseLeft <= 0f)
                {
                    Detonate(g, now);
                    _grenades.RemoveAt(i);
                }
            }

            for (var i = _smokes.Count - 1; i >= 0; i--)
            {
                _smokes[i].TimeLeft -= dt;
                if (_smokes[i].TimeLeft <= 0f) _smokes.RemoveAt(i);
            }
        }

        private void Detonate(CsGrenadeProjectile g, float now)
        {
            var def = CsWeapons.Get(g.WeaponId);
            var owner = _m.Find(g.OwnerId);

            if (def == null)
            {
                _m.RateWarn("nade.nodef." + g.WeaponId, $"手雷爆炸时找不到武器定义 {g.WeaponId}");
                return;
            }

            if (def.Class != CsWeaponClass.Grenade)
            {
                _m.RateWarn("nade.notgrenade." + def.Id, $"{def.DisplayName} 不是手雷却走了爆炸流程");
                return;
            }

            switch (def.Id)
            {
                case CsWeapons.HeGrenade:
                    _m.Damage.ApplyHeExplosion(g.Position, owner, g.OwnerTeam);
                    break;
                case CsWeapons.Flashbang:
                    _m.Damage.ApplyFlash(g.Position, owner, g.OwnerTeam);
                    break;
                case CsWeapons.SmokeGrenade:
                    _smokes.Add(new CsSmokeVolume
                    {
                        Position = g.Position,
                        TimeLeft = CsConst.GrenadeSmokeDuration,
                    });
                    Game.Logger.Info(Tag, $"烟雾弹起雾于 {g.Position}（持续 {CsConst.GrenadeSmokeDuration:F1}s）");
                    break;
                default:
                    _m.RateWarn("nade.unknown." + def.Id, $"未知手雷类型 {def.Id}，无爆炸效果");
                    break;
            }
        }

        /// <summary>视线是否穿过任一烟雾球（线段-球体相交）。</summary>
        public bool IsBlockedBySmoke(Vector3 from, Vector3 to)
        {
            if (_smokes.Count == 0) return false;

            var d = to - from;
            var len = d.magnitude;
            if (len <= Mathf.Epsilon) return false;
            var dir = d / len;
            var r2 = CsMatchConst.SmokeBlockRadius * CsMatchConst.SmokeBlockRadius;

            for (var i = 0; i < _smokes.Count; i++)
            {
                var oc = _smokes[i].Position - from;
                var t = Mathf.Clamp(Vector3.Dot(oc, dir), 0f, len);
                var closest = from + dir * t;
                if ((closest - _smokes[i].Position).sqrMagnitude <= r2) return true;
            }
            return false;
        }
    }
}
