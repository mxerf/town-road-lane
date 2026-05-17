using System;
using System.Collections.Generic;
using Colossal.Logging;
using Game;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;

namespace TownRoadLane
{
    /// <summary>
    /// Gives ordinary city roads (3 m car lanes) the curb-side edge marking line that highway roads
    /// already have.
    ///
    /// v1.1 (commit 342afa4) patched the vanilla 'EU/NA Highway Edge Line' in place — appending city
    /// drive lanes to its m_LeftLanes. v2 instead CLONES that prefab so we can swap its mesh
    /// independently of vanilla (G87 custom mesh support); the vanilla highway edge line stays
    /// untouched. The clones reference vanilla 'Car Drive Lane 3' lanes in m_LeftLanes, so
    /// NetInitializeSystem reverse-indexes our entries onto the vanilla lane prefab's SecondaryNetLane
    /// buffer — and any road (including Road Builder roads) that uses 'Car Drive Lane 3' picks up our
    /// markings automatically. See RESEARCH_road_builder.md §5.
    ///
    /// Per city lane we add TWO SecondaryLaneInfo entries, exact replica of what vanilla uses for
    /// 'Highway Drive Lane 3' on its own edge line:
    ///   { RequireSafe = true } — straight-segment edge line
    ///   { RequireMerge = true, RequireSafeMaster = true } — continues line through merges (onramps,
    ///   width transitions). See RESEARCH_v1_1.md §1 for rationale.
    ///
    /// Lives in m_LeftLanes only; canFlipSides=true makes vanilla mirror to the right curb.
    ///
    /// Risks covered (IMPLEMENTATION_PLAN.md): K1 (no Entity caching, lookup via PrefabBase),
    /// K2 (PrefabUpdate phase, NetInitializeSystem fires same frame),
    /// K3 (DuplicatePrefab calls AddPrefab internally),
    /// K4 (re-runs on every game load via fresh OnCreate),
    /// K6 (SwapMesh always paired with UpdatePrefab),
    /// K7 (G87 fallback chain),
    /// K8 (style=Off strips m_LeftLanes hosting so the clone stops drawing).
    /// </summary>
    public partial class EdgeLineCloneSystem : GameSystemBase
    {
        private static readonly ILog log = Mod.log;

        // City drive-lane prefabs that should now get the edge line. Mirrors v1.1 EdgeMarkingPatchSystem.
        // 'Car Drive Lane 3' uses the same 'Car Lane 3 Mesh' as 'Highway Drive Lane 3', so the edge-line
        // geometry already fits without offset tweaks. Tram / Public Transport variants share the same
        // 3 m width on roads that carry trams or buses.
        private static readonly string[] kCityLaneNames =
        {
            "Car Drive Lane 3",
            "Car Drive Lane 3 - Tram",
            "Public Transport Lane 3",
            "Public Transport Lane 3 - Tram",
        };

        private const string kFallbackMesh = "White Solid Line Mesh";

        private static readonly (string src, string clone)[] kRecipes =
        {
            ("EU Highway Edge Line", "TownRoadLane EU City Edge Line"),
            ("NA Highway Edge Line", "TownRoadLane NA City Edge Line"),
        };

        private PrefabSystem m_PrefabSystem;
        private EntityQuery m_LanePrefabQuery;
        private bool m_Done;

        // Cached managed PrefabBase refs (stable across UpdatePrefab — only the ECS entity behind
        // them gets re-created, see IMPLEMENTATION_PLAN.md K1). Consumers must resolve via
        // CloneEntityEU / CloneEntityNA properties, which call PrefabSystem.GetEntity each time.
        private NetLanePrefab m_CloneEU;
        private NetLanePrefab m_CloneNA;

        /// <summary>Fresh ECS entity for the EU edge-line clone. Always re-resolved through
        /// PrefabSystem so it survives K1 entity re-creations after UpdatePrefab.</summary>
        public Entity CloneEntityEU => (m_CloneEU != null && m_PrefabSystem != null)
            ? m_PrefabSystem.GetEntity(m_CloneEU)
            : Entity.Null;

        /// <summary>Fresh ECS entity for the NA edge-line clone — same K1-safe pattern as EU.</summary>
        public Entity CloneEntityNA => (m_CloneNA != null && m_PrefabSystem != null)
            ? m_PrefabSystem.GetEntity(m_CloneNA)
            : Entity.Null;

        /// <summary>Names of the marking prefabs this system creates/updates — exposed for diagnostics.</summary>
        public static IEnumerable<string> CreatedPrefabNames { get { foreach (var r in kRecipes) yield return r.clone; } }

