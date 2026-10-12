// 枪口火焰贴片的加色着色器：逐字是 Unity 内置 Sprites/Default（SpriteVert / SampleSpriteTexture 同一套），
// 只把混合式从 `Blend One OneMinusSrcAlpha`（alpha 混合）改成 `Blend SrcAlpha One`（加色）。
//
// 出处（为什么必须是加色）：
//   原版四张枪口火焰载体 `sprites/muzzleflash{1,2,3,4}.spr` 的表头 `textureFormat` 全部 = 1
//   = **SPR_ADDITIVE**（`tools/probes/spr-extract.py` 对四张逐张读表头并断言
//   `filesize == 42 + 768 + 20*numFrames + sum(w*h)` 与 `radius == sqrt((w/2)^2+(h/2)^2)` 成立；
//   载体 = `原版资源/cs16src/cstrike/cstrike__sprites__muzzleflash{1,2,3,4}.spr`）。
//   ⇒ 原版是把这张图的亮度**加到背景上**（亮斑），不是当成一张覆盖片画上去。
//   本工程此前用 SpriteRenderer 自带的 `Sprites/Default`（alpha 混合）⇒ 贴片颜色与背景做插值，
//   亮斑在明亮地面/墙面上对比度被拉平（实测：单帧只多 ~150 个近白像素、肉眼不可辨）。
//
// 未逐字节取证的一点（如实登记）：引擎侧加色那一档的 `glBlendFunc` 具体实参本机未反汇编取证，
//   这里取 SpriteRenderer 语义下最标准的加色式 `Blend SrcAlpha One`（alpha 作权重；本回归的 PNG
//   核心 alpha=255、外圈 alpha=0 ⇒ 与 `Blend One One` 在核心区等价）。

Shader "Cs16/FxAdditive"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        [MaterialToggle] PixelSnap ("Pixel snap", Float) = 0
        [HideInInspector] _RendererColor ("RendererColor", Color) = (1,1,1,1)
        [HideInInspector] _Flip ("Flip", Vector) = (1,1,1,1)
        [PerRendererData] _AlphaTex ("External Alpha", 2D) = "white" {}
        [PerRendererData] _EnableExternalAlpha ("Enable External Alpha", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
            "RenderType" = "Transparent"
            "PreviewType" = "Plane"
            "CanUseSpriteAtlas" = "True"
        }

        Cull Off
        Lighting Off
        ZWrite Off
        Blend SrcAlpha One

        Pass
        {
            CGPROGRAM
            #pragma vertex SpriteVert
            #pragma fragment frag
            #pragma target 2.0
            #pragma multi_compile_instancing
            #pragma multi_compile_local _ PIXELSNAP_ON
            #pragma multi_compile _ ETC1_EXTERNAL_ALPHA
            #include "UnitySprites.cginc"

            fixed4 frag(v2f IN) : SV_Target
            {
                // 与 Sprites/Default 的唯一差别：**不**乘 alpha（加色由 Blend 式负责）。
                return SampleSpriteTexture(IN.texcoord) * IN.color;
            }
            ENDCG
        }
    }
}
