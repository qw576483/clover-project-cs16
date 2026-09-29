// =====================================================================================
//  Shim/EngineStub.cs —— `CloverEngine` 的最小替身：真 CsMap.cs 只经 `Game.Map`（位图 +
//  标记点）与 `Game.Logger` 两条路取引擎能力（见 CsMap.cs 的调用点），这里把它们做成
//  可直接注入的替身，其余引擎能力一律没有（有就意味着本宿主越界）。
//
//  `MapDataStub` 的 `WalkableAt` 直接读 `ShimWorld`（同一份解析几何），因此位图与几何
//  是同一套输入 —— 与实机上"位图 + 真几何"两份载体同源的口径一致。
// =====================================================================================

using System;
using System.Collections.Generic;
using UnityEngine;

namespace CloverEngine
{
    /// <summary>命名标记点（真引擎同形：名字 + 世界坐标）。</summary>
    public struct MapPoint
    {
        public string Name;
        public Vector3 Position;
        public MapPoint(string name, Vector3 position) { Name = name; Position = position; }
    }

    /// <summary>逻辑地图（真 CsMap 用到的那几个成员）。</summary>
    public interface IMapData
    {
        bool Loaded { get; }
        string Name { get; }
        string Status { get; }
        void LoadFromResource(string resourcePath, Action<bool> onDone);
        bool WalkableAt(float x, float z);
        IReadOnlyList<MapPoint> Points { get; }
        void Clear();
    }

    public sealed class MapDataStub : IMapData
    {
        private readonly List<MapPoint> _points = new List<MapPoint>();
        public ShimPhysics.ShimWorld World;

        public bool Loaded { get; private set; }
        public string Name => "shim-scenario";
        public string Status { get; private set; } = "未加载";

        public void AddPoint(string name, Vector3 p) => _points.Add(new MapPoint(name, p));

        public void LoadFromResource(string resourcePath, Action<bool> onDone)
        {
            Loaded = World != null;
            Status = Loaded ? "已加载（shim 解析几何）" : "shim 世界未装载";
            onDone?.Invoke(Loaded);
        }

        public bool WalkableAt(float x, float z) => World != null && World.WalkableAt(x, z);

        public IReadOnlyList<MapPoint> Points => _points;

        public void Clear()
        {
            Loaded = false;
            Status = "未加载";
        }
    }

    /// <summary>日志出口（打印到 stdout；静态即可）。</summary>
    public static class ShimLog
    {
        public static bool Verbose;

        public static void Info(string tag, string msg) { if (Verbose) Console.WriteLine("[I][" + tag + "] " + msg); }
        public static void Warn(string tag, string msg) { if (Verbose) Console.WriteLine("[W][" + tag + "] " + msg); }
        public static void Error(string tag, string msg) => Console.WriteLine("[E][" + tag + "] " + msg);
    }

    /// <summary>`Game.Logger` 的替身：只暴露 CsMap 用到的 Info / Warn / Error。</summary>
    public sealed class Logger
    {
        public void Info(string tag, string msg) => ShimLog.Info(tag, msg);
        public void Warn(string tag, string msg) => ShimLog.Warn(tag, msg);
        public void Error(string tag, string msg) => ShimLog.Error(tag, msg);
    }

    /// <summary>引擎入口替身：`Game.Map`（可注入）+ `Game.Logger`。</summary>
    public static class Game
    {
        public static IMapData Map { get; set; }
        public static Logger Logger { get; } = new Logger();
    }
}
