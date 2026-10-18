using System;
using System.IO;
using System.Linq;
using UnityEngine;
using EFTNewCamera;
public static class NativeReticleReferenceAudit
{
 public static void Run()
 {
  ZeroRangeRegression.Run();
  var shaders=AssetBundle.LoadFromFile("D:/games/SPT-4.1.6/EscapeFromTarkov_Data/StreamingAssets/Windows/shaders");
  var opticShader=shaders.LoadAllAssets<Shader>().First(s=>s.name=="CW FX/OpticSight");
  string root="D:/games/SPT-4.1.6/EscapeFromTarkov_Data/StreamingAssets/Windows/assets/content/items/mods/scopes/";
  string[] files={"scope_base_progress_pu_3,5x.bundle","scope_dovetail_belomo_pso_1_4x24.bundle","scope_dovetail_belomo_pso_1m2_1_4x24.bundle","scope_dovetail_belomo_pso_1m2_4x24.bundle"};
  string[] templates={"5b3f7c1c5acfc40dc5296b1d","5c82342f2e221644f31c060e","576fd4ec2459777f0b518431","5c82343a2e221644f31c0611"};
  string report="";
  foreach(string file in files)
  {
   var bundle=AssetBundle.LoadFromFile(root+file);
   bool found=false;
   foreach(var prefab in bundle.LoadAllAssets<GameObject>())
   foreach(var lens in prefab.GetComponentsInChildren<Renderer>(true))
   {
    var mats=lens.sharedMaterials;
    for(int slot=0;slot<mats.Length;slot++)
    {
     var material=mats[slot];
     if(material==null || !material.name.EndsWith("_linza"))continue;
     material.shader=opticShader;
     var mesh=lens.GetComponent<MeshFilter>()?.sharedMesh;
     if(mesh==null)throw new Exception(file+" no reticle mesh");
     Vector3 perV;
     if(!SightReferenceMath.NativeVertical(templates[Array.IndexOf(files,file)],mesh.name,mesh.vertexCount,mesh.bounds.size,out perV))throw new Exception(file+" native fallback geometry rejected");
     var scales=material.GetVector("_Scales");
     Vector3 delta=SightReferenceMath.LegacyTranslation(lens.localToWorldMatrix,perV,scales.x,.1f);
     if(delta.sqrMagnitude<1e-12f)throw new Exception(file+" no world displacement");
     report+=file+" readable="+mesh.isReadable+" verifiedFallback=True perV="+perV.ToString("F7")+" scales="+scales+" deltaForTextureV0.1="+delta.ToString("F7")+"\n";found=true;
    }
   }
   if(!found)throw new Exception(file+" no reticle material");
  }
  File.WriteAllText("D:/games/SPT-4.1.6/.codex-work/EFTNewCamera-source/_build/native-reference-audit.txt",report);
  Debug.Log("PASS native A reticle geometry audit\n"+report);
 }
}
