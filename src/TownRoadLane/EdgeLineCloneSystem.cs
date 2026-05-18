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
    /// already have, AND clones extra marking prefabs (one per <see cref="MarkingStyle"/>) so the
    /// per-line UI tool can pick a style at draw time.
    ///
    /// v1.1 (commit 342afa4) patched the vanilla 'EU/NA Highway Edge Line' in place — appending city
    /// drive lanes to its m_LeftLanes. v2 instead CLONES that prefab so we can swap its mesh
    /// independently of vanilla (G87 custom mesh support); the vanilla highway edge line stays
    /// untouched. The clones reference vanilla 'Car Drive Lane 3' lanes in m_LeftLanes, so
    /// NetInitializeSystem reverse-indexes our entries onto the vanilla lane prefab's SecondaryNetLane
    /// buffer — and any road (including Road Builder roads) that uses 'Car Drive Lane 3' picks up our
    /// markings automatically. See RESEARCH_road_builder.md §5.
    ///
    /// Stage 5c extension: for each style added to <see cref="MarkingStyle"/>, register a
    /// (sourcePrefab, clonedPrefabName) recipe in <see cref="kStyleRecipes"/>. Source prefab must
    /// be a vanilla NetLaneGeometryPrefab with SecondaryLane (else clone is skipped with a warn).
    /// Each style gets its own EU + NA clone. Lookup at emission time goes via
    /// <see cref="GetCloneEntity(MarkingStyle, bool)"/>.
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

        // Per-style clone recipe. Source prefab name on the left; clone name (= what shows up in
        // PrefabSystem) on the right. Fallback-mesh name is the asset we revert to if the
        // user-picked / G87 mesh isn't loaded. Same arrangement for every style — solid uses
        // White Solid Line Mesh, dashed uses White Dashed Line Mesh.
        //
        // To add a style:
        //   1. Append entry to MarkingStyle enum.
        //   2. Append rows here for EU + NA.
        //   3. (Optional) add a per-style settings dropdown if the user should pick the mesh.
        private struct StyleRecipe
        {
            public MarkingStyle style;
            public bool         isNA;
            public string       sourcePrefabName;
            public string       cloneName;
            public string       fallbackMesh;
            // True = host on city Car Drive Lane 3 (the v1.1 "edge line on city roads" feature).
            // False = clone exists ONLY as a spawn-archetype source for the Phase-4 emission system;
            // it must NOT inherit vanilla hosting from the source prefab or it gets auto-drawn on
            // every city road as part of the vanilla SecondaryLane pass.
            //
            // Why this matters: Dashed clones source from "EU Car Lane Line", which is a vanilla
            // lane-divider prefab. If we leave its hosting intact OR add city-lane hosting, every
            // city road grows a dashed line in addition to its normal markings — observed as
            // "странные неконсистентные полосы" after Stage 5c rolled out.
            public bool         hostOnCityLanes;
        }

        // G87 mesh names — prefixes from Setting.cs (kept here as full strings to avoid a
        // cross-class dependency for a value that's bytes long). If G87 isn't installed, the
        // ResolveMeshes pass returns no match and PickMesh falls back to the vanilla mesh
        // matching this recipe's fallbackMesh field (so G87 styles silently degrade to vanilla
        // — no crash, just less variety).
        private const string kG87Prefix = "G87 UK Road Markings RoadMarking G87 ";
        private const string kG87SolidMesh  = kG87Prefix + "UK Carriageway Line White NetLaneDecal_RenderPrefab";
        private const string kG87DashedMesh = kG87Prefix + "UK Carriageway Line White Dashed NetLaneDecal_RenderPrefab";

        private static readonly StyleRecipe[] kStyleRecipes =
        {
            new() { style = MarkingStyle.Solid,     isNA = false, sourcePrefabName = "EU Highway Edge Line", cloneName = "TownRoadLane EU City Edge Line",       fallbackMesh = "White Solid Line Mesh",  hostOnCityLanes = true  },
            new() { style = MarkingStyle.Solid,     isNA = true,  sourcePrefabName = "NA Highway Edge Line", cloneName = "TownRoadLane NA City Edge Line",       fallbackMesh = "White Solid Line Mesh",  hostOnCityLanes = true  },
            new() { style = MarkingStyle.Dashed,    isNA = false, sourcePrefabName = "EU Car Lane Line",     cloneName = "TownRoadLane EU City Dashed Line",     fallbackMesh = "White Dashed Line Mesh", hostOnCityLanes = false },
            new() { style = MarkingStyle.Dashed,    isNA = true,  sourcePrefabName = "NA Car Lane Line",     cloneName = "TownRoadLane NA City Dashed Line",     fallbackMesh = "White Dashed Line Mesh", hostOnCityLanes = false },
            // G87 styles: use 'Car Bay Line' as the source prefab. Same prefab the parking-line
            // clone uses, and parking renders G87 decals brightly while edge-line-source G87s look
            // washed out. Suspected cause: Car Bay Line's NetLaneMeshInfo has the LOD chain /
            // width / material flags G87 was designed against; Highway Edge Line and Car Lane Line
            // have different layouts that scale the G87 decal opacity weirdly.
            new() { style = MarkingStyle.G87Solid,  isNA = false, sourcePrefabName = "EU Car Bay Line", cloneName = "TownRoadLane EU City G87 Solid Line",  fallbackMesh = kG87SolidMesh,  hostOnCityLanes = false },
            new() { style = MarkingStyle.G87Solid,  isNA = true,  sourcePrefabName = "NA Car Bay Line", cloneName = "TownRoadLane NA City G87 Solid Line",  fallbackMesh = kG87SolidMesh,  hostOnCityLanes = false },
            new() { style = MarkingStyle.G87Dashed, isNA = false, sourcePrefabName = "EU Car Bay Line", cloneName = "TownRoadLane EU City G87 Dashed Line", fallbackMesh = kG87DashedMesh, hostOnCityLanes = false },
            new() { style = MarkingStyle.G87Dashed, isNA = true,  sourcePrefabName = "NA Car Bay Line", cloneName = "TownRoadLane NA City G87 Dashed Line", fallbackMesh = kG87DashedMesh, hostOnCityLanes = false },
            // Double Solid — single vanilla mesh "White Double Solid Line Mesh" cloned onto the
            // standard Car Bay Line archetype. Two parallel lines come from the mesh itself, not
            // from spawning two entities, so the emission pipeline stays simple.
            new() { style = MarkingStyle.DoubleSolid, isNA = false, sourcePrefabName = "EU Car Bay Line", cloneName = "TownRoadLane EU City Double Solid Line", fallbackMesh = "White Double Solid Line Mesh", hostOnCityLanes = false },
            new() { style = MarkingStyle.DoubleSolid, isNA = true,  sourcePrefabName = "NA Car Bay Line", cloneName = "TownRoadLane NA City Double Solid Line", fallbackMesh = "White Double Solid Line Mesh", hostOnCityLanes = false },
        };

        private PrefabSystem m_PrefabSystem;
        private EntityQuery m_LanePrefabQuery;
        private bool m_Done;

        // Cached managed PrefabBase refs per (style, theme). Stable across UpdatePrefab —
        // only the ECS entity behind them gets re-created, see IMPLEMENTATION_PLAN.md K1.
        // Resolve to a live ECS entity via GetCloneEntity(...).
        private readonly Dictionary<(MarkingStyle, bool), NetLanePrefab> m_ClonesByStyle = new();

        /// <summary>Fresh ECS entity for the clone matching the given style + theme. Returns
        /// Entity.Null if that combo isn't loaded yet (caller should fall back to Solid).
        /// K1-safe: re-resolves through PrefabSystem on every call so post-UpdatePrefab entity
        /// re-creations don't leave us with stale handles.</summary>
        public Entity GetCloneEntity(MarkingStyle style, bool isNA)
        {
            if (m_PrefabSystem == null) return Entity.Null;
            return m_ClonesByStyle.TryGetValue((style, isNA), out var pb) && pb != null
                ? m_PrefabSystem.GetEntity(pb)
                : Entity.Null;
        }

        /// <summary>Back-compat alias for callers that pre-date the style API. Solid + EU theme —
        /// matches the original CloneEntityEU getter. Kept so this commit doesn't ripple into
        /// MarkingPairEmissionSystem (which is dead-code-but-still-compiled) or anything that
        /// still references the old name.</summary>
        public Entity CloneEntityEU => GetCloneEntity(MarkingStyle.Solid, isNA: false);
        public Entity CloneEntityNA => GetCloneEntity(MarkingStyle.Solid, isNA: true);

        /// <summary>Managed NetLanePrefab refs for the solid EU/NA clones — kept for callers that
        /// need to acquire Material via NetLaneMeshInfo.m_Mesh.ObtainMaterial(). Stable across
        /// UpdatePrefab. New style-aware code should prefer working via Entity through
        /// <see cref="GetCloneEntity"/>.</summary>
        public NetLanePrefab ClonePrefabEU => m_ClonesByStyle.TryGetValue((MarkingStyle.Solid, false), out var p) ? p : null;
        public NetLanePrefab ClonePrefabNA => m_ClonesByStyle.TryGetValue((MarkingStyle.Solid, true),  out var p) ? p : null;

        /// <summary>Names of the marking prefabs this system creates/updates — exposed for diagnostics.</summary>
        public static IEnumerable<string> CreatedPrefabNames { get { foreach (var r in kStyleRecipes) yield return r.cloneName; } }

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
        /// Creates (or refreshes) every style clone defined in <see cref="kStyleRecipes"/>.
        /// Idempotent — safe to call from MarkingToggleSystem on reapply.
        /// When EdgeLineEnabled is false (set externally then reapply triggered), call
        /// <see cref="StripHostingIfDisabled"/> instead to clear hosting without recreating prefabs.
        /// </summary>
        public void ApplyOrUpdate()
        {
            // User-pickable mesh from settings only governs the SOLID style for now; dashed
            // always uses its fallback. When per-style mesh dropdowns are added in a future stage,
            // this picks per-recipe.
            string solidMeshName = Mod.Settings?.EdgeLineMeshName() ?? "White Solid Line Mesh";

            // Resolve every prefab we need by name in one pass over NetLanePrefab entities.
            var wantedLanes = new HashSet<string>(kCityLaneNames);
            foreach (var r in kStyleRecipes) { wantedLanes.Add(r.sourcePrefabName); wantedLanes.Add(r.cloneName); }
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

            // Collect every distinct mesh name we might need so we resolve all of them in one query pass.
            var meshNames = new HashSet<string> { solidMeshName };
            foreach (var r in kStyleRecipes) meshNames.Add(r.fallbackMesh);
            var meshByName = ResolveMeshes(meshNames);

            int touched = 0;
            foreach (var recipe in kStyleRecipes)
            {
                string wantedMesh = recipe.style == MarkingStyle.Solid ? solidMeshName : recipe.fallbackMesh;
                RenderPrefab mesh = PickMesh(meshByName, wantedMesh, recipe.fallbackMesh, recipe.cloneName);

                if (!laneByName.TryGetValue(recipe.cloneName, out var cloneBase) || cloneBase == null)
                {
                    if (!laneByName.TryGetValue(recipe.sourcePrefabName, out var src) || !(src is NetLaneGeometryPrefab) || !src.TryGet<SecondaryLane>(out _))
                    { log.Warn($"source '{recipe.sourcePrefabName}' missing/invalid — can't create '{recipe.cloneName}'"); continue; }
                    cloneBase = m_PrefabSystem.DuplicatePrefab(src, recipe.cloneName) as NetLanePrefab;
                    laneByName[recipe.cloneName] = cloneBase;
                }
                if (cloneBase == null || !cloneBase.TryGet<SecondaryLane>(out var sec)) { log.Warn($"'{recipe.cloneName}' has no SecondaryLane — skipping"); continue; }

                // Always clear ALL host arrays first — DuplicatePrefab carries the source's hosting,
                // and a vanilla divider prefab cloned for our archetype-source use would otherwise
                // re-host itself on whatever the vanilla source originally targeted.
                sec.m_LeftLanes = Array.Empty<SecondaryLaneInfo>();
                sec.m_RightLanes = Array.Empty<SecondaryLaneInfo>();
                sec.m_CrossingLanes = Array.Empty<SecondaryLaneInfo2>();
                sec.m_CanFlipSides = false;

                int hostCount = 0;
                if (recipe.hostOnCityLanes)
                {
                    // Edge-line recipe (v1.1 behaviour): host on Car Drive Lane 3 + variants, both
                    // RequireSafe entry and RequireMerge+RequireSafeMaster entry per lane.
                    sec.m_LeftLanes = MakeCityLaneInfos(cityLanes);
                    sec.m_CanFlipSides = true;
                    hostCount = cityLanes.Count * 2;
                }
                // else: clone exists only as a spawn-archetype source for Phase-4 emission;
                // intentionally hosted on nothing so vanilla SecondaryLaneSystem won't draw it.

                int swapped = SwapMesh(cloneBase, mesh);
                m_PrefabSystem.UpdatePrefab(cloneBase);

                m_ClonesByStyle[(recipe.style, recipe.isNA)] = cloneBase;
                touched++;
                log.Info($"applied '{recipe.cloneName}' [{recipe.style}/{(recipe.isNA ? "NA" : "EU")}]: hostedEntries={hostCount} mesh='{(mesh != null ? mesh.name : "<source>")}' swapped={swapped}");
            }

            log.Info($"EdgeLineCloneSystem: applied {touched} prefab(s)");
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
            foreach (var r in kStyleRecipes) wantedLanes.Add(r.cloneName);
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
