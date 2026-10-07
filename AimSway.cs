using System.Collections.Generic;
using EFT;
using EFT.Animations;
using UnityEngine;

namespace EFTNewCamera;

// What the eye sees of the gun is the gun's pose against the camera. Below the chest both hang on
// the same bones, so the body under it moves them together and never shows. What shows is the
// neck and head carrying the camera, the arm animation and the gun's own motion carrying the gun,
// and recoil. Each of those is split into turns and shifts along the camera's own axes, and every
// one is scaled against its average on its own. At full scale the gun's pose against the camera is
// the game's; only its average is moved so the sight sits in front of the eye.
internal static class AimSway
{
    internal const int Head = 0;
    internal const int Neck = 1;
    internal const int Arm = 2;
    internal const int Gun = 3;
    internal const int PartCount = 4;

    internal const int Yaw = 0;
    internal const int Pitch = 1;
    internal const int Roll = 2;
    internal const int Right = 3;
    internal const int Up = 4;
    internal const int Forward = 5;
    internal const int AxisCount = 6;

    internal static readonly float[,] Amounts = NewAmounts();
    internal static readonly float[] RecoilAmounts = { 1f, 1f, 1f };

    private const float BaseSeconds = 2f;
    private const float ActionBlendSeconds = 0.15f;
    private const float WalkingSpeed = 0.5f;
    private const float MeanStartSeconds = 0.08f;
    private const float MeanSettleHoldSeconds = 2f;
    private const float MeanSettleSeconds = 1f;
    private const float HandsActionInSeconds = 0.1f;
    private const float HandsActionOutSeconds = 0.3f;

    private struct Joint
    {
        public int Part;
        public Quaternion Rotation;
        public Vector3 Position;
    }

    private static readonly List<Joint> Joints = new();
    private static readonly List<Quaternion> MeanRotations = new();
    private static readonly List<Vector3> MeanPositions = new();
    private static bool _hasMean;
    private static int _sightId;
    private static float _sinceReset;
    private static float _standWeight;
    private static float _handsAction;
    private static int _loggedChain;

    internal static BepInEx.Logging.ManualLogSource? Log;

    private static float[,] NewAmounts()
    {
        var amounts = new float[PartCount, AxisCount];
        for (int part = 0; part < PartCount; part++)
        {
            for (int axis = 0; axis < AxisCount; axis++)
                amounts[part, axis] = 1f;
        }

        return amounts;
    }

    internal static void Reset()
    {
        _hasMean = false;
        _handsAction = 0f;
    }

