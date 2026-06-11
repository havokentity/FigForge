// =============================================================================
// FigForge — procedural rounded-rect UI shader (SDF). Resolution-independent:
// the rounded corners, border and fill are computed per-pixel from a signed
// distance field, so a button stays razor-crisp at any size/zoom and matches the
// Figma vector exactly — no exported PNG, no 9-slice. Driven by FigForgeRoundedRect.
// Per-rect values live in UI vertex channels so all instances can share one material.
// =============================================================================
Shader "FigForge/RoundedRect"
{
    Properties
    {
        [PerRendererData] _MainTex ("Gradient", 2D) = "white" {}
        _FillColor ("Fill", Color) = (1,1,1,1)
        _StrokeColor ("Stroke", Color) = (0,0,0,0)
        _StrokeWidth ("Stroke Weight (px)", Float) = 0
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
        _ClipRect ("Clip Rect", Vector) = (-32767, -32767, 32767, 32767)
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
            // RectMask2D support: without this keyword + UnityGet2DClipping the SDF
            // graphics silently IGNORE the mask (rows drew outside the list viewport).
            #pragma multi_compile __ UNITY_UI_CLIP_RECT
            #include "UnityCG.cginc"
            #include "UnityUI.cginc"

            struct appdata
            {
                float4 vertex:POSITION;
                fixed4 color:COLOR;
                float4 uv0:TEXCOORD0; // xy=quad uv, zw=fill packed RG/BA
                float4 uv1:TEXCOORD1; // x=gradient kind, y=stroke uses gradient, zw=stroke packed RG/BA
                float4 uv2:TEXCOORD2; // xy=shadow packed RG/BA, zw=size
                float4 uv3:TEXCOORD3; // x=gradient dir packed, yzw=radius tl/tr/br
                float3 normal:NORMAL; // x=radius bl, y=stroke weight, z=stroke outset
                float4 tangent:TANGENT; // xy=shadow offset, z=shadow blur, w=shadow spread
            };

            struct v2f
            {
                float4 pos:SV_POSITION;
                fixed4 color:COLOR;
                float4 uv0:TEXCOORD0;
                float4 uv1:TEXCOORD1;
                float4 uv2:TEXCOORD2;
                float4 uv3:TEXCOORD3;
                float3 normal:TEXCOORD4;
                float4 tangent:TEXCOORD5;
                float4 worldPosition:TEXCOORD6; // canvas-space pos for RectMask2D clipping
            };

            float4 _ClipRect;

            v2f vert(appdata v)
            {
                v2f o;
                o.worldPosition = v.vertex;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.color = v.color;
                o.uv0 = v.uv0;
                o.uv1 = v.uv1;
                o.uv2 = v.uv2;
                o.uv3 = v.uv3;
                o.normal = v.normal;
                o.tangent = v.tangent;
                return o;
            }

            float2 unpackBytes(float v)
            {
                float n = floor(saturate(v) * 65535.0 + 0.5);
                float hi = floor(n / 256.0);
                float lo = n - hi * 256.0;
                return float2(hi, lo) / 255.0;
            }

            float4 unpackColor(float2 rgBa)
            {
                float2 rg = unpackBytes(rgBa.x);
                float2 ba = unpackBytes(rgBa.y);
                float4 c = float4(rg.x, rg.y, ba.x, ba.y);
                #ifndef UNITY_COLORSPACE_GAMMA
                    c.rgb = GammaToLinearSpace(c.rgb);
                #endif
                return c;
            }

            float2 unpackGradientDir(float packed)
            {
                if (packed <= 0.0) return float2(0.0, 0.0);
                float n = floor(saturate(packed) * 65536.0 + 0.5) - 1.0;
                float x = floor(n / 256.0);
                float y = n - x * 256.0;
                return float2(x, y) / 255.0 * 2.0 - 1.0;
            }

            sampler2D _MainTex;

            float gradientT(float kind, float2 p, float2 size, float2 dir)
            {
                float2 q = p / max(size, float2(1,1));
                if (kind < 1.5)
                {
                    return saturate(dot(q, normalize(dir)) + 0.5);
                }
                if (kind < 2.5)
                {
                    return saturate(length(q * 2.0));
                }
                if (kind < 3.5)
                {
                    float angle = atan2(q.y, q.x);
                    if (dot(dir, dir) > 1e-4) angle -= atan2(dir.y, dir.x);
                    return frac(angle / 6.28318530718 + 0.5);
                }
                float2 axis = dot(dir, dir) > 1e-4 ? normalize(dir) : float2(1.0, 0.0);
                float2 perp = float2(-axis.y, axis.x);
                return saturate(abs(dot(q * 2.0, axis)) + abs(dot(q * 2.0, perp)));
            }

            float meshPad(float4 shadowColor, float2 shadowOffset, float shadowBlur, float shadowSpread, float strokeOutset)
            {
                float shadowReach = shadowColor.a <= 0.001 ? 0.0
                    : max(abs(shadowOffset.x), abs(shadowOffset.y)) + 1.75 * max(0.0, shadowBlur) + max(0.0, shadowSpread) + 1.0;
                return max(max(0.0, strokeOutset), shadowReach);
            }

            // signed distance to a rounded box centred at origin, half-size b, corner radius r
            float sdRoundBox(float2 p, float2 b, float r)
            {
                r = min(r, min(b.x, b.y));
                float2 q = abs(p) - b + r;
                return min(max(q.x, q.y), 0.0) + length(max(q, 0.0)) - r;
            }

            float roundRectPerimeterT(float2 p, float2 halfSize, float r)
            {
                r = clamp(r, 0.0, min(halfSize.x, halfSize.y));
                float2 straight = max(halfSize - r, float2(0.0, 0.0));
                float top = 2.0 * straight.x;
                float side = 2.0 * straight.y;
                float arc = 1.57079632679 * r;
                float beforeRight = top + arc;
                float beforeBottom = beforeRight + side + arc;
                float beforeLeft = beforeBottom + top + arc;

                if (r <= 0.001)
                {
                    if (p.y >= straight.y && abs(p.x) <= straight.x) return p.x + straight.x;
                    if (p.x >= straight.x && abs(p.y) <= straight.y) return top + (straight.y - p.y);
                    if (p.y <= -straight.y && abs(p.x) <= straight.x) return top + side + (straight.x - p.x);
                    return top + side + top + (p.y + straight.y);
                }

                if (p.y >= straight.y && abs(p.x) <= straight.x) return p.x + straight.x;
                if (p.x > straight.x && p.y > straight.y)
                {
                    float a = atan2(p.y - straight.y, p.x - straight.x);
                    return top + (1.57079632679 - a) * r;
                }
                if (p.x >= straight.x && abs(p.y) <= straight.y) return beforeRight + (straight.y - p.y);
                if (p.x > straight.x && p.y < -straight.y)
                {
                    float a = atan2(p.y + straight.y, p.x - straight.x);
                    return beforeRight + side + (-a) * r;
                }
                if (p.y <= -straight.y && abs(p.x) <= straight.x) return beforeBottom + (straight.x - p.x);
                if (p.x < -straight.x && p.y < -straight.y)
                {
                    float a = atan2(p.y + straight.y, p.x + straight.x);
                    return beforeBottom + top + ((-1.57079632679) - a) * r;
                }
                if (p.x <= -straight.x && abs(p.y) <= straight.y) return beforeLeft + (p.y + straight.y);

                float a2 = atan2(p.y - straight.y, p.x + straight.x);
                return beforeLeft + side + (3.14159265359 - a2) * r;
            }

            float strokeDashMask(float2 p, float2 size, float rad, float strokeWidth, float strokeOutset, float aa)
            {
                float dash = max(1.0, strokeWidth * 3.0);
                float period = dash * 2.0;
                float centerOffset = strokeOutset - strokeWidth * 0.5;
                float2 centerHalf = max(float2(1.0, 1.0), size * 0.5 + centerOffset);
                float centerRad = clamp(rad + centerOffset, 0.0, min(centerHalf.x, centerHalf.y));
                float s = roundRectPerimeterT(p, centerHalf, centerRad);
                float phase = s - floor(s / period) * period;
                float edge = max(aa, fwidth(s));
                return saturate(1.0 - smoothstep(dash - edge, dash + edge, phase));
            }

            // erf approximation (Abramowitz & Stegun 7.1.26, ~1e-7 max error) — used
            // for a true Gaussian drop-shadow falloff that matches Figma.
            float erfApprox(float x)
            {
                float s = sign(x); x = abs(x);
                float t = 1.0 / (1.0 + 0.3275911 * x);
                float y = 1.0 - (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * exp(-x * x);
                return s * y;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float4 fillColor = unpackColor(i.uv0.zw);
                float gradientKind = i.uv1.x;
                float strokeGradient = i.uv1.y;
                float4 strokeColor = unpackColor(i.uv1.zw);
                float4 shadowColor = unpackColor(i.uv2.xy);
                float2 size = max(i.uv2.zw, float2(1,1));
                float2 gradientDir = unpackGradientDir(i.uv3.x);
                float4 radius = float4(i.uv3.y, i.uv3.z, i.uv3.w, i.normal.x);
                float strokeWidth = abs(i.normal.y);
                float dashedStroke = i.normal.y < -0.001 ? 1.0 : 0.0;
                float strokeOutset = i.normal.z;
                float2 shadowOffset = i.tangent.xy * float2(1.0, -1.0);
                float shadowBlur = i.tangent.z;
                float shadowSpread = i.tangent.w;
                float pad = meshPad(shadowColor, shadowOffset, shadowBlur, shadowSpread, strokeOutset);

                // The mesh is padded on every side so an OUTSIDE/CENTER
                // stroke has geometry to draw on; map UV across that padded span
                // but keep the SDF box at the original rect size.
                float2 full = size + 2.0 * pad;
                float2 p = (i.uv0.xy - 0.5) * full;         // pixel coords from centre (+x right, +y up)
                // pick this quadrant's corner radius: tl=x, tr=y, br=z, bl=w
                float rad = (p.x > 0.0) ? (p.y > 0.0 ? radius.y : radius.z)
                                        : (p.y > 0.0 ? radius.x : radius.w);
                float d = sdRoundBox(p, size * 0.5, rad);  // d=0 at the FILL edge; d=strokeOutset at the stroke's outer edge
                float aa = max(fwidth(d), 1e-4);

                // ---- fill (optional gradient texture generated from UnityEngine.Gradient) ----
                fixed4 fillBase = fillColor;
                if (gradientKind > 0.5)
                {
                    // Fill-relative coordinate so the gradient spans the FILL rect,
                    // independent of the mesh padding (stroke/shadow).
                    float t = gradientT(gradientKind, p, size, gradientDir.xy);
                    fillBase = tex2D(_MainTex, float2(t, 0.5));
                }
                fixed4 base = (strokeGradient > 0.5) ? fixed4(fillBase.rgb, 0.0) : fillBase;
                fixed4 strokeBase = (strokeGradient > 0.5) ? fillBase : strokeColor;

                // ---- shape colour (fill + optional stroke ring) ----
                float fillCov = 1.0 - smoothstep(-aa, aa, d);
                fixed4 shape = fixed4(base.rgb, base.a * fillCov);
                float shapeCov = fillCov;
                if (strokeWidth > 0.001)
                {
                    // stroke ring runs [strokeOutset - width, strokeOutset]:
                    // inside strokeOutset=0, center=w/2, outside=w (fill stays full size).
                    float inner = strokeOutset - strokeWidth;
                    float inStroke = smoothstep(-aa * 2.0, 0.0, d - inner);
                    float outerCov = 1.0 - smoothstep(-aa, aa, d - strokeOutset);
                    float dashMask = dashedStroke > 0.5 ? strokeDashMask(p, size, rad, strokeWidth, strokeOutset, aa) : 1.0;
                    float strokeCov = outerCov * inStroke * dashMask;
                    float srcA = strokeBase.a * strokeCov;
                    float keepShape = shapeCov > 1e-4 ? saturate((shapeCov - srcA) / shapeCov) : 0.0;
                    float dstA = shape.a * keepShape;
                    float outA = srcA + dstA;
                    fixed3 outRGB = (strokeBase.rgb * srcA + shape.rgb * dstA) / max(outA, 1e-4);
                    shape = fixed4(outRGB, outA);
                    shapeCov = max(shapeCov, strokeCov);
                }
                fixed3 shapeRGB = shape.rgb;
                float shapeA = shape.a;

                // ---- drop shadow (Gaussian silhouette BEHIND the shape, matches Figma) ----
                // The shadow is the rendered silhouette (offset, expanded by spread) blurred by
                // a Gaussian. Figma's "Blur" B maps to std dev sigma = B/2; coverage at a
                // point is the Gaussian CDF of its signed distance to the expanded edge:
                // scov = 0.5 * erfc((d - spread) / (sigma*sqrt2)). True Gaussian tail, not a
                // linear ramp — so the softness reads the same as Figma.
                float shadowA = 0.0;
                if (shadowColor.a > 0.001)
                {
                    float ds = sdRoundBox(p - shadowOffset.xy, size * 0.5, rad) - strokeOutset;  // cast by the outer rendered silhouette
                    float spread = shadowSpread;
                    float sigma = max(shadowBlur * 0.5, aa);                     // Figma blur → std dev (>= AA)
                    float x = (ds - spread) / (sigma * 1.41421356);              // distance from expanded edge, in sqrt2*sigma
                    float scov = 0.5 * (1.0 - erfApprox(x));                      // Gaussian CDF (1 inside → 0 outside)
                    shadowA = shadowColor.a * saturate(scov);
                    #ifndef UNITY_COLORSPACE_GAMMA
                        // Figma composites the shadow in sRGB, but a Linear project blends
                        // it over the background in LINEAR space — which makes a dark shadow
                        // read too light. Pre-warp the alpha so the linear blend matches the
                        // sRGB result (bg term cancels under the ~2.2 power curve → exact for
                        // a black shadow, close for tinted ones).
                        shadowA = 1.0 - pow(1.0 - shadowA, 2.2);
                    #endif
                }
                // Figma's showShadowBehindNode default (false): the shadow is erased
                // under the shape's GEOMETRY (incl. stroke outset), so translucent
                // fills never reveal their own shadow.
                float geomCov = 1.0 - smoothstep(-aa, aa, d - strokeOutset);
                shadowA *= 1.0 - geomCov;

                // ---- composite shadow UNDER shape (straight alpha) ----
                float outA = shapeA + shadowA * (1.0 - shapeA);
                fixed3 outRGB = (shapeRGB * shapeA + shadowColor.rgb * shadowA * (1.0 - shapeA)) / max(outA, 1e-4);
                fixed4 outc = fixed4(outRGB, outA) * i.color;     // CanvasRenderer tint

                // With a shadow, keep the (larger) shadow footprint; otherwise clip to
                // the shape so this graphic can still serve as a rounded UGUI Mask.
                float clipv = (shadowColor.a > 0.001) ? outA : shapeCov;
                #ifdef UNITY_UI_CLIP_RECT
                float maskf = UnityGet2DClipping(i.worldPosition.xy, _ClipRect);
                outc *= maskf;
                clipv *= maskf;
                #endif
                clip(clipv - 0.001);
                return outc;
            }
            ENDCG
        }
    }
}
