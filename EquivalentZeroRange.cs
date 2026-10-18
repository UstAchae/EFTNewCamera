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
    internal static Vector3 DebugOrigin, DebugDirection, NativeOrigin, NativeDirection;
    internal static bool DebugActive => _debug != null && _debug.Value && DebugTrajectory.Count > 1 && _frame >= Time.frameCount - 1;

    internal static void Initialize(ConfigFile config)
    {
        _debug = config.Bind("等效归零距离", "显示瞄准基准与交点", true,
            "紫线以游戏原生瞄准节点轴为基准；A类跟随实际分划位移平移，绿叉为同一条线与理论弹道的交点。黄色虚线延伸未碰撞理论弹道以便观察交点，红叉仍是实际首次碰撞。仅调试绘制，不改弹道。");
        _show = config.Bind("等效归零距离", "显示估算距离", true,
            "同时显示紫色瞄准射线与理论弹道的近、远交点，忽略墙壁碰撞。各交点小于50米显示< 50 m，缺失交点显示—。以游戏原生瞄准节点为零位；A类按分划位移平移基准，B类计入镜内画面投影调节。");
        _maximum = config.Bind("等效归零距离", "最大计算距离（米）", 2000f,
            new ConfigDescription("沿瞄准射线的计算范围，最长模拟20秒。", new AcceptableValueRange<float>(100f, 5000f)));
        _tolerance = config.Bind("等效归零距离", "横向重合容差（mrad）", 0.1f,
            new ConfigDescription("垂直交点还必须落在瞄准射线的横向容差内，否则不显示虚假的归零距离。", new AcceptableValueRange<float>(0.01f, 1f)));

    }

    internal static void Sample(Camera camera, float mrad, bool reticleMoving)
    {
        if (_show == null || (!_show.Value && !_debug.Value) || _frame == Time.frameCount) return;
        _frame = Time.frameCount;
        _text = ZeroRangeMath.FormatPair(-1f, -1f, 0);
        DebugTrajectory.Clear(); DebugCrossings.Clear();
        if (OpticDiagnostics.Force) return;
        Player? player = Singleton<GameWorld>.Instance?.MainPlayer;
        if (player == null || !(player.HandsController is Player.FirearmController firearm)
            || firearm.CurrentFireport?.Original == null) return;
        var ammo = firearm.Weapon.CurrentAmmoTemplate;
        if (ammo == null || ammo.BulletMassGram <= 0f || ammo.BulletDiameterMilimeters <= 0f || ammo.BallisticCoeficient <= 0f) return;
        var bone = player.ProceduralWeaponAnimation?.CurrentScope?.Bone;
        if (bone == null) return;
        NativeOrigin = bone.position;
        NativeDirection = (bone.name == "aim_camera" ? -bone.up : -bone.forward).normalized;
        Vector3 origin = NativeOrigin, ray = NativeDirection;
        Matrix4x4 projection = camera.nonJitteredProjectionMatrix;
        float shift = Mathf.Tan(mrad / 1000f) * projection.m11;
        if (reticleMoving && Mathf.Abs(mrad) > 0.000001f)
        {
            Vector3 translation;
            var reticle = CameraManager.Instance?.OpticCameraManager?.OpticRetrice;
            if (reticle?.CanUpdate == true && reticle.Renderer != null)
            {
                if (!SightReferenceMath.ProjectionTranslation(projection, camera.cameraToWorldMatrix,
                    reticle.Renderer.bounds.center, shift, out translation)) return;
            }
            else if (!LegacyReticle.TryGetTranslation(out translation))
            {
                OpticDiagnostics.Trace("reference.unavailable", camera, "No readable reticle geometry; distance suppressed rather than using an arbitrary displacement.");
                return;
            }
            // Translate the native reference by exactly the rendered mark's displacement. Never turn its axis.
            origin += translation;
        }
        else if (!reticleMoving && Mathf.Abs(mrad) > 0.000001f)
        {
            // B keeps the mark stationary and changes the scene projection. Apply only that angular delta
            // to the native reference, preserving the native zero instead of assuming optic-camera centre.
            Matrix4x4 inverse = projection.inverse;
            Vector4 before = inverse * new Vector4(0f, 0f, 1f, 1f);
            Matrix4x4 moved = projection; moved.m12 -= shift;
            Vector4 after = moved.inverse * new Vector4(0f, 0f, 1f, 1f);
            Vector3 a = camera.cameraToWorldMatrix.MultiplyVector(new Vector3(before.x, before.y, before.z));
            Vector3 b = camera.cameraToWorldMatrix.MultiplyVector(new Vector3(after.x, after.y, after.z));
            ray = (Quaternion.FromToRotation(a, b) * ray).normalized;
        }
        DebugOrigin = origin; DebugDirection = ray;
        OpticDiagnostics.Trace("reference.native", camera, $"reticleMoving={reticleMoving} mrad={mrad:R} translation={origin-NativeOrigin} origin={origin} axis={ray}");
        Vector3 up = Vector3.ProjectOnPlane(-Physics.gravity, ray).normalized;
        if (up.sqrMagnitude < 0.5f) return;
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
    }

    internal static void Draw()
    {
        if (_show == null || !_show.Value || !NewCameraSwitch.Enabled || _frame < Time.frameCount - 1) return;
        if (Singleton<GameWorld>.Instance?.MainPlayer?.HandsController?.IsAiming != true) return;
        GUI.Box(new Rect(Screen.width - 340f, Screen.height - 160f, 320f, 72f), "等效归零（估算）\n" + _text);
    }
}
