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
        File.WriteAllText("D:/games/SPT-4.1.6/.codex-work/EFTNewCamera-source/_build/zero-range-regression.txt", "PASS "+passed+" zero-range assertions");
        Debug.Log("PASS "+passed+" zero-range assertions");
    }
}
