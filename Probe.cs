using System.Globalization;
using System.IO;
using System.Text;
using EFT;
using UnityEngine;

namespace EFTNewCamera;

internal static class Probe
{
    private const int MaxLines = 60000;
    private static StreamWriter? _writer;
    private static int _lines;
    private static float _window;

    internal static void Record(Player player, Vector3 cameraPosition, Quaternion cameraRotation, Vector3 rawMuzzle, Vector3 shownMuzzle,
        Quaternion smoothLook, Quaternion bodyLook, Quaternion lagLook, float followDelay,
        Quaternion rawSight, Quaternion shownSight, Quaternion turn)
    {
        if (_lines >= MaxLines)
            return;

        bool aiming = player.HandsController != null && player.HandsController.IsAiming;
        float mouseStep = Quaternion.Angle(bodyLook, smoothLook);
        if (mouseStep > 0.01f || aiming)
            _window = 1.5f;
        else if (_window > 0f)
            _window -= Time.deltaTime;
        if (_window <= 0f)
            return;

        if (_writer == null)
        {
            string path = Path.Combine(BepInEx.Paths.BepInExRootPath, "EFTNewCamera_probe.csv");
            _writer = new StreamWriter(path, false, Encoding.UTF8) { AutoFlush = true };
            _writer.WriteLine("frame,time,dt,aiming,delay,mouseYaw,mousePitch,bodyYaw,bodyPitch,lagYaw,lagPitch,rawX,rawY,shownX,shownY,camYaw,camPitch,"
                + "sightRawX,sightRawY,sightRawZ,sightShownX,sightShownY,sightShownZ,turnX,turnY,turnZ");
        }

        Quaternion toCamera = Quaternion.Inverse(cameraRotation);
        ScreenAngles(toCamera * (rawMuzzle - cameraPosition), out float rawX, out float rawY);
        ScreenAngles(toCamera * (shownMuzzle - cameraPosition), out float shownX, out float shownY);
        Vector3 mouse = smoothLook.eulerAngles;
        Vector3 body = bodyLook.eulerAngles;
        Vector3 lag = lagLook.eulerAngles;
        Vector3 cam = cameraRotation.eulerAngles;
        Vector3 sightRaw = (toCamera * rawSight).eulerAngles;
        Vector3 sightShown = (toCamera * shownSight).eulerAngles;
        Vector3 turnAngles = turn.eulerAngles;
        CultureInfo c = CultureInfo.InvariantCulture;
        _writer.WriteLine(string.Join(",",
            Time.frameCount.ToString(c),
            Time.time.ToString("F4", c),
            Time.deltaTime.ToString("F4", c),
            aiming ? "1" : "0",
            followDelay.ToString("F3", c),
            Wrap(mouse.y).ToString("F3", c), Wrap(mouse.x).ToString("F3", c),
            Wrap(body.y).ToString("F3", c), Wrap(body.x).ToString("F3", c),
            Wrap(lag.y).ToString("F3", c), Wrap(lag.x).ToString("F3", c),
            rawX.ToString("F3", c), rawY.ToString("F3", c),
            shownX.ToString("F3", c), shownY.ToString("F3", c),
            Wrap(cam.y).ToString("F3", c), Wrap(cam.x).ToString("F3", c),
            Wrap(sightRaw.x).ToString("F4", c), Wrap(sightRaw.y).ToString("F4", c), Wrap(sightRaw.z).ToString("F4", c),
            Wrap(sightShown.x).ToString("F4", c), Wrap(sightShown.y).ToString("F4", c), Wrap(sightShown.z).ToString("F4", c),
            Wrap(turnAngles.x).ToString("F4", c), Wrap(turnAngles.y).ToString("F4", c), Wrap(turnAngles.z).ToString("F4", c)));
        _lines++;
    }

    private static void ScreenAngles(Vector3 local, out float x, out float y)
    {
        x = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
        y = Mathf.Atan2(local.y, new Vector2(local.x, local.z).magnitude) * Mathf.Rad2Deg;
    }

    private static float Wrap(float angle)
    {
        return angle > 180f ? angle - 360f : angle;
    }
}
