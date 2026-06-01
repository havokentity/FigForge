Shader "FigForge/CachedQuad"
{
    Properties
    {
        [PerRendererData] _MainTex ("Cached Surface", 2D) = "white" {}
        _AppearanceOpacity ("Appearance Opacity", Float) = 1
        _BlendMode ("Figma Blend Mode", Float) = 1
        _SrcBlend ("Source Blend", Float) = 1
        _DstBlend ("Destination Blend", Float) = 10
        _SrcBlendA ("Source Alpha Blend", Float) = 1
        _DstBlendA ("Destination Alpha Blend", Float) = 10
        _BlendOp ("Blend Op", Float) = 0
        _BlendOpA ("Alpha Blend Op", Float) = 0

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
        BlendOp [_BlendOp], [_BlendOpA]
        Blend [_SrcBlend] [_DstBlend], [_SrcBlendA] [_DstBlendA]
        ColorMask [_ColorMask]

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex:POSITION;
                fixed4 color:COLOR;
                float2 uv0:TEXCOORD0;
            };

            struct v2f
            {
                float4 pos:SV_POSITION;
                fixed4 color:COLOR;
                float2 uv:TEXCOORD0;
            };

            sampler2D _MainTex;
            float _AppearanceOpacity;

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.color = v.color;
                o.uv = v.uv0;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 c = tex2D(_MainTex, i.uv);
                c *= saturate(_AppearanceOpacity);
                c.rgb *= i.color.rgb * i.color.a;
                c.a *= i.color.a;
                clip(c.a - 0.001);
                return c;
            }
            ENDCG
        }
    }
}
