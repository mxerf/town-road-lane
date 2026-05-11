using System;
using System.Collections.Generic;
using Colossal.Logging;
using Game;
using Game.Buildings;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
// SubLane / SecondaryLane / Upgraded exist in both Game.Net and Game.Prefabs; we want the ECS (Game.Net) ones.
using SubLane = Game.Net.SubLane;
using SecondaryLane = Game.Net.SecondaryLane;
using Upgraded = Game.Net.Upgraded;
using Edge = Game.Net.Edge;

namespace TownRoadLane
{
    /// <summary>
    /// Makes the per-segment "Lane Markings" upgrade actually do something.
    ///
    /// The engine's <see cref="SecondaryLaneSystem"/> draws our city-road markings purely from the lane prefab
    /// (e.g. 'Car Drive Lane 3'); it never looks at an edge's <see cref="Upgraded"/> flags, so we can't suppress
    /// markings through the composition. Instead we let it create the marking sub-lane entities as usual and then,
    /// for any edge that carries our <see cref="MarkingFlags.MarkingsOff"/> bit, remove the marking sub-lanes that
    /// belong to <em>our</em> marking prefabs (the highway edge line + our parking line / end prefabs): we tag the
    /// sub-lane <c>Deleted</c> AND drop it from the edge's <c>SubLane</c> buffer in the same step — leaving a
    /// dangling buffer reference to a <c>Deleted</c> entity is what crashed the renderer the first time. The vanilla
    /// markings on actual highways and the intersection crosswalk markings are left alone because either their
    /// prefab is not in our managed set or the edge does not carry our bit.
    ///
    /// Runs in <see cref="SystemUpdatePhase.Modification5"/> — after <c>ModificationBarrier4B</c> has played back
    /// the command buffer in which <c>SecondaryLaneSystem</c> (phase Modification4B) created the sub-lanes, so they
    /// exist by the time we look. It reacts to the same <c>Updated</c>/<c>Created</c> edge tags <c>SecondaryLaneSystem</c>
    /// does, so every time those markings are rebuilt we remove them again on bit-carrying edges.
    /// </summary>
    public partial class MarkingSuppressSystem : GameSystemBase
    {
        private static readonly ILog log = Mod.log;

        // Marking NetLanePrefab names we own / manage. We only ever suppress sub-lanes whose prefab is one of these.
        private static readonly string[] kVanillaEdgeLineNames = { "EU Highway Edge Line", "NA Highway Edge Line" };

        private PrefabSystem m_PrefabSystem;
        private ModificationBarrier5 m_Barrier;
        private EntityQuery m_EdgeQuery;

        // Set of prefab entities whose sub-lanes we suppress; built lazily once prefabs are loaded.
        private NativeParallelHashSet<Entity> m_ManagedMarkingPrefabs;
        private bool m_Resolved;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_Barrier = World.GetOrCreateSystemManaged<ModificationBarrier5>();
            m_ManagedMarkingPrefabs = new NativeParallelHashSet<Entity>(8, Allocator.Persistent);

