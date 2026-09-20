using System.Collections.Generic;

namespace Cs16.Core
{
    /// <summary>
    /// 武器定义（数值真源）。只读数据，运行时不得修改；
    /// 查询一律走 <see cref="CsWeapons.Get"/> / <see cref="CsWeapons.ByClass"/>。
    ///
    /// <para><b>数值出处（逐条核对过 · agent-11）</b>：<see cref="Price"/> / <see cref="Magazine"/> /
    /// <see cref="ReserveAmmo"/> 三项全部来自原版 <c>mp.dll</c> 的 <c>WeaponInfo[]</c> ——
    /// 数组基址 <c>mp.dll:0x10f708</c>、步长 <c>0x20</c>，字段偏移 <c>iCost=+0x04</c>、
    /// <c>iMaxClip=+0x10</c>、<c>iMaxAmmo=+0x14</c>、<c>pszName=+0x1c</c>
    /// （字段语义的铁证见 <c>原版资源/解包产物/原版数值表.md</c> §0.3/§3）。</para>
    ///
    /// <para>⛔ <b><see cref="CsWeaponDef.Damage"/> / <see cref="CsWeaponDef.ArmorPenetration"/> /
    /// <see cref="CsWeaponDef.Rpm"/> / <see cref="CsWeaponDef.ReloadTime"/> / <see cref="CsWeaponDef.Range"/>
    /// / <see cref="CsWeaponDef.Spread"/> / <see cref="CsWeaponDef.RecoilVert"/> /
    /// <see cref="CsWeaponDef.KillReward"/> 这几组仍无原版出处</b>（原版把它们写死在每把武器类的
    /// <c>PrimaryAttack</c> 浮点立即数里，不是数据表）⇒ 按"未验证"处理，见
    /// <c>策划/对照表.md</c> BLOCKED-2。</para>
    /// </summary>
    public sealed class CsWeaponDef
    {
        public string Id;
        public string DisplayName;
        public CsWeaponClass Class;
        public CsWeaponSlot Slot;          // (CsWeaponSlot)0 = 无槽位（装备）
        public int Price;
        public int Damage;                 // 基础伤害（近距离、无甲）
        public float ArmorPenetration;     // 0~1，越高越无视护甲
        public int Rpm;                    // 每分钟射速（0 = 不可连发）
        public int Magazine;               // 弹匣容量
        public int ReserveAmmo;            // 备弹
        public float ReloadTime;           // 换弹秒数
        public float Spread;               // 静止基础散布（度）
        public float MoveSpread;           // 移动附加散布（度）
        public float RecoilVert;           // 每发垂直后坐力（度）
        public float RecoilHoriz;          // 每发水平后坐力（度）
        public int Pellets;                // 单发弹丸数（霰弹 > 1）
        public float Range;                // 有效射程（米）
        public float FalloffPerMeter;      // 每米伤害衰减系数
        public int KillReward;             // 用本武器击杀的奖励
        public bool AutoFire;              // 全自动
        public CsTeamLimit TeamLimit;
        public string SoundFire = "fire";
        public string SoundReload = "reload";

        public bool HasSlot => (int)Slot > 0;
        public float SecondsPerShot => Rpm > 0 ? 60f / Rpm : 0f;
    }

    /// <summary>全部武器表（手工维护；两端一致由本文件唯一持有）。</summary>
    public static class CsWeapons
    {
        public const string Knife = "knife";
        public const string Glock18 = "glock18";
        public const string Usp = "usp";
        public const string P228 = "p228";
        public const string Deagle = "deagle";
        public const string FiveSeven = "fiveseven";
        public const string Elite = "elite";
        public const string Mp5 = "mp5";
        public const string Tmp = "tmp";
        public const string Mac10 = "mac10";
        public const string Ump45 = "ump45";
        public const string P90 = "p90";
        public const string Galil = "galil";
        public const string Famas = "famas";
        public const string Ak47 = "ak47";
        public const string M4A1 = "m4a1";
        public const string Sg552 = "sg552";
        public const string Aug = "aug";
        public const string Scout = "scout";
        public const string Awp = "awp";
        public const string G3sg1 = "g3sg1";
        public const string Sg550 = "sg550";
        public const string M3 = "m3";
        public const string Xm1014 = "xm1014";
        public const string M249 = "m249";
        public const string HeGrenade = "hegrenade";
        public const string Flashbang = "flashbang";
        public const string SmokeGrenade = "smokegrenade";
        public const string Vest = "vest";
        public const string VestHelm = "vesthelm";
        public const string Defuser = "defuser";
        public const string C4 = "c4";

