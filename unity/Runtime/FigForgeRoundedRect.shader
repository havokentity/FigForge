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
        _Radius ("Corner Radii px (tl,tr,br,bl)", Vector) = (0,0,0,0)
        _Size ("Rect Size (px)", Vector) = (100,100,0,0)
        _GradientDir ("Gradient Dir (xy, 0=off)", Vector) = (0,0,0,0)
        _Pad ("Mesh pad px (max of stroke outset + shadow reach)", Float) = 0
        _StrokeOutset ("Stroke outset px (0=inside, w/2=center, w=outside)", Float) = 0
        _ShadowColor ("Drop Shadow Color", Color) = (0,0,0,0)
        _ShadowOffset ("Shadow Offset px (xy)", Vector) = (0,0,0,0)
        _ShadowParams ("Shadow (x=blur, y=spread) px", Vector) = (0,0,0,0)

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

            fixed4 _FillColor, _Fill2, _BorderColor, _ShadowColor;
            float4 _Size, _GradientDir, _Radius, _ShadowOffset, _ShadowParams; // _Radius=(tl,tr,br,bl); _ShadowParams=(blur,spread)
            float _BorderWidth;
            float _Pad;         // total mesh half-pad (px): max(stroke outset, shadow reach)
            float _StrokeOutset; // how far the stroke extends OUTSIDE the fill edge (px)

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
                // The mesh is padded by _Pad on every side so an OUTSIDE/CENTER
                // stroke has geometry to draw on; map UV across that padded span
                // but keep the SDF box at the original rect size.
                float2 full = size + 2.0 * _Pad;
                float2 p = (i.uv - 0.5) * full;            // pixel coords from centre (+x right, +y up)
                // pick this quadrant's corner radius: tl=x, tr=y, br=z, bl=w
                float rad = (p.x > 0.0) ? (p.y > 0.0 ? _Radius.y : _Radius.z)
                                        : (p.y > 0.0 ? _Radius.x : _Radius.w);
                float d = sdRoundBox(p, size * 0.5, rad);  // d=0 at the FILL edge; d=_StrokeOutset at the stroke's outer edge
                float aa = max(fwidth(d), 1e-4);

                // ---- fill (optional linear gradient, mixed in sRGB to match Figma) ----
                fixed4 base = _FillColor;
                if (dot(_GradientDir.xy, _GradientDir.xy) > 1e-6)
                {
                    // Fill-relative coordinate so the gradient spans the FILL rect,
                    // independent of the mesh padding (stroke/shadow).
                    float t = saturate(dot(p / size, normalize(_GradientDir.xy)) + 0.5);
                    #ifdef UNITY_COLORSPACE_GAMMA
                        base = lerp(_FillColor, _Fill2, t);
                    #else
                        float3 ga = LinearToGammaSpace(_FillColor.rgb);
                        float3 gb = LinearToGammaSpace(_Fill2.rgb);
                        base.rgb = GammaToLinearSpace(lerp(ga, gb, t));
                        base.a = lerp(_FillColor.a, _Fill2.a, t);
                    #endif
                }

                // ---- shape colour (fill + optional stroke ring) ----
                fixed3 shapeRGB = base.rgb;
                float shapeFillA = base.a;
                if (_BorderWidth > 0.001)
                {
                    // stroke ring runs [_StrokeOutset - width, _StrokeOutset]:
                    // inside _StrokeOutset=0, center=w/2, outside=w (fill stays full size).
                    float inner = _StrokeOutset - _BorderWidth;
                    float inBorder = smoothstep(-aa, aa, d - inner);
                    shapeRGB = lerp(base.rgb, _BorderColor.rgb, inBorder);
                    shapeFillA = lerp(base.a, _BorderColor.a, inBorder);
                }
                float shapeCov = 1.0 - smoothstep(-aa, aa, d - _StrokeOutset); // AA edge at the outer stroke edge
                float shapeA = shapeFillA * shapeCov;

                // ---- drop shadow (soft, offset, spread silhouette BEHIND the shape) ----
                float shadowA = 0.0;
                if (_ShadowColor.a > 0.001)
                {
                    float ds = sdRoundBox(p - _ShadowOffset.xy, size * 0.5, rad); // cast by the fill silhouette
                    float spread = _ShadowParams.y;
                    float bl = max(_ShadowParams.x, aa);                          // blur radius (>= AA)
                    float scov = 1.0 - smoothstep(spread - bl, spread + bl, ds);
                    shadowA = _ShadowColor.a * scov;
                }

                // ---- composite shadow UNDER shape (straight alpha) ----
                float outA = shapeA + shadowA * (1.0 - shapeA);
                fixed3 outRGB = (shapeRGB * shapeA + _ShadowColor.rgb * shadowA * (1.0 - shapeA)) / max(outA, 1e-4);
                fixed4 outc = fixed4(outRGB, outA) * i.color;     // CanvasRenderer tint

                // With a shadow, keep the (larger) shadow footprint; otherwise clip to
                // the shape so this graphic can still serve as a rounded UGUI Mask.
                float clipv = (_ShadowColor.a > 0.001) ? outA : shapeCov;
                clip(clipv - 0.001);
                return outc;
            }
            ENDCG
        }
    }
}
