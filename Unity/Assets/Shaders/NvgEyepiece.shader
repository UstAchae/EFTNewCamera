// One eyepiece lens of a head-mounted night-vision device. The tubes are 1x and afocal, so the line from the
// viewer through any point of the lens sees the scene in that same direction: the picture is rendered from the
// viewer and looked up by the line's direction (w 0). Lines more than half the tube's field of view off its
// axis meet the field stop and stay dark apart from the screen's faint glow. Two lenses that share one eyecup
// (the "8" of a GPNVG-18) are cut at the plane halfway between their centres, where the housings meet.
Shader "Justachae/NVG/Eyepiece" {
 Properties {
  _On ("picture", Float) = 0
  _Picture ("picture", 2D) = "black" {}
  _Axis ("tube axis (world, forward), half field (deg)", Vector) = (0, 0, 1, 20)
  _Lens ("lens centre (world), clear radius (m)", Vector) = (0, 0, 0, 0.015)
  _Other ("neighbouring lens centre (world), w 1 if there is one", Vector) = (0, 0, 0, 0)
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
   float4 _Axis, _Lens, _Other, _Glow, _Base;
   struct v2f { float4 pos : SV_POSITION; float3 world : TEXCOORD0; };
   v2f vert (appdata_base v) {
    v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.world = mul(unity_ObjectToWorld, v.vertex).xyz;
    return o;
   }
   fixed4 frag (v2f i) : SV_Target {
    float3 p = i.world;
    float waist = 1;
    if (_Other.w > 0) {
     float3 apart = normalize(_Other.xyz - _Lens.xyz);
     float beyond = dot(p - (_Lens.xyz + _Other.xyz) * 0.5, apart);
     clip(-beyond);
     waist = smoothstep(0, 0.0012, -beyond);   // a thin dark seam where the two lenses meet
    }
    float rim = 1 - smoothstep(_Lens.w - 0.0012, _Lens.w, length(p - _Lens.xyz));
    float3 col = _Base.rgb;
    if (_On > 0) {
     float3 ray = normalize(p - _WorldSpaceCameraPos);
     float4 c = mul(_PictureVP, float4(ray, 0));
     float2 uv = c.xy / c.w * 0.5 + 0.5;
     float inside = step(0, c.w) * step(0, uv.x) * step(uv.x, 1) * step(0, uv.y) * step(uv.y, 1);
     float3 picture = tex2D(_Picture, uv).rgb * inside;
     float off = degrees(acos(saturate(dot(ray, _Axis.xyz))));
     float field = 1 - smoothstep(_Axis.w - 0.6, _Axis.w, off);
     col += (picture * field + _Glow.rgb) * rim * waist;
    }
    return fixed4(col, 1);
   }
   ENDCG
  }
 }
}
