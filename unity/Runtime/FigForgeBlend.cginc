#ifndef FIGFORGE_BLEND_INCLUDED
#define FIGFORGE_BLEND_INCLUDED

fixed4 sourceOver(fixed4 src, fixed4 dst)
{
    float a = src.a + dst.a * (1.0 - src.a);
    fixed3 rgb = (src.rgb * src.a + dst.rgb * dst.a * (1.0 - src.a)) / max(a, 1e-4);
    return fixed4(rgb, a);
}

fixed3 figmaToProjectRgb(fixed3 rgb)
{
    #ifndef UNITY_COLORSPACE_GAMMA
        return GammaToLinearSpace(rgb);
    #else
        return rgb;
    #endif
}

fixed3 projectToFigmaRgb(fixed3 rgb)
{
    #ifndef UNITY_COLORSPACE_GAMMA
        return LinearToGammaSpace(rgb);
    #else
        return rgb;
    #endif
}

float blendLum(float3 c) { return dot(c, float3(0.3, 0.59, 0.11)); }
float blendSat(float3 c) { return max(max(c.r, c.g), c.b) - min(min(c.r, c.g), c.b); }

float3 clipColor(float3 c)
{
    float l = blendLum(c);
    float n = min(min(c.r, c.g), c.b);
    float x = max(max(c.r, c.g), c.b);
    if (n < 0.0) c = l + ((c - l) * l) / max(l - n, 1e-5);
    if (x > 1.0) c = l + ((c - l) * (1.0 - l)) / max(x - l, 1e-5);
    return saturate(c);
}

float3 setLum(float3 c, float l)
{
    return clipColor(c + (l - blendLum(c)));
}

float3 setSat(float3 c, float s)
{
    float mn = min(min(c.r, c.g), c.b);
    float mx = max(max(c.r, c.g), c.b);
    float scale = mx > mn ? s / (mx - mn) : 0.0;
    return (c - mn) * scale;
}

// Blend-mode math with inputs/outputs in FIGMA (sRGB) space — Figma composites
// in sRGB, so callers that mix further (coverage lerps) should stay in this
// space and convert once at the end.
fixed3 figmaBlendFigmaSpace(fixed3 s, fixed3 d, float mode)
{
    fixed3 b = s;

    if (mode < 1.5) b = s;
    else if (mode < 2.5) b = min(s, d);
    else if (mode < 3.5) b = s * d;
    else if (mode < 4.5) b = max(float3(0,0,0), s + d - 1.0);
    else if (mode < 5.5) b = 1.0 - min(1.0, (1.0 - d) / max(s, 1e-5));
    else if (mode < 6.5) b = max(s, d);
    else if (mode < 7.5) b = 1.0 - (1.0 - s) * (1.0 - d);
    else if (mode < 8.5) b = min(1.0, s + d);
    else if (mode < 9.5) b = min(1.0, d / max(1.0 - s, 1e-5));
    else if (mode < 10.5) b = lerp(2.0 * s * d, 1.0 - 2.0 * (1.0 - s) * (1.0 - d), step(0.5, d));
    else if (mode < 11.5)
    {
        fixed3 g = lerp(((16.0 * d - 12.0) * d + 4.0) * d, sqrt(d), step(0.25, d));
        b = lerp(d - (1.0 - 2.0 * s) * d * (1.0 - d), d + (2.0 * s - 1.0) * (g - d), step(0.5, s));
    }
    else if (mode < 12.5) b = lerp(2.0 * s * d, 1.0 - 2.0 * (1.0 - s) * (1.0 - d), step(0.5, s));
    else if (mode < 13.5) b = abs(d - s);
    else if (mode < 14.5) b = d + s - 2.0 * d * s;
    else if (mode < 15.5) b = setLum(setSat(s, blendSat(d)), blendLum(d));
    else if (mode < 16.5) b = setLum(setSat(d, blendSat(s)), blendLum(d));
    else if (mode < 17.5) b = setLum(s, blendLum(d));
    else b = setLum(d, blendLum(s));

    return saturate(b);
}

fixed3 figmaBlendRgb(fixed3 srcProject, fixed3 dstProject, float mode)
{
    fixed3 s = saturate(projectToFigmaRgb(srcProject));
    fixed3 d = saturate(projectToFigmaRgb(dstProject));
    return figmaToProjectRgb(figmaBlendFigmaSpace(s, d, mode));
}

#endif
