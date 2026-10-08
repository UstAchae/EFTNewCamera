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
    internal static float MouseSmoothSeconds = 0.05f;
    internal static float WalkHeadSway;
    private static Vector2 _pendingMouse;
    private static bool _releasingMouse;
    private const float MaxDelayDegrees = 15f;

    private static bool _suppress;
    private static bool _blendAiming;
    private static int _blendSightId;
    private static float _blend = 1f;
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
    private static Quaternion _lagLook = Quaternion.identity;
    private static Vector3 _gunMoveSmooth;
    private static Quaternion _gunTurnSmooth = Quaternion.identity;
    internal static BepInEx.Logging.ManualLogSource? Log;
    private static float _logUntil;
    private static float _logHeadTurn;
    private static bool _hasChase;
    private static Vector3 _chase;
    private static bool _hasHeadMean;
    private static bool _hasCenter;
    private const float StateBlendSeconds = 0.3f;
    private static float _holdWeight;
    private static float _tiredWeight;
    private static bool _logHolding;
    private static bool _logTired;
    private static Physical? _drainOwner;
    private static bool _aimDrain;
    private static int _turnFrame = -1;
    private static int _rotateHeadFrame = -1;
    private static float _leanTilt;
    private static float _leanLogUntil;
    private static Quaternion _headTurnRaw = Quaternion.identity;
    private static bool _hasYawCenter;
    private static float _yawCenter;
    private static Vector3 _leanArm;
    private static float _leanArmTurn;
    private static Vector3 _leanGunMove;
    private static float _leanGunTurn;
    private static float _leanGunLag;
    private static Quaternion _headTurnSmooth = Quaternion.identity;
    private static float _headLock;
    private static bool _hasObserved;
    private static Quaternion _smoothLook = Quaternion.identity;
    // The body is posed with the previous frame's mouse; MovementContext.Rotation is already the new one.
    private static Quaternion _bodyLook = Quaternion.identity;
    private static Vector3 _neckPosition;
    private static Vector3 _headPositionNow;
    private static Quaternion _headTurnNow = Quaternion.identity;
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
    private static int _shotFrame = -1;
    private static Vector3? _shotFireport;
    private const float SteadyLogFrom = 1.5f;
    private const float SteadyLogTo = 6f;
    private static bool _hasSteadyRef;
    private static Vector3 _steadySight;
    private static Quaternion _steadyGun = Quaternion.identity;
    private static Vector2 _mouseNow;
    private static Quaternion _armRotationNow = Quaternion.identity;
    private static Vector3 _chaseLagNow;
    private static bool _hasTurnPrev;
    private static int _turnLogCount;
    private static Vector2 _turnPrevMouse;
    private static Quaternion _turnPrevGun = Quaternion.identity;
    private static Quaternion _turnPrevArmCamera = Quaternion.identity;
    private static Quaternion _turnPrevArmHead = Quaternion.identity;
    private static Vector3 _turnPrevSight;
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
        MethodInfo? rotateHead = AccessTools.Method(typeof(PlayerBones), nameof(PlayerBones.RotateHead));
        MethodInfo? rotateHeadPrefix = typeof(AimArms).GetMethod(nameof(RotateHeadPrefix), BindingFlags.NonPublic | BindingFlags.Static);
        MethodInfo? rotateHeadPostfix = typeof(AimArms).GetMethod(nameof(RotateHeadPostfix), BindingFlags.NonPublic | BindingFlags.Static);
        if (rotateHead != null && rotateHeadPrefix != null && rotateHeadPostfix != null)
            harmony.Patch(rotateHead, prefix: new HarmonyMethod(rotateHeadPrefix), postfix: new HarmonyMethod(rotateHeadPostfix) { priority = Priority.Last });
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
        MethodInfo? rotate = AccessTools.Method(typeof(Player), nameof(Player.Rotate));
        MethodInfo? rotatePrefix = typeof(AimArms).GetMethod(nameof(RotatePrefix), BindingFlags.NonPublic | BindingFlags.Static);
        if (rotate != null && rotatePrefix != null)
            harmony.Patch(rotate, prefix: new HarmonyMethod(rotatePrefix));
        PatchLaser(harmony);
        PatchFieldOfView(harmony);
        _patched = true;
    }

    private static bool RotatePrefix(Player __instance, Vector2 deltaRotation, bool ignoreClamp)
    {
        if (_releasingMouse || ignoreClamp || !NewCameraSwitch.Enabled || MouseSmoothSeconds <= 0f)
            return true;
        if (!ReferenceEquals(__instance, Singleton<GameWorld>.Instance?.MainPlayer))
            return true;
        if (__instance.HandsController == null || !__instance.HandsController.IsAiming)
            return true;
        if (deltaRotation.IsAnyComponentInfinity() || deltaRotation.IsAnyComponentNaN())
            return true;

        _pendingMouse += deltaRotation;
        return false;
    }

    internal static void ReleaseMouse()
    {
        if (_pendingMouse == Vector2.zero)
            return;

        Player? player = Singleton<GameWorld>.Instance?.MainPlayer;
        if (player == null)
        {
            _pendingMouse = Vector2.zero;
            return;
        }

        bool smoothing = NewCameraSwitch.Enabled && MouseSmoothSeconds > 0f
            && player.HandsController != null && player.HandsController.IsAiming;
        Vector2 step = smoothing
            ? _pendingMouse * (1f - Mathf.Exp(-Time.deltaTime / MouseSmoothSeconds))
            : _pendingMouse;
        if ((_pendingMouse - step).sqrMagnitude < 0.00000001f)
            step = _pendingMouse;
        _pendingMouse -= step;
        _releasingMouse = true;
        try
        {
            player.Rotate(step);
        }
        finally
        {
            _releasingMouse = false;
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

    internal static void AlignToCamera(Player player, Vector3 cameraPosition, Quaternion cameraRotation)
    {
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
            if (bone != null)
                sightPoint = bone.position;
            if (aiming && bone != null && animation != null)
            {
                sightId = bone.GetInstanceID();
                float relief = SightRelief(animation, bone);
                Vector3 aimPoint = new Vector3(0f, 0f, ForwardDistance + relief);
                SteadyHead(player, cameraPosition, cameraRotation, out Vector3 steadyPosition, out Quaternion steadyRotation);
                Quaternion bodyRotation = _bodyLook * Quaternion.Inverse(_smoothLook) * steadyRotation;
                Vector3 bodyPosition = CameraPosition.Resolve(steadyPosition - CameraPosition.Resolve(Vector3.zero, steadyRotation), bodyRotation);
                if (bones.Weapon_Root_Third != null && bones.Weapon_Root_Anim != null)
                    Sway(animation, bones.Weapon_Root_Third, bones.Weapon_Root_Anim, bone, sightId, bodyPosition, bodyRotation, aimPoint, out target, out turn);
                else
                    target = bodyPosition - bone.position + bodyRotation * aimPoint;

                Quaternion delay = _lagLook * Quaternion.Inverse(_bodyLook);
                Vector3 placed = bone.position + target;
                target = steadyPosition + delay * (placed - bodyPosition) - bone.position;
                turn = delay * turn;
            }
        }

        if (!aiming)
        {
            _hasMean = false;
            _hasHeadMean = false;
            _hasTurnPrev = false;
            _turnLogCount = 0;
        }

        if (!aiming && !_blendAiming && _offset.sqrMagnitude < 0.00000001f && _turn == Quaternion.identity && _blend >= 1f)
        {
            _hasLaserDisplacement = false;
            return;
        }

        if (bones == null)
        {
            _hasLaserDisplacement = false;
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
        Recoil(player.ProceduralWeaponAnimation, bones.Weapon_Root_Anim, pivot, ref displacement, ref carryTurn);
        _laserDisplacement = displacement;
        _laserPivot = pivot;
        _laserTurn = carryTurn;
        _hasLaserDisplacement = displacement.sqrMagnitude >= 0.00000001f || carryTurn != Quaternion.identity;
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
        if (aiming)
        {
            LogSteady(weapon, sightPoint.HasValue ? pivot + displacement : (Vector3?)null, cameraPosition, cameraRotation);
            LogTurn(weapon, sightPoint.HasValue ? pivot + displacement : (Vector3?)null, cameraPosition, cameraRotation);
            LogLean(player, weapon, sightPoint.HasValue ? pivot + displacement : (Vector3?)null, cameraPosition, cameraRotation);
        }
    }

    private static void LogLean(Player player, Transform weapon, Vector3? sight, Vector3 cameraPosition, Quaternion cameraRotation)
    {
        if (Log == null || player.MovementContext == null)
            return;

        float tilt = player.MovementContext.SmoothedTilt;
        if (Mathf.Abs(tilt - _leanTilt) > 0.0001f)
            _leanLogUntil = Time.time + 0.5f;
        _leanTilt = tilt;
        if (Time.time >= _leanLogUntil)
            return;

        Quaternion toCamera = Quaternion.Inverse(cameraRotation);
        Vector3 sightLocal = sight.HasValue ? toCamera * (sight.Value - cameraPosition) * 100f : Vector3.zero;
        Vector3 gun = (toCamera * weapon.rotation).eulerAngles;
        Vector3 head = _headTurnRaw.eulerAngles;
        Vector3 smooth = _headTurnSmooth.eulerAngles;
        Log.LogInfo(string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "探头 帧={0} dt={1:F4} 探头值={2:F3} 头钩子={3} 头锁={4:F2} 头原始=({5:F2},{6:F2},{7:F2}) 头抹匀=({8:F2},{9:F2},{10:F2}) 镜头离目标={11:F2} " +
            "瞄具cm=({12:F2},{13:F2},{14:F2}) 枪相对镜头=({15:F2},{16:F2},{17:F2}) 手臂cm=({18:F2},{19:F2},{20:F2}) 手臂转偏={21:F2} 枪自身cm=({22:F2},{23:F2},{24:F2}) 枪自身转偏={25:F2} 枪抹匀差={26:F2}",
            Time.frameCount, Time.deltaTime, tilt, _rotateHeadFrame == Time.frameCount, _headLock,
            Signed(head.x), Signed(head.y), Signed(head.z), Signed(smooth.x), Signed(smooth.y), Signed(smooth.z), _logHeadTurn,
            sightLocal.x, sightLocal.y, sightLocal.z, Signed(gun.x), Signed(gun.y), Signed(gun.z),
            _leanArm.x, _leanArm.y, _leanArm.z, _leanArmTurn, _leanGunMove.x, _leanGunMove.y, _leanGunMove.z, _leanGunTurn, _leanGunLag));
    }

    private static void AdjustShotPrefix(Player.FirearmController __instance, ref Vector3 position, ref Vector3 direction)
    {
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

    private static void LogSteady(Transform weapon, Vector3? sight, Vector3 cameraPosition, Quaternion cameraRotation)
    {
        if (Log == null || sight == null)
            return;
        float t = Time.time - _meanStart;
        if (t < SteadyLogFrom || t > SteadyLogTo)
            return;

        Quaternion toCamera = Quaternion.Inverse(cameraRotation);
        Vector3 sightLocal = toCamera * (sight.Value - cameraPosition);
        Quaternion gunLocal = toCamera * weapon.rotation;
        if (!_hasSteadyRef)
        {
            _steadySight = sightLocal;
            _steadyGun = gunLocal;
            _hasSteadyRef = true;
        }

        Vector3 shift = (sightLocal - _steadySight) * 100f;
        Quaternion swing = Quaternion.Inverse(_steadyGun) * gunLocal;
        Quaternion headSway = _headTurnSmooth * Quaternion.Inverse(Lean(_headTurnSmooth));
        Quaternion breath = Quaternion.Inverse(_armTurnCenter) * _armTurnMean;
        Log.LogInfo(string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "静止 t={0:F3} 瞄具偏cm=({1:F2},{2:F2},{3:F2}) 枪转=({4:F2},{5:F2},{6:F2}) 头的摆动=({7:F2},{8:F2},{9:F2}) 手臂慢晃=({10:F2},{11:F2},{12:F2})",
            t, shift.x, shift.y, shift.z,
            Signed(swing.eulerAngles.x), Signed(swing.eulerAngles.y), Signed(swing.eulerAngles.z),
            Signed(headSway.eulerAngles.x), Signed(headSway.eulerAngles.y), Signed(headSway.eulerAngles.z),
            Signed(breath.eulerAngles.x), Signed(breath.eulerAngles.y), Signed(breath.eulerAngles.z)));
    }

    private static void LogTurn(Transform weapon, Vector3? sight, Vector3 cameraPosition, Quaternion cameraRotation)
    {
        if (Log == null || sight == null)
            return;

        Quaternion toCamera = Quaternion.Inverse(cameraRotation);
        Vector3 sightLocal = toCamera * (sight.Value - cameraPosition) * 100f;
        Quaternion gun = toCamera * weapon.rotation;
        Quaternion armCamera = toCamera * _armRotationNow;
        Quaternion armHead = Quaternion.Inverse(_bodyLook * _headTurnRaw) * _armRotationNow;
        if (_hasTurnPrev && _turnLogCount < 1500)
        {
            float yaw = Mathf.DeltaAngle(_turnPrevMouse.x, _mouseNow.x);
            float pitch = Mathf.DeltaAngle(_turnPrevMouse.y, _mouseNow.y);
            if (Mathf.Abs(yaw) > 0.001f || Mathf.Abs(pitch) > 0.001f)
            {
                _turnLogCount++;
                Vector3 sightStep = sightLocal - _turnPrevSight;
                Vector3 headRaw = _headTurnRaw.eulerAngles;
                Log.LogInfo(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "转动 帧={0} dt={1:F4} 鼠标=({2:F3},{3:F3}) 头原始偏=({4:F2},{5:F2}) 枪相对镜头变={6:F3} 手臂相对镜头变={7:F3} 手臂相对原头变={8:F3} 瞄具步cm=({9:F3},{10:F3},{11:F3}) 追差cm=({12:F2},{13:F2},{14:F2})",
                    Time.frameCount, Time.deltaTime, yaw, pitch, Signed(headRaw.x), Signed(headRaw.y),
                    Quaternion.Angle(_turnPrevGun, gun), Quaternion.Angle(_turnPrevArmCamera, armCamera), Quaternion.Angle(_turnPrevArmHead, armHead),
                    sightStep.x, sightStep.y, sightStep.z, _chaseLagNow.x, _chaseLagNow.y, _chaseLagNow.z));
            }
        }

        _turnPrevMouse = _mouseNow;
        _turnPrevGun = gun;
        _turnPrevArmCamera = armCamera;
        _turnPrevArmHead = armHead;
        _turnPrevSight = sightLocal;
        _hasTurnPrev = true;
    }

    internal static void CarryOptic(Camera camera)
    {
        if (!_hasLaserDisplacement || _opticFrame == Time.frameCount)
            return;

        _opticFrame = Time.frameCount;
        Carry(camera.transform, _laserPivot, _laserDisplacement, _laserTurn);
    }

    private static void Carry(Transform transform, Vector3 pivot, Vector3 displacement, Quaternion turn)
    {
        transform.SetPositionAndRotation(pivot + displacement + turn * (transform.position - pivot), turn * transform.rotation);
    }

    private static void Recoil(ProceduralWeaponAnimation? animation, Transform? weapon, Vector3 pivot, ref Vector3 displacement, ref Quaternion turn)
    {
        if (animation == null || weapon == null || animation.Shootingg?.CurrentRecoilEffect == null || animation.HandsContainer == null || RecoilKick <= 0f)
            return;

        Vector3 recoil = animation.Shootingg.CurrentRecoilEffect.GetHandRotationRecoil();
        if (animation.Shootingg.CurrentRecoilEffect.WeaponRecoilEffect != null)
            recoil += animation.Shootingg.CurrentRecoilEffect.WeaponRecoilEffect.GetRotationRecoil();
        if (recoil.sqrMagnitude < 0.00000001f)
            return;

        Quaternion weaponRotation = turn * weapon.rotation;
        Vector3 weaponPosition = pivot + displacement + turn * (weapon.position - pivot);
        Vector3 recoilPivot = weaponPosition + weaponRotation * animation.HandsContainer.RecoilPivot;
        Quaternion kick = Quaternion.Euler(weaponRotation * (recoil * RecoilKick));
        displacement = recoilPivot + kick * (pivot + displacement - recoilPivot) - pivot;
        turn = kick * turn;
    }

    private static void Sway(ProceduralWeaponAnimation animation, Transform third, Transform anim, Transform sightBone, int sightId, Vector3 cameraPosition, Quaternion cameraRotation, Vector3 aimPoint, out Vector3 displacement, out Quaternion turn)
    {
        Quaternion toCamera = Quaternion.Inverse(cameraRotation);
        Vector3 sight = sightBone.position;
        Vector3 sightOnThird = third.position + third.rotation * (Quaternion.Inverse(anim.rotation) * (sight - anim.position));
        Vector3 armMove = toCamera * (sightOnThird - cameraPosition);
        Vector3 rawGunMove = toCamera * (sight - sightOnThird);
        Quaternion armTurn = toCamera * third.rotation;
        Quaternion rawGunTurn = Quaternion.Inverse(third.rotation) * anim.rotation;

        if (!_hasMean || sightId != _meanSightId)
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
            _hasSteadyRef = false;
            _meanSightId = sightId;
            _meanStart = Time.time;
            _hasMean = true;
            _hasChase = false;
            _logUntil = Time.time + 1.5f;
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

        _leanArm = toCamera * (sightOnThird - cameraPosition) * 100f;
        _leanArmTurn = Quaternion.Angle(_armTurnMean, armTurn);
        _leanGunMove = rawGunMove * 100f;
        _leanGunTurn = Quaternion.Angle(_gunTurnMean, gunTurn);
        _leanGunLag = Quaternion.Angle(gunTurn, rawGunTurn);
        Vector3 smoothSight = sight + cameraRotation * (gunMove - rawGunMove);
        Vector3 sway = Now.ArmMove * (armMove - _armMoveMean) + Now.GunMove * (gunMove - _gunMoveMean);
        Vector3 pinned = toCamera * (cameraPosition + cameraRotation * (aimPoint + sway) - smoothSight);
        if (Chase && _hasChase)
        {
            float speed = animation.AimingSpeed * animation.CameraSmoothBlender.Value;
            if (speed <= 0f)
                speed = 16f;
            float side = 1f - Mathf.Exp(-speed * Time.deltaTime);
            float depth = 1f - Mathf.Exp(-animation.CameraSmoothTime * Time.deltaTime);
            _chase.x += (pinned.x - _chase.x) * side;
            _chase.y += (pinned.y - _chase.y) * side;
            _chase.z += (pinned.z - _chase.z) * depth;
        }
        else
        {
            _chase = pinned;
            _hasChase = true;
        }
        displacement = smoothSight - sight + cameraRotation * _chase;
        _armRotationNow = third.rotation;
        _chaseLagNow = (pinned - _chase) * 100f;

        if (Log != null && Time.time < _logUntil)
        {
            float speedNow = animation.AimingSpeed * animation.CameraSmoothBlender.Value;
            Vector3 lag = (pinned - _chase) * 100f;
            Log.LogInfo(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "开镜 t={0:F3} dt={1:F4} 手臂位置cm=({2:F2},{3:F2},{4:F2}) 枪自身位置cm=({5:F2},{6:F2},{7:F2}) 手臂转角={8:F2} 枪自身转角={9:F2} 追的差cm=({10:F2},{11:F2},{12:F2}) 追速={13:F2} 混合={14:F2} 镜头离目标角={15:F2} 头相对鼠标=({16:F2},{17:F2},{18:F2}) 俯仰={19:F1}",
                1.5f - (_logUntil - Time.time), Time.deltaTime,
                armMove.x * 100f, armMove.y * 100f, armMove.z * 100f,
                rawGunMove.x * 100f, rawGunMove.y * 100f, rawGunMove.z * 100f,
                Quaternion.Angle(_armTurnMean, armTurn), Quaternion.Angle(_gunTurnMean, rawGunTurn),
                lag.x, lag.y, lag.z, speedNow, _blend,
                _logHeadTurn,
                Signed(_headTurnSmooth.eulerAngles.x), Signed(_headTurnSmooth.eulerAngles.y), Signed(_headTurnSmooth.eulerAngles.z),
                Signed(_smoothLook.eulerAngles.x)));
        }

        float slow = 1f - Mathf.Exp(-Time.deltaTime / BreathCenterSeconds);
        _armTurnCenter = Quaternion.Slerp(_armTurnCenter, _armTurnMean, slow).normalized;
        _gunTurnCenter = Quaternion.Slerp(_gunTurnCenter, _gunTurnMean, slow).normalized;
        Quaternion armBase = _armTurnCenter * Scale(Quaternion.Inverse(_armTurnCenter) * _armTurnMean, Now.Breath);
        Quaternion gunBase = _gunTurnCenter * Scale(Quaternion.Inverse(_gunTurnCenter) * _gunTurnMean, Now.Breath);
        Quaternion keptArm = armBase * Scale(Quaternion.Inverse(_armTurnMean) * armTurn, Now.ArmTurn);
        Quaternion keptGun = gunBase * Scale(Quaternion.Inverse(_gunTurnMean) * gunTurn, Now.GunTurn);
        turn = cameraRotation * LineOfSight(animation, sightBone, anim, armBase * gunBase) * keptArm * keptGun * Quaternion.Inverse(armTurn * rawGunTurn) * toCamera;
    }

    private static Quaternion LineOfSight(ProceduralWeaponAnimation animation, Transform sightBone, Transform anim, Quaternion weaponInCamera)
    {
        Vector3 axis = sightBone.name == "aim_camera" ? -sightBone.up : -sightBone.forward;
        Vector3 sightInCamera = weaponInCamera * (Quaternion.Inverse(anim.rotation) * axis);
        float zeroing = 0f;
        try
        {
            zeroing = LineOfSightAngle(animation);
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

        ApplyHeadLock(__instance);
        SprintHeadPitch(__instance);
        CalmWalkHead(__instance);
    }

    private const float SprintPitchSeconds = 0.2f;
    private static float _sprintPitch;

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
        if (_sprintPitch <= 0f || player.HeadRotation.sqrMagnitude > 0.01f || _smoothedHead.sqrMagnitude > 0.01f)
            return;

        Vector3 forward = CameraDirection.Resolve(head.rotation) * Vector3.forward;
        float headPitch = -Mathf.Asin(Mathf.Clamp(forward.y, -1f, 1f)) * Mathf.Rad2Deg;
        float weight = _sprintPitch * _sprintPitch * (3f - 2f * _sprintPitch);
        float missing = Mathf.DeltaAngle(headPitch, player.MovementContext.Pitch) * weight;
        if (Mathf.Abs(missing) < 0.01f)
            return;

        Vector3 axis = Quaternion.Euler(0f, player.MovementContext.Rotation.x, 0f) * Vector3.right;
        Transform? neck = player.PlayerBones?.Neck;
        float neckTurn = neck != null ? missing * 0.5f : 0f;
        if (neck != null)
            neck.rotation = Quaternion.AngleAxis(neckTurn, axis) * neck.rotation;
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
        if (head == null || player.MovementContext == null || player.HeadRotation.sqrMagnitude > 0.01f || _smoothedHead.sqrMagnitude > 0.01f)
            return;

        Vector3 look = Vector3.ProjectOnPlane(CameraDirection.Resolve(head.rotation) * Vector3.forward, Vector3.up);
        if (look.sqrMagnitude < 0.0001f)
            return;

        float yaw = Vector3.SignedAngle(Quaternion.Euler(0f, player.MovementContext.Rotation.x, 0f) * Vector3.forward, look, Vector3.up);
        if (!_hasYawCenter)
        {
            _yawCenter = yaw;
            _hasYawCenter = true;
        }
        else
            _yawCenter = Mathf.Lerp(_yawCenter, yaw, 1f - Mathf.Exp(-Time.deltaTime / 0.5f));

        float moving = Mathf.Clamp01(player.MovementContext.ActualLinearVelocity / 0.6f);
        float keep = Mathf.Lerp(1f, WalkHeadSway, moving);
        float fix = _yawCenter + (yaw - _yawCenter) * keep - yaw;
        if (Mathf.Abs(fix) > 0.01f)
            head.rotation = Quaternion.AngleAxis(fix, Vector3.up) * head.rotation;
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
        _headTurnNow = Quaternion.Inverse(_smoothLook) * cameraRotation;
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

        if (Log != null && (holding != _logHolding || tired != _logTired))
            Log.LogInfo($"屏息={holding} 手臂没体力={tired}");
        _logHolding = holding;
        _logTired = tired;

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
        _bodyLook = continuous ? _smoothLook : look;
        _turnFrame = Time.frameCount;
        _mouseNow = mouse;
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
        return Scale(sway, Now.HeadFollow) * Lean(_headTurnRaw);
    }

    private static void RotateHeadPostfix(PlayerBones __instance)
    {
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
        _logHeadTurn = Quaternion.Angle(steadyTurn, _headTurnNow);
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
    }

    private static float Signed(float degrees)
    {
        return degrees > 180f ? degrees - 360f : degrees;
    }

    private static Quaternion Scale(Quaternion rotation, float amount)
    {
        if (rotation.w < 0f)
            rotation = new Quaternion(-rotation.x, -rotation.y, -rotation.z, -rotation.w);
        return Quaternion.SlerpUnclamped(Quaternion.identity, rotation, amount);
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
        if (!aiming && _offset.sqrMagnitude < 0.00000001f && _turn == Quaternion.identity)
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
        _turn = Quaternion.identity;
        _turnFrom = Quaternion.identity;
        _hasMean = false;
        _hasHeadMean = false;
        _hasChase = false;
        _release = 0f;
        _smoothedHead = Vector3.zero;
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
