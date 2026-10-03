using System.Collections.Generic;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.Animations;
using EFT.CameraControl;
using EFT.Settings;
using HarmonyLib;
using UnityEngine;

namespace EFTNewCamera;

internal static class AimArms
{
    private struct SkinState
    {
        public SkinnedMeshRenderer Renderer;
        public bool ForceMatrix;
        public bool UpdateWhenOffscreen;
    }

    private const float ArmCarrySettleSeconds = 0.2f;

    private static bool _suppress;
    private static bool _patched;
    private static bool _replayingLaser;
    private static bool _hasLaserDisplacement;
    private static Vector3 _laserDisplacement;
    private static MethodInfo? _laserLateUpdate;
    private static readonly List<MonoBehaviour> DeferredLasers = new();
    private static int _skinOwnerId;
    private static readonly List<Transform> Upperarms = new();
    private static readonly List<SkinState> Skins = new();
    private static bool _hasArmCarry;
    private static int _armCarrySightId;
    private static float _armCarryTime;
    private static Vector3 _armCarryOffset;

    internal static void Install()
    {
        if (_patched)
            return;

        MethodInfo? target = AccessTools.Method(typeof(PlayerAnimator), nameof(PlayerAnimator.SetAiming));
        MethodInfo? prefix = typeof(AimArms).GetMethod(nameof(SetAimingPrefix), BindingFlags.NonPublic | BindingFlags.Static);
        Harmony harmony = new Harmony(NewCameraPlugin.PluginGuid + ".aimarms");
        if (target != null && prefix != null)
            harmony.Patch(target, prefix: new HarmonyMethod(prefix));
        PatchLaser(harmony);
        PatchFieldOfView(harmony);
        _patched = true;
    }

    private static void PatchFieldOfView(Harmony harmony)
    {
        MethodInfo? setFov = AccessTools.Method(typeof(CameraManager), nameof(CameraManager.SetFov), new[] { typeof(float), typeof(float), typeof(bool) });
        MethodInfo? prefix = typeof(AimArms).GetMethod(nameof(SetFovPrefix), BindingFlags.NonPublic | BindingFlags.Static);
        if (setFov == null || prefix == null)
            return;

        harmony.Patch(setFov, prefix: new HarmonyMethod(prefix));
    }

    private static void SetFovPrefix(ref float x)
    {
        if (!NewCameraSwitch.Enabled || !Singleton<SettingsManager>.Instantiated)
            return;

        x = Singleton<SettingsManager>.Instance.Game.Settings.FieldOfView.Value;
    }

    private static void PatchLaser(Harmony harmony)
    {
        System.Type? laserType = AccessTools.TypeByName("LaserBeam");
        _laserLateUpdate = laserType == null ? null : AccessTools.Method(laserType, "LateUpdate");
        MethodInfo? laserPrefix = typeof(AimArms).GetMethod(nameof(LaserLateUpdatePrefix), BindingFlags.NonPublic | BindingFlags.Static);
        if (_laserLateUpdate == null || laserPrefix == null)
            return;

        harmony.Patch(_laserLateUpdate, prefix: new HarmonyMethod(laserPrefix));
    }

    internal static void Apply(Player player)
    {
        _suppress = true;
        player.MovementContext?.PlayerAnimator?.SetAiming(false);
    }

