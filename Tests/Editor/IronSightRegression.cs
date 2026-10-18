using System;
using System.IO;
using System.Collections.Generic;
using UnityEngine;
using EFTNewCamera;

// Run in a Unity 2022.3 inspection project containing IronSightAlignment.cs.
public static class IronSightRegression
{
    [Serializable] public class Bundle { public string key, path; }
    [Serializable] public class Part { public string id, key, parent, slot; }
    [Serializable] public class Rig { public string name; public Part[] parts; }
    [Serializable] public class Plan { public string output; public Bundle[] bundles; public Rig[] rigs; }
    private static readonly List<string> Results = new();
    private static Transform Node(string name, Transform parent, Vector3 position)
    {
        var t = new GameObject(name).transform; t.SetParent(parent, false); t.localPosition = position; return t;
    }
    private static void Check(bool pass, string message) { if (!pass) throw new Exception(message); Results.Add("PASS " + message); }
    private static void Ray(Vector3 point, Vector3 forward, Transform rear, Transform front, string label)
    {
        float error = Mathf.Max(Vector3.Cross(rear.position - point, forward).magnitude, Vector3.Cross(front.position - point, forward).magnitude);
        Check(error < .00001f, label + " collinear error=" + error.ToString("R"));
    }
    private static void Synthetic()
    {
        var weapon = Node("weapon", null, Vector3.zero);
        var rear = Node("mod_align_rear", weapon, new Vector3(.01f, -.2f, .05f));
        var front = Node("mod_align_front", weapon, new Vector3(.01f, -.7f, .06f));
        var sight = Node("aim_camera", weapon, new Vector3(0, -.1f, .08f));
        IronSightAlignment.Invalidate();
        Check(IronSightAlignment.TryResolve(weapon, sight, true, false, out var p, out var f), "integrated markers resolved"); Ray(p,f,rear,front,"integrated");
        var old = sight.position;
        Check(!IronSightAlignment.TryResolve(weapon,sight,true,true,out p,out f) && p==old,"optic bypass preserves authored point");
        Check(!IronSightAlignment.TryResolve(weapon,sight,false,false,out p,out f) && p==old,"non-alignable sight bypass");
        weapon.SetPositionAndRotation(new Vector3(2,3,-4),Quaternion.Euler(32,71,17));
        Check(IronSightAlignment.TryResolve(weapon,sight,true,false,out p,out f),"rotated root resolves"); Ray(p,f,rear,front,"rotated root");
        var slot=Node("mod_sight_rear",weapon,Vector3.zero);
        var deep=Node("mod_align_rear",slot,new Vector3(.02f,-.22f,.09f));
        IronSightAlignment.Invalidate();
        Check(IronSightAlignment.TryResolve(weapon,sight,true,false,out p,out f),"attachment rebuild resolves");Ray(p,f,deep,front,"deeper attachment wins");
        deep.gameObject.SetActive(false);
        Check(IronSightAlignment.TryResolve(weapon,sight,true,false,out p,out f),"folded rear falls back");Ray(p,f,rear,front,"inactive marker excluded");
        front.gameObject.SetActive(false);
        Check(!IronSightAlignment.TryResolve(weapon,sight,true,false,out p,out f) && p==sight.position,"missing front preserves fallback");
        UnityEngine.Object.DestroyImmediate(weapon.gameObject); IronSightAlignment.Invalidate();
    }
    public static void Run()
    {
        string path=Environment.GetEnvironmentVariable("EFT_CAMERA_TEST_PLAN");
        var plan=JsonUtility.FromJson<Plan>(File.ReadAllText(path));
        Synthetic();
        var bundles=new Dictionary<string,AssetBundle>();
        foreach(var b in plan.bundles) { var ab=AssetBundle.LoadFromFile(b.path); if(ab==null) throw new Exception("Bundle failed: "+b.key); bundles[b.key]=ab; }
        foreach(var rig in plan.rigs)
        {
            var instances=new Dictionary<string,GameObject>(); GameObject root=null;
            foreach(var part in rig.parts)
            {
                GameObject prefab=null;
                foreach(var g in bundles[part.key].LoadAllAssets<GameObject>()) if(g.transform.parent==null) { if(prefab==null || g.GetComponent<WeaponPrefab>()!=null) prefab=g; }
                if(prefab==null) throw new Exception("No prefab "+part.key);
                if(root==null) { var wp=prefab.GetComponent<WeaponPrefab>(); root=UnityEngine.Object.Instantiate(wp!=null?wp._weaponObject:prefab); root.transform.SetPositionAndRotation(Vector3.zero,Quaternion.identity);instances[part.id]=root;continue; }
                Transform slot=null;
                foreach(var t in instances[part.parent].GetComponentsInChildren<Transform>(true)) if(t.name==part.slot) { slot=t; break; }
                if(slot==null) throw new Exception(rig.name+" missing slot "+part.slot);
                var go=UnityEngine.Object.Instantiate(prefab,slot,false);go.transform.localPosition=Vector3.zero;go.transform.localRotation=Quaternion.Euler(90,0,0);go.transform.localScale=Vector3.one;instances[part.id]=go;
            }
            Transform weapon=null,sight=null,rear=null,front=null;int rearDepth=-1,frontDepth=-1;
            foreach(var t in root.GetComponentsInChildren<Transform>(true))
            {
                if(t.name=="weapon") weapon=t;
                if(t.name=="aim_camera" && sight==null) sight=t;
                if(!t.gameObject.activeInHierarchy)continue;
                int depth=0;for(var a=t;a!=null;a=a.parent)depth++;
                if(t.name.Contains("mod_align_front") && depth>frontDepth) { front=t;frontDepth=depth; }
                if(t.name.Contains("mod_align_rear") && depth>rearDepth) { rear=t;rearDepth=depth; }
            }
            if(weapon==null||sight==null||front==null||rear==null) throw new Exception(rig.name+" incomplete markers");
            IronSightAlignment.Invalidate();
            Check(IronSightAlignment.TryResolve(weapon,sight,true,false,out var point,out var axis),rig.name+" resolved");
            Ray(point,axis,rear,front,rig.name);
            float relief=.2f;
            Ray(point-axis*relief,axis,rear,front,rig.name+" mounted eye");
            Results.Add("GEOMETRY "+rig.name+" rear="+rear.position+" front="+front.position+" anchor="+point);
            UnityEngine.Object.DestroyImmediate(root);IronSightAlignment.Invalidate();
        }
        foreach(var ab in bundles.Values)ab.Unload(true);
        File.WriteAllLines(plan.output,Results);Debug.Log("IRON_SIGHT_REGRESSION_PASS "+Results.Count);
    }
}
