using BepInEx.Configuration;
using EFT;
using EFT.InventoryLogic;
using UnityEngine;

namespace EFTNewCamera;

// The helmet tilted on the head, apart from the head: its brim lifted (never lowered) about the skull's middle, the camera's right
// axis, as the bones are posed each frame (after the head model's lift); what hangs from it (the night vision) goes
// with it. Set by hotkeys held (F12), kept in the config.
internal static class HeadwearTilt
{
    private const float Speed = 2.5f;         // degrees per second while a key is held
    internal const float Range = 5f;          // up only, degrees
    private const float SkullMiddle = 0.09f;  // the pivot above the head bone, along the camera's up (m)

    internal static ConfigEntry<float>? Pitch;   // where it is kept
    internal static float Degrees;               // the brim up
    internal static KeyboardShortcut Up = new KeyboardShortcut(KeyCode.Equals);
    internal static KeyboardShortcut Down = new KeyboardShortcut(KeyCode.Minus);

    private static Transform? _model;
    private static Vector3 _restPosition;
    private static Quaternion _restRotation;
    private static bool _turned;

    internal static void Apply(Player player, Transform head)
    {
        Transform? model = player.PlayerBody != null && player.PlayerBody.SlotViews.TryGetByKey(EquipmentSlot.Headwear, out PlayerBody.SlotView? view)
            && view?.Model != null ? view.Model.transform : null;
        if (model != _model)
        {
            Release();
            if (model != null)
            {
                _restPosition = model.localPosition;
                _restRotation = model.localRotation;
            }
            _model = model;
        }
        if (_model == null)
            return;

        // At the hip only: aiming, the same keys set the aim's distance.
        Adjust(player.ProceduralWeaponAnimation == null || !player.ProceduralWeaponAnimation.IsAiming);
        _model.SetLocalPositionAndRotation(_restPosition, _restRotation);
        if (Degrees < 0.01f)
            return;
        Quaternion camera = CameraDirection.Resolve(head.rotation);
        _model.RotateAround(head.position + camera * Vector3.up * SkullMiddle, camera * Vector3.right, -Degrees);
    }

    // Turned while a key is held; written to the config once let go.
    private static void Adjust(bool free)
    {
        int direction = free ? (NightVisionEyepieces.Held(Up) ? 1 : 0) - (NightVisionEyepieces.Held(Down) ? 1 : 0) : 0;
        if (direction != 0)
        {
            Degrees = Mathf.Clamp(Degrees + direction * Speed * Time.deltaTime, 0f, Range);
            _turned = true;
        }
        else if (_turned)
        {
            _turned = false;
            if (Pitch != null)
                Pitch.Value = Mathf.Round(Degrees * 10f) / 10f;
        }
    }

    // The helmet back where the game has it.
    internal static void Release()
    {
        if (_model != null)
            _model.SetLocalPositionAndRotation(_restPosition, _restRotation);
        _model = null;
    }
}