    internal static void AlignToCamera(Player player, Vector3 cameraPosition)
    {
        if (player.HandsController == null || !player.HandsController.IsAiming)
        {
            _hasLaserDisplacement = false;
            _hasArmCarry = false;
            _armCarrySightId = 0;
            _armCarryTime = 0f;
            return;
        }

        ProceduralWeaponAnimation animation = player.ProceduralWeaponAnimation;
        if (animation == null || animation.ScopeAimTransforms == null || animation.ScopeAimTransforms.Count == 0)
            return;

        Transform? bone = animation.CurrentScope?.Bone;
        PlayerBones bones = player.PlayerBones;
        if (bone == null || bones == null)
            return;

        Vector3 displacement = cameraPosition - VanillaAimPoint(animation, bone);
        int sightId = bone.GetInstanceID();
        if (!_hasArmCarry || sightId != _armCarrySightId)
        {
            _hasArmCarry = true;
            _armCarrySightId = sightId;
            _armCarryTime = 0f;
        }
        else
        {
            _armCarryTime += Time.deltaTime;
        }

        displacement = CarryOnArm(bones, displacement, _armCarryTime < ArmCarrySettleSeconds);
        _laserDisplacement = displacement;
        _hasLaserDisplacement = true;
        EnsureUpperarms(player);
        for (int i = Upperarms.Count - 1; i >= 0; i--)
        {
            Transform arm = Upperarms[i];
            if (arm == null)
            {
                Upperarms.RemoveAt(i);
                continue;
            }

            for (int j = 0; j < Upperarms.Count; j++)
            {
                Transform other = Upperarms[j];
                if (i != j && other != null && arm.IsChildOf(other))
                {
                    Upperarms.RemoveAt(i);
                    break;
                }
            }
        }

        foreach (Transform arm in Upperarms)
        {
            if (arm != null)
                arm.position += displacement;
        }

        Transform? weapon = bones.Weapon_Root_Anim;
        if (weapon == null)
            return;
        foreach (Transform arm in Upperarms)
        {
            if (arm != null && weapon.IsChildOf(arm))
                return;
        }

        weapon.position += displacement;
    }

    private static Vector3 VanillaAimPoint(ProceduralWeaponAnimation animation, Transform bone)
    {
        Transform? weaponRoot = animation.HandsContainer != null ? animation.HandsContainer.WeaponRoot : null;
        Transform? parent = weaponRoot != null ? weaponRoot.parent : null;
        if (parent == null || animation.HandsContainer == null || animation.HandsContainer.Weapon == null)
            return bone.position;

        Vector3 local = parent.InverseTransformPoint(bone.position);
        WeaponPrefab.AimPlane? plane = animation._currentAimingPlane;
        float fovCompensation = animation._fovCompensatoryDistance;
        if (animation.CurrentScope != null && animation.CurrentScope.IsOptic)
        {
            if (animation.HandsContainer.DefaultAimPlane != null)
                plane = animation.HandsContainer.DefaultAimPlane;
            fovCompensation = (1f - animation.Scale.y) * 0.075f;
        }

        if (plane != null && animation.AimPointParent != null)
        {
            float depth = plane.Depth;
            WeaponPrefab.AimPlane? far = animation._farPlane;
            if (far != null)
                depth = Mathf.Min(depth, far.Depth - animation.HandsContainer.Weapon.localPosition.y);

            Matrix4x4 toWorld = Matrix4x4.TRS(animation.AimPointParent.position, animation.AimPointParent.rotation, animation.Scale);
            Vector3 point = toWorld.MultiplyPoint3x4(new Vector3(0f, 0f - depth, 0f));
            Matrix4x4 toParent = Matrix4x4.TRS(parent.position, parent.rotation, animation.Scale).inverse;
            float turnAway = animation.TurnAway != null ? animation.TurnAway.Position.y : 0f;
            local.z = toParent.MultiplyPoint3x4(point).z + fovCompensation - turnAway + animation._cameraShiftToLineOfSight.x;
        }

        local.y += animation._cameraShiftToLineOfSight.y;
        return parent.TransformPoint(local);
    }

    private static Vector3 CarryOnArm(PlayerBones bones, Vector3 solvedDisplacement, bool capture)
    {
        Transform? third = bones.Weapon_Root_Third;
        Transform? weapon = bones.Weapon_Root_Anim;
        if (third == null || weapon == null)
            return solvedDisplacement;

        Quaternion armRotation = third.rotation;
        Vector3 armPosition = third.position;
        if (capture)
        {
            Vector3 solved = weapon.position + solvedDisplacement;
            _armCarryOffset = Quaternion.Inverse(armRotation) * (solved - armPosition);
            return solvedDisplacement;
        }

        return armPosition + armRotation * _armCarryOffset - weapon.position;
    }

