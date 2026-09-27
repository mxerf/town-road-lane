using Colossal.Serialization.Entities;
using Unity.Entities;

namespace TownRoadLane
{
    /// <summary>
    /// Legacy per-node buffer of user-drawn lines, one entry per whole line. Kept only so old
    /// saves load: <see cref="MarkingPairMigrationSystem"/> rewrites it as
    /// <see cref="MarkingLine"/> + <see cref="MarkingSegment"/>.
    ///
    /// Endpoints use the gap-based identity of <see cref="MarkingEndpointExtractor"/>: an edge
    /// with N car lanes at the node has N+1 endpoints (each lane-to-lane seam plus the two outer
    /// kerbs), and <c>gapIndex</c> selects one.
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct MarkingPair : IBufferElementData, ISerializable
    {
        public Entity sourceEdge;
        public int    sourceGapIndex;

        public Entity targetEdge;
        public int    targetGapIndex;

        // Version 1 had a different layout that never shipped, so only version 2 is read.
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
            reader.Read(out int _);  // version
            reader.Read(out sourceEdge);
            reader.Read(out sourceGapIndex);
            reader.Read(out targetEdge);
            reader.Read(out targetGapIndex);
        }
    }
}
