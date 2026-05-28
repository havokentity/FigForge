// =============================================================================
// FigForge — procedural rounded-rect UI shader (SDF). Resolution-independent:
// the rounded corners, border and fill are computed per-pixel from a signed
// distance field, so a button stays razor-crisp at any size/zoom and matches the
// Figma vector exactly — no exported PNG, no 9-slice. Driven by FigForgeRoundedRect.
// =============================================================================
Shader "FigForge/RoundedRect"
{
    Properties
    {
        [PerRendererData] _MainTex ("Texture", 2D) = "white" {}
        _FillColor ("Fill", Color) = (1,1,1,1)
        _Fill2 ("Fill 2 (gradient)", Color) = (1,1,1,1)
        _BorderColor ("Border", Color) = (0,0,0,0)
        _BorderWidth ("Border Width (px)", Float) = 0
        _Radius ("Corner Radius (px)", Float) = 0
        _Size ("Rect Size (px)", Vector) = (100,100,0,0)
        _GradientDir ("Gradient Dir (xy, 0=off)", Vector) = (0,0,0,0)

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
        Blend SrcAlpha OneMinusSrcAlpha
        ColorMask [_ColorMask]

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata { float4 vertex:POSITION; float4 color:COLOR; float2 uv:TEXCOORD0; };
            struct v2f { float4 pos:SV_POSITION; fixed4 color:COLOR; float2 uv:TEXCOORD0; };

            fixed4 _FillColor, _Fill2, _BorderColor;
            float4 _Size, _GradientDir;
            float _BorderWidth, _Radius;

            v2f vert(appdata v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.color = v.color; o.uv = v.uv; return o; }

            // signed distance to a rounded box centred at origin, half-size b, corner radius r
            float sdRoundBox(float2 p, float2 b, float r)
            {
                r = min(r, min(b.x, b.y));
                float2 q = abs(p) - b + r;
                return min(max(q.x, q.y), 0.0) + length(max(q, 0.0)) - r;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float2 size = max(_Size.xy, float2(1,1));
                float2 p = (i.uv - 0.5) * size;            // pixel coords from centre
                float d = sdRoundBox(p, size * 0.5, _Radius);
                float aa = max(fwidth(d), 1e-4);

                // base fill (optional linear gradient)
                fixed4 base = _FillColor;
                if (dot(_GradientDir.xy, _GradientDir.xy) > 1e-6)
                {
                    float t = saturate(dot(i.uv - 0.5, normalize(_GradientDir.xy)) + 0.5);
                    base = lerp(_FillColor, _Fill2, t);
                }

                fixed4 col = base;
                if (_BorderWidth > 0.001)
                {
                    float inBorder = smoothstep(-aa, aa, d + _BorderWidth); // 0 centre → 1 border ring
                    col.rgb = lerp(base.rgb, _BorderColor.rgb, inBorder);
                    col.a = lerp(base.a, _BorderColor.a, inBorder);
                }

                float coverage = 1.0 - smoothstep(-aa, aa, d);   // crisp AA edge
                col *= i.color;                                  // CanvasRenderer tint
                col.a *= coverage;
                return col;
            }
            ENDCG
        }
    }
}
