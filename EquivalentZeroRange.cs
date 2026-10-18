using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using EFT.Ballistics;
using EFT.CameraControl;
using UnityEngine;
using System.Collections.Generic;

namespace EFTNewCamera;

internal static class EquivalentZeroRange
{
    private static ConfigEntry<bool> _show = null!;
    private static ConfigEntry<float> _maximum = null!, _tolerance = null!;
    private static int _frame = -1;
    private static string _text = ZeroRangeMath.FormatPair(-1f, -1f, 0);
    private static ConfigEntry<bool> _debug = null!;
    internal static readonly List<Vector3> DebugTrajectory = new(2001);
    internal static readonly List<Vector3> DebugCrossings = new(4);
    internal static readonly List<Vector3> CandidateCrossings = new(4);
    private static string _status = "等待瞄准采样";
    private static GUIStyle? _readout;
    internal static Vector3 DebugOrigin, DebugDirection, NativeOrigin, NativeDirection, DebugZeroPoint, DebugAimPoint;
    internal static string ObserverSource = "unavailable";
    internal static int ObserverCameraId;
    internal static bool DebugActive => _debug != null && _debug.Value && DebugTrajectory.Count > 1 && _frame >= Time.frameCount - 1;

    internal static void Initialize(ConfigFile config)
    {
        _debug = config.Bind("等效归零距离", "显示瞄准基准与交点", true,
            "紫线从实际观察镜头出发，穿过当前分划点；分划移动时射线绕观察位置转动。原生轴只用于标定零位分划点。绿叉为该射线与理论弹道的交点。黄色虚线延伸未碰撞理论弹道以便观察交点，红叉仍是实际首次碰撞。仅调试绘制，不改弹道。");
        _show = config.Bind("等效归零距离", "显示估算距离", true,
            "同时显示紫色瞄准射线与理论弹道的近、远交点，忽略墙壁碰撞。各交点小于50米显示< 50 m，缺失交点显示—。原生节点标定零位分划点，当前射线从观察镜头穿过调节后的分划点；B类计入镜内场景投影。");
        _maximum = config.Bind("等效归零距离", "最大计算距离（米）", 2000f,
            new ConfigDescription("沿瞄准射线的计算范围，最长模拟20秒。", new AcceptableValueRange<float>(100f, 5000f)));
        _tolerance = config.Bind("等效归零距离", "横向重合容差（mrad）", 0.1f,
            new ConfigDescription("垂直交点还必须落在瞄准射线的横向容差内，否则不显示虚假的归零距离。", new AcceptableValueRange<float>(0.01f, 1f)));

    }

    internal static void Sample(Camera camera, float mrad, bool reticleMoving)
    {
        if (_show == null || (!_show.Value && !_debug.Value && !BallisticProbe.Enabled) || _frame == Time.frameCount) return;
        _frame = Time.frameCount;
        _text = ZeroRangeMath.FormatPair(-1f, -1f, 0);
        DebugTrajectory.Clear(); DebugCrossings.Clear(); CandidateCrossings.Clear();
        DebugOrigin = DebugDirection = NativeOrigin = NativeDirection = DebugZeroPoint = DebugAimPoint = Vector3.zero;
        ObserverSource = "unavailable"; ObserverCameraId = 0;
        _status = "未取得有效数据";
        try { SampleCore(camera, mrad, reticleMoving); }
        finally { BallisticProbe.Reference(camera, mrad, reticleMoving, _status); }
    }

