using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BepInEx.Configuration;
using EFT;
using EFT.InventoryLogic;
using UnityEngine;

namespace EFTNewCamera;

// The helmet tilted on the head, apart from the head: its brim lifted (never lowered) about the skull's middle, the camera's right
// axis, as the bones are posed each frame (after the head model's lift); what hangs from it (the night vision) goes
// with it. Set by hotkeys held (F12), kept for each helmet (the worn item, as each mount keeps its own: NightVisionMount)
// in its own file; a helmet never set starts from the old single setting (F12, hidden).
internal static class HeadwearTilt
{
    private const float Speed = 2.5f;         // degrees per second while a key is held
    internal const float Range = 5f;          // up only, degrees
    private const float SkullMiddle = 0.09f;  // the pivot above the head bone, along the camera's up (m)

    internal static float Default;               // a helmet never set (the old single setting)
    internal static float Degrees;               // the brim up, the worn helmet's
    internal static KeyboardShortcut Up = new KeyboardShortcut(KeyCode.Equals);
    internal static KeyboardShortcut Down = new KeyboardShortcut(KeyCode.Minus);

    private static Transform? _model;
    private static Vector3 _restPosition;
    private static Quaternion _restRotation;
    private static bool _turned;
    private static string? _helmetId;

    private static readonly string HelmetsPath = Path.Combine(BepInEx.Paths.ConfigPath, "justachae.thirdpersoncamera.helmets.txt");
    private static Dictionary<string, float>? _saved;   // by the helmet item's id
    private static readonly object WriteLock = new object();

    private static float SavedFor(string? id)
    {
        if (_saved == null)
        {
            _saved = new Dictionary<string, float>();
            try
            {
                if (File.Exists(HelmetsPath))
                {
                    foreach (string line in File.ReadAllLines(HelmetsPath))
                    {
                        string[] f = line.Split(' ');
                        if (f.Length == 2 && float.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float d))
                            _saved[f[0]] = d;
                    }
                }
            }
            catch (IOException e)
            {
                NewCameraPlugin.Log?.LogWarning("Helmet tilts not read: " + e.Message);
            }
        }
        return id != null && _saved.TryGetValue(id, out float degrees) ? degrees : Default;
    }

    private static void Save()
    {
        if (_helmetId == null || _saved == null)
            return;
        _saved[_helmetId] = Mathf.Round(Degrees * 10f) / 10f;
        var lines = new List<string>();
        foreach (KeyValuePair<string, float> e in _saved)
            lines.Add(string.Format(CultureInfo.InvariantCulture, "{0} {1:R}", e.Key, e.Value));
        // Written off the main thread, so a slow disk never holds up a frame; one write at a time, in order.
        string path = HelmetsPath;
        System.Threading.Tasks.Task.Run(() =>
        {
            lock (WriteLock)
            {
                try
                {
                    File.WriteAllLines(path, lines);
                }
                catch (IOException e)
                {
                    NewCameraPlugin.Log?.LogWarning("Helmet tilts not saved: " + e.Message);
                }
            }
        });
    }

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
                // This helmet's own tilt.
                Item? helmet = player.Inventory?.Equipment?.GetSlot(EquipmentSlot.Headwear).ContainedItem;
                _helmetId = helmet != null ? EquipmentIds.Kept(helmet, EquipmentSlot.Headwear) : null;
                Degrees = Mathf.Clamp(SavedFor(_helmetId), 0f, Range);
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
            Save();
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
