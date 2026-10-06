using System.Collections.Generic;
using EFT;
using EFT.Animations;
using UnityEngine;

namespace EFTNewCamera;

internal static class Recoil
{
    internal static float Strength = 0.3f;

    internal static Quaternion CameraKick(Player player)
    {
        if (!Shown(player))
            return Quaternion.identity;

        Vector3 recoil = Read(player).CameraEuler * Mathf.Clamp01(Strength);
        return recoil.sqrMagnitude < 0.0000001f ? Quaternion.identity : Quaternion.Euler(recoil);
    }

    internal static void KickWeapon(Player player)
    {
        if (!Shown(player))
            return;

        Transform? weapon = player.PlayerBones?.Weapon_Root_Anim;
        if (weapon == null)
            return;

        Vanilla native = Read(player);
        float strength = Mathf.Clamp01(Strength);
        Vector3 euler = native.HandEuler * strength;
        Vector3 offset = native.HandOffset * strength;
        if (euler.sqrMagnitude < 0.0000001f && offset.sqrMagnitude < 0.0000001f)
            return;

        Quaternion rotation = weapon.rotation;
        Vector3 position = weapon.position;
        Quaternion worldDelta = Quaternion.Euler(rotation * euler);
        Vector3 worldOffset = rotation * offset;
        Vector3 pivot = position + rotation * Pivot(player);
        List<Transform> arms = Upperarms(player);
        bool underArm = false;
        foreach (Transform arm in arms)
        {
            if (weapon.IsChildOf(arm))
            {
                underArm = true;
                break;
            }
        }

        if (!underArm)
        {
            weapon.rotation = worldDelta * rotation;
            weapon.position = pivot + worldDelta * (position - pivot) + worldOffset;
        }

        foreach (Transform arm in arms)
        {
            arm.position = pivot + worldDelta * (arm.position - pivot) + worldOffset;
            arm.rotation = worldDelta * arm.rotation;
        }
    }

    private static bool Shown(Player? player) => player != null && player.IsYourPlayer;

    private static Vector3 Pivot(Player player)
    {
        try
        {
            ProceduralWeaponAnimation? animation = player.ProceduralWeaponAnimation;
            if (animation?.HandsContainer == null)
                return Vector3.zero;

            return animation.IsMountedState
                ? !animation.IsBipodUsed || animation.IsVerticalMounting
                    ? animation.HandsContainer.MountingRotationCenter
                    : animation.HandsContainer.MountingRotationCenterBipods
                : animation.HandsContainer.RecoilPivot;
        }
        catch
        {
            return Vector3.zero;
        }
    }

    private readonly struct Vanilla
    {
        internal readonly Vector3 CameraEuler;
        internal readonly Vector3 HandEuler;
        internal readonly Vector3 HandOffset;

        internal Vanilla(Vector3 cameraEuler, Vector3 handEuler, Vector3 handOffset)
        {
            CameraEuler = cameraEuler;
            HandEuler = handEuler;
            HandOffset = handOffset;
        }
    }

    private static Vanilla Read(Player player)
    {
        try
        {
            ShotEffector? shooting = player.ProceduralWeaponAnimation?.Shootingg;
            if (shooting?.CurrentRecoilEffect == null)
                return default;

            Vector3 cameraEuler = shooting.CurrentRecoilEffect.GetCameraRotationRecoil();
            Vector3 handEuler = shooting.CurrentRecoilEffect.GetHandRotationRecoil();
            Vector3 handOffset = shooting.CurrentRecoilEffect.GetHandPositionRecoil();
            WeaponRecoil? weaponRecoil = shooting.CurrentRecoilEffect.WeaponRecoilEffect;
            if (weaponRecoil != null)
            {
                cameraEuler += weaponRecoil.GetCameraRotationRecoil();
                handEuler += weaponRecoil.GetRotationRecoil();
                handOffset += weaponRecoil.GetPositionRecoil();
            }

            return new Vanilla(cameraEuler, handEuler, handOffset);
        }
        catch
        {
            return default;
        }
    }

    private static List<Transform> Upperarms(Player player)
    {
        List<Transform> arms = new();
        foreach (SkinnedMeshRenderer skin in ((Component)player).GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (skin.bones == null)
                continue;

            foreach (Transform bone in skin.bones)
            {
                if (bone == null || !IsUpperarm(bone.name) || arms.Contains(bone))
                    continue;

                arms.Add(bone);
            }
        }

        return arms;
    }

    private static bool IsUpperarm(string name)
    {
        string normalized = name.Replace(" ", string.Empty).Replace("_", string.Empty);
        return normalized.IndexOf("upperarm", System.StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