    internal static bool Place(Player player, PlayerBones bones, Transform head, Vector3 cameraPosition, Quaternion cameraRotation,
        float forwardDistance, out int sightId)
    {
        sightId = 0;
        Transform? weapon = bones.Weapon_Root_Anim;
        Transform? body = bones.Weapon_Root_Third;
        ProceduralWeaponAnimation? animation = player.ProceduralWeaponAnimation;
        Transform? sight = animation != null && animation.ScopeAimTransforms != null && animation.ScopeAimTransforms.Count > 0
            ? animation.CurrentScope?.Bone
            : null;
        if (weapon == null || body == null || sight == null || animation == null)
            return false;

        Transform chest = body.parent != null ? body.parent : body;
        Quaternion toChest = Quaternion.Inverse(chest.rotation);
        Quaternion sightLocalRotation = Quaternion.Inverse(weapon.rotation) * sight.rotation;
        Vector3 sightLocalPosition = Quaternion.Inverse(weapon.rotation) * (sight.position - weapon.position);
        Vector3 recoilLocal = RecoilLocal(animation);

        int cameraJoints = CollectCameraJoints(chest, head, toChest);
        Joints.Add(new Joint
        {
            Part = Arm,
            Rotation = toChest * body.rotation,
            Position = toChest * (body.position - chest.position)
        });
        Joints.Add(new Joint
        {
            Part = Gun,
            Rotation = Quaternion.Inverse(body.rotation) * weapon.rotation,
            Position = Quaternion.Inverse(body.rotation) * (weapon.position - body.position) - recoilLocal
        });
        Quaternion cameraOnHead = Quaternion.Inverse(head.rotation) * cameraRotation;
        Vector3 cameraOnHeadPosition = Quaternion.Inverse(head.rotation) * (cameraPosition - head.position);
        Quaternion cameraInChest = toChest * cameraRotation;

        sightId = sight.GetInstanceID();
        bool reset = !_hasMean || sightId != _sightId || MeanRotations.Count != Joints.Count;
        if (reset)
        {
            _hasMean = true;
            _sightId = sightId;
            _sinceReset = 0f;
            MeanRotations.Clear();
            MeanPositions.Clear();
            foreach (Joint joint in Joints)
            {
                MeanRotations.Add(joint.Rotation);
                MeanPositions.Add(joint.Position);
            }
        }
        else
        {
            _sinceReset += Time.deltaTime;
        }

        UpdateWeights(player);
        float follow = reset ? 1f : MeanFollow() * (1f - _handsAction);
        float action = _handsAction * _handsAction * (3f - 2f * _handsAction);

        Quaternion parentShown = Quaternion.identity;
        Vector3 positionShown = Vector3.zero;
        Quaternion parentMean = Quaternion.identity;
        Vector3 positionMean = Vector3.zero;
        Quaternion parentLive = Quaternion.identity;
        Quaternion cameraShown = Quaternion.identity;
        Vector3 cameraShownPosition = Vector3.zero;
        Quaternion cameraMean = Quaternion.identity;
        Vector3 cameraMeanPosition = Vector3.zero;
        Vector3 recoilShown = Vector3.zero;
        for (int i = 0; i < Joints.Count; i++)
        {
            Joint joint = Joints[i];
            if (i == cameraJoints)
            {
                cameraShownPosition = positionShown + parentShown * cameraOnHeadPosition;
                cameraShown = parentShown * cameraOnHead;
                cameraMeanPosition = positionMean + parentMean * cameraOnHeadPosition;
                cameraMean = parentMean * cameraOnHead;
                parentShown = parentMean = parentLive = Quaternion.identity;
                positionShown = positionMean = Vector3.zero;
            }

            MeanRotations[i] = Quaternion.Slerp(MeanRotations[i], joint.Rotation, follow);
            MeanPositions[i] = Vector3.Lerp(MeanPositions[i], joint.Position, follow);
            Quaternion meanRotation = MeanRotations[i];
            Vector3 meanPosition = MeanPositions[i];

            Quaternion frame = parentLive * meanRotation;
            Quaternion turn = Quaternion.Inverse(meanRotation) * joint.Rotation;
            Quaternion turnInCamera = Quaternion.Inverse(cameraInChest) * frame * turn * Quaternion.Inverse(frame) * cameraInChest;
            Quaternion shownTurnInCamera = ScaleTurn(turnInCamera, joint.Part, action);
            Quaternion shownTurn = Quaternion.Inverse(frame) * cameraInChest * shownTurnInCamera * Quaternion.Inverse(cameraInChest) * frame;
            Vector3 shiftInCamera = Quaternion.Inverse(cameraInChest) * (parentLive * (joint.Position - meanPosition));
            Vector3 shownShift = Quaternion.Inverse(parentLive) * (cameraInChest * ScaleShift(shiftInCamera, joint.Part, action));

            positionShown += parentShown * (meanPosition + shownShift);
            parentShown *= meanRotation * shownTurn;
            positionMean += parentMean * meanPosition;
            parentMean *= meanRotation;
            parentLive *= joint.Rotation;

            if (joint.Part == Arm)
            {
                Vector3 recoilInCamera = Quaternion.Inverse(cameraInChest) * (parentLive * recoilLocal);
                recoilShown = parentShown * (Quaternion.Inverse(parentLive) * (cameraInChest * ScaleRecoil(recoilInCamera, action)));
            }
        }

        Vector3 gunShownPosition = positionShown + recoilShown;
        Quaternion gunShown = parentShown;
        Quaternion relative = Quaternion.Inverse(cameraShown) * gunShown;
        Vector3 sightShown = Quaternion.Inverse(cameraShown) * (gunShownPosition + gunShown * sightLocalPosition - cameraShownPosition);
        Vector3 sightMean = Quaternion.Inverse(cameraMean) * (positionMean + parentMean * sightLocalPosition - cameraMeanPosition);

        float relief = SightRelief(animation, sight);
        Vector3 sightInCamera = new Vector3(0f, 0f, forwardDistance + relief) + (sightShown - sightMean);
        Quaternion weaponRotation = cameraRotation * relative;
        Vector3 weaponPosition = cameraPosition + cameraRotation * sightInCamera - weaponRotation * sightLocalPosition;
        weapon.SetPositionAndRotation(weaponPosition, weaponRotation);
        LogChain(chest, head, cameraJoints);
        return true;
    }

