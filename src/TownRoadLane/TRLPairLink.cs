using Unity.Entities;

namespace TownRoadLane
{
    /// <summary>
    /// Tag attached to every sublane that <see cref="MarkingPairEmissionSystem"/> created on a node
    /// for a specific <see cref="MarkingPair"/> entry. Lets the emission system diff "what should
    /// exist" (entries in the MarkingPair buffer) against "what does exist" (sublanes carrying this
    /// component for the node) without re-creating anything that's already correct.
    ///
    /// pairKey is a stable identity for a pair regardless of buffer index — we hash the source +
    /// target identifiers so order-swapped duplicates collide and never produce two sublanes.
    /// </summary>
    public struct TRLPairLink : IComponentData
    {
        public Entity node;
        public int    pairKey;

        public static int ComputeKey(MarkingPair p)
        {
            // Pack each endpoint as a single 64-bit number (edge.Index in hi, gapIndex in lo),
            // sort the pair, then combine. Previous XOR-of-two-component-hashes collapsed when
            // sourceGap == targetGap (gap ^ gap = 0), so e.g. (A.gap0↔B.gap0) and (A.gap1↔B.gap1)
            // got the same key and dedup deleted one of them. User report: "symmetric points on
            // different roads don't connect" — exactly that pattern.
            long a = ((long)p.sourceEdge.Index << 32) | (uint)p.sourceGapIndex;
            long b = ((long)p.targetEdge.Index << 32) | (uint)p.targetGapIndex;
            // Order-insensitive: always combine smaller first.
            long lo = a < b ? a : b;
            long hi = a < b ? b : a;
            // Splittable-ish mix for 64→32 reduction; collision-resistance is fine for our N≪1000.
            ulong m = (ulong)lo;
            m = (m ^ (ulong)hi) * 0x9E3779B97F4A7C15UL;
            m ^= m >> 32;
            return (int)m;
        }
    }
}
