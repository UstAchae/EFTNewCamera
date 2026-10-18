using UnityEngine;

namespace EFTNewCamera
{
internal static class SightReferenceMath
{
    // SPT 4.1.6 ships these meshes without CPU read access. Values extracted from the central
    // UV triangle of the original bundles; guard identity/bounds instead of guessing a mm/mrad ratio.
    internal static bool NativeVertical(string template, string meshName, int vertexCount, Vector3 boundsSize, out Vector3 perV)
    {
        perV = Vector3.zero;
        bool pu = template == "5b3f7c1c5acfc40dc5296b1d";
        bool pso = template == "5c82342f2e221644f31c060e" || template == "5c82343a2e221644f31c0611" || template == "576fd4ec2459777f0b518431";
        float size = pu ? .02628561f : .03283198f;
        if ((!pu && !pso) || vertexCount != (pu ? 60 : 62)
            || (pu ? meshName != "linza" : meshName != "linza_mode_000" && meshName != "linza_mode_001")
            || Mathf.Abs(boundsSize.x-size) > .000001f || Mathf.Abs(boundsSize.z-size) > .000001f) return false;
        perV = pu ? new Vector3(-1.8647153e-10f, 4.8424276e-8f, .01799232f)
            : new Vector3(-2.5208582e-9f, -2.498311e-7f, .022473277f);
        return true;
    }

    // Recover the mesh displacement for one unit of texture V, preserving authored UV orientation.
    internal static bool UvVertical(Vector3 a, Vector3 b, Vector3 c, Vector2 ua, Vector2 ub, Vector2 uc, out Vector3 vertical)
    {
        Vector2 u = ub - ua, v = uc - ua;
        float determinant = u.x * v.y - u.y * v.x;
        vertical = Vector3.zero;
        if (Mathf.Abs(determinant) < 0.000001f) return false;
        vertical = ((c - a) * u.x - (b - a) * v.x) / determinant;
        return vertical.sqrMagnitude > 0.0000000001f;
    }

    internal static Vector3 LegacyTranslation(Matrix4x4 objectToWorld, Vector3 perV, float markScale, float textureShift)
    {
        // Match CW FX/OpticSight's corrected object Y column, not the raw Transform matrix.
        Vector4 x = objectToWorld.GetColumn(0), y = objectToWorld.GetColumn(1);
        float ratio = y.magnitude > 0.000001f ? x.magnitude / y.magnitude : 1f;
        objectToWorld.SetColumn(1, new Vector4(y.x, y.z, y.y, 0f) * ratio);
        // Native mark sampling is 10*UV-4.5; translating the texture by d moves mesh UV by d/10.
        return objectToWorld.MultiplyVector(perV * (markScale * textureShift / 10f));
    }

    internal static bool ProjectionTranslation(Matrix4x4 projection, Matrix4x4 cameraToWorld,
        Vector3 planePoint, float ndcShift, out Vector3 translation)
    {
        translation = Vector3.zero;
        Vector3 view = cameraToWorld.inverse.MultiplyPoint3x4(planePoint);
        Vector4 clip = projection * new Vector4(view.x, view.y, view.z, 1f);
        if (Mathf.Abs(clip.w) < 0.000001f) return false;
        // Solve screen X unchanged / screen Y shifted at exactly the same view depth.
        // Avoid subtracting two inverse-projected distant points (loss of depth precision).
        float x = clip.x / clip.w, y = clip.y / clip.w + ndcShift;
        float a = projection.m00 - x * projection.m30, b = projection.m01 - x * projection.m31;
        float c = projection.m10 - y * projection.m30, d = projection.m11 - y * projection.m31;
        float determinant = a * d - b * c;
        if (Mathf.Abs(determinant) < 0.000001f) return false;
        float rhs = ndcShift * clip.w;
        translation = cameraToWorld.MultiplyVector(new Vector3(-b * rhs / determinant, a * rhs / determinant, 0f));
        return true;
    }
}
}
