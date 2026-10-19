using UnityEngine;

namespace EFTNewCamera
{
internal static class OpticAdjustmentMath
{
    internal static float Next(float current, float steps, float stepSize)
        => Mathf.Clamp(current+steps*stepSize,-50f,0f);
}
}
