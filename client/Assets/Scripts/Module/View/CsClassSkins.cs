using Cs16.Core;

namespace Cs16.Module.View
{
    /// <summary>
    /// 兵种 → 皮肤键（本工程 <c>Resources/Art/{T|CT}/</c> 下的预制体名，见 <see cref="CsViewTuning.SkinsT"/> / <see cref="CsViewTuning.SkinsCT"/>）。
    ///
    /// <para><b>兵种清单、顺序与模型名都来自原版</b>：<c>原版资源/cs16src/cstrike/dlls/mp.dll</c> ——
    /// 每队兵种表在偏移 <c>0x10e38c</c>（T 侧 <c>terror, leet, arctic, guerilla, militia</c>；
    /// CT 侧 <c>urban, gsg9, sas, gign, spetsnaz</c>；同序表另见 <c>0x118070</c>），
    /// 模型路径表在 <c>0x10d8b0</c> 起（<c>models/player/&lt;兵种&gt;/&lt;兵种&gt;.mdl</c>）。
    /// 两份选兵种载体的行序与这两张表一致：<c>Classmenu_TER.res</c> 行 1..5 = terror/leet/arctic/guerilla/militia，
    /// <c>Classmenu_CT.res</c> 行 1..5 = urban/gsg9/sas/gign/spetsnaz。</para>
    ///
    /// <para><b>本工程皮肤键的对应</b>：原版 CT 的 <c>urban</c> 与 T 的 <c>terror</c> 在本工程统一叫
    /// <c>player</c>（两个皮肤表的第一个）；其余同名。原版 <c>militia</c>（T 侧第 5）与 <c>spetsnaz</c>（CT 侧第 5）
    /// 在本工程**没有模型数据** —— <c>Assets/Editor/Views/ModelData/</c> 下只有
    /// <c>player_CT[_gign|_gsg9|_sas|_vip]</c> 与 <c>player_T[_arctic|_guerilla|_leet|_orig]</c>，
    /// 生成器 <c>Assets/Editor/Views/ArtSetup.cs</c> 也无从生成 ⇒ 这两个兵种返回 <c>null</c>，
    /// 由调用方回落到队内默认皮肤并留 Warn（登记在 <c>策划/差异登记.tsv</c>）。</para>
    /// </summary>
    public static class CsClassSkins
    {
        /// <summary>T 侧兵种名（<c>mp.dll</c> 每队兵种表 + <c>Classmenu_TER.res</c> 行序）；下标 0 = <c>joinclass 1</c>。</summary>
        public static readonly string[] ClassesT = { "terror", "leet", "arctic", "guerilla", "militia" };

        /// <summary>CT 侧兵种名（同上；<c>Classmenu_CT.res</c> 行序）；下标 0 = <c>joinclass 1</c>。</summary>
        public static readonly string[] ClassesCT = { "urban", "gsg9", "sas", "gign", "spetsnaz" };

        /// <summary>兵种名（<c>joinclass 1..5</c>，越界/0 返回 <c>null</c> = 没选）。</summary>
        public static string ClassName(CsTeam team, int joinClass)
        {
            var names = team == CsTeam.CT ? ClassesCT : (team == CsTeam.T ? ClassesT : null);
            if (names == null) return null;
            var index = joinClass - 1;
            return (index < 0 || index >= names.Length) ? null : names[index];
        }

        /// <summary>兵种 → 皮肤键；本工程没有该兵种的模型数据时返回 <c>null</c>（调用方回落队内默认）。</summary>
        public static string SkinKey(CsTeam team, int joinClass)
        {
            var name = ClassName(team, joinClass);
            if (name == null) return null;
            switch (name)
            {
                // 原版 urban / terror 在本工程统一叫 player（两个皮肤表的第一个）
                case "urban":
                case "terror":
                    return CsViewTuning.PlayerModelName;

                // 本工程没有这两个兵种的模型数据（见类注释）⇒ 让调用方回落
                case "militia":
                case "spetsnaz":
                    return null;

                default:
                    return name;   // leet / arctic / guerilla / gsg9 / sas / gign：与原版同名
            }
        }
    }
}
