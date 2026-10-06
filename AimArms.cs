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

    // Standing sway is breathing-paced; walking adds step-paced sway. Two filter stages at this
    // time constant keep most of the former and remove most of the latter.
    private const float WalkFilterSeconds = 0.2f;
    private const float BaseSeconds = 2f;
    private const float SwaySnapDegrees = 20f;
    private const float SwaySnapMeters = 0.15f;
    private const float ActionBlendSeconds = 0.15f;
    private const float WalkingSpeed = 0.5f;
    private const float HeadFollowSeconds = 0.05f;
    private const float HeadSnapDegrees = 45f;

    private static bool _hasHeadInLook;
    private static Quaternion _headInLook = Quaternion.identity;
    private static float _actionWeight;

    private const float ConeTipDistance = 100f;
    private const float MouseSpeedReference = 3f;
    private const float MouseSpeedSeconds = 0.1f;
    private const float SwayTurnSpeed = 8f;

    internal static float ForwardDistance;
    internal static bool UseCone;
    internal static float HoldSeconds = 0.6f;

    private static bool _hasSwayMean;
    private static Vector3 _swayMean;
    private static bool _hasAimHold;
    private static Quaternion _aimHold = Quaternion.identity;
    private static Quaternion _lastLook = Quaternion.identity;
    private static float _mouseSpeed;

    private static bool _hasSway;
    private static int _swaySightId;
    private static Quaternion _bodyBaseRotation = Quaternion.identity;
    private static Vector3 _bodyBasePosition;
    private static Quaternion _gunBaseRotation = Quaternion.identity;
    private static Vector3 _gunBasePosition;
    private static Vector3 _turnStage1;
    private static Vector3 _turnStage2;
    private static Vector3 _shiftStage1;
    private static Vector3 _shiftStage2;
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

        MethodInfo? rotateHead = AccessTools.Method(typeof(PlayerBones), nameof(PlayerBones.RotateHead));
        MethodInfo? headPostfix = typeof(AimArms).GetMethod(nameof(RotateHeadPostfix), BindingFlags.NonPublic | BindingFlags.Static);
        if (rotateHead != null && headPostfix != null)
            harmony.Patch(rotateHead, postfix: new HarmonyMethod(headPostfix) { priority = Priority.Last });
    }

    // The gun points along the mouse look, so while aiming the head itself is turned onto that same
    // look plus its own smoothed motion relative to it. The camera stays fixed to the head, and uneven
    // mouse input per frame moves head, picture and gun together.
    private static void RotateHeadPostfix(PlayerBones __instance)
    {
        if (!NewCameraSwitch.Enabled || __instance == null)
            return;

        Player? player = __instance.Player;
        Transform? head = __instance.Head?.Original;
        if (UseCone || player == null || !player.IsYourPlayer || head == null || player.PointOfView != EPointOfView.ThirdPerson
            || !TryGetLook(player, out Quaternion look))
        {
            _hasHeadInLook = false;
            return;
        }

        Quaternion cameraRotation = CameraDirection.Resolve(head.rotation);
        Quaternion headInLook = Quaternion.Inverse(look) * cameraRotation;
        if (!_hasHeadInLook || Quaternion.Angle(_headInLook, headInLook) > HeadSnapDegrees)
        {
            _headInLook = headInLook;
            _hasHeadInLook = true;
        }
        else
        {
            _headInLook = Quaternion.Slerp(_headInLook, headInLook, 1f - Mathf.Exp(-Time.deltaTime / HeadFollowSeconds));
        }

        if (player.HandsController != null && player.HandsController.IsAiming)
            head.rotation = CameraDirection.Unresolve(look * _headInLook);
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
        if (UseCone)
        {
            Quaternion aimRotation = liveRotation;
            if (aiming)
                aimRotation = HoldAim(player, liveRotation);
            else
                ClearHold();
            AlignToCamera(player, cameraPosition, aimRotation, liveRotation);
            return;
        }

        if (!aiming || !TryGetLook(player, out Quaternion look) || !AlignToLook(player, cameraPosition, liveRotation, look))
        {
            ClearHold();
            _offset = Vector3.zero;
            _hasLaserDisplacement = false;
        }
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

    // The body bone that carries the gun is measured against the camera; only the breathing-paced
    // part of its sway is kept and laid onto the mouse look. The gun's own offset on that bone
    // (recoil) is left as the game computed it.
    private static bool AlignToLook(Player player, Vector3 cameraPosition, Quaternion liveRotation, Quaternion look)
    {
        PlayerBones? bones = player.PlayerBones;
        Transform? weapon = bones?.Weapon_Root_Anim;
        Transform? body = bones?.Weapon_Root_Third;
        ProceduralWeaponAnimation? animation = player.ProceduralWeaponAnimation;
        Transform? bone = animation != null && animation.ScopeAimTransforms != null && animation.ScopeAimTransforms.Count > 0
            ? animation.CurrentScope?.Bone
            : null;
        if (weapon == null || body == null || bone == null || animation == null)
            return false;

        Quaternion bodyRotation = body.rotation;
        Quaternion gunRotation = Quaternion.Inverse(bodyRotation) * weapon.rotation;
        Vector3 gunPosition = Quaternion.Inverse(bodyRotation) * (weapon.position - body.position);
        Quaternion boneLocalRotation = Quaternion.Inverse(weapon.rotation) * bone.rotation;
        Vector3 boneLocalPosition = Quaternion.Inverse(weapon.rotation) * (bone.position - weapon.position);
        Quaternion bodyInCamera = Quaternion.Inverse(liveRotation) * bodyRotation;
        Vector3 bodyInCameraPosition = Quaternion.Inverse(liveRotation) * (body.position - cameraPosition);

        bool acting = player.HandsController != null && player.HandsController.IsInInteractionStrictCheck();
        Vector3 velocity = player.Velocity;
        float walking = Mathf.Clamp01(new Vector2(velocity.x, velocity.z).magnitude / WalkingSpeed);
        float raw = acting ? 1f : 1f - walking;
        _actionWeight = Mathf.MoveTowards(_actionWeight, raw, Time.deltaTime / ActionBlendSeconds);
        Quaternion sway = FilterBodySway(bone.GetInstanceID(), bodyInCamera, bodyInCameraPosition, gunRotation, gunPosition, out Vector3 swayShift);

        Quaternion steadyBody = look * _bodyBaseRotation;
        Quaternion steadySight = steadyBody * _gunBaseRotation * boneLocalRotation;
        Quaternion correction = AlignSightTo(steadySight, look * Vector3.forward) * Quaternion.Inverse(steadySight);
        Quaternion placedBody = correction * steadyBody * sway;
        Quaternion targetWeaponRotation = placedBody * gunRotation;
        float relief = SightRelief(animation, bone);
        Vector3 sightPoint = cameraPosition + look * new Vector3(0f, 0f, ForwardDistance + relief)
            + correction * (look * swayShift)
            + placedBody * (gunPosition - _gunBasePosition);
        Vector3 targetWeaponPosition = sightPoint - targetWeaponRotation * boneLocalPosition;
        Vector3 target = targetWeaponPosition - weapon.position;

        _offset = target;
        _laserDisplacement = target;
        _hasLaserDisplacement = target.sqrMagnitude >= 0.00000001f;
        weapon.SetPositionAndRotation(targetWeaponPosition, targetWeaponRotation);
        return true;
    }

    private static Quaternion FilterBodySway(int sightId, Quaternion bodyRotation, Vector3 bodyPosition, Quaternion gunRotation, Vector3 gunPosition, out Vector3 shift)
    {
        shift = Vector3.zero;
        Quaternion turn = Quaternion.Inverse(_bodyBaseRotation) * bodyRotation;
        Vector3 offset = bodyPosition - _bodyBasePosition;
        bool jumped = _actionWeight <= 0f
            && (Quaternion.Angle(Quaternion.identity, turn) > SwaySnapDegrees || offset.sqrMagnitude > SwaySnapMeters * SwaySnapMeters);
        if (!_hasSway || sightId != _swaySightId || jumped)
        {
            _bodyBaseRotation = bodyRotation;
            _bodyBasePosition = bodyPosition;
            _gunBaseRotation = gunRotation;
            _gunBasePosition = gunPosition;
            _turnStage1 = Vector3.zero;
            _turnStage2 = Vector3.zero;
            _shiftStage1 = Vector3.zero;
            _shiftStage2 = Vector3.zero;
            _swaySightId = sightId;
            _hasSway = true;
            return Quaternion.identity;
        }

        float dt = Time.deltaTime;
        float follow = 1f - Mathf.Exp(-dt / BaseSeconds);
        _bodyBaseRotation = Quaternion.Slerp(_bodyBaseRotation, bodyRotation, follow);
        _bodyBasePosition = Vector3.Lerp(_bodyBasePosition, bodyPosition, follow);
        _gunBaseRotation = Quaternion.Slerp(_gunBaseRotation, gunRotation, follow);
        _gunBasePosition = Vector3.Lerp(_gunBasePosition, gunPosition, follow);
        turn = Quaternion.Inverse(_bodyBaseRotation) * bodyRotation;
        offset = bodyPosition - _bodyBasePosition;

        float pass = 1f - Mathf.Exp(-dt / WalkFilterSeconds);
        _turnStage1 = Vector3.Lerp(_turnStage1, ToRotationVector(turn), pass);
        _turnStage2 = Vector3.Lerp(_turnStage2, _turnStage1, pass);
        _shiftStage1 = Vector3.Lerp(_shiftStage1, offset, pass);
        _shiftStage2 = Vector3.Lerp(_shiftStage2, _shiftStage1, pass);
        // Standing still, or during a recognised hands action, the body's motion on the gun is kept
        // whole (bolt cycling, fire mode, breathing); only walking gets the step-paced sway removed.
        shift = Vector3.Lerp(_shiftStage2, offset, _actionWeight);
        return Quaternion.Slerp(FromRotationVector(_turnStage2), turn, _actionWeight);
    }

    private static Vector3 ToRotationVector(Quaternion rotation)
    {
        rotation.ToAngleAxis(out float angle, out Vector3 axis);
        if (angle > 180f)
            angle -= 360f;
        if (Mathf.Abs(angle) < 0.0001f || float.IsNaN(axis.x) || float.IsInfinity(axis.x))
            return Vector3.zero;
        return axis * angle;
    }

    private static Quaternion FromRotationVector(Vector3 vector)
    {
        float angle = vector.magnitude;
        return angle < 0.0001f ? Quaternion.identity : Quaternion.AngleAxis(angle, vector / angle);
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

    internal static void ClearHold()
    {
        _hasSway = false;
        _hasSwayMean = false;
        _hasAimHold = false;
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

    internal static Quaternion HoldAim(Player player, Quaternion liveCameraRotation)
    {
        float holdTime = HoldSeconds;
        if (holdTime <= 0.0001f)
        {
            _hasAimHold = false;
            return liveCameraRotation;
        }

        if (!_hasAimHold)
        {
            _aimHold = liveCameraRotation;
            _lastLook = liveCameraRotation;
            _mouseSpeed = 0f;
            _hasAimHold = true;
            return liveCameraRotation;
        }

        // The gun only ever points along the smoothed picture, and how quickly that smoothing lets
        // go is judged from the picture's own turning speed. Mouse input arrives unevenly per frame;
        // the head-driven picture turns smoothly, so the gun's lag behind it changes smoothly too.
        float dt = Mathf.Max(Time.deltaTime, 0.0001f);
        float speed = AngleBetween(_lastLook, liveCameraRotation) / dt;
        _lastLook = liveCameraRotation;
        _mouseSpeed = Mathf.Lerp(_mouseSpeed, speed, 1f - Mathf.Exp(-dt / MouseSpeedSeconds));
        float ratio = Mathf.Max(0f, _mouseSpeed - SwayTurnSpeed) / MouseSpeedReference;
        float smoothing = holdTime / (1f + ratio * ratio);
        _aimHold = Quaternion.Slerp(_aimHold, liveCameraRotation, 1f - Mathf.Exp(-dt / smoothing));
        return _aimHold;
    }

    // Quaternion.Angle reads anything under about 0.16 degrees as zero, which turns slow motion into
    // on/off spikes at high frame rates.
    private static float AngleBetween(Quaternion from, Quaternion to)
    {
        Quaternion delta = Quaternion.Inverse(from) * to;
        float sine = new Vector3(delta.x, delta.y, delta.z).magnitude;
        return 2f * Mathf.Atan2(sine, Mathf.Abs(delta.w)) * Mathf.Rad2Deg;
    }

    internal static bool TryGetLook(Player player, out Quaternion look)
    {
        look = Quaternion.identity;
        try
        {
            MovementContext? movement = player.MovementContext;
            if (movement == null)
                return false;

            Vector2 rotation = movement.Rotation;
            look = Quaternion.Euler(rotation.y, rotation.x, 0f);
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