        // W(...) 的行尾注释 = 该武器在 mp.dll `WeaponInfo[]` 里的条目偏移与三项原版值
        // （格式：`mp.dll:偏移 iCost / iMaxClip / iMaxAmmo`，均照 mp.dll 逐条读出，读法见类注释）。
        // 与 `策划/对照表.md` §5.3 的 W-01~W-24 逐行对齐；本片（agent-11）核对结果 = 只有 Elite 价格不一致（已改）。
        private static readonly List<CsWeaponDef> _all = new List<CsWeaponDef>
        {
            // 刀 / 手雷 / 装备 / C4 不在 `WeaponInfo[]` 里（该表只含 24 把枪 + 战术盾 idx25）：
            // 它们的价格出处 = resource/ui/buy*.res 的 `cost` 字段（见对照表 §5.1 P-25~P-32）。
            W(Knife, "Knife", CsWeaponClass.Knife, CsWeaponSlot.Knife, 0, 55, 0.50f, 200, 0, 0, 0f, 0f, 0f, 0f, 0f, 1, 1.6f, 0f, 1500, false, CsTeamLimit.Any),

            W(Glock18, "Glock 18", CsWeaponClass.Pistol, CsWeaponSlot.Secondary, 400, 25, 0.47f, 400, 20, 120, 2.2f, 0.60f, 1.50f, 1.00f, 0.40f, 1, 30f, 0.02f, 300, false, CsTeamLimit.TerroristOnly), // mp.dll:0x10f728 400 / 20 / 120（W-02）
            W(Usp, "USP .45", CsWeaponClass.Pistol, CsWeaponSlot.Secondary, 500, 34, 0.50f, 352, 12, 100, 2.2f, 0.50f, 1.40f, 1.10f, 0.40f, 1, 30f, 0.02f, 300, false, CsTeamLimit.CounterTerroristOnly), // mp.dll:0x10f8a8 500 / 12 / 100（W-13）
            W(P228, "P228", CsWeaponClass.Pistol, CsWeaponSlot.Secondary, 600, 32, 0.50f, 400, 13, 52, 2.2f, 0.50f, 1.40f, 1.10f, 0.40f, 1, 30f, 0.02f, 300, false, CsTeamLimit.Any), // mp.dll:0x10f708 600 / 13 / 52（W-01）
            W(Deagle, "Desert Eagle", CsWeaponClass.Pistol, CsWeaponSlot.Secondary, 650, 54, 0.62f, 267, 7, 35, 2.4f, 0.60f, 2.00f, 1.80f, 0.60f, 1, 40f, 0.02f, 300, false, CsTeamLimit.Any), // mp.dll:0x10f9a8 650 / 7 / 35（W-21）
            W(FiveSeven, "Five-SeveN", CsWeaponClass.Pistol, CsWeaponSlot.Secondary, 750, 20, 0.60f, 400, 20, 100, 2.2f, 0.50f, 1.40f, 1.00f, 0.40f, 1, 30f, 0.02f, 300, false, CsTeamLimit.CounterTerroristOnly), // mp.dll:0x10f808 750 / 20 / 100（W-08）
            // ★ 本片唯一改动的武器数值：1000 → 800（出处 mp.dll:0x10f7e8 iCost=800，逐字节重读见回报；buy*.res 同值）
            W(Elite, "Dual Berettas", CsWeaponClass.Pistol, CsWeaponSlot.Secondary, 800, 38, 0.47f, 400, 30, 120, 2.4f, 0.60f, 1.60f, 1.20f, 0.50f, 1, 30f, 0.02f, 300, false, CsTeamLimit.Any), // mp.dll:0x10f7e8 800 / 30 / 120（W-07）

            W(Mp5, "MP5 Navy", CsWeaponClass.SMG, CsWeaponSlot.Primary, 1500, 26, 0.52f, 750, 30, 120, 2.6f, 0.50f, 1.20f, 0.90f, 0.40f, 1, 40f, 0.015f, 600, false, CsTeamLimit.Any), // mp.dll:0x10f8e8 1500 / 30 / 120（W-15）
            W(Tmp, "TMP", CsWeaponClass.SMG, CsWeaponSlot.Primary, 1250, 20, 0.50f, 857, 30, 120, 2.4f, 0.60f, 1.30f, 0.80f, 0.40f, 1, 40f, 0.015f, 600, false, CsTeamLimit.CounterTerroristOnly), // mp.dll:0x10f968 1250 / 30 / 120（W-19）
            W(Mac10, "MAC-10", CsWeaponClass.SMG, CsWeaponSlot.Primary, 1400, 29, 0.48f, 857, 30, 100, 2.6f, 0.60f, 1.40f, 1.00f, 0.50f, 1, 35f, 0.015f, 600, false, CsTeamLimit.TerroristOnly), // mp.dll:0x10f7a8 1400 / 30 / 100（W-05）
            W(Ump45, "UMP-45", CsWeaponClass.SMG, CsWeaponSlot.Primary, 1700, 30, 0.50f, 571, 25, 100, 2.8f, 0.50f, 1.20f, 0.90f, 0.40f, 1, 40f, 0.015f, 600, false, CsTeamLimit.Any), // mp.dll:0x10f828 1700 / 25 / 100（W-09）
            W(P90, "P90", CsWeaponClass.SMG, CsWeaponSlot.Primary, 2350, 21, 0.53f, 857, 50, 100, 3.3f, 0.50f, 1.20f, 0.90f, 0.40f, 1, 40f, 0.015f, 600, true, CsTeamLimit.Any), // mp.dll:0x10fa08 2350 / 50 / 100（W-24）

            W(Galil, "Galil", CsWeaponClass.Rifle, CsWeaponSlot.Primary, 2000, 30, 0.70f, 666, 35, 90, 3.0f, 0.50f, 1.60f, 1.30f, 0.50f, 1, 45f, 0.01f, 300, true, CsTeamLimit.TerroristOnly), // mp.dll:0x10f868 2000 / 35 / 90（W-11）
            W(Famas, "FAMAS", CsWeaponClass.Rifle, CsWeaponSlot.Primary, 2250, 30, 0.70f, 666, 25, 90, 3.0f, 0.50f, 1.60f, 1.30f, 0.50f, 1, 45f, 0.01f, 300, true, CsTeamLimit.CounterTerroristOnly), // mp.dll:0x10f888 2250 / 25 / 90（W-12）
            W(Ak47, "AK-47", CsWeaponClass.Rifle, CsWeaponSlot.Primary, 2500, 36, 0.775f, 600, 30, 90, 3.0f, 0.45f, 2.00f, 1.90f, 0.70f, 1, 50f, 0.01f, 300, true, CsTeamLimit.TerroristOnly), // mp.dll:0x10f9e8 2500 / 30 / 90（W-23）
            W(M4A1, "M4A1", CsWeaponClass.Rifle, CsWeaponSlot.Primary, 3100, 33, 0.70f, 666, 30, 90, 3.0f, 0.40f, 1.80f, 1.50f, 0.60f, 1, 50f, 0.01f, 300, true, CsTeamLimit.CounterTerroristOnly), // mp.dll:0x10f948 3100 / 30 / 90（W-18）
            W(Sg552, "SG-552", CsWeaponClass.Rifle, CsWeaponSlot.Primary, 3500, 33, 0.70f, 666, 30, 90, 3.0f, 0.50f, 1.80f, 1.60f, 0.60f, 1, 45f, 0.01f, 300, true, CsTeamLimit.TerroristOnly), // mp.dll:0x10f9c8 3500 / 30 / 90（W-22）
            W(Aug, "AUG", CsWeaponClass.Rifle, CsWeaponSlot.Primary, 3500, 32, 0.70f, 666, 30, 90, 3.0f, 0.50f, 1.80f, 1.60f, 0.60f, 1, 45f, 0.01f, 300, true, CsTeamLimit.CounterTerroristOnly), // mp.dll:0x10f7c8 3500 / 30 / 90（W-06）

            W(Scout, "Scout", CsWeaponClass.Sniper, CsWeaponSlot.Primary, 2750, 75, 0.85f, 90, 10, 90, 3.0f, 0.20f, 3.00f, 3.00f, 1.00f, 1, 80f, 0.005f, 300, false, CsTeamLimit.Any), // mp.dll:0x10f768 2750 / 10 / 90（W-03）
            W(Awp, "AWP", CsWeaponClass.Sniper, CsWeaponSlot.Primary, 4750, 115, 0.975f, 41, 10, 30, 3.6f, 0.10f, 4.00f, 4.00f, 1.20f, 1, 100f, 0.005f, 100, false, CsTeamLimit.Any), // mp.dll:0x10f8c8 4750 / 10 / 30（W-14）
            W(G3sg1, "G3SG1", CsWeaponClass.Sniper, CsWeaponSlot.Primary, 5000, 80, 0.98f, 240, 20, 90, 4.0f, 0.30f, 3.00f, 2.50f, 1.00f, 1, 90f, 0.005f, 300, false, CsTeamLimit.TerroristOnly), // mp.dll:0x10f988 5000 / 20 / 90（W-20）
            W(Sg550, "SG-550", CsWeaponClass.Sniper, CsWeaponSlot.Primary, 4200, 70, 0.98f, 240, 30, 90, 4.0f, 0.30f, 3.00f, 2.50f, 1.00f, 1, 90f, 0.005f, 300, false, CsTeamLimit.CounterTerroristOnly), // mp.dll:0x10f848 4200 / 30 / 90（W-10）

            W(M3, "M3 Super 90", CsWeaponClass.Shotgun, CsWeaponSlot.Primary, 1700, 22, 0.50f, 85, 8, 32, 3.0f, 3.50f, 1.00f, 3.50f, 1.00f, 9, 15f, 0.05f, 900, false, CsTeamLimit.Any), // mp.dll:0x10f928 1700 / 8 / 32（W-17）
            W(Xm1014, "XM1014", CsWeaponClass.Shotgun, CsWeaponSlot.Primary, 3000, 20, 0.50f, 171, 7, 32, 4.0f, 3.50f, 1.00f, 3.00f, 1.00f, 6, 15f, 0.05f, 900, false, CsTeamLimit.Any), // mp.dll:0x10f788 3000 / 7 / 32（W-04）

            W(M249, "M249", CsWeaponClass.MachineGun, CsWeaponSlot.Primary, 5750, 32, 0.80f, 750, 100, 200, 5.7f, 0.60f, 2.20f, 1.60f, 0.70f, 1, 45f, 0.01f, 300, true, CsTeamLimit.Any), // mp.dll:0x10f908 5750 / 100 / 200（W-16）

            W(HeGrenade, "HE Grenade", CsWeaponClass.Grenade, CsWeaponSlot.Grenade, 300, 98, 0.50f, 0, 1, 0, 0f, 0f, 0f, 0f, 0f, 1, 5.5f, 0f, 300, false, CsTeamLimit.Any),
            W(Flashbang, "Flashbang", CsWeaponClass.Grenade, CsWeaponSlot.Grenade, 200, 0, 0f, 0, 2, 0, 0f, 0f, 0f, 0f, 0f, 1, 0f, 0f, 300, false, CsTeamLimit.Any),
            W(SmokeGrenade, "Smoke Grenade", CsWeaponClass.Grenade, CsWeaponSlot.Grenade, 300, 0, 0f, 0, 1, 0, 0f, 0f, 0f, 0f, 0f, 1, 0f, 0f, 300, false, CsTeamLimit.Any),

            W(Vest, "Kevlar Vest", CsWeaponClass.Equipment, (CsWeaponSlot)0, 650, 0, 0f, 0, 0, 0, 0f, 0f, 0f, 0f, 0f, 0, 0f, 0f, 0, false, CsTeamLimit.Any),
            W(VestHelm, "Kevlar + Helmet", CsWeaponClass.Equipment, (CsWeaponSlot)0, 1000, 0, 0f, 0, 0, 0, 0f, 0f, 0f, 0f, 0f, 0, 0f, 0f, 0, false, CsTeamLimit.Any),
            W(Defuser, "Defusal Kit", CsWeaponClass.Equipment, (CsWeaponSlot)0, 200, 0, 0f, 0, 0, 0, 0f, 0f, 0f, 0f, 0f, 0, 0f, 0f, 0, false, CsTeamLimit.CounterTerroristOnly),

            W(C4, "C4 Explosive", CsWeaponClass.Bomb, CsWeaponSlot.Bomb, 0, 0, 0f, 0, 0, 0, 0f, 0f, 0f, 0f, 0f, 0, 0f, 0f, 0, false, CsTeamLimit.TerroristOnly),
        };

