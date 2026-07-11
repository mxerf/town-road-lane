using System;
using System.Collections.Generic;
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
    /// Handles the "Reapply markings now" settings button.
    ///
    ///   1. Re-run <see cref="EdgeLineCloneSystem.ApplyOrUpdate"/> and
    ///      <see cref="ParkingLineCloneSystem.ApplyOrUpdate"/> so the cloned marking prefabs pick up
    ///      the currently-selected style (or strip hosting if the feature was switched off).
    ///   2. Add <see cref="BatchesUpdated"/> to every live lane entity whose PrefabRef points at one
    ///      of our clone prefabs — the render system rebuilds their batches against the freshly
    ///      swapped mesh, so a STYLE change is visible immediately. This mirrors what the vanilla
    ///      ReplacePrefabSystem does on prefab mesh replacement.
    ///
    /// History: two earlier designs marked road edges + nodes <c>Updated</c> to force a full
    /// SecondaryLane rebuild. Both crashed: one-shot marking (~6.6k entities in one frame) blew up
    /// native jobs downstream, and even a 128/tick batch died in ModificationBarrier4B's ECB playback
    /// (parallel-writer chains from the SecondaryLane rebuild) — native crash, empty managed stack.
    /// Restyling never needed a structural rebuild in the first place: the lanes stay, only their
    /// render batches go stale. Feature ENABLE/DISABLE (lanes appearing/disappearing on existing
    /// roads) is deliberately deferred to the next save load, where the world spawns lanes against
    /// the refreshed prefabs anyway — same guarantee Road Builder roads always had.
    /// </summary>
    public partial class MarkingToggleSystem : GameSystemBase
    {
        private static readonly ILog log = Mod.log;

        // Lane entities tagged per frame while draining. Batch-refresh is cheap (tag add, render
        // batch rebuild only), but stay polite to the frame budget anyway.
        private const int kBatchPerTick = 1024;

        private PrefabSystem m_PrefabSystem;
        private EntityQuery m_LaneQuery;
        private bool m_Requested;
        // Drain queue for the current reapply request. Persistent allocation, created on demand,
        // disposed when fully drained (or on destroy).
        private NativeList<Entity> m_Pending;
        private int m_PendingCursor;
        private int m_TouchedTotal;

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
            // Live lane entities (our markings are net sublanes). Exclude in-edit / dying.
            m_LaneQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Lane>(), ComponentType.ReadOnly<PrefabRef>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
            Enabled = false; // idle until the button asks
        }

        protected override void OnDestroy()
        {
            if (m_Pending.IsCreated) m_Pending.Dispose();
            base.OnDestroy();
        }

        protected override void OnUpdate()
        {
            try
            {
                if (m_Requested)
                {
                    m_Requested = false;
                    BeginReapply();
                }
                DrainBatch();
            }
            catch (Exception e)
            {
                log.Error(e, "MarkingToggleSystem failed");
                if (m_Pending.IsCreated) m_Pending.Dispose();
                Enabled = false;
            }
        }

        /// <summary>Step 1 (prefab refresh) + capture of the lane drain queue. Runs once per request.</summary>
        private void BeginReapply()
        {
            // Refresh the cloned marking prefabs against current settings. Each clone system
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

            // Collect the prefab entities of OUR clones — every clone prefab is named
            // "TownRoadLane ..." (edge lines + parking lines, EU/NA variants). Lane entities
            // referencing anything else are untouched.
            var ourPrefabs = new HashSet<Entity>();
            var prefabQuery = GetEntityQuery(ComponentType.ReadOnly<PrefabData>(), ComponentType.ReadOnly<NetLaneData>());
            using (var prefabEntities = prefabQuery.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < prefabEntities.Length; i++)
                {
                    if (m_PrefabSystem.TryGetPrefab<PrefabBase>(prefabEntities[i], out var pb) && pb != null
                        && pb.name != null && pb.name.StartsWith("TownRoadLane ", StringComparison.Ordinal))
                    {
                        ourPrefabs.Add(prefabEntities[i]);
                    }
                }
            }

            // Capture the lanes that reference our clones; the queue drains kBatchPerTick per frame.
            if (m_Pending.IsCreated) m_Pending.Dispose();
            m_Pending = new NativeList<Entity>(4096, Allocator.Persistent);
            using (var lanes = m_LaneQuery.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < lanes.Length; i++)
                {
                    var prefab = EntityManager.GetComponentData<PrefabRef>(lanes[i]).m_Prefab;
                    if (ourPrefabs.Contains(prefab)) m_Pending.Add(lanes[i]);
                }
            }
            m_PendingCursor = 0;
            m_TouchedTotal = 0;
            log.Info($"reapply: refreshed clone prefabs ({ourPrefabs.Count} clone prefab(s)), queued {m_Pending.Length} lane(s) for batch refresh ({kBatchPerTick}/tick)");
        }

        /// <summary>Step 2, spread across frames: tag queued lanes <see cref="BatchesUpdated"/> so the
        /// renderer rebuilds their batches against the swapped meshes. Non-cascading — nothing but
        /// the render batch reacts, and CleanUpSystem strips the tag right after.</summary>
        private void DrainBatch()
        {
            if (!m_Pending.IsCreated)
            {
                Enabled = false;
                return;
            }

            int end = Math.Min(m_PendingCursor + kBatchPerTick, m_Pending.Length);
            for (; m_PendingCursor < end; m_PendingCursor++)
            {
                var e = m_Pending[m_PendingCursor];
                if (!EntityManager.Exists(e)) continue;
                if (EntityManager.HasComponent<Deleted>(e)) continue;
                if (!EntityManager.HasComponent<BatchesUpdated>(e))
                    EntityManager.AddComponent<BatchesUpdated>(e);
                m_TouchedTotal++;
            }

            if (m_PendingCursor >= m_Pending.Length)
            {
                log.Info($"reapply: done — batch-refreshed {m_TouchedTotal} lane(s)");
                m_Pending.Dispose();
                Enabled = false;
            }
        }
    }
}
