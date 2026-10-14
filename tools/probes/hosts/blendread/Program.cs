using System;
using System.IO;
using Cs16.EditorTools;

namespace Cs16AnimBlendReadcheck
{
    /// <summary>
    /// Offline read-back host for <c>.cs16anim</c>: compiles the real editor-side reader
    /// (<c>client/Assets/Editor/Views/Cs16AnimData.cs</c>, deliberately free of UnityEngine) and
    /// prints what it gets back, so "the new blend format is readable, and the old one still is"
    /// can be answered without an Editor.
    ///
    /// <para><b>Usage</b>: <c>dotnet run --project tools/probes/hosts/blendread/ReadCheck.csproj --
    /// &lt;file.cs16anim&gt; [more...]</c></para>
    ///
    /// Missing input is reported by name (with where the files normally live) and the process
    /// exits non-zero -- a run with nothing to read must never look like a pass.
    /// </summary>
    internal static class Program
    {
        private const string WHERE =
            "assets normally live in client/Assets/Editor/Views/ModelData/<key>.cs16anim; they are "
            + "produced by the exporter in the local extraction tree, whose blend change is carried "
            + "by tools/probes/cs16anim-blend-v3.patch (forward = post-change, reverse = pre-change)";

        private static int Main(string[] args)
        {
            if (args.Length == 0)
            {
                Console.WriteLine("usage: ReadCheck <file.cs16anim> [more files...]");
                Console.WriteLine("  " + WHERE);
                return 2;
            }

            var bad = 0;
            foreach (var path in args)
            {
                if (!File.Exists(path))
                {
                    Console.WriteLine("MISSING " + path);
                    Console.WriteLine("  " + WHERE);
                    bad++;
                    continue;
                }

                var a = Cs16AnimReader.Read(path, out var err);
                if (a == null)
                {
                    Console.WriteLine("FAIL " + path + " : " + err);
                    bad++;
                    continue;
                }
                var multi = 0;
                foreach (var c in a.Clips) if (c.NumBlends > 1) multi++;
                Console.WriteLine("OK   " + path);
                Console.WriteLine("     key=" + a.Key + " bones=" + a.Bones.Count + " caps=" +
                                  a.Caps.Count + " subs=" + a.Subs.Count + " clips=" + a.Clips.Count +
                                  " clipsWithBlends=" + multi);

                foreach (var name in new[] { "ref_aim_rifle", "ref_shoot_rifle", "ref_reload_rifle", "idle1" })
                {
                    var c = a.Clip(name);
                    if (c == null) continue;
                    Console.WriteLine("     " + name + ": numblends=" + c.NumBlends +
                                      " blendTracks=" + c.BlendTracks.Length +
                                      " bonesPerBlend=" + c.Tracks.Length +
                                      " tracks[0].Length=" + c.Tracks.Length);
                    for (var b = 0; b < c.NumBlends; b++)
                    {
                        var tracks = c.BlendTracks[b];
                        var keys = 0;
                        double digest = 0;
                        for (var i = 0; i < tracks.Length; i++)
                        {
                            var tr = tracks[i];
                            if (tr == null) continue;
                            keys += tr.Count;
                            for (var k = 0; k < tr.Count; k++)
                            {
                                digest += tr.Times[k] * 1.0
                                          + tr.Pos[k * 3] * 3.0 + tr.Pos[k * 3 + 1] * 5.0 + tr.Pos[k * 3 + 2] * 7.0
                                          + tr.Quat[k * 4] * 11.0 + tr.Quat[k * 4 + 1] * 13.0
                                          + tr.Quat[k * 4 + 2] * 17.0 + tr.Quat[k * 4 + 3] * 19.0;
                            }
                        }
                        Console.WriteLine("        b" + b + " keys=" + keys + " digest=" + digest.ToString("F6"));
                    }
                }
            }
            Console.WriteLine(bad == 0 ? "ALL FILES READ OK" : ("FAILED = " + bad));
            return bad;
        }
    }
}
