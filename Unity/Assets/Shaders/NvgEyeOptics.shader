// One eye's view through a head-mounted night-vision device, after its camera has rendered the scene. The eye
// focuses far away, so whatever lies a few centimetres in front of it (the device's housing, the eyecups, the
// helmet's edge) is out of focus: each point of it spreads over a disc whose angular size is the pupil's diameter
// over its distance. And the lenses' picture is at infinity, so it is sharp, seen through apertures that are as
// blurred as their own distance makes them.
//
// Pass 0, at half resolution: the near foreground (closer than _Near.x) spread out, premultiplied, by gathering
// each sample whose disc reaches this pixel (each sample stands for an equal share of the search area).
// Pass 1: a small tent filter over it, to smooth the sampling noise.
// Pass 2: the lens map blurred evenly by the lenses' own defocus.
// Pass 3: the scene with the blurred foreground over it, plus the light through the lenses: per lens, how much of
// the pupil it reaches times that tube's field stop, times the picture in the line of sight's direction.
Shader "Justachae/NVG/EyeOptics" {
 Properties { _MainTex ("source", 2D) = "black" {} }
 CGINCLUDE
 #include "UnityCG.cginc"
 sampler2D _MainTex;
 float4 _MainTex_TexelSize;
 UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);
 float4 _Near;            // x foreground limit (m), y defocus radius in output pixels at 1 m, z largest radius, w samples' search radius
 float4 _Jitter;          // x frame

 // Golden-angle spiral over the unit disc, turned per pixel and frame against banding.
 float2 spiral (int k, int count, float turn) {
  float r = sqrt((k + 0.5) / count);
  float a = k * 2.39996323 + turn;
  return r * float2(cos(a), sin(a));
 }
 float noise (float2 pixel) { return frac(52.9829189 * frac(dot(pixel + _Jitter.x * 5.588238, float2(0.06711056, 0.00583715)))); }
 ENDCG

 SubShader {
  Cull Off ZWrite Off ZTest Always
  // 0: the foreground's defocus, gathered.
  Pass {
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   #pragma target 3.0
   float4 _OutSize;   // output pixels: x width, y height
   float4 frag (v2f_img i) : SV_Target {
    const int count = 96;
    float search = _Near.w;
    float turn = noise(i.uv * _OutSize.xy) * 6.2831853;
    float4 sum = 0;
    for (int k = 0; k < count; k++) {
     float2 offset = spiral(k, count, turn) * search;
     float2 uv = i.uv + offset / _OutSize.xy;
     float depth = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE_LOD(_CameraDepthTexture, float4(uv, 0, 0)));
     if (depth >= _Near.x) continue;
     float radius = min(_Near.y / max(depth, 0.001), _Near.z);
     if (length(offset) > radius) continue;
     sum += float4(tex2Dlod(_MainTex, float4(uv, 0, 0)).rgb, 1) / max(radius * radius, 1);
    }
    return sum * (search * search / count);
   }
   ENDCG
  }
  // 1: tent smoothing.
  Pass {
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   float4 frag (v2f_img i) : SV_Target {
    float2 t = _MainTex_TexelSize.xy;
    float4 s = 0;
    for (int y = -2; y <= 2; y++)
     for (int x = -2; x <= 2; x++)
      s += tex2D(_MainTex, i.uv + float2(x, y) * t) * (3 - abs(x)) * (3 - abs(y));
    return s / 81;
   }
   ENDCG
  }
  // 2: the lens map's even blur.
  Pass {
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   #pragma target 3.0
   float4 _MaskBlur;   // x radius in this texture's pixels
   float4 frag (v2f_img i) : SV_Target {
    const int count = 64;
    float turn = noise(i.uv * _MainTex_TexelSize.zw) * 6.2831853;
    float4 sum = 0;
    for (int k = 0; k < count; k++)
     sum += tex2Dlod(_MainTex, float4(i.uv + spiral(k, count, turn) * _MaskBlur.x * _MainTex_TexelSize.xy, 0, 0));
    return sum / count;
   }
   ENDCG
  }
  // 3: the eye's view.
  Pass {
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   sampler2D _Foreground, _LensMap, _Picture;
   float4x4 _PictureVP;      // the picture camera's view-projection
   float4x4 _EyeInvVP;       // this eye's normalised device coordinates to world
   float4 _EyePos;
   float4 _LensNormal[4];    // world, toward the eye; w half field (deg)
   float4 _FieldSoft;        // x field stop softness (deg)
   float4 _Glow;
   float field (float3 ray, int k) {
    float off = degrees(acos(clamp(dot(ray, -_LensNormal[k].xyz), -1, 1)));
    return 1 - smoothstep(_LensNormal[k].w - _FieldSoft.x, _LensNormal[k].w, off);
   }
   float4 frag (v2f_img i) : SV_Target {
    float4 scene = tex2D(_MainTex, i.uv);
    float4 near = tex2D(_Foreground, i.uv);
    float3 col = near.rgb + (1 - saturate(near.a)) * scene.rgb;
    float4 open = tex2D(_LensMap, i.uv);
    float4 far = mul(_EyeInvVP, float4(i.uv * 2 - 1, 1, 1));
    float3 ray = normalize(far.xyz / far.w - _EyePos.xyz);
    float light = open.x * field(ray, 0) + open.y * field(ray, 1) + open.z * field(ray, 2) + open.w * field(ray, 3);
    float4 cp = mul(_PictureVP, float4(ray, 0));
    float2 uv = cp.xy / cp.w * 0.5 + 0.5;
    float inside = step(0, cp.w) * step(0, uv.x) * step(uv.x, 1) * step(0, uv.y) * step(uv.y, 1);
    col += tex2D(_Picture, uv).rgb * inside * light + _Glow.rgb * saturate(dot(open, 1));
    return float4(col, scene.a);
   }
   ENDCG
  }
 }
}
