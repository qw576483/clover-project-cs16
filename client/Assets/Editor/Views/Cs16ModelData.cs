using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Cs16.EditorTools
{
    /// <summary>
    /// 一个命中区（或整个视模型）的网格数据块。
    /// <para>顶点坐标已经**相对本块的 centerY**（Unity 轴、米）——这样生成预制体时把节点摆到
    /// <c>(0, centerY, 0)</c>，网格与胶囊碰撞体自然重合。</para>
    /// </summary>
    internal sealed class Cs16MeshPart
    {
        public string Zone = "";
        public float CenterY;
        public float CapsuleHeight;
        public float CapsuleRadius;
        public readonly List<Cs16SubMesh> Subs = new List<Cs16SubMesh>();
    }

    /// <summary>同一张贴图下的一个网格块（一个 part 可能有多张贴图 → 多个 submesh）。</summary>
    internal sealed class Cs16SubMesh
    {
        public string Texture = "";
        public int TextureFlags;
        public Vector3[] Verts = new Vector3[0];
        public Vector2[] Uvs = new Vector2[0];
        public Vector3[] Norms = new Vector3[0];
        public int[] Tris = new int[0];
    }

    /// <summary>一个模型的完整数据。</summary>
    internal sealed class Cs16ModelData
    {
        public string Key = "";
        public float NativeHeight;      // 原始 GoldSrc 单位高度（诊断用）
        public float Scale;             // HL 单位 → 本块空间 的缩放
        public Vector3 BoundsMin;
        public Vector3 BoundsMax;
        public readonly List<Cs16MeshPart> Parts = new List<Cs16MeshPart>();

        public int TriangleCount
        {
            get
            {
                var n = 0;
                for (var i = 0; i < Parts.Count; i++)
                {
                    for (var j = 0; j < Parts[i].Subs.Count; j++) n += Parts[i].Subs[j].Tris.Length / 3;
                }
                return n;
            }
        }
    }

    /// <summary>
    /// 读取 <c>Assets/Editor/Views/ModelData/*.cs16mdl</c>（由 <c>_assets_tmp/cs16src/cs16_build.py</c>
    /// 从 CS 1.6 的 GoldSrc MDL 转换而来）。
    ///
    /// <para>格式（小端）：</para>
    /// <code>
    /// 'C16M' u32 version u32 keyLen key[]
    /// u32 partCount f32 nativeHeight f32 scale f32[6] bounds
    /// 每个 part : u32 zoneLen zone[]  f32 centerY  f32 capsuleHeight  f32 capsuleRadius  u32 subCount
    ///   每个 sub : u32 texLen tex[]  u32 flags  u32 vertCount  u32 triCount
    ///              f32[3*vc] verts  f32[2*vc] uvs  f32[3*vc] norms  u32[3*tc] idx
    /// 'ENDM'
    /// </code>
    /// </summary>
    internal static class Cs16ModelDataReader
    {
        private const string Magic = "C16M";
        private const string EndMagic = "ENDM";

        /// <summary>读取一个 <c>.cs16mdl</c>；失败返回 null 并打印原因。</summary>
        public static Cs16ModelData Read(string assetPath)
        {
            if (!File.Exists(assetPath))
            {
                Debug.LogError($"[Cs16ModelData] 文件不存在：{assetPath}");
                return null;
            }

            try
            {
                using (var fs = File.OpenRead(assetPath))
                using (var br = new BinaryReader(fs))
                {
                    var magic = new string(br.ReadChars(4));
                    if (magic != Magic)
                    {
                        Debug.LogError($"[Cs16ModelData] {assetPath} 头标记不对（{magic}），期望 {Magic}");
                        return null;
                    }

                    var version = br.ReadUInt32();
                    if (version != 1)
                    {
                        Debug.LogError($"[Cs16ModelData] {assetPath} 版本 {version} 不认识（本生成器只支持 1）");
                        return null;
                    }

                    var data = new Cs16ModelData { Key = ReadString(br) };
                    var partCount = (int)br.ReadUInt32();
                    data.NativeHeight = br.ReadSingle();
                    data.Scale = br.ReadSingle();
                    data.BoundsMin = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                    data.BoundsMax = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());

                    for (var p = 0; p < partCount; p++)
                    {
                        var part = new Cs16MeshPart
                        {
                            Zone = ReadString(br),
                            CenterY = br.ReadSingle(),
                            CapsuleHeight = br.ReadSingle(),
                            CapsuleRadius = br.ReadSingle(),
                        };
                        var subCount = (int)br.ReadUInt32();
                        for (var s = 0; s < subCount; s++)
                        {
                            var sub = new Cs16SubMesh
                            {
                                Texture = ReadString(br),
                                TextureFlags = (int)br.ReadUInt32(),
                            };
                            var vc = (int)br.ReadUInt32();
                            var tc = (int)br.ReadUInt32();

                            sub.Verts = new Vector3[vc];
                            for (var i = 0; i < vc; i++)
                                sub.Verts[i] = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());

                            sub.Uvs = new Vector2[vc];
                            for (var i = 0; i < vc; i++)
                                sub.Uvs[i] = new Vector2(br.ReadSingle(), br.ReadSingle());

                            sub.Norms = new Vector3[vc];
                            for (var i = 0; i < vc; i++)
                                sub.Norms[i] = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());

                            sub.Tris = new int[tc * 3];
                            for (var i = 0; i < sub.Tris.Length; i++)
                                sub.Tris[i] = (int)br.ReadUInt32();

                            part.Subs.Add(sub);
                        }
                        data.Parts.Add(part);
                    }

                    var end = new string(br.ReadChars(4));
                    if (end != EndMagic)
                        Debug.LogWarning($"[Cs16ModelData] {assetPath} 结尾标记异常（{end}），数据可能被截断");

                    return data;
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[Cs16ModelData] 读取 {assetPath} 抛异常：{ex}");
                return null;
            }
        }

        private static string ReadString(BinaryReader br)
        {
            var len = (int)br.ReadUInt32();
            if (len <= 0) return string.Empty;
            var bytes = br.ReadBytes(len);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
    }
}
