// The image blend pipeline's sprite bake. Blitted with the sprite's texture as
// _MainTex; samples the sprite's textureRect sub-region (_UvRect), tints by the
// Image colour, and writes the result PREMULTIPLIED — the same convention as
// every other FigForge surface, so the compositor and present quad treat it
// like any baked layer. _ContentRect places the sprite inset by the AA pad
// ring (left transparent) so the present quad — expanded by the same pad —
// shows the sprite exactly across the node's rect.
Shader "FigForge/ImageBake"
{
    Properties
    {
        _MainTex ("Sprite Texture", 2D) = "white" {}
        _Tint ("Tint", Color) = (1,1,1,1)
        _UvRect ("Sprite UV Rect px-normalized (x,y = min, z,w = size)", Vector) = (0,0,1,1)
        _ContentRect ("Content Rect in target UV (x,y = min, z,w = size)", Vector) = (0,0,1,1)
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
            half4 _Tint;
            float4 _UvRect;
            float4 _ContentRect;

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                // 0..1 across the node's rect; outside lies the transparent pad ring.
                float2 t = (i.uv - _ContentRect.xy) / max(_ContentRect.zw, float2(1e-5, 1e-5));
                float2 uv = _UvRect.xy + saturate(t) * _UvRect.zw;
                half4 c = tex2D(_MainTex, uv) * _Tint;
                // Zero the pad ring — clamped sampling above would otherwise smear the
                // sprite's edge pixels (or atlas neighbours) into the margin.
                half inside = step(0.0, t.x) * step(t.x, 1.0) * step(0.0, t.y) * step(t.y, 1.0);
                c *= inside;
                return half4(c.rgb * c.a, c.a); // straight-alpha PNG in, premultiplied surface out
            }
            ENDCG
        }
    }
}
