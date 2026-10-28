// One eyepiece lens of a head-mounted night-vision device. The tubes are 1x and afocal, so the line from the
// viewer through any point of the lens sees the scene in that same direction: the picture is rendered from the
// viewer and looked up by the line's direction (w 0). Lines more than half the tube's field of view off its
// axis meet the field stop and stay dark apart from the screen's faint glow.
//
// Edges near the eye are as soft as the pupil makes them: the eye focuses far away, so an edge covers the pupil
// gradually while the line of sight crosses a band as wide as the pupil at the edge. That holds for the lens rim
// and for the seam where two lenses share one eyecup (the "8" of a GPNVG-18): across the seam the pupil sees
// through both lenses at once, each with its own tube's field, and the thin wall between them dims it a little.
// Each lens draws its own side of the seam, so the two halves meet at the same value.
Shader "Justachae/NVG/Eyepiece" {
 Properties {
  _On ("picture", Float) = 0
  _Picture ("picture", 2D) = "black" {}
  _Axis ("tube axis (world, forward), half field (deg)", Vector) = (0, 0, 1, 20)
  _Lens ("lens centre (world), clear radius (m)", Vector) = (0, 0, 0, 0.015)
  _Other ("neighbouring lens centre (world), w 1 if there is one", Vector) = (0, 0, 0, 0)
  _OtherAxis ("neighbouring tube's axis (world, forward)", Vector) = (0, 0, 1, 0)
  _Soft ("x pupil diameter (m), y field stop softness (deg)", Vector) = (0.005, 1.5, 0, 0)
  _Glow ("screen glow past the field stop", Color) = (0, 0, 0, 0)
  _Base ("glass", Color) = (0.004, 0.005, 0.006, 1)
 }
 SubShader {
  // Drawn after the opaque queue: the game's screen-space effects read the G-buffer, which this forward shader
  // does not write, and in the opaque queue they painted whatever lies behind the lens onto it.
  Tags { "Queue"="Transparent-1" "RenderType"="Opaque" }
  Cull Off
  Pass {
   CGPROGRAM
   #pragma vertex vert
   #pragma fragment frag
   #include "UnityCG.cginc"
   float _On;
   sampler2D _Picture;
   float4x4 _PictureVP;      // the picture camera's view-projection
   float4 _Axis, _Lens, _Other, _OtherAxis, _Soft, _Glow, _Base;
   struct v2f { float4 pos : SV_POSITION; float3 world : TEXCOORD0; };
   v2f vert (appdata_base v) {
    v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.world = mul(unity_ObjectToWorld, v.vertex).xyz;
    return o;
   }
   // How much of the pupil sees past an edge, d the signed distance inside it.
   float pupilLinear (float d) { return smoothstep(-0.5, 0.5, d / max(_Soft.x, 1e-5)); }
   float rim (float3 p, float3 centre) { return pupilLinear(_Lens.w - length(p - centre)); }
   float field (float3 ray, float3 axis) {
    float off = degrees(acos(saturate(dot(ray, axis))));
    return 1 - smoothstep(_Axis.w - _Soft.y, _Axis.w, off);
   }
   fixed4 frag (v2f i) : SV_Target {
    float3 p = i.world;
    float3 ray = normalize(p - _WorldSpaceCameraPos);
    float own = rim(p, _Lens.xyz);
    float through = own * field(ray, _Axis.xyz);    // light reaching the pupil through the lenses
    float screen = own;
    if (_Other.w > 0) {
     float3 apart = normalize(_Other.xyz - _Lens.xyz);
     float beyond = dot(p - (_Lens.xyz + _Other.xyz) * 0.5, apart);
     clip(-beyond);
     float side = pupilLinear(-beyond);              // 0.5 on the seam, 1 a pupil's half width inside
     float other = rim(p, _Other.xyz);
     through = side * through + (1 - side) * other * field(ray, _OtherAxis.xyz);
     screen = side * own + (1 - side) * other;
     float wall = 1 - 0.2 * (1 - smoothstep(0, _Soft.x * 0.5, abs(beyond)));
     through *= wall;
     screen *= wall;
    }
    float3 col = _Base.rgb;
    if (_On > 0) {
     float4 c = mul(_PictureVP, float4(ray, 0));
     float2 uv = c.xy / c.w * 0.5 + 0.5;
     float inside = step(0, c.w) * step(0, uv.x) * step(uv.x, 1) * step(0, uv.y) * step(uv.y, 1);
     col += tex2D(_Picture, uv).rgb * inside * through + _Glow.rgb * screen;
    }
    return fixed4(col, 1);
   }
   ENDCG
  }
 }
}
