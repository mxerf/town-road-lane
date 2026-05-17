using System.Collections.Generic;
using Colossal.Logging;
using Game;
using Game.City;
using Game.Prefabs;
using Game.Rendering;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace TownRoadLane
{
    /// <summary>
    /// Phase 4 step 3a (PoC): renders user-pair markings via cached managed Mesh + per-camera
    /// Graphics.DrawMesh. Replaces the ECS sublane-entity approach (MarkingPairEmissionSystem)
    /// which failed silently — vanilla rendering pipeline depends on PrefabSystem-baked batch
    /// registration that hand-created entities don't inherit. IMT (CS1 NodeMarkup) solved the
    /// equivalent CS1 problem the same way: own the mesh, own the draw call, skip the lane
    /// system entirely. Vanilla CS2 itself uses this pattern in BrushRenderSystem.cs (template
    /// for this system) plus 5+ others (Aggregate, Area, Route, NotificationIcon, Overlay).
    ///
    /// Lifecycle:
    ///   - Mesh per pairKey, lazily built on first sight, rebuilt only when pair geometry
    ///     changes (signalled by MarkingNodeToolSystem on toggle).
    ///   - GPU resources released via CoreUtils.Destroy on OnDestroy + on pair removal.
    ///   - Render runs from RenderPipelineManager.beginContextRendering — once per frame per
    ///     camera, no OnUpdate work needed.
    ///
    /// PoC scope: single flat quad (4 verts, 2 tris) per pair, width = kMarkingWidth, slightly
    /// elevated above road surface to avoid z-fight. Material borrowed from EdgeLineCloneSystem
    /// (its NetLaneMeshInfo.m_Mesh.ObtainMaterial(0, useVT:false)) — production-baked decal
    /// shader with correct render queue + decal layer, no shader work needed.
    /// </summary>
    public partial class MarkingMeshRenderSystem : GameSystemBase
    {
        private static readonly ILog log = Mod.log;

        // PoC values: deliberately exaggerated (1m wide, 0.5m above ground) so a visible bar
        // is easy to spot in-game. Production-tune to ~0.3m wide / ~0.02m once we confirm
        // the render pipeline works.
        private const float kMarkingWidth   = 1.0f;
        private const float kHeightOffset   = 0.5f;

        private EdgeLineCloneSystem _edgeLineSys;
        private CityConfigurationSystem _cityConfig;
        private PrefabSystem _prefabSystem;
        private EntityQuery _overlayConfigQuery;

        private Material _material;
        private bool _materialAttempted;
        private MaterialPropertyBlock _props;

        // Heartbeat: every N OnUpdate/Render call we log a status line so we can tell whether
        // the system is even being driven. Plain "not rendering" could mean OnUpdate isn't
        // firing (managed system without RequireForUpdate), or Render is firing but
        // _meshes.Count == 0, or cameras filter dropped all cameras.
        private int _updateTicks;
        private int _renderTicks;
        private int _drawsLastReport;

        // Live geometry source: MarkingPair buffers on node entities. Each tick we diff
        // (node, pairIndex, srcEdge, srcGap, dstEdge, dstGap) tuples against _meshes and
        // rebuild any that changed. Cheap because pair counts are tiny per node.
        private readonly Dictionary<PairKey, Mesh> _meshes = new();
        private EntityQuery _nodesWithPairs;

        protected override void OnCreate()
        {
            base.OnCreate();
            _edgeLineSys = World.GetOrCreateSystemManaged<EdgeLineCloneSystem>();
            _cityConfig = World.GetOrCreateSystemManaged<CityConfigurationSystem>();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _overlayConfigQuery = GetEntityQuery(ComponentType.ReadOnly<OverlayConfigurationData>());
            _props = new MaterialPropertyBlock();
            _nodesWithPairs = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<MarkingPair>(), ComponentType.ReadOnly<Game.Net.Node>() },
                None = new[] { ComponentType.ReadOnly<Game.Common.Deleted>(), ComponentType.ReadOnly<Game.Tools.Temp>() },
            });
            RenderPipelineManager.beginContextRendering += Render;
            log.Info("MarkingMeshRenderSystem: created, subscribed to beginContextRendering");
        }

        protected override void OnDestroy()
        {
            RenderPipelineManager.beginContextRendering -= Render;
            foreach (var m in _meshes.Values) SafeDestroy(m);
            _meshes.Clear();
            base.OnDestroy();
        }

        protected override void OnUpdate()
        {
            _updateTicks++;
            if (_updateTicks % 120 == 1)
                log.Info($"[mesh-render] OnUpdate tick={_updateTicks} nodesWithPairs={_nodesWithPairs.CalculateEntityCount()} meshCache={_meshes.Count} renderTicks={_renderTicks} drawsLastReport={_drawsLastReport}");

            // Per-tick reconcile: rebuild mesh cache from MarkingPair buffers. Small N (pairs
            // per node × nodes with pairs), runs main-thread because Mesh API requires it.
            // We rebuild every wanted pair every tick for the PoC — cheap and simpler than
            // diffing. Optimise later if it shows up in profiler.
            var nextKeys = new HashSet<PairKey>();
            var nodes = _nodesWithPairs.ToEntityArray(Unity.Collections.Allocator.Temp);
            for (int n = 0; n < nodes.Length; n++)
            {
                var node = nodes[n];
                if (!EntityManager.HasBuffer<MarkingPair>(node)) continue;
                var pairs = EntityManager.GetBuffer<MarkingPair>(node, isReadOnly: true);
                if (pairs.Length == 0) continue;
                var endpoints = MarkingEndpointExtractor.Extract(EntityManager, node);
                for (int i = 0; i < pairs.Length; i++)
                {
                    var p = pairs[i];
                    if (!TryFindEndpoint(endpoints, p.sourceEdge, p.sourceGapIndex, out var src)) continue;
                    if (!TryFindEndpoint(endpoints, p.targetEdge, p.targetGapIndex, out var dst)) continue;
                    var key = new PairKey(node, i);
                    nextKeys.Add(key);
                    if (!_meshes.TryGetValue(key, out var mesh))
                    {
                        mesh = new Mesh { name = $"TRL_Pair_{node.Index}_{i}" };
                        _meshes[key] = mesh;
                    }
                    BuildQuadMesh(mesh, src.position, dst.position);
                }
            }
            nodes.Dispose();

            // Sweep meshes whose pair was removed.
            if (_meshes.Count > nextKeys.Count)
            {
                var toRemove = new List<PairKey>();
                foreach (var kv in _meshes)
                    if (!nextKeys.Contains(kv.Key)) toRemove.Add(kv.Key);
                foreach (var k in toRemove)
                {
                    SafeDestroy(_meshes[k]);
                    _meshes.Remove(k);
                }
            }
        }

        private void Render(ScriptableRenderContext context, List<Camera> cameras)
        {
            _renderTicks++;
            int draws = 0;
            int eligibleCameras = 0;
            for (int c = 0; c < cameras.Count; c++)
                if (cameras[c].cameraType == CameraType.Game || cameras[c].cameraType == CameraType.SceneView)
                    eligibleCameras++;
            if (_renderTicks % 120 == 1)
                log.Info($"[mesh-render] Render tick={_renderTicks} cameras={cameras.Count} eligible={eligibleCameras} meshCache={_meshes.Count}");

            if (_meshes.Count == 0) { _drawsLastReport = 0; return; }
            var mat = GetMaterial();
            if (mat == null) { _drawsLastReport = 0; return; }
            // Heartbeat — confirm WHICH material we're actually using each frame. If this shows
            // a stale Unlit/HDRP material from a previous build (game wasn't fully restarted),
            // that explains "no visual but draws=N".
            if (_renderTicks % 120 == 1)
                log.Info($"[mesh-render] using material='{mat.name}' shader='{mat.shader?.name}' renderQueue={mat.renderQueue} passCount={mat.passCount}");
            // Diagnostic sample: bounds of first mesh in cache (helps spot mesh-frustum-cull issue).
            if (_renderTicks % 120 == 1)
            {
                foreach (var kv in _meshes)
                {
                    var b = kv.Value.bounds;
                    log.Info($"[mesh-render] mesh sample key=(#{kv.Key.node.Index},{kv.Key.index}) vertCount={kv.Value.vertexCount} bounds.center=({b.center.x:F1},{b.center.y:F1},{b.center.z:F1}) bounds.size=({b.size.x:F1},{b.size.y:F1},{b.size.z:F1})");
                    break;
                }
            }
            var matrix = Matrix4x4.identity; // mesh is in world-space already
            foreach (var kv in _meshes)
            {
                var mesh = kv.Value;
                if (mesh == null) continue;
                for (int c = 0; c < cameras.Count; c++)
                {
                    var cam = cameras[c];
                    if (cam.cameraType != CameraType.Game && cam.cameraType != CameraType.SceneView) continue;
                    Graphics.DrawMesh(mesh, matrix, mat, layer: 0, cam, submeshIndex: 0, _props,
                                      ShadowCastingMode.Off, receiveShadows: false);
                    draws++;
                }
            }
            _drawsLastReport = draws;
        }

        /// <summary>PoC-stage material acquisition. The vanilla edge-line material uses
        /// 'BH/Decals/CurvedDecalShader' which is a HDRP projected-decal shader requiring a
        /// cube bounding volume + per-instance shader uniforms (start/end positions, tangents,
        /// curve parameters). A simple flat quad rendered with it likely projects nothing —
        /// that's our suspected reason the previous PoC was invisible despite material being
        /// acquired correctly.
        ///
        /// To prove the rest of the pipeline (Mesh build, DrawMesh dispatch, camera filter,
        /// layer) works, we first try a plain opaque shader ('Universal Render Pipeline/Lit'
        /// fallback chain) on a flat quad. If THAT renders, we know the cause is the decal
        /// shader path and need to either (a) build a cube + drive curved-decal uniforms, or
        /// (b) keep an opaque-mesh approach with a y-offset (less pretty but works).</summary>
        private Material GetMaterial()
        {
            if (_material != null) return _material;
            if (_materialAttempted) return null;
            _materialAttempted = true;
            try
            {
                // FINAL approach — canonical vanilla CS2 pattern proven by decomp:
                //
                //   AssetImportPipeline.cs:189-200, OverlayRenderSystem.cs:797-808,
                //   ManagedBatchSystem.cs:1388-1392
                //
                // The critical missing call in all previous PoC attempts was
                // HDMaterial.ValidateMaterial(). HDRP's material system derives its shader
                // keywords (_SURFACE_TYPE_OPAQUE, _BLENDMODE_*, _DISABLE_DECALS, etc.),
                // render queue, and stencil state from material *properties* (_SurfaceType,
                // _BlendMode, _CullMode, _DoubleSidedEnable), but only computes them inside
                // ValidateMaterial. Without it, a fresh new Material(HDRP/Unlit) has NO
                // keywords enabled — every render pass is culled to a no-op, DrawMesh
                // dispatches silently to a zero-output shader variant. No errors, no
                // warnings, no pixels. Exactly our symptom for 4+ attempts.
                var shader = Shader.Find("HDRP/Unlit");
                if (shader == null) { log.Warn("MarkingMeshRenderSystem: HDRP/Unlit shader not found"); return null; }

                _material = new Material(shader) { name = "TRL_PairPoC_Unlit" };
                _material.color = Color.white;
                _material.SetFloat("_SurfaceType", 0f);           // 0 = Opaque
                _material.SetFloat("_DoubleSidedEnable", 1f);     // both sides visible (we don't know quad winding orientation relative to camera)
                _material.SetFloat("_CullMode", 0f);              // CullMode.Off → renders both sides
                HDMaterial.ValidateMaterial(_material);

                var kw = _material.enabledKeywords;
                string kwStr = "";
                for (int i = 0; i < kw.Length; i++) { if (i > 0) kwStr += ","; kwStr += kw[i].name; }
                log.Info($"MarkingMeshRenderSystem: built validated material '{_material.name}' shader='{_material.shader.name}' renderQueue={_material.renderQueue} passes={_material.passCount} keywords=[{kwStr}]");
                _materialAttempted = false;
                return _material;
            }
            catch (System.Exception ex)
            {
                log.Error(ex, "MarkingMeshRenderSystem: material acquisition threw");
            }
            return null;
        }

        private NetLanePrefab PickClonePrefab()
        {
            if (_edgeLineSys == null) return null;
            // Mirror MarkingPairEmissionSystem.PickPrefab theme logic, but managed-side.
            var theme = _cityConfig.defaultTheme;
            if (theme == Entity.Null) return _edgeLineSys.ClonePrefabEU;
            var prefabSys = World.GetOrCreateSystemManaged<PrefabSystem>();
            if (prefabSys.TryGetPrefab<PrefabBase>(theme, out var themePrefab) && themePrefab != null)
            {
                var n = themePrefab.name;
                if (!string.IsNullOrEmpty(n) && (n.Contains("North American") || n.StartsWith("NA "))) return _edgeLineSys.ClonePrefabNA;
            }
            return _edgeLineSys.ClonePrefabEU;
        }

        /// <summary>Flat axis-aligned quad of width kMarkingWidth between two world points,
        /// raised by kHeightOffset above the chord midpoint y. Two triangles, no UVs (decal
        /// shader uses world-space sampling per vanilla setup).</summary>
        private static void BuildQuadMesh(Mesh mesh, float3 a, float3 b)
        {
            float3 dir = b - a;
            float len = math.length(dir);
            if (len < 0.01f) { mesh.Clear(); return; }
            float3 fwd = dir / len;
            float3 right = math.normalizesafe(math.cross(new float3(0f, 1f, 0f), fwd));
            float halfW = kMarkingWidth * 0.5f;
            float3 lift = new float3(0f, kHeightOffset, 0f);

            var verts = new Vector3[4]
            {
                (Vector3)(a - right * halfW + lift),
                (Vector3)(a + right * halfW + lift),
                (Vector3)(b + right * halfW + lift),
                (Vector3)(b - right * halfW + lift),
            };
            var tris = new int[6] { 0, 1, 2, 0, 2, 3 };
            var uvs = new Vector2[4]
            {
                new Vector2(0f, 0f),
                new Vector2(1f, 0f),
                new Vector2(1f, len),
                new Vector2(0f, len),
            };
            mesh.Clear();
            mesh.vertices = verts;
            mesh.triangles = tris;
            mesh.uv = uvs;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            // HDRP Forward+ frustum culling can drop meshes whose AABB has zero extent on
            // any axis. Our flat quad has bounds.size.y == 0, since all 4 verts share Y.
            // Expand vertically by 1m so the culler keeps it. Vanilla avoids this by
            // multiplying with a TRS matrix that has non-unit Y scale; we pass identity.
            var meshBounds = mesh.bounds;
            meshBounds.Expand(new Vector3(0f, 1f, 0f));
            mesh.bounds = meshBounds;
        }

        private static bool TryFindEndpoint(List<MarkingEndpoint> endpoints, Entity edge, int gap, out MarkingEndpoint ep)
        {
            for (int i = 0; i < endpoints.Count; i++)
            {
                if (endpoints[i].edge == edge && endpoints[i].gapIndex == gap)
                {
                    ep = endpoints[i];
                    return true;
                }
            }
            ep = default;
            return false;
        }

        /// <summary>Destroy a managed Unity Object safely — Object.Destroy throws if called
        /// outside play mode; we're always in play here but the null-check costs nothing.</summary>
        private static void SafeDestroy(Object o)
        {
            if (o == null) return;
            Object.Destroy(o);
        }

        private readonly struct PairKey : System.IEquatable<PairKey>
        {
            public readonly Entity node;
            public readonly int index;
            public PairKey(Entity n, int i) { node = n; index = i; }
            public bool Equals(PairKey o) => node == o.node && index == o.index;
            public override bool Equals(object o) => o is PairKey k && Equals(k);
            public override int GetHashCode() => (node.Index * 397) ^ (int)((uint)node.Version << 8) ^ index;
        }
    }
}
