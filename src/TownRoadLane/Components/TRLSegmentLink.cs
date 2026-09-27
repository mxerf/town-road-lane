using Unity.Entities;

namespace TownRoadLane
{
    /// <summary>
    /// Tag on every sublane spawned by <see cref="MarkingSegmentEmissionSystem"/>, keyed by
    /// <c>(node, lineIndex, segmentIndex, passIndex)</c>. Emission diffs the segments that should
    /// be drawn against the tagged sublanes and only creates or deletes the difference. The
    /// indices are buffer slots, so they are unique within a node on a given tick.
    /// </summary>
    public struct TRLSegmentLink : IComponentData
    {
        public Entity node;
        public int    lineIndex;
        public int    segmentIndex;
        // 0 for the base copy, 1+ for the extra copies of styles drawn in several passes (see
        // MarkingStyleExtensions.DrawPasses). Part of the key, so the copies are not treated as
        // duplicates of each other.
        public int    passIndex;
    }
}
