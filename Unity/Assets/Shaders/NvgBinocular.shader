// Both eyes looking into a binocular night-vision device, side by side: the left half of the screen is the left
// eye's view, the right half the right eye's, each the main camera's view cut to that half and seen from the eye's
// own place, each eye through its own eyecup only.
//
// Each eyepiece sits close to the eye and faces it, so the eye sees each tube's whole field: the centre tube's
// straight ahead, the side tube's turned outward along its axis, the two meeting halfway between their axes at a
// thin wall, softened by the pupil (the wall's blur grows with the pupil and shrinks with its distance). The tubes
// are 1x, so every line of sight shows the picture in its own direction. The centre lens's rim still counts when
// the eye is far enough back to see it. Outside the fields the eye sees past the eyepieces: a soft dark ring where
// the eyepiece bodies are, then the scene as the main camera shows it without night vision. With _Split.x 0 the
// main camera's own frame passes through.
Shader "Justachae/NVG/Binocular" {
 Properties { _MainTex ("main camera", 2D) = "black" {} }
 SubShader {
  Cull Off ZWrite Off ZTest Always
  Pass {
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   #include "UnityCG.cginc"
   sampler2D _MainTex, _Picture;
   float4x4 _PictureVP;             // the picture camera's view-projection
   float4x4 _EyeInvVP0, _EyeInvVP1; // each eye's half view, normalised device coordinates to world
   float4 _EyePos0, _EyePos1;       // world; w the eye's centre lens
   float4 _EyeSide0, _EyeSide1;     // x the eye's side lens
   float4 _LensCentre[4];           // world centre, clear radius (m)
   float4 _LensNormal[4];           // world, toward the eye; w half field (deg)
   float4 _Soft;                    // x pupil diameter (m), y field stop softness (deg), z dark ring width (deg)
   float4 _Glow;                    // the screen's light past the field stop
   float4 _Split;                   // x 1 while both eyes are drawn

   float offAxis (float3 ray, int i) { return degrees(acos(clamp(dot(ray, -_LensNormal[i].xyz), -1, 1))); }
   float field (float off, int i, float grow) { return 1 - smoothstep(_LensNormal[i].w + grow - _Soft.y, _LensNormal[i].w + grow, off); }
   float rim (float3 eye, float3 ray, int i) {
    float3 n = _LensNormal[i].xyz;
    float along = dot(ray, n);
    if (along > -1e-4) return 0;
    float3 p = eye + ray * (dot(_LensCentre[i].xyz - eye, n) / along);
    return smoothstep(-0.5, 0.5, (_LensCentre[i].w - length(p - _LensCentre[i].xyz)) / max(_Soft.x, 1e-5));
   }

   float4 frag (v2f_img i) : SV_Target {
    float4 scene = tex2D(_MainTex, i.uv);
    if (_Split.x < 0.5) return scene;
    bool right = i.uv.x >= 0.5;
    float2 ndc = float2(i.uv.x * 2 - (right ? 1 : 0), i.uv.y) * 2 - 1;
    float4 far = mul(right ? _EyeInvVP1 : _EyeInvVP0, float4(ndc, 1, 1));
    float4 eyeData = right ? _EyePos1 : _EyePos0;
    float3 eye = eyeData.xyz;
    int c = (int)(eyeData.w + 0.5), s = (int)((right ? _EyeSide1.x : _EyeSide0.x) + 0.5);
    float3 ray = normalize(far.xyz / far.w - eye);

    float offC = offAxis(ray, c), offS = offAxis(ray, s);
    // The wall between the two lenses, blurred over the pupil as seen from the eye.
    float blur = max(degrees(_Soft.x / max(length(_LensCentre[c].xyz - eye), 0.005)), 0.5);
    float toWall = (offS - offC) * 0.5;               // degrees past the wall, toward the centre tube
    float share = smoothstep(-0.5 * blur, 0.5 * blur, toWall);
    float wall = 1 - 0.15 * (1 - smoothstep(0, 0.5 * blur, abs(toWall)));   // a ~1 mm wall over a ~5 mm pupil
    float through = wall * (share * field(offC, c, 0) * rim(eye, ray, c) + (1 - share) * field(offS, s, 0));
    float glow = wall * (share * rim(eye, ray, c) + (1 - share));
    // The eyepieces' own dark bodies just past the fields, then the naked eye's scene.
    float edgeC = _LensNormal[c].w + _Soft.z, edgeS = _LensNormal[s].w + _Soft.z;
    float body = max(1 - smoothstep(edgeC - blur, edgeC + blur, offC), 1 - smoothstep(edgeS - blur, edgeS + blur, offS));

    float4 cp = mul(_PictureVP, float4(ray, 0));
    float2 uv = cp.xy / cp.w * 0.5 + 0.5;
    float inside = step(0, cp.w) * step(0, uv.x) * step(uv.x, 1) * step(0, uv.y) * step(uv.y, 1);
    float3 col = tex2D(_Picture, uv).rgb * inside * through + _Glow.rgb * glow * body + scene.rgb * (1 - body);
    return float4(col, scene.a);
   }
   ENDCG
  }
 }
}
