using Colossal.Serialization.Entities;
using Unity.Entities;
using TownRoadLane.Geometry;
using TownRoadLane.Systems.Emission;

namespace TownRoadLane.Components
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
    [FormerlySerializedAs("TownRoadLane.MarkingPair, TownRoadLane")]
    [InternalBufferCapacity(0)]
    public struct MarkingPair : IBufferElementData, ISerializable
    {
        public Entity sourceEdge;
        public int sourceGapIndex;

        public Entity targetEdge;
        public int targetGapIndex;

        // Version 1 stored lane index + isRight and never shipped. Version 2 is the gap identity.
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
            reader.Read(out int version);
            if (version < 2)
            {
                // v1 stored a lane index and an isRight flag per end. It never shipped.
                // Those six fields are still consumed so a stray v1 element cannot shift
                // the rest of the buffer. The pair is dropped: the lane index does not
                // map onto the gap identity v2 uses.
                reader.Read(out Entity _);
                reader.Read(out int _);
                reader.Read(out bool _);
                reader.Read(out Entity _);
                reader.Read(out int _);
                reader.Read(out bool _);
                sourceEdge = Entity.Null;
                sourceGapIndex = 0;
                targetEdge = Entity.Null;
                targetGapIndex = 0;
                ComponentVersion.Note(version, 2, kVersion, nameof(MarkingPair));
                return;
            }
            reader.Read(out sourceEdge);
            reader.Read(out sourceGapIndex);
            reader.Read(out targetEdge);
            reader.Read(out targetGapIndex);
            ComponentVersion.Note(version, 2, kVersion, nameof(MarkingPair));
        }
    }
}
