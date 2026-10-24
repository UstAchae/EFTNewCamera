using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Comfort.Common;
using EFT;
using EFT.Animations;
using EFT.Ballistics;
using EFT.CameraControl;
using EFT.Interactive;
using EFT.InventoryLogic;
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
    private const float MeanSeconds = 1f;
    private const float AimInSeconds = 0.7f;
    private const float AimInMeanSeconds = 0.08f;
    private const float GunSmoothSeconds = 0.04f;
    private const float HeadSmoothSeconds = 0.05f;
    private const float ReleaseSeconds = 0.3f;
    private const float HeadLockSeconds = 0.2f;

    internal static float ForwardDistance;
    internal sealed class SwayAmounts
    {
        public float GunMove;
        public float GunTurn;
        public float ArmMove;
        public float ArmTurn;
        public float HeadFollow;
        public float Breath;
    }

    internal static readonly SwayAmounts Normal = new() { GunMove = 0.2f, GunTurn = 0.2f, ArmMove = 0.2f, ArmTurn = 0.2f, HeadFollow = 0.2f, Breath = 1f };
    internal static readonly SwayAmounts Holding = new();
    internal static readonly SwayAmounts Exhausted = new() { GunMove = 1f, GunTurn = 1f, ArmMove = 1f, ArmTurn = 1f, HeadFollow = 1f, Breath = 1f };
    private const float BreathCenterSeconds = 6f;
    private static readonly SwayAmounts Now = new() { GunMove = 0.2f, GunTurn = 0.2f, ArmMove = 0.2f, ArmTurn = 0.2f, HeadFollow = 0.2f, Breath = 1f };
    internal static bool Chase = true;
    internal static float FollowDelay;
    internal static float WalkHeadSway;
    internal static float HeadModelPitch;
    private const float MaxDelayDegrees = 15f;

    private static bool _suppress;
    private static bool _blendAiming;
    private static int _blendSightId;
    private static float _blend = 1f;
    private static float _mountBlend;
    private static Vector3 _offset;
    private static Vector3 _blendFrom;
    private static Quaternion _turn = Quaternion.identity;
    private static Quaternion _turnFrom = Quaternion.identity;
    private static bool _hasMean;
    private static int _meanSightId;
    private static Vector3 _armMoveMean;
    private static Vector3 _gunMoveMean;
    private static Quaternion _armTurnMean = Quaternion.identity;
    private static Quaternion _gunTurnMean = Quaternion.identity;
    private static float _meanStart;
    private static Quaternion _armTurnCenter = Quaternion.identity;
    private static Quaternion _gunTurnCenter = Quaternion.identity;
    private static float _sightCatch;
    private static Quaternion _lagLook = Quaternion.identity;
    private static Vector3 _gunMoveSmooth;
    private static Quaternion _gunTurnSmooth = Quaternion.identity;
    private static bool _hasChase;
    private static Quaternion _chaseTurn = Quaternion.identity;
    private static bool _handAction;
    private static bool _chaseCatching;
    private static float _handActionStart;
    private static float _chaseCatchStart;
    private const float ChaseCatchSeconds = 0.4f;
    private static float _handActionMin;
    private const float BoltMinSeconds = 0.3f;
    private const float FireModeMinSeconds = 0.5f;
    private const float HandActionMaxSeconds = 2.5f;
    private static Vector3 _chase;
    private static bool _hasHeadMean;
    private static bool _hasCenter;
    private const float StateBlendSeconds = 0.3f;
    private static float _holdWeight;
    private static float _tiredWeight;
    private static Physical? _drainOwner;
    private static bool _aimDrain;
    private static int _turnFrame = -1;
    private static int _rotateHeadFrame = -1;
    private static Quaternion _headTurnRaw = Quaternion.identity;
    private static bool _hasYawCenter;
    private static Quaternion _lookCenter = Quaternion.identity;
    private static Quaternion _headTurnSmooth = Quaternion.identity;
    private static float _headLock;
    private static bool _hasObserved;
    private static Quaternion _smoothLook = Quaternion.identity;
    // The mouse the body bones were posed with. The body animator poses from what the previous frame's
    // movement update handed it, so the bones trail MovementContext.Rotation by one movement update.
    private static Quaternion _bodyLook = Quaternion.identity;
    private static Vector2 _posedMouse;
    private static int _posedFrame = -1;
    private static Vector2 _previousPosedMouse;
    private static int _previousPosedFrame = -1;
    private static Vector3 _neckPosition;
    private static Vector3 _headPositionNow;
    private static Vector3 _heldHeadPosition;
    private static Quaternion _heldHeadTurn = Quaternion.identity;
    private static float _release;
    private static Vector3 _smoothedHead;
    private static int _headSmoothFrame = -1;
    private static bool _patched;
    private static bool _replayingLaser;
    private static bool _hasLaserDisplacement;
    private static Vector3 _laserDisplacement;
    private static Vector3 _laserPivot;
    private static Quaternion _laserTurn = Quaternion.identity;
    private static MethodInfo? _laserLateUpdate;
    private static Camera? _drawCamera;
    private static int _laserFrame = -1;
    private static int _opticFrame = -1;
    private static readonly AccessTools.FieldRef<ProceduralWeaponAnimation, float> LineOfSightAngle =
        AccessTools.FieldRefAccess<ProceduralWeaponAnimation, float>("_currentLineOfSightDeltaAngle");
    internal static float RecoilKick = 1f;
    private static bool _mounted;
    private static Quaternion _viewKick = Quaternion.identity;
    private static int _shotFrame = -1;
    private static Vector3? _shotFireport;
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
        MethodInfo? findAim = AccessTools.Method(typeof(ProceduralWeaponAnimation), "FindAimTransforms");
        if (findAim != null)
            harmony.Patch(findAim, postfix: new HarmonyMethod(typeof(IronSightAlignment), nameof(IronSightAlignment.Invalidate)));
        MethodInfo? rotateHead = AccessTools.Method(typeof(PlayerBones), nameof(PlayerBones.RotateHead));
        MethodInfo? rotateHeadPrefix = typeof(AimArms).GetMethod(nameof(RotateHeadPrefix), BindingFlags.NonPublic | BindingFlags.Static);
        MethodInfo? rotateHeadPostfix = typeof(AimArms).GetMethod(nameof(RotateHeadPostfix), BindingFlags.NonPublic | BindingFlags.Static);
        if (rotateHead != null && rotateHeadPrefix != null && rotateHeadPostfix != null)
            harmony.Patch(rotateHead, prefix: new HarmonyMethod(rotateHeadPrefix), postfix: new HarmonyMethod(rotateHeadPostfix) { priority = Priority.Last });
        MethodInfo? fireMode = AccessTools.Method(typeof(FirearmsAnimator), nameof(FirearmsAnimator.SetFireMode));
        MethodInfo? fireModeCheck = AccessTools.Method(typeof(FirearmsAnimator), nameof(FirearmsAnimator.TriggerFiremodeCheck));
        MethodInfo? fireModePostfix = typeof(AimArms).GetMethod(nameof(FireModePostfix), BindingFlags.NonPublic | BindingFlags.Static);
        MethodInfo? fireModeCheckPostfix = typeof(AimArms).GetMethod(nameof(FireModeCheckPostfix), BindingFlags.NonPublic | BindingFlags.Static);
        if (fireMode != null && fireModePostfix != null)
            harmony.Patch(fireMode, postfix: new HarmonyMethod(fireModePostfix));
        if (fireModeCheck != null && fireModeCheckPostfix != null)
            harmony.Patch(fireModeCheck, postfix: new HarmonyMethod(fireModeCheckPostfix));
        MethodInfo? adjustShot = AccessTools.Method(typeof(Player.FirearmController), nameof(Player.FirearmController.AdjustShotVectors));
        MethodInfo? adjustShotPrefix = typeof(AimArms).GetMethod(nameof(AdjustShotPrefix), BindingFlags.NonPublic | BindingFlags.Static);
        if (adjustShot != null && adjustShotPrefix != null)
            harmony.Patch(adjustShot, prefix: new HarmonyMethod(adjustShotPrefix));
        MethodInfo? aim = AccessTools.Method(typeof(Physical), nameof(Physical.Aim));
        MethodInfo? aimPrefix = typeof(AimArms).GetMethod(nameof(AimPrefix), BindingFlags.NonPublic | BindingFlags.Static);
        if (aim != null && aimPrefix != null)
            harmony.Patch(aim, prefix: new HarmonyMethod(aimPrefix));
        MethodInfo? holdBreath = AccessTools.Method(typeof(Physical), nameof(Physical.HoldBreath));
        MethodInfo? holdBreathPostfix = typeof(AimArms).GetMethod(nameof(HoldBreathPostfix), BindingFlags.NonPublic | BindingFlags.Static);
        if (holdBreath != null && holdBreathPostfix != null)
            harmony.Patch(holdBreath, postfix: new HarmonyMethod(holdBreathPostfix));
        MethodInfo? visualPass = AccessTools.Method(typeof(Player), "VisualPass");
        MethodInfo? visualPassPostfix = typeof(AimArms).GetMethod(nameof(VisualPassPostfix), BindingFlags.NonPublic | BindingFlags.Static);
        if (visualPass != null && visualPassPostfix != null)
            harmony.Patch(visualPass, postfix: new HarmonyMethod(visualPassPostfix) { priority = Priority.Last });
        MethodInfo? shot = AccessTools.Method(typeof(Player), nameof(Player.OnMakingShot));
        MethodInfo? shotTranspiler = typeof(AimArms).GetMethod(nameof(SkipMountedBodyRecoil), BindingFlags.NonPublic | BindingFlags.Static);
        if (shot != null && shotTranspiler != null)
            harmony.Patch(shot, transpiler: new HarmonyMethod(shotTranspiler));
        MethodInfo? movement = AccessTools.Method(typeof(Player), nameof(Player.MovementUpdate));
        MethodInfo? movementPostfix = typeof(AimArms).GetMethod(nameof(MovementUpdatePostfix), BindingFlags.NonPublic | BindingFlags.Static);
        if (movement != null && movementPostfix != null)
            harmony.Patch(movement, postfix: new HarmonyMethod(movementPostfix));
        MethodInfo? look = AccessTools.Method(typeof(Player), nameof(Player.Look));
        MethodInfo? lookPrefix = typeof(AimArms).GetMethod(nameof(LookPrefix), BindingFlags.NonPublic | BindingFlags.Static);
        MethodInfo? lookPostfix = typeof(AimArms).GetMethod(nameof(LookPostfix), BindingFlags.NonPublic | BindingFlags.Static);
        if (look != null && lookPrefix != null && lookPostfix != null)
            harmony.Patch(look, prefix: new HarmonyMethod(lookPrefix), postfix: new HarmonyMethod(lookPostfix));
        PatchLaser(harmony);
        MethodInfo? visible = AccessTools.PropertyGetter(typeof(Player), nameof(Player.IsVisible));
        MethodInfo? visiblePostfix = typeof(AimArms).GetMethod(nameof(VisiblePostfix), BindingFlags.NonPublic | BindingFlags.Static);
        if (visible != null && visiblePostfix != null)
            harmony.Patch(visible, postfix: new HarmonyMethod(visiblePostfix));
        _patched = true;
    }

    // The movement update hands Aim_angle / DeltaRotation to the body animator. Measured in game, the
    // bones seen this frame follow the value handed over in the previous frame, so both are kept.
    private static void MovementUpdatePostfix(Player __instance)
    {
        if (!NewCameraSwitch.Enabled || !__instance.IsYourPlayer || __instance.MovementContext == null)
            return;

        if (_posedFrame != Time.frameCount)
        {
            _previousPosedMouse = _posedMouse;
            _previousPosedFrame = _posedFrame;
        }
        _posedMouse = __instance.MovementContext.Rotation;
        _posedFrame = Time.frameCount;
    }

    // Third-person VisualPass only places the weapon (sprint pose included) while the body
    // mesh is on screen. Looking up from the head camera takes the body out of view.
    private static void VisiblePostfix(Player __instance, ref bool __result)
    {
        if (!__result && NewCameraSwitch.Enabled && __instance.IsYourPlayer)
            __result = true;
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

    internal static void AlignToCamera(Player player, Vector3 cameraPosition, Quaternion cameraRotation)
    {
        AlignToCameraCore(player, cameraPosition, cameraRotation);
    }

    private static void AlignToCameraCore(Player player, Vector3 cameraPosition, Quaternion cameraRotation)
    {
        _mounted = player.MovementContext != null && player.MovementContext.IsInMountedState;
        UpdateHandAction(player);
        PlayerBones bones = player.PlayerBones;
        bool aiming = player.HandsController != null && player.HandsController.IsAiming;
        int sightId = 0;
        Vector3 target = Vector3.zero;
        Quaternion turn = Quaternion.identity;
        Vector3? sightPoint = null;
        if (bones != null)
        {
            ProceduralWeaponAnimation? animation = player.ProceduralWeaponAnimation;
            Transform? bone = animation != null && animation.ScopeAimTransforms != null && animation.ScopeAimTransforms.Count > 0
                ? animation.CurrentScope?.Bone
                : null;
            Vector3 sightPosition = bone != null ? bone.position : Vector3.zero;
            Vector3 sightForward = Vector3.forward;
            bool ironAligned = bone != null && animation != null && ResolveIronSight(animation, bone, out sightPosition, out sightForward);
            if (bone != null)
                sightPoint = sightPosition;
            if (PlantedAim(player))
            {
                // The mounted hip pose is already the aim pose. Leave the gun there.
                _offset = Vector3.zero;
                _turn = Quaternion.identity;
                _blend = 1f;
                _blendAiming = true;
                _blendSightId = 0;
                _hasLaserDisplacement = false;
                _viewKick = Quaternion.identity;
                return;
            }
            if (aiming && bone != null && animation != null)
            {
                sightId = bone.GetInstanceID();
                float relief = SightRelief(animation, bone);
                Vector3 aimPoint = new Vector3(0f, 0f, ForwardDistance + relief);
                Transform? root = bones.Weapon_Root_Anim;
                SteadyHead(player, cameraPosition, cameraRotation, out Vector3 steadyPosition, out Quaternion steadyRotation);
                Quaternion bodyRotation = _bodyLook * Quaternion.Inverse(_smoothLook) * steadyRotation;
                Vector3 bodyPosition = CameraPosition.Resolve(steadyPosition - CameraPosition.Resolve(Vector3.zero, steadyRotation), bodyRotation);
                if (bones.Weapon_Root_Third != null && root != null)
                    Sway(animation, bones.Weapon_Root_Third, root, bone, sightPosition, sightForward, ironAligned, sightId, bodyPosition, bodyRotation, aimPoint, out target, out turn);
                else
                {
                    target = bodyPosition - sightPosition + bodyRotation * aimPoint;
                    if (ironAligned)
                        turn = Quaternion.FromToRotation(sightForward, bodyRotation * Vector3.forward);
                }

                Quaternion delay = _lagLook * Quaternion.Inverse(_bodyLook);
                Vector3 placed = sightPosition + target;
                target = steadyPosition + delay * (placed - bodyPosition) - sightPosition;
                turn = delay * turn;
            }
            else if (!aiming && FollowDelay > 0f && player.HandsController is Player.FirearmController && bones.Weapon_Root_Anim != null)
            {
                Quaternion delay = _lagLook * Quaternion.Inverse(_smoothLook);
                if (Settled(delay))
                    delay = Quaternion.identity;
                sightPoint = cameraPosition;
                turn = delay;
            }
        }

        if (!aiming)
        {
            _hasMean = false;
            _hasHeadMean = false;
        }

        if (!aiming && !_blendAiming && _offset.sqrMagnitude < 0.00000001f && Settled(_turn) && Settled(turn) && _blend >= 1f)
        {
            _hasLaserDisplacement = false;
            _viewKick = _mounted && bones?.Weapon_Root_Anim != null
                ? ShotKick(player.ProceduralWeaponAnimation, bones.Weapon_Root_Anim.rotation)
                : Quaternion.identity;
            return;
        }

        if (bones == null)
        {
            _hasLaserDisplacement = false;
            _viewKick = Quaternion.identity;
            return;
        }

        if (aiming != _blendAiming || sightId != _blendSightId)
        {
            _blendFrom = _offset;
            _turnFrom = _turn;
            _blend = 0f;
            _blendAiming = aiming;
            _blendSightId = sightId;
        }

        if (_blend < 1f)
        {
            _blend = Mathf.Min(1f, _blend + Time.deltaTime / BlendSeconds);
            float t = _blend * _blend * (3f - 2f * _blend);
            _offset = Vector3.Lerp(_blendFrom, target, t);
            _turn = Quaternion.Slerp(_turnFrom, turn, t);
        }
        else
        {
            _offset = target;
            _turn = turn;
        }

        Vector3 displacement = _offset;
        Quaternion carryTurn = _turn;
        Vector3 pivot = sightPoint ?? (bones.Weapon_Root_Anim != null ? bones.Weapon_Root_Anim.position : Vector3.zero);
        Quaternion kick = Quaternion.identity;
        if (aiming && !_mounted)
            kick = Recoil(player.ProceduralWeaponAnimation, bones.Weapon_Root_Anim, pivot, ref displacement, ref carryTurn);
        else if (!aiming && _mounted && bones.Weapon_Root_Anim != null)
            kick = ShotKick(player.ProceduralWeaponAnimation, bones.Weapon_Root_Anim.rotation);
        _viewKick = !aiming && _mounted ? kick : Quaternion.identity;
        _laserDisplacement = displacement;
        _laserPivot = pivot;
        _laserTurn = carryTurn;
        _hasLaserDisplacement = displacement.sqrMagnitude >= 0.00000001f || !Settled(carryTurn);
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
                Carry(arm, pivot, displacement, carryTurn);
        }

        Transform? weapon = bones.Weapon_Root_Anim;
        if (weapon == null)
            return;
        bool carried = false;
        foreach (Transform arm in Upperarms)
        {
            if (arm != null && weapon.IsChildOf(arm))
                carried = true;
        }

        if (!carried)
            Carry(weapon, pivot, displacement, carryTurn);
        _shotFrame = Time.frameCount;
        _shotFireport = player.HandsController is Player.FirearmController firearm && firearm.CurrentFireport?.Original != null
            ? firearm.CurrentFireport.Original.position
            : (Vector3?)null;
    }

    private static void StartHandAction(float minSeconds)
    {
        _handAction = true;
        _handActionStart = Time.time;
        _handActionMin = minSeconds;
    }

    private static bool IsMainFirearmsAnimator(FirearmsAnimator animator)
    {
        return Singleton<GameWorld>.Instance?.MainPlayer?.HandsController is Player.FirearmController firearm
            && ReferenceEquals(firearm.FirearmsAnimator, animator);
    }

    private static void FireModePostfix(FirearmsAnimator __instance, bool skipAnimation)
    {
        if (NewCameraSwitch.Enabled && !skipAnimation && IsMainFirearmsAnimator(__instance))
            StartHandAction(FireModeMinSeconds);
    }

    private static void FireModeCheckPostfix(FirearmsAnimator __instance)
    {
        if (NewCameraSwitch.Enabled && IsMainFirearmsAnimator(__instance))
            StartHandAction(FireModeMinSeconds);
    }

    // The action lasts until the hands layer is back in idle, but at least long enough for the
    // trigger to reach the animator.
    private static void UpdateHandAction(Player player)
    {
        if (!_handAction)
            return;

        float elapsed = Time.time - _handActionStart;
        bool idle = true;
        try
        {
            FirearmsAnimator? animator = (player.HandsController as Player.FirearmController)?.FirearmsAnimator;
            if (animator != null)
                idle = animator.IsIdling() && !animator.Animator.IsInTransition(FirearmsAnimator.HANDS_LAYER_INDEX);
        }
        catch
        {
        }
        if (elapsed >= HandActionMaxSeconds || (elapsed >= _handActionMin && idle))
            _handAction = false;
    }

    private static void AdjustShotPrefix(Player.FirearmController __instance, ref Vector3 position, ref Vector3 direction)
    {
        if (NewCameraSwitch.Enabled && __instance.Item is Weapon { BoltAction: true }
            && ReferenceEquals(Singleton<GameWorld>.Instance?.MainPlayer?.HandsController, __instance))
            StartHandAction(BoltMinSeconds);
        if (!NewCameraSwitch.Enabled || !_hasLaserDisplacement || Time.frameCount - _shotFrame > 2)
            return;
        Player? player = Singleton<GameWorld>.Instance?.MainPlayer;
        if (player == null || !ReferenceEquals(player.HandsController, __instance))
            return;
        if (_shotFireport.HasValue && (__instance.CurrentFireport?.Original?.position - _shotFireport.Value)?.sqrMagnitude < 0.000001f)
            return;

        position = _laserPivot + _laserDisplacement + _laserTurn * (position - _laserPivot);
        direction = _laserTurn * direction;
    }

    internal static void CarryOptic(Camera camera)
    {
        if (!_hasLaserDisplacement || _opticFrame == Time.frameCount)
            return;

        _opticFrame = Time.frameCount;
        Carry(camera.transform, _laserPivot, _laserDisplacement, _laserTurn);
    }

    // Quaternion.Angle and == treat anything under about 0.16 degrees as no turn at all,
    // which shows as a visible hop when the last bit of the follow delay is dropped.
    private static bool Settled(Quaternion turn)
    {
        float sine = new Vector3(turn.x, turn.y, turn.z).magnitude;
        return 2f * Mathf.Atan2(sine, Mathf.Abs(turn.w)) * Mathf.Rad2Deg < 0.005f;
    }

    private static void Carry(Transform transform, Vector3 pivot, Vector3 displacement, Quaternion turn)
    {
        transform.SetPositionAndRotation(pivot + displacement + turn * (transform.position - pivot), turn * transform.rotation);
    }

    private static Quaternion Recoil(ProceduralWeaponAnimation? animation, Transform? weapon, Vector3 pivot, ref Vector3 displacement, ref Quaternion turn)
    {
        if (animation == null || weapon == null || animation.HandsContainer == null)
            return Quaternion.identity;

        Quaternion weaponRotation = turn * weapon.rotation;
        Quaternion kick = ShotKick(animation, weaponRotation);
        if (Settled(kick))
            return Quaternion.identity;

        Vector3 weaponPosition = pivot + displacement + turn * (weapon.position - pivot);
        Vector3 localPivot = animation.HandsContainer.RecoilPivot;
        if (animation.IsMountedState)
            localPivot = animation.IsBipodUsed && !animation.IsVerticalMounting
                ? animation.HandsContainer.MountingRotationCenterBipods
                : animation.HandsContainer.MountingRotationCenter;
        Vector3 recoilPivot = weaponPosition + weaponRotation * localPivot;
        displacement = recoilPivot + kick * (pivot + displacement - recoilPivot) - pivot;
        turn = kick * turn;
        return kick;
    }

    private static Quaternion ShotKick(ProceduralWeaponAnimation? animation, Quaternion space)
    {
        if (animation == null || animation.Shootingg?.CurrentRecoilEffect == null || RecoilKick <= 0f)
            return Quaternion.identity;

        Vector3 recoil = animation.Shootingg.CurrentRecoilEffect.GetHandRotationRecoil();
        if (animation.Shootingg.CurrentRecoilEffect.WeaponRecoilEffect != null)
            recoil += animation.Shootingg.CurrentRecoilEffect.WeaponRecoilEffect.GetRotationRecoil();
        if (recoil.sqrMagnitude < 0.00000001f)
            return Quaternion.identity;

        return Quaternion.Euler(space * (recoil * RecoilKick));
    }

    // A bipod and a scene gun (the mounted machine gun or grenade launcher) both stay where they are.
    // Aiming moves the head onto that sight instead of moving the gun.
    private static bool PlantedAim(Player player)
    {
        if (player.HandsController == null || !player.HandsController.IsAiming || player.MovementContext == null)
            return false;

        return player.MovementContext.IsInMountedState || player.MovementContext.IsStationaryWeaponInHands;
    }

    internal static bool TrackMountedSight(Player player)
    {
        bool want = PlantedAim(player);
        PlayerBones? bones = player.PlayerBones;
        Transform? head = bones?.Head?.Original;
        if (bones == null || head == null || !SightCamera(player, out Vector3 cameraPosition, out Quaternion cameraRotation))
        {
            _mountBlend = 0f;
            return false;
        }

        _mountBlend = Mathf.MoveTowards(_mountBlend, want ? 1f : 0f, Time.deltaTime / BlendSeconds);
        float along = _mountBlend * _mountBlend * (3f - 2f * _mountBlend);
        if (along <= 0.0001f)
        {
            AimGrenadePicture(player);
            return false;
        }

        // The camera stays fixed on the head. Ease that camera onto the planted sight, and back off it.
        Quaternion fromCamera = CameraDirection.Resolve(head.rotation);
        Quaternion partialCamera = Quaternion.Slerp(fromCamera, cameraRotation, along);
        Vector3 partialCameraPosition = Vector3.Lerp(CameraPosition.Resolve(head.position, fromCamera), cameraPosition, along);
        Quaternion turn = CameraDirection.Unresolve(partialCamera) * Quaternion.Inverse(head.rotation);
        Vector3 displacement = partialCameraPosition - CameraPosition.Resolve(Vector3.zero, partialCamera) - head.position;
        Transform moved = FollowRoot(bones, head);
        HeldPose[] planted = PlantedPoses(bones, moved);
        EnsureUpperarms(player);
        EnsureBodySkins(player);
        Carry(moved, head.position, displacement, turn);
        CarryLoose(bones.Ribcage?.Original, moved, head.position, displacement, turn);
        CarryLoose(bones.Spine3?.Original, moved, head.position, displacement, turn);
        if (bones.Shoulders != null)
        {
            foreach (Transform shoulder in bones.Shoulders)
                CarryLoose(shoulder, moved, head.position, displacement, turn);
        }
        CarryLoose(bones.LeftShoulder?.Original, moved, head.position, displacement, turn);
        CarryLoose(bones.RightShoulder?.Original, moved, head.position, displacement, turn);
        for (int i = 0; i < planted.Length; i++)
            planted[i].Transform.SetPositionAndRotation(planted[i].Position, planted[i].Rotation);
        ApplyMountedLook(bones, head);
        AimGrenadePicture(player);
        return true;
    }

    // The aim pose is applied after the game turns the head, so it would wipe a free look.
    // The game look is withheld while mounted aiming, and put back on the aim pose here.
    private static void ApplyMountedLook(PlayerBones bones, Transform head)
    {
        if (_smoothedHead.sqrMagnitude < 0.0001f)
            return;

        Player? player = Singleton<GameWorld>.Instance?.MainPlayer;
        if (player != null && ReferenceEquals(player.PlayerBones, bones))
            ApplyLook(player, bones, _smoothedHead);
    }

    internal static float RecoilChest = 0.5f;
    internal static float RecoilHead = 0.5f;

    // The first-person camera kick of a shot goes into the chest and on into the head, which carries
    // the camera. The gun and hands keep their place in the world; only the body and view are pushed.
    internal static void BodyRecoil(Player player, ref Vector3 cameraPosition, ref Quaternion cameraRotation)
    {
        if (RecoilChest <= 0f && RecoilHead <= 0f)
            return;

        ProceduralWeaponAnimation? animation = player.ProceduralWeaponAnimation;
        PlayerBones? bones = player.PlayerBones;
        if (animation?.Shootingg?.CurrentRecoilEffect == null || bones == null || !(player.HandsController is Player.FirearmController))
            return;

        Vector3 recoil = animation.Shootingg.CurrentRecoilEffect.GetCameraRotationRecoil();
        if (animation.Shootingg.CurrentRecoilEffect.WeaponRecoilEffect != null)
            recoil += animation.Shootingg.CurrentRecoilEffect.WeaponRecoilEffect.GetCameraRotationRecoil();
        Quaternion kick = cameraRotation * Quaternion.Euler(recoil) * Quaternion.Inverse(cameraRotation);
        if (Settled(kick))
            return;

        Transform? chest = bones.Ribcage?.Original;
        Transform? neck = bones.Neck;
        Quaternion chestKick = Scale(kick, RecoilChest);
        Quaternion headKick = Scale(kick, RecoilHead);
        if (chest != null && !Settled(chestKick))
        {
            Transform upper = FollowRoot(bones, chest);
            List<HeldPose> held = new();
            Hold(held, bones.Weapon_Root_Anim, upper);
            Hold(held, bones.Weapon_Root_Third, upper);
            HoldAll(held, bones.Forearms, upper);
            Hold(held, bones.LeftPalm, upper);
            Hold(held, bones.RightPalm, upper);
            Vector3 pivot = chest.position;
            Carry(chest, pivot, Vector3.zero, chestKick);
            if (bones.Shoulders != null)
            {
                foreach (Transform shoulder in bones.Shoulders)
                    CarryLoose(shoulder, chest, pivot, Vector3.zero, chestKick);
            }
            CarryLoose(bones.LeftShoulder?.Original, chest, pivot, Vector3.zero, chestKick);
            CarryLoose(bones.RightShoulder?.Original, chest, pivot, Vector3.zero, chestKick);
            foreach (HeldPose pose in held)
                pose.Transform.SetPositionAndRotation(pose.Position, pose.Rotation);
            if (neck == null || neck.IsChildOf(chest))
            {
                cameraPosition = pivot + chestKick * (cameraPosition - pivot);
                cameraRotation = chestKick * cameraRotation;
            }
        }

        if (neck != null && !Settled(headKick))
        {
            Vector3 pivot = neck.position;
            Carry(neck, pivot, Vector3.zero, headKick);
            cameraPosition = pivot + headKick * (cameraPosition - pivot);
            cameraRotation = headKick * cameraRotation;
        }
    }

    private struct HeldPose
    {
        internal Transform Transform;
        internal Vector3 Position;
        internal Quaternion Rotation;
    }

    // The torso follows the head, from the waist up. Legs stay. A bone that is not under that
    // chain still has to take the same step, or the chest and shoulders stay in the hip pose.
    private static Transform FollowRoot(PlayerBones bones, Transform head)
    {
        Transform? pelvis = bones.Pelvis?.Original;
        Transform? root = bones.RootJoint;
        Transform best = head;
        Transform? cursor = head.parent;
        while (cursor != null && cursor != pelvis && cursor != root)
        {
            best = cursor;
            cursor = cursor.parent;
        }
        return best;
    }

    private static void CarryLoose(Transform? bone, Transform moved, Vector3 pivot, Vector3 displacement, Quaternion turn)
    {
        if (bone == null || bone == moved || bone.IsChildOf(moved) || moved.IsChildOf(bone))
            return;
        Carry(bone, pivot, displacement, turn);
    }

    private static HeldPose[] PlantedPoses(PlayerBones bones, Transform moved)
    {
        List<HeldPose> held = new();
        Hold(held, bones.Weapon_Root_Anim, moved);
        Hold(held, bones.Weapon_Root_Third, moved);
        HoldAll(held, bones.Forearms, moved);
        Hold(held, bones.LeftPalm, moved);
        Hold(held, bones.RightPalm, moved);
        return held.ToArray();
    }

    private static void HoldAll(List<HeldPose> held, Transform[]? transforms, Transform moved)
    {
        if (transforms == null)
            return;
        foreach (Transform transform in transforms)
            Hold(held, transform, moved);
    }

    private static void Hold(List<HeldPose> held, Transform? transform, Transform moved)
    {
        if (transform == null || transform == moved || !transform.IsChildOf(moved))
            return;

        // A child kept beside its parent gets a world pose while the parent is still moving,
        // which changes the grip. The parent's saved pose already brings the gun back with the hand.
        for (int i = held.Count - 1; i >= 0; i--)
        {
            Transform kept = held[i].Transform;
            if (transform == kept || transform.IsChildOf(kept))
                return;
            if (kept.IsChildOf(transform))
                held.RemoveAt(i);
        }

        held.Add(new HeldPose { Transform = transform, Position = transform.position, Rotation = transform.rotation });
    }

    private static bool SightCamera(Player player, out Vector3 position, out Quaternion rotation)
    {
        position = default;
        rotation = Quaternion.identity;
        ProceduralWeaponAnimation? animation = player.ProceduralWeaponAnimation;
        Transform? bone = animation != null && animation.ScopeAimTransforms != null && animation.ScopeAimTransforms.Count > 0
            ? animation.CurrentScope?.Bone
            : null;
        if (animation == null || bone == null)
            return false;

        ResolveIronSight(animation, bone, out Vector3 sightPosition, out Vector3 forward);
        if (forward.sqrMagnitude < 0.0001f)
            return false;
        forward.Normalize();
        bool ags = GrenadeLauncher(player);
        if (ags)
        {
            // The sight body only yaws. The picture inside the glass follows the barrel, and the eye sits on the sight.
            Vector3 eye = bone.position;
            Vector3 flat = Vector3.ProjectOnPlane(forward, Vector3.up);
            if (flat.sqrMagnitude < 0.0001f)
                return false;
            flat.Normalize();
            rotation = Quaternion.LookRotation(flat, Vector3.up);
            position = eye;
            return true;
        }

        Vector3 up = Vector3.ProjectOnPlane(bone.name == "aim_camera" ? bone.forward : bone.up, forward);
        if (up.sqrMagnitude < 0.0001f)
            up = Vector3.ProjectOnPlane(Vector3.up, forward);
        if (up.sqrMagnitude < 0.0001f)
            return false;
        if (MachineGun(player))
            up = -up;

        rotation = Quaternion.LookRotation(forward, up);
        position = sightPosition - forward * (ForwardDistance + SightRelief(animation, bone));
        return true;
    }

    private static bool GrenadeLauncher(Player player)
    {
        return StationaryHeld(player, out StationaryWeapon? weapon)
            && weapon!.Animation == StationaryWeapon.EStationaryAnimationType.AGS_17;
    }

    private static bool MachineGun(Player player)
    {
        return StationaryHeld(player, out StationaryWeapon? weapon)
            && weapon!.Animation != StationaryWeapon.EStationaryAnimationType.AGS_17;
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
    private static void AimGrenadePicture(Player player)
    {
        if (!GrenadeLauncher(player) || player.HandsController is not Player.FirearmController firearm || !firearm.IsAiming)
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

            OpticSight? sight = animation.CurrentScope?.ScopePrefabCache?.CurrentModOpticSight;
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

    internal static Quaternion KickView(Quaternion view)
    {
        return _viewKick * view;
    }

    private static void Sway(ProceduralWeaponAnimation animation, Transform third, Transform anim, Transform sightBone, Vector3 sight, Vector3 sightForward, bool ironAligned, int sightId, Vector3 cameraPosition, Quaternion cameraRotation, Vector3 aimPoint, out Vector3 displacement, out Quaternion turn)
    {
        Quaternion toCamera = Quaternion.Inverse(cameraRotation);
        Vector3 sightOnThird = third.position + third.rotation * (Quaternion.Inverse(anim.rotation) * (sight - anim.position));
        Vector3 armMove = toCamera * (sightOnThird - cameraPosition);
        Vector3 rawGunMove = toCamera * (sight - sightOnThird);
        Quaternion armTurn = toCamera * third.rotation;
        Quaternion rawGunTurn = Quaternion.Inverse(third.rotation) * anim.rotation;

        bool sightChanged = !_hasMean || sightId != _meanSightId;
        if (sightChanged)
        {
            _gunMoveSmooth = rawGunMove;
            _gunTurnSmooth = rawGunTurn;
            _lagLook = _smoothLook;
            _armMoveMean = armMove;
            _gunMoveMean = rawGunMove;
            _armTurnMean = armTurn;
            _gunTurnMean = rawGunTurn;
            _armTurnCenter = armTurn;
            _gunTurnCenter = rawGunTurn;
            _meanSightId = sightId;
            _meanStart = Time.time;
            _hasMean = true;
            _hasChase = false;
            _sightCatch = 0.35f;
        }
        else
        {
            float smooth = 1f - Mathf.Exp(-Time.deltaTime / GunSmoothSeconds);
            _gunMoveSmooth = Vector3.Lerp(_gunMoveSmooth, rawGunMove, smooth);
            _gunTurnSmooth = Quaternion.Slerp(_gunTurnSmooth, rawGunTurn, smooth).normalized;
        }

        Vector3 gunMove = _gunMoveSmooth;
        Quaternion gunTurn = _gunTurnSmooth;
        float meanSeconds = Time.time - _meanStart < AimInSeconds ? AimInMeanSeconds : MeanSeconds;
        float follow = 1f - Mathf.Exp(-Time.deltaTime / meanSeconds);
        _armMoveMean = Vector3.Lerp(_armMoveMean, armMove, follow);
        _gunMoveMean = Vector3.Lerp(_gunMoveMean, gunMove, follow);
        _armTurnMean = Quaternion.Slerp(_armTurnMean, armTurn, follow).normalized;
        _gunTurnMean = Quaternion.Slerp(_gunTurnMean, gunTurn, follow).normalized;

        Vector3 smoothSight = sight + cameraRotation * (gunMove - rawGunMove);
        float armMoveScale = _mounted ? 0f : Now.ArmMove;
        float gunMoveScale = _mounted ? 0f : Now.GunMove;
        float armTurnScale = _mounted ? 0f : Now.ArmTurn;
        float gunTurnScale = _mounted ? 0f : Now.GunTurn;
        float breathScale = _mounted ? 0f : Now.Breath;
        Vector3 sway = armMoveScale * (armMove - _armMoveMean) + gunMoveScale * (gunMove - _gunMoveMean);
        Vector3 pinned = toCamera * (cameraPosition + cameraRotation * (aimPoint + sway) - smoothSight);
        float side = 1f;
        bool chasing = Chase && _hasChase && (_handAction || _chaseCatching);
        if (Chase && _hasChase)
        {
            float speed = animation.AimingSpeed * animation.CameraSmoothBlender.Value;
            if (speed <= 0f)
                speed = 16f;
            side = 1f - Mathf.Exp(-speed * Time.deltaTime);
            float depth = 1f - Mathf.Exp(-animation.CameraSmoothTime * Time.deltaTime);
            _chase.x += (pinned.x - _chase.x) * side;
            _chase.y += (pinned.y - _chase.y) * side;
            _chase.z += (pinned.z - _chase.z) * depth;
        }
        else
        {
            _chase = pinned;
        }
        displacement = smoothSight - sight + cameraRotation * _chase;

        if (_sightCatch > 0f)
        {
            float catchUp = 1f - Mathf.Exp(-Time.deltaTime / 0.08f);
            _armTurnCenter = Quaternion.Slerp(_armTurnCenter, armTurn, catchUp).normalized;
            _gunTurnCenter = Quaternion.Slerp(_gunTurnCenter, rawGunTurn, catchUp).normalized;
            _sightCatch -= Time.deltaTime;
        }
        else
        {
            float slow = 1f - Mathf.Exp(-Time.deltaTime / BreathCenterSeconds);
            _armTurnCenter = Quaternion.Slerp(_armTurnCenter, _armTurnMean, slow).normalized;
            _gunTurnCenter = Quaternion.Slerp(_gunTurnCenter, _gunTurnMean, slow).normalized;
        }
        Quaternion armBase = _armTurnCenter * Scale(Quaternion.Inverse(_armTurnCenter) * _armTurnMean, breathScale);
        Quaternion gunBase = _gunTurnCenter * Scale(Quaternion.Inverse(_gunTurnCenter) * _gunTurnMean, breathScale);
        Quaternion keptArm = armBase * Scale(Quaternion.Inverse(_armTurnMean) * armTurn, armTurnScale);
        Quaternion keptGun = gunBase * Scale(Quaternion.Inverse(_gunTurnMean) * gunTurn, gunTurnScale);
        // The position above is always chased. The turn is chased only during a bolt cycle or fire mode
        // switch, so that animation shows first; otherwise chasing it lets breath sway through a held breath.
        Quaternion pinnedTurn = LineOfSight(animation, sightBone, sightForward, ironAligned, anim, armBase * gunBase) * keptArm * keptGun * Quaternion.Inverse(armTurn * rawGunTurn);
        // The catch-up after the action has a fixed length. Waiting for the chase to settle never ends
        // while breath sway keeps moving the target.
        if (_handAction)
            _chaseCatchStart = Time.time;
        if (chasing)
        {
            _chaseTurn = Quaternion.Slerp(_chaseTurn, pinnedTurn, side).normalized;
            if (!_handAction)
            {
                float t = Mathf.Clamp01((Time.time - _chaseCatchStart) / ChaseCatchSeconds);
                _chaseTurn = Quaternion.Slerp(_chaseTurn, pinnedTurn, t * t * (3f - 2f * t)).normalized;
                if (t >= 1f)
                    _chaseCatching = false;
            }
        }
        else
        {
            _chaseTurn = pinnedTurn;
        }
        _hasChase = true;
        if (_handAction)
            _chaseCatching = true;
        turn = cameraRotation * _chaseTurn * toCamera;
    }

    private static Quaternion LineOfSight(ProceduralWeaponAnimation animation, Transform sightBone, Vector3 sightForward, bool ironAligned, Transform anim, Quaternion weaponInCamera)
    {
        Vector3 axis = ironAligned ? sightForward : sightBone.name == "aim_camera" ? -sightBone.up : -sightBone.forward;
        Vector3 sightInCamera = weaponInCamera * (Quaternion.Inverse(anim.rotation) * axis);
        float zeroing = 0f;
        try
        {
            // The physical marker axis already contains the iron-sight slope.
            // Applying the native cached slope again would calibrate it twice.
            if (!ironAligned) zeroing = LineOfSightAngle(animation);
        }
        catch
        {
        }
        if (Mathf.Abs(zeroing) > 0.0001f)
            sightInCamera = Quaternion.AngleAxis(-zeroing, weaponInCamera * Vector3.right) * sightInCamera;
        return Quaternion.FromToRotation(sightInCamera, Vector3.forward);
    }

    private static void VisualPassPostfix(Player __instance)
    {
        if (!NewCameraSwitch.Enabled || !__instance.IsYourPlayer)
            return;

        _mounted = __instance.MovementContext != null && __instance.MovementContext.IsInMountedState;
        ApplyHeadLock(__instance);
        UpdateFreeLookReturn(__instance);
        SprintHeadPitch(__instance);
        CalmWalkHead(__instance);
    }

    private static Vector3 _freeLookFrom;
    private static float _freeLookWeight;
    private static bool _freeLookReturning;
    private static bool _pitchUnderLook;

    // While the button is held the head follows the free look. After release, that offset
    // shrinks back to the sprint pose; the pose corrections have to grow in step with it,
    // or the head visits the raw animation and then jumps.
    private static void UpdateFreeLookReturn(Player player)
    {
        Vector3 raw = player.HeadRotation;
        float now = Mathf.Max(_smoothedHead.magnitude, raw.magnitude);
        bool live = raw.sqrMagnitude > 0.01f || _smoothedHead.sqrMagnitude > 0.01f;
        if (!live)
        {
            _freeLookReturning = false;
            _freeLookFrom = Vector3.zero;
            _freeLookWeight = 0f;
            return;
        }

        bool holding = !_freeLookReturning && (player.MouseLookControl || !player.IsResettingLook);
        if (holding)
        {
            _freeLookFrom = _smoothedHead.magnitude >= raw.magnitude ? _smoothedHead : raw;
            _freeLookWeight = 1f;
            return;
        }

        _freeLookReturning = true;
        if (_freeLookFrom.sqrMagnitude < 0.01f)
            _freeLookFrom = _smoothedHead.magnitude >= raw.magnitude ? _smoothedHead : raw;
        float span = _freeLookFrom.magnitude;
        _freeLookWeight = span < 0.01f ? 0f : Mathf.Clamp01(now / span);
    }

    private const float SprintPitchSeconds = 0.2f;
    private static float _sprintPitch;
    private const float PitchCenterSeconds = 0.5f;
    private static bool _hasPitchCenter;
    private static float _pitchCenter;

    // Sprint animation holds the head level; third-person RotateHead only adds free look, not mouse pitch.
    private static void SprintHeadPitch(Player player)
    {
        Transform? head = player.PlayerBones?.Head?.Original;
        if (head == null || player.MovementContext == null)
            return;

        bool sprinting = player.MovementContext.CurrentState != null
            && player.MovementContext.CurrentState.Name == EPlayerState.Sprint
            && (player.HandsController == null || !player.HandsController.IsAiming);
        _sprintPitch = Mathf.MoveTowards(_sprintPitch, sprinting ? 1f : 0f, Time.deltaTime / SprintPitchSeconds);

        if (_freeLookWeight <= 0f)
        {
            Vector3 forward = CameraDirection.Resolve(head.rotation) * Vector3.forward;
            float headPitch = -Mathf.Asin(Mathf.Clamp(forward.y, -1f, 1f)) * Mathf.Rad2Deg;
            if (!_hasPitchCenter)
            {
                _pitchCenter = headPitch;
                _hasPitchCenter = true;
            }
            else
                _pitchCenter = Mathf.Lerp(_pitchCenter, headPitch, 1f - Mathf.Exp(-Time.deltaTime / PitchCenterSeconds));
        }

        float sprintBlend = _sprintPitch * _sprintPitch * (3f - 2f * _sprintPitch);
        bool underLook = _pitchUnderLook;
        _pitchUnderLook = false;
        if (underLook || sprintBlend <= 0f)
            return;

        float missing = Mathf.DeltaAngle(_pitchCenter, player.MovementContext.Pitch) * sprintBlend;
        if (Mathf.Abs(missing) < 0.01f)
            return;

        ApplySprintPitch(player, missing);
    }

    private static bool SprintPitchAngle(Player player, out float missing)
    {
        missing = 0f;
        if (player.MovementContext == null)
            return false;

        float sprintBlend = _sprintPitch * _sprintPitch * (3f - 2f * _sprintPitch);
        if (sprintBlend <= 0f)
            return false;

        missing = Mathf.DeltaAngle(_pitchCenter, player.MovementContext.Pitch) * sprintBlend;
        return Mathf.Abs(missing) >= 0.01f;
    }

    private static void ApplySprintPitch(Player player, float missing)
    {
        if (player.MovementContext == null)
            return;

        Vector3 axis = Quaternion.Euler(0f, player.MovementContext.Rotation.x, 0f) * Vector3.right;
        Transform? neck = player.PlayerBones?.Neck;
        Transform? head = player.PlayerBones?.Head?.Original;
        float neckTurn = neck != null ? missing * 0.5f : 0f;
        if (neck != null)
            neck.rotation = Quaternion.AngleAxis(neckTurn, axis) * neck.rotation;
        if (head != null)
            head.rotation = Quaternion.AngleAxis(missing - neckTurn, axis) * head.rotation;
    }

    private static void ApplyHeadLock(Player player)
    {
        if (_headLock <= 0f)
            return;

        Transform? head = player.PlayerBones?.Head?.Original;
        if (head == null)
            return;

        float weight = _headLock * _headLock * (3f - 2f * _headLock);
        head.rotation = Quaternion.Slerp(head.rotation, CameraDirection.Unresolve(_smoothLook * AimTurn()), weight);
    }

    internal static void CalmWalkHead(Player player)
    {
        Transform? head = player.PlayerBones?.Head?.Original;
        if (head == null || player.MovementContext == null || _freeLookWeight >= 1f)
            return;

        // The turn about the vertical is read from the head's whole rotation, not from a flattened
        // view direction: flattening blows a small run tilt up into a large turn when looking far up or down.
        Quaternion local = Quaternion.Inverse(Quaternion.Euler(0f, player.MovementContext.Rotation.x, 0f)) * CameraDirection.Resolve(head.rotation);
        if (_freeLookWeight <= 0f)
        {
            if (!_hasYawCenter)
            {
                _lookCenter = local;
                _hasYawCenter = true;
            }
            else
                _lookCenter = Quaternion.Slerp(_lookCenter, local, 1f - Mathf.Exp(-Time.deltaTime / 0.5f)).normalized;
        }

        Quaternion delta = local * Quaternion.Inverse(_lookCenter);
        float yaw = Mathf.DeltaAngle(0f, 2f * Mathf.Atan2(delta.y, delta.w) * Mathf.Rad2Deg);
        float moving = Mathf.Clamp01(player.MovementContext.ActualLinearVelocity / 0.6f);
        float keep = Mathf.Lerp(1f, WalkHeadSway, moving);
        float fix = -yaw * (1f - keep) * (1f - _freeLookWeight);
        if (Mathf.Abs(fix) > 0.01f)
            head.rotation = Quaternion.AngleAxis(fix, Vector3.up) * head.rotation;
    }

    internal static void LiftHead(Transform head)
    {
        if (Mathf.Abs(HeadModelPitch) < 0.01f)
            return;

        Vector3 axis = CameraDirection.Resolve(head.rotation) * Vector3.right;
        head.rotation = Quaternion.AngleAxis(-HeadModelPitch, axis) * head.rotation;
    }

    internal static void ObserveHead(Player player, Vector3 cameraPosition, Quaternion cameraRotation)
    {
        _hasObserved = false;
        Transform? neck = player.PlayerBones?.Neck;
        if (neck == null)
            return;

        if (_turnFrame != Time.frameCount && !UpdateTurn(player, cameraRotation))
            return;

        _neckPosition = neck.position;
        _headPositionNow = Quaternion.Inverse(_smoothLook) * (cameraPosition - _neckPosition);
        _hasObserved = true;
    }

    private static void UpdateBreathState(Player player)
    {
        bool holding = false;
        bool tired = false;
        try
        {
            if (player.Physical != null)
            {
                holding = player.Physical.HoldingBreath;
                tired = player.Physical.HandsStamina != null && player.Physical.HandsStamina.Exhausted;
                if (NewCameraSwitch.Enabled && player.Physical is Physical physical)
                    ApplyAimDrain(physical);
            }
        }
        catch
        {
        }

        float step = Time.deltaTime / StateBlendSeconds;
        _holdWeight = Mathf.MoveTowards(_holdWeight, holding ? 1f : 0f, step);
        _tiredWeight = Mathf.MoveTowards(_tiredWeight, tired ? 1f : 0f, step);
        float hold = Smooth(_holdWeight);
        float tiredness = Smooth(_tiredWeight);
        float Mix(float normal, float held, float exhausted) => Mathf.Lerp(Mathf.Lerp(normal, held, hold), exhausted, tiredness);
        Now.GunMove = Mix(Normal.GunMove, Holding.GunMove, Exhausted.GunMove);
        Now.GunTurn = Mix(Normal.GunTurn, Holding.GunTurn, Exhausted.GunTurn);
        Now.ArmMove = Mix(Normal.ArmMove, Holding.ArmMove, Exhausted.ArmMove);
        Now.ArmTurn = Mix(Normal.ArmTurn, Holding.ArmTurn, Exhausted.ArmTurn);
        Now.HeadFollow = Mix(Normal.HeadFollow, Holding.HeadFollow, Exhausted.HeadFollow);
        Now.Breath = Mix(Normal.Breath, Holding.Breath, Exhausted.Breath);
    }

    private static float Smooth(float t)
    {
        return t * t * (3f - 2f * t);
    }

    private static bool AimPrefix(Physical __instance, float mass)
    {
        Player? player = Singleton<GameWorld>.Instance?.MainPlayer;
        if (player == null || !ReferenceEquals(player.Physical, __instance))
            return true;
        if (!NewCameraSwitch.Enabled)
        {
            SetAimDrain(__instance, false);
            return true;
        }

        __instance._aimMass = mass;
        __instance.Consumptions[Physical.EConsumptionType.Aim].Delta.SetDirty();
        ApplyAimDrain(__instance);
        return false;
    }

    private static void HoldBreathPostfix(Physical __instance)
    {
        Player? player = Singleton<GameWorld>.Instance?.MainPlayer;
        if (player == null || !ReferenceEquals(player.Physical, __instance) || !NewCameraSwitch.Enabled)
            return;
        ApplyAimDrain(__instance);
    }

    private static void ApplyAimDrain(Physical physical)
    {
        SetAimDrain(physical, physical._aimMass > 0f && physical.HoldingBreath);
    }

    private static void SetAimDrain(Physical physical, bool active)
    {
        if (!ReferenceEquals(_drainOwner, physical))
        {
            _drainOwner = physical;
            _aimDrain = false;
        }
        if (_aimDrain == active)
            return;

        _aimDrain = active;
        Physical.Consumption consumption = physical.Consumptions[Physical.EConsumptionType.Aim];
        consumption.SetActive(physical, active);
        if (!active)
            physical.Stamina.RemoveConsumption(consumption);
    }

    private static bool UpdateTurn(Player player, Quaternion rawCamera)
    {
        Vector2 mouse;
        try
        {
            if (player.MovementContext == null)
                return false;
            mouse = player.MovementContext.Rotation;
        }
        catch
        {
            return false;
        }

        Quaternion look = Quaternion.Euler(mouse.y, mouse.x, 0f);
        bool continuous = _turnFrame == Time.frameCount - 1;
        if (_posedFrame == Time.frameCount && _previousPosedFrame == Time.frameCount - 1)
            _bodyLook = Quaternion.Euler(_previousPosedMouse.y, _previousPosedMouse.x, 0f);
        else
            _bodyLook = continuous ? _smoothLook : look;
        _turnFrame = Time.frameCount;
        _smoothLook = look;

        if (!_hasCenter || FollowDelay <= 0f)
            _lagLook = _smoothLook;
        else
            _lagLook = Quaternion.Slerp(_lagLook, _smoothLook, 1f - Mathf.Exp(-Time.deltaTime / FollowDelay)).normalized;
        float behind = Quaternion.Angle(_smoothLook, _lagLook);
        if (behind > MaxDelayDegrees)
            _lagLook = Quaternion.Slerp(_smoothLook, _lagLook, MaxDelayDegrees / behind).normalized;

        UpdateBreathState(player);

        Quaternion rawTurn = Quaternion.Inverse(_bodyLook) * rawCamera;
        _headTurnRaw = rawTurn;
        if (!_hasCenter)
        {
            _headTurnSmooth = rawTurn;
            _hasCenter = true;
        }
        else
        {
            _headTurnSmooth = Quaternion.Slerp(_headTurnSmooth, rawTurn, 1f - Mathf.Exp(-Time.deltaTime / HeadSmoothSeconds)).normalized;
        }

        return true;
    }

    private static Quaternion Lean(Quaternion headTurn)
    {
        Vector4 twist = new Vector4(0f, 0f, headTurn.z, headTurn.w);
        float length = twist.magnitude;
        return length < 0.0001f ? Quaternion.identity : new Quaternion(0f, 0f, twist.z / length, twist.w / length);
    }

    private static Quaternion AimTurn()
    {
        Quaternion sway = _headTurnSmooth * Quaternion.Inverse(Lean(_headTurnSmooth));
        return Scale(sway, _mounted ? 0f : Now.HeadFollow) * Lean(_headTurnRaw);
    }

    internal static float FreeLookYaw = 70f;
    internal static float FreeLookDownExtra;
    private static Vector2 _lookLimitSaved;
    private static Vector2 _verticalLimitSaved;
    private static bool _lookLimitSwapped;

    private static void LookPrefix(Player __instance)
    {
        _lookLimitSwapped = false;
        if (!NewCameraSwitch.Enabled || !__instance.IsYourPlayer)
            return;

        EFTHardSettings settings = EFTHardSettings.Instance;
        if (settings == null)
            return;

        _lookLimitSaved = settings.MOUSE_LOOK_HORIZONTAL_LIMIT;
        float scale = 1f;
        if (__instance.HandsController != null && __instance.HandsController.IsAiming)
            scale = settings.MOUSE_LOOK_LIMIT_IN_AIMING_COEF;
        if (scale < 0.01f)
            scale = 1f;
        float reach = FreeLookYaw / scale;
        settings.MOUSE_LOOK_HORIZONTAL_LIMIT = new Vector2(-reach, reach);
        _verticalLimitSaved = settings.MOUSE_LOOK_VERTICAL_LIMIT;
        settings.MOUSE_LOOK_VERTICAL_LIMIT = new Vector2(_verticalLimitSaved.x, _verticalLimitSaved.y + FreeLookDownExtra);
        _lookLimitSwapped = true;
    }

    private static void LookPostfix()
    {
        if (!_lookLimitSwapped)
            return;

        _lookLimitSwapped = false;
        EFTHardSettings settings = EFTHardSettings.Instance;
        if (settings != null)
        {
            settings.MOUSE_LOOK_HORIZONTAL_LIMIT = _lookLimitSaved;
            settings.MOUSE_LOOK_VERTICAL_LIMIT = _verticalLimitSaved;
        }
    }

    private static void RotateHeadPostfix(PlayerBones __instance, Vector3 offset)
    {
        if (NewCameraSwitch.Enabled)
            TurnLook(__instance);

        if (!NewCameraSwitch.Enabled || _turnFrame == Time.frameCount)
            return;

        Player? player = Singleton<GameWorld>.Instance?.MainPlayer;
        Transform? head = __instance.Head?.Original;
        if (player == null || head == null || !ReferenceEquals(player.PlayerBones, __instance))
            return;

        _rotateHeadFrame = Time.frameCount;
        if (!UpdateTurn(player, CameraDirection.Resolve(head.rotation)))
            return;

        bool aiming = player.HandsController != null && player.HandsController.IsAiming;
        float target = aiming && _smoothedHead.sqrMagnitude <= 0.01f ? 1f : 0f;
        _headLock = Mathf.MoveTowards(_headLock, target, Time.deltaTime / HeadLockSeconds);
    }

    private static void SteadyHead(Player player, Vector3 cameraPosition, Quaternion cameraRotation, out Vector3 position, out Quaternion rotation)
    {
        position = cameraPosition;
        rotation = cameraRotation;
        if (!_hasObserved)
            return;

        if (_smoothedHead.sqrMagnitude > 0.01f)
        {
            if (!_hasHeadMean)
            {
                _heldHeadPosition = _headPositionNow;
                _heldHeadTurn = AimTurn();
                _hasHeadMean = true;
            }
            _release = 1f;
            position = _neckPosition + _smoothLook * _heldHeadPosition;
            rotation = _smoothLook * _heldHeadTurn;
            return;
        }

        if (!_hasHeadMean)
        {
            _release = 0f;
            _hasHeadMean = true;
        }

        Vector3 steadyPosition = _headPositionNow;
        Quaternion steadyTurn = AimTurn();
        if (_release > 0f)
        {
            _release = Mathf.Max(0f, _release - Time.deltaTime / ReleaseSeconds);
            float held = _release * _release * (3f - 2f * _release);
            steadyPosition = Vector3.Lerp(steadyPosition, _heldHeadPosition, held);
            steadyTurn = Quaternion.Slerp(steadyTurn, _heldHeadTurn, held);
        }

        _heldHeadPosition = steadyPosition;
        _heldHeadTurn = steadyTurn;
        position = _neckPosition + _smoothLook * steadyPosition;
        rotation = _smoothLook * steadyTurn;
    }

    private static void RotateHeadPrefix(PlayerBones __instance, ref Vector3 offset)
    {
        if (!NewCameraSwitch.Enabled)
            return;

        Player? player = Comfort.Common.Singleton<GameWorld>.Instance?.MainPlayer;
        if (player == null || !ReferenceEquals(player.PlayerBones, __instance))
            return;

        if (_headSmoothFrame != Time.frameCount)
        {
            _headSmoothFrame = Time.frameCount;
            _smoothedHead = Vector3.Lerp(_smoothedHead, offset, 1f - Mathf.Exp(-Time.deltaTime / HeadSmoothSeconds));
            if (offset == Vector3.zero && _smoothedHead.sqrMagnitude < 0.0001f)
                _smoothedHead = Vector3.zero;
        }

        offset = _smoothedHead;
        if (PlantedAim(player))
            offset = Vector3.zero;
        _look = offset;
        offset = Vector3.zero;
        if (player.HeadRotation.sqrMagnitude <= 0.01f && _smoothedHead.sqrMagnitude <= 0.01f)
            return;
        if (!SprintPitchAngle(player, out float missing))
            return;

        // Free look is applied on top of this rotation. Pitch has to be underneath it,
        // otherwise starting a look drops the head back to the level sprint pose first.
        ApplySprintPitch(player, missing);
        _pitchUnderLook = true;
    }

    internal static float LookAxisForward;
    private static Vector3 _look;
    private static float _lookYawSign = 1f;

    // The game turns the free look about the neck and head bones' own axes, which lean forward
    // with the neck, and splits it so the head falls short of the full angle. The whole look is
    // turned at the neck instead: down about the body's right, then sideways about the set axis.
    private static void TurnLook(PlayerBones bones)
    {
        Player? player = Singleton<GameWorld>.Instance?.MainPlayer;
        if (player == null || !ReferenceEquals(player.PlayerBones, bones))
            return;

        Vector3 look = _look;
        _look = Vector3.zero;
        ApplyLook(player, bones, look);
    }

    private static void ApplyLook(Player player, PlayerBones bones, Vector3 offset)
    {
        float yaw = offset.x;
        float down = -offset.z;
        if (Mathf.Abs(yaw) < 0.01f && Mathf.Abs(down) < 0.01f)
            return;

        Transform? pivot = bones.Neck != null ? bones.Neck : bones.Head?.Original;
        if (pivot == null || player.MovementContext == null)
            return;

        float up = Vector3.Dot(pivot.rotation * Vector3.right, Vector3.up);
        if (Mathf.Abs(up) > 0.3f)
            _lookYawSign = Mathf.Sign(up);
        Quaternion body = Quaternion.Euler(0f, player.MovementContext.Rotation.x, 0f);
        Vector3 axis = body * (Quaternion.Euler(LookAxisForward, 0f, 0f) * Vector3.up);
        Quaternion turn = Quaternion.AngleAxis(yaw * _lookYawSign, axis) * Quaternion.AngleAxis(down, body * Vector3.right);
        pivot.rotation = turn * pivot.rotation;
    }

    private static Quaternion Scale(Quaternion rotation, float amount)
    {
        if (rotation.w < 0f)
            rotation = new Quaternion(-rotation.x, -rotation.y, -rotation.z, -rotation.w);
        return Quaternion.SlerpUnclamped(Quaternion.identity, rotation, amount);
    }

    private static bool ResolveIronSight(ProceduralWeaponAnimation animation, Transform bone, out Vector3 position, out Vector3 forward)
    {
        position = bone.position;
        forward = bone.name == "aim_camera" ? -bone.up : -bone.forward;
        var scope = animation.CurrentScope;
        Transform? weapon = animation.HandsContainer?.Weapon;
        return scope != null && weapon != null && IronSightAlignment.TryResolve(weapon, bone,
            scope.AlignIfAvailable, scope.IsOptic, out position, out forward);
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
                Quaternion savedRotation = emitter.rotation;
                if (_hasLaserDisplacement && !alreadyMoved)
                    Carry(emitter, _laserPivot, _laserDisplacement, _laserTurn);
                _laserLateUpdate.Invoke(laser, null);
                if (_hasLaserDisplacement && !alreadyMoved)
                    emitter.SetPositionAndRotation(saved, savedRotation);
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

    // First person returns before the body shot reaction. Mounting already shrinks the
    // hand recoil; the third-person reaction is the full punch and ignores the bipod.
    private static IEnumerable<CodeInstruction> SkipMountedBodyRecoil(IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo? getter = AccessTools.PropertyGetter(typeof(Player), nameof(Player.FirstPersonPointOfView));
        MethodInfo? skip = typeof(AimArms).GetMethod(nameof(OrMounted), BindingFlags.NonPublic | BindingFlags.Static);
        int seen = 0;
        foreach (CodeInstruction instruction in instructions)
        {
            yield return instruction;
            if (getter != null && skip != null && instruction.Calls(getter) && ++seen == 2)
            {
                yield return new CodeInstruction(OpCodes.Ldarg_0);
                yield return new CodeInstruction(OpCodes.Call, skip);
            }
        }
    }

    private static bool OrMounted(bool firstPerson, Player player)
    {
        if (firstPerson)
            return true;

        return NewCameraSwitch.Enabled && player.IsYourPlayer && player.MovementContext != null && player.MovementContext.IsInMountedState;
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
        if (!aiming && _offset.sqrMagnitude < 0.00000001f && Settled(_turn))
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
            {
                RememberSkin(renderer);
                continue;
            }

            RememberSkin(renderer);
        }
    }

    private static int _bodySkinOwner = int.MinValue;

    private static void EnsureBodySkins(Player player)
    {
        int ownerId = player.GetInstanceID();
        if (_bodySkinOwner == ownerId)
            return;

        _bodySkinOwner = ownerId;
        foreach (SkinnedMeshRenderer renderer in player.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (renderer != null)
                RememberSkin(renderer);
        }
    }

    private static void RememberSkin(SkinnedMeshRenderer renderer)
    {
        foreach (SkinState skin in Skins)
        {
            if (skin.Renderer == renderer)
                return;
        }

        Skins.Add(new SkinState
        {
            Renderer = renderer,
            ForceMatrix = renderer.forceMatrixRecalculationPerRender,
            UpdateWhenOffscreen = renderer.updateWhenOffscreen
        });
        renderer.forceMatrixRecalculationPerRender = true;
        renderer.updateWhenOffscreen = true;
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
        _bodySkinOwner = int.MinValue;
    }

    internal static void Release(Player? player)
    {
        _blendAiming = false;
        _blendSightId = 0;
        _blend = 1f;
        _mountBlend = 0f;
        _offset = Vector3.zero;
        _blendFrom = Vector3.zero;
        _turn = Quaternion.identity;
        _turnFrom = Quaternion.identity;
        _hasMean = false;
        _hasHeadMean = false;
        _hasChase = false;
        _handAction = false;
        _chaseCatching = false;
        _release = 0f;
        _smoothedHead = Vector3.zero;
        _freeLookFrom = Vector3.zero;
        _freeLookWeight = 0f;
        _freeLookReturning = false;
        _pitchUnderLook = false;
        _headLock = 0f;
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
