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
    /// prefab → the matching "no-marking" clone (see <see cref="MarkingUpgradePrefabSystem"/>). The clone has an empty
    /// <c>SecondaryNetLane</c> buffer, so SecondaryLaneSystem draws nothing next to it. Everything else about the
    /// composition (pieces, widths, the lanes themselves) is unchanged, so nothing dangles — no crash.
    ///
    /// Runs in <see cref="SystemUpdatePhase.Modification4"/>, after <see cref="NetCompositionSystem"/> and before
    /// <see cref="Game.Net.LaneSystem"/>. A composition entity is built once and cached, so each gets rewritten once
    /// (tracked in <see cref="m_Processed"/>); if it is re-baked (re-appears with <c>Updated</c>) we rewrite it again.
    /// </summary>
    public partial class MarkingLaneSubstituteSystem : GameSystemBase
    {
        private static readonly ILog log = Mod.log;

        private EntityQuery m_CompositionQuery;
        private NativeParallelHashSet<Entity> m_Processed;
        private bool m_DisabledAnnounced;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Processed = new NativeParallelHashSet<Entity>(64, Allocator.Persistent);
            m_CompositionQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<NetCompositionData>(), ComponentType.ReadWrite<NetCompositionLane>() },
                Any = new[] { ComponentType.ReadOnly<Created>(), ComponentType.ReadOnly<Updated>() },
                None = new[] { ComponentType.ReadOnly<Deleted>() },
            });
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
                        if (!map.TryGetValue(lanes[j].m_Lane, out var clone)) continue;
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
    }
}
