using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
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

    internal static float ForwardDistance;

    private const float NeckSeconds = 0.45f;
    private const float NeckSnapDegrees = 20f;
    private const float NeckSnapMeters = 0.1f;

    private static bool _suppress;
    private static bool _blendAiming;
    private static int _blendSightId;
    private static float _blend = 1f;
    private static Vector3 _offset;
    private static Vector3 _blendFrom;
    private static bool _patched;
    private static bool _replayingLaser;
    private static bool _hasLaserDisplacement;
    private static Vector3 _laserDisplacement;
    private static MethodInfo? _laserLateUpdate;
    private static Camera? _drawCamera;
    private static int _laserFrame = -1;
    private static readonly List<MonoBehaviour> DeferredLasers = new();
    private static int _skinOwnerId;
    private static readonly List<Transform> Upperarms = new();
    private static readonly List<SkinState> Skins = new();
    private static bool _neckReady;
    private static Quaternion _neckOffset;
    private static Vector3 _neckLocal;
    private static bool _barrelReady;
    private static Quaternion _barrelHold;
    private static bool _opticReady;
    private static Quaternion _opticOffset;
    private static Quaternion _opticDesired;
    private static bool _opticHeld;
    private static Quaternion _opticSaved;
    private static Camera? _opticCamera;
    private static int _opticFrame = -1;

    internal static void Install()
    {
        if (_patched)
            return;

        MethodInfo? target = AccessTools.Method(typeof(PlayerAnimator), nameof(PlayerAnimator.SetAiming));
        MethodInfo? prefix = typeof(AimArms).GetMethod(nameof(SetAimingPrefix), BindingFlags.NonPublic | BindingFlags.Static);
        if (target == null || prefix == null)
            return;

        Harmony harmony = new Harmony(NewCameraPlugin.PluginGuid + ".aimarms");
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
        MethodInfo? laserTranspiler = typeof(AimArms).GetMethod(nameof(DrawForReplayCamera), BindingFlags.NonPublic | BindingFlags.Static);
        if (_laserLateUpdate == null || laserPrefix == null || laserTranspiler == null)
            return;

        harmony.Patch(_laserLateUpdate, prefix: new HarmonyMethod(laserPrefix), transpiler: new HarmonyMethod(laserTranspiler));
    }

    internal static void Apply(Player player)
    {
        _suppress = true;
        player.MovementContext?.PlayerAnimator?.SetAiming(false);
    }

    internal static void SteadyAim(Player player, ref Vector3 position, ref Quaternion rotation)
    {
        bool aiming = player.HandsController != null && player.HandsController.IsAiming;
        if (!aiming || player.MovementContext == null)
        {
            _neckReady = false;
            _opticReady = false;
            return;
        }

        Quaternion look = Quaternion.Euler(player.MovementContext.Pitch, player.MovementContext.Yaw, 0f);
        Quaternion offset = Quaternion.Inverse(look) * rotation;
        Vector3 local = ((Component)player).transform.InverseTransformPoint(position);
        if (!_neckReady)
        {
            _neckOffset = offset;
            _neckLocal = local;
            _neckReady = true;
            return;
        }

        if (Quaternion.Angle(offset, _neckOffset) > NeckSnapDegrees || Vector3.Distance(local, _neckLocal) > NeckSnapMeters)
        {
            _neckOffset = offset;
            _neckLocal = local;
            return;
        }

        float follow = 1f - Mathf.Exp(-Time.deltaTime / NeckSeconds);
        _neckOffset = Quaternion.Slerp(_neckOffset, offset, follow);
        _neckLocal = Vector3.Lerp(_neckLocal, local, follow);
        rotation = look * _neckOffset;
        position = ((Component)player).transform.TransformPoint(_neckLocal);
    }

    internal static void AlignToCamera(Player player, Vector3 cameraPosition, Quaternion cameraRotation)
    {
        PlayerBones bones = player.PlayerBones;
        bool aiming = player.HandsController != null && player.HandsController.IsAiming;
        int sightId = 0;
        Vector3 target = Vector3.zero;
        Transform? sight = null;
        if (aiming && bones != null)
        {
            ProceduralWeaponAnimation? animation = player.ProceduralWeaponAnimation;
            Transform? bone = animation != null && animation.ScopeAimTransforms != null && animation.ScopeAimTransforms.Count > 0
                ? animation.CurrentScope?.Bone
                : null;
            sight = bone;
            if (bone != null && animation != null)
            {
                sightId = bone.GetInstanceID();
                float relief = SightRelief(animation, bone);
                target = cameraPosition - bone.position + cameraRotation * new Vector3(0f, 0f, ForwardDistance + relief);
            }
        }

        if (!aiming && !_blendAiming && _offset.sqrMagnitude < 0.00000001f && _blend >= 1f)
        {
            _hasLaserDisplacement = false;
            _barrelReady = false;
            return;
        }

        if (bones == null)
        {
            _hasLaserDisplacement = false;
            _barrelReady = false;
            return;
        }

        if (aiming != _blendAiming || sightId != _blendSightId)
        {
            _blendFrom = _offset;
            _blend = 0f;
            _blendAiming = aiming;
            _blendSightId = sightId;
            _barrelReady = false;
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

        Vector3 displacement = _offset;
        _laserDisplacement = displacement;
        _hasLaserDisplacement = displacement.sqrMagnitude >= 0.00000001f;
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
        bool weaponUnderArm = false;
        if (weapon != null)
        {
            foreach (Transform arm in Upperarms)
            {
                if (arm != null && weapon.IsChildOf(arm))
                {
                    weaponUnderArm = true;
                    break;
                }
            }

            if (!weaponUnderArm)
                weapon.position += displacement;
        }

        if (aiming && sight != null && weapon != null && _blend >= 1f)
            HoldBarrel(sight, weapon, weaponUnderArm, cameraRotation);
        else
            _barrelReady = false;
    }

    internal static void HoldOptic(Camera camera, Player player)
    {
        bool aiming = player.HandsController != null && player.HandsController.IsAiming;
        if (!aiming || player.MovementContext == null)
        {
            _opticReady = false;
            return;
        }

        if (_opticFrame != Time.frameCount)
        {
            _opticFrame = Time.frameCount;
            _opticSaved = camera.transform.rotation;
            _opticCamera = camera;
            _opticHeld = true;
            Quaternion look = Quaternion.Euler(player.MovementContext.Pitch, player.MovementContext.Yaw, 0f);
            Quaternion offset = Quaternion.Inverse(look) * _opticSaved;
            if (!_opticReady)
            {
                _opticOffset = offset;
                _opticReady = true;
            }
            else if (Quaternion.Angle(offset, _opticOffset) <= NeckSnapDegrees)
            {
                float follow = 1f - Mathf.Exp(-Time.deltaTime / NeckSeconds);
                _opticOffset = Quaternion.Slerp(_opticOffset, offset, follow);
            }
            else
            {
                _opticOffset = offset;
            }

            _opticDesired = look * _opticOffset;
        }

        camera.transform.rotation = _opticDesired;
    }

    internal static void RestoreOptic()
    {
        if (!_opticHeld || _opticCamera == null)
            return;

        _opticCamera.transform.rotation = _opticSaved;
        _opticHeld = false;
        _opticCamera = null;
    }

    private static void HoldBarrel(Transform sight, Transform weapon, bool weaponUnderArm, Quaternion aimRotation)
    {
        if (!_barrelReady)
        {
            _barrelHold = Quaternion.Inverse(aimRotation) * weapon.rotation;
            _barrelReady = true;
            return;
        }

        Quaternion correction = aimRotation * _barrelHold * Quaternion.Inverse(weapon.rotation);
        if (Quaternion.Angle(Quaternion.identity, correction) < 0.05f)
            return;

        Vector3 pivot = sight.position;
        Vector3 before = weapon.position;
        if (!weaponUnderArm)
            TurnAround(weapon, pivot, correction);
        foreach (Transform arm in Upperarms)
        {
            if (arm != null)
                TurnAround(arm, pivot, correction);
        }

        _laserDisplacement += weapon.position - before;
    }

    private static void TurnAround(Transform target, Vector3 pivot, Quaternion correction)
    {
        target.position = pivot + correction * (target.position - pivot);
        target.rotation = correction * target.rotation;
    }

    private static float SightRelief(ProceduralWeaponAnimation animation, Transform bone)
    {
        if (animation.HandsContainer == null || animation.HandsContainer.Weapon == null)
            return 0f;

        WeaponPrefab.AimPlane? plane = animation._currentAimingPlane;
        if (animation.CurrentScope != null && animation.CurrentScope.IsOptic)
            plane = animation.HandsContainer.DefaultAimPlane;
        if (plane == null)
            plane = animation.HandsContainer.DefaultAimPlane;
        if (plane == null)
            return 0f;

        float boneDepth = 0f - animation.HandsContainer.Weapon.InverseTransformPoint(bone.position).y;
        return boneDepth - plane.Depth;
    }

    internal static void ReplayLasers(Camera camera)
    {
        if (_laserLateUpdate == null || DeferredLasers.Count == 0)
            return;

        _drawCamera = camera;
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
            _replayingLaser = false;
            _drawCamera = null;
        }
    }

    public static Camera? GetDrawCamera()
    {
        return _drawCamera;
    }

    private static IEnumerable<CodeInstruction> DrawForReplayCamera(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> list = new List<CodeInstruction>(instructions);
        MethodInfo? getCamera = typeof(AimArms).GetMethod(nameof(GetDrawCamera), BindingFlags.Public | BindingFlags.Static);
        if (getCamera == null)
            return list;

        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].opcode != OpCodes.Call || list[i].operand is not MethodInfo called || called.Name != nameof(Graphics.DrawMesh))
                continue;

            for (int previous = i - 1; previous >= 0 && previous >= i - 8; previous--)
            {
                if (list[previous].opcode != OpCodes.Ldnull)
                    continue;

                list[previous] = new CodeInstruction(OpCodes.Call, getCamera);
                break;
            }
        }

        return list;
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

        if (_laserFrame != Time.frameCount)
        {
            DeferredLasers.Clear();
            _laserFrame = Time.frameCount;
        }

        Player? player = Comfort.Common.Singleton<GameWorld>.Instance?.MainPlayer;
        if (player == null || !player.IsYourPlayer)
            return true;
        bool aiming = player.HandsController != null && player.HandsController.IsAiming;
        if (!aiming && _offset.sqrMagnitude < 0.00000001f)
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
        _blendAiming = false;
        _blendSightId = 0;
        _blend = 1f;
        _offset = Vector3.zero;
        _blendFrom = Vector3.zero;
        _neckReady = false;
        _barrelReady = false;
        _opticReady = false;
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
