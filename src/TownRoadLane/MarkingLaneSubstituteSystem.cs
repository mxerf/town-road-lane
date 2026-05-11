using System;
using System.Collections.Generic;
using Colossal.Logging;
using Game;
using Game.Common;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;

namespace TownRoadLane
{
    /// <summary>
    /// Makes the per-segment "Lane Markings" upgrade actually suppress this mod's markings — the safe way.
    ///
    /// The engine's <see cref="Game.Net.SecondaryLaneSystem"/> decides whether to draw a marking next to a host lane
    /// purely from that lane's <em>prefab</em> (its baked <c>SecondaryNetLane</c> buffer). It runs as a Burst job, so
    /// we can't hook it; and deleting the marking sub-lanes after the fact crashes the engine. Instead we change
    /// which lane prefabs an edge uses: when an edge carries the <see cref="MarkingFlags.MarkingsOff"/> bit it gets a
    /// distinct <c>NetCompositionData</c> composition (distinct flags ⇒ distinct cache key), and that composition is
    /// used only by such edges. Right after <see cref="NetCompositionSystem"/> bakes that composition's
    /// <c>NetCompositionLane</c> list (which contains the original city drive-lane prefabs) and before
    /// <see cref="Game.Net.LaneSystem"/> instantiates lanes from it, we rewrite the list: each original drive-lane
    /// prefab → the matching "no-marking" clone (see <see cref="MarkingUpgradePrefabSystem"/>).
    ///
    /// The clone is created with an empty <c>SecondaryNetLane</c> buffer (nothing references it). On our first run
    /// we copy each original drive lane's <c>SecondaryNetLane</c> buffer into its clone, dropping only the entries
    /// that point at OUR marking prefabs (the highway edge line + our parking line/end clones) — so the clone keeps
    /// the vanilla center/divider/lane-separator markings and loses just ours. Everything else about the composition
    /// (pieces, widths, the lanes themselves) is unchanged, so nothing dangles — no crash.
    ///
    /// Runs in <see cref="SystemUpdatePhase.Modification4"/>, after <see cref="NetCompositionSystem"/> and before
    /// <see cref="Game.Net.LaneSystem"/>. A composition entity is built once and cached, so each gets rewritten once
    /// (tracked in <see cref="m_Processed"/>); if it is re-baked (re-appears with <c>Updated</c>) we rewrite it again.
    /// </summary>
    public partial class MarkingLaneSubstituteSystem : GameSystemBase
    {
        private static readonly ILog log = Mod.log;

