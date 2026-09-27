using Colossal.Serialization.Entities;
using Unity.Entities;
using Unity.Mathematics;

namespace TownRoadLane
{
    /// <summary>
    /// User-drawn polygonal fill area at a road node, one buffer entry per closed area. Its
    /// vertices are the slice [firstVertex, firstVertex + vertexCount) of the node's
    /// <see cref="MarkingAreaVertex"/> buffer. <see cref="MarkingAreaEmissionSystem"/> turns
    /// each area into vanilla <c>Game.Areas.Area</c> entities.
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct MarkingArea : IBufferElementData, ISerializable
    {
        public int styleId;
        // Hidden areas stay in the buffer, like MarkingSegment.visible.
        public bool visible;
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
    /// One area polygon vertex, stored as a reference to a node anchor rather than a position so
    /// the polygon follows the road when lanes move. Areas own contiguous slices of this
    /// per-node buffer.
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct MarkingAreaVertex : IBufferElementData, ISerializable
    {
        // MarkingNodeToolSystem.AreaAnchorKind: 0 = LaneEndpoint, 1 = NodeCorner,
        // 2 = LineIntersection.
        public byte kind;
        // kind 2: packed (lineA, lineB, hitIndex) crossing reference, stable across loads.
        // kind 0/1 in version 1 saves: raw index into the extracted endpoint/corner list. That
        // order is not stable across loads (the game rebuilds lanes and extraction follows them),
        // so version 2 vertices use the refEdge/refGap/refPos identity below instead and this
        // index only resolves version 1 vertices.
        public int refIndex;
        // MarkingNodeToolSystem.AreaEdgeKind: how the edge to the next vertex is sampled
        // (0 = straight chord, 1 = part of a MarkingLine curve).
        public byte edgeToNext;
        // Stable identity for kind 0/1, the same scheme that keeps MarkingLine valid across loads:
        //   kind 0: refEdgeA = the endpoint's road edge, refGap = its gap index;
        //   kind 1: refEdgeA/refEdgeB = the corner's edge pair (refEdgeB may be Null).
        // refPos is the world position at draw time. It tells apart the two standalone kerb
        // corners of one edge (both are edgeA + Null) and recovers vertices whose road
        // composition changed. refEdgeA == Entity.Null on kind 0/1 marks a version 1 vertex.
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
    /// Tag on every vanilla <c>Game.Areas.Area</c> entity spawned by
    /// <see cref="MarkingAreaEmissionSystem"/>, keyed by (node, areaIndex, pieceIndex). Emission
    /// diffs the areas that should exist against the tagged entities, like
    /// <see cref="TRLSegmentLink"/> does for lines.
    ///
    /// The key lives on the area entity rather than in a node buffer of spawned entities: an
    /// entity created through an EntityCommandBuffer is a placeholder until playback, so a stored
    /// reference is invalid on the next tick and the area would be respawned every frame.
    /// </summary>
    /// <remarks>
    /// Must be serialized. The game saves the spawned Area entities, and without the tag each
    /// load leaves an untagged copy: emission spawns a fresh fill on top, and hide/delete only
    /// affect the new one. Saves that predate the serialized tag are cleaned up by
    /// <see cref="MarkingAreaEmissionSystem"/> on load. Lines don't need this because the game
    /// does not save lanes.
    /// </remarks>
    public struct TRLAreaLink : IComponentData, ISerializable
    {
        public Entity node;
        public int areaIndex;
        // Piece of the area this entity renders (see MarkingAreaPiece.pieceIndex); currently
        // always 0, one piece per area.
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
