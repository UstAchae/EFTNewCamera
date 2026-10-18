using UnityEngine;

namespace EFTNewCamera
{
internal static class ScreenLineMath
{
    // Return the portions outside the lens region; never draw the main-view overlay through the optic.
    internal static int OutsideRect(Vector2 a, Vector2 b, Rect rect,
        out Vector2 a0, out Vector2 b0, out Vector2 a1, out Vector2 b1)
    {
        a0 = a; b0 = b; a1 = b1 = Vector2.zero;
        Vector2 d = b - a;
        float enter = 0f, leave = 1f;
        if (!Edge(-d.x, a.x-rect.xMin, ref enter, ref leave)
            || !Edge(d.x, rect.xMax-a.x, ref enter, ref leave)
            || !Edge(-d.y, a.y-rect.yMin, ref enter, ref leave)
            || !Edge(d.y, rect.yMax-a.y, ref enter, ref leave)) return 1;
        int count = 0;
        if (enter > 0f) { a0 = a; b0 = a + enter*d; count = 1; }
        if (leave < 1f)
        {
            if (count == 0) { a0 = a+leave*d; b0 = b; count = 1; }
            else { a1 = a+leave*d; b1 = b; count = 2; }
        }
        return count;
    }

    private static bool Edge(float p, float q, ref float enter, ref float leave)
    {
        if (Mathf.Abs(p) < 0.0000001f) return q >= 0f;
        float t = q/p;
        if (p < 0f) { if (t > leave) return false; enter = Mathf.Max(enter,t); }
        else { if (t < enter) return false; leave = Mathf.Min(leave,t); }
        return true;
    }
}
}