    private static void SampleCore(Camera camera, float mrad, bool reticleMoving)
    {
        if (OpticDiagnostics.Force) { _status="强制投影测试开启，暂停求交"; return; }
        Player? player = Singleton<GameWorld>.Instance?.MainPlayer;
        if (player == null || !(player.HandsController is Player.FirearmController firearm)
            || firearm.CurrentFireport?.Original == null) { _status="枪械或枪口不可用"; return; }
        var ammo = firearm.Weapon.CurrentAmmoTemplate;
        if (ammo == null || ammo.BulletMassGram <= 0f || ammo.BulletDiameterMilimeters <= 0f || ammo.BallisticCoeficient <= 0f) { _status="弹药参数不可用"; return; }
        var bone = player.ProceduralWeaponAnimation?.CurrentScope?.Bone;
        if (bone == null) { _status="原生瞄准节点不可用"; return; }
        NativeOrigin = bone.position;
        NativeDirection = (bone.name == "aim_camera" ? -bone.up : -bone.forward).normalized;
        Camera observer = camera;
        Vector3 observerPosition = camera.cameraToWorldMatrix.MultiplyPoint3x4(Vector3.zero);
        Vector3 observerForward = camera.transform.forward;
        Vector3 zeroPoint, point;
        Matrix4x4 projection = camera.nonJitteredProjectionMatrix;
        float shift = Mathf.Tan(mrad / 1000f) * projection.m11;
        var reticle = CameraManager.Instance?.OpticCameraManager?.OpticRetrice;
        bool legacy = reticleMoving && reticle?.CanUpdate != true;
        if (legacy)
        {
            observer = Camera.main;
            if (observer == null) { _status="主观察镜头不可用"; return; }
            // Optic rendering can precede the main camera's pre-cull. Its Transform still contains
            // the game's pre-override pose then. Use the exact pose NewCameraSwitch will render.
            if (!NewCameraSwitch.TryObserverPose(out observerPosition,out Quaternion observerRotation))
            { _status="本帧最终观察姿态尚未就绪，等待下一帧"; return; }
            observerForward = observerRotation*Vector3.forward;
            if (!LegacyReticle.TryGetAimPoint(NativeOrigin, NativeDirection, out zeroPoint, out point))
            { _status="无法取得旧式分划平面和当前分划点"; return; }
            ObserverSource = "final-head-pose / native-axis calibrated legacy mark plane";
        }
        else
        {
            Renderer? renderer = reticle?.CanUpdate == true ? reticle.Renderer
                : CameraManager.Instance?.OpticCameraManager?.CurrentOpticSight?.LensRenderer;
            if (renderer == null || !SightReferenceMath.AxisPlanePoint(NativeOrigin,NativeDirection,
                renderer.bounds.center,camera.transform.forward,out zeroPoint))
            { _status="无法取得镜内分划基准平面"; return; }
            // A moves the mark upward. B moves the scene upward, hence the world target at the
            // stationary mark moves downward. Both now define an aim POINT before forming the ray.
            float ndcShift = reticleMoving ? shift : -shift;
            if (!SightReferenceMath.ProjectionTranslation(projection,camera.cameraToWorldMatrix,
                zeroPoint,ndcShift,out Vector3 displacement))
            { _status="分划点投影换算失败"; return; }
            point = zeroPoint+displacement;
            ObserverSource = reticleMoving ? "optic-eye / projected moving mark" : "optic-eye / stationary mark with scene projection";
        }
        Vector3 origin = observerPosition;
        DebugZeroPoint = zeroPoint; DebugAimPoint = point; ObserverCameraId = observer.GetInstanceID();
        DebugOrigin = origin;
        if (!SightReferenceMath.EyeRay(origin,point,observerForward,out Vector3 ray))
        { _status="分划点在观察镜头后方或与镜头重合，停止输出错误距离"; return; }
        DebugOrigin = origin; DebugDirection = ray;
        OpticDiagnostics.Trace("reference.eye", camera, $"source={ObserverSource} observerId={ObserverCameraId} reticleMoving={reticleMoving} mrad={mrad:R} eye={origin.ToString("F6")} zeroPoint={zeroPoint.ToString("F6")} aimPoint={point.ToString("F6")} axis={ray.ToString("F6")}");
        Vector3 up = Vector3.ProjectOnPlane(-Physics.gravity, ray).normalized;
        if (up.sqrMagnitude < 0.5f) { _status="瞄准轴接近重力方向，垂直求交基准退化"; return; }
        Vector3 muzzle = firearm.CurrentFireport.Original.position;
        Vector3 direction = firearm.WeaponDirection.normalized;
        AimArms.ResolvePreviewShot(firearm, ref muzzle, ref direction);
        Vector3 position = muzzle;
        DebugTrajectory.Add(position);
        Vector3 velocity = direction * (ammo.InitialSpeed * firearm.Weapon.SpeedFactor);
        float mass = ammo.BulletMassGram / 1000f;
        float diameter = ammo.BulletDiameterMilimeters / 1000f;
        float coefficient = mass * 0.0014223f / (diameter * diameter * ammo.BallisticCoeficient);
        float area = 1.2f * diameter * diameter * Mathf.PI / 4f;
        float nearest = -1f, farthest = -1f;
        const float step = 0.01f;
        for (int i = 0; i < 2000; i++)
        {
            float speed = velocity.magnitude;
            if (speed < 0.01f) break;
            float drag = Shot.CalculateG1DragCoefficient(speed) * coefficient;
            Vector3 acceleration = Physics.gravity - area * drag * speed * speed / (2f * mass) * velocity.normalized;
            Vector3 next = position + velocity * step + acceleration * (0.5f * step * step);
            DebugTrajectory.Add(next);
            if (ZeroRangeMath.Crossing(position, next, origin, ray, up, 1000000f, out Vector3 candidate)
                && Vector3.Distance(muzzle,candidate)<=_maximum.Value
                && (CandidateCrossings.Count==0 || Vector3.Distance(CandidateCrossings[CandidateCrossings.Count-1],candidate)>.02f)) CandidateCrossings.Add(candidate);
            if (ZeroRangeMath.Crossing(position, next, origin, ray, up, _tolerance.Value, out Vector3 crossing))
            {
                float range = Vector3.Distance(muzzle, crossing);
                if (range <= _maximum.Value)
                {
                    if (DebugCrossings.Count == 0 || Vector3.Distance(DebugCrossings[DebugCrossings.Count - 1], crossing) > 0.02f) DebugCrossings.Add(crossing);
                    if (nearest < 0f || range < nearest) nearest = range;
                    if (range > farthest) farthest = range;
                }
            }
            if (Vector3.Dot(next - origin, ray) > _maximum.Value) break;
            position = next;
            velocity += acceleration * step;
        }
        _text = ZeroRangeMath.FormatPair(nearest, farthest, DebugCrossings.Count);
        _status = _text.Replace("\n", " | ") + $" | 垂直候选 {CandidateCrossings.Count} / 三维有效 {DebugCrossings.Count}";
    }

    internal static void Draw()
    {
        if (_show == null || !_show.Value || !NewCameraSwitch.Enabled || _frame < Time.frameCount - 1) return;
        if (Singleton<GameWorld>.Instance?.MainPlayer?.HandsController?.IsAiming != true) return;
        _readout ??= new GUIStyle(GUI.skin.label) { fontSize=15,alignment=TextAnchor.UpperLeft,wordWrap=false };
        _readout.normal.textColor=Color.white;
        Rect box = new Rect(Screen.width-380f,190f,360f,92f);
        GUI.Box(box,"");
        GUI.Label(new Rect(box.x+10,box.y+5,340f,22f),"等效归零（估算）／当前紫线",_readout);
        GUI.Label(new Rect(box.x+10,box.y+30,340f,55f),_text,_readout);
    }
}
