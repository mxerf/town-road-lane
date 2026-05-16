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
    /// Wires up parallel street-parking markings on ordinary city roads without cloning prefabs.
    ///
    /// CS2 marking prefabs (e.g. 'EU Car Bay Line') declare which lane prefabs they attach to via
    /// the managed <see cref="SecondaryLane"/> component on the marking itself —
    /// <see cref="SecondaryLane.m_LeftLanes"/> / <see cref="SecondaryLane.m_RightLanes"/>. The vanilla
    /// 'Car Bay Line' already has <c>Car Drive Lane 3</c> (+ tram / bus variants) on the LEFT side,
    /// but its RIGHT side only lists <c>Car Bay Lane 3</c> — the special "bay" lane used by
    /// Asymmetric Avenue. Ordinary parallel street parking uses <c>Parking Lane 2</c>, so vanilla's
    /// pair search never matches it and the marking never gets drawn on a Small Road with parking.
    ///
    /// Fix: append <c>Parking Lane 2</c> to <c>m_RightLanes</c>, then call <c>UpdatePrefab</c> to
    /// re-bake the marking's lane data. The vanilla pair-matching pipeline in our
    /// <see cref="CustomSecondaryLaneSystem"/> (a copy of Game.Net.SecondaryLaneSystem) then handles
    /// geometry, sides and intersections automatically — same path that draws the line on
    /// Asymmetric Avenue, just with an extra accepted lane prefab on the right.
    ///
    /// We only edit MARKING prefabs — never drive-lane prefabs. Cloning drive-lane prefabs at
    /// runtime destabilises the engine (the bug that killed the v1.x per-segment upgrade); editing
    /// marking prefabs was stable across v1.x and remains stable here.
    /// </summary>
    public partial class ParkingLineLinkSystem : GameSystemBase
    {
        private static readonly ILog log = Mod.log;

        // Marking prefabs we patch (one per region theme; the game picks the active one via ThemeObject).
        private static readonly string[] kCarBayLineNames = { "EU Car Bay Line", "NA Car Bay Line" };

        // The lane prefab that represents a parallel-parking zone on ordinary city roads.
        // (Asymmetric Avenue uses 'Car Bay Lane 3' instead, which vanilla already wires up.)
        private const string kParkingLaneName = "Parking Lane 2";

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

            if (Mod.Settings != null && !Mod.Settings.ParkingLineEnabled)
            {
                log.Info("ParkingLineLinkSystem: disabled in settings — skipping");
                return;
            }

            try { Patch(); }
            catch (Exception e) { log.Error(e, "ParkingLineLinkSystem failed"); }
        }

        private void Patch()
        {
            // Resolve the prefabs we need in one pass.
            NetLanePrefab parkingLane = null;
            var carBayLines = new Dictionary<string, NetLanePrefab>();
            var entities = m_LanePrefabQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < entities.Length; i++)
            {
                if (!m_PrefabSystem.TryGetPrefab<NetLanePrefab>(entities[i], out var lane) || lane == null) continue;
                if (lane.name == kParkingLaneName && parkingLane == null) parkingLane = lane;
                else foreach (var n in kCarBayLineNames)
                        if (lane.name == n && !carBayLines.ContainsKey(n)) { carBayLines[n] = lane; break; }
            }
            entities.Dispose();

            if (parkingLane == null)
            {
                log.Warn($"parking lane '{kParkingLaneName}' not found — aborting");
                return;
            }

            int patched = 0;
            foreach (var markingName in kCarBayLineNames)
            {
                if (!carBayLines.TryGetValue(markingName, out var markingPrefab) || markingPrefab == null)
                {
                    log.Warn($"marking prefab '{markingName}' not found — skipping");
                    continue;
                }
                if (!markingPrefab.TryGet<SecondaryLane>(out var sec))
                {
                    log.Warn($"'{markingName}' has no SecondaryLane component — skipping");
                    continue;
                }

                if (AppendParkingLane(sec, parkingLane))
                {
                    m_PrefabSystem.UpdatePrefab(markingPrefab);
                    patched++;
                    log.Info($"patched '{markingName}': added '{kParkingLaneName}' to m_RightLanes, queued UpdatePrefab");
                }
                else
                {
                    log.Info($"'{markingName}': '{kParkingLaneName}' already in m_RightLanes, nothing to add");
                }
            }

            log.Info($"ParkingLineLinkSystem: done, {patched} marking prefab(s) patched");
        }

        /// <summary>
        /// Appends a single entry for the parking lane to the marking's <c>m_RightLanes</c>.
        /// Returns true if an entry was added. Uses <c>m_RequireSafe = true</c> to match the
        /// convention of the existing <c>Car Bay Lane 3</c> entry.
        /// </summary>
        private static bool AppendParkingLane(SecondaryLane sec, NetLanePrefab parkingLane)
        {
            var existing = sec.m_RightLanes ?? Array.Empty<SecondaryLaneInfo>();
            for (int i = 0; i < existing.Length; i++)
                if (existing[i] != null && existing[i].m_Lane == parkingLane)
                    return false;

            var merged = new SecondaryLaneInfo[existing.Length + 1];
            Array.Copy(existing, merged, existing.Length);
            merged[existing.Length] = new SecondaryLaneInfo { m_Lane = parkingLane, m_RequireSafe = true };
            sec.m_RightLanes = merged;
            return true;
        }
    }
}
