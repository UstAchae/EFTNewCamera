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

    private const float BaseSeconds = 2f;
    private const float ActionBlendSeconds = 0.15f;
    private const float WalkingSpeed = 0.5f;
    private const float HeadFollowSeconds = 0.05f;
    private const float HeadSnapDegrees = 45f;

    private const float HeadMeanSeconds = 1f;
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

    internal static float ForwardDistance;
    internal static float PelvisAmount = 1f;
    internal static float Spine1Amount = 1f;
    internal static float Spine2Amount = 1f;
    internal static float ChestAmount = 1f;
    internal static float GunAmount = 1f;
    internal static float RecoilAmount = 1f;
    internal static float ActionAmount = 1f;
    internal static float ViewAmount = 1f;
    private static float _aimWeight;
    private const float HandsActionInSeconds = 0.1f;
    private const float HandsActionOutSeconds = 0.3f;
    private static float _handsAction;
    private static Quaternion _viewMean = Quaternion.identity;

    private struct BodyPart
    {
        public Transform Bone;
        public int Kind;
        public Quaternion MeanRotation;
        public Vector3 MeanPosition;
    }

    private static BodyPart[] _bodyParts = System.Array.Empty<BodyPart>();
    private static int _bodyPartsOwner;
    private static bool _hasSway;
    private static int _swaySightId;
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
        if (player == null || !player.IsYourPlayer || head == null || player.PointOfView != EPointOfView.ThirdPerson
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
            _viewMean = headInLook;
            _hasHeadInLook = true;
        }
        else
        {
            _headInLook = Quaternion.Slerp(_headInLook, headInLook, 1f - Mathf.Exp(-Time.deltaTime / HeadFollowSeconds));
        }

        _viewMean = Quaternion.Slerp(_viewMean, _headInLook, 1f - Mathf.Exp(-Time.deltaTime / HeadMeanSeconds));
        bool aiming = player.HandsController != null && player.HandsController.IsAiming;
        // At full view sway the picture is the head, the same as before these sliders. Only a lower
        // setting eases the picture toward the head's average, and aiming in or out eases that change.
        if (ViewAmount >= 0.999f)
            _aimWeight = aiming ? 1f : 0f;
        else
            _aimWeight = Mathf.MoveTowards(_aimWeight, aiming ? 1f : 0f, Time.deltaTime / PlacementBlendSeconds);
        if (!aiming)
        {
            _aimSince = 0f;
            _headMean = _headInLook;
            if (_aimWeight <= 0f)
                return;
        }
        else
        {
            _aimSince += Time.deltaTime;
            float seconds = SettlingSeconds(_aimSince, HeadMeanSeconds);
            _headMean = Quaternion.Slerp(_headMean, _headInLook, 1f - Mathf.Exp(-Time.deltaTime / seconds));
        }

        float aimBlend = _aimWeight * _aimWeight * (3f - 2f * _aimWeight);
        Quaternion calmed = Quaternion.Slerp(_viewMean, _headInLook, ViewAmount);
        Quaternion shownHead = Quaternion.Slerp(_headInLook, calmed, aimBlend);
        head.rotation = CameraDirection.Unresolve(look * shownHead);
    }

    // The picture hangs on the head, which sits at an angle to the mouse look that the game eases
    // while aiming in. The gun aims along the look turned by the head's averaged angle, so it lands
    // in the middle of the picture while still sharing the mouse signal frame by frame.
    private static Quaternion AimLook(Quaternion look)
    {
        return _hasHeadInLook ? look * _headMean : look;
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
        Transform? weapon = __instance.Weapon_Root_Anim;
        if (weapon == null)
            return;

        Vector3 gamePosition = weapon.position;
        int placedSight = 0;
        if (aiming && TryGetLook(player, out Quaternion look) && AlignToLook(player, cameraPosition, liveRotation, AimLook(look)))
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
        return 1f - Mathf.Exp(-Time.deltaTime / SettlingSeconds(_sinceReset, BaseSeconds));
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

    // The chest's motion against the camera, and the gun's own motion on the chest, are each a
    // departure from their average. Each departure is scaled on its own; at full scale the pose is
    // the one the game animated. Recoil is the game's push and is added back on its own scale.
    private static bool AlignToLook(Player player, Vector3 cameraPosition, Quaternion liveRotation, Quaternion look)
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
        Quaternion armRotation = Quaternion.Inverse(frameRotation) * weapon.rotation;
        Vector3 recoil = RecoilShift(animation, body);
        Vector3 armPosition = Quaternion.Inverse(frameRotation) * (weapon.position - recoil - frame.position);
        Quaternion boneLocalRotation = Quaternion.Inverse(weapon.rotation) * bone.rotation;
        Vector3 boneLocalPosition = Quaternion.Inverse(weapon.rotation) * (bone.position - weapon.position);
        Quaternion frameInCamera = Quaternion.Inverse(liveRotation) * frameRotation;
        Vector3 frameInCameraPosition = Quaternion.Inverse(liveRotation) * (frame.position - cameraPosition);

        bool acting = player.HandsController != null && player.HandsController.IsInInteractionStrictCheck();
        Vector3 velocity = player.Velocity;
        float walking = Mathf.Clamp01(new Vector2(velocity.x, velocity.z).magnitude / WalkingSpeed);
        float raw = acting ? 1f : 1f - walking;
        _actionWeight = Mathf.MoveTowards(_actionWeight, raw, Time.deltaTime / ActionBlendSeconds);
        bool reset = NoteSway(bone.GetInstanceID());
        bool handsActing = HandsActing(player);
        _handsAction = Mathf.MoveTowards(_handsAction, handsActing ? 1f : 0f,
            Time.deltaTime / (handsActing ? HandsActionInSeconds : HandsActionOutSeconds));
        TrackArmMean(reset, armRotation, armPosition);

        float actionBlend = _handsAction * _handsAction * (3f - 2f * _handsAction);
        float gunScale = Mathf.Lerp(GunAmount, ActionAmount, actionBlend);
        BodyFrame(bones, frame, liveRotation, cameraPosition, reset, out Quaternion shownFrame, out Vector3 shownFramePosition,
            out Quaternion meanFrame, out Vector3 meanFramePosition);
        Quaternion shownArm = Quaternion.SlerpUnclamped(_armMeanRotation, armRotation, gunScale);
        Vector3 shownArmPosition = Vector3.LerpUnclamped(_armMeanPosition, armPosition, gunScale);
        Quaternion shownSight = shownFrame * shownArm * boneLocalRotation;
        Vector3 shownSightPosition = shownFramePosition + shownFrame * (shownArmPosition + shownArm * boneLocalPosition);
        Quaternion meanSightRotation = meanFrame * _armMeanRotation * boneLocalRotation;
        Vector3 meanSightPosition = meanFramePosition + meanFrame * (_armMeanPosition + _armMeanRotation * boneLocalPosition);
        _sightMeanRotation = meanSightRotation;
        _sightMeanPosition = meanSightPosition;
        _logSightTurn = Quaternion.Angle(meanSightRotation, frameInCamera * armRotation * boneLocalRotation);
        _logSightShift = (frameInCameraPosition + frameInCamera * (armPosition + armRotation * boneLocalPosition) - meanSightPosition).magnitude * 100f;
        _logGunOnBody = Quaternion.Angle(Quaternion.identity, Quaternion.Inverse(body.rotation) * weapon.rotation);
        _logArmTurn = Quaternion.Angle(_armMeanRotation, armRotation);
        Quaternion meanSight = look * meanSightRotation;
        Quaternion correction = AlignSightTo(meanSight, look * Vector3.forward) * Quaternion.Inverse(meanSight);
        Quaternion sightRotation = correction * look * shownSight;
        float relief = SightRelief(animation, bone);
        Vector3 shownShift = shownSightPosition - meanSightPosition;
        Vector3 sightPoint = cameraPosition + look * new Vector3(0f, 0f, ForwardDistance + relief)
            + correction * (look * shownShift);
        Quaternion targetWeaponRotation = sightRotation * Quaternion.Inverse(boneLocalRotation);
        Vector3 recoilInWeapon = Quaternion.Inverse(weapon.rotation) * recoil;
        Vector3 targetWeaponPosition = sightPoint - targetWeaponRotation * boneLocalPosition
            + targetWeaponRotation * (recoilInWeapon * RecoilAmount);
        Vector3 target = targetWeaponPosition - weapon.position;

        _offset = target;
        _laserDisplacement = target;
        _hasLaserDisplacement = target.sqrMagnitude >= 0.00000001f;
        weapon.SetPositionAndRotation(targetWeaponPosition, targetWeaponRotation);
        return true;
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
            // Held through a hand action, so the whole action stays a departure on its own scale.
            float follow = MeanFollow() * (1f - _handsAction);
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

    private static bool NoteSway(int sightId)
    {
        if (!_hasSway || sightId != _swaySightId)
        {
            _swaySightId = sightId;
            _hasSway = true;
            _sinceReset = 0f;
            return true;
        }

        _sinceReset += Time.deltaTime;
        return false;
    }

    // Pelvis, the two waist bones and the chest, each one's own bend, scaled separately and then
    // joined back into the chest the gun hangs on. At full scale the joined pose is the game's.
    private static void BodyFrame(PlayerBones bones, Transform frame, Quaternion liveRotation, Vector3 cameraPosition, bool reset,
        out Quaternion shownFrame, out Vector3 shownFramePosition, out Quaternion meanFrame, out Vector3 meanFramePosition)
    {
        EnsureBodyParts(bones, frame);
        Quaternion toCamera = Quaternion.Inverse(liveRotation);
        if (_bodyParts.Length == 0)
        {
            shownFrame = meanFrame = toCamera * frame.rotation;
            shownFramePosition = meanFramePosition = toCamera * (frame.position - cameraPosition);
            return;
        }

        Transform anchor = _bodyParts[0].Bone.parent != null ? _bodyParts[0].Bone.parent : _bodyParts[0].Bone;
        Quaternion reproducedRotation = anchor.rotation;
        Vector3 reproducedPosition = anchor.position;
        Quaternion scaledRotation = reproducedRotation;
        Vector3 scaledPosition = reproducedPosition;
        Quaternion meanRotation = reproducedRotation;
        Vector3 meanPosition = reproducedPosition;
        float follow = reset ? 1f : MeanFollow();
        for (int i = 0; i < _bodyParts.Length; i++)
        {
            BodyPart part = _bodyParts[i];
            Transform bone = part.Bone;
            if (bone == null)
                continue;

            Quaternion local = bone.localRotation;
            Vector3 localPosition = bone.localPosition;
            if (reset)
            {
                part.MeanRotation = local;
                part.MeanPosition = localPosition;
            }
            else
            {
                part.MeanRotation = Quaternion.Slerp(part.MeanRotation, local, follow);
                part.MeanPosition = Vector3.Lerp(part.MeanPosition, localPosition, follow);
            }

            _bodyParts[i] = part;
            float amount = PartAmount(part.Kind);
            Quaternion usedRotation = Quaternion.SlerpUnclamped(part.MeanRotation, local, amount);
            Vector3 usedPosition = Vector3.LerpUnclamped(part.MeanPosition, localPosition, amount);
            reproducedPosition += reproducedRotation * localPosition;
            reproducedRotation *= local;
            scaledPosition += scaledRotation * usedPosition;
            scaledRotation *= usedRotation;
            meanPosition += meanRotation * part.MeanPosition;
            meanRotation *= part.MeanRotation;
        }

        Quaternion shownWorld = frame.rotation * Quaternion.Inverse(reproducedRotation) * scaledRotation;
        Vector3 shownWorldPosition = frame.position + (scaledPosition - reproducedPosition);
        Quaternion meanWorld = frame.rotation * Quaternion.Inverse(reproducedRotation) * meanRotation;
        Vector3 meanWorldPosition = frame.position + (meanPosition - reproducedPosition);
        shownFrame = toCamera * shownWorld;
        shownFramePosition = toCamera * (shownWorldPosition - cameraPosition);
        meanFrame = toCamera * meanWorld;
        meanFramePosition = toCamera * (meanWorldPosition - cameraPosition);
    }

    private static float PartAmount(int kind)
    {
        switch (kind)
        {
            case 0: return PelvisAmount;
            case 1: return Spine1Amount;
            case 2: return Spine2Amount;
            default: return ChestAmount;
        }
    }

    private static void EnsureBodyParts(PlayerBones bones, Transform frame)
    {
        int owner = bones.GetInstanceID();
        if (_bodyPartsOwner == owner && _bodyParts.Length > 0 && _bodyParts[0].Bone != null)
            return;

        Transform? pelvis = bones.Pelvis != null ? bones.Pelvis.Original : null;
        Transform? spine1 = bones.Spine1;
        Transform? spine3 = bones.Spine3 != null ? bones.Spine3.Original : null;
        Transform? stop = pelvis != null ? pelvis : bones.RootJoint;
        var chain = new List<Transform>();
        for (Transform? bone = frame; bone != null; bone = bone.parent)
        {
            if (bones.RootJoint != null && bone == bones.RootJoint)
                break;
            chain.Add(bone);
            if (stop != null && bone == stop)
                break;
        }

        chain.Reverse();
        var parts = new BodyPart[chain.Count];
        string described = "";
        for (int i = 0; i < chain.Count; i++)
        {
            Transform bone = chain[i];
            int kind = PartKind(bone, pelvis, spine1, spine3);
            parts[i] = new BodyPart
            {
                Bone = bone,
                Kind = kind,
                MeanRotation = bone.localRotation,
                MeanPosition = bone.localPosition
            };
            if (described.Length > 0)
                described += " → ";
            described += PartName(kind) + " " + bone.name;
        }

        _bodyParts = parts;
        _bodyPartsOwner = owner;
        Log?.LogInfo("身体分段: " + described);
    }

    private static int PartKind(Transform bone, Transform? pelvis, Transform? spine1, Transform? spine3)
    {
        if (pelvis != null && bone == pelvis)
            return 0;
        if (spine1 != null && bone == spine1)
            return 1;
        if (spine3 != null && bone == spine3)
            return 3;

        string name = bone.name.Replace(" ", string.Empty).Replace("_", string.Empty);
        if (name.IndexOf("pelvis", System.StringComparison.OrdinalIgnoreCase) >= 0)
            return 0;
        if (name.IndexOf("spine1", System.StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("spine01", System.StringComparison.OrdinalIgnoreCase) >= 0)
            return 1;
        if (name.IndexOf("spine3", System.StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("spine03", System.StringComparison.OrdinalIgnoreCase) >= 0)
            return 3;
        return 2;
    }

    private static string PartName(int kind)
    {
        switch (kind)
        {
            case 0: return "胯";
            case 1: return "腰";
            case 2: return "上腰";
            default: return "胸";
        }
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
        _handsAction = 0f;
    }

    // The hands animator leaves its idle state for bolt cycling, fire mode switches, reloads, checks.
    private static bool HandsActing(Player player)
    {
        var hands = player.HandsController;
        if (hands == null)
            return false;

        if (hands.IsInInteractionStrictCheck())
            return true;

        FirearmsAnimator? animator = hands.FirearmsAnimator;
        return animator != null && !animator.IsIdling();
    }

    // In third person the game's recoil only pushes the gun, in the frame of Weapon_root_3rd_anim.
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
