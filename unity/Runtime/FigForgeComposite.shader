Shader "FigForge/Composite"
{
    Properties
    {
        _Source ("Source", 2D) = "white" {}
        _Backdrop ("Backdrop", 2D) = "black" {}
        _BlendMode ("Figma Blend Mode", Float) = 1
        _AppearanceOpacity ("Appearance Opacity", Float) = 1
        _ClipRect ("Clip Rect", Vector) = (-1000000000,-1000000000,1000000000,1000000000)
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

            sampler2D _Source;
            sampler2D _Backdrop;
            float _BlendMode;
            float _AppearanceOpacity;
            float4 _ClipRect;

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                half4 s = tex2D(_Source, i.uv);
                half4 b = tex2D(_Backdrop, i.uv);
                float as = saturate(s.a * _AppearanceOpacity);
                float ab = saturate(b.a);
                float3 Cs = s.rgb / max(s.a, 1e-4);
                float3 Cb = b.rgb / max(b.a, 1e-4);
                float3 Bc = figmaBlendRgb(Cs, Cb, _BlendMode);

                float3 co = as * (1.0 - ab) * Cs + as * ab * Bc + (1.0 - as) * ab * Cb;
                float ao = as + ab * (1.0 - as);

                float2 pixel = i.uv * _ScreenParams.xy;
                float inside = step(_ClipRect.x, pixel.x) * step(_ClipRect.y, pixel.y)
                    * step(pixel.x, _ClipRect.z) * step(pixel.y, _ClipRect.w);
                return half4(co * inside, ao * inside);
            }
            ENDCG
        }
    }
}
