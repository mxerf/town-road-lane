using Colossal.Serialization.Entities;
using Unity.Entities;

namespace TownRoadLane
{
    /// <summary>
    /// User-defined logical marking line at a road node. Per-node DynamicBuffer; one entry per
    /// line the user drew. Replaces the Phase-4 <see cref="MarkingPair"/> with a richer model
    /// that supports segmentation (a line can be split into multiple drawable pieces at
    /// intersections with other lines on the same node).
    ///
    /// A line is the LOGICAL entity (endpoint A → endpoint B + style). The DRAWABLE pieces are
    /// stored in <see cref="MarkingSegment"/> on the same node and reference back via
    /// <c>lineIndex</c> = the index of this entry in the MarkingLine buffer at the moment the
    /// segment was created.
    ///
    /// Endpoint identity is the same gap-based scheme used by
    /// <see cref="MarkingEndpointExtractor"/> and matches the old MarkingPair semantics —
    /// migration is field-for-field copy.
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct MarkingLine : IBufferElementData, ISerializable
    {
        public Entity sourceEdge;
        public int    sourceGapIndex;
        public Entity targetEdge;
        public int    targetGapIndex;

        // Placeholder for Stage 5c (line styles). 0 = default solid line.
        // Kept in v3 schema so future style work doesn't need another bump.
        public int    style;

        private const int kVersion = 3;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(kVersion);
            writer.Write(sourceEdge);
            writer.Write(sourceGapIndex);
            writer.Write(targetEdge);
            writer.Write(targetGapIndex);
            writer.Write(style);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out int _); // version; only v3 today
            reader.Read(out sourceEdge);
            reader.Read(out sourceGapIndex);
            reader.Read(out targetEdge);
            reader.Read(out targetGapIndex);
            reader.Read(out style);
        }
    }
}