        private static readonly Dictionary<string, CsWeaponDef> _byId = new Dictionary<string, CsWeaponDef>();

        static CsWeapons()
        {
            foreach (var w in _all) _byId[w.Id] = w;
        }

        public static IReadOnlyList<CsWeaponDef> All => _all;

        /// <summary>按 id 查询；不存在返回 null（调用方必须判空并打日志）。</summary>
        public static CsWeaponDef Get(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            return _byId.TryGetValue(id, out var def) ? def : null;
        }

        /// <summary>按大类列出该阵营可购买的武器（买枪菜单用）。</summary>
        public static List<CsWeaponDef> BuyableByClass(CsWeaponClass cls, CsTeam team)
        {
            var list = new List<CsWeaponDef>();
            foreach (var w in _all)
            {
                if (w.Class != cls) continue;
                if (w.Price <= 0) continue;
                if (w.TeamLimit == CsTeamLimit.TerroristOnly && team != CsTeam.T) continue;
                if (w.TeamLimit == CsTeamLimit.CounterTerroristOnly && team != CsTeam.CT) continue;
                list.Add(w);
            }
            return list;
        }

        /// <summary>取阵营的默认手枪 id（出生装备）。</summary>
        public static string DefaultPistol(CsTeam team) => team == CsTeam.T ? Glock18 : Usp;

        private static CsWeaponDef W(string id, string name, CsWeaponClass cls, CsWeaponSlot slot,
            int price, int dmg, float armorPen, int rpm, int mag, int reserve, float reload,
            float spread, float moveSpread, float recoilV, float recoilH, int pellets,
            float range, float falloff, int killReward, bool auto, CsTeamLimit limit)
        {
            return new CsWeaponDef
            {
                Id = id,
                DisplayName = name,
                Class = cls,
                Slot = slot,
                Price = price,
                Damage = dmg,
                ArmorPenetration = armorPen,
                Rpm = rpm,
                Magazine = mag,
                ReserveAmmo = reserve,
                ReloadTime = reload,
                Spread = spread,
                MoveSpread = moveSpread,
                RecoilVert = recoilV,
                RecoilHoriz = recoilH,
                Pellets = pellets,
                Range = range,
                FalloffPerMeter = falloff,
                KillReward = killReward,
                AutoFire = auto,
                TeamLimit = limit,
                SoundFire = "sfx/" + id + "_fire",
                SoundReload = "sfx/" + id + "_reload",
            };
        }
    }
}