    internal static void ReplayLasers()
    {
        if (_laserLateUpdate == null || DeferredLasers.Count == 0)
        {
            DeferredLasers.Clear();
            _hasLaserDisplacement = false;
            return;
        }

        _replayingLaser = true;
        try
        {
            foreach (MonoBehaviour laser in DeferredLasers)
            {
                if (laser == null || !laser.isActiveAndEnabled)
                    continue;

                Transform emitter = laser.transform;
                bool alreadyMoved = EmitterAlreadyMoved(emitter);
                Vector3 saved = emitter.position;
                if (_hasLaserDisplacement && !alreadyMoved)
                    emitter.position = saved + _laserDisplacement;
                _laserLateUpdate.Invoke(laser, null);
                if (_hasLaserDisplacement && !alreadyMoved)
                    emitter.position = saved;
            }
        }
        finally
        {
            DeferredLasers.Clear();
            _replayingLaser = false;
            _hasLaserDisplacement = false;
        }
    }

    private static bool EmitterAlreadyMoved(Transform emitter)
    {
        foreach (Transform arm in Upperarms)
        {
            if (arm != null && emitter.IsChildOf(arm))
                return true;
        }

        Player? player = Comfort.Common.Singleton<GameWorld>.Instance?.MainPlayer;
        Transform? weapon = player?.PlayerBones?.Weapon_Root_Anim;
        return weapon != null && emitter.IsChildOf(weapon);
    }

    private static bool LaserLateUpdatePrefix(MonoBehaviour __instance)
    {
        if (_replayingLaser || __instance == null || !NewCameraSwitch.Enabled)
            return true;

        Player? player = Comfort.Common.Singleton<GameWorld>.Instance?.MainPlayer;
        if (player == null || !player.IsYourPlayer)
            return true;
        if (player.HandsController == null || !player.HandsController.IsAiming)
            return true;

        Transform playerRoot = ((Component)player).transform;
        Transform? weapon = player.PlayerBones?.Weapon_Root_Anim;
        bool onPlayer = __instance.transform.IsChildOf(playerRoot);
        bool onWeapon = weapon != null && __instance.transform.IsChildOf(weapon);
        if (!onPlayer && !onWeapon)
            return true;

        DeferredLasers.Add(__instance);
        return false;
    }

    private static void EnsureUpperarms(Player player)
    {
        int ownerId = player.GetInstanceID();
        if (_skinOwnerId == ownerId && Upperarms.Count > 0)
            return;

        RestoreSkins();
        _skinOwnerId = ownerId;
        HashSet<Transform> seen = new();
        foreach (SkinnedMeshRenderer renderer in player.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (renderer == null)
                continue;

            bool carriesArm = false;
            foreach (Transform bone in renderer.bones)
            {
                if (bone == null || !IsUpperarm(bone.name))
                    continue;
                carriesArm = true;
                if (seen.Add(bone))
                    Upperarms.Add(bone);
            }

            if (!carriesArm)
                continue;

            Skins.Add(new SkinState
            {
                Renderer = renderer,
                ForceMatrix = renderer.forceMatrixRecalculationPerRender,
                UpdateWhenOffscreen = renderer.updateWhenOffscreen
            });
            renderer.forceMatrixRecalculationPerRender = true;
            renderer.updateWhenOffscreen = true;
        }
    }

    private static bool IsUpperarm(string name)
    {
        string normalized = name.Replace(" ", string.Empty).Replace("_", string.Empty);
        return normalized.IndexOf("upperarm", System.StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static void RestoreSkins()
    {
        foreach (SkinState skin in Skins)
        {
            if (skin.Renderer == null)
                continue;
            skin.Renderer.forceMatrixRecalculationPerRender = skin.ForceMatrix;
            skin.Renderer.updateWhenOffscreen = skin.UpdateWhenOffscreen;
        }

        Skins.Clear();
        Upperarms.Clear();
        _skinOwnerId = 0;
    }

    internal static void Release(Player? player)
    {
        _hasArmCarry = false;
        _armCarrySightId = 0;
        _armCarryTime = 0f;
        RestoreSkins();
        if (!_suppress)
            return;

        _suppress = false;
        if (player?.HandsController != null && player.HandsController.IsAiming)
            player.MovementContext?.PlayerAnimator?.SetAiming(true);
    }

    private static void SetAimingPrefix(PlayerAnimator __instance, ref bool isAiming)
    {
        if (!_suppress || !isAiming)
            return;

        Player? player = Comfort.Common.Singleton<GameWorld>.Instance?.MainPlayer;
        if (player != null && player.IsYourPlayer && ReferenceEquals(player.MovementContext?.PlayerAnimator, __instance))
            isAiming = false;
    }
}
