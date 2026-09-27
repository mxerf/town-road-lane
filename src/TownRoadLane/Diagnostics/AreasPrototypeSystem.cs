using Colossal.Logging;
using Game;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace TownRoadLane.Diagnostics
{
    // One-shot survey for area fills. The first update logs area shader availability, the
    // vanilla SurfacePrefabs with their RenderedArea settings and the DecalLayers values. About
    // ten seconds later it logs mod-loaded surfaces (G87 and others) and NetLane meshes, then
    // disables itself.
    public partial class AreasPrototypeSystem : GameSystemBase
    {
        private static readonly ILog log = Mod.log;

        private PrefabSystem _prefabSystem;
        private bool _done;
        private int _ticksSinceFirstProbe;
        private bool _g87Probed;

        protected override void OnCreate()
        {
            base.OnCreate();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
        }

        protected override void OnUpdate()
        {
            if (!_done)
            {
                _done = true;
                log.Info("[AreasPrototype] === Phase 6 prototype probes ===");
                ProbeShader();
                ProbeVanillaSurfacePrefabs();
                ProbeRoadMarkingPriority();
                log.Info("[AreasPrototype] === probes done (initial pass) ===");
                return;
            }

            // ~10 s at 60 fps, so deferred mod asset loading (ExtraAssetsImporter, G87) has finished.
            _ticksSinceFirstProbe++;
            if (_g87Probed || _ticksSinceFirstProbe < 600) return;
            _g87Probed = true;
            log.Info("[AreasPrototype] === second pass: G87 + mod asset survey ===");
            ProbeAllPrefabsByNameSubstring("G87");
            ProbeAllPrefabsByNameSubstring("g87");
            ProbeSurfacePrefabCountAgain();
            ProbeNetLaneGeometry();
            log.Info("[AreasPrototype] === second pass done — disabling ===");
            Enabled = false;
        }

        private void ProbeAllPrefabsByNameSubstring(string substr)
        {
            // Scans every prefab entity; expensive, but it runs once.
            var query = GetEntityQuery(ComponentType.ReadOnly<PrefabData>());
            using var ents = query.ToEntityArray(Allocator.Temp);
            int hits = 0;
            for (int i = 0; i < ents.Length; i++)
            {
                if (!_prefabSystem.TryGetPrefab<PrefabBase>(ents[i], out var pb) || pb == null) continue;
                if (!pb.name.Contains(substr)) continue;
                hits++;
                if (hits <= 60)
                    log.Info($"[AreasPrototype]   G87? [{i}] type={pb.GetType().Name} name={pb.name}");
            }
            log.Info($"[AreasPrototype] substring '{substr}': {hits} matching prefab(s) found");
        }

        private void ProbeSurfacePrefabCountAgain()
        {
            // At startup only vanilla surfaces exist; mod-loaded surfaces appear later, so the
            // full list is logged again here.
            var query = GetEntityQuery(ComponentType.ReadOnly<PrefabData>(), ComponentType.ReadOnly<SurfaceData>());
            using var ents = query.ToEntityArray(Allocator.Temp);
            log.Info($"[AreasPrototype] T5 SurfacePrefab count (second pass) = {ents.Length} — FULL LIST:");
            for (int i = 0; i < ents.Length; i++)
            {
                if (!_prefabSystem.TryGetPrefab<PrefabBase>(ents[i], out var pb) || pb == null) continue;
                if (pb is not SurfacePrefab sp) continue;
                string raInfo = sp.TryGet<RenderedArea>(out var ra) && ra != null
                    ? $"mat={(ra.m_Material != null ? ra.m_Material.name : "null")} prio={ra.m_RendererPriority} layer={ra.m_DecalLayerMask} uvScale={ra.m_UVScale}"
                    : "<no RenderedArea>";
                log.Info($"[AreasPrototype]   surf[{i}] {sp.name} | {raInfo}");
            }
        }

        private void ProbeNetLaneGeometry()
        {
            // Lists NetLaneGeometryPrefabs (lanes with real meshes, not decals) with mesh bounds.
            // The line pipeline can place any NetLane prefab along a curve, so a curb-like
            // cross-section (x = width, y = height) is enough for a curb style.
            var query = GetEntityQuery(ComponentType.ReadOnly<PrefabData>(), ComponentType.ReadOnly<NetLaneData>());
            using var ents = query.ToEntityArray(Allocator.Temp);
            int geomCount = 0;
            for (int i = 0; i < ents.Length; i++)
            {
                if (!_prefabSystem.TryGetPrefab<PrefabBase>(ents[i], out var pb) || pb == null) continue;
                if (pb is not NetLaneGeometryPrefab glp || glp.m_Meshes == null || glp.m_Meshes.Length == 0) continue;
                geomCount++;
                var meshes = new System.Text.StringBuilder();
                for (int m = 0; m < glp.m_Meshes.Length; m++)
                {
                    var mesh = glp.m_Meshes[m].m_Mesh;
                    if (meshes.Length > 0) meshes.Append("; ");
                    if (mesh == null) { meshes.Append("<null>"); continue; }
                    var size = mesh.bounds.max - mesh.bounds.min;
                    meshes.Append($"{mesh.name} [{size.x:F2}w×{size.y:F2}h×{size.z:F2}l m]");
                }
                log.Info($"[AreasPrototype]   netlane-geom {glp.name} ({glp.GetType().Name}): {meshes}");
            }
            log.Info($"[AreasPrototype] NetLane survey: {ents.Length} NetLane prefab(s) total, {geomCount} with 3D lane meshes");
        }

        private void ProbeShader()
        {
            string[] candidates = new[]
            {
                "Shader Graphs/AreaDecalShader",
                "Shader Graphs/AreaShader",
                "ShaderGraphs/AreaDecalShader",
                "HDRP/Decal",
            };
            foreach (var name in candidates)
            {
                var sh = Shader.Find(name);
                log.Info($"[AreasPrototype] T1 Shader.Find(\"{name}\") -> {(sh != null ? "OK" : "NULL")}");
            }
        }

        private void ProbeVanillaSurfacePrefabs()
        {
            var query = GetEntityQuery(
                ComponentType.ReadOnly<PrefabData>(),
                ComponentType.ReadOnly<SurfaceData>());
            using var ents = query.ToEntityArray(Allocator.Temp);
            log.Info($"[AreasPrototype] T2 SurfacePrefab count = {ents.Length}");

            for (int i = 0; i < ents.Length; i++)
            {
                if (!_prefabSystem.TryGetPrefab<PrefabBase>(ents[i], out var pb) || pb == null) continue;
                if (pb is not SurfacePrefab sp) continue;

                string raInfo = sp.TryGet<RenderedArea>(out var ra) && ra != null
                    ? $"mat={(ra.m_Material != null ? ra.m_Material.name : "null")} prio={ra.m_RendererPriority} layer={ra.m_DecalLayerMask} uvScale={ra.m_UVScale}"
                    : "<no RenderedArea>";
                log.Info($"[AreasPrototype]   [{i}] {sp.name} | {raInfo}");
            }
        }

        private void ProbeRoadMarkingPriority()
        {
            // Road markings are RenderPrefab meshes drawn by the curved-decal shader and ignore
            // RendererPriority, which only orders area surfaces among themselves. Lifting a fill
            // above road markings would take a different decal layer, hence this list.
            log.Info($"[AreasPrototype] T4 DecalLayers enum values:");
            foreach (var name in System.Enum.GetNames(typeof(Game.Rendering.DecalLayers)))
            {
                log.Info($"[AreasPrototype]   - DecalLayers.{name}");
            }
        }
    }
}
