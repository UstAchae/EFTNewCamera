using UnityEngine;

namespace EFTNewCamera;

// Turns and carried poses, exact where Unity's own rounds small turns away.
internal static class PoseMath
{
    // The turn that takes one direction onto another, however small. Quaternion.FromToRotation calls anything under
    // about 0.08 degrees parallel and returns no turn, then the whole of it at once past that: the sight's
    // alignment set off a few pixels at once halfway through the aim-in.
    internal static Quaternion TurnFromTo(Vector3 from, Vector3 to)
    {
        Vector3 axis = Vector3.Cross(from, to);
        float sine = axis.magnitude;
        float cosine = Vector3.Dot(from, to);
        if (sine < 1e-12f)
            return cosine >= 0f ? Quaternion.identity : Quaternion.FromToRotation(from, to);
        return Quaternion.AngleAxis(Mathf.Atan2(sine, cosine) * Mathf.Rad2Deg, axis / sine);
    }

    // Quaternion.Angle and == treat anything under about 0.16 degrees as no turn at all,
    // which shows as a visible hop when the last bit of the follow delay is dropped.
    internal static bool Settled(Quaternion turn)
    {
        float sine = new Vector3(turn.x, turn.y, turn.z).magnitude;
        return 2f * Mathf.Atan2(sine, Mathf.Abs(turn.w)) * Mathf.Rad2Deg < 0.005f;
    }

    internal static Quaternion Scale(Quaternion rotation, float amount)
    {
        if (rotation.w < 0f)
            rotation = new Quaternion(-rotation.x, -rotation.y, -rotation.z, -rotation.w);
        return Quaternion.SlerpUnclamped(Quaternion.identity, rotation, amount);
    }

    internal static Quaternion Twist(Quaternion rotation, Vector3 axis)
    {
        Vector3 part = Vector3.Project(new Vector3(rotation.x, rotation.y, rotation.z), axis);
        Quaternion twist = new Quaternion(part.x, part.y, part.z, rotation.w);
        float length = Mathf.Sqrt(twist.x * twist.x + twist.y * twist.y + twist.z * twist.z + twist.w * twist.w);
        return length < 0.0001f ? Quaternion.identity : new Quaternion(twist.x / length, twist.y / length, twist.z / length, twist.w / length);
    }

    internal static void Carry(Transform transform, Vector3 pivot, Vector3 displacement, Quaternion turn)
    {
        transform.SetPositionAndRotation(pivot + displacement + turn * (transform.position - pivot), turn * transform.rotation);
    }
}
