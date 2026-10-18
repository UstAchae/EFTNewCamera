using System;
using System.IO;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Newtonsoft.Json;
using UnityEngine;

namespace EFTNewCamera;

// Two independent observations of the existing calculations, never a replacement shot solver.
internal sealed class BallisticProbe : MonoBehaviour
{
    private static ConfigEntry<bool> _record = null!, _side = null!;
    private static ManualLogSource _log = null!;
    private static StreamWriter? _writer;
    private static bool _failed;
    private static string _file = "", _scope = "", _status = "等待瞄准及调整密位";
    private static float _until, _nextReference, _nextOptic, _nextMain, _offset;
    private static int _records, _shotFrame = -1, _sightFrame = -1;
    private static Vector3 _muzzle, _shotAxis, _origin, _ray;
    private static Vector3[] _solid = Array.Empty<Vector3>(), _theory = Array.Empty<Vector3>(), _hits = Array.Empty<Vector3>();
    private static GUIStyle? _label;
    private static float _maxX = 100f;

    internal void Initialize(ConfigFile config, ManualLogSource log)
    {
        _log = log;
        _record = config.Bind("密位双探针", "记录密位调整", true,
            "每次调节密位后记录2秒，最长每秒10组。分别记录实际黄线采样和紫线求交输入，含帧号、世界坐标、相机矩阵、候选及有效交点。保存到BepInEx/ballistic-probes，不修改弹道。");
        _side = config.Bind("密位双探针", "显示侧视演示", true,
            "侧视窗口使用探针记录的位置，显示黄线和紫线、交点及失败原因。横纵轴分别自动缩放，明确标注真实米数；侧视重合不代表三维相交。");
    }

    internal static bool Enabled => _record != null && (_record.Value || _side.Value);
    internal static void Adjust(string scope, float before, float after)
    {
        if (!Enabled) return;
        _scope = scope; _offset = after; _until = Time.unscaledTime+2f;
        Write(new { kind="adjust", frame=Time.frameCount, time=Time.unscaledTime, scope, beforeMrad=before, afterMrad=after });
    }

    private static float[] V(Vector3 p) => new[] {p.x,p.y,p.z};
    private static float[][] Points(IList<Vector3> p)
    {
        var result = new float[p.Count][];
        for(int i=0;i<p.Count;i++) result[i]=V(p[i]);
        return result;
    }
    private static float[] Matrix(Matrix4x4 matrix)
    {
        var result=new float[16];
        for(int row=0;row<4;row++) for(int col=0;col<4;col++) result[row*4+col]=matrix[row,col];
        return result;
    }

    internal static void Trajectory(Camera camera, Vector3 rawMuzzle, Vector3 muzzle, Vector3 direction, Vector3[] points, int count)
    {
        if (!Enabled) return;
        bool optic=OpticPicture.IsCamera(camera);
        float now=Time.unscaledTime;
        if(now<(optic?_nextOptic:_nextMain)) return;
        if(optic)_nextOptic=now+.1f;else _nextMain=now+.1f;
        _muzzle=muzzle; _shotAxis=direction; _shotFrame=Time.frameCount;
        _solid=new Vector3[count]; Array.Copy(points,_solid,count);
        if(now<=_until) Write(new {kind="trajectory",phase=optic?"optic-pre-render":"main-GUI",frame=_shotFrame,time=now,
            scope=_scope,rawMuzzle=V(rawMuzzle),muzzle=V(muzzle),direction=V(direction),path=Points(_solid),
            cameraId=camera.GetInstanceID(),cameraPosition=V(camera.transform.position),view=Matrix(camera.worldToCameraMatrix),projection=Matrix(camera.projectionMatrix)});
    }

