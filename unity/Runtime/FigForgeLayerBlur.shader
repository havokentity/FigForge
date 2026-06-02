Shader "FigForge/LayerBlur"
{
    Properties
    {
        _MainTex ("Source", 2D) = "white" {}
        _BlurParams ("Blur Params", Vector) = (0,0,0,0)
        _Direction ("Direction", Vector) = (1,0,0,0)
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" }
        Cull Off
        ZWrite Off
        ZTest Always
        Blend One Zero

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

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            float4 _BlurParams; // x enabled, y progressive, z start/uniform px, w end px
            float4 _Direction;

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            float blurPixels(float2 uv)
            {
                if (_BlurParams.x < 0.5) return 0.0;
                if (_BlurParams.y < 0.5) return max(0.0, _BlurParams.z);
                float t = saturate(1.0 - uv.y);
                return max(0.0, lerp(_BlurParams.z, _BlurParams.w, t));
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float blur = blurPixels(i.uv);
                if (blur <= 0.001) return tex2D(_MainTex, i.uv);

                float2 stepUv = _Direction.xy * _MainTex_TexelSize.xy * (blur * 0.25);

                fixed4 c = tex2D(_MainTex, i.uv) * 0.2270270270;
                c += tex2D(_MainTex, i.uv + stepUv * 1.0) * 0.1945945946;
                c += tex2D(_MainTex, i.uv - stepUv * 1.0) * 0.1945945946;
                c += tex2D(_MainTex, i.uv + stepUv * 2.0) * 0.1216216216;
                c += tex2D(_MainTex, i.uv - stepUv * 2.0) * 0.1216216216;
                c += tex2D(_MainTex, i.uv + stepUv * 3.0) * 0.0540540541;
                c += tex2D(_MainTex, i.uv - stepUv * 3.0) * 0.0540540541;
                c += tex2D(_MainTex, i.uv + stepUv * 4.0) * 0.0162162162;
                c += tex2D(_MainTex, i.uv - stepUv * 4.0) * 0.0162162162;
                return c;
            }
            ENDCG
        }
    }
}
