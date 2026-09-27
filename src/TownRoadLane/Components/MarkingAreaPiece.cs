using Colossal.Serialization.Entities;
using Unity.Entities;
using Unity.Mathematics;

namespace TownRoadLane
{
    /// <summary>
    /// Resolved world-space outline of a <see cref="MarkingArea"/>, built by
    /// <c>MarkingAreaTopologySystem</c>. Each visible piece becomes one vanilla
    /// <c>Game.Areas.Area</c> entity in <c>MarkingAreaEmissionSystem</c>. Every area currently
    /// has exactly one piece (pieceIndex 0); lines drawn across an area do not cut it.
    ///
    /// Flat per-node list like <see cref="MarkingSegment"/>. Vertices are the slice
    /// [firstVertex, firstVertex + vertexCount) of the node's
    /// <see cref="MarkingAreaPieceVertex"/> buffer.
    ///
    /// Serialized: the saved ring is the fallback geometry when an area's anchors can't be
    /// resolved, and it keeps per-piece visibility across loads. On a rebuild a new piece
    /// inherits the visibility of the old piece that contains its centroid.
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct MarkingAreaPiece : IBufferElementData, ISerializable
    {
        public int areaIndex;
        public int pieceIndex;
        public bool visible;
        public int firstVertex;
        public int vertexCount;
        // Cached for visibility inheritance, recomputed on every rebuild.
        public float3 centroid;

        private const int kVersion = 1;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(kVersion);
            writer.Write(areaIndex);
            writer.Write(pieceIndex);
            writer.Write(visible);
            writer.Write(firstVertex);
            writer.Write(vertexCount);
            writer.Write(centroid);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out int _);
            reader.Read(out areaIndex);
            reader.Read(out pieceIndex);
            reader.Read(out visible);
            reader.Read(out firstVertex);
            reader.Read(out vertexCount);
            reader.Read(out centroid);
        }
    }

    /// <summary>
    /// World-space vertex of a <see cref="MarkingAreaPiece"/>. Emission copies the ring straight
    /// into the area's <c>Game.Areas.Node</c> buffer.
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct MarkingAreaPieceVertex : IBufferElementData, ISerializable
    {
        public float3 position;

        private const int kVersion = 1;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(kVersion);
            writer.Write(position);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out int _);
            reader.Read(out position);
        }
    }

    /// <summary>Hash of the node's area and line buffers at the last piece rebuild, so
    /// <c>MarkingAreaTopologySystem</c> can skip the rebuild when neither changed.</summary>
    public struct MarkingAreaTopologyState : IComponentData
    {
        public int combinedHash;
    }
}
