using System.Collections.Generic;
using EFT;
using UnityEngine;

namespace EFTNewCamera;

// Tritium vials seen through the objective: each a small, steady light a hand's breadth or two from the lens, which the
// wide-open lens focused far away spreads into an evenly lit disc, as wide as its aperture over the vial's distance
// from it, its edge as clear as the aperture's: a rear sight's two vials are two large overlapping discs, the front
// sight's a smaller one further off. Each disc is as bright as any other: the lens takes in less of a vial's light
// the further it is (as the square of the distance) and spreads it over a disc smaller by as much. Drawn into the
// frame the lenses' picture is made from, after the objective's blur, before the game's or BRNVG's filter: each
// vial's occlusion over its disc into a tile of an atlas (pass 8 of NvgObjective.shader, the gun in hand's parts
// traced, NightVisionTrace.cs), then each disc over a square of its own (pass 9).
// The vials are found in the model, not listed by item: every sight the game makes with tritium has its vials as a
// submesh of their own whose material is named for it (TruGlo TFX, Dead Ringer, AKMP's "tritium_yellow"...) and is
// emissive; read back from the GPU, that submesh's triangles fall into one piece per vial.
internal static partial class NightVisionEyepieces
{
    internal static float TritiumBrightness = 1f;
    private const float Aperture = 0.0225f;          // the objective's, 27 mm at f/1.2
    private const float VialGlow = 0.25f;            // a vial's disc, in the frame's light
    private const int MaxVials = 16;                 // the atlas's tiles
    private const int OcclusionTile = 64;            // an atlas tile's side (texels)
    private const float VialRange = 15f;             // vials further than this from the lens are not drawn (m)
    private const float MinRadius = 1.5f;            // the least disc drawn (frame pixels)
    private const float RootRescan = 5f, PruneAfter = 60f; // seconds

    private sealed class Vial
    {
        internal Vector3 Centre, Normal;             // in its mesh's frame; Normal short where the vial shows all round
        internal Color Tint;
    }

    // A renderer's vial submeshes, whose vials are looked up as their meshes have been read (VialsByMesh).
    private sealed class VialSet
    {
        internal Renderer Renderer = null!;
        internal (int mesh, int submesh)[] Submeshes = System.Array.Empty<(int, int)>();
        internal bool InHand;
    }

    private struct VialSeen
    {
        internal float Distance;
        internal Vector3 At;
        internal Vector4 Shape, Tint;
        internal bool Traced;
    }

    private static readonly Dictionary<(int mesh, int submesh), Vial[]?> VialsByMesh = new();   // null while being read
    private static readonly List<VialSet> _vialSets = new();
    private static readonly HashSet<Renderer> _vialRenderers = new();
    private static readonly Dictionary<int, (float searched, float seen, VialSet[] sets)> VialsByRoot = new();
    private static float _tracePartsTime = -100f, _pruneTime;
    private static readonly List<int> _pruned = new();
    private static readonly List<VialSeen> _vialsSeen = new();
    private static readonly System.Comparison<VialSeen> ByDistance = (a, b) => a.Distance.CompareTo(b.Distance);
    private static readonly float[] _vialTrace = new float[MaxVials];
    private static readonly Vector4[] _vialShape = new Vector4[MaxVials], _vialTint = new Vector4[MaxVials], _vialMasks = new Vector4[MaxVials];
    private static readonly List<Renderer> _rendererScratch = new();
    private static readonly List<Material> _materialScratch = new();
    private static readonly List<VialSet> _setScratch = new();
    private static readonly List<(int, int)> _submeshScratch = new();
    private static RenderTexture? _occlusion;
    private static Transform? _handWeapon;
    private static int _vialWeapon;
    private static int _vialSearchFrame = -100;
    private static readonly int VialsId = Shader.PropertyToID("_Vials");
    private static readonly int VialTintId = Shader.PropertyToID("_VialTint");
    private static readonly int VialMasksId = Shader.PropertyToID("_VialMasks");
    private static readonly int VialCountId = Shader.PropertyToID("_VialCount");
    private static readonly int TargetId = Shader.PropertyToID("_Target");
    private static readonly int VialTraceId = Shader.PropertyToID("_VialTrace");
    private static readonly int OcclusionId = Shader.PropertyToID("_Occlusion");

