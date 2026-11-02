using EFT;
using EFT.Animations;
using EFT.Ballistics;
using EFT.CameraControl;
using EFT.Interactive;
using EFT.InventoryLogic;
using UnityEngine;

namespace EFTNewCamera;

// The AGS-17 grenade launcher's sight: the sight body stays level and only yaws, and the picture inside it looks at
// where the round will land.
internal static class GrenadeSight
{
    internal static bool InHands(Player player)
    {
        return StationaryHeld(player, out StationaryWeapon? weapon)
            && weapon!.Animation == StationaryWeapon.EStationaryAnimationType.AGS_17;
    }

    private static bool StationaryHeld(Player player, out StationaryWeapon? weapon)
    {
        weapon = null;
        if (player.HandsController == null || player.MovementContext == null || !player.MovementContext.IsStationaryWeaponInHands)
            return false;
        weapon = player.MovementContext.StationaryWeapon;
        return weapon != null;
    }

    // The launcher sight stays level. The picture inside it has to look at the same point the round will reach,
    // using the barrel pose after the body has been moved. The optic camera already copied its pivot earlier this frame.
    internal static void AimPicture(Player player)
    {
        if (!InHands(player) || player.HandsController is not Player.FirearmController firearm || !firearm.IsAiming)
            return;
        try
        {
            ProceduralWeaponAnimation? animation = player.ProceduralWeaponAnimation;
            if (animation == null || firearm.Weapon == null || firearm.CurrentFireport?.Original == null)
                return;

            Vector3 direction = firearm.WeaponDirection;
            if (direction.sqrMagnitude < 0.0001f)
                return;
            direction.Normalize();
            Vector3 start = firearm.CurrentFireport.position - direction * firearm.WeaponLn / 5f;
            if (!GrenadeLanding(player, firearm, start, direction, out Vector3 landing))
                return;

            OpticSight? sight = Scopes.Current(animation)?.ScopePrefabCache?.CurrentModOpticSight;
            Transform? pivot = sight?.ScopeData != null ? sight.ScopeData.transform : null;
            Camera? optic = CameraManager.Instance?.OpticCameraManager?.Camera;
            if (sight == null || pivot == null)
                return;

            try
            {
                animation.OpticLookAtGlobal(landing);
            }
            catch (System.Exception)
            {
            }

            if (Vector3.Angle(pivot.forward, landing - pivot.position) > 0.05f)
                sight.LookAt(landing, firearm.CurrentFireport.Original.forward);
            if (optic != null)
                optic.transform.SetPositionAndRotation(pivot.position, pivot.rotation);
        }
        catch (System.Exception)
        {
        }
    }

    private const float GrenadeStep = 0.01f;
    private const int GrenadeStepsPerCast = 4;
    private const float GrenadeFlightSeconds = 13f;
    private static readonly RaycastHit[] GrenadeHits = new RaycastHit[16];

    // Steps the round the same way the game does (G1 drag plus gravity, 10 ms steps) and returns the first
    // thing it would hit. The player and the launcher itself are ignored.
    private static bool GrenadeLanding(Player player, Player.FirearmController firearm, Vector3 start, Vector3 direction, out Vector3 landing)
    {
        landing = default;
        AmmoTemplate? ammo = firearm.Weapon.CurrentAmmoTemplate;
        if (ammo == null || ammo.BulletMassGram <= 0f || ammo.BulletDiameterMilimeters <= 0f || ammo.BallisticCoeficient <= 0f)
            return false;

        float massKg = ammo.BulletMassGram / 1000f;
        float diameterM = ammo.BulletDiameterMilimeters / 1000f;
        float coefficient = massKg * 0.0014223f / (diameterM * diameterM * ammo.BallisticCoeficient);
        float slowdown = 1.2f * diameterM * diameterM * Mathf.PI / 4f;
        Vector3 gravity = Physics.gravity;
        Vector3 position = start;
        Vector3 velocity = direction * (ammo.InitialSpeed * firearm.Weapon.SpeedFactor);
        Transform? launcher = player.MovementContext?.StationaryWeapon != null ? player.MovementContext.StationaryWeapon.transform : null;
        int mask = BallisticsCalculatorConstants.HitMask;

        Vector3 castFrom = position;
        int steps = Mathf.CeilToInt(GrenadeFlightSeconds / GrenadeStep);
        for (int i = 1; i <= steps; i++)
        {
            float speed = velocity.magnitude;
            float drag = EFT.Ballistics.Shot.CalculateG1DragCoefficient(speed) * coefficient;
            Vector3 acceleration = gravity + slowdown * (0f - drag) * speed * speed / (2f * massKg) * velocity.normalized;
            position += velocity * GrenadeStep + 5E-05f * acceleration;
            velocity += acceleration * GrenadeStep;
            if (i % GrenadeStepsPerCast != 0 && i != steps)
                continue;

            Vector3 segment = position - castFrom;
            float length = segment.magnitude;
            if (length > 0.0001f)
            {
                int count = Physics.RaycastNonAlloc(castFrom, segment / length, GrenadeHits, length, mask, QueryTriggerInteraction.Ignore);
                float best = float.MaxValue;
                for (int h = 0; h < count; h++)
                {
                    RaycastHit hit = GrenadeHits[h];
                    if (hit.collider == null || hit.distance >= best)
                        continue;
                    Transform owner = hit.collider.transform;
                    if (owner.GetComponentInParent<Player>() == player || (launcher != null && owner.IsChildOf(launcher)))
                        continue;
                    best = hit.distance;
                    landing = hit.point;
                }
                if (best < float.MaxValue)
                    return true;
            }
            castFrom = position;
        }

        return false;
    }
}
