// Both eyes' views through a binocular night-vision device in one frame. The left eye's view (the main camera's)
// fills the left part of the screen and the right eye's the right part; where they overlap, the brighter wins, as
// the eyes fuse two pictures that each show one lens: the left eye's centre lens and the right eye's centre lens
// both sit in the middle of their views and merge into one circle, the side lenses stay on their own sides.
Shader "Justachae/NVG/Binocular" {
 Properties { _MainTex ("left eye", 2D) = "black" {} }
 SubShader {
  Cull Off ZWrite Off ZTest Always
  Pass {
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   #include "UnityCG.cginc"
   sampler2D _MainTex, _RightEye;
   float4 _Split;   // x where the left view ends, y where the right view starts, z soft edge (screen fractions)
   float4 frag (v2f_img i) : SV_Target {
    float4 left = tex2D(_MainTex, i.uv), right = tex2D(_RightEye, i.uv);
    float leftWeight = 1 - smoothstep(_Split.x - _Split.z, _Split.x, i.uv.x);
    float rightWeight = smoothstep(_Split.y, _Split.y + _Split.z, i.uv.x);
    return max(left * leftWeight, right * rightWeight);
   }
   ENDCG
  }
 }
}
