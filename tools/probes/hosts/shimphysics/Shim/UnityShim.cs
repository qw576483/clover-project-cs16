// =====================================================================================
//  Shim/UnityShim.cs —— `UnityEngine` 的最小替身（只为让**真** CsMap.cs / CsConst.cs /
//  ICsMap.cs 在编辑器外编译并**运行**）。
//
//  为什么需要它：`UnityEngine.Physics.*`（以及 Vector3 / Mathf 等）在 Unity 里来自托管
//  DLL 的 native extern —— 编得过、跑不了（编辑器外一调用就抛）。所以本宿主**不引用任何
//  Unity 程序集**，改用这里的类型 + `PhysicsShim.cs` 的解析几何实现顶上。
//
//  覆盖范围 = 真源码实际用到的那一层（不是 Unity 全量）：CsMap.cs 只用到
//  Vector3 / Mathf / LayerMask / RaycastHit / Collider / QueryTriggerInteraction / Physics
//  （逐条见 CsMap.cs 的调用点）；CsConst.cs 需要 Camera / Transform 只作为类型存在。
//  缺哪个补哪个 —— 编译错误就是清单，⛔ 不许凭印象多塞。
// =====================================================================================

using System;
using System.Globalization;

namespace UnityEngine
{
    /// <summary>三维向量（只实现真源码用到的那部分）。</summary>
    public struct Vector3 : IEquatable<Vector3>
    {
        public float x;
        public float y;
        public float z;

        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }

        public static Vector3 zero => new Vector3(0f, 0f, 0f);
        public static Vector3 one => new Vector3(1f, 1f, 1f);
        public static Vector3 up => new Vector3(0f, 1f, 0f);
        public static Vector3 down => new Vector3(0f, -1f, 0f);
        public static Vector3 left => new Vector3(-1f, 0f, 0f);
        public static Vector3 right => new Vector3(1f, 0f, 0f);
        public static Vector3 forward => new Vector3(0f, 0f, 1f);
        public static Vector3 back => new Vector3(0f, 0f, -1f);

        public float sqrMagnitude => x * x + y * y + z * z;
        public float magnitude => (float)Math.Sqrt(sqrMagnitude);

        public Vector3 normalized
        {
            get
            {
                var m = magnitude;
                return m > 1e-9f ? new Vector3(x / m, y / m, z / m) : zero;
            }
        }

        /// <summary>就地归一化（Unity 的 `Vector3.Normalize()` 是 mutating）。零向量保持零。</summary>
        public void Normalize()
        {
            var m = magnitude;
            if (m <= 1e-9f) return;
            x /= m; y /= m; z /= m;
        }

        public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new Vector3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vector3 operator -(Vector3 a) => new Vector3(-a.x, -a.y, -a.z);
        public static Vector3 operator *(Vector3 a, float s) => new Vector3(a.x * s, a.y * s, a.z * s);
        public static Vector3 operator *(float s, Vector3 a) => new Vector3(a.x * s, a.y * s, a.z * s);
        public static Vector3 operator /(Vector3 a, float s) => new Vector3(a.x / s, a.y / s, a.z / s);
        public static bool operator ==(Vector3 a, Vector3 b) => (a - b).sqrMagnitude < 1e-10f;
        public static bool operator !=(Vector3 a, Vector3 b) => !(a == b);

        public static float Distance(Vector3 a, Vector3 b) => (a - b).magnitude;
        public static float Dot(Vector3 a, Vector3 b) => a.x * b.x + a.y * b.y + a.z * b.z;

