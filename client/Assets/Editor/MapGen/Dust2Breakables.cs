using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace Cs16.EditorTools
{
    /// <summary>一个原版 <c>func_breakable</c> 箱体（de_dust2 共 10 个）。</summary>
    public sealed class Dust2BreakableBox
    {
        /// <summary>原版刷模型号（BSP <c>MODELS</c> lump 的下标）。</summary>
        public int BrushModel;

        /// <summary>箱体 AABB（工程 Unity 米，与 <c>de_dust2_geo.bin</c> 同一空间）。</summary>
        public Vector3 Min, Max;

        /// <summary>该箱箱面的材质组名（= <c>Level/Visual/&lt;贴图&gt;.png</c> 的组名去扩展名）。</summary>
        public string[] Textures;

        /// <summary>原版 <c>material</c> 键（de_dust2 全 2）。</summary>
        public int Material;

        /// <summary>原版 <c>health</c> 键（de_dust2 全 1；只在<see cref="BreakableByDamage"/> 为真时才会被扣）。</summary>
        public float Health;

        /// <summary>原版 <c>gibmodel</c> 键（de_dust2 全 <c>models/mil_crategibs.mdl</c>）。</summary>
        public string GibModel;

        /// <summary>原版 <c>spawnflags</c> 键（de_dust2 全 1）。</summary>
        public int SpawnFlags;

        /// <summary>
        /// 这一箱会不会被伤害打碎（子弹 / 手雷 / 撞击）。
        /// <para><b>spawnflags 1 = <see cref="Dust2Breakables.SpawnFlagTriggerOnly"/></b> ⇒ 原版不接伤害：
        /// <c>CBreakable::Spawn</c> 把 <c>pev->takedamage</c> 设成 <c>DAMAGE_NO</c>，并
        /// <c>SetTouch(NULL)</c>（HLSDK <c>dlls/func_break.cpp:148-151</c> 与 <c>:168-169</c>）。
        /// de_dust2 的 10 个箱子全是这一档 ⇒ 子弹 / 手雷 / 撞击都不会让它们碎。</para>
        /// </summary>
        public bool BreakableByDamage => (SpawnFlags & Dust2Breakables.SpawnFlagTriggerOnly) == 0;

        public Vector3 Center => (Min + Max) * 0.5f;
        public Vector3 Size => Max - Min;
    }

    /// <summary>
    /// <c>Assets/ThirdParty/Dust2/breakables.tsv</c> 的读取器（生成器用）。
    ///
    /// <para><b>表从哪来</b>：<c>tools/probes/dust2_breakables.py</c> 逐条解析原版
    /// <c>de_dust2.bsp</c> 的 ENTITY / MODELS / FACES / TEXINFO / TEXTURES 五个 lump，
    /// 把 10 个 <c>func_breakable</c> 刷实体的 AABB、箱面贴图集合与 <c>material</c>/<c>health</c>/
    /// <c>gibmodel</c>/<c>spawnflags</c> 四个键写出来（坐标换算的拟合过程见该脚本头注释）。
    /// ⛔ 手改这张表等于绕过生成器 —— 要改就改脚本再重跑。</para>
    ///
    /// <para><b>表的用法</b>：<see cref="Load"/> 原样读出**全部**行（原版事实记录），
    /// 生成器只把 <see cref="Dust2BreakableBox.BreakableByDamage"/> 为真的箱体做成可破坏体。
    /// de_dust2 的 10 个箱子 <c>spawnflags</c> 全是 1（<see cref="SpawnFlagTriggerOnly"/>）
    /// ⇒ 一个都不做可破坏体，箱面留在**合并静态网格**里（原版贴图 + MeshCollider 不变）。</para>
    /// </summary>
    public static class Dust2Breakables
    {
        private const string Tag = "[Dust2Breakables]";

        /// <summary>
        /// <c>func_breakable</c> 的 <c>spawnflags</c> 位 0 = <c>SF_BREAK_TRIGGER_ONLY</c>
        /// 「may only be broken by trigger」。
        /// <para>出处：HLSDK <c>dlls/util.h:441</c>（<c>#define SF_BREAK_TRIGGER_ONLY 1</c>）；
        /// 生效点 <c>dlls/func_break.cpp:148-151</c>（置 <c>takedamage = DAMAGE_NO</c>）与
        /// <c>:168-169</c>（<c>SetTouch(NULL)</c>，不因角色撞上来而碎）。
        /// 没有这一位的箱子才是 <c>DAMAGE_YES</c>，能被伤害打碎。</para>
        /// </summary>
        public const int SpawnFlagTriggerOnly = 1;

        /// <summary>
        /// 表的字段数（brushModel + min×3 + max×3 + textures + material + health + gibmodel + spawnflags）。
        /// </summary>
        private const int FieldCount = 12;

        /// <summary>按工程路径读取；失败返回空数组并打 Error（不抛异常，调用方好判空）。</summary>
        public static Dust2BreakableBox[] Load()
        {
            string path = Dust2Layout.BreakablesFile;
            if (!File.Exists(path))
            {
                Debug.LogError($"{Tag} 找不到 {path} —— 先跑 tools/probes/dust2_breakables.py " +
                               "（木箱会整片留在合并网格里，打不碎）");
                return Array.Empty<Dust2BreakableBox>();
            }

            var list = new List<Dust2BreakableBox>();
            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.Length == 0 || line[0] == '#') continue;

                var f = line.Split('\t');
                if (f.Length != FieldCount)
                {
                    Debug.LogError($"{Tag} {path}:{i + 1} 字段数 {f.Length} ≠ {FieldCount} ⇒ 该行丢弃");
                    continue;
                }

                var box = new Dust2BreakableBox
                {
                    BrushModel = ParseInt(f[0], path, i + 1),
                    Min = new Vector3(ParseFloat(f[1], path, i + 1), ParseFloat(f[2], path, i + 1),
                                      ParseFloat(f[3], path, i + 1)),
                    Max = new Vector3(ParseFloat(f[4], path, i + 1), ParseFloat(f[5], path, i + 1),
                                      ParseFloat(f[6], path, i + 1)),
                    Textures = f[7].Split(','),
                    Material = ParseInt(f[8], path, i + 1),
                    Health = ParseFloat(f[9], path, i + 1),
                    GibModel = f[10],
                    SpawnFlags = ParseInt(f[11], path, i + 1),
                };
                if (box.Max.x <= box.Min.x || box.Max.y <= box.Min.y || box.Max.z <= box.Min.z)
                {
                    Debug.LogError($"{Tag} {path}:{i + 1} 箱 {box.BrushModel} 的 AABB 退化（max ≤ min）⇒ 该行丢弃");
                    continue;
                }
                list.Add(box);
            }

            var all = list.ToArray();
            var damageable = DamageBreakable(all);
            Debug.Log($"{Tag} 已读 {path}：{all.Length} 个 func_breakable 箱体" +
                      $"（贴图组 {CountTextures(list)} 种 / health {MinHealth(list):F0}）" +
                      $"｜按 spawnflags 判：可被伤害打碎 {damageable.Length} 个 / " +
                      $"仅 trigger 触发才碎 {all.Length - damageable.Length} 个（不生成可破坏体，箱面留在合并静态网格里）");
            return all;
        }

        /// <summary>
        /// 从整表里筛出**会被伤害打碎**的箱体（<see cref="Dust2BreakableBox.BreakableByDamage"/>）。
        /// de_dust2 的 10 个箱子全是 <c>spawnflags 1</c> ⇒ 返回空数组。
        /// </summary>
        public static Dust2BreakableBox[] DamageBreakable(Dust2BreakableBox[] all)
        {
            if (all == null || all.Length == 0) return Array.Empty<Dust2BreakableBox>();
            var list = new List<Dust2BreakableBox>(all.Length);
            foreach (var b in all) if (b.BreakableByDamage) list.Add(b);
            return list.ToArray();
        }

        private static int ParseInt(string s, string path, int line)
        {
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) return v;
            Debug.LogError($"{Tag} {path}:{line} 解析整数失败：「{s}」⇒ 取 0");
            return 0;
        }

        private static float ParseFloat(string s, string path, int line)
        {
            if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return v;
            Debug.LogError($"{Tag} {path}:{line} 解析小数失败：「{s}」⇒ 取 0");
            return 0f;
        }

        private static int CountTextures(List<Dust2BreakableBox> list)
        {
            var set = new HashSet<string>();
            foreach (var b in list) foreach (var t in b.Textures) set.Add(t);
            return set.Count;
        }

        private static float MinHealth(List<Dust2BreakableBox> list)
        {
            var min = float.MaxValue;
            foreach (var b in list) min = Mathf.Min(min, b.Health);
            return list.Count > 0 ? min : 0f;
        }
    }
}
