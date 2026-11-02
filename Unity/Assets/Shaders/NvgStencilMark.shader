// Marks in the stencil, as the game's own G-buffer pass does for what it draws, the pixels of meshes the eyes put
// into their G-buffer themselves (NightVisionPrecise.cs), so the lights fall on them: deferred lights shade only the
// pixels so marked. Nothing else is written; drawn where the mesh already lies (depth equal).
Shader "Justachae/NVG/StencilMark" {
 SubShader {
  Pass {
   Cull Off ZWrite Off ZTest LEqual ColorMask 0
   Stencil { Ref [_StencilNonBackground] WriteMask [_StencilNonBackground] Comp Always Pass Replace }
   CGPROGRAM
   #pragma vertex vert
   #pragma fragment frag
   #include "UnityCG.cginc"
   float4 vert (float4 v : POSITION) : SV_POSITION { return UnityObjectToClipPos(v); }
   fixed4 frag () : SV_Target { return 0; }
   ENDCG
  }
 }
}