        public bool Equals(Vector3 other) => this == other;
        public override bool Equals(object obj) => obj is Vector3 v && this == v;
        public override int GetHashCode() => x.GetHashCode() ^ (y.GetHashCode() << 2) ^ (z.GetHashCode() >> 2);
        public override string ToString()
            => string.Format(CultureInfo.InvariantCulture, "({0:F3}, {1:F3}, {2:F3})", x, y, z);
    }

    /// <summary>二维整数向量（引擎 `Rng` 的整数区间重载用到）。</summary>
    public struct Vector2Int : IEquatable<Vector2Int>
    {
        public int x;
        public int y;

        public Vector2Int(int x, int y) { this.x = x; this.y = y; }

        public static Vector2Int zero => new Vector2Int(0, 0);

        public static bool operator ==(Vector2Int a, Vector2Int b) => a.x == b.x && a.y == b.y;
        public static bool operator !=(Vector2Int a, Vector2Int b) => !(a == b);

        public bool Equals(Vector2Int other) => this == other;
        public override bool Equals(object obj) => obj is Vector2Int v && this == v;
        public override int GetHashCode() => x * 397 ^ y;
        public override string ToString()
            => string.Format(CultureInfo.InvariantCulture, "({0}, {1})", x, y);
    }

    /// <summary>数学函数（只实现真源码用到的那部分）。</summary>
    public static class Mathf
    {
        public const float Epsilon = 1.401298E-45f;
        public const float PI = 3.14159274f;
        public const float Degrees2Radians = 0.0174532924f;
        public const float Radians2Degrees = 57.29578f;

        public static float Max(float a, float b) => a > b ? a : b;
        public static int Max(int a, int b) => a > b ? a : b;
        public static float Min(float a, float b) => a < b ? a : b;
        public static int Min(int a, int b) => a < b ? a : b;
        public static float Abs(float v) => Math.Abs(v);
        public static int Abs(int v) => Math.Abs(v);
        public static float Sqrt(float v) => (float)Math.Sqrt(v);
        public static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);
        public static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
        public static float Clamp01(float v) => Clamp(v, 0f, 1f);
        public static int CeilToInt(float v) => (int)Math.Ceiling(v);
        public static int FloorToInt(float v) => (int)Math.Floor(v);
        public static int RoundToInt(float v) => (int)Math.Round(v, MidpointRounding.AwayFromZero);
        public static float Floor(float v) => (float)Math.Floor(v);
        public static float Ceil(float v) => (float)Math.Ceiling(v);
        public static float Lerp(float a, float b, float t) => a + (b - a) * Clamp01(t);
        public static bool Approximately(float a, float b) => Abs(b - a) < Max(1E-06f * Max(Abs(a), Abs(b)), Epsilon * 8f);
        public static float Sign(float v) => v >= 0f ? 1f : -1f;
    }

    /// <summary>layer 掩码（隐式转 int，与 Unity 一致）。</summary>
    public struct LayerMask
    {
        public int value;

        public static implicit operator int(LayerMask m) => m.value;
        public static implicit operator LayerMask(int v) => new LayerMask { value = v };

        /// <summary>本宿主的世界层名 → 层号。层名契约见 `Core/CsConst.cs` 的 `PhysicsLayers`。</summary>
        public static int NameToLayer(string name) => LayerTable.LayerFromName(name);
        public static string LayerToName(int layer) => LayerTable.NameFromLayer(layer);
        public static int GetMask(params string[] names) => LayerTable.MaskFromNames(names);
        public override string ToString() => "LayerMask(" + value + ")";
    }

    /// <summary>射线交点：`point` / `normal` 是 CsMap 用到的全部成员。</summary>
    public struct RaycastHit
    {
        public Vector3 point;
        public Vector3 normal;
        public float distance;
        public Collider collider;
    }

    /// <summary>碰撞体（只需要"有名字、是不是 trigger"这一层）。</summary>
    public class Collider
    {
        public string name = "";
        public bool isTrigger;
        public override string ToString() => "Collider(" + name + (isTrigger ? ", trigger" : "") + ")";
    }

    /// <summary>trigger 查询口径（`QueryTriggerInteraction.Ignore` 是 CsMap 的调用口径）。</summary>
    public enum QueryTriggerInteraction
    {
        UseGlobal = 0,
        Ignore = 1,
        Collide = 2,
    }

    /// <summary>时钟（引擎 `LogThrottle` 用它做限频窗口；本宿主的判据不依赖真实时间）。</summary>
    public static class Time
    {
        private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
        public static float realtimeSinceStartup => (float)Clock.Elapsed.TotalSeconds;
        public static float time => realtimeSinceStartup;
        public static float deltaTime => 0f;
        public static float unscaledDeltaTime => 0f;
    }

    /// <summary>CsConst.cs 里只把这些当**类型**用（取值不参与物理判据）。</summary>
    public class Transform { }
    public class Camera { }
    public class GameObject
    {
        public string name = "";
    }

    /// <summary>层名表：与 `Core/CsConst.cs` 的 `PhysicsLayers` 对齐（同一套名字）。</summary>
    internal static class LayerTable
    {
        // 索引 = 层号；只登记本工程用到的世界/角色层，其余按 Unity 默认（无名字 ⇒ -1）。
        private static readonly string[] Names =
        {
            "Default", "TransparentFX", "Ignore Raycast", "", "Water", "UI", "", "", "CsWorld",
            "CsPlayer", "CsBot",
        };

        public static int LayerFromName(string name)
        {
            if (string.IsNullOrEmpty(name)) return -1;
            for (var i = 0; i < Names.Length; i++)
                if (Names[i] == name) return i;
            return -1;
        }

        public static string NameFromLayer(int layer)
            => layer >= 0 && layer < Names.Length ? Names[layer] : "";

        public static int MaskFromNames(string[] names)
        {
            var mask = 0;
            if (names == null) return 0;
            foreach (var n in names)
            {
                var l = LayerFromName(n);
                if (l >= 0) mask |= 1 << l;
            }
            return mask;
        }
    }
}
