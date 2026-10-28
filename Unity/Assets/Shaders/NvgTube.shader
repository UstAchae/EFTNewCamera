// Image intensifier for the night-vision eyepiece picture: the scene weighted by the photocathode's spectral
// response, amplified with automatic gain (the adapted mean brightness from pass 1 is brought to a target within
// the tube's gain range), then read noise plus shot noise that grows with the square root of the signal, a
// coarser grain, a stationary fixed-pattern gain variation and brief single-pixel scintillation, refreshed 30
// times a second and strongest in the dark; finally the phosphor colour.
Shader "Justachae/NVG/Tube" {
 Properties { _MainTex ("scene", 2D) = "black" {} }
 CGINCLUDE
 #include "UnityCG.cginc"
 sampler2D _MainTex;
 float4 _MainTex_TexelSize;
 sampler2D _Exposure;   // 1x1: the adapted mean brightness
 sampler2D _Previous;   // 1x1: last frame's
 float4 _TubeGain;      // x target mean brightness, y lowest gain, z highest gain, w the mean's mip level
 float4 _TubeAdapt;     // x seconds since last frame, y rate toward a brighter mean, z toward a darker one (1/s)
 float4 _TubeNoise;     // x noise scale, y grain size in pixels, z time
 float4 _TubeTint;      // phosphor colour, peak 1
 float4 _TubeSpectral;  // photocathode weights for the scene's r, g, b
 float weigh(float3 c) { float3 w = max(_TubeSpectral.rgb, 0); return dot(max(c, 0), w / max(dot(w, 1), 1e-4)); }
 ENDCG
 SubShader {
  Cull Off ZWrite Off ZTest Always
  Pass {
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag

   float hash12(float2 p) { float3 p3 = frac(p.xyx * 0.1031); p3 += dot(p3, p3.yzx + 33.33); return frac((p3.x + p3.y) * p3.z); }
   float hash13(float3 p3) { p3 = frac(p3 * 0.1031); p3 += dot(p3, p3.zyx + 31.32); return frac((p3.x + p3.y) * p3.z); }
   // Roughly normal (sum of three uniforms), zero mean, unit variance.
   float gauss(float2 cell, float frame) {
    return (hash13(float3(cell, frame)) + hash13(float3(cell + 71.3, frame)) + hash13(float3(cell + 143.7, frame)) - 1.5) * 2.0;
   }
   float value(float2 p) {
    float2 i = floor(p), f = frac(p); f = f * f * (3 - 2 * f);
    return lerp(lerp(hash12(i), hash12(i + float2(1, 0)), f.x), lerp(hash12(i + float2(0, 1)), hash12(i + 1), f.x), f.y);
   }

   fixed4 frag (v2f_img i) : SV_Target {
    float lum = weigh(tex2D(_MainTex, i.uv).rgb);
    float mean = tex2D(_Exposure, float2(0.5, 0.5)).r;
    float gain = clamp(_TubeGain.x / max(mean, 1e-6), _TubeGain.y, _TubeGain.z);
    float tube = 1 - exp(-lum * gain);                     // amplified, soft-saturating screen brightness

    float2 pixel = i.uv * _MainTex_TexelSize.zw;
    float frame = floor(_TubeNoise.z * 30.0);
    float grain = max(_TubeNoise.y, 0.5);
    float k = _TubeNoise.x;
    float dark = 1 - 0.65 * tube;
    float fixedPattern = (value(pixel / 48 + 17.1) - 0.5) * 2 + (hash12(floor(pixel / 5) + 91.7) - 0.5) * 0.45;
    tube *= 1 + fixedPattern * 0.018 * k;
    float sigma = 0.03 + 0.065 * sqrt(max(tube, 0.001));
    float n = gauss(floor(pixel / grain), frame) * sigma + gauss(floor(pixel / (grain * 3)), floor(frame * 0.53) + 137) * 0.022;
    float2 tile = floor(pixel / 3);
    float event = step(1 - 0.0015, hash13(float3(tile, frame)));
    float2 at = floor(float2(hash13(float3(tile + 17.3, frame)), hash13(float3(tile.yx + 43.7, frame))) * 3);
    float2 inTile = floor(pixel) - tile * 3;
    float spark = event * step(dot(abs(inTile - at), 1), 0.5) * 0.35 * (1.25 - saturate(tube));
    tube = max(tube + (n + spark) * k * dark, 0);
    return fixed4(saturate(tube * _TubeTint.rgb), 1);
   }
   ENDCG
  }
  // The tube's gain follows the scene's mean brightness over time, down quickly when it gets brighter and up
  // slowly when it gets darker. _MainTex is a small mipmapped copy of the scene.
  Pass {
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   float4 frag (v2f_img i) : SV_Target {
    float now = weigh(tex2Dlod(_MainTex, float4(0.5, 0.5, 0, _TubeGain.w)).rgb);
    float before = tex2D(_Previous, float2(0.5, 0.5)).r;
    if (!(before > 0)) return now;                    // the first frame, or nothing yet
    float rate = now > before ? _TubeAdapt.y : _TubeAdapt.z;
    return lerp(before, now, 1 - exp(-rate * _TubeAdapt.x));
   }
   ENDCG
  }
 }
}
