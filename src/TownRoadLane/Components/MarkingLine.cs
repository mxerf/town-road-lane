using Colossal.Serialization.Entities;
using Unity.Entities;
using TownRoadLane.Geometry;

namespace TownRoadLane.Components
{
    /// <summary>
    /// User-drawn marking line at a road node, one buffer entry per line (endpoints, style and
    /// curvature). The drawn pieces live in the node's <see cref="MarkingSegment"/> buffer and
    /// point back by <c>lineIndex</c>; a line is split into several segments where it crosses
    /// other lines. A node with a non-empty MarkingLine buffer gets no vanilla markings.
    ///
    /// Endpoints use the gap-based identity of <see cref="MarkingEndpointExtractor"/>, the same
    /// as the legacy <see cref="MarkingPair"/>, so migration copies them field for field.
    /// </summary>
    [FormerlySerializedAs("TownRoadLane.MarkingLine, TownRoadLane")]
    [InternalBufferCapacity(0)]
    public struct MarkingLine : IBufferElementData, ISerializable
    {
        public Entity sourceEdge;
        public int sourceGapIndex;
        public Entity targetEdge;
        public int targetGapIndex;

        // Default style for new segments of this line (a MarkingStyle value).
        public int style;

        // Bezier control-point offset as a fraction of the chord (see MarkingCurveBuilder):
        // 0 = straight, 0.4 = default arc, 0.55 ≈ quarter circle.
        public float curvature;

        private const int kVersion = 4;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(kVersion);
            writer.Write(sourceEdge);
            writer.Write(sourceGapIndex);
            writer.Write(targetEdge);
            writer.Write(targetGapIndex);
            writer.Write(style);
            writer.Write(curvature);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out int version);
            reader.Read(out sourceEdge);
            reader.Read(out sourceGapIndex);
            reader.Read(out targetEdge);
            reader.Read(out targetGapIndex);
            reader.Read(out style);
            // Version 3 lines have no curvature field and used this constant.
            // Version 3 is the oldest layout that shipped.
            if (version >= 4) reader.Read(out curvature);
            else curvature = MarkingCurveBuilder.kPullFactor;
            ComponentVersion.Note(version, 3, kVersion, nameof(MarkingLine));
        }
    }
}
