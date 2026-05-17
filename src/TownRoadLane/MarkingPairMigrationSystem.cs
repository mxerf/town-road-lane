using Colossal.Logging;
using Game;
using Game.Common;
using Unity.Collections;
using Unity.Entities;

namespace TownRoadLane
{
    /// <summary>
    /// One-time-per-node migration from the v2 <see cref="MarkingPair"/> schema (one buffer entry =
    /// one fully drawn line) to the v3 <see cref="MarkingLine"/> + <see cref="MarkingSegment"/>
    /// schema (line = logical entity, segments = drawable pieces). Old saves load with MarkingPair
    /// buffers populated; this system rewrites each one as MarkingLine + a single visible
    /// MarkingSegment <c>[0,1]</c> covering the whole line, then removes the obsolete buffer.
    ///
    /// Idempotent: skips any node that already has a MarkingLine buffer (so re-running on a node
    /// the user has touched since migration is a no-op). Runs every tick — query returns 0
    /// entities in 99% of frames, so the cost is a single empty chunk iteration.
    ///
    /// Phase order: must run BEFORE MarkingSegmentEmissionSystem or the emission pass on a
    /// freshly loaded save sees no input.
    /// </summary>
    [UpdateBefore(typeof(MarkingSegmentEmissionSystem))]
    public partial class MarkingPairMigrationSystem : GameSystemBase
    {
        private static readonly ILog log = Mod.log;

        private EntityQuery _nodesNeedingMigration;

        protected override void OnCreate()
        {
            base.OnCreate();
            // Match nodes with the legacy buffer but NOT the new one — exactly the set that still
            // needs work. Tagging the new buffer as Absent makes this query stable: as soon as
            // migration adds MarkingLine to a node, that node drops out of the query.
            _nodesNeedingMigration = GetEntityQuery(
                ComponentType.ReadOnly<MarkingPair>(),
                ComponentType.Exclude<MarkingLine>());
            RequireForUpdate(_nodesNeedingMigration);
        }

        protected override void OnUpdate()
        {
            using var nodes = _nodesNeedingMigration.ToEntityArray(Allocator.Temp);
            if (nodes.Length == 0) return;

            int migrated = 0;
            for (int i = 0; i < nodes.Length; i++)
            {
                if (MigrateOne(nodes[i])) migrated++;
            }
            if (migrated > 0) log.Info($"MarkingPairMigrationSystem: migrated {migrated} node(s) from MarkingPair → MarkingLine+MarkingSegment");
        }

        private bool MigrateOne(Entity node)
        {
            if (!EntityManager.HasBuffer<MarkingPair>(node)) return false;
            var pairs = EntityManager.GetBuffer<MarkingPair>(node, isReadOnly: true);
            int n = pairs.Length;

            // Snapshot before any structural change — pairs becomes invalid the moment we
            // AddBuffer below.
            var snapshot = new NativeArray<MarkingPair>(n, Allocator.Temp);
            for (int i = 0; i < n; i++) snapshot[i] = pairs[i];

            var lines = EntityManager.AddBuffer<MarkingLine>(node);
            var segments = EntityManager.AddBuffer<MarkingSegment>(node);
            for (int i = 0; i < n; i++)
            {
                var p = snapshot[i];
                lines.Add(new MarkingLine
                {
                    sourceEdge = p.sourceEdge,
                    sourceGapIndex = p.sourceGapIndex,
                    targetEdge = p.targetEdge,
                    targetGapIndex = p.targetGapIndex,
                    style = 0,
                });
                segments.Add(new MarkingSegment
                {
                    lineIndex = i,
                    tStart = 0f,
                    tEnd = 1f,
                    visible = true,
                });
            }
            snapshot.Dispose();

            // Drop the legacy buffer so the query stops matching this node and so future code
            // doesn't have two sources of truth to keep in sync.
            EntityManager.RemoveComponent<MarkingPair>(node);

            // Mark node Updated so the emission system picks up the new buffers this frame.
            if (!EntityManager.HasComponent<Updated>(node))
                EntityManager.AddComponent<Updated>(node);

            return true;
        }
    }
}
