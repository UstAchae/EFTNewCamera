// Both eyes looking into a binocular night-vision device, side by side: the left half of the screen is the left
// eye's view, the right half the right eye's, each the main camera's view cut to that half and seen from the eye's
// own place. Nothing near the eye is drawn as such: focused far away, the eye sees the housing, the eyecups and
// the helmet only as darkness, and the lens edges as soft as its pupil makes them. So every pixel follows its
// line of sight from the eye: through each lens plane, how much of the pupil the lens rim lets through (a band as
// wide as the pupil), across the seam of two lenses in one eyecup how much goes through either, each with its own
// tube's field stop, and the picture in that direction (the tubes are 1x and afocal). With _Split.x 0 the main
// camera's own frame passes through.
Shader "Justachae/NVG/Binocular" {
 Properties { _MainTex ("main camera", 2D) = "black" {} }
 SubShader {
  Cull Off ZWrite Off ZTest Always
  Pass {
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   #include "UnityCG.cginc"
   sampler2D _MainTex, _Picture;
   float4x4 _PictureVP;          // the picture camera's view-projection
   float4x4 _EyeInvVP0, _EyeInvVP1; // each eye's half view, normalised device coordinates to world
   float4 _EyePos0, _EyePos1;
   float4 _LensCentre[4];        // world centre, clear radius (m); lenses 0,1 share an eyecup, so do 2,3
   float4 _LensNormal[4];        // world, toward the eye; w half field (deg)
   float4 _Soft;                 // x pupil diameter (m), y field stop softness (deg)
   float4 _Glow;                 // the screen's light past the field stop
   float4 _Split;                // x 1 while both eyes are drawn

   float pupilLinear (float d) { return smoothstep(-0.5, 0.5, d / max(_Soft.x, 1e-5)); }

   // Where the line meets lens i's plane (a point far away when it runs away from it).
   float3 onLens (float3 eye, float3 ray, int i) {
    float3 n = _LensNormal[i].xyz;
    float along = dot(ray, n);
    float t = along < -1e-4 ? dot(_LensCentre[i].xyz - eye, n) / along : -1;
    return t > 0 ? eye + ray * t : float3(1e6, 1e6, 1e6);
   }
   float rim (float3 p, int i) { return pupilLinear(_LensCentre[i].w - length(p - _LensCentre[i].xyz)); }
   float field (float3 ray, int i) {
    float off = degrees(acos(saturate(dot(ray, -_LensNormal[i].xyz))));
    return 1 - smoothstep(_LensNormal[i].w - _Soft.y, _LensNormal[i].w, off);
   }

   // One eyecup's two lenses a and b: x light through to the eye (with the field stops), y the lenses' own
   // brightness for the screen glow.
   float2 eyecup (float3 eye, float3 ray, int a, int b) {
    float3 pa = onLens(eye, ray, a), pb = onLens(eye, ray, b);
    float3 apart = normalize(_LensCentre[b].xyz - _LensCentre[a].xyz);
    float beyond = dot(pa - (_LensCentre[a].xyz + _LensCentre[b].xyz) * 0.5, apart);
    float side = pupilLinear(-beyond);              // the share of the pupil looking through lens a
    float wall = 1 - 0.2 * (1 - smoothstep(0, _Soft.x * 0.5, abs(beyond)));
    float ra = rim(pa, a), rb = rim(pb, b);
    return wall * float2(side * ra * field(ray, a) + (1 - side) * rb * field(ray, b), side * ra + (1 - side) * rb);
   }

   float4 frag (v2f_img i) : SV_Target {
    if (_Split.x < 0.5) return tex2D(_MainTex, i.uv);
    bool right = i.uv.x >= 0.5;
    float2 ndc = float2(i.uv.x * 2 - (right ? 1 : 0), i.uv.y) * 2 - 1;
    float4 far = mul(right ? _EyeInvVP1 : _EyeInvVP0, float4(ndc, 1, 1));
    float3 eye = right ? _EyePos1.xyz : _EyePos0.xyz;
    float3 ray = normalize(far.xyz / far.w - eye);
    float2 light = eyecup(eye, ray, 0, 1) + eyecup(eye, ray, 2, 3);
    float4 c = mul(_PictureVP, float4(ray, 0));
    float2 uv = c.xy / c.w * 0.5 + 0.5;
    float inside = step(0, c.w) * step(0, uv.x) * step(uv.x, 1) * step(0, uv.y) * step(uv.y, 1);
    float3 col = tex2D(_Picture, uv).rgb * inside * light.x + _Glow.rgb * light.y;
    return float4(col, 1);
   }
   ENDCG
  }
 }
}