        protected override void OnCreate()
        {
            base.OnCreate();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_LanePrefabQuery = GetEntityQuery(ComponentType.ReadOnly<PrefabData>(), ComponentType.ReadOnly<NetLaneData>());
            RequireForUpdate(m_LanePrefabQuery);
        }

        protected override void OnUpdate()
        {
            if (m_Done) return;
            m_Done = true;
            Enabled = false;
            if (Mod.Settings != null && !Mod.Settings.EdgeLineEnabled)
            {
                log.Info("EdgeLineCloneSystem: disabled in settings — skipping");
                return;
            }
            try { ApplyOrUpdate(); }
            catch (Exception e) { log.Error(e, "EdgeLineCloneSystem failed"); }
        }

        /// <summary>
        /// Creates the edge-line prefabs (first call) or refreshes their mesh/SecondaryLane to match the
        /// current settings (later calls). Safe to call from MarkingToggleSystem on reapply.
        /// When EdgeLineEnabled is false (set externally then reapply triggered), call
        /// <see cref="StripHostingIfDisabled"/> instead to clear hosting without recreating prefabs.
        /// </summary>
        public void ApplyOrUpdate()
        {
            string meshName = Mod.Settings?.EdgeLineMeshName() ?? kFallbackMesh;

            // Resolve every prefab we need by name in one pass over NetLanePrefab entities.
            var wantedLanes = new HashSet<string>(kCityLaneNames);
            foreach (var r in kRecipes) { wantedLanes.Add(r.src); wantedLanes.Add(r.clone); }
            var laneByName = new Dictionary<string, NetLanePrefab>();
            var laneEnts = m_LanePrefabQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < laneEnts.Length; i++)
            {
                if (!m_PrefabSystem.TryGetPrefab<NetLanePrefab>(laneEnts[i], out var lane) || lane == null) continue;
                if (wantedLanes.Contains(lane.name) && !laneByName.ContainsKey(lane.name)) laneByName[lane.name] = lane;
            }
            laneEnts.Dispose();

            var cityLanes = ResolveList(laneByName, kCityLaneNames, "city host lane");
            if (cityLanes.Count == 0) { log.Warn("no city host lanes found — aborting"); return; }

            var meshByName = ResolveMeshes(new[] { meshName, kFallbackMesh });
            RenderPrefab mesh = PickMesh(meshByName, meshName, kFallbackMesh, "edge line");

            int touched = 0;
            foreach (var (srcName, cloneName) in kRecipes)
            {
                if (!laneByName.TryGetValue(cloneName, out var cloneBase) || cloneBase == null)
                {
                    if (!laneByName.TryGetValue(srcName, out var src) || !(src is NetLaneGeometryPrefab) || !src.TryGet<SecondaryLane>(out _))
                    { log.Warn($"source '{srcName}' missing/invalid — can't create '{cloneName}'"); continue; }
                    cloneBase = m_PrefabSystem.DuplicatePrefab(src, cloneName) as NetLanePrefab;
                    laneByName[cloneName] = cloneBase;
                }
                if (cloneBase == null || !cloneBase.TryGet<SecondaryLane>(out var sec)) { log.Warn($"'{cloneName}' has no SecondaryLane — skipping"); continue; }

                // Replace any host arrays the source ('EU/NA Highway Edge Line') brought along; our clone
                // is for city lanes only, so we drop the highway-lane entries that came with DuplicatePrefab.
                sec.m_LeftLanes = MakeCityLaneInfos(cityLanes);
                sec.m_RightLanes = Array.Empty<SecondaryLaneInfo>();
                sec.m_CrossingLanes = Array.Empty<SecondaryLaneInfo2>();
                sec.m_CanFlipSides = true;

                int swapped = SwapMesh(cloneBase, mesh);
                m_PrefabSystem.UpdatePrefab(cloneBase);
                // Stash the managed clone ref for the phase-4 tool. K1-safe: getter resolves the
                // current ECS entity through PrefabSystem on every access, so re-creations are fine.
                if (cloneName.StartsWith("TownRoadLane EU")) m_CloneEU = cloneBase;
                else m_CloneNA = cloneBase;
                touched++;
                log.Info($"applied '{cloneName}': hosts={cityLanes.Count}*2 mesh='{(mesh != null ? mesh.name : "<source>")}' swapped={swapped}");
            }

            log.Info($"EdgeLineCloneSystem: applied {touched} prefab(s) (mesh='{meshName}')");
        }

