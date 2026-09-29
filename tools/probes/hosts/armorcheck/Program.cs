// =====================================================================================
//  Program.cs —— armorcheck 宿主的驱动：跑**真** `Core/CsWeapons.cs` 的护甲结算入口
//  （`CsWeapons.ArmorAbsorb` + `CsWeaponDef.ArmorRatioScale`）。
//
//  两类断言：
//    [A] 逐武器比率乘数表 —— 期望值照原版 `CBasePlayer::TakeDamage` 的 `switch (iGunType)`
//        逐 case 抄写（本文件里的 Expected 是**判据**，产品代码读出来的才是被测对象）。
//    [B] 代表性结算 —— AWP / M4A1 / ELITE 各一条（有甲 / 无甲 / 护甲不足三种口径），
//        期望值是**手算**的常数，⛔ 不是拿被测函数自己算出来再比。
//
//  比率表出处 = 公开源 `rehlds/ReGameDLL_CS` 的 `regamedll/dlls/player.cpp`：
//    https://cdn.jsdelivr.net/gh/rehlds/ReGameDLL_CS@master/regamedll/dlls/player.cpp
//  `ARMOR_RATIO` / `ARMOR_BONUS` 均 0.5（见 CsConst.ArmorAbsorbRatio / ArmorDamageRatio）。
//
//  末行 `RESULT-ARMOR: PASS|FAIL`；exit 0 = PASS。
// =====================================================================================

using System;
using System.Collections.Generic;
using Cs16.Core;

internal static class Program
{
    private const float Eps = 1e-4f;

    private static int _checks;
    private static int _fail;

    /// <summary>原版 `switch (iGunType)` 的逐武器乘数（照 player.cpp 逐 case 抄）。</summary>
    private static readonly Dictionary<string, float> Expected = new Dictionary<string, float>
    {
        { CsWeapons.Aug, 1.4f }, { CsWeapons.M4A1, 1.4f },
        { CsWeapons.Awp, 1.95f }, { CsWeapons.G3sg1, 1.65f }, { CsWeapons.Sg550, 1.45f },
        { CsWeapons.M249, 1.5f }, { CsWeapons.Elite, 1.05f }, { CsWeapons.Deagle, 1.5f },
        { CsWeapons.Glock18, 1.05f }, { CsWeapons.FiveSeven, 1.5f }, { CsWeapons.P90, 1.5f },
        { CsWeapons.Mac10, 0.95f }, { CsWeapons.P228, 1.25f },
        { CsWeapons.Scout, 1.7f }, { CsWeapons.Knife, 1.7f },
        { CsWeapons.Famas, 1.4f }, { CsWeapons.Sg552, 1.4f },
        { CsWeapons.Galil, 1.55f }, { CsWeapons.Ak47, 1.55f },
    };

    /// <summary>原版 `switch` 里没有的武器 ⇒ `default` ⇒ 乘数保持 1。</summary>
    private static readonly string[] TableDefaults =
    {
        CsWeapons.Mp5, CsWeapons.Ump45, CsWeapons.Tmp, CsWeapons.Usp, CsWeapons.M3,
        CsWeapons.Xm1014, CsWeapons.HeGrenade, CsWeapons.Flashbang, CsWeapons.SmokeGrenade,
        CsWeapons.C4, CsWeapons.Vest, CsWeapons.VestHelm, CsWeapons.Defuser,
    };

