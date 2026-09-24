using Cs16.Core;
using UnityEngine;

namespace Cs16.Module.Match
{
    /// <summary>
    /// 挂在角色受击体上的代理：射线命中它即可反查到 actor 与命中部位。
    /// 由视图层（Module/View）在生成角色模型时挂载（<c>ArtSetup</c> 生成 <c>player.prefab</c> 时挂 4 个）。
    ///
    /// <para><b>不许把这个类挪回别的文件里</b>（历史上它曾在 <c>ICsMatch.cs</c> 里）：
    /// Unity 的硬规则是 <b>MonoBehaviour 的类名必须与所在文件名一致</b>，否则该脚本「无法挂载」——
    /// <c>AddComponent</c> 当时看起来没报错，但 <c>SaveAsPrefabAsset</c> 之后组件的
    /// <c>m_Script</c> 会写成 <c>{fileID: 0}</c>（脚本引用为空），预制体里等于没有这个组件。
    /// 实测代价：角色一个受击体都不剩 ⇒ 射线打不到任何人 ⇒ 机器人与玩家**双方都打不中**。</para>
    /// </summary>
    public sealed class CsHitboxProxy : MonoBehaviour
    {
        public long ActorId;
        public CsHitbox Hitbox;
    }
}
