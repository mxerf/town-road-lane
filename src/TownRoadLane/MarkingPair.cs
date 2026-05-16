using Colossal.Serialization.Entities;
using Unity.Entities;

namespace TownRoadLane
{
    /// <summary>
    /// User-defined marking connection at a road node. Per-node DynamicBuffer; one entry per
    /// connection the user drew with the Phase-4 tool.
    ///
    /// Semantics (decided upfront, baked into <see cref="CustomSecondaryLaneSystem"/> behaviour):
    /// **a node with a non-empty MarkingPair buffer fully overrides vanilla markings on that node**.
    /// All vanilla CreateSecondaryLane calls are skipped for that owner; only the pairs in this
    /// buffer are emitted as new marking sublanes. Remove the override (delete buffer or zero
    /// length) → vanilla rendering returns.
    ///
    /// This is simpler than Traffic mod's LaneEndKey hashset (which suppressed individual lane
    /// ends so user picks could coexist with vanilla picks). Our binary semantic — pairs present
    /// vs absent — is right for marking customisation where the user wants total node control.
    ///
    /// Endpoint identity: a lane-corner is identified by (edge entity, lane index along the edge's
    /// SubLane buffer, isRight). isRight selects which side of the lane the marking attaches to —
    /// matches the lane-corner pair semantics described in RESEARCH_decomp.md §2 step 3.
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct MarkingPair : IBufferElementData, ISerializable
    {
        // Source endpoint
        public Entity sourceEdge;
        public int    sourceLaneIndex;
        public bool   sourceIsRight;

        // Target endpoint
        public Entity targetEdge;
        public int    targetLaneIndex;
        public bool   targetIsRight;

        // Schema version header for forward compatibility. Bump and gate fields in Deserialize when
        // we add new ones — same idiom Traffic uses (ModifiedLaneConnections.cs:40-72).
        private const int kVersion = 1;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(kVersion);
            writer.Write(sourceEdge);
            writer.Write(sourceLaneIndex);
            writer.Write(sourceIsRight);
            writer.Write(targetEdge);
            writer.Write(targetLaneIndex);
            writer.Write(targetIsRight);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out int _);  // version; only v1 today
            reader.Read(out sourceEdge);
            reader.Read(out sourceLaneIndex);
            reader.Read(out sourceIsRight);
            reader.Read(out targetEdge);
            reader.Read(out targetLaneIndex);
            reader.Read(out targetIsRight);
        }
    }
}
