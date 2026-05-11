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
    /// Makes ordinary city roads (3 m car lanes) get the curb-side edge marking line, the same way highway roads do.
    ///
    /// In CS2 the "this marking line attaches to those lane prefabs" relation is declared on the marking prefab via
    /// its <c>SecondaryLane</c> component (m_LeftLanes / m_RightLanes). The vanilla highway edge line
    /// ('EU Highway Edge Line' / 'NA Highway Edge Line') lists only the 'Highway Drive Lane *' prefabs, so the city
    /// drive lane 'Car Drive Lane 3' never gets it.
    ///
    /// We do NOT modify the vanilla edge-line prefab — re-baking it (which is what makes the change take effect on
    /// existing roads) would also disturb every road using 'Highway Drive Lane *', and some unusual Road Builder
    /// roads (e.g. a highway-based RB road with angled parking) crash SecondaryLaneSystem when re-laid-out. Instead
    /// we CLONE the vanilla edge line into our own prefab whose only host is 'Car Drive Lane 3', then re-bake the
    /// clone — that only touches roads using 'Car Drive Lane 3' (ordinary city Small/Medium roads), not highways.
    /// The clone reuses the same 'Car Lane 3 Mesh', so the line geometry already fits a 3 m lane; SecondaryLaneSystem
    /// then renders it along the curb and handles parking pockets / sidewalk insets / intersections by itself.
    /// </summary>
    public partial class EdgeMarkingPatchSystem : GameSystemBase
    {
        private static readonly ILog log = Mod.log;

        // Vanilla curb-side edge-line prefabs (one per region theme; the game picks via ThemeObject). We clone these.
        private static readonly (string vanilla, string clone)[] kEdgeLines =
        {
            ("EU Highway Edge Line", "TownRoadLane EU City Edge Line"),
            ("NA Highway Edge Line", "TownRoadLane NA City Edge Line"),
        };

        // City drive-lane prefab(s) our cloned edge line hosts. Only the plain 'Car Drive Lane 3' — the '- Tram' /
        // 'Public Transport Lane 3' variants are intentionally excluded (kept the blast radius small).
        private const string kCityLaneName = "Car Drive Lane 3";

        private PrefabSystem m_PrefabSystem;
        private EntityQuery m_LanePrefabQuery;
        private bool m_Done;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_LanePrefabQuery = GetEntityQuery(
                ComponentType.ReadOnly<PrefabData>(),
                ComponentType.ReadOnly<NetLaneData>());
            RequireForUpdate(m_LanePrefabQuery);
        }

        protected override void OnUpdate()
        {
            if (m_Done) return;
            m_Done = true;
            Enabled = false;

            if (Mod.Settings != null && !Mod.Settings.EdgeLineEnabled)
            {
                log.Info("EdgeMarkingPatchSystem: disabled in settings — skipping");
                return;
            }

            try
            {
                Patch();
            }
            catch (Exception e)
            {
                log.Error(e, "EdgeMarkingPatchSystem failed");
            }
        }

        private void Patch()
        {
            // Resolve the vanilla edge-line prefabs and the city drive lane, by name.
            NetLanePrefab cityLane = null;
            var byName = new Dictionary<string, NetLanePrefab>();
            var wanted = new HashSet<string> { kCityLaneName };
            foreach (var (v, _) in kEdgeLines) wanted.Add(v);

            var entities = m_LanePrefabQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < entities.Length; i++)
            {
                if (!m_PrefabSystem.TryGetPrefab<NetLanePrefab>(entities[i], out var lane) || lane == null) continue;
                if (wanted.Contains(lane.name) && !byName.ContainsKey(lane.name)) byName[lane.name] = lane;
            }
            entities.Dispose();

            if (!byName.TryGetValue(kCityLaneName, out cityLane) || cityLane == null)
            {
                log.Warn($"city lane prefab '{kCityLaneName}' not found — nothing to do");
                return;
            }

            int made = 0;
            foreach (var (vanillaName, cloneName) in kEdgeLines)
            {
                if (!byName.TryGetValue(vanillaName, out var vanilla) || vanilla == null)
                {
                    log.Warn($"edge-line prefab '{vanillaName}' not found — skipping");
                    continue;
                }
                if (!vanilla.TryGet<SecondaryLane>(out _))
                {
                    log.Warn($"'{vanillaName}' has no SecondaryLane component — skipping");
                    continue;
                }

                var clone = m_PrefabSystem.DuplicatePrefab(vanilla, cloneName); // Clone + Remove<ObsoleteIdentifiers> + AddPrefab
                if (!clone.TryGet<SecondaryLane>(out var sec))
                {
                    log.Warn($"clone '{cloneName}' lost its SecondaryLane component — skipping");
                    continue;
                }
                // Our clone hosts ONLY the city drive lane (one plain {RequireSafe} entry), nothing else.
                sec.m_LeftLanes = new[] { new SecondaryLaneInfo { m_Lane = cityLane, m_RequireSafe = true } };
                sec.m_RightLanes = Array.Empty<SecondaryLaneInfo>();
                sec.m_CrossingLanes = Array.Empty<SecondaryLaneInfo2>();
                m_PrefabSystem.UpdatePrefab(clone); // re-bake → adds our clone to 'Car Drive Lane 3's SecondaryNetLane buffer
                made++;
                log.Info($"EdgeMarkingPatchSystem: created '{cloneName}' from '{vanillaName}', hosting '{kCityLaneName}'");
            }

            log.Info($"EdgeMarkingPatchSystem: done, {made} city edge-line prefab(s) created");
        }
    }
}
