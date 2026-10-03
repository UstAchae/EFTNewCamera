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

    private const float BlendSeconds = 0.2f;

    private static bool _suppress;
    private static bool _patched;
    private static bool _replayingLaser;
    private static bool _hasLaserDisplacement;
    private static bool _blendAiming;
    private static float _blend = 1f;
    private static int _aimSignature;
    private static Vector3 _laserDisplacement;
    private static Vector3 _offset;
    private static Vector3 _blendFrom;
    private static MethodInfo? _laserLateUpdate;
    private static readonly List<MonoBehaviour> DeferredLasers = new();
    private static int _skinOwnerId;
    private static readonly List<Transform> Upperarms = new();
    private static readonly List<SkinState> Skins = new();

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

    internal static void AlignToCamera(Player player, Vector3 cameraPosition, Quaternion cameraRotation)
    {
        PlayerBones bones = player.PlayerBones;
        if (bones == null)
        {
            _hasLaserDisplacement = false;
            return;
        }

        bool aiming = player.HandsController != null && player.HandsController.IsAiming;
        int signature = 0;
        Vector3 target = aiming
            ? ResolveAimTarget(player.ProceduralWeaponAnimation, cameraPosition, cameraRotation, out signature)
            : Vector3.zero;
        if (signature != _aimSignature || aiming != _blendAiming)
        {
            _blendFrom = _offset;
            _blend = 0f;
            _aimSignature = signature;
            _blendAiming = aiming;
        }

        if (_blend < 1f)
        {
            _blend = Mathf.Min(1f, _blend + Time.deltaTime / BlendSeconds);
            float t = _blend * _blend * (3f - 2f * _blend);
            _offset = Vector3.Lerp(_blendFrom, target, t);
        }
        else
        {
            _offset = target;
        }

        _laserDisplacement = _offset;
        _hasLaserDisplacement = _offset.sqrMagnitude >= 0.00000001f;
        if (!_hasLaserDisplacement)
            return;

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
                arm.position += _offset;
        }

        Transform? weapon = bones.Weapon_Root_Anim;
        if (weapon == null)
            return;
        foreach (Transform arm in Upperarms)
        {
            if (arm != null && weapon.IsChildOf(arm))
                return;
        }

        weapon.position += _offset;
    }

    private static Vector3 ResolveAimTarget(ProceduralWeaponAnimation? animation, Vector3 cameraPosition, Quaternion cameraRotation, out int signature)
    {
        signature = 0;
        if (animation == null || animation.ScopeAimTransforms == null || animation.ScopeAimTransforms.Count == 0)
            return Vector3.zero;

        Transform? active = animation.CurrentScope?.Bone;
        if (active == null)
            return Vector3.zero;

        Transform? weapon = animation.HandsContainer != null ? animation.HandsContainer.Weapon : null;
        Vector3 forward = cameraRotation * Vector3.forward;
        float activeDepth = SightDepth(weapon, active, cameraPosition, forward);
        float rearDepth = activeDepth;
        for (int i = 0; i < animation.ScopeAimTransforms.Count; i++)
        {
            Transform? bone = animation.ScopeAimTransforms[i]?.Bone;
            if (bone == null)
                continue;

            float depth = SightDepth(weapon, bone, cameraPosition, forward);
            if (depth < rearDepth)
                rearDepth = depth;
        }

        float relief = Mathf.Max(0f, activeDepth - rearDepth);
        signature = active.GetInstanceID();
        return cameraPosition + forward * relief - active.position;
    }

    private static float SightDepth(Transform? weapon, Transform bone, Vector3 cameraPosition, Vector3 cameraForward)
    {
        if (weapon != null)
            return -weapon.InverseTransformPoint(bone.position).y;

        return Vector3.Dot(bone.position - cameraPosition, cameraForward);
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
        {
            if (_offset.sqrMagnitude < 0.00000001f)
                return true;
        }

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
        _offset = Vector3.zero;
        _blendFrom = Vector3.zero;
        _blend = 1f;
        _aimSignature = 0;
        _blendAiming = false;
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
