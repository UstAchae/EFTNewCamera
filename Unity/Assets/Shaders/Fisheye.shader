// The frame, rendered straight (rectilinear) with a wide enough field, laid out as a fisheye lens would: a point at an
// angle theta off the centre of view lands at r = f tan(k theta) / k from the screen's centre. k = 1 is the straight
// picture itself, k = 1/2 the stereographic fisheye; in between it eases from one to the other. Distances are in
// half screen heights. The straight frame's field is chosen so that the fisheye's corners land on its corners, so
// nothing past its edges is ever asked for. Then the lens's vignette: the light falling off toward the edge of its
// image circle, and past that circle the dark of the lens barrel, in the corners.
// Where the lens magnifies the frame (toward the centre) it is read through a Catmull-Rom filter, sharper than the
// bilinear one; where it shrinks it (toward the edges) several points over each screen pixel's footprint are averaged,
// so fine lines do not shimmer.
Shader "Justachae/Fisheye" {
 Properties { _MainTex ("", 2D) = "black" {} }
 CGINCLUDE
 #include "UnityCG.cginc"
 sampler2D _MainTex;
 float4 _MainTex_TexelSize;   // 1/width, 1/height, width, height
 ENDCG
 SubShader {
  Cull Off ZWrite Off ZTest Always
  Pass {
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   #pragma target 3.5
   float4 _Fisheye;   // x k, y f, z the straight frame's half height (as the tangent of its half field), w the aspect
   float4 _FisheyeShade;   // x the image circle's radius, y the width of its soft edge (both over the screen's half
                           // diagonal), z how much the light falls off toward the circle's edge

   // Catmull-Rom over 4 by 4 texels, in 9 bilinear reads.
   float4 catmullRom (float2 uv) {
    float2 at = uv * _MainTex_TexelSize.zw;
    float2 p1 = floor(at - 0.5) + 0.5;
    float2 f = at - p1;
    float2 w0 = f * (-0.5 + f * (1 - 0.5 * f));
    float2 w1 = 1 + f * f * (-2.5 + 1.5 * f);
    float2 w2 = f * (0.5 + f * (2 - 1.5 * f));
    float2 w3 = f * f * (-0.5 + 0.5 * f);
    float2 w12 = w1 + w2;
    float2 p0 = (p1 - 1) * _MainTex_TexelSize.xy;
    float2 p3 = (p1 + 2) * _MainTex_TexelSize.xy;
    float2 p12 = (p1 + w2 / w12) * _MainTex_TexelSize.xy;
    float4 c = 0;
    c += tex2Dlod(_MainTex, float4(p0.x, p0.y, 0, 0)) * w0.x * w0.y;
    c += tex2Dlod(_MainTex, float4(p12.x, p0.y, 0, 0)) * w12.x * w0.y;
    c += tex2Dlod(_MainTex, float4(p3.x, p0.y, 0, 0)) * w3.x * w0.y;
    c += tex2Dlod(_MainTex, float4(p0.x, p12.y, 0, 0)) * w0.x * w12.y;
    c += tex2Dlod(_MainTex, float4(p12.x, p12.y, 0, 0)) * w12.x * w12.y;
    c += tex2Dlod(_MainTex, float4(p3.x, p12.y, 0, 0)) * w3.x * w12.y;
    c += tex2Dlod(_MainTex, float4(p0.x, p3.y, 0, 0)) * w0.x * w3.y;
    c += tex2Dlod(_MainTex, float4(p12.x, p3.y, 0, 0)) * w12.x * w3.y;
    c += tex2Dlod(_MainTex, float4(p3.x, p3.y, 0, 0)) * w3.x * w3.y;
    return max(c, 0);
   }

   float2 straightUv (float2 uv) {
    float2 q = (uv * 2 - 1) * float2(_Fisheye.w, 1);
    float r = length(q);
    if (r < 1e-5) return uv;
    float theta = atan(_Fisheye.x * r / _Fisheye.y) / _Fisheye.x;
    float2 straight = q * (tan(theta) / (r * _Fisheye.z));
    return straight / float2(_Fisheye.w, 1) * 0.5 + 0.5;
   }

   float4 frag (v2f_img i) : SV_Target {
    float2 uv = straightUv(i.uv);
    // The screen pixel's footprint on the frame, in the frame's texels.
    float2 dx = ddx(uv), dy = ddy(uv);
    float footprint = max(length(dx * _MainTex_TexelSize.zw), length(dy * _MainTex_TexelSize.zw));
    float4 sharp = catmullRom(saturate(uv));
    float4 averaged = 0.25 * (tex2Dlod(_MainTex, float4(saturate(uv + 0.25 * (dx + dy)), 0, 0))
                            + tex2Dlod(_MainTex, float4(saturate(uv + 0.25 * (dx - dy)), 0, 0))
                            + tex2Dlod(_MainTex, float4(saturate(uv - 0.25 * (dx - dy)), 0, 0))
                            + tex2Dlod(_MainTex, float4(saturate(uv - 0.25 * (dx + dy)), 0, 0)));
    float4 colour = lerp(sharp, averaged, saturate(footprint - 1));
    float2 q = (i.uv * 2 - 1) * float2(_Fisheye.w, 1);
    float across = length(q) / sqrt(_Fisheye.w * _Fisheye.w + 1);
    float inCircle = 1 - smoothstep(_FisheyeShade.x - _FisheyeShade.y, _FisheyeShade.x, across);
    float falloff = 1 - _FisheyeShade.z * pow(saturate(across / _FisheyeShade.x), 4);
    colour.rgb *= inCircle * falloff;
    return colour;
   }
   ENDCG
  }
 }
}
