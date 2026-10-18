using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using EFT.Ballistics;
using UnityEngine;

namespace EFTNewCamera;

internal static class EquivalentZeroRange
{
    private static ConfigEntry<bool> _show = null!;
    private static ConfigEntry<float> _maximum = null!, _tolerance = null!, _x = null!, _y = null!;
    private static int _frame = -1;
    private static string _text = "—";

    internal static void Initialize(ConfigFile config)
    {
        _show = config.Bind("等效归零距离", "显示估算距离", true,
            "独立显示当前瞄准射线与理论弹道的远交点，忽略墙壁碰撞。小于50米显示< 50 m，无有效交点显示—。首版以原版瞄准中心为基准，未逐镜校准尖柱/尖角。");
        _maximum = config.Bind("等效归零距离", "最大计算距离（米）", 2000f,
            new ConfigDescription("沿瞄准射线的计算范围，最长模拟20秒。", new AcceptableValueRange<float>(100f, 5000f)));
        _tolerance = config.Bind("等效归零距离", "横向重合容差（mrad）", 0.1f,
            new ConfigDescription("垂直交点还必须落在瞄准射线的横向容差内，否则不显示虚假的归零距离。", new AcceptableValueRange<float>(0.01f, 1f)));
        _x = config.Bind("等效归零距离", "基准瞄准点X", 0.5f,
            new ConfigDescription("镜内画面归一化坐标。0.5为中心；这是首版手动校准项，尚非逐镜自动识别。", new AcceptableValueRange<float>(0f, 1f)));
        _y = config.Bind("等效归零距离", "基准瞄准点Y", 0.5f,
            new ConfigDescription("镜内画面归一化坐标，0为底部、1为顶部。A类位移在此基准上叠加。", new AcceptableValueRange<float>(0f, 1f)));
    }

    internal static void Sample(Camera camera, float mrad, bool reticleMoving)
    {
        if (_show == null || !_show.Value || _frame == Time.frameCount) return;
        _frame = Time.frameCount;
        _text = "—";
        if (OpticDiagnostics.Force) return;
        Player? player = Singleton<GameWorld>.Instance?.MainPlayer;
        if (player == null || !(player.HandsController is Player.FirearmController firearm)
            || firearm.CurrentFireport?.Original == null) return;
        var ammo = firearm.Weapon.CurrentAmmoTemplate;
        if (ammo == null || ammo.BulletMassGram <= 0f || ammo.BulletDiameterMilimeters <= 0f || ammo.BallisticCoeficient <= 0f) return;
        Matrix4x4 projection = camera.nonJitteredProjectionMatrix;
        float ndcY = _y.Value * 2f - 1f;
        float shift = Mathf.Tan(mrad / 1000f) * projection.m11;
        if (reticleMoving) ndcY += shift;
        else projection.m12 -= shift;
        Vector4 point = projection.inverse * new Vector4(_x.Value * 2f - 1f, ndcY, 1f, 1f);
        if (Mathf.Abs(point.w) < 0.0000001f) return;
        Vector3 ray = camera.cameraToWorldMatrix.MultiplyVector(new Vector3(point.x, point.y, point.z) / point.w).normalized;
        Vector3 origin = camera.cameraToWorldMatrix.MultiplyPoint3x4(Vector3.zero);
        Vector3 up = Vector3.ProjectOnPlane(-Physics.gravity, ray).normalized;
        if (up.sqrMagnitude < 0.5f) return;
        Vector3 muzzle = firearm.CurrentFireport.position;
        Vector3 direction = firearm.WeaponDirection.normalized;
        AimArms.ResolvePreviewShot(firearm, ref muzzle, ref direction);
        Vector3 position = muzzle;
        Vector3 velocity = direction * (ammo.InitialSpeed * firearm.Weapon.SpeedFactor);
        float mass = ammo.BulletMassGram / 1000f;
        float diameter = ammo.BulletDiameterMilimeters / 1000f;
        float coefficient = mass * 0.0014223f / (diameter * diameter * ammo.BallisticCoeficient);
        float area = 1.2f * diameter * diameter * Mathf.PI / 4f;
        float farthest = -1f;
        const float step = 0.01f;
        for (int i = 0; i < 2000; i++)
        {
            float speed = velocity.magnitude;
            if (speed < 0.01f) break;
            float drag = Shot.CalculateG1DragCoefficient(speed) * coefficient;
            Vector3 acceleration = Physics.gravity - area * drag * speed * speed / (2f * mass) * velocity.normalized;
            Vector3 next = position + velocity * step + acceleration * (0.5f * step * step);
            if (ZeroRangeMath.Crossing(position, next, origin, ray, up, _tolerance.Value, out Vector3 crossing))
            {
                float range = Vector3.Distance(muzzle, crossing);
                if (range > farthest && range <= _maximum.Value) farthest = range;
            }
            if (Vector3.Dot(next - origin, ray) > _maximum.Value) break;
            position = next;
            velocity += acceleration * step;
        }
        _text = ZeroRangeMath.Format(farthest);
    }

    internal static void Draw()
    {
        if (_show == null || !_show.Value || !NewCameraSwitch.Enabled || _frame < Time.frameCount - 1) return;
        if (Singleton<GameWorld>.Instance?.MainPlayer?.HandsController?.IsAiming != true) return;
        GUI.Box(new Rect(Screen.width - 340f, Screen.height - 125f, 320f, 40f), "等效归零（估算）  " + _text);
    }
}
