using Colossal.Serialization.Entities;
using Unity.Entities;

namespace TownRoadLane
{
    /// <summary>
    /// User-defined marking connection at a road node. Per-node DynamicBuffer; one entry per
    /// connection the user drew with the Phase-4 tool.
    ///
    /// Semantics: **a node with a non-empty MarkingPair buffer fully overrides vanilla markings
    /// on that node**. All vanilla CreateSecondaryLane calls for that owner are skipped; only
    /// the pairs in this buffer become marking sublanes. Remove the override → vanilla returns.
    ///
    /// Endpoint identity: a gap-based scheme matches how
    /// <see cref="MarkingEndpointExtractor"/> exposes attach points to the user. Each edge with
    /// N car lanes meeting at the node yields N+1 endpoints — one per lane-to-lane stitch +
    /// the two outer kerbs. <c>gapIndex</c> selects which stitch.
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct MarkingPair : IBufferElementData, ISerializable
    {
        // Source endpoint
        public Entity sourceEdge;
        public int    sourceGapIndex;

        // Target endpoint
        public Entity targetEdge;
        public int    targetGapIndex;

        // Schema version. Bump + gate fields in Deserialize when adding new ones. v2 added the
        // gap-based scheme; v1 used a different layout (lane + isRight) that never shipped, so
        // we simply don't support v1 reads — anyone with v1 data has never been in production.
        private const int kVersion = 2;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(kVersion);
            writer.Write(sourceEdge);
            writer.Write(sourceGapIndex);
            writer.Write(targetEdge);
            writer.Write(targetGapIndex);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out int _);  // version; only v2 today
            reader.Read(out sourceEdge);
            reader.Read(out sourceGapIndex);
            reader.Read(out targetEdge);
            reader.Read(out targetGapIndex);
        }
    }
}
