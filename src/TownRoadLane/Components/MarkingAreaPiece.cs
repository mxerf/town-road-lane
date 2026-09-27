using Colossal.Serialization.Entities;
using Unity.Entities;
using Unity.Mathematics;

namespace TownRoadLane
{
    /// <summary>
    /// Phase 6e: one sub-polygon produced by splitting a <see cref="MarkingArea"/> along
    /// every <see cref="MarkingLine"/> that fully crosses it. Computed by
    /// <c>MarkingAreaTopologySystem</c> and consumed by <c>MarkingAreaEmissionSystem</c>:
    /// each visible piece becomes one vanilla <c>Game.Areas.Area</c> entity.
    ///
    /// Buffer layout mirrors <see cref="MarkingSegment"/>: flat per-node list, every entry
    /// names its owning area (areaIndex) + dense per-area counter (pieceIndex). Vertex
    /// positions live in the companion <see cref="MarkingAreaPieceVertex"/> buffer, indexed
    /// by [firstVertex, firstVertex+vertexCount).
    ///
    /// Persistence: serialised so save/load round-trips don't lose per-piece visibility
    /// toggles. Topology system rebuilds the geometry from MarkingArea+MarkingLine, but
    /// preserves visibility for pieces whose centroid still lies inside an old piece.
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct MarkingAreaPiece : IBufferElementData, ISerializable
    {
        public int areaIndex;
        public int pieceIndex;
        public bool visible;
        public int firstVertex;
        public int vertexCount;
        // Cached centroid so visibility-inheritance lookups don't have to re-read the vertex
        // buffer just to compute it. Recomputed by MarkingAreaTopologySystem on each rewrite.
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
    /// Phase 6e: one world-space vertex of a <see cref="MarkingAreaPiece"/>. Pre-computed so
    /// emission doesn't re-run the polygon split on every tick — it just reads the cached
    /// ring straight into <c>Game.Areas.Node[]</c> on the spawned area entity.
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

    /// <summary>Per-node companion of <see cref="MarkingArea"/> + <see cref="MarkingLine"/>:
    /// caches the combined hash at last successful piece recompute. Lets
    /// <c>MarkingAreaTopologySystem</c> skip the O(areas * lines) split work when neither buffer
    /// changed since last tick.</summary>
    public struct MarkingAreaTopologyState : IComponentData
    {
        public int combinedHash;
    }
}
