using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace Cs16.EditorTools
{
    /// <summary>
    /// <c>de_dust2_geo.bin</c> 的读取器（生成器 / 烘焙器 / 探针共用）。
    ///
    /// <para>
    /// 为什么中间要一层自定义二进制、而不是直接导入 OBJ：本文件由 `tools/` 侧的 BSP 转换脚本产出，
    /// 里面同时带了**渲染网格、碰撞盒、标记点、烘焙参数**四样东西，一处读取、三处复用
    /// （<see cref="Dust2Builder"/> 造场景、<see cref="MapBakeRunner"/> 取参数、
    /// <see cref="MapConnectivityProbe"/> 复算位图）。若走 OBJ 导入器，网格之外的语义还得另找地方放。
    /// </para>
    ///
    /// <para><b>格式（小端，版本 1）</b></para>
    /// <code>
    /// char[4]  "CD2G"
    /// u32      version = 1
    /// f32      cellSize, originX, originZ, groundTopY, obstacleMinHeight, probeBottomY, probeTopY
    /// u32      width, depth                                   // 位图格数
    /// f32      worldMinX, worldMinY, worldMinZ, worldMaxX, worldMaxY, worldMaxZ
    /// u32      groupCount
    ///   每材质组： u8 png[48](utf8,补 0), u32 vertCount, u32 idxCount,
    ///              f32 pos[3]*v, f32 uv[2]*v, f32 nrm[3]*v, u32 idx[idxCount]
    /// u32      blockerCount                                   // 阻挡矩形（格坐标，闭区间）
    ///   每条：    u32 ix0, iz0, ix1, iz1, f32 yMin, yMax
    /// u32      markerCount
    ///   每条：    u8 name[32](utf8,补 0), u32 ptCount, f32 xyz[3]*ptCount
    /// </code>
    ///
    /// <para><b>坐标系</b>：Unity 左手系，X 东、Y 上、Z 北，单位米（已由转换脚本从 Half-Life
    /// 单位换算并**按地图包围盒居中**）。位图格 (ix,iz) 的世界中心 =
    /// <c>(originX + (ix+0.5)*cellSize, ·, originZ + (iz+0.5)*cellSize)</c>，
    /// 与引擎 <c>CloverMap</c> 解码端 <c>WalkableAt</c> 的 floor 口径逐位一致。</para>
    /// </summary>
    public sealed class Dust2GeoData
    {
        public const string Magic = "CD2G";
        public const int Version = 1;

        /// <summary>一个材质（= 一个贴图 PNG）对应的合并网格。</summary>
        public sealed class MeshGroup
        {
            public string PngName;
            public Vector3[] Vertices;
            public Vector2[] Uvs;
            public Vector3[] Normals;
            public int[] Indices;
        }

        /// <summary>阻挡矩形：位图格坐标闭区间 + 该阻挡体的 Y 范围（世界米）。</summary>
        public struct Blocker
        {
            public int Ix0, Iz0, Ix1, Iz1;
            public float YMin, YMax;
        }

        public float CellSize, OriginX, OriginZ, GroundTopY, ObstacleMinHeight, ProbeBottomY, ProbeTopY;
        public int Width, Depth;
        public Vector3 WorldMin, WorldMax;
        public MeshGroup[] Groups;
        public Blocker[] Blockers;
        /// <summary>标记名 → 点位。名字与 <c>CsMarkers</c> 一致（生成场景与运行时表都用它）。</summary>
        public Dictionary<string, List<Vector3>> Markers = new Dictionary<string, List<Vector3>>();

        public int TotalTriangles { get; private set; }

        // ==================================================================
        //  读取
        // ==================================================================

        /// <summary>按绝对/相对工程路径读取；失败返回 null 并打 Error（不抛异常，调用方好判空）。</summary>
        public static Dust2GeoData Load(string projectPath)
        {
            if (!File.Exists(projectPath))
            {
                Debug.LogError($"[Dust2Geo] 找不到 {projectPath} —— 先跑转换脚本生成几何数据，" +
                               "或确认 Assets/ThirdParty/Dust2/ 没有被清理");
                return null;
            }

            try
            {
                using (var fs = new FileStream(projectPath, FileMode.Open, FileAccess.Read))
                using (var br = new BinaryReader(fs, Encoding.UTF8))
                {
                    return Read(br, projectPath);
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[Dust2Geo] 解析 {projectPath} 失败：{e.Message}");
                return null;
            }
        }

        private static Dust2GeoData Read(BinaryReader br, string path)
        {
            var magic = Encoding.ASCII.GetString(br.ReadBytes(4));
            if (magic != Magic)
            {
                Debug.LogError($"[Dust2Geo] 魔数不符：期望 {Magic}，实际 \"{magic}\"（文件不是本工程的几何数据）");
                return null;
            }

            int version = br.ReadInt32();
            if (version != Version)
            {
                Debug.LogError($"[Dust2Geo] 数据版本 {version} 不被支持（本生成器支持 {Version}）");
                return null;
            }

            var d = new Dust2GeoData();
            d.CellSize = br.ReadSingle();
            d.OriginX = br.ReadSingle();
            d.OriginZ = br.ReadSingle();
            d.GroundTopY = br.ReadSingle();
            d.ObstacleMinHeight = br.ReadSingle();
            d.ProbeBottomY = br.ReadSingle();
            d.ProbeTopY = br.ReadSingle();
            d.Width = (int)br.ReadUInt32();
            d.Depth = (int)br.ReadUInt32();
            d.WorldMin = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
            d.WorldMax = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());

            int groupCount = (int)br.ReadUInt32();
            d.Groups = new MeshGroup[groupCount];
            for (int g = 0; g < groupCount; g++)
            {
                var mg = new MeshGroup();
                mg.PngName = ReadFixedString(br, 48);
                int vc = (int)br.ReadUInt32();
                int ic = (int)br.ReadUInt32();
                mg.Vertices = new Vector3[vc];
                mg.Uvs = new Vector2[vc];
                mg.Normals = new Vector3[vc];
                mg.Indices = new int[ic];
                for (int i = 0; i < vc; i++) mg.Vertices[i] = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                for (int i = 0; i < vc; i++) mg.Uvs[i] = new Vector2(br.ReadSingle(), br.ReadSingle());
                for (int i = 0; i < vc; i++) mg.Normals[i] = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                for (int i = 0; i < ic; i++) mg.Indices[i] = (int)br.ReadUInt32();
                d.Groups[g] = mg;
                d.TotalTriangles += ic / 3;
            }

            int bc = (int)br.ReadUInt32();
            d.Blockers = new Blocker[bc];
            for (int i = 0; i < bc; i++)
            {
                d.Blockers[i] = new Blocker
                {
                    Ix0 = (int)br.ReadUInt32(),
                    Iz0 = (int)br.ReadUInt32(),
                    Ix1 = (int)br.ReadUInt32(),
                    Iz1 = (int)br.ReadUInt32(),
                    YMin = br.ReadSingle(),
                    YMax = br.ReadSingle(),
                };
            }

            int mc = (int)br.ReadUInt32();
            for (int i = 0; i < mc; i++)
            {
                string name = ReadFixedString(br, 32);
                int pc = (int)br.ReadUInt32();
                var list = new List<Vector3>(pc);
                for (int k = 0; k < pc; k++)
                    list.Add(new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle()));
                d.Markers[name] = list;
            }

            Debug.Log($"[Dust2Geo] 已读取 {path}：网格组 {groupCount} 个 / {d.TotalTriangles} 三角面 / " +
                      $"阻挡盒 {bc} 个 / 标记 {d.Markers.Count} 类 / 位图 {d.Width}x{d.Depth}@cell{d.CellSize:F2}");
            return d;
        }

        private static string ReadFixedString(BinaryReader br, int len)
        {
            var bytes = br.ReadBytes(len);
            int n = Array.IndexOf(bytes, (byte)0);
            if (n < 0) n = bytes.Length;
            return Encoding.UTF8.GetString(bytes, 0, n);
        }

        // ==================================================================
        //  便利查询
        // ==================================================================

        /// <summary>取某标记的点位；没有则返回空数组（不抛异常、不打日志 —— 调用方负责报）。</summary>
        public Vector3[] Points(string marker)
        {
            if (marker != null && Markers.TryGetValue(marker, out var list) && list.Count > 0)
                return list.ToArray();
            return Array.Empty<Vector3>();
        }

        /// <summary>格坐标 → 格心世界坐标（与引擎位图取整口径一致）。</summary>
        public Vector3 CellCenter(int ix, int iz)
            => new Vector3(OriginX + (ix + 0.5f) * CellSize, 0f, OriginZ + (iz + 0.5f) * CellSize);

        /// <summary>某点落在哪一格（<c>Mathf.FloorToInt</c>，与引擎 / 服务端同一口径）。</summary>
        public void CellOf(float x, float z, out int ix, out int iz)
        {
            ix = Mathf.FloorToInt((x - OriginX) / CellSize);
            iz = Mathf.FloorToInt((z - OriginZ) / CellSize);
        }

        /// <summary>由阻挡盒还原"哪一格被阻挡"（与引擎烘焙逐格判定同语义：格柱与障碍 AABB 相交即阻挡）。</summary>
        public bool[] BuildBlockedBitmap()
        {
            var blocked = new bool[Width * Depth];
            var half = new Vector3(CellSize * 0.5f, (ProbeTopY - ProbeBottomY) * 0.5f, CellSize * 0.5f);
            float probeCenterY = (ProbeTopY + ProbeBottomY) * 0.5f;

            for (int b = 0; b < Blockers.Length; b++)
            {
                var bl = Blockers[b];
                // 阻挡盒 AABB（格坐标 → 世界）
                var min = new Vector3(OriginX + bl.Ix0 * CellSize, bl.YMin, OriginZ + bl.Iz0 * CellSize);
                var max = new Vector3(OriginX + (bl.Ix1 + 1) * CellSize, bl.YMax, OriginZ + (bl.Iz1 + 1) * CellSize);
                var center = (min + max) * 0.5f;
                var extents = (max - min) * 0.5f;

                int lx0 = Mathf.Max(0, bl.Ix0 - 1), lx1 = Mathf.Min(Width - 1, bl.Ix1 + 1);
                int lz0 = Mathf.Max(0, bl.Iz0 - 1), lz1 = Mathf.Min(Depth - 1, bl.Iz1 + 1);
                for (int iz = lz0; iz <= lz1; iz++)
                {
                    for (int ix = lx0; ix <= lx1; ix++)
                    {
                        var c = new Vector3(OriginX + (ix + 0.5f) * CellSize, probeCenterY,
                                            OriginZ + (iz + 0.5f) * CellSize);
                        // 与 MapBaker.Intersects 一致：必须**严格小于**，否则障碍会沿墙外扩一格
                        if (Mathf.Abs(c.x - center.x) < half.x + extents.x &&
                            Mathf.Abs(c.y - center.y) < half.y + extents.y &&
                            Mathf.Abs(c.z - center.z) < half.z + extents.z)
                        {
                            blocked[iz * Width + ix] = true;
                        }
                    }
                }
            }
            return blocked;
        }
    }
}
