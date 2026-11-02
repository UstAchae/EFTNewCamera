// The eyepieces' light alone (pass 0) and a wide soft blur of it, one direction at a time (pass 1), for the glow
// NvgBinocular.shader lays over the eyepieces.
Shader "Justachae/NVG/Glow" {
 Properties { _MainTex ("source", 2D) = "black" {} }
 CGINCLUDE
 #include "NvgEyes.cginc"
 ENDCG
 SubShader {
  Cull Off ZWrite Off ZTest Always
  // 0: the lenses' light alone.
  Pass {
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   #pragma target 3.0
   float4 frag (v2f_img i) : SV_Target {
    float4 eye; float4 light; float cover;
    eyeAt(i.uv, eye, light, cover);
    return float4(light.rgb, 1);
   }
   ENDCG
  }
  // 1: a wide soft blur along _Direction, in the source's texels.
  Pass {
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   float4 _Direction;
   float4 frag (v2f_img i) : SV_Target {
    static const float weight[7] = { 0.1964, 0.1748, 0.1232, 0.0688, 0.0304, 0.0106, 0.0029 };
    float3 sum = tex2D(_MainTex, i.uv).rgb * weight[0];
    for (int k = 1; k < 7; k++) {
     float2 o = _Direction.xy * _MainTex_TexelSize.xy * k * 1.5;
     sum += (tex2D(_MainTex, i.uv + o).rgb + tex2D(_MainTex, i.uv - o).rgb) * weight[k];
    }
    return float4(sum, 1);
   }
   ENDCG
  }
 }
}