    internal static void Reference(Camera camera, float mrad, bool moving, string status)
    {
        if(!Enabled || Time.unscaledTime<_nextReference)return;
        _nextReference=Time.unscaledTime+.1f;
        _offset=mrad; _status=status; _sightFrame=Time.frameCount;
        _origin=EquivalentZeroRange.DebugOrigin; _ray=EquivalentZeroRange.DebugDirection;
        _theory=EquivalentZeroRange.DebugTrajectory.ToArray(); _hits=EquivalentZeroRange.DebugCrossings.ToArray();
        var candidates=new List<object>();
        foreach(var hit in EquivalentZeroRange.CandidateCrossings)
        {
            Vector3 relative=hit-_origin;
            float along=Vector3.Dot(relative,_ray), miss=(relative-along*_ray).magnitude;
            candidates.Add(new {position=V(hit),range=Vector3.Distance(_muzzle,hit),along,lateralMissMeters=miss});
        }
        if(Time.unscaledTime<=_until) Write(new {kind="reference",frame=_sightFrame,time=Time.unscaledTime,scope=_scope,mrad,moving,status,
            trajectoryFrame=_shotFrame,nativeOrigin=V(EquivalentZeroRange.NativeOrigin),nativeAxis=V(EquivalentZeroRange.NativeDirection),
            observerCameraId=EquivalentZeroRange.ObserverCameraId,observerSource=EquivalentZeroRange.ObserverSource,
            zeroAimPoint=V(EquivalentZeroRange.DebugZeroPoint),aimPoint=V(EquivalentZeroRange.DebugAimPoint),
            origin=V(_origin),axis=V(_ray),theory=Points(_theory),crossings=Points(_hits),candidates,
            cameraId=camera.GetInstanceID(),cameraPosition=V(camera.transform.position),view=Matrix(camera.worldToCameraMatrix),projection=Matrix(camera.nonJitteredProjectionMatrix)});
    }

    internal static void Overlay(Camera camera, bool excluded, Rect rect)
    {
        if(!Enabled || Time.unscaledTime>_until || Time.frameCount%30!=0)return;
        Write(new {kind="overlay",frame=Time.frameCount,time=Time.unscaledTime,cameraId=camera.GetInstanceID(),excluded,
            rect=new[]{rect.x,rect.y,rect.width,rect.height},screen=new[]{Screen.width,Screen.height}});
    }

