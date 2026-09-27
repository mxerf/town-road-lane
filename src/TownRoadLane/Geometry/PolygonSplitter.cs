using System.Collections.Generic;
using Unity.Mathematics;

namespace TownRoadLane
{
    /// <summary>
    /// Phase 6e: split a simple polygon (no holes, no self-intersections) by one or more
    /// polylines. Result is a list of smaller simple polygons that together cover the input,
    /// each lying entirely on one side of every cut.
    ///
    /// Algorithm: degenerate Weiler-Atherton — chord cut. For each polyline that crosses the
    /// polygon's boundary an EVEN number of times (entry + exit pairs), walk the boundary +
    /// chord to produce two sub-polygons. Recurse on the sub-polygons with the remaining
    /// polylines. ~120 LOC of pure geometry, no DOTS / no EM. XZ plane (Y interpolated).
    ///
    /// References:
    ///   - <see cref="MathUtils.Intersect(Line2.Segment, Line2.Segment, out float, out float)"/>
    ///     for the boundary/cut intersection (vanilla helper).
    ///
    /// Limits and edge cases handled:
    ///   - Odd hit counts: line ends inside polygon or grazes a vertex → skip (no split).
    ///   - Hits on / near a vertex: snap to t≈0 or t≈1 dedupe to avoid sliver edges.
    ///   - Tangent edges: treated as no intersection (cross-product check).
    ///   - Resulting ring with &lt; 3 distinct points: discarded.
    ///   - Multi-chord (≥4 hits on one cut): paired by sorted t-along-cut, each pair = one
    ///     chord; recursion handles them.
    /// </summary>
    public static class PolygonSplitter
    {
        // Hits on a polygon edge within this t-window of an endpoint are snapped to that endpoint.
        // Prevents a zero-length sliver edge when a cut grazes a vertex.
        private const float kVertexSnapT = 1e-3f;
        // Two intersection points along the same cut whose XZ distance is below this are treated
        // as one (typical case: cut passes exactly through a polygon vertex → both edges sharing
        // that vertex report a hit).
        private const float kDuplicateHitDistSq = 1e-4f * 1e-4f;
        // Minimum area (world units²) of a result ring; smaller is discarded as a sliver.
        private const float kMinRingArea = 0.01f;
        // Hard cap on recursion depth. Every legitimate recursion level strictly shrinks the
        // ring area (see the real-split guard below), so genuine splits never get near this;
        // it's a backstop against any degenerate geometry we haven't foreseen — better an
        // unsplit area than a stack overflow (mono dies with an unrecoverable native crash).
        private const int kMaxSplitDepth = 16;

        /// <summary>One intersection between a polygon edge and a cut segment.</summary>
        private struct Hit
        {
            public int edgeIndex;   // index into the polygon ring (edge from ring[edgeIndex] to ring[edgeIndex+1])
            public float tEdge;     // 0..1 along the polygon edge
            public int cutIndex;    // which segment of the cut polyline produced this hit
            public float tCut;      // 0..1 along that cut segment
            public float3 point;    // world-space intersection (Y interpolated along the edge)
        }

        /// <summary>
        /// Split <paramref name="polygon"/> by the polyline <paramref name="cut"/>. Returns the
        /// resulting sub-polygons. If the cut doesn't fully cross the polygon (odd hit count or
        /// fewer than 2 hits), returns a single-element list containing the original polygon
        /// unchanged.
        /// </summary>
        public static List<List<float3>> SplitByPolyline(List<float3> polygon, List<float3> cut)
            => SplitByPolyline(polygon, cut, 0);

