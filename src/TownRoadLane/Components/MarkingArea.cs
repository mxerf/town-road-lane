using Colossal.Serialization.Entities;
using Unity.Entities;
using Unity.Mathematics;
using TownRoadLane.Systems.Emission;

namespace TownRoadLane.Components
{
    /// <summary>
    /// User-drawn polygonal fill area at a road node, one buffer entry per closed area. Its
    /// vertices are the slice [firstVertex, firstVertex + vertexCount) of the node's
    /// <see cref="MarkingAreaVertex"/> buffer. <see cref="MarkingAreaEmissionSystem"/> turns
    /// each area into vanilla <c>Game.Areas.Area</c> entities.
    /// </summary>
    [FormerlySerializedAs("TownRoadLane.MarkingArea, TownRoadLane")]
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
            reader.Read(out int version);
            reader.Read(out styleId);
            reader.Read(out visible);
            reader.Read(out firstVertex);
            reader.Read(out vertexCount);
            ComponentVersion.Note(version, 1, kVersion, nameof(MarkingArea));
        }
    }

    /// <summary>What an area vertex is anchored to. Saved as a byte in
    /// <see cref="MarkingAreaVertex.kind"/>, so values are never renumbered.</summary>
    public enum AreaAnchorKind : byte
    {
        LaneEndpoint = 0,
        NodeCorner = 1,
        // A crossing of two lines. The reference is the packed (lineA, lineB, hitIndex) value from
        // MarkingIntersectionExtractor.Pack, which stays valid when lines are added or their
        // curvature changes.
        LineIntersection = 2,
    }

    /// <summary>How the edge from an area vertex to the next one is drawn. Saved as a byte in
    /// <see cref="MarkingAreaVertex.edgeToNext"/>.</summary>
    public enum AreaEdgeKind : byte
    {
        Straight = 0,    // chord between the two anchors
        LineBezier = 1,  // both anchors lie on the same MarkingLine: follow its curve
    }

    /// <summary>
    /// One area polygon vertex, stored as a reference to a node anchor rather than a position so
    /// the polygon follows the road when lanes move. Areas own contiguous slices of this
    /// per-node buffer.
    /// </summary>
    [FormerlySerializedAs("TownRoadLane.MarkingAreaVertex, TownRoadLane")]
    [InternalBufferCapacity(0)]
    public struct MarkingAreaVertex : IBufferElementData, ISerializable
    {
        public AreaAnchorKind kind;
        // kind 2: packed (lineA, lineB, hitIndex) crossing reference, stable across loads.
        // kind 0/1 in version 1 saves: raw index into the extracted endpoint/corner list. That
        // order is not stable across loads (the game rebuilds lanes and extraction follows them),
        // so version 2 vertices use the refEdge/refGap/refPos identity below instead and this
        // index only resolves version 1 vertices.
        public int refIndex;
        public AreaEdgeKind edgeToNext;
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

        /// <summary>Version 1 lane-endpoint or corner vertex, still named by a raw list index.</summary>
        public bool IsLegacyIndexRef => kind != AreaAnchorKind.LineIntersection && refEdgeA == Entity.Null;

        private const int kVersion = 2;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(kVersion);
            writer.Write((byte)kind);
            writer.Write(refIndex);
            writer.Write((byte)edgeToNext);
            writer.Write(refEdgeA);
            writer.Write(refEdgeB);
            writer.Write(refGap);
            writer.Write(refPos);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out int version);
            reader.Read(out byte k);
            kind = (AreaAnchorKind)k;
            reader.Read(out refIndex);
            reader.Read(out byte e);
            edgeToNext = (AreaEdgeKind)e;
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
            ComponentVersion.Note(version, 1, kVersion, nameof(MarkingAreaVertex));
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
    /// <see cref="MarkingAreaEmissionSystem"/> on load. Lines get away without it: the untagged
    /// copies of their sublanes are removed together with the node's other lanes when the node is
    /// rebuilt after load (see <see cref="TRLSegmentLink"/>).
    /// </remarks>
    [FormerlySerializedAs("TownRoadLane.TRLAreaLink, TownRoadLane")]
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
            reader.Read(out int version);
            reader.Read(out node);
            reader.Read(out areaIndex);
            reader.Read(out pieceIndex);
            ComponentVersion.Note(version, 1, kVersion, nameof(TRLAreaLink));
        }
    }
}
