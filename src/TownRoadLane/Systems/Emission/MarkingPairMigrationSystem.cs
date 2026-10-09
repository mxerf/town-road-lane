using Colossal.Logging;
using Game;
using Game.Common;
using Unity.Collections;
using Unity.Entities;
using TownRoadLane.Components;
using TownRoadLane.Geometry;
using TownRoadLane.Utilities;

namespace TownRoadLane.Systems.Emission
{
    /// <summary>
    /// Converts old saves: nodes that still carry a <see cref="MarkingPair"/> buffer (one entry per
    /// fully drawn line) get a <see cref="MarkingLine"/> per pair plus one visible
    /// <see cref="MarkingSegment"/> <c>[0,1]</c> covering it, and the old buffer is removed.
    /// Nodes that already have MarkingLine are never touched, and the query is empty on almost
    /// every frame. Runs before MarkingSegmentEmissionSystem, otherwise the first emission pass
    /// on a freshly loaded save sees no lines. Order is the UpdateSystem registration in Mod.OnLoad.
    /// </summary>
    public partial class MarkingPairMigrationSystem : GameSystemBase
    {
        private static readonly ILog log = Mod.log;

        private EntityQuery _nodesNeedingMigration;

        protected override void OnCreate()
        {
            base.OnCreate();
            // A node leaves the query as soon as migration adds MarkingLine to it.
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

            // AddBuffer is a structural change and invalidates the pairs buffer.
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
                    curvature = MarkingCurveBuilder.kPullFactor,
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

            EntityManager.RemoveComponent<MarkingPair>(node);

            // Updated makes the emission systems pick up the new buffers this frame.
            EntityManager.MarkUpdated(node);

            return true;
        }
    }
}
