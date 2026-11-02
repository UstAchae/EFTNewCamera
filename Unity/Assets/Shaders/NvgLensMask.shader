// The eye's lens map: from the eye's own place, which of the four tubes each pixel looks into (one channel per tube).
// Pass 0 draws the device's meshes black. Pass 1 draws over each eyecup, worked out per line of sight rather than from
// the meshes, so its edges are true circles: each lens is a circle painted on its eyepiece's floor, square to its axis
// (its centre may sit a little off it, and two lenses of an eyecup may differ in size). A line of sight that reaches
// the floor within a lens, having come in through the eyecup's opening (a circle round the axis, or round both of an
// eyecup's lenses and the straight sides between them; at the axis points and again at the top of the rim), sees that
// lens; within two, the one it is further inside. Both edges are antialiased: the share of the pixel inside. Within a
// lens the light falls off toward its rim, as an eyepiece's does, and a line of sight only finds the picture where it
// passes through the eyepiece's exit pupil (a disc on the axis, an eye relief out): seen from off to one side, part of
// the lens goes dark. The pass is drawn on a card over the opening, just above the rim, so the rim's own low-poly edge
// does not cut it but anything in front of it still hides it.
Shader "Justachae/NVG/LensMask" {
 SubShader {
  Cull Off
  Pass {
   CGPROGRAM
   #pragma vertex vert
   #pragma fragment frag
   #include "UnityCG.cginc"
   float4 vert (float4 v : POSITION) : SV_POSITION { return UnityObjectToClipPos(v); }
   float4 frag () : SV_Target { return 0; }
   ENDCG
  }
  Pass {
   CGPROGRAM
   #pragma vertex vert
   #pragma fragment frag
   #pragma target 3.0
   #include "UnityCG.cginc"
   float4 _Lens0, _Lens1, _Lens2, _Lens3;   // each lens's axis where it crosses the plate (world), xyz; w its radius on the floor (m)
   float4 _LensFloor0, _LensFloor1, _LensFloor2, _LensFloor3;   // each lens's centre on the floor (world)
   float4 _Axis0, _Axis1, _Axis2, _Axis3;   // each lens's axis, toward the eye (world)
   float4 _Eye;                             // where the lens map is drawn from (world)
   float4 _Opening;                         // x the opening's radius at the axis points, y at the rim, z how far the rim rises above them (m)
   float4 _Optics;                          // x eye relief from the plate, y exit pupil radius, z its soft edge (m); w the light left at a lens's rim
   struct v2f { float4 pos : SV_POSITION; float3 world : TEXCOORD0; };
   v2f vert (appdata_base v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.world = mul(unity_ObjectToWorld, v.vertex).xyz; return o; }
   // Where the line of sight crosses the plane through a point square to an axis.
   float3 cross_plane (float3 ray, float3 at, float3 axis) { return _Eye.xyz + ray * (dot(at - _Eye.xyz, axis) / dot(ray, axis)); }
   float on_floor (float3 ray, float4 floorCentre, float4 axis) { return distance(cross_plane(ray, floorCentre.xyz, axis.xyz), floorCentre.xyz); }
   float to_segment (float3 p, float3 a, float3 b) {   // an eyecup with one lens: a and b the same point
    float3 ab = b - a;
    float length2 = dot(ab, ab);
    return distance(p, a + ab * (length2 > 1e-12 ? saturate(dot(p - a, ab) / length2) : 0));
   }
   float4 frag (v2f i) : SV_Target {
    float3 ray = normalize(i.world - _Eye.xyz);
    float4 d = float4(on_floor(ray, _LensFloor0, _Axis0), on_floor(ray, _LensFloor1, _Axis1), on_floor(ray, _LensFloor2, _Axis2), on_floor(ray, _LensFloor3, _Axis3));
    // How far inside each lens's edge (the lenses of an "8" may differ in size); the deepest one is seen.
    float4 inside = float4(_Lens0.w, _Lens1.w, _Lens2.w, _Lens3.w) - d;
    float deepest = max(max(inside.x, inside.y), max(inside.z, inside.w));
    float4 tube = inside == deepest ? 1 : 0;
    tube /= max(dot(tube, 1), 1);
    bool first = tube.x + tube.y > 0;
    float4 centre = first ? _Lens0 : _Lens2, side = first ? _Lens1 : _Lens3;
    float4 lens = tube.x * _Lens0 + tube.y * _Lens1 + tube.z * _Lens2 + tube.w * _Lens3;
    float3 axis = tube.x * _Axis0.xyz + tube.y * _Axis1.xyz + tube.z * _Axis2.xyz + tube.w * _Axis3.xyz;
    float3 centreAxis = first ? _Axis0.xyz : _Axis2.xyz, sideAxis = first ? _Axis1.xyz : _Axis3.xyz;
    float atPlate = to_segment(cross_plane(ray, lens.xyz, axis), centre.xyz, side.xyz);
    float atRim = to_segment(cross_plane(ray, lens.xyz + axis * _Opening.z, axis), centre.xyz + centreAxis * _Opening.z, side.xyz + sideAxis * _Opening.z);
    float cover = saturate(deepest / max(fwidth(deepest), 1e-7) + 0.5)
     * saturate((_Opening.x - atPlate) / max(fwidth(atPlate), 1e-7) + 0.5) * saturate((_Opening.y - atRim) / max(fwidth(atRim), 1e-7) + 0.5);
    clip(cover - 0.001);
    float rim = smoothstep(0.55, 1.0, dot(tube, d) / lens.w);
    float pupil = distance(cross_plane(ray, lens.xyz + axis * _Optics.x, axis), lens.xyz + axis * _Optics.x);
    cover *= lerp(1, _Optics.w, rim * rim) * smoothstep(_Optics.y + _Optics.z, _Optics.y - _Optics.z, pupil);
    return tube * cover;
   }
   ENDCG
  }
 }
}