        private static List<List<float3>> SplitByPolyline(List<float3> polygon, List<float3> cut, int depth)
        {
            var result = new List<List<float3>>();
            if (polygon == null || polygon.Count < 3 || cut == null || cut.Count < 2
                || depth >= kMaxSplitDepth)
            {
                result.Add(polygon ?? new List<float3>());
                return result;
            }

            // 1. Collect hits along the cut (every cut segment × every polygon edge).
            var hits = new List<Hit>(8);
            int n = polygon.Count;
            for (int c = 0; c < cut.Count - 1; c++)
            {
                float3 cA = cut[c], cB = cut[c + 1];
                for (int e = 0; e < n; e++)
                {
                    float3 eA = polygon[e], eB = polygon[(e + 1) % n];
                    if (!Segment2DIntersect(eA.xz, eB.xz, cA.xz, cB.xz, out float tEdge, out float tCut)) continue;
                    if (tEdge < 0f || tEdge > 1f || tCut < 0f || tCut > 1f) continue;
                    // Snap to vertex if very close (avoids sliver edges).
                    if (tEdge < kVertexSnapT) tEdge = 0f;
                    else if (tEdge > 1f - kVertexSnapT) tEdge = 1f;
                    float y = math.lerp(eA.y, eB.y, tEdge);
                    float3 p = new float3(math.lerp(eA.x, eB.x, tEdge), y, math.lerp(eA.z, eB.z, tEdge));
                    hits.Add(new Hit { edgeIndex = e, tEdge = tEdge, cutIndex = c, tCut = tCut, point = p });
                }
            }

            if (hits.Count < 2)
            {
                result.Add(polygon);
                return result;
            }

            // 2. Sort hits by (cutIndex, tCut) — order along the cut polyline.
            hits.Sort((a, b) => a.cutIndex != b.cutIndex
                ? a.cutIndex.CompareTo(b.cutIndex)
                : a.tCut.CompareTo(b.tCut));

            // 3. Dedupe coincident hits (cut passes exactly through a polygon vertex → both
            //    edges sharing the vertex hit at the same point).
            for (int i = hits.Count - 1; i > 0; i--)
            {
                float dx = hits[i].point.x - hits[i - 1].point.x;
                float dz = hits[i].point.z - hits[i - 1].point.z;
                if (dx * dx + dz * dz < kDuplicateHitDistSq) hits.RemoveAt(i);
            }

            if ((hits.Count & 1) != 0)
            {
                // Odd count = cut entered but didn't fully cross (ends inside polygon, or grazes
                // a vertex tangentially). Don't split.
                result.Add(polygon);
                return result;
            }

            // 4. Find a hit pair that produces a REAL split: both rings must be smaller than
            //    the input polygon by more than the sliver threshold. A cut that merely grazes
            //    the boundary — the typical case being a cut line that IS one of the polygon's
            //    own edges (area closed over the endpoints of the very lines that cut it) —
            //    yields one ring ≈ the whole polygon plus a sliver. Recursing on that ring
            //    would re-find the same hits forever: unbounded recursion, stack overflow,
            //    and mono dies with a native crash. Skip such pairs; if no pair is productive,
            //    return the polygon unsplit.
            float polyArea = math.abs(SignedAreaXZ(polygon));
            for (int h = 0; h + 1 < hits.Count; h += 2)
            {
                var left = BuildRing(polygon, cut, hits[h], hits[h + 1], walkForward: true);
                var right = BuildRing(polygon, cut, hits[h], hits[h + 1], walkForward: false);
                float leftArea = left.Count >= 3 ? math.abs(SignedAreaXZ(left)) : 0f;
                float rightArea = right.Count >= 3 ? math.abs(SignedAreaXZ(right)) : 0f;
                bool realSplit = leftArea >= kMinRingArea && rightArea >= kMinRingArea
                              && leftArea <= polyArea - kMinRingArea
                              && rightArea <= polyArea - kMinRingArea;
                if (!realSplit) continue;

                // 5. Recurse on both halves with the same cut — later chords of a multi-chord
                //    cut are re-detected from scratch inside whichever half they landed in.
                //    Bounded: every level strictly shrinks ring area (real-split guard above),
                //    with kMaxSplitDepth as the backstop.
                foreach (var r in SplitByPolyline(left, cut, depth + 1)) if (IsValidRing(r)) result.Add(r);
                foreach (var r in SplitByPolyline(right, cut, depth + 1)) if (IsValidRing(r)) result.Add(r);
                return result;
            }

            // No productive pair — every candidate chord grazed the boundary. Not a split.
            result.Add(polygon);
            return result;
        }

        /// <summary>
        /// Split <paramref name="polygon"/> by every polyline in <paramref name="cuts"/> in
        /// turn. Returns the final set of sub-polygons.
        /// </summary>
        public static List<List<float3>> SplitByPolylines(List<float3> polygon, List<List<float3>> cuts)
        {
            var current = new List<List<float3>> { polygon };
            if (cuts == null) return current;
            for (int c = 0; c < cuts.Count; c++)
            {
                var next = new List<List<float3>>();
                for (int p = 0; p < current.Count; p++)
                {
                    var split = SplitByPolyline(current[p], cuts[c]);
                    next.AddRange(split);
                }
                current = next;
            }
            return current;
        }

