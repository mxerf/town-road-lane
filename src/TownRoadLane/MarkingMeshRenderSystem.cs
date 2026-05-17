using System;
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

        // Path B box geometry. Marking is extruded into a thin box volume — bottom
        // sits below road surface, top above. With DefaultDecalShader the box
        // becomes a projection volume; the shader paints the bitmap onto whatever
        // road surface lies inside. With HDRP/Unlit (Step 1 fallback) the box is
        // visible as a flat 3D bar — useful sanity check that the mesh is built
        // correctly before swapping the material.
        // kHeightOffset is the BOTTOM of the box relative to the curve y.
        // kBoxHeight is total vertical extent. The bottom dips below ground so that
        // on crowned/sloped roads the projection still catches the surface.
        private const float kMarkingWidth   = 0.3f;
        private const float kHeightOffset   = -0.5f;
        private const float kBoxHeight      =  1.5f;

        private EdgeLineCloneSystem _edgeLineSys;
        private CityConfigurationSystem _cityConfig;
        private PrefabSystem _prefabSystem;
        private EntityQuery _overlayConfigQuery;

        private Material _material;
        private bool _materialAttempted;
        private MaterialPropertyBlock _props;

        // Cached at material-build time alongside the borrowed texture: where inside
        // the LaneLine_BaseColor atlas our lane-line sprite lives. Sent per-draw via
        // colossal_TextureArea — full-atlas (0,0,1,1) gives mostly transparent
        // garbage outside the actual paint sprite.
        private Vector4 _textureArea = new Vector4(0f, 0f, 1f, 1f);

        // Per-draw shader uniform IDs — matches vanilla constants from
        // RenderPrefabRenderer.ShaderIDs (decomp lines 580-584) and
        // MaterialProperty.cs (TextureArea, MeshSize, LodDistanceFactor).
        // Cached statically so we don't string-hash on every frame.
        private static class ShaderIDs
        {
            public static readonly int _TextureArea       = Shader.PropertyToID("colossal_TextureArea");
            public static readonly int _MeshSize          = Shader.PropertyToID("colossal_MeshSize");
            public static readonly int _DecalLayerMask    = Shader.PropertyToID("colossal_DecalLayerMask");
            public static readonly int _LodDistanceFactor = Shader.PropertyToID("colossal_LodDistanceFactor");
        }

        // Heartbeat: every N OnUpdate call we log a status line.
        private int _updateTicks;

        // Live geometry source: MarkingPair buffers on node entities. Each tick we diff
        // (node, pairIndex, srcEdge, srcGap, dstEdge, dstGap) tuples against _pairs and
        // rebuild any that changed. Cheap because pair counts are tiny per node.
        private readonly Dictionary<PairKey, PairRender> _pairs = new();
        private EntityQuery _nodesWithPairs;

        // Root for all pair GameObjects. Keeping them under one parent makes scene cleanup
        // and inspector debugging easier. Pattern lifted from vanilla
        // RenderPrefabRenderer.cs:202-203 — same approach: one parent transform, child
        // GameObjects per mesh.
        private GameObject _root;

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
            _root = new GameObject("TRL_MarkingRoot") { hideFlags = HideFlags.DontSave };
            log.Info("MarkingMeshRenderSystem: created (GameObject+MeshRenderer pipeline)");
        }

        protected override void OnDestroy()
        {
            foreach (var p in _pairs.Values) p.Destroy();
            _pairs.Clear();
            SafeDestroy(_root);
            _root = null;
            base.OnDestroy();
        }

        protected override void OnUpdate()
        {
            _updateTicks++;
            if (_updateTicks % 120 == 1)
                log.Info($"[mesh-render] OnUpdate tick={_updateTicks} nodesWithPairs={_nodesWithPairs.CalculateEntityCount()} pairs={_pairs.Count}");

            var mat = GetMaterial();
            if (mat == null) return; // material not yet acquireable (clone not baked)

            // Per-tick reconcile: rebuild geometry + ensure a GameObject per active pair.
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
                    if (!_pairs.TryGetValue(key, out var entry))
                    {
                        entry = PairRender.Create(_root, mat, $"TRL_Pair_{node.Index}_{i}");
                        _pairs[key] = entry;
                    }
                    BuildRibbonMesh(entry.mesh, src.position, src.tangent, dst.position, dst.tangent);
                    UpdatePairMpb(entry);
                }
            }
            nodes.Dispose();

            // Sweep entries whose pair was removed.
            if (_pairs.Count > nextKeys.Count)
            {
                var toRemove = new List<PairKey>();
                foreach (var kv in _pairs)
                    if (!nextKeys.Contains(kv.Key)) toRemove.Add(kv.Key);
                foreach (var k in toRemove)
                {
                    _pairs[k].Destroy();
                    _pairs.Remove(k);
                }
            }
        }

        /// <summary>Push per-pair decal uniforms onto the MeshRenderer via its MPB.
        /// Vanilla equivalent: RenderPrefabRenderer.SetDecalProperties at line 401, called
        /// from Update() on line 818. colossal_DecalLayerMask is NOT here — it lives on
        /// the material (HDRP reads it from material constants for the decal pass).</summary>
        private void UpdatePairMpb(PairRender entry)
        {
            var bounds = entry.mesh.bounds;
            _props.Clear();
            _props.SetVector(ShaderIDs._TextureArea,    _textureArea);
            // w = 0f to match vanilla (RenderPrefabRenderer.cs:408). v1 PoC speculated
            // w = bounds.center.y; cross-reference proved that wrong.
            _props.SetVector(ShaderIDs._MeshSize,       new Vector4(bounds.size.x, bounds.size.y, bounds.size.z, 0f));
            _props.SetFloat (ShaderIDs._LodDistanceFactor, 1f);
            entry.renderer.SetPropertyBlock(_props);
        }

        /// <summary>Path B Step 2: build a DefaultDecalShader material at runtime so the box
        /// mesh becomes a projection volume that paints onto road surface, not opaque
        /// geometry. Previous decal attempt failed because the receiving mesh was a flat
        /// strip with zero volume — agent's vanilla-pipeline decomp identified that decal
        /// shaders project onto whatever surface lies inside their bounding volume, and a
        /// flat strip has no inside. With Step 3c-1 the receiver is now a thin box.
        ///
        /// Falls back to HDRP/Unlit (Step 3b path) if either shader lookup fails or the
        /// validated material somehow ends up unusable. Fallback is silent at the material
        /// level — we log which path we took but never throw.</summary>
        private Material GetMaterial()
        {
            if (_material != null) return _material;
            if (_materialAttempted) return null;
            _materialAttempted = true;
            try
            {
                var decalMat = BuildDecalMaterial();
                if (decalMat != null)
                {
                    _material = decalMat;
                    _materialAttempted = false;
                    return _material;
                }
                log.Warn("MarkingMeshRenderSystem: DefaultDecalShader path unavailable, falling back to HDRP/Unlit");
                var unlitMat = BuildUnlitMaterial();
                if (unlitMat != null)
                {
                    _material = unlitMat;
                    _materialAttempted = false;
                    return _material;
                }
            }
            catch (System.Exception ex)
            {
                log.Error(ex, "MarkingMeshRenderSystem: material acquisition threw");
            }
            return null;
        }

        /// <summary>Construct the decal material. Property names + values mirror what
        /// ManagedBatchSystem sets on vanilla decal materials (see decomp:
        /// ManagedBatchSystem.cs around line 1388 for the canonical setup). The critical
        /// undocumented call is HDMaterial.ValidateMaterial — without it the HDRP shader
        /// keywords aren't derived from properties and every render pass is a silent no-op.</summary>
        private Material BuildDecalMaterial()
        {
            // PATH B.3 — clone the vanilla CurvedDecalShader material straight from
            // EdgeLineCloneSystem's prefab. This is the recipe that finally works:
            //
            // 1) Vanilla decal materials use shader 'BH/Decals/CurvedDecalShader',
            //    NOT 'BH/Decals/DefaultDecalShader'. The previous PoC was on the
            //    wrong shader entirely (Shader.Find("BH/Decals/CurvedDecal") probed
            //    the wrong name).
            // 2) The vanilla material ships with _NormalMap, _MaskMap, baked stencil
            //    refs (_DecalStencilRef=16, _DecalStencilWriteMask=16), surface-type
            //    flags and renderQueue=1975 (decal-pre-pass slot, NOT generic 2000).
            //    Building via `new Material(shader)` defaults all of these wrong;
            //    HDMaterial.ValidateMaterial has a documented runtime gap that
            //    does not recover them.
            // 3) Cloning the vanilla material brings ALL of those for free, plus
            //    the correct shader. We then only override per-instance properties
            //    via MPB at draw time.
            //
            // See research/RESEARCH_decal_breakthrough_hunt.md §2 and §4, and the
            // mat-dump diff captured in TownRoadLane.Mod.log on 2026-05-17 19:24:17.
            var vanillaMat = BorrowVanillaDecalMaterialForDiff();
            if (vanillaMat == null)
            {
                log.Warn("MarkingMeshRenderSystem: vanilla decal material not yet available; will retry next tick");
                return null;
            }

            var mat = new Material(vanillaMat) { name = "TRL_PairPoC_Decal", hideFlags = HideFlags.HideAndDontSave };

            // Store the borrowed TextureArea (atlas sub-rect for the lane-line sprite)
            // for per-draw MPB use. Texture itself is already on the cloned material —
            // no need to re-set it.
            if (TryBorrowLaneLineTexture(out _, out var texArea)) _textureArea = texArea;

            // Belt-and-braces — set decal layer mask on the clone, in case the vanilla
            // material left it at 0 (the dump showed colossal_DecalLayerMask=0 on
            // vanilla too, but it works there because the BRG pipeline sets it via
            // material-key — out of band from the material constants). Encoded as
            // float bit-cast of int (ManagedBatchSystem.cs:1408 pattern).
            mat.SetFloat(ShaderIDs._DecalLayerMask,
                         BitConverter.Int32BitsToSingle((int)Game.Rendering.DecalLayers.Roads));

            // Re-validate after clone — keyword/pass derivation runs against the
            // cloned property state, not the source.
            HDMaterial.ValidateMaterial(mat);

            log.Info($"MarkingMeshRenderSystem: cloned vanilla decal material '{vanillaMat.name}' → '{mat.name}' shader='{mat.shader.name}' renderQueue={mat.renderQueue}");
            DumpMaterial("CLONED", mat);

            return mat;
        }

        private static void DumpMaterial(string tag, Material m)
        {
            try
            {
                var s = m.shader;
                log.Info($"[mat-dump:{tag}] name='{m.name}' shader='{s.name}' renderQueue={m.renderQueue} passCount={s.passCount} propCount={s.GetPropertyCount()}");
                for (int i = 0; i < s.GetPropertyCount(); i++)
                {
                    var name = s.GetPropertyName(i);
                    var type = s.GetPropertyType(i);
                    string val;
                    switch (type)
                    {
                        case UnityEngine.Rendering.ShaderPropertyType.Float:
                        case UnityEngine.Rendering.ShaderPropertyType.Range: val = m.GetFloat(name).ToString("F3"); break;
                        case UnityEngine.Rendering.ShaderPropertyType.Int:   val = m.GetInt(name).ToString(); break;
                        case UnityEngine.Rendering.ShaderPropertyType.Color: val = m.GetColor(name).ToString(); break;
                        case UnityEngine.Rendering.ShaderPropertyType.Vector:val = m.GetVector(name).ToString(); break;
                        case UnityEngine.Rendering.ShaderPropertyType.Texture: val = m.GetTexture(name)?.name ?? "<null>"; break;
                        default: val = "?"; break;
                    }
                    log.Info($"[mat-dump:{tag}]   prop {name} ({type}) = {val}");
                }
                for (int i = 0; i < m.passCount; i++)
                {
                    var pname = m.GetPassName(i);
                    bool enabled = string.IsNullOrEmpty(pname) ? true : m.GetShaderPassEnabled(pname);
                    log.Info($"[mat-dump:{tag}]   pass[{i}] name='{pname}' enabled={enabled}");
                }
                var kws = m.enabledKeywords;
                string ks = "";
                for (int i = 0; i < kws.Length; i++) { if (i > 0) ks += ","; ks += kws[i].name; }
                log.Info($"[mat-dump:{tag}]   enabledKeywords=[{ks}]");
            }
            catch (System.Exception ex) { log.Error(ex, $"DumpMaterial:{tag} threw"); }
        }

        /// <summary>One-shot borrow of the vanilla decal material straight from our edge-line
        /// clone prefab's mesh — for diagnostic comparison with our hand-built one. NOT used
        /// for rendering, only for the side-by-side dump in BuildDecalMaterial().</summary>
        private Material BorrowVanillaDecalMaterialForDiff()
        {
            try
            {
                var prefab = PickClonePrefab();
                if (prefab is NetLaneGeometryPrefab geom && geom.m_Meshes != null && geom.m_Meshes.Length > 0)
                {
                    var meshInfo = geom.m_Meshes[0];
                    if (meshInfo?.m_Mesh != null) return meshInfo.m_Mesh.ObtainMaterial(0, useVT: false);
                }
            }
            catch (System.Exception ex) { log.Error(ex, "BorrowVanillaDecalMaterialForDiff threw"); }
            return null;
        }

        /// <summary>HDRP/Unlit fallback — kept because it's the proven working path from
        /// Step 3b. If DefaultDecalShader misbehaves we degrade gracefully back to the
        /// visible white-bar render rather than dropping rendering entirely.</summary>
        private Material BuildUnlitMaterial()
        {
            var shader = Shader.Find("HDRP/Unlit");
            if (shader == null) { log.Warn("MarkingMeshRenderSystem: HDRP/Unlit shader not found"); return null; }

            var mat = new Material(shader) { name = "TRL_PairPoC_Unlit" };
            mat.color = Color.white;
            mat.SetFloat("_SurfaceType", 0f);
            mat.SetFloat("_DoubleSidedEnable", 1f);
            mat.SetFloat("_CullMode", 0f);

            if (TryBorrowLaneLineTexture(out var tex, out _))
            {
                if (mat.HasProperty("_UnlitColorMap")) mat.SetTexture("_UnlitColorMap", tex);
                if (mat.HasProperty("_MainTex"))       mat.SetTexture("_MainTex",       tex);
                mat.mainTexture = tex;
                log.Info($"MarkingMeshRenderSystem: unlit borrowed texture '{tex.name}' ({tex.width}x{tex.height})");
            }

            HDMaterial.ValidateMaterial(mat);

            var kw = mat.enabledKeywords;
            string kwStr = "";
            for (int i = 0; i < kw.Length; i++) { if (i > 0) kwStr += ","; kwStr += kw[i].name; }
            log.Info($"MarkingMeshRenderSystem: built unlit material '{mat.name}' shader='{mat.shader.name}' renderQueue={mat.renderQueue} passes={mat.passCount} keywords=[{kwStr}]");
            return mat;
        }

        /// <summary>Extract the painted-line bitmap AND its sub-rectangle inside the
        /// vanilla atlas. The borrowed texture (LaneLine_BaseColor_*) is an 8192×4096
        /// atlas — we MUST pass the prefab's DecalProperties.m_TextureArea as
        /// colossal_TextureArea, otherwise the shader samples the whole atlas (which
        /// outside the actual lane-line sprite is mostly transparent or unrelated
        /// pixels — explains why "all material slots set correctly, still zero
        /// visible pixels" was the symptom).
        ///
        /// Returns false if the clone hasn't baked yet or the prefab is missing
        /// DecalProperties — caller falls back to solid white + full-atlas UVs.</summary>
        private bool TryBorrowLaneLineTexture(out Texture tex, out Vector4 textureArea)
        {
            tex = null;
            textureArea = new Vector4(0f, 0f, 1f, 1f); // safe default = full atlas
            try
            {
                var prefab = PickClonePrefab();
                if (prefab == null) return false;
                if (!(prefab is NetLaneGeometryPrefab geom)) return false;

                // Borrow texture from the prefab's render material.
                if (geom.m_Meshes == null || geom.m_Meshes.Length == 0) return false;
                var meshInfo = geom.m_Meshes[0];
                if (meshInfo?.m_Mesh == null) return false;
                var vanillaMat = meshInfo.m_Mesh.ObtainMaterial(0, useVT: false);
                if (vanillaMat != null)
                {
                    if (vanillaMat.HasProperty("_BaseColorMap")) tex = vanillaMat.GetTexture("_BaseColorMap");
                    if (tex == null && vanillaMat.HasProperty("_MainTex")) tex = vanillaMat.GetTexture("_MainTex");
                    if (tex == null) tex = vanillaMat.mainTexture;
                }

                // Borrow texture-area from DecalProperties. Important: DecalProperties
                // lives on the RENDER prefab (the mesh asset inside m_Meshes[].m_Mesh),
                // not on the NetLaneGeometryPrefab wrapping it. NetLanePrefab is just a
                // layout/host descriptor; the actual decal-bearing prefab is the mesh.
                Game.Prefabs.DecalProperties decalProps = null;
                if (meshInfo.m_Mesh != null) decalProps = meshInfo.m_Mesh.GetComponent<Game.Prefabs.DecalProperties>();
                if (decalProps == null) decalProps = prefab.GetComponent<Game.Prefabs.DecalProperties>(); // safety fallback
                if (decalProps != null)
                {
                    var area = decalProps.m_TextureArea;
                    textureArea = new Vector4(area.min.x, area.min.y, area.max.x, area.max.y);
                    log.Info($"MarkingMeshRenderSystem: borrowed DecalProperties.m_TextureArea = ({textureArea.x:F3},{textureArea.y:F3})..({textureArea.z:F3},{textureArea.w:F3}) layerMask={decalProps.m_LayerMask}");
                }
                else
                {
                    var meshName = meshInfo.m_Mesh != null ? meshInfo.m_Mesh.name : "<null>";
                    log.Info($"MarkingMeshRenderSystem: neither '{prefab.name}' nor mesh '{meshName}' has DecalProperties — using full-atlas UVs");
                }
                return tex != null;
            }
            catch (System.Exception ex)
            {
                log.Error(ex, "MarkingMeshRenderSystem: TryBorrowLaneLineTexture threw");
                return false;
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

        /// <summary>Build a thin extruded box following the Bezier arc between two endpoints.
        /// Each Bezier sample produces FOUR vertices: L_bot, R_bot, L_top, R_top. Per segment
        /// we emit 8 triangles (top quad + bottom quad + 2 side quads). The resulting closed
        /// volume serves two purposes:
        ///   - With HDRP/Unlit material the box is visible as a thin 3D bar — sanity-check
        ///     that geometry is correct (Path B Step 1 — geometry only).
        ///   - With DefaultDecalShader material the box becomes a projection volume; the
        ///     shader paints its texture onto whatever surface lies inside (Path B Step 2).
        ///
        /// Winding convention: outward-facing normals. Looking down +Y, vertices are laid
        /// out as L_bot=0, R_bot=1, L_top=2, R_top=3 per sample; "right" = +cross(up,tangent).
        /// Top quad uses (L_top, R_top, R_top_next, L_top_next) wound CCW from above.
        /// Bottom quad is wound CCW from below. Side quads face outward in ±right.</summary>
        private static void BuildRibbonMesh(Mesh mesh, float3 a, float2 aTan, float3 b, float2 bTan)
        {
            float chord = math.distance(a, b);
            if (chord < 0.01f) { mesh.Clear(); return; }
            float pull = chord * kPullFactor;
            float3 aT = new float3(-aTan.x, 0f, -aTan.y);
            float3 bT = new float3(-bTan.x, 0f, -bTan.y);
            float3 p0 = a;
            float3 p1 = a + aT * pull;
            float3 p2 = b + bT * pull;
            float3 p3 = b;

            float halfW = kMarkingWidth * 0.5f;
            float3 liftBot = new float3(0f, kHeightOffset,                0f);
            float3 liftTop = new float3(0f, kHeightOffset + kBoxHeight,   0f);

            int sampleCount = kSegments + 1;
            int vCount = sampleCount * 4;
            // 8 triangles per segment × 3 indices each = 24 indices per segment.
            int tCount = kSegments * 24;
            var verts = new Vector3[vCount];
            var uvs   = new Vector2[vCount];
            var tris  = new int[tCount];

            float3 prevPos = p0;
            float arcLen = 0f;
            for (int i = 0; i < sampleCount; i++)
            {
                float t = (float)i / kSegments;
                float3 pos = CubicBezier(p0, p1, p2, p3, t);
                if (i > 0) arcLen += math.distance(prevPos, pos);
                prevPos = pos;
                float3 tan = CubicBezierTangent(p0, p1, p2, p3, t);
                float3 tanFlat = new float3(tan.x, 0f, tan.z);
                float3 right = math.normalizesafe(math.cross(new float3(0f, 1f, 0f), tanFlat));

                Vector3 lBot = (Vector3)(pos - right * halfW + liftBot);
                Vector3 rBot = (Vector3)(pos + right * halfW + liftBot);
                Vector3 lTop = (Vector3)(pos - right * halfW + liftTop);
                Vector3 rTop = (Vector3)(pos + right * halfW + liftTop);

                int baseV = i * 4;
                verts[baseV + 0] = lBot;
                verts[baseV + 1] = rBot;
                verts[baseV + 2] = lTop;
                verts[baseV + 3] = rTop;

                // All 4 verts at a sample share the same arc-length v coordinate; u maps
                // across the width (0 on left edge, 1 on right edge). Only the top face
                // is visible to the camera under typical viewing, but DefaultDecalShader
                // remaps via colossal_TextureArea so exact UVs barely matter — we provide
                // them mainly for the Unlit fallback render.
                float v = arcLen / kTextureTileMeters;
                uvs[baseV + 0] = new Vector2(0f, v);
                uvs[baseV + 1] = new Vector2(1f, v);
                uvs[baseV + 2] = new Vector2(0f, v);
                uvs[baseV + 3] = new Vector2(1f, v);
            }

            for (int i = 0; i < kSegments; i++)
            {
                int a0 = i * 4;          // current sample base: L_bot, R_bot, L_top, R_top
                int b0 = (i + 1) * 4;    // next sample base
                int Lb0 = a0 + 0, Rb0 = a0 + 1, Lt0 = a0 + 2, Rt0 = a0 + 3;
                int Lb1 = b0 + 0, Rb1 = b0 + 1, Lt1 = b0 + 2, Rt1 = b0 + 3;
                int o = i * 24;

                // Top face (normal +Y). Winding CCW viewed from above: Lt0 → Rt0 → Rt1 → Lt1.
                tris[o +  0] = Lt0; tris[o +  1] = Rt0; tris[o +  2] = Rt1;
                tris[o +  3] = Lt0; tris[o +  4] = Rt1; tris[o +  5] = Lt1;

                // Bottom face (normal −Y). Winding CCW viewed from below: reverse of top.
                tris[o +  6] = Lb0; tris[o +  7] = Rb1; tris[o +  8] = Rb0;
                tris[o +  9] = Lb0; tris[o + 10] = Lb1; tris[o + 11] = Rb1;

                // Right side (normal +right). Verts: Rb0, Rb1 (bottom), Rt0, Rt1 (top).
                tris[o + 12] = Rb0; tris[o + 13] = Rb1; tris[o + 14] = Rt1;
                tris[o + 15] = Rb0; tris[o + 16] = Rt1; tris[o + 17] = Rt0;

                // Left side (normal −right). Verts: Lb0, Lb1 (bottom), Lt0, Lt1 (top).
                tris[o + 18] = Lb0; tris[o + 19] = Lt1; tris[o + 20] = Lb1;
                tris[o + 21] = Lb0; tris[o + 22] = Lt0; tris[o + 23] = Lt1;
            }

            mesh.Clear();
            mesh.vertices = verts;
            mesh.triangles = tris;
            mesh.uv = uvs;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            // Box has real Y extent (kBoxHeight) so the bounds.size.y == 0 culling
            // workaround from the flat-ribbon era is no longer needed. Keep a small
            // safety expansion just in case the curve happens to fall in a degenerate
            // configuration (e.g. both endpoints exactly coplanar with camera).
            var meshBounds = mesh.bounds;
            meshBounds.Expand(0.1f);
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
        private static void SafeDestroy(UnityEngine.Object o)
        {
            if (o == null) return;
            UnityEngine.Object.Destroy(o);
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

        /// <summary>One pair = one GameObject + MeshFilter + MeshRenderer + Mesh.
        /// Pattern mirrors vanilla RenderPrefabRenderer.cs:202-219 — proven HDRP-compatible
        /// way to feed a runtime-built mesh + decal material into the SRP. Replaces the
        /// Graphics.DrawMesh path that delivered draw calls but never reached the decal
        /// pass (commit d7872a9 captured the dead end).</summary>
        private sealed class PairRender
        {
            public readonly GameObject go;
            public readonly MeshFilter filter;
            public readonly MeshRenderer renderer;
            public readonly Mesh mesh;

            private PairRender(GameObject g, MeshFilter f, MeshRenderer r, Mesh m)
            { go = g; filter = f; renderer = r; mesh = m; }

            public static PairRender Create(GameObject parent, Material sharedMaterial, string name)
            {
                var go = new GameObject(name) { hideFlags = HideFlags.DontSave };
                go.transform.SetParent(parent.transform, worldPositionStays: false);
                // Mesh vertices are world-space, so the GO sits at origin (no local offset).
                go.transform.localPosition = Vector3.zero;
                go.transform.localRotation = Quaternion.identity;
                go.transform.localScale = Vector3.one;

                var mesh = new Mesh { name = name };
                var filter = go.AddComponent<MeshFilter>();
                filter.sharedMesh = mesh;
                var renderer = go.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = sharedMaterial;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                renderer.lightProbeUsage = LightProbeUsage.Off;
                renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
                // CRITICAL: HDRP Decal Meshes (our MeshRenderer + decal material) are
                // filtered by renderingLayerMask, NOT by colossal_DecalLayerMask. Default
                // MeshRenderer.renderingLayerMask = 1, vanilla BRG draw commands use
                // uint.MaxValue (BatchManagerSystem.cs:603,831). Mismatch with receiver
                // mask = silent zero pixels. Open the floodgates first; narrow later if
                // we get too much spill onto non-road surfaces.
                renderer.renderingLayerMask = uint.MaxValue;
                return new PairRender(go, filter, renderer, mesh);
            }

            public void Destroy()
            {
                SafeDestroy(mesh);
                SafeDestroy(go);
            }
        }
    }
}
