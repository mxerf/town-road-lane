using Unity.Entities;
using TownRoadLane.Systems.Emission;
using TownRoadLane.Systems.Topology;

namespace TownRoadLane.Components
{
    /// <summary>
    /// Tag on every sublane spawned by <see cref="MarkingSegmentEmissionSystem"/>, keyed by
    /// <c>(node, lineIndex, segmentIndex, passIndex)</c>. Emission diffs the segments that should
    /// be drawn against the tagged sublanes and only creates or deletes the difference. The
    /// indices are buffer slots, so they are unique within a node on a given tick.
    ///
    /// Not serialized. The game saves the sublanes themselves, so a load brings them back
    /// untagged. <see cref="MarkingTopologySystem"/> rebuilds every node with lines after a load
    /// (its state is not saved either) and tags the node Updated;
    /// <see cref="CustomSecondaryLaneSystem"/> then removes all of the node's old lanes, the
    /// untagged copies included, and emission spawns tagged ones.
    /// </summary>
    public struct TRLSegmentLink : IComponentData
    {
        public Entity node;
        public int lineIndex;
        public int segmentIndex;
        // 0 for the base copy, 1+ for the extra copies of styles drawn in several passes (see
        // MarkingStyleExtensions.DrawPasses). Part of the key, so the copies are not treated as
        // duplicates of each other.
        public int passIndex;
    }
}
