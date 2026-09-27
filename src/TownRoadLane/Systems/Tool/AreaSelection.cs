using Unity.Mathematics;
using TownRoadLane.Components;

namespace TownRoadLane.Systems.Tool
{
    /// <summary>A placed vertex of the area being drawn. The anchor reference lets positions be
    /// rebuilt after a topology change. edgeToNext is set once the following vertex is picked, so
    /// on the last vertex it stays unresolved until the next click or the closing click.</summary>
    public struct AreaPolygonVertex
    {
        public AreaAnchorKind kind;
        public int refIndex;
        public AreaEdgeKind edgeToNext;
        public float3 position;  // cached at click time to keep the overlay cheap
    }

    /// <summary>Hover or pick target in area mode: anchor kind plus reference, so one hit-test
    /// pass covers every anchor type. <see cref="None"/> has refIndex -1.</summary>
    public struct AreaCandidate : System.IEquatable<AreaCandidate>
    {
        public AreaAnchorKind kind;
        public int refIndex;
        public static readonly AreaCandidate None = new AreaCandidate { kind = AreaAnchorKind.LaneEndpoint, refIndex = -1 };
        public bool IsValid => refIndex >= 0;
        public bool Equals(AreaCandidate other) => kind == other.kind && refIndex == other.refIndex;
        public override bool Equals(object obj) => obj is AreaCandidate c && Equals(c);
        public override int GetHashCode() => ((int)kind << 24) ^ refIndex;
    }
}
