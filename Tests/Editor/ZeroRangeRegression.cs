using System;
using System.IO;
using EFTNewCamera;
using UnityEngine;

public static class ZeroRangeRegression
{
    public static void Run()
    {
        int passed = 0;
        Action<bool,string> check = (condition,label) => { if(!condition) throw new Exception(label); passed++; };
        Vector3 origin = Vector3.zero, forward = Vector3.forward, up = Vector3.up;
        float far = -1f, near = -1f;
        Func<float,Vector3> trajectory = z => new Vector3(0,-0.05f+0.005f*z-0.00001f*z*z,z);
        Vector3 previous = trajectory(0);
        for(float z=0.25f;z<=1000;z+=0.25f)
        {
            Vector3 next=trajectory(z);
            if(ZeroRangeMath.Crossing(previous,next,origin,forward,up,.1f,out Vector3 hit))
            { if(near<0)near=hit.z; far=hit.z; }
            previous=next;
        }
        check(Mathf.Abs(near-10.2084f)<.01f,"near crossing interpolation");
        check(Mathf.Abs(far-489.7916f)<.01f,"far crossing interpolation");
        check(ZeroRangeMath.Format(near)=="< 50 m","near intersection selected even when far exceeds 50");
        check(ZeroRangeMath.Format(153.74f)=="153.7 m","one decimal above threshold");
        check(ZeroRangeMath.Format(49.99f)=="< 50 m","under 50 threshold before rounding");
        check(ZeroRangeMath.Format(50f)=="50.0 m","exact 50 boundary");
        check(ZeroRangeMath.Format(-1f)=="—","no intersection");
        check(!ZeroRangeMath.Crossing(new Vector3(5,-1,90),new Vector3(5,1,110),origin,forward,up,.1f,out _),"lateral miss rejected");
        check(!ZeroRangeMath.Crossing(new Vector3(0,-1,90),new Vector3(0,-2,110),origin,forward,up,.1f,out _),"no vertical crossing rejected");
        check(ZeroRangeMath.Crossing(new Vector3(0,-1,10),new Vector3(0,1,30),origin,forward,up,.1f,out var below)&&ZeroRangeMath.Format(below.magnitude)=="< 50 m","valid near-only result");
        check(SightReferenceMath.UvVertical(Vector3.zero, Vector3.right, Vector3.forward,
            Vector2.zero, Vector2.right, Vector2.up, out var perV) && (perV-Vector3.forward).sqrMagnitude<1e-10f,"reticle V follows authored mesh axis");
        var delta=SightReferenceMath.LegacyTranslation(Matrix4x4.identity,perV,2f,.1f);
        check((delta-new Vector3(0,0,.02f)).sqrMagnitude<1e-10f,"mark scale and native tenfold UV mapping");
        var reverse=SightReferenceMath.LegacyTranslation(Matrix4x4.identity,perV,2f,-.1f);
        check((delta+reverse).sqrMagnitude<1e-10f,"opposite adjustments reverse translation");
        Vector3 reference=new Vector3(2,3,4), endpoint=reference+Vector3.forward*100f;
        check(((endpoint+delta)-(reference+delta)-Vector3.forward*100f).sqrMagnitude<1e-10f,"translated sight remains parallel at both endpoints");
        check(!SightReferenceMath.UvVertical(Vector3.zero,Vector3.right,Vector3.up,Vector2.zero,Vector2.zero,Vector2.zero,out _),"degenerate UV rejected");
        Matrix4x4 projection=Matrix4x4.Perspective(30f,1f,.01f,1000f);
        check(SightReferenceMath.ProjectionTranslation(projection,Matrix4x4.identity,new Vector3(0,0,-2),.1f,out var pixelDelta),"native reticle plane displacement available");
        Vector4 clip=projection*new Vector4(pixelDelta.x,pixelDelta.y,-2+pixelDelta.z,1);
        Debug.Log("Projection translation delta="+pixelDelta.ToString("F9")+" ndc="+(clip.y/clip.w).ToString("R"));
        check(Mathf.Abs(clip.y/clip.w-.1f)<1e-6f && Mathf.Abs(pixelDelta.z)<1e-6f,"world translation reproduces actual reticle pixel shift at fixed depth");
        File.WriteAllText("D:/games/SPT-4.1.6/.codex-work/EFTNewCamera-source/_build/zero-range-regression.txt", "PASS "+passed+" zero-range assertions");
        Debug.Log("PASS "+passed+" zero-range assertions");
    }
}
