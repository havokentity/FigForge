// Presents a cached FigForge layer surface with a TRUE Figma blend mode by reading
// the backdrop via GrabPass (Built-in only) and compositing coverage-aware. This is
// what makes destination-reading modes (Darken, Overlay, Soft Light, Difference, …)
// render correctly per-graphic — soft shadows stay soft — without the page compositor.
Shader "FigForge/CachedBlend"
{
    Properties
    {
        [PerRendererData] _MainTex ("Cached Surface", 2D) = "white" {}
        _AppearanceOpacity ("Appearance Opacity", Float) = 1
        _BlendMode ("Figma Blend Mode", Float) = 1

        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255
        _ColorMask ("Color Mask", Float) = 15
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" "PreviewType"="Plane" "CanUseSpriteAtlas"="True" }
        Stencil { Ref [_Stencil] Comp [_StencilComp] Pass [_StencilOp] ReadMask [_StencilReadMask] WriteMask [_StencilWriteMask] }
        Cull Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        ColorMask [_ColorMask]

        // Capture everything drawn behind this graphic. Unnamed = per-draw grab, which
        // correctly composites a blend rect stacked behind another blend rect. The proper
        // fix for the per-draw fillrate cost is the page compositor (single offscreen
        // composite), not a named/shared GrabPass (which would drop that stacking).
        GrabPass { }

        Pass
        {
            Blend One Zero // we composite the backdrop ourselves, then replace

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            #include "FigForgeBlend.cginc"

            struct appdata { float4 vertex : POSITION; fixed4 color : COLOR; float2 uv0 : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; fixed4 color : COLOR; float2 uv : TEXCOORD0; float4 grabUV : TEXCOORD1; };

            sampler2D _MainTex;
            sampler2D _GrabTexture;
            float _AppearanceOpacity;
            float _BlendMode;

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.color = v.color;
                o.uv = v.uv0;
                o.grabUV = ComputeGrabScreenPos(o.pos);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 surf = tex2D(_MainTex, i.uv);                 // premultiplied layer
                float cov = surf.a * saturate(_AppearanceOpacity) * i.color.a;
                fixed3 layer = surf.rgb / max(surf.a, 1e-4);         // -> straight colour
                layer *= i.color.rgb;
                fixed3 backdrop = tex2Dproj(_GrabTexture, i.grabUV).rgb;
                fixed3 blended = figmaBlendRgb(layer, backdrop, _BlendMode);
                fixed3 outRgb = lerp(backdrop, blended, saturate(cov)); // coverage-aware
                return fixed4(outRgb, 1.0);
            }
            ENDCG
        }
    }
}
