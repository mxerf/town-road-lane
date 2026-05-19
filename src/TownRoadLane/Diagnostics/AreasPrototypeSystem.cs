using Colossal.Logging;
using Game;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace TownRoadLane.Diagnostics
{
    // Phase 6 prototype probes. Runs ONCE at game-load time, dumps verdicts to the log, then
    // disables itself. Two questions:
    //   T1. Does Shader.Find("Shader Graphs/AreaDecalShader") return non-null from mod context?
    //       If not, we fall back to DuplicatePrefab(vanillaConcrete) + material swap.
    //   T2. Is there a clonable vanilla SurfacePrefab? What's its name + which RenderedArea
    //       fields are populated? This unblocks 6c (Solid fill MVP).
    // Test T3 (Owner cascade-delete on Area) is left for in-game manual verification once 6c
    // is wired — too invasive to spawn-and-delete a real Area entity from a one-shot probe.
    public partial class AreasPrototypeSystem : GameSystemBase
    {
        private static readonly ILog log = Mod.log;

        private PrefabSystem _prefabSystem;
        private bool _done;
        // Second-stage probe: dump G87 prefabs once the world has been ticking long enough that
        // any deferred prefab loading should have settled.
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
                return; // keep system alive for the second pass
            }

            // Wait ~10 sec at 60 fps so any deferred mod-asset loading (Skyve / EAI / G87) is done.
            _ticksSinceFirstProbe++;
            if (_g87Probed || _ticksSinceFirstProbe < 600) return;
            _g87Probed = true;
            log.Info("[AreasPrototype] === second pass: G87 + mod asset survey ===");
            ProbeAllPrefabsByNameSubstring("G87");
            ProbeAllPrefabsByNameSubstring("g87");
            ProbeSurfacePrefabCountAgain();
            log.Info("[AreasPrototype] === second pass done — disabling ===");
            Enabled = false;
        }

        private void ProbeAllPrefabsByNameSubstring(string substr)
        {
            // Use the broadest possible query — every entity that has a PrefabData. Then filter
            // by the name on the corresponding managed prefab. Heavy but one-shot.
            var query = GetEntityQuery(ComponentType.ReadOnly<PrefabData>());
            using var ents = query.ToEntityArray(Allocator.Temp);
            int hits = 0;
            for (int i = 0; i < ents.Length; i++)
            {
                if (!_prefabSystem.TryGetPrefab<PrefabBase>(ents[i], out var pb) || pb == null) continue;
                if (!pb.name.Contains(substr)) continue;
                hits++;
                if (hits <= 60)  // cap log spam
                    log.Info($"[AreasPrototype]   G87? [{i}] type={pb.GetType().Name} name={pb.name}");
            }
            log.Info($"[AreasPrototype] substring '{substr}': {hits} matching prefab(s) found");
        }

        private void ProbeSurfacePrefabCountAgain()
        {
            // First pass found only the 29 vanilla surfaces. Second pass count went to 70 → 41
            // extra surfaces are loaded by mods (likely EAI / G87 / similar asset pipelines).
            // Dump them all now, listing the prefab type and surface name. This is the inventory
            // we use to build the style picker in 6d-3.
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
            // Enumerate ALL loaded SurfacePrefabs (including G87 if user has them installed). For
            // each: name + which RenderedArea fields look usable (material, decal layer mask,
            // renderer priority). This tells us which one to clone for the Solid + Hatching styles.
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
            // Road markings are RenderPrefab assets (mesh + material), not areas — they don't use
            // RendererPriority. They're projected by the curved-decal shader at a fixed depth
            // bias. So our area surfaces are sorted only against OTHER area surfaces by priority.
            // To draw on top of road markings we'd need a different layer entirely — area decals
            // typically render at the road surface level, road markings render slightly above.
            //
            // Conclusion: priority alone won't lift our areas above road markings. We need to
            // try DecalLayerMask alternatives (Markings? Decals? — see DecalLayers enum) or
            // accept that surfaces always sit under road decals.
            //
            // Just dump the DecalLayers enum values so we know what to try in 6d-2.
            log.Info($"[AreasPrototype] T4 DecalLayers enum values:");
            foreach (var name in System.Enum.GetNames(typeof(Game.Rendering.DecalLayers)))
            {
                log.Info($"[AreasPrototype]   - DecalLayers.{name}");
            }
        }
    }
}