    // TEMP DIAG: frame times, with how many discs and how many pixels they cover, every 3 s.
    private static float _dgFrameTime, _dgFrameSum, _dgFrameMax, _dgPixels;
    private static int _dgFrames, _dgDiscs, _dgParts;
    private static void DiagFrames(int count, int parts)
    {
        float dt = Time.unscaledDeltaTime;
        _dgFrames++;
        _dgFrameSum += dt;
        _dgFrameMax = Mathf.Max(_dgFrameMax, dt);
        float pixels = 0f;
        for (int k = 0; k < count; k++)
            pixels += Mathf.PI * _vialShape[k].z * _vialShape[k].z;
        _dgPixels = Mathf.Max(_dgPixels, pixels);
        _dgDiscs = Mathf.Max(_dgDiscs, count);
        _dgParts = Mathf.Max(_dgParts, parts);
        if (Time.time - _dgFrameTime < 3f)
            return;
        NewCameraPlugin.Log?.LogInfo($"TEMP DIAG frames: {_dgFrames} frames, mean {1000f * _dgFrameSum / Mathf.Max(_dgFrames, 1):F1} ms, longest {1000f * _dgFrameMax:F1} ms; up to {_dgDiscs} discs over {_dgPixels:F0} pixels, {_dgParts} parts traced");
        _dgFrameTime = Time.time;
        _dgFrameSum = _dgFrameMax = _dgPixels = 0f;
        _dgFrames = _dgDiscs = _dgParts = 0;
    }

