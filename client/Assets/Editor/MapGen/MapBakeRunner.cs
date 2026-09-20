using System.Collections.Generic;
using System.IO;
using CloverEngine.Editor;
using Cs16.Core;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Cs16.EditorTools
{
    /// <summary>
    /// de_dust2 的**命令行/一键烘焙**入口：调用引擎的 <c>CloverEngine.Editor.MapBaker.Export</c>，
    /// 一次产出同源两份 <c>de_dust2.bytes</c>
    /// （<c>Assets/MapData/</c> 服务端参考 + <c>Assets/Resources/MapData/</c> 客户端运行时）。
    ///
    /// <para><b>为什么这里要多做一步"临时关掉 Visual 的碰撞体"</b>：引擎烘焙器把场景里
    /// **所有 enabled 的 Collider** 当障碍，按 AABB 逐格判定。真实 dust2 的 <c>Level/Visual</c>
    /// 是一个个材质合并的大网格（每个 MeshCollider 的 AABB 都横跨半张图），直接烘会把整张图
    /// 判成全阻挡；而它们又是运行时物理（子弹/地面/斜坡）必需的。所以：</para>
    /// <list type="number">
    /// <item>烘焙前：关掉 <c>Level/Visual/**</c> 的碰撞体（只留 <c>Level/Blockers</c> 的格子盒）；</item>
    /// <item>烘焙：引擎按格子盒逐格判定 ⇒ 位图 = 转换脚本算好的那张图（可逐格复现）；</item>
    /// <item>烘焙后：立刻恢复碰撞体并保存场景 —— 运行时该有的物理一个不少。</item>
    /// </list>
    ///
    /// <para>菜单：<b>Clover/CS16/烘焙 de_dust2（导出 .bytes）</b>；命令行：
    /// <c>-executeMethod Cs16.EditorTools.MapBakeRunner.BakeDust2</c>。</para>
    /// </summary>
    public static class MapBakeRunner
    {
        private const string Tag = "[MapBakeRunner]";

        [MenuItem("Clover/CS16/烘焙 de_dust2（导出 .bytes）", false, 21)]
        public static void BakeDust2FromMenu()
        {
            if (EditorApplication.isPlaying)
            {
                Debug.LogError($"{Tag} 正在 Play 模式：烘焙会切场景，请先停止 Play");
                return;
            }
            BakeDust2();
        }

        /// <summary>命令行入口：构造参数 → 调引擎烘焙器 → 自检产物。</summary>
        public static void BakeDust2()
        {
            var geo = Dust2GeoData.Load(Dust2Layout.GeoFile);
            if (geo == null)
            {
                Debug.LogError($"{Tag} 没有几何数据，无法确定烘焙参数（先跑生成器 / 转换脚本）");
                return;
            }
            if (!File.Exists(Dust2Layout.ScenePath))
            {
                Debug.LogError($"{Tag} 场景不存在：{Dust2Layout.ScenePath} —— 先执行 Clover/CS16/生成 de_dust2 场景");
                return;
            }

            Scene scene = EditorSceneManager.OpenScene(Dust2Layout.ScenePath, OpenSceneMode.Single);

            // ---- ① 临时关掉"运行时物理"那一层（见类注释）----
            //
            // ★ 必须**存盘**再烘：引擎 MapBaker.Export 内部会按路径把场景**重新打开**一遍
            //   （它要防"批处理里 active scene 变成空场景"那个坑），内存里的开关会被这次重开冲掉。
            //   所以顺序是：关 → 存盘 → 烘（Export 重新打开时看到的就是关着的）→ 再打开 → 开 → 存盘。
            //   ⚠️ 若在"关着"的状态下进程被杀，磁盘上的场景会留下关掉的碰撞体 ——
            //   探针 <see cref="MapConnectivityProbe"/> 会专门检查这一条并报 Error，重跑本方法即可恢复。
            var visual = Dust2Builder.FindInScene(scene, Dust2Layout.VisualRoot);
            var disabled = new List<Collider>();
            if (visual == null)
            {
                Debug.LogError($"{Tag} 场景里没有 {Dust2Layout.VisualRoot} → 无法区分「烘焙障碍」和「运行时物理」，" +
                               "本次仍继续烘焙，但位图可能不正确");
            }
            else
            {
                foreach (var c in visual.GetComponentsInChildren<Collider>(true))
                {
                    if (c != null && c.enabled) { c.enabled = false; disabled.Add(c); }
                }
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
                Debug.Log($"{Tag} 烘焙前关闭 Visual 碰撞体 {disabled.Count} 个并已存盘（它们只服务运行时物理，不参与烘焙）");
            }

            try
            {
                // ---- ② 参数（全部来自转换脚本产出的数据，不靠 EditorPrefs 里可能过期的旧值）----
                var o = new MapBakeOptions
                {
                    ScenePath = Dust2Layout.ScenePath,
                    SceneId = Dust2Layout.SceneId,
                    MapName = CsConst.MapDust2,
                    CellSize = geo.CellSize,
                    Origin = new Vector3(geo.OriginX, geo.GroundTopY, geo.OriginZ),
                    MapWidth = geo.Width,
                    MapDepth = geo.Depth,
                    GroundTopY = geo.GroundTopY,
                    ObstacleMinHeight = geo.ObstacleMinHeight,
                    ProbeBottomY = geo.ProbeBottomY,
                    ProbeTopY = geo.ProbeTopY,
                    ServerDir = Dust2Layout.ServerMapDir,
                    ClientDir = Dust2Layout.ClientMapDir,
                    SpawnMarkerPrefix = Dust2Layout.SpawnMarkerPrefix,
                };
                o.Save();   // 存下来：面板 / 其它 agent 的 CLI 复用同一份参数

                if (!MapBaker.Export(o, out string summary))
                {
                    Debug.LogError($"{Tag} 烘焙失败：{summary}");
                    return;
                }
                Debug.Log($"{Tag} 烘焙成功：{summary}");

                // ---- ③ 产物自检（存在 + 阻挡数 > 0；=0 说明没收到障碍，是典型静默失败）----
                VerifyOutputs();
            }
            finally
            {
                // ---- ④ 恢复碰撞体 + 刷新运行时标记表（Export 内部重开过场景，手里的 Scene 已失效）----
                var reloaded = EditorSceneManager.OpenScene(Dust2Layout.ScenePath, OpenSceneMode.Single);

                int restored = 0;
                var v2 = Dust2Builder.FindInScene(reloaded, Dust2Layout.VisualRoot);
                if (v2 != null)
                {
                    foreach (var c in v2.GetComponentsInChildren<Collider>(true))
                    {
                        if (c != null && !c.enabled) { c.enabled = true; restored++; }
                    }
                }
                EditorSceneManager.MarkSceneDirty(reloaded);
                EditorSceneManager.SaveScene(reloaded);

                if (restored != disabled.Count)
                    Debug.LogError($"{Tag} 恢复数量对不上：关掉 {disabled.Count} 个 / 恢复 {restored} 个 —— " +
                                   "场景里的 Visual 碰撞体可能没全开，运行时子弹会穿墙（重跑本方法即可修）");
                else
                    Debug.Log($"{Tag} 已恢复 Visual 碰撞体 {restored} 个并保存场景（运行时物理完好）");

                // 标记改动后不必记得单独跑生成器：这里顺手把运行时标记表刷一遍
                Dust2Builder.ExportMarkerResource(reloaded);
            }
        }

        /// <summary>只读自检：两份 .bytes 是否都在、位图阻挡格是否 &gt; 0。</summary>
        public static void VerifyOutputs()
        {
            string server = $"{Dust2Layout.ServerMapDir}/{CsConst.MapDust2}.bytes";
            string client = $"{Dust2Layout.ClientMapDir}/{CsConst.MapDust2}.bytes";

            foreach (var p in new[] { server, client })
            {
                if (!File.Exists(p))
                {
                    Debug.LogError($"{Tag} 产物缺失：{p}");
                    continue;
                }
                var bytes = File.ReadAllBytes(p);
                if (CloverMapProbe.TryRead(bytes, out var info, out string err))
                {
                    Debug.Log($"{Tag} 产物 {p}：{bytes.Length} 字节｜{info}");
                    if (info.BlockedCount <= 0)
                        Debug.LogError($"{Tag} {p} 的阻挡格 = 0：位图是「全可走」的空地图（物体没 Collider？）");
                }
                else
                {
                    Debug.LogError($"{Tag} 产物 {p} 解不开：{err}");
                }
            }
        }
    }
}
