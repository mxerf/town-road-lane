using System.Collections.Generic;
using Colossal.Mathematics;
using Unity.Entities;
using Unity.Mathematics;

namespace TownRoadLane
{
    /// <summary>
    /// Phase 7a (IMT-style fills, step 1): one clickable line×line crossing the area tool can
    /// anchor a polygon vertex to. Identity is the (lineA, lineB, hitIndex) triple packed into
    /// a single int (see <see cref="MarkingIntersectionExtractor.Pack"/>) — stable across adding
    /// NEW lines (unlike "n-th segment boundary of line i", which shifts), and re-resolving
    /// after a curvature edit simply moves the position along with the crossing.
    /// </summary>
    public struct MarkingIntersectionAnchor
    {
        public int lineA;      // lower lineIndex of the pair
        public int lineB;      // higher lineIndex of the pair
        public int hitIndex;   // k-th surviving hit of this pair, ordered by tA
        public float tA;       // parameter on lineA's Bezier (kept for future curved-edge sampling)
        public float tB;       // parameter on lineB's Bezier
        public float3 position;

        public int PackedRef => MarkingIntersectionExtractor.Pack(lineA, lineB, hitIndex);
    }

    /// <summary>
    /// Computes the intersection anchors of a node's marking lines, and resolves a packed
    /// (lineA, lineB, hitIndex) reference back to a world position. Single source of truth for
    /// the anchor list — the tool (hit-test), the overlay (candidate dots), the area topology
    /// (ring resolve) and the UI system (popover centroid) must all agree, or a saved
    /// MarkingAreaVertex would resolve to a different point than the one the user clicked.
    /// </summary>
    public static class MarkingIntersectionExtractor
    {
        /// <summary>Same endpoint margin as MarkingTopologySystem's Filter B: hits within this
        /// XZ distance of either curve's endpoint are the overlap cluster two lines produce
        /// when they leave a shared dot together, not a real crossing.</summary>
        public const float kEndpointMarginM = 2.0f;

        // Hits whose tA/tB sit outside this window are ON an endpoint parameter-wise — mirror
        // of the t-window MarkingTopologySystem applies to its segment boundaries.
        private const float kTMin = 0.01f;
        private const float kTMax = 0.99f;

        // packedRef layout: [lineA:11 bit][lineB:12 bit][hitIndex:8 bit] — capacities far
        // beyond the practical per-node line count (≤ ~20) and hits per pair (1-2), while
        // keeping the packed value positive (AreaCandidate treats refIndex < 0 as "none").
        public static int Pack(int lineA, int lineB, int hitIndex)
            => ((lineA & 0x7FF) << 20) | ((lineB & 0xFFF) << 8) | (hitIndex & 0xFF);

        public static void Unpack(int packed, out int lineA, out int lineB, out int hitIndex)
        {
            lineA = (packed >> 20) & 0x7FF;
            lineB = (packed >> 8) & 0xFFF;
            hitIndex = packed & 0xFF;
        }

        /// <summary>All intersection anchors of the node's current line set, in deterministic
        /// order (pair-major, then by tA).</summary>
        public static List<MarkingIntersectionAnchor> ExtractAll(EntityManager em, Entity node)
        {
            var result = new List<MarkingIntersectionAnchor>();
            if (node == Entity.Null || !em.HasBuffer<MarkingLine>(node)) return result;
            var lines = em.GetBuffer<MarkingLine>(node, isReadOnly: true);
            if (lines.Length < 2) return result;
            var endpoints = MarkingEndpointExtractor.Extract(em, node);

            // Build every line's Bezier once, then pairwise-intersect (n is tiny, ≤ ~20).
            var beziers = new Bezier4x3[lines.Length];
            var valid = new bool[lines.Length];
            for (int i = 0; i < lines.Length; i++)
                valid[i] = MarkingCurveBuilder.TryBuild(endpoints, lines[i], out beziers[i]);

            for (int i = 0; i < lines.Length; i++)
            {
                if (!valid[i]) continue;
                for (int j = i + 1; j < lines.Length; j++)
                {
                    if (!valid[j]) continue;
                    CollectPair(i, j, beziers[i], beziers[j], result);
                }
            }
            return result;
        }

