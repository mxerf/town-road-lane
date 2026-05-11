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
    /// Makes ordinary city roads (3 m car lanes) get the curb-side edge marking line,
    /// the same way highway roads do.
    ///
    /// In CS2 the "this marking line attaches to those lane prefabs" relation is declared
    /// on the marking prefab itself via its <c>SecondaryLane</c> component (m_LeftLanes / m_RightLanes).
    /// The highway edge line ('EU Highway Edge Line' / 'NA Highway Edge Line') lists only the
    /// 'Highway Drive Lane *' prefabs, so the city drive lane 'Car Drive Lane 3' never gets it.
    /// We simply append 'Car Drive Lane 3' (and a couple of its variants) to those edge-line
    /// prefabs' m_LeftLanes — mirroring exactly what is already there for 'Highway Drive Lane 3'
    /// (which, notably, uses the very same 'Car Lane 3 Mesh', so the line geometry already fits a 3 m lane).
    ///
    /// SecondaryLaneSystem then renders the edge line along the curb and handles parking pockets,
    /// sidewalk insets, intersections and merges by itself — no extra work needed.
    /// </summary>
    public partial class EdgeMarkingPatchSystem : GameSystemBase
    {
        private static readonly ILog log = Mod.log;

        // Marking prefabs that draw the curb-side edge line (one per region theme; the game picks via ThemeObject).
        private static readonly string[] kEdgeLinePrefabNames = { "EU Highway Edge Line", "NA Highway Edge Line" };

        // City drive-lane prefabs that should now also get the edge line, with the requirement flags to use.
        // Mirrors the existing 'Highway Drive Lane 3' entries on the edge-line prefabs:
        //   - one plain entry requiring Safe
        //   - one entry requiring Merge + SafeMaster (so it continues correctly through merges)
        private static readonly string[] kCityLaneNames =
        {
            "Car Drive Lane 3",
            "Car Drive Lane 3 - Tram",
            "Public Transport Lane 3",
            "Public Transport Lane 3 - Tram",
        };

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
            // Resolve all the NetLanePrefab instances we need, by name, from the loaded prefab set.
            var byName = new Dictionary<string, NetLanePrefab>();
            var wanted = new HashSet<string>(kEdgeLinePrefabNames);
            foreach (var n in kCityLaneNames) wanted.Add(n);

            var entities = m_LanePrefabQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < entities.Length; i++)
            {
                if (!m_PrefabSystem.TryGetPrefab<NetLanePrefab>(entities[i], out var lane) || lane == null) continue;
                if (wanted.Contains(lane.name) && !byName.ContainsKey(lane.name))
                    byName[lane.name] = lane;
            }
            entities.Dispose();

            // Build the list of (city lane prefab, flags) entries to add.
            var cityLanes = new List<NetLanePrefab>();
            foreach (var n in kCityLaneNames)
            {
                if (byName.TryGetValue(n, out var p) && p != null) cityLanes.Add(p);
                else log.Warn($"city lane prefab '{n}' not found — skipping it");
            }
            if (cityLanes.Count == 0)
            {
                log.Warn("no city lane prefabs resolved; nothing to do");
                return;
            }

            int patched = 0;
            foreach (var edgeName in kEdgeLinePrefabNames)
            {
                if (!byName.TryGetValue(edgeName, out var edgePrefab) || edgePrefab == null)
                {
                    log.Warn($"edge-line prefab '{edgeName}' not found — skipping");
                    continue;
                }
                if (!edgePrefab.TryGet<SecondaryLane>(out var sec))
                {
                    log.Warn($"'{edgeName}' has no SecondaryLane component — skipping");
                    continue;
                }

                int added = AppendCityLanes(sec, cityLanes, edgeName);
                if (added > 0)
                {
                    m_PrefabSystem.UpdatePrefab(edgePrefab);
                    patched++;
                    log.Info($"patched '{edgeName}': added {added} m_LeftLanes entries, queued UpdatePrefab");
                }
            }

            log.Info($"EdgeMarkingPatchSystem: done, {patched} edge-line prefab(s) patched");
        }

        /// <summary>
        /// Appends, for each city lane prefab, the two SecondaryLaneInfo entries used by 'Highway Drive Lane 3'
        /// on this edge line: { RequireSafe } and { RequireMerge, RequireSafeMaster }. Skips entries that already exist.
        /// Returns the number of entries actually added.
        /// </summary>
        private int AppendCityLanes(SecondaryLane sec, List<NetLanePrefab> cityLanes, string edgeName)
        {
            var existing = sec.m_LeftLanes ?? Array.Empty<SecondaryLaneInfo>();

            var toAdd = new List<SecondaryLaneInfo>();
            foreach (var lane in cityLanes)
            {
                if (!HasEntry(existing, lane, safe: true, merge: false, safeMaster: false))
                    toAdd.Add(new SecondaryLaneInfo { m_Lane = lane, m_RequireSafe = true });
                if (!HasEntry(existing, lane, safe: false, merge: true, safeMaster: true))
                    toAdd.Add(new SecondaryLaneInfo { m_Lane = lane, m_RequireMerge = true, m_RequireSafeMaster = true });
            }
            if (toAdd.Count == 0) return 0;

            var merged = new SecondaryLaneInfo[existing.Length + toAdd.Count];
            Array.Copy(existing, merged, existing.Length);
            for (int i = 0; i < toAdd.Count; i++) merged[existing.Length + i] = toAdd[i];
            sec.m_LeftLanes = merged;
            return toAdd.Count;
        }

        private static bool HasEntry(SecondaryLaneInfo[] arr, NetLanePrefab lane, bool safe, bool merge, bool safeMaster)
        {
            for (int i = 0; i < arr.Length; i++)
            {
                var e = arr[i];
                if (e == null || e.m_Lane != lane) continue;
                if (e.m_RequireSafe == safe && e.m_RequireMerge == merge && e.m_RequireSafeMaster == safeMaster)
                    return true;
            }
            return false;
        }
    }
}
