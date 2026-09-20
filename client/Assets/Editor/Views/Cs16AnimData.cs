using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Cs16.EditorTools
{
    /// <summary>一根骨骼的绑定姿态（局部变换，导出空间：Unity 轴、米）。</summary>
    internal sealed class Cs16AnimBone
    {
        public string Name = "";
        public int Parent = -1;
        public float Px, Py, Pz;
        public float Qx, Qy, Qz, Qw = 1f;
    }

    /// <summary>一个命中区（胶囊）—— 4 段，挂在某根骨骼下。尺寸已在导出空间（米）。</summary>
    internal sealed class Cs16AnimCapsule
    {
        public string Zone = "";
        public int Bone;
        public float CenterY;
        public float CapH;
        public float CapR;
    }

    /// <summary>一个贴图分块的蒙皮网格（顶点已是**绑定姿态世界坐标**，单骨骼权重 1.0）。</summary>
    internal sealed class Cs16AnimSkin
    {
        public string Tex = "";
        public uint Flags;
        public int VertCount;
        public int TriCount;
        public float[] Verts = new float[0];     // 3 * VertCount
        public float[] Uvs = new float[0];       // 2 * VertCount
        public float[] Norms = new float[0];     // 3 * VertCount
        public byte[] BoneIdx = new byte[0];     // VertCount（GoldSrc 单骨骼刚性蒙皮）
        public uint[] Tris = new uint[0];        // 3 * TriCount
    }

    /// <summary>一条骨骼在一条 clip 里的关键帧轨道（每骨骼自己的时间轴）。</summary>
    internal sealed class Cs16AnimTrack
    {
        public int Count;
        public float[] Times = new float[0];     // Count
        public float[] Pos = new float[0];       // 3 * Count
        public float[] Quat = new float[0];      // 4 * Count
    }

    /// <summary>一条动画 clip（名字 = Animator 里的 state 名）。</summary>
    internal sealed class Cs16AnimClip
    {
        public string Name = "";
        public float Fps = 30f;
        public bool Loop;
        public Cs16AnimTrack[] Tracks = new Cs16AnimTrack[0];
    }

    /// <summary>一个模型的完整骨骼动画数据（对应一个 <c>.cs16anim</c>）。</summary>
    internal sealed class Cs16AnimAsset
    {
        public string Key = "";
        public float Scale = 1f;
        public float Ox, Oy, Oz;                 // 导出空间偏移（Unity 轴、HL 单位）
        public readonly List<Cs16AnimBone> Bones = new List<Cs16AnimBone>();
        public readonly List<Cs16AnimCapsule> Caps = new List<Cs16AnimCapsule>();
        public readonly List<Cs16AnimSkin> Subs = new List<Cs16AnimSkin>();
        public readonly List<Cs16AnimClip> Clips = new List<Cs16AnimClip>();

        /// <summary>找 clip（按 state 名）。没有返回 null。</summary>
        public Cs16AnimClip Clip(string name)
        {
            for (var i = 0; i < Clips.Count; i++)
                if (Clips[i].Name == name) return Clips[i];
            return null;
        }

        /// <summary>按父链累乘出每根骨骼的**绑定世界矩阵**（3x4，行主序，导出空间）。
        /// 说明：导出空间里根骨骼的局部平移已经减掉了偏移量，所以它的局部就是世界。</summary>
        public float[][] BindWorld()
        {
            var world = new float[Bones.Count][];
            for (var i = 0; i < Bones.Count; i++)
            {
                var b = Bones[i];
                var m = Cs16Mat.QuatToMatrix(b.Qx, b.Qy, b.Qz, b.Qw);
                m[3] = b.Px; m[7] = b.Py; m[11] = b.Pz;
                if (b.Parent >= 0 && b.Parent < i) world[i] = Cs16Mat.Mul(world[b.Parent], m);
                else world[i] = m;
            }
            return world;
        }
    }

    /// <summary>
    /// 读 <c>Assets/Editor/Views/ModelData/*.cs16anim</c>（由
    /// <c>原版资源/cs16src/cs16_anim.py</c> 从 CS 1.6 原始 mdl 导出）。
    ///
    /// <para><b>刻意不引用 UnityEngine</b>：这样同一个读取实现既能被 Editor 生成器用，
    /// 也能在一个**离线自检宿主**里编译运行（把「生成器实际读到的值」和原版 mdl 实测值
    /// 逐项对照），无需活编辑器。转换到 Unity 类型由生成器侧完成。</para>
    ///
    /// <para><b>格式（小端）</b>：</para>
    /// <code>
    /// 'C16A' u32 version=1  u32 keyLen key[]
    /// f32 scale  f32[3] offset(ox,oy,oz)
    /// u32 numbones  每个: u32 nameLen name[]  i32 parent  f32[3] pos  f32[4] quat
    /// u32 numcaps   每个: u32 zoneLen zone[]  i32 bone     f32 centerY capH capR
    /// u32 numsubs   每个: u32 texLen tex[]  u32 flags  u32 vc  u32 tc
    ///                    f32[3*vc] verts  f32[2*vc] uvs  f32[3*vc] norms
    ///                    u8[vc] boneIdx   u32[3*tc] tris
    /// u32 numclips  每个: u32 nameLen name[]  f32 fps  u32 loop  u32 numbones
    ///                    每骨骼: u32 keyCount  然后 keyCount × f32[8] = (t,pos.xyz,quat.xyzw)
    /// 'ENDA'
    /// </code>
    /// </summary>
    internal static class Cs16AnimReader
    {
        private const string Magic = "C16A";
        private const string EndMagic = "ENDA";

        /// <summary>读一个 <c>.cs16anim</c>；失败返回 null 并给出原因（<paramref name="error"/>）。</summary>
        public static Cs16AnimAsset Read(string path, out string error)
        {
            error = null;
            if (!File.Exists(path)) { error = "文件不存在：" + path; return null; }
            try
            {
                using (var fs = File.OpenRead(path))
                using (var br = new BinaryReader(fs))
                {
                    var magic = new string(br.ReadChars(4));
                    if (magic != Magic) { error = path + " 头标记不对（" + magic + "），期望 " + Magic; return null; }
                    var version = br.ReadUInt32();
                    if (version != 1) { error = path + " 版本 " + version + " 不认识（本生成器只支持 1）"; return null; }

                    var a = new Cs16AnimAsset { Key = ReadString(br) };
                    a.Scale = br.ReadSingle();
                    a.Ox = br.ReadSingle(); a.Oy = br.ReadSingle(); a.Oz = br.ReadSingle();

                    var nb = (int)br.ReadUInt32();
                    for (var i = 0; i < nb; i++)
                    {
                        var bone = new Cs16AnimBone { Name = ReadString(br), Parent = br.ReadInt32() };
                        bone.Px = br.ReadSingle(); bone.Py = br.ReadSingle(); bone.Pz = br.ReadSingle();
                        bone.Qx = br.ReadSingle(); bone.Qy = br.ReadSingle();
                        bone.Qz = br.ReadSingle(); bone.Qw = br.ReadSingle();
                        a.Bones.Add(bone);
                    }
                    if (a.Bones.Count != nb) { error = path + " 骨骼表被截断"; return null; }

                    var nc = (int)br.ReadUInt32();
                    for (var i = 0; i < nc; i++)
                    {
                        a.Caps.Add(new Cs16AnimCapsule
                        {
                            Zone = ReadString(br),
                            Bone = br.ReadInt32(),
                            CenterY = br.ReadSingle(),
                            CapH = br.ReadSingle(),
                            CapR = br.ReadSingle(),
                        });
                    }

                    var ns = (int)br.ReadUInt32();
                    for (var i = 0; i < ns; i++)
                    {
                        var s = new Cs16AnimSkin { Tex = ReadString(br), Flags = br.ReadUInt32() };
                        s.VertCount = (int)br.ReadUInt32();
                        s.TriCount = (int)br.ReadUInt32();
                        s.Verts = ReadFloats(br, s.VertCount * 3);
                        s.Uvs = ReadFloats(br, s.VertCount * 2);
                        s.Norms = ReadFloats(br, s.VertCount * 3);
                        s.BoneIdx = br.ReadBytes(s.VertCount);
                        s.Tris = new uint[s.TriCount * 3];
                        for (var k = 0; k < s.Tris.Length; k++) s.Tris[k] = br.ReadUInt32();
                        a.Subs.Add(s);
                    }

                    var nk = (int)br.ReadUInt32();
                    for (var i = 0; i < nk; i++)
                    {
                        var clip = new Cs16AnimClip
                        {
                            Name = ReadString(br),
                            Fps = br.ReadSingle(),
                            Loop = br.ReadUInt32() != 0,
                        };
                        var boneCount = (int)br.ReadUInt32();
                        clip.Tracks = new Cs16AnimTrack[boneCount];
                        for (var b = 0; b < boneCount; b++)
                        {
                            var tr = new Cs16AnimTrack { Count = (int)br.ReadUInt32() };
                            tr.Times = new float[tr.Count];
                            tr.Pos = new float[tr.Count * 3];
                            tr.Quat = new float[tr.Count * 4];
                            for (var k = 0; k < tr.Count; k++)
                            {
                                tr.Times[k] = br.ReadSingle();
                                tr.Pos[k * 3] = br.ReadSingle();
                                tr.Pos[k * 3 + 1] = br.ReadSingle();
                                tr.Pos[k * 3 + 2] = br.ReadSingle();
                                tr.Quat[k * 4] = br.ReadSingle();
                                tr.Quat[k * 4 + 1] = br.ReadSingle();
                                tr.Quat[k * 4 + 2] = br.ReadSingle();
                                tr.Quat[k * 4 + 3] = br.ReadSingle();
                            }
                            clip.Tracks[b] = tr;
                        }
                        a.Clips.Add(clip);
                    }

                    var end = new string(br.ReadChars(4));
                    if (end != EndMagic) { error = path + " 结尾标记异常（" + end + "），数据可能被截断"; return null; }
                    return a;
                }
            }
            catch (Exception ex)
            {
                error = path + " 读取抛异常：" + ex.Message;
                return null;
            }
        }

        private static float[] ReadFloats(BinaryReader br, int count)
        {
            var a = new float[count];
            for (var i = 0; i < count; i++) a[i] = br.ReadSingle();
            return a;
        }

        private static string ReadString(BinaryReader br)
        {
            var len = (int)br.ReadUInt32();
            if (len <= 0) return string.Empty;
            return Encoding.UTF8.GetString(br.ReadBytes(len));
        }
    }

    /// <summary>BCL 侧的 3x4 矩阵小工具（行主序，长度 12）。</summary>
    internal static class Cs16Mat
    {
        public static float[] QuatToMatrix(float x, float y, float z, float w)
        {
            var m = new float[12];
            m[0] = 1 - 2 * y * y - 2 * z * z; m[1] = 2 * x * y - 2 * w * z; m[2] = 2 * x * z + 2 * w * y;
            m[4] = 2 * x * y + 2 * w * z; m[5] = 1 - 2 * x * x - 2 * z * z; m[6] = 2 * y * z - 2 * w * x;
            m[8] = 2 * x * z - 2 * w * y; m[9] = 2 * y * z + 2 * w * x; m[10] = 1 - 2 * x * x - 2 * y * y;
            return m;
        }

        public static float[] Mul(float[] a, float[] b)
        {
            var o = new float[12];
            for (var r = 0; r < 3; r++)
            {
                for (var c = 0; c < 3; c++)
                    o[r * 4 + c] = a[r * 4] * b[c] + a[r * 4 + 1] * b[4 + c] + a[r * 4 + 2] * b[8 + c];
                o[r * 4 + 3] = a[r * 4] * b[3] + a[r * 4 + 1] * b[7] + a[r * 4 + 2] * b[11] + a[r * 4 + 3];
            }
            return o;
        }
    }
}
