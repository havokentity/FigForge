// =============================================================================
// FigForge — one-pass layered rounded rect. Supports up to four stroke paint
// layers (solid or gradient) composited in Figma order over a resolved fill.
// =============================================================================
Shader "FigForge/LayeredRect4"
{
    Properties
    {
        [PerRendererData] _MainTex ("Paint Gradient Rows", 2D) = "white" {}
        _FillCount ("Fill Count", Float) = 0
        _FillFlags ("Fill Uses Gradient", Vector) = (0,0,0,0)
        _FillKind ("Fill Gradient Kinds", Vector) = (0,0,0,0)
        _FillColor0 ("Fill 0", Color) = (0,0,0,0)
        _FillColor1 ("Fill 1", Color) = (0,0,0,0)
        _FillColor2 ("Fill 2", Color) = (0,0,0,0)
        _FillColor3 ("Fill 3", Color) = (0,0,0,0)
        _FillDir0 ("Fill Dir 0", Vector) = (1,0,0,0)
        _FillDir1 ("Fill Dir 1", Vector) = (1,0,0,0)
        _FillDir2 ("Fill Dir 2", Vector) = (1,0,0,0)
        _FillDir3 ("Fill Dir 3", Vector) = (1,0,0,0)
        _StrokeCount ("Stroke Count", Float) = 0
        _StrokeFlags ("Stroke Uses Gradient", Vector) = (0,0,0,0)
        _StrokeKind ("Stroke Gradient Kinds", Vector) = (0,0,0,0)
        _StrokeColor0 ("Stroke 0", Color) = (0,0,0,0)
        _StrokeColor1 ("Stroke 1", Color) = (0,0,0,0)
        _StrokeColor2 ("Stroke 2", Color) = (0,0,0,0)
        _StrokeColor3 ("Stroke 3", Color) = (0,0,0,0)
        _StrokeDir0 ("Stroke Dir 0", Vector) = (1,0,0,0)
        _StrokeDir1 ("Stroke Dir 1", Vector) = (1,0,0,0)
        _StrokeDir2 ("Stroke Dir 2", Vector) = (1,0,0,0)
        _StrokeDir3 ("Stroke Dir 3", Vector) = (1,0,0,0)
        _StrokeWidth ("Stroke Weight (px)", Float) = 0
        _StrokeOutset ("Stroke outset px", Float) = 0
        _StrokeDash ("Stroke Dash (enabled,dash,gap,offset)", Vector) = (0,0,0,0)
        _Radius ("Corner Radii px (tl,tr,br,bl)", Vector) = (0,0,0,0)
        _Size ("Rect Size (px)", Vector) = (100,100,0,0)
        _AppearanceOpacity ("Appearance Opacity", Float) = 1
        _ShadowCount ("Shadow Count", Float) = 0
        _ShadowColor0 ("Drop Shadow 0 Color", Color) = (0,0,0,0)
        _ShadowColor1 ("Drop Shadow 1 Color", Color) = (0,0,0,0)
        _ShadowColor2 ("Drop Shadow 2 Color", Color) = (0,0,0,0)
        _ShadowColor3 ("Drop Shadow 3 Color", Color) = (0,0,0,0)
        _ShadowOffset0 ("Shadow 0 Offset px (xy)", Vector) = (0,0,0,0)
        _ShadowOffset1 ("Shadow 1 Offset px (xy)", Vector) = (0,0,0,0)
        _ShadowOffset2 ("Shadow 2 Offset px (xy)", Vector) = (0,0,0,0)
        _ShadowOffset3 ("Shadow 3 Offset px (xy)", Vector) = (0,0,0,0)
        _ShadowParams0 ("Shadow 0 (x=blur, y=spread) px", Vector) = (0,0,0,0)
        _ShadowParams1 ("Shadow 1 (x=blur, y=spread) px", Vector) = (0,0,0,0)
        _ShadowParams2 ("Shadow 2 (x=blur, y=spread) px", Vector) = (0,0,0,0)
        _ShadowParams3 ("Shadow 3 (x=blur, y=spread) px", Vector) = (0,0,0,0)
        _InnerShadowCount ("Inner Shadow Count", Float) = 0
        _InnerShadowColor0 ("Inner Shadow 0 Color", Color) = (0,0,0,0)
        _InnerShadowColor1 ("Inner Shadow 1 Color", Color) = (0,0,0,0)
        _InnerShadowColor2 ("Inner Shadow 2 Color", Color) = (0,0,0,0)
        _InnerShadowColor3 ("Inner Shadow 3 Color", Color) = (0,0,0,0)
        _InnerShadowOffset0 ("Inner Shadow 0 Offset px (xy)", Vector) = (0,0,0,0)
        _InnerShadowOffset1 ("Inner Shadow 1 Offset px (xy)", Vector) = (0,0,0,0)
        _InnerShadowOffset2 ("Inner Shadow 2 Offset px (xy)", Vector) = (0,0,0,0)
        _InnerShadowOffset3 ("Inner Shadow 3 Offset px (xy)", Vector) = (0,0,0,0)
        _InnerShadowParams0 ("Inner Shadow 0 (x=blur, y=spread) px", Vector) = (0,0,0,0)
        _InnerShadowParams1 ("Inner Shadow 1 (x=blur, y=spread) px", Vector) = (0,0,0,0)
        _InnerShadowParams2 ("Inner Shadow 2 (x=blur, y=spread) px", Vector) = (0,0,0,0)
        _InnerShadowParams3 ("Inner Shadow 3 (x=blur, y=spread) px", Vector) = (0,0,0,0)
        _LayerBlur ("Layer Blur (enabled,progressive,start,end)", Vector) = (0,0,0,0)
        _LayerBlurAffectsEdge ("Layer Blur Affects Edge", Float) = 1
        _MeshPadOverride ("Mesh Pad Override", Vector) = (0,0,0,0)
        _BlendMode ("Figma Blend Mode", Float) = 1
        _PremultiplyOutput ("Premultiply Output", Float) = 0
        _SrcBlend ("Source Blend", Float) = 5
        _DstBlend ("Destination Blend", Float) = 10
        _BlendOp ("Blend Op", Float) = 0

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
        BlendOp [_BlendOp]
        Blend [_SrcBlend] [_DstBlend]
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
            #include "FigForgeBlend.cginc"

            struct appdata
            {
                float4 vertex:POSITION;
                fixed4 color:COLOR;
                float4 uv0:TEXCOORD0;
            };

            struct v2f
            {
                float4 pos:SV_POSITION;
                fixed4 color:COLOR;
                float4 uv0:TEXCOORD0;
                float4 worldPosition:TEXCOORD1; // canvas-space pos for RectMask2D clipping
            };

            float4 _ClipRect;
            sampler2D _MainTex;
            float _FillCount;
            float4 _FillFlags;
            float4 _FillKind;
            fixed4 _FillColor0;
            fixed4 _FillColor1;
            fixed4 _FillColor2;
            fixed4 _FillColor3;
            float4 _FillDir0;
            float4 _FillDir1;
            float4 _FillDir2;
            float4 _FillDir3;
            float _StrokeCount;
            float4 _StrokeFlags;
            float4 _StrokeKind;
            fixed4 _StrokeColor0;
            fixed4 _StrokeColor1;
            fixed4 _StrokeColor2;
            fixed4 _StrokeColor3;
            float4 _StrokeDir0;
            float4 _StrokeDir1;
            float4 _StrokeDir2;
            float4 _StrokeDir3;
            float _StrokeWidth;
            float _StrokeOutset;
            float4 _StrokeDash;
            float4 _Radius;
            float4 _Size;
            float _AppearanceOpacity;
            float _ShadowCount;
            fixed4 _ShadowColor0;
            fixed4 _ShadowColor1;
            fixed4 _ShadowColor2;
            fixed4 _ShadowColor3;
            float4 _ShadowOffset0;
            float4 _ShadowOffset1;
            float4 _ShadowOffset2;
            float4 _ShadowOffset3;
            float4 _ShadowParams0;
            float4 _ShadowParams1;
            float4 _ShadowParams2;
            float4 _ShadowParams3;
            float _InnerShadowCount;
            fixed4 _InnerShadowColor0;
            fixed4 _InnerShadowColor1;
            fixed4 _InnerShadowColor2;
            fixed4 _InnerShadowColor3;
            float4 _InnerShadowOffset0;
            float4 _InnerShadowOffset1;
            float4 _InnerShadowOffset2;
            float4 _InnerShadowOffset3;
            float4 _InnerShadowParams0;
            float4 _InnerShadowParams1;
            float4 _InnerShadowParams2;
            float4 _InnerShadowParams3;
            float4 _LayerBlur;
            float _LayerBlurAffectsEdge;
            float4 _MeshPadOverride;
            float _BlendMode;
            float _PremultiplyOutput;

            v2f vert(appdata v)
            {
                v2f o;
                o.worldPosition = v.vertex;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.color = v.color;
                o.uv0 = v.uv0;
                return o;
            }

            float gradientT(float kind, float2 p, float2 size, float2 dir)
            {
                float2 q = p / max(size, float2(1,1));
                if (kind < 1.5) return saturate(dot(q, normalize(dir)) + 0.5);
                if (kind < 2.5) return saturate(length(q * 2.0));
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

            float sdRoundBox(float2 p, float2 b, float r)
            {
                r = min(r, min(b.x, b.y));
                float2 q = abs(p) - b + r;
                return min(max(q.x, q.y), 0.0) + length(max(q, 0.0)) - r;
            }

            float erfApprox(float x)
            {
                float s = sign(x); x = abs(x);
                float t = 1.0 / (1.0 + 0.3275911 * x);
                float y = 1.0 - (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * exp(-x * x);
                return s * y;
            }

            fixed4 fillPaint(int idx, float2 p, float2 size)
            {
                fixed4 solid = idx == 0 ? _FillColor0 : idx == 1 ? _FillColor1 : idx == 2 ? _FillColor2 : _FillColor3;
                float flag = idx == 0 ? _FillFlags.x : idx == 1 ? _FillFlags.y : idx == 2 ? _FillFlags.z : _FillFlags.w;
                if (flag < 0.5) return solid;
                float kind = idx == 0 ? _FillKind.x : idx == 1 ? _FillKind.y : idx == 2 ? _FillKind.z : _FillKind.w;
                float2 dir = idx == 0 ? _FillDir0.xy : idx == 1 ? _FillDir1.xy : idx == 2 ? _FillDir2.xy : _FillDir3.xy;
                float t = gradientT(kind, p, size, dir);
                return tex2D(_MainTex, float2(t, (idx + 0.5) / 8.0));
            }

            fixed4 compositeFill(float2 p, float2 size)
            {
                fixed4 acc = fixed4(0,0,0,0);
                if (_FillCount > 0.5) acc = sourceOver(fillPaint(0, p, size), acc);
                if (_FillCount > 1.5) acc = sourceOver(fillPaint(1, p, size), acc);
                if (_FillCount > 2.5) acc = sourceOver(fillPaint(2, p, size), acc);
                if (_FillCount > 3.5) acc = sourceOver(fillPaint(3, p, size), acc);
                return acc;
            }

            fixed4 strokePaint(int idx, float2 p, float2 size)
            {
                fixed4 solid = idx == 0 ? _StrokeColor0 : idx == 1 ? _StrokeColor1 : idx == 2 ? _StrokeColor2 : _StrokeColor3;
                float flag = idx == 0 ? _StrokeFlags.x : idx == 1 ? _StrokeFlags.y : idx == 2 ? _StrokeFlags.z : _StrokeFlags.w;
                if (flag < 0.5) return solid;
                float kind = idx == 0 ? _StrokeKind.x : idx == 1 ? _StrokeKind.y : idx == 2 ? _StrokeKind.z : _StrokeKind.w;
                float2 dir = idx == 0 ? _StrokeDir0.xy : idx == 1 ? _StrokeDir1.xy : idx == 2 ? _StrokeDir2.xy : _StrokeDir3.xy;
                float t = gradientT(kind, p, size, dir);
                return tex2D(_MainTex, float2(t, (idx + 4.5) / 8.0));
            }

            fixed4 compositeStroke(float2 p, float2 size)
            {
                fixed4 acc = fixed4(0,0,0,0);
                if (_StrokeCount > 0.5) acc = sourceOver(strokePaint(0, p, size), acc);
                if (_StrokeCount > 1.5) acc = sourceOver(strokePaint(1, p, size), acc);
                if (_StrokeCount > 2.5) acc = sourceOver(strokePaint(2, p, size), acc);
                if (_StrokeCount > 3.5) acc = sourceOver(strokePaint(3, p, size), acc);
                return acc;
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

            float strokeDashMask(float2 p, float2 size, float rad, float aa)
            {
                if (_StrokeDash.x < 0.5) return 1.0;
                float dash = _StrokeDash.y > 0.001 ? _StrokeDash.y : max(1.0, _StrokeWidth * 3.0);
                float gap = _StrokeDash.z > 0.001 ? _StrokeDash.z : dash;
                float period = max(1.0, dash + gap);
                float centerOffset = _StrokeOutset - _StrokeWidth * 0.5;
                float2 centerHalf = max(float2(1.0, 1.0), size * 0.5 + centerOffset);
                float centerRad = clamp(rad + centerOffset, 0.0, min(centerHalf.x, centerHalf.y));
                float s = roundRectPerimeterT(p, centerHalf, centerRad) + _StrokeDash.w;
                float phase = s - floor(s / period) * period;
                float edge = max(aa, fwidth(s));
                return saturate(1.0 - smoothstep(dash - edge, dash + edge, phase));
            }

            fixed4 shadowColor(int idx)
            {
                return idx == 0 ? _ShadowColor0 : idx == 1 ? _ShadowColor1 : idx == 2 ? _ShadowColor2 : _ShadowColor3;
            }

            float2 shadowOffset(int idx)
            {
                return idx == 0 ? _ShadowOffset0.xy : idx == 1 ? _ShadowOffset1.xy : idx == 2 ? _ShadowOffset2.xy : _ShadowOffset3.xy;
            }

            float2 shadowParams(int idx)
            {
                return idx == 0 ? _ShadowParams0.xy : idx == 1 ? _ShadowParams1.xy : idx == 2 ? _ShadowParams2.xy : _ShadowParams3.xy;
            }

            float shadowReach(int idx)
            {
                float4 c = shadowColor(idx);
                float2 off = shadowOffset(idx);
                float2 p = shadowParams(idx);
                return c.a <= 0.001 ? 0.0
                    : max(abs(off.x), abs(off.y)) + 1.75 * max(0.0, p.x) + max(0.0, p.y) + 1.0;
            }

            float meshPad(float strokeOutset)
            {
                float reach = 0.0;
                if (_ShadowCount > 0.5) reach = max(reach, shadowReach(0));
                if (_ShadowCount > 1.5) reach = max(reach, shadowReach(1));
                if (_ShadowCount > 2.5) reach = max(reach, shadowReach(2));
                if (_ShadowCount > 3.5) reach = max(reach, shadowReach(3));
                if (_LayerBlur.x > 0.5) reach = max(reach, 1.75 * max(_LayerBlur.z, _LayerBlur.w) + 1.0);
                return max(max(0.0, strokeOutset), reach);
            }

            fixed4 shadowLayer(int idx, float2 p, float2 size, float rad, float aa)
            {
                fixed4 color = shadowColor(idx);
                if (color.a <= 0.001) return fixed4(0,0,0,0);
                float2 offset = shadowOffset(idx) * float2(1.0, -1.0);
                float2 params = shadowParams(idx);
                float ds = sdRoundBox(p - offset, size * 0.5, rad) - _StrokeOutset;
                float sigma = max(params.x * 0.5, aa);
                float x = (ds - params.y) / (sigma * 1.41421356);
                float alpha = color.a * saturate(0.5 * (1.0 - erfApprox(x)));
                #ifndef UNITY_COLORSPACE_GAMMA
                    alpha = 1.0 - pow(1.0 - alpha, 2.2);
                #endif
                return fixed4(figmaToProjectRgb(color.rgb), alpha);
            }

            fixed4 compositeShadow(float2 p, float2 size, float rad, float aa)
            {
                fixed4 acc = fixed4(0,0,0,0);
                if (_ShadowCount > 0.5) acc = sourceOver(shadowLayer(0, p, size, rad, aa), acc);
                if (_ShadowCount > 1.5) acc = sourceOver(shadowLayer(1, p, size, rad, aa), acc);
                if (_ShadowCount > 2.5) acc = sourceOver(shadowLayer(2, p, size, rad, aa), acc);
                if (_ShadowCount > 3.5) acc = sourceOver(shadowLayer(3, p, size, rad, aa), acc);
                return acc;
            }

            fixed4 innerShadowColor(int idx)
            {
                return idx == 0 ? _InnerShadowColor0 : idx == 1 ? _InnerShadowColor1 : idx == 2 ? _InnerShadowColor2 : _InnerShadowColor3;
            }

            float2 innerShadowOffset(int idx)
            {
                return idx == 0 ? _InnerShadowOffset0.xy : idx == 1 ? _InnerShadowOffset1.xy : idx == 2 ? _InnerShadowOffset2.xy : _InnerShadowOffset3.xy;
            }

            float2 innerShadowParams(int idx)
            {
                return idx == 0 ? _InnerShadowParams0.xy : idx == 1 ? _InnerShadowParams1.xy : idx == 2 ? _InnerShadowParams2.xy : _InnerShadowParams3.xy;
            }

            fixed4 innerShadowLayer(int idx, float2 p, float2 size, float rad, float aa, float shapeCov)
            {
                fixed4 color = innerShadowColor(idx);
                if (color.a <= 0.001) return fixed4(0,0,0,0);
                float2 offset = innerShadowOffset(idx) * float2(1.0, -1.0);
                float2 params = innerShadowParams(idx);
                float ds = sdRoundBox(p - offset, size * 0.5, rad) - _StrokeOutset;
                float sigma = max(params.x * 0.5, aa);
                float x = (ds - params.y) / (sigma * 1.41421356);
                float alpha = color.a * saturate(0.5 * (1.0 + erfApprox(x))) * shapeCov;
                #ifndef UNITY_COLORSPACE_GAMMA
                    alpha = 1.0 - pow(1.0 - alpha, 2.2);
                #endif
                return fixed4(figmaToProjectRgb(color.rgb), alpha);
            }

            fixed4 compositeInnerShadow(fixed4 shape, float2 p, float2 size, float rad, float aa, float shapeCov)
            {
                if (_InnerShadowCount > 0.5) shape = sourceOver(innerShadowLayer(0, p, size, rad, aa, shapeCov), shape);
                if (_InnerShadowCount > 1.5) shape = sourceOver(innerShadowLayer(1, p, size, rad, aa, shapeCov), shape);
                if (_InnerShadowCount > 2.5) shape = sourceOver(innerShadowLayer(2, p, size, rad, aa, shapeCov), shape);
                if (_InnerShadowCount > 3.5) shape = sourceOver(innerShadowLayer(3, p, size, rad, aa, shapeCov), shape);
                return shape;
            }

            float layerBlurAt(float2 p, float2 size)
            {
                if (_LayerBlur.x < 0.5) return 0.0;
                if (_LayerBlur.y < 0.5) return max(0.0, _LayerBlur.z);
                float t = saturate(0.5 - p.y / max(1.0, size.y));
                return max(0.0, lerp(_LayerBlur.z, _LayerBlur.w, t));
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float2 size = max(_Size.xy, float2(1,1));
                float pad = _MeshPadOverride.x > 0.5 ? _MeshPadOverride.y : meshPad(_StrokeOutset);
                float2 full = size + 2.0 * pad;
                float2 p = (i.uv0.xy - 0.5) * full;
                float rad = (p.x > 0.0) ? (p.y > 0.0 ? _Radius.y : _Radius.z)
                                        : (p.y > 0.0 ? _Radius.x : _Radius.w);
                float d = sdRoundBox(p, size * 0.5, rad);
                float aa = max(fwidth(d), 1e-4);
                float edgeAa = max(aa, (_LayerBlurAffectsEdge > 0.5 ? layerBlurAt(p, size) : 0.0) * 0.5);

                fixed4 base = compositeFill(p, size);
                fixed4 strokeBase = compositeStroke(p, size);
                float hasFillGradient = max(max(_FillFlags.x, _FillFlags.y), max(_FillFlags.z, _FillFlags.w));
                float hasStrokeGradient = max(max(_StrokeFlags.x, _StrokeFlags.y), max(_StrokeFlags.z, _StrokeFlags.w));
                if (_FillCount > 1.5 || hasFillGradient > 0.5) base.rgb = figmaToProjectRgb(base.rgb);
                if (_StrokeCount > 1.5 || hasStrokeGradient > 0.5) strokeBase.rgb = figmaToProjectRgb(strokeBase.rgb);
                float fillCov = 1.0 - smoothstep(-edgeAa, edgeAa, d);
                fixed4 shape = fixed4(base.rgb, base.a * fillCov);
                float shapeCov = fillCov;
                if (_StrokeWidth > 0.001 && _StrokeCount > 0.5)
                {
                    float inner = _StrokeOutset - _StrokeWidth;
                    float inStroke = smoothstep(-edgeAa * 2.0, 0.0, d - inner);
                    float outerCov = 1.0 - smoothstep(-edgeAa, edgeAa, d - _StrokeOutset);
                    float dashMask = strokeDashMask(p, size, rad, aa);
                    float strokeCov = outerCov * inStroke * dashMask;
                    float strokeA = strokeBase.a * strokeCov;
                    float keepShape = shapeCov > 1e-4 ? saturate((shapeCov - strokeA) / shapeCov) : 0.0;
                    float dstA = shape.a * keepShape;
                    float outA = strokeA + dstA;
                    fixed3 outRGB = (strokeBase.rgb * strokeA + shape.rgb * dstA) / max(outA, 1e-4);
                    shape = fixed4(outRGB, outA);
                    shapeCov = max(shapeCov, strokeCov);
                }
                shape = compositeInnerShadow(shape, p, size, rad, aa, shapeCov);
                fixed4 shadow = compositeShadow(p, size, rad, aa);
                if (_FillCount < 0.5 && _StrokeCount < 0.5)
                    shadow.a *= 1.0 - fillCov;
                fixed4 outc = sourceOver(shape, shadow);
                outc.a *= saturate(_AppearanceOpacity);
                outc *= i.color;
                if (_PremultiplyOutput > 0.5)
                    outc.rgb *= outc.a;
                float clipv = max(outc.a, shapeCov);
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
