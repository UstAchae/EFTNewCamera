using System.Collections.Generic;
using EFT;
using UnityEngine;

namespace EFTNewCamera;

// Two-bone IK for the arms and legs: each limb is gathered with where its end should go (GripArms, GripLegs), and
// all of them bent there at once (Reach).
internal static class LimbIk
{
    private struct Limb
    {
        internal Transform Upper;
        internal Transform Lower;
        internal Transform End;
        internal float UpperLength;
        internal float LowerLength;
        internal Vector3 Bend;
        internal Vector3 TargetPosition;
        internal Quaternion TargetRotation;
        internal bool Leg;
    }

    private static readonly List<Limb> Limbs = new();

    // Where each hand should end up (target maps its current pose there), with the arm's lengths and elbow.
    internal static void GripArms(PlayerBones bones, List<Transform> upperarms, Transform? moved, System.Func<Vector3, Vector3> target, Quaternion turn)
    {
        Limbs.Clear();
        AddArm(bones.LeftPalm, upperarms, moved, target, turn);
        AddArm(bones.RightPalm, upperarms, moved, target, turn);
    }

    private static void AddArm(Transform? hand, List<Transform> upperarms, Transform? moved, System.Func<Vector3, Vector3> target, Quaternion turn)
    {
        if (hand == null || (moved != null && !hand.IsChildOf(moved)))
            return;
        Transform? upper = null;
        foreach (Transform arm in upperarms)
        {
            if (arm != null && hand.IsChildOf(arm))
            {
                upper = arm;
                break;
            }
        }
        if (upper == null)
            return;

        Transform lower = hand;
        while (lower.parent != null && lower.parent != upper)
            lower = lower.parent;
        if (lower.parent != upper || lower == hand)
            return;
        AddLimb(upper, lower, hand, target(hand.position), turn * hand.rotation, false);
    }

    // The feet stay where they are; thigh, calf and foot are found by the rig's bone names.
    internal static void GripLegs(PlayerBones bones, Transform pelvis)
    {
        AddLeg(bones.LeftThigh1?.Original, pelvis, "LCalf", "LFoot");
        AddLeg(bones.RightThigh1?.Original, pelvis, "RCalf", "RFoot");
    }

    private static void AddLeg(Transform? thigh, Transform pelvis, string calfName, string footName)
    {
        if (thigh == null || !thigh.IsChildOf(pelvis))
            return;
        Transform? calf = FindBelow(thigh, calfName);
        Transform? foot = calf != null ? FindBelow(calf, footName) : null;
        if (calf == null || foot == null)
            return;
        AddLimb(thigh, calf, foot, foot.position, foot.rotation, true);
    }

    private static Transform? FindBelow(Transform root, string name)
    {
        foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
        {
            if (child != root && child.name.EndsWith(name, System.StringComparison.Ordinal))
                return child;
        }
        return null;
    }

    private static void AddLimb(Transform upper, Transform lower, Transform end, Vector3 position, Quaternion rotation, bool leg)
    {
        Limbs.Add(new Limb
        {
            Upper = upper,
            Lower = lower,
            End = end,
            UpperLength = Vector3.Distance(upper.position, lower.position),
            LowerLength = Vector3.Distance(lower.position, end.position),
            Bend = lower.position,
            TargetPosition = position,
            TargetRotation = rotation,
            Leg = leg
        });
    }

    internal static bool Reaching(Transform upper)
    {
        foreach (Limb limb in Limbs)
        {
            if (limb.Upper == upper)
                return true;
        }
        return false;
    }

    // Bend each limb so its end reaches the target pose (two-bone IK by the law of cosines). An elbow keeps to
    // the side it was on, moved by armMove; a knee likewise, moved by legMove (the hips).
    internal static void Reach(System.Func<Vector3, Vector3> armMove, System.Func<Vector3, Vector3>? legMove = null)
    {
        foreach (Limb limb in Limbs)
        {
            if (limb.Upper == null || limb.Lower == null || limb.End == null || limb.UpperLength < 0.001f || limb.LowerLength < 0.001f)
                continue;

            Vector3 root = limb.Upper.position;
            Vector3 toEnd = limb.TargetPosition - root;
            float reach = toEnd.magnitude;
            if (reach < 0.001f)
                continue;
            Vector3 along = toEnd / reach;
            float distance = Mathf.Clamp(reach, Mathf.Abs(limb.UpperLength - limb.LowerLength) + 0.001f, limb.UpperLength + limb.LowerLength - 0.001f);
            Vector3 side = Vector3.ProjectOnPlane((limb.Leg && legMove != null ? legMove : armMove)(limb.Bend) - root, along);
            if (side.sqrMagnitude < 0.00000001f)
                side = Vector3.ProjectOnPlane(limb.Lower.position - root, along);
            if (side.sqrMagnitude < 0.00000001f)
                continue;
            side.Normalize();

            float cos = (limb.UpperLength * limb.UpperLength + distance * distance - limb.LowerLength * limb.LowerLength) / (2f * limb.UpperLength * distance);
            float sin = Mathf.Sqrt(Mathf.Max(0f, 1f - cos * cos));
            Vector3 bend = root + along * (limb.UpperLength * cos) + side * (limb.UpperLength * sin);
            limb.Upper.rotation = PoseMath.TurnFromTo(limb.Lower.position - root, bend - root) * limb.Upper.rotation;
            limb.Lower.rotation = PoseMath.TurnFromTo(limb.End.position - limb.Lower.position, limb.TargetPosition - limb.Lower.position) * limb.Lower.rotation;
            limb.End.SetPositionAndRotation(limb.TargetPosition, limb.TargetRotation);
        }
        Limbs.Clear();
    }
}
