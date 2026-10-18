using System.Collections.Generic;
using UnityEngine;

namespace EFTNewCamera
{

// Resolve the same deepest active front/rear markers as the native first-person
// calibration. Third-person animations do not populate its private marker cache.
internal static class IronSightAlignment
{
    private static Transform? _weapon;
    private static readonly List<Transform> Front = new();
    private static readonly List<Transform> Rear = new();
    private static readonly Queue<Transform> Pending = new();

    internal static void Invalidate()
    {
        _weapon = null;
        Front.Clear();
        Rear.Clear();
    }

    internal static bool TryResolve(Transform weapon, Transform sight, bool alignIfAvailable,
        bool optic, out Vector3 position, out Vector3 forward)
    {
        position = sight.position;
        forward = sight.name == "aim_camera" ? -sight.up : -sight.forward;
        if (!alignIfAvailable || optic || weapon == null)
            return false;
        if (_weapon != weapon)
        {
            Invalidate();
            _weapon = weapon;
            Pending.Enqueue(weapon);
            while (Pending.Count > 0)
            {
                Transform node = Pending.Dequeue();
                if (node.name.Contains("mod_align_front")) Front.Add(node);
                if (node.name.Contains("mod_align_rear")) Rear.Add(node);
                for (int i = 0; i < node.childCount; i++) Pending.Enqueue(node.GetChild(i));
            }
        }
        Transform? front = DeepestActive(Front, weapon);
        Transform? rear = DeepestActive(Rear, weapon);
        if (front == null || rear == null)
            return false;

        Vector3 rearLocal = weapon.InverseTransformPoint(rear.position);
        Vector3 frontLocal = weapon.InverseTransformPoint(front.position);
        Vector3 axis = frontLocal - rearLocal;
        // Weapon-local -Y is the longitudinal axis used by WeaponPrefab.AimPlane.
        // Reject coincident/reversed markers rather than producing an invalid pose.
        if (axis.y >= -0.0001f || axis.sqrMagnitude < 0.000001f)
            return false;
        float depth = weapon.InverseTransformPoint(sight.position).y;
        position = weapon.TransformPoint(rearLocal + axis * ((depth - rearLocal.y) / axis.y));
        forward = (front.position - rear.position).normalized;
        return true;
    }

    private static Transform? DeepestActive(List<Transform> markers, Transform weapon)
    {
        Transform? result = null;
        int deepest = -1;
        foreach (Transform marker in markers)
        {
            if (marker == null || !marker.gameObject.activeInHierarchy || !marker.IsChildOf(weapon))
                continue;
            int depth = 0;
            for (Transform parent = marker; parent != weapon && parent != null; parent = parent.parent) depth++;
            if (depth <= deepest) continue;
            result = marker;
            deepest = depth;
        }
        return result;
    }
}
}
