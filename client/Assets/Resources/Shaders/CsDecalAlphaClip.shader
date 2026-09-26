// 墙上的弹痕贴花用的精灵着色器：**逐字**是 Unity 内置 Sprites/Default（顶点走它自己的
// SpriteVert / SampleSpriteTexture，混合式同一行），只多一步 alpha 裁切 clip(c.a - _Cutoff)。
//
// 出处（原版 hw.dll 的贴花绘制函数 RVA 0x57800..0x579E7，消费贴花列表 [0x2382730] 的唯一函数）：
//   0x0057822  push 0x0BC0 / call glEnable      ; glEnable(GL_ALPHA_TEST)
//   0x005782D  push 0x0303 / 0x0057832 push 0x0302 / 0x0057837 call glBlendFunc
//              ; glBlendFunc(GL_SRC_ALPHA, GL_ONE_MINUS_SRC_ALPHA) —— 与 Sprites/Default 同式
// 阈值不设在该函数体内，继承引擎上一次 glAlphaFunc：
//   0x00549F8  mov ecx,[0x1E76D54] / 0x00549FF push 0x0204 / 0x0054A04 call glAlphaFunc
//              ; glAlphaFunc(GL_GREATER, gl_alphamin)；cvar gl_alphamin 默认 "0.25"
//   （cvar 结构 VA 0x01E76D48、值 VA 0x01E76D54）
// 即 alpha <= 0.25 的软晕被丢弃、只画硬核。

Shader "Cs16/DecalAlphaClip"
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
        _Cutoff ("Alpha Cutoff", Range(0,1)) = 0.25
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
        Blend One OneMinusSrcAlpha

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

            float _Cutoff;

            fixed4 frag(v2f IN) : SV_Target
            {
                fixed4 c = SampleSpriteTexture(IN.texcoord) * IN.color;
                clip(c.a - _Cutoff);
                c.rgb *= c.a;
                return c;
            }
            ENDCG
        }
    }
}
