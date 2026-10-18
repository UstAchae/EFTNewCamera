using System.Collections.Generic;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using EFT.Ballistics;
using EFT.CameraControl;
using EFT.InventoryLogic;
using UnityEngine;
using UnityEngine.Rendering;

namespace EFTNewCamera;

// Diagnostic only: never changes shot state or weapon transforms.
internal sealed class TrajectoryPreview : MonoBehaviour
{
    private ConfigEntry<float> _range = null!;
    private readonly Vector3[] _points = new Vector3[2002];
    private readonly RaycastHit[] _hits = new RaycastHit[64];
    private int _count;
    private Vector3 _start, _axis;
    private Material? _material;
    private Mesh? _mesh;
    private readonly List<Vector3> _vertices = new(4002);
    private readonly List<int> _indices = new(4002);
    private readonly List<Color32> _colors = new(4002);
    private readonly Dictionary<int, (Camera Camera, CommandBuffer Buffer)> _buffers = new();
    private int _sampleFrame = -1;
    private const float Step = 0.01f;
    private ConfigEntry<float> _rayLength = null!;
    private static int _opticRenderedFrame = -1;

    internal void Initialize(ConfigFile config)
    {
        _range = config.Bind("弹道调试", "预测最大距离", 1000f,
            new ConfigDescription("从枪口计算的最大路径长度，单位米；最长模拟20秒。", new AcceptableValueRange<float>(50f, 2000f)));
        _rayLength = config.Bind("弹道调试", "瞄准线显示长度（米）", 50000f,
            new ConfigDescription("用足够长的线模拟无限射线，实际可见范围仍受相机远裁剪限制；不改变交点计算的最大距离。", new AcceptableValueRange<float>(100f, 100000f)));
        Camera.onPreRender += Draw;
        OpticTest.Changed += ClearTestLines;
    }

    private void BeforeCamera(Camera camera)
    {
        if (!OpticTest.Enabled) { _count = 0; _sampleFrame = -1; return; }
        if (!camera.CompareTag("MainCamera") && !OpticPicture.IsCamera(camera)) return;
        // Both cameras use this frame's sample; never reuse a previous frame's trajectory.
        if (_sampleFrame == Time.frameCount) return;
        _sampleFrame = Time.frameCount;
        _count = 0;
        Player? player = Singleton<GameWorld>.Instance?.MainPlayer;
        if (player == null || !(player.HandsController is Player.FirearmController firearm)
            || firearm.CurrentFireport?.Original == null) return;
        AmmoTemplate? ammo = firearm.Weapon.CurrentAmmoTemplate;
        if (ammo == null || ammo.BulletMassGram <= 0f || ammo.BulletDiameterMilimeters <= 0f || ammo.BallisticCoeficient <= 0f) return;
        _axis = firearm.WeaponDirection.normalized;
        if (_axis.sqrMagnitude < 0.5f) return;
        _start = firearm.CurrentFireport.Original.position;
        AimArms.ResolvePreviewShot(firearm, ref _start, ref _axis);
        float speed = ammo.InitialSpeed * firearm.Weapon.SpeedFactor;
        float mass = ammo.BulletMassGram / 1000f;
        float diameter = ammo.BulletDiameterMilimeters / 1000f;
        float coefficient = mass * 0.0014223f / (diameter * diameter * ammo.BallisticCoeficient);
        float area = 1.2f * diameter * diameter * Mathf.PI / 4f;
        Vector3 position = _start;
        Vector3 velocity = _axis * speed;
        float distance = 0f;
        _points[_count++] = position;
        Transform? stationary = player.MovementContext?.StationaryWeapon?.transform;
        for (int i = 0; i < 2000; i++)
        {
            float v = velocity.magnitude;
            if (v < 0.01f) break;
            float drag = Shot.CalculateG1DragCoefficient(v) * coefficient;
            Vector3 acceleration = Physics.gravity - area * drag * v * v / (2f * mass) * velocity.normalized;
            Vector3 next = position + velocity * Step + acceleration * (0.5f * Step * Step);
            Vector3 segment = next - position;
            float length = segment.magnitude;
            if (length < 0.00001f) break;
            bool limit = distance + length >= _range.Value;
            length = Mathf.Min(length, _range.Value - distance);
            next = position + segment.normalized * length;
            int count = Physics.RaycastNonAlloc(position, segment.normalized, _hits, length,
                BallisticsCalculatorConstants.HitMask, QueryTriggerInteraction.Ignore);
            float nearest = float.MaxValue;
            for (int h = 0; h < count; h++)
            {
                RaycastHit hit = _hits[h];
                if (hit.collider == null || hit.distance >= nearest) continue;
                Transform owner = hit.collider.transform;
                if (owner.GetComponentInParent<Player>() == player || (stationary != null && owner.IsChildOf(stationary))) continue;
                nearest = hit.distance;
                next = hit.point;
            }
            distance += Vector3.Distance(position, next);
            _points[_count++] = next;
            position = next;
            if (nearest < float.MaxValue) break;
            if (limit) break;
            velocity += acceleration * Step;
        }
        OpticDiagnostics.Trace("trajectory.world", camera, $"origin={_start.ToString("F6")} direction={_axis.ToString("F6")} end={_points[_count - 1].ToString("F6")}");
        BallisticProbe.Trajectory(camera, firearm.CurrentFireport.Original.position, _start, _axis, _points, _count);

    }

