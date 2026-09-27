using Colossal.Serialization.Entities;
using Unity.Entities;
using Unity.Mathematics;

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
        // Maps onto MarkingNodeToolSystem.AreaAnchorKind: 0 = LaneEndpoint, 1 = NodeCorner,
        // 2 = LineIntersection.
        public byte kind;
        // kind 2: packed (lineA, lineB, hitIndex) crossing ref — stable across save/load.
        // kind 0/1: LEGACY v1 identity — raw index into the extracted endpoint/corner list.
        // List order is NOT deterministic across save loads (lanes are rebuilt by the game and
        // extraction order follows them), which made saved areas snap to the wrong dots after
        // reload. v2 vertices carry the stable identity below instead; refIndex remains only
        // so v1 saves keep resolving the old way.
        public int refIndex;
        // Maps onto MarkingNodeToolSystem.AreaEdgeKind: 0 = Straight, 1 = LineBezier.
        public byte edgeToNext;
        // v2 stable identity (same scheme that makes MarkingLine survive reloads):
        //   kind 0: refEdgeA = endpoint's road edge, refGap = its gapIndex;
        //   kind 1: refEdgeA/refEdgeB = the corner's edge pair (edgeB may be Null);
        // refPos = draw-time world position — disambiguates standalone kerb corners (two per
        // edge share the same edgeA+Null identity) and rescues vertices whose composition
        // changed. Entity.Null in refEdgeA for kind 0/1 marks a v1 vertex (legacy resolve).
        public Entity refEdgeA;
        public Entity refEdgeB;
        public int refGap;
        public float3 refPos;

        private const int kVersion = 2;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(kVersion);
            writer.Write(kind);
            writer.Write(refIndex);
            writer.Write(edgeToNext);
            writer.Write(refEdgeA);
            writer.Write(refEdgeB);
            writer.Write(refGap);
            writer.Write(refPos);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out int version);
            reader.Read(out kind);
            reader.Read(out refIndex);
            reader.Read(out edgeToNext);
            if (version >= 2)
            {
                reader.Read(out refEdgeA);
                reader.Read(out refEdgeB);
                reader.Read(out refGap);
                reader.Read(out refPos);
            }
            else
            {
                refEdgeA = Entity.Null;
                refEdgeB = Entity.Null;
                refGap = 0;
                refPos = float3.zero;
            }
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
    /// <remarks>
    /// SERIALIZED since 2.2.0: the spawned vanilla Area entity is saved by the game, and
    /// without the tag surviving alongside it every load produced an untagged orphan copy —
    /// the emission diff saw nothing tagged, spawned a fresh fill on top, and delete/hide from
    /// the panel only ever affected the fresh one (pre-2.2.0 orphans are swept by
    /// <see cref="MarkingAreaEmissionSystem"/> on load). Lines never had this problem: the
    /// game does not serialize lanes, so segment spawns die naturally with the save.
    /// </remarks>
    public struct TRLAreaLink : IComponentData, ISerializable
    {
        public Entity node;
        public int areaIndex;
        // Phase 6e: index of the piece this entity renders (after the area was split by every
        // line that crosses it). Pieces are indexed densely 0..K-1 per area in the
        // MarkingAreaPiece buffer on the host node. = 0 when no lines cross the area.
        public int pieceIndex;

        private const int kVersion = 1;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(kVersion);
            writer.Write(node);
            writer.Write(areaIndex);
            writer.Write(pieceIndex);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out int _);
            reader.Read(out node);
            reader.Read(out areaIndex);
            reader.Read(out pieceIndex);
        }
    }
}
