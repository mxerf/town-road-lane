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

        // Tuned proportions. Width matches the visual weight of vanilla edge-line strokes.
        // Height: 0.01m is enough to clear road-surface z-fighting in most cases — the
        // ribbon mesh is still opaque geometry (not a true HDRP projected decal), so a
        // tiny lift is needed. Lower → more z-fight risk; higher → visible floating.
        private const float kMarkingWidth   = 0.15f;
        private const float kHeightOffset   = 0.01f;

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
                    BuildRibbonMesh(mesh, src.position, src.tangent, dst.position, dst.tangent);
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
                // Decal path attempted, abandoned: HDRP decal materials (shader
                // 'BH/Decals/DefaultDecalShader') don't render via Graphics.DrawMesh on
                // ordinary mesh geometry. They're authored for the DecalProjector path —
                // need a cube-volume bounding mesh + UNITY_VT atlas registration + the
                // engine's deferred decal pass. Verified empirically: borrowed
                // 'EU_RoadArrow_*' material loaded cleanly (renderQueue=2000, 2 passes,
                // keywords incl. ENABLE_VT) but produced zero pixels on our ribbon mesh.
                // Future: try DecalProjector (Unity HDRP MonoBehaviour) as a separate
                // experiment — different API entirely. For now, HDRP/Unlit works and is
                // good enough for the killer feature (segmentation) we want to build.

                // Proven path. See AssetImportPipeline.cs:189-200,
                // OverlayRenderSystem.cs:797-808, ManagedBatchSystem.cs:1388-1392 for the
                // canonical vanilla pattern. The critical undocumented call is
                // HDMaterial.ValidateMaterial — without it HDRP shader keywords aren't
                // derived from properties and every render pass is a silent no-op.
                var shader = Shader.Find("HDRP/Unlit");
                if (shader == null) { log.Warn("MarkingMeshRenderSystem: HDRP/Unlit shader not found"); return null; }

                _material = new Material(shader) { name = "TRL_PairPoC_Unlit" };
                _material.color = Color.white;
                _material.SetFloat("_SurfaceType", 0f);           // 0 = Opaque
                _material.SetFloat("_DoubleSidedEnable", 1f);     // both sides visible
                _material.SetFloat("_CullMode", 0f);              // CullMode.Off

                // Borrow vanilla road-line texture from our EdgeLineClone prefab. The
                // RenderPrefab returned by NetLaneMeshInfo.m_Mesh contains the same
                // 'LaneLine_*' material that vanilla edge-lines use; we want only its
                // mainTexture (the painted-line bitmap). We do NOT use the whole material
                // because it's BH/Decals/CurvedDecalShader, which is decal-pipeline-only
                // and won't render through DrawMesh (see decal-experiment fallout). The
                // texture itself, however, is a normal Texture2D — HDRP/Unlit will sample
                // it via _BaseColorMap / _UnlitColorMap / mainTexture exactly like any
                // texture asset.
                var tex = TryBorrowLaneLineTexture();
                if (tex != null)
                {
                    // HDRP/Unlit exposes the bitmap as _UnlitColorMap; legacy alias _MainTex
                    // also works because shader.mainTexture proxies to it.
                    if (_material.HasProperty("_UnlitColorMap")) _material.SetTexture("_UnlitColorMap", tex);
                    if (_material.HasProperty("_MainTex"))       _material.SetTexture("_MainTex",       tex);
                    _material.mainTexture = tex;
                    log.Info($"MarkingMeshRenderSystem: applied borrowed texture '{tex.name}' ({tex.width}x{tex.height})");
                }
                else
                {
                    log.Info("MarkingMeshRenderSystem: no vanilla LaneLine texture found — staying solid white");
                }

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

        /// <summary>Extract the painted-line bitmap from our edge-line clone prefab's
        /// material. We don't reuse the whole material (its shader is decal-pipeline-only),
        /// but the texture itself is a plain Texture2D and renders fine through HDRP/Unlit.
        /// Returns null if the clone hasn't baked yet or the material has no texture slot —
        /// caller falls back to solid white.</summary>
        private Texture TryBorrowLaneLineTexture()
        {
            try
            {
                var prefab = PickClonePrefab();
                if (prefab == null) return null;
                if (!(prefab is NetLaneGeometryPrefab geom)) return null;
                if (geom.m_Meshes == null || geom.m_Meshes.Length == 0) return null;
                var meshInfo = geom.m_Meshes[0];
                if (meshInfo?.m_Mesh == null) return null;
                var vanillaMat = meshInfo.m_Mesh.ObtainMaterial(0, useVT: false);
                if (vanillaMat == null) return null;
                // Try common texture property names in priority order. CurvedDecalShader
                // exposes its bitmap via _BaseColorMap on the HDRP surface inputs.
                Texture t = null;
                if (vanillaMat.HasProperty("_BaseColorMap")) t = vanillaMat.GetTexture("_BaseColorMap");
                if (t == null && vanillaMat.HasProperty("_MainTex")) t = vanillaMat.GetTexture("_MainTex");
                if (t == null) t = vanillaMat.mainTexture;
                return t;
            }
            catch (System.Exception ex)
            {
                log.Error(ex, "MarkingMeshRenderSystem: TryBorrowLaneLineTexture threw");
                return null;
            }
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

        // Bezier ribbon tuning. kSegments = N quads along the curve. 24 makes the arc
        // visually smooth even on tight turns; GPU cost is negligible (50 verts, 48 tris
        // per pair vs 26/24 at 12 segs — a current-gen GPU doesn't notice the difference
        // for hundreds of pairs). CPU cost is in the per-frame rebuild — see roadmap step 5
        // for the "diff only changed pairs" optimisation.
        // kPullFactor matches MarkingOverlaySystem so the drag-preview and the rendered
        // marking trace the same Bezier — what the user sees on hover is exactly what
        // they get.
        // kTextureTileMeters — how often the texture repeats along the curve. Vanilla
        // road-line bitmaps look right when tiled roughly every metre. Too small → texture
        // squished; too large → texture stretched into a smear.
        private const int   kSegments         = 24;
        private const float kPullFactor       = 0.4f;
        private const float kTextureTileMeters = 1.0f;

        /// <summary>Build a ribbon (strip of N quads) following the Bezier arc between two
        /// endpoints. Each endpoint contributes a tangent direction so the curve leaves the
        /// dot perpendicular to its parent edge (same control-point construction as the
        /// overlay drag-preview, see <see cref="MarkingOverlaySystem.BuildSmoothCurve"/>).
        /// Vertices are positioned in world space; the ribbon is widened along the per-segment
        /// horizontal-right vector (perpendicular to tangent in the XZ plane) and lifted by
        /// kHeightOffset.</summary>
        private static void BuildRibbonMesh(Mesh mesh, float3 a, float2 aTan, float3 b, float2 bTan)
        {
            float chord = math.distance(a, b);
            if (chord < 0.01f) { mesh.Clear(); return; }
            float pull = chord * kPullFactor;
            // MarkingEndpoint.tangent points OUTWARD from the node into the parent edge.
            // For the curve to leave each dot toward the intersection interior we negate.
            float3 aT = new float3(-aTan.x, 0f, -aTan.y);
            float3 bT = new float3(-bTan.x, 0f, -bTan.y);
            float3 p0 = a;
            float3 p1 = a + aT * pull;
            float3 p2 = b + bT * pull;
            float3 p3 = b;

            float halfW = kMarkingWidth * 0.5f;
            float3 lift = new float3(0f, kHeightOffset, 0f);

            int vCount = (kSegments + 1) * 2;
            int tCount = kSegments * 6;
            var verts = new Vector3[vCount];
            var uvs   = new Vector2[vCount];
            var tris  = new int[tCount];

            // Sample Bezier at N+1 evenly spaced t values; lay down left+right vertices.
            // Track running arc-length (sum of segment chords) so the texture v-coord can
            // be tiled in metres, not normalised — keeps the marking pattern at consistent
            // scale regardless of arc length.
            float3 prevPos = p0;
            float arcLen = 0f;
            for (int i = 0; i <= kSegments; i++)
            {
                float t = (float)i / kSegments;
                float3 pos = CubicBezier(p0, p1, p2, p3, t);
                if (i > 0) arcLen += math.distance(prevPos, pos);
                prevPos = pos;
                float3 tan = CubicBezierTangent(p0, p1, p2, p3, t);
                // Horizontal-right vector: perpendicular to tangent in XZ plane. Ignore Y
                // component of tangent for the cross — keeps the ribbon flat-relative-to-ground.
                float3 tanFlat = new float3(tan.x, 0f, tan.z);
                float3 right = math.normalizesafe(math.cross(new float3(0f, 1f, 0f), tanFlat));
                Vector3 lv = (Vector3)(pos - right * halfW + lift);
                Vector3 rv = (Vector3)(pos + right * halfW + lift);
                verts[i * 2 + 0] = lv;
                verts[i * 2 + 1] = rv;
                float v = arcLen / kTextureTileMeters;
                uvs[i * 2 + 0]   = new Vector2(0f, v);
                uvs[i * 2 + 1]   = new Vector2(1f, v);
            }
            // Stitch: per segment two triangles connecting consecutive (L,R) pairs.
            for (int i = 0; i < kSegments; i++)
            {
                int baseV = i * 2;
                int o = i * 6;
                tris[o + 0] = baseV + 0;
                tris[o + 1] = baseV + 1;
                tris[o + 2] = baseV + 3;
                tris[o + 3] = baseV + 0;
                tris[o + 4] = baseV + 3;
                tris[o + 5] = baseV + 2;
            }

            mesh.Clear();
            mesh.vertices = verts;
            mesh.triangles = tris;
            mesh.uv = uvs;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            // HDRP Forward+ frustum culling can drop meshes whose AABB has zero extent on
            // any axis. The ribbon lives in a thin band (all verts at the same Y + lift),
            // so bounds.size.y is tiny. Expand vertically so the culler keeps it.
            var meshBounds = mesh.bounds;
            meshBounds.Expand(new Vector3(0f, 1f, 0f));
            mesh.bounds = meshBounds;
        }

        private static float3 CubicBezier(float3 p0, float3 p1, float3 p2, float3 p3, float t)
        {
            float u = 1f - t;
            return u * u * u * p0 + 3f * u * u * t * p1 + 3f * u * t * t * p2 + t * t * t * p3;
        }

        private static float3 CubicBezierTangent(float3 p0, float3 p1, float3 p2, float3 p3, float t)
        {
            float u = 1f - t;
            return 3f * u * u * (p1 - p0) + 6f * u * t * (p2 - p1) + 3f * t * t * (p3 - p2);
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
