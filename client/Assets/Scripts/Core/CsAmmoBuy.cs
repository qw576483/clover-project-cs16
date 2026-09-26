using System.Collections.Generic;

namespace Cs16.Core
{
    /// <summary>
    /// 买备用弹药（原版 <c>buyammo1</c> / <c>buyammo2</c>）的价格与每份补充量。
    ///
    /// <para><b>出处（逐条从 <c>mp.dll</c> 读出，非本项目自定）</b>：原版 <c>CBasePlayer::BuyAmmo</c>
    /// （VA <c>0x1002a8b0</c>）按"买哪一槽的那把枪"查一张<b>步长 0x28</b> 的表 ——
    /// 表基址 VA <c>0x101af0b8</c>（31 条；由 VA <c>0x1002d249</c> 的 <c>rep movsd</c> 从只读模板
    /// VA <c>0x10165128</c> 拷入）。字段：<c>+0x00</c> 武器号、<c>+0x08</c> <b>弹药价格</b>
    /// （<c>0x1002a92d</c> 拿它与玩家金钱比）、<c>+0x0c</c> <b>一次购买补充的弹数</b>
    /// （<c>0x1002a93d</c> 把它作为 <c>GiveAmmo</c> 的 nCount 实参推入）、<c>+0x14</c> 携带上限
    /// （<c>0x1002a8e9</c> 用它判"已满就不卖"）。命令名 <c>buyammo1</c> / <c>buyammo2</c> 在
    /// <c>mp.dll</c> 串表 VA <c>0x101105a8</c> / <c>0x101105b4</c>；买枪菜单里这两页的
    /// <c>Command</c> 是 <c>primammo</c> / <c>secammo</c>
    /// （出处 <c>原版资源/cs16src/cstrike/resource/UI/MainBuyMenu.res:155-194</c>）。</para>
    ///
    /// <para><b>覆盖范围</b>：只含原版那张表里的 24 把枪。刀 / 手雷 / C4 / 装备不在其中
    /// ⇒ <see cref="Price"/> / <see cref="Box"/> 返回 0，调用方按"不能补弹"处理。</para>
    /// </summary>
    public static class CsAmmoBuy
    {
        /// <summary>原版 <c>buyammo1</c>：给当前<b>主武器</b>（<c>m_rgpPlayerItems[1]</c>）补弹的槽位号。</summary>
        public const int PrimarySlot = 1;

        /// <summary>原版 <c>buyammo2</c>：给当前<b>副武器</b>（<c>m_rgpPlayerItems[2]</c>）补弹的槽位号。</summary>
        public const int SecondarySlot = 2;

        private struct Entry
        {
            public int Price;   // mp.dll 表 +0x08：买一份弹药的价格
            public int Box;     // mp.dll 表 +0x0c：买一份补充的弹数
        }

        private static readonly Dictionary<string, Entry> Table = new Dictionary<string, Entry>
        {
            { CsWeapons.P228,      new Entry { Price = 50,  Box = 13 } },
            { CsWeapons.Glock18,   new Entry { Price = 20,  Box = 30 } },
            { CsWeapons.Scout,     new Entry { Price = 80,  Box = 30 } },
            { CsWeapons.Xm1014,    new Entry { Price = 65,  Box = 8 } },
            { CsWeapons.Mac10,     new Entry { Price = 25,  Box = 12 } },
            { CsWeapons.Aug,       new Entry { Price = 60,  Box = 30 } },
            { CsWeapons.Elite,     new Entry { Price = 20,  Box = 30 } },
            { CsWeapons.FiveSeven, new Entry { Price = 50,  Box = 50 } },
            { CsWeapons.Ump45,     new Entry { Price = 25,  Box = 12 } },
            { CsWeapons.Sg550,     new Entry { Price = 60,  Box = 30 } },
            { CsWeapons.Galil,     new Entry { Price = 60,  Box = 30 } },
            { CsWeapons.Famas,     new Entry { Price = 60,  Box = 30 } },
            { CsWeapons.Usp,       new Entry { Price = 25,  Box = 12 } },
            { CsWeapons.Awp,       new Entry { Price = 125, Box = 10 } },
            { CsWeapons.Mp5,       new Entry { Price = 20,  Box = 30 } },
            { CsWeapons.M249,      new Entry { Price = 60,  Box = 30 } },
            { CsWeapons.M3,        new Entry { Price = 65,  Box = 8 } },
            { CsWeapons.M4A1,      new Entry { Price = 60,  Box = 30 } },
            { CsWeapons.Tmp,       new Entry { Price = 20,  Box = 30 } },
            { CsWeapons.G3sg1,     new Entry { Price = 80,  Box = 30 } },
            { CsWeapons.Deagle,    new Entry { Price = 40,  Box = 7 } },
            { CsWeapons.Sg552,     new Entry { Price = 60,  Box = 30 } },
            { CsWeapons.Ak47,      new Entry { Price = 80,  Box = 30 } },
            { CsWeapons.P90,       new Entry { Price = 50,  Box = 50 } },
        };

        /// <summary>这把枪买一份弹药的价格（不是原版表里的武器 = 0）。</summary>
        public static int Price(string weaponId)
        {
            return !string.IsNullOrEmpty(weaponId) && Table.TryGetValue(weaponId, out var e) ? e.Price : 0;
        }

        /// <summary>这把枪买一份弹药补充的弹数（不是原版表里的武器 = 0）。</summary>
        public static int Box(string weaponId)
        {
            return !string.IsNullOrEmpty(weaponId) && Table.TryGetValue(weaponId, out var e) ? e.Box : 0;
        }
    }
}
