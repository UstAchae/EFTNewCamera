using EFT;
using EFT.Animations;
using EFT.CameraControl;
using UnityEngine;

namespace EFTNewCamera;

internal static class OpticPicture
{
    private static int _frame = -1;
    private static bool _placed;
    private static Camera? _savedCamera;
    private static Vector3 _savedPosition;
    private static Quaternion _savedRotation;
    private static OpticSight? _held;

    internal static bool IsCamera(Camera camera)
    {
        try
        {
            Camera? optic = CameraManager.Instance?.OpticCameraManager?.Camera;
            return optic != null && optic.GetInstanceID() == camera.GetInstanceID();
        }
        catch
        {
            return false;
        }
    }

    internal static void Apply(Player player)
    {
        if (_frame == Time.frameCount)
            return;
        _frame = Time.frameCount;

        try
        {
            ProceduralWeaponAnimation? animation = player.ProceduralWeaponAnimation;
            OpticCameraManager? manager = CameraManager.Instance?.OpticCameraManager;
            bool aiming = player.HandsController != null && player.HandsController.IsAiming;
            if (animation == null || manager == null || !aiming || animation.CurrentScope == null || !animation.CurrentScope.IsOptic)
            {
                ReleaseHeld();
                return;
            }

            OpticSight? sight = Resolve(animation);
            if (sight == null || !sight.gameObject.activeInHierarchy)
                return;

            OpticSight? current = manager.CurrentOpticSight;
            if (current != null && !Same(current, sight))
            {
                OpticSight.OpticSightState.Value = new OpticSight.OpticSightStatus
                {
                    IsEnabled = false,
                    OpticSight = current
                };
            }

            OpticSight.OpticSightStatus state = OpticSight.OpticSightState.Value;
            if (!state.IsEnabled || !Same(state.OpticSight, sight))
            {
                OpticSight.OpticSightState.Value = new OpticSight.OpticSightStatus
                {
                    IsEnabled = true,
                    OpticSight = sight
                };
            }

            _held = sight;
        }
        catch
        {
        }
    }

    internal static void PlaceOnDrawnScope(Camera camera, Player player)
    {
        if (!TryGetDrawnScopePose(player, out Vector3 position, out Quaternion rotation))
            return;

        if (!_placed)
        {
            _savedCamera = camera;
            _savedPosition = camera.transform.position;
            _savedRotation = camera.transform.rotation;
            _placed = true;
        }

        camera.transform.SetPositionAndRotation(position, rotation);
    }

    internal static void RestorePose()
    {
        if (!_placed || _savedCamera == null)
            return;

        _savedCamera.transform.SetPositionAndRotation(_savedPosition, _savedRotation);
        _placed = false;
        _savedCamera = null;
    }

    internal static void Release()
    {
        try
        {
            if (_held != null)
            {
                OpticSight.OpticSightState.Value = new OpticSight.OpticSightStatus
                {
                    IsEnabled = true,
                    OpticSight = _held
                };
            }
        }
        catch
        {
        }

        _held = null;
        _frame = -1;
        RestorePose();
    }

    private static bool TryGetDrawnScopePose(Player player, out Vector3 position, out Quaternion rotation)
    {
        position = default;
        rotation = Quaternion.identity;
        OpticSight? sight = CameraManager.Instance?.OpticCameraManager?.CurrentOpticSight;
        Transform? scopeData = sight?.ScopeData != null ? sight.ScopeData.transform : null;
        if (scopeData == null)
            return false;

        Transform? weapon = player.PlayerBones?.Weapon_Root_Anim;
        Transform pivot = scopeData;
        if (weapon != null && !scopeData.IsChildOf(weapon))
        {
            Transform? drawn = FindSameScope(weapon, scopeData);
            if (drawn != null)
                pivot = drawn;
        }

        position = pivot.position;
        rotation = pivot.rotation;
        return true;
    }

    private static Transform? FindSameScope(Transform weapon, Transform scopeData)
    {
        Transform? best = null;
        int bestScore = 0;
        foreach (Transform candidate in weapon.GetComponentsInChildren<Transform>(true))
        {
            if (!string.Equals(candidate.name, scopeData.name, System.StringComparison.Ordinal))
                continue;

            int score = 0;
            Transform? candidateWalk = candidate;
            Transform? scopeWalk = scopeData;
            while (candidateWalk != null && scopeWalk != null)
            {
                if (!string.Equals(candidateWalk.name, scopeWalk.name, System.StringComparison.Ordinal))
                    break;

                score++;
                candidateWalk = candidateWalk.parent;
                scopeWalk = scopeWalk.parent;
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        return best;
    }

    private static void ReleaseHeld()
    {
        if (_held == null)
            return;

        try
        {
            OpticSight.OpticSightStatus state = OpticSight.OpticSightState.Value;
            if (Same(state.OpticSight, _held))
            {
                OpticSight.OpticSightState.Value = new OpticSight.OpticSightStatus
                {
                    IsEnabled = false,
                    OpticSight = _held
                };
            }
        }
        catch
        {
        }

        _held = null;
    }

    private static OpticSight? Resolve(ProceduralWeaponAnimation animation)
    {
        Transform? bone = animation.CurrentScope?.Bone;
        if (bone == null)
            return null;

        ScopePrefabCache? cache = bone.GetComponentInParent<ScopePrefabCache>(true);
        if (cache != null && cache.CurrentModHasOptics)
            return cache.CurrentModOpticSight;

        return bone.GetComponentInParent<OpticSight>(true);
    }

    private static bool Same(Object? left, Object? right)
    {
        if (left == null || right == null)
            return left == right;

        return left.GetInstanceID() == right.GetInstanceID();
    }
}
