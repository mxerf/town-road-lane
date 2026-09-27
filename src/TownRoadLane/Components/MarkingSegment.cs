using Colossal.Serialization.Entities;
using Unity.Entities;

namespace TownRoadLane
{
    /// <summary>
    /// One drawable piece of a <see cref="MarkingLine"/>, stored in a flat per-node buffer shared
    /// by all lines. A new line gets a single segment covering t = [0, 1], and every crossing
    /// with another line splits the segment it falls in. New segments are visible until the user
    /// hides one (for example the part of a median that crosses a turn lane).
    ///
    /// <see cref="lineIndex"/> is the parent's position in the node's MarkingLine buffer. Deleting
    /// a line removes its segments and shifts the lineIndex of the segments after it.
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct MarkingSegment : IBufferElementData, ISerializable
    {
        public int   lineIndex;

        // Parameter range along the parent line's Bezier curve, [tStart, tEnd] ⊂ [0, 1].
        public float tStart;
        public float tEnd;

        public bool  visible;

        // Starts as the parent line's style and can be changed per segment.
        public int   style;

        private const int kVersion = 2;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(kVersion);
            writer.Write(lineIndex);
            writer.Write(tStart);
            writer.Write(tEnd);
            writer.Write(visible);
            writer.Write(style);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out int version);
            reader.Read(out lineIndex);
            reader.Read(out tStart);
            reader.Read(out tEnd);
            reader.Read(out visible);
            // Version 1 has no style field: those segments were drawn Solid (0).
            if (version >= 2) reader.Read(out style); else style = 0;
        }
    }
}