        /// <summary>
        /// K8 OFF→ON→OFF path: if the user disabled the feature after an earlier session created our
        /// clones, this strips their hosting so they stop drawing without removing the prefab entities.
        /// Called by MarkingToggleSystem when EdgeLineEnabled is false at reapply time.
        /// </summary>
        public void StripHostingIfDisabled()
        {
            if (Mod.Settings != null && Mod.Settings.EdgeLineEnabled) return; // still on, nothing to do

            var wantedLanes = new HashSet<string>();
            foreach (var r in kRecipes) wantedLanes.Add(r.clone);
            var laneEnts = m_LanePrefabQuery.ToEntityArray(Allocator.Temp);
            int stripped = 0;
            for (int i = 0; i < laneEnts.Length; i++)
            {
                if (!m_PrefabSystem.TryGetPrefab<NetLanePrefab>(laneEnts[i], out var lane) || lane == null) continue;
                if (!wantedLanes.Contains(lane.name)) continue;
                if (!lane.TryGet<SecondaryLane>(out var sec)) continue;
                if (sec.m_LeftLanes != null && sec.m_LeftLanes.Length > 0)
                {
                    sec.m_LeftLanes = Array.Empty<SecondaryLaneInfo>();
                    m_PrefabSystem.UpdatePrefab(lane);
                    stripped++;
                    log.Info($"cleared edge-line hosting on '{lane.name}' (feature disabled)");
                }
            }
            laneEnts.Dispose();
            log.Info($"EdgeLineCloneSystem: stripped {stripped} clone(s)");
        }

        private static int SwapMesh(NetLanePrefab prefab, RenderPrefab mesh)
        {
            if (mesh == null || !(prefab is NetLaneGeometryPrefab g) || g.m_Meshes == null) return 0;
            int n = 0;
            for (int m = 0; m < g.m_Meshes.Length; m++)
                if (g.m_Meshes[m].m_Mesh != null) { g.m_Meshes[m].m_Mesh = mesh; n++; }
            return n;
        }

        private Dictionary<string, RenderPrefab> ResolveMeshes(IEnumerable<string> names)
        {
            var wanted = new HashSet<string>();
            foreach (var n in names) if (!string.IsNullOrEmpty(n)) wanted.Add(n);
            var result = new Dictionary<string, RenderPrefab>();
            var meshQuery = GetEntityQuery(ComponentType.ReadOnly<PrefabData>(), ComponentType.ReadOnly<MeshData>());
            var ents = meshQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < ents.Length; i++)
                if (m_PrefabSystem.TryGetPrefab<RenderPrefab>(ents[i], out var rp) && rp != null && wanted.Contains(rp.name) && !result.ContainsKey(rp.name))
                    result[rp.name] = rp;
            ents.Dispose();
            return result;
        }

        private static RenderPrefab PickMesh(Dictionary<string, RenderPrefab> byName, string wanted, string fallback, string what)
        {
            if (!string.IsNullOrEmpty(wanted) && byName.TryGetValue(wanted, out var rp) && rp != null) return rp;
            if (byName.TryGetValue(fallback, out var fb) && fb != null)
            { log.Warn($"{what} mesh '{wanted}' not found (G87 not installed?) — falling back to '{fallback}'"); return fb; }
            log.Warn($"{what} mesh '{wanted}' and fallback '{fallback}' both missing — keeping source mesh");
            return null;
        }

        private List<NetLanePrefab> ResolveList(Dictionary<string, NetLanePrefab> byName, string[] names, string what)
        {
            var list = new List<NetLanePrefab>();
            foreach (var n in names)
                if (byName.TryGetValue(n, out var p) && p != null) list.Add(p);
                else log.Warn($"{what} '{n}' not found — skipping it");
            return list;
        }

        /// <summary>
        /// Two SecondaryLaneInfo entries per city lane, matching what vanilla uses for 'Highway Drive Lane 3':
        ///   - { RequireSafe } draws the edge line on straight segments.
        ///   - { RequireMerge, RequireSafeMaster } continues the line through merges (onramps, width
        ///     transitions) — the master side of a merge that continues the safe edge.
        /// Without the second entry the line would stop at every merge point.
        /// </summary>
        private static SecondaryLaneInfo[] MakeCityLaneInfos(IReadOnlyList<NetLanePrefab> lanes)
        {
            var arr = new SecondaryLaneInfo[lanes.Count * 2];
            for (int i = 0; i < lanes.Count; i++)
            {
                arr[i * 2]     = new SecondaryLaneInfo { m_Lane = lanes[i], m_RequireSafe = true };
                arr[i * 2 + 1] = new SecondaryLaneInfo { m_Lane = lanes[i], m_RequireMerge = true, m_RequireSafeMaster = true };
            }
            return arr;
        }
    }
}
