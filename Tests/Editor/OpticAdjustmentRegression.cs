using System;
using System.IO;
using EFTNewCamera;
using UnityEngine;

public static class OpticAdjustmentRegression
{
    public static void Run()
    {
        int passed=0;
        Action<bool,string> check=(ok,label)=>{if(!ok)throw new Exception(label);passed++;};
        check(OpticAdjustmentMath.Next(0,1,.05f)==0,"cannot move above initial position");
        check(OpticAdjustmentMath.Next(0,-1,.05f)==-.05f,"first downward step");
        check(OpticAdjustmentMath.Next(-2.99f,-60,.05f)==-3,"held key clamps at lower boundary");
        check(OpticAdjustmentMath.Next(-.01f,60,.05f)==0,"held return clamps at initial position");
        check(OpticAdjustmentMath.Next(-3,-1,1)==-3,"cannot move below minus three");
        check(OpticAdjustmentMath.Next(-3,1,.05f)>-3,"can return from bottom limit");
        float value=0;
        for(int i=0;i<20000;i++)value=OpticAdjustmentMath.Next(value,i<10000?-1:1,.05f);
        check(value==0,"repeated down/up returns exactly to initial position");
        File.WriteAllText("D:/games/SPT-4.1.6/.codex-work/EFTNewCamera-source/_build/optic-adjustment-regression.txt","PASS "+passed+" adjustment assertions");
        Debug.Log("PASS "+passed+" adjustment assertions");
    }
}