        /// <summary>Resolve a packed intersection reference against the node's CURRENT lines.
        /// False when either line is gone or the pair no longer crosses that many times — the
        /// caller treats it like any other unresolvable vertex (the area gets cleaned up).</summary>
        public static bool TryResolve(IReadOnlyList<MarkingEndpoint> endpoints, IReadOnlyList<MarkingLine> lines,
                                      int packedRef, out float3 pos)
        {
            if (TryResolveAnchor(endpoints, lines, packedRef, out var anchor))
            {
                pos = anchor.position;
                return true;
            }
            pos = default;
            return false;
        }

        /// <summary>Like <see cref="TryResolve"/> but returns the full anchor — the t parameters
        /// on both lines are what curved-edge sampling needs (phase 7b).</summary>
        public static bool TryResolveAnchor(IReadOnlyList<MarkingEndpoint> endpoints, IReadOnlyList<MarkingLine> lines,
                                            int packedRef, out MarkingIntersectionAnchor anchor)
        {
            anchor = default;
            Unpack(packedRef, out int lineA, out int lineB, out int hitIndex);
            if (lineA < 0 || lineB <= lineA || lineB >= lines.Count) return false;
            if (!MarkingCurveBuilder.TryBuild(endpoints, lines[lineA], out var bezA)) return false;
            if (!MarkingCurveBuilder.TryBuild(endpoints, lines[lineB], out var bezB)) return false;
            var pairHits = new List<MarkingIntersectionAnchor>(2);
            CollectPair(lineA, lineB, bezA, bezB, pairHits);
            if (hitIndex >= pairHits.Count) return false;
            anchor = pairHits[hitIndex];
            return true;
        }

        /// <summary>Filtered, tA-ordered hits of one line pair, appended to <paramref name="into"/>.
        /// The filter + order here IS the hitIndex identity — never change one without the other,
        /// or saved area vertices will resolve to a different crossing than the one clicked.</summary>
        private static void CollectPair(int lineA, int lineB, Bezier4x3 bezA, Bezier4x3 bezB,
                                        List<MarkingIntersectionAnchor> into)
        {
            var hits = BezierIntersection.Intersect(bezA, bezB);
            var kept = new List<BezierIntersection.Hit>(hits.Count);
            for (int h = 0; h < hits.Count; h++)
            {
                var hit = hits[h];
                if (hit.tA < kTMin || hit.tA > kTMax || hit.tB < kTMin || hit.tB > kTMax) continue;
                if (IsNearAnyEndpoint(hit.point, bezA, bezB)) continue;
                kept.Add(hit);
            }
            kept.Sort((x, y) => x.tA.CompareTo(y.tA));
            for (int k = 0; k < kept.Count; k++)
            {
                into.Add(new MarkingIntersectionAnchor
                {
                    lineA = lineA,
                    lineB = lineB,
                    hitIndex = k,
                    tA = kept[k].tA,
                    tB = kept[k].tB,
                    position = kept[k].point,
                });
            }
        }

        private static bool IsNearAnyEndpoint(float3 p, Bezier4x3 a, Bezier4x3 b)
        {
            float rSq = kEndpointMarginM * kEndpointMarginM;
            return DistSqXZ(p, a.a) < rSq || DistSqXZ(p, a.d) < rSq
                || DistSqXZ(p, b.a) < rSq || DistSqXZ(p, b.d) < rSq;
        }

        private static float DistSqXZ(float3 p, float3 q)
        {
            float dx = p.x - q.x;
            float dz = p.z - q.z;
            return dx * dx + dz * dz;
        }
    }
}
