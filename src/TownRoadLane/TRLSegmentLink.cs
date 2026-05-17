using Unity.Entities;

namespace TownRoadLane
{
    /// <summary>
    /// Tag attached to every sublane that <see cref="MarkingSegmentEmissionSystem"/> created on
    /// a node for a specific <see cref="MarkingSegment"/>. Lets the emission system diff
    /// "what should exist" (entries in MarkingLine + MarkingSegment buffers) against
    /// "what does exist" (sublanes carrying this component for the node) without re-creating
    /// anything already correct.
    ///
    /// Identity = <c>(node, lineIndex, segmentIndex)</c>. Both indices are buffer slots and are
    /// uniquely stable per (node, tick) — never collide. Replaces the Phase-4
    /// <see cref="TRLPairLink"/> which keyed by (node, pairIndex) under the old MarkingPair
    /// model. Old TRLPairLink entities still alive after schema migration are deleted on first
    /// sight by MarkingSegmentEmissionSystem (they have no MarkingPair to anchor them).
    /// </summary>
    public struct TRLSegmentLink : IComponentData
    {
        public Entity node;
        public int    lineIndex;
        public int    segmentIndex;
    }
}
