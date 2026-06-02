// Bakes a FigForgeVectorGraphic's triangle mesh into its offscreen surface.
// Vertex colours are region colour × AA alpha (straight); output is premultiplied
// so overlapping fill/stroke/AA flatten with premultiplied-over blending. Opacity
// and the Figma blend mode are applied later, when the surface is presented.
Shader "FigForge/VectorBake"
{
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" }

        Pass
        {
            Cull Off
            ZWrite Off
            ZTest Always
            Blend One OneMinusSrcAlpha // premultiplied "over"

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; fixed4 color : COLOR; };
            struct v2f { float4 pos : SV_POSITION; fixed4 color : COLOR; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.color = v.color;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 c = i.color;
                c.rgb *= c.a; // premultiply
                return c;
            }
            ENDCG
        }
    }
}
