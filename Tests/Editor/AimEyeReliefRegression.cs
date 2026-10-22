using System;
using System.IO;
using EFTNewCamera;
using UnityEngine;

public static class AimEyeReliefRegression
{
    public static void Run()
    {
        int count=0;
        Action<bool,string> check=(ok,label)=>{if(!ok)throw new Exception(label);count++;};
        float bone=.1299681134f,localY=-.0237100162f;
        float old=bone-.16f,corrected=AimEyeReliefMath.Resolve(bone,.37f,bone,localY);
        check(Mathf.Abs(old+.0300318866f)<1e-6f,"old default-plane result from assembled Mosin/1P69 assets");
        check(Mathf.Abs(corrected+.0237100162f)<1e-6f,"native far-plane cap includes weapon local offset");
        check(Mathf.Abs((corrected-old)-.0063218704f)<1e-6f,"asset-based correction moves away by about 6.3 mm");
        check(Mathf.Abs(AimEyeReliefMath.Resolve(.13f,.1f,.13f,0)-.03f)<1e-6f,"selected custom aim plane is respected");
        check(Mathf.Abs(AimEyeReliefMath.Resolve(.13f,.37f,.1f,0)-.03f)<1e-6f,"rearmost optic limits far plane");
        check(Mathf.Abs(AimEyeReliefMath.Resolve(.13f,.37f,.11f,0)-.02f)<1e-6f,"eyecup shift reflected in optical plane");
        File.WriteAllText("D:/games/SPT-4.1.6/.codex-work/EFTNewCamera-source/_build/eye-relief-regression.txt","PASS "+count+" eye-relief assertions");
        Debug.Log("PASS "+count+" eye-relief assertions");
    }
}
