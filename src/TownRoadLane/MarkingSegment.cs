using Colossal.Serialization.Entities;
using Unity.Entities;

namespace TownRoadLane
{
    /// <summary>
    /// One drawable piece of a <see cref="MarkingLine"/>. Per-node DynamicBuffer; multiple
    /// entries per line when the line crosses other lines on the same node.
    ///
    /// A freshly created line gets one segment <c>(lineIndex=i, tStart=0, tEnd=1, visible=true)</c>.
    /// When the line intersects another line at parameter <c>t*</c>, the segment splits into
    /// <c>(0, t*)</c> + <c>(t*, 1)</c>, both initially visible. The user toggles visibility per
    /// segment in Stage 5d to hide pieces (e.g. delete the median crossing the left-turn lane).
    ///
    /// Flat-list layout: one buffer per node, segments for all lines mixed together.
    /// <see cref="lineIndex"/> identifies the parent <see cref="MarkingLine"/> entry by its
    /// position in the node's MarkingLine buffer. When a line is deleted, all segments with the
    /// matching lineIndex go with it AND the remaining segments' lineIndex values shift down.
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct MarkingSegment : IBufferElementData, ISerializable
    {
        // Index into the MarkingLine buffer on the same node.
        public int   lineIndex;

        // Parameter range along the parent line's Bezier curve, [tStart, tEnd] ⊂ [0, 1].
        public float tStart;
        public float tEnd;

        // Whether this segment is drawn. Toggled by the user via Stage 5d UI; defaults to true
        // for every newly created segment so a fresh line + every newly inserted intersection
        // boundary look unchanged to the user until they explicitly hide a piece.
        public bool  visible;

        private const int kVersion = 1;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(kVersion);
            writer.Write(lineIndex);
            writer.Write(tStart);
            writer.Write(tEnd);
            writer.Write(visible);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out int _); // version; only v1 today
            reader.Read(out lineIndex);
            reader.Read(out tStart);
            reader.Read(out tEnd);
            reader.Read(out visible);
        }
    }
}