    // Every vial near the lens as a disc over the frame, as seen from view: the gun in hand's, and those of guns and
    // sights lying about or in others' hands, which glow as much (the nearest MaxVials of them).
    private static void AddTritium(Camera view, RenderTexture frame)
    {
        if (_objective == null || _body == null || TritiumBrightness <= 0f)
        {
            DiagFrames(0, 0); // TEMP DIAG
            return;
        }
        Vector3 lens = _body.TransformPoint(_objectiveFront);
        FindVials(lens);

        float tanUp = Mathf.Tan(view.fieldOfView * 0.5f * Mathf.Deg2Rad);
        float pixelsPerRadian = frame.height / (2f * tanUp);
        _vialsSeen.Clear();
        var reach = new Bounds(lens, Vector3.one * 0.06f);  // what the light of the gun in hand's vials could meet
        foreach (VialSet set in _vialSets)
        {
            if (set.Renderer == null || !set.Renderer.enabled || !set.Renderer.gameObject.activeInHierarchy)
                continue;
            Matrix4x4 toWorld = set.Renderer.localToWorldMatrix;
            foreach (var key in set.Submeshes)
            {
                if (!VialsByMesh.TryGetValue(key, out Vial[]? vials) || vials == null)
                    continue;
                foreach (Vial vial in vials)
                {
                    Vector3 at = toWorld.MultiplyPoint3x4(vial.Centre);
                    Vector3 toLens = lens - at;
                    float distance = toLens.magnitude;
                    if (distance < 0.02f || distance > VialRange)
                        continue;
                    // A vial glows out of its open end, toward whoever looks along the sight.
                    float facing = vial.Normal.sqrMagnitude < 0.09f ? 1f
                        : Mathf.Clamp01(Vector3.Dot(toWorld.MultiplyVector(vial.Normal).normalized, toLens / distance) * 3f);
                    // Where the camera saw it as it rendered.
                    Vector4 clip = _renderedViewProjection * new Vector4(at.x, at.y, at.z, 1f);
                    float depth = -_renderedView.MultiplyPoint3x4(at).z;
                    if (facing <= 0f || clip.w <= 0f || depth <= 0f)
                        continue;
                    Vector2 uv = new Vector2(clip.x / clip.w * 0.5f + 0.5f, clip.y / clip.w * 0.5f + 0.5f);
                    if (uv.x < -0.1f || uv.x > 1.1f || uv.y < -0.1f || uv.y > 1.1f)
                        continue;
                    float radius = 0.5f * Aperture / distance * pixelsPerRadian;
                    // A disc smaller than the least drawn keeps its light, not its brightness.
                    float glow = VialGlow * TritiumBrightness * facing;
                    if (radius < MinRadius)
                    {
                        glow *= radius * radius / (MinRadius * MinRadius);
                        radius = MinRadius;
                    }
                    _vialsSeen.Add(new VialSeen
                    {
                        Distance = distance, At = at, Traced = set.InHand,
                        Shape = new Vector4(uv.x, uv.y, radius, glow),
                        Tint = new Vector4(vial.Tint.r, vial.Tint.g, vial.Tint.b, depth),
                    });
                    if (set.InHand)
                        reach.Encapsulate(at);
                }
            }
        }
        if (_vialsSeen.Count == 0)
        {
            DiagFrames(0, 0); // TEMP DIAG
            return;
        }
        _vialsSeen.Sort(ByDistance);
        int count = Mathf.Min(_vialsSeen.Count, MaxVials);
        int parts = PrepareTrace(reach);
        for (int k = 0; k < count; k++)
        {
            VialSeen seen = _vialsSeen[k];
            _vialShape[k] = seen.Shape;
            _vialTint[k] = seen.Tint;
            _vialTrace[k] = seen.Traced ? 1f : 0f;
            // The parts along this vial's light, from it to the objective.
            uint mask = 0;
            if (seen.Traced)
            {
                var along = new Bounds(seen.At, Vector3.zero);
                along.Encapsulate(lens);
                along.Expand(2f * Aperture);
                mask = TracePartsAlong(along, parts);
            }
            _vialMasks[k] = new Vector4(mask & 0xFFFF, mask >> 16, 0f, 0f);
        }
        DiagFrames(count, parts); // TEMP DIAG

        _objective.SetFloatArray(VialTraceId, _vialTrace);
        _objective.SetVectorArray(VialsId, _vialShape);
        _objective.SetVectorArray(VialTintId, _vialTint);
        _objective.SetVectorArray(VialMasksId, _vialMasks);
        _objective.SetFloat(VialCountId, count);
        _objective.SetVector(TargetId, new Vector4(frame.width, frame.height, 0.5f * Aperture, 0f));
        if (_occlusion == null)
        {
            _occlusion = new RenderTexture(4 * OcclusionTile, 4 * OcclusionTile, 0, RenderTextureFormat.RHalf)
            { name = "Justachae NVG tritium occlusion", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            _occlusion.Create();
        }
        Graphics.Blit(Texture2D.blackTexture, _occlusion, _objective, 8);

        // Each disc over a square three of its radii out each way (its glow fades within that).
        _objective.SetTexture(OcclusionId, _occlusion);
        RenderTexture previous = RenderTexture.active;
        Graphics.SetRenderTarget(frame);
        GL.PushMatrix();
        GL.LoadPixelMatrix(0f, frame.width, 0f, frame.height);
        _objective.SetPass(9);
        GL.Begin(GL.QUADS);
        for (int k = 0; k < count; k++)
        {
            Vector4 v = _vialShape[k];
            float r = Mathf.Max(v.z, 1f);
            float x = v.x * frame.width, y = v.y * frame.height;
            Corner(x, y, r, -3f, -3f, k);
            Corner(x, y, r, 3f, -3f, k);
            Corner(x, y, r, 3f, 3f, k);
            Corner(x, y, r, -3f, 3f, k);
        }
        GL.End();
        GL.PopMatrix();
        RenderTexture.active = previous;
    }

    private static void Corner(float x, float y, float r, float across, float up, int vial)
    {
        GL.MultiTexCoord3(0, across, up, vial);
        GL.Vertex3(x + across * r, y + up * r, 0f);
    }

    // The renderers that carry vials near the lens, looked over once a second (and as soon as the gun in hand changes):
    // the gun in hand's; what lies about, from the game's own list of loot (GameWorld.LootItems, kept by the game as
    // things are dropped, spawned and picked up), within range; and the guns others hold, within range. Each thing is
    // searched for vials once and kept while it is about; guns in hands, whose parts can change, again every few
    // seconds.
    private static void FindVials(Vector3 around)
    {
        GameWorld? world = Comfort.Common.Singleton<GameWorld>.Instance;
        Player? player = world?.MainPlayer;
        Transform? weapon = player?.ProceduralWeaponAnimation?.HandsContainer?.Weapon;
        int id = weapon != null ? weapon.GetInstanceID() : 0;
        if (id == _vialWeapon && Time.frameCount - _vialSearchFrame < 60)
            return;
        bool newWeapon = id != _vialWeapon;
        _vialWeapon = id;
        _vialSearchFrame = Time.frameCount;
        _vialSets.Clear();
        _vialRenderers.Clear();
        _handWeapon = weapon;
        if (newWeapon || Time.time - _tracePartsTime > RootRescan)
        {
            FindTraceParts(weapon);
            _tracePartsTime = Time.time;
        }
        if (weapon != null)
            AddVialsOf(weapon, RootRescan, true);
        float range2 = VialRange * VialRange;
        var loot = world?.LootItems;
        if (loot != null)
            for (int i = 0; i < loot.Count; i++)
            {
                EFT.Interactive.LootItem item = loot.GetByIndex(i);
                if (item != null && (item.transform.position - around).sqrMagnitude < range2)
                    AddVialsOf(item.transform, float.PositiveInfinity, false);
            }
        if (world?.AllAlivePlayersList != null)
            foreach (Player other in world.AllAlivePlayersList)
            {
                if (other == null || other == player || (other.Position - around).sqrMagnitude >= range2)
                    continue;
                Transform? held = other.ProceduralWeaponAnimation?.HandsContainer?.Weapon;
                if (held != null)
                    AddVialsOf(held, RootRescan, false);
            }
        if (Time.time - _pruneTime > PruneAfter)
        {
            PruneVials();
            _pruneTime = Time.time;
        }
    }

    // A thing's vials, searched for when first met, or again when last searched longer ago than rescan.
    private static void AddVialsOf(Transform root, float rescan, bool inHand)
    {
        int id = root.GetInstanceID();
        if (!VialsByRoot.TryGetValue(id, out var known) || Time.time - known.searched > rescan)
            known = (Time.time, Time.time, VialSetsIn(root, inHand));
        VialsByRoot[id] = (known.searched, Time.time, known.sets);
        foreach (VialSet set in known.sets)
            if (set.Renderer != null && _vialRenderers.Add(set.Renderer))
                _vialSets.Add(set);
    }

    // What has not been near for a while is let go.
    private static void PruneVials()
    {
        _pruned.Clear();
        foreach (var entry in VialsByRoot)
            if (Time.time - entry.Value.seen > PruneAfter)
                _pruned.Add(entry.Key);
        foreach (int key in _pruned)
            VialsByRoot.Remove(key);
    }

    // A thing's renderers with vial submeshes; each such submesh's vials read from the GPU (in the background) the
    // first time its mesh is met.
    private static VialSet[] VialSetsIn(Transform root, bool inHand)
    {
        _setScratch.Clear();
        root.GetComponentsInChildren(true, _rendererScratch);
        foreach (Renderer renderer in _rendererScratch)
        {
            Mesh? mesh = renderer is SkinnedMeshRenderer skinned ? skinned.sharedMesh
                : renderer.TryGetComponent(out MeshFilter filter) ? filter.sharedMesh : null;
            if (mesh == null)
                continue;
            renderer.GetSharedMaterials(_materialScratch);
            _submeshScratch.Clear();
            for (int i = 0; i < _materialScratch.Count && i < mesh.subMeshCount; i++)
            {
                Material m = _materialScratch[i];
                if (m == null || m.name.IndexOf("tritium", System.StringComparison.OrdinalIgnoreCase) < 0
                    || m.shader == null || m.shader.name.IndexOf("Emissive", System.StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                var key = (mesh.GetInstanceID(), i);
                if (!VialsByMesh.ContainsKey(key))
                {
                    VialsByMesh[key] = null;
                    Color tint = TintOf(m.name);
                    ReadTrianglesAsync(mesh, i, true, (pos, normal) => VialsByMesh[key] = pos != null && normal != null ? Split(pos, normal, tint) : System.Array.Empty<Vial>());
                }
                _submeshScratch.Add(key);
            }
            if (_submeshScratch.Count > 0)
                _setScratch.Add(new VialSet { Renderer = renderer, Submeshes = _submeshScratch.ToArray(), InHand = inHand });
        }
        _rendererScratch.Clear();
        _materialScratch.Clear();
        return _setScratch.ToArray();
    }

    private static Color TintOf(string material)
    {
        string name = material.ToLowerInvariant();
        if (name.Contains("yellow") || name.Contains("orange"))
            return new Color(1f, 0.8f, 0.25f);
        if (name.Contains("red"))
            return new Color(1f, 0.3f, 0.2f);
        return new Color(0.35f, 1f, 0.4f);
    }

    // A vial submesh's triangles split into its vials: the triangles that share corners make one piece each.
    private static Vial[] Split(Vector3[] pos, Vector3[] normal, Color tint)
    {
        int triangles = pos.Length / 3;
        var parent = new int[triangles];
        for (int t = 0; t < triangles; t++)
            parent[t] = t;
        int Find(int t)
        {
            while (parent[t] != t)
                t = parent[t] = parent[parent[t]];
            return t;
        }
        // Corners at the same place (to a twentieth of a millimetre) join their triangles.
        var corner = new Dictionary<Vector3Int, int>();
        for (int k = 0; k < pos.Length; k++)
        {
            Vector3Int key = Vector3Int.RoundToInt(pos[k] * 20000f);
            int t = k / 3;
            if (corner.TryGetValue(key, out int other))
                parent[Find(t)] = Find(other);
            else
                corner[key] = t;
        }
        var pieces = new Dictionary<int, (Vector3 sum, Vector3 normal, int n)>();
        for (int k = 0; k < pos.Length; k++)
        {
            int root = Find(k / 3);
            pieces.TryGetValue(root, out var piece);
            pieces[root] = (piece.sum + pos[k], piece.normal + normal[k], piece.n + 1);
        }
        var vials = new List<Vial>();
        foreach (var piece in pieces.Values)
            vials.Add(new Vial { Centre = piece.sum / piece.n, Normal = piece.normal / piece.n, Tint = tint });
        return vials.ToArray();
    }
}
