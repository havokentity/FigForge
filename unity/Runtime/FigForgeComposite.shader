// The page compositor's per-layer blend pass. Blitted with a Tier-2 layer's
// cached premultiplied surface as _MainTex; samples the page-capture backdrop at
// the layer's screen rect and outputs the Figma-blended result PREMULTIPLIED by
// coverage. The layer's own graphic then presents this texture as a normal
// premult-over quad (FigForge/CachedQuad) at its hierarchy position, so masking,
// raycasts, and z-order against foreign uGUI all behave like any other graphic.
//
// Background blur (glassmorphism): when _HasBackdropBlur is set, the backdrop
// seen by each pixel is the PRE-BLURRED crop (_BackdropBlurTex) inside the
// layer's rounded-rect shape and the sharp capture outside it. The output then
// replaces the backdrop inside the shape (alpha = shape coverage) so the live
// page shows the blur through semi-transparent fills, while drop shadows in the
// same surface keep compositing over the SHARP backdrop outside the shape.
//
// Figma composites in sRGB, so every coverage mix here runs in Figma (sRGB)
// space; mixing in linear reads as a washed-out band wherever soft coverage
// ramps over the backdrop (layer-blurred edges especially). The pass computes
// the final ON-SCREEN colour in Figma space —
//   finalF = cov·blendF(CsF, CbF) + (1−cov)·CbF,  CbF = lerp(sharp, blurred, covShape)
//   out.a  = cov + covShape − cov·covShape
// — then pre-compensates the quad's linear premult-over blend:
//   out.rgb = max(0, linear(finalF) − (1−out.a)·B_sharp_linear)
// so screen = out.rgb + (1−out.a)·live lands exactly on finalF.
//
// Where coverage < 1 the quad blends toward the LIVE backdrop beneath it, which
// equals the captured backdrop — so soft shadows and AA edges stay seamless.
// The capture is treated as opaque (alpha ignored): uGUI leaves non-1 destination
// alpha along AA edges, which is meaningless here.
//
// Pass 1 crops the layer's (blur-extended) screen rect out of the capture so the
// separable blur (FigForge/LayerBlur) runs on a small RT, not the full page.
Shader "FigForge/Composite"
{
    Properties
    {
        _MainTex ("Layer Surface", 2D) = "white" {}
        _Backdrop ("Backdrop Capture", 2D) = "black" {}
        _BackdropBlurTex ("Blurred Backdrop Crop", 2D) = "black" {}
        _BlendMode ("Figma Blend Mode", Float) = 1
        _AppearanceOpacity ("Appearance Opacity", Float) = 1
        _BackdropRect ("Backdrop Rect px (x,y,w,h)", Vector) = (0,0,1,1)
        _BackdropSize ("Backdrop Size px", Vector) = (1,1,0,0)
        _HasBackdropBlur ("Has Backdrop Blur", Float) = 0
        _BlurUvParams ("Blur Crop (extend xy, size zw)", Vector) = (0,0,1,1)
        _ShapeRectPx ("Shape Rect surface px (x,y,w,h)", Vector) = (0,0,1,1)
        _ShapeRadiusPx ("Shape Radii surface px (tl,tr,br,bl)", Vector) = (0,0,0,0)
        _TargetSize ("Target Size px", Vector) = (1,1,0,0)
        _CropRect ("Crop Rect px (x,y,w,h)", Vector) = (0,0,1,1)
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" }
        Cull Off
        ZWrite Off
        ZTest Always
        Blend Off

        // Pass 0 — per-layer blend (+ optional backdrop blur compositing).
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            #include "FigForgeBlend.cginc"

            struct appdata
            {
                float4 vertex:POSITION;
                float2 uv:TEXCOORD0;
            };

            struct v2f
            {
                float4 pos:SV_POSITION;
                float2 uv:TEXCOORD0;
            };

            sampler2D _MainTex;
            sampler2D _Backdrop;
            sampler2D _BackdropBlurTex;
            float _BlendMode;
            float _AppearanceOpacity;
            float4 _BackdropRect;
            float4 _BackdropSize;
            float _HasBackdropBlur;
            float4 _BlurUvParams;
            float4 _ShapeRectPx;
            float4 _ShapeRadiusPx;
            float4 _TargetSize;

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            float sdRoundBoxFF(float2 p, float2 b, float r)
            {
                float2 q = abs(p) - b + r;
                return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
            }

            half4 frag(v2f i) : SV_Target
            {
                half4 s = tex2D(_MainTex, i.uv); // premultiplied layer surface
                half cov = saturate(s.a * _AppearanceOpacity);
                half3 Cs = s.rgb / max(s.a, 1e-4);
                float2 backdropUv = (_BackdropRect.xy + i.uv * _BackdropRect.zw)
                    / max(_BackdropSize.xy, float2(1, 1));
                half3 CbLin = tex2D(_Backdrop, saturate(backdropUv)).rgb;

                half covShape = 0;
                half3 CblurF = half3(0, 0, 0);
                if (_HasBackdropBlur > 0.5)
                {
                    // Analytic rounded-rect coverage in surface pixels: the blur is
                    // bounded by the SHAPE, not by paint alpha — so semi-transparent
                    // glass fills get a fully blurred backdrop while drop shadows in
                    // the pad ring stay over the sharp page.
                    float2 px = i.uv * _TargetSize.xy;
                    float2 p = px - (_ShapeRectPx.xy + _ShapeRectPx.zw * 0.5);
                    float rad = (p.x > 0.0) ? (p.y > 0.0 ? _ShapeRadiusPx.y : _ShapeRadiusPx.z)
                                            : (p.y > 0.0 ? _ShapeRadiusPx.x : _ShapeRadiusPx.w);
                    float d = sdRoundBoxFF(p, _ShapeRectPx.zw * 0.5, rad);
                    covShape = 1.0 - smoothstep(-1.0, 1.0, d);
                    float2 blurUv = (px + _BlurUvParams.xy) / max(_BlurUvParams.zw, float2(1, 1));
                    CblurF = projectToFigmaRgb(saturate(tex2D(_BackdropBlurTex, saturate(blurUv)).rgb));
                }

                // Figma composites in sRGB: do every coverage mix in Figma space and
                // convert once at the end — linear-space mixing reads as a washed-out
                // (white-ish) band wherever soft coverage ramps over the backdrop.
                half3 CsF = projectToFigmaRgb(saturate(Cs));
                half3 CbSharpF = projectToFigmaRgb(saturate(CbLin));
                half3 CbF = lerp(CbSharpF, CblurF, covShape); // backdrop this pixel composites against
                half3 blendedF = figmaBlendFigmaSpace(CsF, CbF, _BlendMode);

                // The colour this pixel should land on ON SCREEN (the live page
                // beneath the quad equals the captured backdrop).
                half3 finalF = cov * blendedF + (1.0 - cov) * CbF;
                half outA = saturate(cov + covShape - cov * covShape);
                // Pre-compensate the quad's linear premult-over blend so the screen
                // result resolves to finalF: screen = outRgb + (1-outA)·backdrop.
                half3 outRgb = max(half3(0, 0, 0),
                    figmaToProjectRgb(finalF) - (1.0 - outA) * CbLin);
                return half4(outRgb, outA);
            }
            ENDCG
        }

        // Pass 1 — backdrop crop: copy the layer's (blur-extended) screen rect out
        // of the page capture at surface resolution, opaque.
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex:POSITION;
                float2 uv:TEXCOORD0;
            };

            struct v2f
            {
                float4 pos:SV_POSITION;
                float2 uv:TEXCOORD0;
            };

            sampler2D _Backdrop;
            float4 _BackdropSize;
            float4 _CropRect;

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                float2 uv = (_CropRect.xy + i.uv * _CropRect.zw)
                    / max(_BackdropSize.xy, float2(1, 1));
                return half4(tex2D(_Backdrop, saturate(uv)).rgb, 1);
            }
            ENDCG
        }
    }
}
