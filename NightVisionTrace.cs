using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace EFTNewCamera;

// Whether the light of a vial on the gun in hand reaches each point of the objective, found by tracing rays against the
// gun's own triangles (pass 8 of NvgObjective.shader), not guessed from the camera's depth: the camera sits behind the
// objective and sees only the nearest surface, so a thin sight blade hides from it a whole wedge of space the light
// crosses well clear of the blade, a thick part shows only its face, and a part the game lets pass into the device
// is in front of the camera's view of the lens. Each part's mesh is read back from the GPU once, in the background
// (AsyncGPUReadback), and made into a bounding volume hierarchy in its own frame on a worker thread; each frame the
// parts the light could meet are given with their places, and each vial the ones along its own light.
internal static partial class NightVisionEyepieces
{
    [StructLayout(LayoutKind.Sequential)]
    private struct TraceNode                         // 32 bytes; Count > 0: a leaf of Count triangles from Left
    {
        public Vector3 Min;
        public int Left;
        public Vector3 Max;
        public int Count;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TraceTri                          // 36 bytes
    {
        public Vector3 A, B, C;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TraceInstance                     // 64 bytes: from the view's frame (x right, y up, z ahead) to the part's
    {
        public Vector4 Row0, Row1, Row2;
        public int Root, Pad0, Pad1, Pad2;
    }

    // A mesh's hierarchy as built on a worker: its nodes' Left counted from its own first node (inner) or triangle (leaf).
    private sealed class BuiltTrace
    {
        internal int Mesh;
        internal string Name = "";
        internal TraceNode[] Nodes = System.Array.Empty<TraceNode>();
        internal TraceTri[] Tris = System.Array.Empty<TraceTri>();
    }

    private const int MaxTraceInstances = 32;        // a vial's parts are a 32-bit mask
    private const int LeafSize = 4;
    private const int Pending = -2;

    private static readonly Dictionary<int, int> TraceRootByMesh = new();   // a mesh's root node; -1 none, Pending
    private static readonly List<TraceNode> _traceNodes = new();
    private static readonly List<TraceTri> _traceTris = new();
    private static readonly ConcurrentQueue<BuiltTrace> _builtTraces = new();
    private static ComputeBuffer? _nodeBuffer, _triBuffer, _instanceBuffer;
    private static bool _traceDirty = true;
    private static readonly TraceInstance[] _traceInstances = new TraceInstance[MaxTraceInstances];
    private static readonly Bounds[] _traceBounds = new Bounds[MaxTraceInstances];
    private static readonly List<(MeshRenderer part, Mesh mesh)> _traceParts = new();
    private static readonly List<MeshRenderer> _partScratch = new();
    private static readonly int TraceNodesId = Shader.PropertyToID("_TraceNodes");
    private static readonly int TraceTrisId = Shader.PropertyToID("_TraceTris");
    private static readonly int TraceInstancesId = Shader.PropertyToID("_TraceInstances");
    private static readonly int TraceCountId = Shader.PropertyToID("_TraceCount");

    // The gun's drawn parts (its LOD0, or what has no levels) and their meshes, looked for when the gun is.
    private static void FindTraceParts(Transform? weapon)
    {
        _traceParts.Clear();
        if (weapon == null)
            return;
        weapon.GetComponentsInChildren(true, _partScratch);
        foreach (MeshRenderer part in _partScratch)
        {
            string name = part.name;
            int lod = name.IndexOf("_LOD", System.StringComparison.OrdinalIgnoreCase);
            if (lod >= 0 && (lod + 4 >= name.Length || name[lod + 4] != '0'))
                continue;
            if (part.TryGetComponent(out MeshFilter filter) && filter.sharedMesh != null)
                _traceParts.Add((part, filter.sharedMesh));
        }
        _partScratch.Clear();
    }

    // The parts within reach of the light (reach, in the world), with their places from the view's frame, given to the
    // objective's material; any not yet read are sent to be read. Their count.
    private static int PrepareTrace(Bounds reach)
    {
        while (_builtTraces.TryDequeue(out BuiltTrace built))
            AddBuiltTrace(built);
        Matrix4x4 viewToWorld = _renderedView.inverse * Matrix4x4.Scale(new Vector3(1f, 1f, -1f));
        int count = 0;
        foreach (var (part, mesh) in _traceParts)
        {
            if (count >= MaxTraceInstances)
                break;
            if (part == null || !part.enabled || !part.gameObject.activeInHierarchy)
                continue;
            Bounds bounds = part.bounds;
            if (!bounds.Intersects(reach))
                continue;
            int id = mesh.GetInstanceID();
            if (!TraceRootByMesh.TryGetValue(id, out int root))
            {
                TraceRootByMesh[id] = Pending;
                ReadTrace(mesh);
                continue;
            }
            if (root < 0)
                continue;
            Matrix4x4 toPart = part.worldToLocalMatrix * viewToWorld;
            _traceBounds[count] = bounds;
            _traceInstances[count++] = new TraceInstance { Row0 = toPart.GetRow(0), Row1 = toPart.GetRow(1), Row2 = toPart.GetRow(2), Root = root };
        }
        UploadTrace(count);
        return count;
    }

    // Which of this frame's parts lie along a vial's light (along, in the world), as bits.
    private static uint TracePartsAlong(Bounds along, int count)
    {
        uint mask = 0;
        for (int k = 0; k < count; k++)
            if (_traceBounds[k].Intersects(along))
                mask |= 1u << k;
        return mask;
    }

    private static void UploadTrace(int count)
    {
        if (_traceDirty || _nodeBuffer == null || _triBuffer == null)
        {
            _nodeBuffer?.Release();
            _triBuffer?.Release();
            _nodeBuffer = new ComputeBuffer(Mathf.Max(_traceNodes.Count, 1), 32);
            _triBuffer = new ComputeBuffer(Mathf.Max(_traceTris.Count, 1), 36);
            if (_traceNodes.Count > 0)
                _nodeBuffer.SetData(_traceNodes);
            if (_traceTris.Count > 0)
                _triBuffer.SetData(_traceTris);
            _traceDirty = false;
        }
        _instanceBuffer ??= new ComputeBuffer(MaxTraceInstances, 64);
        _instanceBuffer.SetData(_traceInstances, 0, 0, Mathf.Max(count, 1));
        _objective!.SetBuffer(TraceNodesId, _nodeBuffer);
        _objective.SetBuffer(TraceTrisId, _triBuffer);
        _objective.SetBuffer(TraceInstancesId, _instanceBuffer);
        _objective.SetInt(TraceCountId, count);
    }

    // A mesh's triangles (all its submeshes) read back in the background, then made into its hierarchy on a worker.
    private static void ReadTrace(Mesh mesh)
    {
        int id = mesh.GetInstanceID(), left = mesh.subMeshCount;
        string name = mesh.name;
        var parts = new Vector3[mesh.subMeshCount][];
        for (int s = 0; s < mesh.subMeshCount; s++)
        {
            int submesh = s;
            ReadTrianglesAsync(mesh, s, false, (pos, _) =>
            {
                parts[submesh] = pos ?? System.Array.Empty<Vector3>();
                if (--left > 0)
                    return;
                System.Threading.Tasks.Task.Run(() => _builtTraces.Enqueue(BuildTrace(id, name, parts)));
            });
        }
        if (mesh.subMeshCount == 0)
            TraceRootByMesh[id] = -1;
    }

    private static void AddBuiltTrace(BuiltTrace built)
    {
        if (built.Tris.Length == 0)
        {
            TraceRootByMesh[built.Mesh] = -1;
            return;
        }
        int nodeBase = _traceNodes.Count, triBase = _traceTris.Count;
        foreach (TraceNode node in built.Nodes)
        {
            TraceNode moved = node;
            moved.Left += node.Count > 0 ? triBase : nodeBase;
            _traceNodes.Add(moved);
        }
        _traceTris.AddRange(built.Tris);
        TraceRootByMesh[built.Mesh] = nodeBase;
        _traceDirty = true;
    }

    // A mesh's triangles as a hierarchy of boxes, split at the middle of the longest side of their centres down to a
    // few triangles a leaf (no Unity calls: it runs on a worker thread).
    private static BuiltTrace BuildTrace(int mesh, string name, Vector3[][] parts)
    {
        var corners = new List<Vector3>();
        foreach (Vector3[] part in parts)
            corners.AddRange(part);
        int n = corners.Count / 3;
        var built = new BuiltTrace { Mesh = mesh, Name = name };
        if (n == 0)
            return built;
        var centre = new Vector3[n];
        for (int t = 0; t < n; t++)
            centre[t] = (corners[3 * t] + corners[3 * t + 1] + corners[3 * t + 2]) / 3f;
        var order = new int[n];
        for (int t = 0; t < n; t++)
            order[t] = t;
        var keys = new float[n];
        var nodes = new List<TraceNode> { default };
        Split(0, 0, n);
        var tris = new TraceTri[n];
        for (int i = 0; i < n; i++)
        {
            int t = order[i];
            tris[i] = new TraceTri { A = corners[3 * t], B = corners[3 * t + 1], C = corners[3 * t + 2] };
        }
        built.Nodes = nodes.ToArray();
        built.Tris = tris;
        return built;

        void Split(int node, int start, int end)
        {
            Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
            Vector3 cMin = Vector3.positiveInfinity, cMax = Vector3.negativeInfinity;
            for (int i = start; i < end; i++)
            {
                int t = order[i];
                for (int k = 0; k < 3; k++)
                {
                    min = Vector3.Min(min, corners[3 * t + k]);
                    max = Vector3.Max(max, corners[3 * t + k]);
                }
                cMin = Vector3.Min(cMin, centre[t]);
                cMax = Vector3.Max(cMax, centre[t]);
            }
            if (end - start <= LeafSize)
            {
                nodes[node] = new TraceNode { Min = min, Max = max, Left = start, Count = end - start };
                return;
            }
            Vector3 extent = cMax - cMin;
            int axis = extent.x >= extent.y && extent.x >= extent.z ? 0 : extent.y >= extent.z ? 1 : 2;
            for (int i = start; i < end; i++)
                keys[i] = centre[order[i]][axis];
            System.Array.Sort(keys, order, start, end - start);
            int middle = (start + end) / 2;
            int left = nodes.Count;
            nodes.Add(default);
            nodes.Add(default);
            nodes[node] = new TraceNode { Min = min, Max = max, Left = left, Count = 0 };
            Split(left, start, middle);
            Split(left + 1, middle, end);
        }
    }

    // A submesh's triangles, three vertices each, read back through the GPU without waiting for it (a mesh the game
    // loads is not readable on the CPU): MeshRead draws each vertex to its own pixel of four float targets, of which
    // the places (and the normals if asked) are read back; done gets them, or nulls, a few frames later.
    private static void ReadTrianglesAsync(Mesh mesh, int submesh, bool withNormals, System.Action<Vector3[]?, Vector3[]?> done)
    {
        if (!LoadShaders() || _meshReadShader == null || mesh.GetTopology(submesh) != MeshTopology.Triangles)
        {
            done(null, null);
            return;
        }
        int vertices = (int)mesh.GetIndexCount(submesh);
        const int width = 256;
        int height = Mathf.Max((vertices + width - 1) / width, 1);
        var material = new Material(_meshReadShader) { hideFlags = HideFlags.DontSave };
        material.SetFloat("_Width", width);
        material.SetFloat("_Height", height);
        var targets = new RenderTexture[4];
        var ids = new RenderTargetIdentifier[4];
        for (int i = 0; i < 4; i++)
        {
            targets[i] = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGBFloat);
            targets[i].filterMode = FilterMode.Point;
            ids[i] = targets[i];
        }
        var command = new CommandBuffer { name = "Justachae mesh read" };
        command.SetRenderTarget(ids, ids[0]);
        command.ClearRenderTarget(false, true, Color.clear);
        command.DrawMesh(mesh, Matrix4x4.identity, material, submesh, 0);
        Graphics.ExecuteCommandBuffer(command);
        command.Release();

        Vector4[]? places = null, normals = null;
        int left = withNormals ? 2 : 1;
        bool failed = false;
        void Finish()
        {
            if (--left > 0)
                return;
            foreach (RenderTexture target in targets)
                RenderTexture.ReleaseTemporary(target);
            Object.Destroy(material);
            if (failed || places == null || (withNormals && normals == null))
            {
                done(null, null);
                return;
            }
            var pos = new Vector3[vertices];
            Vector3[]? normal = withNormals ? new Vector3[vertices] : null;
            var got = new bool[vertices];
            for (int px = 0; px < places.Length; px++)
            {
                int k = Mathf.RoundToInt(places[px].w) - 1;
                if (k < 0 || k >= vertices)
                    continue;
                pos[k] = places[px];
                if (normal != null)
                    normal[k] = normals![px];
                got[k] = true;
            }
            if (System.Array.TrueForAll(got, g => g))
                done(pos, normal);
            else
                done(null, null);
        }
        AsyncGPUReadback.Request(targets[0], 0, TextureFormat.RGBAFloat, request =>
        {
            if (request.hasError) failed = true; else places = request.GetData<Vector4>().ToArray();
            Finish();
        });
        if (withNormals)
            AsyncGPUReadback.Request(targets[1], 0, TextureFormat.RGBAFloat, request =>
            {
                if (request.hasError) failed = true; else normals = request.GetData<Vector4>().ToArray();
                Finish();
            });
    }
}