        private PrefabSystem m_PrefabSystem;
        private EntityQuery m_CompositionQuery;
        private EntityQuery m_LanePrefabQuery;
        private NativeParallelHashSet<Entity> m_Processed;
        private bool m_DisabledAnnounced;
        private bool m_CloneBuffersFiltered;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_Processed = new NativeParallelHashSet<Entity>(64, Allocator.Persistent);
            m_CompositionQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<NetCompositionData>(), ComponentType.ReadWrite<NetCompositionLane>() },
                Any = new[] { ComponentType.ReadOnly<Created>(), ComponentType.ReadOnly<Updated>() },
                None = new[] { ComponentType.ReadOnly<Deleted>() },
            });
            m_LanePrefabQuery = GetEntityQuery(ComponentType.ReadOnly<PrefabData>(), ComponentType.ReadOnly<NetLaneData>());
            RequireForUpdate(m_CompositionQuery);
        }

        protected override void OnDestroy()
        {
            if (m_Processed.IsCreated) m_Processed.Dispose();
            base.OnDestroy();
        }

        protected override void OnUpdate()
        {
            try
            {
                if (Mod.Settings != null && !Mod.Settings.SegmentToggleEnabled)
                {
                    if (!m_DisabledAnnounced) { log.Info("MarkingLaneSubstituteSystem: per-segment Lane Markings toggle disabled in settings — idle"); m_DisabledAnnounced = true; }
                    return;
                }

                var map = MarkingUpgradePrefabSystem.NoMarkingLaneByOriginal;
                if (map.Count == 0) return; // clones not ready yet (or feature off)

                // Only substitute clones whose lane archetype has actually been baked by the prefab-init pipeline —
                // otherwise LaneSystem would CreateEntity with an invalid archetype and crash on command-buffer
                // playback. (Validate once; if a clone isn't ready we just don't use it — markings stay, no crash.)
                var safeMap = new Dictionary<Entity, Entity>(map.Count);
                foreach (var kv in map)
                {
                    if (!EntityManager.HasComponent<NetLaneArchetypeData>(kv.Value))
                    { log.Warn($"MarkingLaneSubstituteSystem: clone entity {kv.Value.Index} has no NetLaneArchetypeData — not substituting it"); continue; }
                    var arch = EntityManager.GetComponentData<NetLaneArchetypeData>(kv.Value);
                    if (!arch.m_EdgeLaneArchetype.Valid || !arch.m_EdgeMasterArchetype.Valid || !arch.m_EdgeSlaveArchetype.Valid)
                    { log.Warn($"MarkingLaneSubstituteSystem: clone entity {kv.Value.Index} has unbaked lane archetype — not substituting it"); continue; }
                    safeMap[kv.Key] = kv.Value;
                }
                if (safeMap.Count == 0) return;

                // Give each clone the vanilla part of its original's SecondaryNetLane buffer (once, when ready).
                EnsureCloneBuffersFiltered(safeMap);

                var comps = m_CompositionQuery.ToEntityArray(Allocator.Temp);
                int rewrittenComps = 0, rewrittenLanes = 0;
                for (int i = 0; i < comps.Length; i++)
                {
                    var comp = comps[i];
                    var data = EntityManager.GetComponentData<NetCompositionData>(comp);
                    if ((data.m_Flags.m_General & MarkingFlags.MarkingsOff) == 0) continue; // not a "markings off" composition
                    if (!m_Processed.Add(comp)) continue;                                   // already rewritten

                    var lanes = EntityManager.GetBuffer<NetCompositionLane>(comp);
                    int n = 0;
                    for (int j = 0; j < lanes.Length; j++)
                    {
                        if (!safeMap.TryGetValue(lanes[j].m_Lane, out var clone)) continue;
                        var e = lanes[j];
                        e.m_Lane = clone;
                        lanes[j] = e;
                        n++;
                    }
                    if (n > 0) { rewrittenComps++; rewrittenLanes += n; }
                }
                comps.Dispose();
                if (rewrittenComps > 0)
                    log.Info($"MarkingLaneSubstituteSystem: substituted {rewrittenLanes} lane(s) to no-marking clones across {rewrittenComps} 'markings off' composition(s)");
            }
            catch (Exception e)
            {
                log.Error(e, "MarkingLaneSubstituteSystem failed");
            }
        }

        /// <summary>
        /// One-time: for each original drive lane that has a no-marking clone, copy its (now fully baked, including
        /// our edge-line addition) <c>SecondaryNetLane</c> buffer into the clone, dropping the entries whose
        /// <c>m_Lane</c> is one of our marking prefabs. After this the clone hosts all the vanilla markings the drive
        /// lane normally has, minus ours.
        /// </summary>
        private void EnsureCloneBuffersFiltered(Dictionary<Entity, Entity> origToClone)
        {
            if (m_CloneBuffersFiltered) return;

            // Resolve our marking prefab entities by name (the highway edge line if we extended it onto city roads,
            // plus our parking line/end clones if parking markings are on).
            var ourNames = new HashSet<string>();
            if (Mod.Settings == null || Mod.Settings.EdgeLineEnabled)
            { ourNames.Add("EU Highway Edge Line"); ourNames.Add("NA Highway Edge Line"); }
            if (Mod.Settings == null || Mod.Settings.ParkingMarkingsEnabled)
                foreach (var n in ParkingMarkingPatchSystem.CreatedPrefabNames) ourNames.Add(n);

            var ourEntities = new HashSet<Entity>();
            var ents = m_LanePrefabQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < ents.Length; i++)
            {
                if (!m_PrefabSystem.TryGetPrefab<NetLanePrefab>(ents[i], out var lane) || lane == null) continue;
                if (ourNames.Contains(lane.name)) ourEntities.Add(ents[i]);
            }
            ents.Dispose();

            int copiedTotal = 0, droppedTotal = 0, doneClones = 0;
            foreach (var kv in origToClone)
            {
                var orig = kv.Key; var clone = kv.Value;
                if (!EntityManager.HasBuffer<SecondaryNetLane>(orig) || !EntityManager.HasBuffer<SecondaryNetLane>(clone))
                { log.Warn($"MarkingLaneSubstituteSystem: orig {orig.Index} or clone {clone.Index} has no SecondaryNetLane buffer — clone hosts nothing"); continue; }
                var src = EntityManager.GetBuffer<SecondaryNetLane>(orig, isReadOnly: true);
                var dst = EntityManager.GetBuffer<SecondaryNetLane>(clone);
                dst.Clear();
                int kept = 0, dropped = 0;
                for (int i = 0; i < src.Length; i++)
                {
                    if (ourEntities.Contains(src[i].m_Lane)) { dropped++; continue; }
                    dst.Add(src[i]); kept++;
                }
                copiedTotal += kept; droppedTotal += dropped; doneClones++;
            }
            m_CloneBuffersFiltered = true;
            log.Info($"MarkingLaneSubstituteSystem: filtered SecondaryNetLane buffers of {doneClones} clone(s) — kept {copiedTotal} vanilla marking ref(s), dropped {droppedTotal} of ours");
        }
    }
}
