using UnityEngine;

namespace EFTNewCamera
{
internal static class AimEyeReliefMath
{
    // Same depth cap used by native CalculateCameraPosition/MountedWeaponCamera.
    internal static float Resolve(float boneDepth, float selectedPlaneDepth, float opticalFarDepth, float weaponLocalY)
        => boneDepth-Mathf.Min(selectedPlaneDepth,opticalFarDepth-weaponLocalY);
}
}
