using Cs16.Core;
using UnityEditor;
using UnityEngine;

namespace Cs16.EditorTools
{
    /// <summary>
    /// 把 <see cref="PhysicsLayers"/> 的三个层名落进 <c>ProjectSettings/TagManager.asset</c>。
    ///
    /// <para><b>为什么必须有这个脚本（实测缺陷）</b>：<c>Core/CsConst.cs</c> 的
    /// <see cref="PhysicsLayers"/> 注释写着"需与 ProjectSettings/TagManager 对齐，**由 Editor 脚本保证**"，
    /// 但工程里**从来没有这个脚本** —— 实测 <c>TagManager.asset</c> 的 <c>layers:</c> 只有
    /// <c>Default / TransparentFX / Ignore Raycast / Water / UI</c>。后果链条（每步都实测过）：</para>
    /// <list type="number">
    /// <item><c>LayerMask.NameToLayer("CsWorld")</c> 恒为 <b>-1</b>；</item>
    /// <item><c>CsMap.GroundMask()</c> 取不到世界层 ⇒ 退化成"全层"，只按**层号**排除角色；</item>
    /// <item>贴地射线于是打到角色**自己的命中盒**上，把命中点当成地面；</item>
    /// <item>命中点随角色一起上升 ⇒ 正反馈抬升：实测同一 actor 的 y 从 <c>-3</c> 一路涨到 <c>500+</c>
    /// （每 0.05s 被抬一次）。</item>
    /// </list>
    ///
    /// <para><b>幂等</b>：已是同名层 → 只读校验层号；层号被别的名字占用 → 报 <c>Error</c> 且
    /// **不覆盖**（覆盖会静默改掉别的系统的碰撞语义）；同名层被放在别的层号 → 报 <c>Error</c>
    /// 并指出实际层号（运行期按 <see cref="PhysicsLayers"/> 的常量索引取层，层号漂了就说不通）。</para>
    ///
    /// <para>入口：菜单 <b>Clover/CS16/确保物理层</b>；<c>ArtSetup.Generate()</c> 也会先调它，
    /// 这样"由 Editor 脚本保证"这句话才真的成立。</para>
    /// </summary>
    public static class PhysicsLayerSetup
    {
        private const string Tag = "[PhysicsLayer]";

        /// <summary>TagManager 资产的固定路径（Unity 的 ProjectSettings 只此一处）。</summary>
        private const string TagManagerPath = "ProjectSettings/TagManager.asset";

        [MenuItem("Clover/CS16/确保物理层", false, 32)]
        public static void EnsureAllFromMenu() => EnsureAll();

        /// <summary>
        /// 校验并补齐 <c>CsPlayer / CsBot / CsWorld</c> 三层。
        /// 返回 <c>true</c> = 三个层在约定层号上都有（可继续生成资源）；<c>false</c> = 有冲突（见 Console 的 Error）。
        /// </summary>
        public static bool EnsureAll()
        {
            var assets = AssetDatabase.LoadAllAssetsAtPath(TagManagerPath);
            if (assets == null || assets.Length == 0)
            {
                Debug.LogError($"{Tag} 打不开 {TagManagerPath} —— 物理层无法建立（所有按层走的射线都会错）");
                return false;
            }

            var so = new SerializedObject(assets[0]);
            var layers = so.FindProperty("layers");
            if (layers == null || !layers.isArray)
            {
                Debug.LogError($"{Tag} {TagManagerPath} 里没有 layers 数组属性 —— 物理层无法建立");
                return false;
            }

            var ok = true;
            var changed = 0;
            Ensure(layers, PhysicsLayers.Player, PhysicsLayers.PlayerName, ref ok, ref changed);
            Ensure(layers, PhysicsLayers.Bot, PhysicsLayers.BotName, ref ok, ref changed);
            Ensure(layers, PhysicsLayers.World, PhysicsLayers.WorldName, ref ok, ref changed);

            if (changed > 0)
            {
                so.ApplyModifiedProperties();
                AssetDatabase.SaveAssets();
            }

            // 断言必须问**运行期的解析结果**（LayerMask.NameToLayer），不是我们刚写进去的字符串 ——
            // 写进 SerializedProperty 和"引擎真的认这个名字"是两件事。
            AssertResolved(PhysicsLayers.PlayerName, PhysicsLayers.Player, ref ok);
            AssertResolved(PhysicsLayers.BotName, PhysicsLayers.Bot, ref ok);
            AssertResolved(PhysicsLayers.WorldName, PhysicsLayers.World, ref ok);

            if (ok)
                Debug.Log($"{Tag} 物理层就绪：{PhysicsLayers.PlayerName}={PhysicsLayers.Player} / " +
                          $"{PhysicsLayers.BotName}={PhysicsLayers.Bot} / {PhysicsLayers.WorldName}={PhysicsLayers.World}" +
                          $"（本次新增 {changed} 个）");
            return ok;
        }

        /// <summary>让一个层名落在指定层号上；冲突时报 Error 并不动它。</summary>
        private static bool Ensure(SerializedProperty layers, int index, string name,
            ref bool ok, ref int changed)
        {
            if (index <= 0 || index >= layers.arraySize)
            {
                Debug.LogError($"{Tag} 「{name}」的约定层号 {index} 超出 layers 范围（0..{layers.arraySize - 1}）—— " +
                               "层号是契约（CsConst.PhysicsLayers 里的常量），不能在编辑器里挪");
                ok = false;
                return false;
            }

            var slot = layers.GetArrayElementAtIndex(index);
            var current = slot.stringValue;
            if (current == name) return true;                    // 已就位（幂等）

            if (!string.IsNullOrEmpty(current))
            {
                Debug.LogError($"{Tag} 层 {index} 已被「{current}」占用，无法放「{name}」—— " +
                               "不覆盖别人的层（覆盖会静默改掉别的系统的碰撞语义）。" +
                               "请腾出层号或改 CsConst.PhysicsLayers 的约定，两边不许各写一个数");
                ok = false;
                return false;
            }

            var elsewhere = Find(layers, name);
            if (elsewhere >= 0)
            {
                Debug.LogError($"{Tag} 「{name}」已经存在，但在层 {elsewhere}（约定是 {index}）—— " +
                               "运行期按物理层常量索引取层，层号不一致等于没有这个层");
                ok = false;
                return false;
            }

            slot.stringValue = name;
            changed++;
            Debug.Log($"{Tag} 层 {index} ← 「{name}」");
            return true;
        }

        private static int Find(SerializedProperty layers, string name)
        {
            for (var i = 0; i < layers.arraySize; i++)
            {
                if (layers.GetArrayElementAtIndex(i).stringValue == name) return i;
            }
            return -1;
        }

        private static void AssertResolved(string name, int expected, ref bool ok)
        {
            var got = LayerMask.NameToLayer(name);
            if (got == expected) return;
            Debug.LogError($"{Tag} 断言失败：LayerMask.NameToLayer(\"{name}\") = {got}，期望 {expected} —— " +
                           "按层走的射线（贴地/子弹/视线）会走错分支");
            ok = false;
        }
    }
}
