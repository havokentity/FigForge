// The page compositor's per-layer blend pass. Blitted with a Tier-2 layer's
// cached premultiplied surface as _MainTex; samples the page-capture backdrop at
// the layer's screen rect and outputs the Figma-blended result PREMULTIPLIED by
// coverage. The layer's own graphic then presents this texture as a normal
// premult-over quad (FigForge/CachedQuad) at its hierarchy position, so masking,
// raycasts, and z-order against foreign uGUI all behave like any other graphic.
//
// Where coverage < 1 the quad blends toward the LIVE backdrop beneath it, which
// equals the captured backdrop — so soft shadows and AA edges stay seamless.
// The capture is treated as opaque (alpha ignored): uGUI leaves non-1 destination
// alpha along AA edges, which is meaningless here.
Shader "FigForge/Composite"
{
    Properties
    {
        _MainTex ("Layer Surface", 2D) = "white" {}
        _Backdrop ("Backdrop Capture", 2D) = "black" {}
        _BlendMode ("Figma Blend Mode", Float) = 1
        _AppearanceOpacity ("Appearance Opacity", Float) = 1
        _BackdropRect ("Backdrop Rect px (x,y,w,h)", Vector) = (0,0,1,1)
        _BackdropSize ("Backdrop Size px", Vector) = (1,1,0,0)
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" }
        Cull Off
        ZWrite Off
        ZTest Always
        Blend Off

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
            float _BlendMode;
            float _AppearanceOpacity;
            float4 _BackdropRect;
            float4 _BackdropSize;

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                half4 s = tex2D(_MainTex, i.uv); // premultiplied layer surface
                half cov = saturate(s.a * _AppearanceOpacity);
                half3 Cs = s.rgb / max(s.a, 1e-4);
                float2 backdropUv = (_BackdropRect.xy + i.uv * _BackdropRect.zw)
                    / max(_BackdropSize.xy, float2(1, 1));
                half3 Cb = tex2D(_Backdrop, saturate(backdropUv)).rgb;
                half3 blended = figmaBlendRgb(Cs, Cb, _BlendMode);
                return half4(cov * blended, cov);
            }
            ENDCG
        }
    }
}
