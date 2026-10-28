// Replacement shader for the eye's lens map: from the eye's own place, which of the four eyepiece lenses each
// pixel looks through (one channel per lens), with everything else (the device's housing, the eyecup rims, the
// helmet) drawn black in front of them. A lens counts within its clear radius and on its own side of the seam
// it shares with its eyecup's other lens, leaving a thin wall between them.
Shader "Justachae/NVG/LensMask" {
 SubShader {
  Tags { "RenderType"="NvgLens" }
  Pass {
   CGPROGRAM
   #pragma vertex vert
   #pragma fragment frag
   #include "UnityCG.cginc"
   float4 _Lens;        // world centre, clear radius (m)
   float4 _Other;       // the eyecup's other lens centre
   float _LensIndex;
   struct v2f { float4 pos : SV_POSITION; float3 world : TEXCOORD0; };
   v2f vert (appdata_base v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.world = mul(unity_ObjectToWorld, v.vertex).xyz; return o; }
   float4 frag (v2f i) : SV_Target {
    float3 apart = normalize(_Other.xyz - _Lens.xyz);
    float beyond = dot(i.world - (_Lens.xyz + _Other.xyz) * 0.5, apart);
    float open = step(length(i.world - _Lens.xyz), _Lens.w) * step(beyond, -0.0005);
    float4 hot = float4(abs(_LensIndex - 0) < 0.5, abs(_LensIndex - 1) < 0.5, abs(_LensIndex - 2) < 0.5, abs(_LensIndex - 3) < 0.5);
    return hot * open;
   }
   ENDCG
  }
 }
 SubShader {
  Tags { "RenderType"="Opaque" }
  Pass {
   CGPROGRAM
   #pragma vertex vert
   #pragma fragment frag
   #include "UnityCG.cginc"
   float4 vert (float4 v : POSITION) : SV_POSITION { return UnityObjectToClipPos(v); }
   float4 frag () : SV_Target { return 0; }
   ENDCG
  }
 }
 SubShader {
  Tags { "RenderType"="TransparentCutout" }
  Pass {
   CGPROGRAM
   #pragma vertex vert
   #pragma fragment frag
   #include "UnityCG.cginc"
   float4 vert (float4 v : POSITION) : SV_POSITION { return UnityObjectToClipPos(v); }
   float4 frag () : SV_Target { return 0; }
   ENDCG
  }
 }
}
