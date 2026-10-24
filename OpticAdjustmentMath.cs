using UnityEngine;

namespace EFTNewCamera
{
internal static class OpticAdjustmentMath
{
    // Most compensation that can be dialled in, in mrad. Enough for the game's ranges (about 600 m for 7.62 mm).
    internal const float MaxCompensation = 4f;

    internal static float Next(float current, float steps, float stepSize)
        => Mathf.Clamp(current+steps*stepSize,-MaxCompensation,0f);
}
}
