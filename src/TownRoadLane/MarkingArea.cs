using Colossal.Serialization.Entities;
using Unity.Entities;

namespace TownRoadLane
{
    /// <summary>
    /// Phase 6c: user-defined polygonal area at a road node. One DynamicBuffer entry per area the
    /// user closed. Vertices live in the companion <see cref="MarkingAreaVertex"/> buffer, sliced
    /// by [FirstVertex .. FirstVertex+VertexCount).
    ///
    /// Spawning a real vanilla <c>Game.Areas.Area</c> entity is done by
    /// <c>MarkingAreaEmissionSystem</c> on every change to this buffer — same flow as
    /// <see cref="MarkingPair"/> / <see cref="MarkingSegment"/> emission to sublanes.
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct MarkingArea : IBufferElementData, ISerializable
    {
        // Style enum to be filled in Phase 6d (Solid / DiagonalHatch / ...). 0 = Solid for now.
        public int styleId;
        // Hide without deleting — analogue of MarkingSegment.visible. Lets the UI toggle an area
        // off temporarily. Defaults to true.
        public bool visible;
        // Slice into the MarkingAreaVertex buffer.
        public int firstVertex;
        public int vertexCount;

        private const int kVersion = 1;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(kVersion);
            writer.Write(styleId);
            writer.Write(visible);
            writer.Write(firstVertex);
            writer.Write(vertexCount);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out int _); // version
            reader.Read(out styleId);
            reader.Read(out visible);
            reader.Read(out firstVertex);
            reader.Read(out vertexCount);
        }
    }

    /// <summary>
    /// Phase 6c: one polygon vertex referenced logically (so we can rebuild positions after
    /// topology changes like lane shifts). Held in a per-node buffer parallel with
    /// <see cref="MarkingArea"/>; areas index into a contiguous slice.
    ///
    /// EdgeToNext controls how the polyline between this vertex and the next is sampled by the
    /// emission system (straight chord vs sub-bezier of a shared MarkingLine).
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct MarkingAreaVertex : IBufferElementData, ISerializable
    {
        // Maps onto MarkingNodeToolSystem.AreaAnchorKind: 0 = LaneEndpoint, 1 = NodeCorner.
        public byte kind;
        // Index into the appropriate live list at the host node (lane endpoints regenerate on
        // every node click — so we re-extract; corner anchors likewise).
        public int refIndex;
        // Maps onto MarkingNodeToolSystem.AreaEdgeKind: 0 = Straight, 1 = LineBezier.
        public byte edgeToNext;

        private const int kVersion = 1;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(kVersion);
            writer.Write(kind);
            writer.Write(refIndex);
            writer.Write(edgeToNext);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out int _);
            reader.Read(out kind);
            reader.Read(out refIndex);
            reader.Read(out edgeToNext);
        }
    }

    /// <summary>
    /// Phase 6c+: tag attached to every vanilla Game.Areas.Area entity that
    /// <see cref="MarkingAreaEmissionSystem"/> created. Mirrors the
    /// <see cref="TRLSegmentLink"/> pattern: identity = (node, areaIndex), and emission diffs
    /// "what should exist" (entries in the MarkingArea buffer on host nodes) against
    /// "what does exist" (entities tagged with this).
    ///
    /// Previous design (per-host-node DynamicBuffer<TRLAreaLink> holding the spawned entity)
    /// hit the deferred-ECB trap — the spawn-time Entity reference is a placeholder that's
    /// invalid after Playback, so EntityManager.Exists(...) returned false next tick and we
    /// re-spawned every frame. Switching identity to a component on the area entity itself
    /// makes the diff lookup-based (find tagged entities, match by key), no entity-pointer
    /// chase across ECB boundaries.
    /// </summary>
    public struct TRLAreaLink : IComponentData
    {
        public Entity node;
        public int areaIndex;
        // Phase 6e: index of the piece this entity renders (after the area was split by every
        // line that crosses it). Pieces are indexed densely 0..K-1 per area in the
        // MarkingAreaPiece buffer on the host node. = 0 when no lines cross the area.
        public int pieceIndex;
    }
}