        /// <summary>
        /// Build one side of the chord cut. Walks the polygon boundary from <paramref name="hEnter"/>
        /// to <paramref name="hExit"/> in one direction (forward = increasing edge index), then
        /// closes via the cut polyline from hExit back to hEnter (reversed).
        /// </summary>
        private static List<float3> BuildRing(List<float3> polygon, List<float3> cut, Hit hEnter, Hit hExit, bool walkForward)
        {
            var ring = new List<float3>(polygon.Count + 4);
            ring.Add(hEnter.point);

            // Walk polygon boundary from edge hEnter.edgeIndex to edge hExit.edgeIndex.
            // Forward: take vertices (edgeIndex+1, +2, …, exit.edgeIndex). Reverse: take
            // (edgeIndex, edgeIndex-1, …, exit.edgeIndex+1).
            int n = polygon.Count;
            if (walkForward)
            {
                int v = (hEnter.edgeIndex + 1) % n;
                // Stop condition: we've passed every vertex up to and including hExit.edgeIndex.
                // If hEnter and hExit are on the same edge, we don't add any polygon vertex.
                if (hEnter.edgeIndex != hExit.edgeIndex || hEnter.tEdge > hExit.tEdge)
                {
                    int safety = n + 1;
                    while (safety-- > 0)
                    {
                        ring.Add(polygon[v]);
                        if (v == hExit.edgeIndex) break;
                        v = (v + 1) % n;
                    }
                }
            }
            else
            {
                int v = hEnter.edgeIndex;
                if (hEnter.edgeIndex != hExit.edgeIndex || hEnter.tEdge < hExit.tEdge)
                {
                    int safety = n + 1;
                    while (safety-- > 0)
                    {
                        ring.Add(polygon[v]);
                        int prev = (v - 1 + n) % n;
                        if (prev == hExit.edgeIndex) break;
                        v = prev;
                    }
                }
            }

            ring.Add(hExit.point);

            // Walk the cut polyline back from hExit to hEnter (intermediate cut vertices).
            // hEnter.cutIndex ≤ hExit.cutIndex by sort order. The polyline vertices strictly
            // between them (not counting cut points already at hEnter / hExit) are
            // cut[hExit.cutIndex] down to cut[hEnter.cutIndex+1].
            for (int c = hExit.cutIndex; c > hEnter.cutIndex; c--)
            {
                ring.Add(cut[c]);
            }

            // Dedupe consecutive identical points (safety against zero-length edges).
            for (int i = ring.Count - 1; i > 0; i--)
            {
                float dx = ring[i].x - ring[i - 1].x;
                float dz = ring[i].z - ring[i - 1].z;
                if (dx * dx + dz * dz < kDuplicateHitDistSq) ring.RemoveAt(i);
            }
            // Also dedupe last vs first (closing edge).
            if (ring.Count >= 2)
            {
                float dx = ring[ring.Count - 1].x - ring[0].x;
                float dz = ring[ring.Count - 1].z - ring[0].z;
                if (dx * dx + dz * dz < kDuplicateHitDistSq) ring.RemoveAt(ring.Count - 1);
            }
            return ring;
        }

        /// <summary>True if the ring has ≥3 vertices and signed XZ area above threshold.</summary>
        private static bool IsValidRing(List<float3> ring)
        {
            if (ring == null || ring.Count < 3) return false;
            return math.abs(SignedAreaXZ(ring)) >= kMinRingArea;
        }

        /// <summary>Shoelace area of the ring in the XZ plane. Positive = CCW.</summary>
        public static float SignedAreaXZ(List<float3> ring)
        {
            float sum = 0f;
            int n = ring.Count;
            for (int i = 0; i < n; i++)
            {
                var a = ring[i];
                var b = ring[(i + 1) % n];
                sum += (b.x - a.x) * (b.z + a.z);
            }
            return -sum * 0.5f;
        }

        /// <summary>Geometric centroid of the ring in XZ (Y averaged). For sandwich
        /// visibility lookup: given an old piece's vertices, the new piece "is the same" if its
        /// centroid lies inside the old one.</summary>
        public static float3 CentroidXZ(List<float3> ring)
        {
            float3 sum = float3.zero;
            for (int i = 0; i < ring.Count; i++) sum += ring[i];
            return sum / ring.Count;
        }

        /// <summary>2D line-segment vs line-segment intersection in the XZ plane. Returns true
        /// and (tA, tB) ∈ [0,1] when segments cross; false for parallel / collinear / no-cross.
        /// Standard parametric form: P = A + tA*(B-A), Q = C + tB*(D-C), solve cross-product
        /// equation for the two t parameters. Caller validates t-range against [0,1].</summary>
        private static bool Segment2DIntersect(float2 a, float2 b, float2 c, float2 d, out float tA, out float tB)
        {
            tA = 0f; tB = 0f;
            float2 r = b - a;
            float2 s = d - c;
            float denom = r.x * s.y - r.y * s.x;
            if (math.abs(denom) < 1e-9f) return false;  // parallel or collinear
            float2 ca = c - a;
            tA = (ca.x * s.y - ca.y * s.x) / denom;
            tB = (ca.x * r.y - ca.y * r.x) / denom;
            return true;
        }

        /// <summary>Point-in-polygon test in the XZ plane (ray casting). Used by the topology
        /// system to inherit per-piece visibility across recomputes (new piece adopts visibility
        /// of whichever old piece contained its centroid).</summary>
        public static bool ContainsXZ(List<float3> ring, float3 p)
        {
            bool inside = false;
            int n = ring.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                float xi = ring[i].x, zi = ring[i].z;
                float xj = ring[j].x, zj = ring[j].z;
                bool intersect = ((zi > p.z) != (zj > p.z)) &&
                                 (p.x < (xj - xi) * (p.z - zi) / (zj - zi + 1e-9f) + xi);
                if (intersect) inside = !inside;
            }
            return inside;
        }
    }
}
