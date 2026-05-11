using System;
using Colossal.Logging;
using Game;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;

namespace TownRoadLane
{
    /// <summary>
    /// Handles the "Reapply markings" button: re-runs <see cref="ParkingMarkingPatchSystem.ApplyOrUpdate"/> so the
    /// parking-marking prefabs pick up the currently-selected style, then marks road edges <c>Updated</c> so the net
    /// pipeline (GeometrySystem → NetCompositionSystem → LaneSystem → SecondaryLaneSystem → rendering) rebuilds and
    /// the new line meshes show up on existing roads. Only ever triggered explicitly by the button.
    ///
    /// IMPORTANT: it skips edges of Road Builder–generated roads (prefab name like <c>r&lt;guid&gt;-&lt;steamid&gt;</c>).
    /// Forcing those to re-build their lanes can crash SecondaryLaneSystem (some RB road configs — e.g. a highway-based
    /// RB road with angled parking — don't survive re-layout). RB roads will pick up the new style on the next game
    /// load instead, like the styles always do.
    /// </summary>
    public partial class MarkingReapplySystem : GameSystemBase
    {
        private static readonly ILog log = Mod.log;

        private PrefabSystem m_PrefabSystem;
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
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            // Road edges: have EdgeGeometry + Edge + a PrefabRef (so we can read the road prefab). Exclude things
            // already mid-edit / deleted.
            m_RoadEdgeQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<EdgeGeometry>(), ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<PrefabRef>() },
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

                var edges = m_RoadEdgeQuery.ToEntityArray(Allocator.Temp);
                int marked = 0, skippedRb = 0;
                for (int i = 0; i < edges.Length; i++)
                {
                    var name = "<?>";
                    if (EntityManager.HasComponent<PrefabRef>(edges[i]))
                    {
                        var pe = EntityManager.GetComponentData<PrefabRef>(edges[i]).m_Prefab;
                        if (m_PrefabSystem.TryGetPrefab<PrefabBase>(pe, out var p) && p != null) name = p.name;
                    }
                    if (LooksLikeRoadBuilder(name)) { skippedRb++; continue; }
                    EntityManager.AddComponent<Updated>(edges[i]);
                    marked++;
                }
                edges.Dispose();
                log.Info($"reapply: refreshed parking-marking prefabs; marked {marked} road edge(s) for rebuild, skipped {skippedRb} Road-Builder edge(s)");
            }
            catch (Exception e) { log.Error(e, "MarkingReapplySystem failed"); }
        }

        /// <summary>Road Builder names its generated road prefabs like "r&lt;guid&gt;-&lt;steamid&gt;".</summary>
        private static bool LooksLikeRoadBuilder(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length < 20) return false;
            if (name[0] != 'r' && name[0] != 'R') return false;
            int dashes = 0;
            foreach (var c in name) if (c == '-') dashes++;
            return dashes >= 4;
        }
    }
}
