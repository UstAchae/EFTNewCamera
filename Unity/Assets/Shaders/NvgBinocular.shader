// Side by side: the left eye's camera fills the left half of the screen, the right eye's the right half, each
// its own full view. With _Split.x 0 the main camera's own frame passes through.
Shader "Justachae/NVG/Binocular" {
 Properties { _MainTex ("main camera", 2D) = "black" {} }
 SubShader {
  Cull Off ZWrite Off ZTest Always
  Pass {
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   #include "UnityCG.cginc"
   sampler2D _MainTex, _LeftEye, _RightEye;
   float4 _Split;   // x 1 while both eyes have a view
   float4 frag (v2f_img i) : SV_Target {
    if (_Split.x < 0.5) return tex2D(_MainTex, i.uv);
    return i.uv.x < 0.5 ? tex2D(_LeftEye, float2(i.uv.x * 2, i.uv.y)) : tex2D(_RightEye, float2(i.uv.x * 2 - 1, i.uv.y));
   }
   ENDCG
  }
 }
}