            // Edges that just (re)built their lanes: have a SubLane buffer + an Upgraded component, tagged Updated
            // or Created, not currently being edited / deleted / part of a building or outside connection.
            m_EdgeQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<SubLane>(),
                    ComponentType.ReadOnly<Upgraded>(),
                    ComponentType.ReadOnly<Edge>(),
                },
                Any = new[]
                {
                    ComponentType.ReadOnly<Updated>(),
                    ComponentType.ReadOnly<Created>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Building>(),
                },
            });
            RequireForUpdate(m_EdgeQuery);
        }

        protected override void OnDestroy()
        {
            if (m_ManagedMarkingPrefabs.IsCreated) m_ManagedMarkingPrefabs.Dispose();
            base.OnDestroy();
        }

        protected override void OnUpdate()
        {
            try
            {
                if (!EnsureResolved()) return;          // prefabs not ready yet, or nothing to manage
                if (m_ManagedMarkingPrefabs.IsEmpty) { Enabled = false; return; }

                var ecb = m_Barrier.CreateCommandBuffer();
                var edges = m_EdgeQuery.ToEntityArray(Allocator.Temp);
                int removed = 0, edgesHit = 0;
                for (int i = 0; i < edges.Length; i++)
                {
                    var edge = edges[i];
                    var upgraded = EntityManager.GetComponentData<Upgraded>(edge);
                    if (!MarkingFlags.HasMarkingsOff(upgraded.m_Flags)) continue; // only "markings off" edges

                    // Mutable buffer: drop our marking sub-lanes from it (and Delete the entities) so nothing is
                    // left pointing at a Deleted entity. Walk back-to-front because we remove in place.
                    var subLanes = EntityManager.GetBuffer<SubLane>(edge);
                    bool any = false;
                    for (int j = subLanes.Length - 1; j >= 0; j--)
                    {
                        var lane = subLanes[j].m_SubLane;
                        if (!EntityManager.HasComponent<SecondaryLane>(lane)) continue;       // only marking lanes
                        if (!EntityManager.HasComponent<PrefabRef>(lane)) continue;
                        var prefab = EntityManager.GetComponentData<PrefabRef>(lane).m_Prefab;
                        if (!m_ManagedMarkingPrefabs.Contains(prefab)) continue;              // only ours
                        subLanes.RemoveAt(j);
                        ecb.AddComponent<Deleted>(lane); // Deleted alone removes it from the render batches; do NOT
                                                         // tag the edge Updated — that would loop with SecondaryLaneSystem
                        removed++; any = true;
                    }
                    if (any) edgesHit++;
                }
                edges.Dispose();
                if (removed > 0)
                    log.Info($"MarkingSuppressSystem: removed {removed} marking sub-lane(s) on {edgesHit} 'markings off' edge(s)");
            }
            catch (Exception e)
            {
                log.Error(e, "MarkingSuppressSystem failed");
            }
        }

        /// <summary>
        /// Lazily resolves the marking prefab entities we manage (highway edge line + our parking line / end
        /// prefabs). Returns false until at least one is found (i.e. prefabs are loaded), so OnUpdate is a no-op
        /// before then. Once resolved we keep the set for the rest of the session.
        /// </summary>
        private bool EnsureResolved()
        {
            if (m_Resolved) return true;

            // Only manage the highway edge line if we actually extended it onto city roads, and only the parking
            // prefabs if parking markings are on — so a "markings off" upgrade never touches a feature that is
            // disabled in settings (and thus only present on its original vanilla networks).
            var wanted = new HashSet<string>();
            if (Mod.Settings == null || Mod.Settings.EdgeLineEnabled)
                foreach (var n in kVanillaEdgeLineNames) wanted.Add(n);
            if (Mod.Settings == null || Mod.Settings.ParkingMarkingsEnabled)
                foreach (var n in ParkingMarkingPatchSystem.CreatedPrefabNames) wanted.Add(n);
            if (wanted.Count == 0) { m_Resolved = true; return true; }   // nothing to manage

            var q = GetEntityQuery(ComponentType.ReadOnly<PrefabData>(), ComponentType.ReadOnly<NetLaneData>());
            var entities = q.ToEntityArray(Allocator.Temp);
            int hits = 0;
            for (int i = 0; i < entities.Length; i++)
            {
                if (!m_PrefabSystem.TryGetPrefab<NetLanePrefab>(entities[i], out var lane) || lane == null) continue;
                if (wanted.Contains(lane.name)) { m_ManagedMarkingPrefabs.Add(entities[i]); hits++; }
            }
            entities.Dispose();

            if (hits == 0) return false;   // prefabs not loaded yet — try again next frame
            m_Resolved = true;
            log.Info($"MarkingSuppressSystem: tracking {hits} managed marking prefab(s)");
            return true;
        }
    }
}
