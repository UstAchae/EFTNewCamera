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
    private const float CenterSeconds = 3f;
    private const float LeanSettleSeconds = 0.3f;
    private const float LookSmoothSeconds = 0.05f;
    private const float HeadLockSeconds = 0.2f;

    internal static float ForwardDistance;
    internal static float GunMove;
    internal static float GunTurn = 1f;
    internal static float ArmMove = 1f;
    internal static float ArmTurn = 1f;
    internal static float HeadFollow;
    internal static bool Chase = true;
    internal static float FollowDelay;
    internal static float Breath = 1f;
    private const float BreathCenterSeconds = 6f;
    internal static float MouseSmoothSeconds = 0.05f;
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
    private static Vector3 _logHeadShift;
    private static bool _hasChase;
    private static Vector3 _chase;
    private static bool _hasHeadMean;
    private static bool _hasCenter;
    private static float _lastTilt;
    private static float _leanUntil;
    private static bool _hasCenterPosition;
    private static int _turnFrame = -1;
    private static Quaternion _headTurnSmooth = Quaternion.identity;
    private static float _headLock;
    private static bool _hasObserved;
    private static Quaternion _smoothLook = Quaternion.identity;
    private static Vector3 _neckPosition;
    private static Vector3 _headPositionNow;
    private static Quaternion _headTurnNow = Quaternion.identity;
    private static Vector3 _centerPosition;
    private static Quaternion _centerTurn = Quaternion.identity;
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
                if (bones.Weapon_Root_Third != null && bones.Weapon_Root_Anim != null)
                    Sway(animation, bones.Weapon_Root_Third, bones.Weapon_Root_Anim, bone.position, sightId, steadyPosition, steadyRotation, aimPoint, out target, out turn);
                else
                    target = steadyPosition - bone.position + steadyRotation * aimPoint;

                Quaternion delay = steadyRotation * (Quaternion.Inverse(_smoothLook) * _lagLook) * Quaternion.Inverse(steadyRotation);
                Vector3 placed = bone.position + target;
                target = steadyPosition + delay * (placed - steadyPosition) - bone.position;
                turn = delay * turn;
            }
        }

        if (!aiming)
        {
            _hasMean = false;
            _hasHeadMean = false;
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
        Vector3 pivot = sightPoint ?? (bones.Weapon_Root_Anim != null ? bones.Weapon_Root_Anim.position : Vector3.zero);
        _laserDisplacement = displacement;
        _laserPivot = pivot;
        _laserTurn = _turn;
        _hasLaserDisplacement = displacement.sqrMagnitude >= 0.00000001f || _turn != Quaternion.identity;
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
                Carry(arm, pivot, displacement, _turn);
        }

        Transform? weapon = bones.Weapon_Root_Anim;
        if (weapon == null)
            return;
        foreach (Transform arm in Upperarms)
        {
            if (arm != null && weapon.IsChildOf(arm))
                return;
        }

        Carry(weapon, pivot, displacement, _turn);
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

    private static void Sway(ProceduralWeaponAnimation animation, Transform third, Transform anim, Vector3 sight, int sightId, Vector3 cameraPosition, Quaternion cameraRotation, Vector3 aimPoint, out Vector3 displacement, out Quaternion turn)
    {
        Quaternion toCamera = Quaternion.Inverse(cameraRotation);
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

        Vector3 smoothSight = sight + cameraRotation * (gunMove - rawGunMove);
        Vector3 sway = ArmMove * (armMove - _armMoveMean) + GunMove * (gunMove - _gunMoveMean);
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

        if (Log != null && Time.time < _logUntil)
        {
            float speedNow = animation.AimingSpeed * animation.CameraSmoothBlender.Value;
            Vector3 lag = (pinned - _chase) * 100f;
            Log.LogInfo(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "开镜 t={0:F3} dt={1:F4} 手臂位置cm=({2:F2},{3:F2},{4:F2}) 枪自身位置cm=({5:F2},{6:F2},{7:F2}) 手臂转角={8:F2} 枪自身转角={9:F2} 追的差cm=({10:F2},{11:F2},{12:F2}) 追速={13:F2} 混合={14:F2} 头偏中心角={15:F2} 头偏中心cm=({16:F2},{17:F2},{18:F2}) 中心偏角=({19:F2},{20:F2},{21:F2}) 俯仰={22:F1}",
                1.5f - (_logUntil - Time.time), Time.deltaTime,
                armMove.x * 100f, armMove.y * 100f, armMove.z * 100f,
                rawGunMove.x * 100f, rawGunMove.y * 100f, rawGunMove.z * 100f,
                Quaternion.Angle(_armTurnMean, armTurn), Quaternion.Angle(_gunTurnMean, rawGunTurn),
                lag.x, lag.y, lag.z, speedNow, _blend,
                _logHeadTurn, _logHeadShift.x, _logHeadShift.y, _logHeadShift.z,
                Signed(_centerTurn.eulerAngles.x), Signed(_centerTurn.eulerAngles.y), Signed(_centerTurn.eulerAngles.z),
                Signed(_smoothLook.eulerAngles.x)));
        }

        float slow;
        if (Time.time - _meanStart < AimInSeconds)
            slow = follow;
        else if (Time.time < _leanUntil)
            slow = FollowDelay > 0f ? 1f - Mathf.Exp(-Time.deltaTime / FollowDelay) : 1f;
        else
            slow = 1f - Mathf.Exp(-Time.deltaTime / BreathCenterSeconds);
        _armTurnCenter = Quaternion.Slerp(_armTurnCenter, _armTurnMean, slow).normalized;
        _gunTurnCenter = Quaternion.Slerp(_gunTurnCenter, _gunTurnMean, slow).normalized;
        Quaternion armBase = _armTurnCenter * Scale(Quaternion.Inverse(_armTurnCenter) * _armTurnMean, Breath);
        Quaternion gunBase = _gunTurnCenter * Scale(Quaternion.Inverse(_gunTurnCenter) * _gunTurnMean, Breath);
        Quaternion keptArm = armBase * Scale(Quaternion.Inverse(_armTurnMean) * armTurn, ArmTurn);
        Quaternion keptGun = gunBase * Scale(Quaternion.Inverse(_gunTurnMean) * gunTurn, GunTurn);
        turn = cameraRotation * keptArm * keptGun * Quaternion.Inverse(armTurn * rawGunTurn) * toCamera;
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
        if (!_hasCenterPosition)
        {
            _centerPosition = _headPositionNow;
            _hasCenterPosition = true;
        }
        else if (_smoothedHead.sqrMagnitude <= 0.01f)
        {
            _centerPosition = Vector3.Lerp(_centerPosition, _headPositionNow, CenterFollow());
        }

        _hasObserved = true;
    }

    private static float CenterFollow()
    {
        if (Time.time < _leanUntil)
            return FollowDelay > 0f ? 1f - Mathf.Exp(-Time.deltaTime / FollowDelay) : 1f;
        return 1f - Mathf.Exp(-Time.deltaTime / CenterSeconds);
    }

    private static bool UpdateTurn(Player player, Quaternion rawCamera)
    {
        Vector2 mouse;
        float tilt;
        try
        {
            if (player.MovementContext == null)
                return false;
            mouse = player.MovementContext.Rotation;
            tilt = player.MovementContext.SmoothedTilt;
        }
        catch
        {
            return false;
        }

        _turnFrame = Time.frameCount;
        Quaternion look = Quaternion.Euler(mouse.y, mouse.x, 0f);
        if (!_hasCenter)
            _smoothLook = look;
        else
            _smoothLook = Quaternion.Slerp(_smoothLook, look, 1f - Mathf.Exp(-Time.deltaTime / LookSmoothSeconds)).normalized;

        if (!_hasCenter || FollowDelay <= 0f)
            _lagLook = _smoothLook;
        else
            _lagLook = Quaternion.Slerp(_lagLook, _smoothLook, 1f - Mathf.Exp(-Time.deltaTime / FollowDelay)).normalized;
        float behind = Quaternion.Angle(_smoothLook, _lagLook);
        if (behind > MaxDelayDegrees)
            _lagLook = Quaternion.Slerp(_smoothLook, _lagLook, MaxDelayDegrees / behind).normalized;

        if (Mathf.Abs(tilt - _lastTilt) > 0.0001f)
            _leanUntil = Time.time + LeanSettleSeconds;
        _lastTilt = tilt;

        Quaternion rawTurn = Quaternion.Inverse(_smoothLook) * rawCamera;
        if (!_hasCenter)
        {
            _centerTurn = rawTurn;
            _headTurnSmooth = rawTurn;
            _hasCenter = true;
        }
        else
        {
            _headTurnSmooth = Quaternion.Slerp(_headTurnSmooth, rawTurn, 1f - Mathf.Exp(-Time.deltaTime / HeadSmoothSeconds)).normalized;
            if (_smoothedHead.sqrMagnitude <= 0.01f)
                _centerTurn = Quaternion.Slerp(_centerTurn, rawTurn, CenterFollow()).normalized;
        }

        return true;
    }

    private static void RotateHeadPostfix(PlayerBones __instance)
    {
        if (!NewCameraSwitch.Enabled || _turnFrame == Time.frameCount)
            return;

        Player? player = Singleton<GameWorld>.Instance?.MainPlayer;
        Transform? head = __instance.Head?.Original;
        if (player == null || head == null || !ReferenceEquals(player.PlayerBones, __instance))
            return;

        if (!UpdateTurn(player, CameraDirection.Resolve(head.rotation)))
            return;

        bool aiming = player.HandsController != null && player.HandsController.IsAiming;
        float target = aiming && _smoothedHead.sqrMagnitude <= 0.01f ? 1f : 0f;
        _headLock = Mathf.MoveTowards(_headLock, target, Time.deltaTime / HeadLockSeconds);
        if (_headLock <= 0f)
            return;

        float weight = _headLock * _headLock * (3f - 2f * _headLock);
        Quaternion locked = CameraDirection.Unresolve(_smoothLook * _headTurnSmooth);
        head.rotation = Quaternion.Slerp(head.rotation, locked, weight);
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
                _heldHeadPosition = _centerPosition;
                _heldHeadTurn = _centerTurn;
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

        _logHeadTurn = Quaternion.Angle(_centerTurn, _headTurnNow);
        _logHeadShift = (_headPositionNow - _centerPosition) * 100f;
        Vector3 steadyPosition = Vector3.Lerp(_centerPosition, _headPositionNow, HeadFollow);
        Quaternion steadyTurn = _centerTurn * Scale(Quaternion.Inverse(_centerTurn) * _headTurnNow, HeadFollow);
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
