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

        protected override void OnCreate()
        {
            base.OnCreate();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
        }

        protected override void OnUpdate()
        {
            if (_done) return;
            _done = true;

            log.Info("[AreasPrototype] === Phase 6 prototype probes ===");
            ProbeShader();
            ProbeVanillaSurfacePrefabs();
            log.Info("[AreasPrototype] === probes done (system disables itself) ===");
            Enabled = false;
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
            // Enumerate all loaded SurfacePrefabs. For each: name + which RenderedArea fields look
            // usable (material, decal layer mask, renderer priority). This tells us which one to
            // clone for the MVP Solid fill.
            var query = GetEntityQuery(
                ComponentType.ReadOnly<PrefabData>(),
                ComponentType.ReadOnly<SurfaceData>());
            using var ents = query.ToEntityArray(Allocator.Temp);
            log.Info($"[AreasPrototype] T2 SurfacePrefab count = {ents.Length}");

            int dumped = 0;
            for (int i = 0; i < ents.Length && dumped < 20; i++)
            {
                if (!_prefabSystem.TryGetPrefab<PrefabBase>(ents[i], out var pb) || pb == null) continue;
                if (pb is not SurfacePrefab sp) continue;

                string raInfo = sp.TryGet<RenderedArea>(out var ra) && ra != null
                    ? $"mat={(ra.m_Material != null ? ra.m_Material.name : "null")} prio={ra.m_RendererPriority} layer={ra.m_DecalLayerMask} uvScale={ra.m_UVScale}"
                    : "<no RenderedArea>";
                log.Info($"[AreasPrototype]   [{i}] {sp.name} | {raInfo}");
                dumped++;
            }
        }
    }
}
