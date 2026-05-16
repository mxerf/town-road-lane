using System;
using Colossal.Logging;
using Game;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using SubLane = Game.Net.SubLane;

namespace TownRoadLane
{
    /// <summary>
    /// Handles the "Reapply markings now" settings button. Two-step:
    ///   1. Re-run <see cref="EdgeLineCloneSystem.ApplyOrUpdate"/> and
    ///      <see cref="ParkingLineCloneSystem.ApplyOrUpdate"/> so the cloned marking prefabs pick up the
    ///      currently-selected style (or strip hosting if the feature was switched off).
    ///   2. Mark every road edge + intersection node <c>Updated</c> so CustomSecondaryLaneSystem
    ///      rebuilds their markings on the next frame and live roads pick up the change.
    ///
    /// On a big city step 2 is a brief freeze, so the system stays idle until the button asks for it.
    /// Road Builder edges are skipped — re-running SecondaryLaneSystem on RB-generated geometry has
    /// historically crashed (commit 5bb0b5e, K5 in IMPLEMENTATION_PLAN.md). RB roads still pick up the
    /// style change on the next game load like styles always do.
    /// </summary>
    public partial class MarkingToggleSystem : GameSystemBase
    {
        private static readonly ILog log = Mod.log;

        private PrefabSystem m_PrefabSystem;
        private EntityQuery m_EdgeQuery;
        private EntityQuery m_NodeQuery;
        private bool m_Requested;

        /// <summary>Called from the settings button. The actual work runs on the next system update.</summary>
        public static void RequestReapply()
        {
            var sys = World.DefaultGameObjectInjectionWorld?.GetExistingSystemManaged<MarkingToggleSystem>();
            if (sys == null) { log.Warn("MarkingToggleSystem not found — cannot reapply"); return; }
            sys.m_Requested = true;
            sys.Enabled = true;
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            // Road edges: have EdgeGeometry + Edge + a PrefabRef. Exclude in-edit / deleted.
            m_EdgeQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<EdgeGeometry>(), ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<PrefabRef>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
            // Intersection nodes: have Node + a SubLane buffer (so secondary lanes can attach).
            m_NodeQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Node>(), ComponentType.ReadOnly<SubLane>() },
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
                // Step 1: refresh the cloned marking prefabs against current settings. Each clone system
                // decides on its own whether to apply (feature enabled, run ApplyOrUpdate) or strip
                // hosting (feature disabled, call the dedicated strip path where one exists).
                var edgeSys = World.GetExistingSystemManaged<EdgeLineCloneSystem>();
                if (edgeSys != null)
                {
                    if (Mod.Settings != null && Mod.Settings.EdgeLineEnabled) edgeSys.ApplyOrUpdate();
                    else edgeSys.StripHostingIfDisabled();
                }
                else log.Warn("EdgeLineCloneSystem not found — edge-line prefabs not refreshed");

                var parkSys = World.GetExistingSystemManaged<ParkingLineCloneSystem>();
                if (parkSys != null)
                {
                    if (Mod.Settings != null && Mod.Settings.ParkingMarkingsEnabled) parkSys.ApplyOrUpdate();
                    // ParkingLineCloneSystem doesn't have a separate strip helper — ApplyOrUpdate handles
                    // the end-tick None case internally; for the longitudinal "feature off" case we'd want
                    // an equivalent strip pass. TODO if/when ParkingMarkingsEnabled toggling is exposed
                    // as a runtime concern (currently it's a session-start setting).
                }
                else log.Warn("ParkingLineCloneSystem not found — parking-line prefabs not refreshed");

                // Step 2: mark every non-RB edge and node Updated so CustomSecondaryLaneSystem rebuilds
                // markings on them. RB roads pick up the prefab change on the next game load.
                var edges = m_EdgeQuery.ToEntityArray(Allocator.Temp);
                var nodes = m_NodeQuery.ToEntityArray(Allocator.Temp);

                int touched = 0, skippedRb = 0;
                MarkUpdated(edges, ref touched, ref skippedRb);
                MarkUpdated(nodes, ref touched, ref skippedRb);
                log.Info($"reapply: refreshed clone prefabs, marked {touched} entities Updated, skipped {skippedRb} RB edge/node(s)");

                edges.Dispose();
                nodes.Dispose();
            }
            catch (Exception e) { log.Error(e, "MarkingToggleSystem failed"); }
        }

        private void MarkUpdated(NativeArray<Entity> entities, ref int touched, ref int skippedRb)
        {
            for (int i = 0; i < entities.Length; i++)
            {
                var e = entities[i];
                if (LooksLikeRoadBuilderRoad(e)) { skippedRb++; continue; }
                if (!EntityManager.HasComponent<Updated>(e))
                    EntityManager.AddComponent<Updated>(e);
                touched++;
            }
        }

        /// <summary>Road Builder names its generated road prefabs like "r&lt;guid&gt;-&lt;steamid&gt;".</summary>
        private bool LooksLikeRoadBuilderRoad(Entity e)
        {
            if (!EntityManager.HasComponent<PrefabRef>(e)) return false;
            var pe = EntityManager.GetComponentData<PrefabRef>(e).m_Prefab;
            if (!m_PrefabSystem.TryGetPrefab<PrefabBase>(pe, out var p) || p == null) return false;
            var name = p.name;
            if (string.IsNullOrEmpty(name) || name.Length < 20) return false;
            if (name[0] != 'r' && name[0] != 'R') return false;
            int dashes = 0;
            foreach (var c in name) if (c == '-') dashes++;
            return dashes >= 4;
        }
    }
}