    private void Draw(Camera camera)
    {
        if (!OpticPicture.IsCamera(camera)) return;
        _opticRenderedFrame = Time.frameCount;
        // The optic camera draws in world space. Main-view lines are projected to the final GUI below.
        if (_buffers.TryGetValue(camera.GetInstanceID(), out var previous)) previous.Buffer.Clear();
        BeforeCamera(camera);
        if (!OpticTest.Enabled || _count < 2) return;
        if (!camera.CompareTag("MainCamera") && !OpticPicture.IsCamera(camera)) return;
        if (_material == null)
        {
            Shader shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null) return;
            _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            _material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            _material.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            _material.SetInt("_Cull", (int)CullMode.Off);
            _material.SetInt("_ZWrite", 0);
            _material.SetInt("_ZTest", (int)CompareFunction.Always);
        }
        if (_mesh == null)
            _mesh = new Mesh { name = "Predicted ballistic path", hideFlags = HideFlags.HideAndDontSave };
        PopulateLines(camera);
        for (int i = 0; i < _vertices.Count; i++) _indices.Add(i);
        _mesh.Clear();
        _mesh.SetVertices(_vertices);
        _mesh.SetColors(_colors);
        _mesh.SetIndices(_indices, MeshTopology.Lines, 0, false);
        _material.SetColor("_Color", Color.white);
        int id = camera.GetInstanceID();
        if (!_buffers.TryGetValue(id, out var entry))
        {
            var buffer = new CommandBuffer { name = "Yellow predicted trajectory (world space)" };
            camera.AddCommandBuffer(CameraEvent.BeforeImageEffects, buffer);
            entry = (camera, buffer);
            _buffers[id] = entry;
        }
        entry.Buffer.Clear();
        // Render into the scene target BEFORE post-processing, with the same scene projection.
        // Never use the reticle-only projection or a post-render projection reset by image effects.
        entry.Buffer.SetViewProjectionMatrices(camera.worldToCameraMatrix, camera.projectionMatrix);
        entry.Buffer.DrawMesh(_mesh, Matrix4x4.identity, _material, 0, 0);
    }

    private void PopulateLines(Camera camera)
    {
        _vertices.Clear();
        _indices.Clear();
        _colors.Clear();
        for (int i = 1; i < _count; i++)
        {
            _vertices.Add(_points[i - 1]);
            _vertices.Add(_points[i]);
        }
        for (int i = 0; i < _vertices.Count; i++)
        {
            _colors.Add(new Color32(255, 255, 0, 255));
        }
        Vector3 end = _points[_count - 1];
        float size = Mathf.Clamp(Vector3.Distance(camera.transform.position, end) * 0.0015f, 0.03f, 1.5f);
        _vertices.Add(end - camera.transform.right * size);
        _vertices.Add(end + camera.transform.right * size);
        _vertices.Add(end - camera.transform.up * size);
        _vertices.Add(end + camera.transform.up * size);
        for (int i = 0; i < 4; i++) _colors.Add(new Color32(255, 0, 0, 255));
        if (EquivalentZeroRange.DebugActive) AppendSightDebug(camera);
    }

    private void Line(Vector3 a, Vector3 b, Color32 color)
    {
        _vertices.Add(a); _vertices.Add(b);
        _colors.Add(color); _colors.Add(color);
    }

    private void Marker(Camera camera, Vector3 point, Color32 color)
    {
        float size = Mathf.Max(0.001f, Vector3.Distance(camera.transform.position, point)
            * Mathf.Tan(camera.fieldOfView * Mathf.Deg2Rad * 0.5f) * 12f / Mathf.Max(1, camera.pixelHeight));
        Line(point - camera.transform.right * size, point + camera.transform.right * size, color);
        Line(point - camera.transform.up * size, point + camera.transform.up * size, color);
    }

    private void AppendSightDebug(Camera camera)
    {
        var purple = new Color32(220, 100, 255, 255);
        var green = new Color32(70, 255, 70, 255);
        Vector3 origin = EquivalentZeroRange.DebugOrigin, direction = EquivalentZeroRange.DebugDirection;
        Line(origin, origin + direction * _rayLength.Value, purple);
        Marker(camera, origin, purple);
        Marker(camera, EquivalentZeroRange.DebugAimPoint, purple);
        // Continue the theoretical path past scene collision so distant intersections can be inspected.
        // Dashes distinguish this from the solid collision-limited yellow prediction and red impact.
        var points = EquivalentZeroRange.DebugTrajectory;
        for (int i = 1; i < points.Count; i++)
            if ((i / 3) % 2 == 0) Line(points[i - 1], points[i], new Color32(255,255,0,190));
        foreach (Vector3 hit in EquivalentZeroRange.DebugCrossings) Marker(camera, hit, green);
    }

    private void OnGUI()
    {
        if (Event.current.type != EventType.Repaint || !OpticTest.Enabled) return;
        Camera camera = Camera.main;
        if (camera == null) return;
        // Sample the final, visible skeleton pose; never reuse the optic camera's earlier sample.
        _sampleFrame = -1;
        BeforeCamera(camera);
        if (_count < 2) return;
        PopulateLines(camera);
        bool hasLens = TryLensRect(camera, out Rect lensRect);
        bool suppressMain = !hasLens && _opticRenderedFrame >= Time.frameCount-1
            && Singleton<GameWorld>.Instance?.MainPlayer?.HandsController?.IsAiming == true;
        BallisticProbe.Overlay(camera, hasLens, lensRect, suppressMain);
        // If the lens cannot be projected reliably, retain the optic rendering and suppress
        // this overlay rather than leaking an unmasked second copy through the scope.
        if (suppressMain) return;
        Color saved = GUI.color;
        Matrix4x4 matrix = GUI.matrix;
        try
        {
            for (int i = 0; i + 1 < _vertices.Count; i += 2)
            {
                Vector3 a = _vertices[i], b = _vertices[i + 1];
                float da = camera.WorldToViewportPoint(a).z;
                float db = camera.WorldToViewportPoint(b).z;
                float near = camera.nearClipPlane + 0.001f;
                if (da < near && db < near) continue;
                if (da < near) a = Vector3.LerpUnclamped(a, b, (near - da) / (db - da));
                else if (db < near) b = Vector3.LerpUnclamped(b, a, (near - db) / (da - db));
                Vector3 sa = camera.WorldToScreenPoint(a), sb = camera.WorldToScreenPoint(b);
                Vector2 p = new Vector2(sa.x, Screen.height - sa.y);
                Vector2 q = new Vector2(sb.x, Screen.height - sb.y);
                if (!ClipScreen(ref p, ref q)) continue;
                GUI.color = _colors[i];
                if (hasLens)
                {
                    int count = ScreenLineMath.OutsideRect(p, q, lensRect, out var a0, out var b0, out var a1, out var b1);
                    if (count > 0) DrawScreenLine(a0, b0, matrix);
                    if (count > 1) DrawScreenLine(a1, b1, matrix);
                }
                else DrawScreenLine(p, q, matrix);
            }
        }
        finally { GUI.color = saved; GUI.matrix = matrix; }
    }

    private static void DrawScreenLine(Vector2 a, Vector2 b, Matrix4x4 matrix)
    {
        Vector2 delta = b-a;
        if (delta.sqrMagnitude < 0.01f) return;
        GUIUtility.RotateAroundPivot(Mathf.Atan2(delta.y, delta.x)*Mathf.Rad2Deg, a);
        GUI.DrawTexture(new Rect(a.x, a.y-0.75f, delta.magnitude, 1.5f), Texture2D.whiteTexture);
        GUI.matrix = matrix;
    }

    private static bool TryLensRect(Camera camera, out Rect rect)
    {
        rect = default;
        var manager = CameraManager.Instance?.OpticCameraManager;
        Renderer? lens = manager?.CurrentOpticSight?.LensRenderer;
        // Optics may be rendered manually while Camera.enabled is false.
        if (manager?.Camera == null || _opticRenderedFrame < Time.frameCount-1 || lens == null
            || !lens.gameObject.activeInHierarchy) return false;
        Mesh? mesh = lens.GetComponent<MeshFilter>()?.sharedMesh;
        Bounds bounds = mesh != null ? mesh.bounds : new Bounds();
        Matrix4x4 matrix = lens.localToWorldMatrix;
        float scale = 1f;
        Vector3 shift = Vector3.zero;
        // The old lens shader expands/shifts the aperture independently of Renderer.bounds.
        // Match that aperture geometry when determining the screen exclusion region.
        foreach (Material material in lens.sharedMaterials)
            if (material != null && material.shader != null && material.shader.name == "CW FX/OpticSight"
                && material.HasProperty("_Scales") && material.HasProperty("_Shifts") && material.HasProperty("_ShiftDirection"))
            {
                scale = material.GetVector("_Scales").y;
                Vector4 axis = material.GetVector("_ShiftDirection");
                shift = new Vector3(axis.x, axis.y, axis.z)*material.GetVector("_Shifts").y;
                Vector4 x = matrix.GetColumn(0), y = matrix.GetColumn(1);
                float ratio = y.magnitude > 0.000001f ? x.magnitude/y.magnitude : 1f;
                matrix.SetColumn(1, new Vector4(y.x, y.z, y.y, 0f)*ratio);
                break;
            }
        float minX = float.PositiveInfinity, minY = minX, maxX = float.NegativeInfinity, maxY = maxX;
        for (int i = 0; i < 8; i++)
        {
            Vector3 corner = bounds.center + Vector3.Scale(bounds.extents,
                new Vector3((i&1)==0 ? -1f:1f, (i&2)==0 ? -1f:1f, (i&4)==0 ? -1f:1f));
            Vector3 world = mesh != null ? matrix.MultiplyPoint3x4(corner*scale+shift)
                : lens.bounds.center + Vector3.Scale(lens.bounds.extents, new Vector3((i&1)==0 ? -1f:1f, (i&2)==0 ? -1f:1f, (i&4)==0 ? -1f:1f));
            Vector3 screen = camera.WorldToScreenPoint(world);
            if (screen.z <= camera.nearClipPlane) continue;
            minX = Mathf.Min(minX, screen.x); maxX = Mathf.Max(maxX, screen.x);
            minY = Mathf.Min(minY, Screen.height-screen.y); maxY = Mathf.Max(maxY, Screen.height-screen.y);
        }
        if (float.IsInfinity(minX) || maxX <= minX || maxY <= minY) return false;
        // Conservative rectangular exclusion also covers the frame; avoids leaking a second line at the edge.
        rect = Rect.MinMaxRect(minX-3f,minY-3f,maxX+3f,maxY+3f);
        return true;
    }

    private static bool ClipScreen(ref Vector2 a, ref Vector2 b)
    {
        Vector2 d = b - a;
        float enter = 0f, leave = 1f;
        if (!ClipEdge(-d.x, a.x, ref enter, ref leave) || !ClipEdge(d.x, Screen.width - a.x, ref enter, ref leave)
            || !ClipEdge(-d.y, a.y, ref enter, ref leave) || !ClipEdge(d.y, Screen.height - a.y, ref enter, ref leave)) return false;
        Vector2 start = a;
        a = start + enter * d;
        b = start + leave * d;
        return true;
    }

    private static bool ClipEdge(float p, float q, ref float enter, ref float leave)
    {
        if (Mathf.Abs(p) < 0.0000001f) return q >= 0f;
        float t = q / p;
        if (p < 0f) { if (t > leave) return false; enter = Mathf.Max(enter, t); }
        else { if (t < enter) return false; leave = Mathf.Min(leave, t); }
        return true;
    }

    private void ClearTestLines()
    {
        _count = 0; _sampleFrame = -1;
        foreach (var entry in _buffers.Values) entry.Buffer.Clear();
    }

    private void Update()
    {
        // Clear queued lines immediately when disabled or when the player no longer holds a firearm.
        var player = Singleton<GameWorld>.Instance?.MainPlayer;
        if (!OpticTest.Enabled || !(player?.HandsController is Player.FirearmController))
            foreach (var entry in _buffers.Values) entry.Buffer.Clear();
    }

    private void OnDestroy()
    {
        Camera.onPreRender -= Draw;
        OpticTest.Changed -= ClearTestLines;
        foreach (var entry in _buffers.Values)
        {
            if (entry.Camera != null) entry.Camera.RemoveCommandBuffer(CameraEvent.BeforeImageEffects, entry.Buffer);
            entry.Buffer.Dispose();
        }
        _buffers.Clear();
        if (_mesh != null) Destroy(_mesh);
        if (_material != null) Destroy(_material);
    }
}
