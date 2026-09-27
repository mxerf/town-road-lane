using Unity.Entities;

namespace TownRoadLane
{
    /// <summary>
    /// Legacy tag on sublanes spawned for <see cref="MarkingPair"/> entries, keyed by
    /// (node, pairIndex). Nothing creates it any more: <see cref="MarkingSegmentEmissionSystem"/>
    /// deletes such sublanes, and <see cref="CustomSecondaryLaneSystem"/> leaves them out of its
    /// old-lane cleanup. pairKey is an endpoint hash kept for diagnostics only: hashes can
    /// collide, so it is never used as the identity.
    /// </summary>
    public struct TRLPairLink : IComponentData
    {
        public Entity node;
        public int    pairIndex;
        public int    pairKey;

        public static int ComputeKey(MarkingPair p)
        {
            // Pack each endpoint into 64 bits (edge index high, gap index low) and combine them
            // order-independently. XOR-ing per-endpoint hashes would cancel out whenever both
            // ends share a gap index.
            long a = ((long)p.sourceEdge.Index << 32) | (uint)p.sourceGapIndex;
            long b = ((long)p.targetEdge.Index << 32) | (uint)p.targetGapIndex;
            long lo = a < b ? a : b;
            long hi = a < b ? b : a;
            // 64 to 32 bit mix, good enough for the handful of pairs on one node.
            ulong m = (ulong)lo;
            m = (m ^ (ulong)hi) * 0x9E3779B97F4A7C15UL;
            m ^= m >> 32;
            return (int)m;
        }
    }
}
