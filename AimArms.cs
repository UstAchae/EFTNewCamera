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

    private const float ConeTipDistance = 100f;
    private const float SwaySnapMeters = 0.15f;

    internal static float ForwardDistance;
    internal static float HoldSeconds = 0.6f;

    private static bool _hasSwayMean;
    private static Vector3 _swayMean;
    private const float MouseSpeedReference = 3f;
    private const float MouseSpeedSeconds = 0.1f;

    private static bool _hasAimHold;
    private static Quaternion _aimHold = Quaternion.identity;
    private static Quaternion _lastLook = Quaternion.identity;
    private static float _mouseSpeed;
    private static bool _suppress;
    private static Vector3 _offset;
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
        PatchShiftWeaponRoot(harmony);
        PatchLaser(harmony);
        PatchFieldOfView(harmony);
        _patched = true;
    }

    private static void PatchShiftWeaponRoot(Harmony harmony)
    {
        MethodInfo? shift = AccessTools.Method(typeof(PlayerBones), nameof(PlayerBones.ShiftWeaponRoot));
        MethodInfo? postfix = typeof(AimArms).GetMethod(nameof(ShiftWeaponRootPostfix), BindingFlags.NonPublic | BindingFlags.Static);
        if (shift == null || postfix == null)
            return;

        harmony.Patch(shift, postfix: new HarmonyMethod(postfix) { priority = Priority.Last });
    }

    // Runs before RotateHead and hand IK, so the hands still grip the gun after it is placed.
    private static void ShiftWeaponRootPostfix(PlayerBones __instance, EPointOfView pv)
    {
        if (!NewCameraSwitch.Enabled || pv != EPointOfView.ThirdPerson || __instance == null)
            return;

        Player? player = __instance.Player;
        if (player == null || !player.IsYourPlayer)
            return;

        Transform? head = __instance.Head?.Original;
        if (head == null)
            return;

        Quaternion liveRotation = CameraDirection.Resolve(head.rotation);
        Vector3 cameraPosition = CameraPosition.Resolve(head.position, liveRotation);
        bool aiming = player.HandsController != null && player.HandsController.IsAiming;
        Quaternion aimRotation = liveRotation;
        if (aiming)
            aimRotation = HoldAim(player, liveRotation);
        else
            ClearHold();
        AlignToCamera(player, cameraPosition, aimRotation, liveRotation);
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

    internal static void AlignToCamera(Player player, Vector3 cameraPosition, Quaternion cameraRotation, Quaternion liveRotation)
    {
        PlayerBones bones = player.PlayerBones;
        bool aiming = player.HandsController != null && player.HandsController.IsAiming;
        Vector3 target = Vector3.zero;
        Transform? weapon = bones?.Weapon_Root_Anim;
        bool placeWeapon = false;
        Quaternion targetWeaponRotation = Quaternion.identity;
        if (aiming && bones != null && weapon != null)
        {
            ProceduralWeaponAnimation? animation = player.ProceduralWeaponAnimation;
            Transform? bone = animation != null && animation.ScopeAimTransforms != null && animation.ScopeAimTransforms.Count > 0
                ? animation.CurrentScope?.Bone
                : null;
            if (bone != null && animation != null)
            {
                float relief = SightRelief(animation, bone);
                Vector3 boneLocalPosition = Quaternion.Inverse(weapon.rotation) * (bone.position - weapon.position);
                Quaternion boneLocalRotation = Quaternion.Inverse(weapon.rotation) * bone.rotation;
                Vector3 sightPoint = cameraPosition + cameraRotation * new Vector3(0f, 0f, ForwardDistance + relief);
                // The rear keeps the sway the game animated for the sight; the barrel is aimed
                // from there at a far point on the held aim line, so the swing narrows toward the tip.
                Vector3 animatedSight = Quaternion.Inverse(liveRotation) * (bone.position - cameraPosition);
                Vector3 rear = sightPoint + liveRotation * TrackSway(animatedSight);
                Vector3 tip = cameraPosition + cameraRotation * new Vector3(0f, 0f, ConeTipDistance);
                Quaternion sightRotation = AlignSightTo(bone.rotation, (tip - rear).normalized);
                targetWeaponRotation = sightRotation * Quaternion.Inverse(boneLocalRotation);
                Vector3 targetWeaponPosition = rear - targetWeaponRotation * boneLocalPosition;
                target = targetWeaponPosition - weapon.position;
                placeWeapon = true;
            }
        }

        if (!placeWeapon || bones == null || weapon == null)
        {
            _offset = Vector3.zero;
            _hasLaserDisplacement = false;
            return;
        }

        _offset = target;
        _laserDisplacement = target;
        _hasLaserDisplacement = target.sqrMagnitude >= 0.00000001f;
        weapon.SetPositionAndRotation(weapon.position + target, targetWeaponRotation);

    }

    private static Vector3 TrackSway(Vector3 animatedSight)
    {
        float holdTime = HoldSeconds;
        if (holdTime <= 0.0001f)
        {
            _hasSwayMean = false;
            return Vector3.zero;
        }

        if (!_hasSwayMean || (animatedSight - _swayMean).sqrMagnitude > SwaySnapMeters * SwaySnapMeters)
        {
            _swayMean = animatedSight;
            _hasSwayMean = true;
            return Vector3.zero;
        }

        _swayMean = Vector3.Lerp(_swayMean, animatedSight, 1f - Mathf.Exp(-Time.deltaTime / holdTime));
        return animatedSight - _swayMean;
    }

    private static Quaternion AlignSightTo(Quaternion sightRotation, Vector3 cameraForward)
    {
        Vector3 fromForward = sightRotation * Vector3.forward;
        float bestDot = Vector3.Dot(fromForward, cameraForward);
        Vector3[] locals =
        {
            Vector3.back, Vector3.up, Vector3.down, Vector3.right, Vector3.left
        };
        for (int i = 0; i < locals.Length; i++)
        {
            Vector3 axis = sightRotation * locals[i];
            float dot = Vector3.Dot(axis, cameraForward);
            if (dot <= bestDot)
                continue;

            bestDot = dot;
            fromForward = axis;
        }

        return Quaternion.FromToRotation(fromForward, cameraForward) * sightRotation;
    }

    internal static Quaternion HoldAim(Player player, Quaternion liveCameraRotation)
    {
        float holdTime = HoldSeconds;
        if (holdTime <= 0.0001f)
        {
            _hasAimHold = false;
            return liveCameraRotation;
        }

        bool hasLook = TryGetMouseLook(player, out Quaternion look);
        if (!_hasAimHold)
        {
            _aimHold = liveCameraRotation;
            _lastLook = look;
            _mouseSpeed = 0f;
            _hasAimHold = true;
            return liveCameraRotation;
        }

        // The gun only ever points along the smoothed picture. The mouse never supplies a
        // direction; its speed only decides how quickly that smoothing lets go while turning.
        float dt = Mathf.Max(Time.deltaTime, 0.0001f);
        float speed = hasLook ? Quaternion.Angle(_lastLook, look) / dt : 0f;
        if (hasLook)
            _lastLook = look;
        _mouseSpeed = Mathf.Lerp(_mouseSpeed, speed, 1f - Mathf.Exp(-dt / MouseSpeedSeconds));
        float ratio = _mouseSpeed / MouseSpeedReference;
        float smoothing = holdTime / (1f + ratio * ratio);
        _aimHold = Quaternion.Slerp(_aimHold, liveCameraRotation, 1f - Mathf.Exp(-dt / smoothing));
        return _aimHold;
    }

    internal static void ClearHold()
    {
        _hasSwayMean = false;
        _hasAimHold = false;
    }

    private static bool TryGetMouseLook(Player player, out Quaternion look)
    {
        look = Quaternion.identity;
        try
        {
            MovementContext? movement = player.MovementContext;
            if (movement == null)
                return false;

            look = Quaternion.Euler(movement.Pitch, movement.Yaw, 0f);
            return true;
        }
        catch
        {
            return false;
        }
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
        _offset = Vector3.zero;
        ClearHold();
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
