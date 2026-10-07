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

    internal static float ReturnSeconds = 0.7f;
    internal static float WalkHeadAmount = 1f;
    private static float _standWeight = 1f;
    private const float SwaySnapDegrees = 20f;
    private const float SwaySnapMeters = 0.15f;
    private const float ActionBlendSeconds = 0.15f;
    private const float WalkingSpeed = 0.5f;
    private const float HeadFollowSeconds = 0.05f;
    private const float HeadSnapDegrees = 45f;

    internal static BepInEx.Logging.ManualLogSource? Log;
    private static float _aimSince;
    private static float _nextAimLog;
    private static float _logSightTurn;
    private static float _logSightShift;
    private static float _logGunOnBody;
    private static float _logArmTurn;
    private static Quaternion _headMean = Quaternion.identity;
    private static bool _hasHeadInLook;
    private static Quaternion _headInLook = Quaternion.identity;
    private static float _actionWeight;

    private const float ConeTipDistance = 100f;
    private const float MouseSpeedReference = 3f;
    private const float MouseSpeedSeconds = 0.1f;
    internal static float ForwardDistance;
    internal static bool UseCone;
    internal static float SwayAmount = 0.5f;
    internal static float RecoilAmount = 1f;
    internal static float ActionAmount = 1f;
    private static float _aimWeight;
    private const float HandsActionInSeconds = 0.1f;
    private const float HandsActionOutSeconds = 0.3f;
    private static float _handsAction;
    private static bool _loggedHandsActing;
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
    private static Quaternion _sightMeanRotation = Quaternion.identity;
    private static Vector3 _sightMeanPosition;
    private static bool _loggedArmFrame;
    private const float MeanStartSeconds = 0.08f;
    private const float MeanSettleHoldSeconds = 2f;
    private const float MeanSettleSeconds = 1f;
    private const float PlacementBlendSeconds = 0.25f;
    private static float _sinceReset;
    private static bool _hasShownPose;
    private static Vector3 _shownPosition;
    private static Quaternion _shownRotation = Quaternion.identity;
    private static int _blendSight;
    private static float _blend = 1f;
    private static Vector3 _blendFromPosition;
    private static Quaternion _blendFromRotation = Quaternion.identity;
    private static Quaternion _armMeanRotation = Quaternion.identity;
    private static Vector3 _armMeanPosition;
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

        bool aiming = player.HandsController != null && player.HandsController.IsAiming;
        _aimWeight = Mathf.MoveTowards(_aimWeight, aiming ? 1f : 0f, Time.deltaTime / PlacementBlendSeconds);
        Vector3 velocity = player.Velocity;
        float standing = 1f - Mathf.Clamp01(new Vector2(velocity.x, velocity.z).magnitude / WalkingSpeed);
        _standWeight = Mathf.MoveTowards(_standWeight, standing, Time.deltaTime / ActionBlendSeconds);
        if (!aiming)
        {
            _aimSince = 0f;
            _headMean = _headInLook;
        }
        else
        {
            _aimSince += Time.deltaTime;
            float seconds = SettlingSeconds(_aimSince, ReturnSeconds);
            _headMean = Quaternion.Slerp(_headMean, _headInLook, 1f - Mathf.Exp(-Time.deltaTime / seconds));
        }

        if (_aimWeight > 0f)
            head.rotation = CameraDirection.Unresolve(look * HeadShown(_headInLook));
    }

    // The head's angle to the mouse look, scaled once aimed in (standing by the sway amount, walking
    // by its own amount): at zero the picture is the mouse look itself. Aiming in and out eases
    // between that and the game's head.
    private static Quaternion HeadShown(Quaternion headInLook)
    {
        float t = _aimWeight * _aimWeight * (3f - 2f * _aimWeight);
        float amount = Mathf.Lerp(WalkHeadAmount, SwayAmount, _standWeight);
        return Quaternion.Slerp(headInLook, Quaternion.Slerp(Quaternion.identity, headInLook, amount), t);
    }

    // The picture hangs on the head, which sits at an angle to the mouse look that the game eases
    // while aiming in. Standing, the gun aims along the look turned by the head's averaged angle, so
    // it lands in the middle of the picture while still sharing the mouse signal frame by frame.
    // Walking, it rides the head's bob with the picture, the way a cheek on the stock carries it.
    private static Quaternion AimLook(Quaternion look)
    {
        if (!_hasHeadInLook)
            return look;
        return look * Quaternion.Slerp(HeadShown(_headInLook), HeadShown(_headMean), _standWeight);
    }

    private static Quaternion AimCamera(Quaternion look)
    {
        return _hasHeadInLook ? look * HeadShown(_headInLook) : look;
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

        Transform? weapon = __instance.Weapon_Root_Anim;
        if (weapon == null)
            return;

        Vector3 gamePosition = weapon.position;
        int placedSight = 0;
        if (aiming && TryGetLook(player, out Quaternion look)
            && AlignToLook(player, cameraPosition, CameraPosition.Resolve(head.position, AimCamera(look)), liveRotation, AimLook(look)))
        {
            placedSight = _swaySightId;
            LogAimIn(look, liveRotation);
        }
        else
            ClearHold();

        BlendPlacement(weapon, placedSight, cameraPosition, liveRotation);
        Vector3 target = weapon.position - gamePosition;
        _offset = target;
        _laserDisplacement = target;
        _hasLaserDisplacement = target.sqrMagnitude >= 0.00000001f;
    }

    // Aiming in or out and switching sights move the gun from wherever it was last shown, in the
    // camera's frame, to the new pose instead of jumping there.
    private static void BlendPlacement(Transform weapon, int placedSight, Vector3 cameraPosition, Quaternion cameraRotation)
    {
        Quaternion toCamera = Quaternion.Inverse(cameraRotation);
        if (!_hasShownPose)
        {
            _blendSight = placedSight;
            _blend = 1f;
        }
        else if (placedSight != _blendSight)
        {
            _blendSight = placedSight;
            _blendFromPosition = _shownPosition;
            _blendFromRotation = _shownRotation;
            _blend = 0f;
        }

        if (_blend < 1f)
        {
            _blend = Mathf.Min(1f, _blend + Time.deltaTime / PlacementBlendSeconds);
            float t = _blend * _blend * (3f - 2f * _blend);
            Vector3 fromPosition = cameraPosition + cameraRotation * _blendFromPosition;
            Quaternion fromRotation = cameraRotation * _blendFromRotation;
            weapon.SetPositionAndRotation(
                Vector3.Lerp(fromPosition, weapon.position, t),
                Quaternion.Slerp(fromRotation, weapon.rotation, t));
        }

        _shownPosition = toCamera * (weapon.position - cameraPosition);
        _shownRotation = toCamera * weapon.rotation;
        _hasShownPose = true;
    }

    private static void LogAimIn(Quaternion look, Quaternion liveRotation)
    {
        if (Log == null || _aimSince > 3.5f || Time.time < _nextAimLog)
            return;

        _nextAimLog = Time.time + 0.1f;
        Quaternion aim = AimLook(look);
        Vector3 headOffset = (Quaternion.Inverse(look) * (look * _headInLook * Vector3.forward));
        Log.LogInfo(string.Format(
            "开镜 {0:F2}s 头相对鼠标 偏航{1:F1} 俯仰{2:F1} | 枪对画面中心 {3:F2}度 | 平均值对头 {4:F2}度 | 摆枪时头骨对画面 {5:F2}度",
            _aimSince,
            Mathf.Atan2(headOffset.x, headOffset.z) * Mathf.Rad2Deg,
            -Mathf.Asin(Mathf.Clamp(headOffset.y, -1f, 1f)) * Mathf.Rad2Deg,
            Vector3.Angle(aim * Vector3.forward, look * _headInLook * Vector3.forward),
            Quaternion.Angle(_headMean, _headInLook),
            Vector3.Angle(liveRotation * Vector3.forward, look * _headInLook * Vector3.forward))
            + string.Format(" | 瞄具偏离平均 {0:F2}度 {1:F1}厘米 | 手臂层偏离平均 {2:F2}度 | 枪相对身体骨头 {3:F2}度",
                _logSightTurn, _logSightShift, _logArmTurn, _logGunOnBody));
    }

    private static float MeanFollow()
    {
        return 1f - Mathf.Exp(-Time.deltaTime / SettlingSeconds(_sinceReset, ReturnSeconds));
    }

    // After aiming in, the body keeps settling into its aiming pose for about one and a half
    // seconds (head pitch, arm and sight drift by a degree or two and a couple of centimetres).
    // The averages track that closely so it is not shown as sway, then relax to their full time.
    private static float SettlingSeconds(float elapsed, float fullSeconds)
    {
        float settling = elapsed <= MeanSettleHoldSeconds
            ? MeanStartSeconds
            : Mathf.Lerp(MeanStartSeconds, fullSeconds, Mathf.Clamp01((elapsed - MeanSettleHoldSeconds) / MeanSettleSeconds));
        // Walking, a tight average would ride along with the steps it is meant to hold out.
        return Mathf.Lerp(fullSeconds, settling, _actionWeight);
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

    // The chain from the ribcage out to the gun (the arms' and weapon's own animation) is
    // kept whole. The ribcage's motion against the camera is the rest of the body; standing it is
    // kept, walking only its breathing-paced part. The result is laid onto the mouse look and the
    // sight keeps the displacement that chain gives it, so swings pivot where the game pivots them.
    private static bool AlignToLook(Player player, Vector3 cameraPosition, Vector3 shownCameraPosition, Quaternion liveRotation, Quaternion look)
    {
        PlayerBones? bones = player.PlayerBones;
        Transform? weapon = bones?.Weapon_Root_Anim;
        Transform? body = bones?.Weapon_Root_Third;
        ProceduralWeaponAnimation? animation = player.ProceduralWeaponAnimation;
        Transform? bone = animation != null && animation.ScopeAimTransforms != null && animation.ScopeAimTransforms.Count > 0
            ? animation.CurrentScope?.Bone
            : null;
        if (bones == null || weapon == null || body == null || bone == null || animation == null)
            return false;

        Transform frame = ArmFrame(bones, body);
        Quaternion frameRotation = frame.rotation;
        Vector3 recoil = RecoilShift(animation, body);
        Vector3 recoilInWeapon = Quaternion.Inverse(weapon.rotation) * recoil;
        Quaternion armRotation = Quaternion.Inverse(frameRotation) * weapon.rotation;
        Vector3 armPosition = Quaternion.Inverse(frameRotation) * (weapon.position - recoil - frame.position);
        Quaternion boneLocalRotation = Quaternion.Inverse(weapon.rotation) * bone.rotation;
        Vector3 boneLocalPosition = Quaternion.Inverse(weapon.rotation) * (bone.position - weapon.position);
        Quaternion frameInCamera = Quaternion.Inverse(liveRotation) * frameRotation;
        Vector3 frameInCameraPosition = Quaternion.Inverse(liveRotation) * (frame.position - cameraPosition);

        bool handsActing = HandsActing(player);
        _handsAction = Mathf.MoveTowards(_handsAction, handsActing ? 1f : 0f,
            Time.deltaTime / (handsActing ? HandsActionInSeconds : HandsActionOutSeconds));
        bool acting = handsActing;
        Vector3 velocity = player.Velocity;
        float walking = Mathf.Clamp01(new Vector2(velocity.x, velocity.z).magnitude / WalkingSpeed);
        float raw = acting ? 1f : 1f - walking;
        _actionWeight = Mathf.MoveTowards(_actionWeight, raw, Time.deltaTime / ActionBlendSeconds);
        Quaternion sway = FilterBodySway(bone.GetInstanceID(), frameInCamera, frameInCameraPosition, out Vector3 swayShift, out bool reset);

        Quaternion filteredFrame = _bodyBaseRotation * sway;
        Vector3 filteredFramePosition = _bodyBasePosition + swayShift;
        TrackArmMean(reset, armRotation, armPosition);
        Quaternion sightInCamera = filteredFrame * armRotation * boneLocalRotation;
        Vector3 sightInCameraPosition = filteredFramePosition + filteredFrame * (armPosition + armRotation * boneLocalPosition);
        if (reset)
        {
            _sightMeanRotation = sightInCamera;
            _sightMeanPosition = sightInCameraPosition;
        }
        else
        {
            // Held during a hand action, so the whole action is a departure on its own scale.
            float follow = MeanFollow() * (1f - _handsAction);
            _sightMeanRotation = Quaternion.Slerp(_sightMeanRotation, sightInCamera, follow);
            _sightMeanPosition = Vector3.Lerp(_sightMeanPosition, sightInCameraPosition, follow);
        }

        _logSightTurn = Quaternion.Angle(_sightMeanRotation, sightInCamera);
        _logSightShift = (sightInCameraPosition - _sightMeanPosition).magnitude * 100f;
        _logGunOnBody = Quaternion.Angle(Quaternion.identity, Quaternion.Inverse(body.rotation) * weapon.rotation);
        _logArmTurn = Quaternion.Angle(_armMeanRotation, armRotation);
        // Every swing is the sight's departure from its average: hand actions (bolt, fire mode,
        // reloads) on their own scale, everything else (body, arms) on the sway scale. Recoil was
        // taken out above and is put back on its own scale.
        float action = _handsAction * _handsAction * (3f - 2f * _handsAction);
        float amount = Mathf.Lerp(SwayAmount, ActionAmount, action);
        Quaternion shownSight = Quaternion.SlerpUnclamped(_sightMeanRotation, sightInCamera, amount);
        Vector3 shownShift = (sightInCameraPosition - _sightMeanPosition) * amount;
        Quaternion meanSight = look * _sightMeanRotation;
        Quaternion correction = AlignSightTo(meanSight, look * Vector3.forward) * Quaternion.Inverse(meanSight);
        Quaternion sightRotation = correction * look * shownSight;
        float relief = SightRelief(animation, bone);
        Vector3 sightPoint = shownCameraPosition + look * new Vector3(0f, 0f, ForwardDistance + relief)
            + correction * (look * shownShift);
        Quaternion targetWeaponRotation = sightRotation * Quaternion.Inverse(boneLocalRotation);
        Vector3 targetWeaponPosition = sightPoint - targetWeaponRotation * boneLocalPosition
            + targetWeaponRotation * (recoilInWeapon * RecoilAmount);
        Vector3 target = targetWeaponPosition - weapon.position;

        _offset = target;
        _laserDisplacement = target;
        _hasLaserDisplacement = target.sqrMagnitude >= 0.00000001f;
        weapon.SetPositionAndRotation(targetWeaponPosition, targetWeaponRotation);
        return true;
    }

    // The hands animator leaves its idle state for every hand action: bolt cycling after a shot,
    // fire mode switches, reloads, checks.
    private static bool HandsActing(Player player)
    {
        var hands = player.HandsController;
        if (hands == null)
            return false;

        bool acting = hands.IsInInteractionStrictCheck();
        FirearmsAnimator? animator = hands.FirearmsAnimator;
        if (!acting && animator != null)
            acting = !animator.IsIdling();
        if (acting != _loggedHandsActing)
        {
            _loggedHandsActing = acting;
            Log?.LogInfo(acting ? "手上动作开始" : "手上动作结束");
        }

        return acting;
    }

    // In third person the game's recoil only pushes the gun, in the frame of Weapon_root_3rd_anim
    // (PlayerBones.Offset); its rotation recoil is applied to the first-person rig only.
    private static Vector3 RecoilShift(ProceduralWeaponAnimation animation, Transform body)
    {
        var effect = animation.Shootingg?.CurrentRecoilEffect;
        if (effect == null)
            return Vector3.zero;

        Vector3 local = effect.GetHandPositionRecoil();
        if (effect.WeaponRecoilEffect != null)
            local += effect.WeaponRecoilEffect.GetPositionRecoil();
        return body.rotation * local;
    }

    private static void TrackArmMean(bool reset, Quaternion armRotation, Vector3 armPosition)
    {
        if (reset)
        {
            _armMeanRotation = armRotation;
            _armMeanPosition = armPosition;
        }
        else
        {
            float follow = MeanFollow();
            _armMeanRotation = Quaternion.Slerp(_armMeanRotation, armRotation, follow);
            _armMeanPosition = Vector3.Lerp(_armMeanPosition, armPosition, follow);
        }
    }

    private static Transform ArmFrame(PlayerBones bones, Transform body)
    {
        Transform? ribcage = bones.Ribcage?.Original;
        Transform frame = ribcage != null && body.IsChildOf(ribcage) && body != ribcage
            ? ribcage
            : body.parent != null ? body.parent : body;
        if (!_loggedArmFrame)
        {
            _loggedArmFrame = true;
            string chain = body.name;
            for (Transform? t = body.parent; t != null && chain.Length < 400; t = t.parent)
                chain += " <- " + t.name;
            Log?.LogInfo("手臂层起点: " + frame.name + "；枪骨链: " + chain);
        }

        return frame;
    }

    private static Quaternion FilterBodySway(int sightId, Quaternion bodyRotation, Vector3 bodyPosition, out Vector3 shift, out bool reset)
    {
        shift = Vector3.zero;
        reset = false;
        Quaternion turn = Quaternion.Inverse(_bodyBaseRotation) * bodyRotation;
        Vector3 offset = bodyPosition - _bodyBasePosition;
        if (!_hasSway || sightId != _swaySightId)
        {
            reset = true;
            _bodyBaseRotation = bodyRotation;
            _bodyBasePosition = bodyPosition;
            _swaySightId = sightId;
            _hasSway = true;
            _sinceReset = 0f;
            return Quaternion.identity;
        }

        float dt = Time.deltaTime;
        _sinceReset += dt;
        float follow = MeanFollow();
        _bodyBaseRotation = Quaternion.Slerp(_bodyBaseRotation, bodyRotation, follow);
        _bodyBasePosition = Vector3.Lerp(_bodyBasePosition, bodyPosition, follow);
        turn = Quaternion.Inverse(_bodyBaseRotation) * bodyRotation;
        offset = bodyPosition - _bodyBasePosition;

        shift = offset;
        return turn;
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
        float ratio = _mouseSpeed / MouseSpeedReference;
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
