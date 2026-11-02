// One eye's view of a head-mounted night-vision device, after its camera has drawn what is within half a metre.
// Pass 0: that, solid where it was drawn, premultiplied with its coverage in alpha, to go over the main camera's
// scene. The light through the lenses is put in afterwards (NvgBinocular.shader).
// Pass 1: resolved over frames where the game dithers (its glass, drawn on some pixels and not their neighbours, in
// a pattern that changes every frame and counts on being evened out), and nowhere else: nothing follows motion
// back, so on a solid surface the frames before would only smear what slides over it, the reflections as one
// turns. The previous result is kept within the range of this frame's neighbourhood besides.
Shader "Justachae/NVG/EyeOptics" {
 Properties { _MainTex ("source", 2D) = "black" {} }
 SubShader {
  Cull Off ZWrite Off ZTest Always
  Pass {
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   #include "UnityCG.cginc"
   sampler2D _MainTex;
   UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);
   float4 frag (v2f_img i) : SV_Target {
    // Drawn: nearer than the eye camera's far plane, where it was cleared.
    float depth = Linear01Depth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.uv));
    return depth < 0.999 ? float4(tex2D(_MainTex, i.uv).rgb, 1) : 0;
   }
   ENDCG
  }
  Pass {
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   #include "UnityCG.cginc"
   sampler2D _MainTex, _History;
   float4 _MainTex_TexelSize;
   float _HistoryWeight;
   float4 frag (v2f_img i) : SV_Target {
    // The history kept within this frame's neighbourhood: its mean and spread (variance clipping), so a dither
    // pattern, whose pixels swing across that spread from frame to frame, settles on its average.
    float4 current = tex2D(_MainTex, i.uv);
    float4 sum = 0, squares = 0;
    float drawnMin = 1, drawnMax = 0;
    for (int y = -1; y <= 1; y++)
     for (int x = -1; x <= 1; x++) {
      float4 s = tex2D(_MainTex, i.uv + float2(x, y) * _MainTex_TexelSize.xy);
      sum += s;
      squares += s * s;
      drawnMin = min(drawnMin, s.a);
      drawnMax = max(drawnMax, s.a);
     }
    float4 mean = sum / 9;
    float4 spread = sqrt(max(squares / 9 - mean * mean, 0)) * 1.25;
    float4 history = clamp(tex2D(_History, i.uv), mean - spread, mean + spread);
    // Dithered: drawn here and not next to it (or the other way round); a solid surface is drawn all about.
    float dithered = saturate(drawnMax - drawnMin);
    return lerp(current, history, _HistoryWeight * dithered);
   }
   ENDCG
  }
 }
}
