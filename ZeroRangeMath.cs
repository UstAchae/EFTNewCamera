using UnityEngine;

namespace EFTNewCamera
{

internal static class ZeroRangeMath
{
    // Find a crossing of the vertical plane through the sight ray, then reject lateral misses.
    internal static bool Crossing(Vector3 previous, Vector3 next, Vector3 origin, Vector3 forward,
        Vector3 up, float toleranceMrad, out Vector3 intersection)
    {
        intersection = default;
        float a = Vector3.Dot(previous - origin, up);
        float b = Vector3.Dot(next - origin, up);
        if ((a > 0f && b > 0f) || (a < 0f && b < 0f) || Mathf.Abs(a - b) < 0.0000001f) return false;
        float t = a / (a - b);
        if (t < 0f || t > 1f) return false;
        intersection = Vector3.LerpUnclamped(previous, next, t);
        Vector3 relative = intersection - origin;
        float along = Vector3.Dot(relative, forward);
        if (along <= 0f) return false;
        float miss = (relative - forward * along).magnitude;
        return miss <= Mathf.Max(0.002f, along * toleranceMrad / 1000f);
    }

    internal static string Format(float farthest)
    {
        if (farthest < 0f || float.IsNaN(farthest) || float.IsInfinity(farthest)) return "—";
        return farthest < 50f ? "< 50 m" : farthest.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " m";
    }

    internal static string FormatPair(float nearest, float farthest, int count)
    {
        return "近交点  " + Format(count > 0 ? nearest : -1f)
            + "\n远交点  " + Format(count > 1 ? farthest : -1f);
    }
}
}
