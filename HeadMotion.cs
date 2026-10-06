using EFT;
using UnityEngine;

namespace EFTNewCamera;

internal static class HeadMotion
{
    internal static float SprintHorizontalSway = 0.5f;

    private const float SmoothTime = 0.22f;

    private static Transform? _head;
    private static Quaternion _baseLocal;
    private static Vector3 _basePosition;
    private static Quaternion _lastWrittenLocal;
    private static Vector3 _lastWrittenPosition;
    private static bool _applied;
    private static bool _hasBaseline;
    private static float _baselineYaw;
    private static float _yawVelocity;
    private static Vector2 _baselinePlanar;
    private static Vector2 _planarVelocity;

    internal static void Apply(Player player)
    {
        if (!IsSprinting(player))
        {
            Restore();
            return;
        }

        Transform? head = AnimatedHead(player);
        if (head == null || head.parent == null)
            return;

        Quaternion animatedLocal = head.localRotation;
        Vector3 animatedPosition = head.position;
        if (_applied && _head == head)
        {
            if (Quaternion.Angle(head.localRotation, _lastWrittenLocal) <= 0.25f)
                animatedLocal = _baseLocal;
            if ((head.position - _lastWrittenPosition).sqrMagnitude <= 0.0000000001f)
                animatedPosition = _basePosition;
        }

        HoldHorizontal(player, head, animatedLocal, animatedPosition, out Quaternion posedLocal, out Vector3 posedPosition);
        Quaternion posedWorld = head.parent.rotation * posedLocal;
        if (TryPitch(player, out float pitch, out Vector3 right))
            posedWorld = Quaternion.AngleAxis(pitch, right) * posedWorld;

        head.localRotation = Quaternion.Inverse(head.parent.rotation) * posedWorld;
        head.position = posedPosition;
        _head = head;
        _baseLocal = animatedLocal;
        _basePosition = animatedPosition;
        _lastWrittenLocal = head.localRotation;
        _lastWrittenPosition = head.position;
        _applied = true;
    }

    internal static void Restore()
    {
        if (!_applied || _head == null)
            return;

        if (Quaternion.Angle(_head.localRotation, _lastWrittenLocal) <= 0.25f)
            _head.localRotation = _baseLocal;
        if ((_head.position - _lastWrittenPosition).sqrMagnitude <= 0.0000000001f)
            _head.position = _basePosition;

        _applied = false;
        _hasBaseline = false;
        _head = null;
    }

    private static void HoldHorizontal(
        Player player,
        Transform head,
        Quaternion animatedLocal,
        Vector3 animatedPosition,
        out Quaternion posedLocal,
        out Vector3 posedPosition)
    {
        Transform body = ((Component)player).transform;
        Quaternion animatedWorld = head.parent.rotation * animatedLocal;
        Vector3 headFlat = Vector3.ProjectOnPlane(animatedWorld * Vector3.forward, Vector3.up);
        Vector3 bodyFlat = Vector3.ProjectOnPlane(body.forward, Vector3.up);
        float delta = 0f;
        if (headFlat.sqrMagnitude > 0.000001f && bodyFlat.sqrMagnitude > 0.000001f)
            delta = Vector3.SignedAngle(bodyFlat, headFlat, Vector3.up);

        Vector3 local = body.InverseTransformPoint(animatedPosition);
        if (!_hasBaseline)
        {
            _hasBaseline = true;
            _baselineYaw = delta;
            _yawVelocity = 0f;
            _baselinePlanar = new Vector2(local.x, local.z);
            _planarVelocity = Vector2.zero;
        }

        float strength = Mathf.Clamp01(SprintHorizontalSway);
        _baselineYaw = Mathf.SmoothDampAngle(_baselineYaw, delta, ref _yawVelocity, SmoothTime);
        float yaw = Mathf.Lerp(_baselineYaw, delta, strength);
        Quaternion stableWorld = Quaternion.AngleAxis(yaw - delta, Vector3.up) * animatedWorld;
        posedLocal = Quaternion.Inverse(head.parent.rotation) * stableWorld;

        float velocityX = _planarVelocity.x;
        float velocityZ = _planarVelocity.y;
        float baselineX = Mathf.SmoothDamp(_baselinePlanar.x, local.x, ref velocityX, SmoothTime);
        float baselineZ = Mathf.SmoothDamp(_baselinePlanar.y, local.z, ref velocityZ, SmoothTime);
        _baselinePlanar = new Vector2(baselineX, baselineZ);
        _planarVelocity = new Vector2(velocityX, velocityZ);
        float x = Mathf.Lerp(baselineX, local.x, strength);
        float z = Mathf.Lerp(baselineZ, local.z, strength);
        posedPosition = body.TransformPoint(new Vector3(x, local.y, z));
    }

    private static bool TryPitch(Player player, out float pitch, out Vector3 right)
    {
        pitch = 0f;
        right = Vector3.right;
        try
        {
            if (player.MovementContext == null || !player.MovementContext.IsSprintEnabled)
                return false;

            pitch = player.MovementContext.Pitch;
            if (Mathf.Abs(pitch) < 0.01f)
                return false;

            right = Quaternion.Euler(0f, player.MovementContext.Yaw, 0f) * Vector3.right;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSprinting(Player player)
    {
        if (!NewCameraSwitch.Enabled || !player.IsYourPlayer)
            return false;

        try
        {
            return player.MovementContext != null && player.MovementContext.IsSprintEnabled;
        }
        catch
        {
            return false;
        }
    }

    private static Transform? AnimatedHead(Player player)
    {
        Transform? head = player.PlayerBones?.Head?.Original;
        Transform? current = head;
        while (current != null)
        {
            if (current.name.Equals("Base HumanHead", System.StringComparison.OrdinalIgnoreCase))
                return current;

            current = current.parent;
        }

        return head;
    }
}
