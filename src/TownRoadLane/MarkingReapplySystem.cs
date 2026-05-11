using System;
using Colossal.Logging;
using Game;
using Game.Common;
using Game.Net;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;

namespace TownRoadLane
{
    /// <summary>
    /// Handles the "Reapply markings" button: re-runs <see cref="ParkingMarkingPatchSystem.ApplyOrUpdate"/> so the
    /// parking-marking prefabs pick up the currently-selected style, then marks every road edge <c>Updated</c> so
    /// the net pipeline (GeometrySystem → NetCompositionSystem → LaneSystem → SecondaryLaneSystem → rendering)
    /// rebuilds and the new line meshes show up on existing roads. On a big city this is a brief freeze, so it is
    /// only ever triggered explicitly by the button — never automatically.
    /// </summary>
    public partial class MarkingReapplySystem : GameSystemBase
    {
        private static readonly ILog log = Mod.log;

        private EntityQuery m_RoadEdgeQuery;
        private bool m_Requested;

        /// <summary>Called from the settings button. The actual work runs on the next system update.</summary>
        public static void RequestReapply()
        {
            var sys = World.DefaultGameObjectInjectionWorld?.GetExistingSystemManaged<MarkingReapplySystem>();
            if (sys == null) { log.Warn("MarkingReapplySystem not found — cannot reapply"); return; }
            sys.m_Requested = true;
            sys.Enabled = true;
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            // Road edges: have EdgeGeometry (so GeometrySystem will reprocess them on Updated) and are roads (RoadData
            // is on the prefab, Edge on the instance; we just take everything with EdgeGeometry — bridges/quays too,
            // harmless). Exclude things already mid-edit.
            m_RoadEdgeQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<EdgeGeometry>(), ComponentType.ReadOnly<Edge>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
            Enabled = false; // idle until the button asks
        }

        protected override void OnUpdate()
        {
            if (!m_Requested) { Enabled = false; return; }
            m_Requested = false;
            Enabled = false;

            try
            {
                if (Mod.Settings == null || !Mod.Settings.ParkingMarkingsEnabled)
                {
                    log.Info("reapply requested but parking markings are disabled — nothing to do");
                    return;
                }

                var parkSys = World.GetExistingSystemManaged<ParkingMarkingPatchSystem>();
                if (parkSys != null) parkSys.ApplyOrUpdate();
                else log.Warn("ParkingMarkingPatchSystem not found — prefabs not refreshed");

                int n = m_RoadEdgeQuery.CalculateEntityCount();
                if (n > 0)
                {
                    EntityManager.AddComponent<Updated>(m_RoadEdgeQuery);
                    log.Info($"reapply: refreshed parking-marking prefabs and marked {n} road edge(s) for rebuild");
                }
                else log.Info("reapply: prefabs refreshed; no road edges in the current scene to rebuild");
            }
            catch (Exception e) { log.Error(e, "MarkingReapplySystem failed"); }
        }
    }
}