    private static int Main()
    {
        Console.WriteLine("code    : 真 Core/{CsWeapons,CsConst,CsEnums}.cs（逐字编译，不生成副本） + shim UnityEngine.Vector3");
        Console.WriteLine("source  : regamedll/dlls/player.cpp 的 CBasePlayer::TakeDamage（逐武器 switch）");
        Console.WriteLine("params  : ARMOR_RATIO=0.5 ARMOR_BONUS=0.5");
        Console.WriteLine();

        Console.WriteLine("[A] 逐武器护甲比率乘数（期望 = 原版 switch 的 case）");
        foreach (var kv in Expected) CheckScale(kv.Key, kv.Value);

        Console.WriteLine();
        Console.WriteLine("[A2] 表外武器（原版 default ⇒ 1）");
        foreach (var id in TableDefaults) CheckScale(id, 1f);

        Console.WriteLine();
        Console.WriteLine("[A3] 反向核对：产品表里乘数 ≠ 1 的武器必须**恰好**是 Expected 那 19 把");
        var actualNonDefault = new List<string>();
        foreach (var def in CsWeapons.All)
            if (Math.Abs(CsWeapons.ArmorRatioScaleFor(def) - 1f) > Eps) actualNonDefault.Add(def.Id);
        actualNonDefault.Sort();
        var expectedIds = new List<string>(Expected.Keys);
        expectedIds.Sort();
        Check("非 default 武器集合大小", actualNonDefault.Count, expectedIds.Count);
        for (var i = 0; i < Math.Max(actualNonDefault.Count, expectedIds.Count); i++)
        {
            var got = i < actualNonDefault.Count ? actualNonDefault[i] : "(缺)";
            var want = i < expectedIds.Count ? expectedIds[i] : "(多)";
            Check($"第 {i} 项武器 id", got == want ? 0f : 1f, 0f);
            if (got != want) Console.WriteLine($"       got={got} want={want}");
        }

        // ---- 手算的期望值 ----
        // ratio = 0.5 × scale；flNew = ratio × dmg；flArmor = (dmg − flNew) × 0.5。
        //   AWP  (1.95): ratio 0.975 → dmg 115 ⇒ flNew 112.125 / flArmor 1.4375 ⇒ 扣甲 1 / 落血 round(112.125) = 112
        //   M4A1 (1.4) : ratio 0.700 → dmg  32 ⇒ flNew  22.4   / flArmor 4.8    ⇒ 扣甲 5 / 落血 round(22.4)   =  22
        //   ELITE(1.05): ratio 0.525 → dmg  38 ⇒ flNew  19.95  / flArmor 9.025  ⇒ 扣甲 9 / 落血 round(19.95)  =  20
        //   MP5  (default): ratio 0.5 → dmg  26 ⇒ flNew  13     / flArmor 6.5    ⇒ 扣甲 6（Math.Round 半值取偶）
        //   护甲不足（armor=1）: flNew = dmg − 1 × (1 / 0.5) = dmg − 2，扣甲 1
        Console.WriteLine();
        Console.WriteLine("[B] 代表性结算（有甲 armor=100）");
        Settlement(CsWeapons.Awp, 115f, 112.125f, 1, 112);
        Settlement(CsWeapons.M4A1, 32f, 22.4f, 5, 22);
        Settlement(CsWeapons.Elite, 38f, 19.95f, 9, 20);
        Settlement(CsWeapons.Mp5, 26f, 13f, 6, 13);
        Settlement(CsWeapons.Ak47, 36f, 27.9f, 4, 28);   // ratio 0.775 → flArmor (36−27.9)×0.5 = 4.05 ⇒ 4

        Console.WriteLine();
        Console.WriteLine("[C] 无甲（armor=0）：伤害原样、不扣甲");
        NoArmor(CsWeapons.Awp, 115f);
        NoArmor(CsWeapons.M4A1, 32f);
        NoArmor(CsWeapons.Elite, 38f);

        Console.WriteLine();
        Console.WriteLine("[D] 护甲不足（armor=1）：剩余护甲折成 2 点伤害抵掉后清零");
        Insufficient(CsWeapons.Awp, 115f, 113f);
        Insufficient(CsWeapons.M4A1, 32f, 30f);
        Insufficient(CsWeapons.Elite, 38f, 36f);

        Console.WriteLine();
        Console.WriteLine($"checks={_checks} failed={_fail}");
        Console.WriteLine(_fail == 0 ? "RESULT-ARMOR: PASS" : "RESULT-ARMOR: FAIL");
        return _fail == 0 ? 0 : 1;
    }

    private static void CheckScale(string id, float want)
    {
        var def = CsWeapons.Get(id);
        if (def == null) { Check($"取武器 {id}", 0f, 1f); return; }
        Check($"scale {id}", CsWeapons.ArmorRatioScaleFor(def), want);
    }

    private static void Settlement(string id, float dmg, float wantDamage, int wantLoss, int wantRounded)
    {
        var def = CsWeapons.Get(id);
        if (def == null) { Check($"取武器 {id}", 0f, 1f); return; }
        var got = CsWeapons.ArmorAbsorb(def, dmg, 100, out var loss);
        Check($"{id} 落血(未取整)", got, wantDamage);
        Check($"{id} 落血(取整, 同 CsDamage 的 RoundToInt)", (float)Math.Round(got), wantRounded);
        Check($"{id} 扣甲", loss, wantLoss);
    }

    private static void NoArmor(string id, float dmg)
    {
        var def = CsWeapons.Get(id);
        if (def == null) { Check($"取武器 {id}", 0f, 1f); return; }
        var got = CsWeapons.ArmorAbsorb(def, dmg, 0, out var loss);
        Check($"{id} 无甲落血", got, dmg);
        Check($"{id} 无甲扣甲", loss, 0);
    }

    private static void Insufficient(string id, float dmg, float wantDamage)
    {
        var def = CsWeapons.Get(id);
        if (def == null) { Check($"取武器 {id}", 0f, 1f); return; }
        var got = CsWeapons.ArmorAbsorb(def, dmg, 1, out var loss);
        Check($"{id} 护甲不足落血", got, wantDamage);
        Check($"{id} 护甲不足扣甲", loss, 1);
    }

    private static void Check(string what, float got, float want)
    {
        _checks++;
        var ok = Math.Abs(got - want) <= Eps;
        if (!ok) _fail++;
        Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {what}: got={got:F4} want={want:F4}");
    }
}