    private static void Write(object row)
    {
        if(_record==null || !_record.Value || _failed)return;
        try
        {
            if(_writer==null)
            {
                string folder=Path.Combine(Path.GetDirectoryName(BepInEx.Paths.ConfigPath)!,"ballistic-probes"); Directory.CreateDirectory(folder);
                _file=Path.Combine(folder,"probe-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff")+".jsonl");
                _writer=new StreamWriter(_file,false,new System.Text.UTF8Encoding(false));
                _log.LogInfo("[BallisticProbe] recording "+_file);
            }
            if(_records>=12000 || _writer.BaseStream.Position>=64L*1024*1024){_status="已达记录上限，请重启游戏建立新记录";return;}
            _writer.WriteLine(JsonConvert.SerializeObject(row)); _writer.Flush(); _records++;
        }
        catch(Exception e){_failed=true;_status="记录失败："+e.Message;_log.LogError("[BallisticProbe] "+e);}
    }

    private static Vector2 Side(Vector3 p, Vector3 right, Vector3 up) => new Vector2(Vector3.Dot(p-_muzzle,right),Vector3.Dot(p-_muzzle,up));
    private static Vector2 Plot(Vector2 p,Rect rect,float maxX,float minY,float maxY) => new Vector2(rect.x+p.x/maxX*rect.width,rect.yMax-(p.y-minY)/(maxY-minY)*rect.height);
    private static void Line(Vector2 a,Vector2 b,Color color)
    {
        Matrix4x4 old=GUI.matrix;Color saved=GUI.color; Vector2 d=b-a;
        GUI.color=color;GUIUtility.RotateAroundPivot(Mathf.Atan2(d.y,d.x)*Mathf.Rad2Deg,a);
        GUI.DrawTexture(new Rect(a.x,a.y-.7f,d.magnitude,1.4f),Texture2D.whiteTexture);GUI.matrix=old;GUI.color=saved;
    }
    private void OnGUI()
    {
        if(_side==null || !_side.Value || Event.current.type!=EventType.Repaint || !NewCameraSwitch.Enabled)return;
        if(Comfort.Common.Singleton<EFT.GameWorld>.Instance?.MainPlayer?.HandsController?.IsAiming!=true)return;
        _label ??= new GUIStyle(GUI.skin.label){fontSize=13,wordWrap=false,alignment=TextAnchor.UpperLeft,clipping=TextClipping.Clip};
        _label.normal.textColor=Color.white;
        float width=Mathf.Min(740f,Screen.width-40f),height=290f;
        Rect panel=new Rect(20f,Screen.height-height-35f,width,height);
        GUI.Box(panel,"");
        GUI.Label(new Rect(panel.x+10,panel.y+5,width-20,22),$"密位双探针 | {_offset:F3} mrad | 黄线帧 {_shotFrame} / 紫线帧 {_sightFrame} | 记录 {_records}",_label);
        GUI.Label(new Rect(panel.x+10,panel.y+27,width-20,22),_status,_label);
        if(_theory.Length<2 || _shotAxis.sqrMagnitude<.5f)return;
        Vector3 up=(-Physics.gravity).normalized, right=Vector3.ProjectOnPlane(_shotAxis,up).normalized;
        if(right.sqrMagnitude<.5f)return;
        _maxX=100f;
        if(_hits.Length>0) foreach(var hit in _hits)_maxX=Mathf.Max(_maxX,Side(hit,right,up).x*1.15f);
        else _maxX=Mathf.Min(1000f,Mathf.Max(100f,Side(_theory[_theory.Length-1],right,up).x));
        Vector2 o=Side(_origin,right,up);
        float rx=Vector3.Dot(_ray,right),ry=Vector3.Dot(_ray,up);
        float rayEnd=o.y+(Mathf.Abs(rx)>1e-6f?(_maxX-o.x)*ry/rx:0f);
        float minY=Mathf.Min(0f,Mathf.Min(o.y,rayEnd)),maxY=Mathf.Max(.01f,Mathf.Max(o.y,rayEnd));
        foreach(var p in _theory){var s=Side(p,right,up);if(s.x>=0 && s.x<=_maxX){minY=Mathf.Min(minY,s.y);maxY=Mathf.Max(maxY,s.y);}}
        float pad=Mathf.Max(.01f,(maxY-minY)*.08f);minY-=pad;maxY+=pad;
        Rect plot=new Rect(panel.x+65,panel.y+70,width-95,height-130);
        GUI.Label(new Rect(panel.x+8,panel.y+50,width-16,20),$"侧视：横向 0–{_maxX:F1} m；纵向 {minY:F3}–{maxY:F3} m（横纵比例不同）",_label);
        GUI.BeginGroup(plot);
        Rect local=new Rect(0,0,plot.width,plot.height);
        int stride=Mathf.Max(1,_theory.Length/300);
        for(int i=stride;i<_theory.Length;i+=stride)Line(Plot(Side(_theory[i-stride],right,up),local,_maxX,minY,maxY),Plot(Side(_theory[i],right,up),local,_maxX,minY,maxY),new Color(1,1,0,.45f));
        stride=Mathf.Max(1,_solid.Length/180);
        for(int i=stride;i<_solid.Length;i+=stride)Line(Plot(Side(_solid[i-stride],right,up),local,_maxX,minY,maxY),Plot(Side(_solid[i],right,up),local,_maxX,minY,maxY),Color.yellow);
        Line(Plot(o,local,_maxX,minY,maxY),Plot(new Vector2(_maxX,rayEnd),local,_maxX,minY,maxY),new Color(.86f,.39f,1f));
        foreach(var hit in _hits){Vector2 p=Plot(Side(hit,right,up),local,_maxX,minY,maxY);Line(p-Vector2.right*5,p+Vector2.right*5,Color.green);Line(p-Vector2.up*5,p+Vector2.up*5,Color.green);}
        GUI.EndGroup();
        GUI.Label(new Rect(panel.x+10,panel.yMax-52,width-20,22),"黄=实线弹道；淡黄=理论延长；紫=求交射线；绿=有效交点。侧视重合不等于三维相交。",_label);
        GUI.Label(new Rect(panel.x+10,panel.yMax-30,width-20,22),$"有效交点 {_hits.Length}；数据：BepInEx/ballistic-probes/（调节后自动记录2秒）",_label);
    }
    private void OnDestroy(){_writer?.Dispose();_writer=null;}
}
