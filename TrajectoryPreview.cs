using System.Collections.Generic;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using EFT.Ballistics;
using EFT.InventoryLogic;
using UnityEngine;
using UnityEngine.Rendering;

namespace EFTNewCamera;

// Diagnostic only: never changes shot state or weapon transforms.
internal sealed class TrajectoryPreview : MonoBehaviour
{
    private ConfigEntry<bool> _enabled = null!;
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

    internal void Initialize(ConfigFile config)
    {
        _enabled = config.Bind("弹道调试", "显示预测弹道", false,
            "黄色：重力与G1阻力预测弹道；红色十字：预测终点。遇到首次碰撞停止，不预测散布、穿透或跳弹。独立于骨架镜头总开关，方便比较。");
        _range = config.Bind("弹道调试", "预测最大距离", 1000f,
            new ConfigDescription("从枪口计算的最大路径长度，单位米；最长模拟20秒。", new AcceptableValueRange<float>(50f, 2000f)));
        Camera.onPreRender += Draw;
    }

    private void BeforeCamera(Camera camera)
    {
        if (_enabled == null || !_enabled.Value) { _count = 0; _sampleFrame = -1; return; }
        if (camera != Camera.main && !OpticPicture.IsCamera(camera)) return;
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
        _start = firearm.CurrentFireport.position;
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

    }

    private void Draw(Camera camera)
    {
        // Sample after the skeleton/camera render preparation has finished.
        if (_buffers.TryGetValue(camera.GetInstanceID(), out var previous)) previous.Buffer.Clear();
        BeforeCamera(camera);
        if (_enabled == null || !_enabled.Value || _count < 2) return;
        if (camera != Camera.main && !OpticPicture.IsCamera(camera)) return;
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

    private void Update()
    {
        // Clear queued lines immediately when disabled or when the player no longer holds a firearm.
        var player = Singleton<GameWorld>.Instance?.MainPlayer;
        if (_enabled == null || !_enabled.Value || !(player?.HandsController is Player.FirearmController))
            foreach (var entry in _buffers.Values) entry.Buffer.Clear();
    }

    private void OnDestroy()
    {
        Camera.onPreRender -= Draw;
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
