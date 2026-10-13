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

    private static StreamWriter? _sightWriter;
    private static int _sightLines;
    private static bool _wasAiming;
    private static CollimatorSight[] _sights = new CollimatorSight[0];

    internal static void RecordCollimators(Player player, Vector3 cameraPosition, Quaternion cameraRotation)
    {
        bool aiming = player.HandsController != null && player.HandsController.IsAiming;
        bool started = aiming && !_wasAiming;
        _wasAiming = aiming;
        if (!aiming || _sightLines >= MaxLines)
            return;

        if (_sightWriter == null)
        {
            string path = Path.Combine(BepInEx.Paths.BepInExRootPath, "EFTNewCamera_collimator.txt");
            _sightWriter = new StreamWriter(path, false, Encoding.UTF8) { AutoFlush = true };
        }

        CultureInfo c = CultureInfo.InvariantCulture;
        if (started)
        {
            _sights = Object.FindObjectsOfType<CollimatorSight>(true);
            _sightWriter.WriteLine($"# aim start frame {Time.frameCount}, CollimatorSight count {_sights.Length}");
            foreach (CollimatorSight s in _sights)
                DumpSight(s, c);
            foreach (MeshRenderer r in Object.FindObjectsOfType<MeshRenderer>())
            {
                foreach (Material m in r.sharedMaterials)
                {
                    if (m != null && m.shader != null && m.shader.name.Contains("Collimator"))
                        _sightWriter.WriteLine($"#   renderer with collimator shader: {PathOf(r.transform)} enabled={r.enabled} active={r.gameObject.activeInHierarchy} layer={r.gameObject.layer} mat={m.name}#{m.GetInstanceID()}");
                }
            }
            foreach (Camera cam in Camera.allCameras)
                _sightWriter.WriteLine($"#   camera {cam.name} enabled={cam.enabled} depth={cam.depth} mask={cam.cullingMask:X8} target={(cam.targetTexture != null ? cam.targetTexture.name : "screen")} main={(cam == Camera.main)}");
            _sightWriter.WriteLine("frame,time,dt,index,visible,posX,posY,dist,axisX,axisY,fwdX,fwdY,localRotX,localRotY,localRotZ");
            _sightLines += 2;
        }

        Quaternion toCamera = Quaternion.Inverse(cameraRotation);
        for (int i = 0; i < _sights.Length; i++)
        {
            CollimatorSight s = _sights[i];
            if (s == null || !s.isActiveAndEnabled || s.CollimatorMeshRenderer == null || !s.CollimatorMeshRenderer.enabled)
                continue;
            Transform t = s.transform;
            Vector3 offset = toCamera * (t.position - cameraPosition);
            ScreenAngles(offset, out float posX, out float posY);
            ScreenAngles(toCamera * -t.up, out float axisX, out float axisY);
            ScreenAngles(toCamera * t.forward, out float fwdX, out float fwdY);
            Vector3 local = t.localRotation.eulerAngles;
            _sightWriter.WriteLine(string.Join(",",
                Time.frameCount.ToString(c),
                Time.time.ToString("F4", c),
                Time.deltaTime.ToString("F4", c),
                i.ToString(c),
                s.CollimatorMeshRenderer.isVisible ? "1" : "0",
                posX.ToString("F4", c), posY.ToString("F4", c),
                offset.magnitude.ToString("F4", c),
                axisX.ToString("F4", c), axisY.ToString("F4", c),
                fwdX.ToString("F4", c), fwdY.ToString("F4", c),
                Wrap(local.x).ToString("F4", c), Wrap(local.y).ToString("F4", c), Wrap(local.z).ToString("F4", c)));
            _sightLines++;
        }
    }

    private static void DumpSight(CollimatorSight s, CultureInfo c)
    {
        MeshRenderer r = s.CollimatorMeshRenderer;
        MeshFilter f = s.GetComponent<MeshFilter>();
        Material? shared = r != null ? r.sharedMaterial : null;
        Material? m = s.CollimatorMaterial;
        _sightWriter!.WriteLine($"#   sight {PathOf(s.transform)} active={s.isActiveAndEnabled} rendererEnabled={(r != null && r.enabled)} layer={s.gameObject.layer} "
            + $"mesh={(f != null && f.sharedMesh != null ? f.sharedMesh.name + " verts=" + f.sharedMesh.vertexCount : "none")} "
            + $"materials={(r != null ? r.sharedMaterials.Length : 0)} sharedMat={(shared != null ? shared.name + "#" + shared.GetInstanceID() : "none")} "
            + $"collimatorMat={(m != null ? m.name + "#" + m.GetInstanceID() : "none")} lossyScale={s.transform.lossyScale.ToString("F4")}");
        if (m == null || m.shader == null)
            return;
        Shader sh = m.shader;
        _sightWriter.WriteLine($"#     shader {sh.name} passes={m.passCount} queue={m.renderQueue} keywords=[{string.Join(" ", m.shaderKeywords)}]");
        for (int p = 0; p < sh.GetPropertyCount(); p++)
        {
            string name = sh.GetPropertyName(p);
            string value = sh.GetPropertyType(p) switch
            {
                UnityEngine.Rendering.ShaderPropertyType.Color => m.GetColor(name).ToString("F4"),
                UnityEngine.Rendering.ShaderPropertyType.Vector => m.GetVector(name).ToString("F4"),
                UnityEngine.Rendering.ShaderPropertyType.Float or UnityEngine.Rendering.ShaderPropertyType.Range => m.GetFloat(name).ToString("F4", c),
                UnityEngine.Rendering.ShaderPropertyType.Texture => TextureText(m, name),
                _ => "?",
            };
            _sightWriter.WriteLine($"#     {name} ({sh.GetPropertyType(p)}) = {value}");
        }
    }

    private static string TextureText(Material m, string name)
    {
        Texture t = m.GetTexture(name);
        string text = t != null ? $"{t.name} {t.width}x{t.height} filter={t.filterMode} wrap={t.wrapMode} mips={t.mipmapCount}" : "none";
        return text + $" scale={m.GetTextureScale(name).ToString("F4")} offset={m.GetTextureOffset(name).ToString("F4")}";
    }

    private static string PathOf(Transform t)
    {
        string path = t.name;
        for (Transform p = t.parent; p != null; p = p.parent)
            path = p.name + "/" + path;
        return path;
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