    // Neck and head bones from the chest out to the head, each in its parent's frame.
    private static int CollectCameraJoints(Transform chest, Transform head, Quaternion toChest)
    {
        Joints.Clear();
        var chain = new List<Transform>();
        for (Transform? bone = head; bone != null && bone != chest; bone = bone.parent)
            chain.Add(bone);

        bool underChest = chain.Count > 0 && chain[chain.Count - 1].parent == chest;
        if (!underChest)
        {
            Joints.Add(new Joint
            {
                Part = Neck,
                Rotation = toChest * head.rotation,
                Position = toChest * (head.position - chest.position)
            });
            return Joints.Count;
        }

        for (int i = chain.Count - 1; i >= 0; i--)
        {
            Transform bone = chain[i];
            Joints.Add(new Joint
            {
                Part = bone == head ? Head : Neck,
                Rotation = bone.localRotation,
                Position = Vector3.Scale(bone.parent.lossyScale, bone.localPosition)
            });
        }

        return Joints.Count;
    }

    private static Quaternion ScaleTurn(Quaternion turn, int part, float action)
    {
        turn.ToAngleAxis(out float angle, out Vector3 axis);
        if (angle > 180f)
            angle -= 360f;
        if (float.IsNaN(axis.x) || Mathf.Abs(angle) < 0.00001f)
            return turn;

        Vector3 vector = axis * angle;
        vector.x *= Amount(part, Pitch, action);
        vector.y *= Amount(part, Yaw, action);
        vector.z *= Amount(part, Roll, action);
        float size = vector.magnitude;
        return size < 0.00001f ? Quaternion.identity : Quaternion.AngleAxis(size, vector / size);
    }

    private static Vector3 ScaleShift(Vector3 shift, int part, float action)
    {
        return new Vector3(
            shift.x * Amount(part, Right, action),
            shift.y * Amount(part, Up, action),
            shift.z * Amount(part, Forward, action));
    }

    private static Vector3 ScaleRecoil(Vector3 recoil, float action)
    {
        return new Vector3(
            recoil.x * Mathf.Lerp(RecoilAmounts[0], 1f, action),
            recoil.y * Mathf.Lerp(RecoilAmounts[1], 1f, action),
            recoil.z * Mathf.Lerp(RecoilAmounts[2], 1f, action));
    }

    // Hand actions (bolt cycling, fire mode switches, reloads) always play at the game's own size.
    private static float Amount(int part, int axis, float action)
    {
        return Mathf.Lerp(Amounts[part, axis], 1f, action);
    }

    private static void UpdateWeights(Player player)
    {
        bool acting = HandsActing(player);
        _handsAction = Mathf.MoveTowards(_handsAction, acting ? 1f : 0f,
            Time.deltaTime / (acting ? HandsActionInSeconds : HandsActionOutSeconds));
        Vector3 velocity = player.Velocity;
        float walking = Mathf.Clamp01(new Vector2(velocity.x, velocity.z).magnitude / WalkingSpeed);
        float standing = acting ? 1f : 1f - walking;
        _standWeight = Mathf.MoveTowards(_standWeight, standing, Time.deltaTime / ActionBlendSeconds);
    }

    // After aiming in, the body keeps settling into its aiming pose for about one and a half
    // seconds. The averages track that closely so it is not taken for sway, then relax.
    private static float MeanFollow()
    {
        float settling = _sinceReset <= MeanSettleHoldSeconds
            ? MeanStartSeconds
            : Mathf.Lerp(MeanStartSeconds, BaseSeconds, Mathf.Clamp01((_sinceReset - MeanSettleHoldSeconds) / MeanSettleSeconds));
        // Walking, a tight average would ride along with the steps it is meant to hold out.
        float seconds = Mathf.Lerp(BaseSeconds, settling, _standWeight);
        return 1f - Mathf.Exp(-Time.deltaTime / seconds);
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
    private static Vector3 RecoilLocal(ProceduralWeaponAnimation animation)
    {
        var effect = animation.Shootingg?.CurrentRecoilEffect;
        if (effect == null)
            return Vector3.zero;

        Vector3 local = effect.GetHandPositionRecoil();
        if (effect.WeaponRecoilEffect != null)
            local += effect.WeaponRecoilEffect.GetPositionRecoil();
        return local;
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

    private static void LogChain(Transform chest, Transform head, int cameraJoints)
    {
        int key = chest.GetInstanceID() * 31 + head.GetInstanceID() * 7 + cameraJoints;
        if (Log == null || key == _loggedChain)
            return;

        _loggedChain = key;
        string chain = "";
        for (Transform? bone = head; bone != null && bone != chest; bone = bone.parent)
            chain = bone.name + (chain.Length > 0 ? " → " : "") + chain;
        Log.LogInfo("瞄准摆动分段: 胸 " + chest.name + " → " + chain + "（脖子和头共 " + cameraJoints + " 节）");
    }
}
