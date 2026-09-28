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

        /// <summary>原版 <c>health</c> 键（de_dust2 全 1 ⇒ 一发即碎）。</summary>
        public float Health;

        /// <summary>原版 <c>gibmodel</c> 键（de_dust2 全 <c>models/mil_crategibs.mdl</c>）。</summary>
        public string GibModel;

        public Vector3 Center => (Min + Max) * 0.5f;
        public Vector3 Size => Max - Min;
    }

    /// <summary>
    /// <c>Assets/ThirdParty/Dust2/breakables.tsv</c> 的读取器（生成器用）。
    ///
    /// <para><b>表从哪来</b>：<c>tools/probes/dust2_breakables.py</c> 逐条解析原版
    /// <c>de_dust2.bsp</c> 的 ENTITY / MODELS / FACES / TEXINFO / TEXTURES 五个 lump，
    /// 把 10 个 <c>func_breakable</c> 刷实体的 AABB、箱面贴图集合与 <c>material</c>/<c>health</c>/
    /// <c>gibmodel</c> 三个键写出来（坐标换算的拟合过程见该脚本头注释）。
    /// ⛔ 手改这张表等于绕过生成器 —— 要改就改脚本再重跑。</para>
    /// </summary>
    public static class Dust2Breakables
    {
        private const string Tag = "[Dust2Breakables]";

        /// <summary>表的字段数（brushModel + min×3 + max×3 + textures + material + health + gibmodel）。</summary>
        private const int FieldCount = 11;

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
                };
                if (box.Max.x <= box.Min.x || box.Max.y <= box.Min.y || box.Max.z <= box.Min.z)
                {
                    Debug.LogError($"{Tag} {path}:{i + 1} 箱 {box.BrushModel} 的 AABB 退化（max ≤ min）⇒ 该行丢弃");
                    continue;
                }
                list.Add(box);
            }

            Debug.Log($"{Tag} 已读 {path}：{list.Count} 个 func_breakable 箱体" +
                      $"（贴图组 {CountTextures(list)} 种 / health {MinHealth(list):F0}）");
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
