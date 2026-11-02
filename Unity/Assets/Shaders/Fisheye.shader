// The frame, rendered straight (rectilinear) with a wide enough field, laid out as a fisheye lens would: a point at an
// angle theta off the centre of view lands at r = f tan(k theta) / k from the screen's centre. k = 1 is the straight
// picture itself, k = 1/2 the stereographic fisheye; in between it eases from one to the other. Distances are in
// half screen heights. The straight frame's field is chosen so that the fisheye's corners land on its corners, so
// nothing past its edges is ever asked for.
Shader "Justachae/Fisheye" {
 Properties { _MainTex ("", 2D) = "black" {} }
 SubShader {
  Pass {
   Cull Off ZWrite Off ZTest Always
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   #include "UnityCG.cginc"
   sampler2D _MainTex;
   float4 _Fisheye;   // x k, y f, z the straight frame's half height (as the tangent of its half field), w the aspect

   float4 frag (v2f_img i) : SV_Target {
    float2 q = (i.uv * 2 - 1) * float2(_Fisheye.w, 1);
    float r = length(q);
    if (r < 1e-5) return tex2D(_MainTex, i.uv);
    float theta = atan(_Fisheye.x * r / _Fisheye.y) / _Fisheye.x;
    float2 straight = q * (tan(theta) / (r * _Fisheye.z));
    return tex2D(_MainTex, saturate(straight / float2(_Fisheye.w, 1) * 0.5 + 0.5));
   }
   ENDCG
  }
 }
}
